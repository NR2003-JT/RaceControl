using System;
using System.Collections.Generic;
using System.Linq;

namespace RaceControl
{
    // NASCAR rule (since 2009): lapped cars between the pace car and the leader that do not pit while pit road
    // is open are waved around the pace car, gain one lap and restart behind the lead lap cars. A car under
    // penalty is not eligible.
    public partial class RaceEngine
    {
        public bool OpenPitsToAll = true;
        public bool WaveAroundOn = true;
        public bool ClearClosedPitPenalty = true;
        public bool RestorePaceLap = true;  // once a waved car has crossed the line, give it the pace car's pace lap so it lines up

        class Settle { public double Down, Before; public bool Waved, LeadLap, FreePass, Credited; }
        class Eligible { public double Down; public bool Pitted, Passed, Restored, FromAhead; public double LastGap = -1, BehindSince = -1, RestoredAt = double.NaN; public int LapsAtPass; }

        HashSet<int> pitted = new HashSet<int>();                       // was on pit road this caution
        Dictionary<int, Settle> settle = new Dictionary<int, Settle>(); // laps to check after the green
        HashSet<int> excused = new HashSet<int>();                      // moved by the officials, no pace-car penalty
        Dictionary<int, Eligible> eligible = new Dictionary<int, Eligible>(); // waved this caution
        HashSet<int> closedLapOffenders = new HashSet<int>();           // drove onto closed pits: their penalty stands
        Dictionary<int, string> decided = new Dictionary<int, string>(); // first time by: waved/pitted/lead/behind/penalty
        int leaderIdx = -1;
        int pitOpenLap = -1;
        bool pitsOpened, firstPassStarted;
        double greenTime = double.NaN;

        Dictionary<int, int> penaltySeen = new Dictionary<int, int>();
        int penaltyTick;

        // scoring watch: every stripe crossing of a waved car until two laps after the green
        class Watch { public int Laps, GreenCrossings; }
        Dictionary<int, Watch> watched = new Dictionary<int, Watch>();

        // Waved cars have been seen on the standings on the lead lap but over a minute behind the leader. Logs each
        // crossing: the lap, how long after the leader it crossed that lap, and where the car physically is.
        void WatchWavedCrossings()
        {
            foreach (int idx in eligible.Keys)
                if (!watched.ContainsKey(idx)) watched[idx] = new Watch { Laps = LapsOf(idx) };
            if (watched.Count == 0) return;
            double trackLen = m.F64(Addr.TrackLength);
            Car leader = null, first;
            List<Car> cars = null;
            foreach (var kv in watched.ToList())
            {
                int idx = kv.Key, laps = LapsOf(idx);
                if (laps == kv.Value.Laps) continue;
                kv.Value.Laps = laps;
                if (cars == null) cars = Field(out leader, out first) ?? new List<Car>();
                Car me = cars.FirstOrDefault(c => c.Idx == idx);
                Car lead = leader;
                int scored = ScoredLaps(idx);
                string timing = "";
                if (lead != null && scored >= 0 && scored < 512)
                {
                    uint mine = m.U32(Addr.CrossTable + idx * 2048 + scored * 4) & 0x0FFFFFFF;
                    uint theirs = m.U32(Addr.CrossTable + lead.Idx * 2048 + scored * 4) & 0x0FFFFFFF;
                    timing = theirs != 0 && mine != 0
                        ? string.Format(", crossed lap {0} {1:0.0} s after leader #{2}", scored, (mine - (double)theirs) / 1000, lead.Num)
                        : string.Format(", leader #{0} has no crossing for lap {1}", lead.Num, scored);
                }
                string where = me == null ? "on pit road or not running"
                    : string.Format("{0:0} m behind the pace car, {1} laps behind the leader on track, line row {2}{3}",
                        me.Gap, F2(me.Down), m.I32(Addr.PacingRow + idx * 13), me.Ahead ? ", still ahead of the leader" : "");
                Log(string.Format("scoring watch #{0}: laps {1} (scored {2}{3}); {4}", CarNumber(idx), laps, scored, timing, where));
                if (flag == 7 && ++kv.Value.GreenCrossings >= 2) watched.Remove(idx);
            }
        }

        // ---- penalties ------------------------------------------------------------------------------

        List<uint> PenaltyRules()
        {
            var rules = new List<uint>();
            uint mgr = m.U32(Addr.PenaltyList);
            if (mgr == 0) return rules;
            uint arr = m.U32(mgr + 4);
            int cnt = m.I32(mgr + 8);
            if (arr == 0 || cnt <= 0 || cnt > 64) return rules;
            for (int r = 0; r < cnt; r++) rules.Add(m.U32(arr + r * 4));
            return rules;
        }

        // Serving or owing a penalty (any rule except the pace-car one, which the lineup itself causes).
        bool HasPenalty(int idx)
        {
            foreach (uint rule in PenaltyRules())
                if (rule != 0 && rule != Addr.PaceCarPenalty && (m.U32(rule + 4 + idx * 4) & 0xF) != 0) return true;
            return false;
        }

        // Logs every penalty the game hands out, ten times a second.
        void WatchPenalties()
        {
            if (++penaltyTick % 10 != 0) return;
            List<uint> rules = PenaltyRules();
            for (int r = 0; r < rules.Count; r++)
            {
                if (rules[r] == 0) continue;
                for (int idx = 0; idx <= maxIdx; idx++)
                {
                    uint v = m.U32(rules[r] + 4 + idx * 4);
                    int key = r * 64 + idx, typ = (int)(v & 0xF), seen;
                    penaltySeen.TryGetValue(key, out seen);
                    if (typ == seen) continue;
                    penaltySeen[key] = typ;
                    if (typ != 0)
                        Log(string.Format("penalty: rule {0} on #{1} type {2} amount {3} (0x{4:X}){5}", r, CarNumber(idx), typ,
                            (v >> 4) & 0xFFF, v, excused.Contains(idx) ? " [moved by the lineup]" : ""));
                    else
                        Log(string.Format("penalty cleared: rule {0} on #{1}", r, CarNumber(idx)));
                }
            }
        }

        // Cars moved by the officials must not be penalised for passing the pace car on the way:
        // stop the rule's timer and remove any penalty it set.
        void Excuse()
        {
            foreach (int idx in excused) ExcuseCar(idx, "it was moved by the lineup");
        }

        // Rule 14 in NR2003's list ("Passing under yellow", held for laps): its timer is stopped and a penalty it set
        // is removed.
        void ExcuseCar(int idx, string why)
        {
            long t = Addr.PaceCarPenalty + 0xF7 + idx * 12;
            if (m.I32(t + 8) != unchecked((int)0xBFF00000)) // high half of -1.0
            {
                m.W32(t + 4, 0);
                m.W32(t + 8, unchecked((int)0xBFF00000));
            }
            long p = Addr.PaceCarPenalty + 4 + idx * 4;
            uint v = m.U32(p);
            if ((v & 0xF) != 0)
            {
                m.W32(p, unchecked((int)(v & 0xFFFF0000)));
                Log(string.Format("removed 'passing under yellow' penalty from #{0} (0x{1:X}), {2}", CarNumber(idx), v, why));
            }
        }

        // Race Control sets the order under caution (lineups, wave arounds, untangling, the cone), so cars pass each
        // other under yellow because they were told to, and the scramble of an out-of-place restart makes AI cars
        // pass on the inside. NR2003 hands out laps-long penalties for both. AI cars are forgiven "passing under
        // yellow" (rule 14) under caution and on the first green lap, and "passing on the inside" (rule 11) on the
        // first green lap. The player keeps them unless Race Control moved the player's car.
        public bool ForgivePenalties = true;
        int restartLap = -1, forgiveTick;
        bool cautionRunning;

