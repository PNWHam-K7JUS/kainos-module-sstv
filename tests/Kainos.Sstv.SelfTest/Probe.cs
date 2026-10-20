using System;
using System.Drawing;
using System.Linq;
namespace Kainos.Sstv.SelfTest
{
    internal static partial class Program
    {
        internal static void Probe(Bitmap test)
        {
            SstvMode m1 = SstvModes.ByShort("M1");
            foreach (double snr in new[] { 6.0, 0.0, -3.0 })
            {
                // the whole picture joined 10 % in, then a fade from 50 % to the end: lines before = signal, after = noise
                SstvDecoder d = new SstvDecoder { AutoStop = false };
                SstvPicture pic = null;
                d.Started += p => pic = p;
                feed(d, joined(test, m1, 0.1, 0, 0.5, 200), sigmaFor(snr), 0);
                double[] sh = d.DebugSyncHere; bool[] ot = d.DebugOnTrack;
                if (sh == null) { Console.WriteLine(snr + " dB: no picture"); continue; }
                int split = (int)(m1.Lines * 0.4) - 2, end = Math.Min(sh.Length, (int)(m1.Lines * 0.9));
                Func<int, int, string> stats = (a, b) =>
                {
                    var v = Enumerable.Range(a, b - a).Select(i => sh[i]).OrderBy(x => x).ToArray();
                    int on = Enumerable.Range(a, b - a).Count(i => ot[i]);
                    int both40 = Enumerable.Range(a, b - a).Count(i => ot[i] && sh[i] >= 0.40);
                    int both45 = Enumerable.Range(a, b - a).Count(i => ot[i] && sh[i] >= 0.45);
                    int ge45 = v.Count(x => x >= 0.45);
                    return "n " + v.Length + " median " + v[v.Length / 2].ToString("0.00") + " p10 " + v[v.Length / 10].ToString("0.00") + " p90 " + v[v.Length * 9 / 10].ToString("0.00") +
                        " onTrack " + on + " sh>=.45 " + ge45 + " onTrack&sh>=.40 " + both40 + " onTrack&sh>=.45 " + both45;
                };
                Console.WriteLine(snr + " dB signal: " + stats(5, split));
                Console.WriteLine(snr + " dB noise : " + stats(split + 4, Math.Min(end, sh.Length)));
            }
        }
    }
}
