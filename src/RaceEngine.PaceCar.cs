using System;
using System.Collections.Generic;
using System.Linq;

namespace RaceControl
{
    public partial class RaceEngine
    {
        // ---- the pace car picks up the leader ----------------------------------------------------------
        // At a caution the pace car sits on pit road in the game's PaceCarWaitingForLeader state, and leaves when
        // the leader's time to reach it (worked out from the leader's speed at that moment) drops below a lead time
        // the state set on entry (state object +0x14). With the field frozen every car slows right away, so that
        // time is far too short: the pace car came out early, in front of cars that were more than half a lap
        // behind the leader (17 lead lap cars at Bristol), and all of them had to be sent around it.
        // While the field is frozen Race Control holds the pace car (the lead time set very low: the state then
        // never decides to go) and lets it go itself when the leader is PaceReleaseAt metres from the pace car's
        // spot on pit road. A time at the leader's speed did not work either (Toledo, 2.8.8): the pace car needs
        // about 16 s down pit road at the pit speed limit, and in that time the leader covers about the same
        // distance whatever it was doing at the yellow, as everyone slows to caution speed. The distance is
        // learned per track: after each release it is moved by how far in front of or behind the leader the
        // pace car came out. Only the game's own data is written, no code; the lead time is put back after.
        public bool PaceCarWaits = true;
        public double PaceReleaseAt = double.NaN;        // metres, per track (saved by the form)
        public Action<double> PaceReleaseLearned;
        const double PaceAheadWanted = 60;               // the pace car this far in front of the leader
        // Talladega (2026-10-27): with the field frozen the leader slows to a crawl well before the release point and
        // stacks the field up on the front stretch (3-15 m/s when it went at 160-280 m; once it never got there and the
        // field sat for the 3 minute limit). A leader this slow is waiting for the pace car: it goes then, wherever
        // the leader is - no caution pace is this slow, so it never cuts in early at a short track. Only near it, though:
        // waiting, the leader slowed 420-690 m out (new21 live); slowed 2120 m out it was a wreck in front of it, the
        // pace car went with nobody near and picked the leader up a lap later.
        const double PaceCrawlSpeed = 12;                // m/s
        const double PaceCrawlWithin = 900;              // m
        const double PaceHoldMax = 60;                   // s
        // The leader needs most of a lap to come round to the pace car: at Talladega 2026-11-08 the 60 s limit sent the
        // pace car with the leader still 1228 m away and the pick-up then failed 4 laps running (3958 m start: 85 s).
        // 3.2.4: a lap at 24 m/s (Talladega 178 s). trackLen / 35 (122 s) ran out twice on 2026-11-08 (laps 28 and 43)
        // with the leader that had just missed the pace car still coming round; 21 and 35 cars were sent around.
        // 3.2.9: while the pace car is held the field crawls at 0.6 of the pace speed (traces 2026-11-08: 12.3 m/s at North
        // Wilkesboro's 45 mph), so a leader that just missed it needs a lap at that: 60 s ran out twice in run F (laps 38,
        // 71). A lap at the crawl and a fifth more (North Wilkesboro 101 s, Talladega 272 s).
        double PaceHoldLimit(double trackLen)
        {
            double limit = Math.Max(PaceHoldMax, trackLen / 24.0);
            float mph = PaceFixes && PaceSpeedOk() ? TrackPaceSpeed() : float.NaN;
            if (!float.IsNaN(mph) && mph > 10) limit = Math.Max(limit, 1.2 * trackLen / (0.6 * mph * 0.44704));
            return limit;
        }
        // The car the pace car is held for: the leader when the hold starts, kept until it goes to pit road or stops
        // running. A wreck towed to its stall jumps ahead of the real leader (Talladega 2026-11-08 lap 20: #66 towed
        // from 735 m to its stall at 3854 m, read as the leader at 0 m/s, and the pace car went for it).
        int paceTarget = -1;
        double paceTargetStopped = double.NaN;
        // the car's speed from its velocity in the car block (+6634/+6638, floats m/s; what the caution recorder reads)
        double CarSpeed(int idx)
        {
            long a = Addr.Laps - 1360 + 6634 + idx * Addr.Ocd;
            float vx = m.F32(a), vy = m.F32(a + 4);
            double v = Math.Sqrt((double)vx * vx + (double)vy * vy);
            return double.IsNaN(v) || v > 150 ? double.NaN : v;
        }
        const double PaceSpeedWindow = 1.0;              // s: the leader's speed over at least this (one poll is 10-15 ms)
        bool paceCrawlRelease;
        double paceHoldLogAt = double.NaN;

        const long PaceStateWaiting = 0x760DA0;          // PaceCarWaitingForLeader {vtable, name*, id 0x1D}
        const long PaceStateLeadTime = PaceStateWaiting + 0x14;
        const long PaceStateWaitingName = 0x71EA64;      // "PaceCarWaitingForLeader", to check the build
        const long PaceStateCaution = 0x760D48;          // PaceCarCaution: on track
        const long PaceStateParked = 0x760E30;           // in its spot between cautions (traces 2026-11-08)
        const float HoldLeadTime = -1e6f;                // exact in a float, and far below any arrival time
        static long CarState(int idx) { return Addr.Laps - 1360 + 5031 + idx * Addr.Ocd; }  // pointer to the AI state

        bool? paceStatesOk;
        bool paceHolding, paceReleased, paceLogged;
        bool paceReleasedNow, paceWaitSeen, paceGameLeadFresh;   // 3.3.9: the lead time read when the waiting state starts
        float paceGameLead;
        double paceHoldStart = double.NaN, paceLastGap = double.NaN, paceLastTime = double.NaN, paceLeaderSpeed = double.NaN;
        int paceLeader = -1, paceLeaderAtRelease = -1;
        double paceReleaseGap, paceReleaseUsed, paceSentAt = double.NaN;

        static float AsFloat(int v) { return BitConverter.ToSingle(BitConverter.GetBytes(v), 0); }
        static int FloatBits(float f) { return BitConverter.ToInt32(BitConverter.GetBytes(f), 0); }

        bool PaceStatesOk()
        {
            if (paceStatesOk == null)
                paceStatesOk = m.U32(PaceStateWaiting + 4) == PaceStateWaitingName && m.I32(PaceStateWaiting + 8) == 0x1D;
            return paceStatesOk.Value;
        }

