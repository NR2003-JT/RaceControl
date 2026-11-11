using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RaceControl
{
    // Pausing and resuming NR2003 by pressing its pause key for the player (NR2003 reads the keyboard through
    // DirectInput, so the key goes in as a scan code with the game window in front). The game must run windowed or
    // borderless for the chart to show over it.
    public static class GamePause
    {
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit, Size = 28)]
        struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(4)] public KEYBDINPUT ki; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

        const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_SCANCODE = 8;

        // "Pause" (default) or a key name Windows knows, such as P or F9.
        public static string KeyName = "Pause";

        // NR2003's own pause and resume (what its pause key runs): pause stamps the time and sets the sim state
        // to 3, resume moves the clocks on by the time paused and sets it back to 2. Called in the game when the
        // code is the build Race Control knows; the pause key otherwise.
        const long PauseFn = 0x5464E0, ResumeFn = 0x5465E0;
        static readonly int[] PauseCode = { 0xE8, -1, -1, -1, -1, 0x6A, 0x03, 0xA3, 0xA8, 0xBC, 0xBE, 0x00, 0xE8 };
        static readonly int[] ResumeCode = { 0x53, 0x56, 0x57, 0xE8, -1, -1, -1, -1, 0x8B, 0x15, 0xA8, 0xBC, 0xBE, 0x00 };

        static bool Matches(ProcessMemory mem, long at, int[] code)
        {
            try
            {
                for (int i = 0; i < code.Length; i++) if (code[i] >= 0 && mem.U8(at + i) != code[i]) return false;
                return true;
            }
            catch (GameMemoryException) { return false; }
        }

        // Returns how the game was paused.
        public static string Pause(ProcessMemory mem)
        {
            if (Matches(mem, PauseFn, PauseCode) && mem.CallGame(PauseFn)) return "through its own pause";
            Press(mem.Process);
            return "with the pause key";
        }

        // Resumes and hands the keyboard back to the game so the player drives on.
        public static void Resume(ProcessMemory mem)
        {
            if (mem == null) return;
            if (!(Matches(mem, ResumeFn, ResumeCode) && mem.CallGame(ResumeFn))) { Press(mem.Process); return; }
            try { if (mem.Process.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(mem.Process.MainWindowHandle); } catch { }
        }

        public static void Press(Process game)
        {
            if (game != null && game.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(game.MainWindowHandle);
            ushort scan;
            uint ext = 0;
            if (string.Equals(KeyName, "Pause", StringComparison.OrdinalIgnoreCase)) { scan = 0x45; ext = KEYEVENTF_EXTENDEDKEY; } // DIK_PAUSE
            else
            {
                Keys k;
                if (!Enum.TryParse(KeyName, true, out k)) k = Keys.Pause;
                scan = (ushort)MapVirtualKey((uint)k, 0);
            }
            var down = new INPUT { type = 1, ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | ext } };
            var up = new INPUT { type = 1, ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | ext | KEYEVENTF_KEYUP } };
            SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
            System.Threading.Thread.Sleep(60);
            SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    // The player's turn at the cone, drawn like the board: the four rows ahead (whoever is in them, empty spots
    // included) and the spot each lane gets the player, marked YOU?. Click one (or Left = inside, Right = outside,
    // Enter = the suggested lane); a 2 s countdown, then the game goes on by itself.
    public class ConeChooser : OverlayForm
    {
        const int FooterH = 58;
        readonly ConeOffer offer;
        readonly Action<int> chosen;
        readonly Action resume;
        readonly ConeBoardPainter painter;
        readonly Timer timer = new Timer { Interval = 50 };
        readonly int first, last, insRow, outRow;
        int lane, hover;
        DateTime resumeAt;

        public ConeChooser(ConeOffer offer, ConeArt art, Rectangle game, double scale, Action<int> chosen, Action resume) : base(false)
        {
            this.offer = offer;
            this.chosen = chosen;
            this.resume = resume;
            painter = new ConeBoardPainter(art);
            art.Prepare(offer.InsideCars.Concat(offer.OutsideCars).Select(e => e.Num).Concat(new[] { offer.PlayerNum }));
            KeyPreview = true;
            insRow = offer.InsideCars.Count + 1;
            outRow = offer.OutsideCars.Count + 1;
            int longest = Math.Max(offer.InsideCars.Count, offer.OutsideCars.Count);
            first = Math.Max(1, Math.Min(longest - 3, Math.Min(insRow, outRow)));
            last = Math.Max(insRow, outRow);
            int h = BoardH;
            float s = (float)Math.Max(0.25, Math.Min(2.5, game.Height * 0.86 / Math.Max(h, ConeBoardPainter.RowsTop + 5 * ConeBoardPainter.RowH + FooterH) * scale));
            painter.Scale = s;
            Size = new Size((int)(ConeBoardPainter.W * s), (int)(h * s));
            Location = new Point(game.Left + (game.Width - Width) / 2, game.Top + (game.Height - Height) / 2);

            KeyDown += (o, e) =>
            {
                if (lane != 0) return;
                if (e.KeyCode == Keys.Left) Choose(1);
                else if (e.KeyCode == Keys.Right) Choose(2);
                else if (e.KeyCode == Keys.Enter) Choose(offer.Recommended);
            };
            MouseMove += (o, e) => { int l = HitLane(e.Location); if (l != hover) { hover = l; Cursor = l != 0 && lane == 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } };
            MouseClick += (o, e) => { int l = HitLane(e.Location); if (l != 0) Choose(l); };
            timer.Tick += (o, e) => CountdownTick();
        }

        int BoardH { get { return ConeBoardPainter.RowsTop + (last - first + 1) * ConeBoardPainter.RowH + FooterH; } }
        int RowY(int row) { return ConeBoardPainter.RowsTop + (row - first) * ConeBoardPainter.RowH; }
        int ToPos(int l) { return l == 1 ? 2 * insRow - 1 : 2 * outRow; }
        int GainFor(int l) { return offer.PlayerFrom <= 1 ? 0 : offer.PlayerFrom - ToPos(l); }

        int HitLane(Point p)
        {
            if (lane != 0) return 0;
            var lp = new PointF(p.X / painter.Scale, p.Y / painter.Scale);
            for (int l = 1; l <= 2; l++)
            {
                Rectangle r = ConeBoardPainter.PanelRect(l, RowY(l == 1 ? insRow : outRow));
                r.Height += ConeBoardPainter.StripH;
                if (r.Contains(Point.Round(lp))) return l;
            }
            return 0;
        }

        protected override void OnPaint(PaintEventArgs pe) { Render(pe.Graphics); }

        public void PreviewChoose(int which) { lane = which; resumeAt = DateTime.Now.AddSeconds(1.5); }

        public void Render(Graphics g)
        {
            ConeBoardPainter.Smooth(g);
            g.ScaleTransform(painter.Scale, painter.Scale);
            int h = BoardH;
            painter.DrawFrame(g, h);
            painter.DrawHeader(g, offer.Title ?? "CHOOSE CONE");
            for (int row = first; row <= last; row++)
            {
                int y = RowY(row);
                for (int l = 1; l <= 2; l++)
                {
                    List<ConeEntry> cars = l == 1 ? offer.InsideCars : offer.OutsideCars;
                    int myRow = l == 1 ? insRow : outRow;
                    ConeEntry e = row <= cars.Count ? cars[row - 1] : null;
                    int choice = 0;
                    if (row == myRow && lane == 0) choice = offer.Recommended == l ? 2 : 1;
                    else if (row == myRow && lane == l) choice = 3;
                    painter.DrawCell(g, l, y, e, 1f, choice, offer.PlayerNum, GainFor(l), hover == l);
                }
                painter.DrawRowBadge(g, row, y);
            }
            var foot = new Rectangle(ConeBoardPainter.Pad, h - FooterH + 2, ConeBoardPainter.W - 2 * ConeBoardPainter.Pad, FooterH - ConeBoardPainter.Pad - 2);
            g.FillRectangle(Brushes.Black, foot);
            using (var big = painter.MakeFont(17, FontStyle.Bold))
            using (var small = painter.MakeFont(12))
            using (var dim = new SolidBrush(Color.FromArgb(170, 176, 186)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                string head, sub;
                if (lane == 0)
                {
                    head = "GAME PAUSED - PICK YOUR LANE";
                    sub = "click a YOU? spot   ◀ inside   outside ▶   Enter = suggested";
                }
                else
                {
                    double left = Math.Max(0, (resumeAt - DateTime.Now).TotalSeconds);
                    head = (lane == 1 ? "INSIDE" : "OUTSIDE") + ", ROW " + (lane == 1 ? insRow : outRow) + " - RESUMING IN " + Math.Ceiling(left).ToString("0");
                    sub = "hands on the wheel: you drive again when the game resumes";
                }
                g.DrawString(head, big, Brushes.White, new RectangleF(foot.X, foot.Y + 2, foot.Width, 24), sf);
                g.DrawString(sub, small, dim, new RectangleF(foot.X, foot.Y + 24, foot.Width, 18), sf);
            }
        }

        void Choose(int which)
        {
            if (lane != 0) return;
            lane = which;
            hover = 0;
            Cursor = Cursors.Default;
            chosen(which);
            resumeAt = DateTime.Now.AddSeconds(2);
            timer.Start();
            Invalidate();
        }

        void CountdownTick()
        {
            Invalidate();
            if ((resumeAt - DateTime.Now).TotalSeconds > 0) return;
            timer.Stop();
            Hide();
            resume();
            Close();
        }

        // Closed from outside (the engine chose after a timeout): no resume key, the game was not paused.
        public void CloseQuietly()
        {
            timer.Stop();
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
