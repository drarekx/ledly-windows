// Effect previews for the Effects pane: a port of EffectRenderer / EqualizerRenderer (Mac app),
// run only for the hovered and the selected card. Colors are [r, g, b] floats.

export const EFFECT_ICONS = {
  rainbowSwirl: "rays", doubleSwirl: "cycle", rainbowWaves: "wave", rainbowMood: "rainbow", breath: "wind",
  candle: "candle", fire: "flame", knightRider: "car", police: "beacon", strobe: "bolt", sparks: "sparkle",
  moodBlobs: "blobs", seaWaves: "wave", plasma: "plasma", snake: "snake", colorWipe: "brush", christmas: "tree",
  lightClock: "clock", aurora: "moon", meteor: "meteor", musicPulse: "music", musicSpectrum: "chart",
  musicWaves: "radio", musicMeter: "gauge", atomicSwirl: "atom", colorWaves: "wave", moodBlue: "blobs",
  moodGreen: "blobs", moodRed: "blobs", moodCold: "blobs", moodWarm: "blobs", policeSolid: "beacon",
  cinemaBrighten: "sun", cinemaDim: "sunDim", notify: "bell", shutdown: "power",
};

/** Effects that use the main color (marked with ● in the grid). */
export const USES_COLOR = new Set(["breath", "knightRider", "strobe", "sparks", "snake", "colorWipe", "meteor", "colorWaves", "notify"]);
export const MUSIC = new Set(["musicPulse", "musicSpectrum", "musicWaves", "musicMeter"]);

const BLACK = [0, 0, 0];
const WHITE = [1, 1, 1];
const TAU = Math.PI * 2;

export function hsv(hue, s = 1, v = 1) {
  const h = (hue - Math.floor(hue)) * 6;
  const i = Math.floor(h) % 6, f = h - Math.floor(h);
  const p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
  return [[v, t, p], [q, v, p], [p, v, t], [p, q, v], [t, p, v], [v, p, q]][i];
}
const mul = (c, k) => [c[0] * k, c[1] * k, c[2] * k];
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const mix = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
const clamp01 = (c) => c.map((x) => Math.min(Math.max(x, 0), 1));
const rand = (a, b) => a + Math.random() * (b - a);

export function hexToRGB(hex) {
  const n = parseInt(String(hex || "#000000").slice(1), 16);
  return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}

const BANDS = 16;

/** Plausible fake audio levels (AudioLevels.demo). */
function demoAudio(time) {
  const beatPhase = (time * 2) % 1;
  const kick = Math.max(0, 1 - beatPhase * 3);
  const bands = [];
  for (let i = 0; i < BANDS; i++) {
    const x = i / BANDS;
    const wobble = 0.5 + 0.5 * Math.sin(time * (2 + i * 0.7) + i);
    bands.push(Math.min(1, (1 - x) * kick * 0.9 + wobble * 0.45 * (1 - x * 0.4)));
  }
  const level = 0.4 + 0.4 * kick;
  return {
    bands, bass: kick, level,
    leftBands: bands.map((b, i) => Math.min(1, b * (0.8 + 0.2 * Math.sin(time * 1.3 + i)))),
    rightBands: bands.map((b, i) => Math.min(1, b * (0.8 + 0.2 * Math.cos(time * 1.1 + i)))),
    leftLevel: level * (0.85 + 0.15 * Math.sin(time * 1.3)),
    rightLevel: level * (0.85 + 0.15 * Math.cos(time * 1.1)),
    lastBeat: time - beatPhase / 2,
  };
}

export class EffectRenderer {
  constructor(kind, zones) {
    this.kind = kind;
    this.zones = zones;
    this.angles = zones.map((z) => { const a = Math.atan2(z.pos.x - 0.5, 0.5 - z.pos.y) / TAU; return a < 0 ? a + 1 : a; });
    const n = zones.length;
    this.heat = new Array(n).fill(0);
    this.levels = new Array(n).fill(0);
    this.phases = zones.map(() => Math.random());
    this.lastTime = null;
    this.smoothBass = 0; this.smoothLevel = 0; this.peakHold = 0; this.seenBeat = -1;
    this.waves = []; this.wavesHueShift = 0; this.firstTime = null;
    this.eq = null;
  }

