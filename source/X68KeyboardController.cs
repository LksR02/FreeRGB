// SPDX-License-Identifier: GPL-2.0-or-later
// Original Windows implementation of X68HE's USB lighting commands, observed
// in the manufacturer's qmk.top web configurator (source and hashes in evidence).
// GET_LEDPARAM 0x87, SET_LEDPARAM 0x07 and, for live colours, the manufacturer's Dynamic RGB commands
// (0x8F/0x94 queries, 0x14 = Windows Dynamic Lighting flag, 0x0F colour frames). No key maps,
// Hall-effect calibration, firmware, reset or analog-stream commands are sent.
using System;
using System.Collections.Generic;
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
            // Right after a burst of commands the firmware can still hold an older reply: ask again a few
            // times (10, 20, 40, 80 ms) before giving up, instead of dropping the keyboard on one bad read.
            InvalidDataException last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                transport.SetFeature(BuildQuery());
                Thread.Sleep(10 << attempt); // Official web app READ_TIME is 10 ms.
                byte[] report = transport.GetFeature();
                try { ValidateReport(report); return (byte[])report.Clone(); }
                catch (InvalidDataException e) { last = e; }
            }
            throw last;
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
                // Like the manufacturer's apps: no other command while colours are being streamed.
                if (IsStreaming && latest != null) return DecodeZone(latest);
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
                RequireConnected(); ValidateSetting(setting); LastWriteVerified = false; StopStreaming();
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
        // Fast path for reactive lighting: one SET, no read-back wait (the service re-reads periodically).
        public void ApplyFast(ZoneSetting setting)
        {
            lock (gate)
            {
                RequireConnected(); ValidateSetting(setting); LastWriteVerified = false; StopStreaming();
                try
                {
                    if (latest == null) latest = ReadReport();
                    byte[] next = BuildReport(latest, setting);
                    transport.SetFeature(next);
                    for (int i = 2; i <= 8; i++) latest[i] = next[i];
                }
                catch { Disconnect(); throw; }
            }
        }
        // ---------- live colour streaming (no blinking) ----------
        // SET_LEDPARAM restarts the keyboard's effect on every command, which is visible as a blink.
        // The manufacturer's own "Dynamic RGB" app (Rongyuan) instead switches the keyboard to its native
        // "Screen color" effect once and then streams frames with command 0x0F: a 21 x 6 grid
        // (126 cells, RGB) split in 56-byte chunks, about every 30 ms. Frames never restart the effect.
        public const int SyncEffect = 21, SyncColumns = 21, SyncRows = 6, SyncChunk = 56;
        public bool Streaming { get { lock (gate) return latest != null && latest[2] == SyncEffect; } }
        // Set when this keyboard refused the live mode; callers then fall back to ApplyFast for this connection.
        public bool StreamUnsupported { get; set; }
        // What the live-mode handshake found (for the diagnostics report).
        public string StreamInfo { get; private set; }
        // "screen": switch to the native Screen color effect, then stream; "overlay": stream over the current effect.
        public string LiveVariant { get { lock (gate) return variant; } set { lock (gate) variant = value == "overlay" ? "overlay" : "screen"; } }
        string variant = "screen";
        bool handshakeDone;
        // The manufacturer's Dynamic RGB sends a frame every ~30 ms, continuously. A background thread does the same
        // with the latest colour, so the keyboard sees an uninterrupted stream whatever the reaction rate is.
        public const int FramePeriodMs = 33, StreamHoldSeconds = 3;
        byte[] liveFrame; DateTime lastStreamCall; int streamGeneration; bool streaming;
        public bool IsStreaming { get { lock (gate) return streaming; } }
        public int FramesSent { get; private set; }
        public void Stream(Color color, int brightnessPercent)
        {
            lock (gate)
            {
                RequireConnected();
                if (StreamUnsupported) throw new NotSupportedException("O teclado não aceitou o modo ao vivo.");
                try
                {
                    if (latest == null) latest = ReadReport();
                    if (!handshakeDone) { handshakeDone = true; Handshake(); }
                    if (variant == "screen" && latest[2] != SyncEffect)
                    {
                        StopStreaming();
                        byte[] mode = BuildSyncMode(latest); // full hardware brightness; dimming is done in the frames
                        transport.SetFeature(mode);
                        // Like Apply: the firmware may answer with the old state for a moment. Confirm the switch.
                        Stopwatch timer = Stopwatch.StartNew();
                        do { Thread.Sleep(60); latest = ReadReport(); } while (latest[2] != SyncEffect && timer.ElapsedMilliseconds < 1500);
                        if (latest[2] != SyncEffect)
                        {
                            StreamUnsupported = true;
                            StreamInfo = (StreamInfo ?? "") + " | modo ao vivo recusado (efeito lido: " + latest[2] + ")";
                            throw new NotSupportedException("O teclado não aceitou o modo ao vivo.");
                        }
                    }
                    if (variant == "overlay" && latest[2] == SyncEffect)
                    {
                        // Frames go over a normal effect on this keyboard: leave the Screen color effect once.
                        byte[] plain = BuildReport(latest, new ZoneSetting { Key = ZoneKey, Color = Color.FromArgb(latest[6], latest[7], latest[8]), BrightnessPercent = 100, Effect = "Static", SpeedPercent = 50 });
                        transport.SetFeature(plain); for (int i = 2; i <= 8; i++) latest[i] = plain[i];
                    }
                    liveFrame = UniformFrame(color, brightnessPercent);
                    lastStreamCall = DateTime.UtcNow;
                    SendFrame(liveFrame);
                    if (!streaming)
                    {
                        streaming = true; int generation = ++streamGeneration;
                        new Thread(() => StreamLoop(generation)) { IsBackground = true, Name = "X68HE live colours" }.Start();
                    }
                }
                catch (NotSupportedException) { throw; }
                catch { Disconnect(); throw; }
            }
        }
        void StreamLoop(int generation)
        {
            // Frames on a fixed schedule: Windows rounds Sleep up to its ~15.6 ms tick, so sleeping a plain 33 ms
            // each time would drift to ~21 frames/s. Aiming at the next slot keeps the average at 30.
            var clock = Stopwatch.StartNew(); long slot = 0;
            while (true)
            {
                slot++;
                long wait = slot * FramePeriodMs - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -FramePeriodMs * 3) slot = clock.ElapsedMilliseconds / FramePeriodMs; // fell far behind: do not burst
                lock (gate)
                {
                    if (generation != streamGeneration) return;
                    // Nobody refreshed the colour for a while (reactions off, app closing): stop streaming.
                    if (transport == null || liveFrame == null || (DateTime.UtcNow - lastStreamCall).TotalSeconds > StreamHoldSeconds) { streaming = false; streamGeneration++; return; }
                    try { SendFrame(liveFrame); }
                    catch (Exception) { streaming = false; streamGeneration++; Disconnect(); return; }
                }
            }
        }
        // Caller holds the gate.
        void StopStreaming() { if (streaming) { streaming = false; streamGeneration++; } }
        public void EndStream() { lock (gate) StopStreaming(); }
        void SendFrame(byte[] frame) { foreach (var chunk in BuildSyncFrame(frame)) transport.SetFeature(chunk); FramesSent++; }
        static byte[] UniformFrame(Color color, int brightnessPercent)
        {
            double k = Math.Max(0, Math.Min(100, brightnessPercent)) / 100.0;
            byte r = (byte)Math.Round(color.R * k), g = (byte)Math.Round(color.G * k), b = (byte)Math.Round(color.B * k);
            byte[] frame = new byte[SyncColumns * SyncRows * 3];
            for (int i = 0; i < frame.Length; i += 3) { frame[i] = r; frame[i + 1] = g; frame[i + 2] = b; }
            return frame;
        }
        // Same start-up as the manufacturer's Dynamic RGB: read the device info (0x8F) and the Windows Dynamic
        // Lighting flag (0x94), kept for the diagnostics report. Nothing is changed here.
        void Handshake()
        {
            var info = new StringBuilder();
            try
            {
                byte[] device = Command(BuildCommand(0x8F), true);
                info.Append("0x8F: " + Hex(device, 1, 12));
                byte[] dynamic = Command(BuildCommand(0x94), true);
                info.Append(" | 0x94: " + Hex(dynamic, 1, 6));
            }
            catch (Exception e) { info.Append(" | handshake: " + e.Message); }
            StreamInfo = info.ToString();
            latest = ReadReport();
        }
        // The keyboard's "Microsoft Dynamic Light" flag (0x94 read, 0x14 write): when on, the keyboard also shows up in
        // Windows as a Dynamic Lighting (HID LampArray) device, which takes live colours without restarting any effect.
        public bool? DynamicLighting()
        {
            lock (gate)
            {
                RequireConnected(); StopStreaming();
                byte[] reply = Command(BuildCommand(0x94), true);
                return reply != null && reply.Length == ReportLength && reply[1] == 0x94 ? reply[2] == 1 : (bool?)null;
            }
        }
        public void SetDynamicLighting(bool on)
        {
            lock (gate) { RequireConnected(); StopStreaming(); Command(BuildCommand(0x14, (byte)(on ? 1 : 0)), false); Thread.Sleep(100); }
        }
        byte[] Command(byte[] report, bool read)
        {
            transport.SetFeature(report);
            Thread.Sleep(read ? 10 : 20);
            return read ? transport.GetFeature() : null;
        }
        // Short command with the Bit7 checksum (payload bytes 0..6, checksum at payload byte 7).
        internal static byte[] BuildCommand(params byte[] payload)
        {
            byte[] report = new byte[ReportLength];
            for (int i = 0; i < payload.Length && i < 7; i++) report[1 + i] = payload[i];
            int sum = 0; for (int i = 1; i <= 7; i++) sum += report[i];
            report[8] = (byte)(255 - (sum & 255));
            return report;
        }
        static string Hex(byte[] data, int from, int count)
        {
            if (data == null) return "-";
            var sb = new StringBuilder();
            for (int i = from; i < Math.Min(data.Length, from + count); i++) sb.Append(data[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }
        public string RawState() { lock (gate) return latest == null ? "-" : Hex(latest, 1, 10); }
        internal static byte[] BuildSyncMode(byte[] previous)
        {
            ValidateReport(previous);
            byte[] report = new byte[ReportLength];
            report[1] = 7; report[2] = SyncEffect; report[3] = previous[3]; report[4] = 4;
            report[5] = 0; // the official configurator sends option 0 for "Screen color"
            report[6] = previous[6]; report[7] = previous[7]; report[8] = previous[8];
            int sum = 0; for (int i = 1; i <= 8; i++) sum += report[i];
            report[9] = (byte)(255 - (sum & 255));
            return report;
        }
        // Packet layout from the vendor app: 8-byte header [0x0F, 0x01, chunk, 56, last, 0, 0, checksum]
        // (Bit7 checksum over header bytes 0..6) followed by up to 56 colour bytes.
        internal static List<byte[]> BuildSyncFrame(byte[] rgb)
        {
            if (rgb == null || rgb.Length != SyncColumns * SyncRows * 3) throw new ArgumentException("Quadro de cores X68HE inválido.");
            var packets = new List<byte[]>();
            int count = (rgb.Length + SyncChunk - 1) / SyncChunk;
            for (int chunk = 0; chunk < count; chunk++)
            {
                byte[] report = new byte[ReportLength];
                report[1] = 0x0F; report[2] = 1; report[3] = (byte)chunk; report[4] = SyncChunk; report[5] = (byte)(chunk == count - 1 ? 1 : 0);
                int length = Math.Min(SyncChunk, rgb.Length - chunk * SyncChunk);
                Buffer.BlockCopy(rgb, chunk * SyncChunk, report, 9, length);
                int sum = 0; for (int i = 1; i <= 7; i++) sum += report[i];
                report[8] = (byte)(255 - (sum & 255));
                packets.Add(report);
            }
            return packets;
        }
        internal static bool LightingFieldsEqual(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null || expected.Length != ReportLength || actual.Length != ReportLength) return false;
            for (int i = 2; i <= 8; i++) if (expected[i] != actual[i]) return false;
            return true;
        }
        public void Disconnect()
        {
            lock (gate) { StopStreaming(); if (transport != null) transport.Dispose(); transport = null; latest = null; LastWriteVerified = false; }
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
            if (report == null || report.Length != 65 || report[0] != 0 || (report[1] != 0x87 && report[1] != 7 && report[1] != 0x0F &&
                report[1] != 0x8F && report[1] != 0x94 && report[1] != 0x14))
                throw new ArgumentException("Somente comandos de parâmetros de luz X68HE são suportados.");
            if (report[1] == 0x8F || report[1] == 0x94 || report[1] == 0x14)
            {
                // Read-only queries (device info, Windows Dynamic Lighting flag) and switching that flag off.
                int check = 0; for (int i = 1; i <= 7; i++) check += report[i];
                if (report[8] != (byte)(255 - (check & 255))) throw new ArgumentException("Checksum X68HE inválido.");
                if (report[1] == 0x14 && report[2] > 1) throw new ArgumentException("Comando X68HE inválido.");
                for (int i = report[1] == 0x14 ? 3 : 2; i < report.Length; i++) if (i != 8 && report[i] != 0) throw new ArgumentException("Comando X68HE inválido.");
                return;
            }
            if (report[1] == 0x0F)
            {
                // Live colour frame: header, Bit7 checksum, colour bytes only.
                int check = 0; for (int i = 1; i <= 7; i++) check += report[i];
                if (report[2] != 1 || report[3] > 15 || report[4] != X68KeyboardController.SyncChunk || report[5] > 1 || report[6] != 0 || report[7] != 0 ||
                    report[8] != (byte)(255 - (check & 255)))
                    throw new ArgumentException("Quadro de cores X68HE inválido.");
                return; // bytes 9..64 are exactly the 56 colour bytes
            }
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
            // "Screen color" (21) with option 0 is the live-streaming mode used by the manufacturer's Dynamic RGB.
            else if (report[2] == X68KeyboardController.SyncEffect) { if (report[3] > 4 || report[4] > 4 || report[5] != 0) throw new ArgumentException("Parâmetros de luz X68HE inválidos."); }
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
