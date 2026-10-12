import { bridge, onState, onSensors } from './bridge.js';
import * as fx from './fx.js';
import { createReactions, sensorShort } from './reactions.js';

const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const icon = (id) => `<svg><use href="#i-${id}"/></svg>`;

const EFFECTS = {
  Static: { name: 'Fixa', icon: 'static', cls: '' },
  Breathing: { name: 'Respiração', icon: 'breath', cls: 'breathing' },
  Flashing: { name: 'Piscar', icon: 'flash', cls: 'flashing' },
  'Double flashing': { name: 'Piscar duplo', icon: 'dflash', cls: 'dflash' },
  Lightning: { name: 'Relâmpago', icon: 'bolt', cls: 'lightning' },
};
const effectName = (e) => (EFFECTS[e] ? EFFECTS[e].name : e === 'Direct' ? 'Controle direto' : e === 'Screen color' ? 'Ao vivo' : e || '—');
const PAGES = {
  home: ['Início', 'Sua iluminação inteira, em um toque.'],
  studio: ['Estúdio', 'Escolha a cor, o brilho e o efeito de cada zona.'],
  scenes: ['Cenas e perfis', 'Atmosferas prontas e suas configurações favoritas.'],
  reactions: ['Reações', 'Luzes que respondem à CPU, GPU, temperatura e consumo.'],
  widgets: ['Widgets', 'Controles na área de trabalho para ligar e desligar tudo.'],
  devices: ['Dispositivos', 'O que o Windows encontrou e o que o Lume controla.'],
  settings: ['Ajustes', 'Visual, comportamento e seus dados.'],
};
const BASE_SWATCHES = ['#7C6CFF', '#35D9C3', '#1FA8FF', '#FF5D91', '#FF6A2B', '#FFD166', '#2BD96B', '#FFFFFF'];

let S = null;
let reactions = null;
const ui = { page: 'home', selected: new Set(), draft: null, dirty: false, booted: false, renaming: null, profileEdit: null, confirmDelete: null, liveTimer: 0, lastScene: null };

// ---------- helpers ----------
function toast(text, error = false) {
  const el = document.createElement('div');
  el.className = 'toast' + (error ? ' error' : '');
  el.innerHTML = `<i></i><span>${esc(text)}</span>`;
  $('#toasts').append(el);
  setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 400); }, error ? 6000 : 3200);
}
async function call(cmd, args, okText) {
  try {
    const result = await bridge.call(cmd, args);
    if (okText) toast(typeof okText === 'function' ? okText(result) : okText);
    return result;
  } catch (e) { toast(e.message, true); throw e; }
}
function countTo(el, value) {
  const from = Number(el.textContent) || 0; if (from === value) return;
  const start = performance.now();
  const step = (now) => { const k = Math.min(1, (now - start) / 600); el.textContent = Math.round(from + (value - from) * (1 - Math.pow(1 - k, 3))); if (k < 1) requestAnimationFrame(step); };
  requestAnimationFrame(step);
}
// Rewrites a container only when its markup really changed: live updates every second must not
// recreate elements under the mouse (a click would be lost) or restart animations.
function setHTML(el, html) { if (el._html === html) return false; el.innerHTML = html; el._html = html; return true; }
const vividColors = () => (S ? S.zones.map((z) => fx.vivid(z.color)) : []);
function hsv2hex(h, s, v) {
  const f = (n) => { const k = (n + h / 60) % 6; return v - v * s * Math.max(0, Math.min(k, 4 - k, 1)); };
  return fx.toHex([f(5) * 255, f(3) * 255, f(1) * 255]);
}
function hex2hsv(hex) {
  const [r, g, b] = fx.parse(hex).map((x) => x / 255); const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = 0;
  if (d) h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return { h: (h * 60 + 360) % 360, s: max ? d / max : 0, v: max };
}
const validHex = (v) => /^#?[0-9a-f]{6}$/i.test(v.trim());
const normHex = (v) => '#' + v.trim().replace('#', '').toUpperCase();

// ---------- navigation ----------
function go(page) {
  if (!PAGES[page]) return;
  const swap = () => {
    ui.page = page;
    // Entrance animation only when arriving on a page, not on every live refresh.
    $$('.page').forEach((p) => { p.classList.toggle('active', p.dataset.page === page); p.classList.toggle('intro', p.dataset.page === page); });
    clearTimeout(ui.introTimer); ui.introTimer = setTimeout(() => $$('.page.intro').forEach((p) => p.classList.remove('intro')), 1200);
    $$('#nav button').forEach((b) => b.classList.toggle('active', b.dataset.page === page));
    $('#pageTitle').textContent = PAGES[page][0]; $('#pageSubtitle').textContent = PAGES[page][1];
    moveIndicator(); fx.heroVisible(page === 'home'); reactions.visible(page === 'reactions');
    if (page === 'studio') drawWheel();
    $('#pages').scrollTop = 0;
  };
  swap();
}
function moveIndicator() {
  const active = $('#nav button.active'); if (!active) return;
  $('#navIndicator').style.transform = `translateY(${active.offsetTop}px)`;
  $('#navIndicator').style.height = active.offsetHeight + 'px';
}

