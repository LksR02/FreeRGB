// Reversible, opt-in physical lighting test. Never changes keys, calibration or firmware.
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using LumeRgb;

class X68TransitionProbe
{
    static byte[] Query(X68WindowsHidTransport transport)
    {
        transport.SetFeature(X68KeyboardController.BuildQuery()); Thread.Sleep(10);
        byte[] state = transport.GetFeature(); X68KeyboardController.ValidateReport(state); return state;
    }
    static byte[] RestoreCommand(byte[] state)
    {
        if ((state[2] != 1 && state[2] != 2 && state[2] != 4) || (state[5] & 15) != 7)
            throw new NotSupportedException("O estado atual não permite restauração exata neste teste.");
        byte[] report = new byte[65]; report[1] = 7; Buffer.BlockCopy(state, 2, report, 2, 7);
        int sum = 0; for (int i = 1; i <= 8; i++) sum += report[i]; report[9] = (byte)(255 - (sum & 255));
        X68WindowsHidTransport.ValidateOutgoingReport(report); return report;
    }
    static void Fade(X68KeyboardController controller, string mode, int seconds)
    {
        Console.WriteLine("PHASE " + mode);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int steps = (seconds * 1000 + 8000) / 50;
        for (int i = 0; i <= steps; i++)
        {
            double t = Math.Max(0, Math.Min(1, (i * 50 - 2000) / (seconds * 1000.0)));
            Color c = ColorTransition.Mix(Color.Lime, Color.Magenta, t);
            if (mode == "classic") controller.ApplyFast(new ZoneSetting { Key = "X68HE", Effect = "Static", Color = c, BrightnessPercent = 100, SpeedPercent = 50 });
            else { controller.LiveVariant = mode; controller.Stream(c, 100); }
            if (i % 100 == 0) Console.WriteLine("FRAME " + SettingsStore.Hex(c) + " " + watch.ElapsedMilliseconds + "ms");
            int wait = (i + 1) * 50 - (int)watch.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
        Thread.Sleep(1000);
        controller.EndStream();
    }
    static int Main(string[] args)
    {
        var device = WindowsDevices.GetHidDevices().Single(d => d.VendorId == 0x3151 && d.ProductId == 0x502D && d.UsagePage == 0xFFFF && d.Usage == 2);
        byte[] original, restore;
        using (var raw = new X68WindowsHidTransport(device.Path)) { original = Query(raw); restore = RestoreCommand(original); }
        try
        {
            using (var controller = new X68KeyboardController())
            {
                controller.Connect(device);
                string mode = args.Length == 0 ? "classic" : args[0];
                int seconds = args.Length < 2 ? 6 : Math.Max(1, Math.Min(60, Int32.Parse(args[1])));
                if (mode != "classic" && mode != "screen" && mode != "overlay" && mode != "app") return 2;
                if (mode == "app")
                {
                    using (var hub = new LightingHub()) { hub.Attach(null, controller, "physical-test"); hub.TestKeyboardLive(); }
                    Console.WriteLine("APP blue-to-red test completed"); return 0;
                }
                if (mode == "overlay") controller.Apply(new ZoneSetting { Key = "X68HE", Effect = "Static", Color = Color.Lime, BrightnessPercent = 100, SpeedPercent = 50 });
                try { Fade(controller, mode, seconds); } catch (NotSupportedException e) { Console.WriteLine(e.Message); }
            }
        }
        finally
        {
            using (var raw = new X68WindowsHidTransport(device.Path))
            {
                if (!X68KeyboardController.LightingFieldsEqual(original, Query(raw))) { raw.SetFeature(restore); Thread.Sleep(200); }
                if (!X68KeyboardController.LightingFieldsEqual(original, Query(raw))) throw new Exception("A restauração não foi confirmada.");
                Console.WriteLine("RESTORED original LED parameters");
            }
        }
        return 0;
    }
}
