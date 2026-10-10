# Architecture

One process, one assembly, namespace `MediaController`, foldered by layer. Everything runs on
the UI thread except SMTC's callbacks, the album-art fetch, the panel's frame pump and the
click-watching hook thread; each of those hands straight back to it or only posts a message.

```
src/
  app/      Program  Controller  Config  ConfigWatcher  Log  AssemblyInfo
  media/    MediaService  MediaState  AlbumArt
  shell/    Native  HotkeyWindow  ClickWatcher  LocationWatcher
  ui/       TaskbarHost  MediaTile  HoverButton  MediaPanel  Glyphs  FramePump  Motion  MenuTheme  Palette
  selftest/ SelfTest
```

## Data flow

```
 Windows SMTC ──events──▶ MediaService ──StateChanged──▶ Controller ──SetState──▶ MediaTile
 (any player)  (pool thread)  (posts to UI thread)       (coalesce 100 ms,   └──▶ MediaPanel (if open)
                                                          grace 2.5 s,
 thumbnail ◀── pool thread, once per track ──────────────  decode art once)
 click / hotkey ─▶ MediaTile / MediaPanel / HotkeyWindow ─▶ Controller ─▶ MediaService ─▶ SMTC command
 config.json ────▶ ConfigWatcher ─▶ Controller.OnConfigChanged ─▶ re-pin / re-bind / re-layout
```

Nothing outside `MediaService` ever holds a WinRT object. It reads once into an immutable
`MediaState` and the rest of the app only sees that.

## Layers

**`media/MediaService`** wraps `GlobalSystemMediaTransportControlsSessionManager`.
- *Which session:* the pinned app's if one is pinned and running, else Windows' current one.
- *Snapshot:* title, artist, album, status, buttons' enabled flags, whether seeking is allowed,
  position (as of `TakenAt`; the tile and panel advance it themselves) and duration. Never throws and never returns null; any failure is
  `MediaState.None` for that tick.
- *Art:* `AttachArt` keys the track by app + title + artist + album and starts a pool-thread
  fetch the first time a key is seen (`ReadArt`: thumbnail stream read by hand with a
  `DataReader`, because `AsStream` is in the unusable assembly). The bytes ride on later snapshots
  and `StateChanged` fires when they land. An empty result is retried twice, two seconds apart -
  browsers publish the title before the image.
- *Commands:* play/pause, next, previous, seek, against the same session `Snapshot` would read.
- *Events:* subscribes to the manager (session list, current session) and, per session, to
  media properties, playback info and timeline. The per-session hooks are redone whenever the
  list changes.
- *Staleness:* an RPC HRESULT marks the manager stale; the next `Snapshot` replaces it. See
  CLAUDE.md for why.
- *Threading:* callbacks only call `RaiseChanged`, which posts through the
  `SynchronizationContext` captured at construction. `Program` installs a WinForms context
  before the controller exists, or that capture would be null.
- *WinRT waiting:* `Await<T>` blocks on `Completed` with a 5 s timeout. Every operation used
  completes in milliseconds, so blocking the UI thread on them is acceptable; do not add a slow one.

**`ui/TaskbarHost`** finds `Shell_TrayWnd`, its notification area and rebar, reads the DPI,
computes where the tile belongs, and samples the taskbar's colour. Copied from DesktopSwitcher
with one change: `AnchorScreenX` anchors to the DesktopSwitcher strip's left edge when that
strip is docked, so the two do not overlap. On the right (`Config.Side`) it does not yield at all: the tile takes the slot beside the clock, names itself
`MediaControllerTileRight`, and the strip anchors to it instead. It is looked up by caption on every call, not
cached, because it moves each time a desktop is added or removed.

**`media/AlbumArt`** is a decoded cover: a centre-cropped square plus an accent colour (a
hue-bucket vote weighted by saturation and brightness, then pulled into a usable band). Drawn as a
circle or rounded square with a `TextureBrush`, not a clip region, so callers' clip rectangles survive.
Owned by the Controller, decoded once per art key, disposed on change.

**`ui/MediaTile`** is a bare `NativeWindow`, created as a child of the taskbar from the start
(`WS_CHILD`, parent = `Shell_TrayWnd`, `WS_EX_NOACTIVATE` so a click never steals focus). It is
owner-drawn into a back buffer: an art disc (circle, rounded square or square - `Config.Shape`), a ring that fills with progress, a hover halo.
*Hover button:* `HoverButton`, a separate popup (the taskbar is only as tall as the tile) above the tile, or
below for a top taskbar. Per-pixel alpha via `UpdateLayeredWindow`, not a region, so a 30 px circle keeps a
smooth edge; fade and rise are stepped by a `FramePump` that runs only while it moves. The pointer leaves the
tile to reach it, so a leave starts a 180 ms timer and the answer is read from the cursor position, not from
messages. The Controller suppresses it while the panel is open. Moving the pointer over the tile asks again,
because hover only changes once per visit.
It knows nothing about media beyond the state it is handed, and reports left / middle / right clicks.
No animation thread; the Controller's 1 s tick advances the ring.

