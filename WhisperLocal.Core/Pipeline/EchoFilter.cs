using System.Text.RegularExpressions;
using WhisperLocal.Audio;

namespace WhisperLocal.Pipeline;

/// <summary>
/// One listening session's echo check. Without headphones the microphone also hears what the Mac plays; a microphone
/// segment is an echo when what the Mac played explains it (<see cref="IsEcho"/>): every moment the microphone is
/// loud, the system audio was loud a little earlier, by about the same ratio. The user speaking makes the microphone
/// louder than the echo could be, during a pause of the Mac or over it, so such a segment is kept. Recognized, a
/// microphone line can still repeat a line just heard from the Mac (<see cref="RepeatsLine"/>). Keeps the last
/// <see cref="HistorySec"/> of system audio and its lines for that.
/// </summary>
public sealed class EchoFilter
{
    private const int HistorySec = 60;
    private const int FrameSamples = 320; // 20 ms at 16 kHz
    private const int FrameMs = 20;
    private const int MaxLeadFrames = 10; // the microphone may be up to 200 ms ahead (capture latencies differ)
    private const int MaxLagFrames = 25; // ...or up to 500 ms behind (the room, the speakers' latency)
    private const int ReverbFrames = 3; // the room keeps a sound up to ~60 ms after the speakers stop
    // ~8 dB louder than the echo explains: the user is speaking. Leaning to keep: an echo kept by it is still dropped
    // when its words repeat the Mac's line, while a line of the user dropped by it would be lost. On test mixes it
    // dropped three in four stretches of pure echo, and kept the user's speech except when quieter than a loud echo
    private const double NearEndFactor = 2.5;
    private const int NearEndMs = 200; // this much of the user speaking keeps the segment
    private const double Silence = 0.003; // about -50 dBFS
    private static readonly TimeSpan LineWindow = TimeSpan.FromSeconds(10);

    private readonly MemoryStream _history = new();
    private TimeSpan _historyAt; // the time of the history's first byte since listening started
    private readonly List<ListenLine> _lines = [];

    /// <summary>Microphone segments and lines left out as an echo.</summary>
    public int Count { get; private set; }

    /// <summary>Keeps system audio, as it is captured, to compare the microphone with.</summary>
    public void RememberAudio(byte[] chunk)
    {
        _history.Write(chunk);
        var keep = AudioConvert.Bytes(HistorySec * 1000);
        if (_history.Length <= keep + AudioConvert.Bytes(10_000))
            return;
        var drop = (int)_history.Length - keep;
        var buffer = _history.GetBuffer();
        Buffer.BlockCopy(buffer, drop, buffer, 0, keep);
        _history.SetLength(keep);
        _historyAt += TimeSpan.FromMilliseconds(drop * 1000.0 / AudioConvert.TargetFormat.AverageBytesPerSecond);
    }

    /// <summary>Keeps a line heard from the Mac, to recognize a microphone line repeating it.</summary>
    public void RememberLine(ListenLine line)
    {
        _lines.Add(line);
        _lines.RemoveAll(l => line.At - l.At > TimeSpan.FromSeconds(HistorySec));
    }

    /// <summary>Whether microphone audio starting at <paramref name="at"/> is only an echo of what the Mac played then.</summary>
    public bool IsEcho(byte[] mic, TimeSpan at)
    {
        var start = (long)((at - _historyAt).TotalSeconds * AudioConvert.TargetFormat.AverageBytesPerSecond) & ~1L;
        if (start < 0 || start >= _history.Length)
            return false;
        // The system audio from up to MaxLagFrames earlier (a late echo) to MaxLeadFrames later (an early one)
        const int frameBytes = FrameSamples * 2;
        var leadFrames = (int)Math.Min(start / frameBytes, MaxLagFrames);
        var from = start - leadFrames * frameBytes;
        var micLength = (int)Math.Min(mic.Length, _history.Length - start);
        var systemLength = (int)Math.Min(_history.Length - from, (long)leadFrames * frameBytes + micLength + MaxLeadFrames * frameBytes);
        return NearEndSpeechMs(mic.AsSpan(0, micLength), _history.GetBuffer().AsSpan((int)from, systemLength), leadFrames) is { } ms
               && ms < NearEndMs;
    }

