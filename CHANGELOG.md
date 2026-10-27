# Changelog

## Unreleased

- macOS version (`WhisperLocal.Mac`): menu bar app with a CGEventTap push-to-talk hotkey, AudioQueue capture,
  whisper-server built with Metal, pasteboard + Cmd+V insertion, LaunchAgent autostart and `--batch` via `afconvert`.
  `scripts/build-mac.sh` builds `WhisperLocal.app`, `scripts/get-model.sh` downloads the model.
- macOS setup window: permission, server and model status with fix-it buttons and an in-app model download.
- Platform-neutral code moved to `WhisperLocal.Core`, shared by the Windows and macOS apps.

## 1.0.0 — 2026-10-27

First working version.

- Push-to-talk dictation from the tray: hold the hotkey (default Left Ctrl + Left Win), speak, release — the text is pasted into the active window.
- Speech recognition by bundled whisper.cpp (`whisper-server`, Vulkan build) with Whisper large-v3, language forced to Ukrainian.
- English terms: initial prompt plus a regex dictionary in the config.
- Silence / short-press filtering and removal of typical Whisper hallucinations.
- Unload from memory (manual and after 5 minutes of inactivity); the next hotkey press loads everything again.
- Hotkey, microphone, GPU, autostart with Windows and all other settings in `config.json` (hot reload, `//` comments).
- `--batch` mode for comparing settings and models on recorded audio.
