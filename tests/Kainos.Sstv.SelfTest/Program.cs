/*  Program.cs (self-test)

Checks the SSTV decoder without a radio: every mode encoded and decoded (compared pixel by pixel, PSNR), with
noise, mistuning and a sender's clock off by 500 ppm (slant); the FSK ID callsign; and pictures made by PySSTV,
an independent encoder (when Python, Pillow and its source are at hand).

  Kainos.Sstv.SelfTest.exe [--pysstv <folder holding the pysstv package>] [--keep]

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Kainos.Sstv.SelfTest
{
    internal static class Program
    {
        private static int _fail;
        internal static bool Verbose;
        private static readonly Random Rnd = new Random(42);

        private static void check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) _fail++;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string work = Path.Combine(Path.GetTempPath(), "kainos-sstv-selftest");
            Directory.CreateDirectory(work);
            bool keep = args.Contains("--keep");
            Verbose = args.Contains("--verbose");
            if (args.Contains("--sweep")) { using (Bitmap tp = TestPicture()) Sweep(tp, work); return 0; }
            int wi = Array.IndexOf(args, "--wav");
            if (wi >= 0 && wi + 1 < args.Length) return DecodeWav(args[wi + 1], work);
            using (Bitmap test = TestPicture())
            {
                test.Save(Path.Combine(work, "test.png"), ImageFormat.Png);

                // 1. every mode, clean
                foreach (SstvMode m in SstvModes.All)
                {
                    float[] a = SstvEncoder.Encode(test, m, 48000);
                    SstvPicture p = decode(a, 48000, 0);
                    double psnr = p != null ? Psnr(SstvEncoder.Pixels(test, m), p) : 0;
                    check(p != null && p.Mode == m && p.Complete && psnr > 26,
                        m.Name + " clean: " + (p == null ? "no picture" : "PSNR " + psnr.ToString("0.0") + " dB, slant " + p.SlantPpm.ToString("0") + " ppm"));
                    if (keep && p != null) Save(p, Path.Combine(work, "clean-" + m.Short + ".png"));
                }

                // 2. mistuning, a clock off (slant), and the FSK ID, in the modes used most; then heavy noise
                foreach (string s in new[] { "M1", "S1", "R36", "PD120" })
                {
                    SstvMode m = SstvModes.ByShort(s);
                    List<KeyValuePair<double, double>> tones = SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), "K7JUS");
                    // mistuned by +70 Hz (as on SSB a little off frequency)
                    List<KeyValuePair<double, double>> off = tones.Select(t => new KeyValuePair<double, double>(t.Key + 70, t.Value)).ToList();
                    // the sender's clock 500 ppm fast: his picture is that much shorter
                    float[] a = Stretch(SstvEncoder.Audio(off, 48000), 1 / 1.0005);
                    foreach (double noise in new[] { 0.05, 0.25 })
                    {
                        SstvPicture p = decode(a, 48000, noise);
                        double psnr = p != null ? Psnr(SstvEncoder.Pixels(test, m), p) : 0;
                        // Robot 36 carries colour at half resolution, so it tops out lower
                        double need = noise < 0.1 ? (m.Family == SstvFamily.Robot36 ? 24 : 26) : 12;
                        check(p != null && p.Mode == m && psnr > need && Math.Abs(p.FreqShift - 70) < 15 && Math.Abs(p.SlantPpm + 500) < 150 && p.Callsign == "K7JUS",
                            m.Name + (noise < 0.1 ? " light" : " heavy") + " noise, +70 Hz off tune, clock +500 ppm: " + (p == null ? "no picture" :
                            "PSNR " + psnr.ToString("0.0") + " dB, tune " + p.FreqShift.ToString("+0;-0") + " Hz, slant " + p.SlantPpm.ToString("0") + " ppm, FSK ID \"" + p.Callsign + "\""));
                        if (keep && p != null) Save(p, Path.Combine(work, (noise < 0.1 ? "skew-" : "noisy-") + m.Short + ".png"));
                    }
                }

                // 2b. headers found in noise as on HF: SNR in a 3 kHz bandwidth (signal 0.5 peak; noise sigma s at 48 kHz)
                {
                    SstvMode m = SstvModes.ByShort("M1");
                    List<KeyValuePair<double, double>> tones = SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), null);
                    float[] head = SstvEncoder.Audio(tones, 48000).Take(48000 * 2).ToArray();        // the header and a little picture
                    foreach (double sigma in new[] { 0.5, 1.0, 1.41, 2.0 })
                    {
                        int found = 0, tries = 8;
                        for (int k = 0; k < tries; k++) if (starts(head, 48000, sigma) > 0) found++;
                        double snr = 10 * Math.Log10(0.125 / (sigma * sigma * 3000 / 24000));
                        bool need = snr >= -0.5;         // must be dependable down to 0 dB; below is a bonus
                        check(!need || found >= tries - 1, "header found at " + snr.ToString("0") + " dB SNR (3 kHz): " + found + " of " + tries);
                    }
                }

                // 2c. a picture whose header was missed: Start now, part way in
                {
                    SstvMode m = SstvModes.ByShort("M1");
                    float[] a = SstvEncoder.Encode(test, m, 48000);
                    int skip = (int)((0.9 + 30.3 * m.LineTime) * 48000);       // the header and 30 lines gone, mid-line
                    float[] rest = a.Skip(skip).ToArray();
                    SstvDecoder d = new SstvDecoder();
                    SstvPicture done = null;
                    d.Finished += pp => done = pp;
                    int chunk = 960, fed = 0;
                    float[] buf = new float[chunk];
                    for (int i = 0; i < rest.Length + 48000 * 20; i += chunk)       // and 20 s after: it runs a whole picture's length from where it started
                    {
                        for (int j = 0; j < chunk; j++) buf[j] = (float)((i + j < rest.Length ? rest[i + j] * 0.5 : 0) + Gauss() * 0.05);
                        d.Push(buf, chunk, 48000);
                        fed += chunk;
                        if (fed == chunk * 100) d.StartNow(m);      // 2 s in
                    }
                    // the decoded rows are the sent ones from some line on: find which, and compare
                    byte[,,] want = SstvEncoder.Pixels(test, m);
                    double bestPsnr = 0; int bestOff = -1;
                    if (done != null)
                        for (int off = 0; off < 60; off++)
                        {
                            double se = 0; int n = 0;
                            for (int y = 4; y + off < m.Lines && y < done.LinesDone - 4; y++)
                                for (int x = 6; x < m.Width - 6; x++)
                                    for (int c = 0; c < 3; c++) { double dd = want[y + off, x, c] - done.Rgb[(y * m.Width + x) * 3 + c]; se += dd * dd; n++; }
                            double ps = n == 0 ? 0 : 10 * Math.Log10(255.0 * 255 / Math.Max(1e-9, se / n));
                            if (ps > bestPsnr) { bestPsnr = ps; bestOff = off; }
                        }
                    check(done != null && bestPsnr > 26, "Start now without a header: " + (done == null ? "no picture" : "PSNR " + bestPsnr.ToString("0.0") + " dB from sent line " + bestOff + ", " + done.LinesDone + " lines"));
                }

                // 2d. weak pictures run to the end (as on HF): 3 and 0 dB SNR in 3 kHz
                foreach (string ms in new[] { "M1", "R36" })
                    foreach (double snr in new[] { 3.0, 0.0 })
                    {
                        SstvMode m = SstvModes.ByShort(ms);
                        double sigma = Math.Sqrt(0.125 / Math.Pow(10, snr / 10) * 24000 / 3000);
                        SstvPicture p = decode(SstvEncoder.Audio(SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), "K7JUS"), 48000), 48000, sigma);
                        double psnr = p != null ? Psnr(SstvEncoder.Pixels(test, m), p) : 0;
                        check(p != null && p.LinesDone >= m.Lines - 2 && psnr > 5.5, m.Name + " at " + snr.ToString("0") + " dB runs to the end: " +
                            (p == null ? "no picture" : p.LinesDone + "/" + m.Lines + " lines, PSNR " + psnr.ToString("0.0") + ", FSK ID \"" + p.Callsign + "\""));
                    }

                // 2e. a header and then no picture (a false start, or the sender stopped): dropped, not kept
                {
                    SstvMode m = SstvModes.ByShort("M1");
                    float[] head = SstvEncoder.Audio(SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), null), 48000).Take((int)(0.91 * 48000)).ToArray();
                    float[] a = new float[head.Length + 48000 * 20];
                    Array.Copy(head, a, head.Length);
                    int started = 0, abandoned = 0, finished = 0;
                    for (int trial = 0; trial < 10; trial++)
                    {
                        SstvDecoder d = new SstvDecoder();
                        d.Started += _ => started++; d.Abandoned += _ => abandoned++;
                        d.Finished += p => { if (p.LinesDone >= p.Mode.Lines / 4) finished++; };      // what the window would keep
                        float[] buf = new float[960];
                        for (int i = 0; i < a.Length; i += 960) { for (int j = 0; j < 960; j++) buf[j] = (float)((i + j < a.Length ? a[i + j] * 0.5 : 0) + Gauss() * 0.3); d.Push(buf, 960, 48000); }
                    }
                    check(started == 10 && abandoned >= 9 && finished == 0, "a header with only noise after it is dropped, 10 tries (started " + started + ", dropped " + abandoned + ", kept " + finished + ")");
                }

                // 2f. a new header part way through a picture starts the new one (the first is kept, cut short)
                {
                    SstvMode m1 = SstvModes.ByShort("M1"), r36 = SstvModes.ByShort("R36");
                    float[] first = SstvEncoder.Encode(test, m1, 48000);
                    float[] second = SstvEncoder.Encode(test, r36, 48000, "K7JUS");
                    int cutAt = (int)(first.Length * 0.6);
                    float[] a = first.Take(cutAt).Concat(second).ToArray();
                    List<SstvPicture> got = new List<SstvPicture>();
                    SstvDecoder d = new SstvDecoder();
                    d.Finished += p => got.Add(p);
                    float[] buf = new float[960];
                    for (int i = 0; i < a.Length + 48000 * 3; i += 960) { for (int j = 0; j < 960; j++) buf[j] = (float)((i + j < a.Length ? a[i + j] * 0.5 : 0) + Gauss() * 0.05); d.Push(buf, 960, 48000); }
                    check(got.Count == 2 && got[0].Mode == m1 && got[0].LinesDone > 100 && got[1].Mode == r36 && got[1].LinesDone >= 238 && got[1].Callsign == "K7JUS",
                        "a header mid-picture starts the new picture: " + string.Join(", ", got.Select(p => p.Mode.Short + " " + p.LinesDone + " lines")));
                }

                // 2g. strong signals beside the SSTV one in the passband (a busy band)
                foreach (string ms in new[] { "M1", "S2" })
                {
                    SstvMode m = SstvModes.ByShort(ms);
                    SstvPicture p = DecodeWithQrm(m, test, 9);
                    double psnr = p != null ? Psnr(SstvEncoder.Pixels(test, m), p) : 0;
                    check(p != null && p.LinesDone >= m.Lines - 2 && psnr > 14, m.Name + " at 9 dB beside two strong carriers (700 and 2900 Hz): " +
                        (p == null ? "no picture" : p.LinesDone + "/" + m.Lines + " lines, PSNR " + psnr.ToString("0.0")));
                    if (keep && p != null) Save(p, Path.Combine(work, "qrm-" + m.Short + ".png"));
                }

                // 2h. a fade part way through (HF QSB): the picture carries on to its end, and is clear again after
                {
                    SstvMode m = SstvModes.ByShort("M1");
                    float[] a = SstvEncoder.Audio(SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), "K7JUS"), 48000);
                    int f0 = (int)(a.Length * 0.3), f1 = (int)(a.Length * 0.5);
                    for (int i = f0; i < f1; i++) a[i] *= 0.02f;            // 34 dB down for 20% of the picture
                    SstvPicture p = decode(a, 48000, 0.1);
                    double psnr = 0;
                    if (p != null)
                    {
                        // the last third, after the fade
                        byte[,,] want = SstvEncoder.Pixels(test, m);
                        double se = 0; int n = 0;
                        for (int y = m.Lines * 2 / 3; y < m.Lines - 4; y++) for (int x = 6; x < m.Width - 6; x++) for (int c = 0; c < 3; c++) { double d = want[y, x, c] - p.Rgb[(y * m.Width + x) * 3 + c]; se += d * d; n++; }
                        psnr = 10 * Math.Log10(255.0 * 255 / Math.Max(1e-9, se / n));
                    }
                    check(p != null && p.LinesDone >= m.Lines - 2 && psnr > 24, "a deep fade part way through: runs to the end, clear after (" +
                        (p == null ? "no picture" : p.LinesDone + " lines, PSNR after the fade " + psnr.ToString("0.0")) + ")");
                }

                // 3. nothing from noise alone
                {
                    float[] noise = new float[48000 * 60];
                    for (int i = 0; i < noise.Length; i++) noise[i] = (float)(Gauss() * 0.3);
                    int started = 0;
                    SstvDecoder d = new SstvDecoder();
                    d.Started += _ => started++;
                    for (int i = 0; i < noise.Length; i += 960) d.Push(noise.Skip(i).Take(960).ToArray(), Math.Min(960, noise.Length - i), 48000);
                    check(started == 0, "no picture from noise alone (" + started + ")");
                }

                // 4. the module itself: its window, fed two pictures by a stand-in Kainos
                if (!args.Contains("--quick")) UiTest(work, test);

                // 5. PySSTV's pictures: an independent encoder's timing and colours
                int pi = Array.IndexOf(args, "--pysstv");
                if (pi >= 0 && pi + 1 < args.Length) PySstv(args[pi + 1], work, test);
                else Console.WriteLine("SKIP  PySSTV cross-check (pass --pysstv <folder holding the pysstv package>)");
            }
            Console.WriteLine(_fail == 0 ? "All passed" : _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        // pictures at falling SNR (3 kHz bandwidth): lines drawn, whether it ran to the end, PSNR
        private static void Sweep(Bitmap test, string work)
        {
            foreach (string ms in new[] { "M1", "S1", "R36", "PD120" })
            {
                SstvMode m = SstvModes.ByShort(ms);
                float[] a = SstvEncoder.Audio(SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), "K7JUS"), 48000);
                foreach (double snr in new[] { 12.0, 9, 6, 3, 0 })
                {
                    double sigma = Math.Sqrt(0.125 / Math.Pow(10, snr / 10) * 24000 / 3000);
                    SstvPicture p = decode(a, 48000, sigma);
                    Console.WriteLine(m.Short.PadRight(6) + snr.ToString("0").PadLeft(3) + " dB: " + (p == null ? "no picture" :
                        p.LinesDone + "/" + m.Lines + " lines, PSNR " + Psnr(SstvEncoder.Pixels(test, m), p).ToString("0.0") + ", slant " + p.SlantPpm.ToString("0") + ", ID \"" + p.Callsign + "\""));
                    if (p != null) Save(p, Path.Combine(work, "sweep-" + m.Short + "-" + snr.ToString("0") + "dB.png"));
                }
            }
        }

        // a picture with a strong signal beside it in the passband (as on a busy band): an SSB-like carrier at 700 Hz and
        // another at 2900 Hz, each twice the SSTV signal's level, with band noise for the given SNR
        internal static SstvPicture DecodeWithQrm(SstvMode m, Bitmap test, double snr)
        {
            float[] a = SstvEncoder.Audio(SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), "K7JUS"), 48000);
            float[] q = new float[a.Length];
            for (int i = 0; i < q.Length; i++)
            {
                double t = i / 48000.0;
                // the carriers wobble a little, as voice would
                q[i] = (float)(a[i] + 2 * Math.Sin(2 * Math.PI * (700 * t + 3 * Math.Sin(2 * Math.PI * 0.7 * t))) + 2 * Math.Sin(2 * Math.PI * (2900 * t + 5 * Math.Sin(2 * Math.PI * 0.4 * t))));
            }
            double sigma = Math.Sqrt(0.125 / Math.Pow(10, snr / 10) * 24000 / 3000);
            return decode(q, 48000, sigma);
        }

        // how many pictures start from this audio (with noise), and 1 s of noise first
        private static int starts(float[] audio, int rate, double noise)
        {
            SstvDecoder d = new SstvDecoder();
            int n = 0;
            d.Started += _ => n++;
            int chunk = rate / 50;
            float[] buf = new float[chunk];
            int total = audio.Length + rate;
            for (int i = 0; i < total; i += chunk)
            {
                for (int j = 0; j < chunk; j++)
                {
                    int k = i + j - rate;
                    buf[j] = (float)((k >= 0 && k < audio.Length ? audio[k] * 0.5 : 0) + Gauss() * noise);
                }
                d.Push(buf, chunk, rate);
            }
            return n;
        }

        // a recording (Options > Save the last 3 minutes of audio) through the decoder: what it found, and why not
        private static int DecodeWav(string path, string work)
        {
            int rate;
            float[] a = ReadWav(path, out rate);
            Console.WriteLine(Path.GetFileName(path) + ": " + (a.Length / (double)rate).ToString("0.0") + " s at " + rate + " Hz, peak " + a.Select(Math.Abs).DefaultIfEmpty(0).Max().ToString("0.000"));
            SstvDecoder d = new SstvDecoder();
            double t = 0;
            d.Diag += s => Console.WriteLine("  " + t.ToString("0.0").PadLeft(6) + " s  " + s);
            d.Started += p => Console.WriteLine("  " + t.ToString("0.0").PadLeft(6) + " s  START " + p.Mode.Name + ", tuning " + p.FreqShift.ToString("+0;-0;0") + " Hz");
            int pics = 0;
            d.Finished += p =>
            {
                string png = Path.Combine(work, "wav-" + (++pics) + "-" + p.Mode.Short + ".png");
                Save(p, png);
                Console.WriteLine("  " + t.ToString("0.0").PadLeft(6) + " s  END " + p.Mode.Name + " " + p.LinesDone + " lines, slant " + p.SlantPpm.ToString("0") + " ppm, FSK ID \"" + p.Callsign + "\" -> " + png);
            };
            int chunk = rate / 50;
            float[] buf = new float[chunk];
            for (int i = 0; i < a.Length; i += chunk)
            {
                int n = Math.Min(chunk, a.Length - i);
                Array.Copy(a, i, buf, 0, n);
                t = i / (double)rate;
                d.Push(buf, n, rate);
            }
            // 8 s of silence after: time for the last picture to end (and its FSK ID to be looked for)
            Array.Clear(buf, 0, chunk);
            for (int i = 0; i < 8 * 50; i++) { t += 0.02; d.Push(buf, chunk, rate); }
            Console.WriteLine(pics + " picture(s)" + (d.Unknown >= 0 ? "; last unknown VIS 0x" + d.Unknown.ToString("X2") : ""));
            return 0;
        }

        // the audio through the decoder in 20 ms pieces, with 2 s of (noisy) silence either side
        private static SstvPicture decode(float[] audio, int rate, double noise)
        {
            float[] all = new float[audio.Length + 4 * rate];
            for (int i = 0; i < all.Length; i++)
            {
                int k = i - 2 * rate;
                all[i] = (float)((k >= 0 && k < audio.Length ? audio[k] * 0.5 : 0) + Gauss() * noise);
            }
            SstvDecoder d = new SstvDecoder();
            SstvPicture done = null;
            d.Finished += p => done = p;
            if (Verbose) { d.Diag += x => Console.WriteLine("    diag: " + x); d.Started += p => Console.WriteLine("    start " + p.Mode.Name); }
            int chunk = rate / 50;
            float[] buf = new float[chunk];
            for (int i = 0; i < all.Length; i += chunk)
            {
                int n = Math.Min(chunk, all.Length - i);
                Array.Copy(all, i, buf, 0, n);
                d.Push(buf, n, rate);
            }
            return done;
        }

        private static void UiTest(string work, Bitmap test)
        {
            string data = Path.Combine(work, "ui-data");
            if (Directory.Exists(data)) Directory.Delete(data, true);       // this test's own folder, from the last run
            FakeHost h = new FakeHost(work, data) { Speed = 10 };
            h.Settings["crisp"] = "0";      // the picture in the window itself, so a screenshot of the window shows it
            string received = Path.Combine(data, "Received");
            Func<int> count = () => Directory.Exists(received) ? Directory.GetFiles(received, "*.png").Length : 0;
            bool timedOut = false;
            using (Form f = new Form { ClientSize = new Size(1100, 620), Text = "Kainos SSTV self-test", StartPosition = FormStartPosition.CenterScreen })
            {
                h.Window = f;
                SstvModule mod = new SstvModule();
                Stopwatch sw = Stopwatch.StartNew();
                TimeSpan firstAt = TimeSpan.Zero;
                bool midShot = false, takenAway = false;
                Timer t = new Timer { Interval = 200 };
                f.Shown += (s, e) =>
                {
                    mod.Start(h);
                    h.Menus[0].Click();
                    float[] gap = new float[48000 * 2];
                    SstvMode r36 = SstvModes.ByShort("R36"), m1 = SstvModes.ByShort("M1");
                    h.Play(gap);
                    h.Play(SstvEncoder.Audio(SstvEncoder.Tones(r36, SstvEncoder.Pixels(test, r36), "K7JUS"), 48000));
                    h.Play(gap);
                    using (Bitmap other = TestPicture()) { other.RotateFlip(RotateFlipType.RotateNoneFlipX); h.Play(SstvEncoder.Audio(SstvEncoder.Tones(m1, SstvEncoder.Pixels(other, m1), "W1AW"), 48000)); }
                    h.Play(gap);
                    t.Start();
                };
                t.Tick += (s, e) =>
                {
                    if (count() >= 1 && firstAt == TimeSpan.Zero) firstAt = sw.Elapsed;
                    if (!midShot && firstAt != TimeSpan.Zero && sw.Elapsed - firstAt > TimeSpan.FromSeconds(5)) { midShot = true; shot(f, Path.Combine(work, "ui-receiving.png")); }
                    if (takenAway) { t.Stop(); f.Close(); return; }
                    if ((count() >= 2 && h.AirEmpty) || sw.Elapsed > TimeSpan.FromSeconds(60))
                    {
                        timedOut = count() < 2;
                        shot(f, Path.Combine(work, "ui-done.png"));
                        // another digital mode (FT8, RTTY...) opens and takes the receiver's audio: SSTV closes
                        h.TakeAway();
                        takenAway = true;
                    }
                };
                Application.Run(f);
                mod.Stop();
                mod.Dispose();
                t.Dispose();
            }
            string[] files = Directory.Exists(received) ? Directory.GetFiles(received, "*.png").Select(Path.GetFileName).OrderBy(x => x).ToArray() : new string[0];
            check(!timedOut && files.Length == 2 && files[0].Contains("_R36_K7JUS") && files[1].Contains("_M1_W1AW"),
                "the window receives and keeps both pictures: " + string.Join(", ", files));
            check(File.Exists(Path.Combine(received, Path.ChangeExtension(files.FirstOrDefault() ?? "x", ".json"))), "each picture has its details beside it (.json)");
            check(!h.Panes[0].IsOpen && !h.Menus[0].Checked && h.Acquires == 1, "closes when another digital mode takes the receiver's audio, and doesn't take it back");
            check(h.MarkerLog.Contains("0:1200,1500,2300"), "the SSTV tones marked on the panadapter (" + string.Join(" ", h.MarkerLog) + ")");
            check(h.LogLines.Any(l => l.StartsWith("received Martin M1 from W1AW")), "logged: " + h.LogLines.LastOrDefault());
            Console.WriteLine("      screenshots: " + Path.Combine(work, "ui-receiving.png") + ", ui-done.png");
        }

        private static void shot(Form f, string path)
        {
            using (Bitmap b = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(b, new Rectangle(Point.Empty, f.Size));
                b.Save(path, ImageFormat.Png);
            }
        }

        private static void PySstv(string pkgDir, string work, Bitmap test)
        {
            string script = Path.Combine(work, "pysstv_make.py");
            File.WriteAllText(script,
                "import sys\n" +
                "sys.path.insert(0, sys.argv[1])\n" +
                "from PIL import Image\n" +
                "from pysstv import color\n" +
                "for name in sys.argv[3:]:\n" +
                "    cls = getattr(color, name)\n" +
                "    s = cls(Image.open(sys.argv[2] + '/py-' + name + '.png'), 48000, 16)\n" +
                "    s.add_fskid_text('K7JUS')\n" +
                "    s.write_wav(sys.argv[2] + '/py-' + name + '.wav')\n");
            string[] modes = { "MartinM1", "ScottieS1", "Robot36", "PD90", "PD120" };
            string[] ours = { "M1", "S1", "R36", "PD90", "PD120" };
            // PySSTV's Scottie lines are 1% short (it takes the gap out of the 138.24 ms scan), against slowrx and
            // MMSSTV: a test of how far off timing still gives a picture, not of the colours
            double[] need = { 30, 15, 24, 30, 30 };
            // PySSTV stretches a picture to fit; give it ours already cropped to each mode's size so both send the same
            for (int i = 0; i < modes.Length; i++) SavePixels(SstvEncoder.Pixels(test, SstvModes.ByShort(ours[i])), Path.Combine(work, "py-" + modes[i] + ".png"));
            ProcessStartInfo psi = new ProcessStartInfo("python", "-I \"" + script + "\" \"" + pkgDir + "\" \"" + work + "\" " + string.Join(" ", modes))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            string err;
            using (Process p = Process.Start(psi)) { err = p.StandardError.ReadToEnd(); p.WaitForExit(300000); }
            for (int i = 0; i < modes.Length; i++)
            {
                string wav = Path.Combine(work, "py-" + modes[i] + ".wav");
                if (!File.Exists(wav)) { check(false, "PySSTV " + modes[i] + ": no WAV (" + err.Trim() + ")"); continue; }
                int rate;
                float[] a = ReadWav(wav, out rate);
                SstvMode m = SstvModes.ByShort(ours[i]);
                SstvPicture pic = decode(a, rate, 0.02);
                double psnr = pic != null ? Psnr(SstvEncoder.Pixels(test, m), pic) : 0;
                check(pic != null && pic.Mode == m && psnr > need[i] && pic.Callsign == "K7JUS",
                    "PySSTV's " + m.Name + " decodes: " + (pic == null ? "no picture" : "PSNR " + psnr.ToString("0.0") + " dB, slant " + pic.SlantPpm.ToString("0") + " ppm, FSK ID \"" + pic.Callsign + "\""));
            }
        }

        // ---- helpers ----

        // colour bars, gradients, a circle and text: the kind of detail a decoder gets wrong
        internal static Bitmap TestPicture()
        {
            Bitmap b = new Bitmap(640, 496, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color[] bars = { Color.White, Color.Yellow, Color.Cyan, Color.Lime, Color.Magenta, Color.Red, Color.Blue, Color.Black };
                for (int i = 0; i < bars.Length; i++) using (Brush br = new SolidBrush(bars[i])) g.FillRectangle(br, i * 80, 0, 80, 200);
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 200, 640, 100), Color.Black, Color.White, 0f)) g.FillRectangle(lg, 0, 200, 640, 100);
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 300, 640, 196), Color.FromArgb(10, 30, 90), Color.FromArgb(220, 160, 60), 90f)) g.FillRectangle(lg, 0, 300, 640, 196);
                g.FillEllipse(Brushes.White, 420, 320, 160, 160);
                using (Font f = new Font("Segoe UI", 64, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("K7JUS", f, Brushes.White, 30, 350);
            }
            return b;
        }

        internal static double Psnr(byte[,,] want, SstvPicture got)
        {
            int h = got.Mode.Lines, w = got.Mode.Width;
            double se = 0;
            int n = 0;
            // the first and last few lines and columns are where filters ramp: leave them out
            for (int y = 4; y < h - 4; y++)
                for (int x = 6; x < w - 6; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        double d = want[y, x, c] - got.Rgb[(y * w + x) * 3 + c];
                        se += d * d; n++;
                    }
            double mse = se / n;
            return mse < 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
        }

        internal static void SavePixels(byte[,,] px, string path)
        {
            int h = px.GetLength(0), w = px.GetLength(1);
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) b.SetPixel(x, y, Color.FromArgb(px[y, x, 0], px[y, x, 1], px[y, x, 2]));
                b.Save(path, ImageFormat.Png);
            }
        }

        internal static void Save(SstvPicture p, string path)
        {
            using (Bitmap b = new Bitmap(p.Mode.Width, p.Mode.Lines, PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < p.Mode.Lines; y++)
                    for (int x = 0; x < p.Mode.Width; x++)
                    {
                        int i = (y * p.Mode.Width + x) * 3;
                        b.SetPixel(x, y, Color.FromArgb(p.Rgb[i], p.Rgb[i + 1], p.Rgb[i + 2]));
                    }
                b.Save(path, ImageFormat.Png);
            }
        }

        // resampled by a factor (linear): > 1 longer
        internal static float[] Stretch(float[] a, double factor)
        {
            int n = (int)(a.Length * factor);
            float[] o = new float[n];
            for (int i = 0; i < n; i++)
            {
                double src = i / factor;
                int k = (int)src;
                double fr = src - k;
                o[i] = (float)(k + 1 < a.Length ? a[k] * (1 - fr) + a[k + 1] * fr : 0);
            }
            return o;
        }

        internal static float[] ReadWav(string path, out int rate)
        {
            byte[] b = File.ReadAllBytes(path);
            rate = BitConverter.ToInt32(b, 24);
            int ch = BitConverter.ToInt16(b, 22), bits = BitConverter.ToInt16(b, 34);
            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (id == "data")
                {
                    int step = ch * bits / 8, n = Math.Min(len, b.Length - pos - 8) / step;
                    float[] s = new float[n];
                    for (int i = 0; i < n; i++) s[i] = bits == 16 ? BitConverter.ToInt16(b, pos + 8 + i * step) / 32768f : (b[pos + 8 + i * step] - 128) / 128f;
                    return s;
                }
                pos += 8 + len;
            }
            return new float[0];
        }

        internal static double Gauss()
        {
            double u1 = 1.0 - Rnd.NextDouble(), u2 = Rnd.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
