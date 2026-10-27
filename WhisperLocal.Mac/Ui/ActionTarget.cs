using System.Runtime.InteropServices;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// One Objective-C object that receives AppKit callbacks of the native parts: menu item and button clicks
/// (dispatched by the sender's tag) and the menu delegate. Avalonia owns the application and window delegates.
/// </summary>
internal static class ActionTarget
{
    private static readonly List<Action> Actions = [];
    private static IntPtr _instance;

    /// <summary>The selector to use as the action of controls whose target is <see cref="Instance"/>.</summary>
    public static IntPtr ActionSelector => Sel("performAction:");

    public static event Action? MenuWillOpen;

    public static unsafe IntPtr Instance
    {
        get
        {
            if (_instance != IntPtr.Zero)
                return _instance;
            var cls = objc_allocateClassPair(Class("NSObject"), "WhisperLocalActionTarget", 0);
            class_addMethod(cls, Sel("performAction:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnAction, "v@:@");
            class_addMethod(cls, Sel("menuWillOpen:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuWillOpen, "v@:@");
            objc_registerClassPair(cls);
            _instance = Send(Send(cls, Sel("alloc")), Sel("init"));
            return _instance;
        }
    }

    /// <summary>Registers a click handler; returns the tag to give the control.</summary>
    public static long Register(Action action)
    {
        lock (Actions)
        {
            Actions.Add(action);
            return Actions.Count - 1;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnAction(IntPtr self, IntPtr sel, IntPtr sender) => Guard("Action", () =>
    {
        var tag = SendLong(sender, Sel("tag"));
        Action? action;
        lock (Actions)
            action = tag >= 0 && tag < Actions.Count ? Actions[(int)tag] : null;
        action?.Invoke();
    });

    [UnmanagedCallersOnly]
    private static void OnMenuWillOpen(IntPtr self, IntPtr sel, IntPtr menu) => Guard("Menu update", () => MenuWillOpen?.Invoke());

    // An exception escaping into native code would kill the process
    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"{what} failed", ex);
        }
    }
}
