using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Config;
using WhisperLocal.Text;

namespace WhisperLocal.Llm;

/// <summary>Next words for the live transcript, and a correction of the last English phrase ("" if none).</summary>
public sealed record LiveHints(IReadOnlyList<string> Next, string Correction);

/// <summary>
/// Live hints: asks the local LLM how the transcript being dictated could go on. Remembers recent dictations into
/// each app as context.
/// </summary>
public sealed class Suggester
{
    private readonly ConfigManager _config;
    private readonly LlamaServer _server;
    private readonly List<(DateTime Utc, string App, string Text)> _recent = [];

    public Suggester(ConfigManager config, LlamaServer server)
    {
        _config = config;
        _server = server;
    }

    /// <summary>Whether hints are on and everything they need is in place (the server is started on demand).</summary>
    public bool IsAvailable
    {
        get
        {
            var c = _config.Current.Suggest;
            return c.IsOn && File.Exists(LlamaServer.ResolveServerExe(c)) && File.Exists(LlamaServer.ResolveModel(c));
        }
    }

    /// <summary>Adds a finished dictation to the context of <paramref name="app"/>.</summary>
    public void Remember(string? app, string text)
    {
        lock (_recent)
            _recent.Add((DateTime.UtcNow, app ?? "", text.Trim()));
    }

    /// <summary>Loads the model in the background (on hotkey press, so the first hints are quick).</summary>
    public async Task PreloadAsync()
    {
        if (!IsAvailable)
            return;
        try
        {
            await _server.WarmUpAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"llama-server warm-up failed: {ex.Message}");
        }
    }

    /// <summary>Hints for <paramref name="transcript"/>, the live transcript of the dictation in progress.</summary>
    public async Task<LiveHints> SuggestAsync(string? app, string transcript, CancellationToken ct)
    {
        var c = _config.Current.Suggest;
        var count = Math.Clamp(c.Count, 1, 5);
        var words = Math.Max(1, c.MaxWords);
        var system = c.SystemPrompt.Replace("{count}", count.ToString()).Replace("{words}", words.ToString());

        var earlier = Earlier(app ?? "", c);
        var user = earlier.Length > 0 ? $"[Earlier: {earlier}]\n\n{transcript}" : transcript;

        // Constrained decoding: the answer is always this JSON, whatever the model would have added around it
        var schema = new
        {
            type = "object",
            properties = new
            {
                next = new { type = "array", items = new { type = "string" }, minItems = count, maxItems = count },
                correction = new { type = "string" },
            },
            required = new[] { "next", "correction" },
        };

        // A cold start (loading the model) does not count towards the timeout
        await _server.WarmUpAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(c.TimeoutMs);

        var sw = Stopwatch.StartNew();
        // Ukrainian takes about 3 tokens per word; the correction about 30
        var answer = await _server.ChatAsync([("system", system), ("user", user)], c.Temperature, count * words * 4 + 60, schema, cts.Token);
        var hints = Parse(answer, transcript, count, words);
        if (!c.Corrections)
            hints = hints with { Correction = "" };
        Log.Debug($"Hints in {sw.ElapsedMilliseconds} ms: {string.Join(" | ", hints.Next)}" +
                  (hints.Correction.Length > 0 ? $" ✎ {hints.Correction}" : ""));
        return hints;
    }

    /// <summary>Recent dictations into the same app, oldest first, cut to the last Suggest.ContextChars characters.</summary>
    private string Earlier(string app, SuggestConfig c)
    {
        var since = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, c.ContextMinutes));
        string text;
        lock (_recent)
        {
            _recent.RemoveAll(r => r.Utc < since);
            text = string.Join(" ", _recent.Where(r => string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase)).Select(r => r.Text));
        }
        if (text.Length > c.ContextChars)
        {
            text = text[^c.ContextChars..];
            var space = text.IndexOf(' ');
            if (space > 0)
                text = "…" + text[(space + 1)..];
        }
        return text.Trim();
    }

    internal static LiveHints Parse(string answer, string transcript, int count, int maxWords)
    {
        var next = new List<string>();
        var correction = "";
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (doc.RootElement.TryGetProperty("next", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;
                    var hint = Clean(item.GetString() ?? "", transcript);
                    if (hint.Length == 0 || next.Contains(hint, StringComparer.OrdinalIgnoreCase))
                        continue;
                    // The schema cannot limit words; a runaway hint is cut rather than dropped
                    var words = hint.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > maxWords + 2)
                        hint = string.Join(' ', words.Take(maxWords + 2)) + "…";
                    next.Add(hint);
                    if (next.Count == count)
                        break;
                }
            }
            if (doc.RootElement.TryGetProperty("correction", out var fix) && fix.ValueKind == JsonValueKind.String)
                correction = CleanCorrection(fix.GetString() ?? "");
        }
        catch (JsonException ex)
        {
            Log.Warn($"Hints are not valid JSON ({ex.Message}): {answer}");
        }
        return new LiveHints(next, correction);
    }

    /// <summary>Trims quotes, a leading "…" or comma, and a repeat of the transcript's end the model sometimes starts with.</summary>
    private static string Clean(string hint, string transcript)
    {
        hint = Regex.Replace(hint, @"\s+", " ").Trim();
        hint = hint.Trim('"', '«', '»', '“', '”').Trim();
        hint = hint.TrimStart('…', '.', ',', ';', ':', '-', '—', ' ');

        // "…the last words of the transcript and then more" → "and then more"
        var tail = transcript.TrimEnd('.', '!', '?', ' ');
        for (var n = Math.Min(tail.Length, hint.Length - 1); n >= 12; n--)
        {
            if (hint.StartsWith(tail[^n..], StringComparison.OrdinalIgnoreCase))
            {
                hint = hint[n..].TrimStart('.', ',', ';', ':', ' ');
                break;
            }
        }
        return TextFilters.CountWords(hint) == 0 ? "" : hint;
    }

    /// <summary>"wrong → right" with both sides present and different; anything else is dropped.</summary>
    private static string CleanCorrection(string correction)
    {
        var parts = Regex.Split(correction.Trim().Trim('"'), @"\s*(?:→|->|=>)\s*");
        if (parts.Length != 2)
            return "";
        var (wrong, right) = (parts[0].Trim(), parts[1].Trim());
        if (wrong.Length == 0 || right.Length == 0 || wrong.Length > 80 || right.Length > 80
            || string.Equals(wrong.TrimEnd('.', ','), right.TrimEnd('.', ','), StringComparison.OrdinalIgnoreCase))
            return "";
        return $"{wrong} → {right}";
    }
}
