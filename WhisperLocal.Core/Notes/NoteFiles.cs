using WhisperLocal.Config;

namespace WhisperLocal.Notes;

/// <summary>A note on the Notes page: its file, name without the extension, last change and first line.</summary>
public sealed record NoteFile(string Path, string Title, DateTime Modified, string Preview);

/// <summary>The notes folder: plain .md and .txt files, listed newest first.</summary>
public static class NoteFiles
{
    public static readonly string[] Extensions = [".md", ".txt"];

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhisperLocal Notes");

    /// <summary>The configured folder ("~/" expanded), or <see cref="DefaultFolder"/> when none is set.</summary>
    public static string Folder(NotesConfig c)
    {
        var folder = c.Folder.Trim();
        if (folder.Length == 0)
            return DefaultFolder;
        if (folder == "~" || folder.StartsWith("~/") || folder.StartsWith("~\\"))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.TrimStart('~', '/', '\\'));
        return AppPaths.Resolve(folder);
    }

    /// <summary>The newest <paramref name="max"/> notes; none if the folder does not exist yet.</summary>
    public static IReadOnlyList<NoteFile> List(string folder, int max) =>
        Files(folder, max)
            .Select(f => new NoteFile(f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTime, Preview(f.FullName)))
            .ToList();

    /// <summary>Names and change times of the notes <see cref="List"/> would return, without reading them.</summary>
    public static string Signature(string folder, int max) =>
        string.Join("\n", Files(folder, max).Select(f => $"{f.Name}|{f.LastWriteTimeUtc.Ticks}"));

    private static List<FileInfo> Files(string folder, int max)
    {
        if (!Directory.Exists(folder))
            return [];
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && !f.Name.StartsWith('.'))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot list the notes in {folder}: {ex.Message}");
            return [];
        }
    }

    /// <summary>Creates an empty note named after the current time ("2026-10-29 14-05.md") and returns its path.</summary>
    public static string Create(string folder, string extension)
    {
        Directory.CreateDirectory(folder);
        var ext = Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? extension.ToLowerInvariant() : ".md";
        var name = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
        var path = Path.Combine(folder, name + ext);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}){ext}");
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>The first line with text, without Markdown heading marks; read from the start of the file only.</summary>
    private static string Preview(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[2048];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var line = new string(buffer, 0, read).Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0);
            return line ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
