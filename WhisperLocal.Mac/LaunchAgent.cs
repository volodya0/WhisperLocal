using System.Security;

namespace WhisperLocal.Mac;

/// <summary>Start at login via a per-user LaunchAgent (~/Library/LaunchAgents), the counterpart of the Windows Run key.</summary>
internal static class LaunchAgent
{
    private const string Label = "com.github.maksymkravchuk.whisperlocal";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    /// <summary>Creates, updates (app moved) or removes the LaunchAgent to match the setting.</summary>
    public static void Apply(bool enabled)
    {
        var path = PlistPath;
        var current = File.Exists(path) ? File.ReadAllText(path) : null;

        if (enabled)
        {
            var plist = BuildPlist(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unknown"));
            if (current != plist)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, plist);
                Log.Info($"Autostart enabled: {path}");
            }
        }
        else if (current != null)
        {
            File.Delete(path);
            Log.Info("Autostart disabled");
        }
    }

    private static string BuildPlist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executable)}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """;
}
