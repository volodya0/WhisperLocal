using System.Diagnostics;
using WhisperLocal.Llm;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Pipeline;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// The listening indicator, first just above the recording indicator: elapsed time, a menu to choose the translation
/// language, buttons to open the notes and to stop, and below them the session's lines in a scrolling list, with the
/// sentence still being spoken greyed at the end. While translating, the original and its translation stand side by
/// side, row by row; new lines and translations fade in. New lines scroll into view unless you scrolled up to read.
/// Resizable by its edges, movable by its background; keeps its size and place for the next session. Stays until
/// stopped. Never takes focus, so the app you are in stays active.
/// </summary>
internal sealed class ListenPanel : IDisposable
{
    private const double Width = 720;
    private const double Height = 170;
    private const double MinWidth = 420;
    private const double MinHeight = 110;
    private const double Padding = 12;
    private const double HeaderHeight = 24;
    private const double ColumnGap = 20;
    private const double RowGap = 6;
    private const double PopupWidth = 150;
    private const double ButtonSize = 24;
    private const double IndicatorSpace = 40 + 36 + 8; // the recording indicator sits 40 pt above the bottom, 36 pt tall
    private const int MaxLines = 300; // the panel's history; the notes file keeps everything
    private const double FadeSeconds = 0.35;

    private static readonly (double R, double G, double B) Red = (1.0, 0.35, 0.35);
    private static readonly (double R, double G, double B) Orange = (1.0, 0.7, 0.24);
    private static readonly (double R, double G, double B) TextColor = (0.92, 0.93, 0.95);
    private static readonly (double R, double G, double B) TranslationColor = (0.55, 0.8, 1.0);
    private static readonly (double R, double G, double B) MutedColor = (0.6, 0.6, 0.62);

    // Who spoke: the user green, "Them" in the plain text color, Speaker 1, 2, … each in its own color (cycling)
    private static readonly (double R, double G, double B) MeColor = (0.55, 0.9, 0.55);
    private static readonly (double R, double G, double B)[] SpeakerColors =
    [
        (1.0, 0.78, 0.45), (0.5, 0.78, 1.0), (1.0, 0.6, 0.78), (0.75, 0.65, 1.0),
        (0.45, 0.88, 0.85), (0.95, 0.9, 0.5), (1.0, 0.55, 0.45), (0.75, 0.95, 0.45),
    ];

    /// <summary>One row: a line and its translation next to it.</summary>
    private sealed record Row(IntPtr Original, IntPtr Translation);

    private readonly SynchronizationContext _ui;
    private readonly Func<string, bool> _chooseLanguage;
    private readonly Func<string?> _hint;
    private readonly IntPtr _panel;
    private readonly IntPtr _background;
    private readonly IntPtr _status;
    private readonly IntPtr _popup;
    private readonly IntPtr[] _buttons;
    private readonly IntPtr _scroll;
    private readonly IntPtr _list; // the scroll view's document: rows top to bottom
    private readonly List<Row> _rows = [];
    private readonly System.Threading.Timer _timer;
    private readonly List<ListenLine> _lines = [];

    private List<string> _languages = [];
    private string _language = "";
    private long _since;
    private bool _finishing;
    private string? _problem;
    private string? _provisional;
    private string? _provisionalTranslation;
    private bool _placed;

