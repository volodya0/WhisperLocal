using System.Text.Json;

namespace WhisperLocal.Pipeline;

/// <summary>Totals of a period: dictations with text, their words and speaking (hotkey hold) time.</summary>
public sealed record StatsTotals(int Dictations, long Words, double SpeakingSec, double ProcessingSec)
{
    public static readonly StatsTotals Zero = new(0, 0, 0, 0);

    /// <summary>Words per minute of speaking; 0 without data.</summary>
    public double WordsPerMinute => SpeakingSec > 0 ? Words / (SpeakingSec / 60) : 0;

    public StatsTotals Add(StatsTotals other) => new(
        Dictations + other.Dictations, Words + other.Words, SpeakingSec + other.SpeakingSec, ProcessingSec + other.ProcessingSec);
}

/// <summary>
/// Lifetime dictation statistics, per local day, in a small JSON file (unlike the history, never trimmed).
/// Only dictations that produced text are counted.
/// </summary>
public sealed class DictationStats
{
    private sealed class Data
    {
        public DateTime Since { get; set; } = DateTime.UtcNow;
        public SortedDictionary<string, StatsTotals> Days { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly object _sync = new();
    private readonly string _file;
    private readonly Data _data;

    /// <param name="seed">Used once, when the file does not exist yet (e.g. the history from before statistics existed).</param>
    public DictationStats(string file, IEnumerable<DictationRecord> seed)
    {
        _file = file;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Data>(File.ReadAllText(file)) is { } loaded)
            {
                _data = loaded;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"Cannot read the statistics, starting over: {ex.Message}");
        }

        _data = new Data();
        var records = seed.Where(r => r.Words > 0).OrderBy(r => r.Time).ToList();
        if (records.Count > 0)
            _data.Since = records[0].Time;
        foreach (var r in records)
            AddToDay(r);
        Save();
    }

    /// <summary>When counting started (the first dictation, or the first run).</summary>
    public DateTime Since
    {
        get
        {
            lock (_sync)
                return _data.Since;
        }
    }

    public void Add(DictationRecord record)
    {
        if (record.Words <= 0)
            return;
        lock (_sync)
        {
            AddToDay(record);
            Save();
        }
    }

    public StatsTotals Today() => Range(DateTime.Today, DateTime.Today);

    public StatsTotals LastDays(int days) => Range(DateTime.Today.AddDays(1 - days), DateTime.Today);

    public StatsTotals AllTime()
    {
        lock (_sync)
            return _data.Days.Values.Aggregate(StatsTotals.Zero, (sum, d) => sum.Add(d));
    }

    private StatsTotals Range(DateTime from, DateTime to)
    {
        var first = Key(from);
        var last = Key(to);
        lock (_sync)
        {
            return _data.Days
                .Where(d => string.CompareOrdinal(d.Key, first) >= 0 && string.CompareOrdinal(d.Key, last) <= 0)
                .Aggregate(StatsTotals.Zero, (sum, d) => sum.Add(d.Value));
        }
    }

    private void AddToDay(DictationRecord r)
    {
        var key = Key(r.Time.ToLocalTime());
        var day = _data.Days.GetValueOrDefault(key, StatsTotals.Zero);
        _data.Days[key] = day.Add(new StatsTotals(1, r.Words, r.RecordSec, r.ProcessSec));
    }

    private static string Key(DateTime localDate) => localDate.ToString("yyyy-MM-dd");

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex)
        {
            Log.Warn($"Cannot write the statistics: {ex.Message}");
        }
    }
}
