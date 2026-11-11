using System;
using System.Collections.Generic;
using System.Linq;

namespace RaceControl
{
    // The line behind the pace car: lapped cars to the back (as soon as the pit stops are done), double file or
    // choose cone at flag 6, and untangling cars that cannot get past each other.
    public partial class RaceEngine
    {
        public bool LappedToBack = true;    // lead lap cars (waved cars among them), then lapped cars
        public bool EarlyLineup = true;     // do that as soon as the pit stops are done, not at one to go
        public bool DoubleFile = true;      // double file at flag 6 from the order on track (and again after a delayed green)
        public bool Cone = true;            // choose cone: each car picks a lane instead of strictly alternating
        public int PreferredLane = 1;       // 0 none (alternate), 1 inside, 2 outside
        public int GiveUpRows = 2;          // rows a driver gives up to get the preferred lane
        public int FrontRows = 3;           // in these rows a driver takes the best spot, whatever the lane
        public bool Untangle = true;        // swap two cars that cannot get past each other

        // our plan for the line: single file (lineup) or two lanes, written to the game and held for a few seconds
        List<int> lineup;                   // single file order, kept for the whole caution
        List<int> inside, outside;          // double file lanes (null while single file)
        bool planActive;
        Dictionary<int, int> planKey = new Dictionary<int, int>(); // car -> place in the plan (lanes: inside 2k, outside 2k+1)
        double planTime, lastPlanUpkeep;
        bool planRestoredLogged;

        bool lineupDone, earlyLineupDone, doubleFileDone, delayedGreenHandled;
        double noPitSince = double.NaN;

        // untangling: (car ahead, car behind) -> since when the car ahead has been stuck ahead of the car it should follow
        Dictionary<long, double> stuckSince = new Dictionary<long, double>();
        double lastUntangle;

        // restart diagnostics
        int prevFlag = -1;
        double flagSince;
        bool lineCheck5Done, lineCheck6Done;
        string lastLineCheck;
        double lastLineCheckTime;

        bool LineupHappened { get { return lineupDone || earlyLineupDone || doubleFileDone; } }

        // ---- writing the line -----------------------------------------------------------------------

        void WriteLine(List<int> order)
        {
            m.W8(Addr.NumPacingLines, 1);
            for (int i = 0; i < 48; i++)
            {
                int idx = i < order.Count ? order[i] : -1;
                m.W32(Addr.PacingOutside1st + i * 8, idx);
                if (idx < 0) continue;
                m.W32(Addr.PacingLineIdx + idx * 13, 0);
                m.W32(Addr.PacingRow + idx * 13, i + 1);
            }
        }

        // Same layout Better Cautions writes for double file: inside (line 1) car k at row k, outside (line 0) car k
        // at row k + 1, side by side. The lanes may have different lengths (choose cone).
        void WriteLanes(List<int> ins, List<int> outs)
        {
            m.W8(Addr.NumPacingLines, 2);
            for (int k = 0; k < 48; k++)
            {
                int a = k < ins.Count ? ins[k] : -1, b = k < outs.Count ? outs[k] : -1;
                m.W32(Addr.PacingInside1st + k * 8, a);
                m.W32(Addr.PacingOutside1st + k * 8, b);
                if (a >= 0) { m.W32(Addr.PacingLineIdx + a * 13, 1); m.W32(Addr.PacingRow + a * 13, k); }
                if (b >= 0) { m.W32(Addr.PacingLineIdx + b * 13, 0); m.W32(Addr.PacingRow + b * 13, k + 1); }
            }
        }

        void WritePlan()
        {
            planKey.Clear();
            if (inside != null)
            {
                WriteLanes(inside, outside);
                for (int k = 0; k < inside.Count; k++) planKey[inside[k]] = 2 * k;
                for (int k = 0; k < outside.Count; k++) planKey[outside[k]] = 2 * k + 1;
            }
            else
            {
                WriteLine(lineup);
                for (int i = 0; i < lineup.Count; i++) planKey[lineup[i]] = 2 * i;
            }
            planActive = true;
            planTime = Now;
            planRestoredLogged = false;
        }

        bool PlanIntact()
        {
            if (inside != null)
            {
                if (m.U8(Addr.NumPacingLines) != 2) return false;
                for (int k = 0; k < inside.Count; k++) if (m.I32(Addr.PacingInside1st + k * 8) != inside[k]) return false;
                for (int k = 0; k < outside.Count; k++) if (m.I32(Addr.PacingOutside1st + k * 8) != outside[k]) return false;
                return true;
            }
            if (m.U8(Addr.NumPacingLines) != 1) return false;
            for (int i = 0; i < lineup.Count; i++)
                if (m.I32(Addr.PacingOutside1st + i * 8) != lineup[i] || m.I32(Addr.PacingRow + lineup[i] * 13) != i + 1) return false;
            return true;
        }

        // NR2003 rewrites the pacing order itself (cars crossing the line, a delayed green), and so would Better
        // Cautions if it were still running. Our plan is held for as long as it is active: once a car has its spot
        // (and in double file its lane) it keeps it. Cars that join (out of the pits) go to the end, cars that
        // retire are taken out.
        void HoldPlan()
        {
            if (!planActive) return;
            if (Now - lastPlanUpkeep >= 0.5)
            {
                lastPlanUpkeep = Now;
                if (KeepPlanInStep()) { WritePlan(); return; }
            }
            if (PlanIntact()) return;
            if (inside != null) WriteLanes(inside, outside); else WriteLine(lineup);
            if (!planRestoredLogged) Log("the game re-ordered the line; ours put back (cars keep their spot and lane)");
            planRestoredLogged = true;
        }