        void ForgiveTick()
        {
            if (flag != 7) cautionRunning = true;
            else if (cautionRunning) { cautionRunning = false; restartLap = lap; }
            if (flag == 7 && restartLap >= 0 && lap > restartLap + 1) restartLap = -1;
            if (!ForgivePenalties || ++forgiveTick % 5 != 0) return;
            bool restart = flag == 7 && restartLap >= 0;
            if (flag == 7 && !restart) return;
            List<uint> rules = PenaltyRules();
            if (rules.Count <= 14 || rules[14] != Addr.PaceCarPenalty) return; // not the rule list Race Control knows
            uint inside = rules[11];
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx)) continue;
                if (idx == Addr.PlayerIdx && CarNumber(idx) > 0 && !excused.Contains(idx)) continue;
                ExcuseCar(idx, restart ? "at the restart (Race Control set the order)" : "under caution (Race Control sets the order)");
                if (restart && inside != 0)
                {
                    uint v = m.U32(inside + 4 + idx * 4);
                    if ((v & 0xF) != 0)
                    {
                        m.W32(inside + 4 + idx * 4, 0);
                        Log(string.Format("removed 'passing on the inside' penalty from #{0} (0x{1:X}) at the restart", CarNumber(idx), v));
                    }
                }
                CallOffPenaltyStop(idx, rules);
            }
        }

        // The AI's pit check marks a car that has a penalty to serve (+8119) and asks it in (+8118); clearing the
        // penalty afterwards does not take that back, so a forgiven car still drove to pit road. Called off unless
        // the car owes another penalty (AIMOD only ever sets +8118, so +8119 marks the game's penalty stop).
        void CallOffPenaltyStop(int idx, List<uint> rules)
        {
            long stop = Addr.PenaltyStop + idx * Addr.Ocd;
            if (m.U8(stop) == 0 || OnPitRoad(idx) || NeedsRepair(idx)) return;
            for (int r = 0; r < rules.Count; r++)
                if (r != 11 && r != 14 && rules[r] != 0 && (m.U32(rules[r] + 4 + idx * 4) & 0xF) != 0) return;
            m.W8(stop, 0);
            m.W8(Addr.Pitting + idx * Addr.Ocd, 0);
            // the game sets it again every frame for a while: logged once, and once more if it keeps at it
            double since;
            if (!stopCalledOff.TryGetValue(idx, out since))
            {
                stopCalledOff[idx] = Now;
                Log("#" + CarNumber(idx) + "'s penalty stop called off (its penalty was forgiven)");
            }
            else if (since >= 0 && Now - since >= 10)
            {
                stopCalledOff[idx] = -1;
                Log(string.Format("#{0}'s penalty stop is still being asked for by the game after {1:0} s; still called off", CarNumber(idx), Now - since));
            }
        }

        Dictionary<int, double> stopCalledOff = new Dictionary<int, double>(); // car -> first called off this caution (-1: logged twice)

        // A car the game rates as needing repairs (+0x1B33 >= 1) is on its way in for them: the game's repair (limp) and
        // tow modes set the same +8119 / +8118 flags as a penalty stop. Calling those off left a damaged car limping round
        // without its repair (limp probe, Michigan 2026-10-28). Every site that clears the stop flag checks this first.
        bool NeedsRepair(int idx) { return m.I32(Addr.RepairNeed + idx * Addr.Ocd) >= 1; }

        // Diagnostics: every pit request that appears under caution, with what NR2003's own AI pit check
        // (0x40B5D0) looks at, so a car the game calls in by itself shows why. The check asks a car in for:
        // fuel (the value at +0x1DBB), car condition (two checks), a penalty it must serve now (also sets +8119),
        // or any penalty (green flag, or with a condition check). AIMOD writes the same request for its own
        // calls and logs them, so a line here with no AIMOD call at that moment is the game's.
        bool[] requestSeen = new bool[64];
        HashSet<int> requestsLogged = new HashSet<int>();

        // The game's penalty stop flag (+8119), green or yellow, checked every tick: the rule that set it is
        // logged the moment it appears (the penalty scan above samples less often and missed short-lived ones
        // under time acceleration).
        bool[] stopFlagSeen = new bool[64];
        double[] stopFlagLogged = Enumerable.Repeat(double.NegativeInfinity, 64).ToArray();

        void WatchPenaltyStopFlag()
        {
            List<uint> rules = null;
            for (int idx = 0; idx <= maxIdx && idx < 64; idx++)
            {
                if (idx == Addr.PaceCarIdx) continue;
                bool set = Running(idx) && m.U8(Addr.PenaltyStop + idx * Addr.Ocd) != 0;
                bool was = stopFlagSeen[idx];
                stopFlagSeen[idx] = set;
                if (!set) { phantomSince[idx] = double.NaN; continue; }
                if (rules == null) rules = PenaltyRules();
                List<string> owed = Owed(idx, rules);
                if (!was && Now - stopFlagLogged[idx] >= 30) // the game sets it again every frame after a call-off: once per 30 s
                {
                    stopFlagLogged[idx] = Now;
                    Log(string.Format("game penalty stop flag set for #{0} ({1}): {2}", CarNumber(idx), flag == 7 ? "green" : "caution",
                        owed.Count == 0 ? "no penalty showing" : string.Join(", ", owed.ToArray())));
                }
                PhantomStop(idx, owed);
            }
        }

        List<string> Owed(int idx, List<uint> rules)
        {
            var owed = new List<string>();
            for (int r = 0; r < rules.Count; r++)
            {
                if (rules[r] == 0) continue;
                uint v = m.U32(rules[r] + 4 + idx * 4);
                if ((v & 0xF) != 0) owed.Add(string.Format("rule {0} type {1} (0x{2:X})", r, v & 0xF, v));
            }
            return owed;
        }

        // Seen live (twice in one race, during or just after time acceleration): the game set a car's penalty
        // stop flag (+8119, only set when a penalty must be served now) while no penalty showed for that car,
        // and brought it in for a stop it did not owe. A flag with no penalty behind it for a second is called
        // off (with its pit request), like a forgiven penalty's. Off with ForgivePenalties=0.
        double[] phantomSince = Enumerable.Repeat(double.NaN, 64).ToArray();
        double[] phantomLogged = Enumerable.Repeat(double.NegativeInfinity, 64).ToArray();

        void PhantomStop(int idx, List<string> owed)
        {
            // A car the game rates as needing repairs is on its way in for them: the game's repair (limp) mode sets the same
            // flag. Calling that off left it limping round at 70 % pace without a repair (limp probe, Michigan 2026-10-28),
            // and AIMOD 2.1.0 calls these cars in itself: leave them be.
            if (!ForgivePenalties || owed.Count > 0 || OnPitRoad(idx) || NeedsRepair(idx))
            { phantomSince[idx] = double.NaN; return; }
            if (double.IsNaN(phantomSince[idx])) { phantomSince[idx] = Now; return; }
            if (Now - phantomSince[idx] < 1) return;
            m.W8(Addr.PenaltyStop + idx * Addr.Ocd, 0);
            m.W8(Addr.Pitting + idx * Addr.Ocd, 0);
            stopFlagSeen[idx] = false;
            phantomSince[idx] = double.NaN;
            if (Now - phantomLogged[idx] >= 30)
            {
                phantomLogged[idx] = Now;
                Log("phantom penalty stop called off for #" + CarNumber(idx) + " (no penalty showing for 1 s)");
            }
        }

        // Pace laps before the start. Several cars a race were called in for a phantom penalty stop on lap 1 and
        // one still pitted on lap 3: the flag looks left over from practice or qualifying. A car on track with the
        // flag set and no penalty behind it has it cleared with its pit request (cars in their stalls are left
        // alone: pit road starts). Each car is logged once, and again if the game sets it back.
        readonly Dictionary<int, int> startFlagsSeen = new Dictionary<int, int>();

        void PreStartTick()
        {
            List<uint> rules = null;
            for (int idx = 0; idx <= maxIdx && idx < 64; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx) || m.U8(Addr.PenaltyStop + idx * Addr.Ocd) == 0) continue;
                if (rules == null) rules = PenaltyRules();
                List<string> owed = Owed(idx, rules);
                bool player = idx == Addr.PlayerIdx && CarNumber(idx) > 0;
                bool repair = NeedsRepair(idx);
                bool clear = ForgivePenalties && owed.Count == 0 && !OnPitRoad(idx) && !player && !repair;
                int n;
                startFlagsSeen.TryGetValue(idx, out n);
                startFlagsSeen[idx] = n + 1;
                if (n == 0)
                    Log(string.Format("game penalty stop flag set for #{0} during the pace laps before the start: {1}{2}", CarNumber(idx),
                        owed.Count == 0 ? "no penalty showing" : string.Join(", ", owed.ToArray()),
                        clear ? " - stale, cleared" : OnPitRoad(idx) ? " - in its pit stall, left alone" : repair ? " - it needs repairs, left alone" : " - left alone"));
                else if (n == 1 && clear)
                    Log("game penalty stop flag set again for #" + CarNumber(idx) + " before the start (the game sets it back): clearing it every tick until the green");
                if (!clear) continue;
                m.W8(Addr.PenaltyStop + idx * Addr.Ocd, 0);
                m.W8(Addr.Pitting + idx * Addr.Ocd, 0);
            }
        }

        void WatchPitRequests()
        {
            List<uint> rules = null;
            for (int idx = 0; idx <= maxIdx && idx < 64; idx++)
            {
                if (idx == Addr.PaceCarIdx) continue;
                bool req = Running(idx) && m.U8(Addr.Pitting + idx * Addr.Ocd) != 0;
                bool was = requestSeen[idx];
                requestSeen[idx] = req;
                if (!req || was || OnPitRoad(idx)) continue;
                // once per car while pit road is held closed (the game keeps asking, Race Control keeps calling
                // it off) and once after it opens
                if (!requestsLogged.Add(idx * 2 + (pitsOpen ? 1 : 0))) continue;
                if (rules == null) rules = PenaltyRules();
                var owed = new List<string>();
                for (int r = 0; r < rules.Count; r++)
                {
                    if (rules[r] == 0) continue;
                    uint v = m.U32(rules[r] + 4 + idx * 4);
                    if ((v & 0xF) != 0) owed.Add(string.Format("rule {0} type {1}", r, v & 0xF));
                }
                Log(string.Format("pit request for #{0} under caution, pits {5} (AIMOD's or the game's): penalty flag {1}, penalties {2}, pit check value {3:0.###}, laps {4}",
                    CarNumber(idx), m.U8(Addr.PenaltyStop + idx * Addr.Ocd), owed.Count == 0 ? "none" : string.Join(", ", owed.ToArray()),
                    m.F64(Addr.PitCheckValue + idx * Addr.Ocd), LapsOf(idx), pitsOpen ? "open" : "closed"));
            }
        }

        // A car sent or waved around from in front of the leader that drops back behind the leader instead of
        // passing the pace car (seen live: one of six cars just let the field by) is where it was going anyway,
        // at the back. Its pace lap is given back so it does not then try to pass the whole field, and pit road
        // no longer waits for it. A lead lap car keeps its lap (credited at the green if short).
        void DroppedBackTick()
        {
            List<Car> cars = null;
            foreach (var kv in eligible)
            {
                Eligible e = kv.Value;
                if (!e.FromAhead || !GoingAround(kv.Key)) continue;
                if (cars == null) cars = Field() ?? new List<Car>();
                Car c = cars.FirstOrDefault(o => o.Idx == kv.Key);
                if (c == null || c.Ahead) { e.BehindSince = -1; continue; }
                if (e.BehindSince < 0) { e.BehindSince = Now; continue; }
                if (Now - e.BehindSince < 3) continue;
                e.Restored = true;
                GiveBackPaceLap(kv.Key);
                sentEarly.Remove(kv.Key);
                Log(string.Format("#{0} dropped back behind the leader instead of going around the pace car: it stays at the back, pit road no longer waits for it", c.Num));
            }
        }

        // The pace lap of a car that gave up going around: "+1" as before, or (3.3.9, pace laps fitted) the value that
        // matches its gap to the car it now follows in the line.
        void GiveBackPaceLap(int idx)
        {
            long a = Addr.PaceLap + idx * Addr.Ocd;
            int was = m.I32(a);
            int? fit = PaceFixes && PaceLapFit ? LinePaceLap(idx) : null;
            int set = fit ?? was + 1;
            m.W32(a, set);
            if (fit.HasValue && set != was + 1)
                Log(string.Format("#{0} pace lap {1} -> {2} (fitted to its gap to the car it follows, not +1)", CarNumber(idx), was, set));
        }

        void OpenPits()
        {
            int w = m.U16(Addr.PitState);
            if ((w & 0x100) == 0) return;
            m.W16(Addr.PitState, (w & ~0x100) | 0x8);
            if (!pitsOpened) Log(string.Format("pit road opened to all cars (state 0x{0:X})", w));
            pitsOpened = true;
        }

        // Pits open to all: the game still hands lapped cars an "entering closed pits" penalty. A car that went in
        // while Race Control held the pits closed keeps its penalty.
        void ClearClosedPitPenalties()
        {
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (closedLapOffenders.Contains(idx)) continue;
                int v = m.I32(Addr.ClosedPitPenalty + idx * 4);
                if (v == 0) continue;
                m.W32(Addr.ClosedPitPenalty + idx * 4, 0);
                Log(string.Format("cleared closed-pits penalty on #{0} (0x{1:X})", CarNumber(idx), v));
            }
        }

        // Gives one lap back through scoring: writes the next lap-crossing entry and the cached lap count.
        // The car's next real crossing then continues from the new count.
        bool CreditLap(int idx, int leader)
        {
            int laps = ScoredLaps(idx);
            if (laps < 0 || laps + 1 >= 512) return false;
            long row = Addr.CrossTable + idx * 2048;
            uint cur = m.U32(row + laps * 4), nxt = m.U32(row + (laps + 1) * 4);
            if ((cur & 0x1FFFFFFF) == 0 || (nxt & 0x1FFFFFFF) != 0)
            {
                Log("skipped lap credit for #" + CarNumber(idx) + " (scoring row not as expected)");
                return false;
            }
            uint flags = m.U32(Addr.CrossTable + leader * 2048 + (laps + 1) * 4) & 0xF0000000;
            uint t = (uint)((long)Math.Floor(Addr.GameSeconds(m) * 1000) & 0x0FFFFFFF);   // the crossing clock
            m.W32(row + (laps + 1) * 4, unchecked((int)(t | flags)));
            m.W32(Addr.StandingsCache + idx * 16, laps + 1);
            return true;
        }

        // Waves one car around the pace car the way NR2003 does itself: its pace lap count drops by one, which
        // lets it pass the pace car, and it goes to the end of the line; once past the pace car it is lined up
        // behind the lead lap cars (and cars waved before it), ahead of the lapped cars (TidyLine). Driving
        // around it gains its lap on track; after that it is free to pit.
        void WaveCar(Car c, List<Car> cars, bool leadLap) { WaveCar(c, cars, leadLap, c.Down); }

        void WaveCar(Car c, List<Car> cars, bool leadLap, double down)
        {
            var inLine = new List<KeyValuePair<int, Car>>();
            foreach (Car o in cars)
            {
                if (o.Idx == c.Idx) continue;
                int row = m.I32(Addr.PacingRow + o.Idx * 13);
                if (row >= 1) inLine.Add(new KeyValuePair<int, Car>(row, o));
            }
            List<Car> order = inLine.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            order.Add(c); // the end of the line until it has passed the pace car (TidyLine)
            WriteLine(order.Select(o => o.Idx).ToList());
            long a = Addr.PaceLap + c.Idx * Addr.Ocd;
            // 3.3.9: with the pace laps fitted, set to one lap down on the pace car instead of "minus one"
            int was = m.I32(a), set = PaceFixes && PaceLapFit ? PaceLapOneDown(c.Idx) : was - 1;
            m.W32(a, set);
            if (set != was - 1)
                Log(string.Format("#{0} pace lap {1} -> {2} to go around the pace car (set to one lap down on it, not minus one)",
                    c.Num, was, set));
            // NR2003's own wave (the !wave chat command, 0x491650) does the same pace lap change and also marks
            // the car waved in the pacing table, which the game's line upkeep reads
            m.W8(Addr.PacingWaved + c.Idx * 13, 1);
            excused.Add(c.Idx);
            eligible[c.Idx] = new Eligible { Down = down, FromAhead = c.Ahead };
            // laps down = the times the leader has passed it: a car 1.97 laps behind is just ahead of the leader,
            // lapped once, and goes onto the lead lap. (It used to be expected to stay 1 down, so the lap a waved
            // car just ahead of the leader is owed was never credited.)
            settle[c.Idx] = new Settle { Down = leadLap ? 0 : Math.Max(0, Math.Floor(down + 0.001) - 1), Waved = true, LeadLap = leadLap, Before = down };
        }

        // ---- free pass (lucky dog) ------------------------------------------------------------------

        // The first car one lap down gets its lap back. It is chosen once the field is bunched up behind the pace
        // car (the standings then match the order on track), before pit road opens, and goes around the pace car
        // like a waved car; pit road waits until it has caught the tail, so it can pit with everyone else. It
        // restarts at the tail of the field. If it cannot get around the pace car in time it drops to the tail
        // and its lap is credited at the green instead.
        public bool FreePassOn = true;
        int freePassIdx = -1;
        bool freePassDone;
        double freePassTime, freePassWaitSince = double.NaN;
        bool freePassWaitLogged;
        const double FreePassGiveUp = 45;       // seconds to get around the pace car

        // Classic rules (no wave around, no cone): the game runs pit road, the pace laps and one to go as it does
        // stock; Race Control only adds the Lucky Dog and (with double file) the 2004 lineup. Michigan 2026-10-28:
        // with the wave around off the Lucky Dog never ran, and the pits were held closed for 2 minutes.
        public bool Classic = false;

        // Laps down when the leader took the yellow (flag 1), for the whole caution. Measured on track later, a lapped
        // car that crossed the line while the leaders were on pit road read as lead lap (Michigan 2026-10-28: #17, and
        // the Lucky Dog went to #25 instead). Nobody gains or loses a lap on the leader after it has taken the yellow.
        Dictionary<int, double> downAtYellow = new Dictionary<int, double>();
        bool downAtYellowTaken;

        void TakeDownAtYellow()
        {
            if (downAtYellowTaken || flag < 1 || flag > 6) return;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            var prog = new Dictionary<int, double>();
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx)) continue;
                float d = DlongOf(idx);
                prog[idx] = LapCount(idx, d, trackLen) + d / trackLen;
            }
            if (prog.Count == 0) return;
            downAtYellowTaken = true;
            double lead = prog.Values.Max();
            foreach (var kv in prog) downAtYellow[kv.Key] = lead - kv.Value;
            var lapped = downAtYellow.Where(kv => kv.Value >= 1).OrderBy(kv => kv.Value).Select(kv => "#" + CarNumber(kv.Key)).ToList();
            Log(string.Format("laps down when the leader took the yellow: {0} lead lap, {1} lapped{2}{3}", prog.Count - lapped.Count, lapped.Count,
                lapped.Count > 0 ? " (" + string.Join(" ", lapped.ToArray()) + ")" : "", flag > 1 ? "; taken late, at flag " + flag : ""));
        }

        // Laps down for the caution's decisions: the count at the yellow in classic rules, else the one on track now.
        double DownOf(Car c)
        {
            double d;
            return Classic && downAtYellow.TryGetValue(c.Idx, out d) ? d : c.Down;
        }

        // 5 s after the green: does the game's scoring agree with the laps down at the yellow (the Lucky Dog one
        // better)? Only logged for now - the user saw lapped cars scored on the lead lap ~2 minutes behind the leader.
        Dictionary<int, double> scoringSnap;
        HashSet<int> scoringDogs;
        double scoringSnapAt;

        void ScoringCheckTick()
        {
            if (scoringSnap == null) return;
            if (flag != 7) { scoringSnap = null; return; }
            if (Now - scoringSnapAt < 5) return;
            var snap = scoringSnap;
            scoringSnap = null;
            var laps = new Dictionary<int, int>();
            foreach (int idx in snap.Keys) if (Running(idx)) laps[idx] = ScoredLaps(idx);
            if (laps.Count == 0) return;
            int lead = laps.Values.Max();
            var wrong = new List<string>();
            foreach (var kv in laps.OrderBy(kv => CarNumber(kv.Key)))
            {
                int want = (int)Math.Floor(snap[kv.Key] + 0.001) - (scoringDogs.Contains(kv.Key) ? 1 : 0);
                int have = lead - kv.Value;
                if (have < want) wrong.Add(string.Format("#{0} {1} down at the yellow{2}, scored {3} down", CarNumber(kv.Key), Math.Max(0, want + (scoringDogs.Contains(kv.Key) ? 1 : 0)),
                    scoringDogs.Contains(kv.Key) ? " (Lucky Dog)" : "", have));
            }
            Log(wrong.Count == 0 ? "scoring check after the green: every car's laps match the yellow"
                                 : "scoring check after the green: the game's scoring has gained laps the cars did not earn: " + string.Join(", ", wrong.ToArray()));
        }

        void FreePassTick()
        {
            if (!FreePassOn || (!WaveAroundOn && !Classic)) return;
            if (freePassDone)
            {
                FreePassAtOneToGo();
                if (!freePassPending) FreePassWatch();
                return;
            }
            if (flag < 2 || flag > 5) return;
            Car leader, first;
            List<Car> cars = Field(out leader, out first);
            if (cars == null) return;
            if (Classic)
            {
                // the game opens pit road to the lead lap at the yellow and to the lapped cars at flag 3: the Lucky
                // Dog goes around once the pace car has picked up the field, before the lapped cars may pit
                if (flag == 2 && PaceStatesOk() && !(PaceCarOnTrack() && first.Gap <= PickedUpWithin)) return;
            }
            // bunched up: the same test pit road waits for (or the pits are open already)
            else if (!pitsOpen && flag < 5 && (!PaceCarPickedUp() || NotPickedUp(cars, leader, true) != null)) return;
            Car dog = cars.Where(c => DownOf(c) >= 1 && DownOf(c) < 2 && !eligible.ContainsKey(c.Idx) && !waveLater.ContainsKey(c.Idx)
                                      && !pitted.Contains(c.Idx) && !HasPenalty(c.Idx))
                          .OrderBy(DownOf).FirstOrDefault();
            // a car still finishing its green flag stop may be the first car one lap down: wait for it to come off
            // pit road (up to a minute, never past one to go)
            double trackLen = m.F64(Addr.TrackLength);
            foreach (int idx in pitRoadAtCaution.Where(i => Running(i) && OnPitRoad(i) && !HasPenalty(i)).ToList())
            {
                float dl = DlongOf(idx);
                double down = leader.Prog - (LapCount(idx, dl, trackLen) + dl / trackLen);
                if (Classic) downAtYellow.TryGetValue(idx, out down);
                if (down < 1 || down >= 2 || (dog != null && DownOf(dog) <= down)) continue;
                if (double.IsNaN(freePassWaitSince)) freePassWaitSince = Now;
                if (flag < 5 && Now - freePassWaitSince < 60)
                {
                    if (!freePassWaitLogged) Log(string.Format("free pass: waiting for #{0} ({1} laps behind the leader) to finish its green flag stop", CarNumber(idx), F2(down)));
                    freePassWaitLogged = true;
                    return;
                }
            }
            freePassDone = true;
            if (dog == null)
            {
                Log("free pass: no car one lap down this caution");
                return;
            }
            freePassIdx = dog.Idx;
            freePassTime = Now;
            decided[dog.Idx] = "free pass";
            if (WavesAtOneToGo && FreePassLate)
            {
                // 3.3.3: chosen now, sent with the wave arounds at one to go (FreePassAtOneToGo)
                freePassPending = true;
                freePassDownChosen = DownOf(dog);
                Log(string.Format("lucky dog: #{0} ({1} laps behind the leader, the first car one lap down) gets the free pass at one to go", dog.Num, F2(freePassDownChosen)));
                return;
            }
            SendFreePass(dog, cars, DownOf(dog));
        }

        // 3.3.5 (the user, run K): the free pass goes at the first chance again (it may still pit; wave arounds may not).
        // Sent at one to go it was still going around during the choose cone and spoiled good restarts.
        public bool FreePassLate = false;            // true: the free pass at one to go with the wave arounds (3.3.3-3.3.4)
        bool freePassPending;
        double freePassDownChosen;

        void FreePassAtOneToGo()
        {
            if (!freePassPending || flag < 5 || flag > 6) return;
            freePassPending = false;
            List<Car> cars = Field();
            Car dog = cars == null ? null : cars.FirstOrDefault(c => c.Idx == freePassIdx);
            string no = dog == null ? "out of the race or on pit road" : OnPitRoad(dog.Idx) ? "on pit road" : HasPenalty(dog.Idx) ? "under penalty" : null;
            if (no != null)
            {
                Log(string.Format("lucky dog: #{0} not sent around at one to go ({1}): no free pass this caution", CarNumber(freePassIdx), no));
                freePassIdx = -1;
                return;
            }
            freePassTime = Now;
            SendFreePass(dog, cars, DownOf(dog));
        }

        void SendFreePass(Car dog, List<Car> cars, double dogDown)
        {
            WaveCar(dog, cars, false, dogDown);
            Settle st = settle[dog.Idx];
            // one lap down in the standings (behind the leader on track: whole laps; in front of it: one more)
            st.Down = Math.Max(0, Math.Floor(dogDown + 0.001) - 1);
            st.FreePass = true;
            Log(string.Format("free pass: #{0} ({1} laps behind the leader{2}, the first car one lap down) goes around the pace car to get its lap back; restarts at the tail (pit road does not wait for it)",
                dog.Num, F2(dogDown), Classic && downAtYellow.ContainsKey(dog.Idx) ? " when the leader took the yellow" : ""));
        }

        // A free pass car that has not got around the pace car in time drops to the tail; its lap is credited at
        // the green and pit road no longer waits for it.
        void FreePassWatch()
        {
            if (freePassIdx < 0 || !GoingAround(freePassIdx) || Now - freePassTime < FreePassGiveUp) return;
            Eligible e = eligible[freePassIdx];
            e.Restored = true;
            GiveBackPaceLap(freePassIdx);
            sentEarly.Remove(freePassIdx);
            Log(string.Format("free pass: #{0} did not get around the pace car in {1:0} s: it drops to the tail and its lap is credited at the green",
                CarNumber(freePassIdx), FreePassGiveUp));
            freePassIdx = -1;
        }

        // Waved around, not sent around on the lead lap: restarts at the tail of the field (NASCAR rule).
        bool WavedToTail(int idx)
        {
            Settle s;
            return eligible.ContainsKey(idx) && settle.TryGetValue(idx, out s) && s.Waved && !s.LeadLap;
        }

        // ---- one to go held for the pit stops ---------------------------------------------------------

        // NR2003 shows one to go when the pace car reaches a point on its lap, unless its "caution still needed"
        // check (0x4185F0) says otherwise; that check is true while the caution request byte (+0x12B, Addr.Yellow)
        // is set, and the game then runs another caution lap (0x4193A8). The byte is cleared on every flag change.
        // Seen live: pit road opened late and one to go came 35 s later with most of the field still on pit road,
        // the lineup and the cone ran without them and the field restarted in two packs. Race Control holds the
        // byte while the pit stops are going on (until the lineup after the pit stops), at most HoldMaxLaps extra,
        // counted from when pit road opens: counted from flag 2, a lap spent waiting to open it ran the hold out
        // late in a Bristol race with the pit stops and the lapped cars' sorting still going on.
        // Diagnostics: every flag change under caution with the pace car's lap count, and a note when the game itself
        // (not Race Control's hold) keeps the caution going another lap - to see where a long caution's laps go.
        int timelineFlag = 7; bool gameHoldLogged;
        void CautionTimeline()
        {
            if (flag != timelineFlag)
            {
                if (flag != 7 || timelineFlag != 7)
                    Log(string.Format("caution timeline: flag {0} -> {1} (pace car lap {2}, caution byte {3}{4})", timelineFlag, flag,
                        m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd), m.U8(Addr.Yellow), holdingOneToGo ? ", Race Control holding one to go" : ""));
                timelineFlag = flag;
                gameHoldLogged = false;
            }
            if (flag >= 2 && flag <= 4 && !holdingOneToGo && m.U8(Addr.Yellow) != 0 && !gameHoldLogged)
            {
                gameHoldLogged = true;
                Log(string.Format("caution timeline: the game's own caution request is set at flag {0} (pace car lap {1}): it runs another caution lap",
                    flag, m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd)));
            }
        }

        public bool HoldOneToGoForPits = true;
        const int HoldMaxLaps = 3;
        bool holdingOneToGo, holdLogged, holdGaveUp;
        int holdStartPaceLap = -1;
        // The lapped cars' part (Talladega, 2026-10-27): dropping 9 cars back took ~100 s and cost a lap nearly every
        // caution, and a car leaving pit road late started the drop again for a second lap. At most one extra lap for
        // it, and once one to go has been let go after the pit stops it is not held again for them (the cone still
        // waits for lapped cars in the way).
        const int LappedHoldMaxLaps = 1;
        bool holdReleasedOnce;
        int lappedHoldStartPaceLap = -1;

        void HoldOneToGoTick()
        {
            // also until the lapped cars on the low line are behind the lead lap cars: at one to go the game puts
            // every car in its own lineup mode, so a lapped car still dropping back then stays in the way (the cone
            // waited for them, or opened at double file with them still there)
            bool pitsDone = PitCycleDone(), dropping = pitsDone && !holdReleasedOnce && LappedStillDropping();
            bool want = flag >= 2 && flag <= 4 && !holdGaveUp && ((HoldOneToGoForPits && !pitsDone) || dropping); // the lapped cars' part goes with the low line option
            if (want && dropping && !holdForLappedLogged)
            {
                holdForLappedLogged = true;
                Log("one to go held until the lapped cars have dropped behind the lead lap cars");
            }
            int paceLap = m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd);
            if (want && dropping && !(HoldOneToGoForPits && !pitsDone))
            {
                if (lappedHoldStartPaceLap < 0) lappedHoldStartPaceLap = paceLap;
                else if (paceLap - lappedHoldStartPaceLap >= LappedHoldMaxLaps)
                {
                    holdReleasedOnce = true;
                    want = false;
                    Log(string.Format("one to go no longer held for the lapped cars: {0} extra caution lap for them already", LappedHoldMaxLaps));
                }
            }
            if (want)
            {
                if (holdStartPaceLap < 0 && pitsOpen) holdStartPaceLap = paceLap;
                if (holdStartPaceLap >= 0 && paceLap - holdStartPaceLap >= HoldMaxLaps)
                {
                    holdGaveUp = true;
                    want = false;
                    Log(string.Format("one to go no longer held: {0} extra caution laps for the pit stops already", HoldMaxLaps));
                }
            }
            if (want)
            {
                if (m.U8(Addr.Yellow) == 0) m.W8(Addr.Yellow, 1);
                if (!holdLogged) Log("one to go held until the pit stops are done and the field is lined up");
                holdLogged = true;
                holdingOneToGo = true;
            }
            else if (holdingOneToGo)
            {
                holdingOneToGo = false;
                if (pitsDone) holdReleasedOnce = true;
                if (m.U8(Addr.Yellow) != 0 && flag != 7) m.W8(Addr.Yellow, 0);
                if (!holdGaveUp) Log(string.Format("one to go released: pit stops done{1} ({0} extra caution lap(s))", holdStartPaceLap < 0 ? 0 : Math.Max(0, paceLap - holdStartPaceLap),
                    holdForLappedLogged ? " and the lapped cars behind the lead lap cars" : ""));
            }
        }

        // The pit stops are done: the lineup after them has been made (or, without the early lineup, every car
        // has had its first time by and nobody has been on pit road for 3 s).
        bool PitCycleDone()
        {
            if (earlyLineupDone) return true;
            if (!pitsOpen) return false;
            bool anyPitting = Enumerable.Range(0, maxIdx + 1).Any(i => i != Addr.PaceCarIdx && Running(i) && OnPitRoad(i));
            if (anyPitting) { cycleQuietSince = double.NaN; return false; }
            if (double.IsNaN(cycleQuietSince)) cycleQuietSince = Now;
            bool allDecided = Enumerable.Range(0, maxIdx + 1).All(i => i == Addr.PaceCarIdx || !Running(i) || decided.ContainsKey(i));
            return Now - cycleQuietSince >= 3 && (allDecided || (pitOpenLap >= 0 && lap >= pitOpenLap + 2));
        }

        double cycleQuietSince = double.NaN;

        // Lead lap cars caught between the pace car and the leader when the caution came out (they had just
        // crossed the line when the pace car pulled out in front of them; the leader was past pit exit) are sent
        // around the pace car as soon as the leader is coming up to it, before pit road opens, and pit road waits
        // until they have caught the field (NotPickedUp). Sent at their first time by instead, the leaders had
        // already pitted and they pitted a lap later, alone, and restarted half a lap behind.
        HashSet<int> sentEarly = new HashSet<int>();
        HashSet<int> pitRoadAtCaution = new HashSet<int>();  // on pit road (a green flag stop) when the caution came out
        bool pitRoadAtCautionTaken;

        // Talladega 2026-10-27 (lap 120): the yellow came just before the line, the pace car left pit road at once and
        // joined the track BEHIND the two leaders, in front of everyone else. Read as "the whole field caught between
        // the pace car and the leader", 17 lead lap cars were sent around, passed the leaders, and a second wave
        // followed (the leaders included) once the sent cars counted as the leader. Cars are only caught while the
        // leader is behind the pace car; with the leader in front of it the game slows the leaders until the pace car
        // gets by, and nobody is sent around.
        bool paceBehindLeaderLogged;

        void SendAroundEarly()
        {
            Car leader, first;
            List<Car> cars = Field(out leader, out first);
            if (cars == null) return;
            double trackLen = m.F64(Addr.TrackLength);
            if (leader != null && leader.Gap > trackLen / 2 && !pitsOpen)
            {
                if (!paceBehindLeaderLogged)
                {
                    paceBehindLeaderLogged = true;
                    Log(string.Format("the pace car is behind the leader #{0} ({1:0} m): nobody is sent around until it is in front of the leader",
                        leader.Num, trackLen - leader.Gap));
                }
                return;
            }
            // Talladega 2026-11-06: caught cars sent before the pace car had picked the field up dropped back
            // (7 of 11); sent after the pickup, all of them went around (5 of 5). Wait for the pickup.
            if (!PaceCarPickedUp()) return;
            // after pit road opened (on the leader's second time by), only the cars caught in front when it opened
            foreach (Car c in cars.Where(c => c.Stuck && !eligible.ContainsKey(c.Idx) && !decided.ContainsKey(c.Idx) &&
                (!pitsOpen || (stuckAtOpen.Contains(c.Idx) && !pitted.Contains(c.Idx)))).ToList())
            {
                decided[c.Idx] = "around";
                sentEarly.Add(c.Idx);
                WaveCar(c, cars, true);
                Log(string.Format("#{0} is on the lead lap ({1} laps behind the leader) but in front of #{2}, caught behind the pace car: sent around it now to catch the back of the field, pit road waits for it",
                    c.Num, F2(c.Down), first.Num));
            }
        }

        // A lapped car that stays out is waved around once the pit stops are done, the way NASCAR does it: it
        // stays in its spot until the cars that pitted are back in line behind the pace car, then goes around the
        // pace car and catches the tail of the field. Waved at its first time by, it drove a lap at speed while the
        // field was on pit road, caught the pace car from behind before the leaders were back on track, and sat in
        // front of them with the whole field told to line up ahead of it.
        public bool WaveEarly = true;           // release the wave around with the last few cars still on pit road
        Dictionary<int, double> waveLater = new Dictionary<int, double>(); // car -> laps down at its first time by
        double pitsQuietSince = double.NaN;

        // 3.3.3 (run J, the user 2026-11-09): wave arounds go at one to go only, like the plan said: released when the
        // pit stops were done, they went around with the whole field still lined up in front of them. Each is checked
        // again at that moment (WaveStillOk). Off: the 3.3.2 timing (pit stops done / early / at the line).
        public bool WavesAtOneToGo = true;

        void ReleaseWavesTick()
        {
            if (waveLater.Count == 0) return;
            if (WavesAtOneToGo)
            {
                if (flag < 5 || flag > 6) return;
                List<Car> otg = Field();
                foreach (var kv in waveLater.ToList()) ReleaseWave(kv.Key, kv.Value, "one to go", otg);
                return;
            }
            int onPit = Enumerable.Range(0, maxIdx + 1).Count(i => i != Addr.PaceCarIdx && Running(i) && OnPitRoad(i));
            bool anyPitting = onPit > 0;
            if (anyPitting) pitsQuietSince = double.NaN;
            else if (double.IsNaN(pitsQuietSince)) pitsQuietSince = Now;
            bool allDecided = Enumerable.Range(0, maxIdx + 1).All(i => i == Addr.PaceCarIdx || !Running(i) || decided.ContainsKey(i));
            bool done = !anyPitting && Now - pitsQuietSince >= 3 && (allDecided || (pitOpenLap >= 0 && lap >= pitOpenLap + 2));
            // Released with the last stops still going (WaveEarly): a long queue takes about 0.6 s a car to get by the pace
            // car (Talladega: 25 cars in 15 s), and the cars at its tail were still queued when the leader came back
            // out of the pits and went by them - they "dropped back" and were credited the lap. The queue goes while
            // the last few cars are still on pit road; how many is judged from the queue's length and ~4 s a car
            // for pit road to empty. The pit stops must all be decided, so nobody is waved who could still pit.
            // WaveAtLine (tracks of 1.99 miles and up): each queued wave car goes the moment it crosses the start/finish
            // line, once pit road is open and it is not on pit road itself - not when the stops are done or at one to go.
            // Talladega 2026-11-08: 5 cars queued 3.4-4.4 laps down were released at one to go and the field split.
            double lineTrackLen = m.F64(Addr.TrackLength);
            if (WaveAtLine && lineTrackLen >= WaveAtLineMinLen && pitsOpen && flag >= 2 && flag < 5)
            {
                List<Car> lineCars = null;
                foreach (var kv in waveLater.ToList())
                {
                    int widx = kv.Key;
                    float wd = DlongOf(widx), wlast;
                    bool had = waveLineD.TryGetValue(widx, out wlast);
                    waveLineD[widx] = wd;
                    if (!had || OnPitRoad(widx) || !Running(widx)) continue;
                    bool wrapped = wlast > lineTrackLen * 0.75 && wd < lineTrackLen * 0.25 && lineTrackLen - wlast + wd < lineTrackLen * 0.1;
                    if (!wrapped) continue;
                    if (lineCars == null) lineCars = Field();
                    ReleaseWave(widx, kv.Value, "crossed the start/finish line", lineCars);
                }
                if (waveLater.Count == 0) return;
            }
            bool early = false;
            if (!done && WaveEarly && allDecided && onPit > 0 && flag >= 2 && flag < 5)
            {
                double lead = 0.6 * waveLater.Count + 3;
                int allow = Math.Max(1, (int)(lead / 4));
                early = onPit <= allow;
            }
            if (!done && !early && flag < 5) return;
            string when = done ? "the pit stops are done" : early
                ? string.Format("early: {0} waved, {1} car(s) still on pit road", waveLater.Count, onPit) : "one to go";
            List<Car> cars = Field();
            foreach (var kv in waveLater.ToList()) ReleaseWave(kv.Key, kv.Value, when, cars);
        }

        // Tracks of this length and up (1.99 miles) release each wave car at the line, and keep a wave car off pit road.
        public bool WaveAtLine = true;
        const double WaveAtLineMinLen = 3203;
        Dictionary<int, float> waveLineD = new Dictionary<int, float>();

        // At one to go: still a lapped car that stayed out, on track, with no penalty, and still in front of the lead lap
        // cars (between the pace car and the first lead lap car). Null = ok, else why not.
        string WaveStillOk(Car c, List<Car> cars)
        {
            if (OnPitRoad(c.Idx)) return "on pit road";
            if (HasPenalty(c.Idx)) return "under penalty";
            if (c.Down < 1) return "no longer a lap down";
            if (!c.Ahead) return "not in front of the lead lap cars any more";
            return null;
        }

        void ReleaseWave(int idx, double down, string when, List<Car> cars)
        {
            waveLater.Remove(idx);
            waveLineD.Remove(idx);
            Car c = cars == null ? null : cars.FirstOrDefault(o => o.Idx == idx);
            if (c == null || pitted.Contains(idx))
            {
                decided[idx] = "pitted";
                Log("#" + CarNumber(idx) + " pitted (or is out) before its wave around: not waved");
                return;
            }
            string no = WavesAtOneToGo ? WaveStillOk(c, cars) : null;
            if (no != null)
            {
                decided[idx] = "behind";
                Log(string.Format("wave around: #{0} stays in line at one to go: {1}", c.Num, no));
                return;
            }
            WaveCar(c, cars, false, down);
            Log(string.Format("wave around: #{0} goes around the pace car now ({1}), {2} laps down at its first time by, and restarts at the tail of the field", c.Num, when, F2(down)));
        }

        // Each car is decided at its first time by: just after it passes pit entry with the pits open. If it stayed
        // on track, is ahead of every lead lap car still on track and has no penalty, it is waved around. "Ahead"
        // is measured behind the pace car, so nothing is decided before flag 2.
        void FirstPassTick()
        {
            bool pending = false;
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || decided.ContainsKey(idx)) continue;
                if (pitted.Contains(idx) && pitsOpen) decided[idx] = "pitted";
                else if (readyToDecide.Contains(idx)) pending = true;
            }
            if (!pending || flag < 2) return;
            Car leader, first;
            List<Car> cars = Field(out leader, out first);
            if (cars == null) return;
            // 3.3.0: with the pace car still behind the leader (sent out behind it, catching it) "ahead" can't be told yet
            if (PaceFixes && leader != null && leader.Gap > m.F64(Addr.TrackLength) / 2 && !pitsOpen) return;
            if (leaderIdx < 0) leaderIdx = leader.Idx;
            foreach (Car c in cars)
            {
                if (decided.ContainsKey(c.Idx) || !readyToDecide.Contains(c.Idx) || pitted.Contains(c.Idx)) continue;
                if (!c.Ahead)
                    decided[c.Idx] = c.Down < 1 ? "lead" : "behind";
                else if (c.Stuck)
                {
                    // on the lead lap, just behind on track (a green flag stop): it goes around the pace car to its
                    // place among the lead lap cars like a waved car, but it gains nothing
                    decided[c.Idx] = "around";
                    WaveCar(c, cars, true);
                    Log(string.Format("#{0} is on the lead lap ({1} laps behind the leader) but in front of #{2}: around the pace car to its place on the lead lap, no lap gained",
                        c.Num, F2(c.Down), first.Num));
                }
                else if (HasPenalty(c.Idx))
                {
                    decided[c.Idx] = "penalty";
                    Log("#" + c.Num + " stayed out ahead of the first lead lap car but is under penalty, not waved");
                }
                else
                {
                    decided[c.Idx] = "waved";
                    waveLater[c.Idx] = c.Down;
                    Log(string.Format("wave around: #{0} stayed out in front of every lead lap car on track (first: #{1}), {2} laps behind the leader -> keeps its spot until the pit stops are done, then around the pace car to the tail",
                        c.Num, first.Num, F2(c.Down)));
                }
            }
        }

        // A waved car gets one pace lap less so it may pass the pace car. Its pace lap must stay like that until it
        // has crossed the start/finish line (that crossing is the lap it gains): given back as soon as it was past
        // the pace car, the car was seen waiting half a lap later and letting the whole field by without ever
        // crossing the line. Once it has crossed, its pace lap is set to the pace car's, so the game lines it up
        // in its spot behind the lead lap cars instead of leaving it cruising ahead of the field.
        void FollowWavedCars()
        {
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            float pc = DlongOf(Addr.PaceCarIdx);
            foreach (var kv in eligible)
            {
                int idx = kv.Key;
                Eligible e = kv.Value;
                if (e.Restored || e.Pitted || OnPitRoad(idx)) continue;
                double gap = Mod(pc - DlongOf(idx), trackLen); // metres behind the pace car
                if (!e.Passed && e.LastGap >= 0 && e.LastGap < trackLen * 0.25 && gap > trackLen * 0.75)
                {
                    e.Passed = true;
                    e.LapsAtPass = LapsOf(idx);
                    Log("waved #" + CarNumber(idx) + " passed the pace car");
                }
                e.LastGap = gap;
                if (!e.Passed || LapsOf(idx) <= e.LapsAtPass) continue;
                e.Restored = true;
                e.RestoredAt = Now;
                if (!RestorePaceLap)
                {
                    Log("waved #" + CarNumber(idx) + " crossed the line after passing the pace car (lap gained on track)");
                    continue;
                }
                long a = Addr.PaceLap + idx * Addr.Ocd;
                int mine = m.I32(a), pace = m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd);
                if (mine < pace) m.W32(a, pace);
                Log(string.Format("waved #{0} crossed the line after passing the pace car (lap gained on track); pace lap {1} -> {2} so it lines up in the field",
                    CarNumber(idx), mine, Math.Max(mine, pace)));
            }
        }

        // The lap a wave around is worth, credited once the car is around (2 s after it crossed the line past the
        // pace car, so the scoring has caught up with the crossing), not at the green: a car the leader had lapped
        // once and was about to lap again goes around to the tail still one lap down on track, so the standings
        // showed waved cars a lap down for the whole caution. The check after the green stays as the fallback.
        void CreditWavesTick()
        {
            List<Car> cars = null;
            Car leader = null, first;
            foreach (var kv in eligible)
            {
                Eligible e = kv.Value;
                Settle s;
                if (!e.Restored || double.IsNaN(e.RestoredAt) || Now - e.RestoredAt < 2) continue;
                e.RestoredAt = double.NaN;
                if (!settle.TryGetValue(kv.Key, out s) || s.Credited || e.Pitted || OnPitRoad(kv.Key)) continue;
                if (cars == null) cars = Field(out leader, out first);
                if (cars == null) return;
                Car c = cars.FirstOrDefault(o => o.Idx == kv.Key);
                if (c == null) continue;
                int down = (int)Math.Floor(leader.Prog - c.Prog + 0.001);
                if (down <= s.Down) continue;
                if (CreditLap(kv.Key, leader.Idx))
                {
                    s.Credited = true;
                    Log(string.Format("#{0} is around: lap credited now ({1} down on track, {2} down after the {3})",
                        CarNumber(kv.Key), down, (int)s.Down, s.FreePass ? "free pass" : s.LeadLap ? "send-around" : "wave around"));
                }
            }
        }

        // Waved (or sent around) and not yet past the pace car.
        bool GoingAround(int idx)
        {
            Eligible e;
            return eligible.TryGetValue(idx, out e) && !e.Passed && !e.Pitted && !e.Restored;
        }

        HashSet<int> keptBack = new HashSet<int>();     // waved cars held at the end of the line until around
        HashSet<int> pitMoved = new HashSet<int>();     // cars taken out of the line's order while on pit road
        double lastTidy;

        // Keeps the line one the cars can follow while the caution runs (single file, before double file):
        //  - a car on pit road goes to the end of the line: a car on track lined up behind it would pace behind it,
        //    crawling at pit road speed until it comes out. Back on track it goes to the end of the lead lap cars
        //    (lead lap) or the end of the line (lapped).
        //  - a waved car that has not passed the pace car yet is at the end of the line, like NR2003's own wave-by:
        //    with no car behind it on track to follow it drives around the pace car to the tail. Lined up anywhere
        //    else it follows the car ahead of it in the line, and if that car is ahead of it on track (out of the
        //    pits) it never gets around; lined up at the front it sits behind the pace car. Once past the pace car
        //    it goes to its spot at the end of the lead lap cars.
        void TidyLine()
        {
            if (Now - lastTidy < 0.5) return;
            lastTidy = Now;
            if (inside != null) return;
            // classic rules: the game keeps its own line; only the Lucky Dog going around is placed
            if (Classic && keptBack.Count == 0 && !eligible.Keys.Any(GoingAround)) return;
            bool plan = planActive && lineup != null;
            if (!plan && m.U8(Addr.NumPacingLines) != 1) return;
            List<Car> cars = Field();
            if (cars == null) return;
            List<int> order = plan ? new List<int>(lineup) : CurrentLine();
            if (order.Count == 0) return;
            var track = cars.ToDictionary(c => c.Idx);
            var going = cars.Where(c => GoingAround(c.Idx) && order.Contains(c.Idx)).OrderBy(c => c.Gap).Select(c => c.Idx).ToList();
            var pits = Classic ? new List<int>() : order.Where(i => Running(i) && OnPitRoad(i)).ToList();
            var rest = order.Where(i => !going.Contains(i) && !pits.Contains(i)).ToList();
            // a lapped car waiting for its wave around is ahead on track of the cars coming off pit road
            var lapped = new HashSet<int>(cars.Where(o => o.Down >= 1 && !eligible.ContainsKey(o.Idx) && !waveLater.ContainsKey(o.Idx)).Select(o => o.Idx));
            Action<int, string> place = (idx, why) =>
            {
                rest.Remove(idx);
                Car c;
                // a waved car goes to the tail; a lead lap car sent around comes up behind the field too, and only
                // takes its spot among the lead lap cars once the lineup after the pit stops has been made
                bool lead = !WavedToTail(idx) && (plan || !eligible.ContainsKey(idx)) && (eligible.ContainsKey(idx) || (track.TryGetValue(idx, out c) && c.Down < 1));
                int at = lead ? rest.FindIndex(lapped.Contains) : -1;
                rest.Insert(at < 0 ? rest.Count : at, idx);
                Log("#" + CarNumber(idx) + " " + why + ": to the end of the " + (lead ? "lead lap cars" : "line"));
            };
            foreach (int idx in keptBack.Where(i => !GoingAround(i) && !pits.Contains(i)).ToList())
            {
                keptBack.Remove(idx);
                if (rest.Contains(idx)) place(idx, eligible[idx].Pitted ? "(sent around) pitted and is back on track" : eligible[idx].Passed ? "(sent around) is past the pace car" : "(sent around) stays at the back");
            }
            foreach (int idx in pitMoved.Where(i => !pits.Contains(i) && !going.Contains(i)).ToList())
            {
                pitMoved.Remove(idx);
                if (rest.Contains(idx)) place(idx, "is back on track from pit road");
            }
            foreach (int idx in going)
                if (keptBack.Add(idx)) Log("waved #" + CarNumber(idx) + " goes to the end of the line until it has passed the pace car");
            foreach (int idx in pits)
                if (pitMoved.Add(idx) && rest.Concat(going).Any(o => order.IndexOf(o) > order.IndexOf(idx)))
                    Log("#" + CarNumber(idx) + " is on pit road: to the end of the line, so cars behind it on track pace on");
            List<int> want = rest.Concat(going).Concat(pits).ToList();
            if (want.SequenceEqual(order)) return;
            if (plan) { lineup = want; WritePlan(); } else WriteLine(want);
        }

        // The single file line as the game has it now.
        List<int> CurrentLine()
        {
            var order = new List<int>();
            for (int i = 0; i < 48; i++)
            {
                int idx = m.I32(Addr.PacingOutside1st + i * 8);
                if (idx >= 0 && idx <= maxIdx && idx != Addr.PaceCarIdx && !order.Contains(idx)) order.Add(idx);
            }
            return order;
        }

        string FirstTimeBySummary()
        {
            Func<string, string> group = d =>
            {
                string s = string.Concat(decided.Where(kv => kv.Value == d).OrderBy(kv => kv.Key).Select(kv => " #" + CarNumber(kv.Key)));
                return s != "" ? s : " none";
            };
            return "first time by summary - free pass:" + group("free pass") + "; waved:" + group("waved") + "; lead lap, sent around to their place:" + group("around") +
                "; under penalty:" + group("penalty") +
                "; pitted:" + group("pitted") + "; lapped, stayed out behind the first lead lap car:" + group("behind");
        }

        // A few seconds after the green, settle the laps of every car that was moved. A waved car must end up one
        // lap better than at the first time by; driving around the pace car usually earns that on track, otherwise
        // the lap is credited here. Returns true when done.
        bool SettleLaps()
        {
            double now = Now;
            if (double.IsNaN(greenTime)) greenTime = now;
            if (now - greenTime < 3) return false;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100 || leaderIdx < 0)
            {
                Log("green: could not settle laps (track length or leader unknown)");
                return true;
            }
            // lap counters and track position roll over on different frames: wait while a car is at the line
            if (now - greenTime < 10)
            {
                Func<int, bool> atLine = idx => DlongOf(idx) < 40 || DlongOf(idx) > trackLen - 40;
                if (atLine(leaderIdx) || settle.Keys.Any(atLine)) return false;
            }
            Func<int, double> progress = idx => LapsOf(idx) + DlongOf(idx) / trackLen;
            double lead = progress(leaderIdx);
            foreach (var kv in settle.OrderBy(kv => kv.Key))
            {
                int idx = kv.Key;
                Settle s = kv.Value;
                int down = (int)Math.Floor(lead - progress(idx) + 0.001);
                string msg = string.Format("#{0} {1}: {2} behind at the first time by, now {3} down (scored {4}), should be {5} down",
                    CarNumber(idx), s.FreePass ? "free pass" : s.LeadLap ? "sent around (lead lap)" : s.Waved ? "waved" : "not waved", F2(s.Before), down, ScoredLaps(leaderIdx) - ScoredLaps(idx), (int)s.Down);
                Eligible e;
                // a lapped car waved and then pitted gave its wave up; a lead lap car sent around (or the free pass)
                // keeps its lap whatever it did after
                if (s.Waved && !s.FreePass && !s.LeadLap && eligible.TryGetValue(idx, out e) && e.Pitted)
                    Log("green: " + msg + " -> pitted after the wave, left as it is");
                else if (s.Waved && s.Credited)
                    Log("green: " + msg + " -> its lap was credited during the caution (the count on track follows at its next crossing)");
                else if (down > s.Down && s.Waved)
                {
                    // a wave around is worth exactly one lap; anything more is a reading problem, not a lap owed
                    bool ok = CreditLap(idx, leaderIdx);
                    Log("green: " + msg + " -> " + (ok ? "lap credited" : "lap credit FAILED") +
                        (down - s.Down > 1 ? " (still more laps short than expected - not credited)" : ""));
                }
                else if (down > s.Down)
                    Log("green: " + msg + " -> lost more laps than expected on its own (pit stop or trouble), left as it is");
                else if (s.Waved && down == s.Down && lead - progress(idx) - down > 0.5)
                    Log("green: " + msg + string.Format(" -> on the right lap but {0} laps behind the leader on track: it never caught the field", F2(lead - progress(idx) - down)));
                else if (down < s.Down && lead - progress(idx) > s.Down - 0.5)
                    Log("green: " + msg + " -> ok (just ahead of the leader on track at the restart)");
                else if (down < s.Down)
                    Log("green: " + msg + " -> GAINED A LAP ON TRACK" + (s.Waved ? " beyond the wave" : " without a wave around"));
                else
                    Log("green: " + msg + " -> ok");
            }
            return true;
        }

        void ResetWave()
        {
            pitted.Clear(); settle.Clear(); excused.Clear(); eligible.Clear(); closedLapOffenders.Clear(); decided.Clear(); keptBack.Clear(); pitMoved.Clear();
            sentEarly.Clear(); waveLater.Clear(); waveLineD.Clear(); pitsQuietSince = double.NaN;
            pitRoadAtCaution.Clear(); pitRoadAtCautionTaken = false;
            if (holdingOneToGo && m.U8(Addr.Yellow) != 0 && flag != 7) m.W8(Addr.Yellow, 0);
            holdingOneToGo = holdLogged = holdGaveUp = holdForLappedLogged = holdReleasedOnce = paceBehindLeaderLogged = false; holdStartPaceLap = lappedHoldStartPaceLap = -1; cycleQuietSince = double.NaN;
            freePassIdx = -1; freePassDone = freePassPending = false; freePassWaitSince = double.NaN; freePassWaitLogged = false; requestsLogged.Clear(); stopCalledOff.Clear();
            downAtYellow.Clear(); downAtYellowTaken = false;
            ResetPits();
            leaderIdx = -1;
            pitOpenLap = -1;
            pitsOpened = firstPassStarted = false;
            greenTime = double.NaN;
        }

        bool CautionHappened { get { return pitsOpen || closedLogged || pitsOpened || firstPassStarted || pitted.Count > 0 || excused.Count > 0 || waveLater.Count > 0; } }

        void WaveTick()
        {
            LearnPitEntry();
            WatchPenalties();
            WatchPenaltyStopFlag();
            WatchWavedCrossings();
            ForgiveTick();
            ScoringCheckTick();
            if (flag == 7)
            {
                pitRoadAtCautionTaken = false;
                if (Classic && downAtYellowTaken && scoringSnap == null && double.IsNaN(greenTime))
                {
                    scoringSnap = new Dictionary<int, double>(downAtYellow);
                    scoringDogs = new HashSet<int>(settle.Where(kv => kv.Value.FreePass).Select(kv => kv.Key));
                    scoringSnapAt = Now;
                    downAtYellowTaken = false;   // taken once per caution (cleared again with the rest)
                }
                ReleaseClosedBit();
                Excuse();
                if (settle.Count > 0 && !SettleLaps()) return;
                if (CautionHappened || LineupHappened) { ResetWave(); ResetLineup(); }
                return;
            }
            if (!double.IsNaN(greenTime)) // a new caution before the laps were settled
            {
                Log("new caution before the wave around laps were settled; settling skipped");
                watched.Clear();
                ResetWave();
                ResetLineup();
            }
            Excuse();
            WatchPitRequests();
            if (Classic) TakeDownAtYellow();
            if (!pitRoadAtCautionTaken)
            {
                // cars finishing a green flag stop when the caution came out have not pitted under it: they keep
                // their wave around and free pass chances (seen live: one was passed over for both)
                pitRoadAtCautionTaken = true;
                pitRoadAtCaution.Clear();
                for (int idx = 0; idx <= maxIdx; idx++)
                    if (idx != Addr.PaceCarIdx && Running(idx) && OnPitRoad(idx) && pitRoadAtCaution.Add(idx))
                        Log("#" + CarNumber(idx) + " was on pit road when the caution came out (a green flag stop): not counted as pitting under the caution");
            }
            if (flag <= 4)
            {
                for (int idx = 0; idx <= maxIdx; idx++)
                {
                    if (idx == Addr.PaceCarIdx) continue;
                    if (!OnPitRoad(idx)) { pitRoadAtCaution.Remove(idx); continue; }
                    if (pitRoadAtCaution.Contains(idx) && !pitsOpen) continue; // still on its green flag stop
                    pitted.Add(idx);
                    Eligible e;
                    if (eligible.TryGetValue(idx, out e) && !e.Pitted)
                    {
                        e.Pitted = true;
                        Log("waved #" + CarNumber(idx) + " is pitting");
                    }
                }
            }
            // Pit road opens at the game's time (flag 1, the leader has taken the yellow) or, with PitsClosedFirstLap,
            // just before the leader's second time by; OpenPitsToAll only changes who may use it. Opening at flag 0
            // made cars give up racing back to the line to pit.
            bool wasOpen = pitsOpen;
            if (WaveAroundOn && PitsClosedFirstLap && (!pitsOpen || stuckAtOpen.Count > 0) && flag >= 2 && flag <= 4) SendAroundEarly();
            FreePassTick();
            bool open = flag <= 6 && PitsOpenTick();
            if (open && !wasOpen)
                for (int idx = 0; idx <= maxIdx; idx++)
                    if (m.I32(Addr.ClosedPitPenalty + idx * 4) != 0) closedLapOffenders.Add(idx);
            if (open && OpenPitsToAll && flag >= 1 && flag <= 4) OpenPits();
            PerCarGateTick();
            if (ClearClosedPitPenalty && open && OpenPitsToAll && flag >= 1 && flag <= 4) ClearClosedPitPenalties();
            if (open && !firstPassStarted)
            {
                firstPassStarted = true; // pit road has opened: every car's next pass of pit entry is its first time by
                watched.Clear();
                pitOpenLap = lap;
                StartEntryWatch();
            }
            if (firstPassStarted && flag <= 4) EntryWatchTick();
            if (WaveAroundOn && firstPassStarted && flag >= 1 && flag <= 4) FirstPassTick();
            if (flag >= 1 && flag <= 6) ReleaseWavesTick();
            if (flag >= 1 && flag <= 5) DroppedBackTick();
            HoldOneToGoTick();
            if (flag >= 1 && flag <= 6) FollowWavedCars();
            if (flag >= 1 && flag <= 6) CreditWavesTick();
            if (flag >= 1 && flag <= 5) TidyLine();
        }
    }
}
