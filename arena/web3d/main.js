// Pezz in the browser (spike): draws a live room in 3D from the game's view data and the same glTF models the Unity
// renderer uses. Look-only: pan, zoom, rotate, follow; nothing is sent to the game.
//
// Data: ./data/frame and ./data/map (the view endpoints, proxied by the sessions dashboard). Positions arrive a few
// times a second and are drawn a little in the past, interpolated, so motion is smooth at the display's frame rate.
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { toCreasedNormals } from "three/addons/utils/BufferGeometryUtils.js";

const Q = new URLSearchParams(location.search);
const DATA = (p) => `./data/${p}?${Q.has("host") ? `host=${Q.get("host")}&room=${Q.get("room")}` : `api=${Q.get("api") ?? 7957}`}&team=${Q.get("team") ?? 0}`;
const POLL_MS = 250, DELAY_MS = 350; // poll rate, and how far in the past positions are drawn (interpolation)

// ---------------------------------------------------------------- palette (PezPalette in Unity)
// Palette values are sRGB (Color32 in Unity): convert, or everything renders pale and washed out.
const C = (r, g, b) => new THREE.Color().setRGB(r / 255, g / 255, b / 255, THREE.SRGBColorSpace);
const TEAM = [C(46, 115, 255), C(242, 46, 31), C(51, 217, 77), C(255, 209, 38), C(142, 68, 217), C(58, 47, 158), C(15, 140, 138), C(142, 27, 74)];
const NEUTRAL = C(185, 174, 152);
const GROUND_A = C(125, 110, 85), GROUND_B = C(142, 127, 99), DIRT = C(106, 92, 70), WATER = C(58, 34, 24), ROCK = C(70, 56, 50);
const ORE = { i: C(138, 58, 36), c: C(200, 116, 47), x: C(143, 228, 255), u: C(182, 255, 59) };
// Units without their own model yet share one (as the 2D viewer's icons do).
const MODEL_AS = { long_range_drone: "gunship", reaper_drone: "stealth_bomber", recon_drone: "gunship", transport_chopper: "gunship", mammoth_tank: "heavy_tank",
  flak_track: "light_tank", apc: "light_tank", minelayer: "mining_truck", commando: "rifleman", sniper: "rifleman", engineer: "medic",
  construction_truck: "outpost_truck", derrick: "deep_mine" };
const AIR = new Set(["gunship", "stealth_bomber", "recon_drone", "long_range_drone", "reaper_drone", "transport_chopper"]);

// ---------------------------------------------------------------- renderer, scene, light
const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: "high-performance" });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.setSize(innerWidth, innerHeight);
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.toneMapping = THREE.NeutralToneMapping; // close to PezPost's light grade (exposure 1.06, contrast 1.04)
renderer.toneMappingExposure = 1.06;
renderer.outputColorSpace = THREE.SRGBColorSpace;
document.body.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x16120f);
// The game world in Unity's coordinates (left-handed, sim (x, y) at (x, 0, y)), mirrored into three's right-handed
// world. three flips the face winding for mirrored objects, so this costs nothing; models get a second mirror of
// their own (in upsert) so they aren't reversed.
const world = new THREE.Group();
world.scale.set(1, 1, -1);
scene.add(world);
// Look.cs: trilight ambient (cool sky, warm ground bounce) and a warm sun at 1.35. three's lights are divided by π
// (physical units) where Unity's built-in pipeline's aren't, hence the π.
const hemi = new THREE.HemisphereLight(new THREE.Color(0.50, 0.58, 0.74), C(98, 80, 58), 1.0 * Math.PI);
scene.add(hemi);
const sun = new THREE.DirectionalLight(C(255, 228, 192), 1.35 * Math.PI);
sun.castShadow = true;
sun.shadow.mapSize.set(2048, 2048);
sun.shadow.bias = -0.0004; sun.shadow.normalBias = 0.02;
scene.add(sun, sun.target);

