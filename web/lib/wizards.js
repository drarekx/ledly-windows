// Layout wizard and white balance assistant (CaptureViews.swift in the Mac app).

import { S, patch, api, refresh, setTest, clearTest } from "./state.js";
import { t } from "./i18n.js";
import { h, bind, dyn, text, button, slider, segmented, openDialog, onCleanup, autoCanvas } from "./ui.js";
import { icon } from "./icons.js";
import { ledCount, cornerIndices, CORNER_TITLES } from "./layout.js";
import { staticStrip } from "./common.js";

const CORNER_ORDER = ["topLeft", "topRight", "bottomRight", "bottomLeft"];
const MAX_LEDS = 400;

/** Finds the layout by lighting one LED at a time: the last LED and each corner are marked. */
export function layoutWizard() {
  const w = { step: "intro", corner: null, cursor: 0, count: 0, marks: {}, hasGap: false, result: null, solving: false };
  const layoutCount = () => ledCount(S.settings.layout);
  const cursorStep = () => w.step === "last" || w.step === "corner";

  const show = () => {
    const n = w.step === "last" ? Math.max(w.cursor + 11, layoutCount()) : w.count;
    setTest({ pattern: "cursor", index: w.cursor, count: n });
  };
  const changed = () => refresh();

  const move = (delta) => {
    const n = w.step === "last" ? MAX_LEDS : Math.max(w.count, 1);
    w.cursor = (((w.cursor + delta) % n) + n) % n;
    show();
    changed();
  };

  const go = (corner) => {
    w.step = "corner";
    w.corner = corner;
    const guess = w.marks[corner] ?? (layoutCount() === w.count ? cornerIndices(S.settings.layout)[corner] : null);
    if (guess != null) w.cursor = guess;
    w.cursor = Math.min(w.cursor, w.count - 1);
    show();
    changed();
  };

  const confirm = () => {
    if (w.step === "last") {
      w.count = w.cursor + 1;
      go("topLeft");
    } else if (w.step === "corner") {
      w.marks[w.corner] = w.cursor;
      const i = CORNER_ORDER.indexOf(w.corner);
      if (i + 1 < CORNER_ORDER.length) go(CORNER_ORDER[i + 1]);
      else { w.step = "gap"; clearTest(["cursor"]); changed(); }
    }
  };

  const back = () => {
    if (w.step === "last") { w.step = "intro"; clearTest(["cursor"]); }
    else if (w.step === "corner") {
      const i = CORNER_ORDER.indexOf(w.corner);
      if (i > 0) { go(CORNER_ORDER[i - 1]); return; }
      w.cursor = w.count - 1;
      w.step = "last";
      show();
    } else if (w.step === "gap") { go("bottomLeft"); return; }
    changed();
  };

  const solve = async (hasGap) => {
    w.hasGap = hasGap;
    w.solving = true;
    changed();
    try {
      const r = await api("POST", "/api/app/layout/solve", { count: w.count, corners: w.marks, hasBottomGap: hasGap });
      w.result = r?.layout ?? null;
    } catch {
      w.result = null;
    }
    w.solving = false;
    w.step = "result";
    changed();
  };

  const restart = () => { w.marks = {}; w.result = null; w.step = "intro"; clearTest(["cursor"]); changed(); };

  let close;
  const finish = () => { clearTest(["cursor"]); close(); };

  close = openDialog((closeDialog) => {
    const onKey = (e) => {
      if (!cursorStep()) return;
      const delta = { ArrowLeft: -1, ArrowRight: 1, ArrowDown: -10, ArrowUp: 10 }[e.key];
      if (delta) { e.preventDefault(); move(delta); }
      else if (e.key === "Enter") { e.preventDefault(); confirm(); }
    };
    document.addEventListener("keydown", onKey);
    onCleanup(() => document.removeEventListener("keydown", onKey));

    return [
      h("div", { class: "head" }, icon("wand"), h("h2", null, t("Asistente de disposición")), h("div", { class: "spacer" }),
        button(t("Cancelar"), finish, "ghost")),
      dyn(() => [w.step, w.corner, w.solving, !!w.result], () => {
        switch (w.step) {
          case "intro": return h("div", { class: "col", style: "gap:14px" },
            h("div", null, t("Vamos a encender un solo LED y moverlo por la tira. Mira la tira detrás del monitor y lleva la luz a cada punto que te pida: el último LED y las cuatro esquinas. Con eso Ledly calcula cuántos LEDs hay en cada lado, dónde empieza la tira y hacia dónde va.")),
            h("div", { class: "muted" }, t("Se mueve con los botones o con las flechas del teclado (← → de uno en uno, ↑ ↓ de diez en diez).")),
            h("div", { class: "row" }, h("div", { class: "spacer" }), button(t("Empezar"), () => {
              w.cursor = Math.max(0, layoutCount() - 1);
              w.step = "last";
              show();
              changed();
            }, "primary")));
          case "last":
          case "corner": {
            const corner = w.step === "corner" ? w.corner : null;
            const title = corner ? t(CORNER_TITLES[corner]) : t("Último LED");
            const detail = corner
              ? t("Mueve la luz hasta el LED que está justo en la esquina {0} de la pantalla (mirándola de frente).", t(CORNER_TITLES[corner]).toLowerCase())
              : t("Mueve la luz blanca hasta el último LED de la tira (el más lejano del Arduino).");
            const stepButton = (label, d) => button(label, () => move(d), "secondary", { height: 34 });
            return h("div", { class: "col", style: "gap:16px" },
              h("div", { class: "row top", style: "gap:20px" }, cornerDiagram(corner, Object.keys(w.marks)),
                h("div", { class: "col", style: "gap:6px;flex:1" }, h("span", { style: "font-size:15px;font-weight:600" }, title), h("span", { class: "muted" }, detail))),
              h("div", { class: "row", style: "gap:8px" },
                stepButton("−10", -10), stepButton("−1", -1),
                h("span", { class: "cursor-led" }, text(() => t("LED {0}", w.cursor + 1))),
                stepButton("+1", 1), stepButton("+10", 10)),
              h("div", { class: "row" },
                corner ? h("span", { class: "faint" }, t("De {0} LEDs", w.count)) : null,
                h("div", { class: "spacer" }),
                button(t("Atrás"), back, "ghost"),
                button(corner ? t("Esta es la esquina") : t("Este es el último"), confirm, "primary")));
          }
          case "gap": return h("div", { class: "col", style: "gap:14px" },
            h("div", null, t("¿Hay un hueco sin LEDs en el lado de abajo, por ejemplo para el pie del monitor?")),
            h("div", { class: "muted" }, t("Si lo hay, Ledly deja ese hueco en el centro de abajo para que los LEDs de los lados lean el sitio correcto.")),
            h("div", { class: "row" }, h("div", { class: "spacer" }),
              button(t("Atrás"), back, "ghost"),
              button(t("No"), () => solve(false), "secondary", { disabled: () => w.solving }),
              button(t("Sí, hay hueco"), () => solve(true), "primary", { disabled: () => w.solving })));
          default: {
            const r = w.result;
            if (!r) {
              return h("div", { class: "col", style: "gap:14px" },
                h("div", { class: "warn-text" }, t("Las esquinas marcadas no cuadran con una tira que dé la vuelta a la pantalla. Comprueba que marcaste cada esquina en su sitio y que la luz estaba en el último LED.")),
                h("div", { class: "row" }, h("div", { class: "spacer" }), button(t("Volver a empezar"), restart, "primary")));
            }
            return h("div", { class: "col", style: "gap:14px" },
              staticStrip(r, { showNumbers: true }),
              h("div", { class: "medium" }, t("Arriba {0} · derecha {1} · abajo {2} · izquierda {3}{4}", r.top, r.right, r.bottom - r.bottomGap, r.left,
                r.bottomGap > 0 ? t(" · hueco de {0}", r.bottomGap) : "")),
              h("div", { class: "muted" }, t("Empieza en la esquina {0}, en sentido {1}, desplazamiento {2}.", t(CORNER_TITLES[r.start]).toLowerCase(),
                r.clockwise ? t("horario") : t("antihorario"), r.offset)),
              h("div", { class: "row" }, h("div", { class: "spacer" }),
                button(t("Volver a empezar"), restart, "ghost"),
                button(t("Guardar"), () => {
                  patch({ layout: r }, 0);
                  setTest({ pattern: "direction" });
                  close();
                }, "primary")));
          }
        }
      }),
    ];
  }, { width: 620, onClose: () => clearTest(["cursor"]) });
}

