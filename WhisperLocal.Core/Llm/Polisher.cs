using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Cloud;
using WhisperLocal.Config;
using WhisperLocal.Stt;
using WhisperLocal.Text;

namespace WhisperLocal.Llm;

/// <summary>
/// Grammar check: a language model rewrites a finished dictation as correct, natural text in the same language; the
/// local one, or Gemini or Groq (Polish.Engine) with the user's key.
/// </summary>
public sealed class Polisher
{
    private readonly ConfigManager _config;
    private readonly LlamaServer _server;

    public Polisher(ConfigManager config, LlamaServer server)
    {
        _config = config;
        _server = server;
    }

    /// <summary>
    /// "Gemini" or "Groq" when that cloud engine is chosen and has a key (in Lite, which has no local model: any key),
    /// otherwise null (the local model checks).
    /// </summary>
    public static string? CloudName(AppConfig c)
    {
        var engine = c.Polish.Engine;
        if (string.Equals(engine, "Gemini", StringComparison.OrdinalIgnoreCase) && c.Cloud.GeminiApiKey.Trim().Length > 0)
            return "Gemini";
        if (string.Equals(engine, "Groq", StringComparison.OrdinalIgnoreCase) && c.Cloud.GroqApiKey.Trim().Length > 0)
            return "Groq";
        return AppEdition.IsLite ? RecognitionRouter.AnyCloud(c) : null;
    }

    /// <summary>What checks, for the status: like <see cref="Translator.Description"/>.</summary>
    public static string Description(AppConfig c) => CloudName(c) switch
    {
        "Groq" => $"{c.Cloud.GroqTextModel} · Groq cloud",
        "Gemini" => $"{c.Cloud.GeminiTextModel} · Google cloud",
        _ => $"{LlmModels.Find(c.LocalLlm.ModelPath)?.Title ?? Path.GetFileName(c.LocalLlm.ModelPath)} · on this computer",
    };

    /// <summary>The polished text, or null if the model found nothing to improve (or its answer was unusable).</summary>
    public async Task<string?> PolishAsync(string text, CancellationToken ct)
    {
        var cfg = _config.Current;
        var c = cfg.Polish;
        if (TextFilters.CountWords(text) < c.MinWords)
            return null;

        var schema = new
        {
            type = "object",
            properties = new { text = new { type = "string" } },
            required = new[] { "text" },
        };

        var cloud = CloudName(cfg);
        var timeout = TimeSpan.FromMilliseconds(c.TimeoutMs);
        var sw = Stopwatch.StartNew();
        string answer;
        if (cloud == "Gemini")
            answer = await GeminiClient.ChatJsonAsync(cfg.Cloud.GeminiApiKey.Trim(), cfg.Cloud.GeminiTextModel, c.SystemPrompt, text, c.Temperature, timeout, ct);
        else if (cloud == "Groq")
            answer = await GroqClient.ChatJsonAsync(cfg.Cloud.GroqApiKey.Trim(), cfg.Cloud.GroqTextModel, c.SystemPrompt, text, c.Temperature, timeout, ct);
        else
        {
            // A cold start (loading the model) does not count towards the timeout
            await _server.WarmUpAsync(ct);
            sw.Restart();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            answer = await _server.ChatAsync([("system", c.SystemPrompt), ("user", text)], c.Temperature,
                text.Length + 200, schema, cts.Token);
        }
        var polished = Parse(answer, text);
        Log.Info($"Grammar check in {sw.ElapsedMilliseconds} ms{(cloud != null ? $" ({cloud})" : "")}: {(polished == null ? "no changes" : "proposed")}");
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
