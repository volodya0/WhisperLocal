using System.Diagnostics;
using System.Threading.Channels;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;
using WhisperLocal.Text;

namespace WhisperLocal.Pipeline;

/// <summary>One segment of what was heard: its time from the start, the text and, once done, its translation.</summary>
public sealed class ListenLine(TimeSpan at, string text)
{
    public TimeSpan At { get; } = at;
    public string Text { get; } = text;
    public string? Translation { get; set; }

    /// <summary>Recognized and, if wanted, translated: nothing more will change.</summary>
    public bool Done { get; set; }
}

/// <summary>
/// Listening: cuts a continuous audio stream into segments at pauses (or every Listen.MaxSegmentSec), transcribes
/// them one after another and, when <see cref="TranslateTo"/> is set, translates each with the local LLM. Recognition
/// and translation run as two stages, so a slow translation never holds up the next segment's text. While a segment
/// is still being spoken, it is transcribed about every Listen.ProvisionalMs as provisional text, whenever no finished
/// segment is waiting for recognition.
/// </summary>
public sealed class LiveListener
{
    private const int TickMs = 200;
    private const int MinSegmentMs = 1000;
    private const int PromptChars = 200;

    private readonly ConfigManager _config;
    private readonly ISpeechToText _stt;
    private readonly Translator _translator;
    private readonly TextFilters _filters = new();
    private volatile string _translateTo = "";

    // Recognition text so far, the prompt for the next segment; all fields below are used on the caller's context
    private string _previous = "";
    private int _generation; // the segment being spoken; increases with every cut
    private int _pendingSegments;
    private bool _provisionalBusy;
    private int _provisionalShown = -1; // generation of the provisional text on screen, -1 for none
    private long _provisionalAt;
    private double _provisionalUpToMs;

    public LiveListener(ConfigManager config, ISpeechToText stt, Translator translator)
    {
        _config = config;
        _stt = stt;
        _translator = translator;
    }

    /// <summary>Target language code, "" for the original only; applies from the next segment on. Any thread.</summary>
    public string TranslateTo
    {
        get => _translateTo;
        set => _translateTo = value;
    }

    /// <summary>A segment was recognized (translation, if any, follows in <see cref="Finished"/>).</summary>
    public event Action<ListenLine>? Heard;

    /// <summary>A segment is complete: translated, or not to be translated.</summary>
    public event Action<ListenLine>? Finished;

    /// <summary>Recognition or translation failed for a segment; listening goes on.</summary>
    public event Action<string>? Failed;

    /// <summary>The segment being spoken, transcribed so far; null when it is gone (recognized, or nothing).</summary>
    public event Action<string?>? Provisional;

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled, then finishes the segments already heard before returning.
    /// The events are raised on the caller's context.
    /// </summary>
    /// <param name="drain">The 16 kHz mono 16-bit PCM captured since the previous call.</param>
    public async Task RunAsync(Func<byte[]> drain, CancellationToken ct)
    {
        _previous = "";
        _generation = 0;
        _pendingSegments = 0;
        _provisionalShown = -1;
        _provisionalUpToMs = 0;
        var segments = Channel.CreateUnbounded<(byte[] Pcm, TimeSpan At, int Generation)>();
        var lines = Channel.CreateUnbounded<ListenLine>();
        var recognize = RecognizeAsync(segments.Reader, lines.Writer);
        var translate = TranslateAsync(lines.Reader);

        var buffer = new MemoryStream();
        var bufferAt = TimeSpan.Zero; // time of the buffer's first byte since listening started
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TickMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                buffer.Write(drain());
                var cfg = _config.Current;
                var pcm = buffer.ToArray();
                var durationMs = AudioConvert.DurationMs(pcm);
                if (durationMs < MinSegmentMs)
                    continue;

                var level = AudioConvert.SpeechLevelDbfs(pcm);
                if (level < cfg.Filter.MinSpeechDbfs)
                {
                    // Only silence so far: keep the last moment, where the next speech may be starting
                    if (durationMs > 1500)
                        bufferAt += Keep(buffer, pcm, 300);
                    continue;
                }

                // Quieter than the speech by 20 dB counts as silence, whatever the volume
                var silenceMs = AudioConvert.TrailingSilenceMs(pcm, Math.Max(level - 20, -65));
                var pause = silenceMs >= cfg.Listen.PauseMs && durationMs - silenceMs >= 500;
                if (pause || durationMs >= cfg.Listen.MaxSegmentSec * 1000.0)
                {
                    _pendingSegments++;
                    segments.Writer.TryWrite((pcm, bufferAt, _generation++));
                    bufferAt += TimeSpan.FromMilliseconds(durationMs);
                    buffer.SetLength(0);
                    _provisionalUpToMs = 0;
                }
                else if (cfg.Listen.ProvisionalMs > 0 && !_provisionalBusy && _pendingSegments == 0
                         && Stopwatch.GetElapsedTime(_provisionalAt).TotalMilliseconds >= cfg.Listen.ProvisionalMs
                         && durationMs - silenceMs > _provisionalUpToMs + 300)
                {
                    // New speech since the last provisional pass
                    _provisionalUpToMs = durationMs - silenceMs;
                    _ = ProvisionalAsync(pcm, _generation);
                }
            }

