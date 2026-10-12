// "Reações" page: live PC sensors and the rules that link them to lighting zones.
const SENSOR_ICONS = { cpu_load: 'cpu', ram_load: 'memory', gpu_load: 'gpu', gpu_temp: 'thermo', cpu_temp: 'thermo', gpu_power: 'bolt', cpu_power: 'bolt', total_power: 'bolt' };
const SENSOR_NAMES = { cpu_load: 'Uso da CPU', ram_load: 'Uso da memória RAM', gpu_load: 'Uso da GPU', gpu_temp: 'Temperatura da GPU', gpu_power: 'Consumo da GPU', cpu_temp: 'Temperatura da CPU', cpu_power: 'Consumo da CPU', total_power: 'Consumo CPU + GPU' };
const SENSOR_UNITS = { cpu_load: '%', ram_load: '%', gpu_load: '%', gpu_temp: '°C', gpu_power: 'W', cpu_temp: '°C', cpu_power: 'W', total_power: 'W' };
const RANGES = { cpu_load: [0, 100], ram_load: [0, 100], gpu_load: [0, 100], gpu_temp: [35, 85], gpu_power: [20, 250], cpu_temp: [35, 90], cpu_power: [15, 140], total_power: [50, 400] };
export const sensorName = (id) => SENSOR_NAMES[id] || id;
const short = { cpu_load: 'CPU', ram_load: 'RAM', gpu_load: 'GPU', gpu_temp: 'GPU °C', gpu_power: 'GPU W', cpu_temp: 'CPU °C', cpu_power: 'CPU W', total_power: 'Watts' };
export const sensorShort = (id) => short[id] || id;

const PRESETS = [
  { title: 'CPU no teclado', sub: 'verde → amarelo → vermelho', rule: { sensor: 'cpu_load', zones: ['X68HE'], colorLow: '#00E676', colorMid: '#FFD600', colorHigh: '#FF1744', alertAbove: 90 } },
  { title: 'GPU quente nas ventoinhas', sub: 'azul → roxo → vermelho', rule: { sensor: 'gpu_temp', zones: ['JRAINBOW1', 'JRAINBOW2'], colorLow: '#00B0FF', colorMid: '#7C4DFF', colorHigh: '#FF1744', alertAbove: 80 } },
  { title: 'Watts no gabinete', sub: 'turquesa → magenta', rule: { sensor: 'gpu_power', zones: ['JRGB1', 'JRGB2'], colorLow: '#1DE9B6', colorMid: null, colorHigh: '#FF00E5' } },
  { title: 'RAM no brilho', sub: 'a fita acende conforme a memória', rule: { sensor: 'ram_load', zones: ['JRAINBOW2'], mode: 'brightness', colorLow: '#000000', colorMid: null, colorHigh: '#7C6CFF' } },
];

