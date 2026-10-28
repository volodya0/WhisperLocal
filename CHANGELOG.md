# Changelog

## Unreleased

- Main window takes less space: one-line header, tighter setup list, hotkey and language in one row, speech model
  choice and memory in one row; descriptions moved into tooltips (messages like "Saved" show briefly); the same space
  around the cards as between them; the last 10 dictations; smaller buttons.
- The success summary on the indicator stays 3 s instead of 6 (`Ui.ResultSeconds`; existing configs keep their value).
- macOS: the recording indicator showed no more over full-screen apps once the main window had been opened (until the
  app was quit): the app became a regular Dock app while the window was open. It now stays a menu bar app. The
  indicator (and the live hints) appear on the screen with the focused window instead of the one with the mouse, and
  the log says when macOS keeps them hidden.
- Speech model choice in the main window: Large v3 or Large v3 Turbo (faster, about half the size); a model that is
  not there yet is downloaded from the setup checklist.
- Live hints (macOS, off by default): while the hotkey is held, a local LLM shows the next words of the sentence in
  the language being spoken, refreshed about every second ("Always", dim, highlighted on a pause) or only on a pause
  ("When I pause"); for English, a correction line points out a mistake in the last phrase. The audio so far is
  transcribed as you speak; the hints come from a bundled llama.cpp `llama-server` (Metal) built by
  `scripts/build-mac.sh`. The GGUF model (Gemma 3 4B / 12B, Qwen 3 4B) is chosen and downloaded in the main window,
  no Ollama needed. Settings in the new `Suggest` config section, which the main window adds to older configs.
- macOS version (`WhisperLocal.Mac`): menu bar app with a CGEventTap push-to-talk hotkey, AudioQueue capture,
  whisper-server built with Metal, pasteboard + Cmd+V insertion, LaunchAgent autostart and `--batch` via `afconvert`.
  `scripts/build-mac.sh` builds `WhisperLocal.app`, `scripts/get-model.sh` downloads the model.
- macOS: manual "Check for Updates…" from GitHub releases (checksum and signer verified), version from the release
  tag, CI signing with a stable certificate; app and menu bar icons from the Windows tray icon design.
- Shared cross-platform windows (`WhisperLocal.Ui`, Avalonia): main window with setup checklist, hotkey selector
  (presets and recording), model memory with Unload/Load, file buttons and recent dictations with timing statistics;
  transcript window with Copy. Used by the macOS app; the Windows app can adopt it next.
- `Whisper.Language` accepts a list (`"uk,en"`, the new macOS default): the language is detected per dictation and
  only listed ones are accepted, so English speech no longer comes out in Ukrainian; selector in the main window.
- macOS hotkeys can include character keys (e.g. Option + /); that key is swallowed while the combination is held.
- Dark redesign of the shared windows (cards, status dots, statistics tiles); the success summary on the indicator
  stays 6 s (`Ui.ResultSeconds`); the history keeps the last 20 dictations.
- Lifetime statistics (today / 7 days / all time): dictations, words, speaking time, words per minute.
- macOS indicator: live seconds while recording/processing, then a summary (times, words) with a Text button.
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
