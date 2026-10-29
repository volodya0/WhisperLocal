using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WhisperLocal.Cloud;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;

namespace WhisperLocal.Ui.Pages;

/// <summary>
/// Every model in one place: the speech models and language models the app offers, whether each is downloaded,
/// in use or loaded, with Download, Cancel and Use; when models are unloaded to free memory; cloud API keys.
/// </summary>
internal sealed class ModelsPage : Page
{
    private static readonly (string Title, int Minutes)[] IdleChoices =
        [("5 minutes", 5), ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60), ("Never", 0)];

    private readonly IAppHost _host;
    private readonly StackPanel _speechRows = new();
    private readonly Ellipse _memoryDot = DarkTheme.Dot(DarkTheme.Idle);
    private readonly TextBlock _memory = DarkTheme.Label("", 12);
    private readonly TextBlock _memoryDetail = DarkTheme.MutedLabel("", 11);
    private readonly Button _memoryButton;
    private readonly StackPanel _llmRows = new();
    private readonly ComboBox _idle = DarkTheme.Dropdown(150);
    private readonly Note _note = new();
    private readonly StackPanel _cloud = new() { Spacing = 6 };
    private readonly TextBox _keyBox = new()
    {
        PasswordChar = '•',
        Watermark = "Paste the API key (AIza…)",
        FontSize = 12,
        Width = 300,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly Note _cloudNote = new();
    private CancellationTokenSource? _check;
    private string? _shownKey;
    private List<int> _idleValues = [];
    private string? _shownSpeech;
    private string? _shownLlm;
    private int? _shownIdle;
    private bool _updating;

    public ModelsPage(IAppHost host) : base("Models")
    {
        _host = host;
        _memoryButton = DarkTheme.Button("", () => _ = ToggleMemoryAsync(), small: true);
        _memoryButton.MinWidth = 72;

        Add(DarkTheme.Section("Speech recognition", new StackPanel
        {
            Spacing = 4,
            Children = { _speechRows, DarkTheme.Separator(), MemoryRow() },
        }));
        if (host.HasGrammarCheck)
            Add(DarkTheme.Section("Language models", _llmRows));
        Add(DarkTheme.Section("Memory", new StackPanel
        {
            Spacing = 4,
            Children = { Widgets.Fields(("Unload models after", _idle), ("", DarkTheme.MutedLabel("without dictation"))), _note.Line },
        }));
        Add(DarkTheme.Section("Folder", FolderRow()));
        Add(DarkTheme.Section("Cloud", _cloud));

        _idle.SelectionChanged += (_, _) => OnIdleSelected();
        _keyBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            SaveKey();
        };
    }

    public override void Refresh()
    {
        var cfg = _host.Config.Current;
        RefreshSpeech(cfg);
        RefreshMemory();
        if (_host.HasGrammarCheck)
            RefreshLlm(cfg);
        RefreshIdle(cfg.Memory.IdleUnloadMinutes);
        RefreshCloud(cfg.Cloud.GeminiApiKey.Trim());
    }

    // ---- Model rows ----

    /// <summary>
    /// One catalog model: <paramref name="Path"/> is its config value ("models/…"); <paramref name="Needed"/> when
    /// the selected model is used now, so a missing one is an error (the grammar check may be off).
    /// </summary>
    private sealed record ModelRow(string Title, string Size, string Hint, string Path, bool InUse, string InUseText, bool Needed, string? Url);

    private void RefreshSpeech(AppConfig cfg)
    {
        var current = cfg.Whisper.ModelPath;
        // With Gemini the local model is only the fallback (if that is on)
        var gemini = RecognitionRouter.UsesGemini(cfg);
        var inUse = gemini ? "Fallback for Gemini" : "In use";
        var needed = !gemini || cfg.Recognition.FallbackToLocal;
        var rows = WhisperModels.Catalog
            .Select(m => new ModelRow(m.Title, m.Size, m.Hint, "models/" + m.File, WhisperModels.Find(current) == m, inUse, needed, null))
            .ToList();
        if (WhisperModels.Find(current) == null)
            rows.Add(new ModelRow($"Custom: {System.IO.Path.GetFileName(current)}", "", "Set in the config (Whisper.ModelPath).", current, true, inUse, needed, null));
        RenderRows(_speechRows, rows, ref _shownSpeech, path => Widgets.Save(_host, "Whisper", "ModelPath", path, _note));
    }

    private void RefreshLlm(AppConfig cfg)
    {
        var current = cfg.LocalLlm.ModelPath;
        var used = cfg.Polish.Enabled;
        var inUse = used ? "Used by the grammar check" : "Selected for the grammar check (off)";
        var rows = LlmModels.Catalog
            .Select(m => new ModelRow(m.Title, m.Size, m.Hint, "models/" + m.File, LlmModels.Find(current) == m, inUse, used, m.Url))
            .ToList();
        if (LlmModels.Find(current) == null)
            rows.Add(new ModelRow($"Custom: {System.IO.Path.GetFileName(current)}", "", "Set in the config (LocalLlm.ModelPath).", current, true, inUse, used, null));
        RenderRows(_llmRows, rows, ref _shownLlm, path => Widgets.Save(_host, "LocalLlm", "ModelPath", path, _note));
    }

    /// <summary>Rebuilt only when a row's state changes, so a button under the mouse keeps its click.</summary>
    private void RenderRows(StackPanel panel, List<ModelRow> rows, ref string? shown, Action<string> use)
    {
        var states = rows.Select(r => (Row: r, Downloaded: File.Exists(AppPaths.Resolve(r.Path)), Progress: _host.DownloadProgress(r.Path))).ToList();
        var signature = string.Join("\n", states.Select(s => $"{s.Row}|{s.Downloaded}|{s.Progress}"));
        if (signature == shown)
            return;
        shown = signature;

        // One download of each kind at a time
        var downloading = states.Any(s => s.Progress != null);
        panel.Children.Clear();
        for (var i = 0; i < states.Count; i++)
        {
            if (i > 0)
                panel.Children.Add(DarkTheme.Separator());
            var (row, downloaded, progress) = states[i];
            panel.Children.Add(Row(row, downloaded, progress, downloading, use));
        }
    }

    private Control Row(ModelRow row, bool downloaded, string? progress, bool downloading, Action<string> use)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), MinHeight = 30 };
        grid.Children.Add(DarkTheme.Dot(
            progress != null ? DarkTheme.Accent
            : row.InUse && downloaded ? (row.Needed ? DarkTheme.Ok : DarkTheme.Idle)
            : row.InUse ? (row.Needed ? DarkTheme.Error : DarkTheme.Warning)
            : downloaded ? DarkTheme.Idle
            : Brushes.Transparent));

        var state = progress
                    ?? (row.InUse && downloaded ? row.InUseText
                        : row.InUse ? $"{row.InUseText}, not downloaded yet"
                        : downloaded ? "Downloaded"
                        : "Not downloaded");
        var texts = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                DarkTheme.Label(row.Title, 13),
                DarkTheme.MutedLabel(row.Size.Length > 0 ? $"{row.Size} · {state}" : state, 11),
            },
        };
        ToolTip.SetTip(texts, row.Hint);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var path = row.Path;
        Button? button = null;
        if (progress != null)
            button = DarkTheme.Button("Cancel", () => _host.CancelDownload(path), small: true);
        else if (!downloaded && (row.Url != null || WhisperModels.Find(path) != null))
        {
            var url = row.Url;
            button = DarkTheme.Button("Download", () =>
            {
                _host.DownloadModel(path, url);
                Refresh();
            }, accent: row.InUse && row.Needed, small: true);
            button.IsEnabled = !downloading;
        }
        else if (downloaded && !row.InUse)
            button = DarkTheme.Button("Use", () => use(path), small: true);
        if (button != null)
        {
            button.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
        return grid;
    }

    /// <summary>Where downloaded models are kept, with a button that opens it.</summary>
    private Control FolderRow()
    {
        var folder = AppPaths.Resolve("models");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new SelectableTextBlock
        {
            Text = folder,
            Foreground = DarkTheme.Text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var open = DarkTheme.Button("Open Folder", () =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                _host.OpenPath(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _note.Set($"Could not open the folder: {ex.Message}", DarkTheme.Error);
            }
        }, small: true);
        open.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);
        return grid;
    }

    // ---- Cloud ----

    /// <summary>Gemini: connected (masked key, Test, Remove) or a box to paste a key; rebuilt only when the key changes.</summary>
    private void RefreshCloud(string key)
    {
        if (key == _shownKey)
            return;
        _shownKey = key;
        _cloud.Children.Clear();

        var connected = key.Length > 0;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), MinHeight = 30 };
        row.Children.Add(DarkTheme.Dot(connected ? DarkTheme.Ok : Brushes.Transparent));
        var texts = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                DarkTheme.Label("Google Gemini", 13),
                DarkTheme.MutedLabel(connected
                    ? $"Speech · Text · Connected, key {GeminiClient.Mask(key)} (kept in config.json on this computer)"
                    : "Speech · Text · Not connected: a free key from Google AI Studio is enough", 11),
            },
        };
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var buttons = connected
            ? new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    DarkTheme.Button("Test", () => _ = CheckKeyAsync(key), small: true),
                    DarkTheme.Button("Remove", RemoveKey, small: true),
                },
            }
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { DarkTheme.Button("Get a Key", () => _host.OpenPath(GeminiClient.KeyPage), small: true) },
            };
        buttons.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);
        _cloud.Children.Add(row);

        if (!connected)
        {
            var save = DarkTheme.Button("Save", SaveKey, accent: true, small: true);
            var paste = Widgets.Fields(("API key", _keyBox), ("", save));
            paste.Margin = new Thickness(18, 0, 0, 0);
            _cloud.Children.Add(paste);
        }
        _cloudNote.Line.Margin = new Thickness(18, 0, 0, 0);
        _cloud.Children.Add(_cloudNote.Line);
        var later = DarkTheme.MutedLabel(
            "Choose Gemini for recognition in Speech to Text → Recognition → Engine.", 11);
        later.Margin = new Thickness(18, 2, 0, 0);
        _cloud.Children.Add(later);
    }

    private void SaveKey()
    {
        var key = (_keyBox.Text ?? "").Trim();
        if (key.Length == 0)
        {
            _cloudNote.Set("Paste the key first: AI Studio shows it after Create API key.", DarkTheme.Warning);
            return;
        }
        Widgets.Save(_host, "Cloud", "GeminiApiKey", key, _cloudNote);
        _keyBox.Text = "";
        _ = CheckKeyAsync(key);
    }

    private void RemoveKey()
    {
        _check?.Cancel();
        Widgets.Save(_host, "Cloud", "GeminiApiKey", "", _cloudNote);
        _cloudNote.Set("Key removed from the config. To revoke it for good, delete it in Google AI Studio.");
    }

    private async Task CheckKeyAsync(string key)
    {
        _check?.Cancel();
        var check = _check = new CancellationTokenSource();
        _cloudNote.Set("Checking the key with Google…", DarkTheme.Accent, sticky: true);
        string? problem;
        try
        {
            problem = await GeminiClient.CheckKeyAsync(key, check.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (check.IsCancellationRequested)
            return;
        if (problem == null)
            _cloudNote.Set("The key works.", DarkTheme.Ok);
        else
            _cloudNote.Set(problem, DarkTheme.Warning, sticky: true);
    }

    // ---- Memory ----

    private Control MemoryRow()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto") };
        grid.Children.Add(_memoryDot);
        var texts = new StackPanel
        {
            Spacing = 1,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _memory, _memoryDetail },
        };
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(_memoryButton, 2);
        grid.Children.Add(_memoryButton);
        return grid;
    }

    private void RefreshMemory()
    {
        var (loaded, detail) = _host.GetModelMemory();
        _memoryDot.Fill = loaded ? DarkTheme.Ok : DarkTheme.Idle;
        _memory.Text = loaded ? "Loaded in memory" : "Not loaded";
        _memoryDetail.Text = !loaded && RecognitionRouter.UsesGemini(_host.Config.Current)
            ? "Gemini recognises in the cloud; the local model loads only when it is needed as the fallback"
            : detail;
        _memoryButton.Content = loaded ? "Unload" : "Load";
    }

    private async Task ToggleMemoryAsync()
    {
        _memoryButton.IsEnabled = false;
        try
        {
            if (_host.GetModelMemory().Loaded)
                await _host.UnloadAsync();
            else
            {
                _memoryDot.Fill = DarkTheme.Accent;
                _memory.Text = "Loading…";
                await _host.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Load/unload failed", ex);
        }
        finally
        {
            _memoryButton.IsEnabled = true;
            RefreshMemory();
        }
    }

    private void RefreshIdle(int minutes)
    {
        if (minutes == _shownIdle)
            return;
        _shownIdle = minutes;

        var titles = IdleChoices.Select(c => c.Title).ToList();
        _idleValues = IdleChoices.Select(c => c.Minutes).ToList();
        var index = _idleValues.IndexOf(minutes);
        if (index < 0)
        {
            titles.Add($"{minutes} minutes");
            _idleValues.Add(minutes);
            index = titles.Count - 1;
        }
        _updating = true;
        _idle.ItemsSource = titles;
        _idle.SelectedIndex = index;
        _updating = false;
    }

    private void OnIdleSelected()
    {
        var i = _idle.SelectedIndex;
        if (_updating || i < 0 || i >= _idleValues.Count)
            return;
        Widgets.Save(_host, "Memory", "IdleUnloadMinutes", _idleValues[i], _note);
    }
}
