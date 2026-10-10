# tools

PowerShell helpers for checking the tile and panel from WSL, where nobody is looking at the screen.
They are development aids, not part of the app, and nothing builds or ships them.

Run from WSL with the path converted, as the build does:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w tools/shot.ps1)"
```

| Script | What it does |
|---|---|
| `shot.ps1` | Captures the bottom 80 px of the primary screen to `C:\Users\Public\tb.png`. Read the PNG to see the tile. |
| `shotfull.ps1` | Captures the bottom-right 800x360 to `C:\Users\Public\tb2.png`, tall enough to include an open menu or the panel. |
| `mv.ps1 -x X -y Y` | Moves the pointer (screen pixels). Hover testing. |
| `click.ps1 -x X -y Y` | Moves, then left-clicks. |
| `rclick.ps1 -x X -y Y` | Moves, then right-clicks. |
| `mclick.ps1 -x X -y Y` | Moves, then middle-clicks. |
| `drag.ps1 -x1 -y1 -x2 -y2` | Presses at the first point, moves to the second in steps, releases. Drives the seek bar. |
| `keys.ps1 -chord ctrl+alt+space` | Sends a key chord: ctrl, alt, shift, space, left, right, up, down, f9, f10, m. |
| `windows.ps1` | Prints every child window of the taskbar (class, parent, rectangle, owner pid, caption). Read-only. Shows where Start, search, the task buttons, the tray, DesktopSwitcher's strip and the tile sit. |

All of them mark themselves DPI-aware first, so coordinates are physical pixels and match the
PNGs. Find where the tile is from a fresh `shot.ps1` - it moves when the tray or
DesktopSwitcher changes width - and never reuse coordinates from an old capture.

Delete the PNGs when done; they are screenshots of the user's screen.