// ---------------------------------------------------------------- camera (RtsCamera: orthographic, 55° pitch)
const PITCH = 55 * Math.PI / 180;
const view = { x: 48, y: 48, size: 9, yaw: 45, followId: 0 };
const cam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 400);
// Sim (x, y) maps to world (x, 0, y): the same as Unity's W(), seen from the same side (see placeCamera).
function placeCamera() {
  const a = innerWidth / innerHeight, s = view.size;
  cam.left = -s * a; cam.right = s * a; cam.top = s; cam.bottom = -s; cam.updateProjectionMatrix();
  const yaw = view.yaw * Math.PI / 180, back = s + 14;
  // Unity's camera direction (yaw about +y, pitched down), carried through the world's mirror (z negated).
  const fwd = new THREE.Vector3(Math.sin(yaw) * Math.cos(PITCH), -Math.sin(PITCH), -Math.cos(yaw) * Math.cos(PITCH));
  const focus = new THREE.Vector3(view.x, 0, -view.y);
  cam.position.copy(focus).addScaledVector(fwd, -back);
  cam.up.set(0, 1, 0);
  cam.lookAt(focus);
  // The sun at the upper left of the view, wherever it turns (RtsCamera.AimSun).
  const sy = yaw - 0.9;
  sun.position.set(focus.x - Math.sin(sy) * 30, 40, focus.z + Math.cos(sy) * 30);
  sun.target.position.copy(focus);
  const r = s * 1.6 + 6;
  Object.assign(sun.shadow.camera, { left: -r, right: r, top: r, bottom: -r, near: 1, far: 120 });
  sun.shadow.camera.updateProjectionMatrix();
}
addEventListener("resize", () => { renderer.setSize(innerWidth, innerHeight); placeCamera(); });

