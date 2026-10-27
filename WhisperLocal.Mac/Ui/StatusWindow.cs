using WhisperLocal.Config;
using WhisperLocal.Mac.Audio;
using WhisperLocal.Mac.Input;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Stt;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// Setup/status window: permissions, whisper-server, model (with an in-app download) and the hotkey,
/// each with a button to fix it. While it is open the app also shows in the Dock and the app switcher.
/// </summary>
internal sealed class StatusWindow : IDisposable
{
    private const double Width = 600;
    private const double RowHeight = 44;
    private const double ButtonWidth = 150;
    private const int Rows = 5;
    private const double Height = 70 + Rows * RowHeight + 90;

    private const ulong StyleTitled = 1, StyleClosable = 2, StyleMiniaturizable = 4;
    private const long ActivationRegular = 0, ActivationAccessory = 1;

    private const string AccessibilitySettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";
    private const string MicrophoneSettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";

    private enum Row { Accessibility, Microphone, Server, Model, Hotkey }

    private readonly ConfigManager _config;
    private readonly Func<bool> _hotkeyInstalled;
    private readonly Action _requestMicrophone;
    private readonly SynchronizationContext _ui;
    private readonly IntPtr _window;
    private readonly IntPtr[] _labels = new IntPtr[Rows];
    private readonly IntPtr[] _buttons = new IntPtr[Rows];
    private readonly Action?[] _buttonActions = new Action?[Rows];
    private readonly System.Threading.Timer _refreshTimer;

    private CancellationTokenSource? _download;
    private string? _downloadText;

