using WhisperLocal.Config;
using WhisperLocal.Mac.Audio;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Mac.Ui;

namespace WhisperLocal.Mac;

internal static class Program
{
    private const long NSApplicationActivationPolicyAccessory = 1; // menu bar only, no Dock icon

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

        InstallDefaultConfig();

        if (args.Length > 0 && args[0].Equals("--batch", StringComparison.OrdinalIgnoreCase))
            return BatchRunner.RunAsync(args, MacAudioFile.Load).GetAwaiter().GetResult();

        using var mutex = new Mutex(true, "WhisperLocal.SingleInstance", out var createdNew);

        var pool = ObjC.objc_autoreleasePoolPush();
        var nsApp = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.Send(nsApp, ObjC.Sel("setActivationPolicy:"), NSApplicationActivationPolicyAccessory);

        if (!createdNew)
        {
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

        SynchronizationContext.SetSynchronizationContext(MainThreadContext.Instance);
        var app = new MenuBarApp(config, MainThreadContext.Instance);
        // SIGTERM (logout, launchctl, kill): do not leave whisper-server running
        AppDomain.CurrentDomain.ProcessExit += (_, _) => app.StopServers();
        ObjC.objc_autoreleasePoolPop(pool);

        // Returns only if the run loop is stopped; Quit ends the process via [NSApp terminate:]
        ObjC.Send(nsApp, ObjC.Sel("run"));
        app.Dispose();
        config.Dispose();
        return 0;
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
