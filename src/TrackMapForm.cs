using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RaceControl
{
    // The cone map (Restarts tab, "Track map..."): the track drawn from its TrackMap, with the start/finish line, pit
    // entry, pit exit, where the field goes double file and the cone marked, and the cars live. Click on the track to put
    // the cone there: the nearest centre line point's lap distance goes into the cone box.
    public class TrackMapForm : Form
    {
        public class Marks
        {
            public double Cone = double.NaN, PitEntry = double.NaN, PitExit = double.NaN, DoubleFile = double.NaN;
        }

        readonly Func<TrackMap> getMap;
        readonly Func<Marks> getMarks;
        readonly Func<List<PointF>> getCars;      // world x/y of the cars on track (the pace car first, NaN when not out)
        readonly Func<string> getStatus;
        readonly Action<double> setCone;
        readonly Action recordAgain;
        readonly Panel canvas;
        readonly Label info;
        readonly Timer refresh;
        bool dark;
        double hover = double.NaN;

        public TrackMapForm(Func<TrackMap> map, Func<Marks> marks, Func<List<PointF>> cars, Func<string> status,
                            Action<double> cone, Action again, bool darkLook)
        {
            getMap = map; getMarks = marks; getCars = cars; getStatus = status; setCone = cone; recordAgain = again; dark = darkLook;
            Text = "RaceControl - track map";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 560);
            MinimumSize = new Size(380, 360);
            ShowInTaskbar = false;

            canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill, Cursor = Cursors.Cross };
            canvas.Paint += (s, e) => Draw(e.Graphics);
            canvas.Resize += (s, e) => canvas.Invalidate();
            canvas.MouseMove += (s, e) =>
            {
                hover = DlongAt(e.Location);
                canvas.Invalidate();
            };
            canvas.MouseLeave += (s, e) => { hover = double.NaN; canvas.Invalidate(); };
            canvas.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                double d = DlongAt(e.Location);
                if (double.IsNaN(d)) return;
                setCone(d);
                canvas.Invalidate();
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58 };
            info = new Label { Left = 8, Top = 6, Height = 46, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            info.Width = bottom.Width - 140;
            var againBtn = new Button { Text = "Record again", Width = 116, Height = 26, Top = 14, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            againBtn.Left = bottom.Width - 124;
            new ToolTip().SetToolTip(againBtn, "Throw this track's map away and draw it again from the cars on track\n(for a map that came out wrong, or one taken from another app).");
            againBtn.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Draw this track's map again from the cars on track?", Text, MessageBoxButtons.OKCancel) == DialogResult.OK) recordAgain();
            };
            bottom.Controls.Add(info);
            bottom.Controls.Add(againBtn);
            Controls.Add(canvas);
            Controls.Add(bottom);

            refresh = new Timer { Interval = 200 };
            refresh.Tick += (s, e) => { info.Text = InfoText(); canvas.Invalidate(); };
            refresh.Start();
            FormClosed += (s, e) => refresh.Stop();
            Theme.Apply(this, dark);
            info.Text = InfoText();
        }

        public void SetDark(bool on) { dark = on; Theme.Apply(this, on); canvas.Invalidate(); }

        class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }

        // world -> screen: fit the track in the panel, north up (world y up), same scale both ways
        bool Fit(TrackMap map, out float scale, out float ox, out float oy)
        {
            scale = 1; ox = oy = 0;
            if (map == null) return false;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < TrackMap.N; i++)
            {
                minX = Math.Min(minX, map.X[i]); maxX = Math.Max(maxX, map.X[i]);
                minY = Math.Min(minY, map.Y[i]); maxY = Math.Max(maxY, map.Y[i]);
            }
            float padX = 84, padY = 44;                                // room for the names beside the track
            float w = Math.Max(1, canvas.ClientSize.Width - 2 * padX), h = Math.Max(1, canvas.ClientSize.Height - 2 * padY);
            scale = Math.Min(w / Math.Max(1f, maxX - minX), h / Math.Max(1f, maxY - minY));
            ox = padX + (w - (maxX - minX) * scale) / 2 - minX * scale;
            oy = padY + (h - (maxY - minY) * scale) / 2 + maxY * scale;
            return true;
        }

        static PointF P(double x, double y, float scale, float ox, float oy) { return new PointF((float)(ox + x * scale), (float)(oy - y * scale)); }

        double DlongAt(Point p)
        {
            TrackMap map = getMap();
            float scale, ox, oy;
            if (!Fit(map, out scale, out ox, out oy)) return double.NaN;
            double wx = (p.X - ox) / scale, wy = (oy - p.Y) / scale, dist;
            double d = map.Nearest(wx, wy, out dist);
            return dist * scale <= 40 ? d : double.NaN;            // within 40 px of the track
        }

        string Metres(double d) { return double.IsNaN(d) ? "-" : d.ToString("0") + " m"; }

        string InfoText()
        {
            TrackMap map = getMap();
            Marks mk = getMarks();
            string s = getStatus();
            if (map == null) return s;
            string h = !double.IsNaN(hover) ? "Click to put the cone at " + Metres(hover) + " (" + (map.Length - hover).ToString("0") + " m before the line)"
                                            : "Click on the track to place the cone.";
            return s + "\n" + h + "   Cone: " + (double.IsNaN(mk.Cone) ? "where the pace car is" : Metres(mk.Cone));
        }

        void Draw(Graphics g)
        {
            Color back = dark ? Theme.Back : Color.White, edge = dark ? Color.FromArgb(18, 18, 20) : Color.FromArgb(40, 40, 40),
                  road = dark ? Color.FromArgb(120, 124, 132) : Color.FromArgb(150, 154, 160), text = dark ? Theme.Text : Color.Black;
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TrackMap map = getMap();
            float scale, ox, oy;
            if (!Fit(map, out scale, out ox, out oy))
            {
                using (var f = new Font(Font.FontFamily, 10))
                using (var b = new SolidBrush(text))
                    g.DrawString("No map for this track yet.\n\nIt draws itself from the cars on track: start the session (practice or the race)\n" +
                                 "and let the AI cars run - the first lap or so is enough. You don't have to drive.", f, b, 16, 16);
                return;
            }
            var pts = new PointF[TrackMap.N + 1];
            for (int i = 0; i <= TrackMap.N; i++) pts[i] = P(map.X[i % TrackMap.N], map.Y[i % TrackMap.N], scale, ox, oy);
            float width = Math.Max(6, Math.Min(16, 15 * scale));     // ~15 m of track
            using (var pen = new Pen(edge, width + 4) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
            using (var pen = new Pen(road, width) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
            // direction of travel, just after the line
            DrawArrow(g, map, map.Length * 0.03, scale, ox, oy, text);

            // the cars under the marks
            List<PointF> cars = getCars();
            if (cars != null)
                for (int i = 0; i < cars.Count; i++)
                {
                    if (float.IsNaN(cars[i].X)) continue;
                    PointF c = P(cars[i].X, cars[i].Y, scale, ox, oy);
                    float r = i == 0 ? 5 : 3.5f;
                    using (var b = new SolidBrush(i == 0 ? Color.Gold : dark ? Color.White : Color.FromArgb(30, 30, 30)))
                        g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
                }

            Marks mk = getMarks();
            using (var f = new Font(Font.FontFamily, 8.5f, FontStyle.Bold))
            {
                Tick(g, map, 0, scale, ox, oy, width, dark ? Color.White : Color.Black, "S/F", f, 4);
                Tick(g, map, mk.PitEntry, scale, ox, oy, width, Color.FromArgb(70, 140, 230), "PIT IN", f, 2);
                Tick(g, map, mk.PitExit, scale, ox, oy, width, Color.FromArgb(70, 140, 230), "PIT OUT", f, 2);
                Tick(g, map, mk.DoubleFile, scale, ox, oy, width, Color.FromArgb(120, 190, 90), "DOUBLE FILE", f, 2);
                if (!double.IsNaN(hover)) Tick(g, map, hover, scale, ox, oy, width, Color.FromArgb(160, 255, 140, 0), null, f, 2);
                if (!double.IsNaN(mk.Cone)) Cone(g, map, mk.Cone, scale, ox, oy, width, f);
            }

        }

        void DrawArrow(Graphics g, TrackMap map, double d, float scale, float ox, float oy, Color c)
        {
            double x, y, tx, ty;
            map.AtDlat(d, 0, out x, out y);
            map.Heading(d, out tx, out ty);
            PointF a = P(x, y, scale, ox, oy), b = P(x + tx * 40 / scale, y + ty * 40 / scale, scale, ox, oy);
            using (var pen = new Pen(Color.FromArgb(150, c), 2) { CustomEndCap = new AdjustableArrowCap(4, 4) }) g.DrawLine(pen, a, b);
        }

        // a mark across the track at a lap distance, with its name outside the track
        void Tick(Graphics g, TrackMap map, double d, float scale, float ox, float oy, float width, Color c, string name, Font f, float thick)
        {
            if (double.IsNaN(d) || map.Length <= 0) return;
            double ax, ay, bx, by;
            float half = (width / 2 + 5) / scale;
            map.AtDlat(d, half, out ax, out ay);
            map.AtDlat(d, -half, out bx, out by);
            using (var pen = new Pen(c, thick)) g.DrawLine(pen, P(ax, ay, scale, ox, oy), P(bx, by, scale, ox, oy));
            if (name != null) Label(g, map, d, scale, ox, oy, width, c, name, f);
        }

        // a name beside the track at a lap distance: to the right of travel (outside on an oval), growing away from the
        // track, kept inside the window
        void Label(Graphics g, TrackMap map, double d, float scale, float ox, float oy, float width, Color c, string name, Font f)
        {
            double cx, cy, lx, ly;
            map.AtDlat(d, 0, out cx, out cy);
            map.AtDlat(d, -(width / 2 + 8) / scale, out lx, out ly);
            PointF a = P(cx, cy, scale, ox, oy), l = P(lx, ly, scale, ox, oy);
            float ux = l.X - a.X, uy = l.Y - a.Y, len = (float)Math.Sqrt(ux * ux + uy * uy);
            if (len > 1e-3) { ux /= len; uy /= len; }
            SizeF sz = g.MeasureString(name, f);
            float left = l.X - sz.Width / 2 + ux * sz.Width / 2, top = l.Y - sz.Height / 2 + uy * sz.Height / 2;
            left = Math.Max(2, Math.Min(canvas.ClientSize.Width - sz.Width - 2, left));
            top = Math.Max(2, Math.Min(canvas.ClientSize.Height - sz.Height - 2, top));
            using (var b = new SolidBrush(c)) g.DrawString(name, f, b, left, top);
        }

        void Cone(Graphics g, TrackMap map, double d, float scale, float ox, float oy, float width, Font f)
        {
            double x, y;
            map.AtDlat(d, 0, out x, out y);
            PointF c = P(x, y, scale, ox, oy);
            float h = Math.Max(14, width * 1.4f);
            var tri = new[] { new PointF(c.X, c.Y - h), new PointF(c.X - h * 0.45f, c.Y + h * 0.15f), new PointF(c.X + h * 0.45f, c.Y + h * 0.15f) };
            using (var b = new SolidBrush(Color.FromArgb(255, 120, 0))) g.FillPolygon(b, tri);
            using (var pen = new Pen(Color.White, 1.5f)) g.DrawPolygon(pen, tri);
            Label(g, map, d, scale, ox, oy, width + h, Color.FromArgb(255, 140, 20), "CONE " + d.ToString("0") + " m", f);
        }
    }
}
