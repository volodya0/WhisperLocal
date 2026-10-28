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

namespace WhisperLocal.Ui;

/// <summary>
/// The app's window: status header, setup checklist, hotkey selector, model memory, continuation hints, statistics,
/// recent dictations and files. Dark, card-based. Closing only hides it; it refreshes itself while visible.
/// </summary>
public sealed class MainWindow : Window
{
    private const int HistoryRows = 8;

    private readonly IAppHost _host;
    private readonly TranscriptWindow _transcript;

    private readonly Ellipse _statusDot = DarkTheme.Dot(DarkTheme.Idle);
    private readonly TextBlock _status = DarkTheme.MutedLabel("", 13);
    private readonly StackPanel _setup = new() { Spacing = 4 };
    private readonly ComboBox _hotkeys = DarkTheme.Dropdown(260);
    private readonly Button _record;
    private readonly TextBlock _hotkeyHint = DarkTheme.MutedLabel("");
    private readonly ComboBox _languages = DarkTheme.Dropdown(260);
    private readonly TextBlock _languageHint = DarkTheme.MutedLabel("");
    private readonly Ellipse _memoryDot = DarkTheme.Dot(DarkTheme.Idle);
    private readonly TextBlock _memory = DarkTheme.Label("");
    private readonly TextBlock _memoryDetail = DarkTheme.MutedLabel("");
    private readonly Button _memoryButton;
    private readonly CheckBox _hintsEnabled = new()
    {
        Content = "Suggest how to continue after each dictation",
        FontSize = 13,
        Foreground = DarkTheme.Text,
    };
    private readonly ComboBox _hintModels = DarkTheme.Dropdown(260);
    private readonly TextBlock _hintHint = DarkTheme.MutedLabel("");
    private readonly StackPanel _hintStatus = new() { Spacing = 4 };
    private readonly Grid _statTiles = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
    private readonly TextBlock _statsNote = DarkTheme.MutedLabel("");
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
    private bool? _shownHintsEnabled;
    private bool _updatingHints;
    private List<string> _hintModelPaths = [];
    private bool _updatingHotkeys;
    private bool _recording;
    private bool _historyDirty = true;
    private int _statsPeriod; // 0 today, 1 last 7 days, 2 all time
    private bool _allowClose;

