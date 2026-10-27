# WhisperLocal

*English: [README.md](README.md)*

Локальна диктовка для Windows і macOS у стилі Wispr Flow: затиснув шорткат, сказав, відпустив — розпізнаний
текст вставляється в активне вікно. Усе працює локально й портативно на відеокарті (вбудований
whisper.cpp зі збіркою під Vulkan); нічого не встановлюється й не оновлюється саме.

```
Шорткат (WH_KEYBOARD_LL) → запис з мікрофона (WASAPI) → 16 kHz моно
  → whisper-server (дочірній процес із проєкту, HTTP /inference)
  → фільтр галюцинацій + словник замін (regex)
  → необов'язкове редагування LLM (за замовчуванням вимкнено)
  → буфер обміну + Ctrl+V, попередній вміст буфера відновлюється
```

## Розгортання на новому комп'ютері

1. Встановити [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) і свіжий драйвер відеокарти (Vulkan входить у драйвери AMD/NVIDIA/Intel).
2. Склонувати й зібрати:
   ```
   git clone <repo-url> WhisperLocal
   cd WhisperLocal
   dotnet build WhisperLocal.sln -c Release
   ```
3. Скачати модель (~3 GB, з перевіркою SHA256) у папку `models\` поруч з exe:
   ```
   powershell -ExecutionPolicy Bypass -File scripts\get-model.ps1 -Configuration Release
   ```
   Без моделі програма покаже вікно «model not found» з точною папкою й посиланням для скачування.
4. Запустити `WhisperLocal\bin\Release\net8.0-windows\WhisperLocal.exe`.
5. Якщо відеокарт кілька, подивитися список Vulkan-пристроїв на початку `logs\whisper-server.log` поруч з exe
   і вказати потрібний номер `-dev N` у `Whisper.ExtraArgs`.

Налаштування приходять разом з репозиторієм (`WhisperLocal\config.json`), копіювати нічого не треба.
Бінарники whisper.cpp лежать у репозиторії (`WhisperLocal\Runtime\whisper`, опис і контрольні суми у `VERSION.md`).
Збірка під Vulkan не прив'язана до виробника відеокарти, тож під іншу карту перезбирати нічого не треба.

## macOS

Той самий конвеєр на рідних механізмах macOS: шорткат через CGEventTap → запис через AudioQueue (16 kHz моно)
→ whisper-server (Metal) → фільтри → буфер обміну + Cmd+V. Програма живе в рядку меню (без іконки в Dock);
Apple Silicon або Intel, macOS 12+.

1. Встановити [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Xcode Command Line Tools
   (`xcode-select --install`) і CMake (`brew install cmake`).
2. Зібрати whisper-server з Metal і саму програму:
   ```
   scripts/build-mac.sh --install        # → artifacts/WhisperLocal.app, копіюється в ~/Applications
   ```
   Перший запуск збирає whisper.cpp (зафіксований тег, див. скрипт) у `WhisperLocal.Mac/Runtime/whisper` (не в git).
   Якщо вже стоїть `brew install whisper-cpp`, можна вказати `"whisper-server"` у `Whisper.ServerExe` — він знайдеться в PATH.
3. Скачати модель у `~/Library/Application Support/WhisperLocal/models`:
   ```
   scripts/get-model.sh
   ```
   На базових чипах M-серії і Mac з 8 GB помітно швидша `ggml-large-v3-turbo.bin`.
4. Запустити WhisperLocal з ~/Applications і дати дозволи, які вона попросить:
   - **Універсальний доступ** (Системні параметри → Приватність і безпека → Універсальний доступ): глобальний
     шорткат і вставка через Cmd+V. Шорткат запрацює одразу після дозволу, перезапуск не потрібен.
   - **Мікрофон**: запит з'явиться при першій диктовці.
5. Затиснути **лівий Control + лівий Command**, сказати, відпустити.

Особливості macOS:

- Конфіг, логи й моделі лежать у `~/Library/Application Support/WhisperLocal` (пункт меню «Show data folder»),
  а не всередині програми. `WhisperLocal.Mac/config.json` копіюється туди лише при першому запуску; далі
  редагується встановлена копія («Open config», перечитується на льоту). Щоб повернути налаштування з проєкту — видалити її.
- Назви клавіш: `LeftControl`, `RightControl`, `LeftOption`, `RightOption`, `LeftCommand`, `RightCommand`,
  `LeftShift`, `RightShift`, `Fn`, `F1`…`F20`; віндовські назви (`LControlKey+LWin`, `RMenu`…) відповідають
  клавішам на тому ж місці. Для `Fn` треба в Системні параметри → Клавіатура → «Натискання клавіші 🌐» вибрати «Нічого не робити».
- `StartWithWindows` тут означає «запускати при вході»: LaunchAgent у `~/Library/LaunchAgents`.
- Збірка підписується ad hoc, тож після кожної перезбірки macOS вважає її новою програмою і перемикач
  Універсального доступу перестає діяти: прибрати WhisperLocal зі списку кнопкою «–» і додати знову. Збірка з
  `CODESIGN_IDENTITY="<назва сертифіката>" scripts/build-mac.sh` (наприклад, самопідписаний сертифікат для
  підпису коду з «Доступу до в'язки ключів») зберігає дозвіл між збірками.
- Сповіщення показуються через `osascript`, тому відправником у них значиться Script Editor.
- Для розробки підходить і `dotnet run --project WhisperLocal.Mac`; тоді дозволи запитуються для програми терміналу.

## Структура

```
WhisperLocal\                       репозиторій
  scripts\get-model.ps1             скачує й перевіряє модель Whisper
  scripts/build-mac.sh, get-model.sh  macOS: збирає WhisperLocal.app / скачує модель
  WhisperLocal.Core\                спільне: конфіг, конвеєр, клієнт whisper-server, фільтри, LLM, batch
  WhisperLocal.Mac\                 програма для рядка меню macOS (AppKit/CoreGraphics/AudioToolbox), свій config.json
  WhisperLocal\                     програма для трею Windows
    config.json                     налаштування (копіюються в bin при збірці)
    Runtime\whisper\                whisper-server.exe + DLL (Vulkan)

