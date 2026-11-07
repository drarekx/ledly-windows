// Ledly for Windows — main window (WebView2). Same sections as the Mac app's MainView.

import { S, api, patch, post, onRefresh, startLive, clearTest } from "./lib/state.js";
import { t, loadTranslations, setLanguage, formatNumber } from "./lib/i18n.js";
import {
  h, bind, dyn, text, button, card, segmented, toggle, slider, colorInput, pane, banner, sectionLabel, autoCanvas,
  Scope, withScope, dialogs, onCleanup,
} from "./lib/ui.js";
import { icon } from "./lib/icons.js";
import { zones, drawStrip } from "./lib/layout.js";
import { EffectRenderer, EFFECT_ICONS, USES_COLOR, hexToRGB } from "./lib/effects.js";
import {
  deviceTitle, deviceColor, deviceKind, count, fps, effectTitle, MODE_TITLES, liveStrip, percent, device,
} from "./lib/common.js";
import { layoutPane, devicePane, imagePane, apiPane, settingsPane } from "./lib/panes.js";

const SECTIONS = [
  ["home", "Inicio", "home"],
  ["effects", "Efectos", "sparkles"],
  ["layout", "Disposición", "layout"],
  ["device", "Dispositivo", "cpu"],
  ["image", "Imagen", "image"],
  ["api", "API", "network"],
  ["settings", "Ajustes", "sliders"],
];

let section = sessionStorage.getItem("section") || "home";
let rootScope = null;
let languageKey = "";

function openSection(id) {
  if (id === section) return;
  section = id;
  try { sessionStorage.setItem("section", id); } catch { /* ignore */ }
  render();
}

// MARK: - Shell

function render() {
  rootScope?.dispose();
  // Leaving a pane ends the test patterns it started (like onDisappear in the Mac app).
  clearTest();
  rootScope = new Scope();
  const app = document.getElementById("app");
  const content = withScope(rootScope, () => [sidebar(), h("main", null, currentPane()), offlineNotice()]);
  app.replaceChildren(...content);
  document.querySelector("main").scrollTop = 0;
}

function refreshAll() {
  if (!S.settings) return;
  const key = `${S.settings.language}`;
  if (key !== languageKey) {
    languageKey = key;
    setLanguage(S.settings.language);
    render();
    return;
  }
  rootScope?.run();
  for (const d of dialogs) d.run();
}

function currentPane() {
  switch (section) {
    case "effects": return effectsPane();
    case "layout": return layoutPane();
    case "device": return devicePane();
    case "image": return imagePane();
    case "api": return apiPane();
    case "settings": return settingsPane();
    default: return homePane();
  }
}

function offlineNotice() {
  const el = h("div", { class: "offline", role: "status" }, h("div", { class: "spinner", style: "border-color: var(--line-strong); border-top-color: var(--text2)" }),
    t("No hay conexión con Ledly. Reintentando…"));
  bind(() => { el.hidden = S.online; });
  return el;
}

function sidebar() {
  const nav = h("nav", { class: "nav" }, SECTIONS.map(([id, title, ic]) =>
    h("button", { type: "button", class: id === section ? "selected" : "", onclick: () => openSection(id) }, icon(ic), t(title))));

  const dot = h("span", { class: "dot" });
  bind(() => { dot.style.background = deviceColor(); });
  const power = h("div");
  power.append(dyn(() => S.settings.isOn, () =>
    button(S.settings.isOn ? t("Apagar luces") : t("Encender luces"), () => patch({ isOn: !S.settings.isOn }, 0),
      S.settings.isOn ? "secondary" : "primary", { icon: S.settings.isOn ? "power" : "bulb", full: true, height: 30 })));

  return h("aside", { class: "sidebar" },
    h("div", { class: "brand" }, h("div", { class: "logo" }, icon("rainbow", 15)), "Ledly"),
    nav,
    h("div", { class: "spacer" }),
    h("div", { class: "status-box" },
      h("div", { class: "row", style: "gap:8px" }, dot, h("span", { class: "medium" }, text(deviceTitle))),
      h("div", { class: "small" }, text(statusDetail)),
      power),
    h("div", { class: "small version" }, text(() => t("Versión {0}", S.status?.version ?? "…"))));
}

