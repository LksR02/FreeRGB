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
        public byte[] State, LastWrite, PendingState; public int QueryCount, LightingWrites, StaleReads, FrameWrites, DynamicOffWrites, GarbageReads; public bool Disposed, BadReadback, DynamicLight, RefuseLive;
        byte lastCommand = 0x87;
        public Fake() { VendorId = 0x3151; ProductId = 0x502D; UsagePage = 0xFFFF; Usage = 2; FeatureReportLength = 65; ProductName = "X68HE";
            State = new byte[65]; State[1] = 0x87; State[2] = 1; State[3] = 3; State[4] = 4; State[5] = 0xA7; State[6] = 24; State[7] = 56; State[8] = 78; }
        public byte[] GetFeature()
        {
            if (GarbageReads > 0) { GarbageReads--; byte[] junk = new byte[65]; junk[1] = 0x0F; return junk; }
            if (lastCommand == 0x94) { byte[] r = new byte[65]; r[1] = 0x94; r[2] = (byte)(DynamicLight ? 1 : 0); return r; }
            if (lastCommand == 0x8F) { byte[] r = new byte[65]; r[1] = 0x8F; r[2] = 0x21; r[3] = 0x01; return r; }
            byte[] result = (byte[])State.Clone();
            if (PendingState != null && StaleReads-- <= 0) { State = PendingState; PendingState = null; result = (byte[])State.Clone(); }
            return result;
        }
        public void SetFeature(byte[] report)
        {
            X68WindowsHidTransport.ValidateOutgoingReport(report);
            lastCommand = report[1];
            if (report[1] == 0x87) { QueryCount++; return; }
            if (report[1] == 0x8F || report[1] == 0x94) return;
            if (report[1] == 0x14) { DynamicOffWrites++; DynamicLight = report[2] == 1; return; }
            if (report[1] == 0x0F) { FrameWrites++; return; }
            LightingWrites++; LastWrite = (byte[])report.Clone();
            if (!BadReadback && !(RefuseLive && report[2] == 21))
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
        // Live colour frames (manufacturer's Dynamic RGB protocol)
        byte[] rgbFrame = new byte[378]; for (int i = 0; i < rgbFrame.Length; i++) rgbFrame[i] = (byte)i;
        var packets = X68KeyboardController.BuildSyncFrame(rgbFrame);
        Assert(packets.Count == 7, "126-cell frame splits into 7 chunks of 56 bytes");
        bool layout = true;
        for (int c = 0; c < packets.Count; c++)
        {
            var pk = packets[c]; X68WindowsHidTransport.ValidateOutgoingReport(pk);
            int frameSum = 0; for (int i = 1; i <= 7; i++) frameSum += pk[i];
            if (pk[0] != 0 || pk[1] != 0x0F || pk[2] != 1 || pk[3] != c || pk[4] != 56 || pk[5] != (c == 6 ? 1 : 0) || pk[6] != 0 || pk[7] != 0 || pk[8] != (byte)(255 - (frameSum & 255))) layout = false;
            for (int i = 0; i < Math.Min(56, 378 - c * 56); i++) if (pk[9 + i] != rgbFrame[c * 56 + i]) layout = false;
        }
        Assert(layout, "frame header, Bit7 checksum, chunk/last flags and colour bytes match the vendor app");
        var mode = X68KeyboardController.BuildSyncMode(fake.State); X68WindowsHidTransport.ValidateOutgoingReport(mode);
        Assert(mode[1] == 7 && mode[2] == 21 && mode[4] == 4 && mode[5] == 0, "live mode = native Screen color effect at full hardware brightness");
        bad = (byte[])packets[0].Clone(); bad[8] ^= 1; Throws<ArgumentException>(() => X68WindowsHidTransport.ValidateOutgoingReport(bad), "corrupt frame checksum refused");
        Fake live = new Fake(); controller = new X68KeyboardController(); controller.Connect(live);
        int before = live.LightingWrites; controller.Stream(Color.FromArgb(10, 20, 30), 50); controller.Stream(Color.FromArgb(40, 50, 60), 100);
        Assert(live.LightingWrites - before == 1 && live.State[2] == 21, "two streamed colours send the mode command only once");
        controller.Dispose();
        // Same start-up as Dynamic RGB: Windows Dynamic Lighting on the keyboard is switched off before streaming.
        live = new Fake(); live.DynamicLight = true; controller = new X68KeyboardController(); controller.Connect(live);
        controller.Stream(Color.Red, 100);
        Assert(live.DynamicOffWrites == 1 && !live.DynamicLight && live.FrameWrites >= 7, "Windows Dynamic Lighting switched off once, then frames");
        controller.Stream(Color.Blue, 100); Assert(live.DynamicOffWrites == 1, "handshake runs once per connection");
        controller.Dispose();
        live = new Fake(); controller = new X68KeyboardController(); controller.Connect(live); controller.Stream(Color.Red, 100);
        Assert(live.DynamicOffWrites == 0, "Dynamic Lighting already off: nothing changed");
        controller.Dispose();
        // Continuous stream like Dynamic RGB: ~30 frames/s from one call, no GET in between, stops when no longer fed.
        live = new Fake(); controller = new X68KeyboardController(); controller.Connect(live);
        controller.Stream(Color.Red, 100); int q0 = live.QueryCount, f0 = live.FrameWrites;
        System.Threading.Thread.Sleep(1000);
        int perSecond = (live.FrameWrites - f0) / 7;
        Assert(perSecond >= 20 && perSecond <= 35, "one colour keeps streaming at ~30 frames/s (" + perSecond + "/s)");
        controller.GetZone(); Assert(live.QueryCount == q0 && controller.IsStreaming, "no GET request while streaming (read-back uses the last state)");
        System.Threading.Thread.Sleep((X68KeyboardController.StreamHoldSeconds + 1) * 1000);
        int stopped = live.FrameWrites; System.Threading.Thread.Sleep(300);
        Assert(!controller.IsStreaming && live.FrameWrites == stopped, "stream stops by itself when nobody feeds it");
        controller.Stream(Color.Blue, 100); controller.Apply(Setting(100, "Static", 50)); stopped = live.FrameWrites; System.Threading.Thread.Sleep(300);
        Assert(!controller.IsStreaming && live.FrameWrites == stopped && live.State[2] == 1, "a normal command ends the stream first");
        controller.Dispose();
        // "overlay" variant: frames over the normal effect, never the Screen color effect (and leaves it if stuck there).
        live = new Fake(); live.State[2] = 21; live.State[5] = 0; controller = new X68KeyboardController(); controller.Connect(live); controller.LiveVariant = "overlay";
        controller.Stream(Color.Green, 100);
        Assert(live.State[2] == 1 && live.FrameWrites >= 7, "overlay variant: leaves Screen color, streams over a static effect");
        controller.Stream(Color.Red, 100); Assert(live.State[2] == 1, "overlay variant never switches to Screen color");
        controller.Dispose();
        // The 10-second keyboard test: red via Screen color, green over the normal effect, then the keyboard is restored.
        live = new Fake(); controller = new X68KeyboardController(); controller.Connect(live);
        var hub = new LightingHub(); hub.Attach(null, controller, "|X68HE:");
        byte[] original = (byte[])live.State.Clone();
        hub.TestKeyboardLive();
        Assert(live.FrameWrites / 7 >= 120, "test streams for ~6 s (" + live.FrameWrites / 7 + " frames)");
        Assert(live.State[2] == original[2] && live.State[6] == original[6] && live.State[7] == original[7] && live.State[8] == original[8] && !controller.IsStreaming, "after the test the keyboard is back to its own effect and colour");
        hub.Dispose();
        // A keyboard that refuses the live mode is reported, stays connected and sends no frames.
        live = new Fake(); live.RefuseLive = true; controller = new X68KeyboardController(); controller.Connect(live);
        Throws<NotSupportedException>(() => controller.Stream(Color.Red, 100), "refused live mode reported");
        Assert(controller.IsConnected && controller.StreamUnsupported && live.FrameWrites == 0, "refused live mode: still connected, no frames");
        int writes = live.LightingWrites; Throws<NotSupportedException>(() => controller.Stream(Color.Red, 100), "refused live mode not retried");
        Assert(live.LightingWrites == writes, "no repeated mode commands after a refusal");
        controller.ApplyFast(Setting(100, "Static", 50)); Assert(live.State[2] == 1, "classic fast command still works");
        controller.Dispose();
        // A stale/garbage reply right after a burst no longer drops the keyboard.
        live = new Fake(); controller = new X68KeyboardController(); controller.Connect(live); live.GarbageReads = 2;
        Assert(controller.GetZone().Effect == "Static" && controller.IsConnected, "garbage replies retried, keyboard kept");
        live.GarbageReads = 10; Throws<InvalidDataException>(() => controller.GetZone(), "persistent garbage still reported");
        controller.Dispose();
        Console.WriteLine("RESULT " + count + " meaningful protocol checks passed; no hardware touched.");
    }
}
