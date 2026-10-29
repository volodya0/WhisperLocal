using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace WhisperLocal.Ui;

/// <summary>The dark palette and the few building blocks the windows are made of.</summary>
internal static class DarkTheme
{
    public static readonly IBrush Background = Brush("#141416");
    public static readonly IBrush Card = Brush("#1C1C20");
    public static readonly IBrush CardBorder = Brush("#2A2A30");
    public static readonly IBrush Divider = Brush("#26262B");
    public static readonly IBrush Text = Brush("#ECECF1");
    public static readonly IBrush Muted = Brush("#8E8E98");
    public static readonly IBrush Subtle = Brush("#5F5F68");
    public static readonly IBrush Accent = Brush("#5B8CFF");
    public static readonly IBrush Ok = Brush("#3FB950");
    public static readonly IBrush Warning = Brush("#D29922");
    public static readonly IBrush Error = Brush("#F85149");
    public static readonly IBrush Idle = Brush("#6E6E78");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static TextBlock Label(string text, double size = 13, FontWeight? weight = null, IBrush? brush = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeight.Normal,
        Foreground = brush ?? Text,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock MutedLabel(string text, double size = 12) => Label(text, size, brush: Muted);

    /// <summary>A section: rounded card with a small uppercase title (none if empty) and an optional control on the right.</summary>
    public static Border Section(string title, Control content, Control? headerRight = null)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(Label(title.ToUpperInvariant(), 11, FontWeight.SemiBold, Muted));
        if (headerRight != null)
        {
            Grid.SetColumn(headerRight, 1);
            header.Children.Add(headerRight);
        }
        return new Border
        {
            Background = Card,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 10),
            Child = title.Length == 0 && headerRight == null ? content : new StackPanel { Children = { header, content } },
        };
    }

    public static Ellipse Dot(IBrush brush, double size = 9) => new()
    {
        Width = size,
        Height = size,
        Fill = brush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Height shared by buttons and dropdowns, so controls in one row line up.</summary>
    public const double ControlHeight = 24;

    public static Button Button(string text, Action onClick, bool accent = false, bool small = false)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 11,
            Height = ControlHeight,
            Padding = new Thickness(small ? 8 : 12, 0),
            MinWidth = small ? 0 : 72,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
        };
        if (accent)
            button.Classes.Add("accent");
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A thin horizontal line between list rows.</summary>
    public static Border Separator() => new() { Height = 1, Background = Divider, Margin = new Thickness(0, 5) };

    /// <summary>The app icon drawn as vectors: five waveform bars on a dark rounded square (as on the tray icon).</summary>
    public static Control AppIcon(double size = 40)
    {
        double[] heights = [0.4, 0.75, 1.0, 0.6, 0.35];
        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = size * 0.065,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var h in heights)
        {
            bars.Children.Add(new Border
            {
                Width = size * 0.11,
                Height = size * 0.6 * h,
                CornerRadius = new CornerRadius(size * 0.055),
                Background = Text,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.24),
            Background = Brush("#26262C"),
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            Child = bars,
        };
    }

    /// <summary>A checkbox with the page's text size and color.</summary>
    public static CheckBox CheckBox(string text) => new() { Content = text, FontSize = 13, Foreground = Text };

    /// <summary>Dropdown with the same height and text size as the buttons.</summary>
    public static ComboBox Dropdown(double width) => new()
    {
        Width = width,
        Height = ControlHeight,
        MinHeight = ControlHeight,
        FontSize = 12,
        Padding = new Thickness(10, 0, 6, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(7),
    };

    /// <summary>Segmented control: one of several toggle buttons is selected.</summary>
    public static StackPanel Segmented(string[] options, int selected, Action<int> onSelect)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var buttons = new List<ToggleButton>();
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var button = new ToggleButton
            {
                Content = options[i],
                FontSize = 11,
                Padding = new Thickness(8, 2),
                CornerRadius = new CornerRadius(6),
                IsChecked = i == selected,
            };
            button.Click += (_, _) =>
            {
                foreach (var b in buttons)
                    b.IsChecked = b == button;
                onSelect(index);
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        return panel;
    }
}
