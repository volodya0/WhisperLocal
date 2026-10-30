using System.Globalization;
using System.Text;

namespace WhisperLocal.Pipeline;

/// <summary>
/// The note of one listening session, in the notes folder, laid out like the panel: every line as it is completed with
/// its time, the text (after who spoke, "[1]", when speakers are told apart) and the translation beside it. Markdown
/// (".md") gets a table; plain text gets columns padded with spaces, wrapped at <see cref="TextColumnWidth"/>, which
/// line up in a monospaced font. The translation column is always there, so a language chosen later from the panel's
/// menu still goes beside the text. Written line by line, so nothing is lost if the app quits; a session in which
/// nothing was heard leaves no file.
/// </summary>
public sealed class ListenNotes
{
    public const int TextColumnWidth = 56;
    private static readonly int[] Widths = [8, TextColumnWidth, TextColumnWidth];

    private readonly DateTime _started = DateTime.Now;
    private readonly bool _markdown;

    /// <param name="extension">".md" or ".txt" (Notes.Extension).</param>
    public ListenNotes(string folder, string extension)
    {
        _markdown = !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase);
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
            var title = $"Listening {_started.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}";
            sb.Append(_markdown ? $"# {title}\n\n" : $"{title}\n\n");
            sb.Append(_markdown
                ? "| Time | Text | Translation |\n|---|---|---|\n"
                : Row(["Time", "Text", "Translation"]) + Row(Widths.Select(w => new string('-', w)).ToList()));
        }

        var text = line.SpeakerName != null ? $"{line.SpeakerName} {line.Text}" : line.Text;
        List<string> cells = [Format(line.At), text, line.Translation ?? ""];
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

    /// <summary>A table cell: "|" would end it and a line break would end the row.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    /// <summary>The cells in padded columns, as many rows as the longest wrapped one needs.</summary>
    private static string TextRows(List<string> cells)
    {
        var wrapped = cells.Select((c, i) => Wrap(c, Widths[i])).ToList();
        var sb = new StringBuilder();
        for (var row = 0; row < wrapped.Max(c => c.Count); row++)
            sb.Append(Row(wrapped.Select(c => row < c.Count ? c[row] : "").ToList()));
        return sb.ToString();
    }

    /// <summary>Cells padded to their widths, two spaces apart; the last one is not padded.</summary>
    private static string Row(List<string> cells) =>
        string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(Widths[i]))).TrimEnd() + "\n";

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
