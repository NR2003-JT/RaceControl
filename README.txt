RaceControl for NASCAR Racing 2003 Season
=========================================

Caution and restart control for NASCAR Racing 2003 Season: everything Better Cautions did, plus the
modern NASCAR wave around, lapped cars to the back during the caution, double file and choose cone
restarts with a TV style choose cone board, and untangling cars that get stuck behind each other.
Or keep the older rules: classic 2004 style cautions, or NR2003's own cautions with just the Lucky Dog added.
It also runs qualifying formats NR2003 doesn't have (single car runs, knockout rounds, Indy formats).
Use it instead of Better Cautions. If another app needs Better Cautions running (for example an overlay
app's stage cautions), keep every Better Cautions option unchecked.

Please send race_control.log (next to the exe) with any problem report: it records every
decision RaceControl made and why.

What's new in v1.3.0 (internal build 3.3.11, since v1.2.0)
----------------------------------------------------------
Pace car and cautions
- The pace car picks up the leader properly: it goes the moment the leader drives past it (the game used to
  make it chase the leader from behind, and held, the field crawled a lap), and a leader just past the pace car
  waits for it whatever the pace car is doing.
- Pace laps are fitted to the field at the yellow so every car closes up on the car in front of it and the pace car,
  instead of the back of the field catching up and swapping places. (PaceLapFit=0 in the ini turns this off.)
- Cars side by side at the yellow that come out the other way round on track keep the order they had.
- A car on pit road at the yellow gives up its spot like a wreck car: end of the line, behind its group.
- The caution line is put in running order from the leader, from the order at the yellow (stopped cars at the end).
- Wave arounds and the free pass at one to go are checked again at the time, and queued cars keep their spot.
- Untangling cars that are stuck behind each other is looser at the back of the field (lapped cars, last quarter).
Your car
- A banner for your own car tells you what is happening: a penalty on your car, the free pass or wave around and what
  to do about it, and FOLLOW #N in the caution line.
Qualifying
- True DNQs: a car that misses the time is out of the race, not just last on the grid. It is scratched before the green.
  (Retiring a DNQ car at the race start is new and has had the least testing.)
- Indy re-qualifying lanes.
Choose cone board
- Art you change on disk (series logo, number art, headshots, colours) now shows the next time the board opens,
  without restarting RaceControl.
- The companion stage app's folder is now a setting only (Folder... on the Stages tab, or
  StageCompanionFolder= in RaceControl.ini). There is no built-in default, so set it if you use that app's stages or art.

