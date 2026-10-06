// Pezz in the browser, Babylon.js edition (bake-off against arena/web3d/next.js, the three.js one). Same data, same
// glTF models, same camera; Babylon's own toolbox for the look: glow layer, default pipeline (bloom, tone mapping,
// anti-aliasing, vignette), SSAO2, shadow generator, particle systems. WebGPU when the browser has it, else WebGL2.
import { Engine } from "@babylonjs/core/Engines/engine";
import { WebGPUEngine } from "@babylonjs/core/Engines/webgpuEngine";
import { Scene } from "@babylonjs/core/scene";
import { Camera } from "@babylonjs/core/Cameras/camera";
import { TargetCamera } from "@babylonjs/core/Cameras/targetCamera";
import { Vector3, Matrix } from "@babylonjs/core/Maths/math.vector";
import { Color3, Color4 } from "@babylonjs/core/Maths/math.color";
import { HemisphericLight } from "@babylonjs/core/Lights/hemisphericLight";
import { DirectionalLight } from "@babylonjs/core/Lights/directionalLight";
import { PointLight } from "@babylonjs/core/Lights/pointLight";
import { ShadowGenerator } from "@babylonjs/core/Lights/Shadows/shadowGenerator";
import "@babylonjs/core/Lights/Shadows/shadowGeneratorSceneComponent";
import { Mesh } from "@babylonjs/core/Meshes/mesh";
import { VertexData } from "@babylonjs/core/Meshes/mesh.vertexData";
import { VertexBuffer } from "@babylonjs/core/Buffers/buffer";
import { CreateGround } from "@babylonjs/core/Meshes/Builders/groundBuilder";
import { CreatePolyhedron } from "@babylonjs/core/Meshes/Builders/polyhedronBuilder";
import { CreateLines } from "@babylonjs/core/Meshes/Builders/linesBuilder";
import { CreateBox } from "@babylonjs/core/Meshes/Builders/boxBuilder";
import "@babylonjs/core/Meshes/thinInstanceMesh";
import { StandardMaterial } from "@babylonjs/core/Materials/standardMaterial";
import { PBRMaterial } from "@babylonjs/core/Materials/PBR/pbrMaterial";
import { RawTexture } from "@babylonjs/core/Materials/Textures/rawTexture";
import { Texture } from "@babylonjs/core/Materials/Textures/texture";
import { GlowLayer } from "@babylonjs/core/Layers/glowLayer";
import "@babylonjs/core/Layers/effectLayerSceneComponent";
import { DefaultRenderingPipeline } from "@babylonjs/core/PostProcesses/RenderPipeline/Pipelines/defaultRenderingPipeline";
import { SSAO2RenderingPipeline } from "@babylonjs/core/PostProcesses/RenderPipeline/Pipelines/ssao2RenderingPipeline";
import "@babylonjs/core/PostProcesses/RenderPipeline/postProcessRenderPipelineManagerSceneComponent";
import "@babylonjs/core/Rendering/geometryBufferRendererSceneComponent";
import "@babylonjs/core/Rendering/prePassRendererSceneComponent";
import { ImageProcessingConfiguration } from "@babylonjs/core/Materials/imageProcessingConfiguration";
import { ParticleSystem } from "@babylonjs/core/Particles/particleSystem";
import "@babylonjs/core/Particles/particleSystemComponent";
import { LoadAssetContainerAsync } from "@babylonjs/core/Loading/sceneLoader";
import "@babylonjs/loaders/glTF/2.0";
import "@babylonjs/core/Culling/ray";
import "@babylonjs/core/Engines/Extensions/engine.multiRender";
import "@babylonjs/core/Engines/WebGPU/Extensions/engine.multiRender";
// Tree-shaking drops the engine extensions that create raw and canvas textures; without them those textures never
// become ready (no particles drawn, no fog of war).
import "@babylonjs/core/Engines/Extensions/engine.rawTexture";
import "@babylonjs/core/Engines/Extensions/engine.dynamicTexture";
import "@babylonjs/core/Engines/WebGPU/Extensions/engine.rawTexture";
import "@babylonjs/core/Engines/WebGPU/Extensions/engine.dynamicTexture";

const Q = new URLSearchParams(location.search);
const BOOT = window.__boot ?? { step() {}, fail() {}, ready() {} };
const DELAY_MS = 250;

// ---------------------------------------------------------------- palette (sRGB, converted to linear for lighting)
const C = (r, g, b) => new Color3(r / 255, g / 255, b / 255).toLinearSpace();
const TEAM = [C(46, 115, 255), C(242, 46, 31), C(51, 217, 77), C(255, 209, 38), C(142, 68, 217), C(58, 47, 158), C(15, 140, 138), C(142, 27, 74)];
const NEUTRAL = C(185, 174, 152);
const G = (r, g, b) => new Color3(r / 255, g / 255, b / 255).toLinearSpace(); // vertex colours, linear like the lighting
const GROUND_A = G(150, 133, 104), GROUND_B = G(165, 148, 116), DIRT = G(126, 110, 84), WATER = G(58, 34, 24), ROCK = G(84, 68, 60);
const ORE = { i: C(138, 58, 36), c: C(200, 116, 47), x: C(143, 228, 255), u: C(182, 255, 59) };
const OREV = { i: G(138, 58, 36), c: G(200, 116, 47), x: G(143, 228, 255), u: G(182, 255, 59) };
const MODEL_AS = { long_range_drone: "gunship", reaper_drone: "stealth_bomber", recon_drone: "gunship", transport_chopper: "gunship", mammoth_tank: "heavy_tank",
  flak_track: "light_tank", apc: "light_tank", minelayer: "mining_truck", commando: "rifleman", sniper: "rifleman", engineer: "medic",
  construction_truck: "outpost_truck", derrick: "deep_mine" };
const AIR = new Set(["gunship", "stealth_bomber", "recon_drone", "long_range_drone", "reaper_drone", "transport_chopper"]);
const INFANTRY = /rifle|soldier|trooper|medic|engineer|sniper|commando/;

