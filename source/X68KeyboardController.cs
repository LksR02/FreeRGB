// SPDX-License-Identifier: GPL-2.0-or-later
// Original Windows implementation of X68HE's USB lighting commands, observed
// in the manufacturer's qmk.top web configurator (source and hashes in evidence).
// Only GET_LEDPARAM 0x87 and SET_LEDPARAM 0x07 are supported. No key maps,
// Hall-effect calibration, firmware, reset or analog-stream commands are sent.
using System;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LumeRgb
{
    public interface IX68HidTransport : IDisposable
    {
        ushort VendorId { get; }
        ushort ProductId { get; }
        ushort UsagePage { get; }
        ushort Usage { get; }
        int FeatureReportLength { get; }
        string ProductName { get; }
        byte[] GetFeature();
        void SetFeature(byte[] report);
    }

    public sealed class X68KeyboardController : IDisposable
    {
        public const string ZoneKey = "X68HE";
        public const int ReportLength = 65;
        readonly object gate = new object();
        IX68HidTransport transport;
        byte[] latest;
        public bool IsConnected { get { lock (gate) { return transport != null; } } }
        public bool LastWriteVerified { get; private set; }
        public static string[] SupportedEffects { get { return new string[] { "Static", "Breathing" }; } }
        public void Connect(HidDeviceInfo device)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (device.VendorId != 0x3151 || device.ProductId != 0x502D || device.UsagePage != 0xFFFF || device.Usage != 2)
                throw new NotSupportedException("A interface selecionada não é a interface de configuração do X68HE.");
            Connect(new X68WindowsHidTransport(device.Path));
        }
        // Takes ownership of the transport. Enables tests without a keyboard.
        public void Connect(IX68HidTransport connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            lock (gate)
            {
                Disconnect();
                try { ValidateIdentity(connection); transport = connection; latest = ReadReport(); }
                catch { connection.Dispose(); transport = null; latest = null; throw; }
            }
        }
        internal static void ValidateIdentity(IX68HidTransport connection)
        {
            if (connection.VendorId != 0x3151 || connection.ProductId != 0x502D || connection.UsagePage != 0xFFFF || connection.Usage != 2 ||
                connection.FeatureReportLength != ReportLength || !String.Equals((connection.ProductName ?? "").Trim(), "X68HE", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Controle direto disponível somente para X68HE, HID 3151:502D, usage FFFF:0002 e feature report de 65 bytes.");
        }
        void RequireConnected() { if (transport == null) throw new IOException("O teclado X68HE não está conectado."); }
        internal static byte[] BuildQuery()
        {
            byte[] report = new byte[ReportLength];
            report[1] = 0x87; // Byte zero is the Windows report ID (zero).
            report[8] = 0x78; // Manufacturer Bit7 checksum of payload bytes 0..6.
            return report;
        }
        byte[] ReadReport()
        {
            ValidateIdentity(transport);
            // SET_FEATURE here sends a GET request. It does not set lighting.
            transport.SetFeature(BuildQuery());
            Thread.Sleep(10); // Official web app READ_TIME is 10 ms.
            byte[] report = transport.GetFeature();
            ValidateReport(report);
            return (byte[])report.Clone();
        }
        internal static void ValidateReport(byte[] report)
        {
            if (report == null || report.Length != ReportLength || report[0] != 0 || report[1] != 0x87)
                throw new InvalidDataException("Resposta X68HE inválida: esperado GET_LEDPARAM 0x87 com 65 bytes.");
            if (report[3] > 4 || report[4] > 4 || (report[5] & 15) > 8)
                throw new InvalidDataException("A resposta X68HE contém parâmetros de luz fora do protocolo conhecido.");
        }
        public RgbZone GetZone()
        {
            lock (gate)
            {
                RequireConnected();
                try { latest = ReadReport(); return DecodeZone(latest); }
                catch { Disconnect(); throw; }
            }
        }
        public byte[] GetReportSnapshot() { lock (gate) { RequireConnected(); return (byte[])latest.Clone(); } }
        internal static RgbZone DecodeZone(byte[] report)
        {
            ValidateReport(report);
            int palette = report[5] & 15;
            Color color = Color.FromArgb(report[6], report[7], report[8]);
            Color[] presets = { Color.Red, Color.FromArgb(255,128,0), Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta };
            if (palette < presets.Length) color = presets[palette];
            string[] effects = { "Off", "Static", "Breathing", "Neon", "Wave", "Ripple", "Raindrop", "Snake", "Press action", "Convergence", "Sine wave", "Kaleidoscope", "Line wave", "User picture", "Laser", "Circle wave", "Dazzling", "Rain down", "Meteor", "Press action off", "Music follow", "Screen color", "Music follow 2", "Train", "Fireworks" };
            return new RgbZone { Key = ZoneKey, Name = "Teclado X68HE", LedCount = 0, CurrentColor = color,
                CurrentBrightnessPercent = report[4] * 25, CurrentSpeedPercent = (4 - report[3]) * 25,
                Effect = report[2] < effects.Length ? effects[report[2]] : "Effect " + report[2] };
        }
        static int EffectCode(string effect)
        {
            if (String.Equals(effect, "Static", StringComparison.OrdinalIgnoreCase)) return 1;
            if (String.Equals(effect, "Breathing", StringComparison.OrdinalIgnoreCase)) return 2;
            throw new NotSupportedException("O X68HE oferece cor fixa e respiração neste app.");
        }
        static void ValidateSetting(ZoneSetting setting)
        {
            if (setting == null || setting.Key != ZoneKey) throw new ArgumentException("Zona X68HE inválida.");
            EffectCode(setting.Effect);
            if (setting.BrightnessPercent < 0 || setting.BrightnessPercent > 100 || setting.SpeedPercent < 0 || setting.SpeedPercent > 100)
                throw new ArgumentException("Brilho e velocidade precisam estar entre 0 e 100.");
        }
        internal static byte[] BuildReport(byte[] previous, ZoneSetting setting)
        {
            ValidateReport(previous); ValidateSetting(setting);
            byte[] report = new byte[ReportLength];
            report[1] = 7;
            report[2] = (byte)EffectCode(setting.Effect);
            // Static has no speed setting in the manufacturer's UI. Preserve it.
            report[3] = report[2] == 1 ? previous[3] : (byte)(4 - ((setting.SpeedPercent + 12) / 25));
            report[4] = (byte)((setting.BrightnessPercent + 12) / 25);
            report[5] = (byte)((previous[5] & 0xF0) | 7); // Preserve unknown option nibble; select custom RGB.
            report[6] = setting.Color.R; report[7] = setting.Color.G; report[8] = setting.Color.B;
            int sum = 0;
            for (int i = 1; i <= 8; i++) sum += report[i];
            report[9] = (byte)(255 - (sum & 255));
            return report;
        }
        public void Apply(ZoneSetting setting)
        {
            lock (gate)
            {
                RequireConnected(); ValidateSetting(setting); LastWriteVerified = false;
                try
                {
                    byte[] previous = ReadReport();
                    byte[] next = BuildReport(previous, setting);
                    transport.SetFeature(next);
                    // Firmware can return the previous state briefly after accepting SET.
                    // Send SET exactly once, then poll only GET requests for at most 1.5 s.
                    Stopwatch timer = Stopwatch.StartNew();
                    do
                    {
                        Thread.Sleep(100);
                        latest = ReadReport();
                        LastWriteVerified = LightingFieldsEqual(next, latest);
                    } while (!LastWriteVerified && timer.ElapsedMilliseconds < 1500);
                    if (!LastWriteVerified) throw new IOException("O teclado não confirmou a configuração de luz pela leitura dos parâmetros.");
                }
                catch { Disconnect(); throw; }
            }
        }
        internal static bool LightingFieldsEqual(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null || expected.Length != ReportLength || actual.Length != ReportLength) return false;
            for (int i = 2; i <= 8; i++) if (expected[i] != actual[i]) return false;
            return true;
        }
        public void Disconnect()
        {
            lock (gate) { if (transport != null) transport.Dispose(); transport = null; latest = null; LastWriteVerified = false; }
        }
        public void Dispose() { Disconnect(); }
    }

    public sealed class X68WindowsHidTransport : IX68HidTransport
    {
        SafeFileHandle handle;
        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public ushort UsagePage { get; private set; }
        public ushort Usage { get; private set; }
        public int FeatureReportLength { get; private set; }
        public string ProductName { get; private set; }
        public X68WindowsHidTransport(string path)
        {
            if (String.IsNullOrEmpty(path) || !path.StartsWith("\\\\?\\hid#", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Caminho HID inválido.");
            handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Não foi possível abrir o teclado X68HE: " + new Win32Exception(error).Message); }
            try
            {
                Attributes attributes = new Attributes(); attributes.Size = Marshal.SizeOf(typeof(Attributes));
                if (!HidD_GetAttributes(handle, ref attributes)) Fail("HidD_GetAttributes");
                VendorId = attributes.VendorID; ProductId = attributes.ProductID;
                byte[] product = new byte[512];
                if (!HidD_GetProductString(handle, product, product.Length)) Fail("HidD_GetProductString");
                ProductName = Encoding.Unicode.GetString(product).TrimEnd('\0');
                IntPtr parsed;
                if (!HidD_GetPreparsedData(handle, out parsed)) Fail("HidD_GetPreparsedData");
                try
                {
                    Caps caps;
                    if (HidP_GetCaps(parsed, out caps) != 0x00110000) throw new IOException("Falha ao consultar o descritor X68HE.");
                    UsagePage = caps.UsagePage; Usage = caps.Usage; FeatureReportLength = caps.FeatureReportByteLength;
                }
                finally { HidD_FreePreparsedData(parsed); }
                X68KeyboardController.ValidateIdentity(this); // Validate actual handle before any request.
            }
            catch { Dispose(); throw; }
        }
        public byte[] GetFeature()
        {
            if (handle == null || handle.IsClosed) throw new ObjectDisposedException("X68WindowsHidTransport");
            X68KeyboardController.ValidateIdentity(this);
            byte[] report = new byte[X68KeyboardController.ReportLength];
            if (!HidD_GetFeature(handle, report, report.Length)) Fail("GET_FEATURE X68HE");
            return report;
        }
        public void SetFeature(byte[] report)
        {
            if (handle == null || handle.IsClosed) throw new ObjectDisposedException("X68WindowsHidTransport");
            X68KeyboardController.ValidateIdentity(this);
            ValidateOutgoingReport(report);
            if (!HidD_SetFeature(handle, report, report.Length)) Fail("SET_FEATURE X68HE");
        }
        internal static void ValidateOutgoingReport(byte[] report)
        {
            if (report == null || report.Length != 65 || report[0] != 0 || (report[1] != 0x87 && report[1] != 7))
                throw new ArgumentException("Somente comandos de parâmetros de luz X68HE são suportados.");
            int end = report[1] == 0x87 ? 7 : 8, sum = 0;
            for (int i = 1; i <= end; i++) sum += report[i];
            if (report[end + 1] != (byte)(255 - (sum & 255))) throw new ArgumentException("Checksum X68HE inválido.");
            for (int i = end + 2; i < report.Length; i++) if (report[i] != 0) throw new ArgumentException("Padding X68HE inválido.");
            if (report[1] == 0x87)
            {
                for (int i = 2; i <= 7; i++) if (report[i] != 0) throw new ArgumentException("Consulta X68HE inválida.");
            }
            // Wave (4) is accepted at the transport level solely to restore an
            // observed existing lighting state after a reversible protocol test.
            // The normal controller/UI still offers only Static and Breathing.
            else if ((report[2] != 1 && report[2] != 2 && report[2] != 4) || report[3] > 4 || report[4] > 4 || (report[5] & 15) != 7)
                throw new ArgumentException("Parâmetros de luz X68HE inválidos.");
        }
        static void Fail(string operation) { throw new IOException(operation + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message); }
        public void Dispose() { if (handle != null) { handle.Dispose(); handle = null; } }
        [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)] struct Caps
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] product, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetFeature(SafeFileHandle handle, [Out] byte[] report, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_SetFeature(SafeFileHandle handle, [In] byte[] report, int length);
    }
}
