// Pieces shared by several panes.

import { S, watchColors } from "./state.js";
import { t } from "./i18n.js";
import { h, autoCanvas, onCleanup, bind } from "./ui.js";
import { zones, drawStrip, ledCount } from "./layout.js";

export const device = () => S.status?.device ?? { status: "disconnected" };
export const deviceKind = () => {
  const s = device().status;
  return s === "error" ? "failed" : s;
};
export const isConnected = () => deviceKind() === "connected";

export function deviceTitle() {
  switch (deviceKind()) {
    case "connected": return t("Tira conectada");
    case "connecting": return t("Conectando…");
    case "failed": return t("Error en la tira");
    default: return t("Tira desconectada");
  }
}

export function deviceColor() {
  switch (deviceKind()) {
    case "connected": return "var(--ok)";
    case "connecting": return "var(--warn)";
    case "failed": return "var(--danger)";
    default: return "var(--text3)";
  }
}

export const count = () => (S.settings ? ledCount(S.settings.layout) : 0);
export const fps = () => Math.round(S.status?.fps ?? 0);

export function effectInfo(id) {
  return S.meta?.effects?.find((e) => e.id === id) ?? { id, titleKey: id, music: false };
}
export const effectTitle = (id) => t(effectInfo(id).titleKey);

export const MODE_TITLES = { screen: "Pantalla", effect: "Efecto", color: "Color" };

/** The screen with its LEDs lit with the colors going to the strip (subscribes to them while shown). */
export function liveStrip({ showZones = false, showNumbers = false } = {}) {
  let zoneList = [];
  let layoutKey = "";
  const canvas = autoCanvas((ctx, w, hgt) => drawStrip(ctx, w, hgt, zoneList, S.colors, { showZones, showNumbers }));
  bind(() => {
    const key = JSON.stringify(S.settings.layout);
    if (key !== layoutKey) {
      layoutKey = key;
      zoneList = zones(S.settings.layout);
      canvas.redraw();
    }
  });
  onCleanup(watchColors(() => canvas.redraw()));
  return h("div", { class: "preview-wrap" }, canvas);
}

/** A still strip preview for a given layout (wizard result). */
export function staticStrip(layout, opts) {
  const list = zones(layout);
  return h("div", { class: "preview-wrap", style: { maxWidth: "420px" } },
    autoCanvas((ctx, w, hgt) => drawStrip(ctx, w, hgt, list, [], opts)));
}

export const percent = (v) => `${Math.round(v * 100)} %`;