// ---------------------------------------------------------------- engine
const canvas = document.createElement("canvas");
canvas.style.cssText = "display:block;width:100vw;height:100vh;touch-action:none";
document.body.prepend(canvas);
let engine, backend;
if (Q.get("gl") !== "webgl" && navigator.gpu && await WebGPUEngine.IsSupportedAsync) {
  engine = new WebGPUEngine(canvas, { antialias: true, adaptToDeviceRatio: true }); await engine.initAsync(); backend = "WebGPU";
} else { engine = new Engine(canvas, true, { stencil: true }, true); backend = "WebGL2"; }
engine.setHardwareScalingLevel(1 / Math.min(devicePixelRatio, 2));
const scene = new Scene(engine);
scene.clearColor = new Color4(0.086, 0.07, 0.059, 1);
scene.skipPointerMovePicking = true;
BOOT.step("start", "ok", backend); BOOT.step("data", "run");

// Look.cs: trilight ambient (cool sky, warm ground bounce), a warm sun at the view's upper left.
const hemi = new HemisphericLight("sky", new Vector3(0, 1, 0), scene);
hemi.diffuse = new Color3(0.5, 0.58, 0.74); hemi.groundColor = C(98, 80, 58); hemi.intensity = 0.95; hemi.specular = Color3.Black();
const sun = new DirectionalLight("sun", new Vector3(-0.4, -1, 0.4), scene);
sun.diffuse = C(255, 228, 192); sun.intensity = 2.6;
const shadows = new ShadowGenerator(2048, sun);
shadows.usePercentageCloserFiltering = true; shadows.filteringQuality = ShadowGenerator.QUALITY_MEDIUM; shadows.bias = 0.004; shadows.normalBias = 0.03; shadows.darkness = 0.25;

// ---------------------------------------------------------------- camera (RtsCamera: orthographic, 55° pitch; Babylon is left-handed like Unity)
const PITCH = 55 * Math.PI / 180;
const view = { x: 48, y: 48, size: 9, yaw: 45, followId: 0 };
const cam = new TargetCamera("cam", new Vector3(0, 30, 0), scene);
cam.mode = Camera.ORTHOGRAPHIC_CAMERA; cam.minZ = 0.1; cam.maxZ = 400;
function placeCamera() {
  const a = engine.getRenderWidth() / engine.getRenderHeight(), s = view.size, yaw = view.yaw * Math.PI / 180, back = s + 14;
  cam.orthoTop = s; cam.orthoBottom = -s; cam.orthoLeft = -s * a; cam.orthoRight = s * a;
  const fwd = new Vector3(Math.sin(yaw) * Math.cos(PITCH), -Math.sin(PITCH), Math.cos(yaw) * Math.cos(PITCH));
  const focus = new Vector3(view.x, 0, view.y);
  cam.position = focus.subtract(fwd.scale(back));
  cam.setTarget(focus);
  const sy = yaw - 0.9;
  sun.direction = new Vector3(Math.sin(sy) * 0.75, -1, Math.cos(sy) * 0.75).normalize();
  sun.position = focus.subtract(sun.direction.scale(40));
  sun.autoUpdateExtends = false; sun.shadowFrustumSize = s * 3.2;
}

// ---------------------------------------------------------------- the look: glow, pipeline, ambient occlusion
const glow = new GlowLayer("glow", scene, { mainTextureSamples: 4, blurKernelSize: 40 });
glow.intensity = 0.9;
const pipe = new DefaultRenderingPipeline("look", true, scene, [cam]);
pipe.samples = 4;
pipe.fxaaEnabled = true;
pipe.bloomEnabled = true; pipe.bloomThreshold = 0.9; pipe.bloomWeight = 0.35; pipe.bloomKernel = 48; pipe.bloomScale = 0.5;
pipe.imageProcessingEnabled = true;
pipe.imageProcessing.toneMappingEnabled = true; pipe.imageProcessing.toneMappingType = ImageProcessingConfiguration.TONEMAPPING_KHR_PBR_NEUTRAL;
pipe.imageProcessing.exposure = 1.0; pipe.imageProcessing.contrast = 1.12;
pipe.imageProcessing.vignetteEnabled = true; pipe.imageProcessing.vignetteWeight = 1.4; pipe.imageProcessing.vignetteColor = new Color4(0, 0, 0, 0);
let ssao = null;
if (Q.get("fx") !== "noao") try {
  ssao = new SSAO2RenderingPipeline("ao", scene, { ssaoRatio: 0.5, blurRatio: 1 }, [cam], true);
  ssao.radius = 1.4; ssao.totalStrength = 1.3; ssao.samples = 16; ssao.expensiveBlur = true; ssao.maxZ = 400; ssao.base = 0.1;
} catch (e) { console.warn("SSAO2 unavailable:", e.message); }

