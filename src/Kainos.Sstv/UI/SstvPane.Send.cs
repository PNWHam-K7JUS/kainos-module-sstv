/*  SstvPane.Send.cs

The send bar: who to (filled in from the FSK ID of the picture received), the report, the CQ / Reply / 73 buttons
and the other pictures (More), the mode (Auto: CQs in the station's mode, replies in the mode the other station
used), Send, Stop and Tune. A button shows the picture first; Send sends it.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Sstv.UI
{
    internal partial class SstvPane
    {
        private TextBox _to;
        private KButton _rst, _cq, _reply, _73, _more, _txMode, _send, _halt, _tune;
        private Label _toLabel;

        private SstvTemplate _pvTemplate;       // what's shown ready to send, or null
        private Bitmap _pvBitmap;
        private SstvMode _pvMode;
        private string _autoTo = "";            // the To box as last filled in from an FSK ID
        private SstvMode _theirMode;            // the mode of the picture last received (or picked in the gallery)
        private SstvMode _modeOverride;         // null: Auto
        private string _rstValue = "595";
        private volatile float _txProgress = -1;

        // for the self-test
        internal Bitmap PreviewBitmap { get { return _pvBitmap; } }
        internal SstvMode PreviewMode { get { return _pvMode; } }
        internal string To { get { return _to.Text; } }

        private void buildSendBar()
        {
            Func<string, string, KButton> btn = (text, tip) =>
            {
                KButton b = new KButton(_t, text);
                if (tip != null) new ToolTip().SetToolTip(b, tip);
                Controls.Add(b);
                return b;
            };
            _toLabel = new Label { Text = "To", ForeColor = _t.Dim, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = _t.Font(_t.S(11), false) };
            Controls.Add(_toLabel);
            _to = new TextBox { AutoSize = false, BackColor = _t.Raised, ForeColor = _t.Text, BorderStyle = BorderStyle.FixedSingle, CharacterCasing = CharacterCasing.Upper, MaxLength = 12, Font = _t.Font(_t.S(12), true) };
            new ToolTip().SetToolTip(_to, "Their callsign: filled in from the FSK ID of a picture received, or type it");
            Controls.Add(_to);
            _rst = btn("595", "The report you give them ({RST} in the pictures)");
            _cq = btn("CQ", "Show your CQ picture, ready to send");
            _reply = btn("Reply", "Show your reply to the last picture received (or the one picked in the gallery), in their mode");
            _73 = btn("73", "Show your 73 picture, ready to send");
            _more = btn("More", "Your other pictures, editing them, and your station's details");
            _txMode = btn("Mode: Auto", "The mode to send in. Auto: CQs in your mode (Options > Station setup), replies and 73s in the mode the other station used");
            _send = btn("Send", "Transmit the picture shown");
            _halt = btn("Stop", "Stop transmitting now");
            _tune = btn("Tune", "A steady 1900 Hz tone (up to 30 s) to set the transmit level: Options > Transmit level, so the radio gives full power with the ALC barely moving. Click again to stop.");

            _rst.Click += (s, e) => rstMenu().Show(_rst, new Point(0, _rst.Height));
            _cq.Click += (s, e) => previewKind("cq");
            _reply.Click += (s, e) => previewKind("reply");
            _73.Click += (s, e) => previewKind("73");
            _more.Click += (s, e) => moreMenu().Show(_more, new Point(0, _more.Height));
            _txMode.Click += (s, e) => txModeMenu().Show(_txMode, new Point(0, _txMode.Height));
            _send.Click += (s, e) => send();
            _halt.Click += (s, e) => _e.Halt();
            _tune.Click += (s, e) =>
            {
                if (_e.Sending) { _e.Halt(); return; }
                string why = _e.Tune();
                status(why ?? "Tune: 1900 Hz at " + _e.LevelDb.ToString("0") + " dB. Set Options > Transmit level for full power with the ALC barely moving.");
            };
            _to.TextChanged += (s, e) => { if (_pvTemplate != null && !_e.Sending) preview(_pvTemplate); };

            _e.LevelDb = Math.Max(-40, Math.Min(0, double.TryParse(_host.GetSetting("txlevel", "-10"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lv) ? lv : -10));
            _e.TxProgress += p => _txProgress = (float)p;
            _e.TxEnded += done => _host.BeginInvoke(() => txEnded(done));
            refreshSendBar();
        }

        private void layoutSendBar(int y, int bh)
        {
            int pad = _t.S(4), x = pad;
            Action<Control, int> place = (c, w) => { c.SetBounds(x, y, _t.S(w), bh); x += _t.S(w) + pad; };
            place(_toLabel, 22); place(_to, 100); place(_rst, 48); x += pad;
            place(_cq, 44); place(_reply, 56); place(_73, 40); place(_more, 56);
            int right = Width - pad;
            _tune.SetBounds(right - _t.S(52), y, _t.S(52), bh); right -= _t.S(52) + pad;
            _halt.SetBounds(right - _t.S(52), y, _t.S(52), bh); right -= _t.S(52) + pad;
            _send.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad * 2;
            _txMode.SetBounds(right - _t.S(100), y, _t.S(100), bh);
        }

        private void refreshSendBar()
        {
            bool tx = _e.Sending;
            _send.Enabled = !tx && _pvTemplate != null;
            _send.On = !tx && _pvTemplate != null;
            _halt.Enabled = tx;
            _tune.On = tx && _e.Tuning;
            _tune.Enabled = !tx || _e.Tuning;
            _cq.Enabled = _reply.Enabled = _73.Enabled = _more.Enabled = !tx;
            _txMode.Text = "Mode: " + (_modeOverride == null ? "Auto" : _modeOverride.Short);
            _rst.Text = _rstValue;
        }

        // ---- the pictures ----

        private StationProfile profile() { return StationProfile.Load(_host); }

        // the mode a template goes out in
        private SstvMode modeFor(SstvTemplate t, StationProfile st)
        {
            if (_modeOverride != null) return _modeOverride;
            if (t.Kind == "cq" || _theirMode == null) return st.CqMode;
            return _theirMode;
        }

        // the received picture for the inset: the one picked in the gallery, else the last received
        private Bitmap receivedPicture() { return _savedItem != null ? _saved : _live; }

        private void previewKind(string kind)
        {
            SstvTemplate t = TemplateStore.Load(_host).FirstOrDefault(x => x.Kind == kind);
            if (t == null) { status("There's no " + kind.ToUpperInvariant() + " picture: More > Edit pictures... (Defaults puts it back)"); return; }
            if (kind != "cq" && _to.Text.Trim().Length == 0)
            {
                status("Who to? Type their callsign in To (it's filled in from a picture's FSK ID when it has one)");
                _to.Focus();
                return;
            }
            preview(t);
        }

        internal void preview(SstvTemplate t)
        {
            StationProfile st = profile();
            if (!st.Done) { if (!RunSetup(true)) return; st = profile(); }
            SstvMode m = modeFor(t, st);
            Bitmap photo = null;
            string pp = st.PhotoPath(_host);
            if (pp != null) photo = Gallery.Open(pp);
            Bitmap rec = receivedPicture();
            SstvTemplate use = t;
            if (photo == null && t.Background == "photo") { use = t.Clone(); use.Background = "gradient"; }
            if (rec == null && t.Background == "received") { use = use.Clone(); use.Background = photo != null ? "photo" : "gradient"; }
            Bitmap b = TemplateRenderer.Render(use, st, new TemplateFields { Call = _to.Text.Trim(), Rst = _rstValue, Mode = m }, m, photo, rec);
            if (photo != null) photo.Dispose();
            if (_pvBitmap != null && _view.Image == _pvBitmap) _view.Image = null;
            if (_pvBitmap != null) _pvBitmap.Dispose();
            _pvBitmap = b;
            _pvTemplate = t;
            _pvMode = m;
            _view.Image = b;
            _view.Progress = -1;
            _view.Caption = "Ready: " + t.Name + (t.Kind != "cq" && _to.Text.Trim().Length > 0 ? " to " + _to.Text.Trim() : "") + ", " + m.Name + " (" + m.Duration.ToString("0") + " s). Send to transmit.";
            _view.Invalidate();
            status("Ready to send " + t.Name + " in " + m.Name + " (" + m.Duration.ToString("0") + " s): press Send");
            refreshSendBar();
        }

        private void clearPreview()
        {
            if (_e.Sending) return;
            _pvTemplate = null;
            if (_pvBitmap != null) { if (_view.Image == _pvBitmap) _view.Image = null; _pvBitmap.Dispose(); _pvBitmap = null; }
            refreshSendBar();
        }

        internal void send()
        {
            if (_pvTemplate == null || _pvBitmap == null) { status("Pick a picture first (CQ, Reply, 73 or More)"); return; }
            if (!_host.Radio.PowerOn) { status("The radio is off"); return; }
            StationProfile st = profile();
            string fsk = st.FskId && st.Call.Length > 0 ? st.Call : null;
            Bitmap pic = new Bitmap(_pvBitmap);
            SstvMode m = _pvMode;
            string name = _pvTemplate.Name;
            _send.Enabled = false;
            status("Getting " + name + " ready...");
            // the audio is made off the window's thread (a long PD picture takes a moment)
            Task.Run(() => { try { return _e.BuildTx(pic, m, fsk); } finally { pic.Dispose(); } }).ContinueWith(task =>
            {
                _host.BeginInvoke(() =>
                {
                    if (task.IsFaulted) { status("Couldn't make the audio: " + task.Exception.GetBaseException().Message); refreshSendBar(); return; }
                    string why = _e.Send(task.Result);
                    if (why != null) { status(why); refreshSendBar(); return; }
                    _txProgress = 0;
                    _view.ProgressColor = _t.Tx;
                    _view.Caption = "Sending: " + name + (_to.Text.Trim().Length > 0 && _pvTemplate.Kind != "cq" ? " to " + _to.Text.Trim() : "") + ", " + m.Name + "   (Stop ends it)";
                    _host.Log("sending " + name + (_to.Text.Length > 0 ? " to " + _to.Text : "") + " in " + m.Name + (fsk != null ? ", FSK ID " + fsk : ""));
                    status("Sending " + name + " in " + m.Name);
                    refreshSendBar();
                });
            });
        }

        private void txEnded(bool done)
        {
            _txProgress = -1;
            _view.Progress = -1;
            _view.ProgressColor = _t.Gold;
            _view.Invalidate();
            if (_pvTemplate != null)
            {
                status(done ? "Sent " + _pvTemplate.Name + " in " + _pvMode.Name : "Stopped sending");
                _view.Caption = (done ? "Sent: " : "Stopped: ") + _pvTemplate.Name + ", " + _pvMode.Name + "   (Send again, or click to go back to live)";
            }
            else status(done ? "Tune ended" : "Tune stopped");
            refreshSendBar();
        }

        // each tick while sending: the progress line and time left
        private void sendTick()
        {
            float p = _txProgress;
            if (_e.Sending && !_e.Tuning && p >= 0 && _pvBitmap != null)
            {
                if (_view.Image != _pvBitmap) _view.Image = _pvBitmap;
                // the picture starts after the header (about 0.9 s) and the lead-in
                _view.Progress = Math.Min(0.999, p);
                _view.Invalidate();
                _statusText = "Sending " + _pvTemplate.Name + " in " + _pvMode.Name + ": " + (p * 100).ToString("0") + " %, " + _e.TxSecondsLeft.ToString("0") + " s left";
                _status.Text = _statusText;
                _statusAt = DateTime.UtcNow;
            }
            if (_halt.Enabled != _e.Sending) refreshSendBar();
        }

        // a picture came in: their callsign and mode for the reply
        private void receivedFrom(string call, SstvMode m)
        {
            if (m != null) _theirMode = m;
            if (string.IsNullOrEmpty(call)) return;
            string cur = _to.Text.Trim();
            if (cur.Length == 0 || cur == _autoTo) { _to.Text = call; _autoTo = call; }
        }

        // ---- menus ----

        private ContextMenuStrip rstMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            foreach (string r in new[] { "595", "585", "575", "565", "555", "545", "535", "525", "515" })
            {
                string rr = r;
                ToolStripMenuItem it = new ToolStripMenuItem(r + "   " + rstWords(r)) { Checked = r == _rstValue };
                it.Click += (s, e) => { _rstValue = rr; refreshSendBar(); if (_pvTemplate != null) preview(_pvTemplate); };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        // RSV: readability, signal strength, video (picture) quality
        private static string rstWords(string r)
        {
            int v = r[2] - '0';
            return v >= 5 ? "(perfect picture)" : v == 4 ? "(slight noise)" : v == 3 ? "(noisy)" : v == 2 ? "(very noisy)" : "(barely there)";
        }

        private ContextMenuStrip moreMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            List<SstvTemplate> all = TemplateStore.Load(_host);
            foreach (SstvTemplate t in all.Where(x => x.Kind == "custom"))
            {
                SstvTemplate tt = t;
                ToolStripMenuItem it = new ToolStripMenuItem(t.Name);
                it.Click += (s, e) => preview(tt);
                m.Items.Add(it);
            }
            if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem edit = new ToolStripMenuItem("Edit pictures...");
            edit.Click += (s, e) => EditTemplates();
            m.Items.Add(edit);
            ToolStripMenuItem setup = new ToolStripMenuItem("Station setup...");
            setup.Click += (s, e) => RunSetup(false);
            m.Items.Add(setup);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private ContextMenuStrip txModeMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem auto = new ToolStripMenuItem("Auto: CQ in " + profile().CqMode.Name + ", replies in theirs" + (_theirMode != null ? " (" + _theirMode.Name + ")" : "")) { Checked = _modeOverride == null };
            auto.Click += (s, e) => { _modeOverride = null; modeChanged(); };
            m.Items.Add(auto);
            m.Items.Add(new ToolStripSeparator());
            foreach (SstvMode mode in SstvModes.All)
            {
                SstvMode mm = mode;
                ToolStripMenuItem it = new ToolStripMenuItem(mode.Name + "   (" + mode.Duration.ToString("0") + " s)") { Checked = _modeOverride == mode };
                it.Click += (s, e) => { _modeOverride = mm; modeChanged(); };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private void modeChanged()
        {
            refreshSendBar();
            if (_pvTemplate != null) preview(_pvTemplate);
        }

        private ToolStripMenuItem levelMenu()
        {
            ToolStripMenuItem lv = new ToolStripMenuItem("Transmit level") { ToolTipText = "Set with Tune: full power with the ALC barely moving" };
            foreach (int db in new[] { 0, -3, -6, -10, -15, -20, -25, -30 })
            {
                int d = db;
                ToolStripMenuItem it = new ToolStripMenuItem(db + " dB") { Checked = Math.Abs(_e.LevelDb - db) < 0.5 };
                it.Click += (s, e) => { _e.LevelDb = d; _host.SetSetting("txlevel", d.ToString(CultureInfo.InvariantCulture)); status("Transmit level " + d + " dB" + (_e.Tuning ? " (takes effect on the next Tune)" : "")); };
                lv.DropDownItems.Add(it);
            }
            return lv;
        }

        // ---- the forms ----

        // the first-run setup; true if saved
        internal bool RunSetup(bool firstRun)
        {
            using (StationSetupForm f = new StationSetupForm(_host, firstRun))
            {
                bool ok = f.ShowDialog(FindForm()) == DialogResult.OK;
                if (ok) { status("Station saved: your CQ, Reply and 73 pictures are ready (More > Edit pictures... to change them)"); if (_pvTemplate != null) preview(_pvTemplate); }
                return ok;
            }
        }

        internal void EditTemplates()
        {
            using (TemplateEditorForm f = new TemplateEditorForm(_host, receivedPicture()))
                if (f.ShowDialog(FindForm()) == DialogResult.OK && _pvTemplate != null)
                {
                    SstvTemplate again = TemplateStore.Load(_host).FirstOrDefault(x => x.Name == _pvTemplate.Name) ?? TemplateStore.Load(_host).FirstOrDefault(x => x.Kind == _pvTemplate.Kind);
                    if (again != null) preview(again); else clearPreview();
                }
        }

        // the first time SSTV is opened: the setup, once
        public void FirstRunIfNeeded()
        {
            if (profile().Done || _host.GetSetting("st.asked", "0") == "1") return;
            _host.SetSetting("st.asked", "1");
            RunSetup(true);
        }
    }
}
