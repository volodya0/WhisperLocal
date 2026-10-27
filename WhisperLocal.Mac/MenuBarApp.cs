using System.Diagnostics;
using System.Reflection;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Mac.Audio;
using WhisperLocal.Mac.Input;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Mac.Output;
using WhisperLocal.Mac.Ui;
using WhisperLocal.Pipeline;
using WhisperLocal.Stt;

namespace WhisperLocal.Mac;

/// <summary>Menu bar application: hotkey → record → pipeline → paste. Everything here runs on the main thread.</summary>
internal sealed class MenuBarApp : IDisposable
{
    private enum State { Idle, Recording, Processing }

    private readonly ConfigManager _config;
    private readonly SynchronizationContext _ui;
    private readonly StatusMenu _menu;
    private readonly IntPtr _enabledItem;
    private readonly IntPtr _unloadItem;
    private readonly HotkeyTap _hotkey;
    private readonly MacAudioRecorder _recorder = new();
    private readonly WhisperServerStt _stt;
    private readonly OllamaCleaner _llm;
    private readonly DictationPipeline _pipeline;
    private readonly MacTextInserter _inserter = new();
    private readonly RecordingOverlay _overlay = new();
    private readonly System.Threading.Timer _idleTimer;

    private State _state = State.Idle;
    private bool _enabled = true;
    private long _pressedAt;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _modelDialogOpen;
    private FrontmostApp? _target;
    private bool _disposed;

    public MenuBarApp(ConfigManager config, SynchronizationContext ui)
    {
        _ui = ui;
        _config = config;
        _stt = new WhisperServerStt(config);
        _llm = new OllamaCleaner(config);
        _pipeline = new DictationPipeline(config, _stt, _llm);

        _menu = new StatusMenu();
        _enabledItem = _menu.AddItem("Enabled", ToggleEnabled);
        _unloadItem = _menu.AddItem("Unload from memory", async () => await UnloadFromMemoryAsync());
        _menu.AddSeparator();
        _menu.AddItem("Open config", () => Dialogs.Open(_config.FilePath, textEditor: true));
        _menu.AddItem("Open log", () => Dialogs.Open(Log.CurrentFile, textEditor: true));
        _menu.AddItem("Show data folder", () => Dialogs.Open(AppPaths.DataDir));
        _menu.AddSeparator();
        _menu.AddItem("Quit WhisperLocal", ExitApp);
        StatusMenu.SetChecked(_enabledItem, true);

        // "Unload from memory" is available only while something is loaded
        _menu.Opening += () => StatusMenu.SetEnabled(_unloadItem, IsAnythingLoaded);

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

        _idleTimer = new System.Threading.Timer(_ => _ui.Post(_ => CheckIdle(), null), null, 30_000, 30_000);

        ApplyConfig();
        UpdateMenuState();
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
        Log.Info($"WhisperLocal {version} (macOS) started, config: {_config.FilePath}");

        if (config.Current.Whisper.StartOnLaunch)
            _ = WarmUpWhisperAsync();
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
        _ = _pipeline.PrepareAsync();
    }

    private void OnHotkeyUp() => _ = FinishRecordingAsync();

    private async Task FinishRecordingAsync()
    {
        if (_state != State.Recording)
            return;

        var cfg = _config.Current;
        var heldMs = Stopwatch.GetElapsedTime(_pressedAt).TotalMilliseconds;
        SetState(State.Processing);
        try
        {
            var pcm = await _recorder.StopAsync();
            if (heldMs < cfg.Hotkey.MinHoldMs)
            {
                Log.Debug($"Hotkey tap ignored ({heldMs:F0} ms)");
                return;
            }

            var result = await _pipeline.ProcessAsync(pcm, _target?.ProcessName, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(result.FinalText))
                return;

            var text = cfg.Insert.AppendTrailingSpace ? result.FinalText + " " : result.FinalText;
            await _inserter.InsertAsync(text, _target, cfg.Insert);
        }
        catch (Exception ex)
        {
            ReportError("Dictation failed", ex);
        }
        finally
        {
            _lastActivityUtc = DateTime.UtcNow;
            SetState(State.Idle);
        }
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

    /// <summary>A missing model gets a dialog with download instructions; other errors a notification.</summary>
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
        // Startup warm-up and a hotkey press may both hit this; one dialog at a time
        if (_modelDialogOpen)
            return;
        _modelDialogOpen = true;
        try
        {
            Dialogs.Show("WhisperLocal: model not found", ex.Message);
        }
        finally
        {
            _modelDialogOpen = false;
        }
    }

    private void SetState(State state)
    {
        _state = state;
        if (_config.Current.Ui.ShowOverlay)
        {
            switch (state)
            {
                case State.Recording:
                    _overlay.ShowState("● Recording", (1.0, 0.35, 0.35));
                    break;
                case State.Processing:
                    _overlay.ShowState("… Processing", (1.0, 0.7, 0.24));
                    break;
                default:
                    _overlay.Hide();
                    break;
            }
        }
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

    private void ExitApp()
    {
        Log.Info("WhisperLocal exiting");
        Dispose();
        // terminate: ends the process without returning from [NSApp run]
        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.Send(app, ObjC.Sel("terminate:"), IntPtr.Zero);
    }

    /// <summary>Stops whisper-server; safe to call from any thread (used on SIGTERM / logout).</summary>
    public void StopServers() => _stt.Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _idleTimer.Dispose();
        _hotkey.Dispose();
        _recorder.Dispose();
        _stt.Dispose();
        _llm.Dispose();
        _overlay.Dispose();
        _menu.Dispose();
    }
}
