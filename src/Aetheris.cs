// Aetheris — lightweight Minecraft server panel.
// Single exe, .NET Framework 4.x (built into Windows), tray-only backend + local web UI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Aetheris")]
[assembly: AssemblyProduct("Aetheris")]
[assembly: AssemblyDescription("Minecraft server panel")]
[assembly: AssemblyVersion("1.6.1.0")]
[assembly: AssemblyFileVersion("1.6.1.0")]

namespace Aetheris
{
    static class Program
    {
        public const string Version = "1.6.1";

        // Aetheris.exe.config: background errors don't terminate the process; lighter garbage collector.
        static void WriteRuntimeConfig()
        {
            try
            {
                var path = Application.ExecutablePath + ".config";
                const string cfg = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.5\"/></startup>\r\n  <runtime><gcConcurrent enabled=\"false\"/><gcServer enabled=\"false\"/><legacyUnhandledExceptionPolicy enabled=\"1\"/></runtime>\r\n</configuration>\r\n";
                if (!File.Exists(path) || !File.ReadAllText(path).Contains("legacyUnhandledExceptionPolicy")) File.WriteAllText(path, cfg);
            }
            catch { }
        }

        static string Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char ch in s) { h ^= ch; h *= 16777619; }
            return h.ToString("x8");
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool trayOnly = args.Any(a => a == "--tray");
            Instances.AutoReplace = args.Any(a => a == "--replace");   // replace an older running copy without asking
            bool created;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            // never let an unexpected error close Aetheris: log it and keep running
            AppDomain.CurrentDomain.UnhandledException += (s, e) => App.CrashLog("unhandled", e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject)));
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => App.CrashLog("ui", e.Exception);
            // enough threads that a busy server start on a slow PC can't starve the panel's web server
            int wt, io; ThreadPool.GetMinThreads(out wt, out io);
            ThreadPool.SetMinThreads(Math.Max(wt, 24), Math.Max(io, 24));
            WriteRuntimeConfig();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // one instance per install folder (so separate servers can each have their own panel)
            var mutex = new Mutex(true, "Local\\AetherisPanel_" + Fnv(AppDomain.CurrentDomain.BaseDirectory.ToLowerInvariant()), out created);
            App.Cfg = Config.Load();
            bool startServerAfter = false;
            if (!created || args.Length > 0) App.Trace("start args=[" + string.Join(" ", args) + "] firstInstance=" + created + " port=" + App.Cfg.Port);

            if (!created)
            {
                // Aetheris from this folder is already running. If it's an older version, offer to replace it.
                var other = Instances.Probe(App.Cfg.Port);
                App.Trace("probe: " + (other == null ? "no answer" : other.Version + " state=" + other.State));
                if (other == null || !Instances.IsOlder(other.Version)) { App.Trace("opening the running copy instead"); App.OpenUi(); return; }
                if (!Instances.AskTakeover(other, true)) { App.OpenUi(); return; }
                if (!Instances.Takeover(other, out startServerAfter)) { App.Trace("takeover failed"); return; }
                bool got;
                try { got = mutex.WaitOne(20000); } catch (AbandonedMutexException) { got = true; }
                App.Trace("takeover done, mutex=" + got + ", restart server=" + startServerAfter);
            }

            int port = App.Cfg.Port;
            for (int attempt = 0; ; attempt++)
            {
                try { Http.Start(port); break; }
                catch (SocketException ex)
                {
                    App.Trace("port " + port + " busy: " + ex.Message);
                    var other = Instances.Probe(port);
                    if (other != null && attempt == 0)
                    {
                        // another Aetheris (other folder or older version) owns the panel port
                        var r = Instances.AskPortConflict(other);
                        if (r == DialogResult.Cancel) { Instances.Open(port); return; }
                        if (r == DialogResult.Yes)
                        {
                            bool srv;
                            if (!Instances.Takeover(other, out srv)) return;
                            if (srv && Instances.SameDir(other)) startServerAfter = true;
                            continue;
                        }
                    }
                    if (attempt > 25)
                    {
                        MessageBox.Show("Aetheris couldn't find a free panel port near " + App.Cfg.Port + ".", "Aetheris", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    port++;   // something else uses this port: try the next one
                }
            }
            if (port != App.Cfg.Port) { App.Cfg.Port = port; try { App.Cfg.Save(); } catch { } }
            if (startServerAfter) App.Cfg.AutoStartOnce = true;
            if (!created || args.Length > 0) App.Trace("running on port " + port);
            Application.Run(new Tray(!trayOnly && App.Cfg.OpenOnLaunch));
            GC.KeepAlive(mutex);
        }
    }

    // Finds / replaces other running copies of Aetheris.
    class OtherInstance { public int Port; public string Version, Exe, Dir, State; public bool CanShutdown; }

    static class Instances
    {
        public static bool AutoReplace;
        static string Get(int port, string path, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Timeout = 4000; req.ReadWriteTimeout = 4000; req.Proxy = null;
            req.Headers["X-Aetheris"] = "1";
            if (body != null)
            {
                req.Method = "POST"; req.ContentType = "application/json";
                var b = Encoding.UTF8.GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try { using (var r = (HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd(); }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) throw;
                using (r) using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd();
            }
        }

        static Dictionary<string, object> J(string s) { try { return Jsn.S.DeserializeObject(s) as Dictionary<string, object>; } catch { return null; } }
        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }

        public static OtherInstance Probe(int port)
        {
            try
            {
                var v = J(Get(port, "/api/version", null));
                if (v != null && v.ContainsKey("version"))
                    return new OtherInstance { Port = port, Version = Str(v, "version"), Exe = Str(v, "exe"), Dir = Str(v, "dir"), State = Str(v, "state"), CanShutdown = true };
                var st = J(Get(port, "/api/state", null));   // Aetheris 1.0 – 1.2 have no /api/version
                if (st != null && st.ContainsKey("state"))
                {
                    int pid = Net2.ListenerPid(port);
                    return new OtherInstance { Port = port, Version = "1.2 or older", Exe = pid > 0 ? Net2.ProcessPath(pid) : "", Dir = Str(st, "dir"), State = Str(st, "state") };
                }
            }
            catch { }
            return null;
        }

        public static bool IsOlder(string v)
        {
            Version a, b;
            if (!System.Version.TryParse(v ?? "", out a)) return true;
            System.Version.TryParse(Program.Version, out b);
            return a < b;
        }

        public static bool SameDir(OtherInstance o)
        {
            return string.Equals((o.Dir ?? "").TrimEnd('\\'), (App.Cfg.ServerDir ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        static string Describe(OtherInstance o)
        {
            return "Aetheris " + o.Version + (o.Exe != "" ? "\n" + o.Exe : "") + "\nServer: " + (o.State == "" ? "unknown" : o.State) + (o.Dir != "" ? " (" + o.Dir + ")" : "");
        }

        public static bool AskTakeover(OtherInstance o, bool sameFolder)
        {
            if (AutoReplace) return true;
            return MessageBox.Show("An older copy of Aetheris is already running:\n\n" + Describe(o) +
                "\n\nClose it and switch to Aetheris " + Program.Version + "?" +
                (o.State != "offline" && o.State != "" ? "\n\nIts Minecraft server is saved and stopped safely first, then started again in the new version." : ""),
                "Aetheris " + Program.Version, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public static DialogResult AskPortConflict(OtherInstance o)
        {
            if (AutoReplace) return DialogResult.Yes;
            return MessageBox.Show("Another Aetheris is already using panel port " + o.Port + ":\n\n" + Describe(o) +
                "\n\nYes: close that copy (its server is saved and stopped safely) and use this one.\nNo: keep both, and run this one on another port.\nCancel: just open the running copy.",
                "Aetheris " + Program.Version, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        }

        public static void Open(int port) { try { using (Process.Start("http://127.0.0.1:" + port + "/")) { } } catch { } }

        // Stops the other copy's server safely and closes it. Shows a small progress window meanwhile.
        public static bool Takeover(OtherInstance o, out bool serverWasRunning)
        {
            bool running = o.State != "offline" && o.State != "";
            serverWasRunning = running;
            string error = null;
            using (var f = new Form { Text = "Aetheris", FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(420, 130), TopMost = true, ShowInTaskbar = true })
            {
                var lbl = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10f), Text = running ? "Saving and stopping the Minecraft server in the old Aetheris…" : "Closing the old Aetheris…" };
                f.Controls.Add(lbl);
                f.Shown += (s, e) => App.Bg(_ =>
                {
                    try { error = DoTakeover(o, running); } catch (Exception ex) { error = ex.Message; }
                    try { f.BeginInvoke(new Action(f.Close)); } catch { }
                });
                f.ShowDialog();
            }
            if (error != null) App.Trace("takeover error: " + error);
            if (error != null && Net2.ListenerPid(o.Port) == 0) return true;   // it closed anyway
            if (error != null && !AutoReplace) MessageBox.Show("Couldn't close the other Aetheris:\n" + error + "\n\nExit it from its tray icon and try again.", "Aetheris", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return error == null;
        }

        static string DoTakeover(OtherInstance o, bool running)
        {
            int pid = Net2.ListenerPid(o.Port);
            App.Trace("takeover: old pid " + pid + ", asking it to " + (o.CanShutdown ? "shut down" : "stop its server"));
            if (o.CanShutdown)
            {
                try { Get(o.Port, "/api/shutdown", "{}"); }
                catch (Exception ex) { App.Trace("takeover: shutdown reply cut off (" + ex.Message + "), old copy is closing"); }
            }
            else
            {
                if (running)
                {
                    Get(o.Port, "/api/power", "{\"action\":\"stop\"}");
                    var until = DateTime.Now.AddSeconds(120);
                    while (DateTime.Now < until)
                    {
                        Thread.Sleep(1000);
                        var st = J(Get(o.Port, "/api/state", null));
                        if (st != null && Str(st, "state") == "offline") break;
                    }
                }
                if (pid > 0)
                    try { using (var p = Process.GetProcessById(pid)) if (p.ProcessName.StartsWith("Aetheris", StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
            }
            var end = DateTime.Now.AddSeconds(130);
            while (Net2.ListenerPid(o.Port) != 0)
            {
                if (DateTime.Now > end) { App.Trace("takeover: port still held by pid " + Net2.ListenerPid(o.Port)); return "it didn't close in time."; }
                Thread.Sleep(500);
            }
            App.Trace("takeover: old copy closed");
            Thread.Sleep(500);
            return null;
        }
    }

    // Low-level Windows helpers: who listens on a port, process paths, Ctrl+C, job objects.
    static class Net2
    {
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr tbl, ref int size, bool order, int af, int cls, uint reserved);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int len);

        // PID of the process listening on a TCP port (IPv4 or IPv6), or 0
        public static int ListenerPid(int port)
        {
            foreach (int af in new[] { 2, 23 })
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 3, 0);
                if (size <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, af, 3, 0) != 0) continue;
                    int n = Marshal.ReadInt32(buf), rowSize = af == 2 ? 24 : 56, portOff = af == 2 ? 8 : 20, pidOff = af == 2 ? 20 : 52;
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr row = IntPtr.Add(buf, 4 + i * rowSize);
                        int lp = Marshal.ReadInt32(row, portOff);
                        if ((((lp & 0xFF) << 8) | ((lp >> 8) & 0xFF)) == port) return Marshal.ReadInt32(row, pidOff);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return 0;
        }

        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return "";
            try { var sb = new StringBuilder(1024); int len = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : ""; }
            finally { CloseHandle(h); }
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
        [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
        static readonly object ConsoleLock = new object();

        // Sends Ctrl+C to a console program: Minecraft saves the world and shuts down, like pressing Ctrl+C in its window.
        public static bool SendCtrlC(int pid)
        {
            lock (ConsoleLock)
            {
                FreeConsole();
                if (!AttachConsole(pid)) return false;
                SetConsoleCtrlHandler(IntPtr.Zero, true);   // don't stop ourselves
                bool ok = GenerateConsoleCtrlEvent(0, 0);
                Thread.Sleep(300);
                FreeConsole();
                App.Bg(_ => { Thread.Sleep(3000); lock (ConsoleLock) SetConsoleCtrlHandler(IntPtr.Zero, false); });
                return ok;
            }
        }

        public static void AllowCtrlCInChildren() { lock (ConsoleLock) SetConsoleCtrlHandler(IntPtr.Zero, false); }

        public static void KillTree(int pid)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
                    p.WaitForExit(10000);
            }
            catch { }
            try { using (var p = Process.GetProcessById(pid)) p.Kill(); } catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attr, string name);
        [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
        [DllImport("kernel32.dll")] public static extern bool TerminateJobObject(IntPtr job, uint code);
        public static void Close(IntPtr h) { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    class Fail : Exception { public Fail(string m) : base(m) { } }

    static class Jsn
    {
        public static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        public static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        public static long Ms(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    }

    // ------------------------------------------------------------------ config
    public class Config
    {
        public string ServerDir { get; set; }
        public string Jar { get; set; }
        public string JavaPath { get; set; }
        public int MinRam { get; set; }
        public int MaxRam { get; set; }
        public string JvmArgs { get; set; }
        public bool AikarFlags { get; set; }
        public bool AutoRestart { get; set; }
        public bool AutoStart { get; set; }
        public string PlayitPath { get; set; }
        public bool StartPlayit { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public bool AppWindow { get; set; }
        public bool OpenOnLaunch { get; set; }
        public int BackupHours { get; set; }
        public int BackupKeep { get; set; }
        public string DailyRestart { get; set; }
        public bool Watchdog { get; set; }
        public bool Notify { get; set; }
        public List<TaskDef> Tasks { get; set; }
        public int Cv { get; set; }                 // config format version
        public string CurseForgeKey { get; set; }
        public bool AutoUpdate { get; set; }
        public long LastUpdateCheck { get; set; }
        [ScriptIgnore] public bool AutoStartOnce;

        public Config()
        {
            ServerDir = ""; Jar = ""; JavaPath = "java"; MinRam = 1024; MaxRam = 2048; JvmArgs = "";
            AikarFlags = false; AutoRestart = false; PlayitPath = ""; Address = ""; Port = 8165;
            AppWindow = true; OpenOnLaunch = true; BackupKeep = 10; DailyRestart = "";
            Watchdog = false; Notify = true; Tasks = new List<TaskDef>(); CurseForgeKey = ""; AutoUpdate = true;
        }

        static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aetheris.json"); } }

        public static Config Load()
        {
            Config c = null;
            try { if (File.Exists(FilePath)) c = Jsn.S.Deserialize<Config>(File.ReadAllText(FilePath)); } catch { }
            if (c != null && c.Cv < 2)
            {
                // older versions turned these on without asking; now they're opt-in
                c.AikarFlags = false; c.Watchdog = false; c.AutoRestart = false; c.AutoUpdate = true;
                c.Cv = 2;
                try { c.Normalize(); c.Save(); } catch { }
            }
            if (c == null)
            {
                c = new Config { Cv = 2 };
                var here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                var jar = FindJar(here);
                if (jar != "") { c.ServerDir = here; c.Jar = jar; }
            }
            c.Normalize();
            return c;
        }

        public void Save() { File.WriteAllText(FilePath, Jsn.S.Serialize(this)); }

        public void Normalize()
        {
            ServerDir = (ServerDir ?? "").Trim().TrimEnd('\\'); Jar = (Jar ?? "").Trim();
            JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? "java" : JavaPath.Trim().Trim('"');
            CurseForgeKey = (CurseForgeKey ?? "").Trim();
            JvmArgs = (JvmArgs ?? "").Trim(); PlayitPath = (PlayitPath ?? "").Trim().Trim('"');
            Address = (Address ?? "").Trim(); DailyRestart = (DailyRestart ?? "").Trim();
            if (MinRam < 256) MinRam = 256;
            if (MaxRam < MinRam) MaxRam = MinRam;
            if (Port < 1024 || Port > 65535) Port = 8165;
            if (BackupKeep < 1) BackupKeep = 1;
            if (BackupHours < 0) BackupHours = 0;
            if (BackupHours > 168) BackupHours = 168;
            if (DailyRestart != "" && !Regex.IsMatch(DailyRestart, @"^([01]\d|2[0-3]):[0-5]\d$")) DailyRestart = "";
            if (Tasks == null) Tasks = new List<TaskDef>();
            Tasks.RemoveAll(t => t == null);
            foreach (var t in Tasks) t.Normalize();
        }

        public static bool JarExists(string dir, string jar)
        {
            if (string.IsNullOrEmpty(jar) || string.IsNullOrEmpty(dir)) return false;
            try { return File.Exists(Path.Combine(dir, jar.StartsWith("@") ? jar.Substring(1).Replace('/', '\\') : jar)); } catch { return false; }
        }

        public static string FindJar(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
                var jars = Directory.GetFiles(dir, "*.jar").Select(Path.GetFileName).ToList();
                foreach (var k in new[] { "server", "paper", "purpur", "fabric", "spigot", "forge", "minecraft" })
                {
                    var m = jars.FirstOrDefault(j => j.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (m != null) return m;
                }
                return jars.FirstOrDefault() ?? "";
            }
            catch { return ""; }
        }
    }

    public class TaskDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Command { get; set; }
        public int Every { get; set; }
        public string At { get; set; }
        public bool Enabled { get; set; }
        public TaskDef() { Id = Guid.NewGuid().ToString("N").Substring(0, 8); Name = ""; Command = ""; At = ""; Enabled = true; }
        public void Normalize()
        {
            Id = Regex.IsMatch(Id ?? "", @"^[a-z0-9]{1,16}$") ? Id : Guid.NewGuid().ToString("N").Substring(0, 8);
            Name = (Name ?? "").Trim(); Command = (Command ?? "").Trim(); At = (At ?? "").Trim();
            if (Name.Length > 60) Name = Name.Substring(0, 60);
            if (Command.Length > 2000) Command = Command.Substring(0, 2000);
            if (Every < 0) Every = 0; if (Every > 10080) Every = 10080;
            if (At != "" && !Regex.IsMatch(At, @"^([01]\d|2[0-3]):[0-5]\d$")) At = "";
        }
    }

    // ------------------------------------------------------------------ app / tray
    static class App
    {
        public static Config Cfg;
        public static Control Ui;
        static System.Threading.Timer trimTimer, tickTimer;
        static DateTime lastDailyRestart = DateTime.MinValue;

        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [StructLayout(LayoutKind.Sequential)]
        class MemStatus { public uint Length = 64; public uint Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MemStatus m);

        public static bool Configured { get { return Cfg.ServerDir != "" && Directory.Exists(Cfg.ServerDir); } }

        public static void Init()
        {
            trimTimer = new System.Threading.Timer(_ => Trim(), null, 5000, Timeout.Infinite);
            tickTimer = new System.Threading.Timer(Tick, null, 60000, 60000);
            Updater.CleanupOld();
            if (Cfg.AutoStart || Cfg.AutoStartOnce) App.Bg(_ => { var e = Server.Start(); if (e != null) Server.Log(e); });
        }

        // Called on every panel request; once the panel goes quiet, trim our RAM back to ~2 MB.
        public static DateTime LastTouch;

        // Runs work in the background; an error is logged instead of crashing Aetheris.
        public static void Bg(Action<object> a)
        {
            ThreadPool.QueueUserWorkItem(o => { try { a(o); } catch (Exception ex) { CrashLog("background task", ex); } });
        }

        public static void Trace(string msg)
        {
            try
            {
                var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aetheris-update.log");
                if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Program.Version + " pid " + Process.GetCurrentProcess().Id + "] " + msg + "\r\n");
            }
            catch { }
        }

        public static void CrashLog(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aetheris-crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + Program.Version + "] " + where + ": " + ex + "\r\n\r\n");
            }
            catch { }
        }
        public static void Touch() { LastTouch = DateTime.Now; if (trimTimer != null) trimTimer.Change(10000, Timeout.Infinite); }

        public static void Trim()
        {
            try
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        static void Tick(object _)
        {
            bool did = false;
            try
            {
                var c = Cfg;
                if (c.BackupHours > 0 && Server.State == "online" && (DateTime.Now - Backups.LastAuto()).TotalHours >= c.BackupHours)
                {
                    Backups.Create("", true); did = true;
                }
                if (c.DailyRestart != "" && Server.Running && DateTime.Now.ToString("HH:mm") == c.DailyRestart && lastDailyRestart.Date != DateTime.Today)
                {
                    lastDailyRestart = DateTime.Now;
                    Server.Send("say Scheduled daily restart — back in a moment!", false);
                    Server.Stop(true); did = true;
                }
            }
            catch (Exception ex) { Server.Log("Scheduler error: " + ex.Message); }
            try { if (Watch()) did = true; } catch { }
            try { if (RunTasks()) did = true; } catch (Exception ex) { Server.Log("Schedule error: " + ex.Message); }
            try { Updater.Tick(); } catch (Exception ex) { App.CrashLog("updater", ex); }
            if (did) Touch();
        }

        static readonly Dictionary<string, DateTime> lastRun = new Dictionary<string, DateTime>();
        static bool RunTasks()
        {
            if (Server.State != "online") return false;
            bool did = false;
            var now = DateTime.Now;
            foreach (var t in Cfg.Tasks.ToArray())
            {
                if (!t.Enabled || t.Command == "") continue;
                DateTime last;
                if (!lastRun.TryGetValue(t.Id, out last)) last = DateTime.MinValue;
                bool due = false;
                if (t.Every > 0)
                {
                    var from = last > Server.Started ? last : Server.Started;
                    due = (now - from).TotalMinutes >= t.Every - 0.05;
                }
                if (t.At != "" && now.ToString("HH:mm") == t.At && last.Date != now.Date) due = true;
                if (!due) continue;
                lastRun[t.Id] = now;
                RunTask(t);
                did = true;
            }
            return did;
        }

        public static void RunTask(TaskDef t)
        {
            Server.Log("Scheduled task: " + (t.Name != "" ? t.Name : t.Command.Split('\n')[0]));
            foreach (var line in t.Command.Split('\n'))
            {
                var c = line.Trim().TrimStart('/');
                if (c != "") Server.Send(c, true);
            }
        }

        static int pingFails;
        // Keeps the server reachable: restarts a dead playit tunnel and a server that stopped answering connections.
        static bool Watch()
        {
            var c = Cfg;
            bool did = false;
            if (Server.State != "online") { pingFails = 0; return false; }
            if (c.StartPlayit) did |= Server.EnsurePlayit();
            if (!c.Watchdog || (DateTime.Now - Server.Started).TotalMinutes < 3 || Props.Get("enable-status", "true") == "false") { pingFails = 0; return did; }
            int port; if (!int.TryParse(Props.Get("server-port", "25565"), out port)) port = 25565;
            var ip = Props.Get("server-ip", "");
            if (Server.Ping(ip == "" ? "127.0.0.1" : ip, port, 10000)) { pingFails = 0; return did; }
            pingFails++;
            if (pingFails < 3) { Server.Log("Watchdog: the server didn't answer a connection check (" + pingFails + "/3)."); return true; }
            pingFails = 0;
            Server.ForceRestart("Watchdog: the server stopped accepting connections for 3 minutes — restarting it.");
            return true;
        }

        public static long SysFreeMb()
        {
            try { var m = new MemStatus(); if (GlobalMemoryStatusEx(m)) return (long)(m.Avail / 1048576UL); } catch { }
            return 0;
        }

        public static long SysRamMb()
        {
            try { var m = new MemStatus(); if (GlobalMemoryStatusEx(m)) return (long)(m.Total / 1048576UL); } catch { }
            return 4096;
        }

        public static void OpenUi()
        {
            string url = "http://127.0.0.1:" + Cfg.Port + "/";
            if (Cfg.AppWindow)
            {
                foreach (var edge in new[] {
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)") + @"\Microsoft\Edge\Application\msedge.exe",
                    Environment.GetEnvironmentVariable("ProgramW6432") + @"\Microsoft\Edge\Application\msedge.exe",
                    Environment.GetEnvironmentVariable("ProgramFiles") + @"\Microsoft\Edge\Application\msedge.exe" })
                {
                    try
                    {
                        if (File.Exists(edge))
                        {
                            using (Process.Start(edge, "--app=" + url + " --window-size=1320,860")) { }
                            return;
                        }
                    }
                    catch { }
                }
            }
            try { using (Process.Start(url)) { } } catch { }
        }

        public static T OnUi<T>(Func<T> f) { return (T)Ui.Invoke(f); }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool GetAutostart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Aetheris") != null; } catch { return false; }
        }
        public static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (on) k.SetValue("Aetheris", "\"" + Application.ExecutablePath + "\" --tray");
                else if (k.GetValue("Aetheris") != null) k.DeleteValue("Aetheris");
            }
        }
    }

    class Tray : ApplicationContext
    {
        readonly NotifyIcon ni;
        readonly MenuItem miStart, miStop, miRestart;
        bool exiting;
        public static Tray Instance;

        public Tray(bool open)
        {
            Instance = this;
            App.Ui = new Control();
            App.Ui.CreateControl();
            var h = App.Ui.Handle;

            Icon icon = null;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) icon = new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { }

            miStart = new MenuItem("Start server", (s, e) => Bg(() => Server.Start()));
            miStop = new MenuItem("Stop server", (s, e) => Bg(() => Server.Stop(false)));
            miRestart = new MenuItem("Restart server", (s, e) => Bg(() => Server.Stop(true)));
            var open1 = new MenuItem("Open Aetheris", (s, e) => App.OpenUi()) { DefaultItem = true };
            ni = new NotifyIcon
            {
                Icon = icon ?? SystemIcons.Application,
                Text = "Aetheris",
                Visible = true,
                ContextMenu = new ContextMenu(new[] {
                    open1, new MenuItem("-"), miStart, miStop, miRestart, new MenuItem("-"),
                    new MenuItem("Open server folder", (s, e) => { if (App.Configured) using (Process.Start("explorer.exe", "\"" + App.Cfg.ServerDir + "\"")) { } }),
                    new MenuItem("Exit Aetheris", (s, e) => Exit())
                })
            };
            ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) App.OpenUi(); };
            Server.Changed += () => { try { App.Ui.BeginInvoke(new Action(Refresh)); } catch { } };
            Server.Notify += (t, m) =>
            {
                if (!App.Cfg.Notify) return;
                try { App.Ui.BeginInvoke(new Action(() => ni.ShowBalloonTip(6000, t, m, ToolTipIcon.Warning))); } catch { }
            };
            Refresh();
            App.Init();
            if (open) App.OpenUi();
        }

        static void Bg(Func<string> f)
        {
            App.Bg(_ => { var err = f(); if (err != null) Server.Log(err); });
        }

        void Refresh()
        {
            string st = Server.State;
            string t = "Aetheris — " + char.ToUpper(st[0]) + st.Substring(1);
            int n = Server.PlayerList().Length;
            if (st == "online") t += " (" + n + (n == 1 ? " player)" : " players)");
            ni.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            miStart.Enabled = st == "offline";
            miStop.Enabled = miRestart.Enabled = st == "online" || st == "starting";
        }

        public void RequestExit() { try { App.Ui.BeginInvoke(new Action(Exit)); } catch { } }

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            if (Server.Running)
            {
                ni.ShowBalloonTip(4000, "Aetheris", "Saving and stopping the server before exiting…", ToolTipIcon.Info);
                App.Bg(_ => { Server.StopAndWait(90000); App.Ui.BeginInvoke(new Action(Quit)); });
            }
            else Quit();
        }

        void Quit()
        {
            ni.Visible = false;
            ni.Dispose();
            Server.StopPlayit();
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ minecraft process
    static class Server
    {
        public static readonly object L = new object();
        static Process proc, playit;
        static IntPtr job = IntPtr.Zero;
        static StreamWriter stdin;
        static readonly List<string> lines = new List<string>();
        static long baseSeq;
        const int MaxLines = 2000;
        static readonly List<string> players = new List<string>();
        public static volatile string State = "offline";
        public static string Version = "";
        public static DateTime Started;
        static bool stopRequested, restartPending;
        static readonly List<DateTime> crashes = new List<DateTime>();
        static TimeSpan lastCpu; static DateTime lastWall;
        public static event Action Changed;
        public static event Action<string, string> Notify;
        static void Alert(string t, string m) { var n = Notify; if (n != null) try { n(t, m); } catch { } }

        [StructLayout(LayoutKind.Sequential)]
        struct Pmc2
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
                QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
        }
        [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr h, ref Pmc2 c, uint cb);

        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[()][A-Z0-9]|\r");
        static readonly Regex JoinRe = new Regex(@"\]: ([A-Za-z0-9_.]{1,17}) joined the game");
        static readonly Regex LeftRe = new Regex(@"\]: ([A-Za-z0-9_.]{1,17}) left the game");
        static readonly Regex ListRe = new Regex(@"There are (\d+) (?:of a max of|/) ?(\d+) players online:?(.*)$");
        static readonly Regex VerRe = new Regex(@"Starting minecraft server version (\S+)");
        static readonly Regex DoneRe = new Regex(@"\]: Done \(|For help, type ""help""");

        public static bool Running { get { lock (L) return proc != null; } }

        static void SetPlayersFromList(Match m)
        {
            players.Clear();
            foreach (var n in m.Groups[3].Value.Split(','))
            {
                var t = Regex.Replace(n.Trim(), @"^\S+:\s*", "");   // Bukkit "group: name"
                if (Regex.IsMatch(t, @"^[A-Za-z0-9_.]{1,17}$")) players.Add(t);
            }
            int c;
            if (int.TryParse(m.Groups[1].Value, out c)) liveOnline = c;
        }

        // ---- live player data (server list ping), only queried while the panel is open
        static bool quietList; static DateTime quietUntil, lastQuiet, lastLive;
        static int liveOnline = -1, liveMax = -1, livePing = -1;

        public static Dictionary<string, object> Live()
        {
            if (State != "online") { liveOnline = liveMax = livePing = -1; return null; }
            if ((DateTime.Now - lastLive).TotalSeconds >= 2.5)
            {
                lastLive = DateTime.Now;
                int port; if (!int.TryParse(Props.Get("server-port", "25565"), out port)) port = 25565;
                var ip = Props.Get("server-ip", "");
                var sw = Stopwatch.StartNew();
                var st = Status(ip == "" ? "127.0.0.1" : ip, port, 2500);
                if (st != null)
                {
                    livePing = (int)sw.ElapsedMilliseconds;
                    var pl = st.ContainsKey("players") ? st["players"] as Dictionary<string, object> : null;
                    if (pl != null)
                    {
                        liveOnline = Convert.ToInt32(pl.ContainsKey("online") ? pl["online"] : 0);
                        liveMax = Convert.ToInt32(pl.ContainsKey("max") ? pl["max"] : 0);
                        var sample = (pl.ContainsKey("sample") ? pl["sample"] as object[] : null) ?? new object[0];
                        var names = sample.OfType<Dictionary<string, object>>().Select(x => Convert.ToString(x.ContainsKey("name") ? x["name"] : ""))
                            .Where(n => Regex.IsMatch(n, @"^[A-Za-z0-9_.]{1,17}$") && n != "Anonymous Player").ToList();
                        bool changed = false;
                        lock (L)
                        {
                            if (names.Count == liveOnline && names.Count > 0 && !(names.All(players.Contains) && players.Count == names.Count))
                            {
                                players.Clear(); players.AddRange(names); changed = true;
                            }
                            else if (liveOnline == 0 && players.Count > 0) { players.Clear(); changed = true; }
                            else if (players.Count != liveOnline && (DateTime.Now - lastQuiet).TotalSeconds > 10)
                            {
                                // counts disagree and the ping didn't list everyone: re-sync names with a quiet "list"
                                lastQuiet = DateTime.Now; quietList = true; quietUntil = DateTime.Now.AddSeconds(4);
                                Send("list", false);
                            }
                        }
                        if (changed) Fire();
                    }
                }
                else livePing = -1;
            }
            if (liveOnline < 0) return null;
            return Jsn.D("online", liveOnline, "max", liveMax, "ping", livePing);
        }

        static int ReadVarInt(Stream s)
        {
            int v = 0, shift = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) throw new IOException("eof");
                v |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 35) throw new IOException("varint");
            }
        }

        // Full "server list ping": returns the status JSON the multiplayer screen shows (players online, max, sample names).
        public static Dictionary<string, object> Status(string host, int port, int timeoutMs)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = timeoutMs; c.SendTimeout = timeoutMs;
                    var body = new List<byte>();
                    VarInt(body, 0); VarInt(body, -1);
                    var hb = Encoding.UTF8.GetBytes(host); VarInt(body, hb.Length); body.AddRange(hb);
                    body.Add((byte)(port >> 8)); body.Add((byte)port); VarInt(body, 1);
                    var pkt = new List<byte>(); VarInt(pkt, body.Count); pkt.AddRange(body); pkt.Add(1); pkt.Add(0);
                    var s = c.GetStream();
                    s.Write(pkt.ToArray(), 0, pkt.Count);
                    ReadVarInt(s);                       // packet length
                    if (ReadVarInt(s) != 0) return null; // packet id
                    int len = ReadVarInt(s);
                    if (len <= 0 || len > 1 << 20) return null;
                    var buf = new byte[len]; int off = 0;
                    while (off < len) { int n = s.Read(buf, off, len - off); if (n <= 0) return null; off += n; }
                    return Jsn.S.DeserializeObject(Encoding.UTF8.GetString(buf)) as Dictionary<string, object>;
                }
            }
            catch { return null; }
        }
        static void Fire() { var c = Changed; if (c != null) try { c(); } catch { } }

        public static string[] PlayerList() { lock (L) return players.ToArray(); }

        // changes that only apply after a restart (shown in the panel until the server restarts)
        static readonly List<string> pending = new List<string>();
        public static string[] Pending() { lock (L) return pending.ToArray(); }
        public static void NeedRestart(string what)
        {
            lock (L) { if (proc != null && !pending.Contains(what)) pending.Add(what); }
        }

        public static void Log(string msg) { AddLine("[Aetheris] " + msg); }

        static void AddLine(string line)
        {
            lock (L)
            {
                lines.Add(line);
                if (lines.Count > MaxLines) { lines.RemoveRange(0, 400); baseSeq += 400; }
                Monitor.PulseAll(L);
            }
        }

        public static long Seq { get { lock (L) return baseSeq + lines.Count; } }

        public static Dictionary<string, object> Since(long since, int waitMs)
        {
            lock (L)
            {
                long end = baseSeq + lines.Count;
                if (since > end || since < 0) since = Math.Max(baseSeq, end - 600);
                else if (since == end && waitMs > 0) { Monitor.Wait(L, waitMs); end = baseSeq + lines.Count; }
                long from = Math.Max(since, baseSeq);
                var arr = lines.GetRange((int)(from - baseSeq), (int)(end - from)).ToArray();
                return Jsn.D("seq", end, "lines", arr, "state", State);
            }
        }

        public static string WaitFor(Regex re, long from, int ms)
        {
            var until = DateTime.Now.AddMilliseconds(ms);
            lock (L)
            {
                while (true)
                {
                    long top = baseSeq + lines.Count;
                    for (long i = Math.Max(from, baseSeq); i < top; i++)
                    {
                        var l = lines[(int)(i - baseSeq)];
                        if (re.IsMatch(l)) return l;
                    }
                    from = top;
                    int left = (int)(until - DateTime.Now).TotalMilliseconds;
                    if (left <= 0) return null;
                    Monitor.Wait(L, left);
                }
            }
        }

        public static List<string> LinesFrom(long from)
        {
            lock (L)
            {
                long top = baseSeq + lines.Count; from = Math.Max(from, baseSeq);
                return lines.GetRange((int)(from - baseSeq), (int)(top - from));
            }
        }

        public static bool Eula()
        {
            try
            {
                var p = Path.Combine(App.Cfg.ServerDir, "eula.txt");
                return File.Exists(p) && Regex.IsMatch(File.ReadAllText(p), @"(?im)^\s*eula\s*=\s*true\s*$");
            }
            catch { return false; }
        }

        public static void AcceptEula()
        {
            if (!App.Configured) throw new Fail("Choose your server folder first.");
            File.WriteAllText(Path.Combine(App.Cfg.ServerDir, "eula.txt"),
                "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\n#Accepted via Aetheris " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\neula=true\n");
        }

        static string AikarFlags(int maxMb)
        {
            bool big = maxMb >= 12288;
            return "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 -XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch " +
                (big ? "-XX:G1NewSizePercent=40 -XX:G1MaxNewSizePercent=50 -XX:G1HeapRegionSize=16M -XX:G1ReservePercent=15 -XX:InitiatingHeapOccupancyPercent=20 "
                     : "-XX:G1NewSizePercent=30 -XX:G1MaxNewSizePercent=40 -XX:G1HeapRegionSize=8M -XX:G1ReservePercent=20 -XX:InitiatingHeapOccupancyPercent=15 ") +
                "-XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4 -XX:G1MixedGCLiveThresholdPercent=90 -XX:G1RSetUpdatingPauseTimePercent=5 " +
                "-XX:SurvivorRatio=32 -XX:+PerfDisableSharedMem -XX:MaxTenuringThreshold=1 -Dusing.aikars.flags=https://mcflags.emc.gs -Daikars.new.flags=true";
        }

        public static string Start()
        {
            var c = App.Cfg;
            lock (L)
            {
                if (proc != null) return "The server is already running.";
                if (!App.Configured) return "Choose your server folder in Settings first.";
                if (!Config.JarExists(c.ServerDir, c.Jar)) return "No server software selected. Pick or install one in Software.";
                if (!Eula()) return "You need to accept the Minecraft EULA before starting.";
                var ext = External();
                if (ext != null) return "A Minecraft server is already running outside Aetheris on port " + ext["port"] + " (" + ext["name"] + ", PID " + ext["pid"] + "). Stop it from the Dashboard first.";

                var a = new StringBuilder();
                a.Append("-Xms").Append(c.MinRam).Append("M -Xmx").Append(c.MaxRam).Append("M ");
                if (c.AikarFlags) a.Append(AikarFlags(c.MaxRam)).Append(' ');
                a.Append("-Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 ");   // only so the console shows text correctly
                if (c.JvmArgs != "") a.Append(c.JvmArgs).Append(' ');
                if (c.Jar.StartsWith("@")) a.Append(c.Jar).Append(" nogui");   // Forge / NeoForge 1.17+: java @libraries/.../win_args.txt
                else a.Append("-jar \"").Append(Path.Combine(c.ServerDir, c.Jar)).Append("\" nogui");

                var psi = new ProcessStartInfo(c.JavaPath, a.ToString())
                {
                    WorkingDirectory = c.ServerDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) try { OnLine(e.Data); } catch (Exception ex) { App.CrashLog("output", ex); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) try { OnLine(e.Data); } catch (Exception ex) { App.CrashLog("output", ex); } };
                p.Exited += (s, e) => { try { OnExit(s, e); } catch (Exception ex) { App.CrashLog("exit", ex); } };
                Log("Starting " + c.Jar + " (" + c.MinRam + "–" + c.MaxRam + " MB RAM)…");
                // windowed apps hand "ignore Ctrl+C" down to console children; clear it so a left-over server can still be
                // stopped safely (Ctrl+C = save and stop) if Aetheris ever closes while it runs
                Net2.AllowCtrlCInChildren();
                try { p.Start(); }
                catch (Exception ex)
                {
                    Log("Could not launch Java: " + ex.Message + ". Check the Java path in Settings.");
                    return "Could not launch Java (" + ex.Message + "). Check the Java path in Settings.";
                }
                // put Java (and anything it starts) in a job, so Kill always takes down the whole process tree
                Net2.Close(job);
                job = Net2.CreateJobObject(IntPtr.Zero, null);
                if (job != IntPtr.Zero && !Net2.AssignProcessToJobObject(job, p.Handle)) { Net2.Close(job); job = IntPtr.Zero; }
                stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                proc = p; State = "starting"; Started = DateTime.Now; players.Clear(); pending.Clear();
                stopRequested = false; restartPending = false;
                lastCpu = TimeSpan.Zero; lastWall = DateTime.MinValue;
            }
            StartPlayit();
            StartSampler();
            Fire();
            return null;
        }

        static void OnLine(string raw)
        {
            string line = Ansi.Replace(raw, "");
            bool changed = false;
            lock (L)
            {
                Match m;
                if (quietList && DateTime.Now < quietUntil && (m = ListRe.Match(line)).Success)
                {
                    quietList = false;
                    SetPlayersFromList(m);
                    Fire();
                    return;   // background re-sync: keep it out of the console
                }
                AddLine(line);
                if (State == "starting" && DoneRe.IsMatch(line)) { State = "online"; changed = true; }
                if ((m = VerRe.Match(line)).Success) Version = m.Groups[1].Value;
                if ((m = JoinRe.Match(line)).Success)
                {
                    var n = m.Groups[1].Value;
                    if (!players.Contains(n)) players.Add(n);
                    changed = true;
                }
                else if ((m = LeftRe.Match(line)).Success) { players.Remove(m.Groups[1].Value); changed = true; }
                else if ((m = ListRe.Match(line)).Success) { SetPlayersFromList(m); changed = true; }
            }
            if (changed) Fire();
        }

        static void OnExit(object sender, EventArgs e)
        {
            var p = (Process)sender;
            bool restart = false, crashRestart = false;
            // drain remaining output first (outside the lock — output handlers need it), but never wait forever:
            // a leftover child process can keep the output pipe open
            try
            {
                var drain = new Thread(() => { try { p.WaitForExit(); } catch { } }) { IsBackground = true };
                drain.Start();
                drain.Join(4000);
            }
            catch { }
            lock (L)
            {
                if (p != proc) return;
                int code = 0;
                try { code = p.ExitCode; } catch { }
                proc = null; stdin = null; players.Clear(); pending.Clear(); State = "offline";
                bool crashed = !stopRequested && !restartPending;
                Log("Server stopped" + (code != 0 ? " (exit code " + code + ")" : "") + ".");
                if (restartPending) restart = true;
                else if (crashed && App.Cfg.AutoRestart && (DateTime.Now - Started).TotalSeconds > 45)
                {
                    crashes.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 10);
                    crashes.Add(DateTime.Now);
                    if (crashes.Count <= 3) { restart = crashRestart = true; Log("The server crashed — restarting in 5 seconds (auto-restart is on)."); Alert("Minecraft server crashed", "Aetheris is restarting it automatically."); }
                    else { Log("The server crashed 3 times in 10 minutes — not restarting automatically."); Alert("Minecraft server keeps crashing", "It crashed 3 times in 10 minutes, so Aetheris left it offline. Check the console."); }
                }
                else if (crashed)
                {
                    Alert("Minecraft server stopped", "The server stopped unexpectedly (exit code " + code + ").");
                }
                restartPending = false;
                if (job != IntPtr.Zero) { Net2.TerminateJobObject(job, 1); Net2.Close(job); job = IntPtr.Zero; }   // clean up stray children
                try { p.Dispose(); } catch { }
            }
            if (!restart) StopPlayit();
            Fire();
            if (restart)
            {
                int delay = crashRestart ? 5000 : 1500;
                App.Bg(_ => { Thread.Sleep(delay); var err = Start(); if (err != null) Log(err); });
            }
        }

        public static bool Send(string cmd, bool echo)
        {
            lock (L)
            {
                if (stdin == null) return false;
                cmd = cmd.Replace("\r", " ").Replace("\n", " ");
                if (echo) AddLine("> " + cmd);
                try { stdin.WriteLine(cmd); return true; } catch { return false; }
            }
        }

        public static string Stop(bool restart)
        {
            Process p;
            lock (L)
            {
                if (proc == null) return restart ? Start() : "The server is not running.";
                if (State == "stopping") { if (restart) restartPending = true; return null; }
                stopRequested = !restart; restartPending = restart;
                State = "stopping"; p = proc;
                Log(restart ? "Restarting server…" : "Stopping server…");
                Send("stop", false);
            }
            Fire();
            App.Bg(_ => StopWatch(p, 90000));
            return null;
        }

        // Makes sure a stop always finishes: kill after the timeout, and clean up if the exit event never comes.
        static void StopWatch(Process p, int ms)
        {
            bool exited = false;
            try { exited = p.WaitForExit(ms); } catch { exited = true; }
            lock (L) { if (proc != p) return; }
            if (!exited) { Log("The server didn't stop within " + (ms / 1000) + " seconds — force killing it."); KillProc(p); }
            Thread.Sleep(10000);
            lock (L) { if (proc != p) return; }
            Log("The server process is gone but never reported back — cleaning up.");
            KillProc(p);
            OnExit(p, EventArgs.Empty);
        }

        static void KillProc(Process p)
        {
            if (job != IntPtr.Zero) Net2.TerminateJobObject(job, 1);
            try { p.Kill(); } catch { }
            try { Net2.KillTree(p.Id); } catch { }
        }

        // A Minecraft server listening on our server port that Aetheris didn't start (e.g. left over, or started from cmd).
        public static Dictionary<string, object> External()
        {
            lock (L) if (proc != null) return null;
            if (!App.Configured) return null;
            int port;
            if (!int.TryParse(Props.Get("server-port", "25565"), out port)) port = 25565;
            int pid = Net2.ListenerPid(port);
            if (pid <= 4 || pid == Process.GetCurrentProcess().Id) return null;
            var path = Net2.ProcessPath(pid);
            return Jsn.D("pid", pid, "port", port, "name", path != "" ? Path.GetFileName(path) : "PID " + pid, "path", path);
        }

        public static string StopExternal(int pid, bool force)
        {
            var ext = External();
            if (ext == null || (int)ext["pid"] != pid) return "That server isn't running any more.";
            if (force) { Log("Force killing outside server (PID " + pid + ")."); Net2.KillTree(pid); return null; }
            Log("Asking the outside server (PID " + pid + ") to save and stop (Ctrl+C)…");
            if (!Net2.SendCtrlC(pid)) return "Couldn't send a stop signal to it (it has no console). Use Force kill, or close its window yourself.";
            return null;
        }

        public static void ForceRestart(string why)
        {
            Process p;
            lock (L)
            {
                if (proc == null) return;
                stopRequested = false; restartPending = true; State = "stopping";
                Log(why);
                p = proc;
            }
            KillProc(p);
            App.Bg(_ => StopWatch(p, 5000));
            Alert("Minecraft server restarted", why.Replace("Watchdog: ", ""));
            Fire();
        }

        static void VarInt(List<byte> l, int v)
        {
            uint u = (uint)v;
            do { byte b = (byte)(u & 0x7F); u >>= 7; if (u != 0) b |= 0x80; l.Add(b); } while (u != 0);
        }

        // Minecraft "server list ping": true if the server answers a status request.
        public static bool Ping(string host, int port, int timeoutMs)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = timeoutMs; c.SendTimeout = timeoutMs;
                    var body = new List<byte>();
                    VarInt(body, 0); VarInt(body, -1);
                    var hb = Encoding.UTF8.GetBytes(host); VarInt(body, hb.Length); body.AddRange(hb);
                    body.Add((byte)(port >> 8)); body.Add((byte)port); VarInt(body, 1);
                    var pkt = new List<byte>(); VarInt(pkt, body.Count); pkt.AddRange(body); pkt.Add(1); pkt.Add(0);
                    var s = c.GetStream();
                    s.Write(pkt.ToArray(), 0, pkt.Count);
                    return s.ReadByte() >= 0;
                }
            }
            catch { return false; }
        }

        public static bool EnsurePlayit()
        {
            var c = App.Cfg;
            if (c.PlayitPath == "" || !File.Exists(c.PlayitPath) || PlayitRunning()) return false;
            Log("The playit.gg tunnel wasn't running — starting it again.");
            StartPlayit();
            return true;
        }

        public static void StopAndWait(int ms)
        {
            Process p;
            lock (L) p = proc;
            if (p == null) return;
            Stop(false);
            try { if (!p.WaitForExit(ms)) KillProc(p); } catch { }
            Thread.Sleep(300);
        }

        public static string Kill()
        {
            Process p;
            lock (L)
            {
                if (proc == null) return "The server is not running.";
                stopRequested = true; restartPending = false;
                State = "stopping";
                Log("Force killing the server (unsaved progress may be lost).");
                p = proc;
            }
            Fire();
            KillProc(p);
            App.Bg(_ => StopWatch(p, 5000));
            return null;
        }

        // ---- live RAM / CPU of the whole server process tree (Java plus anything it started, e.g. when "java" is a launcher stub)
        [DllImport("kernel32.dll")] static extern bool QueryInformationJobObject(IntPtr job, int cls, IntPtr info, int len, IntPtr ret);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public class Sample { public long t; public long ram; public double cpu; public int procs; }
        static readonly List<Sample> hist = new List<Sample>();
        static Sample last = new Sample();
        static System.Threading.Timer sampler;
        static long prevCpu100ns = -1; static DateTime prevWall;

        static List<int> TreePids(Process p)
        {
            var pids = new List<int>();
            if (job != IntPtr.Zero)
            {
                int size = 8 + 1024 * IntPtr.Size;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (QueryInformationJobObject(job, 3, buf, size, IntPtr.Zero))
                    {
                        int n = Marshal.ReadInt32(buf, 4);
                        for (int i = 0; i < n; i++)
                            pids.Add(IntPtr.Size == 8 ? (int)Marshal.ReadInt64(buf, 8 + i * 8) : Marshal.ReadInt32(buf, 8 + i * 4));
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            if (pids.Count == 0) try { pids.Add(p.Id); } catch { }
            return pids;
        }

        static long PrivateMb(int pid)
        {
            IntPtr h = OpenProcess(0x1000 | 0x0400, false, pid);   // QUERY_LIMITED | QUERY_INFORMATION
            if (h == IntPtr.Zero) h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                var m = new Pmc2(); m.cb = (uint)Marshal.SizeOf(typeof(Pmc2));
                if (!GetProcessMemoryInfo(h, ref m, m.cb)) return 0;
                ulong v = m.PrivateWorkingSetSize.ToUInt64();
                if (v == 0) v = m.WorkingSetSize.ToUInt64();
                return (long)(v / 1048576UL);
            }
            finally { CloseHandle(h); }
        }

        static long TreeCpu100ns(Process p)
        {
            if (job != IntPtr.Zero)
            {
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    if (QueryInformationJobObject(job, 1, buf, 48, IntPtr.Zero))
                        return Marshal.ReadInt64(buf, 0) + Marshal.ReadInt64(buf, 8);   // total user + kernel time of every process in the job
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            try { p.Refresh(); return p.TotalProcessorTime.Ticks; } catch { return -1; }
        }

        static void StartSampler()
        {
            lock (hist) { hist.Clear(); last = new Sample(); prevCpu100ns = -1; }
            if (sampler == null) sampler = new System.Threading.Timer(_ => SampleNow(), null, 500, Timeout.Infinite);
            else sampler.Change(500, Timeout.Infinite);
        }

        static void SampleNow()
        {
            Process p;
            lock (L) p = proc;
            if (p == null) return;   // server stopped: sampler goes quiet (0% CPU when idle)
            try
            {
                var pids = TreePids(p);
                long ram = 0;
                foreach (var id in pids) ram += PrivateMb(id);
                long cpuT = TreeCpu100ns(p);
                var now = DateTime.UtcNow;
                double cpu = 0;
                lock (hist)
                {
                    if (prevCpu100ns >= 0 && cpuT >= prevCpu100ns)
                    {
                        double wallMs = (now - prevWall).TotalMilliseconds;
                        if (wallMs > 0) cpu = Math.Max(0, Math.Min(100, (cpuT - prevCpu100ns) / 10000.0 / wallMs / Environment.ProcessorCount * 100));
                    }
                    prevCpu100ns = cpuT; prevWall = now;
                    last = new Sample { t = Jsn.Ms(now), ram = ram, cpu = Math.Round(cpu, 1), procs = pids.Count };
                    hist.Add(last);
                    if (hist.Count > 400) hist.RemoveRange(0, hist.Count - 400);
                }
            }
            catch { }
            // every second while someone watches the panel, every 5 s otherwise (keeps a short history for the graphs)
            int next = (DateTime.Now - App.LastTouch).TotalSeconds < 15 ? 1000 : 5000;
            try { sampler.Change(next, Timeout.Infinite); } catch { }
        }

        public static Sample Latest() { lock (hist) return last; }
        public static Sample[] History(long since)
        {
            lock (hist) return hist.Where(x => x.t > since && x.t > Jsn.Ms(DateTime.UtcNow) - 6 * 60000).ToArray();
        }

        public static void Stats(out long ramMb, out double cpu)
        {
            var s = Latest();
            bool running; lock (L) running = proc != null;
            ramMb = running ? s.ram : 0; cpu = running ? s.cpu : 0;
        }

        public static bool SnakeRules()
        {
            var v = Version ?? "";
            var m = Regex.Match(v, @"^(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return false;
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value), c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return a >= 26 || (a == 1 && (b > 21 || (b == 21 && c >= 11)));
        }

        public static Dictionary<string, object> QueryRules(IEnumerable<string> names)
        {
            if (!Running) throw new Fail("Start the server to read game rules.");
            long from = Seq; int n = 0;
            foreach (var r in names)
            {
                if (!Regex.IsMatch(r ?? "", @"^[A-Za-z0-9_:]{2,64}$")) continue;
                Send("gamerule " + r, false); n++;
            }
            Thread.Sleep(Math.Min(4000, 700 + n * 40));
            var re = new Regex(@"(?i)game ?rule (?:minecraft:)?(\S+) is currently set to:? (\S+)");
            var res = new Dictionary<string, object>();
            foreach (var l in LinesFrom(from))
            {
                var m = re.Match(l);
                if (m.Success) res[m.Groups[1].Value] = m.Groups[2].Value.TrimEnd('.');
            }
            return res;
        }

        // playit.gg tunnel helper (optional)
        static string PlayitName { get { return App.Cfg.PlayitPath != "" ? Path.GetFileNameWithoutExtension(App.Cfg.PlayitPath) : "playit"; } }

        public static bool PlayitRunning()
        {
            try
            {
                var ps = Process.GetProcessesByName(PlayitName);
                bool r = ps.Length > 0;
                foreach (var x in ps) x.Dispose();
                return r;
            }
            catch { return false; }
        }

        public static void StartPlayit()
        {
            var c = App.Cfg;
            if (!c.StartPlayit || c.PlayitPath == "" || !File.Exists(c.PlayitPath) || PlayitRunning()) return;
            try
            {
                playit = Process.Start(new ProcessStartInfo(c.PlayitPath)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    WorkingDirectory = Path.GetDirectoryName(c.PlayitPath)
                });
                Log("Started playit.gg tunnel.");
            }
            catch (Exception ex) { Log("Could not start playit.gg: " + ex.Message); }
        }

        public static void StopPlayit()
        {
            var p = playit; playit = null;
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); Log("Stopped playit.gg tunnel."); } p.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------ server.properties
    static class Props
    {
        static string FilePath { get { return Path.Combine(App.Cfg.ServerDir, "server.properties"); } }
        public static bool Exists() { return App.Configured && File.Exists(FilePath); }

        static bool Parse(string raw, out string k, out string v)
        {
            k = v = null;
            var s = raw.TrimStart();
            if (s.Length == 0 || s[0] == '#' || s[0] == '!') return false;
            int i = s.IndexOfAny(new[] { '=', ':' });
            if (i <= 0) return false;
            k = s.Substring(0, i).Trim();
            v = Unesc(s.Substring(i + 1).TrimStart());
            return true;
        }

        public static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>();
            if (!Exists()) return d;
            foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string k, v;
                if (Parse(raw, out k, out v)) d[k] = v;
            }
            return d;
        }

        public static string Get(string key, string def)
        {
            string v;
            return Read().TryGetValue(key, out v) && v != "" ? v : def;
        }

        public static void Write(Dictionary<string, string> changes)
        {
            if (!App.Configured) throw new Fail("Choose your server folder first.");
            foreach (var k in changes.Keys)
                if (!Regex.IsMatch(k, @"^[A-Za-z0-9._\-]+$")) throw new Fail("Invalid property name: " + k);
            var list = File.Exists(FilePath) ? File.ReadAllLines(FilePath, Encoding.UTF8).ToList() : new List<string> { "#Minecraft server properties" };
            var pending = new Dictionary<string, string>(changes);
            for (int i = 0; i < list.Count; i++)
            {
                string k, v;
                if (Parse(list[i], out k, out v) && pending.ContainsKey(k))
                {
                    list[i] = k + "=" + Esc(pending[k]);
                    pending.Remove(k);
                }
            }
            foreach (var kv in pending) list.Add(kv.Key + "=" + Esc(kv.Value));
            File.WriteAllLines(FilePath, list, new UTF8Encoding(false));
        }

        static string Unesc(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i]; int code;
                    if (n == 'u' && i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, null, out code))
                    { sb.Append((char)code); i += 4; continue; }
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n == 'r' ? '\r' : n == 'f' ? '\f' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string Esc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < (s ?? "").Length; i++)
            {
                char c = s[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == ':' || c == '=') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') { }
                else if (c == '\t') sb.Append("\\t");
                else if (c == ' ' && i == 0) sb.Append("\\ ");
                else if (c < 32 || c > 126) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ players
    static class Players
    {
        static readonly string[] Files = { "whitelist.json", "ops.json", "banned-players.json", "banned-ips.json" };
        static readonly string[] Keys = { "whitelist", "ops", "bans", "ipbans" };
        static readonly object FileLock = new object();

        static string P(string f) { return Path.Combine(App.Cfg.ServerDir, f); }

        static List<Dictionary<string, object>> ReadList(string file)
        {
            var res = new List<Dictionary<string, object>>();
            try
            {
                if (!App.Configured || !File.Exists(P(file))) return res;
                var arr = Jsn.S.DeserializeObject(File.ReadAllText(P(file))) as object[];
                if (arr != null) foreach (var o in arr) { var d = o as Dictionary<string, object>; if (d != null) res.Add(d); }
            }
            catch { }
            return res;
        }

        public static object Lists()
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < Files.Length; i++) d[Keys[i]] = ReadList(Files[i]);
            d["online"] = Server.PlayerList();
            return d;
        }

        public static void Action(string list, string op, string name, string reason)
        {
            name = (name ?? "").Trim(); reason = (reason ?? "").Trim().Replace("\n", " ");
            bool ip = list == "ipbans";
            if (ip ? !Regex.IsMatch(name, @"^[0-9A-Fa-f:.]{3,45}$") : !Regex.IsMatch(name, @"^[A-Za-z0-9_.]{1,17}$"))
                throw new Fail(ip ? "That doesn't look like an IP address." : "That doesn't look like a valid Minecraft username.");
            bool add = op == "add";
            if (op == "kick")
            {
                if (!Server.Send("kick " + name + (reason != "" ? " " + reason : ""), true)) throw new Fail("The server is not running.");
                return;
            }
            if (Server.Running)
            {
                string cmd;
                switch (list)
                {
                    case "whitelist": cmd = "whitelist " + (add ? "add " : "remove ") + name; break;
                    case "ops": cmd = (add ? "op " : "deop ") + name; break;
                    case "bans": cmd = add ? "ban " + name + (reason != "" ? " " + reason : "") : "pardon " + name; break;
                    case "ipbans": cmd = add ? "ban-ip " + name + (reason != "" ? " " + reason : "") : "pardon-ip " + name; break;
                    default: throw new Fail("Unknown list");
                }
                Server.Send(cmd, true);
                if (list == "whitelist") Server.Send("whitelist reload", false);
                Thread.Sleep(700);
                return;
            }
            // server offline: edit the JSON file directly
            int idx = Array.IndexOf(Keys, list);
            if (idx < 0) throw new Fail("Unknown list");
            lock (FileLock)
            {
                var items = ReadList(Files[idx]);
                string field = ip ? "ip" : "name";
                items.RemoveAll(x => x.ContainsKey(field) && string.Equals(Convert.ToString(x[field]), name, StringComparison.OrdinalIgnoreCase));
                if (add)
                {
                    var e = new Dictionary<string, object>();
                    if (!ip) { e["uuid"] = Uuid(name); e["name"] = name; } else e["ip"] = name;
                    if (list == "ops") { e["level"] = 4; e["bypassesPlayerLimit"] = false; }
                    if (list == "bans" || list == "ipbans")
                    {
                        e["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + DateTime.Now.ToString("zzz").Replace(":", "");
                        e["source"] = "Aetheris"; e["expires"] = "forever";
                        e["reason"] = reason != "" ? reason : "Banned by an operator.";
                    }
                    items.Add(e);
                }
                File.WriteAllText(P(Files[idx]), Jsn.S.Serialize(items), new UTF8Encoding(false));
            }
        }

        static string Uuid(string name)
        {
            bool online = Props.Get("online-mode", "true") != "false";
            if (!online || name.StartsWith(".")) return OfflineUuid(name);
            try
            {
                using (var wc = Net.Client())
                {
                    var s = wc.DownloadString("https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(name));
                    var d = Jsn.S.DeserializeObject(s) as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("id")) return Dash(Convert.ToString(d["id"]));
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null || (int)r.StatusCode >= 500) throw new Fail("Couldn't reach Mojang to look up \"" + name + "\". Start the server and try again, or check your internet.");
            }
            throw new Fail("No premium Minecraft account is named \"" + name + "\". (Turn off online-mode for cracked players.)");
        }

        static string OfflineUuid(string name)
        {
            byte[] b;
            using (var md5 = MD5.Create()) b = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            b[6] = (byte)((b[6] & 0x0f) | 0x30);
            b[8] = (byte)((b[8] & 0x3f) | 0x80);
            return Dash(BitConverter.ToString(b).Replace("-", "").ToLowerInvariant());
        }

        static string Dash(string h)
        {
            if (h.Length != 32) return h;
            return h.Substring(0, 8) + "-" + h.Substring(8, 4) + "-" + h.Substring(12, 4) + "-" + h.Substring(16, 4) + "-" + h.Substring(20);
        }
    }

    // ------------------------------------------------------------------ worlds & backups
    static class Fs
    {
        public static long DirSize(string d)
        {
            long n = 0;
            try { foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length; } catch { }
            return n;
        }

        public static void DeleteDir(string d)
        {
            if (!Directory.Exists(d)) return;
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(d, true);
        }

        public static string Safe(string rel)
        {
            if (!App.Configured) throw new Fail("Choose your server folder first.");
            var root = Path.GetFullPath(App.Cfg.ServerDir).TrimEnd('\\');
            var full = Path.GetFullPath(Path.Combine(root, (rel ?? "").Replace('/', '\\').TrimStart('\\'))).TrimEnd('\\');
            if (!(full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new Fail("That path is outside the server folder.");
            return full;
        }

        public static string Rel(string full)
        {
            var root = Path.GetFullPath(App.Cfg.ServerDir).TrimEnd('\\');
            return full.Length <= root.Length ? "" : full.Substring(root.Length + 1).Replace('\\', '/');
        }
    }

    static class Worlds
    {
        static string Base(string n)
        {
            if (n.EndsWith("_nether")) return n.Substring(0, n.Length - 7);
            if (n.EndsWith("_the_end")) return n.Substring(0, n.Length - 8);
            return n;
        }

        public static List<string> Dims(string name)
        {
            var dir = App.Cfg.ServerDir;
            return new[] { name, name + "_nether", name + "_the_end" }.Select(x => Path.Combine(dir, x)).Where(Directory.Exists).ToList();
        }

        public static object List()
        {
            var res = new List<Dictionary<string, object>>();
            var level = Props.Get("level-name", "world");
            if (App.Configured)
            {
                var dir = App.Cfg.ServerDir;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (!File.Exists(Path.Combine(d, "level.dat"))) continue;
                    if (Base(name) != name && Directory.Exists(Path.Combine(dir, Base(name)))) continue;
                    long size = Dims(name).Sum(x => Fs.DirSize(x));
                    res.Add(Jsn.D("name", name, "active", name.Equals(level, StringComparison.OrdinalIgnoreCase), "size", size,
                        "modified", Jsn.Ms(Directory.GetLastWriteTimeUtc(d)), "pending", false));
                }
            }
            if (!res.Any(x => (bool)x["active"])) res.Insert(0, Jsn.D("name", level, "active", true, "size", 0L, "modified", 0L, "pending", true));
            return Jsn.D("worlds", res, "level", level, "seed", Props.Get("level-seed", ""), "type", Props.Get("level-type", "minecraft:normal"));
        }

        static string CheckName(string n)
        {
            n = (n ?? "").Trim();
            if (!Regex.IsMatch(n, @"^[A-Za-z0-9 _\-]{1,40}$")) throw new Fail("World names can use letters, numbers, spaces, - and _ (max 40).");
            return n;
        }

        public static void Op(Req r)
        {
            if (Server.Running) throw new Fail("Stop the server before changing worlds.");
            string op = r.S("op"), name = r.S("name");
            var ch = new Dictionary<string, string>();
            switch (op)
            {
                case "activate":
                    ch["level-name"] = CheckName(name); Props.Write(ch); break;
                case "delete":
                    name = CheckName(name);
                    foreach (var d in Dims(name)) Fs.DeleteDir(d);
                    break;
                case "reset":
                    {
                        var level = Props.Get("level-name", "world");
                        foreach (var d in Dims(level)) Fs.DeleteDir(d);
                        ch["level-seed"] = r.S("seed").Trim();
                        if (r.S("type") != "") ch["level-type"] = r.S("type");
                        Props.Write(ch);
                        break;
                    }
                case "create":
                    name = CheckName(name);
                    if (Dims(name).Count > 0) throw new Fail("A world called \"" + name + "\" already exists.");
                    ch["level-name"] = name; ch["level-seed"] = r.S("seed").Trim();
                    if (r.S("type") != "") ch["level-type"] = r.S("type");
                    Props.Write(ch);
                    break;
                default: throw new Fail("Unknown world action");
            }
        }
    }

    static class Backups
    {
        static readonly object Lk = new object();
        static string Dir { get { return Path.Combine(App.Cfg.ServerDir, "backups"); } }

        public static DateTime LastAuto()
        {
            try
            {
                if (!Directory.Exists(Dir)) return DateTime.MinValue;
                var f = new DirectoryInfo(Dir).GetFiles("auto_*.zip").OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                return f == null ? DateTime.MinValue : f.LastWriteTime;
            }
            catch { return DateTime.MinValue; }
        }

        public static object List()
        {
            var res = new List<object>();
            if (App.Configured && Directory.Exists(Dir))
                foreach (var f in new DirectoryInfo(Dir).GetFiles("*.zip").OrderByDescending(x => x.LastWriteTime))
                    res.Add(Jsn.D("name", f.Name, "size", f.Length, "time", Jsn.Ms(f.LastWriteTimeUtc), "auto", f.Name.StartsWith("auto_")));
            return Jsn.D("backups", res, "dir", Dir);
        }

        public static string Create(string label, bool auto)
        {
            if (!App.Configured) throw new Fail("Choose your server folder first.");
            lock (Lk)
            {
                var level = Props.Get("level-name", "world");
                var dims = Worlds.Dims(level);
                if (dims.Count == 0) throw new Fail("The world \"" + level + "\" hasn't been generated yet — start the server once first.");
                label = Regex.Replace(label ?? "", @"[^A-Za-z0-9 _\-]", "").Trim().Replace(' ', '-');
                var name = (auto ? "auto_" : "") + level.Replace(' ', '-') + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + (label != "" ? "_" + label : "") + ".zip";
                Directory.CreateDirectory(Dir);
                bool live = Server.Running;
                Server.Log("Creating backup " + name + "…");
                if (live)
                {
                    long from = Server.Seq;
                    Server.Send("save-off", false);
                    Server.Send("save-all flush", false);
                    Server.WaitFor(new Regex(@"(?i)saved the (game|world)"), from, 20000);
                }
                var tmp = Path.Combine(Dir, name + ".part");
                int skipped = 0;
                try
                {
                    var root = App.Cfg.ServerDir.TrimEnd('\\');
                    using (var fs = new FileStream(tmp, FileMode.Create))
                    using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        foreach (var d in dims)
                            foreach (var file in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                            {
                                if (Path.GetFileName(file) == "session.lock") continue;
                                try
                                {
                                    using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                    {
                                        var e = za.CreateEntry(file.Substring(root.Length + 1).Replace('\\', '/'), CompressionLevel.Fastest);
                                        e.LastWriteTime = File.GetLastWriteTime(file);
                                        using (var es = e.Open()) src.CopyTo(es);
                                    }
                                }
                                catch (IOException) { skipped++; }
                            }
                    }
                    File.Move(tmp, Path.Combine(Dir, name));
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
                finally { if (live) Server.Send("save-on", false); }
                Server.Log("Backup complete: " + name + (skipped > 0 ? " (" + skipped + " locked files skipped)" : ""));
                if (auto) Prune();
                return name;
            }
        }

        static void Prune()
        {
            var olds = new DirectoryInfo(Dir).GetFiles("auto_*.zip").OrderByDescending(x => x.LastWriteTime).Skip(App.Cfg.BackupKeep);
            foreach (var f in olds) try { f.Delete(); } catch { }
        }

        static string FilePath(string name)
        {
            if (!Regex.IsMatch(name ?? "", @"^[^\\/:*?""<>|]+\.zip$")) throw new Fail("Invalid backup name.");
            var p = Path.Combine(Dir, name);
            if (!File.Exists(p)) throw new Fail("Backup not found.");
            return p;
        }

        public static void Delete(string name) { File.Delete(FilePath(name)); }

        public static void Restore(string name)
        {
            if (Server.Running) throw new Fail("Stop the server before restoring a backup.");
            var p = FilePath(name);
            lock (Lk)
            {
                var root = App.Cfg.ServerDir.TrimEnd('\\');
                string mainWorld = null;
                using (var za = ZipFile.OpenRead(p))
                {
                    var tops = za.Entries.Select(e => e.FullName.Split('/')[0]).Where(t => t != "").Distinct().ToList();
                    foreach (var t in tops)
                    {
                        if (!Regex.IsMatch(t, @"^[A-Za-z0-9 _\-]+$")) throw new Fail("Backup contains an unexpected folder: " + t);
                        if (!t.EndsWith("_nether") && !t.EndsWith("_the_end")) mainWorld = t;
                    }
                    foreach (var t in tops) Fs.DeleteDir(Path.Combine(root, t));
                    foreach (var e in za.Entries)
                    {
                        var dest = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('/', '\\')));
                        if (!dest.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                }
                if (mainWorld != null) Props.Write(new Dictionary<string, string> { { "level-name", mainWorld } });
                Server.Log("Restored backup " + name + ".");
            }
        }

        public static string PathFor(string name) { return FilePath(name); }
    }

    // ------------------------------------------------------------------ mods / plugins / datapacks
    static class Addons
    {
        public static string Dir(string kind)
        {
            if (!App.Configured) throw new Fail("Choose your server folder first.");
            var d = App.Cfg.ServerDir;
            switch (kind)
            {
                case "mods": return Path.Combine(d, "mods");
                case "plugins": return Path.Combine(d, "plugins");
                case "datapacks": return Path.Combine(d, Props.Get("level-name", "world"), "datapacks");
            }
            throw new Fail("Unknown add-on type.");
        }

        public static string Loader()
        {
            var jar = (App.Cfg.Jar ?? "").ToLowerInvariant();
            foreach (var k in new[] { "neoforge", "forge", "quilt", "fabric", "purpur", "folia", "paper", "spigot", "bukkit" })
                if (jar.Contains(k)) return k;
            if (!App.Configured) return "vanilla";
            var d = App.Cfg.ServerDir;
            if (Directory.Exists(Path.Combine(d, ".fabric"))) return "fabric";
            if (Directory.Exists(Path.Combine(d, @"libraries\net\neoforged"))) return "neoforge";
            if (Directory.Exists(Path.Combine(d, @"libraries\net\minecraftforge"))) return "forge";
            if (File.Exists(Path.Combine(d, "bukkit.yml"))) return "paper";
            return "vanilla";
        }

        static string verKey, verVal = "", loaderVer = "";

        public static string McVersion()
        {
            if (!string.IsNullOrEmpty(Server.Version)) return Server.Version;
            JarInfo();
            if (verVal != "") return verVal;
            var jar = App.Cfg.Jar ?? "";
            var nf = Regex.Match(jar, @"neoforge[/\\-](\d+)\.(\d+)\.(\d+)");
            if (nf.Success)   // NeoForge 21.4.x = Minecraft 1.21.4; from 26.x the prefix is the version: 26.1.2.116 = Minecraft 26.1.2
            {
                int a = int.Parse(nf.Groups[1].Value), b = int.Parse(nf.Groups[2].Value), c3 = int.Parse(nf.Groups[3].Value);
                return a < 26 ? "1." + a + (b > 0 ? "." + b : "") : a + "." + b + (c3 > 0 ? "." + c3 : "");
            }
            var m = Regex.Match(jar, @"\d+\.\d+(?:\.\d+)?");
            return m.Success ? m.Value : "";
        }

        public static string LoaderVersion() { JarInfo(); return loaderVer; }

        // Reads the Minecraft version from inside the server jar (version.json / install.properties), cached per jar.
        static void JarInfo()
        {
            try
            {
                if (!App.Configured || App.Cfg.Jar == "") return;
                if (App.Cfg.Jar.StartsWith("@")) return;
                var path = Path.Combine(App.Cfg.ServerDir, App.Cfg.Jar);
                if (!File.Exists(path)) return;
                var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
                if (key == verKey) return;
                verKey = key; verVal = ""; loaderVer = "";
                using (var za = ZipFile.OpenRead(path))
                {
                    var e = za.GetEntry("version.json");
                    if (e != null)
                    {
                        var d = Jsn.S.DeserializeObject(ReadEntry(e)) as Dictionary<string, object>;
                        if (d != null && d.ContainsKey("id")) verVal = Convert.ToString(d["id"]);
                    }
                    e = za.GetEntry("install.properties");
                    if (e != null)
                    {
                        var t = ReadEntry(e);
                        var g = Regex.Match(t, @"(?m)^game-version\s*=\s*(\S+)"); if (g.Success && verVal == "") verVal = g.Groups[1].Value;
                        var lv = Regex.Match(t, @"(?m)^fabric-loader-version\s*=\s*(\S+)"); if (lv.Success) loaderVer = lv.Groups[1].Value;
                    }
                }
                if (loaderVer == "") { var lm = Regex.Match(App.Cfg.Jar, @"loader\.([0-9][^-]*)"); if (lm.Success) loaderVer = lm.Groups[1].Value; }
                if (verVal == "")
                {
                    var log = Path.Combine(App.Cfg.ServerDir, @"logs\latest.log");
                    if (File.Exists(log))
                        using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs))
                            for (int i = 0; i < 300; i++)
                            {
                                var l = sr.ReadLine(); if (l == null) break;
                                var m = Regex.Match(l, @"Starting minecraft server version (\S+)");
                                if (m.Success) { verVal = m.Groups[1].Value; break; }
                            }
                }
            }
            catch { }
        }

        static Dictionary<string, object> JarMeta(string path)
        {
            try
            {
                using (var za = ZipFile.OpenRead(path))
                {
                    var e = za.GetEntry("fabric.mod.json");
                    if (e != null)
                    {
                        var d = Jsn.S.DeserializeObject(ReadEntry(e)) as Dictionary<string, object>;
                        if (d != null)
                        {
                            var dep = d.ContainsKey("depends") ? d["depends"] as Dictionary<string, object> : null;
                            object mc = dep != null && dep.ContainsKey("minecraft") ? dep["minecraft"] : null;
                            if (mc is object[]) mc = string.Join(" || ", ((object[])mc).Select(Convert.ToString));
                            return Jsn.D("name", d.ContainsKey("name") ? d["name"] : null, "version", d.ContainsKey("version") ? d["version"] : null,
                                "desc", d.ContainsKey("description") ? d["description"] : null,
                                "env", d.ContainsKey("environment") ? Convert.ToString(d["environment"]) : "*", "mc", mc != null ? Convert.ToString(mc) : null,
                                "id", d.ContainsKey("id") ? Convert.ToString(d["id"]) : null, "deps", dep != null ? dep.Keys.ToArray() : new string[0]);
                        }
                    }
                    e = za.GetEntry("META-INF/neoforge.mods.toml") ?? za.GetEntry("META-INF/mods.toml");
                    if (e != null)
                    {
                        var t = ReadEntry(e);
                        return Jsn.D("name", Toml(t, "displayName"), "version", Toml(t, "version"), "desc", Toml(t, "description"), "env", "*", "mc", null);
                    }
                    e = za.GetEntry("paper-plugin.yml") ?? za.GetEntry("plugin.yml");
                    if (e != null)
                    {
                        var t = ReadEntry(e);
                        return Jsn.D("name", Yml(t, "name"), "version", Yml(t, "version"), "desc", Yml(t, "description"), "env", "server", "mc", null);
                    }
                }
            }
            catch { }
            return null;
        }

        // Builds a client modpack so every player has exactly the server's mods:
        // a Modrinth .mrpack (one-click import in Modrinth App / Prism / CurseForge) and a plain .zip.
        public static object ClientPack()
        {
            var dir = Dir("mods");
            if (!Directory.Exists(dir)) throw new Fail("There are no mods yet.");
            var loader = Loader();
            if (!Regex.IsMatch(loader, "fabric|quilt|forge|neoforge")) throw new Fail("Your server isn't a mod loader (Fabric, Quilt, Forge or NeoForge) yet.");
            var mc = McVersion();
            var all = new DirectoryInfo(dir).GetFiles("*.jar").ToList();
            var sides = Sides(all);
            Dictionary<string, string> why;
            var need = PlayerSet(all, sides, out why);
            var files = all.Where(f => need.Contains(f.Name)).ToList();
            int skippedOptional = all.Count - files.Count;
            if (files.Count == 0) throw new Fail("None of your mods are needed by players. They're all server-only or optional, so players can join with plain Fabric/Minecraft.");

            var hashes = new Dictionary<string, string[]>();   // file -> sha1, sha512
            foreach (var f in files)
                using (var sha1 = SHA1.Create()) using (var sha512 = SHA512.Create())
                {
                    var b = File.ReadAllBytes(f.FullName);
                    hashes[f.Name] = new[] { Hex(sha1.ComputeHash(b)), Hex(sha512.ComputeHash(b)) };
                }
            Dictionary<string, object> known = null;
            try
            {
                var body = Jsn.S.Serialize(Jsn.D("hashes", hashes.Values.Select(h => h[0]).ToArray(), "algorithm", "sha1"));
                known = Jsn.S.DeserializeObject(Net.PostJson("https://api.modrinth.com/v2/version_files", body)) as Dictionary<string, object>;
            }
            catch { }
            known = known ?? new Dictionary<string, object>();

            var outDir = Path.Combine(App.Cfg.ServerDir, "client-pack");
            Directory.CreateDirectory(outDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            string baseName = "client-modpack-" + (mc != "" ? mc + "-" : "") + stamp;
            var index = new List<object>();
            int fromModrinth = 0;
            var mrpack = Path.Combine(outDir, baseName + ".mrpack");
            var zip = Path.Combine(outDir, baseName + ".zip");
            using (var fs = new FileStream(mrpack, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    var h = hashes[f.Name];
                    var v = known.ContainsKey(h[0]) ? known[h[0]] as Dictionary<string, object> : null;
                    string url = null;
                    if (v != null)
                        foreach (var o in (v.ContainsKey("files") ? v["files"] as object[] : null) ?? new object[0])
                        {
                            var fd = o as Dictionary<string, object>;
                            var fh = fd != null && fd.ContainsKey("hashes") ? fd["hashes"] as Dictionary<string, object> : null;
                            if (fh != null && fh.ContainsKey("sha1") && Convert.ToString(fh["sha1"]) == h[0]) url = Convert.ToString(fd["url"]);
                        }
                    if (url != null)
                    {
                        fromModrinth++;
                        index.Add(Jsn.D("path", "mods/" + f.Name, "hashes", Jsn.D("sha1", h[0], "sha512", h[1]),
                            "env", Jsn.D("client", "required", "server", "required"), "downloads", new[] { url }, "fileSize", f.Length));
                    }
                    else za.CreateEntryFromFile(f.FullName, "overrides/mods/" + f.Name, CompressionLevel.Fastest);
                }
                var deps = new Dictionary<string, object>();
                if (mc != "") deps["minecraft"] = mc;
                var lv = LoaderVersion();
                if (loader == "fabric") deps["fabric-loader"] = lv != "" ? lv : LatestFabricLoader();
                else if (loader == "quilt") deps["quilt-loader"] = lv != "" ? lv : "*";
                var e = za.CreateEntry("modrinth.index.json");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    w.Write(Jsn.S.Serialize(Jsn.D("formatVersion", 1, "game", "minecraft", "versionId", stamp,
                        "name", "Server mods " + (mc != "" ? "(" + mc + ")" : ""), "summary", "Mods needed to join the server. Made with Aetheris.",
                        "files", index, "dependencies", deps)));
            }
            using (var fs = new FileStream(zip, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files) za.CreateEntryFromFile(f.FullName, "mods/" + f.Name, CompressionLevel.Fastest);
                var e = za.CreateEntry("HOW TO INSTALL.txt");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    w.Write("Mods for the server (Minecraft " + (mc != "" ? mc : "?") + ", " + loader + ").\r\n\r\n" +
                        "EASIEST: import the .mrpack file into the Modrinth App, Prism Launcher or CurseForge app. It sets everything up for you.\r\n\r\n" +
                        "MANUAL:\r\n1. Install " + loader + " for Minecraft " + mc + (loader == "fabric" ? " (https://fabricmc.net/use/installer/)" : "") + ".\r\n" +
                        "2. Press Win+R, type %appdata%\\.minecraft and press Enter.\r\n3. Copy everything from this zip's mods folder into the mods folder there (create it if missing).\r\n" +
                        "4. Start Minecraft with the " + loader + " profile and join the server.\r\n");
            }
            Server.Log("Client modpack created: " + files.Count + " mods (" + fromModrinth + " linked to Modrinth).");
            return Jsn.D("ok", true, "mrpack", Fs.Rel(mrpack), "zip", Fs.Rel(zip), "count", files.Count, "linked", fromModrinth, "mc", mc, "loader", loader, "optional", skippedOptional);
        }

        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

        // What Modrinth says about each jar's client requirement ("required" / "optional" / "unsupported"), looked up by file hash.
        static readonly Dictionary<string, string> sha1Cache = new Dictionary<string, string>();   // path|mtime|size -> sha1
        static readonly Dictionary<string, string> sideCache = new Dictionary<string, string>();   // sha1 -> side ("" = not on Modrinth)
        static readonly Dictionary<string, string> projCache = new Dictionary<string, string>();   // sha1 -> Modrinth project id
        static readonly Dictionary<string, string[]> depCache = new Dictionary<string, string[]>(); // sha1 -> required dependency project ids

        static string Sha1Of(FileInfo f)
        {
            var key = f.FullName + "|" + f.LastWriteTimeUtc.Ticks + "|" + f.Length;
            lock (sha1Cache)
            {
                string h;
                if (sha1Cache.TryGetValue(key, out h)) return h;
                using (var sha = SHA1.Create()) using (var fs = f.OpenRead()) h = Hex(sha.ComputeHash(fs));
                sha1Cache[key] = h;
                return h;
            }
        }

        // Which mod jars every player must have: mods Modrinth marks as required on the client (or that the jar says run on both sides
        // and aren't on Modrinth), plus everything those mods depend on. why[name] = the mod that needs it.
        public static HashSet<string> PlayerSet(List<FileInfo> jars, Dictionary<string, string> sides, out Dictionary<string, string> why)
        {
            why = new Dictionary<string, string>();
            var meta = jars.ToDictionary(f => f.Name, f => JarMeta(f.FullName));
            var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byProj = new Dictionary<string, string>();
            foreach (var f in jars)
            {
                var m = meta[f.Name];
                if (m != null && m.ContainsKey("id") && m["id"] != null) byId[Convert.ToString(m["id"])] = f.Name;
                string sha = null; try { sha = Sha1Of(f); } catch { }
                string pj; lock (sideCache) if (sha != null && projCache.TryGetValue(sha, out pj)) byProj[pj] = f.Name;
            }
            var set = new HashSet<string>();
            var queue = new Queue<string>();
            foreach (var f in jars)
            {
                string side; var m = meta[f.Name];
                bool need = sides.TryGetValue(f.Name, out side) ? side == "required"
                    : !(m != null && m.ContainsKey("env") && (Convert.ToString(m["env"]) == "server"));
                if (need && set.Add(f.Name)) queue.Enqueue(f.Name);
            }
            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                var f = jars.First(x => x.Name == name);
                var m = meta[name];
                var add = new List<string>();
                string sha = null; try { sha = Sha1Of(f); } catch { }
                string[] dp; lock (sideCache) if (sha != null && depCache.TryGetValue(sha, out dp)) foreach (var d in dp) { string n; if (byProj.TryGetValue(d, out n)) add.Add(n); }
                if (m != null && m.ContainsKey("deps"))
                    foreach (var id in (m["deps"] as string[]) ?? new string[0])
                    {
                        if (id == "minecraft" || id == "java" || id == "fabricloader" || id == "fabric-loader") continue;
                        string n;
                        if (byId.TryGetValue(id, out n)) add.Add(n);
                        else if (id.StartsWith("fabric-") && (byId.TryGetValue("fabric-api", out n) || byId.TryGetValue("fabric", out n))) add.Add(n);   // Fabric API module
                    }
                var who = m != null && m["name"] != null ? Convert.ToString(m["name"]) : name;
                foreach (var n in add)
                    if (set.Add(n)) { why[n] = who; queue.Enqueue(n); }
            }
            return set;
        }

        public static Dictionary<string, string> Sides(IEnumerable<FileInfo> jars)
        {
            var bySha = new Dictionary<string, string>();   // sha1 -> file name
            foreach (var f in jars) try { bySha[Sha1Of(f)] = f.Name; } catch { }
            var unknown = bySha.Keys.Where(h => { lock (sideCache) return !sideCache.ContainsKey(h); }).ToList();
            if (unknown.Count > 0)
            {
                try
                {
                    var vf = Jsn.S.DeserializeObject(Net.PostJson("https://api.modrinth.com/v2/version_files",
                        Jsn.S.Serialize(Jsn.D("hashes", unknown.ToArray(), "algorithm", "sha1")))) as Dictionary<string, object> ?? new Dictionary<string, object>();
                    var proj = new Dictionary<string, string>();   // sha1 -> project id
                    foreach (var kv in vf)
                    {
                        var v = kv.Value as Dictionary<string, object>;
                        if (v == null || !v.ContainsKey("project_id")) continue;
                        proj[kv.Key] = Convert.ToString(v["project_id"]);
                        var deps = ((v.ContainsKey("dependencies") ? v["dependencies"] as object[] : null) ?? new object[0]).OfType<Dictionary<string, object>>()
                            .Where(x => Convert.ToString(x.ContainsKey("dependency_type") ? x["dependency_type"] : "") == "required" && x.ContainsKey("project_id") && x["project_id"] != null)
                            .Select(x => Convert.ToString(x["project_id"])).ToArray();
                        lock (sideCache) { projCache[kv.Key] = proj[kv.Key]; depCache[kv.Key] = deps; }
                    }
                    var sideOf = new Dictionary<string, string>();
                    if (proj.Count > 0)
                    {
                        var ids = Jsn.S.Serialize(proj.Values.Distinct().ToArray());
                        var arr = Jsn.S.DeserializeObject(Net.Fetch("https://api.modrinth.com/v2/projects?ids=" + Uri.EscapeDataString(ids))) as object[];
                        foreach (var o in arr ?? new object[0])
                        {
                            var d = o as Dictionary<string, object>;
                            if (d != null && d.ContainsKey("id")) sideOf[Convert.ToString(d["id"])] = d.ContainsKey("client_side") ? Convert.ToString(d["client_side"]) : "";
                        }
                    }
                    lock (sideCache)
                        foreach (var h in unknown)
                        {
                            string pid, side;
                            sideCache[h] = proj.TryGetValue(h, out pid) && sideOf.TryGetValue(pid, out side) ? side : "";
                        }
                }
                catch { }   // offline: fall back to what the jar says
            }
            var res = new Dictionary<string, string>();
            lock (sideCache)
                foreach (var kv in bySha) { string side; if (sideCache.TryGetValue(kv.Key, out side) && side != "") res[kv.Value] = side; }
            return res;
        }

        static string LatestFabricLoader()
        {
            try
            {
                var arr = Jsn.S.DeserializeObject(Net.Fetch("https://meta.fabricmc.net/v2/versions/loader")) as object[];
                foreach (var o in arr ?? new object[0])
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("stable") && d["stable"] is bool && (bool)d["stable"]) return Convert.ToString(d["version"]);
                }
            }
            catch { }
            return "*";
        }

        static string ReadEntry(ZipArchiveEntry e)
        {
            if (e.Length > 512 * 1024) return "";
            using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
        }
        static string Toml(string t, string k)
        {
            var m = Regex.Match(t, @"(?m)^\s*" + k + @"\s*=\s*(?:""([^""]*)""|'([^']*)')");
            var v = m.Success ? (m.Groups[1].Value + m.Groups[2].Value).Trim() : null;
            return v == null || v.Contains("${") ? null : v;
        }
        static string Yml(string t, string k)
        {
            var m = Regex.Match(t, @"(?m)^" + k + @":\s*['""]?(.*?)['""]?\s*$");
            return m.Success && m.Groups[1].Value != "" && !m.Groups[1].Value.Contains("${") ? m.Groups[1].Value : null;
        }

        public static object List(string kind)
        {
            var dir = Dir(kind);
            var items = new List<object>();
            var sides = kind == "mods" && Directory.Exists(dir) ? Sides(new DirectoryInfo(dir).GetFiles("*.jar*")) : new Dictionary<string, string>();
            Dictionary<string, string> why = new Dictionary<string, string>();
            var needSet = kind == "mods" && Directory.Exists(dir) ? PlayerSet(new DirectoryInfo(dir).GetFiles("*.jar").ToList(), sides, out why) : new HashSet<string>();
            if (Directory.Exists(dir))
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    bool dis = f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    var bn = dis ? f.Name.Substring(0, f.Name.Length - 9) : f.Name;
                    if (!bn.EndsWith(kind == "datapacks" ? ".zip" : ".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    var meta = kind == "datapacks" ? null : JarMeta(f.FullName);
                    object nm = meta != null ? meta["name"] : null;
                    items.Add(Jsn.D("file", f.Name, "name", nm ?? Path.GetFileNameWithoutExtension(bn),
                        "version", meta != null ? meta["version"] : null, "desc", meta != null ? meta["desc"] : null,
                        "env", meta != null && meta.ContainsKey("env") ? meta["env"] : null, "needs", meta != null && meta.ContainsKey("mc") ? meta["mc"] : null,
                        "side", sides.ContainsKey(f.Name) ? sides[f.Name] : null,
                        "players", kind == "mods" && !dis ? (object)needSet.Contains(f.Name) : null, "neededBy", why.ContainsKey(f.Name) ? why[f.Name] : null,
                        "enabled", !dis, "size", f.Length, "modified", Jsn.Ms(f.LastWriteTimeUtc), "dir", false));
                }
                if (kind == "datapacks")
                    foreach (var d in new DirectoryInfo(dir).GetDirectories())
                    {
                        if (!File.Exists(Path.Combine(d.FullName, "pack.mcmeta"))) continue;
                        bool dis = d.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                        items.Add(Jsn.D("file", d.Name, "name", dis ? d.Name.Substring(0, d.Name.Length - 9) : d.Name, "version", null, "desc", null,
                            "enabled", !dis, "size", Fs.DirSize(d.FullName), "modified", Jsn.Ms(d.LastWriteTimeUtc), "dir", true));
                    }
            }
            return Jsn.D("kind", kind, "items", items, "dir", Fs.Rel(dir), "loader", Loader(), "mc", McVersion(), "loaderVersion", LoaderVersion());
        }

        public static void Op(string kind, string op, string file)
        {
            var dir = Dir(kind);
            if (!Regex.IsMatch(file ?? "", @"^[^\\/:*?""<>|]+$") || file == "." || file == "..") throw new Fail("Invalid name.");
            var p = Path.Combine(dir, file);
            bool isDir = Directory.Exists(p);
            if (!isDir && !File.Exists(p)) throw new Fail("Not found — it may have been removed.");
            if (op == "delete") { if (isDir) Fs.DeleteDir(p); else File.Delete(p); return; }
            if (op != "toggle") throw new Fail("Unknown action.");
            var np = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 9) : p + ".disabled";
            if (File.Exists(np) || Directory.Exists(np)) throw new Fail("A file with that name already exists.");
            if (isDir) Directory.Move(p, np); else File.Move(p, np);
        }

        static void Extract(ZipArchiveEntry e, string dest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            e.ExtractToFile(dest, true);
        }

        static string Norm(ZipArchiveEntry e) { return e.FullName.Replace('\\', '/'); }

        public static object Import(string kind, string tmp, string name)
        {
            var dir = Dir(kind);
            var lname = name.ToLowerInvariant();
            if (!Regex.IsMatch(name, @"^[^\\/:*?""<>|]{1,150}$")) throw new Fail("Invalid file name.");
            var added = new List<string>();
            string note = null;

            if (lname.EndsWith(".jar"))
            {
                if (kind == "datapacks") throw new Fail("Datapacks are .zip files.");
                var dest = Path.Combine(dir, name);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                added.Add(name);
            }
            else if (lname.EndsWith(".mrpack") || lname.EndsWith(".zip"))
            {
                bool moveWhole = false;
                using (var za = ZipFile.OpenRead(tmp))
                {
                    if (za.GetEntry("modrinth.index.json") != null) return MrPack(za);
                    if (za.GetEntry("manifest.json") != null && kind == "mods" && !za.Entries.Any(e => Regex.IsMatch(Norm(e), @"(^|/)mods/.+\.jar$", RegexOptions.IgnoreCase))) return CfPack(za);
                    if (kind == "datapacks")
                    {
                        if (za.GetEntry("pack.mcmeta") != null) moveWhole = true;
                        else
                        {
                            var roots = za.Entries.Select(Norm).Where(n => Regex.IsMatch(n, @"^[^/]+/pack\.mcmeta$")).Select(n => n.Split('/')[0]).ToList();
                            if (roots.Count == 0) throw new Fail("That zip isn't a datapack (no pack.mcmeta found).");
                            foreach (var e in za.Entries)
                            {
                                var n = Norm(e);
                                var root = n.Split('/')[0];
                                if (!roots.Contains(root) || n.EndsWith("/")) continue;
                                var dest = Path.GetFullPath(Path.Combine(dir, n.Replace('/', '\\')));
                                if (!dest.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                                Extract(e, dest);
                            }
                            added.AddRange(roots);
                        }
                    }
                    else
                    {
                        // A whole server pack (has a mods/ or plugins/ folder): extract it into the server folder, configs included.
                        string folder = kind + "/", prefix = null;
                        foreach (var e in za.Entries)
                        {
                            var n = Norm(e);
                            int i = n.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
                            if (i >= 0 && (i == 0 || n[i - 1] == '/') && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                            {
                                var pre = n.Substring(0, i);
                                if (prefix == null || pre.Length < prefix.Length) prefix = pre;
                            }
                        }
                        if (prefix != null)
                        {
                            int files = 0;
                            foreach (var e in za.Entries)
                            {
                                var n = Norm(e);
                                if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || n.EndsWith("/")) continue;
                                var rel = n.Substring(prefix.Length);
                                var fn = Path.GetFileName(rel).ToLowerInvariant();
                                if (fn == "aetheris.json" || fn == "aetheris.exe" || fn == "aetheris.exe.config") continue;
                                Extract(e, Fs.Safe(rel));
                                files++;
                                if (rel.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && rel.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) added.Add(Path.GetFileName(rel));
                            }
                            note = "Server pack extracted: " + files + " files, including configs.";
                        }
                        else
                        {
                            foreach (var e in za.Entries)
                            {
                                var n = Norm(e);
                                if (!n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                                var fn = Path.GetFileName(n);
                                Extract(e, Path.Combine(dir, fn));
                                added.Add(fn);
                            }
                            if (added.Count == 0) throw new Fail("No .jar files were found in that zip.");
                        }
                    }
                }
                if (moveWhole)
                {
                    var dest = Path.Combine(dir, name);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                    added.Add(name);
                }
            }
            else throw new Fail("Upload a .jar, .zip or .mrpack file.");

            Server.Log("Added " + added.Count + " " + kind + ": " + string.Join(", ", added.Take(8)) + (added.Count > 8 ? "…" : ""));
            return Jsn.D("ok", true, "added", added, "note", note);
        }

        // CurseForge modpack (manifest.json + overrides). Needs the user's CurseForge API key to resolve the mod files.
        static object CfPack(ZipArchive za)
        {
            string json;
            using (var r = new StreamReader(za.GetEntry("manifest.json").Open(), Encoding.UTF8)) json = r.ReadToEnd();
            var d = Jsn.S.DeserializeObject(json) as Dictionary<string, object>;
            if (d == null || !d.ContainsKey("files")) throw new Fail("That zip has a manifest.json, but it isn't a CurseForge modpack.");
            if (App.Cfg.CurseForgeKey == "") throw new Fail("This is a CurseForge modpack. Add your free CurseForge API key in Settings so Aetheris can download its mods, or upload the pack's “Server Pack” zip instead (no key needed).");
            var files = (d["files"] as object[]) ?? new object[0];
            var dir = Dir("mods");
            int done = 0, failed = 0, i = 0;
            Server.Log("Installing CurseForge modpack " + (d.ContainsKey("name") ? d["name"] : "") + " (" + files.Length + " mods)…");
            foreach (var o in files)
            {
                i++;
                var f = o as Dictionary<string, object>;
                if (f == null) continue;
                try
                {
                    var info = Jsn.S.DeserializeObject(Net.CurseForge("/v1/mods/" + Convert.ToInt64(f["projectID"]) + "/files/" + Convert.ToInt64(f["fileID"]))) as Dictionary<string, object>;
                    var data = info != null ? info["data"] as Dictionary<string, object> : null;
                    var name = Convert.ToString(data["fileName"]);
                    var url = data.ContainsKey("downloadUrl") && data["downloadUrl"] != null ? Convert.ToString(data["downloadUrl"]) : null;
                    long fid = Convert.ToInt64(data["id"]);
                    if (url == null) url = "https://edge.forgecdn.net/files/" + (fid / 1000) + "/" + (fid % 1000) + "/" + Uri.EscapeDataString(name);
                    if (!Regex.IsMatch(name, @"^[^\\/:*?""<>|]+$")) throw new Exception("bad name");
                    Net.DownloadTo(url, Path.Combine(dir, name));
                    done++;
                }
                catch (Exception ex) { failed++; Server.Log("CurseForge: couldn't get file " + i + ": " + ex.Message); }
                if (i % 10 == 0) Server.Log("CurseForge modpack: " + i + "/" + files.Length + "…");
            }
            var over = (d.ContainsKey("overrides") ? Convert.ToString(d["overrides"]) : "overrides") + "/";
            int extracted = 0;
            foreach (var e in za.Entries)
            {
                var n = Norm(e);
                if (!n.StartsWith(over) || n.EndsWith("/")) continue;
                Extract(e, Fs.Safe(n.Substring(over.Length)));
                extracted++;
            }
            var mc = d.ContainsKey("minecraft") ? d["minecraft"] as Dictionary<string, object> : null;
            string mcv = mc != null && mc.ContainsKey("version") ? Convert.ToString(mc["version"]) : "";
            string loader = "";
            if (mc != null && mc.ContainsKey("modLoaders"))
                foreach (var l in (mc["modLoaders"] as object[]) ?? new object[0]) { var ld = l as Dictionary<string, object>; if (ld != null) { loader = Convert.ToString(ld["id"]); break; } }
            Server.Log("CurseForge modpack installed: " + done + " mods, " + extracted + " config files" + (failed > 0 ? ", " + failed + " failed" : "") + ".");
            return Jsn.D("ok", true, "added", new string[0], "pack", d.ContainsKey("name") ? d["name"] : "Modpack",
                "deps", Jsn.D("minecraft", mcv, "loader", loader),
                "note", "Installed " + done + " mods" + (failed > 0 ? " (" + failed + " couldn't be downloaded)" : "") + ". CurseForge packs are made for players: if the server crashes, disable client-only mods (minimaps, shaders, menus).");
        }

        static object MrPack(ZipArchive za)
        {
            string json;
            using (var r = new StreamReader(za.GetEntry("modrinth.index.json").Open(), Encoding.UTF8)) json = r.ReadToEnd();
            var d = Jsn.S.DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) throw new Fail("Broken modpack index.");
            var files = (d.ContainsKey("files") ? d["files"] as object[] : null) ?? new object[0];
            int done = 0, skipped = 0, i = 0;
            var root = App.Cfg.ServerDir;
            Server.Log("Installing modpack " + (d.ContainsKey("name") ? d["name"] : "") + " (" + files.Length + " files)…");
            foreach (var o in files)
            {
                i++;
                var f = o as Dictionary<string, object>;
                if (f == null) continue;
                var env = f.ContainsKey("env") ? f["env"] as Dictionary<string, object> : null;
                if (env != null && env.ContainsKey("server") && Convert.ToString(env["server"]) == "unsupported") { skipped++; continue; }
                var dest = Fs.Safe(Convert.ToString(f["path"]));
                string url = null;
                foreach (var u in (f.ContainsKey("downloads") ? f["downloads"] as object[] : null) ?? new object[0])
                    try { Net.Check(Convert.ToString(u)); url = Convert.ToString(u); break; } catch { }
                if (url == null) { skipped++; continue; }
                Net.DownloadTo(url, dest);
                done++;
                if (i % 10 == 0) Server.Log("Modpack: " + i + "/" + files.Length + " files…");
            }
            int over = 0;
            foreach (var e in za.Entries)
                foreach (var pre in new[] { "overrides/", "server-overrides/" })
                {
                    var n = Norm(e);
                    if (!n.StartsWith(pre) || n.EndsWith("/")) continue;
                    Extract(e, Fs.Safe(n.Substring(pre.Length)));
                    over++;
                }
            Server.Log("Modpack installed: " + done + " downloads, " + over + " config files" + (skipped > 0 ? ", " + skipped + " client-only skipped" : "") + ".");
            return Jsn.D("ok", true, "added", new string[0], "pack", d.ContainsKey("name") ? d["name"] : "Modpack",
                "downloaded", done, "skipped", skipped, "deps", d.ContainsKey("dependencies") ? d["dependencies"] : null,
                "note", "Installed " + done + " mods and " + over + " config files.");
        }
    }

    // ------------------------------------------------------------------ Forge / NeoForge server installers
    static class Loaders
    {
        public static object Install(string loader, string version, string mc)
        {
            if (!App.Configured) throw new Fail("Choose your server folder first.");
            if (Server.Running) throw new Fail("Stop the server before installing Forge or NeoForge.");
            if (!Regex.IsMatch(version ?? "", @"^[0-9A-Za-z.\-+]{1,40}$") || !Regex.IsMatch(mc ?? "", @"^[0-9A-Za-z.\-]{0,20}$")) throw new Fail("Invalid version.");
            string url;
            if (loader == "forge") url = "https://maven.minecraftforge.net/net/minecraftforge/forge/" + mc + "-" + version + "/forge-" + mc + "-" + version + "-installer.jar";
            else if (loader == "neoforge") url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/" + version + "/neoforge-" + version + "-installer.jar";
            else throw new Fail("Unknown loader.");
            var dir = App.Cfg.ServerDir;
            var inst = Path.Combine(dir, ".aetheris-installer.jar");
            Server.Log("Downloading the " + loader + " " + version + " installer…");
            Net.DownloadTo(url, inst);
            Server.Log("Running the " + loader + " installer (downloads libraries, this can take a few minutes)…");
            var psi = new ProcessStartInfo(App.Cfg.JavaPath, "-jar \"" + inst + "\" --installServer")
            { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            int lines = 0;
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler h = (s, e) => { if (e.Data != null && (++lines % 25 == 0 || Regex.IsMatch(e.Data, "(?i)error|exception|success|finished"))) Server.Log("[installer] " + e.Data); };
                p.OutputDataReceived += h; p.ErrorDataReceived += h;
                try { p.Start(); } catch (Exception ex) { throw new Fail("Couldn't run Java for the installer: " + ex.Message); }
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(15 * 60 * 1000)) { try { p.Kill(); } catch { } throw new Fail("The installer took too long."); }
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Fail("The " + loader + " installer failed (exit code " + p.ExitCode + "). See the console for details. Forge needs the right Java version for this Minecraft version.");
            }
            try { File.Delete(inst); File.Delete(inst + ".log"); } catch { }
            foreach (var f in Directory.GetFiles(dir, "installer.log")) try { File.Delete(f); } catch { }

            // how this Forge/NeoForge version starts: a runnable jar, or java @args file (1.17+)
            string jar = null;
            var args = loader == "forge"
                ? Path.Combine(dir, @"libraries\net\minecraftforge\forge\" + mc + "-" + version + @"\win_args.txt")
                : Path.Combine(dir, @"libraries\net\neoforged\neoforge\" + version + @"\win_args.txt");
            var shim = Directory.GetFiles(dir, (loader == "forge" ? "forge-" : "neoforge-") + "*-shim.jar").FirstOrDefault();
            var plain = loader == "forge" ? Path.Combine(dir, "forge-" + mc + "-" + version + ".jar") : null;
            if (shim != null) jar = Path.GetFileName(shim);
            else if (File.Exists(args)) jar = "@" + args.Substring(dir.TrimEnd('\\').Length + 1).Replace('\\', '/');
            else if (plain != null && File.Exists(plain)) jar = Path.GetFileName(plain);
            else throw new Fail("The installer finished, but Aetheris couldn't find how to start " + loader + ". Check the server folder.");
            App.Cfg.Jar = jar; App.Cfg.Save();
            Server.Log(loader + " " + version + " installed. It will be used on the next start.");
            return Jsn.D("ok", true, "jar", jar);
        }
    }

    // ------------------------------------------------------------------ automatic updates from GitHub releases
    static class Updater
    {
        const string Repo = "jjtjtyt6644/McUi";
        static string latest = "", status = "", error = "";
        static volatile bool busy;
        static bool notified;

        public static object Info()
        {
            return Jsn.D("current", Program.Version, "latest", latest, "available", latest != "" && Newer(latest), "auto", App.Cfg.AutoUpdate,
                "busy", busy, "status", status, "error", error, "lastCheck", App.Cfg.LastUpdateCheck);
        }

        static bool Newer(string v)
        {
            Version a, b;
            return System.Version.TryParse(v.TrimStart('v', 'V'), out a) && System.Version.TryParse(Program.Version, out b) && a > b;
        }

        // Called every minute by the scheduler; only talks to GitHub every 6 hours (or when asked).
        public static void Tick()
        {
            var c = App.Cfg;
            if (!c.AutoUpdate) return;
            if (DateTime.UtcNow.Ticks - c.LastUpdateCheck > TimeSpan.FromHours(6).Ticks) Check(false);
            if (latest != "" && Newer(latest) && !busy)
            {
                if (Server.State == "offline") InstallAsync(false);
                else if (!notified) { notified = true; Server.Log("Aetheris " + latest + " is available. It installs automatically the next time the server is stopped, or click Install in Settings."); }
            }
        }

        // Asks github.com which release is latest. Uses the release page redirect (not the rate-limited API) with cache-busting.
        static string TestFile { get { var f = Environment.GetEnvironmentVariable("AETHERIS_UPDATE_FILE"); return !string.IsNullOrEmpty(f) && File.Exists(f) ? f : null; } }

        public static void Check(bool force)
        {
            if (busy) return;
            if (TestFile != null) { latest = "v" + FileVersionInfo.GetVersionInfo(TestFile).FileVersion; App.Cfg.LastUpdateCheck = DateTime.UtcNow.Ticks; status = Newer(latest) ? "available" : "up to date"; return; }
            try
            {
                status = "checking"; error = "";
                var req = (HttpWebRequest)WebRequest.Create("https://github.com/" + Repo + "/releases/latest?_=" + DateTime.UtcNow.Ticks);
                req.Method = "HEAD"; req.AllowAutoRedirect = false; req.Timeout = 15000;
                req.UserAgent = "Aetheris/" + Program.Version;
                req.Headers[HttpRequestHeader.CacheControl] = "no-cache"; req.Headers[HttpRequestHeader.Pragma] = "no-cache";
                using (var r = (HttpWebResponse)req.GetResponse())
                {
                    var loc = r.Headers["Location"] ?? "";
                    var m = Regex.Match(loc, @"/releases/tag/([^/?#]+)");
                    if (m.Success) latest = Uri.UnescapeDataString(m.Groups[1].Value);
                }
                App.Cfg.LastUpdateCheck = DateTime.UtcNow.Ticks;
                try { App.Cfg.Save(); } catch { }
                status = latest != "" && Newer(latest) ? "available" : "up to date";
            }
            catch (Exception ex) { status = "failed"; error = ex.Message; }
        }

        public static void InstallAsync(bool userAsked)
        {
            if (busy) return;
            if (latest == "" || !Newer(latest)) { Check(true); if (latest == "" || !Newer(latest)) return; }
            busy = true;
            App.Bg(_ =>
            {
                try { Install(); }
                catch (Exception ex) { status = "failed"; error = ex.Message; Server.Log("Update failed: " + ex.Message); }
                finally { busy = false; }
            });
        }

        static void Install()
        {
            status = "downloading";
            var exe = Application.ExecutablePath;
            var dir = Path.GetDirectoryName(exe);
            var tmp = Path.Combine(dir, "Aetheris.update.exe");
            Server.Log("Downloading Aetheris " + latest + "…");
            if (TestFile != null) File.Copy(TestFile, tmp, true);
            else Net.DownloadTo("https://github.com/" + Repo + "/releases/download/" + Uri.EscapeDataString(latest) + "/Aetheris.exe?_=" + DateTime.UtcNow.Ticks, tmp);
            var b = File.ReadAllBytes(tmp);
            if (b.Length < 100000 || b[0] != 'M' || b[1] != 'Z') { File.Delete(tmp); throw new Exception("the download isn't a valid Aetheris.exe"); }
            var fv = FileVersionInfo.GetVersionInfo(tmp);
            Version nv, cv;
            if (!System.Version.TryParse(fv.FileVersion ?? "", out nv) || !System.Version.TryParse(Program.Version, out cv) || nv <= cv)
            { File.Delete(tmp); throw new Exception("the downloaded file isn't newer (" + fv.FileVersion + ")"); }
            status = "installing";
            App.Trace("update: installing " + fv.FileVersion + " over " + Program.Version);
            var old = Path.Combine(dir, "Aetheris.old-" + Program.Version + ".exe");
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);           // a running exe can be renamed
            File.Move(tmp, exe);
            Server.Log("Starting Aetheris " + latest + "… (the server is saved and restarted by the new version if it was running)");
            using (Process.Start(new ProcessStartInfo(exe, "--replace --tray") { UseShellExecute = true, WorkingDirectory = dir })) { }
            // the new copy asks this one to shut down (/api/shutdown) and takes over
        }

        public static void CleanupOld()
        {
            App.Bg(_ =>
            {
                Thread.Sleep(8000);
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var f in Directory.GetFiles(dir, "Aetheris.old*.exe").Concat(Directory.GetFiles(dir, "Aetheris.update.exe")))
                    try { File.Delete(f); } catch { }
            });
        }
    }

    // ------------------------------------------------------------------ bukkit.yml (Paper / Purpur / Spigot)
    static class Bukkit
    {
        static readonly string[] Keys = {
            "settings.allow-end", "settings.connection-throttle", "spawn-limits.monsters", "spawn-limits.animals",
            "spawn-limits.water-animals", "spawn-limits.ambient", "ticks-per.autosave", "ticks-per.monster-spawns" };
        static string FilePath { get { return Path.Combine(App.Cfg.ServerDir, "bukkit.yml"); } }

        static int Find(List<string> lines, string sec, string key)
        {
            bool inSec = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Trim() == "" || l.TrimStart().StartsWith("#")) continue;
                if (!char.IsWhiteSpace(l[0])) { inSec = l.TrimEnd() == sec + ":"; continue; }
                if (inSec && Regex.IsMatch(l, @"^\s+" + Regex.Escape(key) + @":")) return i;
            }
            return -1;
        }

        public static object Get()
        {
            if (!App.Configured || !File.Exists(FilePath)) return Jsn.D("exists", false, "values", new Dictionary<string, object>());
            var lines = File.ReadAllLines(FilePath).ToList();
            var vals = new Dictionary<string, object>();
            foreach (var k in Keys)
            {
                var parts = k.Split(new[] { '.' }, 2);
                int i = Find(lines, parts[0], parts[1]);
                if (i >= 0) vals[k] = lines[i].Substring(lines[i].IndexOf(':') + 1).Trim().Trim('\'', '"');
            }
            return Jsn.D("exists", true, "values", vals);
        }

        public static void Set(Dictionary<string, string> ch)
        {
            if (!App.Configured || !File.Exists(FilePath)) throw new Fail("bukkit.yml wasn't found. Start a Paper, Purpur or Spigot server once first.");
            var lines = File.ReadAllLines(FilePath).ToList();
            foreach (var kv in ch)
            {
                if (!Keys.Contains(kv.Key)) throw new Fail("Unknown setting " + kv.Key);
                if (!Regex.IsMatch(kv.Value ?? "", @"^[A-Za-z0-9.\-]{1,20}$")) throw new Fail("Invalid value for " + kv.Key);
                var parts = kv.Key.Split(new[] { '.' }, 2);
                int i = Find(lines, parts[0], parts[1]);
                if (i < 0) continue;
                lines[i] = lines[i].Substring(0, lines[i].IndexOf(':') + 1) + " " + kv.Value;
            }
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
        }
    }

    // ------------------------------------------------------------------ downloads
    static class Net
    {
        static readonly string[] Hosts = {
            "piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com",
            "api.papermc.io", "fill.papermc.io", "fill-data.papermc.io", "api.purpurmc.org", "meta.fabricmc.net",
            "api.modrinth.com", "cdn.modrinth.com", "files.minecraftforge.net", "maven.minecraftforge.net", "maven.neoforged.net",
            "api.curseforge.com", "edge.forgecdn.net", "mediafilez.forgecdn.net", "github.com", "objects.githubusercontent.com", "raw.githubusercontent.com", "release-assets.githubusercontent.com" };

        class Wc : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                r.Timeout = 30000;
                return r;
            }
        }

        public static WebClient Client()
        {
            var wc = new Wc { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "Aetheris/1.0 (self-hosted Minecraft server panel)";
            return wc;
        }

        public static Uri Check(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u) || u.Scheme != "https" || !Hosts.Contains(u.Host.ToLowerInvariant()))
                throw new Fail("Downloads are only allowed from Mojang, PaperMC, Purpur, Fabric, Forge, NeoForge, Modrinth, CurseForge or GitHub.");
            return u;
        }

        public static string Fetch(string url)
        {
            var u = Check(url);
            try { using (var wc = Client()) return wc.DownloadString(u); }
            catch (WebException ex) { throw new Fail("Request failed: " + ex.Message); }
        }

        public static string CurseForge(string path)
        {
            try
            {
                using (var wc = Client())
                {
                    wc.Headers["x-api-key"] = App.Cfg.CurseForgeKey;
                    wc.Headers[HttpRequestHeader.Accept] = "application/json";
                    return wc.DownloadString(Check("https://api.curseforge.com" + path));
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 403) throw new Fail("CurseForge rejected the API key. Check it in Settings.");
                throw new Fail("CurseForge request failed: " + ex.Message);
            }
        }

        public static string PostJson(string url, string body)
        {
            var u = Check(url);
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                return wc.UploadString(u, "POST", body);
            }
        }

        public static void DownloadTo(string url, string dest)
        {
            var u = Check(url);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            var tmp = dest + ".part";
            try
            {
                using (var wc = Client()) wc.DownloadFile(u, tmp);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (WebException ex) { try { File.Delete(tmp); } catch { } throw new Fail("Download failed: " + ex.Message); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }

        public static string Install(string url, string name)
        {
            var u = Check(url);
            if (!App.Configured) throw new Fail("Choose your server folder first.");
            name = (name ?? "").Trim();
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9._\-+]{1,80}\.jar$")) throw new Fail("Invalid jar file name.");
            var dest = Path.Combine(App.Cfg.ServerDir, name);
            var tmp = dest + ".part";
            Server.Log("Downloading " + name + "…");
            try
            {
                using (var wc = Client()) wc.DownloadFile(u, tmp);
                if (new FileInfo(tmp).Length < 10000) throw new Fail("The download looks broken (file too small).");
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (WebException ex) { try { File.Delete(tmp); } catch { } throw new Fail("Download failed: " + ex.Message); }
            catch { try { File.Delete(tmp); } catch { } throw; }
            App.Cfg.Jar = name; App.Cfg.Save();
            Server.Log("Installed " + name + " — it will be used on next start.");
            return name;
        }

        public static string JavaVersion(string java)
        {
            try
            {
                var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(java) ? "java" : java, "-version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    return err.Trim();
                }
            }
            catch (Exception ex) { throw new Fail("Java not found: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ http
    class Req
    {
        public string Method, Path;
        public readonly Dictionary<string, string> Q = new Dictionary<string, string>();
        public readonly Dictionary<string, string> H = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Stream Body, Out;
        public long Len;
        Dictionary<string, object> json;

        public string Qs(string k) { string v; return Q.TryGetValue(k, out v) ? v : ""; }

        public string Text()
        {
            if (Len <= 0) return "";
            if (Len > 32 * 1048576) throw new Fail("Request too large.");
            var buf = new byte[Len]; int off = 0;
            while (off < Len) { int n = Body.Read(buf, off, (int)Len - off); if (n <= 0) break; off += n; }
            Len = 0;
            return Encoding.UTF8.GetString(buf, 0, off);
        }

        public Dictionary<string, object> Json()
        {
            if (json == null)
            {
                var s = Text();
                json = (s.Trim() == "" ? null : Jsn.S.DeserializeObject(s) as Dictionary<string, object>) ?? new Dictionary<string, object>();
            }
            return json;
        }

        public string S(string k)
        {
            object v;
            if (!Json().TryGetValue(k, out v) || v == null) return "";
            if (v is bool) return (bool)v ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public void Send(int code, string type, byte[] body, string extra)
        {
            var h = "HTTP/1.1 " + code + " " + (code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 500 ? "Error" : "Bad Request") +
                "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" + (extra ?? "") + "\r\n";
            var hb = Encoding.ASCII.GetBytes(h);
            Out.Write(hb, 0, hb.Length);
            Out.Write(body, 0, body.Length);
            Out.Flush();
        }

        public void SaveBody(string path, long max)
        {
            if (Len > max) throw new Fail("File is too large.");
            using (var f = new FileStream(path, FileMode.Create))
            {
                var buf = new byte[65536]; long left = Len;
                while (left > 0)
                {
                    int n = Body.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    f.Write(buf, 0, n); left -= n;
                }
                Len = left;
                if (left > 0) { f.Close(); File.Delete(path); throw new Fail("Upload interrupted."); }
            }
        }

        public void SendJson(int code, object o) { Send(code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Jsn.S.Serialize(o)), null); }

        public void SendFile(string path, string downloadName)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var h = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + f.Length +
                    "\r\nContent-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(downloadName) +
                    "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(h);
                Out.Write(hb, 0, hb.Length);
                f.CopyTo(Out, 65536);
                Out.Flush();
            }
        }
    }

    static class Http
    {
        static TcpListener listener;
        public static int Port;

        public static void Start(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            NoInherit(listener.Server.Handle);
            Port = port;
            new Thread(Loop) { IsBackground = true, Name = "http" }.Start();
        }

        static void Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                NoInherit(c.Client.Handle);
                ThreadPool.QueueUserWorkItem(Handle, c);
            }
        }

        // .NET sockets are inheritable by default: without this, the Minecraft server (a child process) keeps a copy of
        // the panel's port open, so after Aetheris closes the port stays "in use" and the panel can't reconnect.
        [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
        static void NoInherit(IntPtr h) { try { SetHandleInformation(h, 1, 0); } catch { } }

        static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                if (sb.Length > 16384) throw new IOException("Header too long");
                sb.Append((char)b);
            }
        }

        static string Dec(string s) { return Uri.UnescapeDataString(s.Replace('+', ' ')); }

        static void Handle(object o)
        {
            var client = (TcpClient)o;
            try
            {
                client.ReceiveTimeout = 120000; client.SendTimeout = 120000; client.NoDelay = true;
                using (client)
                using (var ns = client.GetStream())
                {
                    var bs = new BufferedStream(ns, 8192);
                    var first = ReadLine(bs);
                    if (string.IsNullOrEmpty(first)) return;
                    var parts = first.Split(' ');
                    if (parts.Length < 2) return;
                    var req = new Req { Method = parts[0].ToUpperInvariant(), Body = bs, Out = ns };
                    var target = parts[1];
                    int qi = target.IndexOf('?');
                    req.Path = Dec(qi >= 0 ? target.Substring(0, qi) : target);
                    if (qi >= 0)
                        foreach (var kv in target.Substring(qi + 1).Split('&'))
                        {
                            if (kv.Length == 0) continue;
                            int e = kv.IndexOf('=');
                            req.Q[Dec(e >= 0 ? kv.Substring(0, e) : kv)] = e >= 0 ? Dec(kv.Substring(e + 1)) : "";
                        }
                    string line;
                    while (!string.IsNullOrEmpty(line = ReadLine(bs)))
                    {
                        int c = line.IndexOf(':');
                        if (c > 0) req.H[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
                    }
                    string cl; long len;
                    if (req.H.TryGetValue("Content-Length", out cl) && long.TryParse(cl, out len)) req.Len = len;
                    App.Touch();
                    Router.Run(req);
                }
            }
            catch { }
            finally { App.Touch(); }
        }
    }

    static class Router
    {
        static byte[] Page()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        public static void Run(Req r)
        {
            string host, origin;
            r.H.TryGetValue("Host", out host);
            host = (host ?? "").ToLowerInvariant();
            string port = ":" + Http.Port;
            bool hostOk = host == "127.0.0.1" + port || host == "localhost" + port;
            bool originOk = !r.H.TryGetValue("Origin", out origin) || origin == "http://" + host;
            if (!hostOk || !originOk) { r.Send(403, "text/plain", Encoding.UTF8.GetBytes("Forbidden"), null); return; }

            if (r.Path == "/" || r.Path == "/index.html")
            {
                r.Send(200, "text/html; charset=utf-8", Page(), "Content-Security-Policy: default-src 'self'; img-src 'self' data: blob: https://mc-heads.net https://cdn.modrinth.com https://media.forgecdn.net; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'\r\n");
                return;
            }
            if (!r.Path.StartsWith("/api/")) { r.Send(404, "text/plain", Encoding.UTF8.GetBytes("Not found"), null); return; }
            // custom header forces a CORS preflight, so other websites can't drive the panel
            if (r.Path != "/api/download" && !(r.Path == "/api/icon" && r.Method == "GET") && !r.H.ContainsKey("X-Aetheris")) { r.Send(403, "text/plain", Encoding.UTF8.GetBytes("Forbidden"), null); return; }

            try
            {
                var res = Api(r);
                if (res != null) r.SendJson(200, res);
            }
            catch (Fail f) { r.SendJson(400, Jsn.D("error", f.Message)); }
            catch (UnauthorizedAccessException ex) { r.SendJson(400, Jsn.D("error", "Access denied: " + ex.Message)); }
            catch (IOException ex) { try { r.SendJson(400, Jsn.D("error", ex.Message)); } catch { } }
            catch (Exception ex) { try { r.SendJson(500, Jsn.D("error", ex.Message)); } catch { } }
        }

        static object Ok() { return Jsn.D("ok", true); }

        static T Safe<T>(Func<T> f, T def) { try { return f(); } catch (Exception ex) { App.CrashLog("state", ex); return def; } }

        static object State(Req r)
        {
            long ram; double cpu;
            Server.Stats(out ram, out cpu);
            var live = Safe(() => Server.Live(), null);
            var p = Safe(() => Props.Read(), new Dictionary<string, string>());
            string v;
            var c = App.Cfg;
            return Jsn.D(
                "state", Server.State, "players", Server.PlayerList(),
                "max", live != null && (int)live["max"] > 0 ? (object)live["max"] : (p.TryGetValue("max-players", out v) ? v : "20"),
                "online", live != null ? live["online"] : null, "ping", live != null ? live["ping"] : null,
                "motd", p.TryGetValue("motd", out v) ? v : "A Minecraft Server",
                "port", p.TryGetValue("server-port", out v) ? v : "25565",
                "version", Server.Version,
                "uptime", Server.Running ? (long)(DateTime.Now - Server.Started).TotalSeconds : 0L,
                "ram", ram, "cpu", Math.Round(cpu, 1), "xmx", c.MaxRam,
                "address", c.Address, "jar", c.Jar, "dir", c.ServerDir,
                "eula", Server.Eula(), "configured", App.Configured,
                "playit", c.PlayitPath != "" || c.StartPlayit ? (object)Safe(() => Server.PlayitRunning(), false) : null,
                "snake", Server.SnakeRules(), "sysram", App.SysRamMb(), "sysfree", App.SysFreeMb(),
                "icon", Safe(() => App.Configured && File.Exists(Path.Combine(c.ServerDir, "server-icon.png")), false),
                "loader", Safe(() => Addons.Loader(), "vanilla"), "mc", Safe(() => Addons.McVersion(), ""), "sample", Server.Latest(),
                "hist", r.Qs("hist") != "" ? (object)Server.History(0) : null, "pending", Server.Pending(), "external", Safe(() => Server.External(), null), "aetheris", Program.Version, "tasks", c.Tasks.Count(t => t.Enabled), "update", Updater.Info());
        }

        static object SaveConfig(Req r)
        {
            var j = r.Json();
            var cd = j.ContainsKey("config") ? j["config"] as Dictionary<string, object> : null;
            if (cd == null) throw new Fail("Missing settings.");
            var n = Jsn.S.ConvertToType<Config>(cd);
            n.Normalize();
            if (n.ServerDir != "" && !Directory.Exists(n.ServerDir)) throw new Fail("That server folder doesn't exist.");
            if (n.ServerDir != "" && !Config.JarExists(n.ServerDir, n.Jar)) n.Jar = Config.FindJar(n.ServerDir);
            n.Cv = 2; n.LastUpdateCheck = App.Cfg.LastUpdateCheck;
            if (n.PlayitPath != "" && !File.Exists(n.PlayitPath)) throw new Fail("playit.gg program not found at that path.");
            n.Tasks = App.Cfg.Tasks;   // schedules are saved separately
            var o = App.Cfg;
            if (o.Jar != n.Jar || o.JavaPath != n.JavaPath || o.MinRam != n.MinRam || o.MaxRam != n.MaxRam || o.JvmArgs != n.JvmArgs || o.AikarFlags != n.AikarFlags)
                Server.NeedRestart("Java / RAM settings");
            n.Save();
            App.Cfg = n;
            if (j.ContainsKey("autostart") && j["autostart"] is bool) App.SetAutostart((bool)j["autostart"]);
            return Jsn.D("ok", true, "config", n, "portChanged", n.Port != Http.Port, "autostart", App.GetAutostart());
        }

        static object Browse(Req r)
        {
            string kind = r.S("kind"), start = r.S("start");
            var res = App.OnUi(() =>
            {
                using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
                {
                    owner.Show(); owner.Activate();
                    if (kind == "folder")
                    {
                        using (var d = new FolderBrowserDialog { Description = "Choose your Minecraft server folder (the one with your server .jar)", ShowNewFolderButton = true })
                        {
                            if (Directory.Exists(start)) d.SelectedPath = start;
                            return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
                        }
                    }
                    using (var d = new OpenFileDialog { Filter = kind == "jar" ? "Java archive (*.jar)|*.jar" : "Programs (*.exe)|*.exe|All files|*.*", Title = kind == "java" ? "Choose java.exe" : kind == "jar" ? "Choose a server .jar" : "Choose playit.exe" })
                    {
                        try { if (File.Exists(start)) d.InitialDirectory = Path.GetDirectoryName(start); else if (Directory.Exists(start)) d.InitialDirectory = start; } catch { }
                        return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
                    }
                }
            });
            return Jsn.D("path", res);
        }

        static readonly string[] TextExt = { ".txt", ".properties", ".json", ".yml", ".yaml", ".toml", ".cfg", ".conf", ".ini", ".log", ".md", ".mcmeta", ".csv", ".xml", ".js", ".sh", ".bat", ".cmd", ".ps1", ".snbt", ".lang", ".html", ".css", ".secret", "" };

        static object Api(Req r)
        {
            var c = App.Cfg;
            switch (r.Path)
            {
                case "/api/state": return State(r);

                case "/api/console":
                    {
                        long since;
                        if (!long.TryParse(r.Qs("since"), out since)) since = -1;
                        return Server.Since(since, 20000);
                    }

                case "/api/power":
                    {
                        string a = r.S("action"), err;
                        if (a == "start") err = Server.Start();
                        else if (a == "stop") err = Server.Stop(false);
                        else if (a == "restart") err = Server.Stop(true);
                        else if (a == "kill") err = Server.Kill();
                        else throw new Fail("Unknown action.");
                        if (err != null) throw new Fail(err);
                        return Ok();
                    }

                case "/api/cmd":
                    {
                        var cmd = r.S("cmd").Trim();
                        if (cmd.StartsWith("/")) cmd = cmd.Substring(1);
                        if (cmd == "") throw new Fail("Type a command first.");
                        if (!Server.Send(cmd, true)) throw new Fail("The server is not running.");
                        return Ok();
                    }

                case "/api/eula": Server.AcceptEula(); return Ok();

                case "/api/config":
                    if (r.Method == "POST") return SaveConfig(r);
                    return Jsn.D("config", c, "autostart", App.GetAutostart(), "sysram", App.SysRamMb(), "exe", Application.ExecutablePath, "portActive", Http.Port);

                case "/api/props":
                    if (r.Method == "POST")
                    {
                        var d = r.Json().ContainsKey("props") ? r.Json()["props"] as Dictionary<string, object> : null;
                        if (d == null) throw new Fail("Nothing to save.");
                        var ch = new Dictionary<string, string>();
                        foreach (var kv in d)
                            ch[kv.Key] = kv.Value == null ? "" : kv.Value is bool ? ((bool)kv.Value ? "true" : "false") : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                        Props.Write(ch);
                        Server.NeedRestart("server options");
                        return Ok();
                    }
                    return Jsn.D("props", Props.Read(), "exists", Props.Exists());

                case "/api/players": return Players.Lists();

                case "/api/player":
                    Players.Action(r.S("list"), r.S("op"), r.S("name"), r.S("reason"));
                    return Players.Lists();

                case "/api/gamerules":
                    {
                        var names = (r.Json().ContainsKey("rules") ? r.Json()["rules"] as object[] : null) ?? new object[0];
                        return Jsn.D("values", Server.QueryRules(names.Select(x => Convert.ToString(x))));
                    }

                case "/api/gamerule":
                    {
                        string rule = r.S("rule").Trim(), val = r.S("value").Trim();
                        if (!Regex.IsMatch(rule, @"^[A-Za-z0-9_:]{2,64}$") || !Regex.IsMatch(val, @"^[A-Za-z0-9_.\-]{1,32}$")) throw new Fail("Invalid game rule or value.");
                        long from = Server.Seq;
                        if (!Server.Send("gamerule " + rule + " " + val, true)) throw new Fail("Start the server to change game rules.");
                        var line = Server.WaitFor(new Regex(@"(?i)(game ?rule .* (is now|now set|set to)|unknown|incorrect|invalid|expected|error)"), from, 2500);
                        if (line != null && Regex.IsMatch(line, @"(?i)unknown|incorrect|invalid|expected|error")) throw new Fail("The server rejected it: " + Regex.Replace(line, @"^\[[^\]]*\]( \[[^\]]*\])?:? ?", ""));
                        return Ok();
                    }

                case "/api/worlds":
                    if (r.Method == "POST") Worlds.Op(r);
                    return Worlds.List();

                case "/api/backups":
                    if (r.Method == "POST")
                    {
                        string op = r.S("op");
                        if (op == "create") Backups.Create(r.S("label"), false);
                        else if (op == "restore") Backups.Restore(r.S("name"));
                        else if (op == "delete") Backups.Delete(r.S("name"));
                        else throw new Fail("Unknown backup action.");
                    }
                    return Backups.List();

                case "/api/jars":
                    {
                        var list = new List<object>();
                        if (App.Configured)
                            foreach (var f in new DirectoryInfo(c.ServerDir).GetFiles("*.jar").OrderBy(x => x.Name))
                                list.Add(Jsn.D("name", f.Name, "size", f.Length, "modified", Jsn.Ms(f.LastWriteTimeUtc)));
                        return Jsn.D("jars", list, "current", c.Jar, "argsFile", c.Jar.StartsWith("@"));
                    }

                case "/api/usejar":
                    {
                        var name = r.S("name");
                        if (!App.Configured || !Regex.IsMatch(name, @"^[^\\/:*?""<>|]+\.jar$") || !File.Exists(Path.Combine(c.ServerDir, name))) throw new Fail("Jar not found.");
                        c.Jar = name; c.Save();
                        Server.NeedRestart("server software");
                        return Ok();
                    }

                case "/api/fetch":
                    r.Send(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Net.Fetch(r.Qs("url"))), null);
                    return null;

                case "/api/install":
                    {
                        var res = Jsn.D("ok", true, "name", Net.Install(r.S("url"), r.S("name")));
                        Server.NeedRestart("server software");
                        return res;
                    }

                case "/api/install-loader":
                    {
                        var res = Loaders.Install(r.S("loader"), r.S("version"), r.S("mc"));
                        Server.NeedRestart("server software");
                        return res;
                    }

                case "/api/cf":
                    {
                        if (c.CurseForgeKey == "") throw new Fail("Add your CurseForge API key in Settings first.");
                        var path = r.Qs("path");
                        if (!path.StartsWith("/v1/")) throw new Fail("Invalid CurseForge request.");
                        r.Send(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Net.CurseForge(path)), null);
                        return null;
                    }

                case "/api/update":
                    if (r.Method == "POST")
                    {
                        if (r.S("action") == "install") Updater.InstallAsync(true);
                        else Updater.Check(true);
                    }
                    return Updater.Info();

                case "/api/java":
                    return Jsn.D("version", Net.JavaVersion(r.S("path") != "" ? r.S("path") : c.JavaPath));

                case "/api/browse": return Browse(r);

                case "/api/openfolder":
                    {
                        var p = Fs.Safe(r.S("path"));
                        using (Process.Start("explorer.exe", "\"" + p + "\"")) { }
                        return Ok();
                    }

                case "/api/files":
                    {
                        var dir = Fs.Safe(r.Qs("path"));
                        if (!Directory.Exists(dir)) throw new Fail("Folder not found.");
                        var di = new DirectoryInfo(dir);
                        var items = new List<object>();
                        foreach (var d in di.GetDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                            items.Add(Jsn.D("name", d.Name, "dir", true, "size", 0L, "modified", Jsn.Ms(d.LastWriteTimeUtc)));
                        foreach (var f in di.GetFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                            items.Add(Jsn.D("name", f.Name, "dir", false, "size", f.Length, "modified", Jsn.Ms(f.LastWriteTimeUtc),
                                "text", TextExt.Contains(f.Extension.ToLowerInvariant()) && f.Length < 3 * 1048576));
                        return Jsn.D("path", Fs.Rel(dir), "items", items);
                    }

                case "/api/file":
                    {
                        var p = Fs.Safe(r.Qs("path"));
                        if (r.Method == "POST")
                        {
                            if (Directory.Exists(p)) throw new Fail("That's a folder.");
                            File.WriteAllText(p, r.Text(), new UTF8Encoding(false));
                            if (Regex.IsMatch(p, @"\.(properties|ya?ml|json|toml|cfg|conf|ini|txt|snbt)$", RegexOptions.IgnoreCase)) Server.NeedRestart("edited files");
                            return Ok();
                        }
                        if (!File.Exists(p)) throw new Fail("File not found.");
                        if (new FileInfo(p).Length > 3 * 1048576) throw new Fail("File is too large to edit here — download it instead.");
                        byte[] b;
                        using (var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var ms = new MemoryStream()) { f.CopyTo(ms); b = ms.ToArray(); }
                        if (b.Take(8192).Any(x => x == 0)) throw new Fail("This looks like a binary file — download it instead.");
                        r.Send(200, "text/plain; charset=utf-8", b, null);
                        return null;
                    }

                case "/api/upload":
                    {
                        var p = Fs.Safe(r.Qs("path"));
                        if (Directory.Exists(p)) throw new Fail("A folder with that name exists.");
                        Directory.CreateDirectory(Path.GetDirectoryName(p));
                        var tmp = p + ".upload";
                        r.SaveBody(tmp, 8L << 30);
                        if (File.Exists(p)) File.Delete(p);
                        File.Move(tmp, p);
                        return Ok();
                    }

                case "/api/fileop":
                    {
                        string op = r.S("op");
                        var p = Fs.Safe(r.S("path"));
                        if (p.TrimEnd('\\').Equals(Path.GetFullPath(c.ServerDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && op != "mkdir")
                            throw new Fail("You can't do that to the server folder itself.");
                        if (op == "delete")
                        {
                            if (Directory.Exists(p)) Fs.DeleteDir(p);
                            else if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); }
                        }
                        else if (op == "mkdir" || op == "rename" || op == "newfile")
                        {
                            var nm = r.S("name").Trim();
                            if (!Regex.IsMatch(nm, @"^[^\\/:*?""<>|]{1,120}$") || nm == "." || nm == "..") throw new Fail("Invalid name.");
                            if (op == "mkdir") Directory.CreateDirectory(Fs.Safe(Fs.Rel(p) + "/" + nm));
                            else if (op == "newfile")
                            {
                                var np = Fs.Safe(Fs.Rel(p) + "/" + nm);
                                if (File.Exists(np) || Directory.Exists(np)) throw new Fail("That already exists.");
                                File.WriteAllText(np, "");
                            }
                            else
                            {
                                var dest = Path.Combine(Path.GetDirectoryName(p), nm);
                                Fs.Safe(Fs.Rel(dest));
                                if (Directory.Exists(p)) Directory.Move(p, dest); else File.Move(p, dest);
                            }
                        }
                        else throw new Fail("Unknown file action.");
                        return Ok();
                    }

                case "/api/clientpack":
                    return Addons.ClientPack();

                case "/api/version":
                    return Jsn.D("version", Program.Version, "exe", Application.ExecutablePath, "dir", c.ServerDir, "state", Server.State);

                case "/api/shutdown":
                    if (r.Method != "POST") throw new Fail("Use POST.");
                    App.Bg(_ => { Thread.Sleep(600); if (Tray.Instance != null) Tray.Instance.RequestExit(); });   // reply first, then close
                    return Ok();

                case "/api/external":
                    {
                        int pid; int.TryParse(r.S("pid"), out pid);
                        var err = Server.StopExternal(pid, r.S("action") == "kill");
                        if (err != null) throw new Fail(err);
                        return Ok();
                    }

                case "/api/tasks":
                    if (r.Method == "POST")
                    {
                        var arr = r.Json().ContainsKey("tasks") ? r.Json()["tasks"] as object[] : null;
                        if (arr == null) throw new Fail("Nothing to save.");
                        var list = arr.OfType<Dictionary<string, object>>().Select(x => Jsn.S.ConvertToType<TaskDef>(x)).ToList();
                        if (list.Count > 100) throw new Fail("Too many schedules (max 100).");
                        foreach (var t in list)
                        {
                            t.Normalize();
                            if (t.Command == "") throw new Fail("Every schedule needs a command.");
                            if (t.Every == 0 && t.At == "") throw new Fail("\"" + (t.Name != "" ? t.Name : t.Command) + "\" needs an interval or a time of day.");
                        }
                        c.Tasks = list; c.Save();
                    }
                    return Jsn.D("tasks", c.Tasks);

                case "/api/addons":
                    if (r.Method == "POST") { Addons.Op(r.S("kind"), r.S("op"), r.S("file")); Server.NeedRestart(r.S("kind")); }
                    return Addons.List(r.Method == "POST" ? r.S("kind") : r.Qs("kind"));

                case "/api/addon-upload":
                    {
                        var kind = r.Qs("kind"); var name = Path.GetFileName(r.Qs("name") ?? "");
                        var dir = Addons.Dir(kind);
                        Directory.CreateDirectory(dir);
                        var tmp = Path.Combine(dir, ".aetheris-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
                        r.SaveBody(tmp, 4L << 30);
                        Server.NeedRestart(kind);
                        try { return Addons.Import(kind, tmp, name); }
                        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                    }

                case "/api/addon-install":
                    {
                        var name = r.S("name");
                        if (!Regex.IsMatch(name, @"^[^\\/:*?""<>|]{1,150}\.(jar|zip)$", RegexOptions.IgnoreCase)) throw new Fail("Invalid file name.");
                        Net.DownloadTo(r.S("url"), Path.Combine(Addons.Dir(r.S("kind")), name));
                        Server.Log("Installed " + name + " from Modrinth.");
                        Server.NeedRestart(r.S("kind"));
                        return Ok();
                    }

                case "/api/bukkit":
                    if (r.Method == "POST")
                    {
                        var d = r.Json().ContainsKey("values") ? r.Json()["values"] as Dictionary<string, object> : null;
                        if (d == null) throw new Fail("Nothing to save.");
                        Server.NeedRestart("Paper/Spigot options");
                        Bukkit.Set(d.ToDictionary(k => k.Key, k => k.Value is bool ? ((bool)k.Value ? "true" : "false") : Convert.ToString(k.Value, CultureInfo.InvariantCulture)));
                    }
                    return Bukkit.Get();

                case "/api/icon":
                    {
                        if (!App.Configured) throw new Fail("Choose your server folder first.");
                        var p = Path.Combine(c.ServerDir, "server-icon.png");
                        if (r.Method == "DELETE") { if (File.Exists(p)) File.Delete(p); Server.NeedRestart("server icon"); return Ok(); }
                        if (r.Method == "POST")
                        {
                            var tmp = p + ".upload";
                            r.SaveBody(tmp, 1 << 20);
                            var b = File.ReadAllBytes(tmp);
                            bool png = b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G';
                            int w = png ? (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19]) : 0, h = png ? (b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]) : 0;
                            if (!png || w != 64 || h != 64) { File.Delete(tmp); throw new Fail("The server icon must be a 64×64 PNG."); }
                            if (File.Exists(p)) File.Delete(p);
                            File.Move(tmp, p);
                            Server.NeedRestart("server icon");
                            return Ok();
                        }
                        if (!File.Exists(p)) { r.Send(404, "text/plain", Encoding.UTF8.GetBytes("No icon"), null); return null; }
                        r.Send(200, "image/png", File.ReadAllBytes(p), null);
                        return null;
                    }

                case "/api/download":
                    {
                        string p;
                        if (r.Qs("backup") != "") p = Backups.PathFor(r.Qs("backup"));
                        else p = Fs.Safe(r.Qs("path"));
                        if (!File.Exists(p)) throw new Fail("File not found.");
                        r.SendFile(p, Path.GetFileName(p));
                        return null;
                    }
            }
            throw new Fail("Unknown endpoint " + r.Path);
        }
    }
}
