// Real child processes with fake measurements: verifies fault isolation without drivers.
using System;
using System.Diagnostics;
using System.Threading;
using LumeRgb;
namespace LumeRgb { static class Program { public const string Version = "test"; } }
class SensorWorkerTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--sensor-worker")
        {
            if (args[1] == "board") { Console.WriteLine("stage=read"); Thread.Sleep(10000); return 0; }
            for (int i = 0; i < 300; i++)
            { Console.WriteLine("sample=cpu|" + DateTime.UtcNow.Ticks + "|cpu_temp=50.0"); Thread.Sleep(50); }
            return 0;
        }
        int samples = 0;
        using (var cpu = new SensorWorker("cpu", line => { if (line.StartsWith("sample=")) Interlocked.Increment(ref samples); }))
        using (var board = new SensorWorker("board", line => { }))
        {
            board.OpenTimeout = 2; board.ReadTimeout = .8; board.RetrySeconds = 60;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 3000) { cpu.Tick(1000); board.Tick(1000); Thread.Sleep(50); }
            Check(board.Error != null && board.Error.Contains("read"), "stuck board child is ended with its exact stage");
            int before = samples;
            Thread.Sleep(500); cpu.Tick(1000);
            Check(samples > before && cpu.Error == null, "CPU readings continue after the board child hangs");
        }
        Console.WriteLine("ALL SENSOR WORKER TESTS PASSED; no driver accessed."); return 0;
    }
}
