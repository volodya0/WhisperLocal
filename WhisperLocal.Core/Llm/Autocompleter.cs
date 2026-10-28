using System.Diagnostics;
using System.Text.RegularExpressions;
using WhisperLocal.Config;

namespace WhisperLocal.Llm;

/// <summary>
/// Autocomplete for dictation, like code completion: the model continues the live transcript as plain text and the
/// continuation stops at the end of the sentence. Recent dictations into the same app come first as context.
/// </summary>
public sealed class Autocompleter
{
    // The end of a sentence or line ends the continuation, like the end of a line in code completion
    private static readonly string[] Stop = ["\n", ". ", "! ", "? ", "…"];

    private readonly ConfigManager _config;
    private readonly LlamaServer _server;
    private readonly List<(DateTime Utc, string App, string Text)> _recent = [];

    public Autocompleter(ConfigManager config, LlamaServer server)
    {
        _config = config;
        _server = server;
    }

    /// <summary>Adds a finished dictation to the context of <paramref name="app"/>.</summary>
    public void Remember(string? app, string text)
    {
        lock (_recent)
            _recent.Add((DateTime.UtcNow, app ?? "", text.Trim()));
    }

    /// <summary>The words that would most likely follow <paramref name="transcript"/>; "" if nothing useful.</summary>
    public async Task<string> CompleteAsync(string? app, string transcript, CancellationToken ct)
    {
        var c = _config.Current.Autocomplete;
        var earlier = Earlier(app ?? "", c);
        var prompt = earlier.Length > 0 ? earlier + " " + transcript : transcript;

        // A cold start (loading the model) does not count towards the timeout
        await _server.WarmUpAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(c.TimeoutMs);

        var sw = Stopwatch.StartNew();
        // Ukrainian takes about 3 tokens per word
        var raw = await _server.CompleteAsync(prompt, c.Temperature, Math.Max(4, c.MaxWords) * 3, Stop, cts.Token);
        var continuation = Clean(raw, transcript, Math.Max(2, c.MaxWords));
        Log.Debug($"Autocomplete in {sw.ElapsedMilliseconds} ms: …{Tail(transcript, 6)} ⟶ {continuation}");
        return continuation;
    }

    /// <summary>The last <paramref name="words"/> words of <paramref name="text"/>.</summary>
    public static string Tail(string text, int words)
    {
        var all = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', all.Skip(Math.Max(0, all.Length - words)));
    }

    /// <summary>Recent dictations into the same app, oldest first, cut to the last ContextChars characters.</summary>
    private string Earlier(string app, AutocompleteConfig c)
    {
        var since = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(0, c.ContextMinutes));
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
                text = text[(space + 1)..];
        }
        return text.Trim();
    }

    /// <summary>
    /// One line, at most <paramref name="maxWords"/> words, keeping the leading space (or its absence: the model may
    /// finish a word that was cut off). Empty if it is only punctuation or noise.
    /// </summary>
    internal static string Clean(string raw, string transcript, int maxWords)
    {
        var text = Regex.Replace(raw, @"[ \t]+", " ");
        var newline = text.IndexOf('\n');
        if (newline >= 0)
            text = text[..newline];
        text = text.TrimEnd();

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > maxWords)
            text = (text.StartsWith(' ') ? " " : "") + string.Join(' ', words.Take(maxWords));
        if (!Regex.IsMatch(text, @"\p{L}|\p{N}"))
            return "";
        // After a finished sentence the continuation starts a new one
        if (Regex.IsMatch(transcript, @"[.!?]$") && !text.StartsWith(' '))
            text = " " + text;
        return text;
    }
}
