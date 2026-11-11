using System;
using System.Collections.Generic;
using System.Linq;

namespace RaceControl
{
    public partial class RaceEngine
    {
        // ---- lapped cars drop back on the low line ------------------------------------------------------
        // Under caution every AI car drives the game's Race line mode (race.lp, inside the minrace/maxrace band).
        // A lapped car that has to drop behind lead lap cars stays on that same line, so the lead lap cars behind
        // it can't get by: at ISM 18 lapped cars were still in the way at one to go and the cone opened with the
        // lineup 26 spots out. Such a car is switched to the game's Limp line mode (limp.lp, the slow-car line well
        // below the groove: about 12 m in from the edge at Bristol against 2 to 8 m for the race line), so the
        // lead lap cars drive past it on the race line; once it is behind them it goes back to the Race mode. The
        // Race and Limp modes have no set-up code (their enter does nothing), and the caution state only switches
        // a car that is in the Race or Panic mode, so it leaves a Limp car alone. Only done where the track has a
        // limp.lp; never for cars on pit road or in any other line mode (pit lane, pacing, panic), and every car
        // is back on the Race line at double file.
        public bool LappedDropLow = true;
        // When they drop: a lapped car stuck behind the new leader (no wave around for it) keeps its spot, single file
        // in the groove, while pit road is open and the cars that pit merge on and off it; once the pit stops are done
        // (the lineup after them - the moment the wave arounds go too) it drops to the low line and the lead lap cars
        // go by high. One to go waits for it (HoldOneToGoTick). Off = drop as soon as the caution lineup has a lead lap
        // car ahead of it (before 2026-10-26).
        public bool LappedHoldUntilPitsDone = true;
        bool lowWaitLogged;

        const long LineRace = 0xBD7954, LineLimp = 0xBD7988; // line mode objects {vtable, name*, id 7 / 9, ..., +0x18 line data}
        static long CarLine(int idx) { return Addr.Laps - 1360 + 0x13B3 + idx * Addr.Ocd; }       // the car's line mode
        static long CarLineNext(int idx) { return Addr.Laps - 1360 + 0x13B7 + idx * Addr.Ocd; }   // the mode it is going to
        static long CarLineShown(int idx) { return Addr.Laps - 1360 + 0x1B91 + idx * Addr.Ocd; }  // set with it by the game

        readonly Dictionary<int, double> lowSince = new Dictionary<int, double>();  // cars on the low line -> since
        readonly Dictionary<int, double> lowClearSince = new Dictionary<int, double>();
        readonly HashSet<int> lowRefused = new HashSet<int>();      // the game took it back: left alone this caution
        bool? lowLineOk;
        bool lowLogged;

        bool LineModesOk()
        {
            if (lowLineOk == null) lowLineOk = m.I32(LineRace + 8) == 7 && m.I32(LineLimp + 8) == 9;
            return lowLineOk.Value;
        }

        // The game changes a car's line as current + target (+0x13B7) and eases it across; with only the current one
        // changed it steered the car straight back to the Race line (Toledo, 2.8.8: the same cars switched many
        // times a second, dropping low and surging forward). Both are set, so the car stays where it is put.
        void SetLine(int idx, long mode)
        {
            m.W32(CarLineNext(idx), (int)mode);
            m.W32(CarLine(idx), (int)mode);
            m.W32(CarLineShown(idx), (int)mode);
        }

        // Lapped cars physically in front of a lead lap car that the lineup has ahead of them.
        HashSet<int> LappedToDropBack(List<Car> cars)
        {
            var want = new Dictionary<int, int>();
            foreach (Car c in cars)
            {
                int line = m.I32(Addr.PacingLineIdx + c.Idx * 13), row = m.I32(Addr.PacingRow + c.Idx * 13);
                if (line >= 0 && line <= 1 && row >= 0) want[c.Idx] = (row + line) * 2 - line;
            }
            var set = new HashSet<int>();
            foreach (Car c in cars)
            {
                if (!Lapped(c) || !want.ContainsKey(c.Idx)) continue;
                if (cars.Any(o => !Lapped(o) && !WavedToTail(o.Idx) && want.ContainsKey(o.Idx) && want[o.Idx] < want[c.Idx] && o.Gap > c.Gap))
                    set.Add(c.Idx);
            }
            return set;
        }