function statusDetail() {
  const s = S.settings;
  if (!s.isOn) return t("Luces apagadas");
  if (S.status?.live) return `${t("Tiempo real (DDP)")} · ${fps()} fps`;
  const what = s.mode === "screen" ? t("Siguiendo la pantalla") : s.mode === "effect" ? effectTitle(s.effect) : s.colorHex;
  return `${what} · ${t("{0} LEDs", count())} · ${fps()} fps`;
}

// MARK: - Home

const PRESET_COLORS = ["#FFFFFF", "#FFB46B", "#FF8A3D", "#FF2D2D", "#FF2D95", "#9B5CFF", "#3D7BFF", "#2DD4FF", "#2DFF8A", "#E5FF2D"];

function homePane() {
  const banners = dyn(() => {
    const st = S.status;
    return [st?.live ? st.liveSource ?? "" : null, st?.overlay ?? false, deviceKind(),
      deviceKind() === "failed" ? device().error : null, (st?.ports ?? []).length === 0,
      st?.capture?.status === "failed" && S.settings.mode === "screen" ? st.capture.message : null];
  }, () => {
    const st = S.status;
    const list = [];
    if (st?.live) {
      list.push(banner("radio", t("Tiempo real: la tira muestra los fotogramas que llegan de {0}.", st.liveSource ?? "DDP"),
        [t("Soltar"), () => post("/api/app/live/end")], "info"));
    }
    if (st?.overlay) {
      list.push(banner("bell", t("La tira muestra un aviso temporal."), [t("Quitar"), () => post("/api/app/overlay/clear")], "info"));
    }
    if (deviceKind() === "disconnected" && (st?.ports ?? []).length === 0) {
      list.push(banner("cable", t("No se ve ningún Arduino. Conéctalo por USB."), null));
    }
    if (deviceKind() === "failed") {
      list.push(banner("alert", device().error || t("Error en la tira"), [t("Ver dispositivo"), () => openSection("device")]));
    }
    if (st?.capture?.status === "failed" && S.settings.mode === "screen") {
      list.push(banner("alert", st.capture.message || t("Error"), [t("Ajustar imagen…"), () => openSection("image")]));
    }
    return list;
  });

  const modeCard = card(
    h("div", { class: "row" },
      segmented(Object.entries(MODE_TITLES).map(([id, k]) => [t(k), id]), () => S.settings.mode,
        (mode) => patch({ mode, isOn: true }, 0)),
      h("div", { class: "spacer" }),
      h("label", { class: "row", style: "gap:8px" }, t("Encendido"), toggle(() => S.settings.isOn, (isOn) => patch({ isOn }, 0), t("Encendido")))),
    slider({ title: t("Brillo"), get: () => S.settings.brightness, set: (brightness) => patch({ brightness }), min: 0, max: 1, step: 0.01, format: percent }),
    dyn(() => S.settings.mode, () => {
      const s = S.settings;
      if (s.mode === "color") {
        const presets = h("div", { class: "presets" }, PRESET_COLORS.map((hex) => {
          const b = h("button", { type: "button", title: hex, style: { background: hex }, onclick: () => patch({ colorHex: hex }, 0) });
          bind(() => b.classList.toggle("selected", S.settings.colorHex.toUpperCase() === hex));
          return b;
        }));
        return [
          h("div", { class: "row" }, h("span", null, t("Color")), h("div", { class: "spacer" }),
            colorInput(() => S.settings.colorHex, (colorHex) => patch({ colorHex }), t("Color")),
            h("span", { class: "mono muted" }, text(() => S.settings.colorHex))),
          presets,
        ];
      }
      if (s.mode === "effect") {
        return h("div", { class: "row" },
          dyn(() => S.settings.effect, () => [h("span", { style: "color:var(--accent-text);display:flex" }, icon(EFFECT_ICONS[S.settings.effect] ?? "sparkles")),
            h("span", { class: "medium" }, effectTitle(S.settings.effect))]),
          h("div", { class: "spacer" }),
          button(t("Elegir efecto…"), () => openSection("effects")));
      }
      const stat = (title, value) => h("div", { class: "stat" }, h("span", { class: "t" }, title), h("span", { class: "v" }, text(value)));
      return h("div", { class: "row", style: "gap:16px" },
        stat(t("Captura"), captureText),
        stat(t("Suavizado"), () => `${Math.round(S.settings.smoothingMs)} ms`),
        stat(t("Bordes negros"), () => (S.settings.blackBorders ? t("Detectar") : t("No"))),
        h("div", { class: "spacer" }),
        button(t("Ajustar imagen…"), () => openSection("image")));
    }));

  const info = (title, value, ic, target) => h("button", { type: "button", class: "info-card", onclick: () => openSection(target) },
    icon(ic), h("div", { class: "v" }, text(value)), h("div", { class: "t" }, title));

  return pane(t("Inicio"), t("La tira sigue los colores del borde de la pantalla, un efecto o un color fijo."),
    banners,
    h("div", { style: "padding:6px 0" }, liveStrip()),
    modeCard,
    h("div", { class: "info-cards" },
      info(t("LEDs"), () => String(count()), "layout", "layout"),
      info(t("Dispositivo"), deviceTitle, "cpu", "device"),
      info(t("Refresco"), () => `${fps()} fps`, "speed", "image"),
      info(t("API"), apiText, "network", "api")));
}

