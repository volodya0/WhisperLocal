using System.Diagnostics;
using System.Runtime.InteropServices;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// Small indicator at the bottom of the screen; a non-activating panel that never takes focus.
/// Recording and processing show a live seconds counter; afterwards a short summary (with a button that opens
/// the transcript) stays for a moment, longer while the mouse is over it. Click-through except in that state.
/// </summary>
internal sealed class RecordingOverlay : IDisposable
{
    private const double Height = 36;
    private const double TimerWidth = 210;
    private const double ResultWidth = 360;
    private const double ButtonWidth = 64;

    private const ulong NSWindowStyleMaskBorderless = 0;
    private const ulong NSWindowStyleMaskNonactivatingPanel = 1 << 7;
    private const ulong NSBackingStoreBuffered = 2;
    private const long NSStatusWindowLevel = 25;
    private const long CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6, FullScreenAuxiliary = 1 << 8;

    // NSTextAlignmentLeft is 0 everywhere; Center is 1 with the iOS-compatible ABI (arm64) and 2 on x86_64
    private static readonly long TextAlignmentCenter = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 2 : 1;

    private static readonly (double R, double G, double B) Red = (1.0, 0.35, 0.35);
    private static readonly (double R, double G, double B) Orange = (1.0, 0.7, 0.24);

    private enum Mode { Hidden, Recording, Processing, Result }

    private readonly SynchronizationContext _ui;
    private readonly IntPtr _panel;
    private readonly IntPtr _label;
    private readonly IntPtr _button;
    private readonly IntPtr _background;
    private readonly System.Threading.Timer _timer;

    private Mode _mode = Mode.Hidden;
    private long _since;
    private TimeSpan _resultFor;

