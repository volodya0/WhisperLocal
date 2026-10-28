using System.Diagnostics;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;
using WhisperLocal.Text;

namespace WhisperLocal.Pipeline;

/// <summary>A continuation for the live transcript: its last words and the words that would follow them.</summary>
public sealed record Completion(string Tail, string Continuation);

/// <summary>
/// Autocomplete while the hotkey is held: transcribes the audio recorded so far whenever new speech came in (about
/// every Autocomplete.RefreshMs, or at once on a pause) and asks the LLM how the sentence continues.
/// </summary>
public sealed class LiveAutocomplete
{
    private const int TickMs = 150;

    // whisper-server takes at most 30 s; the end of a long dictation is what autocomplete continues anyway
    private const int WindowMs = 25_000;

    private readonly ConfigManager _config;
    private readonly ISpeechToText _stt;
    private readonly Autocompleter _autocompleter;
    private readonly TextFilters _filters = new();

    public LiveAutocomplete(ConfigManager config, ISpeechToText stt, Autocompleter autocompleter)
    {
        _config = config;
        _stt = stt;
        _autocompleter = autocompleter;
    }

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled (the hotkey is released). <paramref name="show"/> gets the completion
    /// and whether the speaker is pausing, or null to hide it; it is called on the caller's context.
    /// </summary>
    /// <param name="recorded">The 16 kHz mono PCM recorded so far.</param>
    public async Task RunAsync(Func<byte[]> recorded, string? app, Action<Completion?, bool> show, CancellationToken ct)
    {
        var transcribedUpToMs = 0.0;
        var lastStt = Stopwatch.StartNew();
        var transcript = "";
        Completion? completion = null;
        bool? shown = null; // null: hidden; false: dim (speaking); true: highlighted (pausing)
        var warned = false;

        // "When I pause": only while pausing; "Always": dim while speaking, highlighted on a pause
        void Display(bool paused, bool fresh)
        {
            bool? wanted = completion == null || completion.Continuation.Length == 0
                           || (_config.Current.Autocomplete.OnPauseOnly && !paused) ? null : paused;
            if (wanted == shown && !fresh)
                return;
            shown = wanted;
            show(wanted == null ? null : completion, wanted == true);
        }

        try
        {
            while (true)
            {
                await Task.Delay(TickMs, ct);
                var cfg = _config.Current;
                var c = cfg.Autocomplete;

                var pcm = recorded();
                var durationMs = AudioConvert.DurationMs(pcm);
                var level = AudioConvert.SpeechLevelDbfs(pcm);
                if (durationMs < 800 || level < cfg.Filter.MinSpeechDbfs)
                    continue;

                // Quieter than the speech by 20 dB counts as silence, whatever the microphone's gain
                var silenceMs = AudioConvert.TrailingSilenceMs(pcm, Math.Max(level - 20, -65));
                var paused = silenceMs >= c.PauseMs;
                var speechEndMs = durationMs - silenceMs;
                var newSpeech = speechEndMs > transcribedUpToMs + 100;

                // A pause highlights the current completion at once, and the refresh below follows; "When I pause" waits
                // for the refresh when there is new speech, since its last completion belongs to an earlier pause
                if (!(paused && newSpeech && c.OnPauseOnly))
                    Display(paused, fresh: false);

                if (newSpeech && (paused || (!c.OnPauseOnly && lastStt.ElapsedMilliseconds >= c.RefreshMs)))
                {
                    transcribedUpToMs = speechEndMs;
                    lastStt.Restart();
                    try
                    {
                        var text = await TranscribeAsync(pcm, cfg, ct);
                        if (text.Length > 0 && text != transcript)
                        {
                            transcript = text;
                            var continuation = await _autocompleter.CompleteAsync(app, transcript, ct);
                            completion = new Completion(Autocompleter.Tail(transcript, 6), continuation);
                            Display(paused, fresh: true);
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // A slow or failed round only skips this completion; the next new speech tries again
                        if (!warned)
                            Log.Warn($"Autocomplete failed: {(ex is OperationCanceledException ? "timed out" : ex.Message)}");
                        warned = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The hotkey was released
        }
    }

    private async Task<string> TranscribeAsync(byte[] pcm, AppConfig cfg, CancellationToken ct)
    {
        var maxBytes = WindowMs * 32; // 16 kHz × 2 bytes per ms
        if (pcm.Length > maxBytes)
            pcm = pcm[^maxBytes..];
        var sw = Stopwatch.StartNew();
        var text = await _stt.TranscribeAsync(AudioConvert.ToWav(pcm), ct);
        text = _filters.ApplyDictionary(_filters.RemoveHallucinations(text, cfg), cfg).Trim();
        Log.Debug($"Live transcript in {sw.ElapsedMilliseconds} ms: {text}");
        return text;
    }
}
