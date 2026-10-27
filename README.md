# WhisperLocal

*Українською: [README.uk.md](README.uk.md)*

Local push-to-talk dictation for Windows and macOS (Wispr Flow–style): hold a hotkey, speak, release —
the recognized text is pasted into the active window. Everything runs locally and portably on the GPU
(bundled whisper.cpp Vulkan build on Windows, Metal on macOS); nothing is installed or auto-updated.

```
Hotkey (WH_KEYBOARD_LL) → WASAPI capture → 16 kHz mono
  → whisper-server (bundled child process, HTTP /inference)
  → hallucination filter + regex dictionary
  → optional LLM cleanup (disabled by default)
  → clipboard + Ctrl+V, clipboard restored
```

## Setting up on a new PC

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and a current GPU driver (Vulkan is part of AMD/NVIDIA/Intel drivers).
2. Clone and build:
   ```
   git clone <repo-url> WhisperLocal
   cd WhisperLocal
   dotnet build WhisperLocal.sln -c Release
   ```
3. Download the model (~3 GB, SHA256-verified) into `models\` next to the exe:
   ```
   powershell -ExecutionPolicy Bypass -File scripts\get-model.ps1 -Configuration Release
   ```
   Without it the app shows a "model not found" dialog with the exact folder and download link.
4. Run `WhisperLocal\bin\Release\net8.0-windows\WhisperLocal.exe`.
5. If the PC has several GPUs, check the Vulkan device list at the top of `logs\whisper-server.log` next to the exe
   and set `-dev N` in `Whisper.ExtraArgs`.

Settings come with the repository (`WhisperLocal\config.json`), nothing to copy.
The whisper.cpp binaries are in the repository (`WhisperLocal\Runtime\whisper`, see `VERSION.md` there);
the Vulkan build is vendor-neutral, no rebuild is needed for a different GPU.

## macOS

Same pipeline, macOS-native pieces: CGEventTap hotkey → AudioQueue capture (16 kHz mono) → whisper-server (Metal)
→ filters → pasteboard + Cmd+V. A menu bar app (no Dock icon); Apple Silicon or Intel, macOS 12+.

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), the Xcode Command Line Tools
   (`xcode-select --install`) and CMake (`brew install cmake`).
2. Build whisper-server with Metal and the app bundle:
   ```
   scripts/build-mac.sh --install        # → artifacts/WhisperLocal.app, copied to ~/Applications
   ```
   The first run builds whisper.cpp (pinned tag, see the script) into `WhisperLocal.Mac/Runtime/whisper`, not in git.
   Already have `brew install whisper-cpp`? Set `Whisper.ServerExe` to `"whisper-server"` and it is found in PATH.
3. Download the model into `~/Library/Application Support/WhisperLocal/models`:
   ```
   scripts/get-model.sh
   ```
   `ggml-large-v3-turbo.bin` is noticeably faster on base M-series chips and 8 GB Macs.
4. Start WhisperLocal from ~/Applications and grant the permissions it asks for:
   - **Accessibility** (System Settings → Privacy & Security → Accessibility): the global hotkey and the Cmd+V paste.
     The hotkey starts working as soon as it is allowed, no restart needed.
   - **Microphone**: asked on the first dictation.
5. Hold **Left Control + Left Command**, speak, release.

macOS specifics:

- Config, logs and models live in `~/Library/Application Support/WhisperLocal` ("Show data folder" in the menu),
  not inside the app. `WhisperLocal.Mac/config.json` is installed there on the first run only; afterwards edit the
  installed copy ("Open config", hot-reloaded). Delete it to get the project defaults again.
- Hotkey names: `LeftControl`, `RightControl`, `LeftOption`, `RightOption`, `LeftCommand`, `RightCommand`,
  `LeftShift`, `RightShift`, `Fn`, `F1`…`F20`; the Windows names (`LControlKey+LWin`, `RMenu`…) map to the
  Mac keys in the same place. For `Fn`, set System Settings → Keyboard → "Press 🌐 key to" → "Do Nothing".
- `StartWithWindows` means "start at login" here: a LaunchAgent in `~/Library/LaunchAgents`.
- The build is signed ad hoc, so every rebuild looks like a new app to macOS and the Accessibility switch
  stops working: remove WhisperLocal from the list with "–" and add it again. Building with
  `CODESIGN_IDENTITY="<certificate name>" scripts/build-mac.sh` (e.g. a self-signed code signing certificate
  from Keychain Access) keeps the permission across rebuilds.
- Notifications are shown through `osascript`, so they appear as coming from Script Editor.
- For development, `dotnet run --project WhisperLocal.Mac` works too; macOS then asks for the permissions
  on behalf of the terminal app.

## Layout

```
WhisperLocal\                       repository
  scripts\get-model.ps1             downloads and verifies a Whisper model
  scripts/build-mac.sh, get-model.sh  macOS: builds WhisperLocal.app / downloads the model
  WhisperLocal.Core\                platform-neutral: config, pipeline, whisper-server client, filters, LLM, batch
  WhisperLocal.Mac\                 macOS menu bar app (AppKit/CoreGraphics/AudioToolbox interop), own config.json
  WhisperLocal\                     Windows tray app
    config.json                     settings (copied to the output on build)
    Runtime\whisper\                whisper-server.exe + DLLs (Vulkan build)