        // Returns true when the plan changed.
        bool KeepPlanInStep()
        {
            List<Car> cars = Field();
            if (cars == null) return false;
            bool changed = false;
            foreach (List<int> l in new[] { lineup, inside, outside, coneInside, coneOutside })
                if (l != null) changed |= l.RemoveAll(idx => !Running(idx)) > 0;
            foreach (Car c in cars.OrderBy(c => c.Gap))
            {
                if (planKey.ContainsKey(c.Idx)) continue;
                if (inside != null && Classic && (eligible.ContainsKey(c.Idx) || HasPenalty(c.Idx))) (outside.Count >= inside.Count ? outside : inside).Add(c.Idx);
                else if (inside != null && Classic) (DownOf(c) >= 1 ? inside : outside).Add(c.Idx); // 2004: lapped inside
                else if (inside != null) (inside.Count <= outside.Count ? inside : outside).Add(c.Idx);
                if (lineup != null && !lineup.Contains(c.Idx))
                {
                    // single file: a lead lap car goes to the end of the lead lap cars, a lapped car to the very end
                    var lapped = new HashSet<int>(cars.Where(o => o.Down >= 1 && !eligible.ContainsKey(o.Idx)).Select(o => o.Idx));
                    int at = !WavedToTail(c.Idx) && (c.Down < 1 || eligible.ContainsKey(c.Idx)) ? lineup.FindIndex(lapped.Contains) : -1;
                    lineup.Insert(at < 0 ? lineup.Count : at, c.Idx);
                }
                Log("#" + c.Num + " joined the line" + (inside != null ? " at the end of the " + (inside.Contains(c.Idx) ? "inside" : "outside") + " lane" : ""));
                changed = true;
            }
            return changed;
        }

        // ---- lapped cars to the back ----------------------------------------------------------------

        // Lead lap cars (the waved cars are among them once around), then the lapped cars. Cars moved back from
        // ahead of the leader are excused from the pace-car penalty. The first time it orders the field by the
        // scoring; after that (one to go after an early lineup) cars keep the order they were given and only cars
        // that were not in it (out of the pits) or changed group are placed again.
        void LineUp(string when)
        {
            Car leader, first;
            List<Car> cars = Field(out leader, out first);
            if (cars == null)
            {
                Log(when + ": track length or pace car position unavailable, line left as it is");
                return;
            }
            if (leaderIdx < 0) leaderIdx = leader.Idx;
            var front = new List<Car>();
            var back = new List<Car>();
            var tail = new List<Car>();
            var queued = new List<Car>();       // waiting for one to go in front of the leader: first, in their order on track
            string stillAround = "";
            foreach (Car c in cars)
            {
                if (WaitingForOneToGo(c.Idx) && c.Ahead) queued.Add(c);
                else if (WavedToTail(c.Idx))
                {
                    // waved around: restarts at the tail of the field, where it comes up from behind
                    if (c.Ahead) stillAround += " #" + c.Num;
                    tail.Add(c);
                }
                else if (eligible.ContainsKey(c.Idx))
                {
                    // waved: lines up by its position on the scoring like any other car
                    if (c.Ahead) stillAround += " #" + c.Num;
                    if (c.Down < 1 || c.Ahead) front.Add(c); else back.Add(c);
                }
                else if (c.Ahead && c.Down >= 1)
                {
                    excused.Add(c.Idx);
                    back.Add(c);
                    if (!settle.ContainsKey(c.Idx))
                        settle[c.Idx] = new Settle { Down = Math.Ceiling(c.Down - 0.001), Waved = false, Before = c.Down };
                }
                else if (c.Down < 1)
                {
                    if (c.Ahead) excused.Add(c.Idx); // on the lead lap in front of the leader: moved behind it
                    front.Add(c);
                }
                else back.Add(c);
            }
            bool firstTime = lineup == null;
            // 3.3.10: a car in the wreck or on pit road at the yellow has given up its spot: behind the rest of its group
            var forfeit = new List<int>();
            Func<List<int>, List<int>> last = g =>
            {
                var f = g.Where(Forfeited).ToList();
                forfeit.AddRange(f);
                return g.Where(i => !Forfeited(i)).Concat(f).ToList();
            };
            List<int> order = queued.OrderBy(c => c.Gap).Select(c => c.Idx).Concat(last(Merge(front, lineup))).Concat(last(Merge(back, lineup))).Concat(Merge(tail, lineup)).ToList();
            if (forfeit.Count > 0 && firstTime)
                Log(when + ": in the wreck or on pit road at the yellow, behind the other cars of their group:" + string.Concat(forfeit.Select(i => " #" + CarNumber(i))));
            int moved = order.Where((idx, i) => m.I32(Addr.PacingRow + idx * 13) != i + 1).Count();
            lineup = order;
            inside = outside = null;
            WritePlan();
            Log(when + ": leader #" + leader.Num + ", " + front.Count + " lead lap, " + back.Count + " lapped, " + tail.Count + " waved at the tail, " + (queued.Count > 0 ? queued.Count + " in front waiting to go around at one to go, " : "") + moved + " cars given a new spot; order" + string.Concat(order.Select(i => " #" + CarNumber(i))));
            if (stillAround != "") Log("waved cars not yet around at " + when + ":" + stillAround);
            if (WaveAroundOn && firstTime) Log(FirstTimeBySummary());
        }

        // The group in the order it already had in the plan; cars new to it are put in by their progress.
        static List<int> Merge(List<Car> group, List<int> old)
        {
            var kept = new List<Car>();
            if (old != null) kept = group.Where(c => old.Contains(c.Idx)).OrderBy(c => old.IndexOf(c.Idx)).ToList();
            foreach (Car c in group.Where(c => !kept.Contains(c)).OrderByDescending(c => c.Prog))
            {
                int at = kept.FindIndex(k => k.Prog < c.Prog);
                kept.Insert(at < 0 ? kept.Count : at, c);
            }
            return kept.Select(c => c.Idx).ToList();
        }

