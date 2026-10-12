// SPDX-License-Identifier: GPL-2.0-or-later
// Hardware-free tests for the advanced-sensor client: a fake helper speaks the pipe protocol, and the
// window/sensor-loop calls must never block, whatever the helper (or the Task Scheduler) does.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using LumeRgb;

namespace LumeRgb { static class Program { public const string Version = "test"; } }

class AdvancedSensorsTests
{
    static int count;
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); count++; Console.WriteLine("PASS " + message); }
    static bool WaitFor(Func<bool> condition, double seconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds) { if (condition()) return true; Thread.Sleep(50); }
        return condition();
    }
    // A fake elevated helper. mode: normal | hangBoard (dies while opening the motherboard) | error | silent
    static int launches; static string lastBoard;
    static AdvancedSensors Client(string name, Func<int, string> mode, string marker)
    {
        var client = new AdvancedSensors(false, marker);
        client.TaskExists = () => true; client.DriverCheck = () => true; client.PipeName = name;
        client.ConnectSeconds = 5; client.OpenSeconds = 3; client.SilentSeconds = 3; client.ProblemRetrySeconds = 1;
        client.Launch = (out string output) =>
        {
            int n = Interlocked.Increment(ref launches); string m = mode(n); output = "ok";
            new Thread(() => FakeHost(name, m)) { IsBackground = true }.Start();
            return true;
        };
        client.Start();
        return client;
    }
    static void FakeHost(string name, string mode)
    {
        try
        {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut))
            {
                pipe.Connect(5000);
                var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                var r = new StreamReader(pipe, Encoding.UTF8);
                w.WriteLine("hello=test");
                string board = r.ReadLine(); lastBoard = board;
                if (mode == "error") { w.WriteLine("error=O driver PawnIO não está instalado."); return; }
                if (mode == "silent") { Thread.Sleep(8000); return; }
                if (mode == "slow")
                {
                    // One reading, then a long read that only sends heartbeats, then readings again.
                    w.WriteLine("stage=cpu"); w.WriteLine("cpu_temp=50.0");
                    for (int i = 0; i < 7; i++) { Thread.Sleep(1000); w.WriteLine("alive=read:" + i); }
                    for (int i = 0; i < 100 && pipe.IsConnected; i++) { w.WriteLine("cpu_temp=51.0"); Thread.Sleep(100); }
                    return;
                }
                w.WriteLine("stage=cpu");
                w.WriteLine("cpu_temp=55.5;cpu_power=40.0"); // CPU readings first, the motherboard is added afterwards
                if (board == "board=1") { w.WriteLine("stage=board"); if (mode == "hangBoard") { w.WriteLine("hang=board"); return; } }
                var reader = new Thread(() => { try { while (r.ReadLine() != null) { } } catch { } }) { IsBackground = true }; reader.Start();
                for (int i = 0; i < 400 && pipe.IsConnected; i++) { w.WriteLine("cpu_temp=55.5;cpu_power=40.0"); Thread.Sleep(100); }
            }
        }
        catch { }
    }
    static void Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Lume-adv-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, "marker");

        // 1. Normal start: running, readings flow, Stop lets the helper go and clears the marker.
        var a = Client("lume-adv-1", n => "normal", marker);
        a.Ensure(1000);
        Check(WaitFor(() => a.State == "running", 10), "helper starts and reports readings");
        double v; Check(a.TryGet("cpu_temp", out v) && Math.Abs(v - 55.5) < 0.01, "CPU temperature read through the pipe");
        Check(File.Exists(marker), "marker written while the helper runs");
        a.Stop();
        Check(WaitFor(() => a.State == "off" && !File.Exists(marker), 5), "Stop ends the helper and removes the marker");
        a.Dispose();

        // 2. Marker left behind = the last session ended while the helper ran.
        File.WriteAllText(marker, "x");
        var b = new AdvancedSensors(false, marker);
        Check(b.EndedBadlyLastTime && !File.Exists(marker), "bad previous exit detected at start");
        b.Dispose();

        // 3. Paused: never launches.
        launches = 0;
        var c = new AdvancedSensors(true, marker);
        c.TaskExists = () => true; c.DriverCheck = () => true; c.PipeName = "lume-adv-3";
        c.Launch = (out string output) => { launches++; output = ""; return true; };
        c.Start(); c.Ensure(1000); Thread.Sleep(1500);
        Check(launches == 0 && c.State == "paused", "paused sensors never start the helper");
        c.Dispose();

        // 4. The motherboard step hangs: the next try skips the motherboard and keeps the CPU.
        launches = 0;
        var d = Client("lume-adv-4", n => n == 1 ? "hangBoard" : "normal", marker);
        d.Ensure(1000);
        Check(WaitFor(() => d.State == "running", 20) && launches == 2 && lastBoard == "board=0", "board hang -> retried with CPU only, now running");
        d.Dispose();

        // 5. Helper reports an error: shown as is, the app keeps going.
        var e = Client("lume-adv-5", n => "error", marker);
        e.Ensure(1000);
        Check(WaitFor(() => e.State == "error" && (e.Detail ?? "").Contains("PawnIO"), 10), "helper error reported to the page: " + e.Detail);
        e.Dispose();

        // 6. Helper connects but never sends readings: stopped after the open timeout.
        var f = Client("lume-adv-6", n => "silent", marker);
        f.Ensure(1000);
        Check(WaitFor(() => f.State == "error", 15), "silent helper stopped after the timeout: " + f.Detail);
        f.Dispose();

        // 6b. Slow reads with heartbeats are not mistaken for a dead helper.
        var slow = Client("lume-adv-6b", n => "slow", marker);
        slow.Ensure(1000);
        Check(WaitFor(() => slow.State == "running", 10), "slow helper running");
        Thread.Sleep(6000);
        double t6; Check(slow.State == "running" && slow.TryGet("cpu_temp", out t6), "7 s read with heartbeats: still running, last reading kept");
        Check(WaitFor(() => slow.TryGet("cpu_temp", out t6) && Math.Abs(t6 - 51) < 0.01, 5), "readings resume after the slow read");
        slow.Dispose();

        // 7. Task Scheduler hangs: every call from the window / sensor loop still returns at once.
        var g = new AdvancedSensors(false, marker);
        g.DriverCheck = () => true; g.PipeName = "lume-adv-7";
        g.TaskExists = () => { Thread.Sleep(6000); return true; };
        g.Start(); Thread.Sleep(200);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) { g.Ensure(50); double x; g.TryGet("cpu_temp", out x); string s = g.State + g.Detail; if (s == null) return; bool cfg = g.Configured; bool drv = g.DriverReady; }
        Check(watch.ElapsedMilliseconds < 500, "window and sensor loop never wait on the Task Scheduler (" + watch.ElapsedMilliseconds + " ms for 1000 calls)");
        watch.Restart(); g.Stop(); g.SetPaused(true, "x"); g.ResetConfiguredCache();
        Check(watch.ElapsedMilliseconds < 100, "pause/stop return immediately too");
        g.Dispose();

        try { Directory.Delete(folder, true); } catch { }
        Console.WriteLine("RESULT " + count + " advanced sensor checks passed; no driver, no Task Scheduler touched.");
    }
}