function captureText() {
  switch (S.status?.capture?.status) {
    case "running": return t("{0} fps", S.settings.captureFps);
    case "starting": return t("Iniciando…");
    case "noPermission": return t("Sin permiso");
    case "failed": return t("Error");
    default: return t("Parada");
  }
}

function apiText() {
  const a = S.status?.api;
  if (a?.status === "running") return `:${a.port}`;
  if (a?.status === "failed") return t("Error");
  return t("Desactivada");
}

// MARK: - Effects

/** A small ring of LEDs to preview effects (12 + 5 + 12 + 5). */
const PREVIEW_ZONES = zones({ top: 12, right: 5, bottom: 12, left: 5, start: "bottomLeft", clockwise: true, offset: 0, bottomGap: 0, depth: 0.1, overlap: 0 });

function effectsPane() {
  const cards = [];
  let hovered = null;
  let frame = 0;
  let lastFrame = 0;
  const color = () => hexToRGB(S.settings.colorHex);

  // Only the hovered and the active effect animate (~30 fps); the rest show a still frame.
  const tick = (now) => {
    frame = requestAnimationFrame(tick);
    if (now - lastFrame < 33) return;
    lastFrame = now;
    for (const c of cards) if (c.id === hovered || (S.settings.mode === "effect" && S.settings.effect === c.id)) c.step(now / 1000);
  };
  frame = requestAnimationFrame(tick);
  onCleanup(() => cancelAnimationFrame(frame));

  const grid = h("div", { class: "effect-grid" }, (S.meta?.effects ?? []).map((effect) => {
    const renderer = new EffectRenderer(effect.id, PREVIEW_ZONES);
    let colors = [];
    const canvas = autoCanvas((ctx, w, hgt) => drawStrip(ctx, w, hgt, PREVIEW_ZONES, colors), "");
    const step = (time) => {
      colors = renderer.render(time, S.settings.effectSpeed, color(), S.settings.equalizer);
      canvas.redraw();
    };
    // Still frame: run the effect for a moment so stateful ones (fire, sparks) show something.
    for (let k = 0; k <= 30; k++) colors = renderer.render(3 + k / 30, 1, color(), S.settings.equalizer);
    cards.push({ id: effect.id, step });
    const dot = USES_COLOR.has(effect.id) ? h("span", { class: "color-dot" }) : null;
    if (dot) bind(() => { dot.style.background = S.settings.colorHex; });
    const el = h("button", {
      type: "button", class: "effect-card",
      onclick: () => patch({ effect: effect.id, mode: "effect", isOn: true }, 0),
      onmouseenter: () => { hovered = effect.id; },
      onmouseleave: () => { if (hovered === effect.id) hovered = null; },
    }, canvas, h("div", { class: "name" }, icon(EFFECT_ICONS[effect.id] ?? "sparkles"), h("span", null, t(effect.titleKey)), dot));
    bind(() => el.classList.toggle("selected", S.settings.mode === "effect" && S.settings.effect === effect.id));
    return el;
  }));

  return pane(t("Efectos"), t("Animaciones que no dependen de la pantalla."),
    card(
      slider({ title: t("Velocidad"), get: () => S.settings.effectSpeed, set: (effectSpeed) => patch({ effectSpeed }), min: 0.1, max: 3, step: 0.1,
        format: (v) => `${formatNumber(v, 1)}×` }),
      h("div", { class: "row" }, t("Color del efecto"), h("div", { class: "spacer" }),
        colorInput(() => S.settings.colorHex, (colorHex) => patch({ colorHex }), t("Color del efecto"))),
      h("div", { class: "small" }, t("Solo lo usan los efectos marcados con ●."))),
    dyn(() => S.settings.mode === "effect" && S.settings.effect === "musicSpectrum", () =>
      S.settings.mode === "effect" && S.settings.effect === "musicSpectrum" ? equalizerCard() : null),
    grid);
}

