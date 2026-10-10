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

        /// <summary>
        /// Extra width at 96 DPI while the hover button is out, added on the LEFT: the right edge
        /// stays against the tray, so the disc does not move when the button appears.
        /// </summary>
        const int SlotWidth = 27;

        // Authored at 96 DPI, scaled by _dpiScale.
        const int ButtonDiameter = 24;
        const int ButtonGap = 3;
        const int SlideMs = 130;
        const int Diameter = 30;
        const int RingWidth = 2;
        const int ArtInset = 4;

        // Corner radius of the rounded shape, as a fraction of the disc's side. Large enough to
        // read as clearly not a circle, small enough to read as clearly not a square.
        const float RoundedFraction = 0.30f;

        readonly double _dpiScale;
        readonly Color _background;
        readonly TileShape _shape;

        MediaState _state = MediaState.None;
        AlbumArt _art;
        Bitmap _buffer;
        bool _disposed;
        bool _hover;
        bool _tracking;

        // The hover button. The window grows once, to its full width, when the pointer arrives and
        // shrinks once when the slide back has finished; the slide itself is only painting. Resizing
        // a child of Explorer's window every frame would repaint the taskbar under it each time.
        readonly bool _slide;
        readonly FramePump _pump;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        long _lastStep;
        float _t;               // 0 = button hidden behind the disc, 1 = fully out
        bool _wide;             // the window is currently slot-wide
        bool _buttonHot;

        /// <summary>Left click: the Controller toggles the panel.</summary>
        public event Action Clicked;

        /// <summary>Middle click: play/pause.</summary>
        public event Action MiddleClicked;

        /// <summary>The slid-out play/pause button was clicked.</summary>
        public event Action PlayPauseClicked;

        /// <summary>Right click, at the pointer in screen coordinates.</summary>
        public event Action<Point> ContextMenuRequested;

        public MediaTile(IntPtr parent, Rectangle bounds, Color background, double dpiScale, TileShape shape, bool slide)
        {
            _shape = shape;
            _slide = slide;
            _dpiScale = dpiScale;
            _background = background;

            var cp = new CreateParams();
            cp.Caption = "MediaControllerTile";
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
            _pump = new FramePump(Handle, "MediaTile frames");
        }

        /// <summary>
        /// Window width in pixels right now. The Controller positions the tile by its right edge
        /// and needs the live width, or its once-a-second re-assert would shrink a wide tile.
        /// </summary>
        public int PixelWidth { get { return Px(Width) + (_wide ? Px(SlotWidth) : 0); } }

        /// <summary>The art disc's cell (the base 40 px, right-aligned) in screen coordinates.</summary>
        public Rectangle DiscScreenBounds()
        {
            Native.RECT r;
            Native.GetWindowRect(Handle, out r);
            int w = Px(Width);
            return new Rectangle(r.Right - w, r.Top, w, r.Height);
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
            // A session ending while the button is out sends it back.
            if (_wide || _hover) Kick();
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
            _pump.Dispose();
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
            // The disc is placed from the RIGHT edge: the window is wider than its base while the
            // hover button is out, and the disc must stay where it was.
            int baseW = Px(Width);
            var disc = new Rectangle(w - baseW + (baseW - d) / 2, (h - d) / 2, d, d);

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

            // The hover button, behind the disc: it slides out from under it. The disc's cell is
            // filled first (in the halo colour, which is what the disc sits on while hovered) so
            // the button cannot show through the gap between the ring and the art.
            if (_t > 0)
            {
                DrawButton(g, disc, outer, d, ink);
                using (var br = new SolidBrush(_hover ? Palette.Lift(_background, 0.14f) : _background))
                using (var path = ShapePath(disc, outer))
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

        // Rests just left of the disc and starts hidden at its centre; alpha follows the slide so
        // it is not a hard-edged disc popping out from behind another.
        void DrawButton(Graphics g, Rectangle disc, float discRadius, int d, Color ink)
        {
            int bd = Px(ButtonDiameter);
            float rest = disc.X - Px(ButtonGap) - bd / 2f;
            float cx = disc.X + d / 2f + (rest - (disc.X + d / 2f)) * _t;
            float cy = disc.Y + d / 2f;
            var rect = new Rectangle((int)Math.Round(cx - bd / 2f), (int)Math.Round(cy - bd / 2f), bd, bd);
            int a = (int)Math.Round(255 * _t);

            Color fill = Palette.Lift(_background, _buttonHot ? 0.30f : 0.18f);
            using (var br = new SolidBrush(Color.FromArgb(a, fill)))
            using (var path = ShapePath(rect, Radius(bd)))
                g.FillPath(br, path);

            Color glyph = Color.FromArgb(a, ink);
            if (_state.IsPlaying)
                Glyphs.Pause(g, rect.X + bd / 2f, rect.Y + bd / 2f, bd * 0.42f, glyph);
            else
                Glyphs.Play(g, rect.X + bd / 2f, rect.Y + bd / 2f, bd * 0.42f, glyph);
        }

        // ---- slide ---------------------------------------------------------------------

        bool Slidable { get { return _slide && _state.HasSession && _state.CanPlayPause; } }

        // Starts whatever the current hover/session state calls for: grow the window and run the
        // pump toward out, or run it toward back. Idle (settled) costs nothing: no pump runs.
        void Kick()
        {
            if (_disposed || Handle == IntPtr.Zero) return;
            bool want = _hover && Slidable;
            if (want && !_wide) Resize(true);
            if ((want ? _t < 1f : (_t > 0f || _wide)) && !_pump.Running)
            {
                _lastStep = _clock.ElapsedMilliseconds;
                _pump.Start();
            }
        }

        // Taken at the top of a paint, by measured time (see Motion.Rate).
        void Step()
        {
            if (!_pump.Running) return;
            long now = _clock.ElapsedMilliseconds;
            int dt = (int)Math.Min(now - _lastStep, 50);
            _lastStep = now;

            float target = (_hover && Slidable) ? 1f : 0f;
            Motion.Ease(ref _t, target, Motion.Rate(dt, SlideMs), Motion.ToneEpsilon);
            if (_t == target)
            {
                _pump.Stop();
                if (target == 0f) { _buttonHot = false; Resize(false); }
            }
        }

        // Grows or shrinks the window leftward, keeping its right edge where it is.
        void Resize(bool wide)
        {
            if (wide == _wide) return;
            IntPtr parent = Native.GetParent(Handle);
            if (parent == IntPtr.Zero) return;
            Native.RECT r;
            Native.GetWindowRect(Handle, out r);
            _wide = wide;
            int newW = PixelWidth;
            var pt = new Native.POINT();
            pt.X = r.Right - newW;
            pt.Y = r.Top;
            Native.ScreenToClient(parent, ref pt);
            // NOCOPYBITS: by default Windows keeps the old pixels and pastes them at the new
            // window's top-left until the next paint. Growing leftward that puts the disc 34 px
            // too far left for a frame, and shrinking shows the button where the disc should be -
            // seen as the tile jumping sideways each time the pointer crossed it. Discarding them
            // leaves the whole client invalid, so the first paint is complete.
            Native.SetWindowPos(Handle, IntPtr.Zero, pt.X, pt.Y, newW, r.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOCOPYBITS);
            // Growing happens from a mouse message, so paint the new size now rather than at the
            // next idle. Shrinking happens inside a WM_PAINT that is about to paint it anyway.
            if (wide) Native.UpdateWindow(Handle);
        }

        // ---- shape ---------------------------------------------------------------------

        // Corner radius of the disc's own outline, in pixels.
        float Radius(int side)
        {
            switch (_shape)
            {
                case TileShape.Circle: return side / 2f;
                case TileShape.Rounded: return side * RoundedFraction;
                default: return 0f;
            }
        }

        // A closed outline of r with the given corner radius. Clamped to half the side, which
        // is a circle: an inner outline of a circle or a heavily rounded square can ask for a
        // radius a little over that after the inset is taken off.
        static GraphicsPath ShapePath(Rectangle r, float radius)
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

        // Is a client x over the slid-out button? Everything left of the disc's cell, plus the
        // gap up to the disc, belongs to it.
        bool OnButton(int x)
        {
            return _wide && x < ClientWidth() - Px(Width) + Px(ButtonGap) - 1;
        }

        int ClientWidth()
        {
            Native.RECT r;
            Native.GetWindowRect(Handle, out r);
            return r.Width;
        }

        void SetHover(bool on)
        {
            if (on == _hover) return;
            _hover = on;
            if (!on) _buttonHot = false;
            Invalidate();
            Kick();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    Step();
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
                    {
                        bool hot = _t > 0.4f && OnButton(Native.LoWord(m.LParam));
                        if (hot != _buttonHot) { _buttonHot = hot; Invalidate(); }
                    }
                    break;

                case Native.WM_MOUSELEAVE:
                    _tracking = false;
                    SetHover(false);
                    break;

                case Native.WM_LBUTTONUP:
                    {
                        // Only once the button is mostly out: a click while it is still emerging
                        // from behind the disc means the disc.
                        Action handler = (_t > 0.4f && OnButton(Native.LoWord(m.LParam))) ? PlayPauseClicked : Clicked;
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
