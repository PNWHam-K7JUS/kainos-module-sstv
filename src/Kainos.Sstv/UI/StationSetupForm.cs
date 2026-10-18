/*  StationSetupForm.cs

The first-run setup (and Options > Station setup... later): the station's details asked once, with the CQ picture
made from them shown as they're typed. "Create my pictures" saves them and makes the CQ, Reply and 73 templates.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Sstv.UI
{
    internal class StationSetupForm : Form
    {
        private readonly IKainosHost _host;
        private readonly IKainosTheme _t;
        private readonly StationProfile _st;
        private readonly TextBox _call, _name, _qth, _grid, _rig, _ant, _pwr;
        private readonly ComboBox _mode;
        private readonly CheckBox _fsk, _replace;
        private readonly PictureBox _preview, _photoBox;
        private readonly Label _photoNote;
        private string _photoFile;              // chosen this time (not yet copied), or null
        private bool _photoRemoved;
        private Bitmap _photo;
        private readonly Timer _redraw = new Timer { Interval = 250 };

        public StationSetupForm(IKainosHost host, bool firstRun)
        {
            _host = host;
            _t = host.Theme;
            _st = StationProfile.Load(host);
            Text = firstRun ? "Welcome to SSTV: your station" : "SSTV: your station";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            int s(float v) => _t.S(v);
            ClientSize = new Size(s(820), s(470));
            Font = _t.Font(s(12), false);

            Label intro = new Label
            {
                Text = firstRun
                    ? "Tell SSTV about your station once. Your CQ, reply and 73 pictures are made from this, ready to send. You can change the details and the pictures any time (Options)."
                    : "Your station's details. \"Create my pictures\" remakes the CQ, Reply and 73 pictures from them.",
                Location = new Point(s(16), s(12)), Size = new Size(s(788), s(40)), AutoSize = false,
            };
            Controls.Add(intro);

            int y = s(60), lw = s(96), fw = s(250), rh = s(30);
            Func<string, string, string, TextBox> field = (label, value, tip) =>
            {
                Controls.Add(new Label { Text = label, Location = new Point(s(16), y + s(4)), Size = new Size(lw, s(20)) });
                TextBox tb = new TextBox { Text = value, Location = new Point(s(16) + lw, y), Width = fw };
                if (tip != null) new ToolTip().SetToolTip(tb, tip);
                tb.TextChanged += (o, e) => _redraw.Start();
                Controls.Add(tb);
                y += rh;
                return tb;
            };
            _call = field("Callsign", _st.Call, "Also sent after each picture as FSK ID, so other stations' software shows who sent it");
            _call.CharacterCasing = CharacterCasing.Upper;
            _name = field("Name", _st.Name, "Your first name, as you'd give it on the air");
            _qth = field("Town (QTH)", _st.Qth, "Where you are: town and state or country");
            _grid = field("Grid square", _st.Grid, null);
            _rig = field("Radio", string.IsNullOrEmpty(_st.Rig) ? "Hermes Lite 2" : _st.Rig, null);
            _ant = field("Antenna", _st.Ant, null);
            _pwr = field("Power", _st.Pwr, "e.g. 5 W");

            Controls.Add(new Label { Text = "CQ mode", Location = new Point(s(16), y + s(4)), Size = new Size(lw, s(20)) });
            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(s(16) + lw, y), Width = fw };
            foreach (SstvMode m in SstvModes.All) _mode.Items.Add(m);
            _mode.SelectedItem = _st.CqMode;
            _mode.SelectedIndexChanged += (o, e) => _redraw.Start();
            new ToolTip().SetToolTip(_mode, "The mode your CQs go out in. Scottie S1 and Martin M1 are the usual ones on HF; PD-120 for more detail. Replies go out in the mode the other station used.");
            Controls.Add(_mode);
            y += rh + s(4);

            _fsk = new CheckBox { Text = "Send my callsign after each picture (FSK ID)", Checked = _st.FskId, Location = new Point(s(16), y), AutoSize = true };
            Controls.Add(_fsk);
            y += rh;

            // the photo
            Controls.Add(new Label { Text = "Photo", Location = new Point(s(16), y + s(4)), Size = new Size(lw, s(20)) });
            _photoBox = new PictureBox { Location = new Point(s(16) + lw, y), Size = new Size(s(80), s(60)), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(_photoBox);
            Button choose = new Button { Text = "Choose...", Location = new Point(s(16) + lw + s(90), y), Size = new Size(s(80), s(26)) };
            Button remove = new Button { Text = "Remove", Location = new Point(s(16) + lw + s(176), y), Size = new Size(s(74), s(26)) };
            Controls.Add(choose);
            Controls.Add(remove);
            _photoNote = new Label { Text = "You, your shack or your antenna, behind the text.", Location = new Point(s(16) + lw + s(90), y + s(30)), Size = new Size(s(260), s(36)) };
            Controls.Add(_photoNote);
            choose.Click += (o, e) => pickPhoto();
            remove.Click += (o, e) => { _photoFile = null; _photoRemoved = true; setPhoto(null); };

            // the preview
            Controls.Add(new Label { Text = "Your CQ picture", Location = new Point(s(392), s(60)), AutoSize = true });
            _preview = new PictureBox { Location = new Point(s(392), s(84)), Size = new Size(s(412), s(330)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            Controls.Add(_preview);

            _replace = new CheckBox { Text = "Replace my pictures with fresh ones", Location = new Point(s(16), s(430)), AutoSize = true };
            bool have = File.Exists(TemplateStore.FileOf(host));
            _replace.Checked = !have;
            _replace.Visible = have;
            Controls.Add(_replace);

            Button ok = new Button { Text = have ? "Save" : "Create my pictures", DialogResult = DialogResult.OK, Location = new Point(s(560), s(426)), Size = new Size(s(150), s(32)) };
            Button cancel = new Button { Text = firstRun ? "Later" : "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(s(718), s(426)), Size = new Size(s(86), s(32)) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            _replace.CheckedChanged += (o, e) => ok.Text = _replace.Checked ? "Create my pictures" : "Save";

            string pp = _st.PhotoPath(host);
            if (pp != null && File.Exists(pp)) setPhoto(Gallery.Open(pp));
            _t.Style(this);
            _redraw.Tick += (o, e) => { _redraw.Stop(); redraw(); };
            Shown += (o, e) => redraw();
            FormClosing += (o, e) => { if (DialogResult == DialogResult.OK) save(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _redraw.Dispose();
                if (_photo != null) _photo.Dispose();
                if (_preview.Image != null) _preview.Image.Dispose();
            }
            base.Dispose(disposing);
        }

        private void pickPhoto()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = "Choose a photo", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Bitmap b = Gallery.Open(d.FileName);
                if (b == null) { MessageBox.Show(this, "That file couldn't be read as a picture.", "SSTV"); return; }
                _photoFile = d.FileName;
                _photoRemoved = false;
                setPhoto(b);
            }
        }

        private void setPhoto(Bitmap b)
        {
            if (_photo != null) _photo.Dispose();
            _photo = b;
            _photoBox.Image = b;
            _redraw.Start();
        }

        private StationProfile current()
        {
            return new StationProfile
            {
                Call = _call.Text.Trim().ToUpperInvariant(), Name = _name.Text.Trim(), Qth = _qth.Text.Trim(), Grid = _grid.Text.Trim(),
                Rig = _rig.Text.Trim(), Ant = _ant.Text.Trim(), Pwr = _pwr.Text.Trim(), Photo = _st.Photo,
                Mode = (_mode.SelectedItem as SstvMode ?? SstvModes.ByShort("S1")).Short, FskId = _fsk.Checked, Done = true,
            };
        }

        private void redraw()
        {
            StationProfile st = current();
            SstvTemplate cq = TemplateStore.Load(_host).FirstOrDefault(x => x.Kind == "cq") ?? TemplateStore.Defaults()[0];
            if (_replace.Checked) cq = TemplateStore.Defaults()[0];
            if (_photo == null && cq.Background == "photo") { cq = cq.Clone(); cq.Background = "gradient"; }
            Image old = _preview.Image;
            _preview.Image = TemplateRenderer.Render(cq, st, new TemplateFields { Mode = st.CqMode }, st.CqMode, _photo, null);
            if (old != null) old.Dispose();
        }

        private void save()
        {
            StationProfile st = current();
            try
            {
                Directory.CreateDirectory(_host.DataFolder);
                if (_photoRemoved && !string.IsNullOrEmpty(_st.Photo)) { st.Photo = ""; }
                if (_photoFile != null)
                {
                    // a copy kept with the module's data (the original may move)
                    foreach (string old in Directory.GetFiles(_host.DataFolder, "photo.*")) try { File.Delete(old); } catch { }
                    string name = "photo" + Path.GetExtension(_photoFile).ToLowerInvariant();
                    File.Copy(_photoFile, Path.Combine(_host.DataFolder, name), true);
                    st.Photo = name;
                }
                st.Save(_host);
                if (_replace.Checked || !File.Exists(TemplateStore.FileOf(_host))) TemplateStore.Save(_host, TemplateStore.Defaults());
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't save: " + ex.Message, "SSTV"); }
        }
    }
}
