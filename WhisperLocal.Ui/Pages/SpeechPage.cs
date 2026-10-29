using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;

namespace WhisperLocal.Ui.Pages;

/// <summary>
/// Speech to text settings: the hotkey, recognition (local Whisper or Gemini, language, models) and post-processing (the
/// grammar check and its language model, the corrections dictionary). Models are downloaded on the Models page.
/// </summary>
internal sealed class SpeechPage : Page
{
    private readonly IAppHost _host;
    private readonly ComboBox _hotkeys = DarkTheme.Dropdown(228);
    private readonly Button _record;
    private readonly Note _hotkeyNote = new();
    private readonly ComboBox _languages = DarkTheme.Dropdown(228);
    private readonly ComboBox _engines = DarkTheme.Dropdown(250);
    private readonly ComboBox _geminiModels = DarkTheme.Dropdown(250);
    private readonly Control _geminiModelRow;
    private readonly CheckBox _fallback = new()
    {
        Content = "If Gemini fails (no internet, limit used up), recognise on this computer",
        FontSize = 13,
        Foreground = DarkTheme.Text,
    };
    private readonly Grid _keyMissing = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly TextBlock _cloudNote = DarkTheme.MutedLabel("Recordings are sent to Google for recognition.", 11);
    private readonly ComboBox _speechModels = DarkTheme.Dropdown(250);
    private readonly Grid _speechMissing = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Note _recognitionNote = new();
    private readonly CheckBox _polish = new()
    {
        Content = "Grammar check: after each dictation, propose a polished version",
        FontSize = 13,
        Foreground = DarkTheme.Text,
    };
    private readonly ComboBox _llmModels = DarkTheme.Dropdown(290);
    private readonly Note _polishNote = new();
    private readonly StackPanel _llmStatus = new() { Spacing = 2 };
    private readonly TextBlock _corrections = DarkTheme.Label("", 12);

    private List<HotkeyPreset> _hotkeyChoices = [];
    private List<(string Title, string Value)> _languageChoices = [];
    private List<string> _speechModelPaths = [];
    private List<string> _geminiModelIds = [];
    private List<string> _llmModelPaths = [];
    private string? _shownHotkey;
    private string? _shownLanguage;
    private string? _shownSpeechModel;
    private string? _shownEngine;
    private string? _shownGeminiModel;
    private bool? _shownFallback;
    private string? _shownLlmModel;
    private string? _shownLlmStatus;
    private bool? _shownPolish;
    private bool _updating;
    private bool _recording;

