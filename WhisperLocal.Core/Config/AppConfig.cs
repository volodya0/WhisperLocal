using WhisperLocal.Pipeline;

namespace WhisperLocal.Config;

public sealed class AppConfig
{
    /// <summary>
    /// Start WhisperLocal at logon: HKCU\...\Run entry on Windows, a LaunchAgent on macOS (both point to the current executable).
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    public HotkeyConfig Hotkey { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public RecognitionConfig Recognition { get; set; } = new();
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
    public LocalLlmConfig LocalLlm { get; set; } = new();
    public AutocompleteConfig Autocomplete { get; set; } = new();
    public PolishConfig Polish { get; set; } = new();
    public ListenConfig Listen { get; set; } = new();
    public InsertConfig Insert { get; set; } = new();
    public NotesConfig Notes { get; set; } = new();
    public CloudConfig Cloud { get; set; } = new();
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

/// <summary>Which engine turns speech into text. Whisper.Language and Whisper.InitialPrompt apply to both.</summary>
public sealed class RecognitionConfig
{
    /// <summary>
    /// "Local" (Whisper on this computer), "Gemini" (Google's cloud, needs Cloud.GeminiApiKey) or "Groq" (Whisper in
    /// Groq's cloud, needs Cloud.GroqApiKey).
    /// </summary>
    public string Engine { get; set; } = "Local";

    /// <summary>When the cloud fails (no internet, limit used up), transcribe the same recording locally instead.</summary>
    public bool FallbackToLocal { get; set; } = true;
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
/// The bundled llama-server and its GGUF model, shared by autocomplete and the grammar check. The model is downloaded
/// from the main window; the server starts on demand and unloads with the speech model.
/// </summary>
public sealed class LocalLlmConfig
{
    /// <summary>Relative to the executable's directory, like Whisper.ServerExe.</summary>
    public string ServerExe { get; set; } = OperatingSystem.IsWindows() ? @"llama\llama-server.exe" : "llama/llama-server";

    /// <summary>GGUF model, relative to the data directory; the main window offers the models of <c>LlmModels.Catalog</c>.</summary>
    public string ModelPath { get; set; } = "models/gemma-3-4b-it-Q4_K_M.gguf";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8179;

    /// <summary>-c context size, -ngl layers on the GPU (Metal), no web UI, no thinking (it only adds delay).</summary>
    public string ExtraArgs { get; set; } = "-c 4096 -ngl 99 --no-webui --reasoning off";

    public int StartupTimeoutSec { get; set; } = 60;
}

/// <summary>
/// Autocomplete while dictating, like code completion: as you speak, one grey continuation finishes the sentence.
/// Just say it, or ignore it and go on. Shelved while it is too raw: off whatever Mode says, and not in the UI.
/// </summary>
public sealed class AutocompleteConfig
{
    /// <summary>"Off"; "Always": refreshed about every RefreshMs while speaking, highlighted on a pause; "Pause": only on a pause.</summary>
    public string Mode { get; set; } = "Off";

    /// <summary>Upper bound of words in the continuation; it also stops at the end of the sentence.</summary>
    public int MaxWords { get; set; } = 12;

    /// <summary>"Always" mode: how often the live transcript and the continuation are refreshed while new speech comes in.</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>Silence after speech that counts as a pause (searching for a word).</summary>
    public int PauseMs { get; set; } = 700;

    /// <summary>Earlier dictations into the same app within this many minutes come before the transcript as context.</summary>
    public int ContextMinutes { get; set; } = 2;

    /// <summary>Characters of earlier context (the end of it).</summary>
    public int ContextChars { get; set; } = 600;

    /// <summary>0 always takes the most likely words, like code completion.</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Timeout for one completion once the model is loaded; late ones are dropped.</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>False until autocomplete is ready to come back; then Mode decides again.</summary>
    public static bool Available => false;

    public bool IsOn => Available && !string.Equals(Mode, "Off", StringComparison.OrdinalIgnoreCase);
    public bool OnPauseOnly => string.Equals(Mode, "Pause", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Grammar check after a dictation: the local LLM rewrites the text as correct, natural text in the same language and
/// a panel proposes it, with the changed words highlighted; Insert replaces the pasted text, Copy copies it.
/// </summary>
public sealed class PolishConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// What checks: "Local" (the LocalLlm model, the default), "Gemini" (Cloud.GeminiTextModel) or "Groq"
    /// (Cloud.GroqTextModel, the same text models translation uses); the dictated text is then sent there. A cloud
    /// engine without its key checks locally.
    /// </summary>
    public string Engine { get; set; } = "Local";

    /// <summary>Shorter dictations are not checked.</summary>
    public int MinWords { get; set; } = 3;

    /// <summary>How long the proposal stays (longer while the mouse is over it); the next dictation hides it too.</summary>
    public double DisplaySeconds { get; set; } = 15;

    public double Temperature { get; set; } = 0.2;

    /// <summary>Timeout once the model is loaded (or of the cloud request); a late proposal is dropped.</summary>
    public int TimeoutMs { get; set; } = 15000;

    public string SystemPrompt { get; set; } = DefaultPrompts.Polish;
}

/// <summary>
/// Listening (macOS 14.2+): a shortcut starts transcribing system audio (a call, a video) into a notes file, optionally
/// translated by a language model (local, or Gemini or Groq with the user's key); it runs until the indicator's stop
/// button is clicked.
/// </summary>
public sealed class ListenConfig
{
    /// <summary>Starts listening; key names as in Hotkey.Key. Pressing it again while listening only shows the indicator.</summary>
    public string Hotkey { get; set; } = "LeftControl+LeftOption+L";

    /// <summary>Language of what is heard: "auto" detects it per segment, a code ("en") fixes it.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Translate into this language ("uk", "en", …); "" keeps the original only. Changed from the indicator.</summary>
    public string TranslateTo { get; set; } = "";

    /// <summary>
    /// What translates: "Local" (the language model on this computer, LocalLlm.ModelPath), "Gemini"
    /// (Cloud.GeminiTextModel) or "Groq" (Cloud.GroqTextModel); a cloud engine without its key translates locally.
    /// </summary>
    public string TranslateEngine { get; set; } = "Local";

    /// <summary>
    /// What recognises the speech: "Local" (the Whisper model on this computer set in Speech to Text, Whisper.ModelPath),
    /// "Gemini" (Cloud.GeminiModel) or "Groq" (Cloud.GroqModel); the audio of each line is then sent there, and the
    /// text so far of the sentence being spoken is not shown (to stay within the provider's limits). A cloud engine
    /// without its key recognises locally.
    /// </summary>
    public string RecognitionEngine { get; set; } = "Local";

    /// <summary>
    /// Also write down what is said into the microphone, as "Me"; what the Mac plays is everyone else. Without
    /// headphones the microphone hears the speakers too: such lines are recognised as an echo and dropped.
    /// </summary>
    public bool IncludeMe { get; set; } = true;

    /// <summary>Tell the voices in what the Mac plays apart (Speaker 1, 2, …) with the voice model (VoiceModelPath).</summary>
    public bool SeparateSpeakers { get; set; } = true;

    /// <summary>The voice model for SeparateSpeakers, relative to the data directory; downloaded on the Models page.</summary>
    public string VoiceModelPath { get; set; } = "models/" + VoiceModels.Default.File;

    /// <summary>How alike two lines' voices must be (cosine similarity, 0–1) to count as the same speaker.</summary>
    public double SpeakerThreshold { get; set; } = VoiceModels.DefaultThreshold;

    /// <summary>Languages offered in the indicator's menu.</summary>
    public List<string> Languages { get; set; } = ["uk", "en", "de", "pl", "es", "fr"];

    // PauseMs, MaxSegmentSec and ProvisionalMs are set together by Notes & Translate → Line speed

    /// <summary>A pause this long ends a segment.</summary>
    public int PauseMs { get; set; } = 500;

    /// <summary>A segment is cut here even without a pause (Whisper takes at most 30 s).</summary>
    public int MaxSegmentSec { get; set; } = 10;

    /// <summary>While a sentence is being spoken, its text so far is shown (greyed) about this often; 0 turns it off.</summary>
    public int ProvisionalMs { get; set; } = 800;

    /// <summary>
    /// While a sentence is being spoken, its text so far is translated (greyed) at most this often; 0 translates only
    /// finished lines. Needs ProvisionalMs; fastest with Gemini or Groq translating.
    /// </summary>
    public int TranslateWhileSpeakingMs { get; set; } = 0;

    public double Temperature { get; set; } = 0.1;

    /// <summary>Timeout of one translation once the model is loaded (or of one cloud request); on timeout the line stays untranslated.</summary>
    public int TranslateTimeoutMs { get; set; } = 20000;

    /// <summary>{language} is replaced with the language name.</summary>
    public string TranslatePrompt { get; set; } = DefaultPrompts.Translate;
}

public sealed class InsertConfig
{
    public bool AppendTrailingSpace { get; set; } = true;
    public bool RestoreClipboard { get; set; } = true;
    public int RestoreDelayMs { get; set; } = 400;

    /// <summary>macOS: when no text input is focused, open the transcript (editable, to copy) instead of pasting into nothing.</summary>
    public bool ShowWhenNoTextField { get; set; } = true;
}

/// <summary>The Notes page: text files in one folder, newest first.</summary>
public sealed class NotesConfig
{
    /// <summary>Folder with the notes; empty = "WhisperLocal Notes" in Documents. "~/" is the home folder.</summary>
    public string Folder { get; set; } = "";

    /// <summary>File type of new notes: ".md" or ".txt". The Notes page lists both.</summary>
    public string Extension { get; set; } = ".md";
}

/// <summary>Cloud models with the user's own API keys; empty = not connected. Stored in this file as plain text.</summary>
public sealed class CloudConfig
{
    /// <summary>Google Gemini API key from Google AI Studio ("AIza…").</summary>
    public string GeminiApiKey { get; set; } = "";

    /// <summary>Gemini model for recognition; the "-latest" aliases follow Google's newest Flash models.</summary>
    public string GeminiModel { get; set; } = "gemini-flash-latest";

    /// <summary>Groq API key from the Groq console ("gsk_…").</summary>
    public string GroqApiKey { get; set; } = "";

    /// <summary>Whisper model on Groq for recognition: "whisper-large-v3-turbo" or "whisper-large-v3".</summary>
    public string GroqModel { get; set; } = "whisper-large-v3-turbo";

    /// <summary>Gemini model for text (translation); Flash-Lite answers fastest.</summary>
    public string GeminiTextModel { get; set; } = "gemini-flash-lite-latest";

    /// <summary>Groq language model for text (translation).</summary>
    public string GroqTextModel { get; set; } = "llama-3.3-70b-versatile";

    /// <summary>A cloud request taking longer than this is given up (and falls back to local recognition).</summary>
    public int TimeoutSec { get; set; } = 30;
}

public sealed class UiConfig
{
    public bool ShowOverlay { get; set; } = true;

    /// <summary>How long the indicator shows a successful dictation's summary (longer while the mouse is over it).</summary>
    public double ResultSeconds { get; set; } = 3;
}

public sealed class LoggingConfig
{
    /// <summary>Log raw and final text of every dictation.</summary>
    public bool LogText { get; set; } = true;
    public bool Debug { get; set; } = false;
}
