# ADHDTimer

**English** | [한국어](README_ko.md)

A tiny always-on-top **task queue + focus timer** widget for Windows and macOS.
It sits in the top-right corner of the screen, shows what you're working on and what has been waiting the longest, and walks you through focus/break cycles.

![Demo: tasks follow the window you're in, a focus session with the 1-minute grace card, the break screen, and the stats window](docs/demo.gif)

<sub>Demo with sample data on Windows. The focus run is the 10-second test mode (Ctrl+click ▶ Start).</sub>

## Install

| | Download |
|---|---|
| **Windows 10/11** (x64) | **[FocusQueue-Setup-x64.exe](https://github.com/junwha/ADHDTimer/releases/latest/download/FocusQueue-Setup-x64.exe)** |
| **macOS 12+** (Apple Silicon & Intel) | **[FocusQueue-macOS.dmg](https://github.com/junwha/ADHDTimer/releases/latest/download/FocusQueue-macOS.dmg)** |

**Windows**
1. Run `FocusQueue-Setup-x64.exe`. If SmartScreen warns about an unknown app: **More info → Run anyway**.
2. Click through the installer (no admin rights needed). Tick *Start FocusQueue when Windows starts* if you want it at every login.
3. FocusQueue starts in the top-right corner. Uninstall it from **Settings → Apps** like any other app.

**macOS**
1. Open `FocusQueue-macOS.dmg` and drag **FocusQueue** onto **Applications**.
2. The first time, **right-click FocusQueue in Applications → Open** (the app isn't notarized).
3. Allow FocusQueue in **System Settings → Privacy & Security → Accessibility** so tasks can follow individual windows (without it, they follow whole apps).

Your data never leaves your computer (see [Data](#data)).

## How to use

1. **Add a task** – in the window you're working in, type into the dashed card at the bottom of the widget (`+ task for this window`) and press Enter. The task is now linked to that window. `Ctrl+Alt+Space` (macOS: `Ctrl+Option+Space`) jumps to the card from anywhere.
2. **Work** – whenever you're in a task's window, it shows **● working**. Tasks you haven't touched slowly turn red.
3. **Switch** – click a task to bring its windows back. **Shift+click** (or Del) marks it done. Drag to reorder; drop a task **onto** another one to group them.
4. **Focus** – press **▶ Start**. Each task goes through rounds: 15 → 30 → 60 minutes (`<` `>` to change). Stay in the task's windows; if you leave, you get **1 minute** to come back.
5. **Break** – when the time is up, the screen goes dark for a 3–15 minute break. Take it, or choose **+5 min focus** / **Rest in background** (a reminder pops up 10 seconds before the end).
6. **Look back** – right-click → **Stats** for your focus time per day and per task. Right-click also has **Language** (English / 한국어), **Opacity** and **Quit**.

## Features

### Task queue
- Tasks are linked to the **window you're in** (and windows snapped beside it); while you're there, the task shows as *working*.
- Tasks you haven't touched in a while slowly turn red.
- Drag to reorder, adjustable opacity.
- Drop a task onto another task to make a **group** (a rounded section). Moving between the windows of a group counts as the same task.
- Click the current task again to rename it; click a section header to rename the group.

### Focus timer
- Each task has rounds: **round 1: 15 min focus / 3 min break → round 2: 30 / 5 → round 3+: 60 / 15**. Use `<` `>` to change the round.
- While focusing, other tasks don't age.
- Switching to an unrelated window shows a centered **1-minute grace timer**; come back within a minute and the session continues.
- When a session ends, a **full-screen break** covers the screen.
  - `+5 min focus`: keep going on the same task for 5 more minutes (the bar keeps the time already focused), then the break comes back.
  - `Rest in background`: hide the break screen; a centered alert appears **10 seconds** before the break ends.
  - In an emergency, double-click the text at the bottom to skip the break.

### Log and stats
- Every focus session is appended to `focus_log.csv` (see below).
- Right-click → **Stats**: total focus, sessions, completion rate, focus per day / per hour, focus by task, and recent sessions, for today / 7 days / 30 days / all time.
- `PQ.exe --stats` (Windows) or `FocusQueue.app/Contents/MacOS/FocusQueue --stats` (macOS) opens the stats window directly.

## How it's built

```
core/      pq_core.c/.h   shared C core: queue, groups, files, focus/break rules, log, stats, all UI strings
windows/   PQ.cs, Core.cs Windows frontend (C# WinForms): window tracking, drawing, input
mac/       Sources/*.swift macOS frontend (Swift/AppKit): window tracking, drawing, input
```

Both apps are thin frontends over the same core, so a rule or a string changes in one place. The frontends only do what is platform-specific: follow the window in front, draw, and handle input.

## Build from source

**Windows** – nothing to install:
```bat
windows\build.bat
```
The C# compiler ships with Windows. The script downloads the prebuilt `pq_core.dll` matching your checkout from the [core release](https://github.com/junwha/ADHDTimer/releases/tag/core) (CI publishes it for every commit on `main`) and produces `windows\PQ.exe`. If you build the core yourself (`cl /utf-8 /LD core\pq_core.c core\pq_strings.c`), put the DLL in `windows\` and run `windows\build.bat --local`.

**macOS** – needs the Swift toolchain (Xcode, or `xcode-select --install`):
```bash
mac/build.sh          # builds mac/build/FocusQueue.app (core compiled and linked in)
mac/build.sh --run    # also restarts a running widget (your queue is backed up first)
```

**Core tests** – plain C, any compiler:
```bash
cc -std=c11 -Wall -Wextra -o test_core core/tests/test_core.c core/pq_core.c core/pq_strings.c -lm
./test_core "$(mktemp -d)"
```
On Windows: `cl /utf-8 core\tests\test_core.c core\pq_core.c core\pq_strings.c` and run `test_core.exe` with an empty folder. CI runs them on both platforms.

On macOS, `Option+click` on **▶ Start** runs a 10-second test session (`Ctrl+click` on Windows), `Ctrl+click` opens the right-click menu, `Cmd+click` only selects, and `Delete` marks the selected task done. The build is ad-hoc signed, so you may need to allow Accessibility again after rebuilding.

## Data

All data stays on your machine, in `%APPDATA%\ADHD-PQ\` (Windows) or `~/Library/Application Support/ADHD-PQ/` (macOS). Nothing is sent anywhere.
On Windows, `PQ.exe --data <folder>` runs a separate instance that keeps its data in another folder (handy for a portable copy or a demo).

| File | Contents |
|---|---|
| `items.tsv` | the task queue (linked windows, rounds, groups) |
| `groups.tsv` | group names |
| `focus_log.csv` | focus log (UTF-8 with BOM, opens in Excel) |
| `opacity.txt`, `lang.txt` | settings |

`focus_log.csv` columns:

| column | meaning |
|---|---|
| `start`, `end` | `yyyy-MM-dd HH:mm:ss`, local time |
| `minutes` | focused minutes, excluding time spent away |
| `task`, `group` | task and group name |
| `round` | 1, 2, 3 |
| `planned_min` | planned length of the segment |
| `kind` | `focus` or `extension` (+5 min) |
| `outcome` | `done`, `stopped`, `left` (didn't come back within 1 min), `quit` (widget closed) |

Segments shorter than 30 seconds are not logged. Each launch copies `items.tsv` to `items.bak.tsv` before anything is written. On macOS the window column of `items.tsv` holds a `CGWindowID`, or a negative per-app id when windows are tracked per app (no Accessibility permission), and the process column holds the app's bundle identifier.
