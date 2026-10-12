// SPDX-License-Identifier: GPL-2.0-or-later
// Explicit reversible hardware verification utility. Not invoked by the app.
// Only LED parameter commands 0x87/0x07; all original controlled fields restored.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using LumeRgb;

class X68RoundTrip
{
    static byte[] Query(X68WindowsHidTransport transport)
    {
        transport.SetFeature(X68KeyboardController.BuildQuery()); Thread.Sleep(10);
        byte[] report = transport.GetFeature(); X68KeyboardController.ValidateReport(report); return report;
    }
    static string HashFields(byte[] report)
    {
        if (report == null) return null;
        byte[] fields = new byte[7]; Buffer.BlockCopy(report,2,fields,0,7);
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(fields)).Replace("-","").ToLowerInvariant();
    }
    static string Hex(byte[] report) { return report == null ? null : BitConverter.ToString(report); }
    static byte[] ExactRestore(byte[] original)
    {
        X68KeyboardController.ValidateReport(original);
        // Only modes with a documented, unambiguous custom-RGB parameter layout.
        if ((original[2] != 1 && original[2] != 2 && original[2] != 4) || (original[5] & 15) != 7)
            throw new NotSupportedException("O estado original não permite restauração exata pelo protocolo limitado deste teste. Nenhuma alteração enviada.");
        byte[] result = new byte[65]; result[1] = 7;
        Buffer.BlockCopy(original,2,result,2,7);
        int sum = 0; for (int i=1; i<=8; i++) sum += result[i]; result[9] = (byte)(255-(sum&255));
        X68WindowsHidTransport.ValidateOutgoingReport(result);
        return result;
    }
    static byte[] WaitFor(X68WindowsHidTransport transport, byte[] expected, out int polls)
    {
        Stopwatch timer = Stopwatch.StartNew(); byte[] report; polls=0;
        do { Thread.Sleep(100); report=Query(transport); polls++; if(X68KeyboardController.LightingFieldsEqual(expected,report)) return report; }
        while(timer.ElapsedMilliseconds < 1500);
        throw new IOException("A leitura não confirmou os parâmetros esperados em 1500 ms. Último relatório: " + Hex(report));
    }
    static int Main(string[] args)
    {
        byte[] before=null, after=null, restored=null, requested=null, restoreCommand=null;
        string testError=null, restoreError=null; int applyPolls=0, restorePolls=0, lightingWrites=0;
        bool lightingAttempted=false, applyVerified=false, restoreVerified=false;
        try
        {
            var devices=WindowsDevices.GetHidDevices().Where(d=>d.VendorId==0x3151&&d.ProductId==0x502D&&d.UsagePage==0xFFFF&&d.Usage==2).ToList();
            if(devices.Count!=1) throw new IOException("Esperado exatamente um X68HE na interface de configuração.");
            using(var transport=new X68WindowsHidTransport(devices[0].Path))
            {
                before=Query(transport); restoreCommand=ExactRestore(before);
                requested=X68KeyboardController.BuildReport(before,new ZoneSetting { Key="X68HE", Effect="Static", Color=Color.FromArgb(0x24,0x68,0xAC), BrightnessPercent=50, SpeedPercent=0 });
                X68WindowsHidTransport.ValidateOutgoingReport(requested);
                // Both commands completely validated before the first lighting write.
                try
                {
                    lightingAttempted=true; lightingWrites++; transport.SetFeature(requested);
                    after=WaitFor(transport,requested,out applyPolls); applyVerified=true;
                }
                catch(Exception e) { testError=e.ToString(); }
                finally
                {
                    if(lightingAttempted)
                    {
                        try { lightingWrites++; transport.SetFeature(restoreCommand); restored=WaitFor(transport,restoreCommand,out restorePolls); restoreVerified=X68KeyboardController.LightingFieldsEqual(before,restored); }
                        catch(Exception e) { restoreError=e.ToString(); }
                    }
                }
            }
        }
        catch(Exception e) { testError=e.ToString(); }
        var result=new { CapturedUtc=DateTime.UtcNow.ToString("o"), Method="Direct Windows HID X68HE VID3151:PID502D usageFFFF:0002/productX68HE revalidated from actual handle; one temporary LED setting and exact original LED fields restoration; only GET0x87/SET0x07 commands; no key maps, calibration, reset or firmware commands", AppliedColor="#2468AC", AppliedBrightnessPercent=50, ApplyVerified=applyVerified, RestoreVerified=restoreVerified, ControlledFieldsOriginalEqualRestored=restoreVerified, OriginalControlledFieldsSha256=HashFields(before), RestoredControlledFieldsSha256=HashFields(restored), BeforeHex=Hex(before), RequestedHex=Hex(requested), AfterHex=Hex(after), RestoreCommandHex=Hex(restoreCommand), RestoredHex=Hex(restored), ApplyPolls=applyPolls, RestorePolls=restorePolls, LightingSetCommandAttempts=lightingWrites, TestError=testError, RestoreError=restoreError };
        string json=new JavaScriptSerializer().Serialize(result); File.WriteAllText(args[0],json); Console.WriteLine(json);
        if(restoreError!=null || (lightingAttempted&&!restoreVerified)) { Console.Error.WriteLine("PRIORITY: original lighting restoration failed; inspect evidence immediately."); return 2; }
        return applyVerified&&restoreVerified ? 0 : 1;
    }
}