**`ui/MediaPanel`** is the card: a top-level `WS_POPUP`, topmost, tool-window, **no-activate and
layered**, owner-drawn. Unlike DesktopSwitcher's stats panel it is clickable, but like it, it never
takes focus (`MA_NOACTIVATE`), so it cannot get keys and has no Esc.
- *Corners:* `panelRadius` rounds the card with `SetWindowRgn`, not per-pixel alpha, because the fade is one
  constant layered alpha and a region leaves that path untouched. A region edge is not anti-aliased; `Draw`
  strokes a smooth rounded border just inside it. Shape and radius changes rebuild the tile (and so the panel).
- *Opening:* anchored above the tile (below, for a top taskbar), clamped to the work area. Slides 8 px
  toward the taskbar and fades, by `Motion.Rate` against measured time, driven by a `FramePump`
  that runs only during the slide. One layered alpha for the whole window, text drawn onto opaque
  pixels first, so ClearType survives.
- *Closing:* a `ClickWatcher` (low-level mouse hook on its own thread, posting a message) exists only
  while the panel is open. A press inside the card or on the tile is ignored (the tile toggles it);
  anywhere else closes it.
- *Seek:* press on the bar, `SetCapture`, track the pointer, send on release. The chosen position is
  shown for 1.2 s in place of the player's, so the bar does not snap back before the player reports it.
  Greyed and inert when `CanSeek` is false.
- *Bar movement:* a 50 ms timer while open and playing; nothing while paused or closed.

**`ui/FramePump`** is a thread that sleeps to a fixed 60 Hz schedule and only calls `InvalidateRect`,
with `timeBeginPeriod(1)` held only while it runs. A WinForms `Timer` measured about 37 fps.

**`shell/HotkeyWindow`** is a message-only window owning the `RegisterHotKey` calls. Bindings are
text ("Ctrl+Alt+Space"); a binding that cannot be parsed or is already taken is reported by name
and skipped without affecting the others.

**`app/Controller`** is an `ApplicationContext` that owns all of the above.
- *Tile lifecycle:* the tile is disposable. A 1 s tick checks the taskbar is still the one
  the tile was built against and `Rebuild`s from scratch if not (Explorer restart, taskbar
  not ready yet). Otherwise the tick re-asserts position and z-order, which is also how the
  tile follows DesktopSwitcher as it grows, and refreshes state to advance the ring.
- *Tick vs reads:* the 1 s tick does upkeep and repaints from the last read; a real SMTC read
  happens on events, every 5th tick, and every tick while a vanished session is being held.
- *Following DesktopSwitcher:* `LocationWatcher` (out-of-context WinEvent hook, filtered to that
  process) repositions the tile the instant the neighbour's windows move; a 250 ms timer only
  notices it starting or stopping. Polling alone, even at 80 ms, left the overlap visible.
- *Working set:* trimmed 6 s after start and every 5 min.
- *Panel lifecycle:* built on first open, destroyed whenever the tile is (a top-level window would
  be stranded by an Explorer restart, and it carries the taskbar colour and DPI it was built for).
- *Art:* `UpdateArt` decodes the bytes when they arrive and disposes the old bitmap when the key changes.
- *Coalescing and grace:* see the data-flow diagram. Both exist because SMTC is noisy.
- *Menu:* rebuilt on every right click from the running sessions, themed by `MenuTheme` from the
  sampled taskbar colour; disposed before the next one.
- *Config:* `OnConfigChanged` diffs the new file against the current settings and acts only on
  what changed - re-pin, re-bind hotkeys, or relayout - so the write a pin itself causes does
  nothing visible.

**`app/Config`** is `config.json`: read with `JavaScriptSerializer`, written by hand so the file
is readable. `Load` is for startup (missing -> write defaults; broken -> use defaults, leave
the file); `TryRead` is for live reload (broken -> keep current settings).

**`selftest/SelfTest`** is the console harness. Only reachable from a console build.

## Lifetimes and ownership

| Object | Created | Destroyed |
|---|---|---|
| `Controller` | `Program.RunApp` | on exit |
| `MediaService`, `HotkeyWindow`, `ConfigWatcher`, tray icon | Controller constructor | `ExitThreadCore` |
| `MediaTile` | `Rebuild` | `DestroyTile`: every rebuild, and exit |
| `MediaPanel` | first open after a rebuild | `DestroyPanel`: with the tile, and exit |
| `ClickWatcher` hook thread | each panel open | each panel close |
| Frame thread, raised timer resolution | each panel open / close slide, each hover-button show / hide | when the slide ends |
| `AlbumArt` | when the art bytes arrive | track change, and exit |
| Menu and its theme | each right click | next right click, and exit |

## Files outside `src/`

| Path | Purpose |
|---|---|
| `build.cmd` | the build and install; comments carry the WinRT reference rules |
| `assets/MediaController.ico` | generated once, committed |
| `tools/` | PowerShell helpers for screenshots and input; see its README |
