using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Cloud;
using WhisperLocal.Config;

namespace WhisperLocal.Llm;

/// <summary>
/// Live translation: a language model translates one segment of a transcript, with the text before it as context. The
/// model is the local one, or Gemini or Groq with the user's key (Listen.TranslateEngine).
/// </summary>
public sealed class Translator
{
    // English name for the prompt, own name for the menu
    private static readonly Dictionary<string, (string English, string Native)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = ("Ukrainian", "Українська"), ["en"] = ("English", "English"), ["de"] = ("German", "Deutsch"),
        ["pl"] = ("Polish", "Polski"), ["es"] = ("Spanish", "Español"), ["fr"] = ("French", "Français"),
        ["it"] = ("Italian", "Italiano"), ["pt"] = ("Portuguese", "Português"), ["cs"] = ("Czech", "Čeština"),
        ["nl"] = ("Dutch", "Nederlands"), ["sv"] = ("Swedish", "Svenska"), ["tr"] = ("Turkish", "Türkçe"),
        ["ja"] = ("Japanese", "日本語"), ["zh"] = ("Chinese", "中文"), ["ru"] = ("Russian", "Русский"),
    };

    private readonly ConfigManager _config;
    private readonly LlamaServer _server;

    public Translator(ConfigManager config, LlamaServer server)
    {
        _config = config;
        _server = server;
    }

    /// <summary>"Українська" for "uk"; the code itself for a language without a known name.</summary>
    public static string NativeName(string code) => Names.TryGetValue(code, out var n) ? n.Native : code;

    public static string EnglishName(string code) => Names.TryGetValue(code, out var n) ? n.English : code;

    /// <summary>"Gemini" or "Groq" when that cloud engine is chosen and has a key, otherwise null (the local model translates).</summary>
    public static string? CloudName(AppConfig c)
    {
        var engine = c.Listen.TranslateEngine;
        if (string.Equals(engine, "Gemini", StringComparison.OrdinalIgnoreCase) && c.Cloud.GeminiApiKey.Trim().Length > 0)
            return "Gemini";
        if (string.Equals(engine, "Groq", StringComparison.OrdinalIgnoreCase) && c.Cloud.GroqApiKey.Trim().Length > 0)
            return "Groq";
        return null;
    }

    /// <summary>The translation of <paramref name="text"/>, or null if the answer was unusable.</summary>
    /// <param name="context">The transcript just before the text, to understand it; not translated.</param>
    public async Task<string?> TranslateAsync(string text, string language, string context, CancellationToken ct)
    {
        var cfg = _config.Current;
        var c = cfg.Listen;
        var schema = new
        {
            type = "object",
            properties = new { text = new { type = "string" } },
            required = new[] { "text" },
        };
        var system = c.TranslatePrompt.Replace("{language}", EnglishName(language));
        var user = context.Length == 0 ? text : $"Earlier text (context only):\n{context}\n\nTranslate:\n{text}";

        var cloud = CloudName(cfg);
        var timeout = TimeSpan.FromMilliseconds(c.TranslateTimeoutMs);
        var sw = Stopwatch.StartNew();
        string answer;
        if (cloud == "Gemini")
            answer = await GeminiClient.ChatJsonAsync(cfg.Cloud.GeminiApiKey.Trim(), cfg.Cloud.GeminiTextModel, system, user, c.Temperature, timeout, ct);
        else if (cloud == "Groq")
            answer = await GroqClient.ChatJsonAsync(cfg.Cloud.GroqApiKey.Trim(), cfg.Cloud.GroqTextModel, system, user, c.Temperature, timeout, ct);
        else
        {
            // A cold start (loading the model) does not count towards the timeout
            await _server.WarmUpAsync(ct);
            sw.Restart();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            answer = await _server.ChatAsync([("system", system), ("user", user)], c.Temperature, text.Length * 3 + 200, schema, cts.Token);
        }
        var translation = Parse(answer);
        Log.Debug($"Translated into {language} in {sw.ElapsedMilliseconds} ms{(cloud != null ? $" ({cloud})" : "")}");
        return translation;
    }

    internal static string? Parse(string answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            text = Regex.Replace(text, @"[ \t]+", " ").Trim();
            return text.Length == 0 ? null : text;
        }
        catch (JsonException ex)
        {
            Log.Warn($"Translation answer is not valid JSON ({ex.Message}): {answer}");
            return null;
        }
    }
}
