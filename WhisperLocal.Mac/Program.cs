using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using WhisperLocal.Config;
using WhisperLocal.Mac.Audio;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Mac.Output;
using WhisperLocal.Mac.Ui;
using WhisperLocal.Ui;

namespace WhisperLocal.Mac;

internal static class Program
{
    private static int Main(string[] args)
    {
        Log.Init(AppPaths.LogDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("This build of WhisperLocal is for macOS; use the WhisperLocal project on Windows.");
            return 1;
        }

        if (args.Length > 0 && args[0] == "--self-test")
            return SelfTest();

        InstallDefaultConfig();

        if (args.Length > 0 && args[0].Equals("--batch", StringComparison.OrdinalIgnoreCase))
            return BatchRunner.RunAsync(args, MacAudioFile.Load).GetAwaiter().GetResult();

        using var mutex = new Mutex(true, "WhisperLocal.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // Normally macOS just reactivates the running app; this covers starting the executable directly
            Dialogs.Show("WhisperLocal", "WhisperLocal is already running.", Dialogs.Style.Informational);
            return 1;
        }

        ConfigManager config;
        try
        {
            config = new ConfigManager(AppPaths.ConfigPath, watch: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load config", ex);
            Dialogs.Show("WhisperLocal", $"Failed to load config:\n{AppPaths.ConfigPath}\n\n{ex.Message}", Dialogs.Style.Critical);
            return 1;
        }

        // Avalonia owns the main run loop (and the shared windows); the native menu bar item, indicator and
        // keyboard tap live on the same main thread
        MenuBarApp? app = null;
        App.Started = desktop =>
        {
            // A bug in one callback must not take the whole app down
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Error("Unhandled exception on the main thread", e.Exception);
                e.Handled = true;
            };
            app = new MenuBarApp(config, desktop);
            // SIGTERM (logout, launchctl, kill): do not leave whisper-server running
            AppDomain.CurrentDomain.ProcessExit += (_, _) => app.StopServers();
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

        app?.Dispose();
        config.Dispose();
        return 0;
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false }) // menu bar app; the main window adds the Dock icon
            .LogToTrace();

    /// <summary>
    /// Checks that every native class and symbol the app uses resolves (run by CI after building the .app),
    /// so a missing framework fails the build instead of crashing silently at launch.
    /// </summary>
    private static int SelfTest()
    {
        string[] classes =
        [
            "NSApplication", "NSStatusBar", "NSMenu", "NSMenuItem", "NSWindow", "NSPanel", "NSTextField", "NSButton",
            "NSColor", "NSFont", "NSImage", "NSAlert", "NSEvent", "NSScreen", "NSWorkspace", "NSPasteboard",
            "NSPasteboardItem", "NSString", "NSData", "NSDictionary", "NSNumber", "NSMutableArray", "NSObject",
            "NSMutableAttributedString", "NSAppearance",
        ];
        try
        {
            foreach (var name in classes)
                ObjC.Class(name);
            MacNative.LoadFramework("AVFoundation");
            ObjC.Class("AVCaptureDevice");
            _ = MacNative.kCFRunLoopCommonModes;
            _ = MacNative.CGEventSourceFlagsState(MacNative.kCGEventSourceStateHIDSystemState);
            _ = MacNative.CGEventSourceSecondsSinceLastEventType(MacNative.kCGEventSourceStateHIDSystemState, MacNative.kCGEventKeyDown);
            _ = MacNative.AXIsProcessTrusted();
            _ = FocusedInput.AcceptsText(1); // the AXUIElement functions resolve (the answer itself does not matter here)
            // Avalonia's native platform and a window of the shared UI
            BuildAvaloniaApp().SetupWithoutStarting();
            _ = new TranscriptWindow();
            Console.WriteLine("Self-test OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Self-test failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// The config lives in ~/Library/Application Support/WhisperLocal so it survives app updates and never
    /// touches the signed bundle; the copy shipped with the build is installed there on the first run.
    /// </summary>
    private static void InstallDefaultConfig()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            if (!File.Exists(AppPaths.ConfigPath) && File.Exists(AppPaths.DefaultConfigPath))
            {
                File.Copy(AppPaths.DefaultConfigPath, AppPaths.ConfigPath);
                Log.Info($"Default config installed to {AppPaths.ConfigPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to install the default config", ex);
        }
    }
}
