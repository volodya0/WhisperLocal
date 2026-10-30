using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Notes;
using WhisperLocal.Pipeline;
using WhisperLocal.Stt;

namespace WhisperLocal.Ui.Pages;

/// <summary>
/// Notes settings (which folder, the file type of new notes), listening (macOS: the shortcut that takes notes of what
/// the Mac plays, the language heard and the translation language) and translation.
/// </summary>
internal sealed class NotesSettingsPage : Page
{
    private static readonly (string Title, string Extension)[] Formats = [("Markdown (.md)", ".md"), ("Plain text (.txt)", ".txt")];

    /// <summary>How quickly a line ends (Listen.PauseMs, Listen.MaxSegmentSec) and the text so far shows (Listen.ProvisionalMs).</summary>
    private static readonly (string Title, int PauseMs, int MaxSegmentSec, int ProvisionalMs, string Hint)[] Speeds =
    [
        ("Fast", 350, 6, 500, "Short lines, a moment after the words; a sentence may be split in two."),
        ("Balanced", 500, 10, 800, "Lines end at short pauses or every 10 seconds."),
        ("Accurate", 600, 20, 1000, "Whole sentences, up to 20 seconds; the most context for recognition."),
    ];

    /// <summary>How often the sentence being spoken is translated (Listen.TranslateWhileSpeakingMs).</summary>
    private static readonly (string Title, int Ms)[] LiveTranslations =
        [("Off: finished lines only", 0), ("Every 2 seconds", 2000), ("Every second", 1000), ("As fast as possible", 250)];

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
    private readonly ComboBox _speed = DarkTheme.Dropdown(190);
    private readonly ComboBox _liveTranslation = DarkTheme.Dropdown(190);
    private readonly TextBlock _modelMissing = DarkTheme.Label("", 12, brush: DarkTheme.Warning);
    private readonly Grid _modelMissingRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Button _showModels;
    private readonly Note _listenNote = new();
    private readonly TextBlock _speechModel = DarkTheme.MutedLabel("", 11);
    private readonly CheckBox _includeMe = DarkTheme.CheckBox("Include me: write down what I say into the microphone, as \"Me\"");
    private readonly CheckBox _separateSpeakers = DarkTheme.CheckBox("Tell the other speakers apart ([1], [2], …), each in its own color");
    private readonly TextBlock _voiceMissing = DarkTheme.Label("", 12, brush: DarkTheme.Warning);
    private readonly Grid _voiceMissingRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private bool? _shownIncludeMe;
    private bool? _shownSeparate;
    private readonly TextBlock _translationCloud = DarkTheme.MutedLabel("", 11);
    private string? _shownFolder;
    private string? _shownFormat;
    private string? _shownListen;
    private List<string> _heardCodes = [];
    private List<string> _translateCodes = [];
    private List<TranslationChoice> _modelChoices = [];
    private List<int> _liveTranslationValues = [];
    private bool _recording;
    private bool _updating;

