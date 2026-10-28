using System.Runtime.InteropServices;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.MacNative;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Audio;

/// <summary>
/// Captures what the Mac plays (all apps) with a Core Audio process tap, macOS 14.2+: a mono tap of the system output
/// goes into a private aggregate device whose IO callback receives it; the audio keeps playing as usual. The first
/// start makes macOS ask for "System Audio Recording"; while it is not allowed, the tap delivers silence.
/// Converted to 16 kHz mono 16-bit PCM in the callback; <see cref="Drain"/> takes what came since the last call.
/// </summary>
internal sealed unsafe class SystemAudioTap : IDisposable
{
    private const int TargetRate = 16000;
    private const string Name = "WhisperLocal listening";

    private sealed class Session
    {
        public readonly object Sync = new();
        public readonly MemoryStream Pcm = new(TargetRate * 2 * 10);
        public double Ratio; // source samples per output sample
        public double Phase;
        public double Sum;
        public int Count;
        public bool HeardAnything;
        public GCHandle Handle;
        public IntPtr Description;
        public uint Tap;
        public uint Device;
        public IntPtr Proc;
    }

    private Session? _session;

    public static bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(14, 2);

    public bool IsRunning => _session != null;

    /// <summary>Whether any sound came in yet; it never does while System Audio Recording is not allowed.</summary>
    public bool HeardAnything
    {
        get
        {
            var session = _session;
            if (session == null)
                return false;
            lock (session.Sync)
                return session.HeardAnything;
        }
    }

    public void Start()
    {
        if (_session != null)
            throw new InvalidOperationException("Already capturing system audio");
        if (!IsSupported)
            throw new PlatformNotSupportedException("listening to system audio needs macOS 14.2 or later");
        LoadFramework("CoreAudio");

        var session = new Session();
        session.Handle = GCHandle.Alloc(session);
        try
        {
            WithPool(() => CreateTapAndDevice(session));
            Check(AudioDeviceCreateIOProcID(session.Device, &OnIo, GCHandle.ToIntPtr(session.Handle), out session.Proc), "AudioDeviceCreateIOProcID");
            Check(AudioDeviceStart(session.Device, session.Proc), "AudioDeviceStart");
            _session = session;
        }
        catch
        {
            Destroy(session);
            throw;
        }
    }

    private static void CreateTapAndDevice(Session session)
    {
        // A mono mixdown of everything played, from no process excluded
        session.Description = Send(Send(Class("CATapDescription"), Sel("alloc")), Sel("initMonoGlobalTapButExcludeProcesses:"),
            Send(Class("NSArray"), Sel("array")));
        if (session.Description == IntPtr.Zero)
            throw new InvalidOperationException("CATapDescription could not be created");
        Send(session.Description, Sel("setName:"), Str(Name));
        if (SendBool(session.Description, Sel("respondsToSelector:"), Sel("setPrivateTap:")))
            Send(session.Description, Sel("setPrivateTap:"), true);
        Check(AudioHardwareCreateProcessTap(session.Description, out session.Tap), "AudioHardwareCreateProcessTap");

        var tapUid = FromStr(Send(Send(session.Description, Sel("UUID")), Sel("UUIDString")))
                     ?? throw new InvalidOperationException("The audio tap has no UUID");
        var format = GetTapFormat(session.Tap);
        if ((format.FormatFlags & kAudioFormatFlagIsFloat) == 0 || format.BitsPerChannel != 32 || format.SampleRate < TargetRate)
            throw new InvalidOperationException($"Unexpected system audio format: {format.SampleRate} Hz, {format.BitsPerChannel} bit, flags {format.FormatFlags:X}");
        session.Ratio = format.SampleRate / TargetRate;
        Log.Info($"System audio tap: {format.SampleRate} Hz, {format.ChannelsPerFrame} channel(s)");

        // A private aggregate device with the tap as its input; the output device is its clock
        var tap = Dictionary(("uid", Str(tapUid)), ("drift", Bool(true)));
        var description = Dictionary(
            ("name", Str(Name)),
            ("uid", Str(Guid.NewGuid().ToString())),
            ("private", Bool(true)),
            ("stacked", Bool(false)),
            ("tapautostart", Bool(true)),
            ("taps", Send(Class("NSArray"), Sel("arrayWithObject:"), tap)));
        if (GetDefaultOutputUid() is { } output)
        {
            Send(description, Sel("setObject:forKey:"), Str(output), Str("master"));
            Send(description, Sel("setObject:forKey:"), Send(Class("NSArray"), Sel("arrayWithObject:"), Dictionary(("uid", Str(output)))),
                Str("subdevices"));
        }
        Check(AudioHardwareCreateAggregateDevice(description, out session.Device), "AudioHardwareCreateAggregateDevice");
    }

    [UnmanagedCallersOnly]
    private static int OnIo(uint device, IntPtr now, AudioBufferList* input, IntPtr inputTime, IntPtr output, IntPtr outputTime, IntPtr client)
    {
        try
        {
            if (input != null && GCHandle.FromIntPtr(client).Target is Session session)
                OnData(session, input);
        }
        catch (Exception ex)
        {
            Log.Error("System audio callback failed", ex);
        }
        return 0;
    }