    /// <param name="chooseLanguage">Gets the chosen code ("" for the original); false keeps the previous choice.</param>
    /// <param name="hint">Shown instead of the text while nothing was heard yet (e.g. a permission hint), or null.</param>
    public ListenPanel(SynchronizationContext ui, Action stop, Action openNotes, Func<string, bool> chooseLanguage, Func<string?> hint)
    {
        _ui = ui;
        _chooseLanguage = chooseLanguage;
        _hint = hint;
        (_panel, _background) = Hud.CreatePanel(Width, Height, 0.92, resizable: true);
        Send(_panel, Sel("setIgnoresMouseEvents:"), false);
        Send(_panel, Sel("setMovableByWindowBackground:"), true);
        Send(_panel, Sel("setMinSize:"), new NSSize(MinWidth, MinHeight));
        var content = Send(_panel, Sel("contentView"));

        _status = Retain(Send(Class("NSTextField"), Sel("labelWithString:"), Str("")));
        Send(_status, Sel("setFont:"), Send(Class("NSFont"), Sel("monospacedDigitSystemFontOfSize:weight:"), 13.0, 0.3)); // semibold
        Send(_status, Sel("setLineBreakMode:"), 4L); // truncate tail
        Send(content, Sel("addSubview:"), _status);

        _popup = Retain(Send(Send(Class("NSPopUpButton"), Sel("alloc")), Sel("initWithFrame:pullsDown:"),
            new NSRect(0, 0, PopupWidth, HeaderHeight), false));
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
        foreach (var button in _buttons)
            Send(content, Sel("addSubview:"), button);

        // The lines scroll; the scroller shows only while scrolling (overlay style)
        _scroll = Send(Send(Class("NSScrollView"), Sel("alloc")), Sel("initWithFrame:"), new NSRect(0, 0, 100, 100));
        Send(_scroll, Sel("setDrawsBackground:"), false);
        Send(_scroll, Sel("setHasVerticalScroller:"), true);
        Send(_scroll, Sel("setAutohidesScrollers:"), true);
        Send(_scroll, Sel("setScrollerStyle:"), 1L); // NSScrollerStyleOverlay
        Send(_scroll, Sel("setWantsLayer:"), true);
        _list = Hud.FlippedView(new NSRect(0, 0, 100, 100));
        Send(_list, Sel("setWantsLayer:"), true);
        Send(_scroll, Sel("setDocumentView:"), _list);
        Send(content, Sel("addSubview:"), _scroll);

        ActionTarget.Observe(_panel, "NSWindowDidResizeNotification", () =>
        {
            Layout();
            Render(fadeIn: null);
        });
        Layout();

        _timer = new System.Threading.Timer(_ => _ui.Post(_ => Tick(), null), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsVisible { get; private set; }

    /// <summary>Shows the panel for a new session; <paramref name="languages"/> are the codes offered besides the original.</summary>
    public void Show(IReadOnlyList<string> languages, string language)
    {
        _lines.Clear();
        _finishing = false;
        _problem = null;
        _provisional = null;
        _provisionalTranslation = null;
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

        // The first time above the recording indicator; afterwards where (and as large as) the user left it
        NSRect frame;
        if (_placed)
            frame = SendRect(_panel, Sel("frame"));
        else
        {
            var area = Hud.ActiveScreen();
            frame = new NSRect(area.X + (area.Width - Width) / 2, area.Y + IndicatorSpace, Width, Height);
            _placed = true;
        }
        Hud.Show(_panel, frame, "Listening indicator");
        Layout();
        Tick();
        Render(fadeIn: null);
        IsVisible = true;
        _timer.Change(1000, 1000);
    }

    /// <summary>Brings the panel back to the front (the shortcut pressed again), where the user left it.</summary>
    public void BringToFront()
    {
        if (IsVisible)
            Send(_panel, Sel("orderFrontRegardless"));
    }

    /// <summary>A new line, or one that got its translation; either fades in.</summary>
    public void ShowLine(ListenLine line)
    {
        var isNew = !_lines.Contains(line);
        if (isNew)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
            _problem = null;
        }
        Render(fadeIn: line, translationOnly: !isNew);
    }

    /// <summary>The sentence being spoken so far, greyed under the lines; null removes it.</summary>
    public void ShowProvisional(string? text)
    {
        if (text == _provisional)
            return;
        _provisional = text;
        Render(fadeIn: null);
    }

    /// <summary>The translation of the sentence being spoken, greyed next to it; null removes it.</summary>
    public void ShowProvisionalTranslation(string? text)
    {
        if (text == _provisionalTranslation)
            return;
        _provisionalTranslation = text;
        Render(fadeIn: null);
    }

    /// <summary>A short error after the time until the next line comes.</summary>
    public void ShowProblem(string message)
    {
        _problem = message;
        Tick();
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
        {
            _language = code;
            _provisionalTranslation = null; // in the language before
            Render(fadeIn: null); // one column or two
        }
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
        var time = $"● Listening  {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        SetStatus(_problem == null ? time : $"{time}  ✕ {_problem}", Red);
        if (_lines.Count == 0 && _provisional == null)
            Render(fadeIn: null); // the hint may have changed
    }

    private void SetStatus(string text, (double R, double G, double B) color)
    {
        Send(_status, Sel("setStringValue:"), Str(text));
        Send(_status, Sel("setTextColor:"), Color(color));
    }

    /// <summary>The color of a line's speaker: the user, Speaker 1, 2, …, or the plain text color ("Them", or no speakers).</summary>
    private static (double R, double G, double B) SpeakerColor(ListenLine line) => line.Speaker switch
    {
        0 => MeColor,
        { } n => SpeakerColors[(n - 1) % SpeakerColors.Length],
        null => TextColor,
    };

    /// <summary>A translation in its speaker's color, a third of the way to grey.</summary>
    private static (double R, double G, double B) Dim((double R, double G, double B) c) =>
        (c.R * 0.67 + MutedColor.R * 0.33, c.G * 0.67 + MutedColor.G * 0.33, c.B * 0.67 + MutedColor.B * 0.33);

    /// <summary>Places the header (status left; menu and buttons right) and the list below it for the panel's size.</summary>
    private void Layout()
    {
        var size = SendRect(Send(_panel, Sel("contentView")), Sel("bounds"));
        var top = size.Height - Padding - HeaderHeight;
        var right = size.Width - Padding;
        for (var i = 0; i < _buttons.Length; i++)
            Send(_buttons[i], Sel("setFrame:"), new NSRect(right - (_buttons.Length - i) * (ButtonSize + 4) + 4, top, ButtonSize, ButtonSize));
        var popupX = right - _buttons.Length * (ButtonSize + 4) - 4 - PopupWidth;
        Send(_popup, Sel("setFrame:"), new NSRect(popupX, top, PopupWidth, HeaderHeight));
        Send(_status, Sel("setFrame:"), new NSRect(Padding + 2, top + 3, Math.Max(40, popupX - Padding - 10), 18));
        Send(_scroll, Sel("setFrame:"), new NSRect(Padding, Padding, size.Width - Padding * 2, Math.Max(20, top - 8 - Padding)));
    }

    /// <summary>
    /// All lines top to bottom, then the sentence being spoken; with a translation language, each line on the left
    /// and its translation on the right, the row as tall as the longer of the two. While they fit, they sit at the
    /// bottom of the list, like subtitles. Keeps following the newest line if the list was scrolled to the end.
    /// </summary>
    /// <param name="fadeIn">A line that just came (or, with <paramref name="translationOnly"/>, just got its translation).</param>
    private void Render(ListenLine? fadeIn, bool translationOnly = false)
    {
        var clip = Send(_scroll, Sel("contentView"));
        var visible = SendRect(clip, Sel("bounds"));
        var oldHeight = SendRect(_list, Sel("frame")).Height;
        var followEnd = visible.Y + visible.Height >= oldHeight - 4;

        var width = visible.Width;
        var twoColumns = _language.Length > 0 || _lines.Any(l => l.Translation != null);
        var column = twoColumns ? (width - ColumnGap) / 2 : width;

        // Measure first, then place: the rows go to the bottom while the content is shorter than the view
        var placed = new List<(Row Row, double Height, bool Translation)>();
        if (_lines.Count == 0 && _provisional == null)
        {
            var row = RowAt(0);
            SetText(row.Original, _hint() ?? "Listening to what the Mac plays…", MutedColor);
            placed.Add((row, Measure(row.Original, width), false));
        }
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var row = RowAt(placed.Count);
            var color = SpeakerColor(line);
            // The speaker's name where the speaker changes; the color tells the lines after it apart
            var named = line.SpeakerName != null && (i == 0 || _lines[i - 1].SpeakerName != line.SpeakerName);
            SetText(row.Original, named ? $"{line.SpeakerName}: {line.Text}" : line.Text, color);
            var height = Measure(row.Original, column);
            if (twoColumns)
            {
                var pending = line.Translation == null && !line.Done;
                SetText(row.Translation, line.Translation ?? (pending ? "…" : ""),
                    pending ? MutedColor : line.Speaker == null ? TranslationColor : Dim(color));
                height = Math.Max(height, Measure(row.Translation, column));
            }
            placed.Add((row, height, twoColumns));
            if (line == fadeIn)
                Fade(translationOnly ? row.Translation : row.Original);
            if (line == fadeIn && !translationOnly && twoColumns)
                Fade(row.Translation);
        }
        if (_provisional != null)
        {
            var row = RowAt(placed.Count);
            SetText(row.Original, _provisional, MutedColor);
            var height = Measure(row.Original, column);
            if (twoColumns)
            {
                SetText(row.Translation, _provisionalTranslation ?? "", MutedColor);
                height = Math.Max(height, Measure(row.Translation, column));
            }
            placed.Add((row, height, twoColumns));
        }

        var content = placed.Sum(p => p.Height) + RowGap * Math.Max(0, placed.Count - 1);
        var height2 = Math.Max(content, visible.Height);
        Send(_list, Sel("setFrameSize:"), new NSSize(width, height2));
        var y = height2 - content; // flipped: 0 is the top
        foreach (var (row, height, translation) in placed)
        {
            var full = _lines.Count == 0 && _provisional == null;
            Send(row.Original, Sel("setFrame:"), new NSRect(0, y, full ? width : column, height));
            Send(row.Translation, Sel("setFrame:"), new NSRect(column + ColumnGap, y, column, height));
            Send(row.Original, Sel("setHidden:"), false);
            Send(row.Translation, Sel("setHidden:"), !translation);
            y += height + RowGap;
        }
        for (var i = placed.Count; i < _rows.Count; i++)
        {
            Send(_rows[i].Original, Sel("setHidden:"), true);
            Send(_rows[i].Translation, Sel("setHidden:"), true);
        }

        if (followEnd)
            Send(_list, Sel("scrollPoint:"), new NSPoint(0, Math.Max(0, height2 - visible.Height)));
    }

