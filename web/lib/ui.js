// Tiny DOM toolkit: elements are built once; `bind` registers updaters that run on every refresh,
// and `dyn` rebuilds a part only when its key changes. Controls never fight the user: a slider being
// dragged or a field being edited is not overwritten.

import { icon } from "./icons.js";

export class Scope {
  constructor() { this.updaters = []; this.cleanups = []; }
  run() { for (const f of this.updaters) f(); }
  dispose() {
    for (const f of this.cleanups.splice(0)) { try { f(); } catch (e) { console.error(e); } }
    this.updaters = [];
  }
}

let current = null;

export function withScope(scope, build) {
  const previous = current;
  current = scope;
  try { return build(); } finally { current = previous; }
}

/** Runs `fn` now and on every refresh of the current scope. */
export function bind(fn) {
  current?.updaters.push(fn);
  fn();
}

/** Runs `fn` when the current scope is torn down (pane change, dyn rebuild, dialog closed). */
export function onCleanup(fn) {
  current?.cleanups.push(fn);
}

function toNodes(kids, out = []) {
  for (const k of kids) {
    if (k == null || k === false || k === true) continue;
    if (Array.isArray(k)) toNodes(k, out);
    else out.push(k instanceof Node ? k : document.createTextNode(String(k)));
  }
  return out;
}

/** h("div", {class, style, onclick, …}, ...children) */
export function h(tag, props, ...kids) {
  const el = tag === "svg" || tag === "path" ? document.createElementNS("http://www.w3.org/2000/svg", tag) : document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === "class") el.className = v;
      else if (k === "style" && typeof v === "object") Object.assign(el.style, v);
      else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v);
      else if (k === "text") el.textContent = v;
      else if (k in el && typeof v !== "string") el[k] = v;
      else el.setAttribute(k, v === true ? "" : v);
    }
  }
  el.append(...toNodes(kids));
  return el;
}

/** A part rebuilt when JSON(keyFn()) changes; otherwise its own updaters run. */
export function dyn(keyFn, render, wrapper) {
  const el = wrapper ?? h("div", { style: "display:contents" });
  let key;
  let inner = null;
  bind(() => {
    const k = JSON.stringify(keyFn());
    if (k !== key) {
      key = k;
      inner?.dispose();
      inner = new Scope();
      el.replaceChildren(...toNodes([withScope(inner, render)]));
    } else {
      inner.run();
    }
  });
  onCleanup(() => inner?.dispose());
  return el;
}

/** Text node kept in sync with fn(). */
export function text(fn) {
  const node = document.createTextNode("");
  bind(() => { const v = String(fn() ?? ""); if (node.data !== v) node.data = v; });
  return node;
}

// MARK: - Controls

/** kind: primary | secondary | ghost | danger; opts: {icon, height, full, disabled: fn, title} */
export function button(label, onclick, kind = "secondary", opts = {}) {
  const el = h("button", {
    class: `btn ${kind}${opts.full ? " full" : ""}${opts.height ? ` h${opts.height}` : ""}${!label ? " icon" : ""}`,
    type: "button", title: opts.title, "aria-label": opts.title, onclick,
  }, opts.icon ? icon(opts.icon) : null, label);
  if (opts.disabled) bind(() => { el.disabled = !!opts.disabled(); });
  return el;
}

export function iconButton(name, title, onclick, kind = "ghost") {
  return button("", onclick, kind, { icon: name, title });
}

export function sectionLabel(label) {
  return h("div", { class: "section-label" }, label);
}

export function card(...kids) {
  return h("div", { class: "card" }, ...kids);
}

/** options: [[label, value], …] or a function returning them. */
export function segmented(options, get, set, expand = false) {
  const el = h("div", { class: `seg${expand ? " expand" : ""}`, role: "radiogroup" });
  const build = () => {
    const list = typeof options === "function" ? options() : options;
    el.replaceChildren(...list.map(([label, value]) => {
      const b = h("button", { type: "button", role: "radio", onclick: () => set(value) }, label);
      b._value = value;
      return b;
    }));
  };
  build();
  bind(() => {
    const v = JSON.stringify(get());
    for (const b of el.children) {
      const on = JSON.stringify(b._value) === v;
      b.classList.toggle("selected", on);
      b.setAttribute("aria-checked", on);
    }
  });
  return el;
}