// ---------------------------------------------------------------- terrain
let mapW = 0, mapH = 0, terrainMeshes = [], fogTex = null, fogData = null, oreVersion = -1;
function buildTerrain(m) {
  mapW = m.w; mapH = m.h;
  for (const t of terrainMeshes) t.dispose(); terrainMeshes = [];
  // Vertex colours over a (W+1)x(H+1) grid: biscuit ground, dirt, and soft ore fields.
  const pos = [], col = [], idx = [];
  const vcol = (vx, vy) => {
    const n = Math.sin(vx * 0.21) * Math.cos(vy * 0.17) * 0.5 + 0.5;
    const c = Color3.Lerp(GROUND_A, GROUND_B, n * 0.6);
    let dirt = 0, cnt = 0; const ore = { i: 0, c: 0, x: 0, u: 0 };
    for (let dy = -2; dy <= 1; dy++) for (let dx = -2; dx <= 1; dx++) {
      const tx = vx + dx, ty = vy + dy; if (tx < 0 || ty < 0 || tx >= mapW || ty >= mapH) continue;
      const k = ty * mapW + tx; cnt++; if (m.tiles[k] === "d") dirt++; if (m.ore[k] !== ".") ore[m.ore[k]]++;
    }
    let out = Color3.Lerp(c, DIRT, Math.min(1, dirt / Math.max(1, cnt)) * 0.8);
    for (const [k, v] of Object.entries(ore)) if (v) out = Color3.Lerp(out, OREV[k], Math.min(0.45, (v / cnt) * 0.7));
    return out;
  };
  for (let vy = 0; vy <= mapH; vy++) for (let vx = 0; vx <= mapW; vx++) { pos.push(vx, 0, vy); const c = vcol(vx, vy); col.push(c.r, c.g, c.b, 1); }
  for (let y = 0; y < mapH; y++) for (let x = 0; x < mapW; x++) {
    const t = m.tiles[y * mapW + x]; if (t === "r" || t === "w") continue;
    const a = y * (mapW + 1) + x, b = a + 1, c2 = a + mapW + 1, d = c2 + 1;
    idx.push(a, d, c2, a, b, d); // Babylon is left-handed: wound so the faces (and normals) point up
  }
  const ground = new Mesh("ground", scene);
  const vd = new VertexData(); vd.positions = pos; vd.indices = idx; vd.colors = col;
  const nrm = []; VertexData.ComputeNormals(pos, idx, nrm); vd.normals = nrm; vd.applyToMesh(ground);
  const gm = new PBRMaterial("groundMat", scene); gm.albedoColor = Color3.White(); gm.metallic = 0; gm.roughness = 1; gm.environmentIntensity = 0;
  ground.material = gm; ground.receiveShadows = true; terrainMeshes.push(ground);
  // Rock (raised, uneven blocks) and water (sunk, dark).
  const rp = [], rc = [], ri = [];
  const quad = (x0, z0, x1, z1, y, c) => { const i = rp.length / 3; rp.push(x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1); for (let k = 0; k < 4; k++) rc.push(c.r, c.g, c.b, 1); ri.push(i, i + 2, i + 3, i, i + 1, i + 2); };
  const side = (ax, az, bx, bz, h, c) => { const i = rp.length / 3; rp.push(ax, 0, az, bx, 0, bz, bx, h, bz, ax, h, az); for (let k = 0; k < 4; k++) rc.push(c.r * 0.6, c.g * 0.6, c.b * 0.6, 1); ri.push(i, i + 1, i + 2, i, i + 2, i + 3, i, i + 2, i + 1, i, i + 3, i + 2); };
  const tile = (x, y) => (x < 0 || y < 0 || x >= mapW || y >= mapH) ? "g" : m.tiles[y * mapW + x];
  for (let y = 0; y < mapH; y++) for (let x = 0; x < mapW; x++) {
    const t = m.tiles[y * mapW + x]; if (t !== "r" && t !== "w") continue;
    const r = Math.sin(x * 12.9898 + y * 78.233) * 43758.5453, fr = r - Math.floor(r), h = t === "r" ? 0.75 + fr * 0.45 : -0.15;
    quad(x, y, x + 1, y + 1, h, t === "r" ? ROCK : WATER);
    if (t === "r") for (const [dx, dy, ax, az, bx, bz] of [[1, 0, x + 1, y, x + 1, y + 1], [-1, 0, x, y + 1, x, y], [0, 1, x + 1, y + 1, x, y + 1], [0, -1, x, y, x + 1, y]])
      if (tile(x + dx, y + dy) !== "r") side(ax, az, bx, bz, h, ROCK);
  }
  if (rp.length) {
    const rocks = new Mesh("rocks", scene); const rv = new VertexData(); rv.positions = rp; rv.indices = ri; rv.colors = rc;
    const rn = []; VertexData.ComputeNormals(rp, ri, rn); rv.normals = rn; rv.applyToMesh(rocks);
    rocks.material = gm; rocks.receiveShadows = true; shadows.addShadowCaster(rocks); terrainMeshes.push(rocks);
  }
  // Ore nuggets: thin instances, one mesh per ore type.
  for (const [k, oc] of Object.entries(ORE)) {
    const tiles = []; for (let i = 0; i < m.ore.length; i++) if (m.ore[i] === k) tiles.push(i);
    if (!tiles.length) continue;
    const nug = CreatePolyhedron(`ore_${k}`, { type: 1, size: 0.07 }, scene);
    const nm = new PBRMaterial(`oreMat_${k}`, scene); nm.albedoColor = oc.scale(0.8); nm.metallic = 0; nm.roughness = 0.6;
    if (k === "x" || k === "u") nm.emissiveColor = oc.scale(0.6);
    nug.material = nm;
    const per = 6, buf = new Float32Array(tiles.length * per * 16); let n = 0;
    for (const i of tiles) for (let j = 0; j < per; j++) {
      const r = Math.sin(i * 12.9898 + j * 78.233) * 43758.5453, fr = r - Math.floor(r), r2 = Math.sin(i * 4.1 + j * 17.7) * 9631.17, fr2 = r2 - Math.floor(r2), sc = 0.7 + fr2 * 0.7;
      const mt = Matrix.Scaling(sc, sc * 0.7, sc).multiply(Matrix.RotationY(fr * 6.28)).multiply(Matrix.Translation((i % mapW) + 0.1 + fr * 0.8, 0.03, Math.floor(i / mapW) + 0.1 + fr2 * 0.8));
      mt.copyToArray(buf, (n++) * 16);
    }
    nug.thinInstanceSetBuffer("matrix", buf, 16, true);
    terrainMeshes.push(nug);
  }
  // Fog of war: one texel per tile, filtered soft; unexplored black, explored dimmed.
  fogData = new Uint8Array(mapW * mapH * 4);
  fogTex = RawTexture.CreateRGBATexture(fogData, mapW, mapH, scene, false, false, Texture.BILINEAR_SAMPLINGMODE);
  const fog = CreateGround("fog", { width: mapW, height: mapH }, scene);
  fog.position.set(mapW / 2, 1.6, mapH / 2);
  const fm = new StandardMaterial("fogMat", scene); fm.disableLighting = true; fm.emissiveColor = new Color3(0.07, 0.055, 0.047);
  fm.opacityTexture = fogTex; fm.backFaceCulling = false; fog.material = fm; fog.isPickable = false;
  if (Q.get("fog") === "off") fog.setEnabled(false);
  terrainMeshes.push(fog);
}
function updateFog(shroud) {
  if (!fogTex) return;
  for (let i = 0; i < mapW * mapH; i++) { const s = shroud ? shroud[i] : "2"; fogData[i * 4 + 3] = s === "2" ? 0 : s === "1" ? 120 : 245; }
  fogTex.update(fogData);
}

