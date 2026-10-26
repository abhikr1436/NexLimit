// Settings and per-app rules in %ProgramData%\NexLimit\settings.ini (key=value lines; one "rule=" line per app).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NexLimit
{
    sealed class Rule
    {
        public long Down, Up, QuotaIn, QuotaOut, OffIn, OffOut;
        public bool BlockIn, BlockOut, Monthly, Hidden;
        public DateTime OffKey;
    }

    static class Store
    {
        public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexLimit");
        static string File_ { get { return Path.Combine(Dir, "settings.ini"); } }
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly object saving = new object();

        public static readonly Dictionary<string, Rule> Rules = new Dictionary<string, Rule>();
        public static bool Filtering = true, CloseToTray = true;
        public static string Units = "Auto";

        static string D(DateTime d) { return d.ToString("yyyy-MM-dd", Inv); }
        static DateTime ParseD(string v) { DateTime d; DateTime.TryParseExact(v, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d); return d; }
        static long L(string v) { long n; long.TryParse(v, NumberStyles.Integer, Inv, out n); return n; }

        public static void Load(Shaper s)
        {
            var q = s.Quota;
            if (!File.Exists(File_)) { q.Day = DateTime.Today; q.PeriodStart = Quota.PeriodOf(DateTime.Today, 1); return; }
            foreach (var line in File.ReadAllLines(File_))
            {
                var i = line.IndexOf('='); if (i < 1) continue;
                var k = line.Substring(0, i).Trim(); var v = line.Substring(i + 1).Trim();
                long n = L(v);
                switch (k)
                {
                    case "TotalDown": s.TotalDown.Rate = n; break;
                    case "TotalUp": s.TotalUp.Rate = n; break;
                    case "Filtering": Filtering = v != "0"; break;
                    case "CloseToTray": CloseToTray = v != "0"; break;
                    case "Units": Units = v; break;
                    case "BlockerOn": s.BlockerOn = v != "0"; break;
                    case "LimiterOn": s.LimiterOn = v != "0"; break;
                    case "QuotaOn": s.QuotaOn = v != "0"; break;
                    case "BlockAllIn": s.BlockAllIn = v == "1"; break;
                    case "BlockAllOut": s.BlockAllOut = v == "1"; break;
                    case "DayLimit": q.DayLimit = n; break;
                    case "MonthLimit": q.MonthLimit = n; break;
                    case "ResetDay": q.ResetDay = (int)Math.Max(1, Math.Min(28, n)); break;
                    case "WarnAt90": q.WarnAt90 = v != "0"; break;
                    case "DayUsed": q.DayUsed = n; break;
                    case "MonthUsed": q.MonthUsed = n; break;
                    case "Day": q.Day = ParseD(v); break;
                    case "PeriodStart": q.PeriodStart = ParseD(v); break;
                    case "AllowDay": q.AllowDay = ParseD(v); break;
                    case "rule":
                        // path|down|up|blockIn|blockOut|quotaIn|quotaOut|monthly|hidden|offIn|offOut|offKey  (v1: path|down|up|blocked)
                        var p = v.Split('|');
                        if (p.Length < 4) break;
                        var r = new Rule { Down = L(p[1]), Up = L(p[2]), BlockIn = p[3] == "1" };
                        if (p.Length >= 12)
                        {
                            r.BlockOut = p[4] == "1"; r.QuotaIn = L(p[5]); r.QuotaOut = L(p[6]); r.Monthly = p[7] == "1"; r.Hidden = p[8] == "1";
                            r.OffIn = L(p[9]); r.OffOut = L(p[10]); r.OffKey = ParseD(p[11]);
                        }
                        else r.BlockOut = r.BlockIn;
                        Rules[p[0].ToLowerInvariant()] = r;
                        break;
                }
            }
        }

        /// <summary>Copy the saved rule (if any) onto an app the engine just discovered.</summary>
        public static void Apply(App a)
        {
            Rule r;
            lock (Rules) if (!Rules.TryGetValue(a.Key, out r)) return;
            a.Down.Rate = r.Down; a.Up.Rate = r.Up; a.BlockIn = r.BlockIn; a.BlockOut = r.BlockOut;
            a.QuotaIn = r.QuotaIn; a.QuotaOut = r.QuotaOut; a.QuotaMonthly = r.Monthly; a.Hidden = r.Hidden;
            a.QuotaOffIn = r.OffIn; a.QuotaOffOut = r.OffOut; a.QuotaOffKey = r.OffKey;
        }

        /// <summary>Remember an app's rules; drops the entry when nothing is set.</summary>
        public static void Remember(App a)
        {
            lock (Rules)
            {
                if (!a.HasRule) Rules.Remove(a.Key);
                else Rules[a.Key] = new Rule
                {
                    Down = a.Down.Rate, Up = a.Up.Rate, BlockIn = a.BlockIn, BlockOut = a.BlockOut, QuotaIn = a.QuotaIn, QuotaOut = a.QuotaOut,
                    Monthly = a.QuotaMonthly, Hidden = a.Hidden, OffIn = a.QuotaOffIn, OffOut = a.QuotaOffOut, OffKey = a.QuotaOffKey
                };
            }
        }

        public static void Save(Shaper s)
        {
            lock (saving)
            {
                var q = s.Quota;
                Func<bool, string> B = b => b ? "1" : "0";
                var lines = new List<string>
                {
                    "TotalDown=" + s.TotalDown.Rate, "TotalUp=" + s.TotalUp.Rate,
                    "Filtering=" + B(Filtering), "CloseToTray=" + B(CloseToTray), "Units=" + Units,
                    "BlockerOn=" + B(s.BlockerOn), "LimiterOn=" + B(s.LimiterOn), "QuotaOn=" + B(s.QuotaOn),
                    "BlockAllIn=" + B(s.BlockAllIn), "BlockAllOut=" + B(s.BlockAllOut),
                    "DayLimit=" + q.DayLimit, "MonthLimit=" + q.MonthLimit, "ResetDay=" + q.ResetDay, "WarnAt90=" + B(q.WarnAt90),
                    "DayUsed=" + q.DayUsed, "MonthUsed=" + q.MonthUsed, "Day=" + D(q.Day), "PeriodStart=" + D(q.PeriodStart), "AllowDay=" + D(q.AllowDay),
                };
                lock (Rules)
                    lines.AddRange(Rules.Select(kv =>
                    {
                        var r = kv.Value;
                        return "rule=" + string.Join("|", kv.Key, r.Down, r.Up, B(r.BlockIn), B(r.BlockOut), r.QuotaIn, r.QuotaOut,
                            B(r.Monthly), B(r.Hidden), r.OffIn, r.OffOut, D(r.OffKey));
                    }));
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(File_ + ".tmp", lines);
                if (File.Exists(File_)) File.Delete(File_);
                File.Move(File_ + ".tmp", File_);
            }
        }
    }
}
