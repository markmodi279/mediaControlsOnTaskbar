using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MediaController
{
    /// <summary>
    /// The card that opens above the tile: album art, title, artist, the player, three
    /// transport buttons and a seek bar with elapsed and total time.
    ///
    /// A top-level popup, owner-drawn, a bare NativeWindow. Two things set it apart from the
    /// hover panel in DesktopSwitcher, which it otherwise resembles:
    ///
    ///   It is clickable. That one is click-through; this one has buttons and a bar to drag.
    ///
    ///   It still never takes focus. WS_EX_NOACTIVATE and WM_MOUSEACTIVATE -> MA_NOACTIVATE
    ///   mean a click on a button does not steal the keyboard from the app the user is
    ///   working in - the same reason the tile is no-activate. The price is that it cannot
    ///   receive keys, so there is no Esc to close it; it closes on a click anywhere else
    ///   (ClickWatcher) and on the tile's own click. A global Esc hotkey was rejected: it
    ///   would break Esc in every other program while the panel was open.
    ///
    /// Mouse clicks are still delivered to a no-activate window. The seek drag uses
    /// SetCapture so it keeps tracking when the pointer leaves the card.
    ///
    /// Opening and closing slide a few pixels toward the taskbar and fade. It is a layered
    /// window, pushed with UpdateLayeredWindow so the four rounded corners are anti-aliased
    /// (a window region cannot do that). Text stays ClearType: it is drawn onto opaque pixels
    /// of the back buffer, and only afterwards are the corner pixels given their alpha.
    /// </summary>
    sealed class MediaPanel : NativeWindow, IDisposable
    {
        public const int Previous = 0;
        public const int PlayPause = 1;
        public const int Next = 2;

        // Authored at 96 DPI, scaled by _dpiScale.
        const int W = 340;
        const int H = 152;
        const int Pad = 14;
        const int ArtSize = 84;
        const int TextGap = 14;
        const int ButtonY = 88;
        const int PlayDiameter = 36;
        const int ButtonStep = 46;
        const int BarY = 124;
        const int SlideDistance = 8;
        const int AnimMs = 140;
        const int EdgeGap = 8;
        const int SeekHold = 1200;   // ms the user's chosen position stands in for the player's

        const int ClickedAnywhere = Native.WM_APP + 1;

        readonly double _dpiScale;
        readonly Color _taskbar;
        readonly Font _titleFont, _bodyFont, _smallFont;
        readonly FramePump _pump;
        readonly Timer _barTimer = new Timer();
        readonly Stopwatch _clock = Stopwatch.StartNew();

        MediaState _state = MediaState.None;
        AlbumArt _art;
        Bitmap _buffer;
        int[] _pixels;       // the buffer's pixels, copied out to fix up alpha
        byte[] _mask;        // rounded-rect coverage, 255 inside, per pixel of the buffer
        IntPtr _memDc, _hBitmap, _oldBitmap;
        ClickWatcher _watcher;
        bool _disposed;

        bool _open;          // the target: should it be showing
        bool _visible;       // the window is shown (true while fading out too)
        float _t;            // 0 = gone, 1 = fully open
        long _lastStep;
        Rectangle _tileRect;
        Point _home;
        bool _below;

        int _hot = -1;       // button under the pointer
        int _down = -1;      // button the left button went down on
        bool _overBar;
        bool _tracking;
        bool _drag;
        double _dragFrac;
        bool _optimistic;
        int _optUntil;
        TimeSpan _optPos;

        /// <summary>A transport button was clicked: Previous, PlayPause or Next.</summary>
        public event Action<int> ButtonClicked;

        /// <summary>The user released the seek bar at this position from the start of the track.</summary>
        public event Action<TimeSpan> SeekRequested;

        public MediaPanel(Color taskbarColor, double dpiScale, int cornerRadius)
        {
            _radius = cornerRadius;
            _taskbar = taskbarColor;
            _dpiScale = dpiScale;
            _titleFont = MakeFont("Segoe UI Semibold", 15, FontStyle.Regular);
            _bodyFont = MakeFont("Segoe UI", 13, FontStyle.Regular);
            _smallFont = MakeFont("Segoe UI", 11.5f, FontStyle.Regular);

            var cp = new CreateParams();
            cp.Caption = "MediaControllerPanel";
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
            cp.Width = Px(W);
            cp.Height = Px(H);
            CreateHandle(cp);
            // No SetLayeredWindowAttributes: once it has been called the window can no longer
            // be updated with UpdateLayeredWindow.

            _pump = new FramePump(Handle, "MediaPanel frames");

            // The seek bar moves while a track plays. 20 Hz is plenty for a bar a few hundred
            // pixels long (it advances a pixel in under a second) and costs nothing while
            // paused or closed. The pump is for the slide and fade only.
            _barTimer.Interval = 50;
            _barTimer.Tick += delegate
            {
                if (_visible && _state.IsPlaying && !_drag && !_pump.Running) Invalidate();
            };
        }

        // Corner radius at 96 DPI; 0 leaves the card square.
        readonly int _radius;

        // A window region was tried first and left a hard staircase on the corners that no
        // border drawn over it could hide, so the card is per-pixel alpha instead. Only the
        // corners are partly transparent; every other pixel is opaque, so ClearType survives.
        void BuildMask(int w, int h)
        {
            if (_mask != null && _mask.Length == w * h) return;
            _mask = new byte[w * h];
            if (_radius <= 0)
            {
                for (int i = 0; i < _mask.Length; i++) _mask[i] = 255;
                return;
            }
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using (var path = AlbumArt.RoundedRect(new Rectangle(0, 0, w, h), Px(_radius)))
                        g.FillPath(Brushes.White, path);
                }
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var px = new int[w * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
                bmp.UnlockBits(data);
                for (int i = 0; i < px.Length; i++) _mask[i] = (byte)(px[i] & 0xFF);
            }
        }

        Font MakeFont(string name, float px, FontStyle style)
        {
            return new Font(name, (float)(px * _dpiScale), style, GraphicsUnit.Pixel);
        }

        int Px(int v) { return (int)Math.Round(v * _dpiScale); }

        public bool IsOpen { get { return _open; } }

        // ---- showing and hiding --------------------------------------------------------

        /// <summary>Opens above (or below, for a top taskbar) the tile at <paramref name="tile"/>, in screen pixels.</summary>
        public void Open(Rectangle tile)
        {
            if (_open || _disposed) return;
            _tileRect = tile;

            int w = Px(W), h = Px(H), gap = Px(EdgeGap);
            Screen scr = Screen.FromRectangle(tile);
            Rectangle wa = scr.WorkingArea;
            _below = tile.Top + tile.Height / 2 < scr.Bounds.Top + scr.Bounds.Height / 2;

            int x = tile.Left + tile.Width / 2 - w / 2;
            x = Math.Max(wa.Left + gap, Math.Min(x, wa.Right - w - gap));
            int y = _below ? tile.Bottom + gap : tile.Top - h - gap;
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            _home = new Point(x, y);

            _open = true;
            _hot = _down = -1;

            // Installed only while open, so the rest of the day the app has no hook in the system.
            _watcher = new ClickWatcher(Handle, ClickedAnywhere);
            _barTimer.Start();

            if (!_visible)
            {
                _t = 0;
                // Pushed at alpha 0 before it is shown, or the last frame of the previous
                // fade flashes up for a moment.
                Present();
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y + Offset(), w, h,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                _visible = true;
            }
            BeginAnimation();
        }

        /// <summary>Closes it; <paramref name="immediate"/> skips the fade (Explorer restarts, exit).</summary>
        public void Close(bool immediate)
        {
            if (!_open && !(immediate && _visible)) return;
            _open = false;
            EndDrag(false);
            if (_watcher != null) { _watcher.Dispose(); _watcher = null; }
            _barTimer.Stop();

            if (immediate || !_visible)
            {
                _pump.Stop();
                _t = 0;
                HideNow();
            }
            else
            {
                BeginAnimation();
            }
        }

        void HideNow()
        {
            if (!_visible) return;
            _visible = false;
            if (Handle != IntPtr.Zero) Native.ShowWindow(Handle, Native.SW_HIDE);
        }

        void BeginAnimation()
        {
            _lastStep = _clock.ElapsedMilliseconds;
            if (!_pump.Running) _pump.Start();
        }

        // How far the card is displaced from its resting place: toward the taskbar, shrinking to
        // nothing as it arrives.
        int Offset()
        {
            int d = (int)Math.Round(Px(SlideDistance) * (1f - _t));
            return _below ? -d : d;
        }

        // One animation step, taken at the top of a paint. Stepped by measured time, not by
        // frame count (see Motion.Rate), so a late frame lands where it should.
        void Step()
        {
            if (!_pump.Running) return;
            long now = _clock.ElapsedMilliseconds;
            int dt = (int)Math.Min(now - _lastStep, 50);
            _lastStep = now;

            float target = _open ? 1f : 0f;
            Motion.Ease(ref _t, target, Motion.Rate(dt, AnimMs), Motion.ToneEpsilon);

            if (_t == target)
            {
                _pump.Stop();
                if (!_open) HideNow();
            }
        }

        // ---- state ---------------------------------------------------------------------

        /// <summary>Replaces what is shown. Cheap enough to call every second. The art is borrowed.</summary>
        public void SetState(MediaState state, AlbumArt art)
        {
            _state = state ?? MediaState.None;
            _art = art;
            if (_visible) Invalidate();
        }

        void Invalidate()
        {
            if (Handle != IntPtr.Zero) Native.InvalidateRect(Handle, IntPtr.Zero, false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _barTimer.Stop();
            _barTimer.Dispose();
            _pump.Dispose();
            if (_watcher != null) { _watcher.Dispose(); _watcher = null; }
            if (Handle != IntPtr.Zero) DestroyHandle();
            if (_memDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero) Native.SelectObject(_memDc, _oldBitmap);
                Native.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_hBitmap != IntPtr.Zero) { Native.DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
            if (_buffer != null) _buffer.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
        }

        // ---- geometry ------------------------------------------------------------------

        Rectangle ArtRect { get { return new Rectangle(Px(Pad), Px(Pad), Px(ArtSize), Px(ArtSize)); } }

        int TextLeft { get { return Px(Pad + ArtSize + TextGap); } }

        Point ButtonCenter(int button)
        {
            return new Point(TextLeft + Px(16) + button * Px(ButtonStep), Px(ButtonY));
        }

        int ButtonRadius(int button) { return Px(button == PlayPause ? PlayDiameter / 2 : 18); }

        int HitButton(int x, int y)
        {
            for (int i = 0; i < 3; i++)
            {
                Point c = ButtonCenter(i);
                int r = ButtonRadius(i) + Px(2);
                if ((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y) <= r * r) return i;
            }
            return -1;
        }

        int BarLeft { get { return Px(Pad); } }
        int BarRight { get { return Px(W - Pad); } }

        bool HitBar(int x, int y)
        {
            return _state.CanSeek && x >= BarLeft - Px(6) && x <= BarRight + Px(6) && Math.Abs(y - Px(BarY)) <= Px(11);
        }

        double FractionAt(int x)
        {
            double f = (x - BarLeft) / (double)(BarRight - BarLeft);
            return f < 0 ? 0 : (f > 1 ? 1 : f);
        }

        bool Enabled(int button)
        {
            if (!_state.HasSession) return false;
            switch (button)
            {
                case Previous: return _state.CanPrevious;
                case Next: return _state.CanNext;
                default: return _state.CanPlayPause;
            }
        }

        // The position to show: the one being dragged to, then for a moment the one just
        // chosen (the player takes a beat to report it, and snapping back meanwhile looks like
        // the seek failed), otherwise the snapshot advanced to now.
        TimeSpan DisplayPosition()
        {
            TimeSpan dur = _state.Duration;
            if (_drag) return TimeSpan.FromSeconds(_dragFrac * dur.TotalSeconds);
            if (_optimistic)
            {
                if (unchecked(Environment.TickCount - _optUntil) < 0) return _optPos;
                _optimistic = false;
            }
            TimeSpan pos = _state.Position;
            if (_state.IsPlaying) pos += TimeSpan.FromMilliseconds(unchecked(Environment.TickCount - _state.TakenAt));
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (dur > TimeSpan.Zero && pos > dur) pos = dur;
            return pos;
        }

        // ---- painting ------------------------------------------------------------------

        // Draws the card, gives the corners their alpha, and pushes the picture, the position
        // and the fade alpha to the window in one call.
        void Present()
        {
            if (Handle == IntPtr.Zero || _disposed) return;
            int w = Px(W), h = Px(H);
            if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
            {
                if (_buffer != null) _buffer.Dispose();
                _buffer = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                _pixels = new int[w * h];
            }
            BuildMask(w, h);
            using (Graphics g = Graphics.FromImage(_buffer))
                Draw(g, w, h);

            // GDI text writes zero into the alpha byte, so alpha is set for every pixel here:
            // opaque inside, the mask's coverage (and premultiplied colour) in the corners.
            var data = _buffer.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadWrite,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length);
            for (int i = 0; i < _pixels.Length; i++)
            {
                int a = _mask[i];
                int p = _pixels[i];
                if (a == 255)
                {
                    _pixels[i] = p | unchecked((int)0xFF000000);
                }
                else
                {
                    int r = ((p >> 16) & 0xFF) * a / 255, gr = ((p >> 8) & 0xFF) * a / 255, b = (p & 0xFF) * a / 255;
                    _pixels[i] = (a << 24) | (r << 16) | (gr << 8) | b;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(_pixels, 0, data.Scan0, _pixels.Length);
            _buffer.UnlockBits(data);

            if (_memDc == IntPtr.Zero) _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
            IntPtr fresh = _buffer.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(_memDc, fresh);
            if (_hBitmap == IntPtr.Zero) _oldBitmap = old;   // the DC's own 1x1, to put back on dispose
            else Native.DeleteObject(_hBitmap);
            _hBitmap = fresh;

            var dst = new Native.POINT();
            dst.X = _home.X;
            dst.Y = _home.Y + Offset();
            var size = new Native.SIZE();
            size.cx = w;
            size.cy = h;
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION();
            blend.BlendOp = 0;   // AC_SRC_OVER
            blend.SourceConstantAlpha = (byte)Math.Round(255f * _t);
            blend.AlphaFormat = 1;   // AC_SRC_ALPHA: the bitmap carries its own alpha
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }

        static string Time(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            return ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }

        const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding
            | TextFormatFlags.EndEllipsis;

        void Draw(Graphics g, int w, int h)
        {
            bool live = _state.HasSession;

            // The card follows the taskbar's light or dark, tinted toward the art's accent.
            // No art, no tint worth speaking of: a neutral slate instead of a random colour.
            bool light = Palette.IsLight(_taskbar);
            Color baseColor = light ? Color.FromArgb(244, 244, 246) : Color.FromArgb(30, 30, 33);
            Color accent = (live && _art != null) ? _art.Accent : Color.FromArgb(120, 125, 145);
            Color card = Palette.Blend(baseColor, accent, (live && _art != null) ? (light ? 0.16f : 0.20f) : 0.08f);
            Color ink = Palette.PrimaryText(card);
            Color second = Palette.SecondaryText(card);
            Color muted = Palette.MutedText(card);

            g.Clear(card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // A 1 px border just inside the rounded edge; the corners outside it are made
            // transparent by Present, anti-aliased.
            using (var edge = new Pen(Palette.Lift(card, 0.14f)))
            {
                if (_radius <= 0)
                    g.DrawRectangle(edge, 0, 0, w - 1, h - 1);
                else
                    using (var path = AlbumArt.RoundedRect(new Rectangle(0, 0, w - 1, h - 1), Px(_radius)))
                        g.DrawPath(edge, path);
            }

            // Art, or a quiet placeholder.
            Rectangle art = ArtRect;
            if (live && _art != null)
            {
                _art.DrawRounded(g, art, Px(10));
            }
            else
            {
                using (var br = new SolidBrush(Palette.Lift(card, 0.10f)))
                using (var path = AlbumArt.RoundedRect(art, Px(10)))
                    g.FillPath(br, path);
                Glyphs.Note(g, art.X + art.Width / 2f, art.Y + art.Height / 2f, art.Width * 0.5f, muted);
            }

            // Text: title, artist, then the player.
            int tl = TextLeft;
            int tw = w - tl - Px(Pad);
            string title = !live ? "Nothing playing" : (_state.Title.Length > 0 ? _state.Title : _state.Artist);
            string artist = live && _state.Title.Length > 0 ? _state.Artist : "";
            TextRenderer.DrawText(g, title, _titleFont, new Rectangle(tl, Px(Pad - 2), tw, Px(24)), live ? ink : muted, TextFlags);
            if (artist.Length > 0)
                TextRenderer.DrawText(g, artist, _bodyFont, new Rectangle(tl, Px(37), tw, Px(20)), second, TextFlags);
            if (live)
                TextRenderer.DrawText(g, _state.AppName, _smallFont, new Rectangle(tl, Px(57), tw, Px(16)), muted, TextFlags);

            // Transport.
            DrawButton(g, Previous, card, ink, muted, accent);
            DrawButton(g, PlayPause, card, ink, muted, accent);
            DrawButton(g, Next, card, ink, muted, accent);

            DrawSeek(g, w, card, ink, second, muted, accent);
        }

        void DrawButton(Graphics g, int button, Color card, Color ink, Color muted, Color accent)
        {
            bool on = Enabled(button);
            bool hot = on && _hot == button;
            bool pressed = hot && _down == button;
            Point c = ButtonCenter(button);
            int r = ButtonRadius(button);
            float k = pressed ? 0.9f : 1f;

            if (button == PlayPause)
            {
                Color fill = on ? (hot ? Palette.Blend(accent, Color.White, 0.18f) : accent) : Palette.Lift(card, 0.14f);
                int rr = (int)(r * k);
                using (var br = new SolidBrush(fill))
                    g.FillEllipse(br, c.X - rr, c.Y - rr, rr * 2, rr * 2);
                Color glyph = on ? (Palette.IsLight(accent) ? Color.FromArgb(24, 24, 28) : Color.White) : muted;
                float gs = Px(15) * k;
                if (_state.IsPlaying) Glyphs.Pause(g, c.X, c.Y, gs, glyph);
                else Glyphs.Play(g, c.X + Px(1), c.Y, gs, glyph);
                return;
            }

            if (hot)
                using (var br = new SolidBrush(Palette.Lift(card, pressed ? 0.20f : 0.12f)))
                    g.FillEllipse(br, c.X - r, c.Y - r, r * 2, r * 2);
            Color col = on ? ink : muted;
            float s = Px(15) * k;
            if (button == Previous) Glyphs.Previous(g, c.X, c.Y, s, col);
            else Glyphs.Next(g, c.X, c.Y, s, col);
        }

        void DrawSeek(Graphics g, int w, Color card, Color ink, Color second, Color muted, Color accent)
        {
            int x0 = BarLeft, x1 = BarRight, cy = Px(BarY);
            int th = Math.Max(3, Px(4));
            bool seek = _state.HasSession && _state.CanSeek;
            bool known = _state.HasSession && _state.Duration > TimeSpan.Zero;

            TimeSpan pos = known ? DisplayPosition() : (_state.HasSession ? _state.Position : TimeSpan.Zero);
            double frac = known ? Math.Min(1.0, pos.TotalSeconds / _state.Duration.TotalSeconds) : 0;

            var track = new Rectangle(x0, cy - th / 2, x1 - x0, th);
            using (var br = new SolidBrush(Palette.Lift(card, seek ? 0.20f : 0.12f)))
            using (var path = AlbumArt.RoundedRect(track, th / 2))
                g.FillPath(br, path);

            int fillW = (int)Math.Round(track.Width * frac);
            if (fillW > 0)
            {
                var fill = new Rectangle(x0, track.Y, Math.Max(fillW, th), th);
                // Greyed when the player does not let us seek: the position still shows, it
                // just does not look like something to grab.
                using (var br = new SolidBrush(seek ? accent : Palette.Blend(muted, card, 0.3f)))
                using (var path = AlbumArt.RoundedRect(fill, th / 2))
                    g.FillPath(br, path);
            }

            if (seek)
            {
                bool big = _drag || _overBar;
                int d = Px(big ? 13 : 9);
                int tx = x0 + fillW;
                using (var br = new SolidBrush(Palette.IsLight(card) ? Color.FromArgb(30, 30, 33) : Color.White))
                    g.FillEllipse(br, tx - d / 2, cy - d / 2, d, d);
            }

            if (_state.HasSession)
            {
                var left = new Rectangle(x0, cy + Px(7), Px(80), Px(18));
                var right = new Rectangle(x1 - Px(80), cy + Px(7), Px(80), Px(18));
                TextRenderer.DrawText(g, Time(pos), _smallFont, left, second, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (known)
                    TextRenderer.DrawText(g, Time(_state.Duration), _smallFont, right, muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        // ---- input ---------------------------------------------------------------------

        void EndDrag(bool commit)
        {
            if (_drag)
            {
                _drag = false;
                if (commit)
                {
                    TimeSpan pos = TimeSpan.FromSeconds(_dragFrac * _state.Duration.TotalSeconds);
                    _optimistic = true;
                    _optPos = pos;
                    _optUntil = Environment.TickCount + SeekHold;
                    Action<TimeSpan> handler = SeekRequested;
                    if (handler != null) handler(pos);
                }
            }
            _down = -1;
            if (Handle != IntPtr.Zero && Native.GetCapture() == Handle) Native.ReleaseCapture();
            Invalidate();
        }

        void SetHot(int hot, bool overBar)
        {
            if (hot == _hot && overBar == _overBar) return;
            _hot = hot;
            _overBar = overBar;
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    {
                        // Nothing for GDI to paint in a layered window: validate the region,
                        // then push a frame ourselves.
                        Native.PAINTSTRUCT ps;
                        Native.BeginPaint(Handle, out ps);
                        Native.EndPaint(Handle, ref ps);
                        Step();
                        if (_visible) Present();
                        return;
                    }

                case Native.WM_ERASEBKGND:
                    m.Result = (IntPtr)1;
                    return;

                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;

                case Native.WM_SETCURSOR:
                    // See MediaTile: a bare NativeWindow keeps whatever cursor was active before.
                    Cursor.Current = Cursors.Default;
                    m.Result = (IntPtr)1;
                    return;

                case Native.WM_MOUSEMOVE:
                    {
                        int x = Native.LoWord(m.LParam), y = Native.HiWord(m.LParam);
                        if (!_tracking)
                        {
                            var tme = new Native.TRACKMOUSEEVENT();
                            tme.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
                            tme.dwFlags = Native.TME_LEAVE;
                            tme.hwndTrack = Handle;
                            _tracking = Native.TrackMouseEvent(ref tme);
                        }
                        if (_drag)
                        {
                            _dragFrac = FractionAt(x);
                            Invalidate();
                        }
                        else
                        {
                            SetHot(HitButton(x, y), HitBar(x, y));
                        }
                        return;
                    }

                case Native.WM_MOUSELEAVE:
                    _tracking = false;
                    if (!_drag) SetHot(-1, false);
                    return;

                case Native.WM_LBUTTONDOWN:
                    {
                        int x = Native.LoWord(m.LParam), y = Native.HiWord(m.LParam);
                        int b = HitButton(x, y);
                        if (b >= 0 && Enabled(b))
                        {
                            _down = b;
                            Native.SetCapture(Handle);
                            Invalidate();
                        }
                        else if (HitBar(x, y))
                        {
                            _drag = true;
                            _dragFrac = FractionAt(x);
                            Native.SetCapture(Handle);
                            Invalidate();
                        }
                        return;
                    }

                case Native.WM_LBUTTONUP:
                    {
                        int x = Native.LoWord(m.LParam), y = Native.HiWord(m.LParam);
                        int pressed = _down;
                        bool dragging = _drag;
                        EndDrag(dragging);
                        if (!dragging && pressed >= 0 && HitButton(x, y) == pressed && Enabled(pressed))
                        {
                            Action<int> handler = ButtonClicked;
                            if (handler != null) handler(pressed);
                        }
                        return;
                    }

                case Native.WM_CAPTURECHANGED:
                    // Capture taken away mid-press (another window grabbed it): abandon, do not commit.
                    if (m.LParam != Handle) { _drag = false; _down = -1; Invalidate(); }
                    break;

                case ClickedAnywhere:
                    {
                        // A press somewhere on screen, packed as a mouse message packs a point.
                        var pt = new Point(Native.LoWord(m.LParam), Native.HiWord(m.LParam));
                        Native.RECT r;
                        if (Native.GetWindowRect(Handle, out r)
                            && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                            return;
                        // The tile's own press is its click-to-toggle; closing here as well
                        // would close on press and reopen on release.
                        if (_tileRect.Contains(pt)) return;
                        Close(false);
                        return;
                    }
            }
            base.WndProc(ref m);
        }
    }
}
