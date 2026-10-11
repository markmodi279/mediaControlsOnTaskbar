using System;
using System.Drawing;

namespace MediaController
{
    /// <summary>
    /// Finds the taskbar, works out where the tile belongs, and keeps it parented
    /// there.
    ///
    /// The tile becomes a child of Shell_TrayWnd rather than a floating topmost
    /// window. That inherits, for free, everything a floating window would have to
    /// reimplement: it moves with the taskbar, hides when the taskbar auto-hides or a
    /// fullscreen app takes over, and appears on every virtual desktop.
    ///
    /// The cost is that an Explorer restart destroys our child window along with its
    /// parent. That is why the tile is disposable and the controller rebuilds it.
    ///
    /// Shared with DesktopSwitcher, which docks in exactly the same slot (left of the
    /// notification area). Rather than fight it for the spot, this tile finds the
    /// DesktopSwitcher strip among the taskbar's children and anchors to its left edge
    /// instead - see AnchorScreenX. Not running DesktopSwitcher changes nothing.
    /// </summary>
    sealed class TaskbarHost
    {
        IntPtr _tray;
        IntPtr _rebar;
        IntPtr _notifyArea;

        double _dpiScale = 1.0;

        public IntPtr TrayWindow { get { return _tray; } }
        public bool Located { get { return _tray != IntPtr.Zero; } }

        /// <summary>DPI scale of the taskbar's monitor. 1.0 at 96 DPI.</summary>
        public double DpiScale { get { return _dpiScale; } }

        /// <summary>Scales a size authored for 96 DPI to the taskbar's actual DPI.</summary>
        public int Scale(int value)
        {
            return (int)Math.Round(value * _dpiScale);
        }

        /// <summary>
        /// Re-finds the taskbar windows. Handles must be refreshed after an Explorer
        /// restart, because the new Explorer creates entirely new windows.
        /// </summary>
        public bool Locate()
        {
            _tray = Native.FindWindow("Shell_TrayWnd", null);
            if (_tray == IntPtr.Zero)
            {
                _rebar = IntPtr.Zero;
                _notifyArea = IntPtr.Zero;
                return false;
            }

            _rebar = Native.FindWindowEx(_tray, IntPtr.Zero, "ReBarWindow32", null);
            _notifyArea = Native.FindWindowEx(_tray, IntPtr.Zero, "TrayNotifyWnd", null);
            _dpiScale = Native.GetDpiScale(_tray);
            return true;
        }

        public bool IsHealthy()
        {
            return _tray != IntPtr.Zero
                && Native.IsWindow(_tray)
                && _tray == Native.FindWindow("Shell_TrayWnd", null);
        }

        /// <summary>True once the notification area exists and the tile can be anchored.</summary>
        public bool HasAnchor
        {
            get
            {
                EnsureChildren();
                return _notifyArea != IntPtr.Zero;
            }
        }

        /// <summary>
        /// Re-resolves the taskbar's child windows if they have gone away.
        ///
        /// A restarted Explorer creates Shell_TrayWnd before TrayNotifyWnd, so a Locate
        /// during that window leaves the anchor null. Without this, every later
        /// computation falls back to the taskbar's right edge and the tile is stranded
        /// to the right of the clock, with the once-per-second Reassert faithfully
        /// keeping it there.
        /// </summary>
        void EnsureChildren()
        {
            if (_tray == IntPtr.Zero) return;

            if (_notifyArea == IntPtr.Zero || !Native.IsWindow(_notifyArea))
                _notifyArea = Native.FindWindowEx(_tray, IntPtr.Zero, "TrayNotifyWnd", null);

            if (_rebar == IntPtr.Zero || !Native.IsWindow(_rebar))
                _rebar = Native.FindWindowEx(_tray, IntPtr.Zero, "ReBarWindow32", null);
        }

