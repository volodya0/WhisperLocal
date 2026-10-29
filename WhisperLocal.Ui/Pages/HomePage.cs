using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using WhisperLocal.Config;
using WhisperLocal.Updates;

namespace WhisperLocal.Ui.Pages;

/// <summary>The first page: overall status with the setup checklist, updates and files.</summary>
internal sealed class HomePage : Page
{
    // Builds without the CI's -p:UpdateRepository still link to the project
    private static readonly string RepositoryUrl = $"https://github.com/{GitHubReleases.Repository ?? "volodya0/WhisperLocal"}";

    private readonly IAppHost _host;
    private readonly Ellipse _statusDot = DarkTheme.Dot(DarkTheme.Idle, 11);
    private readonly TextBlock _status = DarkTheme.Label("", 14, FontWeight.SemiBold);
    private readonly StackPanel _setup = new() { Spacing = 2 };
    private string? _shownSetup;

    public HomePage(IAppHost host) : base("Home")
    {
        _host = host;

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        status.Children.Add(_statusDot);
        status.Children.Add(_status);
        Add(DarkTheme.Section("Status", new StackPanel { Children = { status, _setup } }));
        Add(DarkTheme.Section("Updates", UpdatesSection()));
        Add(DarkTheme.Section("Files", FilesSection()));
    }

    public override void Refresh()
    {
        var (brush, text) = Widgets.Status(_host);
        _statusDot.Fill = brush;
        _status.Text = brush == DarkTheme.Ok ? $"Ready · hold {_host.DescribeHotkey(_host.Config.Current.Hotkey.Key)} to dictate" : text;
        Widgets.RenderItems(_setup, _host.GetSetupItems(), ref _shownSetup, () =>
        {
            _shownSetup = null;
            Refresh();
        });
    }

    private Control UpdatesSection()
    {
        var version = DarkTheme.Label($"WhisperLocal {_host.VersionText}", 13);
        version.Margin = new Thickness(0, 0, 16, 0);
        var row = Widgets.Buttons(
            DarkTheme.Button("Check for Updates", _host.CheckForUpdates, small: true),
            DarkTheme.Button("GitHub", () => _host.OpenPath(RepositoryUrl), small: true));
        row.Children.Insert(0, version);
        return row;
    }

    private Control FilesSection()
    {
        var config = _host.Config.FilePath;
        var buttons = Widgets.Buttons(
            DarkTheme.Button("Config", () => _host.OpenPath(config, textEditor: true), small: true),
            DarkTheme.Button("Log", () => _host.OpenPath(Log.CurrentFile, textEditor: true), small: true),
            DarkTheme.Button("Logs folder", () => _host.OpenPath(AppPaths.LogDir), small: true),
            DarkTheme.Button("Data folder", () => _host.OpenPath(AppPaths.DataDir), small: true));
        var path = new SelectableTextBlock { Text = AppPaths.DataDir, Foreground = DarkTheme.Subtle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        return new StackPanel { Spacing = 6, Children = { buttons, path } };
    }
}