WhisperLocal\WhisperLocal\bin\<Configuration>\net8.0-windows\     поруч з exe
  WhisperLocal.exe
  config.json                       копія, яку читає програма
  whisper\                          скопійовано з Runtime\whisper
  models\ggml-large-v3.bin          скачується окремо, не в git
  logs\
```

Відносні шляхи в конфігу (`Whisper.ServerExe`, `Whisper.ModelPath`) рахуються від папки з exe
(на macOS `ModelPath` — від папки з даними).

## Конфіг

Головний конфіг — `WhisperLocal\config.json` у проєкті, він є в git. При збірці він копіюється поруч з exe
(лише коли файл у проєкті новіший), і програма читає саме цю копію. Коментарі `//` дозволені (VS Code
підкреслює їх як помилку — на роботу програми це не впливає), зміни підхоплюються одразу після збереження.
Редагуй файл у проєкті й перезбирай. Правки в копії поруч з exe (наприклад, через «Open config») діють одразу,
але не потрапляють у git і будуть перезаписані, щойно файл у проєкті зміниться.
Якщо файлу немає, програма створить його з типовими значеннями. Логи — у папці `logs\` поруч з exe
(`whisper-server.log` — вивід сервера).

Основні налаштування:

- `StartWithWindows` — автозапуск разом з Windows (запис у реєстрі користувача на той exe, який застосував налаштування).
- `Hotkey.Key` — назви клавіш через `+`: `LControlKey+LWin` (за замовчуванням), `LMenu+LWin`, `RMenu`, `F13`…
- `Audio.DeviceName` — частина назви мікрофона; порожньо = мікрофон Windows за замовчуванням.
- `Whisper.ExtraArgs` — за замовчуванням `-dev 0 -sns -t 8` (номер відеокарти, приглушення не-мовних токенів, потоки); `-bs 5` вмикає beam search (трохи точніше, трохи повільніше).
- `Memory.IdleUnloadMinutes` — через скільки хвилин без диктовки все вивантажується з пам'яті (за замовчуванням 5).

## Англійські терміни в українській мові

Два механізми, обидва в конфігу:

1. **`Whisper.InitialPrompt`** — текст, який Whisper сприймає як «попередню розмову», тому терміни
   з нього він схильний писати так само. Це підказка, а не гарантія. Список має бути коротким (довгий
   розмиває ефект), і в ньому не повинно бути слів, яких ти не вживаєш — вони можуть «пролазити» в текст.
2. **`Dictionary`** — заміни після розпізнавання, по черзі зверху вниз, без урахування регістру, цілими словами.
   `|` розділяє варіанти, `\w*` ловить українські закінчення (`джейсон\w*` → `JSON`, тож «в Джейсоні» стане «в JSON»).

Як поповнювати: дивишся в лозі рядки `raw:`, знаходиш, як саме програма почула термін, додаєш запис у словник.
Конфіг підхопиться сам.

## Меню в треї

- **Enabled** — увімкнути/вимкнути диктовку.
- **Unload from memory** — зупинити всі сервери й вивантажити моделі з пам'яті та відеопам'яті (неактивний, якщо нічого не завантажено); наступне натискання шорткату все піднімає знову. Відбувається автоматично після `Memory.IdleUnloadMinutes` без диктовки.
- Open config / Open log, Exit.

## Пакетна перевірка

```
WhisperLocal.exe --batch D:\Dictations [--out results.csv] [--config path\config.json]
```

Проганяє всі wav/mp3/m4a/flac з папки через ту саму обробку і пише CSV (час, сирий текст, після словника,
фінальний) плюс читабельний `.txt` поруч.

На macOS: `~/Applications/WhisperLocal.app/Contents/MacOS/WhisperLocal --batch ~/Dictations` (файли декодуються через `afconvert`).

## Виміряна швидкість

Whisper large-v3, фрази по 10–15 с:

| Пристрій | Розпізнавання | Висновок |
|---|---|---|
| AMD Radeon RX 6800 XT (Vulkan, `-dev 0`) | ~0.5 с (перший запит після старту ~1.4 с), старт моделі ~2.5 с, ~3.1 GB відеопам'яті | комфортно |
| Лише процесор (`-ng`) | помітно повільніше | годиться як запасний варіант |
| Вбудована AMD Radeon Graphics (Vulkan, `-dev 1`) | значно повільніше за процесор | працювати неможливо |

## Нотатки

- Перед перезбиранням закрий WhisperLocal (Exit у треї): запущений exe заблокований.
- Необов'язковий етап редагування через LLM звертається до Ollama HTTP API, яка в проєкт не входить; за замовчуванням вимкнено (`Llm.Enabled`).
- Запускається тільки whisper-server з проєкту; якщо його порт зайнятий іншим процесом, буде помилка.
- У вікна, запущені від адміністратора, вставка блокується Windows (UIPI), якщо WhisperLocal не запущено теж від адміністратора (у лог пишеться попередження).
- При натисканні шорткату сервер стартує, поки ти говориш, тож холодний старт здебільшого непомітний.
