// SPDX-License-Identifier: GPL-2.0-or-later
// Original settings and profile storage for Lume RGB.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LumeRgb
{
    [DataContract] public class ZoneProfile
    {
        [DataMember] public string Key;
        [DataMember] public string ColorHex;
        [DataMember] public int Brightness;
        [DataMember] public string Effect;
        [DataMember] public int Speed;
    }
    [DataContract] public class LightingProfile
    {
        [DataMember] public string Name;
        [DataMember] public string BoardSerial;
        [DataMember] public List<ZoneProfile> Zones = new List<ZoneProfile>();
        public override string ToString() { return Name; }
    }
    [DataContract] public class ZoneAlias
    {
        [DataMember] public string Key;
        [DataMember] public string Name;
    }
    [DataContract] public class WidgetConfig
    {
        [DataMember] public string Id;
        [DataMember] public string Style;
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public bool Locked;
    }
    // Reactive lighting: a PC sensor drives the colour (or brightness) of chosen zones.
    [DataContract] public class ReactiveRule
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Sensor;
        [DataMember] public List<string> Keys = new List<string>();
        [DataMember] public double Min;
        [DataMember] public double Max;
        [DataMember] public string Mode;          // "color" (gradient) or "brightness" (fixed colour, brightness follows the value)
        [DataMember] public string ColorLow;
        [DataMember] public string ColorMid;      // optional
        [DataMember] public string ColorHigh;
        [DataMember] public int Brightness;
        [DataMember] public double? AlertAbove;   // breathing when the value passes this point
        [DataMember] public bool Enabled;
        [OnDeserializing] void Defaults(StreamingContext context) { Keys = new List<string>(); }
    }
    // State saved when everything is switched off, so "Ligar" restores exactly what was there.
    [DataContract] public class PowerMemory
    {
        [DataMember] public string SetupIdentity;
        [DataMember] public string BoardReportBase64;
        [DataMember] public List<ZoneProfile> Zones = new List<ZoneProfile>();
    }
    [DataContract] public class PersonalSettings
    {
        [DataMember] public List<LightingProfile> Profiles = new List<LightingProfile>();
        [DataMember] public List<ZoneAlias> Aliases = new List<ZoneAlias>();
        [DataMember] public List<string> Palette = new List<string>();
        [DataMember] public bool AlwaysOnTop;
        // New fields are optional so settings.json from earlier versions still loads.
        [DataMember] public List<WidgetConfig> Widgets = new List<WidgetConfig>();
        [DataMember] public PowerMemory PowerMemory;
        [DataMember] public bool WidgetsOnTop;
        [DataMember] public bool WidgetsStill;
        [DataMember] public bool QuitOnClose;
        [DataMember] public bool HotkeyDisabled;
        [DataMember] public bool ReduceMotion;
        [DataMember] public string VisualLevel;
        [DataMember] public bool LiveApply;
        [DataMember] public List<ReactiveRule> Reactions = new List<ReactiveRule>();
        [DataMember] public bool ReactionsEnabled;
        [DataMember] public int? ReactiveRate;        // light updates per second (null = 10)
        [DataMember] public int? ReactiveSmoothing;   // 0 = instant .. 100 = very slow fades (null = 40)
        public int RateOrDefault { get { return ReactiveRate ?? 10; } }
        public int SmoothingOrDefault { get { return ReactiveSmoothing ?? 40; } }
        [DataMember] public bool AutoUpdateDisabled;
        [DataMember] public bool KeyboardCompat;
        [DataMember] public bool AdvancedPaused;      // advanced sensors (elevated LibreHardwareMonitor) switched off by the user or after a bad exit      // reactions drive the keyboard with the classic command (may blink)
        [OnDeserializing] void Defaults(StreamingContext context)
        {
            Profiles = new List<LightingProfile>(); Aliases = new List<ZoneAlias>(); Palette = new List<string>(); Widgets = new List<WidgetConfig>();
            Reactions = new List<ReactiveRule>();
        }
        public string ZoneName(string key, string fallback)
        {
            var alias = Aliases.FirstOrDefault(a => a.Key == key);
            return alias == null ? fallback : alias.Name;
        }
    }
    public static class SettingsStore
    {
        public static readonly string[] Effects = { "Static", "Breathing", "Flashing", "Double flashing", "Lightning" };
        public static readonly string[] WidgetStyles = { "orb", "pill", "panel" };
        public static readonly string[] VisualLevels = { "max", "balanced", "light" };
        public static readonly string[] SensorIds = { "cpu_load", "ram_load", "gpu_load", "gpu_temp", "gpu_power", "cpu_temp", "cpu_power", "total_power", "board_temp", "cpu_fan", "cpu_clock", "disk_load", "net_load" };
        public static Color ParseColor(string value)
        {
            if (value == null) throw new FormatException("Escolha uma cor hexadecimal como #7C6CFF.");
            value = value.Trim().TrimStart('#'); int rgb;
            if (value.Length != 6 || !Int32.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out rgb)) throw new FormatException("Cor inválida. Use seis dígitos, como #7C6CFF.");
            return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }
        public static string Hex(Color c) { return String.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
        public static PersonalSettings Load(string path)
        {
            if (!File.Exists(path)) return new PersonalSettings();
            using (var input = File.OpenRead(path))
            {
                if (input.Length > 1024 * 1024) throw new InvalidDataException("Arquivo de configuração grande demais.");
                var settings = (PersonalSettings)new DataContractJsonSerializer(typeof(PersonalSettings)).ReadObject(input);
                if (settings != null && settings.Widgets == null) settings.Widgets = new List<WidgetConfig>();
                if (settings != null && settings.Reactions == null) settings.Reactions = new List<ReactiveRule>();
                Validate(settings); return settings;
            }
        }
        public static void Validate(PersonalSettings settings)
        {
            if (settings == null || settings.Profiles == null || settings.Aliases == null || settings.Palette == null ||
                settings.Profiles.Count > 256 || settings.Aliases.Count > 64 || settings.Palette.Count > 32)
                throw new InvalidDataException("Configuração inválida.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in settings.Profiles)
            {
                if (p == null || String.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80 || !names.Add(p.Name) ||
                    String.IsNullOrWhiteSpace(p.BoardSerial) || p.Zones == null || p.Zones.Count < 1 || p.Zones.Count > 5)
                    throw new InvalidDataException("Perfil inválido ou incompleto.");
                ValidateZones(p.Zones);
            }
            if (settings.Widgets == null || settings.Widgets.Count > 24) throw new InvalidDataException("Lista de widgets inválida.");
            var widgetIds = new HashSet<string>();
            foreach (var w in settings.Widgets)
                if (w == null || String.IsNullOrWhiteSpace(w.Id) || w.Id.Length > 40 || !widgetIds.Add(w.Id) || !WidgetStyles.Contains(w.Style) ||
                    Math.Abs(w.X) > 100000 || Math.Abs(w.Y) > 100000) throw new InvalidDataException("Widget inválido.");
            if (settings.Reactions == null || settings.Reactions.Count > 32) throw new InvalidDataException("Lista de reações inválida.");
            var ruleIds = new HashSet<string>();
            foreach (var r in settings.Reactions)
            {
                if (r == null || String.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 40 || !ruleIds.Add(r.Id) || r.Name == null || r.Name.Length > 60 ||
                    !SensorIds.Contains(r.Sensor) || r.Keys == null || r.Keys.Count > 5 || r.Keys.Any(k => !ValidKey(k)) || r.Keys.Distinct().Count() != r.Keys.Count ||
                    double.IsNaN(r.Min) || double.IsNaN(r.Max) || r.Max <= r.Min || Math.Abs(r.Min) > 100000 || Math.Abs(r.Max) > 100000 ||
                    (r.Mode != "color" && r.Mode != "brightness") || r.Brightness < 0 || r.Brightness > 100)
                    throw new InvalidDataException("Reação inválida.");
                ParseColor(r.ColorLow); ParseColor(r.ColorHigh); if (r.ColorMid != null) ParseColor(r.ColorMid);
            }
            if ((settings.ReactiveRate.HasValue && (settings.ReactiveRate < 1 || settings.ReactiveRate > 30)) ||
                (settings.ReactiveSmoothing.HasValue && (settings.ReactiveSmoothing < 0 || settings.ReactiveSmoothing > 100)))
                throw new InvalidDataException("Ritmo das reações inválido.");
            if (settings.VisualLevel != null && !VisualLevels.Contains(settings.VisualLevel)) throw new InvalidDataException("Nível visual inválido.");
            if (settings.PowerMemory != null)
            {
                var m = settings.PowerMemory;
                if (m.Zones == null || m.Zones.Count > 5 || m.SetupIdentity == null) throw new InvalidDataException("Memória de energia inválida.");
                if (m.Zones.Count > 0) ValidateZones(m.Zones);
                if (m.BoardReportBase64 != null)
                {
                    byte[] report;
                    try { report = Convert.FromBase64String(m.BoardReportBase64); } catch (FormatException) { throw new InvalidDataException("Memória da placa inválida."); }
                    MsiRgbController.ValidateReport(report);
                }
            }
            var aliasKeys = new HashSet<string>();
            foreach (var alias in settings.Aliases)
                if (alias == null || !ValidKey(alias.Key) || !aliasKeys.Add(alias.Key) || String.IsNullOrWhiteSpace(alias.Name) || alias.Name.Length > 80)
                    throw new InvalidDataException("Nome personalizado inválido.");
            foreach (var color in settings.Palette) ParseColor(color);
        }
        static void ValidateZones(List<ZoneProfile> zones)
        {
            var keys = new HashSet<string>();
            foreach (var z in zones)
            {
                if (z == null || !ValidKey(z.Key) || !keys.Add(z.Key) || z.Brightness < 0 || z.Brightness > 100 ||
                    z.Speed < 0 || z.Speed > 100 || !Effects.Contains(z.Effect) ||
                    (z.Key == "X68HE" && z.Effect != "Static" && z.Effect != "Breathing")) throw new InvalidDataException("Configuração de zona inválida.");
                ParseColor(z.ColorHex);
            }
        }
        public static bool ValidKey(string key)
        {
            return new[] { "JRGB1", "JRGB2", "JRAINBOW1", "JRAINBOW2", "X68HE" }.Contains(key);
        }
        public static void Save(string path, PersonalSettings settings)
        {
            Validate(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var temp = path + ".tmp";
            using (var output = File.Create(temp)) new DataContractJsonSerializer(typeof(PersonalSettings)).WriteObject(output, settings);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
    }
}
