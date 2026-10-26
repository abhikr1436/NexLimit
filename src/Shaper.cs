// Traffic logic with no Windows calls, so it can be tested anywhere: apps, token buckets, quota, usage history.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace NexLimit
{
    static class Clock
    {
        static readonly double Freq = Stopwatch.Frequency;
        public static Func<double> Now = () => Stopwatch.GetTimestamp() / Freq;   // seconds, monotonic
    }

    /// <summary>A packet copied out of the receive buffer because it has to wait in a bucket queue.</summary>
    sealed class Packet
    {
        public byte[] Data; public byte[] Addr; public App App; public bool Up; public int Proto, Port;
    }

    /// <summary>Token bucket with a FIFO queue. Rate 0 = unlimited. Packets queue while tokens are spent and
    /// are dropped when the queue is full, which makes TCP slow down to the limit.</summary>
    sealed class Bucket
    {
        public const int Pass = 0, Queued = 1, Drop = 2;
        long rate;
        public Bucket Next;                     // checked after this one (app bucket -> whole-PC bucket)
        double tokens, last;
        readonly Queue<Packet> q = new Queue<Packet>();
        long queuedBytes;
        volatile int count;

        public long Rate { get { return Interlocked.Read(ref rate); } set { Interlocked.Exchange(ref rate, Math.Max(0, value)); } }
        public bool HasQueue { get { return count > 0; } }
        long Cap { get { var r = Rate; return Math.Max(r / 20, 3000); } }               // ~50 ms burst
        long MaxQueue { get { var r = Rate; return Math.Max(r / 4, 16 * 1024); } }      // ~250 ms of traffic

        void Refill(double now)
        {
            var r = Rate;
            if (last == 0) { last = now; tokens = Cap; return; }
            tokens = Math.Min(Cap, tokens + r * (now - last)); last = now;
        }

        /// <summary>Pass, Queued (copy() is called to keep the packet) or Drop.</summary>
        public int Offer(int len, Func<Packet> copy)
        {
            if (Rate == 0 && count == 0) return Pass;                      // fast path, no lock
            lock (q)
            {
                var r = Rate;
                Refill(Clock.Now());
                if (count == 0 && (r == 0 || tokens > 0)) { if (r > 0) tokens -= len; return Pass; }   // tokens may go into debt for big packets
                if (queuedBytes + len > MaxQueue) return Drop;
                q.Enqueue(copy()); queuedBytes += len; count = q.Count;
                return Queued;
            }
        }

        public int Offer(Packet p) { return Offer(p.Data.Length, () => p); }

        /// <summary>Takes the packets whose turn has come (all of them once the limit is removed or all = true).</summary>
        public void Release(List<Packet> into, bool all = false)
        {
            if (count == 0) return;
            lock (q)
            {
                var r = all ? 0 : Rate;
                Refill(Clock.Now());
                while (q.Count > 0 && (r == 0 || tokens > 0))
                {
                    var p = q.Dequeue(); queuedBytes -= p.Data.Length;
                    if (r > 0) tokens -= p.Data.Length;
                    into.Add(p);
                }
                count = q.Count;
            }
        }
    }

    sealed class App
    {
        public readonly string Key, Path, Name;
        public readonly Bucket Down = new Bucket(), Up = new Bucket();
        public volatile bool BlockIn, BlockOut;   // In = download, Out = upload
        public long QuotaIn, QuotaOut;            // per-app quota in bytes per day (or per month), 0 = off
        public bool QuotaMonthly;
        public volatile bool QuotaHitIn, QuotaHitOut;
        public long QuotaOffIn, QuotaOffOut;      // usage subtracted after "reset usage", valid for period QuotaOffKey
        public DateTime QuotaOffKey;
        public bool Hidden;
        public long BytesDown, BytesUp;           // since NexLimit started (Interlocked)
        public long SavedDown, SavedUp;           // part of the above already written to history
        public long LastSeen;                     // DateTime ticks of the last packet

        public App(string key, string path, string name) { Key = key; Path = path; Name = name; }
        public bool Blocked { get { return BlockIn || BlockOut; } }
        public bool Limited { get { return Down.Rate > 0 || Up.Rate > 0; } }
        public bool HasQuota { get { return QuotaIn > 0 || QuotaOut > 0; } }
        public bool HasRule { get { return Blocked || Limited || HasQuota || Hidden; } }

        /// <summary>Start date of this app's quota period (today, or the PC's monthly period).</summary>
        public DateTime QuotaPeriod(Quota q, DateTime today) { return QuotaMonthly ? Quota.PeriodOf(today, q.ResetDay) : today.Date; }

        /// <summary>Usage (in, out) in the current quota period: saved history + not yet saved bytes - reset offset.</summary>
        public long[] QuotaUsed(Dictionary<string, long[]> period, DateTime periodKey)
        {
            long[] v; period.TryGetValue(Key, out v);
            long i = (v == null ? 0 : v[0]) + Interlocked.Read(ref BytesDown) - SavedDown;
            long o = (v == null ? 0 : v[1]) + Interlocked.Read(ref BytesUp) - SavedUp;
            if (QuotaOffKey == periodKey) { i -= QuotaOffIn; o -= QuotaOffOut; }
            return new[] { Math.Max(0, i), Math.Max(0, o) };
        }
    }

    /// <summary>Per-app accounting and shaping. Engine feeds packets; this decides pass / queue / drop.</summary>
    sealed class Shaper
    {
        public const string SystemKey = "system";
        public readonly Bucket TotalDown = new Bucket(), TotalUp = new Bucket();
        public readonly ConcurrentDictionary<string, App> Apps = new ConcurrentDictionary<string, App>();
        public readonly Quota Quota = new Quota();
        public long BytesDown, BytesUp;
        public volatile bool InternetBlocked;                                  // PC quota reached
        public volatile bool BlockerOn = true, LimiterOn = true, QuotaOn = true; // master switches
        public volatile bool BlockAllIn, BlockAllOut;                          // PC-wide block
        readonly long[] portIn = new long[131072], portOut = new long[131072];  // bytes per (proto, local port)
        readonly ConcurrentDictionary<int, App> byPid = new ConcurrentDictionary<int, App>();
        readonly int[] tcpOwner = new int[65536], udpOwner = new int[65536];
        volatile App[] list = new App[0];
        readonly Func<int, string> pathOfPid;                                 // exe path or null
        public Action<App> NewApp;                                             // e.g. apply saved rules

        public Shaper(Func<int, string> pathOfPid)
        {
            this.pathOfPid = pathOfPid;
            TotalDown.Rate = 0; TotalUp.Rate = 0;
        }

        public App[] AppList { get { return list; } }

        public void SetOwner(int proto, int port, int pid)
        {
            if (proto == 6) tcpOwner[port & 0xFFFF] = pid; else if (proto == 17) udpOwner[port & 0xFFFF] = pid;
        }
        public int Owner(int proto, int port)
        {
            return proto == 6 ? tcpOwner[port & 0xFFFF] : proto == 17 ? udpOwner[port & 0xFFFF] : 0;
        }

        /// <summary>Forget pid -> app so a reused process ID gets looked up again.</summary>
        public void ForgetPids() { byPid.Clear(); }

        public App Get(string key)
        {
            App a; return Apps.TryGetValue(key, out a) ? a : null;
        }

        public App GetOrAdd(string path)
        {
            var key = path == null ? SystemKey : path.ToLowerInvariant();
            App a;
            if (Apps.TryGetValue(key, out a)) return a;
            lock (Apps)
            {
                if (Apps.TryGetValue(key, out a)) return a;
                var file = path == null ? null : path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                var name = file == null ? "System and unknown" : file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
                a = new App(key, path, name);
                a.Down.Next = TotalDown; a.Up.Next = TotalUp;
                if (NewApp != null) NewApp(a);
                Apps[key] = a;
                list = list.Concat(new[] { a }).ToArray();
                return a;
            }
        }

        public App AppOfPid(int pid)
        {
            if (pid <= 4) return GetOrAdd(null);
            App a;
            if (byPid.TryGetValue(pid, out a)) return a;
            a = GetOrAdd(pathOfPid(pid));
            byPid[pid] = a;
            return a;
        }

        /// <summary>Decide what happens to one packet. lan = remote end is on the local network (never touched).
        /// copy() is only called when the packet has to wait.</summary>
        public int Process(int proto, int localPort, bool outbound, bool lan, int len, Func<App, Packet> copy, out App app)
        {
            app = null;
            if (lan) return Bucket.Pass;
            app = AppOfPid(Owner(proto, localPort));
            if (BlockerOn && (outbound ? BlockAllOut || app.BlockOut : BlockAllIn || app.BlockIn)) return Bucket.Drop;
            if (QuotaOn && (InternetBlocked || (outbound ? app.QuotaHitOut : app.QuotaHitIn))) return Bucket.Drop;
            var b = outbound ? app.Up : app.Down;
            if (!LimiterOn || (b.Rate == 0 && !b.HasQueue && b.Next.Rate == 0 && !b.Next.HasQueue)) { Count(app, outbound, len, proto, localPort); return Bucket.Pass; }
            var a = app;
            Packet kept = null;
            Func<Packet> keep = () => kept ?? (kept = copy(a));
            var v = b.Offer(len, keep);
            if (v == Bucket.Pass)
            {
                v = b.Next.Offer(len, keep);
                if (v == Bucket.Pass) Count(app, outbound, len, proto, localPort);
            }
            return v;
        }

        public void Count(App app, bool up, long len, int proto = 0, int port = 0)
        {
            if (up) { Interlocked.Add(ref app.BytesUp, len); Interlocked.Add(ref BytesUp, len); }
            else { Interlocked.Add(ref app.BytesDown, len); Interlocked.Add(ref BytesDown, len); }
            int slot = proto == 6 ? port & 0xFFFF : proto == 17 ? 65536 + (port & 0xFFFF) : -1;
            if (slot >= 0) Interlocked.Add(ref (up ? portOut : portIn)[slot], len);
            app.LastSeen = DateTime.UtcNow.Ticks;
            Quota.Add(len);
        }

        /// <summary>Bytes (in, out) counted for a local port since start; the UI turns deltas into per-connection speed.</summary>
        public long[] PortBytes(int proto, int port)
        {
            int slot = proto == 6 ? port & 0xFFFF : 65536 + (port & 0xFFFF);
            return new[] { Interlocked.Read(ref portIn[slot]), Interlocked.Read(ref portOut[slot]) };
        }

        /// <summary>Once a second: set QuotaHit flags from usage. Returns apps that just used up a quota.</summary>
        public List<App> CheckAppQuotas(History h, DateTime today)
        {
            var hit = new List<App>();
            Dictionary<string, long[]> day = null, month = null;
            foreach (var a in list)
            {
                if (!a.HasQuota) { a.QuotaHitIn = a.QuotaHitOut = false; continue; }
                var key = a.QuotaPeriod(Quota, today);
                var src = a.QuotaMonthly ? (month ?? (month = h.Range(key, today))) : (day ?? (day = h.Range(today, today)));
                var used = a.QuotaUsed(src, key);
                bool hi = a.QuotaIn > 0 && used[0] >= a.QuotaIn, ho = a.QuotaOut > 0 && used[1] >= a.QuotaOut;
                if ((hi && !a.QuotaHitIn) || (ho && !a.QuotaHitOut)) hit.Add(a);
                a.QuotaHitIn = hi; a.QuotaHitOut = ho;
            }
            return hit;
        }

        /// <summary>Move due packets out of every queue; send() gets the ones that may go on the wire now.</summary>
        public void Pump(Action<Packet> send)
        {
            var due = new List<Packet>();
            var all = !LimiterOn;
            foreach (var a in list) { a.Down.Release(due, all); a.Up.Release(due, all); }
            foreach (var p in due)
            {
                var next = p.Up ? TotalUp : TotalDown;
                if (next.Offer(p) == Bucket.Pass) { Count(p.App, p.Up, p.Data.Length, p.Proto, p.Port); send(p); }
            }
            due.Clear();
            TotalDown.Release(due, !LimiterOn); TotalUp.Release(due, !LimiterOn);
            foreach (var p in due) { Count(p.App, p.Up, p.Data.Length, p.Proto, p.Port); send(p); }
        }

        /// <summary>Remote address on the local network, link-local, multicast or loopback? (4 or 16 bytes)</summary>
        public static bool IsLan(byte[] a)
        {
            if (a.Length == 4)
                return a[0] == 10 || a[0] == 127 || (a[0] == 172 && (a[1] & 0xF0) == 16) || (a[0] == 192 && a[1] == 168)
                    || (a[0] == 169 && a[1] == 254) || a[0] >= 224 || (a[0] == 0);
            if (a[0] == 0xFF || (a[0] & 0xFE) == 0xFC || (a[0] == 0xFE && (a[1] & 0xC0) == 0x80)) return true;
            int zeros = 0; while (zeros < 16 && a[zeros] == 0) zeros++;
            if (zeros >= 15 && a[15] <= 1) return true;                                    // :: and ::1
            if (zeros >= 10 && a[10] == 0xFF && a[11] == 0xFF)                              // ::ffff:a.b.c.d
                return IsLan(new[] { a[12], a[13], a[14], a[15] });
            return false;
        }
    }

    /// <summary>Daily / monthly data cap for the whole PC (internet traffic, both directions).</summary>
    sealed class Quota
    {
        public const int None = 0, Warn = 1, Blocked = 2, Unblocked = 3;
        public long DayLimit, MonthLimit;           // bytes, 0 = off
        public int ResetDay = 1;                    // day of month the monthly period starts (1-28)
        public bool WarnAt90 = true;
        public long DayUsed, MonthUsed;             // Interlocked
        public DateTime Day, PeriodStart, AllowDay; // AllowDay = "allow internet for the rest of this day"
        bool warnedDay, warnedMonth, blocked;

        public void Add(long n) { Interlocked.Add(ref DayUsed, n); Interlocked.Add(ref MonthUsed, n); }

        public static DateTime PeriodOf(DateTime d, int resetDay)
        {
            resetDay = Math.Max(1, Math.Min(28, resetDay));
            var s = new DateTime(d.Year, d.Month, resetDay);
            return d.Date >= s ? s : s.AddMonths(-1);
        }
        public DateTime PeriodEnd { get { return PeriodStart.AddMonths(1); } }

        public bool DayOver { get { return DayLimit > 0 && Interlocked.Read(ref DayUsed) >= DayLimit; } }
        public bool MonthOver { get { return MonthLimit > 0 && Interlocked.Read(ref MonthUsed) >= MonthLimit; } }
        public bool IsBlocked { get { return blocked; } }

        public void ResetDayUsage() { Interlocked.Exchange(ref DayUsed, 0); warnedDay = false; }
        public void ResetMonthUsage() { Interlocked.Exchange(ref MonthUsed, 0); warnedMonth = false; }

        /// <summary>Called about once a second. Handles day/period rollover and returns what changed.</summary>
        public int Tick(DateTime now)
        {
            if (now.Date != Day) { Day = now.Date; ResetDayUsage(); }
            var ps = PeriodOf(now, ResetDay);
            if (ps != PeriodStart) { PeriodStart = ps; ResetMonthUsage(); }

            var over = (DayOver || MonthOver) && AllowDay != now.Date;
            if (over != blocked) { blocked = over; return over ? Blocked : Unblocked; }
            if (!WarnAt90 || over) return None;
            if (!warnedDay && DayLimit > 0 && Interlocked.Read(ref DayUsed) >= DayLimit * 9 / 10) { warnedDay = true; return Warn; }
            if (!warnedMonth && MonthLimit > 0 && Interlocked.Read(ref MonthUsed) >= MonthLimit * 9 / 10) { warnedMonth = true; return Warn; }
            return None;
        }
    }

    /// <summary>Bytes per app per day. One tab-separated file per month: date, exe path, down, up.</summary>
    sealed class History
    {
        readonly Dictionary<DateTime, Dictionary<string, long[]>> days = new Dictionary<DateTime, Dictionary<string, long[]>>();
        readonly HashSet<string> dirty = new HashSet<string>();
        readonly string dir;

        public History(string dir) { this.dir = dir; }

        public void Add(DateTime day, string key, long down, long up)
        {
            if (down == 0 && up == 0) return;
            lock (days)
            {
                Dictionary<string, long[]> d;
                if (!days.TryGetValue(day.Date, out d)) days[day.Date] = d = new Dictionary<string, long[]>();
                long[] v;
                if (!d.TryGetValue(key, out v)) d[key] = v = new long[2];
                v[0] += down; v[1] += up;
                dirty.Add(day.ToString("yyyy-MM"));
            }
        }

        /// <summary>Totals per app (down, up) for days from..to inclusive.</summary>
        public Dictionary<string, long[]> Range(DateTime from, DateTime to)
        {
            var r = new Dictionary<string, long[]>();
            lock (days)
                foreach (var kv in days)
                {
                    if (kv.Key < from.Date || kv.Key > to.Date) continue;
                    foreach (var e in kv.Value)
                    {
                        long[] v;
                        if (!r.TryGetValue(e.Key, out v)) r[e.Key] = v = new long[2];
                        v[0] += e.Value[0]; v[1] += e.Value[1];
                    }
                }
            return r;
        }

        /// <summary>Per-day totals (down, up) for one app (key) or all apps (null), days from..to inclusive.</summary>
        public SortedDictionary<DateTime, long[]> Days(DateTime from, DateTime to, string key)
        {
            var r = new SortedDictionary<DateTime, long[]>();
            lock (days)
                foreach (var kv in days)
                {
                    if (kv.Key < from.Date || kv.Key > to.Date) continue;
                    var v = new long[2];
                    foreach (var e in kv.Value) if (key == null || e.Key == key) { v[0] += e.Value[0]; v[1] += e.Value[1]; }
                    if (v[0] + v[1] > 0) r[kv.Key] = v;
                }
            return r;
        }

        /// <summary>Every app key that has any history.</summary>
        public HashSet<string> Keys()
        {
            var r = new HashSet<string>();
            lock (days) foreach (var d in days.Values) foreach (var k in d.Keys) r.Add(k);
            return r;
        }

        public void Load()
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "usage-*.tsv"))
                foreach (var line in File.ReadAllLines(f))
                {
                    var p = line.Split('\t');
                    DateTime d; long down, up;
                    if (p.Length == 4 && DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                        && long.TryParse(p[2], out down) && long.TryParse(p[3], out up))
                        Add(d, p[1], down, up);
                }
            lock (days) dirty.Clear();
        }

        public void Save()
        {
            string[] months;
            lock (days) { months = dirty.ToArray(); dirty.Clear(); }
            Directory.CreateDirectory(dir);
            foreach (var m in months)
            {
                var lines = new List<string>();
                lock (days)
                    foreach (var kv in days.Where(k => k.Key.ToString("yyyy-MM") == m).OrderBy(k => k.Key))
                        foreach (var e in kv.Value)
                            lines.Add(kv.Key.ToString("yyyy-MM-dd") + "\t" + e.Key + "\t" + e.Value[0] + "\t" + e.Value[1]);
                var path = System.IO.Path.Combine(dir, "usage-" + m + ".tsv");
                File.WriteAllLines(path + ".tmp", lines);
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
        }
    }
}