        void PaceCarTick()
        {
            if (!PaceStatesOk()) return;
            uint state = m.U32(CarState(Addr.PaceCarIdx));
            bool waiting = state == PaceStateWaiting;
            // 3.3.9: the game works out the lead time when the pace car ENTERS the waiting state (0x406820 writes +0x14
            // each caution, from the track and the pace speed). Read while the pace car was still parked, as before,
            // it was what the last caution left there: 2745 on 11-05, 0 on every 11-09 trace (Race Control had written
            // its stale 0 back), and a release that hands the game its own value made the pace car wait for a leader
            // 2 s away (run M lap 21: 29 s in the stall). So the value is taken on the first poll in the waiting state.
            // A release made while it was parked ("go now") was written over by that same entry: written again here.
            if (!waiting) paceWaitSeen = false;
            else if (!paceWaitSeen && PaceFixes)
            {
                paceWaitSeen = true;
                float v = m.F32(PaceStateLeadTime);
                if (v != HoldLeadTime && v != 1e6f && !float.IsNaN(v))
                {
                    if (!paceGameLeadFresh)
                        Log(string.Format("pace car waiting: the game's lead time for this caution is {0:0} (was read as {1:0} while parked)", v, paceGameLead));
                    paceGameLead = v;
                    paceGameLeadFresh = true;
                    if (paceReleased && paceReleasedNow) m.W32(PaceStateLeadTime, FloatBits(1e6f));
                }
            }
            if (!PaceCarWaits && PaceFixes && fieldFrozen && flag <= 1 && !paceReleased && (waiting || state == PaceStateParked))
                NoHoldLeaderJustPast(waiting);
            if (!waiting)
            {
                // 3.2.7: held already while it is still parked. The game's own lead time is far above any arrival time,
                // so the waiting state lets the pace car go on its first frame: under time acceleration (run E lap 23,
                // about x12) that frame came and went between two polls, the pace car was never held and 14 cars were
                // caught. The lead time set now is already there when the waiting state starts.
                if (state == PaceStateParked && !paceReleased && PaceFixes && PaceCarWaits && fieldFrozen && flag <= 1)
                {
                    if (!paceHolding)
                    {
                        if (!paceGameLeadFresh)
                        {
                            paceGameLead = m.F32(PaceStateLeadTime);   // a stand-in until the waiting state sets the real one
                            if (paceGameLead == HoldLeadTime || paceGameLead == 1e6f) paceGameLead = 0;
                        }
                        paceHolding = true;
                        paceHoldStart = Now;
                        paceHoldLogAt = double.NaN;
                    }
                    m.W32(PaceStateLeadTime, FloatBits(HoldLeadTime));
                    // 3.3.0: the leader can go by while the pace car is still parked (the first seconds of the yellow)
                    double tl = m.F64(Addr.TrackLength);
                    Car pl, pf;
                    List<Car> pcars = tl > 100 ? Field(out pl, out pf) : null;
                    if (pcars != null && pcars.Count > 0)
                    {
                        Car target = paceTarget >= 0 ? pcars.FirstOrDefault(c => c.Idx == paceTarget) : null;
                        if (target == null) { target = pcars[0]; paceTarget = target.Idx; }
                        double pg = Mod(DlongOf(Addr.PaceCarIdx) - DlongOf(target.Idx), tl);
                        if (target.Idx != paceLeader) { paceLeader = target.Idx; paceLastGap = double.NaN; paceLeaderSpeed = double.NaN; paceGapPrev = double.NaN; }
                        if (LeaderWentBy(target, pg, tl)) return;
                    }
                    return;
                }
                if (paceHolding || paceReleased) PaceCarOut(state);
                return;
            }
            if (paceReleased) return; // let go: the state makes its own exit
            bool want = PaceCarWaits && fieldFrozen && flag <= 2;
            if (!want) { if (paceHolding) ReleasePaceCar("the field is no longer frozen", false); return; }

            double trackLen = m.F64(Addr.TrackLength);
            Car fieldLeader = null, first;
            List<Car> cars = trackLen > 100 ? Field(out fieldLeader, out first) : null;
            if (cars == null || fieldLeader == null) return;
            // only a car on track (Field leaves pit road out of the list); the one picked first is kept
            Car leader = paceTarget >= 0 ? cars.FirstOrDefault(c => c.Idx == paceTarget) : null;
            if (leader == null)
            {
                leader = cars[0];
                if (paceTarget >= 0)
                    Log(string.Format("pace car held for #{0} now: #{1} is on pit road or out", leader.Num, CarNumber(paceTarget)));
                paceTarget = leader.Idx;
            }
            if (!paceHolding)
            {
                if (!paceGameLeadFresh)
                {
                    paceGameLead = m.F32(PaceStateLeadTime);
                    if (paceGameLead == HoldLeadTime || paceGameLead == 1e6f) paceGameLead = 0; // left behind by a Race Control that did not close properly
                }
                paceHolding = true;
                paceHoldStart = Now;
                paceHoldLogAt = double.NaN;
            }
            m.W32(PaceStateLeadTime, FloatBits(HoldLeadTime));

            // the leader's real speed, from its own progress (it is slowing to caution speed)
            double gap = Mod(DlongOf(Addr.PaceCarIdx) - DlongOf(leader.Idx), trackLen); // metres the leader is behind the pace car's spot
            if (leader.Idx != paceLeader) { paceLeader = leader.Idx; paceLastGap = double.NaN; paceLeaderSpeed = double.NaN; paceGapPrev = double.NaN; }
            if (LeaderWentBy(leader, gap, trackLen)) return;
            // 3.2.9: the game's own velocity, at once. Measured over a second (3.2.4) the leader went unmeasured for the
            // first second of the hold: run F laps 38 and 71 had it 162-181 m away at 37-47 m/s at the yellow, past the
            // pace car before its speed was known. (The player's car reads 0 here: the measured speed is kept for it.)
            double vNow = PaceFixes ? CarSpeed(leader.Idx) : double.NaN;
            bool velocityOk = !double.IsNaN(vNow) && (vNow > 0.5 || leader.Idx != Addr.PlayerIdx);
            // measured over a second or more: positions move once a game tick, so one poll often sees no move at all
            // and the per-poll speed (3.2.2) averaged a third to a half of the real one
            if (double.IsNaN(paceLastGap)) { paceLastGap = gap; paceLastTime = Now; }
            else if (Now - paceLastTime >= PaceSpeedWindow)
            {
                double moved = Mod(paceLastGap - gap, trackLen);
                if (moved < trackLen / 4)
                {
                    double v = moved / (Now - paceLastTime);
                    paceLeaderSpeed = double.IsNaN(paceLeaderSpeed) ? v : paceLeaderSpeed * 0.5 + v * 0.5;
                }
                paceLastGap = gap;
                paceLastTime = Now;
            }
            if (velocityOk) paceLeaderSpeed = vNow;
            // 3.2.9: a leader stopped (in the wreck) is not waited for: the next car on track that is moving is picked up.
            // Run F: #38 sat at 338 m for 70 s on lap 71 and the pace car waited for it until the time ran out.
            if (velocityOk && vNow < 2 && gap > 30)
            {
                if (double.IsNaN(paceTargetStopped)) paceTargetStopped = Now;
                else if (Now - paceTargetStopped > 4)
                {
                    Car next = cars.FirstOrDefault(c => c.Idx != leader.Idx && leader.Prog - c.Prog < 0.3 && CarSpeed(c.Idx) > 5);   // close behind it in the running order
                    if (next != null)
                    {
                        Log(string.Format("pace car held for #{0} now: #{1} has stopped {2:0} m from it", next.Num, leader.Num, gap));
                        paceTarget = next.Idx;
                        paceTargetStopped = double.NaN;
                        return;
                    }
                }
            }
            else paceTargetStopped = double.NaN;
            if (!paceLogged)
            {
                paceLogged = true;
                Log(string.Format("pace car held on pit road for leader #{0} ({1:0} m away): the field is frozen, so it leaves when the leader is {2:0} m from it",
                    leader.Num, gap, ReleaseDistance(trackLen)));
            }
            if (double.IsNaN(paceParkedAt)) paceParkedAt = DlongOf(Addr.PaceCarIdx);
            if (double.IsNaN(paceHoldLogAt) || Now - paceHoldLogAt >= 5)   // the leader's approach, to see where it slows
            {
                paceHoldLogAt = Now;
                Log(string.Format("pace car held {0:0} s: leader #{1} {2:0} m away at {3:0} m/s", Now - paceHoldStart, leader.Num, gap,
                    double.IsNaN(paceLeaderSpeed) ? 0 : paceLeaderSpeed));
            }
            double at = ReleaseDistance(trackLen);
            // the leader has gap + offset to go to where the pace car joins the track, and covers avg x exit time
            // while the pace car comes out; it should be PaceAheadWanted short of that point then
            if (!double.IsNaN(paceMergeOffset) && !double.IsNaN(paceExitTime) && !double.IsNaN(paceLeaderAvg))
            {
                double phys = Mod(paceLeaderAvg * paceExitTime + PaceAheadWanted - paceMergeOffset, trackLen);
                if (phys >= 50 && phys <= trackLen * 0.6) at = phys;     // never most of a lap away (see PaceCarOut)
            }
            // 3.2.7: a leader faster than the one the point was learned with needs the pace car sooner. Run E lap 10
            // (North Wilkesboro): the yellow had just come out, the leader was doing 52 m/s and the 85 m point (learned
            // at about 30 m/s) let it past the pace car; it then ran a lap at 36-46 m/s to catch the pace car and lapped
            // the back of the field (18 dropped back). With the leader's speed now, the same sum says 250 m: it waits a lap.
            // 3.2.9: an exit remembered from a leader slower than the pace speed (saved by 3.2.7/3.2.8) is not used
            if (PaceFixes && !double.IsNaN(paceLeaderAvg) && PaceSpeedOk())
            {
                float mphNow = TrackPaceSpeed();
                if (!float.IsNaN(mphNow) && paceLeaderAvg < mphNow * 0.44704)
                {
                    // the pace car's own part (time and metres out) is kept: the fast-leader sum needs only that
                    Log(string.Format("pace car exit: the leader's {0:0} m/s in it is not used (under the pace speed); the pace car's {1:0.0} s and {2:0} m are", paceLeaderAvg, paceExitTime, paceMergeOffset));
                    paceLeaderAvg = double.NaN;
                }
            }
            bool fastLeader = false;
            if (PaceFixes && !double.IsNaN(paceLeaderSpeed) && !double.IsNaN(paceMergeOffset) && !double.IsNaN(paceExitTime))
            {
                double now = paceLeaderSpeed * paceExitTime + PaceAheadWanted - paceMergeOffset;
                if (now > at && now <= trackLen * 0.6) { at = now; fastLeader = true; }
            }
            at = Math.Max(at, MinRelease(trackLen));
            // the leader's speed comes after a second of holding: nothing goes before it
            if (PaceFixes && double.IsNaN(paceLeaderSpeed) && Now - paceHoldStart < 3) return;
            // inside the window: go. Closer than that the leader is too close to make it (Toledo: sent at 174 m with
            // 332 m wanted, it came out 219 m behind the leader), so the pace car waits for its next time by.
            // 3.2.5: the PaceFixes width (150 m) is for the long tracks; at North Wilkesboro (195 m) it let the pace car go
            // with the leader 63 m away (2026-11-08 lap 91, it came out behind the leader). Half the distance, 80-150 m.
            // 3.2.6: never closer than 60% of the release point (and 50 m): with the point learned down to 82-85 m at North
            // Wilkesboro (run D) the window reached 2-5 m, and the pace car went with the leader 29 m and 36 m away
            // (laps 41 and 21: it came out behind the leader, 15 cars dropped back). Closer than that it waits a lap.
            // (3.2.5's wait for a car between the leader and the spot went: all 3 times it waited, run D, the leader got
            // too close and the pickup failed; on a short track the field fills the lap and there is rarely a gap.)
            // 3.2.7: the width is 1.5 s of the leader's speed (at least 20% of the point, 20-150 m): 80 m let a 52 m/s
            // leader through at a short track
            double width = !PaceFixes ? Math.Max(80, at * 0.25)
                : Math.Max(20, Math.Min(150, Math.Max(at * 0.2, 1.5 * (double.IsNaN(paceLeaderSpeed) ? 30 : paceLeaderSpeed))));
            double windowLow = PaceFixes ? Math.Max(at - width, Math.Max(50, at * 0.6)) : at - width;
            bool inWindow = gap <= at && gap >= windowLow;
            // 3.2.6: the crawl release is for long tracks (a leader stopping well short of the pace car at Talladega).
            // At North Wilkesboro (run D laps 111 and 139) it went for a leader at 11 m/s 73 m and 301 m away with the
            // release point at 85 m: 22 cars caught on lap 139. A short track's leader gets to the window anyway.
            bool crawling = !inWindow && (!PaceFixes || trackLen > 2000) &&
                !double.IsNaN(paceLeaderSpeed) && paceLeaderSpeed < PaceCrawlSpeed && Now - paceHoldStart > 5 &&
                gap < Math.Min(trackLen * 0.6, PaceCrawlWithin) &&
                (!PaceFixes || gap <= Math.Max(at, 350) || Now - paceHoldStart > 20);   // a crawling leader 440-640 m out sent the pace car 2 km ahead of it: 70-86 s to pick it up
            if (inWindow || crawling)
            {
                paceCrawlRelease = crawling;
                paceReleaseGap = gap;
                paceReleaseUsed = at;
                paceSentAt = Now;
                paceLeaderAtSend = DlongOf(leader.Idx);
                paceAccelSeen = accelLogged;
                paceLeaderAtRelease = leader.Idx;
                ReleasePaceCar(crawling
                    ? string.Format("leader #{0} slowed to {1:0} m/s {2:0} m away, waiting for it (release point {3:0} m)", leader.Num, paceLeaderSpeed, gap, at)
                    : string.Format("leader #{0} is {1:0} m away ({2:0} m/s; sent out at {3:0} m{4})", leader.Num, gap,
                    double.IsNaN(paceLeaderSpeed) ? 0 : paceLeaderSpeed, at, fastLeader ? ", further out for the leader's speed" : double.IsNaN(PaceReleaseAt) ? ", a first guess for this track" : ", learned here"), true);
            }
            else if (Now - paceHoldStart > PaceHoldLimit(trackLen))
                ReleasePaceCar(string.Format("held for {0:0} s", PaceHoldLimit(trackLen)), true);
            // too close to make it in front of the leader: it waits for the leader's next time by
        }

