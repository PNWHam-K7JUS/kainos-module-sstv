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

    // What's known about the picture, and what's being heard: a waterfall and spectrum of 900 - 2500 Hz with the SSTV
    // tones marked (sync 1200, black 1500, leader 1900, white 2300), and the input level
    internal class DetailsView : Control
    {
        private readonly IKainosTheme _t;
        public readonly List<KeyValuePair<string, string>> Rows = new List<KeyValuePair<string, string>>();
        public double Shift;            // the last picture's mistuning, drawn as a marker
        public float PeakDb = -120;     // input level (dB below full scale)

        private const int WfLines = 400;                // 40 s at 10 lines a second
        private Bitmap _wf;
        private int _wfRow, _wfCount;                   // the newest line's row; lines held
        private float[] _last;
        private float _floor = float.NaN;

        public DetailsView(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Panel;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _wf != null) { _wf.Dispose(); _wf = null; }
            base.Dispose(disposing);
        }

        // a spectrum line (dB per bin, SstvEngine's bins): onto the top of the waterfall
        public void AddLine(float[] db)
        {
            int w = db.Length;
            if (_wf == null || _wf.Width != w) { if (_wf != null) _wf.Dispose(); _wf = new Bitmap(w, WfLines, System.Drawing.Imaging.PixelFormat.Format32bppRgb); _wfCount = 0; }
            _last = db;
            // the noise floor: the line's 20th percentile, followed slowly
            float[] sorted = (float[])db.Clone();
            Array.Sort(sorted);
            float f = sorted[w / 5];
            _floor = float.IsNaN(_floor) ? f : _floor + 0.1f * (f - _floor);
            _wfRow = (_wfRow - 1 + WfLines) % WfLines;
            _wfCount = Math.Min(WfLines, _wfCount + 1);
            int[] row = new int[w];
            for (int i = 0; i < w; i++) row[i] = colour((db[i] - _floor - 3) / 32f).ToArgb();
            System.Drawing.Imaging.BitmapData bd = _wf.LockBits(new Rectangle(0, _wfRow, w, 1), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0, w);
            _wf.UnlockBits(bd);
        }

        // 0 (noise) to 1 (strong): navy, blue, ice, gold, white
        private Color colour(float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            Color[] stops = { _t.Bg, Color.FromArgb(0x16, 0x3a, 0x5c), _t.Ice, _t.Gold, Color.White };
            float p = v * (stops.Length - 1);
            int i = Math.Min(stops.Length - 2, (int)p);
            float fr = p - i;
            Color a = stops[i], b = stops[i + 1];
            return Color.FromArgb((int)(a.R + (b.R - a.R) * fr), (int)(a.G + (b.G - a.G) * fr), (int)(a.B + (b.B - a.B) * fr));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Panel);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int pad = _t.S(8), rh = _t.S(19), y = pad;
            // from the bottom up: the level meter, the tone labels, the spectrum, then the waterfall up to the rows
            int levelH = _t.S(14), labelH = _t.S(13), specH = _t.S(30), minWf = _t.S(36);
            Rectangle lr = new Rectangle(pad, Height - pad - levelH, Width - 2 * pad, levelH);
            Rectangle sr = new Rectangle(pad, lr.Top - _t.S(6) - labelH - specH, Width - 2 * pad, specH);
            int rowsBottom = sr.Top - minWf - _t.S(4);

            using (Font kf = _t.Font(_t.S(10), false))
            using (Font vf = _t.Font(_t.S(12), true))
            using (Brush kb = new SolidBrush(_t.Dim))
            using (Brush vb = new SolidBrush(_t.Text))
            using (StringFormat sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                int kw = _t.S(62);
                foreach (KeyValuePair<string, string> r in Rows)
                {
                    if (y + rh > rowsBottom) break;
                    g.DrawString(r.Key.ToUpperInvariant(), kf, kb, new RectangleF(pad, y + _t.S(3), kw, rh), sf);
                    g.DrawString(r.Value, vf, vb, new RectangleF(pad + kw, y, Width - 2 * pad - kw, rh), sf);
                    y += rh;
                }
            }
            if (sr.Width < 20 || sr.Top < pad) return;
            Rectangle wr = new Rectangle(pad, y + _t.S(4), sr.Width, Math.Max(0, sr.Top - 2 - y - _t.S(4)));
            Func<double, float> xOf = hz => sr.Left + (float)((hz - SstvEngine.SpecLo) / (SstvEngine.SpecHi - SstvEngine.SpecLo) * sr.Width);
            double[] tones = { 1200, 1500, 1900, 2300 };

            // the waterfall, newest at the top, one line a pixel
            using (Brush bg = new SolidBrush(_t.Bg)) { g.FillRectangle(bg, wr); g.FillRectangle(bg, sr); }
            if (_wf != null && wr.Height > 0 && _wfCount > 0)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                int vis = Math.Min(wr.Height, _wfCount);
                int a = Math.Min(vis, WfLines - _wfRow);
                g.DrawImage(_wf, new Rectangle(wr.Left, wr.Top, wr.Width, a), new Rectangle(0, _wfRow, _wf.Width, a), GraphicsUnit.Pixel);
                if (vis > a) g.DrawImage(_wf, new Rectangle(wr.Left, wr.Top + a, wr.Width, vis - a), new Rectangle(0, 0, _wf.Width, vis - a), GraphicsUnit.Pixel);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            using (Pen tick = new Pen(Color.FromArgb(90, _t.Dim)) { DashStyle = DashStyle.Dot })
                foreach (double hz in tones) g.DrawLine(tick, xOf(hz), wr.Top, xOf(hz), wr.Bottom);

            // the spectrum of the newest line
            using (Pen tick = new Pen(_t.Line))
                foreach (double hz in tones) g.DrawLine(tick, xOf(hz), sr.Top, xOf(hz), sr.Bottom);
            float[] s = _last;
            if (s != null && s.Length > 1 && !float.IsNaN(_floor))
            {
                PointF[] pts = new PointF[s.Length + 2];
                for (int i = 0; i < s.Length; i++)
                {
                    float v = Math.Max(0, Math.Min(1, (s[i] - _floor + 3) / 40f));
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
                using (Pen p = new Pen(_t.Gold) { DashStyle = DashStyle.Dot }) g.DrawLine(p, x, wr.Top, x, sr.Bottom);
            }
            using (Font f = _t.Font(_t.S(9), false))
            using (Brush lb = new SolidBrush(_t.Faint))
                foreach (double hz in tones)
                {
                    string label = hz == 1200 ? "sync" : hz == 1500 ? "black" : hz == 1900 ? "1900" : "white";
                    SizeF sz = g.MeasureString(label, f);
                    g.DrawString(label, f, lb, xOf(hz) - sz.Width / 2, sr.Bottom + 1);
                }

            // the input level: peak, -60 to 0 dB
            using (Font f = _t.Font(_t.S(9), true))
            using (Brush kb = new SolidBrush(_t.Dim))
            {
                int kw = _t.S(44);
                g.DrawString("AUDIO", f, kb, lr.Left, lr.Top);
                Rectangle bar = new Rectangle(lr.Left + kw, lr.Top + _t.S(2), lr.Width - kw - _t.S(74), lr.Height - _t.S(4));
                using (Brush bg = new SolidBrush(_t.Bg)) g.FillRectangle(bg, bar);
                float v = Math.Max(0, Math.Min(1, (PeakDb + 60) / 60f));
                Color c = PeakDb > -1 ? _t.Tx : PeakDb > -3 ? _t.Gold : PeakDb < -45 ? _t.Faint : _t.Ice;
                using (Brush fb = new SolidBrush(c)) g.FillRectangle(fb, bar.Left, bar.Top, (int)(bar.Width * v), bar.Height);
                string txt = PeakDb < -100 ? "no audio" : PeakDb > -1 ? "clipping" : PeakDb < -45 ? "very low" : PeakDb.ToString("0") + " dB";
                using (Brush tb = new SolidBrush(PeakDb > -1 ? _t.Tx : _t.Text)) g.DrawString(txt, f, tb, bar.Right + _t.S(6), lr.Top);
            }
        }
    }
}
