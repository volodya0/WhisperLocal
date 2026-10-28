using System.Runtime.InteropServices;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Output;

/// <summary>
/// Whether the frontmost app has a text input focused, asked through the Accessibility API (the permission the hotkey
/// already needs). Used to show the transcript instead of pasting it into nothing.
/// </summary>
internal static class FocusedInput
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrorSuccess = 0, ErrorNoValue = -25212;

    [DllImport(ApplicationServices)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element, float seconds);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    private static readonly HashSet<string> TextRoles = ["AXTextField", "AXTextArea", "AXComboBox"];

    // Focused elements that certainly do not take typed text (a file list in Finder, a button, the desktop)
    private static readonly HashSet<string> NonTextRoles =
    [
        "AXList", "AXOutline", "AXTable", "AXBrowser", "AXRow", "AXCell", "AXButton", "AXCheckBox", "AXRadioButton",
        "AXPopUpButton", "AXMenuButton", "AXImage", "AXStaticText", "AXWindow", "AXApplication", "AXMenu", "AXMenuBar",
        "AXToolbar", "AXSlider", "AXTabGroup",
    ];

    /// <summary>
    /// true: a text input is focused; false: certainly none (no focused element, or a list, button…);
    /// null: cannot tell (no permission, the app does not answer, or a container such as a web area) — paste as usual.
    /// </summary>
    public static bool? AcceptsText(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId)
            return null;
        try
        {
            return WithPool(() =>
            {
                var app = AXUIElementCreateApplication(pid);
                if (app == IntPtr.Zero)
                    return (bool?)null;
                try
                {
                    AXUIElementSetMessagingTimeout(app, 0.3f);
                    // Chromium and Electron apps build their accessibility tree only when asked
                    AXUIElementSetAttributeValue(app, Str("AXManualAccessibility"), Send(Class("NSNumber"), Sel("numberWithBool:"), true));

                    var error = AXUIElementCopyAttributeValue(app, Str("AXFocusedUIElement"), out var focused);
                    if (error == ErrorNoValue)
                        return false;
                    if (error != ErrorSuccess || focused == IntPtr.Zero)
                        return null;
                    try
                    {
                        var role = StringAttribute(focused, "AXRole") ?? "";
                        if (TextRoles.Contains(role) || HasAttribute(focused, "AXSelectedTextRange"))
                            return true;
                        return NonTextRoles.Contains(role) ? false : null;
                    }
                    finally
                    {
                        CFRelease(focused);
                    }
                }
                finally
                {
                    CFRelease(app);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Debug($"Focused element check failed: {ex.Message}");
            return null;
        }
    }

    private static string? StringAttribute(IntPtr element, string name)
    {
        if (AXUIElementCopyAttributeValue(element, Str(name), out var value) != ErrorSuccess || value == IntPtr.Zero)
            return null;
        try
        {
            return FromStr(value);
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static bool HasAttribute(IntPtr element, string name)
    {
        if (AXUIElementCopyAttributeValue(element, Str(name), out var value) != ErrorSuccess || value == IntPtr.Zero)
            return false;
        CFRelease(value);
        return true;
    }
}