  render(time, speed, color, equalizer) {
    const n = this.zones.length;
    if (!n) return [];
    const dt = Math.min(Math.max(time - (this.lastTime ?? time), 0), 0.1);
    this.lastTime = time;
    const t = time * speed;
    const out = new Array(n).fill(BLACK);
    const { angles, zones } = this;
    const audio = MUSIC.has(this.kind) ? demoAudio(time) : null;
    if (audio) this.follow(audio, dt);

    switch (this.kind) {
      case "rainbowSwirl": for (let i = 0; i < n; i++) out[i] = hsv(angles[i] + t * 0.15); break;
      case "doubleSwirl":
        for (let i = 0; i < n; i++) {
          const a = hsv(angles[i] * 2 + t * 0.12), b = hsv(-angles[i] * 2 + t * 0.07 + 0.5);
          out[i] = mix(a, b, (Math.sin(angles[i] * 4 * Math.PI + t) + 1) / 2);
        }
        break;
      case "rainbowWaves":
        for (let i = 0; i < n; i++) {
          const dx = zones[i].pos.x - 0.5, dy = zones[i].pos.y;
          out[i] = hsv(Math.sqrt(dx * dx + dy * dy) * 0.8 - t * 0.2);
        }
        break;
      case "rainbowMood": out.fill(hsv(t * 0.03)); break;
      case "breath": { const s = (Math.sin(t * 1.3) + 1) / 2; out.fill(mul(color, 0.06 + 0.94 * s * s)); break; }
      case "candle": {
        const warm = [1, 0.42, 0.08];
        for (let i = 0; i < n; i++) {
          if (Math.random() < dt * 8 * speed) this.phases[i] = rand(0.35, 1);
          this.levels[i] += (this.phases[i] - this.levels[i]) * Math.min(1, dt * 12);
          out[i] = mul(warm, this.levels[i]);
        }
        break;
      }
      case "fire": {
        const heat = this.heat;
        const cooling = 0.9 * dt * speed * 6;
        for (let i = 0; i < n; i++) heat[i] = Math.max(0, heat[i] - rand(0, cooling));
        for (const row of this.fireRows()) {
          for (let k = row.length - 1; k >= 1; k--) {
            const below = k >= 2 ? (heat[row[k - 1]] + heat[row[k - 2]] * 0.6) / 1.6 : heat[row[k - 1]];
            heat[row[k]] += (below - heat[row[k]]) * Math.min(1, dt * 18 * speed);
          }
          if (row.length && Math.random() < dt * 20 * speed) heat[row[0]] = Math.min(1, heat[row[0]] + rand(0.5, 1));
        }
        for (let i = 0; i < n; i++) out[i] = heatColor(heat[i]);
        break;
      }
      case "knightRider": {
        const topIdx = this.indicesOn("top");
        const path = topIdx.length ? topIdx : [...Array(n).keys()];
        const m = path.length;
        const p = ((1 - Math.cos(t * 1.6)) / 2) * (m - 1);
        path.forEach((i, k) => { out[i] = mul(color, Math.max(0, 1 - Math.abs(k - p) / Math.max(2, m * 0.08))); });
        break;
      }
      case "police": {
        const red = [1, 0, 0], blue = [0, 0.1, 1];
        const phase = Math.floor(t * 4) % 4;
        for (let i = 0; i < n; i++) {
          const on = phase === 0 || phase === 2;
          if (zones[i].pos.x < 0.5) out[i] = on && phase === 0 ? red : phase === 1 ? mul(red, 0.15) : BLACK;
          else out[i] = on && phase === 2 ? blue : phase === 3 ? mul(blue, 0.15) : BLACK;
        }
        break;
      }
      case "strobe": out.fill((t * 8) % 1 < 0.2 ? color : BLACK); break;
      case "sparks":
        for (let i = 0; i < n; i++) {
          this.levels[i] = Math.max(0, this.levels[i] - dt * 3);
          if (Math.random() < dt * 0.6 * speed) this.levels[i] = 1;
          const g = this.levels[i] ** 2;
          out[i] = mul(mix(color, WHITE, g), g);
        }
        break;
      case "moodBlobs": this.blobs(out, t, 3, (b) => t * 0.01 + b * 0.33, 1); break;
      case "moodBlue": this.blobs(out, t, 5, (b) => 0.6 + 0.07 * Math.sin(t * 0.05 + b * 1.3), 0.9); break;
      case "moodGreen": this.blobs(out, t, 5, (b) => 0.33 + 0.07 * Math.sin(t * 0.05 + b * 1.3), 0.9); break;
      case "moodRed": this.blobs(out, t, 5, (b) => 0.99 + 0.04 * Math.sin(t * 0.05 + b * 1.3), 0.95); break;
      case "moodCold": this.blobs(out, t, 5, (b) => 0.58 + 0.12 * Math.sin(t * 0.05 + b * 1.3), 0.8); break;
      case "moodWarm": this.blobs(out, t, 5, (b) => 0.05 + 0.05 * Math.sin(t * 0.05 + b * 1.3), 0.95); break;
      case "seaWaves":
        for (let i = 0; i < n; i++) {
          const a = angles[i] * TAU;
          const w = (Math.sin(a * 3 + t * 1.2) + Math.sin(a * 5 - t * 0.7) * 0.5 + 1.5) / 3;
          out[i] = mul([0, 0.25 + 0.45 * w, 0.55 + 0.45 * w], 0.3 + 0.7 * w);
        }
        break;
      case "plasma":
        for (let i = 0; i < n; i++) {
          const x = zones[i].pos.x * 6, y = zones[i].pos.y * 6;
          const v = Math.sin(x + t) + Math.sin(y * 1.3 - t * 0.8) + Math.sin((x + y) * 0.7 + t * 0.6);
          out[i] = hsv(v / 6 + t * 0.05);
        }
        break;
      case "snake": {
        const length = Math.max(4, Math.floor(n / 8));
        const head = Math.floor(t * 30) % n;
        for (let k = 0; k < length; k++) out[(((head - k) % n) + n) % n] = mul(color, (1 - k / length) ** 2);
        break;
      }
      case "colorWipe": {
        const cycle = t * 0.25, progress = cycle - Math.floor(cycle);
        const even = Math.floor(cycle) % 2 === 0;
        const current = Math.max(...color) > 0 && even ? color : hsv(Math.floor(cycle) * 0.23);
        const previous = even ? hsv((Math.floor(cycle) - 1) * 0.23) : color;
        const edge = Math.floor(progress * n);
        for (let i = 0; i < n; i++) out[i] = i <= edge ? current : previous;
        break;
      }
      case "christmas": {
        const shift = Math.floor(t * 2);
        for (let i = 0; i < n; i++) {
          const k = (Math.floor(i / 3) + shift) % 3;
          out[i] = k === 0 ? [1, 0, 0] : k === 1 ? [0, 0.8, 0.1] : mul([1, 0.85, 0.6], 0.6);
        }
        break;
      }
      case "lightClock": {
        const now = new Date();
        const s = (now.getSeconds() + now.getMilliseconds() / 1000) / 60;
        const m = (now.getMinutes() + s) / 60;
        const hr = ((now.getHours() % 12) + m) / 12;
        for (let i = 0; i < n; i++) {
          const hand = (pos, width) => { let d = Math.abs(angles[i] - pos); d = Math.min(d, 1 - d); return Math.max(0, 1 - d / width); };
          out[i] = clamp01(add(add(add(mul([1, 0.1, 0.05], hand(hr, 0.05)), mul([0.1, 1, 0.2], hand(m, 0.03))),
            mul([0.15, 0.3, 1], hand(s, 0.02))), [0.03, 0.03, 0.03]));
        }
        break;
      }
      case "aurora":
        for (let i = 0; i < n; i++) {
          const a = angles[i] * TAU;
          const v = (Math.sin(a * 2 + t * 0.4) + Math.sin(a * 3.7 - t * 0.25) + 2) / 4;
          out[i] = hsv(0.33 + 0.25 * v + 0.05 * Math.sin(t * 0.1), 0.85, 0.15 + 0.85 * v * v);
        }
        break;
      case "meteor": {
        for (let i = 0; i < n; i++) if (Math.random() < 0.6) this.levels[i] *= Math.max(0, 1 - dt * 4);
        const head = Math.floor(t * 40) % n;
        for (let k = 0; k < 3; k++) this.levels[(head + k) % n] = 1;
        for (let i = 0; i < n; i++) out[i] = mul(mix(color, WHITE, this.levels[i] > 0.95 ? 0.6 : 0), this.levels[i]);
        break;
      }
      case "atomicSwirl":
        for (let i = 0; i < n; i++) {
          const a = (angles[i] - t * 0.04) * 3 * TAU;
          const v = Math.max(0, Math.cos(a)) ** 8;
          const tint = mix([0.55, 0.75, 1], WHITE, (Math.sin(t * 0.5 + angles[i] * 6) + 1) / 2);
          out[i] = add(mul(tint, v), [0, 0.01, 0.04]);
        }
        break;
      case "colorWaves":
        for (let i = 0; i < n; i++) {
          const v = (Math.sin((angles[i] * 3 - t * 0.3) * TAU) + 1) / 2;
          out[i] = mul(color, 0.12 + 0.88 * v * v);
        }
        break;
      case "policeSolid": {
        const swap = Math.floor(t * 2) % 2 === 0;
        for (let i = 0; i < n; i++) out[i] = (zones[i].pos.x < 0.5) === swap ? [1, 0, 0] : [0, 0.1, 1];
        break;
      }
      case "cinemaBrighten": case "cinemaDim": {
        // Loops in the preview so it keeps showing something.
        const p = Math.min(1, ((this.elapsed(time) * speed) % 7) / 5);
        const eased = p * p * (3 - 2 * p);
        out.fill(mul([1, 0.62, 0.3], this.kind === "cinemaBrighten" ? eased : 1 - eased));
        break;
      }
      case "notify": {
        const e = (this.elapsed(time) * speed) % 3;
        out.fill(e < 1.2 && e % 0.4 < 0.2 ? color : BLACK);
        break;
      }
      case "shutdown": {
        const e = (this.elapsed(time) * speed) % 4.5;
        const red = [1, 0, 0];
        for (let i = 0; i < n; i++) {
          if (e < 1.5) out[i] = i / n < e / 1.5 ? red : BLACK;
          else if (e < 2.5) out[i] = (e - 1.5) % 0.5 < 0.25 ? red : BLACK;
          else if (e < 3.5) out[i] = mul(red, 3.5 - e);
        }
        break;
      }
      case "musicPulse": {
        const hue = time * 0.02 * speed + this.wavesHueShift;
        const l = 0.05 + 0.95 * this.smoothBass * this.smoothBass;
        for (let i = 0; i < n; i++) out[i] = hsv(hue + angles[i] * 0.15, 1, l);
        break;
      }
      case "musicSpectrum":
        this.eq ??= new EqualizerRenderer(zones);
        return this.eq.render(audio, equalizer, time, dt, speed, color);
      case "musicWaves": {
        const travel = 0.9 * speed;
        this.waves = this.waves.filter((w) => (time - w.born) * travel <= 1.6);
        for (let i = 0; i < n; i++) {
          const dx = zones[i].pos.x - 0.5, dy = 1 - zones[i].pos.y;
          const d = Math.sqrt(dx * dx + dy * dy);
          let acc = BLACK;
          for (const w of this.waves) {
            const front = (time - w.born) * travel;
            acc = add(acc, mul(hsv(w.hue), Math.max(0, 1 - Math.abs(d - front) / 0.18) * Math.max(0, 1 - front / 1.6)));
          }
          out[i] = clamp01(add(acc, mul(hsv(this.wavesHueShift), 0.04 + 0.1 * this.smoothLevel)));
        }
        break;
      }
      case "musicMeter":
        for (let i = 0; i < n; i++) {
          const z = zones[i];
          const u = z.side === "top" ? 1 : z.side === "bottom" ? 0 : 1 - z.pos.y;
          const base = hsv(0.33 - 0.33 * u);
          if (u <= this.smoothLevel) out[i] = base;
          else if (Math.abs(u - this.peakHold) < 0.04) out[i] = mul(WHITE, 0.7);
          else out[i] = mul(base, 0.03);
        }
        break;
      default:
        out.fill(mul(color, 0.5));
    }
    return out;
  }

