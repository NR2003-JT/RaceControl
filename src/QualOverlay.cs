using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace RaceControl
{
    // Live qualifying timing for the car on TV.
    //   Clock: the game's own tick counter [0xBEBDF5] / tick rate (36 a second; 0x5F7C50), the clock the line
    //   crossings are stamped from (0x53B5D0 -> 0x509A20), so "now - crossing" is exact to one tick (28 ms) in any
    //   session and at any time acceleration. (The session clock 0x977A04 and lap distance +8668 don't move outside
    //   a race.)
    //   A timed lap starts at a line crossing made ON TRACK (never the out lap). While it runs we record the car's
    //   world position and its time into the lap. The gap to pole: find the nearest point of pole's recorded lap to
    //   where the car is now, and compare times there - the racing line doesn't matter. If pole's own lap wasn't
    //   seen, the fastest lap anyone did, scaled to pole's time, stands in.
    public class QualTiming
    {
        const long Block0 = 0xACCA30, Cross0 = 0x77F996, Ticks = 0xBEBDF5, RateFlag = 0x755A18, RateVal = 0x7559B4;
        const int Stride = Addr.Ocd, State = 5031, PosX = 6622, VelX = 6634, CrossCar = 2048;
        const uint CrossMask = 0x0FFFFFFF;
        const long FocusPtr = 0xC069E3, Rec0 = 0xC12030; const int RecSize = 5560;
        // Only these states can start/continue a timed lap. PitInLap is deliberately excluded:
        // the line crossing that sends a car in still finishes its timed lap, but the return to pits
        // must never become a fresh live lap.
        static readonly HashSet<uint> OnTrack = new HashSet<uint> { 0x760d2c, 0x760dfc };   // practising / warming up
        public static readonly HashSet<uint> InLap = new HashSet<uint> { 0x760f48, 0x760f78 };

        public class CarT
        {
            public uint LastCross; public int LapStart = -1;
            public List<float> X = new List<float>(), Y = new List<float>(); public List<int> T = new List<int>();
            public float[] BestX, BestY; public int[] BestT; public int BestMs;
            public int LastLapMs; public DateTime LastLapAt = DateTime.MinValue;
            public uint State;
            public int NextIdx;            // crossings recorded so far in this car's row of the crossing table
            public float PX, PY; public bool HavePos;
            public double Path;            // metres driven since the line (for "N% round the lap")
            public int Match = -1;         // where on the reference lap we last found this car
        }
        readonly Dictionary<int, CarT> cars = new Dictionary<int, CarT>();
        public IEnumerable<KeyValuePair<int, CarT>> All { get { return cars; } }
        float[] refX, refY; int[] refT; int refMs;          // the fastest timed lap anyone has done
        public int NowMs;
        public float TrackLen;                               // metres driven on the last completed timed lap

        public void Reset() { cars.Clear(); TrackLen = 0; refX = null; refY = null; refT = null; refMs = 0; }
        public CarT Of(int c) { CarT t; return cars.TryGetValue(c, out t) ? t : null; }

        public static int GameMs(IGameMemory m) { return (int)Math.Round(Addr.GameSeconds(m) * 1000.0); }

        public void Update(IGameMemory m, IEnumerable<int> slots)
        {
            NowMs = GameMs(m);
            foreach (int c in slots)
            {
                CarT t;
                if (!cars.TryGetValue(c, out t)) cars[c] = t = new CarT();
                long b = Block0 + c * Stride;
                t.State = m.U32(b + State);
                float x = m.F32(b + PosX), y = m.F32(b + PosX + 4);
                float ds = t.HavePos ? (float)Math.Sqrt((x - t.PX) * (x - t.PX) + (y - t.PY) * (y - t.PY)) : 0f;
                if (!(ds >= 0 && ds < 2000)) ds = 0;
                t.PX = x; t.PY = y; t.HavePos = true;
                // the car's latest line crossing = the last filled entry of its row. (Not the lap counter: in practice
                // that restarts with every run while the row keeps growing all session.)
                while (t.NextIdx < CrossCar / 4 && m.U32(Cross0 + c * CrossCar + t.NextIdx * 4) != 0) t.NextIdx++;
                while (t.NextIdx > 0 && m.U32(Cross0 + c * CrossCar + (t.NextIdx - 1) * 4) == 0) t.NextIdx--;   // the table was cleared
                uint cross = t.NextIdx > 0 ? m.U32(Cross0 + c * CrossCar + (t.NextIdx - 1) * 4) & CrossMask : 0;
                bool on = OnTrack.Contains(t.State);
                if (cross != 0 && cross != t.LastCross)                    // crossed the line
                {
                    // Before the first timed crossing, Path contains the run from pit exit
                    // to the stripe. Use that as an initial lap-length estimate so interval
                    // releases can work during the first timed lap of the session too.
                    if (TrackLen <= 0 && on && t.LapStart < 0 && t.Path > 100)
                        TrackLen = (float)t.Path;
                    // Finish a lap already in progress even if the AI changed to PitInLap on this
                    // same poll. Only start the next lap when it remains on the racing surface.
                    if (t.LapStart >= 0 && t.LastCross != 0)
                    {
                        int lap = (int)cross - t.LapStart;
                        if (lap > 5000 && lap < 400000)
                        {
                            if (t.Path > 100) TrackLen = (float)t.Path;
                            t.LastLapMs = lap; t.LastLapAt = DateTime.Now;
                            if (t.X.Count > 10)
                            {
                                t.X.Add(x); t.Y.Add(y); t.T.Add(lap);       // close the lap at the line
                                if (t.BestMs == 0 || lap < t.BestMs) { t.BestMs = lap; t.BestX = t.X.ToArray(); t.BestY = t.Y.ToArray(); t.BestT = t.T.ToArray(); }
                                if (refMs == 0 || lap < refMs) { refMs = lap; refX = t.X.ToArray(); refY = t.Y.ToArray(); refT = t.T.ToArray(); }
                            }
                        }
                    }
                    // In single-car qualifying, the first line crossing after leaving the pits
                    // starts the timed lap. Do not require a previously observed crossing: that
                    // made the car stay on OUT LAP for its entire (and often only) timed run.
                    t.LapStart = on ? (int)cross : -1;
                    t.X.Clear(); t.Y.Clear(); t.T.Clear(); t.Path = 0; t.Match = -1;
                    t.LastCross = cross;
                    if (t.LapStart >= 0) { t.X.Add(x); t.Y.Add(y); t.T.Add(Math.Max(0, NowMs - t.LapStart)); }
                    continue;
                }
                if (!on) { t.LapStart = -1; t.X.Clear(); t.Y.Clear(); t.T.Clear(); t.Path = 0; continue; }
                if (t.LapStart < 0) { t.Path += ds; continue; }
                t.Path += ds;
                if (ds > 0.5f && t.X.Count < 30000) { t.X.Add(x); t.Y.Add(y); t.T.Add(NowMs - t.LapStart); }
            }
        }

        // ms into the timed lap so far (-1 = not on one)
        public int Elapsed(int car)
        {
            CarT t = Of(car);
            return t == null || t.LapStart < 0 ? -1 : Math.Max(0, NowMs - t.LapStart);
        }

        // live gap of `car` to pole in ms (+ = slower); false when it isn't on a timed lap or there's no reference lap
        public bool Delta(int car, int pole, int poleMs, out int delta)
        {
            delta = 0;
            CarT t = Of(car);
            if (t == null || t.LapStart < 0 || pole < 0 || poleMs <= 0) return false;
            int el = NowMs - t.LapStart;
            if (el < 1500) return false;                                    // just crossed: too early to say
            float[] X, Y; int[] T; double scale;
            CarT p = Of(pole);
            if (p != null && p.BestX != null && Math.Abs(p.BestMs - poleMs) <= 5) { X = p.BestX; Y = p.BestY; T = p.BestT; scale = 1; }
            else if (refX != null && refMs > 0) { X = refX; Y = refY; T = refT; scale = (double)poleMs / refMs; }
            else return false;
            // nearest point of the reference lap, searched forward from where we found the car last time
            int n = X.Length, from, to;
            if (t.Match < 0) { from = 0; to = Math.Min(n - 1, n / 3); }    // start of a lap: the first third
            else { from = Math.Max(0, t.Match - 5); to = Math.Min(n - 1, t.Match + 80); }
            int best = -1; double bd = double.MaxValue;
            for (int i = from; i <= to; i++)
            {
                double dx = X[i] - t.PX, dy = Y[i] - t.PY, d2 = dx * dx + dy * dy;
                if (d2 < bd) { bd = d2; best = i; }
            }
            if (best < 0 || bd > 60 * 60) { t.Match = -1; return false; }   // not near the reference line
            t.Match = best;
            // between reference points: project the car onto the segment ahead of the nearest point (or the one behind)
            int i0 = best, i1 = best; double f = 0;
            if (best + 1 < n) { double fa = Proj(X, Y, best, best + 1, t.PX, t.PY); if (fa >= 0) { i1 = best + 1; f = Math.Min(1, fa); } }
            if (i0 == i1 && best > 0) { i0 = best - 1; f = Math.Max(0, Math.Min(1, Proj(X, Y, best - 1, best, t.PX, t.PY))); }
            double refAt = (T[i0] + f * (T[i1] - T[i0])) * scale;
            delta = (int)Math.Round(el - refAt);
            return true;
        }

        // where (x, y) falls along the segment i -> j, 0 at i and 1 at j
        static double Proj(float[] X, float[] Y, int i, int j, float x, float y)
        {
            double sx = X[j] - X[i], sy = Y[j] - Y[i], l2 = sx * sx + sy * sy;
            if (l2 < 1e-6) return 0;
            return ((x - X[i]) * sx + (y - Y[i]) * sy) / l2;
        }

        // the car the TV camera is on (-1 = not known)
        public static int Focus(IGameMemory m)
        {
            long p = m.U32(FocusPtr);
            if (p < Rec0 || (p - Rec0) % RecSize != 0) return -1;
            long i = (p - Rec0) / RecSize;
            return i >= 0 && i < 44 ? (int)i : -1;
        }
    }

    // What the timing box shows (filled in by MainForm from Qualifying + QualTiming).
    public class QualBoxData
    {
        public string Header = "QUALIFYING";
        public int PoleNum = -1; public string PoleName = "", PoleTime = "";
        public int CarNum = -1; public string CarName = "", CarPos = "--";
        public string Right = "";              // "-0.041", "26.502", "OUT LAP"
        public string Projected = "";          // expected finish (live) or the lap just done, shown left of the gap box
        public int Mode;                       // 0 text (grey), 1 lap time (white), 2 delta (green / red by sign)
        public bool Faster;
    }

    // The timing box, drawn like a TV graphic: header, pole on top, the car on TV below with its live delta.
    public class QualBoxForm : OverlayForm
    {
        public const int W = 420, H = 100;
        public QualBoxData Data = new QualBoxData();
        readonly ConeArt art;
        readonly string family;
        readonly BoardLayout layout;
        readonly Rectangle game;
        readonly bool edit;
        float scale = 1f;
        Point dragFrom;
        public event Action<BoardLayout> Saved;
        public bool Finished { get; private set; }

        static readonly Color Bg = Color.FromArgb(20, 22, 26), Edge = Color.FromArgb(92, 96, 104), Gold = Color.FromArgb(255, 196, 40),
            Grey = Color.FromArgb(170, 176, 190), Green = Color.FromArgb(40, 200, 90), Red = Color.FromArgb(225, 50, 45);

        public QualBoxForm(ConeArt art, Rectangle game, BoardLayout layout, bool edit = false) : base(!edit)
        {
            this.art = art; this.game = game; this.layout = layout.Copy(); this.edit = edit;
            var names = new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            family = names.Contains("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
            ApplyScale();
            // default spot: bottom left of the game picture
            if (double.IsNaN(this.layout.X)) this.layout.X = 0.02;
            if (double.IsNaN(this.layout.Y)) this.layout.Y = 0.80;
            Location = this.layout.Place(game, Size);
            Opacity = 0.97;
            if (edit)
            {
                Data = Sample();
                KeyPreview = true;
                Cursor = Cursors.SizeAll;
                MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) dragFrom = e.Location; };
                MouseMove += (o, e) => { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y); };
                MouseWheel += (o, e) =>
                {
                    this.layout.Scale = Math.Max(0.4, Math.Min(2.5, this.layout.Scale + (e.Delta > 0 ? 0.05 : -0.05)));
                    ApplyScale(); Invalidate();
                };
                DoubleClick += (o, e) => Save();
                KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) Save(); else if (e.KeyCode == Keys.Escape) Finish(); };
                // clicking back into the game saves it too: left up, it hid the live overlay for good (beta tester 2026-10-27)
                Deactivate += (o, e) => { if (!Finished && !IsDisposed) BeginInvoke((Action)(() => { if (!Finished && !IsDisposed) Save(); })); };
            }
        }

        public static QualBoxData Sample()
        {
            return new QualBoxData { Header = "ROUND 2  -  4:12", PoleNum = 54, PoleName = "GIBBS", PoleTime = "26.414",
                CarNum = 77, CarName = "HOCEVAR", CarPos = "--", Projected = "26.373", Right = "-0.041", Mode = 2, Faster = true };
        }

        void ApplyScale()
        {
            scale = (float)Math.Max(0.3, Math.Min(3.0, game.Height / 1080.0 * 1.2 * layout.Scale));
            Size = new Size((int)(W * scale), (int)(H * scale));
        }

        void Save()
        {
            layout.Remember(game, Location, Size);
            if (Saved != null) Saved(layout);
            Finish();
        }

        public void Finish() { Finished = true; Close(); }

        Font F(float px, FontStyle st) { return new Font(family, px, st, GraphicsUnit.Pixel); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.ScaleTransform(scale, scale);
            var r = new Rectangle(0, 0, W, H);
            using (var b = new LinearGradientBrush(r, Color.FromArgb(34, 37, 43), Bg, 90f)) g.FillRectangle(b, r);
            using (var p = new Pen(Edge, 2)) g.DrawRectangle(p, 1, 1, W - 2, H - 2);
            var d = Data ?? new QualBoxData();
            using (var f = F(14, FontStyle.Bold)) using (var br = new SolidBrush(Gold)) g.DrawString(d.Header, f, br, 12, 6);
            if (d.PoleNum >= 0) Row(g, 28, "1.", d.PoleNum, d.PoleName, d.PoleTime, 1, false);
            else using (var f = F(19, FontStyle.Bold)) using (var br = new SolidBrush(Grey)) g.DrawString("NO TIME YET", f, br, 12, 30);
            if (d.CarNum >= 0)
            {
                Row(g, 62, d.CarPos, d.CarNum, d.CarName, d.Right, d.Mode, d.Faster);
                if (!string.IsNullOrEmpty(d.Projected))
                    using (var f = F(21, FontStyle.Bold))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                        g.DrawString(d.Projected, f, Brushes.White, new RectangleF(W - 200, 62, 84, 30), sf);
            }
        }

        void Row(Graphics g, float y, string pos, int num, string name, string right, int mode, bool faster)
        {
            using (var f = F(21, FontStyle.Bold)) g.DrawString(pos, f, Brushes.White, 12, y);
            Image card = art != null ? art.Number(num) : null;
            var box = new RectangleF(52, y + 2, 54, 28);
            if (card != null)
            {
                float k = Math.Min(box.Width / card.Width, box.Height / card.Height);
                g.DrawImage(card, box.X + (box.Width - card.Width * k) / 2, box.Y + (box.Height - card.Height * k) / 2, card.Width * k, card.Height * k);
            }
            else using (var f = F(21, FontStyle.Bold)) using (var br = new SolidBrush(Gold)) g.DrawString("#" + Qualifying.Label(num), f, br, 52, y);
            using (var f = F(21, FontStyle.Regular))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(name, f, Brushes.White, new RectangleF(114, y, mode == 2 ? 110 : 190, 30), sf);
            if (string.IsNullOrEmpty(right)) return;
            var rr = new RectangleF(W - 108, y, 96, 30);
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                if (mode == 2)
                {
                    using (var bb = new SolidBrush(faster ? Green : Red)) g.FillRectangle(bb, rr);
                    using (var f = F(21, FontStyle.Bold)) g.DrawString(right, f, Brushes.White, new RectangleF(rr.X, rr.Y, rr.Width - 6, rr.Height), sf);
                }
                else using (var f = F(mode == 1 ? 21 : 16, FontStyle.Bold)) using (var br = new SolidBrush(mode == 1 ? Color.White : Grey))
                    g.DrawString(right, f, br, new RectangleF(rr.X, rr.Y, rr.Width - 6, rr.Height), sf);
            }
        }
    }

    // What the broadcast style overlay shows (filled in by MainForm).
    public class QualBroadcastData
    {
        public string Track = "", Session = "QUALIFYING";
        public int Num = -1; public string First = "", Last = "";
        public bool Tele; public float Mph, Rpm, RpmTop = 9000, Throttle, Brake = -1; public int Gear;   // Brake < 0: not known
        public int LapNo, LapsTotal;                 // LapNo 0 = out lap, LapsTotal + 1 = run done
        public string Current = "", LastLap = "", BestLap = "";
        public List<string> LapRows = new List<string>();
        public bool Ahead = true;                    // on provisional pole pace: lime accent, else red
        public List<string[]> Order = new List<string[]>();   // number, first name, last name, time - fastest first
        public bool ShowOrder = true;

        // everything drawn, as text: the overlay is repainted only when this changes (speed / rpm to the shown precision)
        public string Key()
        {
            return string.Join("|", new[] { Track, Num.ToString(), Last, Tele ? ((int)Math.Round(Mph)) + "," + ((int)Math.Round(Rpm)) + "," +
                ((int)Math.Round(Throttle * 100)) + "," + ((int)Math.Round(Brake * 100)) + "," + Gear : "-", LapNo + "/" + LapsTotal, Current, LastLap, BestLap,
                string.Join(",", LapRows.ToArray()), Ahead ? "1" : "0", string.Join(",", Order.Select(o => o[2] + o[3] + (o.Length > 4 ? o[4] : "")).ToArray()) });
        }
    }

    // The broadcast style qualifying overlay, from the Figma frames (art\Qualifying telemetry overlay.svg, Frame 2.svg),
    // 528 x 706 design units: driver panel, live telemetry and lap timing on the left (280 wide), the running order on
    // the right (248 wide), which slides out behind the left column and back (0.45 s ease-in-out).
    public class QualBroadcastForm : OverlayForm
    {
        public const int W = 528, H = 706;
        public QualBroadcastData Data = new QualBroadcastData();
        readonly ConeArt art;
        readonly string family, labelFamily;
        readonly BoardLayout layout;
        readonly Rectangle game;
        float scale = 1f;
        double orderX = 0;                            // 0 = shown, 1 = tucked behind the left column
        DateTime lastAnim = DateTime.Now;
        public bool Finished { get; private set; }
        readonly bool edit;

        static readonly Color Key = Color.FromArgb(1, 2, 3);     // see-through
        static readonly Color Panel = Color.FromArgb(31, 34, 38), OrderPanel = Color.FromArgb(40, 43, 47), Divider = Color.FromArgb(52, 58, 62), Label = Color.FromArgb(167, 175, 180),
            Value = Color.FromArgb(246, 247, 244), Lime = Color.FromArgb(222, 250, 64), Red = Color.FromArgb(255, 56, 60), Track = Color.FromArgb(53, 59, 64);

        public event Action<BoardLayout> Saved;
        Point dragFrom;

        // edit: the copy you place - drag it, the mouse wheel sizes it, Enter or double-click saves, Esc cancels
        public QualBroadcastForm(ConeArt art, Rectangle game, BoardLayout layout, bool edit = false) : base(!edit)
        {
            this.art = art; this.game = game; this.layout = layout.Copy(); this.edit = edit;
            var names = new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            family = names.Contains("Bahnschrift SemiBold Condensed") ? "Bahnschrift SemiBold Condensed" : names.Contains("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
            labelFamily = names.Contains("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
            BackColor = Key; TransparencyKey = Key;
            ApplyScale();
            if (double.IsNaN(this.layout.X)) this.layout.X = 0.02;
            if (double.IsNaN(this.layout.Y)) this.layout.Y = 0.10;
            Location = this.layout.Place(game, Size);
            Opacity = 0.9;
            if (edit)
            {
                Data = Sample();
                KeyPreview = true;
                Cursor = Cursors.SizeAll;
                MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) dragFrom = e.Location; };
                MouseMove += (o, e) => { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y); };
                MouseWheel += (o, e) =>
                {
                    this.layout.Scale = Math.Max(0.4, Math.Min(2.5, this.layout.Scale + (e.Delta > 0 ? 0.05 : -0.05)));
                    ApplyScale(); Invalidate();
                };
                DoubleClick += (o, e) => Save();
                KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) Save(); else if (e.KeyCode == Keys.Escape) Finish(); };
                // clicking back into the game saves it too: left up, it hid the live overlay for good (beta tester 2026-10-27)
                Deactivate += (o, e) => { if (!Finished && !IsDisposed) BeginInvoke((Action)(() => { if (!Finished && !IsDisposed) Save(); })); };
            }
        }

        void ApplyScale()
        {
            scale = (float)Math.Max(0.3, Math.Min(3.0, game.Height / 1080.0 * 1.0 * layout.Scale));
            Size = new Size((int)(W * scale), (int)(H * scale));
        }

        void Save()
        {
            layout.Remember(game, Location, Size);
            if (Saved != null) Saved(layout);
            Finish();
        }

        public static QualBroadcastData Sample()
        {
            var d = new QualBroadcastData { Track = "INDIANAPOLIS", Num = 10, First = "ALEX", Last = "PALOU", Tele = true, Mph = 234, Rpm = 11940, RpmTop = 12500,
                Throttle = 1, Brake = 0, Gear = 6, LapNo = 3, LapsTotal = 4, Current = "24.176", LastLap = "38.402", BestLap = "38.402" };
            d.LapRows.AddRange(new[] { "38.402", "38.511" });
            d.Order.Add(new[] { "1", "ALEX", "PALOU", "38.402", "f" });
            d.Order.Add(new[] { "2", "JOSEF", "NEWGARDEN", "38.512", "f" });
            d.Order.Add(new[] { "", "PATO", "O'WARD", "24.176", "l" });
            d.Order.Add(new[] { "", "COLTON", "HERTA", "", "u" });
            return d;
        }

        public void Finish() { Finished = true; Close(); }

        // the running order slides with an ease-in-out over 0.45 s towards where Data.ShowOrder wants it
        // moves the running order towards shown / tucked; true while it is moving (the overlay needs a repaint)
        public bool Animate(bool show)
        {
            double dt = Math.Min(0.2, (DateTime.Now - lastAnim).TotalSeconds); lastAnim = DateTime.Now;
            double target = show ? 0 : 1, step = dt / 0.45, was = orderX;
            orderX = target > orderX ? Math.Min(target, orderX + step) : Math.Max(target, orderX - step);
            return orderX != was;
        }

        Font F(float px, FontStyle st) { return new Font(family, px, st, GraphicsUnit.Pixel); }
        static RectangleF R(float x, float y, float w, float h) { return new RectangleF(x, y, w, h); }
        void T(Graphics g, string s, float px, FontStyle st, Color c, RectangleF r, StringAlignment al = StringAlignment.Near, StringAlignment va = StringAlignment.Near)
        {
            using (var f = F(px, st)) using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = al, LineAlignment = va, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(s ?? "", f, b, r, sf);
        }
        // labels: the normal width face (the values use the condensed one)
        void L(Graphics g, string s, float px, RectangleF r, StringAlignment al = StringAlignment.Near) { L(g, s, px, Label, r, al); }
        void L(Graphics g, string s, float px, Color c, RectangleF r, StringAlignment al = StringAlignment.Near)
        {
            using (var f = new Font(labelFamily, px, FontStyle.Bold, GraphicsUnit.Pixel)) using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = al, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(s ?? "", f, b, r, sf);
        }
        static void Fill(Graphics g, Color c, float x, float y, float w, float h) { using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, h); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Key);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(scale, scale);
            var d = Data ?? new QualBroadcastData();
            Color acc = d.Ahead ? Lime : Red;

            // the running order first: it slides out to the left, behind the left column
            float ox = 280 - (float)(Ease(orderX) * 248);
            if (orderX < 1)
            {
                var clip = g.Clip; g.SetClip(new RectangleF(280, 0, 248, H));
                Fill(g, OrderPanel, ox, 16, 248, 674);
                Fill(g, acc, ox, 16, 248, 3);
                L(g, "RUNNING ORDER", 12, R(ox + 16, 32, 216, 14));
                T(g, d.Session, 23, FontStyle.Bold, Value, R(ox + 16, 44, 216, 30));
                Fill(g, Divider, ox, 80, 248, 1);
                for (int i = 0; i < Math.Min(10, d.Order.Count); i++)
                {
                    // o: badge (rank), first, last, time, kind - f ranked, l on its run (the accent), u still to go (dimmed)
                    var o = d.Order[i]; float y = 90 + i * 46;
                    string kind = o.Length > 4 ? o[4] : "f";
                    Color name = kind == "l" ? acc : kind == "u" ? Label : Value;
                    if (kind == "u") using (var pen = new Pen(Label, 1.2f)) g.DrawRectangle(pen, ox + 12.5f, y + 4.5f, 19, 19);
                    else Fill(g, kind == "l" ? acc : Value, ox + 12, y + 4, 20, 20);
                    T(g, o[0], 11, FontStyle.Bold, Panel, R(ox + 12, y + 4, 20, 20), StringAlignment.Center, StringAlignment.Center);
                    T(g, o[2], 19, FontStyle.Bold, name, R(ox + 40, y - 3, 130, 24));
                    L(g, o[1], 9, R(ox + 40, y + 19, 130, 11));
                    T(g, o[3], kind == "f" && o[3] == "NO TIME" ? 12 : 19, FontStyle.Bold, kind == "l" ? acc : Value, R(ox + 140, y + 1, 96, 24), StringAlignment.Far);
                    if (i < 9) Fill(g, Divider, ox + 12, y + 35, 224, 1);
                }
                g.Clip = clip;
            }

            // driver panel
            Fill(g, Panel, 0, 0, 280, 90);
            Fill(g, acc, 0, 0, 280, 3);
            L(g, d.Track, 10, Value, R(16, 12, 150, 12));
            L(g, d.Session, 10, R(130, 12, 134, 12), StringAlignment.Far);
            if (d.Num >= 0)
            {
                Fill(g, acc, 16, 36, 38, 34);
                T(g, Qualifying.Label(d.Num), 24, FontStyle.Bold, Panel, R(16, 36, 38, 34), StringAlignment.Center, StringAlignment.Center);
                L(g, d.First, 11, R(66, 33, 200, 13));
                T(g, d.Last, 28, FontStyle.Bold, Value, R(64, 48, 210, 34));
            }

            // live telemetry
            Fill(g, Panel, 0, 92, 280, 280);
            if (d.Tele)
            {
                T(g, Math.Max(0, (int)Math.Round(d.Mph)).ToString(), 88, FontStyle.Bold, Value, R(10, 96, 180, 92));
                L(g, "SPEED / MPH", 11, R(16, 186, 150, 12));
                Fill(g, Divider, 190, 104, 1, 72);
                T(g, d.Gear > 0 ? d.Gear.ToString() : d.Gear == 0 ? "N" : "R", 76, FontStyle.Bold, acc, R(190, 100, 90, 80), StringAlignment.Center);
                L(g, "GEAR", 11, R(190, 182, 90, 12), StringAlignment.Center);
                L(g, "RPM", 11, R(16, 228, 100, 12));
                T(g, ((int)Math.Round(d.Rpm)).ToString("N0"), 24, FontStyle.Bold, Value, R(130, 214, 134, 30), StringAlignment.Far);
                int lit = (int)Math.Round(Math.Max(0, Math.Min(1, d.Rpm / Math.Max(1f, d.RpmTop))) * 20);
                for (int i = 0; i < 20; i++) Fill(g, i < lit ? acc : Track, 16 + i * 12.55f, 246, 9.55f, 10);
                Bar(g, "THROTTLE", d.Throttle, 280, acc);
                Bar(g, "BRAKE", d.Brake, 329, acc);
            }

            // lap timing
            Fill(g, Panel, 0, 374, 280, 332);
            L(g, "LAP", 11, R(16, 398, 100, 12));
            string lapTxt = d.LapsTotal <= 0 ? "" : d.LapNo <= 0 ? "OUT LAP" : d.LapNo > d.LapsTotal ? "IN LAP" : d.LapNo + " / " + d.LapsTotal;
            T(g, lapTxt, 24, FontStyle.Bold, Value, R(110, 386, 154, 30), StringAlignment.Far);
            Fill(g, Divider, 16, 424, 248, 1);
            Row(g, "CURRENT", d.Current, 442, acc);
            Row(g, "LAST LAP", d.LastLap, 476, Value);
            Row(g, "BEST LAP", d.BestLap, 510, Value);
            for (int i = 0; i < Math.Min(4, d.LapRows.Count); i++) Row(g, "LAP " + (i + 1), d.LapRows[i], 560 + i * 30, Value);
            if (edit)                                 // the copy being placed says so (and how to finish)
            {
                g.ResetTransform();
                using (var f = new Font(labelFamily, Math.Max(11f, 12 * scale), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bg = new SolidBrush(Color.FromArgb(16, 18, 22)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    var r = new RectangleF(0, Height - f.Height * 3.2f, Width, f.Height * 3.2f);
                    g.FillRectangle(bg, r);
                    g.DrawString("PLACING: drag to move, mouse wheel to size\nEnter, double-click or click away saves, Esc cancels", f, Brushes.Gainsboro, r, sf);
                }
            }
        }

        void Bar(Graphics g, string label, float v, float y, Color acc)
        {
            L(g, label, 11, R(16, y + 4, 120, 12));
            T(g, v < 0 ? "--" : ((int)Math.Round(v * 100)) + "%", 24, FontStyle.Bold, Value, R(130, y - 8, 134, 30), StringAlignment.Far);
            Fill(g, Track, 16, y + 24, 248, 7);
            if (v > 0) Fill(g, acc, 16, y + 24, 248 * Math.Min(1, v), 7);
        }

        void Row(Graphics g, string label, string value, float y, Color c)
        {
            L(g, label, 11, R(16, y + 8, 120, 12));
            T(g, value, 24, FontStyle.Bold, c, R(110, y - 2, 154, 30), StringAlignment.Far);
        }

        static double Ease(double t) { return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2; }
    }

    // Lap time text for the overlays and the qualifying lists: m:ss.fff from a minute up (1:10.234, not 70.234), and
    // the option NR2003 has itself - lap speeds in mph (the track's official length / the lap time) instead of times.
    public static class LapFmt
    {
        public static bool Speed;
        public static double TrackM;
        // The official length, track.ini "track_length = 2.000m" (miles; "k" = km), as NR2003 and its telemetry give it.
        // The length the game measures (Addr.TrackLength) is the centre line - 3216 m at Michigan against 3218.69 m
        // (2.000 mi) - and speeds from it read a little low (beta tester: "close but not exact"). 0 = no track.ini.
        public static double Official(string trackIni)
        {
            try
            {
                foreach (string raw in System.IO.File.ReadAllLines(trackIni))
                {
                    string l = raw.Split(';')[0].Trim();
                    int eq = l.IndexOf('=');
                    if (eq < 0 || !string.Equals(l.Substring(0, eq).Trim(), "track_length", StringComparison.OrdinalIgnoreCase)) continue;
                    var mt = System.Text.RegularExpressions.Regex.Match(l.Substring(eq + 1), @"^\s*([0-9.]+)\s*([a-zA-Z]?)");
                    double x;
                    if (!mt.Success || !double.TryParse(mt.Groups[1].Value, System.Globalization.NumberStyles.Float, Inv, out x)) return 0;
                    double len = mt.Groups[2].Value.ToLowerInvariant() == "k" ? x * 1000 : x * 1609.344;
                    return len > 100 && len < 50000 ? len : 0;
                }
            }
            catch (Exception) { }
            return 0;
        }
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        public static string Time(int ms)
        {
            if (ms <= 0) return "-.---";
            long t = ms; int min = (int)(t / 60000); double sec = (t - min * 60000L) / 1000.0;
            return min > 0 ? min + ":" + sec.ToString("00.000", Inv) : sec.ToString("0.000", Inv);
        }
        public static string Lap(int ms)
        {
            if (ms > 0 && Speed && TrackM > 100) return (TrackM / (ms / 1000.0) * 2.2369363).ToString("0.000", Inv);
            return Time(ms);
        }
    }
}