        void LowLineTick()
        {
            // not at one to go: the game puts every car in its own lineup mode then (cars were taken straight off)
            if (!highChecked) { highChecked = true; RestoreHighBackup(); } // first race tick: a line left moved
            bool on = LappedDropLow && (WaveAroundOn || LappedToBack) && flag >= 2 && flag <= 4;
            if (!on)
            {
                if (lowSince.Count > 0 || highOrig != null) EndLowLine(flag >= 5 ? "one to go" : "caution over");
                lowRefused.Clear();
                lowWaitLogged = false;
                return;
            }
            if (!LineModesOk()) return;
            if (m.U32(LineLimp + 0x18) == 0) // the track's limp.lp isn't loaded
            {
                if (!lowLogged) { lowLogged = true; Log("lapped cars can't be moved to the low line here: the track has no limp.lp"); }
                return;
            }
            List<Car> cars = Field();
            if (cars == null) return;
            HashSet<int> drop = LappedToDropBack(cars);
            if (LappedHoldUntilPitsDone && lowSince.Count == 0 && drop.Count > 0 && !PitCycleDone())
            {
                if (!lowWaitLogged)
                    Log("lapped car(s) behind the leader" + string.Concat(drop.Select(i => " #" + CarNumber(i))) +
                        " keep their spots while the pit stops go on; they drop to the low line once the stops are done");
                lowWaitLogged = true;
                return;
            }
            var moved = new List<int>();
            foreach (int idx in drop)
            {
                if (lowSince.ContainsKey(idx)) { lowClearSince.Remove(idx); continue; }
                if (lowRefused.Contains(idx) || OnPitRoad(idx) || m.U32(CarLine(idx)) != LineRace) continue;
                SetLine(idx, LineLimp);
                lowSince[idx] = Now;
                moved.Add(idx);
            }
            if (moved.Count > 0)
                Log("low line for lapped car(s)" + string.Concat(moved.Select(i => " #" + CarNumber(i))) + ": they drop back below the groove while the lead lap cars go by");
            var back = new List<int>();
            foreach (int idx in lowSince.Keys.ToList())
            {
                bool gone = !Running(idx) || OnPitRoad(idx) || m.U32(CarLine(idx)) != LineLimp;
                if (gone)                                    // the game has it in another mode now: leave it be
                {
                    lowSince.Remove(idx); lowClearSince.Remove(idx);
                    if (Running(idx) && !OnPitRoad(idx) && lowRefused.Add(idx))
                        Log("#" + CarNumber(idx) + " was taken off the low line by the game (now in another line mode): left alone this caution");
                    continue;
                }
                if (drop.Contains(idx)) continue;
                double since;
                if (!lowClearSince.TryGetValue(idx, out since)) { lowClearSince[idx] = Now; continue; }
                if (Now - since < 2) continue;
                SetLine(idx, LineRace);
                lowSince.Remove(idx); lowClearSince.Remove(idx);
                back.Add(idx);
            }
            if (back.Count > 0)
                Log("back on the race line, behind the lead lap cars:" + string.Concat(back.Select(i => " #" + CarNumber(i))));
            HighLineTick();
        }

