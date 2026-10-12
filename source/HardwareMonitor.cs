// SPDX-License-Identifier: GPL-2.0-or-later
// Advanced sensors (CPU temperature/power, motherboard temperature, CPU fan) read with the embedded
// LibreHardwareMonitorLib (MPL-2.0). Those readings need administrator rights and the PawnIO
// driver, so they run in a small elevated copy of LumeRGB.exe ("--sensor-host"):
//   * one-time setup ("--setup-sensors", one UAC prompt): installs the official PawnIO driver if
//     missing (downloaded from GitHub, Authenticode-checked) and registers a Windows scheduled task
//     that may start the host with highest privileges;
//   * afterwards the normal (non-elevated) app starts that task on demand — no prompts — and the host
//     streams readings back over a named pipe. It quits as soon as the app closes the pipe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumeRgb
{
    public static class AdvancedSensorIds
    {
        public static readonly string[] All = { "cpu_temp", "cpu_power", "board_temp", "cpu_fan" };
    }

    // ---------- shared helpers ----------
    static class SensorTask
    {
        // Root folder + explicit permission for the user: a task inside a folder created by an elevated
        // process could be invisible to the normal (non-elevated) app, which then could not start it.
        public const string Name = "LumeRGB Sensores";
        public const string OldName = "LumeRGB\\Sensores"; // 3.2–3.3
        public static string PipeName { get { return "LumeRGB.Sensors." + Process.GetCurrentProcess().SessionId; } }
        // Never waits forever: output is drained in the background and schtasks is killed after 10 s.
        static int Schtasks(string args, out string output)
        {
            var info = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var text = new StringBuilder();
            using (var p = new Process { StartInfo = info })
            {
                DataReceivedEventHandler collect = (s, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                p.OutputDataReceived += collect; p.ErrorDataReceived += collect;
                p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(10000))
                {
                    try { p.Kill(); } catch { }
                    output = "o Agendador de Tarefas do Windows não respondeu em 10 s"; return -1;
                }
                p.WaitForExit(); // flushes the asynchronous readers
                lock (text) output = text.ToString();
                return p.ExitCode;
            }
        }
        public static bool Exists() { string o; try { return Schtasks("/query /tn \"" + Name + "\"", out o) == 0; } catch { return false; } }
        public static bool Run(out string output) { try { return Schtasks("/run /tn \"" + Name + "\"", out output) == 0; } catch (Exception e) { output = e.Message; return false; } }
        public static bool Delete(out string output)
        {
            string old; try { Schtasks("/delete /tn \"" + OldName + "\" /f", out old); } catch { }
            try { return Schtasks("/delete /tn \"" + Name + "\" /f", out output) == 0; } catch (Exception e) { output = e.Message; return false; }
        }
        public static string Query()
        {
            var sb = new StringBuilder();
            foreach (var name in new[] { Name, OldName })
            {
                string o; int code;
                try { code = Schtasks("/query /tn \"" + name + "\" /v /fo list", out o); } catch (Exception e) { code = -1; o = e.Message; }
                sb.Append("Tarefa \"" + name + "\": " + (code == 0 ? "encontrada" : "não encontrada (" + code + ")") + "\r\n");
                if (code == 0)
                    foreach (var line in o.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(40)) sb.Append("    " + line + "\r\n");
            }
            return sb.ToString().TrimEnd();
        }
        public static bool Create(string exe, out string output)
        {
            string old; try { Schtasks("/delete /tn \"" + OldName + "\" /f", out old); } catch { }
            if (CreateTask(exe, true, out output)) return true;
            string first = output;
            if (CreateTask(exe, false, out output)) return true; // without the explicit permission, as in 3.2
            output = first + " / " + output; return false;
        }
        static bool CreateTask(string exe, bool explicitPermission, out string output)
        {
            var identity = WindowsIdentity.GetCurrent();
            string user = identity.Name;
            string security = explicitPermission && identity.User != null
                ? "<SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;" + identity.User.Value + ")</SecurityDescriptor>" : "";
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>" + security + "<Description>Lume RGB: sensores de CPU e placa-mãe (LibreHardwareMonitorLib). Iniciada pelo Lume somente quando necessário.</Description></RegistrationInfo>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority>" +
                "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + Escape(exe) + "</Command><Arguments>--sensor-host</Arguments><WorkingDirectory>" + Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string file = Path.Combine(Path.GetTempPath(), "LumeRGB-sensores.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try { return Schtasks("/create /tn \"" + Name + "\" /xml \"" + file + "\" /f", out output) == 0; }
            finally { try { File.Delete(file); } catch { } }
        }
        static string Escape(string s) { return System.Security.SecurityElement.Escape(s); }
    }

    static class PawnIo
    {
        public static Version InstalledVersion
        {
            get
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (var key = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\PawnIO"))
                        {
                            Version v; if (key != null && Version.TryParse(key.GetValue("DisplayVersion") as string, out v)) return v;
                        }
                    }
                    catch { }
                }
                return null;
            }
        }
        public static bool Ready { get { var v = InstalledVersion; return v != null && v >= new Version(2, 0); } }
    }

    // ---------- elevated one-time setup ----------
    public static class SensorSetup
    {
        const string InstallerUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

        public static int Run(bool remove)
        {
            try
            {
                if (remove)
                {
                    string output; SensorTask.Delete(out output); return 0;
                }
                if (!PawnIo.Ready) InstallPawnIo();
                if (!PawnIo.Ready) throw new InvalidOperationException("O driver PawnIO não foi instalado.");
                string result;
                if (!SensorTask.Create(Application.ExecutablePath, out result)) throw new InvalidOperationException("Não foi possível registrar a tarefa do Windows: " + result.Trim());
                return 0;
            }
            catch (Exception e)
            {
                AppLog.Write("Sensores avançados (configuração): " + e);
                MessageBox.Show("Não foi possível ativar os sensores avançados:\r\n\r\n" + e.Message, "Lume RGB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        static void InstallPawnIo()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            string file = Path.Combine(Path.GetTempPath(), "PawnIO_setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "LumeRGB";
                client.DownloadFile(InstallerUrl, file);
            }
            try
            {
                // We are elevated: only run an installer whose digital signature Windows accepts.
                if (!Authenticode.IsTrusted(file)) throw new InvalidOperationException("A assinatura digital do instalador do PawnIO não foi confirmada; nada foi instalado.");
                using (var setup = Process.Start(new ProcessStartInfo(file, "-install") { UseShellExecute = false }))
                    setup.WaitForExit(5 * 60 * 1000);
            }
            finally { try { File.Delete(file); } catch { } }
        }
    }

    static class Authenticode
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo { public uint Size; public string Path; public IntPtr Handle; public IntPtr KnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public uint Size; public IntPtr PolicyCallbackData, SipClientData; public uint UiChoice, RevocationChecks, UnionChoice;
            public IntPtr File; public uint StateAction; public IntPtr StateData, UrlReference; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
        public static bool IsTrusted(string path)
        {
            var info = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = path };
            IntPtr pinfo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            try
            {
                Marshal.StructureToPtr(info, pinfo, false);
                var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), UiChoice = 2 /* none */, RevocationChecks = 0, UnionChoice = 1 /* file */, File = pinfo, StateAction = 0 };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally { Marshal.DestroyStructure(pinfo, typeof(FileInfo)); Marshal.FreeHGlobal(pinfo); }
        }
    }

    // ---------- elevated host ----------
    public static class SensorHostProgram
    {
        // Separate, non-inlined: a missing/broken library dependency then fails here, inside the try.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static HardwareSource CreateSource() { return new HardwareSource(); }
        static string OneLine(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " "); }

        // What the host is doing right now, so the watchdog can tell a stuck driver call from normal work.
        static readonly object stageGate = new object();
        static string stage; static DateTime stageAt;
        static void Stage(string name) { lock (stageGate) { stage = name; stageAt = DateTime.UtcNow; } }

        public static int Run()
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", SensorTask.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(20000);
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    var reader = new StreamReader(pipe, Encoding.UTF8);
                    var writeGate = new object();
                    // The app is gone (pipe closed): leave at once, whatever LibreHardwareMonitor is doing.
                    Action<string> send = line => { lock (writeGate) { try { writer.WriteLine(line); } catch { Environment.Exit(0); } } };
                    int interval = 1000; bool board = true; var configured = new ManualResetEvent(false);
                    var listen = new Thread(() =>
                    {
                        try
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line == "quit") break;
                                int ms; if (line.StartsWith("interval=") && Int32.TryParse(line.Substring(9), out ms)) interval = Math.Max(1000, Math.Min(5000, ms));
                                if (line.StartsWith("board=")) { board = line == "board=1"; configured.Set(); }
                            }
                        }
                        catch { }
                        // The elevated helper never outlives the app, even if a driver call is stuck.
                        Environment.Exit(0);
                    }) { IsBackground = true };
                    listen.Start();
                    send("hello=" + Program.Version);
                    AppLog.Write("Sensores avançados: processo iniciado (PID " + Process.GetCurrentProcess().Id + ", PawnIO " + (PawnIo.InstalledVersion == null ? "ausente" : PawnIo.InstalledVersion.ToString()) + ").");
                    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    { send("error=O processo de sensores não recebeu permissão de administrador. Clique em Reconfigurar com uma conta de administrador."); return 4; }
                    // Every failure is reported to the app, so the page can say exactly what went wrong.
                    if (!PawnIo.Ready) { send("error=O driver PawnIO não está instalado (ou é anterior à 2.0). Clique em Reconfigurar."); return 2; }
                    configured.WaitOne(3000);
                    var watchdog = new Thread(() =>
                    {
                        while (true)
                        {
                            Thread.Sleep(1000);
                            string current; double seconds;
                            lock (stageGate) { current = stage; seconds = (DateTime.UtcNow - stageAt).TotalSeconds; }
                            // Opening can be slow on a busy PC (30–60 s was seen); only a really stuck call is ended.
                            if (current == null || seconds < (current == "read" ? 30 : 150)) continue;
                            string what = current == "cpu" ? "abrir os sensores da CPU" : current == "board" ? "abrir os sensores da placa-mãe" : "ler os sensores";
                            AppLog.Write("Sensores avançados: o LibreHardwareMonitor travou ao " + what + " (" + (int)seconds + " s). Processo encerrado.");
                            // Never wait on a stuck write here: tell the app if possible, then leave.
                            if (Monitor.TryEnter(writeGate, 1000)) { try { writer.WriteLine("hang=" + current); } catch { } }
                            Environment.Exit(5);
                        }
                    }) { IsBackground = true };
                    watchdog.Start();
                    HardwareSource source;
                    try { Stage("cpu"); send("stage=cpu"); source = CreateSource(); }
                    catch (Exception e) { AppLog.Write("Sensores avançados (abrir): " + e); send("error=" + OneLine("O LibreHardwareMonitor não abriu: " + e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message)); return 3; }
                    try
                    {
                        Stage(null);
                        send("info=" + OneLine(source.Describe()));
                        bool first = true;
                        while (pipe.IsConnected)
                        {
                            var watch = Stopwatch.StartNew();
                            Stage("read"); string line = source.Read(); Stage(null);
                            send(line);
                            if (first && line.IndexOf("cpu_temp=", StringComparison.Ordinal) < 0)
                                send("warn=" + OneLine("O LibreHardwareMonitor abriu, mas não encontrou a temperatura da CPU (" + source.Describe() + "). O driver PawnIO pode estar bloqueado (Integridade de Memória/antivírus) ou exigir reiniciar o PC."));
                            if (first && board)
                            {
                                // CPU readings are already flowing; now add the motherboard (it can take a while to open).
                                Stage("board"); send("stage=board");
                                try { source.EnableBoard(); send("info=" + OneLine(source.Describe())); }
                                catch (Exception e) { AppLog.Write("Sensores avançados (placa-mãe): " + e); send("info=" + OneLine("Placa-mãe indisponível: " + e.GetBaseException().Message)); }
                                Stage(null);
                            }
                            first = false;
                            int rest = interval - (int)watch.ElapsedMilliseconds;
                            if (rest > 0) Thread.Sleep(rest);
                        }
                    }
                    finally { Stage(null); source.Dispose(); }
                }
                return 0;
            }
            catch (IOException) { return 1; } // the app closed the pipe: nothing to report
            catch (Exception e) { AppLog.Write("Sensores avançados (processo): " + e); return 1; }
        }
    }

    // Kept in its own class so the main (non-elevated) process never loads LibreHardwareMonitorLib.
    sealed class HardwareSource : IDisposable
    {
        readonly LibreHardwareMonitor.Hardware.Computer computer;
        // CPU first; the motherboard (Super I/O chip) is added separately so a problem there cannot take the CPU readings down.
        public HardwareSource()
        {
            computer = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true };
            computer.Open();
        }
        public void EnableBoard() { computer.IsMotherboardEnabled = true; }
        static IEnumerable<LibreHardwareMonitor.Hardware.IHardware> Flatten(IEnumerable<LibreHardwareMonitor.Hardware.IHardware> list)
        {
            foreach (var h in list) { yield return h; foreach (var s in Flatten(h.SubHardware)) yield return s; }
        }
        public string Describe()
        {
            var all = Flatten(computer.Hardware).ToList();
            if (all.Count == 0) return "nenhum hardware detectado";
            return String.Join("; ", all.Select(h => h.HardwareType + ": " + h.Name + " (" + h.Sensors.Length + " sensores)").ToArray());
        }
        public string Read()
        {
            foreach (var h in Flatten(computer.Hardware)) { try { h.Update(); } catch { } }
            var all = Flatten(computer.Hardware).ToList();
            var cpu = all.Where(h => h.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu).SelectMany(h => h.Sensors).ToList();
            var board = all.Where(h => h.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.SuperIO || h.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Motherboard).SelectMany(h => h.Sensors).ToList();
            var values = new List<string>();
            Add(values, "cpu_temp", Pick(cpu, LibreHardwareMonitor.Hardware.SensorType.Temperature, "Tctl", "Package", "Tdie", "Core (Tctl"));
            Add(values, "cpu_power", Pick(cpu, LibreHardwareMonitor.Hardware.SensorType.Power, "Package", "CPU Package"));
            Add(values, "board_temp", Pick(board, LibreHardwareMonitor.Hardware.SensorType.Temperature, "System", "Motherboard", "Mainboard", "PCH"));
            Add(values, "cpu_fan", Pick(board, LibreHardwareMonitor.Hardware.SensorType.Fan, "CPU Fan", "CPU", "Pump"));
            return String.Join(";", values.ToArray());
        }
        static float? Pick(List<LibreHardwareMonitor.Hardware.ISensor> sensors, LibreHardwareMonitor.Hardware.SensorType type, params string[] preferred)
        {
            var list = sensors.Where(s => s.SensorType == type && s.Value.HasValue && !float.IsNaN(s.Value.Value)).ToList();
            foreach (var name in preferred)
            {
                var hit = list.FirstOrDefault(s => s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit.Value;
            }
            if (type == LibreHardwareMonitor.Hardware.SensorType.Fan) return list.Where(s => s.Value > 0).Select(s => s.Value).FirstOrDefault();
            return list.Count > 0 ? list.Max(s => s.Value) : null;
        }
        static void Add(List<string> values, string id, float? value)
        {
            if (value.HasValue) values.Add(id + "=" + value.Value.ToString("0.0", CultureInfo.InvariantCulture));
        }
        public void Dispose() { try { computer.Close(); } catch { } }
    }

    // ---------- main-process client ----------
    // Everything slow (Task Scheduler, starting the elevated helper, the pipe) runs on this class's own thread.
    // The window and the sensor loop only read cached fields, so the advanced sensors can never freeze the app.
    public sealed class AdvancedSensors : IAdvancedSource, IDisposable
    {
        readonly object data = new object(); // guards values/lastData only; never held during I/O
        readonly Dictionary<string, double> values = new Dictionary<string, double>();
        DateTime lastData = DateTime.MinValue;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool disposed, wanted, paused, recheck = true, configured, driverReady;
        volatile int interval = 1000;
        volatile string state = "off", detail;
        DateTime wantedAt = DateTime.MinValue;
        // Controller-thread state
        NamedPipeServerStream pipe; StreamWriter writer; DateTime startedAt, retryAt = DateTime.MinValue; int sentInterval;
        volatile string hostStage, hostError, hostWarning; volatile bool hostRunning, hostGone;
        bool skipBoard;
        // Test hooks (hardware-free tests replace the Task Scheduler and the helper process).
        internal delegate bool HelperLauncher(out string output);
        internal HelperLauncher Launch = (out string output) => SensorTask.Run(out output);
        internal Func<bool> TaskExists = SensorTask.Exists;
        internal Func<bool> DriverCheck = () => PawnIo.Ready;
        internal string PipeName = SensorTask.PipeName;
        internal string Marker = Path.Combine(Path.GetDirectoryName(AppLog.Path), "sensores-avancados.ativo");
        internal double ConnectSeconds = 15, OpenSeconds = 180, SilentSeconds = 15, ProblemRetrySeconds = 120;

        public string State { get { return state; } }
        public string Detail { get { return detail; } }
        public bool Configured { get { return configured; } }
        public bool DriverReady { get { return driverReady; } }
        public bool Paused { get { return paused; } }
        // True when the previous session ended (crash, freeze, forced close) while the helper was running.
        public bool EndedBadlyLastTime { get; private set; }
        public event EventHandler Changed;

        public AdvancedSensors(bool paused) : this(paused, null) { }
        internal AdvancedSensors(bool paused, string marker)
        {
            this.paused = paused;
            if (marker != null) Marker = marker;
            try { if (File.Exists(Marker)) { EndedBadlyLastTime = true; File.Delete(Marker); } } catch { }
            if (paused) state = "paused";
            thread = new Thread(Loop) { IsBackground = true, Name = "Lume advanced sensors" };
            if (marker == null) thread.Start(); // tests set their hooks first, then call Start()
        }
        internal void Start() { thread.Start(); }
        void Set(string newState, string newDetail)
        {
            bool changed = state != newState || detail != newDetail;
            state = newState; detail = newDetail;
            if (!changed) return;
            if (newState == "error" || newState == "setup" || newState == "running" || newState == "paused")
                AppLog.Write("Sensores avançados: " + newState + (String.IsNullOrEmpty(newDetail) ? "" : " — " + newDetail));
            var h = Changed; if (h != null) try { h(this, EventArgs.Empty); } catch { }
        }
        public bool TryGet(string id, out double value)
        {
            value = 0;
            if (state != "running") return false;
            lock (data) { if ((DateTime.UtcNow - lastData).TotalSeconds > 5) return false; return values.TryGetValue(id, out value); }
        }
        // Called by the sensor loop whenever readings are wanted: only records the wish, never blocks.
        public void Ensure(int intervalMs)
        {
            interval = intervalMs; wantedAt = DateTime.UtcNow;
            if (!wanted) { wanted = true; wake.Set(); }
        }
        public void Stop() { if (wanted) { wanted = false; wake.Set(); } }
        volatile string pauseReason;
        public void SetPaused(bool value, string reason) { pauseReason = value ? reason : null; paused = value; retryAt = DateTime.MinValue; wake.Set(); }
        public void ResetConfiguredCache() { recheck = true; retryAt = DateTime.MinValue; wake.Set(); }

        void Loop()
        {
            while (!disposed)
            {
                wake.WaitOne(500);
                if (disposed) break;
                try { Tick(); } catch (Exception e) { AppLog.Write("Sensores avançados (controle): " + e); StopHost(); Set("error", e.Message); retryAt = DateTime.UtcNow.AddMinutes(1); }
            }
            StopHost();
        }
        void Tick()
        {
            if (recheck) { recheck = false; driverReady = DriverCheck(); configured = TaskExists(); Raise(); }
            // Nobody asked for readings for a while (page closed, no reaction): let the helper go.
            // Opening costs up to a minute on some PCs, so the helper stays around for 5 minutes after the last use.
            if (wanted && (DateTime.UtcNow - wantedAt).TotalSeconds > 300) wanted = false;
            if (paused) { if (pipe != null) StopHost(); Set("paused", pauseReason); return; }
            if (!wanted) { if (pipe != null) StopHost(); if (state == "running" || state == "starting" || state == "paused") Set("off", null); return; }
            if (pipe != null) { Watch(); return; }
            if (DateTime.UtcNow < retryAt) return;
            if (!configured) { Set("setup", driverReady ? "Falta autorizar o processo de sensores (a autorização mudou na 3.4: clique em Ativar de novo)." : "Falta instalar o driver PawnIO."); return; }
            StartHost();
        }
        void Raise() { var h = Changed; if (h != null) try { h(this, EventArgs.Empty); } catch { } }

        void StartHost()
        {
            Set("starting", "Iniciando o processo de sensores…");
            hostStage = null; hostError = null; hostWarning = null; hostRunning = false; hostGone = false;
            try { File.WriteAllText(Marker, DateTime.Now.ToString("s")); } catch { }
            var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
            pipe = server; startedAt = DateTime.UtcNow;
            string output;
            if (!Launch(out output))
            {
                StopHost(); recheck = true; retryAt = DateTime.UtcNow.AddMinutes(1);
                Set("error", "A tarefa de sensores não iniciou: " + output.Trim()); return;
            }
            var wait = server.BeginWaitForConnection(null, null);
            if (!wait.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(ConnectSeconds)))
            {
                StopHost(); retryAt = DateTime.UtcNow.AddMinutes(1);
                Set("error", "O processo de sensores não respondeu. Se você moveu a pasta do Lume, ative os sensores de novo."); return;
            }
            server.EndWaitForConnection(wait);
            writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Send("board=" + (skipBoard ? "0" : "1"));
            sentInterval = interval; Send("interval=" + Math.Max(1000, interval));
            new Thread(() => Read(server)) { IsBackground = true, Name = "Lume advanced sensors pipe" }.Start();
        }
        void Watch()
        {
            if (hostGone)
            {
                string stage = hostStage;
                StopHost();
                if (hostError != null) { retryAt = DateTime.UtcNow.AddSeconds(ProblemRetrySeconds); Set("error", hostError); return; }
                if (stage == "board" && !skipBoard)
                {
                    // The motherboard chip made LibreHardwareMonitor hang: keep going with the CPU only.
                    skipBoard = true; retryAt = DateTime.UtcNow.AddSeconds(5);
                    Set("error", "A leitura da placa-mãe travou; tentando de novo só com a CPU."); return;
                }
                retryAt = DateTime.UtcNow.AddSeconds(stage != null ? ProblemRetrySeconds * 2.5 : ProblemRetrySeconds / 2);
                Set("error", stage == "cpu" ? "O LibreHardwareMonitor travou ao abrir os sensores da CPU e foi encerrado (o Lume continua normal)."
                    : stage == "read" ? "O LibreHardwareMonitor travou durante uma leitura e foi encerrado (o Lume continua normal)."
                    : "O processo de sensores foi encerrado."); return;
            }
            double since; lock (data) since = (DateTime.UtcNow - (lastData == DateTime.MinValue ? startedAt : lastData)).TotalSeconds;
            // While the helper is opening something (CPU at start, motherboard later) it may be silent for a while.
            if (since > (hostRunning && hostStage == null ? SilentSeconds : OpenSeconds))
            {
                StopHost(); retryAt = DateTime.UtcNow.AddSeconds(ProblemRetrySeconds);
                Set("error", hostRunning ? "O processo de sensores parou de responder." : "O LibreHardwareMonitor demorou demais para abrir. Veja o registro de erros."); return;
            }
            if (interval != sentInterval) { sentInterval = interval; Send("interval=" + Math.Max(1000, interval)); }
            if (hostRunning) Set("running", hostWarning);
            else Set("starting", hostStage == "cpu" ? "Abrindo os sensores da CPU… (pode levar até 1 minuto)" : "Processo de sensores iniciado…");
        }
        // Reader thread: parses lines only; all decisions are taken by the controller thread.
        void Read(NamedPipeServerStream server)
        {
            try
            {
                var input = new StreamReader(server, Encoding.UTF8); string line;
                while ((line = input.ReadLine()) != null)
                {
                    if (line.StartsWith("hello=")) { lock (data) lastData = DateTime.UtcNow; continue; }
                    if (line.StartsWith("stage=")) { hostStage = line.Substring(6); lock (data) lastData = DateTime.UtcNow; wake.Set(); continue; }
                    if (line.StartsWith("hang=")) { hostStage = line.Substring(5); continue; }
                    if (line.StartsWith("error=")) { hostError = line.Substring(6); AppLog.Write("Sensores avançados: " + hostError); continue; }
                    if (line.StartsWith("warn=")) { hostWarning = line.Substring(5); AppLog.Write("Sensores avançados: " + hostWarning); continue; }
                    if (line.StartsWith("info=")) { AppLog.Write("Sensores avançados: " + line.Substring(5)); continue; }
                    lock (data)
                    {
                        values.Clear();
                        foreach (var pair in line.Split(';'))
                        {
                            int eq = pair.IndexOf('='); double v;
                            if (eq > 0 && Double.TryParse(pair.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) values[pair.Substring(0, eq)] = v;
                        }
                        lastData = DateTime.UtcNow;
                    }
                    hostStage = null;
                    if (!hostRunning) { hostRunning = true; wake.Set(); }
                }
            }
            catch (Exception) { }
            hostGone = true; wake.Set();
        }
        void Send(string line)
        {
            var w = writer; if (w == null) return;
            // Bounded: a helper that stopped reading must not block this thread.
            try { var t = w.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), 0, Encoding.UTF8.GetByteCount(line + "\n")); t.Wait(2000); } catch { }
        }
        void StopHost()
        {
            var server = pipe; pipe = null;
            if (server != null) { Send("quit"); try { server.Dispose(); } catch { } }
            writer = null; hostRunning = false;
            lock (data) { values.Clear(); lastData = DateTime.MinValue; }
            try { File.Delete(Marker); } catch { }
        }

        public string Diagnostics()
        {
            var lines = new List<string>();
            lines.Add("Estado: " + State + (Detail == null ? "" : " — " + Detail) + (paused ? " (pausado)" : "") + (EndedBadlyLastTime ? " | a sessão anterior terminou com os sensores ativos" : ""));
            lines.Add("PawnIO: " + (PawnIo.InstalledVersion == null ? "não instalado" : PawnIo.InstalledVersion.ToString()));
            lines.Add("Este processo é administrador: " + new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
            lines.Add("Executável: " + Application.ExecutablePath);
            lock (data) lines.Add("Leituras: " + (values.Count == 0 ? "nenhuma" : String.Join(", ", values.Select(v => v.Key + "=" + v.Value.ToString("0.0", CultureInfo.InvariantCulture)).ToArray())));
            lines.Add(SensorTask.Query());
            return String.Join("\r\n", lines.ToArray());
        }

        // One UAC prompt: installs PawnIO if needed and registers the scheduled task.
        public static bool RunSetup(bool remove)
        {
            var info = new ProcessStartInfo(Application.ExecutablePath, remove ? "--remove-sensors" : "--setup-sensors") { UseShellExecute = true, Verb = "runas" };
            try
            {
                using (var p = Process.Start(info)) { p.WaitForExit(10 * 60 * 1000); return p.HasExited && p.ExitCode == 0; }
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC prompt cancelled
        }
        public void Dispose()
        {
            disposed = true; wake.Set();
            try { if (!thread.Join(3000)) StopHost(); } catch { }
        }
    }
}
