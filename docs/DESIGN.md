# NexLimit design (2026-10-26)

**Goal.** A free NetLimiter replacement for Abhishek's Windows PC: live per-app speeds, per-app download/upload
limits, app blocking, usage history, and a daily/monthly data quota that blocks the internet and alerts when used up
(with reset). Small and fast. Built from scratch on WinDivert (NetLimiter itself was not decompiled).

**Stack.** C# WinForms on .NET Framework 4.7.2+ (built into Windows 10/11), same as NexGet; built on Linux with Mono
and NSIS. x64, runs as admin.

**Approach chosen.** One WinDivert NETWORK handle diverts every non-loopback, non-LAN packet (filter verified with
WinDivert's own compiler). A SOCKET-layer sniff handle maps local port -> PID; IP Helper tables fill gaps. Per packet:
find app, drop if blocked or quota hit, else pass through the app bucket then the whole-PC bucket, queue or drop.
Rejected: ETW monitoring + WinDivert only for limited apps (two mechanisms), Windows QoS (can't limit downloads).

**Decisions.**
- LAN, link-local, multicast and loopback are never diverted, limited, blocked or counted.
- Quota counts internet bytes both directions; warn at 90%; at 100% block internet, show alert with
  "Allow for today" / "Reset quota" / "Keep blocked". Resetting never touches history.
- Rules are keyed by lowercase exe path; svchost services share one entry; traffic without a process is "System and unknown".
- Fail-open: if NexLimit exits or crashes, WinDivert releases traffic.
- Start with Windows uses a scheduled task (highest privileges, no 3-day limit, runs on battery) to avoid UAC at sign-in.

## v1.1 UI (2026-10-26)
NetLimiter-style layout requested by Abhishek, keeping NexLimit's own name and colours; theme follows Windows app mode.
- Top bar: Blocker On / Limiter On / Quota On master switches, Units, search, traffic control power button.
- Tabs: Activity, Rule List, Application List, Traffic Statistics, Quota, Settings.
- Activity: tree PC -> LocalNetwork / Internet / apps -> connections; LAN speed = adapter counters minus internet
  counted by the engine. Per-connection speed comes from per-local-port byte counters in the shaper.
- Rule Status slots and Info View grid: Blocker / Limit / Quota x In / Out. Per-app quotas use the app's day or the
  PC's monthly period; "reset usage" stores an offset so history is never changed. PC quota stays combined In + Out.
- Kill connections uses SetTcpEntry (IPv4 TCP only). No "Priorities" (would need priority queues; not requested).