// ---------- render ----------
function render(state) {
  const first = !S; S = state;
  if (first) boot();
  document.body.classList.toggle('busy', S.busy);
  document.body.classList.toggle('pending', S.powerPending);
  const on = S.power === 'on' || S.power === 'mixed';
  document.body.classList.toggle('power-off', S.power === 'off');
  const colors = vividColors();
  const root = document.documentElement.style;
  if (colors.length) { const u = [...new Set(colors)]; root.setProperty('--accent', u[0]); root.setProperty('--accent-2', u[1] || u[0]); root.setProperty('--accent-3', u[2] || u[1] || u[0]); }
  fx.setPalette(colors.length ? colors : ['#7C6CFF', '#35D9C3', '#FF5D91'], S.power !== 'off');
  renderShell(on); renderHome(on); renderStudio(); renderScenes(); renderProfiles(); renderWidgets(); renderDevices(); renderSettings();
  reactions.render(S); renderUpdate();
}
function renderShell(on) {
  const chip = $('#connChip');
  chip.className = 'chip status-chip ' + (S.connected ? 'ok' : S.scanned ? 'warn' : '');
  $('#connText').textContent = S.connected ? `${S.deviceCount} ${S.deviceCount === 1 ? 'dispositivo' : 'dispositivos'} · ${S.zones.length} zonas` : S.scanned ? 'Nenhum controlador' : 'Procurando…';
  $('#rescanBtn').classList.toggle('spinning', S.busy && /Procurando/.test(S.busyText));
  const sw = $('#miniSwitch'); sw.setAttribute('aria-checked', String(on));
  $('#sidePowerTitle').textContent = S.powerPending ? (on ? 'Ligando…' : 'Desligando…') : S.power === 'none' ? 'Sem dispositivos' : on ? 'Tudo ligado' : 'Tudo desligado';
  $('#hotkeyHint').textContent = S.hotkeyActive ? S.hotkey : 'Atalho desativado';
  const bar = $('#statusbar'); bar.classList.toggle('error', !!S.statusError);
  $('#statusText').textContent = S.busy ? S.busyText : S.status;
}
function renderHome(on) {
  const n = S.zones.length;
  $('#powerCoreLabel').textContent = S.powerPending ? (on ? 'LIGANDO' : 'DESLIGANDO') : S.power === 'none' ? 'SEM USB' : on ? 'LIGADO' : 'DESLIGADO';
  $('#heroEyebrow').textContent = S.power === 'mixed' ? 'Parcialmente aceso' : 'Iluminação';
  $('#heroTitle').textContent = S.powerPending ? (on ? 'Ligando…' : 'Desligando…') : S.power === 'none' ? 'Nada conectado' : on ? 'Tudo ligado' : 'Tudo desligado';
  $('#heroSub').textContent = S.power === 'none'
    ? (S.scanned ? 'Nenhuma placa MSI MS-7E30 ou teclado X68HE foi encontrado. Feche outros apps RGB e clique em detectar.' : 'Procurando seus dispositivos…')
    : on ? `${S.zones.filter((z) => z.brightness > 0).length} de ${n} zonas acesas. Um clique no anel apaga tudo — e o Lume guarda as cores para quando você voltar.`
      : 'Suas cores e efeitos ficaram guardados. Clique para acender exatamente como estava.';
  const btn = $('#heroToggle'); btn.querySelector('span').textContent = on ? 'Desligar tudo' : 'Ligar tudo';
  countTo($('#statZones'), n); countTo($('#statDevices'), S.deviceCount); countTo($('#statWidgets'), S.widgets.length);
  const grid = $('#zoneGrid');
  if (!n) setHTML(grid, `<div class="empty" style="grid-column:1/-1"><b>Nenhuma zona disponível</b>Conecte a placa ou o teclado e clique em <a class="link" data-action="rescan">detectar novamente</a>.</div>`);
  const owners = reactions.owners();
  if (n) {
    setHTML(grid, S.zones.map((z, i) => `<article class="glass spotlight zone-card ${z.kind}" data-zone="${z.key}" style="--i:${i}">
      <div class="glow"></div>
      <div class="zone-top"><div class="zone-icon">${icon(z.kind === 'keyboard' ? 'keyboard' : z.kind === 'argb' ? 'fan' : 'strip')}</div>
      <div style="min-width:0"><div class="zone-name">${esc(z.name)}</div><div class="zone-port">${z.kind === 'keyboard' ? 'Teclado · USB' : (z.kind === 'argb' ? 'ARGB' : 'RGB') + ' · ' + z.port}</div></div>${owners[z.key] ? `<span class="react-badge" title="Controlada por uma reação">${icon('pulse')}${sensorShort(owners[z.key])}</span>` : ''}</div>
      <div class="zone-bottom"><span class="zone-state"></span><span class="zone-swatch"></span></div>
      <div class="meter"><i></i></div></article>`).join(''));
    // Live values are patched in place.
    for (const z of S.zones) {
      const card = grid.querySelector(`[data-zone="${z.key}"]`); if (!card) continue;
      const fxc = EFFECTS[z.effect] ? EFFECTS[z.effect].cls : '';
      card.className = `glass spotlight zone-card ${z.kind} ${fxc} ${z.brightness ? '' : 'off'}`;
      card.style.setProperty('--c', fx.vivid(z.color)); card.style.setProperty('--b', z.brightness / 100);
      card.style.setProperty('--period', (z.effect === 'Breathing' ? 4 - z.speed / 40 : 1.6 - z.speed / 100) + 's');
      card.querySelector('.zone-state').textContent = z.brightness ? effectName(z.effect) + ' · ' + z.brightness + '%' : 'Desligada';
      card.querySelector('.meter i').style.width = z.brightness + '%';
    }
  }
  setHTML($('#sceneRow'), S.scenes.slice(0, 6).map((s, i) => sceneCard(s, i)).join(''));
}
function sceneCard(s, i) {
  return `<button class="scene-card ${ui.lastScene === s.id ? 'applied' : ''}" data-scene="${s.id}" style="--i:${i};--c1:${s.color};--c2:${s.glow}">
    <span class="play">${icon('play')}</span><b>${esc(s.name)}</b><small>${esc(effectName(s.effect))} · ${s.brightness}%</small></button>`;
}
function renderScenes() {
  setHTML($('#sceneGrid'), S.scenes.map((s, i) => sceneCard(s, i)).join(''));
}
function renderProfiles() {
  const grid = $('#profileGrid');
  if (ui.profileEdit && document.activeElement && document.activeElement.classList.contains('profile-rename')) return;
  if (!S.profiles.length) { setHTML(grid, `<div class="empty" style="grid-column:1/-1"><b>Nenhum perfil ainda</b>Ajuste suas zonas, dê um nome acima e salve. O perfil guarda cor, brilho e efeito de cada uma.</div>`); return; }
  setHTML(grid, S.profiles.map((p, i) => {
    const editing = ui.profileEdit === p.name, confirming = ui.confirmDelete === p.name;
    return `<article class="glass profile-card spotlight" style="--i:${i}" data-profile="${esc(p.name)}">
      <div class="profile-strip">${p.zones.map((z) => `<i title="${esc(z.key)}" style="background:${z.brightness ? z.color : '#14121f'}"></i>`).join('')}</div>
      <div class="profile-title">${editing ? `<input class="profile-rename" value="${esc(p.name)}" maxlength="80">` : `<span>${esc(p.name)}</span>`}${p.compatible ? '' : '<span class="badge">outro setup</span>'}</div>
      <div class="profile-actions">
        <button class="btn primary" data-pact="apply" ${p.compatible ? '' : 'disabled'}>${icon('play')}<span>Aplicar</span></button>
        <button class="btn ghost" data-pact="${editing ? 'save' : 'rename'}" title="Renomear">${icon(editing ? 'check' : 'edit')}</button>
        <button class="btn ghost danger" data-pact="delete" title="Excluir">${confirming ? '<span>Excluir?</span>' : icon('trash')}</button>
      </div></article>`;
  }).join(''));
  const input = $('.profile-rename'); if (input) { input.focus(); input.select(); }
}
function renderWidgets() {
  const dots = vividColors().map((c) => `<i style="--c:${c}"></i>`).join('');
  $$('.w-dots').forEach((d) => (d.innerHTML = dots));
  const chips = [...S.profiles.slice(0, 5).map((p) => [p.zones[0]?.color || '#7C6CFF', p.zones[p.zones.length - 1]?.color || '#35D9C3']), ...S.scenes.map((s) => [s.color, s.glow])].slice(0, 5);
  $$('.w-chips').forEach((c) => (c.innerHTML = chips.map(([a, b]) => `<i style="--c1:${a};--c2:${b}"></i>`).join('')));
  const status = S.power === 'off' ? 'Desligado' : `Ligado · ${S.zones.length} zonas`;
  $$('.w-pill-text small').forEach((s) => (s.textContent = status));
  $$('.w-orb-disk small').forEach((s) => (s.textContent = S.power === 'off' ? 'DESLIGADO' : 'LIGADO'));
  const names = { orb: 'Orbe', pill: 'Pílula', panel: 'Painel com cenas' };
  const list = $('#activeWidgets');
  setHTML(list, S.widgets.length ? S.widgets.map((w) => `<div class="aw" data-wid="${w.id}"><div class="aw-icon">${icon(w.style === 'orb' ? 'power' : 'widgets')}</div>
    <span>${names[w.style] || w.style}<small>${w.locked ? 'Posição travada' : 'Arraste para mover'}</small></span>
    <button data-wact="lock" title="${w.locked ? 'Destravar' : 'Travar posição'}">${icon(w.locked ? 'lock' : 'unlock')}</button>
    <button data-wact="remove" title="Remover">${icon('trash')}</button></div>`).join('')
    : `<div class="empty">Nenhum widget ainda. Escolha um estilo acima.</div>`);
  $('#removeAllWidgets').hidden = !S.widgets.length;
}
function renderDevices() {
  const q = ($('#deviceSearch').value || '').trim().toLowerCase();
  const labels = { board: 'Controlado · 4 zonas', keyboard: 'Controlado · iluminação global', unknown: 'Controle não confirmado' };
  const items = S.devices.filter((d) => !q || `${d.name} ${d.maker} ${d.id}`.toLowerCase().includes(q));
  setHTML($('#deviceList'), items.length ? items.map((d, i) => `<article class="glass device ${d.support !== 'unknown' ? 'yes' : ''}" style="--i:${Math.min(i, 20)}">
    <div class="dicon">${icon(d.support === 'keyboard' ? 'keyboard' : d.support === 'board' ? 'cpu' : 'usb')}</div>
    <div style="min-width:0"><div class="dname">${esc(d.name)}</div><div class="dsub">${esc(d.maker)} · ${esc(d.id)}</div></div>
    <span class="dtag">${labels[d.support]}</span></article>`).join('')
    : `<div class="empty" style="grid-column:1/-1">${S.scanned ? 'Nenhum dispositivo corresponde à busca.' : 'Procurando…'}</div>`);
  $('#deviceNotes').textContent = (S.notes || []).join('  ');
}
function renderSettings() {
  $$('#visualLevel button').forEach((b) => b.classList.toggle('on', b.dataset.level === S.prefs.visualLevel));
  $$('[data-pref]').forEach((input) => {
    const v = !!S.prefs[input.dataset.pref]; input.checked = input.dataset.invert ? !v : v;
  });
  $('#liveApply').checked = !!S.prefs.liveApply;
  $('#appVersion').textContent = S.version;
  document.body.classList.toggle('reduce-motion', !!S.prefs.reduceMotion);
  $('#gpuInfo').textContent = document.body.classList.contains('no-gpu') ? 'WebGPU não está disponível aqui; usando o fundo animado em CSS.' : document.body.classList.contains('gpu') ? 'WebGPU ativo.' : '';
  fx.setQuality(S.prefs, $('#bg'));
}

