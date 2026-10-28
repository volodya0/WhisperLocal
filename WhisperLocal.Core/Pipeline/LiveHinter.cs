using System.Diagnostics;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;
using WhisperLocal.Text;

namespace WhisperLocal.Pipeline;

/// <summary>
/// Live hints while the hotkey is held: transcribes the audio recorded so far whenever new speech came in (about
/// every Suggest.RefreshMs, or at once on a pause) and asks the LLM how it could go on.
/// </summary>
public sealed class LiveHinter
{
    private const int TickMs = 150;

    // whisper-server takes at most 30 s; the end of a long dictation is what the hints continue anyway
    private const int WindowMs = 25_000;

    private readonly ConfigManager _config;
    private readonly ISpeechToText _stt;
    private readonly Suggester _suggester;
    private readonly TextFilters _filters = new();

    public LiveHinter(ConfigManager config, ISpeechToText stt, Suggester suggester)
    {
        _config = config;
        _stt = stt;
        _suggester = suggester;
    }

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled (the hotkey is released). <paramref name="show"/> gets the hints
    /// and whether the speaker is pausing, or null to hide them; it is called on the caller's context.
    /// </summary>
    /// <param name="recorded">The 16 kHz mono PCM recorded so far.</param>
    public async Task RunAsync(Func<byte[]> recorded, string? app, Action<LiveHints?, bool> show, CancellationToken ct)
    {
        var transcribedUpToMs = 0.0;
        var lastStt = Stopwatch.StartNew();
        var transcript = "";
        LiveHints? hints = null;
        bool? shown = null; // null: hidden; false: dim (speaking); true: highlighted (pausing)
        var warned = false;

        // "On pause": only while pausing; "Always": dim while speaking, highlighted on a pause
        void Display(bool paused, bool fresh)
        {
            bool? wanted = hints == null || (hints.Next.Count == 0 && hints.Correction.Length == 0)
                           || (_config.Current.Suggest.OnPauseOnly && !paused) ? null : paused;
            if (wanted == shown && !fresh)
                return;
            shown = wanted;
            show(wanted == null ? null : hints, wanted == true);
        }

        try
        {
            while (true)
            {
                await Task.Delay(TickMs, ct);
                var cfg = _config.Current;
                var c = cfg.Suggest;

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

                // A pause highlights the current hints at once, and the refresh below follows; "On pause" waits for
                // the refresh instead when there is new speech, since its last hints belong to an earlier pause
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
                            hints = await _suggester.SuggestAsync(app, transcript, ct);
                            Display(paused, fresh: true);
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // A slow or failed round only skips these hints; the next new speech tries again
                        if (!warned)
                            Log.Warn($"Live hints failed: {(ex is OperationCanceledException ? "timed out" : ex.Message)}");
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
