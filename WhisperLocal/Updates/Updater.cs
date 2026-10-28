using System.Diagnostics;
using System.IO.Compression;
using WhisperLocal.Config;

namespace WhisperLocal.Updates;

/// <summary>
/// Manual updates from the GitHub releases (<see cref="GitHubReleases"/>), the Windows counterpart of the macOS updater:
/// on request download the zip, verify it, and once the app has quit, copy the new files over the app folder and
/// start it again. The settings (config.json), models, logs and history next to the exe stay as they are.
/// </summary>
internal static class Updater
{
    private const string AssetSuffix = "-windows-x64.zip";

    public static string? Repository => GitHubReleases.Repository;

    public static string CurrentVersionText => GitHubReleases.CurrentVersionText;

    /// <summary>The newest release with a Windows zip, or null if none is published.</summary>
    public static Task<Release?> GetLatestAsync(CancellationToken ct) => GitHubReleases.GetLatestAsync(AssetSuffix, ct);

    public static bool IsNewer(Release release) => GitHubReleases.IsNewer(release);

    /// <summary>
    /// Downloads and unpacks <paramref name="release"/>, then starts a helper that copies it over the app folder once
    /// this process has exited and starts the new version. The caller quits the app right after.
    /// </summary>
    public static async Task InstallAsync(Release release, Action<string> status, CancellationToken ct)
    {
        var appDir = AppPaths.AppDir;
        var exe = Environment.ProcessPath ?? Path.Combine(appDir, "WhisperLocal.exe");
        EnsureWritable(appDir);

        var work = Path.Combine(Path.GetTempPath(), $"whisperlocal-update-{Guid.NewGuid():N}");
        var unpacked = Path.Combine(work, "app");
        Directory.CreateDirectory(work);
        try
        {
            status($"Downloading WhisperLocal {release.Version.ToString(3)}…");
            var zip = Path.Combine(work, "update.zip");
            await GitHubReleases.DownloadAsync(release, zip, ct);

            status("Installing…");
            ZipFile.ExtractToDirectory(zip, unpacked);
            if (!File.Exists(Path.Combine(unpacked, Path.GetFileName(exe))))
                throw new InvalidDataException("The update does not contain WhisperLocal.exe.");

            // config.json holds the user's settings; the defaults in the zip must not replace it
            var script = Path.Combine(Path.GetTempPath(), $"whisperlocal-update-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(script,
                $"""
                @echo off
                :wait
                tasklist /FI "PID eq {Environment.ProcessId}" 2>nul | find "{Environment.ProcessId}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
                robocopy "{unpacked}" "{appDir}" /E /XF config.json /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul
                start "" "{exe}"
                rmdir /s /q "{work}"
                (goto) 2>nul & del "%~f0"
                """);
            Log.Info($"Updating to {release.Tag}, restarting");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Cannot delete {work}: {ex.Message}");
            }
            throw;
        }
    }

    /// <summary>The update replaces files next to the exe; a folder like Program Files needs the user to update by hand.</summary>
    private static void EnsureWritable(string dir)
    {
        var probe = Path.Combine(dir, $".update-check-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"WhisperLocal cannot write to its folder ({dir}). Download the new version from the releases page and unpack it there yourself.");
        }
    }
}
