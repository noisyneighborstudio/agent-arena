using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// A construction site, visible from the moment the structure is placed (base-building review rec. 2a; fix 7). The
    /// sim blocks the footprint and moves units off it at once, but the model's stages are still under ground at
    /// progress 0, so a queued site used to be invisible. Now:
    ///  - the footprint is staked out: a cream dashed outline on the ground, amber corner ticks and four kraft stakes
    ///    with amber caps, which rise over 0.25 s;
    ///  - while it waits in the queue (progress 0, not the queue head) a pallet of cream bricks sits on it;
    ///  - when the structure is built, the stakes and outline sink away over 0.4 s.
    /// The meshes are built once per footprint size and shared.
    /// </summary>
    public class SiteView
    {
        readonly Transform root, pallet;
        float rise, sink = -1f;
        public bool Done { get; private set; }

        static readonly Dictionary<(int, int), Mesh> siteMeshes = new Dictionary<(int, int), Mesh>();
        static Mesh palletMesh, cube;
        static Material[] siteMats, palletMats;

        public SiteView(Transform parent, int sizeX, int sizeY)
        {
            Init();
            root = new GameObject("site").transform;
            root.SetParent(parent, false);
            root.localScale = new Vector3(1f, 0.01f, 1f);
            var r = Renderer(root, SiteMesh(sizeX, sizeY), siteMats);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            pallet = new GameObject("pallet").transform;
            pallet.SetParent(root, false);
            pallet.localPosition = new Vector3(sizeX * 0.5f - 0.45f, 0f, -sizeY * 0.5f + 0.45f);
            pallet.localRotation = Quaternion.Euler(0f, 12f, 0f);
            Renderer(pallet, palletMesh, palletMats);
            pallet.gameObject.SetActive(false);
        }

        static MeshRenderer Renderer(Transform t, Mesh m, Material[] mats)
        {
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = m;
            var r = t.gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            return r;
        }

        /// <summary>Per frame while the structure exists. queued: placed but waiting behind other sites.</summary>
        public void Tick(bool complete, bool queued, float dt)
        {
            if (Done) return;
            if (!complete)
            {
                if (rise < 1f) { rise = Mathf.Min(1f, rise + dt / 0.25f); root.localScale = new Vector3(1f, Mathf.Max(0.01f, rise * (2f - rise)), 1f); }
                if (pallet.gameObject.activeSelf != queued) pallet.gameObject.SetActive(queued);
                return;
            }
            if (sink < 0f) { sink = 0f; pallet.gameObject.SetActive(false); }
            sink += dt / 0.4f;
            root.localPosition = Vector3.down * 0.35f * sink * sink;
            if (sink >= 1f) { Object.Destroy(root.gameObject); Done = true; }
        }

        public void Destroy() { if (root != null) Object.Destroy(root.gameObject); Done = true; }

        // ---------------------------------------------------------------- meshes

        static void Init()
        {
            if (cube != null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(tmp);
            var cream = Look.Model(PezPalette.MaterialsCreamPlastic, 0.4f, 0f, 0.2f, 0.3f);
            var amber = Look.Model(PezPalette.EmissiveAmberIndustryDocking, 0.45f, 0f, 0.2f, 0.2f); // hazard paint, not a lamp
            var kraft = Look.Model(PezPalette.MaterialsKraft, 0.2f, 0f, 0.3f, 0.4f);
            siteMats = new[] { cream, amber, kraft };
            palletMats = new[] { kraft, cream };
            // A pallet of candy bricks: a kraft deck and two layers of cream bricks.
            var deck = new List<CombineInstance> { Box(new Vector3(0f, 0.04f, 0f), new Vector3(0.5f, 0.06f, 0.42f)) };
            var bricks = new List<CombineInstance>();
            for (int layer = 0; layer < 2; layer++)
                for (int i = 0; i < 3; i++)
                    bricks.Add(Box(new Vector3(-0.15f + i * 0.15f + layer * 0.03f, 0.115f + layer * 0.095f, 0f), new Vector3(0.13f, 0.085f, 0.36f - layer * 0.08f)));
            palletMesh = Combine("site_pallet", deck, bricks);
        }

        static CombineInstance Box(Vector3 c, Vector3 size) =>
            new CombineInstance { mesh = cube, transform = Matrix4x4.TRS(c, Quaternion.identity, size) };

        static Mesh Combine(string name, params List<CombineInstance>[] parts)
        {
            var subs = new CombineInstance[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var m = new Mesh();
                m.CombineMeshes(parts[i].ToArray(), true, true);
                subs[i] = new CombineInstance { mesh = m, transform = Matrix4x4.identity };
            }
            var mesh = new Mesh { name = name };
            mesh.CombineMeshes(subs, false, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Outline dashes (cream), corner ticks and stake caps (amber), stakes (kraft), for a footprint.</summary>
        static Mesh SiteMesh(int sx, int sy)
        {
            if (siteMeshes.TryGetValue((sx, sy), out var m)) return m;
            float hx = sx * 0.5f - 0.1f, hz = sy * 0.5f - 0.1f;
            const float y = 0.012f, th = 0.022f, w = 0.05f, dash = 0.2f, gap = 0.13f, tick = 0.32f;
            var dashes = new List<CombineInstance>();
            void Edge(Vector3 a, Vector3 b)
            {
                float len = (b - a).magnitude;
                var dir = (b - a) / len;
                for (float s = tick + 0.06f; s + dash <= len - tick - 0.06f + 1e-3f; s += dash + gap)
                {
                    var c = a + dir * (s + dash * 0.5f);
                    var size = Mathf.Abs(dir.x) > 0.5f ? new Vector3(dash, th, w) : new Vector3(w, th, dash);
                    dashes.Add(Box(new Vector3(c.x, y, c.z), size));
                }
            }
            Edge(new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz));
            Edge(new Vector3(-hx, 0, hz), new Vector3(hx, 0, hz));
            Edge(new Vector3(-hx, 0, -hz), new Vector3(-hx, 0, hz));
            Edge(new Vector3(hx, 0, -hz), new Vector3(hx, 0, hz));
            var amber = new List<CombineInstance>();
            var stakes = new List<CombineInstance>();
            for (int i = 0; i < 4; i++)
            {
                float cx = (i & 1) == 0 ? -hx : hx, cz = (i & 2) == 0 ? -hz : hz;
                float ix = -Mathf.Sign(cx), iz = -Mathf.Sign(cz);       // arms point inward along each edge
                amber.Add(Box(new Vector3(cx + ix * tick * 0.5f, y, cz), new Vector3(tick, th * 1.2f, w * 1.4f)));
                amber.Add(Box(new Vector3(cx, y, cz + iz * tick * 0.5f), new Vector3(w * 1.4f, th * 1.2f, tick)));
                stakes.Add(Box(new Vector3(cx, 0.17f, cz), new Vector3(0.045f, 0.34f, 0.045f)));
                amber.Add(Box(new Vector3(cx, 0.36f, cz), new Vector3(0.075f, 0.06f, 0.075f)));
            }
            m = Combine($"site_{sx}x{sy}", dashes, amber, stakes);
            siteMeshes[(sx, sy)] = m;
            return m;
        }
    }
}
