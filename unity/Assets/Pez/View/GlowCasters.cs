using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Pez.View
{
    /// <summary>
    /// A building's own glowing part casting steady light on its body, its pad, the ground and nearby units (the laser
    /// tower's coils; later a power plant's cores, the fusion plasma, the optics lab's light...). Glow means state, so the
    /// light only ever follows a glow the Works class already computes.
    ///
    /// Adopting it from a Works class takes two lines:
    /// <code>
    ///   caster = GlowCaster.For(model, new Vector3(0f, 0.8f, 0f), PezPalette.EmissiveCyanLaserOptics, light: 2.8f, range: 2.4f,
    ///                           pool: 1.2f, deck: 0.37f, deckRadius: 0.5f, ring: 0.24f);  // once, in the constructor
    ///   caster.Set(glow, Fx.Audience);                                                   // every frame it's ticked
    /// </code>
    /// `glow` is the light's strength now, 1 being the source's full working glow (0 dark; a flash may go above 1). Fold
    /// in low power, offline and fog yourself (the Works class knows them): pass 0 when it shouldn't shine. `audience` is
    /// Fx.Audience (null: every camera), so a player's stream that only remembers the building under fog gets no light
    /// from it. Stop calling Set and the light fades out in 0.25 s (the building was hidden, went unbuilt, or the Works
    /// stopped ticking it); destroy the model and it's dropped. Nothing else to clean up.
    ///
    /// Each caster gets, at glow g:
    ///  - a ground pool: an additive splash flat on the ground (PezFxGlow, the family of FxSystems' Pool) of radius `pool`,
    ///    and optionally a deck pool of radius `deckRadius` at height `deck` on the building's own pad (a ground-level pool
    ///    is hidden under the pad). The pools read in every view and stream, whatever the light budget;
    ///  - a real per-pixel point light (no shadows) of intensity `light` x g and range `range`, `ring` toward the main
    ///    camera from `local` (a ring of glow round a stem: its near side lights the faces we see, where a point on the
    ///    axis would sit inside the stem and light only what's above and below it), but only while it's one of
    ///    the <see cref="GlowCasters.Budget"/> brightest on screen nearest the middle of the main view. A light is handed
    ///    over by fading it out (0.35 s) before it fades in on its new owner, so nothing pops.
    /// </summary>
    public sealed class GlowCaster
    {
        internal readonly Transform Model;
        internal readonly Vector3 Local;
        internal readonly Color Color;
        internal readonly Color32 Tint;
        internal readonly float Light, Range, Pool, Deck, DeckRadius, Ring;
        internal float Glow, Shown, Score;
        internal int Mask, Frame = -99, Slot = -1, Index = -1;
        internal Vector3 Pos, LightPos;

        GlowCaster(Transform model, Vector3 local, Color color, float light, float range, float pool, float deck, float deckRadius, float ring)
        {
            Model = model; Local = local; Color = color; Light = light; Range = range; Pool = pool; Deck = deck; DeckRadius = deckRadius; Ring = ring;
            Tint = new Color32((byte)(Mathf.Clamp01(color.r) * 255f), (byte)(Mathf.Clamp01(color.g) * 255f), (byte)(Mathf.Clamp01(color.b) * 255f), 255);
        }

        /// <summary>A caster on `model` at `local` (the model's space: the middle of the glowing part), registered with the
        /// shared <see cref="GlowCasters"/>. `deckRadius` 0: no deck pool.</summary>
        public static GlowCaster For(GameObject model, Vector3 local, Color color, float light, float range, float pool,
            float deck = 0f, float deckRadius = 0f, float ring = 0f)
        {
            var c = new GlowCaster(model.transform, local, color, light, range, pool, deck, deckRadius, ring);
            GlowCasters.I.Add(c);
            return c;
        }

        /// <summary>This frame's glow (1 = full working glow, 0 = dark) and who may see it (Fx.Audience; null = everyone).</summary>
        public void Set(float glow, List<int> audience)
        {
            Glow = Mathf.Max(0f, glow);
            int mask = 0;
            if (audience == null) mask = ~0;
            else for (int i = 0; i < audience.Count; i++) if (audience[i] >= 0 && audience[i] < 32) mask |= 1 << audience[i];
            Mask = mask;
            Frame = Time.frameCount;
        }
    }

    /// <summary>
    /// The shared engine behind <see cref="GlowCaster"/>: every caster's pools in one dynamic mesh (one draw call however
    /// many there are), and a small fixed budget of real point lights handed to the casters that matter most. The project
    /// renders with the Built-in pipeline's Forward path at 4 pixel lights per object (Look.SetupLighting): the sun plus
    /// three. Three glow lights fit that alongside the sun, so a ground chunk with all of them in it never ranks one down to
    /// vertex lighting as the coils breathe (that would flicker); FxSystems' 12 transient lamps outshine them for the
    /// tenth of a second they flash. Per-camera audiences (the fog) are applied before each render by
    /// <see cref="SetPovLayer"/>, which WorldView.SetPov calls. Nothing here allocates per frame.
    /// </summary>
    [DefaultExecutionOrder(-100)] // after every Update (WorldView ticks the casters), before the player streams render
    public class GlowCasters : MonoBehaviour
    {
        static GlowCasters inst;
        public static GlowCasters I => inst != null ? inst : inst = Create();

        /// <summary>Real per-pixel point lights shared by all casters.</summary>
        public const int Budget = 3;
        const int Capacity = 128;                   // casters drawn (pools); more are ignored
        const float Handover = 0.35f, Fade = 0.25f; // s: a light fading out/in on a handover; a caster no longer ticked
        const float GroundY = 0.04f;
        /// <summary>Alpha of the ground and deck pools at glow 1 (PezFxGlow additive, as FxSystems' Pool), and how much less
        /// of the ground pool a caster draws while its real light is on (that light is on the ground too).</summary>
        public static float PoolAlpha = 0.45f, DeckAlpha = 0.4f, LitPoolCut = 0.3f;

        readonly List<GlowCaster> casters = new List<GlowCaster>(Capacity);
        readonly Light[] lights = new Light[Budget];
        readonly GlowCaster[] owner = new GlowCaster[Budget];
        readonly float[] lightFade = new float[Budget];
        readonly GlowCaster[] best = new GlowCaster[Budget];
        int povLayer = TerrainView.MainFogLayer; // the layer of the camera about to render (-1: every caster shows)

        Mesh mesh;
        readonly Vector3[] verts = new Vector3[Capacity * 8];
        readonly Color32[] cols = new Color32[Capacity * 8];
        readonly byte[] alpha = new byte[Capacity * 2];
        int drawn; // casters whose quads were last written (the rest of the mesh is degenerate)

        static GlowCasters Create()
        {
            var go = new GameObject("GlowCasters");
            DontDestroyOnLoad(go);
            var g = go.AddComponent<GlowCasters>();
            g.Build();
            return g;
        }

        void Build()
        {
            for (int i = 0; i < Budget; i++)
            {
                var lg = new GameObject("glow_light");
                lg.transform.SetParent(transform, false);
                var l = lg.AddComponent<Light>();
                l.type = LightType.Point; l.shadows = LightShadows.None; l.renderMode = LightRenderMode.Auto;
                l.enabled = false;
                lights[i] = l;
            }
            var uv = new Vector3[Capacity * 8];
            var tris = new int[Capacity * 12];
            for (int q = 0; q < Capacity * 2; q++)
            {
                int v = q * 4;
                uv[v] = new Vector3(0, 0, 0); uv[v + 1] = new Vector3(1, 0, 0); uv[v + 2] = new Vector3(1, 1, 0); uv[v + 3] = new Vector3(0, 1, 0);
                int t = q * 6;
                tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1; tris[t + 3] = v; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
            }
            mesh = new Mesh { name = "glow_pools" };
            mesh.MarkDynamic();
            mesh.vertices = verts;
            mesh.colors32 = cols;
            mesh.SetUVs(0, uv);
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f); // always drawn: it's one quad per caster
            var go = new GameObject("glow_pools");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var s = Resources.Load<Shader>("PezShaders/PezFxGlow");
            // The same material as FxSystems' Pool: a soft additive glow under the fog overlay.
            var m = new Material(s != null ? s : Shader.Find("Pez/FxGlow")) { renderQueue = 2993 };
            m.SetFloat("_Shape", 0f); m.SetFloat("_Opacity", 0f); m.SetFloat("_Boost", 0.9f);
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        internal void Add(GlowCaster c)
        {
            if (casters.Count >= Capacity) return; // past capacity a caster simply isn't drawn
            c.Index = casters.Count;
            casters.Add(c);
        }

        void Remove(int i)
        {
            var c = casters[i];
            if (c.Slot >= 0) { owner[c.Slot] = null; lightFade[c.Slot] = 0f; lights[c.Slot].enabled = false; c.Slot = -1; }
            int last = casters.Count - 1;
            casters[i] = casters[last];
            casters[i].Index = i;
            casters.RemoveAt(last);
            c.Index = -1;
        }

        static bool Sees(int mask, int layer) => layer < 0 || (mask & (1 << layer)) != 0;

        /// <summary>
        /// Before a camera renders: show only the casters whose audience includes its layer (the main view's fog layer, or
        /// a player's stream's team fog layer; -1 every caster). WorldView.SetPov calls it (and again to restore the main view).
        /// </summary>
        public static void SetPovLayer(int layer)
        {
            if (inst == null) return;
            inst.povLayer = layer;
            inst.Apply();
        }

        // ---------------------------------------------------------------- per frame

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            int frame = Time.frameCount;
            var cam = Camera.main;
            for (int k = 0; k < Budget; k++) best[k] = null;
            var toCam = Vector3.zero; // flat, toward the main camera
            if (cam != null) { toCam = -cam.transform.forward; toCam.y = 0f; toCam = toCam.sqrMagnitude > 1e-4f ? toCam.normalized : Vector3.zero; }

            for (int i = casters.Count - 1; i >= 0; i--)
            {
                var c = casters[i];
                if (c.Model == null) { Remove(i); continue; }
                bool ticked = frame - c.Frame <= 2;
                c.Shown = ticked ? c.Glow : Mathf.MoveTowards(c.Shown, 0f, dt / Fade);
                c.Pos = c.Model.TransformPoint(c.Local);
                c.LightPos = c.Pos + toCam * c.Ring;
                // Who deserves a real light: bright, on screen, near the middle of the main view (a little loyalty to the
                // one that has it, so two equal sources don't trade it back and forth).
                c.Score = 0f;
                if (cam != null && c.Shown > 0.02f && Sees(c.Mask, TerrainView.MainFogLayer))
                {
                    var vp = cam.WorldToViewportPoint(c.Pos);
                    if (vp.z > 0f && vp.x > -0.1f && vp.x < 1.1f && vp.y > -0.1f && vp.y < 1.1f)
                    {
                        float dx = vp.x - 0.5f, dy = vp.y - 0.5f;
                        c.Score = c.Shown * c.Light * (1.5f - Mathf.Sqrt(dx * dx + dy * dy)) * (c.Slot >= 0 ? 1.3f : 1f);
                    }
                }
                if (c.Score <= 0f) continue;
                // Keep the Budget best (insertion into a tiny sorted array).
                for (int k = 0; k < Budget; k++)
                {
                    if (best[k] != null && best[k].Score >= c.Score) continue;
                    for (int j = Budget - 1; j > k; j--) best[j] = best[j - 1];
                    best[k] = c;
                    break;
                }
            }

            // Hand the lights over: a light whose owner isn't among the best fades out, and only a dark light changes hands.
            for (int s = 0; s < Budget; s++)
            {
                var o = owner[s];
                bool want = o != null && Wanted(o);
                lightFade[s] = Mathf.MoveTowards(lightFade[s], want ? 1f : 0f, dt / Handover);
                if (o != null && !want && lightFade[s] <= 0f) { o.Slot = -1; owner[s] = null; }
            }
            for (int k = 0; k < Budget; k++)
            {
                var c = best[k];
                if (c == null || c.Slot >= 0) continue;
                for (int s = 0; s < Budget; s++)
                    if (owner[s] == null) { owner[s] = c; c.Slot = s; lightFade[s] = 0f; break; }
            }
            for (int s = 0; s < Budget; s++)
            {
                var o = owner[s];
                if (o == null) continue;
                var l = lights[s];
                l.transform.position = o.LightPos;
                l.color = o.Color;
                l.range = o.Range;
                l.intensity = o.Light * o.Shown * lightFade[s];
            }

            // The pools: two quads per caster (ground, deck), flat, sized by the caster, alpha with its glow.
            int n = casters.Count;
            for (int i = 0; i < n; i++)
            {
                var c = casters[i];
                float y0 = c.Model.position.y;
                Quad(i * 2, new Vector3(c.Pos.x, y0 + GroundY, c.Pos.z), c.Pool, c.Tint);
                Quad(i * 2 + 1, new Vector3(c.Pos.x, y0 + c.Deck, c.Pos.z), c.DeckRadius, c.Tint);
                float lit = c.Slot >= 0 ? lightFade[c.Slot] : 0f;
                alpha[i * 2] = (byte)(Mathf.Clamp01(c.Shown * PoolAlpha * (1f - LitPoolCut * lit)) * 255f);
                alpha[i * 2 + 1] = (byte)(c.DeckRadius > 0f ? Mathf.Clamp01(c.Shown * DeckAlpha) * 255f : 0f);
            }
            for (int i = n; i < drawn; i++) { Quad(i * 2, Vector3.zero, 0f, default); Quad(i * 2 + 1, Vector3.zero, 0f, default); }
            bool any = n > 0 || drawn > 0;
            drawn = n;
            if (any) mesh.SetVertices(verts, 0, verts.Length, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            Apply();
        }

        bool Wanted(GlowCaster c)
        {
            for (int k = 0; k < Budget; k++) if (best[k] == c) return true;
            return false;
        }

        void Quad(int q, Vector3 at, float r, Color32 tint)
        {
            int v = q * 4;
            verts[v] = at + new Vector3(-r, 0f, -r); verts[v + 1] = at + new Vector3(r, 0f, -r);
            verts[v + 2] = at + new Vector3(r, 0f, r); verts[v + 3] = at + new Vector3(-r, 0f, r);
            cols[v] = cols[v + 1] = cols[v + 2] = cols[v + 3] = tint;
        }

        /// <summary>The current camera's view of every caster: pool alphas and lights on or off for its layer.</summary>
        void Apply()
        {
            int n = drawn;
            for (int i = 0; i < n; i++)
            {
                bool see = Sees(casters[i].Mask, povLayer);
                for (int h = 0; h < 2; h++)
                {
                    int q = i * 2 + h, v = q * 4;
                    byte a = see ? alpha[q] : (byte)0;
                    cols[v].a = cols[v + 1].a = cols[v + 2].a = cols[v + 3].a = a;
                }
            }
            if (n > 0) mesh.SetColors(cols, 0, cols.Length, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            for (int s = 0; s < Budget; s++)
            {
                var o = owner[s];
                bool on = o != null && lights[s].intensity > 0.002f && Sees(o.Mask, povLayer);
                if (lights[s].enabled != on) lights[s].enabled = on;
            }
        }
    }
}
