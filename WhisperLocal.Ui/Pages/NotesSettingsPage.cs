using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Notes;
using WhisperLocal.Stt;

namespace WhisperLocal.Ui.Pages;

/// <summary>
/// Notes settings (which folder, the file type of new notes), listening (macOS: the shortcut that takes notes of what
/// the Mac plays, the language heard and the translation language) and translation.
/// </summary>
internal sealed class NotesSettingsPage : Page
{
    private static readonly (string Title, string Extension)[] Formats = [("Markdown (.md)", ".md"), ("Plain text (.txt)", ".txt")];

    private readonly IAppHost _host;
    private readonly SelectableTextBlock _folder = new() { Foreground = DarkTheme.Text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _default;
    private readonly ComboBox _format = DarkTheme.Dropdown(170);
    private readonly Note _note = new();
    private readonly TextBlock _listenKey = DarkTheme.Label("", 12);
    private readonly Button _listenRecord;
    private readonly ComboBox _heard = DarkTheme.Dropdown(190);
    private readonly ComboBox _translateTo = DarkTheme.Dropdown(190);
    private readonly ComboBox _translationModel = DarkTheme.Dropdown(290);
    private readonly TextBlock _modelMissing = DarkTheme.Label("", 12, brush: DarkTheme.Warning);
    private readonly Grid _modelMissingRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Button _showModels;
    private readonly Note _listenNote = new();
    private readonly TextBlock _speechModel = DarkTheme.MutedLabel("", 11);
    private string? _shownFolder;
    private string? _shownFormat;
    private string? _shownListen;
    private List<string> _heardCodes = [];
    private List<string> _translateCodes = [];
    private List<string> _modelPaths = [];
    private bool _recording;
    private bool _updating;

    /// <param name="showModels">Opens the Models page, where a missing model is downloaded.</param>
    public NotesSettingsPage(IAppHost host, Action showModels) : base("Notes & Translate")
    {
        _host = host;
        _showModels = DarkTheme.Button("Models", showModels, small: true);
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
        _listenRecord = DarkTheme.Button("Record…", ToggleShortcutRecording, small: true);
        ToolTip.SetTip(_listenRecord, "Press any key combination to use it as the listening shortcut");
        if (OperatingSystem.IsMacOS())
            Add(DarkTheme.Section("Listening", ListeningSection()));

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

    private Control ListeningSection()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
            return DarkTheme.MutedLabel("Taking notes of what the Mac plays needs macOS 14.2 or later.");

        _heard.SelectionChanged += (_, _) =>
        {
            var i = _heard.SelectedIndex;
            if (!_updating && i >= 0 && i < _heardCodes.Count)
                Widgets.Save(_host, "Listen", "Language", _heardCodes[i], _listenNote);
        };
        _translateTo.SelectionChanged += (_, _) =>
        {
            var i = _translateTo.SelectedIndex;
            if (!_updating && i >= 0 && i < _translateCodes.Count)
                Widgets.Save(_host, "Listen", "TranslateTo", _translateCodes[i], _listenNote);
        };
        // The same language model as the grammar check: one model in memory serves both
        _translationModel.SelectionChanged += (_, _) =>
        {
            var i = _translationModel.SelectedIndex;
            if (!_updating && i >= 0 && i < _modelPaths.Count)
                Widgets.Save(_host, "LocalLlm", "ModelPath", _modelPaths[i], _listenNote);
        };
        ToolTip.SetTip(_translationModel, "The local language model that translates; the grammar check uses the same one");

        _modelMissing.TextWrapping = TextWrapping.Wrap;
        _modelMissing.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _modelMissingRow.Children.Add(_modelMissing);
        Grid.SetColumn(_showModels, 1);
        _modelMissingRow.Children.Add(_showModels);

        var shortcut = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        _listenKey.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        shortcut.Children.Add(_listenKey);
        shortcut.Children.Add(_listenRecord);
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                DarkTheme.MutedLabel("Press the shortcut to write down what the Mac plays (a call, a video) as a note here; " +
                                     "the panel's stop button ends it, and its menu changes the translation language."),
                Widgets.Fields(("Shortcut", shortcut)),
                Widgets.Fields(("Heard in", _heard), ("Translate into", _translateTo)),
                Widgets.Fields(("Translation model", _translationModel)),
                _modelMissingRow,
                _speechModel,
                _listenNote.Line,
            },
        };
    }

    public override void OnHidden()
    {
        base.OnHidden();
        if (!_recording)
            return;
        _host.CancelHotkeyRecording();
        StopShortcutRecording();
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
        RefreshListening();
    }

    private void RefreshListening()
    {
        var cfg = _host.Config.Current;
        var listen = cfg.Listen;
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
            return;
        RefreshModelMissing(cfg);
        RefreshSpeechModel(cfg);
        var signature = $"{listen.Hotkey}|{listen.Language}|{listen.TranslateTo}|{string.Join(",", listen.Languages)}|{cfg.LocalLlm.ModelPath}";
        if (signature == _shownListen || _recording)
            return;
        _shownListen = signature;
        _updating = true;

        _listenKey.Text = listen.Hotkey.Trim().Length == 0 ? "Off" : _host.DescribeHotkey(listen.Hotkey);

        // What is heard: detected, or one language (the codes Translate into offers, plus the current one)
        _heardCodes = ["auto", .. listen.Languages];
        var heard = listen.Language.Trim().ToLowerInvariant();
        if (!_heardCodes.Contains(heard))
            _heardCodes.Add(heard);
        _heard.ItemsSource = _heardCodes.Select(c => c == "auto" ? "Detect automatically" : Translator.EnglishName(c)).ToList();
        _heard.SelectedIndex = _heardCodes.IndexOf(heard);

        _translateCodes = ["", .. listen.Languages];
        var target = listen.TranslateTo.Trim();
        if (!_translateCodes.Contains(target))
            _translateCodes.Add(target);
        _translateTo.ItemsSource = _translateCodes.Select(c => c.Length == 0 ? "Original only" : Translator.NativeName(c)).ToList();
        _translateTo.SelectedIndex = _translateCodes.IndexOf(target);

        var model = cfg.LocalLlm.ModelPath;
        var titles = LlmModels.Catalog.Select(m => $"{m.Title} · {m.Size}").ToList();
        _modelPaths = LlmModels.Catalog.Select(m => "models/" + m.File).ToList();
        var index = LlmModels.Find(model) is { } known ? LlmModels.Catalog.ToList().IndexOf(known) : -1;
        if (index < 0)
        {
            titles.Add($"Custom: {Path.GetFileName(model)}");
            _modelPaths.Add(model);
            index = titles.Count - 1;
        }
        _translationModel.ItemsSource = titles;
        _translationModel.SelectedIndex = index;

        _updating = false;
    }

    /// <summary>Which speech model listening hears with: the local one set up in Speech to Text, even when dictation uses the cloud.</summary>
    private void RefreshSpeechModel(AppConfig cfg)
    {
        var path = cfg.Whisper.ModelPath;
        var name = WhisperModels.Find(path)?.Title ?? Path.GetFileName(path);
        var ready = File.Exists(AppPaths.Resolve(path));
        var cloud = RecognitionRouter.CloudName(cfg);
        _speechModel.Text = (cloud != null, ready) switch
        {
            (false, true) => $"Speech is recognised with {name}, the model set in Speech to Text; changing it there changes it here.",
            (true, true) => $"Speech is recognised on this computer with {name}, the local model set in Speech to Text " +
                            $"(dictation uses {cloud}; listening never sends audio to the cloud).",
            _ => $"Speech is recognised with {name}, the local model set in Speech to Text: it is not downloaded yet.",
        };
        _speechModel.Foreground = ready ? DarkTheme.Muted : DarkTheme.Warning;
    }

    /// <summary>A translation language is chosen but its model is not downloaded: say so, with a way to Models.</summary>
    private void RefreshModelMissing(AppConfig cfg)
    {
        var missing = cfg.Listen.TranslateTo.Trim().Length > 0 && !File.Exists(AppPaths.Resolve(cfg.LocalLlm.ModelPath));
        _modelMissingRow.IsVisible = missing;
        if (missing)
            _modelMissing.Text = "The translation model is not downloaded yet: download it on the Models page.";
    }

    private void ToggleShortcutRecording()
    {
        if (_recording)
        {
            _host.CancelHotkeyRecording();
            StopShortcutRecording();
            return;
        }

        var error = _host.StartHotkeyRecording(
            preview => Dispatcher.UIThread.Post(() => _listenNote.Set($"{preview} … release to save", DarkTheme.Accent, sticky: true)),
            key => Dispatcher.UIThread.Post(() =>
            {
                StopShortcutRecording();
                if (key == null)
                    return;
                _listenNote.Set("");
                Widgets.Save(_host, "Listen", "Hotkey", key, _listenNote);
            }));
        if (error != null)
        {
            _listenNote.Set(error, DarkTheme.Warning);
            return;
        }
        _recording = true;
        _listenRecord.Content = "Cancel";
        _listenNote.Set("Press the keys you want together, then release. Esc cancels.", DarkTheme.Accent, sticky: true);
    }

    private void StopShortcutRecording()
    {
        _recording = false;
        _listenRecord.Content = "Record…";
        _shownListen = null; // re-render
        Refresh();
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