    /// <summary>Mixes the channels (interleaved in one buffer, or one buffer each) to mono and downsamples by averaging.</summary>
    private static void OnData(Session session, AudioBufferList* input)
    {
        var count = (int)input->NumberBuffers;
        if (count == 0)
            return;
        var buffers = AudioBufferList.Buffers(input);
        var channels0 = Math.Max(1, (int)buffers[0].NumberChannels);
        var frames = (int)(buffers[0].DataByteSize / sizeof(float)) / channels0;
        if (frames == 0)
            return;

        Span<byte> out16 = stackalloc byte[2 * (frames / Math.Max(1, (int)session.Ratio) + 2)];
        var written = 0;
        lock (session.Sync)
        {
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                var n = 0;
                for (var b = 0; b < count; b++)
                {
                    var channels = Math.Max(1, (int)buffers[b].NumberChannels);
                    if ((frames * channels) * sizeof(float) > buffers[b].DataByteSize)
                        continue;
                    var data = (float*)buffers[b].Data;
                    for (var c = 0; c < channels; c++)
                        sum += data[f * channels + c];
                    n += channels;
                }
                var sample = n == 0 ? 0 : sum / n;
                if (sample != 0)
                    session.HeardAnything = true;

                session.Sum += sample;
                session.Count++;
                session.Phase += 1;
                if (session.Phase < session.Ratio)
                    continue;
                session.Phase -= session.Ratio;
                var value = (int)Math.Round(session.Sum / session.Count * 32767);
                session.Sum = 0;
                session.Count = 0;
                if (written + 2 > out16.Length)
                    break;
                var s = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                out16[written++] = (byte)s;
                out16[written++] = (byte)(s >> 8);
            }
            session.Pcm.Write(out16[..written]);
        }
    }

    /// <summary>The 16 kHz mono 16-bit PCM captured since the previous call; empty when not running.</summary>
    public byte[] Drain()
    {
        var session = _session;
        if (session == null)
            return [];
        lock (session.Sync)
        {
            var pcm = session.Pcm.ToArray();
            session.Pcm.SetLength(0);
            return pcm;
        }
    }

    public void Stop()
    {
        var session = _session;
        _session = null;
        if (session != null)
            Destroy(session);
    }

    private static void Destroy(Session session)
    {
        // Once the IO proc is destroyed no callback runs any more, so the handle can go
        if (session.Proc != IntPtr.Zero)
        {
            AudioDeviceStop(session.Device, session.Proc);
            AudioDeviceDestroyIOProcID(session.Device, session.Proc);
        }
        if (session.Device != 0)
            AudioHardwareDestroyAggregateDevice(session.Device);
        if (session.Tap != 0)
            AudioHardwareDestroyProcessTap(session.Tap);
        Release(session.Description);
        if (session.Handle.IsAllocated)
            session.Handle.Free();
    }

    private static AudioStreamBasicDescription GetTapFormat(uint tap)
    {
        var address = new AudioObjectPropertyAddress(kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal);
        var format = new AudioStreamBasicDescription();
        var size = (uint)sizeof(AudioStreamBasicDescription);
        Check(AudioObjectGetPropertyData(tap, address, 0, IntPtr.Zero, ref size, &format), "reading the tap format");
        return format;
    }

    private static string? GetDefaultOutputUid()
    {
        var address = new AudioObjectPropertyAddress(kAudioHardwarePropertyDefaultSystemOutputDevice, kAudioObjectPropertyScopeGlobal);
        uint id = 0;
        var size = (uint)sizeof(uint);
        if (AudioObjectGetPropertyData(kAudioObjectSystemObject, address, 0, IntPtr.Zero, ref size, &id) != 0 || id == 0)
            return null;

        var uidAddress = new AudioObjectPropertyAddress(kAudioDevicePropertyDeviceUID, kAudioObjectPropertyScopeGlobal);
        IntPtr cfString = IntPtr.Zero;
        size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(id, uidAddress, 0, IntPtr.Zero, ref size, &cfString) != 0 || cfString == IntPtr.Zero)
            return null;
        try
        {
            return FromStr(cfString);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    /// <summary>Autoreleased NSMutableDictionary.</summary>
    private static IntPtr Dictionary(params (string Key, IntPtr Value)[] entries)
    {
        var dictionary = Send(Class("NSMutableDictionary"), Sel("dictionary"));
        foreach (var (key, value) in entries)
            Send(dictionary, Sel("setObject:forKey:"), value, Str(key));
        return dictionary;
    }

    private static IntPtr Bool(bool value) => Send(Class("NSNumber"), Sel("numberWithBool:"), value);

    private static void Check(int status, string what)
    {
        if (status != 0)
            throw new InvalidOperationException($"{what} failed (OSStatus {status})");
    }

    public void Dispose() => Stop();
}
