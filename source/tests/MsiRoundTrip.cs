// SPDX-License-Identifier: GPL-2.0-or-later
// Deliberate reversible hardware test, separate from the application entry point.
// Changes the four supported headers briefly and restores the exact saved report.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Threading;
using LumeRgb;

class MsiRoundTrip
{
    static string Hash(byte[] bytes)
    { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static bool Equal(byte[] a, byte[] b)
    { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static void CheckOpenRgbAbsent()
    { if (Process.GetProcessesByName("OpenRGB").Length != 0) throw new IOException("OpenRGB está em execução. Nenhum teste de escrita foi iniciado."); }
    static void Main()
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>();
        evidence["evidenceId"] = "MSI-NATIVE-ROUNDTRIP-01";
        evidence["startedAtUtc"] = DateTime.UtcNow.ToString("o");
        evidence["scope"] = "Reversible native HID test on exact MSI 0DB0:0076, serial prefix7E30: four headers Static #2468AC at40% hardware brightness, then restore original185-byte report. No TCP or persistent save flag.";
        evidence["applyAttempted"] = false; evidence["applyVerified"] = false; evidence["restoreAttempted"] = false; evidence["restoreVerified"] = false;
        byte[] original = null;
        HidDeviceInfo selected = null;
        MsiRgbController controller = null;
        bool needRestore = false;
        string applyError = null, restoreError = null;
        try
        {
            CheckOpenRgbAbsent();
            foreach (HidDeviceInfo candidate in WindowsDevices.GetHidDevices())
            {
                if (candidate.VendorId == 0x0DB0 && candidate.ProductId == 0x0076 && candidate.Serial.StartsWith("7E30", StringComparison.OrdinalIgnoreCase))
                {
                    if (selected != null) throw new IOException("Mais de um controlador MS-7E30 encontrado. Teste cancelado.");
                    selected = candidate;
                }
            }
            if (selected == null) throw new IOException("O controlador MSI MS-7E30 não foi encontrado.");
            evidence["path"] = selected.Path; evidence["serial"] = selected.Serial;
            WindowsHidTransport initial = new WindowsHidTransport(selected.Path);
            try
            {
                MsiRgbController.ValidateIdentity(initial);
                if (initial.Serial != selected.Serial) throw new IOException("O serial mudou durante a descoberta.");
                original = initial.GetFeature(0x52, 185);
                MsiRgbController.ValidateReport(original); // An original save flag1 aborts before ANY write.
                evidence["originalSha256"] = Hash(original);
                evidence["originalReportBase64"] = Convert.ToBase64String(original);
                evidence["originalSaveToFlashFlag"] = original[184];
                List<RgbZone> before = MsiRgbController.DecodeZones(original);
                List<ZoneSetting> settings = new List<ZoneSetting>();
                int[] offsets = { 1, 134, 31, 42 };
                for (int i = 0; i < before.Count; i++)
                {
                    int originalSpeed = original[offsets[i] + 4] & 3;
                    if (originalSpeed > 2) throw new IOException("O estado original tem velocidade fora dos três níveis MSI. Teste cancelado.");
                    settings.Add(new ZoneSetting { Key = before[i].Key, Color = Color.FromArgb(0x24, 0x68, 0xAC),
                        BrightnessPercent = 40, SpeedPercent = originalSpeed * 50, Effect = "Static" });
                }
                MsiRgbController.BuildReport(original, settings); // Audit/validate before the mutation window.
                controller = new MsiRgbController(); controller.Connect(initial);
                initial = null; // Controller owns and closes this handle.
                if (!Equal(original, controller.GetReportSnapshot())) throw new IOException("O estado mudou antes do teste. Nenhuma escrita foi iniciada.");
                CheckOpenRgbAbsent();
                needRestore = true;
                evidence["applyAttempted"] = true;
                evidence["applyAtUtc"] = DateTime.UtcNow.ToString("o");
                controller.Apply(settings);
                byte[] applied = controller.GetReportSnapshot();
                bool verified = controller.LastWriteVerified;
                foreach (int o in offsets)
                    verified = verified && applied[o] == 1 && applied[o + 1] == 0x24 && applied[o + 2] == 0x68 && applied[o + 3] == 0xAC &&
                        ((applied[o + 4] & 0x7C) >> 2) == 4 && (applied[o + 4] & 3) == (original[o + 4] & 3);
                evidence["appliedSha256"] = Hash(applied);
                evidence["appliedReportBase64"] = Convert.ToBase64String(applied);
                evidence["applyVerified"] = verified;
                if (!verified) throw new IOException("A leitura não confirmou os campos controlados do teste.");
            }
            finally { if (initial != null) initial.Dispose(); }
        }
        catch (Exception error)
        {
            applyError = error.ToString(); evidence["applyError"] = applyError;
        }
        finally
        {
            if (controller != null) controller.Dispose();
            if (needRestore && original != null && selected != null)
            {
                evidence["restoreAttempted"] = true;
                evidence["restoreAtUtc"] = DateTime.UtcNow.ToString("o");
                try
                {
                    using (WindowsHidTransport restore = new WindowsHidTransport(selected.Path))
                    {
                        MsiRgbController.ValidateIdentity(restore);
                        if (restore.Serial != selected.Serial) throw new IOException("O serial mudou; restauração bloqueada para impedir escrita em outro dispositivo.");
                        byte[] current = restore.GetFeature(0x52, 185);
                        MsiRgbController.ValidateReport(current);
                        byte[] restoreOriginal = (byte[])original.Clone(); restoreOriginal[184] = 0;
                        current[184] = 0;
                        restore.SetFeature(current); // Current state, then original state, as required by MSI.
                        restore.SetFeature(restoreOriginal);
                        byte[] restored;
                        Stopwatch settle = Stopwatch.StartNew();
                        int readAttempts = 0;
                        do
                        {
                            restored = restore.GetFeature(0x52, 185); readAttempts++;
                            MsiRgbController.ValidateReport(restored);
                            if (Equal(restoreOriginal, restored) || settle.ElapsedMilliseconds >= 1500) break;
                            Thread.Sleep(40);
                        } while (true);
                        evidence["restoreReadAttempts"] = readAttempts;
                        evidence["restoreSettleMilliseconds"] = settle.ElapsedMilliseconds;
                        evidence["restoredSha256"] = Hash(restored);
                        evidence["restoredReportBase64"] = Convert.ToBase64String(restored);
                        evidence["restoreVerified"] = Equal(restoreOriginal, restored);
                        if (!Equal(restoreOriginal, restored)) throw new IOException("O relatório de restauração difere do original. Verifique a iluminação antes de usar o app.");
                    }
                }
                catch (Exception error) { restoreError = error.ToString(); evidence["restoreError"] = restoreError; }
            }
            evidence["completedAtUtc"] = DateTime.UtcNow.ToString("o");
            string json = new JavaScriptSerializer().Serialize(evidence);
            Directory.CreateDirectory("outputs/LumeRGB-Independente/evidencias");
            File.WriteAllText("outputs/LumeRGB-Independente/evidencias/msi-native-roundtrip.json", json, new UTF8Encoding(false));
            Console.WriteLine(json);
            Environment.ExitCode = applyError == null && restoreError == null && (bool)evidence["applyVerified"] && (bool)evidence["restoreVerified"] ? 0 : 1;
        }
    }
}
