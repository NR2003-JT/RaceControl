using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RaceControl
{
    // READ-ONLY live check of the telemetry: attaches to NR2003 (admin), times laps and records every moving car for N
    // seconds, saves the laps (tele_laps.bin) and renders the in-game strip and the telemetry window to PNGs.
    //   TeleLive.exe <seconds> <outdir>        record live, save, render
    //   TeleLive.exe render <outdir>           render again from <outdir>\tele_laps.bin (no game needed)
    static class TeleLive
    {
        const long Driver0 = 14711636; const int DriverSize = 175, DriverNum = 156;

        [STAThread]
        static void Main(string[] args)
        {
            bool offline = args.Length > 0 && args[0] == "render";
            string outDir = args.Length > 1 ? args[1] : ".";
            var rec = new TeleRecorder();
            var names = new Dictionary<int, KeyValuePair<int, string>>();
            string file = Path.Combine(outDir, "tele_laps.bin");
            if (offline) Load(file, rec, names);
            else
            {
                double secs = double.Parse(args[0]);
                var m = ProcessMemory.Attach();
                var timing = new QualTiming();
                var moving = Enumerable.Range(0, 44).Where(c =>
                {
                    float vx = m.F32(0xACCA30 + c * Addr.Ocd + 6634L), vy = m.F32(0xACCA30 + c * Addr.Ocd + 6638L);
                    return Math.Sqrt(vx * vx + vy * vy) > 5;
                }).ToList();
                Console.WriteLine("cars: " + string.Join(" ", moving));
                var t0 = DateTime.Now; var lastT = DateTime.MinValue;
                while ((DateTime.Now - t0).TotalSeconds < secs)
                {
                    if ((DateTime.Now - lastT).TotalMilliseconds >= 100) { timing.Update(m, moving); lastT = DateTime.Now; }
                    rec.Sample(m, timing, moving);
                    Thread.Sleep(10);
                }
                foreach (int c in moving) names[c] = new KeyValuePair<int, string>(m.I32(Driver0 + c * DriverSize + DriverNum), m.Str(Driver0 + c * DriverSize + 16, 20).Trim().ToUpperInvariant());
                Save(file, rec, names);
            }
            Func<int, int> num = c => names.ContainsKey(c) ? names[c].Key : c;
            Func<int, string> name = c => "#" + Qualifying.Label(num(c)) + "  " + (names.ContainsKey(c) ? names[c].Value : "slot " + c);
            foreach (var t in rec.Cars.OrderBy(t => t.Car))
                Console.WriteLine(string.Format("{0,-24} laps {1}  best {2}  live {3}", name(t.Car), t.Laps.Count,
                    t.Best != null ? LapFmt.Time(t.Best.LapMs) + " (" + t.Best.S.Count + " samples, " + t.Best.Length.ToString("0") + " m)" : "-",
                    t.Live != null ? t.Live.S.Count + " samples" : "-"));

            // the strip: the car with the most laps, as it was at the end
            var follow = rec.Cars.OrderByDescending(t => t.Laps.Count).First();
            var game = new Rectangle(0, 0, 1920, 1080);
            if (!offline)
                foreach (bool indy in new[] { false, true })
                {
                    var strip = new TeleStripForm(game, new BoardLayout { Scale = 1.5 });
                    strip.Data = new TeleStripData { Have = true, Indy = indy, Num = num(follow.Car), Last = names[follow.Car].Value, Now = rec.Now,
                        Thr = follow.Now.Thr, Brk = follow.Now.Brk, Steer = follow.Now.Steer, Recent = follow.Recent.ToList() };
                    strip.CreateControl();
                    using (var bmp = new Bitmap(strip.Width, strip.Height)) { strip.DrawToBitmap(bmp, new Rectangle(0, 0, strip.Width, strip.Height)); bmp.Save(Path.Combine(outDir, indy ? "strip_indy.png" : "strip_nascar.png")); }
                }
            // the window: the three cars with the fastest laps, their best laps over each other, the cursor in a braking zone
            var win = new TelemetryWindow(rec, name, num, null);
            win.Show();
            Application.DoEvents();
            using (var bmp = new Bitmap(win.Width, win.Height)) { win.DrawToBitmap(bmp, new Rectangle(0, 0, win.Width, win.Height)); bmp.Save(Path.Combine(outDir, "window_empty.png")); }
            win.Select(rec.Cars.Where(t => t.Laps.Count > 0).OrderBy(t => t.Best.LapMs).Take(3).Select(t => t.Car), true);
            win.CursorAt(0.37f);
            Application.DoEvents();
            using (var bmp = new Bitmap(win.Width, win.Height)) { win.DrawToBitmap(bmp, new Rectangle(0, 0, win.Width, win.Height)); bmp.Save(Path.Combine(outDir, "window.png")); }
            win.Close();
            Console.WriteLine("rendered to " + outDir);
        }

        // finished laps only: car, number, name, then each lap (no, start, ms, samples)
        static void Save(string file, TeleRecorder rec, Dictionary<int, KeyValuePair<int, string>> names)
        {
            using (var w = new BinaryWriter(File.Create(file)))
            {
                var cars = rec.Cars.Where(t => t.Laps.Count > 0).ToList();
                w.Write(cars.Count);
                foreach (var t in cars)
                {
                    w.Write(t.Car); w.Write(names[t.Car].Key); w.Write(names[t.Car].Value); w.Write(t.Laps.Count);
                    foreach (var l in t.Laps)
                    {
                        w.Write(l.No); w.Write(l.Start); w.Write(l.LapMs); w.Write(l.S.Count);
                        foreach (var s in l.S) { w.Write(s.D); w.Write(s.Ms); w.Write(s.Mph); w.Write(s.Thr); w.Write(s.Brk); w.Write(s.Steer); w.Write(s.Rpm); w.Write(s.Gear); w.Write(s.X); w.Write(s.Y); }
                    }
                }
            }
        }

        static void Load(string file, TeleRecorder rec, Dictionary<int, KeyValuePair<int, string>> names)
        {
            using (var r = new BinaryReader(File.OpenRead(file)))
            {
                int nc = r.ReadInt32();
                for (int i = 0; i < nc; i++)
                {
                    var t = new TeleCar { Car = r.ReadInt32() };
                    int num = r.ReadInt32(); string nm = r.ReadString(); names[t.Car] = new KeyValuePair<int, string>(num, nm);
                    int nl = r.ReadInt32();
                    for (int j = 0; j < nl; j++)
                    {
                        var l = new TeleLap { Car = t.Car, No = r.ReadInt32(), Start = r.ReadInt32(), LapMs = r.ReadInt32() };
                        int ns = r.ReadInt32();
                        for (int k = 0; k < ns; k++)
                            l.S.Add(new TeleSample { D = r.ReadSingle(), Ms = r.ReadInt32(), Mph = r.ReadSingle(), Thr = r.ReadSingle(), Brk = r.ReadSingle(), Steer = r.ReadSingle(), Rpm = r.ReadSingle(), Gear = r.ReadSByte(), X = r.ReadSingle(), Y = r.ReadSingle() });
                        // saved raw (before the recorder smoothed): the same smoothing for the preview
                        for (int k = 1; k < l.S.Count; k++)
                        {
                            var a = l.S[k - 1]; var b = l.S[k]; double dt = (b.Ms - a.Ms) / 1000.0;
                            if (dt <= 0 || dt > 0.5) continue;
                            b.Thr = TeleRecorder.Smooth(a.Thr, b.Thr, dt, TeleRecorder.ThrottleTau); b.Steer = TeleRecorder.Smooth(a.Steer, b.Steer, dt, TeleRecorder.SteerTau);
                            l.S[k] = b;
                        }
                        t.Laps.Add(l);
                    }
                    t.HaveNow = true;
                    rec.Add(t);
                }
            }
        }
    }
}
