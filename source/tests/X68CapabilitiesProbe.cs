// Descriptor and known lighting queries only; never changes a setting.
using System;
using System.Linq;
using LumeRgb;
class X68CapabilitiesProbe
{
    static void Main(string[] args)
    {
        var devices = WindowsDevices.GetHidDevices();
        foreach (var device in devices.Where(d => d.VendorId == 0x3151 && d.ProductId == 0x502D))
            Console.WriteLine("HID " + device.UsagePage.ToString("X4") + ":" + device.Usage.ToString("X4") + " feature=" + device.FeatureReportLength);
        Console.WriteLine("LampArray=" + devices.Count(WindowsLampArray.IsLampArray));
        using (var keyboard = new X68KeyboardController())
        {
            keyboard.Connect(devices.Single(d => d.VendorId == 0x3151 && d.ProductId == 0x502D && d.UsagePage == 0xFFFF && d.Usage == 2));
            var zone = keyboard.GetZone();
            Console.WriteLine("Lighting=" + zone.Effect + " " + SettingsStore.Hex(zone.CurrentColor) + " brightness=" + zone.CurrentBrightnessPercent);
        }
    }
}
