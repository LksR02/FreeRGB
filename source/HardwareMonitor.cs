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
        public static bool PointsTo(string executable)
        {
            try
            {
                string xml;
                if (Schtasks("/query /tn \"" + Name + "\" /xml", out xml) != 0) return false;
                var document = new System.Xml.XmlDocument { XmlResolver = null };
                document.LoadXml(xml);
                var command = document.SelectSingleNode("//*[local-name()='Actions']/*[local-name()='Exec']/*[local-name()='Command']");
                return command != null && String.Equals(Path.GetFullPath(command.InnerText.Trim().Trim('"')), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
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
        // Only the elevated broker owns the app pipe. Driver calls run in separately
        // supervised children, so a stuck SuperIO cannot take CPU readings down.
        public static int Run()
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", SensorTask.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                using (var stop = new ManualResetEvent(false))
                {
                    pipe.Connect(20000);
                    var writeGate = new object();
                    Action signalStop = () => { try { stop.Set(); } catch (ObjectDisposedException) { } };
                    Action<string> send = line =>
                    {
                        lock (writeGate)
                        {
                            try
                            {
                                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                                if (!pipe.WriteAsync(bytes, 0, bytes.Length).Wait(2000)) signalStop();
                            }
                            catch { signalStop(); }
                        }
                    };
                    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    { send("error=O processo de sensores não recebeu permissão de administrador. Reconfigure os sensores."); return 4; }
                    if (!PawnIo.Ready) { send("error=O driver PawnIO não está instalado."); return 2; }
                    int interval = 1000; bool board = true;
                    var configured = new ManualResetEvent(false);
                    new Thread(() =>
                    {
                        try
                        {
                            var reader = new StreamReader(pipe, Encoding.UTF8); string line;
                            while ((line = reader.ReadLine()) != null && line != "quit")
                            {
                                int ms; if (line.StartsWith("interval=") && Int32.TryParse(line.Substring(9), out ms))
                                    Interlocked.Exchange(ref interval, Math.Max(1000, Math.Min(5000, ms)));
                                if (line.StartsWith("board=")) { board = line == "board=1"; configured.Set(); }
                            }
                        }
                        catch { }
                        finally { signalStop(); }
                    }) { IsBackground = true }.Start();
                    send("hello=" + Program.Version);
                    AppLog.Write("Sensores avançados: broker iniciado (PID " + Process.GetCurrentProcess().Id + ").");
                    configured.WaitOne(3000);
                    using (var cpu = new SensorWorker("cpu", send))
                    using (var mainboard = new SensorWorker("board", send))
                    {
                        string warning = null; int ticks = 0;
                        while (!stop.WaitOne(500))
                        {
                            if (ticks == 0) AppLog.Write("Sensores avançados: iniciando leitor da CPU.");
                            cpu.Tick(interval);
                            if (ticks == 0) AppLog.Write("Sensores avançados: iniciando leitor da placa.");
                            if (board) mainboard.Tick(interval);
                            if (ticks == 0) AppLog.Write("Sensores avançados: leitores iniciados, enviando heartbeat.");
                            send("alive=broker");
                            string next = String.Join(" ", new[] { cpu.Status, board ? mainboard.Status : null }.Where(s => !String.IsNullOrEmpty(s)));
                            if (next != warning) { warning = next; send("warn=" + warning); }
                            ticks++;
                        }
                    }
                    configured.Dispose();
                }
                return 0;
            }
            catch (IOException) { return 0; }
            catch (Exception e) { AppLog.Write("Sensores avançados (broker): " + e); return 1; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static HardwareSource Open(string part) { return new HardwareSource(part); }

        public static int RunWorker(string part, int parentId)
        {
            if ((part != "cpu" && part != "board") || parentId <= 0) return 2;
            // Hold a process handle, so a reused PID cannot keep an orphan alive.
            Process parent;
            try { parent = Process.GetProcessById(parentId); var unused = parent.Handle; }
            catch { return 2; }
            new Thread(() => { try { parent.WaitForExit(); } catch { } Environment.Exit(0); }) { IsBackground = true }.Start();
            try
            {
                AppLog.Write("Sensores avançados (" + part + "): abrindo biblioteca, PID " + Process.GetCurrentProcess().Id + ".");
                Console.WriteLine("stage=open");
                using (var source = Open(part))
                {
                    AppLog.Write("Sensores avançados (" + part + "): " + source.Describe());
                    Console.WriteLine("info=" + source.Describe());
                    int reads = 0;
                    while (true)
                    {
                        Console.WriteLine("stage=read");
                        string reading = source.Read();
                        Console.WriteLine("sample=" + part + "|" + DateTime.UtcNow.Ticks + "|" + reading);
                        if (++reads <= 3 || reads % 300 == 0) Console.WriteLine("info=Tempo de leitura: " + source.Timings());
                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception e)
            {
                AppLog.Write("Sensores avançados (" + part + "): " + e);
                Console.WriteLine("error=" + part + ": " + e.GetBaseException().Message.Replace("\r", " ").Replace("\n", " "));
                return 3;
            }
        }
    }

    // The broker never calls into the driver itself, including during shutdown.
    sealed class SensorWorker : IDisposable
    {
        readonly string part; readonly Action<string> send; readonly object gate = new object();
        Process process; DateTime progress, retryAt; string stage = "open"; bool sampled;
        public string Error { get; private set; }
        public string Status { get { lock (gate) return Error ?? (!sampled ? "Aguardando sensores da " + (part == "cpu" ? "CPU." : "placa-mãe.") : null); } }
        internal double OpenTimeout = 90, ReadTimeout = 30, RetrySeconds = 120;
        public SensorWorker(string part, Action<string> send) { this.part = part; this.send = send; }
        public void Tick(int interval)
        {
            lock (gate)
            {
                if (process == null)
                {
                    if (DateTime.UtcNow < retryAt) return;
                    Start(); return;
                }
                if (process.HasExited)
                { Fail(Error ?? (part + ": processo de leitura encerrado (" + process.ExitCode + ").")); return; }
                if ((DateTime.UtcNow - progress).TotalSeconds > (stage == "open" ? OpenTimeout : ReadTimeout))
                { Fail(part + ": leitura bloqueada em " + stage + ". Nova tentativa em " + (int)RetrySeconds + " s."); }
            }
        }
        void Start()
        {
            stage = "open"; sampled = false; progress = DateTime.UtcNow;
            string executable = Application.ExecutablePath;
            string arguments = "--sensor-worker " + part + " " + Process.GetCurrentProcess().Id;
            if (Type.GetType("Mono.Runtime") != null)
            { arguments = "\"" + executable + "\" " + arguments; executable = Process.GetCurrentProcess().MainModule.FileName; }
            var info = new ProcessStartInfo(executable, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var child = new Process { StartInfo = info };
            process = child;
            child.OutputDataReceived += (s, e) =>
            {
                lock (gate)
                {
                    if (process != child || e.Data == null) return;
                    string line = e.Data;
                    if (line.StartsWith("stage="))
                    {
                        stage = line.Substring(6);
                        // Repeated stage notifications must not reset a stuck operation's timeout.
                        if (!sampled || stage == "read") progress = DateTime.UtcNow;
                    }
                    else if (line.StartsWith("sample="))
                    { sampled = true; progress = DateTime.UtcNow; stage = "idle"; Error = line.EndsWith("|") ? part + ": nenhum sensor disponível." : null; send(line); }
                    else if (line.StartsWith("error=")) Error = line.Substring(6);
                    else if (line.StartsWith("info=")) send("info=" + part + ": " + line.Substring(5));
                }
            };
            child.ErrorDataReceived += (s, e) => { if (e.Data != null) AppLog.Write("Sensores avançados (" + part + " stderr): " + e.Data); };
            try { child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine(); }
            catch (Exception e) { Fail(part + ": " + e.Message); }
        }
        void Fail(string message)
        {
            Error = message; AppLog.Write("Sensores avançados: " + message);
            DisposeProcess(); retryAt = DateTime.UtcNow.AddSeconds(RetrySeconds);
        }
        void DisposeProcess()
        {
            var child = process; process = null;
            if (child == null) return;
            try { if (!child.HasExited) child.Kill(); } catch { }
            child.Dispose();
        }
        public void Dispose() { lock (gate) DisposeProcess(); }
    }
    // Kept in its own class so the main (non-elevated) process never loads LibreHardwareMonitorLib.
    sealed class HardwareSource : IDisposable
    {
        readonly LibreHardwareMonitor.Hardware.Computer computer;
        // CPU first; the motherboard (Super I/O chip) is added separately so a problem there cannot take the CPU readings down.
        public HardwareSource(string part)
        {
            computer = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = part == "cpu", IsMotherboardEnabled = part == "board" };
            computer.Open();
        }
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
        readonly Dictionary<string, double> cost = new Dictionary<string, double>();
        public string Timings()
        {
            return cost.Count == 0 ? "-" : String.Join("; ", cost.Select(c => c.Key + " " + (int)c.Value + " ms").ToArray());
        }
        public string Read()
        {
            // Each top-level part is timed; a slow one (seen: seconds per read on some PCs) is read less often,
            // so it cannot hold the others back.
            foreach (var h in computer.Hardware)
            {
                string key = h.HardwareType + " " + h.Name;
                var watch = Stopwatch.StartNew();
                h.Update(); foreach (var sub in Flatten(h.SubHardware)) sub.Update();
                double ms = watch.Elapsed.TotalMilliseconds; cost[key] = ms;
            }
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
            var list = sensors.Where(s => s.SensorType == type && s.Value.HasValue && !float.IsNaN(s.Value.Value) && !float.IsInfinity(s.Value.Value)).ToList();
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
        readonly Dictionary<string, DateTime> measuredAt = new Dictionary<string, DateTime>();
        DateTime lastData = DateTime.MinValue, lastAlive = DateTime.MinValue;
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
        internal Func<bool> TaskExists = () => SensorTask.PointsTo(Application.ExecutablePath);
        internal Func<bool> DriverCheck = () => PawnIo.Ready;
        internal string PipeName = SensorTask.PipeName;
        internal string Marker = Path.Combine(Path.GetDirectoryName(AppLog.Path), "sensores-avancados.ativo");
        internal double ConnectSeconds = 15, OpenSeconds = 180, SilentSeconds = 15, ProblemRetrySeconds = 120;
        internal double FreshSeconds = 30;

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
            // Readings can be several seconds apart on slow PCs; temperatures do not jump that fast.
            lock (data)
            {
                DateTime at;
                return measuredAt.TryGetValue(id, out at) && (DateTime.UtcNow - at).TotalSeconds <= FreshSeconds && values.TryGetValue(id, out value);
            }
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
            if (!configured) { Set("setup", driverReady ? "Ative ou reconfigure os sensores: a tarefa está ausente ou aponta para outra pasta do Lume." : "Falta instalar o driver PawnIO."); return; }
            StartHost();
        }
        void Raise() { var h = Changed; if (h != null) try { h(this, EventArgs.Empty); } catch { } }

        void StartHost()
        {
            Set("starting", "Iniciando o processo de sensores…");
            try { File.WriteAllText(Marker, DateTime.Now.ToString("s")); } catch { }
            var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
            lock (data)
            {
                pipe = server; startedAt = DateTime.UtcNow;
                hostStage = null; hostError = null; hostWarning = null; hostRunning = false; hostGone = false;
                values.Clear(); measuredAt.Clear(); lastData = DateTime.MinValue; lastAlive = DateTime.MinValue;
            }
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
            AppLog.Write("Sensores avançados: pipe conectado; configurando leitores.");
            writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Send("board=" + (skipBoard ? "0" : "1"));
            sentInterval = interval; Send("interval=" + Math.Max(1000, interval));
            AppLog.Write("Sensores avançados: configuração enviada; iniciando receptor.");
            new Thread(() => Read(server)) { IsBackground = true, Name = "Lume advanced sensors pipe" }.Start();
        }
        void Watch()
        {
            if (hostGone)
            {
                string stage = hostStage, error = hostError;
                StopHost();
                if (error != null) { retryAt = DateTime.UtcNow.AddSeconds(ProblemRetrySeconds); Set("error", error); return; }
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
            double since;
            lock (data)
            {
                DateTime latest = lastData > lastAlive ? lastData : lastAlive; if (latest < startedAt) latest = startedAt;
                since = (DateTime.UtcNow - latest).TotalSeconds;
            }
            // While the helper is opening something (CPU at start, motherboard later) it may be silent for a while.
            if (since > (hostRunning && hostStage == null ? SilentSeconds : OpenSeconds))
            {
                bool wasRunning = hostRunning;
                StopHost(); retryAt = DateTime.UtcNow.AddSeconds(ProblemRetrySeconds);
                Set("error", wasRunning ? "O processo de sensores parou de responder." : "O LibreHardwareMonitor demorou demais para abrir. Veja o registro de erros."); return;
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
                var input = new StreamReader(server, Encoding.UTF8); string line; int received = 0;
                while ((line = input.ReadLine()) != null)
                {
                    if (++received <= 5) AppLog.Write("Sensores avançados: recebido " + line);
                    // An old reader can finish after a replacement has connected. It must never
                    // publish readings or disconnect that new session.
                    lock (data)
                    {
                        if (!Object.ReferenceEquals(pipe, server)) return;
                        if (line.StartsWith("hello=")) { lastAlive = DateTime.UtcNow; continue; }
                        if (line.StartsWith("stage=")) { hostStage = line.Substring(6); lastAlive = DateTime.UtcNow; wake.Set(); continue; }
                        if (line.StartsWith("hang=")) { hostStage = line.Substring(5); continue; }
                        if (line.StartsWith("alive=")) { lastAlive = DateTime.UtcNow; continue; }
                        if (line.StartsWith("error=")) { hostError = line.Substring(6); AppLog.Write("Sensores avançados: " + hostError); continue; }
                        if (line.StartsWith("warn=")) { hostWarning = line.Substring(5); AppLog.Write("Sensores avançados: " + hostWarning); continue; }
                        if (line.StartsWith("info=")) { AppLog.Write("Sensores avançados: " + line.Substring(5)); continue; }
                        bool partial = line.StartsWith("sample=");
                        string payload = line; DateTime measured = DateTime.UtcNow;
                        if (partial)
                        {
                            string[] fields = line.Substring(7).Split(new char[] { '|' }, 3);
                            long ticks;
                            if (fields.Length != 3 || (fields[0] != "cpu" && fields[0] != "board") ||
                                !Int64.TryParse(fields[1], out ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
                            measured = new DateTime(ticks, DateTimeKind.Utc);
                            if (measured > DateTime.UtcNow.AddSeconds(1)) continue;
                            foreach (var id in fields[0] == "cpu" ? new[] { "cpu_temp", "cpu_power" } : new[] { "board_temp", "cpu_fan" })
                            { values.Remove(id); measuredAt.Remove(id); }
                            payload = fields[2];
                        }
                        else { values.Clear(); measuredAt.Clear(); }
                        foreach (var pair in payload.Split(';'))
                        {
                            int eq = pair.IndexOf('='); double v;
                            if (eq > 0 && Double.TryParse(pair.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !Double.IsNaN(v) && !Double.IsInfinity(v))
                            { string id = pair.Substring(0, eq); values[id] = v; measuredAt[id] = measured; }
                        }
                        lastData = DateTime.UtcNow;
                        hostStage = null;
                        if (!hostRunning && values.Count > 0) { hostRunning = true; wake.Set(); }
                    }
                }
            }
            catch (Exception) { }
            lock (data) { if (Object.ReferenceEquals(pipe, server)) { hostGone = true; wake.Set(); } }
        }
        void Send(string line)
        {
            var w = writer; if (w == null) return;
            // Bounded: a helper that stopped reading must not block this thread.
            try { var t = w.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), 0, Encoding.UTF8.GetByteCount(line + "\n")); t.Wait(2000); } catch { }
        }
        void StopHost()
        {
            NamedPipeServerStream server;
            lock (data) { server = pipe; pipe = null; values.Clear(); measuredAt.Clear(); lastData = DateTime.MinValue; lastAlive = DateTime.MinValue; }
            if (server != null) { Send("quit"); try { server.Dispose(); } catch { } }
            writer = null; hostRunning = false;
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