// ---------------------------------------------------------------- models
const containers = new Map(); let modelsAsked = 0, modelsDone = 0, modelBytes = 0, booted = false;
// The art pack ships no normals: crease them like Unity's SmoothByAngle (edges sharper than ~35° stay hard).
function creaseNormals(mesh) {
  const p = mesh.getVerticesData(VertexBuffer.PositionKind), ind = mesh.getIndices();
  if (!p || !ind) return;
  const P = [], N = [], cosA = Math.cos(35 * Math.PI / 180), faces = new Map(), faceN = [];
  for (let f = 0; f < ind.length; f += 3) {
    const a = ind[f] * 3, b = ind[f + 1] * 3, c = ind[f + 2] * 3;
    const A = new Vector3(p[a], p[a + 1], p[a + 2]), B = new Vector3(p[b], p[b + 1], p[b + 2]), Cc = new Vector3(p[c], p[c + 1], p[c + 2]);
    const n = Vector3.Cross(B.subtract(A), Cc.subtract(A)).normalize(); faceN.push(n);
    for (const V of [A, B, Cc]) { const k = `${V.x.toFixed(4)},${V.y.toFixed(4)},${V.z.toFixed(4)}`; (faces.get(k) ?? faces.set(k, []).get(k)).push(n); P.push(V.x, V.y, V.z); }
  }
  for (let f = 0; f < faceN.length; f++) for (let v = 0; v < 3; v++) {
    const i = f * 3 + v, k = `${P[i * 3].toFixed(4)},${P[i * 3 + 1].toFixed(4)},${P[i * 3 + 2].toFixed(4)}`, fn = faceN[f];
    const s = new Vector3(0, 0, 0); for (const o of faces.get(k)) if (Vector3.Dot(o, fn) >= cosA) s.addInPlace(o);
    const n = s.normalize(); N.push(n.x, n.y, n.z);
  }
  const newInd = [...Array(P.length / 3).keys()];
  // glTF winding comes out flipped once Babylon's loader converts handedness; the normals above follow the triangles as stored.
  const vd = new VertexData(); vd.positions = P; vd.normals = N; vd.indices = newInd; vd.applyToMesh(mesh);
}
function model(key) {
  const file = MODEL_AS[key] ?? key;
  if (!containers.has(file)) {
    modelsAsked++; if (!booted) BOOT.step("models", "run", `${modelsDone} of ${modelsAsked}`);
    containers.set(file, (async () => {
      try {
        const r = await fetch(`./models/${file}.glb`); if (!r.ok) return null;
        const buf = await r.arrayBuffer(); modelBytes += buf.byteLength;
        const c = await LoadAssetContainerAsync(new Uint8Array(buf), scene, { pluginExtension: ".glb" });
        for (const m of c.meshes) if (m.getTotalVertices() > 0) creaseNormals(m);
        for (const mat of c.materials) if (mat.name?.startsWith("M_E_")) { mat.emissiveColor = mat.albedoColor.scale(1.6); }
        return c;
      } catch (e) { console.warn("model", file, e.message); return null; }
      finally { modelsDone++; if (!booted) BOOT.step("models", modelsDone >= modelsAsked ? "ok" : "run", `${modelsDone} of ${modelsAsked} · ${(modelBytes / 1024).toFixed(0)} KB`); }
    })());
  }
  return containers.get(file);
}
const teamMats = new Map();
function teamMat(base, team) {
  const k = `${base.uniqueId}:${team}`;
  if (!teamMats.has(k)) { const m = base.clone(`${base.name}_t${team}`); m.albedoColor = team >= 0 ? TEAM[team % TEAM.length] : NEUTRAL; teamMats.set(k, m); }
  return teamMats.get(k);
}

// ---------------------------------------------------------------- particles (FxSystems): textures drawn once
// Drawn on a canvas, uploaded as raw pixels (a DynamicTexture never reported ready to the particle systems here).
function spriteTex(name, draw) {
  const c = document.createElement("canvas"); c.width = c.height = 128; const g = c.getContext("2d"); draw(g, 128);
  const t = RawTexture.CreateRGBATexture(new Uint8Array(g.getImageData(0, 0, 128, 128).data.buffer), 128, 128, scene, true, false, Texture.TRILINEAR_SAMPLINGMODE);
  t.hasAlpha = true; t.name = name; return t;
}
const TX = {
  dot: spriteTex("dot", (g, n) => { const r = g.createRadialGradient(n / 2, n / 2, 0, n / 2, n / 2, n / 2); r.addColorStop(0, "rgba(255,255,255,1)"); r.addColorStop(0.4, "rgba(255,255,255,.5)"); r.addColorStop(1, "rgba(255,255,255,0)"); g.fillStyle = r; g.fillRect(0, 0, n, n); }),
  puff: spriteTex("puff", (g, n) => { for (let i = 0; i < 9; i++) { const a = Math.random() * 6.28, d = Math.random() * n * 0.18, x = n / 2 + Math.cos(a) * d, y = n / 2 + Math.sin(a) * d, rr = n * (0.18 + Math.random() * 0.16); const r = g.createRadialGradient(x, y, 0, x, y, rr); r.addColorStop(0, "rgba(255,255,255,.55)"); r.addColorStop(1, "rgba(255,255,255,0)"); g.fillStyle = r; g.fillRect(0, 0, n, n); } }),
  flame: spriteTex("flame", (g, n) => { const r = g.createRadialGradient(n / 2, n * 0.62, 0, n / 2, n * 0.55, n * 0.48); r.addColorStop(0, "rgba(255,255,230,1)"); r.addColorStop(0.3, "rgba(255,200,90,.9)"); r.addColorStop(0.7, "rgba(255,90,20,.35)"); r.addColorStop(1, "rgba(255,40,0,0)"); g.fillStyle = r; g.fillRect(0, 0, n, n); }),
};
function ps(name, cap, tex, additive) {
  const p = new ParticleSystem(name, cap, scene); p.particleTexture = tex; p.blendMode = additive ? ParticleSystem.BLENDMODE_ADD : ParticleSystem.BLENDMODE_STANDARD;
  p.isLocal = false; p.updateSpeed = 1 / 60;
  // The sprites are shared: a finished burst disposing "its" texture (Babylon's default) blanked every other system.
  const dispose = p.dispose.bind(p); p.dispose = (_tex, ...rest) => dispose(false, ...rest); return p;
}
const loops = new Map();
function loop(key, make, at) {
  let s = loops.get(key);
  if (!s) { s = make(); s.emitter = at.clone(); s.start(); loops.set(key, s); }
  s.emitter.copyFrom ? s.emitter.copyFrom(at) : (s.emitter = at.clone()); s.__seen = performance.now(); return s;
}
function sweep() { const now = performance.now(); for (const [k, s] of loops) if (now - s.__seen > 600) { s.__light?.dispose(); s.stop(); s.disposeOnStop = true; loops.delete(k); } }
const steam = () => { const p = ps("steam", 200, TX.puff, false);
  p.minLifeTime = 2.2; p.maxLifeTime = 3.4; p.emitRate = 6; p.minSize = 0.35; p.maxSize = 0.55; p.minEmitPower = 0.35; p.maxEmitPower = 0.6;
  p.direction1 = new Vector3(-0.1, 1, -0.1); p.direction2 = new Vector3(0.1, 1, 0.1); p.gravity = new Vector3(0.3, 0.15, 0.2);
  p.color1 = new Color4(0.95, 0.93, 0.9, 0.5); p.color2 = new Color4(1, 1, 1, 0.4); p.colorDead = new Color4(1, 1, 1, 0);
  p.addSizeGradient(0, 0.6); p.addSizeGradient(1, 2.6); p.minInitialRotation = 0; p.maxInitialRotation = 6.28; return p; };