// ---------------------------------------------------------------- terrain
let mapW = 0, mapH = 0, terrain = null, fogTex = null, oreVersion = -1;
function buildTerrain(m) {
  mapW = m.w; mapH = m.h;
  if (terrain) { world.remove(terrain); terrain.traverse((o) => o.geometry?.dispose()); }
  terrain = new THREE.Group();
  const pos = [], col = [], idx = [];
  const quad = (x0, z0, x1, z1, y, c) => {
    const i = pos.length / 3;
    pos.push(x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1);
    for (let k = 0; k < 4; k++) col.push(c.r, c.g, c.b);
    idx.push(i, i + 2, i + 1, i, i + 3, i + 2);
  };
  const side = (ax, az, bx, bz, y0, y1, c) => { const i = pos.length / 3; pos.push(ax, y0, az, bx, y0, bz, bx, y1, bz, ax, y1, az); for (let k = 0; k < 4; k++) col.push(c.r * 0.7, c.g * 0.7, c.b * 0.7); idx.push(i, i + 1, i + 2, i, i + 2, i + 3, i, i + 2, i + 1, i, i + 3, i + 2); };
  const tile = (x, y) => (x < 0 || y < 0 || x >= mapW || y >= mapH) ? "g" : m.tiles[y * mapW + x];
  // Vertex colours: the biscuit ground with a little low-frequency variation, dirt patches, and each ore field as a
  // soft disc of its colour (an average over nearby tiles, so the edge fades instead of stepping tile by tile).
  const vcol = (vx, vy) => {
    const n = Math.sin(vx * 0.21) * Math.cos(vy * 0.17) * 0.5 + 0.5;
    const c = GROUND_A.clone().lerp(GROUND_B, n * 0.6);
    let dirt = 0, ore = { i: 0, c: 0, x: 0, u: 0 }, cnt = 0;
    for (let dy = -2; dy <= 1; dy++) for (let dx = -2; dx <= 1; dx++) {
      const tx = vx + dx, ty = vy + dy; if (tx < 0 || ty < 0 || tx >= mapW || ty >= mapH) continue;
      const k = ty * mapW + tx; cnt++;
      if (m.tiles[k] === "d") dirt++;
      if (m.ore[k] !== ".") ore[m.ore[k]]++;
    }
    c.lerp(DIRT, Math.min(1, dirt / Math.max(1, cnt)) * 0.8);
    for (const [k, v] of Object.entries(ore)) if (v) c.lerp(ORE[k], Math.min(0.45, (v / cnt) * 0.7));
    return c;
  };
  const gp = [], gc = [], gi = [];
  for (let vy = 0; vy <= mapH; vy++) for (let vx = 0; vx <= mapW; vx++) { gp.push(vx, 0, vy); const c = vcol(vx, vy); gc.push(c.r, c.g, c.b); }
  for (let y = 0; y < mapH; y++) for (let x = 0; x < mapW; x++) {
    const t = m.tiles[y * mapW + x]; if (t === "r" || t === "w") continue;
    const a = y * (mapW + 1) + x, b = a + 1, c2 = a + mapW + 1, d = c2 + 1;
    gi.push(a, d, b, a, c2, d);
  }
  const gg = new THREE.BufferGeometry();
  gg.setAttribute("position", new THREE.Float32BufferAttribute(gp, 3)); gg.setAttribute("color", new THREE.Float32BufferAttribute(gc, 3));
  gg.setIndex(gi); gg.computeVertexNormals();
  const flat = new THREE.Mesh(gg, new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.95, metalness: 0, side: THREE.DoubleSide }));
  flat.receiveShadow = true; terrain.add(flat);
  // The faint tile grid the Unity ground shows.
  const gl = [];
  for (let x = 0; x <= mapW; x++) gl.push(x, 0.01, 0, x, 0.01, mapH);
  for (let y = 0; y <= mapH; y++) gl.push(0, 0.01, y, mapW, 0.01, y);
  const grid = new THREE.LineSegments(new THREE.BufferGeometry().setAttribute("position", new THREE.Float32BufferAttribute(gl, 3)), new THREE.LineBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.06 }));
  terrain.add(grid);
  // Rock (raised, uneven blocks) and water (sunk, dark): the per-tile quads below.
  for (let y = 0; y < mapH; y++) for (let x = 0; x < mapW; x++) {
    const t = m.tiles[y * mapW + x];
    if (t !== "r" && t !== "w") continue;
    const r = Math.sin(x * 12.9898 + y * 78.233) * 43758.5453, fr = r - Math.floor(r);
    const h = t === "r" ? 0.75 + fr * 0.45 : -0.15;
    quad(x, y, x + 1, y + 1, h, t === "r" ? ROCK.clone().offsetHSL(0, 0, (fr - 0.5) * 0.04) : WATER);
    if (t === "r") for (const [dx, dy, ax, az, bx, bz] of [[1, 0, x + 1, y, x + 1, y + 1], [-1, 0, x, y + 1, x, y], [0, 1, x + 1, y + 1, x, y + 1], [0, -1, x, y, x + 1, y]])
      if (tile(x + dx, y + dy) !== "r") side(ax, az, bx, bz, 0, h, ROCK);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute("position", new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute("color", new THREE.Float32BufferAttribute(col, 3));
  g.setIndex(idx); g.computeVertexNormals();
  const ground = new THREE.Mesh(g, new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.95, metalness: 0 }));
  ground.receiveShadow = true;
  terrain.add(ground);
  // Ore: little nuggets on each ore tile, instanced per type.
  for (const [k, oc] of Object.entries(ORE)) {
    const tiles = []; for (let i = 0; i < m.ore.length; i++) if (m.ore[i] === k) tiles.push(i);
    if (!tiles.length) continue;
    const per = 6;
    const inst = new THREE.InstancedMesh(new THREE.IcosahedronGeometry(0.07, 0), new THREE.MeshStandardMaterial({ color: oc.clone().multiplyScalar(0.8), roughness: 0.6, emissive: k === "x" || k === "u" ? oc : 0x000000, emissiveIntensity: k === "x" || k === "u" ? 0.5 : 0, flatShading: true }), tiles.length * per);
    const mtx = new THREE.Matrix4(); let n = 0;
    for (const i of tiles) for (let j = 0; j < per; j++) {
      const r = Math.sin(i * 12.9898 + j * 78.233) * 43758.5453, fr = r - Math.floor(r), r2 = Math.sin(i * 4.1 + j * 17.7) * 9631.17, fr2 = r2 - Math.floor(r2);
      mtx.makeRotationY(fr * 6.28).scale(new THREE.Vector3(1, 0.7, 1).multiplyScalar(0.7 + fr2 * 0.7)).setPosition((i % mapW) + 0.1 + fr * 0.8, 0.03, Math.floor(i / mapW) + 0.1 + fr2 * 0.8);
      inst.setMatrixAt(n++, mtx);
    }
    inst.castShadow = true;
    terrain.add(inst);
  }
  // Fog of war: one texel per tile over the map, soft-edged (linear filtering), unexplored black, explored dimmed.
  fogTex = new THREE.DataTexture(new Uint8Array(mapW * mapH * 4), mapW, mapH);
  fogTex.magFilter = THREE.LinearFilter; fogTex.minFilter = THREE.LinearFilter; fogTex.needsUpdate = true;
  const fog = new THREE.Mesh(new THREE.PlaneGeometry(mapW, mapH), new THREE.MeshBasicMaterial({ map: fogTex, transparent: true, depthWrite: false }));
  // Lying flat with texel (x, y) over tile (x, y): rotated so the plane's +y runs along +z (sim +y).
  fog.rotation.x = Math.PI / 2; fog.position.set(mapW / 2, 1.6, mapH / 2); fog.renderOrder = 10;
  fog.material.side = THREE.DoubleSide;
  terrain.add(fog);
  world.add(terrain);
}
function updateFog(shroud) {
  if (!fogTex) return;
  const d = fogTex.image.data;
  for (let i = 0; i < mapW * mapH; i++) { const s = shroud ? shroud[i] : "2"; d[i * 4] = 18; d[i * 4 + 1] = 14; d[i * 4 + 2] = 12; d[i * 4 + 3] = s === "2" ? 0 : s === "1" ? 120 : 245; }
  fogTex.needsUpdate = true;
}

