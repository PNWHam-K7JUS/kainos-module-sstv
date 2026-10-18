/*  Station.cs

The station's details (asked once, by the first-run setup) and the picture templates made from them: CQ, Reply and
73 to start with, editable, and as many more as wanted. A template is a few lines of text with placeholders over a
background (the station's photo, a gradient, or the picture just received):

  {MYCALL} {NAME} {QTH} {GRID} {RIG} {ANT} {PWR}   the station's own
  {CALL} {RST}                                     the other station's callsign and the report sent
  {DATE} {TIME} {MODE}                             UTC date and time, and the mode being sent

Templates are kept in DataFolder\templates.json, the photo as DataFolder\photo.<ext>.

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Kainos.Modules;

namespace Kainos.Sstv
{
    internal class StationProfile
    {
        public string Call = "", Name = "", Qth = "", Grid = "", Rig = "", Ant = "", Pwr = "";
        public string Photo = "";           // a file in the data folder, or ""
        public string Mode = "S1";          // the mode CQs go out in (SstvMode.Short)
        public bool FskId = true;           // send the callsign after each picture (MMSSTV's FSK ID)
        public bool Done;                   // the first-run setup has been through

        public static StationProfile Load(IKainosHost h)
        {
            Func<string, string, string> g = (k, d) => h.GetSetting("st." + k, d);
            return new StationProfile
            {
                Call = g("call", h.Callsign ?? ""), Name = g("name", ""), Qth = g("qth", ""), Grid = g("grid", h.Grid ?? ""),
                Rig = g("rig", ""), Ant = g("ant", ""), Pwr = g("pwr", ""), Photo = g("photo", ""), Mode = g("mode", "S1"),
                FskId = g("fskid", "1") == "1", Done = g("done", "0") == "1",
            };
        }

        public void Save(IKainosHost h)
        {
            Action<string, string> s = (k, v) => h.SetSetting("st." + k, v ?? "");
            s("call", Call.Trim().ToUpperInvariant()); s("name", Name.Trim()); s("qth", Qth.Trim()); s("grid", Grid.Trim());
            s("rig", Rig.Trim()); s("ant", Ant.Trim()); s("pwr", Pwr.Trim()); s("photo", Photo); s("mode", Mode);
            s("fskid", FskId ? "1" : "0"); s("done", Done ? "1" : "0");
        }

        public string PhotoPath(IKainosHost h) { return string.IsNullOrEmpty(Photo) ? null : Path.Combine(h.DataFolder, Photo); }

        public SstvMode CqMode { get { return SstvModes.ByShort(Mode) ?? SstvModes.ByShort("S1"); } }
    }

    // what fills a template's placeholders when it's drawn
    internal class TemplateFields
    {
        public string Call = "", Rst = "595";
        public SstvMode Mode;
        public DateTime Utc = DateTime.UtcNow;
    }

    [DataContract]
    internal class SstvTemplate
    {
        [DataMember(Order = 1)] public string Name = "";
        [DataMember(Order = 2)] public string Kind = "custom";      // cq, reply, 73, custom
        [DataMember(Order = 3)] public string Title = "";           // the band across the top
        [DataMember(Order = 4)] public string Big = "";             // large in the middle
        [DataMember(Order = 5)] public string Lines = "";           // smaller, along the bottom (up to 3 lines)
        [DataMember(Order = 6)] public string Background = "photo"; // photo, gradient, received
        [DataMember(Order = 7)] public string Accent = "#D4AD6A";   // the title's colour
        [DataMember(Order = 8)] public bool Inset;                  // the received picture, small, on the right

        public SstvTemplate Clone() { return (SstvTemplate)MemberwiseClone(); }

        public static string Fill(string text, StationProfile st, TemplateFields f)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder b = new StringBuilder(text);
            Action<string, string> r = (k, v) => b.Replace("{" + k + "}", v ?? "");
            r("MYCALL", st.Call); r("NAME", st.Name); r("QTH", st.Qth); r("GRID", st.Grid);
            r("RIG", st.Rig); r("ANT", st.Ant); r("PWR", st.Pwr);
            r("CALL", f.Call); r("RST", f.Rst);
            r("DATE", f.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); r("TIME", f.Utc.ToString("HH:mm", CultureInfo.InvariantCulture));
            r("MODE", f.Mode != null ? f.Mode.Name : "");
            // tidy what empty fields leave behind: "  ·  ·  " and the like
            string s = b.ToString();
            string[] lines = s.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string l = System.Text.RegularExpressions.Regex.Replace(lines[i], @"(\s*·\s*)+", "  ·  ");
                l = System.Text.RegularExpressions.Regex.Replace(l, @"^\s*·\s*|\s*·\s*$", "");
                lines[i] = System.Text.RegularExpressions.Regex.Replace(l, @"\s{3,}", "  ").Trim();
            }
            return string.Join("\n", lines.Where(l => l.Length > 0));
        }

        public const string Placeholders = "{MYCALL} {NAME} {QTH} {GRID} {RIG} {ANT} {PWR}   {CALL} {RST}   {DATE} {TIME} {MODE}";
    }

    internal static class TemplateStore
    {
        public static List<SstvTemplate> Defaults()
        {
            return new List<SstvTemplate>
            {
                new SstvTemplate { Name = "CQ", Kind = "cq", Title = "CQ CQ CQ", Big = "{MYCALL}",
                    Lines = "{NAME}  ·  {QTH}  ·  {GRID}\n{RIG}  ·  {ANT}  ·  {PWR}\nPSE K", Background = "photo", Accent = "#D4AD6A" },
                new SstvTemplate { Name = "Reply", Kind = "reply", Title = "{CALL} de {MYCALL}", Big = "UR {RST}",
                    Lines = "TNX FER PIC!  OP {NAME}  ·  {QTH}  ·  {GRID}\n{RIG}  ·  {ANT}  ·  {PWR}\nBTU {CALL}", Background = "photo", Accent = "#7FB0CC", Inset = true },
                new SstvTemplate { Name = "73", Kind = "73", Title = "TNX QSO {CALL}", Big = "73",
                    Lines = "de {MYCALL}  ·  {NAME}\n{DATE}  {TIME}Z  ·  {MODE}", Background = "photo", Accent = "#B5A8E0", Inset = true },
            };
        }

        public static string FileOf(IKainosHost h) { return Path.Combine(h.DataFolder, "templates.json"); }

        public static List<SstvTemplate> Load(IKainosHost h)
        {
            try
            {
                string f = FileOf(h);
                if (File.Exists(f))
                    using (FileStream s = File.OpenRead(f))
                    {
                        List<SstvTemplate> l = (List<SstvTemplate>)new DataContractJsonSerializer(typeof(List<SstvTemplate>)).ReadObject(s);
                        if (l != null && l.Count > 0) return l;
                    }
            }
            catch (Exception ex) { h.Log("templates: " + ex.Message); }
            return Defaults();
        }

        public static void Save(IKainosHost h, List<SstvTemplate> list)
        {
            Directory.CreateDirectory(h.DataFolder);
            string f = FileOf(h), tmp = f + ".tmp";
            using (FileStream s = File.Create(tmp))
            using (XmlJsonWriter w = new XmlJsonWriter(s))
                new DataContractJsonSerializer(typeof(List<SstvTemplate>)).WriteObject(w.Writer, list);
            if (File.Exists(f)) File.Delete(f);
            File.Move(tmp, f);
        }

        // an indented JSON writer (DataContractJsonSerializer writes it all on one line otherwise)
        private sealed class XmlJsonWriter : IDisposable
        {
            public readonly System.Xml.XmlDictionaryWriter Writer;
            public XmlJsonWriter(Stream s) { Writer = JsonReaderWriterFactory.CreateJsonWriter(s, Encoding.UTF8, true, true, "  "); }
            public void Dispose() { Writer.Flush(); Writer.Dispose(); }
        }
    }
}
