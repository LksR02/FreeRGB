// SPDX-License-Identifier: GPL-2.0-or-later
// PC sensors for reactive lighting. Everything is read-only and comes from what Windows and the
// drivers already expose: system times / memory status (kernel32), NVIDIA NVML (nvml.dll, ships
// with the GeForce driver), GPU performance counters, and — when it is running — the WMI feed of
// LibreHardwareMonitor / OpenHardwareMonitor for CPU temperature and power.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace LumeRgb
{
    public sealed class SensorReading
    {
        public string Id, Name, Unit, Source, Reason;
        public bool Available;
        public double Value, DefaultMin, DefaultMax;
        public SensorReading Copy() { return (SensorReading)MemberwiseClone(); }
    }

    public interface ISensorSource
    {
        event EventHandler Updated;
        void Want(string who, bool on);
        SensorReading Get(string id);
    }

    public sealed class SensorHub : ISensorSource, IDisposable
    {
        public static readonly string[] Ids = { "cpu_load", "ram_load", "gpu_load", "gpu_temp", "gpu_power", "cpu_temp", "cpu_power", "total_power" };
        readonly object gate = new object();
        readonly Dictionary<string, SensorReading> readings = new Dictionary<string, SensorReading>();
        readonly HashSet<string> demand = new HashSet<string>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool disposed;
        public event EventHandler Updated;

        // Sources
        long lastIdle, lastKernel, lastUser;
        bool nvmlTried, nvmlReady; IntPtr gpu = IntPtr.Zero;
        PerformanceCounter[] gpuCounters; DateTime gpuCountersAt;
        PerformanceCounter[] thermal; bool thermalTried;
        string wmiNamespace; DateTime wmiRetryAt; int tick;
        double wmiCpuTemp = double.NaN, wmiCpuPower = double.NaN, wmiGpuTemp = double.NaN, wmiGpuPower = double.NaN, wmiGpuLoad = double.NaN;

        public SensorHub()
        {
            Define("cpu_load", "Uso da CPU", "%", 0, 100);
            Define("ram_load", "Uso da memória RAM", "%", 0, 100);
            Define("gpu_load", "Uso da GPU", "%", 0, 100);
            Define("gpu_temp", "Temperatura da GPU", "°C", 35, 85);
            Define("gpu_power", "Consumo da GPU", "W", 20, 250);
            Define("cpu_temp", "Temperatura da CPU", "°C", 35, 90);
            Define("cpu_power", "Consumo da CPU", "W", 15, 140);
            Define("total_power", "Consumo CPU + GPU", "W", 50, 400);
            thread = new Thread(Run) { IsBackground = true, Name = "Lume sensors", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }
        void Define(string id, string name, string unit, double min, double max)
        {
            readings[id] = new SensorReading { Id = id, Name = name, Unit = unit, DefaultMin = min, DefaultMax = max, Reason = "Aguardando leitura…" };
        }
        public static string NameOf(string id)
        {
            switch (id)
            {
                case "cpu_load": return "Uso da CPU"; case "ram_load": return "Uso da memória RAM"; case "gpu_load": return "Uso da GPU";
                case "gpu_temp": return "Temperatura da GPU"; case "gpu_power": return "Consumo da GPU"; case "cpu_temp": return "Temperatura da CPU";
                case "cpu_power": return "Consumo da CPU"; case "total_power": return "Consumo CPU + GPU"; default: return id;
            }
        }

        // Sensors are only sampled while someone (rules engine, open Reações page) needs them.
        public void Want(string who, bool on)
        {
            lock (gate) { if (on) demand.Add(who); else demand.Remove(who); }
            if (on) wake.Set();
        }
        public List<SensorReading> Snapshot() { lock (gate) return Ids.Select(i => readings[i].Copy()).ToList(); }
        public SensorReading Get(string id) { lock (gate) { SensorReading r; return readings.TryGetValue(id, out r) ? r.Copy() : null; } }

        void Run()
        {
            while (!disposed)
            {
                bool active; lock (gate) active = demand.Count > 0;
                if (!active) { wake.WaitOne(2000); continue; }
                var watch = Stopwatch.StartNew();
                try { Sample(); } catch (Exception e) { AppLog.Write("Sensores: " + e); }
                var handler = Updated; if (handler != null) try { handler(this, EventArgs.Empty); } catch { }
                int rest = 1000 - (int)watch.ElapsedMilliseconds;
                wake.WaitOne(Math.Max(200, rest));
            }
        }
        void Set(string id, bool ok, double value, string source, string reason)
        {
            lock (gate)
            {
                var r = readings[id]; r.Available = ok && !double.IsNaN(value) && !double.IsInfinity(value);
                r.Value = r.Available ? Math.Round(value, 1) : 0; r.Source = source; r.Reason = r.Available ? null : reason;
            }
        }

        void Sample()
        {
            tick++;
            // CPU: share of non-idle time since the previous sample (kernel time includes idle).
            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                long di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
                if (lastKernel != 0 && dk + du > 0) Set("cpu_load", true, 100.0 * (1 - (double)di / (dk + du)), "Windows", null);
                lastIdle = idle; lastKernel = kernel; lastUser = user;
            }
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (GlobalMemoryStatusEx(ref memory)) Set("ram_load", true, memory.Load, "Windows", null);

            if (tick % 2 == 1) ReadWmi();
            ReadGpu();
            ReadCpuThermal();

            SensorReading cpuPower = Get("cpu_power"), gpuPower = Get("gpu_power");
            if (cpuPower.Available && gpuPower.Available) Set("total_power", true, cpuPower.Value + gpuPower.Value, cpuPower.Source + " + " + gpuPower.Source, null);
            else Set("total_power", false, 0, null, "Precisa do consumo da CPU e da GPU. " + (cpuPower.Available ? gpuPower.Reason : cpuPower.Reason));
        }

        void ReadGpu()
        {
            if (!nvmlTried) { nvmlTried = true; nvmlReady = Nvml.Start(out gpu); }
            if (nvmlReady)
            {
                uint temperature, milliwatts; Nvml.Utilization use;
                bool t = Nvml.nvmlDeviceGetTemperature(gpu, 0, out temperature) == 0;
                bool p = Nvml.nvmlDeviceGetPowerUsage(gpu, out milliwatts) == 0;
                bool u = Nvml.nvmlDeviceGetUtilizationRates(gpu, out use) == 0;
                Set("gpu_temp", t, temperature, "NVIDIA", "O driver não informou a temperatura.");
                Set("gpu_power", p, milliwatts / 1000.0, "NVIDIA", "O driver não informou o consumo.");
                Set("gpu_load", u, use.Gpu, "NVIDIA", "O driver não informou o uso.");
                return;
            }
            // Other GPUs: Windows "GPU Engine" counters for load, LibreHardwareMonitor for the rest.
            double load = CounterGpuLoad();
            if (!double.IsNaN(load)) Set("gpu_load", true, load, "Windows", null);
            else Set("gpu_load", !double.IsNaN(wmiGpuLoad), wmiGpuLoad, "LibreHardwareMonitor", "Contadores de GPU do Windows indisponíveis.");
            string need = "Sem driver NVIDIA: abra o LibreHardwareMonitor (com WMI) para esta leitura.";
            Set("gpu_temp", !double.IsNaN(wmiGpuTemp), wmiGpuTemp, "LibreHardwareMonitor", need);
            Set("gpu_power", !double.IsNaN(wmiGpuPower), wmiGpuPower, "LibreHardwareMonitor", need);
        }
        double CounterGpuLoad()
        {
            try
            {
                if (gpuCounters == null || DateTime.UtcNow > gpuCountersAt)
                {
                    if (gpuCounters != null) foreach (var c in gpuCounters) c.Dispose();
                    var category = new PerformanceCounterCategory("GPU Engine");
                    gpuCounters = category.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true)).ToArray();
                    foreach (var c in gpuCounters) c.NextValue(); // first value is always 0
                    gpuCountersAt = DateTime.UtcNow.AddSeconds(10); // processes come and go
                    return double.NaN;
                }
                double sum = 0;
                foreach (var c in gpuCounters) { try { sum += c.NextValue(); } catch (InvalidOperationException) { } }
                return Math.Min(100, sum);
            }
            catch (Exception) { gpuCounters = null; gpuCountersAt = DateTime.UtcNow.AddSeconds(30); return double.NaN; }
        }

        void ReadCpuThermal()
        {
            string lhm = "Abra o LibreHardwareMonitor (opção WMI ativa) para medir a CPU com precisão.";
            if (!double.IsNaN(wmiCpuTemp)) Set("cpu_temp", true, wmiCpuTemp, "LibreHardwareMonitor", null);
            else
            {
                double acpi = AcpiTemperature();
                Set("cpu_temp", !double.IsNaN(acpi), acpi, "ACPI (aproximada)", lhm);
            }
            Set("cpu_power", !double.IsNaN(wmiCpuPower), wmiCpuPower, "LibreHardwareMonitor", lhm);
        }
        double AcpiTemperature()
        {
            try
            {
                if (!thermalTried)
                {
                    thermalTried = true;
                    var category = new PerformanceCounterCategory("Thermal Zone Information");
                    thermal = category.GetInstanceNames().Select(n => new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", n, true)).ToArray();
                }
                if (thermal == null || thermal.Length == 0) return double.NaN;
                double max = thermal.Max(c => (double)c.NextValue()) / 10.0 - 273.15; // tenths of kelvin
                return max > 5 && max < 125 ? max : double.NaN;
            }
            catch (Exception) { thermal = null; return double.NaN; }
        }

        void ReadWmi()
        {
            if (wmiNamespace == null && DateTime.UtcNow < wmiRetryAt) return;
            string[] spaces = wmiNamespace != null ? new[] { wmiNamespace } : new[] { "root\\LibreHardwareMonitor", "root\\OpenHardwareMonitor" };
            foreach (var space in spaces)
            {
                try
                {
                    var rows = new List<Tuple<string, string, string, double>>();
                    using (var searcher = new ManagementObjectSearcher(space, "SELECT Identifier, Name, SensorType, Value FROM Sensor"))
                    {
                        searcher.Options.Timeout = TimeSpan.FromSeconds(2);
                        foreach (ManagementObject o in searcher.Get())
                            using (o) rows.Add(Tuple.Create(Convert.ToString(o["Identifier"]) ?? "", Convert.ToString(o["Name"]) ?? "", Convert.ToString(o["SensorType"]) ?? "", Convert.ToDouble(o["Value"] ?? 0.0)));
                    }
                    if (rows.Count == 0) continue;
                    wmiNamespace = space;
                    wmiCpuTemp = Pick(rows, "cpu", "Temperature", new[] { "Package", "Tctl", "Tdie", "CPU" });
                    wmiCpuPower = Pick(rows, "cpu", "Power", new[] { "Package", "CPU" });
                    wmiGpuTemp = Pick(rows, "gpu", "Temperature", new[] { "GPU Core", "Core", "Hot Spot" });
                    wmiGpuPower = Pick(rows, "gpu", "Power", new[] { "GPU Package", "GPU Power", "Package", "Total" });
                    wmiGpuLoad = Pick(rows, "gpu", "Load", new[] { "GPU Core", "D3D 3D", "Core" });
                    return;
                }
                catch (Exception) { }
            }
            wmiNamespace = null; wmiRetryAt = DateTime.UtcNow.AddSeconds(30);
            wmiCpuTemp = wmiCpuPower = wmiGpuTemp = wmiGpuPower = wmiGpuLoad = double.NaN;
        }
        // Device identifiers look like "/amdcpu/0/temperature/2" or "/gpu-nvidia/0/power/0".
        static double Pick(List<Tuple<string, string, string, double>> rows, string device, string type, string[] preferred)
        {
            var candidates = rows.Where(r => r.Item3 == type && r.Item1.Split('/').Skip(1).FirstOrDefault() != null &&
                r.Item1.Split('/')[1].Contains(device)).ToList();
            if (candidates.Count == 0) return double.NaN;
            foreach (var name in preferred)
            {
                var hit = candidates.FirstOrDefault(r => r.Item2.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit.Item4;
            }
            return candidates.Max(r => r.Item4);
        }

        public void Dispose()
        {
            disposed = true; wake.Set();
            try { thread.Join(1500); } catch { }
            if (nvmlReady) try { Nvml.nvmlShutdown(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)] struct MemoryStatus
        {
            public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }

    // Minimal NVML binding (read-only queries). nvml.dll is installed by the NVIDIA driver.
    static class Nvml
    {
        [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint Gpu, Memory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] public static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        public static bool Start(out IntPtr device)
        {
            device = IntPtr.Zero;
            try
            {
                if (LoadLibrary("nvml.dll") == IntPtr.Zero)
                {
                    string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
                    if (!File.Exists(legacy) || LoadLibrary(legacy) == IntPtr.Zero) return false;
                }
                uint count;
                if (nvmlInit_v2() != 0 || nvmlDeviceGetCount_v2(out count) != 0 || count == 0) return false;
                return nvmlDeviceGetHandleByIndex_v2(0, out device) == 0;
            }
            catch (Exception) { return false; } // DllNotFound / EntryPointNotFound: no NVIDIA driver
        }
    }
}
