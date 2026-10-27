using System.Runtime.InteropServices;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>Menu bar item (NSStatusItem) with its menu: the macOS counterpart of NotifyIcon + ContextMenuStrip.</summary>
internal sealed class StatusMenu : IDisposable
{
    public enum Icon { Idle, Recording, Processing, Off }

    private static readonly List<Action> Actions = [];
    private static Action? _menuOpening;
    private static IntPtr _targetClass;

    private readonly IntPtr _statusItem;
    private readonly IntPtr _menu;
    private readonly IntPtr _target;

    /// <summary>Raised on the main thread right before the menu opens.</summary>
    public event Action? Opening;

    public StatusMenu()
    {
        _target = New(TargetClassName());
        _menu = Send(Send(Class("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), Str("WhisperLocal"));
        Send(_menu, Sel("setAutoenablesItems:"), false);
        Send(_menu, Sel("setDelegate:"), _target);
        _menuOpening = () => Opening?.Invoke();

        var statusBar = Send(Class("NSStatusBar"), Sel("systemStatusBar"));
        _statusItem = Retain(Send(statusBar, Sel("statusItemWithLength:"), -1.0)); // NSVariableStatusItemLength
        Send(_statusItem, Sel("setMenu:"), _menu);
    }

    /// <summary>Adds a menu item; returns its handle for <see cref="SetChecked"/> / <see cref="SetEnabled"/>.</summary>
    public IntPtr AddItem(string title, Action onClick)
    {
        var item = Send(Send(Class("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"),
            Str(title), Sel("menuAction:"), Str(""));
        Send(item, Sel("setTarget:"), _target);
        lock (Actions)
        {
            Send(item, Sel("setTag:"), (long)Actions.Count);
            Actions.Add(onClick);
        }
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

    private static string TargetClassName()
    {
        const string name = "WhisperLocalMenuTarget";
        if (_targetClass != IntPtr.Zero)
            return name;
        unsafe
        {
            var cls = objc_allocateClassPair(Class("NSObject"), name, 0);
            class_addMethod(cls, Sel("menuAction:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuAction, "v@:@");
            class_addMethod(cls, Sel("menuWillOpen:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuWillOpen, "v@:@");
            objc_registerClassPair(cls);
            _targetClass = cls;
        }
        return name;
    }

    [UnmanagedCallersOnly]
    private static void OnMenuAction(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            var tag = SendLong(sender, Sel("tag"));
            Action? action;
            lock (Actions)
                action = tag >= 0 && tag < Actions.Count ? Actions[(int)tag] : null;
            action?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Menu action failed", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnMenuWillOpen(IntPtr self, IntPtr sel, IntPtr menu)
    {
        try
        {
            _menuOpening?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Menu update failed", ex);
        }
    }

    public void Dispose()
    {
        var statusBar = Send(Class("NSStatusBar"), Sel("systemStatusBar"));
        Send(statusBar, Sel("removeStatusItem:"), _statusItem);
        Release(_statusItem);
        Release(_menu);
        Release(_target);
    }
}