    public StatusWindow(ConfigManager config, SynchronizationContext ui, Func<bool> hotkeyInstalled, Action requestMicrophone)
    {
        _config = config;
        _ui = ui;
        _hotkeyInstalled = hotkeyInstalled;
        _requestMicrophone = requestMicrophone;

        _window = Send(Send(Class("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
            new NSRect(0, 0, Width, Height), StyleTitled | StyleClosable | StyleMiniaturizable, 2UL, false);
        Send(_window, Sel("setTitle:"), Str("WhisperLocal"));
        Send(_window, Sel("setReleasedWhenClosed:"), false);
        Send(_window, Sel("setDelegate:"), ActionTarget.Instance);
        var content = Send(_window, Sel("contentView"));

        var title = Label("WhisperLocal setup", 20, Height - 46, Width - 40, 26);
        Send(title, Sel("setFont:"), Send(Class("NSFont"), Sel("boldSystemFontOfSize:"), 17.0));
        Send(content, Sel("addSubview:"), title);

        for (var i = 0; i < Rows; i++)
        {
            var y = Height - 70 - (i + 1) * RowHeight + 10;
            _labels[i] = Label("", 20, y + 2, Width - ButtonWidth - 50, 20);
            Send(content, Sel("addSubview:"), _labels[i]);

            var index = i;
            var button = Send(Class("NSButton"), Sel("buttonWithTitle:target:action:"),
                Str(""), ActionTarget.Instance, ActionTarget.ActionSelector);
            Send(button, Sel("setTag:"), ActionTarget.Register(() => _buttonActions[index]?.Invoke()));
            Send(button, Sel("setFrame:"), new NSRect(Width - ButtonWidth - 20, y - 4, ButtonWidth, 30));
            Send(content, Sel("addSubview:"), button);
            _buttons[i] = button;
        }

        var hint = Send(Class("NSTextField"), Sel("wrappingLabelWithString:"), Str(
            "WhisperLocal lives in the menu bar (microphone icon). If you cannot see it, it may be hidden behind the " +
            "notch or switched off in System Settings → Menu Bar. Open WhisperLocal from Applications again to get back here."));
        Send(hint, Sel("setFrame:"), new NSRect(20, 16, Width - 40, 62));
        Send(hint, Sel("setTextColor:"), Send(Class("NSColor"), Sel("secondaryLabelColor")));
        Send(content, Sel("addSubview:"), hint);

        ActionTarget.WindowWillClose += OnClosed;
        _refreshTimer = new System.Threading.Timer(_ => _ui.Post(_ => { if (IsVisible) Refresh(); }, null), null, 1500, 1500);
    }

    public bool IsVisible => SendBool(_window, Sel("isVisible"));

    /// <summary>True if something has to be done before dictation can work.</summary>
    public bool HasProblems =>
        !MacNative.AXIsProcessTrusted()
        || MacAudioRecorder.GetMicrophoneAccess() == MacAudioRecorder.MicrophoneAccess.Denied
        || !File.Exists(ServerPath)
        || !File.Exists(ModelPath);

    private string ServerPath => WhisperServerStt.ResolveServerExe(_config.Current.Whisper.ServerExe);
    private string ModelPath => AppPaths.Resolve(_config.Current.Whisper.ModelPath);

    public void Show()
    {
        Refresh();
        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        // A regular app while the window is open: Dock icon and Cmd+Tab, so the window cannot get lost
        Send(app, Sel("setActivationPolicy:"), ActivationRegular);
        if (!IsVisible)
            Send(_window, Sel("center"));
        Send(_window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        Send(app, Sel("activateIgnoringOtherApps:"), true);
    }

    private void OnClosed()
    {
        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        Send(app, Sel("setActivationPolicy:"), ActivationAccessory);
    }

    public void Refresh()
    {
        // Accessibility: global hotkey + Cmd+V
        if (MacNative.AXIsProcessTrusted())
            SetRow(Row.Accessibility, "✅  Accessibility: allowed");
        else
            SetRow(Row.Accessibility, "❌  Accessibility: not allowed (needed for the hotkey and pasting)",
                "Open Settings", () => Dialogs.Open(AccessibilitySettings));

        switch (MacAudioRecorder.GetMicrophoneAccess())
        {
            case MacAudioRecorder.MicrophoneAccess.Allowed:
                SetRow(Row.Microphone, "✅  Microphone: allowed");
                break;
            case MacAudioRecorder.MicrophoneAccess.Denied:
                SetRow(Row.Microphone, "❌  Microphone: denied", "Open Settings", () => Dialogs.Open(MicrophoneSettings));
                break;
            case MacAudioRecorder.MicrophoneAccess.NotAsked:
                SetRow(Row.Microphone, "⚠️  Microphone: not asked yet", "Allow…", _requestMicrophone);
                break;
            default:
                SetRow(Row.Microphone, "❔  Microphone: unknown (asked on the first dictation)");
                break;
        }

        var server = ServerPath;
        if (File.Exists(server))
            SetRow(Row.Server, "✅  Speech server: " + Path.GetFileName(server));
        else
            SetRow(Row.Server, "❌  Speech server not found: " + server);

        var model = ModelPath;
        var name = Path.GetFileName(model);
        if (_download != null)
            SetRow(Row.Model, _downloadText ?? $"⏳  Downloading {name}…", "Cancel", () => _download?.Cancel());
        else if (File.Exists(model))
            SetRow(Row.Model, $"✅  Model: {name} ({FormatSize(new FileInfo(model).Length)})", "Show Folder",
                () => Dialogs.Open(Path.GetDirectoryName(model)!));
        else
            SetRow(Row.Model, $"❌  Model not downloaded: {name}", "Download", () => _ = DownloadAsync(model));

        var hotkey = HotkeyTap.Describe(_config.Current.Hotkey.Key);
        if (_hotkeyInstalled())
            SetRow(Row.Hotkey, $"✅  Hotkey: hold {hotkey} to dictate");
        else
            SetRow(Row.Hotkey, $"⏳  Hotkey {hotkey}: waiting for Accessibility");
    }

    private async Task DownloadAsync(string model)
    {
        if (_download != null)
            return;
        _download = new CancellationTokenSource();
        var name = Path.GetFileName(model);
        _downloadText = $"⏳  Downloading {name}…";
        Refresh();

        var progress = new Progress<(long Done, long? Total)>(p =>
        {
            _downloadText = p.Total is { } total and > 0
                ? $"⏳  Downloading {name}: {FormatSize(p.Done)} of {FormatSize(total)} ({p.Done * 100 / total}%)"
                : $"⏳  Downloading {name}: {FormatSize(p.Done)}";
            Refresh();
        });

        try
        {
            await ModelDownloader.DownloadAsync(model, progress, () => _ui.Post(_ =>
            {
                _downloadText = $"⏳  Verifying {name}…";
                Refresh();
            }, null), _download.Token);
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
            Refresh();
        }
    }

    private void SetRow(Row row, string text, string? buttonTitle = null, Action? onClick = null)
    {
        var i = (int)row;
        Send(_labels[i], Sel("setStringValue:"), Str(text));
        _buttonActions[i] = onClick;
        Send(_buttons[i], Sel("setHidden:"), buttonTitle == null);
        if (buttonTitle != null)
            Send(_buttons[i], Sel("setTitle:"), Str(buttonTitle));
    }

    private static IntPtr Label(string text, double x, double y, double width, double height)
    {
        var label = Send(Class("NSTextField"), Sel("labelWithString:"), Str(text));
        Send(label, Sel("setFrame:"), new NSRect(x, y, width, height));
        Send(label, Sel("setLineBreakMode:"), 4L); // NSLineBreakByTruncatingTail
        return label;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";

    public void Dispose()
    {
        _refreshTimer.Dispose();
        _download?.Cancel();
        ActionTarget.WindowWillClose -= OnClosed;
        Send(_window, Sel("orderOut:"), IntPtr.Zero);
        Release(_window);
    }
}
