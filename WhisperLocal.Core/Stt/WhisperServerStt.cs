using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WhisperLocal.Config;
using WhisperLocal.Native;

namespace WhisperLocal.Stt;

/// <summary>
/// whisper.cpp whisper-server (Vulkan build on Windows, Metal on macOS) running as a managed child process, used over HTTP.
/// </summary>
public sealed class WhisperServerStt : ISpeechToText
{
    private readonly ConfigManager _config;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private string? _signature;

    public WhisperServerStt(ConfigManager config) => _config = config;

    public bool IsLoaded => _process is { HasExited: false };

    /// <summary>Resident memory of the running whisper-server, or null when it is not running or unknown.</summary>
    public long? MemoryBytes
    {
        get
        {
            try
            {
                var p = _process;
                if (p is not { HasExited: false })
                    return null;
                p.Refresh();
                return p.WorkingSet64 > 0 ? p.WorkingSet64 : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            await EnsureStartedAsync(_config.Current.Whisper, ct);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct, string? language = null, string? prompt = null)
    {
        var c = _config.Current.Whisper;
        prompt ??= c.InitialPrompt;
        await WarmUpAsync(ct);
        LastUsedUtc = DateTime.UtcNow;

        // "uk,en": detect the language, but only accept the listed ones. A fixed language makes Whisper write
        // that language even for speech in another one (in effect translating it), so English came out Ukrainian.
        var languages = ParseLanguages(language ?? c.Language);
        string text;
        if (languages.Count > 1)
        {
            var (detectedText, detected) = await RequestAsync(c, wav, "auto", prompt, ct);
            var match = languages.FirstOrDefault(l => SameLanguage(l, detected));
            if (match != null)
            {
                Log.Debug($"Language detected: {detected}");
                text = detectedText;
            }
            else
            {
                // Typically a short Ukrainian phrase taken for Russian: transcribe again in the main language
                Log.Info($"Detected language '{detected}' is not in Whisper.Language ({c.Language}), transcribing as '{languages[0]}'");
                (text, _) = await RequestAsync(c, wav, languages[0], prompt, ct);
            }
        }
        else
        {
            (text, _) = await RequestAsync(c, wav, languages[0], prompt, ct);
        }

        LastUsedUtc = DateTime.UtcNow;
        return text;
    }

    /// <summary>One /inference call; returns the text and the language whisper-server reports (full name, e.g. "english").</summary>
    private async Task<(string Text, string Language)> RequestAsync(WhisperConfig c, byte[] wav, string language, string prompt,
        CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(c.Temperature.ToString(CultureInfo.InvariantCulture)), "temperature");
        // verbose_json reports the language; probabilities would cost another encoder pass
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("true"), "no_language_probabilities");
        form.Add(new StringContent(language), "language");
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(c.RequestTimeoutSec));

        using var response = await _http.PostAsync(BaseUrl(c) + c.InferencePath, form, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"whisper-server returned {(int)response.StatusCode}: {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"whisper-server error: {error}");

        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
        var detected = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
        return (text, detected);
    }

