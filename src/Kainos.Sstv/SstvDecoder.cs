/*  SstvDecoder.cs

SSTV receive, as a stream: audio in (any rate), pictures out.

  1. The audio at 24 kHz; the instantaneous frequency of every sample (mixed down from 1900 Hz, low-passed,
     the phase difference from one sample to the next).
  2. VIS: 10 ms averages of the frequency, searched for the header (leader, break, leader, start bit, seven bits,
     parity, stop). The leader's frequency gives the mistuning, used from then on; the leader-to-start-bit edge
     gives the picture's start to the sample.
  3. Lines, as they arrive: each line's sync pulse is found near where it's expected, and a straight line fitted
     through the sync times (outliers left out) gives the sender's line length (correcting slant) and where each
     line starts. Pixels are the frequency averaged over each pixel's time.
  4. At the end, the fit with every line, and the whole picture redrawn from it; then MMSSTV's FSK ID callsign,
     if it follows.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace Kainos.Sstv
{
    internal class SstvPicture
    {
        public SstvMode Mode;
        public byte[] Rgb;                  // Width x Lines x 3, rows top first
        public int LinesDone;               // picture lines drawn so far
        public bool Complete;
        public string Callsign = "";        // from the FSK ID, when one came
        public double FreqShift;            // Hz: how far the sender was off tune
        public double SlantPpm;             // the sender's clock against ours (parts per million)
        public DateTime StartUtc;
    }

    internal class SstvDecoder
    {
        public const int Rate = 24000;

        public event Action<SstvPicture> Started;
        public event Action<SstvPicture> Progress;      // more lines drawn (on the decoder's thread)
        public event Action<SstvPicture> Finished;      // complete (or cut short), slant-corrected, with the FSK ID callsign
        public Func<DateTime> Now = () => DateTime.UtcNow;

        // ---- 1. the frequency of every sample ----

        private const int Cap = Rate * 330;             // the longest picture (PD-290, 289 s) and its FSK ID
        private readonly float[] _freq = new float[Cap];
        private long _n;                                // samples so far (24 kHz)
        private readonly Resampler _rs = new Resampler(Rate);
        private readonly List<float> _tmp = new List<float>(8192);

        private readonly double[] _lp;                  // the low-pass after mixing down
        private readonly double[] _hi, _hq;
        private int _hp;
        private double _mixPhase, _prevI = 1, _prevQ;
        private const double Fmix = 1900;

        public SstvDecoder()
        {
            // 1.4 kHz cut-off: wide enough for the shortest pixels (PD-120, 0.19 ms), narrow enough to keep noise down
            int taps = 49;
            double fc = 1400.0 / Rate;
            _lp = new double[taps];
            double sum = 0;
            for (int i = 0; i < taps; i++)
            {
                double x = i - (taps - 1) / 2.0;
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
                _lp[i] = (x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x)) * w;
                sum += _lp[i];
            }
            for (int i = 0; i < taps; i++) _lp[i] /= sum;
            _hi = new double[taps];
            _hq = new double[taps];
        }

        public long Samples { get { return _n; } }
        public SstvPicture Current { get { return _pic; } }
        public bool Receiving { get { return _pic != null && !_pic.Complete; } }

        // audio from the receiver
        public void Push(float[] buf, int count, int rate)
        {
            _tmp.Clear();
            _rs.Process(buf, count, rate, _tmp);
            for (int i = 0; i < _tmp.Count; i++) sample(_tmp[i]);
            work();
        }

        private void sample(float s)
        {
            double c = Math.Cos(_mixPhase), sn = Math.Sin(_mixPhase);
            _mixPhase += 2 * Math.PI * Fmix / Rate;
            if (_mixPhase > 2 * Math.PI) _mixPhase -= 2 * Math.PI;
            _hi[_hp] = s * c;
            _hq[_hp] = -s * sn;
            _hp = (_hp + 1) % _lp.Length;
            double i = 0, q = 0;
            int p = _hp;
            for (int k = 0; k < _lp.Length; k++) { i += _hi[p] * _lp[k]; q += _hq[p] * _lp[k]; p = (p + 1) % _lp.Length; }
            // the phase step: z(n) * conj(z(n-1))
            double re = i * _prevI + q * _prevQ, im = q * _prevI - i * _prevQ;
            _prevI = i; _prevQ = q;
            double f = Fmix + Math.Atan2(im, re) * Rate / (2 * Math.PI);
            _freq[_n % Cap] = (float)Math.Max(800, Math.Min(2800, f));
            _n++;
            block(f);
        }

        // the filter delays everything by half its length: times are corrected by this many samples
        private int delay { get { return (_lp.Length - 1) / 2; } }

        private double F(long n) { return n < 0 || n >= _n || n < _n - Cap ? 1900 : _freq[n % Cap]; }

        private double meanF(double from, double to)
        {
            long a = (long)Math.Floor(from) + delay, b = (long)Math.Ceiling(to) + delay;
            if (b <= a) b = a + 1;
            double s = 0;
            for (long k = a; k < b; k++) s += F(k);
            return s / (b - a);
        }

        // ---- 2. VIS ----

        private const int Block = Rate / 100;           // 10 ms
        private double _bsum;
        private int _bcount;
        private readonly double[] _blocks = new double[128];
        private long _blockCount;
        private long _visHold;                          // no new header until this sample (inside a header just found)

        private void block(double f)
        {
            _bsum += f; _bcount++;
            if (_bcount < Block) return;
            _blocks[_blockCount % _blocks.Length] = _bsum / _bcount;
            _blockCount++;
            _bsum = 0; _bcount = 0;
            if (_n > _visHold) visCheck();
        }

        // the block k back from the newest (0 = newest)
        private double B(int k) { return k >= _blockCount ? 0 : _blocks[(_blockCount - 1 - k) % _blocks.Length]; }

        private void visCheck()
        {
            if (_blockCount < 95) return;
            // newest first: stop bit 0-2, parity 3-5, bit 6 at 6-8 ... bit 0 at 24-26, start bit 27-29,
            // leader 30-59, break 60, leader 61-90
            List<double> lead = new List<double>();
            for (int k = 32; k <= 57; k++) lead.Add(B(k));
            lead.Sort();
            double L = lead[lead.Count / 2], shift = L - SstvModes.FLeader;
            if (Math.Abs(shift) > 150) return;
            if (lead.Count(x => Math.Abs(x - L) < 60) < lead.Count * 0.8) return;
            Func<int, double> mid = k => B(k + 1) - shift;          // the middle of a 30 ms bit starting k blocks back
            if (Math.Abs(mid(27) - SstvModes.FSync) > 80 || Math.Abs(mid(0) - SstvModes.FSync) > 80) return;
            int code = 0, ones = 0;
            for (int bit = 0; bit < 7; bit++)
            {
                double f = mid(24 - 3 * bit);
                if (Math.Abs(f - SstvModes.FBit1) > 90 && Math.Abs(f - SstvModes.FBit0) > 90) return;
                if (f < (SstvModes.FBit1 + SstvModes.FBit0) / 2) { code |= 1 << bit; ones++; }
            }
            double pf = mid(3);
            if (Math.Abs(pf - SstvModes.FBit1) > 90 && Math.Abs(pf - SstvModes.FBit0) > 90) return;
            int parity = pf < 1200 ? 1 : 0;
            if (parity != ones % 2) return;
            SstvMode mode = SstvModes.ByVis(code);
            if (mode == null) { _visHold = _n + Rate / 2; Unknown = code; return; }

            // the leader-to-start-bit edge, to the sample: about 300 ms before the stop bit's end
            long approxEdge = _n - delay - (long)(0.300 * Rate);
            long edge = approxEdge;
            double best = double.MaxValue;
            double half = (SstvModes.FLeader + SstvModes.FSync) / 2 + shift;
            for (long k = approxEdge - Rate / 50; k <= approxEdge + Rate / 50; k++)
            {
                // the falling crossing: before it leader, after it sync
                double before = 0, after = 0;
                for (int j = 1; j <= 24; j++) { before += F(k - j + delay); after += F(k + j + delay); }
                before /= 24; after /= 24;
                if (before > half && after < half)
                {
                    double d = Math.Abs(F(k + delay) - half);
                    if (d < best) { best = d; edge = k; }
                }
            }
            start(mode, edge + (long)Math.Round(0.300 * Rate), shift);
        }

        public int Unknown = -1;                        // the last VIS code heard that isn't a mode here

        // ---- 3. lines ----

        private SstvPicture _pic;
        private long _t0;                               // the first line's start (24 kHz samples, without the filter delay)
        private double _shift;
        private double[] _sync;                         // where each radio line's sync pulse was found (NaN: not found)
        private int _nextLine;
        private double _period;                         // line length now believed (samples)
        private double _phase;                          // where line 0's sync starts (samples)
        private int _missed;
        private double[] _cb, _cr;                      // Robot 36: the last chroma lines

        private void start(SstvMode m, long t0, double shift)
        {
            if (_pic != null && !_pic.Complete) finish(true);
            _pic = new SstvPicture { Mode = m, Rgb = new byte[m.Width * m.Lines * 3], FreqShift = shift, StartUtc = Now() };
            _t0 = t0 + (m.Family == SstvFamily.Scottie ? (long)Math.Round(m.Sync * Rate) : 0);
            _shift = shift;
            _sync = Enumerable.Repeat(double.NaN, m.RadioLines).ToArray();
            _nextLine = 0;
            _period = m.LineTime * Rate;
            _phase = _t0 + m.SyncOffset * Rate;
            _missed = 0;
            _cb = new double[m.Width];
            _cr = new double[m.Width];
            for (int i = 0; i < m.Width; i++) { _cb[i] = 128; _cr[i] = 128; }
            _visHold = _n + (long)(m.Duration * Rate);
            Started?.Invoke(_pic);
        }

        private void work()
        {
            if (_pic == null || _pic.Complete) { idFind(); return; }
            SstvMode m = _pic.Mode;
            bool drew = false;
            while (_nextLine < m.RadioLines)
            {
                // a line can be decoded once all of it (and a little more, for its sync) is in
                double end = _phase + (_nextLine + 1) * _period + 0.02 * Rate + delay;
                if (_n < end) break;
                findSync(_nextLine);
                refit(false);
                drawLine(_nextLine);
                _nextLine++;
                drew = true;
                // the signal is gone: a long run of lines without sync, once the picture is well under way
                _missed = double.IsNaN(_sync[_nextLine - 1]) ? _missed + 1 : 0;
                if (_missed >= 10 && _nextLine > m.RadioLines / 5) { finish(true); return; }
            }
            if (drew) Progress?.Invoke(_pic);
            if (_nextLine >= m.RadioLines) finish(false);
        }

        // the sync pulse of line l: the best stretch of the sync's length below 1350 Hz near where it's expected
        private void findSync(int l)
        {
            SstvMode m = _pic.Mode;
            double expect = _phase + l * _period;
            int len = (int)Math.Round(m.Sync * Rate);
            int span = (int)Math.Round(Math.Min(0.04 * m.LineTime, 0.012) * Rate);
            double thr = 1350 + _shift;
            // a running count over the window
            int from = (int)(-span), to = span;
            double bestScore = -1, bestAt = 0;
            int count = 0;
            long a0 = (long)Math.Round(expect) + from + delay;
            for (int j = 0; j < len; j++) if (F(a0 + j) < thr) count++;
            for (int o = from; o <= to; o++)
            {
                if (o > from)
                {
                    long gone = (long)Math.Round(expect) + o - 1 + delay, come = gone + len;
                    if (F(gone) < thr) count--;
                    if (F(come) < thr) count++;
                }
                double score = count / (double)len;
                // prefer the middle on a tie (an unbroken stretch longer than the pulse)
                if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) < 1e-9 && Math.Abs(o) < Math.Abs(bestAt - expect))) { bestScore = score; bestAt = expect + o; }
            }
            _sync[l] = bestScore >= 0.6 ? bestAt : double.NaN;
        }

        // a straight line through the syncs found (least squares, then again without those more than 2 ms off)
        private void refit(bool all)
        {
            List<int> ls = new List<int>();
            for (int l = 0; l < _sync.Length; l++) if (!double.IsNaN(_sync[l])) ls.Add(l);
            if (ls.Count < 4) return;
            double slope, icpt;
            if (!fit(ls, out slope, out icpt)) return;
            double tol = 0.002 * Rate;
            List<int> good = ls.Where(l => Math.Abs(_sync[l] - (icpt + slope * l)) <= tol).ToList();
            if (good.Count >= 4 && fit(good, out slope, out icpt)) { }
            // keep the line length sensible (within 2 % of the mode's: a clock that far off isn't a clock)
            double nominal = _pic.Mode.LineTime * Rate;
            if (Math.Abs(slope / nominal - 1) > 0.02) return;
            _period = slope;
            _phase = icpt;
            _pic.SlantPpm = (slope / nominal - 1) * 1e6;
        }

        private bool fit(List<int> ls, out double slope, out double icpt)
        {
            double n = ls.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (int l in ls) { sx += l; sy += _sync[l]; sxx += (double)l * l; sxy += l * _sync[l]; }
            double d = n * sxx - sx * sx;
            if (Math.Abs(d) < 1e-9) { slope = icpt = 0; return false; }
            slope = (n * sxy - sx * sy) / d;
            icpt = (sy - slope * sx) / n;
            return true;
        }

        private double px(double t0, double pixelTime, int x)
        {
            double k = _period / (_pic.Mode.LineTime * Rate);    // the sender's clock against ours
            double w = pixelTime * Rate * k;
            double c = t0 + (x + 0.5) * w;
            return SstvModes.ValueOf(meanF(c - w / 2, c + w / 2) - _shift);
        }

        private void put(int y, int x, int r, int g, int b)
        {
            int i = (y * _pic.Mode.Width + x) * 3;
            _pic.Rgb[i] = (byte)r; _pic.Rgb[i + 1] = (byte)g; _pic.Rgb[i + 2] = (byte)b;
        }

        private void drawLine(int l)
        {
            SstvMode m = _pic.Mode;
            double k = _period / (m.LineTime * Rate);
            double ls = _phase + l * _period - m.SyncOffset * Rate * k;     // where the line begins
            Func<double, double> at = s => ls + s * Rate * k;                // a time in the line, in samples
            int w = m.Width;
            switch (m.Family)
            {
                case SstvFamily.Martin:
                {
                    double g0 = at(m.Sync + m.Porch), b0 = at(m.Sync + m.Porch + m.Scan + m.Gap), r0 = at(m.Sync + m.Porch + 2 * (m.Scan + m.Gap));
                    for (int x = 0; x < w; x++) put(l, x, (int)px(r0, m.PixelTime, x), (int)px(g0, m.PixelTime, x), (int)px(b0, m.PixelTime, x));
                    _pic.LinesDone = l + 1;
                    break;
                }
                case SstvFamily.Scottie:
                {
                    double g0 = at(m.Gap), b0 = at(2 * m.Gap + m.Scan), r0 = at(2 * m.Gap + 2 * m.Scan + m.Sync + m.Porch);
                    for (int x = 0; x < w; x++) put(l, x, (int)px(r0, m.PixelTime, x), (int)px(g0, m.PixelTime, x), (int)px(b0, m.PixelTime, x));
                    _pic.LinesDone = l + 1;
                    break;
                }
                case SstvFamily.Robot36:
                {
                    double y0 = at(m.Sync + m.Porch), sep = at(m.Sync + m.Porch + m.Scan), c0 = at(m.Sync + m.Porch + m.Scan + m.Gap + 0.0015);
                    // which chroma this line carries: the separator says (1500 Hz before R-Y, 2300 Hz before B-Y)
                    double sf = meanF(sep + 0.001 * Rate, sep + (m.Gap - 0.001) * Rate) - _shift;
                    bool isCr = sf < 1900;
                    double[] yy = new double[w];
                    for (int x = 0; x < w; x++)
                    {
                        yy[x] = px(y0, m.PixelTime, x);
                        double c = px(c0, m.PixelTime / 2, x);
                        if (isCr) _cr[x] = c; else _cb[x] = c;
                    }
                    for (int x = 0; x < w; x++) { int r, g, b; SstvModes.ToRgb(yy[x], _cb[x], _cr[x], out r, out g, out b); put(l, x, r, g, b); }
                    // a B-Y line completes the pair: the line before it gets this chroma too
                    if (!isCr && l > 0)
                        for (int x = 0; x < w; x++)
                        {
                            int i = ((l - 1) * w + x) * 3;
                            double y1, cbOld, crOld;
                            SstvModes.ToYCbCr(_pic.Rgb[i], _pic.Rgb[i + 1], _pic.Rgb[i + 2], out y1, out cbOld, out crOld);
                            int r, g, b; SstvModes.ToRgb(y1, _cb[x], _cr[x], out r, out g, out b); put(l - 1, x, r, g, b);
                        }
                    _pic.LinesDone = l + 1;
                    break;
                }
                default:    // PD: two picture lines
                {
                    double t = at(m.Sync + m.Porch), s = m.Scan;
                    for (int x = 0; x < w; x++)
                    {
                        double ya = px(t, m.PixelTime, x), cr = px(t + s * Rate * k, m.PixelTime, x), cb = px(t + 2 * s * Rate * k, m.PixelTime, x), yb = px(t + 3 * s * Rate * k, m.PixelTime, x);
                        int r, g, b;
                        SstvModes.ToRgb(ya, cb, cr, out r, out g, out b); put(2 * l, x, r, g, b);
                        SstvModes.ToRgb(yb, cb, cr, out r, out g, out b); put(2 * l + 1, x, r, g, b);
                    }
                    _pic.LinesDone = 2 * l + 2;
                    break;
                }
            }
        }

        // ---- 4. the end: the fit with every line, redrawn; then the FSK ID ----

        private long _idFrom = -1, _idUntil;
        private SstvPicture _idFor;

        // ends the picture being received where it is (the Skip button)
        public void Cut() { finish(true); }

        private void finish(bool cut)
        {
            if (_pic == null || _pic.Complete) return;
            refit(true);
            int lines = Math.Max(_nextLine, cut ? _nextLine : _pic.Mode.RadioLines);
            for (int l = 0; l < lines; l++) drawLine(l);
            _pic.Complete = true;
            // the FSK ID, if any, follows the picture: look for it from just before its end for 1.5 s
            _idFrom = (long)(_t0 + _pic.Mode.RadioLines * _period - 0.05 * Rate);
            _idUntil = _idFrom + (long)(1.6 * Rate);
            _idFor = _pic;
            _visHold = 0;
            if (cut) { _idFor = null; Finished?.Invoke(_pic); }
        }

        private void idFind()
        {
            if (_idFor == null || _n < _idUntil + delay) return;
            SstvPicture p = _idFor;
            _idFor = null;
            p.Callsign = FskId(_idFrom, _idUntil) ?? "";
            Finished?.Invoke(p);
        }

        // MMSSTV's FSK ID between from and to (samples): 22 ms bits, 1900 Hz = 1, 2100 Hz = 0, 6 bits a byte least
        // significant first: 0x20 0x2A, the callsign (each character less 0x20), 0x01
        internal string FskId(long from, long to)
        {
            double bit = 0.022 * Rate, thr = 2000 + _shift;
            int[] pre = { 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1 };     // 0x20, 0x2A least significant first
            for (double o = from; o + bit * 20 < to; o += Rate * 0.001)
            {
                Func<int, int> b = j => meanF(o + j * bit + bit * 0.2, o + (j + 1) * bit - bit * 0.2) < thr ? 1 : 0;
                bool ok = true;
                for (int j = 0; j < 12 && ok; j++) ok = b(j) == pre[j];
                if (!ok) continue;
                string call = "";
                for (int c = 0; c < 12; c++)
                {
                    int v = 0;
                    for (int j = 0; j < 6; j++) v |= b(12 + c * 6 + j) << j;
                    if (v == 0x01) return call.Length >= 3 ? call : null;
                    char ch = (char)(v + 0x20);
                    if (!(char.IsLetterOrDigit(ch) || ch == '/')) break;
                    call += ch;
                }
            }
            return null;
        }
    }

    // any rate to 24 kHz: a low-pass then decimation for whole ratios (48 kHz), linear interpolation otherwise
    internal class Resampler
    {
        private readonly int _out;
        private int _in = -1, _decim;
        private double[] _taps, _hist;
        private int _hp;
        private long _count;
        private double _phase, _prev, _step;

        public Resampler(int outRate) { _out = outRate; }

        private void setup(int inRate)
        {
            _in = inRate;
            _decim = inRate % _out == 0 ? inRate / _out : 0;
            double ratio = Math.Max(1.0, inRate / (double)_out);
            int n = Math.Max(31, (int)(16 * ratio) | 1);
            double fc = 0.45 / ratio;
            _taps = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double x = i - (n - 1) / 2.0;
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (n - 1));
                _taps[i] = (x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x)) * w;
                sum += _taps[i];
            }
            for (int i = 0; i < n; i++) _taps[i] /= sum;
            _hist = new double[n];
            _hp = 0; _count = 0; _phase = 0; _prev = 0;
            _step = inRate / (double)_out;
        }

        public void Process(float[] src, int count, int inRate, List<float> dst)
        {
            if (inRate != _in) setup(inRate);
            if (inRate == _out) { for (int i = 0; i < count; i++) dst.Add(src[i]); return; }
            int n = _taps.Length;
            for (int k = 0; k < count; k++)
            {
                _hist[_hp] = src[k];
                _hp = (_hp + 1) % n;
                _count++;
                if (_decim > 0) { if (_count % _decim == 0) dst.Add((float)filtered()); continue; }
                double y = filtered();
                while (_phase <= 1.0) { dst.Add((float)(_prev + (y - _prev) * _phase)); _phase += _step; }
                _phase -= 1.0;
                _prev = y;
            }
        }

        private double filtered()
        {
            int n = _taps.Length;
            double acc = 0;
            int p = _hp;
            for (int i = 0; i < n; i++) { acc += _hist[p] * _taps[i]; p = (p + 1) % n; }
            return acc;
        }
    }
}
