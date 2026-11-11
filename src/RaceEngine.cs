using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace RaceControl
{
    // Everything Race Control does to a running NR2003 race, ticked every 10 ms:
    //   RaceEngine.Cautions.cs  race back to the line, green-white-checkered, collisions, throwing a caution
    //   RaceEngine.Wave.cs      pit road open to all, wave around, lap settle after the green, penalties
    //   RaceEngine.Lineup.cs    lapped cars to the back, double file / choose cone, untangling stuck cars
    public partial class RaceEngine
    {
        public Action<string> Logger;       // receives finished log lines

        // Game time in seconds: real time that only runs while the race is running (not paused). The session
        // clock in memory was seen standing still for minutes during a caution, so it is not used for timing.
        public Func<double> RealClock;
        readonly Stopwatch watch = Stopwatch.StartNew();
        double clock, lastReal = double.NaN, lastSession = double.NaN;
        double clockStill;                    // real seconds the game clock has not moved
        const double ClockStillMax = 1.0;
        double accelGame, accelReal;
        bool accelLogged;
        double Now { get { return clock; } }

        class Car
        {
            public int Idx, Num;
            public double Prog, Gap, Down;
            public bool Ahead, Stuck, WaveOk;
        }

        readonly IGameMemory m;

        // race state read each tick
        int flag = 7, lap, maxIdx, numSessions;
        public int Flag { get { return flag; } }
        public int Lap { get { return lap; } }
        public bool InRace { get; private set; }
        public bool Paused { get { return m.U8(Addr.SimState) == 3; } }

        // The player's car position on the lap (metres from the line), NaN when nobody is driving.
        public double PlayerTrackPos()
        {
            try { return Running(Addr.PlayerIdx) && CarNumber(Addr.PlayerIdx) > 0 ? DlongOf(Addr.PlayerIdx) : double.NaN; }
            catch (GameMemoryException) { return double.NaN; }
        }
        public string TrackName { get; private set; }

        bool memoryErrorLogged;
        // Is NR2003 still running? A read that fails because the game has exited is not logged as a memory error:
        // the form reports the exit (and whether it was a crash) on its own.
        public Func<bool> GameAlive;
        public string Where { get { return "lap " + lap + " flag " + flag; } }

        public RaceEngine(IGameMemory memory)
        {
            m = memory;
            RealClock = () => watch.Elapsed.TotalSeconds;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F2(double v) { return v.ToString("0.00", Inv); }

        void Log(string msg)
        {
            if (Logger != null) Logger(DateTime.Now.ToString("HH:mm:ss", Inv) + " lap " + lap + " flag " + flag + ": " + msg);
        }

        static double Mod(double a, double b) { double r = a % b; return r < 0 ? r + b : r; }

        int CarNumber(int idx) { return m.U16(Addr.CarNum + idx * Addr.Ocd); }
        int LapsOf(int idx) { return m.U16(Addr.Laps + idx * Addr.Ocd); }
        float DlongOf(int idx) { return m.F32(Addr.Dlong + idx * Addr.Ocd); }
        float DlatOf(int idx) { return m.F32(Addr.Dlat + idx * Addr.Ocd); }
        bool OnPitRoad(int idx) { return m.U8(Addr.OnPitRoad + idx) == 1; }
        bool Running(int idx) { return m.U8(Addr.HasNotRetired + idx) == 1 && m.U8(Addr.HasSpawned + idx) == 1; }
        int ScoredLaps(int idx) { return m.I32(Addr.StandingsCache + idx * 16); }

        public void Tick()
        {
            try
            {
                if (!ReadRaceState()) return;
                RaceTick();
                memoryErrorLogged = false;
            }
            catch (GameMemoryException e)
            {
                if (!memoryErrorLogged && (GameAlive == null || GameAlive())) Log("memory error: " + e.Message);
                memoryErrorLogged = true;
            }
        }

        // A running (not paused) race, past the initial pace lap.
        bool ReadRaceState()
        {
            byte sim = m.U8(Addr.SimState);
            double real = RealClock();
            // Race Control's waits (seconds) follow the game: under time acceleration the session clock runs faster
            // than real time. It has also been seen standing still for minutes, so real time is the floor.
            double session = Addr.GameSeconds(m);
            // The game clock moves in whole ticks (1/36 s) and Race Control polls every 10-15 ms, so most polls see
            // no tick and the next sees one or two: taking the larger of the two clocks on each poll (3.2.2 and
            // before) ran Race Control's clock about 1.4x fast at 1x (Talladega 2026-11-08: "held 45 s" in 33 s)
            // and read the leader at 12 m/s when it was doing 35-42. The game clock is used whenever it moves;
            // real time only once it has stood still for over a second.
            if (sim == 2 && !double.IsNaN(lastReal))
            {
                double dr = real - lastReal, ds = session - lastSession;
                if (ds > 0 && ds < dr * 100 + 0.05) { clock += ds; clockStill = 0; }
                else if (ds == 0)
                {
                    clockStill += dr;
                    if (clockStill > ClockStillMax) { clock += clockStill; clockStill = 0; }
                }
                else clock += dr;                                 // a jump or a reset: real time for this poll
                WatchAcceleration(dr, ds);
            }
            lastReal = real;
            lastSession = session;
            int laps = m.I32(Addr.LapsComplete);
            numSessions = m.U8(Addr.NumSessions);
            bool race = m.U8(Addr.SessionNum) + 1 == numSessions;
            InRace = (sim == 2 || sim == 3) && race && laps != -1;
            if (sim == 2 || sim == 3)
            {
                string t = m.Str(Addr.TrackName, 32);
                if (!string.IsNullOrEmpty(TrackName) && t != TrackName) NewTrack(TrackName, t);
                TrackName = t;
            }
            if (sim == 2 && race && laps == -1)
            {
                lap = 0;
                flag = m.U8(Addr.FlagState);
                maxIdx = Math.Min(m.I32(Addr.NumCars), 64) - 1;
                PreStartTick();
                ManageCollisions();          // pit road starts: cars leaving their stalls pass through each other
                return false;
            }
            // practice, qualifying, warm-up: no contact on pit road there either (a tester had two AI cars wreck each
            // other on pit road in practice; the race code below never ran outside the race)
            // (with GhostOnPitRoad off this still runs: a car ghosted before it was switched off gets its contact back)
            if (sim == 2 && !race)
            {
                flag = 7;                    // nothing paces outside a race
                maxIdx = Math.Min(m.I32(Addr.NumCars), 64) - 1;
                ManageCollisions();
                return false;
            }
            if (laps != -1) startFlagsSeen.Clear();
            if (sim != 2 || !race || laps == -1) return false;
            lap = laps + 1;
            flag = m.U8(Addr.FlagState);
            maxIdx = Math.Min(m.I32(Addr.NumCars), 64) - 1;
            return true;
        }

        // Running cars on track (not on pit road), by progress. Each car gets
        //   Gap  = metres behind the pace car (smaller = further up the line)
        //   Down = laps + track position behind the leader. The leader is the car furthest ahead of all running
        //          cars, pit road included: while the lead lap cars pit, the best car on track is not the leader.
        //   Ahead  = between the pace car and the first lead lap car at or behind the leader ("first"): stuck
        //            in front of the leader.
        //   Stuck  = on the lead lap but in front of the leader (behind on track after a green flag stop).
        //   WaveOk = lapped and Ahead: a wave around candidate. A Stuck lead lap car in front of it (caught
        //            nearly a lap down when the caution came out) is sent around too and does not stop it.
        List<Car> Field(out Car leader, out Car first)
        {
            leader = first = null;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100 || double.IsNaN(trackLen)) return null;
            float pc = DlongOf(Addr.PaceCarIdx);
            var cars = new List<Car>();
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx)) continue;
                float d = DlongOf(idx);
                var c = new Car { Idx = idx, Num = CarNumber(idx), Prog = LapCount(idx, d, trackLen) + d / trackLen, Gap = Mod(pc - d, trackLen) };
                if (leader == null || c.Prog > leader.Prog) leader = c;
                if (!OnPitRoad(idx)) cars.Add(c);
            }
            if (cars.Count == 0) return null;
            cars = cars.OrderByDescending(c => c.Prog).ToList();
            foreach (Car c in cars)
            {
                c.Down = leader.Prog - c.Prog;
                if (c.Down < 1 && c.Gap >= leader.Gap - 1 && (first == null || c.Gap < first.Gap)) first = c;
            }
            if (first == null) first = cars[0];
            foreach (Car c in cars)
            {
                c.Ahead = c != first && c.Gap < first.Gap;
                c.Stuck = c.Ahead && c.Down < 1;
                c.WaveOk = c.Ahead && c.Down >= 1;
            }
            return cars;
        }

        // The lap counter and the position on the lap roll over on different frames at the line, so for a moment a
        // car reads a whole lap ahead or behind. One to go comes as the leader crosses the line, just then. The lap
        // count used for progress is the game's one taken mid-lap, moved on when the position wraps at the line.
        readonly int[] lapBase = new int[64];
        readonly float[] lastDlong = new float[64];
        readonly bool[] lapSynced = new bool[64];

        int LapCount(int idx, float d, double trackLen)
        {
            if (idx < 0 || idx >= 64) return LapsOf(idx);
            float last = lastDlong[idx];
            lastDlong[idx] = d;
            // a real crossing is a short move over the line; a jump (a car towed to its stall, a reset) is not
            bool wrapped = last > trackLen * 0.75 && d < trackLen * 0.25 && trackLen - last + d < trackLen * 0.1;
            bool unwrapped = last < trackLen * 0.25 && d > trackLen * 0.75 && trackLen - d + last < trackLen * 0.1;
            if (!lapSynced[idx] || (d > trackLen * 0.25 && d < trackLen * 0.75)) { lapBase[idx] = LapsOf(idx); lapSynced[idx] = true; }
            else if (wrapped) lapBase[idx]++;
            else if (unwrapped) lapBase[idx]--;
            else if (Math.Abs(Mod(d - last + trackLen / 2, trackLen) - trackLen / 2) > trackLen * 0.1) lapBase[idx] = LapsOf(idx);
            return lapBase[idx];
        }

        void WatchAcceleration(double dr, double ds)
        {
            if (double.IsNaN(ds) || ds < 0 || ds > 60) return;
            accelGame += ds;
            accelReal += dr;
            if (accelReal < 5) return;
            double x = accelGame / accelReal;
            if (x >= 1.5 && !accelLogged) Log(string.Format("time acceleration is on (about x{0:0}): AI pacing and restarts are less tidy at high speed-up", x));
            accelLogged = x >= 1.5;
            accelGame = accelReal = 0;
        }

        List<Car> Field()
        {
            Car leader, first;
            return Field(out leader, out first);
        }

        // NR2003 runs cautions and restarts itself (user, 2026-10-28): Race Control only gives the Lucky Dog. The form
        // also switches every other race option off; here the lineup, split hold and low line never run either.
        public bool StockCautions = false;
        bool stockLogged;

        void RaceTick()
        {
            PublishState();
            PlayerCallTick();      // the player's race banner (RaceEngine.PlayerCall.cs)
            CautionTimeline();
            CautionsTick();
            WaveTick();
            if (StockCautions != stockLogged)
            {
                stockLogged = StockCautions;
                Log(StockCautions ? "NR2003 runs cautions and restarts: Race Control gives the Lucky Dog only"
                                  : "Race Control runs cautions and restarts again");
            }
            if (StockCautions)                                     // the restart is the game's: only watched
            {
                // switched on mid-caution: the low line / high line LineupTick set up is never cleaned up there, so undo it here
                if (lowSince.Count > 0 || highOrig != null) EndLowLine("NR2003 runs cautions now");
                RestartDiagnostics();
                return;
            }
            LineupTick();
            SplitHoldTick();
        }
    }
}
