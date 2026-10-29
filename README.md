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

1. Download `WhisperLocal-windows-x64.zip` from [Releases](https://github.com/volodya0/WhisperLocal/releases/latest).
2. Unpack it into a folder of your own, for example `Documents\WhisperLocal` (not Program Files: the app keeps its
   settings, models and updates there).
3. Start `WhisperLocal.exe`. If Windows SmartScreen warns about an unknown app, click **More info** → **Run anyway**.

It needs a GPU with a current driver (AMD, NVIDIA or Intel) and the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). WhisperLocal lives in the tray (the
waveform icon by the clock).

## First launch

The main window opens with a short setup checklist. Fix each line with the button next to it:

- **Accessibility** (macOS) — needed for the hotkey and for pasting. Turn WhisperLocal on in System Settings →
  Privacy & Security → Accessibility; the hotkey works as soon as it is allowed.
- **Microphone** — on macOS, allow it when asked.
- **Model** — press **Download**. This happens once: 1.6 GB on Windows, where the faster Large v3 Turbo is the
  default, 3.1 GB for Large v3 on macOS.

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
On Windows, double-click the tray icon.

The sidebar on the left has the pages, with the settings below them and the status (Ready, or what needs
attention) at the bottom.

- **Home** — the status with the setup checklist, **Check for Updates**, and buttons that open the config file, the
  log or the data folder.
- **History** — statistics (dictations, words, speaking time, words per minute) for today, 7 days, 30 days or all
  time, and every kept dictation grouped by day.
- **Notes** — the text files in your notes folder, newest first. **New Note** creates one and opens it in your text
  editor; any `.md` or `.txt` file you put in the folder shows up too.
- **Speech to Text**
  - **Hotkey** — pick a preset, or press **Record…** and hold any combination you like.
  - **Language** — Ukrainian + English (detected for each dictation) is the default. Other choices: add Russian,
    Ukrainian only, English only, or any language. With a single language, speech in another language comes out
    translated into it.
  - **Model** — one list of the models ready to use: the ones downloaded on the Models page (*Large v3* is the most
    accurate; *Large v3 Turbo* is several times faster and almost as accurate, the better choice for computers with
    8–16 GB of memory, and the default on Windows) and, once a Gemini key is added there, *Gemini Flash* and the faster
    *Gemini Flash-Lite*: often more accurate with mixed Ukrainian and English, but the recording is sent to Google;
    once a Groq key is added, *Whisper Large v3 Turbo* and *Whisper Large v3* on Groq: the same Whisper, in well under
    a second without a GPU, but the recording is sent to Groq.
    With a cloud model you can choose whether a failed request (no internet, limit used up) is recognised on this computer,
    with the last local model you chose. Listening uses that same local model.
  - **Post-processing** — the grammar check (see below) and the correction rules for words Whisper mishears.
- **Notes & Translate** — which folder holds the notes, and whether new ones are Markdown or plain text; on macOS,
  **Listening** (see below): its shortcut, the language heard, the translation language and the translation model;
  **Line speed** (Fast, Balanced or Accurate: how soon a line ends) and **Translate while speaking** (translate the
  sentence still being spoken, every 2 s, every second or as fast as possible).
- **Models** — managing models, not choosing them (that happens on the page of the feature that uses one): every
  speech and language model, downloaded or not and which feature uses it, with **Download** and **Delete** (the model
  in use cannot be deleted); **Memory**, the models loaded right now and how much memory each uses, with **Unload All**
  and after how long without use they unload by themselves (5 minutes by default); the folder the models are
  downloaded to; and **Cloud**, where
  you can paste a Google Gemini API key (free from Google AI Studio, **Get a Key**) or a Groq API key (free from the
  Groq console) and **Test** it; that provider's models then appear in Speech to Text → Model and, on macOS, in Notes &
  Translate → Translation model. **Usage** opens the provider's page with the requests and limits you have used. Under each connected provider,
  a line shows what WhisperLocal itself sent today (recordings, translations, tokens), the last minute, how much of
  Groq's limits is left, and a limit reached recently; it turns orange when the provider is near its limit, and then
  live translation of unfinished phrases pauses so finished lines keep getting through.

Everything else (start at login, which microphone, English terms that get misheard…) is in the config file: see
[docs/CONFIGURATION.md](docs/CONFIGURATION.md). Changes apply as soon as you save it.

