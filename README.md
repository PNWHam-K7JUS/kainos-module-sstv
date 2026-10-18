# Kainos SSTV (slow-scan TV)

A module for [Kainos](https://github.com/PNWHam-K7JUS/Kainos), the Hermes Lite 2 SDR program: SSTV pictures received inside Kainos, in the Kainos look, with no virtual audio cables or separate program.

This first version receives. Sending comes next. It will have a first-run setup that asks for your station details once and makes ready-to-use CQ, reply and 73 templates from them, and a Reply button that answers in the sender's mode.

- **Modes found automatically** from the VIS header: Martin M1 and M2, Scottie S1, S2 and DX, Robot 36, and PD-50, 90, 120, 160, 180, 240 and 290. The header is found from the strongest tone in each 10 ms, as slowrx does, so weak signals start too: in the self-test, dependably down to 0 dB SNR in a 3 kHz bandwidth, and mostly at -6 dB.
- **Start** begins a picture now in a mode you pick, for when its header was missed (QRM, or you tuned in part way). The line timing is found from the sync pulses.
- **The picture as it arrives**, line by line, scaled to fit the pane.
- **Weak signals**: the line sync is found from the 1200 Hz tone's share of the signal (narrowband), so pictures keep their lines straight down to about 0 dB SNR (noisy, but recognisable). Only the SSTV band (1000 to 2500 Hz, sharply filtered) reaches the picture decoding, so a strong station or carrier elsewhere in the passband doesn't turn the picture to noise. A confirmed picture runs to its end through fades; a header followed by no picture is dropped rather than drawn as noise, and a new header part way through a picture starts the new one.
- **Slant correction**: the sender's sound-card clock is measured from the sync pulses and the picture is redrawn straight when it ends.
- **Off-tune signals** (up to about 150 Hz) are measured from the VIS leader and allowed for.
- **Who sent it**: the callsign from MMSSTV's FSK ID, when the sender has it turned on (most do).
- **Gallery** along the bottom, newest first. Click a picture to look at it, click the big picture to go back to live, and right-click one to open, copy, show in its folder or delete it (to the Recycle Bin). Pictures are saved as PNG in the module's data folder (**Pictures** opens it), each with a `.json` of its details: mode, callsign, time, frequency, tuning and slant.
- **What's being heard**, even before a picture starts: the details column has a scrolling waterfall (the last 40 s) and a spectrum of 900 to 2500 Hz, with the sync (1200 Hz), black (1500 Hz), leader (1900 Hz) and white (2300 Hz) tones marked, and an input level meter (says "no audio", "very low" or "clipping" when it should). The panadapter shows the same tones as lines from the VFO.
- **Band menu**: tunes to the usual SSTV frequencies (3.845 and 7.171 LSB; 14.230, 14.233, 21.340, 28.680 and 50.950 USB) and sets DIGL or DIGU.
- **Options > Save the last 3 minutes of audio** writes a WAV of what the module heard, for a signal that didn't decode. The self-test decodes one with `Kainos.Sstv.SelfTest.exe --wav <file>` and says what it found and why it turned down any header it nearly took. Those near misses are also in the module's log and, briefly, in the status line.
- **Skip** ends a picture early. Pictures cut short (signal lost, or Skip) are kept if a quarter or more came in (Options).
- At 125 % or 150 % display scaling, the picture is drawn at the screen's own resolution (Options, "Sharp picture").

## Installing

In Kainos: **Setup > Modules**, choose **SSTV (slow-scan TV)** and **Install**. Then open it from **Modes > SSTV** (or the menu bar in Classic layout).

To test a build before it's released, use **Install from file...** with the zip from `build.ps1`.

One digital mode is open at a time: SSTV, FT8 / FT4 (the Digital Modes module) and the RTTY and CW terminals share the receiver's audio. Opening one closes whichever had it.

## How it works

| Part | What it does |
|---|---|
| `SstvModes.cs` | The mode table: VIS codes, sync, porch and gap times, pixel times and line layouts. |
| `SstvDecoder.cs` | Resamples to 24 kHz and finds the instantaneous frequency of every sample (mixed down from 1900 Hz, low-passed, phase difference). Finds the VIS header in 10 ms steps, then decodes each line once it is all in. It fits a straight line through the sync pulses for slant, redraws the whole picture at the end, then reads the FSK ID. |
| `SstvEncoder.cs` | A picture to SSTV audio: VIS, lines and FSK ID. Used by the self-test now, and for sending next. |
| `SstvEngine.cs` | The thread that takes the receiver's audio from Kainos and feeds the decoder, plus the tuning spectrum. |
| `Gallery.cs` | Saving and listing received pictures. |
| `UI/` | The pane, drawn in the Kainos colours. |

## Building

You need Visual Studio 2022 or later (.NET desktop development) and a Kainos build (for `Kainos.ModuleApi.dll`).

```
.\build.ps1
```

The self-test encodes a test picture in every mode and decodes it. It also decodes the modes used most with noise, a 70 Hz tuning error and a sender's clock 500 ppm fast, and checks the FSK ID. It runs the module's window with two pictures played to it. With `-PySstv <folder>` it also decodes pictures made by PySSTV, an independent encoder.

## Licence

GPL version 3 or later (see LICENSE). Copyright (C) 2026 Justin Cron K7JUS.
