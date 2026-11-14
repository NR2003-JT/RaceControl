using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RaceControl
{
    public class MainForm : Form
    {
        public const string Version = "3.3.12";   // 3.3.12: telemetry window (traces, scroll to zoom, TV strip, in-car wheel) + pace laps before the green (StartPaceLaps, RaceEngine.Start.cs); 3.3.11: run Q + the player's race banner (penalty on your car, free pass / wave around and what to do, FOLLOW #N in the caution line; RaceEngine.PlayerCall.cs from the main tree without its new-core branches); 3.3.10 (run Q): a leader just past the pace car (15% of a lap) waits for it whatever the pace car state, and with the hold off the pace car goes at once and the chase holds the leader; cars side by side at the yellow (15 m) that come out the other way round on track (40 m, 3 s) keep the order on track; a car on pit road at the yellow gives up its spot like a wreck car (end of the line, behind its group in the lineup); 3.3.9 (run P): pace lap fit also places the first car against the pace car (a car between the pace car and the leader is then a pace lap down and goes around on its own); send-arounds/waves set one lap down on the pace car instead of minus one, give-ups set the fitted value instead of plus one; the game lead time read when the pace car starts waiting (0x406820), not while parked; 3.3.8 (run O): pace laps fitted to the line at flags 0-1 (each car's pace lap makes the game's own distance sum equal its real gap to the car it follows; from NR2003.exe 0x52B070/0x52B120/0x414380), in place of tail catch-up and the chase's pace laps for cars other than the leader; ini PaceLapFit=0 = 3.3.7; 3.3.7 (run N): pace car chase only over once the pace car has gone by the leader or come out in front of it (not while parked); tail catch-up not helped again while still closing in; 3.3.6 (run M): a pace car sent out behind the leader catches it (cars between it and the leader get its pace lap and crawl; also a leader just past at the first look); tail catch-up eases off at braking distance from the car in front, the clump behind with it, and is not helped again while still closing in; 3.3.5 (run L): caution line from the order at the yellow (stopped cars to the end); free pass at the first chance again; tail catch-up through flag 2 only when the spot is in front with the pace car not between; 3.3.4 (run K): untangling looser at the back (lapped or last quarter: 150 m, 3 s); 3.3.3 (run K): tail catch-up only while the pace car is on pit road; wave arounds and the free pass at one to go, checked again then, queued cars keep their spot in front; 3.3.2 (run J): tail catch-up (a pace lap less for cars far behind their car in the line until the field is packed); 3.3.1 (run I): caution line put in running order from the leader when the game cut it at the start/finish line; 3.3.0 (run H): pace car goes the moment the leader drives past it (the game makes it catch the leader from behind; held, the field crawled a lap); 3.2.9 (run G): leader speed from its velocity, stopped leader skipped, hold a lap at the crawl, no learning from slow leaders, PaceMatch off; 3.2.8 (run F): pace car speed matched to the leader (0x74B080); 3.2.7: release from the leader's speed, pace car held while parked, exit saved per track; 3.2.6 (run E): window floor 60%, crawl release long tracks only, no wait for a car between, learning guards; 3.2.5 (run D): pace car waits for a clear leader, narrower window and learning on short tracks; 3.2.4 (run C): clock, leader speed, target, hold limit, PaceFixes on

        readonly Dictionary<string, CheckBox> checks = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, string> settings = new Dictionary<string, string>();
        readonly ComboBox laneBox, stageCountBox, stageModeBox;
        readonly TextBox stageEndsBox;
        readonly Label stageInfo;
        readonly NumericUpDown giveUpBox, frontRowsBox, coneBox, startPaceBox;
        readonly Label laneLabel, status;
        readonly ListBox logBox;
        readonly Timer tick, slowTimer;
        readonly string logPath, iniPath;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };

        ProcessMemory mem;
        RaceEngine engine;
        string attachError, track;
        bool loading, bcWarned;
        ConeChooser chooser;
        bool pausedForChooser;
        QualReadyForm qualReady;                    // your qualifying turn: game paused until you are ready
        bool qualReadySeenPaused;
        DateTime qualReadyAt;
        readonly ConeArt art = new ConeArt();
        readonly Label artLabel;
        ConeBoardForm board;
        ConeBoardData boardShown;
        DateTime greenAt = DateTime.MaxValue;
        BoardLayout boardLayout = new BoardLayout();
        readonly NumericUpDown boardSizeBox, boardHoldBox;
        ConeBoardForm placing;
        QualBoxForm qualBox, qualPlacing;                  // the qualifying timing box and its placing copy
        PlayerBannerForm banner;                           // calls to the player over the game (qualifying: your turn, come in...)
        static bool QCar(int c) { return c == Addr.PlayerIdx || c >= 2; }   // a car that can be on a qualifying run (not the pace car)
        QualBroadcastForm qualBroad;                       // the broadcast style qualifying overlay
        DateTime broadAt = DateTime.MinValue; string broadKey;
        ComboBox qualStyleBox;
        readonly Dictionary<int, float> rpmTop = new Dictionary<int, float>();   // the highest RPM seen per car (the bar's full scale)
        // AI telemetry (Telemetry.cs): every qualifying car sampled ~30 times a game second while qualifying is live; the
        // broadcast box's brake, the in-game strip and the telemetry window all read it
        readonly TeleRecorder tele = new TeleRecorder();
        // outside custom qualifying (races and the rest) the strip follows the car on TV: its own recorder, with a timing
        // that never runs, so it keeps only the last few seconds and records no laps
        readonly TeleRecorder tvTele = new TeleRecorder();
        readonly QualTiming noTiming = new QualTiming();
        TeleStripForm teleStrip, stripPlacing;
        BoardLayout stripLayout = new BoardLayout();
        DateTime stripAt = DateTime.MinValue;
        TelemetryWindow teleWin;
        bool wheelPatched; int wheelPid = -1;   // WheelAnim.cs: the in-car wheel patch is in this NR2003 process
        ComboBox qualLapUnitBox;
        int orderCar = -1, orderLapStart = -2; DateTime orderUntil = DateTime.MinValue;
        readonly Label[] stageLabels = new Label[3], stageUnits = new Label[3];
        readonly TextBox[] stageVals = new TextBox[3];
        bool stageSync, roundSync;
        readonly Label[] roundLabels = new Label[4], roundMidLabels = new Label[4], roundEndLabels = new Label[4];
        readonly NumericUpDown[] roundMins = new NumericUpDown[4], roundCars = new NumericUpDown[4];
        ListBox orderList;
        List<string> orderOrder = new List<string>();                          // car numbers, in running order
        readonly Dictionary<string, string> orderNames = new Dictionary<string, string>();
        BoardLayout qualBoxLayout = new BoardLayout();
        BoardLayout bannerLayout = new BoardLayout();          // the calls to the player (X/Y unset = centred at the top)
        PlayerBannerForm bannerPlacing;
        readonly ComboBox presetBox;
        readonly Button presetDelete;
        bool presetSync;
        LiveServer live;
        readonly LinkLabel liveLink;
        DateTime lastLiveJson, lastBoardPng;
        static readonly HashSet<string> NotInPresets = new HashSet<string> { "LivePage", "LiveNetwork" };
        readonly TabControl tabs;
        Control page;                                           // the tab the next groups go on
        readonly Qualifying qual = new Qualifying();
        // the track map (cone map window): recorded from the cars, one per track
        readonly TrackMapRecorder mapRec = new TrackMapRecorder();
        TrackMap trackMap;
        string mapTrack;
        TrackMapForm mapForm;
        readonly ComboBox qualFormatBox, qualIndyBox, qualIndySplitBox;
        readonly NumericUpDown qualIndyLapsBox, qualIndyGroupMin, qualIndyGroupAdv, qualIndyF12Min, qualIndyF12Adv, qualIndyF6Min, qualIndyBreak;
        // the Qualifying tab's groups are restacked when a format shows or hides its own fields
        GroupBox qualNascarGroup, qualIndyGroup; Control qualPage; Label qualKoLabel, qualBreakLabel, qualMaxOutLabel, qualIndyLapsLabel, qualIndyHint;
        readonly List<Control> qualIndyRoadCtls = new List<Control>();
        readonly NumericUpDown qualLapsBox, qualBreakBox, qualDnqBox, qualReleaseVal, qualMaxOutBox;
        NumericUpDown qualFuelBox, qualBoostBox;
        // Indy oval re-qualifying lanes, the field size (DNQs), the player's lane buttons
        CheckBox qualReQualBox, qualWaveOffBox; NumericUpDown qualReQualMin, qualReQualRuns, qualStartersBox; ComboBox qualStartersUnit; CheckBox qualScratchBox;
        readonly List<Control> qualOvalCtls = new List<Control>();
        Button laneBtn1, laneBtn2, laneOutBtn; Label qualLaneLabel;
        readonly ComboBox qualReleaseBox;
        readonly TextBox qualRoundsBox, qualOrderBox;
        readonly Label qualStatus, qualNeeds;
        readonly Button qualStart, qualStop;
        readonly ListBox qualBoard;
        DateTime lastQualTick;

        public MainForm()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(dir, "race_control.log");
            iniPath = Path.Combine(dir, "RaceControl.ini");

            Text = "RaceControl";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(620, 640);
            MinimumSize = new Size(560, 480);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            // the RaceControl logo and name across the top
            Image logo = null;
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("RaceControl.logo.png"))
                    if (s != null) logo = new Bitmap(Image.FromStream(s));
            }
            catch (Exception) { }
            if (logo != null)
                Controls.Add(new PictureBox { Left = 12, Top = 4, Width = 40, Height = 40, SizeMode = PictureBoxSizeMode.Zoom, Image = logo });
            Controls.Add(new Label { Left = logo != null ? 58 : 12, Top = 5, AutoSize = true, Text = "RaceControl", Font = new Font("Segoe UI", 13f, FontStyle.Bold) });
            Controls.Add(new Label { Left = logo != null ? 60 : 14, Top = 29, AutoSize = true, Text = "Caution and restart control for NASCAR Racing 2003 Season", ForeColor = SystemColors.GrayText });

            int y = 50;
            page = this;
            GroupBox g = Group("Preset", ref y, 48);
            presetBox = new ComboBox { Left = 10, Top = 18, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 20 };
            tips.SetToolTip(presetBox, "Choose a preset to set every option below in one go (lane and cone settings stay per track,\n" +
                "the board's place stays where you put it). Change anything and it shows (custom).");
            presetBox.SelectedIndexChanged += (s, e) => { if (!presetSync) ApplyPreset(presetBox.SelectedItem as Preset); };
            g.Controls.Add(presetBox);
            var presetSave = new Button { Text = "Save as...", Left = 320, Top = 16, Width = 85, Height = 26 };
            tips.SetToolTip(presetSave, "Saves the options as they are now as your own preset, in the presets folder next to\n" +
                "RaceControl.exe (one .ini per preset: copy it to share it).");
            presetSave.Click += (s, e) => SavePreset();
            g.Controls.Add(presetSave);
            presetDelete = new Button { Text = "Delete", Left = 411, Top = 16, Width = 65, Height = 26 };
            presetDelete.Click += (s, e) => DeletePreset();
            g.Controls.Add(presetDelete);
            var presetFolder = new Button { Text = "Folder", Left = 482, Top = 16, Width = 65, Height = 26 };
            tips.SetToolTip(presetFolder, "Opens the presets folder.");
            presetFolder.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(PresetStore.Folder); Process.Start(PresetStore.Folder); } catch (Exception ex) { Show(Stamp + ex.Message); }
            };
            g.Controls.Add(presetFolder);

            status = new Label { Left = 12, Top = y, Width = 596, Height = 20, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(status);
            tabs = new TabControl { Left = 12, Top = y + 22, Width = 596, Height = ClientSize.Height - y - 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(tabs);

            y = Tab("Cautions");
            g = Group("Cautions", ref y, 136);
            Check(g, "RaceBack", "Cars race back to the line", 10, 20, false,
                "Off: the field is frozen in its order when the caution comes out (no racing back).");
            Check(g, "GreenWhiteCheckered", "Green-white-checkered finish", 10, 42, true,
                "A caution in the last two laps adds laps so the race can finish under green.");
            var throwBtn = new Button { Text = "Throw caution", Left = 440, Top = 26, Width = 130, Height = 28 };
            throwBtn.Click += (s, e) => { if (engine != null) engine.ThrowCaution(); };
            g.Controls.Add(throwBtn);
            Check(g, "PaceCarWaits", "Pace car picks up the leader (field frozen)", 10, 64, true,
                "With the field frozen at the caution every car slows at once, and the game sends the pace car out too\n" +
                "early: in front of whoever is nearest, so the lead lap cars between it and the leader have to go around it.\n" +
                "RaceControl holds the pace car on pit road and sends it out when the leader is a set distance from it,\n" +
                "learned per track: after each caution it is moved by how far in front of or behind the leader the pace\n" +
                "car came out. If the leader is too close to make it, the pace car waits for its next time by.");
            Check(g, "GhostOnPitRoad", "No car-to-car contact on pit road", 10, 86, true,
                "Cars on pit road pass through each other, in every session (practice and qualifying too). Contact comes back\n" +
                "once a car is clear of the others.");
            Check(g, "StockCautions", "NR2003 runs cautions and restarts (RaceControl: Lucky Dog only)", 10, 108, false,
                "On: during a race RaceControl leaves the whole caution to NR2003 - racing back, pit road, the pace car,\n" +
                "one to go and the restart (single file, or NR2003's own 'double file restarts' race option, which puts the\n" +
                "lapped cars on the inside). RaceControl only gives the Lucky Dog (when that box is on) and runs qualifying.\n" +
                "Nothing else is written in a race: no lineups, no ghosting, no penalty clearing, no stages, no\n" +
                "green-white-checkered. Every other race option is ignored while this is on.");

            g = Group("Stages", ref y, 198);
            Check(g, "StageRacing", "Stage racing", 10, 20, true,
                "The race is run in stages; each stage but the last (which ends with the race) ends with a caution.");
            g.Controls.Add(new Label { Left = 120, Top = 22, Width = 45, Text = "Stages:" });
            stageCountBox = new ComboBox { Left = 168, Top = 18, Width = 45, DropDownStyle = ComboBoxStyle.DropDownList };
            stageCountBox.Items.AddRange(new object[] { "2", "3", "4" });
            g.Controls.Add(stageCountBox);
            g.Controls.Add(new Label { Left = 222, Top = 22, Width = 50, Text = "End by:" });
            stageModeBox = new ComboBox { Left = 274, Top = 18, Width = 95, DropDownStyle = ComboBoxStyle.DropDownList };
            stageModeBox.Items.AddRange(new object[] { "Track length", "% of race", "Lap" });
            tips.SetToolTip(stageModeBox, "Track length: 3 stages, ending at 21/51% under 0.9 mi, 25/61% to 1.45 mi, 28/62% to 1.99 mi and\n" +
                "30/63% from 1.99 mi; Charlotte at night 4 stages at 25/50/75%. % of race / Lap: the stages and values set here.");
            g.Controls.Add(stageModeBox);
            stageEndsBox = new TextBox { Left = 376, Top = 18, Width = 190, Visible = false };   // kept as the store: "25, 61"
            tips.SetToolTip(stageEndsBox, "Where each stage but the last ends, separated by commas: % of the race (25, 61) or lap numbers (84, 204).\n" +
                "With 3 stages give 2 values, with 4 stages 3 values.");
            g.Controls.Add(stageEndsBox);
            for (int i = 0; i < 3; i++)
            {
                int top = 48 + i * 26;
                stageLabels[i] = new Label { Left = 30, Top = top + 3, Width = 110, Text = "Stage " + (i + 1) + " ends at" };
                stageVals[i] = new TextBox { Left = 145, Top = top, Width = 60 };
                stageUnits[i] = new Label { Left = 212, Top = top + 3, Width = 200, ForeColor = SystemColors.GrayText };
                tips.SetToolTip(stageVals[i], "Where stage " + (i + 1) + " ends: % of the race or a lap number (see End by). The last stage ends with the race.");
                stageVals[i].TextChanged += (s2, e2) => { if (!stageSync) StageTextFromBoxes(); };
                g.Controls.Add(stageLabels[i]); g.Controls.Add(stageVals[i]); g.Controls.Add(stageUnits[i]);
            }
            Check(g, "StageThrowCaution", "Throw the stage caution 3/4 through the last lap", 10, 128, true,
                "RaceControl throws the stage caution when the leader is 3/4 through the stage's last lap, so the cars race back\n" +
                "and the leader crosses the line first. Off: another app throws it (a caution near the stage end is then\n" +
                "treated as the stage caution).");
            Check(g, "StageRaceBack", "Race back at stage ends", 330, 128, true,
                "At a stage end the field races back to the line and each car finishes the stage where it crosses.\n" +
                "Other cautions follow 'Cars race back to the line'.");
            Check(g, "StageFromCompanionApp", "Stage laps from a stage racing companion app", 10, 150, false,
                "Optional. On: the stage end laps come from a stage racing companion app (its stage racing settings and its\n" +
                "log for this race), so RaceControl's stages match what that app shows. RaceControl only reads its files.\n" +
                "Folder... picks the app's data folder (also used for its series art). Off: the stage settings above.");
            var stageFolderBtn = new Button { Text = "Folder...", Top = 146, Width = 80, Height = 24 };
            stageFolderBtn.Left = checks["StageFromCompanionApp"].Left + checks["StageFromCompanionApp"].PreferredSize.Width + 10;
            tips.SetToolTip(stageFolderBtn, "The stage racing companion app's data folder (where its settings are kept). Cancel = the usual place.");
            stageFolderBtn.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "The stage racing companion app's data folder (Cancel = none).", SelectedPath = CompanionAppStages.Folder })
                    settings["StageCompanionFolder"] = dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : "";
                CompanionAppStages.Folder = settings["StageCompanionFolder"];
                SaveSettings();
                ShowStages();
            };
            g.Controls.Add(stageFolderBtn);
            stageInfo = new Label { Left = 10, Top = 176, Width = 556, Text = "" };
            g.Controls.Add(stageInfo);
            EventHandler stageChanged = (s, e) => { if (loading) return; SaveStageSettings(); };
            stageCountBox.SelectedIndexChanged += stageChanged;
            stageModeBox.SelectedIndexChanged += stageChanged;
            stageEndsBox.TextChanged += stageChanged;
            stageCountBox.SelectedIndexChanged += (s2, e2) => StageRowsUi();
            stageModeBox.SelectedIndexChanged += (s2, e2) => StageRowsUi();
            stageEndsBox.EnabledChanged += (s2, e2) => StageRowsUi();

            y = Tab("Restarts");
            g = Group("Wave Around and Lucky Dog", ref y, 70);
            Check(g, "WaveAround", "Wave Around", 10, 20, true,
                "Pit road opens to every car once the pace car has picked up the leader and the field is behind it. Lapped cars\n" +
                "between the pace car and the first lead lap car that don't pit their first time by are waved around the pace car\n" +
                "and get their lap back. Once the pit stops are done the lapped cars are sent to the back (on the low line, see\n" +
                "below), giving the field the rest of the caution to sort out; cars that pit after that are placed again at one to go.");
            Check(g, "FreePass", "Lucky Dog", 200, 20, true,
                "Each caution the first car one lap down gets its lap back (the free pass): once the field is bunched up it goes\n" +
                "around the pace car, pit road waits until it has caught the tail so it can pit with everyone, and it restarts at\n" +
                "the tail. With the Wave Around and the Choose cone both off (classic rules) it goes around once the pace car has\n" +
                "picked up the leader, before the lapped cars' pits open; pit road timing is then left to the game.");
            Check(g, "HoldOneToGo", "Hold one to go until the pit stops are done", 10, 42, true,
                "The game shows one to go on its own schedule, which could come while most of the field was still on pit road.\n" +
                "RaceControl keeps the caution going (up to 3 extra laps after pit road opens) until the pit stops are done and\n" +
                "the field is lined up.");
            Check(g, "HoldGreenIfSplit", "Hold the green if the pack is split (test)", 300, 42, false,
                "TEST: at one to go or double file, if there is a gap of more than 60 m in the line, RaceControl asks the game for\n" +
                "another caution lap (once per caution), like the game's own delayed green. Whether the game honours it this late\n" +
                "is what this tests - the log says what happened (\"field split: ...\"). Off by default.");

            g = Group("Restarts", ref y, 222);
            Check(g, "DoubleFile", "Double file restarts", 10, 20, true,
                "At one to go the cars pair up in the order they are running behind the pace car. Redone after a delayed green.\n" +
                "Classic rules (Wave Around and Choose cone off): 2004 style at one to go - lead lap cars single file on the outside,\n" +
                "lapped cars single file on the inside (laps down as they were when the leader took the yellow), the Lucky Dog and\n" +
                "penalized cars at the tail of the longer line. No lapped cars: single file.");
            Check(g, "Cone", "Choose cone", 200, 20, true,
                "Instead of strictly alternating, each car picks a lane: the preferred lane unless it costs more rows than the\n" +
                "driver will give up. Up front drivers take the best spot whatever the lane.");
            laneLabel = new Label { Left = 10, Top = 48, Width = 330, Text = "Preferred lane:" };
            g.Controls.Add(laneLabel);
            laneBox = new ComboBox { Left = 345, Top = 45, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            laneBox.Items.AddRange(new object[] { "No preference", "Inside", "Outside" });
            g.Controls.Add(laneBox);
            g.Controls.Add(new Label { Left = 10, Top = 76, Width = 330, Text = "Rows a driver gives up for the preferred lane:" });
            giveUpBox = new NumericUpDown { Left = 345, Top = 73, Width = 60, Minimum = 0, Maximum = 10, Value = 2 };
            g.Controls.Add(giveUpBox);
            g.Controls.Add(new Label { Left = 10, Top = 100, Width = 330, Text = "Front rows that take the best spot regardless of lane:" });
            frontRowsBox = new NumericUpDown { Left = 345, Top = 97, Width = 60, Minimum = 0, Maximum = 10, Value = 3 };
            g.Controls.Add(frontRowsBox);
            g.Controls.Add(new Label { Left = 10, Top = 124, Width = 330, Text = "Cone, metres from the line (0 = where the pace car is):" });
            coneBox = new NumericUpDown { Left = 345, Top = 121, Width = 70, Minimum = 0, Maximum = 10000, Value = 0 };
            g.Controls.Add(coneBox);
            var coneHere = new Button { Text = "My car's spot", Left = 425, Top = 119, Width = 140, Height = 26 };
            tips.SetToolTip(coneHere, "Drive to where the cone should be and click: cars stay single file until there, then choose. It must be before the start/finish line (between where the pace car is at double file and the line); a cone past the line is moved to the pace car's spot.");
            coneHere.Click += (s, e) =>
            {
                double d = engine != null ? engine.PlayerTrackPos() : double.NaN;
                if (double.IsNaN(d)) { Show(Stamp + "no car of yours on track to take the cone spot from"); return; }
                coneBox.Value = Math.Max(1, Math.Min(10000, (decimal)Math.Round(d)));
            };
            g.Controls.Add(coneHere);
            var mapBtn = new Button { Text = "Track map...", Left = 425, Top = 146, Width = 140, Height = 26 };
            tips.SetToolTip(mapBtn, "The track drawn from above: click where the cone should go. Pit entry, pit exit and where the field\n" +
                "goes double file are marked. The map draws itself from the AI cars the first time a session runs at a track\n" +
                "(the first lap or so) - you don't have to drive.");
            mapBtn.Click += (s, e) => ShowTrackMap();
            g.Controls.Add(mapBtn);
            Check(g, "PlayerChoosesLane", "I choose my lane at the cone (pauses the game)", 10, 150, true,
                "When you are driving, the game pauses just before you reach the cone and a chart shows the lanes the cars\n" +
                "ahead chose and the spot each lane gets you. Click a lane (or Left / Right); a 2 s countdown, then the game\n" +
                "resumes and you drive on. NR2003 must run windowed or borderless.");
            Check(g, "Untangle", "Untangle stuck cars", 10, 174, true,
                "Two cars that stay nose to tail in the wrong order (one waiting for the other to go by) swap spots and keep\n" +
                "the order they have on track: after 12 s while pacing, 6 s once double file.");
            Check(g, "LappedDropLow", "Lapped cars drop low, lead lap cars go high", 10, 198, true,
                "A lapped car that has to drop behind lead lap cars drives the track's slow-car line (limp.lp, below the\n" +
                "groove), and meanwhile the lead lap cars' race line is moved toward the high groove (the bound nearer the wall; 70%,\n" +
                "LeadLapHighShare= in RaceControl.ini, 0 = off), so they get by instead of being stuck behind it. Everything\n" +
                "goes back once the lapped cars are behind them, and at one to go. Tracks without a limp.lp are left alone.");
            Check(g, "GhostWhilePacing", "No contact while pacing", 200, 174, false,
                "Cars behind the pace car pass through each other (as Better Cautions' 'disable caution collisions').\n" +
                "Contact comes back per car once it is clear of the others, instead of for everyone at the green.");

            g = Group("Race start", ref y, 50);
            g.Controls.Add(new Label { Left = 10, Top = 23, Width = 330, Text = "Pace laps before the green (1 = the game's own):" });
            startPaceBox = new NumericUpDown { Left = 345, Top = 20, Width = 60, Minimum = 1, Maximum = 10, Value = 1 };
            tips.SetToolTip(startPaceBox, "NR2003 always gives one pace lap before the start. More than 1: RaceControl keeps the game's\n" +
                "delayed green going until the field has run this many pace laps, then one to go and the green as usual.\n" +
                "If the game counts those laps toward the race, they are added back to the race length at the green.");
            g.Controls.Add(startPaceBox);
            startPaceBox.ValueChanged += (s, e) => { if (loading) return; settings["StartPaceLaps"] = ((int)startPaceBox.Value).ToString(); SaveSettings(); ApplyOptions(); };

            g = Group("Choose cone board", ref y, 84);
            Check(g, "ShowConeBoard", "Show the board as the cars choose", 10, 22, true,
                "A TV style board over the game: the lanes the cars choose, five rows at a time, with the spots each car\n" +
                "gained or lost. Shown whether you are driving or watching. NR2003 must run windowed or borderless.");
            var artBtn = new Button { Text = "Art folder...", Left = 250, Top = 18, Width = 100, Height = 26 };
            tips.SetToolTip(artBtn, "Number art and headshots, laid out as series/<series>/numbers and headshots (AIMOD's art folder\n" +
                "works, as does a companion app's). RaceControl looks in its own art folder,\n" +
                "then this one. Cancel = no extra folder.");
            artBtn.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "Folder with series/<series>/numbers and headshots (for example AIMOD's art folder). Cancel clears it.", SelectedPath = art.ExtraFolder ?? "" })
                    settings["ArtFolder"] = dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : "";
                SaveSettings();
                ApplyOptions();
            };
            g.Controls.Add(artBtn);
            artLabel = new Label { Left = 356, Top = 23, Width = 214, Height = 18, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
            g.Controls.Add(artLabel);
            g.Controls.Add(new Label { Left = 10, Top = 55, Width = 40, Text = "Size %" });
            boardSizeBox = new NumericUpDown { Left = 52, Top = 52, Width = 55, Minimum = 40, Maximum = 200, Increment = 5, Value = 100 };
            g.Controls.Add(boardSizeBox);
            g.Controls.Add(new Label { Left = 120, Top = 55, Width = 128, Text = "Seconds before scroll" });
            boardHoldBox = new NumericUpDown { Left = 250, Top = 52, Width = 55, Minimum = 1, Maximum = 30, DecimalPlaces = 1, Increment = 0.5m, Value = 3.5m };
            tips.SetToolTip(boardHoldBox, "How long a full page of five rows stays up before the rows slide away for the next five.");
            g.Controls.Add(boardHoldBox);
            var placeBtn = new Button { Text = "Place board...", Left = 320, Top = 50, Width = 110, Height = 26 };
            tips.SetToolTip(placeBtn, "Shows the board over the game: drag it where you want it, mouse wheel to size,\nEnter or double-click to save, Esc to cancel. The lane chooser uses the same size.");
            placeBtn.Click += (s, e) => PlaceBoard();
            g.Controls.Add(placeBtn);
            var resetBtn = new Button { Text = "Reset", Left = 436, Top = 50, Width = 60, Height = 26 };
            resetBtn.Click += (s, e) => { boardLayout = new BoardLayout(); SaveBoardLayout(); };
            g.Controls.Add(resetBtn);
            boardSizeBox.ValueChanged += (s, e) => { if (loading) return; boardLayout.Scale = (double)boardSizeBox.Value / 100; SaveBoardLayout(); };
            boardHoldBox.ValueChanged += (s, e) => { if (loading) return; boardLayout.Hold = (double)boardHoldBox.Value; SaveBoardLayout(); };

            y = Tab("Qualifying");
            g = Group("NASCAR Qualifying Types", ref y, 268);
            qualNascarGroup = g; qualPage = page;
            g.Controls.Add(new Label { Left = 10, Top = 22, Width = 60, Text = "Format:" });
            qualFormatBox = new ComboBox { Left = 75, Top = 18, Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
            qualFormatBox.Items.AddRange(new object[] { "Off", "Single car runs", "Knockout rounds" });
            tips.SetToolTip(qualFormatBox, "Qualifying is run in the PRACTICE session just before qualifying (make it long enough), with the AI\n" +
                "cars held in their pit boxes. In the qualifying session after it the AI's laps are replaced with these, so the\n" +
                "leaderboard and the starting grid follow them. Your own laps are never touched.\n" +
                "Single car runs: one car at a time rolls off, runs an out lap and its timed laps, and comes back in.\n" +
                "Knockout rounds: every car in a round is out until its time runs out; the fastest go through to the next.");
            g.Controls.Add(qualFormatBox);
            g.Controls.Add(new Label { Left = 255, Top = 22, Width = 62, Text = "Release at:" });
            qualReleaseBox = new ComboBox { Left = 318, Top = 18, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
            qualReleaseBox.Items.AddRange(new object[] { "when the car before is on pit road", "N seconds after the car before left", "when the car before is N% round its last lap", "green N s after the car before finishes" });
            qualReleaseBox.DropDownWidth = 260;
            tips.SetToolTip(qualReleaseBox, "When the next single car leaves its pit box.\n" +
                "Green N s after the car before finishes: every box is a different distance from pit exit, so this times each\n" +
                "car's release from its own predicted out lap (learned from the cars already run: pit road speed, box to exit\n" +
                "distance, exit to the line) so it takes the green about N seconds after the car before ends its last timed lap.\n" +
                "The first pair can be late: there is no out lap to learn from until the first car has taken the green.\n" +
                "The log shows each release and the gap each car actually got.");
            g.Controls.Add(qualReleaseBox);
            qualReleaseVal = new NumericUpDown { Left = 514, Top = 18, Width = 52, Minimum = 0, Maximum = 600, Value = 30 };
            tips.SetToolTip(qualReleaseVal, "N: seconds after the car before left its box, % of the way round its final timed lap,\n" +
                "or (green gap) seconds between the car before finishing and this car taking the green.");
            g.Controls.Add(qualReleaseVal);
            qualMaxOutLabel = new Label { Left = 10, Top = 213, Width = 235, Text = "Single car: max cars out at once" };
            g.Controls.Add(qualMaxOutLabel);
            qualMaxOutBox = new NumericUpDown { Left = 250, Top = 209, Width = 50, Minimum = 1, Maximum = 20, Value = 3 };
            tips.SetToolTip(qualMaxOutBox, "Single car: never more than this many cars out on their runs at once (a car on its in-lap counts until it\n" +
                "reaches pit road). Raise it for long road courses with a short release interval.");
            g.Controls.Add(qualMaxOutBox);
            g.Controls.Add(new Label { Left = 10, Top = 54, Width = 235, Text = "Single car: timed laps per car" });
            qualLapsBox = new NumericUpDown { Left = 250, Top = 51, Width = 50, Minimum = 1, Maximum = 5, Value = 1 };
            g.Controls.Add(new Label { Left = 320, Top = 54, Width = 182, Text = "DNQ if still in its box after (s)" });
            qualDnqBox = new NumericUpDown { Left = 506, Top = 51, Width = 60, Minimum = 15, Maximum = 600, Value = 90 };
            tips.SetToolTip(qualDnqBox, "Single car: a car that is told to go but doesn't pull out of its pit box in this many seconds is\n" +
                "a DNQ: it stays in its box, starts at the back, and the next car goes.");
            g.Controls.Add(qualDnqBox);
            tips.SetToolTip(qualLapsBox, "The out lap never counts: the first time over the line after leaving pit road starts the timed lap.");
            g.Controls.Add(qualLapsBox);
            g.Controls.Add(new Label { Left = 10, Top = 82, Width = 235, Text = "Single car: running order (car numbers)", Visible = false });
            qualOrderBox = new TextBox { Left = 250, Top = 79, Width = 316, Visible = false };   // the store; the list below edits it
            tips.SetToolTip(qualOrderBox, "Car numbers in the order they go out, separated by spaces or commas. Cars not listed go after them in a\n" +
                "random draw. Empty: a random draw for everyone.");
            g.Controls.Add(qualOrderBox);
            qualKoLabel = new Label { Left = 10, Top = 84, Width = 235, Text = "Knockout rounds:" };
            g.Controls.Add(qualKoLabel);
            qualRoundsBox = new TextBox { Left = 250, Top = 107, Width = 150, Visible = false };   // the store: "25:24, 10:12, 5:12"
            tips.SetToolTip(qualRoundsBox, "25:24, 10:12, 5:12 = 25 minutes, the top 24 go through; 10 minutes, the top 12 go through; 5 minutes for pole.\n" +
                "Grid: the last round by its times, then each earlier round's other cars by that round's times.\n" +
                "When time runs out cars finish the lap they are on; out laps, in-laps and pit road never count.");
            g.Controls.Add(qualRoundsBox);
            for (int i = 0; i < 4; i++)
            {
                int top = 104 + i * 26;
                roundLabels[i] = new Label { Left = 30, Top = top + 3, Width = 60, Text = "Round " + (i + 1) };
                roundMins[i] = new NumericUpDown { Left = 95, Top = top, Width = 55, Minimum = 0, Maximum = 180, DecimalPlaces = 0 };
                roundMidLabels[i] = new Label { Left = 155, Top = top + 3, Width = 80, Text = "minutes, top" };
                roundCars[i] = new NumericUpDown { Left = 238, Top = top, Width = 50, Minimum = 1, Maximum = 60 };
                roundEndLabels[i] = new Label { Left = 293, Top = top + 3, Width = 110, Text = "go through" };
                tips.SetToolTip(roundMins[i], "Round " + (i + 1) + " length in minutes. 0 = no such round.");
                tips.SetToolTip(roundCars[i], "How many cars go through to the next round (not used in the last round).");
                roundMins[i].ValueChanged += (s2, e2) => { if (!roundSync) RoundsTextFromRows(); };
                roundCars[i].ValueChanged += (s2, e2) => { if (!roundSync) RoundsTextFromRows(); };
                g.Controls.Add(roundLabels[i]); g.Controls.Add(roundMins[i]); g.Controls.Add(roundMidLabels[i]);
                g.Controls.Add(roundCars[i]); g.Controls.Add(roundEndLabels[i]);
            }
            qualBreakLabel = new Label { Left = 410, Top = 84, Width = 95, Text = "Break (s):" };
            g.Controls.Add(qualBreakLabel);
            qualBreakBox = new NumericUpDown { Left = 506, Top = 81, Width = 60, Minimum = 0, Maximum = 600, Value = 60 };
            tips.SetToolTip(qualBreakBox, "Seconds between knockout rounds, with every car back in its box.");
            g.Controls.Add(qualBreakBox);
            qualNeeds = new Label { Left = 10, Top = 240, Width = 556, ForeColor = SystemColors.GrayText };
            g.Controls.Add(qualNeeds);

            // Indy formats: picking one turns the NASCAR format off (and the other way round). The oval runs use the single
            // car release, DNQ and max-cars-out settings above, and the running order below.
            g = Group("Indy Qualifying Types", ref y, 82);
            qualIndyGroup = g;
            g.Controls.Add(new Label { Left = 10, Top = 22, Width = 60, Text = "Type:" });
            qualIndyBox = new ComboBox { Left = 75, Top = 18, Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
            qualIndyBox.Items.AddRange(new object[] { "Off", "Oval: 4-lap average (single car)", "Road / street: groups, Fast 12, Fast Six" });
            tips.SetToolTip(qualIndyBox, "Oval: each car makes one run of consecutive timed laps from a flying start (the out lap never counts),\n" +
                "ranked by the AVERAGE of those laps - every lap counts, a slow one included; a run that isn't finished gets\n" +
                "no time. In the qualifying session every lap is written as the average, so the grid follows it.\n" +
                "Uses the single car release, DNQ and max cars out settings above and the running order below.\n" +
                "Road / street: Group 1 and Group 2 on track in turn, the fastest of each go to the Fast 12, the fastest of\n" +
                "that to the Fast Six for pole. Grid from 13th: Group 1 on the odd spots, Group 2 on the even ones.\n" +
                "The Indy 500 format is coming.");
            g.Controls.Add(qualIndyBox);
            qualIndyLapsLabel = new Label { Left = 320, Top = 22, Width = 120, Text = "Laps per run:" };
            g.Controls.Add(qualIndyLapsLabel);
            qualIndyLapsBox = new NumericUpDown { Left = 506, Top = 19, Width = 60, Minimum = 1, Maximum = 10, Value = 4 };
            g.Controls.Add(qualIndyLapsBox);
            // road / street: a row per session, plus the group split and the break
            Func<int, int, string, Label> lab = (x, top, txt) => { var l = new Label { Left = x, Top = top + 3, AutoSize = true, Text = txt }; g.Controls.Add(l); qualIndyRoadCtls.Add(l); return l; };
            Func<int, int, int, int, int, NumericUpDown> num = (x, top, min, max, val) =>
            { var n = new NumericUpDown { Left = x, Top = top, Width = 50, Minimum = min, Maximum = max, Value = val }; g.Controls.Add(n); qualIndyRoadCtls.Add(n); return n; };
            lab(10, 52, "Groups:"); qualIndyGroupMin = num(110, 52, 1, 60, 10); lab(165, 52, "min each, top"); qualIndyGroupAdv = num(250, 52, 1, 20, 6); lab(305, 52, "of each group go through");
            lab(10, 80, "Fast 12:"); qualIndyF12Min = num(110, 80, 1, 60, 10); lab(165, 80, "min, top"); qualIndyF12Adv = num(250, 80, 1, 12, 6); lab(305, 80, "go to the Fast Six");
            lab(10, 108, "Fast Six:"); qualIndyF6Min = num(110, 108, 1, 60, 6); lab(165, 108, "min");
            lab(305, 108, "Break (s):"); qualIndyBreak = num(370, 108, 0, 600, 60);
            lab(10, 136, "Split the groups by:");
            qualIndySplitBox = new ComboBox { Left = 130, Top = 132, Width = 236, DropDownStyle = ComboBoxStyle.DropDownList };
            qualIndySplitBox.Items.AddRange(new object[] { "Driver rating (fastest alternate)", "Running order list (alternate)", "Random draw" });
            tips.SetToolTip(qualIndySplitBox, "Driver rating: the cars ranked by the AI's qualifying rating, then 1st to Group 1, 2nd to Group 2, and so on, so\n" +
                "the groups are evenly matched (as a split on practice speed would be). Running order list: alternating down the\n" +
                "list below. Random draw: a random split.");
            g.Controls.Add(qualIndySplitBox); qualIndyRoadCtls.Add(qualIndySplitBox);
            // oval: two-lane re-qualifying after everyone's first run, and wave-offs
            qualReQualBox = new CheckBox { Left = 10, Top = 50, Width = 152, Text = "Re-qualifying lanes for" };
            tips.SetToolTip(qualReQualBox, "Once every car has made its first run, the two lanes open for this many minutes (game time):\n" +
                "Lane 1 (priority): the car gives up its time and goes before Lane 2. Its result is then its best Lane 1 run,\n" +
                "which can be slower than the time it gave up (or no time at all).\n" +
                "Lane 2: the car keeps its time and a faster run replaces it, but it only goes when Lane 1 is empty and no\n" +
                "Lane 1 car is on track, so it may never get its chance.\n" +
                "The AI teams pick their lane from where they stand: no time or outside the field = Lane 1, on the bubble =\n" +
                "mostly Lane 2, well down on what their rating says = sometimes. They only go again when they still need to.\n" +
                "You pick yours with the lane buttons under Session while the lanes are open.");
            g.Controls.Add(qualReQualBox); qualOvalCtls.Add(qualReQualBox);
            qualReQualMin = new NumericUpDown { Left = 165, Top = 50, Width = 50, Minimum = 1, Maximum = 120, Value = 20 };
            g.Controls.Add(qualReQualMin); qualOvalCtls.Add(qualReQualMin);
            var rqLab = new Label { Left = 220, Top = 53, AutoSize = true, Text = "min, up to" };
            g.Controls.Add(rqLab); qualOvalCtls.Add(rqLab);
            qualReQualRuns = new NumericUpDown { Left = 290, Top = 50, Width = 45, Minimum = 2, Maximum = 10, Value = 3 };
            tips.SetToolTip(qualReQualRuns, "Runs a car may make in all, its first run included.");
            g.Controls.Add(qualReQualRuns); qualOvalCtls.Add(qualReQualRuns);
            var rqLab2 = new Label { Left = 340, Top = 53, AutoSize = true, Text = "runs a car" };
            g.Controls.Add(rqLab2); qualOvalCtls.Add(rqLab2);
            qualWaveOffBox = new CheckBox { Left = 430, Top = 50, Width = 136, Text = "AI wave-offs", Checked = true };
            tips.SetToolTip(qualWaveOffBox, "An AI team can wave off a run before its last timed lap (the run counts for nothing): a Lane 2 run\n" +
                "that can't beat its time, a first run well off the car's pace (it goes to Lane 1), a Lane 1 run heading\n" +
                "outside the field with time left to go again. Yours: come back onto pit road before your last timed lap.");
            g.Controls.Add(qualWaveOffBox); qualOvalCtls.Add(qualWaveOffBox);
            qualIndyHint = new Label { Left = 10, Top = 52, Width = 556, ForeColor = SystemColors.GrayText,
                Text = "Picking an Indy type turns the NASCAR format off. Coming: the Indy 500 format." };
            g.Controls.Add(qualIndyHint);

            // the field: true DNQs (any format)
            g = Group("Field size (DNQs)", ref y, 100);
            g.Controls.Add(new Label { Left = 10, Top = 22, Width = 150, Text = "Cars that make the field:" });
            qualStartersBox = new NumericUpDown { Left = 165, Top = 19, Width = 55, Minimum = 0, Maximum = 100, Value = 0 };
            tips.SetToolTip(qualStartersBox, "0 = every car makes the field. Otherwise only the top cars after qualifying do; the rest did not qualify.");
            g.Controls.Add(qualStartersBox);
            qualStartersUnit = new ComboBox { Left = 226, Top = 18, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            qualStartersUnit.Items.AddRange(new object[] { "cars", "% of the field" });
            qualStartersUnit.SelectedIndex = 0;
            g.Controls.Add(qualStartersUnit);
            qualScratchBox = new CheckBox { Left = 370, Top = 20, Width = 200, Text = "Take them out before the green", Checked = true };
            tips.SetToolTip(qualScratchBox, "At the start of the race the AI cars that didn't qualify get engine damage past NR2003's must-pit line\n" +
                "and are called in, so they go to their stalls and retire there (like an engine failure). One still out\n" +
                "when the pace car pulls off gets a tow. A car that is still running at the green starts from the back.\n" +
                "Your own car is never touched.");
            g.Controls.Add(qualScratchBox);
            g.Controls.Add(new Label { Left = 10, Top = 46, Width = 556, Height = 52, ForeColor = SystemColors.GrayText,
                Text = "The rest are DNQs: marked on the results and held in their boxes in a practice after qualifying (Happy Hour). With " +
                    "\"Take them out\" ticked they are sent in and retired during the pace laps; any that are still running at the green start from the back." });

            g = Group("Running order (single car)", ref y, 250);
            orderList = new ListBox { Left = 10, Top = 22, Width = 400, Height = 218, IntegralHeight = false, Font = new Font("Consolas", 9.5f), AllowDrop = true };
            tips.SetToolTip(orderList, "Drag a driver to the place they go out. Cars not in the list go after them in a random draw.");
            orderList.MouseDown += (s2, e2) =>
            {
                int i = orderList.IndexFromPoint(e2.Location);
                if (i >= 0 && e2.Button == MouseButtons.Left) orderList.DoDragDrop(i, DragDropEffects.Move);
            };
            orderList.DragOver += (s2, e2) => { e2.Effect = DragDropEffects.Move; };
            orderList.DragDrop += (s2, e2) =>
            {
                if (!e2.Data.GetDataPresent(typeof(int))) return;
                int from = (int)e2.Data.GetData(typeof(int));
                int to = orderList.IndexFromPoint(orderList.PointToClient(new Point(e2.X, e2.Y)));
                if (to < 0) to = orderOrder.Count - 1;
                if (from < 0 || from >= orderOrder.Count || to == from) return;
                string n = orderOrder[from]; orderOrder.RemoveAt(from); orderOrder.Insert(to, n);
                OrderChanged(); orderList.SelectedIndex = to;
            };
            g.Controls.Add(orderList);
            var getField = new Button { Text = "Get the field", Left = 420, Top = 22, Width = 146, Height = 28 };
            tips.SetToolTip(getField, "Fill the list with the AI cars in this session (NR2003 running, a session loaded). Keeps the order you set.");
            getField.Click += (s2, e2) => OrderGetField();
            g.Controls.Add(getField);
            var drawBtn = new Button { Text = "Random draw", Left = 420, Top = 56, Width = 146, Height = 28 };
            drawBtn.Click += (s2, e2) => { var r = new Random(); orderOrder = orderOrder.OrderBy(x => r.Next()).ToList(); OrderChanged(); };
            g.Controls.Add(drawBtn);
            var upBtn = new Button { Text = "Up", Left = 420, Top = 96, Width = 70, Height = 28 };
            upBtn.Click += (s2, e2) => OrderMove(-1);
            g.Controls.Add(upBtn);
            var downBtn = new Button { Text = "Down", Left = 496, Top = 96, Width = 70, Height = 28 };
            downBtn.Click += (s2, e2) => OrderMove(1);
            g.Controls.Add(downBtn);
            var clearBtn = new Button { Text = "Clear (random draw)", Left = 420, Top = 136, Width = 146, Height = 28 };
            tips.SetToolTip(clearBtn, "Empty list = a random draw for everyone when qualifying starts.");
            clearBtn.Click += (s2, e2) => { orderOrder.Clear(); OrderChanged(); };
            g.Controls.Add(clearBtn);
            g.Controls.Add(new Label { Left = 420, Top = 172, Width = 150, Height = 60, ForeColor = SystemColors.GrayText,
                Text = "Drag names into place. Cars missing from the list go after them in a random draw." });

            g = Group("Session", ref y, 122);
            qualStatus = new Label { Left = 10, Top = 20, Width = 400, Height = 64 };
            g.Controls.Add(qualStatus);
            // your lane while the re-qualifying lanes are open (Indy oval)
            qualLaneLabel = new Label { Left = 10, Top = 93, Width = 90, Text = "Your lane:" };
            g.Controls.Add(qualLaneLabel);
            laneBtn1 = new Button { Text = "Lane 1 (give up time)", Left = 100, Top = 88, Width = 150, Height = 26 };
            tips.SetToolTip(laneBtn1, "Priority lane: your time is given up NOW and you go before anyone in Lane 2. Your best Lane 1 run is your time.");
            laneBtn1.Click += (s, e) => { Show(Stamp + qual.ChooseLane(1)); QualUi(); };
            g.Controls.Add(laneBtn1);
            laneBtn2 = new Button { Text = "Lane 2 (keep time)", Left = 256, Top = 88, Width = 150, Height = 26 };
            tips.SetToolTip(laneBtn2, "You keep your time and a faster run replaces it; you only go when Lane 1 is empty and clear of the track.");
            laneBtn2.Click += (s, e) => { Show(Stamp + qual.ChooseLane(2)); QualUi(); };
            g.Controls.Add(laneBtn2);
            laneOutBtn = new Button { Text = "Done with the lanes", Left = 420, Top = 88, Width = 146, Height = 26 };
            tips.SetToolTip(laneOutBtn, "Out of the lanes (your time, if you have one, stands). The lanes close early once nobody is left in them.");
            laneOutBtn.Click += (s, e) => { Show(Stamp + qual.ChooseLane(0)); QualUi(); };
            g.Controls.Add(laneOutBtn);
            qualStart = new Button { Text = "Start qualifying", Left = 420, Top = 18, Width = 146, Height = 28 };
            tips.SetToolTip(qualStart, "Once every car is sitting in its box (the count stops growing). Stay out of the car and watch on the TV cameras.");
            qualStart.Click += (s, e) => { qual.Start(); QualUi(); };
            g.Controls.Add(qualStart);
            qualStop = new Button { Text = "Stop", Left = 420, Top = 52, Width = 146, Height = 28 };
            tips.SetToolTip(qualStop, "Stops qualifying for this session: the cars are let go and no grid is written.");
            qualStop.Click += (s, e) => { qual.Stop(); QualUi(); };
            g.Controls.Add(qualStop);
            g = Group("Timing box", ref y, 84);
            g.Controls.Add(new Label { Left = 10, Top = 56, Width = 40, Text = "Style:" });
            qualStyleBox = new ComboBox { Left = 52, Top = 52, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
            qualStyleBox.Items.AddRange(new object[] { "Classic timing box", "Broadcast (driver, telemetry, laps, order)" });
            tips.SetToolTip(qualStyleBox, "Classic: the small TV box (pole on top, the car on its run below with its gap).\n" +
                "Broadcast: the full panel from the Figma design - the driver, live telemetry (speed, gear, RPM, throttle, brake),\n" +
                "the lap block (lap n of N, current, last and best lap, each timed lap) and the running order, which slides in\n" +
                "between runs and every 15 s during one. Lime while the car is on pole pace, red when slower. Any format.");
            qualStyleBox.SelectedIndexChanged += (s, e) => { if (loading) return; settings["QualBoxStyle"] = qualStyleBox.SelectedIndex == 1 ? "broadcast" : "classic"; SaveSettings(); };
            g.Controls.Add(qualStyleBox);
            g.Controls.Add(new Label { Left = 262, Top = 56, Width = 84, Text = "Lap times as:" });
            qualLapUnitBox = new ComboBox { Left = 348, Top = 52, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            qualLapUnitBox.Items.AddRange(new object[] { "Time", "Speed (mph)" });
            tips.SetToolTip(qualLapUnitBox, "Lap times on the timing box, the broadcast overlay and the results list: as times (m:ss.fff from a\n" +
                "minute up), or as speeds in mph like NR2003's own option (the track length over the lap time). Gaps stay in seconds.");
            qualLapUnitBox.SelectedIndexChanged += (s, e) => { LapFmt.Speed = qualLapUnitBox.SelectedIndex == 1; if (loading) return; settings["QualLapUnit"] = LapFmt.Speed ? "speed" : "time"; SaveSettings(); };
            g.Controls.Add(qualLapUnitBox);
            Check(g, "ShowQualBox", "Show the timing box over the game", 10, 22, true,
                "A TV style box while qualifying runs: pole on top, below it the car on its run with its live gap to pole\n" +
                "(green = faster) on every timed lap and its lap time when it crosses the line. Single car runs: after the\n" +
                "last timed lap, its best lap, gap and place for 5 s, then the next car with -.-- until it takes the green.\n" +
                "Knockout rounds: the car on TV.\n" +
                "NR2003 must run windowed or borderless.");
            var qualPlaceBtn = new Button { Text = "Place the box", Left = 250, Top = 18, Width = 110, Height = 26 };
            tips.SetToolTip(qualPlaceBtn, "Drag it where you want it, the mouse wheel sizes it, Enter or double-click saves, Esc cancels.");
            qualPlaceBtn.Click += (s, e) => PlaceQualBox();
            g.Controls.Add(qualPlaceBtn);
            Check(g, "QualPace", "Qualifying pace for the AI", 380, 22, true,
                "In its own qualifying session NR2003 gives every AI car a small speed bonus from its qualifying rating\n" +
                "(papy_ai.ini driver_qualifying_to_*). Practice doesn't, so with this on the cars get that bonus while\n" +
                "they are out on a qualifying run - their laps match what they'd do in qualifying.");
            g = Group("Telemetry", ref y, 80);
            Check(g, "RealWheel", "In-car steering wheel and arms follow the real steering", 10, 48, true,
                "NR2003 turns a car's in-car steering wheel (and the driver's arms) from how fast the car is turning, times\n" +
                "your setup's steering ratio - cut to 7 when you aren't driving - so the AI wheels show the wrong amount and\n" +
                "it changes with speed. With this on they turn by the car's actual front wheel steer times your setup's\n" +
                "steering ratio, the same angle the telemetry strip shows. Your own car is left as the game draws it.");
            Check(g, "ShowTeleStrip", "Show the telemetry strip over the game", 10, 22, false,
                "A small TV style strip for the car on the timing box: its throttle (green) and brake (red) as running lines\n" +
                "over the last few seconds, and a steering wheel that turns with the car (round wheel for NASCAR formats,\n" +
                "formula wheel for Indy). The AI have no brake value of their own: the brake is worked out from how hard the car\n" +
                "slows with the throttle off. In custom qualifying it follows the car on its run; in races and every other\n" +
                "session it follows the car on TV. NR2003 must run windowed or borderless.");
            var stripPlaceBtn = new Button { Text = "Place the strip", Left = 280, Top = 18, Width = 120, Height = 26 };
            tips.SetToolTip(stripPlaceBtn, "Drag it where you want it, the mouse wheel sizes it, Enter or double-click saves, Esc cancels.");
            stripPlaceBtn.Click += (s, e) => PlaceStrip();
            g.Controls.Add(stripPlaceBtn);
            var teleWinBtn = new Button { Text = "Telemetry window...", Left = 420, Top = 18, Width = 146, Height = 26 };
            tips.SetToolTip(teleWinBtn, "SMT style traces: tick drivers to follow their lap live, tick any of their timed laps (or their best) to\n" +
                "lay them over each other - speed, throttle, brake, steering and the time gained or lost round the lap.\n" +
                "Laps are recorded while qualifying runs, for every car in it.");
            teleWinBtn.Click += (s, e) => OpenTelemetry();
            g.Controls.Add(teleWinBtn);
            g = Group("Qualifying fuel and pace", ref y, 100);
            g.Controls.Add(new Label { Left = 10, Top = 22, Width = 330, Text = "Fuel load for a qualifying run (% of the tank, 0 = leave it):" });
            qualFuelBox = new NumericUpDown { Left = 345, Top = 19, Width = 55, Minimum = 0, Maximum = 100, Value = 25 };
            tips.SetToolTip(qualFuelBox, "In practice the AI carry a race fuel load; in qualifying they run light. Each car gets this much fuel as it rolls off. Race Control logs its fuel before and after.");
            qualFuelBox.ValueChanged += (s2, e2) => { if (loading) return; settings["QualFuel"] = qualFuelBox.Value.ToString(); SaveSettings(); qual.FuelPercent = (double)qualFuelBox.Value; };
            g.Controls.Add(qualFuelBox);
            Check(g, "QualMatchPace", "Match the stock qualifying pace (times scaled, order unchanged)", 10, 46, true,
                "In the qualifying session the practice laps are scaled by the median of (the game's own qualifying lap /\n" +
                "that car's practice lap), so the times look like stock qualifying times. The order stays as qualified.");
            g.Controls.Add(new Label { Left = 10, Top = 76, Width = 330, Text = "Qualifying boost strength (% of the game's bonus, 100 = stock formula):" });
            qualBoostBox = new NumericUpDown { Left = 345, Top = 73, Width = 55, Minimum = 0, Maximum = 1000, Value = 100 };
            tips.SetToolTip(qualBoostBox, "Scales how much of the game's qualifying speed bonus the AI cars get while out on a run: 1 + (rating - 1) x coefficient x this %. Raise it if the laps are still slower than stock qualifying; the log line 'rating ..., multipliers ...' shows what each car got.");
            qualBoostBox.ValueChanged += (s2, e2) => { if (loading) return; settings["QualBoost"] = qualBoostBox.Value.ToString(); SaveSettings(); qual.BoostPercent = (double)qualBoostBox.Value; };
            g.Controls.Add(qualBoostBox);
            g = Group("Your car", ref y, 98);
            Check(g, "QualPlayer", "Include my car in qualifying (I drive my own turn)", 10, 22, true,
                "Your car gets a turn in the single car order (or runs in the knockout / Indy rounds) like an AI car.\n" +
                "Race Control never touches your car: it tells you on screen when to go, your timed laps and when to come in.\n" +
                "Your practice time becomes your qualifying time: in the qualifying session drive one lap (any speed)\n" +
                "and Race Control writes your practice time over it, the same way it does for the AI.");
            Check(g, "ShowPlayerCalls", "Show the calls to me over the game", 10, 46, true,
                "A banner across the top of the game: YOUR RUN: GO NOW, TIMED LAP 1 OF 2, RUN COMPLETE - COME IN,\n" +
                "CHECKERED, THROUGH TO ROUND 2, DNQ... On the right a countdown: when Race Control expects you at the\n" +
                "green (single car runs), the time left to pull out of your box before a DNQ, the time left in a round.\n" +
                "In a race the same banner tells you about a penalty on your car, your FREE PASS or WAVE AROUND and what to do\n" +
                "next, and FOLLOW #N: the car ahead of you in the caution line.\n" +
                "NR2003 must run windowed or borderless.");
            Check(g, "QualReadyPause", "Pause the game at my turn so I can set my camera", 10, 70, true,
                "When your turn is called the game pauses and a card says so. Set your camera (RaceVision puts the TV\n" +
                "in your car meanwhile; the game's keys still work), then resume with your pause key or the card's\n" +
                "RESUME button. The time to pull out of your box (DNQ setting) only starts then. Time the game is\n" +
                "paused never counts towards a DNQ, for any car.");
            var bannerPlaceBtn = new Button { Text = "Place the banner", Left = 300, Top = 42, Width = 120, Height = 26 };
            tips.SetToolTip(bannerPlaceBtn, "Drag it where you want it, the mouse wheel sizes it, Enter or double-click saves, Esc cancels.");
            bannerPlaceBtn.Click += (s, e) => PlaceBanner();
            g.Controls.Add(bannerPlaceBtn);
            // a fixed height under the last group: the tab scrolls down to it in any window size (anchored to the bottom
            // it was squeezed or pushed out of reach, and the tab's scrolling never covered it)
            qualBoard = new ListBox { Left = 6, Top = y, Width = 576, Height = 340, IntegralHeight = false, Font = new Font("Consolas", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            page.Controls.Add(qualBoard);
            ((ScrollableControl)page).AutoScrollMargin = new Size(0, 8);
            qual.Logger = Show;
            EventHandler qualChanged = (s, e) => { if (loading) return; SaveQualSettings(); };
            // one format at a time: an Indy type turns the NASCAR format off and the other way round
            qualFormatBox.SelectedIndexChanged += (s, e) => { if (!loading && qualFormatBox.SelectedIndex > 0 && qualIndyBox.SelectedIndex > 0) qualIndyBox.SelectedIndex = 0; qualChanged(s, e); };
            qualIndyBox.SelectedIndexChanged += (s, e) => { if (!loading && qualIndyBox.SelectedIndex > 0 && qualFormatBox.SelectedIndex > 0) qualFormatBox.SelectedIndex = 0; qualChanged(s, e); };
            qualIndyLapsBox.ValueChanged += qualChanged;
            qualReQualBox.CheckedChanged += qualChanged; qualWaveOffBox.CheckedChanged += qualChanged;
            qualReQualMin.ValueChanged += qualChanged; qualReQualRuns.ValueChanged += qualChanged;
            qualStartersBox.ValueChanged += qualChanged; qualStartersUnit.SelectedIndexChanged += qualChanged; qualScratchBox.CheckedChanged += qualChanged;
            foreach (var n in new[] { qualIndyGroupMin, qualIndyGroupAdv, qualIndyF12Min, qualIndyF12Adv, qualIndyF6Min, qualIndyBreak }) n.ValueChanged += qualChanged;
            qualIndySplitBox.SelectedIndexChanged += qualChanged;
            qualLapsBox.ValueChanged += qualChanged;
            qualDnqBox.ValueChanged += qualChanged;
            qualReleaseBox.SelectedIndexChanged += qualChanged;
            qualReleaseVal.ValueChanged += qualChanged;
            qualMaxOutBox.ValueChanged += qualChanged;
            qualOrderBox.TextChanged += qualChanged;
            qualRoundsBox.TextChanged += qualChanged;
            qualBreakBox.ValueChanged += qualChanged;

            y = Tab("Log");
            g = Group("Live page", ref y, 48);
            Check(g, "LivePage", "Live page", 10, 20, true,
                "A read-only web page with the race as RaceControl sees it: phase, pits, first time by, the line, the\n" +
                "cone, the running order and the log, refreshed every second. /board is the choose cone board for OBS.");
            Check(g, "LiveNetwork", "Other computers too", 95, 20, false,
                "Lets other computers on your network open the page (adds a Windows firewall rule for private networks\n" +
                "only). Off: only this computer.");
            liveLink = new LinkLabel { Left = 245, Top = 21, Width = 320, Height = 18, AutoEllipsis = true };
            liveLink.LinkClicked += (s, e) => { try { Process.Start((string)e.Link.LinkData); } catch { } };
            tips.SetToolTip(liveLink, "Click to open. Add ?view=order (or status, ftb, line, cone, log) and &bare=1 for an OBS browser source;\n" +
                "/board is the choose cone board on a transparent background.");
            g.Controls.Add(liveLink);
            logBox = new ListBox { Left = 4, Top = y, Width = 576, Height = 300, HorizontalScrollbar = true, IntegralHeight = false, Font = new Font("Consolas", 8.5f) };
            page.Controls.Add(logBox);
            // fill the rest of the tab whatever its size (the tab's size isn't known yet while the window is built)
            var logPage = page; int logTop = y;
            logPage.Layout += (s2, e2) => logBox.SetBounds(4, logTop, Math.Max(100, logPage.ClientSize.Width - 8), Math.Max(80, logPage.ClientSize.Height - logTop - 4));

            LoadSettings();
            laneBox.SelectedIndexChanged += (s, e) => SaveTrackSettings();
            giveUpBox.ValueChanged += (s, e) => SaveTrackSettings();
            frontRowsBox.ValueChanged += (s, e) => SaveTrackSettings();
            coneBox.ValueChanged += (s, e) => SaveTrackSettings();
            LoadTrackSettings();
            ShowPreset();
            StartLive();

            // light / dark look (top right): follow Windows, or force one
            Controls.Add(new Label { Text = "Theme:", AutoSize = true, Top = 17, Left = ClientSize.Width - 178, Anchor = AnchorStyles.Top | AnchorStyles.Right });
            themeBox = new ComboBox { Left = ClientSize.Width - 130, Top = 13, Width = 118, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            themeBox.Items.AddRange(new object[] { "Follow Windows", "Light", "Dark" });
            string th;
            themeBox.SelectedIndex = !settings.TryGetValue("Theme", out th) ? 0 : th == "dark" ? 2 : th == "light" ? 1 : 0;
            themeBox.SelectedIndexChanged += (s, e) =>
            {
                settings["Theme"] = themeBox.SelectedIndex == 2 ? "dark" : themeBox.SelectedIndex == 1 ? "light" : "system";
                SaveSettings();
                ApplyTheme();
            };
            Controls.Add(themeBox);
            ApplyTheme();

            tick = new Timer { Interval = 10 };
            tick.Tick += (s, e) =>
            {
                if (engine == null) return;
                // AI telemetry: samples itself at most ~30 times a game second (Telemetry.cs)
                if (qual.LiveOn && mem != null)
                    try { tele.Sample(mem, qual.Timing, qual.Field.ToList()); }
                    catch (GameMemoryException) { }
                else if (mem != null && StripWanted)
                    try { int tv = QualTiming.Focus(mem); if (QCar(tv)) tvTele.Sample(mem, noTiming, new[] { tv }); }
                    catch (GameMemoryException) { }
                try { TeleStripTick(); }                                   // ~25 frames a second
                catch (GameMemoryException) { }
                if ((DateTime.Now - lastQualTick).TotalMilliseconds >= 100)
                {
                    lastQualTick = DateTime.Now;
                    HubFeed.Current.Tick();
                    try { qual.Tick(mem); QualBoxTick(); }
                    catch (GameMemoryException) { }
                    try { if (qual.TakeReadyRequest()) ShowQualReady(); QualReadyTick(); }
                    catch (GameMemoryException) { }
                    QualUi();
                    try { MapTick(); }
                    catch (GameMemoryException) { }
                }
                engine.Tick();
                // the engine chose for the player (no answer while the game kept running): take the chart down
                if (chooser != null && !chooser.IsDisposed && !engine.PlayerOfferOpen && !pausedForChooser) { chooser.CloseQuietly(); chooser = null; }
                BoardTick();
                LiveTick();
            };
            slowTimer = new Timer { Interval = 1000 };
            slowTimer.Tick += (s, e) => SlowTick();
            slowTimer.Start();

            FormClosing += (s, e) => { if (engine != null) engine.Shutdown(); try { qual.Shutdown(); } catch (Exception) { } if (live != null) live.Stop(); };
            Show("RaceControl build " + Version + " started, log: " + logPath);
            SlowTick();
        }

        // A new tab; the groups that follow go on it. Returns where the first one goes.
        int Tab(string caption)
        {
            var p = new TabPage(caption) { AutoScroll = true, UseVisualStyleBackColor = true };
            tabs.TabPages.Add(p);
            page = p;
            return 6;
        }

        GroupBox Group(string caption, ref int y, int height)
        {
            bool top = page == this;
            var g = new GroupBox { Text = caption, Left = top ? 12 : 4, Top = y, Width = top ? 596 : 576, Height = height, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            page.Controls.Add(g);
            y += height + 6;
            return g;
        }

        void Check(GroupBox g, string key, string caption, int left, int top, bool def, string tip)
        {
            var cb = new CheckBox { Text = caption, Left = left, Top = top, AutoSize = true, Checked = def };
            tips.SetToolTip(cb, tip);
            cb.CheckedChanged += (s, e) =>
            {
                ApplyOptions(); SaveSettings();
                if (NotInPresets.Contains(key)) StartLive(); else ShowPreset();
            };
            g.Controls.Add(cb);
            checks[key] = cb;
        }

        // ---- settings: RaceControl.ini, key=value; lane settings per track as Lane.<track>=<lane>,<rows>,<front> ----

        void LoadSettings()
        {
            try
            {
                if (File.Exists(iniPath))
                    foreach (string line in File.ReadAllLines(iniPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) settings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            loading = true;
            foreach (var kv in checks)
            {
                string v;
                if (settings.TryGetValue(kv.Key, out v)) kv.Value.Checked = v == "1";
            }
            string sv;
            int sc;
            stageCountBox.SelectedIndex = settings.TryGetValue("StageCount", out sv) && int.TryParse(sv, out sc) && sc >= 2 && sc <= 4 ? sc - 2 : 1;
            stageModeBox.SelectedIndex = !settings.TryGetValue("StageMode", out sv) ? 0 : sv == "laps" ? 2 : sv == "percent" ? 1 : 0;
            stageEndsBox.Text = settings.TryGetValue("StageEndValues", out sv) ? sv : "25, 61";
            StageBoxesFromText();
            CompanionAppStages.Folder = settings.TryGetValue("StageCompanionFolder", out sv) ? sv : "";
            string bl;
            boardLayout = BoardLayout.Parse(settings.TryGetValue("Board", out bl) ? bl : null);
            qualBoxLayout = BoardLayout.Parse(settings.TryGetValue("QualBox", out bl) ? bl : null);
            stripLayout = BoardLayout.Parse(settings.TryGetValue("TeleStrip", out bl) ? bl : null);
            bannerLayout = BoardLayout.Parse(settings.TryGetValue("PlayerBanner", out bl) ? bl : null);
            ShowBoardLayout();
            qualFormatBox.SelectedIndex = !settings.TryGetValue("QualFormat", out sv) ? 0 : sv == "knockout" ? 2 : sv == "single" ? 1 : 0;
            qualIndyBox.SelectedIndex = sv == "indy_oval" ? 1 : sv == "indy_road" ? 2 : 0;
            {   // "group min, through, Fast 12 min, through, Fast Six min, split (rating / list / random), break s"
                string[] ir = (settings.TryGetValue("QualIndyRoad", out sv) ? sv : "").Split(',');
                Func<int, decimal, decimal> at = (i, def) => { decimal v; return i < ir.Length && decimal.TryParse(ir[i], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def; };
                Action<NumericUpDown, decimal> set = (nb, v) => nb.Value = Math.Max(nb.Minimum, Math.Min(nb.Maximum, v));
                set(qualIndyGroupMin, at(0, 10)); set(qualIndyGroupAdv, at(1, 6)); set(qualIndyF12Min, at(2, 10)); set(qualIndyF12Adv, at(3, 6)); set(qualIndyF6Min, at(4, 6));
                qualIndySplitBox.SelectedIndex = ir.Length > 5 && ir[5] == "list" ? 1 : ir.Length > 5 && ir[5] == "random" ? 2 : 0;
                set(qualIndyBreak, at(6, 60));
            }
            startPaceBox.Value = settings.TryGetValue("StartPaceLaps", out sv) && int.TryParse(sv, out sc) ? Math.Max(1, Math.Min(10, sc)) : 1;
            qualIndyLapsBox.Value = settings.TryGetValue("QualIndyLaps", out sv) && int.TryParse(sv, out sc) ? Math.Max(1, Math.Min(10, sc)) : 4;
            {   // "on (1/0), minutes, runs per car, AI wave-offs (1/0)"
                string[] rq = (settings.TryGetValue("QualReQual", out sv) ? sv : "").Split(',');
                int a;
                qualReQualBox.Checked = rq.Length > 0 && rq[0] == "1";
                qualReQualMin.Value = rq.Length > 1 && int.TryParse(rq[1], out a) ? Math.Max(1, Math.Min(120, a)) : 20;
                qualReQualRuns.Value = rq.Length > 2 && int.TryParse(rq[2], out a) ? Math.Max(2, Math.Min(10, a)) : 3;
                qualWaveOffBox.Checked = !(rq.Length > 3 && rq[3] == "0");
                // "N" cars or "N%" of the field; 0 = everyone
                string st = settings.TryGetValue("QualStarters", out sv) ? sv.Trim() : "0";
                bool pct = st.EndsWith("%");
                qualStartersBox.Value = int.TryParse(st.TrimEnd('%'), out a) ? Math.Max(0, Math.Min(100, a)) : 0;
                qualStartersUnit.SelectedIndex = pct ? 1 : 0;
                qualScratchBox.Checked = !settings.TryGetValue("QualDnqScratch", out sv) || sv.Trim() != "0";
            }
            qualStyleBox.SelectedIndex = settings.TryGetValue("QualBoxStyle", out sv) && sv == "broadcast" ? 1 : 0;
            qualLapUnitBox.SelectedIndex = settings.TryGetValue("QualLapUnit", out sv) && sv == "speed" ? 1 : 0;
            LapFmt.Speed = qualLapUnitBox.SelectedIndex == 1;
            qualLapsBox.Value = settings.TryGetValue("QualLaps", out sv) && int.TryParse(sv, out sc) ? Math.Max(1, Math.Min(5, sc)) : 1;
            qualOrderBox.Text = settings.TryGetValue("QualOrder", out sv) ? sv : "";
            qualRoundsBox.Text = settings.TryGetValue("QualRounds", out sv) && sv != "" ? sv : "25:24, 10:12, 5:12";
            RoundRowsFromText();
            qualFuelBox.Value = settings.TryGetValue("QualFuel", out sv) && int.TryParse(sv, out sc) ? Math.Max(0, Math.Min(100, sc)) : 25;
            qualBoostBox.Value = settings.TryGetValue("QualBoost", out sv) && int.TryParse(sv, out sc) ? Math.Max(0, Math.Min(1000, sc)) : 100;
            orderOrder = qualOrderBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('#')).ToList();
            ShowOrderList();
            qualBreakBox.Value = settings.TryGetValue("QualBreak", out sv) && int.TryParse(sv, out sc) ? Math.Max(0, Math.Min(600, sc)) : 60;
            qualDnqBox.Value = settings.TryGetValue("QualDnq", out sv) && int.TryParse(sv, out sc) ? Math.Max(15, Math.Min(600, sc)) : 90;
            settings.TryGetValue("QualRelease", out sv);
            qualReleaseBox.SelectedIndex = sv == "seconds" ? 1 : sv == "percent" || sv == "schedule" ? 2 : sv == "gap" || sv == "pitexit" ? 3 : 0;   // "schedule" (removed) -> the % mode
            qualMaxOutBox.Value = settings.TryGetValue("QualMaxOut", out sv) && int.TryParse(sv, out sc) ? Math.Max(1, Math.Min(20, sc)) : 3;
            qualReleaseVal.Value = settings.TryGetValue("QualReleaseValue", out sv) && int.TryParse(sv, out sc) ? Math.Max(0, Math.Min(600, sc)) : 30;
            loading = false;
            ApplyQualSettings();
        }

        void SaveSettings()
        {
            if (loading) return;
            foreach (var kv in checks) settings[kv.Key] = kv.Value.Checked ? "1" : "0";
            try { File.WriteAllLines(iniPath, settings.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray()); } catch { }
        }

        string TrackName { get { return string.IsNullOrEmpty(track) ? "default" : track; } }
        string TrackKey { get { return "Lane." + TrackName; } }

        void LoadTrackSettings()
        {
            string v;
            if (!settings.TryGetValue(TrackKey, out v) && !settings.TryGetValue("Lane.default", out v)) v = "1,2,3,0";
            string[] p = v.Split(',');
            int lane, rows, front, cone;
            loading = true;
            laneBox.SelectedIndex = p.Length > 0 && int.TryParse(p[0], out lane) && lane >= 0 && lane <= 2 ? lane : 1;
            giveUpBox.Value = p.Length > 1 && int.TryParse(p[1], out rows) ? Math.Max(0, Math.Min(10, rows)) : 2;
            frontRowsBox.Value = p.Length > 2 && int.TryParse(p[2], out front) ? Math.Max(0, Math.Min(10, front)) : 3;
            coneBox.Value = p.Length > 3 && int.TryParse(p[3], out cone) ? Math.Max(0, Math.Min(10000, cone)) : 0;
            loading = false;
            laneLabel.Text = "Preferred lane at " + (string.IsNullOrEmpty(track) ? "this track" : track) + ":";
            ApplyOptions();
        }

        void SaveQualSettings()
        {
            settings["QualFormat"] = qualIndyBox.SelectedIndex == 2 ? "indy_road" : qualIndyBox.SelectedIndex == 1 ? "indy_oval" : qualFormatBox.SelectedIndex == 2 ? "knockout" : qualFormatBox.SelectedIndex == 1 ? "single" : "off";
            settings["QualIndyLaps"] = qualIndyLapsBox.Value.ToString();
            settings["QualReQual"] = (qualReQualBox.Checked ? "1" : "0") + "," + qualReQualMin.Value + "," + qualReQualRuns.Value + "," + (qualWaveOffBox.Checked ? "1" : "0");
            settings["QualStarters"] = qualStartersBox.Value + (qualStartersUnit.SelectedIndex == 1 ? "%" : "");
            settings["QualDnqScratch"] = qualScratchBox.Checked ? "1" : "0";
            settings["QualIndyRoad"] = string.Join(",", new[] { qualIndyGroupMin.Value.ToString(), qualIndyGroupAdv.Value.ToString(), qualIndyF12Min.Value.ToString(),
                qualIndyF12Adv.Value.ToString(), qualIndyF6Min.Value.ToString(), qualIndySplitBox.SelectedIndex == 1 ? "list" : qualIndySplitBox.SelectedIndex == 2 ? "random" : "rating",
                qualIndyBreak.Value.ToString() });
            settings["QualLaps"] = qualLapsBox.Value.ToString();
            settings["QualOrder"] = qualOrderBox.Text.Trim();
            settings["QualRounds"] = qualRoundsBox.Text.Trim();
            settings["QualBreak"] = qualBreakBox.Value.ToString();
            settings["QualDnq"] = qualDnqBox.Value.ToString();
            settings["QualRelease"] = qualReleaseBox.SelectedIndex == 1 ? "seconds" : qualReleaseBox.SelectedIndex == 2 ? "percent" : qualReleaseBox.SelectedIndex == 3 ? "gap" : "pitroad";
            settings["QualMaxOut"] = qualMaxOutBox.Value.ToString();
            settings["QualReleaseValue"] = qualReleaseVal.Value.ToString();
            SaveSettings();
            ApplyQualSettings();
        }

        // Each format shows only its own fields: the knockout rounds only with Knockout rounds, the road / street
        // fields only with that Indy type, laps per run only with the Indy oval. The groups below move up or down to fit.
        void LayoutQualTab()
        {
            if (qualNascarGroup == null) return;
            bool ko = qualFormatBox.SelectedIndex == 2, road = qualIndyBox.SelectedIndex == 2, oval = qualIndyBox.SelectedIndex == 1;
            foreach (Control c in new Control[] { qualKoLabel, qualBreakLabel, qualBreakBox }) c.Visible = ko;
            for (int i = 0; i < 4; i++) foreach (Control c in new Control[] { roundLabels[i], roundMins[i], roundMidLabels[i], roundCars[i], roundEndLabels[i] }) c.Visible = ko;
            qualMaxOutLabel.Top = ko ? 213 : 87; qualMaxOutBox.Top = ko ? 209 : 83;
            qualNeeds.Top = ko ? 240 : 114;
            int nascarH = ko ? 268 : 142;
            foreach (Control c in qualIndyRoadCtls) c.Visible = road;
            qualIndyLapsLabel.Visible = qualIndyLapsBox.Visible = oval;
            foreach (Control c in qualOvalCtls) c.Visible = oval;
            qualIndyHint.Top = road ? 164 : oval ? 80 : 52;
            int indyH = road ? 192 : oval ? 108 : 82;
            if (qualNascarGroup.Height == nascarH && qualIndyGroup.Height == indyH) return;
            // restack every group (and the list) under the NASCAR group in the order they sit
            var stack = qualPage.Controls.Cast<Control>().Where(c => c.Top >= qualNascarGroup.Top).OrderBy(c => c.Top).ToList();
            qualNascarGroup.Height = nascarH; qualIndyGroup.Height = indyH;
            int y = qualNascarGroup.Top;
            foreach (Control c in stack) { c.Top = y; y += c.Height + 6; }
        }

        void ApplyQualSettings()
        {
            qual.Mode = qualIndyBox.SelectedIndex == 2 ? Qualifying.Format.IndyRoad : qualIndyBox.SelectedIndex == 1 ? Qualifying.Format.IndyOval : qualFormatBox.SelectedIndex == 2 ? Qualifying.Format.Knockout : qualFormatBox.SelectedIndex == 1 ? Qualifying.Format.Single : Qualifying.Format.Off;
            qual.Laps = qual.Mode == Qualifying.Format.IndyOval ? (int)qualIndyLapsBox.Value : (int)qualLapsBox.Value;
            qual.Order = qualOrderBox.Text;
            qual.Rounds = qualRoundsBox.Text;
            qual.BreakSeconds = qual.Mode == Qualifying.Format.IndyRoad ? (double)qualIndyBreak.Value : (double)qualBreakBox.Value;
            qual.IndyGroupMin = (double)qualIndyGroupMin.Value; qual.IndyGroupAdv = (int)qualIndyGroupAdv.Value;
            qual.IndyF12Min = (double)qualIndyF12Min.Value; qual.IndyF12Adv = (int)qualIndyF12Adv.Value; qual.IndyF6Min = (double)qualIndyF6Min.Value;
            qual.IndySplit = qualIndySplitBox.SelectedIndex == 1 ? Qualifying.Split.List : qualIndySplitBox.SelectedIndex == 2 ? Qualifying.Split.Random : Qualifying.Split.Rating;
            qual.DnqSeconds = (double)qualDnqBox.Value;
            qual.ReQual = qualReQualBox.Checked; qual.ReQualMinutes = (double)qualReQualMin.Value; qual.ReQualMaxRuns = (int)qualReQualRuns.Value;
            qual.WaveOffs = qualWaveOffBox.Checked;
            qual.Starters = (int)qualStartersBox.Value; qual.StartersPercent = qualStartersUnit.SelectedIndex == 1; qual.ScratchDnqs = qualScratchBox.Checked;
            qualReQualMin.Enabled = qualReQualRuns.Enabled = qualWaveOffBox.Enabled = qualReQualBox.Checked;
            qualStartersUnit.Enabled = qualScratchBox.Enabled = qualStartersBox.Value > 0;
            qual.FuelPercent = (double)qualFuelBox.Value;
            qual.BoostPercent = (double)qualBoostBox.Value;
            qual.ReleaseMode = qualReleaseBox.SelectedIndex == 1 ? Qualifying.Release.Seconds : qualReleaseBox.SelectedIndex == 2 ? Qualifying.Release.LapPercent : qualReleaseBox.SelectedIndex == 3 ? Qualifying.Release.Gap : Qualifying.Release.PitRoad;
            qual.MaxOut = (int)qualMaxOutBox.Value;
            qual.ReleaseValue = (double)qualReleaseVal.Value;
            bool single = qual.SingleRuns, ko = qual.Mode == Qualifying.Format.Knockout;
            qualLapsBox.Enabled = qualOrderBox.Enabled = qualDnqBox.Enabled = qualReleaseBox.Enabled = single;
            qualLapsBox.Enabled = qual.Mode == Qualifying.Format.Single;      // the Indy oval has its own laps per run
            qualIndyLapsBox.Enabled = qual.Mode == Qualifying.Format.IndyOval;
            qualMaxOutBox.Enabled = single;
            qualReleaseVal.Enabled = single && qualReleaseBox.SelectedIndex >= 1;
            qualRoundsBox.Enabled = qualBreakBox.Enabled = ko;
            var rounds = Qualifying.ParseRounds(qualRoundsBox.Text);
            qualNeeds.Text = single ? "Practice needs about 1 to 1.5 minutes per car, plus time for the cars to reach their boxes."
                : ko ? (rounds.Count == 0 ? "Rounds: minutes : cars through, for example 25:24, 10:12, 5:12"
                    : string.Format("Practice needs about {0:0} minutes.", rounds.Sum(r => r.Item1) + (double)qualBreakBox.Value / 60 * (rounds.Count - 1) + 3))
                : qual.Mode == Qualifying.Format.IndyRoad ? string.Format("Practice needs about {0:0} minutes.", (double)(2 * qualIndyGroupMin.Value + qualIndyF12Min.Value + qualIndyF6Min.Value) + (double)qualIndyBreak.Value / 60 * 3 + 3)
                : "Off: practice and qualifying are left to the game.";
            LayoutQualTab();
            QualUi();
        }

        void QualUi()
        {
            if (qualStatus == null) return;
            qualStatus.Text = qual.Status;
            qualStart.Enabled = qual.CanStart;
            qualStop.Enabled = qual.Active;
            if (laneBtn1 != null)
            {
                bool lanes = qual.LanesOpen;
                int pl = qual.PlayerLane;
                laneBtn1.Enabled = lanes && qual.PlayerCanPickLane && pl != 1;
                laneBtn2.Enabled = lanes && qual.PlayerCanPickLane && pl != 2;
                laneOutBtn.Enabled = lanes && (qual.PlayerCanPickLane || pl > 0);
                qualLaneLabel.Text = !lanes ? "Your lane:" : pl == 1 ? "Lane 1" : pl == 2 ? "Lane 2" : "No lane";
            }
            var lines = qual.Board;
            bool same = qualBoard.Items.Count == lines.Count;
            for (int i = 0; same && i < lines.Count; i++) same = (string)qualBoard.Items[i] == lines[i];
            if (same) return;
            qualBoard.BeginUpdate();
            qualBoard.Items.Clear();
            foreach (string l in lines) qualBoard.Items.Add(l);
            qualBoard.EndUpdate();
        }

        void SaveStageSettings()
        {
            settings["StageCount"] = (stageCountBox.SelectedIndex + 2).ToString();
            settings["StageMode"] = stageModeBox.SelectedIndex == 2 ? "laps" : stageModeBox.SelectedIndex == 1 ? "percent" : "track";
            settings["StageEndValues"] = stageEndsBox.Text.Trim();
            ApplyOptions();
            SaveSettings();
            ShowStages();
        }

        // The stage end laps for a race length, from Race Control's settings or the overlay app.
        int[] StageEndsFor(int raceLength)
        {
            if (checks["StageFromCompanionApp"].Checked) return CompanionAppStages.Ends(raceLength);
            if (stageModeBox.SelectedIndex <= 0)
            {
                double metres = engine != null ? engine.TrackLengthMetres : double.NaN;
                return double.IsNaN(metres) ? null : StageSettings.ByTrack(metres, engine.TrackName, raceLength);
            }
            return StageSettings.Ends(stageCountBox.SelectedIndex + 2, stageModeBox.SelectedIndex == 1, stageEndsBox.Text, raceLength);
        }

        void ShowStages()
        {
            if (!checks["StageRacing"].Checked) { stageInfo.Text = "Stage racing off"; return; }
            int len = engine != null ? engine.RaceLengthLaps : -1;
            bool steel = checks["StageFromCompanionApp"].Checked;
            bool byTrack = stageModeBox.SelectedIndex <= 0 && !steel;
            stageCountBox.Enabled = stageEndsBox.Enabled = !byTrack && !steel;
            if (len <= 0 && stageModeBox.SelectedIndex != 2 && !steel)
            {
                stageInfo.Text = byTrack ? "Stage ends: set by the track's length once a race is running" : "Stage ends: worked out from the race length once a race is running";
                return;
            }
            string problem = steel ? CompanionAppStages.Status(len) : null;
            if (problem != null) { stageInfo.Text = "Stage racing companion app: " + problem; return; }
            int[] ends = StageEndsFor(len);
            stageInfo.Text = ends == null || ends.Length == 0
                ? (steel ? "Stage racing companion app: no stage laps for this race yet" : "No stage ends set")
                : "Stage ends: lap " + string.Join(", ", ends.Select(e => e.ToString()).ToArray()) + (len > 0 ? " of " + len : "") + (steel ? " (from the companion app)" : byTrack ? " (" + TrackStageWhy() + ")" : "");
        }

        string TrackStageWhy()
        {
            string why;
            StageSettings.ByTrack(engine.TrackLengthMetres, engine.TrackName, out why);
            return why;
        }

        void SaveTrackSettings()
        {
            if (loading) return;
            settings[TrackKey] = laneBox.SelectedIndex + "," + giveUpBox.Value + "," + frontRowsBox.Value + "," + coneBox.Value;
            ApplyOptions();
            SaveSettings();
        }

        void ApplyOptions()
        {
            if (checks.ContainsKey("QualPace")) qual.QualPace = checks["QualPace"].Checked;
            if (checks.ContainsKey("QualMatchPace")) qual.MatchStockPace = checks["QualMatchPace"].Checked;
            if (checks.ContainsKey("QualPlayer")) qual.IncludePlayer = checks["QualPlayer"].Checked;
            if (checks.ContainsKey("QualReadyPause")) qual.PlayerReadyPause = checks["QualReadyPause"].Checked;
            if (mem != null && checks.ContainsKey("RealWheel"))
            {
                bool want = checks["RealWheel"].Checked;
                if (want != wheelPatched || mem.Process.Id != wheelPid)
                {
                    bool? r = null;
                    try { r = WheelAnim.Set(mem, want); } catch (GameMemoryException) { }
                    wheelPid = mem.Process.Id; wheelPatched = r == true;
                    if (r == null) Show(Stamp + "steering wheel animation: this NR2003.exe isn't the one Race Control knows, left as it is");
                    else if (want) Show(Stamp + "steering wheel animation: in-car wheels follow the real steering");
                }
            }
            if (engine == null) return;
            engine.RaceBack = checks["RaceBack"].Checked;
            engine.StageRaceBack = checks["StageRaceBack"].Checked;
            engine.StageRacing = checks["StageRacing"].Checked;
            engine.StageThrowCaution = checks["StageThrowCaution"].Checked;
            engine.StageEnds = StageEndsFor;
            engine.GreenWhiteCheckered = checks["GreenWhiteCheckered"].Checked;
            // classic rules (no wave around, no cone): the game's own pit road timing, pace laps and one to go,
            // plus the Lucky Dog and (with double file on) the 2004 lineup: lead lap outside, lapped inside
            bool stock = checks["StockCautions"].Checked;
            bool classic = stock || (!checks["WaveAround"].Checked && !checks["Cone"].Checked);
            engine.Classic = classic;
            engine.OpenPitsToAll = !classic;      // pit road timing is fixed since 2.9.1 (it was the Pit road tab)
            engine.GhostOnPitRoad = checks["GhostOnPitRoad"].Checked;
            engine.WaveAroundOn = checks["WaveAround"].Checked;
            engine.LappedToBack = checks["WaveAround"].Checked;
            engine.DoubleFile = checks["DoubleFile"].Checked;
            engine.Cone = checks["Cone"].Checked;
            engine.Untangle = checks["Untangle"].Checked;
            engine.GhostWhilePacing = checks["GhostWhilePacing"].Checked;
            engine.PreferredLane = Math.Max(0, laneBox.SelectedIndex);
            engine.GiveUpRows = (int)giveUpBox.Value;
            engine.FrontRows = (int)frontRowsBox.Value;
            engine.ConePoint = coneBox.Value > 0 ? (double)coneBox.Value : double.NaN;
            engine.PitsClosedFirstLap = !classic;
            engine.OpenWhenPacked = !classic;
            engine.PlayerChoosesLane = checks["PlayerChoosesLane"].Checked;
            string v;
            engine.EarlyLineup = !settings.TryGetValue("EarlyLineup", out v) || v != "0"; // advanced, ini only
            engine.RestorePaceLap = !settings.TryGetValue("RestorePaceLap", out v) || v != "0";
            engine.ForgivePenalties = !settings.TryGetValue("ForgivePenalties", out v) || v != "0";
            engine.WaveEarly = !settings.TryGetValue("WaveEarly", out v) || v != "0";         // advanced, ini only (2026-11-08 test)
            engine.WaveAtLine = !settings.TryGetValue("WaveAtLine", out v) || v != "0";       // advanced, ini only (2026-11-08 Talladega splits)
            engine.PaceFixes = !settings.TryGetValue("PaceFixes", out v) || v != "0";
            engine.PaceLapFit = !settings.TryGetValue("PaceLapFit", out v) || v != "0";       // 3.3.8, advanced, ini only: 0 = 3.3.7's tail catch-up
            engine.PaceMatch = settings.TryGetValue("PaceMatch", out v) && v == "1";        // 3.2.9: off unless PaceMatch=1 (the field slows with it)
            double pull;
            engine.LeadLapHighPull = settings.TryGetValue("LeadLapHighPull", out v) && double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out pull) ? Math.Max(0, Math.Min(3, pull)) : 1.0;
            engine.FreePassOn = checks["FreePass"].Checked;
            engine.StartPaceLaps = (int)startPaceBox.Value;
            engine.StartPaceAddLaps = !settings.TryGetValue("StartPaceAddLaps", out v) || v != "0";   // ini only
            engine.HoldOneToGoForPits = checks["HoldOneToGo"].Checked;
            engine.HoldGreenIfSplit = checks["HoldGreenIfSplit"].Checked;
            engine.PaceCarWaits = checks["PaceCarWaits"].Checked;
            engine.LappedDropLow = checks["LappedDropLow"].Checked;
            engine.LappedHoldUntilPitsDone = !settings.TryGetValue("LappedHoldUntilPitsDone", out v) || v != "0";
            engine.StockCautions = stock;
            if (stock)
            {
                // NR2003 runs the caution (user, 2026-10-28): everything RaceControl would write is off but the Lucky Dog
                engine.RaceBack = engine.StageRaceBack = true;          // never freeze the field
                engine.StageRacing = engine.StageThrowCaution = engine.GreenWhiteCheckered = false;
                engine.GhostOnPitRoad = engine.GhostWhilePacing = false;
                engine.WaveAroundOn = engine.LappedToBack = engine.DoubleFile = engine.Cone = engine.Untangle = false;
                engine.PitsClosedFirstLap = engine.OpenWhenPacked = engine.OpenPitsToAll = false;
                engine.HoldOneToGoForPits = engine.HoldGreenIfSplit = engine.PaceCarWaits = engine.LappedDropLow = false;
                engine.ForgivePenalties = false;
            }
            HubFeed.Current.Enabled = !settings.TryGetValue("UseHub", out v) || v != "0";   // NR2003 Companions Hub (MODS\Hub)
            HubFeed.Current.Logger = Show;
            double highShare;
            engine.LeadLapHighShare = settings.TryGetValue("LeadLapHighShare", out v) &&
                double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out highShare) ? Math.Max(0, Math.Min(1, highShare)) : 0.7;
            double paceAt;
            engine.PaceReleaseAt = settings.TryGetValue("PaceRelease." + TrackName, out v) &&
                double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out paceAt) ? paceAt : double.NaN;
            if (settings.TryGetValue("PaceExit." + TrackName, out v)) engine.PaceExitSaved = v;   // 3.2.7: the last clean pace car exit here
            GamePause.KeyName = settings.TryGetValue("PauseKey", out v) && v != "" ? v : "Pause";
            art.ExtraFolder = settings.TryGetValue("ArtFolder", out v) && v != "" ? v : null;
            art.SeriesChoice = settings.TryGetValue("ArtSeries", out v) && v != "" ? v : null;
            art.UseCompanion = !settings.TryGetValue("ArtOverlayApp", out v) || v != "0";
            art.CompanionFolder = settings.TryGetValue("StageCompanionFolder", out v) && v != "" ? v : null;
            List<string> roots = art.Roots();
            artLabel.Text = roots.Count == 0 ? "no art found: numbers drawn" : string.Join(", ", roots.Select(r => art.IsCompanionAppFolder(r) ? "companion app art" : Path.GetFileName(Path.GetDirectoryName(Path.Combine(r, "x")).TrimEnd(Path.DirectorySeparatorChar))).ToArray());
            tips.SetToolTip(artLabel, string.Join("\n", roots.ToArray()));
            double entry, dfAt;
            engine.DoubleFileAt = settings.TryGetValue("DoubleFileAt." + TrackName, out v) &&
                double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dfAt) ? dfAt : double.NaN;
            engine.PitEntry = settings.TryGetValue("PitEntry." + TrackName, out v) &&
                double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out entry) ? entry : double.NaN;
        }

        // ---- the player's qualifying turn: paused until ready ---------------------------------------------

        void ShowQualReady()
        {
            if (mem == null || engine == null) return;
            CloseQualReady();
            if (!engine.Paused) Show(Stamp + "your qualifying turn: NR2003 paused " + GamePause.Pause(mem) + " - set your camera, then resume");
            qualReadySeenPaused = engine.Paused;
            qualReadyAt = DateTime.Now;
            qualReady = new QualReadyForm(OverlayForm.GameRect(mem.Process.MainWindowHandle), qual.PlayerCall, qual.DnqSeconds, () =>
            {
                if (engine != null && engine.Paused && mem != null) GamePause.Resume(mem);
                qual.PlayerResumed();
                CloseQualReady();
            });
            qualReady.Show();
        }

        void QualReadyTick()
        {
            if (qualReady == null) return;
            if (!qual.PlayerHolding) { CloseQualReady(); return; }             // DNQ / garage / session over
            bool paused = engine != null && engine.Paused;
            if (paused) qualReadySeenPaused = true;
            else if (qualReadySeenPaused)                                    // you resumed the game yourself
            {
                Show(Stamp + "your qualifying turn: game resumed - pull out within " + qual.DnqSeconds.ToString("0") + " s");
                qual.PlayerResumed();
                CloseQualReady();
            }
            else if ((DateTime.Now - qualReadyAt).TotalSeconds > 3 && !qualReadyWarned)
            {
                qualReadyWarned = true;                                      // never paused: the card waits for a click
                Show(Stamp + "the game did not pause for your turn (fallback pause key '" + GamePause.KeyName + "'; set PauseKey= in RaceControl.ini) - click RESUME when ready");
            }
        }
        bool qualReadyWarned;

        void CloseQualReady()
        {
            if (qualReady != null && !qualReady.IsDisposed) qualReady.Close();
            qualReady = null;
            qualReadyWarned = false;
        }

        // ---- the player's lane at the cone ---------------------------------------------------------------

        void ShowChooser(ConeOffer offer)
        {
            if (mem == null) return;
            if (chooser != null && !chooser.IsDisposed) chooser.CloseQuietly();
            pausedForChooser = false;
            if (!engine.Paused)
            {
                string how = GamePause.Pause(mem);
                Show(Stamp + "lane chart: NR2003 paused " + how);
                var check = new Timer { Interval = 700 };
                check.Tick += (s, e) =>
                {
                    check.Stop();
                    check.Dispose();
                    pausedForChooser = engine != null && engine.Paused;
                    if (!pausedForChooser)
                        Show(Stamp + "the game did not pause for the lane chart (fallback pause key '" + GamePause.KeyName + "'; set PauseKey= in RaceControl.ini) - choose within 10 s");
                };
                check.Start();
            }
            else pausedForChooser = true;
            if (board != null && !board.IsDisposed) board.Hide();
            chooser = new ConeChooser(offer, art, OverlayForm.GameRect(mem.Process.MainWindowHandle), boardLayout.Scale, lane => { if (engine != null) engine.ChoosePlayerLane(lane); }, () =>
            {
                if (engine != null && engine.Paused) GamePause.Resume(mem);
                pausedForChooser = false;
                chooser = null;
            });
            chooser.Show();
            chooser.Activate();
        }

        void ShowBoardLayout()
        {
            bool was = loading;
            loading = true;
            boardSizeBox.Value = Math.Max(boardSizeBox.Minimum, Math.Min(boardSizeBox.Maximum, (decimal)Math.Round(boardLayout.Scale * 100)));
            boardHoldBox.Value = Math.Max(boardHoldBox.Minimum, Math.Min(boardHoldBox.Maximum, (decimal)boardLayout.Hold));
            loading = was;
        }

        void SaveBoardLayout()
        {
            settings["Board"] = boardLayout.ToString();
            ShowBoardLayout();
            SaveSettings();
            ShowPreset();
        }

        // ---- live page ---------------------------------------------------------------------------------

        void StartLive()
        {
            if (loading || liveLink == null) return;
            bool on = checks["LivePage"].Checked, net = on && checks["LiveNetwork"].Checked;
            string v;
            int port;
            if (!settings.TryGetValue("LivePort", out v) || !int.TryParse(v, out port) || port < 1024 || port > 65535) port = LiveServer.DefaultPort;
            if (live != null && on && live.Network == net && live.Port == port) return;
            bool wasNet = live != null && live.Network;
            if (live != null) { live.Stop(); live = null; }
            if (net != wasNet || net)
            {
                string err = LiveServer.SetFirewall(port, net);
                if (err != null) Show(Stamp + "live page: firewall rule not set (" + err + ")");
            }
            liveLink.Links.Clear();
            if (!on) { liveLink.Text = "off"; return; }
            try
            {
                live = new LiveServer(port, net);
                live.Start();
            }
            catch (Exception ex)
            {
                live = null;
                liveLink.Text = "port " + port + " in use (LivePort= in RaceControl.ini)";
                Show(Stamp + "live page: " + ex.Message);
                return;
            }
            string[] ips = net ? LiveServer.Addresses() : new string[0];
            string url = "http://" + (ips.Length > 0 ? ips[0] : "localhost") + ":" + port + "/";
            liveLink.Text = url + (ips.Length > 1 ? "  (also " + string.Join(", ", ips.Skip(1).ToArray()) + ")" : "");
            liveLink.Links.Add(0, url.Length, net ? url : "http://localhost:" + port + "/");
            Show(Stamp + "live page: " + url + (net ? " (other computers on your network can open it)" : " (this computer only)"));
        }

        void LiveTick()
        {
            if (live == null) return;
            DateTime now = DateTime.Now;
            if ((now - lastLiveJson).TotalMilliseconds >= 500)
            {
                lastLiveJson = now;
                string preset;
                settings.TryGetValue("Preset", out preset);
                var tail = logBox.Items.Cast<object>().Skip(Math.Max(0, logBox.Items.Count - 80)).Select(o => o.ToString()).ToList();
                try
                {
                    live.StateJson = engine != null ? engine.LiveJson(tail, presetBox.SelectedItem is Preset ? preset : "(custom)")
                        : "{\"inRace\":false,\"time\":\"" + now.ToString("HH:mm:ss") + "\",\"track\":\"\",\"log\":[]}";
                }
                catch (Exception) { }
            }
            if ((now - live.BoardWantedAt).TotalSeconds > 3 || (now - lastBoardPng).TotalMilliseconds < 66) return;
            lastBoardPng = now;
            live.BoardPng = BoardPicture();
        }

        // The board as it is on screen now: the lane chooser, else the board, else the one being placed.
        byte[] BoardPicture()
        {
            Form f = null;
            Action<System.Drawing.Graphics> render = null;
            if (chooser != null && !chooser.IsDisposed) { f = chooser; render = chooser.Render; }
            else if (board != null && !board.IsDisposed && !board.Finished) { f = board; render = board.Render; }
            else if (placing != null && !placing.IsDisposed) { f = placing; render = placing.Render; }
            if (f == null || f.Width < 2 || f.Height < 2) return null;
            try
            {
                using (var b = new Bitmap(f.Width, f.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var ms = new MemoryStream())
                {
                    using (var g = Graphics.FromImage(b)) render(g);
                    b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch (Exception) { return null; }
        }

        // ---- presets -------------------------------------------------------------------------------------

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        // The options a preset holds, as they are now.
        Dictionary<string, string> PresetValues()
        {
            var d = checks.Where(kv => !NotInPresets.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Checked ? "1" : "0");
            string v;
            d["EarlyLineup"] = settings.TryGetValue("EarlyLineup", out v) && v == "0" ? "0" : "1";
            d["RestorePaceLap"] = settings.TryGetValue("RestorePaceLap", out v) && v == "0" ? "0" : "1";
            d["BoardSize"] = Math.Round(boardLayout.Scale * 100).ToString(Inv);
            d["BoardHold"] = boardLayout.Hold.ToString("0.0", Inv);
            return d;
        }

        // Fills the list and selects the preset the options match (the one last chosen first), else (custom).
        void ShowPreset()
        {
            if (presetBox == null || loading) return;
            var all = PresetStore.BuiltIns().Concat(PresetStore.Mine()).ToList();
            var now = PresetValues();
            string last;
            settings.TryGetValue("Preset", out last);
            Preset match = all.FirstOrDefault(p => p.Name == last && p.Matches(now)) ?? all.FirstOrDefault(p => p.Matches(now));
            presetSync = true;
            presetBox.BeginUpdate();
            presetBox.Items.Clear();
            if (match == null) presetBox.Items.Add("(custom)");
            foreach (var p in all) presetBox.Items.Add(p);
            presetBox.SelectedItem = match ?? (object)"(custom)";
            presetBox.EndUpdate();
            presetSync = false;
            presetDelete.Enabled = match != null && !match.BuiltIn;
        }

        void ApplyPreset(Preset p)
        {
            if (p == null) return;
            loading = true;
            string v;
            foreach (var kv in checks)
                if (p.Values.TryGetValue(kv.Key, out v)) kv.Value.Checked = v == "1";
            if (p.Values.TryGetValue("EarlyLineup", out v)) settings["EarlyLineup"] = v == "0" ? "0" : "1";
            if (p.Values.TryGetValue("RestorePaceLap", out v)) settings["RestorePaceLap"] = v == "0" ? "0" : "1";
            double d;
            if (p.Values.TryGetValue("BoardSize", out v) && double.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out d))
                boardLayout.Scale = Math.Max(0.4, Math.Min(2.0, d / 100));
            if (p.Values.TryGetValue("BoardHold", out v) && double.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out d))
                boardLayout.Hold = Math.Max(1, Math.Min(30, d));
            settings["Board"] = boardLayout.ToString();
            settings["Preset"] = p.Name;
            ShowBoardLayout();
            loading = false;
            ApplyOptions();
            SaveSettings();
            ShowPreset();
            Show(Stamp + "preset: " + p.Name);
        }

        void SavePreset()
        {
            string last;
            settings.TryGetValue("Preset", out last);
            bool mine = PresetStore.Mine().Any(p => p.Name == last);
            string name = PresetStore.AskName(this, mine ? last : "");
            if (name == null) return;
            try { PresetStore.Save(name, PresetValues()); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save preset", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            settings["Preset"] = name;
            SaveSettings();
            ShowPreset();
            Show(Stamp + "preset saved: " + name);
        }

        void DeletePreset()
        {
            var p = presetBox.SelectedItem as Preset;
            if (p == null || p.BuiltIn) return;
            if (MessageBox.Show(this, "Delete the preset \"" + p.Name + "\"? The options stay as they are.", "Delete preset",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { PresetStore.Delete(p.Name); } catch (Exception ex) { Show(Stamp + ex.Message); }
            ShowPreset();
            Show(Stamp + "preset deleted: " + p.Name);
        }

        Rectangle GameArea { get { return mem != null ? OverlayForm.GameRect(mem.Process.MainWindowHandle) : Screen.PrimaryScreen.Bounds; } }

        ComboBox themeBox;
        void ApplyTheme()
        {
            var mode = themeBox.SelectedIndex == 2 ? Theme.Mode.Dark : themeBox.SelectedIndex == 1 ? Theme.Mode.Light : Theme.Mode.System;
            Theme.Apply(this, Theme.IsDark(mode));
            if (mapForm != null && !mapForm.IsDisposed) mapForm.SetDark(Theme.IsDark(mode));
        }

        // ---- the track map: recorded from the cars, the cone placed by clicking it ----
        void MapTick()
        {
            if (mem == null || engine == null || mem.U8(Addr.SimState) != 2) return;
            string t = engine.TrackName;
            double len = mem.F64(Addr.TrackLength);
            if (string.IsNullOrEmpty(t) || !(len > 100 && len < 100000)) return;
            if (t != mapTrack) { mapTrack = t; LoadMap(t, len); }
            if (!mapRec.Active) return;
            TrackMap made = mapRec.Sample(mem, Math.Min(mem.I32(Addr.NumCars), 64) - 1);
            if (made == null) return;
            trackMap = made;
            string saved;
            try { made.Save(); saved = "saved to " + TrackMap.PathFor(t); }
            catch (Exception ex) { saved = "could not save it: " + ex.Message; }
            Show(Stamp + string.Format(System.Globalization.CultureInfo.InvariantCulture, "track map: drawn from {0} cars in {1:0} s, {2}; {3}",
                mapRec.Cars, (DateTime.Now - mapRec.Started).TotalSeconds, mapRec.Note, saved));
        }

        void LoadMap(string t, double len)
        {
            trackMap = TrackMap.Load(t);
            if (trackMap != null && Math.Abs(trackMap.Length - len) > 5)
            {
                Show(Stamp + string.Format("track map: the saved map is for a {0:0} m lap, this track is {1:0} m - drawing it again", trackMap.Length, len));
                trackMap = null;
            }
            if (trackMap != null && trackMap.Source == TrackMap.Recorded) { mapRec.Stop(); return; }
            if (trackMap == null)
            {
                string from;
                trackMap = TrackMap.Import(t, len, out from);
                if (trackMap != null) Show(Stamp + "track map: using another overlay app's map of this track until the cars have drawn RaceControl's own");
            }
            mapRec.Start(t, len);
        }

        string MapStatus()
        {
            if (mem == null || engine == null || string.IsNullOrEmpty(mapTrack)) return "Waiting for NR2003 and a session at a track.";
            string seen = mapRec.Active ? string.Format("{0}/{1} of the lap seen", mapRec.Covered, TrackMap.N) : "";
            if (trackMap == null) return "No map yet - drawing it from the cars on track: " + seen + ".";
            if (trackMap.Source == TrackMap.Imported) return "Map from another overlay app; RaceControl draws its own as the cars run (" + seen + ").";
            return mapTrack + ": drawn from the cars " + trackMap.Made + ", " + trackMap.Length.ToString("0") + " m lap.";
        }

        List<PointF> MapCars()
        {
            var list = new List<PointF>();
            if (mem == null) return list;
            try
            {
                int max = Math.Min(mem.I32(Addr.NumCars), 64) - 1;
                var order = new List<int> { Addr.PaceCarIdx };
                for (int i = 0; i <= max; i++) if (i != Addr.PaceCarIdx) order.Add(i);
                foreach (int i in order)
                {
                    bool on = mem.U8(Addr.HasNotRetired + i) == 1 && mem.U8(Addr.HasSpawned + i) == 1 && mem.U8(Addr.OnPitRoad + i) != 1;
                    long b = 0xACCA30 + (long)i * Addr.Ocd + 6622;
                    list.Add(on ? new PointF(mem.F32(b), mem.F32(b + 4)) : new PointF(float.NaN, float.NaN));
                }
            }
            catch (GameMemoryException) { }
            return list;
        }

        void ShowTrackMap()
        {
            if (mapForm != null && !mapForm.IsDisposed) { mapForm.Activate(); return; }
            var mode = themeBox.SelectedIndex == 2 ? Theme.Mode.Dark : themeBox.SelectedIndex == 1 ? Theme.Mode.Light : Theme.Mode.System;
            mapForm = new TrackMapForm(() => trackMap, () =>
            {
                var mk = new TrackMapForm.Marks { Cone = coneBox.Value > 0 ? (double)coneBox.Value : double.NaN };
                if (engine != null)
                {
                    mk.PitEntry = engine.PitEntry;
                    mk.DoubleFile = engine.DoubleFileAt;
                    if (engine.PitMerges.Count > 0) mk.PitExit = engine.PitMerges[0][0];
                }
                return mk;
            }, MapCars, MapStatus, d => coneBox.Value = Math.Max(1, Math.Min(10000, (decimal)Math.Round(d))), () =>
            {
                if (string.IsNullOrEmpty(mapTrack)) return;
                try { File.Delete(TrackMap.PathFor(mapTrack)); } catch (Exception) { }
                trackMap = null;
                double len = mem != null ? mem.F64(Addr.TrackLength) : 0;
                mapRec.Start(mapTrack, len);
                Show(Stamp + "track map: drawing " + mapTrack + " again from the cars on track");
            }, Theme.IsDark(mode));
            mapForm.Show(this);
        }

        // ---- stage ends: one box per stage, stored as "25, 61" ----
        void StageBoxesFromText()
        {
            stageSync = true;
            var v = stageEndsBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < 3; i++) stageVals[i].Text = i < v.Length ? v[i] : "";
            stageSync = false;
            StageRowsUi();
        }

        void StageTextFromBoxes()
        {
            int n = stageCountBox.SelectedIndex + 1;                               // stage ends to give = stages - 1
            stageEndsBox.Text = string.Join(", ", stageVals.Take(n).Select(t => t.Text.Trim()).Where(x => x.Length > 0).ToArray());
        }

        void StageRowsUi()
        {
            if (stageVals[0] == null || stageCountBox.SelectedIndex < 0) return;
            int n = stageCountBox.SelectedIndex + 1;
            string unit = stageModeBox.SelectedIndex == 2 ? "lap" : stageModeBox.SelectedIndex == 1 ? "% of the race" : "set by the track's length";
            for (int i = 0; i < 3; i++)
            {
                bool show = i < n;
                stageLabels[i].Visible = stageVals[i].Visible = stageUnits[i].Visible = show;
                stageVals[i].Enabled = stageEndsBox.Enabled;
                stageUnits[i].Text = unit;
            }
        }

        // ---- knockout rounds: one row per round, stored as "25:24, 10:12, 5:12" ----
        void RoundRowsFromText()
        {
            roundSync = true;
            var r = Qualifying.ParseRounds(qualRoundsBox.Text);
            for (int i = 0; i < 4; i++)
            {
                roundMins[i].Value = i < r.Count ? (decimal)Math.Max(0, Math.Min(180, r[i].Item1)) : 0;
                roundCars[i].Value = i < r.Count ? Math.Max(1, Math.Min(60, r[i].Item2)) : 12;
            }
            roundSync = false;
        }

        void RoundsTextFromRows()
        {
            var parts = new List<string>();
            for (int i = 0; i < 4; i++)
                if (roundMins[i].Value > 0) parts.Add(roundMins[i].Value + ":" + roundCars[i].Value);
            qualRoundsBox.Text = string.Join(", ", parts.ToArray());
        }

        // ---- running order: a drag-and-drop list, stored as car numbers ----
        void ShowOrderList()
        {
            if (orderList == null) return;
            int sel = orderList.SelectedIndex;
            orderList.BeginUpdate();
            orderList.Items.Clear();
            for (int i = 0; i < orderOrder.Count; i++)
            {
                string nm;
                orderList.Items.Add(string.Format("{0,2}.  #{1,-4} {2}", i + 1, orderOrder[i], orderNames.TryGetValue(orderOrder[i], out nm) ? nm : ""));
            }
            orderList.EndUpdate();
            if (sel >= 0 && sel < orderList.Items.Count) orderList.SelectedIndex = sel;
        }

        void OrderChanged()
        {
            qualOrderBox.Text = string.Join(" ", orderOrder.ToArray());     // saved with the other qualifying settings
            ShowOrderList();
        }

        void OrderMove(int dir)
        {
            int i = orderList.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= orderOrder.Count) return;
            string n = orderOrder[i]; orderOrder.RemoveAt(i); orderOrder.Insert(j, n);
            OrderChanged(); orderList.SelectedIndex = j;
        }

        void OrderGetField()
        {
            if (mem == null) { Show(Stamp + "running order: start NR2003 and load a session first"); return; }
            List<Tuple<int, string, string>> roster;
            try { roster = Qualifying.Roster(mem); }
            catch (Exception ex) { Show(Stamp + "running order: " + ex.Message); return; }
            if (roster.Count == 0) { Show(Stamp + "running order: no AI cars found - load a session (practice) first"); return; }
            foreach (var r in roster) orderNames[r.Item2] = r.Item3;
            var nums = roster.Select(r => r.Item2).ToList();
            var keep = orderOrder.Where(nums.Contains).ToList();                   // your order for cars still here
            keep.AddRange(nums.Where(n => !keep.Contains(n)).OrderBy(n => n.Length).ThenBy(n => n));
            orderOrder = keep;
            OrderChanged();
            Show(Stamp + "running order: " + orderOrder.Count + " cars");
        }

        // ---- qualifying timing box ----
        void PlaceQualBox()
        {
            if (qualStyleBox != null && qualStyleBox.SelectedIndex == 1) { PlaceBroadcast(); return; }
            if (qualPlacing != null && !qualPlacing.IsDisposed) { qualPlacing.Activate(); return; }
            if (qualBox != null) { qualBox.Finish(); qualBox = null; }               // only the copy you're moving is shown
            qualPlacing = new QualBoxForm(art, GameArea, qualBoxLayout, true);
            if (qual.LiveOn) qualPlacing.Data = QualBoxNow();
            qualPlacing.Saved += l =>
            {
                qualBoxLayout = l; settings["QualBox"] = l.ToString(); SaveSettings();
                Show(Stamp + "timing box placed, size " + Math.Round(l.Scale * 100) + "%");
            };
            qualPlacing.FormClosed += (s, e) => { qualPlacing = null; };            // the live box comes back in the saved spot
            qualPlacing.Show();
            qualPlacing.Activate();
        }

        // The broadcast style is placed with a copy of itself (same layout and size setting as the timing box).
        QualBroadcastForm broadPlacing;
        void PlaceBroadcast()
        {
            if (broadPlacing != null && !broadPlacing.IsDisposed) { broadPlacing.Activate(); return; }
            if (qualBroad != null) { qualBroad.Finish(); qualBroad = null; }
            broadPlacing = new QualBroadcastForm(art, GameArea, qualBoxLayout, true);
            if (qual.LiveOn && mem != null) broadPlacing.Data = BroadcastNow();
            broadPlacing.Saved += l =>
            {
                qualBoxLayout = l; settings["QualBox"] = l.ToString(); SaveSettings();
                Show(Stamp + "broadcast overlay placed, size " + Math.Round(l.Scale * 100) + "%");
            };
            broadPlacing.FormClosed += (s, e) => { broadPlacing = null; };
            broadPlacing.Show();
            broadPlacing.Activate();
        }

        void PlaceBanner()
        {
            if (bannerPlacing != null && !bannerPlacing.IsDisposed) { bannerPlacing.Activate(); return; }
            if (banner != null) { banner.Close(); banner = null; }                   // only the copy you're moving is shown
            bannerPlacing = new PlayerBannerForm(GameArea, bannerLayout, true);
            bannerPlacing.Saved += l =>
            {
                bannerLayout = l; settings["PlayerBanner"] = l.ToString(); SaveSettings();
                Show(Stamp + "player banner placed, size " + Math.Round(l.Scale * 100) + "%");
            };
            bannerPlacing.FormClosed += (s, e) => { bannerPlacing = null; };        // the live banner comes back in the saved spot
            bannerPlacing.Show();
            bannerPlacing.Activate();
        }

        void BannerTick()
        {
            string text = mem != null && checks.ContainsKey("ShowPlayerCalls") && checks["ShowPlayerCalls"].Checked ? qual.PlayerCall : "";
            int kind = qual.PlayerCallKind;
            string timer = qual.PlayerTimer; bool urgent = qual.PlayerTimerUrgent;
            // in a race (qualifying not live): the race banner from the engine (RaceEngine.PlayerCall.cs)
            if (text.Length == 0 && !qual.LiveOn && engine != null && mem != null && checks.ContainsKey("ShowPlayerCalls") && checks["ShowPlayerCalls"].Checked)
            { text = engine.PlayerCall; kind = engine.PlayerCallKind; timer = ""; urgent = false; }
            if (banner != null && banner.IsDisposed) banner = null;
            if (bannerPlacing != null && !bannerPlacing.IsDisposed) text = "";
            if (string.IsNullOrEmpty(text)) { if (banner != null) { banner.Close(); banner = null; } return; }
            if (banner == null)
            {
                try { banner = new PlayerBannerForm(GameArea, bannerLayout); banner.Show(); }
                catch (Exception ex) { Show(Stamp + "player banner: " + ex.Message); banner = null; return; }
            }
            banner.Set(text, kind, timer, urgent);
        }

        void QualBoxTick()
        {
            BannerTick();
            if (qual.LiveOn && mem != null) TrackHeld(); else if (!qual.LiveOn) heldCar = -1;
            if (mem != null) try { LapFmt.TrackM = officialLen > 0 ? officialLen : mem.F64(Addr.TrackLength); } catch (GameMemoryException) { }
            bool placingNow = (qualPlacing != null && !qualPlacing.IsDisposed) || (broadPlacing != null && !broadPlacing.IsDisposed);
            if (qualPlacing != null && !qualPlacing.IsDisposed && qual.LiveOn) { qualPlacing.Data = QualBoxNow(); qualPlacing.Invalidate(); }
            // the broadcast copy being placed shows the live run too (it kept the sample driver - beta tester 2026-10-27)
            if (broadPlacing != null && !broadPlacing.IsDisposed && qual.LiveOn && mem != null && (DateTime.Now - broadAt).TotalMilliseconds >= 40)
            {
                broadAt = DateTime.Now;
                var pd = BroadcastNow();
                broadPlacing.Animate(pd.ShowOrder);
                broadPlacing.Data = pd; broadPlacing.Invalidate();
            }
            bool want = checks["ShowQualBox"].Checked && qual.LiveOn && mem != null && !placingNow;
            bool broadcast = qualStyleBox != null && qualStyleBox.SelectedIndex == 1;
            if (qualBox != null && (qualBox.IsDisposed || qualBox.Finished)) qualBox = null;
            if (qualBroad != null && (qualBroad.IsDisposed || qualBroad.Finished)) qualBroad = null;
            if (!want || !broadcast) { if (qualBroad != null) { qualBroad.Finish(); qualBroad = null; } }
            if (want && broadcast)
            {
                if (qualBox != null) { qualBox.Finish(); qualBox = null; }
                if (qualBroad == null)
                {
                    try { qualBroad = new QualBroadcastForm(art, GameArea, qualBoxLayout); qualBroad.Show(); }
                    catch (Exception ex) { Show(Stamp + "broadcast overlay: " + ex.Message); qualBroad = null; return; }
                }
                // about 25 frames a second, and a repaint only when something on it changed (a big layered window
                // redrawn 100 times a second was needless load on the game's graphics)
                if ((DateTime.Now - broadAt).TotalMilliseconds < 40) return;
                broadAt = DateTime.Now;
                var data = BroadcastNow();
                bool moving = qualBroad.Animate(data.ShowOrder);
                string key = data.Key();
                if (moving || key != broadKey) { qualBroad.Data = data; qualBroad.Invalidate(); broadKey = key; }
                return;
            }
            if (!want) { if (qualBox != null) { qualBox.Finish(); qualBox = null; } return; }
            if (qualBox == null)
            {
                try { qualBox = new QualBoxForm(art, GameArea, qualBoxLayout); qualBox.Show(); }
                catch (Exception ex) { Show(Stamp + "timing box: " + ex.Message); qualBox = null; return; }
            }
            qualBox.Data = QualBoxNow();
            qualBox.Invalidate();
        }

        // the car the broadcast overlay and the telemetry strip follow: the car on its run (single car runs; the car on
        // TV if that one is on a timed lap) or the car on TV (knockout)
        int BoxCar()
        {
            int tv = QualTiming.Focus(mem);
            int car = qual.SingleRuns ? qual.BoxCar : tv;
            if (qual.SingleRuns && qual.BoxKind != Qualifying.BoxShow.Result && QCar(tv) && tv != car && qual.OnTimedLap(tv)) car = tv;
            return car;
        }

        // the in-game telemetry strip (TeleStrip.cs): the box car's throttle and brake lines and its steering wheel
        bool StripWanted { get { return checks.ContainsKey("ShowTeleStrip") && checks["ShowTeleStrip"].Checked; } }

        void TeleStripTick()
        {
            bool placing = stripPlacing != null && !stripPlacing.IsDisposed;
            bool want = StripWanted && mem != null && !placing;
            if (teleStrip != null && (teleStrip.IsDisposed || teleStrip.Finished)) teleStrip = null;
            if (!want) { if (teleStrip != null) { teleStrip.Finish(); teleStrip = null; } return; }
            if ((DateTime.Now - stripAt).TotalMilliseconds < 40) return;
            stripAt = DateTime.Now;
            if (teleStrip == null)
            {
                try { teleStrip = new TeleStripForm(GameArea, stripLayout); teleStrip.Show(); }
                catch (Exception ex) { Show(Stamp + "telemetry strip: " + ex.Message); teleStrip = null; return; }
            }
            var rec = qual.LiveOn ? tele : tvTele;
            int car = qual.LiveOn ? BoxCar() : QualTiming.Focus(mem);
            var d = new TeleStripData { Indy = qual.IndyFormat, Now = rec.Now };
            var tc = QCar(car) ? rec.Of(car) : null;
            if (tc != null && tc.HaveNow && qual.HasMemory)
            {
                d.Have = true; d.Num = qual.CarNumber(car); d.Last = qual.LastName(car);
                d.Thr = tc.Now.Thr; d.Brk = tc.Now.Brk; d.Steer = tc.Now.Steer;
                d.Recent.AddRange(tc.Recent);
            }
            teleStrip.Data = d; teleStrip.Invalidate();
        }

        void PlaceStrip()
        {
            if (stripPlacing != null && !stripPlacing.IsDisposed) { stripPlacing.Activate(); return; }
            if (teleStrip != null) { teleStrip.Finish(); teleStrip = null; }
            stripPlacing = new TeleStripForm(GameArea, stripLayout, true);
            stripPlacing.Saved += l =>
            {
                stripLayout = l; settings["TeleStrip"] = l.ToString(); SaveSettings();
                Show(Stamp + "telemetry strip placed, size " + Math.Round(l.Scale * 100) + "%");
            };
            stripPlacing.FormClosed += (s, e) => { stripPlacing = null; };
            stripPlacing.Show();
            stripPlacing.Activate();
        }

        void OpenTelemetry()
        {
            if (teleWin != null && !teleWin.IsDisposed) { teleWin.Show(); teleWin.Activate(); if (teleWin.WindowState == FormWindowState.Minimized) teleWin.WindowState = FormWindowState.Normal; return; }
            Func<int, string> name = c =>
            {
                try { return mem != null ? "#" + Qualifying.Label(qual.CarNumber(c)) + "  " + qual.LastName(c) : "slot " + c; }
                catch (GameMemoryException) { return "slot " + c; }
            };
            Func<int, int> num = c => { try { return mem != null ? qual.CarNumber(c) : c; } catch (GameMemoryException) { return c; } };
            teleWin = new TelemetryWindow(tele, name, num, Icon);
            teleWin.Show();
        }

        // The broadcast overlay's data: the same car the classic box follows (single car runs) or the car on TV (knockout).
        // Telemetry: speed from the car block's velocity, rpm from the car record (0xC12030 + slot * 5560, +0x1508), the
        // throttle pedal and gear from the block's driver inputs (+6398, +6390 minus 1), and the brake worked out from the
        // car's slowing (the telemetry recorder, Telemetry.cs - the AI have no brake value of their own).
        QualBroadcastData BroadcastNow()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Func<int, string> time = LapFmt.Lap;                       // lap times: m:ss.fff, or mph (Lap times as)
            var d = new QualBroadcastData { LapsTotal = qual.Laps };
            string tn = mem.Str(Addr.TrackName, 64).Trim();
            d.Track = (tn.Contains('_') ? tn.Substring(0, tn.IndexOf('_')) : tn).ToUpperInvariant();
            int car = BoxCar();
            var b = qual.LiveBoard;
            int pole = b.Count > 0 ? b[0].Item1 : -1, poleMs = b.Count > 0 ? b[0].Item2 : 0;
            // the running order, as RaceControl has it: ranked as cars finish (pole first), the car on its run (colour
            // coded), then the cars still to go in the qualifying order. Ten rows: if the live car would be below them,
            // the top seven, the live car and the next two.
            var view = qual.SingleRuns ? qual.OrderView() : b.Select(e => Tuple.Create(e.Item1, e.Item2, 'f')).ToList();
            int liveAt = view.FindIndex(v => v.Item3 == 'l');
            var rows = view.Take(10).ToList();
            if (liveAt >= 10) rows = view.Take(7).Concat(view.Skip(liveAt).Take(3)).ToList();
            int rank = 0;
            foreach (var v in view)
            {
                if (v.Item3 == 'f') rank++;
                if (!rows.Contains(v)) continue;
                int el = v.Item3 == 'l' ? qual.Timing.Elapsed(v.Item1) : -1;
                d.Order.Add(new[] { v.Item3 == 'f' ? rank.ToString() : v.Item3 == 'l' ? "" : "", qual.FirstName(v.Item1), qual.LastName(v.Item1),
                    v.Item3 == 'f' ? (v.Item2 > 0 ? time(v.Item2) : "NO TIME") : v.Item3 == 'l' ? (el >= 0 ? LapFmt.Time(el) : "OUT LAP") : "", v.Item3.ToString() });
            }
            // the running order slides in once a lap (6 s after the car on its run crosses the line) and stays up
            // between runs
            bool live = qual.SingleRuns ? qual.BoxKind == Qualifying.BoxShow.Live : QCar(car);
            var lt = QCar(car) ? qual.Timing.Of(car) : null;
            if (lt != null && (car != orderCar || lt.LapStart != orderLapStart))
            {
                if (car == orderCar && lt.LapStart >= 0) orderUntil = DateTime.Now.AddSeconds(6);
                orderCar = car; orderLapStart = lt.LapStart;
            }
            d.ShowOrder = !live || DateTime.Now < orderUntil;
            if (!QCar(car)) return d;
            d.Num = qual.CarNumber(car); d.First = qual.FirstName(car); d.Last = qual.LastName(car);
            // telemetry: speed from the car's own velocity (the record's speed field read ~30 % high)
            long rec = 0xC12030 + car * 5560L, blk = 0xACCA30 + car * (long)Addr.Ocd;
            double vx = mem.F32(blk + 6634), vy = mem.F32(blk + 6638);
            d.Mph = (float)(Math.Sqrt(vx * vx + vy * vy) * 2.2369363);
            d.Rpm = mem.F32(rec + 0x1508);
            // throttle: the pedal in the car block's driver inputs (+6390 gear, +6394 brake slot, +6398 throttle, +6402
            // rpm). Mapped live 2026-11-13 at Indy and Coronado: 1.0 flat out, 0.41 on the pit road limiter, 0 under
            // braking. (The old source, record +0x604, is the top byte of a bit-packed state word with 3 bits of a frame
            // counter in it; +0x605 after it is steering, not brake.)
            float thr = mem.F32(blk + 6398);
            d.Throttle = float.IsNaN(thr) ? 0 : Math.Max(0, Math.Min(1, thr));
            var tc = tele.Of(car);
            if (tc != null && tc.HaveNow) d.Throttle = tc.Now.Thr;        // the recorder's (lightly smoothed, Telemetry.cs)
            d.Brake = tc != null && tc.HaveNow ? tc.Now.Brk : -1;          // from the telemetry recorder (Telemetry.cs)
            int gb = mem.U8(blk + 6390);
            d.Gear = gb <= 10 ? gb - 1 : 0;
            d.Tele = d.Mph >= 0 && d.Mph < 400 && d.Rpm >= 0 && d.Rpm < 30000;
            float top; rpmTop.TryGetValue(car, out top);
            if (d.Tele && d.Rpm > top) rpmTop[car] = top = d.Rpm;
            d.RpmTop = Math.Max(6000, top);
            // laps: the run's own, else what the timing has
            int lapNo; List<int> laps;
            var t = qual.Timing.Of(car);
            if (qual.RunView(car, out lapNo, out laps))
            {
                d.LapNo = lapNo;
                foreach (int ms in laps) d.LapRows.Add(time(ms));
                if (laps.Count > 0) { d.LastLap = time(laps[laps.Count - 1]); d.BestLap = time(laps.Min()); }
            }
            else if (t != null)
            {
                d.LapNo = 1; d.LapsTotal = 0;
                if (t.LastLapMs > 0) d.LastLap = time(t.LastLapMs);
                if (t.BestMs > 0) d.BestLap = time(t.BestMs);
            }
            int run = t != null ? qual.Timing.Elapsed(car) : -1;
            d.Current = run >= 0 ? LapFmt.Time(run) : "-.---";
            // the accent: lime on pole pace, red when slower
            int delta;
            if (qual.SingleRuns && qual.BoxKind == Qualifying.BoxShow.Result) d.Ahead = !qual.ResultHasDiff || qual.ResultDiffMs <= 0;
            else if (pole >= 0 && pole != car && qual.Timing.Delta(car, pole, poleMs, out delta)) d.Ahead = delta <= 0;
            return d;
        }

        // The last timed lap anyone finished: it stays in the box until the next car starts its first timed lap.
        int heldCar = -1, heldLapMs, heldDiffMs, heldPos;
        DateTime heldStamp = DateTime.MinValue;
        void TrackHeld()
        {
            foreach (var kv in qual.Timing.All)
            {
                var t = kv.Value;
                if (t.LastLapMs <= 0 || t.LastLapAt <= heldStamp) continue;
                heldStamp = t.LastLapAt; heldCar = kv.Key; heldLapMs = t.LastLapMs;
                var others = qual.LiveBoard.Where(x => x.Item1 != kv.Key).Select(x => x.Item2).ToList();
                heldDiffMs = others.Count > 0 ? heldLapMs - others.Min() : 0;         // against the best of the others
                heldPos = 1 + others.Count(ms => ms < heldLapMs);
            }
        }

        // once a second while qualifying is live: what the timing box sees (qual_timing.log next to RaceControl.exe)
        DateTime qualDbgAt = DateTime.MinValue;
        void QualTimingDebug(int car, string kind)
        {
            if ((DateTime.Now - qualDbgAt).TotalSeconds < 1) return;
            qualDbgAt = DateTime.Now;
            try
            {
                var t = QCar(car) ? qual.Timing.Of(car) : null;
                string line = DateTime.Now.ToString("HH:mm:ss") + " car " + car + (kind != "" ? " box " + kind : "") + (t == null ? " (not tracked)" :
                    " state 0x" + t.State.ToString("x") + " lapStart " + t.LapStart + " now " + qual.Timing.NowMs + " run " + qual.Timing.Elapsed(car) +
                    " path " + t.Path.ToString("0") + " lastCross " + t.LastCross + " crossings " + t.NextIdx + " trackLen " + qual.Timing.TrackLen.ToString("0") +
                    " board " + qual.LiveBoard.Count) + Environment.NewLine;
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qual_timing.log"), line);
            }
            catch (Exception) { }
        }

        // Single car runs: Qualifying picks the car (the same one it names to broadcast tools in the state file), whatever
        // the camera is on - unless you put the camera on another car that is on a timed lap yourself.
        //   Result:  the run just done, during its in-lap - its best lap, the gap to pole and its place, held a few seconds
        //   Live:    the car on a timed lap - the live gap to pole (each timed lap), the lap just done for 4 s between laps
        //   Waiting: the next car on its way to the green - "-.--" until it crosses the line
        QualBoxData SingleBox(QualBoxData d, int tv, int pole, int poleMs)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Func<int, string> time = LapFmt.Lap;
            Func<int, string> gap = ms => (ms / 1000.0).ToString("+0.000;-0.000;0.000", inv);
            int car = qual.BoxCar;
            var kind = qual.BoxKind;
            if (kind != Qualifying.BoxShow.Result && QCar(tv) && tv != car && qual.OnTimedLap(tv)) { car = tv; kind = Qualifying.BoxShow.Live; }
            QualTimingDebug(car, kind.ToString());
            if (!QCar(car) || kind == Qualifying.BoxShow.None) return d;
            d.CarNum = qual.CarNumber(car); d.CarName = qual.LastName(car); d.CarPos = "--";
            int delta;
            if (kind == Qualifying.BoxShow.Result)
            {
                d.CarPos = qual.ResultPos + ".";
                if (qual.ResultHasDiff) { d.Projected = time(qual.ResultBestMs); d.Right = gap(qual.ResultDiffMs); d.Mode = 2; d.Faster = qual.ResultDiffMs <= 0; }
                else { d.Right = time(qual.ResultBestMs); d.Mode = 1; }                 // the first time posted: nothing to compare yet
            }
            else if (kind == Qualifying.BoxShow.Live && car == qual.BoxCar && qual.BoxLapMs > 0 && qual.BoxLapAge < 4)
            {
                if (pole >= 0) { d.Projected = time(qual.BoxLapMs); d.Right = gap(qual.BoxLapMs - poleMs); d.Mode = 2; d.Faster = qual.BoxLapMs <= poleMs; }
                else { d.Right = time(qual.BoxLapMs); d.Mode = 1; }
            }
            else if (kind == Qualifying.BoxShow.Live && pole >= 0 && qual.Timing.Delta(car, pole, poleMs, out delta))
            {
                d.Right = gap(delta); d.Mode = 2; d.Faster = delta <= 0;
            }
            else { d.Right = "-.--"; d.Mode = 0; }
            return d;
        }

        QualBoxData QualBoxNow()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var d = new QualBoxData { Header = qual.LiveHeader };
            var b = qual.LiveBoard;
            int pole = b.Count > 0 ? b[0].Item1 : -1, poleMs = b.Count > 0 ? b[0].Item2 : 0;
            if (pole >= 0) { d.PoleNum = qual.CarNumber(pole); d.PoleName = qual.LastName(pole); d.PoleTime = LapFmt.Lap(poleMs); }
            int tv = QualTiming.Focus(mem);
            if (qual.SingleRuns) return SingleBox(d, tv, pole, poleMs);
            // knockout: the car on TV
            int car = tv;
            QualTimingDebug(car, "");
            var t = QCar(car) ? qual.Timing.Of(car) : null;
            int delta, run = t != null ? qual.Timing.Elapsed(car) : -1;
            bool justCrossed = heldCar == car && (DateTime.Now - heldStamp).TotalSeconds < 4;   // the exact lap, for a moment
            double resultAge = (DateTime.Now - heldStamp).TotalSeconds;
            bool completedRun = QCar(heldCar) && !qual.ActiveRunners.Contains(heldCar);
            bool nextFocusedBeforeGreen = qual.SingleRuns && QCar(car) && car != heldCar && tv == car &&
                qual.ActiveRunners.Contains(car) && run < 0;
            // Keep the completed result up for four seconds, and keep it until RaceVision
            // has focused the next runner near the stripe before that runner's green lap.
            if (completedRun && (resultAge < 4 || (run < 0 && !nextFocusedBeforeGreen)))
            {
                d.CarNum = qual.CarNumber(heldCar); d.CarName = qual.LastName(heldCar); d.CarPos = heldPos + ".";
                d.Projected = LapFmt.Lap(heldLapMs);
                d.Right = (heldDiffMs / 1000.0).ToString("+0.000;-0.000;0.000", inv); d.Mode = 2; d.Faster = heldDiffMs <= 0;
                return d;
            }
            if (run >= 0 && !justCrossed)
            {
                // on a timed lap: the running lap time, live, and the gap to pole at this point of the lap
                d.CarNum = qual.CarNumber(car); d.CarName = qual.LastName(car); d.CarPos = "--";
                d.Projected = "";                                                 // no count-up: just the live gap
                if (pole >= 0 && qual.Timing.Delta(car, pole, poleMs, out delta))
                {
                    d.Right = (delta / 1000.0).ToString("+0.000;-0.000;0.000", inv); d.Mode = 2; d.Faster = delta <= 0;
                }
                else { d.Right = ""; d.Mode = 0; }
                return d;
            }
            if (nextFocusedBeforeGreen)
            {
                d.CarNum = qual.CarNumber(car); d.CarName = qual.LastName(car); d.CarPos = "--";
                d.Projected = ""; d.Right = ""; d.Mode = 0;
                return d;
            }
            bool hold = QCar(heldCar) && (qual.SingleRuns || heldCar == car);
            if (hold)
            {
                // the last lap done stays up through the next car's out lap
                d.CarNum = qual.CarNumber(heldCar); d.CarName = qual.LastName(heldCar); d.CarPos = heldPos + ".";
                d.Projected = LapFmt.Lap(heldLapMs);
                d.Right = (heldDiffMs / 1000.0).ToString("+0.000;-0.000;0.000", inv); d.Mode = 2; d.Faster = heldDiffMs <= 0;
                return d;
            }
            if (t != null)
            {
                d.CarNum = qual.CarNumber(car); d.CarName = qual.LastName(car);
                d.Right = QualTiming.InLap.Contains(t.State) ? "IN LAP" : "OUT LAP"; d.Mode = 0;
            }
            return d;
        }

        // A still board with the last cone's lanes (or sample cars) to drag into place.
        void PlaceBoard()
        {
            if (placing != null && !placing.IsDisposed) { placing.Activate(); return; }
            ConeBoardData data = engine != null && engine.ConeBoard != null && engine.ConeBoard.Inside.Count + engine.ConeBoard.Outside.Count >= 2 ? engine.ConeBoard : SampleBoard();
            placing = new ConeBoardForm(data, art, GameArea, boardLayout, true);
            placing.Saved += l => { boardLayout = l; SaveBoardLayout(); Show(Stamp + "choose cone board placed, size " + Math.Round(l.Scale * 100) + "%"); };
            placing.Show();
            placing.Activate();
        }

        ConeBoardData SampleBoard()
        {
            int[] nums = { 9, 88, 19, 24, 11, 22, 4, 14, 2, 8 };
            var d = new ConeBoardData { Title = "CHOOSE CONE", Cars = nums.Length, Done = true };
            for (int k = 0; k < nums.Length; k++)
            {
                bool ins = k % 2 == 1;
                int row = k / 2 + 1, to = ins ? 2 * row - 1 : 2 * row;
                (ins ? d.Inside : d.Outside).Add(new ConeEntry { Idx = 100 + k, Num = nums[k], Lane = ins ? 1 : 2, Row = row, From = k + 1, To = to, Gained = k == 0 ? 0 : k + 1 - to });
            }
            return d;
        }

        // The choose cone board: a new one each double file; taken down 8 s after the green if it is still going.
        void BoardTick()
        {
            if (engine.Flag == 7) { if (greenAt == DateTime.MaxValue) greenAt = DateTime.Now; }
            else greenAt = DateTime.MaxValue;
            ConeBoardData data = engine.ConeBoard;
            bool want = checks["ShowConeBoard"].Checked && data != null && engine.InRace;
            if (board != null && (board.IsDisposed || board.Finished)) board = null;
            if (board != null && (!want || board.Data != data || (DateTime.Now - greenAt).TotalSeconds > 8)) { board.Finish(); board = null; }
            if (want && data != boardShown && (engine.Flag == 5 || engine.Flag == 6) && mem != null)
            {
                boardShown = data;
                try
                {
                    board = new ConeBoardForm(data, art, OverlayForm.GameRect(mem.Process.MainWindowHandle), boardLayout);
                    board.Show();
                }
                catch (Exception ex) { Show(Stamp + "choose cone board: " + ex.Message); board = null; }
            }
            if (board != null && !board.Visible && chooser == null) board.Show();
        }

        // ---- log and attaching ----------------------------------------------------------------------

        void Show(string line)
        {
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
            logBox.Items.Add(line);
            while (logBox.Items.Count > 1000) logBox.Items.RemoveAt(0);
            logBox.TopIndex = Math.Max(0, logBox.Items.Count - 1);
        }

        static string Stamp { get { return DateTime.Now.ToString("HH:mm:ss") + " "; } }

        void SlowTick()
        {
            ShowStages();
            if (mem != null && !mem.Alive)
            {
                tick.Stop();
                // a normal exit is code 0; an exception code (0xC0000005 access violation and the like) is a crash
                uint code = 0; bool known = false;
                try { code = unchecked((uint)mem.Process.ExitCode); known = true; } catch (Exception) { }
                string where = engine != null ? " at " + engine.Where : "";
                mem.Dispose();
                mem = null;
                engine = null;
                track = null; // the next engine gets its track's settings (and track.ini) even on the same track
                Show(Stamp + (known && code >= 0xC0000000 ? string.Format("NR2003 CRASHED{0} (exception 0x{1:X8})", where, code)
                    : "NR2003 closed" + (known ? " (exit code " + code + ")" : "")));
            }
            if (mem == null)
            {
                try { mem = ProcessMemory.Attach(); attachError = null; }
                catch (GameMemoryException ex)
                {
                    if (attachError != ex.Message) Show(Stamp + ex.Message);
                    attachError = ex.Message;
                }
                if (mem != null)
                {
                    var attached = mem;
                    engine = new RaceEngine(mem) { Logger = Show, GameAlive = () => attached.Alive };
                    engine.GameFolder = () => { try { return Path.GetDirectoryName(attached.Process.MainModule.FileName); } catch (Exception) { return null; } };
                    engine.PlayerLaneNeeded = ShowChooser;
                    engine.PaceReleaseLearned = d =>
                    {
                        settings["PaceRelease." + TrackName] = d.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                        SaveSettings();
                    };
                    engine.PaceExitLearned = text =>
                    {
                        settings["PaceExit." + TrackName] = text;
                        SaveSettings();
                    };
                    engine.DoubleFileAtLearned = d =>
                    {
                        settings["DoubleFileAt." + TrackName] = d.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                        SaveSettings();
                    };
                    engine.PitEntryLearned = d =>
                    {
                        settings["PitEntry." + TrackName] = d.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                        SaveSettings();
                    };
                    ApplyOptions();
                    Show(Stamp + "attached to NR2003 (process " + mem.Process.Id + ")");
                    tick.Start();
                }
            }
            bool bc = Process.GetProcessesByName("bc").Length > 0 || Process.GetProcessesByName("bc_wave").Length > 0;
            if (bc && !bcWarned) Show(Stamp + "Better Cautions is running: fine with all its options unchecked (for example for a stage racing companion app's stage cautions); with any of them on it fights Race Control over the line");
            bcWarned = bc;
            if (engine != null && engine.TrackName != null && engine.TrackName != track)
            {
                track = engine.TrackName;
                LoadTrackSettings();
                engine.PitMerges = TrackPitMerges(track);
                officialLen = TrackOfficialLength(track);
                Show(Stamp + "track: " + track);
            }
            UpdateStatus(bc);
        }

        double officialLen;      // the track's official length (track.ini), for lap speeds; 0 = not known

        double TrackOfficialLength(string name)
        {
            try
            {
                string exe = mem != null ? mem.Process.MainModule.FileName : null;
                return exe == null ? 0 : LapFmt.Official(Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(exe), "tracks"), name), "track.ini"));
            }
            catch (Exception) { return 0; }
        }

        // The track's pit lanes from its track.ini in NR2003's tracks folder (empty when not found).
        List<double[]> TrackPitMerges(string name)
        {
            try
            {
                string exe = mem != null ? mem.Process.MainModule.FileName : null;
                string ini = exe == null ? null : Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(exe), "tracks"), name), "track.ini");
                if (ini != null && File.Exists(ini)) return RaceEngine.ReadPitMerges(ini);
            }
            catch (Exception) { }
            return new List<double[]>();
        }

        static readonly string[] FlagNames = { "yellow", "leader took the yellow, pits open", "catching the pace car", "pacing",
            "delayed green", "one to go", "double file", "green" };

        void UpdateStatus(bool bc)
        {
            string s;
            if (mem == null) s = attachError != null ? "Cannot attach to NR2003 - run as administrator" : "Waiting for NR2003...";
            else if (engine == null || !engine.InRace) s = "Attached to NR2003 - waiting for a race";
            else
            {
                int f = engine.Flag;
                s = "Race, lap " + engine.Lap + " - " + (f >= 0 && f < FlagNames.Length ? FlagNames[f] : "flag " + f);
            }
            status.Text = s + (bc ? "   (Better Cautions running: keep its options unchecked)" : "");
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
