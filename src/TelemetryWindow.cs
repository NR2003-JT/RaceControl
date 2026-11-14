using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace RaceControl
{
    // The telemetry window (SMT style traces). Tick drivers: their lap running now draws live. Tick any of their
    // recorded timed laps (or "Best laps") to lay them over each other. Charts by distance round the lap: speed,
    // throttle, brake, steering (steering wheel degrees) and the time gained / lost against the reference lap, with
    // each trace's lap time. Hover for the values at any point of the lap.
    public class TelemetryWindow : Form
    {
        readonly TeleRecorder rec;
        readonly Func<int, string> nameOf;          // "#24 GORDON"
        readonly Func<int, int> numberOf;
        readonly CheckedListBox drivers, laps;
        readonly CheckBox liveBox;
        readonly ComboBox refBox;
        readonly GraphPanel graph;
        readonly Timer timer;
        int seenVersion = -1, seenCars = -1;
        float cursor = -1;                          // 0..1 of the lap under the mouse (-1 = none)
        bool building;
        readonly HashSet<int> tickedCars = new HashSet<int>();
        readonly HashSet<string> tickedLaps = new HashSet<string>();
        readonly Dictionary<string, int> colorOf = new Dictionary<string, int>();
        List<Trace> traces = new List<Trace>();
        string refKey = "";                          // "" = the fastest lap shown

        static readonly Color Back = Color.FromArgb(22, 24, 27), Panel = Color.FromArgb(31, 34, 38), Divider = Color.FromArgb(52, 58, 62),
            Label = Color.FromArgb(167, 175, 180), Value = Color.FromArgb(246, 247, 244), Grid = Color.FromArgb(40, 45, 50),
            Lime = Color.FromArgb(222, 250, 64), Gain = Color.FromArgb(64, 224, 120), Lose = Color.FromArgb(255, 56, 60);
        static readonly Color[] Palette = {
            Color.FromArgb(222, 250, 64), Color.FromArgb(64, 196, 255), Color.FromArgb(255, 140, 50), Color.FromArgb(232, 92, 255),
            Color.FromArgb(64, 224, 120), Color.FromArgb(255, 214, 64), Color.FromArgb(255, 96, 140), Color.FromArgb(150, 160, 255),
            Color.FromArgb(120, 240, 230), Color.FromArgb(255, 255, 255) };

        class Trace { public string Key, Text; public TeleLap Lap; public bool Live; public Color C; }
        class Item { public string Key, Text; public int Car; public TeleLap Lap; public override string ToString() { return Text; } }

        public TelemetryWindow(TeleRecorder rec, Func<int, string> nameOf, Func<int, int> numberOf, Icon icon)
        {
            this.rec = rec; this.nameOf = nameOf; this.numberOf = numberOf;
            Text = "Race Control - telemetry";
            if (icon != null) Icon = icon;
            BackColor = Back; ForeColor = Value;
            Size = new Size(1280, 800); MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var left = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Panel, Padding = new Padding(10) };
            var lblD = new System.Windows.Forms.Label { Text = "DRIVERS  (tick to follow live)", ForeColor = Label, Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            drivers = new CheckedListBox { Dock = DockStyle.Top, Height = 260, CheckOnClick = true, BackColor = Back, ForeColor = Value, BorderStyle = BorderStyle.None, IntegralHeight = false };
            liveBox = new CheckBox { Text = "Show the lap they're on now (live)", Checked = true, Dock = DockStyle.Top, Height = 26, ForeColor = Value };
            var lblL = new System.Windows.Forms.Label { Text = "LAPS  (tick to lay over)", ForeColor = Label, Dock = DockStyle.Top, Height = 24, Padding = new Padding(0, 6, 0, 0), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            laps = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BackColor = Back, ForeColor = Value, BorderStyle = BorderStyle.None, IntegralHeight = false };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            var bestBtn = Btn("Best laps", "Tick each ticked driver's best lap");
            var clearBtn = Btn("Clear laps", "Untick every lap");
            btns.Controls.Add(bestBtn); btns.Controls.Add(clearBtn);
            var lblR = new System.Windows.Forms.Label { Text = "Time gained / lost against:", ForeColor = Label, AutoSize = false, Width = 226, Height = 18, Margin = new Padding(3, 8, 3, 0) };
            refBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 228, BackColor = Back, ForeColor = Value, FlatStyle = FlatStyle.Flat };
            btns.Controls.Add(lblR); btns.Controls.Add(refBox);
            btns.Height = 100;
            // docking order: the last added sits on top
            left.Controls.Add(laps); left.Controls.Add(lblL); left.Controls.Add(liveBox); left.Controls.Add(drivers); left.Controls.Add(lblD); left.Controls.Add(btns);
            graph = new GraphPanel { Dock = DockStyle.Fill };
            graph.Paint += (s, e) => Draw(e.Graphics, graph.ClientSize);
            graph.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && dragX >= 0 && plotW > 0)
                {
                    float span = v1 - v0, nv0 = dragV0 - (e.X - dragX) / plotW * span;
                    nv0 = Math.Max(0, Math.Min(1 - span, nv0));
                    if (nv0 != v0) { v0 = nv0; v1 = nv0 + span; graph.Invalidate(); }
                }
                float f = Frac(e.X); if (f != cursor) { cursor = f; graph.Invalidate(); }
            };
            graph.MouseLeave += (s, e) => { cursor = -1; dragX = -1; graph.Invalidate(); };
            // scroll to zoom round the mouse, drag to slide along the lap, double-click for the whole lap
            graph.MouseEnter += (s, e) => { if (ContainsFocus && !graph.Focused) graph.Focus(); };
            graph.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragX = e.X; dragV0 = v0; graph.Focus(); } };
            graph.MouseUp += (s, e) => { dragX = -1; };
            graph.MouseDoubleClick += (s, e) => { v0 = 0; v1 = 1; cursor = Frac(e.X); graph.Invalidate(); };
            graph.MouseWheel += (s, e) =>
            {
                if (plotW <= 0) return;
                float at = Math.Max(0, Math.Min(1, (e.X - PadL) / plotW));          // where the mouse is across the plot
                float pivot = v0 + at * (v1 - v0);
                float span = (float)((v1 - v0) * Math.Pow(0.8, e.Delta / 120.0));
                span = Math.Max(MinSpan, Math.Min(1, span));
                float nv0 = Math.Max(0, Math.Min(1 - span, pivot - at * span));
                v0 = nv0; v1 = nv0 + span;
                cursor = Frac(e.X); graph.Invalidate();
            };
            Controls.Add(graph); Controls.Add(left);

            drivers.ItemCheck += (s, e) =>
            {
                if (building) return;
                var it = (Item)drivers.Items[e.Index];
                if (e.NewValue == CheckState.Checked) tickedCars.Add(it.Car); else tickedCars.Remove(it.Car);
                BeginInvoke((Action)(() => { RebuildLaps(); Retrace(); }));
            };
            laps.ItemCheck += (s, e) =>
            {
                if (building) return;
                var it = (Item)laps.Items[e.Index];
                if (e.NewValue == CheckState.Checked) tickedLaps.Add(it.Key); else tickedLaps.Remove(it.Key);
                BeginInvoke((Action)Retrace);
            };
            liveBox.CheckedChanged += (s, e) => Retrace();
            refBox.SelectedIndexChanged += (s, e) => { if (building) return; var it = refBox.SelectedItem as Item; refKey = it == null ? "" : it.Key; graph.Invalidate(); };
            bestBtn.Click += (s, e) =>
            {
                foreach (int c in tickedCars) { var t = rec.Of(c); var b = t != null ? t.Best : null; if (b != null) tickedLaps.Add(LapKey(b)); }
                RebuildLaps(); Retrace();
            };
            clearBtn.Click += (s, e) => { tickedLaps.Clear(); RebuildLaps(); Retrace(); };

            timer = new Timer { Interval = 100 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            FormClosed += (s, e) => timer.Stop();
        }

        // from code: follow these drivers (live), and optionally tick their best laps
        public void Select(IEnumerable<int> cars, bool bestLaps)
        {
            foreach (int c in cars) tickedCars.Add(c);
            if (bestLaps) foreach (int c in tickedCars) { var t = rec.Of(c); var b = t != null ? t.Best : null; if (b != null) tickedLaps.Add(LapKey(b)); }
            seenVersion = -1; Tick();
            RebuildDrivers(); RebuildLaps(); Retrace();
        }

        // for the offline render: the cursor at a share of the lap
        public void CursorAt(float frac) { cursor = frac; graph.Invalidate(); }

        Button Btn(string text, string tip)
        {
            var b = new Button { Text = text, Width = 106, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Divider, ForeColor = Value };
            b.FlatAppearance.BorderColor = Divider;
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        static string LapKey(TeleLap l) { return l.Car + ":" + l.No; }

        // follow the recorder: new cars / laps refresh the lists, live laps redraw
        public void Tick()
        {
            if (!Visible) return;
            int carsNow = rec.Cars.Count();
            if (rec.Version != seenVersion || carsNow != seenCars)
            {
                seenVersion = rec.Version; seenCars = carsNow;
                RebuildDrivers(); RebuildLaps(); Retrace();
            }
            else if (traces.Any(t => t.Live)) graph.Invalidate();
        }

        void RebuildDrivers()
        {
            var list = rec.Cars.Where(t => t.HaveNow).OrderBy(t => numberOf(t.Car)).Select(t => new Item { Car = t.Car, Key = "c" + t.Car, Text = nameOf(t.Car) + (t.Laps.Count > 0 ? "   (" + t.Laps.Count + (t.Laps.Count == 1 ? " lap)" : " laps)") : "") }).ToList();
            building = true;
            drivers.BeginUpdate(); drivers.Items.Clear();
            foreach (var it in list) drivers.Items.Add(it, tickedCars.Contains(it.Car));
            drivers.EndUpdate();
            building = false;
        }

        void RebuildLaps()
        {
            building = true;
            laps.BeginUpdate(); laps.Items.Clear();
            foreach (int c in tickedCars.OrderBy(c => numberOf(c)))
            {
                var t = rec.Of(c); if (t == null) continue;
                var best = t.Best;
                foreach (var l in t.Laps.AsEnumerable().Reverse())
                {
                    var it = new Item { Car = c, Lap = l, Key = LapKey(l), Text = nameOf(c) + "   lap " + l.No + "   " + LapFmt.Time(l.LapMs) + (l == best ? "   BEST" : "") };
                    laps.Items.Add(it, tickedLaps.Contains(it.Key));
                }
            }
            laps.EndUpdate();
            building = false;
        }

        // what is drawn: the live laps of the ticked drivers, then the ticked laps
        void Retrace()
        {
            var list = new List<Trace>();
            if (liveBox.Checked)
                foreach (int c in tickedCars.OrderBy(c => numberOf(c)))
                {
                    var t = rec.Of(c);
                    if (t != null && t.Live != null) list.Add(new Trace { Key = "live" + c, Lap = t.Live, Live = true, Text = nameOf(c) + "  LIVE" });
                }
            foreach (var t in rec.Cars)
                foreach (var l in t.Laps)
                    if (tickedLaps.Contains(LapKey(l)) && tickedCars.Contains(t.Car))
                        list.Add(new Trace { Key = LapKey(l), Lap = l, Text = nameOf(t.Car) + "  lap " + l.No });
            foreach (var tr in list)
            {
                int ci;
                if (!colorOf.TryGetValue(tr.Key, out ci)) { ci = Enumerable.Range(0, Palette.Length).FirstOrDefault(i => !colorOf.Values.Contains(i)); colorOf[tr.Key] = ci; }
                tr.C = Palette[ci % Palette.Length];
            }
            foreach (var k in colorOf.Keys.ToList()) if (!list.Any(t => t.Key == k)) colorOf.Remove(k);
            traces = list;
            // the reference choices: the fastest lap shown, or any finished lap shown
            building = true;
            refBox.BeginUpdate(); refBox.Items.Clear();
            refBox.Items.Add(new Item { Key = "", Text = "The fastest lap shown" });
            foreach (var t in traces.Where(t => !t.Live)) refBox.Items.Add(new Item { Key = t.Key, Text = t.Text + "  " + LapFmt.Time(t.Lap.LapMs) });
            int sel = 0;
            for (int i = 0; i < refBox.Items.Count; i++) if (((Item)refBox.Items[i]).Key == refKey) sel = i;
            refBox.SelectedIndex = sel; refKey = ((Item)refBox.Items[sel]).Key;
            refBox.EndUpdate();
            building = false;
            graph.Invalidate();
        }

        TeleLap Reference()
        {
            var t = traces.FirstOrDefault(x => x.Key == refKey && !x.Live);
            if (t != null) return t.Lap;
            var done = traces.Where(x => !x.Live).OrderBy(x => x.Lap.LapMs).FirstOrDefault();
            if (done != null) return done.Lap;
            // nothing finished is shown: the fastest lap recorded by a ticked driver keeps the distances right
            return rec.Cars.Where(c => tickedCars.Contains(c.Car)).Select(c => c.Best).Where(b => b != null).OrderBy(b => b.LapMs).FirstOrDefault();
        }

        // ---- drawing ----
        const float PadL = 96, PadR = 24;
        float plotW;
        const float MinSpan = 0.01f;                 // zoomed in as far as 1% of the lap
        float v0 = 0, v1 = 1;                        // the share of the lap in view (zoom)
        float dragX = -1, dragV0;
        // a screen x to the share of the lap there (-1 = off the plot)
        float Frac(int x) { if (plotW <= 0) return -1; float f = (x - PadL) / plotW; return f < 0 || f > 1 ? -1 : v0 + f * (v1 - v0); }
        // a share of the lap to a screen x
        float XOf(float frac) { return PadL + (frac - v0) / (v1 - v0) * plotW; }

        // each trace lined up with the reference by position (TeleRecorder.Align), kept between paints: a lap still
        // running only has its new samples matched
        readonly Dictionary<string, KeyValuePair<TeleLap, TeleLap>> aligned = new Dictionary<string, KeyValuePair<TeleLap, TeleLap>>();
        TeleLap Aligned(Trace t, TeleLap reference)
        {
            if (reference == null || t.Lap == reference) return t.Lap;
            KeyValuePair<TeleLap, TeleLap> c;
            TeleLap from = aligned.TryGetValue(t.Key, out c) && c.Key == reference ? c.Value : null;
            var a = TeleRecorder.Align(t.Lap, reference, from);
            aligned[t.Key] = new KeyValuePair<TeleLap, TeleLap>(reference, a);
            return a;
        }

        void Draw(Graphics g, Size size)
        {
            g.Clear(Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            plotW = Math.Max(10, size.Width - PadL - PadR);
            var reference = Reference();
            // every trace on the reference lap's distance scale (or, with no reference, its own)
            var al = traces.ToDictionary(t => t, t => Aligned(t, reference));
            foreach (var k in aligned.Keys.ToList()) if (!traces.Any(t => t.Key == k)) aligned.Remove(k);
            float L = reference != null ? reference.Length : Math.Max(1, traces.Select(t => t.Lap.Length).DefaultIfEmpty(1).Max());

            // legend: one row a trace: colour, driver, lap time (live: time so far), gap; at the cursor: the values there
            float y = 10;
            using (var f = new Font("Segoe UI", 9.5f, FontStyle.Bold)) using (var small = new Font("Segoe UI", 9f))
            {
                float rowH = Math.Max(20, (float)Math.Ceiling(f.GetHeight(g)) + 4);   // a row a trace, sized by the font
                if (traces.Count == 0)
                {
                    Txt(g, "Tick drivers on the left: their lap running now draws live. Tick laps (or Best laps) to compare them.", f, Label, PadL, y);
                    y += rowH;                                                    // its own row: the charts start under it
                }
                foreach (var t in traces)
                {
                    using (var b = new SolidBrush(t.C)) g.FillRectangle(b, PadL, y + 4, 14, 10);
                    Txt(g, t.Text, f, Value, PadL + 22, y);
                    string time = t.Live ? (t.Lap.S.Count > 0 ? LapFmt.Time(t.Lap.S[t.Lap.S.Count - 1].Ms) : "-") : LapFmt.Time(t.Lap.LapMs);
                    Txt(g, time, f, t.Live ? Lime : Value, PadL + 250, y);
                    if (reference != null && t.Lap != reference)
                    {
                        int d; float at = cursor >= 0 ? cursor * L : t.Live ? al[t].Length : L;
                        if (TeleRecorder.Delta(al[t], reference, at, out d)) Txt(g, Gap(d), f, d <= 0 ? Gain : Lose, PadL + 340, y);
                    }
                    else if (reference != null) Txt(g, "REF", f, Label, PadL + 340, y);
                    TeleSample v;
                    if (cursor >= 0 && al[t].At(cursor * L, out v))
                        Txt(g, string.Format("{0:0} mph   gear {1}   throttle {2:0}%   brake {3:0}%   steering {4}   {5}", v.Mph, v.Gear > 0 ? v.Gear.ToString() : "N",
                            v.Thr * 100, v.Brk * 100, SteerText(v.Steer), LapFmt.Time(v.Ms)), small, Label, PadL + 430, y + 1);
                    y += rowH;
                }
            }
            y += 8;
            float legendBottom = y;

            // the charts share the distance axis
            float bottom = size.Height - 34, avail = bottom - y;
            if (avail < 120) return;
            var rows = new[] { new { Name = "SPEED", Unit = "mph", W = 0.25f }, new { Name = "THROTTLE", Unit = "%", W = 0.15f }, new { Name = "BRAKE", Unit = "%", W = 0.15f },
                               new { Name = "STEERING", Unit = "wheel °", W = 0.22f }, new { Name = "TIME +/-", Unit = "s", W = 0.23f } };
            float gap = 8, total = avail - gap * (rows.Length - 1);
            // scales
            float vmax = 40;
            foreach (var t in traces) foreach (var s in t.Lap.S) vmax = Math.Max(vmax, s.Mph);
            vmax = (float)Math.Ceiling(vmax / 20) * 20;
            float smax = 30;
            foreach (var t in traces) foreach (var s in t.Lap.S) smax = Math.Max(smax, Math.Abs(s.Steer * TeleRecorder.SteerRatio));
            smax = (float)Math.Ceiling(smax / 15) * 15;
            int n = (int)plotW;
            var deltas = new Dictionary<Trace, float[]>();
            float dmax = 0.2f;
            if (reference != null)
                foreach (var t in traces)
                {
                    if (t.Lap == reference) continue;
                    var arr = new float[n + 1];
                    for (int i = 0; i <= n; i++) { int d; arr[i] = TeleRecorder.Delta(al[t], reference, (v0 + i / (float)n * (v1 - v0)) * L, out d) ? d / 1000f : float.NaN; if (!float.IsNaN(arr[i])) dmax = Math.Max(dmax, Math.Abs(arr[i])); }
                    deltas[t] = arr;
                }
            dmax = (float)Math.Ceiling(dmax * 10) / 10;

            using (var lf = new Font("Segoe UI", 8.5f, FontStyle.Bold)) using (var tf = new Font("Segoe UI", 8f))
            {
                for (int r = 0; r < rows.Length; r++)
                {
                    float h = total * rows[r].W;
                    var box = new RectangleF(PadL, y, plotW, h);
                    using (var b = new SolidBrush(Panel)) g.FillRectangle(b, box);
                    Txt(g, rows[r].Name, lf, Label, 8, y + 2);
                    Txt(g, rows[r].Unit, tf, Label, 8, y + 18);
                    using (var p = new Pen(Grid)) for (int q = 1; q < 4; q++) g.DrawLine(p, PadL + plotW * q / 4, y, PadL + plotW * q / 4, y + h);
                    if (r == 0) Axis(g, tf, box, 0, vmax, "0");
                    if (r == 1 || r == 2) Axis(g, tf, box, 0, 100, "0");
                    if (r == 3) Axis(g, tf, box, -smax, smax, "0");
                    if (r == 4) Axis(g, tf, box, -dmax, dmax, "0.0");
                    var clip = g.Clip; g.SetClip(box);
                    if (r == 0) { foreach (var t in traces) Plot(g, t.C, al[t], L, box, s => s.Mph / vmax, 2f); }
                    if (r == 1) { foreach (var t in traces) Plot(g, t.C, al[t], L, box, s => s.Thr, 1.8f); }
                    if (r == 2) { foreach (var t in traces) Plot(g, t.C, al[t], L, box, s => s.Brk, 1.8f); }
                    if (r == 3)
                    {
                        Mid(g, box);
                        foreach (var t in traces) Plot(g, t.C, al[t], L, box, s => 0.5f + s.Steer * TeleRecorder.SteerRatio / smax / 2, 1.6f);
                    }
                    if (r == 4)
                    {
                        Mid(g, box);
                        if (reference == null) Txt(g, "Needs a finished lap to measure against", tf, Label, PadL + 8, y + 6);
                        else
                        {
                            Txt(g, "faster", tf, Gain, PadL + plotW - 44, y + h - 16);
                            Txt(g, "slower", tf, Lose, PadL + plotW - 44, y + 3);
                            foreach (var kv in deltas)
                            {
                                var pts = new List<PointF>();
                                for (int i = 0; i <= n; i++)
                                {
                                    if (float.IsNaN(kv.Value[i])) { Flush(g, kv.Key.C, pts, 2f); continue; }
                                    pts.Add(new PointF(PadL + i, y + h / 2 - kv.Value[i] / dmax * (h / 2 - 4)));
                                }
                                Flush(g, kv.Key.C, pts, 2f);
                            }
                        }
                    }
                    g.Clip = clip;
                    y += h + gap;
                }
                // the distance axis
                float len = L;
                for (int q = 0; q <= 4; q++)
                {
                    float fq = v0 + (v1 - v0) * q / 4;
                    bool fine = v1 - v0 < 0.2f;
                    string s = (fq * 100).ToString(fine ? "0.0" : "0") + "%" + (len > 100 ? "  " + (len * fq / 1609.344).ToString(fine ? "0.000" : "0.00") + " mi" : "");
                    var sz = g.MeasureString(s, tf);
                    Txt(g, s, tf, Label, Math.Max(PadL, Math.Min(PadL + plotW - sz.Width, PadL + plotW * q / 4 - sz.Width / 2)), bottom + 6);
                }
                Txt(g, "LAP DISTANCE", lf, Label, 8, bottom + 6);
                if (v1 - v0 < 0.999f) Txt(g, "ZOOM " + (1 / (v1 - v0)).ToString("0.0") + "x", lf, Lime, 8, bottom + 20);
            }
            if (cursor >= 0)
                using (var p = new Pen(Color.FromArgb(160, Value), 1) { DashStyle = DashStyle.Dash })
                    g.DrawLine(p, XOf(cursor), legendBottom, XOf(cursor), bottom);
        }

        // one trace in a chart: v gives 0 (bottom) .. 1 (top)
        void Plot(Graphics g, Color c, TeleLap l, float len, RectangleF box, Func<TeleSample, float> v, float width)
        {
            if (l.S.Count < 2) return;
            var pts = new List<PointF>();
            int n = (int)box.Width;
            for (int i = 0; i <= n; i++)
            {
                TeleSample s;
                if (!l.At((v0 + i / (float)n * (v1 - v0)) * len, out s)) { if (pts.Count > 0) break; continue; }
                pts.Add(new PointF(box.X + i, box.Bottom - 3 - (box.Height - 6) * Math.Max(-0.02f, Math.Min(1.02f, v(s)))));
            }
            Flush(g, c, pts, width);
        }

        static void Flush(Graphics g, Color c, List<PointF> pts, float w)
        {
            if (pts.Count > 1) using (var p = new Pen(c, w) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts.ToArray());
            pts.Clear();
        }

        static void Mid(Graphics g, RectangleF box) { using (var p = new Pen(Divider)) g.DrawLine(p, box.X, box.Y + box.Height / 2, box.Right, box.Y + box.Height / 2); }

        void Axis(Graphics g, Font f, RectangleF box, float lo, float hi, string fmt)
        {
            Txt(g, hi.ToString(fmt), f, Label, box.X - 6 - g.MeasureString(hi.ToString(fmt), f).Width, box.Y);
            Txt(g, lo.ToString(fmt), f, Label, box.X - 6 - g.MeasureString(lo.ToString(fmt), f).Width, box.Bottom - 14);
        }

        static void Txt(Graphics g, string s, Font f, Color c, float x, float y) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y); }

        static string Gap(int ms) { return (ms <= 0 ? "-" : "+") + (Math.Abs(ms) / 1000.0).ToString("0.000"); }

        static string SteerText(float frontDeg)
        {
            int w = (int)Math.Round(frontDeg * TeleRecorder.SteerRatio);
            return w == 0 ? "0°" : (w > 0 ? "L " : "R ") + Math.Abs(w) + "°";
        }

        class GraphPanel : Panel
        {
            public GraphPanel() { DoubleBuffered = true; ResizeRedraw = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        }
    }
}