What's new in v1.2.0 (internal build 3.2.0, since v1.1.0)
---------------------------------------------------------
Cautions and restarts
- NR2003 runs cautions and restarts (Cautions tab, new): leave the whole caution to the game - its own pit road
  timing, pace car and restart (single file, or NR2003's "double file restarts" race option) - and RaceControl
  only adds the Lucky Dog. Preset "NR2003 cautions + Lucky Dog".
- Classic rules: with Wave Around and Choose cone both off, pit road runs on the game's own timing, the Lucky Dog
  works without the wave around, and Double file restarts line up the 2004 way: lead lap cars single file on the
  outside, lapped cars single file on the inside, the Lucky Dog and penalized cars at the tail of the longer line.
  Laps down are taken when the leader takes the yellow, so a leader on pit road can't hand the Lucky Dog to the
  wrong car.
- Track map (Restarts tab > Track map...): the track drawn from above - click where the cone should go. Pit entry,
  pit exit and where the field goes double file are marked, and the cars move on it live. The map draws itself
  from the AI cars the first time a session runs at a track (the first lap or so): you don't have to drive a lap.
  RaceVision uses the same map for its camera map.
- The pace car never parks the field: with the field frozen it now goes as soon as the leader slows down to wait
  for it, and never waits more than 60 s (the field used to stack up on the front stretch for minutes).
- Lapped cars behind the new leader keep their spot while the pit stops go on, then drop to the low line while the
  lead lap cars go by high. One to go waits at most one extra lap for them.
- Big tracks (Talladega, Daytona): pit road opens on the leader's first time by, and a bad pace car reading far down
  the backstretch is no longer learned from.
- A yellow just before the line no longer sends the field around the pace car.
- Repair stops: NR2003 brings a damaged car in (the black flag shows) the moment it needs repairs. RaceControl used to
  call those off as "phantom penalties", which left the car limping round without a repair. Now it leaves them be.
- Restarts tab: "Hold the green if the pack is split (test)", off by default.
Qualifying
- Drive your own run: "Include my car in qualifying" (Qualifying tab > Your car). A banner tells you when to go
  and counts you down to the green and to the DNQ time; the game pauses at your turn so you can set your camera
  (paused time never counts towards a DNQ).
- NASCAR and Indy qualifying types. Indy oval: runs of 4 (or N) laps ranked by the average. Indy road / street:
  Group 1, Group 2, the Fast 12 and the Fast Six.
- Single car runs: "green N s after the car before finishes" release (each car's out lap is learned, so the gaps
  on track are even), max cars out, and a car that comes back to pit road on its own ends its run.
- Timing box: a live gap to pole on every timed lap, the run's result, and a Broadcast style with live telemetry
  and the running order. Lap times as Time or Speed (mph); speeds use the track's official length, so they match
  NR2003's own.
- Fixed: an out lap from a pit box just before the line counted as the timed lap; a false DNQ at Bristol; the Indy
  broadcast overlay showed sample values while being placed.

What's new in v1.1.0 (since v1.0.1)
-----------------------------------
- Qualifying modes: single car runs and knockout rounds, run in practice and written to the real grid,
  with a TV timing box (live gap to pole), stock qualifying pace and a DNQ for a car that wrecks on its run.
- The pace car picks up the leader: with the field frozen it is held on pit road and sent out to join the
  track just ahead of the leader, timed from what it measures at each caution and saved per track. No more half the field sent around the pace car.
- Pit road only counts the leader's times by once the pace car has the leader, so it can't open early.
- Lapped cars drop back on the low line (the track's limp line) while the lead lap cars are moved to the high
  groove to go by, and one to go waits until the lapped cars are behind them.
- Pit road timing is fixed to the realistic version (the Pit road tab is gone); Wave Around and Lucky Dog
  (renamed from Free pass) are on the Restarts tab; the live page sits on top of the Log tab.
- No car-to-car contact on pit road now works in practice and qualifying too.
- A truer clock: RaceControl times everything from the game's own tick clock (the one the line crossings
  are stamped with), so the race clock, the wave around lap credit and the qualifying timing box stay
  exact with time acceleration.
- With the Wave Around off, AIMOD no longer keeps lapped cars out waiting for one.
- Light and dark look (top right). Fixes: stale penalty stop flags cleared before the start, pace car and
  restart timing on short tracks, and more (the log explains every decision).

How to use
----------
1. Start NR2003. If Better Cautions is running, uncheck all its options.
2. Start RaceControl.exe. It asks for administrator rights because NR2003 runs as administrator.
3. It attaches by itself and works in every race. Hover over an option for what it does.
   The options are on tabs: Cautions, Restarts, Qualifying, and Log (with the live page on top).
   For the choose cone board and the lane chooser NR2003 must run windowed or borderless.

How it works
------------
Caution comes out
  The field freezes in its order (or races back, if you choose); at a stage end (see Stages) it races
  back to the line. With the field frozen RaceControl holds the pace car on pit road and sends it out
  just ahead of the leader (see Cautions). A lead lap car caught between the pace car and the leader (it had just crossed the line when the
  pace car pulled out) is sent around the pace car straight away. Pit road stays closed until the pace car has picked up the leader, those cars have
  caught the back of the field and the field is packed up: on a long track that can be before the leader
  first reaches pit road, usually it opens just before the leader's second time by, at the latest its
  third.
  Once the field is bunched up the first car one lap down gets the free pass (see Free pass).
First time by
  Each car's first time by is its first pass of pit entry with the pits open. A lapped car between the
  pace car and the leader that stays out is to be waved around: it keeps its spot until the pit stops
  are done, then goes around the pace car and gets its lap back when it crosses the line.
After the pit stops
  The lapped cars are sent to the back straight away, so the field has the rest of the caution to sort
  itself out: lead lap cars, then the lapped cars, then the waved cars at the tail.
One to go / double file
  Cars choose a lane at the cone, one at a time, in lineup order. When the cone comes early on the one
  to go lap and a lapped car is still in front of lead lap cars, the cone opens only once it has dropped
  behind them (at double file at the latest); cars already past the cone then choose at once. The board
  shows each choice and the spots it gained or lost. When you drive, the game pauses at the cone and you
  pick your lane.

Options (saved in RaceControl.ini next to the exe)
-------------------------------------------------
The preset is above the tabs; each part below is on its tab (Cautions and Stages on Cautions; Wave Around,
Lucky Dog, Restarts and the board on Restarts). Pit road has no options any more (2.9.1): see Pit road.
Preset
  Sets every option below in one go. Built in:
    Modern NASCAR (choose cone)          everything on, as RaceControl starts out
    Watching AI races                    the same, without pausing for your lane at the cone
    Double file, no cone (2009-2016)     double file in running order, no cone or board
    Wave around, single file             wave around and lapped cars to the back, single file restarts
    Classic 2003 (race back to the line) race back, no wave around, no green-white-checkered, single file
    NR2003 cautions + Lucky Dog          the game runs the cautions and restarts; RaceControl adds the Lucky Dog
  Save as... keeps the options as they are now as your own preset: presets\<name>.ini next to
  RaceControl.exe (Folder opens it; copy a file to share a preset). Delete removes your own presets.
  Change any option and the list shows (custom). A preset holds the checkboxes, EarlyLineup,
  RestorePaceLap and the board's size and seconds before scroll; lane and cone settings stay per track
  and the board stays where you placed it. A key left out of a preset file is not changed.
Cautions
  NR2003 runs cautions and        During a race RaceControl leaves the whole caution to NR2003: racing back,
  restarts (Lucky Dog only)       pit road, the pace car, one to go and the restart (single file, or NR2003's
                                  own "double file restarts" race option, which puts the lapped cars on the
                                  inside). RaceControl only gives the Lucky Dog (when that box is on) and runs
                                  qualifying; every other race option is ignored while this is on.
  Cars race back to the line      Off: the field freezes in its order when the caution comes out.
  Green-white-checkered finish    A caution in the last two laps adds laps to finish under green.
  Throw caution                   Button: a caution right now.
  Pace car picks up the leader    With the field frozen every car slows at once, and the game then sent the
  (field frozen)                  pace car out too early, in front of whoever was nearest: the lead lap cars
                                  between it and the leader all had to go around it. RaceControl holds the
                                  pace car on pit road and sends it out so that it joins the track just in
                                  front of the leader. At each caution (at normal speed) it measures where the
                                  pace car joins the track, how long that takes and how fast the leader goes
                                  meanwhile, and saves the release distance for the track (PaceRelease.<track>=
                                  in RaceControl.ini; 400 m until the first caution there). If the leader is
                                  too close to make it, the pace car waits for its next time by. The log says
                                  when it was sent out and which car ended up right behind it.
Stages
  Stage racing                    The race runs in 2, 3 or 4 stages; every stage but the last (which ends
                                  with the race) ends with a caution.
  End by: Track length (default)  3 stages ending at a % of the race set by the track's length:
                                    under 0.9 mi      21%, 51%       1.45 to 1.99 mi   28%, 62%
                                    0.9 to 1.45 mi    25%, 61%       1.99 mi and up    30%, 63%
                                  Charlotte at night: 4 stages at 25%, 50%, 75% (the 600).
  End by: % of race / Lap         Your own: how many stages, and where each stage but the last ends, as %
                                  of the race ("25, 61") or lap numbers ("84, 204").
                                  The line under the options shows the stage end laps for the race running.
  Throw the stage caution 3/4     RaceControl throws the stage caution when the leader is 3/4 through the
  through the last lap            stage's last lap, so the cars behind race back and the leader crosses the
                                  line first; nobody gets caught in front of the pace car. Off: another app
                                  throws it. A caution another app throws on the stage's last lap from 3/4
                                  of the way round, or at the line (leader at most a quarter lap past it), is
                                  the stage caution; a wreck earlier in that lap is a normal caution, and the
                                  stage ends under it.
  Race back at stage ends         At a stage end the field races back to the line and each car finishes the
                                  stage where it crosses. Other cautions follow "Cars race back to the line".
  Stage laps from a stage         Optional: the stage end laps come from a stage racing companion app (its
  racing companion app            stage racing settings and its log for this race) instead of the settings
  / Folder...                     above, so the stages match what that app shows. Folder... picks the app's
                                  data folder if it is not found on its own; the line under the options
                                  says what was found (or why not, for example its stage racing is off).
                                  Off, RaceControl runs the stages on its own. If that app also throws the stage caution,
                                  RaceControl's comes first (3/4 through the lap) and the other one lands
                                  under yellow and does nothing.
Pit road (with the Wave Around or the Choose cone on; classic rules leave pit road to the game - see Classic rules)
  Closed until the pace car has         Pit road stays closed until the pace car has picked up the leader and
  picked up the leader                  the field is lined up behind it (the game's own pit road gate is shut
                                        and AI pit requests are called off meanwhile; AIMOD waits too). It opens
                                        on the leader's first time by when the field is already nose to tail
                                        behind the pace car, else just before its second time by, counted only
                                        once the pace car has the leader (at the latest the third time by).
                                        Lead lap cars caught in front of the leader don't keep pit road shut
                                        for everyone: they are sent around the pace car and only they are kept
                                        off pit road until they have crossed the line after passing it.
                                        RaceControl learns where pit entry is from cars that pit (per track).
                                        The pits open a quarter lap before the point where cars start moving
                                        over for pit road, read from the track's own track.ini (on short tracks
                                        like Bristol that is well before pit lane itself), else before pit entry.
  Open to every car                     When pits open they are open to every car, lapped cars included (the
                                        modern rule: no black flag for a lapped car that pits).
  Hold one to go until the pit stops    (Restarts tab) The game shows one to go on its own schedule, which
  are done                              could come while most of the field was still on pit road. RaceControl
                                        keeps the caution going (at most 3 extra laps after pit road opens)
                                        until the pit stops are done and the field is lined up.
  No car-to-car contact on pit road     (Cautions tab) Cars pass through each other on pit road, in every
                                        session: practice and qualifying too.