        // 3.3.10 (run P lap 46, the pace car not held): leader #34 was 87 m past the pace car's stall at the yellow. The
        // game let the pace car out behind it, the leader was told to drive a lap round to it (fixed in the pace lap fit)
        // and pit road opened 100 s after the yellow. As with the hold (LeaderWentBy): the pace car goes at once, and the
        // chase gives the leader the pace car's pace lap so it crawls and the pace car catches it from behind. The
        // waiting state's own entry writes over a "go now" set while parked; it is written again there (PaceCarTick).
        void NoHoldLeaderJustPast(bool waiting)
        {
            double tl = m.F64(Addr.TrackLength);
            if (tl <= 100) return;
            Car fl, ff;
            List<Car> cars = Field(out fl, out ff);
            if (cars == null || cars.Count == 0) return;
            Car leader = cars[0];
            double gap = Mod(DlongOf(Addr.PaceCarIdx) - DlongOf(leader.Idx), tl);   // metres the leader is behind the pace car's spot
            if (gap <= tl * (1 - LeaderJustPast)) return;
            paceHolding = false;
            paceReleased = paceReleasedNow = true;
            paceReleaseGap = gap;
            paceReleaseUsed = ReleaseDistance(tl);
            paceSentAt = Now;
            paceLeaderAtSend = DlongOf(leader.Idx);
            paceAccelSeen = accelLogged;
            paceLeaderAtRelease = leader.Idx;
            paceWentBehind = true;
            chaseLeader = leader.Idx; chaseLeadPrev = double.NaN;
            if (waiting) m.W32(PaceStateLeadTime, FloatBits(1e6f));
            Log(string.Format("pace car sent out: leader #{0} is {1:0} m past it (pace car not held): out now to catch it from behind, the leader crawls for it",
                leader.Num, tl - gap));
        }

