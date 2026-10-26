// WinDivert glue: packet threads, socket-event listener (port -> process), IP Helper tables, pump and housekeeping.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NexLimit
{
    static unsafe class Native
    {
        const string WD = "WinDivert.dll";
        public const int LayerNetwork = 0, LayerSocket = 3;
        public const ulong FlagSniff = 1, FlagRecvOnly = 4;
        public const int AddrSize = 80;

        [DllImport(WD, SetLastError = true, CharSet = CharSet.Ansi)] public static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);
        [DllImport(WD, SetLastError = true)] public static extern bool WinDivertRecvEx(IntPtr h, byte* pkt, uint len, uint* recvLen, ulong flags, byte* addr, uint* addrLen, IntPtr overlapped);
        [DllImport(WD, SetLastError = true)] public static extern bool WinDivertRecv(IntPtr h, byte* pkt, uint len, uint* recvLen, byte* addr);
        [DllImport(WD, SetLastError = true)] public static extern bool WinDivertSendEx(IntPtr h, byte* pkt, uint len, uint* sendLen, ulong flags, byte* addr, uint addrLen, IntPtr overlapped);
        [DllImport(WD, SetLastError = true)] public static extern bool WinDivertShutdown(IntPtr h, int how);
        [DllImport(WD, SetLastError = true)] public static extern bool WinDivertClose(IntPtr h);
        [DllImport(WD, SetLastError = true)] public static extern bool WinDivertSetParam(IntPtr h, int param, ulong value);
        [DllImport(WD)] public static extern bool WinDivertHelperParsePacket(byte* pkt, uint len, byte** ip, byte** ip6, byte* proto,
            void* icmp, void* icmp6, byte** tcp, byte** udp, void* data, void* dataLen, byte** next, uint* nextLen);
        [DllImport(WD)] public static extern bool WinDivertHelperCompileFilter(string filter, int layer, IntPtr obj, uint objLen, out IntPtr err, out uint errPos);

        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(byte* table, ref int size, bool order, int af, int cls, int reserved);
        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedUdpTable(byte* table, ref int size, bool order, int af, int cls, int reserved);
        [DllImport("iphlpapi.dll")] public static extern uint SetTcpEntry(int* row);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("winmm.dll")] public static extern int timeBeginPeriod(int ms);

        public static string PathOfPid(int pid)
        {
            var h = OpenProcess(0x1000, false, pid);              // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }

    /// <summary>One socket from the Windows connection tables.</summary>
    sealed class Conn
    {
        public int Pid, Proto, LocalPort, RemotePort, State; public string Remote; public bool V6;
        public int[] Raw;                                      // IPv4 TCP row, for Kill
        public string Key { get { return Proto + ":" + LocalPort + ">" + Remote + ":" + RemotePort; } }
    }

    sealed unsafe class Engine
    {
        // Internet traffic only: loopback and LAN / link-local / multicast never leave the kernel.
        public const string Filter = "!loopback and !impostor"
            + " and (remoteAddr < 10.0.0.0 or remoteAddr > 10.255.255.255)"
            + " and (remoteAddr < 172.16.0.0 or remoteAddr > 172.31.255.255)"
            + " and (remoteAddr < 192.168.0.0 or remoteAddr > 192.168.255.255)"
            + " and (remoteAddr < 169.254.0.0 or remoteAddr > 169.254.255.255)"
            + " and (remoteAddr < 224.0.0.0 or remoteAddr > 255.255.255.255)"
            + " and (remoteAddr < fc00:: or remoteAddr > febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff)"
            + " and (remoteAddr < ff00:: or remoteAddr > ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff)";
        const string FallbackFilter = "!loopback and !impostor";
        const int Batch = 64, BufLen = Batch * 1600 + 65535;

        public readonly Shaper Shaper;
        public readonly History History;
        public event Action<int> QuotaEvent;                  // Quota.Warn / Blocked / Unblocked (from a worker thread)
        public event Action<List<App>> AppQuotaHit;           // apps that just used up their own quota
        IntPtr net = IntPtr.Zero, sock = IntPtr.Zero;
        volatile bool running;
        readonly List<Thread> threads = new List<Thread>();
        readonly AutoResetEvent refresh = new AutoResetEvent(true);
        public bool Running { get { return running; } }

        public Engine(Shaper s, History h)
        {
            Shaper = s; History = h;
            new Thread(Housekeeping) { IsBackground = true, Name = "housekeeping" }.Start();
        }

        /// <summary>Opens WinDivert and starts the threads. Returns null on success or a message for the user.</summary>
        public string Start()
        {
            if (running) return null;
            try { net = Native.WinDivertOpen(Filter, Native.LayerNetwork, 0, 0); }
            catch (Exception e) when (e is DllNotFoundException || e is BadImageFormatException || e is EntryPointNotFoundException)
            { return "WinDivert.dll is missing or damaged next to NexLimit.exe. Please reinstall NexLimit."; }
            if (net == new IntPtr(-1)) net = Native.WinDivertOpen(FallbackFilter, Native.LayerNetwork, 0, 0);
            if (net == new IntPtr(-1)) return Explain(Marshal.GetLastWin32Error());
            Native.WinDivertSetParam(net, 0, 16384);            // queue length (packets)
            Native.WinDivertSetParam(net, 2, 32u << 20);         // queue size (bytes)
            sock = Native.WinDivertOpen("true", Native.LayerSocket, 0, Native.FlagSniff | Native.FlagRecvOnly);
            if (sock == new IntPtr(-1)) { var e = Marshal.GetLastWin32Error(); Native.WinDivertClose(net); return Explain(e); }

            running = true;
            Native.timeBeginPeriod(1);
            Spawn(SocketLoop, "sockets");
            Spawn(TableLoop, "tables");
            Spawn(PumpLoop, "pump");
            for (int i = 0; i < Math.Max(2, Math.Min(4, Environment.ProcessorCount)); i++) Spawn(PacketLoop, "packets");
            return null;
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            Native.WinDivertShutdown(net, 3); Native.WinDivertShutdown(sock, 3);
            refresh.Set();
            foreach (var t in threads) t.Join(2000);
            threads.Clear();
            Native.WinDivertClose(net); Native.WinDivertClose(sock);
        }

        void Spawn(ThreadStart f, string name)
        {
            var t = new Thread(f) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
            threads.Add(t); t.Start();
        }

        static string Explain(int err)
        {
            switch (err)
            {
                case 2: return "WinDivert64.sys is missing next to NexLimit.exe. Please reinstall NexLimit.";
                case 5: return "NexLimit needs to run as administrator to control network traffic.";
                case 577: return "Windows refused to load the WinDivert driver (driver signature check). Secure Boot or a security tool may be blocking it.";
                case 654: return "Another program has an incompatible WinDivert version loaded. Close it (or reboot) and try again.";
                case 1060: case 1753: return "The Windows Base Filtering Engine service isn't running. Start it in services.msc and try again.";
                case 1275: return "Windows or an anti-virus blocked the WinDivert driver from loading.";
                default: return "Couldn't start traffic control (" + new Win32Exception(err).Message + ", error " + err + ").";
            }
        }

        /// <summary>Copies a waiting packet out of the receive buffer; one per thread so nothing is allocated per packet.</summary>
        sealed class Copier
        {
            public byte* P, A; public int Len, Proto, Port; public bool Up;
            public readonly Func<App, Packet> Fn;
            public Copier() { Fn = Copy; }
            Packet Copy(App app)
            {
                var p = new Packet { Data = new byte[Len], Addr = new byte[Native.AddrSize], App = app, Up = Up, Proto = Proto, Port = Port };
                Marshal.Copy((IntPtr)P, p.Data, 0, Len); Marshal.Copy((IntPtr)A, p.Addr, 0, Native.AddrSize);
                return p;
            }
        }

        void PacketLoop()
        {
            var buf = (byte*)Marshal.AllocHGlobal(BufLen); var addr = (byte*)Marshal.AllocHGlobal(Native.AddrSize * Batch);
            var sbuf = (byte*)Marshal.AllocHGlobal(BufLen); var saddr = (byte*)Marshal.AllocHGlobal(Native.AddrSize * Batch);
            var verdict = new int[Batch]; var start = new long[Batch]; var length = new uint[Batch];
            var c = new Copier(); var remote = new byte[16]; var remote4 = new byte[4];
            try
            {
                while (running)
                {
                    uint recvLen = 0, addrLen = Native.AddrSize * Batch;
                    if (!Native.WinDivertRecvEx(net, buf, BufLen, &recvLen, 0, addr, &addrLen, IntPtr.Zero))
                    {
                        if (!running || Marshal.GetLastWin32Error() == 232) break;       // ERROR_NO_DATA after shutdown
                        continue;
                    }
                    int n = (int)(addrLen / Native.AddrSize); byte* p = buf; uint left = recvLen; bool allPass = true;
                    int i = 0;
                    for (; i < n && left > 0; i++)
                    {
                        byte* ip = null, ip6 = null, tcp = null, udp = null, next = null; byte proto = 0; uint nextLen = 0;
                        Native.WinDivertHelperParsePacket(p, left, &ip, &ip6, &proto, null, null, &tcp, &udp, null, null, &next, &nextLen);
                        uint plen = next != null ? (uint)(next - p) : left;
                        byte* a = addr + i * Native.AddrSize;
                        bool outb = (a[10] & 2) != 0;
                        bool lan;
                        if (ip != null) { var r = ip + (outb ? 16 : 12); for (int k = 0; k < 4; k++) remote4[k] = r[k]; lan = Shaper.IsLan(remote4); }
                        else if (ip6 != null) { var r = ip6 + (outb ? 24 : 8); for (int k = 0; k < 16; k++) remote[k] = r[k]; lan = Shaper.IsLan(remote); }
                        else lan = true;
                        byte* ports = tcp != null ? tcp : udp;
                        int port = ports == null ? 0 : outb ? (ports[0] << 8 | ports[1]) : (ports[2] << 8 | ports[3]);
                        int pr = tcp != null ? 6 : udp != null ? 17 : proto;
                        if (ports != null && Shaper.Owner(pr, port) == 0) refresh.Set();

                        c.P = p; c.A = a; c.Len = (int)plen; c.Up = outb; c.Proto = pr; c.Port = port;
                        App app;
                        verdict[i] = Shaper.Process(pr, port, outb, lan, (int)plen, c.Fn, out app);
                        start[i] = p - buf; length[i] = plen;
                        if (verdict[i] != Bucket.Pass) allPass = false;
                        p += plen; left -= plen;
                    }
                    n = i;
                    uint sent;
                    if (allPass) { Native.WinDivertSendEx(net, buf, recvLen, &sent, 0, addr, (uint)(n * Native.AddrSize), IntPtr.Zero); continue; }
                    uint slen = 0; int sc = 0;
                    for (i = 0; i < n; i++)
                    {
                        if (verdict[i] != Bucket.Pass) continue;
                        Buffer.MemoryCopy(buf + start[i], sbuf + slen, BufLen - slen, length[i]);
                        Buffer.MemoryCopy(addr + i * Native.AddrSize, saddr + sc * Native.AddrSize, Native.AddrSize, Native.AddrSize);
                        slen += length[i]; sc++;
                    }
                    if (sc > 0) Native.WinDivertSendEx(net, sbuf, slen, &sent, 0, saddr, (uint)(sc * Native.AddrSize), IntPtr.Zero);
                }
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)buf); Marshal.FreeHGlobal((IntPtr)addr);
                Marshal.FreeHGlobal((IntPtr)sbuf); Marshal.FreeHGlobal((IntPtr)saddr);
            }
        }

        void Send(Packet p)
        {
            uint sent;
            fixed (byte* d = p.Data) fixed (byte* a = p.Addr)
                Native.WinDivertSendEx(net, d, (uint)p.Data.Length, &sent, 0, a, Native.AddrSize, IntPtr.Zero);
        }

        void PumpLoop()
        {
            Action<Packet> send = Send;
            while (running) { Shaper.Pump(send); Thread.Sleep(1); }
        }

        /// <summary>bind/connect/listen/accept events tell us which process owns each local port.</summary>
        void SocketLoop()
        {
            var addr = stackalloc byte[Native.AddrSize];
            while (running)
            {
                uint len = 0;
                if (!Native.WinDivertRecv(sock, null, 0, &len, addr))
                {
                    if (!running || Marshal.GetLastWin32Error() == 232) break;
                    continue;
                }
                int ev = addr[9];
                if (ev < 3 || ev > 6) continue;                                        // BIND, CONNECT, LISTEN, ACCEPT
                int pid = *(int*)(addr + 32), port = *(ushort*)(addr + 68), proto = addr[72];
                if (pid > 0) Shaper.SetOwner(proto, port, pid);
            }
        }

        /// <summary>Reads the Windows connection tables at start and whenever a packet's port has no known owner.</summary>
        void TableLoop()
        {
            while (running)
            {
                refresh.WaitOne(5000);
                if (!running) break;
                ReadTables();
                Thread.Sleep(250);                                                      // at most 4 reads a second
            }
        }

        public void ReadTables()
        {
            ReadTable(true, 2, 24, 8, 20); ReadTable(true, 23, 56, 20, 52);           // TCP: IPv4, IPv6
            ReadTable(false, 2, 12, 4, 8); ReadTable(false, 23, 28, 20, 24);          // UDP: IPv4, IPv6
        }

        void ReadTable(bool tcp, int af, int rowSize, int portOff, int pidOff)
        {
            var t = GetTable(tcp, af);
            if (t != null) fixed (byte* p = t) ParseTable(Shaper, p, t.Length, tcp ? 6 : 17, rowSize, portOff, pidOff);
        }

        /// <summary>A copy of a MIB_TCP(6)TABLE_OWNER_PID / MIB_UDP(6)TABLE_OWNER_PID, or null.</summary>
        static byte[] GetTable(bool tcp, int af)
        {
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int size = 0;
                    if (tcp) Native.GetExtendedTcpTable(null, ref size, false, af, 5, 0); else Native.GetExtendedUdpTable(null, ref size, false, af, 1, 0);
                    if (size <= 0) return null;
                    size += 4096;
                    var t = new byte[size];
                    uint rc;
                    fixed (byte* p = t) rc = tcp ? Native.GetExtendedTcpTable(p, ref size, false, af, 5, 0) : Native.GetExtendedUdpTable(p, ref size, false, af, 1, 0);
                    if (rc == 0) return t;
                    if (rc != 122) return null;                                         // grew meanwhile: try again
                }
            }
            catch (Exception) { }                                                       // not on Windows
            return null;
        }

        static readonly string[] TcpStates = { "", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1", "FIN wait 2", "Close wait", "Closing", "Last ACK", "Time wait", "Deleted" };

        /// <summary>All current TCP and UDP endpoints that belong to a process.</summary>
        public static List<Conn> Connections()
        {
            var r = new List<Conn>();
            Action<bool, int> read = (tcp, af) =>
            {
                var t = GetTable(tcp, af);
                if (t == null) return;
                int n = BitConverter.ToInt32(t, 0);
                int size = tcp ? (af == 2 ? 24 : 56) : (af == 2 ? 12 : 28);
                for (int i = 0; i < n && 4 + (i + 1) * size <= t.Length; i++)
                {
                    int o = 4 + i * size;
                    var c = new Conn { Proto = tcp ? 6 : 17, V6 = af == 23 };
                    Func<int, int> port = off => t[o + off] << 8 | t[o + off + 1];
                    Func<int, int, string> ip = (off, len) => { var b = new byte[len]; Array.Copy(t, o + off, b, 0, len); return new System.Net.IPAddress(b).ToString(); };
                    if (tcp && af == 2)
                    {
                        c.State = BitConverter.ToInt32(t, o); c.Raw = new int[5];
                        for (int k = 0; k < 5; k++) c.Raw[k] = BitConverter.ToInt32(t, o + k * 4);
                        c.LocalPort = port(8); c.Remote = ip(12, 4); c.RemotePort = port(16); c.Pid = BitConverter.ToInt32(t, o + 20);
                    }
                    else if (tcp) { c.LocalPort = port(20); c.Remote = ip(24, 16); c.RemotePort = port(44); c.State = BitConverter.ToInt32(t, o + 48); c.Pid = BitConverter.ToInt32(t, o + 52); }
                    else if (af == 2) { c.LocalPort = port(4); c.Pid = BitConverter.ToInt32(t, o + 8); }
                    else { c.LocalPort = port(20); c.Pid = BitConverter.ToInt32(t, o + 24); }
                    if (c.Pid > 0 && c.State != 11) r.Add(c);
                }
            };
            read(true, 2); read(true, 23); read(false, 2); read(false, 23);
            return r;
        }

        public static string StateName(int s) { return s > 0 && s < TcpStates.Length ? TcpStates[s] : ""; }

        /// <summary>Close an IPv4 TCP connection (SetTcpEntry with DELETE_TCB). IPv6 and UDP can't be killed this way.</summary>
        public static bool Kill(Conn c)
        {
            if (c.Raw == null) return false;
            var row = (int[])c.Raw.Clone(); row[0] = 12;
            try { fixed (int* p = row) return Native.SetTcpEntry(p) == 0; } catch (Exception) { return false; }
        }

        /// <summary>MIB_*_OWNER_PID table: DWORD count, then rows; the port is in network order.</summary>
        public static void ParseTable(Shaper s, byte* t, int size, int proto, int rowSize, int portOff, int pidOff)
        {
            int n = *(int*)t;
            for (int i = 0; i < n && 4 + (i + 1) * rowSize <= size; i++)
            {
                byte* row = t + 4 + i * rowSize;
                int pid = *(int*)(row + pidOff);
                if (pid > 0) s.SetOwner(proto, row[portOff] << 8 | row[portOff + 1], pid);
            }
        }

        /// <summary>Once a second: quota rollover and checks. Every 30 s forget pids; every minute save history.</summary>
        void Housekeeping()
        {
            for (long tick = 1; ; tick++)
            {
                Thread.Sleep(1000);
                try
                {
                    var ev = Shaper.Quota.Tick(DateTime.Now);
                    Shaper.InternetBlocked = Shaper.Quota.IsBlocked;
                    if (ev != Quota.None && QuotaEvent != null) QuotaEvent(ev);
                    var hit = Shaper.CheckAppQuotas(History, DateTime.Today);
                    if (hit.Count > 0 && Shaper.QuotaOn && AppQuotaHit != null) AppQuotaHit(hit);
                    if (tick % 30 == 0) Shaper.ForgetPids();
                    if (tick % 60 == 0) Save();
                }
                catch (Exception) { }                                                    // never let housekeeping die
            }
        }

        /// <summary>Move new byte counts into history and write everything to disk.</summary>
        public void Save()
        {
            lock (this)
            {
                var today = DateTime.Today;
                foreach (var a in Shaper.AppList)
                {
                    long d = Interlocked.Read(ref a.BytesDown), u = Interlocked.Read(ref a.BytesUp);
                    History.Add(today, a.Key, d - a.SavedDown, u - a.SavedUp);
                    a.SavedDown = d; a.SavedUp = u;
                }
                try { History.Save(); Store.Save(Shaper); } catch (Exception) { }
            }
        }
    }
}
