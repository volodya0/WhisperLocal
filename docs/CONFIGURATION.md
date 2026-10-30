# Configuration

Everything WhisperLocal does is set in `config.json`. The main window (macOS) changes the common settings for you;
this page covers the rest. The file accepts `//` comments, and changes apply as soon as you save it — no restart.
If the file is missing, the app writes one with the defaults.

## Where the file is

| | File | How to open it |
|---|---|---|
| macOS | `~/Library/Application Support/WhisperLocal/config.json` | Main window → **Config** |
| Windows | `config.json` next to `WhisperLocal.exe` | Main window → **Config** |

macOS: the app's own `WhisperLocal.Mac/config.json` is copied there on the first run only; afterwards the copy in
Application Support is the one to edit. Delete it to get the defaults back.

Windows (built from source): `WhisperLocal\config.json` in the project is copied next to the exe on build, but only
when the project file is newer. Edits to the copy apply at once but are overwritten the next time the project file
changes, so for lasting changes edit the project file and rebuild.

Relative paths (`Whisper.ServerExe`, `Whisper.ModelPath`) are relative to the exe folder; on macOS `ModelPath` is
relative to the data folder.

## Main settings

| Setting | Meaning |
|---|---|
| `StartWithWindows` | Start at login: a per-user Run registry entry on Windows, a LaunchAgent in `~/Library/LaunchAgents` on macOS. |
| `Hotkey.Key` | The push-to-talk key or combination, names joined with `+` (see below). |
| `Hotkey.MinHoldMs` | Shorter presses are ignored. |
| `Hotkey.SuppressKey` | Swallow the hotkey so the app you type in does not see it. |
| `Audio.DeviceName` | Part of the microphone name; empty = the system default. |
| `Audio.MaxDurationSec` | A recording stops by itself after this long. |
| `Recognition.Engine` | `"Local"` (Whisper on this computer, the default), `"Gemini"`: recordings are sent to Google's Gemini API with `Cloud.GeminiApiKey`, or `"Groq"`: recordings are sent to Whisper on Groq's API with `Cloud.GroqApiKey`. `Whisper.Language` and `Whisper.InitialPrompt` (the terms) apply to the cloud too. |
| `Recognition.FallbackToLocal` | When the cloud fails (no internet, limit used up, timeout), transcribe the recording locally instead (default `true`). |
| `Whisper.ModelPath` | The speech model file: `models/ggml-large-v3-turbo.bin` (Windows default) or `models/ggml-large-v3.bin` (macOS default, the most accurate). |
| `Whisper.Language` | `"uk,en"` (detected per dictation, the default), `"uk,en,ru"`, `"uk"`, `"en"`, `"auto"`, or any list of language codes; the first one is the fallback. A single language makes speech in another language come out translated into it. |
| `Whisper.ExtraArgs` | Extra `whisper-server` arguments. Windows default `-dev 0 -sns -t 8` (GPU index, suppress non-speech tokens, threads); add `-bs 5` for beam search (a bit more accurate, a bit slower). |
| `Whisper.ServerExe` | The `whisper-server` to run. On macOS, `"whisper-server"` uses one from PATH (e.g. `brew install whisper-cpp`). |
| `Memory.IdleUnloadMinutes` | Unload the models after this many minutes without dictation (default 5; 0 = never). |
| `Insert.AppendTrailingSpace` | Add a space after the pasted text. |
| `Insert.RestoreClipboard` | Put the previous clipboard content back after pasting. |
| `Insert.ShowWhenNoTextField` | When nothing can take text (macOS: no text field focused; Windows: the desktop or the taskbar), open the transcript instead of pasting. |
| `Notes.Folder` | The folder the Notes page lists (`.md` and `.txt` files); empty = `WhisperLocal Notes` in Documents. `~/` is the home folder. |
| `Notes.Extension` | File type of notes made with New Note: `".md"` (default) or `".txt"`. |
| `Cloud.GeminiApiKey` | Your Google Gemini API key (from [Google AI Studio](https://aistudio.google.com/apikey)); set on the Models page, kept here as plain text. |
| `Cloud.GeminiModel` | Gemini model for recognition: `gemini-flash-latest` (default) or `gemini-flash-lite-latest` (faster), or any Gemini model id that accepts audio. |
| `Cloud.GroqApiKey` | Your Groq API key (from the [Groq console](https://console.groq.com/keys)); set on the Models page, kept here as plain text. |
| `Cloud.GroqModel` | Whisper model on Groq: `whisper-large-v3-turbo` (default, faster) or `whisper-large-v3` (most accurate). |
| `Cloud.GeminiTextModel` | Gemini model for translation: `gemini-flash-lite-latest` (default, fastest) or `gemini-flash-latest`, or any Gemini text model id. |
| `Cloud.GroqTextModel` | Groq language model for translation: `llama-3.3-70b-versatile` (default) or `llama-3.1-8b-instant` (faster), or any Groq chat model that supports JSON mode. |
| `Cloud.TimeoutSec` | A cloud request taking longer is given up (default 30). |
| `Ui.ShowOverlay` | Show the indicator at the bottom of the screen. |
| `Ui.ResultSeconds` | How long the indicator shows a dictation's summary. |
| `Logging.LogText` | Keep the text of each dictation in the log and the history (on by default). |
| `Logging.Debug` | More detail in the log. |

## Hotkey names

macOS: `LeftControl`, `RightControl`, `LeftOption`, `RightOption`, `LeftCommand`, `RightCommand`, `LeftShift`,
`RightShift`, `Fn`, `F1`…`F20`, letters, digits, `Slash`, `Comma`, `Period` and other keys. Example:
`LeftOption+Slash` for Option + / (the `/` does not reach the app while the combination is held). The Windows
names below map to the Mac keys in the same place. For `Fn`, set System Settings → Keyboard →
"Press 🌐 key to" → "Do Nothing".

Windows: .NET `Keys` names: `LControlKey+LWin` (default), `LMenu+LWin` (Left Alt + Win), `RMenu` (Right Alt),
`F13`…

## English terms in Ukrainian speech

Two mechanisms:

1. **`Whisper.InitialPrompt`** — text Whisper treats as what was said before, so terms listed there tend to be
   spelled the same way. It is a hint, not a guarantee. Keep it short (a long list dilutes it) and do not list
   words you do not use — they can leak into the text.
2. **`Dictionary`** — regex replacements applied after recognition, top to bottom, case-insensitive, whole words.
   `|` separates variants, `\w*` catches Ukrainian endings: `джейсон\w*` → `JSON` turns «в Джейсоні» into «в JSON».

Workflow: find the `raw:` line of the dictation in the log, see how the term was heard, add a dictionary entry.

## Choosing the GPU (Windows)

With several GPUs, the list of Vulkan devices is at the top of `logs\whisper-server.log` next to the exe. Put the
index of the one you want into `Whisper.ExtraArgs` as `-dev N`. An integrated GPU is usually much slower than the
CPU; `-ng` runs on the CPU only.

## Grammar check (macOS)

| Setting | Meaning |
|---|---|
| `Polish.Enabled` | Propose a corrected version after each dictation (the checkbox in Speech to Text → Post-processing). |
| `Polish.MinWords` | Shorter dictations are not checked. |
| `Polish.DisplaySeconds` | How long the proposal stays (longer while the mouse is over it). |
| `LocalLlm.ModelPath` | The GGUF model, for the grammar check and translation (chosen in Speech to Text or Notes & Translate, downloaded on the Models page; any instruction-tuned GGUF works). |
| `LocalLlm.ServerExe`, `Port`, `ExtraArgs` | The bundled `llama-server` and how it runs. |

## Listening: notes from system audio (macOS 14.2+)

| Setting | Meaning |
|---|---|
| `Listen.Hotkey` | Starts listening (key names as for `Hotkey.Key`); `""` turns the shortcut off. Default `LeftControl+LeftOption+L`. |
| `Listen.Language` | Language of what is heard: `"auto"` detects it per segment, a code (`"en"`) fixes it. |
| `Listen.TranslateTo` | Translate into this language (`"uk"`, `"en"`, …), `""` for the original only. Set from the indicator's menu. |
| `Listen.TranslateEngine` | What translates: `"Local"` (the `LocalLlm` model, the default), `"Gemini"` (`Cloud.GeminiTextModel`) or `"Groq"` (`Cloud.GroqTextModel`); the text of each line is then sent there. Without that provider's key, the local model translates. Chosen with Notes & Translate → Translation model. |
| `Listen.IncludeMe` | Also write down what is said into the microphone (`Audio.DeviceName`), as "Me", recognised in `Whisper.Language` (default `true`). An echo of the speakers is left out. |
| `Listen.SeparateSpeakers` | Tell the voices of what the Mac plays apart ([1], [2], …) with the voice model, cutting a line where another speaker starts (default `true`; needs `Listen.VoiceModelPath` downloaded). Off: everyone is "Them". |
| `Listen.VoiceModelPath` | The voice model (speaker embeddings, ONNX) for `Listen.SeparateSpeakers`, relative to the data folder; default `models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` (CAM++, downloaded on the Models page). |
| `Listen.SpeakerThreshold` | How alike two voices must sound (cosine similarity, 0–1) to be the same speaker, and below which a line is cut (default `0.6`). Higher tells similar voices apart but may split one person in two. |
| `Listen.Languages` | The languages the indicator's menu offers. |
| `Listen.PauseMs`, `Listen.MaxSegmentSec` | A pause this long ends a segment; without a pause it is cut after this many seconds. Default 500 ms and 10 s; Notes & Translate → Line speed sets both with `Listen.ProvisionalMs` (Fast 350/6/500, Balanced 500/10/800, Accurate 600/20/1000). |
| `Listen.ProvisionalMs` | While a sentence is being spoken, its text so far is shown greyed about this often (ms); `0` turns it off. |
| `Listen.TranslateWhileSpeakingMs` | While a sentence is being spoken, its text so far is also translated (greyed) at most this often (ms); `0` (default) translates only finished lines. Notes & Translate → Translate while speaking: every 2 s, every second, or as fast as possible (250). Quickest with Gemini or Groq translating. |
| `Listen.TranslateTimeoutMs`, `Listen.Temperature`, `Listen.TranslatePrompt` | How the language model translates (`{language}` in the prompt is the language name). |

Each session is a note in the notes folder (`Notes.Folder`, `Notes.Extension`), so the Notes page lists it.
Translation uses the `LocalLlm` model (the one the grammar check uses), or Gemini or Groq (`Listen.TranslateEngine`). Listening always recognises with local
Whisper, whatever `Recognition.Engine` says. The shortcut and the languages are on the Notes & Translate page.

## LLM cleanup through Ollama (optional)

The `Llm` section sends each transcript to a local [Ollama](https://ollama.com) server to remove filler words and
apply self-corrections, with per-app style instructions (`Llm.AppStyles`). Ollama is not bundled; this is off by default
(`Llm.Enabled`).

## Autocomplete (shelved)

The `Autocomplete` section belongs to an experimental feature that is switched off in the code; the app ignores it.
