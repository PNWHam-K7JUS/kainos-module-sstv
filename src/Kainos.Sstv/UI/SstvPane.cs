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
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Sstv.UI
{
    internal class SstvPane : Panel
    {
        private readonly IKainosHost _host;
        private readonly IKainosTheme _t;
        private readonly SstvEngine _e;
        private readonly string _folder;

        private readonly KButton _band, _rxSel, _startBtn, _skip, _openFolder, _pop, _options;
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
            _skip = btn("Skip", "End the picture being received now (it's kept if a quarter or more came in)");
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

            wire();
            _e.Rx = host.GetSetting("rx", "0") == "1" ? 1 : 0;
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
            place(_startBtn, 52); place(_skip, 52); place(_openFolder, 70);
            int right = Width - pad;
            _options.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _pop.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _status.SetBounds(x + pad, y, Math.Max(0, right - x - pad), bh);

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
            _startBtn.Click += (s, e) => startMenu().Show(_startBtn, new Point(0, _startBtn.Height));
            _openFolder.Click += (s, e) => openFolder();
            _pop.Click += (s, e) => PopToggled?.Invoke();
            _options.Click += (s, e) => optionsMenu().Show(_options, new Point(0, _options.Height));
            _view.Click += (s, e) => { if (_savedItem != null) showLive(); };
            _gallery.Picked += show;
            _gallery.Menu += (it, p) => itemMenu(it).Show(_gallery, p);

            _e.Status += s => _host.BeginInvoke(() => status(s));
            _e.Decoder.Progress += p => _dirty = true;
            _e.Decoder.Finished += p => _host.BeginInvoke(() => finished(p));
            _e.Decoder.Abandoned += p => _host.BeginInvoke(() => abandoned(p));
            _e.SpectrumReady += () => _specDirty = true;
            // headers that nearly passed: in the log, and briefly in the status line
            _e.Decoder.Diag += d => { _host.Log("vis: " + d); _host.BeginInvoke(() => status("Heard what may be a " + d)); };
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
                ToolStripMenuItem it = new ToolStripMenuItem(b[0] + "   " + (f / 1e6).ToString("0.000", CultureInfo.InvariantCulture) + " " + mode) { Checked = Math.Abs(dial - f) < 500 };
                it.Click += (s, e) =>
                {
                    // DIGU / DIGL: the audio straight through, as for the other digital modes
                    _host.Radio.SetMode(_e.Rx, mode == "LSB" ? "DIGL" : "DIGU");
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
            check("Keep pictures that were cut short", "savecut", "1", "Save a picture that ended early (signal lost, or Skip) if a quarter or more of it came in");
            check("Show the SSTV tones on the panadapter", "markers", "1", "Lines at sync (1200 Hz), black (1500 Hz) and white (2300 Hz) from the VFO, to tune by");
            check("Sharp picture on high-DPI screens", "crisp", "1", "Draw the picture at the screen's own resolution at 125 % / 150 % scaling (takes effect when Kainos restarts)");
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem rec = new ToolStripMenuItem("Save the last 3 minutes of audio") { ToolTipText = "A WAV of what the module heard, for checking a signal that didn't decode" };
            rec.Click += (s, e) => saveAudio();
            m.Items.Add(rec);
            ToolStripMenuItem open = new ToolStripMenuItem("Open the pictures folder");
            open.Click += (s, e) => openFolder();
            m.Items.Add(open);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
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
                if (_savedItem == null) { _view.Progress = _livePic.Complete ? -1 : _livePic.LinesDone / (double)_livePic.Mode.Lines; _view.Invalidate(); }
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
            if (DateTime.UtcNow - _statusAt > TimeSpan.FromSeconds(8)) status(null);
        }

        private void startLive(SstvPicture p)
        {
            _livePic = p;
            if (_live != null) _live.Dispose();
            _live = new Bitmap(p.Mode.Width, p.Mode.Lines, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(_live)) g.Clear(Color.Black);
            showLive();
            status("Receiving " + p.Mode.Name);
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
            _saved = b;
            _savedItem = it;
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
            bool keep = complete || (_host.GetSetting("savecut", "1") == "1" && p.LinesDone >= p.Mode.Lines / 4);
            if (p == _livePic && _live != null)
            {
                Gallery.CopyInto(p, _live);
                if (_savedItem == null) { _view.Caption = captionOf(p); _view.Progress = -1; _view.Invalidate(); }
            }
            if (keep)
            {
                try
                {
                    GalleryItem it = Gallery.Save(_folder, p, _host.Radio.VfoHz(_e.Rx), complete);
                    _gallery.Insert(it);
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
            if (_savedItem == null) showLive();
        }

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
                row("Started", p.StartUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z");
                row("Lines", p.LinesDone + " of " + p.Mode.Lines);
                row("Tuning", p.FreqShift.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " Hz");
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
        }

        private void markers()
        {
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
