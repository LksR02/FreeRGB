// SPDX-License-Identifier: GPL-2.0-or-later
// Hardware-free tests for reactive lighting (sensor -> zone colour rules).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using LumeRgb;

class ReactiveTests
{
    class FakeHid : IHidTransport
    {
        public ushort VendorId { get; set; } public ushort ProductId { get; set; }
        public string Serial { get; set; } public string Path { get; set; } public int FeatureReportLength { get; set; }
        public byte[] State; public int Writes;
        public FakeHid(byte[] state) { VendorId = 0x0DB0; ProductId = 0x0076; Serial = "7E3024102203"; Path = "fake"; FeatureReportLength = 761; State = (byte[])state.Clone(); }
        public byte[] GetFeature(byte id, int length) { return (byte[])State.Clone(); }
        public void SetFeature(byte[] report) { MsiRgbController.ValidateReport(report); Writes++; State = (byte[])report.Clone(); }
        public void Dispose() { }
    }
    class FakeKeys : IX68HidTransport
    {
        public ushort VendorId { get { return 0x3151; } } public ushort ProductId { get { return 0x502D; } }
        public ushort UsagePage { get { return 0xFFFF; } } public ushort Usage { get { return 2; } }
        public int FeatureReportLength { get { return 65; } } public string ProductName { get { return "X68HE"; } }
        public byte[] State;
        public FakeKeys() { State = new byte[65]; State[1] = 0x87; State[2] = 2; State[3] = 2; State[4] = 3; State[5] = 0xA7; State[6] = 10; State[7] = 200; State[8] = 90; }
        public byte[] GetFeature() { return (byte[])State.Clone(); }
        public void SetFeature(byte[] report)
        {
            X68WindowsHidTransport.ValidateOutgoingReport(report);
            if (report[1] == 7) for (int i = 2; i <= 8; i++) State[i] = report[i];
        }
        public void Dispose() { }
    }
    static byte[] Fixture()
    {
        byte[] b = new byte[185]; for (int i = 0; i < b.Length; i++) b[i] = (byte)(i ^ 0x5A);
        b[0] = 0x52; b[184] = 0; b[74] = 1; b[78] = 40; b[82] = 0x80;
        foreach (int o in new int[] { 1, 134, 31, 42 }) { b[o] = 1; b[o + 4] = 40; b[o + 9] = 0; }
        b[1] = 2; b[42] = 3; // Different native effects per header: they must come back after "Ligar".
        b[41] = 100; b[52] = 80; return b;
    }
    class FakeSensors : ISensorSource
    {
        public event EventHandler Updated;
        public Dictionary<string, double> Values = new Dictionary<string, double>();
        public HashSet<string> Wanted = new HashSet<string>();
        public void Want(string who, bool on) { if (on) Wanted.Add(who); else Wanted.Remove(who); }
        public SensorReading Get(string id)
        {
            double v; bool ok = Values.TryGetValue(id, out v);
            return new SensorReading { Id = id, Available = ok, Value = v };
        }
        public void Fire() { if (Updated != null) Updated(this, EventArgs.Empty); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
    static void Settle(LumeService service) { service.Idle().Wait(); }
    static RgbZone Zone(LumeService s, string key) { return s.Snapshot().Zones.First(z => z.Key == key); }
    static void Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Lume-reactive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var hid = new FakeHid(Fixture()); var keys = new FakeKeys();
        Func<List<HidDeviceInfo>, ILightingBackend> factory = found => {
            var board = new MsiRgbController(); board.Connect(hid);
            var keyboard = new X68KeyboardController(); keyboard.Connect(keys);
            var hub = new LightingHub(); hub.Attach(board, keyboard, "MSI:test|X68HE:"); return hub;
        };
        var service = new LumeService(Path.Combine(folder, "settings.json"), factory, () => new List<HidDeviceInfo>(), () => null);
        service.Scan().Wait();
        var sensors = new FakeSensors();
        var engine = new ReactiveEngine(service, sensors, false);
        Check(!sensors.Wanted.Contains("rules"), "sensors idle while there are no rules");
        var before = Zone(service, "X68HE").CurrentColor;

        var rule = new ReactiveRule { Id = "r1", Name = "CPU no teclado", Sensor = "cpu_load", Keys = new List<string> { "X68HE" }, Min = 0, Max = 100,
            Mode = "color", ColorLow = "#00FF00", ColorMid = "#FFFF00", ColorHigh = "#FF0000", Brightness = 100, AlertAbove = 90, Enabled = true };
        service.Mutate(s => { s.Reactions.Add(rule); s.ReactionsEnabled = true; }); engine.Refresh();
        Check(sensors.Wanted.Contains("rules"), "sensors sampled once a rule is active");
        Check(service.Settings.Reactions.Count == 1, "rule persisted and validated");

        sensors.Values["cpu_load"] = 0; engine.Step(1); Settle(service);
        var k = Zone(service, "X68HE"); Check(k.CurrentColor.G == 255 && k.CurrentColor.R == 0 && k.Effect == "Static", "CPU 0% -> keyboard green");
        int writes = hid.Writes;
        engine.Step(1); Settle(service); Check(hid.Writes == writes, "unchanged value sends nothing");
        for (int i = 0; i < 12; i++) { sensors.Values["cpu_load"] = 100; engine.Step(1); Settle(service); }
        k = Zone(service, "X68HE"); Check(k.CurrentColor.R == 255 && k.CurrentColor.G < 10, "CPU 100% -> keyboard red (after smoothing)");
        Check(k.Effect == "Breathing", "alert above 90% makes the keyboard breathe");
        Check(hid.Writes == writes, "keyboard-only rule never writes to the board");

        var mid = ReactiveEngine.Evaluate(rule, "X68HE", 50); Check(mid.Color.R == 255 && mid.Color.G == 255 && mid.Color.B == 0, "50% hits the middle colour");

        // Board zone in color mode + GPU temperature
        service.Mutate(s => s.Reactions.Add(new ReactiveRule { Id = "r2", Name = "GPU nas ventoinhas", Sensor = "gpu_temp", Keys = new List<string> { "JRAINBOW1" },
            Min = 40, Max = 80, Mode = "brightness", ColorLow = "#000000", ColorHigh = "#3366FF", Brightness = 100, Enabled = true }));
        engine.Refresh();
        sensors.Values["gpu_temp"] = 60; for (int i = 0; i < 12; i++) { engine.Step(1); Settle(service); }
        var fan = Zone(service, "JRAINBOW1"); Check(fan.CurrentColor.B == 0xFF && fan.CurrentBrightnessPercent == 50, "brightness mode: 60°C in 40–80 -> 50%");

        // Missing sensor: zone left alone
        service.Mutate(s => s.Reactions.Add(new ReactiveRule { Id = "r3", Name = "CPU W", Sensor = "cpu_power", Keys = new List<string> { "JRGB2" },
            Min = 10, Max = 100, Mode = "color", ColorLow = "#000011", ColorHigh = "#FFFFFF", Brightness = 100, Enabled = true }));
        engine.Refresh(); var jrgb2 = Zone(service, "JRGB2").CurrentColor; engine.Step(1); Settle(service);
        Check(Zone(service, "JRGB2").CurrentColor == jrgb2, "unavailable sensor leaves its zones unchanged");

        // Power off: engine pauses
        service.SetPower(false).Wait(); writes = hid.Writes; sensors.Values["cpu_load"] = 0; engine.Step(1); Settle(service);
        Check(hid.Writes == writes && service.Snapshot().Power == "off", "no reactive writes while everything is off");
        service.SetPower(true).Wait(); for (int i = 0; i < 12; i++) { engine.Step(1); Settle(service); }
        Check(Zone(service, "X68HE").CurrentColor.G == 255, "after Ligar the rule takes over again");

        // (rhythm tests run before switching off)
        // Disabling restores what was there before reactions started
        service.Mutate(s => s.ReactionsEnabled = false); engine.Refresh(); Settle(service);
        Check(Zone(service, "X68HE").CurrentColor == before, "switching reactions off restores the previous keyboard colour");
        Check(!sensors.Wanted.Contains("rules"), "sensors released when reactions are off");

        // Rhythm: fast path keeps up with many updates per second and glides between values.
        service.Mutate(s => { s.ReactionsEnabled = true; s.ReactiveRate = 30; s.ReactiveSmoothing = 0; }); engine.Refresh();
        int keyboardWrites = 0; int last = keys.State[6] << 16 | keys.State[7] << 8 | keys.State[8];
        for (int i = 0; i <= 30; i++) { sensors.Values["cpu_load"] = i * 100.0 / 30; engine.Step(1 / 30.0); Settle(service); int rgb = keys.State[6] << 16 | keys.State[7] << 8 | keys.State[8]; if (rgb != last) { keyboardWrites++; last = rgb; } }
        Check(keyboardWrites >= 25, "30 updates/s with smoothing 0: keyboard follows almost every step (" + keyboardWrites + " distinct colours)");
        Check(Zone(service, "X68HE").CurrentColor.R == 255 && keys.State[6] == 255, "fast path state mirrors the hardware");
        service.Mutate(s => s.ReactiveSmoothing = 100); engine.Refresh();
        sensors.Values["cpu_load"] = 0; engine.Step(1 / 30.0); engine.Step(1 / 30.0); Settle(service);
        var glide = Zone(service, "X68HE").CurrentColor;
        Check(glide.R > 200, "smoothing 100: a sudden drop fades instead of jumping (R=" + glide.R + ")");
        for (int i = 0; i < 600; i++) engine.Step(1 / 30.0); Settle(service);
        var arrived = Zone(service, "X68HE"); Check(arrived.CurrentColor.G == 255 && arrived.CurrentColor.R < 10, "…and arrives at the new colour after ~20 s of 30 Hz steps");
        Check(ReactiveEngine.TimeConstant(0) == 0 && ReactiveEngine.TimeConstant(100) > 2, "smoothing maps to a time constant");
        service.Mutate(s => s.ReactiveSmoothing = 0); engine.Refresh();
        int boardBefore = hid.Writes; var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 60; i++) { sensors.Values["gpu_temp"] = 40 + (i % 2) * 30; engine.Step(1 / 30.0); Settle(service); Thread.Sleep(5); }
        double boardPerSecond = (hid.Writes - boardBefore) / 2.0 / watch.Elapsed.TotalSeconds;
        Check(hid.Writes > boardBefore && boardPerSecond <= ReactiveEngine.BoardMaxPerSecond + 1, "board follows a flickering sensor but never above its pace (" + boardPerSecond.ToString("0.0") + "/s)");
        bool rejected = false; try { service.Mutate(s => s.Reactions.Add(new ReactiveRule { Id = "bad", Name = "x", Sensor = "cpu_load", Keys = new List<string>(), Min = 5, Max = 5, Mode = "color", ColorLow = "#000000", ColorHigh = "#FFFFFF" })); } catch { rejected = true; }
        Check(rejected, "rule with empty range rejected");
        engine.Dispose(); service.Dispose();
        Console.WriteLine("ALL REACTIVE TESTS PASSED");
    }
}
