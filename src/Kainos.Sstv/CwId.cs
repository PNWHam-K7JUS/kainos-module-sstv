/*  CwId.cs

A CW identification after a picture (GitHub #72): "DE <callsign>" in Morse, as an audio tone keyed on and off, so it
goes out through the same transmit audio as the picture (on DIGU / USB that's a CW signal on the air at the dial plus
the tone). 20 WPM by the PARIS standard, at 1900 Hz: the Tune tone's frequency, so the transmit level set with Tune
suits it too. Each element rises and falls over 5 ms, so it doesn't click.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;

namespace Kainos.Sstv
{
    internal static class CwId
    {
        public const double ToneHz = 1900, Wpm = 20, GapBefore = 0.6;

        private static readonly Dictionary<char, string> Code = new Dictionary<char, string>
        {
            { 'A', ".-" }, { 'B', "-..." }, { 'C', "-.-." }, { 'D', "-.." }, { 'E', "." }, { 'F', "..-." }, { 'G', "--." },
            { 'H', "...." }, { 'I', ".." }, { 'J', ".---" }, { 'K', "-.-" }, { 'L', ".-.." }, { 'M', "--" }, { 'N', "-." },
            { 'O', "---" }, { 'P', ".--." }, { 'Q', "--.-" }, { 'R', ".-." }, { 'S', "..." }, { 'T', "-" }, { 'U', "..-" },
            { 'V', "...-" }, { 'W', ".--" }, { 'X', "-..-" }, { 'Y', "-.--" }, { 'Z', "--.." },
            { '0', "-----" }, { '1', ".----" }, { '2', "..---" }, { '3', "...--" }, { '4', "....-" }, { '5', "....." },
            { '6', "-...." }, { '7', "--..." }, { '8', "---.." }, { '9', "----." }, { '/', "-..-." },
        };

        public static string Text(string call) { return "DE " + (call ?? "").Trim().ToUpperInvariant(); }

        // the keying, in dot lengths: + on, - off (characters without a Morse code are left out)
        public static List<int> Elements(string text)
        {
            List<int> e = new List<int>();
            string[] words = text.ToUpperInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int w = 0; w < words.Length; w++)
            {
                if (w > 0) e.Add(-7);
                bool firstChar = true;
                foreach (char c in words[w])
                {
                    string code;
                    if (!Code.TryGetValue(c, out code)) continue;
                    if (!firstChar) e.Add(-3);
                    firstChar = false;
                    for (int i = 0; i < code.Length; i++)
                    {
                        if (i > 0) e.Add(-1);
                        e.Add(code[i] == '.' ? 1 : 3);
                    }
                }
            }
            return e;
        }

        // the audio: a gap after the picture, then the Morse, full scale (the engine applies the transmit level)
        public static float[] Audio(string text, int rate)
        {
            double dot = 1.2 / Wpm;
            List<int> el = Elements(text);
            int gap = (int)(GapBefore * rate), ramp = Math.Max(1, rate / 200);
            long total = gap;
            foreach (int x in el) total += (long)Math.Round(Math.Abs(x) * dot * rate);
            total += (int)(0.1 * rate);
            float[] o = new float[total];
            int pos = gap;
            double step = 2 * Math.PI * ToneHz / rate;
            foreach (int x in el)
            {
                int n = (int)Math.Round(Math.Abs(x) * dot * rate);
                if (x > 0)
                    for (int i = 0; i < n; i++)
                    {
                        double env = i < ramp ? 0.5 - 0.5 * Math.Cos(Math.PI * i / ramp) : i >= n - ramp ? 0.5 - 0.5 * Math.Cos(Math.PI * (n - i) / ramp) : 1;
                        o[pos + i] = (float)(Math.Sin(step * (pos + i)) * env);
                    }
                pos += n;
            }
            return o;
        }
    }
}
