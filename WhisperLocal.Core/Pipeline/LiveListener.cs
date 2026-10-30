using System.Diagnostics;
using System.Threading.Channels;
using WhisperLocal.Audio;
using WhisperLocal.Cloud;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;
using WhisperLocal.Text;

namespace WhisperLocal.Pipeline;

/// <summary>Where a line was heard: in what the Mac plays, or in the microphone (the user).</summary>
public enum ListenSource { System, Microphone }

/// <summary>
/// One segment of what was heard: its time from the start, the text, who spoke and, once done, its translation.
/// </summary>
public sealed class ListenLine(TimeSpan at, string text, int? speaker = null, string? speakerName = null)
{
    public TimeSpan At { get; } = at;
    public string Text { get; } = text;

    /// <summary>0 for the user (the microphone), 1, 2, … for the speakers told apart; null for "Them" or no speakers.</summary>
    public int? Speaker { get; } = speaker;

    /// <summary>The short label in front of the line: "[Me]", "[Them]" or "[1]", "[2]", …; null when speakers are not told apart.</summary>
    public string? SpeakerName { get; } = speakerName;

    public string? Translation { get; set; }

    /// <summary>Recognized and, if wanted, translated: nothing more will change.</summary>
    public bool Done { get; set; }
}

/// <summary>
/// Listening: cuts the audio of what the Mac plays and, with Listen.IncludeMe, of the microphone into segments at pauses
/// (or every Listen.MaxSegmentSec), transcribes them one after another and, when <see cref="TranslateTo"/> is set,
/// translates each with the language model. Recognition and translation run as two stages, so a slow translation never
/// holds up the next segment's text, and up to two lines are translated at once (finished in order). While a segment
/// is still being spoken, it is transcribed about every Listen.ProvisionalMs as provisional text, whenever no finished
/// segment is waiting for recognition, and that text is translated at most every Listen.TranslateWhileSpeakingMs.
/// Microphone lines are "Me" (an echo of the speakers is dropped, see <see cref="EchoFilter"/>); the voices in what the
/// Mac plays are told apart by their voiceprints (<see cref="SpeakerClusters"/>), or are all "Them".
/// </summary>
public sealed class LiveListener
{
    private const int TickMs = 200;
    private const int MinSegmentMs = 1000;
    private const int PromptChars = 200;
    private const int ParallelTranslations = 2;
    private const int Me = 0;

    // Turn taking: the last VoiceWindowMs of what the Mac plays is compared with the start of the line about every
    // VoiceCheckMs; a different voice (similarity under Listen.SpeakerThreshold) ends the line where it starts
    private const int VoiceWindowMs = 1500;
    private const int VoiceCheckMs = 600;
    private const int VoiceSampleMs = 8000; // a line's voiceprint: enough to tell, and quick to compute

    private readonly ConfigManager _config;
    private readonly ISpeechToText _stt;
    private readonly Translator _translator;
    private readonly IVoiceprints? _voiceprints;
    private readonly TextFilters _filters = new();
    private volatile string _translateTo = "";

    /// <summary>One audio source being cut into segments: what was captured since the last cut, and its preview state.</summary>
    private sealed class Capture(ListenSource source, Func<byte[]> drain)
    {
        public ListenSource Source { get; } = source;
        public Func<byte[]> Drain { get; } = drain;
        public MemoryStream Buffer { get; } = new();
        public TimeSpan BufferAt; // time of the buffer's first byte since listening started
        public int Generation; // the segment being spoken
        public double ProvisionalUpToMs;
        public float[]? Voice; // the voiceprint of the segment's start, to notice the next speaker
        public double VoiceCheckedAtMs;
        public bool Speaking; // the segment has speech so far
    }

    private sealed record Segment(byte[] Pcm, TimeSpan At, ListenSource Source, int Generation);

