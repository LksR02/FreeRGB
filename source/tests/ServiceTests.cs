// SPDX-License-Identifier: GPL-2.0-or-later
// Hardware-free tests for the switch-everything-off/on path used by the desktop widgets.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using LumeRgb;

class ServiceTests
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
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
    static void Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Lume-service-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "settings.json");
        var hid = new FakeHid(Fixture()); var keys = new FakeKeys(); byte[] original = (byte[])hid.State.Clone();
        Func<List<HidDeviceInfo>, ILightingBackend> factory = found => {
            var board = new MsiRgbController(); board.Connect(hid);
            var keyboard = new X68KeyboardController(); keyboard.Connect(keys);
            var hub = new LightingHub(); hub.Attach(board, keyboard, "MSI:7E3024102203|X68HE:"); return hub;
        };
        var service = new LumeService(path, factory, () => new List<HidDeviceInfo>(), () => null);
        service.Scan().Wait();
        var s = service.Snapshot();
        Check(s.Connected && s.Zones.Count == 5 && s.Power == "on", "detects five zones, initially on");
        Check(s.Zones.First(z => z.Key == "JRGB1").Effect == "Breathing" && s.Zones.First(z => z.Key == "JRAINBOW2").Effect == "Flashing", "decodes per-header effects");

        service.SetPower(false).Wait(); s = service.Snapshot();
        Check(s.Power == "off" && s.Zones.All(z => z.CurrentBrightnessPercent == 0), "Desligar sets every zone to brightness 0");
        Check(keys.State[4] == 0 && keys.State[2] == 2, "keyboard off keeps its effect");
        Check(hid.State[184] == 0, "never writes the flash-save flag");
        var memory = SettingsStore.Load(path).PowerMemory;
        Check(memory != null && memory.Zones.Count == 5 && memory.BoardReportBase64 != null, "state remembered on disk before switching off");

        service.SetPower(true).Wait(); s = service.Snapshot();
        Check(s.Power == "on", "Ligar turns everything back on");
        Check(MsiRgbController.SnapshotMatches(original, hid.State), "board restored exactly (effects, colors, brightness)");
        Check(keys.State[4] == 3 && keys.State[6] == 10 && keys.State[7] == 200, "keyboard brightness and color restored");
        Check(SettingsStore.Load(path).PowerMemory == null, "memory cleared after restoring");

        service.TogglePower().Wait(); Check(service.Snapshot().Power == "off", "toggle from on -> off");
        service.TogglePower().Wait(); Check(service.Snapshot().Power == "on", "toggle from off -> on");

        // Rapid clicks coalesce into the latest wish instead of queueing a long sequence.
        int before = hid.Writes; var a = service.SetPower(false); var b = service.SetPower(true); var c = service.SetPower(false);
        System.Threading.Tasks.Task.WaitAll(a, b, c); Check(service.Snapshot().Power == "off", "rapid toggles end in the last requested state");
        Check(hid.Writes - before <= 4, "rapid toggles do not replay every click");

        // While off, explicitly applying a colour turns those zones on and replaces the memory.
        service.Apply(new List<ZoneSetting> { new ZoneSetting { Key = "JRAINBOW1", Color = Color.Red, BrightnessPercent = 100, Effect = "Static", SpeedPercent = 50 } }).Wait();
        s = service.Snapshot(); Check(s.Power == "mixed", "partial change while off gives mixed state");
        var mem = SettingsStore.Load(path).PowerMemory; Check(mem != null && mem.BoardReportBase64 == null && !mem.Zones.Any(z => z.Key == "JRAINBOW1"), "applied zone dropped from power memory");
        service.SetPower(true).Wait(); s = service.Snapshot();
        Check(s.Power == "on" && s.Zones.First(z => z.Key == "JRAINBOW1").CurrentColor.R == 255, "Ligar keeps the explicit change and restores the rest");

        service.ApplyScene("ocean").Wait(); s = service.Snapshot();
        Check(s.Zones.All(z => z.CurrentColor.B == 0xFF && z.Effect == "Breathing"), "scene applies colour + effect to every zone");
        service.ApplyScene("storm").Wait(); s = service.Snapshot();
        Check(s.Zones.First(z => z.Key == "X68HE").Effect == "Static" && s.Zones.First(z => z.Key == "JRGB1").Effect == "Lightning", "keyboard falls back to a supported effect");

        service.SaveProfile("Noite").Wait(); service.SetPower(false).Wait(); service.ApplyProfile("Noite").Wait();
        Check(service.Snapshot().Power == "on" && SettingsStore.Load(path).Profiles.Count == 1, "profile save/apply");
        service.RenameZone("JRAINBOW1", "Ventoinhas"); Check(service.Snapshot().Zones.First(z => z.Key == "JRAINBOW1").Name == "Ventoinhas", "rename zone");
        service.Mutate(x => x.Widgets.Add(new WidgetConfig { Id = "w1", Style = "orb", X = 10, Y = 20 }));
        Check(SettingsStore.Load(path).Widgets.Count == 1, "widget persisted");
        bool rejected = false; try { service.Mutate(x => x.Widgets.Add(new WidgetConfig { Id = "w1", Style = "bad" })); } catch { rejected = true; }
        Check(rejected && service.Settings.Widgets.Count == 1, "invalid widget rejected and rolled back");
        File.WriteAllText(path + ".old.json", "{\"Profiles\":[],\"Aliases\":[],\"Palette\":[\"#112233\"],\"AlwaysOnTop\":true}");
        var old = SettingsStore.Load(path + ".old.json"); Check(old.Widgets != null && old.Palette.Count == 1 && old.AlwaysOnTop, "loads settings from the previous version");
        service.Dispose();
        Console.WriteLine("ALL SERVICE TESTS PASSED");
    }
}
