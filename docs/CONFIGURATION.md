# Configuration

Everything WhisperLocal does is set in `config.json`. The main window (macOS) changes the common settings for you;
this page covers the rest. The file accepts `//` comments, and changes apply as soon as you save it — no restart.
If the file is missing, the app writes one with the defaults.

## Where the file is

| | File | How to open it |
|---|---|---|
| macOS | `~/Library/Application Support/WhisperLocal/config.json` | Main window → **Config** |
| Windows | `config.json` next to `WhisperLocal.exe` | Main window → **Config**, or tray icon → **Open config** |

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
| `Whisper.ModelPath` | The speech model file (`models/ggml-large-v3.bin` or `models/ggml-large-v3-turbo.bin`). |
| `Whisper.Language` | `"uk,en"` (detected per dictation, the default), `"uk,en,ru"`, `"uk"`, `"en"`, `"auto"`, or any list of language codes; the first one is the fallback. A single language makes speech in another language come out translated into it. |
| `Whisper.ExtraArgs` | Extra `whisper-server` arguments. Windows default `-dev 0 -sns -t 8` (GPU index, suppress non-speech tokens, threads); add `-bs 5` for beam search (a bit more accurate, a bit slower). |
| `Whisper.ServerExe` | The `whisper-server` to run. On macOS, `"whisper-server"` uses one from PATH (e.g. `brew install whisper-cpp`). |
| `Memory.IdleUnloadMinutes` | Unload the models after this many minutes without dictation (default 5; 0 = never). |
| `Insert.AppendTrailingSpace` | Add a space after the pasted text. |
| `Insert.RestoreClipboard` | Put the previous clipboard content back after pasting. |
| `Insert.ShowWhenNoTextField` | macOS: when no text field is focused, open the transcript instead of pasting. |
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
| `Polish.Enabled` | Propose a corrected version after each dictation (the checkbox in the main window). |
| `Polish.MinWords` | Shorter dictations are not checked. |
| `Polish.DisplaySeconds` | How long the proposal stays (longer while the mouse is over it). |
| `LocalLlm.ModelPath` | The GGUF model (chosen and downloaded in the main window; any instruction-tuned GGUF works). |
| `LocalLlm.ServerExe`, `Port`, `ExtraArgs` | The bundled `llama-server` and how it runs. |

## LLM cleanup through Ollama (optional)

The `Llm` section sends each transcript to a local [Ollama](https://ollama.com) server to remove filler words and
apply self-corrections, with per-app style instructions (`Llm.AppStyles`). Ollama is not bundled; this is off by default
(`Llm.Enabled`).

## Autocomplete (shelved)

The `Autocomplete` section belongs to an experimental feature that is switched off in the code; the app ignores it.
