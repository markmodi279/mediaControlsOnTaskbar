# Project context

Read this before touching anything. It records the constraints that are not visible in the
code and the workflow gotchas that otherwise cost an hour to rediscover.

For how the code is organised, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). For what the
app does, see [README.md](README.md). Sibling project: `../desktopSwitcher`, whose house style
and taskbar-docking code this follows.

## What this is

A round album-art tile docked inside the Windows 10 taskbar, with a progress ring; a click opens
a card above it with the track, transport buttons and a seek bar. It reads and drives Windows'
media session manager (SMTC), so it works with any player. Single process, single assembly, no installer, no admin rights.

## Hard constraints

Not preferences. Violating any of them breaks the build or the app.

**C# 5 only.** The compiler is the one in the box - `.NET Framework 4.8`'s `csc.exe` - because
nothing needs installing, and this machine has no .NET SDK at all. No string interpolation, no
`nameof`, no `?.`, no expression-bodied members, no `async` beyond what C# 5 has. There is no
project file and adding one defeats the design.

**Source files stay ASCII.** The compiler reads BOM-less files in the system codepage. Write
non-ASCII characters as escapes: the menu bullet is `"\u2022"`.

**`build.cmd` uses `-recurse:`.** `src/` is foldered by layer; a plain `src\*.cs` does not
descend and silently compiles nothing.

**Windows 10 for docking.** The tile becomes a child window of `Shell_TrayWnd`, which is a
Windows 10 technique. SMTC itself needs 1809+. Verified on build 19045.

**DPI awareness before any window.** `Native.EnableDpiAwareness()` runs first in `RunApp`.
Explorer is DPI-aware; a DPI-unaware child of its window is silently scaled and lands at the
wrong coordinates on a scaled display, while every log line looks right.

### Calling WinRT from C# 5 without an SDK

This is the part that was found by trial and is easy to break. SMTC is WinRT
(`Windows.Media.Control`). It is reachable with the in-box compiler, but only like this:

- Reference the **`.winmd` files** in `C:\Windows\System32\WinMetadata` (`Windows.Foundation`,
  `Windows.Media`, `Windows.Storage`) plus the `System.Runtime.dll` facade.
- **Do not reference `System.Runtime.WindowsRuntime.dll`.** It supplies `AsTask()`, but it
  wants a merged `Windows` assembly that only the Windows SDK ships; the build fails with
  CS0012. `MediaService.Await<T>` waits on `IAsyncOperation<T>.Completed` instead.
