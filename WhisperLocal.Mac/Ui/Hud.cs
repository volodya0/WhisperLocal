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
    private const ulong NSWindowStyleMaskNonactivatingPanel = 1 << 7;
    private const ulong NSBackingStoreBuffered = 2;
    private const long NSStatusWindowLevel = 25;
    private const long CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6, FullScreenAuxiliary = 1 << 8;
    private const long Behavior = CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary;
    private const long NSWindowAnimationBehaviorNone = 2;
    private const ulong NSWindowOcclusionStateVisible = 1 << 1;

    /// <summary>A dark rounded panel; returns it and the background colour to release with it.</summary>
    public static (IntPtr Panel, IntPtr Background) CreatePanel(double width, double height, double alpha)
    {
        var panel = Send(Send(Class("NSPanel"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
            new NSRect(0, 0, width, height), NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel, NSBackingStoreBuffered, false);
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
