/*  TemplateEditorForm.cs

The picture templates: a list on the left, the chosen one's text, background and colour on the right, and how it
will look (drawn with an example callsign and report, and the last picture received for the inset).

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Sstv.UI
{
    internal class TemplateEditorForm : Form
    {
        private readonly IKainosHost _host;
        private readonly IKainosTheme _t;
        private readonly List<SstvTemplate> _list;
        private readonly StationProfile _st;
        private readonly Bitmap _photo, _received;
        private readonly ListBox _names;
        private readonly TextBox _name, _title, _big, _lines;
        private readonly ComboBox _bg;
        private readonly Button _accent;
        private readonly CheckBox _inset;
        private readonly PictureBox _preview;
        private bool _loading;
        private readonly Timer _redraw = new Timer { Interval = 200 };

        private static readonly string[] BgValues = { "photo", "gradient", "received" };
        private static readonly string[] BgNames = { "My photo", "Plain (gradient)", "The picture received" };

        // received: the last picture received (for the inset and the "received" background), or null
        public TemplateEditorForm(IKainosHost host, Bitmap received)
        {
            _host = host;
            _t = host.Theme;
            _list = TemplateStore.Load(host).Select(tp => tp.Clone()).ToList();
            _st = StationProfile.Load(host);
            string pp = _st.PhotoPath(host);
            _photo = pp != null ? Gallery.Open(pp) : null;
            _received = received != null ? new Bitmap(received) : null;
            Text = "SSTV: pictures";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            int s(float v) => _t.S(v);
            ClientSize = new Size(s(900), s(520));
            MinimumSize = new Size(s(760), s(480));
            Font = _t.Font(s(12), false);

            _names = new ListBox { Location = new Point(s(12), s(12)), Size = new Size(s(160), s(400)), IntegralHeight = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom };
            Controls.Add(_names);
            Button add = new Button { Text = "New", Location = new Point(s(12), s(420)), Size = new Size(s(77), s(28)), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Button dup = new Button { Text = "Copy", Location = new Point(s(95), s(420)), Size = new Size(s(77), s(28)), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Button del = new Button { Text = "Delete", Location = new Point(s(12), s(452)), Size = new Size(s(77), s(28)), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Button reset = new Button { Text = "Defaults", Location = new Point(s(95), s(452)), Size = new Size(s(77), s(28)), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.AddRange(new Control[] { add, dup, del, reset });
            new ToolTip().SetToolTip(reset, "Put back the CQ, Reply and 73 pictures as first made (others are kept)");

            int x = s(186), lw = s(84), fw = s(300), y = s(12);
            Func<string, int, TextBox> field = (label, h) =>
            {
                Controls.Add(new Label { Text = label, Location = new Point(x, y + s(4)), Size = new Size(lw, s(20)) });
                TextBox tb = new TextBox { Location = new Point(x + lw, y), Width = fw, Multiline = h > 1, Height = h > 1 ? s(22) * h : s(24), ScrollBars = h > 1 ? ScrollBars.Vertical : ScrollBars.None, AcceptsReturn = h > 1 };
                tb.TextChanged += (o, e) => changed();
                Controls.Add(tb);
                y += (h > 1 ? s(22) * h : s(24)) + s(8);
                return tb;
            };
            _name = field("Name", 1);
            _title = field("Top line", 1);
            _big = field("Big text", 1);
            _lines = field("Bottom lines", 3);

            Controls.Add(new Label { Text = "Background", Location = new Point(x, y + s(4)), Size = new Size(lw, s(20)) });
            _bg = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x + lw, y), Width = s(180) };
            _bg.Items.AddRange(BgNames);
            _bg.SelectedIndexChanged += (o, e) => changed();
            Controls.Add(_bg);
            y += s(32);
            Controls.Add(new Label { Text = "Top colour", Location = new Point(x, y + s(4)), Size = new Size(lw, s(20)) });
            _accent = new Button { Location = new Point(x + lw, y), Size = new Size(s(60), s(26)), FlatStyle = FlatStyle.Flat };
            _accent.Click += (o, e) => pickColour();
            Controls.Add(_accent);
            y += s(34);
            _inset = new CheckBox { Text = "The received picture, small, on the right", Location = new Point(x + lw, y), AutoSize = true };
            _inset.CheckedChanged += (o, e) => changed();
            Controls.Add(_inset);
            y += s(34);
            Label help = new Label
            {
                Text = "Filled in when sent:\n{MYCALL} {NAME} {QTH} {GRID} {RIG} {ANT} {PWR}: yours (Options > Station setup)\n{CALL} {RST}: theirs, from the send bar\n{DATE} {TIME} {MODE}: UTC, and the mode it goes out in",
                Location = new Point(x, y), Size = new Size(lw + fw, s(90)), AutoSize = false,
            };
            Controls.Add(help);

            _preview = new PictureBox { Location = new Point(s(590), s(12)), Size = new Size(s(298), s(240)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(_preview);
            Controls.Add(new Label { Text = "Example: to W1AW, report 595", Location = new Point(s(590), s(258)), AutoSize = true });

            Button ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(s(700), s(480)), Size = new Size(s(90), s(30)), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(s(798), s(480)), Size = new Size(s(90), s(30)), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = null;        // Enter makes new lines in the bottom lines box
            CancelButton = cancel;

            _names.SelectedIndexChanged += (o, e) => load();
            add.Click += (o, e) => { _list.Add(new SstvTemplate { Name = "New picture", Title = "{CALL} de {MYCALL}", Big = "", Lines = "", Background = _photo != null ? "photo" : "gradient" }); refill(_list.Count - 1); };
            dup.Click += (o, e) => { SstvTemplate c = cur; if (c == null) return; SstvTemplate n = c.Clone(); n.Name = c.Name + " (copy)"; n.Kind = "custom"; _list.Add(n); refill(_list.Count - 1); };
            del.Click += (o, e) =>
            {
                SstvTemplate c = cur;
                if (c == null || _list.Count <= 1) return;
                if (c.Kind != "custom" && MessageBox.Show(this, "\"" + c.Name + "\" is the " + c.Kind.ToUpperInvariant() + " button's picture. Delete it? (Defaults puts it back.)", "SSTV", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                int i = _names.SelectedIndex;
                _list.Remove(c);
                refill(Math.Min(i, _list.Count - 1));
            };
            reset.Click += (o, e) =>
            {
                foreach (SstvTemplate d in TemplateStore.Defaults())
                {
                    int i = _list.FindIndex(t => t.Kind == d.Kind);
                    if (i >= 0) _list[i] = d; else _list.Insert(0, d);
                }
                refill(0);
            };
            FormClosing += (o, e) => { if (DialogResult == DialogResult.OK) try { TemplateStore.Save(_host, _list); } catch (Exception ex) { MessageBox.Show(this, "Couldn't save: " + ex.Message, "SSTV"); e.Cancel = true; } };
            _redraw.Tick += (o, e) => { _redraw.Stop(); redraw(); };
            _t.Style(this);
            refill(0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _redraw.Dispose();
                if (_photo != null) _photo.Dispose();
                if (_received != null) _received.Dispose();
                if (_preview.Image != null) _preview.Image.Dispose();
            }
            base.Dispose(disposing);
        }

        private SstvTemplate cur { get { int i = _names.SelectedIndex; return i >= 0 && i < _list.Count ? _list[i] : null; } }

        private void refill(int select)
        {
            _loading = true;
            _names.Items.Clear();
            foreach (SstvTemplate t in _list) _names.Items.Add(label(t));
            _loading = false;
            if (_list.Count > 0) _names.SelectedIndex = Math.Max(0, Math.Min(select, _list.Count - 1));
        }

        private static string label(SstvTemplate t) { return t.Kind == "custom" ? t.Name : t.Name + "  (" + t.Kind.ToUpperInvariant() + " button)"; }

        private void load()
        {
            SstvTemplate t = cur;
            if (t == null || _loading) return;
            _loading = true;
            _name.Text = t.Name;
            _title.Text = t.Title;
            _big.Text = t.Big;
            _lines.Text = (t.Lines ?? "").Replace("\r", "").Replace("\n", "\r\n");
            _bg.SelectedIndex = Math.Max(0, Array.IndexOf(BgValues, t.Background));
            _accent.BackColor = TemplateRenderer.parse(t.Accent, Color.Gold);
            _inset.Checked = t.Inset;
            _loading = false;
            redraw();
        }

        private void changed()
        {
            SstvTemplate t = cur;
            if (t == null || _loading) return;
            t.Name = _name.Text;
            t.Title = _title.Text;
            t.Big = _big.Text;
            t.Lines = _lines.Text.Replace("\r", "");
            t.Background = BgValues[Math.Max(0, _bg.SelectedIndex)];
            t.Inset = _inset.Checked;
            int i = _names.SelectedIndex;
            _loading = true;
            if (i >= 0 && (string)_names.Items[i] != label(t)) _names.Items[i] = label(t);
            _loading = false;
            _redraw.Start();
        }

        private void pickColour()
        {
            SstvTemplate t = cur;
            if (t == null) return;
            using (ColorDialog d = new ColorDialog { Color = _accent.BackColor, FullOpen = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _accent.BackColor = d.Color;
                t.Accent = TemplateRenderer.hex(d.Color);
                _redraw.Start();
            }
        }

        private void redraw()
        {
            SstvTemplate t = cur;
            if (t == null) return;
            SstvMode m = _st.CqMode;
            Image old = _preview.Image;
            _preview.Image = TemplateRenderer.Render(t, _st, new TemplateFields { Call = "W1AW", Rst = "595", Mode = m }, m, _photo, _received ?? SamplePicture());
            if (old != null) old.Dispose();
        }

        // something to show where the received picture goes, when none has come in yet
        internal static Bitmap SamplePicture()
        {
            Bitmap b = new Bitmap(320, 256);
            using (Graphics g = Graphics.FromImage(b))
            {
                Color[] bars = { Color.White, Color.Yellow, Color.Cyan, Color.Lime, Color.Magenta, Color.Red, Color.Blue, Color.Black };
                for (int i = 0; i < bars.Length; i++) using (Brush br = new SolidBrush(bars[i])) g.FillRectangle(br, i * 40, 0, 40, 170);
                using (Brush br = new SolidBrush(Color.FromArgb(20, 40, 80))) g.FillRectangle(br, 0, 170, 320, 86);
                using (Font f = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("W1AW", f, Brushes.White, 90, 190);
            }
            return b;
        }
    }
}
