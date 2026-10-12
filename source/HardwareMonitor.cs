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
        public const string Name = "LumeRGB\\Sensores";
        public static string PipeName { get { return "LumeRGB.Sensors." + Process.GetCurrentProcess().SessionId; } }
        static int Schtasks(string args, out string output)
        {
            var info = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(info))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000); return p.HasExited ? p.ExitCode : -1;
            }
        }
        public static bool Exists() { string o; try { return Schtasks("/query /tn \"" + Name + "\"", out o) == 0; } catch { return false; } }
        public static bool Run(out string output) { try { return Schtasks("/run /tn \"" + Name + "\"", out output) == 0; } catch (Exception e) { output = e.Message; return false; } }
        public static bool Delete(out string output) { try { return Schtasks("/delete /tn \"" + Name + "\" /f", out output) == 0; } catch (Exception e) { output = e.Message; return false; } }
        public static bool Create(string exe, out string output)
        {
            string user = WindowsIdentity.GetCurrent().Name;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Lume RGB: sensores de CPU e placa-mãe (LibreHardwareMonitorLib). Iniciada pelo Lume somente quando necessário.</Description></RegistrationInfo>\r\n" +
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
        public static int Run()
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", SensorTask.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(20000);
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    var reader = new StreamReader(pipe, Encoding.UTF8);
                    int interval = 500; bool quit = false;
                    var listen = new Thread(() =>
                    {
                        try
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line == "quit") break;
                                int ms; if (line.StartsWith("interval=") && Int32.TryParse(line.Substring(9), out ms)) interval = Math.Max(250, Math.Min(5000, ms));
                            }
                        }
                        catch { }
                        quit = true;
                    }) { IsBackground = true };
                    listen.Start();
                    writer.WriteLine("hello=" + Program.Version);
                    // Every failure is reported to the app, so the page can say exactly what went wrong.
                    if (!PawnIo.Ready) { writer.WriteLine("error=O driver PawnIO não está instalado (ou é anterior à 2.0). Clique em Reconfigurar."); return 2; }
                    HardwareSource source;
                    try { source = CreateSource(); }
                    catch (Exception e) { AppLog.Write("Sensores avançados (abrir): " + e); writer.WriteLine("error=" + OneLine("O LibreHardwareMonitor não abriu: " + e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message)); return 3; }
                    try
                    {
                        writer.WriteLine("info=" + OneLine(source.Describe()));
                        bool first = true;
                        while (!quit && pipe.IsConnected)
                        {
                            var watch = Stopwatch.StartNew();
                            string line = source.Read();
                            writer.WriteLine(line);
                            if (first && line.IndexOf("cpu_temp=", StringComparison.Ordinal) < 0)
                                writer.WriteLine("warn=" + OneLine("O LibreHardwareMonitor abriu, mas não encontrou a temperatura da CPU (" + source.Describe() + "). O driver PawnIO pode estar bloqueado (Integridade de Memória/antivírus) ou exigir reiniciar o PC."));
                            first = false;
                            int rest = interval - (int)watch.ElapsedMilliseconds;
                            if (rest > 0) Thread.Sleep(rest);
                        }
                    }
                    finally { source.Dispose(); }
                }
                return 0;
            }
            catch (Exception e) { AppLog.Write("Sensores avançados (processo): " + e); return 1; }
        }
    }

    // Kept in its own class so the main (non-elevated) process never loads LibreHardwareMonitorLib.
    sealed class HardwareSource : IDisposable
    {
        readonly LibreHardwareMonitor.Hardware.Computer computer;
        public HardwareSource()
        {
            computer = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };
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
    public sealed class AdvancedSensors : IAdvancedSource, IDisposable
    {
        readonly object gate = new object();
        readonly Dictionary<string, double> values = new Dictionary<string, double>();
        NamedPipeServerStream pipe; StreamWriter writer; Thread reader;
        DateTime lastData = DateTime.MinValue, retryAt = DateTime.MinValue;
        bool? configured; int sentInterval; string failed, warning;
        public string State { get; private set; }   // off, setup, starting, running, error
        public string Detail { get; private set; }
        public event EventHandler Changed;
        public AdvancedSensors() { State = "off"; }

        public bool Configured
        {
            get { lock (gate) { if (!configured.HasValue) configured = SensorTask.Exists(); return configured.Value; } }
        }
        public bool DriverReady { get { return PawnIo.Ready; } }
        void Set(string state, string detail)
        {
            bool changed; lock (gate) { changed = State != state || Detail != detail; State = state; Detail = detail; }
            if (changed) { var h = Changed; if (h != null) try { h(this, EventArgs.Empty); } catch { } }
        }
        public bool TryGet(string id, out double value)
        {
            lock (gate)
            {
                value = 0;
                if (State != "running" || (DateTime.UtcNow - lastData).TotalSeconds > 5) return false;
                return values.TryGetValue(id, out value);
            }
        }

        // Called from the sensor thread whenever readings are wanted.
        public void Ensure(int intervalMs)
        {
            lock (gate)
            {
                if (State == "starting" && lastData != DateTime.MinValue && (DateTime.UtcNow - lastData).TotalSeconds > 25)
                {
                    Stop(); retryAt = DateTime.UtcNow.AddMinutes(1);
                    Set("error", "O LibreHardwareMonitor demorou demais para abrir. Veja o registro de erros."); return;
                }
                if (State == "running" || State == "starting")
                {
                    if (State == "running" && (DateTime.UtcNow - lastData).TotalSeconds > 10) { Stop(); Set("error", "O processo de sensores parou de responder."); retryAt = DateTime.UtcNow.AddSeconds(20); }
                    else if (intervalMs != sentInterval) { sentInterval = intervalMs; Send("interval=" + Math.Max(250, intervalMs)); }
                    return;
                }
                if (DateTime.UtcNow < retryAt) return;
                retryAt = DateTime.UtcNow.AddSeconds(30);
            }
            if (!Configured) { Set("setup", PawnIo.Ready ? "Falta autorizar o processo de sensores." : "Falta instalar o driver PawnIO."); return; }
            // Starting the task and waiting for it can take seconds: never block the sensor loop.
            Set("starting", null); sentInterval = intervalMs;
            ThreadPool.QueueUserWorkItem(_ => StartHost(intervalMs));
        }
        void StartHost(int intervalMs)
        {
            try
            {
                Set("starting", null);
                var server = new NamedPipeServerStream(SensorTask.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                lock (gate) pipe = server;
                string output;
                if (!SensorTask.Run(out output)) { Stop(); lock (gate) configured = null; Set("error", "A tarefa de sensores não iniciou: " + output.Trim()); return; }
                var wait = server.BeginWaitForConnection(null, null);
                if (!wait.AsyncWaitHandle.WaitOne(15000)) { Stop(); Set("error", "O processo de sensores não respondeu. Se você moveu a pasta do Lume, ative os sensores de novo."); return; }
                server.EndWaitForConnection(wait);
                lock (gate)
                {
                    writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    Send("interval=" + Math.Max(250, intervalMs));
                    reader = new Thread(() => Read(server)) { IsBackground = true, Name = "Lume advanced sensors" };
                    reader.Start();
                }
            }
            catch (Exception e) { Stop(); AppLog.Write("Sensores avançados: " + e); Set("error", e.Message); }
        }
        void Read(NamedPipeServerStream server)
        {
            try
            {
                var input = new StreamReader(server, Encoding.UTF8); string line;
                while ((line = input.ReadLine()) != null)
                {
                    if (line.StartsWith("hello=")) { lock (gate) lastData = DateTime.UtcNow; Set("starting", "Processo de sensores iniciado; abrindo o LibreHardwareMonitor…"); continue; }
                    if (line.StartsWith("error=")) { AppLog.Write("Sensores avançados: " + line.Substring(6)); lock (gate) { failed = line.Substring(6); retryAt = DateTime.UtcNow.AddMinutes(2); } Set("error", line.Substring(6)); continue; }
                    if (line.StartsWith("warn=")) { AppLog.Write("Sensores avançados: " + line.Substring(5)); lock (gate) warning = line.Substring(5); continue; }
                    if (line.StartsWith("info=")) { AppLog.Write("Sensores avançados: " + line.Substring(5)); continue; }
                    lock (gate)
                    {
                        values.Clear();
                        foreach (var pair in line.Split(';'))
                        {
                            int eq = pair.IndexOf('='); double v;
                            if (eq > 0 && Double.TryParse(pair.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) values[pair.Substring(0, eq)] = v;
                        }
                        lastData = DateTime.UtcNow;
                    }
                    if (State != "running") Set("running", warning);
                }
            }
            catch (Exception) { }
            lock (gate) if (pipe == server) { Stop(); State = "error"; Detail = failed ?? "O processo de sensores foi encerrado."; retryAt = DateTime.UtcNow.AddSeconds(failed != null ? 120 : 15); }
        }
        void Send(string line) { try { if (writer != null) writer.WriteLine(line); } catch { } }
        public void Stop()
        {
            lock (gate)
            {
                Send("quit");
                try { if (pipe != null) pipe.Dispose(); } catch { }
                pipe = null; writer = null; values.Clear();
                if (State == "running" || State == "starting") State = "off";
            }
        }
        public void ResetConfiguredCache() { lock (gate) { configured = null; retryAt = DateTime.MinValue; } }

        // One UAC prompt: installs PawnIO if needed and registers the scheduled task.
        public static bool RunSetup(bool remove)
        {
            var info = new ProcessStartInfo(Application.ExecutablePath, remove ? "--remove-sensors" : "--setup-sensors") { UseShellExecute = true, Verb = "runas" };
            try
            {
                using (var p = Process.Start(info)) { p.WaitForExit(); return p.ExitCode == 0; }
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC prompt cancelled
        }
        public void Dispose() { Stop(); }
    }
}
