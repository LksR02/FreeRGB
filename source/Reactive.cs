// SPDX-License-Identifier: GPL-2.0-or-later
// Reactive lighting engine. It runs on its own clock (1–30 updates per second, user setting),
// glides the sensor values with a time-based smoothing so colours flow between samples, and sends
// only what changed through LumeService's fast, coalesced writes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace LumeRgb
{
    public sealed class ReactiveEngine : IDisposable
    {
        readonly LumeService service;
        readonly ISensorSource sensors;
        readonly object gate = new object();
        readonly Dictionary<string, double> smoothed = new Dictionary<string, double>();
        readonly Dictionary<string, ZoneSetting> sent = new Dictionary<string, ZoneSetting>();
        readonly HashSet<string> controlled = new HashSet<string>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        List<ZoneSetting> baseline;
        bool active, disposed;
        string lastPower;
        DateTime lastBoardWrite = DateTime.MinValue, statsAt = DateTime.UtcNow;
        int statsWrites; double writesPerSecond;
        public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(10);
        // The board rewrites a 185-byte settings report per change; keep it at a pace its firmware handles well.
        public const double BoardMaxPerSecond = 15;

        public ReactiveEngine(LumeService service, ISensorSource sensors) : this(service, sensors, true) { }
        public ReactiveEngine(LumeService service, ISensorSource sensors, bool autoRun)
        {
            this.service = service; this.sensors = sensors;
            Refresh();
            if (autoRun) { thread = new Thread(Run) { IsBackground = true, Name = "Lume reactions" }; thread.Start(); }
        }

        bool Wanted(PersonalSettings settings) { return settings.ReactionsEnabled && settings.Reactions.Any(r => r.Enabled && r.Keys.Count > 0); }

        // Call after the rules, the rhythm or the master switch change.
        public void Refresh()
        {
            bool want = Wanted(service.Settings);
            sensors.Want("rules", want);
            lock (gate)
            {
                // Keep the gliding values of rules that still exist, so changing the rhythm never makes colours jump.
                var ids = new HashSet<string>(service.Settings.Reactions.Select(r => r.Id));
                foreach (var gone in smoothed.Keys.Where(k => !ids.Contains(k)).ToList()) smoothed.Remove(gone);
                sent.Clear(); if (!want && active) Deactivate();
            }
            wake.Set();
        }
        public bool Active { get { lock (gate) return active; } }
        public double WritesPerSecond { get { lock (gate) return writesPerSecond; } }

        void Run()
        {
            var clock = Stopwatch.StartNew(); double last = 0;
            while (!disposed)
            {
                var settings = service.Settings;
                if (!Wanted(settings)) { lock (gate) if (active) Deactivate(); wake.WaitOne(1000); last = clock.Elapsed.TotalSeconds; continue; }
                double now = clock.Elapsed.TotalSeconds, dt = Math.Min(1, now - last); last = now;
                try { Step(dt); } catch (Exception e) { AppLog.Write("Reações: " + e); }
                int period = 1000 / Math.Max(1, Math.Min(30, settings.RateOrDefault));
                int rest = period - (int)((clock.Elapsed.TotalSeconds - now) * 1000);
                wake.WaitOne(Math.Max(5, rest));
            }
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
        // What a rule asks for at a given sensor value (the page preview uses the same maths in JS).
        public static ZoneSetting Evaluate(ReactiveRule rule, string key, double value)
        {
            double t = (value - rule.Min) / (rule.Max - rule.Min);
            var low = SettingsStore.ParseColor(rule.ColorLow); var high = SettingsStore.ParseColor(rule.ColorHigh);
            Color? mid = rule.ColorMid == null ? (Color?)null : SettingsStore.ParseColor(rule.ColorMid);
            bool alert = rule.AlertAbove.HasValue && value >= rule.AlertAbove.Value;
            int brightness = rule.Brightness <= 0 ? 100 : rule.Brightness;
            if (rule.Mode == "brightness") brightness = (int)Math.Round(Math.Max(5, Math.Min(100, 100 * t)) * brightness / 100.0);
            return new ZoneSetting { Key = key, Color = rule.Mode == "brightness" ? high : Gradient(low, mid, high, t),
                BrightnessPercent = Math.Max(1, brightness), Effect = alert ? "Breathing" : "Static", SpeedPercent = 100 };
        }
        // Seconds for the light to cover ~63% of a jump (0 = instant).
        public static double TimeConstant(int smoothing)
        {
            return smoothing <= 0 ? 0 : 0.03 + Math.Pow(Math.Min(100, smoothing) / 100.0, 2) * 2.5;
        }

        public void Step(double dt)
        {
            lock (gate)
            {
                if (disposed) return;
                var settings = service.Settings;
                if (!Wanted(settings)) { if (active) Deactivate(); return; }
                var snap = service.Snapshot();
                if (snap.Power != lastPower) { sent.Clear(); lastPower = snap.Power; } // "Ligar" restored other colours
                if (!snap.Connected || snap.Power == "off" || snap.PowerPending || snap.Busy) return;
                if (DateTime.UtcNow - service.LastReactiveFailure < Backoff) return;
                if (!active) { baseline = service.CurrentSettings(); active = true; sent.Clear(); }
                UpdateStats();

                double tau = TimeConstant(settings.SmoothingOrDefault);
                double alpha = tau <= 0 ? 1 : 1 - Math.Exp(-Math.Max(0, dt) / tau);
                var targets = new Dictionary<string, ZoneSetting>();
                foreach (var rule in settings.Reactions.Where(r => r.Enabled))
                {
                    var reading = sensors.Get(rule.Sensor);
                    if (reading == null || !reading.Available) continue;
                    double previous, value = reading.Value;
                    if (smoothed.TryGetValue(rule.Id, out previous)) value = previous + alpha * (value - previous);
                    smoothed[rule.Id] = value;
                    foreach (var key in rule.Keys) if (snap.Zones.Any(z => z.Key == key)) targets[key] = Evaluate(rule, key, value);
                }
                controlled.UnionWith(targets.Keys);

                var changes = new List<ZoneSetting>();
                foreach (var target in targets.Values)
                {
                    var zone = snap.Zones.First(z => z.Key == target.Key);
                    ZoneSetting last;
                    // Someone else changed the zone (Studio, profile, a periodic read-back): forget what we sent.
                    if (sent.TryGetValue(target.Key, out last) && StateDiffers(zone, last)) sent.Remove(target.Key);
                    if (!sent.TryGetValue(target.Key, out last) || Visible(last, target)) changes.Add(target);
                }
                if (changes.Any(c => LumeService.BoardKeys.Contains(c.Key)))
                {
                    if ((DateTime.UtcNow - lastBoardWrite).TotalSeconds < 1 / BoardMaxPerSecond) changes.RemoveAll(c => LumeService.BoardKeys.Contains(c.Key));
                    else lastBoardWrite = DateTime.UtcNow;
                }
                if (changes.Count == 0) return;
                foreach (var c in changes) sent[c.Key] = c;
                service.ApplyReactive(changes);
            }
        }
        static int Level(string key, int brightness) { return key == "X68HE" ? (brightness + 12) / 25 : (brightness + 5) / 10; }
        // Is the new target different enough to be worth a USB write? (1/255 colour steps, physical brightness levels)
        static bool Visible(ZoneSetting a, ZoneSetting b)
        {
            return a.Effect != b.Effect || Level(a.Key, a.BrightnessPercent) != Level(b.Key, b.BrightnessPercent) ||
                Math.Abs(a.Color.R - b.Color.R) > 1 || Math.Abs(a.Color.G - b.Color.G) > 1 || Math.Abs(a.Color.B - b.Color.B) > 1;
        }
        static bool StateDiffers(RgbZone zone, ZoneSetting sentValue)
        {
            if (Level(zone.Key, zone.CurrentBrightnessPercent) != Level(zone.Key, sentValue.BrightnessPercent)) return true;
            if (zone.Effect != sentValue.Effect) return true;
            return Math.Abs(zone.CurrentColor.R - sentValue.Color.R) > 6 || Math.Abs(zone.CurrentColor.G - sentValue.Color.G) > 6 || Math.Abs(zone.CurrentColor.B - sentValue.Color.B) > 6;
        }
        void UpdateStats()
        {
            double seconds = (DateTime.UtcNow - statsAt).TotalSeconds;
            if (seconds < 1) return;
            int writes = service.ReactiveWrites;
            writesPerSecond = Math.Round((writes - statsWrites) / seconds, 1); statsWrites = writes; statsAt = DateTime.UtcNow;
        }
        // When reactions are switched off, controlled zones go back to how they were before.
        void Deactivate()
        {
            active = false; writesPerSecond = 0;
            var restore = baseline == null ? new List<ZoneSetting>() : baseline.Where(b => controlled.Contains(b.Key)).ToList();
            baseline = null; controlled.Clear(); sent.Clear(); smoothed.Clear();
            if (restore.Count == 0 || service.Snapshot().Power == "off") return;
            try { service.Apply(restore).ContinueWith(t => { if (t.IsFaulted) AppLog.Write("Reações (restaurar): " + t.Exception.InnerException); }); }
            catch (Exception e) { AppLog.Write("Reações (restaurar): " + e); }
        }
        public void Dispose()
        {
            lock (gate) disposed = true; wake.Set();
            if (thread != null) try { thread.Join(1000); } catch { }
            sensors.Want("rules", false);
        }
    }
}
