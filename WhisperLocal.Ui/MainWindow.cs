using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WhisperLocal.Config;
using WhisperLocal.Pipeline;

namespace WhisperLocal.Ui;

/// <summary>
/// The app's window: setup checklist, hotkey selector, model memory, files and recent dictations.
/// Closing only hides it; it refreshes itself while visible.
/// </summary>
public sealed class MainWindow : Window
{
    private const int HistoryRows = 10;

    private readonly IAppHost _host;
    private readonly TranscriptWindow _transcript;
    private readonly StackPanel _setup = new() { Spacing = 6 };
    private readonly ComboBox _hotkeys = new() { MinWidth = 300 };
    private readonly Button _record = new() { Content = "Record…" };
    private readonly TextBlock _hotkeyHint = Hint("");
    private readonly TextBlock _memory = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _memoryButton = new();
    private readonly Grid _stats = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto") };
    private readonly TextBlock _statsSince = Hint("");
    private readonly Grid _history = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto") };
    private readonly TextBlock _historySummary = Hint("");
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private List<HotkeyPreset> _hotkeyChoices = [];
    private string? _shownHotkey;
    private string? _shownSetup;
    private bool _updatingHotkeys;
    private bool _recording;
    private bool _historyDirty = true;
    private bool _allowClose;

    public MainWindow(IAppHost host, TranscriptWindow transcript)
    {
        _host = host;
        _transcript = transcript;
        Title = "WhisperLocal";
        Width = 720;
        Height = 760;
        MinWidth = 560;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24, 18, 24, 24),
                Spacing = 10,
                Children =
                {
                    Header(),
                    Section("Setup"), _setup,
                    Section("Hotkey"), HotkeySection(),
                    Section("Speech model"), MemorySection(),
                    Section("Files"), FilesSection(),
                    Section("Statistics"), _stats, _statsSince,
                    Section("Recent dictations"), _history, _historySummary,
                    Hint("WhisperLocal lives in the menu bar (tray on Windows). If you cannot see its icon, it may be hidden " +
                         "behind the notch or switched off in System Settings → Menu Bar. Open WhisperLocal again to get back here."),
                },
            },
        };

        _hotkeys.SelectionChanged += (_, _) => OnHotkeySelected();
        _record.Click += (_, _) => ToggleHotkeyRecording();
        _memoryButton.Click += async (_, _) => await ToggleMemoryAsync();
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
                    StopHotkeyRecording();
            }
            _host.MainWindowVisibilityChanged(IsVisible);
        };
    }

    /// <summary>True if the setup checklist has anything that is not OK.</summary>
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
        RefreshMemory();
        if (_historyDirty)
            RefreshHistory();
    }

    private Control Header()
    {
        var update = new Button { Content = "Check for Updates…", VerticalAlignment = VerticalAlignment.Center };
        update.Click += (_, _) => _host.CheckForUpdates();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(new TextBlock { Text = "WhisperLocal", FontSize = 22, FontWeight = FontWeight.Bold });
        var version = Hint($"Version {_host.VersionText}");
        version.VerticalAlignment = VerticalAlignment.Center;
        version.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(version, 1);
        Grid.SetColumn(update, 2);
        grid.Children.Add(version);
        grid.Children.Add(update);
        return grid;
    }

    private void RefreshSetup()
    {
        // Rebuild only on change: replacing a button under the mouse would swallow the click
        var items = _host.GetSetupItems();
        var signature = string.Join("\n", items.Select(i => $"{i.State}|{i.Text}|{i.ActionLabel}"));
        if (signature == _shownSetup)
            return;
        _shownSetup = signature;

        _setup.Children.Clear();
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto"), MinHeight = 30 };
            row.Children.Add(new TextBlock
            {
                Text = item.State switch
                {
                    SetupState.Ok => "✅",
                    SetupState.Warning => "⚠️",
                    SetupState.Pending => "⏳",
                    _ => "❌",
                },
                VerticalAlignment = VerticalAlignment.Center,
            });
            var text = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (item.ActionLabel != null && item.Action != null)
            {
                var button = new Button { Content = item.ActionLabel, MinWidth = 130, HorizontalContentAlignment = HorizontalAlignment.Center };
                var action = item.Action;
                button.Click += (_, _) =>
                {
                    action();
                    _shownSetup = null;
                    Refresh();
                };
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
            }
            _setup.Children.Add(row);
        }
    }

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
        _hotkeyHint.Text = $"Hold {_host.DescribeHotkey(current)}, speak, release. \"Record…\" takes any combination you hold.";
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
            preview => Dispatcher.UIThread.Post(() => _hotkeyHint.Text = $"Recording: {preview} … release to save"),
            key => Dispatcher.UIThread.Post(() =>
            {
                StopHotkeyRecording();
                if (key != null)
                    SaveHotkey(key);
            }));
        if (error != null)
        {
            _hotkeyHint.Text = error;
            return;
        }
        _recording = true;
        _record.Content = "Cancel";
        _hotkeyHint.Text = "Hold the keys you want to use, then release them. Esc cancels.";
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
            _hotkeyHint.Text = $"Saved: hold {_host.DescribeHotkey(key)} to dictate.";
        }
        catch (Exception ex)
        {
            Log.Error("Saving the hotkey failed", ex);
            _hotkeyHint.Text = $"Could not save the hotkey: {ex.Message}";
        }
    }

    private Control MemorySection()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_memory);
        Grid.SetColumn(_memoryButton, 1);
        _memoryButton.MinWidth = 130;
        _memoryButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        grid.Children.Add(_memoryButton);
        return grid;
    }

    private void RefreshMemory()
    {
        var (loaded, detail) = _host.GetModelMemory();
        _memory.Text = (loaded ? "🟢  Loaded in memory: " : "⚪️  Not loaded: ") + detail;
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
                _memory.Text = "⏳  Loading the model…";
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

    private Control FilesSection()
    {
        var config = _host.Config.FilePath;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                FileButton("Open Config", () => _host.OpenPath(config, textEditor: true)),
                FileButton("Open Log", () => _host.OpenPath(Log.CurrentFile, textEditor: true)),
                FileButton("Logs Folder", () => _host.OpenPath(AppPaths.LogDir)),
                FileButton("Data Folder", () => _host.OpenPath(AppPaths.DataDir)),
            },
        };
        var path = new SelectableTextBlock { Text = AppPaths.DataDir, Foreground = Brushes.Gray, FontSize = 12 };
        return new StackPanel { Spacing = 6, Children = { buttons, path } };
    }

    private static Button FileButton(string title, Action open)
    {
        var button = new Button { Content = title };
        button.Click += (_, _) => open();
        return button;
    }

    private void RefreshStats()
    {
        _stats.Children.Clear();
        _stats.RowDefinitions.Clear();

        var periods = new[] { ("Today", _host.Stats.Today()), ("Last 7 days", _host.Stats.LastDays(7)), ("All time", _host.Stats.AllTime()) };
        AddRow(_stats, 0, [Cell(""), .. periods.Select(p => (Control)new TextBlock { Text = p.Item1, FontWeight = FontWeight.SemiBold })]);
        AddRow(_stats, 1, [Cell("Dictations"), .. periods.Select(p => (Control)Cell(p.Item2.Dictations.ToString("N0")))]);
        AddRow(_stats, 2, [Cell("Words"), .. periods.Select(p => (Control)Cell(p.Item2.Words.ToString("N0")))]);
        AddRow(_stats, 3, [Cell("Speaking time"), .. periods.Select(p => (Control)Cell(FormatDuration(p.Item2.SpeakingSec)))]);
        AddRow(_stats, 4, [Cell("Speaking rate"), .. periods.Select(p => (Control)Cell(
            p.Item2.SpeakingSec > 0 ? $"{p.Item2.WordsPerMinute:F0} words/min" : "–"))]);

        var all = periods[2].Item2;
        // Typing at ~40 words/min is a common average; speaking is usually several times faster
        var typingSec = all.Words / 40.0 * 60;
        var saved = typingSec - all.SpeakingSec - all.ProcessingSec;
        _statsSince.Text = $"Counting since {_host.Stats.Since.ToLocalTime():d}." +
                           (saved > 60 ? $" Compared with typing at 40 words/min, about {FormatDuration(saved)} saved." : "");
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    private void RefreshHistory()
    {
        _historyDirty = false;
        RefreshStats();
        _history.Children.Clear();
        _history.RowDefinitions.Clear();

        var records = _host.History.Recent(HistoryRows);
        if (records.Count == 0)
        {
            _historySummary.Text = "No dictations yet.";
            return;
        }

        string[] headers = ["Time", "Recording", "Processing", "Recognition", "Words", "Text", ""];
        AddRow(_history, 0, headers.Select(h => (Control)new TextBlock { Text = h, FontWeight = FontWeight.SemiBold }).ToArray());
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var text = new TextBlock
            {
                Text = string.IsNullOrEmpty(r.Text) ? r.Note : r.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = string.IsNullOrEmpty(r.Text) ? Brushes.Gray : null,
            };
            ToolTip.SetTip(text, string.IsNullOrEmpty(r.App) ? r.Text : $"{r.App}: {r.Text}");

            Control show = new TextBlock();
            if (!string.IsNullOrEmpty(r.Text))
            {
                var button = new Button { Content = "Show", Padding = new Thickness(8, 1) };
                var full = r.Text;
                button.Click += (_, _) => _transcript.ShowText(full);
                show = button;
            }

            AddRow(_history, i + 1,
            [
                Cell(r.Time.ToLocalTime().ToString("HH:mm")),
                Cell($"{r.RecordSec:F1} s"),
                Cell($"{r.ProcessSec:F1} s"),
                Cell($"{r.SttSec:F1} s"),
                Cell(r.Words.ToString()),
                text,
                show,
            ]);
        }

        var spoken = records.Where(r => r.Words > 0).ToList();
        if (spoken.Count == 0)
        {
            _historySummary.Text = "";
            return;
        }
        var avgRecord = spoken.Average(r => r.RecordSec);
        var avgProcess = spoken.Average(r => r.ProcessSec);
        var avgStt = spoken.Average(r => r.SttSec);
        var speed = avgStt > 0 ? avgRecord / avgStt : 0;
        _historySummary.Inlines =
        [
            new Run($"Average of the last {spoken.Count}: recording {avgRecord:F1} s, processing {avgProcess:F1} s " +
                    $"(recognition {avgStt:F1} s, {speed:F0}× faster than real time), {spoken.Average(r => r.Words):F0} words."),
        ];
    }

    private static void AddRow(Grid grid, int row, Control[] cells)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var column = 0; column < cells.Length; column++)
        {
            var cell = cells[column];
            cell.Margin = new Thickness(0, 3, 14, 3);
            cell.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    private static TextBlock Cell(string text) => new() { Text = text };

    private static TextBlock Section(string title) =>
        new() { Text = title, FontSize = 15, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 2) };

    private static TextBlock Hint(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };

    public void CloseForGood()
    {
        _allowClose = true;
        _refresh.Stop();
        Close();
    }
}
