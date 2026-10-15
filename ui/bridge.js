// Bridge between the page and LumeRGB.exe (WebView2 postMessage).
// Opened in a normal browser, it switches to a demo backend so the interface can be previewed.
const host = window.chrome && window.chrome.webview;
const listeners = new Set();
const sensorListeners = new Set();
let seq = 0;
const pending = new Map();

function emit(state) { for (const fn of listeners) fn(state); }
function emitSensors(list, extra) { for (const fn of sensorListeners) fn(list, extra); }

function hostBridge() {
  host.addEventListener('message', (event) => {
    const msg = event.data;
    if (!msg || typeof msg !== 'object') return;
    if (msg.type === 'state') emit(msg.state);
    else if (msg.type === 'sensors') emitSensors(msg.sensors, { engine: msg.engine, advanced: msg.advanced });
    else if (msg.type === 'reply' && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.ok ? resolve(msg.result) : reject(new Error(msg.error || 'Falha desconhecida'));
    }
  });
  return {
    demo: false,
    call(cmd, args = {}) {
      const id = ++seq;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        host.postMessage(JSON.stringify({ id, cmd, args }));
      });
    },
  };
}

export const bridge = host ? hostBridge() : (await import('./demo.js')).createDemoBridge(emit, emitSensors);
export function onState(fn) { listeners.add(fn); return () => listeners.delete(fn); }
export function onSensors(fn) { sensorListeners.add(fn); return () => sensorListeners.delete(fn); }
