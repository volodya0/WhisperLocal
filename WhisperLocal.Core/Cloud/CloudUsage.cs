using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Config;

namespace WhisperLocal.Cloud;

/// <summary>
/// What this app sent to the cloud providers today (requests, audio seconds, tokens), the limits Groq reports with
/// every answer, and the last time a provider refused a request for its limit. Neither provider lets a key look up its
/// account's usage, so this counts only this app's requests. Today's counts are kept in cloud-usage.json and start
/// again at midnight. Any thread.
/// </summary>
public static class CloudUsage
{
    /// <summary>After a refused request without a Retry-After, how long the provider counts as strained.</summary>
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "cloud-usage.json");
    private static readonly Dictionary<string, ProviderState> States = new(StringComparer.OrdinalIgnoreCase);
    private static DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private static long _savedAt;
    private static bool _loaded;

    /// <summary>Counts of one provider today.</summary>
    public sealed class Counts
    {
        public int SpeechRequests { get; set; }
        public double SpeechSeconds { get; set; }
        public int TextRequests { get; set; }
        public long TextTokens { get; set; }
    }

    /// <summary>Groq's limits from the latest answer: requests per day, tokens per minute (as its headers define them).</summary>
    public sealed record GroqLimits(long? RequestsLimit, long? RequestsLeft, TimeSpan? RequestsReset, long? TokensLimit, long? TokensLeft,
        TimeSpan? TokensReset, DateTime AtUtc);

    private sealed class ProviderState
    {
        public Counts Today { get; set; } = new();
        public Queue<DateTime> RecentUtc { get; } = new(); // request times within the last minute
        public GroqLimits? Limits { get; set; }
        public DateTime? LimitHitUtc { get; set; }
        public DateTime StrainedUntilUtc { get; set; }
        public string? LimitMessage { get; set; }
    }

    private sealed class Saved
    {
        public string Day { get; set; } = "";
        public Dictionary<string, Counts> Providers { get; set; } = [];
    }

    /// <summary>A recognition request answered: <paramref name="wavBytes"/> is the 16 kHz mono 16-bit WAV sent.</summary>
    public static void RecordSpeech(string provider, int wavBytes) =>
        Update(provider, s =>
        {
            s.Today.SpeechRequests++;
            s.Today.SpeechSeconds += Math.Max(0, wavBytes - 44) / 32000.0;
        });

    /// <summary>A text request (translation) answered with <paramref name="tokens"/> in and out, if the provider said.</summary>
    public static void RecordText(string provider, long? tokens) =>
        Update(provider, s =>
        {
            s.Today.TextRequests++;
            s.Today.TextTokens += tokens ?? 0;
        });

    /// <summary>
    /// A request refused for the provider's limit (HTTP 429): remembered with its message; the provider counts as
    /// strained until the Retry-After it sent, or for <see cref="DefaultBackoff"/>.
    /// </summary>
    public static void RecordLimitHit(string provider, string message, HttpResponseHeaders headers)
    {
        var retry = headers.RetryAfter?.Delta ?? (headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var backoff = retry is { } r && r > TimeSpan.Zero && r < TimeSpan.FromMinutes(10) ? r : DefaultBackoff;
        Update(provider, s =>
        {
            s.LimitHitUtc = DateTime.UtcNow;
            s.StrainedUntilUtc = DateTime.UtcNow + backoff;
            s.LimitMessage = message;
        }, count: false);
    }

    /// <summary>Reads Groq's x-ratelimit-* headers, sent with every answer (refused ones too).</summary>
    public static void RecordGroqHeaders(HttpResponseHeaders headers)
    {
        var requestsLimit = Number(headers, "x-ratelimit-limit-requests");
        var tokensLimit = Number(headers, "x-ratelimit-limit-tokens");
        if (requestsLimit == null && tokensLimit == null)
            return;
        var limits = new GroqLimits(requestsLimit, Number(headers, "x-ratelimit-remaining-requests"), Duration(headers, "x-ratelimit-reset-requests"),
            tokensLimit, Number(headers, "x-ratelimit-remaining-tokens"), Duration(headers, "x-ratelimit-reset-tokens"), DateTime.UtcNow);
        Update("Groq", s => s.Limits = limits, count: false);
    }

    /// <summary>
    /// The provider is near or over its limit: refused a request lately, or (Groq) has under a fifth of its tokens for
    /// this minute or under 20 requests for today left. Live translation of unfinished phrases pauses meanwhile, so
    /// finished lines keep getting through.
    /// </summary>
    public static bool IsStrained(string provider)
    {
        lock (Lock)
        {
            if (!States.TryGetValue(provider, out var s))
                return false;
            if (DateTime.UtcNow < s.StrainedUntilUtc)
                return true;
            if (s.Limits is not { } l)
                return false;
            var tokensLow = l.TokensLeft is { } tokensLeft && l.TokensLimit is > 0 && tokensLeft < l.TokensLimit / 5
                            && DateTime.UtcNow < l.AtUtc + (l.TokensReset ?? TimeSpan.FromMinutes(1));
            var requestsLow = l.RequestsLeft is < 20 && DateTime.UtcNow < l.AtUtc + (l.RequestsReset ?? TimeSpan.FromDays(1));
            return tokensLow || requestsLow;
        }
    }

    /// <summary>
    /// One line for the Models page: today's counts, the last minute, Groq's limits, and a limit hit within the last
    /// hour; <paramref name="problem"/> when the provider is strained now. Null before the first request today.
    /// </summary>
    public static string? Describe(string provider, out bool problem)
    {
        var strained = IsStrained(provider);
        lock (Lock)
        {
            Load();
            RollDay();
            problem = strained;
            if (!States.TryGetValue(provider, out var s))
                return null;
            var parts = new List<string>();
            var t = s.Today;
            var today = new List<string>();
            if (t.SpeechRequests > 0)
                today.Add($"{Plural(t.SpeechRequests, "recording")} ({FormatSeconds(t.SpeechSeconds)})");
            if (t.TextRequests > 0)
                today.Add($"{Plural(t.TextRequests, "translation")}" + (t.TextTokens > 0 ? $" ({FormatTokens(t.TextTokens)} tokens)" : ""));
            if (today.Count > 0)
                parts.Add("Today: " + string.Join(", ", today));
            Trim(s);
            if (s.RecentUtc.Count > 0)
                parts.Add($"last minute: {Plural(s.RecentUtc.Count, "request")}");
            if (s.Limits is { } l && DateTime.UtcNow - l.AtUtc < TimeSpan.FromHours(1))
            {
                var left = new List<string>();
                if (l.RequestsLeft is { } rl && l.RequestsLimit is { } rt)
                    left.Add($"{rl:N0} of {rt:N0} requests today");
                if (l.TokensLeft is { } tl && l.TokensLimit is { } tt)
                {
                    // The token budget refills within the minute: past its reset, it is full again
                    var refilled = l.TokensReset is { } reset && DateTime.UtcNow > l.AtUtc + reset;
                    left.Add($"{(refilled ? tt : tl):N0} of {tt:N0} tokens this minute");
                }
                if (left.Count > 0)
                    parts.Add("Groq left: " + string.Join(", ", left));
            }
            if (s.LimitHitUtc is { } hit && DateTime.UtcNow - hit < TimeSpan.FromHours(1))
                parts.Add($"limit reached {Ago(hit)}" + (s.LimitMessage is { Length: > 0 } m ? $": {Shorten(m)}" : ""));
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    private static void Update(string provider, Action<ProviderState> change, bool count = true)
    {
        lock (Lock)
        {
            Load();
            RollDay();
            if (!States.TryGetValue(provider, out var s))
                States[provider] = s = new ProviderState();
            change(s);
            if (!count)
                return;
            s.RecentUtc.Enqueue(DateTime.UtcNow);
            Trim(s);
            if (Stopwatch.GetElapsedTime(_savedAt) >= SaveEvery)
                Save();
        }
    }

    private static void Trim(ProviderState s)
    {
        while (s.RecentUtc.Count > 0 && DateTime.UtcNow - s.RecentUtc.Peek() > TimeSpan.FromMinutes(1))
            s.RecentUtc.Dequeue();
    }

    /// <summary>A new day: the counts start again (limits and limit hits stay, they carry their own times).</summary>
    private static void RollDay()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        if (day == _day)
            return;
        _day = day;
        foreach (var s in States.Values)
            s.Today = new Counts();
    }

    private static void Load()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is not { } saved)
                return;
            if (saved.Day != _day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                return;
            foreach (var (provider, counts) in saved.Providers)
                States[provider] = new ProviderState { Today = counts };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Cloud usage: could not read {FilePath}: {ex.Message}");
        }
    }

    private static void Save()
    {
        _savedAt = Stopwatch.GetTimestamp();
        try
        {
            var saved = new Saved
            {
                Day = _day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Providers = States.ToDictionary(p => p.Key, p => p.Value.Today),
            };
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cloud usage: could not write {FilePath}: {ex.Message}");
        }
    }

    private static long? Number(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>Groq's reset times look like "2m59.56s", "7.66s" or "1h2m3s".</summary>
    private static TimeSpan? Duration(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values) || values.FirstOrDefault() is not { Length: > 0 } text)
            return null;
        var match = Regex.Match(text, @"^(?:(\d+)h)?(?:(\d+)m(?!s))?(?:([\d.]+)s)?(?:([\d.]+)ms)?$");
        if (!match.Success)
            return null;
        double Part(int i) => match.Groups[i].Success ? double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromHours(Part(1)) + TimeSpan.FromMinutes(Part(2)) + TimeSpan.FromSeconds(Part(3)) + TimeSpan.FromMilliseconds(Part(4));
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";

    private static string FormatSeconds(double seconds) =>
        seconds < 60 ? $"{seconds:F0} s" : seconds < 3600 ? $"{seconds / 60:F0} min" : $"{seconds / 3600:F1} h";

    private static string FormatTokens(long tokens) => tokens < 1000 ? tokens.ToString(CultureInfo.InvariantCulture) : $"{tokens / 1000.0:F0}k";

    private static string Ago(DateTime utc)
    {
        var ago = DateTime.UtcNow - utc;
        return ago < TimeSpan.FromMinutes(1) ? "just now" : $"{(int)ago.TotalMinutes} min ago";
    }

    private static string Shorten(string message) => message.Length > 90 ? message[..90] + "…" : message;

    /// <summary>Whether an HTTP status means the provider's limit (rate or quota).</summary>
    public static bool IsLimit(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests;
}
