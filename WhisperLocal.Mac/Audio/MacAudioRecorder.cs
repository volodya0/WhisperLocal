using System.Runtime.InteropServices;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.MacNative;

namespace WhisperLocal.Mac.Audio;

/// <summary>
/// Microphone capture with an AudioQueue. The queue converts to 16 kHz mono 16-bit PCM itself,
/// so the result goes to Whisper as is.
/// </summary>
internal sealed unsafe class MacAudioRecorder : IDisposable
{
    private const int SampleRate = 16000;
    private const int BufferCount = 4;
    private const uint BufferBytes = SampleRate * 2 / 20; // 50 ms

    private readonly object _sync = new();
    private IntPtr _queue;
    private GCHandle _self;
    private MemoryStream? _buffer;
    private long _maxBytes;
    private bool _limitHit;
    private volatile bool _stopping;

    /// <summary>Raised on the audio thread when MaxDurationSec is reached.</summary>
    public event Action? MaxDurationReached;

    public bool IsRecording => _queue != IntPtr.Zero;

    public static IReadOnlyList<string> ListDevices() => InputDevices().Select(d => d.Name).ToList();

    public void Start(string? deviceName, int maxSeconds)
    {
        if (_queue != IntPtr.Zero)
            throw new InvalidOperationException("Recording is already in progress");

        CheckMicrophoneAccess();

        var format = new AudioStreamBasicDescription
        {
            SampleRate = SampleRate,
            FormatID = kAudioFormatLinearPCM,
            FormatFlags = kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
            BytesPerPacket = 2,
            FramesPerPacket = 1,
            BytesPerFrame = 2,
            ChannelsPerFrame = 1,
            BitsPerChannel = 16,
        };

        lock (_sync)
        {
            _buffer = new MemoryStream(SampleRate * 2 * 15);
            _maxBytes = maxSeconds > 0 ? (long)SampleRate * 2 * maxSeconds : long.MaxValue;
            _limitHit = false;
        }
        _stopping = false;
        _self = GCHandle.Alloc(this);

        // No run loop: callbacks come on an internal AudioQueue thread
        Check(AudioQueueNewInput(format, &OnInput, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, out var queue), "AudioQueueNewInput");
        try
        {
            var device = SelectDevice(queue, deviceName);
            for (var i = 0; i < BufferCount; i++)
            {
                Check(AudioQueueAllocateBuffer(queue, BufferBytes, out var buffer), "AudioQueueAllocateBuffer");
                Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), "AudioQueueEnqueueBuffer");
            }
            Check(AudioQueueStart(queue, IntPtr.Zero), "AudioQueueStart");
            _queue = queue;
            Log.Debug($"Recording started: '{device}', 16 kHz mono");
        }
        catch
        {
            AudioQueueDispose(queue, true);
            _self.Free();
            throw;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnInput(IntPtr userData, IntPtr queue, AudioQueueBuffer* buffer, IntPtr startTime, uint packets, IntPtr packetDescs)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not MacAudioRecorder self)
                return;
            self.OnData(buffer);
            if (!self._stopping)
                AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Audio callback failed", ex);
        }
    }

    private void OnData(AudioQueueBuffer* buffer)
    {
        var size = (int)buffer->AudioDataByteSize;
        if (size == 0)
            return;

        var limitReached = false;
        lock (_sync)
        {
            if (_buffer == null || _limitHit)
                return;
            var count = (int)Math.Min(_maxBytes - _buffer.Length, size);
            count -= count % 2;
            _buffer.Write(new ReadOnlySpan<byte>((void*)buffer->AudioData, count));
            if (count < size)
                _limitHit = limitReached = true;
        }
        if (limitReached)
            MaxDurationReached?.Invoke();
    }

    /// <summary>Stops capture and returns 16 kHz mono 16-bit PCM (no WAV header).</summary>
    public Task<byte[]> StopAsync()
    {
        var queue = _queue;
        if (queue == IntPtr.Zero)
            return Task.FromResult<byte[]>([]);
        _queue = IntPtr.Zero;

        return Task.Run(() =>
        {
            _stopping = true;
            // Synchronous stop: no callbacks run after it returns (the last <= 50 ms buffer is dropped)
            AudioQueueStop(queue, true);
            AudioQueueDispose(queue, true);
            _self.Free();

            lock (_sync)
            {
                var pcm = _buffer?.ToArray() ?? [];
                _buffer = null;
                return pcm;
            }
        });
    }

    public enum MicrophoneAccess { NotAsked, Allowed, Denied, Unknown }

    /// <summary>[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio].</summary>
    public static MicrophoneAccess GetMicrophoneAccess()
    {
        try
        {
            LoadFramework("AVFoundation");
            // AVMediaTypeAudio == @"soun"
            var status = ObjC.WithPool(() => ObjC.SendLong(ObjC.Class("AVCaptureDevice"),
                ObjC.Sel("authorizationStatusForMediaType:"), ObjC.Str("soun")));
            return status switch
            {
                0 => MicrophoneAccess.NotAsked,
                3 => MicrophoneAccess.Allowed,
                1 or 2 => MicrophoneAccess.Denied, // restricted, denied
                _ => MicrophoneAccess.Unknown,
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"Microphone permission check failed: {ex.Message}");
            return MicrophoneAccess.Unknown;
        }
    }

    /// <summary>
    /// The system asks for microphone access on the first recording; a denied permission
    /// would silently record zeros, so it is reported as an error instead.
    /// </summary>
    private static void CheckMicrophoneAccess()
    {
        if (GetMicrophoneAccess() == MicrophoneAccess.Denied)
            throw new InvalidOperationException(
                "microphone access is denied. Allow WhisperLocal in System Settings → Privacy & Security → Microphone");
    }

    /// <summary>Picks the input device whose name contains <paramref name="name"/>; returns the device name used.</summary>
    private static string SelectDevice(IntPtr queue, string? name)
    {
        var devices = InputDevices();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = devices.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match.Uid != null)
            {
                ObjC.WithPool(() =>
                {
                    var uid = ObjC.Str(match.Uid);
                    Check(AudioQueueSetProperty(queue, kAudioQueueProperty_CurrentDevice, uid, (uint)IntPtr.Size), "select input device");
                });
                return match.Name;
            }
            Log.Warn($"Capture device '{name}' not found, using the default device. Available: {string.Join(", ", devices.Select(d => d.Name))}");
        }
        var id = GetDefaultInputDevice();
        return devices.FirstOrDefault(d => d.Id == id).Name ?? "default";
    }

    private static List<(uint Id, string Name, string Uid)> InputDevices()
    {
        var result = new List<(uint, string, string)>();
        var address = new AudioObjectPropertyAddress(kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal);
        if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return result;

        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
        {
            if (AudioObjectGetPropertyData(kAudioObjectSystemObject, address, 0, IntPtr.Zero, ref size, p) != 0)
                return result;
        }

        foreach (var id in ids)
        {
            // Only devices with input streams
            var streams = new AudioObjectPropertyAddress(kAudioDevicePropertyStreams, kAudioDevicePropertyScopeInput);
            if (AudioObjectGetPropertyDataSize(id, streams, 0, IntPtr.Zero, out var streamsSize) != 0 || streamsSize == 0)
                continue;

            var name = GetStringProperty(id, kAudioObjectPropertyName);
            var uid = GetStringProperty(id, kAudioDevicePropertyDeviceUID);
            if (name != null && uid != null)
                result.Add((id, name, uid));
        }
        return result;
    }

    private static uint GetDefaultInputDevice()
    {
        var address = new AudioObjectPropertyAddress(kAudioHardwarePropertyDefaultInputDevice, kAudioObjectPropertyScopeGlobal);
        uint id = 0;
        var size = (uint)sizeof(uint);
        AudioObjectGetPropertyData(kAudioObjectSystemObject, address, 0, IntPtr.Zero, ref size, &id);
        return id;
    }

    private static string? GetStringProperty(uint id, uint selector)
    {
        var address = new AudioObjectPropertyAddress(selector, kAudioObjectPropertyScopeGlobal);
        IntPtr cfString = IntPtr.Zero;
        var size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(id, address, 0, IntPtr.Zero, ref size, &cfString) != 0 || cfString == IntPtr.Zero)
            return null;
        try
        {
            return ObjC.FromStr(cfString);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    private static void Check(int status, string what)
    {
        if (status != 0)
            throw new InvalidOperationException($"{what} failed (OSStatus {status})");
    }

    public void Dispose()
    {
        if (_queue == IntPtr.Zero)
            return;
        _stopping = true;
        AudioQueueStop(_queue, true);
        AudioQueueDispose(_queue, true);
        _queue = IntPtr.Zero;
        _self.Free();
    }
}
