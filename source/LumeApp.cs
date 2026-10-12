// SPDX-License-Identifier: GPL-2.0-or-later
// Process entry point: single instance, tray icon, global shortcut, desktop widgets and the
// main window. Everything shares one LumeService, so all of them always show the same state.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumeRgb
{
    static class Program
    {
        public const string Version = "3.1";
        static readonly string[] Commands = { "show", "toggle", "on", "off" };
        public static string EventName(string command) { return "Local\\LumeRGBStandalone." + command; }

        [STAThread] static int Main(string[] args)
        {
            // Any startup failure becomes a readable message + log file instead of a silent exit.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => Crash(e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            try { return Start(args); }
            catch (Exception e) { Crash(e); return 1; }
        }
        public static string LogPath { get { return AppLog.Path; } }
        public static void Log(string text) { AppLog.Write(text); }
        static void Crash(Exception e)
        {
            string text = "Lume RGB " + Version + "  " + Environment.OSVersion + "  .NET " + Environment.Version +
                (Environment.Is64BitProcess ? " x64" : " x86") + "\r\n" + (e == null ? "Erro desconhecido" : e.ToString()) + "\r\n\r\n";
            AppLog.Write(text.TrimEnd());
            try
            {
                MessageBox.Show("O Lume RGB encontrou um erro:\r\n\r\n" + (e == null ? "desconhecido" : e.GetBaseException().Message) +
                    "\r\n\r\nDetalhes salvos em:\r\n" + LogPath, "Lume RGB", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int Start(string[] args)
        {
            string command = args.Contains("--toggle") ? "toggle" : args.Contains("--on") ? "on" : args.Contains("--off") ? "off" : "show";
            bool background = args.Contains("--background");
            // After a self-update the new exe waits for the old process to release the single-instance lock.
            int wait = Array.IndexOf(args, "--wait-pid"), pid;
            if (wait >= 0 && wait + 1 < args.Length && Int32.TryParse(args[wait + 1], out pid))
                try { using (var old = Process.GetProcessById(pid)) old.WaitForExit(20000); } catch (ArgumentException) { }
            bool first;
            using (var mutex = new Mutex(true, "Local\\LumeRGBStandalone", out first))
            {
                if (!first)
                {
                    // Already running: hand the request to the existing instance (window, tray and widgets live there).
                    try { using (var signal = EventWaitHandle.OpenExisting(EventName(command))) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                var events = Commands.Select(c => new EventWaitHandle(false, EventResetMode.AutoReset, EventName(c))).ToArray();
                var context = new LumeContext(command, background, args.Contains("--devtools"), events, Commands, args.Contains("--updated"));
                Application.Run(context);
                foreach (var e in events) e.Dispose();
                return 0;
            }
        }
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    }

    public sealed class LumeContext : ApplicationContext
    {
        public readonly LumeService Service;
        public readonly WidgetManager Widgets;
        public readonly SensorHub Sensors;
        public readonly ReactiveEngine Reactions;
        public readonly Updater Updater;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        readonly SynchronizationContext ui;
        readonly NotifyIcon tray;
        readonly HostWindow host;
        readonly bool devTools;
        readonly System.Windows.Forms.Timer rescan = new System.Windows.Forms.Timer { Interval = 1200 };
        LumeWindow window;
        bool exiting, trayHintShown, changePosted;
        public bool HotkeyRegistered { get; private set; }
        public Icon AppIcon { get; private set; }
        public event EventHandler StateChanged;

        public LumeContext(string command, bool background, bool devTools, EventWaitHandle[] events, string[] names, bool updated)
        {
            this.devTools = devTools;
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            try { AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { AppIcon = SystemIcons.Application; }
            Service = LumeService.CreateDefault();
            Widgets = new WidgetManager(this);
            Sensors = new SensorHub();
            Reactions = new ReactiveEngine(Service, Sensors);
            Updater = Updater.CreateDefault();
            Updater.CleanupOldFiles();
            Updater.Changed += delegate { Post(OnStateChanged); };
            // First check a few seconds after start (every launch), then every 12 hours while running.
            updateTimer.Tick += delegate { updateTimer.Interval = 12 * 60 * 60 * 1000; CheckForUpdates(false); };
            updateTimer.Start();
            host = new HostWindow(this);
            HotkeyRegistered = host.RegisterToggleHotkey(!Service.Settings.HotkeyDisabled);
            tray = new NotifyIcon { Icon = AppIcon, Text = "Lume RGB", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
            Menus.Style(tray.ContextMenuStrip);
            tray.ContextMenuStrip.Opening += (s, e) => { Menus.BuildMain(this, tray.ContextMenuStrip.Items, null); e.Cancel = false; };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            rescan.Tick += delegate { rescan.Stop(); Run(Service.Scan()); };
            Service.Changed += delegate
            {
                // Coalesce bursts of hardware events into one UI refresh.
                lock (rescan) { if (changePosted) return; changePosted = true; }
                ui.Post(_ => { lock (rescan) changePosted = false; OnStateChanged(); }, null);
            };
            Widgets.Sync();
            StartListener(events, names);
            Run(Service.Scan().ContinueWith(t => { if (command != "show") ui.Post(_ => Execute(command), null); }));
            if (!background && command == "show") ShowWindow();
            if (updated) tray.ShowBalloonTip(4000, "Lume RGB atualizado", "Versão " + Updater.Short(Updater.Installed) + " instalada a partir do GitHub.", ToolTipIcon.Info);
        }

        // ---------- updates ----------
        public bool WindowVisible { get { return window != null && !window.IsDisposed && window.Visible && window.WindowState != FormWindowState.Minimized; } }
        public void CheckForUpdates(bool manual)
        {
            if (Updater.Busy || exiting) return;
            Updater.Check().ContinueWith(t => Post(() =>
            {
                if (Updater.State == "available" && (!Service.Settings.AutoUpdateDisabled)) InstallUpdate(false);
            }));
        }
        // Downloads + compiles in the background. Applies right away when nobody is looking at the window.
        public void InstallUpdate(bool restartWhenReady)
        {
            if (Updater.Busy || exiting) return;
            if (Updater.State == "ready") { if (restartWhenReady) RestartIntoUpdate(); return; }
            Updater.Prepare().ContinueWith(t => Post(() =>
            {
                if (Updater.State != "ready") { if (restartWhenReady) Notify(Updater.Error ?? "A atualização falhou.", true); return; }
                if (restartWhenReady || !WindowVisible) RestartIntoUpdate();
                else Notify("Atualização pronta. Clique em Reiniciar no aviso do app.", false);
            }));
        }
        public void RestartIntoUpdate()
        {
            try { Updater.Apply(); }
            catch (Exception e) { Notify("Não foi possível instalar a atualização: " + e.Message, true); return; }
            string args = "--wait-pid " + Process.GetCurrentProcess().Id + " --updated" + (WindowVisible ? "" : " --background");
            try { Process.Start(Application.ExecutablePath, args); }
            catch (Exception e) { AppLog.Write("Reiniciar após atualização: " + e); }
            Exit();
        }
        void OnStateChanged()
        {
            if (exiting) return;
            var snapshot = Service.Snapshot();
            tray.Text = "Lume RGB · " + (snapshot.Power == "off" ? "desligado" : snapshot.Power == "none" ? "sem dispositivos" : "ligado");
            Widgets.Update(snapshot);
            if (StateChanged != null) StateChanged(this, EventArgs.Empty);
        }
        void StartListener(EventWaitHandle[] events, string[] names)
        {
            var thread = new Thread(() =>
            {
                while (!exiting)
                {
                    int index = WaitHandle.WaitAny(events, 1000);
                    if (index >= 0 && index < names.Length) { string command = names[index]; ui.Post(_ => Execute(command), null); }
                }
            }) { IsBackground = true, Name = "Lume commands" };
            thread.Start();
        }
        void Execute(string command)
        {
            if (command == "toggle") Run(Service.TogglePower());
            else if (command == "on") Run(Service.SetPower(true));
            else if (command == "off") Run(Service.SetPower(false));
            else ShowWindow();
        }
        // Background jobs report failures through the service status; widgets/tray add a notification.
        public void Run(Task task)
        {
            task.ContinueWith(t =>
            {
                if (!t.IsFaulted) return;
                var error = t.Exception.InnerException ?? t.Exception;
                ui.Post(_ => Notify(error.Message, true), null);
            });
        }
        public void Run(Func<Task> action)
        {
            try { Run(action()); } catch (Exception e) { Notify(e.Message, true); }
        }
        public void Notify(string text, bool error)
        {
            if (exiting) return;
            bool visible = window != null && window.Visible && window.WindowState != FormWindowState.Minimized;
            if (!visible) tray.ShowBalloonTip(3500, "Lume RGB", text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
            if (error) Service.SetStatus(text, true);
        }
        public void RequestRescan() { rescan.Stop(); rescan.Start(); }
        public void Post(Action action) { ui.Post(_ => action(), null); }

        public void ShowWindow()
        {
            if (exiting) return;
            if (window == null || window.IsDisposed)
            {
                window = new LumeWindow(this, devTools);
                window.FormClosing += (s, e) =>
                {
                    if (exiting || e.CloseReason == CloseReason.WindowsShutDown) return;
                    if (Service.Settings.QuitOnClose) { e.Cancel = true; Post(Exit); return; }
                    e.Cancel = true; window.Hide();
                    if (!trayHintShown) { trayHintShown = true; tray.ShowBalloonTip(3500, "Lume RGB continua ativo", "Os widgets e o atalho Ctrl + Alt + L seguem funcionando. Clique no ícone da bandeja para abrir.", ToolTipIcon.Info); }
                };
            }
            window.Show();
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.Activate(); window.BringToFront();
        }
        public void ApplyHotkeyPreference() { HotkeyRegistered = host.RegisterToggleHotkey(!Service.Settings.HotkeyDisabled); }
        public void Exit()
        {
            if (exiting) return; exiting = true;
            updateTimer.Stop();
            // A downloaded update is installed on the way out, so the next start is already new.
            if (Updater.State == "ready") try { Updater.Apply(); } catch { }
            tray.Visible = false; tray.Dispose();
            Widgets.CloseAll();
            if (window != null && !window.IsDisposed) window.Close();
            host.DestroyHandle();
            Reactions.Dispose(); Sensors.Dispose();
            Service.Dispose();
            ExitThread();
        }

        // Hidden top-level window: receives WM_HOTKEY and the WM_DEVICECHANGE broadcast.
        sealed class HostWindow : NativeWindow
        {
            readonly LumeContext owner; bool registered;
            public HostWindow(LumeContext owner) { this.owner = owner; CreateHandle(new CreateParams { Caption = "Lume RGB host" }); }
            public bool RegisterToggleHotkey(bool enable)
            {
                if (registered) { UnregisterHotKey(Handle, 1); registered = false; }
                if (enable) registered = RegisterHotKey(Handle, 1, 0x0001 | 0x0002 | 0x4000, (uint)Keys.L); // Ctrl + Alt + L, no repeat
                return registered;
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0312) owner.Run(owner.Service.TogglePower());
                else if (m.Msg == 0x0219) owner.RequestRescan();
                base.WndProc(ref m);
            }
            [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
            [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        }
    }

    public sealed class WidgetManager
    {
        readonly LumeContext app;
        readonly Dictionary<string, DesktopWidget> open = new Dictionary<string, DesktopWidget>();
        LumeSnapshot last;
        public WidgetManager(LumeContext app) { this.app = app; }
        LumeService Service { get { return app.Service; } }

        public void Sync()
        {
            var configs = Service.Settings.Widgets.ToList();
            foreach (var id in open.Keys.Where(k => !configs.Any(c => c.Id == k)).ToList()) { open[id].Close(); open[id].Dispose(); open.Remove(id); }
            float scale = DpiScale();
            foreach (var config in configs)
            {
                DesktopWidget widget;
                if (!open.TryGetValue(config.Id, out widget))
                {
                    widget = new DesktopWidget(config.Id, config.Style, Visible(new Point(config.X, config.Y), config.Style, scale), scale);
                    widget.ActionInvoked += OnAction; widget.Moved += OnMoved; widget.MenuRequested += OnMenu;
                    open[config.Id] = widget;
                    ApplyOptions(widget, config);
                    widget.Show();
                }
                else ApplyOptions(widget, config);
            }
            if (last != null) Update(last);
        }
        void ApplyOptions(DesktopWidget widget, WidgetConfig config)
        {
            var settings = Service.Settings;
            widget.Locked = config.Locked; widget.Ambient = !settings.WidgetsStill && !settings.ReduceMotion;
            if (widget.OnTop != settings.WidgetsOnTop) { widget.OnTop = settings.WidgetsOnTop; widget.TopMost = settings.WidgetsOnTop; widget.ApplyLayer(); }
        }
        static float DpiScale()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f);
        }
        // Keeps a saved position on a monitor that still exists.
        static Point Visible(Point p, string style, float scale)
        {
            var size = WidgetRenderer.LogicalSize(style);
            var rect = new Rectangle(p, new Size((int)(size.Width * scale), (int)(size.Height * scale)));
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return p;
            var area = Screen.PrimaryScreen.WorkingArea;
            return new Point(area.Right - rect.Width - 40, area.Bottom - rect.Height - 40);
        }
        public void Update(LumeSnapshot snapshot)
        {
            last = snapshot;
            bool on = snapshot.Power == "on" || snapshot.Power == "mixed";
            var colors = snapshot.Zones.Select(z => z.CurrentColor).ToArray();
            string status = snapshot.PowerPending ? (on ? "Ligando…" : "Desligando…") :
                !snapshot.Connected ? (Service.ScannedOnce ? "Sem dispositivo" : "Procurando…") :
                on ? "Ligado · " + snapshot.Zones.Count + " zonas" : "Desligado";
            var chips = Chips();
            foreach (var widget in open.Values) widget.SetState(on, snapshot.PowerPending, snapshot.Connected, colors, status, chips);
        }
        List<WidgetChip> Chips()
        {
            var chips = new List<WidgetChip>();
            foreach (var p in Service.Settings.Profiles.Take(5))
            {
                var first = p.Zones.FirstOrDefault(); var lastZone = p.Zones.LastOrDefault();
                chips.Add(new WidgetChip { Label = p.Name, Action = "profile:" + p.Name,
                    Color = first == null ? Color.SlateBlue : SettingsStore.ParseColor(first.ColorHex), Glow = lastZone == null ? Color.SlateBlue : SettingsStore.ParseColor(lastZone.ColorHex) });
            }
            foreach (var s in LumeService.Scenes)
            {
                if (chips.Count >= 5) break;
                chips.Add(new WidgetChip { Label = s.Name, Action = "scene:" + s.Id, Color = SettingsStore.ParseColor(s.Color), Glow = SettingsStore.ParseColor(s.Glow) });
            }
            return chips;
        }
        void OnAction(DesktopWidget widget, string action)
        {
            if (action == "power") app.Run(Service.TogglePower());
            else if (action.StartsWith("scene:")) app.Run(() => Service.ApplyScene(action.Substring(6)));
            else if (action.StartsWith("profile:")) app.Run(() => Service.ApplyProfile(action.Substring(8)));
        }
        void OnMoved(DesktopWidget widget)
        {
            try { Service.Mutate(s => { var c = s.Widgets.FirstOrDefault(w => w.Id == widget.Id); if (c != null) { c.X = widget.Left; c.Y = widget.Top; } }); }
            catch (Exception e) { app.Notify(e.Message, true); }
        }
        void OnMenu(DesktopWidget widget, MouseEventArgs e)
        {
            var menu = new ContextMenuStrip(); Menus.Style(menu);
            Menus.BuildMain(app, menu.Items, widget.Id);
            menu.Closed += delegate { app.Post(menu.Dispose); };
            menu.Show(widget, e.Location);
            SetForegroundWindow(menu.Handle); // lets the menu close when clicking elsewhere

        }
        public string Add(string style)
        {
            if (!SettingsStore.WidgetStyles.Contains(style)) throw new ArgumentException("Estilo de widget desconhecido.");
            float scale = DpiScale(); var size = WidgetRenderer.LogicalSize(style); var area = Screen.PrimaryScreen.WorkingArea;
            int offset = Service.Settings.Widgets.Count * 28;
            var config = new WidgetConfig { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Style = style,
                X = area.Right - (int)(size.Width * scale) - 48 - offset, Y = area.Bottom - (int)(size.Height * scale) - 48 - offset };
            Service.Mutate(s => s.Widgets.Add(config));
            Sync();
            DesktopWidget widget; if (open.TryGetValue(config.Id, out widget)) widget.Pulse();
            Service.NotifyChanged();
            return config.Id;
        }
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        public void Remove(string id) { Service.Mutate(s => s.Widgets = s.Widgets.Where(w => w.Id != id).ToList()); Sync(); Service.NotifyChanged(); }
        public void RemoveAll() { Service.Mutate(s => s.Widgets.Clear()); Sync(); Service.NotifyChanged(); }
        public void SetLocked(string id, bool locked)
        {
            Service.Mutate(s => { var c = s.Widgets.FirstOrDefault(w => w.Id == id); if (c != null) c.Locked = locked; }); Sync(); Service.NotifyChanged();
        }
        public void Highlight()
        {
            foreach (var w in open.Values) w.Pulse();
        }
        public void CloseAll() { foreach (var w in open.Values) { w.Close(); w.Dispose(); } open.Clear(); }
    }

    // Tray and widget context menus (same items, dark styling).
    static class Menus
    {
        public static void BuildMain(LumeContext app, ToolStripItemCollection items, string widgetId)
        {
            items.Clear();
            var service = app.Service; var snapshot = service.Snapshot();
            bool on = snapshot.Power != "off";
            items.Add(new ToolStripMenuItem("Lume RGB · " + (snapshot.Connected ? snapshot.Zones.Count + " zonas" : "sem dispositivos")) { Enabled = false });
            items.Add(new ToolStripSeparator());
            items.Add(Item(on ? "Desligar tudo" : "Ligar tudo", () => app.Run(service.SetPower(!on)), "Ctrl+Alt+L"));
            var scenes = new ToolStripMenuItem("Cenas");
            foreach (var s in LumeService.Scenes) { var id = s.Id; var item = Item(s.Name, () => app.Run(() => service.ApplyScene(id)), null); item.Image = Swatch(s.Color, s.Glow); scenes.DropDownItems.Add(item); }
            items.Add(scenes);
            var profiles = new ToolStripMenuItem("Perfis");
            foreach (var p in service.Settings.Profiles.OrderBy(p => p.Name)) { var name = p.Name; profiles.DropDownItems.Add(Item(name, () => app.Run(() => service.ApplyProfile(name)), null)); }
            if (profiles.DropDownItems.Count == 0) profiles.DropDownItems.Add(new ToolStripMenuItem("Nenhum perfil salvo") { Enabled = false });
            items.Add(profiles);
            items.Add(new ToolStripSeparator());
            var widgets = new ToolStripMenuItem("Widgets na área de trabalho");
            widgets.DropDownItems.Add(Item("Adicionar Orbe", () => Safe(app, () => app.Widgets.Add("orb")), null));
            widgets.DropDownItems.Add(Item("Adicionar Pílula", () => Safe(app, () => app.Widgets.Add("pill")), null));
            widgets.DropDownItems.Add(Item("Adicionar Painel com cenas", () => Safe(app, () => app.Widgets.Add("panel")), null));
            widgets.DropDownItems.Add(new ToolStripSeparator());
            var top = Item("Manter widgets por cima das janelas", () => Safe(app, () => { service.Mutate(s => s.WidgetsOnTop = !s.WidgetsOnTop); app.Widgets.Sync(); service.NotifyChanged(); }), null);
            top.Checked = service.Settings.WidgetsOnTop; widgets.DropDownItems.Add(top);
            if (service.Settings.Widgets.Count > 0) widgets.DropDownItems.Add(Item("Remover todos os widgets", () => Safe(app, app.Widgets.RemoveAll), null));
            items.Add(widgets);
            if (widgetId != null)
            {
                var config = service.Settings.Widgets.FirstOrDefault(w => w.Id == widgetId);
                if (config != null)
                {
                    var lockItem = Item("Travar posição deste widget", () => Safe(app, () => app.Widgets.SetLocked(widgetId, !config.Locked)), null);
                    lockItem.Checked = config.Locked; items.Add(lockItem);
                    items.Add(Item("Remover este widget", () => Safe(app, () => app.Widgets.Remove(widgetId)), null));
                }
            }
            var reactive = Item("Reações (CPU, GPU, temperatura…)", () => Safe(app, () => { service.Mutate(s => s.ReactionsEnabled = !s.ReactionsEnabled); app.Reactions.Refresh(); service.NotifyChanged(); }), null);
            reactive.Checked = service.Settings.ReactionsEnabled; reactive.Enabled = service.Settings.Reactions.Count > 0; items.Add(reactive);
            items.Add(new ToolStripSeparator());
            items.Add(Item("Abrir Lume RGB", app.ShowWindow, null));
            if (app.Updater.State == "ready") items.Add(Item("Reiniciar e atualizar", app.RestartIntoUpdate, null));
            else items.Add(Item("Verificar atualizações", () => app.CheckForUpdates(true), null));
            items.Add(Item("Procurar dispositivos", () => app.Run(service.Scan()), null));
            items.Add(Item("Sair", app.Exit, null));
        }
        static void Safe(LumeContext app, Action action) { try { action(); } catch (Exception e) { app.Notify(e.Message, true); } }
        static ToolStripMenuItem Item(string text, Action action, string keys)
        {
            var item = new ToolStripMenuItem(text); item.Click += delegate { action(); };
            if (keys != null) item.ShortcutKeyDisplayString = keys;
            return item;
        }
        static Image Swatch(string a, string b)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 16, 16), SettingsStore.ParseColor(a), SettingsStore.ParseColor(b), 45f))
            { g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.FillEllipse(brush, 1, 1, 14, 14); }
            return bmp;
        }
        public static void Style(ContextMenuStrip menu)
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = true };
            menu.BackColor = Color.FromArgb(22, 20, 36); menu.ForeColor = Color.FromArgb(236, 234, 250);
            menu.Font = new Font("Segoe UI", 9.5f); menu.ShowImageMargin = true;
            menu.ItemAdded += (s, e) => Paint(e.Item);
        }
        static void Paint(ToolStripItem item)
        {
            item.ForeColor = Color.FromArgb(236, 234, 250);
            var drop = item as ToolStripMenuItem;
            if (drop != null)
            {
                drop.DropDown.BackColor = Color.FromArgb(22, 20, 36); drop.DropDown.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
                drop.DropDown.ItemAdded += (s, e) => Paint(e.Item);
            }
        }
        sealed class DarkColors : ProfessionalColorTable
        {
            static readonly Color Back = Color.FromArgb(22, 20, 36), Hot = Color.FromArgb(52, 44, 92), Line = Color.FromArgb(48, 46, 70);
            public override Color ToolStripDropDownBackground { get { return Back; } }
            public override Color ImageMarginGradientBegin { get { return Back; } }
            public override Color ImageMarginGradientMiddle { get { return Back; } }
            public override Color ImageMarginGradientEnd { get { return Back; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Hot; } }
            public override Color MenuItemSelected { get { return Hot; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hot; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hot; } }
            public override Color MenuItemPressedGradientBegin { get { return Hot; } }
            public override Color MenuItemPressedGradientEnd { get { return Hot; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Back; } }
            public override Color CheckBackground { get { return Hot; } }
            public override Color CheckSelectedBackground { get { return Hot; } }
            public override Color CheckPressedBackground { get { return Hot; } }
        }
    }

    // Windows integration used by the Preferences page.
    public static class SystemIntegration
    {
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        public static bool StartsWithWindows
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue("LumeRGB") != null; }
        }
        public static void SetStartWithWindows(bool enable)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) key.SetValue("LumeRGB", "\"" + Application.ExecutablePath + "\" --background");
                else if (key.GetValue("LumeRGB") != null) key.DeleteValue("LumeRGB");
            }
        }
        // A classic desktop shortcut that toggles all lighting (also handy for Stream Deck / macro keys).
        public static string CreateToggleShortcut()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string path = Path.Combine(desktop, "Ligar-Desligar RGB.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("O Windows Script Host não está disponível para criar o atalho.");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type linkType = link.GetType();
                Action<string, object> set = (name, value) => linkType.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, link, new[] { value });
                set("TargetPath", Application.ExecutablePath); set("Arguments", "--toggle");
                set("WorkingDirectory", Path.GetDirectoryName(Application.ExecutablePath));
                set("IconLocation", Application.ExecutablePath + ",0"); set("Description", "Liga ou desliga toda a iluminação RGB (Lume RGB)");
                linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
                Marshal.FinalReleaseComObject(link);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            return path;
        }
    }
}
