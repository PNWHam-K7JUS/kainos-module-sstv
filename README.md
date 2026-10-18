# Kainos SSTV (slow-scan TV)

A module for [Kainos](https://github.com/PNWHam-K7JUS/Kainos), the Hermes Lite 2 SDR program: SSTV pictures received inside Kainos, in the Kainos look, with no virtual audio cables or separate program.

## Sending

- **First-run setup**: the first time SSTV opens it asks for your callsign, name, town, grid, radio, antenna, power, a photo (optional) and the mode for your CQs, showing your CQ picture as you type. **Create my pictures** makes your **CQ**, **Reply** and **73** pictures from them. Options > Station setup... changes them later.
- **The send bar**: **To** (their callsign, filled in from the FSK ID of the picture you received), the **report** (595, 575...), **CQ**, **Reply** and **73**, and **More** (your other pictures, Edit pictures..., Station setup...). A button shows the picture first; **Send** transmits it, **Stop** stops.
- **Reply** answers the last picture received (or the one you click in the gallery): their callsign and report in the text, their picture small on the right, **in the mode they used**. **Mode** can be set by hand instead of Auto (CQs in your mode, replies in theirs).
- **Your callsign goes out after each picture** as MMSSTV's FSK ID, so other stations' software shows who sent it (Station setup to turn off).
- **Edit pictures...**: change any picture's top line, big text and bottom lines (with placeholders such as {CALL}, {RST}, {NAME}, {GRID}), its background (your photo, plain, or the picture received) and colour, or add your own.
- **Transmitting** goes through Kainos as the other digital modes do: no audio cables or CAT. The radio keys only while the picture goes out, and stops if it's un-keyed elsewhere. Received audio is ignored while sending. Set the level once with **Tune** (a 1900 Hz tone) and **Options > Transmit level**: full power with the ALC barely moving.

- **Modes found automatically** from the VIS header: Martin M1 and M2, Scottie S1, S2 and DX, Robot 36, and PD-50, 90, 120, 160, 180, 240 and 290. The header is found from the strongest tone in each 10 ms, as slowrx does, so weak signals start too: in the self-test, dependably down to 0 dB SNR in a 3 kHz bandwidth, and mostly at -6 dB.
- **Start** begins a picture now in a mode you pick, for when its header was missed (QRM, or you tuned in part way). The line timing is found from the sync pulses.
- **The picture as it arrives**, line by line, scaled to fit the pane.
- **Weak signals**: the line sync is found from the 1200 Hz tone's share of the signal (narrowband), so pictures keep their lines straight down to about 0 dB SNR (noisy, but recognisable). Only the SSTV band (1000 to 2500 Hz, sharply filtered) reaches the picture decoding, so a strong station or carrier elsewhere in the passband doesn't turn the picture to noise. A confirmed picture runs to its end through fades; a header followed by no picture is dropped rather than drawn as noise, and a new header part way through a picture starts the new one.
- **Slant correction**: the sender's sound-card clock is measured from the sync pulses and the picture is redrawn straight when it ends.
- **Off-tune signals** (up to about 150 Hz) are measured from the VIS leader and allowed for.
- **Who sent it**: the callsign from MMSSTV's FSK ID, when the sender has it turned on (most do).
- **Gallery** along the bottom, newest first. Click a picture to look at it, click the big picture to go back to live, and right-click one to open, copy, show in its folder or delete it (to the Recycle Bin). Pictures are saved as PNG in the module's data folder (**Pictures** opens it), each with a `.json` of its details: mode, callsign, time, frequency, tuning and slant.
- **What's being heard**, even before a picture starts: the details column has a scrolling waterfall (the last 40 s) and a spectrum of 900 to 2500 Hz, with the sync (1200 Hz), black (1500 Hz), leader (1900 Hz) and white (2300 Hz) tones marked, and an input level meter (says "no audio", "very low" or "clipping" when it should). The panadapter shows the same tones as lines from the VFO.
- **Band menu**: tunes to the usual SSTV frequencies (3.845 and 7.171 LSB; 14.230, 14.233, 21.340, 28.680 and 50.950 USB) and sets DIGL or DIGU. **2 m ISS** (145.800 FM) is there for the ISS's SSTV events (PD-120 / PD-180), with a 2 m transverter or a VHF radio.
- **Options > Save the last 3 minutes of audio** writes a WAV of what the module heard, for a signal that didn't decode. The self-test decodes one with `Kainos.Sstv.SelfTest.exe --wav <file>` and says what it found and why it turned down any header it nearly took. Those near misses are also in the module's log and, briefly, in the status line.
- **Skip** ends a picture early. Pictures cut short (signal lost, or Skip) are kept if a quarter or more came in (Options).
- At 125 % or 150 % display scaling, the picture is drawn at the screen's own resolution (Options, "Sharp picture").

## Installing

In Kainos: **Setup > Modules**, choose **SSTV (slow-scan TV)** and **Install**. Then open it from **Modes > SSTV** (or the menu bar in Classic layout).

To test a build before it's released, use **Install from file...** with the zip from `build.ps1`.

One digital mode is open at a time: SSTV, FT8 / FT4 (the Digital Modes module) and the RTTY and CW terminals share the receiver's audio. Opening one closes whichever had it.

## Logging

- **Log** (on the send bar) opens Log QSO filled in: their callsign, the reports, start (the first picture to or from them) and end, frequency, picture mode and your power. It also opens by itself when your 73 has gone out (Options to turn off).
- QSOs go in `kainos-sstv.adi` (Options > Open the log folder) as ADIF mode SSTV, with the picture mode in the comment, and are sent to your logging program as WSJT-X sends them (UDP "Logged ADIF" to 127.0.0.1:2237; Options > Logging programs address...). Log4OM, N1MM+, HRD, DXKeeper (via JTAlert) and GridTracker take them.
- **Worked before**: when a picture arrives from a station in the log, the details column shows how many QSOs and the last date.

## How it works

| Part | What it does |
|---|---|
| `SstvModes.cs` | The mode table: VIS codes, sync, porch and gap times, pixel times and line layouts. |
| `SstvDecoder.cs` | Resamples to 24 kHz and finds the instantaneous frequency of every sample (mixed down from 1900 Hz, low-passed, phase difference). Finds the VIS header in 10 ms steps, then decodes each line once it is all in. It fits a straight line through the sync pulses for slant, redraws the whole picture at the end, then reads the FSK ID. |
| `SstvEncoder.cs` | A picture to SSTV audio: VIS, lines and FSK ID. Used by the self-test now, and for sending next. |
| `SstvEngine.cs` | The thread that takes the receiver's audio from Kainos and feeds the decoder, plus the tuning spectrum. |
| `Gallery.cs` | Saving and listing received pictures. |
| `SstvLog.cs` | The ADIF log, worked before, and the UDP message to logging programs. |
| `Station.cs`, `TemplateRenderer.cs` | The station's details, the picture templates (DataFolder	emplates.json) and drawing them. |
| `SstvEngine.cs` (transmit) | Sending: the audio fed to Kainos's transmit tap with MOX, as the Digital Modes module does. |
| `UI/` | The pane, drawn in the Kainos colours. |

## Building

You need Visual Studio 2022 or later (.NET desktop development) and a Kainos build (for `Kainos.ModuleApi.dll`).

```
.\build.ps1
```

The self-test encodes a test picture in every mode and decodes it. It also decodes the modes used most with noise, a 70 Hz tuning error and a sender's clock 500 ppm fast, and checks the FSK ID. It runs the module's window with two pictures played to it. With `-PySstv <folder>` it also decodes pictures made by PySSTV, an independent encoder.

## Licence

GPL version 3 or later (see LICENSE). Copyright (C) 2026 Justin Cron K7JUS.
