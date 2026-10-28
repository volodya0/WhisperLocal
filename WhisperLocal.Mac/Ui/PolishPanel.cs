using System.Diagnostics;
using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// The grammar check's proposal above the indicator after a dictation: the polished text with the changed words
/// highlighted, and small icon buttons to insert it (replacing what was pasted), copy it or close. Never takes focus,
/// so a click leaves the app you are writing in active. Goes away after a while, later while the mouse is over it.
/// </summary>
internal sealed class PolishPanel : IDisposable
{
    private const double Width = 560;
    private const double Padding = 10;
    private const double ButtonSize = 24;
    private const double ButtonGap = 2;
    private const double HeaderHeight = 16;
    private const double IndicatorSpace = 40 + 36 + 8; // the recording indicator sits 40 pt above the bottom, 36 pt tall
    private const double FontSize = 13;

    private static readonly (double R, double G, double B) TextColor = (0.92, 0.93, 0.95);
    private static readonly (double R, double G, double B) ChangedColor = (0.45, 0.88, 0.5);

    private readonly SynchronizationContext _ui;
    private readonly IntPtr _panel;
    private readonly IntPtr _background;
    private readonly IntPtr _header;
    private readonly IntPtr _text;
    private readonly IntPtr[] _buttons;
    private readonly System.Threading.Timer _timer;

    private long _since;
    private TimeSpan _showFor;

    public PolishPanel(SynchronizationContext ui, Action insert, Action copy)
    {
        _ui = ui;
        (_panel, _background) = Hud.CreatePanel(Width, 80, 0.94);
        var content = Send(_panel, Sel("contentView"));

        _header = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("✎ Polished")));
        Send(_header, Sel("setFont:"), Send(Class("NSFont"), Sel("systemFontOfSize:weight:"), 11.0, 0.3));
        Send(_header, Sel("setTextColor:"), Send(Class("NSColor"), Sel("secondaryLabelColor")));
        Send(content, Sel("addSubview:"), _header);

        _text = Retain(Send(Class("NSTextField"), Sel("wrappingLabelWithString:"), Str("")));
        Send(_text, Sel("setSelectable:"), false);
        Send(content, Sel("addSubview:"), _text);

        _buttons =
        [
            IconButton("text.insert", "Insert", "Replace what was pasted with this text", insert),
            IconButton("doc.on.doc", "Copy", "Copy to the clipboard", copy),
            IconButton("xmark", "Close", "Close", Hide),
        ];
        foreach (var button in _buttons)
            Send(content, Sel("addSubview:"), button);

        _timer = new System.Threading.Timer(_ => _ui.Post(_ => Tick(), null), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsVisible { get; private set; }

    /// <summary>Shows the polished text; <paramref name="parts"/> are its words, marked where they differ from what was said.</summary>
    public void Show(IReadOnlyList<(string Text, bool Changed)> parts, TimeSpan duration)
    {
        Send(_text, Sel("setAttributedStringValue:"), Attributed(parts));

        var textWidth = Width - Padding * 2 - _buttons.Length * (ButtonSize + ButtonGap) - 8;
        var textHeight = Math.Min(SendSize(_text, Sel("sizeThatFits:"), new NSSize(textWidth, 10_000)).Height, 160);
        var height = Padding + HeaderHeight + 4 + Math.Max(textHeight, ButtonSize) + Padding;

        // AppKit's y axis points up: the header and the buttons at the top, the text below the header
        Send(_header, Sel("setFrame:"), new NSRect(Padding + 2, height - Padding - HeaderHeight, 200, HeaderHeight));
        Send(_text, Sel("setFrame:"), new NSRect(Padding, Padding, textWidth, height - Padding * 2 - HeaderHeight - 4));
        for (var i = 0; i < _buttons.Length; i++)
        {
            var x = Width - Padding - (_buttons.Length - i) * (ButtonSize + ButtonGap) + ButtonGap;
            Send(_buttons[i], Sel("setFrame:"), new NSRect(x, height - Padding - ButtonSize + 2, ButtonSize, ButtonSize));
        }

        var area = Hud.ActiveScreen();
        Send(_panel, Sel("setIgnoresMouseEvents:"), false);
        Hud.Show(_panel, new NSRect(area.X + (area.Width - Width) / 2, area.Y + IndicatorSpace, Width, height), "Grammar check");
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

    private void Tick()
    {
        if (IsVisible && Stopwatch.GetElapsedTime(_since) >= _showFor && !MouseInside())
            Hide();
    }

    private bool MouseInside() =>
        SendRect(_panel, Sel("frame")).Contains(SendPoint(Class("NSEvent"), Sel("mouseLocation")));

    /// <summary>The text in the panel's colours, the changed words in green; autoreleased.</summary>
    private static IntPtr Attributed(IReadOnlyList<(string Text, bool Changed)> parts)
    {
        var text = string.Concat(parts.Select(p => p.Text));
        var attributed = Send(Send(Send(Class("NSMutableAttributedString"), Sel("alloc")), Sel("initWithString:"), Str(text)), Sel("autorelease"));
        var all = new NSRange(0, text.Length);
        Send(attributed, Sel("addAttribute:value:range:"), Str("NSFont"), Send(Class("NSFont"), Sel("systemFontOfSize:"), FontSize), all);
        Send(attributed, Sel("addAttribute:value:range:"), Str("NSColor"), Color(TextColor), all);

        // NSString ranges count UTF-16 code units, like .NET strings
        var at = 0;
        foreach (var (part, changed) in parts)
        {
            var word = part.TrimEnd().Length;
            if (changed && word > 0)
                Send(attributed, Sel("addAttribute:value:range:"), Str("NSColor"), Color(ChangedColor), new NSRange(at, word));
            at += part.Length;
        }
        return attributed;
    }

    private static IntPtr Color((double R, double G, double B) c) =>
        Send(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), c.R, c.G, c.B, 1.0);

    /// <summary>A borderless button with an SF Symbol (macOS 11+), or its title where the symbol is missing.</summary>
    internal static IntPtr IconButton(string symbol, string title, string tooltip, Action action)
    {
        var image = Send(Class("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), Str(symbol), Str(title));
        var button = image != IntPtr.Zero
            ? Send(Class("NSButton"), Sel("buttonWithImage:target:action:"), image, ActionTarget.Instance, ActionTarget.ActionSelector)
            : Send(Class("NSButton"), Sel("buttonWithTitle:target:action:"), Str(title), ActionTarget.Instance, ActionTarget.ActionSelector);
        Retain(button);
        Send(button, Sel("setTag:"), ActionTarget.Register(action));
        Send(button, Sel("setBordered:"), false);
        Send(button, Sel("setToolTip:"), Str(tooltip));
        return button;
    }

    public void Dispose()
    {
        _timer.Dispose();
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        foreach (var button in _buttons)
            Release(button);
        Release(_text);
        Release(_header);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
