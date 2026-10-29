using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WhisperLocal.Notes;

namespace WhisperLocal.Ui.Pages;

/// <summary>The notes folder as a list, newest first; a note opens in the text editor.</summary>
internal sealed class NotesPage : Page
{
    private const int MaxNotes = 200;

    private readonly IAppHost _host;
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty = DarkTheme.MutedLabel("");
    private readonly SelectableTextBlock _folder = new() { Foreground = DarkTheme.Subtle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Note _note = new();
    private string? _shown;

    public NotesPage(IAppHost host) : base("Notes")
    {
        _host = host;
        _folder.Margin = new Thickness(0, 6, 0, 0);
        Add(DarkTheme.Section("Notes", new StackPanel { Spacing = 4, Children = { _list, _empty, _note.Line, _folder } },
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    DarkTheme.Button("New Note", NewNote, small: true),
                    DarkTheme.Button("Open Folder", OpenFolder, small: true),
                },
            }));
    }

    private string Folder => NoteFiles.Folder(_host.Config.Current.Notes);

    public override void Refresh()
    {
        var folder = Folder;
        // Listing a folder is cheap; the previews are read only when something changed
        var signature = folder + "\n" + NoteFiles.Signature(folder, MaxNotes);
        if (signature == _shown)
            return;
        _shown = signature;

        _folder.Text = folder;
        _list.Children.Clear();
        var notes = NoteFiles.List(folder, MaxNotes);
        _empty.IsVisible = notes.Count == 0;
        _empty.Text = "No notes yet. New Note creates one; any .md or .txt file in the folder shows up here.";
        for (var i = 0; i < notes.Count; i++)
        {
            if (i > 0)
                _list.Children.Add(DarkTheme.Separator());
            _list.Children.Add(Row(notes[i]));
        }
    }

    private Control Row(NoteFile note)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var title = new TextBlock
        {
            Text = note.Title,
            FontSize = 13,
            Foreground = DarkTheme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title } };
        if (note.Preview.Length > 0 && note.Preview != note.Title)
            texts.Children.Add(new TextBlock
            {
                Text = note.Preview,
                FontSize = 12,
                Foreground = DarkTheme.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        ToolTip.SetTip(texts, note.Path);
        row.Children.Add(texts);

        var date = note.Modified.Date;
        var when = DarkTheme.Label(
            date == DateTime.Today ? note.Modified.ToString("HH:mm")
            : date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : note.Modified.ToString("d"), 12, brush: DarkTheme.Subtle);
        when.Margin = new Thickness(14, 0, 8, 0);
        Grid.SetColumn(when, 1);
        row.Children.Add(when);

        var path = note.Path;
        var open = DarkTheme.Button("Open", () => _host.OpenPath(path, textEditor: true), small: true);
        Grid.SetColumn(open, 2);
        row.Children.Add(open);
        return row;
    }

    private void NewNote()
    {
        try
        {
            var path = NoteFiles.Create(Folder, _host.Config.Current.Notes.Extension);
            _host.OpenPath(path, textEditor: true);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Creating a note failed", ex);
            _note.Set($"Could not create a note: {ex.Message}", DarkTheme.Error);
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            _host.OpenPath(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _note.Set($"Could not open the folder: {ex.Message}", DarkTheme.Error);
        }
    }
}
