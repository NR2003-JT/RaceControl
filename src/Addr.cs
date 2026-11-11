namespace RaceControl
{
    // NR2003.exe addresses. Per-car fields are base + carIdx * Ocd (car block) or + carIdx * n (race control arrays).
    public static class Addr
    {
        public const int Ocd = 8724;                        // per-car block stride
        const int AOffset = 2 * Ocd;

        // per-car block
        public const long CarNum = 0xAD207A - AOffset;      // u16
        public const long Laps = 0xAD13A8 - AOffset;        // u16, laps completed
        public const long Dlong = 0xAD26C2 - AOffset;       // float, metres past the stripe
        public const long PaceLap = 0xAD28E2 - AOffset;     // int, caution laps done; one less lets a car pass the pace car
        public const long Dlat = 0xAD26DE - AOffset;        // float, metres across the track
        public const long CarClass = 0xAD13EE - AOffset;    // byte (low byte of the class dword at car+0x596): 0 = no car-to-car contact
        public const long CarClassLoad = 0xAD2072 - AOffset; // byte, the car's own class (0 for the pace car)
        public const long Pitting = Laps - 1360 + 8118;     // byte, 1 = pit stop requested / under way (AIMOD's +8118)
        public const long PenaltyStop = Pitting + 1;        // byte (+8119): the game's AI pit check set it, a penalty to serve
                                                            // - or its repair (limp) / tow modes (limp probe, 2026-10-28/29)
        public const long RepairNeed = 0xACCA30 + 0x1B33;   // int per car block: the game's repair need, 1 should pit, 2 must pit
        public const long PitCheckValue = Laps - 1360 + 0x1DBB; // double: the AI's pit check calls it in when 0 <= value <= 1 + a laps figure (fuel)

        public const int PlayerIdx = 0;                     // the player's car, when someone is driving

        // per-car bytes
        public const long OnPitRoad = 0x76BB0E - 2;
        public const long HasSpawned = 0x76B35E - 2;
        // int, 3 while you sit in your car (NR2003.exe: fgmsgDriverGetInCar sets it, GetOutOfCar clears it; static, 2026-10-26)
        public const long DriverInCar = 0xC067D4;
        public const long HasNotRetired = 0x7A76DA - 2;
        public const long RacedBack = 0x76B32D - 2;         // 1 = has raced back to the line (set for all = field frozen)
        // 1 = the game's "not keeping up in the pacing line" flag (race control 0x76B230 + 0x4061; NR2003.exe 0x4173B0 sets
        // it, 0x417640 clears it, cleared for all at the yellow): the cars behind skip this car as the one they follow
        // (pacing line lookup 0x414380) and may pass it. Read only.
        public const long Lagging = 0x76F291;

        // pacing line
        public const long PacingInside1st = 0x76AD00;       // int per row, stride 8; double file fills both lines as one array, stride 4
        public const long PacingOutside1st = 0x76AD04;      // int per row, stride 8 (single file uses this line)
        public const long NumPacingLines = 0x76B0EC;        // byte
        public const long PacingLineIdx = 0x76AE80;         // int per car, stride 13
        public const long PacingRow = 0x76AE84;             // int per car, stride 13
        public const long PacingWaved = 0x76AE88;           // byte per car, stride 13: waved by (set by the game's own !wave,
                                                            // 0x491650 -> pacing vtable +0x2C; its line upkeep reads and clears it)

        // race state
        public const long LapsComplete = 0x76F180;          // int, -1 on the initial pace lap
        public const long FlagState = 0x76F184;             // byte: 0 yellow out, 1 leader took yellow (pits open), 2 catching pace car,
                                                            //       3 pacing, 4 delayed green, 5 one to go, 6 double file, 7 green
        public const long NumCars = 0x979010;               // int
        public const long TrackLength = 0xDACF0C;           // double, metres
        public const long SimState = 0xBEBD30;              // byte: 2 running, 3 paused
        public const long SessionNum = 0xBEBDE9;            // byte, 0-based
        public const long NumSessions = 0xBEBD9F;           // byte, the race is the last session
        public const long SessionClock = 0x977A04;          // double, seconds (scoring time; stood still during some cautions,
                                                            // and for whole practice sessions) - use GameSeconds() instead
        // The game's own clock: int ticks at 0xBEBDF5 (+1 per tick, 0x509A50) / tick rate (0x5F7C50: 36 if [0x755A18] != 0,
        // else [0x7559B4]). The line crossings are stamped from it (0x53B5D0 -> 0x509A20); checked live 2026-10-21:
        // ticks x 1000 / 36 = the crossing time within one tick. It follows time acceleration and stops when paused.
        public const long GameTicks = 0xBEBDF5, TickRateFlag = 0x755A18, TickRateValue = 0x7559B4;
        public static double GameSeconds(IGameMemory m)
        {
            int ticks = m.I32(GameTicks);
            if (ticks <= 0) return m.F64(SessionClock);              // not readable (offline sims): the old clock
            int rate = m.I32(TickRateFlag) != 0 ? 36 : m.I32(TickRateValue);
            if (rate <= 0 || rate > 10000) rate = 36;
            return ticks / (double)rate;
        }
        public const long Yellow = 0x76B35B;                // byte, 1 throws a caution
        public const long RaceLength = 0xBEBDBD;            // u16 laps (race weekend)
        public const long RaceLengthQuickRace = 0xBEBDAD;   // u16 laps (quick race: 2 sessions)
        public const long RaceLengthDisplay = 0xC069AF;
        public const long RaceLengthDisplayQuickRace = 0xC0699F;
        public const long TrackName = 0xDAF110;             // track folder name, e.g. Charlotte_2012_SDT_Night_CE

        // pit road and penalties
        public const long PitState = 0x76B2F9;              // u16: 0x100 open to the lead lap only, 0x8 open to all
        public const long PitGate = 0x76F563;               // dword per pit lane (race ctl +0x4333, lane = [[car+0x21E8]]): the game's
                                                            // "may this car pit" check 0x415A30: 2 all, 1 lead lap only, else none
        public const long PitGateCar = 0x76F56B;            // byte per car (48): with a lane at 1, 1 = this car may pit (the game
                                                            // sets it at each crossing: on the lead lap)
        public const long ClosedPitPenalty = 0x86D7B8 + 4;  // dword per car, rule "Entering closed pits"
        public const long PaceCarPenalty = 0x870890;        // rule object "ahead of the pace car": dword per car at +4+car*4,
                                                            // timer at +0xF7+car*12 (+4 double start time, -1.0 = not running)
        public const long PenaltyList = 0xBDB9D8;           // [ptr]+4 = array of rule objects, +8 = count

        // scoring
        public const long CrossTable = 0x77F996;            // dword per lap per car (512 laps per car): 28-bit ms | leader flags
        public const long StandingsCache = 0x797996;        // 16 bytes per car, +0 = laps completed

        public const int PaceCarIdx = 1;
    }
}