  elapsed(time) { this.firstTime ??= time; return time - this.firstTime; }

  blobs(out, t, count, hue, saturation) {
    for (let i = 0; i < out.length; i++) {
      let acc = BLACK;
      for (let b = 0; b < count; b++) {
        const center = (b / count + t * 0.02 * ((b % 3) + 1)) % 1;
        let d = Math.abs(this.angles[i] - center);
        d = Math.min(d, 1 - d);
        const w = Math.max(0, 1 - d * count * 1.3);
        acc = add(acc, mul(hsv(hue(b), saturation), w * w));
      }
      out[i] = clamp01(acc);
    }
  }

  follow(audio, dt) {
    const approach = (c, target, fall) => (target > c ? target : c + (target - c) * Math.min(1, dt / fall));
    this.smoothBass = approach(this.smoothBass, audio.bass, 0.25);
    this.smoothLevel = approach(this.smoothLevel, audio.level, 0.25);
    this.peakHold = this.smoothLevel > this.peakHold ? this.smoothLevel : Math.max(0, this.peakHold - dt * 0.4);
    if (audio.lastBeat > this.seenBeat + 0.05) {
      this.seenBeat = audio.lastBeat;
      this.wavesHueShift += 0.13;
      this.waves.push({ born: this.lastTime ?? 0, hue: this.wavesHueShift });
      if (this.waves.length > 8) this.waves.shift();
    }
  }