    /// <summary>The <paramref name="index"/>-th row's labels, created the first time they are needed.</summary>
    private Row RowAt(int index)
    {
        while (_rows.Count <= index)
        {
            var row = new Row(Label(), Label());
            Send(_list, Sel("addSubview:"), row.Original);
            Send(_list, Sel("addSubview:"), row.Translation);
            _rows.Add(row);
        }
        return _rows[index];
    }

    /// <summary>The next change of <paramref name="view"/>'s content cross-fades instead of switching at once.</summary>
    private static void Fade(IntPtr view)
    {
        var layer = Send(view, Sel("layer"));
        if (layer == IntPtr.Zero)
            return;
        var transition = Send(Class("CATransition"), Sel("animation"));
        Send(transition, Sel("setType:"), Str("fade"));
        Send(transition, Sel("setDuration:"), FadeSeconds);
        Send(transition, Sel("setTimingFunction:"),
            Send(Class("CAMediaTimingFunction"), Sel("functionWithName:"), Str("easeInEaseOut")));
        Send(layer, Sel("addAnimation:forKey:"), transition, Str("fade"));
    }

    private static IntPtr Label()
    {
        var label = Retain(Send(Class("NSTextField"), Sel("wrappingLabelWithString:"), Str("")));
        Send(label, Sel("setSelectable:"), false);
        Send(label, Sel("setFont:"), Send(Class("NSFont"), Sel("systemFontOfSize:"), 13.0));
        Send(label, Sel("setWantsLayer:"), true);
        return label;
    }

    private static void SetText(IntPtr label, string text, (double R, double G, double B) color)
    {
        Send(label, Sel("setStringValue:"), Str(text));
        Send(label, Sel("setTextColor:"), Color(color));
    }

    private static double Measure(IntPtr label, double width) =>
        SendSize(label, Sel("sizeThatFits:"), new NSSize(width, 10_000)).Height;

    private static IntPtr Color((double R, double G, double B) c) =>
        Send(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), c.R, c.G, c.B, 1.0);

    public void Dispose()
    {
        _timer.Dispose();
        Send(_panel, Sel("orderOut:"), IntPtr.Zero);
        foreach (var button in _buttons)
            Release(button);
        foreach (var row in _rows)
        {
            Release(row.Original);
            Release(row.Translation);
        }
        Release(_list);
        Release(_scroll);
        Release(_popup);
        Release(_status);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
