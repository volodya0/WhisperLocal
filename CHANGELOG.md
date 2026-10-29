# Changelog

## Unreleased

- Groq as a recognition engine: paste a Groq API key on Models → Cloud (free from the Groq console, **Get a Key**,
  `Cloud.GroqApiKey`) and *Whisper Large v3 Turbo* and *Whisper Large v3* on Groq appear in Speech to Text →
  Recognition → Model (`Recognition.Engine` `"Groq"`, `Cloud.GroqModel`). The same Whisper as on this computer, in
  well under a second and without a GPU; `Whisper.Language`, `Whisper.InitialPrompt` and `Whisper.Temperature` apply,
  and a failed request is recognised locally like with Gemini (`Recognition.FallbackToLocal`).
- Listening panel: resizable by its edges and corners, and the lines scroll: the whole session (its last 300 lines)
  instead of the last few, following the newest line unless you scrolled up to read. The panel keeps its size and
  place for the next session.
- Models → Memory lists the models loaded right now (speech and language model, what uses each and how much memory)
  with one **Unload All** button and the auto-unload time; the separate Load/Unload of the speech model and the
  language model's Unload are gone.
- Speech to Text → Recognition has one **Model** list instead of Engine, Gemini model and Local model: the downloaded
  local models and, with a Gemini key, the Gemini models; choosing one sets the engine too. The Gemini fallback uses
  the last local model chosen. Notes & Translate → Listening says which speech model it hears with (the same local
  model).
- Models is for managing models only: Download, Cancel and Delete (the model in use cannot be deleted, a first
  click asks), which feature uses each one, memory, the folder and cloud keys. The Use buttons are gone: models are
  chosen on the page of the feature that uses them (Speech to Text, Notes & Translate → Listening).
- macOS 14.2+: listening, notes from what the Mac plays. Left Control + Left Option + L (`Listen.Hotkey`) starts
  transcribing system audio (a call, a video) into a note with times, in the notes folder, so it shows on the Notes
  page. A panel above the indicator shows the last sentences and, greyed, the one still being spoken
  (`Listen.ProvisionalMs`); its menu translates them with the local language model, the translation next to each
  sentence, and translated lines are written in both languages. It runs until the panel's stop button is clicked.
  Notes & Translate → Listening has the shortcut and the languages; the menu has "Listen to System Audio".
- 1.0.30 quit right after starting on macOS (the new main window asked the app for its setup checklist while the
  app was still starting); fixed.
- Main window redesign: a sidebar with the pages (Home, History, Notes) and the settings (Speech to Text, Notes &
  Translate, Models), with the status always at the bottom. Home has the status and setup checklist, updates and
  files. History and statistics are one page instead of two windows, and
  statistics have a 30-day period. Speech to Text has the hotkey, language, speech model and post-processing (grammar
  check, correction rules). Models lists every speech and language model with its state and Download, Cancel and
  Use, the speech model's memory, the idle unload time (`Memory.IdleUnloadMinutes`) as a dropdown, and the models folder.
- Notes: a page listing the `.md` and `.txt` files of a notes folder (`Notes.Folder`, by default
  `WhisperLocal Notes` in Documents), newest first with their first line; New Note creates one (`Notes.Extension`)
  and opens it in the text editor. The folder and file type are set on the Notes & Translate page.
- Models → Cloud: paste a Google Gemini API key (Get a Key opens Google AI Studio), Test it, Remove it. The key is
  kept in `config.json` (`Cloud.GeminiApiKey`).
- Gemini as a recognition engine (Speech to Text → Recognition → Engine, `Recognition.Engine`): the recording goes
  to Google's Gemini API (`Cloud.GeminiModel`, `gemini-flash-latest` by default, or Flash-Lite) with an instruction
  built from the language setting and the terms of `Whisper.InitialPrompt`; the correction rules and filters still
  apply. When Gemini fails (no internet, limit used up, `Cloud.TimeoutSec`), the recording is recognised locally
  (`Recognition.FallbackToLocal`). With Gemini the local model is not loaded in advance, and a missing local model
  is only a warning.
- Windows: WhisperLocal.exe has the waveform app icon (Explorer, taskbar, Alt+Tab, Start) instead of the default one.
- Main window: a GitHub button next to Check for Updates opens the project's page.
- Windows: the shared main window (setup checklist with model download, hotkey selector with Record…, language,
  speech model and memory, recent dictations), history, statistics and transcript windows; "Open WhisperLocal…" and
  "Show Last Transcript" in the tray menu (double-click the icon for the main window). Dictations are kept in the
  history and statistics like on macOS. A missing model opens the main window instead of a dialog.