// ---------------------------------------------------------------- models
const loader = new GLTFLoader();
const models = new Map(); // key -> Promise<THREE.Object3D | null>
let modelBytes = 0;
function model(key) {
  const file = MODEL_AS[key] ?? key;
  if (!models.has(file)) models.set(file, new Promise((res) => {
    fetch(`./models/${file}.glb`).then((r) => r.ok ? r.arrayBuffer() : null).then((buf) => {
      if (!buf) return res(null);
      modelBytes += buf.byteLength;
      loader.parse(buf, "", (g) => {
        // The art pack ships no normals: crease them like Unity's SmoothByAngle (edges sharper than ~35° stay hard).
        g.scene.traverse((o) => { if (o.isMesh) { o.geometry = toCreasedNormals(o.geometry, 35 * Math.PI / 180); o.castShadow = o.receiveShadow = true; } });
        res(g.scene);
      }, () => res(null));
    }).catch(() => res(null));
  }));
  return models.get(file);
}
const teamMats = new Map();
function tint(obj, team) {
  obj.traverse((o) => {
    if (!o.isMesh) return;
    const mats = Array.isArray(o.material) ? o.material : [o.material];
    o.material = mats.map((m) => {
      if (m.name?.startsWith("M_Team")) {
        const k = `${m.uuid}:${team}`;
        if (!teamMats.has(k)) { const c = m.clone(); c.color = (team >= 0 ? TEAM[team % TEAM.length] : NEUTRAL).clone(); teamMats.set(k, c); }
        return teamMats.get(k);
      }
      if (m.name?.startsWith("M_E_") && !m.userData.glow) { m.emissive = m.color.clone(); m.emissiveIntensity = 1.6; m.userData.glow = true; }
      return m;
    });
    if (o.material.length === 1) o.material = o.material[0];
  });
}
function placeholder(team) {
  const m = new THREE.Mesh(new THREE.BoxGeometry(0.6, 0.4, 0.8), new THREE.MeshStandardMaterial({ color: team >= 0 ? TEAM[team % TEAM.length] : NEUTRAL }));
  m.position.y = 0.2; m.castShadow = true;
  const g = new THREE.Group(); g.add(m); return g;
}

