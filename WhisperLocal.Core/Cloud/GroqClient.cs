using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisperLocal.Config;
using WhisperLocal.Stt;

namespace WhisperLocal.Cloud;

/// <summary>
/// Groq API (api.groq.com, OpenAI-compatible) with the user's own key from the Groq console: the same Whisper models
/// as on this computer, run on Groq's hardware in a fraction of a second.
/// </summary>
public static class GroqClient
{
    /// <summary>Where a key is made: the Groq console, free, no card needed.</summary>
    public const string KeyPage = "https://console.groq.com/keys";

    private const string BaseUrl = "https://api.groq.com/openai/v1/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Transcription sets its own timeout per request (Cloud.TimeoutSec)
    private static readonly HttpClient TranscribeHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Checks a key by listing the models it may use (costs no quota). Returns null if the key works, otherwise
    /// what is wrong. The key goes in a header, never in the URL or the log.
    /// </summary>
    public static async Task<string?> CheckKeyAsync(string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return null;
            Log.Warn($"Groq key check: HTTP {(int)response.StatusCode}");
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Groq rejected the key: check that it is copied in full and not deleted in the Groq console.",
                HttpStatusCode.TooManyRequests => "The key works, but its limit is used up for now.",
                _ => $"Groq answered HTTP {(int)response.StatusCode}; try again later.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"Groq key check failed: {ex.Message}");
            return "Could not reach Groq: check the internet connection.";
        }
    }

    /// <summary>
    /// Transcribes a WAV recording with the Whisper <paramref name="model"/>, following Whisper.Language the way local
    /// recognition does: a list ("uk,en") detects the language and accepts only the listed ones, transcribing again in
    /// the first one otherwise. Whisper.InitialPrompt and Whisper.Temperature apply too.
    /// Throws <see cref="GroqException"/> when Groq answers with an error, or on a timeout.
    /// </summary>
    public static async Task<string> TranscribeAsync(string key, string model, byte[] wav, WhisperConfig w, TimeSpan timeout,
        CancellationToken ct)
    {
        var languages = WhisperServerStt.ParseLanguages(w.Language);
        if (languages is not [_, _, ..])
            return (await RequestAsync(key, model, wav, languages[0], w, timeout, ct)).Text;

        var (text, detected) = await RequestAsync(key, model, wav, "auto", w, timeout, ct);
        if (languages.Any(l => WhisperServerStt.SameLanguage(l, detected)))
            return text;
        // Typically a short Ukrainian phrase taken for Russian: transcribe again in the main language
        Log.Info($"Groq detected '{detected}', not in Whisper.Language ({w.Language}), transcribing as '{languages[0]}'");
        return (await RequestAsync(key, model, wav, languages[0], w, timeout, ct)).Text;
    }

    /// <summary>One transcription request; returns the text and the language Groq reports (full name, e.g. "English").</summary>
    private static async Task<(string Text, string Language)> RequestAsync(string key, string model, byte[] wav, string language,
        WhisperConfig w, TimeSpan timeout, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(w.Temperature.ToString(CultureInfo.InvariantCulture)), "temperature");
        // verbose_json reports the detected language
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            form.Add(new StringContent(language), "language");
        if (!string.IsNullOrWhiteSpace(w.InitialPrompt))
            form.Add(new StringContent(w.InitialPrompt.Trim()), "prompt");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        string json;
        try
        {
            using var response = await TranscribeHttp.SendAsync(request, timeoutCts.Token);
            json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new GroqException($"HTTP {(int)response.StatusCode}: {ErrorMessage(json)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GroqException($"no answer within {timeout.TotalSeconds:F0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new GroqException(ex.Message);
        }

        try
        {
            var root = JsonNode.Parse(json);
            return (root?["text"]?.GetValue<string>()?.Trim() ?? "", root?["language"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new GroqException("unreadable answer: " + ex.Message);
        }
    }

    /// <summary>The "message" of an OpenAI-style error body, or the start of the body.</summary>
    private static string ErrorMessage(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException)
        {
        }
        return json.Length > 200 ? json[..200] : json;
    }
}

/// <summary>Groq answered with an error, nothing usable, or not in time.</summary>
public sealed class GroqException(string message) : CloudException(message);
