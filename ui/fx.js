// Visual effects built with the MIT "shaders" library (github.com/shader-effects-inc/shaders),
// bundled locally in lib/shaders.js. WebGPU only: without it the page keeps its CSS fallback.
let lib = null;
let background = null, hero = null;
let level = 'max', reduceMotion = false;
let current = { a: '#7C6CFF', b: '#35D9C3', c: '#FF5D91', on: true };
let tween = null;
let shown = { a: current.a, b: current.b, c: current.c, t: 1 };
const options = { disableTelemetry: true };

const hex = (n) => n.toString(16).padStart(2, '0');
export function parse(c) { const v = parseInt(c.slice(1), 16); return [(v >> 16) & 255, (v >> 8) & 255, v & 255]; }
export function toHex([r, g, b]) { return '#' + hex(Math.round(r)) + hex(Math.round(g)) + hex(Math.round(b)); }
export function mix(a, b, t) { const x = parse(a), y = parse(b); return toHex(x.map((v, i) => v + (y[i] - v) * t)); }
export function vivid(c) {
  const [r, g, b] = parse(c); const m = Math.max(r, g, b);
  if (m < 10) return '#7C6CFF';
  return toHex([r, g, b].map((v) => (v * 255) / m));
}

function stops(p, t) {
  // t = 1 lit, 0 dark. Dark ends keep the interface readable; colours bloom in the middle.
  const dark = '#05040b', deep = mix('#0b0918', p.a, 0.22 * t + 0.04);
  return [
    { color: dark, position: 0 },
    { color: deep, position: 0.22 },
    { color: mix('#100d22', p.a, 0.58 * t), position: 0.46 },
    { color: mix('#0d0b1c', p.b, 0.48 * t), position: 0.66 },
    { color: mix('#0b0918', p.c, 0.32 * t), position: 0.84 },
    { color: dark, position: 1 },
  ];
}

function backgroundPreset(p) {
  const speed = reduceMotion ? 0 : 1;
  const layers = [
    { type: 'MeshGradient', id: 'mesh', props: { stops: stops(p, p.on ? 1 : 0.08), speed: 0.35 * speed, swirl: 0.45, smoothness: 2.8, drift: 0.6, count: 6 } },
  ];
  if (level === 'max') {
    layers.push({ type: 'Aurora', id: 'aurora', props: { colorA: p.a, colorB: p.b, colorC: p.c, intensity: p.on ? 42 : 0, speed: 2.5 * speed, curtainCount: 3, height: 90, blendMode: 'screen', opacity: 0.55 } });
    layers.push({ type: 'FloatingParticles', id: 'dust', props: { particleColor: '#ffffff', count: 260, particleSize: 0.8, speed: 0.12 * speed, twinkle: 0.9, angle: 80, opacity: 0.35, blendMode: 'screen' } });
  }
  layers.push({ type: 'Vignette', props: { color: '#04030a', radius: 0.62, falloff: 0.75, intensity: 0.85 } });
  layers.push({ type: 'FilmGrain', props: { strength: 0.07 } });
  return { components: layers };
}

function heroPreset(p) {
  const speed = reduceMotion ? 0 : 1;
  return {
    components: [
      { type: 'Neon', id: 'ring', props: {
        shape: JSON.stringify({ type: 'ringSDF', radius: 0.36, thickness: 0.006 }),
        color: p.a, secondaryColor: p.b, glowColor: p.a, secondaryBlend: 0.65,
        intensity: p.on ? 1.9 : 0.25, hotCoreIntensity: p.on ? 0.8 : 0.1, glowIntensity: p.on ? 1 : 0.12, glowRadius: 0.32,
        flowSpeed: 1.4 * speed, flowAmount: 0.55, flickerSpeed: 0, flickerAmount: 0.2 } },
    ],
  };
}

export async function init(bgCanvas, heroCanvas, prefs) {
  level = prefs.visualLevel || 'max'; reduceMotion = !!prefs.reduceMotion;
  try {
    lib = await import('./lib/shaders.js');
    const support = await lib.getWebGPUSupport();
    if (!support || !support.supported) throw new Error('WebGPU indisponível');
  } catch (e) {
    lib = null; document.body.classList.add('no-gpu');
    return false;
  }
  document.body.classList.add('gpu');
  await mountBackground(bgCanvas);
  try { hero = await lib.createShader(heroCanvas, heroPreset(current), { ...options, onError: () => heroCanvas.classList.add('failed') }); }
  catch { heroCanvas.classList.add('failed'); }
  return true;
}

async function mountBackground(canvas) {
  if (background) { background.destroy(); background = null; }
  if (!lib || level === 'light') { document.body.classList.add('fx-light'); return; }
  document.body.classList.remove('fx-light');
  try {
    background = await lib.createShader(canvas, backgroundPreset(current), { ...options, onError: () => document.body.classList.add('no-gpu') });
    requestAnimationFrame(() => canvas.classList.add('ready'));
  } catch { document.body.classList.add('no-gpu'); }
}

export async function setQuality(prefs, bgCanvas) {
  const nextLevel = prefs.visualLevel || 'max', nextMotion = !!prefs.reduceMotion;
  if (nextLevel === level && nextMotion === reduceMotion) return;
  level = nextLevel; reduceMotion = nextMotion;
  if (!lib) return;
  await mountBackground(bgCanvas);
  if (hero) hero.update('ring', { flowSpeed: reduceMotion ? 0 : 1.4 });
}

// Smoothly blends the scene towards the lights' colours (and darkness when switched off).
export function setPalette(colors, on) {
  const unique = [...new Set(colors.map(vivid))];
  const target = { a: unique[0] || '#7C6CFF', b: unique[1] || unique[0] || '#35D9C3', c: unique[2] || unique[1] || unique[0] || '#FF5D91', on };
  if (target.a === current.a && target.b === current.b && target.c === current.c && target.on === current.on) return;
  current = target;
  const from = { ...shown }, toT = on ? 1 : 0.08, start = performance.now(), duration = 1100;
  cancelAnimationFrame(tween);
  const step = (now) => {
    const k = Math.min(1, (now - start) / duration), e = k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2;
    shown = { a: mix(from.a, target.a, e), b: mix(from.b, target.b, e), c: mix(from.c, target.c, e), t: from.t + (toT - from.t) * e };
    const h = Math.max(0, (shown.t - 0.08) / 0.92);
    if (background) {
      background.update('mesh', { stops: stops(shown, shown.t) });
      if (level === 'max') background.update('aurora', { colorA: shown.a, colorB: shown.b, colorC: shown.c, intensity: 42 * h });
    }
    if (hero) hero.update('ring', { color: shown.a, glowColor: shown.a, secondaryColor: shown.b, intensity: 0.25 + 1.65 * h, hotCoreIntensity: 0.1 + 0.7 * h, glowIntensity: 0.12 + 0.88 * h });
    if (k < 1) tween = requestAnimationFrame(step);
  };
  tween = requestAnimationFrame(step);
}

// Brief neon flicker used as the "switching" animation on the power ring.
export function flicker(on) {
  if (!hero) return;
  hero.update('ring', { flickerSpeed: 6, flickerAmount: on ? 0.35 : 0.8 });
  setTimeout(() => hero && hero.update('ring', { flickerSpeed: 0, flickerAmount: 0.2 }), on ? 700 : 900);
}
export function heroVisible(visible) { if (hero) visible ? hero.resume() : hero.pause(); }
