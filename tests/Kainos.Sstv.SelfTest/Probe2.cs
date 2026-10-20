using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
namespace Kainos.Sstv.SelfTest
{
    internal static partial class Program
    {
        // how often rejoining works, and false starts in noise, over many tries
        internal static void Probe2(Bitmap test)
        {
            SstvMode m1 = SstvModes.ByShort("M1");
            int ok = 0;
            for (int t = 0; t < 25; t++)
            {
                SstvDecoder d = new SstvDecoder();
                List<string> log = new List<string>();
                int resumed = 0, started = 0, dropped = 0;
                d.Abandoned += _ => dropped++;
                d.Diag += x => log.Add(x);
                d.Started += _ => started++;
                d.Resumed += _ => resumed++;
                feed(d, joined(test, m1, 0.1, 0, 0.3, 35), sigmaFor(6), 30);
                if (resumed == 1 && started - dropped == 1) ok++;
                else Console.WriteLine("try " + t + ": started " + started + " resumed " + resumed + "\n   " + string.Join("\n   ", log));
            }
            Console.WriteLine("rejoined " + ok + " of 25");
            if (Environment.GetEnvironmentVariable("PROBE_NOISE") == null) return;
            foreach (int sens in new[] { 1, 2 })
            {
                SstvDecoder d = new SstvDecoder { Sensitivity = sens };
                int started = 0, dropped = 0;
                d.Started += _ => started++;
                d.Abandoned += _ => dropped++;
                feed(d, new float[0], 0.3, 600);
                Console.WriteLine("sensitivity " + sens + ": 10 min of noise, " + started + " started, " + dropped + " dropped as false starts");
            }
        }
    }
}
