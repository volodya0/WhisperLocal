using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>Menu bar item (NSStatusItem) with its menu: the macOS counterpart of NotifyIcon + ContextMenuStrip.</summary>
internal sealed class StatusMenu : IDisposable
{
    public enum Icon { Idle, Recording, Processing, Off }

    // The waveform of the Windows tray icon (scripts/make-mac-icons.py). Idle/off are template images, which macOS
    // colours for light/dark menu bars; the menu bar ignores tint colours, so recording/processing are coloured images.
    // A system symbol stands in if a file is missing.
    private static readonly Lazy<IntPtr> IdleImage = new(() => LoadImage("MenuBarIcon.png", "waveform", template: true));
    private static readonly Lazy<IntPtr> OffImage = new(() => LoadImage("MenuBarIconOff.png", "mic.slash", template: true));
    private static readonly Lazy<IntPtr> RecordingImage = new(() => LoadImage("MenuBarIconRecording.png", "mic.fill", template: false));
    private static readonly Lazy<IntPtr> ProcessingImage = new(() => LoadImage("MenuBarIconProcessing.png", "ellipsis.circle", template: false));

    private readonly IntPtr _statusItem;
    private readonly IntPtr _menu;

    /// <summary>Raised on the main thread right before the menu opens.</summary>
    public event Action? Opening;

    public StatusMenu()
    {
        _menu = Send(Send(Class("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), Str("WhisperLocal"));
        Send(_menu, Sel("setAutoenablesItems:"), false);
        Send(_menu, Sel("setDelegate:"), ActionTarget.Instance);
        ActionTarget.MenuWillOpen += () => Opening?.Invoke();

        var statusBar = Send(Class("NSStatusBar"), Sel("systemStatusBar"));
        _statusItem = Retain(Send(statusBar, Sel("statusItemWithLength:"), -1.0)); // NSVariableStatusItemLength
        Send(_statusItem, Sel("setMenu:"), _menu);
    }

    /// <summary>Adds a menu item; returns its handle for <see cref="SetChecked"/> / <see cref="SetEnabled"/>.</summary>
    public IntPtr AddItem(string title, Action onClick)
    {
        var item = Send(Send(Class("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"),
            Str(title), ActionTarget.ActionSelector, Str(""));
        Send(item, Sel("setTarget:"), ActionTarget.Instance);
        Send(item, Sel("setTag:"), ActionTarget.Register(onClick));
        Send(_menu, Sel("addItem:"), item);
        Release(item); // the menu keeps it
        return item;
    }

    public void AddSeparator() => Send(_menu, Sel("addItem:"), Send(Class("NSMenuItem"), Sel("separatorItem")));

    public static void SetChecked(IntPtr item, bool value) => Send(item, Sel("setState:"), value ? 1L : 0L);

    public static void SetEnabled(IntPtr item, bool value) => Send(item, Sel("setEnabled:"), value);

    public void SetState(Icon icon, string tooltip)
    {
        var button = Send(_statusItem, Sel("button"));
        if (button == IntPtr.Zero)
            return;

        var image = icon switch
        {
            Icon.Recording => RecordingImage.Value,
            Icon.Processing => ProcessingImage.Value,
            Icon.Off => OffImage.Value,
            _ => IdleImage.Value,
        };
        if (image != IntPtr.Zero)
        {
            Send(button, Sel("setImage:"), image);
            Send(button, Sel("setTitle:"), Str(""));
        }
        else
        {
            Send(button, Sel("setTitle:"), Str(icon == Icon.Off ? "WL-" : "WL"));
        }

        Send(button, Sel("setToolTip:"), Str(tooltip));
    }

    /// <summary>Retained image, 18×18 pt (the PNG is 36 px for Retina).</summary>
    private static IntPtr LoadImage(string fileName, string fallbackSymbol, bool template)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        var image = File.Exists(path)
            ? Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), Str(path))
            : Retain(Send(Class("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), Str(fallbackSymbol), Str("WhisperLocal")));
        if (image == IntPtr.Zero)
            return image;
        if (File.Exists(path))
            Send(image, Sel("setSize:"), new NSSize(18, 18));
        // A missing coloured image falls back to a symbol, which has to be a template to be visible at all
        Send(image, Sel("setTemplate:"), template || !File.Exists(path));
        return image;
    }

    public void Dispose()
    {
        var statusBar = Send(Class("NSStatusBar"), Sel("systemStatusBar"));
        Send(statusBar, Sel("removeStatusItem:"), _statusItem);
        Release(_statusItem);
        Release(_menu);
    }
}
