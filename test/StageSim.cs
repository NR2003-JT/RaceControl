using System;
using System.Collections.Generic;
using System.Linq;

namespace RaceControl
{
    // Stage cautions on a 2000 m track, field in single file at racing speed. Each case starts a fresh race and
    // reports whether Race Control threw the caution, where, and whether the field was frozen or races back.
    static class StageSim
    {
        class Mem : IGameMemory
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

        const double L = 2000;

        // ownThrow: Race Control throws; otherAt: share of the stage's last lap (or of the next lap if > 1) where
        // another app throws the caution; raceBack: StageRaceBack.
        static void Case(string name, bool ownThrow, double otherAt, bool raceBack, bool stagesOn = true)
        {
            var m = new Mem();
            var log = new List<string>();
            RaceEngine.StatePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc_sim_state.txt");
            var e = new RaceEngine(m) { Logger = s => log.Add(s) };
            double t = 0;
            e.RealClock = () => t;
            e.StageRacing = stagesOn; e.StageThrowCaution = ownThrow; e.StageRaceBack = raceBack;
            e.StageEnds = len => new[] { 30 };
            m.W8(Addr.SimState, 2); m.W8(Addr.NumSessions, 1); m.W8(Addr.SessionNum, 0);
            m.WD(Addr.TrackLength, L); m.W16(Addr.RaceLength, 100);
            int n = 8;
            m.W32(Addr.NumCars, n + 2);
            var d = new double[n + 2]; var laps = new int[n + 2];
            for (int i = 0; i < n + 2; i++)
            {
                if (i == Addr.PaceCarIdx) continue;
                m.W8(Addr.HasSpawned + i, 1); m.W8(Addr.HasNotRetired + i, 1);
                m.W16(Addr.CarNum + i * Addr.Ocd, 10 + i);
                d[i] = 1000 - 30 * i; laps[i] = 28;
            }
            d[Addr.PaceCarIdx] = 0;
            int flag = 7;
            int thrownAtTick = -1; double thrownShare = double.NaN;
            for (int k = 0; k < 4000; k++)
            {
                for (int i = 0; i < n + 2; i++)
                {
                    if (i == Addr.PaceCarIdx) continue;
                    d[i] += 5; // 50 m/s
                    if (d[i] >= L) { d[i] -= L; laps[i]++; }
                    m.WF(Addr.Dlong + i * Addr.Ocd, (float)d[i]);
                    m.W16(Addr.Laps + i * Addr.Ocd, laps[i]);
                    m.W32(Addr.StandingsCache + i * 16, laps[i]);
                }
                int leader = 0;
                m.W32(Addr.LapsComplete, laps[leader]);
                double share = d[leader] / L;
                // another app's caution
                if (flag == 7 && otherAt >= 0)
                {
                    int lapNo = laps[leader] + 1; // lap being run
                    double at = otherAt;
                    bool fire = at <= 1 ? (lapNo == 30 && share >= at) : (lapNo == 31 && share >= at - 1);
                    if (fire) { m.W8(Addr.Yellow, 1); }
                }
                if (m.U8(Addr.Yellow) == 1 && flag == 7)
                {
                    if (thrownAtTick < 0) { thrownAtTick = k; thrownShare = share; }
                    m.W8(Addr.Yellow, 0);
                    flag = 0;
                }
                if (k == 100 && Environment.GetEnvironmentVariable("PHANTOM") == "1")
                {
                    m.W8(Addr.PenaltyStop + 3 * Addr.Ocd, 1); m.W8(Addr.Pitting + 3 * Addr.Ocd, 1);
                    Console.WriteLine("   (penalty stop flag set on #13 with no penalty, pit request " + m.U8(Addr.Pitting + 3 * Addr.Ocd) + ")");
                }
                if (k == 200 && Environment.GetEnvironmentVariable("PHANTOM") == "1")
                    Console.WriteLine("   (2 s later: flag " + m.U8(Addr.PenaltyStop + 3 * Addr.Ocd) + ", pit request " + m.U8(Addr.Pitting + 3 * Addr.Ocd) + ")");
                m.W8(Addr.FlagState, (byte)flag);
                e.Tick();
                t += 0.1;
                if (flag == 0 && k > thrownAtTick + 20) break;
            }
            bool frozen = Enumerable.Range(0, n).Any(i => m.U8(Addr.RacedBack + i) == 1);
            Console.WriteLine("== " + name);
            foreach (string s in log.Where(s => s.Contains("stage") || s.Contains("caution") || s.Contains("penalty stop"))) Console.WriteLine("   " + s);
            Console.WriteLine(string.Format("   caution came out: {0}; field {1}", thrownAtTick < 0 ? "no" : string.Format("leader {0:0%} round the lap", thrownShare),
                frozen ? "FROZEN" : "races back"));
            try { Console.WriteLine("   state.txt: " + System.IO.File.ReadAllText(RaceEngine.StatePath).Replace("\r\n", " | ")); } catch (Exception) { }
        }

        static int Main()
        {
            foreach (var t in new[] { Tuple.Create("Bristol", 0.533, 500), Tuple.Create("Darlington", 1.366, 367), Tuple.Create("Charlotte_2012_SDT_Night_CE", 1.5, 334),
                Tuple.Create("Charlotte_2012_SDT_Day", 1.5, 334), Tuple.Create("Atlanta", 1.54, 325), Tuple.Create("Sonoma", 1.99, 110), Tuple.Create("Daytona", 2.5, 200), Tuple.Create("Martinsville", 0.526, 500) })
            {
                string why;
                StageSettings.ByTrack(t.Item2 * 1609.344, t.Item1, out why);
                Console.WriteLine(string.Format("{0,-30} {1,4} laps: ends {2,-12} ({3})", t.Item1, t.Item3,
                    string.Join(",", StageSettings.ByTrack(t.Item2 * 1609.344, t.Item1, t.Item3).Select(x => x.ToString()).ToArray()), why));
            }
            foreach (string t in new[] { "bristol_night_RR", "Bristol_2019_BBMC_Day", "Charlotte_2012_SDT_Night_CE", "ADSS_2023" })
            {
                string ini = @"C:\Papyrus\NASCAR Racing 2003 Season\tracks\" + t + @"\track.ini";
                if (!System.IO.File.Exists(ini)) continue;
                Console.WriteLine(t + " pit lanes (move over, pit lane proper): " + string.Join("; ", RaceEngine.ReadPitMerges(ini).Select(p => p[0] + ", " + p[1]).ToArray()));
            }
            Case("Race Control throws, race back on", true, -1, true);
            Case("Race Control throws, race back off", true, -1, false);
            Case("other app throws at the line (leader just crossed), RC throw off", false, 1.02, true);
            Case("other app throws at 80% of the last lap, RC throw off", false, 0.80, true);
            Case("wreck at 40% of the stage's last lap, RC throw off", false, 0.40, true);
            Case("wreck mid next lap (40% into lap after stage end), RC throw off", false, 1.40, true);
            Case("stage racing off, other app throws at the line", false, 1.02, true, false);
            return 0;
        }
    }
}
