using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RaceControl
{
    // A drawing of the track: 500 world x/y points at even steps of lap distance (dlong, metres past the stripe), on the
    // track's centre line. Shared by the companion apps: %PROGRAMDATA%\NR2003 Companions\tracks\<track>\map.json
    //   { "track", "length_m", "source", "made", "points": [[x, y, dlong], ...] }
    // Recorded from the AI cars in any session (TrackMapRecorder) - nobody has to drive a lap. Until then a map another
    // overlay app recorded (AppData\<app>\tracks\<track>\trackmap.json, [x, y, lap fraction], same world frame -
    // checked against live cars at Michigan 2026-10-29) stands in.
    public class TrackMap
    {
        public const int N = 500;
        public string Track, Source = "", Made = "";
        public double Length;
        public readonly float[] X = new float[N], Y = new float[N];

        public static string Folder = Path.Combine(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NR2003 Companions"), "tracks");
        public const string Recorded = "recorded", Imported = "imported";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static double Mod(double a, double b) { double r = a % b; return r < 0 ? r + b : r; }

        public static string PathFor(string track)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) track = track.Replace(c, '_');
            return Path.Combine(Path.Combine(Folder, track), "map.json");
        }

        public double DlongOf(int i) { return i * Length / N; }

        // Takes out the last metre or two of wobble: a quadratic fitted through the points within SmoothM metres either
        // side (Savitzky-Golay), so corners keep their shape. In metres, not points: on a short track the points are
        // close together (1.7 m at Bristol, 6.4 m at Michigan) and the same noise made it look squiggly.
        public const double SmoothM = 12;
        public void Smooth()
        {
            int h = (int)Math.Round(SmoothM / (Length / N));
            if (h < 2) h = 2;
            if (h > 40) h = 40;
            double norm = (2 * h + 1) * (4.0 * h * h + 4 * h - 3);
            var w = new double[2 * h + 1];
            for (int j = -h; j <= h; j++) w[j + h] = (3 * (3.0 * h * h + 3 * h - 1) - 15.0 * j * j) / norm;
            float[] x0 = (float[])X.Clone(), y0 = (float[])Y.Clone();
            for (int i = 0; i < N; i++)
            {
                double sx = 0, sy = 0;
                for (int j = -h; j <= h; j++) { int k = ((i + j) % N + N) % N; sx += w[j + h] * x0[k]; sy += w[j + h] * y0[k]; }
                X[i] = (float)sx; Y[i] = (float)sy;
            }
        }

        // the point on the centre line at a lap distance
        public void At(double dlong, out double x, out double y)
        {
            double f = Mod(dlong, Length) / Length * N;
            int i = (int)Math.Floor(f) % N, j = (i + 1) % N;
            double t = f - Math.Floor(f);
            x = X[i] + (X[j] - X[i]) * t;
            y = Y[i] + (Y[j] - Y[i]) * t;
        }

        // the direction of travel at a lap distance (unit vector)
        public void Heading(double dlong, out double tx, out double ty)
        {
            double step = Length / N, ax, ay, bx, by;
            At(dlong - step, out ax, out ay);
            At(dlong + step, out bx, out by);
            tx = bx - ax; ty = by - ay;
            double l = Math.Sqrt(tx * tx + ty * ty);
            if (l < 1e-6) { tx = 1; ty = 0; } else { tx /= l; ty /= l; }
        }

        // a point across the track: + dlat = to the left of the direction of travel (the infield on an oval run
        // anticlockwise, as the cars' own dlat and the TV cameras' "cl" count it)
        public void AtDlat(double dlong, double dlat, out double x, out double y)
        {
            double tx, ty;
            At(dlong, out x, out y);
            Heading(dlong, out tx, out ty);
            x += -ty * dlat; y += tx * dlat;
        }

        // the lap distance of the centre line point nearest to (x, y); dist = how far away that is
        public double Nearest(double x, double y, out double dist)
        {
            double best = double.MaxValue, at = 0;
            for (int i = 0; i < N; i++)
            {
                int j = (i + 1) % N;
                double sx = X[j] - X[i], sy = Y[j] - Y[i], l2 = sx * sx + sy * sy;
                double t = l2 > 1e-9 ? ((x - X[i]) * sx + (y - Y[i]) * sy) / l2 : 0;
                t = Math.Max(0, Math.Min(1, t));
                double px = X[i] + sx * t - x, py = Y[i] + sy * t - y, d = px * px + py * py;
                if (d < best) { best = d; at = (i + t) * Length / N; }
            }
            dist = Math.Sqrt(best);
            return Mod(at, Length);
        }

        // ---- files ----

        public void Save()
        {
            string path = PathFor(Track);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.Append("{\"track\": \"").Append(Track.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\n");
            sb.Append(" \"length_m\": ").Append(Length.ToString("0.###", Inv)).Append(",\n");
            sb.Append(" \"source\": \"").Append(Source).Append("\",\n");
            sb.Append(" \"made\": \"").Append(Made).Append("\",\n");
            sb.Append(" \"points\": [");
            for (int i = 0; i < N; i++)
                sb.Append(i == 0 ? "" : ",").Append(i % 4 == 0 ? "\n  " : " ").Append('[').Append(X[i].ToString("0.###", Inv)).Append(", ")
                  .Append(Y[i].ToString("0.###", Inv)).Append(", ").Append(DlongOf(i).ToString("0.##", Inv)).Append(']');
            sb.Append("\n]}\n");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static Dictionary<string, object> ReadJson(string path)
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 << 20 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        }

        // our own map for the track, or null
        public static TrackMap Load(string track)
        {
            try
            {
                string path = PathFor(track);
                if (!File.Exists(path)) return null;
                var d = ReadJson(path);
                var pts = (ArrayList)d["points"];
                if (pts.Count != N) return null;
                var map = new TrackMap { Track = track, Length = Convert.ToDouble(d["length_m"], Inv) };
                object o;
                if (d.TryGetValue("source", out o) && o != null) map.Source = o.ToString();
                if (d.TryGetValue("made", out o) && o != null) map.Made = o.ToString();
                for (int i = 0; i < N; i++)
                {
                    var p = (ArrayList)pts[i];
                    map.X[i] = (float)Convert.ToDouble(p[0], Inv);
                    map.Y[i] = (float)Convert.ToDouble(p[1], Inv);
                }
                if (map.Length <= 100) return null;
                map.Smooth();                       // maps saved before the smoothing came in (short tracks)
                return map;
            }
            catch (Exception) { return null; }
        }

        // A map another overlay app keeps for this track (AppData\<app>\tracks\<track>\trackmap.json: [x, y, lap
        // fraction] from a lap it saw driven), resampled to our 500 points. Null when there is none.
        public static TrackMap Import(string track, double length, out string from)
        {
            from = null;
            if (length <= 100) return null;
            string[] apps;
            try { apps = Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)); }
            catch (Exception) { return null; }
            foreach (string app in apps)
            {
                string file = Path.Combine(Path.Combine(Path.Combine(app, "tracks"), track), "trackmap.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var pts = (ArrayList)ReadJson(file)["points"];
                    var src = new List<double[]>();
                    foreach (object p in pts)
                    {
                        var a = (ArrayList)p;
                        if (a.Count < 3) continue;
                        double f = Convert.ToDouble(a[2], Inv);
                        if (f >= 0 && f < 1) src.Add(new[] { f, Convert.ToDouble(a[0], Inv), Convert.ToDouble(a[1], Inv) });
                    }
                    if (src.Count < 50) continue;
                    var map = new TrackMap { Track = track, Length = length, Source = Imported, Made = File.GetLastWriteTime(file).ToString("yyyy-MM-dd", Inv) };
                    if (!Resample(src, 1.0, map)) continue;
                    from = file;
                    return map;
                }
                catch (Exception) { }
            }
            return null;
        }

        // points {position along the lap (0..period), x, y}, in any order -> the map's N even steps (wrapping round)
        internal static bool Resample(List<double[]> src, double period, TrackMap map)
        {
            src = src.OrderBy(p => p[0]).ToList();
            int n = src.Count;
            if (n < 3) return false;
            int k = 0;
            for (int i = 0; i < N; i++)
            {
                double at = period * i / N;
                while (k < n && src[k][0] <= at) k++;
                double[] a = src[(k - 1 + n) % n], b = src[k % n];
                double pa = a[0] - (k == 0 ? period : 0), pb = b[0] + (k == n ? period : 0);
                double t = pb - pa > 1e-12 ? (at - pa) / (pb - pa) : 0;
                map.X[i] = (float)(a[1] + (b[1] - a[1]) * t);
                map.Y[i] = (float)(a[2] + (b[2] - a[2]) * t);
            }
            return true;
        }
    }

    // Builds the map from every car on track (not the pace car, not on pit road) as it drives by: each lap distance
    // bin keeps a few positions with the car's dlat, and once the whole lap is covered the positions are moved back
    // onto the centre line (position - dlat across) and the middle of each bin taken. The first green lap of a race,
    // or a minute or so of practice with the AI out, covers it.
    public class TrackMapRecorder
    {
        class S { public float X, Y, Lat, D; }
        const int Keep = 12, Need = 3, MaxCars = 64;
        const double ClearOfPits = 300;   // metres a car drives off pit road before it counts (pit exit lanes, out laps)
        const long Block0 = 0xACCA30;
        const int PosX = 6622;
        readonly List<S>[] bins = new List<S>[TrackMap.N];
        readonly float[] lastD = new float[MaxCars];
        readonly double[] clear = new double[MaxCars];   // metres driven since it was last on pit road
        readonly HashSet<int> cars = new HashSet<int>();
        public string Track { get; private set; }
        public double Length { get; private set; }
        public bool Active { get; private set; }
        public DateTime Started { get; private set; }
        public string Note = "";          // how the centre line came out (for the log)

        public TrackMapRecorder() { for (int i = 0; i < bins.Length; i++) bins[i] = new List<S>(); }

        public void Start(string track, double length)
        {
            Track = track; Length = length; Active = length > 100;
            foreach (var b in bins) b.Clear();
            for (int i = 0; i < MaxCars; i++) { lastD[i] = float.NaN; clear[i] = ClearOfPits; }
            cars.Clear();
            Started = DateTime.Now;
        }
        public void Stop() { Active = false; }

        public int Covered { get { return bins.Count(b => b.Count >= Need); } }
        public int Cars { get { return cars.Count; } }

        static double Mod(double a, double b) { double r = a % b; return r < 0 ? r + b : r; }

        // one look at the cars; returns the finished map once the lap is covered (then stops)
        public TrackMap Sample(IGameMemory m, int maxIdx)
        {
            if (!Active) return null;
            int n = TrackMap.N;
            for (int idx = 0; idx <= Math.Min(maxIdx, MaxCars - 1); idx++)
            {
                if (idx == Addr.PaceCarIdx) continue;
                if (m.U8(Addr.HasNotRetired + idx) != 1 || m.U8(Addr.HasSpawned + idx) != 1 || m.U8(Addr.OnPitRoad + idx) == 1)
                { lastD[idx] = float.NaN; clear[idx] = 0; continue; }
                float d = m.F32(Addr.Dlong + idx * Addr.Ocd), lat = m.F32(Addr.Dlat + idx * Addr.Ocd);
                long b = Block0 + idx * Addr.Ocd;
                float x = m.F32(b + PosX), y = m.F32(b + PosX + 4);
                // the four values come from separate reads: if the game moved the car in between (time acceleration
                // makes that common), they don't belong together - Bristol came out jagged in the turns that way
                if (m.F32(Addr.Dlong + idx * Addr.Ocd) != d || m.F32(b + PosX) != x) continue;
                if (!(d >= 0 && d < Length) || !(Math.Abs(lat) < 40) || float.IsNaN(x) || float.IsNaN(y)) { lastD[idx] = float.NaN; continue; }
                float prev = lastD[idx];
                lastD[idx] = d;
                if (float.IsNaN(prev)) continue;
                double moved = Mod(d - prev, Length);
                if (moved < 0.3 || moved > 150) continue;          // standing, backing up, or a jump (tow, reset)
                clear[idx] += moved;
                if (clear[idx] < ClearOfPits) continue;            // just off pit road: still on the pit exit lane
                var bin = bins[Math.Min(n - 1, (int)(d / Length * n))];
                if (bin.Count >= Keep) continue;
                bin.Add(new S { X = x, Y = y, Lat = lat, D = d });
                cars.Add(idx);
            }
            if (!Ready()) return null;
            Active = false;
            return Build();
        }

        // every bin has its samples, except short gaps (up to 2 bins) that are filled in between their neighbours
        bool Ready()
        {
            int n = TrackMap.N, have = 0, run = 0, worst = 0, first = -1;
            for (int i = 0; i < n; i++) if (bins[i].Count >= Need) { have++; if (first < 0) first = i; }
            if (have < n - 10 || first < 0) return false;
            for (int k = 0; k < n; k++)
            {
                if (bins[(first + k) % n].Count >= Need) run = 0;
                else { run++; worst = Math.Max(worst, run); }
            }
            return worst <= 2;
        }

        static float Median(List<float> v) { v.Sort(); int c = v.Count; return c == 0 ? float.NaN : c % 2 == 1 ? v[c / 2] : (v[c / 2 - 1] + v[c / 2]) / 2; }

        TrackMap Build()
        {
            int n = TrackMap.N;
            // 1. the middle of where the cars ran in each bin (the racing line, roughly) - for the track's direction
            var raw = new List<double[]>();
            foreach (var b in bins)
                if (b.Count > 0) raw.Add(new double[] { b.Average(s => s.D), Median(b.Select(s => s.X).ToList()), Median(b.Select(s => s.Y).ToList()) });
            var line = new TrackMap { Track = Track, Length = Length };
            TrackMap.Resample(raw, Length, line);
            // 2. back onto the centre line: position - dlat to the left. Which way dlat counts is checked, not assumed:
            //    the right sign pulls each bin's cars together, the wrong one spreads them out.
            double s0 = Spread(line, 0), sp = Spread(line, 1), sm = Spread(line, -1);
            double sign = sp <= sm ? 1 : -1;
            if (Math.Min(sp, sm) > s0 * 0.8) { sign = 0; Note = string.Format("racing line (dlat did not line the cars up: {0:0.0} m spread, {1:0.0} m with it)", Math.Sqrt(s0), Math.Sqrt(Math.Min(sp, sm))); }
            else Note = string.Format("centre line ({0:0.0} m spread across the cars, {1:0.0} m after dlat{2})", Math.Sqrt(s0), Math.Sqrt(Math.Min(sp, sm)), sign > 0 ? "" : ", counted to the right");
            var centre = new List<double[]>();
            foreach (var b in bins)
            {
                if (b.Count == 0) continue;
                var xs = new List<float>(); var ys = new List<float>();
                foreach (S s in b)
                {
                    double tx, ty;
                    line.Heading(s.D, out tx, out ty);
                    xs.Add((float)(s.X - sign * s.Lat * -ty));
                    ys.Add((float)(s.Y - sign * s.Lat * tx));
                }
                // a car off line (a spin, a pit lane the flags missed) doesn't pull the bin: drop what is > 6 m away
                float mx = Median(new List<float>(xs)), my = Median(new List<float>(ys));
                var keep = Enumerable.Range(0, xs.Count).Where(k => (xs[k] - mx) * (xs[k] - mx) + (ys[k] - my) * (ys[k] - my) < 36).ToList();
                if (keep.Count > 0 && keep.Count < xs.Count) { mx = Median(keep.Select(k => xs[k]).ToList()); my = Median(keep.Select(k => ys[k]).ToList()); }
                centre.Add(new double[] { b.Average(s => s.D), mx, my });
            }
            var map = new TrackMap { Track = Track, Length = Length, Source = TrackMap.Recorded, Made = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            TrackMap.Resample(centre, Length, map);
            map.Smooth();
            return map;
        }

        // mean squared distance of each bin's samples from the bin's middle, with the samples moved by sign * dlat
        double Spread(TrackMap line, int sign)
        {
            double sum = 0; int cnt = 0;
            foreach (var b in bins)
            {
                if (b.Count < 2) continue;
                var px = new double[b.Count];
                for (int k = 0; k < b.Count; k++)
                {
                    double tx, ty, cx, cy;
                    line.Heading(b[k].D, out tx, out ty);
                    line.At(b[k].D, out cx, out cy);
                    double off = (b[k].X - cx) * -ty + (b[k].Y - cy) * tx;           // metres left of the line
                    px[k] = off - sign * b[k].Lat;
                }
                double mean = px.Average();
                foreach (double v in px) { sum += (v - mean) * (v - mean); cnt++; }
            }
            return cnt == 0 ? 0 : sum / cnt;
        }
    }
}
