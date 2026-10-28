using Avalonia;
using Avalonia.Threading;

namespace WhisperLocal.Ui;

/// <summary>
/// The shared Avalonia windows (main window, history, statistics, transcript) inside the WinForms app. Avalonia is
/// set up without its own run loop: its windows and dispatcher run on the WinForms message loop of the UI thread.
/// </summary>
internal static class SharedUi
{
    public static void Setup()
    {
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .SetupWithoutStarting();
        // A bug in one callback must not take the whole app down
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception in a window", e.Exception);
            e.Handled = true;
        };
    }
}
