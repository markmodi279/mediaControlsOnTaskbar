using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MediaController
{
    /// <summary>
    /// The mark on the taskbar: a round tile showing the album art, with a thin ring that
    /// fills as the track plays. Everything else lives in the panel it opens.
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

        readonly double _dpiScale;
        readonly Color _background;

        MediaState _state = MediaState.None;
        AlbumArt _art;
        Bitmap _buffer;
        bool _disposed;
        bool _hover;
        bool _tracking;

        /// <summary>Left click: the Controller toggles the panel.</summary>
        public event Action Clicked;

        /// <summary>Middle click: play/pause.</summary>
        public event Action MiddleClicked;

        /// <summary>Right click, at the pointer in screen coordinates.</summary>
        public event Action<Point> ContextMenuRequested;

        public MediaTile(IntPtr parent, Rectangle bounds, Color background, double dpiScale)
        {
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

            // Hover: a soft halo behind the disc, lifted from the taskbar colour so it reads
            // on a light or dark bar alike.
            if (_hover)
            {
                var halo = Rectangle.Inflate(disc, Px(3), Px(3));
                using (var br = new SolidBrush(Palette.Lift(_background, 0.14f)))
                    g.FillEllipse(br, halo);
            }

            // Ring: a faint full circle, with the played part over it in the accent.
            int pen = Math.Max(2, Px(RingWidth));
            var ringRect = new Rectangle(disc.X + pen / 2, disc.Y + pen / 2, disc.Width - pen, disc.Height - pen);
            using (var track = new Pen(Palette.Lift(_background, live ? 0.22f : 0.12f), pen))
                g.DrawEllipse(track, ringRect);
            double frac = Fraction();
            if (live && frac > 0)
            {
                using (var played = new Pen(_state.IsPlaying ? accent : Palette.Blend(accent, _background, 0.4f), pen))
                {
                    played.StartCap = LineCap.Round;
                    played.EndCap = LineCap.Round;
                    g.DrawArc(played, ringRect, -90f, (float)(360.0 * frac));
                }
            }

            // The face, inside the ring.
            int inset = pen + Px(ArtInset) - 2;
            var face = Rectangle.Inflate(disc, -inset, -inset);
            if (!live)
            {
                Glyphs.Note(g, face.X + face.Width / 2f, face.Y + face.Height / 2f, face.Width, dim);
                return;
            }

            if (_art != null)
            {
                _art.DrawCircle(g, face);
            }
            else
            {
                using (var br = new SolidBrush(Palette.Lift(_background, 0.16f)))
                    g.FillEllipse(br, face);
            }

            if (!_state.IsPlaying)
            {
                if (_art != null)
                    using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                        g.FillEllipse(shade, face);
                Glyphs.Pause(g, face.X + face.Width / 2f, face.Y + face.Height / 2f, face.Width * 0.45f, _art != null ? Color.White : ink);
            }
            else if (_art == null)
            {
                Glyphs.Note(g, face.X + face.Width / 2f, face.Y + face.Height / 2f, face.Width, ink);
            }
        }

        // ---- input ---------------------------------------------------------------------

        void SetHover(bool on)
        {
            if (on == _hover) return;
            _hover = on;
            Invalidate();
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
                    break;

                case Native.WM_MOUSELEAVE:
                    _tracking = false;
                    SetHover(false);
                    break;

                case Native.WM_LBUTTONUP:
                    {
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