    // All fields below are used on the caller's context
    private string _previous = ""; // recognition text so far, the prompt for the next segment
    private int _nextGeneration; // segments of both sources are numbered together
    private int _pendingSegments;
    private bool _provisionalBusy;
    private int _provisionalShown = -1; // generation of the provisional text on screen, -1 for none
    private ListenSource _provisionalSource;
    private long _provisionalAt;
    private bool _provisionalTranslateBusy;
    private long _provisionalTranslatedAt;
    private (string Text, int Generation)? _provisionalToTranslate; // the newest text, waiting for the one in flight
    private bool _strainShown; // "limit reached" was reported for the current strain
    private List<Capture> _captures = [];

    // Speakers of this session
    private SpeakerClusters? _clusters; // null: the others are all "Them"
    private int _lastOther = 1;
    private EchoFilter _echo = new();
    private readonly List<(Segment Segment, long HeldAt)> _heldMicrophone = []; // waiting for the system audio of the same moments

    public LiveListener(ConfigManager config, ISpeechToText stt, Translator translator, IVoiceprints? voiceprints = null)
    {
        _config = config;
        _stt = stt;
        _translator = translator;
        _voiceprints = voiceprints;
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

    /// <summary>The translation of the provisional text; null when it is gone (with the provisional text).</summary>
    public event Action<string?>? ProvisionalTranslation;

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled, then finishes the segments already heard before returning.
    /// The events are raised on the caller's context.
    /// </summary>
    /// <param name="drainSystem">What the Mac played since the previous call, 16 kHz mono 16-bit PCM.</param>
    /// <param name="drainMicrophone">
    /// The same for the microphone, on the system audio's timeline (silence for the time it took to start); null to
    /// leave the user out.
    /// </param>
    public async Task RunAsync(Func<byte[]> drainSystem, Func<byte[]>? drainMicrophone, CancellationToken ct)
    {
        var cfg = _config.Current;
        _previous = "";
        _nextGeneration = 0;
        _pendingSegments = 0;
        _provisionalShown = -1;
        _provisionalToTranslate = null;
        _captures = [new Capture(ListenSource.System, drainSystem) { Generation = _nextGeneration++ }];
        if (drainMicrophone != null)
            _captures.Add(new Capture(ListenSource.Microphone, drainMicrophone) { Generation = _nextGeneration++ });
        _clusters = cfg.Listen.SeparateSpeakers && _voiceprints is { IsAvailable: true } ? new SpeakerClusters(cfg.Listen.SpeakerThreshold) : null;
        _lastOther = 1;
        _echo = new EchoFilter();
        _heldMicrophone.Clear();

        var segments = Channel.CreateUnbounded<Segment>();
        var lines = Channel.CreateUnbounded<ListenLine>();
        var recognize = RecognizeAsync(segments.Reader, lines.Writer);
        var translate = TranslateAsync(lines.Reader);
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
                foreach (var capture in _captures)
                    await CutAsync(capture, segments.Writer, final: false);
                ReleaseMicrophone(segments.Writer, all: false);
            }
            foreach (var capture in _captures)
                await CutAsync(capture, segments.Writer, final: true);
            ReleaseMicrophone(segments.Writer, all: true);
        }
        finally
        {
            segments.Writer.TryComplete();
            await recognize;
            await translate;
            if (_echo.Count > 0)
                Log.Info($"Listening: {_echo.Count} microphone segments were an echo of the speakers");
        }
    }

    /// <summary>
    /// Takes what <paramref name="capture"/> heard since the last tick and cuts a segment at a pause, where another
    /// speaker starts, at Listen.MaxSegmentSec or (<paramref name="final"/>) at the end; otherwise maybe starts a
    /// provisional pass.
    /// </summary>
    private async Task CutAsync(Capture capture, ChannelWriter<Segment> segments, bool final)
    {
        var chunk = capture.Drain();
        capture.Buffer.Write(chunk);
        if (capture.Source == ListenSource.System)
            _echo.RememberAudio(chunk);
        var cfg = _config.Current;
        var pcm = capture.Buffer.ToArray();
        var durationMs = AudioConvert.DurationMs(pcm);
        var level = AudioConvert.SpeechLevelDbfs(pcm);

        if (final)
        {
            if (durationMs >= 500 && level >= cfg.Filter.MinSpeechDbfs)
                Send(capture, segments, pcm);
            else
            {
                ClearProvisional(capture.Generation, capture.Source);
                NextSegment(capture);
            }
            return;
        }
        capture.Speaking = durationMs >= MinSegmentMs && level >= cfg.Filter.MinSpeechDbfs;
        if (durationMs < MinSegmentMs)
            return;
        if (level < cfg.Filter.MinSpeechDbfs)
        {
            // Only silence so far: keep the last moment, where the next speech may be starting
            if (durationMs > 1500)
                capture.BufferAt += Keep(capture.Buffer, pcm, 300);
            return;
        }

        if (capture.Source == ListenSource.System && _clusters != null && await SpeakerChangedAsync(capture, pcm, durationMs, level))
        {
            // The line so far goes; from the quietest moment of the window (the gap between turns, if any), the new
            // speaker's start stays for the next one
            var split = QuietestPoint(pcm, pcm.Length - AudioConvert.Bytes(VoiceWindowMs), pcm.Length - AudioConvert.Bytes(VoiceWindowMs / 3));
            Send(capture, segments, pcm[..split]);
            capture.Buffer.Write(pcm, split, pcm.Length - split);
            return;
        }

        // Quieter than the speech by 20 dB counts as silence, whatever the volume
        var silenceMs = AudioConvert.TrailingSilenceMs(pcm, Math.Max(level - 20, -65));
        var pause = silenceMs >= cfg.Listen.PauseMs && durationMs - silenceMs >= 500;
        if (pause || durationMs >= cfg.Listen.MaxSegmentSec * 1000.0)
            Send(capture, segments, pcm);
        else if (cfg.Listen.ProvisionalMs > 0 && !_provisionalBusy && _pendingSegments == 0
                 && Stopwatch.GetElapsedTime(_provisionalAt).TotalMilliseconds >= cfg.Listen.ProvisionalMs
                 && durationMs - silenceMs > capture.ProvisionalUpToMs + 300
                 // One preview at a time: the other side's, while the Mac speaks
                 && !(capture.Source == ListenSource.Microphone && _captures[0].Speaking))
        {
            // New speech since the last provisional pass (none for the microphone hearing only the speakers)
            capture.ProvisionalUpToMs = durationMs - silenceMs;
            if (!(capture.Source == ListenSource.Microphone && _echo.IsEcho(pcm, capture.BufferAt)))
                _ = ProvisionalAsync(pcm, capture.Generation, capture.Source);
        }
    }

    /// <summary>
    /// Whether the last <see cref="VoiceWindowMs"/> sound like another speaker than the start of the segment. The start's
    /// voiceprint is taken once the segment has speech enough; the check runs about every <see cref="VoiceCheckMs"/>.
    /// </summary>
    private async Task<bool> SpeakerChangedAsync(Capture capture, byte[] pcm, double durationMs, double level)
    {
        if (durationMs < 2 * VoiceWindowMs || durationMs - capture.VoiceCheckedAtMs < VoiceCheckMs)
            return false;
        capture.VoiceCheckedAtMs = durationMs;
        var window = AudioConvert.Bytes(VoiceWindowMs);
        var minLevel = Math.Max(level - 20, _config.Current.Filter.MinSpeechDbfs);
        try
        {
            if (capture.Voice == null)
            {
                var start = pcm[..window];
                if (AudioConvert.SpeechLevelDbfs(start) >= minLevel)
                    capture.Voice = await Task.Run(() => _voiceprints!.Compute(start));
                return false;
            }
            var recent = pcm[^window..];
            return AudioConvert.SpeechLevelDbfs(recent) >= minLevel && await Task.Run(() => _voiceprints!.Compute(recent)) is { } print
                && _clusters!.Differ(capture.Voice, print);
        }
        catch (Exception ex)
        {
            Log.Debug($"Listening: voiceprint failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The byte offset of the quietest 20 ms between <paramref name="from"/> and <paramref name="to"/> (both even).</summary>
    private static int QuietestPoint(byte[] pcm, int from, int to)
    {
        const int frameSamples = 320;
        var rms = AudioConvert.FrameRms(pcm.AsSpan(from, to - from), frameSamples);
        return from + Array.IndexOf(rms, rms.Min()) * frameSamples * 2;
    }

    /// <summary>
    /// Queues a cut segment for recognition (unless it is the microphone hearing the speakers) and starts the
    /// capture's next segment.
    /// </summary>
    private void Send(Capture capture, ChannelWriter<Segment> segments, byte[] pcm)
    {
        var at = capture.BufferAt;
        var generation = capture.Generation;
        capture.BufferAt += TimeSpan.FromMilliseconds(AudioConvert.DurationMs(pcm));
        capture.Buffer.SetLength(0);
        NextSegment(capture);
        if (capture.Source == ListenSource.Microphone && _echo.IsEcho(pcm, at))
        {
            _echo.Dropped();
            ClearProvisional(generation, capture.Source);
            return;
        }
        _pendingSegments++;
        var segment = new Segment(pcm, at, capture.Source, generation);
        if (capture.Source == ListenSource.Microphone)
            _heldMicrophone.Add((segment, Stopwatch.GetTimestamp()));
        else
            segments.TryWrite(segment);
    }

    /// <summary>
    /// Queues the microphone segments whose moments the system audio has been cut past, so the system lines of the same
    /// moments are recognized first and an echo repeating one is noticed (<see cref="EchoFilter.RepeatsLine"/>). A
    /// segment waits at most a little longer than the longest system segment; <paramref name="all"/> at the end.
    /// </summary>
    private void ReleaseMicrophone(ChannelWriter<Segment> segments, bool all)
    {
        var systemAt = _captures[0].BufferAt;
        var maxWait = TimeSpan.FromSeconds(_config.Current.Listen.MaxSegmentSec + 2);
        while (_heldMicrophone.Count > 0)
        {
            var (segment, heldAt) = _heldMicrophone[0];
            var end = segment.At + TimeSpan.FromMilliseconds(AudioConvert.DurationMs(segment.Pcm));
            if (!all && end > systemAt && Stopwatch.GetElapsedTime(heldAt) < maxWait)
                return;
            _heldMicrophone.RemoveAt(0);
            segments.TryWrite(segment);
        }
    }

    /// <summary>The capture starts a new segment: a provisional pass of the previous one still running is stale.</summary>
    private void NextSegment(Capture capture)
    {
        capture.Generation = _nextGeneration++;
        capture.ProvisionalUpToMs = 0;
        capture.Voice = null; // the next segment takes its own
        capture.VoiceCheckedAtMs = 0;
    }

    /// <summary>Drops all but the last <paramref name="keepMs"/> of the buffer; returns the duration dropped.</summary>
    private static TimeSpan Keep(MemoryStream buffer, byte[] pcm, int keepMs)
    {
        var keep = AudioConvert.Bytes(keepMs);
        buffer.SetLength(0);
        buffer.Write(pcm, pcm.Length - keep, keep);
        return TimeSpan.FromMilliseconds(AudioConvert.DurationMs(pcm) - keepMs);
    }

    /// <summary>The languages a source is recognized in: what the Mac plays in Listen.Language, the user in their dictation languages.</summary>
    private string Language(ListenSource source) =>
        source == ListenSource.Microphone ? _config.Current.Whisper.Language : _config.Current.Listen.Language;

    private bool IsCurrent(int generation) => _captures.Any(c => c.Generation == generation);

    /// <summary>Transcribes the segment being spoken; shown only if that segment is still the current one.</summary>
    private async Task ProvisionalAsync(byte[] pcm, int generation, ListenSource source)
    {
        _provisionalBusy = true;
        _provisionalAt = Stopwatch.GetTimestamp();
        try
        {
            var cfg = _config.Current;
            var text = await _stt.TranscribeAsync(AudioConvert.ToWav(pcm), CancellationToken.None, Language(source), Prompt());
            text = _filters.RemoveHallucinations(text, cfg);
            if (!IsCurrent(generation) || text.Length == 0)
                return;
            if (_provisionalShown != generation)
                ProvisionalTranslation?.Invoke(null); // the translation of another segment
            _provisionalShown = generation;
            _provisionalSource = source;
            Provisional?.Invoke(text);
            TranslateProvisional(text, generation);
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

    /// <summary>Removes the provisional text if it belongs to <paramref name="generation"/> or an earlier segment of the same source.</summary>
    private void ClearProvisional(int generation, ListenSource source)
    {
        if (_provisionalShown < 0 || (_provisionalShown != generation && (_provisionalSource != source || _provisionalShown > generation)))
            return;
        _provisionalShown = -1;
        _provisionalToTranslate = null;
        Provisional?.Invoke(null);
        ProvisionalTranslation?.Invoke(null);
    }

    /// <summary>
    /// Translates the provisional text when Listen.TranslateWhileSpeakingMs has passed since the last request and none
    /// is in flight; otherwise keeps it as the next one to translate. A result is shown only while its segment is still
    /// the one being spoken.
    /// </summary>
    private void TranslateProvisional(string text, int generation)
    {
        var target = TranslateTo;
        var intervalMs = _config.Current.Listen.TranslateWhileSpeakingMs;
        if (target.Length == 0 || intervalMs <= 0)
            return;
        // Near the provider's limit: finished lines go first, the preview waits until it has room again
        if (Translator.CloudName(_config.Current) is { } cloud && CloudUsage.IsStrained(cloud))
        {
            if (!_strainShown)
                Failed?.Invoke($"{cloud} limit nearly reached: live translation paused");
            _strainShown = true;
            return;
        }
        _strainShown = false;
        if (_provisionalTranslateBusy || Stopwatch.GetElapsedTime(_provisionalTranslatedAt).TotalMilliseconds < intervalMs)
        {
            _provisionalToTranslate = (text, generation);
            return;
        }
        _ = TranslateProvisionalAsync(text, generation, target);
    }

    private async Task TranslateProvisionalAsync(string text, int generation, string target)
    {
        _provisionalTranslateBusy = true;
        _provisionalTranslatedAt = Stopwatch.GetTimestamp();
        _provisionalToTranslate = null;
        try
        {
            var translation = await _translator.TranslateAsync(text, target, Prompt(), CancellationToken.None);
            if (IsCurrent(generation) && _provisionalShown == generation && translation != null)
                ProvisionalTranslation?.Invoke(translation);
        }
        catch (Exception ex)
        {
            // The finished line reports failures; this one is only a preview
            Log.Debug($"Listening: provisional translation failed: {ex.Message}");
        }
        finally
        {
            _provisionalTranslateBusy = false;
        }
        // The text moved on while this one was translated: the newest text, once the interval allows
        if (_provisionalToTranslate is { } next && IsCurrent(next.Generation))
        {
            var wait = _config.Current.Listen.TranslateWhileSpeakingMs - Stopwatch.GetElapsedTime(_provisionalTranslatedAt).TotalMilliseconds;
            if (wait > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(wait));
            if (_provisionalToTranslate is { } newest && IsCurrent(newest.Generation) && !_provisionalTranslateBusy)
                TranslateProvisional(newest.Text, newest.Generation);
        }
    }

    // The text before this segment helps Whisper continue a sentence cut in the middle
    private string Prompt() => _previous.Length > PromptChars ? _previous[^PromptChars..] : _previous;

    private async Task RecognizeAsync(ChannelReader<Segment> segments, ChannelWriter<ListenLine> lines)
    {
        try
        {
            await foreach (var segment in segments.ReadAllAsync())
            {
                try
                {
                    var cfg = _config.Current;
                    var text = await _stt.TranscribeAsync(AudioConvert.ToWav(segment.Pcm), CancellationToken.None, Language(segment.Source), Prompt());
                    text = _filters.RemoveHallucinations(text, cfg);
                    ClearProvisional(segment.Generation, segment.Source);
                    if (text.Length == 0)
                        continue;
                    if (segment.Source == ListenSource.Microphone
                        && _echo.RepeatsLine(text, segment.At, segment.At + TimeSpan.FromMilliseconds(AudioConvert.DurationMs(segment.Pcm))))
                    {
                        _echo.Dropped();
                        continue;
                    }
                    var speaker = await SpeakerAsync(segment);
                    if (cfg.Logging.LogText)
                        Log.Info($"Heard{(speaker.Name != null ? $" ({speaker.Name})" : "")}: {text}");
                    _previous = (_previous + " " + text).Trim();
                    var line = new ListenLine(segment.At, text, speaker.Index, speaker.Name);
                    if (segment.Source == ListenSource.System)
                        _echo.RememberLine(line);
                    Heard?.Invoke(line);
                    lines.TryWrite(line);
                }
                catch (Exception ex)
                {
                    ClearProvisional(segment.Generation, segment.Source);
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

    /// <summary>
    /// Who said a segment: the user for the microphone; for what the Mac plays, the speaker its voiceprint belongs to
    /// (taken from at most <see cref="VoiceSampleMs"/> in the middle; a segment too short to tell keeps the previous
    /// speaker), or "Them" without telling voices apart.
    /// </summary>
    private async Task<(int? Index, string? Name)> SpeakerAsync(Segment segment)
    {
        // Lines carry a speaker with the microphone or with speakers told apart
        if (_captures.Count == 1 && _clusters == null)
            return (null, null);
        if (segment.Source == ListenSource.Microphone)
            return (Me, "[Me]");
        if (_clusters == null)
            return (null, "[Them]");
        try
        {
            var sample = segment.Pcm.Length <= AudioConvert.Bytes(VoiceSampleMs)
                ? segment.Pcm
                : segment.Pcm.AsSpan((segment.Pcm.Length - AudioConvert.Bytes(VoiceSampleMs)) / 2 & ~1, AudioConvert.Bytes(VoiceSampleMs)).ToArray();
            if (await Task.Run(() => _voiceprints!.Compute(sample)) is { } print)
                _lastOther = _clusters.Assign(print, AudioConvert.DurationMs(segment.Pcm) / 1000) + 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"Listening: voiceprint failed: {ex.Message}");
        }
        return (_lastOther, $"[{_lastOther}]");
    }

    /// <summary>
    /// Translates the recognized lines, up to <see cref="ParallelTranslations"/> at once, and finishes them in order: a
    /// slow answer does not hold up the requests for the lines after it, only their showing.
    /// </summary>
    private async Task TranslateAsync(ChannelReader<ListenLine> lines)
    {
        var started = Channel.CreateUnbounded<(ListenLine Line, Task<string?>? Translation)>();
        var finish = FinishAsync(started.Reader);
        using var slots = new SemaphoreSlim(ParallelTranslations);
        var context = new Queue<string>();
        try
        {
            await foreach (var line in lines.ReadAllAsync())
            {
                var target = TranslateTo;
                Task<string?>? translation = null;
                if (target.Length > 0)
                {
                    await slots.WaitAsync();
                    translation = TranslateLineAsync(line, target, string.Join(" ", context), slots);
                }
                started.Writer.TryWrite((line, translation));
                context.Enqueue(line.Text);
                if (context.Count > 2)
                    context.Dequeue();
            }
        }
        finally
        {
            started.Writer.TryComplete();
            await finish;
        }
    }

    /// <summary>The translation of one line, or null (none needed, or it failed: reported, the line stays as heard).</summary>
    private async Task<string?> TranslateLineAsync(ListenLine line, string target, string context, SemaphoreSlim slots)
    {
        try
        {
            var translation = await _translator.TranslateAsync(line.Text, target, context, CancellationToken.None);
            // Already in that language: nothing to add
            return translation != null && !string.Equals(translation.TrimEnd('.'), line.Text.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                ? translation
                : null;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "timed out" : ex.Message;
            Log.Warn($"Listening: translation failed: {message}");
            Failed?.Invoke($"translation {message}");
            return null;
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task FinishAsync(ChannelReader<(ListenLine Line, Task<string?>? Translation)> started)
    {
        await foreach (var (line, translation) in started.ReadAllAsync())
        {
            if (translation != null)
                line.Translation = await translation;
            line.Done = true;
            Finished?.Invoke(line);
        }
    }
}
