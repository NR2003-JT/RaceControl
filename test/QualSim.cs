using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceControl
{
    // Offline single car qualifying: a fake NR2003 with AI cars in pit boxes at very different distances from pit exit,
    // driven through Qualifying.Tick. Checks the green gap release (every car should take the green about the set gap
    // after the car before finishes), the timing box focus sequence, a car that comes in by itself after one timed lap
    // (its lap stands) and one that comes in before its green (DNQ).
    class FakeMem : IGameMemory
    {
        readonly Dictionary<long, byte> b = new Dictionary<long, byte>();
        public byte U8(long a) { byte v; return b.TryGetValue(a, out v) ? v : (byte)0; }
        public int U16(long a) { return U8(a) | U8(a + 1) << 8; }
        public int I32(long a) { return U8(a) | U8(a + 1) << 8 | U8(a + 2) << 16 | U8(a + 3) << 24; }
        public uint U32(long a) { return unchecked((uint)I32(a)); }
        public float F32(long a) { return BitConverter.ToSingle(BitConverter.GetBytes(I32(a)), 0); }
        public double F64(long a) { return BitConverter.Int64BitsToDouble((long)U32(a) | (long)U32(a + 4) << 32); }
        public string Str(long a, int max) { var s = ""; for (int i = 0; i < max && U8(a + i) != 0; i++) s += (char)U8(a + i); return s; }
        public bool Guard;                                   // record writes Race Control makes into the player's car block
        public readonly List<long> PlayerWrites = new List<long>();
        public void W8(long a, byte v) { if (Guard && a >= 0xACCA30 && a < 0xACCA30 + Addr.Ocd) PlayerWrites.Add(a); b[a] = v; }
        public void W16(long a, int v) { W8(a, (byte)v); W8(a + 1, (byte)(v >> 8)); }
        public void W32(long a, int v) { for (int i = 0; i < 4; i++) W8(a + i, (byte)(v >> (8 * i))); }
        public void WF(long a, float v) { W32(a, BitConverter.ToInt32(BitConverter.GetBytes(v), 0)); }
        public void WD(long a, double v) { long x = BitConverter.DoubleToInt64Bits(v); W32(a, (int)x); W32(a + 4, (int)(x >> 32)); }
        public void WS(long a, string s) { for (int i = 0; i < s.Length; i++) W8(a + i, (byte)s[i]); W8(a + s.Length, 0); }
    }

    static class QualSim
    {
        const long Block0 = 0xACCA30, Cross0 = 0x77F996, Driver0 = 14711636;
        const int Stride = Addr.Ocd, State = 5031, Countdown = 6922, RunEnd = 6918, Lap = 6778, PosX = 6622;
        const uint Wait = 0x760f60, Leaving = 0x760efc, Track = 0x760d2c, PitIn = 0x760f48, Entering = 0x760f84;
        const double L = 2000, Exit = 350, Entry = 1600, PitV = 20, TrackV = 50;

        class Car { public int Slot, Num; public double Stall, D, Speed, Wait = -1, Act; public uint St = QualSim.Wait; public int Laps, Crosses; public int AbortAfterCrossings = -1;
            public Queue<double> RunSpeeds; }                // (each run's speed in turn, taken as the car pulls out)

        // "QualSim extra" runs only the checks added with the DNQs / re-qualifying lanes / the review fixes (the full run is slow)
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "extra") return Extra();
            int f = Full();
            return Extra() + f > 0 ? 1 : 0;
        }

        static int Full()
        {
            var m = new FakeMem();
            double t = 0;
            m.W8(0x977A00, 4); m.W8(Addr.SessionNum, 0); m.W8(Addr.NumSessions, 3); m.W16(0xBEBDA3 + 8, 5);
            m.W32(Addr.TickRateFlag, 1); m.WD(Addr.TrackLength, L); m.WS(Addr.TrackName, "simtrack");
            Qualifying.RunsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc_qualsim_runs.txt");   // never the real file
            // never the real qualifying_state.txt either: RaceVision follows its focus car (sim runs during a live session
            // sent the TV camera to cars sitting in their pit boxes)
            Qualifying.StateDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc_qualsim_state");
            if (System.IO.File.Exists(Qualifying.RunsPath)) System.IO.File.Delete(Qualifying.RunsPath);
            var cars = new List<Car>();
            for (int i = 0; i < 8; i++)
            {
                var c = new Car { Slot = 2 + i, Num = 10 + i, Stall = (1650 + i * 85) % L, Speed = TrackV + (i % 3 - 1) * 0.4 };
                c.D = c.Stall;
                cars.Add(c);
                m.WS(Driver0 + c.Slot * 175, "Sim"); m.WS(Driver0 + c.Slot * 175 + 16, "Driver" + c.Num); m.W32(Driver0 + c.Slot * 175 + 156, c.Num);
                m.W32(Block0 + c.Slot * Stride + Countdown, 10000000);
            }
            cars[4].AbortAfterCrossings = 3;     // #14: out of fuel after its first timed lap -> that lap stands
            cars[6].AbortAfterCrossings = 0;     // #16: comes in before its green -> DNQ

            var q = new Qualifying { Mode = Qualifying.Format.Single, Laps = 2, ReleaseMode = Qualifying.Release.Gap, ReleaseValue = 8, MaxOut = 3, FuelPercent = 0, QualPace = false, DnqSeconds = 90 };
            q.Clock = () => t;
            var log = new List<string>();
            q.Logger = s => { string line = t.ToString("0000.0", CultureInfo.InvariantCulture) + " " + s.Substring(9); log.Add(line); Console.WriteLine(line); };
            string lastBox = "";
            var boxSeq = new List<string>();
            bool started = false;
            const double dt = 0.05;
            for (int step = 0; step < 200000 && t < 4000; step++)
            {
                t += dt;
                m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                foreach (var c in cars) Move(m, c, t, dt);
                q.Tick(m);
                if (!started && q.CanStart && t > 2) { q.Start(); started = true; }
                string box = q.BoxKind + " #" + (q.BoxCar >= 2 ? q.CarNumber(q.BoxCar).ToString() : "-");
                if (box != lastBox) { lastBox = box; boxSeq.Add(t.ToString("0000.0", CultureInfo.InvariantCulture) + " box " + box); Console.WriteLine(boxSeq.Last()); }
                if (started && q.Status.StartsWith("Done")) break;
            }
            Console.WriteLine();
            int fails = 0;
            Action<bool, string> check = (ok, what) => { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; };
            var gaps = log.Select(l => Regex.Match(l, @"#(\d+) took the green ([\d.]+) s (after|before)")).Where(x => x.Success)
                .Select(x => Tuple.Create(x.Groups[1].Value, double.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture) * (x.Groups[3].Value == "after" ? 1 : -1))).ToList();
            Console.WriteLine("gaps: " + string.Join(", ", gaps.Select(g => "#" + g.Item1 + " " + g.Item2.ToString("0.0", CultureInfo.InvariantCulture)).ToArray()));
            var learned = gaps.Skip(1).ToList();
            check(learned.Count >= 3 && learned.All(g => Math.Abs(g.Item2 - 8) <= 2), "green gaps after the first pair within 2 s of the 8 s aim");
            check(log.Any(l => l.Contains("#14") && l.Contains("came back to pit road on its own") && l.Contains("stand")), "#14 (in by itself after a timed lap): its lap stands");
            check(log.Any(l => l.Contains("#16 DNQ: came back to pit road on its own")), "#16 (in before its green): DNQ");
            check(q.Status.StartsWith("Done"), "all cars ran");
            // box: every car that took the green was named (Waiting) before it went Live, and every finished run showed a Result
            foreach (var c in cars.Where(c => c.Num != 16))
            {
                string n = "#" + c.Num;
                int wait = boxSeq.FindIndex(s => s.EndsWith("Waiting " + n)), live = boxSeq.FindIndex(s => s.EndsWith("Live " + n));
                check(live >= 0 && (wait < 0 || wait < live), n + " shown live" + (wait >= 0 ? " after -.-- (waiting)" : ""));
                if (c.Num != 14) check(boxSeq.Any(s => s.EndsWith("Result " + n)), n + " result shown");
            }
            // the qualifying session: the times are written, kept when the session is restarted, and saved for a restarted
            // Race Control
            Action<int, double> session = (kind, secs) =>
            {
                m.W8(0x977A00, (byte)kind);
                for (double end = t + secs; t < end; t += dt) { m.W32(Addr.GameTicks, (int)Math.Round(t * 36)); q.Tick(m); }
            };
            Func<string, int> count = w => log.Count(l => l.Contains(w));
            session(4, 2); session(5, 3); session(6, 1); session(5, 3); session(7, 1);
            check(count("every lap the AI post is replaced") == 2, "qualifying session restarted: the times are written again");
            check(count("the race loaded: the grid from qualifying stands") == 1, "the race loaded: grid kept, then cleared");
            check(System.IO.File.Exists(Qualifying.RunsPath) && System.IO.File.ReadAllText(Qualifying.RunsPath).Contains("track simtrack"), "times saved for a restarted Race Control");
            var q2 = new Qualifying { Mode = Qualifying.Format.Single };
            q2.Clock = () => t; q2.Logger = q.Logger; q = q2;
            session(5, 3);
            check(count("times loaded from the qualifying run saved") == 1 && count("every lap the AI post is replaced") == 3, "a restarted Race Control loads the saved times in qualifying");
            m.WS(Addr.TrackName, "othertrack");
            var q3 = new Qualifying { Mode = Qualifying.Format.Single };
            q3.Clock = () => t; q3.Logger = q.Logger; q = q3;
            session(7, 1); session(5, 3);
            check(count("times loaded from the qualifying run saved") == 1, "saved times are not used at another track");

            // practice ends in the middle of the runs: the game clears the crossings and sends every car to the garage
            m.W8(0x977A00, 4);
            foreach (var c in cars) c.AbortAfterCrossings = -1;
            var q5 = new Qualifying { Mode = Qualifying.Format.Single, Laps = 2, ReleaseMode = Qualifying.Release.Gap, ReleaseValue = 8, MaxOut = 3, FuelPercent = 0, QualPace = false, DnqSeconds = 90 };
            q5.Clock = () => t; q5.Logger = q.Logger; q = q5;
            int mark = log.Count;
            bool st = false; Car inLap = null;
            for (int step = 0; step < 200000 && inLap == null; step++)
            {
                t += dt;
                m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                foreach (var c in cars) Move(m, c, t, dt);
                q.Tick(m);
                if (!st && q.CanStart) { q.Start(); st = true; }
                if (st) inLap = cars.FirstOrDefault(c => c.St == PitIn);
            }
            for (int i = 0; i <= inLap.Laps; i++) m.W32(Cross0 + inLap.Slot * 2048 + i * 4, 0);
            foreach (var c in cars) m.W32(Block0 + c.Slot * Stride + State, unchecked((int)0x760ea0));
            t += dt; q.Tick(m);
            m.W8(0x977A00, 6); t += dt; q.Tick(m);
            var after = log.Skip(mark).ToList();
            check(after.Any(l => l.Contains("#" + inLap.Num + " ") && (l.Contains(": laps ") || l.Contains("practice ended during its run - laps"))),
                "practice ending on #" + inLap.Num + "'s in-lap (crossings cleared): its timed laps stand");
            check(!after.Any(l => l.Contains("DNQ: went to the garage")), "no 'went to the garage' DNQ when the session ends");

            // Indy oval: 4-lap average runs, every car back in its box first
            foreach (var c in cars)
            {
                // a new practice session: an empty crossing table and lap count, like the game starts each session with
                for (int i = 0; i <= c.Laps; i++) m.W32(Cross0 + c.Slot * 2048 + i * 4, 0);
                c.Laps = 0; c.St = Wait; c.D = c.Stall; c.Wait = -1; c.Crosses = 0; c.AbortAfterCrossings = -1;
                m.W32(Block0 + c.Slot * Stride + Countdown, 10000000);
            }
            m.W8(0x977A00, 4);
            var q6 = new Qualifying { Mode = Qualifying.Format.IndyOval, Laps = 4, ReleaseMode = Qualifying.Release.Gap, ReleaseValue = 8, MaxOut = 3, FuelPercent = 0, QualPace = false, DnqSeconds = 90 };
            q6.Clock = () => t; q6.Logger = q.Logger; q = q6;
            mark = log.Count; st = false;
            for (int step = 0; step < 400000 && !q.Status.StartsWith("Done"); step++)
            {
                t += dt;
                m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                foreach (var c in cars) Move(m, c, t, dt);
                q.Tick(m);
                if (!st && q.CanStart) { q.Start(); st = true; }
            }
            var indy = log.Skip(mark).ToList();
            var avgLines = indy.Where(l => l.Contains(", average ") && l.Contains(" mph)")).ToList();
            check(q.Status.StartsWith("Done") && avgLines.Count == cars.Count, "Indy oval: every car's run scored by its 4-lap average with mph (" + avgLines.Count + " of " + cars.Count + ")");
            var saved = System.IO.File.ReadAllLines(Qualifying.RunsPath).Where(l => !l.StartsWith("track") && !l.StartsWith("saved")).ToList();
            check(saved.Count == cars.Count && saved.All(l => { var v = l.Split(' ')[1].Split(','); return v.Length == 4 && v.Distinct().Count() == 1; }),
                "Indy oval: each car's grid laps are 4 x its average");
            // the order is by average, not best lap: check against the logged averages
            var avg = avgLines.Select(l => Regex.Match(l, @"#(\d+) .*average ([\d.]+)"))
                .ToDictionary(x => "#" + x.Groups[1].Value, x => double.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture));
            // the results board is one log entry, a line per car
            var board = indy.FirstOrDefault(l => l.Contains("INDY OVAL QUALIFYING (4-LAP AVERAGE)")) ?? "";
            var gridLine = board.Split('\n').Skip(1).Select(l => Regex.Match(l, @"#(\d+)").Value).Where(n => n != "").ToList();
            var inOrder = gridLine.Select(n => avg.ContainsKey(n) ? avg[n] : 9e9).ToList();   // ties may come in either order
            check(inOrder.Count == cars.Count && inOrder.Zip(inOrder.Skip(1), (a, b) => a <= b).All(x => x), "Indy oval: results ranked by the 4-lap average");

            // NASCAR knockout rounds: 3 min (top 4 through), then 2 min; every car back in its box first
            foreach (var c in cars)
            {
                for (int i = 0; i <= c.Laps; i++) m.W32(Cross0 + c.Slot * 2048 + i * 4, 0);
                c.Laps = 0; c.St = Wait; c.D = c.Stall; c.Wait = -1; c.Crosses = 0; c.AbortAfterCrossings = -1;
                m.W32(Block0 + c.Slot * Stride + Countdown, 10000000); m.W32(Block0 + c.Slot * Stride + RunEnd, 0);
            }
            m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
            var q7 = new Qualifying { Mode = Qualifying.Format.Knockout, Rounds = "3:4, 2:4", BreakSeconds = 20, FuelPercent = 0, QualPace = false };
            q7.Clock = () => t; q7.Logger = q.Logger; q = q7;
            mark = log.Count; st = false;
            for (int step = 0; step < 400000 && !q.Status.StartsWith("Done"); step++)
            {
                t += dt;
                m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                foreach (var c in cars) Move(m, c, t, dt);
                q.Tick(m);
                if (!st && q.CanStart) { q.Start(); st = true; }
            }
            var ko = log.Skip(mark).ToList();
            check(ko.Any(l => l.Contains("ROUND 1 RESULTS")) && ko.Any(l => l.Contains("ROUND 2 RESULTS")), "knockout: both rounds ran and posted results");
            var r1 = ko.FirstOrDefault(l => l.Contains("ROUND 1 RESULTS")) ?? "";
            int r1Times = Regex.Matches(r1, @"#\d+ \d+\.\d{3}").Count;
            check(r1Times >= cars.Count - 1, "knockout: round 1 times for most cars (" + r1Times + " of " + cars.Count + ")");
            var koGrid = ko.FirstOrDefault(l => l.Contains("KNOCKOUT QUALIFYING GRID")) ?? "";
            var koRows = koGrid.Split('\n').Skip(1).Where(l => Regex.IsMatch(l, @"#\d+")).ToList();
            check(q.Status.StartsWith("Done") && koRows.Count == cars.Count, "knockout: grid written for every car (" + koRows.Count + " of " + cars.Count + ")");
            check(!ko.Any(l => l.Contains("DNQ") || l.Contains("Exception")), "knockout: no DNQs or errors");

            // Indy road / street: groups of 4 (top 2 of each through), a Fast 12 of 4 (top 2 through), the Fast Six
            foreach (var c in cars)
            {
                for (int i = 0; i <= c.Laps; i++) m.W32(Cross0 + c.Slot * 2048 + i * 4, 0);
                c.Laps = 0; c.St = Wait; c.D = c.Stall; c.Wait = -1; c.Crosses = 0; c.AbortAfterCrossings = -1;
                m.W32(Block0 + c.Slot * Stride + Countdown, 10000000); m.W32(Block0 + c.Slot * Stride + RunEnd, 0);
            }
            m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
            var q8 = new Qualifying { Mode = Qualifying.Format.IndyRoad, IndyGroupMin = 2, IndyGroupAdv = 2, IndyF12Min = 2, IndyF12Adv = 2, IndyF6Min = 2,
                IndySplit = Qualifying.Split.Random, BreakSeconds = 20, FuelPercent = 0, QualPace = false };
            q8.Clock = () => t; q8.Logger = q.Logger; q = q8;
            mark = log.Count; st = false;
            for (int step = 0; step < 600000 && !q.Status.StartsWith("Done"); step++)
            {
                t += dt;
                m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                foreach (var c in cars) Move(m, c, t, dt);
                q.Tick(m);
                if (!st && q.CanStart) { q.Start(); st = true; }
            }
            var ir = log.Skip(mark).ToList();
            check(new[] { "GROUP 1 RESULTS", "GROUP 2 RESULTS", "FAST 12 RESULTS", "FAST SIX RESULTS" }.All(w => ir.Any(l => l.Contains(w))), "Indy road: Group 1, Group 2, Fast 12 and Fast Six all ran");
            Func<string, List<string>> nums = line => Regex.Matches(line ?? "", @"#(\d+)").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
            var g1 = nums(ir.FirstOrDefault(l => l.Contains("qualifying: Group 1: ")));
            var g2 = nums(ir.FirstOrDefault(l => l.Contains("qualifying: Group 2: ")));
            var f12 = nums(ir.FirstOrDefault(l => l.Contains("FAST 12: ") && l.Contains("cars")) == null ? "" : "");
            var f6Res = nums((ir.FirstOrDefault(l => l.Contains("FAST SIX RESULTS")) ?? "").Split(new[] { "RESULTS:" }, StringSplitOptions.None).Last());
            var f12Res = nums((ir.FirstOrDefault(l => l.Contains("FAST 12 RESULTS")) ?? "").Split(new[] { "RESULTS:" }, StringSplitOptions.None).Last());
            var irGrid = (ir.FirstOrDefault(l => l.Contains("INDY ROAD / STREET QUALIFYING GRID")) ?? "").Split('\n').Skip(1)
                .Select(l => Regex.Match(l, @"#(\d+)").Groups[1].Value).Where(n => n != "").ToList();
            check(g1.Count == 4 && g2.Count == 4 && irGrid.Count == cars.Count, "Indy road: two groups of 4 and a grid of " + irGrid.Count);
            bool front = irGrid.Count == 8 && irGrid.Take(2).SequenceEqual(f6Res.Take(2)) && irGrid.Skip(2).Take(2).All(n => f12Res.Contains(n) && !f6Res.Contains(n));
            check(front, "Indy road: the Fast Six cars first, then the rest of the Fast 12");
            bool alternate = irGrid.Count == 8 && irGrid.Skip(4).Select((n, i) => i % 2 == 0 ? g1.Contains(n) : g2.Contains(n)).All(x => x);
            check(alternate, "Indy road: from 5th (13th in a full field) Group 1 and Group 2 alternate");
            // the player's car (slot 0, no AI state): takes its turn in single car runs when told, never written to
            var me = new Player { D = (1600 + 85 * 8) % L };
            me.Stall = me.D;
            m.WS(Driver0, "Sim"); m.WS(Driver0 + 16, "Player"); m.W32(Driver0 + 156, 99); m.W8(Addr.HasSpawned, 1);
            Action resetAll = () =>
            {
                foreach (var c in cars)
                {
                    for (int i = 0; i <= c.Laps; i++) m.W32(Cross0 + c.Slot * 2048 + i * 4, 0);
                    c.Laps = 0; c.St = Wait; c.D = c.Stall; c.Wait = -1; c.Crosses = 0; c.AbortAfterCrossings = -1;
                    m.W32(Block0 + c.Slot * Stride + Countdown, 10000000); m.W32(Block0 + c.Slot * Stride + RunEnd, 0);
                }
                for (int i = 0; i <= me.Laps; i++) m.W32(Cross0 + i * 4, 0);
                me.Laps = 0; me.Mode = 0; me.D = me.Stall; me.GoAt = -1;
            };
            resetAll();
            m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
            var q9 = new Qualifying { Mode = Qualifying.Format.Single, Laps = 2, ReleaseMode = Qualifying.Release.PitRoad, MaxOut = 3, FuelPercent = 25, QualPace = true, DnqSeconds = 90,
                Order = "15, 99, 11" };
            q9.Clock = () => t; q9.Logger = q.Logger; q = q9;
            mark = log.Count; st = false;
            var calls = new List<string>();
            var timers = new List<string>();
            bool sawDrive = false, trackNoDrive = false, fileDrive = false;
            string statePath = System.IO.Path.Combine(Qualifying.StateDir, "qualifying_state.txt");
            m.PlayerWrites.Clear(); m.Guard = true;
            for (int step = 0; step < 400000 && !q.Status.StartsWith("Done"); step++)
            {
                t += dt;
                m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                m.Guard = false;
                foreach (var c in cars) Move(m, c, t, dt);
                MovePlayer(m, me, q, t, dt);
                m.Guard = true;
                q.Tick(m);
                if (q.PlayerCall != "" && (calls.Count == 0 || calls.Last() != q.PlayerCall)) calls.Add(q.PlayerCall);
                string lbl = q.PlayerTimer.Split('|')[0];
                if (lbl != "" && (timers.Count == 0 || timers.Last() != lbl)) timers.Add(lbl);
                if (q.DriveCar == 0) sawDrive = true;
                if (me.Mode >= 1 && me.Mode <= 3 && q.DriveCar != 0) trackNoDrive = true;
                if (q.DriveCar == 0 && !fileDrive && step % 50 == 0) try { fileDrive = System.IO.File.ReadAllText(statePath).Contains("\ndrive 0\n"); } catch (Exception) { }
                if (!st && q.CanStart) { q.Start(); st = true; }
            }
            m.Guard = false;
            Console.WriteLine("player calls: " + string.Join(" | ", calls.ToArray()));
            Console.WriteLine("player timers: " + string.Join(" | ", timers.ToArray()));
            check(sawDrive && !trackNoDrive && q.DriveCar == -1, "player in single car runs: drive = the player from GO until stopped back in the box");
            check(fileDrive, "player in single car runs: the state file names the car being driven (drive 0)");
            check(timers.Contains("AT THE GREEN IN"), "player in single car runs: an expected-at-the-green countdown");
            var pl = log.Skip(mark).ToList();
            check(m.PlayerWrites.Count == 0, "player in single car runs: nothing written to the player's car (" + m.PlayerWrites.Count + " writes)");
            check(pl.Any(l => l.Contains("order by your order") && l.Contains("#15 #99 #11")), "player in single car runs: in the order where it was listed");
            check(calls.Any(c => c.StartsWith("YOU'RE NEXT")) && calls.Any(c => c.StartsWith("YOUR RUN: GO NOW")) && calls.Any(c => c.StartsWith("TIMED LAP 2 OF 2"))
                && calls.Any(c => c.StartsWith("RUN COMPLETE")), "player in single car runs: next / go / timed laps / come in shown");
            check(pl.Any(l => l.Contains("#99 Sim Player: laps ")), "player in single car runs: its laps timed");
            var spBoard = pl.FirstOrDefault(l => l.Contains("SINGLE CAR QUALIFYING:")) ?? "";
            check(Regex.IsMatch(spBoard, @"#99\s+Sim Player\s+\d"), "player in single car runs: ranked with a time in the results");
            check(q.Status.StartsWith("Done") && pl.Count(l => l.Contains(": laps ")) == cars.Count + 1, "player in single car runs: every car and the player ran");
            // qualifying session: the player's practice time is written over its own lap like the AI's
            mark = log.Count;
            session(4, 1); session(5, 3);
            check(log.Skip(mark).Any(l => l.Contains("#99 set to")), "player: its practice time written in the qualifying session");
            session(7, 1);

            // the player's turn with a short DNQ window (20 s): a slow start is a DNQ, but not while the ready screen
            // is up (until PlayerResumed) and not for the time the game is paused
            Func<string, double, bool, double, double, bool> slowStart = (what, delay, readyPause, resumeAfter, pauseFor) =>
            {
                resetAll();
                me.Delay = delay;
                m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
                var qr = new Qualifying { Mode = Qualifying.Format.Single, Laps = 1, ReleaseMode = Qualifying.Release.PitRoad, MaxOut = 3, FuelPercent = 25,
                    QualPace = true, DnqSeconds = 20, Order = "15, 99, 11", PlayerReadyPause = readyPause };
                qr.Clock = () => t; qr.Logger = q.Logger; q = qr;
                int mk = log.Count; bool qrStarted = false; double goAt = -1, pauseEnd = -1;
                for (int step = 0; step < 400000 && !q.Status.StartsWith("Done"); step++)
                {
                    t += dt;
                    bool paused = pauseEnd > 0 && t < pauseEnd;
                    m.W8(Addr.SimState, (byte)(paused ? 3 : 2));
                    if (!paused)
                    {
                        m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                        m.Guard = false;
                        foreach (var c in cars) Move(m, c, t, dt);
                        MovePlayer(m, me, q, t, dt);
                        m.Guard = true;
                    }
                    q.Tick(m);
                    if (goAt < 0 && q.PlayerCallKind == 1 && q.DriveCar == 0)
                    {
                        goAt = t;
                        if (pauseFor > 0) { pauseEnd = t + pauseFor; me.GoAt = pauseEnd + delay; }      // the player waits out the pause
                        if (readyPause) me.GoAt = t + resumeAfter + delay;
                    }
                    if (readyPause && goAt >= 0 && q.PlayerHolding && t >= goAt + resumeAfter) q.PlayerResumed();
                    if (!qrStarted && q.CanStart) { q.Start(); qrStarted = true; }
                }
                m.Guard = false; m.W8(Addr.SimState, 2); me.Delay = 1.0;
                bool dnqd = log.Skip(mk).Any(l => l.Contains("#99 Sim Player didn't pull out"));
                Console.WriteLine("  " + what + ": " + (dnqd ? "DNQ" : "ran"));
                return dnqd;
            };
            check(slowStart("30 s to pull out, no ready screen", 30, false, 0, 0), "player slow start: 30 s to pull out with a 20 s DNQ = DNQ (control)");
            check(!slowStart("ready screen up 60 s, out 8 s after resuming", 8, true, 60, 0), "player ready screen: no DNQ while it is up, the 20 s start at resume");
            check(!slowStart("game paused 60 s, out 8 s after", 8, false, 0, 60), "player paused: paused time doesn't count to the DNQ");
            me.StuckPitFlag = true;                              // live 2026-10-26: out 11 s after the go, still a DNQ at 20 s
            check(!slowStart("out 11 s after the go, pit road flag never clears", 11, false, 0, 0), "player: once out of the box, never a DNQ (whatever the pit road flag says)");
            me.StuckPitFlag = false;

            // live 2026-10-26 (Talladega): the player's box is just before the line and its car is on track 74 m before
            // it - that first crossing is still the out lap; the timed lap starts at the next one
            {
                resetAll();
                double oldStall = me.Stall;
                me.Stall = me.D = L - 160; me.ExitAt = L - 60;
                m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
                var qe = new Qualifying { Mode = Qualifying.Format.Single, Laps = 1, ReleaseMode = Qualifying.Release.PitRoad, MaxOut = 3, FuelPercent = 25,
                    QualPace = true, DnqSeconds = 90, Order = "15, 99, 11" };
                qe.Clock = () => t; qe.Logger = q.Logger; q = qe;
                int mk = log.Count; bool qeStarted = false; int crossAtTimed = -1, crossAtDone = -1;
                for (int step = 0; step < 400000 && !q.Status.StartsWith("Done"); step++)
                {
                    t += dt;
                    m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                    m.Guard = false;
                    foreach (var c in cars) Move(m, c, t, dt);
                    MovePlayer(m, me, q, t, dt);
                    m.Guard = true;
                    q.Tick(m);
                    if (crossAtTimed < 0 && q.PlayerCall.StartsWith("TIMED LAP")) crossAtTimed = me.Laps;
                    if (crossAtDone < 0 && q.PlayerCall.StartsWith("RUN COMPLETE")) crossAtDone = me.Laps;
                    if (!qeStarted && q.CanStart) { q.Start(); qeStarted = true; }
                }
                m.Guard = false;
                var el = log.Skip(mk).ToList();
                Console.WriteLine("  box before the line: timed lap shown after crossing " + crossAtTimed + ", run complete after crossing " + crossAtDone);
                check(el.Any(l => l.Contains("#99 Sim Player came onto the track") && l.Contains("before the line")),
                      "player on track just before the line: that crossing is called its out lap");
                check(crossAtTimed == 2 && crossAtDone == 3, "player on track just before the line: timed lap from the 2nd crossing, done at the 3rd");
                bool aiSame = !el.Any(l => Regex.IsMatch(l, @"#(15|11) .*came onto the track"));
                check(aiSame, "cars that join after the line: no change");
                me.Stall = me.D = oldStall; me.ExitAt = Exit;
            }

            // live 2026-10-26 (Bristol): the player's velocity reads 0 and its pit road leaves the pit road flag for a while
            // and comes back to it before the exit - that is not 'came back to pit road on its own'
            {
                resetAll();
                me.ZeroVel = true; me.GapFrom = me.Stall + 40; me.GapTo = me.Stall + 60;
                m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
                var qb = new Qualifying { Mode = Qualifying.Format.Single, Laps = 1, ReleaseMode = Qualifying.Release.PitRoad, MaxOut = 3, FuelPercent = 25,
                    QualPace = true, DnqSeconds = 90, Order = "15, 99, 11" };
                qb.Clock = () => t; qb.Logger = q.Logger; q = qb;
                int mk = log.Count; bool qbStarted = false;
                for (int step = 0; step < 400000 && !q.Status.StartsWith("Done"); step++)
                {
                    t += dt;
                    m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                    m.Guard = false;
                    foreach (var c in cars) Move(m, c, t, dt);
                    MovePlayer(m, me, q, t, dt);
                    m.Guard = true;
                    q.Tick(m);
                    if (!qbStarted && q.CanStart) { q.Start(); qbStarted = true; }
                }
                m.Guard = false;
                var bl = log.Skip(mk).ToList();
                foreach (var l in bl.Where(l => l.Contains("your car: ") && l.Contains("m/s"))) Console.WriteLine("  " + l);
                check(!bl.Any(l => l.Contains("#99") && l.Contains("DNQ")), "player, pit road that leaves the flag and comes back (velocity 0): no DNQ");
                check(bl.Any(l => l.Contains("#99 Sim Player: laps ")), "player, pit road that leaves the flag and comes back: its lap timed");
                check(bl.Any(l => Regex.IsMatch(l, @"your car: Practicing .* (1\d|2\d)\.\d m/s")), "player speed from its lap distance (about 20 m/s along pit road, not 0)");
                me.ZeroVel = false; me.GapFrom = me.GapTo = -1;
            }

            // live 2026-10-26: back in the box the broadcast stays in the player's car until it climbs out (the game's
            // in-car flag 0xC067D4 = 3 while sitting in it), then lets go
            {
                resetAll();
                m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
                var qc = new Qualifying { Mode = Qualifying.Format.Single, Laps = 1, ReleaseMode = Qualifying.Release.PitRoad, MaxOut = 3, FuelPercent = 25,
                    QualPace = true, DnqSeconds = 90, Order = "15, 99, 11" };
                qc.Clock = () => t; qc.Logger = q.Logger; q = qc;
                int mk = log.Count; bool qcStarted = false; double boxAt = -1, outAt = -1, releasedAt = -1; bool drove = false;
                m.W32(Addr.DriverInCar, 3);
                for (int step = 0; step < 400000 && !q.Status.StartsWith("Done"); step++)
                {
                    t += dt;
                    m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                    m.Guard = false;
                    foreach (var c in cars) Move(m, c, t, dt);
                    MovePlayer(m, me, q, t, dt);
                    if (drove && boxAt < 0 && me.Mode == 0) boxAt = t;
                    if (boxAt > 0 && outAt < 0 && t > boxAt + 12) { outAt = t; m.W32(Addr.DriverInCar, 0); }   // climbs out 12 s later
                    m.Guard = true;
                    q.Tick(m);
                    if (q.DriveCar == 0 && me.Mode >= 2) drove = true;
                    if (boxAt > 0 && releasedAt < 0 && q.DriveCar != 0) releasedAt = t;
                    if (!qcStarted && q.CanStart) { q.Start(); qcStarted = true; }
                }
                m.Guard = false;
                Console.WriteLine(string.Format("  climb out: stopped in the box at {0:0.0}, climbed out at {1:0.0}, broadcast let go at {2:0.0}", boxAt, outAt, releasedAt));
                check(boxAt > 0 && outAt > 0 && releasedAt >= outAt && releasedAt < outAt + 1, "player back in the box: the broadcast stays in the car until it climbs out");
                check(log.Skip(mk).Any(l => l.Contains("IN YOUR BOX - climb out")), "player back in the box: told to climb out");
                check(!q.PlayerCall.StartsWith("IN YOUR BOX"), "player climbed out: the climb-out call is cleared");
                m.W32(Addr.DriverInCar, 0);
            }

            // knockout with the player: goes when told, comes in at the checkered
            resetAll();
            m.W8(0x977A00, 6); t += dt; q.Tick(m); m.W8(0x977A00, 4);
            var q10 = new Qualifying { Mode = Qualifying.Format.Knockout, Rounds = "3:4, 2:4", BreakSeconds = 20, FuelPercent = 0, QualPace = false };
            q10.Clock = () => t; q10.Logger = q.Logger; q = q10;
            mark = log.Count; st = false; calls.Clear(); timers.Clear(); sawDrive = false;
            m.PlayerWrites.Clear(); m.Guard = true;
            for (int step = 0; step < 600000 && !q.Status.StartsWith("Done"); step++)
            {
                t += dt;
                m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                m.Guard = false;
                foreach (var c in cars) Move(m, c, t, dt);
                MovePlayer(m, me, q, t, dt);
                m.Guard = true;
                q.Tick(m);
                if (q.PlayerCall != "" && (calls.Count == 0 || calls.Last() != q.PlayerCall)) calls.Add(q.PlayerCall);
                string klbl = q.PlayerTimer.Split('|')[0];
                if (klbl != "" && (timers.Count == 0 || timers.Last() != klbl)) timers.Add(klbl);
                if (q.DriveCar == 0) sawDrive = true;
                if (!st && q.CanStart) { q.Start(); st = true; }
            }
            m.Guard = false;
            Console.WriteLine("player calls: " + string.Join(" | ", calls.ToArray()));
            check(sawDrive && q.DriveCar == -1 && timers.Contains("TIME LEFT"), "player in knockout: drive while out, cleared in the box, time left shown");
            var kp = log.Skip(mark).ToList();
            check(m.PlayerWrites.Count == 0, "player in knockout: nothing written to the player's car");
            check(Regex.IsMatch(kp.FirstOrDefault(l => l.Contains("ROUND 1 RESULTS")) ?? "", @"#99 \d"), "player in knockout: a round 1 time");
            check(calls.Any(c => c.Contains("GO - set your best lap")) && calls.Any(c => c.Contains("CHECKERED")) && calls.Any(c => c.Contains("THROUGH TO") || c.Contains("OUT IN")),
                "player in knockout: go / checkered / through-or-out shown");
            check(q.Status.StartsWith("Done") && Regex.IsMatch(kp.FirstOrDefault(l => l.Contains("KNOCKOUT QUALIFYING GRID")) ?? "", @"#99 "), "player in knockout: on the grid");

            System.IO.File.Delete(Qualifying.RunsPath);
            Console.WriteLine(fails == 0 ? "QualSim: all checks passed" : "QualSim: " + fails + " check(s) FAILED");
            return fails == 0 ? 0 : 1;
        }


        // ---- the checks added 2026-11-03: true DNQs, Happy Hour holds, Indy oval re-qualifying lanes and wave-offs, and the
        // review fixes (pause clocks, the final state file, closing mid-qualifying, a late player in a knockout) ----
        static int Extra()
        {
            var m = new FakeMem();
            double t = 0;
            const double dt = 0.05;
            m.W8(0x977A00, 4); m.W8(Addr.SessionNum, 0); m.W8(Addr.NumSessions, 4); m.W16(0xBEBDA3 + 8, 5); m.W16(0xBEBDA3 + 16, 4); m.W16(0xBEBDA3 + 24, 7);
            m.W32(Addr.TickRateFlag, 1); m.WD(Addr.TrackLength, L); m.WS(Addr.TrackName, "simtrack");
            Qualifying.RunsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc_qualsim_runs.txt");
            Qualifying.StateDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc_qualsim_state");
            if (System.IO.File.Exists(Qualifying.RunsPath)) System.IO.File.Delete(Qualifying.RunsPath);
            string statePath = System.IO.Path.Combine(Qualifying.StateDir, "qualifying_state.txt");
            var cars = new List<Car>();
            for (int i = 0; i < 8; i++)
            {
                var c = new Car { Slot = 2 + i, Num = 10 + i, Stall = (1650 + i * 85) % L, Speed = 51.0 - 0.2 * i };
                c.D = c.Stall;
                cars.Add(c);
                m.WS(Driver0 + c.Slot * 175, "Sim"); m.WS(Driver0 + c.Slot * 175 + 16, "Driver" + c.Num); m.W32(Driver0 + c.Slot * 175 + 156, c.Num);
                m.W32(Block0 + c.Slot * Stride + Countdown, 10000000);
                m.WF(Block0 + c.Slot * Stride + 5914, (float)(1.30 - 0.05 * i));        // qualifying rating: #10 best ... #17 worst
            }
            var log = new List<string>();
            Action<string> logger = s => { string line = t.ToString("0000.0", CultureInfo.InvariantCulture) + " " + s.Substring(9); log.Add(line); Console.WriteLine(line); };
            int fails = 0;
            Action<bool, string> check = (ok, what) => { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; };
            Action reset = () =>
            {
                foreach (var c in cars)
                {
                    for (int i = 0; i <= c.Laps + 1; i++) m.W32(Cross0 + c.Slot * 2048 + i * 4, 0);
                    c.Laps = 0; c.St = Wait; c.D = c.Stall; c.Wait = -1; c.Crosses = 0; c.AbortAfterCrossings = -1; c.RunSpeeds = null;
                    m.W32(Block0 + c.Slot * Stride + Countdown, 10000000); m.W32(Block0 + c.Slot * Stride + RunEnd, 0);
                    m.WF(Block0 + c.Slot * Stride + 6634, 0f);
                }
                m.W8(Addr.SimState, 2);
            };
            Action<int, int> sessionNo = (no, kind) => { m.W8(Addr.SessionNum, (byte)no); m.W8(0x977A00, (byte)kind); };
            Func<Qualifying> fresh = null;
            Qualifying q = null;
            Action<Qualifying> use = nq => { nq.Clock = () => t; nq.Logger = logger; q = nq; sessionNo(0, 6); t += dt; q.Tick(m); sessionNo(0, 4); };
            Exception thrown = null;
            Func<Func<bool>, int, bool, bool> run = (until, maxSteps, startIt) =>
            {
                bool st = false;
                for (int step = 0; step < maxSteps; step++)
                {
                    if (until()) return true;
                    t += dt;
                    m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                    foreach (var c in cars) Move(m, c, t, dt);
                    try { q.Tick(m); } catch (Exception e) { if (thrown == null) thrown = e; Console.WriteLine("EXCEPTION " + e); }
                    if (startIt && !st && q.CanStart) { q.Start(); st = true; }
                }
                return until();
            };
            Func<string, int> count = w => log.Count(l => l.Contains(w));

            // 1. single car, 6 of 8 make the field: the two slowest are DNQs, saved, marked, and held through Happy Hour
            reset();
            use(new Qualifying { Mode = Qualifying.Format.Single, Laps = 1, ReleaseMode = Qualifying.Release.Gap, ReleaseValue = 8, MaxOut = 3, FuelPercent = 0, QualPace = false, Starters = 6 });
            int mark = log.Count;
            run(() => q.Status.StartsWith("Done"), 400000, true);
            var dnqLine = log.Skip(mark).FirstOrDefault(l => l.Contains("DID NOT QUALIFY (6 of 8 cars make the field)")) ?? "";
            check(dnqLine.Contains("#16") && dnqLine.Contains("#17") && !dnqLine.Contains("#15"), "DNQ: 6 of 8 make the field, #16 and #17 (the slowest) don't");
            check(q.NotQualified.Count == 2, "DNQ: two cars on the DNQ list");
            var res = log.Skip(mark).FirstOrDefault(l => l.Contains("SINGLE CAR QUALIFYING:")) ?? "";
            check(res.Contains("---- did not qualify ----") && Regex.Matches(res, @"  DNQ").Count == 2, "DNQ: marked on the results board");
            string saved = System.IO.File.Exists(Qualifying.RunsPath) ? System.IO.File.ReadAllText(Qualifying.RunsPath) : "";
            check(saved.Contains("\ndnq 16 17\n"), "DNQ: saved with the times (for a restarted Race Control)");
            string stTxt = System.IO.File.Exists(statePath) ? System.IO.File.ReadAllText(statePath) : "";
            check(stTxt.Contains("\ndnq 8 9\n"), "DNQ: in the state file (slots 8 9)");
            // the qualifying session, then Happy Hour (a practice with the race next)
            sessionNo(1, 5); for (double end = t + 3; t < end; t += dt) { m.W32(Addr.GameTicks, (int)Math.Round(t * 36)); q.Tick(m); }
            sessionNo(2, 4);
            reset();
            foreach (var c in cars) m.W32(Block0 + c.Slot * Stride + Countdown, 300);      // the game's own practice countdowns
            cars[0].St = Track; m.W32(Block0 + cars[0].Slot * Stride + RunEnd, 99); m.W32(Block0 + cars[0].Slot * Stride + State, unchecked((int)Track));         // #10 practicing (not a DNQ: left alone)
            cars[7].St = Track; m.W32(Block0 + cars[7].Slot * Stride + RunEnd, 99); m.W32(Block0 + cars[7].Slot * Stride + State, unchecked((int)Track));         // #17 practicing (a DNQ: called in)
            mark = log.Count;
            for (double end = t + 3; t < end; t += dt) { m.W32(Addr.GameTicks, (int)Math.Round(t * 36)); q.Tick(m); }
            check(log.Skip(mark).Any(l => l.Contains("Happy Hour: the cars that didn't qualify stay in their boxes")), "Happy Hour: the DNQ hold is logged");
            check(m.I32(Block0 + cars[6].Slot * Stride + Countdown) == 10000000 && m.I32(Block0 + cars[2].Slot * Stride + Countdown) == 300,
                "Happy Hour: DNQ #16 held in its box, #12 (qualified) left alone");
            check(m.I32(Block0 + cars[7].Slot * Stride + RunEnd) <= m.I32(Block0 + cars[7].Slot * Stride + Lap) && m.I32(Block0 + cars[0].Slot * Stride + RunEnd) == 99,
                "Happy Hour: DNQ #17 on track called in, #10 left alone");
            // a Race Control restarted in Happy Hour gets the DNQs from the saved file
            use(new Qualifying { Mode = Qualifying.Format.Single, Starters = 6 }); sessionNo(2, 4);
            foreach (var c in cars) m.W32(Block0 + c.Slot * Stride + Countdown, 300);
            mark = log.Count;
            for (double end = t + 3; t < end; t += dt) { m.W32(Addr.GameTicks, (int)Math.Round(t * 36)); q.Tick(m); }
            check(log.Skip(mark).Any(l => l.Contains("the DNQs from the qualifying run saved")) && m.I32(Block0 + cars[6].Slot * Stride + Countdown) == 10000000,
                "Happy Hour: a restarted Race Control loads the DNQs and holds them");
            sessionNo(3, 7); mark = log.Count;
            for (double end = t + 2; t < end; t += dt) q.Tick(m);
            check(log.Skip(mark).Any(l => l.Contains("the DNQ cars #16 #17 are still in the field")), "race: the DNQs are named as still in the field (from the back)");
            // the race again, before the green, with a restarted Race Control: the DNQs (from the saved file) are taken out -
            // engine damage past the must-pit line, called in, a tow asked for when the pace car pulls off
            {
                const int EngineDmg = 0x1327 + 0x58, Stuck = 0x1B2E, PitReq = 8118; const uint Done = 0xBCF7F4;
                foreach (var c in cars)
                {
                    m.W8(Addr.HasSpawned + c.Slot, 1); m.W8(Addr.HasNotRetired + c.Slot, 1);
                    m.W32(Block0 + c.Slot * Stride + State, unchecked((int)Track)); m.WF(Block0 + c.Slot * Stride + EngineDmg, 0.05f);
                }
                m.W32(Addr.LapsComplete, -1); m.W8(Addr.FlagState, 3); m.W8(Addr.SimState, 2);
                use(new Qualifying { Mode = Qualifying.Format.Single, Starters = 6 }); sessionNo(3, 7); mark = log.Count;
                Action race = () => { for (double end = t + 2; t < end; t += dt) { m.W32(Addr.GameTicks, (int)Math.Round(t * 36)); q.Tick(m); } };
                race();
                Func<int, int, float> f = (slot, off) => m.F32(Block0 + slot * Stride + off);
                check(log.Skip(mark).Any(l => l.Contains("taking the DNQ cars out before the green: #16 #17")), "race start: a restarted Race Control loads the DNQs and starts taking them out");
                check(Math.Abs(f(8, EngineDmg) - 0.95f) < 1e-4 && Math.Abs(f(9, EngineDmg) - 0.95f) < 1e-4 && Math.Abs(f(7, EngineDmg) - 0.05f) < 1e-4,
                    "race start: DNQ #16 #17 engine damage 0.95, #15 (qualified) untouched");
                check(m.U8(Block0 + 8 * Stride + PitReq) == 1 && m.U8(Block0 + 9 * Stride + PitReq) == 1 && m.U8(Block0 + 7 * Stride + PitReq) == 0,
                    "race start: DNQ cars called in, #15 not");
                check(f(8, Stuck) == 0 && f(9, Stuck) == 0, "race start: no tow while the pace car is still out (and < 20 s)");
                m.WF(Block0 + 8 * Stride + EngineDmg, 0.30f);                                 // the game rates it back: written again
                race();
                check(Math.Abs(f(8, EngineDmg) - 0.95f) < 1e-4 && log.Skip(mark).Count(l => l.Contains("#16 (DNQ): engine damage back to 0.30")) == 1,
                    "race start: engine damage set back by the game is written again (logged once)");
                m.W32(Block0 + 8 * Stride + State, unchecked((int)Done)); race();          // #16 retires in its stall
                check(log.Skip(mark).Any(l => l.Contains("#16 (DNQ) is out of the race before the green")), "race start: #16 retired before the green");
                m.W8(Addr.FlagState, 5); race();                                             // one to go: #17 still circulating
                check(Math.Abs(f(9, Stuck) - 13f) < 1e-4 && f(8, Stuck) == 0 && log.Skip(mark).Any(l => l.Contains("#17 (DNQ): still on track") && l.Contains("pace car pulls off")),
                    "race start: #17 still out when the pace car pulls off - a tow asked for (#16 already out: nothing)");
                m.W32(Addr.LapsComplete, 0); m.W8(Addr.FlagState, 7); m.WF(Block0 + 9 * Stride + Stuck, 0f); race();
                check(log.Skip(mark).Any(l => l.Contains("green flag: DNQ cars out of the race 1 of 2; still running (from the back, damaged): #17")), "race: green flag summary");
                check(f(9, Stuck) == 0, "race: nothing written after the green");
                m.W8(Addr.HasNotRetired + 9, 0); race();
                check(log.Skip(mark).Any(l => l.Contains("#17 (DNQ) is out of the race after the green")) && log.Skip(mark).Any(l => l.Contains("DNQ cars: all out - 2 out of the race")),
                    "race: #17 retires after the green, all out");
                check(q.Status != null && !q.Status.StartsWith("Race start"), "race: the DNQ watch ends");
                foreach (var c in cars) { m.W8(Addr.HasSpawned + c.Slot, 0); m.W8(Addr.HasNotRetired + c.Slot, 0); m.W32(Block0 + c.Slot * Stride + State, unchecked((int)Wait)); }
                m.W32(Addr.LapsComplete, 0); m.W8(Addr.FlagState, 0);
            }
            sessionNo(0, 4);

            // 2. knockout with a field cut: the grid's last two are DNQs
            reset();
            use(new Qualifying { Mode = Qualifying.Format.Knockout, Rounds = "2:4, 2:4", BreakSeconds = 10, FuelPercent = 0, QualPace = false, Starters = 75, StartersPercent = true });
            mark = log.Count;
            run(() => q.Status.StartsWith("Done"), 600000, true);
            check(log.Skip(mark).Any(l => l.Contains("DID NOT QUALIFY (6 of 8 cars make the field, 75%)")), "DNQ: knockout, 75% of 8 = 6 make the field");

            // 3. Indy oval with the lanes: 6 of 8 make the field, 15 minutes of re-qualifying, 3 runs a car, wave-offs
            reset();
            cars[1].RunSpeeds = new Queue<double>(new[] { 48.6, 50.8, 50.8 });   // #11: a slow first run (well off its rating) - waves off or is bumped
            cars[6].RunSpeeds = new Queue<double>(new[] { 49.8, 50.5, 50.5 });   // #16: bumped, then fast in Lane 1
            cars[4].RunSpeeds = new Queue<double>(new[] { 50.2, 50.3, 50.3 });   // #14: on the bubble, a little faster in Lane 2
            use(new Qualifying { Mode = Qualifying.Format.IndyOval, Laps = 4, ReleaseMode = Qualifying.Release.Gap, ReleaseValue = 8, MaxOut = 3, FuelPercent = 0, QualPace = false,
                Starters = 6, ReQual = true, ReQualMinutes = 15, ReQualMaxRuns = 3, WaveOffs = true, RandomSeed = 7 });
            mark = log.Count;
            bool mixed = false;
            run(() =>
            {
                if (q.Status.Contains("(lane 1)") && q.Status.Contains("(lane 2)")) mixed = true;
                return q.Status.StartsWith("Done");
            }, 1200000, true);
            var lz = log.Skip(mark).ToList();
            int open = lz.FindIndex(l => l.Contains("RE-QUALIFYING OPEN"));
            check(open >= 0 && lz.Take(open).Count(l => l.Contains("rolls off pit road")) == 8, "lanes: open once all 8 first runs are done");
            string openLine = open >= 0 ? lz[open] : "";
            string l1 = Regex.Match(openLine, @"Lane 1 \(priority, times given up\): ([^;]*);").Groups[1].Value;
            check(l1.Contains("#17") && (l1.Contains("#16") || l1.Contains("#11")), "lanes: the bumped cars go to Lane 1 (" + l1 + ")");
            // every Lane 1 car at the open either had no time (waved off / none) or gave its time up
            check(Regex.Matches(l1, @"#(\d+)").Cast<Match>().All(x => lz.Take(open + 1).Any(l => l.Contains("#" + x.Groups[1].Value + " Sim Driver" + x.Groups[1].Value) &&
                (l.Contains("gives up its time") || l.Contains("waved off its first run") || l.Contains(" DNQ")))), "lanes: each Lane 1 car had no time or gave its time up");
            check(!mixed, "lanes: never a Lane 2 car out with a Lane 1 car");
            check(lz.Any(l => l.Contains("RE-QUALIFYING CLOSED")) && q.Status.StartsWith("Done"), "lanes: closed, then done");
            check(cars.All(c => lz.Count(l => l.Contains("#" + c.Num + " Sim Driver" + c.Num + " rolls off pit road")) <= 3), "lanes: no car runs more than 3 times");
            check(lz.Count(l => l.Contains("re-qualifying: #") && l.Contains(" goes from Lane")) >= 2, "lanes: re-qualifying runs were made (" + lz.Count(l => l.Contains(" goes from Lane")) + ")");
            check(lz.Any(l => l.Contains("WAVES OFF")), "lanes: an AI team waved off a run");
            var board = lz.LastOrDefault(l => l.Contains("INDY OVAL QUALIFYING (4-LAP AVERAGE)")) ?? "";
            var rows = board.Split('\n').Skip(1).Select(l => Regex.Match(l, @"#(\d+)").Groups[1].Value).Where(n => n != "").ToList();
            check(rows.Count == 8 && rows.Distinct().Count() == 8, "lanes: every car once on the results (" + rows.Count + ")");
            check(lz.Any(l => l.Contains("DID NOT QUALIFY (6 of 8")), "lanes: the field cut applied after re-qualifying");
            var finalSaved = System.IO.File.ReadAllLines(Qualifying.RunsPath).Where(l => Regex.IsMatch(l, @"^\d+ ")).ToList();
            check(finalSaved.All(l => { var v = l.Split(' ')[1].Split(','); return v.Length == 4 && v.Distinct().Count() == 1; }), "lanes: each car's grid laps are 4 x its standing average");
            check(q.ChooseLane(1).Contains("not open"), "lanes: picking a lane when they are closed is refused");
            // ... again without wave-offs: the bumped car has a time, so it gives it up for Lane 1
            reset();
            cars[1].RunSpeeds = new Queue<double>(new[] { 48.6, 50.8, 50.8 });
            use(new Qualifying { Mode = Qualifying.Format.IndyOval, Laps = 4, ReleaseMode = Qualifying.Release.Gap, ReleaseValue = 8, MaxOut = 3, FuelPercent = 0, QualPace = false,
                Starters = 6, ReQual = true, ReQualMinutes = 6, ReQualMaxRuns = 2, WaveOffs = false, RandomSeed = 3 });
            mark = log.Count;
            run(() => q.Status.StartsWith("Done"), 1200000, true);
            lz = log.Skip(mark).ToList();
            check(lz.Any(l => l.Contains("#11 Sim Driver11 gives up its time")) && !lz.Any(l => l.Contains("WAVES OFF")), "lanes, no wave-offs: bumped #11 gives up its time for Lane 1");
            check(lz.Any(l => l.Contains("#11 Sim Driver11: Lane 1 run stands")) && q.Status.StartsWith("Done"), "lanes, no wave-offs: its Lane 1 run stands");

            // ... with the player: never put in a lane it didn't pick; picks Lane 1 once the lanes open, runs, its Lane 1 run stands
            Func<bool, bool> playerLanes = pick =>
            {
                reset();
                var pme = new Player { D = (1600 + 85 * 8) % L };
                pme.Stall = pme.D;
                m.WS(Driver0, "Sim"); m.WS(Driver0 + 16, "Player"); m.W32(Driver0 + 156, 99); m.W8(Addr.HasSpawned, 1);
                for (int i = 0; i < 64; i++) m.W32(Cross0 + i * 4, 0);
                use(new Qualifying { Mode = Qualifying.Format.IndyOval, Laps = 4, ReleaseMode = Qualifying.Release.Gap, ReleaseValue = 8, MaxOut = 3, FuelPercent = 0, QualPace = false,
                    Starters = 6, ReQual = true, ReQualMinutes = 6, ReQualMaxRuns = 2, WaveOffs = true, RandomSeed = 5, PlayerReadyPause = false, DnqSeconds = 90 });
                int mk = log.Count; bool st = false, picked = false;
                m.PlayerWrites.Clear(); m.Guard = true;
                for (int step = 0; step < 1500000 && !q.Status.StartsWith("Done"); step++)
                {
                    t += dt; m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                    m.Guard = false;
                    foreach (var c in cars) Move(m, c, t, dt);
                    MovePlayer(m, pme, q, t, dt);
                    m.Guard = true;
                    try { q.Tick(m); } catch (Exception e) { if (thrown == null) thrown = e; Console.WriteLine("EXCEPTION " + e); break; }
                    if (!st && q.CanStart) { q.Start(); st = true; }
                    if (pick && !picked && q.LanesOpen && q.PlayerCanPickLane) { Console.WriteLine("  player: " + q.ChooseLane(1)); picked = true; }
                    if (!pick && q.LanesOpen && q.PlayerCanPickLane && !picked) { Console.WriteLine("  player: " + q.ChooseLane(0)); picked = true; }   // done with the lanes
                }
                m.Guard = false; m.W8(Addr.HasSpawned, 0);
                var pz = log.Skip(mk).ToList();
                if (pick)
                {
                    check(pz.Any(l => l.Contains("#99 Sim Player goes from Lane 1")) && pz.Any(l => l.Contains("LANE 1: YOUR RUN: GO NOW")), "lanes, player: picked Lane 1 and was sent out from it");
                    check(pz.Any(l => l.Contains("#99 Sim Player: Lane 1 run stands")), "lanes, player: its Lane 1 run stands");
                    check(m.PlayerWrites.Count == 0, "lanes, player: nothing written to the player's car (" + m.PlayerWrites.Count + ")");
                }
                else check(!pz.Any(l => l.Contains("#99 Sim Player goes from Lane")) && pz.Count(l => l.Contains("#99 Sim Player rolls off")) == 1, "lanes, player: never sent from a lane it didn't pick");
                return q.Status.StartsWith("Done");
            };
            thrown = null;
            check(playerLanes(false) && playerLanes(true) && thrown == null, "lanes, player: both runs done without errors");

            // 4. review fix: a knockout round clock stands while the game is paused (the AI velocity still reads moving)
            reset();
            use(new Qualifying { Mode = Qualifying.Format.Knockout, Rounds = "3:4, 2:4", BreakSeconds = 10, FuelPercent = 0, QualPace = false });
            run(() => q.LiveHeader.StartsWith("ROUND 1  -  2:2"), 400000, true);
            string before = q.LiveHeader;
            m.W8(Addr.SimState, 3);
            foreach (var c in cars) m.WF(Block0 + c.Slot * Stride + 6634, 50f);
            for (double end = t + 60; t < end; t += dt) q.Tick(m);                      // 60 s paused: no move, no game clock
            string after = q.LiveHeader;
            m.W8(Addr.SimState, 2);
            foreach (var c in cars) m.WF(Block0 + c.Slot * Stride + 6634, 0f);
            check(before == after, "fix: the knockout round clock stands while paused (" + before + " / " + after + ")");
            run(() => q.Status.StartsWith("Done"), 600000, false);

            // 5. review fix: a single car run paused for 11 minutes isn't ended by the 600 s cap
            reset();
            use(new Qualifying { Mode = Qualifying.Format.Single, Laps = 1, ReleaseMode = Qualifying.Release.PitRoad, MaxOut = 1, FuelPercent = 0, QualPace = false });
            mark = log.Count;
            run(() => q.Status.Contains("timed lap 1 of 1"), 400000, true);
            m.W8(Addr.SimState, 3);
            for (double end = t + 660; t < end; t += dt) q.Tick(m);
            m.W8(Addr.SimState, 2);
            run(() => log.Skip(mark).Any(l => l.Contains(": laps ") || l.Contains("no lap time read")), 100000, false);
            check(log.Skip(mark).Any(l => l.Contains(": laps ")) && !log.Skip(mark).Any(l => l.Contains("no lap time read")), "fix: a long pause doesn't end a run (its lap is timed)");

            // 6. review fix: Stop during a run writes a final state (no one on track, no focus, nobody driving)
            run(() => q.Status.Contains("out lap") || q.Status.Contains("timed lap"), 100000, false);
            q.Stop();
            stTxt = System.IO.File.ReadAllText(statePath);
            check(stTxt.Contains("phase done\n") && stTxt.Contains("\non_track \n") && stTxt.Contains("\nfocus -1\n") && stTxt.Contains("\ndrive -1\n"),
                "fix: Stop writes a final state file (done, nobody on track, no focus)");

            // 7. review fix: closing Race Control while the cars are held lets them go
            reset();
            use(new Qualifying { Mode = Qualifying.Format.Single, Laps = 1, FuelPercent = 0, QualPace = false });
            for (double end = t + 3; t < end; t += dt) { m.W32(Addr.GameTicks, (int)Math.Round(t * 36)); foreach (var c in cars) Move(m, c, t, dt); q.Tick(m); }
            foreach (var c in cars) m.W32(Block0 + c.Slot * Stride + Countdown, 5000);     // the game counting down
            q.Tick(m);
            bool heldBefore = cars.All(c => m.I32(Block0 + c.Slot * Stride + Countdown) == 10000000);
            q.Shutdown();
            check(heldBefore && cars.All(c => m.I32(Block0 + c.Slot * Stride + Countdown) < 5000000), "fix: closing Race Control lets the held cars go");

            // 8. review fix: the player joining a knockout round after its checkered (stale speed: leaving) doesn't throw
            reset();
            var me = new Player { D = (1600 + 85 * 8) % L };
            me.Stall = me.D;
            m.WS(Driver0, "Sim"); m.WS(Driver0 + 16, "Player"); m.W32(Driver0 + 156, 99); m.W8(Addr.HasSpawned, 0);
            use(new Qualifying { Mode = Qualifying.Format.Knockout, Rounds = "2:4, 2:4", BreakSeconds = 10, FuelPercent = 0, QualPace = false });
            thrown = null;
            run(() => q.LiveHeader.Contains("CHECKERED"), 400000, true);
            m.W8(Addr.HasSpawned, 1); me.Mode = 1;                                         // spawns on pit road, moving
            for (int i = 0; i < 400; i++)
            {
                t += dt; m.W32(Addr.GameTicks, (int)Math.Round(t * 36));
                foreach (var c in cars) Move(m, c, t, dt);
                MovePlayer(m, me, q, t, dt);
                try { q.Tick(m); } catch (Exception e) { if (thrown == null) thrown = e; }
            }
            check(thrown == null, "fix: a player joining after the checkered doesn't throw" + (thrown != null ? " (" + thrown.GetType().Name + ")" : ""));
            m.W8(Addr.HasSpawned, 0);

            System.IO.File.Delete(Qualifying.RunsPath);
            Console.WriteLine(fails == 0 ? "QualSim extra: all checks passed" : "QualSim extra: " + fails + " check(s) FAILED");
            return fails;
        }

        static void Move(FakeMem m, Car c, double t, double dt)
        {
            long b = Block0 + c.Slot * Stride;
            int countdown = m.I32(b + Countdown), runEnd = m.I32(b + RunEnd);
            double before = c.D;
            switch (c.St)
            {
                case Wait:
                    // the game counts a short stall countdown down (knockout staggers its releases with 1, 9, 17, ...)
                    if (countdown > 1 && countdown < 1000) m.W32(b + Countdown, countdown = countdown - 1);
                    if (countdown <= 1 && c.Wait < 0) c.Wait = t + 1.0;              // a second to pull out
                    if (c.Wait >= 0 && t >= c.Wait)
                    {
                        if (countdown <= 1) { c.St = Leaving; if (c.RunSpeeds != null && c.RunSpeeds.Count > 0) c.Speed = c.RunSpeeds.Dequeue(); }
                        c.Wait = -1;
                    }
                    break;
                case Leaving:
                    c.D = (c.D + PitV * dt) % L;
                    if (Ahead(before, Exit) < PitV * dt) { c.D = Exit; c.St = Track; c.Act = t; }
                    break;
                case Track:
                    c.D = (c.D + c.Speed * dt) % L;
                    if (c.Laps >= runEnd && runEnd > 0) c.St = PitIn;
                    if (c.AbortAfterCrossings >= 0 && c.Crosses >= c.AbortAfterCrossings && (c.AbortAfterCrossings > 0 || t - c.Act > 5) && Ahead(c.D, Entry) < 1500)
                        c.St = PitIn;
                    break;
                case PitIn:
                    c.D = (c.D + c.Speed * dt) % L;
                    if (Ahead(before, Entry) < c.Speed * dt) { c.D = Entry; c.St = Entering; }
                    break;
                case Entering:
                    c.D = (c.D + PitV * dt) % L;
                    if (Ahead(before, c.Stall) < PitV * dt) { c.D = c.Stall; c.St = Wait; c.Wait = -1; c.Crosses = 0; c.AbortAfterCrossings = -1; }
                    break;
            }
            if (c.St != Wait && before > L / 2 && c.D < L / 2)                  // over the line
            {
                int ms = (int)Math.Round(t * 1000) & 0x0FFFFFFF;
                m.W32(Cross0 + c.Slot * 2048 + c.Laps * 4, ms);
                c.Laps++; c.Crosses++;
            }
            m.W32(b + State, unchecked((int)c.St));
            m.W32(b + Lap, c.Laps);
            m.WF(Addr.Dlong + c.Slot * Addr.Ocd, (float)c.D);
            double a = c.D / L * 2 * Math.PI, r = L / (2 * Math.PI);
            m.WF(b + PosX, (float)(r * Math.Cos(a))); m.WF(b + PosX + 4, (float)(r * Math.Sin(a)));
        }

        static double Ahead(double from, double to) { double d = (to - from) % L; return d < 0 ? d + L : d; }

        // The player: sits in its box until a GO call, a second later pulls out; drives until told to come in (or the
        // checkered), then stops in its box. Only what the game shows for a player's car: pit road, speed, position.
        class Player { public double D, Stall, GoAt = -1, Delay = 1.0, ExitAt = Exit, GapFrom = -1, GapTo = -1; public int Mode, Laps; public bool StuckPitFlag, ZeroVel; }      // Mode 0 box, 1 leaving, 2 track, 3 in-lap, 4 entering
        static void MovePlayer(FakeMem m, Player p, Qualifying q, double t, double dt)
        {
            double before = p.D, v = 0;
            bool go = q.PlayerCallKind == 1, comeIn = q.PlayerCallKind == 2 || q.PlayerCallKind == 3;
            switch (p.Mode)
            {
                case 0:
                    if (go && p.GoAt < 0) p.GoAt = t + p.Delay;
                    if (p.GoAt >= 0 && t >= p.GoAt) { p.Mode = 1; p.GoAt = -1; }
                    break;
                case 1:
                    v = PitV; p.D = (p.D + v * dt) % L;
                    if (Ahead(before, p.ExitAt) < v * dt) { p.D = p.ExitAt; p.Mode = 2; }
                    break;
                case 2:
                    v = TrackV + 0.2; p.D = (p.D + v * dt) % L;
                    if (comeIn) p.Mode = 3;
                    break;
                case 3:
                    v = TrackV; p.D = (p.D + v * dt) % L;
                    if (Ahead(before, Entry) < v * dt) { p.D = Entry; p.Mode = 4; }
                    break;
                case 4:
                    v = PitV; p.D = (p.D + v * dt) % L;
                    if (Ahead(before, p.Stall) < v * dt) { p.D = p.Stall; p.Mode = 0; v = 0; }
                    break;
            }
            if (p.Mode != 0 && before > L / 2 && p.D < L / 2)
            {
                m.W32(Cross0 + p.Laps * 4, (int)Math.Round(t * 1000) & 0x0FFFFFFF);
                p.Laps++;
            }
            long b = Block0;
            bool gap = p.Mode == 1 && p.GapFrom >= 0 && p.D >= p.GapFrom && p.D < p.GapTo;     // a stretch of pit road off pit road
            m.W8(Addr.OnPitRoad, (byte)(p.StuckPitFlag || ((p.Mode == 0 || p.Mode == 1 || p.Mode == 4) && !gap) ? 1 : 0));
            m.WF(b + 6634, p.ZeroVel ? 0f : (float)v); m.WF(b + 6638, 0f);
            m.W32(b + Lap, p.Laps);
            m.WF(Addr.Dlong, (float)p.D);
            double a = p.D / L * 2 * Math.PI, r = L / (2 * Math.PI);
            m.WF(b + PosX, (float)(r * Math.Cos(a))); m.WF(b + PosX + 4, (float)(r * Math.Sin(a)));
        }
    }
}
