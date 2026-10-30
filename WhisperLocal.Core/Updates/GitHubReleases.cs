using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WhisperLocal.Config;

namespace WhisperLocal.Updates;

/// <summary>A published release with the download for this platform.</summary>
public sealed record Release(Version Version, string Tag, string AssetUrl, string? Sha256, string Notes);

/// <summary>
/// The GitHub releases of the repository the app was built from: the newest one, and its download verified against
/// the checksum GitHub publishes. Shared by the macOS and Windows updaters, which differ only in how they install.
/// </summary>
public static class GitHubReleases
{
    /// <summary>"owner/repo", baked in at build time (-p:UpdateRepository, from CI or the git remote).</summary>
    public static string? Repository { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } repo ? repo : null;

    public static Version CurrentVersion { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>The newest release with an asset whose name ends with <paramref name="assetSuffix"/>, or null if none is published.</summary>
    public static async Task<Release?> GetLatestAsync(string assetSuffix, CancellationToken ct)
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
            if (!name.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Worth installing: a newer version of this edition, or the other edition (Full ↔ Lite) in any version.</summary>
    public static bool IsWorthInstalling(Release release, bool lite) => lite != AppEdition.IsLite || IsNewer(release);

    /// <summary>The question asked before installing: an update of this edition, or a switch to the other one.</summary>
    public static (string Title, string Message) Describe(Release release, bool lite)
    {
        var version = release.Version.ToString(3);
        if (lite == AppEdition.IsLite)
        {
            var notes = release.Notes.Length > 600 ? release.Notes[..600] + "…" : release.Notes;
            return ($"WhisperLocal {version} is available", $"You have {CurrentVersionText}.\n\n{notes}".TrimEnd());
        }
        var speakers = OperatingSystem.IsMacOS() ? ", and telling speakers apart while listening" : "";
        return lite
            ? ($"Switch to WhisperLocal Lite {version}?",
                "Lite is a much smaller app without local models: speech is recognised and translated only with Gemini or Groq, " +
                $"so it needs a key (Models page); the grammar check uses the cloud too. " +
                $"{(OperatingSystem.IsMacOS() ? "Autocomplete and telling speakers apart are" : "Autocomplete is")} not in Lite. Downloaded models " +
                "stay in the data folder for going back to Full; delete them there to free the space.")
            : ($"Switch to WhisperLocal Full {version}?",
                $"Full adds recognition on this computer (whisper.cpp), autocomplete and the grammar check with a local language model{speakers}. " +
                "The models are downloaded on the Models page; your settings and keys stay.");
    }

    /// <summary>Downloads the release's asset to <paramref name="path"/> and checks it against the published checksum.</summary>
    public static async Task DownloadAsync(Release release, string path, CancellationToken ct)
    {
        using (var http = CreateClient())
        await using (var source = await http.GetStreamAsync(release.AssetUrl, ct))
        await using (var file = File.Create(path))
            await source.CopyToAsync(file, ct);

        if (release.Sha256 == null)
            return;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (actual != release.Sha256.ToLowerInvariant())
            throw new InvalidDataException("The downloaded update is damaged (checksum mismatch). Try again later.");
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WhisperLocal", CurrentVersionText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
