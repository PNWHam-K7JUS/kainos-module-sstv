/*  SstvEngine.cs

Receive: a thread that takes the receiver's audio from Kainos (IKainosAudio), feeds the decoder, and keeps a small
spectrum of the SSTV tones (1000 - 2500 Hz) for tuning. The decoder's events come on this thread.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Threading;
using Kainos.Modules;

namespace Kainos.Sstv
{
    internal class SstvEngine
    {
        private readonly IKainosHost _host;
        public readonly SstvDecoder Decoder = new SstvDecoder();

        private Thread _thread;
        private volatile bool _run;
        private volatile bool _tapOk, _lost;
        private int _tapRx = -1;

        public volatile int Rx;
        public event Action<string> Status;
        public event Action RxLost;                 // another terminal or module took the audio
        public event Action SpectrumReady;

        public SstvEngine(IKainosHost host) { _host = host; }

        public void Start()
        {
            if (_thread != null) return;
            _run = true;
            _thread = new Thread(work) { IsBackground = true, Name = "Kainos SSTV" };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            if (_thread != null && !_thread.Join(1500)) _host.Log("engine thread didn't stop in time");
            _thread = null;
            endTx(false);
            _host.Audio.ReleaseRx();
            _tapOk = false;
        }

        // listen again: after the audio was taken by something else, or on another receiver
        public void Listen() { _lost = false; _tapOk = false; }

        public bool Listening { get { return _tapOk; } }
        public bool Lost { get { return _lost; } }

        // ends the picture being received where it is (done on the engine's thread, which owns the decoder)
        public void Cut() { _cut = true; }
        private volatile bool _cut;

        // draws a finished picture again with its slant (ppm) and start (ms) moved: the Adjust window. Done on the engine's
        // thread, which owns the decoder; Redrawn says whether it could (its audio is kept for 330 s)
        public void Redraw(SstvPicture p, double ppm, double ms) { _redraw = Tuple.Create(p, ppm, ms); }
        private volatile Tuple<SstvPicture, double, double> _redraw;
        public event Action<SstvPicture, bool> Redrawn;

        // starts a picture in a mode now, without waiting for a header
        public void StartNow(SstvMode m) { _startNow = m; }
        private volatile SstvMode _startNow;

        private bool ensureTap()
        {
            int rx = Rx;
            if (_tapOk && _tapRx == rx) return true;
            if (_lost) return false;                // wait to be asked: taking it straight back would fight the other user
            string why;
            if (!_host.Audio.AcquireRx(rx, () => { _tapOk = false; _lost = true; RxLost?.Invoke(); }, out why))
            {
                Status?.Invoke("Can't listen to RX" + (rx + 1) + ": " + why);
                _lost = true;
                return false;
            }
            _tapOk = true;
            _tapRx = rx;
            return true;
        }

        private readonly float[] _buf = new float[16384];

        private void work()
        {
            while (_run)
            {
                try
                {
                    if (_cut) { _cut = false; Decoder.Skip(); }
                    SstvMode sn = _startNow;
                    if (sn != null) { _startNow = null; Decoder.StartNow(sn); }
                    Tuple<SstvPicture, double, double> rd = _redraw;
                    if (rd != null) { _redraw = null; bool ok = Decoder.Redraw(rd.Item1, rd.Item2, rd.Item3); Redrawn?.Invoke(rd.Item1, ok); }
                    float[] pend = _pendingWave;
                    if (pend != null) { _pendingWave = null; startTx(pend); }
                    if (Sending) feedTx();
                    if (ensureTap())
                    {
                        int rate = _host.Audio.RxRate, n;
                        while (rate > 0 && (n = _host.Audio.ReadRx(_buf, _buf.Length)) > 0)
                        {
                            // while sending (and just after) what comes back is our own signal or nothing: not decoded
                            if (Sending || DateTime.UtcNow < _rxMuteUntil) continue;
                            Decoder.Push(_buf, n, rate);
                            record(_buf, n, rate);
                            spectrum(_buf, n, rate);
                        }
                    }
                }
                catch (Exception ex) { _host.Log("engine: " + ex); Thread.Sleep(200); }
                Thread.Sleep(Sending ? 10 : 20);
            }
        }

        // ---- transmit ----
        // As the Digital Modes module does it: the transmit audio feed started, then MOX; about 0.25 s kept queued; stopped
        // by Halt, by the radio being un-keyed elsewhere (MOX button, foot switch), or if MOX never comes; then the feed
        // stopped and MOX off.

        public double LevelDb = -10;                // transmit audio level (dB below full scale): set for full power, ALC barely moving
        public volatile bool Sending;               // a picture (or the tune tone) is going out
        public volatile bool Tuning;
        public event Action<double> TxProgress;     // 0 - 1, on the engine's thread
        public event Action<bool> TxEnded;          // true: sent to the end

        private volatile float[] _pendingWave;
        private float[] _txWave;
        private int _txPos, _txRate;
        private volatile bool _halt;
        private bool _moxSeen;
        private DateTime _moxAskedAt, _rxMuteUntil;
        private const double TxLead = 0.25;         // silence first, for the radio to switch over

        public double TxSecondsLeft { get { float[] w = _txWave; return w == null || _txRate <= 0 ? 0 : (w.Length - _txPos) / (double)_txRate; } }

        // the audio for a picture in a mode, with the FSK ID callsign after it (null: none), at the transmit level
        public float[] BuildTx(System.Drawing.Bitmap picture, SstvMode m, string fskId)
        {
            int rate = _host.Audio.TxRate;
            if (rate <= 0) rate = 48000;
            float[] a = SstvEncoder.Audio(SstvEncoder.Tones(m, SstvEncoder.Pixels(picture, m), fskId), rate);
            return shape(a, rate);
        }

        private float[] shape(float[] a, int rate)
        {
            float amp = (float)Math.Pow(10, LevelDb / 20.0);
            int lead = (int)(TxLead * rate), ramp = rate / 200;      // 5 ms ramps: no clicks
            float[] o = new float[lead + a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                double env = i < ramp ? i / (double)ramp : i > a.Length - ramp ? (a.Length - i) / (double)ramp : 1;
                o[lead + i] = (float)(a[i] * amp * env);
            }
            return o;
        }

        // sends audio from BuildTx; returns why not, or null
        public string Send(float[] wave)
        {
            if (!_host.Radio.PowerOn) return "The radio is off";
            if (Sending) return "Already sending";
            if (_thread == null) return "SSTV isn't open";
            _txRate = _host.Audio.TxRate;
            _pendingWave = wave;
            return null;
        }

        // a steady 1900 Hz tone (up to 30 s) to set the transmit level and drive
        public string Tune()
        {
            int rate = _host.Audio.TxRate;
            if (rate <= 0) return "No transmit audio stream";
            float[] t = new float[rate * 30];
            double step = 2 * Math.PI * 1900 / rate;
            for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Sin(step * i);
            string why = Send(shape(t, rate));
            if (why == null) Tuning = true;
            return why;
        }

        public void Halt() { _halt = true; _pendingWave = null; }

        private void startTx(float[] wave)
        {
            if (!_host.Radio.PowerOn) { Status?.Invoke("The radio is off"); return; }
            if (Decoder.Receiving) Decoder.Cut();
            _txWave = wave;
            _txPos = 0;
            _txRate = _host.Audio.TxRate;
            _halt = false;
            _moxSeen = false;
            _moxAskedAt = DateTime.UtcNow;
            Sending = true;
            _host.Audio.TxStart();
            _host.BeginInvoke(() => _host.Radio.SetMox(true));
            _host.Log((Tuning ? "tune" : "TX") + " " + (wave.Length / (double)Math.Max(1, _txRate)).ToString("0.0") + " s at " + LevelDb.ToString("0") + " dB");
            feedTx();
        }

        private void feedTx()
        {
            if (!Sending || _txWave == null) return;
            bool keyed = _host.Radio.Transmitting;
            if (keyed) _moxSeen = true;
            if (_halt || (_moxSeen && !keyed) || (!_moxSeen && (DateTime.UtcNow - _moxAskedAt).TotalSeconds > 2))
            {
                if (!_moxSeen && !_halt) Status?.Invoke("The radio didn't go into transmit");
                else if (!_halt) Status?.Invoke("Transmit stopped: the radio was un-keyed");
                endTx(false);
                return;
            }
            int ahead = Math.Max(2048, _txRate / 4);
            while (_txPos < _txWave.Length && _host.Audio.TxQueued < ahead)
            {
                int n = Math.Min(4096, _txWave.Length - _txPos);
                float[] part = new float[n];
                Array.Copy(_txWave, _txPos, part, 0, n);
                int w = _host.Audio.TxWrite(part, n);
                _txPos += w;
                if (w < n) break;
            }
            TxProgress?.Invoke(_txPos / (double)_txWave.Length);
            if (_txPos >= _txWave.Length && _host.Audio.TxQueued == 0) endTx(true);
        }

        private void endTx(bool done)
        {
            if (!Sending) return;
            Sending = false;
            Tuning = false;
            _host.Audio.TxStop();
            _host.BeginInvoke(() => _host.Radio.SetMox(false));
            _txWave = null;
            _rxMuteUntil = DateTime.UtcNow.AddSeconds(1);
            TxEnded?.Invoke(done);
        }

        // ---- the last 3 minutes of audio, to save for a look at what was heard ----

        private const int RecSeconds = 180;
        private short[] _rec;
        private int _recRate, _recPos;
        private bool _recFull;

        private void record(float[] a, int n, int rate)
        {
            lock (this)
            {
                if (rate != _recRate || _rec == null) { _recRate = rate; _rec = new short[rate * RecSeconds]; _recPos = 0; _recFull = false; }
                for (int i = 0; i < n; i++)
                {
                    _rec[_recPos++] = (short)Math.Max(-32767, Math.Min(32767, a[i] * 16384));
                    if (_recPos == _rec.Length) { _recPos = 0; _recFull = true; }
                }
            }
        }

        // writes what's been heard (up to 3 minutes) as a WAV file; returns the seconds written
        public double SaveRecording(string path)
        {
            short[] s;
            int rate;
            lock (this)
            {
                if (_rec == null) return 0;
                rate = _recRate;
                int len = _recFull ? _rec.Length : _recPos;
                s = new short[len];
                if (_recFull) { Array.Copy(_rec, _recPos, s, 0, _rec.Length - _recPos); Array.Copy(_rec, 0, s, _rec.Length - _recPos, _recPos); }
                else Array.Copy(_rec, s, len);
            }
            using (System.IO.BinaryWriter w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + s.Length * 2);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(s.Length * 2);
                foreach (short v in s) w.Write(v);
            }
            return s.Length / (double)rate;
        }

        // ---- the waterfall and spectrum: 10 Hz bins from 900 to 2500 Hz, a line every 100 ms; and the input level ----

        public const double SpecLo = 900, SpecHi = 2500, SpecStep = 10;
        public static readonly int SpecBins = (int)((SpecHi - SpecLo) / SpecStep) + 1;
        private readonly float[] _ring = new float[8192];
        private int _ringPos, _since;
        private double _peak, _sumSq;
        private int _levelN;
        private readonly System.Collections.Generic.Queue<float[]> _lines = new System.Collections.Generic.Queue<float[]>();

        // the input level over the last 100 ms (dB below full scale)
        public volatile float PeakDb = -120, RmsDb = -120;

        // the spectrum lines since the last call (dB, oldest first)
        public float[][] TakeLines() { lock (_lines) { float[][] l = _lines.ToArray(); _lines.Clear(); return l; } }

        private void spectrum(float[] a, int n, int rate)
        {
            for (int i = 0; i < n; i++)
            {
                float v = a[i];
                _ring[_ringPos] = v;
                _ringPos = (_ringPos + 1) % _ring.Length;
                double av = Math.Abs(v);
                if (av > _peak) _peak = av;
                _sumSq += v * v;
            }
            _levelN += n;
            _since += n;
            if (_since < rate / 10) return;
            _since = 0;
            PeakDb = (float)(20 * Math.Log10(_peak + 1e-6));
            RmsDb = (float)(10 * Math.Log10(_sumSq / Math.Max(1, _levelN) + 1e-12));
            _peak = 0; _sumSq = 0; _levelN = 0;

            int len = Math.Min(_ring.Length, rate * 8 / 100);        // 80 ms
            float[] x = new float[len];
            for (int i = 0; i < len; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (len - 1));
                x[i] = (float)(_ring[(_ringPos - len + i + _ring.Length) % _ring.Length] * w);
            }
            float[] o = new float[SpecBins];
            for (int b = 0; b < SpecBins; b++)
            {
                // Goertzel
                double k = 2 * Math.Cos(2 * Math.PI * (SpecLo + b * SpecStep) / rate), s1 = 0, s2 = 0;
                for (int i = 0; i < len; i++) { double s0 = x[i] + k * s1 - s2; s2 = s1; s1 = s0; }
                double p = (s1 * s1 + s2 * s2 - k * s1 * s2) / ((double)len * len);
                o[b] = (float)(10 * Math.Log10(p + 1e-14));
            }
            lock (_lines) { _lines.Enqueue(o); while (_lines.Count > 100) _lines.Dequeue(); }
            SpectrumReady?.Invoke();
        }
    }
}
