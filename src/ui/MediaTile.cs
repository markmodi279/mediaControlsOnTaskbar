using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MediaController
{
    /// <summary>
    /// The mark on the taskbar: a tile (circle, rounded square or square, per config) showing the
    /// album art, with a thin ring that fills as the track plays. Everything else lives in the panel it opens.
    ///
    /// A child window of Shell_TrayWnd, owner-drawn, a bare NativeWindow - for the reason
    /// a Control wants a WinForms parent and this one's parent is
    /// Explorer's. CreateHandle with WS_CHILD and the taskbar as Parent makes it a child
    /// from the first instant.
    ///
    /// Looks:
    ///   playing, with art   art disc, ring in the art's accent colour
    ///   paused              the art dimmed under a pause glyph
    ///   no art              a plain disc with a note (playing) or a pause glyph
    ///   idle                an empty ring and a muted glyph - present, not shouting
    ///
    /// Knows nothing about media beyond what it is handed. It reports clicks and the
    /// Controller decides what they mean.
    /// </summary>
    sealed class MediaTile : NativeWindow, IDisposable
    {
        /// <summary>Window width at 96 DPI: the disc plus breathing room either side.</summary>
        public const int Width = 40;

        // Authored at 96 DPI, scaled by _dpiScale.
        const int Diameter = 30;
        const int RingWidth = 2;
        const int ArtInset = 4;

        // Corner radius of the rounded shape, as a fraction of the disc's side. Large enough to
        // read as clearly not a circle, small enough to read as clearly not a square.
        const float RoundedFraction = 0.30f;

        readonly double _dpiScale;
        readonly Color _background;
        readonly TileShape _shape;

        // The play/pause button: a small popup above (below, for a top taskbar) the tile, shown
        // while the pointer is over the tile or the button. It is a separate window because the
        // taskbar is only as tall as the tile; see HoverButton.
        readonly bool _wantButton;
        HoverButton _button;
        readonly Timer _hideTimer = new Timer();

        MediaState _state = MediaState.None;
        AlbumArt _art;
        Bitmap _buffer;
        bool _disposed;
        bool _hover;
        bool _tracking;

        /// <summary>Left click: the Controller toggles the panel.</summary>
        public event Action Clicked;

        /// <summary>The hover button was clicked.</summary>
        public event Action PlayPauseClicked;

        /// <summary>
        /// True while the button should stay away. The Controller says so while the panel is
        /// open: the panel sits where the button would, and carries its own transport buttons.
        /// </summary>
        public Func<bool> Suppressed;

        /// <summary>Middle click: play/pause.</summary>
        public event Action MiddleClicked;

        /// <summary>Right click, at the pointer in screen coordinates.</summary>
        public event Action<Point> ContextMenuRequested;

        public MediaTile(IntPtr parent, Rectangle bounds, Color background, double dpiScale, TileShape shape, bool hoverButton, bool rightOfNeighbour)
        {
            _shape = shape;
            _wantButton = hoverButton;
            _dpiScale = dpiScale;
            _background = background;

            var cp = new CreateParams();
            // The caption is how DesktopSwitcher finds this window. The "Right" form says the tile is
            // beside the clock and the strip must anchor to its left edge; it is chosen at
            // creation, and a change of side rebuilds the tile.
            cp.Caption = rightOfNeighbour ? "MediaControllerTileRight" : "MediaControllerTile";
            cp.X = bounds.X;
            cp.Y = bounds.Y;
            cp.Width = bounds.Width;
            cp.Height = bounds.Height;
            cp.Parent = parent;
            cp.Style = Native.WS_CHILD | Native.WS_VISIBLE;
            // Never take focus: a click on the tile must not steal it from the app the user
            // is working in.
            cp.ExStyle = Native.WS_EX_NOACTIVATE;
            CreateHandle(cp);

            // The pointer leaves the tile to reach the button, which is a different window, so
            // "left" cannot hide it on the spot. A beat later, if the pointer is over neither,
            // it goes.
            _hideTimer.Interval = 180;
            _hideTimer.Tick += delegate { _hideTimer.Stop(); HideIfAway(); };
        }

        /// <summary>
        /// Replaces what is shown and repaints. Cheap enough to call every second: that is
        /// what moves the ring between SMTC events. The art is borrowed, not owned.
        /// </summary>
        public void SetState(MediaState state, AlbumArt art)
        {
            state = state ?? MediaState.None;
            // The ring is a 30 px circle: under half a pixel of arc per 2 degrees. A repaint
            // for a change smaller than that draws the same picture, so it is skipped.
            int step = (int)(Fraction(state) * 180);
            bool same = ReferenceEquals(state, _state) && ReferenceEquals(art, _art) && step == _ringStep;
            _state = state;
            _art = art;
            _ringStep = step;
            if (!same) Invalidate();
            if (_button != null && _button.IsShown)
            {
                if (!state.HasSession || !state.CanPlayPause) HideButton();
                else _button.SetPlaying(state.IsPlaying);
            }
        }

        int _ringStep = -1;

        void Invalidate()
        {
            if (Handle != IntPtr.Zero) Native.InvalidateRect(Handle, IntPtr.Zero, false);
        }

        int Px(int v) { return (int)Math.Round(v * _dpiScale); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hideTimer.Dispose();
            if (_button != null) _button.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
            if (_buffer != null) _buffer.Dispose();
        }

        // ---- painting ------------------------------------------------------------------

        void Paint()
        {
            Native.PAINTSTRUCT ps;
            IntPtr hdc = Native.BeginPaint(Handle, out ps);
            try
            {
                Native.RECT r;
                Native.GetWindowRect(Handle, out r);
                int w = r.Width, h = r.Height;
                if (w <= 0 || h <= 0) return;

                // Back buffer, blitted once: painting straight to a taskbar child flickers.
                if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
                {
                    if (_buffer != null) _buffer.Dispose();
                    _buffer = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                }
                using (Graphics g = Graphics.FromImage(_buffer))
                    Draw(g, w, h);
                using (Graphics screen = Graphics.FromHdc(hdc))
                    screen.DrawImageUnscaled(_buffer, 0, 0);
            }
            finally
            {
                Native.EndPaint(Handle, ref ps);
            }
        }

        // Position now, not at the snapshot: the snapshot is up to a second old.
        double Fraction() { return Fraction(_state); }

        static double Fraction(MediaState st)
        {
            if (!st.HasSession || st.Duration <= TimeSpan.Zero) return 0;
            double pos = st.Position.TotalSeconds;
            if (st.IsPlaying) pos += unchecked(Environment.TickCount - st.TakenAt) / 1000.0;
            double f = pos / st.Duration.TotalSeconds;
            return f < 0 ? 0 : (f > 1 ? 1 : f);
        }

        void Draw(Graphics g, int w, int h)
        {
            g.Clear(_background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            bool live = _state.HasSession;
            Color ink = Palette.PrimaryText(_background);
            Color dim = Palette.MutedText(_background);
            Color accent = (live && _art != null) ? _art.Accent : ink;

            int d = Px(Diameter);
            var disc = new Rectangle((w - d) / 2, (h - d) / 2, d, d);

            // Corner radius of the outermost outline. Each inner outline (ring centre line, face)
            // is inset from it and loses the same amount of radius, so the corners stay concentric
            // and the ring does not pinch against the art at the corners.
            float outer = Radius(d);

            // Hover: a soft halo behind the disc, lifted from the taskbar colour so it reads
            // on a light or dark bar alike.
            if (_hover)
            {
                int grow = Px(3);
                var halo = Rectangle.Inflate(disc, grow, grow);
                using (var br = new SolidBrush(Palette.Lift(_background, 0.14f)))
                using (var path = ShapePath(halo, outer + grow))
                    g.FillPath(br, path);
            }

            // Ring: a faint full outline, with the played part over it in the accent.
            int pen = Math.Max(2, Px(RingWidth));
            var ringRect = new Rectangle(disc.X + pen / 2, disc.Y + pen / 2, disc.Width - pen, disc.Height - pen);
            float ringR = outer - pen / 2f;
            using (var track = new Pen(Palette.Lift(_background, live ? 0.22f : 0.12f), pen))
            using (var path = ShapePath(ringRect, ringR))
                g.DrawPath(track, path);
            double frac = Fraction();
            if (live && frac > 0)
            {
                using (var played = new Pen(_state.IsPlaying ? accent : Palette.Blend(accent, _background, 0.4f), pen))
                {
                    played.StartCap = LineCap.Round;
                    played.EndCap = LineCap.Round;
                    if (_shape == TileShape.Circle)
                    {
                        g.DrawArc(played, ringRect, -90f, (float)(360.0 * frac));
                    }
                    else
                    {
                        using (var arc = OutlineProgress(ringRect, ringR, frac))
                            g.DrawPath(played, arc);
                    }
                }
            }

            // The face, inside the ring.
            int inset = pen + Px(ArtInset) - 2;
            var face = Rectangle.Inflate(disc, -inset, -inset);
            float faceR = outer - inset;
            if (!live)
            {
                Glyphs.Note(g, face.X + face.Width / 2f, face.Y + face.Height / 2f, face.Width, dim);
                return;
            }

            using (var facePath = ShapePath(face, faceR))
            {
                if (_art != null)
                {
                    _art.DrawPath(g, face, facePath);
                }
                else
                {
                    using (var br = new SolidBrush(Palette.Lift(_background, 0.16f)))
                        g.FillPath(br, facePath);
                }

                if (!_state.IsPlaying)
                {
                    if (_art != null)
                        using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                            g.FillPath(shade, facePath);
                    Glyphs.Pause(g, face.X + face.Width / 2f, face.Y + face.Height / 2f, face.Width * 0.45f, _art != null ? Color.White : ink);
                }
                else if (_art == null)
                {
                    Glyphs.Note(g, face.X + face.Width / 2f, face.Y + face.Height / 2f, face.Width, ink);
                }
            }
        }

        // ---- shape ---------------------------------------------------------------------

        // Corner radius of the disc's own outline, in pixels.
        float Radius(int side) { return RadiusFor(_shape, side); }

        internal static float RadiusFor(TileShape shape, int side)
        {
            switch (shape)
            {
                case TileShape.Circle: return side / 2f;
                case TileShape.Rounded: return side * RoundedFraction;
                default: return 0f;
            }
        }

        // A closed outline of r with the given corner radius. Clamped to half the side, which
        // is a circle: an inner outline of a circle or a heavily rounded square can ask for a
        // radius a little over that after the inset is taken off.
        internal static GraphicsPath ShapePath(Rectangle r, float radius)
        {
            var p = new GraphicsPath();
            float max = Math.Min(r.Width, r.Height) / 2f;
            if (radius > max) radius = max;
            if (radius <= 0.5f) { p.AddRectangle(r); return p; }
            float d = radius * 2;
            if (d >= Math.Min(r.Width, r.Height) - 0.01f) { p.AddEllipse(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // The first <frac> of the outline's length, walked clockwise from top centre, as one
        // open figure: DrawArc only knows circles, so a square or rounded progress ring has to
        // be traced by hand. Nine pieces (a half edge, then corner / edge / corner / edge /
        // corner / edge / corner, then the other half edge); each is laid down whole while the
        // budget lasts and cut short when it runs out, so the end sits exactly where the
        // fraction says. Lengths are in pixels so a corner and an edge advance at the same speed.
        static GraphicsPath OutlineProgress(Rectangle rc, float radius, double frac)
        {
            float max = Math.Min(rc.Width, rc.Height) / 2f;
            float R = radius > max ? max : (radius < 0 ? 0 : radius);
            if (R < 0.5f) R = 0;
            float x = rc.X, y = rc.Y, right = rc.Right, bottom = rc.Bottom, cx = x + rc.Width / 2f;
            float d = R * 2;
            float arcLen = (float)(Math.PI * R / 2);

            // kind 0 = line, 1 = arc. Lines: x1,y1 -> x2,y2. Arcs: bounding square x1,y1,d and start angle.
            float[][] seg = new float[][]
            {
                new float[] { 0, cx, y, right - R, y },
                new float[] { 1, right - d, y, 270 },
                new float[] { 0, right, y + R, right, bottom - R },
                new float[] { 1, right - d, bottom - d, 0 },
                new float[] { 0, right - R, bottom, x + R, bottom },
                new float[] { 1, x, bottom - d, 90 },
                new float[] { 0, x, bottom - R, x, y + R },
                new float[] { 1, x, y, 180 },
                new float[] { 0, x + R, y, cx, y },
            };

            double total = 0;
            for (int i = 0; i < seg.Length; i++) total += Length(seg[i], arcLen);

            var path = new GraphicsPath();
            double left = total * frac;
            bool started = false;
            for (int i = 0; i < seg.Length && left > 0; i++)
            {
                float[] s = seg[i];
                float len = Length(s, arcLen);
                if (len <= 0) continue;
                float t = left >= len ? 1f : (float)(left / len);
                if (s[0] == 0)
                {
                    path.AddLine(s[1], s[2], s[1] + (s[3] - s[1]) * t, s[2] + (s[4] - s[2]) * t);
                }
                else
                {
                    path.AddArc(s[1], s[2], d, d, s[3], 90f * t);
                }
                started = true;
                left -= len;
            }
            // An empty path makes DrawPath throw; a degenerate outline gets a dot instead.
            if (!started) path.AddLine(cx, y, cx + 0.01f, y);
            return path;
        }

        static float Length(float[] s, float arcLen)
        {
            if (s[0] == 1) return arcLen;
            float dx = s[3] - s[1], dy = s[4] - s[2];
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        // ---- input ---------------------------------------------------------------------

        void SetHover(bool on)
        {
            if (on == _hover) return;
            _hover = on;
            Invalidate();
            if (on) ShowButton(); else _hideTimer.Start();
        }

        // ---- hover button --------------------------------------------------------------

        void ShowButton()
        {
            _hideTimer.Stop();
            if (!_wantButton || _disposed || !_state.HasSession || !_state.CanPlayPause) return;
            Func<bool> hold = Suppressed;
            if (hold != null && hold()) return;

            if (_button == null)
            {
                _button = new HoverButton(_background, _dpiScale, _shape);
                _button.Clicked += delegate
                {
                    Action handler = PlayPauseClicked;
                    if (handler != null) handler();
                };
                _button.PointerEntered += delegate { _hideTimer.Stop(); };
                _button.PointerLeft += delegate { _hideTimer.Start(); };
            }
            Native.RECT r;
            Native.GetWindowRect(Handle, out r);
            _button.Show(new Rectangle(r.Left, r.Top, r.Width, r.Height), _state.IsPlaying);
        }

        void HideButton()
        {
            _hideTimer.Stop();
            if (_button != null) _button.Hide();
        }

        // Over the tile or the button still? Asked of the pointer's position, not of mouse
        // messages: a hide that waited for a leave message would miss the case where the
        // pointer went from one window to the other and the second one has not heard yet.
        void HideIfAway()
        {
            if (_button == null || !_button.IsShown) return;
            Point p = Cursor.Position;
            Native.RECT r;
            Native.GetWindowRect(Handle, out r);
            if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return;
            if (_button.Contains(p)) return;
            _button.Hide();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    Paint();
                    return;

                case Native.WM_ERASEBKGND:
                    // Painted whole in WM_PAINT from the back buffer; a default erase would
                    // flash the class background between frames.
                    m.Result = (IntPtr)1;
                    return;

                case Native.WM_SETCURSOR:
                    // A bare NativeWindow gets no default cursor handling (that is a Control
                    // behaviour), so without this the pointer keeps whatever cursor was active
                    // before it arrived - a taskbar resize arrow, a stray busy cursor. Same fix
                    // as DesktopSwitcher's strip (its commit 277b02a).
                    Cursor.Current = Cursors.Default;
                    m.Result = (IntPtr)1;
                    return;

                case Native.WM_MOUSEMOVE:
                    if (!_tracking)
                    {
                        // One WM_MOUSELEAVE, or a pointer leaving quickly leaves the halo lit.
                        var tme = new Native.TRACKMOUSEEVENT();
                        tme.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
                        tme.dwFlags = Native.TME_LEAVE;
                        tme.hwndTrack = Handle;
                        _tracking = Native.TrackMouseEvent(ref tme);
                    }
                    SetHover(true);
                    // Hover only changes once per visit, so a button held back by the panel (or
                    // hidden by a click) would stay away until the pointer left and came back.
                    // Any movement over the tile asks again; ShowButton is a no-op while shown.
                    if (_button == null || !_button.IsShown) ShowButton();
                    break;

                case Native.WM_MOUSELEAVE:
                    _tracking = false;
                    SetHover(false);
                    break;

                case Native.WM_LBUTTONUP:
                    {
                        // The click opens the panel, which sits where the button is.
                        HideButton();
                        Action handler = Clicked;
                        if (handler != null) handler();
                        return;
                    }

                case Native.WM_MBUTTONUP:
                    {
                        Action handler = MiddleClicked;
                        if (handler != null) handler();
                        return;
                    }

                case Native.WM_RBUTTONUP:
                    {
                        Native.RECT r;
                        Native.GetWindowRect(Handle, out r);
                        // A child's client origin is its window origin (no border).
                        Action<Point> handler = ContextMenuRequested;
                        if (handler != null)
                            handler(new Point(r.Left + Native.LoWord(m.LParam), r.Top + Native.HiWord(m.LParam)));
                        return;
                    }
            }
            base.WndProc(ref m);
        }
    }
}
