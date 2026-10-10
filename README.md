# MediaController

A small round album-art tile in the Windows 10 taskbar, just left of the tray icons (and left of
[DesktopSwitcher](../desktopSwitcher)'s strip, if that is running). A thin ring around it fills as
the track plays. Click it and a card opens above it: art, title, artist, previous / play-pause /
next, and a seek bar you can drag.

```
        +--------------------------------------+
        | [art]  Title                         |
        |        Artist                        |
        |        Brave                         |
        |        |<   (||)   >|                |
        |  ----------o-----------------------  |
        |  1:01                          3:41  |
        +--------------------------------------+
                          (o)  [1][2][+]  ^ ENG  17:22
```

Windows shows what is playing only inside a flyout that has to be opened, and only when a
media key is pressed. This keeps a mark on the taskbar all the time, and the controls one click away.

It works with anything that reports to Windows' media session manager (SMTC): Spotify,
browsers, VLC, the Windows media apps. No plugin per player.

## Requirements

Windows 10 1809 or later, and nothing else. It builds with the C# compiler that ships in the
box (`.NET Framework 4.8`) and needs no SDK, no admin rights and no installer.

Developed and verified on **Windows 10 22H2, build 19045**. Docking into the taskbar is a
Windows 10 technique; it is not expected to work on Windows 11.

## Build

```cmd
build.cmd            :: windowed build (normal use)
build.cmd console    :: console build, keeps the selftest output visible
```

Output goes to `%LOCALAPPDATA%\MediaController\MediaController.exe`. Run it with no arguments.

> The compiler is **C# 5 only** - no string interpolation, `nameof`, `?.` or expression-bodied
> members anywhere in this tree.

## Use

| Action | Effect |
|---|---|
| Left click the tile | open / close the panel |
| Middle click the tile | play / pause |
| Right click the tile | pick which player to control, run at login, open the config, exit |
| Click outside the panel | close it |
| Panel: buttons | previous / play-pause / next |
| Panel: click or drag the bar | seek; released, the player jumps there |
| Tray icon, right click | run at login, open the config, exit |

The tile shows the album art in a circle with a progress ring in a colour taken from the art.
Paused dims the art under a pause mark; with no art it is a plain disc with a note; with nothing
playing it is an empty ring. The panel is tinted with the same colour.

The seek bar is greyed out when the player does not allow seeking (live streams, some browser
tabs). The panel never takes keyboard focus, so it has no Esc: close it by clicking elsewhere,
on the tile, or with the panel hotkey.

**Which player.** By default the tile follows whichever player Windows treats as current.
Right click and choose one to pin it: it then controls that app regardless of what else
is playing, and falls back to following if it is closed. "Follow current player" unpins.

## Hotkeys

Global, working while any other app has focus. Defaults:

| Keys | Action |
|---|---|
| Ctrl+Alt+Space | play / pause |
| Ctrl+Alt+Right | next |
| Ctrl+Alt+Left | previous |
| Ctrl+Alt+M | open / close the panel |

Change them in the config file. If another program already owns a combination, the others
still work and a notification names the one that did not.

## Config

`%APPDATA%\MediaController\config.json`, written with defaults on first run and **applied the
moment it is saved** - no restart.

```json
{
  "pinnedApp": "",
  "margin": 6,
  "diagnostics": false,
  "hotkeys": {
    "playPause": "Ctrl+Alt+Space",
    "next": "Ctrl+Alt+Right",
    "previous": "Ctrl+Alt+Left",
    "panel": "Ctrl+Alt+M"
  }
}
```

- `pinnedApp` - set by the menu; the app's id as Windows knows it, e.g. `brave.exe`.
- `margin` - pixels at 96 DPI between the tile and its neighbour; scaled to the display.
- `diagnostics` - `true` writes `%APPDATA%\MediaController\log.txt`.
- Hotkeys are zero or more of `Ctrl`, `Alt`, `Shift`, `Win`, then one key by its .NET `Keys`
  name (`Space`, `Left`, `F9`, `A`, `MediaNextTrack`) or a digit. `""` turns one off.

A file that does not parse is ignored - the app keeps its current settings - so a half-typed
edit cannot break it.

## Known limits

- Windows 10 only for docking (see above).
- Between tracks, players such as a browser moving to the next video drop their session for a
  second or two; the tile holds the last state for 2.5 s rather than flashing its idle look.
- Position is extrapolated between updates, so the ring and bar can lag a player that does not
  report it often. A player may accept a seek and ignore it; the bar then snaps back after a second.
- Art is whatever the player publishes: a browser tab gives a small video thumbnail or just the
  browser's logo.
- A long title is cut with an ellipsis; there is no hover tooltip yet.
- Shuffle/repeat, volume and output-device switching are not in this version.

See [CLAUDE.md](CLAUDE.md) for the constraints behind how it is built, and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the code is laid out.
