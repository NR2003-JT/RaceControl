using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RaceControl
{
    // Qualifying run inside the PRACTICE session (NR2003 runs the AI's own qualifying unseen, all at once), from
    // the QualifyProbe prototypes (single_car_qualifying.py / knockout.py):
    //   Single car: every AI car is held in its pit box; one at a time rolls off, runs an out lap and N timed laps
    //               and comes back in; the next one goes when it reaches pit road.
    //   Knockout:   rounds of minutes : cars that advance (25:24, 10:12, 5:12); everyone in the round stays out
    //               until the clock runs out, finishes that lap and comes in. Grid: the last round by its times,
    //               then each earlier round's eliminated cars by that round's times.
    // In the QUALIFYING session that follows every lap the AI post is rewritten (their S/F crossing times) to
    // these laps, so the game's leaderboard and starting grid follow them.
    // The player's car can take part (IncludePlayer): it is never written to (no hold, release, call-in, fuel or pace).
    // Its turn and calls are put on screen (PlayerCall), and its state comes from pit road, speed and spawned (PlayerSt)
    // as one of the AI states the rest of this code uses. In the qualifying session its laps are rewritten like the AI's.
    // Levers (found 2026-10-19): +6922 stall-wait countdown (huge = stay in the box, 1 = go now), +6918 run-end lap
    // (compared with +6778, the car's lap: lap >= end = come in), +5031 = the car's AI state object.
    public class Qualifying
    {
        public enum Format { Off, Single, Knockout, IndyOval, IndyRoad }
        public Format Mode = Format.Off;
        // run one car at a time (NASCAR single car runs, Indy oval 4-lap average runs)
        public bool SingleRuns { get { return Mode == Format.Single || Mode == Format.IndyOval; } }
        // rounds with everyone in a round out at once (NASCAR knockout, Indy road / street course)
        public bool KnockoutLike { get { return Mode == Format.Knockout || Mode == Format.IndyRoad; } }
        // Indy road / street course: Group 1, Group 2 (top IndyGroupAdv of each through), Fast 12 (top IndyF12Adv through),
        // Fast Six. Grid: Fast Six 1-6, the rest of the Fast 12 7-12, then from 13th the groups' other cars - Group 1 on the
        // odd spots, Group 2 on the even ones, each in its finishing order.
        public enum Split { Rating, List, Random }
        public double IndyGroupMin = 10, IndyF12Min = 10, IndyF6Min = 6;
        public int IndyGroupAdv = 6, IndyF12Adv = 6;
        public Split IndySplit = Split.Rating;
        List<int> indyG1, indyG2;
        string RoundName(int i)
        {
            if (Mode != Format.IndyRoad) return "ROUND " + (i + 1);
            return i == 0 ? "GROUP 1" : i == 1 ? "GROUP 2" : i == 2 ? "FAST 12" : "FAST SIX";
        }
        // Indy oval: a run counts only complete, ranked by the average of its timed laps (every lap counts)
        bool Average { get { return Mode == Format.IndyOval; } }

        // a run's score in seconds (9e9 = no time): its best lap, or for an Indy oval run the average of all its laps
        double Score(List<double> laps)
        {
            if (!Average) return laps.Count > 0 ? laps.Min() : 9e9;
            return laps.Count >= Laps ? laps.Take(Laps).Average() : 9e9;
        }
        string ScoreText(List<double> laps)
        {
            var inv = CultureInfo.InvariantCulture;
            if (!Average) return laps.Count > 0 ? ", best " + laps.Min().ToString("0.000", inv) : "";
            double s = Score(laps), len = LapFmt.TrackM > 100 ? LapFmt.TrackM : TrackM();   // speeds: the official length
            if (s >= 9e9) return " - " + laps.Count + " of " + Laps + " laps: an incomplete run, no time";
            return ", average " + s.ToString("0.000", inv) + (len > 0 ? string.Format(inv, " ({0:0.000} mph)", len / s * 2.2369363) : "");
        }
        public int Laps = 1;                                   // single car: timed laps (the out lap never counts)
        public string Rounds = "25:24, 10:12, 5:12";           // knockout: minutes : cars that advance
        public double BreakSeconds = 60;
        public string Order = "";                              // single car: car numbers in running order (else a random draw)
        public Action<string> Logger;
        public bool QualPace = true;                           // give the AI the game's qualifying speed bonus while out
        public double DnqSeconds = 90;                         // single car: a released car not out of its box by then = DNQ
        public bool MatchStockPace = true;                     // scale the written times to the game's own qualifying pace (order unchanged)
        public double FuelPercent = 25;                        // qualifying fuel load (% of the tank) given at the start of a run; 0 = leave it
        readonly HashSet<int> dnq = new HashSet<int>();

        // ---- the field: true DNQs ----
        // Only the top Starters cars (a number, or a % of the cars that took part) make the field; the rest did not qualify.
        // They are marked on the results and the state file, held in their boxes through a practice after qualifying (Happy
        // Hour), and written to the back of the grid in the qualifying session. NR2003 sets the race's field when the weekend
        // loads, so they are still in the race (from the back): taking them out of it needs a live test of the race start.
        public int Starters = 0;                               // 0 = every car makes the field
        public bool StartersPercent;                           // Starters is a % of the qualifying field
        readonly List<string> dnqNums = new List<string>();    // car numbers that didn't make the field (kept until the race loads)
        readonly HashSet<int> dnqSlots = new HashSet<int>();   // ... as this session's slots, while the results are shown
        public List<string> NotQualified { get { return dnqNums.ToList(); } }
        int FieldCut(int entered)
        {
            if (Starters <= 0 || entered <= 0) return entered;
            int k = StartersPercent ? (int)Math.Round(entered * Starters / 100.0, MidpointRounding.AwayFromZero) : Starters;
            return Math.Max(1, Math.Min(entered, k));
        }
        // the cars past the cut of a finishing order (pole first) did not qualify: logged and kept for Happy Hour
        void ApplyCut(List<int> order)
        {
            dnqNums.Clear(); dnqSlots.Clear();
            int cut = FieldCut(order.Count);
            if (cut >= order.Count) return;
            foreach (int c in order.Skip(cut)) { dnqSlots.Add(c); dnqNums.Add(Num(c)); }
            Log(string.Format("DID NOT QUALIFY ({0} of {1} cars make the field{2}): {3}", cut, order.Count, StartersPercent ? ", " + Starters + "%" : "",
                string.Join(", ", order.Skip(cut).Select(c => Car(c)).ToArray())));
            if (dnqSlots.Contains(Me)) Call(3, "DID NOT QUALIFY: you're outside the top " + cut, 30);
        }

        // ---- Indy oval: two-lane re-qualifying and wave-offs ----
        // Once every car has made its first run the lanes open for ReQualMinutes (game time). Lane 1 (priority): the car
        // gives up its time and goes before anyone in Lane 2; its result is then its best Lane 1 run, which can be slower than
        // what it gave up (or no time at all). Lane 2: the car keeps its time and a faster run replaces it, but it only goes
        // when Lane 1 is empty and no Lane 1 car is on track - it may never get its chance. A team can wave off a run before
        // its last timed lap (the run counts for nothing); a wave-off on the first run puts the car in Lane 1. The AI teams
        // choose their lane from where they stand (bumped, on the bubble, well down on what their rating says) and only go
        // again when they still need to, so Lane 1 doesn't fill the whole window. The player picks a lane in the window.
        public bool ReQual = false;
        public double ReQualMinutes = 20;
        public int ReQualMaxRuns = 3;                          // runs a car may make in all, its first one included
        public bool WaveOffs = true;
        bool Lanes { get { return ReQual && Mode == Format.IndyOval; } }
        const int Bubble = 2;                                  // cars this close inside the cut are on the bubble
        readonly List<int> lane1Q = new List<int>(), lane2Q = new List<int>();
        readonly Dictionary<int, int> runsMade = new Dictionary<int, int>();
        readonly HashSet<int> withdrawn = new HashSet<int>(), wavedOffFirst = new HashSet<int>();
        bool reqOpen, reqDone; double reqEndG = -1;
        public bool LanesOpen { get { return reqOpen; } }
        public double ReQualLeft { get { return reqOpen ? Math.Max(0, reqEndG - G()) : 0; } }
        public int PlayerLane { get { return lane1Q.Contains(Me) ? 1 : lane2Q.Contains(Me) ? 2 : 0; } }
        public bool PlayerCanPickLane
        {
            get { return reqOpen && field.Contains(Me) && single != null && !single.Active.Any(r => r.Car == Me) && Runs(Me) < ReQualMaxRuns && !PendingRun(Me); }
        }
        int Runs(int c) { int n; return runsMade.TryGetValue(c, out n) ? n : 0; }
        bool PendingRun(int c) { return single != null && single.Order.Skip(single.K + 1).Contains(c); }
        double Official(int c) { var r = results.FirstOrDefault(x => x.Item2 == c); return r != null ? r.Item1 : 9e9; }
        public int RandomSeed { set { rnd = new Random(value); } }

        // the player's lane: 1 gives up the time now, 2 keeps it, 0 leaves the lanes. Returns what happened (for the window).
        public string ChooseLane(int laneNo)
        {
            if (!PlayerCanPickLane && !(laneNo == 0 && PlayerLane > 0)) return reqOpen ? "You can't change lanes now (on a run, or out of runs)." : "The lanes are not open.";
            lane1Q.Remove(Me); lane2Q.Remove(Me);
            if (laneNo == 1)
            {
                Withdraw(Me);
                lane1Q.Add(Me);
                Call(0, "LANE 1: your time is given up - you go before Lane 2", 15);
                return "In Lane 1: your time is given up.";
            }
            if (laneNo == 2)
            {
                if (withdrawn.Contains(Me) || Official(Me) >= 9e9) { lane1Q.Add(Me); Call(0, "LANE 1: you have no time to keep", 15); return "No time to keep: put in Lane 1."; }
                lane2Q.Add(Me);
                Call(0, "LANE 2: you keep your time - you go when Lane 1 is clear", 15);
                return "In Lane 2: you keep your time.";
            }
            meWantsWindow = false;
            Call(0, "OUT OF THE LANES: your time stands", 10);
            return "Out of the lanes.";
        }

        void Withdraw(int c)
        {
            int i = results.FindIndex(x => x.Item2 == c);
            if (withdrawn.Contains(c)) return;
            withdrawn.Add(c);
            if (i >= 0 && results[i].Item1 < 9e9)
            {
                Log(string.Format(CultureInfo.InvariantCulture, "{0} gives up its time ({1}) for Lane 1", Car(c), Ms((int)Math.Round(results[i].Item1 * 1000))));
                results[i] = Tuple.Create(9e9, c, new List<double>());
            }
        }

        // ---- the player's car ----
        public bool IncludePlayer = true;
        public string PlayerCall = "";                         // what the on-screen banner tells the player now ("" = nothing)
        public int PlayerCallKind;                             // 0 info, 1 go, 2 come in, 3 out (DNQ / eliminated / not your turn)
        double playerCallUntil = -1;                           // a call that times out (-1 = until replaced)
        const int Me = Addr.PlayerIdx;
        uint playerSt = 0x760ea0;
        bool meInQual;                                         // the player had a turn / a round in this practice's qualifying
        bool meNoLapLogged;
        // the player's countdown on the banner ("LABEL|m:ss", "" = none) - at the green in / pull out in / time left
        public string PlayerTimer = "";
        public bool PlayerTimerUrgent;
        // Your turn pauses the game and shows a "get ready" screen (set your camera); the pull-out time only starts
        // when you resume. While the screen is up you can't be a DNQ. Time the game is paused never counts to a DNQ.
        public bool PlayerReadyPause = true;
        bool readyRequested, playerHold;
        double lastTickWall = -1;
        // MainForm: true once when the player's turn is called and the ready screen should go up
        public bool TakeReadyRequest() { bool r = readyRequested; readyRequested = false; return r; }
        public bool PlayerHolding { get { return playerHold; } }
        // the ready screen closed (game resumed): the pull-out time starts now
        public void PlayerResumed()
        {
            if (!playerHold) return;
            playerHold = false;
            Run r = single != null ? single.Active.FirstOrDefault(x => x.Car == Me) : null;
            if (r != null && r.LeftAt < 0) r.Started = Wall;
            Log("your car: ready - pull out within " + DnqSeconds.ToString("0") + " s");
        }
        void HoldForReady(string call)
        {
            Call(1, call);
            if (!PlayerReadyPause) return;
            readyRequested = true; playerHold = true;
        }
        // The player is driving a run: from the moment their turn is called until they are stopped back on pit road.
        // Written to the state file as `drive`, so broadcast tools put the camera in the player's cockpit meanwhile.
        bool meDriving, meLeftBox;
        double meBoxAt = -1;                                   // stopped back in the box after the run (Wall), -1 = not yet
        public double ClimbOutWait = 90;                       // the broadcast waits this long at most for you to climb out
        public int DriveCar { get { return meDriving ? Me : -1; } }
        void Drive(bool on)
        {
            if (on == meDriving) return;
            meDriving = on; meLeftBox = false; meBoxAt = -1;
            if (!on) playerHold = false;
            if (!on && PlayerCall.StartsWith("IN YOUR BOX")) Call(0, "");   // out of the car: the climb-out call is done
            Log(on ? "your car: on your run - broadcast tools go to your cockpit" : "your car: out of the car / done - broadcast tools go back to the runs");
        }
        void DriveTick()
        {
            if (!meDriving) return;
            if (!PlayerReady()) { Drive(false); return; }       // to the garage
            uint s = PlayerSt();
            if (OnTrack.Contains(s)) { meLeftBox = true; meBoxAt = -1; }
            else if (meLeftBox && Wait.Contains(s))
            {
                // Stopped back in the box: the broadcast stays in your car until you climb out (live 2026-10-26: it cut to
                // the next runner while you still sat in the car, and getting out then left the game on its replay), at
                // most ClimbOutWait seconds.
                if (meBoxAt < 0)
                {
                    meBoxAt = Wall;
                    if (InCar()) { Log("your car: stopped in the box - the broadcast waits for you to climb out"); Call(2, "IN YOUR BOX - climb out of the car to go back to the broadcast"); }
                }
                if (!InCar() || Wall - meBoxAt > ClimbOutWait) Drive(false);
            }
        }
        // In the car: the hub (NR2003's telemetry / the companion feed) when it runs, else the game's driver state in
        // memory (0xC067D4 = 3, never verified live). A disagreement is logged once a session, to settle the memory one.
        bool inCarDisagreeLogged;
        bool InCar()
        {
            bool mem = m.U32(Addr.DriverInCar) == 3;
            HubFeed h = HubFeed.Current;
            if (!h.Fresh) return mem;
            if (h.InCar != mem && !inCarDisagreeLogged)
            {
                inCarDisagreeLogged = true;
                Log(string.Format("your car: the hub says {0} the car, game memory (0xC067D4 = {1}) says {2} - using the hub",
                    h.InCar ? "in" : "out of", m.U32(Addr.DriverInCar), mem ? "in" : "out"));
            }
            return h.InCar;
        }
        static string TimerText(double s) { if (s <= 0.5) return "NOW"; int t = (int)Math.Ceiling(s); return t / 60 + ":" + (t % 60).ToString("00"); }
        void Call(int kind, string text, double hold = -1)
        {
            if (text != PlayerCall) { PlayerCall = text; PlayerCallKind = kind; if (text.Length > 0) Log("your car: " + text); }
            playerCallUntil = hold > 0 ? Wall + hold : -1;
        }
        bool PlayerReady() { return IncludePlayer && m.U8(Addr.HasSpawned + Me) == 1 && m.I32(Driver0 + Me * DriverSize + DriverNum) >= 0 && Name(Me).Length > 0; }
        // the player's state as the AI state the qualifying code checks for: off pit road = practicing; on pit road stopped =
        // in the box; on pit road moving = leaving (after the box) or entering (after the track); not spawned = garage
        uint PlayerSt()
        {
            uint was = playerSt;
            if (!PlayerReady()) playerSt = 0x760ea0;
            else if (m.U8(Addr.OnPitRoad + Me) == 0) playerSt = 0x760d2c;
            else if (Speed(Me) < 1.0) playerSt = 0x760f60;
            else
            {
                bool fromBox = Wait.Contains(playerSt) || Leaving.Contains(playerSt) || Garage.Contains(playerSt);
                playerSt = fromBox ? 0x760efcu : 0x760f84u;
            }
            if (playerSt != was && meDriving)                            // what Race Control sees of your car on your run
                Log(string.Format("your car: {0} (pit road flag {1}, {2:0.0} m/s, {3:0} m round the lap)", StateName(playerSt),
                    m.U8(Addr.OnPitRoad + Me), Speed(Me), Dl(Me)));
            return playerSt;
        }

        // The player's car: its velocity in the car block reads 0 (live 2026-10-26: "0.0 m/s" on every line, also on
        // track - at Bristol that made a stretch of pit road look like the car had stopped back in its box: DNQ). Its
        // speed comes from how far it went round the lap instead, over at least a quarter of a game second.
        float meLastDl = float.NaN; double meLastG = -1, meSpeed;
        double PlayerSpeed()
        {
            HubFeed h = HubFeed.Current;
            if (h.Fresh && h.HasPlayer) return meSpeed = h.PlayerSpeed;   // the game's own speed (hub), when it runs
            double g = G(); float d = Dl(Me);
            if (float.IsNaN(d)) return meSpeed;
            if (float.IsNaN(meLastDl) || meLastG < 0 || g < meLastG || g - meLastG > 5) { meLastDl = d; meLastG = g; return meSpeed; }   // (after a gap: the last speed until the next reading)
            double dt = g - meLastG;
            if (dt < 0.25) return meSpeed;
            double a = Ahead(meLastDl, d);
            if (!double.IsNaN(a) && a < 150 * dt) meSpeed = a / dt;          // (a jump back = reversing / reset: no reading)
            meLastDl = d; meLastG = g;
            return meSpeed;
        }
        // the player back on pit road on its own counts only once it has stopped there for 3 s (driving along a pit road
        // that leaves the track and comes back to it, as Bristol's, is not stopping)
        double meWaitSince = -1;
        bool PlayerStopped(uint s)
        {
            if (!Wait.Contains(s)) { meWaitSince = -1; return false; }
            if (meWaitSince < 0) meWaitSince = Wall;
            return Wall - meWaitSince >= 3;
        }

        // the player moved away from where it sat when its turn was called (or is driving off): out of its box
        bool PlayerLeftBox(Run r)
        {
            if (Speed(Me) > 3.0) return true;
            double len = TrackM(), d = Dl(Me);
            if (len <= 0 || double.IsNaN(d) || double.IsNaN(r.StallDl)) return false;
            double moved = Ahead(r.StallDl, d);
            return moved > 15 && moved < len - 15;
        }

        // live timing for the overlay (MainForm draws it)
        public readonly QualTiming Timing = new QualTiming();
        public bool LiveOn { get; private set; }
        public string LiveHeader = "";
        public List<Tuple<int, int>> LiveBoard = new List<Tuple<int, int>>();   // slot, best ms - fastest first
        public int CarNumber(int c) { return m.I32(Driver0 + c * DriverSize + DriverNum); }
        // single car: the cars on their run (release order) and the one the timing box follows by default
        // A car that has been called in is still in Active until it reaches pit road, but it is no
        // longer a timed runner. Do not let the timing box follow its in-lap.
        public List<int> ActiveRunners { get { return single != null ? single.Active.Where(r => !r.CalledIn).Select(r => r.Car).ToList() : new List<int>(); } }
        public int RunnerSlot
        {
            get
            {
                if (single == null || single.Active.Count == 0) return -1;
                var r = single.Active.FirstOrDefault(x => !x.CalledIn);
                return r != null ? r.Car : -1;
            }
        }
        public enum Release { PitRoad, Seconds, LapPercent, Gap }
        public Release ReleaseMode = Release.PitRoad;          // when the next single car rolls off
        public double ReleaseValue = 30;                       // seconds after the car before left its box / % of its last lap / green gap (s)
        public int MaxOut = 3;                                 // single car: never more than this many cars out at once (in-laps count)

        // What the timing box shows in single car runs (decided here so the box and the broadcast state file agree,
        // whatever car the TV camera is on): a run's result for ResultHold seconds after its last timed lap, the car
        // on a timed lap, else the next car on its way to the green ("-.--").
        public enum BoxShow { None, Result, Live, Waiting }
        public BoxShow BoxKind = BoxShow.None;
        public int BoxCar = -1;
        public int ResultBestMs, ResultPos, ResultDiffMs; public bool ResultHasDiff;   // BoxShow.Result: the run just done
        public int BoxLapMs; public double BoxLapAge = 1e9;   // BoxShow.Live: the timed lap this car just finished (between timed laps)
        public const double ResultHold = 5;
        int resultCar = -1; double resultAt;
        // The AI cars in the session (for the running-order list): slot, painted number, name.
        public static List<Tuple<int, string, string>> Roster(IGameMemory mem)
        {
            var o = new List<Tuple<int, string, string>>();
            // the player's car, when someone is driving (marked "(you)")
            int pn = mem.I32(Driver0 + Me * DriverSize + DriverNum);
            string pName = (mem.Str(Driver0 + Me * DriverSize, 16) + " " + mem.Str(Driver0 + Me * DriverSize + 16, 20)).Trim();
            if (mem.U8(Addr.HasSpawned + Me) == 1 && pn >= 0 && pName.Length > 0) o.Add(Tuple.Create(Me, Label(pn), pName + " (you)"));
            for (int c = 2; c < Slots; c++)
            {
                uint st = mem.U32(Block0 + c * Stride + State);
                if (st < 0x760D20 || st > 0x760FB0) continue;                      // not an AI car in this session
                int num = mem.I32(Driver0 + c * DriverSize + DriverNum);
                string name = (mem.Str(Driver0 + c * DriverSize, 16) + " " + mem.Str(Driver0 + c * DriverSize + 16, 20)).Trim();
                if (num < 0 || name.Length == 0) continue;
                o.Add(Tuple.Create(c, Label(num), name));
            }
            return o;
        }
        public string LastName(int c) { string l = m.Str(Driver0 + c * DriverSize + 16, 20).Trim(); return (l.Length > 0 ? l : Name(c)).ToUpperInvariant(); }
        public string FirstName(int c) { return m.Str(Driver0 + c * DriverSize, 16).Trim().ToUpperInvariant(); }

        // The broadcast overlay's lap block for a car on (or just off) its single car run: the timed lap it is on
        // (0 = out lap, Laps+1 = in-lap) and the timed laps it has finished (ms). False when it has no run.
        // The running order as the broadcast overlay shows it: the cars that have run, ranked by time (pole first; no
        // time / DNQ at the end of them), then the car(s) on their run now, then the cars still to go in the
        // qualifying order. Item2 = time in ms (0 = none), Item3 = 'f' finished, 'l' on its run, 'u' still to go.
        public List<Tuple<int, int, char>> OrderView()
        {
            var o = new List<Tuple<int, int, char>>();
            if (single == null) return o;
            foreach (var r in results.OrderBy(x => x.Item1))
                o.Add(Tuple.Create(r.Item2, r.Item1 < 9e9 ? (int)Math.Round(r.Item1 * 1000) : 0, 'f'));
            foreach (var r in single.Active)
                if (!o.Any(x => x.Item1 == r.Car)) o.Add(Tuple.Create(r.Car, 0, 'l'));
            foreach (int c in single.Order.Skip(single.K + 1))
                if (!o.Any(x => x.Item1 == c)) o.Add(Tuple.Create(c, 0, 'u'));
            return o;
        }

        public bool RunView(int car, out int lapNo, out List<int> lapMs)
        {
            lapNo = 0; lapMs = new List<int>();
            if (single == null) return false;
            var r = single.Active.FirstOrDefault(x => x.Car == car) ?? single.History.LastOrDefault(x => x.Car == car);
            if (r == null) return false;
            lapNo = r.CalledIn || r.FinishG >= 0 ? Laps + 1 : r.Before == null ? 0 : Math.Min(Laps, Math.Max(0, r.Fresh));
            lapMs = r.LapMs.ToList();
            return true;
        }

        const long Block0 = 0xACCA30, Session = 0x977A00, GameTime = 0x977A04;
        const int Stride = Addr.Ocd, Slots = 44;
        const int State = 5031, Countdown = 6922, RunEnd = 6918, Lap = 6778, Vel = 6634;
        const long Driver0 = 14711636; const int DriverSize = 175, DriverNum = 156;
        const long Cross0 = 0x77F996; const int CrossCar = 2048; const uint CrossMask = 0x0FFFFFFF;
        const int Hold = 10000000;
        static readonly Dictionary<uint, string> Known = new Dictionary<uint, string> {
            { 0x760ea0, "InGarageSlow" }, { 0x760e7c, "InGarageFast" }, { 0x760f60, "InPitStallWaitingSlow" },
            { 0x760f08, "InPitStallWaitingFast" }, { 0x760ed8, "InPitStallSlow" }, { 0x760ebc, "InPitStallFast" },
            { 0x760efc, "LeavingPitsSlow" }, { 0x760f6c, "LeavingPitsFast" }, { 0x760f84, "EnteringPitsSlow" },
            { 0x760f90, "EnteringPitsFast" }, { 0x760f48, "PitInLapSlow" }, { 0x760f78, "PitInLapFast" },
            { 0x760d2c, "Practicing" }, { 0x760dfc, "WarmingUp" } };
        static readonly HashSet<uint> Wait = new HashSet<uint> { 0x760f60, 0x760f08 }, Garage = new HashSet<uint> { 0x760ea0, 0x760e7c },
            Leaving = new HashSet<uint> { 0x760efc, 0x760f6c }, OnTrack = new HashSet<uint> { 0x760d2c, 0x760dfc },
            PitIn = new HashSet<uint> { 0x760f48, 0x760f78 }, Entering = new HashSet<uint> { 0x760f84, 0x760f90 },
            Stall = new HashSet<uint> { 0x760ed8, 0x760ebc };
        // a car on its run in one of these is out of it: tow called / being towed / limping in (a spin that recovers
        // - RecoveringSlow / Fast - carries on and keeps its lap if it completes)
        static readonly HashSet<uint> Damaged = new HashSet<uint> { 0x760f3c, 0x760df0, 0x760de4, 0x760d78 };
        static string StateName(uint s)
        {
            switch (s)
            {
                case 0x760f3c: return "called a tow";
                case 0x760df0: return "being towed";
                case 0x760de4: case 0x760d78: return "limping to the pits";
                case 0x760ea0: case 0x760e7c: return "went to the garage";
                default: return Known.ContainsKey(s) ? Known[s] : "state 0x" + s.ToString("x");
            }
        }

        enum Phase { Idle, Holding, Running, Break, Done, GridWait, Grid, Stopped, Scratch }
        Phase phase = Phase.Idle;
        IGameMemory m;
        Random rnd = new Random();
        readonly System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        public Func<double> Clock;                             // the offline sim's clock (else real time)
        double Wall { get { return Clock != null ? Clock() : wall.Elapsed.TotalSeconds; } }

        readonly HashSet<int> field = new HashSet<int>(), told = new HashSet<int>(), outNow = new HashSet<int>();
        string status = "Off";
        public string Status { get { return status; } }
        public List<string> Board = new List<string>();        // what the UI shows: the order / results so far
        public bool CanStart { get { return phase == Phase.Holding && field.Count(c => Wait.Contains(St(c))) > 0; } }
        public bool Active { get { return phase != Phase.Idle && phase != Phase.Stopped; } }

        // ---- reading the game ----
        uint St(int c) { return c == Me ? PlayerSt() : m.U32(Block0 + c * Stride + State); }
        int Rd(int c, int off) { return m.I32(Block0 + c * Stride + off); }
        void Wr(int c, int off, int v) { if (c != Me) m.W32(Block0 + c * Stride + off, v); }
        int SessionKind() { return m.U8(Session); }                // 4 practice, 5 qualifying, 7 race
        // the next session's kind, from the weekend's session list (word [0xBEBDA3 + session * 8])
        int NextKind() { int i = m.U8(Addr.SessionNum) + 1; return i < m.U8(Addr.NumSessions) ? m.U16(0xBEBDA3 + i * 8) : -1; }

        public static string Label(int v)
        {
            if (v == 100) return "00";
            if (v >= 2000) { int digits = v / 1000, n = v % 1000; if (digits >= 2 && digits <= 3 && n < Math.Pow(10, digits)) return n.ToString().PadLeft(digits, '0'); }
            return v.ToString();
        }
        string Num(int c) { return Label(m.I32(Driver0 + c * DriverSize + DriverNum)); }
        string Name(int c) { return (m.Str(Driver0 + c * DriverSize, 16) + " " + m.Str(Driver0 + c * DriverSize + 16, 20)).Trim(); }
        string Car(int c) { return "#" + Num(c) + " " + Name(c); }

        List<uint> Crossings(int c)
        {
            var list = new List<uint>();
            for (int i = 0; i < CrossCar / 4; i++)
            {
                uint v = m.U32(Cross0 + c * CrossCar + i * 4);
                if (v == 0) break;
                list.Add(v & CrossMask);
            }
            return list;
        }
        // The game's qualifying bonus (0x4F74B0): in its qualifying session each AI car gets three multipliers,
        // 1 + (qualifying rating - 1) x papy_ai.ini driver_qualifying_to_*; in practice they are 1.0. While a car is out
        // on a qualifying run we give it the qualifying values, so its laps match what it would do in qualifying.
        const int QpRating = 5914, QpMults = 6106;                     // float; 3 floats: aero, power, grip
        static readonly long[] QpCoeffs = { 0x74B3E0, 0x74B3F8, 0x74B3C8 };   // live to_aero_drag, to_engine_power, to_traction_circle
        // Fuel: two pointers at car +0x518 / +0x520 to 8-byte blocks; the fuel (a double) is block A XOR block B (from
        // AIMOD). Capacity: float at [car +0x58A] + 0x24. A run starts with FuelPercent of the tank, as in qualifying.
        double Fuel(int c)
        {
            long b = Block0 + c * Stride;
            long pa = m.U32(b + 0x518), pb = m.U32(b + 0x520);
            if (pa == 0 || pb == 0) return -1;
            long a = ((long)m.U32(pa + 4) << 32) | m.U32(pa), bb = ((long)m.U32(pb + 4) << 32) | m.U32(pb);
            double f = BitConverter.Int64BitsToDouble(a ^ bb);
            return f >= 0 && f < 1000 ? f : -1;
        }
        double Capacity(int c)
        {
            long p = m.U32(Block0 + c * Stride + 0x58A);
            if (p == 0) return -1;
            float cap = m.F32(p + 0x24);
            return cap > 1 && cap < 1000 ? cap : -1;
        }
        void SetQualFuel(int c)
        {
            if (FuelPercent <= 0 || c == Me) return;            // (the player's fuel is the player's own setup)
            double cap = Capacity(c), before = Fuel(c);
            if (cap <= 0 || before < 0) { Log(Car(c) + ": fuel not readable, left as it is"); return; }
            double want = cap * FuelPercent / 100;
            if (want >= before) { Log(string.Format("{0}: fuel {1:0.0} of {2:0.0} (already light enough)", Car(c), before, cap)); return; }
            long b = Block0 + c * Stride;
            long pa = m.U32(b + 0x518), pb = m.U32(b + 0x520);
            long a = ((long)m.U32(pa + 4) << 32) | m.U32(pa);
            long nb = a ^ BitConverter.DoubleToInt64Bits(want);
            m.W32(pb, unchecked((int)(nb & 0xFFFFFFFF)));
            m.W32(pb + 4, unchecked((int)(nb >> 32)));
            Log(string.Format("{0}: qualifying fuel {1:0.0} -> {2:0.0} of {3:0.0} (read back {4:0.0})", Car(c), before, want, cap, Fuel(c)));
        }
        string PaceNow(int c)
        {
            long b = Block0 + c * Stride;
            return string.Format(CultureInfo.InvariantCulture, "rating {0:0.00}, multipliers {1:0.0000} {2:0.0000} {3:0.0000}",
                m.F32(b + QpRating), m.F32(b + QpMults), m.F32(b + QpMults + 4), m.F32(b + QpMults + 8));
        }

        void Pace(int c, bool on)
        {
            if (!QualPace || c == Me) return;
            long b = Block0 + c * Stride;
            float r = m.F32(b + QpRating);
            if (!(r > 0.2f && r < 3f)) return;
            for (int k = 0; k < 3; k++)
            {
                float v = 1f;
                if (on)
                {
                    float co = m.F32(QpCoeffs[k]);
                    if (!(co >= 0f && co < 1f)) return;
                    v = 1f + (r - 1f) * co;
                }
                m.W32(b + QpMults + k * 4, BitConverter.ToInt32(BitConverter.GetBytes(v), 0));
            }
        }

        double Speed(int c)
        {
            if (c == Me) return PlayerSpeed();
            float vx = m.F32(Block0 + c * Stride + Vel), vy = m.F32(Block0 + c * Stride + Vel + 4);
            return Math.Sqrt(vx * vx + vy * vy);
        }

        void Log(string s) { if (Logger != null) Logger(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " qualifying: " + s); }

        // Every AI car in its box stays there; one out without permission is called back in.
        void Upkeep(HashSet<int> allowed)
        {
            if (PlayerReady()) field.Add(Me);              // (never held or called in: nothing is written to it)
            for (int c = 2; c < Slots; c++)
            {
                uint s = St(c);
                if (!Known.ContainsKey(s)) continue;
                field.Add(c);
                if (allowed != null && allowed.Contains(c)) continue;
                if (Wait.Contains(s)) { if (Rd(c, Countdown) < Hold / 2) Wr(c, Countdown, Hold); }
                else if ((Leaving.Contains(s) || OnTrack.Contains(s)) && Rd(c, RunEnd) > Rd(c, Lap))
                {
                    Wr(c, RunEnd, Rd(c, Lap));
                    if (told.Add(c)) Log("calling in " + Car(c) + " (it was " + Known[s] + ")");
                }
            }
        }

        public void Tick(IGameMemory mem)
        {
            m = mem;
            if (m == null) return;
            LiveOn = false;
            if (playerCallUntil > 0 && Wall > playerCallUntil) { PlayerCall = ""; playerCallUntil = -1; }
            double nowWall = Wall;
            // paused: no run clock runs - neither the DNQ clock in the box nor the 600 s safety cap once out
            if (lastTickWall >= 0 && single != null && m.U8(Addr.SimState) == 3)
                foreach (Run pr in single.Active) pr.Started += nowWall - lastTickWall;
            lastTickWall = nowWall;
            PlayerTimer = ""; PlayerTimerUrgent = false;
            if (phase == Phase.Running || phase == Phase.Break || phase == Phase.Done) DriveTick(); else Drive(false);
            int kind = SessionKind();
            switch (phase)
            {
                case Phase.Idle:
                    if (Mode == Format.Off) { status = "Off"; return; }
                    if (kind != 5) loadTried = false;
                    else if (!loadTried) { loadTried = true; if (LoadRuns()) { StartGrid(); return; } }   // Race Control restarted in qualifying
                    if (kind != 7) raceTried = false;
                    else if (!raceTried)                                               // Race Control restarted before the green
                    {
                        raceTried = true;
                        if (ScratchDnqs && Starters > 0 && m.I32(Addr.LapsComplete) < 0 && LoadRuns(true)) { StartScratch(); return; }
                    }
                    if (kind == 4 && NextKind() == 5) NewPractice();
                    else if (kind == 4 && NextKind() == 7 && !hhTried)                 // Race Control restarted in Happy Hour
                    {
                        hhTried = true;
                        if (Starters > 0 && LoadRuns(true)) { phase = Phase.GridWait; gridWritten = true; return; }
                    }
                    else status = "Waiting for the practice session before qualifying";
                    if (kind != 4) hhTried = false;
                    return;
                case Phase.Stopped:
                    if (kind != 4) phase = Phase.Idle;
                    status = "Stopped for this practice session";
                    return;
                case Phase.Holding:
                    if (kind != 4) { Log("practice ended before qualifying started"); phase = Phase.Idle; return; }
                    if (Mode == Format.Off) { Stop(); return; }
                    Upkeep(null);
                    int boxed = field.Count(c => Wait.Contains(St(c)));
                    status = string.Format("Holding the cars in their boxes: {0} of {1} there, {2} in the garage. Start when the count stops growing.",
                        boxed, field.Count, field.Count(c => Garage.Contains(St(c))));
                    WriteState("waiting", 0, 0, null, 0);
                    return;
                case Phase.Running:
                case Phase.Break:
                    if (kind != 4) { PracticeOver(); return; }
                    if (koRounds != null) KnockoutTick(); else if (single != null) SingleTick(); else phase = Phase.Idle;
                    if ((phase == Phase.Running || phase == Phase.Break || phase == Phase.Done) && SessionKind() == 4) { LiveOn = true; Timing.Update(m, field); }
                    return;
                case Phase.Done:
                    if (kind != 4) { phase = runs.Count > 0 ? Phase.GridWait : Phase.Idle; gridWaitLogged = false; return; }
                    Upkeep(null);
                    status = "Done: every car is held in its box until practice ends. Then go to the qualifying session.";
                    LiveHeader = "QUALIFYING COMPLETE"; LiveOn = true; Timing.Update(m, field);
                    if (SingleRuns) WriteSingleState("done"); else WriteState("done", 0, 0, null, 0);
                    return;
                case Phase.GridWait:
                    if (kind == 5) { StartGrid(); return; }
                    if (kind == 4 && gridWritten && dnqNums.Count > 0) { HappyHourTick(); return; }
                    if (kind == 7 && !gridWaitLogged)
                    {
                        gridWaitLogged = true;
                        Log(gridWritten ? "the race loaded: the grid from qualifying stands" : "the race loaded without a qualifying session: no grid written");
                        raceTried = true;
                        if (dnqNums.Count > 0 && ScratchDnqs && m.I32(Addr.LapsComplete) < 0)
                        {
                            runs.Clear(); runNums.Clear(); gridWritten = false; dnqSlots.Clear(); hhLogged = false;
                            StartScratch();
                            return;
                        }
                        if (dnqNums.Count > 0)
                            Log("the race loaded: the DNQ cars " + string.Join(" ", dnqNums.Select(n => "#" + n).ToArray()) + " are still in the field (NR2003 sets it when the weekend " +
                                "loads) - they start from the back");
                        phase = Phase.Idle; runs.Clear(); runNums.Clear(); gridWritten = false; dnqNums.Clear(); dnqSlots.Clear(); hhLogged = false;
                    }
                    status = "Go to the qualifying session: the grid is written there (qualify yourself if you like).";
                    return;
                case Phase.Grid:
                    if (kind != 5) { FinishGrid(); return; }
                    GridTick();
                    return;
                case Phase.Scratch:
                    ScratchTick(kind);
                    return;
            }
        }
        bool gridWaitLogged, gridWritten, loadTried, hhTried, hhLogged, raceTried;

        // A practice after the qualifying session (Happy Hour): the cars that didn't qualify stay in their boxes, and one
        // out on track is called in. (The player's car is never held; a DNQ player is told.)
        void HappyHourTick()
        {
            var held = new List<int>();
            for (int c = 2; c < Slots; c++)
            {
                uint s = St(c);
                if (!Known.ContainsKey(s) || !dnqNums.Contains(Num(c))) continue;
                held.Add(c);
                if (Wait.Contains(s)) { if (Rd(c, Countdown) < Hold / 2) Wr(c, Countdown, Hold); }
                else if ((Leaving.Contains(s) || OnTrack.Contains(s)) && Rd(c, RunEnd) > Rd(c, Lap)) Wr(c, RunEnd, Rd(c, Lap));
            }
            if (!hhLogged && held.Count > 0)
            {
                hhLogged = true;
                Log("Happy Hour: the cars that didn't qualify stay in their boxes: " + Nums(held));
                if (PlayerReady() && dnqNums.Contains(Num(Me))) Call(3, "YOU DIDN'T QUALIFY: Happy Hour laps are yours, but you start from the back", 20);
            }
            status = "Happy Hour: " + dnqNums.Count + " car(s) that didn't qualify are held in their boxes. Then the race.";
        }

        // ---- the race: the DNQ cars taken out before the green ----
        // NR2003 puts every car of the weekend in the race. During the pace laps each DNQ AI car is sent the way an engine
        // failure goes (static read of NR2003.exe, 2026-11-03; live test pending):
        //  1. its engine damage (damage block car+0x1327, +0x58) goes past 0.70 - the game's repair rating 0x52FAD0 then
        //     says "must pit" (+0x1B33 = 2) - and it is called in (+8118). Engine failures retire in their stall
        //     (Michigan 2026-10-28: +0x58 0.69-0.87, the car retired there): mode DoneForSession (0xBCF7F4), whose
        //     entry 0x52E4E0 writes the car's out reason.
        //  2. one still on track when the pace car pulls off (or 20 game s after it was called in) gets its stuck timer
        //     (+0x1B2E, s) set past the game's tow call (> 5 s Crashed -> CallingTow, > 12 s elsewhere; > 8 s DoneForSession
        //     in some modes): towed to its stall, retired there.
        // At the green nothing more is written; a DNQ still running starts from the back (the damage stays, so the game
        // usually retires it in the first laps). The player's car is never written to.
        public bool ScratchDnqs = true;
        const int EngineDmg = 0x1327 + 0x58, OutReason = 0x1334, StuckTimer = 0x1B2E, RepairNeedOff = 0x1B33, PitRequest = 8118;
        const uint DoneForSession = 0xBCF7F4, CrashedMode = 0xBCF80C;
        const double TowAfter = 20;                            // game s after being called in with the car still on track
        static readonly HashSet<uint> HeadingIn = new HashSet<uint> { 0x760F3C, 0x760DF0, 0x760DE4, 0x760D78, 0x760D94, 0x760F54,
            0x760F48, 0x760F78, 0x760F84, 0x760F90, CrashedMode };
        class ScratchCar { public string Num; public uint Mode; public double CalledAt = -1, TowAt = -1; public bool Out, ResetLogged, ReqLogged; }
        readonly Dictionary<int, ScratchCar> scratch = new Dictionary<int, ScratchCar>();
        double scratchGreenAt = -1;
        bool scratchMeLogged;

        void StartScratch()
        {
            scratch.Clear(); scratchGreenAt = -1; scratchMeLogged = false;
            phase = Phase.Scratch;
            Log("the race loaded: taking the DNQ cars out before the green: " + string.Join(" ", dnqNums.Select(n => "#" + n).ToArray()) +
                " (engine damage past the must-pit line and called in; a tow if still out when the pace car pulls off)");
        }

        void EndScratch(string why)
        {
            var running = scratch.Where(kv => !kv.Value.Out).Select(kv => "#" + kv.Value.Num).ToArray();
            Log("DNQ cars: " + why + " - " + scratch.Count(kv => kv.Value.Out) + " out of the race" +
                (running.Length > 0 ? ", still running: " + string.Join(" ", running) : ""));
            phase = Phase.Idle; scratch.Clear(); dnqNums.Clear(); dnqSlots.Clear(); scratchGreenAt = -1;
        }

        void WrF(int c, int off, float v) { if (c != Me) m.W32(Block0 + c * Stride + off, BitConverter.ToInt32(BitConverter.GetBytes(v), 0)); }
        float RdF(int c, int off) { return m.F32(Block0 + c * Stride + off); }
        string ModeName(uint p)
        {
            if (Known.ContainsKey(p)) return Known[p];
            if (p == DoneForSession) return "DoneForSession";
            try
            {
                string n = m.Str(m.U32(p + 4), 32);
                if (n.Length > 2 && n.All(ch => ch >= 32 && ch < 127)) return n;
            }
            catch (GameMemoryException) { }
            return "0x" + p.ToString("x");
        }

        void ScratchTick(int kind)
        {
            if (kind != 7) { EndScratch("the race session ended"); return; }
            int left = dnqNums.Count - scratch.Count(kv => kv.Value.Out);
            status = "Race start: taking the " + dnqNums.Count + " car(s) that didn't qualify out of the race (" + left + " still in)";
            if (m.U8(Addr.SimState) != 2) return;                 // paused / loading: nothing moves
            int laps = m.I32(Addr.LapsComplete), flag = m.U8(Addr.FlagState);
            bool green = laps >= 0 || flag == 7;
            bool paceOff = flag >= 4 && flag <= 6;                // delayed green / one to go / double file: the pace car pulls off
            double now = Addr.GameSeconds(m);

            if (!scratchMeLogged && PlayerReady() && dnqNums.Contains(Num(Me)))
            {
                scratchMeLogged = true;
                Log("you didn't qualify: your car is never touched - you start from the back");
                Call(3, "YOU DIDN'T QUALIFY: you start from the back", 20);
            }
            for (int c = 2; c < Slots; c++)
            {
                if (scratch.ContainsKey(c) || m.U8(Addr.HasSpawned + c) != 1) continue;
                string n = Num(c);
                if (dnqNums.Contains(n)) scratch[c] = new ScratchCar { Num = n, Mode = St(c) };
            }

            foreach (var kv in scratch)
            {
                int c = kv.Key; ScratchCar sc = kv.Value;
                if (sc.Out) continue;
                long car = Block0 + c * Stride;
                uint mode = St(c);
                byte reason = m.U8(car + OutReason);
                bool pitRoad = m.U8(Addr.OnPitRoad + c) == 1;
                if (mode != sc.Mode)
                {
                    Log(string.Format(CultureInfo.InvariantCulture, "#{0} (DNQ): {1} -> {2}, on pit road {3}, repair need {4}, engine damage {5:0.00}{6}",
                        sc.Num, ModeName(sc.Mode), ModeName(mode), pitRoad ? 1 : 0, m.I32(car + RepairNeedOff), RdF(c, EngineDmg),
                        green ? (laps >= 0 ? ", lap " + laps : ", green") : ""));
                    sc.Mode = mode;
                }
                if (m.U8(Addr.HasNotRetired + c) != 1 || mode == DoneForSession || reason > 0)
                {
                    sc.Out = true;
                    Log(string.Format("#{0} (DNQ) is out of the race {1} ({2}, out reason {3})", sc.Num, green ? "after the green" : "before the green", ModeName(mode), reason));
                    continue;
                }
                if (green) continue;

                // 1. engine damage past the must-pit line (every tick: the game may rate or repair it back)
                float eng = RdF(c, EngineDmg);
                if (eng < 0.95f)
                {
                    WrF(c, EngineDmg, 0.95f);
                    if (sc.CalledAt < 0) Log(string.Format(CultureInfo.InvariantCulture, "#{0} (DNQ): engine damage {1:0.00} -> 0.95 (must pit)", sc.Num, eng));
                    else if (!sc.ResetLogged) { sc.ResetLogged = true; Log(string.Format(CultureInfo.InvariantCulture, "#{0} (DNQ): engine damage back to {1:0.00} ({2}) - set to 0.95 again (and every time after)", sc.Num, eng, ModeName(mode))); }
                }
                if (sc.CalledAt < 0) sc.CalledAt = now;
                // ... and called in, until it is on pit road or the game has it heading in
                if (!pitRoad && !HeadingIn.Contains(mode) && m.U8(car + PitRequest) == 0)
                {
                    m.W8(car + PitRequest, 1);
                    if (!sc.ReqLogged) { sc.ReqLogged = true; Log("#" + sc.Num + " (DNQ): called in (" + ModeName(mode) + ")"); }
                }
                // 2. still out on track when the pace car pulls off, or long after the call: a tow
                if (!pitRoad && !HeadingIn.Contains(mode) && (paceOff || now - sc.CalledAt > TowAfter))
                {
                    WrF(c, StuckTimer, 13f);
                    if (sc.TowAt < 0)
                    {
                        sc.TowAt = now;
                        Log("#" + sc.Num + " (DNQ): still on track (" + ModeName(mode) + ")" + (paceOff ? " as the pace car pulls off" : " " + TowAfter.ToString("0") + " s after the call") + ": asking for a tow");
                    }
                }
            }

            if (green)
            {
                if (scratchGreenAt < 0)
                {
                    scratchGreenAt = now;
                    var running = scratch.Where(kv => !kv.Value.Out).Select(kv => "#" + kv.Value.Num).ToArray();
                    Log("green flag: DNQ cars out of the race " + scratch.Count(kv => kv.Value.Out) + " of " + dnqNums.Count +
                        (running.Length > 0 ? "; still running (from the back, damaged): " + string.Join(" ", running) : ""));
                }
                if (scratch.Count > 0 && scratch.All(kv => kv.Value.Out)) EndScratch("all out");
                else if (laps >= 5 || now - scratchGreenAt > 300) EndScratch("no longer watched (lap " + laps + ")");
            }
        }

        void NewPractice()
        {
            field.Clear(); told.Clear(); outNow.Clear(); runs.Clear(); results.Clear(); Board.Clear();
            Timing.Reset(); LiveBoard = new List<Tuple<int, int>>(); dnq.Clear();
            meInQual = false; PlayerCall = ""; playerCallUntil = -1;
            single = null; koRounds = null;
            lane1Q.Clear(); lane2Q.Clear(); runsMade.Clear(); withdrawn.Clear(); wavedOffFirst.Clear();
            reqOpen = reqDone = false; reqEndG = -1;
            dnqNums.Clear(); dnqSlots.Clear(); hhLogged = false;
            resultCar = -1; BoxKind = BoxShow.None; BoxCar = -1;
            phase = Phase.Holding;
            Log("practice session: holding the AI cars in their pit boxes (" + (SingleRuns ? (Average ? "Indy oval runs, " + Laps + "-lap average" : "single car runs, " + Laps + " timed lap(s)") : Mode == Format.IndyRoad ? "Indy road / street: groups, Fast 12, Fast Six" : "knockout rounds " + Rounds) + ")");
        }

        // The Start button.
        public void Start()
        {
            if (!CanStart) return;
            if (SingleRuns) StartSingle(); else StartKnockout();
        }

        // The Stop button: stop holding cars for the rest of this practice session (they go out as the game likes).
        public void Stop()
        {
            if (m == null || !Active) return;
            if (phase == Phase.Holding || phase == Phase.Running || phase == Phase.Break || phase == Phase.Done)
            {
                LetGo();
                phase = Phase.Stopped;
                Log("stopped: the cars are let go for the rest of this practice session");
            }
            else { phase = Phase.Idle; Log("stopped: no grid written"); }
            FinalState();
        }

        // Race Control closing: nothing would undo the holds or the qualifying pace later, so let the cars go now (as Stop
        // does) - a car left with the hold would sit in its box for the rest of practice. The grid isn't written.
        public void Shutdown()
        {
            if (m == null) return;
            try
            {
                if (phase == Phase.Holding || phase == Phase.Running || phase == Phase.Break || phase == Phase.Done)
                {
                    LetGo();
                    Log("Race Control closing: the cars are let go for the rest of this practice session");
                }
                if (phase != Phase.Idle && phase != Phase.Grid) FinalState();
            }
            catch (GameMemoryException) { }
        }

        // the held cars go out as the game likes, and the cars on a run lose the qualifying pace bonus Race Control gave them
        void LetGo()
        {
            foreach (int c in field)
                if (c != Me && Wait.Contains(St(c)) && Rd(c, Countdown) > Hold / 2) Wr(c, Countdown, 60 + rnd.Next(600));
            var onRun = new HashSet<int>(outNow);
            if (single != null) foreach (Run r in single.Active) onRun.Add(r.Car);
            foreach (int c in onRun) Pace(c, false);
        }

        // The last qualifying_state write when qualifying stops (Stop, practice over, Race Control closing): not throttled,
        // in every format, with nobody on track, no focus car and nobody driving, so broadcast tools stop following a run.
        void FinalState()
        {
            Drive(false); playerHold = false;
            BoxKind = BoxShow.None; BoxCar = -1;
            outNow.Clear();
            lastState = -1;
            if (SingleRuns) { if (single != null) WriteSingleState("done", true); }
            else WriteState("done", 0, 0, null, 0);
        }

        // ---- single car runs ----
        class SingleRun { public List<int> Order; public int K = -1; public List<Run> Active = new List<Run>(), History = new List<Run>(); public Run Last; }
        SingleRun single;
        readonly List<Tuple<double, int, List<double>>> results = new List<Tuple<double, int, List<double>>>(); // best s, slot, laps
        readonly Dictionary<int, List<int>> runs = new Dictionary<int, List<int>>(); // slot -> lap ms to write
        readonly Dictionary<int, string> runNums = new Dictionary<int, string>();

        void StartSingle()
        {
            var boxed = field.Where(c => Wait.Contains(St(c))).ToList();
            var order = new List<int>();
            string kindTxt = "random draw";
            var wanted = (Order ?? "").Split(new[] { ',', ' ', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimStart('#')).ToList();
            if (wanted.Count > 0)
            {
                var byNum = new Dictionary<string, int>();
                foreach (int c in boxed) byNum[Num(c)] = c;
                foreach (string n in wanted) { int c; if (byNum.TryGetValue(n, out c) && !order.Contains(c)) order.Add(c); }
                kindTxt = "your order (" + order.Count + " listed cars found, the rest drawn after them)";
            }
            var rest = boxed.Where(c => !order.Contains(c)).OrderBy(c => rnd.Next()).ToList();
            order.AddRange(rest);
            single = new SingleRun { Order = order };
            pitSpeeds.Clear(); resultCar = -1; BoxKind = BoxShow.None; BoxCar = -1;
            phase = Phase.Running;
            Log((Average ? "INDY OVAL QUALIFYING, " : "SINGLE CAR QUALIFYING, ") + order.Count + " cars, " + Laps + (Average ? "-lap average runs" : " timed lap(s) each") + ", order by " + kindTxt + ": " +
                string.Join(" ", order.Select(c => "#" + Num(c)).ToArray()));
            if (ReleaseMode == Release.Gap)
                Log(string.Format(CultureInfo.InvariantCulture, "release: each car aims to take the green {0:0} s after the car before finishes (at most {1} out at once)", ReleaseValue, MaxOut));
            var missing = field.Where(c => !order.Contains(c)).Select(c => "#" + Num(c)).ToArray();
            if (missing.Length > 0) Log("not in their box yet (they join the end of the order when they show up): " + string.Join(" ", missing));
            ShowSingleBoard();
            // Publish the full draw before the first car is released, so broadcast overlays can
            // display the lineup in qualifying order while every driver is still on their out lap.
            lastState = -1;
            WriteSingleState("running");
        }

        // One car's run: out lap, timed laps, in-lap. Several can be under way when the next car is released early.
        // Game clock marks (s) for the green gap release: let go from the box, off pit road (the AI's on-track state),
        // the green (the crossing that starts timed lap 1) and the finish (the crossing that ends its last timed lap).
        class Run
        {
            public int Car; public List<uint> Before; public List<double> WallAt = new List<double>(); public bool CalledIn; public double Started, LeftAt = -1; public int Fresh;
            public double ReleasedG = -1, ExitG = -1, GreenG = -1, FinishG = -1, LapStartG = -1;
            public float StallDl = float.NaN, ExitDl = float.NaN, LastDl = float.NaN; public double LastG = -1;
            public int LapsDone, LastLapMs; public double LastLapAt = -1;
            public List<int> LapMs = new List<int>();          // each timed lap as it was completed (kept if the crossings go)
            public Run Prev; public bool GapLogged;
            public double TargetG = -1;                        // the green Race Control expects (game clock; the player's countdown)
            public float JoinDl = float.NaN; public bool JoinChecked;   // where it came onto the track (out-lap check)
            public int Lane;                                   // re-qualifying lane of this run (0 = the car's first run)
            public bool WavedOff; public int WaveChecked;      // waved off (counts for nothing); timed laps already looked at
        }

        void SingleTick()
        {
            var s1 = single;
            Upkeep(new HashSet<int>(s1.Active.Select(r => r.Car)));
            foreach (int x in field.OrderBy(x => x))
                if (!s1.Order.Contains(x) && Wait.Contains(St(x)))
                {
                    s1.Order.Add(x);
                    Log("late arrival " + Car(x) + " joins at position " + s1.Order.Count);
                }
            if (Lanes && s1.K + 1 >= s1.Order.Count) ReQualFeed(s1);      // the first runs are done: the lanes feed the order
            if (s1.K + 1 < s1.Order.Count && ReleaseNext(s1)) ReleaseSingleCar(s1);
            if (s1.Active.Count == 0 && s1.K + 1 >= s1.Order.Count && (!Lanes || reqDone)) { SingleDone(); return; }
            LiveHeader = reqOpen ? "RE-QUALIFYING  " + TimerText(ReQualLeft) : "SINGLE-CAR QUALIFYING";
            LiveBoard = results.Where(r => r.Item1 < 9e9).OrderBy(r => r.Item1).Select(r => Tuple.Create(r.Item2, (int)Math.Round(r.Item1 * 1000))).ToList();
            foreach (var r in s1.Active.ToList()) RunTick(s1, r);
            if (field.Contains(Me) && !s1.Active.Any(r => r.Car == Me))
            {
                // (a car can be in the order more than once with re-qualifying: its next turn is the first one still to come)
                int at = s1.Order.FindIndex(Math.Min(s1.K + 1, s1.Order.Count), x => x == Me);
                int pl = PlayerLane;
                if ((at > s1.K || (reqOpen && pl == 0 && Runs(Me) > 0)) && OnTrack.Contains(St(Me)))
                    Call(3, reqOpen && at < 0 ? "RE-QUALIFYING: pick a lane first - these laps don't count" : "NOT YOUR TURN YET: these laps don't count - back to your box");
                else if (at == s1.K + 1) Call(0, "YOU'RE NEXT: wait in your pit box");
                else if (at > s1.K + 1) Call(0, string.Format("YOUR RUN: {0} cars before you", at - s1.K - 1));
                else if (pl == 1) Call(0, string.Format("LANE 1: {0} car(s) ahead of you", lane1Q.IndexOf(Me)));
                else if (pl == 2) Call(0, string.Format("LANE 2: you go when Lane 1 is clear ({0} in Lane 1, {1} ahead of you in Lane 2)", lane1Q.Count, lane2Q.IndexOf(Me)));
            }
            PlayerTimerSingle(s1);
            BoxFocus(s1);
            string runs = string.Join("   ", s1.Active.Select(r =>
                Car(r.Car) + (r.Lane > 0 ? " (lane " + r.Lane + ")" : "") + " - " + (r.WavedOff ? "waved off" : r.CalledIn ? "in-lap" : r.Before == null || r.Fresh == 0 ? "out lap" : "timed lap " + Math.Min(Laps, r.Fresh) + " of " + Laps)).ToArray());
            status = (reqOpen ? string.Format("RE-QUALIFYING {0} left - Lane 1: {1} waiting, Lane 2: {2} waiting. ", TimerText(ReQualLeft), lane1Q.Count, lane2Q.Count) : "")
                + (s1.Active.Count == 0 ? "Next car rolling off" : runs);
            WriteSingleState("running");
        }

        // ---- re-qualifying lanes ----
        readonly Dictionary<int, int> pendingLane = new Dictionary<int, int>();   // car put in the order from a lane -> that lane

        // The order has run out. Before the lanes open: wait for the last first run, then open them. While open: put the next
        // car in (Lane 1 first; Lane 2 only with Lane 1 empty and no Lane 1 car out); close them when the time is up or nobody
        // is left in either lane with nobody out.
        void ReQualFeed(SingleRun s1)
        {
            if (reqDone) return;
            if (!reqOpen)
            {
                if (s1.Active.Count > 0) return;
                OpenReQual(s1);
                if (!reqOpen) return;
            }
            if (G() >= reqEndG) { CloseReQual(s1, "time is up"); return; }
            Func<int, bool> inBox = c => Wait.Contains(St(c)) && Runs(c) < ReQualMaxRuns;
            int next = -1, laneNo = 0;
            // (FindIndex, not FirstOrDefault: "none" would read as slot 0, the player's car)
            if (lane1Q.Count > 0)
            {
                int k = lane1Q.FindIndex(c => inBox(c)); laneNo = 1;           // (Lane 2 still waits while a Lane 1 car is on its way back to its box)
                next = k >= 0 ? lane1Q[k] : -1;
            }
            else if (!s1.Active.Any(r => r.Lane == 1) && lane2Q.Count > 0)
            {
                int k = lane2Q.FindIndex(c => inBox(c)); laneNo = 2;
                next = k >= 0 ? lane2Q[k] : -1;
            }
            // anyone in a lane that can never go (out of runs, gone to the garage) leaves it
            foreach (int c in lane1Q.Concat(lane2Q).ToList())
                if (Runs(c) >= ReQualMaxRuns || (c != Me && Garage.Contains(St(c)))) { lane1Q.Remove(c); lane2Q.Remove(c); }
            if (next >= 0)
            {
                (laneNo == 1 ? lane1Q : lane2Q).Remove(next);
                pendingLane[next] = laneNo;
                s1.Order.Add(next);
                Log(string.Format("re-qualifying: {0} goes from Lane {1} ({2} left in Lane 1, {3} in Lane 2)", Car(next), laneNo, lane1Q.Count, lane2Q.Count));
                return;
            }
            if (lane1Q.Count == 0 && lane2Q.Count == 0 && s1.Active.Count == 0 && !(field.Contains(Me) && PlayerCanPickLane && G() < reqEndG - 30 && meWantsWindow))
                CloseReQual(s1, "both lanes are empty");
        }
        bool meWantsWindow;                                          // the player can still pick a lane: the window stays open for it

        void OpenReQual(SingleRun s1)
        {
            reqOpen = true; reqEndG = G() + ReQualMinutes * 60;
            var timed = results.Where(r => r.Item1 < 9e9).OrderBy(r => r.Item1).Select(r => r.Item2).ToList();
            int entered = s1.Order.Distinct().Count(), cut = Starters > 0 ? FieldCut(entered) : 0;
            var stay = new List<int>();
            foreach (int c in s1.Order.Distinct())
            {
                if (Runs(c) >= ReQualMaxRuns) { stay.Add(c); continue; }
                if (c == Me)
                {
                    if (wavedOffFirst.Contains(Me)) { Withdraw(Me); lane1Q.Add(Me); }   // a wave-off on the first run: Lane 1
                    continue;
                }
                int lane = wavedOffFirst.Contains(c) ? 1 : DecideLane(c, timed, cut, true);
                if (lane == 1) { Withdraw(c); lane1Q.Add(c); }
                else if (lane == 2) lane2Q.Add(c);
                else stay.Add(c);
            }
            meWantsWindow = field.Contains(Me);
            Log(string.Format(CultureInfo.InvariantCulture, "RE-QUALIFYING OPEN for {0:0.#} min{1}: Lane 1 (priority, times given up): {2}; Lane 2 (keep their time): {3}; staying: {4}",
                ReQualMinutes, cut > 0 ? " (" + cut + " make the field)" : "", Nums(lane1Q), Nums(lane2Q), Nums(stay)));
            if (field.Contains(Me))
                Call(0, PlayerLane == 1 ? "RE-QUALIFYING OPEN: you waved off, so you're in Lane 1" : "RE-QUALIFYING OPEN: pick Lane 1 or Lane 2 in Race Control (Qualifying tab), or keep your time", 30);
            ShowSingleBoard();
        }
        string Nums(IEnumerable<int> cars) { var a = cars.Select(c => "#" + Num(c)).ToArray(); return a.Length == 0 ? "none" : string.Join(" ", a); }

        void CloseReQual(SingleRun s1, string why)
        {
            reqOpen = false; reqDone = true;
            // a car put in the order but not out of its box yet doesn't go
            for (int i = s1.Order.Count - 1; i > s1.K; i--) { pendingLane.Remove(s1.Order[i]); s1.Order.RemoveAt(i); }
            var left = lane1Q.Concat(lane2Q).ToList();
            lane1Q.Clear(); lane2Q.Clear();
            Log("RE-QUALIFYING CLOSED (" + why + ")" + (left.Count > 0 ? ": still in line, no run: " + Nums(left) : "") + (s1.Active.Count > 0 ? "; the cars out finish their runs" : ""));
            if (field.Contains(Me) && left.Contains(Me)) Call(3, "RE-QUALIFYING CLOSED: no run for you", 15);
        }

        // Where an AI team goes: 1 Lane 1, 2 Lane 2, 0 stays. No time / bumped = Lane 1 (nothing to lose); on the bubble =
        // mostly Lane 2 (keep the time, try to go faster); well down on what its rating says = sometimes. Once the lanes
        // are running (first = false) the same rules decide whether a car that just ran gets back in line, more rarely.
        int DecideLane(int c, List<int> timed, int cut, bool first)
        {
            int pos = timed.IndexOf(c) + 1;
            double roll = rnd.NextDouble();
            if (pos == 0) return roll < (first ? 1.0 : 0.85) ? 1 : 0;
            if (cut > 0 && pos > cut) return roll < (first ? 1.0 : 0.85) ? 1 : 0;
            if (cut > 0 && pos > cut - Bubble) return roll < (first ? 0.75 : 0.4) ? 2 : 0;
            int exp = RatingPos(c, timed);
            if (exp > 0 && pos - exp >= 5)
            {
                if (first) return roll < 0.25 ? 1 : roll < 0.6 ? 2 : 0;
                return roll < 0.15 ? 2 : 0;                              // (rarely: a fast car with a slow time tries once more)
            }
            return 0;
        }

        // where its qualifying rating puts this car among the cars with a time (1 = the best rated); 0 = ratings not readable
        int RatingPos(int c, List<int> timed)
        {
            Func<int, float> rating = x => m.F32(Block0 + x * Stride + QpRating);
            float mine = rating(c);
            if (!(mine > 0.2f && mine < 3f)) return 0;
            var rs = timed.Select(rating).Where(r => r > 0.2f && r < 3f).ToList();
            if (rs.Count < 3) return 0;
            return 1 + rs.Count(r => r > mine);
        }
        // the time this car "should" do: the time of the car at its rating's place (NaN: not enough to go on)
        double ExpectedScore(int c)
        {
            var timed = results.Where(r => r.Item1 < 9e9 && r.Item2 != c).OrderBy(r => r.Item1).ToList();
            if (timed.Count < 3) return double.NaN;
            int exp = RatingPos(c, timed.Select(r => r.Item2).Concat(new[] { c }).ToList());
            if (exp <= 0) return double.NaN;
            return timed[Math.Min(exp, timed.Count) - 1].Item1;
        }

        // A run that is going badly, looked at after each timed lap but the last (AI teams, with the lanes on): a Lane 2 run
        // that can't beat the car's time; a first run well off what the car should do (the lanes come after), or one heading
        // outside the field for a car that should be in it; a Lane 1 run heading outside the field with time to go again.
        bool WaveOffDue(Run r)
        {
            int c = r.Car;
            if (!Lanes || !WaveOffs || c == Me || r.WavedOff || r.Before == null || r.LapsDone < 1 || r.LapsDone >= Laps || r.LapsDone <= r.WaveChecked) return false;
            r.WaveChecked = r.LapsDone;
            double soFar = r.LapMs.Take(r.LapsDone).Average() / 1000.0, roll = rnd.NextDouble();
            var timed = results.Where(x => x.Item1 < 9e9 && x.Item2 != c).OrderBy(x => x.Item1).Select(x => x.Item1).ToList();
            int entered = single.Order.Distinct().Count(), cut = Starters > 0 ? FieldCut(entered) : 0;
            bool outside = cut > 0 && timed.Count >= cut && soFar > timed[cut - 1];
            string why = null;
            double need = double.NaN;
            if (r.Lane == 2) { need = Official(c); if (soFar >= need && roll < 0.8) why = "can't beat its time"; }
            else if (r.Lane == 1) { if (outside && ReQualLeft > 120 && Runs(c) + 1 < ReQualMaxRuns && roll < 0.5) why = "heading outside the field, time to go again"; }
            else if (!reqDone)
            {
                need = ExpectedScore(c);
                if (!double.IsNaN(need) && soFar > need * 1.004 && roll < 0.5) why = "well off its pace";
                else if (outside && !double.IsNaN(need) && cut > 0 && timed.Count >= cut && need <= timed[cut - 1] && roll < 0.6) why = "heading outside the field";
            }
            if (why == null) return false;
            r.WavedOff = true; r.CalledIn = true;
            Wr(c, RunEnd, Rd(c, Lap));
            Log(string.Format(CultureInfo.InvariantCulture, "{0} WAVES OFF after timed lap {1} of {2} ({3}: {4} lap(s) at {5:0.000}{6})", Car(c), r.LapsDone, Laps, why, r.LapsDone, soFar,
                double.IsNaN(need) || need >= 9e9 ? "" : string.Format(CultureInfo.InvariantCulture, ", wanted {0:0.000}", need)));
            return true;
        }

        // A run is over: its result as the lanes have it. First run: its time (none if waved off). Lane 2: a faster time
        // replaces the old one. Lane 1: its best Lane 1 run (it gave its old time up). Then, with the lanes open, whether the
        // car gets back in line.
        void PostResult(Run r, List<double> laps, bool noShow = false)
        {
            int c = r.Car;
            runsMade[c] = Runs(c) + 1;
            double sc = r.WavedOff ? 9e9 : Score(laps);
            int i = results.FindIndex(x => x.Item2 == c);
            if (i < 0)
            {
                results.Add(Tuple.Create(sc, c, r.WavedOff ? new List<double>() : laps));
                if (r.WavedOff && !noShow) { wavedOffFirst.Add(c); Log(Car(c) + ": waved off its first run - no time" + (Lanes && !reqDone ? "; it goes to Lane 1 when the lanes open" : "")); }
            }
            else if (sc < results[i].Item1)
            {
                Log(string.Format(CultureInfo.InvariantCulture, "{0}: {1}{2}", Car(c), r.Lane == 1 ? "Lane 1 run stands: " : "faster - replaces its time: ",
                    Ms((int)Math.Round(sc * 1000)) + (results[i].Item1 < 9e9 ? " (was " + Ms((int)Math.Round(results[i].Item1 * 1000)) + ")" : "")));
                results[i] = Tuple.Create(sc, c, laps);
                dnq.Remove(c);
            }
            else if (r.Lane > 0 && !noShow)
                Log(Car(c) + (r.WavedOff ? ": waved off" : ": no faster") + " - " + (results[i].Item1 < 9e9 ? "keeps " + Ms((int)Math.Round(results[i].Item1 * 1000)) : "still no time"));
            if (!reqOpen || r.Lane == 0 || c == Me || Runs(c) >= ReQualMaxRuns) return;
            var timed = results.Where(x => x.Item1 < 9e9).OrderBy(x => x.Item1).Select(x => x.Item2).ToList();
            int cut = Starters > 0 ? FieldCut(single.Order.Distinct().Count()) : 0;
            int again = DecideLane(c, timed, cut, false);
            if (again == 0) return;
            if (again == 1) { Withdraw(c); lane1Q.Add(c); } else lane2Q.Add(c);
            Log(Car(c) + " gets back in line: Lane " + again);
        }

        void ReleaseSingleCar(SingleRun s1)
        {
            s1.K++;
            var nr = new Run { Car = s1.Order[s1.K], Started = Wall, ReleasedG = G(), Prev = s1.Last };
            int ln; if (pendingLane.TryGetValue(nr.Car, out ln)) { nr.Lane = ln; pendingLane.Remove(nr.Car); }
            nr.StallDl = Dl(nr.Car);
            if (nr.Car == Me)
            {
                double toLine, outT = PredictOut(nr.StallDl, nr, out toLine);
                double fin = ReleaseMode == Release.Gap && nr.Prev != null && s1.Active.Contains(nr.Prev) ? PredictFinish(nr.Prev) : double.NaN;
                nr.TargetG = !double.IsNaN(outT) ? nr.ReleasedG + outT : !double.IsNaN(fin) ? fin + ReleaseValue : -1;
            }
            s1.Active.Add(nr); s1.History.Add(nr); s1.Last = nr;
            Wr(nr.Car, Countdown, 1);
            Log(string.Format("[{0}/{1}] {2} rolls off pit road{3}", s1.K + 1, s1.Order.Count, Car(nr.Car), ReleaseMode == Release.Gap ? gapNote : ""));
            gapNote = "";
            if (nr.Car == Me)
            {
                meInQual = true;
                Drive(true);
                HoldForReady(string.Format("{0}YOUR RUN: GO NOW - out lap, then {1} timed lap{2}", nr.Lane > 0 ? "LANE " + nr.Lane + ": " : "", Laps, Laps == 1 ? "" : "s"));
                return;
            }
            SetQualFuel(nr.Car);
            Pace(nr.Car, true);
            Log(Car(nr.Car) + ": " + PaceNow(nr.Car));
        }

        // The player's countdown: next up in a green gap release = the green that release aims at; released and still in the
        // box with under 30 s to the DNQ = the time left to pull out; else, until the green, the green Race Control expects.
        void PlayerTimerSingle(SingleRun s1)
        {
            var r = s1.Active.FirstOrDefault(x => x.Car == Me);
            if (r == null)
            {
                var prev = s1.Last;
                if (ReleaseMode == Release.Gap && s1.K + 1 < s1.Order.Count && s1.Order[s1.K + 1] == Me && prev != null && s1.Active.Contains(prev))
                {
                    double fin = PredictFinish(prev);
                    if (!double.IsNaN(fin)) PlayerTimer = "AT THE GREEN IN|" + TimerText(fin + ReleaseValue - G());
                }
                return;
            }
            if (r.CalledIn || r.GreenG >= 0) return;
            if (r.LeftAt < 0 && DnqSeconds - (Wall - r.Started) < 30) { PlayerTimer = "PULL OUT IN|" + TimerText(DnqSeconds - (Wall - r.Started)); PlayerTimerUrgent = true; return; }
            if (r.TargetG >= 0) PlayerTimer = "AT THE GREEN IN|" + TimerText(r.TargetG - G());
        }

        // The timing box: the run just done (its best lap, gap to pole and place) for ResultHold seconds, else the car on
        // a timed lap, else the next car on its way to the green, else the last result while the next one is in its box.
        void BoxFocus(SingleRun s1)
        {
            var runners = s1.Active.Where(r => !r.CalledIn).ToList();
            var timed = runners.FirstOrDefault(r => r.Before != null && r.Fresh >= 1);
            var next = runners.FirstOrDefault(r => r.LeftAt >= 0);
            BoxLapMs = 0; BoxLapAge = 1e9;
            if (resultCar >= 0 && Wall - resultAt < ResultHold) { BoxKind = BoxShow.Result; BoxCar = resultCar; }
            else if (timed != null)
            {
                BoxKind = BoxShow.Live; BoxCar = timed.Car;
                if (timed.LastLapAt >= 0) { BoxLapMs = timed.LastLapMs; BoxLapAge = Wall - timed.LastLapAt; }
            }
            else if (next != null) { BoxKind = BoxShow.Waiting; BoxCar = next.Car; }
            else if (resultCar >= 0) { BoxKind = BoxShow.Result; BoxCar = resultCar; }
            else { BoxKind = BoxShow.None; BoxCar = -1; }
        }

        // Is this car on one of its timed laps right now (single car)?
        public bool OnTimedLap(int car) { return single != null && single.Active.Any(r => r.Car == car && !r.CalledIn && r.Before != null && r.Fresh >= 1); }

        // Is it time for the next car to roll off? (ReleaseMode: when the car before reaches pit road, N seconds after
        // it left its box, when it is N% round its last timed lap, or so it takes the green N s after that one finishes.)
        bool ReleaseNext(SingleRun s1)
        {
            if (s1.Active.Count == 0) return true;
            var prev = s1.Last;
            if (prev == null || !s1.Active.Contains(prev)) return true;          // the car before has finished its run
            if (s1.Active.Count >= Math.Max(1, MaxOut)) return false;             // never more than MaxOut out at once
            if (prev.LeftAt < 0) return false;                                     // still in its box
            switch (ReleaseMode)
            {
                case Release.Seconds:
                    return Wall - prev.LeftAt >= ReleaseValue;
                case Release.LapPercent:
                    if (prev.CalledIn) return true;                                // past its last timed lap
                    if (prev.Before == null || prev.Fresh < Laps) return false;    // not on its last timed lap yet
                    var t = Timing.Of(prev.Car);
                    if (t == null || Timing.TrackLen <= 0) return false;
                    return t.Path / Timing.TrackLen * 100 >= ReleaseValue;
                case Release.Gap:
                    return GapDue(s1, prev);
                default:
                    return false;                                                  // pit road: when its run ends
            }
        }

        // ---- green gap release ----
        // Every pit box is a different distance from the exit, so a fixed release point gives a different gap on track.
        // Instead the next car leaves its box when (the car before's predicted finish + the gap - its own predicted out
        // lap) comes round. Out lap = time to the exit (a constant + box-to-exit distance / pit road speed) + exit to the
        // line, learned from the cars that have already run, all on the game clock (time acceleration doesn't matter).
        readonly List<double> pitSpeeds = new List<double>();
        string gapNote = "";
        double G() { return Addr.GameSeconds(m); }
        float Dl(int c) { return m.F32(Addr.Dlong + c * Addr.Ocd); }
        double TrackM() { double l = m.F64(Addr.TrackLength); return l > 100 && l < 100000 ? l : 0; }
        double Ahead(double from, double to)                   // metres from `from` forward to `to` round the lap
        {
            double L = TrackM();
            if (L <= 0 || double.IsNaN(from) || double.IsNaN(to)) return double.NaN;
            double d = (to - from) % L;
            return d < 0 ? d + L : d;
        }
        // pit road speed (m/s): from 3+ runs with boxes spread along pit road, the slope of (time to the exit) against
        // (box to exit distance); else the median of 1 s samples of the cars leaving
        double PitSpeed(List<Run> done)
        {
            var pts = done.Select(x => Tuple.Create(Ahead(x.StallDl, x.ExitDl), x.ExitG - x.ReleasedG)).Where(x => !double.IsNaN(x.Item1)).ToList();
            if (pts.Count >= 3 && pts.Max(x => x.Item1) - pts.Min(x => x.Item1) > 150)
            {
                double mx = pts.Average(x => x.Item1), my = pts.Average(x => x.Item2);
                double sxx = pts.Sum(x => (x.Item1 - mx) * (x.Item1 - mx)), sxy = pts.Sum(x => (x.Item1 - mx) * (x.Item2 - my));
                double v = sxy > 0 ? sxx / sxy : double.NaN;
                if (v > 3 && v < 60) return v;
            }
            if (pitSpeeds.Count < 5) return double.NaN;
            var s = pitSpeeds.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }

        // seconds from leaving a box at lap distance stallDl to the green, from the last few runs (NaN: none yet)
        double PredictOut(float stallDl, Run self, out double toLine)
        {
            toLine = double.NaN;
            var done = single.History.Where(x => x != self && x.ExitG > x.ReleasedG && x.ReleasedG >= 0 && x.GreenG > x.ExitG &&
                !float.IsNaN(x.StallDl) && !float.IsNaN(x.ExitDl)).Reverse().Take(6).ToList();
            if (done.Count == 0) return double.NaN;
            toLine = done.Average(x => x.GreenG - x.ExitG);
            double v = PitSpeed(done), mine = Ahead(stallDl, done[0].ExitDl);
            if (double.IsNaN(mine)) return double.NaN;
            bool useDist = !double.IsNaN(v) && done.All(x => !double.IsNaN(Ahead(x.StallDl, x.ExitDl)));
            double pitConst = done.Average(x => (x.ExitG - x.ReleasedG) - (useDist ? Ahead(x.StallDl, x.ExitDl) / v : 0));
            return Math.Max(1, pitConst + (useDist ? mine / v : 0) + toLine);
        }

        // this car's timed lap: from its live gap to pole on the lap it is on (past 30% of it), else its last one, else
        // the median of everyone's laps so far, else extrapolated from this lap so far
        double LapGuess(Run p)
        {
            double f = TrackM() > 0 ? Ahead(0, Dl(p.Car)) / TrackM() : 0;
            int delta;
            if (p.LapStartG >= 0 && f > 0.3 && f < 0.98 && LiveBoard.Count > 0 && Timing.Delta(p.Car, LiveBoard[0].Item1, LiveBoard[0].Item2, out delta))
                return (LiveBoard[0].Item2 + delta / f) / 1000.0;
            if (p.LastLapMs > 0) return p.LastLapMs / 1000.0;
            var any = single.History.Where(x => x.LastLapMs > 0).Select(x => x.LastLapMs).ToList();
            if (any.Count > 0) return any.OrderBy(x => x).ElementAt(any.Count / 2) / 1000.0;
            double L = TrackM(), into = Ahead(0, Dl(p.Car)), el = G() - p.LapStartG;
            if (p.LapStartG >= 0 && L > 0 && into > 0.2 * L && into < 0.98 * L && el > 1) return el * L / into;
            return double.NaN;
        }

        double PredictFinish(Run p)
        {
            if (p.FinishG >= 0) return p.FinishG;
            double lap = LapGuess(p);
            if (double.IsNaN(lap)) return double.NaN;
            if (p.GreenG >= 0) return p.LapStartG + (Laps - Math.Min(Laps, p.Fresh) + 1) * lap;
            double toLine, outT = PredictOut(p.StallDl, p, out toLine);
            double green = p.ExitG >= 0 && !double.IsNaN(toLine) ? p.ExitG + toLine : p.ReleasedG + outT;
            return double.IsNaN(green) ? double.NaN : green + Laps * lap;
        }

        bool GapDue(SingleRun s1, Run prev)
        {
            int next = s1.Order[s1.K + 1];
            double toLine, outT = PredictOut(Dl(next), null, out toLine), fin = PredictFinish(prev), now = G();
            if (double.IsNaN(outT) || double.IsNaN(fin)) return false;          // not enough to go on yet (the first car's out lap)
            double at = fin + ReleaseValue - outT;
            if (now < at) return false;
            gapNote = string.Format(CultureInfo.InvariantCulture, " (out lap about {0:0.0} s; aiming for the green {1:0} s after #{2} finishes{3})",
                outT, ReleaseValue, Num(prev.Car), now - at > 3 ? string.Format(CultureInfo.InvariantCulture, ", {0:0.0} s late", now - at) : "");
            return true;
        }

        void LogGap(Run r)
        {
            if (r.GapLogged || r.Prev == null || r.Prev.FinishG < 0 || r.GreenG < 0) return;
            r.GapLogged = true;
            double gap = r.GreenG - r.Prev.FinishG;
            Log(string.Format(CultureInfo.InvariantCulture, "#{0} took the green {1:0.0} s {2} #{3} finished{4}", Num(r.Car), Math.Abs(gap), gap >= 0 ? "after" : "before",
                Num(r.Prev.Car), ReleaseMode == Release.Gap ? string.Format(CultureInfo.InvariantCulture, " (aim {0:0} s)", ReleaseValue) : ""));
        }

        // pit road speed samples while leaving, and the game clock marks of a run
        void RunMarks(Run r, uint s, List<uint> cur)
        {
            int c = r.Car;
            if (Leaving.Contains(s))
            {
                float dl = Dl(c); double g = G();
                if (r.LastG < 0 || g > r.LastG + 1)                               // 1 s samples: the clock moves in 1/36 s steps
                {
                    if (r.LastG >= 0)
                    {
                        double v = Ahead(r.LastDl, dl) / (g - r.LastG);
                        if (v > 3 && v < 60) { pitSpeeds.Add(v); if (pitSpeeds.Count > 2000) pitSpeeds.RemoveRange(0, 500); }
                    }
                    r.LastDl = dl; r.LastG = g;
                }
            }
            if (r.ExitG < 0 && r.LeftAt >= 0 && OnTrack.Contains(s)) { r.ExitG = G(); r.ExitDl = Dl(c); }
            if (cur.Count == 0) return;
            r.LapStartG = cur[Math.Min(cur.Count, Laps) - 1] / 1000.0;
            if (r.GreenG < 0) { r.GreenG = cur[0] / 1000.0; LogGap(r); }
            int done = Math.Min(cur.Count - 1, Laps);
            if (done > r.LapsDone)
            {
                for (int i = r.LapsDone + 1; i <= done; i++) r.LapMs.Add((int)(cur[i] - cur[i - 1]));
                r.LapsDone = done;
                r.LastLapMs = (int)(cur[done] - cur[done - 1]); r.LastLapAt = Wall;
            }
            if (r.FinishG < 0 && cur.Count > Laps)
            {
                r.FinishG = cur[Laps] / 1000.0;
                foreach (var x in single.History.Where(x => x.Prev == r)) LogGap(x);
            }
        }

        // laps of a run from its line crossings (the game clock), else the wall clock when a crossing time looks wrong
        List<double> RunLaps(Run r, List<uint> cur)
        {
            var laps = new List<double>();
            for (int i = 1; i < Math.Min(cur.Count, Laps + 1); i++)
            {
                double d = (cur[i] - (double)cur[i - 1]) / 1000.0;
                if (d > 5 && d < 400) laps.Add(d);
                else if (i < r.WallAt.Count) laps.Add(Math.Round(r.WallAt[i] - r.WallAt[i - 1], 3));
            }
            // the crossings can be gone (the game clears them when the session ends): the laps seen as they happened
            if (laps.Count < r.LapMs.Count) laps = r.LapMs.Where(ms => ms > 5000 && ms < 400000).Select(ms => ms / 1000.0).ToList();
            return laps;
        }

        // The game sends every car to the garage as practice ends, a moment before the session changes: a car on its run
        // going to the garage then is the session ending, not the car giving up.
        bool FieldToGarage() { return field.Count >= 4 && field.Count(c => Garage.Contains(St(c))) >= field.Count / 2; }

        // the run's last timed lap is done: what the timing box shows during its in-lap
        void RunResult(Run r, List<uint> cur)
        {
            var laps = RunLaps(r, cur);
            double score = Score(laps);
            if (score >= 9e9) return;
            int best = (int)Math.Round(score * 1000);
            var others = results.Where(x => x.Item1 < 9e9 && x.Item2 != r.Car).Select(x => (int)Math.Round(x.Item1 * 1000)).ToList();
            resultCar = r.Car; resultAt = Wall; ResultBestMs = best;
            ResultHasDiff = others.Count > 0;
            ResultDiffMs = ResultHasDiff ? best - others.Min() : 0;
            ResultPos = 1 + others.Count(o => o < best);
        }

        void RunTick(SingleRun s1, Run r)
        {
            int c = r.Car;
            uint s = St(c);
            Pace(c, true);                                               // the game may reset it: keep it on
            if (r.LeftAt < 0 && (Leaving.Contains(s) || OnTrack.Contains(s))) r.LeftAt = Wall;
            if (c == Me && r.LeftAt < 0 && PlayerLeftBox(r))
            {
                r.LeftAt = Wall;                                         // moved off its stall: out of the box, whatever the state reads
                Log(string.Format("your car: left the box ({0:0.0} s after the go)", Wall - r.Started));
            }
            if (c == Me && playerHold && r.LeftAt < 0) r.Started = Wall;  // ready screen up: the clock waits for you
            if (c == Me && r.LeftAt >= 0) playerHold = false;             // pulled out: the ready screen is done
            if (r.Before == null && r.LeftAt < 0 && !Leaving.Contains(s) && !OnTrack.Contains(s) && !PitIn.Contains(s) && Wall - r.Started > DnqSeconds)
            {
                bool had = results.Any(x => x.Item2 == c);
                Log(had ? string.Format("{0} didn't pull out in {1:0} s: no run (it keeps what it had)", Car(c), DnqSeconds)
                        : string.Format("{0} didn't pull out in {1:0} s: DNQ (starts at the back)", Car(c), DnqSeconds));
                if (c == Me) { Call(3, had ? "NO RUN: you didn't pull out in time" : "DNQ: you didn't pull out in time", 15); Drive(false); playerHold = false; }
                Wr(c, Countdown, Hold);                                  // back to sitting in its box
                Pace(c, false);
                if (!had) dnq.Add(c);
                r.WavedOff = true;                                       // (counts for nothing)
                PostResult(r, new List<double>(), true);
                s1.Active.Remove(r);
                ShowSingleBoard();
                return;
            }
            // The first S/F crossing after pit exit ends the out lap and starts timed lap 1. Crossings made while
            // still leaving pit road (stalls before the line) don't count: counting starts once the car is on track.
            if (r.Before == null && OnTrack.Contains(s)) { r.Before = Crossings(c); r.JoinDl = Dl(c); }
            var cur = r.Before != null ? Crossings(c).Skip(r.Before.Count).ToList() : new List<uint>();
            // Onto the track just before the line (a box ahead of the line, or off pit road early): crossing the line
            // moments later is still the out lap - the timed laps start at the next crossing. (Live 2026-10-26, the
            // player at Talladega: a 3 s "out lap" and a lap from a standing start counted as the timed lap.)
            if (r.Before != null && !r.JoinChecked && cur.Count > 0)
            {
                r.JoinChecked = true;
                double len = TrackM(), toLine = Ahead(r.JoinDl, 0);
                if (len > 0 && !double.IsNaN(toLine) && toLine < 0.4 * len)
                {
                    Log(string.Format("{0} came onto the track {1:0} m before the line: that crossing is its out lap, the timed lap starts next time by", Car(c), toLine));
                    r.Before.Add(cur[0]);
                    cur.RemoveAt(0);
                }
            }
            r.Fresh = cur.Count;
            while (r.WallAt.Count < r.Fresh) r.WallAt.Add(Wall);
            RunMarks(r, s, cur);
            // Out of it before its timed laps are done - crashed or broke (tow, limping in, off to the garage) or back on
            // pit road by itself (out of fuel): the run is over and the next car goes at once. A timed lap it did finish
            // stands as its time; none = DNQ. It never goes out again.
            string over = null;
            if (!r.CalledIn)
            {
                if (Garage.Contains(s) && FieldToGarage()) return;          // practice is ending: PracticeOver closes the run
                if (Damaged.Contains(s) || (r.LeftAt >= 0 && Garage.Contains(s))) over = StateName(s);
                else if (r.Before != null && (c == Me ? PlayerStopped(s) : Entering.Contains(s) || Wait.Contains(s) || Stall.Contains(s))) over = "came back to pit road on its own";
                else if (WaveOffDue(r)) { }
                else if (r.Fresh >= Laps + 1)
                {
                    Wr(c, RunEnd, Rd(c, Lap)); r.CalledIn = true; RunResult(r, cur);
                    if (c == Me) Call(2, resultCar == Me ? string.Format("RUN COMPLETE: {0}, P{1} - bring it in to your box and climb out", Ms(ResultBestMs), ResultPos) : "RUN COMPLETE - bring it in to your box and climb out");
                }
                else if (Leaving.Contains(s) || OnTrack.Contains(s)) Wr(c, RunEnd, Rd(c, Lap) + 50);
                if (c == Me && !r.CalledIn && over == null)
                    Call(1, r.Before == null && !OnTrack.Contains(s) ? "YOUR RUN: GO NOW"
                            : r.Fresh == 0 ? "OUT LAP: your time starts at the line"
                            : string.Format("TIMED LAP {0} OF {1}", Math.Min(Laps, r.Fresh), Laps));
            }
            // (the player can't be called in: a whole lap more on track after the run also ends it)
            bool back = r.CalledIn && (Entering.Contains(s) || Wait.Contains(s) || Garage.Contains(s) || Stall.Contains(s) || (c == Me && r.Fresh >= Laps + 2));
            if (over == null && !back && Wall - r.Started < 600) return;
            // the run is over: its laps
            var laps = RunLaps(r, cur);
            // a lap over twice the usual (median of the times so far, else this run's best) doesn't count (a spin, a tow)
            // (not in a 4-lap average run: every lap of the run counts, a slow one included)
            var timedSoFar = results.Where(x => x.Item1 < 9e9).Select(x => x.Item1).OrderBy(x => x).ToList();
            double usual = timedSoFar.Count >= 3 ? timedSoFar[timedSoFar.Count / 2] : laps.Count > 0 ? laps.Min() : 0;
            if (usual > 0 && !Average)
            {
                var slow = laps.Where(x => x > 2 * usual).ToList();
                if (slow.Count > 0) Log(Car(c) + ": lap(s) " + string.Join(", ", slow.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)).ToArray()) + " not counted (over twice the usual lap)");
                laps = laps.Where(x => x <= 2 * usual).ToList();
            }
            if (over != null && c == Me && Lanes && Average && r.Before != null && laps.Count < Laps && over == "came back to pit road on its own")
            {
                r.WavedOff = true; over = null;
                Log("your car: back on pit road before the last timed lap - waved off");
                Call(2, "WAVED OFF: this run doesn't count", 15);
            }
            if (over != null)
            {
                bool had = results.Any(x => x.Item2 == c && x.Item1 < 9e9);
                if (laps.Count == 0 && !had) { Log("#" + Num(c) + " DNQ: " + over + " before finishing a timed lap"); dnq.Add(c); }
                else if (laps.Count == 0) Log(Car(c) + ": " + over + " before finishing a timed lap - it keeps what it had");
                else Log(Car(c) + ": " + over + " - the timed lap(s) it finished stand");
            }
            PostResult(r, laps);
            if (c == Me && over != null) Call(laps.Count == 0 ? 3 : 2, laps.Count == 0 ? (Official(Me) < 9e9 ? "RUN OVER: no timed lap - your time stands" : "DNQ: no timed lap finished") : "RUN OVER: your timed lap(s) stand", 15);
            else if (c == Me) Call(PlayerCallKind, PlayerCall, 8);
            Log(Car(c) + (r.WavedOff ? ": waved off" + (laps.Count > 0 ? " (laps " + string.Join(", ", laps.Select(t => t.ToString("0.000", CultureInfo.InvariantCulture)).ToArray()) + ")" : "")
                : laps.Count > 0 ? ": laps " + string.Join(", ", laps.Select(t => t.ToString("0.000", CultureInfo.InvariantCulture)).ToArray()) + ScoreText(laps) : ": no lap time read"));
            Pace(c, false);
            s1.Active.Remove(r);
            ShowSingleBoard();
        }

        void ShowSingleBoard()
        {
            var b = new List<string>();
            int i = 0;
            int cut = Starters > 0 && single != null ? FieldCut(single.Order.Distinct().Count()) : 0;
            foreach (var r in results.OrderBy(r => r.Item1))
            {
                if (cut > 0 && i == cut) b.Add("    ---- the field: " + cut + " cars ----");
                b.Add(string.Format("{0,2}. #{1,-4} {2,-24} {3}", ++i, Num(r.Item2), Name(r.Item2), r.Item1 < 9e9 ? LapFmt.Lap((int)Math.Round(r.Item1 * 1000))
                    : withdrawn.Contains(r.Item2) ? "time given up (Lane 1)" : dnq.Contains(r.Item2) ? "DNQ" : "no time"));
            }
            if (single != null)
            {
                foreach (var r in single.Active)
                    b.Add(string.Format("    #{0,-4} {1,-24} {2}", Num(r.Car), Name(r.Car), r.Lane > 0 ? "on track (lane " + r.Lane + ")" : "on track"));
                foreach (int c in single.Order.Skip(single.K + 1))
                    b.Add(string.Format("    #{0,-4} {1,-24} {2}", Num(c), Name(c), "waiting"));
                if (reqOpen) b.Add("    Lane 1: " + Nums(lane1Q) + "    Lane 2: " + Nums(lane2Q));
            }
            Board = b;
        }

        void SingleDone()
        {
            Log("all cars have run; holding everyone in their boxes until practice ends");
            TakeSingleRuns();
            phase = Phase.Done;
            WriteSingleState("done");
        }

        void TakeSingleRuns()
        {
            runs.Clear(); runNums.Clear();
            foreach (var r in results)
            {
                if (r.Item3.Count == 0 || r.Item1 >= 9e9) continue;
                // a 4-lap average: every lap written as the average, so the game's standings (best lap) rank by it
                runs[r.Item2] = Average ? Enumerable.Repeat((int)Math.Round(r.Item1 * 1000), r.Item3.Count).ToList()
                                        : r.Item3.Select(t => (int)Math.Round(t * 1000)).ToList();
                runNums[r.Item2] = Num(r.Item2);
            }
            // the finishing order: by time, then no time, then the cars that never had a run (practice ended)
            var order = results.OrderBy(r => r.Item1).Select(r => r.Item2).ToList();
            if (single != null) order.AddRange(single.Order.Distinct().Where(c => !order.Contains(c)));
            ApplyCut(order);
            if (runs.Count > 0 || dnqNums.Count > 0) SaveRuns();
            WriteResults(Average ? "INDY OVAL QUALIFYING (" + Laps + "-LAP AVERAGE)" : "SINGLE CAR QUALIFYING", order.Select(c => { double sc = Official(c); return Tuple.Create(c, sc < 9e9 ? (int?)Math.Round(sc * 1000) : null); }).ToList());
        }

        void PracticeOver()
        {
            if (koRounds != null)
            {
                Log("PRACTICE ENDED before the knockout rounds were done: no grid written (make practice longer)");
                koRounds = null;
                phase = Phase.Idle;
                FinalState();
                return;
            }
            // runs still going when practice ended: the timed laps they finished stand (no DNQ - the session ran out)
            if (single != null)
                foreach (var r in single.Active.ToList())
                {
                    var laps = RunLaps(r, new List<uint>());
                    PostResult(r, laps);
                    Log(Car(r.Car) + ": practice ended during its run - " + (laps.Count > 0
                        ? "laps " + string.Join(", ", laps.Select(t => t.ToString("0.000", CultureInfo.InvariantCulture)).ToArray()) + (Score(laps) < 9e9 ? " stand" + ScoreText(laps) : ScoreText(laps))
                        : "no timed lap finished, no time"));
                    Pace(r.Car, false);
                    single.Active.Remove(r);
                }
            Log(single != null && single.K + 1 < single.Order.Count
                ? "PRACTICE ENDED after " + (single.K + 1) + " of " + single.Order.Count + " cars (make practice longer): the cars without a run go to the back"
                : "practice ended");
            TakeSingleRuns();
            phase = runs.Count > 0 ? Phase.GridWait : Phase.Idle;
            FinalState();
        }

        // ---- knockout rounds ----
        List<Tuple<double, int>> koRounds;
        int koRound;
        readonly List<Dictionary<int, int>> koResults = new List<Dictionary<int, int>>();
        readonly List<HashSet<int>> koEntered = new List<HashSet<int>>();
        List<int> koEntrants;
        HashSet<int> koCars, koReleased;
        Dictionary<int, List<Tuple<uint, bool>>> koRec;
        Dictionary<int, int> koSeen, koAtFlag;
        bool koFlag;
        double koElapsed, koLastGame, koLastWall, koBreakEnd, koNextPrint;

        public static List<Tuple<double, int>> ParseRounds(string text)
        {
            var list = new List<Tuple<double, int>>();
            foreach (string part in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] mn = part.Split(':');
                double min; int adv;
                if (mn.Length == 2 && double.TryParse(mn[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out min) && int.TryParse(mn[1].Trim(), out adv) && min > 0)
                    list.Add(Tuple.Create(min, Math.Max(1, adv)));
            }
            return list;
        }

        void StartKnockout()
        {
            if (Mode == Format.IndyRoad) { StartIndyRoad(); return; }
            koRounds = ParseRounds(Rounds);
            if (koRounds.Count == 0) { Log("no knockout rounds set (minutes : cars, e.g. 25:24, 10:12, 5:12)"); koRounds = null; return; }
            koResults.Clear(); koEntered.Clear(); koEntrants = null; koRound = 0;
            double total = koRounds.Sum(r => r.Item1) + BreakSeconds / 60 * (koRounds.Count - 1);
            Log(string.Format("KNOCKOUT QUALIFYING: {0} rounds, {1}. Needs about {2:0} minutes of practice.", koRounds.Count,
                string.Join(", ", koRounds.Select((r, i) => "R" + (i + 1) + " " + r.Item1.ToString("0.#", CultureInfo.InvariantCulture) + " min" + (i < koRounds.Count - 1 ? " (top " + r.Item2 + " advance)" : "")).ToArray()), total + 3));
            StartRound();
        }

        void StartIndyRoad()
        {
            var cars = field.Where(c => Wait.Contains(St(c))).ToList();
            string how;
            if (IndySplit == Split.Rating)
            {
                cars = cars.OrderByDescending(c => m.F32(Block0 + c * Stride + QpRating)).ToList();
                how = "driver rating, alternating";
            }
            else if (IndySplit == Split.List)
            {
                var wanted = (Order ?? "").Split(new[] { ',', ' ', ';', (char)13, (char)10, (char)9 }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('#')).ToList();
                cars = cars.OrderBy(c => { int k = wanted.IndexOf(Num(c)); return k < 0 ? int.MaxValue : k; }).ThenBy(c => rnd.Next()).ToList();
                how = "the running order list, alternating";
            }
            else { cars = cars.OrderBy(c => rnd.Next()).ToList(); how = "random draw"; }
            indyG1 = cars.Where((c, i) => i % 2 == 0).ToList();
            indyG2 = cars.Where((c, i) => i % 2 == 1).ToList();
            koRounds = new List<Tuple<double, int>> { Tuple.Create(IndyGroupMin, IndyGroupAdv), Tuple.Create(IndyGroupMin, IndyGroupAdv),
                Tuple.Create(IndyF12Min, IndyF12Adv), Tuple.Create(IndyF6Min, 0) };
            koResults.Clear(); koEntered.Clear(); koEntrants = null; koRound = 0;
            double total = 2 * IndyGroupMin + IndyF12Min + IndyF6Min + BreakSeconds / 60 * 3;
            Log(string.Format(CultureInfo.InvariantCulture, "INDY ROAD / STREET QUALIFYING: Group 1 and Group 2 ({0:0.#} min, top {1} of each through), Fast 12 ({2:0.#} min, top {3} through), " +
                "Fast Six ({4:0.#} min). Groups by {5}. Needs about {6:0} minutes of practice.", IndyGroupMin, IndyGroupAdv, IndyF12Min, IndyF12Adv, IndyF6Min, how, total + 3));
            Log("Group 1: " + string.Join(" ", indyG1.Select(c => "#" + Num(c)).ToArray()));
            Log("Group 2: " + string.Join(" ", indyG2.Select(c => "#" + Num(c)).ToArray()));
            StartRound();
        }

        // which cars run in this Indy segment: a group, the top of both groups, or the top of the Fast 12
        HashSet<int> IndyRoundCars()
        {
            if (koRound == 0) return new HashSet<int>(indyG1);
            if (koRound == 1) return new HashSet<int>(indyG2);
            if (koRound == 2)
            {
                Func<List<int>, Dictionary<int, int>, IEnumerable<int>> top = (grp, res) =>
                    grp.Where(c => res.ContainsKey(c)).OrderBy(c => res[c]).Concat(grp.Where(c => !res.ContainsKey(c))).Take(IndyGroupAdv);
                return new HashSet<int>(top(indyG1, koResults[0]).Concat(top(indyG2, koResults[1])));
            }
            return new HashSet<int>(koEntrants ?? new List<int>());
        }

        void StartRound()
        {
            var r = koRounds[koRound];
            bool last = koRound == koRounds.Count - 1;
            koCars = Mode == Format.IndyRoad ? IndyRoundCars() : koEntrants == null ? new HashSet<int>(field) : new HashSet<int>(koEntrants);
            koEntered.Add(koCars);
            koRec = koCars.ToDictionary(c => c, c => new List<Tuple<uint, bool>>());
            koSeen = koCars.ToDictionary(c => c, c => Crossings(c).Count);
            koAtFlag = new Dictionary<int, int>();
            koReleased = new HashSet<int>();
            koFlag = false;
            koElapsed = 0; koLastGame = Addr.GameSeconds(m); koLastWall = Wall; koNextPrint = 60;
            phase = Phase.Running;
            Log(string.Format("{0}: {1} cars, {2} minutes{3}", RoundName(koRound), koCars.Count, r.Item1.ToString("0.#", CultureInfo.InvariantCulture), last ? "" : ", top " + r.Item2 + " advance"));
        }

        // Round time from the game's session clock; real time while that is frozen but cars are moving; stops
        // while paused (the game clock stands and nobody moves).
        double RoundClock()
        {
            double g = Addr.GameSeconds(m), w = Wall, dw = w - koLastWall;
            bool paused = m.U8(Addr.SimState) == 3;                  // (the player's speed reads its last value while paused)
            if (paused) { }
            else if (g > koLastGame) koElapsed += Math.Min(g - koLastGame, dw * 64 + 0.5);   // follows time acceleration
            else if (outNow.Any(c => Speed(c) > 5)) koElapsed += dw;
            koLastGame = g; koLastWall = w;
            return koElapsed;
        }

        void KoCollect()
        {
            foreach (int c in koRec.Keys.ToList())
            {
                var cur = Crossings(c);
                if (cur.Count <= koSeen[c]) continue;
                uint s = St(c);
                bool on = OnTrack.Contains(s) || PitIn.Contains(s);
                for (int i = koSeen[c]; i < cur.Count; i++) koRec[c].Add(Tuple.Create(cur[i], on));
                koSeen[c] = cur.Count;
            }
        }

        // Laps that started and ended on the track, started before time ran out.
        Dictionary<int, int> KoBests()
        {
            var o = new Dictionary<int, int>();
            foreach (var kv in koRec)
            {
                int lim = koAtFlag.ContainsKey(kv.Key) ? koAtFlag[kv.Key] : kv.Value.Count;
                int best = int.MaxValue;
                for (int i = 0; i + 1 < kv.Value.Count; i++)
                {
                    var a = kv.Value[i]; var b = kv.Value[i + 1];
                    long d = (long)b.Item1 - a.Item1;
                    if (a.Item2 && b.Item2 && i < lim && d > 5000 && d < 400000 && d < best) best = (int)d;
                }
                if (best < int.MaxValue) o[kv.Key] = best;
            }
            return o;
        }

        void KnockoutTick()
        {
            var r = koRounds[koRound];
            bool last = koRound == koRounds.Count - 1;
            int cutoff = last ? 0 : r.Item2;
            if (phase == Phase.Break)
            {
                Upkeep(null);
                double left = koBreakEnd - Wall;
                status = string.Format("Break before round {0}: {1:0} s", koRound + 1, Math.Max(0, left));
                LiveHeader = string.Format("{0} STARTS IN {1}:{2:00}", RoundName(koRound), (int)Math.Max(0, left) / 60, (int)Math.Max(0, left) % 60);
                LiveBoard = koResults[koResults.Count - 1].OrderBy(kv => kv.Value).Select(kv => Tuple.Create(kv.Key, kv.Value)).ToList();
                WriteState("break", koRound, left, koResults[koResults.Count - 1].OrderBy(kv => kv.Value).Select(kv => Tuple.Create(kv.Key, (int?)kv.Value)).ToList(), koRounds[koRound - 1].Item2, koRound + 1);
                if (left <= 0) StartRound();
                return;
            }
            Upkeep(outNow);
            if (field.Contains(Me) && !koCars.Contains(Me) && OnTrack.Contains(St(Me))) Call(3, "NOT IN " + RoundName(koRound) + ": these laps don't count");
            if (koEntrants == null && Mode != Format.IndyRoad && !koFlag) // round 1: late arrivals join in (not into fixed Indy groups; not after the flag)
                foreach (int c in field)
                    if (!koRec.ContainsKey(c)) { koRec[c] = new List<Tuple<uint, bool>>(); koSeen[c] = Crossings(c).Count; koCars.Add(c); }
            double el = RoundClock(), dur = r.Item1 * 60, remain = Math.Max(0, dur - el);
            foreach (int c in koCars)                                    // released a few at a time (none once time is up)
                if (!koFlag && !koReleased.Contains(c) && (Wait.Contains(St(c)) || (c == Me && !Garage.Contains(St(c)))))
                {
                    outNow.Add(c); Wr(c, Countdown, 1 + 8 * koReleased.Count); koReleased.Add(c);
                    SetQualFuel(c);
                    if (c == Me) { meInQual = true; Drive(true); HoldForReady(RoundName(koRound) + ": GO - set your best lap"); }
                }
            if (!koFlag && outNow.Contains(Me)) { PlayerTimer = "TIME LEFT|" + TimerText(remain); PlayerTimerUrgent = remain < 60; }
            KoCollect();
            foreach (int c in koCars)
                if (outNow.Contains(c) && Damaged.Contains(St(c)))
                {
                    outNow.Remove(c); Pace(c, false);                         // out of this round; held once it's back in
                    Log("#" + Num(c) + " out of round " + (koRound + 1) + ": damaged (" + StateName(St(c)) + ")");
                }
            foreach (int c in koCars) Pace(c, outNow.Contains(c));
            if (!koFlag)
            {
                foreach (int c in koCars)
                {
                    uint s = St(c);
                    if (Leaving.Contains(s) || OnTrack.Contains(s)) Wr(c, RunEnd, Rd(c, Lap) + 50);
                }
                if (remain <= 0)
                {
                    koFlag = true;
                    foreach (int c in koCars)
                    {
                        koAtFlag[c] = koRec[c].Count;
                        uint s = St(c);
                        if (OnTrack.Contains(s)) Wr(c, RunEnd, Rd(c, Lap) + 1);  // finish this lap, then in
                        else if (Leaving.Contains(s)) Wr(c, RunEnd, Rd(c, Lap));
                        else if (Wait.Contains(s)) { outNow.Remove(c); Wr(c, Countdown, Hold); }
                    }
                    Log(RoundName(koRound) + ": time! Cars finish the lap they are on and come in.");
                    if (koCars.Contains(Me)) Call(2, RoundName(koRound) + ": CHECKERED - finish this lap and come in");
                }
            }
            else
            {
                foreach (int c in koCars)
                {
                    uint s = St(c);
                    if (outNow.Contains(c) && (Wait.Contains(s) || Garage.Contains(s) || Stall.Contains(s))) outNow.Remove(c);
                    // the player isn't called in: done once over the line after the flag (or back on pit road)
                    else if (c == Me && outNow.Contains(c))
                    {
                        int atFlag; List<Tuple<uint, bool>> rec;
                        if (Entering.Contains(s) || !koAtFlag.TryGetValue(c, out atFlag) || !koRec.TryGetValue(c, out rec) || rec.Count > atFlag) outNow.Remove(c);
                    }
                }
                if (!koCars.Any(c => outNow.Contains(c))) { EndRound(); return; }
            }
            var board = KoBests().OrderBy(kv => kv.Value).ToList();
            if (el >= koNextPrint && !koFlag)
            {
                koNextPrint += 60;
                Log(string.Format("R{0} {1:0.0} min left: ", koRound + 1, remain / 60) + string.Join("  ", board.Take(6).Select((kv, i) => (i + 1) + ". #" + Num(kv.Key) + " " + Ms(kv.Value)).ToArray()));
            }
            status = string.Format("{0} ({1} of {2}): {3}", RoundName(koRound), koRound + 1, koRounds.Count, koFlag ? "checkered, cars coming in" : string.Format("{0}:{1:00} left", (int)remain / 60, (int)remain % 60));
            Board = board.Select((kv, i) => string.Format("{0,2}. #{1,-4} {2,-24} {3}{4}", i + 1, Num(kv.Key), Name(kv.Key), Ms(kv.Value), cutoff > 0 && i == cutoff - 1 ? "   ---- cut" : "")).ToList();
            LiveHeader = koFlag ? RoundName(koRound) + "  -  CHECKERED FLAG" : string.Format("{0}  -  {1}:{2:00}", RoundName(koRound), (int)remain / 60, (int)remain % 60);
            LiveBoard = board.Select(kv => Tuple.Create(kv.Key, kv.Value)).ToList();
            WriteState(koFlag ? "checkered" : "running", koRound + 1, remain, board.Select(kv => Tuple.Create(kv.Key, (int?)kv.Value)).ToList(), cutoff);
        }

        static string Ms(int ms) { return LapFmt.Time(ms); }                // m:ss.fff from a minute up

        void EndRound()
        {
            KoCollect();
            var best = KoBests();
            koResults.Add(best);
            var r = koRounds[koRound];
            bool last = koRound == koRounds.Count - 1;
            var ranked = best.OrderBy(kv => kv.Value).ToList();
            Log(RoundName(koRound) + " RESULTS: " + string.Join("  ", ranked.Select((kv, i) => (i + 1) + ". #" + Num(kv.Key) + " " + Ms(kv.Value) + (!last && i == r.Item2 - 1 ? " |" : "")).ToArray()));
            var noTime = koCars.Where(c => !best.ContainsKey(c)).ToList();
            if (noTime.Count > 0) Log("no time: " + string.Join(" ", noTime.Select(c => "#" + Num(c)).ToArray()));
            if (last) { if (Mode == Format.IndyRoad) IndyGrid(); else KnockoutGrid(); return; }
            var prev = koResults.Count > 1 ? koResults[koResults.Count - 2] : new Dictionary<int, int>();
            noTime = noTime.OrderBy(c => prev.ContainsKey(c) ? prev[c] : int.MaxValue).ToList();
            koEntrants = ranked.Select(kv => kv.Key).Concat(noTime).Take(r.Item2).ToList();
            if (koCars.Contains(Me))
            {
                int pos = ranked.FindIndex(kv => kv.Key == Me) + 1;
                bool through = Mode == Format.IndyRoad && koRound < 2 ? pos > 0 && pos <= IndyGroupAdv : koEntrants.Contains(Me);
                Call(through ? 0 : 3, (pos > 0 ? "P" + pos + " - " : "no time - ") + (through ? "THROUGH TO " + RoundName(koRound + 1) : "OUT IN " + RoundName(koRound)), 20);
            }
            if (ranked.Count < r.Item2) Log("only " + ranked.Count + " cars set a time; " + (r.Item2 - ranked.Count) + " more go through in their previous order");
            koRound++;
            koBreakEnd = Wall + BreakSeconds;
            phase = Phase.Break;
        }

        // The last round's cars by their time (no time: by the previous round), then each earlier round's
        // eliminated cars the same way, then cars that never set a time.
        void KnockoutGrid()
        {
            var grid = new List<int>(); var deciding = new Dictionary<int, int>(); var placed = new HashSet<int>();
            for (int r = koResults.Count - 1; r >= 0; r--)
            {
                var res = koResults[r]; var prev = r > 0 ? koResults[r - 1] : new Dictionary<int, int>();
                var mine = koEntered[r].Where(c => !placed.Contains(c)).ToList();
                foreach (int c in mine.Where(c => res.ContainsKey(c)).OrderBy(c => res[c])) { grid.Add(c); deciding[c] = res[c]; placed.Add(c); }
                foreach (int c in mine.Where(c => !res.ContainsKey(c)).OrderBy(c => prev.ContainsKey(c) ? prev[c] : int.MaxValue).ThenBy(c => c))
                {
                    grid.Add(c); placed.Add(c);
                    if (prev.ContainsKey(c)) deciding[c] = prev[c];
                }
            }
            foreach (int c in field.OrderBy(c => c)) if (placed.Add(c)) grid.Add(c);
            WriteGrid("KNOCKOUT QUALIFYING GRID", grid, deciding);
        }

        // Fast Six by its times (no time: by the Fast 12), the rest of the Fast 12 by its times, then the groups' other
        // cars from 13th: Group 1 on the odd spots, Group 2 on the even ones, each in its finishing order (no time last).
        void IndyGrid()
        {
            var grid = new List<int>(); var deciding = new Dictionary<int, int>(); var placed = new HashSet<int>();
            Action<IEnumerable<int>, Dictionary<int, int>, Dictionary<int, int>> add = (cars, res, prev) =>
            {
                foreach (int c in cars.Where(c => res.ContainsKey(c)).OrderBy(c => res[c]).ToList()) if (placed.Add(c)) { grid.Add(c); deciding[c] = res[c]; }
                foreach (int c in cars.Where(c => !res.ContainsKey(c)).OrderBy(c => prev != null && prev.ContainsKey(c) ? prev[c] : int.MaxValue).ToList())
                    if (placed.Add(c)) { grid.Add(c); if (prev != null && prev.ContainsKey(c)) deciding[c] = prev[c]; }
            };
            add(koEntered[3], koResults[3], koResults[2]);
            add(koEntered[2], koResults[2], null);
            Func<List<int>, Dictionary<int, int>, List<int>> rest = (grp, res) =>
                grp.Where(c => !placed.Contains(c)).OrderBy(c => res.ContainsKey(c) ? res[c] : int.MaxValue).ToList();
            var g1 = rest(indyG1, koResults[0]); var g2 = rest(indyG2, koResults[1]);
            for (int i = 0; i < Math.Max(g1.Count, g2.Count); i++)
                foreach (var pair in new[] { Tuple.Create(g1, koResults[0]), Tuple.Create(g2, koResults[1]) })
                    if (i < pair.Item1.Count && placed.Add(pair.Item1[i]))
                    {
                        grid.Add(pair.Item1[i]);
                        if (pair.Item2.ContainsKey(pair.Item1[i])) deciding[pair.Item1[i]] = pair.Item2[pair.Item1[i]];
                    }
            foreach (int c in field.OrderBy(c => c)) if (placed.Add(c)) grid.Add(c);
            WriteGrid("INDY ROAD / STREET QUALIFYING GRID", grid, deciding);
        }

        void WriteGrid(string title, List<int> grid, Dictionary<int, int> deciding)
        {
            ApplyCut(grid);
            // the times written to the game: the deciding time, nudged up where needed so the order holds
            runs.Clear(); runNums.Clear();
            int last = 0;
            var lines = new List<Tuple<int, int?>>();
            foreach (int c in grid)
            {
                int t = deciding.ContainsKey(c) ? Math.Max(deciding[c], last + 1) : last + 100;
                last = t;
                runs[c] = new List<int> { t };
                runNums[c] = Num(c);
                lines.Add(Tuple.Create(c, deciding.ContainsKey(c) ? (int?)deciding[c] : null));
            }
            WriteResults(title, lines);
            SaveRuns();
            WriteState("done", koRounds.Count, 0, lines, 0);
            koRounds = null;
            phase = Phase.Done;
        }

        void WriteResults(string title, List<Tuple<int, int?>> order)
        {
            var b = new List<string>();
            int i = 0;
            foreach (var o in order)
            {
                if (dnqSlots.Count > 0 && dnqSlots.Contains(o.Item1) && !b.Any(x => x.StartsWith("    ---- did not qualify"))) b.Add("    ---- did not qualify ----");
                b.Add(string.Format("{0,2}. #{1,-4} {2,-24} {3}{4}", ++i, Num(o.Item1), Name(o.Item1), o.Item2.HasValue ? Ms(o.Item2.Value) : "no time", dnqSlots.Contains(o.Item1) ? "  DNQ" : ""));
            }
            Board = b;
            Log(title + ":\r\n    " + string.Join("\r\n    ", b.ToArray()));
        }

        // ---- the qualifying session: the AI's laps become ours ----
        readonly Dictionary<int, List<uint>> orig = new Dictionary<int, List<uint>>();
        readonly HashSet<int> written = new HashSet<int>();
        int slowest;

        void StartGrid()
        {
            foreach (int c in runs.Keys.ToList())
                if (Num(c) != runNums[c]) { Log("slot " + c + " is now #" + Num(c) + ", not #" + runNums[c] + ": skipped"); runs.Remove(c); }
            orig.Clear(); written.Clear(); meNoLapLogged = false;
            if (runs.Count == 0) { Log("no qualifying times to write"); phase = Phase.Idle; return; }
            slowest = runs.Values.Max(v => v.Min());
            phase = Phase.Grid;
            Log("qualifying session: every lap the AI post is replaced with their practice laps until the session ends (cars without a run go to the back)");
        }

        bool IsAi(int c) { if (c == Me) return false; uint v = St(c); return v >= 0x760D20 && v <= 0x760FB0; }
        // the qualifying session's cars: the AI, and the player's car when it took part (it needs a lap of its own there)
        IEnumerable<int> QualSlots() { yield return Me; for (int c = 2; c < Slots; c++) yield return c; }

        int? OrigBest(int c)
        {
            List<uint> t;
            if (!orig.TryGetValue(c, out t) || t.Count < 2) return null;
            int best = int.MaxValue;
            for (int i = 1; i < t.Count; i++) best = Math.Min(best, (int)(t[i] - t[i - 1]));
            return best;
        }

        // Stock qualifying pace: the game's own lap for each car (recorded before we rewrite it) over its practice best;
        // the median of those ratios scales every written time, so the times match stock qualifying and the order
        // stays as qualified. Needs 3 cars that have both; until then 1.0.
        double PaceFactor(out int samples)
        {
            var r = runs.Keys.Where(c => OrigBest(c).HasValue && runs[c].Count > 0 && runs[c].Min() > 0)
                .Select(c => (double)OrigBest(c).Value / runs[c].Min()).Where(x => x > 0.8 && x < 1.25).OrderBy(x => x).ToList();
            samples = r.Count;
            if (!MatchStockPace || r.Count < 3) return 1.0;
            double med = r.Count % 2 == 1 ? r[r.Count / 2] : (r[r.Count / 2 - 1] + r[r.Count / 2]) / 2;
            return Math.Max(0.85, Math.Min(1.15, med));
        }
        double paceK = 1.0; int paceN;

        double lastGridTick;
        void GridTick()
        {
            if (Wall - lastGridTick < 0.5) return;
            lastGridTick = Wall;
            paceK = PaceFactor(out paceN);
            status = "Qualifying session: " + written.Count + " AI cars set to their practice times";
            foreach (int c in QualSlots())
            {
                if (!(runs.ContainsKey(c) || IsAi(c) || (c == Me && meInQual && IncludePlayer))) continue;
                var row = new uint[16];
                for (int i = 0; i < 16; i++) row[i] = m.U32(Cross0 + c * CrossCar + i * 4);
                if (c == Me && row[1] == 0 && !meNoLapLogged)
                {
                    meNoLapLogged = true;
                    Log("your car has no lap in this qualifying session yet: drive out and complete a lap (any speed) so your practice time can be written");
                    Call(1, "QUALIFYING SESSION: run one lap (any speed) - your practice time replaces it", 30);
                }
                if (row[0] == 0) continue;
                int n = 0;
                while (n + 1 < row.Length && row[n + 1] != 0) n++;
                List<uint> o;
                if (!orig.TryGetValue(c, out o)) orig[c] = o = new List<uint>();
                while (o.Count <= n) o.Add(row[o.Count] & CrossMask);   // entries not rewritten yet are the game's own
                if (n < 1) continue;
                List<int> laps;
                if (runs.TryGetValue(c, out laps)) laps = laps.Select(x => (int)Math.Round(x * paceK)).ToList();   // at stock pace
                else
                {
                    var ranked = orig.Keys.Where(x => !runs.ContainsKey(x) && OrigBest(x).HasValue).OrderBy(x => OrigBest(x).Value).ToList();
                    int k = ranked.IndexOf(c);
                    laps = new List<int> { (int)Math.Round(slowest * paceK) + 1000 + (k >= 0 ? k : ranked.Count) * 10 };
                }
                uint t = row[0] & CrossMask;
                for (int i = 1; i <= n; i++)
                {
                    t += (uint)(i - 1 < laps.Count ? laps[i - 1] : laps.Max() + 500);
                    uint want = (row[i] & ~CrossMask) | (t & CrossMask);
                    if (want != row[i]) m.W32(Cross0 + c * CrossCar + i * 4, unchecked((int)want));
                }
                // the game's standings only rebuild a car's entry when it crosses the line - after its last lap they
                // never would - so write the entry too: int laps, int best lap number, double best (s), in both copies
                int bestNo = 1, bestMs = int.MaxValue;
                for (int i = 1; i <= n; i++) { int lm = i - 1 < laps.Count ? laps[i - 1] : laps.Max() + 500; if (lm < bestMs) { bestMs = lm; bestNo = i; } }
                long bits = BitConverter.DoubleToInt64Bits(bestMs / 1000.0);
                foreach (long cache in StandingsCaches)
                {
                    long e = cache + c * 16;
                    if (m.I32(e) != n) continue;                                   // not this car's entry for this session
                    if (m.I32(e + 4) != bestNo) m.W32(e + 4, bestNo);
                    m.W32(e + 8, unchecked((int)(bits & 0xFFFFFFFF)));
                    m.W32(e + 12, unchecked((int)(bits >> 32)));
                }
                if (runs.ContainsKey(c)) standingsOk[c] = Math.Abs(m.F64(StandingsCaches[0] + c * 16 + 8) * 1000 - bestMs) < 2;
                if (runs.ContainsKey(c) && written.Add(c)) Log("#" + Num(c) + " set to " + Ms(laps.Min()));
            }
        }
        static readonly long[] StandingsCaches = { 0x797996, 0x8002A0 };
        readonly Dictionary<int, bool> standingsOk = new Dictionary<int, bool>();

        void FinishGrid()
        {
            if (standingsOk.Count > 0)
                Log(string.Format("standings check: {0} of {1} cars showed their practice time in the game's standings{2}", standingsOk.Count(kv => kv.Value), standingsOk.Count,
                    standingsOk.Any(kv => !kv.Value) ? " (not: " + string.Join(" ", standingsOk.Where(kv => !kv.Value).Select(kv => "#" + Num(kv.Key) + " " + Ms(runs[kv.Key].Min()) +
                        " wanted, " + Ms((int)Math.Round(m.F64(StandingsCaches[0] + kv.Key * 16 + 8) * 1000)) + " shown").ToArray()) + ")" : ""));
            standingsOk.Clear();
            var rest = orig.Keys.Where(x => !runs.ContainsKey(x) && OrigBest(x).HasValue).OrderBy(x => OrigBest(x).Value).ToList();
            var grid = runs.Keys.OrderBy(c => runs[c].Min()).Concat(rest).ToList();
            Log("qualifying session over, times written for " + written.Count + " cars. AI order: " + string.Join(" ", grid.Select(c => "#" + Num(c)).ToArray()) + (runs.ContainsKey(Me) || meInQual ? "" : " (your own lap slots in by its time)"));
            // practice pace vs the game's own qualifying pace
            var rows = runs.Keys.Where(c => OrigBest(c).HasValue).Select(c => runs[c].Min() - OrigBest(c).Value).ToList();
            if (rows.Count > 0)
                Log(string.Format("pace check: practice laps were on average {0:+0.000;-0.000} s against the game's own qualifying laps ({1} cars)", rows.Average() / 1000.0, rows.Count));
            if (paceK != 1.0)
                Log(string.Format(CultureInfo.InvariantCulture, "matched the game's qualifying pace: practice times x{0:0.0000} (median of {1} cars; about {2:+0.000;-0.000} s on a {3:0.000} s lap), order unchanged",
                    paceK, paceN, runs.Count > 0 ? runs.Values.Select(v => v.Min()).Average() * (paceK - 1) / 1000.0 : 0, runs.Count > 0 ? runs.Values.Select(v => v.Min()).Average() / 1000.0 : 0));
            else if (MatchStockPace)
                Log("game's qualifying pace not matched: fewer than 3 cars had both a practice time and a game qualifying lap");
            paceK = 1.0;
            // the times are kept until the race loads: a qualifying session that is restarted (or was skipped and run
            // again) gets them written again
            gridWritten = true; gridWaitLogged = false;
            phase = Phase.GridWait;
        }

        // ---- the times on disk: a Race Control restarted in the qualifying session can still write them ----
        public static string RunsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NR2003RaceControl", "qualifying_runs.txt");
        const double RunsMaxHours = 6;
        void SaveRuns()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RunsPath));
                var b = new StringBuilder();
                b.Append("track " + m.Str(Addr.TrackName, 64).Trim() + "\n");
                b.Append("saved " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n");
                foreach (var kv in runs) b.Append(runNums[kv.Key] + " " + string.Join(",", kv.Value.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + "\n");
                if (dnqNums.Count > 0) b.Append("dnq " + string.Join(" ", dnqNums.ToArray()) + "\n");   // (no car number is "dnq")
                File.WriteAllText(RunsPath, b.ToString());
            }
            catch (Exception e) { Log("couldn't save the qualifying times: " + e.Message); }
        }
        // the saved times, if they are for this track and recent; car numbers are matched to this session's AI cars
        bool LoadRuns(bool dnqOnly = false)
        {
            try
            {
                if (!File.Exists(RunsPath)) return false;
                string track = "", here = m.Str(Addr.TrackName, 64).Trim(); DateTime saved = DateTime.MinValue;
                var byNum = new Dictionary<string, List<int>>();
                var dnqFile = new List<string>();
                foreach (string raw in File.ReadAllLines(RunsPath))
                {
                    string l = raw.Trim(); int sp = l.IndexOf(' ');
                    if (sp < 0) continue;
                    string k = l.Substring(0, sp), v = l.Substring(sp + 1).Trim();
                    if (k == "track") track = v;
                    else if (k == "saved") DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out saved);
                    else if (k == "dnq") dnqFile = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    else
                    {
                        var ms = v.Split(',').Select(x => { int n; return int.TryParse(x, out n) ? n : 0; }).Where(n => n > 5000).ToList();
                        if (ms.Count > 0) byNum[k] = ms;
                    }
                }
                double age = (DateTime.UtcNow - saved.ToUniversalTime()).TotalHours;
                if (!string.Equals(track, here, StringComparison.OrdinalIgnoreCase) || age < 0 || age > RunsMaxHours) return false;
                if (dnqOnly)
                {
                    if (dnqFile.Count == 0) return false;
                    dnqNums.Clear(); dnqNums.AddRange(dnqFile);
                    Log(string.Format(CultureInfo.InvariantCulture, "the DNQs from the qualifying run saved {0:0.0} h ago at {1}: {2}", age, track, string.Join(" ", dnqFile.Select(n => "#" + n).ToArray())));
                    return true;
                }
                if (byNum.Count == 0) return false;
                dnqNums.Clear(); dnqNums.AddRange(dnqFile);
                runs.Clear(); runNums.Clear();
                foreach (int c in QualSlots())
                {
                    if (c == Me ? !PlayerReady() : !IsAi(c)) continue;
                    List<int> ms;
                    if (byNum.TryGetValue(Num(c), out ms)) { runs[c] = ms; runNums[c] = Num(c); }
                }
                if (runs.Count == 0) return false;
                Log(string.Format(CultureInfo.InvariantCulture, "qualifying session: {0} cars' times loaded from the qualifying run saved {1:0.0} h ago at {2}", runs.Count, age, track));
                return true;
            }
            catch (Exception e) { Log("couldn't load the saved qualifying times: " + e.Message); return false; }
        }

        // ---- status file for broadcast tools (RaceVision's knockout camera mode) ----
        public static string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NR2003 Companions");
        double lastState;

        void WriteSingleState(string ph, bool final = false)
        {
            if (!SingleRuns || single == null || Wall - lastState < 0.1) return;
            lastState = Wall;
            var posted = results.Where(r => r.Item1 < 9e9)
                .OrderBy(r => r.Item1)
                .Select(r => Tuple.Create(r.Item2, (int?)Math.Round(r.Item1 * 1000))).ToList();
            var order = single.Order ?? new List<int>();
            var onTrack = final ? new List<int>() : single.Active.Where(r => !r.CalledIn).Select(r => r.Car).ToList();
            var txt = new StringBuilder();
            txt.Append("mode single\nphase " + ph + "\nround 1\nrounds 1\ntime_left 0\ncutoff 0\non_track " +
                string.Join(" ", onTrack) + "\norder " + string.Join(" ", order) + "\nboard " +
                string.Join(" ", posted.Select(b => b.Item1 + ":" + b.Item2.Value)) + "\n");
            // the car the timing box is on (result / live / waiting): broadcast tools put the camera on the same car
            string kind = BoxKind.ToString().ToLowerInvariant();
            txt.Append("focus " + BoxCar + "\nfocus_kind " + kind + "\n");
            // the player is driving their run: broadcast tools put the camera in that car's cockpit until it is back on pit road
            txt.Append("drive " + DriveCar + "\n");
            // two-lane re-qualifying (Indy oval) and the cars that didn't make the field
            if (Lanes) txt.Append("lanes " + (reqOpen ? "open" : reqDone ? "closed" : "later") + "\nlane1 " + string.Join(" ", lane1Q) + "\nlane2 " + string.Join(" ", lane2Q) +
                "\nrequal_left " + Math.Round(ReQualLeft) + "\n");
            txt.Append("dnq " + string.Join(" ", dnqSlots.OrderBy(x => x)) + "\n");
            var json = new StringBuilder();
            json.Append("{\"mode\":\"single\",\"phase\":\"" + ph + "\",\"focus\":" + BoxCar + ",\"focus_kind\":\"" + kind + "\",\"drive\":" + DriveCar +
                (Lanes ? ",\"lanes\":\"" + (reqOpen ? "open" : reqDone ? "closed" : "later") + "\",\"lane1\":[" + string.Join(",", lane1Q) + "],\"lane2\":[" + string.Join(",", lane2Q) +
                    "],\"requal_left\":" + Math.Round(ReQualLeft) : "") + ",\"dnq\":[" + string.Join(",", dnqSlots.OrderBy(x => x)) + "],\"order\":[" +
                string.Join(",", order) + "],\"board\":[" + string.Join(",", posted.Select(b =>
                    "{\"slot\":" + b.Item1 + ",\"best_ms\":" + b.Item2.Value + "}")) + "]}");
            try
            {
                Directory.CreateDirectory(StateDir);
                foreach (var f in new[] { Tuple.Create("qualifying_state.txt", txt.ToString()), Tuple.Create("qualifying_state.json", json.ToString()) })
                {
                    string path = Path.Combine(StateDir, f.Item1), tmp = path + ".tmp";
                    File.WriteAllText(tmp, f.Item2);
                    // swapped in one step: a reader never finds the file missing (delete + move left a gap)
                    if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                }
            }
            catch (Exception) { }
        }

        void WriteState(string ph, int round, double left, List<Tuple<int, int?>> board, int cutoff, int nextRound = 0)
        {
            if (!KnockoutLike || Wall - lastState < 0.1) return;
            lastState = Wall;
            board = board ?? new List<Tuple<int, int?>>();
            var onTrack = outNow.Where(c => { uint s = St(c); return OnTrack.Contains(s) || PitIn.Contains(s) || Leaving.Contains(s); }).OrderBy(c => c).ToList();
            var txt = new StringBuilder();
            txt.Append("mode knockout\nphase " + ph + "\nround " + round + "\nrounds " + (koRounds != null ? koRounds.Count : round) + "\ntime_left " + Math.Round(left) +
                "\ncutoff " + cutoff + "\non_track " + string.Join(" ", onTrack.Select(c => c.ToString()).ToArray()) +
                "\nboard " + string.Join(" ", board.Where(b => b.Item2.HasValue).Select(b => b.Item1 + ":" + b.Item2.Value).ToArray()) + "\ndrive " + DriveCar +
                "\ndnq " + string.Join(" ", dnqSlots.OrderBy(x => x)) + "\n");
            var json = new StringBuilder();
            json.Append("{\"mode\":\"knockout\",\"phase\":\"" + ph + "\",\"round\":" + round + ",\"time_left\":" + Math.Round(left, 1).ToString(CultureInfo.InvariantCulture) +
                ",\"cutoff\":" + cutoff + ",\"drive\":" + DriveCar + ",\"dnq\":[" + string.Join(",", dnqSlots.OrderBy(x => x)) + "]" + (nextRound > 0 ? ",\"next_round\":" + nextRound : "") + ",\"on_track\":[" + string.Join(",", onTrack.Select(c => c.ToString()).ToArray()) + "],\"board\":[" +
                string.Join(",", board.Select(b => "{\"slot\":" + b.Item1 + ",\"number\":\"" + Num(b.Item1) + "\",\"best_ms\":" + (b.Item2.HasValue ? b.Item2.Value.ToString() : "null") + "}").ToArray()) + "]}");
            try
            {
                Directory.CreateDirectory(StateDir);
                foreach (var f in new[] { Tuple.Create("qualifying_state.txt", txt.ToString()), Tuple.Create("qualifying_state.json", json.ToString()) })
                {
                    string path = Path.Combine(StateDir, f.Item1), tmp = path + ".tmp";
                    File.WriteAllText(tmp, f.Item2);
                    // swapped in one step: a reader never finds the file missing (delete + move left a gap)
                    if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                }
            }
            catch (Exception) { }
        }
    }
}
