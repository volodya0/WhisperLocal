using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Mac.Audio;
using WhisperLocal.Mac.Input;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Mac.Output;
using WhisperLocal.Mac.Ui;
using WhisperLocal.Mac.Updates;
using WhisperLocal.Updates;
using WhisperLocal.Notes;
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

    // Failures and "no speech" need only a glance; a successful summary stays Ui.ResultSeconds
    private static readonly TimeSpan BriefDisplay = TimeSpan.FromSeconds(2);

    private readonly ConfigManager _config;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly SynchronizationContext _ui;
    private readonly DictationHistory _history;
    private readonly DictationStats _stats;
    private readonly StatusMenu _menu;
    private readonly IntPtr _enabledItem;
    private readonly IntPtr _unloadItem;
    private readonly IntPtr _transcriptItem;
    private readonly IntPtr _listenItem;
    private readonly HotkeyTap _hotkey;
    private readonly MacAudioRecorder _recorder = new();
    private readonly WhisperServerStt _stt;
    private readonly OllamaCleaner _llm;
    private readonly DictationPipeline _pipeline;
    private readonly LlamaServer _llama;
    private readonly Autocompleter _autocompleter;
    private readonly LiveAutocomplete _liveAutocomplete;
    private readonly SystemAudioTap _systemAudio = new();
    private readonly MacAudioRecorder _listenMicrophone = new(); // the user, while listening (dictation has its own)
    private readonly Voiceprints _voiceprints;
    private readonly LiveListener _listener;
    private readonly ListenPanel _listenPanel;
    private readonly MacTextInserter _inserter = new();
    private readonly RecordingOverlay _overlay;
    private readonly AutocompletePanel _completionPanel;
    private readonly TranscriptWindow _transcriptWindow = new();
    private readonly MainWindow _mainWindow;
    private readonly System.Threading.Timer _idleTimer;

    private State _state = State.Idle;
    private bool _enabled = true;
    private long _pressedAt;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private FrontmostApp? _target;
    private string _lastTranscript = "";
    private (string Text, FrontmostApp Target, long PastedAt)? _lastPaste; // the transcript window's Replace undoes it
    private bool _checkingForUpdates;
    private Download? _download;
    private Download? _llmDownload;
    private Download? _voiceDownload;
    private bool _microphoneProblemShown; // "listening without your microphone" is said once per run
    private long _systemAudioStartedAt; // Stopwatch timestamps: the microphone's audio is aligned with the system audio's
    private bool _microphoneAligned;
    private long _microphoneGiven; // bytes of microphone audio given to the listener this session, silence included
    private Task? _microphoneStart;
    private CancellationTokenSource? _liveCompletion;
    private CancellationTokenSource? _listening;
    private Task? _listenTask;
    private ListenNotes? _notes;
    private long _listenStartedAt;
    private bool _disposed;

    /// <summary>A model download in progress: what the setup line shows, and how to cancel it.</summary>
    private sealed class Download
    {
        public CancellationTokenSource Cancel { get; } = new();
        public string Text { get; set; } = "";

        /// <summary>Full path of the model file.</summary>
        public string Model { get; init; } = "";
    }

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
        _pipeline = new DictationPipeline(config, new RecognitionRouter(config, _stt), _llm);
        _llama = new LlamaServer(config);
        _autocompleter = new Autocompleter(config, _llama);
        _liveAutocomplete = new LiveAutocomplete(config, _stt, _autocompleter);
        _voiceprints = new Voiceprints(config);
        _listener = new LiveListener(config, new RecognitionRouter(config, _stt, listening: true), new Translator(config, _llama), _voiceprints);
        _listenPanel = new ListenPanel(_ui, StopListening, OpenNotes, ChooseListenLanguage, SetListenIncludeMe, SetListenSpeakers, ListenHint);
        _listener.Heard += _listenPanel.ShowLine;
        _listener.Finished += OnListenLineFinished;
        _listener.Failed += _listenPanel.ShowProblem;
        _listener.Provisional += _listenPanel.ShowProvisional;
        _listener.ProvisionalTranslation += _listenPanel.ShowProvisionalTranslation;
        _overlay = new RecordingOverlay(_ui, ShowLastTranscript);
        _completionPanel = new AutocompletePanel();
        _transcriptWindow.Tools = new TranscriptTools(config, _llama, _history);
        _mainWindow = new MainWindow(this, _transcriptWindow);

        _menu = new StatusMenu();
        _menu.AddItem("Open WhisperLocal…", ShowMainWindow);
        _transcriptItem = _menu.AddItem("Show Last Transcript", ShowLastTranscript);
        _listenItem = _menu.AddItem("Listen to System Audio", ToggleListening);
        _menu.AddItem("Check for Updates…", () => _ = CheckForUpdatesAsync(AppEdition.IsLite));
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
            ObjC.WithPool(() => ObjC.Send(_listenItem, ObjC.Sel("setTitle:"),
                ObjC.Str(_listening != null ? "Stop Listening" : "Listen to System Audio")));
        };

        RequestAccessibility();
        _hotkey = new HotkeyTap(_ui);
        _hotkey.HotkeyDown += OnHotkeyDown;
        _hotkey.HotkeyUp += OnHotkeyUp;
        _hotkey.HotkeyCancel += OnHotkeyCancel;
        _hotkey.TriggerPressed += StartListening;
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
        Log.Info($"WhisperLocal {Updater.CurrentVersionText} {AppEdition.Name} (macOS) started, config: {_config.FilePath}");

        if (_mainWindow.HasProblems)
            ShowMainWindow();
        else if (config.Current.Whisper.StartOnLaunch && !AppEdition.IsLite && !RecognitionRouter.UsesCloud(config.Current))
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

        // What recognises dictation: the provider and the model
        var cfg = _config.Current;
        if (AppEdition.IsLite)
        {
            // Lite has no speech server or local model: Gemini or Groq recognise
            items.Add(RecognitionRouter.UsesCloud(cfg)
                ? new SetupItem(SetupState.Ok, $"Recognition: {RecognitionRouter.Description(cfg)}")
                : new SetupItem(SetupState.Error, "Add a Gemini or Groq key: Lite recognises speech in the cloud", "Add a Key", _mainWindow.ShowModels));
        }
        else
        {
            var cloud = RecognitionRouter.UsesCloud(cfg);
            // Shown only when missing (a damaged install): when it is there, the recognition line says the setup is ready
            var server = WhisperServerStt.ResolveServerExe(cfg.Whisper.ServerExe);
            if (!File.Exists(server))
                items.Add(new SetupItem(cloud ? SetupState.Warning : SetupState.Error, "Speech server not found: " + server));

            var model = AppPaths.Resolve(cfg.Whisper.ModelPath);
            var name = Path.GetFileName(model);
            if (cloud)
                items.Add(new SetupItem(SetupState.Ok, $"Recognition: {RecognitionRouter.Description(cfg)}"));
            if (_download is { } download)
                items.Add(new SetupItem(SetupState.Pending, download.Text, "Cancel", () => download.Cancel.Cancel()));
            else if (cloud)
            {
                // With a cloud engine the local model is only the fallback
                if (cfg.Recognition.FallbackToLocal && !File.Exists(model))
                    items.Add(new SetupItem(SetupState.Warning, $"Fallback model not downloaded: {name}", "Download", () => _ = DownloadModelAsync(model)));
            }
            else if (File.Exists(model))
                items.Add(new SetupItem(SetupState.Ok, $"Recognition: {RecognitionRouter.Description(cfg)} ({FormatSize(new FileInfo(model).Length)})",
                    "Show Folder", () => Dialogs.Open(Path.GetDirectoryName(model)!)));
            else
                items.Add(new SetupItem(SetupState.Error, $"Model not downloaded: {name}", "Download", () => _ = DownloadModelAsync(model)));
        }

        var hotkey = HotkeyTap.Describe(_config.Current.Hotkey.Key);
        items.Add(_hotkey.IsInstalled
            ? new SetupItem(SetupState.Ok, $"Hotkey: hold {hotkey} to dictate")
            : new SetupItem(SetupState.Pending, $"Hotkey {hotkey}: waiting for Accessibility"));

        // Note-taking, one line each: its shortcut, what recognises and, with a translation language, what translates
        if (SystemAudioTap.IsSupported && cfg.Listen.Hotkey.Trim().Length > 0)
        {
            items.Add(new SetupItem(SetupState.Ok, $"Notes: press {HotkeyTap.Describe(cfg.Listen.Hotkey)} to take notes of what the Mac plays"));
            items.Add(new SetupItem(SetupState.Ok, $"Notes recognition: {RecognitionRouter.ListenDescription(cfg)}"));
            var translateTo = cfg.Listen.TranslateTo.Trim();
            if (translateTo.Length > 0)
                items.Add(new SetupItem(SetupState.Ok, $"Notes translation: into {Translator.EnglishName(translateTo)}, {Translator.Description(cfg)}"));
        }

        return items;
    }

    public bool HasGrammarCheck => true; // in Lite with Gemini or Groq

    public bool HasSpeakerSeparation => !AppEdition.IsLite;

    public IReadOnlyList<SetupItem> GetLanguageModelItems()
    {
        var c = _config.Current.LocalLlm;
        var items = new List<SetupItem>();

        var server = LlamaServer.ResolveServerExe(c);
        if (!File.Exists(server))
            items.Add(new SetupItem(SetupState.Error, "Language model server not found: " + server));

        var model = LlamaServer.ResolveModel(c);
        var name = Path.GetFileName(model);
        var known = LlmModels.Find(model);
        if (_llmDownload is { } download)
            items.Add(new SetupItem(SetupState.Pending, download.Text, "Cancel", () => download.Cancel.Cancel()));
        else if (!File.Exists(model))
            items.Add(known != null
                ? new SetupItem(SetupState.Warning, $"Not downloaded yet: {name} ({known.Size})", "Download", () => _ = DownloadLlmAsync(model, known.Url))
                : new SetupItem(SetupState.Error, $"Model not found: {model}", "Show Folder", () => OpenFolder(model)));
        else if (_llama.IsLoaded)
            items.Add(new SetupItem(SetupState.Ok,
                // Unloading is in Models → Memory, with everything else in memory
                $"{name}: loaded" + (_llama.MemoryBytes is { } bytes ? $", llama-server uses {FormatSize(bytes)}" : "")));
        else
            items.Add(new SetupItem(SetupState.Ok,
                $"{name} ({FormatSize(new FileInfo(model).Length)}): loads when you start dictating", "Show Folder", () => OpenFolder(model)));

        return items;
    }

    private static void OpenFolder(string file)
    {
        var folder = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(folder);
        Dialogs.Open(folder);
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

    public IReadOnlyList<(string Name, string Detail)> GetLoadedModels()
    {
        var cfg = _config.Current;
        var loaded = new List<(string, string)>();
        if (_stt.IsLoaded)
        {
            var name = PlainName(WhisperModels.Find(cfg.Whisper.ModelPath)?.Title) ?? Path.GetFileName(cfg.Whisper.ModelPath);
            var uses = _listening != null ? "Speech recognition: dictation and listening" : "Speech recognition";
            loaded.Add((name, uses + (_stt.MemoryBytes is { } bytes ? $" · {FormatSize(bytes)}" : "")));
        }
        if (_llama.IsLoaded)
        {
            var name = PlainName(LlmModels.Find(LlamaServer.ResolveModel(cfg.LocalLlm))?.Title) ?? Path.GetFileName(cfg.LocalLlm.ModelPath);
            var uses = IsTranslatingLocally ? "Language model: notes translation and the transcript" : "Language model: the transcript (translate, check)";
            loaded.Add((name, uses + (_llama.MemoryBytes is { } bytes ? $" · {FormatSize(bytes)}" : "")));
        }
        if (_voiceprints.IsLoaded)
            loaded.Add((VoiceModels.Find(cfg.Listen.VoiceModelPath)?.Title ?? Path.GetFileName(cfg.Listen.VoiceModelPath),
                "Voice model: telling speakers apart while listening"));
        return loaded;
    }

    /// <summary>"Large v3 (most accurate)" → "Large v3": the hint helps choosing, not reading what is loaded.</summary>
    private static string? PlainName(string? title) =>
        title == null ? null : System.Text.RegularExpressions.Regex.Replace(title, @"\s*\([^)]*\)\s*$", "");

    public Task UnloadAsync() => UnloadFromMemoryAsync();

    public async Task LoadAsync()
    {
        _lastActivityUtc = DateTime.UtcNow;
        await WarmUpWhisperAsync();
    }

    public void DownloadModel(string modelPath, string? url = null)
    {
        var model = AppPaths.Resolve(modelPath);
        if (VoiceModels.Find(model) is { } voice)
            _ = DownloadVoiceModelAsync(model, voice);
        else
            _ = url == null ? DownloadModelAsync(model) : DownloadLlmAsync(model, url);
    }

    public string? DownloadProgress(string modelPath) => FindDownload(modelPath)?.Text;

    public void CancelDownload(string modelPath) => FindDownload(modelPath)?.Cancel.Cancel();

    private Download? FindDownload(string modelPath)
    {
        var model = AppPaths.Resolve(modelPath);
        return new[] { _download, _llmDownload, _voiceDownload }.FirstOrDefault(d => d != null && d.Model == model);
    }

    public void CheckForUpdates(bool lite) => _ = CheckForUpdatesAsync(lite);

    public void OpenPath(string path, bool textEditor = false) => Dialogs.Open(path, textEditor);

    public void MainWindowVisibilityChanged(bool visible)
    {
        // The app stays a menu bar (accessory) app, no Dock icon: once it has been a regular app, macOS no longer shows
        // its indicator over full-screen apps, even after switching back
        if (visible)
            ObjC.Send(ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication")), ObjC.Sel("activateIgnoringOtherApps:"), true);
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
            _transcriptWindow.ShowText(_lastTranscript, pasteBack: PasteBackFor(_lastTranscript));
    }

    /// <summary>
    /// Replace in the transcript window, when <paramref name="text"/> is the last dictation and it was pasted: undoes
    /// that paste and pastes the window's text. If a key was pressed since (typing there), undo would hit that, so the
    /// text is pasted at the cursor instead.
    /// </summary>
    private PasteBack? PasteBackFor(string text)
    {
        if (_lastPaste is not { } p || p.Text != text)
            return null;
        var sinceKey = MacNative.CGEventSourceSecondsSinceLastEventType(MacNative.kCGEventSourceStateHIDSystemState, MacNative.kCGEventKeyDown);
        var typedSince = sinceKey < Stopwatch.GetElapsedTime(p.PastedAt).TotalSeconds;
        var name = p.Target.ProcessName.Length > 0 ? p.Target.ProcessName : "the app";
        return new PasteBack(name, !typedSince, (newText, replace) => PasteBackAsync(newText, replace, p.Target));
    }

    private async Task PasteBackAsync(string text, bool replace, FrontmostApp target)
    {
        if (_state != State.Idle)
            return;
        try
        {
            // The transcript window had the focus: give it back to the app the dictation went into
            if (!target.Activate())
            {
                Dialogs.Notify($"{target.ProcessName} is no longer running; copy the text instead");
                return;
            }
            for (var i = 0; i < 20 && FrontmostApp.Capture().ProcessId != target.ProcessId; i++)
                await Task.Delay(50);
            if (FrontmostApp.Capture().ProcessId != target.ProcessId)
            {
                Dialogs.Notify($"Could not switch back to {target.ProcessName}; copy the text instead");
                return;
            }
            await Task.Delay(120); // the app focuses its text field again

            var cfg = _config.Current;
            var trimmed = text.Trim();
            var insert = cfg.Insert.AppendTrailingSpace ? trimmed + " " : trimmed;
            Log.Info(replace ? "Transcript window: replacing the dictation" : "Transcript window: pasting at the cursor");
            var pastedAt = Stopwatch.GetTimestamp();
            if (replace)
                await _inserter.ReplaceLastAsync(insert, target, cfg.Insert);
            else
                await _inserter.InsertAsync(insert, target, cfg.Insert);
            // The new text is now the last paste: Replace again replaces it
            _lastTranscript = trimmed;
            _lastPaste = (trimmed, target, pastedAt);
        }
        catch (Exception ex)
        {
            ReportError(replace ? "Replacing the dictation failed" : "Pasting the text failed", ex);
        }
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
            _hotkey.SetTrigger(cfg.Listen.Hotkey.Trim().Length == 0 ? [] : HotkeyTap.ParseHotkey(cfg.Listen.Hotkey));
        }
        catch (ArgumentException ex)
        {
            Log.Error($"Listen.Hotkey: {ex.Message}");
            Dialogs.Notify($"Listen.Hotkey: {ex.Message}");
        }

        if (!cfg.Autocomplete.IsOn)
            StopAutocomplete();
        if (!IsLanguageModelWanted && _llama.IsLoaded)
            _llama.Unload();

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
        StopAutocomplete();
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
        if (!AppEdition.IsLite && !File.Exists(modelPath) && !RecognitionRouter.UsesCloud(cfg))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            ShowModelMissing(new ModelNotFoundException(modelPath));
            return;
        }
        // Lite without a cloud key: say so now, not after the recording fails
        if (AppEdition.IsLite && !RecognitionRouter.UsesCloud(cfg))
        {
            Dialogs.Notify("WhisperLocal Lite recognises speech with Gemini or Groq: add a key on the Models page");
            _mainWindow.ShowModels();
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
        if (IsLanguageModelReady)
            _ = PreloadLanguageModelAsync();
        StartAutocomplete();
    }

    private void OnHotkeyUp() => _ = FinishRecordingAsync();

    private async Task FinishRecordingAsync()
    {
        if (_state != State.Recording)
            return;

        var cfg = _config.Current;
        var recordSec = Stopwatch.GetElapsedTime(_pressedAt).TotalSeconds;
        var processStart = Stopwatch.GetTimestamp();
        StopAutocomplete();
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

            // Nowhere to type (a file list, the desktop): show the text to edit and copy instead of pasting into nothing
            if (cfg.Insert.ShowWhenNoTextField && FocusedInput.AcceptsText(FrontmostApp.Capture().ProcessId) == false)
            {
                Log.Info("No text input is focused: showing the transcript instead of pasting");
                _transcriptWindow.ShowText(result.FinalText, "No text field was focused, so nothing was pasted. Edit and copy it here:");
                return;
            }

            var text = cfg.Insert.AppendTrailingSpace ? result.FinalText + " " : result.FinalText;
            // Where it goes (the frontmost app may have changed while processing) and when, for the transcript's Replace;
            // taken before the paste, as InsertAsync returns only after restoring the clipboard and a key pressed
            // meanwhile is typing after the dictation
            var pasted = (result.FinalText.Trim(), FrontmostApp.Capture(), Stopwatch.GetTimestamp());
            await _inserter.InsertAsync(text, _target, cfg.Insert);
            _lastPaste = pasted;
            if (cfg.Autocomplete.IsOn)
                _autocompleter.Remember(_target?.ProcessName, result.FinalText);
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
                _overlay.ShowResult($"✕ Failed: {error}", (1.0, 0.4, 0.4), hasText: false, BriefDisplay);
            else if (words > 0)
                _overlay.ShowResult($"✓ {recordSec:F1} s rec · {processSec:F1} s proc · {words} {(words == 1 ? "word" : "words")}",
                    (0.45, 0.88, 0.5), hasText: true, TimeSpan.FromSeconds(Math.Max(1, cfg.Ui.ResultSeconds)));
            else
                _overlay.ShowResult($"No speech recognized · {recordSec:F1} s", (0.75, 0.75, 0.75), hasText: false, BriefDisplay);
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

    // ---- Language model: autocomplete, translation and the transcript window's tools ----

    /// <summary>Something keeps the local model in use (the transcript window's tools load it on demand).</summary>
    private bool IsLanguageModelWanted =>
        _config.Current.Autocomplete.IsOn || IsTranslatingLocally;

    /// <summary>Listening translates, with the local model (not Gemini or Groq).</summary>
    private bool IsTranslatingLocally =>
        _listening != null && _listener.TranslateTo.Length > 0 && Translator.CloudName(_config.Current) == null;

    /// <summary>Something can translate: a cloud engine with its key, or the local model installed.</summary>
    private bool CanTranslate => Translator.CloudName(_config.Current) != null || IsLanguageModelInstalled;

    /// <summary>llama-server and the model are in place (the server starts on demand).</summary>
    private bool IsLanguageModelInstalled
    {
        get
        {
            var c = _config.Current.LocalLlm;
            return File.Exists(LlamaServer.ResolveServerExe(c)) && File.Exists(LlamaServer.ResolveModel(c));
        }
    }

    /// <summary>A feature is on and the language model is installed.</summary>
    private bool IsLanguageModelReady => IsLanguageModelWanted && IsLanguageModelInstalled;

    /// <summary>On hotkey press: loads the model while the user speaks.</summary>
    private async Task PreloadLanguageModelAsync()
    {
        try
        {
            await _llama.WarmUpAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"llama-server warm-up failed: {ex.Message}");
        }
    }

    /// <summary>On hotkey press: shows the continuation of what is being said until the hotkey is released.</summary>
    private void StartAutocomplete()
    {
        StopAutocomplete();
        if (!_config.Current.Autocomplete.IsOn || !IsLanguageModelReady)
            return;
        var live = _liveCompletion = new CancellationTokenSource();
        _ = RunAutocompleteAsync(live);
    }

    private async Task RunAutocompleteAsync(CancellationTokenSource live)
    {
        try
        {
            await _liveAutocomplete.RunAsync(_recorder.Snapshot, _target?.ProcessName, (completion, pausing) =>
            {
                if (live.IsCancellationRequested)
                    return;
                if (completion == null)
                    _completionPanel.Hide();
                else
                    _completionPanel.Show(completion.Tail, completion.Continuation, pausing);
            }, live.Token);
        }
        finally
        {
            if (_liveCompletion == live)
                _liveCompletion = null;
            live.Dispose();
        }
    }

    private void StopAutocomplete()
    {
        _liveCompletion?.Cancel();
        _liveCompletion = null;
        _completionPanel.Hide();
    }

    // ---- Listening: system audio → notes, optionally translated ----

    /// <summary>Listening notes go with the other notes, so the Notes page lists them.</summary>
    private string NotesFolder => NoteFiles.Folder(_config.Current.Notes);

    private void ToggleListening()
    {
        if (_listening != null)
            StopListening();
        else
            StartListening();
    }

    /// <summary>The listening shortcut: starts listening; while listening it only brings the indicator back.</summary>
    private void StartListening()
    {
        if (_listening != null)
        {
            _listenPanel.BringToFront();
            return;
        }
        if (!SystemAudioTap.IsSupported)
        {
            Dialogs.Show("Listening", "Listening to what the Mac plays needs macOS 14.2 or later.", Dialogs.Style.Informational);
            return;
        }

        var cfg = _config.Current;
        var modelPath = AppPaths.Resolve(cfg.Whisper.ModelPath);
        var cloud = RecognitionRouter.ListenCloudName(cfg);
        if (cloud == null && AppEdition.IsLite)
        {
            Dialogs.Notify("WhisperLocal Lite listens with Gemini or Groq: add a key on the Models page");
            _mainWindow.ShowModels();
            return;
        }
        if (cloud == null && !File.Exists(modelPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            ShowModelMissing(new ModelNotFoundException(modelPath));
            return;
        }

        try
        {
            _systemAudio.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start listening to system audio", ex);
            Dialogs.Notify($"Cannot listen to system audio: {ex.Message}");
            return;
        }

        _systemAudioStartedAt = Stopwatch.GetTimestamp();
        _microphoneAligned = false;
        _microphoneGiven = 0;
        _listener.IncludeMe = cfg.Listen.IncludeMe;
        _listener.SeparateSpeakers = cfg.Listen.SeparateSpeakers;
        if (cfg.Listen.IncludeMe)
            _microphoneStart = StartListenMicrophoneAsync(cfg.Audio.DeviceName);

        // Translation needs a language model; without one the choice waits until it is downloaded (or a key added)
        var language = CanTranslate ? cfg.Listen.TranslateTo.Trim() : "";
        _listener.TranslateTo = language;
        _notes = new ListenNotes(NotesFolder, cfg.Notes.Extension);
        var listening = _listening = new CancellationTokenSource();
        _listenStartedAt = Stopwatch.GetTimestamp();
        _lastActivityUtc = DateTime.UtcNow;
        Log.Info($"Listening started{(cloud != null ? $", recognising with {cloud}" : "")}{(cfg.Listen.IncludeMe ? ", with the microphone" : "")}" +
                 $"{(language.Length > 0 ? $", translating into {language}" : "")}");
        _listenPanel.Show(cfg.Listen.Languages, language, cfg.Listen.IncludeMe, cfg.Listen.SeparateSpeakers && _voiceprints.IsAvailable);
        UpdateMenuState();
        if (cloud == null)
            _ = WarmUpWhisperAsync();
        if (IsTranslatingLocally)
            _ = PreloadLanguageModelAsync();
        _listenTask = RunListeningAsync(listening);
    }

    /// <summary>
    /// The user as "Me". Started off the main thread: starting a device can wait on Core Audio, with Bluetooth
    /// headphones switching to their microphone even forever (the running system audio capture has to restart);
    /// listening goes on meanwhile, and without the microphone if it cannot start (denied, no device).
    /// </summary>
    private async Task StartListenMicrophoneAsync(string deviceName)
    {
        var start = Task.Run(() => _listenMicrophone.Start(deviceName, 0));
        if (await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(5))) != start)
            Log.Warn("Listening: the microphone has not started after 5 s");
        try
        {
            await start;
        }
        catch (Exception ex)
        {
            Log.Warn($"Listening without the microphone: {ex.Message}");
            if (!_microphoneProblemShown)
                Dialogs.Notify($"Listening without your microphone: {ex.Message}");
            _microphoneProblemShown = true;
            return;
        }
        // Started only after listening stopped, or after the user was left out
        if ((_listening == null || !_listener.IncludeMe) && _listenMicrophone.IsRecording)
            await _listenMicrophone.StopAsync();
    }

    /// <summary>
    /// The microphone's audio since the last call; the first audio after it (re)started comes after silence for the
    /// time it was not recording, so both are on one timeline (the echo check compares them moment by moment).
    /// </summary>
    private byte[] DrainListenMicrophone()
    {
        var pcm = _listenMicrophone.Drain();
        if (!_microphoneAligned && _listenMicrophone.IsRecording)
        {
            _microphoneAligned = true;
            var late = Stopwatch.GetElapsedTime(_systemAudioStartedAt, _listenMicrophone.StartedAt);
            var gap = late <= TimeSpan.Zero ? 0 : AudioConvert.Bytes((int)late.TotalMilliseconds) - _microphoneGiven;
            if (gap > 0)
                pcm = [.. new byte[gap], .. pcm];
        }
        _microphoneGiven += pcm.Length;
        return pcm;
    }

    private async Task RunListeningAsync(CancellationTokenSource listening)
    {
        try
        {
            await _listener.RunAsync(_systemAudio.Drain, DrainListenMicrophone, listening.Token);
        }
        catch (Exception ex)
        {
            ReportError("Listening failed", ex);
        }
        finally
        {
            _systemAudio.Stop();
            if (_listenMicrophone.IsRecording)
                await _listenMicrophone.StopAsync();
            _voiceprints.Unload();
            _listenPanel.Hide();
            var notes = _notes;
            _notes = null;
            _listening = null;
            _listenTask = null;
            listening.Dispose();
            _lastActivityUtc = DateTime.UtcNow;
            UpdateMenuState();
            Log.Info($"Listening stopped: {notes?.Lines ?? 0} lines");
            try
            {
                notes?.Close();
                if (notes is { Lines: > 0 })
                    Dialogs.Notify($"Notes saved: {Path.GetFileName(notes.FilePath)}");
            }
            catch (Exception ex)
            {
                Log.Error("Saving the notes failed", ex);
            }
        }
    }

    /// <summary>The indicator's stop button: the capture stops now, what was already heard is still finished.</summary>
    private void StopListening()
    {
        if (_listening is not { IsCancellationRequested: false } listening)
            return;
        listening.Cancel();
        _systemAudio.Stop();
        _listenPanel.ShowFinishing();
    }

    private void OnListenLineFinished(ListenLine line)
    {
        _lastActivityUtc = DateTime.UtcNow;
        _listenPanel.ShowLine(line);
        try
        {
            _notes?.Add(line);
        }
        catch (Exception ex)
        {
            Log.Error("Writing the notes failed", ex);
            _listenPanel.ShowProblem($"cannot write the notes: {ex.Message}");
        }
    }

    /// <summary>The indicator's language menu: applies from the next line on, and is remembered for the next time.</summary>
    private bool ChooseListenLanguage(string code)
    {
        if (code.Length > 0 && !CanTranslate)
        {
            Dialogs.Notify("Translation uses a language model: download one, or add a Gemini or Groq key, on the Models page");
            _mainWindow.ShowModels();
            return false;
        }
        _listener.TranslateTo = code;
        Log.Info(code.Length > 0 ? $"Listening: translating into {code}" : "Listening: original only");
        if (IsTranslatingLocally)
            _ = PreloadLanguageModelAsync();
        try
        {
            ConfigEditor.SetValue(_config.FilePath, "Listen", "TranslateTo", code);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot remember the translation language: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// The indicator's Me switch: the microphone starts or stops now, and the choice is remembered for the next time
    /// (Listen.IncludeMe).
    /// </summary>
    private bool SetListenIncludeMe(bool on)
    {
        _listener.IncludeMe = on;
        if (on && _listening != null && !_listenMicrophone.IsRecording && _microphoneStart is not { IsCompleted: false })
        {
            _microphoneAligned = false;
            _microphoneStart = StartListenMicrophoneAsync(_config.Current.Audio.DeviceName);
        }
        else if (!on && _listenMicrophone.IsRecording)
            _ = _listenMicrophone.StopAsync();
        Log.Info(on ? "Listening: with the microphone" : "Listening: without the microphone");
        RememberListen("IncludeMe", on);
        return true;
    }

    /// <summary>
    /// The indicator's Speakers switch: applies from the next line on, and is remembered for the next time
    /// (Listen.SeparateSpeakers). Telling them apart needs the voice model.
    /// </summary>
    private bool SetListenSpeakers(bool on)
    {
        if (on && !_voiceprints.IsAvailable)
        {
            Dialogs.Notify("Telling speakers apart needs the voice model: download it on the Models page (Speaker separation)");
            _mainWindow.ShowModels();
            return false;
        }
        _listener.SeparateSpeakers = on;
        Log.Info(on ? "Listening: telling speakers apart" : "Listening: speakers not told apart");
        RememberListen("SeparateSpeakers", on);
        return true;
    }

    private void RememberListen(string property, bool value)
    {
        try
        {
            ConfigEditor.SetValue(_config.FilePath, "Listen", property, value);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot remember Listen.{property}: {ex.Message}");
        }
    }

    /// <summary>While nothing was heard for a few seconds: most likely System Audio Recording is not allowed.</summary>
    private string? ListenHint()
    {
        if (_listening == null || _systemAudio.HeardAnything || Stopwatch.GetElapsedTime(_listenStartedAt) < TimeSpan.FromSeconds(5))
            return null;
        return "No sound yet. If something is playing, allow WhisperLocal in System Settings → Privacy & Security → " +
               "Screen & System Audio Recording (System Audio Recording Only).";
    }

    private void OpenNotes()
    {
        if (_notes is { Lines: > 0 } notes)
            Dialogs.Open(notes.FilePath);
        else
            OpenFolder(Path.Combine(NotesFolder, "_"));
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
        if (_listenTask is { } listening)
        {
            StopListening();
            await listening;
        }
        if (_state == State.Recording)
        {
            await _recorder.StopAsync();
            _overlay.Hide();
            SetState(State.Idle);
        }

        StopAutocomplete();
        _stt.Unload();
        _llama.Unload();
        _voiceprints.Unload();
        if (_config.Current.Llm.Enabled)
            await _llm.UnloadAsync();
        Log.Info("Unloaded from memory: all servers stopped, models released");
    }

    private bool IsAnythingLoaded => _stt.IsLoaded || _llama.IsLoaded || _voiceprints.IsLoaded;

    private async void CheckIdle()
    {
        var minutes = _config.Current.Memory.IdleUnloadMinutes;
        if (_disposed || minutes <= 0 || _state != State.Idle || _listening != null || !IsAnythingLoaded)
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

    private Task DownloadModelAsync(string model) =>
        DownloadIntoAsync(() => _download, d => _download = d, model, ModelDownloader.UrlFor(model), null, "Model downloaded, ready to dictate");

    private Task DownloadLlmAsync(string model, string url) =>
        DownloadIntoAsync(() => _llmDownload, d => _llmDownload = d, model, url, null, "Language model downloaded");

    private Task DownloadVoiceModelAsync(string model, VoiceModel voice) =>
        DownloadIntoAsync(() => _voiceDownload, d => _voiceDownload = d, model, voice.Url, voice.Sha256,
            "Voice model downloaded: listening tells speakers apart");

    /// <summary>Downloads into one slot (one download of each kind at a time); <paramref name="done"/> is the notice after.</summary>
    private async Task DownloadIntoAsync(Func<Download?> slot, Action<Download?> setSlot, string model, string url, string? sha256, string done)
    {
        if (slot() != null)
            return;
        var download = new Download { Model = model };
        setSlot(download);
        try
        {
            if (await DownloadAsync(model, url, sha256, download))
                Dialogs.Notify(done);
        }
        finally
        {
            setSlot(null);
            _mainWindow.Refresh();
        }
    }

    /// <summary>Downloads <paramref name="model"/>, keeping <see cref="Download.Text"/> up to date; false if cancelled or failed.</summary>
    private async Task<bool> DownloadAsync(string model, string url, string? sha256, Download download)
    {
        var name = Path.GetFileName(model);
        download.Text = $"Downloading {name}…";

        var progress = new Progress<(long Done, long? Total)>(p =>
            download.Text = p.Total is { } total and > 0
                ? $"Downloading {name}: {FormatSize(p.Done)} of {FormatSize(total)} ({p.Done * 100 / total}%)"
                : $"Downloading {name}: {FormatSize(p.Done)}");
        try
        {
            await ModelDownloader.DownloadAsync(model, url, progress, () => _ui.Post(_ => download.Text = $"Verifying {name}…", null),
                download.Cancel.Token, sha256);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Info($"Download of {name} cancelled; it resumes on the next Download");
        }
        catch (Exception ex)
        {
            Log.Error($"Download of {name} failed", ex);
            Dialogs.Show("Download failed", $"{ex.Message}\n\nYou can also download it in a browser:\n{url}\n\nand put it into:\n{Path.GetDirectoryName(model)}");
        }
        return false;
    }

    /// <summary>
    /// Manual update check (never automatic) of the Full or the Lite edition: asks before downloading and installing; the
    /// other edition than this one is offered in any version (a switch).
    /// </summary>
    private async Task CheckForUpdatesAsync(bool lite)
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
            var release = await Updater.GetLatestAsync(lite, CancellationToken.None);
            if (release == null && lite != AppEdition.IsLite)
            {
                Dialogs.Show("Not published yet", $"No release has the {(lite ? "Lite" : "Full")} edition for this Mac yet.", Dialogs.Style.Informational);
                return;
            }
            if (release == null || !GitHubReleases.IsWorthInstalling(release, lite))
            {
                Dialogs.Show("You're up to date", $"WhisperLocal {Updater.CurrentVersionText} {AppEdition.Name} is the latest version.", Dialogs.Style.Informational);
                return;
            }

            var (title, message) = GitHubReleases.Describe(release, lite);
            if (!Dialogs.Ask(title, message, "Install and Restart", "Later"))
                return;
            if (_state != State.Idle)
            {
                Dialogs.Show("Update", "Finish the current dictation first, then check for updates again.", Dialogs.Style.Informational);
                return;
            }

            Log.Info($"Installing update {release.Tag} ({(lite ? "Lite" : "Full")})");
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
            _ when _listening != null => (StatusMenu.Icon.Idle, "WhisperLocal: listening to system audio"),
            _ => (StatusMenu.Icon.Idle, $"WhisperLocal: hold {HotkeyTap.Describe(_config.Current.Hotkey.Key)} to dictate"),
        };
        _menu.SetState(icon, text);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";

    private void ExitApp()
    {
        Log.Info("WhisperLocal exiting");
        // Quitting always quits: after an update the bundle is already replaced, and cleaning up may fail there
        try
        {
            Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Cleaning up on exit failed", ex);
        }
        _desktop.Shutdown();
    }

    /// <summary>Stops whisper-server; safe to call from any thread (used on SIGTERM / logout).</summary>
    public void StopServers()
    {
        _stt.Dispose();
        _llama.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _download?.Cancel.Cancel();
        _llmDownload?.Cancel.Cancel();
        _voiceDownload?.Cancel.Cancel();
        _liveCompletion?.Cancel();
        _listening?.Cancel();
        _systemAudio.Dispose();
        _listenMicrophone.Dispose();
        _voiceprints.Dispose();
        _notes?.Close();
        _notes = null;
        _idleTimer.Dispose();
        _hotkey.Dispose();
        _recorder.Dispose();
        _stt.Dispose();
        _llm.Dispose();
        _llama.Dispose();
        _overlay.Dispose();
        _completionPanel.Dispose();
        _listenPanel.Dispose();
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
