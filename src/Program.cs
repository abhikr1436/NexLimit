using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace NexLimit
{
    static class Program
    {
        public const string Version = "1.2.0";

        [STAThread]
        static void Main(string[] args)
        {
            var tray = Array.IndexOf(args, "--tray") >= 0;
            if (Array.IndexOf(args, "--autostart-on") >= 0) { Autostart.Set(true); return; }
            if (Array.IndexOf(args, "--cleanup") >= 0) { Autostart.Set(false); return; }

            bool first;
            using (var mutex = new Mutex(true, "Global\\NexLimit.Running", out first))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\NexLimit.Show"))
            {
                if (!first) { show.Set(); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var shaper = new Shaper(Native.PathOfPid);
                shaper.NewApp = Store.Apply;
                Store.Load(shaper);
                var history = new History(Store.Dir);
                try { history.Load(); } catch (Exception) { }
                var engine = new Engine(shaper, history);

                var form = new MainForm(engine, tray);
                new Thread(() => { while (show.WaitOne()) form.BeginInvoke((Action)form.ShowFromTray); }) { IsBackground = true }.Start();
                Application.Run(form);
                engine.Stop();
                engine.Save();
            }
        }
    }

    /// <summary>Start with Windows through an elevated logon task, so there is no UAC prompt at every sign-in.</summary>
    static class Autostart
    {
        static int Schtasks(string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(10000); return p.ExitCode;
            }
            catch (Exception) { return -1; }
        }

        public static bool IsOn { get { return Schtasks("/Query /TN NexLimit") == 0; } }

        public static void Set(bool on)
        {
            if (!on) { Schtasks("/Delete /F /TN NexLimit"); return; }
            // XML instead of /SC ONLOGON: the defaults would stop NexLimit after 3 days and skip it on battery.
            var user = System.Security.SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>"
                + "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
                + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>"
                + "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(Application.ExecutablePath)
                + "</Command><Arguments>--tray</Arguments></Exec></Actions></Task>";
            var f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexlimit-task.xml");
            System.IO.File.WriteAllText(f, xml, System.Text.Encoding.Unicode);
            Schtasks("/Create /F /TN NexLimit /XML \"" + f + "\"");
            try { System.IO.File.Delete(f); } catch (Exception) { }
        }
    }
}
