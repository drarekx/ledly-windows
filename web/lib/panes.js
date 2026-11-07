// Disposición, Dispositivo, Imagen, API and Ajustes panes (Panes.swift / CaptureViews.swift in the Mac app).

import { S, patch, post, api, refresh, setTest, clearTest, adoptSettings, openUrl, watchColors } from "./state.js";
import { t, formatNumber } from "./i18n.js";
import {
  h, bind, dyn, text, button, iconButton, card, segmented, slider, stepper, select, pickerRow, switchRow, colorInput,
  textField, pane, sectionLabel, autoCanvas, copyText, onCleanup,
} from "./ui.js";
import { icon } from "./icons.js";
import { CORNERS, CORNER_TITLES, ledCount, zones } from "./layout.js";
import { deviceTitle, deviceColor, deviceKind, isConnected, device, liveStrip, percent, count } from "./common.js";
import { layoutWizard, whiteBalanceWizard } from "./wizards.js";

const FIRMWARE_BAUD = 1000000;

// MARK: - Layout

export function layoutPane() {
  const L = () => S.settings.layout;
  const setL = (changes) => patch({ layout: changes });
  const step = (title, key, min, max) => stepper({ title, get: () => L()[key], set: (v) => setL({ [key]: v }), min, max });

  const testButton = (title, pattern) => {
    const b = button(title, () => setTest({ pattern }));
    bind(() => { b.className = `btn ${S.testPattern === pattern ? "primary" : "secondary"}`; });
    return b;
  };
  const endTest = button(t("Terminar prueba"), () => setTest(null), "primary");
  bind(() => { endTest.hidden = S.testPattern == null; });

  const warning = h("div", { class: "small danger-text" }, t("El firmware del Nano admite hasta 400 LEDs."));
  bind(() => { warning.hidden = count() <= 400; });

  return pane(t("Disposición"), t("Cuántos LEDs hay en cada lado y por dónde empieza la tira, mirando la pantalla de frente."),
    liveStrip({ showZones: true, showNumbers: true }),
    h("div", { class: "cards" },
      h("div", { class: "card tight" },
        sectionLabel(t("LEDs por lado")),
        step(t("Arriba"), "top", 0, 200),
        step(t("Derecha"), "right", 0, 200),
        step(t("Abajo"), "bottom", 0, 200),
        step(t("Izquierda"), "left", 0, 200),
        stepper({ title: t("Hueco abajo (pie)"), get: () => L().bottomGap, set: (v) => setL({ bottomGap: v }), min: 0, max: () => Math.max(0, L().bottom) }),
        h("hr", { class: "divider" }),
        h("div", { class: "row" }, t("Total"), h("div", { class: "spacer" }), h("span", { class: "bold" }, text(() => t("{0} LEDs", count())))),
        warning),
      card(
        sectionLabel(t("Inicio y sentido")),
        pickerRow(t("Primer LED"), select(() => CORNERS.map((c) => [t(CORNER_TITLES[c]), c]), () => L().start, (start) => setL({ start }), { label: t("Primer LED"), width: 180 })),
        segmented([[t("Horario"), true], [t("Antihorario"), false]], () => L().clockwise, (clockwise) => setL({ clockwise }), true),
        step(t("Desplazamiento"), "offset", -200, 200),
        slider({ title: t("Profundidad de muestreo"), get: () => L().depth, set: (depth) => setL({ depth }), min: 0.03, max: 0.3, step: 0.01, format: percent }),
        slider({ title: t("Solapamiento"), get: () => L().overlap, set: (overlap) => setL({ overlap }), min: 0, max: 1, step: 0.01, format: percent }))),
    h("div", { class: "card" }, h("div", { class: "row", style: "gap:14px" },
      h("span", { style: "color:var(--accent-text);display:flex" }, icon("wand", 20)),
      h("div", { class: "col", style: "gap:2px;flex:1" },
        h("span", { class: "bold" }, t("Asistente de disposición")),
        h("span", { class: "muted", style: "font-size:12px" }, t("Enciende un LED, llévalo al último y a cada esquina, y Ledly calcula el resto."))),
      button(t("Empezar…"), layoutWizard, "primary", { disabled: () => !isConnected() }))),
    card(
      sectionLabel(t("Comprobar")),
      h("div", { class: "muted" }, t("«Dirección» enciende el primer LED en verde, el segundo en azul y el último en rojo. «Lados» pinta cada lado de un color: arriba rojo, derecha verde, abajo azul, izquierda amarillo.")),
      h("div", { class: "row" }, testButton(t("Dirección"), "direction"), testButton(t("Lados"), "sides"), endTest)));
}

