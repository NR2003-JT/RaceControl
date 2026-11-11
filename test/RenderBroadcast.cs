// Offscreen render of the broadcast overlay with sample data. Build like RenderQualTab.cs with /main:B.
// Offscreen render of the broadcast overlay with the Figma frame's sample data (red = slower than pole, lime = on pace).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

static class B
{
    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        var names = new[] { new[] { "10", "ALEX", "PALOU", "38.402" }, new[] { "2", "JOSEF", "NEWGARDEN", "38.512" }, new[] { "5", "PATO", "O'WARD", "38.584" },
            new[] { "26", "COLTON", "HERTA", "38.612" }, new[] { "20", "ALEX", "ROSSI", "38.648" }, new[] { "27", "KYLE", "KIRKWOOD", "38.671" },
            new[] { "8", "MARCUS", "ERICSSON", "38.702" }, new[] { "3", "SCOTT", "McLAUGHLIN", "38.731" }, new[] { "28", "ROMAIN", "GROSJEAN", "38.764" },
            new[] { "21", "RINUS", "VEEKAY", "38.794" } };
        {
            var fs = new RaceControl.QualBroadcastForm(null, new Rectangle(0, 0, 1920, 1080), new RaceControl.BoardLayout());
            fs.Location = new Point(-5000, -5000); fs.Data = RaceControl.QualBroadcastForm.Sample(); fs.Data.Ahead = true;
            fs.Show(); Application.DoEvents();
            using (var bmp = new Bitmap(fs.Width, fs.Height)) { fs.DrawToBitmap(bmp, new Rectangle(0, 0, fs.Width, fs.Height)); bmp.Save(System.IO.Path.Combine(outDir, "broadcast_sample.png")); }
            fs.Close();
        }
        foreach (bool ahead in new[] { false, true })
            foreach (double order in new[] { 0.0, 1.0 })
            {
                var f = new RaceControl.QualBroadcastForm(null, new Rectangle(0, 0, 1920, 1080), new RaceControl.BoardLayout());
                f.Location = new Point(-5000, -5000);
                var d = new RaceControl.QualBroadcastData { Track = "INDIANAPOLIS", Num = 10, First = "ALEX", Last = "PALOU", Tele = true, Mph = 234, Rpm = 11940,
                    RpmTop = 12500, Throttle = 1, Brake = 0, Gear = 6, LapNo = 3, LapsTotal = 4, Current = "24.176", LastLap = "38.402", BestLap = "38.402",
                    Ahead = ahead, ShowOrder = order == 0 };
                if (!ahead) d.LapRows = new List<string> { "38.402", "38.402", "38.402", "38.402" };
                foreach (var n in names) d.Order.Add(n);
                f.Data = d;
                for (int i = 0; i < 40; i++) { f.Animate(d.ShowOrder); System.Threading.Thread.Sleep(15); }
                f.Show(); Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(outDir + "\\broadcast_" + (ahead ? "lime" : "red") + (order == 0 ? "" : "_ordertucked") + ".png");
                }
                f.Close();
            }
    }
}
