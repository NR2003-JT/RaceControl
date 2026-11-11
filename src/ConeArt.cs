using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceControl
{
    // Number art, headshots and driver colours for the choose cone board, read (never written) from art folders laid
    // out the way AIMOD and companion overlay apps keep them:
    //   series/<id>/numbers/<car number>.png, series/<id>/headshots/<car number>.png, series/<id>/colors.json,
    //   series/<id>/serieslogo.png, images/defaultheadshot.png
    // Folders looked in, first match wins: Race Control's own art\ folder, the folder set as ArtFolder= (another
    // app's, AIMOD's art folder for one), then the companion app's data folder (StageCompanionFolder=). None of them is needed: without art the
    // board draws the numbers itself.
    public class ConeArt
    {
        public string ExtraFolder;          // ArtFolder= in RaceControl.ini
        public string SeriesChoice;         // ArtSeries= in RaceControl.ini, empty = the companion app's series or best match
        public bool UseCompanion = true;
        public string CompanionFolder;      // StageCompanionFolder= in RaceControl.ini, empty = none

        public string Series { get; private set; }
        public string SeriesName { get; private set; }
        public string Source { get; private set; }  // the folder the series came from

        Dictionary<string, Color> colors = new Dictionary<string, Color>();
        readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        readonly Dictionary<string, Bitmap> scaled = new Dictionary<string, Bitmap>();

        string CompanionDir { get { return CompanionFolder; } }

        public List<string> Roots()
        {
            var r = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "art") };
            if (!string.IsNullOrEmpty(ExtraFolder)) r.Add(ExtraFolder);
            if (UseCompanion && !string.IsNullOrEmpty(CompanionDir)) r.Add(CompanionDir);
            return r.Where(Directory.Exists).ToList();
        }

        public List<string> AllSeries()
        {
            var s = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in Roots())
            {
                string d = Path.Combine(r, "series");
                if (Directory.Exists(d)) foreach (string x in Directory.GetDirectories(d)) s.Add(Path.GetFileName(x));
            }
            return s.ToList();
        }

        string SeriesDir(string series)
        {
            if (string.IsNullOrEmpty(series)) return null;
            foreach (string r in Roots())
            {
                string d = Path.Combine(r, "series", series);
                if (Directory.Exists(d)) return d;
            }
            return null;
        }

        public bool IsCompanionAppFolder(string root)
        {
            return !string.IsNullOrEmpty(CompanionDir) && string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(CompanionDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        string CompanionSeries()
        {
            try
            {
                Match m = Regex.Match(File.ReadAllText(Path.Combine(CompanionDir, "config.json")), "\"series\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception) { return null; }
        }

        // Pick the series for these car numbers (once per board): the chosen one, the companion app's active one, else the
        // series whose number art covers the most of them.
        public void Prepare(IEnumerable<int> field)
        {
            var nums = field.Select(n => n.ToString()).ToList();
            // This object lives as long as the app, so drop everything cached and read the art folders afresh each
            // time a board opens; otherwise a logo, number or colour edited on disk never shows up until a restart.
            // (Not disposed: another board may still be drawing the old images; the GC takes them.)
            scaled.Clear();
            images.Clear();
            Series = null;
            string series = null;
            if (!string.IsNullOrEmpty(SeriesChoice) && SeriesDir(SeriesChoice) != null) series = SeriesChoice;
            if (series == null && UseCompanion && SeriesDir(CompanionSeries()) != null) series = CompanionSeries();
            if (series == null)
            {
                int best = 0;
                foreach (string s in AllSeries())
                {
                    string d = SeriesDir(s);
                    int n = nums.Count(x => File.Exists(Path.Combine(d, "numbers", x + ".png")));
                    if (n > best) { best = n; series = s; }
                }
            }
            Series = series;
            string dir = SeriesDir(series);
            Source = dir;
            SeriesName = series;
            colors = new Dictionary<string, Color>();
            if (dir == null) return;
            try
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(dir, "colors.json")), "\"\\s*(\\d+)\\s*\"\\s*:\\s*\"#([0-9a-fA-F]{6})\""))
                    colors[m.Groups[1].Value] = Color.FromArgb(Convert.ToInt32(m.Groups[2].Value, 16) | unchecked((int)0xFF000000));
            }
            catch (Exception) { }
            try
            {
                foreach (string line in File.ReadAllLines(Path.Combine(dir, "seriesinfo.ini")))
                    if (line.StartsWith("seriesname=", StringComparison.OrdinalIgnoreCase)) SeriesName = line.Substring(11).Trim();
            }
            catch (Exception) { }
        }

        public Color? ColorOf(int num)
        {
            Color c;
            if (!colors.TryGetValue(num.ToString(), out c) || (c.R == 0x88 && c.G == 0x88 && c.B == 0x88)) return null;
            return c;
        }

        Image Load(string path)
        {
            Image img;
            if (images.TryGetValue(path, out img)) return img;
            img = null;
            try
            {
                if (File.Exists(path))
                    using (var ms = new MemoryStream(File.ReadAllBytes(path)))    // no file lock
                        img = new Bitmap(ms);
            }
            catch (Exception) { img = null; }
            images[path] = img;
            return img;
        }

        Image SeriesImage(string rel)
        {
            string dir = SeriesDir(Series);
            return dir == null ? null : Load(Path.Combine(dir, rel));
        }

        public Image Number(int num) { return SeriesImage(Path.Combine("numbers", num + ".png")); }
        public Image Logo() { return SeriesImage("serieslogo.png"); }

        public Image Headshot(int num)
        {
            Image img = SeriesImage(Path.Combine("headshots", num + ".png"));
            if (img != null) return img;
            foreach (string r in Roots())
            {
                img = Load(Path.Combine(r, "images", "defaultheadshot.png"));
                if (img != null) return img;
            }
            return null;
        }

        // The image scaled once to fit w x h (keeping its shape), so the board does not resample every frame.
        public Bitmap Fit(Image img, int w, int h)
        {
            if (img == null || w <= 0 || h <= 0) return null;
            double k = Math.Min((double)w / img.Width, (double)h / img.Height);
            int sw = Math.Max(1, (int)Math.Round(img.Width * k)), sh = Math.Max(1, (int)Math.Round(img.Height * k));
            string key = img.GetHashCode() + ":" + sw + "x" + sh;
            Bitmap b;
            if (scaled.TryGetValue(key, out b)) return b;
            b = new Bitmap(sw, sh);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, 0, 0, sw, sh);
            }
            scaled[key] = b;
            return b;
        }
    }
}
