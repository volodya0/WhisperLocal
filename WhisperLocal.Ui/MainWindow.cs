using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Pipeline;
using WhisperLocal.Stt;

namespace WhisperLocal.Ui;

/// <summary>
/// The app's window: status header, setup checklist, hotkey selector, model memory, language model,
/// the last few dictations (full history and statistics open in their own windows) and files.
/// Dark, card-based. Closing only hides it; it refreshes itself while visible.
/// </summary>
public sealed class MainWindow : Window
{
    private const int HistoryRows = 3;

    private readonly IAppHost _host;
    private readonly TranscriptWindow _transcript;
    private readonly HistoryWindow _historyWindow;
    private readonly StatsWindow _statsWindow;

    private readonly Ellipse _statusDot = DarkTheme.Dot(DarkTheme.Idle);
    private readonly TextBlock _status = DarkTheme.MutedLabel("", 13);
    private readonly StackPanel _setup = new() { Spacing = 2 };
    private readonly ComboBox _hotkeys = DarkTheme.Dropdown(228);
    private readonly Button _record;
    private readonly ComboBox _languages = DarkTheme.Dropdown(190);
    private readonly Note _rowNote = new();
    private readonly Ellipse _memoryDot = DarkTheme.Dot(DarkTheme.Idle);
    private readonly TextBlock _memory = DarkTheme.Label("", 12);
    private readonly TextBlock _memoryDetail = DarkTheme.MutedLabel("", 11);
    private readonly Button _memoryButton;
    private readonly ComboBox _speechModels = DarkTheme.Dropdown(250);
    private readonly Note _speechNote = new();
    private readonly CheckBox _polish = new()
    {
        Content = "Grammar check: after each dictation, propose a polished version",
        FontSize = 13,
        Foreground = DarkTheme.Text,
    };
    private readonly ComboBox _hintModels = DarkTheme.Dropdown(290);
    private readonly Note _hintNote = new();
    private readonly StackPanel _hintStatus = new() { Spacing = 2 };
    private readonly StackPanel _history = new();
    private readonly TextBlock _historyNote = DarkTheme.MutedLabel("");
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private List<HotkeyPreset> _hotkeyChoices = [];
    private string? _shownHotkey;
    private string? _shownLanguage;
    private bool _updatingLanguages;
    private List<(string Title, string Value)> _languageChoices = [];
    private string? _shownSetup;
    private string? _shownHintStatus;
    private string? _shownHintModel;
    private string? _shownSpeechModel;
    private List<string> _speechModelPaths = [];
    private bool? _shownPolish;
    private bool _updatingHints;
    private bool _updatingSpeechModels;
    private List<string> _hintModelPaths = [];
    private bool _updatingHotkeys;
    private bool _recording;
    private bool _historyDirty = true;
    private bool _allowClose;