    public SpeechPage(IAppHost host, Action showModels) : base("Speech to Text")
    {
        _host = host;

        _record = DarkTheme.Button("Record…", ToggleHotkeyRecording, small: true);
        ToolTip.SetTip(_record, "Hold any key combination to use it as the hotkey");
        Add(DarkTheme.Section("Dictation", new StackPanel
        {
            Spacing = 4,
            Children = { Widgets.Fields(("Hotkey", _hotkeys), ("", _record)), _hotkeyNote.Line },
        }));

        WarningRow(_speechMissing, "The local model is not downloaded yet: download it on the Models page.", showModels);
        WarningRow(_keyMissing, "Gemini needs an API key: add it on the Models page. Until then recognition runs locally.", showModels);
        _engines.ItemsSource = Engines.Select(e => e.Title).ToList();
        _geminiModelRow = Widgets.Fields(("Gemini model", _geminiModels));
        Add(DarkTheme.Section("Recognition", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Widgets.Fields(("Engine", _engines), ("Language", _languages)),
                _keyMissing,
                _geminiModelRow,
                _fallback,
                Widgets.Fields(("Local model", _speechModels)),
                _speechMissing,
                _cloudNote,
                _recognitionNote.Line,
            },
        }));

        var postProcessing = new StackPanel { Spacing = 8 };
        if (host.HasGrammarCheck)
            postProcessing.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children = { _polish, Widgets.Fields(("Model", _llmModels)), _polishNote.Line, _llmStatus },
            });
        var corrections = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        corrections.Children.Add(_corrections);
        var edit = DarkTheme.Button("Edit in Config", () => _host.OpenPath(_host.Config.FilePath, textEditor: true), small: true);
        ToolTip.SetTip(edit, "The Dictionary list: a pattern of what Whisper hears and the word to write instead");
        Grid.SetColumn(edit, 1);
        corrections.Children.Add(edit);
        postProcessing.Children.Add(corrections);
        Add(DarkTheme.Section("Post-processing", postProcessing));

        _hotkeys.SelectionChanged += (_, _) => OnHotkeySelected();
        _languages.SelectionChanged += (_, _) => OnLanguageSelected();
        _speechModels.SelectionChanged += (_, _) => OnSpeechModelSelected();
        _engines.SelectionChanged += (_, _) =>
        {
            var i = _engines.SelectedIndex;
            if (!_updating && i >= 0)
                Widgets.Save(_host, "Recognition", "Engine", Engines[i].Value, _recognitionNote);
        };
        _geminiModels.SelectionChanged += (_, _) =>
        {
            var i = _geminiModels.SelectedIndex;
            if (!_updating && i >= 0 && i < _geminiModelIds.Count)
                Widgets.Save(_host, "Cloud", "GeminiModel", _geminiModelIds[i], _recognitionNote);
        };
        _fallback.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
                Widgets.Save(_host, "Recognition", "FallbackToLocal", _fallback.IsChecked == true, _recognitionNote);
        };
        _polish.IsCheckedChanged += (_, _) => OnPolishToggled();
        _llmModels.SelectionChanged += (_, _) => OnLlmModelSelected();
    }

    public override void Refresh()
    {
        var cfg = _host.Config.Current;
        _updating = true;
        RefreshHotkey(cfg.Hotkey.Key);
        RefreshLanguage(cfg.Whisper.Language);
        RefreshEngine(cfg);
        RefreshSpeechModel(cfg);
        if (_host.HasGrammarCheck)
            RefreshPolish(cfg);
        _updating = false;

        var rules = cfg.Dictionary.Count;
        _corrections.Text = rules == 1
            ? "1 correction rule fixes a word Whisper mishears"
            : $"{rules} correction rules fix words Whisper mishears";
    }

    public override void OnHidden()
    {
        if (!_recording)
            return;
        _host.CancelHotkeyRecording();
        StopHotkeyRecording();
    }

    // ---- Hotkey ----

    private void RefreshHotkey(string current)
    {
        if (current == _shownHotkey || _recording)
            return;
        _shownHotkey = current;

        _hotkeyChoices = _host.HotkeyPresets.ToList();
        var index = _hotkeyChoices.FindIndex(p => SameHotkey(p.Key, current));
        if (index < 0)
        {
            _hotkeyChoices.Add(new HotkeyPreset($"Custom: {_host.DescribeHotkey(current)}", current));
            index = _hotkeyChoices.Count - 1;
        }
        _hotkeys.ItemsSource = _hotkeyChoices.Select(p => p.Title).ToList();
        _hotkeys.SelectedIndex = index;
        ToolTip.SetTip(_hotkeys, "Hold to record, release to transcribe. Record… takes any combination you hold.");
    }

    private bool SameHotkey(string a, string b) =>
        string.Equals(_host.DescribeHotkey(a), _host.DescribeHotkey(b), StringComparison.OrdinalIgnoreCase);

    private void OnHotkeySelected()
    {
        if (_updating || _hotkeys.SelectedIndex < 0 || _hotkeys.SelectedIndex >= _hotkeyChoices.Count)
            return;
        SaveHotkey(_hotkeyChoices[_hotkeys.SelectedIndex].Key);
    }

    private void ToggleHotkeyRecording()
    {
        if (_recording)
        {
            _host.CancelHotkeyRecording();
            StopHotkeyRecording();
            return;
        }

        var error = _host.StartHotkeyRecording(
            preview => Dispatcher.UIThread.Post(() => _hotkeyNote.Set($"{preview} … release to save", DarkTheme.Accent, sticky: true)),
            key => Dispatcher.UIThread.Post(() =>
            {
                StopHotkeyRecording();
                if (key != null)
                    SaveHotkey(key);
            }));
        if (error != null)
        {
            _hotkeyNote.Set(error, DarkTheme.Warning);
            return;
        }
        _recording = true;
        _record.Content = "Cancel";
        _hotkeyNote.Set("Listening… hold the keys you want, then release. Esc cancels.", DarkTheme.Accent, sticky: true);
    }

    private void StopHotkeyRecording()
    {
        _recording = false;
        _record.Content = "Record…";
        _shownHotkey = null; // re-render
        Refresh();
    }

    private void SaveHotkey(string key)
    {
        // Clears the "release to save" prompt; Home and the sidebar say which keys to hold
        _hotkeyNote.Set("");
        Widgets.Save(_host, "Hotkey", "Key", key, _hotkeyNote);
    }

    // ---- Language ----

    private static readonly (string Title, string Value, string Hint)[] LanguagePresets =
    [
        ("Ukrainian + English", "uk,en", "Detects which of the two you speak, every dictation."),
        ("Ukrainian + English + Russian", "uk,en,ru",
            "Detects which of the three you speak. Short Ukrainian phrases may occasionally come out in Russian."),
        ("Ukrainian only", "uk", "Always Ukrainian: English speech comes out translated into Ukrainian."),
        ("English only", "en", "Always English: other speech comes out translated into English."),
        ("Any language", "auto", "Detects any language; short phrases are occasionally misdetected."),
    ];

    private void RefreshLanguage(string current)
    {
        if (current == _shownLanguage)
            return;
        _shownLanguage = current;

        _languageChoices = LanguagePresets.Select(p => (p.Title, p.Value)).ToList();
        var index = _languageChoices.FindIndex(c => SameLanguages(c.Value, current));
        if (index < 0)
        {
            _languageChoices.Add(($"Custom: {current}", current));
            index = _languageChoices.Count - 1;
        }
        _languages.ItemsSource = _languageChoices.Select(c => c.Title).ToList();
        _languages.SelectedIndex = index;
        ToolTip.SetTip(_languages, index < LanguagePresets.Length
            ? LanguagePresets[index].Hint
            : "Set in the config (Whisper.Language): a code like \"de\", or a list like \"uk,en,pl\".");
    }

    private static bool SameLanguages(string a, string b) =>
        string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private void OnLanguageSelected()
    {
        var i = _languages.SelectedIndex;
        if (_updating || i < 0 || i >= _languageChoices.Count)
            return;
        Widgets.Save(_host, "Whisper", "Language", _languageChoices[i].Value, _recognitionNote);
    }

    // ---- Engine ----

    private static readonly (string Title, string Value)[] Engines =
        [("On this computer (Whisper)", "Local"), ("Google Gemini (cloud)", "Gemini")];

    private static readonly (string Title, string Id, string Hint)[] GeminiModels =
    [
        ("Gemini Flash (latest)", "gemini-flash-latest", "Google's newest Flash model: accurate, a second or two per dictation."),
        ("Gemini Flash-Lite (latest)", "gemini-flash-lite-latest", "Smaller and faster, a bit less accurate; higher free limits."),
    ];

    private void RefreshEngine(AppConfig cfg)
    {
        var gemini = string.Equals(cfg.Recognition.Engine, "Gemini", StringComparison.OrdinalIgnoreCase);
        if (cfg.Recognition.Engine != _shownEngine)
        {
            _shownEngine = cfg.Recognition.Engine;
            _engines.SelectedIndex = gemini ? 1 : 0;
        }
        if (cfg.Cloud.GeminiModel != _shownGeminiModel)
        {
            _shownGeminiModel = cfg.Cloud.GeminiModel;
            var titles = GeminiModels.Select(m => m.Title).ToList();
            _geminiModelIds = GeminiModels.Select(m => m.Id).ToList();
            var index = _geminiModelIds.IndexOf(cfg.Cloud.GeminiModel);
            if (index < 0)
            {
                titles.Add(cfg.Cloud.GeminiModel);
                _geminiModelIds.Add(cfg.Cloud.GeminiModel);
                index = titles.Count - 1;
            }
            _geminiModels.ItemsSource = titles;
            _geminiModels.SelectedIndex = index;
            ToolTip.SetTip(_geminiModels, index < GeminiModels.Length
                ? GeminiModels[index].Hint
                : "Set in the config (Cloud.GeminiModel): any Gemini model that accepts audio.");
        }
        if (cfg.Recognition.FallbackToLocal != _shownFallback)
        {
            _shownFallback = cfg.Recognition.FallbackToLocal;
            _fallback.IsChecked = cfg.Recognition.FallbackToLocal;
        }

        var hasKey = cfg.Cloud.GeminiApiKey.Trim().Length > 0;
        _keyMissing.IsVisible = gemini && !hasKey;
        _geminiModelRow.IsVisible = _fallback.IsVisible = _cloudNote.IsVisible = gemini && hasKey;
    }

    /// <summary>A warning line with a button that opens the Models page.</summary>
    private static void WarningRow(Grid row, string text, Action showModels)
    {
        row.Children.Add(DarkTheme.Label(text, 12, brush: DarkTheme.Warning));
        var models = DarkTheme.Button("Models", showModels, small: true);
        models.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(models, 1);
        row.Children.Add(models);
    }

    // ---- Speech model ----

    private void RefreshSpeechModel(AppConfig cfg)
    {
        var path = cfg.Whisper.ModelPath;
        if (path != _shownSpeechModel)
        {
            _shownSpeechModel = path;
            var titles = WhisperModels.Catalog.Select(m => $"{m.Title} · {m.Size}").ToList();
            _speechModelPaths = WhisperModels.Catalog.Select(m => "models/" + m.File).ToList();
            var known = WhisperModels.Find(path);
            var index = known != null ? WhisperModels.Catalog.ToList().IndexOf(known) : -1;
            if (index < 0)
            {
                titles.Add($"Custom: {Path.GetFileName(path)}");
                _speechModelPaths.Add(path);
                index = titles.Count - 1;
            }
            _speechModels.ItemsSource = titles;
            _speechModels.SelectedIndex = index;
            ToolTip.SetTip(_speechModels, known?.Hint ?? "Set in the config (Whisper.ModelPath).");
        }
        // Checked every time: the line goes away when the download finishes
        // With Gemini and no fallback the local model is not needed
        var needed = !RecognitionRouter.UsesGemini(cfg) || cfg.Recognition.FallbackToLocal;
        _speechMissing.IsVisible = needed && !File.Exists(AppPaths.Resolve(path)) && _host.DownloadProgress(path) == null;
    }

    private void OnSpeechModelSelected()
    {
        var i = _speechModels.SelectedIndex;
        if (_updating || i < 0 || i >= _speechModelPaths.Count)
            return;
        // whisper-server restarts with it on the next dictation
        Widgets.Save(_host, "Whisper", "ModelPath", _speechModelPaths[i], _recognitionNote);
    }

    // ---- Post-processing: grammar check ----

    private void RefreshPolish(AppConfig cfg)
    {
        var model = cfg.LocalLlm.ModelPath;
        // Only on a change in the config: a click shows at once, the config catches up after the save
        if (cfg.Polish.Enabled != _shownPolish)
        {
            _shownPolish = cfg.Polish.Enabled;
            _polish.IsChecked = cfg.Polish.Enabled;
        }
        if (model != _shownLlmModel)
        {
            _shownLlmModel = model;
            var titles = LlmModels.Catalog.Select(m => $"{m.Title} · {m.Size}").ToList();
            _llmModelPaths = LlmModels.Catalog.Select(m => "models/" + m.File).ToList();
            var index = LlmModels.Find(model) is { } known ? LlmModels.Catalog.ToList().IndexOf(known) : -1;
            if (index < 0)
            {
                titles.Add($"Custom: {Path.GetFileName(model)}");
                _llmModelPaths.Add(model);
                index = titles.Count - 1;
            }
            _llmModels.ItemsSource = titles;
            _llmModels.SelectedIndex = index;
            ToolTip.SetTip(_llmModels, LlmModels.Find(model)?.Hint ?? "Set in the config (LocalLlm.ModelPath): any instruction-tuned GGUF model.");
        }

        var used = cfg.Polish.Enabled;
        _llmModels.IsEnabled = used;
        _llmStatus.IsVisible = used;
        if (used)
            Widgets.RenderItems(_llmStatus, _host.GetLanguageModelItems(), ref _shownLlmStatus, () =>
            {
                _shownLlmStatus = null;
                Refresh();
            });
    }

    private void OnPolishToggled()
    {
        if (_updating)
            return;
        Widgets.Save(_host, "Polish", "Enabled", _polish.IsChecked == true, _polishNote);
    }

    private void OnLlmModelSelected()
    {
        var i = _llmModels.SelectedIndex;
        if (_updating || i < 0 || i >= _llmModelPaths.Count)
            return;
        Widgets.Save(_host, "LocalLlm", "ModelPath", _llmModelPaths[i], _polishNote);
    }
}
