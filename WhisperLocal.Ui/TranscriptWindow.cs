using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WhisperLocal.Llm;
using WhisperLocal.Text;

namespace WhisperLocal.Ui;

/// <summary>
/// Puts the transcript window's text back where the dictation was pasted: <paramref name="Paste"/> gets the text and
/// whether to replace the dictation (undo it, then paste) or paste at the cursor.
/// </summary>
/// <param name="AppName">The app the dictation was pasted into, for the button's tooltip.</param>
/// <param name="Replace">
/// Nothing was typed since the dictation was pasted, so undo removes exactly it; otherwise the text is pasted at the
/// cursor instead.
/// </param>
public sealed record PasteBack(string AppName, bool Replace, Func<string, bool, Task> Paste);

/// <summary>
/// Shows a dictation's text, editable, with a Copy button, just above the recording indicator. With
/// <see cref="Tools"/> set, a language model can rework it: what is shown is the dictation, then (each when chosen)
/// fixed by context (misheard words, judged by the recent dictations), grammar-checked, and translated into a language
/// in a style. Every step's result is kept, so a combination seen before shows at once without asking the model again.
/// </summary>
public sealed class TranscriptWindow : Window
{
    private static readonly string[] StyleTitles = ["Literal", "Fluent", "Technical"];
    private static readonly string[] StyleHints =
    [
        "Literal: close to the original wording",
        "Fluent: natural, as a native speaker would write it",
        "Technical: precise register; terms, names and code kept",
    ];

