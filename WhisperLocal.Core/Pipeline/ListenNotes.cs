using System.Globalization;
using System.Text;

namespace WhisperLocal.Pipeline;

/// <summary>
/// The note of one listening session, in the notes folder, laid out like the panel: every line as it is completed with
/// its time, who spoke (when speakers are told apart) and, when translated, the translation beside it. Markdown (".md")
/// gets a table; plain text gets columns padded with spaces, wrapped at <see cref="TextColumnWidth"/>, which line up in
/// a monospaced font. Written line by line, so nothing is lost if the app quits; a session in which nothing was heard
/// leaves no file. The speaker column is there when the first line has a speaker.
/// </summary>
public sealed class ListenNotes
{
    public const int TextColumnWidth = 56;

    private readonly DateTime _started = DateTime.Now;
    private readonly bool _markdown;
    private readonly bool _translations;
    private bool _speakers;
    private int[] _widths = [];

    /// <param name="extension">".md" or ".txt" (Notes.Extension).</param>
    /// <param name="translating">A translation language is chosen: the translation column is there from the start.</param>
    public ListenNotes(string folder, string extension, bool translating)
    {
        _markdown = !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase);
        _translations = translating;
        var name = "Listening " + _started.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
        FilePath = Path.Combine(folder, name + (_markdown ? ".md" : ".txt"));
    }

    public string FilePath { get; }

    public int Lines { get; private set; }

    public void Add(ListenLine line)
    {
        var sb = new StringBuilder();
        if (Lines == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _speakers = line.SpeakerName != null;
            _widths = Columns(8, 10, TextColumnWidth, TextColumnWidth).ToArray();
            var title = $"Listening {_started.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}";
            sb.Append(_markdown ? $"# {title}\n\n" : $"{title}\n\n");
            var header = Columns("Time", "Speaker", "Text", "Translation");
            sb.Append(_markdown
                ? $"| {string.Join(" | ", header)} |\n|{string.Join("|", header.Select(_ => "---"))}|\n"
                : Row(header) + Row(_widths.Select(w => new string('-', w)).ToList()));
        }

        // A translation without its column (the language was chosen later) goes under the text
        var text = !_translations && line.Translation != null ? $"{line.Text} ↳ {line.Translation}" : line.Text;
        var cells = Columns(Format(line.At), line.SpeakerName ?? "", text, line.Translation ?? "");
        sb.Append(_markdown ? $"| {string.Join(" | ", cells.Select(Cell))} |\n" : TextRows(cells));
        File.AppendAllText(FilePath, sb.ToString());
        Lines++;
    }

    /// <summary>Adds the end time; does nothing if no line was written.</summary>
    public void Close()
    {
        if (Lines == 0)
            return;
        var duration = DateTime.Now - _started;
        File.AppendAllText(FilePath, $"\n{(_markdown ? "---\n" : "")}Ended {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)} · {Format(duration)}\n");
    }

    /// <summary>The cells (or widths) of the columns this note has: the time, the speaker, the text and the translation.</summary>
    private List<T> Columns<T>(T time, T speaker, T text, T translation)
    {
        var cells = new List<T> { time };
        if (_speakers)
            cells.Add(speaker);
        cells.Add(text);
        if (_translations)
            cells.Add(translation);
        return cells;
    }

    /// <summary>A table cell: "|" would end it and a line break would end the row.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    /// <summary>The cells in padded columns, as many rows as the longest wrapped one needs.</summary>
    private string TextRows(List<string> cells)
    {
        var wrapped = cells.Select((c, i) => Wrap(c, _widths[i])).ToList();
        var sb = new StringBuilder();
        for (var row = 0; row < wrapped.Max(c => c.Count); row++)
            sb.Append(Row(wrapped.Select(c => row < c.Count ? c[row] : "").ToList()));
        return sb.ToString();
    }

    /// <summary>Cells padded to their widths, two spaces apart; the last one is not padded.</summary>
    private string Row(List<string> cells) =>
        string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(_widths[i]))).TrimEnd() + "\n";

    /// <summary>Words wrapped into lines of at most <paramref name="width"/> characters (a longer word gets a line of its own).</summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0 || lines.Count == 0)
            lines.Add(current.ToString());
        return lines;
    }

    private static string Format(TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
