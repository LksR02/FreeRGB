// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using LumeRgb;

class SettingsTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, message); }
    static void Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Lume-settings-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        var settings = new PersonalSettings();
        var profile = new LightingProfile { Name = "Meu perfil", BoardSerial = "MSI:test|X68HE:" };
        foreach (string key in new[] { "JRGB1", "JRGB2", "JRAINBOW1", "JRAINBOW2", "X68HE" })
            profile.Zones.Add(new ZoneProfile { Key = key, ColorHex = "#7C6CFF", Brightness = 40, Speed = 50, Effect = "Static" });
        settings.Profiles.Add(profile); settings.Aliases.Add(new ZoneAlias { Key = "JRAINBOW1", Name = "Ventoinhas frontais" }); settings.Palette.Add("#1234AB");
        SettingsStore.Save(path, settings);
        var read = SettingsStore.Load(path);
        Check(read.Profiles[0].Zones.Count == 5 && read.Profiles[0].BoardSerial == "MSI:test|X68HE:", "Profile/device identity roundtrip");
        Check(read.ZoneName("JRAINBOW1", "original") == "Ventoinhas frontais" && read.Palette[0] == "#1234AB", "Aliases/palette roundtrip");
        settings.Profiles[0].Name = "Atualizado"; SettingsStore.Save(path, settings);
        Check(File.Exists(path + ".bak") && SettingsStore.Load(path + ".bak").Profiles[0].Name == "Meu perfil", "Atomic backup");
        profile.Zones[0].Brightness = 101; Reject(() => SettingsStore.Save(path, settings), "Reject invalid brightness before writing");
        Check(SettingsStore.Load(path).Profiles[0].Zones[0].Brightness == 40, "Invalid save preserved file"); profile.Zones[0].Brightness = 40;
        profile.Zones[4].Effect = "Lightning"; Reject(() => SettingsStore.Validate(settings), "Reject unsupported keyboard effect"); profile.Zones[4].Effect = "Static";
        profile.Zones[4].Key = "JRGB1"; Reject(() => SettingsStore.Validate(settings), "Reject duplicate zones"); profile.Zones[4].Key = "X68HE";
        profile.Zones[0].ColorHex = "#GG0000"; Reject(() => SettingsStore.Validate(settings), "Reject invalid RGB"); profile.Zones[0].ColorHex = "#7C6CFF";
        profile.Zones[0].Key = "UNKNOWN"; Reject(() => SettingsStore.Validate(settings), "Reject unknown hardware key"); profile.Zones[0].Key = "JRGB1";
        settings.Profiles.Add(profile); Reject(() => SettingsStore.Validate(settings), "Reject duplicate profile names"); settings.Profiles.RemoveAt(1);
        File.WriteAllText(path, "{not-json"); Reject(() => SettingsStore.Load(path), "Reject malformed JSON");
        Console.WriteLine("PASS settings: five-item profile and identity roundtrip, aliases, palette, atomic backup, invalid save preserves previous file, bounds, unsupported keyboard effect, duplicate zones/names, unknown key, malformed JSON.");
        Console.WriteLine("No hardware commands sent.");
    }
}
