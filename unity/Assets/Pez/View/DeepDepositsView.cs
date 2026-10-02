using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Deep deposits on the ground (art pack v0.4, 06_DeepMining): an x-ray vein decal in the ore colour with 1-3 dashed
    /// richness rings that fade out a third of the reserve at a time, and a survey stake with an ore-tinted cap at the
    /// drill spot. A team sees only the deposits its surveyors found; spectators see every deposit. Also plays the
    /// surveyor's ground ripple.
    /// </summary>
    public class DeepDepositsView : MonoBehaviour
    {
        const float DecalSize = 5f;     // tiles across the 512 px decal (the outer ring is about 4.5)
        const float FadeIn = 0.6f, FadeOut = 2f;

        class Shown
        {
            public Transform Root, Stake;
            public Material Base;
            public Material[] Rings;
            public float Alpha, StakeK;
        }

        World world;
        readonly Dictionary<int, Shown> shown = new Dictionary<int, Shown>();
        readonly List<(Transform t, Material m, float start)> ripples = new List<(Transform, Material, float)>();
        static Shader decalShader;
        static Mesh quad;

        public void Init(World w) { world = w; }

        static Shader DecalShader => decalShader != null ? decalShader : decalShader = Resources.Load<Shader>("PezShaders/PezDecal");

        static Mesh Quad()
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "decal_quad" };
            quad.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f) };
            quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            quad.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            quad.RecalculateNormals();
            return quad;
        }

        static Material Decal(string texture)
        {
            var tex = Resources.Load<Texture2D>("PezDecals/" + texture);
            if (tex == null || DecalShader == null) return null;
            return new Material(DecalShader) { mainTexture = tex, color = new Color(1, 1, 1, 0) };
        }

        static Transform DecalQuad(Transform parent, string name, Material m, float y, float size)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, y, 0);
            go.transform.localScale = new Vector3(size, 1, size);
            go.GetComponent<MeshFilter>().sharedMesh = Quad();
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        /// <summary>Show deposits as `team` knows them, for a per-player stream render; SetPov(main) restores the local view.</summary>
        public void SetPov(int team, int main)
        {
            if (world == null) return;
            foreach (var d in world.Map.Deep)
                if (shown.TryGetValue(d.Id, out var s))
                {
                    bool on = s.Alpha > 0.001f && (team == main || Visible(d, team));
                    if (s.Root.gameObject.activeSelf != on) s.Root.gameObject.SetActive(on);
                }
        }

        bool Visible(DeepDeposit d, int team) => team < 0 || (team < world.Teams.Count && world.Teams[team].Surveyed.Contains(d.Id));

        public void Sync(int team)
        {
            if (world == null) return;
            float dt = Time.deltaTime;
            foreach (var d in world.Map.Deep)
            {
                bool want = Visible(d, team) && d.Amount > 0;
                if (!shown.TryGetValue(d.Id, out var s))
                {
                    if (!want) continue;
                    shown[d.Id] = s = Make(d);
                }
                s.Root.gameObject.SetActive(true);
                // Fade in over 0.6 s when found; out over 2 s when it runs dry.
                s.Alpha = Mathf.MoveTowards(s.Alpha, want ? 1f : 0f, dt / (want ? FadeIn : FadeOut));
                float frac = d.Initial > 0 ? Mathf.Clamp01(d.Amount / d.Initial) : 0;
                if (s.Base != null) s.Base.color = new Color(1, 1, 1, s.Alpha);
                // Each ring is a third of the reserve: ring 3 (outer) goes first.
                for (int i = 0; i < s.Rings.Length; i++)
                    if (s.Rings[i] != null) s.Rings[i].color = new Color(1, 1, 1, s.Alpha * Mathf.Clamp01((frac - i / 3f) * 3f));
                // The stake rises 0.3 out of the ground over 0.4 s; it goes under a live mine and sinks when dry.
                bool mined = d.MineId != 0 && world.Get(d.MineId) is Entity mine && !mine.Dead;
                s.StakeK = Mathf.MoveTowards(s.StakeK, want && !mined ? 1f : 0f, dt / (want ? 0.4f : 0.6f));
                if (s.Stake != null)
                {
                    s.Stake.localPosition = new Vector3(0, -0.3f * (1f - s.StakeK), 0);
                    if (s.Stake.gameObject.activeSelf != s.StakeK > 0.01f) s.Stake.gameObject.SetActive(s.StakeK > 0.01f);
                }
                if (!want && s.Alpha <= 0f) s.Root.gameObject.SetActive(false);
            }

            for (int i = ripples.Count - 1; i >= 0; i--)
            {
                var (t, m, start) = ripples[i];
                float k = (Time.time - start) / 1.2f;
                if (k >= 1f || t == null) { if (t != null) Destroy(t.gameObject); Destroy(m); ripples.RemoveAt(i); continue; }
                // Out from the foot plate to the survey radius (8 tiles) over 1.2 s, fading as it goes.
                float r = Mathf.Lerp(0.6f, 8f, 1f - (1f - k) * (1f - k)) * 2f;
                t.localScale = new Vector3(r, 1, r);
                m.color = new Color(1, 1, 1, 1f - k);
            }
        }

        Shown Make(DeepDeposit d)
        {
            var ore = Defs.Ores[d.Type];
            var root = new GameObject("deposit" + d.Id).transform;
            root.SetParent(transform, false);
            root.position = WorldView.W(d.Pos);
            var s = new Shown { Root = root, Rings = new Material[3] };
            s.Base = Decal("deep_deposit_" + ore);
            if (s.Base != null) DecalQuad(root, "veins", s.Base, 0.02f, DecalSize);
            for (int i = 0; i < 3; i++)
            {
                s.Rings[i] = Decal($"deep_deposit_{ore}_ring{i + 1}");
                if (s.Rings[i] != null) DecalQuad(root, "ring" + (i + 1), s.Rings[i], 0.025f + i * 0.002f, DecalSize);
            }
            var marker = Resources.Load<GameObject>("PezModels/deposits/deep_deposit_marker");
            if (marker != null)
            {
                var go = Instantiate(marker, root, false);
                Models.FlatShade(go);
                Models.TintOre(go, d.Type);
                s.Stake = go.transform;
                foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            return s;
        }

        /// <summary>One thump's ground ripple, out from `pos`.</summary>
        public void Ripple(Vector3 pos)
        {
            var m = Decal("survey_ripple");
            if (m == null) return;
            var t = DecalQuad(transform, "ripple", m, 0.03f, 1.2f);
            t.position = new Vector3(pos.x, 0.03f, pos.z);
            ripples.Add((t, m, Time.time));
        }
    }
}
