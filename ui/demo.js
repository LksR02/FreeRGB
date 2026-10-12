// Demo backend used only when the page is opened outside LumeRGB.exe (browser preview).
// It mimics the real state shape and timings; it never touches hardware.
export function createDemoBridge(emit) {
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
    version: '3.0', rev: 1, busy: false, busyText: '', status: 'Modo demonstração: nenhum hardware é alterado.', statusError: false,
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
    prefs: { visualLevel: 'max', reduceMotion: false, widgetsOnTop: false, widgetsStill: false, quitOnClose: false, hotkey: true, alwaysOnTop: false, liveApply: false, startWithWindows: false },
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
    export: () => null, import: () => null, open: () => true, quit: () => true,
  };
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