    /// <summary>"uk", "auto" or a list "uk,en" (the first is the main language).</summary>
    internal static List<string> ParseLanguages(string value)
    {
        var list = value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant())
            .Distinct()
            .ToList();
        return list.Count == 0 ? ["auto"] : list;
    }

    // whisper-server reports full names ("ukrainian"); the config uses codes ("uk")
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "english", ["uk"] = "ukrainian", ["ru"] = "russian", ["pl"] = "polish", ["de"] = "german",
        ["fr"] = "french", ["es"] = "spanish", ["it"] = "italian", ["pt"] = "portuguese", ["cs"] = "czech",
        ["sk"] = "slovak", ["be"] = "belarusian", ["bg"] = "bulgarian", ["ro"] = "romanian", ["nl"] = "dutch",
        ["sv"] = "swedish", ["fi"] = "finnish", ["tr"] = "turkish", ["ja"] = "japanese", ["zh"] = "chinese",
    };

    internal static bool SameLanguage(string code, string reported) =>
        string.Equals(code, reported, StringComparison.OrdinalIgnoreCase)
        || (LanguageNames.TryGetValue(code, out var name) && string.Equals(name, reported, StringComparison.OrdinalIgnoreCase));

    public void Unload()
    {
        if (_process != null)
            Log.Info("Stopping whisper-server");
        StopProcess();
        _signature = null;
    }

    private async Task EnsureStartedAsync(WhisperConfig c, CancellationToken ct)
    {
        var exe = ResolveServerExe(c.ServerExe);
        var model = AppPaths.Resolve(c.ModelPath);
        var args = BuildArgs(c, model);
        var signature = exe + "|" + args;

        if (_process is { HasExited: false } && _signature == signature)
            return;

        if (_process != null)
        {
            Log.Info("whisper-server settings changed or the process exited, restarting");
            StopProcess();
        }

        if (!File.Exists(exe))
            throw new FileNotFoundException(OperatingSystem.IsMacOS()
                ? $"whisper-server not found: {exe}. Run scripts/build-mac.sh, or install it with 'brew install whisper-cpp' and set Whisper.ServerExe to \"whisper-server\""
                : $"whisper-server not found: {exe}");
        if (!File.Exists(model))
        {
            // Create the folder so the user can drop the downloaded model straight into it
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            throw new ModelNotFoundException(model);
        }

        ChildProcessJob.KillOrphans();

        // Only the bundled server is used; anything else on the port is a configuration error
        if (await PingAsync(c, ct))
            throw new InvalidOperationException($"Port {c.Port} is already in use by another process, change Whisper.Port in the config");

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Server(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Server(e.Data); };
        process.Exited += (_, _) => Log.Info($"whisper-server exited with code {process.ExitCode}");

        Log.Info($"Starting whisper-server: \"{exe}\" {args}");
        var sw = Stopwatch.StartNew();
        process.Start();
        ChildProcessJob.Add(process, Path.GetFileNameWithoutExtension(exe));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        while (true)
        {
            if (process.HasExited)
            {
                StopProcess();
                throw new InvalidOperationException($"whisper-server exited during startup, see {Log.ServerFile}");
            }
            if (await PingAsync(c, ct))
                break;
            if (sw.Elapsed > TimeSpan.FromSeconds(c.StartupTimeoutSec))
            {
                StopProcess();
                throw new TimeoutException($"whisper-server did not become ready within {c.StartupTimeoutSec} s");
            }
            await Task.Delay(200, ct);
        }

        _signature = signature;
        LastUsedUtc = DateTime.UtcNow;
        Log.Info($"whisper-server ready in {sw.ElapsedMilliseconds} ms");
    }

    private async Task<bool> PingAsync(WhisperConfig c, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000);
            using var response = await _http.GetAsync(BaseUrl(c) + "/", cts.Token);
            return true; // any HTTP answer means the server is listening
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void StopProcess()
    {
        var p = _process;
        _process = null;
        if (p == null)
            return;
        var pid = p.Id;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
                p.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to stop whisper-server: {ex.Message}");
        }
        ChildProcessJob.Remove(pid);
        p.Dispose();
    }

    /// <summary>
    /// Relative paths are relative to the executable's directory; a bare file name ("whisper-server")
    /// that is not there is looked up in PATH and the usual Homebrew locations.
    /// </summary>
    internal static string ResolveServerExe(string serverExe)
    {
        var exe = AppPaths.ResolveRuntime(serverExe);
        if (File.Exists(exe) || serverExe.IndexOfAny(['/', '\\']) >= 0)
            return exe;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        IEnumerable<string> dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (OperatingSystem.IsMacOS())
            dirs = dirs.Concat(["/opt/homebrew/bin", "/usr/local/bin"]); // apps started from Finder get a minimal PATH
        return dirs.Select(d => Path.Combine(d, serverExe)).FirstOrDefault(File.Exists) ?? exe;
    }

    private static string BuildArgs(WhisperConfig c, string model) =>
        // A language list is chosen per request; the server itself only knows one language or "auto"
        $"-m \"{model}\" --host {c.Host} --port {c.Port} -l {(ParseLanguages(c.Language) is [var single] ? single : "auto")} {c.ExtraArgs}".Trim();

    private static string BaseUrl(WhisperConfig c) => $"http://{c.Host}:{c.Port}";

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public void Dispose()
    {
        StopProcess();
        _http.Dispose();
    }
}
