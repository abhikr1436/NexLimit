<p align="center"><img src="res/nexlimit-256.png" width="96" alt="NexLimit logo"></p>
<h1 align="center">NexLimit</h1>
<p align="center">A free, small per-app bandwidth limiter, firewall and data quota for Windows 10/11.</p>

## Features
- **Activity view.** A tree with your PC on top, then LocalNetwork, Internet and every app. Expand an app to see each of its
  connections with its own live speed. Rate cells light up while data flows. Columns: Download, Upload (live speed), Total Download,
  Total Upload (everything each app has used since NexLimit started counting, updated live), Rule Status, Today. Sort by
  rate, name, download, upload, today or total, filter All / Online / Offline / Hidden, and search.
- **Rule Status column.** Six slots per app: block In / Out, limit In / Out, quota In / Out. Click a slot to set or change it.
- **Info View.** The selected item's rules grid (Blocker / Limit / Quota × In / Out), tools (traffic stats, delete rules,
  kill connections, hide, open file location) and a live 6-minute download/upload chart.
- **Per-app rules.** Block downloads and/or uploads, speed limits per direction, and daily or monthly data quotas per
  direction (the app is blocked in that direction when its quota is used up; reset any time).
- **Whole-PC rules.** Total speed limit, block all, and a daily/monthly data quota that warns at 90% and blocks the
  internet at 100% (allow for today / reset quota). LAN keeps working.
- **Master switches.** Blocker On, Limiter On and Quota On turn each kind of rule on or off at once. Units: Auto, B, KB, MB, bit/s.
- **Rule List, Application List, Traffic Statistics** (per-app totals plus a per-day bar chart) tabs.
- **Follows Windows dark / light mode**, including the title bar. Tray icon with live speed, start with Windows (no UAC prompt).
- **Small and fast.** ~175 KB app plus the 140 KB WinDivert driver; ~320 KB installer. LAN traffic never leaves the kernel.
  If NexLimit closes or crashes, traffic simply flows normally.

## Install
Run `NexLimit-Setup-x.y.z.exe` (needs admin; NexLimit loads a packet driver). The installer isn't code-signed yet, so
SmartScreen may say "Windows protected your PC": choose **More info → Run anyway**.

## How it works
| File | What it does |
|---|---|
| `src/Engine.cs` | WinDivert glue. Diverts all non-LAN packets in batches of 64 on 2-4 threads; listens to socket events (bind/connect/accept) to know which process owns each local port; falls back to the Windows connection tables; pumps queued packets every 1 ms. |
| `src/Shaper.cs` | The logic, no Windows calls: per-app token buckets (download and upload) chained to a whole-PC bucket, queue-then-drop shaping, block, quota with rollover, per-day usage history. |
| `src/Store.cs` | `%ProgramData%\NexLimit\settings.ini` (limits, blocks by exe path, quota). History is in `usage-YYYY-MM.tsv` next to it. |
| `src/MainForm.cs` | Window frame: top bar with master switches, tabs, status bar, tray, the once-a-second data collection. |
| `src/Pages.cs`, `src/Actions.cs` | The tab pages and the rule actions shared by slots, Info View, menus and Rule List. |
| `src/TreeGrid.cs`, `src/Controls.cs`, `src/Theme.cs` | Custom-painted Activity tree, switches, tab strip, themed lists, rules grid, charts; dark/light palette. |
| `test/Tests.cs` | Logic checks (limits hold within ~10%, quota, history, settings, table parsing). Run under Mono. |

Shaping works like NetLimiter's: a packet that would exceed an app's limit waits in a short queue (about 250 ms
of traffic); when the queue is full, packets are dropped and TCP slows itself down to the limit.

## Build
On Linux with `mono-devel`, `binutils-mingw-w64-x86-64` and `nsis`:
```sh
./build.sh          # fetches WinDivert 2.2.2 if needed, builds dist/NexLimit.exe, runs tests, builds installer/out/NexLimit-Setup-*.exe
```

## Credits
[WinDivert](https://github.com/basil00/WinDivert) 2.2.2 by basil00, LGPL v3 (`licenses/WinDivert-LICENSE.txt`), used unmodified.
