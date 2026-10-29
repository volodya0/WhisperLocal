using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperLocal.Cloud;

/// <summary>Google Gemini API (generativelanguage.googleapis.com) with the user's own key from Google AI Studio.</summary>
public static class GeminiClient
{
    /// <summary>Where a key is made: Google AI Studio, free, no card needed.</summary>
    public const string KeyPage = "https://aistudio.google.com/apikey";

    /// <summary>Requests and limits used by the user's keys, in Google AI Studio.</summary>
    public const string UsagePage = "https://aistudio.google.com/usage";

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Transcription and text requests set their own timeout per request
    private static readonly HttpClient RequestHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Checks a key by listing the models it may use (costs no quota). Returns null if the key works, otherwise
    /// what is wrong. The key goes in a header, never in the URL or the log.
    /// </summary>
    public static async Task<string?> CheckKeyAsync(string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "models?pageSize=1");
        request.Headers.Add("x-goog-api-key", key);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return null;
            Log.Warn($"Gemini key check: HTTP {(int)response.StatusCode}");
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Google rejected the key: check that it is copied in full and not deleted in AI Studio.",
                HttpStatusCode.TooManyRequests => "The key works, but its limit is used up for now.",
                _ => $"Google answered HTTP {(int)response.StatusCode}; try again later.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"Gemini key check failed: {ex.Message}");
            return "Could not reach Google: check the internet connection.";
        }
    }

    /// <summary>
    /// Transcribes a WAV recording with <paramref name="model"/> (generateContent with the audio inline, a few
    /// hundred KB for a dictation). <paramref name="instruction"/> says how: languages, terms, no translating.
    /// Throws <see cref="GeminiException"/> when Google answers with an error, or on a timeout.
    /// </summary>
    public static async Task<string> TranscribeAsync(string key, string model, byte[] wav, string instruction, TimeSpan timeout,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instruction }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject
                    {
                        ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) },
                    },
                    new JsonObject { ["text"] = "Transcribe this dictation." }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0 },
        };

        var text = await GenerateAsync(key, model, body, timeout, ct);
        CloudUsage.RecordSpeech("Gemini", wav.Length);
        return text;
    }

    /// <summary>
    /// A text request: <paramref name="system"/> and <paramref name="user"/> in, the answer out, as JSON matching
    /// {"text": "…"} (the reply format the translation prompt asks for). Throws <see cref="GeminiException"/> like
    /// <see cref="TranscribeAsync"/>.
    /// </summary>
    public static async Task<string> ChatJsonAsync(string key, string model, string system, string user, double temperature, TimeSpan timeout,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = user }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = temperature,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "STRING" } },
                    ["required"] = new JsonArray("text"),
                },
            },
        };
        var text = await GenerateAsync(key, model, body, timeout, ct, tokens => CloudUsage.RecordText("Gemini", tokens));
        return text;
    }

    /// <summary>
    /// One generateContent call; returns the answer's text (without thought summaries). <paramref name="onTokens"/>
    /// gets the tokens the answer reports (usageMetadata); a refusal for the limit is noted in <see cref="CloudUsage"/>.
    /// </summary>
    private static async Task<string> GenerateAsync(string key, string model, JsonObject body, TimeSpan timeout, CancellationToken ct,
        Action<long?>? onTokens = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", key);

        string json;
        try
        {
            using var response = await RequestHttp.SendAsync(request, timeoutCts.Token);
            json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (CloudUsage.IsLimit(response.StatusCode))
                CloudUsage.RecordLimitHit("Gemini", ErrorMessage(json), response.Headers);
            if (!response.IsSuccessStatusCode)
                throw new GeminiException($"HTTP {(int)response.StatusCode}: {ErrorMessage(json)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeminiException($"no answer within {timeout.TotalSeconds:F0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new GeminiException(ex.Message);
        }

        try
        {
            var root = JsonNode.Parse(json);
            onTokens?.Invoke(root?["usageMetadata"]?["totalTokenCount"]?.GetValue<long>());
            var candidate = root?["candidates"]?[0];
            // No candidate: the prompt was blocked (promptFeedback says why)
            if (candidate == null)
                throw new GeminiException("no answer: " + (root?["promptFeedback"]?["blockReason"]?.GetValue<string>() ?? "empty answer"));
            var text = new StringBuilder();
            foreach (var part in candidate["content"]?["parts"]?.AsArray() ?? [])
            {
                // Thinking models may return thought summaries; only the answer counts
                if (part?["thought"]?.GetValue<bool>() == true)
                    continue;
                text.Append(part?["text"]?.GetValue<string>());
            }
            return text.ToString().Trim();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new GeminiException("unreadable answer: " + ex.Message);
        }
    }

    /// <summary>The "message" of a Google API error body, or the start of the body.</summary>
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

    /// <summary>"AIza…x9F": enough to recognise a key without showing it.</summary>
    public static string Mask(string key) => key.Length <= 8 ? "••••" : $"{key[..4]}…{key[^3..]}";
}

/// <summary>A cloud recognition request failed: an error answer, nothing usable, or not in time.</summary>
public class CloudException(string message) : Exception(message);

/// <summary>Gemini answered with an error, nothing usable, or not in time.</summary>
public sealed class GeminiException(string message) : CloudException(message);