export function toggle(get, set, label) {
  const input = h("input", { type: "checkbox", class: "switch", role: "switch", "aria-label": label,
    onchange: () => set(input.checked) });
  bind(() => { input.checked = !!get(); });
  return input;
}

export function switchRow(title, detail, get, set) {
  return h("label", { class: "switch-row" },
    h("div", { class: "label" }, h("div", null, title),
      detail == null ? null : h("div", { class: "small" }, typeof detail === "function" ? text(detail) : detail)),
    toggle(get, set, title));
}

/** Slider with its value on the right. */
export function slider({ title, get, set, min, max, step = "any", format = (v) => v.toFixed(2) }) {
  let dragging = false;
  const input = h("input", { type: "range", min, max, step, "aria-label": title });
  const value = h("span", { class: "value mono" });
  const paint = (v) => {
    value.textContent = format(v);
    input.style.setProperty("--fill", `${((v - min) / (max - min)) * 100}%`);
  };
  input.addEventListener("pointerdown", () => { dragging = true; });
  window.addEventListener("pointerup", () => { dragging = false; });
  input.addEventListener("input", () => { const v = Number(input.value); paint(v); set(v); });
  bind(() => {
    const v = Number(get());
    if (!dragging) input.value = v;
    paint(dragging ? Number(input.value) : v);
  });
  return h("div", { class: "slider-row" }, h("div", { class: "head" }, h("span", null, title), value), input);
}

/** Integer stepper: editable number with − / + buttons. */
export function stepper({ title, get, set, min, max }) {
  const lo = () => (typeof min === "function" ? min() : min);
  const hi = () => (typeof max === "function" ? max() : max);
  const clamp = (v) => Math.min(hi(), Math.max(lo(), Math.round(v)));
  const input = h("input", { type: "number", "aria-label": title });
  const commit = () => {
    const v = Number(input.value);
    if (Number.isFinite(v) && input.value !== "") set(clamp(v));
    input.value = get();
  };
  input.addEventListener("change", commit);
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter") input.blur();
    else if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      e.preventDefault();
      set(clamp(Number(get()) + (e.key === "ArrowUp" ? 1 : -1)));
    }
  });
  const stepBy = (d) => () => set(clamp(Number(get()) + d));
  const minus = h("button", { type: "button", "aria-label": "−", onclick: stepBy(-1) }, icon("minus"));
  const plus = h("button", { type: "button", "aria-label": "+", onclick: stepBy(1) }, icon("plus"));
  bind(() => {
    if (document.activeElement !== input) input.value = get();
    minus.disabled = get() <= lo();
    plus.disabled = get() >= hi();
  });
  return h("div", { class: "stepper-row" }, h("span", { class: "title" }, title),
    h("div", { class: "stepper" }, input, minus, plus));
}

/** <select>; options: fn → [[label, value], …] (values compared as JSON). */
export function select(options, get, set, opts = {}) {
  const el = h("select", { class: "field", "aria-label": opts.label, style: opts.width ? { width: `${opts.width}px` } : null });
  let values = [];
  let signature;
  el.addEventListener("change", () => set(values[el.selectedIndex]));
  bind(() => {
    const list = options();
    const sig = JSON.stringify(list);
    if (sig !== signature) {
      signature = sig;
      values = list.map((o) => o[1]);
      el.replaceChildren(...list.map(([label]) => h("option", null, label)));
    }
    const v = JSON.stringify(get());
    const i = values.findIndex((x) => JSON.stringify(x) === v);
    if (i >= 0 && el.selectedIndex !== i) el.selectedIndex = i;
  });
  return el;
}

