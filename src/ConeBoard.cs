using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RaceControl
{
    // The choose cone board, drawn like a TV graphic: series logo and title on top, an INSIDE / OUTSIDE bar, then
    // rows of two driver panels (number art, headshot, the driver's colour) with ROW n in a circle between them and
    // the spots gained or lost underneath. Everything is laid out on a 380 px wide board and scaled to the game.
    public class ConeBoardPainter
    {
        public const int W = 380, Pad = 10, HeaderH = 112, BarH = 28, PanelH = 128, StripH = 28, RowGap = 8, Gap = 4;
        public const int RowH = PanelH + StripH + RowGap;
        public const int ColW = (W - 2 * Pad - Gap) / 2;
        public static int RowsTop { get { return HeaderH + BarH + 6; } }

        public readonly ConeArt Art;
        public float Scale = 1f;
        readonly string family;

        static readonly Color Frame = Color.FromArgb(20, 22, 26), FrameEdge = Color.FromArgb(92, 96, 104),
            Empty = Color.FromArgb(38, 41, 48), Up = Color.FromArgb(40, 200, 70), Down = Color.FromArgb(225, 40, 40),
            Gold = Color.FromArgb(255, 196, 40), Choice = Color.FromArgb(60, 220, 110);

        // fallback panel colours when the art has none for a car
        static readonly Color[] Palette = { Color.FromArgb(30, 80, 170), Color.FromArgb(170, 30, 40), Color.FromArgb(220, 170, 20),
            Color.FromArgb(30, 130, 70), Color.FromArgb(120, 120, 128), Color.FromArgb(230, 110, 20), Color.FromArgb(90, 40, 150), Color.FromArgb(20, 20, 24) };

        public ConeBoardPainter(ConeArt art)
        {
            Art = art;
            var names = new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            family = names.Contains("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
        }

        public Font MakeFont(float px, FontStyle style = FontStyle.Regular) { return new Font(family, px, style, GraphicsUnit.Pixel); }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
        }

        // ---- frame, header, lane bar ------------------------------------------------------------

        public void DrawFrame(Graphics g, int height)
        {
            var r = new Rectangle(0, 0, W, height);
            using (var b = new LinearGradientBrush(r, Color.FromArgb(34, 37, 43), Frame, 90f)) g.FillRectangle(b, r);
            using (var p = new Pen(FrameEdge, 2)) g.DrawRectangle(p, 1, 1, W - 2, height - 2);
        }

        public void DrawHeader(Graphics g, string title)
        {
            var logoBox = new Rectangle(Pad + 50, Pad + 2, W - 2 * Pad - 100, 62);
            Image logo = Art.Logo();
            Bitmap fit = logo != null ? Art.Fit(logo, (int)(logoBox.Width * Scale), (int)(logoBox.Height * Scale)) : null;
            if (fit != null)
            {
                float w = fit.Width / Scale, h = fit.Height / Scale;
                g.DrawImage(fit, logoBox.X + (logoBox.Width - w) / 2, logoBox.Y + (logoBox.Height - h) / 2, w, h);
            }
            else
            {
                using (var f = MakeFont(24, FontStyle.Bold | FontStyle.Italic))
                using (var sf = Center())
                    g.DrawString(string.IsNullOrEmpty(Art.SeriesName) ? "RACE CONTROL" : Art.SeriesName.ToUpperInvariant(), f, Brushes.White, logoBox, sf);
            }
            var tr = new Rectangle(Pad, Pad + 66, W - 2 * Pad, 34);
            using (var b = new LinearGradientBrush(tr, Color.FromArgb(58, 62, 70), Color.FromArgb(30, 32, 38), 90f)) g.FillRectangle(b, tr);
            using (var f = MakeFont(23))
            using (var sf = Center())
                g.DrawString(Spaced(title), f, Brushes.White, tr, sf);
            var bar = new Rectangle(Pad, HeaderH, W - 2 * Pad, BarH);
            g.FillRectangle(Brushes.Black, bar);
            using (var f = MakeFont(19))
            using (var sf = Center())
            {
                g.DrawString(Spaced("INSIDE"), f, Brushes.White, new Rectangle(Pad, HeaderH, ColW, BarH), sf);
                g.DrawString(Spaced("OUTSIDE"), f, Brushes.White, new Rectangle(Pad + ColW + Gap, HeaderH, ColW, BarH), sf);
            }
        }

        static string Spaced(string s) { return string.Join(" ", s.ToCharArray().Select(c => c.ToString()).ToArray()); }

        static StringFormat Center() { return new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; }

        public static Rectangle PanelRect(int lane, int y) { return new Rectangle(Pad + (lane == 1 ? 0 : ColW + Gap), y, ColW, PanelH); }

        // ---- a row ------------------------------------------------------------------------------

        // One lane's cell: the car (reveal 0..1 wipes it in from the outer edge), an empty spot, or (choice != 0) the
        // spot the player would get: 1 offered, 2 offered and suggested, 3 chosen.
        public void DrawCell(Graphics g, int lane, int y, ConeEntry e, float reveal, int choice, int playerNum, int gainedIfChosen, bool hover)
        {
            Rectangle r = PanelRect(lane, y);
            var strip = new Rectangle(r.X, r.Bottom, r.Width, StripH);
            g.FillRectangle(Brushes.Black, strip);
            if (e == null && choice == 0)
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(46, 49, 57), Empty, 90f)) g.FillRectangle(b, r);
                using (var p = new Pen(Color.FromArgb(54, 58, 66), 1)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                return;
            }
            int num = e != null ? e.Num : playerNum;
            bool ghost = e == null && choice != 3;
            GraphicsState st = g.Save();
            if (reveal < 1f)
            {
                int w = (int)Math.Ceiling(r.Width * Math.Max(0f, reveal));
                g.SetClip(lane == 1 ? new Rectangle(r.X, r.Y, w, r.Height + StripH) : new Rectangle(r.Right - w, r.Y, w, r.Height + StripH));
            }
            Color tint = TintOf(num);
            Color top = Mix(tint, Color.FromArgb(40, 42, 48), ghost ? 0.75 : 0.35), bottom = Mix(tint, Color.Black, ghost ? 0.85 : 0.65);
            using (var b = new LinearGradientBrush(r, top, bottom, 90f)) g.FillRectangle(b, r);
            // a light sweep across the panel, as on the TV graphic
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[] { new PointF(r.X + r.Width * 0.15f, r.Y), new PointF(r.X + r.Width * 0.55f, r.Y), new PointF(r.X + r.Width * 0.30f, r.Bottom), new PointF(r.X - r.Width * 0.10f, r.Bottom) });
                using (var b = new SolidBrush(Color.FromArgb(ghost ? 8 : 22, 255, 255, 255))) g.FillPath(b, path);
            }
            g.SetClip(r, CombineMode.Intersect);
            float alpha = ghost ? 0.35f : 1f;
            // number art on the outer side, headshot towards the middle
            Image numImg = Art.Number(num);
            var numBox = new RectangleF(lane == 1 ? r.X + 6 : r.Right - 6 - r.Width * 0.5f, r.Y + r.Height * 0.16f, r.Width * 0.5f, r.Height * 0.56f);
            if (numImg != null) DrawFit(g, numImg, numBox, lane == 1 ? 0 : 2, 1, alpha);
            else DrawNumberText(g, num, numBox, tint, lane, alpha);
            Image head = Art.Headshot(num);
            if (head != null)
            {
                var headBox = new RectangleF(lane == 1 ? r.X + r.Width * 0.30f : r.X + r.Width * 0.06f, r.Y + 6, r.Width * 0.64f, r.Height - 6);
                DrawFit(g, head, headBox, 1, 2, alpha);
            }
            using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Bottom - 30, r.Width, 30), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(120, 0, 0, 0), 90f))
                g.FillRectangle(b, r.X, r.Bottom - 29, r.Width, 29);
            g.Restore(st);
            if (reveal < 1f) { st = g.Save(); g.SetClip(lane == 1 ? new Rectangle(r.X, r.Y, (int)Math.Ceiling(r.Width * reveal), r.Height + StripH) : new Rectangle(r.Right - (int)Math.Ceiling(r.Width * reveal), r.Y, r.Width, r.Height + StripH)); }

            if (choice != 0 && choice != 3)
            {
                using (var p = new Pen(choice == 2 || hover ? Choice : Color.FromArgb(150, Choice), hover ? 4 : 3) { DashStyle = choice == 2 || hover ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawRectangle(p, r.X + 1.5f, r.Y + 1.5f, r.Width - 3, r.Height - 3);
                using (var f = MakeFont(30, FontStyle.Bold))
                using (var sf = Center())
                using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                {
                    var tr = new RectangleF(r.X, r.Y + r.Height * 0.30f, r.Width, 40);
                    tr.Offset(2, 2);
                    g.DrawString("YOU?", f, shadow, tr, sf);
                    tr.Offset(-2, -2);
                    g.DrawString("YOU?", f, Brushes.White, tr, sf);
                }
                if (choice == 2)
                    using (var f = MakeFont(13, FontStyle.Bold))
                    using (var b = new SolidBrush(Choice))
                    using (var sf = Center())
                        g.DrawString("SUGGESTED", f, b, new RectangleF(r.X, r.Y + 8, r.Width, 18), sf);
            }
            else if ((e != null && e.Player) || choice == 3)
                using (var p = new Pen(Gold, 3)) g.DrawRectangle(p, r.X + 1.5f, r.Y + 1.5f, r.Width - 3, r.Height - 3);
            DrawGain(g, strip, e != null ? e.Gained : gainedIfChosen, ghost);
            if (reveal < 1f) g.Restore(st);
        }

        void DrawGain(Graphics g, Rectangle strip, int gained, bool ghost)
        {
            using (var f = MakeFont(19, FontStyle.Bold))
            using (var sf = Center())
            using (var white = new SolidBrush(Color.FromArgb(ghost ? 150 : 255, 255, 255, 255)))
            {
                if (gained == 0) { g.DrawString("-", f, white, strip, sf); return; }
                string txt = (gained > 0 ? "+" : "") + gained;
                SizeF sz = g.MeasureString(txt, f);
                float tri = 14, total = tri + 8 + sz.Width, x = strip.X + (strip.Width - total) / 2, cy = strip.Y + strip.Height / 2f;
                PointF[] pts = gained > 0
                    ? new[] { new PointF(x, cy + tri * 0.45f), new PointF(x + tri, cy + tri * 0.45f), new PointF(x + tri / 2, cy - tri * 0.5f) }
                    : new[] { new PointF(x, cy - tri * 0.45f), new PointF(x + tri, cy - tri * 0.45f), new PointF(x + tri / 2, cy + tri * 0.5f) };
                using (var b = new SolidBrush(Color.FromArgb(ghost ? 150 : 255, gained > 0 ? Up : Down))) g.FillPolygon(b, pts);
                g.DrawString(txt, f, white, new RectangleF(x + tri + 4, strip.Y, sz.Width + 8, strip.Height), new StringFormat { LineAlignment = StringAlignment.Center });
            }
        }

        public void DrawRowBadge(Graphics g, int row, int y)
        {
            float cx = W / 2f, cy = y + PanelH * 0.5f, rad = 25;
            using (var b = new SolidBrush(Color.FromArgb(235, 8, 8, 10))) g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
            using (var p = new Pen(Color.FromArgb(150, 150, 156), 1.5f)) g.DrawEllipse(p, cx - rad, cy - rad, rad * 2, rad * 2);
            using (var small = MakeFont(11))
            using (var big = MakeFont(row > 9 ? 19 : 22))
            using (var sf = Center())
            {
                g.DrawString("ROW", small, Brushes.White, new RectangleF(cx - rad, cy - 17, rad * 2, 14), sf);
                g.DrawString(row.ToString(), big, Brushes.White, new RectangleF(cx - rad, cy - 4, rad * 2, 22), sf);
            }
        }

        Color TintOf(int num)
        {
            Color? c = Art.ColorOf(num);
            return c.HasValue ? c.Value : Palette[Math.Abs(num * 7 + num / 10) % Palette.Length];
        }

        // halign/valign: 0 start, 1 centre, 2 end
        void DrawFit(Graphics g, Image img, RectangleF box, int halign, int valign, float alpha)
        {
            Bitmap b = Art.Fit(img, (int)(box.Width * Scale), (int)(box.Height * Scale));
            if (b == null) return;
            float w = b.Width / Scale, h = b.Height / Scale;
            float x = halign == 0 ? box.X : halign == 1 ? box.X + (box.Width - w) / 2 : box.Right - w;
            float y = valign == 0 ? box.Y : valign == 1 ? box.Y + (box.Height - h) / 2 : box.Bottom - h;
            if (alpha >= 1f) { g.DrawImage(b, x, y, w, h); return; }
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                g.DrawImage(b, new Rectangle((int)x, (int)y, (int)Math.Round(w), (int)Math.Round(h)), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // no number art: the number in big slanted figures, outlined
        void DrawNumberText(Graphics g, int num, RectangleF box, Color tint, int lane, float alpha)
        {
            Color fill = Luma(tint) > 0.75 ? Color.FromArgb(20, 20, 24) : Color.White;
            using (var path = new GraphicsPath())
            using (var ff = new FontFamily(family))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                path.AddString(num.ToString(), ff, (int)(FontStyle.Bold | FontStyle.Italic), 100f, PointF.Empty, sf);
                RectangleF bb = path.GetBounds();
                float k = Math.Min(box.Width / bb.Width, box.Height / bb.Height);
                float x = lane == 1 ? box.X : box.Right - bb.Width * k, y = box.Y + (box.Height - bb.Height * k) / 2;
                using (var m = new Matrix())
                {
                    m.Translate(x, y);
                    m.Scale(k, k);
                    m.Translate(-bb.X, -bb.Y);
                    path.Transform(m);
                }
                using (var p = new Pen(Color.FromArgb((int)(220 * alpha), 0, 0, 0), 4) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * alpha), fill))) g.FillPath(b, path);
            }
        }

        static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255; }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static float Ease(float t) { t = Math.Max(0f, Math.Min(1f, t)); return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2; }
    }

    // Where the board goes and how big: Scale on top of "86% of the game window's height", the top-left corner as
    // a share of the game window (NaN = left edge, centred), and the seconds each full page holds before it scrolls.
    public class BoardLayout
    {
        public double Scale = 1, X = double.NaN, Y = double.NaN, Hold = 3.5;

        public BoardLayout Copy() { return (BoardLayout)MemberwiseClone(); }

        public Point Place(Rectangle game, Size size)
        {
            int x = double.IsNaN(X) ? game.Left + Math.Max(8, game.Width / 50) : game.Left + (int)Math.Round(X * game.Width);
            int y = double.IsNaN(Y) ? game.Top + (game.Height - size.Height) / 2 : game.Top + (int)Math.Round(Y * game.Height);
            Rectangle scr = Screen.FromRectangle(game).Bounds;
            x = Math.Max(scr.Left - size.Width + 40, Math.Min(scr.Right - 40, x));
            y = Math.Max(scr.Top, Math.Min(scr.Bottom - 40, y));
            return new Point(x, y);
        }

        public void Remember(Rectangle game, Point at, Size size)
        {
            X = (double)(at.X - game.Left) / game.Width;
            Y = (double)(at.Y - game.Top) / game.Height;
        }

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        public override string ToString()
        {
            return string.Format(Inv, "{0:0.00},{1},{2},{3:0.0}", Scale, double.IsNaN(X) ? "" : X.ToString("0.0000", Inv), double.IsNaN(Y) ? "" : Y.ToString("0.0000", Inv), Hold);
        }

        public static BoardLayout Parse(string v)
        {
            var l = new BoardLayout();
            string[] p = (v ?? "").Split(',');
            double d;
            if (p.Length > 0 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, Inv, out d) && d > 0.2 && d < 3) l.Scale = d;
            if (p.Length > 1 && double.TryParse(p[1], System.Globalization.NumberStyles.Float, Inv, out d)) l.X = d;
            if (p.Length > 2 && double.TryParse(p[2], System.Globalization.NumberStyles.Float, Inv, out d)) l.Y = d;
            if (p.Length > 3 && double.TryParse(p[3], System.Globalization.NumberStyles.Float, Inv, out d) && d >= 0.5 && d <= 30) l.Hold = d;
            return l;
        }
    }

    // A window that sits over the game without taking the focus or the mouse.
    public class OverlayForm : Form
    {
        protected readonly bool clickThrough;
        public OverlayForm(bool clickThrough)
        {
            this.clickThrough = clickThrough;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation { get { return clickThrough; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x8;                                    // tool window, topmost
                if (clickThrough) cp.ExStyle |= 0x08000000 | 0x20 | 0x80000; // no activate, transparent to the mouse, layered
                return cp;
            }
        }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        public static Rectangle GameRect(IntPtr h)
        {
            RECT r;
            if (h != IntPtr.Zero && GetWindowRect(h, out r) && r.Right - r.Left > 200 && r.Bottom - r.Top > 200)
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return Screen.PrimaryScreen.Bounds;
        }
    }

    // The board everyone sees while the cars choose: five rows at a time, filled in as the cars pick their lane; when
    // a page is complete it holds, then the rows slide up and away for the next five. Closes a few seconds after the
    // last page (or soon after the green).
    public class ConeBoardForm : OverlayForm
    {
        const int PageRows = 5;
        const double LastHold = 6, Slide = 0.7, Pop = 0.35;

        public readonly ConeBoardData Data;
        readonly ConeBoardPainter painter;
        readonly Timer timer = new Timer { Interval = 30 };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly Dictionary<int, double> seen = new Dictionary<int, double>();
        int page;
        double readySince = -1, slideStart = -1, fadeStart = -1;
        public bool Finished { get; private set; }

        double Now { get { return clock.Elapsed.TotalSeconds; } }
        static int BoardH { get { return ConeBoardPainter.RowsTop + PageRows * ConeBoardPainter.RowH + ConeBoardPainter.Pad - ConeBoardPainter.RowGap + 4; } }

        readonly BoardLayout layout;
        readonly Rectangle game;
        readonly bool edit;
        Point dragFrom;
        public event Action<BoardLayout> Saved;  // edit mode: Enter or double-click

        // edit = a still board to place: drag it, the mouse wheel sizes it, Enter / double-click saves, Esc cancels.
        public ConeBoardForm(ConeBoardData data, ConeArt art, Rectangle game, BoardLayout layout, bool edit = false) : base(!edit)
        {
            Data = data;
            this.game = game;
            this.layout = layout.Copy();
            this.edit = edit;
            painter = new ConeBoardPainter(art);
            art.Prepare(data.Inside.Concat(data.Outside).Select(e => e.Num));
            ApplyScale();
            Location = this.layout.Place(game, Size);
            Opacity = 0.97;
            if (edit)
            {
                PreviewFirst = 0;
                KeyPreview = true;
                Cursor = Cursors.SizeAll;
                MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) dragFrom = e.Location; };
                MouseMove += (o, e) => { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y); };
                MouseWheel += (o, e) =>
                {
                    this.layout.Scale = Math.Max(0.4, Math.Min(2.0, this.layout.Scale + (e.Delta > 0 ? 0.05 : -0.05)));
                    ApplyScale();
                    Invalidate();
                };
                DoubleClick += (o, e) => Save();
                KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) Save(); else if (e.KeyCode == Keys.Escape) Finish(); };
                return;
            }
            timer.Tick += (o, e) => Step();
            timer.Start();
        }

        void ApplyScale()
        {
            float s = (float)Math.Max(0.25, Math.Min(2.5, game.Height * 0.86 / BoardH * layout.Scale));
            painter.Scale = s;
            Size = new Size((int)(ConeBoardPainter.W * s), (int)(BoardH * s));
        }

        void Save()
        {
            layout.Remember(game, Location, Size);
            if (Saved != null) Saved(layout);
            Finish();
        }

        void Step()
        {
            double now = Now;
            int filled = Math.Min(Data.Inside.Count, Data.Outside.Count), total = Math.Max(Data.Inside.Count, Data.Outside.Count);
            foreach (ConeEntry e in Data.Inside.Concat(Data.Outside))
                if (!seen.ContainsKey(e.Idx)) seen[e.Idx] = e.Row > page * PageRows && e.Row <= (page + 1) * PageRows ? now : -10;
            if (slideStart >= 0)
            {
                if (now - slideStart >= Slide) { page++; slideStart = -1; readySince = -1; }
            }
            else if (fadeStart < 0)
            {
                bool complete = Data.Done || filled >= (page + 1) * PageRows;
                if (complete && readySince < 0) readySince = now;
                bool more = total > (page + 1) * PageRows || !Data.Done;
                if (complete && more && now - readySince > layout.Hold) slideStart = now;
                if (complete && !more && now - readySince > LastHold) fadeStart = now;
            }
            if (fadeStart >= 0)
            {
                double t = (now - fadeStart) / 0.5;
                if (t >= 1) { Finish(); return; }
                Opacity = 0.97 * (1 - t);
            }
            Invalidate();
        }

        // Taken down early (the green, a new caution, or the option turned off).
        public void Finish()
        {
            if (Finished) return;
            Finished = true;
            timer.Stop();
            Close();
        }

        public float PreviewFirst = float.NaN;    // for pictures of the board: the first row shown (0 = row 1)

        protected override void OnPaint(PaintEventArgs pe) { Render(pe.Graphics); }

        public void Render(Graphics g)
        {
            ConeBoardPainter.Smooth(g);
            g.ScaleTransform(painter.Scale, painter.Scale);
            int h = BoardH;
            painter.DrawFrame(g, h);
            double now = Now;
            float first = page * PageRows + (slideStart >= 0 ? ConeBoardPainter.Ease((float)((now - slideStart) / Slide)) * PageRows : 0);
            if (!float.IsNaN(PreviewFirst)) { first = PreviewFirst; now += 100; }
            int top = ConeBoardPainter.RowsTop;
            GraphicsState st = g.Save();
            g.SetClip(new Rectangle(0, top, ConeBoardPainter.W, h - top - 2));
            int from = (int)Math.Floor(first) + 1, to = (int)Math.Ceiling(first) + PageRows;
            for (int row = from; row <= to; row++)
            {
                int y = top + (int)Math.Round((row - 1 - first) * ConeBoardPainter.RowH);
                ConeEntry ins = Data.Inside.Count >= row ? Data.Inside[row - 1] : null;
                ConeEntry outs = Data.Outside.Count >= row ? Data.Outside[row - 1] : null;
                if (row > Math.Max(Math.Max(Data.Inside.Count, Data.Outside.Count), Data.Done ? 0 : (Data.Cars + 1) / 2)) continue; // no such row
                painter.DrawCell(g, 1, y, ins, Reveal(ins, now), 0, 0, 0, false);
                painter.DrawCell(g, 2, y, outs, Reveal(outs, now), 0, 0, 0, false);
                painter.DrawRowBadge(g, row, y);
            }
            g.Restore(st);
            painter.DrawHeader(g, Data.Title ?? "CHOOSE CONE");
            if (edit)
            {
                var r = new Rectangle(ConeBoardPainter.Pad + 10, h / 2 - 60, ConeBoardPainter.W - 2 * ConeBoardPainter.Pad - 20, 120);
                using (var b = new SolidBrush(Color.FromArgb(225, 0, 0, 0))) g.FillRectangle(b, r);
                using (var p = new Pen(Color.FromArgb(60, 220, 110), 2)) g.DrawRectangle(p, r);
                using (var f = painter.MakeFont(17, FontStyle.Bold))
                using (var f2 = painter.MakeFont(14))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("PLACE THE BOARD  " + Math.Round(layout.Scale * 100) + "%", f, Brushes.White, new RectangleF(r.X, r.Y + 8, r.Width, 26), sf);
                    g.DrawString("drag to move, mouse wheel to size\nEnter or double-click to save, Esc to cancel", f2, Brushes.Gainsboro, new RectangleF(r.X, r.Y + 38, r.Width, 70), sf);
                }
            }
        }

        float Reveal(ConeEntry e, double now)
        {
            double t;
            if (e == null || !seen.TryGetValue(e.Idx, out t)) return 1f;
            return ConeBoardPainter.Ease((float)((now - t) / Pop));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
