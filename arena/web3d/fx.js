// Effects for the browser renderer: post-processing (ambient occlusion, bloom, anti-aliasing, grade) and particles
// (steam, smoke, fire, explosions, muzzle flashes, dust), modelled on the Unity view's FxSystems and PezPost.
import * as THREE from "three";
import { EffectComposer, RenderPass, EffectPass, BloomEffect, SMAAEffect, VignetteEffect, ToneMappingEffect, ToneMappingMode, HueSaturationEffect, BrightnessContrastEffect } from "postprocessing";
import { N8AOPostPass } from "n8ao";
import { BatchedRenderer, ParticleSystem, RenderMode } from "three.quarks";
import { ConstantValue, IntervalValue, ColorRange, Vector4, Vector3 as QV3, SphereEmitter, ConeEmitter, PointEmitter, SizeOverLife, ColorOverLife, PiecewiseBezier, Bezier, ApplyForce, Gradient, RotationOverLife } from "quarks.core";

// ---------------------------------------------------------------- post-processing (PezPost: AO, bloom, a light grade)
export function makeComposer(renderer, scene, camera) {
  renderer.toneMapping = THREE.NoToneMapping; // the ToneMappingEffect does it, after AO and bloom
  const composer = new EffectComposer(renderer, { frameBufferType: THREE.HalfFloatType, multisampling: 0 });
  composer.addPass(new RenderPass(scene, camera));
  const ao = new N8AOPostPass(scene, camera, innerWidth, innerHeight);
  Object.assign(ao.configuration, { aoRadius: 1.6, distanceFalloff: 0.8, intensity: 4.5, color: new THREE.Color(0x1a120c), halfRes: true, depthAwareUpsampling: true });
  ao.setQualityMode("Medium");
  composer.addPass(ao);
  const bloom = new BloomEffect({ intensity: 1.8, luminanceThreshold: 1.6, luminanceSmoothing: 0.15, mipmapBlur: true, radius: 0.85 }); // above any sunlit wall, so only the glowing parts bloom (like Babylon's GlowLayer)
  const grade = new EffectPass(camera,
    bloom,
    new BrightnessContrastEffect({ brightness: -0.03, contrast: 0.1 }),
    new HueSaturationEffect({ saturation: 0.08 }),
    new ToneMappingEffect({ mode: ToneMappingMode.NEUTRAL }),
    new VignetteEffect({ offset: 0.32, darkness: 0.42 }),
    new SMAAEffect());
  grade.dithering = true; // smoke's soft gradients band in 8 bits without it
  composer.addPass(grade);
  addEventListener("resize", () => composer.setSize(innerWidth, innerHeight));
  return { composer, ao, bloom };
}

/** The solid scene's depth, without particles or fog, at half resolution: what soft particles fade against.
 * A separate render because the main pass can't read the depth buffer it's drawing into. */
export class DepthPrepass {
  constructor(renderer) {
    this.renderer = renderer;
    this.rt = new THREE.WebGLRenderTarget(1, 1, { depthBuffer: true, depthTexture: new THREE.DepthTexture(1, 1) });
    this.mat = new THREE.MeshBasicMaterial({ colorWrite: false });
    this.size = new THREE.Vector2();
  }
  get texture() { return this.rt.depthTexture; }
  render(scene, camera, hide) {
    const r = this.renderer; r.getDrawingBufferSize(this.size);
    const w = Math.max(1, Math.ceil(this.size.x / 2)), h = Math.max(1, Math.ceil(this.size.y / 2));
    if (this.rt.width !== w || this.rt.height !== h) this.rt.setSize(w, h);
    const shown = hide.map((o) => o.visible); hide.forEach((o) => { o.visible = false; });
    const om = scene.overrideMaterial, auto = r.shadowMap.autoUpdate, prev = r.getRenderTarget();
    scene.overrideMaterial = this.mat; r.shadowMap.autoUpdate = false; // the shadows are drawn once, by the main pass
    r.setRenderTarget(this.rt); r.clear(); r.render(scene, camera); r.setRenderTarget(prev);
    scene.overrideMaterial = om; r.shadowMap.autoUpdate = auto; hide.forEach((o, i) => { o.visible = shown[i]; });
  }
}

