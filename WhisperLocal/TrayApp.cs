using System.Diagnostics;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Input;
using WhisperLocal.Llm;
using WhisperLocal.Native;
using WhisperLocal.Output;
using WhisperLocal.Pipeline;
using WhisperLocal.Stt;
using WhisperLocal.Text;
using WhisperLocal.Ui;

namespace WhisperLocal;

/// <summary>
/// Tray application: hotkey → record → pipeline → paste, plus the host of the shared (Avalonia) windows.
/// Everything here runs on the UI thread.
/// </summary>
internal sealed class TrayApp : ApplicationContext, IAppHost
{
    private enum State { Idle, Recording, Processing }

    private const string ReleasesUrl = "https://github.com/volodya0/WhisperLocal/releases";

    private readonly ConfigManager _config;
    private readonly SynchronizationContext _ui;
    private readonly DictationHistory _history;
    private readonly DictationStats _stats;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _unloadItem;
    private readonly ToolStripMenuItem _transcriptItem;
    private readonly KeyboardHook _hook;
    private readonly AudioRecorder _recorder = new();
    private readonly WhisperServerStt _stt;
    private readonly OllamaCleaner _llm;
    private readonly DictationPipeline _pipeline;
    private readonly TextInserter _inserter = new();
    private readonly RecordingOverlay _overlay = new();
    private readonly TranscriptWindow _transcriptWindow = new();
    private readonly MainWindow _mainWindow;
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };

    private State _state = State.Idle;
    private bool _enabled = true;
    private long _pressedAt;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private ForegroundApp? _target;
    private string _lastTranscript = "";
    private Download? _download;
    private bool _disposed;

    /// <summary>A model download in progress: what the setup line shows, and how to cancel it.</summary>
    private sealed class Download
    {
        public CancellationTokenSource Cancel { get; } = new();
        public string Text { get; set; } = "";
    }

    public TrayApp(ConfigManager config)
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;

        _config = config;
        _history = new DictationHistory(Path.Combine(AppPaths.DataDir, "history.jsonl"));
        // Seeded from the history once, so dictations from before the statistics existed count too
        _stats = new DictationStats(Path.Combine(AppPaths.DataDir, "stats.json"), _history.Recent(int.MaxValue));
        _stt = new WhisperServerStt(config);
        _llm = new OllamaCleaner(config);
        _pipeline = new DictationPipeline(config, _stt, _llm);
        _mainWindow = new MainWindow(this, _transcriptWindow);

        var openItem = new ToolStripMenuItem("Open WhisperLocal…", null, (_, _) => ShowMainWindow());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold); // the double-click action
        _transcriptItem = new ToolStripMenuItem("Show Last Transcript", null, (_, _) => ShowLastTranscript());
        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled()) { Checked = true };
        _unloadItem = new ToolStripMenuItem("Unload from memory", null, async (_, _) => await UnloadFromMemoryAsync());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            openItem,
            _transcriptItem,
            new ToolStripSeparator(),
            _enabledItem,
            _unloadItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open config", null, (_, _) => OpenPath(_config.FilePath, textEditor: true)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenPath(Log.CurrentFile, textEditor: true)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        ]);

        menu.Opening += (_, _) =>
        {
            // "Unload from memory" is available only while something is loaded
            _unloadItem.Enabled = IsAnythingLoaded;
            _transcriptItem.Enabled = _lastTranscript.Length > 0;
        };

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowMainWindow();
        };

        _hook = new KeyboardHook(_ui);
        _hook.HotkeyDown += OnHotkeyDown;
        _hook.HotkeyUp += OnHotkeyUp;
        _hook.HotkeyCancel += OnHotkeyCancel;
        _recorder.MaxDurationReached += () => _ui.Post(_ => _ = FinishRecordingAsync(), null);
        _config.Changed += () => _ui.Post(_ => ApplyConfig(), null);

        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        ApplyConfig();
        UpdateTrayState();
        Log.Info($"WhisperLocal {Application.ProductVersion} started, config: {_config.FilePath}");

        if (_mainWindow.HasProblems)
            ShowMainWindow();
        else if (config.Current.Whisper.StartOnLaunch)
            _ = WarmUpWhisperAsync();
    }

    // ---- IAppHost: what the shared windows ask for ----

    public ConfigManager Config => _config;
    public DictationHistory History => _history;
    public DictationStats Stats => _stats;
    public string VersionText => Application.ProductVersion.Split('+')[0];
    public bool HasGrammarCheck => false;

    public IReadOnlyList<HotkeyPreset> HotkeyPresets { get; } =
    [
        new("Left Ctrl + Left Win", "LControlKey+LWin"),
        new("Right Alt", "RMenu"),
        new("Left Alt + Left Win", "LMenu+LWin"),
        new("Left Ctrl + Left Alt", "LControlKey+LMenu"),
        new("Right Ctrl", "RControlKey"),
    ];

    public string DescribeHotkey(string key) => KeyboardHook.Describe(key);

    public IReadOnlyList<SetupItem> GetSetupItems()
    {
        var items = new List<SetupItem>();

        var microphones = AudioRecorder.ListDevices();
        items.Add(microphones.Count > 0
            ? new SetupItem(SetupState.Ok, "Microphone: " + microphones[0])
            : new SetupItem(SetupState.Error, "No microphone found", "Sound Settings", () => OpenPath("ms-settings:sound")));

        var server = WhisperServerStt.ResolveServerExe(_config.Current.Whisper.ServerExe);
        items.Add(File.Exists(server)
            ? new SetupItem(SetupState.Ok, "Speech server: " + Path.GetFileName(server))
            : new SetupItem(SetupState.Error, "Speech server not found: " + server));

        var model = AppPaths.Resolve(_config.Current.Whisper.ModelPath);
        var name = Path.GetFileName(model);
        if (_download is { } download)
            items.Add(new SetupItem(SetupState.Pending, download.Text, "Cancel", () => download.Cancel.Cancel()));
        else if (File.Exists(model))
            items.Add(new SetupItem(SetupState.Ok, $"Model: {name} ({FormatSize(new FileInfo(model).Length)})",
                "Show Folder", () => OpenPath(Path.GetDirectoryName(model)!)));
        else
            items.Add(new SetupItem(SetupState.Error, $"Model not downloaded: {name}", "Download", () => _ = DownloadModelAsync(model)));

        items.Add(new SetupItem(SetupState.Ok, $"Hotkey: hold {KeyboardHook.Describe(_config.Current.Hotkey.Key)} to dictate"));
        return items;
    }

    public IReadOnlyList<SetupItem> GetLanguageModelItems() => [];

    public string? StartHotkeyRecording(Action<string> preview, Action<string?> done)
    {
        if (_state != State.Idle)
            return "Finish the current dictation first.";
        _hook.BeginRecording(
            keys => preview(KeyboardHook.Describe(KeyboardHook.ToConfigString(keys))),
            keys => done(keys == null ? null : KeyboardHook.ToConfigString(keys)));
        return null;
    }

    public void CancelHotkeyRecording() => _hook.CancelRecording();

    public (bool Loaded, string Detail) GetModelMemory()
    {
        var model = AppPaths.Resolve(_config.Current.Whisper.ModelPath);
        var detail = Path.GetFileName(model);
        if (File.Exists(model))
            detail += $" ({FormatSize(new FileInfo(model).Length)})";
        if (!_stt.IsLoaded)
            return (false, detail + (_config.Current.Memory.IdleUnloadMinutes > 0
                ? $"; loads on the next dictation, unloads after {_config.Current.Memory.IdleUnloadMinutes} min idle"
                : "; loads on the next dictation"));
        if (_stt.MemoryBytes is { } bytes)
            detail += $", whisper-server uses {FormatSize(bytes)}";
        return (true, detail);
    }

    public Task UnloadAsync() => UnloadFromMemoryAsync();

    public async Task LoadAsync()
    {
        _lastActivityUtc = DateTime.UtcNow;
        await WarmUpWhisperAsync();
    }

    /// <summary>No updater on Windows yet: the releases page opens in the browser.</summary>
    public void CheckForUpdates() => OpenPath(ReleasesUrl);

    public void OpenPath(string path, bool textEditor = false)
    {
        try
        {
            if (textEditor)
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Cannot open {path}", ex);
        }
    }

    public void MainWindowVisibilityChanged(bool visible)
    {
    }

    // ---- Dictation ----

    private void ShowMainWindow()
    {
        _mainWindow.Refresh();
        _mainWindow.ShowAndActivate();
    }

    private void ShowLastTranscript()
    {
        if (_lastTranscript.Length > 0)
            _transcriptWindow.ShowText(_lastTranscript);
    }

    private void ApplyConfig()
    {
        var cfg = _config.Current;
        Log.DebugEnabled = cfg.Logging.Debug;
        try
        {
            _hook.SetHotkey(KeyboardHook.ParseHotkey(cfg.Hotkey.Key));
            Log.Info($"Hotkey: {KeyboardHook.Describe(cfg.Hotkey.Key)}");
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex.Message);
            Notify(ex.Message, ToolTipIcon.Error);
        }
        _hook.SuppressKey = cfg.Hotkey.SuppressKey;

        try
        {
            Autostart.Apply(cfg.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the Windows autostart entry", ex);
        }

        UpdateTrayState();
    }

    private async void OnHotkeyCancel()
    {
        if (_state != State.Recording)
            return;
        Log.Info("Recording cancelled: another key was pressed while holding the hotkey");
        await _recorder.StopAsync();
        SetState(State.Idle);
    }

    private void OnHotkeyDown()
    {
        if (!_enabled || _state != State.Idle)
            return;

        var cfg = _config.Current;

        // Do not let the user dictate for nothing: the main window has the Download button
        var modelPath = AppPaths.Resolve(cfg.Whisper.ModelPath);
        if (!File.Exists(modelPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            ShowModelMissing(new ModelNotFoundException(modelPath));
            return;
        }

        _target = ForegroundApp.Capture();
        try
        {
            _recorder.Start(cfg.Audio.DeviceName, cfg.Audio.MaxDurationSec);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start recording", ex);
            Notify($"Microphone error: {ex.Message}", ToolTipIcon.Error);
            return;
        }

        _pressedAt = Stopwatch.GetTimestamp();
        _lastActivityUtc = DateTime.UtcNow;
        SetState(State.Recording);
        _ = _pipeline.PrepareAsync();
    }

    private void OnHotkeyUp() => _ = FinishRecordingAsync();

    private async Task FinishRecordingAsync()
    {
        if (_state != State.Recording)
            return;

        var cfg = _config.Current;
        var recordSec = Stopwatch.GetElapsedTime(_pressedAt).TotalSeconds;
        var processStart = Stopwatch.GetTimestamp();
        SetState(State.Processing);

        DictationResult? result = null;
        string? error = null;
        var tap = false;
        try
        {
            var pcm = await _recorder.StopAsync();
            if (recordSec * 1000 < cfg.Hotkey.MinHoldMs)
            {
                Log.Debug($"Hotkey tap ignored ({recordSec * 1000:F0} ms)");
                tap = true;
                return;
            }

            result = await _pipeline.ProcessAsync(pcm, _target?.ProcessName, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(result.FinalText))
                return;

            var text = cfg.Insert.AppendTrailingSpace ? result.FinalText + " " : result.FinalText;
            await _inserter.InsertAsync(text, _target, cfg.Insert);
        }
        catch (Exception ex)
        {
            error = ex is ModelNotFoundException ? "model not found" : ex.Message;
            ReportError("Dictation failed", ex);
        }
        finally
        {
            _lastActivityUtc = DateTime.UtcNow;
            SetState(State.Idle);
            if (!tap)
                RecordOutcome(recordSec, Stopwatch.GetElapsedTime(processStart).TotalSeconds, result, error);
        }
    }

    /// <summary>A record in the history and the statistics; the last text for "Show Last Transcript".</summary>
    private void RecordOutcome(double recordSec, double processSec, DictationResult? result, string? error)
    {
        var cfg = _config.Current;
        var text = result?.FinalText.Trim() ?? "";
        var words = text.Length == 0 ? 0 : TextFilters.CountWords(text);
        if (words > 0)
            _lastTranscript = text;

        var record = new DictationRecord(
            DateTime.UtcNow,
            Math.Round(recordSec, 2),
            Math.Round(processSec, 2),
            Math.Round((result?.SttMs ?? 0) / 1000.0, 2),
            words,
            _target?.ProcessName ?? "",
            // Keep the text only where the log keeps it too
            cfg.Logging.LogText ? text : "",
            error ?? result?.Note ?? "");
        // Statistics first: the history's Changed event refreshes the window, which shows both
        _stats.Add(record);
        _history.Add(record);
    }

    private void ToggleEnabled()
    {
        _enabled = !_enabled;
        _enabledItem.Checked = _enabled;
        Log.Info(_enabled ? "Dictation enabled" : "Dictation disabled");
        UpdateTrayState();
    }

    /// <summary>
    /// Stops all servers and unloads all models to free RAM/VRAM.
    /// Dictation stays enabled: the next hotkey press starts everything again.
    /// </summary>
    private async Task UnloadFromMemoryAsync()
    {
        // A running dictation finishes first; the idle timer or the user can unload afterwards
        if (_state == State.Processing)
            return;
        if (_state == State.Recording)
        {
            await _recorder.StopAsync();
            SetState(State.Idle);
        }

        _stt.Unload();
        if (_config.Current.Llm.Enabled)
            await _llm.UnloadAsync();
        Log.Info("Unloaded from memory: all servers stopped, models released");
    }

    private bool IsAnythingLoaded => _stt.IsLoaded;

    private async void CheckIdle()
    {
        var minutes = _config.Current.Memory.IdleUnloadMinutes;
        if (_disposed || minutes <= 0 || _state != State.Idle || !IsAnythingLoaded)
            return;
        if (DateTime.UtcNow - _lastActivityUtc < TimeSpan.FromMinutes(minutes))
            return;

        Log.Info($"No dictation for {minutes} min, unloading from memory");
        await UnloadFromMemoryAsync();
    }

    private async Task WarmUpWhisperAsync()
    {
        try
        {
            await _stt.WarmUpAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportError("whisper-server start failed", ex);
        }
    }

    private async Task DownloadModelAsync(string model)
    {
        if (_download != null)
            return;
        var download = _download = new Download();
        var name = Path.GetFileName(model);
        var url = ModelDownloader.UrlFor(model);
        download.Text = $"Downloading {name}…";

        var progress = new Progress<(long Done, long? Total)>(p =>
            download.Text = p.Total is { } total and > 0
                ? $"Downloading {name}: {FormatSize(p.Done)} of {FormatSize(total)} ({p.Done * 100 / total}%)"
                : $"Downloading {name}: {FormatSize(p.Done)}");
        try
        {
            await ModelDownloader.DownloadAsync(model, url, progress, () => _ui.Post(_ => download.Text = $"Verifying {name}…", null),
                download.Cancel.Token);
            Notify("Model downloaded, ready to dictate", ToolTipIcon.Info);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"Download of {name} cancelled; it resumes on the next Download");
        }
        catch (Exception ex)
        {
            Log.Error($"Download of {name} failed", ex);
            MessageBox.Show($"{ex.Message}\n\nYou can also download it in a browser:\n{url}\n\nand put it into:\n{Path.GetDirectoryName(model)}",
                "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _download = null;
            _mainWindow.Refresh();
        }
    }

    /// <summary>A missing model opens the main window (with the Download button); other errors a tray balloon.</summary>
    private void ReportError(string context, Exception ex)
    {
        if (ex is ModelNotFoundException missing)
        {
            ShowModelMissing(missing);
            return;
        }
        Log.Error(context, ex);
        Notify($"{context}: {ex.Message}", ToolTipIcon.Error);
    }

    private void ShowModelMissing(ModelNotFoundException ex)
    {
        Log.Error(ex.Message.ReplaceLineEndings(" "));
        ShowMainWindow();
    }

    private void SetState(State state)
    {
        _state = state;
        if (_config.Current.Ui.ShowOverlay)
        {
            switch (state)
            {
                case State.Recording:
                    _overlay.ShowState("● Recording", Color.FromArgb(255, 90, 90));
                    break;
                case State.Processing:
                    _overlay.ShowState("… Processing", Color.FromArgb(255, 180, 60));
                    break;
                default:
                    _overlay.Hide();
                    break;
            }
        }
        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        if (!_enabled)
        {
            _tray.Icon = TrayIcons.Off;
            _tray.Text = "WhisperLocal: disabled";
            return;
        }
        (_tray.Icon, _tray.Text) = _state switch
        {
            State.Recording => (TrayIcons.Recording, "WhisperLocal: recording"),
            State.Processing => (TrayIcons.Processing, "WhisperLocal: processing"),
            _ => (TrayIcons.Idle, $"WhisperLocal: hold {KeyboardHook.Describe(_config.Current.Hotkey.Key)} to dictate"),
        };
    }

    private void Notify(string message, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(4000, "WhisperLocal", message, icon);

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";

    private void ExitApp()
    {
        Log.Info("WhisperLocal exiting");
        _tray.Visible = false;
        Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _download?.Cancel.Cancel();
            _idleTimer.Stop();
            _hook.Dispose();
            _recorder.Dispose();
            _stt.Dispose();
            _llm.Dispose();
            try
            {
                _mainWindow.CloseForGood();
                _transcriptWindow.CloseForGood();
            }
            catch (Exception ex)
            {
                Log.Debug($"Closing windows: {ex.Message}");
            }
            _tray.Dispose();
            _overlay.Dispose();
            _idleTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
