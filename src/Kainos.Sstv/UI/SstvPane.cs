/*  SstvPane.cs

The SSTV window: a bar (band, receiver, skip, pictures folder, pop out, options), the picture as it arrives, its
details with a tuning spectrum, and the gallery of received pictures along the bottom.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Sstv.UI
{
    internal partial class SstvPane : Panel
    {
        private readonly IKainosHost _host;
        private readonly IKainosTheme _t;
        private readonly SstvEngine _e;
        private readonly string _folder;

        private readonly KButton _band, _rxSel, _startBtn, _skip, _adjust, _afcBtn, _sensBtn, _openFolder, _pop, _options;
        private AdjustForm _adjustForm;
        private static readonly string[] SensNames = { "Low", "Normal", "High" };
        private readonly Label _status;
        private readonly PictureView _view;
        private readonly KainosCrispHost _viewHost;
        private readonly DetailsView _details;
        private readonly GalleryStrip _gallery;
        private readonly Timer _timer;
        private bool _built;

        // the picture being received, and a saved one being looked at (null: showing the live one)
        private SstvPicture _livePic;
        private Bitmap _live, _saved;
        private GalleryItem _savedItem;
        private volatile bool _dirty;
        private string _statusText = "";
        private DateTime _statusAt;
        private string _markersFor = "";

        public event Action PopToggled;

        // the SSTV calling frequencies (dial, and the sideband used there)
        private static readonly string[][] Bands =
        {
            new[] { "80 m", "3845000", "LSB" },
            new[] { "40 m", "7171000", "LSB" },
            new[] { "20 m", "14230000", "USB" },
            new[] { "20 m (alt)", "14233000", "USB" },
            new[] { "15 m", "21340000", "USB" },
            new[] { "10 m", "28680000", "USB" },
            new[] { "6 m", "50950000", "USB" },
            // the ISS's SSTV events (PD-120 / PD-180), on 2 m FM: with a transverter (Setup > XVTRs) or a VHF radio
            new[] { "2 m ISS", "145800000", "FM" },
        };

        public SstvPane(IKainosHost host, SstvEngine engine)
        {
            _host = host;
            _t = host.Theme;
            _e = engine;
            _folder = Path.Combine(host.DataFolder, "Received");
            BackColor = _t.Bg;
            DoubleBuffered = true;

            Func<string, string, KButton> btn = (text, tip) =>
            {
                KButton b = new KButton(_t, text);
                if (tip != null) new ToolTip().SetToolTip(b, tip);
                Controls.Add(b);
                return b;
            };
            _band = btn("Band", "Tune to a band's SSTV frequency (14.230 USB, 7.171 LSB...)");
            _rxSel = btn("RX1", "The receiver listened to: RX1 or RX2");
            _startBtn = btn("Start", "Start a picture now in a mode you pick, for when its header was missed (weak, or you tuned in part way). Normally the mode is found by itself.");
            _skip = btn("Skip", "End the picture being received now (it's kept if a quarter or more came in). It isn't started again from its line sync for a minute.");
            _adjust = btn("Adjust", "Straighten the last picture received, or move it left or right, if it came out slanted or wrapped round. It's drawn again from its audio (kept for 5 minutes).");
            _afcBtn = btn("AFC", "AFC: follow the tuning from the pictures' sync pulses, for the right colours and a steady picture when you're a little off frequency. Lit: on.");
            _sensBtn = btn("Normal", "Sensitivity: how readily a picture is found without its header, and how long one is held through a fade before it's ended (Low 10 s, Normal 20 s, High 30 s). Click to change.");
            _openFolder = btn("Pictures", "Open the folder the received pictures are saved in");
            _pop = btn("Pop out", "Docked under the panadapter, or in its own window");
            _options = btn("Options", "Saving, panadapter markers and display");

            _status = new Label { ForeColor = _t.Dim, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = _t.Font(_t.S(11), false) };
            Controls.Add(_status);

            _view = new PictureView(_t) { Placeholder = "Listening for SSTV.\nPictures appear here as they arrive: the mode is found by itself (Martin, Scottie, Robot 36, PD)." };
            _viewHost = new KainosCrispHost(_view, host.GetSetting("crisp", "1") == "1");     // the picture at the screen's own resolution
            Controls.Add(_viewHost);
            _details = new DetailsView(_t);
            Controls.Add(_details);
            _gallery = new GalleryStrip(_t);
            Controls.Add(_gallery);

            buildSendBar();
            wire();
            _e.Rx = host.GetSetting("rx", "0") == "1" ? 1 : 0;
            applyReceiveSettings();
            _rxSel.Text = "RX" + (_e.Rx + 1);
            _gallery.SetItems(Gallery.Load(_folder, 300));

            _timer = new Timer { Interval = 100 };      // the waterfall gets a line every 100 ms
            _timer.Tick += (s, e) => tick();
            _timer.Start();
            _built = true;
            PerformLayout();
            showDetails();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop(); _timer.Dispose();
                if (_live != null) _live.Dispose();
                if (_saved != null) _saved.Dispose();
                if (_pvBitmap != null) _pvBitmap.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- layout ----

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!_built) return;
            int pad = _t.S(4), bh = _t.S(24), x = pad, y = pad;
            Action<Control, int> place = (c, w) => { c.SetBounds(x, y, _t.S(w), bh); x += _t.S(w) + pad; };
            place(_band, 64); place(_rxSel, 44); x += pad;
            place(_startBtn, 52); place(_skip, 52); place(_adjust, 60); x += pad;
            place(_afcBtn, 44); place(_sensBtn, 70); x += pad;
            place(_openFolder, 70);
            int right = Width - pad;
            _options.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _pop.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _status.SetBounds(x + pad, y, Math.Max(0, right - x - pad), bh);

            y += bh + pad;
            layoutSendBar(y, bh);
            y += bh + pad;
            int galH = Math.Max(_t.S(64), Math.Min(_t.S(112), (Height - y) * 22 / 100));
            int detW = _t.S(230);
            int midH = Math.Max(_t.S(40), Height - y - galH - 2 * pad);
            _viewHost.SetBounds(pad, y, Math.Max(_t.S(40), Width - 3 * pad - detW), midH);
            _details.SetBounds(Width - pad - detW, y, detW, midH);
            _gallery.SetBounds(pad, y + midH + pad, Width - 2 * pad, galH);
        }

        // ---- wiring ----

        private void wire()
        {
            _band.Click += (s, e) => bandMenu().Show(_band, new Point(0, _band.Height));
            _rxSel.Click += (s, e) =>
            {
                _e.Rx = 1 - _e.Rx;
                _rxSel.Text = "RX" + (_e.Rx + 1);
                _host.SetSetting("rx", _e.Rx.ToString(CultureInfo.InvariantCulture));
                _e.Listen();
                _markersFor = "";
                _host.SetMarkers(1 - _e.Rx, null);
            };
            _skip.Click += (s, e) => _e.Cut();
            _adjust.Click += (s, e) => openAdjust();
            _afcBtn.Click += (s, e) => { _host.SetSetting("afc", _e.Decoder.Afc ? "0" : "1"); applyReceiveSettings(); };
            _sensBtn.Click += (s, e) =>
            {
                _host.SetSetting("sens", ((_e.Decoder.Sensitivity + 1) % 3).ToString(CultureInfo.InvariantCulture));
                applyReceiveSettings();
                status("Sensitivity " + SensNames[_e.Decoder.Sensitivity].ToLowerInvariant());
            };
            _e.Redrawn += (p, ok) => _host.BeginInvoke(() => redrawn(p, ok));
            _e.Decoder.Resumed += p => _host.BeginInvoke(() => resumed(p));
            _startBtn.Click += (s, e) => startMenu().Show(_startBtn, new Point(0, _startBtn.Height));
            _openFolder.Click += (s, e) => openFolder();
            _pop.Click += (s, e) => PopToggled?.Invoke();
            _options.Click += (s, e) => optionsMenu().Show(_options, new Point(0, _options.Height));
            // a click on the picture goes back to the live one (from a saved picture, or one shown ready to send)
            _view.Click += (s, e) =>
            {
                if (_e.Sending) return;
                if (_pvTemplate != null) { clearPreview(); showLive(); }
                else if (_savedItem != null) showLive();
            };
            _gallery.Picked += show;
            _gallery.Menu += (it, p) => itemMenu(it).Show(_gallery, p);

            _e.Status += s => _host.BeginInvoke(() => status(s));
            _e.Decoder.Progress += p => _dirty = true;
            _e.Decoder.Finished += p => _host.BeginInvoke(() => finished(p));
            _e.Decoder.Abandoned += p => _host.BeginInvoke(() => abandoned(p));
            _e.SpectrumReady += () => _specDirty = true;
            // headers that nearly passed: in the log, and briefly in the status line
            _e.Decoder.Diag += d =>
            {
                _host.Log("vis: " + d);
                string say = d.StartsWith("header") ? "Heard what may be a " + d : char.ToUpperInvariant(d[0]) + d.Substring(1);
                _host.BeginInvoke(() => status(say));
            };
            _status.Click += (s, e) => { if (_e.Lost) { _e.Listen(); status("Listening again"); } };
        }

        private volatile bool _specDirty;

        private ContextMenuStrip bandMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            double dial = _host.Radio.VfoHz(_e.Rx);
            foreach (string[] b in Bands)
            {
                double f = double.Parse(b[1], CultureInfo.InvariantCulture);
                string mode = b[2];
                if (mode == "FM") m.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem it = new ToolStripMenuItem(b[0] + "   " + (f / 1e6).ToString("0.000", CultureInfo.InvariantCulture) + " " + mode) { Checked = Math.Abs(dial - f) < 500 };
                if (mode == "FM") it.ToolTipText = "The ISS sends SSTV (usually PD-120 or PD-180) on 145.800 FM during its events (ARISS). Needs a 2 m transverter (Setup > XVTRs) or a VHF radio; Doppler is no trouble on FM.";
                it.Click += (s, e) =>
                {
                    // DIGU / DIGL: the audio straight through, as for the other digital modes; FM for the ISS
                    _host.Radio.SetMode(_e.Rx, mode == "FM" ? "FM" : mode == "LSB" ? "DIGL" : "DIGU");
                    _host.Radio.TuneVfo(_e.Rx, f);
                    _markersFor = "";
                };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private ContextMenuStrip startMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            foreach (SstvMode mode in SstvModes.All)
            {
                SstvMode mm = mode;
                ToolStripMenuItem it = new ToolStripMenuItem(mode.Name);
                it.Click += (s, e) => { _e.StartNow(mm); status("Starting " + mm.Name + " now: looking for its line sync"); };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private void saveAudio()
        {
            try
            {
                string dir = Path.Combine(_host.DataFolder, "Recordings");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z_" +
                    (_host.Radio.VfoHz(_e.Rx) / 1e3).ToString("0", CultureInfo.InvariantCulture) + "kHz_" + _host.Radio.Mode(_e.Rx) + ".wav");
                double sec = _e.SaveRecording(path);
                if (sec <= 0) { status("Nothing heard yet to save"); return; }
                status("Saved the last " + sec.ToString("0") + " s of audio: " + Path.GetFileName(path));
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex) { status("Couldn't save the audio: " + ex.Message); }
        }

        private ContextMenuStrip optionsMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            Func<string, string, string, string, ToolStripMenuItem> check = (text, key, def, tip) =>
            {
                ToolStripMenuItem it = new ToolStripMenuItem(text) { Checked = _host.GetSetting(key, def) == "1", ToolTipText = tip };
                it.Click += (s, e) => { _host.SetSetting(key, it.Checked ? "0" : "1"); _markersFor = ""; };
                m.Items.Add(it);
                return it;
            };
            ToolStripMenuItem auto = check("Start pictures without a header (from their line sync)", "autostart", "1", "When a picture's header was missed (a fade, QRM, or you tuned in part way), find its mode from the rhythm of its line sync and start it");
            auto.Click += (s, e) => applyReceiveSettings();
            ToolStripMenuItem stop = check("End those when their signal goes (auto stop)", "autostop", "1", "A picture found without its header ends once its signal has been gone for a while (it carries on if the signal comes back soon after). Off: it runs a whole picture's length, and Skip or the next header ends it.");
            stop.Click += (s, e) => applyReceiveSettings();
            ToolStripMenuItem afc = check("AFC: follow the tuning from the sync pulses", "afc", "1", "For the right colours and a steady picture when you're a little off frequency");
            afc.Click += (s, e) => applyReceiveSettings();
            m.Items.Add(sensitivityMenu());
            m.Items.Add(modesMenu());
            m.Items.Add(new ToolStripSeparator());
            check("Keep pictures that were cut short", "savecut", "1", "Save a picture that ended early (signal lost, or Skip) if a quarter or more of it came in");
            check("Show the SSTV tones on the panadapter", "markers", "1", "Lines at sync (1200 Hz), black (1500 Hz) and white (2300 Hz) from the VFO, to tune by");
            check("Log window after sending a 73", "log73", "1", "Open Log QSO, filled in, when your 73 picture has gone out");
            check("Send logged QSOs to logging programs", "udp.on", "1", "As WSJT-X does (UDP \"Logged ADIF\"), for Log4OM, N1MM+, HRD, DXKeeper and GridTracker");
            check("Sharp picture on high-DPI screens", "crisp", "1", "Draw the picture at the screen's own resolution at 125 % / 150 % scaling (takes effect when Kainos restarts)");
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(levelMenu());
            ToolStripMenuItem setup = new ToolStripMenuItem("Station setup...");
            setup.Click += (s, e) => RunSetup(false);
            m.Items.Add(setup);
            ToolStripMenuItem edit = new ToolStripMenuItem("Edit pictures...");
            edit.Click += (s, e) => EditTemplates();
            m.Items.Add(edit);
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem rec = new ToolStripMenuItem("Save the last 3 minutes of audio") { ToolTipText = "A WAV of what the module heard, for checking a signal that didn't decode" };
            rec.Click += (s, e) => saveAudio();
            m.Items.Add(rec);
            ToolStripMenuItem addr = new ToolStripMenuItem("Logging programs address...");
            addr.Click += (s, e) => loggerAddress();
            m.Items.Add(addr);
            ToolStripMenuItem logf = new ToolStripMenuItem("Open the log folder");
            logf.Click += (s, e) => Process.Start("explorer.exe", "/select,\"" + SstvLog.FileOf(_host.DataFolder) + "\"");
            m.Items.Add(logf);
            ToolStripMenuItem open = new ToolStripMenuItem("Open the pictures folder");
            open.Click += (s, e) => openFolder();
            m.Items.Add(open);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        // ---- receive settings ----

        private void applyReceiveSettings()
        {
            SstvDecoder d = _e.Decoder;
            d.AutoStart = _host.GetSetting("autostart", "1") == "1";
            d.AutoStop = _host.GetSetting("autostop", "1") == "1";
            d.Afc = _host.GetSetting("afc", "1") == "1";
            int sens;
            d.Sensitivity = int.TryParse(_host.GetSetting("sens", "1"), out sens) && sens >= 0 && sens <= 2 ? sens : 1;
            string modes = _host.GetSetting("automodes", "");
            d.AutoModes = modes.Length == 0 ? null : new HashSet<string>(modes.Split(','), StringComparer.OrdinalIgnoreCase);
            _afcBtn.On = d.Afc;
            _sensBtn.Text = SensNames[d.Sensitivity];
        }

        private ToolStripMenuItem sensitivityMenu()
        {
            ToolStripMenuItem m = new ToolStripMenuItem("Sensitivity") { ToolTipText = "How readily a picture is found without its header, and how long one is held through a fade" };
            string[] tips =
            {
                "Fewer false starts; a picture without its header is ended 10 s after its signal goes",
                "A picture without its header is ended 20 s after its signal goes",
                "Finds weaker pictures without their header; holds one 30 s through a fade",
            };
            for (int i = 0; i < 3; i++)
            {
                int v = i;
                ToolStripMenuItem it = new ToolStripMenuItem(SensNames[i]) { Checked = _e.Decoder.Sensitivity == i, ToolTipText = tips[i] };
                it.Click += (s, e) => { _host.SetSetting("sens", v.ToString(CultureInfo.InvariantCulture)); applyReceiveSettings(); };
                m.DropDownItems.Add(it);
            }
            return m;
        }

        // the modes looked for without a header (a header is always believed): fewer means fewer false starts
        private ToolStripMenuItem modesMenu()
        {
            ToolStripMenuItem m = new ToolStripMenuItem("Modes found without a header") { ToolTipText = "Which modes are looked for from their line sync when a header was missed. A picture with its header is received in any mode." };
            HashSet<string> on = _e.Decoder.AutoModes;
            Action<IEnumerable<string>> set = list =>
            {
                string v = list == null ? "" : string.Join(",", list);
                _host.SetSetting("automodes", v.Length == 0 && list != null ? "none" : v);
                applyReceiveSettings();
            };
            ToolStripMenuItem all = new ToolStripMenuItem("All") { Checked = on == null };
            all.Click += (s, e) => set(null);
            m.DropDownItems.Add(all);
            ToolStripMenuItem common = new ToolStripMenuItem("The usual HF ones") { ToolTipText = "Martin M1, Scottie S1, Scottie DX, Robot 36, PD-120: most of what's sent on HF" };
            common.Click += (s, e) => set(new[] { "M1", "S1", "SDX", "R36", "PD120" });
            m.DropDownItems.Add(common);
            m.DropDownItems.Add(new ToolStripSeparator());
            foreach (SstvMode mode in SstvModes.All)
            {
                string sh = mode.Short;
                ToolStripMenuItem it = new ToolStripMenuItem(mode.Name) { Checked = on == null || on.Contains(sh) };
                it.Click += (s, e) =>
                {
                    HashSet<string> cur = _e.Decoder.AutoModes != null ? new HashSet<string>(_e.Decoder.AutoModes, StringComparer.OrdinalIgnoreCase)
                                                                       : new HashSet<string>(SstvModes.All.Select(x => x.Short), StringComparer.OrdinalIgnoreCase);
                    if (!cur.Remove(sh)) cur.Add(sh);
                    set(cur.Count == SstvModes.All.Length ? null : SstvModes.All.Select(x => x.Short).Where(cur.Contains).ToList());
                };
                m.DropDownItems.Add(it);
            }
            return m;
        }

        // ---- Adjust: the last picture received, straightened or moved, drawn again from its audio ----

        private SstvPicture _adjustPic;

        private bool canAdjust { get { return _livePic != null && _livePic.Complete && _livePic.State != null && !_e.Sending; } }

        private void openAdjust()
        {
            if (_adjustForm != null && !_adjustForm.IsDisposed) { _adjustForm.Activate(); return; }
            if (!canAdjust) { status("Adjust works on the last picture received, once it's in"); return; }
            clearPreview();
            showLive();
            SstvPicture p = _livePic;
            _adjustPic = p;
            AdjustForm f = new AdjustForm(_host, p.Mode.Name + " " + p.StartUtc.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z");
            _adjustForm = f;
            f.Changed += () => _e.Redraw(p, f.Ppm, f.Ms);
            f.SaveWanted += () => { savePicture(p); status("Saved the adjusted picture"); };
            f.FormClosed += (s, e) =>
            {
                if (!f.Saved) _e.Redraw(p, 0, 0);       // not saved: back as it was
                _adjustForm = null;
                _adjustPic = null;
            };
            f.Show(FindForm());
        }

        private void redrawn(SstvPicture p, bool ok)
        {
            if (!ok)
            {
                if (_adjustForm != null && p == _adjustPic) _adjustForm.Note = "Its audio has gone (only the last 5 minutes are kept)";
                return;
            }
            if (_adjustForm != null && p == _adjustPic) _adjustForm.Note = "";
            if (p == _livePic && _live != null)
            {
                Gallery.CopyInto(p, _live);
                if (showingLive) _view.Invalidate();
                showDetails();
            }
        }

        // an ended picture's signal came back in step: the same picture carries on
        private void resumed(SstvPicture p)
        {
            if (p != _livePic)
            {
                _livePic = p;
                if (_live != null) _live.Dispose();
                _live = new Bitmap(p.Mode.Width, p.Mode.Lines, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                Gallery.CopyInto(p, _live);
                if (_pvTemplate == null) showLive();
            }
            if (_adjustForm != null && p == _adjustPic) _adjustForm.Close();
            status("Signal back: carrying on with the " + p.Mode.Name + " picture");
        }

        // saves a picture (in place of an earlier save of it: the name comes from its start time, mode and callsign)
        private GalleryItem savePicture(SstvPicture p)
        {
            bool complete = p.LinesDone >= p.Mode.Lines - 2;
            GalleryItem it = Gallery.Save(_folder, p, _host.Radio.VfoHz(_e.Rx), complete);
            foreach (GalleryItem old in _gallery.Items.Where(x => string.Equals(x.Path, it.Path, StringComparison.OrdinalIgnoreCase)).ToList()) _gallery.Remove(old);
            _gallery.Insert(it);
            return it;
        }

        private void openFolder()
        {
            Directory.CreateDirectory(_folder);
            Process.Start("explorer.exe", "\"" + _folder + "\"");
        }

        private ContextMenuStrip itemMenu(GalleryItem it)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            Action<string, Action> add = (text, a) => { ToolStripMenuItem i = new ToolStripMenuItem(text); i.Click += (s, e) => a(); m.Items.Add(i); };
            add("Open", () => { try { Process.Start(it.Path); } catch (Exception ex) { status("Can't open it: " + ex.Message); } });
            add("Show in folder", () => Process.Start("explorer.exe", "/select,\"" + it.Path + "\""));
            add("Copy", () => { using (Bitmap b = Gallery.Open(it.Path)) if (b != null) Clipboard.SetImage(b); });
            m.Items.Add(new ToolStripSeparator());
            add("Delete (to the Recycle Bin)", () =>
            {
                try
                {
                    foreach (string f in new[] { it.Path, Path.ChangeExtension(it.Path, ".json") })
                        if (File.Exists(f)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    if (_savedItem == it) showLive();
                    _gallery.Remove(it);
                }
                catch (Exception ex) { status("Can't delete it: " + ex.Message); }
            });
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        // ---- the picture ----

        private void tick()
        {
            SstvPicture cur = _e.Decoder.Current;
            if (cur != null && cur != _livePic && !cur.Complete) startLive(cur);
            if (_dirty && _livePic != null && _live != null)
            {
                _dirty = false;
                Gallery.CopyInto(_livePic, _live);
                if (showingLive) { _view.Progress = _livePic.Complete ? -1 : _livePic.LinesDone / (double)_livePic.Mode.Lines; _view.Invalidate(); }
                showDetails();
            }
            if (_specDirty)
            {
                _specDirty = false;
                foreach (float[] line in _e.TakeLines()) _details.AddLine(line);
                _details.PeakDb = _e.PeakDb;
                _details.Invalidate();
            }
            markers();
            sendTick();
            if (DateTime.UtcNow - _statusAt > TimeSpan.FromSeconds(8)) status(null);
        }

        private void startLive(SstvPicture p)
        {
            _livePic = p;
            if (_live != null) _live.Dispose();
            _live = new Bitmap(p.Mode.Width, p.Mode.Lines, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(_live)) g.Clear(Color.Black);
            if (_pvTemplate == null) showLive();
            status("Receiving " + p.Mode.Name + (p.NoHeader ? " (no header: found from its line sync)" : "") + (_pvTemplate != null ? " (click the picture to watch it)" : ""));
        }

        private void showLive()
        {
            _savedItem = null;
            _gallery.Selected = null;
            _gallery.Invalidate();
            if (_saved != null) { _saved.Dispose(); _saved = null; }
            _view.Image = _live;
            _view.Caption = _livePic == null ? "" : captionOf(_livePic);
            _view.Progress = _livePic != null && !_livePic.Complete ? _livePic.LinesDone / (double)_livePic.Mode.Lines : -1;
            _view.Invalidate();
            showDetails();
        }

        private void show(GalleryItem it)
        {
            Bitmap b = Gallery.Open(it.Path);
            if (b == null) { status("Can't read " + Path.GetFileName(it.Path)); return; }
            if (_saved != null) _saved.Dispose();
            clearPreview();
            _saved = b;
            _savedItem = it;
            // a reply goes to this picture's sender, in its mode
            receivedFrom(it.Call, SstvModes.All.FirstOrDefault(m => m.Name == it.Mode), it.Utc);
            _view.Image = b;
            _view.Caption = it.Caption + "   (click to go back to live)";
            _view.Progress = -1;
            _view.Invalidate();
            showDetails();
        }

        private static string captionOf(SstvPicture p)
        {
            return (p.Callsign.Length > 0 ? p.Callsign + "  " : "") + p.Mode.Name + "  " + p.StartUtc.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z";
        }

        private void finished(SstvPicture p)
        {
            bool complete = p.LinesDone >= p.Mode.Lines - 2;
            receivedFrom(p.Callsign, p.Mode, p.StartUtc);
            bool keep = complete || (_host.GetSetting("savecut", "1") == "1" && p.LinesDone >= p.Mode.Lines / 4);
            if (p == _livePic && _live != null)
            {
                Gallery.CopyInto(p, _live);
                if (showingLive) { _view.Caption = captionOf(p); _view.Progress = -1; _view.Invalidate(); }
            }
            if (keep)
            {
                try
                {
                    GalleryItem it = savePicture(p);
                    _host.Log("received " + p.Mode.Name + (p.Callsign.Length > 0 ? " from " + p.Callsign : "") + ", " + p.LinesDone + " lines: " + it.Path);
                }
                catch (Exception ex) { _host.Log("save: " + ex); status("Couldn't save the picture: " + ex.Message); return; }
            }
            status((complete ? "Received " : "Cut short: ") + p.Mode.Name + (p.Callsign.Length > 0 ? " from " + p.Callsign : "") +
                   (keep ? "" : " (not kept: less than a quarter came in)"));
            showDetails();
        }

        // a false start (no line sync): drop the half-drawn picture
        private void abandoned(SstvPicture p)
        {
            if (p != _livePic) return;
            _livePic = null;
            if (_live != null) { _live.Dispose(); _live = null; }
            if (showingLive) showLive();
        }

        // the live picture is what's shown (not a saved one, nor one ready to send)
        private bool showingLive { get { return _savedItem == null && _pvTemplate == null; } }

        private void showDetails()
        {
            List<KeyValuePair<string, string>> r = _details.Rows;
            r.Clear();
            Action<string, string> row = (k, v) => r.Add(new KeyValuePair<string, string>(k, v));
            if (_savedItem != null)
            {
                row("Saved", "");
                row("Mode", _savedItem.Mode);
                row("From", _savedItem.Call.Length > 0 ? _savedItem.Call : "(no FSK ID)");
                if (workedText(_savedItem.Call).Length > 0) row("Worked", workedText(_savedItem.Call));
                row("Time", _savedItem.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z");
                string json = "";
                try { json = File.ReadAllText(Path.ChangeExtension(_savedItem.Path, ".json")); } catch { }
                Match d = Regex.Match(json, "\"dialHz\":\\s*(\\d+)");
                if (d.Success) row("Freq", (double.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture) / 1e6).ToString("0.000", CultureInfo.InvariantCulture) + " MHz");
                Match tu = Regex.Match(json, "\"tuneHz\":\\s*(-?\\d+)");
                if (tu.Success) row("Tuning", int.Parse(tu.Groups[1].Value, CultureInfo.InvariantCulture).ToString("+0;-0;0") + " Hz");
                _details.Shift = 0;
            }
            else if (_livePic != null)
            {
                SstvPicture p = _livePic;
                row(p.Complete ? "Last" : "Receiving", "");
                row("Mode", p.Mode.Name);
                row("From", p.Callsign.Length > 0 ? p.Callsign : p.Complete ? "(no FSK ID)" : "(at the end)");
                if (workedText(p.Callsign).Length > 0) row("Worked", workedText(p.Callsign));
                row("Started", p.StartUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z");
                row("Lines", p.LinesDone + " of " + p.Mode.Lines);
                row("Tuning", p.FreqShift.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " Hz" + (_e.Decoder.Afc ? " (AFC)" : ""));
                if (p.Rejoined > 0) row("Fade", "rejoined" + (p.Rejoined > 1 ? " " + p.Rejoined + "x" : ""));
                if (p.Complete) row("Slant", Math.Abs(p.SlantPpm) < 20 ? "none" : "fixed (" + p.SlantPpm.ToString("+0;-0", CultureInfo.InvariantCulture) + " ppm)");
                _details.Shift = p.FreqShift;
            }
            else
            {
                row("Listening", "");
                row("Modes", "Martin, Scottie");
                row("", "Robot 36, PD");
            }
            _details.Invalidate();
        }

        // ---- status and markers ----

        private void status(string s)
        {
            if (s != null) { _statusText = s; _statusAt = DateTime.UtcNow; }
            else
            {
                double f = _host.Radio.VfoHz(_e.Rx);
                string listen = !_host.Radio.PowerOn ? "Radio off" :
                    _e.Lost ? "Not listening (click here to try again)" :
                    _e.Listening ? "Listening on RX" + (_e.Rx + 1) + "  " + (f / 1e6).ToString("0.000", CultureInfo.InvariantCulture) + " " + _host.Radio.Mode(_e.Rx) : "Starting...";
                if (_e.Decoder.Receiving && _livePic != null) listen = "Receiving " + _livePic.Mode.Name + ", line " + _livePic.LinesDone + " of " + _livePic.Mode.Lines;
                _statusText = listen;
            }
            _status.Text = _statusText;
            _status.Cursor = _e.Lost ? Cursors.Hand : Cursors.Default;
            _skip.Enabled = _e.Decoder.Receiving;
            _adjust.Enabled = canAdjust;
        }

        // set by the module while its window is open. The pane's timer runs for its whole life, so after closing (which
        // clears the markers) the next tick drew sync / black / white back on the panadapter, in every mode.
        public bool ShowMarkers { get; set; }

        private void markers()
        {
            if (!ShowMarkers) return;
            bool on = _host.GetSetting("markers", "1") == "1";
            string mode = _host.Radio.Mode(_e.Rx) ?? "";
            string key = on + mode + _e.Rx;
            if (key == _markersFor) return;
            _markersFor = key;
            if (!on) { _host.SetMarkers(_e.Rx, null); return; }
            double sign = mode.EndsWith("L") || mode == "LSB" ? -1 : 1;
            _host.SetMarkers(_e.Rx, new[]
            {
                new KainosMarker(sign * 1200, "sync", _t.Violet),
                new KainosMarker(sign * 1500, "black", _t.Dim),
                new KainosMarker(sign * 2300, "white", _t.Ice),
            });
        }

        public void ClearMarkers()
        {
            _host.SetMarkers(0, null);
            _host.SetMarkers(1, null);
            _markersFor = "";
        }
    }
}
