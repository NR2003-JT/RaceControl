using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace RaceControl
{
    // NR2003 Companions Hub (MODS\Hub, see its CONTRACT.md): NR2003's own telemetry, or a companion overlay app's feed,
    // as files in %PROGRAMDATA%\NR2003 Companions\hub\. Race Control reads live.json (the player's state) and follows
    // events.log (session changes, the player's laps and stops) - only while the hub is running and the file is fresh;
    // otherwise everything is read from game memory as before. Off unless MainForm turns it on (the offline sims never
    // read the real folder).
    public class HubFeed
    {
        public static readonly HubFeed Current = new HubFeed();

        public bool Enabled;
        public Action<string> Logger;
        public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NR2003 Companions", "hub");
        const double StaleMs = 3000;

        // what the hub says now (valid while Fresh)
        public bool Fresh { get; private set; }
        public string Source = "";               // direct | companion
        public bool SimActive, InCar, Paused;
        public string SessionType = "", SessionState = "", Flag = "";
        public int Cookie = -1, LapsLeft = -1;
        public bool HasPlayer;                   // the player's car block (only while in the car)
        public double PlayerSpeed, PlayerTrkPct;

        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        DateTime liveStamp = DateTime.MinValue;
        long eventsPos;
        DateTime eventsStamp = DateTime.MinValue;
        bool wasFresh;

        public void Tick()
        {
            if (!Enabled) { Fresh = false; return; }
            ReadLive();
            ReadEvents();
            if (Fresh != wasFresh)
            {
                wasFresh = Fresh;
                Log(Fresh ? "hub: connected (" + (Source == "" ? "?" : Source) + ") - the player's state comes from NR2003's telemetry"
                          : "hub: not running (or no game) - the player's state is read from game memory");
            }
        }

        void ReadLive()
        {
            string path = Path.Combine(Dir, "live.json");
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp != liveStamp)
                {
                    liveStamp = stamp;
                    Update(File.ReadAllText(path));
                }
                double age = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds - writtenMs;
                Fresh = writtenMs > 0 && age < StaleMs && age > -StaleMs && SimActive;
            }
            catch (Exception) { Fresh = false; }
        }

        double writtenMs;

        // One live.json (public for the offline checks).
        public void Update(string text)
        {
            var d = json.DeserializeObject(text) as Dictionary<string, object>;
            if (d == null) return;
            writtenMs = Num(d, "written_ms", 0);
            Source = Str(d, "source");
            SimActive = Bool(d, "sim_active");
            InCar = Bool(d, "in_car");
            Paused = Bool(d, "paused");
            var s = Obj(d, "session");
            if (s != null)
            {
                SessionType = Str(s, "type"); SessionState = Str(s, "state"); Flag = Str(s, "flag");
                Cookie = (int)Num(s, "cookie", -1); LapsLeft = (int)Num(s, "laps_left", -1);
            }
            var p = Obj(d, "player");
            HasPlayer = p != null;
            if (p != null) { PlayerSpeed = Num(p, "speed_ms", 0); PlayerTrkPct = Num(p, "trk_pct", 0); }
        }

        // events.log: logs the session changes and the player's (car 0) laps and stops as the game reported them
        void ReadEvents()
        {
            string path = Path.Combine(Dir, "events.log");
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                if (fi.CreationTimeUtc != eventsStamp) { eventsStamp = fi.CreationTimeUtc; eventsPos = fi.Length; return; }   // a new hub run: from now on
                if (fi.Length < eventsPos) eventsPos = 0;
                if (fi.Length == eventsPos) return;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(eventsPos, SeekOrigin.Begin);
                    var rd = new StreamReader(fs);
                    string rest = rd.ReadToEnd();
                    int last = rest.LastIndexOf('\n');
                    if (last < 0) return;                                // a line still being written
                    eventsPos += System.Text.Encoding.UTF8.GetByteCount(rest.Substring(0, last + 1));
                    foreach (string line in rest.Substring(0, last).Split('\n')) Event(line.TrimEnd('\r'));
                }
            }
            catch (Exception) { }
        }

        void Event(string line)
        {
            string[] f = line.Split('\t');
            if (f.Length < 2) return;
            var kv = new Dictionary<string, string>();
            for (int i = 2; i < f.Length; i++) { int e = f[i].IndexOf('='); if (e > 0) kv[f[i].Substring(0, e)] = f[i].Substring(e + 1); }
            string car; kv.TryGetValue("car", out car);
            switch (f[1])
            {
                case "SESSION":
                    Log(string.Format("hub: session {0}, {1}, flag {2}, {3} laps left{4}", Get(kv, "type"), Get(kv, "state"), Get(kv, "flag"),
                        Get(kv, "laps_left"), Get(kv, "new") == "1" ? " (new session)" : ""));
                    break;
                case "LAP":
                    if (car == "0") Log(string.Format("hub: your lap {0} at {1} s, flags {2}", Get(kv, "lap"), Get(kv, "at"), Get(kv, "flags")));
                    break;
                case "PITSTOP":
                    if (car == "0") Log("hub: your pit stop" + (kv.ContainsKey("tires") ? " - tires " + kv["tires"] + ", fuel " + Get(kv, "fuel_gal") + " gal" : ""));
                    break;
                case "INCAR":
                    Log("hub: you " + (Get(kv, "on") == "1" ? "got in the car" : "got out of the car"));
                    break;
            }
        }

        void Log(string s) { if (Logger != null) Logger(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + s); }
        static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : "?"; }
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
        static double Num(Dictionary<string, object> d, string k, double def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch (Exception) { return def; }
        }
    }
}
