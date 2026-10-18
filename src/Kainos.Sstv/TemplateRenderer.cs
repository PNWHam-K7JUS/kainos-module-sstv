/*  TemplateRenderer.cs

A template drawn as the picture to send: the background (the station's photo cropped to fill, a gradient, or the
picture just received), a band across the top with the title in the accent colour, the big text in the middle,
optionally the received picture small on the right, and up to three lines along the bottom. Text is outlined and
sized to fit, so it stays readable at SSTV's 320 pixels across.

Drawn at twice the mode's width (and the mode's shape) and scaled down by the encoder, for smooth edges.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;

namespace Kainos.Sstv
{
    internal static class TemplateRenderer
    {
        public static Bitmap Render(SstvTemplate t, StationProfile st, TemplateFields f, SstvMode m, Bitmap photo, Bitmap received)
        {
            int w = 640, h = (int)Math.Round(640.0 * m.Lines / m.Width);
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            Color accent = parse(t.Accent, Color.FromArgb(0xD4, 0xAD, 0x6A));
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // background
                Bitmap bg = t.Background == "received" ? received : t.Background == "photo" ? photo : null;
                if (bg != null)
                {
                    cover(g, bg, new Rectangle(0, 0, w, h));
                    using (Brush dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillRectangle(dim, 0, 0, w, h);
                }
                else
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(0x08, 0x14, 0x24), mix(accent, Color.FromArgb(0x08, 0x14, 0x24), 0.45f), 60f))
                        g.FillRectangle(lg, 0, 0, w, h);
                    // a faint grid, so a plain background isn't flat
                    using (Pen p = new Pen(Color.FromArgb(18, 255, 255, 255)))
                        for (int x = 0; x < w; x += 40) g.DrawLine(p, x, 0, x, h);
                }

                // the top band
                int topH = (int)(h * 0.19);
                using (Brush band = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(band, 0, 0, w, topH);
                using (Brush ab = new SolidBrush(accent)) g.FillRectangle(ab, 0, topH, w, Math.Max(3, h / 120));
                drawFit(g, SstvTemplate.Fill(t.Title, st, f), new RectangleF(16, 4, w - 32, topH - 8), accent, StringAlignment.Center, 0.62f);

                // the bottom band
                string[] lines = SstvTemplate.Fill(t.Lines, st, f).Split('\n').Where(l => l.Length > 0).Take(3).ToArray();
                int lineH = (int)(h * 0.085);
                int botH = lines.Length == 0 ? 0 : lines.Length * lineH + 14;
                if (botH > 0)
                {
                    using (Brush band = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(band, 0, h - botH, w, botH);
                    for (int i = 0; i < lines.Length; i++)
                        drawFit(g, lines[i], new RectangleF(16, h - botH + 7 + i * lineH, w - 32, lineH), Color.White, StringAlignment.Center, 0.78f);
                }

                // the middle: the big text, and the received picture beside it
                RectangleF mid = new RectangleF(16, topH + 10, w - 32, h - topH - botH - 20);
                if (t.Inset && received != null && mid.Height > 40)
                {
                    float ih = mid.Height * 0.92f, iw = Math.Min(w * 0.42f, ih * received.Width / received.Height);
                    ih = iw * received.Height / received.Width;
                    RectangleF ir = new RectangleF(w - 16 - iw, mid.Top + (mid.Height - ih) / 2, iw, ih);
                    using (Brush sh = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(sh, ir.X + 5, ir.Y + 5, ir.Width, ir.Height);
                    g.DrawImage(received, ir);
                    using (Pen p = new Pen(Color.White, 4)) g.DrawRectangle(p, ir.X, ir.Y, ir.Width, ir.Height);
                    mid = new RectangleF(mid.Left, mid.Top, ir.Left - mid.Left - 14, mid.Height);
                }
                drawFit(g, SstvTemplate.Fill(t.Big, st, f), mid, Color.White, StringAlignment.Center, 0.8f);
            }
            return b;
        }

        // the picture cropped to fill the rectangle
        private static void cover(Graphics g, Bitmap src, Rectangle r)
        {
            double s = Math.Max(r.Width / (double)src.Width, r.Height / (double)src.Height);
            int w = (int)Math.Ceiling(src.Width * s), h = (int)Math.Ceiling(src.Height * s);
            g.DrawImage(src, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        }

        // one line of text as large as fits the rectangle (height share `fill`), outlined in black
        private static void drawFit(Graphics g, string text, RectangleF r, Color c, StringAlignment align, float fill)
        {
            if (string.IsNullOrWhiteSpace(text) || r.Width < 8 || r.Height < 6) return;
            text = text.Replace("\n", "  ");
            using (FontFamily ff = new FontFamily("Segoe UI"))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = align, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                float em = r.Height * fill;
                RectangleF bounds;
                // shrink until it fits across
                for (int i = 0; i < 30; i++)
                {
                    using (GraphicsPath probe = new GraphicsPath())
                    {
                        probe.AddString(text, ff, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);
                        bounds = probe.GetBounds();
                    }
                    if (bounds.Width <= r.Width && bounds.Height <= r.Height * 0.95f) break;
                    em *= Math.Min(0.95f, Math.Min(r.Width / Math.Max(1, bounds.Width), r.Height * 0.95f / Math.Max(1, bounds.Height)));
                }
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddString(text, ff, (int)FontStyle.Bold, em, r, sf);
                    float ow = Math.Max(2.5f, em / 9f);
                    using (Pen outline = new Pen(Color.FromArgb(230, 0, 0, 0), ow) { LineJoin = LineJoin.Round })
                        g.DrawPath(outline, p);
                    using (Brush fb = new SolidBrush(c)) g.FillPath(fb, p);
                }
            }
        }

        private static Color mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));
        }

        internal static Color parse(string hex, Color fallback)
        {
            try
            {
                string s = (hex ?? "").Trim().TrimStart('#');
                if (s.Length != 6) return fallback;
                int v = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
            }
            catch { return fallback; }
        }

        internal static string hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
    }
}
