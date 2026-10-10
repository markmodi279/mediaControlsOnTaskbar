using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MediaController
{
    /// <summary>
    /// The play/pause button that appears over the tile while the pointer is on it.
    ///
    /// A top-level popup, not part of the tile: the tile is a child of the taskbar and is only
    /// as tall as the taskbar, so anything above it has to be a window of its own. Same recipe
    /// as the panel - topmost, tool window, layered, and never activated, so clicking it does
    /// not take focus from what the user is working in - and it sits on the side of the tile
    /// away from the screen edge, like the panel does.
    ///
    /// Unlike the panel it is drawn with per-pixel alpha (UpdateLayeredWindow), not a window
    /// region: it is a 30 px circle or rounded square and a region's edge is not anti-aliased,
    /// which at that size looks like a bitten coin. The panel can live with a region because
    /// its corners are a small part of a large card. Fade and rise are by measured time, from a
    /// FramePump that exists only while it moves (see FramePump and Motion).
    ///
    /// It knows nothing about the pointer's relationship to the tile. It reports when the
    /// pointer enters and leaves itself, and the tile decides when to hide it.
    /// </summary>
    sealed class HoverButton : NativeWindow, IDisposable
    {
        // Authored at 96 DPI.
        const int Size = 30;
        const int Gap = 4;
        const int Rise = 6;
        const int AnimMs = 120;

        readonly Color _taskbar;
        readonly double _dpiScale;
        readonly TileShape _shape;
        readonly FramePump _pump;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        Bitmap _face;
        IntPtr _memDc;
        IntPtr _hBitmap;
        IntPtr _oldBitmap;
        bool _dirty = true;

        Point _home;
        bool _below;
        bool _open;
        bool _visible;
        bool _playing;
        bool _hot;
        bool _tracking;
        bool _disposed;
        float _t;
        long _lastStep;

        public event Action Clicked;
        public event Action PointerEntered;
        public event Action PointerLeft;

        public HoverButton(Color taskbarColor, double dpiScale, TileShape shape)
        {
            _taskbar = taskbarColor;
            _dpiScale = dpiScale;
            _shape = shape;

            var cp = new CreateParams();
            cp.Caption = "MediaControllerButton";
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
            cp.Width = Px(Size);
            cp.Height = Px(Size);
            CreateHandle(cp);

            _pump = new FramePump(Handle, "HoverButton frames");
        }

        int Px(int v) { return (int)Math.Round(v * _dpiScale); }

        /// <summary>Shown, or on its way in. False once it has finished fading out.</summary>
        public bool IsShown { get { return _open; } }

        public bool Contains(Point screen)
        {
            if (!_visible) return false;
            Native.RECT r;
            Native.GetWindowRect(Handle, out r);
            return screen.X >= r.Left && screen.X < r.Right && screen.Y >= r.Top && screen.Y < r.Bottom;
        }

        // ---- showing and hiding --------------------------------------------------------

        /// <summary>Opens beside <paramref name="tile"/> (screen pixels): above it, or below for a top taskbar.</summary>
        public void Show(Rectangle tile, bool playing)
        {
            if (_disposed) return;
            if (playing != _playing) { _playing = playing; _dirty = true; }
            if (_open) return;

            int s = Px(Size), gap = Px(Gap);
            Screen scr = Screen.FromRectangle(tile);
            Rectangle wa = scr.WorkingArea;
            _below = tile.Top + tile.Height / 2 < scr.Bounds.Top + scr.Bounds.Height / 2;

            int x = tile.Left + tile.Width / 2 - s / 2;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - s));
            int y = _below ? tile.Bottom + gap : tile.Top - s - gap;
            _home = new Point(x, y);

            _open = true;
            if (!_visible)
            {
                _t = 0;
                Present();
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                _visible = true;
            }
            BeginAnimation();
        }

        public void Hide()
        {
            if (!_open) return;
            _open = false;
            _hot = false;
            _dirty = true;
            if (!_visible) return;
            BeginAnimation();
        }

        /// <summary>Swaps the glyph in place when playback changes under a shown button.</summary>
        public void SetPlaying(bool playing)
        {
            if (playing == _playing) return;
            _playing = playing;
            _dirty = true;
            if (_visible && !_pump.Running) Present();
        }

        void BeginAnimation()
        {
            _lastStep = _clock.ElapsedMilliseconds;
            if (!_pump.Running) _pump.Start();
        }

        // Displacement from the resting place: toward the tile, shrinking to nothing on arrival.
        int Offset()
        {
            int d = (int)Math.Round(Px(Rise) * (1f - _t));
            return _below ? -d : d;
        }

        // One animation step, taken when a frame arrives. By elapsed time, not frame count.
        void Step()
        {
            if (!_pump.Running) return;
            long now = _clock.ElapsedMilliseconds;
            int dt = (int)Math.Min(now - _lastStep, 50);
            _lastStep = now;

            float target = _open ? 1f : 0f;
            Motion.Ease(ref _t, target, Motion.Rate(dt, AnimMs), Motion.ToneEpsilon);
            Present();

            if (_t == target)
            {
                _pump.Stop();
                if (!_open)
                {
                    _visible = false;
                    Native.ShowWindow(Handle, Native.SW_HIDE);
                }
            }
        }

        // ---- painting ------------------------------------------------------------------

        // Pushes the picture, the position and the overall alpha in one call. The face is redrawn
        // only when what it shows changes (glyph, hot), not per frame of the fade.
        void Present()
        {
            if (Handle == IntPtr.Zero) return;
            int s = Px(Size);

            if (_dirty || _face == null)
            {
                if (_face == null) _face = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(_face))
                    Draw(g, s);
                _dirty = false;

                if (_memDc == IntPtr.Zero) _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
                IntPtr fresh = _face.GetHbitmap(Color.FromArgb(0));
                IntPtr old = Native.SelectObject(_memDc, fresh);
                if (_hBitmap == IntPtr.Zero) _oldBitmap = old;   // the DC's own 1x1, to put back on dispose
                else Native.DeleteObject(_hBitmap);
                _hBitmap = fresh;
            }

            var dst = new Native.POINT();
            dst.X = _home.X;
            dst.Y = _home.Y + Offset();
            var size = new Native.SIZE();
            size.cx = s;
            size.cy = s;
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION();
            blend.BlendOp = 0;   // AC_SRC_OVER
            blend.SourceConstantAlpha = (byte)Math.Round(255f * _t);
            blend.AlphaFormat = 1;   // AC_SRC_ALPHA: the bitmap carries its own alpha
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }

        void Draw(Graphics g, int s)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // The same card colours as the panel, so the two read as one family.
            bool light = Palette.IsLight(_taskbar);
            Color card = light ? Color.FromArgb(244, 244, 246) : Color.FromArgb(30, 30, 33);
            Color face = _hot ? Palette.Lift(card, 0.12f) : card;
            Color ink = Palette.PrimaryText(card);

            var rect = new Rectangle(1, 1, s - 2, s - 2);
            using (var path = MediaTile.ShapePath(rect, MediaTile.RadiusFor(_shape, rect.Width)))
            using (var br = new SolidBrush(face))
            using (var edge = new Pen(Palette.Lift(card, 0.20f)))
            {
                g.FillPath(br, path);
                g.DrawPath(edge, path);
            }

            float c = s / 2f;
            if (_playing) Glyphs.Pause(g, c, c, s * 0.40f, ink);
            else Glyphs.Play(g, c, c, s * 0.40f, ink);
        }

        void SetHot(bool hot)
        {
            if (hot == _hot) return;
            _hot = hot;
            _dirty = true;
            if (_visible && !_pump.Running) Present();
        }

        // ---- input ---------------------------------------------------------------------

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    {
                        // A layered window drawn with UpdateLayeredWindow has nothing for GDI to
                        // paint; the pump's invalidation is only the beat. Validate it, then step.
                        Native.PAINTSTRUCT ps;
                        Native.BeginPaint(Handle, out ps);
                        Native.EndPaint(Handle, ref ps);
                        Step();
                        return;
                    }

                case Native.WM_ERASEBKGND:
                    m.Result = (IntPtr)1;
                    return;

                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;

                case Native.WM_SETCURSOR:
                    Cursor.Current = Cursors.Default;
                    m.Result = (IntPtr)1;
                    return;

                case Native.WM_MOUSEMOVE:
                    if (!_tracking)
                    {
                        var tme = new Native.TRACKMOUSEEVENT();
                        tme.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
                        tme.dwFlags = Native.TME_LEAVE;
                        tme.hwndTrack = Handle;
                        _tracking = Native.TrackMouseEvent(ref tme);
                        Action entered = PointerEntered;
                        if (entered != null) entered();
                    }
                    SetHot(true);
                    return;

                case Native.WM_MOUSELEAVE:
                    _tracking = false;
                    SetHot(false);
                    {
                        Action left = PointerLeft;
                        if (left != null) left();
                    }
                    return;

                case Native.WM_LBUTTONUP:
                    {
                        Action handler = Clicked;
                        if (handler != null) handler();
                        return;
                    }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pump.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
            if (_memDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero) Native.SelectObject(_memDc, _oldBitmap);
                Native.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_hBitmap != IntPtr.Zero) { Native.DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
            if (_face != null) { _face.Dispose(); _face = null; }
        }
    }
}
