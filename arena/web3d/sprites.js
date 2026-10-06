// Particle sprites, computed pixel by pixel and shared by both renderers.
//
// Not drawn with canvas radial gradients: Safari dithers those, and once a 128-pixel sprite is stretched over a
// few hundred screen pixels the dither shows as grain in every puff of smoke. Smooth falloffs, faded to nothing
// well inside the square, so no sprite ever shows its edge.

const SIZE = 256;
const smooth = (t) => (t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t));

/** Colour stops [t, r, g, b, a] (0..1) at distance t from the centre, as a fraction of the radius. */
function ramp(stops, t) {
  if (t <= stops[0][0]) return stops[0].slice(1);
  for (let i = 1; i < stops.length; i++) if (t <= stops[i][0]) {
    const [t0, ...a] = stops[i - 1], [t1, ...b] = stops[i], k = smooth((t - t0) / (t1 - t0));
    return a.map((v, j) => v + (b[j] - v) * k);
  }
  return stops[stops.length - 1].slice(1);
}

function radial(cx, cy, r, stops) {
  const n = SIZE, px = new Uint8Array(n * n * 4);
  for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
    const t = Math.hypot((x + 0.5) / n - cx, (y + 0.5) / n - cy) / r, c = ramp(stops, t), o = (y * n + x) * 4;
    px[o] = c[0] * 255; px[o + 1] = c[1] * 255; px[o + 2] = c[2] * 255; px[o + 3] = c[3] * 255;
  }
  return px;
}

/** A soft round glow (sparks, flashes). */
export const dot = () => radial(0.5, 0.5, 0.48, [[0, 1, 1, 1, 1], [0.35, 1, 1, 1, 0.55], [1, 1, 1, 1, 0]]);

/** A flame: hot white-yellow core low down, orange to red at the rim. */
export const flame = () => radial(0.5, 0.58, 0.42, [[0, 1, 1, 0.9, 1], [0.3, 1, 0.78, 0.35, 0.9], [0.7, 1, 0.35, 0.08, 0.35], [1, 1, 0.16, 0, 0]]);

/** A lumpy smoke puff: overlapping soft blobs, white (the particle colour tints it). Seeded, so it's the same
 * puff in every browser. */
export function puff(seed = 7) {
  let s = seed; const rnd = () => ((s = (s * 16807) % 2147483647) / 2147483647);
  const blobs = Array.from({ length: 9 }, () => { const a = rnd() * 6.28, d = rnd() * 0.16; return { x: 0.5 + Math.cos(a) * d, y: 0.5 + Math.sin(a) * d, r: 0.16 + rnd() * 0.14 }; });
  const n = SIZE, px = new Uint8Array(n * n * 4);
  for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
    const u = (x + 0.5) / n, v = (y + 0.5) / n;
    let clear = 1; // what the blobs let through, composited one over another
    for (const b of blobs) clear *= 1 - 0.55 * smooth(1 - Math.hypot(u - b.x, v - b.y) / b.r);
    const edge = smooth((0.5 - Math.hypot(u - 0.5, v - 0.5)) / 0.08); // nothing reaches the square's edge
    const o = (y * n + x) * 4; px[o] = px[o + 1] = px[o + 2] = 255; px[o + 3] = (1 - clear) * edge * 255;
  }
  return px;
}

export { SIZE };
