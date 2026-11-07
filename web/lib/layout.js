// LED layout → zones (same maths as LEDLayout.swift / LEDLayout.cs) and the strip preview drawing.

export const CORNERS = ["bottomLeft", "topLeft", "topRight", "bottomRight"];
export const CORNER_TITLES = {
  bottomLeft: "Abajo izquierda", topLeft: "Arriba izquierda", topRight: "Arriba derecha", bottomRight: "Abajo derecha",
};

export function ledCount(l) {
  return Math.max(0, l.top + l.right + l.bottom + l.left - Math.min(l.bottomGap, l.bottom));
}

const mod = (a, n) => ((a % n) + n) % n;

/** Zones in strip order: {rect: {x, y, w, h}, pos: {x, y}, side} in 0…1 screen coordinates. */
export function zones(l) {
  const ring = [];
  const d = Math.max(0.01, Math.min(l.depth ?? 0.1, 0.5));
  const ov = Math.max(0, l.overlap ?? 0);
  const span = (i, n) => {
    const w = 1 / n;
    const a = i * w - (w * ov) / 2;
    return [Math.max(0, a), Math.min(1, a + w * (1 + ov))];
  };
  const top = Math.max(l.top, 0), right = Math.max(l.right, 0), bottom = Math.max(l.bottom, 0), left = Math.max(l.left, 0);
  for (let i = 0; i < top; i++) {
    const [a, b] = span(i, top);
    ring.push({ rect: { x: a, y: 0, w: b - a, h: d }, pos: { x: (i + 0.5) / top, y: 0 }, side: "top" });
  }
  for (let i = 0; i < right; i++) {
    const [a, b] = span(i, right);
    ring.push({ rect: { x: 1 - d, y: a, w: d, h: b - a }, pos: { x: 1, y: (i + 0.5) / right }, side: "right" });
  }
  const gap = Math.min(Math.max(l.bottomGap, 0), bottom);
  const gapStart = Math.floor((bottom - gap) / 2);
  for (let k = 0; k < bottom; k++) {
    const i = bottom - 1 - k;
    if (i >= gapStart && i < gapStart + gap) continue;
    const [a, b] = span(i, bottom);
    ring.push({ rect: { x: a, y: 1 - d, w: b - a, h: d }, pos: { x: (i + 0.5) / bottom, y: 1 }, side: "bottom" });
  }
  for (let k = 0; k < left; k++) {
    const i = left - 1 - k;
    const [a, b] = span(i, left);
    ring.push({ rect: { x: 0, y: a, w: d, h: b - a }, pos: { x: 0, y: (i + 0.5) / left }, side: "left" });
  }
  const n = ring.length;
  if (!n) return [];
  const bottomCount = bottom - gap;
  const corner = { topLeft: 0, topRight: top, bottomRight: top + right, bottomLeft: top + right + bottomCount }[l.start] ?? 0;
  let ordered;
  if (l.clockwise) {
    const c = corner % n;
    ordered = ring.slice(c).concat(ring.slice(0, c));
  } else {
    ordered = ring.map((_, k) => ring[mod(corner - 1 - k, n)]);
  }
  if (l.offset) {
    const s = mod(l.offset, n);
    ordered = ordered.slice(s).concat(ordered.slice(0, s));
  }
  return ordered;
}

/** Strip index of the LED at each screen corner (what the layout wizard asks to mark). */
export function cornerIndices(l) {
  const z = zones(l);
  const first = (side, key) => {
    let best = null;
    z.forEach((zone, i) => { if (zone.side === side && (best === null || key(zone) < key(z[best]))) best = i; });
    return best;
  };
  if (l.clockwise) {
    return {
      topLeft: first("top", (q) => q.pos.x), topRight: first("right", (q) => q.pos.y),
      bottomRight: first("bottom", (q) => -q.pos.x), bottomLeft: first("left", (q) => -q.pos.y),
    };
  }
  return {
    topLeft: first("left", (q) => q.pos.y), bottomLeft: first("bottom", (q) => q.pos.x),
    bottomRight: first("right", (q) => -q.pos.y), topRight: first("top", (q) => -q.pos.x),
  };
}

// MARK: - Drawing

const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const rgba = (c, a) => `rgba(${Math.round(c[0] * 255)},${Math.round(c[1] * 255)},${Math.round(c[2] * 255)},${a})`;

function point(zone, s, outset) {
  const p = { x: s.x + zone.pos.x * s.w, y: s.y + zone.pos.y * s.h };
  if (zone.side === "top") p.y -= outset;
  else if (zone.side === "bottom") p.y += outset;
  else if (zone.side === "left") p.x -= outset;
  else p.x += outset;
  return p;
}

/** The screen with its LEDs around it (StripPreview in the Mac app). colors: [[r,g,b] 0…1]. */
export function drawStrip(ctx, w, h, zoneList, colors, { showZones = false, showNumbers = false } = {}) {
  const inset = 18;
  const s = { x: inset, y: inset, w: w - inset * 2, h: h - inset * 2 };
  if (s.w <= 0 || s.h <= 0) return;
  const black = [0, 0, 0];
  // Glow behind the screen.
  zoneList.forEach((zone, i) => {
    const c = colors[i] ?? black;
    if (Math.max(...c) < 0.02) return;
    const p = point(zone, s, 10);
    const g = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, 26);
    g.addColorStop(0, rgba(c, 0.55));
    g.addColorStop(1, rgba(c, 0));
    ctx.fillStyle = g;
    ctx.fillRect(p.x - 26, p.y - 26, 52, 52);
  });
  ctx.beginPath();
  ctx.roundRect(s.x, s.y, s.w, s.h, 6);
  ctx.fillStyle = css("--screen");
  ctx.fill();
  ctx.strokeStyle = css("--line-strong");
  ctx.lineWidth = 1;
  ctx.stroke();
  if (showZones) {
    ctx.strokeStyle = css("--accent");
    ctx.globalAlpha = 0.25;
    ctx.lineWidth = 0.5;
    for (const z of zoneList) ctx.strokeRect(s.x + z.rect.x * s.w, s.y + z.rect.y * s.h, z.rect.w * s.w, z.rect.h * s.h);
    ctx.globalAlpha = 1;
  }
  const off = css("--led-off");
  const ok = css("--ok");
  const text3 = css("--text3");
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  zoneList.forEach((zone, i) => {
    const c = colors[i] ?? black;
    const p = point(zone, s, 9);
    const size = i === 0 && showNumbers ? 9 : 6;
    ctx.beginPath();
    ctx.arc(p.x, p.y, size / 2, 0, Math.PI * 2);
    ctx.fillStyle = Math.max(...c) < 0.02 ? off : rgba(c, 1);
    ctx.fill();
    if (showNumbers && (i === 0 || (i + 1) % 10 === 0 || i === zoneList.length - 1)) {
      const q = point(zone, s, -8);
      ctx.font = `${i === 0 ? "bold " : ""}9px system-ui, sans-serif`;
      ctx.fillStyle = i === 0 ? ok : text3;
      ctx.fillText(String(i + 1), q.x, q.y);
    }
  });
}
