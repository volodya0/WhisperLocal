using System.Diagnostics;
using System.Runtime.InteropServices;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// Small indicator at the bottom of the screen; a non-activating panel that never takes focus.
/// Recording and processing show a live seconds counter; afterwards a short summary (with a button that opens
/// the transcript) stays for a moment, longer while the mouse is over it. Click-through except in that state.
/// Each dictation gets a freshly created panel: after the app had run for hours, macOS sometimes stopped showing the
/// long-lived one anywhere (not even on the normal desktop) until the app was restarted.
/// </summary>
internal sealed class RecordingOverlay : IDisposable
{
    private const double Height = 36;
    private const double TimerWidth = 210;
    private const double ResultWidth = 360;
    private const double ButtonWidth = 64;

    // NSTextAlignmentLeft is 0 everywhere; Center is 1 with the iOS-compatible ABI (arm64) and 2 on x86_64
    private static readonly long TextAlignmentCenter = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 2 : 1;

    private static readonly (double R, double G, double B) Red = (1.0, 0.35, 0.35);
    private static readonly (double R, double G, double B) Orange = (1.0, 0.7, 0.24);

    private enum Mode { Hidden, Recording, Processing, Result }

    private readonly SynchronizationContext _ui;
    private readonly long _showTextTag;
    private readonly System.Threading.Timer _timer;
    private IntPtr _panel;
    private IntPtr _label;
    private IntPtr _button;
    private IntPtr _background;

    private Mode _mode = Mode.Hidden;
    private long _since;
    private TimeSpan _resultFor;

    public RecordingOverlay(SynchronizationContext ui, Action showText)
    {
        _ui = ui;
        _showTextTag = ActionTarget.Register(showText);
        _timer = new System.Threading.Timer(_ => _ui.Post(_ => Tick(), null), null, Timeout.Infinite, Timeout.Infinite);
        CreatePanel();
    }

    private void CreatePanel()
    {
        (_panel, _background) = Hud.CreatePanel(TimerWidth, Height, 0.9);
        var content = Send(_panel, Sel("contentView"));

        _label = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(_label, Sel("setFont:"), Send(Class("NSFont"), Sel("monospacedDigitSystemFontOfSize:weight:"), 13.0, 0.3)); // semibold
        Send(_label, Sel("setLineBreakMode:"), 4L); // truncate tail
        Send(content, Sel("addSubview:"), _label);

        _button = Retain(Send(Class("NSButton"), Sel("buttonWithTitle:target:action:"),
            Str("Text"), ActionTarget.Instance, ActionTarget.ActionSelector));
        Send(_button, Sel("setTag:"), _showTextTag);
        Send(_button, Sel("setControlSize:"), 1L); // NSControlSizeSmall
        Send(content, Sel("addSubview:"), _button);
    }

    private void ReleasePanel()
    {
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        Release(_button);
        Release(_label);
        // Autoreleased: a Text button click or a timer tick may still be using it in this event
        Send(_panel, Sel("autorelease"));
        MacNative.CGColorRelease(_background);
    }

    public void ShowRecording(long startTimestamp)
    {
        // A new panel for every dictation; one still showing the last summary is simply reused
        if (_mode == Mode.Hidden)
        {
            ReleasePanel();
            CreatePanel();
        }
        ShowCounter(Mode.Recording, startTimestamp);
    }

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

    /// <summary>Resizes and shows the panel at the bottom centre of the screen you are working on.</summary>
    private void Present(double width)
    {
        var area = Hud.ActiveScreen();
        Hud.Show(_panel, new NSRect(area.X + (area.Width - width) / 2, area.Y + 40, width, Height), "Recording indicator");
    }

    public void Dispose()
    {
        _timer.Dispose();
        ReleasePanel();
    }
}