// ---------- updates ----------
const UPDATE_TEXT = {
  available: ['Nova versão disponível no GitHub', 'Atualizar agora'],
  downloading: ['Baixando a nova versão do GitHub…', 'Baixando…'],
  building: ['Compilando a nova versão no seu PC…', 'Compilando…'],
  ready: ['Atualização pronta para instalar', 'Reiniciar agora'],
};
function renderUpdate() {
  const u = S.update; if (!u) return;
  const banner = $('#updateBanner'), info = UPDATE_TEXT[u.state];
  const key = u.state + u.remote;
  banner.hidden = !info || ui.updateDismissed === key;
  if (info) {
    $('#ubTitle').textContent = info[0];
    const changes = (u.changes || []).slice(0, 3).join(' · ');
    $('#ubSub').textContent = u.state === 'available' ? `${u.behind > 1 ? u.behind + ' novidades' : 'Novidade'}: ${changes}` : u.state === 'ready' ? 'O Lume reinicia em segundos e volta com tudo como estava.' : changes;
    const action = $('#ubAction'); action.querySelector('span').textContent = info[1];
    action.disabled = u.state === 'downloading' || u.state === 'building';
    banner.classList.toggle('working', action.disabled);
  }
  const labels = { idle: 'aguardando', checking: 'verificando…', uptodate: 'atualizado', available: 'nova versão', downloading: 'baixando…', building: 'compilando…', ready: 'pronta', error: 'erro', installed: 'instalada' };
  const chip = $('#updChip'); chip.textContent = labels[u.state] || u.state; chip.className = 'chip-btn ' + (u.state === 'uptodate' ? 'ok' : u.state === 'error' ? 'danger' : info ? 'on' : '');
  $('#updVersion').textContent = S.version + (u.installed ? ' · ' + u.installed : ' · build local');
  $('#updDate').textContent = u.installedDate ? '(' + new Date(u.installedDate).toLocaleDateString('pt-BR') + ')' : '';
  $('#updStatus').textContent = u.state === 'error' ? u.error : u.state === 'uptodate' ? `Você está na versão mais recente da main${u.lastCheck ? ' · verificado às ' + u.lastCheck : ''}.`
    : info ? `${info[0]} (${u.remote}).` : 'O Lume consulta o GitHub (branch main) sempre que abre.';
  $('#updCheck').disabled = !!info && u.state !== 'available' || u.state === 'checking';
}

