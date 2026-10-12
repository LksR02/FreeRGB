// SPDX-License-Identifier: GPL-2.0-or-later
// Windows "Dynamic Lighting" (HID LampArray, usage page 0x59) for the X68HE.
// The keyboard exposes it once its "Microsoft Dynamic Light" flag is on (vendor command 0x14 1, the same
// switch the manufacturer's Dynamic RGB uses for its "system" channel). LampArray is the standard made for
// real-time colour: one LampRangeUpdateReport sets every key at once and nothing is restarted, so no blink.
// Reports are built with the Windows HID parser (HidP_*) from the device's own descriptor.
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LumeRgb
{
    public interface ILampArray : IDisposable
    {
        int LampCount { get; }
        void SetAutonomous(bool autonomous);
        void SetAll(Color color);
    }

    public sealed class WindowsLampArray : ILampArray
    {
        public const ushort UsagePage = 0x59, LampArrayUsage = 0x01;
        const ushort LampCountUsage = 0x03, RedChannel = 0x51, GreenChannel = 0x52, BlueChannel = 0x53, IntensityChannel = 0x54,
            LampUpdateFlags = 0x55, LampIdStart = 0x61, LampIdEnd = 0x62, AutonomousMode = 0x71;
        const int Feature = 2, Success = 0x00110000;
        SafeFileHandle handle; IntPtr parsed;
        readonly int featureLength;
        readonly byte attributesId, rangeId, controlId;
        readonly bool autonomousIsButton;
        public int LampCount { get; private set; }

        public static bool IsLampArray(HidDeviceInfo d)
        {
            return d.VendorId == 0x3151 && d.ProductId == 0x502D && d.UsagePage == UsagePage && d.Usage == LampArrayUsage;
        }
        public WindowsLampArray(string path)
        {
            handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Não foi possível abrir a Iluminação Dinâmica do teclado: " + new Win32Exception(error).Message); }
            try
            {
                if (!HidD_GetPreparsedData(handle, out parsed)) Fail("HidD_GetPreparsedData");
                Caps caps;
                if (HidP_GetCaps(parsed, out caps) != Success) throw new IOException("Descritor LampArray ilegível.");
                if (caps.UsagePage != UsagePage || caps.Usage != LampArrayUsage) throw new NotSupportedException("Esta interface não é um LampArray.");
                featureLength = caps.FeatureReportByteLength;
                ushort count = caps.NumberFeatureValueCaps;
                var values = new ValueCaps[Math.Max((int)count, 1)];
                if (count > 0 && HidP_GetValueCaps(Feature, values, ref count, parsed) != Success) throw new IOException("Descritor LampArray sem valores.");
                Func<ushort, int> idOf = usage =>
                {
                    var hit = values.Take(count).FirstOrDefault(v => v.UsagePage == UsagePage && (v.IsRange != 0 ? usage >= v.UsageMin && usage <= v.UsageMax : v.UsageMin == usage));
                    return hit.UsagePage == UsagePage ? hit.ReportID : -1;
                };
                int attributes = idOf(LampCountUsage), range = idOf(LampIdStart), control = idOf(AutonomousMode);
                if (control < 0)
                {
                    // Some descriptors declare AutonomousMode as a 1-bit button.
                    ushort buttons = caps.NumberFeatureButtonCaps; var bcaps = new ButtonCaps[Math.Max((int)buttons, 1)];
                    if (buttons > 0 && HidP_GetButtonCaps(Feature, bcaps, ref buttons, parsed) == Success)
                    {
                        var hit = bcaps.Take(buttons).FirstOrDefault(b => b.UsagePage == UsagePage && (b.IsRange != 0 ? AutonomousMode >= b.UsageMin && AutonomousMode <= b.UsageMax : b.UsageMin == AutonomousMode));
                        if (hit.UsagePage == UsagePage) { control = hit.ReportID; autonomousIsButton = true; }
                    }
                }
                if (attributes < 0 || range < 0 || control < 0) throw new NotSupportedException("O teclado não oferece os relatórios LampArray necessários.");
                attributesId = (byte)attributes; rangeId = (byte)range; controlId = (byte)control;
                byte[] report = NewReport(attributesId);
                if (!HidD_GetFeature(handle, report, report.Length)) Fail("LampArrayAttributesReport");
                uint lamps;
                if (HidP_GetUsageValue(Feature, UsagePage, 0, LampCountUsage, out lamps, parsed, report, (uint)report.Length) != Success || lamps == 0)
                    throw new IOException("O teclado não informou quantas luzes tem.");
                LampCount = (int)lamps;
            }
            catch { Dispose(); throw; }
        }
        byte[] NewReport(byte id)
        {
            byte[] report = new byte[featureLength];
            if (HidP_InitializeReportForID(Feature, id, parsed, report, (uint)report.Length) != Success) report[0] = id;
            return report;
        }
        void Value(byte[] report, ushort usage, uint value)
        {
            int status = HidP_SetUsageValue(Feature, UsagePage, 0, usage, value, parsed, report, (uint)report.Length);
            if (status != Success) throw new IOException("Relatório LampArray recusou o campo 0x" + usage.ToString("X2") + " (0x" + status.ToString("X8") + ").");
        }
        public void SetAutonomous(bool autonomous)
        {
            byte[] report = NewReport(controlId);
            if (!autonomousIsButton) Value(report, AutonomousMode, autonomous ? 1u : 0u);
            else if (autonomous)
            {
                var list = new ushort[] { AutonomousMode }; uint length = 1;
                HidP_SetUsages(Feature, UsagePage, 0, list, ref length, parsed, report, (uint)report.Length);
            }
            if (!HidD_SetFeature(handle, report, report.Length)) Fail("LampArrayControlReport");
        }
        public void SetAll(Color color)
        {
            byte[] report = NewReport(rangeId);
            Value(report, LampUpdateFlags, 1); // LampUpdateComplete
            Value(report, LampIdStart, 0);
            Value(report, LampIdEnd, (uint)(LampCount - 1));
            Value(report, RedChannel, color.R); Value(report, GreenChannel, color.G); Value(report, BlueChannel, color.B);
            try { Value(report, IntensityChannel, 255); } catch (IOException) { } // optional on some devices
            if (!HidD_SetFeature(handle, report, report.Length)) Fail("LampRangeUpdateReport");
        }
        static void Fail(string operation) { throw new IOException(operation + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message); }
        public void Dispose()
        {
            if (parsed != IntPtr.Zero) { HidD_FreePreparsedData(parsed); parsed = IntPtr.Zero; }
            if (handle != null) { handle.Dispose(); handle = null; }
        }

        [StructLayout(LayoutKind.Sequential)] struct Caps
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        // HIDP_VALUE_CAPS (72 bytes); UsageMin is Usage when IsRange == 0.
        [StructLayout(LayoutKind.Sequential)] struct ValueCaps
        {
            public ushort UsagePage; public byte ReportID, IsAlias; public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
            public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
            public ushort BitSize, ReportCount, R1, R2, R3, R4, R5;
            public uint UnitsExp, Units; public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
            public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        }
        // HIDP_BUTTON_CAPS (72 bytes)
        [StructLayout(LayoutKind.Sequential)] struct ButtonCaps
        {
            public ushort UsagePage; public byte ReportID, IsAlias; public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
            public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute;
            public ushort ReportCount, Reserved2; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public uint[] Reserved;
            public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, [Out] ValueCaps[] caps, ref ushort length, IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, [Out] ButtonCaps[] caps, ref ushort length, IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_InitializeReportForID(int type, byte id, IntPtr data, [Out] byte[] report, uint length);
        [DllImport("hid.dll")] static extern int HidP_SetUsageValue(int type, ushort page, ushort link, ushort usage, uint value, IntPtr data, byte[] report, uint length);
        [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr data, byte[] report, uint length);
        [DllImport("hid.dll")] static extern int HidP_SetUsages(int type, ushort page, ushort link, ushort[] usages, ref uint length, IntPtr data, byte[] report, uint length2);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetFeature(SafeFileHandle handle, [Out] byte[] report, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_SetFeature(SafeFileHandle handle, [In] byte[] report, int length);
    }
}
