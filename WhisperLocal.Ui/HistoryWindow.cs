using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WhisperLocal.Pipeline;

namespace WhisperLocal.Ui;

/// <summary>All kept dictations, grouped by day, with average timings. Closing only hides it.</summary>
public sealed class HistoryWindow : Window
{
    private readonly IAppHost _host;
    private readonly TranscriptWindow _transcript;
    private readonly StackPanel _list = new();
    private readonly TextBlock _note = DarkTheme.MutedLabel("");
    private bool _allowClose;

    public HistoryWindow(IAppHost host, TranscriptWindow transcript)
    {
        _host = host;
        _transcript = transcript;
        Title = "Dictation History";
        Width = 680;
        Height = 620;
        MinWidth = 480;
        MinHeight = 320;
        Background = DarkTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _note.Margin = new Thickness(0, 6, 0, 0);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(10),
                Children = { DarkTheme.Section("History", new StackPanel { Spacing = 4, Children = { _list, _note } }) },
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
        // Always: the Today/Yesterday headings move past midnight
        Refresh();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Refresh()
    {
        _list.Children.Clear();

        var records = _host.History.Recent(DictationHistory.Keep);
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
        var spoken = records.Where(r => r.Words > 0).ToList();
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

    /// <summary>One dictation: time, text (Show opens it in full), timings. Also used for the main window's recents.</summary>
    internal static Control Row(DictationRecord r, TranscriptWindow transcript)
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

    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }
}
