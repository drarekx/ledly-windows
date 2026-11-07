// App state and the link with the local API (docs/app-api.md): settings (optimistic PATCH, debounced),
// status over the WebSocket with polling as fallback, live LED colors while a preview is visible.

export const S = {
  settings: null,
  status: null,
  meta: null,
  /** Latest LED colors as [r, g, b] floats 0…1. */
  colors: [],
  /** Test pattern this window set ("direction", "sides", "cursor", "calibration") or null. */
  testPattern: null,
  online: true,
};

let refreshHandler = () => {};
const colorHandlers = new Set();

export function onRefresh(fn) { refreshHandler = fn; }
export function refresh() { refreshHandler(); }

// MARK: - HTTP

export async function api(method, path, body) {
  const r = await fetch(path, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store",
  });
  if (r.status === 204) return null;
  const type = r.headers.get("Content-Type") || "";
  const data = type.includes("json") ? await r.json().catch(() => null) : null;
  if (!r.ok) throw new Error(data?.error || `${r.status}`);
  return data;
}

export const post = (path, body = {}) => api("POST", path, body).catch((e) => { console.warn(path, e); return null; });

/** Opens a web link in the default browser (not inside the window). */
export function openUrl(url) { post("/api/app/open", { url }); }

// MARK: - Settings

const isObject = (v) => v && typeof v === "object" && !Array.isArray(v);

/** Deep merge like the server's Settings.Patch; calibration targets are replaced as a whole. */
export function merge(target, patch) {
  for (const [k, v] of Object.entries(patch)) {
    if (isObject(v) && isObject(target[k]) && k !== "targets") merge(target[k], v);
    else target[k] = isObject(v) ? structuredClone(v) : v;
  }
  return target;
}

let pending = null;
let inFlight = false;
let timer = 0;
let lastEdit = 0;

/** Changes settings at once in the UI and sends them ~100 ms later (sliders send while dragging). */
export function patch(changes, delay = 100) {
  merge(S.settings, changes);
  pending = merge(pending ?? {}, changes);
  lastEdit = performance.now();
  refresh();
  clearTimeout(timer);
  timer = setTimeout(flush, delay);
}

async function flush() {
  if (inFlight || !pending) return;
  const body = pending;
  pending = null;
  inFlight = true;
  try {
    const full = await api("PATCH", "/api/app/settings", body);
    if (full && !pending) {
      S.settings = full;
      refresh();
    }
  } catch (e) {
    console.warn("settings", e);
    reloadSettings(true);
  } finally {
    inFlight = false;
    if (pending) flush();
  }
}

/** Takes the server's settings unless the user is in the middle of changing something. */
export async function reloadSettings(force = false) {
  if (!force && (pending || inFlight || performance.now() - lastEdit < 1500)) return;
  try {
    const full = await api("GET", "/api/app/settings");
    if (full && !pending && !inFlight) {
      if (JSON.stringify(full) !== JSON.stringify(S.settings)) {
        S.settings = full;
        refresh();
      }
    }
  } catch { /* offline: the status poll shows it */ }
}

/** Replaces the settings with a full object from the server (reset-image, …). */
export function adoptSettings(full) {
  if (full) { S.settings = full; refresh(); }
}

// MARK: - Test patterns

let keepAlive = 0;

/** Sets the strip's test pattern (null clears it). Cursor/calibration are re-sent so the server's 30 s watchdog keeps them. */
export function setTest(body) {
  S.testPattern = body?.pattern ?? null;
  clearInterval(keepAlive);
  post("/api/app/test", body ?? { pattern: null });
  if (body && (body.pattern === "cursor" || body.pattern === "calibration")) {
    keepAlive = setInterval(() => post("/api/app/test", body), 10000);
  }
  refresh();
}

export function clearTest(only) {
  if (S.testPattern == null) return;
  if (only && !only.includes(S.testPattern)) return;
  setTest(null);
}

// MARK: - Live updates

let ws = null;
let wsOpen = false;
let lastStatusAt = 0;
let colorViewers = 0;
let reconnectDelay = 1000;
let settingsCheck = 0;

function hexToRGB(hex) {
  const n = parseInt(String(hex).slice(1), 16);
  return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}

function setColors(list) {
  S.colors = list.map(hexToRGB);
  for (const fn of colorHandlers) fn();
}

function setStatus(status) {
  S.status = status;
  lastStatusAt = performance.now();
  if (!S.online) { S.online = true; reloadSettings(true); }
  refresh();
}

function connect() {
  const scheme = location.protocol === "https:" ? "wss" : "ws";
  const token = new URLSearchParams(location.search).get("token");
  try {
    ws = new WebSocket(`${scheme}://${location.host}/api/ws${token ? `?token=${encodeURIComponent(token)}` : ""}`);
  } catch {
    setTimeout(connect, reconnectDelay);
    return;
  }
  ws.onopen = () => {
    wsOpen = true;
    reconnectDelay = 1000;
    ws.send(JSON.stringify({ subscribe: "app" }));
    if (colorViewers > 0) ws.send(JSON.stringify({ subscribe: "colors" }));
  };
  ws.onmessage = (e) => {
    let msg;
    try { msg = JSON.parse(e.data); } catch { return; }
    if (msg.type === "app-status") {
      const { type, ...status } = msg;
      setStatus(status);
    } else if (msg.type === "colors" && Array.isArray(msg.colors)) {
      setColors(msg.colors);
    } else if (msg.type === "app-settings" && msg.settings) {
      if (!pending && !inFlight && performance.now() - lastEdit > 1500) adoptSettings(msg.settings);
    } else if (msg.state) {
      // The Mac API's state broadcast: a setting changed somewhere (tray menu, API, another window).
      clearTimeout(settingsCheck);
      settingsCheck = setTimeout(() => reloadSettings(), 150);
    }
  };
  ws.onclose = () => {
    wsOpen = false;
    ws = null;
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(reconnectDelay * 2, 5000);
  };
  ws.onerror = () => ws?.close();
}

async function pollStatus() {
  if (wsOpen && performance.now() - lastStatusAt < 2500) return;
  try {
    setStatus(await api("GET", "/api/app/status"));
    if (!wsOpen) reloadSettings();
  } catch {
    if (S.online) { S.online = false; refresh(); }
  }
}

let ledPoll = 0;
async function pollColors() {
  if (wsOpen || colorViewers === 0) return;
  try {
    const r = await api("GET", "/api/leds");
    if (Array.isArray(r?.colors)) setColors(r.colors);
  } catch { /* ignore */ }
}

export function startLive() {
  connect();
  setInterval(pollStatus, 1000);
  ledPoll = setInterval(pollColors, 200);
}

/** A view that shows LED colors registers while visible; returns the function that unregisters it. */
export function watchColors(fn) {
  colorHandlers.add(fn);
  colorViewers++;
  if (colorViewers === 1 && wsOpen) ws.send(JSON.stringify({ subscribe: "colors" }));
  return () => {
    colorHandlers.delete(fn);
    colorViewers--;
    if (colorViewers === 0 && wsOpen) ws.send(JSON.stringify({ unsubscribe: "colors" }));
  };
}
