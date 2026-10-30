using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WhisperLocal.Config;

namespace WhisperLocal.Ui;

/// <summary>Building blocks shared by the main window's pages, on top of <see cref="DarkTheme"/>.</summary>
internal static class Widgets
{
    /// <summary>
    /// Labelled controls in one row, side by side while the window is wide enough and wrapping otherwise.
    /// An empty label puts the control right after the previous one.
    /// </summary>
    public static WrapPanel Fields(params (string Label, Control Control)[] fields)
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

    /// <summary>
    /// Labelled controls as a table: the labels in one column, the controls lined up in the next, one per row. An empty
    /// label puts the control (e.g. a note) under the previous one, in the controls' column.
    /// </summary>
    public static Grid Table(params (string Label, Control Control)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        foreach (var (label, control) in rows)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (label.Length > 0)
            {
                var text = DarkTheme.Label(label);
                text.VerticalAlignment = VerticalAlignment.Center;
                text.Margin = new Thickness(0, 2, 12, 2);
                Grid.SetRow(text, row);
                grid.Children.Add(text);
            }
            control.HorizontalAlignment = HorizontalAlignment.Left;
            control.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return grid;
    }

    /// <summary>Buttons in a row with the usual gap, wrapping when narrow.</summary>
    public static WrapPanel Buttons(params Control[] buttons)
    {
        var row = new WrapPanel();
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>The page title above the cards.</summary>
    public static TextBlock PageTitle(string text)
    {
        var title = DarkTheme.Label(text, 18, FontWeight.SemiBold);
        title.Margin = new Thickness(2, 0, 0, 2);
        return title;
    }

    /// <summary>
    /// Status rows with a dot and an optional fix-it button; rebuilt only when they change (replacing a button under
    /// the mouse would swallow the click). <paramref name="shown"/> keeps what is on screen; <paramref name="acted"/>
    /// runs after a button's action.
    /// </summary>
    public static void RenderItems(StackPanel panel, IReadOnlyList<SetupItem> items, ref string? shown, Action acted)
    {
        var signature = string.Join("\n", items.Select(i => $"{i.State}|{i.Text}|{i.ActionLabel}"));
        if (signature == shown)
            return;
        shown = signature;

        panel.Children.Clear();
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), MinHeight = 22 };
            row.Children.Add(DarkTheme.Dot(StateBrush(item.State)));
            var text = DarkTheme.Label(item.Text, 12);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (item.ActionLabel != null && item.Action != null)
            {
                var action = item.Action;
                var button = DarkTheme.Button(item.ActionLabel, () =>
                {
                    action();
                    acted();
                }, accent: item.State == SetupState.Error, small: true);
                button.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
            }
            panel.Children.Add(row);
        }
    }

    public static IBrush StateBrush(SetupState state) => state switch
    {
        SetupState.Ok => DarkTheme.Ok,
        SetupState.Warning => DarkTheme.Warning,
        SetupState.Pending => DarkTheme.Accent,
        _ => DarkTheme.Error,
    };

    /// <summary>Overall state for the sidebar and Home: the dot colour and one line.</summary>
    public static (IBrush Brush, string Text) Status(IAppHost host)
    {
        var items = host.GetSetupItems();
        var problems = items.Count(i => i.State is SetupState.Error or SetupState.Warning);
        return problems switch
        {
            0 when items.Any(i => i.State == SetupState.Pending) => (DarkTheme.Accent, "Almost ready…"),
            0 => (DarkTheme.Ok, "Ready"),
            1 => (DarkTheme.Warning, "1 thing needs attention"),
            _ => (DarkTheme.Warning, $"{problems} things need attention"),
        };
    }

    /// <summary>Saves one config value; on failure logs it and shows the error in <paramref name="note"/>.</summary>
    public static void Save<T>(IAppHost host, string section, string property, T value, Note note)
    {
        try
        {
            ConfigEditor.SetValue(host.Config.FilePath, section, property, value);
            // Applied, and shown by the pages, now rather than when the file watcher notices
            host.Config.ReloadNow();
        }
        catch (Exception ex)
        {
            Log.Error($"Saving {section}.{property} failed", ex);
            note.Set($"Could not save: {ex.Message}", DarkTheme.Error);
        }
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";
}

/// <summary>A line for errors and prompts that hides itself after a few seconds; descriptions are tooltips.</summary>
internal sealed class Note
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