        // ---- ... and the lead lap cars go high -----------------------------------------------------------
        // The Race line mode drives race.lp, bounded by maxrace.lp (low, inside) and minrace.lp (high, near the wall).
        // The game keeps the three as arrays of 28-byte points (the .lp file's 6 floats, the lateral position at
        // +0x14; Race mode object +0xC race, +0x10 minrace, +0x14 maxrace; point count [0xB8FAA0]) and the cars steer
        // for the lateral position of the point ahead of them, which is how AI Enhanced's multigroove moves cars
        // (it rewrites race.lp just ahead of each car). While lapped cars are on the low line (they drive limp.lp,
        // not race.lp) the whole race line is moved LeadLapHighShare of the way to minrace, so the lead lap cars
        // pass high, well clear of them, and it is put back as soon as no lapped car is low (and at one to go).
        // The original points are also saved to a file, so a RaceControl that did not close properly puts them
        // back next time on the same track.
        public double LeadLapHighShare = 0.7;      // 0 = off
        // The cars scraped the wall on the high line (user, 2026-11-07): the line is moved this many metres back
        // toward the original race line (a car is about 1.9 m wide, so 1.0 = half a car width). 0 = as before.
        public double LeadLapHighPull = 1.0;
        const long LinePoints = 0xB8FAA0;
        float[] highOrig;
        uint highArray;
        int highPoints;
        double highClearSince = double.NaN;
        bool highChecked;
        static string HighBackupPath { get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(StatePath), "raceline_backup.txt"); } }

        void HighLineTick()
        {
            bool want = LeadLapHighShare > 0 && lowSince.Count > 0;
            if (want) { highClearSince = double.NaN; if (highOrig == null) ApplyHighLine(); return; }
            if (highOrig == null) return;
            if (double.IsNaN(highClearSince)) { highClearSince = Now; return; }
            if (Now - highClearSince >= 2) RestoreHighLine("the lapped cars are behind the lead lap cars");
        }

        void ApplyHighLine()
        {
            int n = m.I32(LinePoints);
            // The two bounds (+0x10, +0x14) are not the same way round on every track (Toledo: +0x14 is the one by
            // the wall), so the high one is the bound further from the limp line, the apron.
            uint race = m.U32(LineRace + 0xC), b1 = m.U32(LineRace + 0x10), b2 = m.U32(LineRace + 0x14), limp = m.U32(LineLimp + 0xC);
            if (n < 20 || n > 20000 || race == 0 || b1 == 0 || b2 == 0 || limp == 0 || race == b1 || race == b2) return;
            double d1 = 0, d2 = 0;
            for (int k = 0; k < n; k += 8)
            {
                float l = m.F32(limp + k * 28 + 0x14);
                d1 += Math.Abs(m.F32(b1 + k * 28 + 0x14) - l); d2 += Math.Abs(m.F32(b2 + k * 28 + 0x14) - l);
            }
            uint min = d1 >= d2 ? b1 : b2;
            var orig = new float[n];
            for (int k = 0; k < n; k++)
            {
                orig[k] = m.F32(race + k * 28 + 0x14);
                if (float.IsNaN(orig[k]) || Math.Abs(orig[k]) > 200) return; // not a line: leave it
            }
            highOrig = orig; highArray = race; highPoints = n;
            SaveHighBackup();
            double share = Math.Max(0, Math.Min(1, LeadLapHighShare));
            for (int k = 0; k < n; k++)
            {
                float hi = m.F32(min + k * 28 + 0x14);
                if (float.IsNaN(hi) || Math.Abs(hi) > 200) continue;
                double move = share * (hi - orig[k]);
                double back = Math.Max(0, Math.Min(Math.Abs(move), LeadLapHighPull));   // toward the original line, never past it
                move -= Math.Sign(move) * back;
                m.W32(race + k * 28 + 0x14, FloatBits((float)(orig[k] + move)));
            }
            Log(string.Format("high line for the lead lap cars: the race line is moved {0:0}% of the way to the high groove (bound +0x{1:X}), {2:0.0} m back from the wall side, while the lapped cars drop back low", share * 100, min == b1 ? 0x10 : 0x14, LeadLapHighPull));
        }

        void RestoreHighLine(string why)
        {
            if (highOrig == null) return;
            try
            {
                if (m.U32(LineRace + 0xC) == highArray && m.I32(LinePoints) == highPoints)
                    for (int k = 0; k < highPoints; k++) m.W32(highArray + k * 28 + 0x14, FloatBits(highOrig[k]));
                Log("race line back where it was (" + why + ")");
            }
            catch (GameMemoryException) { }
            highOrig = null;
            highClearSince = double.NaN;
            try { System.IO.File.Delete(HighBackupPath); } catch (Exception) { }
        }

        void SaveHighBackup()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(TrackName).Append('\n').Append(highArray).Append('\n').Append(highPoints).Append('\n');
                foreach (float f in highOrig) sb.Append(FloatBits(f)).Append('\n');
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HighBackupPath));
                System.IO.File.WriteAllText(HighBackupPath, sb.ToString());
            }
            catch (Exception) { }
        }

        // A backup left by a RaceControl that closed with the race line moved: put it back if it is the same line.
        void RestoreHighBackup()
        {
            try
            {
                if (!System.IO.File.Exists(HighBackupPath)) return;
                string[] l = System.IO.File.ReadAllLines(HighBackupPath);
                uint arr; int n;
                if (l.Length >= 3 && l[0] == TrackName && uint.TryParse(l[1], out arr) && int.TryParse(l[2], out n) && l.Length >= 3 + n &&
                    m.U32(LineRace + 0xC) == arr && m.I32(LinePoints) == n)
                {
                    for (int k = 0; k < n; k++) m.W32(arr + k * 28 + 0x14, int.Parse(l[3 + k]));
                    Log("race line put back from the backup a RaceControl that did not close properly left");
                }
                System.IO.File.Delete(HighBackupPath);
            }
            catch (Exception) { }
        }

        // Lapped cars still on the low line or still in front of lead lap cars the lineup has ahead of them.
        bool holdForLappedLogged;

        bool LappedStillDropping()
        {
            if (!LappedDropLow || !(WaveAroundOn || LappedToBack) || !LineModesOk()) return false;
            if (lowSince.Count > 0) return true;
            List<Car> cars = Field();
            return cars != null && LappedToDropBack(cars).Count > 0;
        }

        void EndLowLine(string why)
        {
            var back = new List<int>();
            foreach (int idx in lowSince.Keys)
                try { if (m.U32(CarLine(idx)) == LineLimp) { SetLine(idx, LineRace); back.Add(idx); } }
                catch (GameMemoryException) { }
            lowSince.Clear(); lowClearSince.Clear(); lowRefused.Clear();
            if (back.Count > 0) Log("back on the race line (" + why + "):" + string.Concat(back.Select(i => " #" + CarNumber(i))));
            RestoreHighLine(why);
        }
    }
}
