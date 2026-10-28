using WhisperLocal.Mac.Interop;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// Live hints above the recording indicator while the hotkey is held: one line of next words ("set up · configure ·
/// install") and, for English, a correction line ("✎ I am agree → I agree"). Click-through and never takes focus.
/// Dim while you speak, bright when you pause.
/// </summary>
internal sealed class SuggestionPanel : IDisposable
{
    private const double MaxWidth = 620;
    private const double MinWidth = 220;
    private const double LineHeight = 22;
    private const double Padding = 7;
    private const double IndicatorSpace = 40 + 36 + 8; // the recording indicator sits 40 pt above the bottom, 36 pt tall

    private const double DimAlpha = 0.55;

    private readonly IntPtr _panel;
    private readonly IntPtr _hints;
    private readonly IntPtr _correction;
    private readonly IntPtr _background;

    public SuggestionPanel()
    {
        (_panel, _background) = Hud.CreatePanel(MaxWidth, 40, 0.92);
        var content = Send(_panel, Sel("contentView"));

        _hints = Label(15.0, 0.3, (0.92, 0.93, 0.95)); // medium weight
        _correction = Label(13.0, 0.0, (1.0, 0.78, 0.35));
        Send(content, Sel("addSubview:"), _hints);
        Send(content, Sel("addSubview:"), _correction);
    }

    public bool IsVisible { get; private set; }

    /// <summary>Shows or updates the hints; <paramref name="highlight"/> when the speaker pauses.</summary>
    public void Show(IReadOnlyList<string> next, string correction, bool highlight)
    {
        var hintsText = string.Join("   ·   ", next);
        var correctionText = correction.Length > 0 ? "✎  " + correction : "";
        var lines = (hintsText.Length > 0 ? 1 : 0) + (correctionText.Length > 0 ? 1 : 0);
        if (lines == 0)
        {
            Hide();
            return;
        }

        Send(_hints, Sel("setStringValue:"), Str(hintsText));
        Send(_correction, Sel("setStringValue:"), Str(correctionText));
        var width = Math.Clamp(Math.Max(TextWidth(_hints), TextWidth(_correction)) + 32, MinWidth, MaxWidth);
        var height = Padding * 2 + lines * LineHeight;

        // Lines from the top: AppKit's y axis points up
        var y = height - Padding - LineHeight;
        Send(_hints, Sel("setHidden:"), hintsText.Length == 0);
        if (hintsText.Length > 0)
        {
            Send(_hints, Sel("setFrame:"), new NSRect(16, y, width - 32, LineHeight - 2));
            y -= LineHeight;
        }
        Send(_correction, Sel("setHidden:"), correctionText.Length == 0);
        Send(_correction, Sel("setFrame:"), new NSRect(16, y, width - 32, LineHeight - 2));

        var area = Hud.ActiveScreen();
        Send(_panel, Sel("setAlphaValue:"), highlight ? 1.0 : DimAlpha);
        Hud.Show(_panel, new NSRect(area.X + (area.Width - width) / 2, area.Y + IndicatorSpace, width, height), "Live hints");
        IsVisible = true;
    }

    public void Hide()
    {
        if (!IsVisible)
            return;
        IsVisible = false;
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
    }

    private static IntPtr Label(double size, double weight, (double R, double G, double B) color)
    {
        var label = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(label, Sel("setFont:"), Send(Class("NSFont"), Sel("systemFontOfSize:weight:"), size, weight));
        Send(label, Sel("setTextColor:"), Send(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), color.R, color.G, color.B, 1.0));
        Send(label, Sel("setLineBreakMode:"), 4L); // truncate tail
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
        Release(_hints);
        Release(_correction);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
