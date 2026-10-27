using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>Menu bar item (NSStatusItem) with its menu: the macOS counterpart of NotifyIcon + ContextMenuStrip.</summary>
internal sealed class StatusMenu : IDisposable
{
    public enum Icon { Idle, Recording, Processing, Off }

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

        var symbol = icon switch
        {
            Icon.Recording => "mic.fill",
            Icon.Processing => "ellipsis.circle",
            Icon.Off => "mic.slash",
            _ => "mic",
        };
        var image = Send(Class("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), Str(symbol), Str("WhisperLocal"));
        if (image != IntPtr.Zero)
        {
            Send(image, Sel("setTemplate:"), true);
            Send(button, Sel("setImage:"), image);
            Send(button, Sel("setTitle:"), Str(""));
        }
        else
        {
            Send(button, Sel("setTitle:"), Str(icon == Icon.Off ? "WL-" : "WL"));
        }

        // Template images take the tint: red while recording, orange while processing, default otherwise
        var tint = icon switch
        {
            Icon.Recording => Send(Class("NSColor"), Sel("systemRedColor")),
            Icon.Processing => Send(Class("NSColor"), Sel("systemOrangeColor")),
            _ => IntPtr.Zero,
        };
        Send(button, Sel("setContentTintColor:"), tint);
        Send(button, Sel("setToolTip:"), Str(tooltip));
    }

    public void Dispose()
    {
        var statusBar = Send(Class("NSStatusBar"), Sel("systemStatusBar"));
        Send(statusBar, Sel("removeStatusItem:"), _statusItem);
        Release(_statusItem);
        Release(_menu);
    }
}
