/*  SstvEncoder.cs

A picture to SSTV audio: the VIS header (1900 Hz leader, 1200 Hz break, leader, start bit, seven data bits
least significant first at 1100 Hz for 1 and 1300 Hz for 0, even parity, stop bit, 30 ms each), the lines, and
optionally MMSSTV's FSK ID (the callsign, 6 bits a character, least significant first, 1900 Hz = 1, 2100 Hz = 0,
22 ms a bit, between the bytes 0x20 0x2A and 0x01). Continuous phase throughout.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Kainos.Sstv
{
    internal static class SstvEncoder
    {
        // the picture scaled (and cropped to keep its shape) to the mode's size, as R, G, B rows
        public static byte[,,] Pixels(Bitmap src, SstvMode m)
        {
            using (Bitmap b = new Bitmap(m.Width, m.Lines, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    double s = Math.Max(m.Width / (double)src.Width, m.Lines / (double)src.Height);
                    int w = (int)Math.Ceiling(src.Width * s), h = (int)Math.Ceiling(src.Height * s);
                    g.DrawImage(src, (m.Width - w) / 2, (m.Lines - h) / 2, w, h);
                }
                byte[,,] px = new byte[m.Lines, m.Width, 3];
                BitmapData bd = b.LockBits(new Rectangle(0, 0, m.Width, m.Lines), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                byte[] row = new byte[bd.Stride];
                for (int y = 0; y < m.Lines; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, bd.Stride);
                    for (int x = 0; x < m.Width; x++) { px[y, x, 0] = row[3 * x + 2]; px[y, x, 1] = row[3 * x + 1]; px[y, x, 2] = row[3 * x]; }
                }
                b.UnlockBits(bd);
                return px;
            }
        }

        // the tones: (frequency, seconds)
        public static List<KeyValuePair<double, double>> Tones(SstvMode m, byte[,,] px, string fskId)
        {
            List<KeyValuePair<double, double>> t = new List<KeyValuePair<double, double>>();
            Action<double, double> add = (f, s) => t.Add(new KeyValuePair<double, double>(f, s));

            // VIS
            add(SstvModes.FLeader, 0.300); add(SstvModes.FSync, 0.010); add(SstvModes.FLeader, 0.300); add(SstvModes.FSync, 0.030);
            int ones = 0;
            for (int i = 0; i < 7; i++) { int bit = (m.Vis >> i) & 1; ones += bit; add(bit == 1 ? SstvModes.FBit1 : SstvModes.FBit0, 0.030); }
            add(ones % 2 == 1 ? SstvModes.FBit1 : SstvModes.FBit0, 0.030);
            add(SstvModes.FSync, 0.030);

            Func<int, int, int, double> v = (y, x, c) => px[y, x, c];
            switch (m.Family)
            {
                case SstvFamily.Martin:
                    for (int y = 0; y < m.Lines; y++)
                    {
                        add(SstvModes.FSync, m.Sync); add(SstvModes.FBlack, m.Porch);
                        foreach (int c in new[] { 1, 2, 0 })            // G, B, R
                        {
                            for (int x = 0; x < m.Width; x++) add(SstvModes.FreqOf(v(y, x, c)), m.PixelTime);
                            add(SstvModes.FBlack, m.Gap);
                        }
                    }
                    break;
                case SstvFamily.Scottie:
                    add(SstvModes.FSync, m.Sync);                   // the starting sync pulse
                    for (int y = 0; y < m.Lines; y++)
                    {
                        add(SstvModes.FBlack, m.Gap);
                        for (int x = 0; x < m.Width; x++) add(SstvModes.FreqOf(v(y, x, 1)), m.PixelTime);
                        add(SstvModes.FBlack, m.Gap);
                        for (int x = 0; x < m.Width; x++) add(SstvModes.FreqOf(v(y, x, 2)), m.PixelTime);
                        add(SstvModes.FSync, m.Sync); add(SstvModes.FBlack, m.Porch);
                        for (int x = 0; x < m.Width; x++) add(SstvModes.FreqOf(v(y, x, 0)), m.PixelTime);
                    }
                    break;
                case SstvFamily.Robot36:
                    for (int y = 0; y < m.Lines; y++)
                    {
                        add(SstvModes.FSync, m.Sync); add(SstvModes.FBlack, m.Porch);
                        double[] cb = new double[m.Width], cr = new double[m.Width];
                        for (int x = 0; x < m.Width; x++)
                        {
                            double yy, b, r;
                            SstvModes.ToYCbCr(px[y, x, 0], px[y, x, 1], px[y, x, 2], out yy, out b, out r);
                            cb[x] = b; cr[x] = r;
                            add(SstvModes.FreqOf(yy), m.PixelTime);
                        }
                        bool even = y % 2 == 0;                     // even lines carry R-Y, odd lines B-Y
                        add(even ? SstvModes.FBlack : SstvModes.FWhite, m.Gap);
                        add(SstvModes.FLeader, 0.0015);
                        for (int x = 0; x < m.Width; x++) add(SstvModes.FreqOf(even ? cr[x] : cb[x]), m.PixelTime / 2);
                    }
                    break;
                case SstvFamily.PD:
                    for (int y = 0; y < m.Lines; y += 2)
                    {
                        add(SstvModes.FSync, m.Sync); add(SstvModes.FBlack, m.Porch);
                        double[] y0 = new double[m.Width], y1 = new double[m.Width], cb = new double[m.Width], cr = new double[m.Width];
                        for (int x = 0; x < m.Width; x++)
                        {
                            double a, b0, r0, c, b1, r1;
                            SstvModes.ToYCbCr(px[y, x, 0], px[y, x, 1], px[y, x, 2], out a, out b0, out r0);
                            SstvModes.ToYCbCr(px[y + 1, x, 0], px[y + 1, x, 1], px[y + 1, x, 2], out c, out b1, out r1);
                            y0[x] = a; y1[x] = c; cb[x] = (b0 + b1) / 2; cr[x] = (r0 + r1) / 2;
                        }
                        foreach (double[] ch in new[] { y0, cr, cb, y1 })
                            for (int x = 0; x < m.Width; x++) add(SstvModes.FreqOf(ch[x]), m.PixelTime);
                    }
                    break;
            }

            if (!string.IsNullOrEmpty(fskId))
            {
                List<int> bytes = new List<int> { 0x20, 0x2A };
                foreach (char ch in fskId.ToUpperInvariant()) bytes.Add(Math.Max(0, Math.Min(63, ch - 0x20)));
                bytes.Add(0x01);
                foreach (int by in bytes)
                    for (int i = 0; i < 6; i++) add(((by >> i) & 1) == 1 ? 1900 : 2100, 0.022);
            }
            return t;
        }

        // continuous-phase audio, amplitude 1
        public static float[] Audio(List<KeyValuePair<double, double>> tones, int rate)
        {
            double total = 0;
            foreach (KeyValuePair<double, double> t in tones) total += t.Value;
            float[] s = new float[(int)Math.Ceiling(total * rate) + 1];
            double phi = 0, pos = 0;
            int n = 0;
            foreach (KeyValuePair<double, double> t in tones)
            {
                pos += t.Value * rate;
                int end = Math.Min(s.Length, (int)Math.Round(pos));
                double step = 2 * Math.PI * t.Key / rate;
                for (; n < end; n++) { s[n] = (float)Math.Sin(phi); phi += step; if (phi > 2 * Math.PI) phi -= 2 * Math.PI; }
            }
            Array.Resize(ref s, n);
            return s;
        }

        public static float[] Encode(Bitmap picture, SstvMode m, int rate, string fskId = null)
        {
            return Audio(Tones(m, Pixels(picture, m), fskId), rate);
        }
    }
}
