using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceControl
{
    // What Better Cautions did besides the lineup: race back to the line, green-white-checkered, collisions.
    public partial class RaceEngine
    {
        public bool RaceBack = false;               // false: the field is frozen when the caution comes out
        // Stage racing: the laps stages end on come from StageEnds (Race Control's own settings, or optionally the
        // overlay app's). Race Control throws the stage caution itself when the leader is 3/4 through the stage's
        // last lap, so the cars behind race back to the line and the leader crosses it first (a caution at the
        // line itself left the cars just behind the leader stuck in front of the pace car). A caution another
        // app throws around the stage end is treated as the stage caution too.
        public bool StageRacing = true;
        public bool StageThrowCaution = true;
        public bool StageRaceBack = true;           // the field races back to the line at a stage end
        public Func<int, int[]> StageEnds;          // race length -> the laps stages end on (null/empty: no stages)
        const double StageCautionAt = 0.75;         // share of the stage's last lap the leader has done
        int stageThrown = -1;                       // stage end lap Race Control threw the caution for
        HashSet<int> stagesDone = new HashSet<int>();
        int stageRaceLength = -1, stageLastDone = -1;
        public bool GreenWhiteCheckered = true;     // a caution near the end adds laps so the race can finish under green
        public bool GhostOnPitRoad = true;          // no car-to-car contact on pit road
        public bool GhostWhilePacing = false;       // no car-to-car contact behind the pace car (flags 2-6)

        bool fieldFrozen, cautionSeen, stageCaution;
        bool throwCaution;
        Dictionary<int, double> ghostEndWanted = new Dictionary<int, double>(); // car -> when it should have had contact back

        // Asks for a caution on the next tick (from the window's button).
        public void ThrowCaution() { throwCaution = true; }

        long RaceLengthAddr { get { return numSessions == 2 ? Addr.RaceLengthQuickRace : Addr.RaceLength; } }
        long RaceLengthDisplayAddr { get { return numSessions == 2 ? Addr.RaceLengthDisplayQuickRace : Addr.RaceLengthDisplay; } }
        bool WhiteFlagOut { get { return lap == m.U16(RaceLengthAddr); } }

        // NR2003 has every car race back to the line when the caution comes out. Marking every car as having
        // raced back freezes the field in the order it had at the yellow.
        void FreezeField()
        {
            if (fieldFrozen || flag != 0 || WhiteFlagOut || stageCaution) return;
            fieldFrozen = true;
            for (int idx = 0; idx < 48; idx++) m.W8(Addr.RacedBack + idx, 1);
        }

        // 3.3.0 (run H, North Wilkesboro 2026-11-09, lap 14): with every car marked as raced back, the game lines the field
        // up in the order the cars are behind the start/finish line, whatever lap they are on. When the yellow comes out
        // with the field spread across the line (the leader and 15 cars past it, the other 20 still coming to it) the cars
        // still coming to the line go to the front: the leader got row 21, stopped dead at the line to let 20 cars by,
        // sat there until it was towed (5 of the top 8 were), and the pace car picked up the 9th-11th cars. The same in
        // run F lap 71 (leader last in the line), run G lap 67 (row 24) - both read then as "the leader stopped in the
        // wreck". Every caution with the leader first in the game's line was clean. So during the yellow the line is
        // put in running order on track from the leader back (what the game builds when the field is not across the
        // line), cars on pit road last; the game's own line is left alone whenever the leader is already first.
        // 3.3.5 (run K lap 22): the order is taken at the moment of the yellow, not when the game builds its line. There
        // the line came 13 s after the yellow; the leader (#24) had slowed more than the cars behind it, #10, #38 and #20
        // had gone by it, and "on track from the leader back" put them a lap behind it: they went to the end and #9 and
        // the rest followed the leader past them. Now: the cars on track in their order behind the leader at the yellow
        // (lapped cars where they were among the others), cars that stop (in the wreck: under 4 m/s for 2 s while the
        // field is moving) to the end, then cars not in that order (off pit road) by where they are, pit road last.
        List<int> yellowOrder;
        HashSet<int> yellowSlow = new HashSet<int>();
        Dictionary<int, double> slowSince = new Dictionary<int, double>();
        Dictionary<int, KeyValuePair<double, float>> lineSpeedAt = new Dictionary<int, KeyValuePair<double, float>>();
        Dictionary<int, double> lineSpeed = new Dictionary<int, double>();
        int lineFixes;
        double lineFixLogAt = double.NaN;

        // 3.3.10 (run P lap 11, the user): a car on pit road when the yellow comes out (#20 had been pushed onto it in the
        // wreck) gives up its spot like a car stopped in the wreck: to the end of the line, and the lineup after the pit
        // stops keeps it behind the other cars of its group (lead lap / lapped) however far up it drives. Before, it was
        // put in "where it is now", and it drove through a stalled half of the field (cars the game had flagged as
        // lagging may be passed) back up to 10th.
        HashSet<int> yellowPit = new HashSet<int>();
        Dictionary<int, float> yellowD = new Dictionary<int, float>();     // dlong of each car at the yellow
        Dictionary<long, double> tieSince = new Dictionary<long, double>();
        // Dry run on 14 North Wilkesboro cautions: with 1 m / 3 s it fired 14-27 times a caution (two-wide cars settling,
        // swapped and swapped back); with "8-40 m in front, crawling (under 15 m/s), 5 s, never the same pair twice" it
        // fires 0-3 times, and at lap 11 on #23/#16 (t+14) and #5/#33 (t+15).
        const double TieAtYellow = 15, TieNear = 8, TieAhead = 40, TieWait = 5, TieCrawl = 15;
        HashSet<long> tieSwapped = new HashSet<long>();

        // in the wreck or on pit road at the yellow: it has given up its spot
        bool Forfeited(int idx) { return yellowPit.Contains(idx) || yellowSlow.Contains(idx); }

        void ResetCautionLine()
        {
            yellowOrder = null; yellowSlow.Clear(); slowSince.Clear(); lineSpeedAt.Clear(); lineSpeed.Clear();
            yellowPit.Clear(); yellowD.Clear(); tieSince.Clear(); tieSwapped.Clear();
            lineFixes = 0; lineFixLogAt = double.NaN;
        }

        // 3.3.10 (run P lap 11): #19, #33 and #5 were three wide within 3 m at the yellow. The order at the yellow had #33
        // in front of #5, but #5 came out of the scramble 16 m ahead. The game has a car in front of the car it follows
        // wait for it (the crawl), #33 could not get by #5, and the 25 cars behind crawled at 12 m/s while the front 9 ran
        // on: 687 m apart. (#23 and #16 the same.) So two cars that were side by side at the yellow (within 15 m) and
        // are now the other way round on track (the one behind in the line 8-40 m in front, crawling for it) for 5 s
        // swap in the order at the yellow: they keep the order they have on track. Cars further apart at the yellow
        // keep the order.
        void UntangleYellowTies(double trackLen)
        {
            Func<int, bool> active = i => Running(i) && !OnPitRoad(i) && !yellowSlow.Contains(i);
            var order = yellowOrder.Where(active).ToList();
            var seen = new HashSet<long>();
            for (int k = 0; k + 1 < order.Count; k++)
            {
                int a = order[k], b = order[k + 1];       // a is in front of b in the line
                float ya, yb;
                if (!yellowD.TryGetValue(a, out ya) || !yellowD.TryGetValue(b, out yb)) continue;
                double atYellow = Mod(ya - yb, trackLen);
                if (atYellow > trackLen / 2) atYellow -= trackLen;
                if (Math.Abs(atYellow) > TieAtYellow) continue;
                double now = Mod(DlongOf(b) - DlongOf(a), trackLen);  // how far b is in front of a on track
                if (now > TieAhead || now < TieNear || CarSpeed(b) >= TieCrawl) continue;
                long pair = a * 1000L + b;
                if (tieSwapped.Contains(pair) || tieSwapped.Contains(b * 1000L + a)) continue;
                seen.Add(pair);
                double since;
                if (!tieSince.TryGetValue(pair, out since)) { tieSince[pair] = Now; continue; }
                if (Now - since < TieWait) continue;
                int ia = yellowOrder.IndexOf(a), ib = yellowOrder.IndexOf(b);
                yellowOrder[ia] = b; yellowOrder[ib] = a;
                tieSwapped.Add(pair);
                seen.Remove(pair);
                Log(string.Format("caution line: #{0} is {1:0} m in front of #{2} on track (side by side at the yellow, {3:0} m apart): they keep the order they have on track",
                    CarNumber(b), now, CarNumber(a), Math.Abs(atYellow)));
                k++;   // one swap per pair per poll
            }
            foreach (long p in tieSince.Keys.Where(p => !seen.Contains(p)).ToList()) tieSince.Remove(p);
        }

        void TakeYellowOrder()
        {
            if (yellowOrder != null) return;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            Car leader, first;
            List<Car> cars = Field(out leader, out first);
            if (cars == null || leader == null) return;
            float ld = DlongOf(leader.Idx);
            yellowOrder = cars.Where(c => !OnPitRoad(c.Idx)).OrderBy(c => Mod(ld - DlongOf(c.Idx), trackLen)).Select(c => c.Idx).ToList();
            Log("caution line: order at the yellow, from leader #" + leader.Num + ":" + string.Concat(yellowOrder.Select(i => " #" + CarNumber(i))));
            foreach (int i in yellowOrder) yellowD[i] = DlongOf(i);
            var onPit = new List<int>();
            for (int idx = 0; idx <= maxIdx; idx++)
                if (idx != Addr.PaceCarIdx && Running(idx) && OnPitRoad(idx) && yellowPit.Add(idx)) { yellowSlow.Add(idx); onPit.Add(idx); }
            if (onPit.Count > 0)
                Log("caution line: on pit road at the yellow, they give up their spot like a car in the wreck (to the end of the line, and behind their group in the lineup after the pit stops):" +
                    string.Concat(onPit.Select(i => " #" + CarNumber(i))));
        }

        // speed from the car's own progress (the player's velocity reads 0), over half a second or more
        void WatchLineSpeeds(double trackLen)
        {
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx)) continue;
                float d = DlongOf(idx);
                KeyValuePair<double, float> was;
                if (!lineSpeedAt.TryGetValue(idx, out was)) { lineSpeedAt[idx] = new KeyValuePair<double, float>(Now, d); continue; }
                if (Now - was.Key < 0.5) continue;
                double moved = Mod(d - was.Value, trackLen);
                if (moved > trackLen / 2) moved = 0;               // went backwards (towed, reset)
                lineSpeed[idx] = moved / (Now - was.Key);
                lineSpeedAt[idx] = new KeyValuePair<double, float>(Now, d);
            }
        }

        void FixCautionLine()
        {
            TakeYellowOrder();
            if (yellowOrder == null) return;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            WatchLineSpeeds(trackLen);
            // stopped in the wreck: to the end (a stopped car kept in its spot holds up every car behind it in the line)
            var moving = lineSpeed.Where(kv => !OnPitRoad(kv.Key)).Select(kv => kv.Value).OrderBy(v => v).ToList();
            double median = moving.Count > 0 ? moving[moving.Count / 2] : 0;
            foreach (var kv in lineSpeed)
            {
                if (yellowSlow.Contains(kv.Key) || OnPitRoad(kv.Key)) continue;
                if (kv.Value < 4 && median > 8)
                {
                    double since;
                    if (!slowSince.TryGetValue(kv.Key, out since)) slowSince[kv.Key] = Now;
                    else if (Now - since >= 2)
                    {
                        yellowSlow.Add(kv.Key);
                        Log(string.Format("caution line: #{0} has stopped ({1:0} m/s, the field {2:0}): to the end of the line", CarNumber(kv.Key), kv.Value, median));
                    }
                }
                else slowSince.Remove(kv.Key);
            }
            if (m.U8(Addr.NumPacingLines) != 1) return;
            UntangleYellowTies(trackLen);
            List<int> line = CurrentLine();
            if (line.Count < 2) return;
            Func<int, bool> onTrack = i => Running(i) && !OnPitRoad(i);
            var want = yellowOrder.Where(i => onTrack(i) && !yellowSlow.Contains(i) && line.Contains(i)).ToList();
            if (want.Count == 0) return;
            int lead = want[0];
            float ld = DlongOf(lead);
            // not in the order at the yellow (were on pit road then): where they are now behind the leader
            foreach (int i in line.Where(i => onTrack(i) && !yellowSlow.Contains(i) && !want.Contains(i)).OrderBy(i => Mod(ld - DlongOf(i), trackLen)))
            {
                double gi = Mod(ld - DlongOf(i), trackLen);
                int at = want.FindIndex(w => w != lead && Mod(ld - DlongOf(w), trackLen) > gi);
                want.Insert(at < 0 ? want.Count : at, i);
            }
            want.AddRange(line.Where(i => onTrack(i) && yellowSlow.Contains(i)));
            want.AddRange(line.Where(i => !want.Contains(i)));
            if (want.SequenceEqual(line)) return;
            int row = line.IndexOf(lead) + 1;
            int moved = want.Where((i, k) => line.IndexOf(i) != k).Count();
            WriteLine(want);
            lineFixes++;
            if (lineFixes == 1 || double.IsNaN(lineFixLogAt) || Now - lineFixLogAt >= 5)
            {
                lineFixLogAt = Now;
                Log(string.Format("caution line: the game had leader #{0} in row {1} behind #{2}, {3} cars moved: put back in the order at the yellow{4}",
                    CarNumber(lead), row, CarNumber(line[0]), moved, lineFixes > 1 ? string.Format(" ({0} times this caution)", lineFixes) : ""));
            }
        }

        // 3.3.2 (run I, North Wilkesboro 2026-11-09): each car's pace lap goes up as it crosses the line under the yellow,
        // like the pace car's. A car with the pace car's pace lap that the pace car has not picked up crawls at 0.6 of the
        // pace speed (12.3 m/s there); one with a pace lap less runs at catch-up speed (30-45 m/s) and closes on its spot
        // in the line (lap 14: #2, not across the line yet, closed from 300 m back to 11 m behind its car in 20 s). The
        // tail of the field crossed the line just after the yellow, got the pace car's pace lap, and crawled 100-450 m
        // apart for the whole hold and pickup: the field filled 930 m of the 1020 m lap, the pace car came out among the
        // tail cars and the leaders followed it through them. So until the field is packed, a car more than TailFar
        // behind the car in front of it in the line gets a pace lap less (it catches up), and its own back within
        // TailNear (3.3.6: eases off sooner, see below); the cars right behind a catching car go with it. The leader, cars on pit road and cars waved/sent
        // around are left alone. Off: TailCatchUp = false.
        public bool TailCatchUp = true;
        const double TailFar = 80, TailEaseOff = 20, TailBrake = 3.5;
        Dictionary<int, int> tailLowered = new Dictionary<int, int>();   // car -> pace laps taken off

        void TailCatchUpTick()
        {
            if (m.U8(Addr.NumPacingLines) != 1) { RestoreTail("double file"); return; }
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            List<int> line = CurrentLine();
            if (line.Count < 2) return;
            int pace = m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd);
            bool pcOut = PaceStatesOk() && PaceCarOnTrack();
            float pcD = DlongOf(Addr.PaceCarIdx);
            int ahead = -1;   // the car in front in the line that is on track
            bool clumpBack = false;   // 3.3.6: the car in front, catching up, was just given its pace lap back
            foreach (int idx in line)
            {
                bool skip = !Running(idx) || OnPitRoad(idx) || eligible.ContainsKey(idx) || m.U8(Addr.PacingWaved + idx * 13) != 0
                    || chaseHeld.Contains(idx);
                if (skip || ahead < 0)
                {
                    if (tailLowered.ContainsKey(idx)) RestoreOne(idx, skip ? "pit road or going around" : "first in the line");
                    if (!skip) ahead = idx;
                    clumpBack = false;
                    continue;
                }
                long a = Addr.PaceLap + idx * Addr.Ocd;
                double gap = Mod(DlongOf(ahead) - DlongOf(idx), trackLen);   // metres behind the car in front of it in the line
                // 3.3.5: its spot must be in front of it with the pace car not in between. A car with a pace lap under the
                // pace car's drives forward to its spot whatever is in the way: run J lap 33 #34 had its spot moved 350 m
                // BEHIND it (lapped, to the end of the line), counted as "666 m in front", and went round the pace car and
                // a whole lap. And with the pace car out it is the car's own spot it catches, not the pace car (Michigan
                // run K: #16 and the 17 cars behind it crossed the line just after the pace car left, were never picked up
                // and crawled at 10 m/s while the rest ran at 70; 3.3.3-3.3.4 had stopped helping once the pace car was out).
                double toPace = Mod(pcD - DlongOf(idx), trackLen);
                bool behindIt = gap < trackLen * 0.5 && (!pcOut || gap < toPace);
                int natural = m.I32(a) + (tailLowered.ContainsKey(idx) ? tailLowered[idx] : 0);
                // a car right behind (or just in front of) one that is catching up goes with it: the tail is in clumps,
                // and only the first car of each clump is far from the car in front of it (run I lap 14)
                bool aheadCatching = tailLowered.ContainsKey(ahead);
                double closing = CarSpeed(idx) - CarSpeed(ahead);
                if (double.IsNaN(closing) || closing < 0) closing = 0;
                double room = TailEaseOff + closing * closing / (2 * TailBrake);   // 3.3.6: where it eases off (below)
                if (tailLowered.ContainsKey(idx))
                {
                    // 3.3.6 (run L lap 32): a catching car drives for its spot behind the PACE CAR at 30-45 m/s, not for
                    // the car in front of it. The lead pack (#17 and 11 cars behind it, 12-18 m apart) was sent after
                    // leader #7 as the pace car came out; #7 ran 21-27 m behind the pace car, behind its own spot, and
                    // the pack went past it and over each other. So the first car of a catching clump eases off (its
                    // pace lap back) while it still has room to slow to the car in front: 20 m plus its braking distance
                    // for the speed it is closing at (the AI slowed only ~3.5 m/s^2 there: #17 38 -> 24 m/s in 4 s), and
                    // the cars right behind it ease off with it. The game picks a crawling car up
                    // once it is within about 30-40 m of a picked-up car in front of it (run L traces). It is helped
                    // again only past TailFar, so it does not go on and off every second (run J).
                    if (!behindIt) RestoreOne(idx, "its spot is not in front of it");
                    else if (natural < pace) RestoreOne(idx, "its own pace lap is under the pace car's now");
                    else if (clumpBack) RestoreOne(idx, string.Format("easing off with #{0} in front of it", CarNumber(ahead)));
                    else if (!aheadCatching && gap <= room)
                    {
                        RestoreOne(idx, string.Format("{0:0} m behind #{1}, closing at {2:0} m/s: eases off to slot in behind it", gap, CarNumber(ahead), closing));
                        clumpBack = true;
                    }
                }
                // not while it is still closing in after easing off (3.3.7, run M lap 21: with the same distance both ways
                // #23 went on and off every poll from 101 m to 79 m as its closing speed wobbled 20-24 m/s)
                else if (behindIt && (aheadCatching || (gap > TailFar && closing < 3)) && m.I32(a) >= pace)
                {
                    // to one under the pace car's: a car that crossed the line before the pace car is one over it
                    int by = m.I32(a) - (pace - 1);
                    tailLowered[idx] = by;
                    m.W32(a, m.I32(a) - by);
                    clumpBack = false;
                    Log(string.Format("tail catch-up: #{0} is {1:0} m behind #{2} in the line{3}: pace lap under the pace car's so it catches up", CarNumber(idx),
                        gap, CarNumber(ahead), aheadCatching ? " (catching up with it)" : ""));
                }
                else clumpBack = false;
                ahead = idx;
            }
            foreach (int idx in tailLowered.Keys.Where(i => !line.Contains(i)).ToList()) RestoreOne(idx, "out of the line");
        }

        void RestoreOne(int idx, string why)
        {
            int by;
            if (!tailLowered.TryGetValue(idx, out by)) return;
            tailLowered.Remove(idx);
            try { long a = Addr.PaceLap + idx * Addr.Ocd; m.W32(a, m.I32(a) + by); } catch (GameMemoryException) { return; }
            Log(string.Format("tail catch-up: #{0} back to its own pace lap ({1})", CarNumber(idx), why));
        }

        void RestoreTail(string why)
        {
            foreach (int idx in tailLowered.Keys.ToList()) RestoreOne(idx, why);
        }

        // 3.3.8 (run O, 2026-11-09): pace laps that match the line. Read from NR2003.exe: under the yellow a car follows the
        // first car in an earlier row of its line that the game has not flagged as lagging (pacing line 0x76ACF8, lookup
        // 0x414380), and the distance it works with (0x52B070) is
        //     (that car's pace lap - its own) x track length + that car's dlong - its own,
        // with no wrap. More than 30 m short of its spot it accelerates (2.9 m/s^2, braking only to match the car in
        // front); at or past its spot it eases to half the pace speed + 5 mph, the crawl (0x52B120). So the pace lap is
        // only the lap counter that makes this sum right. The game sets every car's to 0 at the yellow (0x418099) and adds
        // one at each crossing of the line: right for racing back to the line, wrong for a field frozen in running order
        // whenever the line cuts through it. Atlanta 2026-11-09 lap 131 (trace atl2, with the game's own acceleration
        // command recorded): the four tail cars, just in front of the leader on track, crossed the line at the yellow
        // (pace lap 1) while the car they follow, 600 m up the road, had not (0): they read it as 1850 m BEHIND them and
        // were told to crawl; one sat in the lead pack for a minute while the line went by it. Cars whose pace lap gave
        // their real gap formed up 9-10 m apart on their own. Tail catch-up (3.3.2-3.3.7) was a rough version of this
        // with thresholds and its own errors: it held rows 15-31 a lap low after they had crossed the line (a car 238 m
        // ahead read as 2693 m: no braking, so they had to be eased off by hand, on and off) and left three cars a pace
        // lap too high. So, while the field is frozen and single file (flags 0-1), every car on track behind the first
        // gets the pace lap that makes the game's sum equal its real gap to the car it follows: forward along the track
        // when that is half a lap or less, else that car counts as behind it (it waits for it). The game adds one at
        // each crossing as before, so a value set once stays right; it is checked every poll all the same (at flag 0 the
        // game does not count a crossing while the pace car is rolling down pit road, 0x416D26). Left alone: the first
        // car (the hold and the chase place it against the pace car), cars on pit road, cars going round the pace car,
        // a car the chase holds, and any pair with a car within 25 m of the line (dlong and pace lap change there one
        // after the other). A change is written only when two polls running ask for the same value.
        // Tail catch-up does not run while this is on. Off: ini PaceLapFit=0 (3.3.7's tail catch-up and chase).
        public bool PaceLapFit = true;
        const double FitLineEdge = 25;
        Dictionary<int, int> fitPending = new Dictionary<int, int>();    // car -> the pace lap asked for on the last poll
        int fitWrites;

        void PaceLapFitTick()
        {
            if (m.U8(Addr.NumPacingLines) != 1) { fitPending.Clear(); return; }
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            List<int> line = CurrentLine();
            if (line.Count < 2) return;
            var asked = new Dictionary<int, int>();
            var done = new List<string>();
            int ahead = -1, aheadPl = 0;      // the car the next one follows, and the pace lap it has (or is about to get)
            foreach (int idx in line)
            {
                if (!Running(idx)) continue;
                bool lagging = m.U8(Addr.Lagging + idx) != 0;        // the game skips it as a car to follow
                long a = Addr.PaceLap + idx * Addr.Ocd;
                int pl = m.I32(a);
                if (ahead < 0)
                {
                    if (lagging) continue;
                    // 3.3.9: the first car against the pace car (the game's row 0)
                    int lw = LeaderFitPaceLap(idx, pl, trackLen);
                    if (lw != pl)
                    {
                        int before;
                        if (fitPending.TryGetValue(idx, out before) && before == lw)
                        {
                            m.W32(a, lw);
                            fitWrites++;
                            done.Add(string.Format("#{0} {1}->{2} (first in line, {3:0} m behind the pace car)", CarNumber(idx), pl, lw,
                                Mod(DlongOf(Addr.PaceCarIdx) - DlongOf(idx), trackLen)));
                        }
                        else asked[idx] = lw;
                    }
                    ahead = idx; aheadPl = lw;
                    continue;
                }
                int target = ahead, targetPl = aheadPl;
                int want = pl;
                bool leave = OnPitRoad(idx) || OnPitRoad(target) || chaseHeld.Contains(idx)
                    || eligible.ContainsKey(idx) || m.U8(Addr.PacingWaved + idx * 13) != 0;
                float d = DlongOf(idx), td = DlongOf(target);
                if (!leave && d >= FitLineEdge && d <= trackLen - FitLineEdge && td >= FitLineEdge && td <= trackLen - FitLineEdge)
                {
                    double fwd = Mod(td - d, trackLen);              // metres to the car it follows, forward along the track
                    if (fwd > trackLen / 2) fwd -= trackLen;         // more than half a lap: it is in front of that car
                    want = targetPl - (int)Math.Round((fwd - (td - d)) / trackLen);
                    if (want != pl)
                    {
                        int before;
                        if (fitPending.TryGetValue(idx, out before) && before == want)
                        {
                            m.W32(a, want);
                            fitWrites++;
                            done.Add(string.Format("#{0} {1}->{2} ({3:0} m {4} #{5})", CarNumber(idx), pl, want, Math.Abs(fwd),
                                fwd >= 0 ? "behind" : "in front of", CarNumber(target)));
                        }
                        else asked[idx] = want;
                    }
                }
                if (!lagging) { ahead = idx; aheadPl = want; }
            }
            fitPending = asked;
            if (done.Count > 0)
                Log(string.Format("pace lap fit: {0} car(s) given the pace lap that matches their gap to the car they follow in the line: {1}",
                    done.Count, string.Join(", ", done.ToArray())));
        }

        // 3.3.9 (run P): the first car in line follows the pace car. Its pace lap is set so the game's sum is its gap
        // forward to the pace car (parked, waiting, on pit road or on track), so the whole line agrees with the pace car
        // and a car on track between the pace car and the leader comes out a pace lap down by the same rule: it reads
        // the pace car a lap further on, may pass it (0x403920) and drives on round to its place at the back. One
        // exception: a leader less than 15% of a lap in front of a pace car that is out and moving waits for it (the
        // pace car catches it from behind; 3.3.10: whatever the pace car's state). Left alone (returns pl): pit road, a car the chase holds, a waved car,
        // either car within 25 m of the line, no pace car states.
        const double LeaderJustPast = 0.15;   // share of a lap: a leader this far in front of the pace car waits for it

        int LeaderFitPaceLap(int idx, int pl, double trackLen)
        {
            if (!PaceStatesOk() || OnPitRoad(idx) || chaseHeld.Contains(idx) || eligible.ContainsKey(idx) ||
                m.U8(Addr.PacingWaved + idx * 13) != 0) return pl;
            float d = DlongOf(idx), pd = DlongOf(Addr.PaceCarIdx);
            if (d < FitLineEdge || d > trackLen - FitLineEdge || pd < FitLineEdge || pd > trackLen - FitLineEdge) return pl;
            int ppl = m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd);
            double fwd = Mod(pd - d, trackLen);
            // 3.3.10 (run P lap 46): also while the pace car is parked or on pit road. Leader #34 was 87 m past the stall at
            // the yellow, was told to drive a lap round to it, the game sent the pace car out behind it at once and the
            // two chased each other: pit road opened on the third time by, 100 s after the yellow.
            if (fwd > trackLen * (1 - LeaderJustPast)) fwd -= trackLen;
            return ppl - (int)Math.Round((fwd - (pd - d)) / trackLen);
        }

        // 3.3.9: a car sent or waved around the pace car reads the pace car one lap further on than it is on track
        // (what NR2003's own wave does with "pace lap - 1" when the pace laps were right to start with). Set, not
        // subtracted: a car the fit had already put a lap down (in front of the leader on track) went two down and
        // would have run an extra lap past the field.
        int PaceLapOneDown(int idx)
        {
            double trackLen = m.F64(Addr.TrackLength);
            long a = Addr.PaceLap + idx * Addr.Ocd;
            if (trackLen <= 100) return m.I32(a) - 1;
            float d = DlongOf(idx), pd = DlongOf(Addr.PaceCarIdx);
            int ppl = m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd);
            double fwd = Mod(pd - d, trackLen);
            return ppl - 1 - (int)Math.Round((fwd - (pd - d)) / trackLen);
        }

        // 3.3.9: a car that gave up going around (dropped back, or the free pass that did not get by in time) gets the
        // pace lap the fit gives it against the car it now follows in the line (nearest earlier row, not lagging, else
        // the pace car), in place of "+1": it waits for that car when it is in front of it, so the field goes by it.
        // Null when it is not in the line.
        int? LinePaceLap(int idx)
        {
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return null;
            List<int> line = CurrentLine();
            int pos = line.IndexOf(idx);
            if (pos < 0) return null;
            int target = Addr.PaceCarIdx;
            for (int i = pos - 1; i >= 0; i--)
            {
                int o = line[i];
                if (Running(o) && m.U8(Addr.Lagging + o) == 0) { target = o; break; }   // the game's own lookup (0x414380)
            }
            float d = DlongOf(idx), td = DlongOf(target);
            int tpl = m.I32(Addr.PaceLap + target * Addr.Ocd);
            double fwd = Mod(td - d, trackLen);
            if (fwd > trackLen / 2) fwd -= trackLen;
            return tpl - (int)Math.Round((fwd - (td - d)) / trackLen);
        }

        void EndPaceLapFit()
        {
            if (fitPending.Count > 0) fitPending.Clear();
            if (flag == 7 && fitWrites > 0) fitWrites = 0;
        }

        // A caution in the last two laps would end the race under yellow: push the finish out to two laps
        // from now, as many times as it takes.
        void ExtendForGreenWhiteCheckered()
        {
            if (flag > 6 || WhiteFlagOut) return;
            int len = m.U16(RaceLengthAddr);
            if (len < lap || lap + 2 <= len) return; // a length below the current lap is not the race length (multiplayer?)
            m.W16(RaceLengthAddr, lap + 2);
            m.W16(RaceLengthDisplayAddr, lap + 2);
            Log("green-white-checkered: race extended to " + (lap + 2) + " laps");
        }

        // A car of class 0 (what the pace car is) has no contact with other cars: NR2003's car-to-car contact
        // test (0x549290) gives up on a pair when either car is class 0. Better Cautions switched contact back on
        // for the whole field at the green; a car still overlapping another at that moment is launched. Here
        // contact comes back per car, once it is clear of every other car (or after 5 s regardless).
        void ManageCollisions()
        {
            bool pacing = flag >= 2 && flag <= 6;
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                byte load = m.U8(Addr.CarClassLoad + idx * Addr.Ocd);
                if (load == 0) continue; // the pace car, empty slots
                long a = Addr.CarClass + idx * Addr.Ocd;
                byte cur = m.U8(a);
                bool ghost = (GhostOnPitRoad && OnPitRoad(idx)) || (GhostWhilePacing && pacing);
                if (ghost)
                {
                    ghostEndWanted.Remove(idx);
                    if (cur != 0) m.W8(a, 0);
                    continue;
                }
                if (cur != 0) continue;
                double since;
                if (!ghostEndWanted.TryGetValue(idx, out since)) ghostEndWanted[idx] = since = Now;
                if (Now - since < 5 && Overlapping(idx)) continue;
                m.W8(a, load);
                ghostEndWanted.Remove(idx);
            }
        }

        // Every ghosted car gets its own class back at once (Race Control closing: nothing would put it back later).
        void RestoreContact()
        {
            int n = 0;
            for (int idx = 0; idx < 48; idx++)               // the game's 48 car slots
                try
                {
                    byte load = m.U8(Addr.CarClassLoad + idx * Addr.Ocd);
                    if (load == 0 || m.U8(Addr.CarClass + idx * Addr.Ocd) != 0) continue;
                    m.W8(Addr.CarClass + idx * Addr.Ocd, load);
                    n++;
                }
                catch (GameMemoryException) { return; }
            ghostEndWanted.Clear();
            if (n > 0) Log("car-to-car contact back on for " + n + " car(s) (RaceControl closing)");
        }

        bool Overlapping(int idx)
        {
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return false;
            float d = DlongOf(idx), lat = DlatOf(idx);
            for (int o = 0; o <= maxIdx; o++)
            {
                if (o == idx || o == Addr.PaceCarIdx || !Running(o)) continue;
                double dl = Math.Abs(Mod(DlongOf(o) - d + trackLen / 2, trackLen) - trackLen / 2);
                if (dl < 5.5 && Math.Abs(DlatOf(o) - lat) < 2.2) return true;
            }
            return false;
        }

        // A stage ends when the leader completes the stage's last lap: the overlay app running the stages throws
        // the caution right then. So a caution is a stage caution when the laps completed are a stage end lap and
        // the leader has only just crossed the line (a crash later in that lap is a normal caution).
        int[] CurrentStageEnds()
        {
            if (!StageRacing || StageEnds == null) return null;
            try { return StageEnds(m.U16(RaceLengthAddr)); }
            catch (Exception) { return null; }
        }

        // The leader's laps done and how far round its lap it is, or false.
        bool LeaderOnLap(out int done, out double share)
        {
            done = m.I32(Addr.LapsComplete);
            share = 0;
            double trackLen = m.F64(Addr.TrackLength);
            Car leader, first;
            if (trackLen <= 100 || Field(out leader, out first) == null) return false;
            share = DlongOf(leader.Idx) / trackLen;
            return true;
        }

        // Throws the stage caution 3/4 through the stage's last lap (once per stage). A caution already out then
        // (a wreck) is the stage caution: the stage ends under it.
        void StageCautionTick()
        {
            int[] ends = CurrentStageEnds();
            int len = m.U16(RaceLengthAddr);
            if (len != stageRaceLength) { stageRaceLength = len; stagesDone.Clear(); }
            if (ends == null || ends.Length == 0) return;
            int done;
            double share;
            if (!LeaderOnLap(out done, out share)) return;
            if (done < stageLastDone - 1) stagesDone.Clear(); // a new race
            stageLastDone = done;
            int end = done + 1;
            if (!ends.Contains(end) || stagesDone.Contains(end)) return;
            if (flag != 7) { stagesDone.Add(end); Log("stage " + StageNumber(ends, end) + " ends on lap " + end + " under the caution already out"); return; }
            if (!StageThrowCaution || share < StageCautionAt) return;
            stagesDone.Add(end);
            stageThrown = end;
            m.W8(Addr.Yellow, 1);
            Log(string.Format("stage {0} ends on lap {1}: caution with the leader {2:0%} through the lap; {3}",
                StageNumber(ends, end), end, share, StageRaceBack ? "the field races back to the line" : "the field is frozen (race back at stage ends is off)"));
        }

        // The track's length in metres (for the stage settings), NaN when not known.
        public double TrackLengthMetres
        {
            get
            {
                try { double v = m.F64(Addr.TrackLength); return InRace && v > 100 ? v : double.NaN; }
                catch (GameMemoryException) { return double.NaN; }
            }
        }

        // The race length in laps (for the stage settings), -1 when not known.
        public int RaceLengthLaps
        {
            get
            {
                try { return InRace ? m.U16(RaceLengthAddr) : -1; }
                catch (GameMemoryException) { return -1; }
            }
        }

        static int StageNumber(int[] ends, int end) { return ends.OrderBy(e => e).ToList().IndexOf(end) + 1; }

        // Is the caution that just came out the stage caution? Race Control's own, or one another app threw on the
        // stage's last lap from 3/4 of the way round, or at the line (the leader at most a quarter lap past it).
        bool IsStageEnd()
        {
            int thrown = stageThrown;
            stageThrown = -1;
            if (thrown > 0) return true;
            int[] ends = CurrentStageEnds();
            if (ends == null || ends.Length == 0) return false;
            int done;
            double share;
            if (!LeaderOnLap(out done, out share)) return false;
            int end = ends.Contains(done) && share <= 0.25 ? done : ends.Contains(done + 1) && share >= StageCautionAt ? done + 1 : -1;
            if (end < 0)
            {
                if (ends.Contains(done) || ends.Contains(done + 1))
                    Log(string.Format("caution near the stage end (leader {0:0%} round lap {1}): a normal caution, the field is frozen", share, done + 1));
                return false;
            }
            stagesDone.Add(end);
            Log(string.Format("stage {0} ends on lap {1} (caution from another app): {2}", StageNumber(ends, end), end,
                StageRaceBack ? "the field races back to the line" : "the field is frozen (race back at stage ends is off)"));
            return true;
        }

        void CautionsTick()
        {
            if (throwCaution)
            {
                throwCaution = false;
                if (flag == 7) { m.W8(Addr.Yellow, 1); Log("caution thrown from Race Control"); }
            }
            if (flag == 7) { fieldFrozen = cautionSeen = stageCaution = false; ResetCautionLine(); }
            if (flag != 7 && !cautionSeen)
            {
                cautionSeen = true;
                stageCaution = StageRacing && IsStageEnd() && StageRaceBack;
            }
            if (StageRacing) StageCautionTick();
            if (!RaceBack && !StockCautions) FreezeField();
            if (fieldFrozen && PaceFixes && flag <= 1) FixCautionLine();
            // 3.3.6: cars in front of a pace car sent out behind the leader crawl so it catches them (PaceCar.cs)
            if (fieldFrozen && PaceFixes && flag <= 2) ChaseTick(); else EndChase(flag == 7 ? "green" : "flag " + flag);
            // 3.3.5: through flags 0-2 again, only for a car whose spot is in front of it with the pace car not between
            // 3.3.8: not while the pace laps are fitted to the line (PaceLapFit)
            if (fieldFrozen && PaceFixes && TailCatchUp && !PaceLapFit && flag <= 2) TailCatchUpTick();
            else if (tailLowered.Count > 0) RestoreTail(flag == 7 ? "green" : PaceLapFit ? "pace laps fitted to the line instead" : "the field is packed (flag " + flag + ")");
            // 3.3.8: after the chase (it places the leader against the pace car; the fit leaves a held car alone)
            if (fieldFrozen && PaceFixes && PaceLapFit && flag <= 1) PaceLapFitTick(); else EndPaceLapFit();
            if (flag == 7) paceGameLeadFresh = false;    // 3.3.9: read again when the pace car next starts waiting
            if (flag == 7 || StockCautions) { if (paceHolding || paceReleased || paceLogged) ResetPaceCar(); RestorePaceSpeed("green"); }
            else { PaceCarTick(); PaceMatchTick(); }
            if (GreenWhiteCheckered) ExtendForGreenWhiteCheckered();
            ManageCollisions();
        }
    }

    // Race Control's own stage settings: the number of stages (2-4, the last ends with the race) and where each
    // of the others ends, as laps or as % of the race length ("25, 61").
    public static class StageSettings
    {
        public static int[] Ends(int stages, bool percent, string values, int raceLength)
        {
            var list = new List<int>();
            foreach (string part in (values ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) || v <= 0) continue;
                int lap = percent ? (int)Math.Round(raceLength * v / 100.0, MidpointRounding.AwayFromZero) : (int)Math.Round(v);
                if (lap > 0 && (raceLength <= 0 || lap < raceLength) && !list.Contains(lap)) list.Add(lap);
            }
            list.Sort();
            return list.Take(Math.Max(1, Math.Min(4, stages)) - 1).ToArray();
        }

        // The default: stage ends by track length (% of the race), and four equal stages at Charlotte at night
        // (the 600).
        //   under 0.9 mi     21, 51        1.45 to 1.99 mi  28, 62
        //   0.9 to 1.45 mi   25, 61        1.99 mi and up   30, 63
        public static double[] ByTrack(double trackMetres, string trackName, out string why)
        {
            string name = (trackName ?? "").ToLowerInvariant();
            if (name.Contains("charlotte") && name.Contains("night"))
            {
                why = "Charlotte night: 4 stages at 25/50/75%";
                return new[] { 25.0, 50, 75 };
            }
            double miles = trackMetres / 1609.344;
            double[] p = miles < 0.9 ? new[] { 21.0, 51 } : miles < 1.45 ? new[] { 25.0, 61 } : miles < 1.99 ? new[] { 28.0, 62 } : new[] { 30.0, 63 };
            why = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} mi track: 3 stages at {1}/{2}%", miles, p[0], p[1]);
            return p;
        }

        public static int[] ByTrack(double trackMetres, string trackName, int raceLength)
        {
            string why;
            double[] p = ByTrack(trackMetres, trackName, out why);
            return Ends(p.Length + 1, true, string.Join(",", p.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()), raceLength);
        }
    }

    // Stage ends from a stage racing companion app (optional; Race Control's own stage settings need nothing else).
    // Reads the folder set as StageCompanionFolder=: the laps its bridge log
    // says it computed for this race ("stage-end laps computed: [84, 204] (of 334 total)"), else its config
    // (stageRacing: laps, or percentages of the race length). Read-only, re-read when the files change.
    public static class CompanionAppStages
    {
        public static readonly string DefaultFolder = "";
        static string dir = DefaultFolder;

        // The companion app's data folder (its stage racing settings and log). Null or empty: none.
        public static string Folder
        {
            get { return dir; }
            set
            {
                string v = string.IsNullOrEmpty(value) ? DefaultFolder : value;
                if (v == dir) return;
                dir = v;
                cfgTime = logTime = lastRefresh = DateTime.MinValue;
                fromLog = null; logTotal = -1; enabled = false; pcts.Clear(); laps.Clear();
            }
        }

        // What was found in the folder, for the window.
        public static string Status(int raceLength)
        {
            if (string.IsNullOrEmpty(dir)) return "no companion app folder set: choose it with Folder...";
            if (!Directory.Exists(dir)) return "companion app folder not found: choose it with Folder...";
            if (!File.Exists(Path.Combine(dir, "config.json"))) return "no stage racing settings in the chosen folder";
            Refresh();
            if (!enabled) return "the companion app's stage racing is off";
            return null;
        }
        static DateTime cfgTime, logTime;
        static int[] fromLog;
        static int logTotal = -1;
        static bool enabled;
        static string mode = "";
        static List<double> pcts = new List<double>();
        static List<int> laps = new List<int>();

        public static int[] Ends(int raceLength)
        {
            Refresh();
            if (!enabled) return null;
            if (fromLog != null && logTotal == raceLength) return Stages(fromLog, raceLength);
            if (mode == "laps" && laps.Count > 0) return Stages(laps, raceLength);
            if (raceLength <= 0) return null;
            return Stages(pcts.Select(p => (int)Math.Round(raceLength * p / 100.0, MidpointRounding.AwayFromZero)), raceLength);
        }

        // Stage ends only, as StageSettings gives them: the race's own last lap (a config listing 200 of 200, or 100 %) is
        // the finish, not a stage end - a caution there would end the race under yellow, and state.txt's stage_ends is
        // every stage end but the race's.
        static int[] Stages(IEnumerable<int> ends, int raceLength)
        {
            return ends.Where(l => l > 0 && (raceLength <= 0 || l < raceLength)).Distinct().OrderBy(l => l).ToArray();
        }

        static DateTime lastRefresh;

        // at most every 2 s: callers ask every tick, and the bridge log is large and written all the time
        static void Refresh()
        {
            if (string.IsNullOrEmpty(dir) || (DateTime.Now - lastRefresh).TotalSeconds < 2) return;
            lastRefresh = DateTime.Now;
            try
            {
                string cfg = Path.Combine(dir, "config.json");
                if (File.Exists(cfg) && File.GetLastWriteTime(cfg) != cfgTime)
                {
                    cfgTime = File.GetLastWriteTime(cfg);
                    string j = File.ReadAllText(cfg);
                    int at = j.IndexOf("\"stageRacing\"");
                    int end = at < 0 ? -1 : j.IndexOf('}', at);
                    string sr = at < 0 || end < 0 ? "" : j.Substring(at, end - at);
                    enabled = Regex.IsMatch(sr, "\"enabled\"\\s*:\\s*true");
                    Match mm = Regex.Match(sr, "\"inputMode\"\\s*:\\s*\"(\\w+)\"");
                    mode = mm.Success ? mm.Groups[1].Value : "";
                    pcts = Numbers(sr, "percentages").ToList();
                    laps = Numbers(sr, "laps").Select(x => (int)x).ToList();
                }
                string log = Path.Combine(dir, "bridge.log");
                if (File.Exists(log) && File.GetLastWriteTime(log) != logTime)
                {
                    logTime = File.GetLastWriteTime(log);
                    using (var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long n = Math.Min(f.Length, 2 << 20);
                        f.Seek(-n, SeekOrigin.End);
                        var buf = new byte[n];
                        int got = 0;
                        while (got < n) { int r = f.Read(buf, got, (int)n - got); if (r <= 0) break; got += r; }
                        string tail = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                        MatchCollection all = Regex.Matches(tail, @"stage-end laps computed: \[([0-9, ]*)\] \(of (\d+) total\)");
                        if (all.Count > 0)
                        {
                            Match last = all[all.Count - 1];
                            fromLog = last.Groups[1].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x.Trim())).ToArray();
                            logTotal = int.Parse(last.Groups[2].Value);
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        static IEnumerable<double> Numbers(string json, string key)
        {
            Match mm = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!mm.Success) return new double[0];
            return mm.Groups[1].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
