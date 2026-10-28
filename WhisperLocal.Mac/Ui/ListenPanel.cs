using System.Diagnostics;
using WhisperLocal.Llm;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Pipeline;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// The listening indicator: elapsed time, the last lines heard (with their translations), a menu to choose the
/// translation language, and buttons to open the notes and to stop. Stays until stopped; can be dragged anywhere.
/// Never takes focus, so the app you are in stays active.
/// </summary>
internal sealed class ListenPanel : IDisposable
{
    private const double Width = 560;
    private const double Padding = 10;
    private const double HeaderHeight = 24;
    private const double TextHeight = 100;
    private const double Height = Padding + TextHeight + 6 + HeaderHeight + Padding;
    private const double PopupWidth = 140;
    private const double ButtonSize = 24;
    private const double IndicatorSpace = 40 + 36 + 8; // above the recording indicator
    private const int MaxLines = 6;

    private static readonly (double R, double G, double B) Red = (1.0, 0.35, 0.35);
    private static readonly (double R, double G, double B) Orange = (1.0, 0.7, 0.24);
    private static readonly (double R, double G, double B) TextColor = (0.92, 0.93, 0.95);
    private static readonly (double R, double G, double B) TranslationColor = (0.55, 0.8, 1.0);
    private static readonly (double R, double G, double B) MutedColor = (0.6, 0.6, 0.62);

    private readonly SynchronizationContext _ui;
    private readonly Func<string, bool> _chooseLanguage;
    private readonly Func<string?> _hint;
    private readonly IntPtr _panel;
    private readonly IntPtr _background;
    private readonly IntPtr _status;
    private readonly IntPtr _popup;
    private readonly IntPtr[] _buttons;
    private readonly IntPtr _text;
    private readonly System.Threading.Timer _timer;
    private readonly List<ListenLine> _lines = [];

    private List<string> _languages = [];
    private string _language = "";
    private long _since;
    private bool _finishing;
    private string? _problem;