// A soft sky for reflections on metal and glass (Look.cs SkyCube: zenith, upper, horizon, ground).
export function skyEnvironment(renderer) {
  const c = document.createElement("canvas"); c.width = 256; c.height = 128;
  const g = c.getContext("2d"), grad = g.createLinearGradient(0, 0, 0, 128);
  grad.addColorStop(0, "#9aa1ad"); grad.addColorStop(0.35, "#bdbfc4"); grad.addColorStop(0.5, "#e6ddcf"); grad.addColorStop(0.52, "#574a39"); grad.addColorStop(1, "#2b241c");
  g.fillStyle = grad; g.fillRect(0, 0, 256, 128);
  const tex = new THREE.CanvasTexture(c); tex.mapping = THREE.EquirectangularReflectionMapping; tex.colorSpace = THREE.SRGBColorSpace;
  const pm = new THREE.PMREMGenerator(renderer);
  const env = pm.fromEquirectangular(tex).texture; pm.dispose(); tex.dispose();
  return env;
}

// ---------------------------------------------------------------- particle textures (drawn once)
function sprite(draw, size = 128) {
  const c = document.createElement("canvas"); c.width = c.height = size;
  draw(c.getContext("2d"), size);
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; return t;
}
const softDot = sprite((g, n) => { const r = g.createRadialGradient(n / 2, n / 2, 0, n / 2, n / 2, n / 2); r.addColorStop(0, "rgba(255,255,255,1)"); r.addColorStop(0.35, "rgba(255,255,255,.55)"); r.addColorStop(1, "rgba(255,255,255,0)"); g.fillStyle = r; g.fillRect(0, 0, n, n); });
const puff = sprite((g, n) => {
  // a lumpy smoke puff: overlapping soft blobs
  for (let i = 0; i < 9; i++) {
    const a = Math.random() * 6.28, d = Math.random() * n * 0.18, x = n / 2 + Math.cos(a) * d, y = n / 2 + Math.sin(a) * d, rr = n * (0.18 + Math.random() * 0.16);
    const r = g.createRadialGradient(x, y, 0, x, y, rr); r.addColorStop(0, "rgba(255,255,255,.55)"); r.addColorStop(1, "rgba(255,255,255,0)"); g.fillStyle = r; g.fillRect(0, 0, n, n);
  }
});
const flame = sprite((g, n) => { const r = g.createRadialGradient(n / 2, n * 0.62, 0, n / 2, n * 0.55, n * 0.48); r.addColorStop(0, "rgba(255,255,230,1)"); r.addColorStop(0.3, "rgba(255,200,90,.9)"); r.addColorStop(0.7, "rgba(255,90,20,.35)"); r.addColorStop(1, "rgba(255,40,0,0)"); g.fillStyle = r; g.fillRect(0, 0, n, n); });

const mat = (map, additive) => new THREE.MeshBasicMaterial({ map, transparent: true, depthWrite: false, blending: additive ? THREE.AdditiveBlending : THREE.NormalBlending, color: 0xffffff });
const MAT = { smoke: mat(puff, false), steam: mat(puff, false), fire: mat(flame, true), glow: mat(softDot, true), spark: mat(softDot, true) };
// Fire, flashes and sparks are brighter than white (HDR) so the bloom catches them, as Unity's do.
MAT.fire.color.setRGB(3.2, 1.9, 1.1); MAT.glow.color.setRGB(4, 3, 1.8); MAT.spark.color.setRGB(4, 2.6, 1.2);
// Soft particles: each sprite fades out where it meets a roof, wall or the ground instead of being cut off in a hard line.
// Needs the solid scene's depth (DepthPrepass); fades over the last 0.7 units before a surface.
const SOFT = { softParticles: true, softNearFade: 0, softFarFade: 0.7 };
const V4 = (r, g, b, a) => new Vector4(r, g, b, a);
const fadeOut = () => new PiecewiseBezier([[new Bezier(1, 0.9, 0.5, 0), 0]]);
const grow = (a, b) => new PiecewiseBezier([[new Bezier(a, (a + b) / 2, b, b), 0]]);

