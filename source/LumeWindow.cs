// SPDX-License-Identifier: GPL-2.0-or-later
// Main window: hosts the HTML/WebGPU interface (folder "ui") in WebView2 and exposes the
// LumeService to it through a small JSON message bridge. The page is served from a local
// virtual host; nothing is downloaded from the internet.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LumeRgb
{
    public sealed class LumeWindow : Form
    {
        static readonly Color Background = Color.FromArgb(9, 8, 16);
        readonly LumeContext app;
        readonly WebView2 web;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        bool ready, watchingSensors;
        LumeService Service { get { return app.Service; } }
        DateTime sensorsPushedAt;
        void OnSensors(object sender, EventArgs e)
        {
            if ((DateTime.UtcNow - sensorsPushedAt).TotalMilliseconds < 180) return; // the page animates in between
            sensorsPushedAt = DateTime.UtcNow; app.Post(PushSensors);
        }
        void WatchSensors(bool on)
        {
            on = on && Visible;
            if (on == watchingSensors) return;
            watchingSensors = on;
            if (on) app.Sensors.Updated += OnSensors; else app.Sensors.Updated -= OnSensors;
            app.Sensors.Want("ui", on);
            if (on) PushSensors();
        }
        void PushSensors()
        {
            if (!ready || IsDisposed || web.CoreWebView2 == null) return;
            try { web.CoreWebView2.PostWebMessageAsJson(json.Serialize(new Dictionary<string, object> { { "type", "sensors" }, { "sensors", SensorList() },
                { "engine", new Dictionary<string, object> { { "active", app.Reactions.Active }, { "writesPerSecond", app.Reactions.WritesPerSecond } } },
                { "advanced", new Dictionary<string, object> { { "state", app.Advanced.State }, { "detail", app.Advanced.Detail } } } })); } catch { }
        }
        List<Dictionary<string, object>> SensorList()
        {
            return app.Sensors.Snapshot().Select(r => new Dictionary<string, object> { { "id", r.Id }, { "name", r.Name }, { "unit", r.Unit }, { "available", r.Available },
                { "value", r.Value }, { "source", r.Source }, { "reason", r.Reason }, { "min", r.DefaultMin }, { "max", r.DefaultMax } }).ToList();
        }

        public LumeWindow(LumeContext app, bool devTools)
        {
            this.app = app;
            Text = "Lume RGB"; Icon = app.AppIcon; BackColor = Background; StartPosition = FormStartPosition.CenterScreen;
            var area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1360, area.Width - 40), Math.Min(880, area.Height - 40)); MinimumSize = new Size(980, 660);
            TopMost = Service.Settings.AlwaysOnTop;
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
            Controls.Add(web);
            EventHandler onState = delegate { PushState(); };
            app.StateChanged += onState;
            FormClosed += delegate { app.StateChanged -= onState; WatchSensors(false); };
            VisibleChanged += delegate { if (!Visible) WatchSensors(false); };
            Load += async delegate { await Initialize(devTools); };
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar matching the interface (Windows 10 20H1+ / Windows 11; ignored elsewhere).
            int on = 1, caption = 0x0010080A, text = 0x00F5EEF0;
            DwmSetWindowAttribute(Handle, 20, ref on, 4); DwmSetWindowAttribute(Handle, 19, ref on, 4);
            DwmSetWindowAttribute(Handle, 35, ref caption, 4); DwmSetWindowAttribute(Handle, 36, ref text, 4);
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        async Task Initialize(bool devTools)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
            if (!File.Exists(Path.Combine(folder, "index.html"))) { Fallback("A pasta “ui” não foi encontrada ao lado do LumeRGB.exe. Extraia o zip completo."); return; }
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumeRGBStandalone", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--enable-unsafe-webgpu --ignore-gpu-blocklist"));
                await web.EnsureCoreWebView2Async(environment);
                var core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = devTools; core.Settings.AreDefaultContextMenusEnabled = devTools;
                core.Settings.IsStatusBarEnabled = false; core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = devTools; core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
                core.SetVirtualHostNameToFolderMapping("lume.app", folder, CoreWebView2HostResourceAccessKind.DenyCors);
                core.WebMessageReceived += OnMessage;
                core.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://lume.app/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
                core.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
                core.NavigationCompleted += delegate { ready = true; PushState(); };
                core.ProcessFailed += (s, e) => { if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) core.Reload(); };
                core.Navigate("https://lume.app/index.html");
            }
            catch (WebView2RuntimeNotFoundException)
            {
                Fallback("Esta interface usa o Microsoft Edge WebView2, que vem com o Windows 10/11. Ele não foi encontrado neste PC.\r\n\r\n" +
                    "Instale o “WebView2 Runtime” gratuito da Microsoft e abra o Lume novamente. Enquanto isso, o ícone da bandeja, os widgets e o atalho Ctrl + Alt + L continuam funcionando.", true);
            }
            catch (Exception e) { Program.Log("WebView2: " + e); Fallback("Não foi possível iniciar a interface: " + e.Message + "\r\n\r\nDetalhes em " + Program.LogPath); }
        }
        void Fallback(string message) { Fallback(message, false); }
        void Fallback(string message, bool offerDownload)
        {
            web.Visible = false;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(60), RowCount = 3, ColumnCount = 1 };
            panel.Controls.Add(new Label { Text = "Lume RGB", Font = new Font("Segoe UI", 26, FontStyle.Bold), ForeColor = Color.White, AutoSize = true });
            panel.Controls.Add(new Label { Text = message, Font = new Font("Segoe UI", 12), ForeColor = Color.FromArgb(200, 200, 220), AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 16, 0, 16) });
            if (offerDownload)
            {
                var link = new LinkLabel { Text = "Baixar o WebView2 Runtime (site da Microsoft)", Font = new Font("Segoe UI", 12, FontStyle.Bold), LinkColor = Color.FromArgb(160, 150, 255), AutoSize = true };
                link.Click += delegate { OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/"); };
                panel.Controls.Add(link);
            }
            Controls.Add(panel); panel.BringToFront();
        }
        static void OpenExternal(string url)
        {
            if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) try { Process.Start(url); } catch { }
        }

        // ---------- bridge ----------
        void PushState()
        {
            if (!ready || IsDisposed || web.CoreWebView2 == null) return;
            try { web.CoreWebView2.PostWebMessageAsJson(json.Serialize(new Dictionary<string, object> { { "type", "state" }, { "state", BuildState() } })); }
            catch (Exception) { }
        }
        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> message;
            try { message = json.Deserialize<Dictionary<string, object>>(e.TryGetWebMessageAsString()); } catch { return; }
            if (message == null) return;
            object id; message.TryGetValue("id", out id);
            string command = Str(message, "cmd");
            object raw; message.TryGetValue("args", out raw);
            var args = raw as Dictionary<string, object> ?? new Dictionary<string, object>();
            Task<object> task;
            try { task = Dispatch(command, args); }
            catch (Exception error) { task = Failed(error); }
            task.ContinueWith(t => app.Post(() => Reply(id, t)));
        }
        void Reply(object id, Task<object> task)
        {
            if (IsDisposed || web.CoreWebView2 == null || id == null) return;
            var reply = new Dictionary<string, object> { { "type", "reply" }, { "id", id }, { "ok", !task.IsFaulted } };
            if (task.IsFaulted) { var error = task.Exception.InnerException ?? task.Exception; while (error is AggregateException && error.InnerException != null) error = error.InnerException; reply["error"] = error.Message; }
            else reply["result"] = task.Result;
            try { web.CoreWebView2.PostWebMessageAsJson(json.Serialize(reply)); } catch { }
        }
        static Task<object> Done(object value) { var t = new TaskCompletionSource<object>(); t.SetResult(value); return t.Task; }
        static Task<object> Failed(Exception e) { var t = new TaskCompletionSource<object>(); t.SetException(e); return t.Task; }
        // Modal dialogs must not run inside a WebView2 event handler; run them right after it returns.
        Task<object> Later(Func<object> action)
        {
            var completion = new TaskCompletionSource<object>();
            app.Post(() => { try { completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } });
            return completion.Task;
        }
        static Task<object> After(Task task) { return task.ContinueWith(t => { if (t.IsFaulted) throw t.Exception.InnerException ?? t.Exception; return (object)true; }); }

        Task<object> Dispatch(string command, Dictionary<string, object> a)
        {
            switch (command)
            {
                case "getState": return Done(BuildState());
                case "scan": return After(Service.Scan());
                case "toggle": return After(Service.TogglePower());
                case "power": return After(Service.SetPower(Bool(a, "on")));
                case "scene": return After(Service.ApplyScene(Str(a, "id")));
                case "applyAll": return After(Service.ApplyColorToAll(Str(a, "color"), Str(a, "effect") ?? "Static", Int(a, "brightness"), Int(a, "speed"), null));
                case "apply":
                {
                    var color = SettingsStore.ParseColor(Str(a, "color"));
                    var settings = List(a, "keys").Select(k => new ZoneSetting { Key = k, Color = color, Effect = Str(a, "effect") ?? "Static",
                        BrightnessPercent = Int(a, "brightness"), SpeedPercent = Int(a, "speed") }).ToList();
                    return After(Service.Apply(settings));
                }
                case "saveProfile": return After(Service.SaveProfile(Str(a, "name")));
                case "applyProfile": return After(Service.ApplyProfile(Str(a, "name")));
                case "deleteProfile": Service.DeleteProfile(Str(a, "name")); return Done(true);
                case "renameProfile": Service.RenameProfile(Str(a, "name"), Str(a, "next")); return Done(true);
                case "renameZone": Service.RenameZone(Str(a, "key"), Str(a, "name")); return Done(true);
                case "paletteAdd":
                {
                    string hex = SettingsStore.Hex(SettingsStore.ParseColor(Str(a, "color")));
                    Service.Mutate(s => { if (!s.Palette.Contains(hex)) { if (s.Palette.Count >= 32) s.Palette.RemoveAt(0); s.Palette.Add(hex); } });
                    Service.NotifyChanged(); return Done(true);
                }
                case "paletteRemove": Service.Mutate(s => s.Palette.Remove(Str(a, "color"))); Service.NotifyChanged(); return Done(true);
                case "prefs": SetPreferences(a); return Done(true);
                case "widgetAdd": return Done(app.Widgets.Add(Str(a, "style")));
                case "widgetRemove": app.Widgets.Remove(Str(a, "id")); return Done(true);
                case "widgetRemoveAll": app.Widgets.RemoveAll(); return Done(true);
                case "widgetLock": app.Widgets.SetLocked(Str(a, "id"), Bool(a, "locked")); return Done(true);
                case "widgetsHighlight": app.Widgets.Highlight(); return Done(true);
                case "shortcut": return Later(() => SystemIntegration.CreateToggleShortcut());
                case "export": return Later(Export);
                case "import": return Later(Import);
                case "open": Open(Str(a, "what"), Str(a, "url")); return Done(true);
                case "quit": app.Post(app.Exit); return Done(true);
                case "sensorsWatch": WatchSensors(Bool(a, "on")); return Done(SensorList());
                case "reactionsEnabled":
                    Service.Mutate(s => s.ReactionsEnabled = Bool(a, "on")); app.Reactions.Refresh(); Service.NotifyChanged(); return Done(true);
                case "reactionSave": return Done(SaveRule(a));
                case "reactionDelete":
                    Service.Mutate(s => s.Reactions = s.Reactions.Where(r => r.Id != Str(a, "id")).ToList()); app.Reactions.Refresh(); Service.NotifyChanged(); return Done(true);
                case "sensorsSetup": return AdvancedSetup(false);
                case "sensorsRemove": return AdvancedSetup(true);
                case "sensorsPause":
                    {
                        bool pause = Bool(a, "paused");
                        Service.Mutate(s => s.AdvancedPaused = pause);
                        app.Advanced.SetPaused(pause, pause ? "Pausados por você. Temperatura da CPU volta a vir da estimativa do Windows." : null);
                        Service.NotifyChanged(); return Done(true);
                    }
                case "diagnostics": return Diagnostics();
                case "keyboardTest": return After(Service.TestKeyboardLive());
                case "keyboardTestResult": Service.SetKeyboardTestResult(Str(a, "seen")); app.Reactions.Refresh(); return Done(true);
                case "updateCheck": app.CheckForUpdates(true); return Done(true);
                case "updateInstall": app.InstallUpdate(true); return Done(true);
                default: throw new ArgumentException("Comando desconhecido: " + command);
            }
        }
        // Runs the elevated one-time setup off the UI thread (UAC prompt, driver installer).
        Task<object> AdvancedSetup(bool remove)
        {
            if (remove) app.Advanced.Stop();
            else { try { Service.Mutate(s => s.AdvancedPaused = false); } catch { } app.Advanced.SetPaused(false, null); }
            return Task.Factory.StartNew(() =>
            {
                bool ok = AdvancedSensors.RunSetup(remove);
                app.Advanced.ResetConfiguredCache();
                app.Post(() => Service.NotifyChanged());
                if (!ok && !remove) throw new InvalidOperationException("A ativação foi cancelada ou não terminou. Nada foi alterado.");
                return (object)ok;
            });
        }
        string SaveRule(Dictionary<string, object> a)
        {
            string id = Str(a, "id");
            if (String.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N").Substring(0, 10);
            object alert; a.TryGetValue("alertAbove", out alert);
            var rule = new ReactiveRule
            {
                Id = id, Name = (Str(a, "name") ?? "").Trim(), Sensor = Str(a, "sensor"), Keys = List(a, "keys"),
                Min = Num(a, "min"), Max = Num(a, "max"), Mode = Str(a, "mode") ?? "color",
                ColorLow = Norm(Str(a, "colorLow")), ColorMid = String.IsNullOrEmpty(Str(a, "colorMid")) ? null : Norm(Str(a, "colorMid")), ColorHigh = Norm(Str(a, "colorHigh")),
                Brightness = Int(a, "brightness"), AlertAbove = alert == null ? (double?)null : Num(a, "alertAbove"), Enabled = Bool(a, "enabled")
            };
            if (rule.Name.Length == 0) rule.Name = SensorHub.NameOf(rule.Sensor);
            Service.Mutate(s =>
            {
                int index = s.Reactions.FindIndex(r => r.Id == id);
                if (index >= 0) s.Reactions[index] = rule; else s.Reactions.Add(rule);
            });
            app.Reactions.Refresh(); Service.NotifyChanged();
            return id;
        }
        static string Norm(string hex) { return SettingsStore.Hex(SettingsStore.ParseColor(hex)); }
        static double Num(Dictionary<string, object> a, string key)
        {
            object v; return a.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
        }
        void SetPreferences(Dictionary<string, object> a)
        {
            if (a.ContainsKey("startWithWindows")) SystemIntegration.SetStartWithWindows(Bool(a, "startWithWindows"));
            Service.Mutate(s =>
            {
                if (a.ContainsKey("visualLevel")) s.VisualLevel = Str(a, "visualLevel");
                if (a.ContainsKey("reduceMotion")) s.ReduceMotion = Bool(a, "reduceMotion");
                if (a.ContainsKey("widgetsOnTop")) s.WidgetsOnTop = Bool(a, "widgetsOnTop");
                if (a.ContainsKey("widgetsStill")) s.WidgetsStill = Bool(a, "widgetsStill");
                if (a.ContainsKey("quitOnClose")) s.QuitOnClose = Bool(a, "quitOnClose");
                if (a.ContainsKey("hotkey")) s.HotkeyDisabled = !Bool(a, "hotkey");
                if (a.ContainsKey("alwaysOnTop")) s.AlwaysOnTop = Bool(a, "alwaysOnTop");
                if (a.ContainsKey("liveApply")) s.LiveApply = Bool(a, "liveApply");
                if (a.ContainsKey("autoUpdate")) s.AutoUpdateDisabled = !Bool(a, "autoUpdate");
                if (a.ContainsKey("reactiveRate")) s.ReactiveRate = Math.Max(1, Math.Min(30, Int(a, "reactiveRate")));
                if (a.ContainsKey("keyboardLive")) s.KeyboardCompat = !Bool(a, "keyboardLive");
                if (a.ContainsKey("reactiveSmoothing")) s.ReactiveSmoothing = Math.Max(0, Math.Min(100, Int(a, "reactiveSmoothing")));
            });
            TopMost = Service.Settings.AlwaysOnTop;
            if (a.ContainsKey("hotkey")) app.ApplyHotkeyPreference();
            if (a.ContainsKey("reactiveRate") || a.ContainsKey("reactiveSmoothing")) app.ApplyReactiveRhythm();
            if (a.ContainsKey("keyboardLive")) app.Reactions.Refresh(); // re-send the keyboard through the chosen path
            app.Widgets.Sync(); Service.NotifyChanged();
        }
        string Export()
        {
            using (var dialog = new SaveFileDialog { Filter = "Configuração Lume RGB (*.json)|*.json", FileName = "Meu-LumeRGB.json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                SettingsStore.Save(dialog.FileName, Service.CopySettings()); return dialog.FileName;
            }
        }
        string Import()
        {
            using (var dialog = new OpenFileDialog { Filter = "Configuração Lume RGB (*.json)|*.json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                var imported = SettingsStore.Load(dialog.FileName);
                if (MessageBox.Show(this, "Substituir suas configurações locais pelas importadas? A iluminação não será alterada.", "Importar", MessageBoxButtons.YesNo) != DialogResult.Yes) return null;
                Service.ReplaceSettings(imported); app.Widgets.Sync(); app.ApplyHotkeyPreference(); app.ApplyReactiveRhythm(); TopMost = Service.Settings.AlwaysOnTop;
                return dialog.FileName;
            }
        }
        // "Copiar diagnóstico": one text with everything needed to understand a problem on this PC.
        Task<object> Diagnostics()
        {
            return Task.Factory.StartNew(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("Lume RGB " + Program.Version + " — diagnóstico " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(Environment.OSVersion + "  .NET " + Environment.Version + (Environment.Is64BitProcess ? " x64" : " x86"));
                sb.AppendLine(); sb.AppendLine("== Dispositivos ==");
                try { var t = Service.Diagnostics(); sb.AppendLine(t.Wait(8000) ? t.Result : "(o serviço não respondeu em 8 s)"); } catch (Exception e) { sb.AppendLine(e.Message); }
                sb.AppendLine(); sb.AppendLine("== Sensores ==");
                foreach (var r in app.Sensors.Snapshot())
                    sb.AppendLine("  " + r.Id + ": " + (r.Available ? r.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + r.Unit + " (" + r.Source + ")" : "indisponível — " + r.Reason));
                sb.AppendLine(); sb.AppendLine("== Sensores avançados ==");
                try { sb.AppendLine(app.Advanced.Diagnostics()); } catch (Exception e) { sb.AppendLine(e.Message); }
                sb.AppendLine(); sb.AppendLine("== Registro (fim de erro.log) ==");
                try
                {
                    if (File.Exists(AppLog.Path)) { var lines = File.ReadAllLines(AppLog.Path); foreach (var l in lines.Skip(Math.Max(0, lines.Length - 120))) sb.AppendLine(l); }
                    else sb.AppendLine("(vazio)");
                }
                catch (Exception e) { sb.AppendLine(e.Message); }
                string text = sb.ToString();
                string file = Path.Combine(Path.GetDirectoryName(AppLog.Path), "diagnostico.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, text, new UTF8Encoding(true));
                app.Post(() => { try { Clipboard.SetText(text); } catch { } try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { } });
                return (object)file;
            });
        }
        void Open(string what, string url)
        {
            string target = what == "settings" ? Path.GetDirectoryName(Service.SettingsPath) :
                what == "source" ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "source") :
                what == "app" ? AppDomain.CurrentDomain.BaseDirectory : null;
            if (what == "url") { OpenExternal(url); return; }
            if (what == "log") { if (!File.Exists(AppLog.Path)) throw new InvalidOperationException("Nenhum erro registrado ainda."); Process.Start("notepad.exe", "\"" + AppLog.Path + "\""); return; }
            if (target == null) throw new ArgumentException("Destino desconhecido.");
            Directory.CreateDirectory(target); Process.Start(target);
        }

        // ---------- state for the page ----------
        Dictionary<string, object> BuildState()
        {
            var snap = Service.Snapshot(); var settings = Service.Settings;
            var zones = snap.Zones.Select(z => new Dictionary<string, object> {
                { "key", z.Key }, { "name", z.Name ?? z.Key }, { "kind", z.Key == "X68HE" ? "keyboard" : z.Key.StartsWith("JRAINBOW") ? "argb" : "rgb" },
                { "color", SettingsStore.Hex(z.CurrentColor) }, { "brightness", z.CurrentBrightnessPercent }, { "speed", z.CurrentSpeedPercent },
                { "effect", z.Effect }, { "effects", Service.EffectsFor(z.Key) }, { "leds", z.LedCount },
                { "steps", z.Key == "X68HE" ? 4 : 10 }, { "port", z.Key == "X68HE" ? "USB" : z.Key } }).ToList();
            var profiles = settings.Profiles.OrderBy(p => p.Name).Select(p => new Dictionary<string, object> {
                { "name", p.Name }, { "compatible", p.BoardSerial == snap.SetupIdentity },
                { "zones", p.Zones.Select(z => new Dictionary<string, object> { { "key", z.Key }, { "color", z.ColorHex }, { "brightness", z.Brightness }, { "effect", z.Effect } }).ToList() } }).ToList();
            var scenes = LumeService.Scenes.Select(s => new Dictionary<string, object> {
                { "id", s.Id }, { "name", s.Name }, { "color", s.Color }, { "glow", s.Glow }, { "effect", s.Effect }, { "brightness", s.Brightness }, { "speed", s.Speed } }).ToList();
            var widgets = settings.Widgets.Select(w => new Dictionary<string, object> { { "id", w.Id }, { "style", w.Style }, { "locked", w.Locked } }).ToList();
            var devices = snap.Devices.GroupBy(h => h.VendorId + ":" + h.ProductId + ":" + h.Serial + ":" + h.ProductName).Select(g => g.First()).Select(h =>
            {
                bool board = h.VendorId == 0x0DB0 && h.ProductId == 0x0076 && !String.IsNullOrEmpty(h.Serial) && h.Serial.StartsWith("7E30");
                bool keys = h.VendorId == 0x3151 && h.ProductId == 0x502D;
                return new Dictionary<string, object> {
                    { "name", String.IsNullOrWhiteSpace(h.ProductName) ? "Dispositivo HID não identificado" : h.ProductName.Trim() },
                    { "maker", String.IsNullOrWhiteSpace(h.Manufacturer) ? "Não informado" : h.Manufacturer.Trim() },
                    { "id", h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4") },
                    { "support", board ? "board" : keys ? "keyboard" : "unknown" } };
            }).OrderBy(d => (string)d["support"] == "unknown" ? 1 : 0).ThenBy(d => (string)d["name"]).ToList();
            return new Dictionary<string, object> {
                { "version", Program.Version }, { "rev", snap.Version },
                { "busy", snap.Busy }, { "busyText", snap.BusyText }, { "status", snap.Status }, { "statusError", snap.StatusError },
                { "connected", snap.Connected }, { "scanned", Service.ScannedOnce }, { "deviceCount", snap.DeviceCount }, { "power", snap.Power }, { "powerPending", snap.PowerPending },
                { "invalidSettings", snap.InvalidSettings }, { "notes", snap.Notes },
                { "zones", zones }, { "profiles", profiles }, { "scenes", scenes }, { "palette", settings.Palette.ToList() }, { "widgets", widgets }, { "devices", devices },
                { "hotkey", "Ctrl + Alt + L" }, { "hotkeyActive", app.HotkeyRegistered },
                { "reactions", new Dictionary<string, object> { { "enabled", settings.ReactionsEnabled }, { "active", app.Reactions.Active },
                    { "rules", settings.Reactions.Select(r => new Dictionary<string, object> {
                        { "id", r.Id }, { "name", r.Name }, { "sensor", r.Sensor }, { "keys", r.Keys.ToList() }, { "min", r.Min }, { "max", r.Max }, { "mode", r.Mode },
                        { "colorLow", r.ColorLow }, { "colorMid", r.ColorMid }, { "colorHigh", r.ColorHigh }, { "brightness", r.Brightness },
                        { "alertAbove", r.AlertAbove }, { "enabled", r.Enabled } }).ToList() } } },
                { "update", UpdateState() },
                { "advanced", new Dictionary<string, object> { { "state", app.Advanced.State }, { "detail", app.Advanced.Detail },
                    { "configured", app.Advanced.Configured }, { "driver", app.Advanced.DriverReady }, { "paused", app.Advanced.Paused } } },
                { "prefs", new Dictionary<string, object> {
                    { "visualLevel", settings.VisualLevel ?? "max" }, { "reduceMotion", settings.ReduceMotion }, { "widgetsOnTop", settings.WidgetsOnTop },
                    { "widgetsStill", settings.WidgetsStill }, { "quitOnClose", settings.QuitOnClose }, { "hotkey", !settings.HotkeyDisabled },
                    { "alwaysOnTop", settings.AlwaysOnTop }, { "liveApply", settings.LiveApply }, { "autoUpdate", !settings.AutoUpdateDisabled }, { "reactiveRate", settings.RateOrDefault }, { "reactiveSmoothing", settings.SmoothingOrDefault }, { "keyboardLive", !settings.KeyboardCompat }, { "startWithWindows", SafeStartup() } } },
            };
        }
        Dictionary<string, object> UpdateState()
        {
            var u = app.Updater;
            return new Dictionary<string, object> {
                { "state", u.State }, { "installed", Updater.Short(u.Installed) }, { "installedDate", u.InstalledDate }, { "remote", Updater.Short(u.Remote) },
                { "remoteDate", u.RemoteDate }, { "behind", u.Behind }, { "changes", u.Changes.ToList() }, { "error", u.Error },
                { "lastCheck", u.LastCheck.HasValue ? u.LastCheck.Value.ToString("HH:mm") : null },
                { "repo", Updater.Owner + "/" + Updater.Repository } };
        }
        static bool SafeStartup() { try { return SystemIntegration.StartsWithWindows; } catch { return false; } }

        static string Str(Dictionary<string, object> a, string key) { object v; return a.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null; }
        static bool Bool(Dictionary<string, object> a, string key) { object v; return a.TryGetValue(key, out v) && v is bool && (bool)v; }
        static int Int(Dictionary<string, object> a, string key)
        {
            object v; if (!a.TryGetValue(key, out v) || v == null) return 0;
            return (int)Math.Round(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        static List<string> List(Dictionary<string, object> a, string key)
        {
            object v; if (!a.TryGetValue(key, out v) || !(v is IEnumerable) || v is string) return new List<string>();
            return ((IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x)).ToList();
        }
    }
}
