# ADHDTimer

**English** | [한국어](README_ko.md)

A tiny always-on-top **task queue + focus timer** widget.
It sits in the top-right corner of the screen, shows what you're working on and what has been waiting the longest, and walks you through focus/break cycles.

![Demo: tasks follow the window you're in, a focus session with the 1-minute grace card, the break screen, and the stats window](docs/demo.gif)

<sub>Demo with sample data. The focus run is the 10-second test mode (Ctrl+click ▶ Start).</sub>

> Currently **Windows** only (`PQ.cs`, a single-file C# WinForms app). A macOS version is in progress.

The UI is available in **English** and **Korean**. It follows the system display language by default; switch any time from the right-click menu → **Language**.

## Features

### Task queue
- Type a task in the input card and it gets linked to the **window you're in**. While you're in that window the task shows as *working*.
- Tasks you haven't touched in a while slowly turn red.
- Notes, drag to reorder, adjustable opacity.
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
- `PQ.exe --stats` opens the stats window directly.

## Build (Windows)

Only needs `csc.exe`, which ships with .NET Framework 4. Nothing to install.

```bat
build.bat
```

This produces `PQ.exe`. Hotkey: `Ctrl+Alt+Space`. Launching it again while it's running brings the widget back on screen.

## Data

All data stays on your machine, in `%APPDATA%\ADHD-PQ\`. Nothing is sent anywhere.
`PQ.exe --data <folder>` runs a separate instance that keeps its data in another folder (handy for a portable copy or a demo).

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

Segments shorter than 30 seconds are not logged.