// ---------------------------------------------------------------- the particle world
export class Particles {
  constructor(parent, scene) {
    this.batch = new BatchedRenderer();
    this.parent = parent;            // the (mirrored) world group: emitters sit in sim coordinates like everything else
    scene.add(this.batch);           // but world-space particles are drawn from the scene root (inside the group they'd be mirrored twice)
    this.loops = new Map();          // key -> system (steam stacks, fires) that live as long as their source
    this.bursts = [];                // one-shot systems, removed when done
  }
  add(sys, at) { sys.emitter.position.copy(at); this.parent.add(sys.emitter); this.batch.addSystem(sys); return sys; }
  remove(sys) { this.batch.deleteSystem(sys); sys.emitter.removeFromParent(); sys.dispose?.(); }
  update(dt) {
    this.batch.update(dt);
    for (let i = this.bursts.length - 1; i >= 0; i--) { const b = this.bursts[i]; b.t += dt; if (b.t > b.life) { this.remove(b.sys); this.bursts.splice(i, 1); } }
  }

  /** A looping source keyed by name: created once, moved each frame, stopped when the key stops being asked for. */
  loop(key, make, at) {
    let s = this.loops.get(key);
    if (!s) { s = this.add(make(), at); s.__seen = 0; this.loops.set(key, s); }
    s.emitter.position.copy(at); s.__seen = performance.now();
    return s;
  }
  sweep() { const now = performance.now(); for (const [k, s] of this.loops) if (now - s.__seen > 600) { s.__light?.removeFromParent(); this.remove(s); this.loops.delete(k); } }

  // Steam off a power plant stack: pale, slow, rising and spreading, drifting downwind (FxSystems.Wind).
  steam() {
    return new ParticleSystem({ ...SOFT,
      duration: 1, looping: true, worldSpace: true, startLife: new IntervalValue(2.2, 3.4), startSpeed: new IntervalValue(0.35, 0.6),
      startSize: new IntervalValue(0.35, 0.55), startColor: new ColorRange(V4(0.92, 0.9, 0.86, 0.55), V4(1, 1, 1, 0.4)),
      emissionOverTime: new ConstantValue(5), shape: new ConeEmitter({ radius: 0.08, angle: 0.18 }), material: MAT.steam, renderMode: RenderMode.BillBoard,
      startRotation: new IntervalValue(0, 6.28),
      behaviors: [new SizeOverLife(grow(0.6, 2.6)), new ColorOverLife(new Gradient([[new QV3(1, 1, 1), 0], [new QV3(0.95, 0.93, 0.9), 1]], [[0.0, 0], [0.55, 0.15], [0, 1]])), new ApplyForce(new QV3(0.35, 0.15, 0.2), new ConstantValue(0.25)), new RotationOverLife(new IntervalValue(-0.4, 0.4))],
    });
  }

