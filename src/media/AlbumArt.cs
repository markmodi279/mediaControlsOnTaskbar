using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace MediaController
{
    /// <summary>
    /// A decoded album-art image: a square centre crop ready to draw, and an accent colour.
    ///
    /// Decoded once per track (the Controller owns one and disposes it when the track
    /// changes) and drawn from many places, so the tile and the panel never decode.
    /// Art arrives in any shape - browsers hand over a 16:9 video thumbnail - so what is
    /// kept is the central square; a circle or rounded square is then cut from that.
    ///
    /// Shapes are drawn with a TextureBrush rather than by setting a clip region: the
    /// callers already clip to the rectangle being repainted, and ResetClip would throw
    /// that away.
    /// </summary>
    sealed class AlbumArt : IDisposable
    {
        const int MaxSide = 256;

        Bitmap _square;

        /// <summary>A colour pulled from the image: saturated, mid-bright, usable as a tint.</summary>
        public Color Accent { get; private set; }

        AlbumArt() { }

        /// <summary>Decodes the bytes; null if they are not an image GDI+ understands.</summary>
        public static AlbumArt FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var src = Image.FromStream(ms))
                {
                    int side = Math.Min(src.Width, src.Height);
                    if (side <= 0) return null;
                    int outSide = Math.Min(side, MaxSide);

                    var art = new AlbumArt();
                    // Copied into our own bitmap: an Image from a stream needs the stream
                    // alive for as long as it is used, and the MemoryStream goes with this block.
                    art._square = new Bitmap(outSide, outSide, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(art._square))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        var from = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                        g.DrawImage(src, new Rectangle(0, 0, outSide, outSide), from, GraphicsUnit.Pixel);
                    }
                    art.Accent = ComputeAccent(art._square);
                    return art;
                }
            }
            catch (Exception ex)
            {
                Log.Write("art: decode failed: " + ex.Message);
                return null;
            }
        }

        public int Side { get { return _square.Width; } }

        /// <summary>Fills an ellipse inscribed in <paramref name="r"/> with the art.</summary>
        public void DrawCircle(Graphics g, Rectangle r)
        {
            using (var br = Brush(r))
                g.FillEllipse(br, r);
        }

        /// <summary>Fills <paramref name="path"/> with the art laid over <paramref name="r"/>.</summary>
        public void DrawPath(Graphics g, Rectangle r, GraphicsPath path)
        {
            using (var br = Brush(r))
                g.FillPath(br, path);
        }

        /// <summary>Fills a rounded square (radius in pixels) with the art.</summary>
        public void DrawRounded(Graphics g, Rectangle r, int radius)
        {
            using (var br = Brush(r))
            using (var path = RoundedRect(r, radius))
                g.FillPath(br, path);
        }

        // A TextureBrush scaled by its transform does not filter: shrunk it aliases (the 22 px
        // tile face looked gritty), enlarged it goes blocky. So the image is resampled ONCE per
        // size with bicubic filtering and the brush then maps it 1:1. Two sizes are in use
        // (tile and panel), so the cache is tiny; it is disposed with the art.
        readonly System.Collections.Generic.Dictionary<int, Bitmap> _scaled = new System.Collections.Generic.Dictionary<int, Bitmap>();

        Bitmap Scaled(int size)
        {
            if (size <= 0 || size == _square.Width) return _square;
            Bitmap b;
            if (_scaled.TryGetValue(size, out b)) return b;
            b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            using (var attr = new ImageAttributes())
            {
                // TileFlipXY: without it the edge pixels blend with transparent black and the
                // rim of the picture shows a faint dark halo.
                attr.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(_square, new Rectangle(0, 0, size, size), 0, 0, _square.Width, _square.Height, GraphicsUnit.Pixel, attr);
            }
            _scaled[size] = b;
            return b;
        }

        TextureBrush Brush(Rectangle r)
        {
            var br = new TextureBrush(Scaled(Math.Max(r.Width, r.Height)), WrapMode.Clamp);
            br.TranslateTransform(r.X, r.Y);
            return br;
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---- accent colour ---------------------------------------------------------------

        // Average of a 32x32 thumbnail, each pixel weighted by how colourful and bright it
        // is. A plain average of a dark, mostly-black cover is mud; weighting lets the one
        // vivid element decide. Then pulled into a range that works as a tint on a dark card.
        static Color ComputeAccent(Bitmap src)
        {
            const int N = 32;
            using (var small = new Bitmap(N, N, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, new Rectangle(0, 0, N, N));
                }

                var data = small.LockBits(new Rectangle(0, 0, N, N), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                // Averaging every pixel turns a yellow-on-blue cover into khaki, a colour that
                // is not in the picture. So pixels vote into 12 hue buckets by colourfulness
                // and the heaviest bucket's own average is the accent.
                const int Buckets = 12;
                var wsum = new double[Buckets];
                var rs = new double[Buckets];
                var gs = new double[Buckets];
                var bs = new double[Buckets];
                double allW = 0, ar = 0, ag = 0, ab = 0;
                try
                {
                    var buf = new byte[data.Stride * N];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                    for (int y = 0; y < N; y++)
                    {
                        for (int x = 0; x < N; x++)
                        {
                            int i = y * data.Stride + x * 4;
                            double b = buf[i], gg = buf[i + 1], r = buf[i + 2];
                            double max = Math.Max(r, Math.Max(gg, b));
                            double min = Math.Min(r, Math.Min(gg, b));
                            double s = max <= 0 ? 0 : (max - min) / max;
                            double v = max / 255.0;
                            double w = s * s * v + 0.002;
                            ar += r * w; ag += gg * w; ab += b * w; allW += w;
                            if (s < 0.15 || v < 0.15) continue;      // greys and near-black have no hue to vote for
                            double h, hs, hv;
                            ToHsv(Color.FromArgb((int)r, (int)gg, (int)b), out h, out hs, out hv);
                            int k = ((int)(h / 30)) % Buckets;
                            wsum[k] += w; rs[k] += r * w; gs[k] += gg * w; bs[k] += b * w;
                        }
                    }
                }
                finally { small.UnlockBits(data); }

                int best = -1;
                for (int k = 0; k < Buckets; k++)
                    if (wsum[k] > 0 && (best < 0 || wsum[k] > wsum[best])) best = k;
                if (best >= 0)
                    return Tint(Color.FromArgb((int)(rs[best] / wsum[best]), (int)(gs[best] / wsum[best]), (int)(bs[best] / wsum[best])));
                if (allW <= 0) return Color.FromArgb(120, 120, 130);
                return Tint(Color.FromArgb((int)(ar / allW), (int)(ag / allW), (int)(ab / allW)));
            }
        }

        // Keeps the hue; lifts a washed-out colour to a usable saturation and brings the
        // brightness into a band that is neither black on a dark card nor glaring.
        static Color Tint(Color c)
        {
            double h, s, v;
            ToHsv(c, out h, out s, out v);
            if (s > 0.12) s = Math.Max(s, 0.45);      // greys stay grey; colours get some punch
            s = Math.Min(s, 0.85);
            v = Math.Max(0.62, Math.Min(v, 0.92));
            return FromHsv(h, s, v);
        }

        static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        static Color FromHsv(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        public void Dispose()
        {
            foreach (var b in _scaled.Values) b.Dispose();
            _scaled.Clear();
            if (_square != null) { _square.Dispose(); _square = null; }
        }
    }
}