    private readonly TextBox _text;
    private readonly TextBlock _words = DarkTheme.MutedLabel("");
    private readonly TextBlock _status = DarkTheme.MutedLabel("");
    private readonly TextBlock _note = DarkTheme.Label("", 12, brush: DarkTheme.Warning);
    private readonly ComboBox _language = DarkTheme.Dropdown(170);
    private readonly ComboBox _style = DarkTheme.Dropdown(110);
    private readonly CheckBox _grammar = DarkTheme.CheckBox("Grammar");
    private readonly CheckBox _context = DarkTheme.CheckBox("Context fix");
    private readonly Button _reset;
    private readonly Button _paste;
    private readonly Button _copy;
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };

    private string _original = ""; // as dictated
    private string _source = ""; // the original with the user's edits; every step starts from it

    // Each step's result by its input: ("context" | "grammar" | "translate|uk|Fluent", input) → output and a short note
    private readonly Dictionary<(string Step, string Input), (string Text, string Note)> _results = new();

    // The user's edits of a reworked text, by the combination it was shown for
    private readonly Dictionary<string, string> _edits = new();
    private List<string> _languages = [];
    private CancellationTokenSource? _work;
    private PasteBack? _pasteBack;
    private bool _pasting; // our own Hide for Replace/Paste, not the user leaving
    private bool _setting; // the text or the controls are being set by code, not the user
    private bool _allowClose;

    public TranscriptWindow()
    {
        Title = "Transcript";
        Icon = App.WindowIcon;
        Width = 640;
        Height = 300;
        MinWidth = 560;
        MinHeight = 180;
        Topmost = true;
        ShowInTaskbar = false;
        Background = DarkTheme.Background;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Padding = new Thickness(12, 10),
            Background = DarkTheme.Card,
            BorderBrush = DarkTheme.CardBorder,
            CornerRadius = new CornerRadius(10),
        };
        _text.TextChanged += (_, _) => OnTextEdited();

        // Toolbar: language and style of the translation, the two fixes, Reset
        ToolTip.SetTip(_language, "Show the original or translate it");
        _style.ItemsSource = StyleTitles;
        _style.SelectedIndex = (int)TranslationStyle.Fluent;
        ToolTip.SetTip(_style, string.Join("\n", StyleHints));
        _grammar.FontSize = _context.FontSize = 12;
        _grammar.VerticalAlignment = _context.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_grammar, "Correct grammar, punctuation and word choice (before translating)");
        ToolTip.SetTip(_context, "Replace misheard words with what you meant, judging by the text and your recent dictations");
        _reset = DarkTheme.Button("Reset", Reset, small: true);
        ToolTip.SetTip(_reset, "Back to the dictation as it was said: the original, no fixes, your edits dropped");
        _language.SelectionChanged += (_, _) => OnOptionChanged();
        _style.SelectionChanged += (_, _) => OnOptionChanged();
        _grammar.IsCheckedChanged += (_, _) => OnOptionChanged();
        _context.IsCheckedChanged += (_, _) => OnOptionChanged();
        _toolbar.Children.Add(_language);
        _toolbar.Children.Add(_style);
        _toolbar.Children.Add(_context);
        _toolbar.Children.Add(_grammar);
        _toolbar.IsVisible = false;

        _copy = DarkTheme.Button("Copy", () => _ = CopyAsync(), accent: true);
        _paste = DarkTheme.Button("Replace", () => _ = PasteBackAsync());
        _paste.IsVisible = false;
        var close = DarkTheme.Button("Close", Hide);

        // The word count, then the status (cut short when the window is narrow; the full text is its tooltip)
        var info = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(_words);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_status, 1);
        info.Children.Add(_status);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(info);
        _reset.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(_reset, 1);
        footer.Children.Add(_reset);
        close.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(close, 2);
        footer.Children.Add(close);
        _paste.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_paste, 3);
        footer.Children.Add(_paste);
        Grid.SetColumn(_copy, 4);
        footer.Children.Add(_copy);
        DockPanel.SetDock(footer, Dock.Bottom);
        _note.Margin = new Thickness(2, 0, 0, 8);
        DockPanel.SetDock(_note, Dock.Top);
        DockPanel.SetDock(_toolbar, Dock.Top);
        Content = new DockPanel { Margin = new Thickness(12), Children = { footer, _note, _toolbar, _text } };

        // Leaving for another app may mean typing there: undo would then hit that, so Replace becomes Paste
        Deactivated += (_, _) =>
        {
            if (!_pasting && _pasteBack is { Replace: true } p)
                SetPasteBack(p with { Replace = false });
        };

        // Reused: closing only hides it
        Closing += (_, e) =>
        {
            Cancel();
            if (_allowClose)
                return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Translation, grammar check and fix by context; null leaves the toolbar out (the text only).</summary>
    public TranscriptTools? Tools { get; set; }

    /// <param name="note">A line above the text, e.g. why it was not pasted; null for none.</param>
    /// <param name="pasteBack">The last dictation's paste, to replace with the text shown; null leaves Replace out.</param>
    public void ShowText(string text, string? note = null, PasteBack? pasteBack = null)
    {
        Cancel();
        _original = _source = text;
        _results.Clear();
        _edits.Clear();
        _note.Text = note ?? "";
        _note.IsVisible = note != null;
        _copy.Content = "Copy";
        SetPasteBack(pasteBack);
        RefreshToolbar();
        ResetOptions();
        _ = UpdateAsync();
        PlaceAboveIndicator();
        Show();
        Activate();
    }

    /// <summary>The languages from the config; the options off without tools or without a model to use.</summary>
    private void RefreshToolbar()
    {
        _toolbar.IsVisible = _reset.IsVisible = Tools != null;
        SetStatus("");
        if (Tools == null)
            return;

        _languages = Tools.Languages.ToList();
        _setting = true;
        _language.ItemsSource = new[] { "Original" }.Concat(_languages.Select(l => $"Translate: {Translator.NativeName(l)}")).ToList();
        _language.SelectedIndex = 0;
        _setting = false;

        var unavailable = Tools.Unavailable();
        _language.IsEnabled = _grammar.IsEnabled = _context.IsEnabled = unavailable == null;
        if (unavailable != null)
            SetStatus(unavailable);
    }

    // ---- Options ----

    private string SelectedLanguage => _language.SelectedIndex is var i && i > 0 && i - 1 < _languages.Count ? _languages[i - 1] : "";

    private TranslationStyle SelectedStyle => (TranslationStyle)Math.Clamp(_style.SelectedIndex, 0, StyleTitles.Length - 1);

    private bool GrammarOn => _grammar.IsChecked == true;

    private bool ContextOn => _context.IsChecked == true;

    /// <summary>Nothing reworks the text: it is the dictation (with the user's edits).</summary>
    private bool ShowsSource => !GrammarOn && !ContextOn && SelectedLanguage.Length == 0;

    /// <summary>The combination shown, for the user's edits of it.</summary>
    private string Combination => $"{ContextOn}|{GrammarOn}|{SelectedLanguage}|{SelectedStyle}";

    private void OnOptionChanged()
    {
        if (!_setting)
            _ = UpdateAsync();
    }

    /// <summary>Unchecked fixes, Original, Fluent.</summary>
    private void ResetOptions()
    {
        _setting = true;
        _grammar.IsChecked = false;
        _context.IsChecked = false;
        if (_language.ItemCount > 0)
            _language.SelectedIndex = 0;
        _style.SelectedIndex = (int)TranslationStyle.Fluent;
        _setting = false;
    }

    /// <summary>Reset: the dictation as it was said; the results stay known, so choosing them again costs nothing.</summary>
    private void Reset()
    {
        _source = _original;
        _edits.Clear();
        ResetOptions();
        _ = UpdateAsync();
    }

    // ---- The text shown: source → fix by context → grammar → translation ----

    private async Task UpdateAsync()
    {
        Cancel();
        _style.IsEnabled = SelectedLanguage.Length > 0 && _language.IsEnabled;
        if (ShowsSource || Tools is not { } tools)
        {
            SetText(_source);
            SetStatus(Tools?.Unavailable() ?? "");
            return;
        }
        if (_edits.TryGetValue(Combination, out var edited))
        {
            SetText(edited);
            SetStatus("Your edit");
            return;
        }

        var steps = new List<(string Key, string Busy, Func<string, CancellationToken, Task<(string, string)>> Run)>();
        if (ContextOn)
            steps.Add(("context", "Fixing by context…", async (text, ct) =>
            {
                var fixedText = await tools.FixByContextAsync(text, ct);
                return fixedText == null ? (text, "Context: nothing to fix") : (fixedText, $"Context: {Changed(text, fixedText)} fixed");
            }));
        if (GrammarOn)
            steps.Add(("grammar", "Checking grammar…", async (text, ct) =>
            {
                var corrected = await tools.CheckGrammarAsync(text, ct);
                return corrected == null ? (text, "Grammar: no corrections") : (corrected, $"Grammar: {Changed(text, corrected)} corrected");
            }));
        var (language, style) = (SelectedLanguage, SelectedStyle);
        if (language.Length > 0)
        {
            var name = Translator.NativeName(language);
            steps.Add(($"translate|{language}|{style}", $"Translating into {name}…", async (text, ct) =>
            {
                var translation = await tools.TranslateAsync(text, language, style, ct)
                                  ?? throw new InvalidOperationException("the model's answer was unusable; change an option to retry");
                return (translation, $"{name}, {StyleTitles[(int)style]}");
            }));
        }

        var cts = _work = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var text = _source;
            var notes = new List<string>();
            var asked = false;
            foreach (var (key, busy, run) in steps)
            {
                if (!_results.TryGetValue((key, text), out var result))
                {
                    SetStatus(busy);
                    result = await run(text, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    _results[(key, text)] = result;
                    asked = true;
                }
                notes.Add(result.Note);
                text = result.Text;
            }
            SetText(text);
            SetStatus(string.Join(" · ", notes) + (asked ? "" : " (kept)"), DarkTheme.Ok);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // An option changed meanwhile: the steps done so far are kept
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException ? "timed out" : ex.Message;
            Log.Warn($"Transcript window: {reason}");
            if (_work == cts)
                SetStatus($"Failed: {reason}", DarkTheme.Warning);
        }
        finally
        {
            if (_work == cts)
            {
                _work = null;
                SetBusy(false);
            }
            cts.Dispose();
        }
    }

    private static string Changed(string before, string after)
    {
        var count = TextDiff.Changes(before, after).Count(p => p.Changed && p.Text.Trim().Length > 0);
        return count == 1 ? "1 word" : $"{count} words";
    }

    private void Cancel()
    {
        if (_work == null)
            return;
        _work.Cancel();
        _work = null;
        SetBusy(false);
    }

    /// <summary>While the model works the text is read-only and dimmed; the options stay usable (a change starts over).</summary>
    private void SetBusy(bool busy)
    {
        _text.IsReadOnly = busy;
        _text.Opacity = busy ? 0.6 : 1;
        _paste.IsEnabled = !busy; // not the half-done text
    }

    private void SetText(string text)
    {
        _setting = true;
        _text.Text = text;
        _setting = false;
        UpdateWordCount();
    }

    private void SetStatus(string text, IBrush? brush = null)
    {
        _status.Text = text;
        _status.Foreground = brush ?? DarkTheme.Muted;
        ToolTip.SetTip(_status, text.Length > 0 ? text : null);
    }

    /// <summary>The user's own edit: of the dictation (everything is redone from it) or of the reworked text shown.</summary>
    private void OnTextEdited()
    {
        UpdateWordCount();
        if (_setting || _text.IsReadOnly)
            return;
        var text = _text.Text ?? "";
        if (ShowsSource)
        {
            _source = text;
            _edits.Clear();
        }
        else
            _edits[Combination] = text;
    }

    private void UpdateWordCount()
    {
        var count = (_text.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        _words.Text = count == 1 ? "1 word" : $"{count} words";
    }

    /// <summary>Bottom centre of the primary screen, above the indicator (which sits 40 pt above the bottom).</summary>
    private void PlaceAboveIndicator()
    {
        if (Screens.Primary is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Bottom - height - (int)(100 * scale));
    }

    private void SetPasteBack(PasteBack? pasteBack)
    {
        _pasteBack = pasteBack;
        _paste.IsVisible = pasteBack != null;
        if (pasteBack == null)
            return;
        _paste.Content = pasteBack.Replace ? "Replace" : "Paste";
        ToolTip.SetTip(_paste, pasteBack.Replace
            ? $"Replace the dictation pasted in {pasteBack.AppName} with the text shown here"
            : $"Paste the text shown here at the cursor in {pasteBack.AppName} (typed there since the dictation, so it is not replaced)");
    }

    /// <summary>Replace or Paste: the window goes away and the app the dictation went into gets the text.</summary>
    private async Task PasteBackAsync()
    {
        if (_pasteBack is not { } p)
            return;
        var text = _text.Text ?? "";
        if (text.Trim().Length == 0)
            return;
        Cancel();
        _pasting = true;
        try
        {
            Hide();
            await p.Paste(text, p.Replace);
        }
        finally
        {
            _pasting = false;
        }
    }

    private async Task CopyAsync()
    {
        if (Clipboard == null)
            return;
        await Clipboard.SetTextAsync(_text.Text ?? "");
        _copy.Content = "Copied ✓";
        DispatcherTimer.RunOnce(() => _copy.Content = "Copy", TimeSpan.FromSeconds(1.5));
    }

    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }
}
