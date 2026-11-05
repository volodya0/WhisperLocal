using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Output;

/// <summary>Snapshot of the frontmost application (NSWorkspace.frontmostApplication).</summary>
internal sealed record FrontmostApp(int ProcessId, string ProcessName, string BundleId)
{
    /// <summary>Must run on the main thread.</summary>
    public static FrontmostApp Capture() => WithPool(() =>
    {
        var workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
        var app = Send(workspace, Sel("frontmostApplication"));
        if (app == IntPtr.Zero)
            return new FrontmostApp(0, "", "");
        // The localized name ("Code", "Slack", "Telegram") is what Llm.AppStyles keys on
        return new FrontmostApp(
            SendInt(app, Sel("processIdentifier")),
            FromStr(Send(app, Sel("localizedName"))) ?? "",
            FromStr(Send(app, Sel("bundleIdentifier"))) ?? "");
    });

    /// <summary>Brings this app to the front (it then focuses the window and field it had); false if it is gone.</summary>
    public bool Activate() => WithPool(() =>
    {
        var app = Send(Class("NSRunningApplication"), Sel("runningApplicationWithProcessIdentifier:"), (long)ProcessId);
        if (app == IntPtr.Zero)
            return false;
        // NSApplicationActivateIgnoringOtherApps
        return SendBool(app, Sel("activateWithOptions:"), (IntPtr)2);
    });
}
