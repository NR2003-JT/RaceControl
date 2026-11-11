// Offscreen render of the Qualifying tab (stops the timers, but it still ATTACHES to a running NR2003 - only run it with the game closed).
// Build: csc /target:exe /platform:x86 /main:D (all RaceControl src files) testRenderQualTab.cs; run: D.exe <outdir>
// Offscreen render of RaceControl's Qualifying tab: builds the window (no message loop, so its timers never run and it
// never attaches to NR2003), draws the tab at two window sizes, top and scrolled to the bottom.
using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

static class D
{
    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        var f = new RaceControl.MainForm();
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-5000, -5000);
        f.ShowInTaskbar = false;
        // never attach to the game: both timers stopped before the window is shown
        foreach (string t in new[] { "tick", "slowTimer" })
        {
            var tm = (Timer)typeof(RaceControl.MainForm).GetField(t, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
            tm.Stop(); tm.Enabled = false;
        }
        f.Show();
        Application.DoEvents();
        var tabs = (TabControl)typeof(RaceControl.MainForm).GetField("tabs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
        TabPage q = null;
        foreach (TabPage p in tabs.TabPages) if (p.Text == "Qualifying") q = p;
        foreach (var size in new[] { new Size(660, 760), new Size(660, 1040) })
        {
            f.Size = size;
            f.CreateControl();
            tabs.SelectedTab = q;
            f.PerformLayout(); q.PerformLayout(); Application.DoEvents();
            q.AutoScrollPosition = new Point(0, 0); Application.DoEvents();
            Shot(f, outDir + "\\qual_" + size.Height + "_top.png");
            q.AutoScrollPosition = new Point(0, 5000);
            q.PerformLayout(); Application.DoEvents();
            Shot(f, outDir + "\\qual_" + size.Height + "_bottom.png");
            var board = (ListBox)typeof(RaceControl.MainForm).GetField("qualBoard", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
            Console.WriteLine("{0}: page {1}x{2}, scroll range {3}, board top {4} height {5} (bottom {6})", size, q.ClientSize.Width, q.ClientSize.Height,
                q.DisplayRectangle.Height, board.Top, board.Height, board.Bottom);
            q.AutoScrollPosition = new Point(0, 0);
        }
    }

    static void Shot(Form f, string path)
    {
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(path);
        }
    }
}