// ---------------------------------------------------------------- entities: [id, key, team, x, y, hp%, facing, build%, sizeX, fuel%]
const ents = new Map(); // id -> { obj, key, team, a:{x,y,f,t}, b:{x,y,f,t}, alive }
let frames = 0, lastFrameAt = 0, bytesIn = 0, bytesWindow = [], lastTick = -1;
function upsert(e, now) {
  const [id, key, team, x, y, hp, facing, build, size] = e;
  let s = ents.get(id);
  if (!s) {
    s = { key, team, obj: new THREE.Group(), a: { x, y, f: facing, t: now }, b: { x, y, f: facing, t: now }, build };
    s.obj.userData.id = id;
    world.add(s.obj);
    ents.set(id, s);
    model(key).then((proto) => {
      if (!ents.has(id)) return;
      const m = proto ? proto.clone(true) : placeholder(team);
      tint(m, team);
      const holder = new THREE.Group(); holder.scale.set(1, 1, -1); holder.add(m); // undo the world's mirror for the model itself
      s.body = m; s.obj.add(holder);
    });
  }
  if (s.team !== team && s.body) { tint(s.body, team); s.team = team; } // captured
  s.a = { ...s.b, t: s.b.t }; s.b = { x, y, f: facing, t: now };
  s.build = build; s.seen = now;
}

// ---------------------------------------------------------------- effects: [seq, type, x, y, x2, y2, team, a]
const fx = [], seenFx = new Set();
function effect(e) {
  const [seq, type, x, y, x2, y2, team] = e;
  if (seenFx.has(seq)) return; seenFx.add(seq);
  if (type === "shot" || type === "fire") {
    const g = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(x, 0.45, y), new THREE.Vector3(x2, 0.35, y2)]);
    const l = new THREE.Line(g, new THREE.LineBasicMaterial({ color: team >= 0 ? TEAM[team % TEAM.length] : 0xffe0a0, transparent: true }));
    world.add(l); fx.push({ o: l, life: 0.18, t: 0 });
  } else if (type === "destroyed" || type === "hit") {
    const big = type === "destroyed";
    const s = new THREE.Mesh(new THREE.SphereGeometry(big ? 0.6 : 0.18, 12, 8), new THREE.MeshBasicMaterial({ color: 0xffb04a, transparent: true }));
    s.position.set(x, 0.4, y); world.add(s); fx.push({ o: s, life: big ? 0.7 : 0.25, t: 0, grow: big ? 2.6 : 1.6 });
    if (big) { const pl = new THREE.PointLight(0xff9a3a, 30, 6); pl.position.set(x, 1.2, y); world.add(pl); fx.push({ o: pl, life: 0.5, t: 0, light: true }); }
  }
}

