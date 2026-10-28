namespace WhisperLocal.Config;

public sealed class AppConfig
{
    /// <summary>
    /// Start WhisperLocal at logon: HKCU\...\Run entry on Windows, a LaunchAgent on macOS (both point to the current executable).
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    public HotkeyConfig Hotkey { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public WhisperConfig Whisper { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public FilterConfig Filter { get; set; } = new();

    /// <summary>
    /// Regex replacements applied after STT, in order (case-insensitive, whole words by default).
    /// Add "\w*" to catch Ukrainian case endings: "джейсон\w*" matches "Джейсоні".
    /// </summary>
    public List<DictionaryEntry> Dictionary { get; set; } =
    [
        new(@"вайр\s?гард", "WireGuard"),
        new(@"подман", "Podman"),
        // Before the Whisper rule, which would otherwise take "віспер" from "віспер флоу"
        new(@"в[іи][сз]п[еоа]?р\s?флоу", "Wispr Flow"),
        new(@"в[іи]спер\w*", "Whisper"),
        new(@"р[иі]дм[іи]|редм[іи]|redmi", "README"),
        new(@"тлм|елелем|ел\s?ел\s?ем", "LLM"),
        new(@"джейсон\w*", "JSON"),
        new(@"апаче|апач", "Apache"),
        new(@"некст\s?клауд|нек[сc]клауд", "Nextcloud"),
        new(@"мейл\s?коу|mail\s+cow", "mailcow"),
        new(@"веб\s?джі\s?ел", "WebGL"),
    ];

    public LlmConfig Llm { get; set; } = new();
    public SuggestConfig Suggest { get; set; } = new();
    public InsertConfig Insert { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public sealed class HotkeyConfig
{
    /// <summary>
    /// Active hotkey: System.Windows.Forms.Keys names joined with '+', e.g. "RMenu", "LMenu+LWin", "LControlKey+LWin".
    /// Aliases: Alt, Ctrl, Shift, Win (left keys), RightAlt, RightCtrl, RightShift, RightWin.
    /// macOS: LeftControl, LeftOption, LeftCommand, RightOption, Fn, F13...; the Windows names map to their Mac counterparts.
    /// </summary>
    public string Key { get; set; } = OperatingSystem.IsMacOS() ? "LeftControl+LeftCommand" : "LControlKey+LWin";

    /// <summary>Presses shorter than this are treated as accidental taps.</summary>
    public int MinHoldMs { get; set; } = 300;

    /// <summary>
    /// Swallow the hotkey's non-modifier key so the active app never sees it.
    /// Windows: single non-modifier keys only (F13, Scroll, ...); combinations are never swallowed and a lone
    /// Win/Alt release is masked instead. macOS: also the character key of a combination (the "/" of Option+/)
    /// while the combination is held. Modifiers are never swallowed.
    /// </summary>
    public bool SuppressKey { get; set; } = true;
}

public sealed class AudioConfig
{
    /// <summary>Substring of the capture device friendly name; empty = the system default device.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Recording stops automatically after this many seconds (0 = unlimited).</summary>
    public int MaxDurationSec { get; set; } = 120;
}

public sealed class WhisperConfig
{
    /// <summary>
    /// Relative to the executable's directory. A bare file name ("whisper-server") is also looked up in PATH.
    /// </summary>
    public string ServerExe { get; set; } = OperatingSystem.IsWindows() ? @"whisper\whisper-server.exe" : "whisper/whisper-server";

    /// <summary>Relative to the data directory (the exe directory on Windows, ~/Library/Application Support/WhisperLocal on macOS).</summary>
    public string ModelPath { get; set; } = Path.Combine("models", "ggml-large-v3.bin");
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8178;
    public string InferencePath { get; set; } = "/inference";

    /// <summary>Additional whisper-server command line arguments (threads, beam size, VAD, ...).</summary>
    /// <remarks>
    /// -dev 0 = discrete GPU (device 1 is the integrated Radeon), -sns = suppress non-speech tokens.
    /// macOS uses the Metal GPU automatically, there is a single device.
    /// </remarks>
    public string ExtraArgs { get; set; } = OperatingSystem.IsMacOS() ? "-sns -t 4" : "-dev 0 -sns -t 8";

    /// <summary>
    /// "uk" forces one language (speech in another language comes out translated into it), "auto" detects any,
    /// "uk,en" detects but accepts only the listed languages, falling back to the first.
    /// </summary>
    public string Language { get; set; } = OperatingSystem.IsMacOS() ? "uk,en" : "uk";
    public double Temperature { get; set; } = 0.0;

    /// <summary>Biases spelling of English terms; keep them in Latin script.</summary>
    public string InitialPrompt { get; set; } =
        "Розмова українською з англійськими термінами: Whisper, LLM, README, JSON, GitHub, Visual Studio, Podman, Apache, WireGuard, Nextcloud, mailcow, BLAST, WebGL, deploy, Docker.";

    /// <summary>Start whisper-server when WhisperLocal starts (otherwise on the first hotkey press).</summary>
    public bool StartOnLaunch { get; set; } = true;

    public int StartupTimeoutSec { get; set; } = 120;
    public int RequestTimeoutSec { get; set; } = 60;
}

public sealed class MemoryConfig
{
    /// <summary>Unload all servers and models after this many minutes without dictation (0 = never).</summary>
    public int IdleUnloadMinutes { get; set; } = 5;
}

public sealed class FilterConfig
{
    public int MinDurationMs { get; set; } = 500;

    /// <summary>Speech level (mean RMS of the loudest 20% frames) below this is treated as silence.</summary>
    public double MinSpeechDbfs { get; set; } = -45;

    /// <summary>Regexes of known Whisper hallucinations; dropped when they are the whole text or a trailing sentence.</summary>
    public List<string> Hallucinations { get; set; } =
    [
        @"дякую за перегляд",
        @"дякуємо за перегляд",
        @"субтитр\w*.*",
        @"редактор субтитрів.*",
        @"продовження (?:буде|наступне)",
        @"підписуйтес[ья] на (?:наш )?канал.*",
        @"ставте лайки.*",
        @"thank you for watching",
        @"thanks for watching",
    ];

    /// <summary>Regexes removed anywhere in the text (non-speech tags).</summary>
    public List<string> NoiseTags { get; set; } =
    [
        @"\[[^\]]*\]",
        @"\((?:музика|music|сміх|оплески|applause|шум)\)",
        @"\*[^*]*\*",
    ];
}

public sealed record DictionaryEntry(string Pattern, string Replacement)
{
    /// <summary>Match only whole words (letters/digits around the match are not allowed).</summary>
    public bool WholeWord { get; init; } = true;
}

public sealed class LlmConfig
{
    // Disabled until the bundled llama-server backend replaces Ollama
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "gemma3:12b";

    /// <summary>Ollama keep_alive: "10m", "1h", "0", "-1" (forever) or seconds.</summary>
    public string KeepAlive { get; set; } = "10m";

    public double Temperature { get; set; } = 0.2;

    /// <summary>Ollama "think" flag; null = automatic (false for Qwen models, not sent otherwise).</summary>
    public bool? Think { get; set; }

    /// <summary>Timeout when the model is expected to be loaded already.</summary>
    public int TimeoutMs { get; set; } = 4000;

    /// <summary>Timeout when the model probably has to be loaded into VRAM first.</summary>
    public int ColdTimeoutMs { get; set; } = 15000;

    /// <summary>Utterances with fewer words skip the LLM.</summary>
    public int MinWords { get; set; } = 8;

    /// <summary>Output/input length ratio bounds; outside them the LLM result is rejected (it answered or truncated).</summary>
    public double MinLengthRatio { get; set; } = 0.4;
    public double MaxLengthRatio { get; set; } = 1.6;

    public string SystemPrompt { get; set; } = DefaultPrompts.System;
    public List<FewShotExample> Examples { get; set; } = DefaultPrompts.Examples();

    /// <summary>Process name (without .exe; the app name on macOS) → extra instruction appended to the system prompt.</summary>
    public Dictionary<string, string> AppStyles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slack"] = "This is a casual chat message: keep it short and informal, no paragraphs unless dictated.",
        ["Telegram"] = "This is a casual chat message: keep it short and informal, no paragraphs unless dictated.",
        ["Code"] = "The text goes into a code editor: keep code identifiers, file names and shell commands verbatim.",
        ["devenv"] = "The text goes into a code editor: keep code identifiers, file names and shell commands verbatim.",
    };
}

public sealed record FewShotExample(string Input, string Output);

/// <summary>
/// Continuation hints: after a dictation, a local LLM (bundled llama-server) suggests a few ways to continue the text;
/// Option+1…N (Alt on Windows) inserts one. The model is a GGUF file downloaded from the main window.
/// </summary>
public sealed class SuggestConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Relative to the executable's directory, like Whisper.ServerExe.</summary>
    public string ServerExe { get; set; } = OperatingSystem.IsWindows() ? @"llama\llama-server.exe" : "llama/llama-server";

    /// <summary>GGUF model, relative to the data directory; the main window offers the models of <c>LlmModels.Catalog</c>.</summary>
    public string ModelPath { get; set; } = "models/gemma-3-4b-it-Q4_K_M.gguf";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8179;

    /// <summary>-c context size, -ngl layers on the GPU (Metal), no web UI, no thinking (it only adds delay).</summary>
    public string ExtraArgs { get; set; } = "-c 4096 -ngl 99 --no-webui --reasoning off";

    /// <summary>Number of hints (1–5).</summary>
    public int Count { get; set; } = 3;

    /// <summary>Upper bound of words per hint.</summary>
    public int MaxWords { get; set; } = 15;

    /// <summary>Earlier dictations into the same app within this many minutes are the context the hints continue.</summary>
    public int ContextMinutes { get; set; } = 10;

    /// <summary>Characters of context sent to the model (the end of it).</summary>
    public int ContextChars { get; set; } = 1500;

    /// <summary>How long the hints stay (longer while the mouse is over them); any key press hides them sooner.</summary>
    public double DisplaySeconds { get; set; } = 12;

    /// <summary>Higher values give more varied hints.</summary>
    public double Temperature { get; set; } = 0.8;

    /// <summary>Timeout for one request once the model is loaded; hints that arrive later are dropped.</summary>
    public int TimeoutMs { get; set; } = 8000;

    public int StartupTimeoutSec { get; set; } = 60;

    public string SystemPrompt { get; set; } = DefaultPrompts.Suggest;
}

public sealed class InsertConfig
{
    public bool AppendTrailingSpace { get; set; } = true;
    public bool RestoreClipboard { get; set; } = true;
    public int RestoreDelayMs { get; set; } = 400;
}

public sealed class UiConfig
{
    public bool ShowOverlay { get; set; } = true;

    /// <summary>How long the indicator shows a successful dictation's summary (longer while the mouse is over it).</summary>
    public double ResultSeconds { get; set; } = 6;
}

public sealed class LoggingConfig
{
    /// <summary>Log raw and final text of every dictation.</summary>
    public bool LogText { get; set; } = true;
    public bool Debug { get; set; } = false;
}
