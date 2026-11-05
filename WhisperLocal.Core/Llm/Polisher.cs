using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Config;
using WhisperLocal.Stt;

namespace WhisperLocal.Llm;

/// <summary>
/// Grammar check (Grammar and Context fix in the transcript window, see <see cref="TranscriptTools"/>): which
/// model checks, the local one or Gemini or Groq (Polish.Engine) with the user's key, and reading its answer.
/// </summary>
public static class Polisher
{
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
            Log.Warn($"Correction rejected (length ratio {ratio:F2}): {polished}");
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