Wave Around (Restarts tab)
  A lapped car between the pace car and the leader that doesn't pit its first time by is waved around
  the pace car once the pit stops are done (at one to go at the latest), when the cars that pitted are
  back in line behind it: it drives around to the tail of the field and restarts there, behind the
  lapped cars (NASCAR rule), one lap better off than it was: a car the leader has lapped once goes onto
  the lead lap. Driving around the pace car does not always gain that lap on track (a car just ahead of
  the leader ends up just behind it), so the lap is credited as soon as the car is around, and the
  standings show it during the caution. The wave is the same one the game's own wave command makes. If it pits before then it is
  not waved. A car that was on pit road for a green flag stop when the caution came out has not pitted
  under the caution and can be waved. A lead lap car caught in front of
  the leader is sent around at once, before pit road opens, and keeps its lead lap spot. A lapped car
  behind the leader is not waved. A waved car keeps going until it crosses the start/finish line (the
  lap it gains). Until it has passed the pace car it is at the end of the line, as in NR2003's own
  wave-by, so it drives around to the tail instead of following other cars.
  A car on pit road is moved to the end of the line while it is there, so cars on track don't pace behind
  it at pit road speed; back on track it goes to the end of the lead lap cars (or of the line, if lapped).
  Once the pit stops are done the lapped cars are sent to the back.