    /// <param name="chooseLanguage">Gets the chosen code ("" for the original); false keeps the previous choice.</param>
    /// <param name="hint">Shown instead of the text while nothing was heard yet (e.g. a permission hint), or null.</param>
    public ListenPanel(SynchronizationContext ui, Action stop, Action openNotes, Func<string, bool> chooseLanguage, Func<string?> hint)
    {
        _ui = ui;
        _chooseLanguage = chooseLanguage;
        _hint = hint;
        (_panel, _background) = Hud.CreatePanel(Width, Height, 0.92);
        Send(_panel, Sel("setIgnoresMouseEvents:"), false);
        Send(_panel, Sel("setMovableByWindowBackground:"), true);
        var content = Send(_panel, Sel("contentView"));

        var top = Height - Padding - HeaderHeight;
        _status = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(_status, Sel("setFont:"), Send(Class("NSFont"), Sel("monospacedDigitSystemFontOfSize:weight:"), 13.0, 0.3)); // semibold
        Send(_status, Sel("setLineBreakMode:"), 4L); // truncate tail
        Send(_status, Sel("setFrame:"), new NSRect(Padding + 2, top + 3, Width - Padding * 2 - PopupWidth - 2 * ButtonSize - 16, 18));
        Send(content, Sel("addSubview:"), _status);

        _popup = Retain(Send(Send(Class("NSPopUpButton"), Sel("alloc")), Sel("initWithFrame:pullsDown:"),
            new NSRect(Width - Padding - 2 * ButtonSize - 8 - PopupWidth, top, PopupWidth, HeaderHeight), false));
        Send(_popup, Sel("setControlSize:"), 1L); // NSControlSizeSmall
        Send(_popup, Sel("setFont:"), Send(Class("NSFont"), Sel("systemFontOfSize:"), 12.0));
        Send(_popup, Sel("setToolTip:"), Str("Translate into"));
        Send(_popup, Sel("setTarget:"), ActionTarget.Instance);
        Send(_popup, Sel("setAction:"), ActionTarget.ActionSelector);
        Send(_popup, Sel("setTag:"), ActionTarget.Register(OnLanguageSelected));
        Send(content, Sel("addSubview:"), _popup);

        _buttons =
        [
            PolishPanel.IconButton("folder", "Notes", "Open the notes", openNotes),
            PolishPanel.IconButton("stop.circle", "Stop", "Stop listening and save the notes", stop),
        ];
        for (var i = 0; i < _buttons.Length; i++)
        {
            Send(_buttons[i], Sel("setFrame:"), new NSRect(Width - Padding - (_buttons.Length - i) * (ButtonSize + 4) + 4, top, ButtonSize, ButtonSize));
            Send(content, Sel("addSubview:"), _buttons[i]);
        }

        _text = Retain(Send(Class("NSTextField"), Sel("wrappingLabelWithString:"), Str("")));
        Send(_text, Sel("setSelectable:"), false);
        Send(content, Sel("addSubview:"), _text);

        _timer = new System.Threading.Timer(_ => _ui.Post(_ => Tick(), null), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsVisible { get; private set; }

    /// <summary>Shows the panel for a new session; <paramref name="languages"/> are the codes offered besides the original.</summary>
    public void Show(IReadOnlyList<string> languages, string language)
    {
        _lines.Clear();
        _finishing = false;
        _problem = null;
        _since = Stopwatch.GetTimestamp();
        _languages = languages.ToList();
        if (language.Length > 0 && !_languages.Contains(language))
            _languages.Add(language);
        _language = language;

        Send(_popup, Sel("removeAllItems"));
        Send(_popup, Sel("addItemWithTitle:"), Str("Original"));
        foreach (var code in _languages)
            Send(_popup, Sel("addItemWithTitle:"), Str("→ " + Translator.NativeName(code)));
        SelectLanguage();
        Send(_popup, Sel("setEnabled:"), true);

        Tick();
        Render();
        var area = Hud.ActiveScreen();
        Hud.Show(_panel, new NSRect(area.X + (area.Width - Width) / 2, area.Y + IndicatorSpace, Width, Height), "Listening indicator");
        IsVisible = true;
        _timer.Change(1000, 1000);
    }

    /// <summary>Brings the panel back to the front (the shortcut pressed again), where the user left it.</summary>
    public void BringToFront()
    {
        if (IsVisible)
            Send(_panel, Sel("orderFrontRegardless"));
    }

    /// <summary>A new line, or one that got its translation.</summary>
    public void ShowLine(ListenLine line)
    {
        if (!_lines.Contains(line))
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
        }
        _problem = null;
        Render();
    }

    /// <summary>A short error line under the text until the next line comes.</summary>
    public void ShowProblem(string message)
    {
        _problem = message;
        Render();
    }

    /// <summary>Stopped: the lines still being recognized or translated are finished before the panel goes.</summary>
    public void ShowFinishing()
    {
        _finishing = true;
        Send(_popup, Sel("setEnabled:"), false);
        Tick();
    }

    public void Hide()
    {
        if (!IsVisible)
            return;
        IsVisible = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
    }

    private void OnLanguageSelected()
    {
        var index = (int)SendLong(_popup, Sel("indexOfSelectedItem"));
        var code = index <= 0 || index > _languages.Count ? "" : _languages[index - 1];
        if (code != _language && _chooseLanguage(code))
            _language = code;
        SelectLanguage();
    }

    private void SelectLanguage() =>
        Send(_popup, Sel("selectItemAtIndex:"), (long)(_language.Length == 0 ? 0 : _languages.IndexOf(_language) + 1));

    private void Tick()
    {
        if (_finishing)
        {
            SetStatus("… Finishing the notes", Orange);
            return;
        }
        var elapsed = Stopwatch.GetElapsedTime(_since);
        SetStatus($"● Listening  {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}", Red);
        if (_lines.Count == 0)
            Render(); // the hint may have changed
    }

    private void SetStatus(string text, (double R, double G, double B) color)
    {
        Send(_status, Sel("setStringValue:"), Str(text));
        Send(_status, Sel("setTextColor:"), Color(color));
    }

    /// <summary>The newest lines that fit, oldest first, anchored to the bottom.</summary>
    private void Render()
    {
        var textWidth = Width - Padding * 2;
        var count = _lines.Count;
        IntPtr attributed;
        NSSize size;
        while (true)
        {
            attributed = Attributed(_lines.Skip(_lines.Count - count).ToList());
            Send(_text, Sel("setAttributedStringValue:"), attributed);
            size = SendSize(_text, Sel("sizeThatFits:"), new NSSize(textWidth, 10_000));
            if (size.Height <= TextHeight || count <= 1)
                break;
            count--;
        }
        var height = Math.Min(size.Height, TextHeight);
        Send(_text, Sel("setFrame:"), new NSRect(Padding, Padding + TextHeight - height, textWidth, height));
    }

    /// <summary>Each line in the text colour, its translation under it in blue; autoreleased.</summary>
    private IntPtr Attributed(IReadOnlyList<ListenLine> lines)
    {
        var parts = new List<(string Text, (double R, double G, double B) Color)>();
        if (lines.Count == 0)
            parts.Add((_hint() ?? "Listening to what the Mac plays…", MutedColor));
        foreach (var line in lines)
        {
            if (parts.Count > 0)
                parts.Add(("\n", TextColor));
            parts.Add((line.Text, TextColor));
            if (line.Translation != null)
                parts.Add(("\n" + line.Translation, TranslationColor));
        }
        if (_problem != null)
            parts.Add(("\n✕ " + _problem, Red));

        var text = string.Concat(parts.Select(p => p.Text));
        var attributed = Send(Send(Send(Class("NSMutableAttributedString"), Sel("alloc")), Sel("initWithString:"), Str(text)), Sel("autorelease"));
        Send(attributed, Sel("addAttribute:value:range:"), Str("NSFont"), Send(Class("NSFont"), Sel("systemFontOfSize:"), 13.0),
            new NSRange(0, text.Length));
        // NSString ranges count UTF-16 code units, like .NET strings
        var at = 0;
        foreach (var (part, color) in parts)
        {
            Send(attributed, Sel("addAttribute:value:range:"), Str("NSColor"), Color(color), new NSRange(at, part.Length));
            at += part.Length;
        }
        return attributed;
    }

    private static IntPtr Color((double R, double G, double B) c) =>
        Send(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), c.R, c.G, c.B, 1.0);

    public void Dispose()
    {
        _timer.Dispose();
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        foreach (var button in _buttons)
            Release(button);
        Release(_popup);
        Release(_text);
        Release(_status);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
