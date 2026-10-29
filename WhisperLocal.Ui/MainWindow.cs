using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WhisperLocal.Ui.Pages;

namespace WhisperLocal.Ui;

/// <summary>
/// The app's window: a sidebar with the pages (Home, History, Notes; then the settings: Speech to Text, Notes &amp;
/// Translate, Models) and the overall status at the bottom; the selected page on the right. Dark, card-based.
/// Closing only hides it; the visible page refreshes itself while the window is shown.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly IAppHost _host;
    private readonly HomePage _home;
    private readonly HistoryPage _history;
    private readonly ModelsPage _models;
    private readonly List<(Page Page, Button Button)> _nav = [];
    private readonly ContentControl _content = new();
    private readonly Ellipse _statusDot = DarkTheme.Dot(DarkTheme.Idle);
    private readonly TextBlock _status = DarkTheme.Label("", 12);
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private Page? _page;
    private bool _allowClose;

    // Icons drawn as paths on a 24×24 grid
    private const string HomeIcon = "M3,11 L12,3 L21,11 V21 H14 V15 H10 V21 H3 Z";
    private const string HistoryIcon = "F0 M12,2 A10,10 0 1 1 11.99,2 Z M12,4 A8,8 0 1 0 12.01,4 Z M11,7 H13 V11.4 L16.5,13.5 L15.5,15.2 L11,12.5 Z";
    private const string NotesIcon = "F0 M5,2 H14 L20,8 V22 H5 Z M7,4 V20 H18 V9 H13 V4 Z M9,12 H16 V13.6 H9 Z M9,15.5 H16 V17.1 H9 Z";
    private const string SpeechIcon = "M12,2 A3,3 0 0 1 15,5 V12 A3,3 0 0 1 9,12 V5 A3,3 0 0 1 12,2 Z M5,11 H7 A5,5 0 0 0 17,11 H19 A7,7 0 0 1 13,17.9 V21 H11 V17.9 A7,7 0 0 1 5,11 Z";
    private const string TranslateIcon = "M3,17.25 V21 H6.75 L17.81,9.94 L14.06,6.19 Z M20.71,7.04 C21.1,6.65 21.1,6.02 20.71,5.63 L18.37,3.29 C17.98,2.9 17.35,2.9 16.96,3.29 L15.13,5.12 L18.88,8.87 Z";
    private const string ModelsIcon = "F0 M6,6 H18 V18 H6 Z M8.5,8.5 V15.5 H15.5 V8.5 Z M9,2 H11 V5 H9 Z M13,2 H15 V5 H13 Z M9,19 H11 V22 H9 Z M13,19 H15 V22 H13 Z M2,9 H5 V11 H2 Z M2,13 H5 V15 H2 Z M19,9 H22 V11 H19 Z M19,13 H22 V15 H19 Z";

    public MainWindow(IAppHost host, TranscriptWindow transcript)
    {
        _host = host;
        Title = "WhisperLocal";
        Icon = App.WindowIcon;
        Width = 880;
        Height = 680;
        MinWidth = 700;
        MinHeight = 480;
        Background = DarkTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _history = new HistoryPage(host, transcript);
        _models = new ModelsPage(host);
        _home = new HomePage(host);
        var notes = new NotesPage(host);
        var speech = new SpeechPage(host, () => Select(_models));
        var notesSettings = new NotesSettingsPage(host, () => Select(_models));

        var nav = new StackPanel { Spacing = 2 };
        nav.Children.Add(NavButton(_home, HomeIcon));
        nav.Children.Add(NavButton(_history, HistoryIcon));
        nav.Children.Add(NavButton(notes, NotesIcon));
        var settings = DarkTheme.Label("SETTINGS", 10, FontWeight.SemiBold, DarkTheme.Subtle);
        settings.Margin = new Thickness(12, 16, 0, 4);
        nav.Children.Add(settings);
        nav.Children.Add(NavButton(speech, SpeechIcon));
        nav.Children.Add(NavButton(notesSettings, TranslateIcon));
        nav.Children.Add(NavButton(_models, ModelsIcon));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
        grid.Children.Add(Sidebar(nav));
        Grid.SetColumn(_content, 1);
        grid.Children.Add(_content);
        Content = grid;

        // Not Select: the page would refresh, and the platform app is still being constructed (it creates this
        // window in its constructor), so asking it for the setup items would crash; the page refreshes when shown
        Highlight(_home);
        _refresh.Tick += (_, _) => Refresh();

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
                _page?.OnShown();
                RefreshStatus();
                _refresh.Start();
            }
            else
            {
                _refresh.Stop();
                _page?.OnHidden();
            }
            _host.MainWindowVisibilityChanged(IsVisible);
        };
    }

    /// <summary>True if the setup checklist has anything that blocks dictation.</summary>
    public bool HasProblems => _host.GetSetupItems().Any(i => i.State is SetupState.Error);

    public void ShowAndActivate()
    {
        // Whatever blocks dictation is on Home
        if (HasProblems)
            Select(_home);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Opens the window on the Models page (e.g. a feature needs a model that is not downloaded).</summary>
    public void ShowModels()
    {
        Select(_models);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void Refresh()
    {
        RefreshStatus();
        _page?.Refresh();
    }

    private void Select(Page page)
    {
        if (page == _page)
            return;
        _page?.OnHidden();
        Highlight(page);
        page.OnShown();
    }

    /// <summary>Makes <paramref name="page"/> the visible one and marks it in the sidebar, without refreshing it.</summary>
    private void Highlight(Page page)
    {
        _page = page;
        _content.Content = page;
        foreach (var (p, button) in _nav)
        {
            var selected = p == page;
            button.Background = selected ? DarkTheme.Brush("#2A2A31") : Brushes.Transparent;
            button.Foreground = selected ? DarkTheme.Text : DarkTheme.Muted;
        }
    }

    // ---- Sidebar ----

    private Control Sidebar(Control nav)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(8, 2, 0, 18) };
        header.Children.Add(DarkTheme.AppIcon(26));
        header.Children.Add(DarkTheme.Label("WhisperLocal", 15, FontWeight.SemiBold));

        // The overall status, on every page; a click shows Home with the checklist
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        _statusDot.Margin = new Thickness(0, 0, 8, 0);
        status.Children.Add(_statusDot);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(_status, 1);
        status.Children.Add(_status);
        var version = DarkTheme.Label($"v{_host.VersionText}", 11, brush: DarkTheme.Subtle);
        version.Margin = new Thickness(17, 2, 0, 0);
        var footer = new Button
        {
            Content = new StackPanel { Children = { status, version } },
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(8),
        };
        footer.Click += (_, _) => Select(_home);

        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        return new Border
        {
            Background = DarkTheme.Brush("#18181B"),
            BorderBrush = DarkTheme.CardBorder,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 14, 10, 10),
            Child = new DockPanel { Children = { header, footer, nav } },
        };
    }

    private Button NavButton(Page page, string icon)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new PathIcon { Data = Geometry.Parse(icon), Width = 15, Height = 15 },
                    new TextBlock { Text = page.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Background = Brushes.Transparent,
            Foreground = DarkTheme.Muted,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 7),
            CornerRadius = new CornerRadius(8),
        };
        button.Click += (_, _) => Select(page);
        _nav.Add((page, button));
        return button;
    }

    private void RefreshStatus()
    {
        var (brush, text) = Widgets.Status(_host);
        _statusDot.Fill = brush;
        _status.Text = text;
    }

    public void CloseForGood()
    {
        _allowClose = true;
        _refresh.Stop();
        Close();
    }
}