- Windows CI build (GitHub Actions): builds the app, runs a self-test and keeps a zip of the app folder.
- Windows: the windows have the app icon instead of the default one; the setup line names the microphone that is
  actually used; the default language is Ukrainian + English (`"uk,en"`), as on macOS.
- Windows works like macOS: the indicator counts seconds while recording and processing, then shows a summary
  (times, words) with a Text button; the tray menu has the same items as the macOS menu; dictating on the desktop or the
  taskbar opens the transcript instead of pasting (`Insert.ShowWhenNoTextField`).
- Windows updates: every release has a Windows zip next to the macOS DMG, and "Check for Updates…" on Windows
  downloads it, checks its checksum and, once the app has quit, copies it over the app folder (keeping
  `config.json`, models and history) and starts the new version. One CI workflow builds both apps, on every branch.
- Smaller Windows download: the app is built for Windows x64 only, so the UI libraries for other platforms
  (Linux, macOS, x86, ARM64) are no longer in the folder; no debug files, no whisper-bench; zipped with 7-Zip's
  strongest zip compression. CI prints the app folder's largest files.
- Windows: Large v3 Turbo is the default speech model (1.6 GB, several times faster than Large v3, almost as
  accurate); `scripts/get-model.ps1` downloads it by default. Existing installs keep their model: updates do not
  replace `config.json`, and Large v3 stays one click away in the main window.
- History: the last 1000 dictations are kept instead of 20; the history window shows 100 at a time with a
  "Show more" button. The file is appended to instead of rewritten on every dictation.
- macOS: the recording indicator could stop showing anywhere (not only over full-screen apps) after the app had run
  for a long time, until it was restarted. Every dictation now gets a freshly created indicator panel, and the log
  says more when macOS keeps it hidden.
- Main window: changing a setting no longer shows a "Saved…" line with a description under it; errors and the hotkey
  recording prompt still show there.
- Autocomplete is shelved while it is too raw: it is gone from the main window, the default config and the docs, and
  stays off even if an older config still has `Autocomplete.Mode` set (the code is kept for when it comes back).
- Main window shows only the last 3 dictations; the full history (grouped by day, with averages) and the statistics
  open in their own windows from the All history and Statistics buttons.
- macOS: when no text input is focused (a file list, the desktop), the dictation is not pasted into nothing: the
  transcript window opens with the text to edit and copy (`Insert.ShowWhenNoTextField`, on by default).
- Main window takes less space: one-line header, tighter setup list, hotkey and language in one row, speech model
  choice and memory in one row; descriptions moved into tooltips (messages like "Saved" show briefly); the same space
  around the cards as between them; the last 10 dictations; smaller buttons.
- The success summary on the indicator stays 3 s instead of 6 (`Ui.ResultSeconds`; existing configs keep their value).
- macOS: the recording indicator showed no more over full-screen apps once the main window had been opened (until the
  app was quit): the app became a regular Dock app while the window was open. It now stays a menu bar app. The
  indicator (and the autocomplete panel) appear on the screen with the focused window instead of the one with the mouse, and
  the log says when macOS keeps them hidden.
- Speech model choice in the main window: Large v3 or Large v3 Turbo (faster, about half the size); a model that is
  not there yet is downloaded from the setup checklist.
- Autocomplete (macOS, off by default), like code completion: while the hotkey is held, the panel above the
  indicator shows the last words and, in grey, how the sentence most likely continues; refreshed about every second
  ("Always", dim, highlighted on a pause) or only on a pause ("When I pause"). The audio so far is transcribed as you
  speak; a bundled llama.cpp `llama-server` (Metal, built by `scripts/build-mac.sh`) continues it as plain text.
- Grammar check (macOS, off by default): after a dictation, the same model proposes a polished version in a panel,
  changed words in green, with icon buttons to insert it (undo + paste) or copy it.
- The GGUF model (Gemma 3 4B / 12B, Qwen 3 4B) is chosen and downloaded in the main window, no Ollama needed.
  Settings in the new `LocalLlm`, `Autocomplete` and `Polish` config sections, which the main window adds to older configs.
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
