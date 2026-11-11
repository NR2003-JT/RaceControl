using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RaceControl
{
    // Offline track map: a stadium oval (two 800 m straights, two 200 m radius turns) driven anticlockwise by 30 fake
    // cars spread across the track, through TrackMapRecorder. Checks the map lands on the true centre line (dlat taken
    // out, both ways dlat could count), that cars on pit road / standing still / the pace car are left out, Nearest and
    // AtDlat, the save/load round trip (to a temp folder), and resampling a 500-point [x, y, fraction] map.
    class MapMem : IGameMemory
    {
        readonly Dictionary<long, byte> b = new Dictionary<long, byte>();
        public byte U8(long a) { byte v; return b.TryGetValue(a, out v) ? v : (byte)0; }
        public int U16(long a) { return U8(a) | U8(a + 1) << 8; }
        public int I32(long a) { return U8(a) | U8(a + 1) << 8 | U8(a + 2) << 16 | U8(a + 3) << 24; }
        public uint U32(long a) { return unchecked((uint)I32(a)); }
        public float F32(long a) { return BitConverter.ToSingle(BitConverter.GetBytes(I32(a)), 0); }
        public double F64(long a) { return BitConverter.Int64BitsToDouble((long)U32(a) | (long)U32(a + 4) << 32); }
        public string Str(long a, int max) { return ""; }
        public void W8(long a, byte v) { b[a] = v; }
        public void W16(long a, int v) { W8(a, (byte)v); W8(a + 1, (byte)(v >> 8)); }
        public void W32(long a, int v) { for (int i = 0; i < 4; i++) W8(a + i, (byte)(v >> (8 * i))); }
        public void WF(long a, float v) { W32(a, BitConverter.ToInt32(BitConverter.GetBytes(v), 0)); }
    }

    static class TrackMapSim
    {
        const double Straight = 800, R = 200;
        static readonly double L = 2 * Straight + 2 * Math.PI * R;
        static int fails;

        // the true centre line: start/finish in the middle of the bottom straight, heading +x (anticlockwise)
        static void Centre(double s, out double x, out double y, out double tx, out double ty)
        {
            s = ((s % L) + L) % L;
            double half = Straight / 2, arc = Math.PI * R;
            if (s < half) { x = s; y = -R; tx = 1; ty = 0; return; }
            s -= half;
            if (s < arc) { double a = -Math.PI / 2 + s / R; x = half + R * Math.Cos(a); y = R * Math.Sin(a); tx = -Math.Sin(a); ty = Math.Cos(a); return; }
            s -= arc;
            if (s < Straight) { x = half - s; y = R; tx = -1; ty = 0; return; }
            s -= Straight;
            if (s < arc) { double a = Math.PI / 2 + s / R; x = -half + R * Math.Cos(a); y = R * Math.Sin(a); tx = -Math.Sin(a); ty = Math.Cos(a); return; }
            s -= arc;
            x = -half + s; y = -R; tx = 1; ty = 0;
        }

        static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

        static TrackMap Run(int dlatSign, out TrackMapRecorder rec, out double seconds)
        {
            var m = new MapMem();
            var rnd = new Random(7);
            int n = 32;
            var d = new double[n]; var lat = new double[n]; var v = new double[n];
            for (int i = 0; i < n; i++) { d[i] = rnd.NextDouble() * 300; lat[i] = -6 + rnd.NextDouble() * 15; v[i] = 60 + rnd.NextDouble() * 10; }
            for (int i = 0; i < n; i++) { m.W8(Addr.HasNotRetired + i, 1); m.W8(Addr.HasSpawned + i, 1); }
            m.W8(Addr.OnPitRoad + 5, 1);                         // on pit road the whole time, 60 m off: must be left out
            rec = new TrackMapRecorder();
            rec.Start("simoval", L);
            TrackMap map = null;
            double t = 0, dt = 0.1;
            while (map == null && t < 600)
            {
                for (int i = 0; i < n; i++)
                {
                    if (i == 7 && t < 300) continue;             // standing still for a while (a wreck) - not moving: left out
                    d[i] = (d[i] + v[i] * dt) % L;
                    lat[i] += (rnd.NextDouble() - 0.5) * 0.4; lat[i] = Math.Max(-7, Math.Min(10, lat[i]));
                    double x, y, tx, ty;
                    Centre(d[i], out x, out y, out tx, out ty);
                    double l = i == 5 ? 60 : i == Addr.PaceCarIdx ? 25 : lat[i];   // pit road car / pace car way off the line
                    x += -ty * l; y += tx * l;
                    m.WF(Addr.Dlong + i * Addr.Ocd, (float)d[i]);
                    m.WF(Addr.Dlat + i * Addr.Ocd, (float)(dlatSign * lat[i]));
                    long b = 0xACCA30 + i * Addr.Ocd + 6622;
                    m.WF(b, (float)x); m.WF(b + 4, (float)y);
                }
                map = rec.Sample(m, n - 1);
                t += dt;
            }
            seconds = t;
            return map;
        }

        static double Worst(TrackMap map)
        {
            double worst = 0;
            for (int i = 0; i < TrackMap.N; i++)
            {
                double x, y, tx, ty;
                Centre(map.DlongOf(i), out x, out y, out tx, out ty);
                worst = Math.Max(worst, Math.Sqrt((map.X[i] - x) * (map.X[i] - x) + (map.Y[i] - y) * (map.Y[i] - y)));
            }
            return worst;
        }

        static int Main()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "rc_trackmap_sim");
            TrackMap.Folder = tmp;                                // never the shared %PROGRAMDATA% folder

            TrackMapRecorder rec; double secs;
            TrackMap map = Run(1, out rec, out secs);
            Check(map != null, string.Format("map finished ({0:0} s of driving, {1} cars used)", secs, rec.Cars));
            if (map == null) return 1;
            double w = Worst(map);
            Check(w < 1.0, string.Format("on the centre line: worst {0:0.00} m off ({1})", w, rec.Note));
            Check(rec.Cars <= 30, "pit road car and pace car left out (" + rec.Cars + " of 32 used)");

            TrackMapRecorder rec2; double secs2;
            TrackMap flipped = Run(-1, out rec2, out secs2);
            double w2 = flipped == null ? 99 : Worst(flipped);
            Check(w2 < 1.0, string.Format("dlat counted the other way is found: worst {0:0.00} m ({1})", w2, rec2.Note));

            double dist, x0, y0, tx, ty;
            Centre(1234, out x0, out y0, out tx, out ty);
            double near = map.Nearest(x0 + -ty * 5, y0 + tx * 5, out dist);
            Check(Math.Abs(near - 1234) < 3 && Math.Abs(dist - 5) < 1, string.Format("Nearest: 5 m off the line at 1234 m -> {0:0.0} m, {1:0.0} m away", near, dist));
            double ax, ay;
            map.AtDlat(500, 10, out ax, out ay);
            Centre(500, out x0, out y0, out tx, out ty);
            Check(Math.Abs(ax - (x0 - ty * 10)) < 1.2 && Math.Abs(ay - (y0 + tx * 10)) < 1.2, "AtDlat: + = left of travel");

            map.Track = "simoval";
            map.Save();
            TrackMap back = TrackMap.Load("simoval");
            Check(back != null && Math.Abs(back.Length - map.Length) < 0.01 && Math.Abs(back.X[123] - map.X[123]) < 0.01 && back.Source == TrackMap.Recorded,
                  "save / load round trip (" + TrackMap.PathFor("simoval") + ")");

            // a [x, y, fraction] map at 0.002 steps (500 points) comes back point for point
            var src = new List<double[]>();
            for (int i = 0; i < 500; i++) { double x, y; Centre(i * L / 500, out x, out y, out tx, out ty); src.Add(new[] { i * 0.002, x, y }); }
            var imp = new TrackMap { Track = "simoval", Length = L };
            TrackMap.Resample(src, 1.0, imp);
            Check(Worst(imp) < 0.01, string.Format("resampled fraction map: worst {0:0.000} m", Worst(imp)));

            try { Directory.Delete(tmp, true); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "all passed" : fails + " failed");
            return fails == 0 ? 0 : 1;
        }
    }
}