            buffer.Write(drain());
            var rest = buffer.ToArray();
            if (AudioConvert.DurationMs(rest) >= 500 && AudioConvert.SpeechLevelDbfs(rest) >= _config.Current.Filter.MinSpeechDbfs)
            {
                _pendingSegments++;
                segments.Writer.TryWrite((rest, bufferAt, _generation++));
            }
            else
            {
                _generation++; // a provisional pass still running is stale
                ClearProvisional(_generation);
            }
        }
        finally
        {
            segments.Writer.TryComplete();
            await recognize;
            await translate;
        }
    }

    /// <summary>Drops all but the last <paramref name="keepMs"/> of the buffer; returns the duration dropped.</summary>
    private static TimeSpan Keep(MemoryStream buffer, byte[] pcm, int keepMs)
    {
        var keep = (int)(AudioConvert.TargetFormat.AverageBytesPerSecond * keepMs / 1000L) & ~1;
        buffer.SetLength(0);
        buffer.Write(pcm, pcm.Length - keep, keep);
        return TimeSpan.FromMilliseconds(AudioConvert.DurationMs(pcm) - keepMs);
    }

    /// <summary>Transcribes the segment being spoken; shown only if that segment is still the current one.</summary>
    private async Task ProvisionalAsync(byte[] pcm, int generation)
    {
        _provisionalBusy = true;
        _provisionalAt = Stopwatch.GetTimestamp();
        try
        {
            var cfg = _config.Current;
            var text = await _stt.TranscribeAsync(AudioConvert.ToWav(pcm), CancellationToken.None, cfg.Listen.Language, Prompt());
            text = _filters.RemoveHallucinations(text, cfg);
            if (generation != _generation || text.Length == 0)
                return;
            _provisionalShown = generation;
            Provisional?.Invoke(text);
        }
        catch (Exception ex)
        {
            // The final pass reports failures; a provisional one is only a preview
            Log.Debug($"Listening: provisional recognition failed: {ex.Message}");
        }
        finally
        {
            _provisionalBusy = false;
            _provisionalAt = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Removes the provisional text if it belongs to <paramref name="generation"/> or an earlier segment.</summary>
    private void ClearProvisional(int generation)
    {
        if (_provisionalShown < 0 || _provisionalShown > generation)
            return;
        _provisionalShown = -1;
        Provisional?.Invoke(null);
    }

    // The text before this segment helps Whisper continue a sentence cut in the middle
    private string Prompt() => _previous.Length > PromptChars ? _previous[^PromptChars..] : _previous;

    private async Task RecognizeAsync(ChannelReader<(byte[] Pcm, TimeSpan At, int Generation)> segments, ChannelWriter<ListenLine> lines)
    {
        try
        {
            await foreach (var (pcm, at, generation) in segments.ReadAllAsync())
            {
                try
                {
                    var cfg = _config.Current;
                    var text = await _stt.TranscribeAsync(AudioConvert.ToWav(pcm), CancellationToken.None, cfg.Listen.Language, Prompt());
                    text = _filters.RemoveHallucinations(text, cfg);
                    ClearProvisional(generation);
                    if (text.Length == 0)
                        continue;
                    if (cfg.Logging.LogText)
                        Log.Info($"Heard: {text}");
                    _previous = (_previous + " " + text).Trim();
                    var line = new ListenLine(at, text);
                    Heard?.Invoke(line);
                    lines.TryWrite(line);
                }
                catch (Exception ex)
                {
                    ClearProvisional(generation);
                    Log.Warn($"Listening: recognition failed: {ex.Message}");
                    Failed?.Invoke(ex.Message);
                }
                finally
                {
                    _pendingSegments--;
                }
            }
        }
        finally
        {
            lines.TryComplete();
        }
    }

    private async Task TranslateAsync(ChannelReader<ListenLine> lines)
    {
        var context = new Queue<string>();
        await foreach (var line in lines.ReadAllAsync())
        {
            var target = TranslateTo;
            if (target.Length > 0)
            {
                try
                {
                    var translation = await _translator.TranslateAsync(line.Text, target, string.Join(" ", context), CancellationToken.None);
                    // Already in that language: nothing to add
                    if (translation != null && !string.Equals(translation.TrimEnd('.'), line.Text.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                        line.Translation = translation;
                }
                catch (Exception ex)
                {
                    var message = ex is OperationCanceledException ? "timed out" : ex.Message;
                    Log.Warn($"Listening: translation failed: {message}");
                    Failed?.Invoke($"translation {message}");
                }
            }

            context.Enqueue(line.Text);
            if (context.Count > 2)
                context.Dequeue();
            line.Done = true;
            Finished?.Invoke(line);
        }
    }
}
