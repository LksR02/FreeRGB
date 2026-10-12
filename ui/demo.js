// Demo backend used only when the page is opened outside LumeRGB.exe (browser preview).
// It mimics the real state shape and timings; it never touches hardware.
export function createDemoBridge(emit, emitSensors = () => {}) {
  const BOARD = ['Static', 'Breathing', 'Flashing', 'Double flashing', 'Lightning'];
  const KEYS = ['Static', 'Breathing'];
  const zone = (key, name, kind, color, effect, leds) => ({ key, name, kind, color, brightness: 100, speed: 50, effect,
    effects: kind === 'keyboard' ? KEYS : BOARD, leds, steps: kind === 'keyboard' ? 4 : 10, port: kind === 'keyboard' ? 'USB' : key });
  const scenes = [
    ['aurora', 'Aurora', '#7C6CFF', '#35D9C3', 'Static', 100, 50], ['ocean', 'Oceano', '#1FA8FF', '#00F0D0', 'Breathing', 100, 25],
    ['forest', 'Floresta', '#2BD96B', '#B7FF5C', 'Static', 90, 50], ['sunset', 'Pôr do sol', '#FF6A2B', '#FF2E88', 'Static', 100, 50],
    ['sakura', 'Sakura', '#FF4FA3', '#FFC2E2', 'Breathing', 100, 25], ['focus', 'Foco', '#FFD7A1', '#FFF4E0', 'Static', 60, 50],
    ['night', 'Noite', '#3A2BFF', '#120B4D', 'Breathing', 20, 0], ['storm', 'Tempestade', '#B8C6FF', '#4B6BFF', 'Lightning', 100, 100],
  ].map(([id, name, color, glow, effect, brightness, speed]) => ({ id, name, color, glow, effect, brightness, speed }));
  const state = {
    version: '3.8', rev: 1, busy: false, busyText: '', status: 'Modo demonstração: nenhum hardware é alterado.', statusError: false,
    connected: true, scanned: true, deviceCount: 2, power: 'on', powerPending: false, invalidSettings: false, notes: [],
    zones: [
      zone('JRGB1', 'RGB 1', 'rgb', '#7C6CFF', 'Static', 1), zone('JRGB2', 'RGB 2', 'rgb', '#7C6CFF', 'Static', 1),
      zone('JRAINBOW1', 'Ventoinhas frontais', 'argb', '#35D9C3', 'Breathing', 24), zone('JRAINBOW2', 'Fita do gabinete', 'argb', '#FF5D91', 'Static', 60),
      zone('X68HE', 'Teclado X68HE', 'keyboard', '#FFAA55', 'Static', 0),
    ],
    profiles: [
      { name: 'Jogo', compatible: true, zones: [{ key: 'JRGB1', color: '#FF2E63', brightness: 100, effect: 'Static' }, { key: 'X68HE', color: '#08D9D6', brightness: 100, effect: 'Static' }] },
      { name: 'Trabalho', compatible: true, zones: [{ key: 'JRGB1', color: '#FFD7A1', brightness: 60, effect: 'Static' }, { key: 'X68HE', color: '#FFFFFF', brightness: 50, effect: 'Static' }] },
    ],
    scenes, palette: ['#00E5FF', '#FF3CAC'], widgets: [{ id: 'demo1', style: 'orb', locked: false }],
    devices: [
      { name: 'MYSTIC LIGHT', maker: 'MSI', id: '0DB0:0076', support: 'board' },
      { name: 'X68HE', maker: 'ATTACK SHARK', id: '3151:502D', support: 'keyboard' },
      { name: 'USB Receiver', maker: 'Logitech', id: '046D:C548', support: 'unknown' },
      { name: 'H510-PRO', maker: 'HyperX', id: '0951:16E4', support: 'unknown' },
    ],
    hotkey: 'Ctrl + Alt + L', hotkeyActive: true,
    reactions: { enabled: true, active: true, rules: [
      { id: 'demo-cpu', name: 'CPU no teclado', sensor: 'cpu_load', keys: ['X68HE'], min: 0, max: 100, mode: 'color', colorLow: '#00E676', colorMid: '#FFD600', colorHigh: '#FF1744', brightness: 100, alertAbove: 90, enabled: true },
      { id: 'demo-gpu', name: 'GPU quente nas ventoinhas', sensor: 'gpu_temp', keys: ['JRAINBOW1'], min: 35, max: 85, mode: 'color', colorLow: '#00B0FF', colorMid: '#7C4DFF', colorHigh: '#FF1744', brightness: 100, alertAbove: null, enabled: true },
    ] },
    advanced: { state: 'setup', detail: null, configured: false, driver: false },
    update: { state: 'available', installed: '9627ee9', installedDate: '2026-10-12T16:28:00Z', remote: 'a1b2c3d', remoteDate: '', behind: 3, changes: ['Reações: luzes seguem CPU, GPU e watts', 'Atualização automática pelo GitHub'], error: null, lastCheck: '17:02', repo: 'LksR02/FreeRGB' },
    prefs: { reactiveRate: 10, reactiveSmoothing: 40, keyboardLive: true, keyboardMode: 'classic', autoUpdate: true, visualLevel: 'max', reduceMotion: false, widgetsOnTop: false, widgetsStill: false, quitOnClose: false, hotkey: true, alwaysOnTop: false, liveApply: false, startWithWindows: false },
  };
  let memory = null;
  const clone = (x) => JSON.parse(JSON.stringify(x));
  const push = () => { state.rev++; emit(clone(state)); };
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  const job = async (text, fn, done) => {
    state.busy = true; state.busyText = text; push();
    await wait(650);
    try { fn(); state.status = done; state.statusError = false; }
    catch (e) { state.status = e.message; state.statusError = true; throw e; }
    finally { state.busy = false; push(); }
    return true;
  };
  const recomputePower = () => {
    const lit = state.zones.filter((z) => z.brightness > 0).length;
    state.power = lit === 0 ? 'off' : lit === state.zones.length ? 'on' : 'mixed';
  };
  const setPower = async (on) => {
    state.powerPending = true; state.power = on ? 'on' : 'off'; push();
    return job(on ? 'Ligando tudo…' : 'Desligando tudo…', () => {
      state.powerPending = false;
      if (!on) { memory = clone(state.zones); state.zones.forEach((z) => (z.brightness = 0)); }
      else { state.zones.forEach((z) => { const m = memory && memory.find((x) => x.key === z.key); z.brightness = m && m.brightness > 0 ? m.brightness : 100; }); memory = null; }
      recomputePower();
    }, on ? 'Tudo ligado. Iluminação restaurada.' : 'Tudo desligado. A configuração foi guardada para quando você ligar.');
  };
  const norm = (z, effect) => (z.effects.includes(effect) ? effect : 'Static');
  const commands = {
    getState: () => clone(state),
    scan: () => job('Procurando dispositivos…', () => {}, 'Pronto. 5 zonas de iluminação encontradas.'),
    toggle: () => setPower(state.power === 'off'),
    power: ({ on }) => setPower(on),
    scene: ({ id }) => { const s = scenes.find((x) => x.id === id); return job('Aplicando cena…', () => { state.zones.forEach((z) => Object.assign(z, { color: s.color, effect: norm(z, s.effect), brightness: s.brightness, speed: s.speed })); recomputePower(); }, `Cena ${s.name} aplicada.`); },
    applyAll: (a) => job('Aplicando cor a tudo…', () => { state.zones.forEach((z) => Object.assign(z, { color: a.color, effect: norm(z, a.effect), brightness: a.brightness, speed: a.speed })); recomputePower(); }, 'Cor aplicada a todas as zonas.'),
    apply: (a) => job('Aplicando iluminação…', () => { state.zones.filter((z) => a.keys.includes(z.key)).forEach((z) => Object.assign(z, { color: a.color.toUpperCase(), effect: norm(z, a.effect), brightness: a.brightness, speed: a.speed })); recomputePower(); }, 'Iluminação atualizada e confirmada pelos dispositivos.'),
    saveProfile: ({ name }) => job('Salvando perfil…', () => { state.profiles = state.profiles.filter((p) => p.name.toLowerCase() !== name.toLowerCase()); state.profiles.push({ name, compatible: true, zones: state.zones.map((z) => ({ key: z.key, color: z.color, brightness: z.brightness, effect: z.effect })) }); state.profiles.sort((a, b) => a.name.localeCompare(b.name)); }, `Perfil “${name}” salvo.`),
    applyProfile: ({ name }) => { const p = state.profiles.find((x) => x.name === name); return job('Aplicando perfil…', () => { p.zones.forEach((pz) => { const z = state.zones.find((x) => x.key === pz.key); if (z) Object.assign(z, { color: pz.color, brightness: pz.brightness, effect: pz.effect }); }); recomputePower(); }, `Perfil “${name}” aplicado.`); },
    deleteProfile: ({ name }) => { state.profiles = state.profiles.filter((p) => p.name !== name); push(); return true; },
    renameProfile: ({ name, next }) => { const p = state.profiles.find((x) => x.name === name); if (p) p.name = next; push(); return true; },
    renameZone: ({ key, name }) => { const z = state.zones.find((x) => x.key === key); if (z) z.name = name || z.key; push(); return true; },
    paletteAdd: ({ color }) => { if (!state.palette.includes(color)) state.palette.push(color.toUpperCase()); push(); return true; },
    paletteRemove: ({ color }) => { state.palette = state.palette.filter((c) => c !== color); push(); return true; },
    prefs: (a) => { Object.assign(state.prefs, a); push(); return true; },
    widgetAdd: ({ style }) => { const id = Math.random().toString(16).slice(2, 12); state.widgets.push({ id, style, locked: false }); push(); return id; },
    widgetRemove: ({ id }) => { state.widgets = state.widgets.filter((w) => w.id !== id); push(); return true; },
    widgetRemoveAll: () => { state.widgets = []; push(); return true; },
    widgetLock: ({ id, locked }) => { const w = state.widgets.find((x) => x.id === id); if (w) w.locked = locked; push(); return true; },
    widgetsHighlight: () => true,
    shortcut: () => 'C:\\Users\\voce\\Desktop\\Ligar-Desligar RGB.lnk',
    export: () => null, import: () => null, open: () => true, diagnostics: () => 'diagnostico.txt', quit: () => true,
  };
  // Simulated sensors + a tiny copy of the reactive engine, so the demo shows colours moving.
  const names = { cpu_load: ['Uso da CPU', '%', 0, 100], ram_load: ['Uso da memória RAM', '%', 0, 100], gpu_load: ['Uso da GPU', '%', 0, 100], gpu_temp: ['Temperatura da GPU', '°C', 35, 85],
    gpu_power: ['Consumo da GPU', 'W', 20, 250], cpu_temp: ['Temperatura da CPU', '°C', 35, 90], cpu_power: ['Consumo da CPU', 'W', 15, 140], total_power: ['Consumo CPU + GPU', 'W', 50, 400], board_temp: ['Temperatura da placa-mãe', '°C', 25, 60], cpu_fan: ['Ventoinha da CPU', 'RPM', 500, 2500], cpu_clock: ['Frequência da CPU', 'MHz', 800, 5500], disk_load: ['Uso do disco', '%', 0, 100], net_load: ['Tráfego de rede', 'Mbit/s', 0, 200] };
  let t0 = 0, watching = false;
  const sensors = () => {
    t0 += 1; const w = (a, b, p) => a + (b - a) * (0.5 + 0.5 * Math.sin(t0 / p));
    const v = { cpu_clock: w(3200, 5050, 3), disk_load: w(0, 64, 2), net_load: w(0.4, 96, 5), cpu_load: w(8, 96, 3), ram_load: w(41, 58, 9), gpu_load: w(2, 99, 5), gpu_temp: w(38, 79, 7), gpu_power: w(24, 231, 5) };
    return Object.keys(names).map((id) => {
      const [name, unit, min, max] = names[id];
      const adv = state.advanced.state === 'running';
      if (id === 'cpu_temp') return { id, name, unit, min, max, available: true, value: Math.round(w(41, 83, 4) * 10) / 10, source: adv ? 'LibreHardwareMonitor (embutido)' : 'ACPI (aproximada)' };
      if (['cpu_power', 'total_power', 'board_temp', 'cpu_fan'].includes(id)) {
        if (!adv) return { id, name, unit, min, max, available: false, value: 0, reason: 'Ative os sensores avançados no topo da página Reações (pede permissão de administrador uma única vez).' };
        const val = { cpu_power: w(22, 131, 3), total_power: w(60, 360, 4), board_temp: w(34, 46, 11), cpu_fan: w(700, 1900, 6) }[id];
        return { id, name, unit, min, max, available: true, value: Math.round(val * 10) / 10, source: 'LibreHardwareMonitor (embutido)' };
      }
      return { id, name, unit, min, max, available: true, value: Math.round(v[id] * 10) / 10, source: id.startsWith('gpu') ? 'NVIDIA' : 'Windows' };
    });
  };
  const mixHex = (a, b, t) => '#' + [0, 2, 4].map((i) => Math.round(parseInt(a.substr(1 + i, 2), 16) + (parseInt(b.substr(1 + i, 2), 16) - parseInt(a.substr(1 + i, 2), 16)) * t).toString(16).padStart(2, '0')).join('').toUpperCase();
  let ticks = 0;
  setInterval(() => {
    const list = sensors(); ticks++;
    if (watching) emitSensors(list, { engine: { active: state.reactions.enabled, writesPerSecond: Math.min(state.prefs.reactiveRate, 9.6 + (ticks % 3) / 10) }, advanced: state.advanced });
    if (!state.reactions.enabled || state.power === 'off') return;
    let changed = false;
    for (const r of state.reactions.rules.filter((x) => x.enabled)) {
      const s = list.find((x) => x.id === r.sensor); if (!s || !s.available) continue;
      const t = Math.max(0, Math.min(1, (s.value - r.min) / (r.max - r.min)));
      const color = r.mode === 'brightness' ? r.colorHigh : r.colorMid ? (t < .5 ? mixHex(r.colorLow, r.colorMid, t * 2) : mixHex(r.colorMid, r.colorHigh, (t - .5) * 2)) : mixHex(r.colorLow, r.colorHigh, t);
      for (const z of state.zones.filter((z) => r.keys.includes(z.key))) {
        z.color = color; z.brightness = r.mode === 'brightness' ? Math.max(5, Math.round(100 * t)) : r.brightness || 100;
        z.effect = r.alertAbove != null && s.value >= r.alertAbove ? 'Breathing' : 'Static'; changed = true;
      }
    }
    if (changed) push();
  }, 1000);
  Object.assign(commands, {
    sensorsWatch: ({ on }) => { watching = on; return sensors(); },
    sensorsSetup: async () => { state.advanced = { state: 'starting', detail: null, configured: true, driver: true }; push(); await wait(1200); state.advanced.state = 'running'; push(); return true; },
    keyboardTest: async () => { await wait(1500); return true; },
    keyboardTestResult: (a) => { const m = { lamp: 'lamparray', red: 'screen', both: 'screen', green: 'overlay' }[a.seen]; state.prefs.keyboardLive = !!m; state.prefs.keyboardMode = m || 'classic'; push(); return true; },
    keyboardLampTest: async () => { await wait(1500); return 'shown'; },
    sensorsPause: (a) => { state.advanced = { ...state.advanced, paused: !!a.paused, state: a.paused ? 'paused' : 'running', detail: null }; push(); return true; },
    sensorsRemove: () => { state.advanced = { state: 'setup', detail: null, configured: false, driver: true }; push(); return true; },
    reactionsEnabled: ({ on }) => { state.reactions.enabled = on; push(); return true; },
    reactionSave: (rule) => {
      const r = JSON.parse(JSON.stringify(rule)); if (!r.id) r.id = Math.random().toString(16).slice(2, 12);
      const i = state.reactions.rules.findIndex((x) => x.id === r.id); if (i >= 0) state.reactions.rules[i] = r; else state.reactions.rules.push(r);
      push(); return r.id;
    },
    reactionDelete: ({ id }) => { state.reactions.rules = state.reactions.rules.filter((r) => r.id !== id); push(); return true; },
    updateCheck: async () => { state.update.state = 'checking'; push(); await wait(900); state.update.state = 'available'; state.update.lastCheck = new Date().toTimeString().slice(0, 5); push(); return true; },
    updateInstall: async () => {
      if (state.update.state === 'ready') { state.status = 'Demonstração: o app reiniciaria agora na nova versão.'; push(); return true; }
      state.update.state = 'downloading'; push(); await wait(1500); state.update.state = 'building'; push(); await wait(1800); state.update.state = 'ready'; push(); return true;
    },
  });
  setTimeout(push, 50);
  return {
    demo: true,
    async call(cmd, args = {}) {
      const fn = commands[cmd];
      if (!fn) throw new Error('Comando desconhecido: ' + cmd);
      return fn(args);
    },
  };
}
