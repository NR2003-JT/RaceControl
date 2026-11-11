using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RaceControl
{
    // A named set of options: the checkboxes, the ini-only rules and the board's size and scroll time. Lane and cone
    // settings stay per track and the board's place stays per screen, so a preset never touches them. Missing keys
    // are left as they are, so a hand-written preset can set just a few options.
    public class Preset
    {
        public string Name;
        public bool BuiltIn;
        public Dictionary<string, string> Values = new Dictionary<string, string>();

        public bool Matches(Dictionary<string, string> now)
        {
            foreach (var kv in Values)
            {
                string v;
                if (!now.TryGetValue(kv.Key, out v)) continue; // an option RaceControl no longer has (pit road ones, 2.9.1)
                if (!Same(v, kv.Value)) return false;
            }
            return true;
        }

        static bool Same(string a, string b)
        {
            double x, y;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (double.TryParse(a, System.Globalization.NumberStyles.Float, inv, out x) &&
                double.TryParse(b, System.Globalization.NumberStyles.Float, inv, out y)) return Math.Abs(x - y) < 1e-6;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() { return BuiltIn ? Name : Name + "  (mine)"; }
    }

    // Built-in presets in code, your own as presets\<name>.ini next to the exe (copy them to share them).
    public static class PresetStore
    {
        public static string Folder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presets"); } }

        static Preset Make(string name, string values)
        {
            var p = new Preset { Name = name, BuiltIn = true };
            foreach (string kv in values.Split(';'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) p.Values[kv.Substring(0, eq).Trim()] = kv.Substring(eq + 1).Trim();
            }
            return p;
        }

        const string Modern =
            "StockCautions=0;RaceBack=0;StageRacing=1;StageThrowCaution=1;StageRaceBack=1;GreenWhiteCheckered=1;GhostOnPitRoad=1;HoldOneToGo=1;WaveAround=1;FreePass=1;" +
            "DoubleFile=1;Cone=1;PlayerChoosesLane=1;Untangle=1;GhostWhilePacing=0;ShowConeBoard=1;EarlyLineup=1;RestorePaceLap=1";

        public static List<Preset> BuiltIns()
        {
            return new List<Preset>
            {
                Make("Modern NASCAR (choose cone)", Modern + ";BoardSize=100;BoardHold=3.5"),
                Make("Watching AI races", Modern.Replace("PlayerChoosesLane=1", "PlayerChoosesLane=0") + ";BoardSize=100;BoardHold=3.5"),
                Make("Double file, no cone (2009-2016)", Modern.Replace("Cone=1", "Cone=0").Replace("ShowConeBoard=1", "ShowConeBoard=0")),
                Make("Wave around, single file", Modern.Replace("DoubleFile=1", "DoubleFile=0").Replace("Cone=1", "Cone=0").Replace("ShowConeBoard=1", "ShowConeBoard=0")),
                Make("Classic 2003 (race back to the line)",
                    "StockCautions=0;RaceBack=1;StageRacing=0;GreenWhiteCheckered=0;GhostOnPitRoad=1;HoldOneToGo=0;WaveAround=0;FreePass=0;" +
                    "DoubleFile=0;Cone=0;Untangle=1;GhostWhilePacing=0;ShowConeBoard=0"),
                // NR2003's own cautions and restarts; RaceControl adds the Lucky Dog (and runs qualifying)
                Make("NR2003 cautions + Lucky Dog", "StockCautions=1;FreePass=1"),
            };
        }

        public static List<Preset> Mine()
        {
            var list = new List<Preset>();
            try
            {
                if (!Directory.Exists(Folder)) return list;
                foreach (string f in Directory.GetFiles(Folder, "*.ini").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var p = new Preset { Name = Path.GetFileNameWithoutExtension(f) };
                    foreach (string line in File.ReadAllLines(f))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && !line.TrimStart().StartsWith(";")) p.Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                    if (p.Values.Count > 0) list.Add(p);
                }
            }
            catch { }
            return list;
        }

        public static void Save(string name, Dictionary<string, string> values)
        {
            Directory.CreateDirectory(Folder);
            var lines = new List<string> { "; Race Control preset. Keys left out are not changed when the preset is chosen." };
            lines.AddRange(values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
            File.WriteAllLines(Path.Combine(Folder, name + ".ini"), lines.ToArray());
        }

        public static void Delete(string name)
        {
            string f = Path.Combine(Folder, name + ".ini");
            if (File.Exists(f)) File.Delete(f);
        }

        // null = a good name; otherwise why not
        public static string Check(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Type a name.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name can't have any of  \\ / : * ? \" < > |";
            if (BuiltIns().Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))) return "That is a built-in preset's name; pick another.";
            return null;
        }

        public static string AskName(IWin32Window owner, string suggested)
        {
            using (var f = new Form { Text = "Save preset", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                                      StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(340, 96), Font = new Font("Segoe UI", 9f) })
            {
                f.Controls.Add(new Label { Left = 10, Top = 10, Width = 320, Text = "Name (an existing preset of yours is replaced):" });
                var box = new TextBox { Left = 10, Top = 32, Width = 320, Text = suggested ?? "" };
                var ok = new Button { Text = "Save", Left = 174, Top = 62, Width = 75, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 255, Top = 62, Width = 75, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { box, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                while (f.ShowDialog(owner) == DialogResult.OK)
                {
                    string why = Check(box.Text);
                    if (why == null) return box.Text.Trim();
                    MessageBox.Show(f, why, "Save preset", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return null;
            }
        }
    }
}