  indicesOn(side) {
    return this.zones.map((z, i) => [z, i]).filter(([z]) => z.side === side).sort((a, b) => a[0].pos.x - b[0].pos.x).map(([, i]) => i);
  }

  fireRows() {
    if (this.rows) return this.rows;
    const z = this.zones;
    const idx = [...z.keys()];
    const left = idx.filter((i) => z[i].side === "left").sort((a, b) => z[b].pos.y - z[a].pos.y);
    const right = idx.filter((i) => z[i].side === "right").sort((a, b) => z[b].pos.y - z[a].pos.y);
    const top = this.indicesOn("top");
    const half = Math.floor(top.length / 2);
    let rows = [left.concat(top.slice(0, half)), right.concat(top.slice(half).reverse())];
    rows = rows.concat(idx.filter((i) => z[i].side === "bottom").map((i) => [i]));
    this.rows = rows.filter((r) => r.length);
    return this.rows;
  }
}

function heatColor(h) {
  const x = Math.min(Math.max(h, 0), 1);
  if (x < 0.33) return [x * 3, 0, 0];
  if (x < 0.66) return [1, (x - 0.33) * 3 * 0.6, 0];
  return [1, 0.6 + (x - 0.66) * 1.2, (x - 0.66) * 1.5];
}

class EqualizerRenderer {
  constructor(zones) {
    this.zones = zones;
    this.perimeter = zones.map((z) => {
      const { x, y } = z.pos;
      const raw = z.side === "top" ? 16 * x : z.side === "right" ? 16 + 9 * y : z.side === "bottom" ? 25 + 16 * (1 - x) : 41 + 9 * (1 - y);
      const t = (raw - 33) / 50;
      return t - Math.floor(t);
    });
    this.bands = [0, 1, 2].map(() => new Array(BANDS).fill(0));
    this.bandPeaks = [0, 1, 2].map(() => new Array(BANDS).fill(0));
    this.levels = [0, 0, 0];
    this.levelPeaks = [0, 0, 0];
  }

