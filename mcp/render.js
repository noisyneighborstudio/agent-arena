// A top-down picture of a player's own fogged view, drawn from the game's ASCII map. Headless rooms have no
// renderer, so this is what `look` returns there: north up, one square per tile, no dependencies (PNG via zlib).
import zlib from "node:zlib";

const STRUCTURES = "COPRBFTEDSLNZKUA";
const COLORS = {
  ".": [120, 104, 78], "#": [72, 70, 66], "~": [52, 92, 140], " ": [18, 20, 26],
  $: [196, 92, 52], "%": [214, 140, 60], "*": [96, 210, 230], "!": [130, 230, 90],
};
const MINE = [80, 220, 120], ENEMY = [235, 64, 64];

/** Parse the ASCII map into rows of { y, cells } (north first, as printed). */
function parse(ascii) {
  const rows = [];
  for (const line of ascii.split("\n")) {
    const m = line.match(/^\s*(\d+) (.*)$/);
    if (m && !/^ {4}[\d ]+$/.test(line)) rows.push({ y: Number(m[1]), cells: m[2] });
  }
  return rows;
}

/** Render a PNG around (cx, cy), or around your own base when no point is given. */
export function renderView(ascii, cx, cy, radius = 32, px = 10) {
  const rows = parse(ascii);
  if (!rows.length) throw new Error("no map to draw");
  const H = rows.length, W = Math.max(...rows.map((r) => r.cells.length));
  const at = (x, y) => { const r = rows[H - 1 - y]; return r && x >= 0 && x < r.cells.length ? r.cells[x] : " "; };
  if (!Number.isFinite(cx) || !Number.isFinite(cy)) {
    // Centre on your command centre, else on anything of yours.
    let sx = 0, sy = 0, n = 0;
    for (const want of ["C", STRUCTURES + "ivm"]) {
      for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) if (want.includes(at(x, y))) { sx += x; sy += y; n++; }
      if (n) break;
    }
    cx = n ? Math.round(sx / n) : W / 2; cy = n ? Math.round(sy / n) : H / 2;
  }
  const x0 = Math.max(0, Math.min(W - 1, Math.round(cx - radius))), x1 = Math.min(W - 1, Math.round(cx + radius));
  const y0 = Math.max(0, Math.min(H - 1, Math.round(cy - radius))), y1 = Math.min(H - 1, Math.round(cy + radius));
  const w = (x1 - x0 + 1) * px, h = (y1 - y0 + 1) * px;
  const img = Buffer.alloc(w * h * 3);
  const fill = (tx, ty, c, inset = 0) => {
    const ox = (tx - x0) * px, oy = (y1 - ty) * px; // north up
    for (let y = oy + inset; y < oy + px - inset; y++) for (let x = ox + inset; x < ox + px - inset; x++) {
      const i = (y * w + x) * 3; img[i] = c[0]; img[i + 1] = c[1]; img[i + 2] = c[2];
    }
  };
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
    const ch = at(x, y);
    const ground = COLORS[ch] ?? COLORS["."];
    fill(x, y, ground);
    if (COLORS[ch]) continue;
    // 'a' is both your aircraft and an enemy airfield; an airfield is a block, an aircraft a single tile.
    const enemyAirfield = ch === "a" && [[1, 0], [-1, 0], [0, 1], [0, -1]].some(([dx, dy]) => at(x + dx, y + dy) === "a");
    if (STRUCTURES.includes(ch)) fill(x, y, MINE, 0);
    else if (enemyAirfield || STRUCTURES.toLowerCase().includes(ch) && !"ivma".includes(ch)) fill(x, y, ENEMY, 0);
    else if ("ivma^".includes(ch)) fill(x, y, MINE, Math.floor(px / 4));
    else if ("xXMW&".includes(ch)) fill(x, y, ENEMY, Math.floor(px / 4));
  }
  return { png: encodePng(w, h, img), x0, x1, y0, y1 };
}

function encodePng(w, h, rgb) {
  const raw = Buffer.alloc((w * 3 + 1) * h);
  for (let y = 0; y < h; y++) rgb.copy(raw, y * (w * 3 + 1) + 1, y * w * 3, (y + 1) * w * 3);
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type), data]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(td) >>> 0);
    return Buffer.concat([len, td, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", zlib.deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}
