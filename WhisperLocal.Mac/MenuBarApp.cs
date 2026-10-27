using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Mac.Audio;
using WhisperLocal.Mac.Input;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Mac.Output;
using WhisperLocal.Mac.Ui;
using WhisperLocal.Mac.Updates;
using WhisperLocal.Pipeline;
using WhisperLocal.Stt;
using WhisperLocal.Text;
using WhisperLocal.Ui;

namespace WhisperLocal.Mac;

/// <summary>
/// Menu bar application: hotkey → record → pipeline → paste, plus the host of the shared (Avalonia) windows.
/// Everything here runs on the main thread.
/// </summary>
internal sealed class MenuBarApp : IAppHost, IDisposable
{
    private enum State { Idle, Recording, Processing }

    private const string AccessibilitySettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";
    private const string MicrophoneSettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";
    private const long ActivationRegular = 0, ActivationAccessory = 1;

    private static readonly TimeSpan ResultDisplay = TimeSpan.FromSeconds(2);

    private readonly ConfigManager _config;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly SynchronizationContext _ui;
    private readonly DictationHistory _history;
    private readonly DictationStats _stats;
    private readonly StatusMenu _menu;
    private readonly IntPtr _enabledItem;
    private readonly IntPtr _unloadItem;
    private readonly IntPtr _transcriptItem;
    private readonly HotkeyTap _hotkey;
    private readonly MacAudioRecorder _recorder = new();
    private readonly WhisperServerStt _stt;
    private readonly OllamaCleaner _llm;
    private readonly DictationPipeline _pipeline;
    private readonly MacTextInserter _inserter = new();
    private readonly RecordingOverlay _overlay;
    private readonly TranscriptWindow _transcriptWindow = new();
    private readonly MainWindow _mainWindow;
    private readonly System.Threading.Timer _idleTimer;

    private State _state = State.Idle;
    private bool _enabled = true;
    private long _pressedAt;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private FrontmostApp? _target;
    private string _lastTranscript = "";
    private bool _checkingForUpdates;
    private CancellationTokenSource? _download;
    private string? _downloadText;
    private bool _disposed;

    public MenuBarApp(ConfigManager config, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _config = config;
        _desktop = desktop;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("No UI synchronization context");
        _history = new DictationHistory(Path.Combine(AppPaths.DataDir, "history.jsonl"));
        // Seeded from the history once, so dictations from before the statistics existed count too
        _stats = new DictationStats(Path.Combine(AppPaths.DataDir, "stats.json"), _history.Recent(int.MaxValue));
        _stt = new WhisperServerStt(config);
        _llm = new OllamaCleaner(config);
        _pipeline = new DictationPipeline(config, _stt, _llm);
        _overlay = new RecordingOverlay(_ui, ShowLastTranscript);
        _mainWindow = new MainWindow(this, _transcriptWindow);

        _menu = new StatusMenu();
        _menu.AddItem("Open WhisperLocal…", ShowMainWindow);
        _transcriptItem = _menu.AddItem("Show Last Transcript", ShowLastTranscript);
        _menu.AddItem("Check for Updates…", () => _ = CheckForUpdatesAsync());
        _menu.AddSeparator();
        _enabledItem = _menu.AddItem("Enabled", ToggleEnabled);
        _unloadItem = _menu.AddItem("Unload from memory", async () => await UnloadFromMemoryAsync());
        _menu.AddSeparator();
        _menu.AddItem("Quit WhisperLocal", ExitApp);
        StatusMenu.SetChecked(_enabledItem, true);

        _menu.Opening += () =>
        {
            // "Unload from memory" is available only while something is loaded
            StatusMenu.SetEnabled(_unloadItem, IsAnythingLoaded);
            StatusMenu.SetEnabled(_transcriptItem, _lastTranscript.Length > 0);
        };

        RequestAccessibility();
        _hotkey = new HotkeyTap(_ui);
        _hotkey.HotkeyDown += OnHotkeyDown;
        _hotkey.HotkeyUp += OnHotkeyUp;
        _hotkey.HotkeyCancel += OnHotkeyCancel;
        _hotkey.PermissionMissing += () => Dialogs.Notify(
            "Allow WhisperLocal in System Settings → Privacy & Security → Accessibility. The hotkey starts working as soon as it is allowed.");
        _hotkey.Installed += () => Dialogs.Notify($"Ready: hold {HotkeyTap.Describe(_config.Current.Hotkey.Key)} to dictate");
        _recorder.MaxDurationReached += () => _ui.Post(_ => _ = FinishRecordingAsync(), null);
        _config.Changed += () => _ui.Post(_ => ApplyConfig(), null);
        ApplyConfig();
        _hotkey.Start();

        // Opening the app again from Finder/Launchpad/Spotlight brings up the window (there is no Dock icon to click)
        if (Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                    ShowMainWindow();
            };
        }