        // Once every car has had its first time by (or two laps after pit road opened) and no running car has been
        // on pit road for 3 s the lineup is made, leaving the rest of the caution to sort the field out. Waiting
        // for the two laps alone, a caution whose pits opened late reached one to go first and all the cars were
        // moved at once there.
        void EarlyLineUpTick()
        {
            if (earlyLineupDone || flag < 2 || pitOpenLap < 0) return;
            bool allDecided = Enumerable.Range(0, maxIdx + 1).All(i => i == Addr.PaceCarIdx || !Running(i) || decided.ContainsKey(i));
            if (!allDecided && lap < pitOpenLap + 2) return;
            bool anyPitting = false;
            for (int idx = 0; idx <= maxIdx && !anyPitting; idx++)
                anyPitting = idx != Addr.PaceCarIdx && OnPitRoad(idx) && Running(idx);
            if (anyPitting) { noPitSince = double.NaN; return; }
            if (double.IsNaN(noPitSince)) noPitSince = Now;
            if (Now - noPitSince < 3) return;
            earlyLineupDone = true;
            LineUp("lineup after the pit stops");
        }

        // ---- classic double file (2004) ---------------------------------------------------------------

        // At one to go: lead lap cars on the outside in running order, lapped cars on the inside in running order,
        // side by side from the front. When one group runs out the rest of the other alternate lanes. The Lucky Dog
        // goes to the tail of the longer line. Cars out of the pits later join their group's lane (KeepPlanInStep).
        void ClassicDoubleFile()
        {
            doubleFileDone = true;
            Car leader, first;
            List<Car> cars = Field(out leader, out first);
            if (cars == null)
            {
                Log("double file (2004 rules): track length or pace car position unavailable, the game's line left as it is");
                return;
            }
            if (leaderIdx < 0) leaderIdx = leader.Idx;
            // the user's rule (2026-10-28): lead lap cars single file on the outside, lapped cars single file on the
            // inside, each in running order, never alternating; the Lucky Dog and penalized cars at the tail of the
            // longer line. Lap status is the one at the yellow (DownOf), not the scoring now.
            List<Car> byRace = cars.OrderByDescending(c => c.Prog).ToList();
            Func<Car, bool> tail = c => eligible.ContainsKey(c.Idx) || HasPenalty(c.Idx);
            List<int> lead = byRace.Where(c => !tail(c) && DownOf(c) < 1).Select(c => c.Idx).ToList();
            List<int> lapped = byRace.Where(c => !tail(c) && DownOf(c) >= 1).Select(c => c.Idx).ToList();
            List<int> back = byRace.Where(tail).Select(c => c.Idx).ToList();
            var outs = new List<int>(lead);
            var ins = new List<int>(lapped);
            foreach (int idx in back) (outs.Count >= ins.Count ? outs : ins).Add(idx);
            Func<List<int>, string> nums = l => string.Concat(l.Select(i => " #" + CarNumber(i)));
            excused.UnionWith(ins.Concat(outs));    // moved by the officials: no passing-under-yellow penalty
            string backText = back.Count > 0 ? ", at the tail" + nums(back) : "";
            if (ins.Count == 0)
            {
                // no lapped cars: single file
                inside = outside = null;
                lineup = outs;
                WritePlan();
                Log(string.Format("one to go (classic rules): no lapped cars, single file{0}: order{1}", backText, nums(outs)));
                return;
            }
            lineup = null;
            inside = ins;
            outside = outs;
            WritePlan();
            Log(string.Format("double file (2004 rules): {0} lead lap outside, {1} lapped inside{2}: outside{3}; inside{4}",
                lead.Count, lapped.Count, backText, nums(outs), nums(ins)));
        }

        // ---- double file / choose cone --------------------------------------------------------------

        // The cone is a point on the track. Cars stay single file until they reach it and choose a lane there, one
        // after another in the order they arrive, each knowing what every car ahead of it chose. A car that has
        // not reached the cone yet follows the cars ahead in the "queue lane" (written behind the cars that chose
        // it) so it does not start moving across early. The player, when driving, chooses on a chart instead: the
        // game is paused just before the cone (see PlayerLaneNeeded).
        public double ConePoint = double.NaN;   // metres from the line; NaN = where the pace car is when double file starts
        public bool PlayerChoosesLane = true;   // the player picks a lane at the cone
        public Action<ConeOffer> PlayerLaneNeeded; // ask the window to show the chart (and pause the game)

        List<int> coneInside, coneOutside;      // cars that have chosen, in the order they chose
        bool coneRunning;
        double coneAt, lastConeTick;
        HashSet<int> pastCone = new HashSet<int>();          // cars that have reached the cone
        Dictionary<int, double> toCone = new Dictionary<int, double>(); // car -> metres to the cone, last tick
        // A cone on the one to go lap lies between the line and where the field goes double file. One to go comes
        // out before the line, with most of the field between the cone point and the pace car on the lap before:
        // those cars reach the cone only after they cross the line. coneNeedsLine: a car counts as at the cone only
        // once it is on the one to go lap (onConeLap).
        bool coneNeedsLine, coneLapChecked;
        HashSet<int> onConeLap = new HashSet<int>();
        Dictionary<int, double> lastConeDlong = new Dictionary<int, double>();
        int queueLane;                          // 1 inside, 2 outside: where cars that have not chosen yet wait
        ConeOffer playerOffer;
        int playerChoice;                       // 1 inside, 2 outside, set by ChoosePlayerLane
        double playerOfferTime;

        public bool PlayerOfferOpen { get { return playerOffer != null; } }

        // The lanes as the cars choose them, for the choose cone board: a new board each double file.
        public ConeBoardData ConeBoard { get; private set; }
        Dictionary<int, int> coneFrom = new Dictionary<int, int>();   // car -> spot in the single file line at double file

        ConeEntry Entry(int idx, int lane, int row)
        {
            int from;
            if (!coneFrom.TryGetValue(idx, out from)) from = 0;
            int to = lane == 1 ? 2 * row - 1 : 2 * row;  // inside odd, outside even
            return new ConeEntry { Idx = idx, Num = CarNumber(idx), From = from, To = to, Lane = lane, Row = row, Player = idx == Addr.PlayerIdx,
                Gained = from == 1 || from == 0 ? 0 : from - to };   // the leader keeps the lead
        }

