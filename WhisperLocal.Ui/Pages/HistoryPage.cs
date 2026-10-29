using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WhisperLocal.Pipeline;

namespace WhisperLocal.Ui.Pages;

/// <summary>
/// Statistics for a period on top, then all kept dictations grouped by day with average timings; the newest page
/// first, "Show more" adds the next.
/// </summary>
internal sealed class HistoryPage : Page
{
    private const int PageSize = 100;

    private readonly IAppHost _host;
    private readonly TranscriptWindow _transcript;
    private readonly Grid _tiles = new();
    private readonly TextBlock _statsNote = DarkTheme.MutedLabel("");
    private readonly StackPanel _list = new();
    private readonly TextBlock _note = DarkTheme.MutedLabel("");
    private readonly Button _more;
    private int _period; // 0 today, 1 last 7 days, 2 last 30 days, 3 all time
    private int _shown = PageSize;
    private bool _dirty = true;
    private DateTime _shownDay;

    public HistoryPage(IAppHost host, TranscriptWindow transcript) : base("History")
    {
        _host = host;
        _transcript = transcript;

        _note.Margin = new Thickness(0, 6, 0, 0);
        _more = DarkTheme.Button("Show more", () =>
        {
            _shown += PageSize;
            _dirty = true;
            Refresh();
        }, small: true);
        _more.HorizontalAlignment = HorizontalAlignment.Left;
        _more.Margin = new Thickness(0, 6, 0, 0);

        Add(DarkTheme.Section("Statistics", new StackPanel { Spacing = 8, Children = { _tiles, _statsNote } },
            DarkTheme.Segmented(["Today", "7 days", "30 days", "All time"], _period, i =>
            {
                _period = i;
                _dirty = true;
                Refresh();
            })));
        Add(DarkTheme.Section("Dictations", new StackPanel { Spacing = 4, Children = { _list, _more, _note } }));

        _host.History.Changed += () => Dispatcher.UIThread.Post(() => _dirty = true);
    }

    public override void OnShown()
    {
        _shown = PageSize;
        _dirty = true;
        Refresh();
    }

    public override void Refresh()
    {
        // The Today/Yesterday headings and "today" move past midnight
        if (!_dirty && _shownDay == DateTime.Today)
            return;
        _dirty = false;
        _shownDay = DateTime.Today;
        RefreshStats();
        RefreshList();
    }

    private void RefreshStats()
    {
        var stats = _host.Stats;
        var totals = _period switch
        {
            0 => stats.Today(),
            1 => stats.LastDays(7),
            2 => stats.LastDays(30),
            _ => stats.AllTime(),
        };
        FillTiles(_tiles, totals);

        var all = stats.AllTime();
        // Typing at ~40 words/min is a common average
        var saved = all.Words / 40.0 * 60 - all.SpeakingSec - all.ProcessingSec;
        _statsNote.Text = $"Counting since {stats.Since.ToLocalTime():d}" +
                          (saved > 60 ? $" · about {Widgets.FormatDuration(saved)} saved compared with typing at 40 words/min" : "");
    }

    /// <summary>Four numbers side by side: dictations, words, speaking time, words per minute.</summary>
    private static void FillTiles(Grid tiles, StatsTotals totals)
    {
        tiles.ColumnDefinitions = new ColumnDefinitions("*,*,*,*");
        tiles.Children.Clear();
        (string Value, string Label)[] values =
        [
            (totals.Dictations.ToString("N0"), "dictations"),
            (totals.Words.ToString("N0"), "words"),
            (Widgets.FormatDuration(totals.SpeakingSec), "speaking"),
            (totals.SpeakingSec > 0 ? totals.WordsPerMinute.ToString("F0") : "–", "words / min"),
        ];
        for (var i = 0; i < values.Length; i++)
        {
            var tile = new StackPanel
            {
                Children =
                {
                    DarkTheme.Label(values[i].Value, 20, FontWeight.SemiBold),
                    DarkTheme.MutedLabel(values[i].Label),
                },
            };
            Grid.SetColumn(tile, i);
            tiles.Children.Add(tile);
        }
    }

    private void RefreshList()
    {
        _list.Children.Clear();

        var total = _host.History.Count;
        var records = _host.History.Recent(_shown);
        _more.IsVisible = total > records.Count;
        _more.Content = $"Show more ({total - records.Count} older)";
        if (records.Count == 0)
        {
            _note.Text = "Nothing yet. Hold the hotkey anywhere, speak, release.";
            return;
        }

        DateTime? day = null;
        foreach (var r in records)
        {
            var date = r.Time.ToLocalTime().Date;
            if (date != day)
            {
                var heading = DarkTheme.Label(DayTitle(date), 12, FontWeight.SemiBold, DarkTheme.Muted);
                heading.Margin = new Thickness(0, day == null ? 0 : 10, 0, 2);
                _list.Children.Add(heading);
                day = date;
            }
            else
                _list.Children.Add(DarkTheme.Separator());
            _list.Children.Add(Row(r, _transcript));
        }

        var note = $"The last {DictationHistory.Keep} dictations are kept.";
        var spoken = _host.History.Recent(total).Where(r => r.Words > 0).ToList();
        if (spoken.Count > 0)
        {
            var avgStt = spoken.Average(r => r.SttSec);
            var speed = avgStt > 0 ? spoken.Average(r => r.RecordSec) / avgStt : 0;
            note = $"Average of {spoken.Count}: processing {spoken.Average(r => r.ProcessSec):F1} s, " +
                   $"recognition {avgStt:F1} s ({speed:F0}× real time). " + note;
        }
        _note.Text = note;
    }

    private static string DayTitle(DateTime date) =>
        date == DateTime.Today ? "Today"
        : date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : date.ToString("D");

    /// <summary>One dictation: time, text (Show opens it in full), timings.</summary>
    private static Control Row(DictationRecord r, TranscriptWindow transcript)
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
            var show = DarkTheme.Button("Show", () => transcript.ShowText(full), small: true);
            Grid.SetColumn(show, 3);
            row.Children.Add(show);
        }
        return row;
    }

    private static string NoteText(string note) => string.IsNullOrEmpty(note) ? "No speech" : char.ToUpper(note[0]) + note[1..];
}
