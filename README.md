# RaceControl

Caution, pace car and restart control for **NASCAR Racing 2003 Season**. RaceControl runs the yellow flag the way
modern NASCAR does: the field freezes, the pace car picks up the leader, the free pass and wave around put lapped cars
back where they belong, and the restart is double file with a TV-style choose cone board. It also runs stage racing
and qualifying formats the game doesn't have.

It replaces Better Cautions and does everything it did. If another app needs Better Cautions running (an overlay
app's stage cautions, for example), keep every Better Cautions option unchecked.

Download the latest release from the [Releases](../../releases) page. The `README.txt` inside the zip, also kept here
as [USER GUIDE.txt](USER%20GUIDE.txt), explains every setting; each option also has a tooltip in the app.

## What it does

- **Pace car.** With the field frozen the pace car is held on pit road and sent out so it joins the track just
  ahead of the leader, timed from what it measures at each caution and saved per track. Pace laps are fitted so the
  field closes up behind it instead of swapping places.
- **Free pass and wave around.** The first car a lap down gets its lap back (the free pass); lapped cars between the
  pace car and the leader are waved around the modern NASCAR way and restart at the tail, with the lap credited
  straight away. Lapped cars drop to the low line after the pit stops.
- **Pit road.** Closed until the pace car has the leader and the field is packed, then open to every car. No
  car-to-car contact on pit road.
- **Restarts.** Double file or single file, with the choose cone: cars pick a lane one at a time and a TV-style board
  shows each choice and the spots gained or lost. When you drive, the game pauses at the cone and you pick your lane.
  A track map lets you click where the cone goes.
- **Stages.** 2 to 4 stages, with the caution thrown 3/4 through the stage's last lap so nobody gets caught in front
  of the pace car. Stage laps by track length, percentage, lap numbers, or read from a stage racing companion app.
- **Other rule sets.** Classic 2003 race back to the line, 2004-style double file, or NR2003's own cautions with just
  the Lucky Dog added. Built-in presets, plus your own.
- **Qualifying.** Single car runs and knockout rounds run in practice and written to the real grid, NASCAR and Indy
  types (oval runs ranked by average, road and street Group 1 / Group 2 / Fast 12 / Fast Six), true DNQs, and a TV timing
  box with a live gap to pole and a broadcast style with telemetry. You can drive your own run with a banner that tells
  you when to go.
- **Your car.** A banner tells you about a penalty, the free pass or wave around and what to do, and who to follow.
- **A live page** in your browser with the running state, and a log that records every decision and why.

## Requirements

- Windows 10/11 and NASCAR Racing 2003 Season.
- RaceControl runs as administrator, because NR2003 normally does and RaceControl reads and writes its memory.
- The choose cone board and the lane chooser need NR2003 running windowed or borderless.

## Building from source

C# WinForms, .NET Framework 4, x86. You need the C# compiler that ships with Windows:

    build.cmd

This writes `bin\RaceControl.exe` and the offline simulators from `test\`. If you add a source file, add it to
`build.cmd`.

## Tests

Offline simulators drive the race engine with no game running. Run them after a logic change:

    bin\CautionSim.exe
    bin\StageSim.exe

`QualSim`, `TrackMapSim` and `BoardRender` cover qualifying, the track map and the boards. They use a temporary
state file, never your real one. `RaceControlSim` replays a recorded dump file given on the command line.

## Layout

- `src\` the app: `GameMemory.cs` and `Addr.cs` (game memory), `RaceEngine*.cs` (race logic, one file per topic:
  Wave, Cautions, Pits, PaceCar, Lineup, LowLine, Live, PlayerCall), `Qualifying.cs`, `MainForm.cs`, the cone board
  and TV overlays, and the live web page.
- `test\` the simulators and helper scripts.
- `art\` is not included. Series art (number art, headshots, logos) goes under `art\series\<id>\`.

AIMOD, the companion pit strategy app, reads RaceControl's `race_control.log` and
`%LOCALAPPDATA%\NR2003RaceControl\state.txt`, so the wording of those lines is kept stable.

## How it was built

RaceControl was written with AI assistance (Claude), working from live NR2003 sessions. Memory addresses were found by
probing the running game and are documented where they are defined. Features were tested in live races and by
testers, and the offline simulators reproduce the cautions that went wrong.

## Credits

- **Jonathon Thomas**: RaceControl.

## License

MIT, see [LICENSE](LICENSE).
