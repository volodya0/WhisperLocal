# WhisperLocal

*Українською: [README.uk.md](README.uk.md)*

Dictation that stays on your computer. Hold a key, speak, release — the text appears in whatever app you are typing
in. Speech recognition (Whisper) runs locally on your GPU: no account, no subscription, no audio sent anywhere.
Tuned for Ukrainian with English terms, and works for English too.

## Install

### macOS (Apple Silicon, macOS 12 or later)

1. Download the latest `.dmg` from [Releases](https://github.com/volodya0/WhisperLocal/releases/latest).
2. Open it and drag **WhisperLocal** to Applications.
3. Start it. If macOS says it cannot verify the developer, open System Settings → Privacy & Security and click
   **Open Anyway**.

WhisperLocal lives in the menu bar (the waveform icon); it has no Dock icon.

### Windows

A ready-to-run download is coming. For now WhisperLocal is built from source: see
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#windows). It needs a GPU with a current driver (AMD, NVIDIA or Intel).

## First launch (macOS)

The main window opens with a short setup checklist. Fix each line with the button next to it:

- **Accessibility** — needed for the hotkey and for pasting. Turn WhisperLocal on in System Settings →
  Privacy & Security → Accessibility; the hotkey works as soon as it is allowed.
- **Microphone** — allow it when asked.
- **Model** — press **Download** (3.1 GB, or 1.6 GB for the Turbo model). This happens once.

When every line is green, the header says *Ready*.

## Dictating

1. Put the cursor where the text should go.
2. Hold **Left Control + Left Command** (Windows: **Left Ctrl + Left Win**) and speak.
3. Release. The text is pasted a moment later.

The indicator at the bottom of the screen counts the seconds while recording and processing, then shows a short
summary. Its **Text** button opens what you said, to edit and copy. If no text field was focused (the desktop, a
file list), nothing is pasted — the text opens in that window instead.

- A very short tap is ignored, so the hotkey does not fire by accident.
- Pressing another key while holding the hotkey cancels the recording.
- The first dictation after a pause takes a few seconds longer: the model loads while you speak.

## Settings

Open the main window from the menu bar icon → **Open WhisperLocal…** (or open WhisperLocal from Applications again).

- **Hotkey** — pick a preset, or press **Record…** and hold any combination you like.
- **Language** — Ukrainian + English (detected for each dictation) is the default. Other choices: add Russian,
  Ukrainian only, English only, or any language. With a single language, speech in another language comes out
  translated into it.
- **Speech model** — *Large v3* is the most accurate; *Large v3 Turbo* is several times faster and almost as
  accurate, the better choice for Macs with 8–16 GB of memory. **Unload** frees the memory right away; this also
  happens by itself after 5 minutes without dictation.
- **Grammar check** — see below.
- **Recent dictations** — the last three, with timings. **All history** and **Statistics** (words, speaking time,
  words per minute) open their own windows.
- **Files** — open the config file, the log, or the data folder.

Everything else (start at login, which microphone, English terms that get misheard…) is in the config file: see
[docs/CONFIGURATION.md](docs/CONFIGURATION.md). On Windows all settings live there for now: tray icon →
**Open config**; changes apply as soon as you save.

## Grammar check (macOS, optional)

Turn it on in the main window and download its model (Gemma 3 4B, 2.5 GB, is the default). After each dictation, a
small panel proposes a corrected version with the changed words in green. Its buttons replace what was pasted with
the corrected text or copy it; or just ignore it — it goes away by itself. It runs locally too.

## Menu

- **Open WhisperLocal…** — the main window.
- **Show Last Transcript** — the text of the last dictation.
- **Check for Updates…** — looks for a new version and, if you agree, installs it and restarts.
- **Enabled** — pause or resume dictation.
- **Unload from memory** — free the memory the models use; the next dictation loads them again.
- **Quit WhisperLocal**.

On Windows the tray menu has Enabled, Unload from memory, Open config, Open log and Exit.

## Troubleshooting

- **The hotkey stopped working after an update.** macOS sometimes forgets the permission for a new version: in
  System Settings → Privacy & Security → Accessibility, remove WhisperLocal with **–** and add it again.
- **Fn as the hotkey types emoji or switches the input language.** Set System Settings → Keyboard →
  "Press 🌐 key to" → **Do Nothing**.
- **English terms come out wrong.** Teach them in the config: see
  [English terms](docs/CONFIGURATION.md#english-terms-in-ukrainian-speech).
- **Nothing is pasted into some Windows apps.** Apps running as administrator do not accept pasting from a normal
  app; run WhisperLocal as administrator too.
- **Slow on a Windows PC with two GPUs.** It may be using the integrated one: see
  [choosing the GPU](docs/CONFIGURATION.md#choosing-the-gpu-windows).
- Anything else: the log (main window → **Log**, or tray → **Open log**) usually says what went wrong.

## Privacy

Audio and text never leave your computer. The only network access is downloading models from HuggingFace and,
when you ask for it, checking GitHub for updates. Recordings are not saved. The log and the dictation history keep
the text of your dictations to help with troubleshooting; set `Logging.LogText` to `false` in the config to stop
that.

Data folder: `~/Library/Application Support/WhisperLocal` on macOS, the app folder on Windows.

## For developers

- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — building, releases, batch evaluation, measured performance.
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md) — every setting in `config.json`.
- [docs/PROJECT_STRUCTURE.md](docs/PROJECT_STRUCTURE.md) — how the code is organized, with a note for each file.
- [CHANGELOG.md](CHANGELOG.md) — what changed.
