using System.Diagnostics;
using System.Runtime.InteropServices;
using WhisperLocal.Config;
using WhisperLocal.Updates;

namespace WhisperLocal.Mac.Updates;

/// <summary>
/// Manual updates from the GitHub releases of the repository the app was built from (<see cref="GitHubReleases"/>):
/// check, and on request download the DMG, verify it, replace the .app and restart. Nothing runs unless the user asks.
/// </summary>
internal static class Updater
{
    /// <summary>"owner/repo", baked in at build time (scripts/build-mac.sh, from CI or the git remote).</summary>
    public static string? Repository => GitHubReleases.Repository;

    public static string CurrentVersionText => GitHubReleases.CurrentVersionText;

    /// <summary>The running .app bundle; null when started with dotnet run or outside a bundle.</summary>
    public static string? BundlePath
    {
        get
        {
            var macOs = AppPaths.AppDir;
            var contents = Path.GetDirectoryName(macOs);
            var bundle = contents == null ? null : Path.GetDirectoryName(contents);
            return bundle != null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
        }
    }

    private static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>The newest release with a DMG for this Mac's architecture, or null if none is published.</summary>
    public static Task<Release?> GetLatestAsync(CancellationToken ct) => GitHubReleases.GetLatestAsync($"-{Arch}.dmg", ct);

    public static bool IsNewer(Release release) => GitHubReleases.IsNewer(release);

    /// <summary>
    /// Downloads and installs <paramref name="release"/> over the running bundle, then starts a helper that
    /// reopens the app once this process has exited. The caller quits the app right after.
    /// </summary>
    public static async Task InstallAsync(Release release, Action<string> status, CancellationToken ct)
    {
        var bundle = BundlePath ?? throw new InvalidOperationException("Updates work only for the installed WhisperLocal.app.");
        if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal) || bundle.StartsWith("/Volumes/", StringComparison.Ordinal))
            throw new InvalidOperationException("Move WhisperLocal to the Applications folder first, start it from there and try again.");

        var work = Path.Combine(Path.GetTempPath(), $"whisperlocal-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var mount = Path.Combine(work, "mnt");
        var staged = Path.Combine(Path.GetDirectoryName(bundle)!, ".WhisperLocal-update.app");
        var old = Path.Combine(Path.GetDirectoryName(bundle)!, ".WhisperLocal-old.app");
        var mounted = false;
        try
        {
            status($"Downloading WhisperLocal {release.Version.ToString(3)}…");
            var dmg = Path.Combine(work, "update.dmg");
            await GitHubReleases.DownloadAsync(release, dmg, ct);

            status("Installing…");
            Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmg);
            mounted = true;
            var newApp = Directory.GetDirectories(mount, "*.app").FirstOrDefault()
                         ?? throw new InvalidDataException("The update does not contain an app.");

            DeleteIfExists(staged);
            Run("/usr/bin/ditto", newApp, staged);
            Run("/usr/bin/hdiutil", "detach", mount, "-quiet");
            mounted = false;

            VerifySignature(bundle, staged);

            // Swap the bundles; the running process keeps its open files, the new app starts after it exits
            DeleteIfExists(old);
            Directory.Move(bundle, old);
            try
            {
                Directory.Move(staged, bundle);
            }
            catch
            {
                Directory.Move(old, bundle);
                throw;
            }
            Log.Info($"Updated to {release.Tag}, restarting");

            var script = $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.2; done; " +
                         $"rm -rf {Quote(old)} {Quote(work)}; /usr/bin/open {Quote(bundle)}";
            Process.Start(new ProcessStartInfo("/bin/sh", ["-c", script]) { UseShellExecute = false })?.Dispose();
        }
        catch
        {
            if (mounted)
                TryRun("/usr/bin/hdiutil", "detach", mount, "-force", "-quiet");
            DeleteIfExists(staged);
            DeleteIfExists(work);
            throw;
        }
    }

    /// <summary>
    /// The new app must have a valid signature and, unless this build is signed ad hoc, satisfy this build's
    /// designated requirement: only an app signed with the same certificate can replace it.
    /// </summary>
    private static void VerifySignature(string current, string candidate)
    {
        Run("/usr/bin/codesign", "--verify", candidate);

        var (_, output, error) = TryRun("/usr/bin/codesign", "-d", "-r-", current);
        var designated = (output + "\n" + error).Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("designated => ", StringComparison.Ordinal))?["designated => ".Length..];
        // Ad-hoc signatures pin the exact binary (cdhash), which no other build can match
        if (designated == null || designated.StartsWith("cdhash", StringComparison.Ordinal))
        {
            Log.Warn("This build is signed ad hoc; the update's signer cannot be compared");
            return;
        }
        var (exit, _, verifyError) = TryRun("/usr/bin/codesign", "--verify", $"-R={designated}", candidate);
        if (exit != 0)
            throw new InvalidDataException($"The update is not signed by the same certificate as this app. {verifyError.Trim()}");
    }

    private static void Run(string file, params string[] args)
    {
        var (exit, output, error) = TryRun(file, args);
        if (exit != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} failed ({exit}): {(error + output).Trim()}");
    }

    private static (int Exit, string Output, string Error) TryRun(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Result, error);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot delete {path}: {ex.Message}");
        }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