function fire(level, footprint) {
  const f = ps("fire", 600, TX.flame, true);
  f.minLifeTime = 0.35; f.maxLifeTime = 0.8; f.emitRate = (10 + level * 55) * footprint / 2; f.minSize = (0.45 + level * 0.9) * 0.6; f.maxSize = 0.45 + level * 0.9;
  f.minEmitPower = 0.5; f.maxEmitPower = 1.1 + level; f.direction1 = new Vector3(-0.15, 1, -0.15); f.direction2 = new Vector3(0.15, 1, 0.15);
  f.createSphereEmitter(footprint * 0.35 * (0.5 + level * 0.5));
  f.color1 = new Color4(3.2, 1.9, 1.1, 1); f.color2 = new Color4(3, 1.2, 0.5, 1); f.colorDead = new Color4(1, 0.2, 0, 0);
  f.addSizeGradient(0, 1); f.addSizeGradient(1, 0.2);
  const s = ps("smoke", 400, TX.puff, false);
  s.minLifeTime = 2; s.maxLifeTime = 3.5; s.emitRate = (5 + level * 18) * footprint / 2; s.minSize = 0.7; s.maxSize = 1.1 + level * 0.9; s.minEmitPower = 0.5; s.maxEmitPower = 0.9;
  s.createSphereEmitter(footprint * 0.3); s.direction1 = new Vector3(-0.1, 1, -0.1); s.direction2 = new Vector3(0.2, 1, 0.1); s.gravity = new Vector3(0.3, 0.35, 0.15);
  s.color1 = new Color4(0.12, 0.1, 0.09, 0.8); s.color2 = new Color4(0.24, 0.21, 0.19, 0.7); s.colorDead = new Color4(0.3, 0.3, 0.3, 0);
  s.addSizeGradient(0, 0.7); s.addSizeGradient(1, 3);
  f.__smoke = s; return f;
}
function fireAt(key, level, at, footprint) {
  const k = `${key}:${Math.round(level * 3)}`;
  const f = loop(k, () => { const x = fire(level, footprint); x.__light = new PointLight("firelight", at.add(new Vector3(0, 0.8, 0)), scene); x.__light.diffuse = new Color3(1, 0.54, 0.23); x.__light.range = 3 + footprint * 2; x.__level = level; return x; }, at);
  loop(k + ":s", () => f.__smoke, at.add(new Vector3(0, 0.4, 0)));
  if (f.__light) f.__light.intensity = (2 + f.__level * 6) * (0.75 + 0.25 * Math.sin(performance.now() * 0.023) * Math.sin(performance.now() * 0.011));
}
function burst(make, at, life) { const p = make(); p.emitter = at.clone(); p.targetStopDuration = 0.12; p.disposeOnStop = true; p.start(); }
function explosion(at, size) {
  burst(() => { const p = ps("blast", 80, TX.flame, true); p.minLifeTime = 0.25; p.maxLifeTime = 0.55; p.minSize = 0.5 * size; p.maxSize = 1.1 * size; p.minEmitPower = 0.8 * size; p.maxEmitPower = 2.2 * size; p.createSphereEmitter(0.2 * size); p.manualEmitCount = Math.round(16 * size); p.color1 = new Color4(4, 3, 1.6, 1); p.color2 = new Color4(3.5, 1.5, 0.5, 1); p.colorDead = new Color4(0.5, 0.1, 0, 0); p.addSizeGradient(0, 0.6); p.addSizeGradient(1, 1.6); return p; }, at);
  burst(() => { const p = ps("sparks", 80, TX.dot, true); p.minLifeTime = 0.3; p.maxLifeTime = 0.8; p.minSize = 0.06; p.maxSize = 0.12; p.minEmitPower = 3 * size; p.maxEmitPower = 6 * size; p.createSphereEmitter(0.1); p.manualEmitCount = Math.round(18 * size); p.gravity = new Vector3(0, -6, 0); p.color1 = new Color4(4, 3, 1.5, 1); p.color2 = new Color4(4, 2, 0.8, 1); p.colorDead = new Color4(1, 0.3, 0, 0); p.billboardMode = ParticleSystem.BILLBOARDMODE_STRETCHED; return p; }, at);
  burst(() => { const p = ps("blastsmoke", 60, TX.puff, false); p.minLifeTime = 1.4; p.maxLifeTime = 2.6; p.minSize = 0.6 * size; p.maxSize = 1.1 * size; p.minEmitPower = 0.4; p.maxEmitPower = 1.2 * size; p.createSphereEmitter(0.3 * size); p.manualEmitCount = Math.round(10 * size); p.gravity = new Vector3(0.2, 0.5, 0.1); p.color1 = new Color4(0.16, 0.13, 0.11, 0.85); p.color2 = new Color4(0.32, 0.28, 0.24, 0.7); p.colorDead = new Color4(0.3, 0.3, 0.3, 0); p.addSizeGradient(0, 0.8); p.addSizeGradient(1, 2.6); return p; }, at);
  const l = new PointLight("blastlight", at.add(new Vector3(0, 1.2, 0)), scene); l.diffuse = new Color3(1, 0.6, 0.23); l.range = size > 1.4 ? 9 : 6;
  const peak = size > 1.4 ? 18 : 9, t0 = performance.now();
  const obs = scene.onBeforeRenderObservable.add(() => { const k = (performance.now() - t0) / 600; if (k >= 1) { l.dispose(); scene.onBeforeRenderObservable.remove(obs); } else l.intensity = peak * (1 - k) * (1 - k); });
}
function flash(at) { burst(() => { const p = ps("flash", 4, TX.dot, true); p.minLifeTime = p.maxLifeTime = 0.09; p.minSize = p.maxSize = 0.35; p.minEmitPower = p.maxEmitPower = 0; p.manualEmitCount = 1; p.color1 = p.color2 = new Color4(4, 3, 1.8, 1); p.colorDead = new Color4(1, 0.6, 0.2, 0); return p; }, at); }
const dust = () => { const p = ps("dust", 60, TX.puff, false); p.minLifeTime = 0.6; p.maxLifeTime = 1.1; p.emitRate = 9; p.minSize = 0.2; p.maxSize = 0.35; p.minEmitPower = 0.1; p.maxEmitPower = 0.3; p.color1 = new Color4(0.62, 0.53, 0.4, 0.45); p.color2 = new Color4(0.7, 0.6, 0.46, 0.3); p.colorDead = new Color4(0.7, 0.6, 0.46, 0); p.addSizeGradient(0, 0.7); p.addSizeGradient(1, 2); return p; };