  // A building on fire, by how hurt it is (BuildingFire: smoulder, standing, raging): flames, then dark smoke.
  fire(level, footprint = 2) { // level 0..1 (smouldering .. raging), footprint in tiles
    const rate = (10 + level * 55) * footprint / 2, size = 0.45 + level * 0.9;
    const f = new ParticleSystem({ ...SOFT,
      duration: 1, looping: true, worldSpace: true, startLife: new IntervalValue(0.35, 0.8), startSpeed: new IntervalValue(0.5, 1.1 + level),
      startSize: new IntervalValue(size * 0.6, size), startColor: new ColorRange(V4(1, 0.75, 0.35, 1), V4(1, 0.45, 0.15, 1)),
      emissionOverTime: new ConstantValue(rate), shape: new SphereEmitter({ radius: footprint * 0.35 * (0.5 + level * 0.5), thickness: 1 }), material: MAT.fire, renderMode: RenderMode.BillBoard,
      behaviors: [new SizeOverLife(grow(1, 0.2)), new ColorOverLife(new Gradient([[new QV3(1, 0.9, 0.6), 0], [new QV3(1, 0.35, 0.08), 1]], [[1, 0], [0, 1]]))],
    });
    const s = new ParticleSystem({ ...SOFT,
      duration: 1, looping: true, worldSpace: true, startLife: new IntervalValue(2, 3.5), startSpeed: new IntervalValue(0.5, 0.9),
      startSize: new IntervalValue(0.7, 1.1 + level * 0.9), startColor: new ColorRange(V4(0.12, 0.1, 0.09, 0.85), V4(0.24, 0.21, 0.19, 0.75)),
      emissionOverTime: new ConstantValue((5 + level * 18) * footprint / 2), shape: new SphereEmitter({ radius: footprint * 0.3, thickness: 1 }), material: MAT.smoke, renderMode: RenderMode.BillBoard,
      startRotation: new IntervalValue(0, 6.28),
      behaviors: [new SizeOverLife(grow(0.7, 3)), new ColorOverLife(new Gradient([[new QV3(1, 1, 1), 0], [new QV3(0.7, 0.7, 0.7), 1]], [[0, 0], [0.75, 0.1], [0, 1]])), new ApplyForce(new QV3(0.3, 1, 0.15), new ConstantValue(0.35))],
    });
    f.__smoke = s;
    return f;
  }
  fireAt(key, level, at, footprint = 2) {
    const k = `${key}:${Math.round(level * 3)}`; // a new system when the fire grows a stage
    const s = this.loop(k, () => {
      const f = this.fire(level, footprint); this.add(f.__smoke, at); this.loops.set(k + ":s", f.__smoke);
      // a flickering firelight on the ground and walls
      const l = new THREE.PointLight(0xff8a3a, 0, 3 + footprint * 2); l.position.copy(at); l.position.y += 0.8; this.parent.add(l); f.__light = l; f.__level = level;
      return f;
    }, at);
    if (s.__light) { s.__light.intensity = (6 + s.__level * 22) * (0.75 + 0.25 * Math.sin(performance.now() * 0.023) * Math.sin(performance.now() * 0.011)); }
    const sm = this.loops.get(k + ":s"); if (sm) { sm.emitter.position.copy(at); sm.emitter.position.y += 0.4; sm.__seen = performance.now(); }
    return s;
  }