// ---------------------------------------------------------------- polling
async function poll() {
  try {
    const r = await fetch(DATA("frame"), { cache: "no-store" });
    const txt = await r.text(); bytesIn += txt.length; bytesWindow.push([performance.now(), txt.length]);
    const f = JSON.parse(txt);
    if (f.ok === false) throw new Error(f.error);
    if (!terrain || f.ore_version !== oreVersion) { const m = await (await fetch(DATA("map"), { cache: "no-store" })).json(); bytesIn += 1; buildTerrain(m); oreVersion = f.ore_version; if (!frames) {
      // Start on ?x=&y=, else this team's command center, else anything of theirs, else anything at all.
      const team = Number(Q.get("team") ?? 0), mine = f.entities.filter((e) => e[2] === team);
      const at = Q.has("x") ? [0, 0, 0, +Q.get("x"), +Q.get("y")] : mine.find((e) => e[1] === "command_center") ?? mine[0] ?? f.entities[0];
      if (at) { view.x = at[3]; view.y = at[4]; }
      if (Q.has("size")) view.size = +Q.get("size");
      placeCamera();
    } }
    if (f.tick !== lastTick) {
      lastTick = f.tick;
      const now = performance.now();
      const live = new Set();
      for (const e of f.entities) { upsert(e, now); live.add(e[0]); }
      for (const [id, s] of ents) if (!live.has(id)) { world.remove(s.obj); ents.delete(id); }
      for (const e of f.effects ?? []) effect(e);
      updateFog(f.shroud);
      frames++;
      document.getElementById("who").textContent = `· ${f.you?.flavor ?? ""} · game ${Math.floor(f.time_s / 60)}:${String(Math.floor(f.time_s % 60)).padStart(2, "0")}`;
    }
  } catch (err) { document.getElementById("stats").textContent = "data: " + err.message; }
  setTimeout(poll, POLL_MS);
}

// ---------------------------------------------------------------- input: drag pan, pinch/wheel zoom, rotate, tap to follow
const pts = new Map(); let pinch = 0, moved = 0;
const el = renderer.domElement;
function pan(dx, dy) {
  const s = view.size, a = innerWidth / innerHeight;
  const right = new THREE.Vector3(1, 0, 0).applyQuaternion(cam.quaternion); right.y = 0; right.normalize();
  const up = new THREE.Vector3(0, 1, 0).applyQuaternion(cam.quaternion); up.y = 0; up.normalize();
  const ux = -dx / innerWidth * 2 * s * a, uy = dy / innerHeight * 2 * s / Math.sin(PITCH);
  const d = right.multiplyScalar(ux).add(up.multiplyScalar(uy)); // three's world: sim y is -z
  view.x += d.x; view.y -= d.z;
  view.followId = 0; showFollow();
}
el.addEventListener("pointerdown", (e) => { el.setPointerCapture(e.pointerId); pts.set(e.pointerId, { x: e.clientX, y: e.clientY }); if (pts.size === 1) moved = 0; });
el.addEventListener("pointermove", (e) => {
  const p = pts.get(e.pointerId); if (!p) return;
  if (pts.size === 1) { moved += Math.abs(e.clientX - p.x) + Math.abs(e.clientY - p.y); if (moved > 4) pan(e.clientX - p.x, e.clientY - p.y); }
  pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
  if (pts.size === 2) { const [a, b] = [...pts.values()], d = Math.hypot(a.x - b.x, a.y - b.y); if (pinch) zoom(pinch / d); pinch = d; moved = 99; }
});
el.addEventListener("pointerup", (e) => { if (pts.size === 1 && moved < 6) pick(e); pts.delete(e.pointerId); if (pts.size < 2) pinch = 0; });
el.addEventListener("pointercancel", (e) => { pts.delete(e.pointerId); pinch = 0; });
addEventListener("wheel", (e) => { e.preventDefault(); zoom(e.deltaY < 0 ? 0.9 : 1.11); }, { passive: false });
const zoom = (f) => { view.size = Math.min(40, Math.max(4, view.size * f)); };
addEventListener("keydown", (e) => { const k = e.key.toLowerCase(), s = 60; if (k === "a" || k === "arrowleft") pan(s, 0); if (k === "d" || k === "arrowright") pan(-s, 0); if (k === "w" || k === "arrowup") pan(0, s); if (k === "s" || k === "arrowdown") pan(0, -s); if (k === "q") view.yaw -= 15; if (k === "e") view.yaw += 15; if (k === "escape") { view.followId = 0; showFollow(); } });
document.querySelectorAll("#pad button").forEach((b) => b.onclick = () => { if (b.dataset.z) zoom(+b.dataset.z); if (b.dataset.y) view.yaw += +b.dataset.y; });
const ray = new THREE.Raycaster();
function pick(e) {
  ray.setFromCamera(new THREE.Vector2(e.clientX / innerWidth * 2 - 1, -(e.clientY / innerHeight * 2 - 1)), cam);
  const hit = ray.intersectObjects([...ents.values()].map((s) => s.obj), true)[0];
  let o = hit?.object; while (o && o.userData.id == null) o = o.parent;
  const s = o && ents.get(o.userData.id);
  view.followId = s && !["command_center"].includes(s.key) && !s.build_structure ? o.userData.id : 0;
  showFollow();
}
function showFollow() { const f = document.getElementById("fol"), s = ents.get(view.followId); f.style.display = s ? "block" : "none"; if (s) f.textContent = `following ${s.key} #${view.followId} · tap empty ground or Esc to stop`; }

