namespace WhisperLocal.Pipeline;

/// <summary>
/// Tells speakers apart as lines come, by their voiceprints: a line joins the speaker whose average voiceprint is
/// most alike (cosine similarity at least the threshold), otherwise it starts a new speaker. A short line's voiceprint
/// is unreliable: it joins the most alike speaker without starting one or changing the average (only the very first
/// voice heard starts a speaker however short, so a later voice is not mistaken for it).
/// </summary>
public sealed class SpeakerClusters(double threshold)
{
    private const int MaxSpeakers = 8;
    private const double ReliableSeconds = 1.5; // below this much speech a voiceprint only picks among the known speakers

    private readonly List<float[]> _sums = []; // per speaker: the sum of its normalized voiceprints

    /// <summary>The speaker of a line: 0, 1, ….</summary>
    public int Assign(float[] voiceprint, double seconds)
    {
        var v = Normalize(voiceprint);
        var best = -1;
        var bestSimilarity = double.MinValue;
        for (var i = 0; i < _sums.Count; i++)
        {
            var similarity = Dot(v, Normalize(_sums[i]));
            if (similarity > bestSimilarity)
                (best, bestSimilarity) = (i, similarity);
        }

        if (seconds < ReliableSeconds && best >= 0)
            return best;
        if (best >= 0 && (bestSimilarity >= threshold || _sums.Count >= MaxSpeakers))
        {
            var sum = _sums[best];
            for (var i = 0; i < sum.Length; i++)
                sum[i] += v[i];
            return best;
        }
        _sums.Add(v);
        return _sums.Count - 1;
    }

    /// <summary>Whether two voiceprints sound like different speakers (less alike than the threshold).</summary>
    public bool Differ(float[] a, float[] b) => Dot(Normalize(a), Normalize(b)) < threshold;

    private static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? (float[])v.Clone() : v.Select(x => (float)(x / norm)).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            sum += a[i] * b[i];
        return sum;
    }
}
