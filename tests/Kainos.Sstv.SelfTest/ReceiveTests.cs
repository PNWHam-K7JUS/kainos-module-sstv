/*  ReceiveTests.cs

The receive controls (0.5.0): pictures without a header held through fades and rejoined after long ones, AFC,
sensitivity, the modes looked for without a header, and Redraw (the Adjust window).

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Kainos.Sstv.SelfTest
{
    internal static partial class Program
    {
        // noise sigma at 48 kHz for an SNR in a 3 kHz bandwidth (the signal fed at 0.5 peak)
        private static double sigmaFor(double snrDb) { return Math.Sqrt(0.125 / Math.Pow(10, snrDb / 10) * 24000 / 3000); }

        // feeds audio (at 0.5) with noise, then `tail` seconds of noise alone
        private static void feed(SstvDecoder d, float[] a, double noise, int tail)
        {
            float[] buf = new float[960];
            for (int i = 0; i < a.Length + 48000 * tail; i += 960)
            {
                for (int j = 0; j < 960; j++) buf[j] = (float)((i + j < a.Length ? a[i + j] * 0.5 : 0) + Gauss() * noise);
                d.Push(buf, 960, 48000);
            }
        }

        // a picture without its header, joined `at` of the way in, mistuned by `shift` Hz, with a fade (34 dB down)
        // from `fadeAt` (share of the whole) for `fadeSec` seconds
        private static float[] joined(Bitmap test, SstvMode m, double at, double shift, double fadeAt, double fadeSec)
        {
            List<KeyValuePair<double, double>> tones = SstvEncoder.Tones(m, SstvEncoder.Pixels(test, m), null)
                .Select(t => new KeyValuePair<double, double>(t.Key + shift, t.Value)).ToList();
            float[] a = SstvEncoder.Audio(tones, 48000);
            if (fadeSec > 0)
            {
                int f0 = (int)(a.Length * fadeAt), f1 = Math.Min(a.Length, f0 + (int)(fadeSec * 48000));
                for (int i = f0; i < f1; i++) a[i] *= 0.02f;
            }
            return a.Skip((int)(a.Length * at)).ToArray();
        }

        internal static void ReceiveTests(Bitmap test)
        {
            SstvMode m1 = SstvModes.ByShort("M1");
            int joinedLines = (int)(m1.Lines * 0.9);        // what's left after joining 10 % in

            // a. a 10 s fade in a picture without its header: held through it, one picture to the end
            {
                SstvDecoder d = new SstvDecoder();
                int started = 0; SstvPicture last = null; int finishes = 0;
                d.Started += _ => started++;
                d.Finished += p => { last = p; finishes++; };
                if (Verbose) d.Diag += x => Console.WriteLine("    diag: " + x);
                feed(d, joined(test, m1, 0.1, 0, 0.5, 10), sigmaFor(6), 30);
                check(started == 1 && finishes == 1 && last != null && last.LinesDone >= joinedLines - 6,
                    "no header, 10 s fade: held through it (" + started + " started, " + finishes + " ended, " + (last == null ? "none" : last.LinesDone + " lines") + ")");
            }

            // b. a 35 s fade (longer than the 20 s hold): ended, then rejoined when the signal is back; one picture
            {
                SstvDecoder d = new SstvDecoder();
                int started = 0; SstvPicture last = null; HashSet<SstvPicture> pics = new HashSet<SstvPicture>(); int resumed = 0;
                d.Started += _ => started++;
                d.Finished += p => { last = p; pics.Add(p); };
                d.Resumed += _ => resumed++;
                if (Verbose) d.Diag += x => Console.WriteLine("    diag: " + x);
                feed(d, joined(test, m1, 0.1, 0, 0.3, 35), sigmaFor(6), 30);
                check(started == 1 && pics.Count == 1 && resumed == 1 && last != null && last.Rejoined == 1 && last.LinesDone >= joinedLines - 6,
                    "no header, 35 s fade: ended, then rejoined (" + started + " started, " + pics.Count + " pictures, " + resumed + " rejoined, " + (last == null ? "none" : last.LinesDone + " lines") + ")");
            }

            // c. AFC: a picture without its header 90 Hz off tune comes out with the right tuning and colours
            foreach (bool afc in new[] { true, false })
            {
                SstvDecoder d = new SstvDecoder { Afc = afc };
                SstvPicture got = null;
                d.Finished += p => { if (got == null) got = p; };
                feed(d, joined(test, m1, 0.3, 90, 0, 0), 0.05, 25);
                int off;
                double ps = got != null ? bestRowPsnr(SstvEncoder.Pixels(test, m1), got, out off) : 0;
                if (afc)
                    check(got != null && Math.Abs(got.FreqShift - 90) < 12 && ps > 26,
                        "AFC: no header, +90 Hz off tune: " + (got == null ? "no picture" : "tuning " + got.FreqShift.ToString("+0;-0") + " Hz, PSNR " + ps.ToString("0.0")));
                else
                    Console.WriteLine("      (AFC off, the same picture: " + (got == null ? "no picture" : "PSNR " + ps.ToString("0.0")) + ")");
            }

            // d. high sensitivity still finds nothing in noise alone
            {
                // A false start is dropped after its first 12 lines (no line sync), up to 28 s in the slow PD modes; nothing
                // may be kept (what the window would save: a quarter of a picture or more)
                SstvDecoder d = new SstvDecoder { Sensitivity = 2 };
                int started = 0, dropped = 0, kept = 0;
                d.Started += _ => started++;
                d.Abandoned += _ => dropped++;
                d.Finished += p => { if (p.LinesDone >= p.Mode.Lines / 4) kept++; };
                feed(d, new float[0], 0.3, 180);
                check(kept == 0 && started <= 2, "high sensitivity: nothing kept from 3 min of noise (" + started + " started, " + dropped + " dropped, " + kept + " kept)");
            }

            // e. high sensitivity finds a weak picture without its header
            {
                SstvDecoder d = new SstvDecoder { Sensitivity = 2 };
                SstvPicture got = null;
                d.Finished += p => { if (got == null) got = p; };
                feed(d, joined(test, m1, 0.3, 0, 0, 0), sigmaFor(0), 40);      // high holds 30 s after the signal goes
                check(got != null && got.Mode == m1, "high sensitivity: no header at 0 dB found (" + (got == null ? "not found" : got.Mode.Name + ", " + got.LinesDone + " lines") + ")");
            }

            // f. the modes looked for without a header: one left out isn't found; a header is still believed
            {
                SstvMode s1 = SstvModes.ByShort("S1");
                SstvDecoder d = new SstvDecoder { AutoModes = new HashSet<string> { "M1" } };
                int started = 0;
                d.Started += _ => started++;
                feed(d, joined(test, s1, 0.3, 0, 0, 0), 0.05, 5);
                check(started == 0, "modes: Scottie S1 left out isn't found without its header (" + started + " started)");
                SstvPicture got = null;
                d.Finished += p => got = p;
                feed(d, SstvEncoder.Encode(test, s1, 48000), 0.05, 3);
                check(got != null && got.Mode == s1 && !got.NoHeader, "modes: Scottie S1 with its header is still received (" + (got == null ? "no picture" : got.Mode.Name) + ")");
            }

            // g. Redraw: slant put in and taken out again gives the same picture; slant left in spoils it
            {
                SstvDecoder d = new SstvDecoder();
                SstvPicture got = null;
                d.Finished += p => got = p;
                feed(d, SstvEncoder.Encode(test, m1, 48000), 0.05, 3);
                byte[,,] want = SstvEncoder.Pixels(test, m1);
                double before = got != null ? Psnr(want, got) : 0;
                bool ok1 = got != null && d.Redraw(got, 300, 0);
                double skewed = got != null ? Psnr(want, got) : 0;
                bool ok2 = got != null && d.Redraw(got, 0, 0);
                double after = got != null ? Psnr(want, got) : 0;
                check(ok1 && ok2 && skewed < before - 6 && Math.Abs(after - before) < 0.5,
                    "Redraw: +300 ppm spoils it (" + skewed.ToString("0.0") + " dB), back to 0 restores it (" + before.ToString("0.0") + " -> " + after.ToString("0.0") + " dB)");
            }
        }
    }
}
