using System.Runtime.InteropServices;

namespace WhisperLocal.Mac.Interop;

/// <summary>C APIs of CoreFoundation, CoreGraphics (events), ApplicationServices and CoreAudio/AudioToolbox.</summary>
internal static unsafe class MacNative
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    // CoreFoundation
    [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] public static extern IntPtr CFRunLoopGetMain();
    [DllImport(CoreFoundation)] public static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
    [DllImport(CoreFoundation)] public static extern void CFRunLoopRemoveSource(IntPtr runLoop, IntPtr source, IntPtr mode);
    [DllImport(CoreFoundation)] public static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, nint order);
    [DllImport(CoreFoundation)] public static extern void CFMachPortInvalidate(IntPtr port);

    private static readonly Lazy<IntPtr> CommonModes = new(() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes")));

    public static IntPtr kCFRunLoopCommonModes => CommonModes.Value;

    // CoreGraphics events
    public const int kCGSessionEventTap = 1;
    public const int kCGHIDEventTap = 0;
    public const int kCGHeadInsertEventTap = 0;
    public const int kCGEventTapOptionDefault = 0;
    public const int kCGEventSourceStatePrivate = -1;
    public const int kCGEventSourceStateHIDSystemState = 1;

    public const uint kCGEventKeyDown = 10;
    public const uint kCGEventKeyUp = 11;
    public const uint kCGEventFlagsChanged = 12;
    public const uint kCGEventTapDisabledByTimeout = 0xFFFFFFFE;
    public const uint kCGEventTapDisabledByUserInput = 0xFFFFFFFF;

    public const int kCGKeyboardEventKeycode = 9;
    public const int kCGEventSourceUserData = 42;

    public const ulong kCGEventFlagMaskShift = 0x20000;
    public const ulong kCGEventFlagMaskControl = 0x40000;
    public const ulong kCGEventFlagMaskAlternate = 0x80000;
    public const ulong kCGEventFlagMaskCommand = 0x100000;
    public const ulong kCGEventFlagMaskSecondaryFn = 0x800000;

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventTapCreate(int tap, int place, int options, ulong eventsOfInterest,
        delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> callback, IntPtr userInfo);

    [DllImport(CoreGraphics)] public static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);
    [DllImport(CoreGraphics)] public static extern long CGEventGetIntegerValueField(IntPtr evt, int field);
    [DllImport(CoreGraphics)] public static extern void CGEventSetIntegerValueField(IntPtr evt, int field, long value);
    [DllImport(CoreGraphics)] public static extern ulong CGEventGetFlags(IntPtr evt);
    [DllImport(CoreGraphics)] public static extern void CGEventSetFlags(IntPtr evt, ulong flags);
    [DllImport(CoreGraphics)] public static extern IntPtr CGEventSourceCreate(int stateId);
    [DllImport(CoreGraphics)] public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keycode, [MarshalAs(UnmanagedType.I1)] bool keyDown);
    [DllImport(CoreGraphics)] public static extern void CGEventPost(int tap, IntPtr evt);
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGEventSourceKeyState(int stateId, ushort keycode);
    [DllImport(CoreGraphics)] public static extern ulong CGEventSourceFlagsState(int stateId);
    [DllImport(CoreGraphics)] public static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);
    [DllImport(CoreGraphics)] public static extern IntPtr CGColorCreateGenericGray(double gray, double alpha);
    [DllImport(CoreGraphics)] public static extern void CGColorRelease(IntPtr color);

    // Accessibility (needed for the event tap and for posting Cmd+V)
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    // AudioToolbox: AudioQueue input
    public const uint kAudioFormatLinearPCM = 0x6C70636D; // 'lpcm'
    public const uint kLinearPCMFormatFlagIsSignedInteger = 0x4;
    public const uint kLinearPCMFormatFlagIsPacked = 0x8;
    public const uint kAudioQueueProperty_CurrentDevice = 0x61716364; // 'aqcd'

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [DllImport(AudioToolbox)]
    public static extern int AudioQueueNewInput(in AudioStreamBasicDescription format,
        delegate* unmanaged<IntPtr, IntPtr, AudioQueueBuffer*, IntPtr, uint, IntPtr, void> callback,
        IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

    [DllImport(AudioToolbox)] public static extern int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out AudioQueueBuffer* buffer);
    [DllImport(AudioToolbox)] public static extern int AudioQueueEnqueueBuffer(IntPtr queue, AudioQueueBuffer* buffer, uint numPacketDescs, IntPtr packetDescs);
    [DllImport(AudioToolbox)] public static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolbox)] public static extern int AudioQueueStop(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
    [DllImport(AudioToolbox)] public static extern int AudioQueueDispose(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
    [DllImport(AudioToolbox)] public static extern int AudioQueueSetProperty(IntPtr queue, uint propertyId, in IntPtr data, uint dataSize);

    // CoreAudio: device enumeration
    public const uint kAudioObjectSystemObject = 1;
    public const uint kAudioHardwarePropertyDevices = 0x64657623; // 'dev#'
    public const uint kAudioHardwarePropertyDefaultInputDevice = 0x64496E20; // 'dIn '
    public const uint kAudioDevicePropertyStreams = 0x73746D23; // 'stm#'
    public const uint kAudioObjectPropertyName = 0x6C6E616D; // 'lnam'
    public const uint kAudioDevicePropertyDeviceUID = 0x75696420; // 'uid '
    public const uint kAudioObjectPropertyScopeGlobal = 0x676C6F62; // 'glob'
    public const uint kAudioDevicePropertyScopeInput = 0x696E7074; // 'inpt'
    public const uint kAudioObjectPropertyElementMain = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress(uint selector, uint scope)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = kAudioObjectPropertyElementMain;
    }

    [DllImport(CoreAudio)]
    public static extern int AudioObjectGetPropertyDataSize(uint objectId, in AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(CoreAudio)]
    public static extern int AudioObjectGetPropertyData(uint objectId, in AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, void* data);

    // CoreAudio: system audio capture through a process tap (macOS 14.2+) and an aggregate device
    public const uint kAudioHardwarePropertyDefaultSystemOutputDevice = 0x734F7574; // 'sOut'
    public const uint kAudioTapPropertyFormat = 0x74666D74; // 'tfmt'
    public const uint kAudioFormatFlagIsFloat = 0x1;
    public const uint kAudioFormatFlagIsNonInterleaved = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public IntPtr Data;
    }

    /// <summary>AudioBufferList's header; its AudioBuffer array starts 8 bytes in (after the count and padding).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBufferList
    {
        public uint NumberBuffers;
        private readonly uint _padding;

        public static AudioBuffer* Buffers(AudioBufferList* list) => (AudioBuffer*)((byte*)list + 8);
    }

    [DllImport(CoreAudio)] public static extern int AudioHardwareCreateProcessTap(IntPtr description, out uint tapId);
    [DllImport(CoreAudio)] public static extern int AudioHardwareDestroyProcessTap(uint tapId);
    [DllImport(CoreAudio)] public static extern int AudioHardwareCreateAggregateDevice(IntPtr description, out uint deviceId);
    [DllImport(CoreAudio)] public static extern int AudioHardwareDestroyAggregateDevice(uint deviceId);

    [DllImport(CoreAudio)]
    public static extern int AudioDeviceCreateIOProcID(uint deviceId,
        delegate* unmanaged<uint, IntPtr, AudioBufferList*, IntPtr, IntPtr, IntPtr, IntPtr, int> proc, IntPtr clientData, out IntPtr procId);

    [DllImport(CoreAudio)] public static extern int AudioDeviceDestroyIOProcID(uint deviceId, IntPtr procId);
    [DllImport(CoreAudio)] public static extern int AudioDeviceStart(uint deviceId, IntPtr procId);
    [DllImport(CoreAudio)] public static extern int AudioDeviceStop(uint deviceId, IntPtr procId);

    public static void LoadFramework(string name) =>
        NativeLibrary.Load($"/System/Library/Frameworks/{name}.framework/{name}");
}
