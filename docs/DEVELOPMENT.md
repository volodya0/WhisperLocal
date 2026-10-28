# Development

How WhisperLocal is built, released and evaluated. For the code layout see
[PROJECT_STRUCTURE.md](PROJECT_STRUCTURE.md); for settings, [CONFIGURATION.md](CONFIGURATION.md).

```
Hotkey → microphone capture → 16 kHz mono PCM
  → whisper-server (bundled child process, HTTP /inference)
  → hallucination filter + regex dictionary
  → optional LLM cleanup (Ollama, off by default)
  → clipboard + Ctrl+V / Cmd+V, clipboard restored
  → optional grammar check proposal (macOS, llama-server)
```

## Windows

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and a current GPU driver (Vulkan is
   part of the AMD, NVIDIA and Intel drivers).
2. Clone and build:
   ```
   git clone https://github.com/volodya0/WhisperLocal.git
   cd WhisperLocal
   dotnet build WhisperLocal.sln -c Release
   ```
3. Download the model (~3 GB, SHA256-verified) into `models\` next to the exe:
   ```
   powershell -ExecutionPolicy Bypass -File scripts\get-model.ps1 -Configuration Release
   ```
   Without it the app shows a "model not found" dialog with the exact folder and download link.
4. Run `WhisperLocal\bin\Release\net8.0-windows\WhisperLocal.exe`.
5. With several GPUs, see [choosing the GPU](CONFIGURATION.md#choosing-the-gpu-windows).

The whisper.cpp binaries are in the repository (`WhisperLocal\Runtime\whisper`, see `VERSION.md` there). The
Vulkan build is vendor-neutral, so nothing needs rebuilding for a different GPU. Settings come with the repository
(`WhisperLocal\config.json`).

Output next to the exe:

```
WhisperLocal\bin\<Configuration>\net8.0-windows\
  WhisperLocal.exe
  config.json                  the copy the app reads
  whisper\                     copied from Runtime\whisper
  models\ggml-large-v3.bin     downloaded, not in git
  logs\                        app log and whisper-server.log
```

Exit WhisperLocal before rebuilding: the running exe is locked.

## macOS

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), the Xcode Command Line Tools
   (`xcode-select --install`) and CMake (`brew install cmake`).
2. Build whisper-server and llama-server with Metal, and the app bundle:
   ```
   scripts/build-mac.sh --install        # → artifacts/WhisperLocal.app, copied to ~/Applications
   scripts/build-mac.sh --dmg            # or a drag-to-Applications disk image
   ```
   The first run builds whisper.cpp and llama.cpp (pinned tags, see the script) into `WhisperLocal.Mac/Runtime`,
   which is not in git.
3. The model can be downloaded in the app, or with `scripts/get-model.sh` into
   `~/Library/Application Support/WhisperLocal/models`.

`dotnet run --project WhisperLocal.Mac` works too; macOS then asks for the permissions on behalf of the terminal app.

### Signing and the Accessibility permission

macOS ties the Accessibility permission to the app's signature. An ad hoc signed build looks like a new app after
every rebuild, and the switch in System Settings stops working (remove the app with "–" and add it again). Build with
`CODESIGN_IDENTITY="<certificate name>" scripts/build-mac.sh` (a self-signed code signing certificate from Keychain
Access is enough) to keep the permission across rebuilds.

Notifications are shown through `osascript`, so they appear as coming from Script Editor.

## CI and releases

`.github/workflows/macos.yml` ("Build") runs on every push to any branch, every pull request and on demand. It
builds the macOS DMG on GitHub's Mac runners (Apple Silicon) and the Windows app on a Windows runner, runs each app's
`--self-test`, and keeps the DMG and a zip of the Windows app folder as workflow artifacts.

- Every push to `main` is released automatically as `v1.0.<run number>`, with both downloads.
- A pushed tag like `v1.2.0` is released with that version.

The release's notes are the commit message. "Check for Updates…" in the apps reads the latest release of the
repository they were built from (`-p:UpdateRepository`) and verifies the download's checksum. macOS also checks the
DMG's signer, replaces the app and restarts; Windows unpacks the zip and, once the app has quit, copies it over the
app folder (keeping `config.json`) and starts it again.

With the repository secrets `MACOS_CERT_P12` (base64 of a `.p12` with a code signing certificate; self-signed is
enough) and `MACOS_CERT_PASSWORD`, every build carries the same signature, so the Accessibility permission survives
updates. Without them builds are signed ad hoc.


## Batch evaluation

Runs every audio file in a folder through the pipeline, for comparing settings and models:

```
WhisperLocal.exe --batch D:\Dictations [--out results.csv] [--config path\config.json]
~/Applications/WhisperLocal.app/Contents/MacOS/WhisperLocal --batch ~/Dictations
```

Writes a CSV (timings, raw, after the dictionary, final text) plus a readable `.txt` next to it. Windows decodes
files with Media Foundation, macOS with `afconvert`.

## Measured performance

Whisper large-v3, 10–15 s utterances:

| Device | Recognition | Verdict |
|---|---|---|
| AMD Radeon RX 6800 XT (Vulkan, `-dev 0`) | ~0.5 s (first request after start ~1.4 s), model start ~2.5 s, ~3.1 GB VRAM | comfortable |
| CPU only (`-ng`) | noticeably slower | usable as a fallback |
| Integrated AMD Radeon Graphics (Vulkan, `-dev 1`) | much slower than CPU | not usable |

## Notes

- Only the bundled whisper-server is started; if its port is taken by another process, an error is shown.
- On a hotkey press the servers start while you speak, so a cold start mostly overlaps with speaking.
- Pasting into elevated windows is blocked by Windows (UIPI) unless WhisperLocal runs as administrator too
  (a warning is logged).
