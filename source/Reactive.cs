// SPDX-License-Identifier: GPL-2.0-or-later
// Reactive lighting engine: every sensor sample turns the user's rules into target colours and
// sends only the zones whose real state differs, through LumeService's coalesced quiet writes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LumeRgb
{
    public sealed class ReactiveEngine : IDisposable
    {
        readonly LumeService service;
        readonly ISensorSource sensors;
        readonly object gate = new object();
        readonly Dictionary<string, double> smoothed = new Dictionary<string, double>();
        readonly HashSet<string> controlled = new HashSet<string>();
        List<ZoneSetting> baseline;
        bool active, disposed;
        public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(10);

        public ReactiveEngine(LumeService service, ISensorSource sensors)
        {
            this.service = service; this.sensors = sensors;
            sensors.Updated += OnSensors;
            Refresh();
        }
        void OnSensors(object sender, EventArgs e) { try { Step(); } catch (Exception error) { AppLog.Write("Reações: " + error); } }

        bool Wanted(PersonalSettings settings) { return settings.ReactionsEnabled && settings.Reactions.Any(r => r.Enabled && r.Keys.Count > 0); }

        // Call after the rules or the master switch change.
        public void Refresh()
        {
            bool want = Wanted(service.Settings);
            sensors.Want("rules", want);
            lock (gate) { smoothed.Clear(); if (!want && active) Deactivate(); }
        }

        public static Color Gradient(Color low, Color? mid, Color high, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            if (mid.HasValue) return t < .5 ? Mix(low, mid.Value, t * 2) : Mix(mid.Value, high, (t - .5) * 2);
            return Mix(low, high, t);
        }
        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }
        // What a rule asks for at a given sensor value (also used by the page preview through the same maths in JS).
        public static ZoneSetting Evaluate(ReactiveRule rule, string key, double value)
        {
            double t = (value - rule.Min) / (rule.Max - rule.Min);
            var low = SettingsStore.ParseColor(rule.ColorLow); var high = SettingsStore.ParseColor(rule.ColorHigh);
            Color? mid = rule.ColorMid == null ? (Color?)null : SettingsStore.ParseColor(rule.ColorMid);
            bool alert = rule.AlertAbove.HasValue && value >= rule.AlertAbove.Value;
            int brightness = rule.Brightness <= 0 ? 100 : rule.Brightness;
            if (rule.Mode == "brightness") { brightness = (int)Math.Round(Math.Max(5, Math.Min(100, 100 * t)) * brightness / 100.0); }
            return new ZoneSetting { Key = key, Color = rule.Mode == "brightness" ? high : Gradient(low, mid, high, t),
                BrightnessPercent = Math.Max(1, brightness), Effect = alert ? "Breathing" : "Static", SpeedPercent = 100 };
        }

        public void Step()
        {
            lock (gate)
            {
                if (disposed) return;
                var settings = service.Settings;
                if (!Wanted(settings)) { if (active) Deactivate(); return; }
                var snap = service.Snapshot();
                if (!snap.Connected || snap.Power == "off" || snap.PowerPending || snap.Busy) return;
                if (DateTime.UtcNow - service.LastReactiveFailure < Backoff) return;
                if (!active) { baseline = service.CurrentSettings(); active = true; }

                var targets = new Dictionary<string, ZoneSetting>();
                foreach (var rule in settings.Reactions.Where(r => r.Enabled))
                {
                    var reading = sensors.Get(rule.Sensor);
                    if (reading == null || !reading.Available) continue;
                    double previous, value = reading.Value;
                    if (smoothed.TryGetValue(rule.Id, out previous)) value = previous + .45 * (value - previous); // softens spikes
                    smoothed[rule.Id] = value;
                    foreach (var key in rule.Keys) if (snap.Zones.Any(z => z.Key == key)) targets[key] = Evaluate(rule, key, value);
                }
                controlled.UnionWith(targets.Keys);
                var changes = targets.Values.Where(t => Differs(snap.Zones.First(z => z.Key == t.Key), t)).ToList();
                if (changes.Count > 0) service.ApplyReactive(changes);
            }
        }
        // Compares against the real (read-back) state; the keyboard only has 4 brightness steps, the board 10.
        static bool Differs(RgbZone zone, ZoneSetting target)
        {
            int tolerance = zone.Key == "X68HE" ? 13 : 6;
            if (Math.Abs(zone.CurrentBrightnessPercent - target.BrightnessPercent) > tolerance) return true;
            if (zone.Effect != target.Effect) return true;
            return Math.Abs(zone.CurrentColor.R - target.Color.R) > 6 || Math.Abs(zone.CurrentColor.G - target.Color.G) > 6 || Math.Abs(zone.CurrentColor.B - target.Color.B) > 6;
        }
        // When reactions are switched off, controlled zones go back to how they were before.
        void Deactivate()
        {
            active = false;
            var restore = baseline == null ? new List<ZoneSetting>() : baseline.Where(b => controlled.Contains(b.Key)).ToList();
            baseline = null; controlled.Clear();
            if (restore.Count == 0 || service.Snapshot().Power == "off") return;
            try { service.Apply(restore).ContinueWith(t => { if (t.IsFaulted) AppLog.Write("Reações (restaurar): " + t.Exception.InnerException); }); }
            catch (Exception e) { AppLog.Write("Reações (restaurar): " + e); }
        }
        public bool Active { get { lock (gate) return active; } }
        public void Dispose() { lock (gate) disposed = true; sensors.Updated -= OnSensors; sensors.Want("rules", false); }
    }
}
