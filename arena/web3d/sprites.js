// Particle sprites, computed pixel by pixel and shared by both renderers.
//
// - Not drawn with canvas gradients: Safari dithers those, and a sprite stretched over a few hundred screen pixels
//   showed the dither as grain.
// - Ramps are linear between stops and falloffs are single smooth curves. Easing in and out at every stop flattens the
//   brightness there, and the eye sees rings (Mach bands).
// - Smoke and flame are noise, not discs: smoke is fractal noise in a soft round mask (four variants, an atlas), and
//   the flame is a flipbook of a tongue that rises, tears and burns out over a particle's life.
//
// Pixels are in canvas order (row 0 at the top), RGBA, straight (not premultiplied) alpha.

const clamp01 = (t) => (t < 0 ? 0 : t > 1 ? 1 : t);
const smooth = (t) => { t = clamp01(t); return t * t * (3 - 2 * t); };

// ---------------------------------------------------------------- value noise
function hash3(x, y, z, seed) {
  let h = (x * 374761393 + y * 668265263 + z * 2147483647 + seed * 144665) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177); h ^= h >>> 16;
  return (h >>> 0) / 4294967295;
}
function noise3(x, y, z, seed) {
  const xi = Math.floor(x), yi = Math.floor(y), zi = Math.floor(z), xf = x - xi, yf = y - yi, zf = z - zi;
  const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf), w = zf * zf * (3 - 2 * zf);
  const l = (a, b, t) => a + (b - a) * t, h = (dx, dy, dz) => hash3(xi + dx, yi + dy, zi + dz, seed);
  return l(l(l(h(0, 0, 0), h(1, 0, 0), u), l(h(0, 1, 0), h(1, 1, 0), u), v), l(l(h(0, 0, 1), h(1, 0, 1), u), l(h(0, 1, 1), h(1, 1, 1), u), v), w);
}
/** Fractal noise, 0..1 (about 0.5 on average). */
function fbm(x, y, z, seed, octaves = 5) {
  let sum = 0, amp = 0.5, norm = 0;
  for (let i = 0; i < octaves; i++) { sum += amp * noise3(x, y, z, seed + i * 17); norm += amp; x *= 2.03; y *= 2.03; z *= 2.03; amp *= 0.5; }
  return sum / norm;
}

/** Linear colour stops [t, r, g, b, a] (0..1). */
function ramp(stops, t) {
  if (t <= stops[0][0]) return stops[0].slice(1);
  for (let i = 1; i < stops.length; i++) if (t <= stops[i][0]) {
    const [t0, ...a] = stops[i - 1], [t1, ...b] = stops[i], k = (t - t0) / (t1 - t0);
    return a.map((v, j) => v + (b[j] - v) * k);
  }
  return stops[stops.length - 1].slice(1);
}

/** An atlas of cols × rows tiles of `tile` pixels; paint(tileIndex, u, v) gives [r, g, b, a] with u, v in 0..1
 * across the tile and v measured from the tile's top. */
function atlas(cols, rows, tile, paint) {
  const w = cols * tile, h = rows * tile, px = new Uint8Array(w * h * 4);
  for (let ty = 0; ty < rows; ty++) for (let tx = 0; tx < cols; tx++) {
    const i = ty * cols + tx;
    for (let y = 0; y < tile; y++) for (let x = 0; x < tile; x++) {
      const c = paint(i, (x + 0.5) / tile, (y + 0.5) / tile), o = ((ty * tile + y) * w + tx * tile + x) * 4;
      px[o] = clamp01(c[0]) * 255 + 0.5; px[o + 1] = clamp01(c[1]) * 255 + 0.5; px[o + 2] = clamp01(c[2]) * 255 + 0.5; px[o + 3] = clamp01(c[3]) * 255 + 0.5;
    }
  }
  return { px, width: w, height: h, cols, rows };
}

// ---------------------------------------------------------------- the sprites

/** A soft round glow (sparks, flashes, muzzle light): one gaussian, nothing to band. */
export const dot = () => atlas(1, 1, 128, (_i, u, v) => {
  const d2 = ((u - 0.5) ** 2 + (v - 0.5) ** 2) / 0.25;
  return [1, 1, 1, d2 >= 1 ? 0 : Math.exp(-d2 * 5) * (1 - d2)];
});

/** Smoke and steam: billowy fractal noise in a round, soft mask, lit a little from above so it reads as volume.
 * Four variants (2 × 2); the particle picks one. */
export const smoke = (tile = 192) => atlas(2, 2, tile, (i, u, v) => {
  const x = u - 0.5, y = v - 0.5, d = Math.sqrt(x * x + y * y) / 0.5;
  if (d >= 1) return [1, 1, 1, 0];
  const n = fbm(u * 2.6 + i * 7.1, v * 2.6, i * 3.3, 11, 4);
  const body = (1 - d * d) * (1 - d * d);                 // a round falloff with no corner
  const a = smooth((n * 1.25 + body * 0.85 - 0.95) / 0.45) * body;
  const lit = 0.72 + 0.28 * clamp01((fbm(u * 2.6 + i * 7.1, v * 2.6 - 0.06, i * 3.3, 11, 4) - n) * 8 + 0.5) - y * 0.25; // brighter up top
  return [lit, lit, lit, a];
});

/** A flame tongue as a flipbook (4 × 4, played once over a particle's life): it rises from a broad hot base, tears into
 * licks at the top, and burns out. White-hot core to orange to a deep red edge; the particle colour tints it. */
export const flame = (tile = 96) => atlas(4, 4, tile, (f, u, v) => {
  const t = f / 15, up = 1 - v;                            // up: 0 at the tile's bottom, 1 at its top
  const sway = (fbm(up * 2.2 - t * 2.5, t * 3, 0, 23, 3) - 0.5) * 0.45 * up;
  const x = (u - 0.5 - sway) * 2;                          // -1..1 across
  const width = 0.62 * Math.pow(clamp01(1 - up), 0.55) * (1 - t * 0.35);
  const tear = fbm(x * 2.5, up * 3.2 - t * 4.5, t * 2, 31, 4);
  let heat = (1 - Math.abs(x) / Math.max(width, 1e-3)) * (1.05 - t * 0.55) - (tear - 0.42) * (0.5 + up * 1.4) - up * 0.25;
  heat *= smooth(up / 0.12) * 0.5 + 0.5;                   // a soft foot, not a flat cut
  const c = ramp([[0, 0.7, 0.1, 0, 0], [0.12, 0.9, 0.22, 0.02, 0.55], [0.35, 1, 0.5, 0.1, 0.9], [0.65, 1, 0.82, 0.4, 1], [1, 1, 0.98, 0.85, 1]], clamp01(heat));
  const edge = Math.min(smooth(u / 0.08), smooth((1 - u) / 0.08), smooth(v / 0.06), smooth(up / 0.14)); // up: the foot fades in too
  return [c[0], c[1], c[2], c[3] * edge];
});

/** One tile of an atlas as its own texture (for renderers without flipbooks). */
export function tileOf(a, i) {
  const tw = a.width / a.cols, th = a.height / a.rows, px = new Uint8Array(tw * th * 4), cx = (i % a.cols) * tw, cy = Math.floor(i / a.cols) * th;
  for (let y = 0; y < th; y++) px.set(a.px.subarray(((cy + y) * a.width + cx) * 4, ((cy + y) * a.width + cx + tw) * 4), y * tw * 4);
  return { px, width: tw, height: th, cols: 1, rows: 1 };
}
