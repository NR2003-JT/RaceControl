using System;

namespace RaceControl
{
    public partial class RaceEngine
    {
        // ---- more pace laps before the start ---------------------------------------------------------------------
        // NR2003 starts every race at one to go: race init (0x415737) sets laps complete to -1 and the flag to 5, so the
        // field gets the one pace lap. In flag 5 the game asks its "caution still needed" check (0x4185F0, true while
        // the caution request byte Addr.Yellow is set) and, if so, goes to flag 4 (delayed green, 0x419467) instead.
        // In flag 4 it goes back to one to go once the check says no: when the pace car's pace lap count moves on at
        // the line (0x419367 -> 0x41938E) or as the pace car passes its pit-entry window (track.ini value at 0xBDE260,
        // +100 m; 0x4193B4). Every flag change clears the byte (0x417FF0). The game's pace car block is [0x760B38].
        // So Race Control sets the byte at the start and keeps it set until the last extra lap: released once the pace
        // car is past its window on the lap before one to go, the line crossing then gives one to go for the last lap.
        // Not live checked yet: every step is logged ("start pacing: ...") to calibrate on the first race.
        public int StartPaceLaps = 1;                   // pace laps before the green, 1 = the game's own
        public bool StartPaceAddLaps = true;            // laps the game counted during the extra pace laps are added back
        const long PaceWindowAt = 0xBDE260;             // float metres, the pace car's pit-entry window start (+100 m)
        bool startPacing, startReleased, startLogged, startSawBeforeWindow, startNoTakeLogged;
        int startPaceLap0 = int.MinValue, startLastPaceLap = int.MinValue, startFlag = -1, startLastLc = -1;
        int startCrossings;
        float startLastDl = float.NaN;
        double startHeldAt5 = double.NaN;

        int PaceCarPaceLap() { return m.I32(Addr.PaceLap + Addr.PaceCarIdx * Addr.Ocd); }

        // ticked before the green (laps complete -1, or still pacing after Race Control asked for more laps)
        void StartPaceTick(int lc)
        {
            if (StartPaceLaps <= 1 && !startPacing) return;
            double trackLen = m.F64(Addr.TrackLength);
            if (trackLen <= 100 || double.IsNaN(trackLen)) return;
            int pl = PaceCarPaceLap();
            float dl = DlongOf(Addr.PaceCarIdx), win = m.F32(PaceWindowAt);
            bool winOk = win > 0 && win + 100 < trackLen;
            if (startPaceLap0 != int.MinValue && pl < startPaceLap0) ResetStartPace();   // the race was restarted
            if (startPaceLap0 == int.MinValue) { startPaceLap0 = startLastPaceLap = pl; }
            // our own count of the pace car's line crossings, as a guard if its pace lap count doesn't move
            if (!float.IsNaN(startLastDl) && startLastDl > trackLen * 0.75 && dl < trackLen * 0.25) startCrossings++;
            startLastDl = dl;
            if (pl != startLastPaceLap)
            {
                startLastPaceLap = pl; startSawBeforeWindow = false;
                Log(string.Format("start pacing: pace car lap count {0} ({1} done of {2} pace laps), laps complete {3}", pl, pl - startPaceLap0, StartPaceLaps, lc));
            }
            if (winOk && dl < win) startSawBeforeWindow = true;
            if (flag != startFlag)
            {
                Log(string.Format("start pacing: flag {0} -> {1} (pace car lap count {2}, at {3:0} m, window {4:0} m, laps complete {5}, caution byte {6})",
                    startFlag, flag, pl, dl, win, lc, m.U8(Addr.Yellow)));
                startFlag = flag;
            }
            if (flag != 7) startLastLc = lc;

            int done = pl - startPaceLap0;
            bool want = !startReleased && (flag == 4 || flag == 5) && StartPaceLaps > 1;
            if (want)
            {
                // the last extra lap: let go once past the window, so the next line crossing gives one to go
                bool last = done >= StartPaceLaps - 1 || (done >= StartPaceLaps - 2 && (!winOk || (startSawBeforeWindow && dl > win + 100)));
                bool runaway = startCrossings > StartPaceLaps + 2;
                if (last || runaway)
                {
                    startReleased = true; want = false;
                    if (m.U8(Addr.Yellow) != 0 && flag != 7) m.W8(Addr.Yellow, 0);
                    Log(runaway ? string.Format("start pacing: let go after {0} line crossings (the pace car's lap count didn't move as expected)", startCrossings)
                                : string.Format("start pacing: let go at {0:0} m with {1} pace lap(s) done: one to go next", dl, done));
                }
            }
            if (want)
            {
                if (m.U8(Addr.Yellow) == 0) m.W8(Addr.Yellow, 1);
                if (!startLogged) Log(string.Format("start pacing: {0} pace laps before the green (the game's own is 1): holding the caution request", StartPaceLaps));
                startLogged = startPacing = true;
                // the game only asks while its yellow option is on (0xBEBD84 bit 0x100000): flag 5 held for long = not taken
                if (flag == 5 && done == 0)
                {
                    if (double.IsNaN(startHeldAt5)) startHeldAt5 = Now;
                    else if (Now - startHeldAt5 > 20 && !startNoTakeLogged)
                    {
                        startNoTakeLogged = true;
                        Log("start pacing: the game hasn't taken the extra lap after 20 s at one to go (are yellow flags on in the game's options?)");
                    }
                }
            }
        }

        // the green after start pacing: the race laps the game counted under it are added back
        void StartPaceGreen(int lc)
        {
            if (!startPacing) return;
            startPacing = false;
            int counted = startLastLc + 1;          // -1 at one to go = none counted
            Log(string.Format("start pacing: green (laps complete {0} at the last pace lap: {1} counted as race laps)", startLastLc, counted));
            if (counted > 0 && StartPaceAddLaps)
            {
                int len = m.U16(RaceLengthAddr);
                if (len > 0 && len < 5000)
                {
                    m.W16(RaceLengthAddr, len + counted);
                    m.W16(RaceLengthDisplayAddr, len + counted);
                    Log(string.Format("start pacing: race length {0} -> {1} laps (the pace laps counted are added back)", len, len + counted));
                }
            }
        }

        void ResetStartPace()
        {
            startPacing = startReleased = startLogged = startSawBeforeWindow = startNoTakeLogged = false;
            startPaceLap0 = startLastPaceLap = int.MinValue; startFlag = startLastLc = -1; startCrossings = 0;
            startLastDl = float.NaN; startHeldAt5 = double.NaN;
        }
    }
}
