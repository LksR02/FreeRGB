// Reads the real installed sensor task for a bounded time. No RGB or driver settings changed.
using System;
using System.IO;
using System.Threading;
using LumeRgb;
namespace LumeRgb { static class Program { public const string Version = "probe"; } }
class AdvancedSensorsProbe
{
    static int Main(string[] args)
    {
        int seconds = args.Length == 0 ? 105 : Int32.Parse(args[0]);
        using (var source = new AdvancedSensors(false, Path.Combine(Path.GetTempPath(), "Lume-probe-" + Guid.NewGuid().ToString("N"))))
        {
            // The read-only probe intentionally calls the installed task, not itself.
            source.TaskExists = SensorTask.Exists;
            source.Start();
            bool cpu = false, board = false;
            for (int i = 0; i < seconds; i++)
            {
                source.Ensure(1000);
                if (i % 5 == 0)
                {
                    Console.WriteLine("STATE " + source.State + " " + source.Detail);
                    foreach (string id in AdvancedSensorIds.All)
                    {
                        double value;
                        if (source.TryGet(id, out value))
                        { Console.WriteLine(id + "=" + value); if (id == "cpu_temp") cpu = true; if (id == "board_temp") board = true; }
                    }
                }
                Thread.Sleep(1000);
            }
            Console.WriteLine("RESULT cpu=" + cpu + " board=" + board);
            source.Stop();
            return cpu && board ? 0 : 1;
        }
    }
}