    /// <param name="showModels">Opens the Models page, where a missing model is downloaded.</param>
    public NotesSettingsPage(IAppHost host, Action showModels) : base("Notes & Translate")
    {
        _host = host;
        _showModels = DarkTheme.Button("Models", showModels, small: true);
        _voiceMissingRow.Children.Add(_voiceMissing);
        var voiceModels = DarkTheme.Button("Models", showModels, small: true);
        Grid.SetColumn(voiceModels, 1);
        _voiceMissingRow.Children.Add(voiceModels);
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
        _translationModel.SelectionChanged += (_, _) => OnTranslationModelSelected();
        _includeMe.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
                Widgets.Save(_host, "Listen", "IncludeMe", _includeMe.IsChecked == true, _listenNote);
        };
        ToolTip.SetTip(_includeMe,
            "Your microphone is recorded along with what the Mac plays. Without headphones it also hears the speakers: " +
            "such lines are recognised as an echo and left out. Applies from the next listening.");
        _separateSpeakers.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
                Widgets.Save(_host, "Listen", "SeparateSpeakers", _separateSpeakers.IsChecked == true, _listenNote);
        };
        ToolTip.SetTip(_separateSpeakers,
            "A voice model on this Mac compares how the voices sound; a line is cut where another speaker starts. " +
            "Off: everyone the Mac plays is \"Them\". Applies from the next listening.");
        _voiceMissing.TextWrapping = TextWrapping.Wrap;
        _voiceMissing.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _speed.SelectionChanged += (_, _) =>
        {
            var i = _speed.SelectedIndex;
            if (_updating || i < 0 || i >= Speeds.Length)
                return;
            Widgets.Save(_host, "Listen", "PauseMs", Speeds[i].PauseMs, _listenNote);
            Widgets.Save(_host, "Listen", "MaxSegmentSec", Speeds[i].MaxSegmentSec, _listenNote);
            Widgets.Save(_host, "Listen", "ProvisionalMs", Speeds[i].ProvisionalMs, _listenNote);
        };
        _liveTranslation.SelectionChanged += (_, _) =>
        {
            var i = _liveTranslation.SelectedIndex;
            if (!_updating && i >= 0 && i < _liveTranslationValues.Count)
                Widgets.Save(_host, "Listen", "TranslateWhileSpeakingMs", _liveTranslationValues[i], _listenNote);
        };
        ToolTip.SetTip(_liveTranslation,
            "Translates the sentence while it is still being spoken, greyed, then the finished line replaces it. " +
            "Quickest with Gemini or Groq; the local model shares the computer with recognition");
        ToolTip.SetTip(_translationModel,
            "The language model that translates: a local one (the grammar check uses the same one), or a cloud one once its key is added on the Models page");

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
                _includeMe,
                _separateSpeakers,
                _voiceMissingRow,
                Widgets.Fields(("Heard in", _heard), ("Translate into", _translateTo)),
                Widgets.Fields(("Translation model", _translationModel)),
                Widgets.Fields(("Line speed", _speed)),
                Widgets.Fields(("Translate while speaking", _liveTranslation)),
                _modelMissingRow,
                _translationCloud,
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
        RefreshSpeakers(listen);
        var signature = $"{listen.Hotkey}|{listen.Language}|{listen.TranslateTo}|{string.Join(",", listen.Languages)}|{cfg.LocalLlm.ModelPath}|" +
                        $"{listen.TranslateEngine}|{cfg.Cloud.GeminiTextModel}|{cfg.Cloud.GroqTextModel}|" +
                        $"{cfg.Cloud.GeminiApiKey.Trim().Length > 0}|{cfg.Cloud.GroqApiKey.Trim().Length > 0}|" +
                        $"{listen.PauseMs}|{listen.MaxSegmentSec}|{listen.ProvisionalMs}|{listen.TranslateWhileSpeakingMs}";
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

        RefreshSpeed(listen);

        _modelChoices = TranslationChoices(cfg, out var index);
        _translationModel.ItemsSource = _modelChoices.Select(c => c.Title).ToList();
        _translationModel.SelectedIndex = index;

        _updating = false;
    }

    /// <summary>The line speed preset matching the config ("Custom" when edited by hand) and the live translation interval.</summary>
    private void RefreshSpeed(ListenConfig listen)
    {
        var titles = Speeds.Select(s => s.Title).ToList();
        var index = Array.FindIndex(Speeds, s => s.PauseMs == listen.PauseMs && s.MaxSegmentSec == listen.MaxSegmentSec && s.ProvisionalMs == listen.ProvisionalMs);
        if (index < 0)
        {
            titles.Add($"Custom: {listen.PauseMs} ms pause, {listen.MaxSegmentSec} s");
            index = titles.Count - 1;
        }
        _speed.ItemsSource = titles;
        _speed.SelectedIndex = index;
        ToolTip.SetTip(_speed, index < Speeds.Length ? Speeds[index].Hint : "Set in the config (Listen.PauseMs, Listen.MaxSegmentSec, Listen.ProvisionalMs).");

        var liveTitles = LiveTranslations.Select(l => l.Title).ToList();
        _liveTranslationValues = LiveTranslations.Select(l => l.Ms).ToList();
        var live = _liveTranslationValues.IndexOf(Math.Max(0, listen.TranslateWhileSpeakingMs));
        if (live < 0)
        {
            liveTitles.Add($"Every {listen.TranslateWhileSpeakingMs} ms");
            _liveTranslationValues.Add(listen.TranslateWhileSpeakingMs);
            live = liveTitles.Count - 1;
        }
        _liveTranslation.ItemsSource = liveTitles;
        _liveTranslation.SelectedIndex = live;
        // The sentence being spoken is translated only when its text is shown
        _liveTranslation.IsEnabled = listen.ProvisionalMs > 0;
    }

    // ---- Translation model: local language models and, with a key, cloud ones ----

    /// <summary>
    /// A translation model: local (<paramref name="Engine"/> "Local", <paramref name="Value"/> is LocalLlm.ModelPath)
    /// or cloud ("Gemini" or "Groq", <paramref name="Value"/> is Cloud.GeminiTextModel or Cloud.GroqTextModel).
    /// </summary>
    private sealed record TranslationChoice(string Title, string Engine, string Value);

    private static readonly (string Engine, string Vendor, string Property, (string Title, string Id)[] Models)[] CloudTextModels =
    [
        ("Gemini", "Google", "GeminiTextModel", [("Gemini Flash-Lite", "gemini-flash-lite-latest"), ("Gemini Flash", "gemini-flash-latest")]),
        ("Groq", "Groq", "GroqTextModel", [("Llama 3.3 70B", "llama-3.3-70b-versatile"), ("Llama 3.1 8B", "llama-3.1-8b-instant")]),
    ];

    private static string CloudKey(AppConfig c, string engine) => (engine == "Groq" ? c.Cloud.GroqApiKey : c.Cloud.GeminiApiKey).Trim();

    private static string CloudTextModel(AppConfig c, string engine) => engine == "Groq" ? c.Cloud.GroqTextModel : c.Cloud.GeminiTextModel;

    /// <summary>Every local model, the cloud ones with a key, and always the current choice (even without its key).</summary>
    private static List<TranslationChoice> TranslationChoices(AppConfig cfg, out int index)
    {
        var model = cfg.LocalLlm.ModelPath;
        var choices = LlmModels.Catalog.Select(m => new TranslationChoice($"{m.Title} · {m.Size}", "Local", "models/" + m.File)).ToList();
        if (LlmModels.Find(model) == null)
            choices.Add(new TranslationChoice($"Custom: {Path.GetFileName(model)}", "Local", model));
        foreach (var (engine, vendor, _, models) in CloudTextModels.Where(p => CloudKey(cfg, p.Engine).Length > 0))
            choices.AddRange(models.Select(m => new TranslationChoice($"{m.Title} · {vendor} cloud", engine, m.Id)));

        var wanted = CloudTextModels.FirstOrDefault(p => string.Equals(cfg.Listen.TranslateEngine, p.Engine, StringComparison.OrdinalIgnoreCase));
        if (wanted.Engine != null)
        {
            var current = CloudTextModel(cfg, wanted.Engine);
            index = choices.FindIndex(c => c.Engine == wanted.Engine && c.Value == current);
            if (index >= 0)
                return choices;
            var title = wanted.Models.FirstOrDefault(m => m.Id == current).Title ?? current;
            choices.Add(new TranslationChoice(CloudKey(cfg, wanted.Engine).Length > 0 ? $"{title} · {wanted.Vendor} cloud" : $"{title} · needs an API key",
                wanted.Engine, current));
            index = choices.Count - 1;
            return choices;
        }
        index = choices.FindIndex(c => c.Engine == "Local" && (c.Value == model || LlmModels.Find(c.Value) is { } m && m == LlmModels.Find(model)));
        return choices;
    }

    /// <summary>The engine follows the model; a local model is the same one the grammar check uses.</summary>
    private void OnTranslationModelSelected()
    {
        var i = _translationModel.SelectedIndex;
        if (_updating || i < 0 || i >= _modelChoices.Count)
            return;
        var choice = _modelChoices[i];
        if (CloudTextModels.FirstOrDefault(p => p.Engine == choice.Engine) is { Engine: not null } provider)
            Widgets.Save(_host, "Cloud", provider.Property, choice.Value, _listenNote);
        else
            Widgets.Save(_host, "LocalLlm", "ModelPath", choice.Value, _listenNote);
        Widgets.Save(_host, "Listen", "TranslateEngine", choice.Engine, _listenNote);
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

    /// <summary>
    /// The two speaker checkboxes, only on a change in the config (a click shows at once); and, checked every time,
    /// whether telling speakers apart waits for the voice model.
    /// </summary>
    private void RefreshSpeakers(ListenConfig listen)
    {
        _updating = true;
        if (listen.IncludeMe != _shownIncludeMe)
            _includeMe.IsChecked = _shownIncludeMe = listen.IncludeMe;
        if (listen.SeparateSpeakers != _shownSeparate)
            _separateSpeakers.IsChecked = _shownSeparate = listen.SeparateSpeakers;
        _updating = false;

        var missing = listen.SeparateSpeakers && !File.Exists(AppPaths.Resolve(listen.VoiceModelPath));
        _voiceMissingRow.IsVisible = missing;
        if (missing)
            _voiceMissing.Text = _host.DownloadProgress(listen.VoiceModelPath)
                                 ?? $"Telling speakers apart needs the voice model ({VoiceModels.Find(listen.VoiceModelPath)?.Size ?? "a model file"}): " +
                                 "download it on the Models page. Until then the others are all \"Them\".";
    }

    /// <summary>A translation language is chosen but its model is not downloaded: say so, with a way to Models.</summary>
    private void RefreshModelMissing(AppConfig cfg)
    {
        var translating = cfg.Listen.TranslateTo.Trim().Length > 0;
        var cloud = Translator.CloudName(cfg);
        var wanted = CloudTextModels.FirstOrDefault(p => string.Equals(cfg.Listen.TranslateEngine, p.Engine, StringComparison.OrdinalIgnoreCase));
        string? problem = null;
        if (wanted.Engine != null && cloud == null)
            problem = $"{wanted.Engine} needs an API key: add it on the Models page. Until then the local model translates.";
        else if (translating && cloud == null && !File.Exists(AppPaths.Resolve(cfg.LocalLlm.ModelPath)))
            problem = "The translation model is not downloaded yet: download it on the Models page.";
        _modelMissingRow.IsVisible = problem != null;
        _modelMissing.Text = problem ?? "";
        _translationCloud.IsVisible = cloud != null;
        _translationCloud.Text = $"The text of each line is sent to {(cloud == "Groq" ? "Groq" : "Google")} to translate; the audio stays on this Mac.";
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