Lucky Dog (free pass, Restarts tab)
  Every caution the first car one lap down gets its lap back. It is chosen once the field is bunched up
  behind the pace car (before pit road opens) and goes around the pace car. Pit road does not wait for
  it: to pit with the field it has to be quick. It restarts at the tail of the field. A car under
  penalty is skipped. A car finishing a green flag stop when the caution comes out counts too: the
  free pass waits (up to a minute) for it to come off pit road. If it cannot get around the pace car
  within 45 s it drops to the tail and its lap is credited at the green. Checkbox "Lucky Dog".
  With classic rules (below) it is chosen as soon as the pace car has picked up the leader, before the lapped
  cars' pits open, from the laps down when the leader took the yellow.
Classic rules (Wave Around and Choose cone both off)
  The 2004 way: pit road opens on the game's own timing (lead lap cars first, lapped cars a lap later), no wave
  around, the Lucky Dog as above. With Double file restarts on, the field lines up at one to go with the lead lap
  cars single file on the outside and the lapped cars single file on the inside, each in running order; the
  Lucky Dog and penalized cars go to the tail of the longer line. No lapped cars: single file.
  Keep NR2003's own "double file restarts" race option off while RaceControl lines up the field.
Restarts
  Double file restarts   Cars pair up in lineup order. After a delayed green the double file is kept if
                         at least 3 of 4 cars are in their spot; otherwise it is redone. (Classic rules:
                         lead lap cars outside, lapped cars inside - see Classic rules.)
  Cone point             Metres from the line where cars choose (0 = where the pace car is when the
                         field goes double file); "My car's spot" takes it from your car, and
                         Track map... lets you click the spot on a drawing of the track. Cars stay
                         single file until they reach the cone, then choose one at a time, in lineup order.
                         A cone before the spot where the game calls double file (learned per track
                         at each restart, half a lap until then) is passed on the one to go lap, so
                         the cars choose there, soon after one to go - a cone just past the stripe
                         works. A cone later in the lap is chosen at during double file.
  I choose my lane       When you are driving, the game pauses just before you reach the cone and a
                         board shows the four rows ahead of you and the spot each lane gets you
                         (YOU?). The game stays paused until you pick: click one (or Left / Right,
                         Enter = suggested), a 2 s countdown shows, then the game resumes and you drive
                         on. RaceControl pauses NR2003 through the game's own pause; only if that is
                         not possible (another NR2003 build) it presses the pause key: PauseKey= in
                         RaceControl.ini (default Pause).
                         Turn it off when you only watch AI races.
  Choose cone            Each car picks a lane: the preferred lane for this track unless that costs
                         more rows than "rows a driver gives up"; in the front rows drivers take the
                         best spot whatever the lane. Lane settings are remembered per track.
                         Once a car has its lane it keeps it to the green.
  Untangle stuck cars    Two cars that stay in the wrong order (one waiting for the other) swap spots
                         (in double file only within the same lane). A lapped car is never let stay in
                         front of a lead lap car, and a car that has chosen its lane never trades with
                         one still waiting for the cone.
  No contact while pacing  Cars behind the pace car pass through each other; contact comes back per
                         car once it is clear of the others, not for everyone at the green.
  Lapped cars drop low,  A lapped car that has to drop behind lead lap cars drives the track's slow-car
  lead lap cars go high  line (limp.lp, below the groove) until they are past, and meanwhile the lead
                         lap cars' race line (race.lp in memory) is moved 70% of the way to the high
                         groove (the bound line nearer the wall, minrace.lp or maxrace.lp depending on
                         the track; LeadLapHighShare= in RaceControl.ini, 0 = off), so they get
                         by instead of queuing behind it. All goes back once the lapped cars are behind
                         them; the line files on disk are never changed. One to go waits until they are (the game takes every car off its line at
                         one to go; at most 3 extra laps after pit road opens, with Hold one to go on).
                         Tracks without a limp.lp: no change.