        void BoardAdd(int idx, bool toInside, int row)
        {
            if (ConeBoard == null) return;
            (toInside ? ConeBoard.Inside : ConeBoard.Outside).Add(Entry(idx, toInside ? 1 : 2, row));
            ConeBoard.Version++;
        }

        // From the window: the player's lane (1 inside, 2 outside).
        public void ChoosePlayerLane(int lane) { if (playerOffer != null && (lane == 1 || lane == 2)) playerChoice = lane; }

        bool PlayerDriving { get { return Running(Addr.PlayerIdx) && CarNumber(Addr.PlayerIdx) > 0; } }

        // The cone rule: up front a driver takes the better spot (ties: the preferred lane); further back the
        // preferred lane unless it costs more than GiveUpRows rows. No cone (or no preferred lane): alternate.
        bool ChoosesInside(List<int> ins, List<int> outs)
        {
            if (!Cone || PreferredLane == 0) return ins.Count <= outs.Count;
            List<int> pref = PreferredLane == 1 ? ins : outs, other = PreferredLane == 1 ? outs : ins;
            bool takePref;
            if (Math.Min(pref.Count, other.Count) < FrontRows) takePref = pref.Count <= other.Count;
            else takePref = pref.Count - other.Count <= GiveUpRows;
            return takePref == (PreferredLane == 1);
        }

        // Where the pace car is when the game calls double file (learned at each restart, saved per track). A cone
        // before that point is passed on the one to go lap, so the cars choose there, soon after one to go.
        public double DoubleFileAt = double.NaN;
        public Action<double> DoubleFileAtLearned;
        int coneFlagSeen = -1;
        bool coneWaiting;                       // one to go cone held until the lapped cars are behind the lead lap cars
        double coneWaitSince;
        string coneWaitCars = "";

        // Lapped cars still in front of a lead lap car on track (cars sent around on the lead lap count as lead lap
        // cars; waved cars restart behind the lapped cars).
        List<Car> LappedInTheWay(List<Car> cars)
        {
            return cars.Where(c => Lapped(c) && cars.Any(o => !Lapped(o) && !WavedToTail(o.Idx) && o.Gap > c.Gap)).ToList();
        }

        // The cone on the one to go lap opens once no lapped car is in front of a lead lap car: otherwise the cars
        // choosing would have to drive around a car that is still dropping back. Cars that have passed the cone by
        // then choose at once, in lineup order. At double file it opens whatever happens.
        void OneToGoConeTick()
        {
            List<Car> cars = Field();
            List<Car> inWay = cars == null ? new List<Car>() : LappedInTheWay(cars);
            if (flag == 5 && inWay.Count > 0)
            {
                string nums = string.Concat(inWay.Select(c => " #" + c.Num));
                if (!coneWaiting) { coneWaiting = true; coneWaitSince = Now; Log("cone waits for lapped car(s)" + nums + " to drop behind the lead lap cars"); }
                coneWaitCars = nums;
                return;
            }
            doubleFileDone = true;
            if (coneWaiting)
                Log(inWay.Count == 0 ? string.Format("cone opens: the lapped cars are behind the lead lap cars ({0:0} s)", Now - coneWaitSince)
                                     : "cone opens at double file with lapped car(s) still in the way:" + coneWaitCars);
            coneWaiting = false;
            StartCone(true);
        }

        bool ConeOnOneToGoLap()
        {
            double trackLen = m.F64(Addr.TrackLength);
            if (double.IsNaN(ConePoint) || trackLen <= 100 || ConePoint < 0 || ConePoint >= trackLen) return false;
            double at = !double.IsNaN(DoubleFileAt) && DoubleFileAt > 0 && DoubleFileAt < trackLen ? DoubleFileAt : trackLen * 0.5;
            return ConePoint < at - 50;
        }

        void LearnDoubleFileAt()
        {
            if (flag == 6 && coneFlagSeen == 5)
            {
                double pc = DlongOf(Addr.PaceCarIdx), trackLen = m.F64(Addr.TrackLength);
                if (trackLen > 100 && pc > 0 && pc < trackLen && (double.IsNaN(DoubleFileAt) || Math.Abs(pc - DoubleFileAt) > 25))
                {
                    DoubleFileAt = pc;
                    Log(string.Format("the field goes double file with the pace car at {0:0} m at this track", pc));
                    if (DoubleFileAtLearned != null) DoubleFileAtLearned(pc);
                }
            }
            coneFlagSeen = flag;
        }

        // The single file order the cars are in for the cone: our lineup when there is one, else the order on track.
        List<Car> LineOrder(List<Car> cars)
        {
            List<Car> byGap = cars.OrderBy(c => c.Gap).ToList();
            if (lineup == null) return byGap;
            var at = new Dictionary<int, int>();
            for (int i = 0; i < lineup.Count; i++) at[lineup[i]] = i;
            return byGap.Select((c, i) => new { c, k = at.ContainsKey(c.Idx) ? at[c.Idx] : 1000 + i }).OrderBy(x => x.k).Select(x => x.c).ToList();
        }

