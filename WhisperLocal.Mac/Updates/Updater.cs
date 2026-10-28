using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WhisperLocal.Config;

namespace WhisperLocal.Mac.Updates;

/// <summary>
/// Manual updates from the GitHub releases of the repository the app was built from: check, and on request
/// download the DMG, verify it, replace the .app and restart. Nothing runs unless the user asks.
/// </summary>
internal static class Updater
{
    public sealed record Release(Version Version, string Tag, string DmgUrl, string? Sha256, string Notes);

    /// <summary>"owner/repo", baked in at build time (scripts/build-mac.sh, from CI or the git remote).</summary>
    public static string? Repository { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } repo ? repo : null;

    public static Version CurrentVersion { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

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
    public static async Task<Release?> GetLatestAsync(CancellationToken ct)
    {
        if (Repository == null)
            throw new InvalidOperationException("This build does not know where to look for updates.");

        using var http = CreateClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // no releases yet
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith($"-{Arch}.dmg", StringComparison.OrdinalIgnoreCase))
                continue;
            // GitHub publishes "sha256:<hex>" digests for release assets
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha256 = digest != null && digest.StartsWith("sha256:") ? digest["sha256:".Length..] : null;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new Release(Normalize(version), tag, asset.GetProperty("browser_download_url").GetString()!, sha256, notes);
        }
        return null;
    }

    public static bool IsNewer(Release release) => release.Version > Normalize(CurrentVersion);

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
            using (var http = CreateClient())
            await using (var source = await http.GetStreamAsync(release.DmgUrl, ct))
            await using (var file = File.Create(dmg))
                await source.CopyToAsync(file, ct);

            if (release.Sha256 != null)
            {
                status("Verifying the download…");
                await using var stream = File.OpenRead(dmg);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                if (actual != release.Sha256.ToLowerInvariant())
                    throw new InvalidDataException("The downloaded update is damaged (checksum mismatch). Try again later.");
            }

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

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WhisperLocal", CurrentVersionText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

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
