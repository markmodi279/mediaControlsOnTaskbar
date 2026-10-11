using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MediaController
{
    /// <summary>
    /// Owns everything and runs the message loop: the media service, the taskbar host, the
    /// tile, the tray icon.
    ///
    /// An ApplicationContext rather than a hidden Form. Nothing here needs a top-level
    /// window of its own - the tile is a child of Explorer's and the tray icon brings its
    /// own - and a hidden Form is one more thing for Alt+Tab and the shell to trip over.
    ///
    /// The tile is disposable. An Explorer restart destroys it along with its parent, so
    /// the once-per-second tick checks that the taskbar is the one the tile was built
    /// against and builds a fresh tile if not. Rebuilding is the only recovery; there is
    /// no attempt to re-parent a dead window.
    /// </summary>
    sealed class Controller : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "MediaController";

        static readonly Color FallbackBackground = Color.FromArgb(32, 32, 32);

        readonly Config _cfg;
        readonly MediaService _media = new MediaService();
        readonly TaskbarHost _host = new TaskbarHost();
        readonly Timer _tick = new Timer();
        readonly Timer _coalesce = new Timer();
        readonly Timer _follow = new Timer();
        readonly Timer _trim = new Timer();
        readonly NotifyIcon _tray = new NotifyIcon();

        HotkeyWindow _hotkeys;
        ConfigWatcher _watcher;
        MediaTile _tile;
        MediaPanel _panel;
        AlbumArt _art;
        string _artKey = "";
        ContextMenuStrip _menu;
        MenuTheme _menuTheme;
        Color _background = FallbackBackground;

        public Controller(Config cfg)
        {
            _cfg = cfg;
            _media.PinnedAppId = cfg.PinnedApp;

            try
            {
                _media.Start();
            }
            catch (Exception ex)
            {
                // No session manager (Windows older than 1809) or it would not start. The
                // tile still docks, permanently dimmed, rather than the app failing to run.
                Log.Write("media: start failed: " + ex.Message);
            }
            _media.StateChanged += OnMediaChanged;

            _tray.Icon = CreateIcon();
            _tray.Text = "MediaController";
            var trayMenu = new ContextMenuStrip();
            trayMenu.Opening += delegate { FillCommonItems(trayMenu); };
            _tray.ContextMenuStrip = trayMenu;
            _tray.Visible = true;

            // SMTC raises two or three events for one change (play, then position, then
            // properties). A short one-shot timer folds them into a single read and repaint.
            _coalesce.Interval = 100;
            _coalesce.Tick += delegate { _coalesce.Stop(); Refresh(); };

            // A tray app sits for days, and the runtime and startup leave a lot of pages
            // resident that nothing will touch again (JIT, WinForms and WinRT initialisation).
            // Trimmed once shortly after start, once the first paint is done, and now and
            // then after. Not on every tick: each trim costs soft faults on the next use.
            _trim.Interval = 6000;
            _trim.Tick += delegate { _trim.Interval = 300000; GC.Collect(); Native.TrimWorkingSet(); };
            _trim.Start();

            _glide.Interval = Motion.FrameMs;
            _glide.Tick += delegate { OnGlideFrame(); };

            _follow.Interval = 250;
            _follow.Tick += delegate { FollowNeighbour(); };
            _follow.Start();

            _tick.Interval = 1000;
            _tick.Tick += delegate { OnTick(); };
            _tick.Start();

            _hotkeys = new HotkeyWindow();
            _hotkeys.Pressed += OnHotkey;
            ApplyHotkeys();

            _watcher = new ConfigWatcher();
            _watcher.Changed += OnConfigChanged;

            Rebuild();
            Refresh();
        }

        // ---- hotkeys and live config ---------------------------------------------------

        void OnHotkey(int id)
        {
            switch (id)
            {
                case HotkeyWindow.Next: _media.Next(); break;
                case HotkeyWindow.Previous: _media.Previous(); break;
                case HotkeyWindow.TogglePanel: TogglePanel(); break;
                default: _media.TogglePlayPause(); break;
            }
        }

        // A binding another program already owns is not fatal - the other two still work -
        // but silence would look like a bug, so it is said once, from the tray.
        void ApplyHotkeys()
        {
            List<string> problems = _hotkeys.Register(_cfg.HotkeyPlayPause, _cfg.HotkeyNext, _cfg.HotkeyPrevious, _cfg.HotkeyPanel);
            if (problems.Count == 0) return;

            string text = string.Join("\n", problems.ToArray());
            Log.Write("hotkeys: " + text.Replace("\n", "; "));
            _tray.ShowBalloonTip(6000, "MediaController: hotkey problem", text, ToolTipIcon.Warning);
        }

        // The file changed on disk. Only what actually differs is acted on: rebuilding the
        // tile or re-registering hotkeys for a no-op save (and every pin writes the file,
        // which comes straight back here) would flicker the tile for nothing. A file that
        // does not parse is skipped, not defaulted - see Config.TryRead.
        void OnConfigChanged()
        {
            Config next;
            if (!Config.TryRead(out next)) return;

            // Shape and corner radius are baked in at construction, so they take the same path as a
            // margin change: the tile is rebuilt, and rebuilding drops the panel too.
            bool relayout = next.Margin != _cfg.Margin
                || next.Side != _cfg.Side
                || next.Shape != _cfg.Shape
                || next.PanelRadius != _cfg.PanelRadius
                || next.HoverControl != _cfg.HoverControl;
            bool rebind = next.HotkeyPlayPause != _cfg.HotkeyPlayPause
                || next.HotkeyNext != _cfg.HotkeyNext
                || next.HotkeyPrevious != _cfg.HotkeyPrevious
                || next.HotkeyPanel != _cfg.HotkeyPanel;
            bool repin = next.PinnedApp != _cfg.PinnedApp;

            _cfg.ApplyFrom(next);
            Log.Init(Config.LogPath, _cfg.Diagnostics || Environment.GetEnvironmentVariable("MC_LOG") == "1");

            if (repin) _media.PinnedAppId = _cfg.PinnedApp;
            if (rebind) ApplyHotkeys();
            if (relayout) Rebuild();
            Log.Write("config: reloaded" + (relayout ? " relayout" : "") + (rebind ? " rebind" : "") + (repin ? " repin" : ""));
        }

        // ---- tile lifecycle -----------------------------------------------------------

        void Rebuild()
        {
            DestroyTile();
            if (!_host.Locate() || !_host.HasAnchor) return;   // the tick retries

            _host.RightOfNeighbour = _cfg.Side == TileSide.Right;
            _host.ResetGlide();
            _glide.Stop();
            int width = _host.Scale(MediaTile.Width);
            int margin = _host.Scale(_cfg.Margin);

            // Sample with the old tile already gone, or the "taskbar colour" read back is
            // our own last paint (see TaskbarHost.TrySampleBackground).
            Color sampled;
            if (_host.TrySampleBackground(width, margin, out sampled)) _background = sampled;

            Rectangle bounds;
            if (!_host.TryComputeBounds(width, margin, out bounds)) return;

            _tile = new MediaTile(_host.TrayWindow, bounds, _background, _host.DpiScale, _cfg.Shape,
                // Above the tile there is no room on a vertical taskbar worth the code.
                _cfg.HoverControl && !_host.IsVertical,
                _cfg.Side == TileSide.Right);
            // The panel opens where the hover button would be, and has transport buttons of its own.
            _tile.Suppressed = delegate { return _panel != null && _panel.IsOpen; };
            _tile.Clicked += OnTileClicked;
            _tile.MiddleClicked += OnTileMiddleClicked;
            _tile.PlayPauseClicked += OnTileMiddleClicked;
            _tile.ContextMenuRequested += OnContextMenu;
            Native.SetWindowPos(_tile.Handle, Native.HWND_TOP, bounds.X, bounds.Y,
                bounds.Width, bounds.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Log.Write("tile built: " + bounds);
        }

        void DestroyTile()
        {
            // The panel is a top-level window and would be stranded by an Explorer restart,
            // and it was built against this taskbar's colour and scale.
            DestroyPanel();
            if (_tile == null) return;
            _tile.Clicked -= OnTileClicked;
            _tile.MiddleClicked -= OnTileMiddleClicked;
            _tile.PlayPauseClicked -= OnTileMiddleClicked;
            _tile.ContextMenuRequested -= OnContextMenu;
            _tile.Dispose();
            _tile = null;
        }

        // The tick is for upkeep, not for reading. A full SMTC read is several cross-process
        // calls and measured about 4.7 ms; doing it every second was the whole of the idle
        // cost (~0.5% of a core while a track played). The tile extrapolates the position
        // from the last read, so between SMTC events the tick only repaints the ring, and a
        // real read happens on events, every SafetyReadTicks as a net for anything that
        // raised none (a stale manager after an Explorer restart), and every tick while a
        // vanished session is being held (see GraceMs) so the idle look is not late.
        const int SafetyReadTicks = 5;
        int _ticks;

        void OnTick()
        {
            bool rebuilt = false;
            if (_tile == null || !_host.IsHealthy() || !_host.IsAttached(_tile.Handle))
            {
                Rebuild();
                rebuilt = true;
            }
            else
            {
                // Keeps us above the task buttons in z-order. Following the neighbour as it
                // grows is FollowNeighbour's job, which runs much more often.
                PlaceTile();
                _lastAnchor = _host.AnchorX();
            }

            _ticks++;
            if (rebuilt || _holding || _lastReal == null || _ticks % SafetyReadTicks == 0) Refresh();
            else Repaint();
        }

        // DesktopSwitcher's strip docks in the same slot and grows leftward over us each time a
        // desktop is added. Found by the 1 s tick alone, the two overlapped for up to a second.
        // This looks only at where the neighbour's edge is - two window lookups - and moves
        // the tile the moment it changes.
        //
        // Now event-driven as well (LocationWatcher): the poll was still leaving the overlap
        // visible for a few frames when a desktop was added. The poll stays, slower, to notice
        // the neighbour starting, stopping or restarting, which is when the hook is (re)made.
        int _lastAnchor;
        LocationWatcher _neighbourWatch;

        // On the right of the strip the tile sits against the notification area, and a tray icon
        // coming or going moves that. The 250 ms poll above left the tile a quarter of a second
        // behind, which with DesktopSwitcher's strip following the tile read as the tile jumping
        // onto the strip and the strip then shifting. Explorer owns the notification area, so a
        // location hook on Explorer says when it moves; the callback is one comparison. The tile
        // then glides rather than jumps, and the strip, which follows the tile, glides with it.
        LocationWatcher _explorerWatch;
        readonly Timer _glide = new Timer();
        int _glideTick;

        void WatchExplorer()
        {
            uint pid = _cfg.Side == TileSide.Right ? _host.TaskbarProcessId() : 0;
            if (_explorerWatch != null && _explorerWatch.ProcessId == pid) return;
            if (_explorerWatch != null) { _explorerWatch.Dispose(); _explorerWatch = null; }
            if (pid != 0) _explorerWatch = new LocationWatcher(pid, OnExplorerMoved);
        }

        void OnExplorerMoved()
        {
            if (_tile != null && _host.AnchorMoved()) PlaceTile();
        }

        /// <summary>Positions the tile, and starts the glide if that found the anchor had moved.</summary>
        void PlaceTile()
        {
            _host.Reassert(_tile.Handle, _host.Scale(MediaTile.Width), _host.Scale(_cfg.Margin));
            if (_host.Gliding && !_glide.Enabled) { _glideTick = Environment.TickCount; _glide.Start(); }
        }

        void OnGlideFrame()
        {
            int now = Environment.TickCount;
            int elapsed = now - _glideTick;
            _glideTick = now;
            if (_tile == null) { _glide.Stop(); return; }
            _host.StepGlide(elapsed);
            _host.Reassert(_tile.Handle, _host.Scale(MediaTile.Width), _host.Scale(_cfg.Margin));
            if (!_host.Gliding) _glide.Stop();
        }

        void FollowNeighbour()
        {
            WatchExplorer();
            uint pid = _host.NeighbourProcessId();
            if (_neighbourWatch == null ? pid != 0 : _neighbourWatch.ProcessId != pid)
            {
                if (_neighbourWatch != null) { _neighbourWatch.Dispose(); _neighbourWatch = null; }
                if (pid != 0) _neighbourWatch = new LocationWatcher(pid, FollowNeighbour2);
            }
            FollowNeighbour2();
        }

        void FollowNeighbour2()
        {
            if (_tile == null) return;
            if (_cfg.Side == TileSide.Right) { if (_host.AnchorMoved()) PlaceTile(); return; }
            int a = _host.AnchorX();
            if (a == _lastAnchor) return;
            _lastAnchor = a;
            PlaceTile();
        }

        // ---- media ---------------------------------------------------------------------

        void OnMediaChanged()
        {
            _coalesce.Stop();
            _coalesce.Start();
        }

        // Between tracks - especially a browser moving to the next video - the session
        // disappears for a second or two and comes back with the next title. Shown as it
        // happens, the tile flashes its idle look at every track change. So a snapshot
        // with no session is ignored for a short grace period after the last real one;
        // a player that has genuinely gone dims the tile once the period is up.
        const long GraceMs = 2500;
        MediaState _lastReal;
        long _lastRealAt;
        MediaState _shown = MediaState.None;
        bool _holding;

        void Refresh()
        {
            if (_tile == null) return;

            MediaState st = _media.Snapshot();
            long now = Environment.TickCount;
            _holding = false;
            if (st.HasSession)
            {
                _lastReal = st;
                _lastRealAt = now;
            }
            else if (_lastReal != null && unchecked(now - _lastRealAt) < GraceMs)
            {
                st = _lastReal;
                _holding = true;
            }
            _shown = st;
            UpdateArt(st);
            Repaint();
        }

        // Pushes what is already known to the tile and panel. The tile skips the paint when
        // the ring has not visibly moved.
        void Repaint()
        {
            if (_tile == null) return;
            _tile.SetState(_shown, _art);
            if (_panel != null) _panel.SetState(_shown, _art);
        }

        // One decoded image at a time, replaced when the track's art key changes and when
        // the bytes arrive (they come in after the first snapshot of a new track). The old
        // bitmap is disposed here and only here, so the leak risk is this one method.
        void UpdateArt(MediaState st)
        {
            if (st.ArtKey != _artKey)
            {
                _artKey = st.ArtKey;
                if (_art != null) { _art.Dispose(); _art = null; }
            }
            if (_art == null && st.ArtBytes != null)
                _art = AlbumArt.FromBytes(st.ArtBytes);
        }

        // ---- panel ---------------------------------------------------------------------

        void OnTileClicked()
        {
            TogglePanel();
        }

        void TogglePanel()
        {
            if (_tile == null) return;
            if (_panel != null && _panel.IsOpen) { _panel.Close(false); return; }

            // Built on first use and again after a rebuild: it carries the taskbar colour and
            // DPI scale it was made for, and both can change with the taskbar.
            if (_panel == null)
            {
                _panel = new MediaPanel(_background, _host.DpiScale, _cfg.PanelRadius);
                _panel.ButtonClicked += OnPanelButton;
                _panel.SeekRequested += OnSeek;
            }
            Refresh();   // so it opens showing now, not up to a second ago
            Native.RECT r;
            Native.GetWindowRect(_tile.Handle, out r);
            _panel.Open(new Rectangle(r.Left, r.Top, r.Width, r.Height));
        }

        void DestroyPanel()
        {
            if (_panel == null) return;
            _panel.ButtonClicked -= OnPanelButton;
            _panel.SeekRequested -= OnSeek;
            _panel.Close(true);
            _panel.Dispose();
            _panel = null;
        }

        void OnPanelButton(int button)
        {
            switch (button)
            {
                case MediaPanel.Previous: _media.Previous(); break;
                case MediaPanel.Next: _media.Next(); break;
                default: _media.TogglePlayPause(); break;
            }
        }

        void OnSeek(TimeSpan position)
        {
            _media.Seek(position);
            OnMediaChanged();   // read it back at once rather than at the next tick
        }

        void OnTileMiddleClicked()
        {
            _media.TogglePlayPause();
        }

        // ---- menu ----------------------------------------------------------------------

        // Rebuilt on every right click: its contents are the players running right now, and
        // the previous one is disposed first. The theme owns a font, so building one per
        // click without disposing would leak one per click.
        void OnContextMenu(Point screen)
        {
            DisposeMenu();
            _menuTheme = new MenuTheme(_background, _host.DpiScale);
            var menu = new ContextMenuStrip();

            string pin = _media.PinnedAppId;
            menu.Items.Add(Item("Follow current player", pin.Length == 0, delegate { Pin(""); }));

            bool pinnedRunning = false;
            List<string> ids = _media.SessionAppIds();
            foreach (string id in ids)
            {
                bool isPinned = string.Equals(id, pin, StringComparison.OrdinalIgnoreCase);
                if (isPinned) pinnedRunning = true;
                string captured = id;
                menu.Items.Add(Item(MediaState.FriendlyName(id), isPinned, delegate { Pin(captured); }));
            }
            if (pin.Length > 0 && !pinnedRunning)
            {
                // Still the pin, though its app is closed: shown so the state is visible and
                // can be undone, and it takes effect again if the app comes back.
                menu.Items.Add(Item(MediaState.FriendlyName(pin) + " (not running)", true, delegate { Pin(""); }));
            }
            if (ids.Count == 0 && pin.Length == 0)
            {
                var none = new ToolStripMenuItem("No players running");
                none.Enabled = false;
                menu.Items.Add(none);
            }

            menu.Items.Add(new ToolStripSeparator());
            FillCommonItems(menu, false);
            _menuTheme.Apply(menu);
            _menu = menu;

            // Anchored to the tile's top edge rather than the pointer: the tile is on the
            // taskbar, so the menu opens upward from it.
            Native.RECT r;
            Native.GetWindowRect(_tile.Handle, out r);
            menu.Show(new Point(screen.X, r.Top));
        }

        // Run at login, Open config, Exit: shared by the tile menu and the tray menu.
        void FillCommonItems(ContextMenuStrip menu)
        {
            FillCommonItems(menu, true);
        }

        void FillCommonItems(ContextMenuStrip menu, bool clear)
        {
            if (clear) menu.Items.Clear();
            menu.Items.Add(Item("Run at login", IsAutostartEnabled(), delegate { SetAutostart(!IsAutostartEnabled()); }));
            menu.Items.Add(Item("Open config file", false, delegate { OpenConfig(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", false, delegate { ExitThread(); }));
        }

        // The theme turns the check margin off (it would be a gutter down a flat panel and
        // its tick is drawn in the system's blue), so a selected row leads with a bullet.
        // Unselected rows are padded to the same indent so the labels line up.
        static ToolStripMenuItem Item(string text, bool selected, Action onClick)
        {
            var item = new ToolStripMenuItem((selected ? "\u2022  " : "     ") + text);
            item.Click += delegate { onClick(); };
            return item;
        }

        void Pin(string appId)
        {
            _media.PinnedAppId = appId;
            _cfg.PinnedApp = appId;
            _cfg.Save();
        }

        void DisposeMenu()
        {
            if (_menu != null) { _menu.Dispose(); _menu = null; }
            if (_menuTheme != null) { _menuTheme.Dispose(); _menuTheme = null; }
        }

        void OpenConfig()
        {
            try
            {
                // notepad.exe by name rather than the file's association: .json often has
                // none, and then Process.Start fails with a "choose an app" dialog.
                Process.Start("notepad.exe", "\"" + Config.FilePath + "\"");
            }
            catch (Exception ex)
            {
                Log.Write("controller: could not open config - " + ex.Message);
            }
        }

        // ---- autostart -----------------------------------------------------------------

        static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
            catch (Exception) { return false; }
        }

        static void SetAutostart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(RunValue, false);
                    Log.Write("controller: autostart " + (enabled ? "enabled" : "disabled"));
                }
            }
            catch (Exception ex)
            {
                Log.Write("controller: autostart change failed - " + ex.Message);
            }
        }

        // ---- tray icon -----------------------------------------------------------------

        // The tray shows the same icon as the exe (assets\MediaController.ico, stamped in by
        // -win32icon), so changing the .ico changes both. The drawn triangle below was the
        // original tray icon, before the exe had one of its own; it stays as the fallback
        // for when the exe's icon cannot be read.
        static Icon CreateIcon()
        {
            try
            {
                Icon own = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (own != null) return own;
            }
            catch (Exception ex)
            {
                Log.Write("controller: exe icon unavailable, drawing one - " + ex.Message);
            }
            return DrawIcon();
        }

        static Icon DrawIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(230, 230, 230)))
                        g.FillPolygon(b, new[] { new Point(3, 1), new Point(3, 15), new Point(14, 8) });
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }

        protected override void ExitThreadCore()
        {
            _tick.Stop();
            _follow.Stop();
            _glide.Stop();
            if (_explorerWatch != null) { _explorerWatch.Dispose(); _explorerWatch = null; }
            if (_neighbourWatch != null) { _neighbourWatch.Dispose(); _neighbourWatch = null; }
            _trim.Stop();
            _coalesce.Stop();
            if (_watcher != null) _watcher.Dispose();
            if (_hotkeys != null) _hotkeys.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            DestroyTile();
            if (_art != null) { _art.Dispose(); _art = null; }
            DisposeMenu();
            _media.Dispose();
            base.ExitThreadCore();
        }
    }
}