Track map
  Track map...           (Restarts tab) The track drawn from above with the start/finish line, PIT IN, PIT
                         OUT, DOUBLE FILE, the cone and the cars on track (pace car in gold). Hover for the
                         distance, click to put the cone there. The map draws itself from the AI cars the
                         first time any session runs at a track - practice or the race, the first lap or so
                         (more cars, faster) - and is saved once per track in
                         %PROGRAMDATA%\NR2003 Companions\tracks\<track>\map.json, where RaceVision finds it for
                         its camera map. Until then a map another overlay app keeps for the track stands in.
                         Record again draws it fresh.
Choose cone board
  Show the board         A TV style board over the game while the cars choose: five rows at a time,
                         filled in as each car picks its lane, with the spots it gained or lost; then
                         the rows slide away for the next five. Shown whether you drive or watch.
                         Positions: inside row n = 2n-1, outside row n = 2n (the leader keeps the lead).
  Art folder             Optional number art and headshots, laid out as series/<series>/numbers/<car>.png,
                         headshots/<car>.png, colors.json and serieslogo.png. RaceControl only reads
                         them: from an art folder next to the exe, then the folder chosen here (AIMOD's
                         art folder works, as does a companion app's art). Without any art the board
                         draws the numbers itself.
                         ArtSeries= in RaceControl.ini picks the series (otherwise the one that matches
                         the cars); ArtOverlayApp=0 stops it looking in overlay apps' folders.
  Size % / Place board   Size of the board (the lane chooser uses the same size). Place board... shows
                         it over the game: drag it, mouse wheel to size, Enter or double-click to save,
                         Esc to cancel. Reset puts it back on the left edge.
  Seconds before scroll  How long a full page of five rows stays up before the next five slide in.

Qualifying
  Run in the PRACTICE session just before qualifying, with the AI cars held in their pit boxes (stay out
  of the car and watch on the TV cameras). In the qualifying session after it every lap the AI post is
  replaced with these laps, so the leaderboard and the starting grid follow them. Your own laps are left
  alone. Let the qualifying session run (or sim it to the end); don't skip it.
  Format                 NASCAR Qualifying Types: Off (the game's own practice and qualifying), Single car runs
                         or Knockout rounds. Indy Qualifying Types: Oval 4-lap average, or Road / street (picking
                         one turns the NASCAR format off). Each type shows only its own settings.
  Indy road / street     Group 1, then Group 2 (minutes each, top N of each through), the Fast 12 (minutes, top N
                         through) and the Fast Six for pole, with a break between. Groups split by driver rating
                         (alternating, so they're even), by the running order list, or at random. Grid: Fast Six
                         1-6, the rest of the Fast 12 7-12, then from 13th Group 1 on odd spots, Group 2 on even.
  Indy oval              Single car runs of N consecutive timed laps (default 4, flying start) ranked by the
                         average of the laps; every lap counts, a run that isn't finished gets no time. The log
                         gives the average and mph. Uses the single car release, DNQ and max cars out settings.
  Timing box style       Classic (the small TV box) or Broadcast: driver, live telemetry (speed, gear, RPM,
                         throttle, brake), the lap block and the running order sliding in between runs and every
                         15 s during one; lime while on pole pace, red when slower.
  Lap times as           Time (m:ss.fff from a minute up) or Speed (mph from the track's official length in its
                         track.ini, as NR2003 shows speeds).
  Your car               Include my car in qualifying: you take your turn with the AI (single car runs). A
                         banner (Place the banner) shows YOUR RUN / GO NOW / OUT LAP / TIMED LAP / RUN COMPLETE /
                         DNQ with countdowns; the game pauses at your turn so you can set your camera.
  Single car runs        One car at a time rolls off its box, runs an out lap and its timed laps (1 to 5;
                         the out lap never counts) and comes back in. Next car: when the car before is on
                         pit road, N seconds after it left, when it is N% round its last lap, or "green N s
                         after the car before finishes". Practice needs about 1 to 1.5 minutes per car.
  Green gap release      Every pit box is a different distance from pit exit, so a fixed release point gives
                         a different gap on track. This mode times each car's release from its own predicted
                         out lap (learned from the cars already run: pit road speed, box to exit distance,
                         exit to the line, on the game's clock) so it takes the green about N s after the car
                         before ends its last timed lap. The log shows each release and the gap each car got.
  Max cars out           Never more than this many cars on their runs at once (in-laps count). Default 3.
  Running order          Get the field, then drag drivers into place (or Random draw). Cars not in the list
                         go after them in a random draw; an empty list is a random draw for everyone.
  DNQ                    A car that doesn't pull out of its box in time, or is out of its run before it
                         finishes a timed lap (calls a tow, is towed, limps in, goes to the garage, or comes
                         back to pit road by itself, e.g. out of fuel) gets no time and starts behind every
                         car with one; the next car goes at once. A timed lap it did finish stands as its
                         time. A spin it recovers from is not a DNQ.
                         Box timeout: 90 s by default. A lap over twice the usual is dropped.
                         If practice runs out mid-run, the timed laps a car finished still stand (no DNQ);
                         cars that never got to run go to the back.
  Knockout rounds        Up to 3 rounds: minutes, and how many cars go through (25 min top 24, 10 min top 12,
                         5 min for pole). Every car in a round is out until its time runs out, then finishes
                         the lap it is on and comes in. Out laps, in-laps and pit road never count. Grid: the
                         last round by its times, then each earlier round's other cars by that round's times.
                         Break: seconds between rounds.
  Start qualifying       Click once every car is sitting in its box (the count stops growing).
  Stop                   Lets the cars go for the rest of the session; no grid is written.
  Timing box             A TV style box over the game while qualifying runs: pole on top, the car on its run
                         below (the car on TV in knockout rounds). On every timed lap it shows the live gap to
                         pole at that point of the lap (green = faster, red = slower; no running clock), from
                         the game's own clock, so it is right with time acceleration too; between timed laps,
                         the lap just done for a moment. Single car runs: after the last timed lap, the run's
                         best lap, its gap to pole and its place for 5 s (never a live gap on the in-lap), then
                         the next car on its way to the green with -.-- until it takes the green. The box
                         follows the runs, not the camera (unless you put the camera on another car on a timed
                         lap yourself). Place the box: drag, mouse wheel to size, Enter to save.
  Qualifying pace        The AI get the speed bonus from their qualifying rating that the game gives them in
  for the AI             its own qualifying session (practice doesn't).
  Fuel load              Each car runs light, with this % of the tank (0 = leave the practice load).
  Match the stock        In the qualifying session the practice laps are scaled by the median of (the game's
  qualifying pace        own qualifying lap / that car's practice lap), so the times look like stock
                         qualifying times; the order stays as qualified. Needs 3 or more cars with both
                         laps; the factor is kept between 0.85 and 1.15 and the log gives it.
  A car that goes out when it shouldn't is called back in. The list shows the running order or the round's
  times; the log has every run. Qualifying status for broadcast tools (single car runs: also the car the
  timing box is on, so a TV director can keep the camera on it) is written to
  %PROGRAMDATA%\NR2003 Companions\qualifying_state.txt.

Live page
  Live page              A read-only web page with the race as RaceControl sees it, refreshed every
                         second: phase and lap, pits open or closed, the first time by (waved, pitted,
                         stayed out), the line (single file or the two lanes), the choose cone with
                         spots gained, the running order and the log. Click the link to open it.
                         Nothing can be changed from the page.
  Other computers too    Lets another computer, tablet or phone on your network open it, at the address
                         shown (http://<this PC>:8768/). Adds a Windows firewall rule for private
                         networks only; turning it off removes the rule. Port: LivePort= in the ini.
  For OBS / recording    Each panel on its own: /?view=status, ftb, line, cone, order or log, add &bare=1
                         for a see-through background. /board is the choose cone board (and your lane
                         chooser) on a transparent background, live as it animates - add it as a
                         Browser source. Game Capture records only NR2003 itself, not the board or other
                         apps' overlay windows: use Browser sources (or Display Capture) for those.

Penalties
  NR2003 gives "passing under yellow" (held for laps) to cars that pass under caution, and "passing on the
  inside" to cars that go low in a restart scramble. Under RaceControl the officials set the order, so AI
  cars are forgiven "passing under yellow" under caution and on the first green lap, and "passing on the
  inside" on the first green lap. Your own car keeps them unless RaceControl moved it.
  If the game has already called a forgiven car in to serve it, that pit stop is called off too.
  If the game flags a car to serve a penalty but no penalty shows for it for a second, that "phantom" penalty
  stop is called off as well - unless the game rates the car as needing repairs: NR2003 shows its own repair
  (limp) and tow stops with the same flag, and those are left to go ahead.
  ForgivePenalties=0 in RaceControl.ini turns this off. With "NR2003 runs cautions and restarts" nothing is
  forgiven.

Time acceleration
  RaceControl follows the game's clock, so it works with time acceleration, but AI pacing and restarts are
  less tidy at high speed-up. The log notes when it is on.

Advanced (RaceControl.ini only): EarlyLineup=0 sends lapped cars back at one to go instead;
RestorePaceLap=0 leaves waved cars' pace lap as the game set it after they cross the line.

Using it with other apps
------------------------
On its own        RaceControl needs nothing else. It runs the cautions, pit road timing, wave arounds,
                  lineups and restarts for every car.
With AIMOD        AIMOD decides when the AI cars pit and what they do (tyres, fuel, staying out); Race
                  Control decides when pit road is open and who is waved around. While RaceControl holds
                  the pits closed it tells AIMOD through %LOCALAPPDATA%\NR2003RaceControl\state.txt, and
                  AIMOD waits instead of asking its cars to pit. Each works without the other.
Overlay apps      Overlay, timing and stage racing companion apps that only read the game run alongside
                  it. RaceControl can show their number art on its board, and can take its stage laps
                  from a stage racing companion app (optional).
Wave around       With the wave around off, AIMOD (2.0.1 from 20 October 2026, and 2.1.0) no longer keeps lapped
                  cars out waiting for one: RaceControl tells it whether the wave around is on.
RaceVision        RaceVision 1.1.0 draws its camera map from RaceControl's track map, and follows the car the
                  qualifying timing box shows.
Live pages        RaceControl's live page is on port 8768; AIMOD's is on 8767. Both can be open at once.
Better Cautions   Not needed. If another app needs it running (for example a companion app's stage
                  cautions), keep all its options unchecked; with any on, the two fight over the pacing
                  line. RaceControl notes in the log when Better Cautions is running.

Log
---
race_control.log next to the exe: pit openings, wave arounds, penalties, lineups, double file / cone
lanes, untangles, line checks (cars not yet in their spot) and the lap check after the green.
Under caution each pit request is logged once ("pit request for #N under caution ..."), with the game's
penalty flag, any penalties and the value its AI pit check uses for fuel, so a car the game calls in by
itself shows why.
