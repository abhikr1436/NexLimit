// Logic checks that run under Mono on Linux (no driver needed): mono test/tests.exe
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NexLimit
{
    static unsafe class Tests
    {
        static double now = 1000;
        static int failed;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); if (!ok) failed++; }

        static Shaper NewShaper()
        {
            Clock.Now = () => now;
            var s = new Shaper(pid => pid == 100 ? @"C:\Apps\Game.exe" : pid == 200 ? @"C:\Apps\Browser.exe" : null);
            s.SetOwner(6, 5000, 100); s.SetOwner(17, 6000, 200);
            return s;
        }

        static Func<App, Packet> Copier(int len, bool up) { return a => new Packet { Data = new byte[len], Addr = new byte[80], App = a, Up = up }; }

        /// <summary>Offer `pps` packets/s of `len` bytes for `secs` seconds; return bytes that reached the wire.</summary>
        static long Simulate(Shaper s, int port, bool up, int len, int pps, double secs)
        {
            long wire = 0; App app;
            double step = 1.0 / pps, end = now + secs;
            while (now < end)
            {
                if (s.Process(6, port, up, false, len, Copier(len, up), out app) == Bucket.Pass) wire += len;
                s.Pump(p => wire += p.Data.Length);
                now += step;
            }
            return wire;
        }

        static int Main()
        {
            // --- shaping ---
            var s = NewShaper();
            App app;
            Check(s.Process(6, 5000, false, false, 1500, Copier(1500, false), out app) == Bucket.Pass && app.Path == @"C:\Apps\Game.exe", "unlimited app passes and is found by port");
            Check(app.BytesDown == 1500 && s.Quota.DayUsed == 1500, "passed bytes are counted for app and quota");
            Check(s.Process(6, 9999, false, false, 100, Copier(100, false), out app) == Bucket.Pass && app.Key == Shaper.SystemKey, "unknown port goes to System");
            Check(s.Process(6, 5000, false, true, 1500, Copier(1500, false), out app) == Bucket.Pass && app == null, "LAN traffic is not touched");

            var game = s.Get(@"c:\apps\game.exe");
            game.Down.Rate = 100 * 1024;                                           // 100 KB/s
            long got = Simulate(s, 5000, false, 1500, 400, 10);                  // offer 600 KB/s for 10 s
            double rate = got / 10.0;
            Check(rate > 90 * 1024 && rate < 112 * 1024, "download limit 100 KB/s holds (got " + (int)(rate / 1024) + " KB/s)");

            game.Up.Rate = 20 * 1024;
            got = Simulate(s, 5000, true, 1000, 200, 10);
            rate = got / 10.0;
            Check(rate > 17 * 1024 && rate < 23 * 1024, "upload limit 20 KB/s holds (got " + (int)(rate / 1024) + " KB/s)");

            game.Down.Rate = 0; game.Up.Rate = 0;
            long flushed = 0; s.Pump(p => flushed += p.Data.Length);
            Check(!game.Down.HasQueue && !game.Up.HasQueue, "removing a limit releases queued packets");

            s.TotalDown.Rate = 50 * 1024;
            got = Simulate(s, 6000, false, 1500, 200, 10);                        // browser, no app limit
            rate = got / 10.0;
            Check(rate > 45 * 1024 && rate < 56 * 1024, "whole-PC limit 50 KB/s holds (got " + (int)(rate / 1024) + " KB/s)");
            s.TotalDown.Rate = 0; s.Pump(p => { });

            game.Down.Rate = 10 * 1024;
            got = Simulate(s, 5000, false, 64000, 50, 10);                        // big (LSO) packets bigger than the burst
            Check(got > 0 && got / 10.0 < 20 * 1024, "big packets still flow under a small limit (" + got / 10240 + " KB/s)");
            game.Down.Rate = 0; s.Pump(p => { });

            game.BlockIn = true;
            Check(s.Process(6, 5000, false, false, 100, Copier(100, false), out app) == Bucket.Drop, "blocked-in app: download dropped");
            Check(s.Process(6, 5000, true, false, 100, Copier(100, true), out app) == Bucket.Pass, "blocked-in app: upload still passes");
            s.BlockerOn = false;
            Check(s.Process(6, 5000, false, false, 100, Copier(100, false), out app) == Bucket.Pass, "Blocker switch off ignores blocks");
            s.BlockerOn = true; game.BlockIn = false;
            s.BlockAllOut = true;
            Check(s.Process(6, 6000, true, false, 100, Copier(100, true), out app) == Bucket.Drop, "PC-wide upload block");
            s.BlockAllOut = false;

            game.Down.Rate = 1024; s.LimiterOn = false;
            got = Simulate(s, 5000, false, 1500, 200, 2);
            Check(got / 2.0 > 250 * 1024, "Limiter switch off ignores limits");
            s.LimiterOn = true; game.Down.Rate = 0; s.Pump(p => { });

            var before = s.PortBytes(6, 5000)[0];
            s.Process(6, 5000, false, false, 777, Copier(777, false), out app);
            Check(s.PortBytes(6, 5000)[0] - before == 777, "bytes are counted per local port (connection speed)");

            // per-app quota
            var hq = new History(Path.Combine(Path.GetTempPath(), "nlq-" + Guid.NewGuid().ToString("N")));
            var browser = s.GetOrAdd(@"C:\Apps\Browser.exe");
            browser.QuotaIn = 10000;
            hq.Add(DateTime.Today, browser.Key, 6000, 0); browser.SavedDown = browser.BytesDown;
            Check(s.CheckAppQuotas(hq, DateTime.Today).Count == 0 && !browser.QuotaHitIn, "app quota not hit at 60%");
            s.Count(browser, false, 4000);
            Check(s.CheckAppQuotas(hq, DateTime.Today).Count == 1 && browser.QuotaHitIn && !browser.QuotaHitOut, "app quota hit counts saved + unsaved bytes");
            Check(s.Process(17, 6000, false, false, 100, Copier(100, false), out app) == Bucket.Drop, "app over quota is blocked (that direction)");
            Check(s.Process(17, 6000, true, false, 100, Copier(100, true), out app) == Bucket.Pass, "other direction still passes");
            var used = browser.QuotaUsed(hq.Range(DateTime.Today, DateTime.Today), DateTime.Today);
            browser.QuotaOffIn = used[0]; browser.QuotaOffOut = used[1]; browser.QuotaOffKey = DateTime.Today;
            Check(s.CheckAppQuotas(hq, DateTime.Today).Count == 0 && !browser.QuotaHitIn, "reset app quota usage unblocks");
            browser.QuotaIn = 0; s.CheckAppQuotas(hq, DateTime.Today);
            s.InternetBlocked = true;
            Check(s.Process(6, 6000, true, false, 100, Copier(100, true), out app) == Bucket.Drop, "quota block drops internet traffic");
            Check(s.Process(6, 6000, true, true, 100, Copier(100, true), out app) == Bucket.Pass, "quota block keeps LAN working");
            s.InternetBlocked = false;

            // --- LAN detection ---
            Check(Shaper.IsLan(new byte[] { 192, 168, 1, 5 }) && Shaper.IsLan(new byte[] { 10, 0, 0, 1 }) && Shaper.IsLan(new byte[] { 172, 20, 0, 1 }), "private IPv4 is LAN");
            Check(!Shaper.IsLan(new byte[] { 8, 8, 8, 8 }) && !Shaper.IsLan(new byte[] { 172, 32, 0, 1 }) && !Shaper.IsLan(new byte[] { 100, 64, 0, 1 }), "public IPv4 (and CGNAT) is internet");
            var v6 = new byte[16]; v6[0] = 0xFE; v6[1] = 0x80; Check(Shaper.IsLan(v6), "fe80:: is LAN");
            v6 = new byte[16]; v6[0] = 0x20; v6[1] = 0x01; v6[2] = 0x48; v6[3] = 0x60; Check(!Shaper.IsLan(v6), "2001:4860:: is internet");
            v6 = new byte[16]; v6[10] = 0xFF; v6[11] = 0xFF; v6[12] = 192; v6[13] = 168; Check(Shaper.IsLan(v6), "::ffff:192.168.x.x is LAN");

            // --- quota ---
            var q = new Quota { DayLimit = 1000, MonthLimit = 5000, ResetDay = 15 };
            var d1 = new DateTime(2026, 10, 26, 10, 0, 0);
            Check(q.Tick(d1) == Quota.None && q.PeriodStart == new DateTime(2026, 10, 15), "period starts on reset day");
            Check(Quota.PeriodOf(new DateTime(2026, 10, 3), 15) == new DateTime(2026, 9, 15), "before reset day -> previous month's period");
            q.Add(900); Check(q.Tick(d1) == Quota.Warn, "warns at 90%");
            Check(q.Tick(d1) == Quota.None, "warns only once");
            q.Add(100); Check(q.Tick(d1) == Quota.Blocked && q.IsBlocked, "blocks at 100% of daily limit");
            q.AllowDay = d1.Date; Check(q.Tick(d1) == Quota.Unblocked && !q.IsBlocked, "allow-for-today unblocks");
            q.AllowDay = default(DateTime); Check(q.Tick(d1) == Quota.Blocked, "blocked again without the override");
            q.ResetDayUsage(); Check(q.Tick(d1) == Quota.Unblocked && q.MonthUsed == 1000, "reset daily usage unblocks, month keeps counting");
            q.Add(500); q.Tick(d1);
            Check(q.Tick(d1.AddDays(1)) == Quota.None && q.DayUsed == 0 && q.MonthUsed == 1500, "new day resets daily usage only");
            q.Add(3600); Check(q.Tick(d1.AddDays(1)) == Quota.Blocked && q.MonthOver, "monthly limit blocks");
            Check(q.Tick(new DateTime(2026, 11, 15, 0, 0, 1)) == Quota.Unblocked && q.MonthUsed == 0, "new period resets monthly usage");

            // --- history + settings files ---
            var dir = Path.Combine(Path.GetTempPath(), "nexlimit-test-" + Guid.NewGuid().ToString("N"));
            var h = new History(dir);
            h.Add(new DateTime(2026, 10, 1), @"c:\a.exe", 100, 10);
            h.Add(new DateTime(2026, 10, 1), @"c:\a.exe", 50, 5);
            h.Add(new DateTime(2026, 9, 30), @"c:\b.exe", 7, 3);
            h.Save();
            var h2 = new History(dir); h2.Load();
            var r = h2.Range(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));
            Check(r.Count == 1 && r[@"c:\a.exe"][0] == 150 && r[@"c:\a.exe"][1] == 15, "history round-trips and sums per app");
            Check(h2.Range(DateTime.MinValue, DateTime.MaxValue).Count == 2 && Directory.GetFiles(dir, "usage-*.tsv").Length == 2, "one history file per month");

            Store.Dir = dir;
            var s1 = NewShaper(); s1.NewApp = Store.Apply;
            var g = s1.GetOrAdd(@"C:\Apps\Game.exe"); g.Down.Rate = 4096; g.BlockOut = true; g.QuotaIn = 99; g.QuotaMonthly = true; g.Hidden = true; Store.Remember(g);
            s1.LimiterOn = false; s1.BlockAllIn = true;
            s1.TotalUp.Rate = 777; s1.Quota.DayLimit = 123; s1.Quota.ResetDay = 9; s1.Quota.DayUsed = 55;
            Store.Save(s1);
            Store.Rules.Clear();
            var s2 = NewShaper(); s2.NewApp = Store.Apply; Store.Load(s2);
            var g2 = s2.GetOrAdd(@"C:\APPS\game.exe");
            Check(g2.Down.Rate == 4096 && g2.BlockOut && !g2.BlockIn && s2.TotalUp.Rate == 777, "rules and whole-PC limit survive restart (path case ignored)");
            Check(g2.QuotaIn == 99 && g2.QuotaMonthly && g2.Hidden && !s2.LimiterOn && s2.BlockAllIn, "app quota, hidden, switches and PC block survive restart");
            File.AppendAllText(Path.Combine(dir, "settings.ini"), "rule=c:\\old.exe|0|0|1\n");
            Store.Rules.Clear(); var s4 = NewShaper(); s4.NewApp = Store.Apply; Store.Load(s4);
            var old = s4.GetOrAdd(@"c:\old.exe");
            Check(old.BlockIn && old.BlockOut, "v1 rule (blocked) still loads as block in+out");
            Check(s2.Quota.DayLimit == 123 && s2.Quota.ResetDay == 9 && s2.Quota.DayUsed == 55, "quota settings and usage survive restart");
            Directory.Delete(dir, true);

            // --- IP Helper table parsing ---
            var t = new byte[4 + 2 * 24];
            BitConverter.GetBytes(2).CopyTo(t, 0);
            t[4 + 8] = 0x1F; t[4 + 9] = 0x90; BitConverter.GetBytes(4321).CopyTo(t, 4 + 20);      // port 8080, pid 4321
            t[28 + 8] = 0x00; t[28 + 9] = 0x50; BitConverter.GetBytes(0).CopyTo(t, 28 + 20);       // port 80, pid 0 (ignored)
            var s3 = NewShaper();
            fixed (byte* p = t) Engine.ParseTable(s3, p, t.Length, 6, 24, 8, 20);
            Check(s3.Owner(6, 8080) == 4321 && s3.Owner(6, 80) == 0, "TCP table rows map port -> pid (network byte order)");

            Console.WriteLine(failed == 0 ? "\nAll checks passed." : "\n" + failed + " check(s) FAILED.");
            return failed == 0 ? 0 : 1;
        }
    }
}