export function createReactions(h) {
  const { $, $$, esc, icon, call, toast } = h;
  let S = null, sensors = {}, watching = false, lastSig = '';
  const timers = {};

  const parse = (c) => { const v = parseInt(c.slice(1), 16); return [(v >> 16) & 255, (v >> 8) & 255, v & 255]; };
  const hex = (rgb) => '#' + rgb.map((x) => Math.round(x).toString(16).padStart(2, '0')).join('').toUpperCase();
  const mix = (a, b, t) => { const x = parse(a), y = parse(b); return hex(x.map((v, i) => v + (y[i] - v) * t)); };
  function evaluate(rule, value) {
    const t = Math.max(0, Math.min(1, (value - rule.min) / (rule.max - rule.min)));
    if (rule.mode === 'brightness') return { color: rule.colorHigh, brightness: Math.max(5, Math.round(100 * t)), t };
    const color = rule.colorMid ? (t < 0.5 ? mix(rule.colorLow, rule.colorMid, t * 2) : mix(rule.colorMid, rule.colorHigh, (t - 0.5) * 2)) : mix(rule.colorLow, rule.colorHigh, t);
    return { color, brightness: rule.brightness || 100, t };
  }
  const heat = (t) => (t < 0.5 ? mix('#2BE38B', '#FFD24A', t * 2) : mix('#FFD24A', '#FF4D6D', (t - 0.5) * 2));

  // ---------- sensors ----------
  function renderSensors() {
    const grid = $('#sensorGrid'); if (!grid) return;
    const ids = Object.keys(SENSOR_NAMES);
    if (!grid.children.length) {
      grid.innerHTML = ids.map((id, i) => `<article class="glass sensor" data-sensor="${id}" style="--i:${i}">
        <div class="sensor-top">${icon(SENSOR_ICONS[id])}<span>${SENSOR_NAMES[id]}</span></div>
        <div class="sensor-value"><b>—</b><small>${SENSOR_UNITS[id]}</small></div>
        <div class="sensor-bar"><i></i></div><div class="sensor-src"></div></article>`).join('');
    }
    for (const id of ids) {
      const el = grid.querySelector(`[data-sensor="${id}"]`), r = sensors[id];
      const ok = r && r.available;
      el.classList.toggle('na', !ok);
      const [lo, hi] = RANGES[id], t = ok ? Math.max(0, Math.min(1, (r.value - lo) / (hi - lo))) : 0;
      el.style.setProperty('--heat', heat(t)); el.style.setProperty('--t', t);
      el.querySelector('.sensor-value b').textContent = ok ? (r.value >= 100 || SENSOR_UNITS[id] === '%' ? Math.round(r.value) : r.value.toFixed(1)) : '—';
      el.querySelector('.sensor-src').textContent = !r ? 'Aguardando…' : ok ? 'Fonte: ' + r.source : r.reason || 'Indisponível';
      el.title = ok ? '' : (r && r.reason) || '';
    }
  }
  function updateLive() {
    for (const card of $$('#ruleList [data-rule]')) {
      const rule = S && S.reactions.rules.find((r) => r.id === card.dataset.rule); if (!rule) continue;
      const r = sensors[rule.sensor], live = card.querySelector('.rule-live'), marker = card.querySelector('.gbar .marker');
      if (!r || !r.available) { live.innerHTML = `<i></i><span>${r ? esc(r.reason || 'Sensor indisponível') : 'Abra esta página para ler os sensores'}</span>`; live.classList.add('na'); marker.hidden = true; continue; }
      const out = evaluate(rule, r.value);
      live.classList.remove('na'); marker.hidden = false;
      live.innerHTML = `<i style="--c:${out.color};opacity:${0.3 + 0.7 * out.brightness / 100}"></i><span><b>${Math.round(r.value * 10) / 10} ${SENSOR_UNITS[rule.sensor]}</b> → ${out.color}${rule.mode === 'brightness' ? ' · ' + out.brightness + '%' : ''}${rule.alertAbove != null && r.value >= rule.alertAbove ? ' · alerta' : ''}</span>`;
      marker.style.left = out.t * 100 + '%'; marker.style.setProperty('--c', out.color);
    }
  }

  // ---------- rules ----------
  function ruleCard(rule, i, owners) {
    const zones = S.zones.length ? S.zones : [{ key: 'X68HE', name: 'Teclado' }];
    const unit = SENSOR_UNITS[rule.sensor];
    const grad = rule.mode === 'brightness' ? `linear-gradient(90deg, #000, ${rule.colorHigh})` : rule.colorMid ? `linear-gradient(90deg, ${rule.colorLow}, ${rule.colorMid}, ${rule.colorHigh})` : `linear-gradient(90deg, ${rule.colorLow}, ${rule.colorHigh})`;
    const shared = rule.keys.filter((k) => owners[k] && owners[k] !== rule.id);
    return `<article class="glass rule-card ${rule.enabled ? '' : 'disabled'}" data-rule="${rule.id}" style="--i:${i}">
      <header class="rule-head">
        <input class="rule-name" data-f="name" value="${esc(rule.name)}" maxlength="60" aria-label="Nome da reação">
        <div class="rule-live na"><i></i><span></span></div>
        <label class="toggle-line" title="Ativar esta reação"><input type="checkbox" data-f="enabled" ${rule.enabled ? 'checked' : ''}><span class="switch"></span></label>
        <button class="icon-btn small danger" data-ract="delete" title="Excluir">${icon('trash')}</button>
      </header>
      <div class="rule-grid">
        <div class="field"><label>Quando</label>
          <select data-f="sensor">${Object.keys(SENSOR_NAMES).map((id) => `<option value="${id}" ${id === rule.sensor ? 'selected' : ''}>${SENSOR_NAMES[id]} (${SENSOR_UNITS[id]})</option>`).join('')}</select></div>
        <div class="field range-field"><label>Vai de</label>
          <input type="number" data-f="min" value="${rule.min}" step="any"><span>até</span><input type="number" data-f="max" value="${rule.max}" step="any"><em>${unit}</em></div>
        <div class="field"><label>Muda a</label>
          <div class="segmented mini" data-f="mode"><button data-v="color" class="${rule.mode !== 'brightness' ? 'on' : ''}">Cor</button><button data-v="brightness" class="${rule.mode === 'brightness' ? 'on' : ''}">Intensidade</button></div></div>
      </div>
      <div class="field"><label>Nas zonas</label>
        <div class="zone-chips">${zones.map((z) => `<button class="zchip ${rule.keys.includes(z.key) ? 'on' : ''}" data-zone-key="${z.key}">${esc(z.name)}</button>`).join('')}</div>
        ${shared.length ? `<p class="hint warn">Também usada por outra reação: ${shared.map((k) => esc((S.zones.find((z) => z.key === k) || { name: k }).name)).join(', ')}. Vale a regra mais abaixo.</p>` : ''}</div>
      <div class="gradient-row">
        ${rule.mode === 'brightness' ? '' : `<label class="cpick" title="Cor no mínimo"><input type="color" data-f="colorLow" value="${rule.colorLow.toLowerCase()}"><i style="--c:${rule.colorLow}"></i><small>mín</small></label>`}
        <div class="gbar" style="--g:${grad}"><i class="marker" hidden></i><span>${rule.min} ${unit}</span><span>${rule.max} ${unit}</span></div>
        ${rule.mode === 'brightness' ? '' : rule.colorMid ? `<label class="cpick" title="Cor do meio"><input type="color" data-f="colorMid" value="${rule.colorMid.toLowerCase()}"><i style="--c:${rule.colorMid}"></i><small>meio <a data-ract="nomid">×</a></small></label>` : `<button class="cpick add" data-ract="mid" title="Adicionar cor do meio">${icon('plus')}<small>meio</small></button>`}
        <label class="cpick" title="${rule.mode === 'brightness' ? 'Cor' : 'Cor no máximo'}"><input type="color" data-f="colorHigh" value="${rule.colorHigh.toLowerCase()}"><i style="--c:${rule.colorHigh}"></i><small>${rule.mode === 'brightness' ? 'cor' : 'máx'}</small></label>
      </div>
      <div class="rule-extra">
        <label class="check-line"><input type="checkbox" data-f="alertOn" ${rule.alertAbove != null ? 'checked' : ''}><span>Pulsar (respiração) acima de</span></label>
        <input type="number" class="tiny" data-f="alertAbove" value="${rule.alertAbove != null ? rule.alertAbove : Math.round(rule.min + (rule.max - rule.min) * 0.9)}" step="any" ${rule.alertAbove != null ? '' : 'disabled'}><em>${unit}</em>
        <span class="spacer"></span>
        <label>Brilho máx.</label><input type="range" class="range compact" data-f="brightness" min="10" max="100" step="5" value="${rule.brightness || 100}" style="--p:${rule.brightness || 100}%"><output>${rule.brightness || 100}%</output>
      </div>
    </article>`;
  }
  function renderRules(force) {
    const list = $('#ruleList'); if (!list) return;
    const active = document.activeElement;
    if (!force && active && list.contains(active) && /INPUT|SELECT/.test(active.tagName) && active.type !== 'checkbox') return; // user is typing
    const sig = JSON.stringify([S.reactions.rules, S.zones.map((z) => z.key + z.name)]);
    if (!force && sig === lastSig) return;
    lastSig = sig;
    const owners = {};
    S.reactions.rules.filter((r) => r.enabled).forEach((r) => r.keys.forEach((k) => (owners[k] = r.id)));
    list.innerHTML = S.reactions.rules.length ? S.reactions.rules.map((r, i) => ruleCard(r, i, owners)).join('')
      : `<div class="empty"><b>Nenhuma reação ainda</b>Escolha um modelo acima ou crie a sua do zero.</div>`;
    updateLive();
  }
  function readCard(card) {
    const rule = { ...S.reactions.rules.find((r) => r.id === card.dataset.rule) };
    const f = (name) => card.querySelector(`[data-f="${name}"]`);
    rule.name = f('name').value.trim() || sensorName(f('sensor').value);
    rule.enabled = f('enabled').checked;
    rule.sensor = f('sensor').value;
    rule.min = Number(f('min').value); rule.max = Number(f('max').value);
    rule.mode = card.querySelector('[data-f="mode"] .on').dataset.v;
    if (f('colorLow')) rule.colorLow = f('colorLow').value.toUpperCase();
    rule.colorMid = f('colorMid') ? f('colorMid').value.toUpperCase() : rule.mode === 'brightness' ? rule.colorMid : null;
    rule.colorHigh = f('colorHigh').value.toUpperCase();
    rule.keys = [...card.querySelectorAll('.zchip.on')].map((b) => b.dataset.zoneKey);
    rule.alertAbove = f('alertOn').checked ? Number(f('alertAbove').value) : null;
    rule.brightness = Number(f('brightness').value);
    return rule;
  }
  async function save(rule, quiet) {
    if (!(rule.max > rule.min)) { toast('O valor máximo precisa ser maior que o mínimo.', true); return; }
    const index = S.reactions.rules.findIndex((r) => r.id === rule.id);
    if (index >= 0) S.reactions.rules[index] = rule; // optimistic: live preview follows instantly
    updateLive();
    try { await call('reactionSave', rule, quiet ? null : 'Reação salva.'); } catch { renderRules(true); }
  }
  function saveSoon(card, delay = 450) {
    const id = card.dataset.rule; clearTimeout(timers[id]);
    timers[id] = setTimeout(() => save(readCard(card), true), delay);
  }
  async function addRule(base) {
    const sensor = base.sensor || 'cpu_load', [lo, hi] = RANGES[sensor];
    const keys = (base.zones || []).filter((k) => S.zones.some((z) => z.key === k));
    const rule = { id: '', name: base.name || sensorName(sensor), sensor, keys: keys.length || !S.zones.length ? keys : [S.zones[S.zones.length - 1].key], min: lo, max: hi,
      mode: base.mode || 'color', colorLow: base.colorLow || '#00E676', colorMid: base.colorMid === undefined ? '#FFD600' : base.colorMid, colorHigh: base.colorHigh || '#FF1744',
      brightness: 100, alertAbove: base.alertAbove ?? null, enabled: true };
    try {
      await call('reactionSave', rule, 'Reação criada. Ative as Reações no topo da página se ainda estiverem desligadas.');
      if (!S.reactions.enabled) await call('reactionsEnabled', { on: true });
    } catch { /* toast shown */ }
  }

  function bind() {
    $('#reactionsToggle').addEventListener('change', (e) => call('reactionsEnabled', { on: e.target.checked }, e.target.checked ? 'Reações ativadas.' : 'Reações desligadas. As zonas voltaram às cores anteriores.').catch(() => {}));
    $('#presetRow').addEventListener('click', (e) => {
      const b = e.target.closest('[data-preset]'); if (!b) return;
      if (b.dataset.preset === 'blank') addRule({ name: 'Minha reação', sensor: 'cpu_load', zones: [] });
      else { const p = PRESETS[Number(b.dataset.preset)]; addRule({ ...p.rule, name: p.title }); }
    });
    const list = $('#ruleList');
    list.addEventListener('click', (e) => {
      const card = e.target.closest('[data-rule]'); if (!card) return;
      const act = e.target.closest('[data-ract]');
      if (act && act.dataset.ract === 'delete') {
        if (card.classList.contains('confirm')) call('reactionDelete', { id: card.dataset.rule }, 'Reação excluída.').catch(() => {});
        else { card.classList.add('confirm'); act.title = 'Clique de novo para excluir'; setTimeout(() => card.classList.remove('confirm'), 2500); }
        return;
      }
      if (act && act.dataset.ract === 'mid') { e.preventDefault(); const r = readCard(card); r.colorMid = mix(r.colorLow, r.colorHigh, 0.5); save(r, true).then(() => renderRules(true)); return; }
      if (act && act.dataset.ract === 'nomid') { e.preventDefault(); const r = readCard(card); r.colorMid = null; save(r, true).then(() => renderRules(true)); return; }
      const chip = e.target.closest('.zchip'); if (chip) { chip.classList.toggle('on'); saveSoon(card, 150); return; }
      const seg = e.target.closest('[data-f="mode"] button');
      if (seg) { seg.parentElement.querySelectorAll('button').forEach((b) => b.classList.toggle('on', b === seg)); save(readCard(card), true).then(() => renderRules(true)); }
    });
    list.addEventListener('input', (e) => {
      const card = e.target.closest('[data-rule]'); if (!card) return;
      if (e.target.type === 'color') e.target.nextElementSibling.style.setProperty('--c', e.target.value);
      if (e.target.dataset.f === 'brightness') { e.target.style.setProperty('--p', e.target.value + '%'); e.target.nextElementSibling.textContent = e.target.value + '%'; }
      if (e.target.dataset.f === 'alertOn') card.querySelector('[data-f="alertAbove"]').disabled = !e.target.checked;
      saveSoon(card);
    });
    list.addEventListener('change', (e) => {
      const card = e.target.closest('[data-rule]'); if (!card) return;
      if (e.target.dataset.f === 'sensor') {
        // New sensor: suggest its typical range.
        const [lo, hi] = RANGES[e.target.value]; card.querySelector('[data-f="min"]').value = lo; card.querySelector('[data-f="max"]').value = hi;
        save(readCard(card), true).then(() => renderRules(true)); return;
      }
      if (e.target.dataset.f === 'enabled' || e.target.type === 'number' || e.target.type === 'color') save(readCard(card), true).then(() => { if (e.target.type !== 'color') renderRules(true); });
    });
  }

  return {
    bind,
    render(state) {
      S = state;
      const r = S.reactions, rulesOn = r.rules.filter((x) => x.enabled).length;
      const toggle = $('#reactionsToggle'); toggle.checked = r.enabled;
      $('#reactionsToggleText').textContent = !r.enabled ? 'Reações desligadas' : rulesOn ? `Ativas · ${rulesOn} ${rulesOn === 1 ? 'regra' : 'regras'}` : 'Ativas · nenhuma regra';
      document.body.classList.toggle('reactive', r.enabled && rulesOn > 0);
      const row = $('#presetRow'); if (!row.children.length) row.innerHTML = PRESETS.map((p, i) => `<button class="preset" data-preset="${i}" style="--a:${p.rule.colorLow === '#000000' ? p.rule.colorHigh : p.rule.colorLow};--b:${p.rule.colorMid || p.rule.colorHigh};--c:${p.rule.colorHigh}">
        <span class="preset-g"></span><b>${p.title}</b><small>${sensorShort(p.rule.sensor)} · ${p.sub}</small></button>`).join('')
        + `<button class="preset blank" data-preset="blank">${icon('plus')}<b>Criar do zero</b><small>qualquer sensor, qualquer zona</small></button>`;
      renderRules(false);
    },
    sensors(list) { for (const r of list || []) sensors[r.id] = r; renderSensors(); updateLive(); },
    async visible(on) {
      if (on === watching) return; watching = on;
      try { const list = await call('sensorsWatch', { on }); if (on) this.sensors(list); } catch { /* ignore */ }
    },
    // Zones controlled by an active rule, for badges elsewhere in the app.
    owners() {
      const map = {}; if (!S || !S.reactions.enabled) return map;
      S.reactions.rules.filter((r) => r.enabled).forEach((r) => r.keys.forEach((k) => (map[k] = r.sensor)));
      return map;
    },
  };
}
