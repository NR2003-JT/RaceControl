using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RaceControl
{
    class Mem2 : IGameMemory
    {
        readonly Dictionary<long, byte> b = new Dictionary<long, byte>();
        byte[] Get(long a, int n) { var r = new byte[8]; for (int i = 0; i < n; i++) { byte v; b.TryGetValue(a + i, out v); r[i] = v; } return r; }
        void Put(long a, byte[] v) { for (int i = 0; i < v.Length; i++) b[a + i] = v[i]; }
        public byte U8(long a) { return Get(a, 1)[0]; }
        public int U16(long a) { return BitConverter.ToUInt16(Get(a, 2), 0); }
        public int I32(long a) { return BitConverter.ToInt32(Get(a, 4), 0); }
        public uint U32(long a) { return BitConverter.ToUInt32(Get(a, 4), 0); }
        public float F32(long a) { return BitConverter.ToSingle(Get(a, 4), 0); }
        public double F64(long a) { return BitConverter.ToDouble(Get(a, 8), 0); }
        public string Str(long a, int max) { return ""; }
        public void W8(long a, byte v) { b[a] = v; }
        public void W16(long a, int v) { Put(a, BitConverter.GetBytes((ushort)v)); }
        public void W32(long a, int v) { Put(a, BitConverter.GetBytes(v)); }
        public void WF(long a, float v) { Put(a, BitConverter.GetBytes(v)); }
        public void WD(long a, double v) { Put(a, BitConverter.GetBytes(v)); }
    }

    // A whole caution on a 2000 m track, everyone moving at pace speed in one line behind the pace car:
    // pits held closed for a lap, opened before the leader's second time by, decisions at pit entry, the
    // early lineup, then the cone at 1000 m with the player (#7) choosing the outside.
    static class CautionSim
    {
        static Mem2 m = new Mem2();
        const double L = 2000, V = 40, Entry = 1700;
        class C { public int Idx, Num; public double D; public int Laps; public bool Pits; public double PitUntil = -1; public bool Exited; public double Around = -1; public bool Caught; }
        static List<C> cars = new List<C>();
        static double t;
        static List<string> log = new List<string>();

        static void Put(C c)
        {
            m.W16(Addr.CarNum + c.Idx * Addr.Ocd, c.Num);
            m.W16(Addr.Laps + c.Idx * Addr.Ocd, c.Laps);
            m.WF(Addr.Dlong + c.Idx * Addr.Ocd, (float)c.D);
            m.W8(Addr.OnPitRoad + c.Idx, (byte)(t < c.PitUntil ? 1 : 0));
            m.W32(Addr.StandingsCache + c.Idx * 16, c.Laps);
        }

        static int Main(string[] args)
        {
            RaceEngine.StatePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc_sim_state.txt");
            var e = new RaceEngine(m) { RealClock = () => t, Logger = s => { log.Add(s); Console.WriteLine(s); } };
            int choiceAsked = 0;
            e.PlayerLaneNeeded = o =>
            {
                choiceAsked++;
                Console.WriteLine("  chart: inside " + string.Join(" ", o.Inside.Select(n => "#" + n)) + " | outside " + string.Join(" ", o.Outside.Select(n => "#" + n)) + ", suggested " + o.Recommended);
            };
            e.ConePoint = args.Length > 1 ? double.Parse(args[1]) : 1000;
            string merge = Environment.GetEnvironmentVariable("MERGE"); // where cars move over for pit road (track.ini)
            if (!string.IsNullOrEmpty(merge)) e.PitMerges.Add(new[] { double.Parse(merge), Entry });
            if (Environment.GetEnvironmentVariable("STAGE") == "1") e.StageEnds = len => new[] { m.I32(Addr.LapsComplete) };
            e.PreferredLane = 1; e.GiveUpRows = 2; e.FrontRows = 2;
            if (Environment.GetEnvironmentVariable("NOPACKED") == "1") e.OpenWhenPacked = false; // the pits open on the leader's second time by
            if (Environment.GetEnvironmentVariable("STOCK") == "1")
            {
                // NR2003 runs the caution, as MainForm sets it: Lucky Dog only
                e.StockCautions = e.Classic = true; e.WaveAroundOn = e.LappedToBack = e.Cone = e.DoubleFile = e.Untangle = false;
                e.PitsClosedFirstLap = e.OpenWhenPacked = e.OpenPitsToAll = e.HoldOneToGoForPits = e.PaceCarWaits = false;
                e.LappedDropLow = e.ForgivePenalties = e.GhostOnPitRoad = e.GhostWhilePacing = e.GreenWhiteCheckered = false;
                e.StageRacing = false; e.RaceBack = true;
            }
            else if (Environment.GetEnvironmentVariable("CLASSIC") == "1")
            {
                // classic rules as MainForm sets them: no wave around, no cone, the game's pit timing, Lucky Dog, 2004 double file
                e.Classic = true; e.WaveAroundOn = e.LappedToBack = e.Cone = false;
                e.PitsClosedFirstLap = e.OpenWhenPacked = e.OpenPitsToAll = false; e.RaceBack = true;
            }
            m.W8(Addr.SimState, 2); m.W8(Addr.NumSessions, 1); m.W8(Addr.SessionNum, 0);
            m.WD(Addr.TrackLength, L); m.W16(Addr.RaceLength, 200);

            // line behind the pace car (index 1): #94 lapped, #12 lead lap but behind on track, #95 lapped, leader #10, ...
            var line = new[] { new[] { 94, 48 }, new[] { 12, 49 }, new[] { 95, 48 }, new[] { 10, 50 }, new[] { 11, 50 }, new[] { 7, 50 },
                new[] { 13, 50 }, new[] { 14, 50 }, new[] { 15, 50 }, new[] { 16, 50 }, new[] { 17, 50 }, new[] { 96, 49 } };
            double pcD = 1500;
            cars.Add(new C { Idx = 1, Num = 0, D = pcD, Laps = 50 });
            int idx = 2;
            for (int i = 0; i < line.Length; i++)
            {
                int n = line[i][0];
                var c = new C { Idx = n == 7 ? 0 : idx++, Num = n, D = pcD - 15 * (i + 1), Laps = line[i][1], Pits = n == 11 || n == 13 || n == 14 };
                cars.Add(c);
            }
            foreach (C c in cars)
            {
                m.W8(Addr.HasSpawned + c.Idx, 1); m.W8(Addr.HasNotRetired + c.Idx, 1);
                m.W32(Addr.CrossTable + c.Idx * 2048 + c.Laps * 4, 1000);
            }
            m.W32(Addr.NumCars, idx);
            // the leader's lap counter crossing: give #10 a progress lead
            // green flag stops earlier in the race taught the pit entry: three cars "enter" at ~1700 m
            int flag = 7;
            // "exit": cars come off pit road just behind the pace car, in front of the waved cars still going
            // around; a waved car kept ahead of them passes the pace car 3 s later
            bool exitAhead = args.Length > 3 && args[3] == "exit";
            Action step = () =>
            {
                foreach (C c in cars)
                {
                    // a car around the pace car runs faster until it is 15 m behind the tail of the field
                    double v = V;
                    if (c.Around == 0 && !c.Caught)
                    {
                        double pc = cars[0].D;
                        Func<C, double> gap = o => ((pc - o.D) % L + L) % L;
                        double tail = cars.Where(o => o != c && o.Idx != 1 && !(o.Around == 0 && !o.Caught) && !(t < o.PitUntil)).Select(gap).DefaultIfEmpty(0).Max();
                        if (gap(c) < L - 30 && gap(c) <= tail + 15) { c.Caught = true; Console.WriteLine(string.Format("  t={0:0.0} #{1} has caught the tail of the field", t, c.Num)); }
                        else v = V * (Environment.GetEnvironmentVariable("SLOWAROUND") == "1" ? 1.15 : 1.8);
                    }
                    double nd = c.D + v * 0.1;
                    if (nd >= L)
                    {
                        nd -= L;
                        // the game's crossing: the next scoring row gets the time (unless it was credited already)
                        int scored = m.I32(Addr.StandingsCache + c.Idx * 16);
                        if (m.I32(Addr.CrossTable + c.Idx * 2048 + (scored + 1) * 4) == 0) m.W32(Addr.CrossTable + c.Idx * 2048 + (scored + 1) * 4, 1000 + (int)(t * 1000));
                        c.Laps = scored + 1;
                    }
                    if (c.Pits && c.PitUntil < 0 && e.Flag >= 1 && c.D < Entry && nd >= Entry && log.Any(l => l.Contains("pits open")))
                    { c.PitUntil = t + 15; Console.WriteLine(string.Format("  t={0:0.0} #{1} drives onto pit road", t, c.Num)); }
                    c.D = nd;
                    if (exitAhead && c.PitUntil > 0 && t >= c.PitUntil && !c.Exited)
                    {
                        c.Exited = true;
                        c.D = cars[0].D - 5;
                        Console.WriteLine(string.Format("  t={0:0.0} #{1} comes off pit road just behind the pace car", t, c.Num));
                    }
                    if (c.Around == -1 && Environment.GetEnvironmentVariable("NOPASS") != c.Num.ToString() && log.Any(l => l.Contains("waved #" + c.Num + " goes to the end of the line"))) c.Around = t + 3;
                    if (c.Around > 0 && t >= c.Around && Environment.GetEnvironmentVariable("DROPBACK") == c.Num.ToString())
                    {
                        // lets the whole field by and sits at the tail instead of passing the pace car
                        c.Around = -2;
                        c.D = cars.Where(o => o != c && o.Idx != 1).Min(o => o.D) - 15;
                        Console.WriteLine(string.Format("  t={0:0.0} #{1} drops to the tail", t, c.Num));
                    }
                    if (c.Around > 0 && t >= c.Around && !(t < c.PitUntil))
                    {
                        c.Around = 0;
                        c.D = cars[0].D + 20;
                        if (c.D >= L) { c.D -= L; c.Laps++; }
                        Console.WriteLine(string.Format("  t={0:0.0} #{1} passes the pace car", t, c.Num));
                    }
                    Put(c);
                }
                t += 0.1;
                m.W8(Addr.FlagState, (byte)flag);
                m.W32(Addr.LapsComplete, cars.Where(c => c.Idx != 1).Max(c => c.Laps));
                e.Tick();
            };
            foreach (C c in cars) Put(c);
            for (int s = 0; s < 3; s++)
            {
                C g = cars[cars.Count - 1 - s];
                m.WF(Addr.Dlong + g.Idx * Addr.Ocd, (float)(Entry - 1 + s)); m.W8(Addr.FlagState, 7); e.Tick(); t += 0.1;
                m.W8(Addr.OnPitRoad + g.Idx, 1); e.Tick(); t += 0.1;
                m.W8(Addr.OnPitRoad + g.Idx, 0); e.Tick(); t += 0.1;
            }
            Console.WriteLine("pit entry learned: " + e.PitEntry);

            // caution: an AI pit request at flag 0 gets called off
            flag = 0;
            // GREENSTOP=n: car #n is one lap down and on pit road (a green flag stop) when the caution comes out
            string gs = Environment.GetEnvironmentVariable("GREENSTOP");
            if (!string.IsNullOrEmpty(gs))
            {
                C g = cars.First(c => c.Num.ToString() == gs);
                g.Laps--; g.Pits = false; g.PitUntil = t + 25;
                Console.WriteLine(string.Format("  t={0:0.0} #{1} is on its green flag stop, one lap down", t, g.Num));
            }
            m.W16(Addr.PitState, 0x100);
            m.W8(Addr.Pitting + cars.First(c => c.Num == 16).Idx * Addr.Ocd, 1);
            step();
            Console.WriteLine("pit state at flag 0: 0x" + m.U16(Addr.PitState).ToString("X") + ", #16 request " + m.U8(Addr.Pitting + cars.First(c => c.Num == 16).Idx * Addr.Ocd));
            int leaderLaps = cars.First(c => c.Num == 10).Laps;
            while (cars.First(c => c.Num == 10).Laps == leaderLaps) step();
            flag = 1; m.W16(Addr.PitState, 0x100);
            Console.WriteLine(string.Format("t={0:0.0} flag 1 (leader took the yellow)", t));
            for (int s = 0; s < 30; s++) step();
            flag = 2; m.W16(Addr.PitState, 0x8);
            while (!log.Any(l => l.Contains("pits open"))) step();
            Console.WriteLine(string.Format("t={0:0.0} leader at {1:0} m, laps {2}", t, cars.First(c => c.Num == 10).D, cars.First(c => c.Num == 10).Laps));
            for (int s = 0; s < 800; s++) { if (s == 300) flag = 3; step(); }
            flag = 5;
            bool drop = args.Length > 3 && args[3] == "drop"; // a lapped car still up front at one to go, dropping back after 15 s
            C late = cars.First(c => c.Num == 96);
            double lateBack = late.D;
            if (drop) { late.D = cars[0].D - 5; late.Laps--; }
            string dfat = Environment.GetEnvironmentVariable("DFAT"); // double file where the game calls it (learned), e.g. 1500
            if (!string.IsNullOrEmpty(dfat))
            {
                // like the game: one to go comes out before the line, double file once the pace car reaches DFAT
                e.DoubleFileAt = double.Parse(dfat);
                flag = 3;
                while (cars[0].D < L - 300) step();
                flag = 5;
                Console.WriteLine(string.Format("pace car at {0:0} m at one to go", cars[0].D));
                int pl = cars[0].Laps; double lastD = cars[0].D; bool crossed = false;
                for (int s = 0; s < 4000; s++)
                {
                    step();
                    if (cars[0].D < lastD - L / 2) crossed = true;
                    lastD = cars[0].D;
                    if (s % 50 == 0)
                    {
                        var ins = Enumerable.Range(0, 20).Select(k => m.I32(Addr.PacingInside1st + k * 8)).TakeWhile(x => x >= 0).Select(x => "#" + m.U16(Addr.CarNum + x * Addr.Ocd));
                        var outs = Enumerable.Range(0, 20).Select(k => m.I32(Addr.PacingOutside1st + k * 8)).TakeWhile(x => x >= 0).Select(x => "#" + m.U16(Addr.CarNum + x * Addr.Ocd));
                        Console.WriteLine(string.Format("  flag 5 pace car {0:0} m, leader {1:0} m, tail {2:0} m: inside {3} | outside {4}", cars[0].D,
                            cars.First(c => c.Num == 10).D, cars.Where(c => c.Idx != 1 && c != cars[0]).Min(c => c.D), string.Join(" ", ins), string.Join(" ", outs)));
                    }
                    if (crossed && cars[0].D >= e.DoubleFileAt) break;
                }
            }
            else
            for (int s = 0; s < 400 + (args.Length > 2 ? int.Parse(args[2]) : 0); s++)
            {
                if (drop && s == 150) { late.D = cars.Where(c => c != late && c.Idx != 1).Min(c => c.D) - 15; Console.WriteLine(string.Format("  t={0:0.0} #96 dropped to the back", t)); }
                step();
            }
            flag = 6; Console.WriteLine(string.Format("pace car at {0:0} m at double file", cars[0].D));
            for (int s = 0; s < 900; s++)
            {
                step();
                if (e.PlayerOfferOpen && choiceAsked > 0) { e.ChoosePlayerLane(2); choiceAsked = -1; }
                if (s % 150 == 0)
                {
                    int lines = m.U8(Addr.NumPacingLines);
                    var ins = Enumerable.Range(0, 20).Select(k => m.I32(Addr.PacingInside1st + k * 8)).TakeWhile(x => x >= 0).Select(x => "#" + m.U16(Addr.CarNum + x * Addr.Ocd));
                    var outs = Enumerable.Range(0, 20).Select(k => m.I32(Addr.PacingOutside1st + k * 8)).TakeWhile(x => x >= 0).Select(x => "#" + m.U16(Addr.CarNum + x * Addr.Ocd));
                    Console.WriteLine(string.Format("  t={0:0.0} lanes ({1}): inside {2} | outside {3}", t, lines, string.Join(" ", ins), string.Join(" ", outs)));
                }
            }
            string lj = Environment.GetEnvironmentVariable("LIVEJSON");
            if (!string.IsNullOrEmpty(lj)) File.WriteAllText(lj, e.LiveJson(log.Skip(log.Count - 3), "test"));
            if (args.Length > 0) File.WriteAllLines(args[0], log);
            return 0;
        }
    }
}