const EQ_TITLES = {
  fromBottom: "Desde abajo", fromTop: "Desde arriba", stereo: "Estéreo", ring: "Anillo", quad: "Cuatro espejos",
  spectrum: "Espectro", bars: "Barras", level: "Nivel", flashes: "Destellos",
  rainbow: "Arcoíris", fixed: "Color fijo", heat: "Calor", traffic: "Semáforo", cycle: "Ciclo",
};

function equalizerCard() {
  const eq = () => S.settings.equalizer;
  const meta = S.meta?.equalizer ?? { layouts: [], styles: [], colors: [], presets: [] };
  const row = (title, control) => h("div", { class: "eq-row" }, h("span", { class: "title" }, title), control);
  const seg = (list, key) => segmented(list.map((id) => [t(EQ_TITLES[id] ?? id), id]), () => eq()[key], (v) => patch({ equalizer: { [key]: v } }, 0));
  return card(
    h("div", { class: "card-title" }, icon("chart"), t("Ecualizador")),
    h("div", { class: "col", style: "gap:6px" },
      sectionLabel(t("Combinaciones rápidas")),
      h("div", { class: "eq-presets" }, meta.presets.map((p) => dyn(
        () => eq().layout === p.layout && eq().style === p.style && eq().colors === p.colors,
        () => {
          const active = eq().layout === p.layout && eq().style === p.style && eq().colors === p.colors;
          return button(t(p.titleKey), () => patch({ equalizer: { layout: p.layout, style: p.style, colors: p.colors } }, 0),
            active ? "primary" : "secondary", { full: true, height: 28 });
        })))),
    row(t("Disposición"), seg(meta.layouts, "layout")),
    row(t("Estilo"), seg(meta.styles, "style")),
    row(t("Colores"), seg(meta.colors, "colors")),
    dyn(() => eq().colors === "fixed", () => eq().colors === "fixed"
      ? h("div", { class: "row" }, t("Color"), h("div", { class: "spacer" }), colorInput(() => S.settings.colorHex, (colorHex) => patch({ colorHex }), t("Color")))
      : null),
    slider({ title: t("Sensibilidad"), get: () => eq().sensitivity, set: (sensitivity) => patch({ equalizer: { sensitivity } }), min: 0.5, max: 2.5, step: 0.1,
      format: (v) => `${formatNumber(v, 1)}×` }),
    h("div", { class: "small" }, t("«Estéreo» pone el canal izquierdo en el lado izquierdo de la pantalla y el derecho en el derecho.")));
}

// MARK: - Start

async function start() {
  await loadTranslations();
  onRefresh(refreshAll);
  for (;;) {
    try {
      const [meta, settings, status] = await Promise.all([
        api("GET", "/api/app/meta"), api("GET", "/api/app/settings"), api("GET", "/api/app/status"),
      ]);
      Object.assign(S, { meta, settings, status, online: true });
      break;
    } catch (e) {
      console.warn("load", e);
      setLanguage("system");
      document.getElementById("app").replaceChildren(h("div", { class: "row", style: "margin:auto;gap:10px" },
        h("div", { class: "spinner", style: "border-color: var(--line-strong); border-top-color: var(--text2)" }),
        t("No hay conexión con Ledly. Reintentando…")));
      await new Promise((r) => setTimeout(r, 1500));
    }
  }
  languageKey = S.settings.language;
  setLanguage(S.settings.language);
  render();
  startLive();
  // Ctrl+number jumps between sections.
  document.addEventListener("keydown", (e) => {
    if (e.ctrlKey && !e.altKey && e.key >= "1" && e.key <= "7" && !dialogs.size) {
      e.preventDefault();
      openSection(SECTIONS[Number(e.key) - 1][0]);
    }
  });
  // Links open in the default browser, not inside the window.
  document.addEventListener("click", (e) => {
    const a = e.target.closest?.("a[href]");
    if (a && /^https?:/.test(a.href)) { e.preventDefault(); post("/api/app/open", { url: a.href }); }
  });
}

start();