export function pickerRow(title, control) {
  return h("div", { class: "picker-row" }, h("span", { class: "title" }, title), control);
}

/** Color well; fires while dragging in the picker. */
export function colorInput(get, set, title) {
  const input = h("input", { type: "color", title, "aria-label": title });
  input.addEventListener("input", () => set(input.value.toUpperCase()));
  bind(() => {
    const v = String(get() || "#000000").toLowerCase();
    if (document.activeElement !== input && input.value !== v) input.value = v;
  });
  return input;
}

/** Text field committing on Enter/blur (or on every keystroke with live: true). */
export function textField({ get, set, placeholder, width, live = false, validate }) {
  const input = h("input", { class: "field", placeholder, spellcheck: false, style: width ? { width: `${width}px` } : null });
  const commit = () => {
    const v = input.value;
    if (validate && !validate(v)) { input.value = get(); return; }
    if (v !== get()) set(v);
  };
  if (live) input.addEventListener("input", commit);
  input.addEventListener("change", commit);
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") input.blur(); });
  bind(() => { if (document.activeElement !== input) input.value = get() ?? ""; });
  return input;
}

/** Pane scaffolding: title, subtitle and content. */
export function pane(title, subtitle, ...kids) {
  return h("div", { class: "pane" },
    h("header", null, h("h1", null, title), subtitle ? h("p", null, subtitle) : null),
    ...kids);
}

export function banner(iconName, message, action, kind = "") {
  return h("div", { class: `banner ${kind}` }, icon(iconName),
    h("div", { class: "text" }, message),
    action ? button(action[0], action[1], "secondary") : null);
}

/** Canvas sized to its box (device pixels); draw(ctx, w, h) on resize and on demand. */
export function autoCanvas(draw, cls = "fill") {
  const canvas = h("canvas", { class: cls });
  let w = 0, hgt = 0, queued = false;
  const paint = () => {
    queued = false;
    if (!w || !hgt) return;
    const ctx = canvas.getContext("2d");
    ctx.setTransform(devicePixelRatio, 0, 0, devicePixelRatio, 0, 0);
    ctx.clearRect(0, 0, w, hgt);
    draw(ctx, w, hgt);
  };
  const ro = new ResizeObserver(([entry]) => {
    const box = entry.contentRect;
    w = box.width; hgt = box.height;
    canvas.width = Math.max(1, Math.round(w * devicePixelRatio));
    canvas.height = Math.max(1, Math.round(hgt * devicePixelRatio));
    paint();
  });
  ro.observe(canvas);
  onCleanup(() => ro.disconnect());
  canvas.redraw = () => { if (!queued) { queued = true; requestAnimationFrame(paint); } };
  canvas.redrawNow = paint;
  return canvas;
}

export async function copyText(value) {
  try {
    await navigator.clipboard.writeText(value);
  } catch {
    const area = h("textarea", { style: "position:fixed;opacity:0" });
    area.value = value;
    document.body.append(area);
    area.select();
    document.execCommand("copy");
    area.remove();
  }
}

/** Modal dialog; returns close(). build(close) gives the content. */
export function openDialog(build, { width = 620, onClose } = {}) {
  const scope = new Scope();
  let closed = false;
  const backdrop = h("div", { class: "backdrop" });
  const close = () => {
    if (closed) return;
    closed = true;
    document.removeEventListener("keydown", onKey, true);
    scope.dispose();
    backdrop.remove();
    dialogs.delete(scope);
    onClose?.();
  };
  const onKey = (e) => { if (e.key === "Escape") { e.preventDefault(); close(); } };
  const dialog = h("div", { class: "dialog", role: "dialog", "aria-modal": "true", style: { width: `${width}px` } });
  dialog.append(...toNodes([withScope(scope, () => build(close))]));
  backdrop.append(dialog);
  document.body.append(backdrop);
  document.addEventListener("keydown", onKey, true);
  dialogs.add(scope);
  return close;
}

/** Open dialogs' scopes, refreshed with the rest of the UI. */
export const dialogs = new Set();
