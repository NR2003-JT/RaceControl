using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RaceControl
{
    // What the live telemetry strip shows (filled in by MainForm from the TeleRecorder).
    public class TeleStripData
    {
        public int Num = -1; public string Last = "";
        public bool Indy;                                   // the formula wheel (Indy formats) or the round one
        public double Now;                                  // game seconds
        public List<TeleSample> Recent = new List<TeleSample>();   // D = game seconds
        public float Thr, Brk, Steer;                       // Steer: front wheel degrees, + = left
        public bool Have;
    }

    // The small in-game telemetry strip: the car on TV's throttle and brake as running lines (up on the pedal, down on
    // release) over the last few seconds, and a steering wheel that turns with the car's steering. 420 x 132 design units,
    // the same colours as the broadcast overlay. Placed like the timing box (drag, mouse wheel sizes, Enter saves).
    public class TeleStripForm : OverlayForm
    {
        public const int W = 420, H = 132;
        const double Window = 6;                            // seconds of history across the chart
        public TeleStripData Data = new TeleStripData();
        readonly BoardLayout layout;
        readonly Rectangle game;
        readonly string family, labelFamily;
        readonly bool edit;
        float scale = 1f;
        public bool Finished { get; private set; }
        public event Action<BoardLayout> Saved;
        Point dragFrom;

        static readonly Color Key = Color.FromArgb(1, 2, 3);
        static readonly Color Panel = Color.FromArgb(31, 34, 38), Divider = Color.FromArgb(52, 58, 62), Label = Color.FromArgb(167, 175, 180),
            Value = Color.FromArgb(246, 247, 244), Lime = Color.FromArgb(222, 250, 64), Gas = Color.FromArgb(64, 224, 120), Stop = Color.FromArgb(255, 56, 60),
            Grid = Color.FromArgb(44, 49, 54);
        static Image wheelRound, wheelFormula;

        public static Image Wheel(bool indy)
        {
            if (wheelRound == null)
            {
                wheelRound = Res("RaceControl.wheel_nascar.png");
                wheelFormula = Res("RaceControl.wheel_indy.png");
            }
            return indy ? wheelFormula : wheelRound;
        }
        static Image Res(string name)
        {
            try { using (Stream s = typeof(TeleStripForm).Assembly.GetManifestResourceStream(name)) return s == null ? null : new Bitmap(Image.FromStream(s)); }
            catch { return null; }
        }

        public TeleStripForm(Rectangle game, BoardLayout layout, bool edit = false) : base(!edit)
        {
            this.game = game; this.layout = layout.Copy(); this.edit = edit;
            var names = new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            family = names.Contains("Bahnschrift SemiBold Condensed") ? "Bahnschrift SemiBold Condensed" : names.Contains("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
            labelFamily = names.Contains("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
            BackColor = Key; TransparencyKey = Key;
            ApplyScale();
            if (double.IsNaN(this.layout.X)) this.layout.X = 0.02;
            if (double.IsNaN(this.layout.Y)) this.layout.Y = 0.80;
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
                    this.layout.Scale = Math.Max(0.4, Math.Min(3.0, this.layout.Scale + (e.Delta > 0 ? 0.05 : -0.05)));
                    ApplyScale(); Invalidate();
                };
                DoubleClick += (o, e) => Save();
                KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) Save(); else if (e.KeyCode == Keys.Escape) Finish(); };
                Deactivate += (o, e) => { if (!Finished && !IsDisposed) BeginInvoke((Action)(() => { if (!Finished && !IsDisposed) Save(); })); };
            }
        }

        void ApplyScale()
        {
            scale = (float)Math.Max(0.3, Math.Min(4.0, game.Height / 1080.0 * layout.Scale));
            Size = new Size((int)(W * scale), (int)(H * scale));
        }

        void Save()
        {
            layout.Remember(game, Location, Size);
            if (Saved != null) Saved(layout);
            Finish();
        }

        public void Finish() { Finished = true; Close(); }

        // a made-up braking zone and corner exit, for placing
        public static TeleStripData Sample()
        {
            var d = new TeleStripData { Num = 10, Last = "PALOU", Indy = false, Now = 100, Have = true };
            for (int i = 0; i <= 180; i++)
            {
                double t = 100 - Window + i * Window / 180, u = i / 180.0;
                float thr = u < 0.3 ? 1 : u < 0.36 ? (float)(1 - (u - 0.3) / 0.06) : u < 0.62 ? 0 : (float)Math.Min(1, (u - 0.62) / 0.2);
                float brk = u < 0.33 || u > 0.6 ? 0 : (float)Math.Min(0.8, Math.Sin((u - 0.33) / 0.27 * Math.PI) * 0.9);
                d.Recent.Add(new TeleSample { D = (float)t, Thr = thr, Brk = brk, Steer = (float)(u > 0.45 ? 3.5 * Math.Sin((u - 0.45) / 0.55 * Math.PI) : 0) });
            }
            var last = d.Recent[d.Recent.Count - 1];
            d.Thr = last.Thr; d.Brk = last.Brk; d.Steer = 2.4f;
            return d;
        }

        Font F(float px, FontStyle st) { return new Font(family, px, st, GraphicsUnit.Pixel); }
        static RectangleF R(float x, float y, float w, float h) { return new RectangleF(x, y, w, h); }
        void T(Graphics g, string s, float px, Color c, RectangleF r, StringAlignment al = StringAlignment.Near)
        {
            using (var f = F(px, FontStyle.Bold)) using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = al, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(s ?? "", f, b, r, sf);
        }
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
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.ScaleTransform(scale, scale);
            var d = Data ?? new TeleStripData();

            Fill(g, Panel, 0, 0, W, H);
            Fill(g, Lime, 0, 0, W, 3);

            // the wheel, turned like the car's (front wheel angle x the steering ratio), with the angle under it
            const float cx = 62, cy = 62, size = 84;
            float wheelDeg = Math.Max(-270, Math.Min(270, d.Steer * TeleRecorder.SteerRatio));
            var img = Wheel(d.Indy);
            var st = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-wheelDeg);                   // + = left = anticlockwise
            float top = -size / 2;                          // the wheel's top edge (the formula wheel is wider than tall)
            if (img != null)
            {
                // the PNGs are 256 square with the wheel centred; the formula wheel fills the width only
                bool wide = d.Indy;
                float iw = size, ih = size * img.Height / Math.Max(1f, img.Width);
                g.DrawImage(img, -iw / 2, -ih / 2, iw, ih);
                if (wide) top = -size / 2 * (898f / 1538f);  // its rim height / width
            }
            else using (var p = new Pen(Value, 6)) { g.DrawEllipse(p, -size / 2 + 3, -size / 2 + 3, size - 6, size - 6); g.DrawLine(p, -size / 2 + 6, 0, size / 2 - 6, 0); }
            using (var p = new Pen(Lime, 4)) g.DrawLine(p, 0, top - 2, 0, top + 8);   // top-centre mark
            g.Restore(st);
            int shown = (int)Math.Round(Math.Abs(wheelDeg));
            L(g, !d.Have ? "--" : shown < 1 ? "0°" : (wheelDeg > 0 ? "L " : "R ") + shown + "°", 11, Label, R(12, 110, 100, 14), StringAlignment.Center);

            // the running lines: throttle (green) and brake (red), newest on the right
            const float gx = 124, gy = 30, gw = 210, gh = 82;
            Fill(g, Color.FromArgb(25, 28, 31), gx, gy, gw, gh);
            using (var p = new Pen(Grid, 1)) { g.DrawLine(p, gx, gy + gh / 2, gx + gw, gy + gh / 2); g.DrawRectangle(p, gx, gy, gw, gh); }
            if (d.Recent.Count > 1)
            {
                var clip = g.Clip; g.SetClip(R(gx, gy, gw, gh));
                Line(g, d, s => s.Brk, Stop, gx, gy, gw, gh);
                Line(g, d, s => s.Thr, Gas, gx, gy, gw, gh);
                g.Clip = clip;
            }
            L(g, d.Num >= 0 ? "#" + Qualifying.Label(d.Num) + "  " + d.Last : "LIVE TELEMETRY", 11, Value, R(gx, 12, gw, 14));

            // the values
            Fill(g, Divider, 344, 14, 1, 104);
            L(g, "THROTTLE", 10, Label, R(352, 14, 64, 12));
            T(g, d.Have ? (int)Math.Round(d.Thr * 100) + "%" : "--", 26, Gas, R(348, 26, 68, 30), StringAlignment.Far);
            L(g, "BRAKE", 10, Label, R(352, 66, 64, 12));
            T(g, d.Have ? (int)Math.Round(d.Brk * 100) + "%" : "--", 26, Stop, R(348, 78, 68, 30), StringAlignment.Far);

            if (edit)
            {
                g.ResetTransform();
                using (var f = new Font(labelFamily, Math.Max(10f, 11 * scale), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bg = new SolidBrush(Color.FromArgb(16, 18, 22)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    var r = new RectangleF(0, Height - f.Height * 2.4f, Width, f.Height * 2.4f);
                    g.FillRectangle(bg, r);
                    g.DrawString("PLACING: drag, mouse wheel sizes, Enter / double-click / click away saves, Esc cancels", f, Brushes.Gainsboro, r, sf);
                }
            }
        }

        // one input as a line with a soft fill under it
        void Line(Graphics g, TeleStripData d, Func<TeleSample, float> v, Color c, float gx, float gy, float gw, float gh)
        {
            var pts = new List<PointF>();
            foreach (var s in d.Recent)
            {
                double age = d.Now - s.D;
                if (age > Window + 0.2) continue;
                float x = gx + gw * (float)(1 - age / Window), y = gy + gh - 3 - (gh - 6) * Math.Max(0, Math.Min(1, v(s)));
                pts.Add(new PointF(x, y));
            }
            if (pts.Count < 2) return;
            var poly = new List<PointF>(pts) { new PointF(pts[pts.Count - 1].X, gy + gh), new PointF(pts[0].X, gy + gh) };
            using (var b = new SolidBrush(Color.FromArgb(46, c))) g.FillPolygon(b, poly.ToArray());
            using (var p = new Pen(c, 2.2f) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts.ToArray());
        }
    }
}
