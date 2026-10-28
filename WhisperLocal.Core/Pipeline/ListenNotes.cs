using System.Globalization;
using System.Text;

namespace WhisperLocal.Pipeline;

/// <summary>
/// The notes file of one listening session: a Markdown file with every line as it is completed, each with its time
/// and, when translated, the translation under it. Written line by line, so nothing is lost if the app quits; a
/// session in which nothing was heard leaves no file.
/// </summary>
public sealed class ListenNotes
{
    private readonly DateTime _started = DateTime.Now;

    public ListenNotes(string folder)
    {
        FilePath = Path.Combine(folder, _started.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + ".md");
    }

    public string FilePath { get; }

    public int Lines { get; private set; }

    public void Add(ListenLine line)
    {
        var sb = new StringBuilder();
        if (Lines == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            sb.Append($"# Notes {_started.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}\n\n");
        }
        sb.Append($"**{Format(line.At)}** {line.Text}");
        // Two trailing spaces: a line break inside the paragraph
        if (line.Translation != null)
            sb.Append($"  \n↳ {line.Translation}");
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
        File.AppendAllText(FilePath, $"---\nEnded {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)} · {Format(duration)}\n");
    }

    private static string Format(TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
