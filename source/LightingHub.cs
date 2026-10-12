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
        byte[] ReadBoardSnapshot();
        void RestoreBoard(byte[] snapshot);
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
        }
        // Hardware-free tests attach controllers that already own fake transports.
        public void Attach(MsiRgbController board, X68KeyboardController keys, string identity)
        {
            Dispose(); notes.Clear(); motherboard = board; keyboard = keys; SetupIdentity = identity;
        }
        public List<RgbZone> GetZones()
        {
            var result = new List<RgbZone>();
            if (motherboard != null) result.AddRange(motherboard.GetZones());
            if (keyboard != null) result.Add(keyboard.GetZone());
            return result;
        }
        public string[] EffectsFor(IEnumerable<string> keys)
        {
            var requested = keys.ToList();
            if (requested.Contains("X68HE")) return X68KeyboardController.SupportedEffects;
            return MsiRgbController.SupportedEffects;
        }
        public void Apply(List<ZoneSetting> settings)
        {
            if (settings == null || settings.Count == 0) throw new ArgumentException("Selecione um item de iluminação.");
            if (settings.Select(s => s.Key).Distinct().Count() != settings.Count) throw new ArgumentException("Seleção de zonas duplicada.");
            if (settings.Any(s => s.Key != "X68HE" && !new[] { "JRGB1", "JRGB2", "JRAINBOW1", "JRAINBOW2" }.Contains(s.Key)))
                throw new ArgumentException("Item de iluminação desconhecido.");
            var boardSettings = settings.Where(s => s.Key != "X68HE").ToList();
            var keySettings = settings.Where(s => s.Key == "X68HE").ToList();
            if (boardSettings.Count > 0 && motherboard == null) throw new InvalidOperationException("A placa configurada não está disponível.");
            if (keySettings.Count > 0 && keyboard == null) throw new InvalidOperationException("O teclado configurado não está disponível.");
            foreach (var s in settings) if (!EffectsFor(new[] { s.Key }).Contains(s.Effect)) throw new ArgumentException("Efeito incompatível com " + s.Key + ".");
            foreach (var s in settings) if (s.BrightnessPercent < 0 || s.BrightnessPercent > 100 || s.SpeedPercent < 0 || s.SpeedPercent > 100)
                throw new ArgumentException("Brilho ou ritmo fora da faixa válida.");
            LastWriteVerified = false; bool boardComplete = false;
            try {
                if (boardSettings.Count > 0) { motherboard.Apply(boardSettings); boardComplete = true; }
                if (keySettings.Count > 0) keyboard.Apply(keySettings[0]);
                LastWriteVerified = (boardSettings.Count == 0 || motherboard.LastWriteVerified) && (keySettings.Count == 0 || keyboard.LastWriteVerified);
            } catch (Exception e) {
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
            if (motherboard != null) motherboard.Dispose(); if (keyboard != null) keyboard.Dispose();
            motherboard = null; keyboard = null; LastWriteVerified = false;
        }
    }
}
