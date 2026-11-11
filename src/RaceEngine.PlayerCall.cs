using System;
using System.Collections.Generic;
using System.Linq;

namespace RaceControl
{
    // What the player's banner (PlayerBanner.cs, the same one qualifying uses) says during a race: a penalty on the
    // car, the free pass (Lucky Dog) or a wave around with what to do next, and in the caution line which car to follow.
    // Fed from the engine's wave tables; the line to follow is read from the game's own pacing table. Priority: penalty, free pass / wave, follow.
    // Penalties are only reported, never judged: the rule's name is given for the two rules Race Control knows
    // (11 passing on the inside, 14 passing under yellow), any other is "rule N".
    public partial class RaceEngine
    {
        public string PlayerCall = "";             // what the banner says now ("" = nothing)
        public int PlayerCallKind;                 // PlayerBannerForm.Kind: 0 info, 3 red, 4 wave around, 5 free pass
        string callLogged = "";
        string penaltyLogged = "";

        void PlayerCallTick()
        {
            string text = ""; int kind = 0;
            try { Compose(out text, out kind); }
            catch (GameMemoryException) { text = ""; }
            PlayerCall = text; PlayerCallKind = kind;
            if (text != callLogged)
            {
                callLogged = text;
                if (text.Length > 0) Log("your car: " + text);
            }
        }

        void Compose(out string text, out int kind)
        {
            text = ""; kind = 0;
            const int me = Addr.PlayerIdx;
            if (!PlayerDriving) return;

            string pen = PlayerPenalty(me);
            if (pen.Length > 0)
            {
                bool serve = m.U8(Addr.PenaltyStop + me * Addr.Ocd) != 0;
                text = serve ? "PENALTY STOP - COME IN TO SERVE IT (" + pen + ")" : "PENALTY ON YOUR CAR (" + pen + ")";
                kind = 3;
                return;
            }
            if (flag < 1 || flag > 6 || OnPitRoad(me)) return;

            int going = GoingRoundState(me);   // 0 no, 1 going round the pace car, 2 past it
            if (going > 0)
            {
                bool dog = IsFreePass(me);
                kind = dog ? 5 : 4;
                string what = dog ? "FREE PASS" : "WAVED AROUND";
                text = going == 1 ? what + ": GO AROUND THE PACE CAR" : what + ": CATCH THE BACK OF THE FIELD AND LINE UP AT THE TAIL";
                return;
            }
            string follow = FollowCall(me);
            if (follow.Length > 0) { text = follow; kind = 0; }
        }

        // the first penalty showing on the car, as "rule N type T" (or its name); "" when none
        string PlayerPenalty(int idx)
        {
            List<uint> rules = PenaltyRules();
            string found = "";
            for (int r = 0; r < rules.Count; r++)
            {
                if (rules[r] == 0) continue;
                uint v = m.U32(rules[r] + 4 + idx * 4);
                if ((v & 0xF) == 0) continue;
                string name = r == 14 ? "passing under yellow" : r == 11 ? "passing on the inside" : "rule " + r;
                if (found.Length == 0) found = name;
                string key = r + ":" + v;
                if (penaltyLogged != key) { penaltyLogged = key; Log(string.Format("your car: penalty rule {0} type {1} amount {2} (0x{3:X}); stop flag {4}, pits {5}",
                    r, v & 0xF, (v >> 4) & 0xFFF, v, m.U8(Addr.PenaltyStop + idx * Addr.Ocd), pitsOpen ? "open" : "closed")); }
            }
            if (found.Length == 0) penaltyLogged = "";
            return found.ToUpperInvariant();
        }

        // 0: not going round the pace car; 1: sent round, not past it yet; 2: past it, still to catch the back of the field
        int GoingRoundState(int idx)
        {
            Eligible e;
            if (!eligible.TryGetValue(idx, out e) || e.Pitted || e.Restored) return 0;
            return e.Passed ? 2 : 1;
        }

        bool IsFreePass(int idx)
        {
            return freePassIdx == idx;
        }

        // The car ahead of the player in its line of the game's pacing table, or the pace car when nobody is.
        string FollowCall(int me)
        {
            if (m.U8(Addr.NumPacingLines) < 1 || !PaceCarOnTrack()) return "";
            int myLine = m.I32(Addr.PacingLineIdx + me * 13), myRow = m.I32(Addr.PacingRow + me * 13);
            if (myRow < 0 || myRow > 60) return "";
            int ahead = -1, aheadRow = -1;
            for (int o = 0; o <= maxIdx && o < 48; o++)
            {
                if (o == me || o == Addr.PaceCarIdx || !Running(o) || OnPitRoad(o)) continue;
                if (m.I32(Addr.PacingLineIdx + o * 13) != myLine) continue;
                int row = m.I32(Addr.PacingRow + o * 13);
                if (row >= 0 && row < myRow && row > aheadRow) { ahead = o; aheadRow = row; }
            }
            if (ahead < 0) return "FOLLOW THE PACE CAR";
            return "FOLLOW #" + CarNumber(ahead);
        }
    }
}
