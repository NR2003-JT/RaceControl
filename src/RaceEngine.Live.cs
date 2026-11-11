using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RaceControl
{
    // What the live page shows: the race state as JSON, built on the window's thread (it reads game memory).
    public partial class RaceEngine
    {
        static string Phase(int f)
        {
            switch (f)
            {
                case 7: return "Green";
                case 5: return "One to go";
                case 6: return "Double file - green next";
                case 4: return "Caution (delayed green)";
                default: return f >= 0 && f <= 3 ? "Caution" : "-";
            }
        }

        public string LiveJson(IEnumerable<string> logTail, string preset)
        {
            var j = new Json();
            j.Begin();
            j.Prop("time", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            j.Prop("inRace", InRace);
            j.Prop("track", TrackName ?? "");
            j.Prop("preset", preset ?? "");
            if (!InRace) { j.Prop("log", logTail); j.End(); return j.ToString(); }
            j.Prop("lap", lap);
            j.Prop("flag", flag);
            j.Prop("phase", Phase(flag));
            bool caution = flag >= 0 && flag <= 6;
            j.Prop("caution", caution);
            j.Prop("pits", !caution ? "open (green)" : pitsOpen ? (OpenPitsToAll ? "open to all cars" : "open") : publishedClosed ? "closed by Race Control" : "closed");

            Car leader = null, first;
            List<Car> cars = Field(out leader, out first);
            var waved = new HashSet<int>(eligible.Keys);
            j.Key("order"); j.BeginArray();
            if (cars != null)
            {
                // running order by progress, pit road included
                var all = new List<Tuple<int, double, bool>>();
                double trackLen = m.F64(Addr.TrackLength);
                for (int idx = 0; idx <= maxIdx; idx++)
                    if (idx != Addr.PaceCarIdx && Running(idx)) all.Add(Tuple.Create(idx, LapsOf(idx) + DlongOf(idx) / trackLen, OnPitRoad(idx)));
                double lead = all.Count > 0 ? all.Max(t => t.Item2) : 0;
                int pos = 0;
                foreach (var t in all.OrderByDescending(t => t.Item2))
                {
                    string d;
                    decided.TryGetValue(t.Item1, out d);
                    j.Begin();
                    j.Prop("pos", ++pos);
                    j.Prop("num", CarNumber(t.Item1));
                    j.Prop("down", Math.Floor(lead - t.Item2 + 0.0001));
                    j.Prop("pit", t.Item3);
                    j.Prop("waved", waved.Contains(t.Item1));
                    j.Prop("player", t.Item1 == Addr.PlayerIdx);
                    j.Prop("firstTimeBy", d ?? "");
                    j.Prop("penalty", HasPenalty(t.Item1));
                    j.End();
                }
            }
            j.EndArray();

            Func<string, List<int>> group = g => decided.Where(kv => kv.Value == g).OrderBy(kv => kv.Key).Select(kv => CarNumber(kv.Key)).ToList();
            j.Key("firstTimeBy"); j.Begin();
            j.Prop("waved", group("waved")); j.Prop("around", group("around")); j.Prop("penalty", group("penalty"));
            j.Prop("pitted", group("pitted")); j.Prop("behind", group("behind")); j.Prop("lead", group("lead"));
            j.End();

            j.Key("line"); j.Begin();
            j.Prop("active", planActive);
            j.Prop("single", lineup != null && inside == null ? lineup.Select(CarNumber).ToList() : new List<int>());
            j.Prop("inside", inside != null ? inside.Select(CarNumber).ToList() : new List<int>());
            j.Prop("outside", outside != null ? outside.Select(CarNumber).ToList() : new List<int>());
            j.Prop("coneOpen", coneRunning);
            j.Prop("coneWaiting", coneWaiting);
            j.End();

            j.Key("cone");
            if (ConeBoard == null) j.Raw("null");
            else
            {
                j.Begin();
                j.Prop("title", ConeBoard.Title ?? "");
                j.Prop("done", ConeBoard.Done);
                foreach (var lane in new[] { Tuple.Create("inside", ConeBoard.Inside), Tuple.Create("outside", ConeBoard.Outside) })
                {
                    j.Key(lane.Item1); j.BeginArray();
                    foreach (ConeEntry e in lane.Item2.ToList())
                    {
                        j.Begin(); j.Prop("num", e.Num); j.Prop("row", e.Row); j.Prop("from", e.From); j.Prop("to", e.To); j.Prop("gained", e.Gained); j.End();
                    }
                    j.EndArray();
                }
                j.End();
            }
            j.Prop("log", logTail);
            j.End();
            return j.ToString();
        }
    }

    // Just enough JSON writing for the live page (C# 5, no serializer needed).
    class Json
    {
        readonly StringBuilder sb = new StringBuilder();
        bool comma;

        void Sep() { if (comma) sb.Append(','); comma = false; }
        public void Begin() { Sep(); sb.Append('{'); }
        public void End() { sb.Append('}'); comma = true; }
        public void BeginArray() { Sep(); sb.Append('['); }
        public void EndArray() { sb.Append(']'); comma = true; }
        public void Key(string k) { Sep(); Str(k); sb.Append(':'); }
        public void Raw(string v) { Sep(); sb.Append(v); comma = true; }

        void Str(string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        public void Prop(string k, string v) { Key(k); Str(v); comma = true; }
        public void Prop(string k, bool v) { Key(k); sb.Append(v ? "true" : "false"); comma = true; }
        public void Prop(string k, double v) { Key(k); sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture)); comma = true; }
        public void Prop(string k, IEnumerable<int> v) { Key(k); sb.Append('[').Append(string.Join(",", v.Select(x => x.ToString(CultureInfo.InvariantCulture)))).Append(']'); comma = true; }
        public void Prop(string k, IEnumerable<string> v)
        {
            Key(k); sb.Append('[');
            bool f = true;
            foreach (string s in v) { if (!f) sb.Append(','); f = false; Str(s); }
            sb.Append(']'); comma = true;
        }

        public override string ToString() { return sb.ToString(); }
    }
}