/** Small monitor drawing with the corner being asked for highlighted. */
function cornerDiagram(highlight, marked) {
  const canvas = autoCanvas((ctx, w, hgt) => {
    const css = (n) => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
    const r = { x: 12, y: 12, w: w - 24, h: hgt - 24 };
    ctx.beginPath();
    ctx.roundRect(r.x, r.y, r.w, r.h, 4);
    ctx.strokeStyle = css("--line-strong");
    ctx.lineWidth = 2;
    ctx.stroke();
    const points = { topLeft: [r.x, r.y], topRight: [r.x + r.w, r.y], bottomRight: [r.x + r.w, r.y + r.h], bottomLeft: [r.x, r.y + r.h] };
    for (const [corner, [x, y]] of Object.entries(points)) {
      const on = corner === highlight;
      ctx.beginPath();
      ctx.arc(x, y, on ? 7 : 4.5, 0, Math.PI * 2);
      ctx.fillStyle = on ? (matchMedia("(prefers-color-scheme: light)").matches ? "#1c1c1e" : "#fff") : marked.includes(corner) ? css("--ok") : css("--line");
      ctx.fill();
    }
  }, "");
  canvas.style.cssText = "width:150px;height:96px;flex:none";
  return canvas;
}

/** White (or grey) on the strip next to the same color on screen; gains and temperature until both look alike. */
export function whiteBalanceWizard() {
  const s = () => S.settings;
  const saved = { temperature: s().temperature, redGain: s().redGain, greenGain: s().greenGain, blueGain: s().blueGain };
  let level = 1;
  const hex = () => {
    const v = Math.round(level * 255).toString(16).padStart(2, "0").toUpperCase();
    return `#${v}${v}${v}`;
  };
  const show = () => setTest({ pattern: "calibration", color: hex() });
  show();

  openDialog((close) => {
    const swatch = h("div", { class: "wb-swatch" });
    bind(() => { swatch.style.background = hex(); });
    const gain = (title, key) => slider({ title, get: () => s()[key], set: (v) => patch({ [key]: v }), min: 0, max: 1, step: 0.01 });
    return [
      h("div", { class: "head" }, icon("circleHalf"), h("h2", null, t("Balance de blancos"))),
      h("div", { class: "muted" }, t("La tira se pone en el mismo blanco que el recuadro de abajo. Mira los dos a la vez y mueve los controles hasta que el blanco de la tira se parezca al de la pantalla. Las tiras baratas suelen salir azuladas: baja un poco el azul o la temperatura.")),
      h("div", { class: "row", style: "gap:16px" }, swatch,
        h("div", { class: "col", style: "gap:8px" },
          h("span", null, t("Comparar con")),
          segmented([[t("Blanco"), 1], [t("Gris 50 %"), 0.5], [t("Gris 20 %"), 0.2]], () => level, (v) => { level = v; show(); refresh(); }),
          h("span", { class: "small" }, t("El gris sirve para ver si a poco brillo cambia el tono.")))),
      slider({ title: t("Temperatura"), get: () => s().temperature, set: (temperature) => patch({ temperature }), min: 2500, max: 10000, step: 100, format: (v) => `${Math.round(v)} K` }),
      gain(t("Rojo"), "redGain"),
      gain(t("Verde"), "greenGain"),
      gain(t("Azul"), "blueGain"),
      h("div", { class: "row" },
        button(t("Deshacer cambios"), () => patch(saved, 0), "ghost"),
        h("div", { class: "spacer" }),
        button(t("Hecho"), close, "primary")),
    ];
  }, { width: 560, onClose: () => clearTest(["calibration"]) });
}
