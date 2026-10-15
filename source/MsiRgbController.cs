// SPDX-License-Identifier: GPL-2.0-or-later
// Original C# implementation of the MSI MS-7E30 HID protocol, based on OpenRGB
// MSIMotherboard185Controller.cpp / MSIMotherboardCommon.h (T-bond, Adam Honse).
// Windows feature-report padding follows libusb/hidapi/windows/hid.c.
// Protocol data attribution and source hashes are recorded in this package's evidence.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LumeRgb
{
    public sealed class RgbZone
    {
        public string Key, Name, Effect;
        public int LedCount, CurrentBrightnessPercent, CurrentSpeedPercent;
        public Color CurrentColor;
    }
    public sealed class ZoneSetting
    {
        public string Key, Effect;
        public Color Color;
        public int BrightnessPercent, SpeedPercent;
    }
    public interface IHidTransport : IDisposable
    {
        ushort VendorId { get; }
        ushort ProductId { get; }
        string Serial { get; }
        string Path { get; }
        int FeatureReportLength { get; }
        byte[] GetFeature(byte reportId, int length);
        void SetFeature(byte[] report);
    }

    public sealed class MsiRgbController : IDisposable
    {
        public const int ReportLength = 185;
        private readonly object gate = new object();
        private IHidTransport transport;
        private byte[] latest;
        private static readonly string[] keys = { "JRGB1", "JRGB2", "JRAINBOW1", "JRAINBOW2" };
        private static readonly int[] offsets = { 1, 134, 31, 42 };
        private static readonly string[] effects = { "Static", "Breathing", "Flashing", "Double flashing", "Lightning" };
        private static readonly string[] observedEffects = { "Off", "Static", "Breathing", "Flashing", "Double flashing", "Lightning",
            "MSI marquee", "Meteor", "Water drop", "MSI rainbow", "Pop", "Rap", "Jazz", "Play", "Movie", "Color ring", "Planetary",
            "Double meteor", "Energy", "Blink", "Clock", "Color pulse", "Color shift", "Color wave", "Marquee", "Rainbow", "Rainbow wave",
            "Visor", "JRAINBOW", "Rainbow flashing", "Rainbow double flashing", "Random", "Fan control", "Off 2", "Color ring flashing",
            "Color ring double flashing", "Stack", "Corsair queue", "Fire", "Lava" };
        public bool IsConnected { get { lock (gate) { return transport != null; } } }
        public bool LastWriteVerified { get; private set; }
        public string Serial { get { lock (gate) { return transport == null ? "" : transport.Serial; } } }
        public static string[] SupportedEffects { get { return (string[])effects.Clone(); } }

        public void Connect(string path) { Connect(new WindowsHidTransport(path)); }
        public void Connect(HidDeviceInfo device)
        {
            if (device == null) throw new ArgumentNullException("device");
            Connect(device.Path); // Identity is checked against the actual handle, never trusted metadata.
        }
        // Takes ownership of the transport. This overload also enables hardware-free tests.
        public void Connect(IHidTransport connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            lock (gate)
            {
                Disconnect();
                try
                {
                    ValidateIdentity(connection);
                    transport = connection;
                    latest = ReadReport(); // GET only. Connecting never changes lighting.
                }
                catch { connection.Dispose(); transport = null; latest = null; throw; }
            }
        }
        internal static void ValidateIdentity(IHidTransport connection)
        {
            ushort board;
            if (connection.VendorId != 0x0DB0 || connection.ProductId != 0x0076 ||
                String.IsNullOrEmpty(connection.Serial) || connection.Serial.Length < 4 ||
                !UInt16.TryParse(connection.Serial.Substring(0, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out board) || board != 0x7E30)
                throw new NotSupportedException("O controle direto é compatível somente com MSI B650M GAMING WIFI (MS-7E30), HID 0DB0:0076 e serial 7E30.");
            if (connection.FeatureReportLength < ReportLength || connection.FeatureReportLength > 4096)
                throw new InvalidDataException("O descritor HID não aceita o relatório MSI esperado.");
        }
        public List<RgbZone> GetZones()
        {
            lock (gate)
            {
                RequireConnected();
                try { latest = ReadReport(); return DecodeZones(latest); }
                catch { Disconnect(); throw; }
            }
        }
        public byte[] GetReportSnapshot()
        {
            lock (gate) { RequireConnected(); return (byte[])latest.Clone(); }
        }
        public void Apply(List<ZoneSetting> settings)
        {
            lock (gate)
            {
                RequireConnected(); LastWriteVerified = false;
                try
                {
                    ValidateSettings(settings); // Validate every selection before any write.
                    byte[] old = ReadReport();
                    byte[] next = BuildReport(old, settings);
                    // Native MSI sequence: current settings first, then new settings. No flash save.
                    transport.SetFeature((byte[])old.Clone());
                    transport.SetFeature((byte[])next.Clone());
                    // Firmware acknowledges USB transfers before updating its settings buffer.
                    // Poll GET only: never repeat SET merely because the first read is stale.
                    Stopwatch settle = Stopwatch.StartNew();
                    do
                    {
                        latest = ReadReport();
                        LastWriteVerified = VerifyReadback(next, latest, settings);
                        if (LastWriteVerified || settle.ElapsedMilliseconds >= 1500) break;
                        Thread.Sleep(40);
                    } while (true);
                    if (!LastWriteVerified) throw new IOException("A leitura do controlador não confirmou a alteração. Atualize o estado antes de tentar novamente.");
                }
                catch (ArgumentException) { throw; }
                catch (NotSupportedException) { throw; }
                catch { Disconnect(); throw; }
            }
        }
        // Fast path for reactive lighting: builds on the last known report and sends the same
        // native SET sequence, but skips the read-back wait. LumeService re-reads every few seconds.
        public void ApplyFast(List<ZoneSetting> settings)
        {
            lock (gate)
            {
                RequireConnected(); LastWriteVerified = false;
                ValidateSettings(settings);
                try
                {
                    if (latest == null) latest = ReadReport();
                    byte[] next = BuildReport(latest, settings);
                    transport.SetFeature((byte[])latest.Clone());
                    transport.SetFeature((byte[])next.Clone());
                    latest = next;
                }
                catch (ArgumentException) { throw; }
                catch (NotSupportedException) { throw; }
                catch { Disconnect(); throw; }
            }
        }
        // Writes back a report previously read from this same controller (used by "Ligar" to
        // restore exactly the effect that was active before "Desligar"). Same native sequence
        // as Apply: current settings first, then the saved ones. Never sets the flash flag.
        public void RestoreSnapshot(byte[] snapshot)
        {
            lock (gate)
            {
                RequireConnected(); LastWriteVerified = false;
                ValidateReport(snapshot);
                try
                {
                    byte[] old = ReadReport();
                    transport.SetFeature((byte[])old.Clone());
                    transport.SetFeature((byte[])snapshot.Clone());
                    Stopwatch settle = Stopwatch.StartNew();
                    do
                    {
                        latest = ReadReport();
                        LastWriteVerified = SnapshotMatches(snapshot, latest);
                        if (LastWriteVerified || settle.ElapsedMilliseconds >= 1500) break;
                        Thread.Sleep(40);
                    } while (true);
                    if (!LastWriteVerified) throw new IOException("A placa não confirmou a restauração da iluminação anterior.");
                }
                catch { Disconnect(); throw; }
            }
        }
        internal static bool SnapshotMatches(byte[] expected, byte[] actual)
        {
            foreach (int offset in offsets)
                for (int i = 0; i < 9; i++)
                {
                    int mask = i == 4 ? 0x7F : (i == 8 ? 0x80 : 0xFF);
                    if ((expected[offset + i] & mask) != (actual[offset + i] & mask)) return false;
                }
            return actual[74] == expected[74];
        }
        private void RequireConnected() { if (transport == null) throw new IOException("Conecte ao controlador MSI primeiro."); }
        private byte[] ReadReport()
        {
            byte[] report = transport.GetFeature(0x52, ReportLength); ValidateReport(report); return (byte[])report.Clone();
        }
        internal static void ValidateReport(byte[] report)
        {
            if (report == null || report.Length != ReportLength || report[0] != 0x52)
                throw new InvalidDataException("O controlador não retornou o relatório MSI 0x52 com 185 bytes.");
            if (report[184] != 0) throw new InvalidDataException("Relatório MSI com flag de gravação permanente inesperada. Nenhuma alteração foi enviada.");
        }
        private static int ZoneIndex(string key)
        {
            for (int i = 0; i < keys.Length; i++) if (String.Equals(keys[i], key, StringComparison.Ordinal)) return i;
            throw new ArgumentException("Zona MSI não reconhecida: " + key);
        }
        private static int EffectCode(string effect)
        {
            for (int i = 0; i < effects.Length; i++) if (String.Equals(effects[i], effect, StringComparison.OrdinalIgnoreCase)) return i + 1;
            throw new ArgumentException("Efeito não suportado: " + effect);
        }
        private static void ValidateSettings(List<ZoneSetting> settings)
        {
            if (settings == null || settings.Count == 0 || settings.Count > 4) throw new ArgumentException("Selecione de uma a quatro zonas.");
            HashSet<string> unique = new HashSet<string>();
            foreach (ZoneSetting setting in settings)
            {
                if (setting == null) throw new ArgumentException("Configuração de zona ausente.");
                ZoneIndex(setting.Key); EffectCode(setting.Effect);
                if (!unique.Add(setting.Key)) throw new ArgumentException("Zona duplicada: " + setting.Key);
                if (setting.BrightnessPercent < 0 || setting.BrightnessPercent > 100 || setting.SpeedPercent < 0 || setting.SpeedPercent > 100)
                    throw new ArgumentException("Brilho e velocidade precisam estar entre 0 e 100.");
            }
        }
        internal static byte[] BuildReport(byte[] previous, List<ZoneSetting> settings)
        {
            ValidateReport(previous); ValidateSettings(settings);
            bool direct = previous[74] == 0x25;
            bool complexSync = previous[74] > 5 && ((previous[78] & 0x80) != 0 || (previous[82] & 0x30) != 0);
            if ((direct || complexSync) && settings.Count != 4)
                throw new NotSupportedException("O controlador está em modo sincronizado. Selecione as quatro zonas para mudar para este efeito.");
            byte[] next = (byte[])previous.Clone();
            foreach (ZoneSetting setting in settings)
            {
                int index = ZoneIndex(setting.Key), offset = offsets[index];
                byte effect = (byte)EffectCode(setting.Effect);
                int brightness = (setting.BrightnessPercent + 5) / 10;
                int speed = (setting.SpeedPercent + 25) / 50; // MSI speed has exactly three levels, 0..2.
                next[offset] = effect;
                next[offset + 1] = next[offset + 5] = setting.Color.R;
                next[offset + 2] = next[offset + 6] = setting.Color.G;
                next[offset + 3] = next[offset + 7] = setting.Color.B;
                next[offset + 4] = (byte)((previous[offset + 4] & 0x80) | ((brightness << 2) & 0x7C) | speed);
                next[offset + 8] |= 0x80; // Custom color only; preserve unrelated color flags.
                next[offset + 9] = 0; // Known zone padding required by the MSI normal protocol.
                if (index < 2) // No physical onboard LEDs, but firmware still needs a hidden master.
                {
                    next[74] = effect;
                    next[75] = next[79] = setting.Color.R;
                    next[76] = next[80] = setting.Color.G;
                    next[77] = next[81] = setting.Color.B;
                    next[82] |= 0x01; next[83] = 0;
                }
            }
            next[78] &= 0x7F; // Effects 1..5 use independent header settings.
            next[82] &= 0xCF;
            next[184] = 0;
            return next;
        }
        internal static List<RgbZone> DecodeZones(byte[] report)
        {
            ValidateReport(report); List<RgbZone> zones = new List<RgbZone>();
            for (int i = 0; i < keys.Length; i++)
            {
                int offset = offsets[i], code = report[offset];
                int brightness = (report[offset + 4] & 0x7C) >> 2;
                if (brightness > 10) throw new InvalidDataException("O relatório MSI contém um nível de brilho fora de 0 a 10.");
                zones.Add(new RgbZone { Key = keys[i], Name = keys[i], LedCount = i < 2 ? 1 : report[offset + 10],
                    CurrentColor = Color.FromArgb(report[offset + 1], report[offset + 2], report[offset + 3]),
                    CurrentBrightnessPercent = brightness * 10, CurrentSpeedPercent = Math.Min(2, report[offset + 4] & 3) * 50,
                    Effect = report[74] == 0x25 ? "Direct" : (code < observedEffects.Length ? observedEffects[code] : "Effect " + code) });
            }
            return zones;
        }
        private static bool VerifyReadback(byte[] expected, byte[] actual, List<ZoneSetting> settings)
        {
            foreach (ZoneSetting setting in settings)
            {
                int offset = offsets[ZoneIndex(setting.Key)];
                for (int i = 0; i < 9; i++)
                {
                    int mask = i == 4 ? 0x7F : (i == 8 ? 0x80 : 0xFF);
                    if ((expected[offset + i] & mask) != (actual[offset + i] & mask)) return false;
                }
            }
            return actual[74] == expected[74] && (actual[78] & 0x80) == 0 && (actual[82] & 0x30) == 0;
        }
        public void Disconnect()
        {
            lock (gate) { if (transport != null) transport.Dispose(); transport = null; latest = null; LastWriteVerified = false; }
        }
        public void Dispose() { Disconnect(); }
    }

    public sealed class WindowsHidTransport : IHidTransport
    {
        private SafeFileHandle handle;
        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public string Serial { get; private set; }
        public string Path { get; private set; }
        public int FeatureReportLength { get; private set; }
        public WindowsHidTransport(string path)
        {
            if (String.IsNullOrEmpty(path) || !path.StartsWith("\\\\?\\hid#", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Caminho HID inválido.");
            Path = path;
            handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Não foi possível abrir o controlador MSI. Feche outros apps RGB. " + new Win32Exception(error).Message); }
            try
            {
                Attributes attributes = new Attributes(); attributes.Size = Marshal.SizeOf(typeof(Attributes));
                if (!HidD_GetAttributes(handle, ref attributes)) Fail("HidD_GetAttributes");
                VendorId = attributes.VendorID; ProductId = attributes.ProductID;
                byte[] serial = new byte[512];
                if (!HidD_GetSerialNumberString(handle, serial, serial.Length)) Fail("HidD_GetSerialNumberString");
                Serial = Encoding.Unicode.GetString(serial).TrimEnd('\0');
                IntPtr preparsed;
                if (!HidD_GetPreparsedData(handle, out preparsed)) Fail("HidD_GetPreparsedData");
                try
                {
                    Caps caps;
                    if (HidP_GetCaps(preparsed, out caps) < 0) throw new IOException("Falha ao consultar o descritor HID.");
                    FeatureReportLength = caps.FeatureReportByteLength;
                }
                finally { HidD_FreePreparsedData(preparsed); }
                MsiRgbController.ValidateIdentity(this);
            }
            catch { Dispose(); throw; }
        }
        public byte[] GetFeature(byte reportId, int length)
        {
            if (handle == null || handle.IsClosed) throw new ObjectDisposedException("WindowsHidTransport");
            if (reportId != 0x52 || length != 185) throw new ArgumentException("Somente a leitura MSI 0x52 de 185 bytes é suportada.");
            // IOCTL returns the actual report length; unlike HidD_GetFeature this detects short reads.
            byte[] input = new byte[length]; input[0] = reportId;
            byte[] output = new byte[length]; output[0] = reportId;
            uint returned;
            if (!DeviceIoControl(handle, 0x000B0192, input, input.Length, output, output.Length, out returned, IntPtr.Zero)) Fail("GET_FEATURE");
            if (returned != length) throw new InvalidDataException("GET_FEATURE retornou " + returned + " bytes; eram esperados 185.");
            return output;
        }
        internal static byte[] PrepareNativeFeature(byte[] report, int nativeLength)
        {
            MsiRgbController.ValidateReport(report);
            if (nativeLength < 185 || nativeLength > 4096) throw new ArgumentException("Comprimento HID nativo inválido.");
            byte[] padded = new byte[nativeLength]; Buffer.BlockCopy(report, 0, padded, 0, report.Length); return padded;
        }
        public void SetFeature(byte[] report)
        {
            if (handle == null || handle.IsClosed) throw new ObjectDisposedException("WindowsHidTransport");
            MsiRgbController.ValidateIdentity(this);
            byte[] padded = PrepareNativeFeature(report, FeatureReportLength);
            if (!HidD_SetFeature(handle, padded, padded.Length)) Fail("SET_FEATURE");
        }
        private static void Fail(string operation) { throw new IOException(operation + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message); }
        public void Dispose() { if (handle != null) { handle.Dispose(); handle = null; } }
        [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)] private struct Caps
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle device, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle device, [Out] byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out Caps caps);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetFeature(SafeFileHandle device, [In] byte[] report, int length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, int inputLength, [Out] byte[] output, int outputLength, out uint returned, IntPtr overlapped);
    }
}
