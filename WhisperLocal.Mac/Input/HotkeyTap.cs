using System.Runtime.InteropServices;
using static WhisperLocal.Mac.Interop.MacNative;

namespace WhisperLocal.Mac.Input;

/// <summary>
/// Push-to-talk hotkey via a CGEventTap (the macOS counterpart of WH_KEYBOARD_LL).
/// Supports a single key ("RightOption", "F13") or a combination ("LeftControl+LeftCommand"): active while all
/// keys are held, released when any of them goes up, cancelled when another key is pressed meanwhile.
/// Needs the Accessibility permission; until it is granted, creating the tap is retried every few seconds.
/// The tap runs on the main run loop; events are posted to the given context.
/// </summary>
internal sealed unsafe class HotkeyTap : IDisposable
{
    /// <summary>Marks events posted by WhisperLocal itself (Cmd+V), so the tap ignores them.</summary>
    public const long InjectedMarker = 0x574C; // "WL"

    // Virtual key codes from HIToolbox/Events.h (kVK_*)
    private static readonly Dictionary<string, ushort> KeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LeftControl"] = 59, ["RightControl"] = 62,
        ["LeftOption"] = 58, ["RightOption"] = 61,
        ["LeftCommand"] = 55, ["RightCommand"] = 54,
        ["LeftShift"] = 56, ["RightShift"] = 60,
        ["Fn"] = 63,
        ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118, ["F5"] = 96, ["F6"] = 97, ["F7"] = 98, ["F8"] = 100,
        ["F9"] = 101, ["F10"] = 109, ["F11"] = 103, ["F12"] = 111, ["F13"] = 105, ["F14"] = 107, ["F15"] = 113,
        ["F16"] = 106, ["F17"] = 64, ["F18"] = 79, ["F19"] = 80, ["F20"] = 90,
        ["Escape"] = 53, ["Space"] = 49, ["Tab"] = 48, ["Return"] = 36,
        ["Home"] = 115, ["End"] = 119, ["PageUp"] = 116, ["PageDown"] = 121, ["ForwardDelete"] = 117,
    };

    // Short and Windows names, so the same config works on both systems
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = "LeftControl", ["Control"] = "LeftControl", ["LeftCtrl"] = "LeftControl", ["RightCtrl"] = "RightControl",
        ["LControlKey"] = "LeftControl", ["RControlKey"] = "RightControl",
        ["Alt"] = "LeftOption", ["Option"] = "LeftOption", ["LeftAlt"] = "LeftOption", ["RightAlt"] = "RightOption",
        ["AltGr"] = "RightOption", ["LMenu"] = "LeftOption", ["RMenu"] = "RightOption",
        ["Cmd"] = "LeftCommand", ["Command"] = "LeftCommand", ["Win"] = "LeftCommand", ["LeftWin"] = "LeftCommand",
        ["RightWin"] = "RightCommand", ["LWin"] = "LeftCommand", ["RWin"] = "RightCommand",
        ["Shift"] = "LeftShift", ["LShiftKey"] = "LeftShift", ["RShiftKey"] = "RightShift",
        ["Globe"] = "Fn", ["Function"] = "Fn",
    };

    private static readonly Dictionary<ushort, string> DisplayNames = new()
    {
        [59] = "Left Control", [62] = "Right Control",
        [58] = "Left Option", [61] = "Right Option",
        [55] = "Left Command", [54] = "Right Command",
        [56] = "Left Shift", [60] = "Right Shift",
        [63] = "Fn",
    };

    // Device-dependent modifier bits (IOKit NX_DEVICE*KEYMASK) tell left and right keys apart
    private static readonly Dictionary<ushort, ulong> ModifierMasks = new()
    {
        [59] = 0x1, [62] = 0x2000,
        [56] = 0x2, [60] = 0x4,
        [55] = 0x8, [54] = 0x10,
        [58] = 0x20, [61] = 0x40,
        [63] = kCGEventFlagMaskSecondaryFn,
    };

    private static HotkeyTap? _instance; // the tap callback is static

    private readonly SynchronizationContext _context;
    private readonly System.Threading.Timer _retry;
    private IntPtr _tap;
    private IntPtr _source;
    private ushort[] _keys = [];
    private bool _active;
    private bool _warnedNoPermission;

    public bool SuppressKey { get; set; }
    public bool IsInstalled => _tap != IntPtr.Zero;

    public event Action? HotkeyDown;
    public event Action? HotkeyUp;

    /// <summary>Another key was pressed while the hotkey was held (e.g. Ctrl+Cmd+Q).</summary>
    public event Action? HotkeyCancel;

    /// <summary>Raised once when the tap cannot be created (no Accessibility permission yet).</summary>
    public event Action? PermissionMissing;

    /// <summary>Raised when the tap was created after the permission had been missing.</summary>
    public event Action? Installed;

    public HotkeyTap(SynchronizationContext context)
    {
        _context = context;
        _instance = this;
        _retry = new System.Threading.Timer(_ => _context.Post(_ => TryInstall(), null), null, Timeout.Infinite, Timeout.Infinite);
        TryInstall();
    }

    private void TryInstall()
    {
        if (_tap != IntPtr.Zero)
            return;

        const ulong mask = (1UL << (int)kCGEventKeyDown) | (1UL << (int)kCGEventKeyUp) | (1UL << (int)kCGEventFlagsChanged);
        var tap = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionDefault, mask, &Callback, IntPtr.Zero);
        if (tap == IntPtr.Zero)
        {
            if (!_warnedNoPermission)
            {
                _warnedNoPermission = true;
                Log.Warn("Cannot install the keyboard event tap: Accessibility permission is missing, retrying until it is granted");
                PermissionMissing?.Invoke();
            }
            _retry.Change(3000, Timeout.Infinite);
            return;
        }

        _tap = tap;
        _source = CFMachPortCreateRunLoopSource(IntPtr.Zero, tap, 0);
        CFRunLoopAddSource(CFRunLoopGetMain(), _source, kCFRunLoopCommonModes);
        CGEventTapEnable(tap, true);
        Log.Info("Keyboard event tap installed");
        if (_warnedNoPermission)
            Installed?.Invoke();
    }

    public void SetHotkey(ushort[] keys)
    {
        _keys = keys;
        _active = false;
    }

    /// <summary>Parses "RightOption", "LeftControl+LeftCommand", "Ctrl+Cmd", "LControlKey+LWin" etc. into key codes.</summary>
    public static ushort[] ParseHotkey(string text)
    {
        var keys = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseKey)
            .Distinct()
            .ToArray();
        if (keys.Length == 0)
            throw new ArgumentException("Hotkey is empty");
        return keys;
    }

    /// <summary>Human-readable form, e.g. "Left Control + Left Command".</summary>
    public static string Describe(string text)
    {
        try
        {
            return string.Join(" + ", ParseHotkey(text).Select(code =>
                DisplayNames.TryGetValue(code, out var name)
                    ? name
                    : KeyCodes.First(k => k.Value == code).Key));
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static ushort ParseKey(string name)
    {
        if (Aliases.TryGetValue(name, out var canonical))
            name = canonical;
        if (KeyCodes.TryGetValue(name, out var code))
            return code;
        throw new ArgumentException($"Unknown hotkey key '{name}'. Use names joined with '+', e.g. RightOption, Fn, F13, LeftControl+LeftCommand.");
    }

    [UnmanagedCallersOnly]
    private static IntPtr Callback(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo)
    {
        try
        {
            return _instance?.Handle(type, evt) ?? evt;
        }
        catch (Exception ex)
        {
            // An exception escaping into native code would kill the process
            Log.Error("Keyboard tap callback failed", ex);
            return evt;
        }
    }

    private IntPtr Handle(uint type, IntPtr evt)
    {
        if (type is kCGEventTapDisabledByTimeout or kCGEventTapDisabledByUserInput)
        {
            // macOS disables a tap that responded too slowly; switch it back on
            Log.Warn("Keyboard event tap was disabled by the system, re-enabling");
            if (_tap != IntPtr.Zero)
                CGEventTapEnable(_tap, true);
            return evt;
        }

        if (_keys.Length == 0 || CGEventGetIntegerValueField(evt, kCGEventSourceUserData) == InjectedMarker)
            return evt;

        var code = (ushort)CGEventGetIntegerValueField(evt, kCGKeyboardEventKeycode);
        var flags = CGEventGetFlags(evt);
        bool isDown, isUp;
        if (type == kCGEventFlagsChanged)
        {
            if (!ModifierMasks.TryGetValue(code, out var bit))
                return evt; // Caps Lock and the like
            isDown = (flags & bit) != 0;
            isUp = !isDown;
        }
        else
        {
            isDown = type == kCGEventKeyDown;
            isUp = type == kCGEventKeyUp;
        }

        if (Array.IndexOf(_keys, code) >= 0)
        {
            // Swallowing a modifier (or any key of a combo) would break other shortcuts like Cmd+Tab,
            // so only a single non-modifier key is suppressed
            var suppress = SuppressKey && _keys.Length == 1 && !ModifierMasks.ContainsKey(code);

            // Auto-repeat re-sends key-down while held; _active filters it out
            if (isDown && !_active && OtherKeysHeld(code, flags))
            {
                _active = true;
                _context.Post(_ => HotkeyDown?.Invoke(), null);
            }
            else if (isUp && _active)
            {
                _active = false;
                _context.Post(_ => HotkeyUp?.Invoke(), null);
            }

            if (suppress)
                return IntPtr.Zero;
        }
        else if (isDown && _active)
        {
            _active = false;
            _context.Post(_ => HotkeyCancel?.Invoke(), null);
        }

        return evt;
    }

    /// <summary>
    /// Physical state of the other hotkey keys: modifiers from the event's flags, other keys from the HID state.
    /// Asking the system (instead of tracking downs/ups) avoids stuck keys when a key-up is missed.
    /// </summary>
    private bool OtherKeysHeld(ushort current, ulong flags)
    {
        foreach (var k in _keys)
        {
            if (k == current)
                continue;
            var held = ModifierMasks.TryGetValue(k, out var bit)
                ? (flags & bit) != 0
                : CGEventSourceKeyState(kCGEventSourceStateHIDSystemState, k);
            if (!held)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        _retry.Dispose();
        if (_tap == IntPtr.Zero)
            return;
        CGEventTapEnable(_tap, false);
        CFRunLoopRemoveSource(CFRunLoopGetMain(), _source, kCFRunLoopCommonModes);
        CFMachPortInvalidate(_tap);
        CFRelease(_source);
        CFRelease(_tap);
        _tap = _source = IntPtr.Zero;
    }
}
