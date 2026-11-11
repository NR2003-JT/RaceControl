# RaceControl

Caution, pace car and restart control for NASCAR Racing 2003 Season: wave arounds / free pass, pit road open and close,
lapped cars to the back, double file and choose cone restarts with a TV-style board, stage cautions, and
qualifying modes. It reads and writes the game's memory, so it has to run as administrator, like the game.
See `README.txt` for the user guide and release notes.

## Build

C# WinForms, .NET Framework 4, x86. Needs the Windows `csc.exe` that ships with .NET Framework.

    build.cmd

This writes `bin\RaceControl.exe` plus offline simulators (`CautionSim`, `StageSim`, `QualSim`, ...) built from `test\`.
Run the sims after logic changes; they use a temporary state file, never your real one.

## Layout

- `src\` the app: `GameMemory.cs` / `Addr.cs` (game memory), `RaceEngine*.cs` (race logic by topic), `Qualifying.cs`,
  `MainForm.cs`, the cone board and TV overlays, the live web page.
- `test\` offline simulators and helper scripts.
- Series art (number art, headshots, logos) is not included. Put your own under `art\series\<id>\`.

Companion tools read RaceControl's `race_control.log` and `%LOCALAPPDATA%\NR2003RaceControl\state.txt`; keep those
wordings stable.

## License

MIT, see `LICENSE`.