    public MainWindow(IAppHost host, TranscriptWindow transcript)
    {
        _host = host;
        _transcript = transcript;
        Title = "WhisperLocal";
        Width = 720;
        Height = 760;
        MinWidth = 600;
        MinHeight = 520;
        Background = DarkTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _record = DarkTheme.Button("Record…", ToggleHotkeyRecording);
        _memoryButton = DarkTheme.Button("", () => _ = ToggleMemoryAsync(), small: true);
        _memoryButton.MinWidth = 72;

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18, 14, 18, 18),
                Spacing = 10,
                Children =
                {
                    Header(),
                    DarkTheme.Section("Setup", _setup),
                    DarkTheme.Section("Hotkey", HotkeySection()),
                    DarkTheme.Section("Language", new StackPanel { Spacing = 6, Children = { _languages, _languageHint } }),
                    DarkTheme.Section("Speech model", MemorySection()),
                    DarkTheme.Section("Continuation hints", HintsSection()),
                    DarkTheme.Section("Statistics", StatsSection(),
                        DarkTheme.Segmented(["Today", "7 days", "All time"], _statsPeriod, i =>
                        {
                            _statsPeriod = i;
                            RefreshStats();
                        })),
                    DarkTheme.Section("Recent dictations", new StackPanel { Spacing = 4, Children = { _history, _historyNote } }),
                    DarkTheme.Section("Files", FilesSection()),
                },
            },
        };

        _hotkeys.SelectionChanged += (_, _) => OnHotkeySelected();
        _languages.SelectionChanged += (_, _) => OnLanguageSelected();
        _hintsEnabled.IsCheckedChanged += (_, _) => OnHintsToggled();
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
        var titles = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                DarkTheme.Label("WhisperLocal", 19, FontWeight.SemiBold),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _statusDot, _status } },
            },
        };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                DarkTheme.Label($"v{_host.VersionText}", 12, brush: DarkTheme.Subtle),
                DarkTheme.Button("Check for Updates", _host.CheckForUpdates, small: true),
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        var icon = DarkTheme.AppIcon(38);
        icon.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(icon);
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        Grid.SetColumn(right, 2);
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
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), MinHeight = 26 };
            row.Children.Add(DarkTheme.Dot(item.State switch
            {
                SetupState.Ok => DarkTheme.Ok,
                SetupState.Warning => DarkTheme.Warning,
                SetupState.Pending => DarkTheme.Accent,
                _ => DarkTheme.Error,
            }));
            var text = DarkTheme.Label(item.Text, brush: item.State == SetupState.Ok ? DarkTheme.Text : DarkTheme.Text);
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

    // ---- Hotkey ----

    private Control HotkeySection()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _hotkeys, _record } };
        return new StackPanel { Spacing = 6, Children = { row, _hotkeyHint } };
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
        SetHotkeyHint("Hold to record, release to transcribe. Record… takes any combination you hold.");
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
            preview => Dispatcher.UIThread.Post(() => SetHotkeyHint($"{preview} … release to save", DarkTheme.Accent)),
            key => Dispatcher.UIThread.Post(() =>
            {
                StopHotkeyRecording();
                if (key != null)
                    SaveHotkey(key);
            }));
        if (error != null)
        {
            SetHotkeyHint(error, DarkTheme.Warning);
            return;
        }
        _recording = true;
        _record.Content = "Cancel";
        SetHotkeyHint("Listening… hold the keys you want, then release. Esc cancels.", DarkTheme.Accent);
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
            ConfigEditor.SetValue(_host.Config.FilePath, "Hotkey", "Key", key);
            SetHotkeyHint($"Saved. Hold {_host.DescribeHotkey(key)} to dictate.", DarkTheme.Ok);
        }
        catch (Exception ex)
        {
            Log.Error("Saving the hotkey failed", ex);
            SetHotkeyHint($"Could not save the hotkey: {ex.Message}", DarkTheme.Error);
        }
    }

    private void SetHotkeyHint(string text, IBrush? brush = null)
    {
        _hotkeyHint.Text = text;
        _hotkeyHint.Foreground = brush ?? DarkTheme.Muted;
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
        _languageHint.Text = index < LanguagePresets.Length
            ? LanguagePresets[index].Hint
            : "Set in the config (Whisper.Language): a code like \"de\", or a list like \"uk,en,pl\".";
        _languageHint.Foreground = DarkTheme.Muted;
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
            _languageHint.Text = i < LanguagePresets.Length ? $"Saved. {LanguagePresets[i].Hint}" : "Saved.";
            _languageHint.Foreground = DarkTheme.Ok;
        }
        catch (Exception ex)
        {
            Log.Error("Saving the language failed", ex);
            _languageHint.Text = $"Could not save the language: {ex.Message}";
            _languageHint.Foreground = DarkTheme.Error;
        }
    }

    // ---- Speech model ----

    private Control MemorySection()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto") };
        grid.Children.Add(_memoryDot);
        var texts = new StackPanel { Spacing = 2, Children = { _memory, _memoryDetail } };
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(_memoryButton, 2);
        grid.Children.Add(_memoryButton);
        return grid;
    }

    private void RefreshMemory()
    {
        var (loaded, detail) = _host.GetModelMemory();
        _memoryDot.Fill = loaded ? DarkTheme.Ok : DarkTheme.Idle;
        _memory.Text = loaded ? "Loaded in memory" : "Not loaded";
        _memoryDetail.Text = detail;
        _memoryButton.Content = loaded ? "Unload" : "Load";
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

    // ---- Continuation hints ----

    private Control HintsSection()
    {
        var model = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { DarkTheme.Label("Model"), _hintModels },
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _hintsEnabled,
                DarkTheme.MutedLabel("A local language model offers a few ways to continue what you just dictated. " +
                                     "Option+1, 2, 3 or a click inserts one; any other key hides them."),
                model,
                _hintHint,
                _hintStatus,
            },
        };
    }

    private void RefreshHints()
    {
        var c = _host.Config.Current.Suggest;
        _updatingHints = true;
        // Only on a change in the config: a click shows at once, the config catches up after the save
        if (c.Enabled != _shownHintsEnabled)
        {
            _shownHintsEnabled = c.Enabled;
            _hintsEnabled.IsChecked = c.Enabled;
        }
        if (c.ModelPath != _shownHintModel)
        {
            _shownHintModel = c.ModelPath;
            var titles = LlmModels.Catalog.Select(m => $"{m.Title} · {m.Size}").ToList();
            _hintModelPaths = LlmModels.Catalog.Select(m => "models/" + m.File).ToList();
            var index = LlmModels.Find(c.ModelPath) is { } known ? LlmModels.Catalog.ToList().IndexOf(known) : -1;
            if (index < 0)
            {
                titles.Add($"Custom: {System.IO.Path.GetFileName(c.ModelPath)}");
                _hintModelPaths.Add(c.ModelPath);
                index = titles.Count - 1;
            }
            _hintModels.ItemsSource = titles;
            _hintModels.SelectedIndex = index;
            SetHintHint(LlmModels.Find(c.ModelPath)?.Hint ?? "Set in the config (Suggest.ModelPath): any instruction-tuned GGUF model.");
        }
        _updatingHints = false;

        _hintModels.IsEnabled = c.Enabled;
        _hintStatus.IsVisible = c.Enabled;
        if (c.Enabled)
            RenderItems(_hintStatus, _host.GetSuggestionItems(), ref _shownHintStatus);
    }

    private void OnHintsToggled()
    {
        if (_updatingHints)
            return;
        var enabled = _hintsEnabled.IsChecked == true;
        try
        {
            ConfigEditor.SetValue(_host.Config.FilePath, "Suggest", "Enabled", enabled);
            SetHintHint(enabled
                ? "Saved. Download the model below if it is not there yet; hints appear after your next dictation."
                : "Saved. Hints are off; the language model is unloaded.", DarkTheme.Ok);
        }
        catch (Exception ex)
        {
            Log.Error("Saving Suggest.Enabled failed", ex);
            SetHintHint($"Could not save: {ex.Message}", DarkTheme.Error);
        }
    }

    private void OnHintModelSelected()
    {
        var i = _hintModels.SelectedIndex;
        if (_updatingHints || i < 0 || i >= _hintModelPaths.Count)
            return;
        try
        {
            ConfigEditor.SetValue(_host.Config.FilePath, "Suggest", "ModelPath", _hintModelPaths[i]);
            SetHintHint(LlmModels.Find(_hintModelPaths[i])?.Hint ?? "Saved.");
        }
        catch (Exception ex)
        {
            Log.Error("Saving Suggest.ModelPath failed", ex);
            SetHintHint($"Could not save the model: {ex.Message}", DarkTheme.Error);
        }
    }

    private void SetHintHint(string text, IBrush? brush = null)
    {
        _hintHint.Text = text;
        _hintHint.Foreground = brush ?? DarkTheme.Muted;
    }

    // ---- Statistics ----

    private Control StatsSection() => new StackPanel { Spacing = 8, Children = { _statTiles, _statsNote } };

    private void RefreshStats()
    {
        var totals = _statsPeriod switch
        {
            0 => _host.Stats.Today(),
            1 => _host.Stats.LastDays(7),
            _ => _host.Stats.AllTime(),
        };

        _statTiles.Children.Clear();
        (string Value, string Label)[] tiles =
        [
            (totals.Dictations.ToString("N0"), "dictations"),
            (totals.Words.ToString("N0"), "words"),
            (FormatDuration(totals.SpeakingSec), "speaking"),
            (totals.SpeakingSec > 0 ? totals.WordsPerMinute.ToString("F0") : "–", "words / min"),
        ];
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    DarkTheme.Label(tiles[i].Value, 20, FontWeight.SemiBold),
                    DarkTheme.MutedLabel(tiles[i].Label),
                },
            };
            Grid.SetColumn(tile, i);
            _statTiles.Children.Add(tile);
        }

        var all = _host.Stats.AllTime();
        // Typing at ~40 words/min is a common average
        var saved = all.Words / 40.0 * 60 - all.SpeakingSec - all.ProcessingSec;
        _statsNote.Text = $"Counting since {_host.Stats.Since.ToLocalTime():d}" +
                          (saved > 60 ? $" · about {FormatDuration(saved)} saved compared with typing at 40 words/min" : "");
    }

    // ---- Recent dictations ----

    private void RefreshHistory()
    {
        _historyDirty = false;
        RefreshStats();
        _history.Children.Clear();

        var records = _host.History.Recent(HistoryRows);
        if (records.Count == 0)
        {
            _historyNote.Text = "Nothing yet. Hold the hotkey anywhere, speak, release.";
            return;
        }

        for (var i = 0; i < records.Count; i++)
        {
            if (i > 0)
                _history.Children.Add(DarkTheme.Separator());
            _history.Children.Add(HistoryRow(records[i]));
        }

        var spoken = records.Where(r => r.Words > 0).ToList();
        if (spoken.Count == 0)
        {
            _historyNote.Text = "";
            return;
        }
        var avgStt = spoken.Average(r => r.SttSec);
        var speed = avgStt > 0 ? spoken.Average(r => r.RecordSec) / avgStt : 0;
        _historyNote.Text = $"Average of the last {spoken.Count}: processing {spoken.Average(r => r.ProcessSec):F1} s, " +
                            $"recognition {avgStt:F1} s ({speed:F0}× real time)";
        _historyNote.Margin = new Thickness(0, 4, 0, 0);
    }

    private Control HistoryRow(DictationRecord r)
    {
        var hasText = !string.IsNullOrEmpty(r.Text);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("54,*,Auto,Auto") };

        row.Children.Add(DarkTheme.Label(r.Time.ToLocalTime().ToString("HH:mm"), 12, brush: DarkTheme.Subtle));

        var text = new TextBlock
        {
            Text = hasText ? r.Text : (r.Words > 0 ? $"{r.Words} words (text not kept)" : NoteText(r.Note)),
            FontSize = 13,
            Foreground = hasText ? DarkTheme.Text : DarkTheme.Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (hasText)
            ToolTip.SetTip(text, string.IsNullOrEmpty(r.App) ? r.Text : $"{r.App}: {r.Text}");
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var meta = DarkTheme.Label($"{r.RecordSec:F1} s · {r.ProcessSec:F1} s · {r.Words} w", 12, brush: DarkTheme.Subtle);
        meta.Margin = new Thickness(14, 0, 8, 0);
        ToolTip.SetTip(meta, $"Recording {r.RecordSec:F1} s, processing {r.ProcessSec:F1} s (recognition {r.SttSec:F1} s), {r.Words} words");
        Grid.SetColumn(meta, 2);
        row.Children.Add(meta);

        if (hasText)
        {
            var full = r.Text;
            var show = DarkTheme.Button("Show", () => _transcript.ShowText(full), small: true);
            Grid.SetColumn(show, 3);
            row.Children.Add(show);
        }
        return row;
    }

    private static string NoteText(string note) => string.IsNullOrEmpty(note) ? "No speech" : char.ToUpper(note[0]) + note[1..];

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

    // ---- Helpers ----

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    public void CloseForGood()
    {
        _allowClose = true;
        _refresh.Stop();
        Close();
    }
}