    /// <summary>
    /// Whether a microphone line from <paramref name="at"/> to <paramref name="end"/> repeats a line from the Mac heard
    /// around then (from 10 s before to 10 s after).
    /// </summary>
    public bool RepeatsLine(string text, TimeSpan at, TimeSpan end) =>
        _lines.Any(l => l.At >= at - LineWindow && l.At <= end + LineWindow && SameWords(text, l.Text));

    /// <summary>Counts a segment or line left out as an echo.</summary>
    public void Dropped() => Count++;

    /// <summary>
    /// How long the microphone is clearly louder than an echo of the system audio could make it, in ms; null when the
    /// Mac played nothing then (nothing to echo). Both are 16 kHz mono 16-bit PCM; <paramref name="system"/> starts
    /// <paramref name="leadFrames"/> frames before <paramref name="mic"/>.
    /// </summary>
    private static int? NearEndSpeechMs(ReadOnlySpan<byte> mic, ReadOnlySpan<byte> system, int leadFrames)
    {
        var m = AudioConvert.FrameRms(mic, FrameSamples);
        var s = AudioConvert.FrameRms(system, FrameSamples);
        if (m.Length < 10 || s.Length < 10 || s.Max() < Silence)
            return null;

        // The delay at which the loudness contours (in dB) line up best
        var mDb = m.Select(Db).ToArray();
        var sDb = s.Select(Db).ToArray();
        var (lag, best) = (0, double.MinValue);
        for (var l = -MaxLeadFrames; l <= MaxLagFrames; l++)
        {
            var c = Pearson(mDb, sDb, l - leadFrames);
            if (c > best)
                (lag, best) = (l, c);
        }

        // What the echo would be at each moment: the system audio that delay earlier, with the room's tail
        var expected = new double[m.Length];
        for (var i = 0; i < m.Length; i++)
            for (var k = i - lag + leadFrames - ReverbFrames; k <= i - lag + leadFrames + 1; k++)
                if (k >= 0 && k < s.Length)
                    expected[i] = Math.Max(expected[i], s[k]);

        // The echo's loudness relative to the system audio, from the moments the Mac plays loud: the user speaking
        // only raises the ratio, so the echo is near its low end
        var loud = expected.Max() / 4;
        var ratios = Enumerable.Range(0, m.Length).Where(i => expected[i] >= loud && expected[i] > Silence)
            .Select(i => m[i] / expected[i]).ToArray();
        Array.Sort(ratios);
        var gain = ratios.Length > 0 ? ratios[ratios.Length / 5] : 0;
        var sorted = (double[])m.Clone();
        Array.Sort(sorted);
        var noise = sorted[sorted.Length / 20];

        var nearEnd = 0;
        for (var i = 0; i < m.Length; i++)
            if (m[i] > Silence && m[i] > NearEndFactor * (gain * expected[i] + noise))
                nearEnd++;
        return nearEnd * FrameMs;
    }

    private static double Db(double rms) => 20 * Math.Log10(Math.Max(rms, 1e-3));

    /// <summary>Pearson correlation of mic[i] and system[i - lag] where both exist.</summary>
    private static double Pearson(double[] mic, double[] system, int lag)
    {
        var start = Math.Max(0, lag);
        var end = Math.Min(mic.Length, system.Length + lag);
        var n = end - start;
        if (n < 10)
            return 0;
        double sumM = 0, sumS = 0;
        for (var i = start; i < end; i++)
        {
            sumM += mic[i];
            sumS += system[i - lag];
        }
        double meanM = sumM / n, meanS = sumS / n, cov = 0, varM = 0, varS = 0;
        for (var i = start; i < end; i++)
        {
            var dm = mic[i] - meanM;
            var ds = system[i - lag] - meanS;
            cov += dm * ds;
            varM += dm * dm;
            varS += ds * ds;
        }
        return varM <= 0 || varS <= 0 ? 0 : cov / Math.Sqrt(varM * varS);
    }

    /// <summary>
    /// Whether most of the shorter line's words are in the other one. Lines under three words are never matched
    /// (a "yes" from both sides is not an echo).
    /// </summary>
    private static bool SameWords(string mic, string system)
    {
        var a = Words(mic);
        var b = Words(system);
        var shorter = Math.Min(a.Count, b.Count);
        return shorter >= 3 && a.Intersect(b).Count() >= 0.6 * shorter;
    }

    private static HashSet<string> Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value).ToHashSet();
}
