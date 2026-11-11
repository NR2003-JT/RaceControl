using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceControl
{
    // Byte-addressed fake of NR2003's memory: unwritten bytes read as 0.
    class SimMemory : IGameMemory
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
        public string Str(long a, int max) { var s = new System.Text.StringBuilder(); for (int i = 0; i < max; i++) { byte v; b.TryGetValue(a + i, out v); if (v == 0) break; s.Append((char)v); } return s.ToString(); }
        public void W8(long a, byte v) { b[a] = v; }
        public void W16(long a, int v) { Put(a, BitConverter.GetBytes((ushort)v)); }
        public void W32(long a, int v) { Put(a, BitConverter.GetBytes(v)); }
        public void WF(long a, float v) { Put(a, BitConverter.GetBytes(v)); }
        public void WD(long a, double v) { Put(a, BitConverter.GetBytes(v)); }
    }

    // Runs the engine against the lap-81 line from line_dump.txt (pace car at 2663 m, 4017 m track), the same
    // scenario as build/test/run_sim.py runs against wave_around.lua.
    static class Sim
    {
        static SimMemory m = new SimMemory();
        static RaceEngine e;
        class C { public int Idx, Num, Laps; }
        static List<C> cars = new List<C>();

        static void SetCar(int idx, int num, int laps, double dlong, bool pit)
        {
            m.W16(Addr.CarNum + idx * Addr.Ocd, num);
            m.W16(Addr.Laps + idx * Addr.Ocd, laps);
            m.WF(Addr.Dlong + idx * Addr.Ocd, (float)dlong);
            m.W8(Addr.OnPitRoad + idx, (byte)(pit ? 1 : 0));
            m.W8(Addr.HasSpawned + idx, 1);
            m.W8(Addr.HasNotRetired + idx, 1);
            if (idx != 1) { m.W8(Addr.CarClass + idx * Addr.Ocd, 1); m.W8(Addr.CarClassLoad + idx * Addr.Ocd, 1); }
            m.W32(Addr.StandingsCache + idx * 16, laps);
            m.W32(Addr.CrossTable + idx * 2048 + laps * 4, 1000);
            cars.Add(new C { Idx = idx, Num = num, Laps = laps });
        }

        static void Tick(int flag) { m.W8(Addr.FlagState, (byte)flag); e.Tick(); }

        static int Main(string[] args)
        {

            RaceEngine.StatePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc_sim_state.txt");
            string dump = args[0];
            var log = new List<string>();
            e = new RaceEngine(m) { RealClock = () => m.F64(Addr.SessionClock), Logger = s => { log.Add(s); Console.WriteLine(s); } };

            m.W8(Addr.SimState, 2);
            m.W8(Addr.NumSessions, 1);
            m.W8(Addr.SessionNum, 0);
            m.W32(Addr.LapsComplete, 81);
            m.WD(Addr.TrackLength, 4017.0);
            m.W16(Addr.RaceLength, 200);
            e.GhostWhilePacing = true;
            m.W32(Addr.PenaltyList, 0);
            double pc = 2663.0;
            SetCar(1, 0, 82, pc, false);
            int idx = 2;
            var pittedNums = new HashSet<int> { 51, 13, 22 };   // pit this caution
            var rx = new Regex(@"#(\d+)\s+down=\s*([\d.]+) laps=\s*(\d+) gapToPC=\s*(\d+).*row=\s*(-?\d+) onPitRoad=(\d)");
            foreach (string ln in File.ReadAllLines(dump).Skip(1))
            {
                Match x = rx.Match(ln);
                if (!x.Success) continue;
                int num = int.Parse(x.Groups[1].Value);
                if (num == 21) continue;
                double dl = ((pc - double.Parse(x.Groups[4].Value)) % 4017 + 4017) % 4017;
                SetCar(idx, num, int.Parse(x.Groups[3].Value), dl, x.Groups[6].Value == "1");
                m.W32(Addr.PacingRow + idx * 13, int.Parse(x.Groups[5].Value));
                idx++;
            }
            m.W32(Addr.NumCars, idx);

            // flag 1: pit road opens; #51 #13 #22 are on pit road
            foreach (C c in cars.Where(c => pittedNums.Contains(c.Num))) m.W8(Addr.OnPitRoad + c.Idx, 1);
            m.W16(Addr.PitState, 0x100);
            Tick(1);
            foreach (C c in cars.Where(c => pittedNums.Contains(c.Num))) m.W8(Addr.OnPitRoad + c.Idx, 0);
            // everyone crosses the stripe once with pits open (a lap later in the same positions)
            foreach (C c in cars) m.W16(Addr.Laps + c.Idx * Addr.Ocd, m.U16(Addr.Laps + c.Idx * Addr.Ocd) + 1);
            Tick(2);
            // two laps after pit road opened, nobody on pit road: the early lineup
            m.W32(Addr.LapsComplete, 83);
            foreach (C c in cars) m.W8(Addr.OnPitRoad + c.Idx, 0);
            m.WD(Addr.SessionClock, 10.0); Tick(3);
            m.WD(Addr.SessionClock, 14.0); Tick(3);
            m.WD(Addr.SessionClock, 20.0);
            // a lead lap car (#48) comes out of the pits ahead of everyone before one to go
            foreach (C c in cars.Where(c => c.Num == 48)) m.WF(Addr.Dlong + c.Idx * Addr.Ocd, 2663 - 8);
            Tick(5);
            // the field sits still for 14 s: every pair still in the wrong order is untangled
            for (double t = 20.5; t <= 34; t += 0.5) { m.WD(Addr.SessionClock, t); Tick(5); }
            Console.WriteLine("class of car 2 while pacing: " + m.U8(Addr.CarClass + 2 * Addr.Ocd));
            m.WD(Addr.SessionClock, 34.5);
            // Better Cautions' "send lapped cars to back" writes its own order: the engine must restore ours
            var ours = Enumerable.Range(0, 60).Select(i => m.I32(Addr.PacingOutside1st + i * 8)).ToList();
            m.W32(Addr.PacingOutside1st, ours[1]); m.W32(Addr.PacingOutside1st + 8, ours[0]);
            Tick(5);
            bool restored = Enumerable.Range(0, 60).All(i => m.I32(Addr.PacingOutside1st + i * 8) == ours[i]);
            Console.WriteLine("lineup restored after another write: " + restored);

            // restart: everyone keeps laps, the waved cars dropped to the tail without gaining a lap
            var order = new List<int>();
            for (int i = 0; i <= 60; i++) { int c = m.I32(Addr.PacingOutside1st + i * 8); if (c != 0 || i == 0) order.Add(c); }
            foreach (int c in order)
            {
                C car = cars.FirstOrDefault(k => k.Idx == c && k.Idx != 1);
                if (car == null) continue;
                m.W16(Addr.Laps + c * Addr.Ocd, car.Laps + 2);
                m.W32(Addr.StandingsCache + c * 16, car.Laps + 2);
                m.W32(Addr.CrossTable + c * 2048 + (car.Laps + 1) * 4, 2000);
                m.W32(Addr.CrossTable + c * 2048 + (car.Laps + 2) * 4, 3000);
            }
            for (int i = 0; i < order.Count; i++)
                if (cars.Any(k => k.Idx == order[i] && k.Idx != 1))
                    m.WF(Addr.Dlong + order[i] * Addr.Ocd, (float)(((600 - i * 12) % 4017 + 4017) % 4017));
            // double file: one waved car (#39) is still 10 spots short of where it was told to be
            foreach (C c in cars.Where(c => c.Num == 39)) m.WF(Addr.Dlong + c.Idx * Addr.Ocd, 590f);
            m.WD(Addr.SessionClock, 90.0); Tick(6);
            var df = Enumerable.Range(0, 42).Select(i => m.I32(Addr.PacingInside1st + i * 4)).ToList();
            Console.WriteLine("double file: inside " + string.Join(" ", Enumerable.Range(0, 6).Where(i => i % 2 == 0).Select(i => "#" + m.U16(Addr.CarNum + df[i] * Addr.Ocd) + "/r" + m.I32(Addr.PacingRow + df[i] * 13))) +
                ", outside " + string.Join(" ", Enumerable.Range(0, 6).Where(i => i % 2 == 1).Select(i => "#" + m.U16(Addr.CarNum + df[i] * Addr.Ocd) + "/r" + m.I32(Addr.PacingRow + df[i] * 13))));
            m.W32(Addr.PacingInside1st, df[1]); m.W32(Addr.PacingInside1st + 4, df[0]); // Better Cautions writes its own
            m.WD(Addr.SessionClock, 91.5); Tick(6);
            Console.WriteLine("double file restored: " + Enumerable.Range(0, 42).All(i => m.I32(Addr.PacingInside1st + i * 4) == df[i]));
            // delayed green, then one to go and double file again
            m.WD(Addr.SessionClock, 95.0); Tick(4);
            m.WD(Addr.SessionClock, 96.0); Tick(5);
            m.WD(Addr.SessionClock, 98.0); Tick(6);
            m.WD(Addr.SessionClock, 100.0); Tick(7);
            m.WD(Addr.SessionClock, 104.0); Tick(7);
            Console.WriteLine("class of car 2 after the green: " + m.U8(Addr.CarClass + 2 * Addr.Ocd));
            foreach (C c in cars.Where(c => c.Num == 29 || c.Num == 24))
                Console.WriteLine("#" + c.Num + " scored laps " + m.I32(Addr.StandingsCache + c.Idx * 16));
            if (args.Length > 1) File.WriteAllLines(args[1], log);
            return 0;
        }
    }
}
