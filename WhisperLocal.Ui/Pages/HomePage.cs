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

    /// <summary>
    /// The version and edition, and one button per edition: this one's updates, and installing the other (Full has
    /// the local models, Lite only the cloud ones and a much smaller download).
    /// </summary>
    private Control UpdatesSection()
    {
        var version = DarkTheme.Label($"WhisperLocal {_host.VersionText} {AppEdition.Name}", 13);
        version.Margin = new Thickness(0, 0, 16, 0);
        var full = DarkTheme.Button(AppEdition.IsLite ? "Install Full" : "Update Full", () => _host.CheckForUpdates(lite: false), small: true);
        ToolTip.SetTip(full, AppEdition.IsLite
            ? "Switch to Full: recognition on this computer, autocomplete and the grammar check with local models; a larger download"
            : "Look for a newer version of Full, this edition");
        var lite = DarkTheme.Button(AppEdition.IsLite ? "Update Lite" : "Install Lite", () => _host.CheckForUpdates(lite: true), small: true);
        ToolTip.SetTip(lite, AppEdition.IsLite
            ? "Look for a newer version of Lite, this edition"
            : "Switch to Lite: a much smaller app that recognises and translates only with Gemini or Groq (needs a key)");
        var row = Widgets.Buttons(full, lite, DarkTheme.Button("GitHub", () => _host.OpenPath(RepositoryUrl), small: true));
        row.Children.Insert(0, version);
        var about = DarkTheme.MutedLabel("Full runs the speech and language models on this computer; Lite is smaller and uses only " +
                                         "Gemini or Groq. Either can be installed over the other; settings and keys stay.", 11);
        about.TextWrapping = TextWrapping.Wrap;
        return new StackPanel { Spacing = 6, Children = { row, about } };
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