// ---------------------------------------------------------------- entities: [id, key, team, x, y, hp%, facing, build%, sizeX, fuel%]
const ents = new Map();
const WINDOWS = [[0, 0.10], [0.10, 0.45], [0.45, 0.75], [0.75, 0.95]], easeOut = (k) => 1 - Math.pow(1 - k, 3);
function upsert(e, now) {
  const [id, key, team, x, y, hp, facing, build, size] = e;
  let s = ents.get(id);
  if (!s) {
    s = { key, team, a: { x, y, f: facing, t: now }, b: { x, y, f: facing, t: now }, build, hp, size };
    s.root = new Mesh(`e${id}`, scene); s.root.metadata = { id };
    ents.set(id, s);
    model(key).then((c) => {
      if (!ents.has(id)) return;
      let body;
      if (c) {
        const inst = c.instantiateModelsToScene((n) => n, false, { doNotInstantiate: true });
        body = inst.rootNodes[0];
        for (const m of body.getChildMeshes(false)) {
          if (m.material?.name?.startsWith("M_Team")) m.material = teamMat(m.material, team);
          m.receiveShadows = true; shadows.addShadowCaster(m, false); m.metadata = { id };
        }
        s.stages = [0, 1, 2, 3].map((i) => {
          const n = body.getDescendants(false).find((d) => d.name === `stage_${i}`); if (!n) return null;
          const { max } = n.getHierarchyBoundingVectors(true);
          return { n, y0: n.position.y, h: (i === 0 ? 0.15 : Math.max(0.05, max.y)) + 0.02 };
        });
        if (key === "power_plant") {
          s.vents = [];
          body.computeWorldMatrix(true); // not parented yet: "world" here is the model's own space
          for (const m of body.getChildMeshes(false)) if (/M_E_Cyan/.test(m.name) || /M_E_Cyan/.test(m.material?.name ?? "")) {
            m.computeWorldMatrix(true); m.refreshBoundingInfo();
            const bb = m.getBoundingInfo().boundingBox, mn = bb.minimumWorld, mx = bb.maximumWorld;
            const w = mx.x - mn.x, d = mx.z - mn.z;
            const local = (px, pz) => new Vector3(px, mx.y, pz);
            if (Math.max(w, d) > 0.6) { if (w >= d) s.vents.push(local(mn.x + w * 0.25, (mn.z + mx.z) / 2), local(mx.x - w * 0.25, (mn.z + mx.z) / 2)); else s.vents.push(local((mn.x + mx.x) / 2, mn.z + d * 0.25), local((mn.x + mx.x) / 2, mx.z - d * 0.25)); }
            else s.vents.push(local((mn.x + mx.x) / 2, (mn.z + mx.z) / 2));
          }
        }
      } else { body = CreateBox("ph", { width: 0.6, height: 0.4, depth: 0.8 }, scene); body.position.y = 0.2; const pm = new PBRMaterial("ph", scene); pm.albedoColor = team >= 0 ? TEAM[team % TEAM.length] : NEUTRAL; body.material = pm; body.metadata = { id }; }
      body.parent = s.root; s.body = body;
    });
  }
  if (s.team !== team && s.body) { for (const m of s.body.getChildMeshes(false)) if (m.material?.name?.startsWith("M_Team")) m.material = teamMat(m.material, team); s.team = team; }
  s.a = { ...s.b }; s.b = { x, y, f: facing, t: now }; s.build = build; s.hp = hp; s.size = size;
}
const shotsSeen = new Set();
function effect(e) {
  const [seq, type, x, y, x2, y2, team] = e;
  if (shotsSeen.has(seq)) return; shotsSeen.add(seq);
  if (type === "shot" || type === "fire") {
    flash(new Vector3(x, 0.55, y));
    const l = CreateLines("tracer", { points: [new Vector3(x, 0.45, y), new Vector3(x2, 0.35, y2)] }, scene); l.color = team >= 0 ? TEAM[team % TEAM.length].toGammaSpace() : new Color3(1, 0.88, 0.6); l.isPickable = false;
    setTimeout(() => l.dispose(), 180);
  } else if (type === "destroyed") {
    const dead = ents.get(e[7]), structure = dead && dead.size > 0;
    explosion(new Vector3(x, 0.3, y), structure ? 1.8 : dead && INFANTRY.test(dead.key) ? 0.4 : 1);
  }
}