// MARK: - Device

export function devicePane() {
  const st = () => S.status ?? {};
  const statusCard = card(
    h("div", { class: "row" },
      dotEl(true),
      h("span", { style: "font-size:15px;font-weight:600" }, text(deviceTitle)),
      h("div", { class: "spacer" }),
      button(t("Reconectar"), () => post("/api/app/device/reconnect"), "secondary", { disabled: () => !S.settings.portPath })),
    dyn(() => [deviceKind(), device().firmware, device().firmwareOutdated, device().ledlyVersion, device().error], () => {
      const d = device();
      const out = [];
      if (deviceKind() === "connected") {
        out.push(h("div", { class: "muted selectable" }, d.firmware ? t("Firmware: {0}", d.firmware) : t("Firmware Adalight sin identificar (no es el de Ledly).")));
        if (d.firmwareOutdated && d.ledlyVersion != null) {
          out.push(h("div", { class: "row" },
            h("span", { class: "warn-text", style: "display:flex" }, icon("arrowUpCircle")),
            h("span", { style: "flex:1" }, t("El Nano lleva la versión {0} del firmware; la app trae la {1}, que va más rápida.", d.ledlyVersion, S.meta?.firmware?.version ?? 2)),
            button(t("Actualizar firmware"), () => {
              const pin = d.ledlyPin ?? S.settings.dataPin;
              if (d.ledlyPin) patch({ dataPin: pin }, 0);
              if (S.settings.portPath) post("/api/app/device/flash", { pin, port: S.settings.portPath });
            }, "primary", { disabled: flashing })));
        }
      }
      if (deviceKind() === "failed" && d.error) out.push(h("div", { class: "danger-text selectable" }, d.error));
      return out;
    }),
    h("div", { class: "row", style: "padding-top:6px" }, sectionLabel(t("Puerto")), h("div", { class: "spacer" }),
      iconButton("refresh", t("Actualizar lista"), () => post("/api/app/device/refresh-ports"))),
    dyn(() => [st().ports ?? [], S.settings.portPath], () => {
      const ports = st().ports ?? [];
      const list = ports.map(portRow);
      if (!ports.length) list.unshift(h("div", { class: "faint" }, t("No hay ningún puerto USB serie. Conecta el Arduino.")));
      const path = S.settings.portPath;
      if (path && !ports.some((p) => p.path === path)) list.push(portRow({ path, name: t("No conectado"), description: path }));
      return list;
    }));

  return pane(t("Dispositivo"), t("Arduino Nano con tira WS2812B, conectado por USB."),
    statusCard,
    firmwareCard(),
    powerCard(),
    card(
      sectionLabel(t("Conexión")),
      h("div", { class: "muted" }, t("Datos de la tira (DIN) al pin elegido del Nano, con una resistencia de 330 Ω si puedes. GND de la tira al GND del Nano. Alimenta la tira con una fuente de 5 V propia: el USB solo aguanta unos pocos LEDs a poco brillo.")),
      h("div", { class: "small" }, t("Protocolo Adalight a {0} baudios. El firmware de Ledly confirma cada fotograma, así que no se pierden datos.", formatNumber(FIRMWARE_BAUD)))));
}

function dotEl(big = false) {
  const dot = h("span", { class: `dot${big ? " big" : ""}` });
  bind(() => { dot.style.background = deviceColor(); });
  return dot;
}

function portRow(port) {
  const selected = S.settings.portPath === port.path;
  return h("button", { type: "button", class: `port-row${selected ? " selected" : ""}`, onclick: () => patch({ portPath: port.path }, 0) },
    h("span", { class: "radio" }),
    h("div", { class: "col", style: "gap:2px;flex:1;min-width:0" },
      h("div", { class: "row", style: "gap:6px" }, h("span", { class: "medium" }, port.name || port.path),
        port.looksLikeArduino ? h("span", { class: "badge" }, t("Arduino")) : null),
      h("span", { class: "mono faint", style: "font-size:11px" }, port.description || port.path)));
}

const flashState = () => S.status?.flash ?? { state: "idle" };
const flashing = () => flashState().state === "running";