WhisperLocal\WhisperLocal\bin\<Configuration>\net8.0-windows\     next to the exe
  WhisperLocal.exe
  config.json                       copy the app reads
  whisper\                          copied from Runtime\whisper
  models\ggml-large-v3.bin          downloaded, not in source control
  logs\
```

Relative paths in the config (`Whisper.ServerExe`, `Whisper.ModelPath`) are relative to the exe directory
(on macOS, `ModelPath` is relative to the data folder).

## Config

`WhisperLocal\config.json` in the project is the source of truth and is in source control; the build copies it
next to the exe (only when the project file is newer), and the app reads that copy. It accepts `//` comments and is
hot-reloaded on save. Edit the project file and rebuild — edits made to the copy next to the exe
(e.g. via "Open config") apply immediately but are not in git and are overwritten once the project file changes.
If the file is missing, the app writes one with defaults. Logs go to `logs\` next to the exe
(`whisper-server.log` holds the server output).

Key settings:

- `StartWithWindows` — per-user Run registry entry pointing to the exe that applied the setting.
- `Hotkey.Key` — Keys names joined with `+`: `LControlKey+LWin` (default), `LMenu+LWin`, `RMenu`, `F13`…
- `Audio.DeviceName` — part of the microphone name; empty = Windows default.
- `Whisper.ExtraArgs` — defaults to `-dev 0 -sns -t 8` (GPU index, suppress non-speech tokens, threads); add `-bs 5` for beam search.
- `Memory.IdleUnloadMinutes` — auto-unload after inactivity (default 5).

## English terms in Ukrainian speech

Two mechanisms, both in the config:

1. **`Whisper.InitialPrompt`** — text Whisper treats as preceding context, so terms listed there tend to be
   spelled the same way. It is a hint, not a guarantee; keep it short (a long list dilutes it), and do not
   list words you do not use — they can leak into the text.
2. **`Dictionary`** — regex replacements applied after recognition, top to bottom, case-insensitive,
   whole words. `|` separates variants, `\w*` catches Ukrainian endings (`джейсон\w*` → `JSON`).

Workflow: look at the `raw:` lines in the log, find the misheard form, add a dictionary entry; the config reloads automatically.

## Tray menu

- **Enabled** — turn dictation on/off.
- **Unload from memory** — stops all servers and unloads all models to free RAM/VRAM (disabled when nothing is loaded); the next hotkey press starts everything again. Happens automatically after `Memory.IdleUnloadMinutes` without dictation.
- Open config / Open log, Exit.

## Batch evaluation

```
WhisperLocal.exe --batch D:\Dictations [--out results.csv] [--config path\config.json]
```

On macOS: `~/Applications/WhisperLocal.app/Contents/MacOS/WhisperLocal --batch ~/Dictations` (files are decoded with `afconvert`).

Runs every wav/mp3/m4a/flac in the folder through the pipeline and writes a CSV (timings, raw,
dictionary, final text) plus a readable `.txt` next to it.

## Measured performance

Whisper large-v3, 10–15 s utterances:

| Device | Recognition | Verdict |
|---|---|---|
| AMD Radeon RX 6800 XT (Vulkan, `-dev 0`) | ~0.5 s (first request after start ~1.4 s), model start ~2.5 s, ~3.1 GB VRAM | comfortable |
| CPU only (`-ng`) | noticeably slower | usable as a fallback |
| Integrated AMD Radeon Graphics (Vulkan, `-dev 1`) | much slower than CPU | not usable |

## Notes

- Exit WhisperLocal before rebuilding: the running exe is locked.
- The optional LLM cleanup stage talks to an Ollama HTTP API, which is not bundled; it is disabled by default (`Llm.Enabled`).
- Only the bundled whisper-server is started; if its port is taken by another process, an error is shown.
- Pasting into elevated windows is blocked by UIPI unless WhisperLocal runs as administrator (a warning is logged).
- On hotkey press, the servers are started while you speak, so a cold start mostly overlaps with speaking.
