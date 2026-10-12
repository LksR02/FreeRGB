using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using LumeRgb;
class MsiNativeTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    class FakeHid : IHidTransport
    {
        public ushort VendorId { get; set; } public ushort ProductId { get; set; }
        public string Serial { get; set; } public string Path { get; set; }
        public int FeatureReportLength { get; set; }
        public byte[] State, InitialState; public int Reads; public bool Disposed, Mismatch; public int FailWriteNumber, DelayReadbackReads;
        public List<byte[]> Writes = new List<byte[]>();
        public FakeHid(byte[] state) { VendorId = 0x0DB0; ProductId = 0x0076; Serial = "7E3024102203"; Path = "fake"; FeatureReportLength = 761; State = (byte[])state.Clone(); InitialState = (byte[])state.Clone(); }
        public byte[] GetFeature(byte id, int length)
        {
            Check(id == 0x52 && length == 185, "only requested report52"); Reads++;
            if (Writes.Count == 2 && DelayReadbackReads > 0) { DelayReadbackReads--; return (byte[])InitialState.Clone(); }
            byte[] result = (byte[])State.Clone(); if (Mismatch && Writes.Count == 2) result[2] ^= 1; return result;
        }
        public void SetFeature(byte[] report)
        {
            MsiRgbController.ValidateReport(report); Writes.Add((byte[])report.Clone());
            if (Writes.Count == FailWriteNumber) throw new IOException("fake write failure");
            State = (byte[])report.Clone();
        }
        public void Dispose() { Disposed = true; }
    }
    static byte[] Fixture()
    {
        byte[] b = new byte[185]; for (int i = 0; i < b.Length; i++) b[i] = (byte)(i ^ 0x5A);
        b[0] = 0x52; b[184] = 0; b[74] = 1; b[78] = 40; b[82] = 0x80;
        foreach (int o in new int[] { 1, 134, 31, 42 }) { b[o] = 1; b[o + 4] = 40; b[o + 9] = 0; }
        b[41] = 100; b[52] = 80; return b;
    }
    static ZoneSetting Setting(string key)
    { return new ZoneSetting { Key = key, Color = Color.FromArgb(12, 34, 56), BrightnessPercent = 65, Effect = "Static", SpeedPercent = 100 }; }
    static List<ZoneSetting> One(string key) { return new List<ZoneSetting> { Setting(key) }; }
    static List<ZoneSetting> All()
    { return new List<ZoneSetting> { Setting("JRGB1"), Setting("JRGB2"), Setting("JRAINBOW1"), Setting("JRAINBOW2") }; }
    static void Main()
    {
        byte[] original = Fixture();
        Run("exact four MS7E30 headers and brightness decode", delegate()
        {
            List<RgbZone> z = MsiRgbController.DecodeZones(original);
            Check(z.Count == 4 && z[1].Key == "JRGB2" && z[3].Key == "JRAINBOW2", "actual native headers");
            Check(z[2].LedCount == 100 && z[3].LedCount == 80 && z[0].CurrentBrightnessPercent == 100, "settings decode");
        });
        Run("selected Rainbow header preservation", delegate()
        {
            byte[] n = MsiRgbController.BuildReport(original, One("JRAINBOW1"));
            for (int i = 0; i < 185; i++) if (i < 31 || i > 40) Check(original[i] == n[i], "unrelated byte changed at " + i);
            Check(n[32] == 12 && n[33] == 34 && n[34] == 56 && n[36] == 12 && n[37] == 34 && n[38] == 56, "RGB1/2");
            Check((n[35] & 0x7C) == 28 && (n[35] & 3) == 2 && n[40] == 0, "65% rounds to hardware70% plus speed2");
            Check((n[39] & 0x80) != 0 && (n[39] & 0x7F) == (original[39] & 0x7F), "preserve unrelated color flags");
        });
        Run("mixed-mode JRGB2 uses134 and required hidden master", delegate()
        {
            byte[] n = MsiRgbController.BuildReport(original, One("JRGB2"));
            Check(n[135] == 12 && n[136] == 34 && n[137] == 56, "mixed-mode header color");
            for (int i = 174; i < 184; i++) Check(n[i] == original[i], "legacy JRGB2 offset must be untouched");
            Check(n[74] == 1 && n[75] == 12 && n[79] == 12 && (n[82] & 1) != 0 && n[83] == 0, "hidden master protocol fields");
            for (int i = 0; i < 185; i++) if ((i < 134 || i > 143) && (i < 74 || i > 83)) Check(n[i] == original[i], "unselected byte changed");
        });
        Run("brightness mask and speed preserve reserved bit", delegate()
        {
            byte[] b = (byte[])original.Clone(); b[5] = 0x80 | 40;
            ZoneSetting s = Setting("JRGB1"); s.BrightnessPercent = 0; s.SpeedPercent = 0;
            byte[] n = MsiRgbController.BuildReport(b, new List<ZoneSetting> { s }); Check(n[5] == 0x80, "off uses zero brightness without clearing reservedbit");
            s.BrightnessPercent = 100; s.SpeedPercent = 50; n = MsiRgbController.BuildReport(b, new List<ZoneSetting> { s });
            Check(n[5] == (0x80 | 40 | 1), "max brightness + medium speed");
        });
        Run("five native effect codes1..5", delegate()
        {
            string[] names = MsiRgbController.SupportedEffects;
            Check(names.Length == 5, "five audited effects");
            for (int i = 0; i < names.Length; i++) { ZoneSetting s = Setting("JRGB1"); s.Effect = names[i]; Check(MsiRgbController.BuildReport(original, new List<ZoneSetting> { s })[1] == i + 1, "native effect id"); }
        });
        Run("reject partial transition from direct/global synchronization", delegate()
        {
            byte[] b = (byte[])original.Clone(); b[74] = 0x25; Throws<NotSupportedException>(delegate { MsiRgbController.BuildReport(b, One("JRAINBOW1")); });
            byte[] n = MsiRgbController.BuildReport(b, All()); Check(n[74] == 1 && (n[78] & 0x80) == 0 && (n[82] & 0x30) == 0, "exit direct through all headers");
            b[74] = 26; b[78] |= 0x80; b[82] |= 0x30; Throws<NotSupportedException>(delegate { MsiRgbController.BuildReport(b, One("JRGB1")); });
            n = MsiRgbController.BuildReport(b, All()); Check((n[78] & 0x80) == 0 && (n[82] & 0x30) == 0, "clear obsolete global sync");
        });
        Run("reject EEPROM, wrong report, short frame and unsafe native padding", delegate()
        {
            byte[] flash = (byte[])original.Clone(); flash[184] = 1;
            Throws<InvalidDataException>(delegate { MsiRgbController.BuildReport(flash, All()); });
            Throws<InvalidDataException>(delegate { WindowsHidTransport.PrepareNativeFeature(flash, 761); });
            Throws<InvalidDataException>(delegate { MsiRgbController.ValidateReport(new byte[184]); });
            byte[] wrong = (byte[])original.Clone(); wrong[0] = 0x53; Throws<InvalidDataException>(delegate { MsiRgbController.ValidateReport(wrong); });
            byte[] padded = WindowsHidTransport.PrepareNativeFeature(original, 761);
            Check(padded.Length == 761, "Windowscaps length");
            for (int i = 0; i < 185; i++) Check(padded[i] == original[i], "protocolframe unchanged");
            for (int i = 185; i < 761; i++) Check(padded[i] == 0, "Windowspadding must be zero");
            Throws<ArgumentException>(delegate { WindowsHidTransport.PrepareNativeFeature(original, 184); });
        });
        Run("wrong board identity rejected before GET or SET", delegate()
        {
            foreach (int condition in new int[] { 0, 1, 2, 3, 4 })
            {
                FakeHid t = new FakeHid(original);
                if (condition == 0) t.VendorId = 1; if (condition == 1) t.ProductId = 0x7E30;
                if (condition == 2) t.Serial = "7D750000"; if (condition == 3) t.Serial = "bad"; if (condition == 4) t.Serial = "GGGG00";
                using (MsiRgbController c = new MsiRgbController()) Throws<NotSupportedException>(delegate { c.Connect(t); });
                Check(t.Reads == 0 && t.Writes.Count == 0 && t.Disposed, "identity rejected before reports");
            }
        });
        Run("native sequence old then new and verified readback", delegate()
        {
            FakeHid t = new FakeHid(original); using (MsiRgbController c = new MsiRgbController())
            {
                c.Connect(t); Check(t.Reads == 1 && t.Writes.Count == 0, "connect read-only");
                c.GetZones(); Check(t.Reads == 2, "fresh zones GET");
                c.Apply(One("JRGB1")); Check(t.Writes.Count == 2 && t.Reads == 4 && c.LastWriteVerified, "old/new/readback sequence");
                for (int i = 0; i < 185; i++) Check(t.Writes[0][i] == original[i], "old state first");
                Check(t.Writes[1][2] == 12 && t.Writes[0][184] == 0 && t.Writes[1][184] == 0, "no flash flag");
            }
        });
        Run("invalid batch never writes and validates all selections first", delegate()
        {
            FakeHid t = new FakeHid(original); using (MsiRgbController c = new MsiRgbController())
            {
                c.Connect(t); List<ZoneSetting> batch = One("JRGB1"); batch.Add(Setting("ONBOARD"));
                Throws<ArgumentException>(delegate { c.Apply(batch); }); Check(t.Reads == 1 && t.Writes.Count == 0, "validate before read/write");
                batch = One("JRGB1"); batch.Add(Setting("JRGB1")); Throws<ArgumentException>(delegate { c.Apply(batch); });
                batch = One("JRGB1"); batch[0].BrightnessPercent = 101; Throws<ArgumentException>(delegate { c.Apply(batch); });
                Check(t.Writes.Count == 0 && c.IsConnected, "invalid config leaves hardware intact");
            }
        });
        Run("stale readback polls GET without repeating writes", delegate()
        {
            FakeHid t = new FakeHid(original); t.DelayReadbackReads = 1;
            using (MsiRgbController c = new MsiRgbController())
            {
                c.Connect(t); c.Apply(One("JRGB1"));
                Check(c.LastWriteVerified && t.Reads == 4 && t.Writes.Count == 2, "stale-once must add GET only");
                Check(c.GetReportSnapshot()[2] == 12, "snapshot must use final confirmed state");
            }
        });
        Run("mismatch and write failure never confirm", delegate()
        {
            FakeHid mismatch = new FakeHid(original); mismatch.Mismatch = true;
            using (MsiRgbController c = new MsiRgbController())
            { c.Connect(mismatch); Throws<IOException>(delegate { c.Apply(One("JRGB1")); }); Check(!c.LastWriteVerified && !c.IsConnected, "readback mismatch"); }
            FakeHid failed = new FakeHid(original); failed.FailWriteNumber = 2;
            using (MsiRgbController c = new MsiRgbController())
            { c.Connect(failed); Throws<IOException>(delegate { c.Apply(One("JRGB1")); }); Check(!c.LastWriteVerified && !c.IsConnected && failed.Disposed, "second write failure"); }
        });
        string live = "outputs/LumeRGB-Independente/evidencias/msi-report52-antes.bin";
        if (!File.Exists(live)) live = "evidencias/msi-report52-antes.bin";
        if (File.Exists(live)) Run("actual native read-only fixture parses and all-header transition", delegate()
        {
            byte[] b = File.ReadAllBytes(live); List<RgbZone> zones = MsiRgbController.DecodeZones(b);
            Check(zones.Count == 4 && zones[2].LedCount == 100 && zones[3].LedCount == 100, "captured header settings");
            byte[] built = MsiRgbController.BuildReport(b, All());
            Check(built[0] == 0x52 && built[184] == 0 && built[134] == 1 && built[31] == 1, "realframe builder");
        });
        Console.WriteLine("RESULT " + passed + " groups passed. Fake HID only; no hardware SET_FEATURE calls.");
    }
}
