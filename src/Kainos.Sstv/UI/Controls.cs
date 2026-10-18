/*  Controls.cs

Small controls drawn in the Kainos look (IKainosTheme): a push button, the picture, the gallery strip, and the
details column with its tuning spectrum.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Sstv.UI
{
    internal class KButton : Control
    {
        private readonly IKainosTheme _t;
        private bool _on, _hover;
        public float FontPx = 11;

        public KButton(IKainosTheme t, string text)
        {
            _t = t;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg;
            Cursor = Cursors.Hand;
        }

        public bool On { get { return _on; } set { if (_on != value) { _on = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            _t.DrawButton(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Text, _on, Enabled, _hover, _t.S(FontPx));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    // The picture, scaled to fit. Lives in a KainosCrispHost, so it works in screen pixels (_k).
    internal class PictureView : Control
    {
        private readonly IKainosTheme _t;
        private float _k = 1;
        public Bitmap Image;                // not owned
        public string Caption = "";
        public string Placeholder = "";
        public double Progress = -1;        // while receiving: how far down the picture is (0 - 1)

        public PictureView(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _k = KainosCrispHost.DpiScale(this); }
        protected override void OnSizeChanged(EventArgs e) { _k = KainosCrispHost.DpiScale(this); base.OnSizeChanged(e); }
        private float px(float design) { return _t.S(design) * _k; }

        // where the picture is drawn
        public RectangleF PictureRect
        {
            get
            {
                if (Image == null) return RectangleF.Empty;
                float s = Math.Min(Width / (float)Image.Width, Height / (float)Image.Height);
                float w = Image.Width * s, h = Image.Height * s;
                return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Bg);
            if (Image == null)
            {
                using (Pen p = new Pen(_t.Line) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(p, px(6), px(6), Width - px(12) - 1, Height - px(12) - 1);
                using (Font f = _t.Font(px(12), false))
                using (Brush b = new SolidBrush(_t.Dim))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(Placeholder, f, b, new RectangleF(px(16), 0, Width - px(32), Height), sf);
                return;
            }
            RectangleF r = PictureRect;
            g.InterpolationMode = r.Width >= Image.Width ? InterpolationMode.Bicubic : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(Image, r);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            if (Progress >= 0 && Progress < 1)
            {
                float y = r.Top + (float)(Progress * r.Height);
                using (Pen p = new Pen(_t.Gold, Math.Max(1, px(1.5f)))) g.DrawLine(p, r.Left, y, r.Right, y);
            }
            if (!string.IsNullOrEmpty(Caption))
            {
                using (Font f = _t.Font(px(11), true))
                {
                    SizeF sz = g.MeasureString(Caption, f);
                    RectangleF cr = new RectangleF(r.Left + px(6), r.Bottom - sz.Height - px(10), sz.Width + px(10), sz.Height + px(4));
                    using (Brush bg = new SolidBrush(Color.FromArgb(170, _t.Bg))) g.FillRectangle(bg, cr);
                    using (Brush b = new SolidBrush(_t.Text)) g.DrawString(Caption, f, b, cr.Left + px(5), cr.Top + px(2));
                }
            }
        }
    }

    // The received pictures, newest on the left
    internal class GalleryStrip : Control
    {
        private readonly IKainosTheme _t;
        private readonly List<GalleryItem> _items = new List<GalleryItem>();
        private readonly Dictionary<string, Bitmap> _thumbs = new Dictionary<string, Bitmap>();
        private int _scroll, _hover = -1;
        public GalleryItem Selected;

        public event Action<GalleryItem> Picked;
        public event Action<GalleryItem, Point> Menu;

        public GalleryStrip(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Surface;
        }

        public IList<GalleryItem> Items { get { return _items; } }

        public void SetItems(IEnumerable<GalleryItem> items) { _items.Clear(); _items.AddRange(items); _scroll = 0; Invalidate(); }
        public void Insert(GalleryItem i) { _items.Insert(0, i); _scroll = 0; Invalidate(); }

        public void Remove(GalleryItem i)
        {
            _items.Remove(i);
            Bitmap b;
            if (_thumbs.TryGetValue(i.Path, out b)) { b.Dispose(); _thumbs.Remove(i.Path); }
            if (Selected == i) Selected = null;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (Bitmap b in _thumbs.Values) b.Dispose(); _thumbs.Clear(); }
            base.Dispose(disposing);
        }

        private int pad { get { return _t.S(4); } }
        private int thumbH { get { return Math.Max(16, Height - 2 * pad - _t.S(15)); } }
        private int cellW { get { return thumbH * 5 / 4 + pad; } }
        private int contentW { get { return _items.Count * cellW + pad; } }

        private int hit(Point p)
        {
            int i = (p.X + _scroll - pad) / cellW;
            return p.X + _scroll >= pad && i >= 0 && i < _items.Count ? i : -1;
        }

        private Bitmap thumb(GalleryItem it)
        {
            Bitmap b;
            if (_thumbs.TryGetValue(it.Path, out b)) return b;
            using (Bitmap full = Gallery.Open(it.Path)) b = full != null ? Gallery.Thumb(full, thumbH) : null;
            _thumbs[it.Path] = b;
            return b;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            // thumbnails are made at the strip's height
            foreach (Bitmap b in _thumbs.Values) if (b != null) b.Dispose();
            _thumbs.Clear();
            base.OnSizeChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Surface);
            using (Font f = _t.Font(_t.S(10), false))
            {
                if (_items.Count == 0)
                {
                    using (Brush b = new SolidBrush(_t.Faint))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("Pictures you receive are kept here (newest first). Right-click one to open, copy or delete it.", f, b, ClientRectangle, sf);
                    return;
                }
                int first = Math.Max(0, (_scroll - pad) / cellW), th = thumbH;
                for (int i = first; i < _items.Count; i++)
                {
                    int x = pad + i * cellW - _scroll;
                    if (x > Width) break;
                    GalleryItem it = _items[i];
                    Rectangle cell = new Rectangle(x, pad, cellW - pad, th);
                    Bitmap b = thumb(it);
                    using (Brush bg = new SolidBrush(_t.Bg)) g.FillRectangle(bg, cell);
                    if (b != null)
                    {
                        int w = Math.Min(cell.Width, b.Width);
                        g.DrawImage(b, new Rectangle(cell.X + (cell.Width - w) / 2, cell.Y, w, th), new Rectangle((b.Width - w) / 2, 0, w, b.Height), GraphicsUnit.Pixel);
                    }
                    Color edge = it == Selected ? _t.Gold : i == _hover ? _t.Ice : _t.Line;
                    using (Pen p = new Pen(edge, it == Selected ? 2 : 1)) g.DrawRectangle(p, cell);
                    string cap = it.Call.Length > 0 ? it.Call : it.Utc.ToString("HH:mm") + "Z";
                    using (Brush tb = new SolidBrush(it == Selected ? _t.Gold : _t.Dim))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(cap, f, tb, new RectangleF(cell.X, cell.Bottom + 1, cell.Width, _t.S(15)), sf);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = hit(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = hit(e.Location);
            if (h >= 0)
            {
                Selected = _items[h];
                Invalidate();
                if (e.Button == MouseButtons.Left) Picked?.Invoke(Selected);
                else if (e.Button == MouseButtons.Right) Menu?.Invoke(Selected, e.Location);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Max(0, Math.Min(Math.Max(0, contentW - Width), _scroll - e.Delta * cellW / 120));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
    }

    // What's known about the picture, and the tones as they're heard (for tuning)
    internal class DetailsView : Control
    {
        private readonly IKainosTheme _t;
        public readonly List<KeyValuePair<string, string>> Rows = new List<KeyValuePair<string, string>>();
        public float[] Spectrum;
        public double Shift;            // the last picture's mistuning, drawn as a marker

        public DetailsView(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Panel);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int pad = _t.S(8), rh = _t.S(19), y = pad;
            using (Font kf = _t.Font(_t.S(10), false))
            using (Font vf = _t.Font(_t.S(12), true))
            using (Brush kb = new SolidBrush(_t.Dim))
            using (Brush vb = new SolidBrush(_t.Text))
            using (StringFormat sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                int kw = _t.S(62);
                foreach (KeyValuePair<string, string> r in Rows)
                {
                    if (y + rh > Height - _t.S(70)) break;
                    g.DrawString(r.Key.ToUpperInvariant(), kf, kb, new RectangleF(pad, y + _t.S(3), kw, rh), sf);
                    g.DrawString(r.Value, vf, vb, new RectangleF(pad + kw, y, Width - 2 * pad - kw, rh), sf);
                    y += rh;
                }
            }

            // the tuning spectrum: 1000 - 2500 Hz, with sync (1200), black (1500), leader (1900) and white (2300)
            Rectangle sr = new Rectangle(pad, Height - _t.S(62), Width - 2 * pad, _t.S(40));
            if (sr.Width < 20 || sr.Top < pad) return;
            using (Brush bg = new SolidBrush(_t.Bg)) g.FillRectangle(bg, sr);
            Func<double, float> xOf = hz => sr.Left + (float)((hz - SstvEngine.SpecLo) / (SstvEngine.SpecHi - SstvEngine.SpecLo) * sr.Width);
            using (Font f = _t.Font(_t.S(9), false))
            using (Brush lb = new SolidBrush(_t.Faint))
            using (Pen tick = new Pen(_t.Line))
            {
                foreach (double hz in new double[] { 1200, 1500, 1900, 2300 })
                {
                    float x = xOf(hz);
                    g.DrawLine(tick, x, sr.Top, x, sr.Bottom);
                    string label = hz == 1200 ? "sync" : hz == 1500 ? "black" : hz == 1900 ? "1900" : "white";
                    SizeF sz = g.MeasureString(label, f);
                    g.DrawString(label, f, lb, x - sz.Width / 2, sr.Bottom + 1);
                }
            }
            float[] s = Spectrum;
            if (s != null && s.Length > 1)
            {
                float max = float.MinValue;
                foreach (float v in s) max = Math.Max(max, v);
                float floor = max - 40;
                PointF[] pts = new PointF[s.Length + 2];
                for (int i = 0; i < s.Length; i++)
                {
                    float v = Math.Max(0, Math.Min(1, (s[i] - floor) / 40f));
                    pts[i + 1] = new PointF(sr.Left + i * (sr.Width - 1f) / (s.Length - 1), sr.Bottom - v * (sr.Height - 2));
                }
                pts[0] = new PointF(sr.Left, sr.Bottom);
                pts[s.Length + 1] = new PointF(sr.Right - 1, sr.Bottom);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Brush fill = new SolidBrush(Color.FromArgb(70, _t.Ice))) g.FillPolygon(fill, pts);
                using (Pen p = new Pen(_t.Ice)) g.DrawLines(p, pts);
                g.SmoothingMode = SmoothingMode.Default;
            }
            if (Math.Abs(Shift) >= 1)
            {
                float x = xOf(1900 + Shift);
                using (Pen p = new Pen(_t.Gold) { DashStyle = DashStyle.Dot }) g.DrawLine(p, x, sr.Top, x, sr.Bottom);
            }
        }
    }
}
