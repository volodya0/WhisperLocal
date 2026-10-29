using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WhisperLocal.Notes;

namespace WhisperLocal.Ui.Pages;

/// <summary>Notes settings (which folder, the file type of new notes) and translation.</summary>
internal sealed class NotesSettingsPage : Page
{
    private static readonly (string Title, string Extension)[] Formats = [("Markdown (.md)", ".md"), ("Plain text (.txt)", ".txt")];

    private readonly IAppHost _host;
    private readonly SelectableTextBlock _folder = new() { Foreground = DarkTheme.Text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _default;
    private readonly ComboBox _format = DarkTheme.Dropdown(170);
    private readonly Note _note = new();
    private string? _shownFolder;
    private string? _shownFormat;
    private bool _updating;

    public NotesSettingsPage(IAppHost host) : base("Notes & Translate")
    {
        _host = host;
        _default = DarkTheme.Button("Use Default", () => Widgets.Save(_host, "Notes", "Folder", "", _note), small: true);
        ToolTip.SetTip(_default, NoteFiles.DefaultFolder);

        var folder = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2, 0, 2) };
        var label = DarkTheme.Label("Folder");
        label.Margin = new Thickness(0, 0, 8, 0);
        folder.Children.Add(label);
        Grid.SetColumn(_folder, 1);
        _folder.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        folder.Children.Add(_folder);

        Add(DarkTheme.Section("Notes", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                folder,
                Widgets.Buttons(DarkTheme.Button("Change…", () => _ = PickFolderAsync(), small: true), _default),
                Widgets.Fields(("New notes as", _format)),
                _note.Line,
            },
        }));
        Add(DarkTheme.Section("Translation", DarkTheme.MutedLabel(
            "Dictate in one language and paste in another: comes with the cloud models.")));

        _format.ItemsSource = Formats.Select(f => f.Title).ToList();
        _format.SelectionChanged += (_, _) =>
        {
            var i = _format.SelectedIndex;
            if (!_updating && i >= 0)
                Widgets.Save(_host, "Notes", "Extension", Formats[i].Extension, _note);
        };
    }

    public override void Refresh()
    {
        var notes = _host.Config.Current.Notes;
        if (notes.Folder != _shownFolder)
        {
            _shownFolder = notes.Folder;
            _folder.Text = NoteFiles.Folder(notes);
            _default.IsVisible = notes.Folder.Trim().Length > 0;
        }
        if (notes.Extension != _shownFormat)
        {
            _shownFormat = notes.Extension;
            _updating = true;
            _format.SelectedIndex = Math.Max(0, Array.FindIndex(Formats, f => string.Equals(f.Extension, notes.Extension, StringComparison.OrdinalIgnoreCase)));
            _updating = false;
        }
    }

    private async Task PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var current = NoteFiles.Folder(_host.Config.Current.Notes);
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Notes folder",
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(current) ? await top.StorageProvider.TryGetFolderFromPathAsync(current) : null,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Widgets.Save(_host, "Notes", "Folder", path, _note);
    }
}
