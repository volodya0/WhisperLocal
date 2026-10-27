using System.Text.Json;

namespace WhisperLocal.Pipeline;

/// <param name="RecordSec">How long the hotkey was held.</param>
/// <param name="ProcessSec">From releasing the hotkey until the text was inserted.</param>
/// <param name="SttSec">Speech recognition (whisper-server) alone.</param>
public sealed record DictationRecord(
    DateTime Time, double RecordSec, double ProcessSec, double SttSec, int Words, string App, string Text, string Note);

/// <summary>The last dictations with their timings, kept in a small JSON-lines file between runs.</summary>
public sealed class DictationHistory
{
    private const int Keep = 20;

    private readonly object _sync = new();
    private readonly string _file;
    private readonly List<DictationRecord> _items = [];

    /// <summary>Raised on the thread that called <see cref="Add"/>.</summary>
    public event Action? Changed;

    public DictationHistory(string file)
    {
        _file = file;
        try
        {
            if (!File.Exists(file))
                return;
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    if (JsonSerializer.Deserialize<DictationRecord>(line) is { } record)
                        _items.Add(record);
                }
                catch (JsonException)
                {
                    // A damaged line loses one record, not the history
                }
            }
            if (_items.Count > Keep)
                _items.RemoveRange(0, _items.Count - Keep);
        }
        catch (IOException ex)
        {
            Log.Warn($"Cannot read the dictation history: {ex.Message}");
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<DictationRecord> Recent(int count)
    {
        lock (_sync)
            return _items.AsEnumerable().Reverse().Take(count).ToList();
    }

    public void Add(DictationRecord record)
    {
        lock (_sync)
        {
            _items.Add(record);
            if (_items.Count > Keep)
                _items.RemoveRange(0, _items.Count - Keep);
            try
            {
                File.WriteAllLines(_file, _items.Select(r => JsonSerializer.Serialize(r)));
            }
            catch (IOException ex)
            {
                Log.Warn($"Cannot write the dictation history: {ex.Message}");
            }
        }
        Changed?.Invoke();
    }
}