        // 3.3.0 (run G2, North Wilkesboro 2026-11-09, lap 4): once the leader has driven past the held pace car, the game
        // makes the PACE CAR catch the leader from behind. Until it does, the leader and the whole field crawl at 0.6 of
        // the pace speed (12.3 m/s there) and never speed up to catch a pace car in front of them. Held for the leader's
        // next time by, the field crawled a full lap (57 s), the pace car then came out 85 m in front of the leader, ran
        // away from it at 20 m/s and had to lap the whole field (another 2 minutes) to get to it. Every trace agrees:
        // a leader that had not gone by caught the pace car to 20 m within 15-20 s; one that had, sat at 12.3 m/s.
        // So the moment the leader goes by, the pace car goes, right behind it, and catches it while it crawls.
        double paceGapPrev = double.NaN;
        bool paceWentBehind;
        bool LeaderWentBy(Car leader, double gap, double trackLen)
        {
            double prev = paceGapPrev;
            paceGapPrev = gap;
            // 3.3.6: also a leader already just past it at the first look (within 15% of a lap): held for its next time by,
            // the field crawls a whole lap (run G lap 67, 192 m past); sent now, ChaseTick holds it for the pace car
            bool justPast = double.IsNaN(prev) && gap > trackLen * 0.85;
            if (!PaceFixes || !(justPast || (!double.IsNaN(prev) && prev < trackLen * 0.3 && gap > trackLen * 0.7))) return false;
            paceReleaseGap = gap;
            paceReleaseUsed = ReleaseDistance(trackLen);
            paceSentAt = Now;
            paceLeaderAtSend = DlongOf(leader.Idx);
            paceAccelSeen = accelLogged;
            paceLeaderAtRelease = leader.Idx;
            paceWentBehind = true;
            chaseLeader = leader.Idx; chaseLeadPrev = double.NaN;
            ReleasePaceCar(string.Format("leader #{0} went by it ({1:0} m/s): out right behind it to catch it (the game holds the field at a crawl until the pace car gets by the leader)",
                leader.Num, CarSpeed(leader.Idx)), true);
            return true;
        }

        // 3.3.6 (run L lap 102, North Wilkesboro): the pace car went out the moment leader #7 went by (20 m from its stall
        // at the yellow), but #7, #10 and #9 had not crossed the line since the yellow: pace lap 0 against the pace car's 1.
        // A car with a pace lap under the pace car's drives at catch-up speed to its spot BEHIND the pace car, so the three
        // ran a whole lap at 35-43 m/s through the crawling field (the pace car at 20 m/s never caught them) and lapped it to
        // get there (57 s). The game catches a leader from behind only when it crawls: one with the pace car's pace lap
        // (run G2). So until the pace car is in front of the leader, every car between the pace car and the leader gets
        // the pace car's pace lap: they crawl at 0.6 of the pace speed and the pace car, at the pace speed, catches them
        // and goes by them. Not given back: it is the pace lap they would have had crossing the line behind the pace car.
        // Tail catch-up leaves them alone meanwhile.
        int chaseLeader = -1;
        HashSet<int> chaseHeld = new HashSet<int>();      // cars between the pace car and the leader right now
        HashSet<int> chaseLogged = new HashSet<int>();
        double chaseLeadPrev = double.NaN;

