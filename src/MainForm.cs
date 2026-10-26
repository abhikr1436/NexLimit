// Main window: top bar with master switches, tabs (Activity, Rule List, Application List, Traffic Statistics,
// Quota, Settings), status bar, tray icon. Pages live in Pages.cs; rule actions in Actions.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NexLimit
{
    sealed partial class MainForm : Form
    {
        readonly Engine engine;
        readonly Shaper shaper;
        bool startHidden, exiting;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem trayPause = new ToolStripMenuItem("Pause traffic control");

        // top bar
        Switch blockerOn, limiterOn, quotaOn;
        readonly ComboBox units = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox search = new TextBox();
        readonly Button power = new Button { AutoSize = true };
        Strip tabs;
        readonly Panel body = new Panel { Dock = DockStyle.Fill };
        readonly List<Control> pages = new List<Control>();
        readonly Panel banner = new Panel { Dock = DockStyle.Top, Visible = false };
        readonly Label bannerText = new Label { AutoSize = true, Font = Theme.UiBold };
        readonly Label statusLeft = new Label { AutoSize = true }, statusRight = new Label { AutoSize = true, Tag = "muted" };

        // live data
        List<Conn> conns = new List<Conn>();
        readonly Dictionary<App, List<Conn>> appConns = new Dictionary<App, List<Conn>>();
        readonly Dictionary<App, long[]> prev = new Dictionary<App, long[]>();
        readonly Dictionary<App, double[]> rate = new Dictionary<App, double[]>();
        readonly Dictionary<int, long[]> prevPort = new Dictionary<int, long[]>();
        readonly Dictionary<int, double[]> portRate = new Dictionary<int, double[]>();
        readonly Dictionary<string, Series> series = new Dictionary<string, Series>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
        readonly ImageList imageList = new ImageList { ColorDepth = ColorDepth.Depth32Bit };   // 16 x 26 px: icon centred, gives list rows room
        Dictionary<string, long[]> today = new Dictionary<string, long[]>();
        double inetDl, inetUl, lanDl, lanUl, pcDl, pcUl, lastTick;
        long lastInetD, lastInetU, lastNicD = -1, lastNicU = -1;
        string engineError;
        int slowTick;
        public static Func<List<Conn>> ConnectionSource = Engine.Connections;   // tests / preview can replace it

        sealed class Series
        {
            public const int N = 360;
            public readonly double[] D = new double[N], U = new double[N]; int head, count;
            public void Push(double d, double u) { D[head] = d; U[head] = u; head = (head + 1) % N; count = Math.Min(N, count + 1); }
            public double[] Ordered(double[] src) { var r = new double[N]; for (int i = 0; i < N; i++) r[i] = i < N - count ? 0 : src[(head + i) % N]; return r; }
        }

        public MainForm(Engine engine, bool tray)
        {
            this.engine = engine; shaper = engine.Shaper; startHidden = tray;
            using (var g = CreateGraphics()) Theme.S = g.DpiX / 96f;
            Text = "NexLimit"; Icon = Theme.AppIcon; Font = Theme.UiFont; AutoScaleMode = AutoScaleMode.None;
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.Px(1300), wa.Width - 40), Math.Min(Theme.Px(800), wa.Height - 40));
            MinimumSize = new Size(Theme.Px(900), Theme.Px(560));
            StartPosition = FormStartPosition.CenterScreen;
            Fmt.Units = Store.Units;
            imageList.ImageSize = new Size(Theme.Px(16), Theme.Px(26));

            foreach (var key in Store.Rules.Keys.ToArray()) shaper.GetOrAdd(key == Shaper.SystemKey ? null : key);

            pages.Add(BuildActivity()); pages.Add(BuildRuleList()); pages.Add(BuildAppList()); pages.Add(BuildStats());
            pages.Add(BuildQuota()); pages.Add(BuildSettings());
            foreach (var p in pages) { p.Dock = DockStyle.Fill; p.Visible = false; body.Controls.Add(p); }
            pages[0].Visible = true;
            Controls.Add(body); Controls.Add(banner); Controls.Add(BuildTabs()); Controls.Add(BuildTop()); Controls.Add(BuildStatus());
            BuildBanner(); BuildTray();
            ApplyTheme();
            Theme.Changed += () => { try { BeginInvoke((Action)ApplyTheme); } catch (Exception) { } };

            engine.QuotaEvent += ev => { try { BeginInvoke((Action)(() => OnQuota(ev))); } catch (Exception) { } };
            engine.AppQuotaHit += apps => { try { BeginInvoke((Action)(() => OnAppQuota(apps))); } catch (Exception) { } };
            timer.Tick += (s, e) => Tick();
            var h = Handle;                                   // so BeginInvoke works while hidden in the tray
            BeginInvoke((Action)(() => { StartEngine(Store.Filtering); Tick(); }));
            timer.Start();
        }

        void ApplyTheme()
        {
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Theme.Apply(this);
            banner.BackColor = Theme.Bad; bannerText.ForeColor = Color.White;
            foreach (var l in new[] { ruleList, appList, statsList }) l.Retheme();
            UpdatePower();
            Theme.TitleBar(this);
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TitleBar(this); }

        // ================= top bar, tabs, status =================

        Control BuildTop()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.Px(52), Tag = "panel" };
            var logo = new PaintedControl { Location = new Point(Theme.Px(12), Theme.Px(10)), Size = new Size(Theme.Px(32), Theme.Px(32)) };
            logo.Paint += (s, e) => { e.Graphics.Clear(Theme.Panel); Theme.DrawLogo(e.Graphics, new RectangleF(0, 0, logo.Width - 1, logo.Height - 1)); };
            var title = new Label { Text = "NexLimit", Font = Theme.Title, AutoSize = true, Location = new Point(Theme.Px(50), Theme.Px(13)), Tag = "accent" };

            var left = new FlowLayoutPanel { Location = new Point(Theme.Px(160), Theme.Px(12)), AutoSize = true, WrapContents = false };
            blockerOn = new Switch("Blocker On") { Checked = shaper.BlockerOn }; limiterOn = new Switch("Limiter On") { Checked = shaper.LimiterOn };
            quotaOn = new Switch("Quota On") { Checked = shaper.QuotaOn };
            blockerOn.CheckedChanged += (s, e) => { shaper.BlockerOn = blockerOn.Checked; Persist(); Tick(); };
            limiterOn.CheckedChanged += (s, e) => { shaper.LimiterOn = limiterOn.Checked; Persist(); Tick(); };
            quotaOn.CheckedChanged += (s, e) => { shaper.QuotaOn = quotaOn.Checked; Persist(); Tick(); };
            foreach (var sw in new[] { blockerOn, limiterOn, quotaOn }) { sw.Margin = new Padding(0, 0, Theme.Px(12), 0); left.Controls.Add(sw); }
            var ul = new Label { Text = "Units", AutoSize = true, Margin = new Padding(Theme.Px(8), Theme.Px(6), Theme.Px(6), 0), Tag = "muted" };
            units.Items.AddRange(new object[] { "Auto", "B", "KB", "MB", "bit" }); units.Width = Theme.Px(70);
            units.SelectedItem = Fmt.Units; if (units.SelectedIndex < 0) units.SelectedIndex = 0;
            units.SelectedIndexChanged += (s, e) => { Fmt.Units = Store.Units = (string)units.SelectedItem; Persist(); Tick(); };
            units.Margin = new Padding(0, Theme.Px(2), 0, 0);
            left.Controls.Add(ul); left.Controls.Add(units);

            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, Theme.Px(11), Theme.Px(12), 0) };
            search.Width = Theme.Px(220); search.Margin = new Padding(0, Theme.Px(3), Theme.Px(10), 0);
            search.TextChanged += (s, e) => { if (tabs.Selected == 0) RefreshActivity(); else if (tabs.Selected == 2) RefreshAppList(); else if (tabs.Selected == 3) RefreshStats(); };
            Cue(search, "Search apps…");
            power.Click += (s, e) => StartEngine(!engine.Running);
            power.Margin = new Padding(0);
            right.Controls.Add(search); right.Controls.Add(power);
            top.Controls.Add(right); top.Controls.Add(logo); top.Controls.Add(title); top.Controls.Add(left);
            return top;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        static void Cue(TextBox t, string text)
        {
            t.HandleCreated += (s, e) => { try { SendMessage(t.Handle, 0x1501, (IntPtr)1, text); } catch (Exception) { } };
        }

        Control BuildTabs()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = Theme.Px(40), Tag = "panel" };
            tabs = new Strip(false, "Activity", "Rule List", "Application List", "Traffic Statistics", "Quota", "Settings");
            tabs.Location = new Point(Theme.Px(6), Theme.Px(2)); tabs.Width = tabs.PreferredWidth;
            tabs.SelectedChanged += (s, e) => ShowPage(tabs.Selected);
            bar.Controls.Add(tabs);
            bar.Paint += (s, e) => { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            return bar;
        }

        void ShowPage(int i)
        {
            if (tabs.Selected != i) { tabs.Selected = i; return; }
            for (int k = 0; k < pages.Count; k++) pages[k].Visible = k == i;
            if (i == 0) RefreshActivity(); else if (i == 1) RefreshRuleList(); else if (i == 2) RefreshAppList(); else if (i == 3) RefreshStats();
        }

        Control BuildStatus()
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(26), Tag = "panel", Padding = new Padding(Theme.Px(10), Theme.Px(5), Theme.Px(10), 0) };
            statusRight.Dock = DockStyle.Right; statusLeft.Dock = DockStyle.Left;
            bar.Controls.Add(statusLeft); bar.Controls.Add(statusRight);
            bar.Paint += (s, e) => { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
            return bar;
        }

        void BuildBanner()
        {
            banner.Height = Theme.Px(40);
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(Theme.Px(12), Theme.Px(6), 0, 0) };
            bannerText.Margin = new Padding(0, Theme.Px(6), Theme.Px(16), 0);
            var allow = new Button { Text = "Allow for today", AutoSize = true }; var reset = new Button { Text = "Reset quota", AutoSize = true };
            allow.Click += (s, e) => AllowToday(); reset.Click += (s, e) => ResetQuota();
            f.Controls.Add(bannerText); f.Controls.Add(allow); f.Controls.Add(reset);
            banner.Controls.Add(f);
            banner.Tag = "bad-banner";
        }

        void StartEngine(bool on)
        {
            if (on)
            {
                var err = engine.Start();
                engineError = err;
                if (err != null)
                {
                    if (Visible) MessageBox.Show(this, err, "NexLimit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else tray.ShowBalloonTip(5000, "NexLimit", err, ToolTipIcon.Warning);
                }
                else Store.Filtering = true;
            }
            else { engine.Stop(); Store.Filtering = false; engineError = null; }
            UpdatePower(); Persist();
        }

        void UpdatePower()
        {
            var on = engine.Running;
            power.Text = on ? "●  Traffic control ON" : "○  Traffic control OFF";
            power.Tag = on ? "accent" : null; Theme.Apply(power);
            trayPause.Checked = !on;
        }

        // ================= tick: collect live data =================

        static long[] NicTotals()
        {
            long d = 0, u = 0;
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var s = n.GetIPStatistics(); d += s.BytesReceived; u += s.BytesSent;
                }
            }
            catch (Exception) { return null; }
            return new[] { d, u };
        }

        Series Ser(string key) { Series s; if (!series.TryGetValue(key, out s)) series[key] = s = new Series(); return s; }

        void Tick()
        {
            var now = Clock.Now();
            double dt = lastTick == 0 ? 1 : Math.Max(0.2, now - lastTick); bool first = lastTick == 0; lastTick = now;

            // connections -> apps
            conns = ConnectionSource();
            appConns.Clear();
            foreach (var c in conns)
            {
                var a = shaper.AppOfPid(c.Pid);
                List<Conn> l; if (!appConns.TryGetValue(a, out l)) appConns[a] = l = new List<Conn>(); l.Add(c);
            }

            // speeds: internet (counted by the engine), whole PC (network adapters), LAN = difference
            long td = Interlocked.Read(ref shaper.BytesDown), tu = Interlocked.Read(ref shaper.BytesUp);
            inetDl = first ? 0 : (td - lastInetD) / dt; inetUl = first ? 0 : (tu - lastInetU) / dt; lastInetD = td; lastInetU = tu;
            var nic = NicTotals();
            if (nic != null && lastNicD >= 0) { pcDl = Math.Max(0, (nic[0] - lastNicD) / dt); pcUl = Math.Max(0, (nic[1] - lastNicU) / dt); }
            else { pcDl = inetDl; pcUl = inetUl; }
            if (nic != null) { lastNicD = nic[0]; lastNicU = nic[1]; }
            if (!engine.Running) { inetDl = pcDl; inetUl = pcUl; }
            pcDl = Math.Max(pcDl, inetDl); pcUl = Math.Max(pcUl, inetUl);
            lanDl = Math.Max(0, pcDl - inetDl); lanUl = Math.Max(0, pcUl - inetUl);
            Ser("pc").Push(pcDl, pcUl); Ser("lan").Push(lanDl, lanUl); Ser("inet").Push(inetDl, inetUl);
            foreach (var a in shaper.AppList)
            {
                long d = Interlocked.Read(ref a.BytesDown), u = Interlocked.Read(ref a.BytesUp);
                long[] p; if (!prev.TryGetValue(a, out p)) { prev[a] = p = new[] { d, u }; rate[a] = new double[2]; }
                rate[a][0] = (d - p[0]) / dt; rate[a][1] = (u - p[1]) / dt; p[0] = d; p[1] = u;
                Ser(a.Key).Push(rate[a][0], rate[a][1]);
            }
            var seenPorts = new HashSet<int>();
            foreach (var c in conns)
            {
                int slot = (c.Proto == 6 ? 0 : 65536) + c.LocalPort;
                if (!seenPorts.Add(slot)) continue;
                var b = shaper.PortBytes(c.Proto, c.LocalPort);
                long[] p; if (!prevPort.TryGetValue(slot, out p)) { prevPort[slot] = p = new[] { b[0], b[1] }; portRate[slot] = new double[2]; }
                portRate[slot][0] = (b[0] - p[0]) / dt; portRate[slot][1] = (b[1] - p[1]) / dt; p[0] = b[0]; p[1] = b[1];
            }
            foreach (var k in prevPort.Keys.Where(k => !seenPorts.Contains(k)).ToList()) { prevPort.Remove(k); portRate.Remove(k); }

            today = engine.History.Range(DateTime.Today, DateTime.Today);
            var tip = "NexLimit  ↓ " + Fmt.Rate(pcDl) + "  ↑ " + Fmt.Rate(pcUl);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            RefreshQuota();
            if (!Visible) return;

            int online = shaper.AppList.Count(IsOnline);
            statusLeft.Text = engine.Running
                ? "●  Traffic control on" + (shaper.BlockerOn ? "" : "  ·  Blocker off") + (shaper.LimiterOn ? "" : "  ·  Limiter off") + (shaper.QuotaOn ? "" : "  ·  Quota off")
                : engineError != null ? "●  Traffic control off: " + engineError : "○  Traffic control paused: traffic flows normally and isn't counted";
            statusLeft.ForeColor = engine.Running ? Theme.Good : engineError != null ? Theme.Bad : Theme.Muted;
            statusRight.Text = online + " apps online   ·   ↓ " + Fmt.Rate(pcDl) + "   ↑ " + Fmt.Rate(pcUl) + "   ·   Internet today " + Fmt.Size(Interlocked.Read(ref shaper.Quota.DayUsed));
            slowTick++;
            if (tabs.Selected == 0) RefreshActivity();
            else if (tabs.Selected == 1 && slowTick % 3 == 0) RefreshRuleList();
            else if (tabs.Selected == 2 && slowTick % 3 == 0) RefreshAppList();
        }

        bool IsOnline(App a) { return appConns.ContainsKey(a) || DateTime.UtcNow.Ticks - a.LastSeen < TimeSpan.TicksPerSecond * 10; }

        long Today(App a, int dir)
        {
            long[] v; today.TryGetValue(a.Key, out v);
            return (v == null ? 0 : v[dir]) + (dir == 0 ? Interlocked.Read(ref a.BytesDown) - a.SavedDown : Interlocked.Read(ref a.BytesUp) - a.SavedUp);
        }

        string NameOf(App a)
        {
            if (a.Path == null) return a.Name;
            string n;
            if (names.TryGetValue(a.Key, out n)) return n;
            n = a.Name;
            try { if (File.Exists(a.Path)) { var d = FileVersionInfo.GetVersionInfo(a.Path).FileDescription; if (!string.IsNullOrWhiteSpace(d)) n = d.Trim(); } } catch (Exception) { }
            if (n.Length > 0 && char.IsLower(n[0])) n = char.ToUpper(n[0]) + n.Substring(1);
            return names[a.Key] = n;
        }

        Image IconOf(App a)
        {
            Image img;
            if (icons.TryGetValue(a.Key, out img)) return img;
            var sz = new Size(Theme.Px(16), Theme.Px(16));
            try { if (a.Path != null && File.Exists(a.Path)) using (var ic = Icon.ExtractAssociatedIcon(a.Path)) img = new Bitmap(ic.ToBitmap(), sz); } catch (Exception) { }
            if (img == null) img = new Bitmap(SystemIcons.Application.ToBitmap(), sz);
            icons[a.Key] = img;
            var tall = new Bitmap(imageList.ImageSize.Width, imageList.ImageSize.Height);
            using (var g = Graphics.FromImage(tall)) g.DrawImage(img, 0, (tall.Height - sz.Height) / 2, sz.Width, sz.Height);
            imageList.Images.Add(a.Key, tall);
            return img;
        }
        int IconIndex(App a) { IconOf(a); return imageList.Images.IndexOfKey(a.Key); }

        /// <summary>The live app for a key, or a stand-in for apps known only from history.</summary>
        App AppOf(string key)
        {
            return shaper.Get(key) ?? new App(key, key == Shaper.SystemKey ? null : key, key == Shaper.SystemKey ? "System and unknown" : Path.GetFileNameWithoutExtension(key));
        }

        void Persist() { try { Store.Save(shaper); } catch (Exception) { } }

        // ================= tray and window =================

        void BuildTray()
        {
            tray.Icon = Theme.AppIcon; tray.Text = "NexLimit"; tray.Visible = true;
            var m = new ContextMenuStrip { Renderer = new MenuRenderer() };
            var open = new ToolStripMenuItem("Open NexLimit", null, (s, e) => ShowFromTray()) { Font = Theme.UiBold };
            trayPause.Click += (s, e) => StartEngine(!engine.Running);
            var exit = new ToolStripMenuItem("Exit", null, (s, e) => { exiting = true; Close(); });
            m.Items.AddRange(new ToolStripItem[] { open, trayPause, new ToolStripSeparator(), exit });
            tray.ContextMenuStrip = m;
            tray.DoubleClick += (s, e) => ShowFromTray();
        }

        public void ShowFromTray()
        {
            Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate(); Tick();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && value) { startHidden = false; if (!IsHandleCreated) CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        bool toldAboutTray;
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && Store.CloseToTray)
            {
                e.Cancel = true; Hide();
                if (!toldAboutTray) { toldAboutTray = true; tray.ShowBalloonTip(3000, "NexLimit is still running", "Limits, blocks and quotas stay active. Right-click the tray icon to exit.", ToolTipIcon.Info); }
                return;
            }
            timer.Stop(); tray.Visible = false;
            base.OnFormClosing(e);
        }
    }
}
