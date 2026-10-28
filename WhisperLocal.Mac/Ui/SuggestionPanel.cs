using System.Diagnostics;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// Continuation hints above the recording indicator: a non-activating panel (focus stays in the app being written
/// in) with one clickable row per hint, "⌥1 …", "⌥2 …". Hides itself after a while, later while the mouse is over it.
/// </summary>
internal sealed class SuggestionPanel : IDisposable
{
    public const int MaxRows = 5;

    private const double Width = 520;
    private const double RowHeight = 26;
    private const double HeaderHeight = 22;
    private const double Padding = 8;
    private const double IndicatorSpace = 40 + 36 + 8; // the recording indicator sits 40 pt above the bottom, 36 pt tall

    private const ulong NSWindowStyleMaskBorderless = 0;
    private const ulong NSWindowStyleMaskNonactivatingPanel = 1 << 7;
    private const ulong NSBackingStoreBuffered = 2;
    private const long NSStatusWindowLevel = 25;
    private const long CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6, FullScreenAuxiliary = 1 << 8;

    private readonly SynchronizationContext _ui;
    private readonly IntPtr _panel;
    private readonly IntPtr _header;
    private readonly IntPtr[] _rows = new IntPtr[MaxRows];
    private readonly IntPtr _background;
    private readonly System.Threading.Timer _timer;

    private long _since;
    private TimeSpan _showFor;

    public SuggestionPanel(SynchronizationContext ui, Action<int> choose)
    {
        _ui = ui;
        _panel = Send(Send(Class("NSPanel"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
            new NSRect(0, 0, Width, 100), NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel, NSBackingStoreBuffered, false);
        Send(_panel, Sel("setReleasedWhenClosed:"), false);
        Send(_panel, Sel("setHidesOnDeactivate:"), false);
        Send(_panel, Sel("setBecomesKeyOnlyIfNeeded:"), true);
        Send(_panel, Sel("setLevel:"), NSStatusWindowLevel);
        Send(_panel, Sel("setOpaque:"), false);
        Send(_panel, Sel("setHasShadow:"), true);
        Send(_panel, Sel("setBackgroundColor:"), Send(Class("NSColor"), Sel("clearColor")));
        Send(_panel, Sel("setCollectionBehavior:"), CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);
        Send(_panel, Sel("setAppearance:"), Send(Class("NSAppearance"), Sel("appearanceNamed:"), Str("NSAppearanceNameDarkAqua")));

        var content = Send(_panel, Sel("contentView"));
        Send(content, Sel("setWantsLayer:"), true);
        var layer = Send(content, Sel("layer"));
        _background = MacNative.CGColorCreateGenericGray(0.125, 0.94);
        Send(layer, Sel("setBackgroundColor:"), _background);
        Send(layer, Sel("setCornerRadius:"), 10.0);

        _header = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(_header, Sel("setFont:"), Send(Class("NSFont"), Sel("systemFontOfSize:"), 11.0));
        Send(_header, Sel("setTextColor:"), Send(Class("NSColor"), Sel("secondaryLabelColor")));
        Send(content, Sel("addSubview:"), _header);

        for (var i = 0; i < MaxRows; i++)
        {
            var index = i;
            var row = Retain(Send(Class("NSButton"), Sel("buttonWithTitle:target:action:"),
                Str(""), ActionTarget.Instance, ActionTarget.ActionSelector));
            Send(row, Sel("setTag:"), ActionTarget.Register(() => choose(index)));
            Send(row, Sel("setBordered:"), false);
            Send(row, Sel("setAlignment:"), 0L); // NSTextAlignmentLeft
            Send(row, Sel("setLineBreakMode:"), 4L); // truncate tail
            Send(row, Sel("setFont:"), Send(Class("NSFont"), Sel("systemFontOfSize:"), 13.0));
            Send(content, Sel("addSubview:"), row);
            _rows[i] = row;
        }

        _timer = new System.Threading.Timer(_ => _ui.Post(_ => Tick(), null), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsVisible { get; private set; }

    /// <summary>Shows up to <see cref="MaxRows"/> hints for <paramref name="duration"/> (longer while hovered).</summary>
    public void Show(IReadOnlyList<string> hints, TimeSpan duration)
    {
        var count = Math.Min(hints.Count, MaxRows);
        var height = Padding * 2 + HeaderHeight + count * RowHeight;

        var keys = count == 1 ? "⌥1" : $"⌥1–{count}";
        Send(_header, Sel("setStringValue:"), Str($"Continue · {keys} or click to insert · Esc or any key to close"));
        Send(_header, Sel("setFrame:"), new NSRect(14, height - Padding - HeaderHeight + 2, Width - 28, 16));
        for (var i = 0; i < MaxRows; i++)
        {
            var row = _rows[i];
            Send(row, Sel("setHidden:"), i >= count);
            if (i >= count)
                continue;
            Send(row, Sel("setTitle:"), Str($"⌥{i + 1}   {hints[i]}"));
            // Rows from the top: AppKit's y axis points up
            Send(row, Sel("setFrame:"), new NSRect(10, height - Padding - HeaderHeight - (i + 1) * RowHeight, Width - 20, RowHeight));
        }

        var area = ScreenUnderMouse();
        Send(_panel, Sel("setFrame:display:"), new NSRect(area.X + (area.Width - Width) / 2, area.Y + IndicatorSpace, Width, height), true);
        Send(_panel, Sel("orderFrontRegardless"));

        IsVisible = true;
        _since = Stopwatch.GetTimestamp();
        _showFor = duration;
        _timer.Change(250, 250);
    }

    public void Hide()
    {
        if (!IsVisible)
            return;
        IsVisible = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
    }

    /// <summary>Raised when the panel hides itself after its time is up.</summary>
    public event Action? Expired;

    private void Tick()
    {
        if (!IsVisible || Stopwatch.GetElapsedTime(_since) < _showFor || MouseInside())
            return;
        Hide();
        Expired?.Invoke();
    }

    private bool MouseInside() =>
        SendRect(_panel, Sel("frame")).Contains(SendPoint(Class("NSEvent"), Sel("mouseLocation")));

    /// <summary>Visible frame (without menu bar and Dock) of the screen with the mouse pointer.</summary>
    private static NSRect ScreenUnderMouse()
    {
        var mouse = SendPoint(Class("NSEvent"), Sel("mouseLocation"));
        foreach (var s in Items(Send(Class("NSScreen"), Sel("screens"))))
        {
            if (SendRect(s, Sel("frame")).Contains(mouse))
                return SendRect(s, Sel("visibleFrame"));
        }
        return SendRect(Send(Class("NSScreen"), Sel("mainScreen")), Sel("visibleFrame"));
    }

    public void Dispose()
    {
        _timer.Dispose();
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        foreach (var row in _rows)
            Release(row);
        Release(_header);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