        void StartCone(bool oneToGo)
        {
            double trackLen = m.F64(Addr.TrackLength);
            coneInside = new List<int>();
            coneOutside = new List<int>();
            coneRunning = true;
            playerOffer = null;
            playerChoice = 0;
            pastCone.Clear();
            toCone.Clear();
            coneFrom.Clear();
            onConeLap.Clear();
            lastConeDlong.Clear();
            coneNeedsLine = oneToGo && !double.IsNaN(ConePoint);
            queueLane = PreferredLane == 2 ? 2 : 1;
            List<Car> cars = trackLen > 100 ? Field() : null;
            double pc = DlongOf(Addr.PaceCarIdx);
            string where = "";
            coneAt = !double.IsNaN(ConePoint) && ConePoint >= 0 && ConePoint < trackLen ? ConePoint : pc;
            if (double.IsNaN(ConePoint)) where = " (where the pace car is now)";
            else if (oneToGo) where = " on the one to go lap (it comes before where the field goes double file)";
            else if (cars != null && cars.Count > 0)
            {
                // The cone has to come before the restart. Cars between the cone and the pace car have passed it
                // already (they choose at once); otherwise it must be ahead of the leader before the line. A cone
                // just past the line (or one the whole field has passed) is reached only after the green: the cars
                // then choose where the pace car is now, one after the other.
                double lead = DlongOf(cars.OrderBy(c => c.Gap).First().Idx);
                double last = cars.Max(c => c.Gap);
                bool straddled = Mod(pc - coneAt, trackLen) <= last;
                bool ahead = Mod(coneAt - lead, trackLen) < Mod(-lead, trackLen) - 30;
                if (!straddled && !ahead)
                {
                    where = string.Format(" (the cone at {0:0} m would come after the green: the pace car is at {1:0} m at double file, the restart line at 0 m)", coneAt, pc);
                    coneAt = pc;
                }
            }
            if (cars != null && coneNeedsLine) ConeLapFromPaceCar(cars, pc, trackLen);
            else if (cars != null)
                foreach (Car c in cars)
                {
                    double d = DlongOf(c.Idx);
                    if (Mod(d - coneAt, trackLen) <= Mod(pc - coneAt, trackLen) && Mod(pc - coneAt, trackLen) < trackLen / 2) pastCone.Add(c.Idx);
                }
            if (where == "" && !double.IsNaN(ConePoint)) where = string.Format(" (pace car at {0:0} m)", pc);
            Log(string.Format("double file: cars choose their lane at the cone, {0:0} m from the line{1}", coneAt, where));
            if (cars != null)
            {
                int k = 0;
                foreach (Car c in LineOrder(cars)) coneFrom[c.Idx] = ++k;
            }
            ConeBoard = new ConeBoardData { Title = Cone && PreferredLane != 0 ? "CHOOSE CONE" : "DOUBLE FILE", Cars = coneFrom.Count };
            lastConeTick = -1;
            coneLapChecked = false;
            ConeTick();
        }

        // Once the pace car has crossed the line onto the one to go lap (it is short of where the field goes double
        // file), the cars between the line and the pace car are on that lap too, and those past the cone point have
        // passed the cone. Also run at double file, in case a car's crossing of the line was missed.
        void ConeLapFromPaceCar(List<Car> cars, double pc, double trackLen)
        {
            double at = !double.IsNaN(DoubleFileAt) && DoubleFileAt > 0 && DoubleFileAt < trackLen ? DoubleFileAt : trackLen * 0.5;
            if (flag != 6 && pc >= at + 50) return; // still on the lap before
            foreach (Car c in cars)
            {
                double d = DlongOf(c.Idx);
                if (d > pc) continue;
                onConeLap.Add(c.Idx);
                if (d >= coneAt && pc >= coneAt) pastCone.Add(c.Idx);
            }
        }

        void ConeTick()
        {
            if (!coneRunning || (Now - lastConeTick < 0.1 && playerChoice == 0)) return;
            lastConeTick = Now;
            double trackLen = m.F64(Addr.TrackLength);
            List<Car> cars = Field();
            if (cars == null || trackLen <= 100) return;
            List<Car> order = LineOrder(cars);
            // each car reaches the cone when it drives past it (not measured from the pace car, which pulls off)
            if (coneNeedsLine && flag == 6 && !coneLapChecked) { coneLapChecked = true; ConeLapFromPaceCar(cars, DlongOf(Addr.PaceCarIdx), trackLen); }
            foreach (Car c in cars)
            {
                double dl = DlongOf(c.Idx), last;
                if (coneNeedsLine && lastConeDlong.TryGetValue(c.Idx, out last) && last > trackLen * 0.75 && dl < trackLen * 0.25) onConeLap.Add(c.Idx);
                lastConeDlong[c.Idx] = dl;
                double d = Mod(coneAt - dl, trackLen), prev;
                if (toCone.TryGetValue(c.Idx, out prev) && prev < trackLen * 0.25 && d > trackLen * 0.75 && (!coneNeedsLine || onConeLap.Contains(c.Idx)))
                    pastCone.Add(c.Idx);
                toCone[c.Idx] = d;
            }
            var chosen = new HashSet<int>(coneInside.Concat(coneOutside));
            bool changed = false;
            foreach (Car c in order)
            {
                if (chosen.Contains(c.Idx)) continue;
                bool player = c.Idx == Addr.PlayerIdx && PlayerChoosesLane && PlayerDriving;
                if (player && !PlayerTurn(c)) break;
                if (!player && !pastCone.Contains(c.Idx)) break; // not at the cone yet; nobody behind it is either
                bool toInside;
                string how = "";
                if (player)
                {
                    toInside = playerChoice == 1;
                    how = " (player's choice)";
                    if (playerChoice == 0)
                    {
                        toInside = ChoosesInside(coneInside, coneOutside);
                        how = " (player did not choose in time: the cone rule chose)";
                    }
                    playerOffer = null;
                    playerChoice = 0;
                }
                else toInside = ChoosesInside(coneInside, coneOutside);
                List<int> lane = toInside ? coneInside : coneOutside;
                lane.Add(c.Idx);
                chosen.Add(c.Idx);
                BoardAdd(c.Idx, toInside, lane.Count);
                changed = true;
                if (player)
                    Log(string.Format("cone: #{0} chose the {1} lane, row {2}{3}", c.Num, toInside ? "inside" : "outside", lane.Count, how));
            }
            // the queue waits in the lane with more cars, so its spots are behind everyone who has chosen; it only
            // switches when the other lane gets longer
            int qCount = queueLane == 1 ? coneInside.Count : coneOutside.Count, oCount = queueLane == 1 ? coneOutside.Count : coneInside.Count;
            if (oCount > qCount) { queueLane = 3 - queueLane; changed = true; }
            List<int> queue = order.Where(c => !chosen.Contains(c.Idx)).Select(c => c.Idx).ToList();
            if (!changed && inside != null) return;
            inside = new List<int>(coneInside);
            outside = new List<int>(coneOutside);
            (queueLane == 1 ? inside : outside).AddRange(queue);
            WritePlan();
            if (queue.Count > 0) return;
            coneRunning = false;
            if (ConeBoard != null) { ConeBoard.Done = true; ConeBoard.Version++; }
            int lappedAmong = cars.Count(c => c.Down >= 1 && cars.Any(o => o.Down < 1 && o.Gap > c.Gap));
            Func<List<int>, string> nums = l => string.Concat(l.Select(i => " #" + CarNumber(i)));
            Log((Cone && PreferredLane != 0 ? string.Format("choose cone done ({0} lane preferred, up to {1} rows given up for it, front {2} rows take the best spot)",
                    PreferredLane == 1 ? "inside" : "outside", GiveUpRows, FrontRows) : "double file done") +
                ": inside" + nums(coneInside) + "; outside" + nums(coneOutside) +
                (lappedAmong > 0 ? "; " + lappedAmong + " lapped car(s) among the lead lap cars" : ""));
        }

