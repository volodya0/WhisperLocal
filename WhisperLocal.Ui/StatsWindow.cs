using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace WhisperLocal.Ui;

/// <summary>Dictation statistics for today, the last 7 days or all time. Closing only hides it.</summary>
public sealed class StatsWindow : Window
{
    private readonly IAppHost _host;
    private readonly Grid _tiles = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
    private readonly TextBlock _note = DarkTheme.MutedLabel("");
    private int _period; // 0 today, 1 last 7 days, 2 all time
    private bool _allowClose;

    public StatsWindow(IAppHost host)
    {
        _host = host;
        Title = "Statistics";
        Width = 560;
        Height = 220;
        MinWidth = 440;
        MinHeight = 180;
        Background = DarkTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _note.TextWrapping = TextWrapping.Wrap;
        Content = new StackPanel
        {
            Margin = new Thickness(10),
            Children =
            {
                DarkTheme.Section("Statistics", new StackPanel { Spacing = 8, Children = { _tiles, _note } },
                    DarkTheme.Segmented(["Today", "7 days", "All time"], _period, i =>
                    {
                        _period = i;
                        Refresh();
                    })),
            },
        };

        _host.History.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                Refresh();
        });

        Closing += (_, e) =>
        {
            if (_allowClose)
                return;
            e.Cancel = true;
            Hide();
        };
    }

    public void ShowAndActivate()
    {
        // "Today" moves past midnight: recount on every show
        Refresh();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Refresh()
    {
        var totals = _period switch
        {
            0 => _host.Stats.Today(),
            1 => _host.Stats.LastDays(7),
            _ => _host.Stats.AllTime(),
        };

        _tiles.Children.Clear();
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
            _tiles.Children.Add(tile);
        }

        var all = _host.Stats.AllTime();
        // Typing at ~40 words/min is a common average
        var saved = all.Words / 40.0 * 60 - all.SpeakingSec - all.ProcessingSec;
        _note.Text = $"Counting since {_host.Stats.Since.ToLocalTime():d}" +
                     (saved > 60 ? $" · about {FormatDuration(saved)} saved compared with typing at 40 words/min" : "");
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

    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }
}
