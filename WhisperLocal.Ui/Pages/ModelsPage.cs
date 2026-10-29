using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WhisperLocal.Cloud;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;

namespace WhisperLocal.Ui.Pages;

/// <summary>
/// Managing models, not choosing them: the speech and language models the app offers, whether each is downloaded,
/// which feature uses it, with Download, Cancel and Delete; the models loaded in memory now, with Unload All, and when
/// they unload by themselves; the models folder; cloud models and their API keys. Which model a feature uses is chosen
/// on that feature's page (Speech to Text, Notes &amp; Translate).
/// </summary>
internal sealed class ModelsPage : Page
{
    private static readonly (string Title, int Minutes)[] IdleChoices =
        [("5 minutes", 5), ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60), ("Never", 0)];

    private readonly IAppHost _host;
    private readonly StackPanel _loaded = new() { Spacing = 4 };
    private readonly Button _unloadAll;
    private readonly ComboBox _idle = DarkTheme.Dropdown(150);
    private readonly Note _memoryNote = new();
    private List<int> _idleValues = [];
    private int? _shownIdle;
    private string? _shownLoaded;
    private bool _updating;
    private readonly StackPanel _speechRows = new();
    private readonly StackPanel _llmRows = new();
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
    private string? _shownSpeech;
    private string? _shownLlm;
    private string? _confirmDelete; // the model path whose Delete was clicked once

    public ModelsPage(IAppHost host) : base("Models")
    {
        _host = host;

        Add(DarkTheme.Section("Speech recognition", new StackPanel
        {
            Spacing = 4,
            Children = { _speechRows, _note.Line },
        }));
        if (host.HasGrammarCheck)
            Add(DarkTheme.Section("Language models", _llmRows));
        _unloadAll = DarkTheme.Button("Unload All", () => _ = UnloadAllAsync(), small: true);
        ToolTip.SetTip(_unloadAll, "Frees the memory now; models load again when you dictate, listen or check grammar");
        var unload = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        unload.Children.Add(Widgets.Fields(("Unload by themselves after", _idle), ("", DarkTheme.MutedLabel("without use"))));
        Grid.SetColumn(_unloadAll, 1);
        _unloadAll.VerticalAlignment = VerticalAlignment.Center;
        unload.Children.Add(_unloadAll);
        Add(DarkTheme.Section("Memory", new StackPanel
        {
            Spacing = 6,
            Children = { _loaded, DarkTheme.Separator(), unload, _memoryNote.Line },
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

    public override void OnHidden()
    {
        base.OnHidden();
        if (_confirmDelete == null)
            return;
        _confirmDelete = null; // a Delete clicked once does not stay armed
        _shownSpeech = _shownLlm = null;
    }

    public override void Refresh()
    {
        var cfg = _host.Config.Current;
        RefreshSpeech(cfg);
        if (_host.HasGrammarCheck)
            RefreshLlm(cfg);
        RefreshLoaded();
        RefreshIdle(cfg.Memory.IdleUnloadMinutes);
        RefreshCloud(cfg.Cloud.GeminiApiKey.Trim());
    }

    // ---- Model rows ----

    /// <summary>
    /// One catalog model: <paramref name="Path"/> is its config value ("models/…"); <paramref name="Needed"/> when
    /// the selected model is used now, so a missing one is an error (the grammar check may be off);
    /// <paramref name="Chooser"/> names the page where another model is chosen.
    /// </summary>
    private sealed record ModelRow(string Title, string Size, string Hint, string Path, bool InUse, string InUseText, bool Needed, string? Url,
        string Chooser);

    private void RefreshSpeech(AppConfig cfg)
    {
        var current = cfg.Whisper.ModelPath;
        // With Gemini the local model is only the fallback (if that is on)
        var gemini = RecognitionRouter.UsesGemini(cfg);
        var inUse = gemini ? "Fallback for Gemini" : "In use";
        var needed = !gemini || cfg.Recognition.FallbackToLocal;
        var rows = WhisperModels.Catalog
            .Select(m => new ModelRow(m.Title, m.Size, m.Hint, "models/" + m.File, WhisperModels.Find(current) == m, inUse, needed, null, "Speech to Text"))
            .ToList();
        if (WhisperModels.Find(current) == null)
            rows.Add(new ModelRow($"Custom: {System.IO.Path.GetFileName(current)}", "", "Set in the config (Whisper.ModelPath).", current, true, inUse, needed,
                null, "Speech to Text"));
        RenderRows(_speechRows, rows, ref _shownSpeech);
    }

    private void RefreshLlm(AppConfig cfg)
    {
        var current = cfg.LocalLlm.ModelPath;
        var translating = cfg.Listen.TranslateTo.Trim().Length > 0;
        var used = cfg.Polish.Enabled || translating;
        var inUse = (cfg.Polish.Enabled, translating) switch
        {
            (true, true) => "Used by the grammar check and translation",
            (true, false) => "Used by the grammar check",
            (false, true) => "Used for translation",
            _ => "Selected for the grammar check and translation (both off)",
        };
        var rows = LlmModels.Catalog
            .Select(m => new ModelRow(m.Title, m.Size, m.Hint, "models/" + m.File, LlmModels.Find(current) == m, inUse, used, m.Url,
                "Speech to Text or Notes & Translate"))
            .ToList();
        if (LlmModels.Find(current) == null)
            rows.Add(new ModelRow($"Custom: {System.IO.Path.GetFileName(current)}", "", "Set in the config (LocalLlm.ModelPath).", current, true, inUse, used,
                null, "Speech to Text or Notes & Translate"));
        RenderRows(_llmRows, rows, ref _shownLlm);
    }

    /// <summary>Rebuilt only when a row's state changes, so a button under the mouse keeps its click.</summary>
    private void RenderRows(StackPanel panel, List<ModelRow> rows, ref string? shown)
    {
        var states = rows.Select(r => (Row: r, Downloaded: File.Exists(AppPaths.Resolve(r.Path)), Progress: _host.DownloadProgress(r.Path))).ToList();
        var signature = string.Join("\n", states.Select(s => $"{s.Row}|{s.Downloaded}|{s.Progress}|{s.Row.Path == _confirmDelete}"));
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
            panel.Children.Add(Row(row, downloaded, progress, downloading));
        }
    }

    private Control Row(ModelRow row, bool downloaded, string? progress, bool downloading)
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
        else if (downloaded && row.InUse)
        {
            // The model in use stays: choosing another one first is a decision for that feature's page
            button = DarkTheme.Button("Delete", () => { }, small: true);
            button.IsEnabled = false;
            ToolTip.SetTip(button, $"In use: choose another model in {row.Chooser} first");
        }
        else if (downloaded)
        {
            var confirming = _confirmDelete == path;
            button = DarkTheme.Button(confirming ? "Delete for good?" : "Delete", () => Delete(path), small: true);
            ToolTip.SetTip(button, $"Removes {System.IO.Path.GetFileName(AppPaths.Resolve(path))} from the models folder; Download gets it back");
        }
        if (button != null)
        {
            button.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
        return grid;
    }

    /// <summary>The first click asks (the button says "Delete for good?"), the second deletes the file.</summary>
    private void Delete(string path)
    {
        if (_confirmDelete != path)
        {
            _confirmDelete = path;
            _shownSpeech = _shownLlm = null;
            Refresh();
            return;
        }
        _confirmDelete = null;
        var file = AppPaths.Resolve(path);
        try
        {
            File.Delete(file);
            Log.Info($"Model deleted: {file}");
            _note.Set($"Deleted {System.IO.Path.GetFileName(file)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Deleting {file} failed", ex);
            _note.Set($"Could not delete it: {ex.Message}", DarkTheme.Error);
        }
        _shownSpeech = _shownLlm = null;
        Refresh();
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
            "With a key, the Gemini models appear in Speech to Text → Recognition → Model.", 11);
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

    /// <summary>One row per loaded model (name, what uses it, its memory); rebuilt only when that changes.</summary>
    private void RefreshLoaded()
    {
        var loaded = _host.GetLoadedModels();
        var signature = string.Join("\n", loaded.Select(l => $"{l.Name}|{l.Detail}"));
        _unloadAll.IsEnabled = loaded.Count > 0;
        if (signature == _shownLoaded)
            return;
        _shownLoaded = signature;
        _loaded.Children.Clear();
        if (loaded.Count == 0)
        {
            _loaded.Children.Add(DarkTheme.MutedLabel("Nothing is loaded. Models load when you dictate, listen or check grammar."));
            return;
        }
        foreach (var (name, detail) in loaded)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), MinHeight = 30 };
            row.Children.Add(DarkTheme.Dot(DarkTheme.Ok));
            var texts = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { DarkTheme.Label(name, 13), DarkTheme.MutedLabel(detail, 11) },
            };
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            _loaded.Children.Add(row);
        }
    }

    private async Task UnloadAllAsync()
    {
        _unloadAll.IsEnabled = false;
        try
        {
            await _host.UnloadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Unloading failed", ex);
            _memoryNote.Set($"Could not unload: {ex.Message}", DarkTheme.Error);
        }
        finally
        {
            _shownLoaded = null;
            RefreshLoaded();
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
        Widgets.Save(_host, "Memory", "IdleUnloadMinutes", _idleValues[i], _memoryNote);
    }
}
