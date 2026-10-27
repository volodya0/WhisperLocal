using System.Runtime.InteropServices;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.MacNative;

namespace WhisperLocal.Mac;

/// <summary>
/// SynchronizationContext of the AppKit main thread (via the GCD main queue), the macOS counterpart
/// of WindowsFormsSynchronizationContext: await continuations and posted callbacks run on the main thread.
/// </summary>
internal sealed unsafe class MainThreadContext : SynchronizationContext
{
    public static bool IsMainThread => pthread_main_np() != 0;

    public override void Post(SendOrPostCallback d, object? state)
    {
        var handle = GCHandle.Alloc((d, state));
        dispatch_async_f(DispatchMainQueue, GCHandle.ToIntPtr(handle), &Invoke);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsMainThread)
        {
            d(state);
            return;
        }
        var handle = GCHandle.Alloc((d, state));
        dispatch_sync_f(DispatchMainQueue, GCHandle.ToIntPtr(handle), &Invoke);
    }

    public override SynchronizationContext CreateCopy() => this;

    [UnmanagedCallersOnly]
    private static void Invoke(IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var (callback, state) = ((SendOrPostCallback, object?))handle.Target!;
        handle.Free();

        var previous = Current;
        SetSynchronizationContext(Instance);
        try
        {
            // An exception escaping into native code would kill the process
            ObjC.WithPool(() => callback(state));
        }
        catch (Exception ex)
        {
            Log.Error("Unhandled exception on the main thread", ex);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public static MainThreadContext Instance { get; } = new();
}