// ---------------------------------------------------------------- data: the WebSocket feed (or polling)
let frames = 0, lastTick = -1, bytesWindow = [], who = "";
function applyMap(m, entities) {
  if (!booted) BOOT.step("map", "run");
  buildTerrain(m); oreVersion = m.ore_version;
  if (!booted) BOOT.step("map", "ok", `${m.w}×${m.h} tiles`);
  if (!frames && entities) {
    const team = Number(Q.get("team") ?? 0), mine = entities.filter((e) => e[2] === team);
    const at = Q.has("x") ? [0, 0, 0, +Q.get("x"), +Q.get("y")] : mine.find((e) => e[1] === "command_center") ?? mine[0] ?? entities[0];
    if (at) { view.x = at[3]; view.y = at[4]; }
    if (Q.has("size")) view.size = +Q.get("size");
  }
}
function applyFrame(f, size) {
  if (!booted) BOOT.step("data", "ok", `${(size / 1024).toFixed(1)} KB · ${f.entities.length} things`);
  if (f.tick === lastTick) return; lastTick = f.tick;
  const now = performance.now(), live = new Set();
  for (const e of f.entities) { upsert(e, now); live.add(e[0]); }
  for (const e of f.effects ?? []) effect(e);
  for (const [id, s] of ents) if (!live.has(id)) { s.root.dispose(false, false); ents.delete(id); }
  if (f.shroud !== undefined) updateFog(f.shroud);
  frames++;
  if (f.you?.flavor) who = f.you.flavor;
  document.getElementById("who").textContent = `· ${who} · game ${Math.floor((f.time_s ?? 0) / 60)}:${String(Math.floor((f.time_s ?? 0) % 60)).padStart(2, "0")} · ${backend}`;
}
const DATA = (p) => `./data/${p}?${Q.has("host") ? `host=${Q.get("host")}&room=${Q.get("room")}` : `api=${Q.get("api") ?? 7957}`}&team=${Q.get("team") ?? 0}`;
function feed() {
  const wsBase = location.port === "8458" ? `wss://${location.hostname}:8459` : "ws://127.0.0.1:7427";
  const qs = Q.has("host") ? `host=${Q.get("host")}&room=${Q.get("room")}` : `api=${Q.get("api") ?? 7957}`;
  const ws = new WebSocket(`${wsBase}/feed?${qs}&team=${Q.get("team") ?? 0}`);
  const table = new Map(); let head = {}, shroud = null, pendingMap = null;
  ws.onmessage = (ev) => {
    const txt = ev.data; bytesWindow.push([performance.now(), txt.length]);
    const m = JSON.parse(txt);
    if (m.t === "map") { pendingMap = m.map; if (table.size) { applyMap(pendingMap, [...table.values()]); pendingMap = null; } return; }
    if (m.t === "error") { if (!booted) BOOT.step("data", "run", `retrying: ${m.error}`); return; }
    if (m.t === "full") { table.clear(); for (const e of m.ents) table.set(e[0], e); head = m.head ?? {}; shroud = m.shroud; }
    else if (m.t === "delta") { for (const e of m.add) table.set(e[0], e); for (const e of m.upd) table.set(e[0], e); for (const id of m.del) table.delete(id); if (m.head) head = m.head; if (m.shroud !== undefined) shroud = m.shroud; head.time_s = m.time_s; }
    if (pendingMap) { applyMap(pendingMap, [...table.values()]); pendingMap = null; }
    applyFrame({ tick: m.tick, time_s: head.time_s, you: head.you, entities: [...table.values()], effects: m.fx ?? [], shroud }, txt.length);
  };
  ws.onclose = () => { if (!booted) BOOT.step("data", "run", "reconnecting…"); setTimeout(feed, 1500); };
}
async function poll() {
  try {
    const txt = await (await fetch(DATA("frame"), { cache: "no-store" })).text(); bytesWindow.push([performance.now(), txt.length]);
    const f = JSON.parse(txt); if (f.ok === false) throw new Error(f.error);
    if (!fogTex || f.ore_version !== oreVersion) applyMap(await (await fetch(DATA("map"), { cache: "no-store" })).json(), f.entities);
    applyFrame(f, txt.length);
  } catch (err) { if (!booted) BOOT.step("data", "run", `retrying: ${err.message}`); }
  setTimeout(poll, 250);
}

// ---------------------------------------------------------------- input: drag pan, pinch/wheel zoom, rotate, tap to follow
const pts = new Map(); let pinch = 0, moved = 0;
function pan(dx, dy) {
  const s = view.size, a = engine.getRenderWidth() / engine.getRenderHeight(), w = canvas.clientWidth, h = canvas.clientHeight;
  const right = cam.getDirection(new Vector3(1, 0, 0)); right.y = 0; right.normalize();
  const up = cam.getDirection(new Vector3(0, 1, 0)); up.y = 0; up.normalize();
  const d = right.scale(-dx / w * 2 * s * a).add(up.scale(dy / h * 2 * s / Math.sin(PITCH)));
  view.x += d.x; view.y += d.z; view.followId = 0; showFollow();
}
const zoom = (f) => { view.size = Math.min(40, Math.max(4, view.size * f)); };
canvas.addEventListener("pointerdown", (e) => { canvas.setPointerCapture(e.pointerId); pts.set(e.pointerId, { x: e.clientX, y: e.clientY }); if (pts.size === 1) moved = 0; });
canvas.addEventListener("pointermove", (e) => {
  const p = pts.get(e.pointerId); if (!p) return;
  if (pts.size === 1) { moved += Math.abs(e.clientX - p.x) + Math.abs(e.clientY - p.y); if (moved > 4) pan(e.clientX - p.x, e.clientY - p.y); }
  pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
  if (pts.size === 2) { const [a, b] = [...pts.values()], d = Math.hypot(a.x - b.x, a.y - b.y); if (pinch) zoom(pinch / d); pinch = d; moved = 99; }
});
canvas.addEventListener("pointerup", (e) => { if (pts.size === 1 && moved < 6) pick(e); pts.delete(e.pointerId); if (pts.size < 2) pinch = 0; });
canvas.addEventListener("pointercancel", (e) => { pts.delete(e.pointerId); pinch = 0; });
addEventListener("wheel", (e) => { e.preventDefault(); zoom(e.deltaY < 0 ? 0.9 : 1.11); }, { passive: false });
addEventListener("keydown", (e) => { const k = e.key.toLowerCase(); if (k === "a" || k === "arrowleft") pan(60, 0); if (k === "d" || k === "arrowright") pan(-60, 0); if (k === "w" || k === "arrowup") pan(0, 60); if (k === "s" || k === "arrowdown") pan(0, -60); if (k === "q") view.yaw -= 15; if (k === "e") view.yaw += 15; if (k === "escape") { view.followId = 0; showFollow(); } });
document.querySelectorAll("#pad button").forEach((b) => b.onclick = () => { if (b.dataset.z) zoom(+b.dataset.z); if (b.dataset.y) view.yaw += +b.dataset.y; });
function pick(e) {
  const r = canvas.getBoundingClientRect();
  const hit = scene.pick((e.clientX - r.left) * engine.getRenderWidth() / r.width, (e.clientY - r.top) * engine.getRenderHeight() / r.height, (m) => m.metadata?.id != null);
  const id = hit?.pickedMesh?.metadata?.id, s = id != null && ents.get(id);
  view.followId = s && s.size === 0 ? id : 0; showFollow();
}
function showFollow() { const f = document.getElementById("fol"), s = ents.get(view.followId); f.style.display = s ? "block" : "none"; if (s) f.textContent = `following ${s.key} #${view.followId} · tap empty ground or Esc to stop`; }