  render(audio, options, time, dt, speed, color) {
    const o = { layout: "fromBottom", style: "spectrum", colors: "rainbow", sensitivity: 1, ...options };
    this.follow(audio, dt, o.sensitivity);
    return this.zones.map((_, i) => {
      const [p, channel] = this.axis(i, o.layout);
      return this.draw(p, channel, o, time, speed, color);
    });
  }

  axis(i, layout) {
    const t = this.perimeter[i];
    const fromBottom = t < 0.5 ? t * 2 : (1 - t) * 2;
    switch (layout) {
      case "fromTop": return [1 - fromBottom, 0];
      case "stereo": return [fromBottom, t < 0.5 ? 1 : 2];
      case "ring": return [t, 0];
      case "quad": { const q = (t % 0.5) * 2; return [q < 0.5 ? q * 2 : (1 - q) * 2, 0]; }
      default: return [fromBottom, 0];
    }
  }

  draw(p, channel, o, time, speed, color) {
    const b = this.bands[channel];
    const band = (x) => {
      const f = Math.min(Math.max(x, 0), 1) * (BANDS - 1);
      const lo = Math.floor(f), hi = Math.min(lo + 1, BANDS - 1);
      return b[lo] + (b[hi] - b[lo]) * (f - lo);
    };
    const paint = (pp, v) => this.paint(pp, v, o, time, speed, color);
    switch (o.style) {
      case "bars": {
        const count = 8;
        const k = Math.min(Math.floor(p * count), count - 1);
        const inBar = p * count - k;
        const v = band((k + 0.5) / count);
        const peak = this.bandPeaks[channel][Math.floor(((k + 0.5) / count) * (BANDS - 1))];
        if (inBar <= v) return paint((k + 0.5) / count, v);
        if (Math.abs(inBar - peak) < 0.12) return mul(WHITE, 0.6);
        return BLACK;
      }
      case "level": {
        const v = this.levels[channel];
        if (p <= v) return paint(p, p);
        if (Math.abs(p - this.levelPeaks[channel]) < 0.035) return mul(WHITE, 0.7);
        return mul(paint(p, p), 0.03);
      }
      case "flashes": {
        const v = band(p);
        const f = Math.max(0, (v - 0.55) / 0.45);
        return mul(mix(paint(p, v), WHITE, f * f * 0.5), f * f);
      }
      default: { const v = band(p); return mul(paint(p, v), v * v); }
    }
  }

