using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RaceControl
{
    // Light / dark look for the RaceControl window. Every control is recoloured by its type; the tab headers are drawn
    // by us (WinForms tabs ignore colours) and the title bar goes dark through DWM (Windows 10 1809+ / 11).
    public static class Theme
    {
        public enum Mode { System, Light, Dark }

        public static readonly Color Back = Color.FromArgb(30, 31, 34), Panel = Color.FromArgb(40, 42, 46),
            Input = Color.FromArgb(52, 54, 59), Text = Color.FromArgb(228, 230, 235), Muted = Color.FromArgb(150, 156, 168),
            Border = Color.FromArgb(78, 82, 90), Hover = Color.FromArgb(62, 65, 72);

        // Windows' own app setting (Settings > Personalisation > Colours > app mode)
        public static bool WindowsIsDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static bool IsDark(Mode m) { return m == Mode.Dark || (m == Mode.System && WindowsIsDark()); }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        static void TitleBar(Form f, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
                // nudge the frame so the new title bar colour shows at once
                f.Width += 1; f.Width -= 1;
            }
            catch (Exception) { }
        }

        // the light look each control had (so switching back is exact)
        static readonly Dictionary<Control, Tuple<Color, Color>> light = new Dictionary<Control, Tuple<Color, Color>>();
        static readonly HashSet<TabControl> hooked = new HashSet<TabControl>();
        static readonly HashSet<ComboBox> hookedCombos = new HashSet<ComboBox>();

        static void DrawCombo(object sender, DrawItemEventArgs e)
        {
            var cb = (ComboBox)sender;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Hover : Input)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, e.Bounds, cb.Enabled ? Text : Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        static bool dark;

        public static void Apply(Form f, bool on)
        {
            dark = on;
            if (f.IsHandleCreated) TitleBar(f, on); else f.HandleCreated += (s, e) => TitleBar(f, dark);
            Walk(f);
            f.Invalidate(true);
        }

        static void Walk(Control c)
        {
            if (!light.ContainsKey(c)) light[c] = Tuple.Create(c.BackColor, c.ForeColor);
            var orig = light[c];
            bool muted = orig.Item2 == SystemColors.GrayText;

            if (c is TextBox || c is ListBox || c is NumericUpDown || c is ComboBox || c is RichTextBox)
            {
                c.BackColor = dark ? Input : orig.Item1;
                c.ForeColor = dark ? Text : orig.Item2;
                var cb = c as ComboBox;
                if (cb != null)
                {
                    // drop-down lists ignore colours: draw the box and its items ourselves in the dark look
                    if (hookedCombos.Add(cb)) cb.DrawItem += DrawCombo;
                    cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    cb.DrawMode = dark ? DrawMode.OwnerDrawFixed : DrawMode.Normal;
                }
                var tb = c as TextBox;
                if (tb != null) tb.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                var nu = c as NumericUpDown;
                if (nu != null) nu.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
            }
            else if (c is Button)
            {
                var b = (Button)c;
                if (dark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = Hover;
                    b.BackColor = Input; b.ForeColor = Text;
                }
                else
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.BackColor = orig.Item1; b.ForeColor = orig.Item2;
                    b.UseVisualStyleBackColor = true;
                }
            }
            else if (c is TabControl)
            {
                var t = (TabControl)c;
                if (hooked.Add(t)) t.DrawItem += DrawTab;
                t.DrawMode = dark ? TabDrawMode.OwnerDrawFixed : TabDrawMode.Normal;
            }
            else if (c is TabPage)
            {
                var p = (TabPage)c;
                p.UseVisualStyleBackColor = !dark;
                p.BackColor = dark ? Panel : orig.Item1;
                p.ForeColor = dark ? Text : orig.Item2;
            }
            else if (c is GroupBox || c is Panel || c is Form || c is UserControl)
            {
                c.BackColor = dark ? (c is Form ? Back : Panel) : orig.Item1;
                c.ForeColor = dark ? Text : orig.Item2;
            }
            else if (c is CheckBox || c is RadioButton || c is Label || c is LinkLabel)
            {
                c.ForeColor = dark ? (muted ? Muted : Text) : orig.Item2;
                if (c.BackColor != Color.Transparent) c.BackColor = dark ? Color.Transparent : orig.Item1;
            }
            else if (!(c is PictureBox))
            {
                c.BackColor = dark ? Panel : orig.Item1;
                c.ForeColor = dark ? Text : orig.Item2;
            }
            foreach (Control k in c.Controls) Walk(k);
        }

        static void DrawTab(object sender, DrawItemEventArgs e)
        {
            var t = (TabControl)sender;
            if (e.Index < 0 || e.Index >= t.TabPages.Count) return;
            bool sel = e.Index == t.SelectedIndex;
            using (var b = new SolidBrush(sel ? Panel : Back)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) using (var p = new Pen(Color.FromArgb(255, 196, 40), 2)) e.Graphics.DrawLine(p, e.Bounds.Left + 2, e.Bounds.Bottom - 1, e.Bounds.Right - 2, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, t.TabPages[e.Index].Text, t.Font, e.Bounds, sel ? Text : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