        // The player's turn comes 100 m before the cone (or at once if already past it). The chart is offered once;
        // the cars behind wait until the player has chosen. Without an answer after 10 s of racing (the game was not
        // paused) the cone rule chooses.
        bool PlayerTurn(Car c)
        {
            if (playerChoice != 0) return true;
            if (playerOffer != null) return Now - playerOfferTime > 10;
            double d;
            bool near = pastCone.Contains(c.Idx) || (toCone.TryGetValue(c.Idx, out d) && d <= 100 && (!coneNeedsLine || onConeLap.Contains(c.Idx)));
            if (!near) return false;
            var ins = coneInside.Select(CarNumber).ToList();
            var outs = coneOutside.Select(CarNumber).ToList();
            int pf;
            playerOffer = new ConeOffer
            {
                Inside = ins, Outside = outs,
                InsideCars = coneInside.Select((i, k) => Entry(i, 1, k + 1)).ToList(),
                OutsideCars = coneOutside.Select((i, k) => Entry(i, 2, k + 1)).ToList(),
                PlayerNum = c.Num, PlayerFrom = coneFrom.TryGetValue(c.Idx, out pf) ? pf : 0,
                Title = ConeBoard != null ? ConeBoard.Title : "CHOOSE CONE",
                Recommended = ChoosesInside(coneInside, coneOutside) ? 1 : 2,
                PreferredLane = Cone ? PreferredLane : 0
            };
            playerOfferTime = Now;
            Log(string.Format("cone: the player's turn - inside would be row {0}, outside row {1}", ins.Count + 1, outs.Count + 1));
            if (PlayerLaneNeeded != null) PlayerLaneNeeded(playerOffer);
            return false;
        }

        // ---- untangling -----------------------------------------------------------------------------

        // Two cars running nose to tail in the wrong order (the one ahead should be behind) are usually sorting
        // themselves out; if they are still in that order after a while, one is waiting for the other and neither
        // gives way. Then they swap spots in the plan and keep the order they have on track. In double file only
        // two cars of the same lane swap: a car never changes lane once it has chosen.
        void UntangleTick()
        {
            if (!planActive || Now - lastUntangle < 0.5) return;
            lastUntangle = Now;
            List<Car> cars = Field();
            if (cars == null) return;
            var phys = new List<Car>();
            if (inside == null) phys = cars.Where(c => planKey.ContainsKey(c.Idx)).OrderBy(c => c.Gap).ToList();
            else
                foreach (int lane in new[] { 0, 1 }) // each lane on its own, so neighbours are in the same lane
                {
                    phys.AddRange(cars.Where(c => planKey.ContainsKey(c.Idx) && planKey[c.Idx] % 2 == lane).OrderBy(c => c.Gap));
                    phys.Add(null);
                }
            double limit = flag == 6 ? 6 : 12;
            // 3.3.4 (the user, 2026-11-09): looser at the back. A car held up there (P35) kept P36-41 waiting for it to
            // come by: they were more than 25 m in front of it (never looked at) or waited the 12 s. Between two
            // lapped cars, or two cars in the last quarter of the line, any gap up to 150 m counts and 3 s is enough.
            int maxKey = planKey.Count > 0 ? planKey.Values.Max() : 0;
            var seen = new HashSet<long>();
            for (int i = 0; i + 1 < phys.Count; i++)
            {
                Car a = phys[i], b = phys[i + 1];
                if (a == null || b == null) continue;
                if (PlayerChoosesLane && (a.Idx == Addr.PlayerIdx || b.Idx == Addr.PlayerIdx)) continue; // the player keeps the spot chosen
                int ka = planKey[a.Idx], kb = planKey[b.Idx];
                bool rear = (Lapped(a) && Lapped(b)) || (ka >= maxKey * 0.75 && kb >= maxKey * 0.75);
                if (ka <= kb || b.Gap - a.Gap > (rear ? 150 : 25)) continue;
                // a lapped car stays behind the lead lap cars however long it takes to drop back
                if (Lapped(a) && !Lapped(b)) continue;
                // a waved car on its way around the pace car is placed by TidyLine
                if (GoingAround(a.Idx) || GoingAround(b.Idx)) continue;
                // the cone order is the lineup: a car that has chosen and one still waiting never trade spots
                if (coneRunning && Chose(a.Idx) != Chose(b.Idx)) continue;
                if (inside != null && b.Gap - a.Gap < 3) continue; // side by side
                long pair = a.Idx * 1000L + b.Idx;
                seen.Add(pair);
                double since;
                if (!stuckSince.TryGetValue(pair, out since)) { stuckSince[pair] = Now; continue; }
                if (Now - since < (rear ? Math.Min(limit, 3) : limit)) continue;
                Swap(a.Idx, b.Idx);
                Log(string.Format("untangled: #{0} could not get behind #{1} for {2:0} s; they keep the order they have on track", a.Num, b.Num, Now - since));
                seen.Remove(pair);
            }
            foreach (long p in stuckSince.Keys.Where(p => !seen.Contains(p)).ToList()) stuckSince.Remove(p);
        }

