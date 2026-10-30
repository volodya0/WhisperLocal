using System.Text.Encodings.Web;
using System.Text.Json;

namespace WhisperLocal.Config;

/// <summary>Loads config.json, writes defaults on first run and hot-reloads on change.</summary>
public sealed class ConfigManager : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        // Keep Cyrillic readable in the file
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounce;
    private string? _loadedText; // the file as last loaded: an unchanged file is not applied again
    private readonly object _reloading = new(); // ReloadNow (UI thread) and the watcher's timer

    public string FilePath { get; }
    public AppConfig Current { get; private set; }

    /// <summary>Raised on a thread-pool thread after a successful reload.</summary>
    public event Action? Changed;

    public ConfigManager(string filePath, bool watch)
    {
        FilePath = filePath;
        Current = LoadOrCreate();
        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);

        if (watch)
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e) => _debounce.Change(500, Timeout.Infinite);

    private AppConfig LoadOrCreate()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new AppConfig();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, JsonOptions));
            Log.Info($"Default config written to {FilePath}");
            return defaults;
        }
        _loadedText = ReadWithRetry();
        return Parse(_loadedText);
    }

    /// <summary>
    /// Reads the file now instead of waiting for the watcher (which lags, and is debounced): a setting saved from the
    /// window applies, and shows, at once. The watcher's reload that follows finds the file unchanged and does nothing.
    /// </summary>
    public void ReloadNow()
    {
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        Reload();
    }

    private void Reload()
    {
        try
        {
            lock (_reloading)
            {
                var text = ReadWithRetry();
                if (text == _loadedText)
                    return;
                Current = Parse(text);
                _loadedText = text;
            }
            Log.Info("Config reloaded");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Config reload failed, keeping previous settings", ex);
        }
    }

    private string ReadWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(FilePath);
            }
            catch (IOException) when (attempt < 5)
            {
                // Editors may hold the file briefly while saving
                Thread.Sleep(100);
            }
        }
    }

    private static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? throw new InvalidDataException("Config file is empty");

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
