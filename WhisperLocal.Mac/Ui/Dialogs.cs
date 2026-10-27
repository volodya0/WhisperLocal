using System.Diagnostics;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

internal static class Dialogs
{
    public enum Style { Warning = 0, Informational = 1, Critical = 2 }

    /// <summary>Modal alert brought to the front (the app has no Dock icon and is normally never active).</summary>
    public static void Show(string title, string message, Style style = Style.Warning) => WithPool(() =>
    {
        var alert = New("NSAlert");
        Send(alert, Sel("setMessageText:"), Str(title));
        Send(alert, Sel("setInformativeText:"), Str(message));
        Send(alert, Sel("setAlertStyle:"), (long)style);
        Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("activateIgnoringOtherApps:"), true);
        Send(alert, Sel("runModal"));
        Release(alert);
    });

    /// <summary>
    /// Banner notification (the tray balloon of the Windows version). Posted through osascript, which works
    /// without registering for UNUserNotificationCenter; the notification shows Script Editor as its sender.
    /// </summary>
    public static void Notify(string message)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"display notification {AppleScriptString(message)} with title \"WhisperLocal\"");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot show a notification: {ex.Message}");
        }
    }

    /// <summary>Opens a file or folder with its default app; <paramref name="textEditor"/> forces the default text editor.</summary>
    public static void Open(string path, bool textEditor = false)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            if (textEditor)
                psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Cannot open {path}", ex);
        }
    }

    private static string AppleScriptString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