// ---------- studio ----------
const wheel = { size: 0, drawn: false };
function zonesSelected() { return S.zones.filter((z) => ui.selected.has(z.key)); }
function loadDraftFrom(zone) {
  const color = zone ? zone.color : '#7C6CFF';
  const hsv = hex2hsv(color);
  ui.draft = { h: hsv.h, s: hsv.s, v: Math.max(hsv.v, 0.05), color, brightness: zone && zone.brightness ? zone.brightness : 100, effect: zone && EFFECTS[zone.effect] ? zone.effect : 'Static', speed: zone ? zone.speed : 50 };
  ui.dirty = false;
}
function renderStudio() {
  if (!S.zones.length) { setHTML($('#targetList'), `<div class="empty">Nenhuma zona conectada.</div>`); $('#targetHint').textContent = ''; return; }
  if (!ui.booted) { S.zones.forEach((z) => ui.selected.add(z.key)); ui.booted = true; }
  for (const key of [...ui.selected]) if (!S.zones.some((z) => z.key === key)) ui.selected.delete(key);
  if (!ui.draft || !ui.dirty) loadDraftFrom(zonesSelected()[0] || S.zones[0]);
  if (ui.renaming && document.activeElement && document.activeElement.classList.contains('rename-input')) { renderEditor(); return; }
  const changed = setHTML($('#targetList'), S.zones.map((z) => {
    const editing = ui.renaming === z.key;
    return `<div class="target ${ui.selected.has(z.key) ? 'on' : ''}" data-target="${z.key}" role="checkbox" aria-checked="${ui.selected.has(z.key)}" tabindex="0">
      <span class="box">${icon('check')}</span>
      ${editing ? `<input class="rename-input" value="${esc(z.name)}" maxlength="80">` : `<span class="tname">${esc(z.name)}<span class="tsub"></span></span>`}
      <button class="rename" data-rename="${z.key}" title="Renomear">${icon(editing ? 'check' : 'edit')}</button>
      <span class="tdot"></span></div>`;
  }).join(''));
  for (const z of S.zones) {
    const row = $(`#targetList [data-target="${z.key}"]`); if (!row) continue;
    row.style.setProperty('--c', fx.vivid(z.color));
    const sub = row.querySelector('.tsub'); if (sub) sub.textContent = `${z.kind === 'keyboard' ? 'Teclado' : z.port} · ${z.brightness ? effectName(z.effect) : 'desligada'}`;
  }
  const r = $('.rename-input'); if (r && changed) { r.focus(); r.select(); }
  const count = ui.selected.size;
  $('#selectAll').textContent = count === S.zones.length ? 'Nenhuma' : 'Todas';
  $('#targetHint').textContent = count ? `${count} de ${S.zones.length} zonas recebem a configuração.` : 'Selecione ao menos uma zona.';
  renderEditor();
}
function availableEffects() {
  const sel = zonesSelected(); if (!sel.length) return Object.keys(EFFECTS);
  return Object.keys(EFFECTS).filter((e) => sel.every((z) => z.effects.includes(e)));
}
function renderEditor() {
  const d = ui.draft; if (!d) return;
  const pick = d.color;
  const root = document.documentElement.style;
  root.setProperty('--pick', pick); root.setProperty('--pick-full', hsv2hex(d.h, d.s, 1));
  const hexInput = $('#hexInput'); if (document.activeElement !== hexInput) hexInput.value = pick.replace('#', '');
  $('.hex-field').classList.remove('invalid');
  placeKnob();
  const vr = $('#valueRange'); vr.value = Math.round(d.v * 100); vr.style.setProperty('--p', vr.value + '%');
  const br = $('#brightRange'); br.value = d.brightness; br.style.setProperty('--p', d.brightness + '%');
  const steps = zonesSelected().some((z) => z.kind === 'keyboard') ? 4 : 10;
  $('#brightOut').textContent = d.brightness === 0 ? 'Desligado' : `${d.brightness}% · nível ${Math.round(d.brightness / (100 / steps))}/${steps}`;
  $('#brightTicks').innerHTML = Array.from({ length: steps + 1 }, () => '<i></i>').join('');
  const effects = availableEffects(); if (!effects.includes(d.effect)) d.effect = 'Static';
  $('#effects').innerHTML = Object.keys(EFFECTS).map((e) => `<button class="effect ${d.effect === e ? 'on' : ''}" data-effect="${e}" ${effects.includes(e) ? '' : 'disabled title="Indisponível para a seleção"'}>${icon(EFFECTS[e].icon)}<span>${EFFECTS[e].name}</span></button>`).join('');
  $('#effectOut').textContent = effects.length < 5 ? 'teclado: fixa ou respiração' : '';
  const sr = $('#speedRange'); sr.value = d.speed; sr.style.setProperty('--p', d.speed + '%'); sr.disabled = d.effect === 'Static';
  $('#speedControl').style.opacity = d.effect === 'Static' ? .45 : 1;
  $('#speedOut').textContent = d.effect === 'Static' ? '—' : d.speed < 25 ? 'Lento' : d.speed < 75 ? 'Médio' : 'Rápido';
  const prev = $('#preview');
  prev.style.setProperty('--pc', pick); prev.style.setProperty('--pb', Math.max(0.04, d.brightness / 100));
  prev.style.setProperty('--period', (d.effect === 'Breathing' ? 4.2 - d.speed / 35 : 1.6 - d.speed / 110) + 's');
  prev.className = 'preview' + (EFFECTS[d.effect].cls ? ' fx-' + EFFECTS[d.effect].cls : '');
  $('#previewKeys').classList.toggle('off', d.brightness === 0);
  $('#previewLabel').textContent = ui.dirty ? 'Prévia · não aplicado' : 'Prévia';
  const all = [...new Set([...BASE_SWATCHES, ...S.palette])];
  $('#swatches').innerHTML = all.map((c) => `<button class="swatch ${c.toUpperCase() === pick.toUpperCase() ? 'sel' : ''}" data-swatch="${c}" style="--c:${c}" title="${c}">${S.palette.includes(c) && !BASE_SWATCHES.includes(c) ? '<span class="x" data-unswatch="' + c + '">×</span>' : ''}</button>`).join('')
    + `<button class="swatch add" id="addSwatch" title="Guardar a cor atual">${icon('plus')}</button>`;
  const apply = $('#applyBtn');
  apply.disabled = !ui.selected.size || !S.connected;
  apply.classList.toggle('dirty', ui.dirty);
  apply.querySelector('span').textContent = ui.selected.size === S.zones.length ? 'Aplicar a todas' : `Aplicar a ${ui.selected.size} ${ui.selected.size === 1 ? 'zona' : 'zonas'}`;
}
function drawWheel() {
  const canvas = $('#wheel'); const size = canvas.width; const ctx = canvas.getContext('2d');
  if (wheel.drawn) return;
  const img = ctx.createImageData(size, size), r = size / 2;
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const dx = x - r, dy = y - r, dist = Math.sqrt(dx * dx + dy * dy), i = (y * size + x) * 4;
    if (dist > r) { img.data[i + 3] = 0; continue; }
    const h = (Math.atan2(dy, dx) * 180 / Math.PI + 360) % 360, s = Math.min(1, dist / (r - 1));
    const [cr, cg, cb] = fx.parse(hsv2hex(h, s, 1));
    img.data[i] = cr; img.data[i + 1] = cg; img.data[i + 2] = cb; img.data[i + 3] = dist > r - 1.5 ? (r - dist) / 1.5 * 255 : 255;
  }
  ctx.putImageData(img, 0, 0); wheel.drawn = true;
}
function placeKnob() {
  const d = ui.draft, wrap = $('.wheel-wrap'), size = wrap.clientWidth || 300, r = size / 2;
  const a = d.h * Math.PI / 180;
  const knob = $('#wheelKnob'); knob.style.left = r + Math.cos(a) * d.s * r + 'px'; knob.style.top = r + Math.sin(a) * d.s * r + 'px';
}
function setDraft(patch, fromColor) {
  Object.assign(ui.draft, patch);
  if (fromColor) ui.draft.color = hsv2hex(ui.draft.h, ui.draft.s, ui.draft.v);
  ui.dirty = true; renderEditor(); scheduleLive();
}
function setDraftColor(hex) { const hsv = hex2hsv(hex); setDraft({ h: hsv.h, s: hsv.s, v: hsv.v, color: normHex(hex) }); }
function scheduleLive() {
  if (!S.prefs.liveApply) return;
  clearTimeout(ui.liveTimer);
  ui.liveTimer = setTimeout(() => { if (!S.busy) applyDraft(true); else scheduleLive(); }, 450);
}
async function applyDraft(quiet) {
  if (!ui.selected.size) { toast('Selecione ao menos uma zona.', true); return; }
  const d = ui.draft;
  const btn = $('#applyBtn'); btn.classList.add('busy');
  try {
    await call('apply', { keys: [...ui.selected], color: d.color, brightness: d.brightness, effect: d.effect, speed: d.speed }, quiet ? null : 'Iluminação aplicada ✓');
    ui.dirty = false; renderEditor();
  } catch { /* toast already shown */ } finally { btn.classList.remove('busy'); }
}
function wheelPointer(e) {
  const rect = $('#wheel').getBoundingClientRect(), r = rect.width / 2;
  const dx = e.clientX - rect.left - r, dy = e.clientY - rect.top - r;
  const s = Math.min(1, Math.sqrt(dx * dx + dy * dy) / r), h = (Math.atan2(dy, dx) * 180 / Math.PI + 360) % 360;
  setDraft({ h, s, v: ui.draft.v < 0.15 ? 1 : ui.draft.v }, true);
}

