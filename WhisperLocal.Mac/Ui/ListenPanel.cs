using System.Diagnostics;
using WhisperLocal.Llm;
using WhisperLocal.Mac.Interop;
using WhisperLocal.Pipeline;
using static WhisperLocal.Mac.Interop.ObjC;

namespace WhisperLocal.Mac.Ui;

/// <summary>
/// The listening indicator, just above the recording indicator: elapsed time, a menu to choose the translation
/// language, buttons to open the notes and to stop, and the last lines heard, with the sentence still being spoken greyed
/// at the bottom. While translating, the original and its translation stand side by side, row by row; new lines and
/// translations fade in. Stays until stopped; can be dragged
/// anywhere. Never takes focus, so the app you are in stays active.
/// </summary>
internal sealed class ListenPanel : IDisposable
{
    private const double Width = 720;
    private const double Padding = 12;
    private const double HeaderHeight = 24;
    private const double TextHeight = 116;
    private const double Height = Padding + TextHeight + 8 + HeaderHeight + Padding;
    private const double ColumnGap = 20;
    private const double RowGap = 6;
    private const double PopupWidth = 150;
    private const double ButtonSize = 24;
    private const double IndicatorSpace = 40 + 36 + 8; // the recording indicator sits 40 pt above the bottom, 36 pt tall
    private const int MaxRows = 6;
    private const double FadeSeconds = 0.35;

    private static readonly (double R, double G, double B) Red = (1.0, 0.35, 0.35);
    private static readonly (double R, double G, double B) Orange = (1.0, 0.7, 0.24);
    private static readonly (double R, double G, double B) TextColor = (0.92, 0.93, 0.95);
    private static readonly (double R, double G, double B) TranslationColor = (0.55, 0.8, 1.0);
    private static readonly (double R, double G, double B) MutedColor = (0.6, 0.6, 0.62);

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
    private readonly IntPtr _rowsView;
    private readonly Row[] _rows;
    private readonly System.Threading.Timer _timer;
    private readonly List<ListenLine> _lines = [];

    private List<string> _languages = [];
    private string _language = "";
    private long _since;
    private bool _finishing;
    private string? _problem;
    private string? _provisional;

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

        // The rows live in their own layer-backed view, so a fade covers exactly the text area
        _rowsView = Send(Send(Class("NSView"), Sel("alloc")), Sel("initWithFrame:"), new NSRect(Padding, Padding, Width - Padding * 2, TextHeight));
        Send(_rowsView, Sel("setWantsLayer:"), true);
        Send(content, Sel("addSubview:"), _rowsView);
        _rows = new Row[MaxRows];
        for (var i = 0; i < MaxRows; i++)
        {
            _rows[i] = new Row(Label(), Label());
            Send(_rowsView, Sel("addSubview:"), _rows[i].Original);
            Send(_rowsView, Sel("addSubview:"), _rows[i].Translation);
        }

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
        Render(fadeIn: null);
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

    /// <summary>A new line, or one that got its translation; either fades in.</summary>
    public void ShowLine(ListenLine line)
    {
        var isNew = !_lines.Contains(line);
        if (isNew)
        {
            _lines.Add(line);
            if (_lines.Count > MaxRows)
                _lines.RemoveAt(0);
            _problem = null;
        }
        Render(fadeIn: isNew ? _rowsView : null, line);
    }

    /// <summary>The sentence being spoken so far, greyed under the lines; null removes it.</summary>
    public void ShowProvisional(string? text)
    {
        if (text == _provisional)
            return;
        _provisional = text;
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
            Render(fadeIn: _rowsView); // one column or two
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

    /// <summary>
    /// The newest lines that fit, newest at the bottom; with a translation language, each line on the left and its
    /// translation on the right, the row as tall as the longer of the two.
    /// </summary>
    /// <param name="fadeIn">The view whose change fades in (the whole text area for a new line), or null.</param>
    /// <param name="updated">A line whose translation just came: its translation fades in.</param>
    private void Render(IntPtr? fadeIn, ListenLine? updated = null)
    {
        if (fadeIn is { } view)
            Fade(view);

        var twoColumns = _language.Length > 0 || _lines.Any(l => l.Translation != null);
        var full = Width - Padding * 2;
        var column = twoColumns ? (full - ColumnGap) / 2 : full;

        var used = 0;
        var y = 0.0;
        if (_provisional != null)
        {
            // The newest text of all: always shown; when long, only its end, so the lines above stay in view
            var row = _rows[0];
            var text = _provisional;
            SetText(row.Original, text, MutedColor);
            var height = Measure(row.Original, column);
            while (height > TextHeight / 2 && text.IndexOf(' ', 1) is var space and > 0)
            {
                text = text[(space + 1)..];
                SetText(row.Original, "…" + text, MutedColor);
                height = Measure(row.Original, column);
            }
            height = Math.Min(height, TextHeight);
            Send(row.Original, Sel("setFrame:"), new NSRect(0, 0, column, height));
            Send(row.Original, Sel("setHidden:"), false);
            Send(row.Translation, Sel("setHidden:"), true);
            y = height + RowGap;
            used = 1;
        }
        else if (_lines.Count == 0)
        {
            var row = _rows[0];
            SetText(row.Original, _hint() ?? "Listening to what the Mac plays…", MutedColor);
            var height = Math.Min(Measure(row.Original, full), TextHeight);
            Send(row.Original, Sel("setFrame:"), new NSRect(0, TextHeight - height, full, height));
            Send(row.Original, Sel("setHidden:"), false);
            Send(row.Translation, Sel("setHidden:"), true);
            used = 1;
        }

        for (var i = _lines.Count - 1; i >= 0 && used < MaxRows; i--)
        {
            var line = _lines[i];
            var row = _rows[used];
            SetText(row.Original, line.Text, TextColor);
            var height = Measure(row.Original, column);
            if (twoColumns)
            {
                var pending = line.Translation == null && !line.Done;
                SetText(row.Translation, line.Translation ?? (pending ? "…" : ""), pending ? MutedColor : TranslationColor);
                height = Math.Max(height, Measure(row.Translation, column));
            }
            // The newest line always shows, cut at the top if it is very long
            if (y + height > TextHeight && used > 0)
                break;
            height = Math.Min(height, TextHeight - y);

            Send(row.Original, Sel("setFrame:"), new NSRect(0, y, column, height));
            Send(row.Translation, Sel("setFrame:"), new NSRect(column + ColumnGap, y, column, height));
            Send(row.Original, Sel("setHidden:"), false);
            Send(row.Translation, Sel("setHidden:"), !twoColumns);
            if (line == updated && fadeIn == null)
                Fade(row.Translation);
            y += height + RowGap;
            used++;
        }

        for (var i = used; i < MaxRows; i++)
        {
            Send(_rows[i].Original, Sel("setHidden:"), true);
            Send(_rows[i].Translation, Sel("setHidden:"), true);
        }
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
        Release(_rowsView);
        Release(_popup);
        Release(_status);
        Release(_panel);
        MacNative.CGColorRelease(_background);
    }
}
