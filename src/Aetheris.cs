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
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Aetheris
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool trayOnly = args.Any(a => a == "--tray");
            bool created;
            var mutex = new Mutex(true, "Local\\AetherisPanel", out created);
            App.Cfg = Config.Load();
            if (!created) { App.OpenUi(); return; }

            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Http.Start(App.Cfg.Port); }
            catch (Exception ex)
            {
                MessageBox.Show("Aetheris could not open panel port " + App.Cfg.Port + ".\n" + ex.Message +
                    "\n\nEdit \"Port\" in aetheris.json and try again.", "Aetheris", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Run(new Tray(!trayOnly && App.Cfg.OpenOnLaunch));
            GC.KeepAlive(mutex);
        }
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

        public Config()
        {
            ServerDir = ""; Jar = ""; JavaPath = "java"; MinRam = 1024; MaxRam = 2048; JvmArgs = "";
            AikarFlags = true; AutoRestart = true; PlayitPath = ""; Address = ""; Port = 8165;
            AppWindow = true; OpenOnLaunch = true; BackupKeep = 10; DailyRestart = "";
        }

        static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aetheris.json"); } }

        public static Config Load()
        {
            Config c = null;
            try { if (File.Exists(FilePath)) c = Jsn.S.Deserialize<Config>(File.ReadAllText(FilePath)); } catch { }
            if (c == null)
            {
                c = new Config();
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
            JvmArgs = (JvmArgs ?? "").Trim(); PlayitPath = (PlayitPath ?? "").Trim().Trim('"');
            Address = (Address ?? "").Trim(); DailyRestart = (DailyRestart ?? "").Trim();
            if (MinRam < 256) MinRam = 256;
            if (MaxRam < MinRam) MaxRam = MinRam;
            if (Port < 1024 || Port > 65535) Port = 8165;
            if (BackupKeep < 1) BackupKeep = 1;
            if (BackupHours < 0) BackupHours = 0;
            if (BackupHours > 168) BackupHours = 168;
            if (DailyRestart != "" && !Regex.IsMatch(DailyRestart, @"^([01]\d|2[0-3]):[0-5]\d$")) DailyRestart = "";
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
            if (Cfg.AutoStart) ThreadPool.QueueUserWorkItem(_ => Server.Start());
        }

        // Called on every panel request; once the panel goes quiet, trim our RAM back to ~2 MB.
        public static void Touch() { if (trimTimer != null) trimTimer.Change(10000, Timeout.Infinite); }

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
            if (did) Touch();
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

        public Tray(bool open)
        {
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
            Refresh();
            App.Init();
            if (open) App.OpenUi();
        }

        static void Bg(Func<string> f)
        {
            ThreadPool.QueueUserWorkItem(_ => { var err = f(); if (err != null) Server.Log(err); });
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

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            if (Server.Running)
            {
                ni.ShowBalloonTip(4000, "Aetheris", "Saving and stopping the server before exiting…", ToolTipIcon.Info);
                ThreadPool.QueueUserWorkItem(_ => { Server.StopAndWait(90000); App.Ui.BeginInvoke(new Action(Quit)); });
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

        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[()][A-Z0-9]|\r");
        static readonly Regex JoinRe = new Regex(@"\]: ([A-Za-z0-9_.]{1,17}) joined the game");
        static readonly Regex LeftRe = new Regex(@"\]: ([A-Za-z0-9_.]{1,17}) left the game");
        static readonly Regex ListRe = new Regex(@"There are (\d+) (?:of a max of|/) ?(\d+) players online:?(.*)$");
        static readonly Regex VerRe = new Regex(@"Starting minecraft server version (\S+)");
        static readonly Regex DoneRe = new Regex(@"\]: Done \(|For help, type ""help""");

        public static bool Running { get { lock (L) return proc != null; } }
        static void Fire() { var c = Changed; if (c != null) try { c(); } catch { } }

        public static string[] PlayerList() { lock (L) return players.ToArray(); }

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
                if (c.Jar == "" || !File.Exists(Path.Combine(c.ServerDir, c.Jar))) return "No server jar selected. Pick or install one in Software.";
                if (!Eula()) return "You need to accept the Minecraft EULA before starting.";

                var a = new StringBuilder();
                a.Append("-Xms").Append(c.MinRam).Append("M -Xmx").Append(c.MaxRam).Append("M ");
                if (c.AikarFlags) a.Append(AikarFlags(c.MaxRam)).Append(' ');
                a.Append("-Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 -Dfile.encoding=UTF-8 -Dterminal.jline=false -Dterminal.ansi=true ");
                if (c.JvmArgs != "") a.Append(c.JvmArgs).Append(' ');
                a.Append("-jar \"").Append(c.Jar).Append("\" nogui");

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
                p.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                p.Exited += OnExit;
                Log("Starting " + c.Jar + " (" + c.MinRam + "–" + c.MaxRam + " MB RAM)…");
                try { p.Start(); }
                catch (Exception ex)
                {
                    Log("Could not launch Java: " + ex.Message + ". Check the Java path in Settings.");
                    return "Could not launch Java (" + ex.Message + "). Check the Java path in Settings.";
                }
                stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                proc = p; State = "starting"; Started = DateTime.Now; players.Clear();
                stopRequested = false; restartPending = false;
                lastCpu = TimeSpan.Zero; lastWall = DateTime.MinValue;
            }
            StartPlayit();
            Fire();
            return null;
        }

        static void OnLine(string raw)
        {
            string line = Ansi.Replace(raw, "");
            bool changed = false;
            lock (L)
            {
                AddLine(line);
                Match m;
                if (State == "starting" && DoneRe.IsMatch(line)) { State = "online"; changed = true; }
                if ((m = VerRe.Match(line)).Success) Version = m.Groups[1].Value;
                if ((m = JoinRe.Match(line)).Success)
                {
                    var n = m.Groups[1].Value;
                    if (!players.Contains(n)) players.Add(n);
                    changed = true;
                }
                else if ((m = LeftRe.Match(line)).Success) { players.Remove(m.Groups[1].Value); changed = true; }
                else if ((m = ListRe.Match(line)).Success)
                {
                    players.Clear();
                    foreach (var n in m.Groups[3].Value.Split(','))
                    {
                        var t = n.Trim();
                        if (Regex.IsMatch(t, @"^[A-Za-z0-9_.]{1,17}$")) players.Add(t);
                    }
                    changed = true;
                }
            }
            if (changed) Fire();
        }

        static void OnExit(object sender, EventArgs e)
        {
            var p = (Process)sender;
            bool restart = false, crashRestart = false;
            // drain remaining output first (outside the lock — output handlers need it)
            try { p.WaitForExit(); } catch { }
            lock (L)
            {
                if (p != proc) return;
                int code = 0;
                try { code = p.ExitCode; } catch { }
                proc = null; stdin = null; players.Clear(); State = "offline";
                bool crashed = !stopRequested && !restartPending;
                Log("Server stopped" + (code != 0 ? " (exit code " + code + ")" : "") + ".");
                if (restartPending) restart = true;
                else if (crashed && App.Cfg.AutoRestart && (DateTime.Now - Started).TotalSeconds > 45)
                {
                    crashes.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 10);
                    crashes.Add(DateTime.Now);
                    if (crashes.Count <= 3) { restart = crashRestart = true; Log("The server crashed — restarting in 5 seconds (auto-restart is on)."); }
                    else Log("The server crashed 3 times in 10 minutes — not restarting automatically.");
                }
                restartPending = false;
                try { p.Dispose(); } catch { }
            }
            if (!restart) StopPlayit();
            Fire();
            if (restart)
            {
                int delay = crashRestart ? 5000 : 1500;
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(delay); var err = Start(); if (err != null) Log(err); });
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
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!p.WaitForExit(90000)) { Log("Server did not stop within 90 seconds — force killing it."); p.Kill(); }
                }
                catch { }
            });
            return null;
        }

        public static void StopAndWait(int ms)
        {
            Process p;
            lock (L) p = proc;
            if (p == null) return;
            Stop(false);
            try { if (!p.WaitForExit(ms)) p.Kill(); } catch { }
            Thread.Sleep(300);
        }

        public static string Kill()
        {
            lock (L)
            {
                if (proc == null) return "The server is not running.";
                stopRequested = true; restartPending = false;
                Log("Force killing server process (unsaved progress may be lost).");
                try { proc.Kill(); } catch (Exception ex) { return ex.Message; }
            }
            return null;
        }

        public static void Stats(out long ramMb, out double cpu)
        {
            ramMb = 0; cpu = 0;
            Process p;
            lock (L) p = proc;
            if (p == null) return;
            try
            {
                p.Refresh();
                ramMb = p.WorkingSet64 / 1048576;
                var now = DateTime.UtcNow; var t = p.TotalProcessorTime;
                if (lastWall != DateTime.MinValue)
                {
                    double wall = (now - lastWall).TotalMilliseconds;
                    if (wall > 0) cpu = Math.Max(0, Math.Min(100, (t - lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100));
                }
                lastCpu = t; lastWall = now;
            }
            catch { }
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

        static void StartPlayit()
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

    // ------------------------------------------------------------------ downloads
    static class Net
    {
        static readonly string[] Hosts = {
            "piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com",
            "api.papermc.io", "fill.papermc.io", "fill-data.papermc.io", "api.purpurmc.org", "meta.fabricmc.net" };

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
                throw new Fail("Downloads are only allowed from official Mojang / PaperMC / Purpur / Fabric servers.");
            return u;
        }

        public static string Fetch(string url)
        {
            var u = Check(url);
            try { using (var wc = Client()) return wc.DownloadString(u); }
            catch (WebException ex) { throw new Fail("Request failed: " + ex.Message); }
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
            Port = port;
            new Thread(Loop) { IsBackground = true, Name = "http" }.Start();
        }

        static void Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(Handle, c);
            }
        }

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
                r.Send(200, "text/html; charset=utf-8", Page(), "Content-Security-Policy: default-src 'self'; img-src 'self' data: https://mc-heads.net; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'\r\n");
                return;
            }
            if (!r.Path.StartsWith("/api/")) { r.Send(404, "text/plain", Encoding.UTF8.GetBytes("Not found"), null); return; }
            // custom header forces a CORS preflight, so other websites can't drive the panel
            if (r.Path != "/api/download" && !r.H.ContainsKey("X-Aetheris")) { r.Send(403, "text/plain", Encoding.UTF8.GetBytes("Forbidden"), null); return; }

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

        static object State()
        {
            long ram; double cpu;
            Server.Stats(out ram, out cpu);
            var p = Props.Read();
            string v;
            var c = App.Cfg;
            return Jsn.D(
                "state", Server.State, "players", Server.PlayerList(),
                "max", p.TryGetValue("max-players", out v) ? v : "20",
                "motd", p.TryGetValue("motd", out v) ? v : "A Minecraft Server",
                "port", p.TryGetValue("server-port", out v) ? v : "25565",
                "version", Server.Version,
                "uptime", Server.Running ? (long)(DateTime.Now - Server.Started).TotalSeconds : 0L,
                "ram", ram, "cpu", Math.Round(cpu, 1), "xmx", c.MaxRam,
                "address", c.Address, "jar", c.Jar, "dir", c.ServerDir,
                "eula", Server.Eula(), "configured", App.Configured,
                "playit", c.PlayitPath != "" || c.StartPlayit ? (object)Server.PlayitRunning() : null,
                "snake", Server.SnakeRules(), "sysram", App.SysRamMb());
        }

        static object SaveConfig(Req r)
        {
            var j = r.Json();
            var cd = j.ContainsKey("config") ? j["config"] as Dictionary<string, object> : null;
            if (cd == null) throw new Fail("Missing settings.");
            var n = Jsn.S.ConvertToType<Config>(cd);
            n.Normalize();
            if (n.ServerDir != "" && !Directory.Exists(n.ServerDir)) throw new Fail("That server folder doesn't exist.");
            if (n.ServerDir != "" && (n.Jar == "" || !File.Exists(Path.Combine(n.ServerDir, n.Jar)))) n.Jar = Config.FindJar(n.ServerDir);
            if (n.PlayitPath != "" && !File.Exists(n.PlayitPath)) throw new Fail("playit.gg program not found at that path.");
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
                case "/api/state": return State();

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
                        return Jsn.D("jars", list, "current", c.Jar);
                    }

                case "/api/usejar":
                    {
                        var name = r.S("name");
                        if (!App.Configured || !Regex.IsMatch(name, @"^[^\\/:*?""<>|]+\.jar$") || !File.Exists(Path.Combine(c.ServerDir, name))) throw new Fail("Jar not found.");
                        c.Jar = name; c.Save();
                        return Ok();
                    }

                case "/api/fetch":
                    r.Send(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Net.Fetch(r.Qs("url"))), null);
                    return null;

                case "/api/install":
                    return Jsn.D("ok", true, "name", Net.Install(r.S("url"), r.S("name")));

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
                        using (var f = new FileStream(tmp, FileMode.Create))
                        {
                            var buf = new byte[65536]; long left = r.Len;
                            while (left > 0)
                            {
                                int n = r.Body.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                if (n <= 0) break;
                                f.Write(buf, 0, n); left -= n;
                            }
                            if (left > 0) { f.Close(); File.Delete(tmp); throw new Fail("Upload interrupted."); }
                        }
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
