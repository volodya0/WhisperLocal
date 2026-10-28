using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WhisperLocal.Config;
using WhisperLocal.Native;

namespace WhisperLocal.Llm;

/// <summary>
/// llama.cpp llama-server (Metal on macOS) running as a managed child process, used over its OpenAI-compatible
/// HTTP API. Started on demand, like whisper-server; the model is a GGUF file in the data directory.
/// </summary>
public sealed class LlamaServer : IDisposable
{
    private readonly ConfigManager _config;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private string? _signature;

    public LlamaServer(ConfigManager config) => _config = config;

    public bool IsLoaded => _process is { HasExited: false };

    /// <summary>Resident memory of the running llama-server, or null when it is not running or unknown.</summary>
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

    public static string ResolveServerExe(SuggestConfig c) => AppPaths.ResolveRuntime(c.ServerExe);

    public static string ResolveModel(SuggestConfig c) => AppPaths.Resolve(c.ModelPath);

    /// <summary>Starts the server and loads the model, unless that is done already.</summary>
    /// <remarks>
    /// <paramref name="ct"/> only stops the waiting: loading goes on, otherwise every hint request cancelled by a key
    /// press during a cold start would kill the half-loaded server and the next one would start over.
    /// </remarks>
    public Task WarmUpAsync(CancellationToken ct) => WarmUpCoreAsync().WaitAsync(ct);

    private async Task WarmUpCoreAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            await EnsureStartedAsync(_config.Current.Suggest, CancellationToken.None);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>One /v1/chat/completions call; returns the assistant message.</summary>
    /// <param name="schema">JSON schema the answer must follow (constrained decoding), or null for free text.</param>
    public async Task<string> ChatAsync(IReadOnlyList<(string Role, string Content)> messages, double temperature, int maxTokens,
        object? schema, CancellationToken ct)
    {
        var c = _config.Current.Suggest;
        await WarmUpAsync(ct);

        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
            ["cache_prompt"] = true, // the system prompt is the same every time
        };
        if (schema != null)
            body["response_format"] = new { type = "json_schema", json_schema = new { schema } };

        using var response = await _http.PostAsJsonAsync(BaseUrl(c) + "/v1/chat/completions", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"llama-server returned {(int)response.StatusCode}: {Truncate(json)}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public void Unload()
    {
        if (_process != null)
            Log.Info("Stopping llama-server");
        StopProcess();
        _signature = null;
    }

    private async Task EnsureStartedAsync(SuggestConfig c, CancellationToken ct)
    {
        var exe = ResolveServerExe(c);
        var model = ResolveModel(c);
        var args = $"-m \"{model}\" --host {c.Host} --port {c.Port} {c.ExtraArgs}".Trim();
        var signature = exe + "|" + args;

        if (_process is { HasExited: false } && _signature == signature)
            return;

        if (_process != null)
        {
            Log.Info("llama-server settings changed or the process exited, restarting");
            StopProcess();
        }

        if (!File.Exists(exe))
            throw new FileNotFoundException($"llama-server not found: {exe}. Run scripts/build-mac.sh, which builds it.");
        if (!File.Exists(model))
            throw new FileNotFoundException($"Language model not downloaded: {model}. Download it in the main window (Continuation hints).");

        ChildProcessJob.KillOrphans();

        // Only the bundled server is used; anything else on the port is a configuration error
        if (await PingAsync(c, ct) != null)
            throw new InvalidOperationException($"Port {c.Port} is already in use by another process, change Suggest.Port in the config");

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
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Server("[llama] " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Server("[llama] " + e.Data); };
        process.Exited += (_, _) => Log.Info($"llama-server exited with code {process.ExitCode}");

        Log.Info($"Starting llama-server: \"{exe}\" {args}");
        var sw = Stopwatch.StartNew();
        process.Start();
        ChildProcessJob.Add(process, Path.GetFileNameWithoutExtension(exe));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        // /health answers 503 while the model loads and 200 once it is ready
        while (true)
        {
            if (process.HasExited)
            {
                StopProcess();
                throw new InvalidOperationException($"llama-server exited during startup, see {Log.ServerFile}");
            }
            if (await PingAsync(c, ct) == true)
                break;
            if (sw.Elapsed > TimeSpan.FromSeconds(c.StartupTimeoutSec))
            {
                StopProcess();
                throw new TimeoutException($"llama-server did not become ready within {c.StartupTimeoutSec} s");
            }
            await Task.Delay(200, ct);
        }

        _signature = signature;
        Log.Info($"llama-server ready in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>null: nothing listens on the port; false: listening, still loading; true: ready.</summary>
    private async Task<bool?> PingAsync(SuggestConfig c, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000);
            using var response = await _http.GetAsync(BaseUrl(c) + "/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
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
            Log.Warn($"Failed to stop llama-server: {ex.Message}");
        }
        ChildProcessJob.Remove(pid);
        p.Dispose();
    }

    private static string BaseUrl(SuggestConfig c) => $"http://{c.Host}:{c.Port}";

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public void Dispose()
    {
        StopProcess();
        _http.Dispose();
    }
}