    public RecordingOverlay(SynchronizationContext ui, Action showText)
    {
        _ui = ui;
        _panel = Send(Send(Class("NSPanel"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
            new NSRect(0, 0, TimerWidth, Height), NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel, NSBackingStoreBuffered, false);
        Send(_panel, Sel("setReleasedWhenClosed:"), false);
        Send(_panel, Sel("setHidesOnDeactivate:"), false);
        Send(_panel, Sel("setBecomesKeyOnlyIfNeeded:"), true);
        Send(_panel, Sel("setLevel:"), NSStatusWindowLevel);
        Send(_panel, Sel("setIgnoresMouseEvents:"), true);
        Send(_panel, Sel("setOpaque:"), false);
        Send(_panel, Sel("setHasShadow:"), true);
        Send(_panel, Sel("setBackgroundColor:"), Send(Class("NSColor"), Sel("clearColor")));
        Send(_panel, Sel("setCollectionBehavior:"), CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);
        // Dark controls on the dark pill, whatever the system appearance
        Send(_panel, Sel("setAppearance:"), Send(Class("NSAppearance"), Sel("appearanceNamed:"), Str("NSAppearanceNameDarkAqua")));

        // Rounded dark background drawn by the content view's layer
        var content = Send(_panel, Sel("contentView"));
        Send(content, Sel("setWantsLayer:"), true);
        var layer = Send(content, Sel("layer"));
        _background = MacNative.CGColorCreateGenericGray(0.125, 0.9);
        Send(layer, Sel("setBackgroundColor:"), _background);
        Send(layer, Sel("setCornerRadius:"), 10.0);

        _label = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(_label, Sel("setFont:"), Send(Class("NSFont"), Sel("monospacedDigitSystemFontOfSize:weight:"), 13.0, 0.3)); // semibold
        Send(_label, Sel("setLineBreakMode:"), 4L); // truncate tail
        Send(content, Sel("addSubview:"), _label);

        _button = Retain(Send(Class("NSButton"), Sel("buttonWithTitle:target:action:"),
            Str("Text"), ActionTarget.Instance, ActionTarget.ActionSelector));
        Send(_button, Sel("setTag:"), ActionTarget.Register(showText));
        Send(_button, Sel("setControlSize:"), 1L); // NSControlSizeSmall
        Send(content, Sel("addSubview:"), _button);

        _timer = new System.Threading.Timer(_ => _ui.Post(_ => Tick(), null), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void ShowRecording(long startTimestamp) => ShowCounter(Mode.Recording, startTimestamp);

    public void ShowProcessing(long startTimestamp) => ShowCounter(Mode.Processing, startTimestamp);

    /// <summary>A summary line for <paramref name="duration"/> (longer while hovered); the Text button if there is text.</summary>
    public void ShowResult(string summary, (double R, double G, double B) color, bool hasText, TimeSpan duration)
    {
        _mode = Mode.Result;
        _since = Stopwatch.GetTimestamp();
        _resultFor = duration;
        Layout(ResultWidth, hasText);
        SetLabel(summary, color);
        Send(_panel, Sel("setIgnoresMouseEvents:"), false);
        Present(ResultWidth);
        _timer.Change(200, 200);
    }

    public void Hide()
    {
        _mode = Mode.Hidden;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
    }

    private void ShowCounter(Mode mode, long startTimestamp)
    {
        _mode = mode;
        _since = startTimestamp;
        Layout(TimerWidth, button: false);
        Send(_panel, Sel("setIgnoresMouseEvents:"), true);
        Tick();
        Present(TimerWidth);
        _timer.Change(100, 100);
    }

    private void Tick()
    {
        var elapsed = Stopwatch.GetElapsedTime(_since);
        switch (_mode)
        {
            case Mode.Recording:
                SetLabel($"● Recording  {elapsed.TotalSeconds:F1} s", Red);
                break;
            case Mode.Processing:
                SetLabel($"… Processing  {elapsed.TotalSeconds:F1} s", Orange);
                break;
            case Mode.Result when elapsed >= _resultFor && !MouseInside():
                Hide();
                break;
        }
    }

    private bool MouseInside() =>
        SendRect(_panel, Sel("frame")).Contains(SendPoint(Class("NSEvent"), Sel("mouseLocation")));

    private void Layout(double width, bool button)
    {
        var labelWidth = width - 24 - (button ? ButtonWidth + 8 : 0);
        Send(_label, Sel("setFrame:"), new NSRect(12, (Height - 18) / 2, labelWidth, 18));
        Send(_label, Sel("setAlignment:"), button ? 0L : TextAlignmentCenter);
        Send(_button, Sel("setHidden:"), !button);
        Send(_button, Sel("setFrame:"), new NSRect(width - ButtonWidth - 10, (Height - 24) / 2, ButtonWidth, 24));
    }

    private void SetLabel(string text, (double R, double G, double B) color)
    {
        Send(_label, Sel("setStringValue:"), Str(text));
        Send(_label, Sel("setTextColor:"), Send(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), color.R, color.G, color.B, 1.0));
    }

    /// <summary>Resizes and shows the panel at the bottom centre of the screen with the mouse pointer.</summary>
    private void Present(double width)
    {
        var area = ScreenUnderMouse();
        Send(_panel, Sel("setFrame:display:"), new NSRect(area.X + (area.Width - width) / 2, area.Y + 40, width, Height), true);
        Send(_panel, Sel("orderFrontRegardless"));
    }

    /// <summary>Visible frame (without menu bar and Dock) of the screen with the mouse pointer.</summary>
    private static NSRect ScreenUnderMouse()
    {
        var mouse = SendPoint(Class("NSEvent"), Sel("mouseLocation"));
        IntPtr screen = IntPtr.Zero;
        foreach (var s in Items(Send(Class("NSScreen"), Sel("screens"))))
        {
            if (SendRect(s, Sel("frame")).Contains(mouse))
            {
                screen = s;
                break;
            }
        }
        if (screen == IntPtr.Zero)
            screen = Send(Class("NSScreen"), Sel("mainScreen"));
        return SendRect(screen, Sel("visibleFrame"));
    }

    public void Dispose()
    {
        _timer.Dispose();
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        Release(_button);
        Release(_label);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
