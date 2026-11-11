using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace RaceControl
{
    // A call to the player over the game picture: a coloured flag panel with one line of text, and on the right a
    // countdown when there is one (at the green in, pull out in, time left). Click-through, never takes the focus.
    // Qualifying uses it now (your turn, timed lap, come in, checkered, through / out, DNQ); the planned wave around and
    // free pass calls use the same window (see BUILD NOTES - Claude plans, player flags).
    // edit = a sample banner to place: drag it, the mouse wheel sizes it, Enter / double-click saves, Esc cancels.
    public class PlayerBannerForm : OverlayForm
    {
        public enum Kind { Info = 0, Go = 1, ComeIn = 2, Out = 3, WaveAround = 4, FreePass = 5 }
        public const int W = 900, H = 64, TimerW = 230;
        readonly Rectangle game;
        readonly string family;
        readonly BoardLayout layout;
        readonly bool edit;
        Point dragFrom;
        float scale = 1f;
        string text = "", timerLabel = "", timerValue = "";
        bool timerUrgent;
        Kind kind;
        public event Action<BoardLayout> Saved;

        public PlayerBannerForm(Rectangle game, BoardLayout layout, bool edit = false) : base(!edit)
        {
            this.game = game;
            this.layout = (layout ?? new BoardLayout()).Copy();
            this.edit = edit;
            var names = new System.Collections.Generic.HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            family = names.Contains("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
            ApplyScale();
            // not placed yet: centred across the top of the game
            Location = double.IsNaN(this.layout.X) || double.IsNaN(this.layout.Y)
                ? new Point(game.Left + (game.Width - Width) / 2, game.Top + (int)(game.Height * 0.07))
                : this.layout.Place(game, Size);
            Opacity = 0.95;
            if (!edit) return;
            text = "YOUR RUN: GO NOW - out lap, then 1 timed lap"; kind = Kind.Go; timerLabel = "AT THE GREEN IN"; timerValue = "0:42";
            KeyPreview = true;
            Cursor = Cursors.SizeAll;
            MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) dragFrom = e.Location; };
            MouseMove += (o, e) => { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y); };
            MouseWheel += (o, e) =>
            {
                this.layout.Scale = Math.Max(0.4, Math.Min(2.0, this.layout.Scale + (e.Delta > 0 ? 0.05 : -0.05)));
                var mid = new Point(Left + Width / 2, Top + Height / 2);
                ApplyScale();
                Location = new Point(mid.X - Width / 2, mid.Y - Height / 2);
                Invalidate();
            };
            DoubleClick += (o, e) => Save();
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) Save(); else if (e.KeyCode == Keys.Escape) Close(); };
        }

        void ApplyScale()
        {
            scale = (float)Math.Max(0.4, Math.Min(2.5, game.Height / 1080.0 * layout.Scale));
            Size = new Size((int)(W * scale), (int)(H * scale));
        }

        void Save()
        {
            layout.Remember(game, Location, Size);
            if (Saved != null) Saved(layout);
            Close();
        }

        // timer: "LABEL|VALUE" ("" = none); urgent = drawn red (a deadline close)
        public void Set(string t, int k, string timer = "", bool urgent = false)
        {
            if (edit) return;
            string tl = "", tv = "";
            if (!string.IsNullOrEmpty(timer)) { int bar = timer.IndexOf('|'); tl = bar < 0 ? "" : timer.Substring(0, bar); tv = bar < 0 ? timer : timer.Substring(bar + 1); }
            if (t == text && (Kind)k == kind && tl == timerLabel && tv == timerValue && urgent == timerUrgent) return;
            text = t ?? ""; kind = (Kind)k; timerLabel = tl; timerValue = tv; timerUrgent = urgent;
            Invalidate();
        }

        static Color Back(Kind k)
        {
            switch (k)
            {
                case Kind.Go: return Color.FromArgb(24, 150, 70);
                case Kind.ComeIn: return Color.FromArgb(230, 180, 20);
                case Kind.Out: return Color.FromArgb(200, 40, 36);
                case Kind.WaveAround: return Color.FromArgb(30, 100, 210);
                case Kind.FreePass: return Color.FromArgb(230, 180, 20);
                default: return Color.FromArgb(28, 30, 36);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Color bg = Back(kind);
            g.Clear(bg);
            // a checkered strip on the left: the flag the call stands for
            int sq = (int)(H * scale / 4);
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 3; c++)
                    using (var b = new SolidBrush((r + c) % 2 == 0 ? Color.White : Color.Black))
                        if (kind == Kind.ComeIn) g.FillRectangle(b, c * sq, r * sq, sq, sq);
            int left = (kind == Kind.ComeIn) ? 3 * sq + (int)(14 * scale) : (int)(18 * scale);
            // the countdown: a dark panel on the right, a small label over a big m:ss
            int timerW = timerValue.Length > 0 ? (int)(TimerW * scale) : 0;
            if (timerW > 0)
            {
                var box = new Rectangle(Width - timerW, 0, timerW, Height);
                using (var b = new SolidBrush(timerUrgent ? Color.FromArgb(200, 40, 36) : Color.FromArgb(20, 22, 26))) g.FillRectangle(b, box);
                var mid = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
                using (var f = new Font(family, 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(200, 205, 215)))
                    g.DrawString(timerLabel, f, b, new RectangleF(box.Left, 4 * scale, box.Width, 20 * scale), mid);
                using (var f = new Font(family, 30f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.White))
                    g.DrawString(timerValue, f, b, new RectangleF(box.Left, 20 * scale, box.Width, Height - 22 * scale), mid);
            }
            Color fg = kind == Kind.ComeIn || kind == Kind.FreePass ? Color.Black : Color.White;
            using (var f = new Font(family, 22f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(fg))
            {
                var rect = new RectangleF(left, 0, Width - left - timerW - 12 * scale, Height);
                var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(text.ToUpperInvariant(), f, b, rect, fmt);
            }
            using (var p = new Pen(Color.FromArgb(90, 0, 0, 0), 2)) g.DrawRectangle(p, 1, 1, Width - 2, Height - 2);
            if (edit)
                using (var p = new Pen(Color.White, 2) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, 2, 2, Width - 5, Height - 5);
        }
    }

    // Your qualifying turn: the game is paused and this card says so, until you are ready (set your camera first -
    // RaceVision puts the TV in your car meanwhile). It never takes the keyboard, so the game's keys (camera, car
    // focus) still work: resume with the game's own pause key, or click RESUME here. The pull-out time starts then.
    public class QualReadyForm : OverlayForm
    {
        const int W = 640, H = 250;
        readonly string call;
        readonly double dnq;
        readonly Action resume;
        Rectangle button;
        bool hover;

        public QualReadyForm(Rectangle game, string call, double dnqSeconds, Action resume) : base(false)
        {
            this.call = call; dnq = dnqSeconds; this.resume = resume;
            Size = new Size(W, H);
            Location = new Point(game.Left + (game.Width - W) / 2, game.Top + (int)(game.Height * 0.62) - H / 2);
            button = new Rectangle(W / 2 - 110, H - 66, 220, 44);
            Paint += (o, e) => Render(e.Graphics);
            MouseMove += (o, e) => { bool h = button.Contains(e.Location); if (h != hover) { hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); } };
            MouseClick += (o, e) => { if (button.Contains(e.Location)) resume(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }   // the game keeps the keyboard

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; }   // WS_EX_NOACTIVATE
        }

        void Render(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var bg = new SolidBrush(Color.FromArgb(18, 20, 24))) g.FillRectangle(bg, 0, 0, W, H);
            using (var bar = new SolidBrush(Color.FromArgb(0, 170, 80))) g.FillRectangle(bar, 0, 0, W, 8);
            using (var edge = new Pen(Color.FromArgb(60, 64, 72), 2)) g.DrawRectangle(edge, 1, 1, W - 3, H - 3);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var big = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var mid = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font("Segoe UI", 15f, GraphicsUnit.Pixel))
            using (var dim = new SolidBrush(Color.FromArgb(180, 186, 196)))
            {
                g.DrawString("YOUR QUALIFYING RUN - GAME PAUSED", big, Brushes.White, new RectangleF(0, 18, W, 36), sf);
                g.DrawString(call, mid, new SolidBrush(Color.FromArgb(0, 210, 106)), new RectangleF(0, 58, W, 26), sf);
                g.DrawString("Set your camera now (your cockpit). The game's keys still work while this shows.", small, dim, new RectangleF(10, 94, W - 20, 22), sf);
                g.DrawString("Resume with your pause key or click RESUME: you then have " + dnq.ToString("0") + " s to pull out.", small, dim, new RectangleF(10, 118, W - 20, 22), sf);
                using (var b = new SolidBrush(hover ? Color.FromArgb(0, 200, 96) : Color.FromArgb(0, 150, 72))) g.FillRectangle(b, button);
                g.DrawString("RESUME", mid, Brushes.White, button, sf);
            }
        }
    }
}
