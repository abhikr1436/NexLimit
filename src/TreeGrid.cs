// The Activity tree: PC -> LocalNetwork / Internet / apps -> connections, with rate cells and rule-status slots.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NexLimit
{
    sealed class Row
    {
        public const int Pc = 0, Lan = 1, Internet = 2, AppRow = 3, ConnRow = 4;
        public int Kind, Depth;
        public string Key, Name, Sub;                 // Sub: second text (path / state), shown in tooltip
        public App App; public Conn Conn;
        public bool HasKids, Expanded, Online;
        public double Dl, Ul; public long Today, TotDl = -1, TotUl = -1;   // -1 = no total column value
        public int[] Slots;                           // 6 slot states (Glyph.Slot), null = no rule column
        public Image Icon;
    }

    sealed class TreeGrid : PaintedControl
    {
        public List<Row> Rows = new List<Row>();
        public string SelectedKey;
        public event Action<Row> SelectionChanged, ToggleExpand, Activated;
        public event Action<Row, Point> RowMenu;
        public event Action<Row, int, Rectangle> SlotClicked;
        public event Action<int> HeaderClicked;
        public int SortCol = 1; public bool SortDesc = true;
        public static readonly string[] Headers = { "Name", "Download", "Upload", "Total Download", "Total Upload", "Rule Status", "Today" };
        int scroll, hoverRow = -1, hoverSlot = -1, dragStart = -1, dragScroll;
        readonly ToolTip tip = new ToolTip { InitialDelay = 400, ReshowDelay = 100 };
        string tipText;

        public TreeGrid() { TabStop = true; }

        int RowH { get { return Theme.Px(26); } }
        int HeadH { get { return Theme.Px(30); } }
        int Bar { get { return Theme.Px(10); } }
        int[] Widths()
        {
            int dl = Theme.Px(92), ul = Theme.Px(92), tot = Theme.Px(118), rs = Theme.Px(150), td = Theme.Px(84);
            int name = Math.Max(Theme.Px(160), Width - Bar - dl - ul - tot * 2 - rs - td);
            return new[] { name, dl, ul, tot, tot, rs, td };
        }
        int ColX(int c) { var w = Widths(); int x = 0; for (int i = 0; i < c; i++) x += w[i]; return x; }
        int PageRows { get { return Math.Max(1, (Height - HeadH) / RowH); } }
        int MaxScroll { get { return Math.Max(0, Rows.Count * RowH - (Height - HeadH)); } }

        public void SetRows(List<Row> rows)
        {
            Rows = rows; scroll = Math.Min(scroll, MaxScroll);
            if (SelectedKey == null && rows.Count > 0) SelectedKey = rows[0].Key;
            Invalidate();
        }
        public Row Selected { get { return Rows.Find(r => r.Key == SelectedKey); } }

        int RowAt(int y) { if (y < HeadH) return -1; int i = (y - HeadH + scroll) / RowH; return i < Rows.Count ? i : -1; }
        RectangleF SlotRect(int row, int slot)
        {
            int s = Theme.Px(16), gap = Theme.Px(6), x = ColX(5) + Theme.Px(8) + slot * (s + gap);
            int y = HeadH + row * RowH - scroll + (RowH - s) / 2;
            return new RectangleF(x, y, s, s);
        }
        int SlotAt(int row, Point p)
        {
            if (row < 0 || Rows[row].Slots == null) return -1;
            for (int i = 0; i < 6; i++) { var r = SlotRect(row, i); r.Inflate(2, 2); if (r.Contains(p)) return i; }
            return -1;
        }
        RectangleF Expander(int row) { var r = Rows[row]; int x = Theme.Px(14) + r.Depth * Theme.Px(18); return new RectangleF(x, HeadH + row * RowH - scroll, Theme.Px(14), RowH); }

        void Select(int i)
        {
            if (i < 0 || i >= Rows.Count) return;
            SelectedKey = Rows[i].Key;
            int top = i * RowH, bottom = top + RowH, view = Height - HeadH;
            if (top < scroll) scroll = top; else if (bottom > scroll + view) scroll = bottom - view;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(Rows[i]);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.X >= Width - Bar && MaxScroll > 0) { dragStart = e.Y; dragScroll = scroll; return; }
            if (e.Y < HeadH) { var w = Widths(); int x = 0; for (int c = 0; c < w.Length; c++) { if (e.X < x + w[c]) { if (c != 5 && HeaderClicked != null) HeaderClicked(c); break; } x += w[c]; } return; }
            int i = RowAt(e.Y); if (i < 0) return;
            var row = Rows[i];
            if (e.Button == MouseButtons.Left && row.HasKids && Expander(i).Contains(e.Location)) { if (ToggleExpand != null) ToggleExpand(row); return; }
            Select(i);
            int s = SlotAt(i, e.Location);
            if (e.Button == MouseButtons.Left && s >= 0 && SlotClicked != null)
            {
                var r = SlotRect(i, s);
                SlotClicked(row, s, RectangleToScreen(Rectangle.Round(r)));
            }
            if (e.Button == MouseButtons.Right && RowMenu != null) RowMenu(row, e.Location);
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragStart = -1; }
        protected override void OnDoubleClick(EventArgs e)
        {
            var p = PointToClient(MousePosition); int i = RowAt(p.Y);
            if (i < 0 || SlotAt(i, p) >= 0) return;
            if (Rows[i].HasKids) { if (ToggleExpand != null) ToggleExpand(Rows[i]); }
            else if (Activated != null) Activated(Rows[i]);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragStart >= 0)
            {
                int track = Height - HeadH; float ratio = (Rows.Count * RowH) / (float)Math.Max(1, track);
                scroll = Math.Max(0, Math.Min(MaxScroll, dragScroll + (int)((e.Y - dragStart) * ratio))); Invalidate(); return;
            }
            int i = RowAt(e.Y), s = SlotAt(i, e.Location);
            if (i != hoverRow || s != hoverSlot) { hoverRow = i; hoverSlot = s; Invalidate(); }
            string t = null;
            if (s >= 0) t = Glyph.SlotNames[s] + ": " + (Rows[i].Slots[s] == 0 ? "not set (click to set)" : Rows[i].Slots[s] == 3 ? "used up" : "on (click to change)");
            else if (i >= 0 && e.X < ColX(1) && Rows[i].Sub != null) t = Rows[i].Sub;
            if (t != tipText) { tipText = t; if (t == null) tip.Hide(this); else tip.Show(t, this, e.X + 12, e.Y + 18, 4000); }
        }
        protected override void OnMouseLeave(EventArgs e) { hoverRow = hoverSlot = -1; tipText = null; tip.Hide(this); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * RowH * 3)); Invalidate(); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int i = Rows.FindIndex(r => r.Key == SelectedKey);
            if (e.KeyCode == Keys.Down) Select(Math.Min(Rows.Count - 1, i + 1));
            else if (e.KeyCode == Keys.Up) Select(Math.Max(0, i - 1));
            else if (e.KeyCode == Keys.PageDown) Select(Math.Min(Rows.Count - 1, i + PageRows));
            else if (e.KeyCode == Keys.PageUp) Select(Math.Max(0, i - PageRows));
            else if (i >= 0 && (e.KeyCode == Keys.Right && !Rows[i].Expanded || e.KeyCode == Keys.Left && Rows[i].Expanded) && Rows[i].HasKids && ToggleExpand != null) ToggleExpand(Rows[i]);
            else if (i >= 0 && e.KeyCode == Keys.Apps && RowMenu != null) RowMenu(Rows[i], new Point(Theme.Px(40), HeadH + i * RowH - scroll + RowH));
        }
        protected override void OnResize(EventArgs e) { scroll = Math.Min(scroll, MaxScroll); base.OnResize(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Surface);
            var w = Widths(); int rh = RowH;
            int first = scroll / rh, last = Math.Min(Rows.Count - 1, (scroll + Height) / rh);
            for (int i = first; i <= last; i++) PaintRow(g, i, w);
            // header
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, 0, 0, Width, HeadH);
            using (var p = new Pen(Theme.Line))
            {
                g.DrawLine(p, 0, HeadH - 1, Width, HeadH - 1);
                int x = 0;
                for (int c = 0; c < w.Length; c++)
                {
                    var r = new Rectangle(x + Theme.Px(8), 0, w[c] - Theme.Px(16), HeadH);
                    var text = Headers[c] + (SortCol == c ? (SortDesc ? "  ▼" : "  ▲") : "");
                    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | (c == 0 ? TextFormatFlags.Left : c == 5 ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Right);
                    TextRenderer.DrawText(g, text, Theme.UiBold, r, SortCol == c ? Theme.Text : Theme.Muted, flags);
                    x += w[c];
                    if (c < w.Length - 1) g.DrawLine(p, x - 1, Theme.Px(7), x - 1, HeadH - Theme.Px(7));
                }
            }
            // scrollbar
            if (MaxScroll > 0)
            {
                int track = Height - HeadH, thumb = Math.Max(Theme.Px(30), track * track / (Rows.Count * rh));
                int y = HeadH + (int)((track - thumb) * (scroll / (float)MaxScroll));
                using (var b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Muted, 0.45f)))
                using (var path = Theme.Round(new RectangleF(Width - Bar + Theme.Px(2), y + 2, Bar - Theme.Px(4), thumb - 4), Theme.Px(3))) g.FillPath(b, path);
            }
        }

        void PaintRow(Graphics g, int i, int[] w)
        {
            var row = Rows[i]; int rh = RowH, y = HeadH + i * rh - scroll;
            bool sel = row.Key == SelectedKey;
            var bg = sel ? Theme.Sel : i == hoverRow ? Theme.Hover : i % 2 == 1 ? Theme.Alt : Theme.Surface;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, y, Width - Bar, rh);
            var fg = sel ? Theme.SelText : row.Kind == Row.ConnRow ? Theme.Muted : Theme.Text;

            // online dot, expander, icon, name
            if (row.Online) using (var b = new SolidBrush(Theme.Good)) g.FillEllipse(b, Theme.Px(5), y + rh / 2 - Theme.Px(3), Theme.Px(6), Theme.Px(6));
            int x = Theme.Px(14) + row.Depth * Theme.Px(18);
            if (row.HasKids) Glyph.Triangle(g, new RectangleF(x, y + (rh - Theme.Px(14)) / 2f, Theme.Px(14), Theme.Px(14)), row.Expanded, sel ? Theme.SelText : Theme.Muted);
            x += Theme.Px(16);
            var ir = new RectangleF(x, y + (rh - Theme.Px(16)) / 2f, Theme.Px(16), Theme.Px(16));
            var ic = sel ? Theme.SelText : Theme.AccentText;
            if (row.Kind == Row.Pc) Glyph.Computer(g, ir, ic);
            else if (row.Kind == Row.Lan) Glyph.Lan(g, ir, Theme.Warn);
            else if (row.Kind == Row.Internet) Glyph.Globe(g, ir, Theme.Warn);
            else if (row.Kind == Row.ConnRow) Glyph.Plug(g, ir, sel ? Theme.SelText : Theme.Muted);
            else if (row.Icon != null) g.DrawImage(row.Icon, ir);
            x += Theme.Px(22);
            TextRenderer.DrawText(g, row.Name, row.Kind == Row.Pc ? Theme.UiBold : Theme.UiFont, new Rectangle(x, y, w[0] - x - Theme.Px(4), rh), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // rate cells
            int cx = w[0];
            RateCell(g, new Rectangle(cx, y, w[1], rh), row.Dl, Theme.Dl, sel); cx += w[1];
            RateCell(g, new Rectangle(cx, y, w[2], rh), row.Ul, Theme.Ul, sel); cx += w[2];
            var tc = sel ? Theme.SelText : Theme.Text;
            if (row.TotDl >= 0) TextRenderer.DrawText(g, Fmt.Size(row.TotDl), Theme.UiFont, new Rectangle(cx, y, w[3] - Theme.Px(8), rh), tc, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            cx += w[3];
            if (row.TotUl >= 0) TextRenderer.DrawText(g, Fmt.Size(row.TotUl), Theme.UiFont, new Rectangle(cx, y, w[4] - Theme.Px(8), rh), tc, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            cx += w[4];

            // rule status slots
            if (row.Slots != null)
            {
                using (var b = new SolidBrush(sel ? Theme.Mix(Theme.Sel, Theme.Surface, 0.3f) : Theme.Chip))
                using (var p = Theme.Round(new RectangleF(cx + Theme.Px(3), y + Theme.Px(3), w[5] - Theme.Px(6), rh - Theme.Px(6)), Theme.Px(rh / 2 - 3))) g.FillPath(b, p);
                for (int s = 0; s < 6; s++)
                {
                    int st = row.Slots[s];
                    if (st == 0 && i == hoverRow) st = 1;
                    var r = SlotRect(i, s);
                    if (s == hoverSlot && i == hoverRow) using (var hb = new SolidBrush(Theme.Hover)) g.FillEllipse(hb, RectangleF.Inflate(r, 3, 3));
                    Glyph.Slot(g, r, s, st);
                }
            }
            cx += w[5];
            if (row.Today > 0)
                TextRenderer.DrawText(g, Fmt.Size(row.Today), Theme.UiFont, new Rectangle(cx, y, w[6] - Theme.Px(8), rh), sel ? Theme.SelText : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        void RateCell(Graphics g, Rectangle r, double v, Color c, bool sel)
        {
            bool active = v >= 1;
            if (active) using (var b = new SolidBrush(c)) g.FillRectangle(b, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            TextRenderer.DrawText(g, Fmt.Rate(v), Theme.UiFont, new Rectangle(r.X, r.Y, r.Width - Theme.Px(8), r.Height),
                active ? Color.White : sel ? Theme.SelText : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
    }
}
