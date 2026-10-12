// SPDX-License-Identifier: GPL-2.0-or-later
// Lume RGB application core: one background worker serializes every hardware request, so the
// main window, the tray menu, desktop widgets and the global shortcut can all act at once safely.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRgb
{
    public static class AppLog
    {
        public static string Path
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumeRGBStandalone", "erro.log"); }
        }
        public static void Write(string text)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                var info = new FileInfo(Path); if (info.Exists && info.Length > 512 * 1024) info.Delete(); // keep the log small
                File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n\r\n");
            }
            catch { }
        }
    }

    public sealed class Scene
    {
        public string Id, Name, Color, Effect, Glow;
        public int Brightness, Speed;
    }

    public sealed class LumeSnapshot
    {
        public bool Busy, StatusError, Connected, PowerPending, InvalidSettings;
        public string BusyText, Status, SetupIdentity;
        public string Power; // on, off, mixed or none
        public int DeviceCount, Version;
        public List<RgbZone> Zones = new List<RgbZone>();
        public List<HidDeviceInfo> Devices = new List<HidDeviceInfo>();
        public List<string> Notes = new List<string>();
    }

    public sealed class LumeService : IDisposable
    {
        public static readonly Scene[] Scenes = {
            new Scene { Id = "aurora", Name = "Aurora", Color = "#7C6CFF", Glow = "#35D9C3", Effect = "Static", Brightness = 100, Speed = 50 },
            new Scene { Id = "ocean", Name = "Oceano", Color = "#1FA8FF", Glow = "#00F0D0", Effect = "Breathing", Brightness = 100, Speed = 25 },
            new Scene { Id = "forest", Name = "Floresta", Color = "#2BD96B", Glow = "#B7FF5C", Effect = "Static", Brightness = 90, Speed = 50 },
            new Scene { Id = "sunset", Name = "Pôr do sol", Color = "#FF6A2B", Glow = "#FF2E88", Effect = "Static", Brightness = 100, Speed = 50 },
            new Scene { Id = "sakura", Name = "Sakura", Color = "#FF4FA3", Glow = "#FFC2E2", Effect = "Breathing", Brightness = 100, Speed = 25 },
            new Scene { Id = "focus", Name = "Foco", Color = "#FFD7A1", Glow = "#FFF4E0", Effect = "Static", Brightness = 60, Speed = 50 },
            new Scene { Id = "night", Name = "Noite", Color = "#3A2BFF", Glow = "#120B4D", Effect = "Breathing", Brightness = 20, Speed = 0 },
            new Scene { Id = "storm", Name = "Tempestade", Color = "#B8C6FF", Glow = "#4B6BFF", Effect = "Lightning", Brightness = 100, Speed = 100 },
        };
        public static readonly string[] BoardKeys = { "JRGB1", "JRGB2", "JRAINBOW1", "JRAINBOW2" };

        readonly Func<List<HidDeviceInfo>, ILightingBackend> backendFactory;
        readonly Func<List<HidDeviceInfo>> enumerate;
        readonly Func<string> conflictCheck;
        readonly object gate = new object();
        readonly Queue<Action> jobs = new Queue<Action>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Thread worker;
        volatile bool stopping;
        ILightingBackend backend;
        List<RgbZone> zones = new List<RgbZone>();
        List<HidDeviceInfo> devices = new List<HidDeviceInfo>();
        bool busy, statusError, invalidSettings, scannedOnce;
        string busyText = "", status = "Iniciando…";
        bool? desiredPower; Task powerTask; bool powerQueued;
        int version;

        public string SettingsPath { get; private set; }
        public PersonalSettings Settings { get; private set; }
        public event EventHandler Changed;

        public LumeService(string settingsPath, Func<List<HidDeviceInfo>, ILightingBackend> backendFactory, Func<List<HidDeviceInfo>> enumerate, Func<string> conflictCheck)
        {
            SettingsPath = settingsPath; this.backendFactory = backendFactory; this.enumerate = enumerate; this.conflictCheck = conflictCheck;
            try { Settings = SettingsStore.Load(settingsPath); }
            catch (Exception e) { Settings = new PersonalSettings(); invalidSettings = true; status = "Configuração inválida; o arquivo foi preservado: " + e.Message; statusError = true; }
            worker = new Thread(Run) { IsBackground = true, Name = "Lume hardware" };
            worker.Start();
        }
        public static LumeService CreateDefault()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumeRGBStandalone", "settings.json");
            return new LumeService(path, found => { var hub = new LightingHub(); hub.Detect(found); return hub; }, WindowsDevices.GetHidDevices, () =>
                Process.GetProcessesByName("OpenRGB").Length != 0 ? "O OpenRGB está aberto. Feche-o, inclusive na bandeja, para que apenas o Lume controle a iluminação." : null);
        }

        // ---------- worker ----------
        void Run()
        {
            while (!stopping)
            {
                Action job = null;
                lock (gate) { if (jobs.Count > 0) job = jobs.Dequeue(); }
                if (job == null) { signal.WaitOne(500); continue; }
                job();
            }
        }
        Task Enqueue(string text, Action work, Func<string> success) { return Enqueue(text, work, success, false); }
        // quiet jobs (reactive lighting) run without the busy indicator so the UI does not flicker every second.
        Task Enqueue(string text, Action work, Func<string> success, bool quiet)
        {
            var completion = new TaskCompletionSource<object>();
            Action job = delegate
            {
                if (!quiet) { lock (gate) { busy = true; busyText = text; } Raise(); }
                Exception failure = null; string message = null;
                try { work(); if (success != null) message = success(); }
                catch (Exception e) { failure = e; }
                lock (gate)
                {
                    busy = jobs.Count > 0;
                    if (failure != null) { status = Friendly(failure); statusError = true; }
                    else if (message != null) { status = message; statusError = false; }
                }
                Raise();
                if (failure != null) completion.TrySetException(failure); else completion.TrySetResult(null);
            };
            lock (gate) { jobs.Enqueue(job); }
            signal.Set();
            return completion.Task;
        }
        static string Friendly(Exception e)
        {
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            return e.Message;
        }
        void Raise()
        {
            lock (gate) version++;
            var handler = Changed; if (handler != null) try { handler(this, EventArgs.Empty); } catch { }
        }

        // ---------- state ----------
        public LumeSnapshot Snapshot()
        {
            lock (gate)
            {
                var s = new LumeSnapshot
                {
                    Busy = busy, BusyText = busyText, Status = status, StatusError = statusError, InvalidSettings = invalidSettings,
                    Connected = backend != null && backend.IsConnected, DeviceCount = backend == null ? 0 : backend.DeviceCount,
                    SetupIdentity = backend == null ? "" : backend.SetupIdentity, Version = version,
                    Zones = zones.Select(Copy).ToList(), Devices = devices.ToList(),
                    Notes = backend == null ? new List<string>() : backend.DiscoveryNotes.ToList(),
                    PowerPending = desiredPower.HasValue
                };
                s.Power = desiredPower.HasValue ? (desiredPower.Value ? "on" : "off") : PowerOf(zones);
                return s;
            }
        }
        static RgbZone Copy(RgbZone z)
        {
            return new RgbZone { Key = z.Key, Name = z.Name, Effect = z.Effect, LedCount = z.LedCount, CurrentBrightnessPercent = z.CurrentBrightnessPercent,
                CurrentSpeedPercent = z.CurrentSpeedPercent, CurrentColor = z.CurrentColor };
        }
        public static string PowerOf(List<RgbZone> list)
        {
            if (list == null || list.Count == 0) return "none";
            int lit = list.Count(z => z.CurrentBrightnessPercent > 0);
            return lit == 0 ? "off" : lit == list.Count ? "on" : "mixed";
        }
        public string ZoneName(string key)
        {
            string friendly = key == "X68HE" ? "Teclado X68HE" : key.Replace("JRAINBOW", "ARGB ").Replace("JRGB", "RGB ");
            lock (gate) return Settings.ZoneName(key, friendly);
        }
        public string[] EffectsFor(string key)
        {
            return key == "X68HE" ? X68KeyboardController.SupportedEffects : MsiRgbController.SupportedEffects;
        }
        string Normalize(string key, string effect)
        {
            return EffectsFor(key).Contains(effect) ? effect : (effect == "Breathing" ? "Breathing" : "Static");
        }

        // ---------- hardware jobs ----------
        public Task Scan()
        {
            return Enqueue("Procurando dispositivos…", ScanNow, delegate
            {
                lock (gate)
                {
                    if (backend.DiscoveryNotes.Count > 0) return String.Join("  ", backend.DiscoveryNotes.ToArray());
                    return backend.IsConnected ? "Pronto. " + zones.Count + " zonas de iluminação encontradas." : "Nenhum controlador RGB compatível foi encontrado.";
                }
            });
        }
        void ScanNow()
        {
            var found = enumerate();
            ILightingBackend next = backendFactory(found);
            var list = next.IsConnected ? next.GetZones() : new List<RgbZone>();
            lock (gate)
            {
                if (backend != null) backend.Dispose();
                backend = next; devices = found; zones = list; scannedOnce = true;
                foreach (var z in zones) z.Name = ZoneNameUnlocked(z.Key);
            }
        }
        string ZoneNameUnlocked(string key)
        {
            string friendly = key == "X68HE" ? "Teclado X68HE" : key.Replace("JRAINBOW", "ARGB ").Replace("JRGB", "RGB ");
            return Settings.ZoneName(key, friendly);
        }
        void EnsureReady()
        {
            string conflict = conflictCheck == null ? null : conflictCheck();
            if (conflict != null) throw new InvalidOperationException(conflict);
            bool connected; lock (gate) connected = backend != null && backend.IsConnected;
            if (!connected) ScanNow();
            lock (gate) if (backend == null || !backend.IsConnected) throw new InvalidOperationException("Nenhum dispositivo de iluminação compatível está conectado.");
        }
        void Refresh()
        {
            List<RgbZone> list;
            try { list = backend.GetZones(); }
            catch { lock (gate) zones = new List<RgbZone>(); throw; }
            lock (gate) { zones = list; foreach (var z in zones) z.Name = ZoneNameUnlocked(z.Key); }
        }
        void ApplyNow(List<ZoneSetting> settings)
        {
            try { backend.Apply(settings); }
            catch { TryRefresh(); throw; }
            // An explicit change replaces whatever "Desligar" remembered for those zones.
            ForgetPower(settings.Where(s => s.BrightnessPercent > 0).Select(s => s.Key));
            Refresh();
        }
        void TryRefresh() { try { if (backend != null && backend.IsConnected) Refresh(); else lock (gate) zones = new List<RgbZone>(); } catch { } }

        public Task Apply(List<ZoneSetting> settings)
        {
            if (settings == null || settings.Count == 0) throw new ArgumentException("Selecione ao menos uma zona.");
            foreach (var s in settings) s.Effect = Normalize(s.Key, s.Effect);
            return Enqueue("Aplicando iluminação…", delegate { EnsureReady(); ApplyNow(settings); },
                () => "Iluminação atualizada" + (backend.LastWriteVerified ? " e confirmada pelos dispositivos." : "."));
        }
        public Task ApplyScene(string id)
        {
            var scene = Scenes.FirstOrDefault(s => s.Id == id);
            if (scene == null) throw new ArgumentException("Cena desconhecida.");
            return ApplyColorToAll(scene.Color, scene.Effect, scene.Brightness, scene.Speed, "Cena " + scene.Name + " aplicada.");
        }
        public Task ApplyColorToAll(string hex, string effect, int brightness, int speed, string message)
        {
            Color color = SettingsStore.ParseColor(hex);
            return Enqueue("Aplicando cor a tudo…", delegate
            {
                EnsureReady();
                List<string> keys; lock (gate) keys = zones.Select(z => z.Key).ToList();
                ApplyNow(keys.Select(k => new ZoneSetting { Key = k, Color = color, Effect = Normalize(k, effect), BrightnessPercent = brightness, SpeedPercent = speed }).ToList());
            }, () => message ?? "Cor aplicada a todas as zonas.");
        }

        // ---------- power ----------
        public bool IsOnOrPending
        {
            get { lock (gate) return desiredPower.HasValue ? desiredPower.Value : PowerOf(zones) != "off"; }
        }
        public Task TogglePower() { return SetPower(!IsOnOrPending); }
        public Task SetPower(bool on)
        {
            Task queued = null;
            lock (gate)
            {
                desiredPower = on;
                if (powerQueued) queued = powerTask;
            }
            if (queued != null) { Raise(); return queued; } // The pending job picks up the newest target.
            lock (gate)
            {
                powerQueued = true;
                bool target = on;
                powerTask = Enqueue(on ? "Ligando tudo…" : "Desligando tudo…", delegate
                {
                    lock (gate) { target = desiredPower.Value; powerQueued = false; busyText = target ? "Ligando tudo…" : "Desligando tudo…"; }
                    try { EnsureReady(); if (target) PowerOnNow(); else PowerOffNow(); }
                    finally { lock (gate) if (!powerQueued) desiredPower = null; }
                }, () => target ? "Tudo ligado. Iluminação restaurada." : "Tudo desligado. A configuração foi guardada para quando você ligar.");
                return powerTask;
            }
        }
        void PowerOffNow()
        {
            Refresh();
            List<RgbZone> current; string identity;
            lock (gate) { current = zones.Select(Copy).ToList(); identity = backend.SetupIdentity; }
            if (PowerOf(current) == "off") return;
            var memory = new PowerMemory { SetupIdentity = identity ?? "" };
            foreach (var z in current)
                memory.Zones.Add(new ZoneProfile { Key = z.Key, ColorHex = SettingsStore.Hex(z.CurrentColor), Brightness = z.CurrentBrightnessPercent,
                    Effect = Normalize(z.Key, z.Effect), Speed = z.CurrentSpeedPercent });
            if (backend.HasBoard)
            {
                byte[] report = backend.ReadBoardSnapshot();
                if (report != null) memory.BoardReportBase64 = Convert.ToBase64String(report);
            }
            // Remember first: if anything fails mid-way, "Ligar" still knows what to restore.
            Mutate(s => s.PowerMemory = memory);
            ApplyOff(current);
        }
        void ApplyOff(List<RgbZone> current)
        {
            var off = current.Select(z => new ZoneSetting { Key = z.Key, Color = z.CurrentColor, BrightnessPercent = 0,
                Effect = Normalize(z.Key, z.Effect), SpeedPercent = z.CurrentSpeedPercent }).ToList();
            try { backend.Apply(off); } catch { TryRefresh(); throw; }
            Refresh();
        }
        void PowerOnNow()
        {
            PowerMemory memory; string identity;
            lock (gate) { memory = Settings.PowerMemory; identity = backend.SetupIdentity ?? ""; }
            if (memory != null && memory.SetupIdentity != identity) memory = null;
            if (memory != null && memory.BoardReportBase64 != null && backend.HasBoard)
            {
                try { backend.RestoreBoard(Convert.FromBase64String(memory.BoardReportBase64)); }
                catch { TryRefresh(); throw; }
            }
            Refresh();
            List<RgbZone> current; lock (gate) current = zones.Select(Copy).ToList();
            var targets = new List<ZoneSetting>();
            foreach (var z in current)
            {
                if (z.CurrentBrightnessPercent > 0) continue;
                var saved = memory == null ? null : memory.Zones.FirstOrDefault(m => m.Key == z.Key);
                Color color = saved != null ? SettingsStore.ParseColor(saved.ColorHex) : z.CurrentColor;
                if (color.R + color.G + color.B < 24) color = Color.White;
                targets.Add(new ZoneSetting { Key = z.Key, Color = color, BrightnessPercent = saved != null && saved.Brightness > 0 ? saved.Brightness : 100,
                    Effect = Normalize(z.Key, saved != null ? saved.Effect : z.Effect), SpeedPercent = saved != null ? saved.Speed : z.CurrentSpeedPercent });
            }
            // The board accepts a partial change only outside its synchronized modes; send all four headers together.
            if (targets.Any(t => BoardKeys.Contains(t.Key)))
                foreach (var z in current.Where(c => BoardKeys.Contains(c.Key) && !targets.Any(t => t.Key == c.Key)))
                    targets.Add(new ZoneSetting { Key = z.Key, Color = z.CurrentColor, BrightnessPercent = z.CurrentBrightnessPercent,
                        Effect = Normalize(z.Key, z.Effect), SpeedPercent = z.CurrentSpeedPercent });
            if (targets.Count > 0) { try { backend.Apply(targets); } catch { TryRefresh(); throw; } Refresh(); }
            Mutate(s => s.PowerMemory = null);
        }
        void ForgetPower(IEnumerable<string> keys)
        {
            var list = keys.ToList(); if (list.Count == 0) return;
            lock (gate) if (Settings.PowerMemory == null) return;
            try
            {
                Mutate(s =>
                {
                    var m = s.PowerMemory; if (m == null) return;
                    m.Zones = m.Zones.Where(z => !list.Contains(z.Key)).ToList();
                    if (list.Any(k => BoardKeys.Contains(k))) m.BoardReportBase64 = null;
                    if (m.Zones.Count == 0) s.PowerMemory = null;
                });
            }
            catch { }
        }

        // ---------- reactive lighting ----------
        List<ZoneSetting> pendingReactive; bool reactiveQueued; DateTime reactiveFailedAt = DateTime.MinValue;
        public DateTime LastReactiveFailure { get { lock (gate) return reactiveFailedAt; } }
        // Coalesced: while a reactive write is waiting, newer targets replace it instead of piling up.
        public void ApplyReactive(List<ZoneSetting> settings)
        {
            if (settings == null || settings.Count == 0) return;
            lock (gate) { pendingReactive = settings; if (reactiveQueued) return; reactiveQueued = true; }
            Enqueue(null, delegate
            {
                List<ZoneSetting> list;
                lock (gate)
                {
                    list = pendingReactive; pendingReactive = null; reactiveQueued = false;
                    if (list == null || desiredPower.HasValue || backend == null || !backend.IsConnected || PowerOf(zones) == "off") return;
                    list = list.Where(t => zones.Any(z => z.Key == t.Key)).ToList();
                    if (list.Count == 0) return;
                }
                string conflict = conflictCheck == null ? null : conflictCheck();
                if (conflict != null) throw new InvalidOperationException(conflict);
                foreach (var t in list) t.Effect = Normalize(t.Key, t.Effect);
                try
                {
                    try { backend.Apply(list); }
                    catch (NotSupportedException)
                    {
                        // Board still in a synchronized mode: send all four headers once, keeping the others as they are.
                        if (!list.Any(t => BoardKeys.Contains(t.Key))) throw;
                        List<RgbZone> current; lock (gate) current = zones.Select(Copy).ToList();
                        foreach (var z in current.Where(c => BoardKeys.Contains(c.Key) && !list.Any(t => t.Key == c.Key)))
                            list.Add(new ZoneSetting { Key = z.Key, Color = z.CurrentColor, BrightnessPercent = z.CurrentBrightnessPercent, Effect = Normalize(z.Key, z.Effect), SpeedPercent = z.CurrentSpeedPercent });
                        backend.Apply(list);
                    }
                }
                catch { lock (gate) reactiveFailedAt = DateTime.UtcNow; TryRefresh(); throw; }
                Refresh();
            }, null, true);
        }
        // Completes once every job queued before it has run (jobs are strictly FIFO).
        public Task Idle() { return Enqueue(null, delegate { }, null, true); }
        public List<ZoneSetting> CurrentSettings()
        {
            lock (gate) return zones.Select(z => new ZoneSetting { Key = z.Key, Color = z.CurrentColor, BrightnessPercent = z.CurrentBrightnessPercent,
                Effect = Normalize(z.Key, z.Effect), SpeedPercent = z.CurrentSpeedPercent }).ToList();
        }

        // ---------- profiles ----------
        public Task SaveProfile(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Dê um nome ao perfil.");
            if (name.Length > 80) throw new ArgumentException("Nome de perfil longo demais.");
            return Enqueue("Lendo o estado atual para salvar o perfil…", delegate
            {
                EnsureReady(); Refresh();
                List<RgbZone> current; string identity;
                lock (gate) { current = zones.Select(Copy).ToList(); identity = backend.SetupIdentity; }
                var profile = new LightingProfile { Name = name, BoardSerial = String.IsNullOrEmpty(identity) ? "local" : identity,
                    Zones = current.Select(z => new ZoneProfile { Key = z.Key, ColorHex = SettingsStore.Hex(z.CurrentColor), Brightness = z.CurrentBrightnessPercent,
                        Effect = Normalize(z.Key, z.Effect), Speed = z.CurrentSpeedPercent }).ToList() };
                Mutate(s => { s.Profiles = s.Profiles.Where(p => !String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList(); s.Profiles.Add(profile); });
            }, () => "Perfil “" + name + "” salvo com o estado atual de todas as zonas.");
        }
        public Task ApplyProfile(string name)
        {
            LightingProfile profile; lock (gate) profile = Settings.Profiles.FirstOrDefault(p => p.Name == name);
            if (profile == null) throw new ArgumentException("Perfil não encontrado.");
            var targets = profile.Zones.Select(z => new ZoneSetting { Key = z.Key, Color = SettingsStore.ParseColor(z.ColorHex),
                BrightnessPercent = z.Brightness, Effect = z.Effect, SpeedPercent = z.Speed }).ToList();
            return Enqueue("Aplicando perfil " + name + "…", delegate
            {
                EnsureReady();
                string identity; lock (gate) identity = backend.SetupIdentity;
                if (profile.BoardSerial != identity) throw new InvalidOperationException("Este perfil foi salvo com outro conjunto de dispositivos. Nada foi alterado.");
                ApplyNow(targets);
            }, () => "Perfil “" + name + "” aplicado.");
        }
        public void DeleteProfile(string name) { Mutate(s => s.Profiles = s.Profiles.Where(p => p.Name != name).ToList()); Raise(); }
        public void RenameProfile(string name, string next)
        {
            next = (next ?? "").Trim();
            if (next.Length == 0 || next.Length > 80) throw new ArgumentException("Nome de perfil inválido.");
            Mutate(s =>
            {
                if (s.Profiles.Any(p => p.Name != name && String.Equals(p.Name, next, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Já existe um perfil com esse nome.");
                var target = s.Profiles.FirstOrDefault(p => p.Name == name); if (target == null) throw new ArgumentException("Perfil não encontrado.");
                target.Name = next;
            });
            Raise();
        }
        public void RenameZone(string key, string name)
        {
            name = (name ?? "").Trim();
            if (!SettingsStore.ValidKey(key)) throw new ArgumentException("Zona desconhecida.");
            if (name.Length > 80) throw new ArgumentException("Nome longo demais.");
            Mutate(s =>
            {
                s.Aliases = s.Aliases.Where(a => a.Key != key).ToList();
                if (name.Length > 0) s.Aliases.Add(new ZoneAlias { Key = key, Name = name });
            });
            lock (gate) foreach (var z in zones) z.Name = ZoneNameUnlocked(z.Key);
            Raise();
        }

        // ---------- settings ----------
        public bool InvalidSettings { get { lock (gate) return invalidSettings; } }
        // Changes settings atomically: validated, written to disk, rolled back if anything fails.
        public void Mutate(Action<PersonalSettings> change)
        {
            lock (gate)
            {
                if (invalidSettings) throw new InvalidOperationException("O arquivo de configuração original é inválido; importe uma configuração válida antes de salvar.");
                var backup = Clone(Settings);
                try { change(Settings); SettingsStore.Save(SettingsPath, Settings); }
                catch { Settings = backup; throw; }
            }
        }
        public void ReplaceSettings(PersonalSettings imported)
        {
            SettingsStore.Validate(imported);
            lock (gate) { SettingsStore.Save(SettingsPath, imported); Settings = imported; invalidSettings = false; status = "Configurações importadas."; statusError = false;
                foreach (var z in zones) z.Name = ZoneNameUnlocked(z.Key); }
            Raise();
        }
        public PersonalSettings CopySettings() { lock (gate) return Clone(Settings); }
        public void NotifyChanged() { Raise(); }
        public void SetStatus(string text, bool error) { lock (gate) { status = text; statusError = error; } Raise(); }
        static PersonalSettings Clone(PersonalSettings value)
        {
            var serializer = new DataContractJsonSerializer(typeof(PersonalSettings));
            using (var buffer = new MemoryStream()) { serializer.WriteObject(buffer, value); buffer.Position = 0; return (PersonalSettings)serializer.ReadObject(buffer); }
        }
        public bool ScannedOnce { get { lock (gate) return scannedOnce; } }

        public void Dispose()
        {
            stopping = true; signal.Set();
            try { worker.Join(2500); } catch { }
            lock (gate) { if (backend != null) backend.Dispose(); backend = null; }
        }
    }
}
