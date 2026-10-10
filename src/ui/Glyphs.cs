using System;
using System.Drawing;

namespace MediaController
{
    /// <summary>
    /// The media symbols, drawn as shapes. Not font characters: no font is guaranteed to
    /// carry them, and a missing glyph is a box in the taskbar. Each is drawn centred on
    /// (cx, cy) in a box of roughly <c>size</c> pixels.
    /// </summary>
    static class Glyphs
    {
        public static void Pause(Graphics g, float cx, float cy, float size, Color color)
        {
            float s = size / 2f;
            using (var br = new SolidBrush(color))
            {
                g.FillRectangle(br, cx - s * 0.8f, cy - s * 0.9f, s * 0.62f, s * 1.8f);
                g.FillRectangle(br, cx + s * 0.18f, cy - s * 0.9f, s * 0.62f, s * 1.8f);
            }
        }

        public static void Play(Graphics g, float cx, float cy, float size, Color color)
        {
            float s = size / 2f;
            using (var br = new SolidBrush(color))
                g.FillPolygon(br, new[] {
                    new PointF(cx - s * 0.55f, cy - s * 0.95f),
                    new PointF(cx - s * 0.55f, cy + s * 0.95f),
                    new PointF(cx + s * 0.85f, cy) });
        }

        public static void Previous(Graphics g, float cx, float cy, float size, Color color)
        {
            float s = size / 2f;
            float bar = Math.Max(1.5f, s * 0.26f);
            using (var br = new SolidBrush(color))
            {
                g.FillRectangle(br, cx - s * 0.8f, cy - s * 0.75f, bar, s * 1.5f);
                g.FillPolygon(br, new[] {
                    new PointF(cx + s * 0.8f, cy - s * 0.75f),
                    new PointF(cx + s * 0.8f, cy + s * 0.75f),
                    new PointF(cx - s * 0.7f + bar, cy) });
            }
        }

        public static void Next(Graphics g, float cx, float cy, float size, Color color)
        {
            float s = size / 2f;
            float bar = Math.Max(1.5f, s * 0.26f);
            using (var br = new SolidBrush(color))
            {
                g.FillRectangle(br, cx + s * 0.8f - bar, cy - s * 0.75f, bar, s * 1.5f);
                g.FillPolygon(br, new[] {
                    new PointF(cx - s * 0.8f, cy - s * 0.75f),
                    new PointF(cx - s * 0.8f, cy + s * 0.75f),
                    new PointF(cx + s * 0.7f - bar, cy) });
            }
        }

        /// <summary>A single eighth note: the generic "audio" mark for something with no art.</summary>
        public static void Note(Graphics g, float cx, float cy, float size, Color color)
        {
            float u = size / 14f;
            using (var br = new SolidBrush(color))
            using (var pen = new Pen(color, Math.Max(1.5f, u * 1.3f)))
            {
                g.FillEllipse(br, cx - 4.2f * u, cy + 1.2f * u, 4.6f * u, 3.6f * u);
                g.DrawLine(pen, cx + 0.2f * u, cy + 3f * u, cx + 0.2f * u, cy - 4.6f * u);
                g.DrawLine(pen, cx + 0.2f * u, cy - 4.6f * u, cx + 3.6f * u, cy - 2.4f * u);
            }
        }
    }
}
