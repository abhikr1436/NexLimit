// Palette that follows the Windows app mode (dark / light), vector glyphs, number formatting.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NexLimit
{
    static class Theme
    {
        public static bool Dark;
        public static Color Bg, Panel, Surface, Alt, Line, Text, Muted, Accent, AccentText, Sel, SelText, Hover, Dl, Ul, Chip;
        public static readonly Color Bad = Color.FromArgb(0xE5, 0x48, 0x4D), Good = Color.FromArgb(0x2E, 0xB8, 0x7A), Warn = Color.FromArgb(0xE0, 0x9B, 0x1A);
        public static readonly Color Brand = Color.FromArgb(0x5B, 0x3D, 0xF5), BrandDark = Color.FromArgb(0x43, 0x26, 0xD9);
        public static readonly Font UiFont = new Font("Segoe UI", 9f), UiBold = new Font("Segoe UI Semibold", 9f);
        public static readonly Font Small = new Font("Segoe UI", 8f), Title = new Font("Segoe UI Semibold", 13f), Big = new Font("Segoe UI Semibold", 11f);
        public static float S = 1f;                                      // DPI scale
        public static event Action Changed;

        public static int Px(float v) { return (int)Math.Round(v * S); }

        static Theme()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = Math.Max(1f, g.DpiX / 96f); } catch (Exception) { }
            Load();
            try { SystemEvents.UserPreferenceChanged += (s, e) => { var was = Dark; Load(); if (was != Dark && Changed != null) Changed(); }; } catch (Exception) { }
        }

        public static bool WindowsUsesDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && k.GetValue("AppsUseLightTheme") is int && (int)k.GetValue("AppsUseLightTheme") == 0;
            }
            catch (Exception) { return false; }
        }

        public static void Load()
        {
            Dark = Environment.GetEnvironmentVariable("NEXLIMIT_THEME") == "dark" || (Environment.GetEnvironmentVariable("NEXLIMIT_THEME") != "light" && WindowsUsesDark());
            if (Dark)
            {
                Bg = C(0x1C1C22); Panel = C(0x24242C); Surface = C(0x19191E); Alt = C(0x1E1E24); Line = C(0x33333D);
                Text = C(0xE9E9F0); Muted = C(0x9898AC); Accent = C(0x8B6CFF); AccentText = C(0xA992FF);
                Sel = C(0x4A33C9); SelText = Color.White; Hover = C(0x2A2A34); Dl = C(0x5B3DF5); Ul = C(0x16917C); Chip = C(0x2C2C36);
            }
            else
            {
                Bg = C(0xF4F3F9); Panel = Color.White; Surface = Color.White; Alt = C(0xFAFAFD); Line = C(0xE2E0EC);
                Text = C(0x1C1B29); Muted = C(0x6B6A80); Accent = C(0x5B3DF5); AccentText = C(0x4A2FE0);
                Sel = C(0xE6E0FF); SelText = C(0x1C1B29); Hover = C(0xF1EFFA); Dl = C(0x7C5CFF); Ul = C(0x22A58F); Chip = C(0xF0EEF8);
            }
        }

        static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string id);

        /// <summary>Dark title bar on Windows 10 2004+ / 11.</summary>
        public static void TitleBar(Form f)
        {
            try { int v = Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref v, 4); } catch (Exception) { }
        }

        /// <summary>Dark scrollbars / headers for native controls.</summary>
        public static void Native(Control c)
        {
            try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch (Exception) { }
        }

        /// <summary>Recolour standard controls. Tag "muted" / "accent" / "panel" changes a control's role.</summary>
        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls) Apply(c);
            var tag = root.Tag as string;
            if (root is TextBox || root is ComboBox || root is NumericUpDown || root is ListBox)
            {
                root.BackColor = Dark ? C(0x2A2A33) : Color.White; root.ForeColor = Text;
                if (root is ComboBox) ((ComboBox)root).FlatStyle = FlatStyle.Flat;
                if (root is TextBox) ((TextBox)root).BorderStyle = BorderStyle.FixedSingle;
                if (root is NumericUpDown) ((NumericUpDown)root).BorderStyle = BorderStyle.FixedSingle;
            }
            else if (root is Button)
            {
                var b = (Button)root; b.FlatStyle = FlatStyle.Flat; b.BackColor = tag == "accent" ? Accent : Chip;
                b.ForeColor = tag == "accent" ? Color.White : Text; b.FlatAppearance.BorderColor = tag == "accent" ? Accent : Line;
                b.FlatAppearance.MouseOverBackColor = tag == "accent" ? Brand : Hover;
            }
            else if (root is LinkLabel)
            {
                var l = (LinkLabel)root; l.LinkColor = AccentText; l.ActiveLinkColor = Accent; l.VisitedLinkColor = AccentText;
                l.DisabledLinkColor = Muted; l.LinkBehavior = LinkBehavior.HoverUnderline;
            }
            else if (root is Label || root is CheckBox || root is RadioButton)
                root.ForeColor = tag == "muted" ? Muted : tag == "accent" ? AccentText : tag == "bad" ? Bad : Text;
            if ((root is Panel || root is Label || root is CheckBox || root is RadioButton || root is SplitContainer || root is TabPage) && !(root is ISelfThemed))
                root.BackColor = SurfaceOf(root);
            if (root is ProgressBar) { root.BackColor = Chip; root.ForeColor = Accent; }
            if (root is ISelfThemed) root.Invalidate();
        }

        /// <summary>Background of the nearest ancestor tagged "panel" / "surface", else the window background.</summary>
        public static Color SurfaceOf(Control c)
        {
            for (var p = c; p != null; p = p.Parent)
            {
                var t = p.Tag as string;
                if (t == "panel") return Panel;
                if (t == "bad-banner") return Bad;
                if (t == "surface") return Surface;
            }
            return Bg;
        }

        static Icon appIcon;
        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                {
                    try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
                    if (appIcon == null) appIcon = SystemIcons.Application;
                }
                return appIcon;
            }
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Max(0.1f, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>The NexLimit mark: a speed gauge with the needle held back, on a violet tile.</summary>
        public static void DrawLogo(Graphics g, RectangleF r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new LinearGradientBrush(r, Color.FromArgb(0x6E, 0x4B, 0xFF), BrandDark, 45f))
            using (var tile = Round(r, r.Width * 0.22f)) g.FillPath(b, tile);
            float w = r.Width, cx = r.X + w / 2, cy = r.Y + w * 0.60f, rad = w * 0.30f;
            using (var pen = new Pen(Color.White, w * 0.085f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, cx - rad, cy - rad, rad * 2, rad * 2, 180, 180);
                double ang = Math.PI * 1.25;
                g.DrawLine(pen, cx, cy, cx + (float)Math.Cos(ang) * rad * 0.8f, cy + (float)Math.Sin(ang) * rad * 0.8f);
            }
            g.FillEllipse(Brushes.White, cx - w * 0.07f, cy - w * 0.07f, w * 0.14f, w * 0.14f);
        }
    }

    /// <summary>Custom-painted controls that read Theme colours themselves.</summary>
    interface ISelfThemed { }

    /// <summary>Small vector icons, drawn at any size.</summary>
    static class Glyph
    {
        public const int BlockIn = 0, BlockOut = 1, LimitIn = 2, LimitOut = 3, QuotaIn = 4, QuotaOut = 5;
        public static readonly string[] SlotNames = { "Block download", "Block upload", "Download limit", "Upload limit", "Download quota", "Upload quota" };

        static void Arrow(Graphics g, RectangleF r, bool down, Color c, float w)
        {
            float cx = r.X + r.Width / 2, t = r.Y + r.Height * 0.24f, b = r.Bottom - r.Height * 0.24f, hw = r.Width * 0.2f;
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(p, cx, t, cx, b);
                if (down) g.DrawLines(p, new[] { new PointF(cx - hw, b - hw), new PointF(cx, b), new PointF(cx + hw, b - hw) });
                else g.DrawLines(p, new[] { new PointF(cx - hw, t + hw), new PointF(cx, t), new PointF(cx + hw, t + hw) });
            }
        }

        /// <summary>Rule-status slot icon. state: 0 = empty (dot), 1 = ghost (hover), 2 = active, 3 = used up / alert.</summary>
        public static void Slot(Graphics g, RectangleF r, int slot, int state)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (state == 0)
            {
                float d = Math.Max(2, r.Width * 0.14f);
                using (var b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Muted, 0.45f))) g.FillEllipse(b, r.X + r.Width / 2 - d / 2, r.Y + r.Height / 2 - d / 2, d, d);
                return;
            }
            bool down = slot % 2 == 0; int kind = slot / 2;
            Color c = kind == 0 ? Theme.Bad : kind == 1 ? Theme.Accent : (state == 3 ? Theme.Bad : Theme.Warn);
            if (state == 1) c = Theme.Mix(Theme.Surface, Theme.Muted, 0.75f);
            var inner = new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            using (var br = new SolidBrush(c))
            {
                if (kind == 1) using (var p = Theme.Round(inner, inner.Width * 0.25f)) g.FillPath(br, p);
                else if (kind == 0) g.FillEllipse(br, inner);
                else
                {
                    var pts = new PointF[6];
                    for (int i = 0; i < 6; i++)
                    {
                        double a = Math.PI / 3 * i + Math.PI / 6;
                        pts[i] = new PointF(inner.X + inner.Width / 2 + (float)Math.Cos(a) * inner.Width / 2, inner.Y + inner.Height / 2 + (float)Math.Sin(a) * inner.Height / 2);
                    }
                    g.FillPolygon(br, pts);
                }
            }
            Arrow(g, inner, down, state == 1 ? Theme.Surface : Color.White, Math.Max(1.4f, r.Width * 0.12f));
        }

        public static void Computer(Graphics g, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(c, Math.Max(1.3f, r.Width / 11)))
            {
                g.DrawRectangle(p, r.X + r.Width * 0.08f, r.Y + r.Height * 0.15f, r.Width * 0.84f, r.Height * 0.55f);
                g.DrawLine(p, r.X + r.Width * 0.5f, r.Y + r.Height * 0.7f, r.X + r.Width * 0.5f, r.Y + r.Height * 0.86f);
                g.DrawLine(p, r.X + r.Width * 0.28f, r.Y + r.Height * 0.88f, r.X + r.Width * 0.72f, r.Y + r.Height * 0.88f);
            }
        }

        public static void Globe(Graphics g, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var i = RectangleF.Inflate(r, -r.Width * 0.1f, -r.Height * 0.1f);
            using (var p = new Pen(c, Math.Max(1.2f, r.Width / 12)))
            {
                g.DrawEllipse(p, i);
                g.DrawEllipse(p, i.X + i.Width * 0.28f, i.Y, i.Width * 0.44f, i.Height);
                g.DrawLine(p, i.X, i.Y + i.Height / 2, i.Right, i.Y + i.Height / 2);
            }
        }

        public static void Lan(Graphics g, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(c, Math.Max(1.2f, r.Width / 12)))
            using (var b = new SolidBrush(c))
            {
                float s = r.Width * 0.26f;
                g.FillRectangle(b, r.X + r.Width / 2 - s / 2, r.Y + r.Height * 0.08f, s, s);
                g.FillRectangle(b, r.X + r.Width * 0.06f, r.Bottom - s - r.Height * 0.06f, s, s);
                g.FillRectangle(b, r.Right - s - r.Width * 0.06f, r.Bottom - s - r.Height * 0.06f, s, s);
                float my = r.Y + r.Height * 0.55f;
                g.DrawLine(p, r.X + r.Width / 2, r.Y + r.Height * 0.08f + s, r.X + r.Width / 2, my);
                g.DrawLine(p, r.X + r.Width * 0.06f + s / 2, my, r.Right - r.Width * 0.06f - s / 2, my);
                g.DrawLine(p, r.X + r.Width * 0.06f + s / 2, my, r.X + r.Width * 0.06f + s / 2, r.Bottom - s - r.Height * 0.06f);
                g.DrawLine(p, r.Right - r.Width * 0.06f - s / 2, my, r.Right - r.Width * 0.06f - s / 2, r.Bottom - s - r.Height * 0.06f);
            }
        }

        public static void Plug(Graphics g, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(c, Math.Max(1.2f, r.Width / 11)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(p, r.X + r.Width * 0.2f, r.Y + r.Height * 0.5f, r.Right - r.Width * 0.2f, r.Y + r.Height * 0.5f);
                g.DrawEllipse(p, r.X + r.Width * 0.08f, r.Y + r.Height * 0.38f, r.Width * 0.24f, r.Height * 0.24f);
                g.DrawEllipse(p, r.Right - r.Width * 0.32f, r.Y + r.Height * 0.38f, r.Width * 0.24f, r.Height * 0.24f);
            }
        }

        public static void Triangle(Graphics g, RectangleF r, bool open, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = r.Width * 0.22f;
            var pts = open ? new[] { new PointF(cx - s, cy - s * 0.6f), new PointF(cx + s, cy - s * 0.6f), new PointF(cx, cy + s * 0.8f) }
                           : new[] { new PointF(cx - s * 0.6f, cy - s), new PointF(cx + s * 0.8f, cy), new PointF(cx - s * 0.6f, cy + s) };
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }
    }

    static class Fmt
    {
        public static string Units = "Auto";                       // Auto, B, KB, MB, bit

        public static string Size(long b)
        {
            if (b < 1024) return b + " B";
            double v = b / 1024.0; string[] u = { "KB", "MB", "GB", "TB" }; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0") + " " + u[i];
        }

        public static string Rate(double bps)
        {
            if (bps < 0) bps = 0;
            switch (Units)
            {
                case "B": return ((long)bps).ToString("N0") + " B/s";
                case "KB": return (bps / 1024).ToString(bps < 10240 ? "0.0" : "N0") + " KB/s";
                case "MB": return (bps / 1048576).ToString("0.00") + " MB/s";
                case "bit":
                    double b = bps * 8; string[] u = { "bit/s", "kbit/s", "Mbit/s", "Gbit/s" }; int i = 0;
                    while (b >= 1000 && i < u.Length - 1) { b /= 1000; i++; }
                    return b.ToString(i == 0 || b >= 100 ? "0" : "0.0") + " " + u[i];
                default: return bps < 1024 ? (long)bps + " B/s" : Size((long)bps) + "/s";
            }
        }

        public static string Limit(long bps) { return bps <= 0 ? "" : Size(bps) + "/s"; }
    }
}
