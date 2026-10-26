// Small custom-painted controls that follow Theme: switches, tab strip, themed list view, rules grid, charts.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace NexLimit
{
    class PaintedControl : Control, ISelfThemed
    {
        public PaintedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.UiFont;
        }
        protected Color Back { get { return Theme.SurfaceOf(Parent ?? this); } }
    }

    /// <summary>Check box drawn as a square with a check mark (Blocker On / Limiter On / Quota On).</summary>
    sealed class Switch : PaintedControl
    {
        bool on, hover;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return on; } set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        public Switch(string text) { Text = text; Cursor = Cursors.Hand; Size = new Size(Theme.Px(24) + TextRenderer.MeasureText(text, Theme.UiFont).Width + Theme.Px(10), Theme.Px(28)); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Back); g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Theme.Px(16); var box = new RectangleF(Theme.Px(2), (Height - s) / 2, s, s);
            using (var p = Theme.Round(box, Theme.Px(3)))
            {
                if (on) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
                else using (var pen = new Pen(hover ? Theme.Accent : Theme.Muted, Theme.S)) g.DrawPath(pen, p);
            }
            if (on)
                using (var pen = new Pen(Color.White, 2 * Theme.S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(box.X + s * 0.24f, box.Y + s * 0.52f), new PointF(box.X + s * 0.43f, box.Y + s * 0.7f), new PointF(box.X + s * 0.78f, box.Y + s * 0.3f) });
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)box.Right + Theme.Px(6), 0, Width, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    /// <summary>Row of selectable labels; the selected one gets an accent chip. Used for tabs and filters.</summary>
    sealed class Strip : PaintedControl
    {
        public readonly List<string> Items = new List<string>();
        int sel, hover = -1;
        readonly bool compact;
        public event EventHandler SelectedChanged;
        public int Selected { get { return sel; } set { if (sel == value) return; sel = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); } }
        public Strip(bool compact, params string[] items) { this.compact = compact; Items.AddRange(items); Height = Theme.Px(compact ? 26 : 34); Cursor = Cursors.Hand; Font = compact ? Theme.UiFont : Theme.UiBold; }

        Rectangle[] Boxes()
        {
            var r = new Rectangle[Items.Count]; int x = Theme.Px(compact ? 0 : 6), pad = Theme.Px(compact ? 7 : 10);
            for (int i = 0; i < Items.Count; i++)
            {
                int w = TextRenderer.MeasureText(Items[i], Font).Width + pad * 2;
                r[i] = new Rectangle(x, Theme.Px(compact ? 2 : 4), w, Height - Theme.Px(compact ? 4 : 8)); x += w + Theme.Px(2);
            }
            return r;
        }
        public int PreferredWidth { get { var b = Boxes(); return b.Length == 0 ? 0 : b.Last().Right + Theme.Px(4); } }
        protected override void OnMouseMove(MouseEventArgs e) { var h = Array.FindIndex(Boxes(), b => b.Contains(e.Location)); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { var h = Array.FindIndex(Boxes(), b => b.Contains(e.Location)); if (h >= 0) Selected = h; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Back); g.SmoothingMode = SmoothingMode.AntiAlias;
            var boxes = Boxes();
            for (int i = 0; i < Items.Count; i++)
            {
                var b = boxes[i];
                if (i == sel || i == hover)
                    using (var p = Theme.Round(b, Theme.Px(4)))
                    using (var br = new SolidBrush(i == sel ? Theme.Accent : Theme.Hover)) g.FillPath(br, p);
                TextRenderer.DrawText(g, Items[i], Font, b, i == sel ? Color.White : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>ListView with themed header, rows and scrollbars.</summary>
    sealed class ThemedList : ListView
    {
        public ThemedList()
        {
            View = View.Details; FullRowSelect = true; HideSelection = false; OwnerDraw = true; DoubleBuffered = true;
            BorderStyle = BorderStyle.None; Font = Theme.UiFont; Dock = DockStyle.Fill;
            Retheme();
        }
        public void Retheme() { BackColor = Theme.Surface; ForeColor = Theme.Text; if (IsHandleCreated) Theme.Native(this); Invalidate(); }

        /// <summary>Last column fills the rest of the width, so no unthemed header strip is left over.</summary>
        public void FillLast()
        {
            if (Columns.Count == 0 || Width <= 0) return;
            int used = 0; for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
            var w = Math.Max(Theme.Px(120), ClientSize.Width - used);
            if (Columns[Columns.Count - 1].Width != w) Columns[Columns.Count - 1].Width = w;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); FillLast(); }
        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e) { base.OnColumnWidthChanged(e); if (e.ColumnIndex != Columns.Count - 1) FillLast(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Native(this); }
        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Theme.Line)) { e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1); e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4); }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.UiBold, Rectangle.Inflate(e.Bounds, -6, 0), Theme.Muted, flags);
        }
        protected override void OnDrawItem(DrawListViewItemEventArgs e) { }
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var sel = e.Item.Selected;
            var bg = sel ? Theme.Sel : e.ItemIndex % 2 == 1 ? Theme.Alt : Theme.Surface;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = Rectangle.Inflate(e.Bounds, -6, 0);
            if (e.ColumnIndex == 0 && SmallImageList != null && e.Item.ImageIndex >= 0 && e.Item.ImageIndex < SmallImageList.Images.Count)
            {
                var sz = SmallImageList.ImageSize;
                e.Graphics.DrawImage(SmallImageList.Images[e.Item.ImageIndex], r.X, e.Bounds.Y + (e.Bounds.Height - sz.Height) / 2, sz.Width, sz.Height);
                r.X += sz.Width + 6; r.Width -= sz.Width + 6;
            }
            var color = sel ? Theme.SelText : e.Item.ForeColor == Color.Empty || e.Item.ForeColor == ForeColor || e.Item.ForeColor == SystemColors.WindowText ? Theme.Text : e.Item.ForeColor;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, r, color, flags);
        }
    }

    /// <summary>Progress bar in theme colours (the native one ignores colours with visual styles on).</summary>
    sealed class Meter : PaintedControl
    {
        int value;
        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Back); g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Theme.Chip)) using (var p = Theme.Round(r, r.Height / 2)) g.FillPath(b, p);
            if (value <= 0) return;
            var f = new RectangleF(0, 0, Math.Max(r.Height, r.Width * value / 100f), r.Height);
            using (var b = new SolidBrush(value >= 100 ? Theme.Bad : value >= 90 ? Theme.Warn : Theme.Accent)) using (var p = Theme.Round(f, r.Height / 2)) g.FillPath(b, p);
        }
    }

    /// <summary>The "Rules" box of the Info View: Blocker / Limit / Quota rows, In / Out columns, clickable cells.</summary>
    sealed class RuleGrid : PaintedControl
    {
        public static readonly string[] RowNames = { "Blocker", "Limit", "Quota" };
        public string[,] Cells = new string[3, 2];
        public int[,] States = new int[3, 2];          // 0 not set, 2 active, 3 alert
        public bool[] Span = new bool[3];              // one cell across In and Out
        public bool Enabled_ = true;
        public event Action<int, int, Rectangle> CellClicked;
        int hr = -1, hc = -1;

        public RuleGrid() { Height = Theme.Px(30) * 4 + Theme.Px(8); Cursor = Cursors.Hand; }

        Rectangle CellRect(int r, int c)
        {
            int label = Theme.Px(70), rowH = Theme.Px(30), top = rowH, w = (Width - label - Theme.Px(12)) / 2;
            int x = label + c * w; if (Span[r]) return new Rectangle(label, top + r * rowH + Theme.Px(3), w * 2 - Theme.Px(4), rowH - Theme.Px(6));
            return new Rectangle(x, top + r * rowH + Theme.Px(3), w - Theme.Px(4), rowH - Theme.Px(6));
        }

        bool Hit(Point p, out int r, out int c)
        {
            for (r = 0; r < 3; r++) for (c = 0; c < 2; c++) { if (Span[r] && c == 1) continue; if (CellRect(r, c).Contains(p)) return true; }
            r = c = -1; return false;
        }
        protected override void OnMouseMove(MouseEventArgs e) { int r, c; Hit(e.Location, out r, out c); if (r != hr || c != hc) { hr = r; hc = c; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hr = hc = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int r, c;
            if (Enabled_ && Hit(e.Location, out r, out c) && CellClicked != null) CellClicked(r, c, RectangleToScreen(CellRect(r, c)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Back); g.SmoothingMode = SmoothingMode.AntiAlias;
            int rowH = Theme.Px(30), label = Theme.Px(70);
            using (var p = new Pen(Theme.Line)) using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.Px(5))) g.DrawPath(p, path);
            TextRenderer.DrawText(g, "Type", Theme.UiBold, new Rectangle(Theme.Px(10), 0, label, rowH), Theme.Muted, TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "In (download)", Theme.UiBold, new Rectangle(CellRect(0, 0).X, 0, 200, rowH), Theme.Muted, TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "Out (upload)", Theme.UiBold, new Rectangle(CellRect(1, 1).X, 0, 200, rowH), Theme.Muted, TextFormatFlags.VerticalCenter);
            for (int r = 0; r < 3; r++)
            {
                TextRenderer.DrawText(g, RowNames[r], Theme.UiBold, new Rectangle(Theme.Px(10), rowH * (r + 1), label, rowH), Theme.Text, TextFormatFlags.VerticalCenter);
                for (int c = 0; c < 2; c++)
                {
                    if (Span[r] && c == 1) continue;
                    var cr = CellRect(r, c); int st = States[r, c];
                    var bg = r == hr && c == hc && Enabled_ ? Theme.Hover : Theme.Chip;
                    using (var p = Theme.Round(cr, Theme.Px(4))) using (var b = new SolidBrush(bg)) g.FillPath(b, p);
                    var icon = new RectangleF(cr.X + Theme.Px(5), cr.Y + (cr.Height - Theme.Px(14)) / 2f, Theme.Px(14), Theme.Px(14));
                    Glyph.Slot(g, icon, r * 2 + c, st == 0 ? 1 : st);
                    var col = st == 3 ? Theme.Bad : st == 2 ? Theme.Text : Theme.Muted;
                    TextRenderer.DrawText(g, Cells[r, c] ?? "Not set", Font, new Rectangle((int)icon.Right + Theme.Px(5), cr.Y, cr.Right - (int)icon.Right - Theme.Px(6), cr.Height), col,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    /// <summary>Live download / upload chart: download as a filled area, upload as a line; each axis scaled on its own.</summary>
    sealed class TrafficChart : PaintedControl
    {
        public double[] Down = new double[0], Up = new double[0];
        public string Caption = "";
        public double SecondsPerSample = 1;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Panel); g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.Px(10), top = Theme.Px(26), bottom = Theme.Px(22), side = Theme.Px(62);
            TextRenderer.DrawText(g, "Download", Theme.UiBold, new Point(pad, Theme.Px(5)), Theme.AccentText);
            var upSz = TextRenderer.MeasureText("Upload", Theme.UiBold);
            TextRenderer.DrawText(g, "Upload", Theme.UiBold, new Point(Width - pad - upSz.Width, Theme.Px(5)), Theme.Ul);
            var dnW = TextRenderer.MeasureText("Download", Theme.UiBold).Width + pad * 2;
            TextRenderer.DrawText(g, Caption, Theme.UiFont, new Rectangle(dnW, Theme.Px(5), Width - dnW - upSz.Width - pad * 2, Theme.Px(18)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var plot = new RectangleF(side, top, Width - side * 2, Height - top - bottom);
            if (plot.Width < 20 || plot.Height < 20) return;
            using (var p = new Pen(Theme.Line)) { for (int i = 0; i <= 4; i++) { float y = plot.Y + plot.Height * i / 4; g.DrawLine(p, plot.X, y, plot.Right, y); } }
            double maxD = Math.Max(1024, Down.Length == 0 ? 0 : Down.Max()), maxU = Math.Max(1024, Up.Length == 0 ? 0 : Up.Max());
            maxD *= 1.15; maxU *= 1.15;
            TextRenderer.DrawText(g, Fmt.Size((long)maxD), Theme.Small, new Rectangle(0, (int)plot.Y - 7, side - 4, 16), Theme.AccentText, TextFormatFlags.Right);
            TextRenderer.DrawText(g, "0", Theme.Small, new Rectangle(0, (int)plot.Bottom - 8, side - 4, 16), Theme.Muted, TextFormatFlags.Right);
            TextRenderer.DrawText(g, Fmt.Size((long)maxU), Theme.Small, new Rectangle((int)plot.Right + 4, (int)plot.Y - 7, side, 16), Theme.Ul, TextFormatFlags.Left);
            TextRenderer.DrawText(g, "0", Theme.Small, new Rectangle((int)plot.Right + 4, (int)plot.Bottom - 8, side, 16), Theme.Muted, TextFormatFlags.Left);
            int n = Math.Max(Down.Length, 2); double span = (n - 1) * SecondsPerSample;
            for (int i = 0; i <= 2; i++)
            {
                var label = i == 2 ? "now" : (int)Math.Round(span * (2 - i) / 2 / 60) + " min ago";
                TextRenderer.DrawText(g, label, Theme.Small, new Rectangle((int)(plot.X + plot.Width * i / 2) - 40, (int)plot.Bottom + 4, 80, 16), Theme.Muted, TextFormatFlags.HorizontalCenter);
            }
            Func<double[], double, PointF[]> pts = (data, max) =>
            {
                var r = new PointF[data.Length];
                for (int i = 0; i < data.Length; i++) r[i] = new PointF(plot.X + plot.Width * i / (n - 1), plot.Bottom - (float)(plot.Height * Math.Min(1, data[i] / max)));
                return r;
            };
            if (Down.Length > 1)
            {
                var d = pts(Down, maxD);
                var area = new List<PointF>(d) { new PointF(d.Last().X, plot.Bottom), new PointF(d[0].X, plot.Bottom) };
                using (var b = new SolidBrush(Color.FromArgb(70, Theme.Accent))) g.FillPolygon(b, area.ToArray());
                using (var p = new Pen(Theme.Accent, 1.5f * Theme.S)) g.DrawLines(p, d);
            }
            if (Up.Length > 1) using (var p = new Pen(Theme.Ul, 1.5f * Theme.S)) g.DrawLines(p, pts(Up, maxU));
        }
    }

    /// <summary>Stacked bars per day (download + upload) for the Traffic Statistics tab.</summary>
    sealed class DailyBars : PaintedControl
    {
        public SortedDictionary<DateTime, long[]> Data = new SortedDictionary<DateTime, long[]>();
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Panel);
            int pad = Theme.Px(12), bottom = Theme.Px(20), left = Theme.Px(64);
            var plot = new Rectangle(left, pad, Width - left - pad, Height - pad - bottom);
            if (Data.Count == 0 || plot.Width < 20) { TextRenderer.DrawText(g, "No traffic recorded in this range yet.", Font, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            var days = Data.Keys.ToList();
            DateTime first = days[0], last = days[days.Count - 1];
            int n = (int)(last - first).TotalDays + 1;
            long max = Math.Max(1, Data.Values.Max(v => v[0] + v[1]));
            using (var p = new Pen(Theme.Line)) for (int i = 0; i <= 2; i++) g.DrawLine(p, plot.X, plot.Y + plot.Height * i / 2, plot.Right, plot.Y + plot.Height * i / 2);
            TextRenderer.DrawText(g, Fmt.Size(max), Theme.Small, new Rectangle(0, plot.Y - 7, left - 6, 16), Theme.Muted, TextFormatFlags.Right);
            TextRenderer.DrawText(g, "0", Theme.Small, new Rectangle(0, plot.Bottom - 8, left - 6, 16), Theme.Muted, TextFormatFlags.Right);
            float w = (float)plot.Width / n;
            int lx = Width - pad - Theme.Px(150), ly = Theme.Px(2) - Theme.Px(24);
            using (var bd = new SolidBrush(Theme.Dl)) using (var bu = new SolidBrush(Theme.Ul))
            {
                ly = 0;
                g.FillRectangle(bd, lx, ly + Theme.Px(4), Theme.Px(10), Theme.Px(10)); TextRenderer.DrawText(g, "Download", Theme.Small, new Point(lx + Theme.Px(14), ly + Theme.Px(1)), Theme.Muted);
                g.FillRectangle(bu, lx + Theme.Px(80), ly + Theme.Px(4), Theme.Px(10), Theme.Px(10)); TextRenderer.DrawText(g, "Upload", Theme.Small, new Point(lx + Theme.Px(94), ly + Theme.Px(1)), Theme.Muted);
            }
            using (var bd = new SolidBrush(Theme.Dl)) using (var bu = new SolidBrush(Theme.Ul))
                foreach (var kv in Data)
                {
                    int i = (int)(kv.Key - first).TotalDays;
                    float x = plot.X + i * w + Math.Min(w * 0.15f, 3), bw = Math.Max(1, w - Math.Min(w * 0.3f, 6));
                    float hd = plot.Height * kv.Value[0] / (float)max, hu = plot.Height * kv.Value[1] / (float)max;
                    g.FillRectangle(bd, x, plot.Bottom - hd, bw, hd);
                    g.FillRectangle(bu, x, plot.Bottom - hd - hu, bw, hu);
                }
            int step = Math.Max(1, (int)Math.Ceiling(n / (plot.Width / (float)Theme.Px(60))));
            for (int i = 0; i < n; i += step)
                TextRenderer.DrawText(g, first.AddDays(i).ToString(n > 60 ? "MMM d" : "d MMM"), Theme.Small, new Rectangle((int)(plot.X + i * w + w / 2) - 30, plot.Bottom + 3, 60, 16), Theme.Muted, TextFormatFlags.HorizontalCenter);
        }
    }
}
