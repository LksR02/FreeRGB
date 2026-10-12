using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using LumeRgb;
class MsiReadProbe
{
    static void Main()
    {
        foreach (HidDeviceInfo d in WindowsDevices.GetHidDevices())
        {
            if (d.VendorId != 0x0DB0 || d.ProductId != 0x0076 || !d.Serial.StartsWith("7E30", StringComparison.OrdinalIgnoreCase)) continue;
            using (MsiRgbController controller = new MsiRgbController())
            {
                controller.Connect(d); // One GET_FEATURE 0x52; no Apply/SetFeature invocation.
                byte[] report = controller.GetReportSnapshot();
                List<RgbZone> zones = MsiRgbController.DecodeZones(report);
                List<object> rows = new List<object>();
                foreach (RgbZone z in zones) rows.Add(new { key = z.Key, name = z.Name, ledCount = z.LedCount,
                    rgb = new int[] { z.CurrentColor.R, z.CurrentColor.G, z.CurrentColor.B },
                    brightnessPercent = z.CurrentBrightnessPercent, speedPercent = z.CurrentSpeedPercent, effect = z.Effect });
                string digest;
                using (SHA256 hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(report)).Replace("-", "").ToLowerInvariant();
                string json = new JavaScriptSerializer().Serialize(new { evidenceId = "MSI-NATIVE-READ-01", observedAtUtc = DateTime.UtcNow.ToString("o"),
                    vendorId = "0DB0", productId = "0076", virtualBoardId = "7E30", serial = d.Serial, path = d.Path,
                    featureReportMaximumLength = d.FeatureReportLength, reportId = "52", reportLength = report.Length, reportSha256 = digest,
                    hiddenMasterEffect = report[74], saveToFlashFlag = report[184],
                    scope = "One native IOCTL_HID_GET_FEATURE request 0x52; no SET_FEATURE, no output reports, no firmware requests, no TCP.", zones = rows });
                Directory.CreateDirectory("outputs/LumeRGB-Independente/evidencias");
                File.WriteAllBytes("outputs/LumeRGB-Independente/evidencias/msi-report52-antes.bin", report);
                File.WriteAllText("outputs/LumeRGB-Independente/evidencias/msi-leitura-nativa.json", json, new UTF8Encoding(false));
                Console.WriteLine(json);
                return;
            }
        }
        throw new Exception("MSI MS-7E30 não encontrado.");
    }
}
