using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Config;
using WhisperLocal.Text;

namespace WhisperLocal.Llm;

/// <summary>Grammar check: the local LLM rewrites a finished dictation as correct, natural text in the same language.</summary>
public sealed class Polisher
{
    private readonly ConfigManager _config;
    private readonly LlamaServer _server;

    public Polisher(ConfigManager config, LlamaServer server)
    {
        _config = config;
        _server = server;
    }

    /// <summary>The polished text, or null if the model found nothing to improve (or its answer was unusable).</summary>
    public async Task<string?> PolishAsync(string text, CancellationToken ct)
    {
        var c = _config.Current.Polish;
        if (TextFilters.CountWords(text) < c.MinWords)
            return null;

        var schema = new
        {
            type = "object",
            properties = new { text = new { type = "string" } },
            required = new[] { "text" },
        };

        // A cold start (loading the model) does not count towards the timeout
        await _server.WarmUpAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(c.TimeoutMs);

        var sw = Stopwatch.StartNew();
        var answer = await _server.ChatAsync([("system", c.SystemPrompt), ("user", text)], c.Temperature,
            text.Length + 200, schema, cts.Token);
        var polished = Parse(answer, text);
        Log.Info($"Grammar check in {sw.ElapsedMilliseconds} ms: {(polished == null ? "no changes" : "proposed")}");
        if (polished != null && _config.Current.Logging.LogText)
            Log.Info($"  polished: {polished}");
        return polished;
    }

    internal static string? Parse(string answer, string original)
    {
        string polished;
        try
        {
            using var doc = JsonDocument.Parse(answer);
            polished = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            Log.Warn($"Grammar check answer is not valid JSON ({ex.Message}): {answer}");
            return null;
        }

        polished = Regex.Replace(polished, @"[ \t]+", " ").Trim();
        if (polished.Length == 0 || Normalize(polished) == Normalize(original))
            return null;

        // A much shorter or longer answer means the model answered the text or dropped part of it
        var ratio = (double)polished.Length / Math.Max(1, original.Length);
        if (ratio < 0.5 || ratio > 1.8)
        {
            Log.Warn($"Grammar check rejected (length ratio {ratio:F2}): {polished}");
            return null;
        }
        return polished;
    }

    /// <summary>Differences that are not worth a proposal: spacing, a final period, the case of the first letter.</summary>
    private static string Normalize(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim().TrimEnd('.');
        return text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }
}
