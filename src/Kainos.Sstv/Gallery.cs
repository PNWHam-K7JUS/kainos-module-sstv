/*  Gallery.cs

Received pictures on disk: DataFolder\Received\<yyyyMMdd-HHmmss>Z_<mode>[_<callsign>].png, each with a .json
beside it (mode, callsign, time, frequency, tuning, slant, whether it was complete).

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Kainos.Sstv
{
    internal class GalleryItem
    {
        public string Path;
        public DateTime Utc;
        public string Mode = "", Call = "";

        public string Caption
        {
            get { return (Call.Length > 0 ? Call + "  " : "") + Mode + "  " + Utc.ToString("MMM d HH:mm", CultureInfo.InvariantCulture) + "Z"; }
        }
    }

    internal static class Gallery
    {
        private static readonly Regex Name = new Regex(@"^(\d{8}-\d{6})Z_([A-Za-z0-9]+)(?:_(.+))?$");

        public static Bitmap ToBitmap(SstvPicture p)
        {
            int w = p.Mode.Width, h = p.Mode.Lines;
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            CopyInto(p, b);
            return b;
        }

        // the picture's pixels into a bitmap of its size
        public static void CopyInto(SstvPicture p, Bitmap b)
        {
            int w = p.Mode.Width, h = p.Mode.Lines;
            BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            byte[] row = new byte[bd.Stride];
            byte[] rgb = p.Rgb;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 3;
                    row[3 * x] = rgb[i + 2]; row[3 * x + 1] = rgb[i + 1]; row[3 * x + 2] = rgb[i];
                }
                Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, bd.Stride);
            }
            b.UnlockBits(bd);
        }

        public static GalleryItem Save(string folder, SstvPicture p, double dialHz, bool complete)
        {
            Directory.CreateDirectory(folder);
            string call = Regex.Replace(p.Callsign ?? "", @"[^A-Za-z0-9]", "-");
            string stem = p.StartUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z_" + p.Mode.Short + (call.Length > 0 ? "_" + call : "");
            string path = System.IO.Path.Combine(folder, stem + ".png");
            using (Bitmap b = ToBitmap(p)) b.Save(path, ImageFormat.Png);
            CultureInfo ic = CultureInfo.InvariantCulture;
            StringBuilder j = new StringBuilder();
            j.Append("{\n");
            j.Append("  \"mode\": \"").Append(p.Mode.Name).Append("\",\n");
            j.Append("  \"callsign\": \"").Append(p.Callsign ?? "").Append("\",\n");
            j.Append("  \"utc\": \"").Append(p.StartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", ic)).Append("\",\n");
            j.Append("  \"dialHz\": ").Append(dialHz.ToString("0", ic)).Append(",\n");
            j.Append("  \"tuneHz\": ").Append(p.FreqShift.ToString("0", ic)).Append(",\n");
            j.Append("  \"slantPpm\": ").Append(p.SlantPpm.ToString("0", ic)).Append(",\n");
            j.Append("  \"lines\": ").Append(p.LinesDone.ToString(ic)).Append(",\n");
            j.Append("  \"complete\": ").Append(complete ? "true" : "false").Append("\n");
            j.Append("}\n");
            File.WriteAllText(System.IO.Path.ChangeExtension(path, ".json"), j.ToString());
            return Parse(path) ?? new GalleryItem { Path = path, Utc = p.StartUtc, Mode = p.Mode.Short, Call = p.Callsign ?? "" };
        }

        public static GalleryItem Parse(string path)
        {
            Match m = Name.Match(System.IO.Path.GetFileNameWithoutExtension(path));
            if (!m.Success) return null;
            DateTime t;
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) return null;
            SstvMode mode = SstvModes.ByShort(m.Groups[2].Value);
            return new GalleryItem { Path = path, Utc = t, Mode = mode != null ? mode.Name : m.Groups[2].Value, Call = m.Groups[3].Success ? m.Groups[3].Value : "" };
        }

        // newest first
        public static List<GalleryItem> Load(string folder, int max)
        {
            if (!Directory.Exists(folder)) return new List<GalleryItem>();
            return Directory.GetFiles(folder, "*.png").Select(Parse).Where(i => i != null)
                .OrderByDescending(i => i.Utc).Take(max).ToList();
        }

        // a picture file read without keeping it locked
        public static Bitmap Open(string path)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
                using (Image img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch { return null; }
        }

        public static Bitmap Thumb(Bitmap src, int h)
        {
            int w = Math.Max(1, src.Width * h / src.Height);
            Bitmap t = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(t))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return t;
        }
    }
}
