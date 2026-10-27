using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace WhisperLocal.Stt;

/// <summary>
/// Downloads a ggml Whisper model from HuggingFace (resuming a partial download) and verifies known hashes,
/// like scripts/get-model.ps1 and get-model.sh.
/// </summary>
public static class ModelDownloader
{
    // Known-good hashes; models without an entry are not verified
    private static readonly Dictionary<string, string> KnownHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml-large-v3.bin"] = "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
    };

    public static string UrlFor(string modelPath) => ModelNotFoundException.DownloadUrlBase + Path.GetFileName(modelPath);

    /// <param name="progress">Bytes downloaded and total size (null if unknown); reported a few times per second.</param>
    /// <param name="verifying">Raised before the hash check, which takes a few seconds for a 3 GB file.</param>
    public static async Task DownloadAsync(string modelPath, IProgress<(long Done, long? Total)> progress, Action verifying, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        var partial = modelPath + ".partial";
        var url = UrlFor(modelPath);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        Log.Info($"Downloading {url} to {partial}" + (existing > 0 ? $", resuming at {existing} bytes" : ""));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // 416: the partial file is already complete (e.g. the app quit during the hash check); just verify it
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            progress.Report((existing, existing));
            await VerifyAndInstallAsync(partial, modelPath, verifying, ct);
            return;
        }
        response.EnsureSuccessStatusCode();

        // A server that ignores the range sends the whole file again
        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed)
            existing = 0;
        var total = response.Content.Headers.ContentLength is { } length ? length + existing : (long?)null;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            var buffer = new byte[1 << 20];
            var done = existing;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (lastReport.ElapsedMilliseconds >= 250)
                {
                    progress.Report((done, total));
                    lastReport.Restart();
                }
            }
            progress.Report((done, total));
        }

        await VerifyAndInstallAsync(partial, modelPath, verifying, ct);
    }

    private static async Task VerifyAndInstallAsync(string partial, string modelPath, Action verifying, CancellationToken ct)
    {
        if (KnownHashes.TryGetValue(Path.GetFileName(modelPath), out var expected))
        {
            verifying();
            var actual = await Task.Run(() =>
            {
                using var stream = File.OpenRead(partial);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }, ct);
            if (actual != expected)
            {
                File.Delete(partial);
                throw new InvalidDataException($"The downloaded model is damaged (SHA256 {actual}, expected {expected}); try again.");
            }
        }

        File.Move(partial, modelPath, overwrite: true);
        Log.Info($"Model downloaded: {modelPath}");
    }
}
