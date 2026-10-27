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
    private readonly Button _copy;
    private bool _allowClose;

    public TranscriptWindow()
    {
        Title = "Transcript";
        Width = 480;
        Height = 230;
        MinWidth = 300;
        MinHeight = 140;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        _copy = new Button { Content = "Copy", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        _copy.Click += async (_, _) => await CopyAsync();
        var close = new Button { Content = "Close", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Hide();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { close, _copy },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(14), Children = { buttons, _text } };

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
