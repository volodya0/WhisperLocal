using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace WhisperLocal.Ui;

/// <summary>The Avalonia application. The platform app hooks in through <see cref="Started"/>.</summary>
public sealed class App : Application
{
    /// <summary>Title bar and taskbar icon of every window (Windows); macOS shows the app icon by itself.</summary>
    public static WindowIcon? WindowIcon { get; set; }

    /// <summary>Called on the UI thread once Avalonia is running.</summary>
    public static Action<IClassicDesktopStyleApplicationLifetime>? Started { get; set; }

    public override void Initialize()
    {
        Name = "WhisperLocal";
        RequestedThemeVariant = ThemeVariant.Dark; // always dark, matching the menu bar indicator
        // Fluent's accent (accent buttons, selected toggles, focus) in the app's softer blue
        Styles.Add(new FluentTheme
        {
            Palettes = { [ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#4F7FF0") } },
        });
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
