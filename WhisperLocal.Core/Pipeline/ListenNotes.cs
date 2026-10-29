using System.Globalization;
using System.Text;

namespace WhisperLocal.Pipeline;

/// <summary>
/// The note of one listening session, in the notes folder: every line as it is completed, each with its time and,
/// when translated, the translation under it; Markdown for ".md", plain text otherwise. Written line by line, so
/// nothing is lost if the app quits; a session in which nothing was heard leaves no file.
/// </summary>
public sealed class ListenNotes
{
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
        }
        sb.Append(_markdown ? $"**{Format(line.At)}** {line.Text}" : $"[{Format(line.At)}] {line.Text}");
        // Markdown: two trailing spaces make a line break inside the paragraph
        if (line.Translation != null)
            sb.Append(_markdown ? $"  \n↳ {line.Translation}" : $"\n↳ {line.Translation}");
        sb.Append("\n\n");
        File.AppendAllText(FilePath, sb.ToString());
        Lines++;
    }

    /// <summary>Adds the end time; does nothing if no line was written.</summary>
    public void Close()
    {
        if (Lines == 0)
            return;
        var duration = DateTime.Now - _started;
        File.AppendAllText(FilePath, $"{(_markdown ? "---" : "")}\nEnded {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)} · {Format(duration)}\n");
    }

    private static string Format(TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
