using System;
using System.Drawing;
using System.IO;
using LumeRgb;

class X68KeyboardControllerTests
{
    sealed class Fake : IX68HidTransport
    {
        public ushort VendorId { get; set; } public ushort ProductId { get; set; }
        public ushort UsagePage { get; set; } public ushort Usage { get; set; }
        public int FeatureReportLength { get; set; } public string ProductName { get; set; }
        public byte[] State, LastWrite, PendingState; public int QueryCount, LightingWrites, StaleReads; public bool Disposed, BadReadback;
        public Fake() { VendorId = 0x3151; ProductId = 0x502D; UsagePage = 0xFFFF; Usage = 2; FeatureReportLength = 65; ProductName = "X68HE";
            State = new byte[65]; State[1] = 0x87; State[2] = 1; State[3] = 3; State[4] = 4; State[5] = 0xA7; State[6] = 24; State[7] = 56; State[8] = 78; }
        public byte[] GetFeature()
        {
            byte[] result = (byte[])State.Clone();
            if (PendingState != null && StaleReads-- <= 0) { State = PendingState; PendingState = null; result = (byte[])State.Clone(); }
            return result;
        }
        public void SetFeature(byte[] report)
        {
            X68WindowsHidTransport.ValidateOutgoingReport(report);
            if (report[1] == 0x87) { QueryCount++; return; }
            LightingWrites++; LastWrite = (byte[])report.Clone();
            if (!BadReadback)
            {
                byte[] target = (byte[])State.Clone(); for (int i = 2; i <= 8; i++) target[i] = report[i];
                if (StaleReads > 0) PendingState = target; else State = target;
            }
        }
        public void Dispose() { Disposed = true; }
    }
    static int count;
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); count++; Console.WriteLine("PASS " + name); }
    static void Throws<T>(Action action, string name) where T : Exception { bool ok = false; try { action(); } catch(T) { ok = true; } Assert(ok,name); }
    static ZoneSetting Setting(int brightness, string effect, int speed) { return new ZoneSetting { Key = "X68HE", Color = Color.FromArgb(123, 45, 67), BrightnessPercent = brightness, Effect = effect, SpeedPercent = speed }; }
    static void Main()
    {
        Fake fake = new Fake(); X68KeyboardController controller = new X68KeyboardController(); controller.Connect(fake);
        Assert(fake.QueryCount == 1 && fake.LightingWrites == 0, "connect uses only GET query and never lighting change");
        byte[] query = X68KeyboardController.BuildQuery(); Assert(query.Length == 65 && query[0] == 0 && query[1] == 0x87 && query[8] == 0x78, "GET report framing and Bit7 checksum");
        X68WindowsHidTransport.ValidateOutgoingReport(query);
        RgbZone zone = controller.GetZone(); Assert(zone.Key == "X68HE" && zone.CurrentBrightnessPercent == 100 && zone.CurrentSpeedPercent == 25 && zone.CurrentColor.R == 24, "decode native state");
        for (int raw = 0; raw <= 4; raw++) { controller.Apply(Setting(raw * 25,"Static",100)); Assert(fake.LastWrite[4] == raw && fake.LastWrite[3] == 3 && fake.LastWrite[5] == 0xA7 && controller.LastWriteVerified, "brightness level " + raw + ", preserve static speed and option"); }
        controller.Apply(Setting(63,"Breathing",100)); Assert(fake.LastWrite[2] == 2 && fake.LastWrite[3] == 0 && fake.LastWrite[4] == 3, "breathing native speed and brightness quantization");
        int sum = 0; for (int i = 1; i <= 9; i++) sum += fake.LastWrite[i]; Assert((sum & 255) == 255, "SET Bit8 checksum");
        bool padded = true; for (int i = 10; i < 65; i++) if (fake.LastWrite[i] != 0) padded = false; Assert(padded, "all unused native report bytes are zero");
        int previousQueries = fake.QueryCount, previousWrites = fake.LightingWrites;
        Throws<NotSupportedException>(()=>controller.Apply(Setting(50,"Flashing",50)),"MSI-only effects rejected before traffic");
        Throws<ArgumentException>(()=>controller.Apply(Setting(101,"Static",50)),"invalid brightness rejected before traffic");
        Assert(fake.QueryCount == previousQueries && fake.LightingWrites == previousWrites,"invalid setting causes zero requests");
        byte[] bad = (byte[])fake.LastWrite.Clone(); bad[9] ^= 1; Throws<ArgumentException>(()=>X68WindowsHidTransport.ValidateOutgoingReport(bad),"corrupt outgoing checksum refused");
        bad = (byte[])fake.LastWrite.Clone(); bad[64] = 1; Throws<ArgumentException>(()=>X68WindowsHidTransport.ValidateOutgoingReport(bad),"nonzero outgoing padding refused");
        controller.Dispose(); Assert(fake.Disposed,"transport ownership disposal");
        Fake stale = new Fake(); controller = new X68KeyboardController(); controller.Connect(stale); stale.StaleReads = 1;
        controller.Apply(Setting(25,"Static",50)); Assert(controller.LastWriteVerified && stale.LightingWrites == 1 && stale.QueryCount >= 4,"stale first readback retries GET without repeating SET"); controller.Dispose();
        Fake wrong = new Fake(); wrong.VendorId = 0x046D; Throws<NotSupportedException>(()=>new X68KeyboardController().Connect(wrong),"wrong actual vendor refused"); Assert(wrong.QueryCount == 0 && wrong.Disposed,"wrong vendor causes zero traffic");
        wrong = new Fake(); wrong.Usage = 1; Throws<NotSupportedException>(()=>new X68KeyboardController().Connect(wrong),"wrong HID collection refused");
        wrong = new Fake(); wrong.FeatureReportLength = 64; Throws<NotSupportedException>(()=>new X68KeyboardController().Connect(wrong),"wrong descriptor length refused");
        wrong = new Fake(); wrong.ProductName = "OTHER"; Throws<NotSupportedException>(()=>new X68KeyboardController().Connect(wrong),"same IDs but wrong product refused");
        wrong = new Fake(); wrong.State[1] = 0x88; Throws<InvalidDataException>(()=>new X68KeyboardController().Connect(wrong),"wrong reply command refused");
        wrong = new Fake(); wrong.State = new byte[64]; Throws<InvalidDataException>(()=>new X68KeyboardController().Connect(wrong),"short reply refused");
        wrong = new Fake(); wrong.State[4] = 5; Throws<InvalidDataException>(()=>new X68KeyboardController().Connect(wrong),"unknown brightness refused");
        wrong = new Fake(); controller = new X68KeyboardController(); controller.Connect(wrong); wrong.BadReadback = true;
        Throws<IOException>(()=>controller.Apply(Setting(25,"Static",50)),"wrong independent readback reported"); Assert(!controller.IsConnected && wrong.Disposed,"readback failure disconnects");
        Console.WriteLine("RESULT " + count + " meaningful protocol checks passed; no hardware touched.");
    }
}
