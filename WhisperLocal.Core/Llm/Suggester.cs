using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Config;
using WhisperLocal.Text;

namespace WhisperLocal.Llm;

/// <summary>
/// Continuation hints: remembers what was recently dictated into each app and asks the local LLM for a few
/// short ways to continue it.
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
            return c.Enabled && File.Exists(LlamaServer.ResolveServerExe(c)) && File.Exists(LlamaServer.ResolveModel(c));
        }
    }

    /// <summary>Adds inserted text (a dictation or an accepted hint) to the context of <paramref name="app"/>.</summary>
    public void Remember(string? app, string text)
    {
        lock (_recent)
            _recent.Add((DateTime.UtcNow, app ?? "", text.Trim()));
    }

    /// <summary>Loads the model in the background (on hotkey press, so the hints are quick after the dictation).</summary>
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

    /// <summary>Hints that continue what was recently dictated into <paramref name="app"/>; empty if there is nothing to continue.</summary>
    public async Task<IReadOnlyList<string>> SuggestAsync(string? app, CancellationToken ct)
    {
        var c = _config.Current.Suggest;
        var context = Context(app ?? "", c);
        if (context.Length == 0)
            return [];

        var count = Math.Clamp(c.Count, 1, 5);
        var words = Math.Max(3, c.MaxWords);
        var system = c.SystemPrompt.Replace("{count}", count.ToString()).Replace("{words}", words.ToString());
        if (!string.IsNullOrEmpty(app))
            system += $"\n\nThe text is being written in the application \"{app}\".";

        // Constrained decoding: the answer is always this JSON, whatever the model would have added around it
        var schema = new
        {
            type = "object",
            properties = new
            {
                suggestions = new { type = "array", items = new { type = "string" }, minItems = count, maxItems = count },
            },
            required = new[] { "suggestions" },
        };

        // A cold start (loading the model) does not count towards the timeout
        await _server.WarmUpAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(c.TimeoutMs);

        var sw = Stopwatch.StartNew();
        // Ukrainian takes about 3 tokens per word
        var answer = await _server.ChatAsync([("system", system), ("user", context)], c.Temperature, count * words * 4 + 40, schema, cts.Token);
        var hints = Parse(answer, context, count, words);
        Log.Info($"Hints: {hints.Count} in {sw.ElapsedMilliseconds} ms");
        if (_config.Current.Logging.LogText)
            foreach (var hint in hints)
                Log.Info($"  hint: {hint}");
        return hints;
    }

    /// <summary>Recent text dictated into the same app, oldest first, cut to the last Suggest.ContextChars characters.</summary>
    private string Context(string app, SuggestConfig c)
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

    internal static List<string> Parse(string answer, string context, int count, int maxWords)
    {
        var hints = new List<string>();
        JsonElement items;
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (!doc.RootElement.TryGetProperty("suggestions", out items) || items.ValueKind != JsonValueKind.Array)
                return hints;
            items = items.Clone();
        }
        catch (JsonException ex)
        {
            Log.Warn($"Hints are not valid JSON ({ex.Message}): {answer}");
            return hints;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var hint = Clean(item.GetString() ?? "", context);
            if (hint.Length == 0 || hints.Contains(hint, StringComparer.OrdinalIgnoreCase))
                continue;
            // The schema cannot limit words; a runaway hint is cut rather than dropped
            var words = hint.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > maxWords * 2)
                hint = string.Join(' ', words.Take(maxWords * 2)) + "…";
            hints.Add(hint);
            if (hints.Count == count)
                break;
        }
        return hints;
    }

    /// <summary>Trims quotes, a leading "…" or comma, and a repeat of the text's end the model sometimes starts with.</summary>
    private static string Clean(string hint, string context)
    {
        hint = Regex.Replace(hint, @"\s+", " ").Trim();
        hint = hint.Trim('"', '«', '»', '“', '”').Trim();
        hint = hint.TrimStart('…', '.', ',', ';', ':', '-', '—', ' ');

        // "…the last words of the text and then more" → "and then more"
        var tail = context.TrimEnd('.', '!', '?', ' ');
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
}