- **Do reference `System.Runtime.InteropServices.WindowsRuntime.dll`.** It supplies only
  `EventRegistrationToken`. Without it `+=` on any WinRT event is CS1545 ("not supported by
  the language"), and the error message suggests calling `add_X` by hand, which is CS0571.
  With it, plain `+=` and `-=` work.
- `Completed` fires on a thread-pool thread. `MediaService` posts everything to the UI thread.

## Building and running from WSL

Developed from WSL, targets Windows. The Windows user is **Siddh**, the WSL user is `mark` -
`%APPDATA%` and `%LOCALAPPDATA%` are under `/mnt/c/Users/Siddh/`. Confirm with
`cmd.exe /c 'echo %APPDATA%'`.

### Build

`csc.exe` resolves WSL-relative paths against the wrong UNC base (CS1504 on every file), so
convert with `wslpath -w`. A test build:

```bash
CSC=/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe
FW='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'; WM='C:\Windows\System32\WinMetadata'
OUT=$(wslpath -w /mnt/c/Users/Public/mctest.exe)
$CSC -nologo -target:exe -platform:x64 -optimize+ -out:"$OUT" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -r:"$FW\\System.Web.Extensions.dll" -r:"$FW\\System.Runtime.dll" \
  -r:"$FW\\System.Runtime.InteropServices.WindowsRuntime.dll" \
  -r:"$WM\\Windows.Foundation.winmd" -r:"$WM\\Windows.Media.winmd" -r:"$WM\\Windows.Storage.winmd" \
  -recurse:"$(wslpath -w $PWD/src)\\*.cs"
```

`-target:exe` is the console build, the one that can print selftest output; `-target:winexe` is
the shipped, windowed one. Keep this line in step with `build.cmd`'s references.

`build.cmd` itself cannot run from a UNC working directory; `pushd` maps a drive letter:

```bash
cmd.exe /c 'pushd \\wsl$\Ubuntu-24.04\home\mark\projects\my\mediacontroller && call build.cmd && popd'
```

That is the real install: it writes `%LOCALAPPDATA%\MediaController\MediaController.exe`.

### Run

An exe cannot be launched from a WSL path (`Permission denied`); copy it to
`/mnt/c/Users/Public/` first, run it there, delete it afterwards. Launch the windowed app
with `powershell.exe -NoProfile -Command 'Start-Process C:\Users\Public\x.exe'`.

To pass `MC_LOG=1` (forces the log on for one run), set it in PowerShell:
`'$env:MC_LOG="1"; Start-Process ...'`. Setting it with `cmd.exe /c 'set ...&& start'` from WSL did
not launch the app when tried (no process, no log), so use PowerShell.

Windows locks a running exe: `taskkill.exe /f /im <name>` before rebuilding over it. Never
`taskkill /im notepad.exe` and friends unprompted - it kills the user's windows too.

## Verifying a change

No unit tests, and no sensible way to add any: everything is live shell state.

**Selftests** (console build only) run against the real session manager. Start a player first.

| Command | Checks |
|---|---|
| `--now` | one snapshot of the current session: app, caption, status, position, button and seek flags |
| `--sessions` | the running session app ids |
| `--toggle` `--next` `--prev` | sends the command; exit 0 if a session accepted it |
| `--watch` | prints every state change for 30 s - the check that event hooks survive session churn |
| `--art [file.png]` | fetches the thumbnail, decodes it, prints size and accent colour, saves the PNG, then checks it reaches a snapshot |
| `--seek <seconds>` | seeks the current session and prints the position before and after (moves the user's player - say so first) |

**Looking at the screen.** [tools/](tools/README.md) has PowerShell helpers that capture the
taskbar to a PNG (read it with the image viewer) and simulate pointer and key input. That is how
clicks, the panel, buttons, the seek drag, the right-click menu, hotkeys and Explorer-restart recovery were checked. Find
the tile from a fresh capture each time; it shifts when the tray or DesktopSwitcher changes.

**Restarting Explorer** is the standard resilience test:
`taskkill.exe /f /im explorer.exe; cmd.exe /c start "" explorer.exe`. It blanks the taskbar and
DesktopSwitcher for a moment - say so before doing it to someone else's session.

**Only a person can judge:** how the panel looks and how its slide and fade feel (they cannot be
seen in a still), menu appearance, behaviour at DPIs other than the dev machine's 125%, an auto-hiding
taskbar, a second monitor.

## Where state lives

| Path | What |
|---|---|
| `%LOCALAPPDATA%\MediaController\MediaController.exe` | installed binary - `build.cmd` writes here |
| `%APPDATA%\MediaController\config.json` | settings, written on first run, hot-reloaded |
| `%APPDATA%\MediaController\log.txt` | rolling log (256 KB), only when `diagnostics` or `MC_LOG=1` |
| `HKCU\...\CurrentVersion\Run\MediaController` | run at login, toggled from the menu |

## Things learned the hard way

- **Explorer restart kills the SMTC manager.** Every call then fails with 0x800706BA ("RPC
  server unavailable") and nothing recovers the object. `MediaService` replaces the manager
  (`Reconnect`) when it sees an RPC HRESULT. Match on the **HRESULT**, not on `COMException` -
  the projection does not throw that type, and a type test never fired.
- **Sessions vanish between tracks**, notably a browser moving to the next video. Shown raw,
  the tile flashes its idle look. `Controller.Refresh` holds the last real state for 2.5 s.
- **SMTC raises 2-3 events per change.** A 100 ms one-shot timer coalesces them.
- **Position is not pushed.** SMTC gives a position and when it was valid; `Snapshot`
  extrapolates, clamped to the duration.
- **`tileSide: right` is a handshake with the sibling.** Both apps dock beside the clock and neither
  can move the other's window. With `right`, the tile names its window `MediaControllerTileRight`
  (plain `MediaControllerTile` otherwise) and DesktopSwitcher's `TaskbarHost.AnchorScreenX` anchors
  to that window's left edge. The caption, not the tile's position, is what it reads, so the two
  cannot each wait for the other. Rename it here and the strip lands under the tile. The hover button
  is off on the right: it would slide out over the strip, and both apps re-assert z-order every second.
- **DesktopSwitcher docks in the same slot.** `TaskbarHost.AnchorScreenX` finds its strip by
  window caption (`DesktopSwitcherStrip`) and sits left of it. If that caption ever changes in
  the sibling, the two strips overlap.
- **`TextRenderer` ignores `Graphics.SetClip`** unless `PreserveGraphicsClipping` is passed;
  without it the scrolling title painted over the buttons.
- **A WinForms `Timer` cannot animate smoothly.** WM_TIMER is low priority and quantised to
  the 15.6 ms tick: measured ~37 fps with 45 ms gaps. `FramePump` is a thread that sleeps to a
  fixed 60 Hz schedule and only calls `InvalidateRect`, with `timeBeginPeriod(1)` held only while
  it runs; animation is stepped by measured time (`Motion.Rate`), not frame count. Do not
  "simplify" it back. (It is used for the panel's slide and fade only; the seek bar's 20 Hz is a
  plain Timer, which is fine at that rate.)
- **Album art needs a hand-rolled stream read.** `AsStream()` is in the assembly that cannot be
  referenced. `Thumbnail.OpenReadAsync()` -> `DataReader.LoadAsync(size)` -> `ReadBytes` works,
  off the UI thread. Browsers hand over a small 16:9 video thumbnail (or just their own logo), so
  art is centre-cropped to a square, and a title can arrive before its image - hence the retries.
- **An average of the pixels is a bad accent.** A yellow-on-blue cover averages to khaki, which is
  not in the picture. `AlbumArt` votes pixels into 12 hue buckets and takes the heaviest.
- **A clickable popup that never takes focus** needs `WS_EX_NOACTIVATE` *and* `WM_MOUSEACTIVATE ->
  MA_NOACTIVATE`; clicks are still delivered. A seek drag uses `SetCapture`. It cannot receive
  keys, so there is no Esc; outside clicks come from a low-level mouse hook (`ClickWatcher`, own
  thread, installed only while open) and the tile's own press is ignored there, or the panel
  would close on press and reopen on release.
- **Handle counts climb while the panel is toggled and fall back** on the next GC (thread and
  event handles awaiting finalisation). 30 open/close cycles went 571 -> 803 -> 419 after a GC;
  GDI objects stayed at 18 throughout. Not a leak, but do not mistake it for one.
- **Follow the neighbour by event, not by poll.** Adding a desktop makes DesktopSwitcher's strip
  grow over the tile; a 1 s tick, then an 80 ms poll, both left it visible. `LocationWatcher`
  (`SetWinEventHook`, EVENT_OBJECT_LOCATIONCHANGE, filtered to its process id) fixed it.
- **The idle cost was the SMTC read, not the paint** (~4.7 ms per read, 0.47% of a core at 1 Hz).
  The tick repaints from the last read and the tile extrapolates; 0.16% after.
- **Textures do not filter when scaled by a brush transform.** `AlbumArt` resamples once per size.
- **Painting a layered window:** one alpha for the whole window keeps ClearType, because the text
  is drawn onto opaque pixels of the back buffer first.
- **A broken config is skipped, not defaulted**, when reloading live (`Config.TryRead`): an
  editor mid-save would otherwise reset every setting for a moment.

## Build order

The order the pieces were built in, each verified live before the next - useful when something
breaks, as the layers can be tested bottom-up with the selftests.

| | What |
|---|---|
| 1 | Spike: SMTC from the in-box compiler (decided the build approach, above); thumbnail via `DataReader`; seek. |
| 2 | `MediaService`, `MediaState`, the selftests. |
| 3 | `TaskbarHost` (from DesktopSwitcher), `Native`, `MediaTile`, `Controller`: the tile docks, dims when idle, rebuilds after an Explorer restart. |
| 4 | SMTC manager reconnect after Explorer restarts; right-click menu, pinning, `Config`, run at login, track-change grace. |
| 5 | Global hotkeys, live config reload. |
| 6 | `AlbumArt`, `FramePump`, `Motion`, `ClickWatcher`, `MediaPanel`: buttons, seek drag, accent tint, slide and fade, panel hotkey. |
| 7 | Icon, `tools/`, install via `build.cmd`, these docs; then CPU and memory trimming and `LocationWatcher`. |

Not built, deliberately: volume mixer, output-device switching, auto-pause, ducking, shuffle/repeat,
a player-switcher row in the panel (the right-click menu pins), Esc to close, a hover tooltip with
the full title.

## House style

Match DesktopSwitcher: comments explain *why*, at length, and often record the failure that
motivated the current shape. That is deliberate, given how much rests on behaviour that cannot
be looked up. A change that strips the reasoning out of a comment loses more than it saves.
Prose in the docs is plain, specific, no marketing.
