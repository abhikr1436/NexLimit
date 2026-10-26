// Rule actions shared by the tree's status slots, the Info View grid, context menus and the Rule List.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace NexLimit
{
    sealed partial class MainForm
    {
        static readonly long[] Presets = { 0, 32, 64, 128, 256, 512, 1024, 2048, 5120, 10240 };   // KB/s

        /// <summary>Refresh everything that shows rules after a change.</summary>
        void Changed()
        {
            Persist();
            if (tabs.Selected == 0) RefreshActivity(); else if (tabs.Selected == 1) RefreshRuleList(); else if (tabs.Selected == 2) RefreshAppList();
        }

        /// <summary>rr: 0 Blocker, 1 Limit, 2 Quota. c: 0 In (download), 1 Out (upload).</summary>
        void Act(Row row, int rr, int c, Rectangle at)
        {
            if (row.Kind == Row.Pc)
            {
                if (rr == 0)
                {
                    bool cur = c == 0 ? shaper.BlockAllIn : shaper.BlockAllOut;
                    if (!cur && MessageBox.Show(this, "Block all internet " + (c == 0 ? "downloads" : "uploads") + " for every app on this PC?\n\nYour local network keeps working.",
                        "NexLimit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    if (c == 0) shaper.BlockAllIn = !cur; else shaper.BlockAllOut = !cur;
                    Changed();
                }
                else if (rr == 1) LimitMenu(c == 0 ? shaper.TotalDown : shaper.TotalUp, "Whole PC " + (c == 0 ? "download" : "upload") + " limit", Changed).Show(at.Left, at.Bottom);
                else ShowPage(4);
                return;
            }
            if (row.Kind != Row.AppRow) return;
            var a = row.App;
            if (rr == 0) { if (c == 0) a.BlockIn = !a.BlockIn; else a.BlockOut = !a.BlockOut; Store.Remember(a); Changed(); }
            else if (rr == 1) LimitMenu(c == 0 ? a.Down : a.Up, NameOf(a) + ": " + (c == 0 ? "download" : "upload") + " limit", () => { Store.Remember(a); Changed(); }).Show(at.Left, at.Bottom);
            else EditQuota(a, c == 1);
        }

        ContextMenuStrip LimitMenu(Bucket b, string title, Action done)
        {
            var m = new ContextMenuStrip { Renderer = new MenuRenderer() };
            m.Items.Add(new ToolStripLabel(title) { Font = Theme.UiBold, ForeColor = Theme.Muted });
            m.Items.Add(new ToolStripSeparator());
            FillLimitItems(m.Items, b, title, done);
            return m;
        }

        void FillLimitItems(ToolStripItemCollection items, Bucket b, string title, Action done)
        {
            foreach (var kb in Presets)
            {
                var v = kb * 1024;
                var it = new ToolStripMenuItem(kb == 0 ? "No limit" : Fmt.Limit(v)) { Checked = b.Rate == v };
                it.Click += (s, e) => { b.Rate = v; done(); };
                items.Add(it);
            }
            items.Add(new ToolStripSeparator());
            var custom = new ToolStripMenuItem("Custom…") { Checked = b.Rate > 0 && !Presets.Contains(b.Rate / 1024) || (b.Rate > 0 && b.Rate % 1024 != 0) };
            custom.Click += (s, e) => { using (var d = new LimitDialog(title, b.Rate)) if (d.ShowDialog(this) == DialogResult.OK) { b.Rate = d.Bps; done(); } };
            items.Add(custom);
        }

        void EditQuota(App a, bool up)
        {
            var q = shaper.Quota;
            var key = a.QuotaPeriod(q, DateTime.Today);
            Func<bool, long[]> raw = monthly =>
            {
                var k = monthly ? Quota.PeriodOf(DateTime.Today, q.ResetDay) : DateTime.Today;
                return a.QuotaUsed(engine.History.Range(k, DateTime.Today), monthly == a.QuotaMonthly ? key : DateTime.MinValue);
            };
            int dir = up ? 1 : 0;
            using (var d = new QuotaDialog(NameOf(a), up, up ? a.QuotaOut : a.QuotaIn, a.QuotaMonthly, raw(a.QuotaMonthly)[dir], Quota.PeriodOf(DateTime.Today, q.ResetDay)))
            {
                var r = d.ShowDialog(this);
                if (r == DialogResult.Yes)
                {
                    if (up) a.QuotaOut = d.Bytes; else a.QuotaIn = d.Bytes;
                    if (d.Monthly != a.QuotaMonthly) { a.QuotaMonthly = d.Monthly; a.QuotaOffKey = DateTime.MinValue; }
                }
                else if (r == DialogResult.No) { if (up) a.QuotaOut = 0; else a.QuotaIn = 0; }
                else if (r == DialogResult.Retry)
                {
                    var newKey = a.QuotaPeriod(q, DateTime.Today);
                    var full = a.QuotaUsed(a.QuotaMonthly ? engine.History.Range(newKey, DateTime.Today) : engine.History.Range(DateTime.Today, DateTime.Today), DateTime.MinValue);
                    if (a.QuotaOffKey != newKey) { a.QuotaOffIn = a.QuotaOffOut = 0; a.QuotaOffKey = newKey; }
                    if (up) a.QuotaOffOut = full[1]; else a.QuotaOffIn = full[0];
                }
                else return;
            }
            Store.Remember(a);
            shaper.CheckAppQuotas(engine.History, DateTime.Today);
            Changed();
        }

        void DeleteRules(App a)
        {
            a.BlockIn = a.BlockOut = false; a.Down.Rate = 0; a.Up.Rate = 0; a.QuotaIn = a.QuotaOut = 0; a.QuotaHitIn = a.QuotaHitOut = false;
            Store.Remember(a); Changed();
        }

        void SetHidden(App a, bool hidden) { a.Hidden = hidden; Store.Remember(a); infoKey = null; Changed(); }

        void OpenLocation(App a) { if (a.Path != null && File.Exists(a.Path)) Process.Start("explorer.exe", "/select,\"" + a.Path + "\""); }

        void KillConn(Conn c) { if (!Engine.Kill(c)) MessageBox.Show(this, "Windows didn't let NexLimit close this connection.", "NexLimit"); Tick(); }

        void KillConnections(App a)
        {
            List<Conn> cs;
            if (!appConns.TryGetValue(a, out cs)) return;
            int n = cs.Count(c => c.Raw != null && c.State != 2 && Engine.Kill(c));
            tray.ShowBalloonTip(3000, "NexLimit", "Closed " + n + " connection" + (n == 1 ? "" : "s") + " of " + NameOf(a) + ". (IPv6 and UDP connections can't be closed this way.)", ToolTipIcon.Info);
            Tick();
        }

        ContextMenuStrip MenuFor(Row row)
        {
            var m = new ContextMenuStrip { Renderer = new MenuRenderer() };
            Func<string, bool, Action, ToolStripMenuItem> item = (t, check, f) => { var it = new ToolStripMenuItem(t) { Checked = check }; it.Click += (s, e) => f(); m.Items.Add(it); return it; };
            if (row.Kind == Row.Pc)
            {
                item("Block all downloads", shaper.BlockAllIn, () => Act(row, 0, 0, Rectangle.Empty));
                item("Block all uploads", shaper.BlockAllOut, () => Act(row, 0, 1, Rectangle.Empty));
                var dl = new ToolStripMenuItem("Download limit (whole PC)"); FillLimitItems(dl.DropDownItems, shaper.TotalDown, "Whole PC download limit", Changed); m.Items.Add(dl);
                var ul = new ToolStripMenuItem("Upload limit (whole PC)"); FillLimitItems(ul.DropDownItems, shaper.TotalUp, "Whole PC upload limit", Changed); m.Items.Add(ul);
                item("Quota settings…", false, () => ShowPage(4));
            }
            else if (row.Kind == Row.AppRow)
            {
                var a = row.App;
                item("Block download", a.BlockIn, () => Act(row, 0, 0, Rectangle.Empty));
                item("Block upload", a.BlockOut, () => Act(row, 0, 1, Rectangle.Empty));
                Action save = () => { Store.Remember(a); Changed(); };
                var dl = new ToolStripMenuItem("Download limit") { Checked = a.Down.Rate > 0 }; FillLimitItems(dl.DropDownItems, a.Down, NameOf(a) + ": download limit", save); m.Items.Add(dl);
                var ul = new ToolStripMenuItem("Upload limit") { Checked = a.Up.Rate > 0 }; FillLimitItems(ul.DropDownItems, a.Up, NameOf(a) + ": upload limit", save); m.Items.Add(ul);
                item("Download quota…", a.QuotaIn > 0, () => EditQuota(a, false));
                item("Upload quota…", a.QuotaOut > 0, () => EditQuota(a, true));
                m.Items.Add(new ToolStripSeparator());
                item("Kill connections", false, () => KillConnections(a)).Enabled = appConns.ContainsKey(a);
                item("Delete rules", false, () => DeleteRules(a)).Enabled = a.Blocked || a.Limited || a.HasQuota;
                item(a.Hidden ? "Unhide" : "Hide from Activity", false, () => SetHidden(a, !a.Hidden));
                item("Traffic statistics", false, () => { statsKey = a.Key; ShowPage(3); });
                item("Open file location", false, () => OpenLocation(a)).Enabled = a.Path != null;
            }
            else if (row.Kind == Row.ConnRow)
            {
                var c = row.Conn;
                item("Kill connection", false, () => KillConn(c)).Enabled = c.Raw != null && c.State != 2;
                item("Copy address", false, () => { try { Clipboard.SetText(c.Remote != null ? c.Remote + ":" + c.RemotePort : "port " + c.LocalPort); } catch (Exception) { } }).Enabled = true;
                m.Items.Add(new ToolStripSeparator());
                var app = new ToolStripMenuItem("App: " + NameOf(row.App));
                foreach (ToolStripItem it in MenuFor(new Row { Kind = Row.AppRow, App = row.App, Key = "a:" + row.App.Key }).Items.Cast<ToolStripItem>().ToList()) app.DropDownItems.Add(it);
                m.Items.Add(app);
            }
            else return null;
            return m;
        }
    }

    /// <summary>Context menus in the current theme.</summary>
    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
            public override Color MenuBorder { get { return Theme.Line; } }
            public override Color MenuItemBorder { get { return Theme.Hover; } }
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color SeparatorDark { get { return Theme.Line; } }
            public override Color SeparatorLight { get { return Theme.Panel; } }
            public override Color CheckBackground { get { return Theme.Sel; } }
            public override Color CheckSelectedBackground { get { return Theme.Sel; } }
            public override Color CheckPressedBackground { get { return Theme.Sel; } }
        }
        public MenuRenderer() : base(new Colors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Theme.Mix(Theme.Panel, Theme.Muted, 0.6f) : e.Item is ToolStripLabel ? Theme.Muted : Theme.Text;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle; e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Accent)) using (var p = Theme.Round(r, 3)) e.Graphics.FillPath(b, p);
            using (var pen = new Pen(Color.White, 2f))
                e.Graphics.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.25f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.7f), new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.32f) });
        }
    }
}