// ---------------------------------------------------------------- frame loop
const clock = new THREE.Clock(); let fpsN = 0, fpsT = 0, fps = 0;
function lerpAngle(a, b, t) { let d = b - a; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return a + d * t; }
function tick() {
  const dt = clock.getDelta(), now = performance.now(), at = now - DELAY_MS;
  for (const [id, s] of ents) {
    const span = Math.max(1, s.b.t - s.a.t), t = Math.min(1, Math.max(0, (at - s.a.t) / span));
    const x = s.a.x + (s.b.x - s.a.x) * t, y = s.a.y + (s.b.y - s.a.y) * t, f = lerpAngle(s.a.f, s.b.f, t);
    const alt = AIR.has(s.key) ? 2.2 : 0;
    s.obj.position.set(x, alt, y);
    // Unity: Euler(0, 90° − facing) in its own (left-handed) coordinates: the world group carries that over.
    s.obj.rotation.y = Math.PI / 2 - f;
    if (s.build >= 0 && s.build < 100) s.obj.scale.set(1, Math.max(0.08, s.build / 100), 1); else s.obj.scale.set(1, 1, 1);
    if (id === view.followId) { view.x += (x - view.x) * Math.min(1, dt * 6); view.y += (y - view.y) * Math.min(1, dt * 6); }
  }
  for (let i = fx.length - 1; i >= 0; i--) {
    const e = fx[i]; e.t += dt; const k = e.t / e.life;
    if (k >= 1) { world.remove(e.o); e.o.geometry?.dispose(); fx.splice(i, 1); continue; }
    if (e.light) e.o.intensity = 30 * (1 - k); else { e.o.material.opacity = 1 - k; if (e.grow) e.o.scale.setScalar(1 + (e.grow - 1) * k); }
  }
  placeCamera();
  renderer.render(scene, cam);
  fpsN++; fpsT += dt; if (fpsT >= 1) { fps = fpsN / fpsT; fpsN = 0; fpsT = 0;
    const cut = now - 5000; bytesWindow = bytesWindow.filter(([t]) => t > cut); const bps = bytesWindow.reduce((s, [, n]) => s + n, 0) / 5;
    document.getElementById("stats").textContent = `${fps.toFixed(0)} fps · ${ents.size} things · ${(bps / 1024).toFixed(1)} KB/s data · models ${(modelBytes / 1024).toFixed(0)} KB · ${renderer.info.render.calls} draw calls`;
    window.__perf = { fps, bps, ents: ents.size, calls: renderer.info.render.calls, modelBytes };
  }
  requestAnimationFrame(tick);
}
placeCamera();
poll();
tick();