## Grammar check (macOS, optional)

Turn it on in Speech to Text → Post-processing and download its model on the Models page (Gemma 3 4B, 2.5 GB, is the default). After each dictation, a
small panel proposes a corrected version with the changed words in green. Its buttons replace what was pasted with
the corrected text or copy it; or just ignore it — it goes away by itself. It runs locally too.

## Notes from what the Mac plays (macOS 14.2 or later)

For a call, a lecture or a video: press **Left Control + Left Option + L** and WhisperLocal starts writing down
everything the Mac plays. The first time, macOS asks to allow **System Audio
Recording**; if it was refused, allow WhisperLocal in System Settings → Privacy & Security → Screen & System Audio
Recording.

A panel above the indicator shows the sentences so far, and the one still being spoken greyed at the bottom; scroll
up to read earlier ones, and drag its edges to make it larger. It keeps its size and place for the next time. Its menu
translates them: **Original**, or a language such as Українська or English; the translation then stands next to each
sentence. When translated, every line is written in both languages. The translation uses the model chosen on Notes &
Translate → **Translation model**: a local language model (download one on the **Models** page first) or, once its
key is added, Gemini or Groq, which need no download but get the text of each line. With **Translate while
speaking** on, the sentence still being spoken is translated too, greyed, and the finished line replaces it. The
language you pick is remembered.

**Who said what.** With **Include me** (on by default), what you say into the microphone is written down too, as
**Me**, in green. Without headphones the microphone also hears the speakers: those lines are recognised as an echo and
left out (headphones work best when you talk over the others). With **Tell the other speakers apart** (also on, once
the 28 MB voice model is downloaded on the **Models** page), the voices the Mac plays become **Speaker 1**, **2**, …,
each in its own color, and a line is cut where another speaker starts; it works for any language. It compares how the
voices sound, so two similar voices or people talking over each other can get mixed up. Off, everyone the Mac plays
is **Them**. Both checkboxes are on the **Notes & Translate** page.

The panel stays until you click its **stop** button (pressing the shortcut again only brings it back); the folder
button opens the note. Each session is a note in your notes folder, so it is on the **Notes** page, laid out like
the panel: the time, who spoke and the text, with the translation beside it. A Markdown note is a table (columns in
Obsidian, Typora, VS Code or GitHub); a plain text note has columns that line up in a monospaced font. The shortcut,
the language that is heard and the translation language are on the **Notes & Translate** page. Listening always
recognises on this Mac, even when dictation uses a cloud model.

## Menu

The menu bar icon on macOS, the tray icon on Windows:

- **Open WhisperLocal…** — the main window.
- **Show Last Transcript** — the text of the last dictation.
- **Listen to System Audio** / **Stop Listening** (macOS) — the same as the listening shortcut and the stop button.
- **Check for Updates…** — looks for a new version and, if you agree, installs it and restarts. Your settings,
  models and history stay.
- **Enabled** — pause or resume dictation.
- **Unload from memory** — free the memory the models use; the next dictation loads them again.
- **Quit WhisperLocal**.

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
- Anything else: the log (Home → **Log**) usually says what went wrong.

## Privacy

Unless you choose a cloud model (Gemini or Groq), audio and text never leave your computer. The only network access
is downloading models from HuggingFace and, when you ask for it, checking GitHub for updates and checking a Gemini or
Groq API key with Google or Groq.
With Gemini as the recognition engine, each recording is sent to Google's Gemini API; on the free tier Google may use
it to improve its products (see Google's terms). With Groq, each recording is sent to Groq's API (see Groq's terms). Recordings are not saved (listening keeps only the text, in its note). Listening always
recognises locally, even with a cloud model, and tells voices apart on this Mac too; with Gemini or Groq as the translation model, the text of each line is
sent there to be translated. The log and the dictation history keep
the text of your dictations to help with troubleshooting; set `Logging.LogText` to `false` in the config to stop
that.

Data folder: `~/Library/Application Support/WhisperLocal` on macOS, the app folder on Windows.

## For developers

- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — building, releases, batch evaluation, measured performance.
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md) — every setting in `config.json`.
- [docs/PROJECT_STRUCTURE.md](docs/PROJECT_STRUCTURE.md) — how the code is organized, with a note for each file.
- [CHANGELOG.md](CHANGELOG.md) — what changed.
