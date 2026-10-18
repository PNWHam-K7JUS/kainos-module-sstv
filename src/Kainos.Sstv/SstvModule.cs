/*  SstvModule.cs

Kainos SSTV: slow-scan TV inside Kainos, as a downloadable module (Setup > Modules). Receives Martin, Scottie,
Robot 36 and PD pictures (the mode found from the VIS header), corrects slant, reads MMSSTV's FSK ID callsign and
keeps a gallery. Sending, with CQ and reply templates made from a first-run setup, comes next.

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

Copyright (C) 2026 Justin Cron K7JUS
*/

using Kainos.Modules;
using Kainos.Sstv.UI;

namespace Kainos.Sstv
{
    public class SstvModule : IKainosModule
    {
        private IKainosHost _host;
        private SstvEngine _engine;
        private SstvPane _view;
        private IKainosPane _pane;
        private IKainosMenuItem _menu;

        public void Start(IKainosHost host)
        {
            _host = host;
            _engine = new SstvEngine(host);
            _view = new SstvPane(host, _engine);
            _pane = host.CreatePane("Kainos SSTV", _view);
            _pane.Closed += (s, e) => close();
            _view.PopToggled += () => _pane.Popped = !_pane.Popped;
            _menu = host.AddMenuItem("SSTV", "Slow-scan TV (the SSTV module): open / close.", () => { if (_pane.IsOpen) close(); else open(); });
            host.Log("SSTV " + ModuleVersion + " started");
            if (host.GetSetting("open", "0") == "1") open();
        }

        internal static string ModuleVersion { get { System.Version v = typeof(SstvModule).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; } }

        private void open()
        {
            _pane.Open();
            _engine.Listen();
            _engine.Start();
            _menu.Checked = true;
            _host.SetSetting("open", "1");
        }

        private void close()
        {
            _engine.Stop();
            _pane.Close();
            _view.ClearMarkers();
            _menu.Checked = false;
            _host.SetSetting("open", "0");
        }

        public void Stop()
        {
            if (_engine != null) _engine.Stop();
        }

        public void Dispose()
        {
            if (_view != null && !_view.IsDisposed) _view.Dispose();
        }
    }
}