  // A blast: fireball, sparks, rolling smoke, a shockwave of dust (FxSystems.Blast), sized by what died.
  explosion(at, size = 1) {
    const life = 2.8;
    const ball = new ParticleSystem({ ...SOFT,
      duration: 0.15, looping: false, worldSpace: true, startLife: new IntervalValue(0.25, 0.55), startSpeed: new IntervalValue(0.8 * size, 2.2 * size),
      startSize: new IntervalValue(0.5 * size, 1.1 * size), startColor: new ColorRange(V4(1, 0.85, 0.5, 1), V4(1, 0.5, 0.15, 1)),
      emissionOverTime: new ConstantValue(0), emissionBursts: [{ time: 0, count: new ConstantValue(Math.round(14 * size)), cycle: 1, interval: 0.01, probability: 1 }],
      shape: new SphereEmitter({ radius: 0.2 * size, thickness: 1 }), material: MAT.fire, renderMode: RenderMode.BillBoard,
      behaviors: [new SizeOverLife(grow(0.6, 1.6)), new ColorOverLife(new Gradient([[new QV3(1, 0.95, 0.75), 0], [new QV3(1, 0.3, 0.05), 1]], [[1, 0], [0, 1]]))],
    });
    const sparks = new ParticleSystem({ ...SOFT,
      duration: 0.1, looping: false, worldSpace: true, startLife: new IntervalValue(0.3, 0.8), startSpeed: new IntervalValue(3 * size, 6 * size),
      startSize: new IntervalValue(0.06, 0.12), startColor: new ColorRange(V4(1, 0.9, 0.6, 1), V4(1, 0.6, 0.2, 1)),
      emissionOverTime: new ConstantValue(0), emissionBursts: [{ time: 0, count: new ConstantValue(Math.round(18 * size)), cycle: 1, interval: 0.01, probability: 1 }],
      shape: new SphereEmitter({ radius: 0.1, thickness: 1 }), material: MAT.spark, renderMode: RenderMode.StretchedBillBoard, speedFactor: 0.08,
      behaviors: [new ApplyForce(new QV3(0, -1, 0), new ConstantValue(6)), new ColorOverLife(new Gradient([[new QV3(1, 1, 1), 0], [new QV3(1, 0.4, 0.1), 1]], [[1, 0], [0, 1]]))],
    });
    const smoke = new ParticleSystem({ ...SOFT,
      duration: 0.3, looping: false, worldSpace: true, startLife: new IntervalValue(1.4, 2.6), startSpeed: new IntervalValue(0.4, 1.2 * size),
      startSize: new IntervalValue(0.6 * size, 1.1 * size), startColor: new ColorRange(V4(0.16, 0.13, 0.11, 0.85), V4(0.32, 0.28, 0.24, 0.7)),
      emissionOverTime: new ConstantValue(0), emissionBursts: [{ time: 0.05, count: new ConstantValue(Math.round(10 * size)), cycle: 1, interval: 0.01, probability: 1 }],
      shape: new SphereEmitter({ radius: 0.3 * size, thickness: 1 }), material: MAT.smoke, renderMode: RenderMode.BillBoard, startRotation: new IntervalValue(0, 6.28),
      behaviors: [new SizeOverLife(grow(0.8, 2.6)), new ColorOverLife(new Gradient([[new QV3(1, 1, 1), 0], [new QV3(0.8, 0.8, 0.8), 1]], [[0, 0], [0.85, 0.12], [0, 1]])), new ApplyForce(new QV3(0.2, 1, 0.1), new ConstantValue(0.5))],
    });
    for (const sys of [ball, sparks, smoke]) { this.add(sys, at); this.bursts.push({ sys, t: 0, life }); }
  }

  // Muzzle flash: one bright blip at the barrel.
  flash(at, color = 0xffd27a, size = 0.35) {
    const sys = new ParticleSystem({ ...SOFT,
      duration: 0.05, looping: false, worldSpace: true, startLife: new ConstantValue(0.09), startSpeed: new ConstantValue(0),
      startSize: new ConstantValue(size), startColor: new ColorRange(V4(1, 0.85, 0.5, 1), V4(1, 0.95, 0.7, 1)),
      emissionOverTime: new ConstantValue(0), emissionBursts: [{ time: 0, count: new ConstantValue(1), cycle: 1, interval: 0.01, probability: 1 }],
      shape: new PointEmitter(), material: MAT.glow, renderMode: RenderMode.BillBoard, behaviors: [new SizeOverLife(fadeOut())],
    });
    this.add(sys, at); this.bursts.push({ sys, t: 0, life: 0.3 });
  }

  // Dust kicked up behind a moving vehicle.
  dust() {
    return new ParticleSystem({ ...SOFT,
      duration: 1, looping: true, worldSpace: true, startLife: new IntervalValue(0.6, 1.1), startSpeed: new IntervalValue(0.1, 0.3),
      startSize: new IntervalValue(0.2, 0.35), startColor: new ColorRange(V4(0.62, 0.53, 0.4, 0.45), V4(0.7, 0.6, 0.46, 0.3)),
      emissionOverTime: new ConstantValue(9), shape: new SphereEmitter({ radius: 0.15, thickness: 1 }), material: MAT.smoke, renderMode: RenderMode.BillBoard,
      startRotation: new IntervalValue(0, 6.28),
      behaviors: [new SizeOverLife(grow(0.7, 2)), new ColorOverLife(new Gradient([[new QV3(1, 1, 1), 0], [new QV3(1, 1, 1), 1]], [[0.0, 0], [0.5, 0.2], [0, 1]]))],
    });
  }
}