  paint(p, v, o, time, speed, color) {
    switch (o.colors) {
      case "fixed": return color;
      case "heat": {
        const x = Math.min(Math.max(v, 0), 1);
        if (x < 0.25) return [0.1 * x * 4, 0, 0.6 * x * 4];
        if (x < 0.6) return [0.1 + (0.9 * (x - 0.25)) / 0.35, 0, 0.6 * (1 - (x - 0.25) / 0.35)];
        if (x < 0.9) return [1, (0.8 * (x - 0.6)) / 0.3, 0];
        return [1, 0.8 + (0.2 * (x - 0.9)) / 0.1, ((x - 0.9) / 0.1) * 0.7];
      }
      case "traffic": return hsv(0.33 - 0.33 * Math.min(Math.max(p, 0), 1));
      case "cycle": return hsv(p * 0.35 + time * 0.06 * speed);
      default: return hsv(p * 0.8);
    }
  }

  follow(audio, dt, gain) {
    const sources = [audio.bands, audio.leftBands, audio.rightBands];
    const sourceLevels = [audio.level, audio.leftLevel, audio.rightLevel];
    for (let c = 0; c < 3; c++) {
      for (let k = 0; k < BANDS; k++) {
        const target = Math.min(1, sources[c][k] * gain);
        const cur = this.bands[c][k];
        this.bands[c][k] = target > cur ? target : cur + (target - cur) * Math.min(1, dt / 0.15);
        this.bandPeaks[c][k] = this.bands[c][k] > this.bandPeaks[c][k] ? this.bands[c][k] : Math.max(0, this.bandPeaks[c][k] - dt * 0.5);
      }
      const target = Math.min(1, sourceLevels[c] * gain);
      this.levels[c] = target > this.levels[c] ? target : this.levels[c] + (target - this.levels[c]) * Math.min(1, dt / 0.25);
      this.levelPeaks[c] = this.levels[c] > this.levelPeaks[c] ? this.levels[c] : Math.max(0, this.levelPeaks[c] - dt * 0.4);
    }
  }
}
