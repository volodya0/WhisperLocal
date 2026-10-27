using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace WhisperLocal.Ui;

/// <summary>The Avalonia application. The platform app hooks in through <see cref="Started"/>.</summary>
public sealed class App : Application
{
    /// <summary>Called on the UI thread once Avalonia is running.</summary>
    public static Action<IClassicDesktopStyleApplicationLifetime>? Started { get; set; }

    public override void Initialize()
    {
        Name = "WhisperLocal";
        RequestedThemeVariant = ThemeVariant.Default; // follows the system light/dark setting
        Styles.Add(new FluentTheme());
        // Replaces the default macOS application menu (which would offer "About Avalonia")
        NativeMenu.SetMenu(this, new NativeMenu());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A tray/menu bar app: closing windows never quits
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Started?.Invoke(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