        bool Lapped(Car c) { return c.Down >= 1 && !eligible.ContainsKey(c.Idx) && !WaitingForOneToGo(c.Idx); }

        // 3.3.3: a lapped car queued for its wave around (or the lucky dog) keeps its spot in front of the leader until
        // one to go, when it goes around the pace car: dropped behind the lead lap cars first, it would no longer be in
        // front of them when it is checked again at one to go
        bool WaitingForOneToGo(int idx) { return WavesAtOneToGo && (waveLater.ContainsKey(idx) || (freePassPending && idx == freePassIdx)); }

        bool Chose(int idx) { return coneInside != null && (coneInside.Contains(idx) || coneOutside.Contains(idx)); }

        void Swap(int x, int y)
        {
            foreach (List<int> l in new[] { lineup, inside, outside, coneInside, coneOutside })
            {
                if (l == null) continue;
                int i = l.IndexOf(x), j = l.IndexOf(y);
                if (l != lineup && (i < 0 || j < 0)) continue; // never move a car into the other lane
                if (i >= 0) l[i] = y;
                if (j >= 0) l[j] = x;
            }
            WritePlan();
        }

        // ---- restart diagnostics --------------------------------------------------------------------

        // Compares the order the game was told (pacing rows) with where the cars physically are. Cars out of
        // place are still moving to their spot; many of them at the green means a messy restart.
        // Cars more than a spot (double file: a row) away from where the plan wants them.
        int OutOfPlace(out int placed)
        {
            placed = 0;
            List<Car> cars = Field();
            if (cars == null) return 0;
            var key = new Dictionary<int, int>();
            foreach (Car c in cars)
            {
                int line = m.I32(Addr.PacingLineIdx + c.Idx * 13), row = m.I32(Addr.PacingRow + c.Idx * 13);
                if (line < 0 || line > 1 || row < 0) continue;
                key[c.Idx] = (row + line) * 2 - line;
            }
            List<Car> wanted = cars.Where(c => key.ContainsKey(c.Idx)).OrderBy(c => key[c.Idx]).ToList();
            List<Car> actual = wanted.OrderBy(c => c.Gap).ToList();
            placed = wanted.Count;
            int slack = m.U8(Addr.NumPacingLines) >= 2 ? 1 : 0, off = 0;
            for (int i = 0; i < wanted.Count; i++) if (Math.Abs(actual.IndexOf(wanted[i]) - i) > slack) off++;
            return off;
        }

        string LineCheck()
        {
            List<Car> cars = Field();
            if (cars == null) return null;
            int lines = m.U8(Addr.NumPacingLines);
            var placed = new List<Car>();
            var key = new Dictionary<int, int>();
            foreach (Car c in cars)
            {
                int line = m.I32(Addr.PacingLineIdx + c.Idx * 13), row = m.I32(Addr.PacingRow + c.Idx * 13);
                if (line < 0 || line > 1 || row < 0) continue;
                key[c.Idx] = (row + line) * 2 - line; // single file: row order; double file: inside then outside
                placed.Add(c);
            }
            List<Car> wanted = placed.OrderBy(c => key[c.Idx]).ToList();
            List<Car> actual = placed.OrderBy(c => c.Gap).ToList();
            int slack = lines >= 2 ? 1 : 0;
            var off = wanted.Select((c, i) => new { c, want = i, at = actual.IndexOf(c) })
                            .Where(x => Math.Abs(x.want - x.at) > slack)
                            .OrderByDescending(x => Math.Abs(x.want - x.at)).ToList();
            // a lapped car in front of a lead lap car. Not counted: the free pass and waved cars, which restart at the
            // tail behind the lapped cars (the rule), and in double file a lapped car side by side with a lead lap car
            // (the inside and outside car k of a row have keys 2k+1 and 2k+2)
            Func<Car, int> spot = c => !key.ContainsKey(c.Idx) ? -1 : lines >= 2 ? (key[c.Idx] - 1) / 2 : key[c.Idx];
            Func<Car, bool> atTail = c => eligible.ContainsKey(c.Idx) || c.Idx == freePassIdx;
            int lapped = cars.Count(c => c.Down >= 1 && cars.Any(o => o.Down < 1 && !atTail(o) &&
                (spot(c) >= 0 && spot(o) >= 0 ? spot(o) > spot(c) : o.Gap > c.Gap)));
            return string.Format("{0} lines, {1} cars placed, {2} out of place{3}{4}; {5} lapped car(s) still ahead of a lead lap car",
                lines, placed.Count, off.Count,
                off.Count > 0 ? " (now->wanted: " + string.Join(", ", off.Take(8).Select(x => string.Format("#{0} {1}{2}->{3}",
                    x.c.Num, eligible.ContainsKey(x.c.Idx) ? "(waved) " : "", x.at + 1, x.want + 1))) + ")" : "",
                off.Count > 0 ? ", worst " + Math.Abs(off[0].want - off[0].at) + " spots" : "", lapped);
        }

        void RestartDiagnostics()
        {
            double now = Now;
            if (flag != prevFlag)
            {
                if (flag == 7 && prevFlag == 6 && lastLineCheck != null)
                    Log("line check just before the green: " + lastLineCheck);
                if (flag == 5) lineCheck5Done = false;
                if (flag == 6) lineCheck6Done = false;
                prevFlag = flag;
                flagSince = now;
                lastLineCheck = null;
            }
            if (flag == 5 && !lineCheck5Done && now - flagSince >= 10)
            {
                lineCheck5Done = true;
                string s = LineCheck();
                if (s != null) Log("line check 10 s after one to go: " + s);
            }
            if (flag == 6)
            {
                if (!lineCheck6Done && now - flagSince >= 1)
                {
                    lineCheck6Done = true;
                    string s = LineCheck();
                    if (s != null) Log("line check after double file: " + s);
                }
                if (now - lastLineCheckTime >= 0.25)
                {
                    lastLineCheckTime = now;
                    lastLineCheck = LineCheck();
                }
            }
        }

        // ---- the tick -------------------------------------------------------------------------------

