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
        void ApplyTransition(List<ZoneSetting> settings);
        bool KeyboardContinuous { get; }
        string TransitionNote { get; }
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
            // Use only an interface actually advertised by the device.
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
        ZoneSetting lastKeyboardTarget;
        public bool KeyboardContinuous { get { return KeyboardLive && keyboard != null && keyboard.IsConnected &&
            (keyboardVariant == "lamparray" ? lamp != null : !keyboard.StreamUnsupported); } }
        public string TransitionNote { get; private set; }
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
            keyboard.EndStream(); keyboard.GetZone();
            byte[] original = keyboard.GetReportSnapshot();
            X68KeyboardController.BuildRestoreReport(original); // Validate restoration before changing any light.
            bool wasStreamed = KeyboardStreamed; var target = lastKeyboardTarget;
            try
            {
                keyboard.StreamUnsupported = false; keyboard.LiveVariant = "screen";
                keyboard.Stream(System.Drawing.Color.Blue, 100); System.Threading.Thread.Sleep(1500);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                for (int step = 0; step <= 120; step++)
                {
                    keyboard.Stream(ColorTransition.Mix(System.Drawing.Color.Blue, System.Drawing.Color.Red, step / 120.0), 100);
                    int wait = (step + 1) * 50 - (int)timer.ElapsedMilliseconds;
                    if (wait > 0) System.Threading.Thread.Sleep(wait);
                }
                System.Threading.Thread.Sleep(1500);
                AppLog.Write("Teste de transição do teclado: " + keyboard.FramesSent + " cores enviadas pelo protocolo de fábrica 0x0E. " + keyboard.StreamInfo);
            }
            finally
            {
                keyboard.EndStream(); keyboard.LiveVariant = keyboardVariant == "overlay" ? "overlay" : "screen";
                if (keyboard.IsConnected)
                {
                    keyboard.RestoreLighting(original);
                    if (wasStreamed && target != null) { keyboard.Stream(target.Color, target.BrightnessPercent); keyboard.HoldStream = true; }
                }
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
            if (KeyboardStreamed && lastKeyboardTarget != null)
            {
                var z = result.FirstOrDefault(v => v.Key == "X68HE");
                if (z != null) { z.Effect = LumeService.StreamEffect; z.CurrentColor = lastKeyboardTarget.Color; z.CurrentBrightnessPercent = lastKeyboardTarget.BrightnessPercent; }
            }
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
                lines.Add("  LampArray: " + (lamp != null ? lamp.LampCount + " luzes" + (lampHostControl ? ", controlado pelo Lume" : "") : "não encontrado" + (lampError != null ? " (" + lampError + ")" : "")));
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
        public void ApplyTransition(List<ZoneSetting> settings)
        {
            TransitionNote = null;
            // Validate the complete request before any intermediate frame is sent.
            ValidateSettings(settings);
            var before = GetZones().ToDictionary(z => z.Key);
            var boardRequest = settings.Where(s => s.Key != "X68HE").ToList();
            if (boardRequest.Count > 0) MsiRgbController.BuildReport(motherboard.GetReportSnapshot(), boardRequest);
            bool continuous = KeyboardContinuous;
            var fade = settings.Where(s => s.Effect == "Static" && s.BrightnessPercent > 0 && before.ContainsKey(s.Key) &&
                before[s.Key].CurrentBrightnessPercent > 0 && (s.Key != "X68HE" || continuous) &&
                (before[s.Key].Effect == "Static" || before[s.Key].Effect == LumeService.StreamEffect) &&
                before[s.Key].CurrentColor.ToArgb() != s.Color.ToArgb()).ToList();
            if (settings.Any(s => s.Key == "X68HE") && !continuous)
                TransitionNote = "Teclado atualizado pelo modo clássico. A transição sem piscar precisa de um canal ao vivo confirmado no teste do teclado.";
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int slot = 0;
            while (fade.Count > 0 && timer.ElapsedMilliseconds < 650)
            {
                double t = Math.Min(1, timer.ElapsedMilliseconds / 650.0);
                var frames = fade.Select(s => new ZoneSetting { Key = s.Key, Effect = "Static", SpeedPercent = s.SpeedPercent,
                    Color = ColorTransition.Mix(before[s.Key].CurrentColor, s.Color, t),
                    BrightnessPercent = (int)Math.Round(before[s.Key].CurrentBrightnessPercent + (s.BrightnessPercent - before[s.Key].CurrentBrightnessPercent) * t) }).ToList();
                // Retain all requested board headers even if only one is fading.
                foreach (var s in boardRequest.Where(b => !frames.Any(f => f.Key == b.Key))) frames.Add(s);
                Write(frames, true);
                if (continuous && !KeyboardContinuous)
                {
                    continuous = false; fade.RemoveAll(s => s.Key == "X68HE");
                    TransitionNote = "O canal ao vivo falhou. O teclado voltou ao modo clássico; a transição suave não foi confirmada.";
                }
                int delay = (++slot * 67) - (int)timer.ElapsedMilliseconds;
                if (delay > 0) System.Threading.Thread.Sleep(delay);
            }
            // A continuous keyboard must not receive SET_LEDPARAM at the end of
            // the fade: that would restart its effect and reintroduce the blink.
            var regular = settings.Where(s => s.Key != "X68HE" || !continuous || s.BrightnessPercent == 0 || s.Effect != "Static").ToList();
            var live = settings.Where(s => !regular.Contains(s)).ToList();
            if (regular.Count > 0) Write(regular, false);
            if (live.Count > 0) Write(live, true);
            if (live.Count > 0 && KeyboardStreamed && keyboard != null) keyboard.HoldStream = true;
        }
        public void ApplyFast(List<ZoneSetting> settings) { Write(settings, true); }
        void ValidateSettings(List<ZoneSetting> settings)
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
        }
        void Write(List<ZoneSetting> settings, bool fast)
        {
            ValidateSettings(settings);
            var boardSettings = settings.Where(s => s.Key != "X68HE").ToList();
            var keySettings = settings.Where(s => s.Key == "X68HE").ToList();
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
                            ReleaseLamps(); try { lamp.Dispose(); } catch { } lamp = null; lampHostControl = false; lampError = e.Message;
                            keyboard.ApplyFast(k);
                        }
                    }
                    else if (KeyboardLive && keyboardVariant != "lamparray" && !keyboard.StreamUnsupported)
                    {
                        try { keyboard.Stream(keySettings[0].Color, keySettings[0].BrightnessPercent); KeyboardStreamed = true; }
                        catch (NotSupportedException) { AppLog.Write("Teclado X68HE: modo ao vivo recusado; usando o modo compatível. " + keyboard.StreamInfo); keyboard.ApplyFast(keySettings[0]); }
                    }
                    else keyboard.ApplyFast(keySettings[0]);
                    var target = keySettings[0];
                    lastKeyboardTarget = KeyboardStreamed ? new ZoneSetting { Key = target.Key, Color = target.Color,
                        BrightnessPercent = target.BrightnessPercent, SpeedPercent = target.SpeedPercent, Effect = target.Effect } : null;
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
            // A live colour needs the host. Leave its last visible colour in a
            // native static effect before closing, so it survives without frames.
            if (lamp == null && KeyboardStreamed && lastKeyboardTarget != null && keyboard != null && keyboard.IsConnected)
                try { keyboard.Apply(lastKeyboardTarget); } catch (Exception e) { AppLog.Write("Teclado (manter cor ao encerrar): " + e.Message); }
            if (motherboard != null) motherboard.Dispose(); if (keyboard != null) keyboard.Dispose();
            motherboard = null; keyboard = null; lamp = null; LastWriteVerified = false;
            lastKeyboardTarget = null; KeyboardStreamed = false;
        }
    }
}
