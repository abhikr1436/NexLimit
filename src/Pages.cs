// Tab pages: Activity (tree + Info View), Rule List, Application List, Traffic Statistics, Quota, Settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace NexLimit
{
    sealed partial class MainForm
    {
        // ================= Activity =================

        readonly TreeGrid tree = new TreeGrid { Dock = DockStyle.Fill };
        readonly ComboBox sortBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        Strip filter;
        readonly HashSet<string> expanded = new HashSet<string> { "pc" };
        int sortMode; bool sortDesc = true; bool syncingSort;
        SplitContainer split;

        readonly Label infoTitle = new Label { AutoSize = true, Font = Theme.Big, Tag = "accent", Margin = new Padding(0, 0, 0, 2) };
        readonly Label infoSub = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(0, 0, 0, 10) };
        readonly Label rulesHead = new Label { Text = "Rules", AutoSize = true, Font = Theme.UiBold, Margin = new Padding(0, 4, 0, 6) };
        readonly RuleGrid ruleGrid = new RuleGrid { Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly Label infoNote = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(0, 8, 0, 0) };
        readonly FlowLayoutPanel tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly TrafficChart chart = new TrafficChart { Dock = DockStyle.Bottom };
        string infoKey;

        Control BuildActivity()
        {
            var page = new Panel();
            split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = Theme.Px(5) };
            var left = split.Panel1; var right = split.Panel2; right.Tag = "panel";

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Px(40), WrapContents = false, Padding = new Padding(Theme.Px(8), Theme.Px(7), 0, 0) };
            sortBox.Items.AddRange(new object[] { "Rate", "Name", "Download", "Upload", "Today", "Total" });
            sortBox.SelectedIndex = 0; sortBox.Width = Theme.Px(110);
            sortBox.SelectedIndexChanged += (s, e) => { if (syncingSort) return; sortMode = sortBox.SelectedIndex; sortDesc = sortMode != 1; RefreshActivity(); };
            filter = new Strip(true, "All", "Online", "Offline", "Hidden"); filter.Width = filter.PreferredWidth; filter.Selected = 1;
            filter.SelectedChanged += (s, e) => RefreshActivity();
            var expandAll = new LinkLabel { Text = "Expand all", AutoSize = true, Margin = new Padding(Theme.Px(16), Theme.Px(6), 0, 0) };
            var collapse = new LinkLabel { Text = "Collapse all", AutoSize = true, Margin = new Padding(Theme.Px(10), Theme.Px(6), 0, 0) };
            expandAll.LinkClicked += (s, e) => { foreach (var a in appConns.Keys) expanded.Add("a:" + a.Key); RefreshActivity(); };
            collapse.LinkClicked += (s, e) => { expanded.Clear(); expanded.Add("pc"); RefreshActivity(); };
            bar.Controls.Add(new Label { Text = "Sort", AutoSize = true, Tag = "muted", Margin = new Padding(0, Theme.Px(6), Theme.Px(6), 0) });
            bar.Controls.Add(sortBox);
            filter.Margin = new Padding(Theme.Px(14), 0, 0, 0);
            bar.Controls.Add(filter); bar.Controls.Add(expandAll); bar.Controls.Add(collapse);
            left.Controls.Add(tree); left.Controls.Add(bar);

            tree.HeaderClicked += c =>
            {
                int m = c == 0 ? 1 : c == 1 ? 2 : c == 2 ? 3 : c == 6 ? 4 : 5;
                if (m == sortMode) sortDesc = !sortDesc; else { sortMode = m; sortDesc = m != 1; }
                syncingSort = true; sortBox.SelectedIndex = sortMode; syncingSort = false;
                RefreshActivity();
            };
            tree.ToggleExpand += r => { if (!expanded.Remove(r.Key)) expanded.Add(r.Key); RefreshActivity(); };
            tree.SelectionChanged += r => UpdateInfo();
            tree.SlotClicked += (r, slot, at) => Act(r, slot / 2, slot % 2, at);
            tree.RowMenu += (r, p) => { var m = MenuFor(r); if (m != null) m.Show(tree, p); };

            // Info View
            var info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Theme.Px(14), Theme.Px(10), Theme.Px(14), 0), AutoScroll = true };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var toolsHead = new Label { Text = "Tools", AutoSize = true, Font = Theme.UiBold, Margin = new Padding(0, 14, 0, 4) };
            infoTitle.MaximumSize = infoSub.MaximumSize = infoNote.MaximumSize = new Size(Theme.Px(370), 0);
            foreach (var c in new Control[] { infoTitle, infoSub, rulesHead, ruleGrid, infoNote, toolsHead, tools }) info.Controls.Add(c);
            ruleGrid.CellClicked += (r, c, at) => { var row = tree.Selected; if (row != null) Act(row, r, c, at); };
            chart.Height = Theme.Px(220);
            right.Controls.Add(info); right.Controls.Add(chart);
            var infoHead = new Panel { Dock = DockStyle.Top, Height = Theme.Px(32) };
            infoHead.Controls.Add(new Label { Text = "Info View", AutoSize = true, Font = Theme.UiBold, Location = new Point(Theme.Px(14), Theme.Px(8)) });
            infoHead.Paint += (s, e) => { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, infoHead.Height - 1, infoHead.Width, infoHead.Height - 1); };
            right.Controls.Add(infoHead);

            page.Controls.Add(split);
            bool sized = false;
            page.Layout += (s, e) =>
            {
                if (sized || page.Width < Theme.Px(600)) return;
                sized = true;
                try { split.Panel2MinSize = Theme.Px(330); split.SplitterDistance = page.Width - Theme.Px(420); } catch (Exception) { }
            };
            return page;
        }

        int[] AppSlots(App a)
        {
            return new[] { a.BlockIn ? 2 : 0, a.BlockOut ? 2 : 0, a.Down.Rate > 0 ? 2 : 0, a.Up.Rate > 0 ? 2 : 0,
                a.QuotaIn > 0 ? (a.QuotaHitIn ? 3 : 2) : 0, a.QuotaOut > 0 ? (a.QuotaHitOut ? 3 : 2) : 0 };
        }
        int[] PcSlots()
        {
            var q = shaper.Quota; int qs = q.DayLimit > 0 || q.MonthLimit > 0 ? (q.IsBlocked ? 3 : 2) : 0;
            return new[] { shaper.BlockAllIn ? 2 : 0, shaper.BlockAllOut ? 2 : 0, shaper.TotalDown.Rate > 0 ? 2 : 0, shaper.TotalUp.Rate > 0 ? 2 : 0, qs, qs };
        }

        bool Matches(App a)
        {
            var t = search.Text.Trim();
            if (t.Length == 0) return true;
            return NameOf(a).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || (a.Path ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        IEnumerable<App> ActivityApps()
        {
            var f = filter.Selected;
            var apps = shaper.AppList.Where(a => f == 3 ? a.Hidden : !a.Hidden && (f == 0 || (f == 1) == IsOnline(a))).Where(Matches);
            Func<App, double> r0 = a => rate.ContainsKey(a) ? rate[a][0] : 0, r1 = a => rate.ContainsKey(a) ? rate[a][1] : 0;
            IOrderedEnumerable<App> o;
            switch (sortMode)
            {
                case 1: o = sortDesc ? apps.OrderByDescending(NameOf, StringComparer.CurrentCultureIgnoreCase) : apps.OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase); return o;
                case 2: o = sortDesc ? apps.OrderByDescending(r0) : apps.OrderBy(r0); break;
                case 3: o = sortDesc ? apps.OrderByDescending(r1) : apps.OrderBy(r1); break;
                case 5: o = sortDesc ? apps.OrderByDescending(a => Total(a).Sum()) : apps.OrderBy(a => Total(a).Sum()); break;
                case 4: o = sortDesc ? apps.OrderByDescending(a => Today(a, 0) + Today(a, 1)) : apps.OrderBy(a => Today(a, 0) + Today(a, 1)); break;
                default: o = sortDesc ? apps.OrderByDescending(a => r0(a) + r1(a)) : apps.OrderBy(a => r0(a) + r1(a)); break;
            }
            return o.ThenByDescending(a => a.LastSeen).ThenBy(NameOf, StringComparer.CurrentCultureIgnoreCase);
        }

        Dictionary<string, long[]> allTime = new Dictionary<string, long[]>();

        /// <summary>Everything an app has used since NexLimit started counting: saved history + bytes not saved yet.</summary>
        long[] Total(App a)
        {
            long[] v; allTime.TryGetValue(a.Key, out v);
            return new[] { (v == null ? 0 : v[0]) + Interlocked.Read(ref a.BytesDown) - a.SavedDown, (v == null ? 0 : v[1]) + Interlocked.Read(ref a.BytesUp) - a.SavedUp };
        }

        void RefreshActivity()
        {
            var rows = new List<Row>();
            allTime = engine.History.Range(DateTime.MinValue, DateTime.Today);
            long totD = 0, totU = 0;
            foreach (var a in shaper.AppList) { var t = Total(a); totD += t[0]; totU += t[1]; }
            foreach (var kv in allTime) if (shaper.Get(kv.Key) == null) { totD += kv.Value[0]; totU += kv.Value[1]; }
            long todayAll = today.Values.Sum(v => v[0] + v[1]) + shaper.AppList.Sum(a => Interlocked.Read(ref a.BytesDown) - a.SavedDown + Interlocked.Read(ref a.BytesUp) - a.SavedUp);
            var pc = new Row { Kind = Row.Pc, Key = "pc", Name = Environment.MachineName, Sub = "This computer", Dl = pcDl, Ul = pcUl, HasKids = true, Expanded = expanded.Contains("pc"), Online = true, Slots = PcSlots(), Today = todayAll, TotDl = totD, TotUl = totU };
            rows.Add(pc);
            if (pc.Expanded)
            {
                rows.Add(new Row { Kind = Row.Lan, Key = "lan", Name = "LocalNetwork", Depth = 1, Dl = lanDl, Ul = lanUl, Sub = "Devices on your own network. Never limited, blocked or counted." });
                rows.Add(new Row { Kind = Row.Internet, Key = "inet", Name = "Internet", Depth = 1, Dl = inetDl, Ul = inetUl, Today = todayAll, TotDl = totD, TotUl = totU, Sub = "Everything outside your local network" });
                foreach (var a in ActivityApps())
                {
                    List<Conn> cs; appConns.TryGetValue(a, out cs);
                    var key = "a:" + a.Key;
                    var r = new Row
                    {
                        Kind = Row.AppRow, Key = key, App = a, Name = NameOf(a), Sub = a.Path ?? "Windows and traffic without a program file", Icon = IconOf(a), Depth = 1,
                        Dl = rate.ContainsKey(a) ? rate[a][0] : 0, Ul = rate.ContainsKey(a) ? rate[a][1] : 0, HasKids = cs != null, Expanded = cs != null && expanded.Contains(key),
                        Online = IsOnline(a), Slots = AppSlots(a), Today = Today(a, 0) + Today(a, 1)
                    };
                    var tot = Total(a); r.TotDl = tot[0]; r.TotUl = tot[1];
                    rows.Add(r);
                    if (!r.Expanded) continue;
                    foreach (var c in cs.OrderByDescending(c => PortRate(c).Sum()).ThenBy(c => c.Proto).ThenBy(c => c.Remote).Take(200))
                    {
                        var pr = PortRate(c);
                        rows.Add(new Row { Kind = Row.ConnRow, Key = "c:" + a.Key + "|" + c.Key, Conn = c, App = a, Name = ConnName(c), Sub = ConnSub(c), Depth = 2, Dl = pr[0], Ul = pr[1] });
                    }
                }
            }
            tree.SortCol = sortMode == 1 ? 0 : sortMode == 2 ? 1 : sortMode == 3 ? 2 : sortMode == 4 ? 6 : sortMode == 5 ? 3 : -1; tree.SortDesc = sortDesc;
            if (tree.SelectedKey != null && !rows.Any(r => r.Key == tree.SelectedKey)) tree.SelectedKey = "pc";
            tree.SetRows(rows);
            UpdateInfo();
        }

        double[] PortRate(Conn c) { double[] r; return portRate.TryGetValue((c.Proto == 6 ? 0 : 65536) + c.LocalPort, out r) ? r : new double[2]; }

        static string ConnName(Conn c)
        {
            if (c.Proto == 17) return "UDP   port " + c.LocalPort;
            if (c.State == 2) return "TCP   listening on port " + c.LocalPort;
            return "TCP   " + (c.V6 ? "[" + c.Remote + "]" : c.Remote) + " : " + c.RemotePort;
        }
        static string ConnSub(Conn c)
        {
            return (c.Proto == 6 ? "TCP " + Engine.StateName(c.State) : "UDP") + ", local port " + c.LocalPort + (c.V6 ? ", IPv6" : ", IPv4");
        }

        string SeriesKey(Row r)
        {
            switch (r.Kind) { case Row.Pc: return "pc"; case Row.Lan: return "lan"; case Row.Internet: return "inet"; default: return r.App.Key; }
        }

        void UpdateInfo()
        {
            var row = tree.Selected ?? (tree.Rows.Count > 0 ? tree.Rows[0] : null);
            if (row == null) return;
            bool changed = row.Key != infoKey; infoKey = row.Key;
            infoTitle.Text = row.Kind == Row.ConnRow ? row.Name.Replace("   ", " ") : row.Name;
            var q = shaper.Quota;
            if (row.Kind == Row.Pc || row.Kind == Row.AppRow)
            {
                ruleGrid.Visible = rulesHead.Visible = true; infoNote.Visible = false;
                ruleGrid.Span[2] = row.Kind == Row.Pc;
                if (row.Kind == Row.Pc)
                {
                    infoSub.Text = "Rules for the whole computer (internet traffic only).";
                    ruleGrid.Cells[0, 0] = shaper.BlockAllIn ? "All downloads blocked" : null; ruleGrid.Cells[0, 1] = shaper.BlockAllOut ? "All uploads blocked" : null;
                    ruleGrid.Cells[1, 0] = shaper.TotalDown.Rate > 0 ? Fmt.Limit(shaper.TotalDown.Rate) : null; ruleGrid.Cells[1, 1] = shaper.TotalUp.Rate > 0 ? Fmt.Limit(shaper.TotalUp.Rate) : null;
                    var parts = new List<string>();
                    if (q.DayLimit > 0) parts.Add(Fmt.Size(q.DayLimit) + "/d " + Math.Min(999, q.DayUsed * 100 / q.DayLimit) + "%");
                    if (q.MonthLimit > 0) parts.Add(Fmt.Size(q.MonthLimit) + "/mo " + Math.Min(999, q.MonthUsed * 100 / q.MonthLimit) + "%");
                    ruleGrid.Cells[2, 0] = parts.Count == 0 ? null : (q.IsBlocked ? "Used up · " : "") + string.Join(" · ", parts);
                    var s = PcSlots(); for (int i = 0; i < 6; i++) ruleGrid.States[i / 2, i % 2] = s[i];
                }
                else
                {
                    var a = row.App;
                    infoSub.Text = (a.Path ?? "Windows and traffic without a program file") + (a.Hidden ? "\n(hidden from the Activity list)" : "");
                    ruleGrid.Cells[0, 0] = a.BlockIn ? "Blocked" : null; ruleGrid.Cells[0, 1] = a.BlockOut ? "Blocked" : null;
                    ruleGrid.Cells[1, 0] = a.Down.Rate > 0 ? Fmt.Limit(a.Down.Rate) : null; ruleGrid.Cells[1, 1] = a.Up.Rate > 0 ? Fmt.Limit(a.Up.Rate) : null;
                    if (a.HasQuota)
                    {
                        var key = a.QuotaPeriod(q, DateTime.Today);
                        var used = a.QuotaUsed(a.QuotaMonthly ? engine.History.Range(key, DateTime.Today) : today, key);
                        var per = a.QuotaMonthly ? "/mo" : "/d";
                        ruleGrid.Cells[2, 0] = a.QuotaIn > 0 ? (a.QuotaHitIn ? "Used up · " : "") + Fmt.Size(a.QuotaIn) + per + " · " + Math.Min(999, used[0] * 100 / a.QuotaIn) + "%" : null;
                        ruleGrid.Cells[2, 1] = a.QuotaOut > 0 ? (a.QuotaHitOut ? "Used up · " : "") + Fmt.Size(a.QuotaOut) + per + " · " + Math.Min(999, used[1] * 100 / a.QuotaOut) + "%" : null;
                    }
                    else ruleGrid.Cells[2, 0] = ruleGrid.Cells[2, 1] = null;
                    var s = AppSlots(a); for (int i = 0; i < 6; i++) ruleGrid.States[i / 2, i % 2] = s[i];
                }
                ruleGrid.Invalidate();
            }
            else
            {
                ruleGrid.Visible = rulesHead.Visible = false; infoNote.Visible = true;
                if (row.Kind == Row.ConnRow) { infoSub.Text = NameOf(row.App); infoNote.Text = row.Sub + "\nDownload " + Fmt.Rate(row.Dl) + ", upload " + Fmt.Rate(row.Ul) + "\nRules apply to the whole app."; }
                else { infoSub.Text = ""; infoNote.Text = row.Sub + "\nDownload " + Fmt.Rate(row.Dl) + ", upload " + Fmt.Rate(row.Ul); }
            }
            if (changed) BuildTools(row);
            Series ser; series.TryGetValue(SeriesKey(row), out ser);
            chart.Caption = row.Kind == Row.ConnRow ? NameOf(row.App) : row.Name;
            chart.Down = ser == null ? new double[0] : ser.Ordered(ser.D); chart.Up = ser == null ? new double[0] : ser.Ordered(ser.U);
            chart.Invalidate();
        }

        static string Pct(long used, long limit) { return Math.Min(999, used * 100 / Math.Max(1, limit)) + "% used"; }

        void BuildTools(Row row)
        {
            tools.SuspendLayout(); tools.Controls.Clear();
            Action<string, Action> link = (t, f) =>
            {
                var l = new LinkLabel { Text = t, AutoSize = true, Margin = new Padding(0, 2, Theme.Px(14), 4) };
                l.LinkClicked += (s, e) => f();
                tools.Controls.Add(l);
            };
            if (row.Kind == Row.Pc)
            {
                link("Traffic stats", () => { statsKey = null; ShowPage(3); });
                link("Quota settings", () => ShowPage(4));
                link("Rule list", () => ShowPage(1));
            }
            else if (row.Kind == Row.AppRow)
            {
                var a = row.App;
                link("Traffic stats", () => { statsKey = a.Key; ShowPage(3); });
                link("Delete rules", () => DeleteRules(a));
                link("Kill connections", () => KillConnections(a));
                link(a.Hidden ? "Unhide" : "Hide", () => SetHidden(a, !a.Hidden));
                if (a.Path != null) link("Open file location", () => OpenLocation(a));
            }
            else if (row.Kind == Row.ConnRow && row.Conn.Raw != null) link("Kill connection", () => KillConn(row.Conn));
            Theme.Apply(tools);
            tools.ResumeLayout();
        }

        // ================= Rule List =================

        readonly ThemedList ruleList = new ThemedList();

        sealed class RuleRef { public App App; public int Kind, Dir; public string Id { get { return (App == null ? "pc" : App.Key) + "|" + Kind + "|" + Dir; } } }

        Control BuildRuleList()
        {
            var page = new Panel();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Px(42), WrapContents = false, Padding = new Padding(Theme.Px(8), Theme.Px(7), 0, 0) };
            var del = new Button { Text = "Delete selected rules", AutoSize = true };
            del.Click += (s, e) => DeleteSelectedRules();
            bar.Controls.Add(del);
            bar.Controls.Add(new Label { Text = "Double-click a rule to change it. Rules are remembered by program path.", AutoSize = true, Tag = "muted", Margin = new Padding(Theme.Px(12), Theme.Px(6), 0, 0) });
            ruleList.Columns.Add("Application", Theme.Px(260)); ruleList.Columns.Add("Rule", Theme.Px(90)); ruleList.Columns.Add("Direction", Theme.Px(110));
            ruleList.Columns.Add("Value", Theme.Px(200)); ruleList.Columns.Add("Status", Theme.Px(220)); ruleList.Columns.Add("Path", Theme.Px(380));
            ruleList.SmallImageList = imageList;
            ruleList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) DeleteSelectedRules(); };
            ruleList.DoubleClick += (s, e) =>
            {
                if (ruleList.SelectedItems.Count == 0) return;
                var r = (RuleRef)ruleList.SelectedItems[0].Tag;
                if (r.Kind == 3) { SetHidden(r.App, false); return; }
                var row = r.App == null ? new Row { Kind = Row.Pc, Key = "pc" } : new Row { Kind = Row.AppRow, App = r.App, Key = "a:" + r.App.Key };
                Act(row, r.Kind, r.Dir, ruleList.RectangleToScreen(ruleList.SelectedItems[0].Bounds));
            };
            page.Controls.Add(ruleList); page.Controls.Add(bar);
            return page;
        }

        void RefreshRuleList()
        {
            var selected = new HashSet<string>(ruleList.SelectedItems.Cast<ListViewItem>().Select(i => ((RuleRef)i.Tag).Id));
            var top = ruleList.TopItem == null ? 0 : ruleList.TopItem.Index;
            ruleList.BeginUpdate(); ruleList.Items.Clear();
            var q = shaper.Quota;
            Action<App, int, int, string, string> add = (a, kind, dir, value, status) =>
            {
                var r = new RuleRef { App = a, Kind = kind, Dir = dir };
                var it = new ListViewItem(a == null ? Environment.MachineName + " (whole PC)" : NameOf(a), a == null ? -1 : IconIndex(a)) { Tag = r };
                it.SubItems.Add(new[] { "Blocker", "Limit", "Quota", "Hidden" }[kind]);
                it.SubItems.Add(kind == 3 ? "" : kind == 2 && a == null ? "In + Out" : dir == 0 ? "In (download)" : "Out (upload)");
                it.SubItems.Add(value); it.SubItems.Add(status); it.SubItems.Add(a == null ? "" : a.Path ?? "");
                if (status.StartsWith("Used up")) it.ForeColor = Theme.Bad;
                it.Selected = selected.Contains(r.Id);
                ruleList.Items.Add(it);
            };
            string bOff = shaper.BlockerOn ? "Active" : "Off (Blocker switch)", lOff = shaper.LimiterOn ? "Active" : "Off (Limiter switch)", qOff = shaper.QuotaOn ? null : "Off (Quota switch)";
            if (shaper.BlockAllIn) add(null, 0, 0, "Block all", bOff);
            if (shaper.BlockAllOut) add(null, 0, 1, "Block all", bOff);
            if (shaper.TotalDown.Rate > 0) add(null, 1, 0, Fmt.Limit(shaper.TotalDown.Rate), lOff);
            if (shaper.TotalUp.Rate > 0) add(null, 1, 1, Fmt.Limit(shaper.TotalUp.Rate), lOff);
            if (q.DayLimit > 0) add(null, 2, 0, Fmt.Size(q.DayLimit) + " per day", qOff ?? (q.DayOver ? "Used up" : Pct(q.DayUsed, q.DayLimit)));
            if (q.MonthLimit > 0) add(null, 2, 1, Fmt.Size(q.MonthLimit) + " per month", qOff ?? (q.MonthOver ? "Used up" : Pct(q.MonthUsed, q.MonthLimit)));
            Dictionary<string, long[]> month = null;
            foreach (var a in shaper.AppList.Where(a => a.HasRule).OrderBy(NameOf))
            {
                if (a.BlockIn) add(a, 0, 0, "Blocked", bOff);
                if (a.BlockOut) add(a, 0, 1, "Blocked", bOff);
                if (a.Down.Rate > 0) add(a, 1, 0, Fmt.Limit(a.Down.Rate), lOff);
                if (a.Up.Rate > 0) add(a, 1, 1, Fmt.Limit(a.Up.Rate), lOff);
                if (a.HasQuota)
                {
                    var key = a.QuotaPeriod(q, DateTime.Today);
                    var used = a.QuotaUsed(a.QuotaMonthly ? (month ?? (month = engine.History.Range(key, DateTime.Today))) : today, key);
                    var per = a.QuotaMonthly ? " per month" : " per day";
                    if (a.QuotaIn > 0) add(a, 2, 0, Fmt.Size(a.QuotaIn) + per, qOff ?? (a.QuotaHitIn ? "Used up" : Pct(used[0], a.QuotaIn)));
                    if (a.QuotaOut > 0) add(a, 2, 1, Fmt.Size(a.QuotaOut) + per, qOff ?? (a.QuotaHitOut ? "Used up" : Pct(used[1], a.QuotaOut)));
                }
                if (a.Hidden) add(a, 3, 0, "Hidden from Activity", "Double-click to unhide");
            }
            if (ruleList.Items.Count == 0) ruleList.Items.Add(new ListViewItem("No rules yet. In Activity, click a dot in Rule Status or right-click an app.") { Tag = null, ForeColor = Theme.Muted });
            ruleList.EndUpdate();
            if (top > 0 && top < ruleList.Items.Count) try { ruleList.TopItem = ruleList.Items[top]; } catch (Exception) { }
        }

        void DeleteSelectedRules()
        {
            foreach (ListViewItem it in ruleList.SelectedItems)
            {
                var r = it.Tag as RuleRef; if (r == null) continue;
                if (r.App == null)
                {
                    if (r.Kind == 0) { if (r.Dir == 0) shaper.BlockAllIn = false; else shaper.BlockAllOut = false; }
                    else if (r.Kind == 1) (r.Dir == 0 ? shaper.TotalDown : shaper.TotalUp).Rate = 0;
                    else if (r.Kind == 2) { if (r.Dir == 0) shaper.Quota.DayLimit = 0; else shaper.Quota.MonthLimit = 0; LoadQuota(); }
                }
                else
                {
                    var a = r.App;
                    if (r.Kind == 0) { if (r.Dir == 0) a.BlockIn = false; else a.BlockOut = false; }
                    else if (r.Kind == 1) (r.Dir == 0 ? a.Down : a.Up).Rate = 0;
                    else if (r.Kind == 2) { if (r.Dir == 0) a.QuotaIn = 0; else a.QuotaOut = 0; }
                    else a.Hidden = false;
                    Store.Remember(a);
                }
            }
            Changed();
        }

        // ================= Application List =================

        readonly ThemedList appList = new ThemedList();

        Control BuildAppList()
        {
            var page = new Panel();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Px(42), WrapContents = false, Padding = new Padding(Theme.Px(8), Theme.Px(10), 0, 0) };
            bar.Controls.Add(new Label { Text = "Every program that used the network or has a rule. Right-click for actions, double-click to find it in Activity.", AutoSize = true, Tag = "muted" });
            appList.Columns.Add("Application", Theme.Px(250)); appList.Columns.Add("Status", Theme.Px(80)); appList.Columns.Add("Rules", Theme.Px(230));
            appList.Columns.Add("Today", Theme.Px(90), HorizontalAlignment.Right); appList.Columns.Add("This month", Theme.Px(100), HorizontalAlignment.Right);
            appList.Columns.Add("Path", Theme.Px(420));
            appList.SmallImageList = imageList;
            appList.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || appList.SelectedItems.Count == 0) return;
                var a = (App)appList.SelectedItems[0].Tag;
                var m = MenuFor(new Row { Kind = Row.AppRow, App = shaper.GetOrAdd(a.Path), Key = "a:" + a.Key });
                m.Show(appList, e.Location);
            };
            appList.DoubleClick += (s, e) =>
            {
                if (appList.SelectedItems.Count == 0) return;
                var a = shaper.GetOrAdd(((App)appList.SelectedItems[0].Tag).Path);
                filter.Selected = a.Hidden ? 3 : 0; expanded.Add("pc"); search.Text = "";
                tree.SelectedKey = "a:" + a.Key; ShowPage(0);
            };
            page.Controls.Add(appList); page.Controls.Add(bar);
            return page;
        }

        static string RuleSummary(App a)
        {
            var p = new List<string>();
            if (a.BlockIn && a.BlockOut) p.Add("Blocked"); else if (a.BlockIn) p.Add("Download blocked"); else if (a.BlockOut) p.Add("Upload blocked");
            if (a.Down.Rate > 0) p.Add("↓ " + Fmt.Limit(a.Down.Rate)); if (a.Up.Rate > 0) p.Add("↑ " + Fmt.Limit(a.Up.Rate));
            if (a.HasQuota) p.Add(a.QuotaHitIn || a.QuotaHitOut ? "Quota used up" : "Quota");
            if (a.Hidden) p.Add("Hidden");
            return string.Join(", ", p);
        }

        void RefreshAppList()
        {
            var sel = appList.SelectedItems.Count > 0 ? ((App)appList.SelectedItems[0].Tag).Key : null;
            var top = appList.TopItem == null ? 0 : appList.TopItem.Index;
            var month = engine.History.Range(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);
            var keys = new HashSet<string>(shaper.AppList.Select(a => a.Key)); keys.UnionWith(engine.History.Keys());
            appList.BeginUpdate(); appList.Items.Clear();
            foreach (var a in keys.Select(AppOf).Where(Matches).OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
            {
                long[] m; month.TryGetValue(a.Key, out m);
                long unsaved = Interlocked.Read(ref a.BytesDown) - a.SavedDown + Interlocked.Read(ref a.BytesUp) - a.SavedUp;
                var it = new ListViewItem(NameOf(a), IconIndex(a)) { Tag = a };
                it.SubItems.Add(a.Hidden ? "Hidden" : IsOnline(a) ? "Online" : "Offline");
                it.SubItems.Add(RuleSummary(a));
                var t = Today(a, 0) + Today(a, 1); it.SubItems.Add(t > 0 ? Fmt.Size(t) : "");
                var mt = (m == null ? 0 : m[0] + m[1]) + unsaved; it.SubItems.Add(mt > 0 ? Fmt.Size(mt) : "");
                it.SubItems.Add(a.Path ?? "");
                if (a.Hidden) it.ForeColor = Theme.Muted;
                it.Selected = a.Key == sel;
                appList.Items.Add(it);
            }
            appList.EndUpdate();
            if (top > 0 && top < appList.Items.Count) try { appList.TopItem = appList.Items[top]; } catch (Exception) { }
        }

        // ================= Traffic Statistics =================

        readonly ThemedList statsList = new ThemedList();
        readonly ComboBox statsRange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label statsTotal = new Label { AutoSize = true, Font = Theme.UiBold, Margin = new Padding(Theme.Px(14), Theme.Px(6), 0, 0) };
        readonly Label barsTitle = new Label { AutoSize = true, Font = Theme.UiBold, Location = new Point(Theme.Px(12), Theme.Px(8)) };
        readonly DailyBars bars = new DailyBars { Dock = DockStyle.Fill };
        string statsKey;
        bool statsLoading;

        Control BuildStats()
        {
            var page = new Panel();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Px(42), WrapContents = false, Padding = new Padding(Theme.Px(8), Theme.Px(8), 0, 0) };
            statsRange.Items.AddRange(new object[] { "Today", "Yesterday", "Last 7 days", "Last 30 days", "This quota period", "This month", "Last month", "All time" });
            statsRange.SelectedIndex = 3; statsRange.Width = Theme.Px(150);
            statsRange.SelectedIndexChanged += (s, e) => RefreshStats();
            var all = new LinkLabel { Text = "Show all apps in the chart", AutoSize = true, Margin = new Padding(Theme.Px(14), Theme.Px(6), 0, 0) };
            all.LinkClicked += (s, e) => { statsKey = null; statsList.SelectedItems.Clear(); RefreshStats(); };
            bar.Controls.Add(new Label { Text = "Show", AutoSize = true, Tag = "muted", Margin = new Padding(0, Theme.Px(6), Theme.Px(6), 0) });
            bar.Controls.Add(statsRange); bar.Controls.Add(statsTotal); bar.Controls.Add(all);
            var chartBox = new Panel { Dock = DockStyle.Top, Height = Theme.Px(200), Tag = "panel", Padding = new Padding(0, Theme.Px(28), 0, 0) };
            chartBox.Controls.Add(bars); chartBox.Controls.Add(barsTitle);
            statsList.Columns.Add("Application", Theme.Px(280)); statsList.Columns.Add("Download", Theme.Px(110), HorizontalAlignment.Right);
            statsList.Columns.Add("Upload", Theme.Px(110), HorizontalAlignment.Right); statsList.Columns.Add("Total", Theme.Px(110), HorizontalAlignment.Right);
            statsList.Columns.Add("Path", Theme.Px(420));
            statsList.SmallImageList = imageList;
            statsList.SelectedIndexChanged += (s, e) =>
            {
                if (statsLoading || statsList.SelectedItems.Count == 0) return;
                statsKey = ((App)statsList.SelectedItems[0].Tag).Key; RefreshBars();
            };
            page.Controls.Add(statsList); page.Controls.Add(chartBox); page.Controls.Add(bar);
            return page;
        }

        void StatsRange(out DateTime from, out DateTime to)
        {
            var t = DateTime.Today; from = to = t;
            switch (statsRange.SelectedIndex)
            {
                case 1: from = to = t.AddDays(-1); break;
                case 2: from = t.AddDays(-6); break;
                case 3: from = t.AddDays(-29); break;
                case 4: from = Quota.PeriodOf(t, shaper.Quota.ResetDay); break;
                case 5: from = new DateTime(t.Year, t.Month, 1); break;
                case 6: from = new DateTime(t.Year, t.Month, 1).AddMonths(-1); to = from.AddMonths(1).AddDays(-1); break;
                case 7: from = DateTime.MinValue; break;
            }
        }

        void RefreshStats()
        {
            engine.Save();
            DateTime from, to; StatsRange(out from, out to);
            var data = engine.History.Range(from, to);
            statsLoading = true;
            statsList.BeginUpdate(); statsList.Items.Clear();
            long sd = 0, su = 0;
            foreach (var kv in data.OrderByDescending(k => k.Value[0] + k.Value[1]))
            {
                var a = AppOf(kv.Key);
                if (!Matches(a)) continue;
                var it = new ListViewItem(NameOf(a), IconIndex(a)) { Tag = a };
                it.SubItems.Add(Fmt.Size(kv.Value[0])); it.SubItems.Add(Fmt.Size(kv.Value[1]));
                it.SubItems.Add(Fmt.Size(kv.Value[0] + kv.Value[1])); it.SubItems.Add(a.Path ?? "");
                it.Selected = a.Key == statsKey;
                statsList.Items.Add(it); sd += kv.Value[0]; su += kv.Value[1];
            }
            statsList.EndUpdate();
            statsLoading = false;
            statsTotal.Text = "Total " + Fmt.Size(sd + su) + "    ↓ " + Fmt.Size(sd) + "    ↑ " + Fmt.Size(su);
            RefreshBars();
        }

        void RefreshBars()
        {
            DateTime from, to; StatsRange(out from, out to);
            var d = engine.History.Days(from, to, statsKey);
            if (statsRange.SelectedIndex <= 1 && d.Count == 0) d[from] = new long[2];
            bars.Data = d; bars.Invalidate();
            barsTitle.Text = "Data per day: " + (statsKey == null ? "all apps" : NameOf(AppOf(statsKey)));
        }

        // ================= Quota (whole PC) =================

        readonly Switch dayOn = new Switch("Daily limit"), monthOn = new Switch("Monthly limit"), warn90 = new Switch("Warn me when 90% is used");
        readonly NumericUpDown dayAmt = Num(1, 1000000, 2), monthAmt = Num(1, 1000000, 50), resetDay = Num(1, 28, 1);
        readonly ComboBox dayUnit = Units(), monthUnit = Units();
        readonly Meter dayBar = new Meter(), monthBar = new Meter();
        readonly Label dayUsed = new Label { AutoSize = true }, monthUsed = new Label { AutoSize = true };
        readonly Label quotaState = new Label { AutoSize = true, Font = Theme.UiBold };
        readonly Button allowToday = new Button { Text = "Allow internet for the rest of today", AutoSize = true };
        bool loadingQuota;

        static NumericUpDown Num(int min, int max, int val) { return new NumericUpDown { Minimum = min, Maximum = max, Value = val, Width = Theme.Px(90), ThousandsSeparator = true }; }
        static ComboBox Units() { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.Px(60) }; c.Items.AddRange(new object[] { "MB", "GB" }); c.SelectedIndex = 1; return c; }

        Control BuildQuota()
        {
            var page = new Panel { AutoScroll = true };
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(Theme.Px(20), Theme.Px(16), Theme.Px(20), Theme.Px(16)) };
            Func<Control[], FlowLayoutPanel> row = cs =>
            {
                var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
                foreach (var c in cs) { if (c is Label || c is CheckBox) c.Margin = new Padding(3, 6, 6, 0); f.Controls.Add(c); }
                return f;
            };
            Func<string, Label> L = s => new Label { Text = s, AutoSize = true };
            var resetD = new Button { Text = "Reset today's usage", AutoSize = true };
            var resetM = new Button { Text = "Reset this period's usage", AutoSize = true };
            resetD.Click += (s, e) => { shaper.Quota.ResetDayUsage(); Persist(); RefreshQuota(); };
            resetM.Click += (s, e) => { shaper.Quota.ResetMonthUsage(); Persist(); RefreshQuota(); };
            allowToday.Click += (s, e) => AllowToday();
            foreach (var b in new[] { dayBar, monthBar }) { b.Size = new Size(Theme.Px(360), Theme.Px(10)); b.Margin = new Padding(Theme.Px(26), Theme.Px(10), Theme.Px(10), 0); }

            var head = new Label { Text = "Data quota for this PC", Font = Theme.Big, AutoSize = true, Margin = new Padding(3, 0, 0, 6) };
            var note = new Label
            {
                Text = "Counts internet traffic in both directions (download + upload). When a quota is used up, NexLimit blocks the internet and tells you.\n"
                    + "Your local network (printers, shared folders, other PCs) keeps working. Usage history is never changed by a reset.\n"
                    + "Quotas for single apps: select the app in Activity and click its Quota cell in the Info View.",
                AutoSize = true, Tag = "muted", MaximumSize = new Size(Theme.Px(820), 0), Margin = new Padding(3, 0, 0, 14)
            };
            t.Controls.Add(head); t.Controls.Add(note);
            t.Controls.Add(row(new Control[] { dayOn, dayAmt, dayUnit, L("per day") }));
            t.Controls.Add(row(new Control[] { dayBar, dayUsed, resetD }));
            t.Controls.Add(new Label { Height = 10 });
            t.Controls.Add(row(new Control[] { monthOn, monthAmt, monthUnit, L("per month, starting on day"), resetDay, L("of each month") }));
            t.Controls.Add(row(new Control[] { monthBar, monthUsed, resetM }));
            t.Controls.Add(new Label { Height = 10 });
            t.Controls.Add(row(new Control[] { warn90 }));
            t.Controls.Add(new Label { Height = 10 });
            t.Controls.Add(row(new Control[] { quotaState, allowToday }));
            page.Controls.Add(t);

            LoadQuota();
            EventHandler apply = (s, e) => ApplyQuota();
            dayOn.CheckedChanged += apply; monthOn.CheckedChanged += apply; warn90.CheckedChanged += apply;
            dayOn.Width = monthOn.Width = Theme.Px(120);
            dayAmt.ValueChanged += apply; monthAmt.ValueChanged += apply; resetDay.ValueChanged += apply;
            dayUnit.SelectedIndexChanged += apply; monthUnit.SelectedIndexChanged += apply;
            return page;
        }

        static void ShowAmount(long bytes, NumericUpDown n, ComboBox unit)
        {
            if (bytes <= 0) return;
            bool gb = bytes % (1L << 30) == 0;
            unit.SelectedIndex = gb ? 1 : 0;
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, bytes >> (gb ? 30 : 20)));
        }

        void LoadQuota()
        {
            loadingQuota = true;
            var q = shaper.Quota;
            dayOn.Checked = q.DayLimit > 0; monthOn.Checked = q.MonthLimit > 0; warn90.Checked = q.WarnAt90;
            ShowAmount(q.DayLimit, dayAmt, dayUnit); ShowAmount(q.MonthLimit, monthAmt, monthUnit);
            resetDay.Value = q.ResetDay;
            loadingQuota = false;
        }

        void ApplyQuota()
        {
            if (loadingQuota) return;
            var q = shaper.Quota;
            q.DayLimit = dayOn.Checked ? (long)dayAmt.Value << (dayUnit.SelectedIndex == 1 ? 30 : 20) : 0;
            q.MonthLimit = monthOn.Checked ? (long)monthAmt.Value << (monthUnit.SelectedIndex == 1 ? 30 : 20) : 0;
            q.WarnAt90 = warn90.Checked;
            if (q.ResetDay != (int)resetDay.Value) { q.ResetDay = (int)resetDay.Value; q.PeriodStart = Quota.PeriodOf(DateTime.Today, q.ResetDay); }
            Persist(); RefreshQuota();
        }

        void RefreshQuota()
        {
            var q = shaper.Quota;
            Action<Meter, Label, long, long, string> show = (bar, lbl, used, limit, what) =>
            {
                bar.Visible = limit > 0;
                bar.Value = limit > 0 ? (int)Math.Min(100, used * 100 / limit) : 0;
                lbl.Text = limit > 0 ? "Used " + what + ": " + Fmt.Size(used) + " of " + Fmt.Size(limit) : "Used " + what + ": " + Fmt.Size(used) + " (no limit)";
            };
            show(dayBar, dayUsed, Interlocked.Read(ref q.DayUsed), q.DayLimit, "today");
            show(monthBar, monthUsed, Interlocked.Read(ref q.MonthUsed), q.MonthLimit, "since " + q.PeriodStart.ToString("d MMM"));
            bool allowed = q.AllowDay == DateTime.Today && (q.DayOver || q.MonthOver);
            bool blocked = q.IsBlocked && shaper.QuotaOn;
            if (blocked)
            {
                quotaState.Text = (q.DayOver ? "Daily" : "Monthly") + " quota used up: internet is blocked."; quotaState.ForeColor = Theme.Bad;
                bannerText.Text = quotaState.Text;
            }
            else
            {
                quotaState.Text = !shaper.QuotaOn ? "The Quota switch is off: quotas are not enforced." : allowed ? "Quota used up, but internet is allowed for the rest of today." : "Internet is allowed.";
                quotaState.ForeColor = !shaper.QuotaOn || allowed ? Theme.Warn : Theme.Good;
            }
            if (banner.Visible != blocked) banner.Visible = blocked;
            allowToday.Visible = blocked;
        }

        void AllowToday() { shaper.Quota.AllowDay = DateTime.Today; Persist(); }

        void ResetQuota()
        {
            var q = shaper.Quota;
            if (q.DayOver) q.ResetDayUsage();
            if (q.MonthOver) q.ResetMonthUsage();
            Persist();
        }

        void OnQuota(int ev)
        {
            var q = shaper.Quota;
            if (!shaper.QuotaOn) return;
            if (ev == Quota.Warn)
                tray.ShowBalloonTip(8000, "NexLimit: 90% of your data quota is used", "Today: " + Fmt.Size(q.DayUsed) + (q.MonthLimit > 0 ? ", this month: " + Fmt.Size(q.MonthUsed) : ""), ToolTipIcon.Warning);
            else if (ev == Quota.Unblocked)
                tray.ShowBalloonTip(5000, "NexLimit", "Internet is available again.", ToolTipIcon.Info);
            else if (ev == Quota.Blocked)
            {
                RefreshQuota();
                tray.ShowBalloonTip(10000, "NexLimit: internet blocked", "Your " + (q.DayOver ? "daily" : "monthly") + " data quota is used up.", ToolTipIcon.Error);
                using (var d = new QuotaAlert(q.DayOver ? "daily" : "monthly", q.DayOver ? q.DayLimit : q.MonthLimit))
                {
                    var r = d.ShowDialog(Visible ? this : null);
                    if (r == DialogResult.Yes) AllowToday(); else if (r == DialogResult.Retry) ResetQuota();
                }
            }
            RefreshQuota();
        }

        void OnAppQuota(List<App> apps)
        {
            var a = apps[0];
            var what = a.QuotaHitIn && a.QuotaHitOut ? "download and upload" : a.QuotaHitIn ? "download" : "upload";
            tray.ShowBalloonTip(8000, "NexLimit: " + NameOf(a) + " is blocked", NameOf(a) + " used up its " + what + " quota" + (apps.Count > 1 ? " (and " + (apps.Count - 1) + " more apps)" : "") + ".", ToolTipIcon.Warning);
            Changed();
        }

        // ================= Settings =================

        Control BuildSettings()
        {
            var page = new Panel();
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(Theme.Px(20), Theme.Px(16), Theme.Px(20), Theme.Px(12)) };
            var autostart = new Switch("Start NexLimit when I sign in to Windows (in the tray)");
            var closeToTray = new Switch("Closing the window keeps NexLimit running in the tray") { Checked = Store.CloseToTray };
            try { autostart.Checked = Autostart.IsOn; } catch (Exception) { }
            autostart.CheckedChanged += (s, e) => Autostart.Set(autostart.Checked);
            closeToTray.CheckedChanged += (s, e) => { Store.CloseToTray = closeToTray.Checked; Persist(); };
            var data = new LinkLabel { Text = "Open the data folder (settings and usage history)", AutoSize = true, Margin = new Padding(3, Theme.Px(14), 3, 3) };
            data.LinkClicked += (s, e) => { Directory.CreateDirectory(Store.Dir); Process.Start("explorer.exe", Store.Dir); };
            var theme = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(3, Theme.Px(14), 3, 3), Text = "Colours follow Windows: Settings → Personalization → Colors → Choose your app mode (Dark / Light)." };
            var about = new Label
            {
                AutoSize = true, Tag = "muted", Margin = new Padding(3, Theme.Px(14), 3, 3), MaximumSize = new Size(Theme.Px(820), 0),
                Text = "NexLimit " + Program.Version + ". Free per-app bandwidth limiter, firewall and data quota for Windows.\n"
                    + "Uses the WinDivert driver (LGPL v3, by basil00) to see and shape packets. If NexLimit closes or crashes, traffic flows normally."
            };
            f.Controls.AddRange(new Control[] { autostart, closeToTray, data, theme, about });
            page.Controls.Add(f);
            return page;
        }
    }
}
