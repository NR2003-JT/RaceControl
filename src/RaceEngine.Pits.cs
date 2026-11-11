using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceControl
{
    // When pit road opens and where it starts. With PitsClosedFirstLap the pits stay closed from the caution until
    // just before the leader's second time by, so the field has a lap to get lined up behind the pace car first:
    // cars no longer pit before they even reach the pace car, which made wave around eligibility a guess. Each car's
    // "first time by" is the moment it passes pit entry with the pits open.
    public partial class RaceEngine
    {
        public bool PitsClosedFirstLap = true;
        public bool OpenWhenPacked = true;      // ... but open on the first time by when the field is already packed up
        public double PitEntry = double.NaN;    // metres from the line where pit road starts (learned, saved per track)
        public Action<double> PitEntryLearned;  // a new pit entry estimate, to save

        // Where cars heading for pit road move over to the pit lane, from the track's own settings (track.ini:
        // merge_to_pit_line_dlong of each pit lane, with that lane's pit_lane_start_dlong). A car has to be told
        // to pit before it gets there, which can be far ahead of pit lane proper: at Bristol the move over starts
        // 20 m past the line, pit lane proper at 341 m. Empty: the track's settings were not found.
        public List<double[]> PitMerges = new List<double[]>(); // { merge dlong, pit lane start dlong }
        string mergeLoggedFor;
        string openPointName = "pit entry";

        const double EntryFallback = 0.85;      // pit entry as a share of the lap until it has been seen
        const double OpenBeforeEntry = 0.25;    // pits open when the leader is this share of a lap from pit entry

        List<double> entrySamples = new List<double>();
        bool[] wasOnPitRoad = new bool[64];
        bool entrySeen, fallbackLogged;

        bool pitsOpen;                          // open to the cars this caution (by the game's timing or ours)
        bool closedLogged;
        HashSet<int> requestsCancelled = new HashSet<int>();
        int entryLeader = -1, leaderPasses;
        double leaderToEntry = -1;
        Dictionary<int, double> toEntry = new Dictionary<int, double>(); // car -> metres to pit entry, last tick
        HashSet<int> passedEntry = new HashSet<int>();                   // passed pit entry with the pits open
        HashSet<int> readyToDecide = new HashSet<int>();                 // ... and is 60 m past it

        double EntryPoint(double trackLen)
        {
            if (!double.IsNaN(PitEntry) && PitEntry >= 0 && PitEntry < trackLen) return PitEntry;
            if (!fallbackLogged)
            {
                fallbackLogged = true;
                Log(string.Format("pit entry not seen at this track yet: assuming {0:0} m ({1:0%} of the lap) until a car pits", trackLen * EntryFallback, EntryFallback));
            }
            return trackLen * EntryFallback;
        }

        // The point the pits open a quarter lap ahead of: the earliest move over onto a pit lane (the track's
        // settings), else pit entry.
        double OpenPoint(double trackLen)
        {
            double entry = EntryPoint(trackLen), best = entry, most = 0;
            string lane = null;
            foreach (double[] p in PitMerges)
            {
                if (p[0] < 0 || p[0] >= trackLen) continue;
                double before = Mod(entry - p[0], trackLen);
                if (before > trackLen / 2 || before <= most) continue; // not ahead of pit entry: a bad value
                most = before; best = p[0];
                lane = string.Format("{0:0} m, pit lane proper at {1:0} m", p[0], p[1]);
            }
            openPointName = lane != null ? "where cars move over for pit road" : "pit entry";
            string key = lane + "|" + trackLen;
            if (lane != null && mergeLoggedFor != key)
            {
                mergeLoggedFor = key;
                Log(string.Format("pits open from where cars move over for pit road (the track's settings: {0}), {1:0} m before pit entry", lane, most));
            }
            return best;
        }

        // Read a track.ini's pit lanes: { merge_to_pit_line_dlong, pit_lane_start_dlong } per [ pit_lane_N ].
        public static List<double[]> ReadPitMerges(string trackIni)
        {
            var lanes = new List<double[]>();
            double merge = double.NaN, start = double.NaN;
            bool inLane = false;
            Action flush = () => { if (inLane && !double.IsNaN(merge) && !double.IsNaN(start)) lanes.Add(new[] { merge, start }); };
            foreach (string raw in System.IO.File.ReadAllLines(trackIni))
            {
                string line = raw.Split(';')[0].Trim();
                if (line.StartsWith("["))
                {
                    flush();
                    inLane = line.Trim('[', ']', ' ', '\t').StartsWith("pit_lane", StringComparison.OrdinalIgnoreCase);
                    merge = start = double.NaN;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (!inLane || eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                double v;
                if (!double.TryParse(line.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                if (key == "merge_to_pit_line_dlong") merge = v;
                else if (key == "pit_lane_start_dlong") start = v;
            }
            flush();
            return lanes;
        }

        // A different track loaded in the same NR2003 (the engine lives as long as the game does): what was learned at the
        // last track is no use here. MainForm loads this track's saved PitEntry / PaceReleaseAt on its next tick.
        void NewTrack(string was, string now)
        {
            entrySamples.Clear(); entrySeen = false; fallbackLogged = false; mergeLoggedFor = null;
            for (int i = 0; i < wasOnPitRoad.Length; i++) wasOnPitRoad[i] = false;
            PitEntry = double.NaN;
            paceMergeOffset = paceExitTime = paceParkedAt = double.NaN;
            paceLeaderAvg = paceLeaderAtSend = double.NaN;
            highChecked = false;
            Log("track changed (" + was + " -> " + now + "): pit entry and pace car measurements start again for this track");
        }

        // Where cars drive onto pit road: the spot most samples agree on (a towed car "enters" at its stall).
        void LearnPitEntry()
        {
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            for (int idx = 0; idx <= maxIdx && idx < 64; idx++)
            {
                if (idx == Addr.PaceCarIdx) continue;
                bool on = Running(idx) && OnPitRoad(idx);
                if (on && !wasOnPitRoad[idx] && entrySeen) entrySamples.Add(DlongOf(idx));
                wasOnPitRoad[idx] = on;
            }
            entrySeen = true; // cars already on pit road when Race Control started are not samples
            if (entrySamples.Count > 15) entrySamples.RemoveRange(0, entrySamples.Count - 15);
            if (entrySamples.Count < 3) return;
            Func<double, double, double> dist = (a, b) => Math.Abs(Mod(a - b + trackLen / 2, trackLen) - trackLen / 2);
            double best = entrySamples.OrderBy(s => entrySamples.Sum(o => Math.Min(dist(s, o), 100.0))).First();
            if (!double.IsNaN(PitEntry) && dist(best, PitEntry) < 5) return;
            PitEntry = best;
            Log(string.Format("pit entry is at {0:0} m from the line ({1} cars seen)", best, entrySamples.Count));
            if (PitEntryLearned != null) PitEntryLearned(best);
        }

        // The game's pit road state word is left alone: changing it (clearing the open bits, or setting the 0x10
        // "closed" bit) held the yellow lights and the pace car's lights off until the cars were already pacing.
        // Pits are held closed by calling off the pit requests of cars not yet on pit road, and AIMOD is told
        // through a status file (it would otherwise keep asking its cars to pit).
        void HoldPitsClosed()
        {
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx) || OnPitRoad(idx) || m.U8(Addr.Pitting + idx * Addr.Ocd) == 0) continue;
                m.W8(Addr.Pitting + idx * Addr.Ocd, 0);
                if (requestsCancelled.Add(idx)) Log("pits closed: #" + CarNumber(idx) + "'s pit request called off");
            }
            if (!closedLogged) Log(OpenWhenPacked ? "pits closed until the field is packed up behind the pace car (at the latest the leader's second time by)"
                                                  : "pits closed until the leader's second time by");
            closedLogged = true;
            CloseGate();
            PublishPitsClosed(true);
        }

        // The game's own pit gate: a value per pit lane that its AI checks before turning onto pit road. Under a
        // caution it goes 0 (yellow out), 1 (lead lap only), 2 (open to all); it is only raised while below 2, so a
        // 3 stays put until flag 3, when the game sets 2 every frame (hence rewritten every tick). 3 = closed to
        // every car without touching the pit road lights. Lane 1's slot is written too: unused on one-lane tracks.
        bool gateClosed;

        void CloseGate()
        {
            string was = m.I32(Addr.PitGate) + "/" + m.I32(Addr.PitGate + 4);
            for (int lane = 0; lane < 2; lane++)
                if (m.I32(Addr.PitGate + lane * 4) != 3) m.W32(Addr.PitGate + lane * 4, 3);
            if (!gateClosed) Log("pit road gate closed for every car (the game had " + was + ")");
            gateClosed = true;
        }

        // ---- per-car gate -------------------------------------------------------------------------------
        // Pit road opens on the leader's second time by even with lead lap cars caught in front of the leader:
        // only those cars are kept out, with the game's lead-lap-only gate (lanes at 1) and its per-car byte at 0
        // (every other car 1). Each is let in when AIMOD lets go of its own hold on it (the same events: crossed
        // the line after passing the pace car, dropped back, pitting). Waiting a lap for them cost a caution lap in
        // 7 of 11 Bristol cautions and late in a race ran out the one-to-go hold with the lineup still unsorted.
        // From flag 3 the game sets the lanes to 2 every frame, so their pit requests are also called off.
        public bool GateCatchingCars = true;
        bool perCarGate;
        HashSet<int> gateLogged = new HashSet<int>();

        // Cars kept off pit road: caught in front of the leader and not yet sent around, or sent / waved around
        // and not yet back across the line.
        // Only cars caught in front when pit road opened: a car out of the pits ahead of the leader is not one.
        HashSet<int> stuckAtOpen = new HashSet<int>();
        bool stuckTaken;

        List<int> GatedCars()
        {
            var held = new List<int>();
            double glen = m.F64(Addr.TrackLength);
            foreach (var kv in eligible)
            {
                if (kv.Value.Pitted || !Running(kv.Key) || OnPitRoad(kv.Key)) continue;
                Settle gs;
                // WaveAtLine tracks: a wave around car (not the free pass car, not a lead lap send-around) may not pit
                // for the rest of this caution, so it cannot be judged or released while on pit road
                bool noPit = WaveAtLine && glen >= WaveAtLineMinLen && settle.TryGetValue(kv.Key, out gs) && gs.Waved && !gs.LeadLap && !gs.FreePass;
                if (!kv.Value.Restored || noPit) held.Add(kv.Key);
            }
            foreach (int idx in stuckAtOpen)
                if (!eligible.ContainsKey(idx) && !pitted.Contains(idx) && Running(idx) && !OnPitRoad(idx)) held.Add(idx);
            return held;
        }

        void PerCarGateTick()
        {
            bool want = GateCatchingCars && WaveAroundOn && pitsOpen && flag >= 1 && flag <= 4;
            if (want && !stuckTaken)
            {
                stuckTaken = true;
                List<Car> cars = Field();
                if (cars != null) foreach (Car c in cars) if (c.Stuck && !eligible.ContainsKey(c.Idx)) stuckAtOpen.Add(c.Idx);
                var first = GatedCars();
                if (first.Count > 0) Log("pit road stays shut for" + string.Concat(first.Select(i => CarNumber(i)).OrderBy(n => n).Select(n => " #" + n)) +
                    " (caught in front of the leader or going around the pace car) until each has crossed the line after passing the pace car");
                gateLogged.UnionWith(first);
            }
            List<int> held = want ? GatedCars() : new List<int>();
            foreach (int idx in gateLogged.Where(i => !held.Contains(i)).ToList())
            {
                gateLogged.Remove(idx);
                if (want) Log("pit road open for #" + CarNumber(idx) + " now");
            }
            if (held.Count == 0)
            {
                if (!perCarGate) return;
                perCarGate = false;
                RestoreGate();
                return;
            }
            perCarGate = true;
            for (int lane = 0; lane < 2; lane++)
                if (m.I32(Addr.PitGate + lane * 4) != 1) m.W32(Addr.PitGate + lane * 4, 1);
            for (int idx = 0; idx <= maxIdx && idx < 48; idx++)
            {
                bool shut = held.Contains(idx);
                if (!shut && !OpenPitsToAll) continue; // the game's own lead lap choice
                int v = shut ? 0 : 1;
                if (m.U8(Addr.PitGateCar + idx) != v) m.W8(Addr.PitGateCar + idx, (byte)v);
            }
            foreach (int idx in held)
            {
                if (gateLogged.Add(idx)) Log("pit road shut for #" + CarNumber(idx) + " (going around the pace car) until it has crossed the line after passing it");
                if (OnPitRoad(idx) || m.U8(Addr.Pitting + idx * Addr.Ocd) == 0) continue;
                m.W8(Addr.Pitting + idx * Addr.Ocd, 0);
                if (requestsCancelled.Add(idx)) Log("pit road still shut for #" + CarNumber(idx) + ": its pit request called off");
            }
        }

        // The lanes back to what the game would have once the per-car gate is no longer needed.
        void RestoreGate()
        {
            try
            {
                int v = OpenPitsToAll || flag >= 3 ? 2 : 1;
                for (int lane = 0; lane < 2; lane++) m.W32(Addr.PitGate + lane * 4, v);
            }
            catch (Exception) { }
        }

        void EndPerCarGate()
        {
            gateLogged.Clear();
            if (!perCarGate) return;
            perCarGate = false;
            RestoreGate();
        }

        // Back to what the game would have: open to all once the pits open with OpenPitsToAll, from flag 3 or at green, else
        // lead lap only (the game raises it to 2 itself later). Never left above 2: the AI would not pit at all.
        void OpenGate(string why)
        {
            if (!gateClosed) return;
            gateClosed = false;
            try
            {
                int v = OpenPitsToAll || flag >= 3 ? 2 : 1;
                for (int lane = 0; lane < 2; lane++) m.W32(Addr.PitGate + lane * 4, v);
                Log(string.Format("pit road gate opened ({0}): {1}", v == 2 ? "all cars" : "lead lap cars, the rest when the game opens it", why));
            }
            catch (Exception) { }
        }

        // %LOCALAPPDATA%\NR2003RaceControl\state.txt: "pits_closed=1", rewritten every second while the pits are held
        // (AIMOD trusts it for 3 s), "pits_closed=0" once they open. With stage racing on, also "stage_ends=84,204"
        // (every stage end but the race's), rewritten every 2 s during a race (AIMOD trusts it for 10 s); left
        // out when Race Control is not running the stages. During a race also "wave_around=1" or "=0" (the wave around
        // option; AIMOD keeps lapped cars out for the wave around only when it is 1), refreshed the same way.
        public static string StatePath = System.IO.Path.Combine( // the offline sims point it elsewhere
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NR2003RaceControl", "state.txt");
        bool publishedClosed;
        DateTime lastPublish;

        void PublishPitsClosed(bool closed)
        {
            string stages = StageEndsLine();
            string wave = shuttingDown || !InRace ? null : WaveAroundOn ? "1" : "0";
            double age = (DateTime.Now - lastPublish).TotalSeconds;
            if (closed == publishedClosed && stages == publishedStages && wave == publishedWave &&
                (closed ? age < 1 : (stages == null && wave == null) || age < 2)) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StatePath));
                System.IO.File.WriteAllText(StatePath, "pits_closed=" + (closed ? "1" : "0") + "\r\n" +
                    (stages != null ? "stage_ends=" + stages + "\r\n" : "") + (wave != null ? "wave_around=" + wave + "\r\n" : ""));
            }
            catch (Exception) { }
            publishedClosed = closed;
            publishedStages = stages;
            publishedWave = wave;
            lastPublish = DateTime.Now;
        }

        string publishedStages, publishedWave;

        // "84,204", or null when Race Control is not running the stages (or is shutting down).
        string StageEndsLine()
        {
            if (shuttingDown || !InRace) return null;
            int[] ends = CurrentStageEnds();
            return ends == null || ends.Length == 0 ? null : string.Join(",", ends.OrderBy(e => e).Select(e => e.ToString()).ToArray());
        }

        bool shuttingDown;

        // Keeps state.txt fresh during a race (stage_ends), whatever the pits are doing.
        void PublishState() { PublishPitsClosed(publishedClosed); }

        // True once pit road is open to the cars this caution.
        bool PitsOpenTick()
        {
            if (pitsOpen) return true;
            if (!PitsClosedFirstLap)
            {
                OpenGate("closed first lap switched off");
                PublishPitsClosed(false);
                pitsOpen = flag >= 1; // the game's own timing: the leader has taken the yellow
                if (pitsOpen && Classic) Log("pits open on the game's own timing (classic rules: lead lap cars now, lapped cars from the game's next pace lap)");
                return pitsOpen;
            }
            double trackLen = m.F64(Addr.TrackLength);
            Car leader = null, first;
            List<Car> cars = trackLen > 100 ? Field(out leader, out first) : null;
            if (flag >= 5 || (cars == null && flag >= 3))
            {
                OpenNow("one to go came before the leader's second time by");
                return true;
            }
            HoldPitsClosed();
            if (flag < 1 || cars == null) return false;
            double entry = OpenPoint(trackLen);
            double d = Mod(entry - DlongOf(leader.Idx), trackLen);
            if (leader.Idx != entryLeader) { entryLeader = leader.Idx; leaderToEntry = -1; }
            if (leaderToEntry >= 0 && d > leaderToEntry + trackLen / 2)
            {
                // the first time by usually comes with the pace car still coming out, as always; after that only
                // passes with the leader picked up count
                if (leaderPasses == 0 || PaceCarHasLeader(leader, trackLen)) leaderPasses++;
                else Log(string.Format("leader #{0} went by pit road again before the pace car picked it up: that time by doesn't count", leader.Num));
            }
            leaderToEntry = d;
            if (d > trackLen * OpenBeforeEntry) return false;
            bool gate = WaveAroundOn && GateCatchingCars;
            string notReady = NotPickedUp(cars, leader, false, gate && leaderPasses >= 1);
            if (leaderPasses < 1)
            {
                string packed = OpenWhenPacked && notReady == null ? Packed(cars) : null;
                if (packed == null) return false;
                OpenNow(string.Format("leader #{0} is {1:0} m from {3} on its first time by and the field is packed up ({2})", leader.Num, d, packed, openPointName));
                return true;
            }
            if (notReady != null && leaderPasses < 2)
            {
                // the same reason with a distance counting down is not news
                if (heldFor == null || Regex.Replace(notReady, @"\(\d+ m", "(") != Regex.Replace(heldFor, @"\(\d+ m", "("))
                    Log("pits stay closed on the leader's second time by: " + notReady);
                heldFor = notReady;
                return false;
            }
            OpenNow(string.Format("leader #{0} is {1:0} m from {4} on its {2} time by{3}", leader.Num, d, leaderPasses < 2 ? "second" : "third",
                notReady != null ? " (opened anyway: " + notReady + ")" : heldFor != null ? " (the field is lined up behind the pace car now)" : "", openPointName));
            return true;
        }

        // Pit road opens only once the pace car has picked up the leader and the field is behind it: a lead lap car
        // caught between the pace car and the leader when the caution came out is sent around the pace car first
        // (SendAroundEarly) and must have caught the back of the field. Opened with them still out there, the
        // leaders pitted a lap before them and they came out half a lap behind everyone, and every car lined up
        // behind them on the restart order had to wait for them. Null = ready.
        string heldFor;

        string NotPickedUp(List<Car> cars, Car leader) { return NotPickedUp(cars, leader, false, false); }

        // anyCatching: cars sent around and still catching the field don't count (the free pass is chosen then)
        // gated: the cars caught in front of the leader (stuck, or sent around and catching) are left to the per-car
        // gate (GatedCars) instead of keeping pit road shut for everybody
        string NotPickedUp(List<Car> cars, Car leader, bool anyCatching, bool gated = false)
        {
            var stuck = cars.Where(c => c.Stuck && !eligible.ContainsKey(c.Idx)).OrderBy(c => c.Num).ToList();
            if (stuck.Count > 0 && !gated) return "lead lap car(s)" + string.Concat(stuck.Select(c => " #" + c.Num)) + " still in front of the leader";
            var catching = cars.Where(c => sentEarly.Contains(c.Idx) && !eligible[c.Idx].Pitted).ToList();
            var others = cars.Where(c => !catching.Contains(c)).ToList();
            if (gated) others = others.Where(c => !c.Stuck).ToList();
            double tail = others.Count > 0 ? others.Max(c => c.Gap) : 0;
            var behind = catching.Where(c => GoingAround(c.Idx) || c.Gap > tail + 100).OrderBy(c => c.Num).ToList();
            if (behind.Count > 0 && !anyCatching && !gated) return "#" + string.Join(" #", behind.Select(c => c.Num)) + " sent around, still catching the back of the field";
            // the cars from the pace car back to the leader are nose to tail
            Car lead = cars.FirstOrDefault(c => c.Idx == leader.Idx);
            if (lead == null) return null; // the leader is on pit road
            double len = m.F64(Addr.TrackLength);
            if (len > 100 && !PaceCarHasLeader(lead, len))
                return string.Format("the pace car has not picked up leader #{0} yet ({1})", lead.Num,
                    lead.Gap >= len / 2 ? "the leader is in front of it" : "it is still on pit road");
            double prev = 0;
            foreach (Car c in cars.Where(c => c.Gap <= lead.Gap && !catching.Contains(c)).OrderBy(c => c.Gap))
            {
                if (c.Gap - prev > (prev == 0 ? 150 : 100))
                    return string.Format("the pace car has not picked up leader #{0} yet ({1:0} m behind it)", lead.Num, lead.Gap);
                prev = c.Gap;
            }
            // and the rest of the field has closed up behind the leader: strung out round the lap, the cars at the
            // back reached pit road long after the leaders and came out most of a lap behind them
            // (Talladega 2026-11-06: a strung-out tail held the pits a whole lap in 10 of 15 cautions. The leader is
            // picked up and the cars from it to the pace car are nose to tail: the rest follow, pit road opens.)
            if (!PaceFixes && Packed(cars.Where(c => !catching.Contains(c)).ToList()) == null)
                return "the field is still strung out behind the pace car";
            return null;
        }

        // On a long track the field is often nose to tail behind the pace car before the leader first reaches pit
        // road; holding the pits for another lap then only costs a pace lap. Packed: the first car close behind the
        // pace car and every gap after it short, two stragglers (a slow damaged car) allowed. Null = not packed.
        string Packed(List<Car> cars)
        {
            List<Car> line = cars.OrderBy(c => c.Gap).ToList();
            if (line.Count < 2 || line[0].Gap > 120) return null;
            int stragglers = 0;
            double len = 0;
            for (int i = 1; i < line.Count; i++)
            {
                double gap = line[i].Gap - line[i - 1].Gap;
                if (gap > 60 && ++stragglers > 2) return null;
                if (gap <= 60) len = line[i].Gap;
            }
            return string.Format("{0} cars in {1:0} m{2}", line.Count - stragglers, len, stragglers > 0 ? ", " + stragglers + " straggling" : "");
        }

        void OpenNow(string why)
        {
            pitsOpen = true;
            OpenGate("pits open");
            PublishPitsClosed(false);
            Log("pits open" + (OpenPitsToAll ? " to all cars" : "") + ": " + why);
        }

        void StartEntryWatch()
        {
            toEntry.Clear(); passedEntry.Clear(); readyToDecide.Clear();
        }

        // A car's first time by is when it passes pit entry with the pits open; it is decided 60 m later (on pit
        // road by then, or it stayed out).
        void EntryWatchTick()
        {
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100) return;
            double entry = EntryPoint(trackLen);
            for (int idx = 0; idx <= maxIdx; idx++)
            {
                if (idx == Addr.PaceCarIdx || !Running(idx) || readyToDecide.Contains(idx)) continue;
                double d = Mod(entry - DlongOf(idx), trackLen), prev;
                if (toEntry.TryGetValue(idx, out prev) && prev < trackLen * 0.25 && d > trackLen * 0.75) passedEntry.Add(idx);
                toEntry[idx] = d;
                if (passedEntry.Contains(idx) && (d <= trackLen - 60 || OnPitRoad(idx))) readyToDecide.Add(idx);
            }
        }

        // Race Control closing: AIMOD must not think the pits are still closed.
        public void Shutdown() { shuttingDown = true; RestoreTail("RaceControl closing"); RestorePaceSpeed("RaceControl closing"); ResetPaceCar(); EndLowLine("RaceControl closing"); RestoreContact(); OpenGate("RaceControl closing"); EndPerCarGate(); PublishPitsClosed(false); }

        // At the green (or when a caution ends early) the pits are no longer held.
        void ReleaseClosedBit() { OpenGate("caution over"); EndPerCarGate(); PublishPitsClosed(false); }

        void ResetPits()
        {
            pitsOpen = closedLogged = false;
            heldFor = null;
            requestsCancelled.Clear();
            entryLeader = -1; leaderPasses = 0; leaderToEntry = -1;
            stuckAtOpen.Clear(); stuckTaken = false;
            StartEntryWatch();
        }
    }
}
