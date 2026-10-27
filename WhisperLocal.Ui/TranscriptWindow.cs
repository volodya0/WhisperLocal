using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace WhisperLocal.Ui;

/// <summary>Shows a dictation's text with a Copy button, just above the recording indicator.</summary>
public sealed class TranscriptWindow : Window
{
    private readonly TextBox _text;
    private readonly TextBlock _words = DarkTheme.MutedLabel("");
    private readonly Button _copy;
    private bool _allowClose;

    public TranscriptWindow()
    {
        Title = "Transcript";
        Width = 520;
        Height = 260;
        MinWidth = 320;
        MinHeight = 160;
        Topmost = true;
        ShowInTaskbar = false;
        Background = DarkTheme.Background;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Padding = new Thickness(12, 10),
            Background = DarkTheme.Card,
            BorderBrush = DarkTheme.CardBorder,
            CornerRadius = new CornerRadius(10),
        };
        _text.TextChanged += (_, _) => UpdateWordCount();
        _copy = DarkTheme.Button("Copy", () => _ = CopyAsync(), accent: true);
        var close = DarkTheme.Button("Close", Hide);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_words);
        close.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetColumn(_copy, 2);
        footer.Children.Add(_copy);
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(12), Children = { footer, _text } };

        // Reused: closing only hides it
        Closing += (_, e) =>
        {
            if (_allowClose)
                return;
            e.Cancel = true;
            Hide();
        };
    }

    public void ShowText(string text)
    {
        _text.Text = text;
        _copy.Content = "Copy";
        PlaceAboveIndicator();
        Show();
        Activate();
    }

    private void UpdateWordCount()
    {
        var count = (_text.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        _words.Text = count == 1 ? "1 word" : $"{count} words";
    }

    /// <summary>Bottom centre of the primary screen, above the indicator (which sits 40 pt above the bottom).</summary>
    private void PlaceAboveIndicator()
    {
        if (Screens.Primary is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Bottom - height - (int)(100 * scale));
    }

    private async Task CopyAsync()
    {
        if (Clipboard == null)
            return;
        await Clipboard.SetTextAsync(_text.Text ?? "");
        _copy.Content = "Copied ✓";
        DispatcherTimer.RunOnce(() => _copy.Content = "Copy", TimeSpan.FromSeconds(1.5));
    }

    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }
}
