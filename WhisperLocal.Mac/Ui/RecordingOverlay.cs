using System.Runtime.InteropServices;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>Small click-through indicator at the bottom of the screen; a non-activating panel that never takes focus.</summary>
internal sealed class RecordingOverlay : IDisposable
{
    private const double Width = 180;
    private const double Height = 36;

    private const ulong NSWindowStyleMaskBorderless = 0;
    private const ulong NSWindowStyleMaskNonactivatingPanel = 1 << 7;
    private const ulong NSBackingStoreBuffered = 2;
    private const long NSStatusWindowLevel = 25;
    private const long CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6, FullScreenAuxiliary = 1 << 8;

    // NSTextAlignmentCenter is 1 with the iOS-compatible ABI (arm64) and 2 on x86_64
    private static readonly long TextAlignmentCenter = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 2 : 1;

    private readonly IntPtr _panel;
    private readonly IntPtr _label;
    private readonly IntPtr _background;
    private bool _visible;

    public RecordingOverlay()
    {
        _panel = Send(Send(Class("NSPanel"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
            new NSRect(0, 0, Width, Height), NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel, NSBackingStoreBuffered, false);
        Send(_panel, Sel("setReleasedWhenClosed:"), false);
        Send(_panel, Sel("setHidesOnDeactivate:"), false);
        Send(_panel, Sel("setLevel:"), NSStatusWindowLevel);
        Send(_panel, Sel("setIgnoresMouseEvents:"), true);
        Send(_panel, Sel("setOpaque:"), false);
        Send(_panel, Sel("setHasShadow:"), true);
        Send(_panel, Sel("setBackgroundColor:"), Send(Class("NSColor"), Sel("clearColor")));
        Send(_panel, Sel("setCollectionBehavior:"), CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);

        // Rounded dark background drawn by the content view's layer
        var content = Send(_panel, Sel("contentView"));
        Send(content, Sel("setWantsLayer:"), true);
        var layer = Send(content, Sel("layer"));
        _background = MacNative.CGColorCreateGenericGray(0.125, 0.88);
        Send(layer, Sel("setBackgroundColor:"), _background);
        Send(layer, Sel("setCornerRadius:"), 8.0);

        _label = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(_label, Sel("setFrame:"), new NSRect(0, (Height - 18) / 2, Width, 18));
        Send(_label, Sel("setAlignment:"), TextAlignmentCenter);
        Send(_label, Sel("setFont:"), Send(Class("NSFont"), Sel("boldSystemFontOfSize:"), 13.0));
        Send(content, Sel("addSubview:"), _label);
    }

    public void ShowState(string text, (double R, double G, double B) color)
    {
        Send(_label, Sel("setStringValue:"), Str(text));
        Send(_label, Sel("setTextColor:"), Send(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), color.R, color.G, color.B, 1.0));

        var area = ScreenUnderMouse();
        Send(_panel, Sel("setFrameOrigin:"), new NSPoint(area.X + (area.Width - Width) / 2, area.Y + 40));
        if (!_visible)
        {
            Send(_panel, Sel("orderFrontRegardless"));
            _visible = true;
        }
    }

    public void Hide()
    {
        if (!_visible)
            return;
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        _visible = false;
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
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        Release(_label);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