// ---------- power ----------
async function togglePower() {
  const turningOn = S.power === 'off';
  const burst = $('#burst'); burst.classList.remove('go'); void burst.offsetWidth; burst.classList.add('go');
  fx.flicker(turningOn);
  document.body.classList.toggle('power-off', !turningOn);
  try { await bridge.call('toggle'); } catch (e) { toast(e.message, true); }
}

// ---------- events ----------
function bind() {
  $('#nav').addEventListener('click', (e) => { const b = e.target.closest('button[data-page]'); if (b) go(b.dataset.page); });
  document.addEventListener('click', (e) => {
    const t = e.target;
    const goto = t.closest('[data-goto]'); if (goto) go(goto.dataset.goto);
    const btn = t.closest('.btn'); if (btn) { const r = btn.getBoundingClientRect(), s = document.createElement('span'); s.className = 'ripple'; const size = Math.max(r.width, r.height); s.style.cssText = `width:${size}px;height:${size}px;left:${e.clientX - r.left - size / 2}px;top:${e.clientY - r.top - size / 2}px`; btn.append(s); setTimeout(() => s.remove(), 700); }
    if (t.closest('[data-action="rescan"]')) call('scan');
    const zone = t.closest('[data-zone]');
    if (zone) { ui.selected = new Set([zone.dataset.zone]); ui.dirty = false; ui.draft = null; go('studio'); renderStudio(); }
    const scene = t.closest('[data-scene]');
    if (scene) { ui.lastScene = scene.dataset.scene; const s = S.scenes.find((x) => x.id === scene.dataset.scene); call('scene', { id: scene.dataset.scene }, `Cena ${s ? s.name : ''} aplicada ✓`).catch(() => {}); $$(`[data-scene]`).forEach((c) => c.classList.toggle('applied', c.dataset.scene === ui.lastScene)); }
    const open = t.closest('[data-open]'); if (open) call('open', { what: open.dataset.open }).catch(() => {});
    const add = t.closest('[data-add]');
    if (add) call('widgetAdd', { style: add.dataset.add }, 'Widget adicionado — ele está no canto inferior direito da tela.').catch(() => {});
  });
  for (const id of ['powerCore', 'heroToggle', 'miniSwitch']) $('#' + id).addEventListener('click', togglePower);
  $('#rescanBtn').addEventListener('click', () => call('scan').catch(() => {}));
  $('#rescan2').addEventListener('click', () => call('scan').catch(() => {}));
  document.addEventListener('pointermove', (e) => {
    const card = e.target.closest && e.target.closest('.spotlight'); if (!card) return;
    const r = card.getBoundingClientRect(); card.style.setProperty('--mx', e.clientX - r.left + 'px'); card.style.setProperty('--my', e.clientY - r.top + 'px');
  });

  // studio
  $('#targetList').addEventListener('click', (e) => {
    const rename = e.target.closest('[data-rename]');
    if (rename) { e.stopPropagation(); if (ui.renaming === rename.dataset.rename) commitRename(); else { ui.renaming = rename.dataset.rename; renderStudio(); } return; }
    if (e.target.closest('.rename-input')) return;
    const row = e.target.closest('[data-target]'); if (!row) return;
    const key = row.dataset.target;
    if (e.ctrlKey || ui.selected.size !== 1 || !ui.selected.has(key)) ui.selected.has(key) ? ui.selected.delete(key) : ui.selected.add(key);
    if (!ui.dirty) ui.draft = null;
    renderStudio();
  });
  $('#targetList').addEventListener('keydown', (e) => {
    if (e.target.classList.contains('rename-input')) { if (e.key === 'Enter') commitRename(); if (e.key === 'Escape') { ui.renaming = null; renderStudio(); } return; }
    if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); e.target.click(); }
  });
  $('#selectAll').addEventListener('click', () => { ui.selected = ui.selected.size === S.zones.length ? new Set() : new Set(S.zones.map((z) => z.key)); renderStudio(); });
  const wheelEl = $('#wheel');
  wheelEl.addEventListener('pointerdown', (e) => { wheelEl.setPointerCapture(e.pointerId); wheelPointer(e); });
  wheelEl.addEventListener('pointermove', (e) => { if (e.buttons) wheelPointer(e); });
  $('#valueRange').addEventListener('input', (e) => setDraft({ v: e.target.value / 100 }, true));
  $('#brightRange').addEventListener('input', (e) => setDraft({ brightness: Number(e.target.value) }));
  $('#speedRange').addEventListener('input', (e) => setDraft({ speed: Number(e.target.value) }));
  $('#hexInput').addEventListener('input', (e) => { const v = e.target.value; if (validHex(v)) setDraftColor(normHex(v)); else $('.hex-field').classList.add('invalid'); });
  $('#effects').addEventListener('click', (e) => { const b = e.target.closest('[data-effect]'); if (b && !b.disabled) setDraft({ effect: b.dataset.effect }); });
  $('#swatches').addEventListener('click', (e) => {
    const un = e.target.closest('[data-unswatch]'); if (un) { e.stopPropagation(); call('paletteRemove', { color: un.dataset.unswatch }).catch(() => {}); return; }
    if (e.target.closest('#addSwatch')) { call('paletteAdd', { color: ui.draft.color }, 'Cor guardada na paleta.').catch(() => {}); return; }
    const sw = e.target.closest('[data-swatch]'); if (sw) setDraftColor(sw.dataset.swatch);
  });
  $('#applyBtn').addEventListener('click', () => applyDraft(false));
  $('#revertBtn').addEventListener('click', () => { ui.dirty = false; ui.draft = null; renderStudio(); });
  $('#liveApply').addEventListener('change', (e) => call('prefs', { liveApply: e.target.checked }).catch(() => {}));

  // profiles
  $('#saveProfile').addEventListener('click', saveProfile);
  $('#profileName').addEventListener('keydown', (e) => { if (e.key === 'Enter') saveProfile(); });
  $('#profileGrid').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-pact]'); if (!b) return;
    const name = b.closest('[data-profile]').dataset.profile;
    if (b.dataset.pact === 'apply') call('applyProfile', { name }, `Perfil “${name}” aplicado ✓`).catch(() => {});
    if (b.dataset.pact === 'rename') { ui.profileEdit = name; renderProfiles(); }
    if (b.dataset.pact === 'save') commitProfileRename(name);
    if (b.dataset.pact === 'delete') {
      if (ui.confirmDelete === name) { ui.confirmDelete = null; call('deleteProfile', { name }, 'Perfil excluído.').catch(() => {}); }
      else { ui.confirmDelete = name; renderProfiles(); setTimeout(() => { if (ui.confirmDelete === name) { ui.confirmDelete = null; renderProfiles(); } }, 3000); }
    }
  });
  $('#profileGrid').addEventListener('keydown', (e) => {
    if (!e.target.classList.contains('profile-rename')) return;
    if (e.key === 'Enter') commitProfileRename(ui.profileEdit);
    if (e.key === 'Escape') { ui.profileEdit = null; renderProfiles(); }
  });

  // widgets
  $('#activeWidgets').addEventListener('click', (e) => {
    const b = e.target.closest('[data-wact]'); if (!b) return;
    const id = b.closest('[data-wid]').dataset.wid, w = S.widgets.find((x) => x.id === id);
    if (b.dataset.wact === 'remove') call('widgetRemove', { id }, 'Widget removido.').catch(() => {});
    if (b.dataset.wact === 'lock') call('widgetLock', { id, locked: !w.locked }).catch(() => {});
  });
  $('#removeAllWidgets').addEventListener('click', () => call('widgetRemoveAll', {}, 'Widgets removidos.').catch(() => {}));
  $('#highlightWidgets').addEventListener('click', () => call('widgetsHighlight').catch(() => {}));
  $('#makeShortcut').addEventListener('click', () => call('shortcut', {}, (path) => 'Ícone criado: ' + path).catch(() => {}));
  $$('.w-orb, .w-pill, .w-mini-orb').forEach((el) => el.addEventListener('click', togglePower));

  // prefs
  $$('[data-pref]').forEach((input) => input.addEventListener('change', () => {
    const value = input.dataset.invert ? !input.checked : input.checked;
    call('prefs', { [input.dataset.pref]: value }).catch(() => renderSettings());
  }));
  $('#visualLevel').addEventListener('click', (e) => { const b = e.target.closest('[data-level]'); if (b) call('prefs', { visualLevel: b.dataset.level }).catch(() => {}); });
  $('#deviceSearch').addEventListener('input', renderDevices);
  $('#exportBtn').addEventListener('click', () => call('export', {}, (p) => (p ? 'Configurações exportadas.' : null)).catch(() => {}));
  $('#importBtn').addEventListener('click', () => call('import', {}, (p) => (p ? 'Configurações importadas.' : null)).catch(() => {}));
  $('#quitApp').addEventListener('click', () => call('quit').catch(() => {}));
  $('#ubAction').addEventListener('click', () => call('updateInstall').catch(() => {}));
  $('#ubLater').addEventListener('click', () => { ui.updateDismissed = S.update.state + S.update.remote; renderUpdate(); });
  $('#updCheck').addEventListener('click', () => call('updateCheck').catch(() => {}));
  $('#updRepo').addEventListener('click', () => call('open', { what: 'url', url: 'https://github.com/' + (S.update.repo || 'LksR02/FreeRGB') }).catch(() => {}));
  reactions.bind();
  onSensors((list, extra) => reactions.sensors(list, extra));
  document.addEventListener('visibilitychange', () => { if (!document.hidden && ui.page === 'reactions') reactions.visible(false).then(() => reactions.visible(true)); });

  document.addEventListener('keydown', (e) => {
    const typing = /INPUT|TEXTAREA/.test(document.activeElement.tagName) && document.activeElement.type !== 'range' && document.activeElement.type !== 'checkbox';
    if (e.key === 'F5' || (e.ctrlKey && e.key.toLowerCase() === 'r')) { e.preventDefault(); call('scan').catch(() => {}); return; }
    if (e.ctrlKey && e.key === 'Enter' && ui.page === 'studio') { e.preventDefault(); applyDraft(false); return; }
    if (e.key === 'Escape' && ui.page === 'studio' && ui.dirty && !typing) { ui.dirty = false; ui.draft = null; renderStudio(); return; }
    if (!typing && !e.ctrlKey && !e.altKey && /^[1-7]$/.test(e.key)) go(Object.keys(PAGES)[Number(e.key) - 1]);
  });
  window.addEventListener('resize', () => { moveIndicator(); if (ui.draft) placeKnob(); });
}
async function saveProfile() {
  const input = $('#profileName'), name = input.value.trim();
  if (!name) { toast('Dê um nome ao perfil.', true); input.focus(); return; }
  try { await call('saveProfile', { name }, `Perfil “${name}” salvo ✓`); input.value = ''; } catch { /* shown */ }
}
function commitProfileRename(name) {
  const input = $('.profile-rename'); const next = input ? input.value.trim() : '';
  ui.profileEdit = null;
  if (next && next !== name) call('renameProfile', { name, next }, 'Perfil renomeado.').catch(() => renderProfiles()); else renderProfiles();
}
function commitRename() {
  const input = $('.rename-input'); const key = ui.renaming; ui.renaming = null;
  call('renameZone', { key, name: input ? input.value.trim() : '' }, 'Nome salvo.').catch(() => renderStudio());
}

// ---------- boot ----------
function boot() {
  bind();
  $('#previewKeys').innerHTML = '<i></i>'.repeat(40);
  if (bridge.demo) $('#demoBadge').hidden = false;
  requestAnimationFrame(() => { document.body.classList.remove('booting'); moveIndicator(); });
  fx.init($('#bg'), $('#heroCanvas'), S.prefs).then(() => { fx.setPalette(vividColors(), S.power !== 'off'); renderSettings(); });
}
reactions = createReactions({ $, $$, esc, icon, call, toast });
onState(render);
bridge.call('getState').then((s) => { if (!S || s.rev >= S.rev) render(s); }).catch((e) => toast(e.message, true));