        void ChaseTick()
        {
            chaseHeld.Clear();
            if (chaseLeader < 0) return;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            if (!Running(chaseLeader) || OnPitRoad(chaseLeader)) { EndChase("leader #" + CarNumber(chaseLeader) + " is off the track"); return; }
            float pcD = DlongOf(Addr.PaceCarIdx);
            double lead = Mod(DlongOf(chaseLeader) - pcD, trackLen);   // how far the leader is in front of the pace car
            // 3.3.7 (run M lap 21): the game kept the pace car in its stall 29 s (a yellow 30 s after a restart) and the
            // leader crawled on round: "more than 60% of a lap in front" ended this while the pace car had not moved.
            // Over only when the pace car has gone by the leader (the distance wraps), or it is out and moving with
            // the leader behind it (it came out in front of a leader that crawled round to it).
            double prevLead = chaseLeadPrev;
            chaseLeadPrev = lead;
            bool wentBy = !double.IsNaN(prevLead) && prevLead < trackLen * 0.3 && lead > trackLen * 0.7;
            bool outInFront = lead > trackLen * 0.5 && CarSpeed(Addr.PaceCarIdx) > 5;
            if (wentBy || outInFront)
            {
                EndChase(wentBy ? "the pace car went by leader #" + CarNumber(chaseLeader) : "the pace car came out in front of leader #" + CarNumber(chaseLeader));
                return;
            }
            int pace = m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd);
            var raised = new List<string>();
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx) || OnPitRoad(idx)) continue;
                if (Mod(DlongOf(idx) - pcD, trackLen) > lead + 5) continue;
                // 3.3.8: with the pace laps fitted to the line only the leader is held here: the cars behind it get the
                // pace lap that matches their gap to the car they follow (they crawl behind a crawling leader), and the
                // tail cars in front of it are a pace lap down by the same rule and drive on round to the back
                if (PaceLapFit && idx != chaseLeader) continue;
                chaseHeld.Add(idx);
                if (tailLowered.ContainsKey(idx)) tailLowered.Remove(idx);   // overwritten here, nothing to give back
                long a = Addr.PaceLap + idx * Addr.Ocd;
                int pl = m.I32(a);
                if (pl >= pace) continue;
                m.W32(a, pace);
                if (chaseLogged.Add(idx)) raised.Add("#" + CarNumber(idx) + (pace - pl > 1 ? " (+" + (pace - pl) + ")" : ""));
            }
            if (raised.Count > 0)
                Log(string.Format("pace car behind leader #{0} ({1:0} m in front of it): {2} given the pace car's pace lap so they crawl and the pace car catches them (instead of a lap round to get behind it)",
                    CarNumber(chaseLeader), lead, string.Join(" ", raised.ToArray())));
        }

        void EndChase(string why)
        {
            if (chaseLeader < 0) return;
            if (chaseLogged.Count > 0) Log("pace car catching the leader: done (" + why + ")");
            chaseLeader = -1;
            chaseHeld.Clear();
            chaseLogged.Clear();
            chaseLeadPrev = double.NaN;
        }

        // Has the pace car picked up the leader: on track, with the leader behind it. Pit road only counts the
        // leader's times by from then on: at Toledo the pace car came out behind the leader, the leader's next pass
        // counted as its second time by and pit road opened with the leader never picked up (it drove straight in).
        // Without the game's pace car states (the offline sims) it counts as picked up.
        bool PaceCarHasLeader(Car leader, double trackLen)
        {
            if (!PaceStatesOk()) return true;
            if (!PaceCarOnTrack()) return false;
            // close behind it: at Toledo the pace car came out 258 m in front of the leader with 19 cars in between,
            // and "the leader is behind it" opened pit road that same second
            return leader.Gap <= PickedUpWithin;
        }

        // The game's own pickup: the pace car's state switches to "caution" once it has the field behind it. Cars sent
        // around before that drop back (they have no pace car in front of them to pass). From flag 3 it no longer waits.
        bool PaceCarPickedUp()
        {
            if (!PaceFixes || !PaceStatesOk() || flag >= 3) return true;
            return m.U32(CarState(Addr.PaceCarIdx)) == PaceStateCaution;
        }

        const double PickedUpWithin = 200;          // metres: the leader this close behind the pace car = picked up

        // On track and pacing: its caution state, or (Talladega, 2026-10-25) the state it leaves pit road in, which it
        // kept for 85 s after reaching the track - it ran at caution speed for the last 30 s of that and only switched
        // about 3 km round the lap. Waiting for the switch, the leader's first time by never counted and every caution
        // there opened pit road a lap late.
        const long PaceStateLeaving = 0x760F14;
        bool PaceCarOnTrack()
        {
            uint s = m.U32(CarState(Addr.PaceCarIdx));
            return s == PaceStateCaution || (s == PaceStateLeaving && !OnPitRoad(Addr.PaceCarIdx));
        }
        bool paceAccelSeen;
        // Measured at each release (not under time acceleration): where the pace car reaches the track, metres past
        // its spot on pit road, and how long that takes (Toledo: 22 s, the merge far down pit road). Then the leader
        // is released for when it gets to that point just after the pace car, at the speed it is really doing.
        double paceMergeOffset = double.NaN, paceExitTime = double.NaN, paceParkedAt = double.NaN;
        double paceLeaderAvg = double.NaN, paceLeaderAtSend = double.NaN;  // the leader's average speed while the pace car comes out (it is still slowing)                          // time acceleration while the pace car was out: don't learn from it

        // The 3.2.1 changes (release floor on long tracks, crawl release, wider window, send-arounds wait for pickup, no
        // strung-out wait for pit road). Off = RaceControl 1.2.0 as it was.
        public bool PaceFixes = false;
        // 3.2.7: the last clean exit (seconds to the track, metres out, the leader's average meanwhile) is saved per track
        // (ini PaceExit.<track>), so the first caution of a race can allow for a fast leader too (run E lap 10 was the first)
        public Action<string> PaceExitLearned;
        public string PaceExitSaved
        {
            set
            {
                string[] p = (value ?? "").Split(',');
                double t, o, v;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                if (p.Length == 3 && double.TryParse(p[0], ns, ci, out t) && double.TryParse(p[1], ns, ci, out o) &&
                    double.TryParse(p[2], ns, ci, out v) && t > 1 && t < 60 && o >= 0 && v > 5 && v < 80 && double.IsNaN(paceExitTime))
                {
                    paceExitTime = t; paceMergeOffset = o; paceLeaderAvg = v;
                }
            }
        }
        double MinRelease(double trackLen) { return !PaceFixes ? 50 : trackLen > 3000 ? 250 : 80; }   // 3.2.5: 80 m on short tracks (the learned point can now come down there)

        // Where the leader is when the pace car goes: learned for the track, else about 16 s at caution speed.
        double ReleaseDistance(double trackLen)
        {
            double d = double.IsNaN(PaceReleaseAt) ? 400 : PaceReleaseAt;
            // never close to a full lap: the leader just past would count. On a long track never under 250 m: the
            // Talladega setting had been learned down to 50 m (a bad exit measurement), the pace car then came out
            // behind the leader, in the pack, and took 51-54 s to be picked up (about 8 s when it comes out in front).
            return Math.Max(MinRelease(trackLen), Math.Min(trackLen * 0.6, d));
        }

        // now: go straight away; else the game's own lead time decides
        void ReleasePaceCar(string why, bool now)
        {
            if (!paceHolding) return;
            m.W32(PaceStateLeadTime, FloatBits(now ? 1e6f : paceGameLead));
            paceHolding = false;
            paceReleased = true;
            paceReleasedNow = now;
            Log("pace car sent out: " + why);
        }

        // The pace car has left: the game's lead time back, and once it is on track, who it picked up.
        void PaceCarOut(uint state)
        {
            if (accelLogged) paceAccelSeen = true;
            if (paceHolding || paceReleased)
            {
                float lead = m.F32(PaceStateLeadTime);
                if (lead == HoldLeadTime || lead == 1e6f) m.W32(PaceStateLeadTime, FloatBits(paceGameLead));
            }
            if (paceHolding) { paceHolding = false; paceReleased = true; }
            if (state != PaceStateCaution && paceReleased) PaceCarExitWatch(state);
            if (state != PaceStateCaution || paceLeaderAtRelease < 0) { if (state == PaceStateCaution) paceReleased = false; return; }
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            float pc = DlongOf(Addr.PaceCarIdx);
            int behind = -1; double best = double.MaxValue;
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx) || OnPitRoad(idx)) continue;
                double g = Mod(pc - DlongOf(idx), trackLen);
                if (g < best) { best = g; behind = idx; }
            }
            if (behind >= 0)
                Log(string.Format("pace car on track {4:0} s after it was sent out: #{0} right behind it ({1:0} m){2} (the leader was {3:0} m away when it was sent out)",
                    CarNumber(behind), best, behind == paceLeaderAtRelease ? ", the leader" : ", not the leader #" + CarNumber(paceLeaderAtRelease), paceReleaseGap, Now - paceSentAt));
            paceMatchArmed = true; paceMatchStart = Now;      // 3.2.8: speed matching starts from here (PaceMatchTick)
            if (paceWentBehind)
            {
                // sent out behind the leader on purpose: nothing to learn about the release point from it
                double inFront = Running(paceLeaderAtRelease) ? Mod(DlongOf(paceLeaderAtRelease) - pc, trackLen) : double.NaN;
                Log(string.Format("pace car on track behind leader #{0} ({1:0} m in front of it), catching it: release point not changed",
                    CarNumber(paceLeaderAtRelease), inFront));
                paceWentBehind = false;
                paceReleased = false;
                paceLeaderAtRelease = -1;
                paceAccelSeen = false;
                paceCrawlRelease = false;
                paceParkedAt = double.NaN;
                return;
            }
            // learn: how far in front of (or behind) the leader it came out, and move the release point by most of it
            // a normal exit only: sent out with the field going by, the game held it on pit road for 82 s
            double exitTook = Now - paceSentAt;
            bool measuredNow = false;
            // 3.2.5: 5 s, not 8: at North Wilkesboro (2026-11-08) a clear exit took 7-8 s and was read as held up
            bool normalExit = exitTook >= 5 && exitTook <= 40 && (double.IsNaN(paceExitTime) || exitTook <= paceExitTime * 1.5);
            if (!normalExit && !double.IsNaN(exitTook))
                Log(string.Format("pace car took {0:0} s to reach the track ({1}): not used for the timing", exitTook,
                    exitTook < 5 ? "too quick to tell" : "held up by traffic on the way out"));
            // Talladega (2026-10-27): its caution state only came 2196 m past its pit road spot, 37 s out - not where it
            // joined the track. That "measurement" set the release point to 3262 m (the pace car then went with the
            // leader most of a lap away) and the learning step moved the saved one to 50 m. A merge that far down the
            // track is not a merge: nothing is learned from that exit.
            // 3.2.5: at least 400 m: on a 1020 m track (North Wilkesboro) the switch comes 286-323 m out every time, and
            // 25% of the lap (255 m) threw away every measurement, so the release point there never learned
            if (normalExit && !double.IsNaN(paceParkedAt) && Mod(pc - paceParkedAt, trackLen) > Math.Max(400, trackLen * 0.25))
            {
                normalExit = false;
                Log(string.Format("pace car counted on track {0:0} m past its pit road spot (the game switched its state late): not used for the timing",
                    Mod(pc - paceParkedAt, trackLen)));
            }
            // 3.2.6: nothing is learned from a pace car sent the moment it was held (the leader was already in the window
            // at the yellow, still near race speed): run D lap 41, leader 29 m away at 44 m/s, moved the point 82 -> 199 m
            // and the next caution came out early with 7 cars caught
            bool sentAtOnce = !double.IsNaN(paceHoldStart) && !double.IsNaN(paceSentAt) && paceSentAt - paceHoldStart < 3;
            if (sentAtOnce) normalExit = false;
            // 3.2.9: nor from one where the leader averaged less than the pace speed while the pace car came out (it was
            // stuck behind a stopped car, or crawling): run F laps 49 and 83 at 13 m/s saved a 10.1 s exit and moved the
            // point 84 -> 50 m. The leader catches the pace car at about 1.2-1.5 times its speed when nothing holds it up.
            float paceMph = PaceSpeedOk() ? TrackPaceSpeed() : float.NaN;
            double paceMs = float.IsNaN(paceMph) ? double.NaN : paceMph * 0.44704;
            bool slowLeader = false;
            if (PaceFixes && !double.IsNaN(paceMs) && Running(paceLeaderAtRelease) && !double.IsNaN(paceLeaderAtSend) && Now - paceSentAt > 1)
            {
                double avg = Mod(DlongOf(paceLeaderAtRelease) - paceLeaderAtSend, trackLen) / (Now - paceSentAt);
                if (avg < paceMs)
                {
                    slowLeader = true;
                    normalExit = false;
                    Log(string.Format("pace car release point not changed: the leader averaged {0:0} m/s while it came out, under the pace speed ({1:0} m/s)", avg, paceMs));
                }
            }
            if (normalExit && !paceAccelSeen && !accelLogged && !double.IsNaN(paceParkedAt) && !double.IsNaN(paceSentAt))
            {
                paceMergeOffset = Mod(pc - paceParkedAt, trackLen);
                paceExitTime = Now - paceSentAt;
                if (Running(paceLeaderAtRelease) && !double.IsNaN(paceLeaderAtSend) && paceExitTime > 1)
                    paceLeaderAvg = Math.Max(8, Math.Min(60, Mod(DlongOf(paceLeaderAtRelease) - paceLeaderAtSend, trackLen) / paceExitTime));
                Log(string.Format("pace car reaches the track {0:0} m past its pit road spot, {1:0.0} s after it is sent, the leader averaging {2:0} m/s meanwhile: used for the next release here",
                    paceMergeOffset, paceExitTime, paceLeaderAvg));
                if (PaceExitLearned != null && !double.IsNaN(paceLeaderAvg))
                    PaceExitLearned(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0},{1:0},{2:0.0}", paceExitTime, paceMergeOffset, paceLeaderAvg));
                if (!double.IsNaN(paceLeaderAvg))
                {
                    // saved as the track's release distance too, so the first caution after a restart uses it
                    double physAt = Mod(paceLeaderAvg * paceExitTime + PaceAheadWanted - paceMergeOffset, trackLen);
                    // 3.2.6: at most half again or half less than the point it replaces, one caution at a time
                    if (PaceFixes && !double.IsNaN(PaceReleaseAt) && physAt >= 50 && physAt <= trackLen * 0.6)
                        physAt = Math.Max(PaceReleaseAt * 0.5, Math.Min(PaceReleaseAt * 1.5, physAt));
                    if (physAt >= 50 && physAt <= trackLen * 0.6)
                    {
                        PaceReleaseAt = physAt;
                        if (PaceReleaseLearned != null) PaceReleaseLearned(physAt);
                        Log(string.Format("pace car goes when the leader is {0:0} m from it next time here (saved for this track)", physAt));
                        measuredNow = true;
                    }
                }
            }
            if (Running(paceLeaderAtRelease) && !OnPitRoad(paceLeaderAtRelease))
            {
                double ahead = Mod(pc - DlongOf(paceLeaderAtRelease), trackLen); // pace car in front of the leader
                double error = ahead <= trackLen / 2 ? -(ahead - PaceAheadWanted) : trackLen - ahead + PaceAheadWanted;
                // Not learned from: time acceleration (one tick is up to a second of game time; at Toledo the
                // distance jumped 553 -> 654 -> 438 -> 214 -> 388 at x30 to x90), or an outcome near half a lap,
                // where "in front" and "behind" can't be told apart. Otherwise half the way, at most 120 m a time.
                bool unclear = Math.Abs(ahead - trackLen / 2) < trackLen * 0.15;
                if (slowLeader) { }
                else if ((accelLogged || paceAccelSeen || unclear || !normalExit || paceCrawlRelease) && Math.Abs(error) > 30)
                    Log("pace car release point not changed: " + (paceCrawlRelease ? "sent out for a crawling leader" : unclear ? "it came out about half a lap from the leader (in front or behind can't be told)" : sentAtOnce ? "it went the moment it was held" : !normalExit ? "held up on the way out" : "time acceleration was on"));
                else if (Math.Abs(error) > 30 && !measuredNow)   // the measurement above is better
                {
                    // the leader was paceReleaseGap away when it went, so that plus the error is where it should
                    // have gone; half way there from the distance used, at most 120 m
                    double step = Math.Max(-120, Math.Min(120, 0.5 * (paceReleaseGap + error - paceReleaseUsed)));
                    double next = Math.Max(50, Math.Min(trackLen * 0.85, paceReleaseUsed + step));
                    Log(string.Format("pace car came out {0:0} m {1} the leader: it goes when the leader is {2:0} m away next time here (was {3:0})",
                        ahead <= trackLen / 2 ? ahead : trackLen - ahead, ahead <= trackLen / 2 ? "in front of" : "behind", next, paceReleaseUsed));
                    PaceReleaseAt = next;
                    if (PaceReleaseLearned != null) PaceReleaseLearned(next);
                }
            }
            paceReleased = false;
            paceLeaderAtRelease = -1;
            paceAccelSeen = false;
            paceCrawlRelease = false;
            paceParkedAt = double.NaN;
        }

        // Diagnostics (Talladega, 2026-10-25: 53 s to 2:05 every caution against 11-22 s normally): while the pace car
        // is on its way down pit road, every 4 s for its first 2 minutes, where it is, how fast, and the cars on pit road
        // within 300 m in front of it - the cars it is stuck behind.
        double paceWatchAt = double.NaN;
        void PaceCarExitWatch(uint state)
        {
            if (double.IsNaN(paceSentAt) || Now - paceSentAt > 120) return;
            if (!double.IsNaN(paceWatchAt) && Now - paceWatchAt < 4) return;
            paceWatchAt = Now;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            long pb = Addr.Laps - 1360 + Addr.PaceCarIdx * Addr.Ocd;
            float pc = DlongOf(Addr.PaceCarIdx);
            double v = Math.Sqrt(Math.Pow(m.F32(pb + 6634), 2) + Math.Pow(m.F32(pb + 6638), 2));
            var ahead = new List<string>();
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx) || !OnPitRoad(idx)) continue;
                double g = Mod(DlongOf(idx) - pc, trackLen);
                if (g > 300) continue;
                long b = Addr.Laps - 1360 + idx * Addr.Ocd;
                double cv = Math.Sqrt(Math.Pow(m.F32(b + 6634), 2) + Math.Pow(m.F32(b + 6638), 2));
                ahead.Add(string.Format("#{0} {1:0} m ahead at {2:0} m/s{3}", CarNumber(idx), g, cv, m.U8(Addr.Pitting + idx * Addr.Ocd) != 0 ? " (pitting)" : ""));
            }
            Log(string.Format("pace car exit {0:0} s: {1:0} m round the lap at {2:0} m/s (state 0x{3:x}, {5}){4}", Now - paceSentAt, pc, v, state,
                ahead.Count > 0 ? "; on pit road in front of it: " + string.Join(", ", ahead.ToArray()) : "; nothing on pit road in front of it",
                OnPitRoad(Addr.PaceCarIdx) ? "on pit road" : "on track, counts as out"));
        }

        // ---- 3.2.8: the pace car's speed matched to the leader --------------------------------------------------------
        // Run E (North Wilkesboro, 2026-11-08): with the pace car on track at its fixed speed and the leader still behind
        // it, one chased the other: a leader crawling at 13 m/s was left 149 m behind (laps 71, 105) and the pace car
        // ran on into the slow cars ahead of it. The game keeps track.ini's pace_speed_limit_MPH in its settings table
        // at 0x0074B080 (float mph; NR2003.exe's default there is 60) and reads it live (25 places, fld [0x74B080] x
        // 0.012408 = mph -> metres a tick); nothing in the game writes it after the track loads. Written live it takes
        // effect within a second, either way (user test 2026-11-08: 45 -> 30, 55 and 10 mph, the pace car at once
        // 13.4, 24.6 and 4.5 m/s). So once the pickup is logged, while the leader is more than PaceMatchNear behind the
        // pace car (and less than half a lap), the pace speed comes down (to 60% at most), in steps (the change itself
        // is instant: 4 mph a second), and goes back to the track's own value when the leader is on its bumper, at
        // flag 3, after PaceMatchMax, at the green, when Race Control closes or the option is off. The track's value
        // is read from tracks\<track>\track.ini (else the value found before Race Control ever wrote it).
        // 3.2.9: off unless PaceMatch=1. Run F (2026-11-08): the field's caution speeds come from the same setting (it
        // crawled at 0.6 of it while the pace car was held, and slowed with it: 12.3 -> 8.3 m/s as the pace car went
        // 20.1 -> 12.1), so slowing the pace car slowed the leader too and the gap never closed (105 -> 333 m, lap 49).
        public bool PaceMatch = false;
        public Func<string> GameFolder;                  // NR2003's folder (for tracks\<track>\track.ini)
        const long PaceSpeedSlot = 0x0074B080, PaceSpeedEntryKey = 0x0074B078, PaceSpeedKeyString = 0x00749F90;
        const double PaceMatchNear = 70, PaceMatchMax = 90, PaceMatchStep = 4, PaceMatchFloor = 0.6;
        bool? paceSpeedOk;
        bool paceMatchArmed, paceMatchDone, paceMatchLogged;
        double paceMatchStart = double.NaN, paceMatchLastStep = double.NaN, paceMatchLogAt = double.NaN;
        float paceSpeedTrack = float.NaN, paceSpeedWritten = float.NaN;
        string paceSpeedFor;

        bool PaceSpeedOk()
        {
            if (paceSpeedOk == null)
                try { paceSpeedOk = m.U32(PaceSpeedEntryKey) == PaceSpeedKeyString && m.Str(PaceSpeedKeyString, 32) == "pace_speed_limit_MPH"; }
                catch (Exception) { paceSpeedOk = false; }
            return paceSpeedOk.Value;
        }

        // The track's own pace speed (mph): track.ini, else what the game had before Race Control wrote anything.
        float TrackPaceSpeed()
        {
            if (paceSpeedFor == TrackName) return paceSpeedTrack;
            paceSpeedFor = TrackName;
            paceSpeedTrack = float.NaN;
            float inMemory = m.F32(PaceSpeedSlot);
            string where = "the game's memory";
            try
            {
                string folder = GameFolder != null ? GameFolder() : null;
                string ini = folder == null || string.IsNullOrEmpty(TrackName) ? null : System.IO.Path.Combine(folder, "tracks", TrackName, "track.ini");
                if (ini != null && System.IO.File.Exists(ini))
                    foreach (string line in System.IO.File.ReadAllLines(ini))
                    {
                        string t = line.Split(';')[0].Trim();
                        if (!t.StartsWith("pace_speed_limit_mph", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] kv = t.Split('=');
                        float v;
                        if (kv.Length == 2 && float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v >= 15 && v <= 150)
                        { paceSpeedTrack = v; where = "track.ini"; }
                    }
            }
            catch (Exception) { }
            if (float.IsNaN(paceSpeedTrack) && float.IsNaN(paceSpeedWritten) && inMemory >= 15 && inMemory <= 150) paceSpeedTrack = inMemory;
            if (!float.IsNaN(paceSpeedTrack))
            {
                Log(string.Format("pace speed here: {0:0.#} mph ({1})", paceSpeedTrack, where));
                // a Race Control that closed during a slow-down leaves it low until the track is loaded again
                if (where == "track.ini" && Math.Abs(inMemory - paceSpeedTrack) > 0.05 && inMemory >= 5 && inMemory <= 150)
                {
                    m.W32(PaceSpeedSlot, FloatBits(paceSpeedTrack));
                    Log(string.Format("pace speed was {0:0.#} mph in the game's memory: put back to the track's {1:0.#}", inMemory, paceSpeedTrack));
                }
            }
            return paceSpeedTrack;
        }

        void WritePaceSpeed(float mph)
        {
            m.W32(PaceSpeedSlot, FloatBits(mph));
            paceSpeedWritten = mph;
        }

        // back to the track's value at once (green, Race Control closing, option off)
        void RestorePaceSpeed(string why)
        {
            if (float.IsNaN(paceSpeedWritten)) return;
            try
            {
                if (!float.IsNaN(paceSpeedTrack) && PaceSpeedOk()) m.W32(PaceSpeedSlot, FloatBits(paceSpeedTrack));
            }
            catch (Exception) { }
            if (paceMatchLogged) Log(string.Format("pace car back to {0:0.#} mph ({1})", paceSpeedTrack, why));
            paceSpeedWritten = float.NaN;
            paceMatchLogged = false;
        }

        void PaceMatchTick()
        {
            if (!PaceMatch || !PaceFixes || !PaceSpeedOk()) { RestorePaceSpeed("speed matching off"); return; }
            float track = TrackPaceSpeed();
            if (float.IsNaN(track)) return;
            double trackLen = m.F64(Addr.TrackLength);
            float want = track;
            string why = null;
            if (paceMatchArmed && !paceMatchDone && flag <= 2 && trackLen > 100 && PaceCarOnTrack())
            {
                Car leader, first;
                List<Car> cars = Field(out leader, out first);
                Car lead = cars == null ? null : (leader != null && cars.Contains(leader) ? leader : cars.FirstOrDefault());
                if (lead != null)
                {
                    double g = lead.Gap;                        // metres the leader is behind the pace car
                    if (g <= PaceMatchNear) { paceMatchDone = true; why = string.Format("leader #{0} on its bumper ({1:0} m)", lead.Num, g); }
                    else if (g >= trackLen / 2) { paceMatchDone = true; why = string.Format("leader #{0} more than half a lap back: no slowing for it", lead.Num); }
                    else if (Now - paceMatchStart > PaceMatchMax) { paceMatchDone = true; why = "slowed long enough"; }
                    else
                    {
                        // 70 m behind = the track's speed, 270 m or more = 60% of it
                        double share = Math.Max(PaceMatchFloor, 1 - (1 - PaceMatchFloor) * (g - PaceMatchNear) / 200);
                        want = (float)Math.Round(track * share);
                        if (double.IsNaN(paceMatchLogAt) || Now - paceMatchLogAt >= 5)
                        {
                            paceMatchLogAt = Now;
                            Log(string.Format("pace car slowed toward {0:0} mph (track {1:0.#}): leader #{2} is {3:0} m behind it", want, track, lead.Num, g));
                            paceMatchLogged = true;
                        }
                    }
                }
            }
            else if (paceMatchArmed && !paceMatchDone && flag > 2) { paceMatchDone = true; why = "flag " + flag; }
            float cur = float.IsNaN(paceSpeedWritten) ? track : paceSpeedWritten;
            if (Math.Abs(cur - want) < 0.05)
            {
                if (want == track && !float.IsNaN(paceSpeedWritten))
                {
                    if (paceMatchLogged) Log(string.Format("pace car back to {0:0.#} mph{1}", track, why != null ? " (" + why + ")" : ""));
                    paceSpeedWritten = float.NaN; paceMatchLogged = false;
                }
                return;
            }
            if (!double.IsNaN(paceMatchLastStep) && Now - paceMatchLastStep < 0.25) return;
            paceMatchLastStep = Now;
            float step = (float)(PaceMatchStep * 0.25);       // 4 mph a second
            float next = cur > want ? Math.Max(want, cur - step) : Math.Min(want, cur + step);
            WritePaceSpeed(next);
            if (why != null && want == track && paceMatchLogged) Log("pace car speeding back up to the track's " + track.ToString("0.#") + " mph (" + why + ")");
        }

        void ResetPaceCar()
        {
            if (paceHolding)
                try { m.W32(PaceStateLeadTime, FloatBits(paceGameLead)); } catch (Exception) { }
            paceHolding = paceReleased = paceLogged = false;
            paceReleasedNow = paceWaitSeen = paceGameLeadFresh = false;
            paceLeader = paceLeaderAtRelease = paceTarget = -1;
            paceTargetStopped = double.NaN;
            paceMatchArmed = paceMatchDone = false;
            paceLastGap = paceLeaderSpeed = paceGapPrev = double.NaN;
            paceWentBehind = false;
            chaseLeader = -1; chaseHeld.Clear(); chaseLogged.Clear(); chaseLeadPrev = double.NaN;
        }
    }
}
