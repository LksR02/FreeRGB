using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LumeRgb
{
    public sealed class HidDeviceInfo
    {
        public string Path = "";
        public ushort VendorId, ProductId, UsagePage, Usage;
        public string ProductName = "", Manufacturer = "", Serial = "", InstanceId = "";
        public int FeatureReportLength, InputReportLength, OutputReportLength;
        public bool MetadataAccessible;
        public int MetadataError;
    }

    public sealed class PnpDeviceInfo
    {
        public string InstanceId = "", FriendlyName = "", Manufacturer = "", ClassName = "";
        public string[] HardwareIds = new string[0];
    }

    // Enumerates present devices using the Windows device manager APIs. It does
    // not issue HID feature reports, output reports, vendor requests or writes.
    public static class WindowsDevices
    {
        const uint Present = 2, AllClasses = 4, DeviceInterface = 16;
        static readonly IntPtr Invalid = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceInfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct InterfaceData
        {
            public uint Size;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct HidAttributes
        {
            public uint Size;
            public ushort VendorID, ProductID, VersionNumber;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct HidCaps
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetProductString(SafeFileHandle handle, IntPtr buffer, uint length);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetManufacturerString(SafeFileHandle handle, IntPtr buffer, uint length);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, IntPtr buffer, uint length);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint detailSize, out uint required, ref DeviceInfoData device);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfoData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfoData data, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfoData data, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static DeviceInfoData NewDeviceInfo()
        {
            DeviceInfoData value = new DeviceInfoData();
            value.Size = (uint)Marshal.SizeOf(typeof(DeviceInfoData));
            return value;
        }
        static string Instance(IntPtr set, ref DeviceInfoData device)
        {
            StringBuilder id = new StringBuilder(2048);
            uint required;
            return SetupDiGetDeviceInstanceId(set, ref device, id, (uint)id.Capacity, out required) ? id.ToString() : "";
        }
        static string[] Property(IntPtr set, ref DeviceInfoData device, uint property)
        {
            uint type, required;
            byte[] buffer = new byte[8192];
            if (!SetupDiGetDeviceRegistryProperty(set, ref device, property, out type, buffer, (uint)buffer.Length, out required))
                return new string[0];
            return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length)).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
        static string First(string[] values) { return values.Length == 0 ? "" : values[0]; }
        static string HidString(SafeFileHandle handle, int kind)
        {
            IntPtr buffer = Marshal.AllocHGlobal(512);
            try
            {
                for (int i = 0; i < 512; i++) Marshal.WriteByte(buffer, i, 0);
                bool ok = kind == 0 ? HidD_GetProductString(handle, buffer, 512) : kind == 1 ? HidD_GetManufacturerString(handle, buffer, 512) : HidD_GetSerialNumberString(handle, buffer, 512);
                return ok ? (Marshal.PtrToStringUni(buffer) ?? "") : "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static List<HidDeviceInfo> GetHidDevices()
        {
            List<HidDeviceInfo> devices = new List<HidDeviceInfo>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Present | DeviceInterface);
            if (set == Invalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; ; index++)
                {
                    InterfaceData data = new InterfaceData();
                    data.Size = (uint)Marshal.SizeOf(typeof(InterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259) throw new System.ComponentModel.Win32Exception(error);
                        break;
                    }
                    DeviceInfoData device = NewDeviceInfo();
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, ref device);
                    if (required < 6 || required > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, ref device)) continue;
                        HidDeviceInfo info = new HidDeviceInfo();
                        info.Path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? "";
                        info.InstanceId = Instance(set, ref device);
                        info.ProductName = First(Property(set, ref device, 12));
                        if (info.ProductName.Length == 0) info.ProductName = First(Property(set, ref device, 0));
                        info.Manufacturer = First(Property(set, ref device, 11));
                        // Zero requested access: descriptor queries only, shared with other apps.
                        using (SafeFileHandle handle = CreateFile(info.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (!handle.IsInvalid)
                            {
                                info.MetadataAccessible = true;
                                HidAttributes attrs = new HidAttributes();
                                attrs.Size = (uint)Marshal.SizeOf(typeof(HidAttributes));
                                if (HidD_GetAttributes(handle, ref attrs)) { info.VendorId = attrs.VendorID; info.ProductId = attrs.ProductID; }
                                string product = HidString(handle, 0), maker = HidString(handle, 1);
                                if (product.Length > 0) info.ProductName = product;
                                if (maker.Length > 0) info.Manufacturer = maker;
                                info.Serial = HidString(handle, 2);
                                IntPtr parsed;
                                if (HidD_GetPreparsedData(handle, out parsed))
                                {
                                    try
                                    {
                                        HidCaps caps;
                                        if (HidP_GetCaps(parsed, out caps) == 0x00110000)
                                        {
                                            info.Usage = caps.Usage; info.UsagePage = caps.UsagePage;
                                            info.FeatureReportLength = caps.FeatureReportByteLength;
                                            info.InputReportLength = caps.InputReportByteLength;
                                            info.OutputReportLength = caps.OutputReportByteLength;
                                        }
                                    }
                                    finally { HidD_FreePreparsedData(parsed); }
                                }
                            }
                            else info.MetadataError = Marshal.GetLastWin32Error();
                        }
                        // Protected keyboard/mouse collections may still expose their IDs in the path.
                        if (info.VendorId == 0) info.VendorId = IdFromPath(info.Path, "vid_");
                        if (info.ProductId == 0) info.ProductId = IdFromPath(info.Path, "pid_");
                        devices.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return devices;
        }
        static ushort IdFromPath(string path, string marker)
        {
            int index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            ushort value;
            if (index >= 0 && index + marker.Length + 4 <= path.Length && ushort.TryParse(path.Substring(index + marker.Length, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)) return value;
            return 0;
        }

        public static List<PnpDeviceInfo> GetPresentDevices()
        {
            List<PnpDeviceInfo> devices = new List<PnpDeviceInfo>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, Present | AllClasses);
            if (set == Invalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; ; index++)
                {
                    DeviceInfoData device = NewDeviceInfo();
                    if (!SetupDiEnumDeviceInfo(set, index, ref device))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259) throw new System.ComponentModel.Win32Exception(error);
                        break;
                    }
                    PnpDeviceInfo info = new PnpDeviceInfo();
                    info.InstanceId = Instance(set, ref device);
                    info.FriendlyName = First(Property(set, ref device, 12));
                    if (info.FriendlyName.Length == 0) info.FriendlyName = First(Property(set, ref device, 0));
                    info.Manufacturer = First(Property(set, ref device, 11));
                    info.ClassName = First(Property(set, ref device, 7));
                    info.HardwareIds = Property(set, ref device, 1);
                    // Presence is not RGB support. Include buses relevant to discovery and board metadata.
                    if (info.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                        info.InstanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
                        info.ClassName.Equals("Display", StringComparison.OrdinalIgnoreCase) ||
                        info.ClassName.Equals("Memory", StringComparison.OrdinalIgnoreCase) ||
                        info.ClassName.Equals("System", StringComparison.OrdinalIgnoreCase)) devices.Add(info);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return devices;
        }
    }
}
