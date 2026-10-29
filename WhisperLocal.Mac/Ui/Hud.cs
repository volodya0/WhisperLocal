using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// The floating panels (recording indicator, autocomplete, grammar check): non-activating, never take focus, on every desktop and
/// over full-screen apps. That last part works only while the app is an accessory (menu bar) app, so the app never
/// switches to a regular Dock app.
/// </summary>
internal static class Hud
{
    private const ulong NSWindowStyleMaskBorderless = 0;
    private const ulong NSWindowStyleMaskTitled = 1 << 0;
    private const ulong NSWindowStyleMaskResizable = 1 << 3;
    private const ulong NSWindowStyleMaskNonactivatingPanel = 1 << 7;
    private const ulong NSWindowStyleMaskFullSizeContentView = 1 << 15;
    private const ulong NSBackingStoreBuffered = 2;
    private const long NSStatusWindowLevel = 25;
    private const long CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6, FullScreenAuxiliary = 1 << 8;
    private const long Behavior = CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary;
    private const long NSWindowAnimationBehaviorNone = 2;
    private const ulong NSWindowOcclusionStateVisible = 1 << 1;

    /// <summary>A dark rounded panel; returns it and the background colour to release with it.</summary>
    /// <param name="resizable">
    /// Resizable by its edges and corners: a titled panel whose (invisible) title bar the content covers, since macOS
    /// resizes only windows with a frame.
    /// </param>
    public static (IntPtr Panel, IntPtr Background) CreatePanel(double width, double height, double alpha, bool resizable = false)
    {
        var style = resizable
            ? NSWindowStyleMaskTitled | NSWindowStyleMaskResizable | NSWindowStyleMaskFullSizeContentView | NSWindowStyleMaskNonactivatingPanel
            : NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel;
        var panel = Send(Send(Class("NSPanel"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
            new NSRect(0, 0, width, height), style, NSBackingStoreBuffered, false);
        if (resizable)
        {
            Send(panel, Sel("setTitlebarAppearsTransparent:"), true);
            Send(panel, Sel("setTitleVisibility:"), 1L); // NSWindowTitleHidden
            for (long button = 0; button <= 2; button++) // close, minimize, zoom
                Send(Send(panel, Sel("standardWindowButton:"), button), Sel("setHidden:"), true);
            // The title bar's container lies over the top of the content and would take the clicks meant for controls
            // there; hidden, it takes none (resizing is done by the window frame, not the title bar)
            var titlebar = Send(Send(Send(panel, Sel("standardWindowButton:"), 0L), Sel("superview")), Sel("superview"));
            if (titlebar != IntPtr.Zero)
                Send(titlebar, Sel("setHidden:"), true);
        }
        Send(panel, Sel("setReleasedWhenClosed:"), false);
        Send(panel, Sel("setHidesOnDeactivate:"), false);
        Send(panel, Sel("setBecomesKeyOnlyIfNeeded:"), true);
        Send(panel, Sel("setLevel:"), NSStatusWindowLevel);
        Send(panel, Sel("setIgnoresMouseEvents:"), true);
        Send(panel, Sel("setOpaque:"), false);
        Send(panel, Sel("setHasShadow:"), true);
        Send(panel, Sel("setBackgroundColor:"), Send(Class("NSColor"), Sel("clearColor")));
        Send(panel, Sel("setCollectionBehavior:"), Behavior);
        Send(panel, Sel("setAnimationBehavior:"), NSWindowAnimationBehaviorNone);
        // Dark controls on the dark background, whatever the system appearance
        Send(panel, Sel("setAppearance:"), Send(Class("NSAppearance"), Sel("appearanceNamed:"), Str("NSAppearanceNameDarkAqua")));

        // Rounded dark background drawn by the content view's layer
        var content = Send(panel, Sel("contentView"));
        Send(content, Sel("setWantsLayer:"), true);
        var layer = Send(content, Sel("layer"));
        var background = MacNative.CGColorCreateGenericGray(0.125, alpha);
        Send(layer, Sel("setBackgroundColor:"), background);
        Send(layer, Sel("setCornerRadius:"), 10.0);
        return (panel, background);
    }

    private static IntPtr _flippedViewClass;

    /// <summary>A plain view whose y axis points down (top-down layout, as in a list), owned by the caller.</summary>
    public static unsafe IntPtr FlippedView(NSRect frame)
    {
        if (_flippedViewClass == IntPtr.Zero)
        {
            _flippedViewClass = objc_allocateClassPair(Class("NSView"), "WhisperLocalFlippedView", 0);
            class_addMethod(_flippedViewClass, Sel("isFlipped"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&IsFlipped, "c@:");
            objc_registerClassPair(_flippedViewClass);
        }
        return Send(Send(_flippedViewClass, Sel("alloc")), Sel("initWithFrame:"), frame);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static byte IsFlipped(IntPtr self, IntPtr sel) => 1;

    /// <summary>
    /// Visible frame (without menu bar and Dock) of the screen you are working on: the one with the focused window
    /// (NSScreen.mainScreen), which is also where a full-screen app is; the mouse may be elsewhere.
    /// </summary>
    public static NSRect ActiveScreen()
    {
        var screen = Send(Class("NSScreen"), Sel("mainScreen"));
        if (screen == IntPtr.Zero)
            screen = Items(Send(Class("NSScreen"), Sel("screens"))).FirstOrDefault();
        return SendRect(screen, Sel("visibleFrame"));
    }

    /// <summary>Moves the panel to <paramref name="frame"/> and shows it on the current desktop; logs if macOS keeps it hidden.</summary>
    public static void Show(IntPtr panel, NSRect frame, string name)
    {
        // Set again each time: cheap, and nothing else can have changed them
        Send(panel, Sel("setCollectionBehavior:"), Behavior);
        Send(panel, Sel("setLevel:"), NSStatusWindowLevel);
        Send(panel, Sel("setFrame:display:"), frame, true);
        Send(panel, Sel("orderFrontRegardless"));

        var visible = SendBool(panel, Sel("isVisible"));
        var onActiveSpace = SendBool(panel, Sel("isOnActiveSpace"));
        // Occlusion is updated asynchronously, so it only adds detail to a report
        var occlusion = (ulong)SendLong(panel, Sel("occlusionState"));
        if (!visible || !onActiveSpace)
            Log.Warn($"{name} is not on screen after ordering it front: visible={visible}, onActiveSpace={onActiveSpace}, " +
                     $"occluded={(occlusion & NSWindowOcclusionStateVisible) == 0}, window={SendLong(panel, Sel("windowNumber"))}, " +
                     $"frame=({frame.X:F0}, {frame.Y:F0}, {frame.Width:F0}×{frame.Height:F0}), activation policy={ActivationPolicy()}");
    }

    /// <summary>0 regular, 1 accessory, 2 prohibited.</summary>
    private static long ActivationPolicy() =>
        SendLong(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("activationPolicy"));
}