        void ResetLineup()
        {
            lineup = inside = outside = coneInside = coneOutside = null;
            coneRunning = false;
            playerOffer = null;
            playerChoice = 0;
            planActive = false;
            planKey.Clear();
            stuckSince.Clear();
            lineupDone = earlyLineupDone = doubleFileDone = delayedGreenHandled = false;
            coneWaiting = false;
            noPitSince = double.NaN;
        }

        // ---- hold the green if the field is split (experimental, off by default) ----------------------
        // NR2003 runs a delayed green itself (flag 6 -> 4) when the line is out of shape. To ask for one when the field is
        // split - a gap in the line over SplitGapM at one to go or double file - this sets the "caution still needed"
        // byte, the lever the one-to-go hold uses before one to go. Whether the game honours it this late isn't known
        // yet: the log says what the game did next, so a live race settles it. Once per caution.
        public bool HoldGreenIfSplit = false;
        const double SplitGapM = 60;
        bool splitAsked, splitReported; int splitAskFlag;
        void SplitHoldTick()
        {
            if (flag == 0) { splitAsked = splitReported = false; return; }
            if (splitAsked && !splitReported && flag != splitAskFlag)
            {
                splitReported = true;
                Log(flag == 4 ? "field split: the game ran a delayed green - the hold worked"
                    : flag == 7 ? "field split: the game went green anyway - the caution request at this point has no effect"
                    : "field split: the game went to flag " + flag + " after the request (not a delayed green)");
            }
            if (!HoldGreenIfSplit || splitAsked || (flag != 5 && flag != 6)) return;
            List<Car> cars = Field();
            if (cars == null || cars.Count < 4) return;
            var line = cars.Where(c => !GoingAround(c.Idx)).OrderBy(c => c.Gap).ToList();
            for (int i = 1; i < line.Count; i++)
            {
                double gap = line[i].Gap - line[i - 1].Gap;
                if (gap <= SplitGapM) continue;
                splitAsked = true; splitAskFlag = flag;
                m.W8(Addr.Yellow, 1);
                Func<int, string> why = ix => (pitted.Contains(ix) ? " pitted this caution" : "") + (waveLater.ContainsKey(ix) ? " wave still queued" : "") +
                    (eligible.ContainsKey(ix) ? (settle.ContainsKey(ix) && settle[ix].FreePass ? " free pass" : " waved/sent around") : "");
                Log(string.Format("field split at flag {0}: #{1} is {2:0} m behind #{3} ({4} cars ahead of the gap, {5} behind) - asking the game for another caution lap [behind car:{6}; ahead car:{7}]",
                    flag, line[i].Num, gap, line[i - 1].Num, i, line.Count - i, why(line[i].Idx).Length > 0 ? why(line[i].Idx) : " nothing special", why(line[i - 1].Idx).Length > 0 ? why(line[i - 1].Idx) : " nothing special"));
                return;
            }
        }

        void LineupTick()
        {
            RestartDiagnostics();
            LowLineTick();
            if (flag == 7) return;
            // delayed green: when the cars are in their double file spots they stay double file, lanes and all, to
            // the next green; only a field that is out of place is lined up again
            if (flag == 4 && (lineupDone || doubleFileDone) && !delayedGreenHandled)
            {
                delayedGreenHandled = true;
                int placed, off = OutOfPlace(out placed);
                if (inside != null && planActive && placed > 0 && off * 4 <= placed)
                {
                    Log(string.Format("delayed green: {0} of {1} cars out of place, the double file is kept (lanes locked)", off, placed));
                    WritePlan();
                }
                else
                {
                    Log(string.Format("delayed green: {0} of {1} cars out of place, the lineup and double file will be redone", off, placed));
                    lineupDone = doubleFileDone = coneWaiting = false;
                    inside = outside = coneInside = coneOutside = null;
                    coneRunning = false;
                    playerOffer = null;
                    planActive = false;
                    stuckSince.Clear();
                }
            }
            if (flag != 4) delayedGreenHandled = false;
            bool lineupOn = WaveAroundOn || LappedToBack;
            if (lineupOn && EarlyLineup && flag >= 2 && flag <= 4) EarlyLineUpTick();
            if (flag == 5 && lineupOn && !lineupDone && inside == null)
            {
                lineupDone = true;
                LineUp("one to go");
            }
            LearnDoubleFileAt();
            if (Classic)
            {
                if ((flag == 5 || flag == 6) && DoubleFile && !doubleFileDone) ClassicDoubleFile();
            }
            else if ((flag == 5 || (flag == 6 && coneWaiting)) && DoubleFile && !doubleFileDone && ConeOnOneToGoLap() && (lineupDone || !lineupOn))
                OneToGoConeTick();
            if (flag == 6 && DoubleFile && !doubleFileDone)
            {
                doubleFileDone = true;
                StartCone(false);
            }
            if (flag == 5 || flag == 6) ConeTick();
            if (flag >= 2 && flag <= 6) HoldPlan();
            if (Untangle && (flag == 3 || flag == 5 || flag == 6)) UntangleTick();
        }
    }

    // What the player is offered at the cone: the car numbers that have chosen each lane so far, front first.
    public class ConeOffer
    {
        public List<int> Inside, Outside;
        public List<ConeEntry> InsideCars, OutsideCars;
        public int PlayerNum, PlayerFrom;   // the player's spot in the single file line
        public string Title;
        public int Recommended;     // 1 inside, 2 outside: what the cone rule would pick
        public int PreferredLane;   // 0 none, 1 inside, 2 outside
    }

    // One car on the choose cone board.
    public class ConeEntry
    {
        public int Idx, Num, Lane, Row;
        public int From, To;        // spot in the single file line before the cone, spot in the double file after
        public int Gained;          // From - To (0 for the leader)
        public bool Player;
    }

    public class ConeBoardData
    {
        public string Title;
        public int Cars;            // cars in the line at double file
        public List<ConeEntry> Inside = new List<ConeEntry>(), Outside = new List<ConeEntry>();
        public bool Done;
        public int Version;
    }
}
