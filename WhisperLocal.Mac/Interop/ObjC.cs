using System.Runtime.InteropServices;

namespace WhisperLocal.Mac.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NSPoint(double x, double y)
{
    public double X = x;
    public double Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSSize(double width, double height)
{
    public double Width = width;
    public double Height = height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSRange(long location, long length)
{
    public nuint Location = (nuint)location;
    public nuint Length = (nuint)length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSRect(double x, double y, double width, double height)
{
    public double X = x;
    public double Y = y;
    public double Width = width;
    public double Height = height;

    public readonly bool Contains(NSPoint p) => p.X >= X && p.X < X + Width && p.Y >= Y && p.Y < Y + Height;
}

/// <summary>
/// Minimal Objective-C runtime access: classes, selectors and typed objc_msgSend overloads.
/// Objects are used under manual reference counting: alloc/init results are owned, convenience
/// constructors are autoreleased (an autorelease pool must be active; NSApp provides one per event).
/// </summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    private static readonly Dictionary<string, IntPtr> Selectors = new();
    private static readonly Dictionary<string, IntPtr> Classes = new();

    // A .NET process links none of the Cocoa frameworks: their classes exist only once the framework is loaded
    static ObjC()
    {
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, long a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, long b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, double a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, double a, double b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, double a, double b, double c, double d);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, NSPoint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, NSSize a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, NSRect a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, NSRect a, [MarshalAs(UnmanagedType.I1)] bool b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, NSRect a, ulong b, ulong c, [MarshalAs(UnmanagedType.I1)] bool d);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SendBool(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long SendLong(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long SendLong(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern int SendInt(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, NSRange c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern NSPoint SendPoint(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern NSSize SendSize(IntPtr receiver, IntPtr selector, NSSize a);

    // 32-byte structs are returned in registers on arm64 but through a hidden pointer on x86_64
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectArm64(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectX64(out NSRect result, IntPtr receiver, IntPtr selector);

    public static NSRect SendRect(IntPtr receiver, IntPtr selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            SendRectX64(out var rect, receiver, selector);
            return rect;
        }
        return SendRectArm64(receiver, selector);
    }

    public static IntPtr Sel(string name)
    {
        lock (Selectors)
        {
            if (!Selectors.TryGetValue(name, out var sel))
                Selectors[name] = sel = sel_registerName(name);
            return sel;
        }
    }

    public static IntPtr Class(string name)
    {
        lock (Classes)
        {
            if (!Classes.TryGetValue(name, out var cls))
            {
                cls = objc_getClass(name);
                if (cls == IntPtr.Zero)
                    throw new InvalidOperationException($"Objective-C class {name} not found");
                Classes[name] = cls;
            }
            return cls;
        }
    }

    /// <summary>[[Class alloc] init], owned by the caller.</summary>
    public static IntPtr New(string className) => Send(Send(Class(className), Sel("alloc")), Sel("init"));

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            Send(obj, Sel("release"));
    }

    public static IntPtr Retain(IntPtr obj) => obj == IntPtr.Zero ? obj : Send(obj, Sel("retain"));

    /// <summary>Autoreleased NSString.</summary>
    public static IntPtr Str(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>NSString (or a toll-free bridged CFStringRef) to a managed string.</summary>
    public static string? FromStr(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    /// <summary>Autoreleased NSData copy of the bytes.</summary>
    public static unsafe IntPtr Data(byte[] bytes)
    {
        fixed (byte* p = bytes)
            return Send(Class("NSData"), Sel("dataWithBytes:length:"), (IntPtr)p, bytes.LongLength);
    }

    public static byte[] FromData(IntPtr nsData)
    {
        if (nsData == IntPtr.Zero)
            return [];
        var length = SendLong(nsData, Sel("length"));
        var bytes = new byte[length];
        if (length > 0)
            Marshal.Copy(Send(nsData, Sel("bytes")), bytes, 0, (int)length);
        return bytes;
    }

    /// <summary>Elements of an NSArray (not retained).</summary>
    public static IEnumerable<IntPtr> Items(IntPtr nsArray)
    {
        if (nsArray == IntPtr.Zero)
            yield break;
        var count = SendLong(nsArray, Sel("count"));
        for (long i = 0; i < count; i++)
            yield return Send(nsArray, Sel("objectAtIndex:"), (IntPtr)i);
    }

    /// <summary>Runs <paramref name="action"/> inside its own autorelease pool (for code outside the NSApp event loop).</summary>
    public static T WithPool<T>(Func<T> action)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            return action();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public static void WithPool(Action action) => WithPool(() =>
    {
        action();
        return 0;
    });
}