        /// <summary>True when the taskbar is docked left or right rather than top or bottom.</summary>
        public bool IsVertical
        {
            get
            {
                Native.RECT r;
                if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out r)) return false;
                return r.Height > r.Width;
            }
        }

        /// <summary>Caption of the sibling app's strip window; see AnchorScreenX.</summary>
        const string NeighbourCaption = "DesktopSwitcherStrip";

        /// <summary>
        /// Screen X that the tile's right edge (plus margin) sits against: the left edge of
        /// the DesktopSwitcher strip if one is docked, otherwise of the notification area,
        /// otherwise the taskbar's right edge.
        ///
        /// The neighbour is looked up by window caption each call, not cached: it appears
        /// and disappears with that app and its Explorer restarts, and it moves left every
        /// time a desktop is added. The once-per-second Reassert is what follows it.
        /// </summary>
        int AnchorScreenX(Native.RECT tray)
        {
            Native.RECT notify;
            int x = tray.Right;
            if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
                x = notify.Left;

            // On the right the tile owns the slot beside the clock and the strip steps aside
            // for it (DesktopSwitcher finds the tile by its "...Right" caption), so there is
            // nothing to yield to here.
            if (RightOfNeighbour) return x;

            Native.RECT n;
            IntPtr neighbour = Neighbour();
            if (neighbour != IntPtr.Zero && GetNeighbourRect(neighbour, tray, x, out n))
                x = n.Left;

            return x;
        }

        /// <summary>
        /// True when the tile docks beside the clock and DesktopSwitcher's strip sits left of
        /// it. The tile then names itself differently (see MediaTile) so the strip can tell.
        /// </summary>
        public bool RightOfNeighbour;

        IntPtr Neighbour()
        {
            return _tray == IntPtr.Zero ? IntPtr.Zero
                : Native.FindWindowEx(_tray, IntPtr.Zero, null, NeighbourCaption);
        }

        bool GetNeighbourRect(IntPtr neighbour, Native.RECT tray, int anchorX, out Native.RECT n)
        {
            n = new Native.RECT();
            return Native.IsWindowVisible(neighbour) && Native.GetWindowRect(neighbour, out n)
                && n.Left < anchorX && n.Left > tray.Left;
        }

        /// <summary>Process id of the DesktopSwitcher strip's owner, or 0 if it is not docked.</summary>
        public uint NeighbourProcessId()
        {
            if (_tray == IntPtr.Zero) return 0;
            IntPtr n = Native.FindWindowEx(_tray, IntPtr.Zero, null, NeighbourCaption);
            if (n == IntPtr.Zero) return 0;
            uint pid;
            Native.GetWindowThreadProcessId(n, out pid);
            return pid;
        }

        /// <summary>
        /// Where the tile's right edge currently has to be (see AnchorScreenX), or 0 when the
        /// taskbar is not there. Cheap enough to poll: two window lookups.
        /// </summary>
        public int AnchorX()
        {
            Native.RECT tray;
            if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out tray)) return 0;
            return AnchorScreenX(tray);
        }

        // --- glide ------------------------------------------------------------

        /// <summary>
        /// How long the tile takes to settle after the tray moves it.
        ///
        /// Not 100, which is what DesktopSwitcher's hover animations use. The ease is exponential,
        /// so it covers the same fraction of what is left every frame, and for a 30 px move at
        /// 100 ms that is 17 px in the first 15 ms frame - logged from a real move. It looks like
        /// the tile jumping to the new place with a short tail, and the pixels it leaves behind at
        /// the old place read as a second tile. At 300 ms the first frame is about 7 px.
        /// </summary>
        public const int GlideMs = 300;

        int _glideLastRaw = int.MinValue;   // the anchor as last seen, before any glide
        float _glideOffset;                 // pixels the shown anchor still lags the real one

        /// <summary>True while the tile is still catching up with a moved anchor.</summary>
        public bool Gliding { get { return _glideOffset != 0f; } }

        public void ResetGlide()
        {
            _glideLastRaw = int.MinValue;
            _glideOffset = 0f;
        }

        /// <summary>
        /// The anchor the tile is drawn against: the real one plus whatever of the last move it
        /// has not yet covered. Only on the right of the strip, where the anchor is the
        /// notification area and a tray icon coming or going moves it. On the left the anchor is
        /// the strip, which is already gliding itself, and the tile follows that exactly.
        ///
        /// This is the tile's half of a hand-off. DesktopSwitcher's strip sits against the tile
        /// and follows its position frame by frame, so whatever the tile does is what the strip
        /// does - with a jump here the strip jumps, and with an ease here both ease together and
        /// neither overlaps the other. The offset form (remember how far behind, ease that to
        /// zero) is the same one DesktopSwitcher uses: a second move mid-glide just adds to it.
        /// </summary>
        int GlideAnchor(int raw)
        {
            if (!RightOfNeighbour) { ResetGlide(); return raw; }
            if (_glideLastRaw != int.MinValue && raw != _glideLastRaw)
                _glideOffset += _glideLastRaw - raw;
            _glideLastRaw = raw;
            return raw + (int)Math.Round(_glideOffset);
        }

        /// <summary>True when the real anchor has moved since the tile was last placed against it.</summary>
        public bool AnchorMoved()
        {
            return RightOfNeighbour && _glideLastRaw != int.MinValue && AnchorX() != _glideLastRaw;
        }

        /// <summary>One frame of the glide; true while there is more to do.</summary>
        public bool StepGlide(int elapsedMs)
        {
            return Motion.Ease(ref _glideOffset, 0f, Motion.Rate(elapsedMs, GlideMs), Motion.PixelEpsilon);
        }

        /// <summary>Process id of Explorer, which owns the notification area; 0 if the taskbar is gone.</summary>
        public uint TaskbarProcessId()
        {
            if (_tray == IntPtr.Zero) return 0;
            uint pid;
            Native.GetWindowThreadProcessId(_tray, out pid);
            return pid;
        }

        /// <summary>
        /// Tile bounds in Shell_TrayWnd client coordinates.
        ///
        /// The tile is right-anchored to the notification area, so the gap to the
        /// clock stays constant as buttons are added or removed, and it grows leftward
        /// into the empty tail of the task-button area.
        /// </summary>
        public bool TryComputeBounds(int width, int margin, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            EnsureChildren();

            Native.RECT tray;
            if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out tray)) return false;

            int anchorScreenX = AnchorScreenX(tray);

            Native.RECT notify;
            if (IsVertical)
            {
                // Not the configured layout, but degrade sensibly instead of drawing
                // the tile off-screen: sit just above the notification area.
                int vBottom;
                if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
                    vBottom = notify.Top - margin;
                else
                    vBottom = tray.Bottom - margin;

                var vTopLeft = new Native.POINT();
                vTopLeft.X = tray.Left;
                vTopLeft.Y = vBottom - tray.Width;
                if (!Native.ScreenToClient(_tray, ref vTopLeft)) return false;

                bounds = new Rectangle(vTopLeft.X, vTopLeft.Y, tray.Width, tray.Width);
                return true;
            }

            var topLeft = new Native.POINT();
            topLeft.X = GlideAnchor(anchorScreenX) - margin - width;
            topLeft.Y = tray.Top;
            if (!Native.ScreenToClient(_tray, ref topLeft)) return false;

            bounds = new Rectangle(topLeft.X, topLeft.Y, width, tray.Height);
            return true;
        }

        public bool IsAttached(IntPtr child)
        {
            return child != IntPtr.Zero
                && Native.IsWindow(child)
                && Native.GetParent(child) == _tray;
        }

        /// <summary>
        /// Re-applies position and z-order. The tile overlaps the tail of the
        /// task-button area rather than reserving space, so it must stay above its
        /// siblings or task buttons would paint over it.
        /// </summary>
        public bool Reassert(IntPtr child, int width, int margin)
        {
            Rectangle bounds;
            if (!TryComputeBounds(width, margin, out bounds)) return false;

            if (RightOfNeighbour) CarryNeighbour(child, bounds.X, margin);

            return Native.SetWindowPos(child, Native.HWND_TOP,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        /// <summary>
        /// Moves DesktopSwitcher's strip by the same step as the tile, just before the tile moves.
        ///
        /// On the right of the strip the tile is the one that moves when a tray icon comes or
        /// goes, and the strip follows it from another process: a location hook, a message-loop
        /// turn, then its own SetWindowPos. That is a few milliseconds at best, and a few
        /// milliseconds is a frame the compositor can present - with the tile moving left, its
        /// edge showed over the strip's. Following can never close that gap, only shrink it. So
        /// the leader carries the follower: both move in this one turn, the gap between them
        /// does not change, and the strip's own follow, arriving later, finds it already there
        /// and has nothing to do.
        ///
        /// Only when the strip is actually the tile's neighbour - to its left, close enough to
        /// be the one DesktopSwitcher docked against us - and only by the tile's own step, so the
        /// spacing DesktopSwitcher chose is kept whatever its margin is.
        /// </summary>
        void CarryNeighbour(IntPtr tile, int newClientX, int margin)
        {
            IntPtr strip = Neighbour();
            if (strip == IntPtr.Zero || !Native.IsWindowVisible(strip)) return;

            Native.RECT t, n;
            if (!Native.GetWindowRect(tile, out t) || !Native.GetWindowRect(strip, out n)) return;

            var tp = new Native.POINT(); tp.X = t.Left; tp.Y = t.Top;
            var np = new Native.POINT(); np.X = n.Left; np.Y = n.Top;
            if (!Native.ScreenToClient(_tray, ref tp) || !Native.ScreenToClient(_tray, ref np)) return;

            int dx = newClientX - tp.X;
            int gap = t.Left - n.Right;
            if (dx == 0 || gap < 0 || gap > margin * 2 + Scale(20)) return;

            Native.SetWindowPos(strip, IntPtr.Zero, np.X + dx, np.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        /// <summary>
        /// Samples the taskbar's rendered colour just left of where the tile sits.
        ///
        /// A child window gets no DWM blur from its parent, so a hardcoded colour would
        /// read as a visible patch against a translucent taskbar. Sampling the live
        /// pixel keeps the tile blending in whatever the theme and wallpaper are.
        ///
        /// tileWidth must be the width of the tile that is actually on screen, and the
        /// caller must have destroyed it first. Both were got wrong for a long time: the
        /// probe was computed for two desktops however many there were, so with four at
        /// 125% the point landed inside the tile, and the tile was still up when it was
        /// read. What came back was the colour the tile was already painted, which it then
        /// dutifully repainted itself - a sample that could confirm anything except that
        /// the taskbar had changed.
        ///
        /// Fails rather than guessing when the taskbar is not the window at that pixel. The
        /// screen DC returns what is on screen, not what the taskbar is, and on waking from
        /// sleep the lock screen is on screen - fullscreen, over the taskbar - when the
        /// display change arrives. GetPixel read the lock screen's wallpaper, succeeded, and
        /// the tile repainted itself that colour and kept it: nothing resamples after an
        /// unlock, so it survived until an Explorer restart, a theme change, or the tray's
        /// reload item. Delaying the read cannot help, and the delay already there was aimed
        /// at this and missed. The pixel had settled. It belonged to a different window.
        /// </summary>
        public bool TrySampleBackground(int tileWidth, int margin, out Color color)
        {
            color = Color.Empty;
            EnsureChildren();

            Native.RECT tray;
            if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out tray)) return false;

            int anchorScreenX = AnchorScreenX(tray);

            // Just left of where the tile will sit, vertically centred - empty taskbar
            // unless a great many windows are open. The step off the tile's edge is
            // scaled like every other size here: unscaled, it was 8 physical pixels at
            // any DPI, which on a 150% display is barely five of the ones the layout is
            // authored in and leaves the probe hugging the tile's edge.
            int x = anchorScreenX - margin - tileWidth - Scale(8);
            // On the right, what is left of the tile is the strip, not empty taskbar: probe
            // left of the strip instead, or the sample is one of its (possibly lit) buttons.
            if (RightOfNeighbour)
            {
                Native.RECT sn;
                IntPtr nb = Neighbour();
                if (nb != IntPtr.Zero && GetNeighbourRect(nb, tray, anchorScreenX, out sn))
                    x = sn.Left - Scale(8);
            }
            int y = tray.Top + tray.Height / 2;
            if (x < tray.Left) x = tray.Left + Scale(4);

            // Whose pixel is this? Anything covering the taskbar - the lock screen after a
            // wake, a fullscreen app - would otherwise be sampled as though it were the
            // taskbar. Returning false here hands the caller its existing "keep the last
            // good colour" path, which is the right answer: a translucent taskbar over an
            // unchanged wallpaper is still the colour it was.
            if (Native.RootWindowFromPoint(x, y) != _tray)
            {
                Log.Write("taskbar: probe point is not the taskbar - sample skipped");
                return false;
            }

            // The screen DC, not the taskbar's own. Under DWM a foreign window's DC does
            // not contain its composited pixels, so GetPixel there returns CLR_INVALID;
            // the screen DC reflects what is actually displayed.
            IntPtr hdc = Native.GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;

            try
            {
                uint pixel = Native.GetPixel(hdc, x, y);
                if (pixel == 0xFFFFFFFF) return false; // CLR_INVALID

                color = Color.FromArgb((int)(pixel & 0xFF),
                                       (int)((pixel >> 8) & 0xFF),
                                       (int)((pixel >> 16) & 0xFF));
                return true;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        /// <summary>Diagnostic snapshot of the taskbar layout.</summary>
        public string Describe()
        {
            if (_tray == IntPtr.Zero) return "taskbar not found";
            EnsureChildren();

            Native.RECT tray, rebar, notify;
            Native.GetWindowRect(_tray, out tray);

            string s = string.Format("Shell_TrayWnd  ({0},{1})-({2},{3})  {4}x{5}  {6}",
                tray.Left, tray.Top, tray.Right, tray.Bottom,
                tray.Width, tray.Height, IsVertical ? "VERTICAL" : "horizontal");

            if (_rebar != IntPtr.Zero && Native.GetWindowRect(_rebar, out rebar))
                s += string.Format("\n  ReBarWindow32  x {0} -> {1}", rebar.Left, rebar.Right);

            if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
                s += string.Format("\n  TrayNotifyWnd  x {0} -> {1}", notify.Left, notify.Right);

            return s;
        }
    }
}
