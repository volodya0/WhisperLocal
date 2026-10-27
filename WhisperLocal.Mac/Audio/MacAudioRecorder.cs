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

    /// <summary>
    /// One recording: its queue, buffer and the GCHandle the audio callback gets. Kept separate per recording,
    /// so a stop still finishing in the background can never touch the next recording.
    /// </summary>
    private sealed class Session(MacAudioRecorder owner, long maxBytes)
    {
        public readonly object Sync = new();
        public readonly MacAudioRecorder Owner = owner;
        public readonly MemoryStream Buffer = new(SampleRate * 2 * 15);
        public readonly long MaxBytes = maxBytes;
        public IntPtr Queue;
        public GCHandle Handle;
        public bool LimitHit;
        public volatile bool Stopping;
    }

    private Session? _session;

    /// <summary>Raised on the audio thread when MaxDurationSec is reached.</summary>
    public event Action? MaxDurationReached;

    public bool IsRecording => _session != null;

    public static IReadOnlyList<string> ListDevices() => InputDevices().Select(d => d.Name).ToList();

    public void Start(string? deviceName, int maxSeconds)
    {
        if (_session != null)
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

        var session = new Session(this, maxSeconds > 0 ? (long)SampleRate * 2 * maxSeconds : long.MaxValue);
        session.Handle = GCHandle.Alloc(session);
        try
        {
            // No run loop: callbacks come on an internal AudioQueue thread
            Check(AudioQueueNewInput(format, &OnInput, GCHandle.ToIntPtr(session.Handle), IntPtr.Zero, IntPtr.Zero, 0, out session.Queue),
                "AudioQueueNewInput");
            var device = SelectDevice(session.Queue, deviceName);
            for (var i = 0; i < BufferCount; i++)
            {
                Check(AudioQueueAllocateBuffer(session.Queue, BufferBytes, out var buffer), "AudioQueueAllocateBuffer");
                Check(AudioQueueEnqueueBuffer(session.Queue, buffer, 0, IntPtr.Zero), "AudioQueueEnqueueBuffer");
            }
            Check(AudioQueueStart(session.Queue, IntPtr.Zero), "AudioQueueStart");
            _session = session;
            Log.Debug($"Recording started: '{device}', 16 kHz mono");
        }
        catch
        {
            if (session.Queue != IntPtr.Zero)
                AudioQueueDispose(session.Queue, true);
            session.Handle.Free();
            throw;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnInput(IntPtr userData, IntPtr queue, AudioQueueBuffer* buffer, IntPtr startTime, uint packets, IntPtr packetDescs)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not Session session)
                return;
            OnData(session, buffer);
            if (!session.Stopping)
                AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Audio callback failed", ex);
        }
    }

    private static void OnData(Session session, AudioQueueBuffer* buffer)
    {
        var size = (int)buffer->AudioDataByteSize;
        if (size == 0)
            return;

        var limitReached = false;
        lock (session.Sync)
        {
            if (session.Stopping || session.LimitHit)
                return;
            var count = (int)Math.Min(session.MaxBytes - session.Buffer.Length, size);
            count -= count % 2;
            session.Buffer.Write(new ReadOnlySpan<byte>((void*)buffer->AudioData, count));
            if (count < size)
                session.LimitHit = limitReached = true;
        }
        if (limitReached)
            session.Owner.MaxDurationReached?.Invoke();
    }

    /// <summary>Stops capture and returns 16 kHz mono 16-bit PCM (no WAV header).</summary>
    public Task<byte[]> StopAsync()
    {
        var session = _session;
        if (session == null)
            return Task.FromResult<byte[]>([]);
        _session = null;
        return Task.Run(() => Stop(session));
    }

    private static byte[] Stop(Session session)
    {
        byte[] pcm;
        lock (session.Sync)
        {
            session.Stopping = true;
            pcm = session.Buffer.ToArray();
        }
        // Synchronous stop: no callbacks run after it returns, so the handle can go (the last <= 50 ms are dropped)
        AudioQueueStop(session.Queue, true);
        AudioQueueDispose(session.Queue, true);
        session.Handle.Free();
        return pcm;
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
        var session = _session;
        _session = null;
        if (session != null)
            Stop(session);
    }
}