        _idleTimer = new System.Threading.Timer(_ => _ui.Post(_ => CheckIdle(), null), null, 30_000, 30_000);

        UpdateMenuState();
        Log.Info($"WhisperLocal {Updater.CurrentVersionText} (macOS) started, config: {_config.FilePath}");

        if (_mainWindow.HasProblems)
            ShowMainWindow();
        else if (config.Current.Whisper.StartOnLaunch)
            _ = WarmUpWhisperAsync();
    }

    // ---- IAppHost: what the shared windows ask for ----

    public ConfigManager Config => _config;
    public DictationHistory History => _history;
    public DictationStats Stats => _stats;
    public string VersionText => Updater.CurrentVersionText;

    public IReadOnlyList<HotkeyPreset> HotkeyPresets { get; } =
    [
        new("Left Control + Left Command", "LeftControl+LeftCommand"),
        new("Right Option", "RightOption"),
        new("Right Command", "RightCommand"),
        new("Fn (Globe)", "Fn"),
        new("Left Option + Left Command", "LeftOption+LeftCommand"),
        new("Left Control + Left Option", "LeftControl+LeftOption"),
    ];

    public string DescribeHotkey(string key) => HotkeyTap.Describe(key);

    public IReadOnlyList<SetupItem> GetSetupItems()
    {
        var items = new List<SetupItem>();

        items.Add(MacNative.AXIsProcessTrusted()
            ? new SetupItem(SetupState.Ok, "Accessibility: allowed")
            : new SetupItem(SetupState.Error, "Accessibility: not allowed (needed for the hotkey and pasting)",
                "Open Settings", () => Dialogs.Open(AccessibilitySettings)));

        items.Add(MacAudioRecorder.GetMicrophoneAccess() switch
        {
            MacAudioRecorder.MicrophoneAccess.Allowed => new SetupItem(SetupState.Ok, "Microphone: allowed"),
            MacAudioRecorder.MicrophoneAccess.Denied => new SetupItem(SetupState.Error, "Microphone: denied",
                "Open Settings", () => Dialogs.Open(MicrophoneSettings)),
            MacAudioRecorder.MicrophoneAccess.NotAsked => new SetupItem(SetupState.Warning, "Microphone: not asked yet",
                "Allow…", RequestMicrophone),
            _ => new SetupItem(SetupState.Warning, "Microphone: unknown (asked on the first dictation)"),
        });

        var server = WhisperServerStt.ResolveServerExe(_config.Current.Whisper.ServerExe);
        items.Add(File.Exists(server)
            ? new SetupItem(SetupState.Ok, "Speech server: " + Path.GetFileName(server))
            : new SetupItem(SetupState.Error, "Speech server not found: " + server));

        var model = AppPaths.Resolve(_config.Current.Whisper.ModelPath);
        var name = Path.GetFileName(model);
        if (_download != null)
            items.Add(new SetupItem(SetupState.Pending, _downloadText ?? $"Downloading {name}…", "Cancel", () => _download?.Cancel()));
        else if (File.Exists(model))
            items.Add(new SetupItem(SetupState.Ok, $"Model: {name} ({FormatSize(new FileInfo(model).Length)})",
                "Show Folder", () => Dialogs.Open(Path.GetDirectoryName(model)!)));
        else
            items.Add(new SetupItem(SetupState.Error, $"Model not downloaded: {name}", "Download", () => _ = DownloadModelAsync(model)));

        var hotkey = HotkeyTap.Describe(_config.Current.Hotkey.Key);
        items.Add(_hotkey.IsInstalled
            ? new SetupItem(SetupState.Ok, $"Hotkey: hold {hotkey} to dictate")
            : new SetupItem(SetupState.Pending, $"Hotkey {hotkey}: waiting for Accessibility"));

        return items;
    }

    public string? StartHotkeyRecording(Action<string> preview, Action<string?> done)
    {
        if (!_hotkey.IsInstalled)
            return "Allow Accessibility first: recording a hotkey uses the same keyboard access as dictation.";
        if (_state != State.Idle)
            return "Finish the current dictation first.";
        _hotkey.BeginRecording(
            keys => preview(HotkeyTap.Describe(HotkeyTap.ToConfigString(keys))),
            keys => done(keys == null ? null : HotkeyTap.ToConfigString(keys)));
        return null;
    }

    public void CancelHotkeyRecording() => _hotkey.CancelRecording();

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

    public void CheckForUpdates() => _ = CheckForUpdatesAsync();

    public void OpenPath(string path, bool textEditor = false) => Dialogs.Open(path, textEditor);

    public void MainWindowVisibilityChanged(bool visible)
    {
        // A regular app (Dock icon, Cmd+Tab) while the window is open, so it cannot get lost; menu bar only otherwise
        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.Send(app, ObjC.Sel("setActivationPolicy:"), visible ? ActivationRegular : ActivationAccessory);
        if (visible)
            ObjC.Send(app, ObjC.Sel("activateIgnoringOtherApps:"), true);
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

    /// <summary>Shows the system Accessibility prompt if the permission is missing (needed for the hotkey and for Cmd+V).</summary>
    private static void RequestAccessibility()
    {
        if (MacNative.AXIsProcessTrusted())
            return;
        ObjC.WithPool(() =>
        {
            // @{ @"AXTrustedCheckOptionPrompt": @YES }
            var yes = ObjC.Send(ObjC.Class("NSNumber"), ObjC.Sel("numberWithBool:"), true);
            var options = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Sel("dictionaryWithObject:forKey:"), yes, ObjC.Str("AXTrustedCheckOptionPrompt"));
            MacNative.AXIsProcessTrustedWithOptions(options);
        });
    }

    private void ApplyConfig()
    {
        var cfg = _config.Current;
        Log.DebugEnabled = cfg.Logging.Debug;
        try
        {
            _hotkey.SetHotkey(HotkeyTap.ParseHotkey(cfg.Hotkey.Key));
            Log.Info($"Hotkey: {HotkeyTap.Describe(cfg.Hotkey.Key)}");
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex.Message);
            Dialogs.Notify(ex.Message);
        }
        _hotkey.SuppressKey = cfg.Hotkey.SuppressKey;

        try
        {
            LaunchAgent.Apply(cfg.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the login item (LaunchAgent)", ex);
        }

        UpdateMenuState();
    }

    private async void OnHotkeyCancel()
    {
        if (_state != State.Recording)
            return;
        Log.Info("Recording cancelled: another key was pressed while holding the hotkey");
        await _recorder.StopAsync();
        _overlay.Hide();
        SetState(State.Idle);
    }

    private void OnHotkeyDown()
    {
        if (!_enabled || _state != State.Idle)
            return;

        var cfg = _config.Current;

        // Do not let the user dictate for nothing
        var modelPath = AppPaths.Resolve(cfg.Whisper.ModelPath);
        if (!File.Exists(modelPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            ShowModelMissing(new ModelNotFoundException(modelPath));
            return;
        }

        _target = FrontmostApp.Capture();
        try
        {
            _recorder.Start(cfg.Audio.DeviceName, cfg.Audio.MaxDurationSec);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start recording", ex);
            Dialogs.Notify($"Microphone error: {ex.Message}");
            return;
        }

        _pressedAt = Stopwatch.GetTimestamp();
        _lastActivityUtc = DateTime.UtcNow;
        SetState(State.Recording);
        if (cfg.Ui.ShowOverlay)
            _overlay.ShowRecording(_pressedAt);
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
        if (cfg.Ui.ShowOverlay)
            _overlay.ShowProcessing(processStart);

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
            if (tap)
                _overlay.Hide();
            else
                ShowOutcome(recordSec, Stopwatch.GetElapsedTime(processStart).TotalSeconds, result, error);
        }
    }

    /// <summary>The short summary on the indicator, and a record in the history.</summary>
    private void ShowOutcome(double recordSec, double processSec, DictationResult? result, string? error)
    {
        var cfg = _config.Current;
        var text = result?.FinalText.Trim() ?? "";
        var words = text.Length == 0 ? 0 : TextFilters.CountWords(text);
        if (words > 0)
            _lastTranscript = text;

        if (cfg.Ui.ShowOverlay)
        {
            if (error != null)
                _overlay.ShowResult($"✕ Failed: {error}", (1.0, 0.4, 0.4), hasText: false, ResultDisplay);
            else if (words > 0)
                _overlay.ShowResult($"✓ {recordSec:F1} s rec · {processSec:F1} s proc · {words} {(words == 1 ? "word" : "words")}",
                    (0.45, 0.88, 0.5), hasText: true, ResultDisplay);
            else
                _overlay.ShowResult($"No speech recognized · {recordSec:F1} s", (0.75, 0.75, 0.75), hasText: false, ResultDisplay);
        }

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
        StatusMenu.SetChecked(_enabledItem, _enabled);
        Log.Info(_enabled ? "Dictation enabled" : "Dictation disabled");
        UpdateMenuState();
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
            _overlay.Hide();
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

    /// <summary>A short recording makes macOS show its microphone permission prompt.</summary>
    private async void RequestMicrophone()
    {
        if (_state != State.Idle)
            return;
        try
        {
            _recorder.Start(_config.Current.Audio.DeviceName, 1);
            await Task.Delay(500);
            await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Microphone request failed", ex);
            Dialogs.Notify($"Microphone error: {ex.Message}");
        }
    }

    private async Task DownloadModelAsync(string model)
    {
        if (_download != null)
            return;
        _download = new CancellationTokenSource();
        var name = Path.GetFileName(model);
        _downloadText = $"Downloading {name}…";

        var progress = new Progress<(long Done, long? Total)>(p =>
            _downloadText = p.Total is { } total and > 0
                ? $"Downloading {name}: {FormatSize(p.Done)} of {FormatSize(total)} ({p.Done * 100 / total}%)"
                : $"Downloading {name}: {FormatSize(p.Done)}");
        try
        {
            await ModelDownloader.DownloadAsync(model, progress, () => _ui.Post(_ => _downloadText = $"Verifying {name}…", null), _download.Token);
            Dialogs.Notify("Model downloaded, ready to dictate");
        }
        catch (OperationCanceledException)
        {
            Log.Info("Model download cancelled; it resumes on the next Download");
        }
        catch (Exception ex)
        {
            Log.Error("Model download failed", ex);
            Dialogs.Show("Model download failed", $"{ex.Message}\n\nYou can also download it in a browser:\n{ModelDownloader.UrlFor(model)}\n\nand put it into:\n{Path.GetDirectoryName(model)}");
        }
        finally
        {
            _download.Dispose();
            _download = null;
            _downloadText = null;
            _mainWindow.Refresh();
        }
    }

    /// <summary>Manual update check (never automatic): asks before downloading and installing.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_checkingForUpdates)
            return;
        if (Updater.Repository == null)
        {
            Dialogs.Show("Updates", "This build does not know where updates are published (it was built without a GitHub repository).",
                Dialogs.Style.Informational);
            return;
        }

        _checkingForUpdates = true;
        try
        {
            var release = await Updater.GetLatestAsync(CancellationToken.None);
            if (release == null || !Updater.IsNewer(release))
            {
                Dialogs.Show("You're up to date", $"WhisperLocal {Updater.CurrentVersionText} is the latest version.", Dialogs.Style.Informational);
                return;
            }

            var version = release.Version.ToString(3);
            var notes = release.Notes.Length > 600 ? release.Notes[..600] + "…" : release.Notes;
            if (!Dialogs.Ask($"WhisperLocal {version} is available", $"You have {Updater.CurrentVersionText}.\n\n{notes}".TrimEnd(),
                    "Install and Restart", "Later"))
                return;
            if (_state != State.Idle)
            {
                Dialogs.Show("Update", "Finish the current dictation first, then check for updates again.", Dialogs.Style.Informational);
                return;
            }

            Log.Info($"Installing update {release.Tag}");
            // Download, hdiutil and ditto take a while: keep the main thread free
            await Task.Run(() => Updater.InstallAsync(release, Dialogs.Notify, CancellationToken.None));
            ExitApp();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            Dialogs.Show("Update failed", ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    /// <summary>A missing model opens the main window (with the Download button); other errors a notification.</summary>
    private void ReportError(string context, Exception ex)
    {
        if (ex is ModelNotFoundException missing)
        {
            ShowModelMissing(missing);
            return;
        }
        Log.Error(context, ex);
        Dialogs.Notify($"{context}: {ex.Message}");
    }

    private void ShowModelMissing(ModelNotFoundException ex)
    {
        Log.Error(ex.Message.ReplaceLineEndings(" "));
        ShowMainWindow();
    }

    private void SetState(State state)
    {
        _state = state;
        UpdateMenuState();
    }

    private void UpdateMenuState()
    {
        if (!_enabled)
        {
            _menu.SetState(StatusMenu.Icon.Off, "WhisperLocal: disabled");
            return;
        }
        var (icon, text) = _state switch
        {
            State.Recording => (StatusMenu.Icon.Recording, "WhisperLocal: recording"),
            State.Processing => (StatusMenu.Icon.Processing, "WhisperLocal: processing"),
            _ => (StatusMenu.Icon.Idle, $"WhisperLocal: hold {HotkeyTap.Describe(_config.Current.Hotkey.Key)} to dictate"),
        };
        _menu.SetState(icon, text);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";

    private void ExitApp()
    {
        Log.Info("WhisperLocal exiting");
        Dispose();
        _desktop.Shutdown();
    }

    /// <summary>Stops whisper-server; safe to call from any thread (used on SIGTERM / logout).</summary>
    public void StopServers() => _stt.Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _download?.Cancel();
        _idleTimer.Dispose();
        _hotkey.Dispose();
        _recorder.Dispose();
        _stt.Dispose();
        _llm.Dispose();
        _overlay.Dispose();
        try
        {
            _mainWindow.CloseForGood();
            _transcriptWindow.CloseForGood();
        }
        catch (Exception ex)
        {
            // After Avalonia has shut down (Cmd+Q) the windows are already gone
            Log.Debug($"Closing windows: {ex.Message}");
        }
        _menu.Dispose();
    }
}
