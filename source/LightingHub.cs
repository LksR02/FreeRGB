// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace LumeRgb
{
    // Only in-process device drivers. This class never starts or connects to another RGB app.
    // What LumeService needs from the hardware layer; tests replace it with a fake.
    public interface ILightingBackend : IDisposable
    {
        string SetupIdentity { get; }
        List<string> DiscoveryNotes { get; }
        bool IsConnected { get; }
        bool LastWriteVerified { get; }
        int DeviceCount { get; }
        bool HasBoard { get; }
        List<RgbZone> GetZones();
        string[] EffectsFor(IEnumerable<string> keys);
        void Apply(List<ZoneSetting> settings);
        void ApplyFast(List<ZoneSetting> settings);
        byte[] ReadBoardSnapshot();
        void RestoreBoard(byte[] snapshot);
        // A device found at detection stopped answering: the service scans again instead of failing forever.
        bool NeedsRescan { get; }
        // Fast keyboard writes may use the native live mode (no blinking); false = classic SET_LEDPARAM.
        bool KeyboardLive { get; set; }
        bool KeyboardStreamed { get; }
        bool KeyboardStreamUnsupported { get; }
        string KeyboardVariant { get; set; }
        // Shows red through the Screen color mode, then green over the normal effect (3 s each), then restores.
        void TestKeyboardLive();
        // Windows Dynamic Lighting (LampArray) path for the keyboard.
        bool HasLampArray { get; }
        bool? KeyboardDynamicLighting();
        void SetKeyboardDynamicLighting(bool on);
        void TestLampArray();
        string Diagnostics();
    }
    public sealed class LightingHub : ILightingBackend
    {
        MsiRgbController motherboard;
        X68KeyboardController keyboard;
        public string SetupIdentity { get; private set; }
        readonly List<string> notes = new List<string>();
        public List<string> DiscoveryNotes { get { return notes; } }
        public bool IsConnected { get { return (motherboard != null && motherboard.IsConnected) || (keyboard != null && keyboard.IsConnected); } }
        public bool LastWriteVerified { get; private set; }
        public int DeviceCount { get { return (motherboard == null ? 0 : 1) + (keyboard == null ? 0 : 1); } }
        public void Detect(List<HidDeviceInfo> devices)
        {
            Dispose(); DiscoveryNotes.Clear(); SetupIdentity = "";
            var boards = devices.Where(d => d.VendorId == 0x0DB0 && d.ProductId == 0x0076 &&
                !String.IsNullOrEmpty(d.Serial) && d.Serial.StartsWith("7E30", StringComparison.OrdinalIgnoreCase)).ToList();
            if (boards.Count == 1)
            {
                try { motherboard = new MsiRgbController(); motherboard.Connect(boards[0]); SetupIdentity += "MSI:" + boards[0].Serial; }
                catch (Exception e) { if (motherboard != null) motherboard.Dispose(); motherboard = null; DiscoveryNotes.Add("Placa MSI: " + e.Message); }
            }
            else if (boards.Count > 1) DiscoveryNotes.Add("Mais de uma placa MSI com a mesma identificação; controle indisponível até selecionar a placa.");
            var keyboards = devices.Where(d => d.VendorId == 0x3151 && d.ProductId == 0x502D && d.UsagePage == 0xFFFF && d.Usage == 2).ToList();
            if (keyboards.Count == 1)
            {
                try { keyboard = new X68KeyboardController(); keyboard.Connect(keyboards[0]); SetupIdentity += "|X68HE:" + keyboards[0].Serial; }
                catch (Exception e) { if (keyboard != null) keyboard.Dispose(); keyboard = null; DiscoveryNotes.Add("Teclado X68HE: " + e.Message); }
            }
            else if (keyboards.Count > 1) DiscoveryNotes.Add("Mais de um X68HE; controle indisponível até selecionar o teclado.");
            // Present only while the keyboard's Dynamic Lighting flag is on.
            var lamps = devices.Where(WindowsLampArray.IsLampArray).ToList();
            if (keyboard != null && lamps.Count == 1)
            {
                try { lamp = new WindowsLampArray(lamps[0].Path); }
                catch (Exception e) { lamp = null; lampError = e.Message; AppLog.Write("Iluminação Dinâmica do teclado: " + e.Message); }
            }
        }
        ILampArray lamp; bool lampHostControl; string lampError;
        // Hardware-free tests attach controllers that already own fake transports.
        public void Attach(MsiRgbController board, X68KeyboardController keys, string identity) { Attach(board, keys, null, identity); }
        public void Attach(MsiRgbController board, X68KeyboardController keys, ILampArray lamps, string identity)
        {
            Dispose(); notes.Clear(); motherboard = board; keyboard = keys; lamp = lamps; SetupIdentity = identity;
        }
        public bool HasLampArray { get { return lamp != null; } }
        public bool? KeyboardDynamicLighting() { return keyboard == null || !keyboard.IsConnected ? null : keyboard.DynamicLighting(); }
        public void SetKeyboardDynamicLighting(bool on)
        {
            if (keyboard == null || !keyboard.IsConnected) throw new InvalidOperationException("O teclado X68HE não está conectado.");
            if (!on) ReleaseLamps();
            keyboard.SetDynamicLighting(on);
        }
        // Magenta for 4 s through Windows Dynamic Lighting, then the keyboard gets its own effect back.
        public void TestLampArray()
        {
            if (lamp == null) throw new InvalidOperationException("O teclado ainda não apareceu como dispositivo de Iluminação Dinâmica.");
            if (keyboard != null) keyboard.EndStream();
            try
            {
                lamp.SetAutonomous(false); lampHostControl = true;
                for (int i = 0; i < 8; i++) { lamp.SetAll(i % 2 == 0 ? System.Drawing.Color.Magenta : System.Drawing.Color.FromArgb(255, 0, 200)); System.Threading.Thread.Sleep(500); }
            }
            finally { ReleaseLamps(); }
        }
        // Hand the keys back to the keyboard's own effect.
        void ReleaseLamps()
        {
            if (lamp == null || !lampHostControl) return;
            lampHostControl = false;
            try { lamp.SetAutonomous(true); } catch (Exception e) { AppLog.Write("Iluminação Dinâmica (liberar): " + e.Message); }
        }
        public bool NeedsRescan { get { return (motherboard != null && !motherboard.IsConnected) || (keyboard != null && !keyboard.IsConnected); } }
        public bool KeyboardLive { get; set; }
        public bool KeyboardStreamed { get; private set; }
        public bool KeyboardStreamUnsupported { get { return keyboard != null && keyboard.StreamUnsupported; } }
        string keyboardVariant = "screen";
        public string KeyboardVariant
        {
            get { return keyboardVariant; }
            set
            {
                keyboardVariant = value == "overlay" ? "overlay" : value == "lamparray" ? "lamparray" : "screen";
                if (keyboard != null) keyboard.LiveVariant = keyboardVariant == "overlay" ? "overlay" : "screen";
                if (keyboardVariant != "lamparray") ReleaseLamps();
            }
        }
        public void TestKeyboardLive()
        {
            if (keyboard == null || !keyboard.IsConnected) throw new InvalidOperationException("O teclado X68HE não está conectado.");
            keyboard.EndStream();
            var before = keyboard.GetZone();
            var restore = new ZoneSetting { Key = "X68HE", Color = before.CurrentColor, BrightnessPercent = Math.Max(25, before.CurrentBrightnessPercent),
                Effect = before.Effect == "Breathing" ? "Breathing" : "Static", SpeedPercent = before.CurrentSpeedPercent };
            try
            {
                // 1) native Screen color mode + red frames
                keyboard.StreamUnsupported = false; keyboard.LiveVariant = "screen";
                try { for (int i = 0; i < 3; i++) { keyboard.Stream(System.Drawing.Color.Red, 100); System.Threading.Thread.Sleep(1000); } }
                catch (NotSupportedException) { AppLog.Write("Teste do teclado: modo Screen color recusado."); }
                // 2) back to a plain colour, then green frames over it
                keyboard.Apply(new ZoneSetting { Key = "X68HE", Color = System.Drawing.Color.Blue, BrightnessPercent = 100, Effect = "Static", SpeedPercent = 50 });
                keyboard.LiveVariant = "overlay";
                for (int i = 0; i < 3; i++) { keyboard.Stream(System.Drawing.Color.Lime, 100); System.Threading.Thread.Sleep(1000); }
                AppLog.Write("Teste do teclado: " + keyboard.FramesSent + " quadros enviados. " + keyboard.StreamInfo);
            }
            finally
            {
                keyboard.EndStream(); keyboard.LiveVariant = keyboardVariant == "overlay" ? "overlay" : "screen";
                if (keyboard.IsConnected) keyboard.Apply(restore);
            }
        }
        // One device failing must not hide the other: read each on its own; only fail when nothing answered.
        public List<RgbZone> GetZones()
        {
            var result = new List<RgbZone>(); Exception failure = null;
            if (motherboard != null && motherboard.IsConnected)
                try { result.AddRange(motherboard.GetZones()); } catch (Exception e) { failure = e; AppLog.Write("Placa MSI (leitura): " + e.Message); }
            if (keyboard != null && keyboard.IsConnected)
                try { result.Add(keyboard.GetZone()); } catch (Exception e) { failure = failure ?? e; AppLog.Write("Teclado X68HE (leitura): " + e.Message); }
            if (failure != null && result.Count == 0) throw failure;
            return result;
        }
        public string Diagnostics()
        {
            var lines = new List<string>();
            lines.Add("Placa MSI: " + (motherboard == null ? "não detectada" : motherboard.IsConnected ? "conectada" : "desconectada"));
            lines.Add("Teclado X68HE: " + (keyboard == null ? "não detectado" : keyboard.IsConnected ? "conectado" : "desconectado"));
            if (keyboard != null)
            {
                try { lines.Add("  GET_LEDPARAM: " + keyboard.RawState()); } catch (Exception e) { lines.Add("  GET_LEDPARAM: " + e.Message); }
                lines.Add("  modo ao vivo: " + (!KeyboardLive ? "desligado (modo compatível)" : keyboard.StreamUnsupported ? "recusado pelo teclado" : KeyboardStreamed ? "ativo" : "não usado ainda"));
                lines.Add("  handshake: " + (keyboard.StreamInfo ?? "não executado"));
                bool? flag = null; try { flag = keyboard.IsConnected ? keyboard.DynamicLighting() : null; } catch { }
                lines.Add("  Iluminação Dinâmica do Windows no teclado: " + (flag == null ? "desconhecida" : flag.Value ? "ligada" : "desligada") +
                    " | LampArray: " + (lamp != null ? lamp.LampCount + " luzes" + (lampHostControl ? ", controlado pelo Lume" : "") : "não encontrado" + (lampError != null ? " (" + lampError + ")" : "")));
                lines.Add("  variante: " + keyboardVariant + ", transmitindo: " + keyboard.IsStreaming + ", quadros enviados: " + keyboard.FramesSent);
            }
            return String.Join("\r\n", lines.ToArray());
        }
        public string[] EffectsFor(IEnumerable<string> keys)
        {
            var requested = keys.ToList();
            if (requested.Contains("X68HE")) return X68KeyboardController.SupportedEffects;
            return MsiRgbController.SupportedEffects;
        }
        public void Apply(List<ZoneSetting> settings) { Write(settings, false); }
        public void ApplyFast(List<ZoneSetting> settings) { Write(settings, true); }
        void Write(List<ZoneSetting> settings, bool fast)
        {
            if (settings == null || settings.Count == 0) throw new ArgumentException("Selecione um item de iluminação.");
            if (settings.Select(s => s.Key).Distinct().Count() != settings.Count) throw new ArgumentException("Seleção de zonas duplicada.");
            if (settings.Any(s => s.Key != "X68HE" && !new[] { "JRGB1", "JRGB2", "JRAINBOW1", "JRAINBOW2" }.Contains(s.Key)))
                throw new ArgumentException("Item de iluminação desconhecido.");
            var boardSettings = settings.Where(s => s.Key != "X68HE").ToList();
            var keySettings = settings.Where(s => s.Key == "X68HE").ToList();
            if (boardSettings.Count > 0 && (motherboard == null || !motherboard.IsConnected)) throw new InvalidOperationException("A placa configurada não está disponível.");
            if (keySettings.Count > 0 && (keyboard == null || !keyboard.IsConnected)) throw new InvalidOperationException("O teclado configurado não está disponível.");
            foreach (var s in settings) if (!EffectsFor(new[] { s.Key }).Contains(s.Effect)) throw new ArgumentException("Efeito incompatível com " + s.Key + ".");
            foreach (var s in settings) if (s.BrightnessPercent < 0 || s.BrightnessPercent > 100 || s.SpeedPercent < 0 || s.SpeedPercent > 100)
                throw new ArgumentException("Brilho ou ritmo fora da faixa válida.");
            LastWriteVerified = false; bool boardComplete = false;
            try {
                if (boardSettings.Count > 0) { if (fast) motherboard.ApplyFast(boardSettings); else motherboard.Apply(boardSettings); boardComplete = true; }
                // Reactive (fast) keyboard updates stream frames in the native "Screen color" mode: no blinking.
                // A keyboard that refuses that mode gets the classic command instead (it may blink, but it works).
                if (keySettings.Count > 0)
                {
                    KeyboardStreamed = false;
                    if (!fast) { ReleaseLamps(); keyboard.Apply(keySettings[0]); }
                    else if (KeyboardLive && keyboardVariant == "lamparray" && lamp != null)
                    {
                        // Windows Dynamic Lighting: one report with the final colour, nothing restarts on the keyboard.
                        var k = keySettings[0]; double scale = Math.Max(0, Math.Min(100, k.BrightnessPercent)) / 100.0;
                        try
                        {
                            if (!lampHostControl) { keyboard.EndStream(); lamp.SetAutonomous(false); lampHostControl = true; }
                            lamp.SetAll(System.Drawing.Color.FromArgb((int)Math.Round(k.Color.R * scale), (int)Math.Round(k.Color.G * scale), (int)Math.Round(k.Color.B * scale)));
                            KeyboardStreamed = true;
                        }
                        catch (Exception e)
                        {
                            AppLog.Write("Iluminação Dinâmica do teclado falhou; usando o comando clássico: " + e.Message);
                            try { lamp.Dispose(); } catch { } lamp = null; lampHostControl = false; lampError = e.Message;
                            keyboard.ApplyFast(k);
                        }
                    }
                    else if (KeyboardLive && keyboardVariant != "lamparray" && !keyboard.StreamUnsupported)
                    {
                        try { keyboard.Stream(keySettings[0].Color, keySettings[0].BrightnessPercent); KeyboardStreamed = true; }
                        catch (NotSupportedException) { AppLog.Write("Teclado X68HE: modo ao vivo recusado; usando o modo compatível. " + keyboard.StreamInfo); keyboard.ApplyFast(keySettings[0]); }
                    }
                    else keyboard.ApplyFast(keySettings[0]);
                }
                LastWriteVerified = (boardSettings.Count == 0 || motherboard.LastWriteVerified) && (keySettings.Count == 0 || keyboard.LastWriteVerified);
            } catch (NotSupportedException) { throw; }
            catch (Exception e) {
                throw new InvalidOperationException((boardComplete && keySettings.Count > 0 ? "A placa foi configurada, mas o teclado não concluiu. " : "") + e.Message);
            }
        }
        public bool HasBoard { get { return motherboard != null && motherboard.IsConnected; } }
        public bool HasKeyboard { get { return keyboard != null && keyboard.IsConnected; } }
        // Raw board report read just now, used to remember non-standard effects before switching off.
        public byte[] ReadBoardSnapshot()
        {
            if (motherboard == null) return null;
            motherboard.GetZones(); return motherboard.GetReportSnapshot();
        }
        public void RestoreBoard(byte[] snapshot)
        {
            if (motherboard == null) throw new InvalidOperationException("A placa configurada não está disponível.");
            LastWriteVerified = false; motherboard.RestoreSnapshot(snapshot); LastWriteVerified = motherboard.LastWriteVerified;
        }
        public void Dispose()
        {
            ReleaseLamps(); if (lamp != null) try { lamp.Dispose(); } catch { }
            if (motherboard != null) motherboard.Dispose(); if (keyboard != null) keyboard.Dispose();
            motherboard = null; keyboard = null; lamp = null; LastWriteVerified = false;
        }
    }
}