function firmwareCard() {
  const pins = S.meta?.firmware?.pins ?? [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
  return card(
    sectionLabel(t("Firmware")),
    h("div", { class: "muted" }, t("Instala en el Nano el programa que recibe los colores. Solo hace falta una vez, o al cambiar de pin.")),
    h("div", { class: "row" },
      h("span", null, t("Pin de datos")),
      select(() => pins.map((p) => [`D${p}`, p]), () => S.settings.dataPin, (dataPin) => patch({ dataPin }, 0), { label: t("Pin de datos"), width: 100 }),
      h("div", { class: "spacer" }),
      button(t("Instalar firmware"), () => post("/api/app/device/flash", { pin: S.settings.dataPin, port: S.settings.portPath }), "primary",
        { icon: "download", disabled: () => !S.settings.portPath || flashing() })),
    dyn(() => [flashState().state, flashState().error], () => {
      const f = flashState();
      if (f.state === "running") {
        const label = h("div", { class: "muted" });
        const bar = h("progress", { max: 1 });
        bind(() => {
          const s = flashState();
          const p = Math.max(0, Math.min(1, s.progress ?? 0));
          const pct = Math.floor(p * 100);
          const [msg, total] = {
            connecting: [t("Conectando con el bootloader…"), 0],
            backingUp: [t("Guardando copia del firmware actual… {0} %", pct), p * 0.3],
            writing: [t("Escribiendo… {0} %", pct), 0.3 + p * 0.4],
            verifying: [t("Verificando… {0} %", pct), 0.7 + p * 0.3],
            done: [t("Listo"), 1],
          }[s.step] ?? [t("Conectando con el bootloader…"), 0];
          label.textContent = msg;
          bar.value = total;
        });
        return h("div", { class: "col", style: "gap:6px" }, label, bar);
      }
      if (f.state === "done") {
        return h("div", { class: "row" }, h("span", { style: "color:var(--ok);display:flex" }, icon("checkCircle")),
          h("span", { style: "flex:1" }, t("Firmware instalado en el pin D{0}.", S.settings.dataPin)),
          button(t("Vale"), () => post("/api/app/device/flash/reset"), "ghost"));
      }
      if (f.state === "failed") {
        return h("div", { class: "row top" }, h("span", { class: "danger-text", style: "display:flex" }, icon("xCircle")),
          h("span", { class: "selectable", style: "flex:1" }, f.error ?? t("Error")),
          button(t("Vale"), () => post("/api/app/device/flash/reset"), "ghost"));
      }
      return null;
    }));
}

function powerCard() {
  const power = () => S.status?.power ?? { milliamps: 0, limited: false };
  const limited = h("span", { class: "badge warn" }, t("Limitado"));
  bind(() => { limited.hidden = !power().limited; });
  return card(
    sectionLabel(t("Consumo")),
    switchRow(t("Limitar consumo"),
      t("Baja el brillo de toda la tira si el consumo estimado pasa del límite. Ponlo un poco por debajo de lo que da tu fuente (el USB del PC da 500 mA)."),
      () => S.settings.powerLimitEnabled, (powerLimitEnabled) => patch({ powerLimitEnabled }, 0)),
    dyn(() => S.settings.powerLimitEnabled, () => S.settings.powerLimitEnabled
      ? slider({ title: t("Límite"), get: () => S.settings.powerLimitMilliamps, set: (v) => patch({ powerLimitMilliamps: Math.round(v) }),
        min: 200, max: 10000, step: 100, format: (v) => `${Math.round(v)} mA` })
      : null),
    h("div", { class: "row", style: "gap:8px" }, t("Consumo estimado ahora"), h("div", { class: "spacer" }), limited,
      h("span", { class: "mono" }, text(() => `${Math.round(power().milliamps ?? 0)} mA`))),
    h("div", { class: "small" }, t("Estimación para WS2812B: unos 20 mA por color a tope (60 mA en blanco) más 1 mA por LED.")));
}

// MARK: - Image

const SAMPLING = [["mean", "Media"], ["linear", "Lineal"], ["dominant", "Dominante"]];
const SMOOTHING = [["exponential", "Exponencial"], ["spring", "Muelle"]];

export function imagePane() {
  const s = () => S.settings;
  const set = (key, delay) => (v) => patch({ [key]: v }, delay);
  const gain = (title, key) => slider({ title, get: () => s()[key], set: set(key), min: 0, max: 1, step: 0.01 });

  return pane(t("Imagen"), t("Cómo se captura la pantalla y cómo se corrigen los colores antes de enviarlos."),
    captureViewer(),
    card(
      sectionLabel(t("Captura")),
      pickerRow(t("Capturar"), h("div", { class: "row", style: "gap:6px" },
        select(() => {
          const apps = S.status?.apps ?? [];
          const list = [[t("Pantalla completa"), ""]];
          const id = s().captureApp;
          if (id && !apps.some((a) => a.id === id)) list.push([t("{0} (no abierta)", s().captureAppName ?? id), id]);
          return list.concat(apps.map((a) => [a.name, a.id]));
        }, () => s().captureApp ?? "", (id) => {
          const app = (S.status?.apps ?? []).find((a) => a.id === id);
          patch(id ? { captureApp: id, captureAppName: app?.name ?? s().captureAppName } : { captureApp: null }, 0);
        }, { label: t("Capturar"), width: 260 }),
        iconButton("refresh", t("Actualizar lista"), async () => {
          try { S.status = await api("GET", "/api/app/status"); refresh(); } catch { /* ignore */ }
        }))),
      pickerRow(text(() => (s().captureApp ? t("Pantalla si la app no tiene ventanas") : t("Pantalla"))),
        select(() => [[t("Principal"), ""], ...(S.status?.displays ?? []).map((d) => [d.name || d.id, d.id])],
          () => s().displayId ?? "", set("displayId", 0), { label: t("Pantalla"), width: 260 })),
      pickerRow(t("Fotogramas por segundo"), select(() => [15, 20, 30, 45, 60].map((n) => [String(n), n]), () => s().captureFps, set("captureFps", 0), { width: 120 })),
      pickerRow(t("Cálculo del color"), select(() => SAMPLING.map(([id, k]) => [t(k), id]), () => s().sampling, set("sampling", 0), { width: 160 })),
      switchRow(t("Detectar bordes negros"), t("Ignora las franjas negras de películas y vídeos para que los LEDs de arriba y abajo no se apaguen."),
        () => s().blackBorders, set("blackBorders", 0))),
    card(
      h("div", { class: "row" }, sectionLabel(t("Color")), h("div", { class: "spacer" }),
        button(t("Balance de blancos…"), whiteBalanceWizard),
        button(t("Restablecer"), async () => adoptSettings(await post("/api/app/settings/reset-image")), "ghost")),
      slider({ title: t("Suavizado"), get: () => s().smoothingMs, set: set("smoothingMs"), min: 0, max: 1000, step: 10, format: (v) => `${Math.round(v)} ms` }),
      h("div", { class: "row" }, t("Tipo de suavizado"), h("div", { class: "spacer" }),
        segmented(SMOOTHING.map(([id, k]) => [t(k), id]), () => s().smoothing, set("smoothing", 0))),
      switchRow(t("Anti-parpadeo"), t("Evita que los LEDs tiemblen entre dos niveles en escenas oscuras."), () => s().antiFlicker, set("antiFlicker", 0)),
      slider({ title: t("Gamma"), get: () => s().gamma, set: set("gamma"), min: 0.5, max: 3, step: 0.01 }),
      slider({ title: t("Saturación"), get: () => s().saturation, set: set("saturation"), min: 0, max: 2, step: 0.01 }),
      slider({ title: t("Temperatura"), get: () => s().temperature, set: set("temperature"), min: 2500, max: 10000, step: 100, format: (v) => `${Math.round(v)} K` }),
      slider({ title: t("Luz mínima"), get: () => s().backlight, set: set("backlight"), min: 0, max: 0.2, step: 0.005, format: percent }),
      gain(t("Rojo"), "redGain"),
      gain(t("Verde"), "greenGain"),
      gain(t("Azul"), "blueGain")),
    calibrationCard());
}

/** What the capture sees, with each LED's zone in its color and the area left after cropping black borders. */
function captureViewer() {
  let bitmap = null;
  let size = null;
  let content = null;
  let zoneList = [];
  let layoutKey = "";
  const active = () => S.settings.mode === "screen" && S.settings.isOn;

  const canvas = autoCanvas((ctx, w, hgt) => {
    if (!bitmap || !active()) return;
    const iw = size?.[0] ?? bitmap.width, ih = size?.[1] ?? bitmap.height;
    const scale = Math.min(w / Math.max(iw, 1), hgt / Math.max(ih, 1));
    const fw = iw * scale, fh = ih * scale, fx = (w - fw) / 2, fy = (hgt - fh) / 2;
    ctx.imageSmoothingQuality = "medium";
    ctx.drawImage(bitmap, fx, fy, fw, fh);
    const c = content ?? [0, 0, iw, ih];
    const area = { x: fx + c[0] * scale, y: fy + c[1] * scale, w: c[2] * scale, h: c[3] * scale };
    zoneList.forEach((z, i) => {
      const col = S.colors[i];
      const css = col ? `rgb(${col.map((v) => Math.round(v * 255)).join(",")})` : getComputedStyle(document.documentElement).getPropertyValue("--accent");
      const r = [area.x + z.rect.x * area.w, area.y + z.rect.y * area.h, z.rect.w * area.w, z.rect.h * area.h];
      ctx.globalAlpha = 0.35;
      ctx.fillStyle = css;
      ctx.fillRect(...r);
      ctx.globalAlpha = 1;
      ctx.strokeStyle = css;
      ctx.lineWidth = 1;
      ctx.strokeRect(...r);
    });
    if (area.w < fw - 1 || area.h < fh - 1) {
      ctx.setLineDash([6, 4]);
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue("--warn");
      ctx.strokeRect(area.x, area.y, area.w, area.h);
      ctx.setLineDash([]);
    }
  });
  bind(() => {
    const key = JSON.stringify(S.settings.layout);
    if (key !== layoutKey) { layoutKey = key; zoneList = zones(S.settings.layout); canvas.redraw(); }
  });
  onCleanup(watchColors(() => canvas.redraw()));

  // ~5 previews a second, only while this view is shown (each request keeps them coming for 3 s).
  let busy = false;
  const poll = async () => {
    if (busy || document.hidden || !active()) return;
    busy = true;
    try {
      const r = await fetch("/api/app/capture.png", { cache: "no-store" });
      if (r.status === 200) {
        const rect = r.headers.get("X-Content-Rect")?.split(",").map(Number);
        const imageSize = r.headers.get("X-Image-Size")?.split(",").map(Number);
        const next = await createImageBitmap(await r.blob());
        bitmap?.close?.();
        bitmap = next;
        size = imageSize?.length === 2 && imageSize.every(Number.isFinite) ? imageSize : [next.width, next.height];
        content = rect?.length === 4 && rect.every(Number.isFinite) ? rect : null;
        refresh();
        canvas.redraw();
      }
    } catch { /* keep the last one */ }
    busy = false;
  };
  const timer = setInterval(poll, 200);
  poll();
  onCleanup(() => { clearInterval(timer); bitmap?.close?.(); });

  const message = dyn(() => [active(), S.status?.capture?.status, !!bitmap, S.status?.capture?.message], () => {
    if (!active()) {
      return h("div", { class: "msg" }, t("La captura solo funciona con la tira encendida en modo pantalla."),
        button(t("Pasar a modo pantalla"), () => patch({ mode: "screen", isOn: true }, 0)));
    }
    const cap = S.status?.capture;
    if (cap?.status === "noPermission") return h("div", { class: "msg", style: "color:var(--warn)" }, t("Falta el permiso de Grabación de pantalla."));
    if (cap?.status === "failed" && !bitmap) return h("div", { class: "msg", style: "color:var(--danger)" }, cap.message || t("Error"));
    if (!bitmap) return h("div", { class: "msg" }, h("div", { class: "spinner" }));
    return null;
  });
  bind(() => { canvas.hidden = !active() || !bitmap; });

  const caption = h("div", { class: "small" });
  bind(() => {
    caption.hidden = !bitmap || !active();
    if (!bitmap) return;
    const [iw, ih] = size;
    const c = content ?? [0, 0, iw, ih];
    const top = Math.round(c[1]), side = Math.round(c[0]);
    caption.textContent = top === 0 && side === 0
      ? t("Captura de {0} × {1} px. Cada recuadro es la zona que lee un LED, pintada con su color.", iw, ih)
      : t("Bordes negros detectados: {0} px arriba y abajo, {1} px a los lados (de {2} × {3}). La línea discontinua marca la parte que se usa.", top, side, iw, ih);
  });

  const source = h("span", { class: "small", style: "white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:60%" });
  bind(() => {
    const src = S.status?.capture?.source ?? "";
    source.textContent = src;
    source.hidden = !src || S.settings.mode !== "screen";
  });

  return card(
    h("div", { class: "row" }, sectionLabel(t("Vista de la captura")), h("div", { class: "spacer" }), source),
    h("div", { class: "capture-box" }, canvas, message),
    caption);
}

/** Advanced calibration: target color per corner of the RGB cube, each one testable on the strip. */
function calibrationCard() {
  let testing = null;
  const corners = S.meta?.calibrationCorners ?? [];
  const cal = () => S.settings.calibration ?? { enabled: false, targets: {} };
  const target = (c) => (cal().targets?.[c.id] ?? c.input).toUpperCase();
  const isIdentity = () => corners.every((c) => target(c) === c.input.toUpperCase());

  const test = (c) => { testing = c.id; setTest({ pattern: "calibration", color: c.input }); };
  const stopTest = () => { if (testing) { testing = null; clearTest(["calibration"]); refresh(); } };
  onCleanup(stopTest);

  const setTarget = (c, hex) => {
    const targets = { ...(cal().targets ?? {}) };
    if (hex.toUpperCase() === c.input.toUpperCase()) delete targets[c.id];
    else targets[c.id] = hex.toUpperCase();
    patch({ calibration: { targets } });
  };

  const row = (c) => {
    const reset = iconButton("undo", t("Restablecer"), () => { setTarget(c, c.input); if (testing === c.id) test(c); });
    const probe = button("", () => test(c));
    bind(() => {
      reset.hidden = target(c) === c.input.toUpperCase();
      probe.textContent = testing === c.id ? t("Probando") : t("Probar");
      probe.className = `btn ${testing === c.id ? "primary" : "secondary"}`;
    });
    return h("div", { class: "calib-row" },
      h("span", { class: "swatch", title: t("Color en pantalla"), style: { background: c.input } }),
      h("span", { class: "name" }, t(c.titleKey)),
      h("span", { class: "faint", style: "display:flex" }, icon("arrowRight", 14)),
      colorInput(() => target(c), (hex) => { setTarget(c, hex); test(c); }, t("Color que debe dar la tira")),
      h("span", { class: "mono faint", style: "font-size:11px" }, text(() => target(c))),
      h("div", { class: "spacer" }),
      reset, probe);
  };

  return card(
    h("div", { class: "row" }, sectionLabel(t("Calibración avanzada")), h("div", { class: "spacer" }),
      button(t("Restablecer"), () => {
        patch({ calibration: { targets: {} } }, 0);
        const c = corners.find((x) => x.id === testing);
        if (c) test(c);
      }, "ghost", { disabled: isIdentity })),
    switchRow(t("Activar calibración avanzada"),
      t("Corrige cómo saca la tira cada color. Pulsa «Probar», compara el LED con la muestra de la pantalla y ajusta el color de la derecha hasta que se parezcan. Los colores intermedios se ajustan solos."),
      () => cal().enabled, (enabled) => { patch({ calibration: { enabled } }, 0); if (!enabled) stopTest(); }),
    dyn(() => cal().enabled, () => cal().enabled ? [
      ...corners.map(row),
      (() => {
        const end = button(t("Terminar prueba"), stopTest, "primary");
        bind(() => { end.hidden = !testing; });
        return h("div", null, end);
      })(),
    ] : null));
}

// MARK: - API

export function apiPane() {
  const s = () => S.settings;
  const st = () => S.status ?? {};
  const validPort = (v) => /^\d+$/.test(v.trim()) && Number(v) >= 1 && Number(v) <= 65535;
  const base = () => `http://localhost:${s().apiPort}`;
  const auth = () => (s().apiToken ? ` -H "Authorization: Bearer ${s().apiToken}"` : "");

  const statusText = () => {
    const a = st().api;
    if (a?.status === "running") return t("Escuchando en el puerto {0} · {1} clientes WebSocket", a.port, a.clients ?? 0);
    if (a?.status === "failed") return a.error || t("Error");
    return t("Desactivada");
  };
  const ddpText = () => {
    if (st().live && st().liveSource) return t("Recibiendo de {0}", st().liveSource);
    const d = st().ddp;
    if (d?.status === "running") return t("Esperando fotogramas en UDP {0}", d.port);
    if (d?.status === "failed") return d.error || t("Error");
    return t("Desactivada");
  };

  // Windows shells (cmd/PowerShell) need double quotes; JSON quotes are escaped for curl.exe.
  const q = (json) => `"${json.replace(/"/g, '\\"')}"`;
  const examples = () => [
    [t("Estado"), `curl${auth()} ${base()}/api/state`],
    [t("Color fijo"), `curl${auth()} -X POST ${base()}/api/state -d ${q('{"color":"#FF2D95","brightness":60}')}`],
    [t("Efecto"), `curl${auth()} -X POST ${base()}/api/state -d ${q('{"effect":"fire","speed":1.5}')}`],
    [t("Volver a la pantalla"), `curl${auth()} -X POST ${base()}/api/state -d ${q('{"mode":"screen"}')}`],
    [t("Aviso de 3 segundos"), `curl${auth()} -X POST ${base()}/api/state -d ${q('{"effect":"strobe","color":"#FF0000","duration":3}')}`],
    [t("Apagar"), `curl${auth()} -X POST ${base()}/api/state -d ${q('{"on":false}')}`],
    [t("Colores en vivo (WebSocket)"), `websocat ws://localhost:${s().apiPort}/api/ws${s().apiToken ? `?token=${s().apiToken}` : ""}  # {"subscribe":"leds"}`],
    [t("Soltar el tiempo real"), `curl${auth()} -X POST ${base()}/api/state -d ${q('{"live":false}')}`],
    [t("WLED"), `curl -X POST ${base()}/json/state -d ${q('{"on":true,"bri":128,"seg":[{"col":[[0,120,255]]}]}')}`],
  ];

  return pane(t("API"), t("Controla la tira y consulta su estado desde otras apps, scripts o Home Assistant."),
    card(
      switchRow(t("Activar API"), statusText, () => s().apiEnabled, (apiEnabled) => patch({ apiEnabled }, 0)),
      h("div", { class: "row" }, t("Puerto"), h("div", { class: "spacer" }),
        textField({ get: () => String(s().apiPort), set: (v) => patch({ apiPort: Number(v) }, 0), placeholder: "8765", width: 100, validate: validPort })),
      switchRow(t("Solo este PC"), t("Rechaza conexiones de otros equipos de la red."), () => s().apiLocalOnly, (apiLocalOnly) => patch({ apiLocalOnly }, 0)),
      switchRow(t("Anunciar como WLED"), t("Home Assistant y las apps de WLED la encuentran solas en la red."), () => s().wledDiscovery, (wledDiscovery) => patch({ wledDiscovery }, 0)),
      h("div", { class: "row" }, t("Token (opcional)"), h("div", { class: "spacer" }),
        textField({ get: () => s().apiToken ?? "", set: (apiToken) => patch({ apiToken }, 400), placeholder: t("sin token"), width: 240, live: true })),
      h("div", { class: "small" }, t("Con token, /api pide «Authorization: Bearer <token>». La parte compatible con WLED sigue abierta."))),
    card(
      sectionLabel(t("Tiempo real (DDP)")),
      switchRow(t("Recibir fotogramas por DDP"), ddpText, () => s().ddpEnabled, (ddpEnabled) => patch({ ddpEnabled }, 0)),
      h("div", { class: "row" }, t("Puerto UDP"), h("div", { class: "spacer" }),
        textField({ get: () => String(s().ddpPort), set: (v) => patch({ ddpPort: Number(v) }, 0), placeholder: "4048", width: 100, validate: validPort })),
      h("div", { class: "small" }, t("Al llegar fotogramas, mandan sobre la pantalla, los efectos, el color y los avisos. Si dejan de llegar 2,5 s, vuelve a lo que había. Se aplican el brillo, la temperatura y el límite de consumo. Sin token, como en WLED."))),
    card(
      h("div", { class: "row" }, sectionLabel(t("Direcciones")), h("div", { class: "spacer" }),
        button(t("Documentación de la API"), () => openUrl(`${base()}/api/docs`), "secondary", { icon: "external" })),
      dyn(() => [s().apiPort, s().apiLocalOnly, st().api?.addresses ?? []], () => {
        const urls = [base()];
        if (!s().apiLocalOnly) urls.push(...(st().api?.addresses ?? []).map((ip) => `http://${ip}:${s().apiPort}`));
        return urls.map((url) => h("div", { class: "row" }, h("span", { class: "mono selectable", style: "flex:1" }, url),
          iconButton("copy", t("Copiar"), () => copyText(url))));
      })),
    card(
      sectionLabel(t("Ejemplos")),
      dyn(() => [s().apiPort, s().apiToken, currentLanguageKey()], () => examples().map(([title, command]) =>
        h("div", { class: "col", style: "gap:4px" }, h("span", { class: "muted" }, title),
          h("div", { class: "code" }, h("span", { class: "mono" }, command), iconButton("copy", t("Copiar"), () => copyText(command))))))));
}

const currentLanguageKey = () => S.settings.language;

// MARK: - Settings

const COFFEE_URL = "https://buymeacoffee.com/drarek";

export function settingsPane() {
  const s = () => S.settings;
  const upd = () => S.status?.update ?? { state: "idle" };
  let userChecked = false;

  const languages = () => (S.meta?.languages ?? ["system", "en", "es", "ca"]).map((code) => [
    { system: t("Sistema"), en: "English", es: "Español", ca: "Català" }[code] ?? code, code]);

  const updates = card(
    sectionLabel(t("Actualizaciones")),
    switchRow(t("Buscar actualizaciones automáticamente"), null, () => s().autoCheckUpdates, (autoCheckUpdates) => patch({ autoCheckUpdates }, 0)),
    h("div", { class: "row" },
      h("div", { class: "col", style: "gap:2px;flex:1" },
        h("span", null, text(() => t("Versión {0}", S.status?.version ?? "…"))),
        h("span", { class: "small" }, text(() => {
          const last = s().lastUpdateCheck;
          if (!last) return "";
          const d = new Date(last);
          return Number.isNaN(d.getTime()) ? "" : t("Última comprobación: {0}", d.toLocaleString(document.documentElement.lang || undefined));
        }))),
      button(t("Buscar actualizaciones…"), () => { userChecked = true; post("/api/app/update/check"); refresh(); }, "secondary",
        { disabled: () => upd().state !== "idle" })),
    dyn(() => [upd().state, upd().available, upd().error, upd().notes, userChecked, s().skippedVersion], () => {
      const u = upd();
      const out = [];
      if (u.state === "checking") out.push(h("div", { class: "row muted" }, h("div", { class: "spinner", style: "border-color: var(--line-strong); border-top-color: var(--text2)" }), t("Buscando actualizaciones…")));
      if (u.state === "downloading" || u.state === "installing") out.push(h("div", { class: "row muted" }, h("div", { class: "spinner", style: "border-color: var(--line-strong); border-top-color: var(--text2)" }), t("Actualizando…")));
      if (u.error) out.push(h("div", { class: "danger-text selectable" }, u.error));
      if (u.available && u.available !== s().skippedVersion) {
        out.push(h("div", { class: "banner info", style: "flex-direction:column;align-items:stretch" },
          h("div", { class: "row" }, h("span", { style: "color:var(--accent-text);display:flex" }, icon("arrowUpCircle")),
            h("span", { class: "bold" }, t("Ledly {0} está disponible", u.available))),
          h("div", { class: "muted" }, t("Tienes la versión {0}.", S.status?.version ?? "")),
          u.notes ? h("div", { class: "small selectable", style: "white-space:pre-wrap;max-height:160px;overflow:auto" }, u.notes) : null,
          h("div", { class: "row" }, h("div", { class: "spacer" }),
            button(t("Omitir esta versión"), () => post("/api/app/update/skip"), "ghost"),
            button(t("Actualizar ahora"), () => post("/api/app/update/install"), "primary", { disabled: () => upd().state !== "idle" }))));
      } else if (userChecked && u.state === "idle" && !u.error) {
        out.push(h("div", { class: "row" }, h("span", { style: "color:var(--ok);display:flex" }, icon("checkCircle")),
          t("Tienes la última versión ({0}).", S.status?.version ?? "")));
      }
      return out;
    }));

  return pane(t("Ajustes"), null,
    h("div", { class: "card tight" },
      pickerRow(t("Idioma"), select(languages, () => s().language, (language) => patch({ language }, 0), { label: t("Idioma"), width: 180 })),
      switchRow(t("Abrir al iniciar sesión"), null, () => s().launchAtLogin, (launchAtLogin) => patch({ launchAtLogin }, 0)),
      switchRow(t("Apagar al dormir"), t("Apaga la tira cuando el PC o las pantallas se duermen."), () => s().offWhenAsleep, (offWhenAsleep) => patch({ offWhenAsleep }, 0))),
    updates,
    card(
      h("div", { class: "row" },
        button(t("Invítame a un café…"), () => openUrl(COFFEE_URL), "secondary", { icon: "coffee" }),
        button(t("Ver en GitHub"), () => openUrl(`https://github.com/${S.meta?.repo ?? "drarekx/ledly-windows"}`), "ghost", { icon: "external" }),
        h("div", { class: "spacer" }),
        button(t("Salir de Ledly"), () => post("/api/app/quit"), "danger", { icon: "quit" }))));
}
