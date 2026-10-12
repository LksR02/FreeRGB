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
        public byte[] State; public int ModeWrites; public byte[] Frame = new byte[378]; public int Frames; public bool Fail, RefuseLive;
        // Colour the keyboard is actually showing: streamed frame in live mode, parameters otherwise.
        public int Rgb { get { return State[2] == 21 ? Frame[3] << 16 | Frame[4] << 8 | Frame[5] : State[6] << 16 | State[7] << 8 | State[8]; } }
        public FakeKeys() { State = new byte[65]; State[1] = 0x87; State[2] = 2; State[3] = 2; State[4] = 3; State[5] = 0xA7; State[6] = 10; State[7] = 200; State[8] = 90; }
        public byte[] GetFeature() { if (Fail) throw new IOException("unplugged"); return (byte[])State.Clone(); }
        public void SetFeature(byte[] report)
        {
            if (Fail) throw new IOException("unplugged");
            X68WindowsHidTransport.ValidateOutgoingReport(report);
            if (report[1] == 7 && RefuseLive && report[2] == 21) { ModeWrites++; return; }
            if (report[1] == 7) { ModeWrites++; for (int i = 2; i <= 8; i++) State[i] = report[i]; }
            if (report[1] == 0x0F) { Buffer.BlockCopy(report, 9, Frame, report[3] * 56, Math.Min(56, 378 - report[3] * 56)); if (report[5] == 1) Frames++; }
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
        var k = Zone(service, "X68HE"); Check(k.CurrentColor.G == 255 && k.CurrentColor.R == 0 && k.Effect == LumeService.StreamEffect && (keys.Rgb >> 8 & 255) == 255, "CPU 0% -> keyboard green (streamed)");
        int writes = hid.Writes;
        engine.Step(1); Settle(service); Check(hid.Writes == writes, "unchanged value sends nothing");
        for (int i = 0; i < 12; i++) { sensors.Values["cpu_load"] = 100; engine.Step(1); Settle(service); }
        k = Zone(service, "X68HE"); Check(k.CurrentColor.R == 255 && k.CurrentColor.G < 10, "CPU 100% -> keyboard red (after smoothing)");
        int low = 100, high = 0;
        for (int i = 0; i < 40; i++) { engine.Step(1 / 30.0); Settle(service); System.Threading.Thread.Sleep(35); int b = Zone(service, "X68HE").CurrentBrightnessPercent; low = Math.Min(low, b); high = Math.Max(high, b); }
        Check(high - low > 40 && keys.State[2] == 21, "alert above 90%: the streamed keyboard breathes smoothly without leaving live mode (" + low + "–" + high + "%)");
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
        int keyboardWrites = 0; int last = keys.Rgb, modeBefore = keys.ModeWrites;
        for (int i = 0; i <= 30; i++) { sensors.Values["cpu_load"] = i * 100.0 / 30; engine.Step(1 / 30.0); Settle(service); int rgb = keys.Rgb; if (rgb != last) { keyboardWrites++; last = rgb; } }
        Check(keys.State[2] == 21 && keys.ModeWrites - modeBefore <= 1, "keyboard streams in its native live mode: at most one mode command, then only colour frames (no blinking)");
        bool uniform = true; for (int i = 0; i < 378; i += 3) if (keys.Frame[i] != keys.Frame[0] || keys.Frame[i + 1] != keys.Frame[1] || keys.Frame[i + 2] != keys.Frame[2]) uniform = false;
        Check(uniform, "every one of the 126 cells receives the same colour");
        Check(keyboardWrites >= 25, "30 updates/s with smoothing 0: keyboard follows almost every step (" + keyboardWrites + " distinct colours)");
        var mirrored = Zone(service, "X68HE");
        Check(mirrored.CurrentColor.R == 255 && (keys.Rgb >> 16) == (int)Math.Round(255 * mirrored.CurrentBrightnessPercent / 100.0), "fast path state mirrors the hardware (frame = colour x brightness)");
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
        // A zone taken out of every enabled rule goes back to its pre-reaction colour while other rules keep running.
        service.Mutate(s => { s.ReactiveSmoothing = 0; }); engine.Refresh();
        service.Mutate(s => s.Reactions.First(r => r.Id == "r1").Keys.Clear()); engine.Refresh();
        engine.Step(1); Settle(service);
        Check(Zone(service, "X68HE").CurrentColor == before, "zone removed from a still-active reaction is restored");
        Check(Zone(service, "JRAINBOW1").CurrentColor.B == 0xFF, "other reactions keep their zones");
        // Reactions switched off while everything is off: "Ligar" brings back the pre-reaction colours, not the reactive ones.
        service.Mutate(s => s.Reactions.First(r => r.Id == "r1").Keys.Add("X68HE")); engine.Refresh();
        sensors.Values["cpu_load"] = 100; engine.Step(1); Settle(service);
        Check(Zone(service, "X68HE").CurrentColor.R == 255, "keyboard reactive again");
        service.SetPower(false).Wait(); engine.Step(1);
        service.Mutate(s => s.ReactionsEnabled = false); engine.Refresh(); Settle(service);
        service.SetPower(true).Wait();
        Check(Zone(service, "X68HE").CurrentColor == before, "disabling reactions while off: Ligar restores the pre-reaction colour");
        bool rejected = false; try { service.Mutate(s => s.Reactions.Add(new ReactiveRule { Id = "bad", Name = "x", Sensor = "cpu_load", Keys = new List<string>(), Min = 5, Max = 5, Mode = "color", ColorLow = "#000000", ColorHigh = "#FFFFFF" })); } catch { rejected = true; }
        Check(rejected, "rule with empty range rejected");
        // Compatible mode (user switch): the keyboard gets classic commands, never the live mode.
        service.Mutate(s => { s.ReactionsEnabled = true; s.KeyboardCompat = true; }); engine.Refresh();
        sensors.Values["cpu_load"] = 0; for (int i = 0; i < 3; i++) { engine.Step(1); Settle(service); }
        Check(keys.State[2] != 21 && keys.State[7] == 255 && Zone(service, "X68HE").Effect != LumeService.StreamEffect && !service.KeyboardLive, "compatible mode: classic keyboard commands, no live mode");
        service.Mutate(s => s.KeyboardCompat = false); engine.Refresh();
        for (int i = 0; i < 3; i++) { engine.Step(1); Settle(service); }
        Check(keys.State[2] == 21 && Zone(service, "X68HE").Effect == LumeService.StreamEffect, "live mode back on: keyboard streams again");
        // Closing the app while the keyboard streams gives it back its pre-reaction look (no frozen frame left behind).
        engine.Dispose(); Settle(service);
        Check(keys.State[2] != 21 && Zone(service, "X68HE").CurrentColor == before, "closing the Lume restores the keyboard from the live mode");
        service.Dispose();

        // A keyboard left in the live mode (Lume closed abruptly) is healed at start when no reaction uses it.
        var stuck = new FakeKeys(); stuck.State[2] = 21; stuck.State[5] = 0; stuck.State[6] = 1; stuck.State[7] = 2; stuck.State[8] = 3;
        string folder2 = Path.Combine(Path.GetTempPath(), "Lume-reactive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder2);
        Func<List<HidDeviceInfo>, ILightingBackend> factory2 = found => {
            var board = new MsiRgbController(); board.Connect(new FakeHid(Fixture()));
            var keyboard = new X68KeyboardController(); keyboard.Connect(stuck);
            var hub = new LightingHub(); hub.Attach(board, keyboard, "MSI:test|X68HE:"); return hub;
        };
        var service2 = new LumeService(Path.Combine(folder2, "settings.json"), factory2, () => new List<HidDeviceInfo>(), () => null);
        service2.Scan().Wait();
        Check(stuck.State[2] == 1 && stuck.State[4] == 4 && Zone(service2, "X68HE").Effect == "Static", "keyboard stuck in live mode at start is brought back to a normal colour");

        // One device stops answering: the other keeps working and the next action reconnects it.
        stuck.Fail = true;
        try { service2.ApplyColorToAll("#FF0000", "Static", 100, 0, null).Wait(); } catch { }
        var zones2 = service2.Snapshot().Zones;
        Check(zones2.Any(z => z.Key == "JRGB1") && service2.Snapshot().Connected, "keyboard failure does not take the board zones down");
        stuck.Fail = false; Thread.Sleep(5600); Settle(service2);
        Check(service2.Snapshot().Zones.Any(z => z.Key == "X68HE"), "keyboard reconnected in the background once it answers again");
        service2.Apply(new List<ZoneSetting> { new ZoneSetting { Key = "X68HE", Color = Color.Blue, BrightnessPercent = 100, Effect = "Static", SpeedPercent = 50 } }).Wait();
        Check(stuck.State[8] == 255 && Zone(service2, "X68HE").CurrentColor.B == 255, "next action reconnects the keyboard and applies the colour");

        // A keyboard that refuses the live mode falls back to classic commands by itself.
        stuck.RefuseLive = true; service2.Scan().Wait();
        var sensors2 = new FakeSensors(); sensors2.Values["cpu_load"] = 0;
        service2.Mutate(s => { s.Reactions.Add(new ReactiveRule { Id = "k", Name = "CPU", Sensor = "cpu_load", Keys = new List<string> { "X68HE" }, Min = 0, Max = 100,
            Mode = "color", ColorLow = "#00FF00", ColorHigh = "#FF0000", Brightness = 100, Enabled = true }); s.ReactionsEnabled = true; s.ReactiveSmoothing = 0; });
        var engine2 = new ReactiveEngine(service2, sensors2, false);
        for (int i = 0; i < 4; i++) { engine2.Step(1); Settle(service2); }
        Check(stuck.State[2] != 21 && stuck.State[7] == 255 && !service2.KeyboardLive, "keyboard refusing the live mode still follows the reaction (classic commands)");
        sensors2.Values["cpu_load"] = 100; for (int i = 0; i < 4; i++) { engine2.Step(1); Settle(service2); }
        Check(stuck.State[6] == 255 && stuck.State[7] == 0, "…and keeps changing colour");
        engine2.Dispose(); service2.Dispose();
        Console.WriteLine("ALL REACTIVE TESTS PASSED");
    }
}