    public MainWindow(IAppHost host, TranscriptWindow transcript)
    {
        _host = host;
        _transcript = transcript;
        _historyWindow = new HistoryWindow(host, transcript);
        _statsWindow = new StatsWindow(host);
        Title = "WhisperLocal";
        Icon = App.WindowIcon;
        Width = 720;
        Height = 760;
        MinWidth = 600;
        MinHeight = 520;
        Background = DarkTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _record = DarkTheme.Button("Record…", ToggleHotkeyRecording, small: true);
        _memoryButton = DarkTheme.Button("", () => _ = ToggleMemoryAsync(), small: true);
        _memoryButton.MinWidth = 72;
        var languageModel = DarkTheme.Section("Language model", LanguageModelSection());
        languageModel.IsVisible = host.HasGrammarCheck;

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                // The same space around the cards as between them
                Margin = new Thickness(10),
                Spacing = 10,
                Children =
                {
                    Header(),
                    DarkTheme.Section("Setup", _setup),
                    DarkTheme.Section("", HotkeyAndLanguageSection()),
                    DarkTheme.Section("Speech model", MemorySection()),
                    languageModel,
                    DarkTheme.Section("Recent dictations", new StackPanel { Spacing = 4, Children = { _history, _historyNote } },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children =
                            {
                                DarkTheme.Button("Statistics", _statsWindow.ShowAndActivate, small: true),
                                DarkTheme.Button("All history", _historyWindow.ShowAndActivate, small: true),
                            },
                        }),
                    DarkTheme.Section("Files", FilesSection()),
                },
            },
        };

        _hotkeys.SelectionChanged += (_, _) => OnHotkeySelected();
        _languages.SelectionChanged += (_, _) => OnLanguageSelected();
        _speechModels.SelectionChanged += (_, _) => OnSpeechModelSelected();
        _polish.IsCheckedChanged += (_, _) => OnPolishToggled();
        _hintModels.SelectionChanged += (_, _) => OnHintModelSelected();
        _refresh.Tick += (_, _) => Refresh();

        _host.History.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _historyDirty = true;
            if (IsVisible)
                RefreshHistory();
        });

        Closing += (_, e) =>
        {
            if (_allowClose)
                return;
            e.Cancel = true;
            Hide();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty)
                return;
            if (IsVisible)
            {
                Refresh();
                _refresh.Start();
            }
            else
            {
                _refresh.Stop();
                if (_recording)
                {
                    _host.CancelHotkeyRecording();
                    StopHotkeyRecording();
                }
            }
            _host.MainWindowVisibilityChanged(IsVisible);
        };
    }

    /// <summary>True if the setup checklist has anything that blocks dictation.</summary>
    public bool HasProblems => _host.GetSetupItems().Any(i => i.State is SetupState.Error);

    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void Refresh()
    {
        RefreshSetup();
        RefreshHotkey();
        RefreshLanguage();
        RefreshMemory();
        RefreshHints();
        if (_historyDirty)
            RefreshHistory();
    }

    // ---- Header ----

    private Control Header()
    {
        // One line: icon, name, status; version and updates on the right
        var title = DarkTheme.Label("WhisperLocal", 15, FontWeight.SemiBold);
        title.Margin = new Thickness(0, 0, 14, 0);
        // A grid, not a horizontal StackPanel: the status gets the remaining width and is cut with "…"
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = VerticalAlignment.Center };
        _statusDot.Margin = new Thickness(0, 0, 7, 0);
        status.Children.Add(_statusDot);
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_status, 1);
        status.Children.Add(_status);
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                DarkTheme.Label($"v{_host.VersionText}", 12, brush: DarkTheme.Subtle),
                DarkTheme.Button("Check for Updates", _host.CheckForUpdates, small: true),
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var icon = DarkTheme.AppIcon(24);
        icon.Margin = new Thickness(2, 0, 9, 0);
        grid.Children.Add(icon);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);
        return grid;
    }

    // ---- Setup ----

    private void RefreshSetup()
    {
        var items = _host.GetSetupItems();

        var problems = items.Count(i => i.State is SetupState.Error or SetupState.Warning);
        var hotkey = _host.DescribeHotkey(_host.Config.Current.Hotkey.Key);
        (_statusDot.Fill, _status.Text) = problems switch
        {
            0 when items.Any(i => i.State == SetupState.Pending) => (DarkTheme.Accent, "Almost ready…"),
            0 => (DarkTheme.Ok, $"Ready · hold {hotkey} to dictate"),
            1 => (DarkTheme.Warning, "1 thing needs attention"),
            _ => (DarkTheme.Warning, $"{problems} things need attention"),
        };

        RenderItems(_setup, items, ref _shownSetup);
    }

    /// <summary>Status rows with a dot and an optional fix-it button; rebuilt only when they change.</summary>
    private void RenderItems(StackPanel panel, IReadOnlyList<SetupItem> items, ref string? shown)
    {
        // Rebuild only on change: replacing a button under the mouse would swallow the click
        var signature = string.Join("\n", items.Select(i => $"{i.State}|{i.Text}|{i.ActionLabel}"));
        if (signature == shown)
            return;
        shown = signature;

        panel.Children.Clear();
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), MinHeight = 22 };
            row.Children.Add(DarkTheme.Dot(item.State switch
            {
                SetupState.Ok => DarkTheme.Ok,
                SetupState.Warning => DarkTheme.Warning,
                SetupState.Pending => DarkTheme.Accent,
                _ => DarkTheme.Error,
            }));
            var text = DarkTheme.Label(item.Text, 12);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (item.ActionLabel != null && item.Action != null)
            {
                var action = item.Action;
                var button = DarkTheme.Button(item.ActionLabel, () =>
                {
                    action();
                    _shownSetup = null;
                    _shownHintStatus = null;
                    Refresh();
                }, accent: item.State == SetupState.Error, small: true);
                button.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
            }
            panel.Children.Add(row);
        }
    }

    // ---- Hotkey and language ----

    private Control HotkeyAndLanguageSection()
    {
        ToolTip.SetTip(_record, "Hold any key combination to use it as the hotkey");
        return new StackPanel
        {
            Spacing = 4,
            Children = { Fields(("Hotkey", _hotkeys), ("", _record), ("Language", _languages)), _rowNote.Line },
        };
    }

    /// <summary>
    /// Labelled controls in one row, side by side while the window is wide enough and wrapping otherwise.
    /// An empty label puts the control right after the previous one.
    /// </summary>
    private static WrapPanel Fields(params (string Label, Control Control)[] fields)
    {
        var row = new WrapPanel();
        StackPanel? group = null;
        foreach (var (label, control) in fields)
        {
            if (label.Length > 0 || group == null)
            {
                group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 24, 2) };
                if (label.Length > 0)
                    group.Children.Add(DarkTheme.Label(label));
                row.Children.Add(group);
            }
            group.Children.Add(control);
        }
        return row;
    }

    /// <summary>A line for errors and prompts that hides itself after a few seconds; descriptions are tooltips.</summary>
    private sealed class Note
    {
        private readonly DispatcherTimer _clear = new() { Interval = TimeSpan.FromSeconds(5) };

        public Note()
        {
            Line.IsVisible = false;
            _clear.Tick += (_, _) => Set("");
        }

        public TextBlock Line { get; } = DarkTheme.MutedLabel("");

        /// <summary>"" hides the line; a sticky message stays until replaced.</summary>
        public void Set(string text, IBrush? brush = null, bool sticky = false)
        {
            _clear.Stop();
            Line.Text = text;
            Line.Foreground = brush ?? DarkTheme.Muted;
            Line.IsVisible = text.Length > 0;
            if (text.Length > 0 && !sticky)
                _clear.Start();
        }
    }

    private void RefreshHotkey()
    {
        var current = _host.Config.Current.Hotkey.Key;
        if (current == _shownHotkey || _recording)
            return;
        _shownHotkey = current;

        _hotkeyChoices = _host.HotkeyPresets.ToList();
        var index = _hotkeyChoices.FindIndex(p => SameHotkey(p.Key, current));
        if (index < 0)
        {
            _hotkeyChoices.Add(new HotkeyPreset($"Custom: {_host.DescribeHotkey(current)}", current));
            index = _hotkeyChoices.Count - 1;
        }

        _updatingHotkeys = true;
        _hotkeys.ItemsSource = _hotkeyChoices.Select(p => p.Title).ToList();
        _hotkeys.SelectedIndex = index;
        _updatingHotkeys = false;
        ToolTip.SetTip(_hotkeys, "Hold to record, release to transcribe. Record… takes any combination you hold.");
    }

    private bool SameHotkey(string a, string b) =>
        string.Equals(_host.DescribeHotkey(a), _host.DescribeHotkey(b), StringComparison.OrdinalIgnoreCase);

    private void OnHotkeySelected()
    {
        if (_updatingHotkeys || _hotkeys.SelectedIndex < 0 || _hotkeys.SelectedIndex >= _hotkeyChoices.Count)
            return;
        SaveHotkey(_hotkeyChoices[_hotkeys.SelectedIndex].Key);
    }

    private void ToggleHotkeyRecording()
    {
        if (_recording)
        {
            _host.CancelHotkeyRecording();
            StopHotkeyRecording();
            return;
        }

        var error = _host.StartHotkeyRecording(
            preview => Dispatcher.UIThread.Post(() => _rowNote.Set($"{preview} … release to save", DarkTheme.Accent, sticky: true)),
            key => Dispatcher.UIThread.Post(() =>
            {
                StopHotkeyRecording();
                if (key != null)
                    SaveHotkey(key);
            }));
        if (error != null)
        {
            _rowNote.Set(error, DarkTheme.Warning);
            return;
        }
        _recording = true;
        _record.Content = "Cancel";
        _rowNote.Set("Listening… hold the keys you want, then release. Esc cancels.", DarkTheme.Accent, sticky: true);
    }

    private void StopHotkeyRecording()
    {
        _recording = false;
        _record.Content = "Record…";
        _shownHotkey = null; // re-render
        RefreshHotkey();
    }

    private void SaveHotkey(string key)
    {
        try
        {
            // Clears the "release to save" prompt; the header says which keys to hold
            ConfigEditor.SetValue(_host.Config.FilePath, "Hotkey", "Key", key);
            _rowNote.Set("");
        }
        catch (Exception ex)
        {
            Log.Error("Saving the hotkey failed", ex);
            _rowNote.Set($"Could not save the hotkey: {ex.Message}", DarkTheme.Error);
        }
    }

    // ---- Language ----

    private static readonly (string Title, string Value, string Hint)[] LanguagePresets =
    [
        ("Ukrainian + English", "uk,en", "Detects which of the two you speak, every dictation."),
        ("Ukrainian + English + Russian", "uk,en,ru",
            "Detects which of the three you speak. Short Ukrainian phrases may occasionally come out in Russian."),
        ("Ukrainian only", "uk", "Always Ukrainian: English speech comes out translated into Ukrainian."),
        ("English only", "en", "Always English: other speech comes out translated into English."),
        ("Any language", "auto", "Detects any language; short phrases are occasionally misdetected."),
    ];

    private void RefreshLanguage()
    {
        var current = _host.Config.Current.Whisper.Language;
        if (current == _shownLanguage)
            return;
        _shownLanguage = current;

        _languageChoices = LanguagePresets.Select(p => (p.Title, p.Value)).ToList();
        var index = _languageChoices.FindIndex(c => SameLanguages(c.Value, current));
        if (index < 0)
        {
            _languageChoices.Add(($"Custom: {current}", current));
            index = _languageChoices.Count - 1;
        }

        _updatingLanguages = true;
        _languages.ItemsSource = _languageChoices.Select(c => c.Title).ToList();
        _languages.SelectedIndex = index;
        _updatingLanguages = false;
        ToolTip.SetTip(_languages, index < LanguagePresets.Length
            ? LanguagePresets[index].Hint
            : "Set in the config (Whisper.Language): a code like \"de\", or a list like \"uk,en,pl\".");
    }

    private static bool SameLanguages(string a, string b) =>
        string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private void OnLanguageSelected()
    {
        var i = _languages.SelectedIndex;
        if (_updatingLanguages || i < 0 || i >= _languageChoices.Count)
            return;
        try
        {
            ConfigEditor.SetValue(_host.Config.FilePath, "Whisper", "Language", _languageChoices[i].Value);
        }
        catch (Exception ex)
        {
            Log.Error("Saving the language failed", ex);
            _rowNote.Set($"Could not save the language: {ex.Message}", DarkTheme.Error);
        }
    }

    // ---- Speech model ----

    private Control MemorySection()
    {
        // One row: model choice, then whether it is loaded
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,18,*,Auto") };
        _speechModels.Margin = new Thickness(0, 0, 16, 0);
        grid.Children.Add(_speechModels);
        Grid.SetColumn(_memoryDot, 1);
        grid.Children.Add(_memoryDot);
        var texts = new StackPanel
        {
            Spacing = 1,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _memory, _memoryDetail },
        };
        Grid.SetColumn(texts, 2);
        grid.Children.Add(texts);
        Grid.SetColumn(_memoryButton, 3);
        grid.Children.Add(_memoryButton);
        return new StackPanel { Spacing = 4, Children = { grid, _speechNote.Line } };
    }

    private void RefreshMemory()
    {
        var path = _host.Config.Current.Whisper.ModelPath;
        if (path != _shownSpeechModel)
        {
            _shownSpeechModel = path;
            var titles = WhisperModels.Catalog.Select(m => $"{m.Title} · {m.Size}").ToList();
            _speechModelPaths = WhisperModels.Catalog.Select(m => "models/" + m.File).ToList();
            var known = WhisperModels.Find(path);
            var index = known != null ? WhisperModels.Catalog.ToList().IndexOf(known) : -1;
            if (index < 0)
            {
                titles.Add($"Custom: {System.IO.Path.GetFileName(path)}");
                _speechModelPaths.Add(path);
                index = titles.Count - 1;
            }
            _updatingSpeechModels = true;
            _speechModels.ItemsSource = titles;
            _speechModels.SelectedIndex = index;
            _updatingSpeechModels = false;
        }
        ToolTip.SetTip(_speechModels, WhisperModels.Find(path)?.Hint ?? "Set in the config (Whisper.ModelPath).");
        // Checked every time: the note goes away when the download finishes
        if (!File.Exists(AppPaths.Resolve(path)))
            _speechNote.Set(NotDownloaded, DarkTheme.Warning, sticky: true);
        else if (_speechNote.Line.Text == NotDownloaded)
            _speechNote.Set("");

        var (loaded, detail) = _host.GetModelMemory();
        _memoryDot.Fill = loaded ? DarkTheme.Ok : DarkTheme.Idle;
        _memory.Text = loaded ? "Loaded in memory" : "Not loaded";
        _memoryDetail.Text = detail;
        _memoryButton.Content = loaded ? "Unload" : "Load";
    }

    private const string NotDownloaded = "Not downloaded yet: press Download in Setup above.";

    private void OnSpeechModelSelected()
    {
        var i = _speechModels.SelectedIndex;
        if (_updatingSpeechModels || i < 0 || i >= _speechModelPaths.Count)
            return;
        try
        {
            // The hint below the dropdown follows on the next refresh; whisper-server restarts with it on the next dictation
            ConfigEditor.SetValue(_host.Config.FilePath, "Whisper", "ModelPath", _speechModelPaths[i]);
        }
        catch (Exception ex)
        {
            Log.Error("Saving Whisper.ModelPath failed", ex);
            _speechNote.Set($"Could not save the model: {ex.Message}", DarkTheme.Error);
        }
    }

    private async Task ToggleMemoryAsync()
    {
        _memoryButton.IsEnabled = false;
        try
        {
            if (_host.GetModelMemory().Loaded)
                await _host.UnloadAsync();
            else
            {
                _memoryDot.Fill = DarkTheme.Accent;
                _memory.Text = "Loading…";
                await _host.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Load/unload failed", ex);
        }
        finally
        {
            _memoryButton.IsEnabled = true;
            RefreshMemory();
        }
    }

    // ---- Language model: grammar check ----

    private Control LanguageModelSection() => new StackPanel
    {
        Spacing = 4,
        Children = { _polish, Fields(("Model", _hintModels)), _hintNote.Line, _hintStatus },
    };

    private void RefreshHints()
    {
        if (!_host.HasGrammarCheck)
            return;
        var cfg = _host.Config.Current;
        var model = cfg.LocalLlm.ModelPath;
        _updatingHints = true;
        // Only on a change in the config: a click shows at once, the config catches up after the save
        if (cfg.Polish.Enabled != _shownPolish)
        {
            _shownPolish = cfg.Polish.Enabled;
            _polish.IsChecked = cfg.Polish.Enabled;
        }
        if (model != _shownHintModel)
        {
            _shownHintModel = model;
            var titles = LlmModels.Catalog.Select(m => $"{m.Title} · {m.Size}").ToList();
            _hintModelPaths = LlmModels.Catalog.Select(m => "models/" + m.File).ToList();
            var index = LlmModels.Find(model) is { } known ? LlmModels.Catalog.ToList().IndexOf(known) : -1;
            if (index < 0)
            {
                titles.Add($"Custom: {System.IO.Path.GetFileName(model)}");
                _hintModelPaths.Add(model);
                index = titles.Count - 1;
            }
            _hintModels.ItemsSource = titles;
            _hintModels.SelectedIndex = index;
            ToolTip.SetTip(_hintModels, LlmModels.Find(model)?.Hint ?? "Set in the config (LocalLlm.ModelPath): any instruction-tuned GGUF model.");
        }
        _updatingHints = false;

        var used = cfg.Polish.Enabled;
        _hintModels.IsEnabled = used;
        _hintStatus.IsVisible = used;
        if (used)
            RenderItems(_hintStatus, _host.GetLanguageModelItems(), ref _shownHintStatus);
    }

    private void OnPolishToggled()
    {
        if (_updatingHints)
            return;
        var enabled = _polish.IsChecked == true;
        try
        {
            ConfigEditor.SetValue(_host.Config.FilePath, "Polish", "Enabled", enabled);
        }
        catch (Exception ex)
        {
            Log.Error("Saving Polish.Enabled failed", ex);
            _hintNote.Set($"Could not save: {ex.Message}", DarkTheme.Error);
        }
    }

    private void OnHintModelSelected()
    {
        var i = _hintModels.SelectedIndex;
        if (_updatingHints || i < 0 || i >= _hintModelPaths.Count)
            return;
        try
        {
            ConfigEditor.SetValue(_host.Config.FilePath, "LocalLlm", "ModelPath", _hintModelPaths[i]);
        }
        catch (Exception ex)
        {
            Log.Error("Saving LocalLlm.ModelPath failed", ex);
            _hintNote.Set($"Could not save the model: {ex.Message}", DarkTheme.Error);
        }
    }

    // ---- Recent dictations ----

    private void RefreshHistory()
    {
        _historyDirty = false;
        _history.Children.Clear();

        var records = _host.History.Recent(HistoryRows);
        _historyNote.IsVisible = records.Count == 0;
        _historyNote.Text = "Nothing yet. Hold the hotkey anywhere, speak, release.";
        for (var i = 0; i < records.Count; i++)
        {
            if (i > 0)
                _history.Children.Add(DarkTheme.Separator());
            _history.Children.Add(HistoryWindow.Row(records[i], _transcript));
        }
    }

    // ---- Files ----

    private Control FilesSection()
    {
        var config = _host.Config.FilePath;
        var buttons = new WrapPanel
        {
            Children =
            {
                DarkTheme.Button("Config", () => _host.OpenPath(config, textEditor: true), small: true),
                DarkTheme.Button("Log", () => _host.OpenPath(Log.CurrentFile, textEditor: true), small: true),
                DarkTheme.Button("Logs folder", () => _host.OpenPath(AppPaths.LogDir), small: true),
                DarkTheme.Button("Data folder", () => _host.OpenPath(AppPaths.DataDir), small: true),
            },
        };
        foreach (var child in buttons.Children)
            child.Margin = new Thickness(0, 0, 8, 0);
        var path = new SelectableTextBlock { Text = AppPaths.DataDir, Foreground = DarkTheme.Subtle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        return new StackPanel { Spacing = 6, Children = { buttons, path } };
    }

    public void CloseForGood()
    {
        _allowClose = true;
        _refresh.Stop();
        _historyWindow.CloseForGood();
        _statsWindow.CloseForGood();
        Close();
    }
}
