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
                    if (_cut) { _cut = false; Decoder.Cut(); }
                    SstvMode sn = _startNow;
                    if (sn != null) { _startNow = null; Decoder.StartNow(sn); }
                    if (ensureTap())
                    {
                        int rate = _host.Audio.RxRate, n;
                        while (rate > 0 && (n = _host.Audio.ReadRx(_buf, _buf.Length)) > 0)
                        {
                            Decoder.Push(_buf, n, rate);
                            record(_buf, n, rate);
                            spectrum(_buf, n, rate);
                        }
                    }
                }
                catch (Exception ex) { _host.Log("engine: " + ex); Thread.Sleep(200); }
                Thread.Sleep(20);
            }
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

        // ---- the tuning spectrum: 25 Hz bins from 1000 to 2500 Hz, every 100 ms ----

        public const double SpecLo = 1000, SpecHi = 2500, SpecStep = 25;
        public static readonly int SpecBins = (int)((SpecHi - SpecLo) / SpecStep) + 1;
        private readonly float[] _spec = new float[SpecBins];
        private readonly float[] _ring = new float[8192];
        private int _ringPos, _since, _specRate;

        // the latest spectrum (dB), copied
        public float[] Spectrum { get { lock (_spec) return (float[])_spec.Clone(); } }

        private void spectrum(float[] a, int n, int rate)
        {
            for (int i = 0; i < n; i++) { _ring[_ringPos] = a[i]; _ringPos = (_ringPos + 1) % _ring.Length; }
            _specRate = rate;
            _since += n;
            if (_since < rate / 10) return;
            _since = 0;
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
                double k = 2 * Math.Cos(2 * Math.PI * (SpecLo + b * SpecStep) / _specRate), s1 = 0, s2 = 0;
                for (int i = 0; i < len; i++) { double s0 = x[i] + k * s1 - s2; s2 = s1; s1 = s0; }
                double p = (s1 * s1 + s2 * s2 - k * s1 * s2) / (len * len);
                o[b] = (float)(10 * Math.Log10(p + 1e-12));
            }
            lock (_spec) Array.Copy(o, _spec, SpecBins);
            SpectrumReady?.Invoke();
        }
    }
}
