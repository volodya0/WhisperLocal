using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// Autocomplete above the recording indicator while the hotkey is held, like ghost text in a code editor: the last
/// words you said, then the likely continuation in grey. Click-through, never takes focus. Dim while you speak,
/// bright when you pause.
/// </summary>
internal sealed class AutocompletePanel : IDisposable
{
    private const double MaxWidth = 640;
    private const double Height = 34;
    private const double IndicatorSpace = 40 + 36 + 8; // the recording indicator sits 40 pt above the bottom, 36 pt tall
    private const double DimAlpha = 0.55;

    private readonly IntPtr _panel;
    private readonly IntPtr _background;
    private readonly IntPtr _tail;
    private readonly IntPtr _continuation;

    public AutocompletePanel()
    {
        (_panel, _background) = Hud.CreatePanel(MaxWidth, Height, 0.92);
        var content = Send(_panel, Sel("contentView"));
        _tail = Label((0.92, 0.93, 0.95), TruncateHead);
        _continuation = Label((0.55, 0.62, 0.75), TruncateTail);
        Send(content, Sel("addSubview:"), _tail);
        Send(content, Sel("addSubview:"), _continuation);
    }

    public bool IsVisible { get; private set; }

    /// <summary>Shows or updates the completion; <paramref name="highlight"/> when the speaker pauses.</summary>
    public void Show(string tail, string continuation, bool highlight)
    {
        if (continuation.Trim().Length == 0)
        {
            Hide();
            return;
        }

        // The continuation keeps its leading space (or its absence, when it finishes a cut-off word)
        Send(_tail, Sel("setStringValue:"), Str("…" + tail));
        Send(_continuation, Sel("setStringValue:"), Str(continuation));
        var tailWidth = TextWidth(_tail);
        var continuationWidth = TextWidth(_continuation);
        // A long tail gives way to the continuation, which is what matters
        tailWidth = Math.Min(tailWidth, Math.Max(120, MaxWidth - 32 - continuationWidth));
        continuationWidth = Math.Min(continuationWidth, MaxWidth - 32 - tailWidth);
        var width = 16 + tailWidth + continuationWidth + 16;

        Send(_tail, Sel("setFrame:"), new NSRect(16, (Height - 20) / 2, tailWidth, 20));
        Send(_continuation, Sel("setFrame:"), new NSRect(16 + tailWidth, (Height - 20) / 2, continuationWidth, 20));

        var area = Hud.ActiveScreen();
        Send(_panel, Sel("setAlphaValue:"), highlight ? 1.0 : DimAlpha);
        Hud.Show(_panel, new NSRect(area.X + (area.Width - width) / 2, area.Y + IndicatorSpace, width, Height), "Autocomplete");
        IsVisible = true;
    }

    public void Hide()
    {
        if (!IsVisible)
            return;
        IsVisible = false;
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
    }

    private const long TruncateHead = 3, TruncateTail = 4; // NSLineBreakMode

    private static IntPtr Label((double R, double G, double B) color, long lineBreakMode)
    {
        var label = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(label, Sel("setFont:"), Send(Class("NSFont"), Sel("systemFontOfSize:weight:"), 15.0, 0.0));
        Send(label, Sel("setTextColor:"), Send(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), color.R, color.G, color.B, 1.0));
        Send(label, Sel("setLineBreakMode:"), lineBreakMode);
        return label;
    }

    private static double TextWidth(IntPtr label)
    {
        Send(label, Sel("sizeToFit"));
        return SendRect(label, Sel("frame")).Width;
    }

    public void Dispose()
    {
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        Release(_tail);
        Release(_continuation);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