// ---------------------------------------------------------------- frame loop
let fpsN = 0, fpsT = 0;
const lerpAngle = (a, b, t) => { let d = b - a; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return a + d * t; };
const t0boot = performance.now();
scene.onBeforeRenderObservable.add(() => {
  const dt = engine.getDeltaTime() / 1000, now = performance.now(), at = now - DELAY_MS;
  if (!booted && fogTex && (modelsDone >= modelsAsked || now - t0boot > 15000)) { booted = true; if (!modelsAsked) BOOT.step("models", "ok", "none needed yet"); BOOT.ready(); }
  for (const [id, s] of ents) {
    const span = Math.max(1, s.b.t - s.a.t), t = Math.min(1, Math.max(0, (at - s.a.t) / span));
    const x = s.a.x + (s.b.x - s.a.x) * t, y = s.a.y + (s.b.y - s.a.y) * t, f = lerpAngle(s.a.f, s.b.f, t);
    s.root.position.set(x, AIR.has(s.key) ? 2.2 : 0, y);
    s.root.rotation.y = s.size > 0 ? 0 : Math.PI / 2 - f; // Unity: Euler(0, 90° − facing); structures never turn
    if (s.stages) {
      const target = s.build < 0 ? 1 : s.build / 100; s.shown = s.shown == null ? target : s.shown + (target - s.shown) * Math.min(1, dt * 4);
      s.stages.forEach((st, i) => { if (!st) return; const [a, b] = WINDOWS[i], k = Math.min(1, Math.max(0, (s.shown - a) / (b - a))); st.n.position.y = st.y0 - st.h * (1 - easeOut(k)); st.n.setEnabled(k > 0 || i === 0); });
    }
    if (s.vents && (s.build < 0 || s.build >= 100)) s.vents.forEach((v, i) => loop(`v${id}:${i}`, steam, Vector3.TransformCoordinates(v, s.root.computeWorldMatrix(true))));
    if (s.size > 0 && s.build >= 100 && s.hp < 30) fireAt(`f${id}`, Math.min(1, (30 - s.hp) / 30), new Vector3(x, 0.5, y), s.size);
    const speed = Math.hypot(s.b.x - s.a.x, s.b.y - s.a.y) / Math.max(0.05, (s.b.t - s.a.t) / 1000);
    if (s.size === 0 && !AIR.has(s.key) && !INFANTRY.test(s.key) && speed > 0.25) loop(`d${id}`, dust, new Vector3(x, 0.1, y));
    if (id === view.followId) { view.x += (x - view.x) * Math.min(1, dt * 6); view.y += (y - view.y) * Math.min(1, dt * 6); }
  }
  sweep();
  placeCamera();
  fpsN++; fpsT += dt;
  if (fpsT >= 1) {
    const fps = fpsN / fpsT; fpsN = 0; fpsT = 0;
    const cut = now - 5000; bytesWindow = bytesWindow.filter(([t]) => t > cut); const bps = bytesWindow.reduce((s, [, n]) => s + n, 0) / 5;
    document.getElementById("stats").textContent = `${fps.toFixed(0)} fps · ${ents.size} things · ${(bps / 1024).toFixed(1)} KB/s data · models ${(modelBytes / 1024).toFixed(0)} KB · ${backend}`;
    window.__perf = { fps, bps, ents: ents.size, modelBytes, backend };
  }
});
addEventListener("resize", () => engine.resize());
window.__psum = () => { const all = scene.particleSystems; return JSON.stringify({ n: all.length, notReady: all.filter((p) => !p.isReady()).length, active: all.reduce((a, p) => a + p.getActiveCount(), 0), byName: Object.fromEntries([...new Set(all.map((p) => p.name))].map((n) => [n, all.filter((p) => p.name === n).length])) }); };
window.__ps2 = () => { const p = scene.particleSystems[0]; const t = p.particleTexture; return JSON.stringify({ internal: !!t._texture, internalReady: t._texture?.isReady, delay: t.delayLoadState, fogReady: fogTex?.isReady(), fogInternal: fogTex?._texture?.isReady, tex: p.particleTexture?.isReady(), texClass: p.particleTexture?.getClassName(), effect: !!p._drawWrappers, shadersLoaded: p._shadersLoaded ?? null, shaderLang: engine.isWebGPU ? "wgsl" : "glsl" }); };
window.__ps = () => JSON.stringify(scene.particleSystems.slice(0, 8).map((p) => ({ n: p.name, active: p.getActiveCount(), started: p.isStarted(), ready: p.isReady(), em: p.emitter?.x != null ? [p.emitter.x, p.emitter.y, p.emitter.z].map((v) => +v.toFixed(1)) : String(p.emitter), cap: p.getCapacity() })));
window.__dbg = () => JSON.stringify({ loops: loops.size, vents: [...ents.values()].filter((s) => s.vents).map((s) => s.vents.length), ssao: !!ssao, casters: shadows.getShadowMap()?.renderList?.length, particles: scene.particleSystems.length });
if (Q.get("feed") === "ws") feed(); else poll();
engine.runRenderLoop(() => scene.render());
