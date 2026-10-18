/*  SstvModes.cs

The SSTV modes: timings from slowrx's mode table (N7CXI's "Proposal for SSTV Mode Specifications", Dayton 2000;
KB4YZ's line timings), line layouts as slowrx and PySSTV build them, VIS codes from KB4YZ's list.

  Martin  (GBR):   sync, porch, G, gap, B, gap, R, gap
  Scottie (GBR):   gap, G, gap, B, sync, porch, R        (and one sync pulse before the first line)
  Robot 36 (YUV):  sync, porch, Y, separator (1500 Hz before R-Y, 2300 Hz before B-Y), porch (1900 Hz), R-Y or B-Y
                   (alternate lines), each chroma half the luminance's length
  PD (YUV):        sync, porch, Y(line n), R-Y, B-Y (both the average of lines n and n+1), Y(line n+1)

Frequencies: sync 1200 Hz, black 1500 Hz, white 2300 Hz. Colour difference is full-range YCbCr (JPEG / BT.601,
as slowrx and PySSTV decode and encode it).

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace Kainos.Sstv
{
    internal enum SstvFamily { Martin, Scottie, Robot36, PD }

    internal class SstvMode
    {
        public string Name, Short;
        public int Vis;
        public SstvFamily Family;
        public int Width, Lines;
        public double Sync, Porch, Gap;     // seconds
        public double PixelTime;            // seconds (Robot 36: the luminance pixel; chroma pixels are twice as fast)

        public double Scan { get { return PixelTime * Width; } }

        // the time from one sync pulse to the next (PD: one radio line carries two picture lines)
        public double LineTime
        {
            get
            {
                switch (Family)
                {
                    case SstvFamily.Martin: return Sync + Porch + 3 * Gap + 3 * Scan;     // porch = the first gap
                    case SstvFamily.Scottie: return Sync + Porch + 2 * Gap + 3 * Scan;
                    case SstvFamily.Robot36: return Sync + Porch + Scan + Gap + 0.0015 + Scan / 2;
                    default: return Sync + Porch + 4 * Scan;
                }
            }
        }

        public int RadioLines { get { return Family == SstvFamily.PD ? Lines / 2 : Lines; } }
        public double Duration { get { return RadioLines * LineTime + (Family == SstvFamily.Scottie ? Sync : 0); } }

        // where in a line the sync pulse starts (Scottie's is mid-line, before red)
        public double SyncOffset { get { return Family == SstvFamily.Scottie ? Gap + Scan + Gap + Scan : 0; } }

        public override string ToString() { return Name; }
    }

    internal static class SstvModes
    {
        private static SstvMode martin(string n, string s, int vis, double px) { return new SstvMode { Name = n, Short = s, Vis = vis, Family = SstvFamily.Martin, Width = 320, Lines = 256, Sync = 4.862e-3, Porch = 0.572e-3, Gap = 0.572e-3, PixelTime = px }; }
        private static SstvMode scottie(string n, string s, int vis, double px) { return new SstvMode { Name = n, Short = s, Vis = vis, Family = SstvFamily.Scottie, Width = 320, Lines = 256, Sync = 9e-3, Porch = 1.5e-3, Gap = 1.5e-3, PixelTime = px }; }
        private static SstvMode pd(string n, string s, int vis, int w, int h, double px) { return new SstvMode { Name = n, Short = s, Vis = vis, Family = SstvFamily.PD, Width = w, Lines = h, Sync = 20e-3, Porch = 2.08e-3, Gap = 0, PixelTime = px }; }

        public static readonly SstvMode[] All =
        {
            martin("Martin M1", "M1", 0x2C, 0.4576e-3),
            martin("Martin M2", "M2", 0x28, 0.2288e-3),
            scottie("Scottie S1", "S1", 0x3C, 0.4320e-3),
            scottie("Scottie S2", "S2", 0x38, 0.2752e-3),
            scottie("Scottie DX", "SDX", 0x4C, 1.08e-3),
            new SstvMode { Name = "Robot 36", Short = "R36", Vis = 0x08, Family = SstvFamily.Robot36, Width = 320, Lines = 240, Sync = 9e-3, Porch = 3e-3, Gap = 4.5e-3, PixelTime = 0.275e-3 },
            pd("PD-50", "PD50", 0x5D, 320, 256, 0.286e-3),
            pd("PD-90", "PD90", 0x63, 320, 256, 0.532e-3),
            pd("PD-120", "PD120", 0x5F, 640, 496, 0.19e-3),
            pd("PD-160", "PD160", 0x62, 512, 400, 0.382e-3),
            pd("PD-180", "PD180", 0x60, 640, 496, 0.286e-3),
            pd("PD-240", "PD240", 0x61, 640, 496, 0.382e-3),
            pd("PD-290", "PD290", 0x5E, 800, 616, 0.286e-3),
        };

        public static SstvMode ByVis(int vis) { return All.FirstOrDefault(m => m.Vis == vis); }
        public static SstvMode ByShort(string s) { return All.FirstOrDefault(m => string.Equals(m.Short, s, StringComparison.OrdinalIgnoreCase)); }

        public const double FSync = 1200, FBlack = 1500, FWhite = 2300, FLeader = 1900, FBit1 = 1100, FBit0 = 1300;
        public static double FreqOf(double value) { return FBlack + (FWhite - FBlack) * value / 255.0; }
        public static double ValueOf(double freq) { return Math.Max(0, Math.Min(255, (freq - FBlack) * 255.0 / (FWhite - FBlack))); }

        // full-range YCbCr (JPEG / BT.601)
        public static void ToYCbCr(int r, int g, int b, out double y, out double cb, out double cr)
        {
            y = 0.299 * r + 0.587 * g + 0.114 * b;
            cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
            cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;
        }

        public static void ToRgb(double y, double cb, double cr, out int r, out int g, out int b)
        {
            r = clip(y + 1.402 * (cr - 128));
            g = clip(y - 0.344136 * (cb - 128) - 0.714136 * (cr - 128));
            b = clip(y + 1.772 * (cb - 128));
        }

        private static int clip(double v) { return v < 0 ? 0 : v > 255 ? 255 : (int)Math.Round(v); }
    }
}
