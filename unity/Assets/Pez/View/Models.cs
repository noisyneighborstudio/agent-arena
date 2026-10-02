using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>Handles to the moving parts of a procedural model.</summary>
    public class Rig
    {
        public Transform Root, Body, Turret, Barrel, Spinner, Bin;
        public float Altitude;
        // Art-pack models (glTF from the v0.2 handoff) are driven by the pack's own components.
        public GameObject Model;
        public PezMotion Motion;
        public PezEmerge Emerge;
        public bool HasModel => Model != null;
        public Vector3 BarrelRest;
        public Gait Gait;            // infantry walk cycle (legs, hips) or null
    }

    /// <summary>
    /// Procedural models built from primitives so the MVP needs no imported art.
    /// Everything faces +Z; one tile is one world unit. Swap these for real meshes later
    /// by returning the same Rig handles.
    /// </summary>
    public static class Models
    {
        // Placeholder materials follow the art pack's neutral palette: the world stays unflavoured, and the four
        // team hues (plus hazard yellow and red) are never used outside the team mask.
        static readonly Color Steel = PezPalette.MaterialsSpringSteel;
        static readonly Color DarkSteel = PezPalette.MaterialsSmokePlastic;
        static readonly Color Concrete = PezPalette.MaterialsSugarPad;
        static readonly Color Track = PezPalette.MaterialsLicorice;
        static readonly Color Skin = PezPalette.MaterialsCreamPlastic;
        static readonly Color Kraft = PezPalette.MaterialsKraft;

        public static Transform Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            t.localScale = scale;
            t.localEulerAngles = euler;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return t;
        }

        static Transform Empty(Transform parent, string name, Vector3 pos = default)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        static readonly Dictionary<string, GameObject> modelCache = new Dictionary<string, GameObject>();
        static readonly Dictionary<(Material, int), Material> tinted = new Dictionary<(Material, int), Material>();
        static readonly Dictionary<string, float> Altitudes = new Dictionary<string, float> { { "gunship", 2.4f }, { "stealth_bomber", 3.2f } };

        /// <summary>The art pack's model for a key (Resources/PezModels/<key>.glb), or null if there isn't one yet.</summary>
        public static GameObject ModelFor(string key)
        {
            if (!modelCache.TryGetValue(key, out var m)) modelCache[key] = m = Resources.Load<GameObject>("PezModels/" + key);
            return m;
        }

        static Mesh dashedRing;
        static int gaitSeed;

        /// <summary>
        /// The HUD kit's selection ring: a flat dashed circle (diameter 1, in XZ), drawn in cream for every team.
        /// </summary>
        public static Transform SelectionRing(Transform parent, Material mat)
        {
            if (dashedRing == null)
            {
                const int dashes = 20, steps = 4;
                const float outer = 0.5f, inner = 0.44f, fill = 0.6f; // each dash covers 60% of its slot
                var verts = new List<Vector3>();
                var tris = new List<int>();
                for (int d = 0; d < dashes; d++)
                {
                    float a0 = d * Mathf.PI * 2f / dashes, a1 = a0 + Mathf.PI * 2f / dashes * fill;
                    int start = verts.Count;
                    for (int k = 0; k <= steps; k++)
                    {
                        float a = Mathf.Lerp(a0, a1, k / (float)steps);
                        var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                        verts.Add(dir * outer);
                        verts.Add(dir * inner);
                    }
                    for (int k = 0; k < steps; k++)
                    {
                        int o = start + k * 2;
                        tris.AddRange(new[] { o, o + 1, o + 2, o + 2, o + 1, o + 3 });
                    }
                }
                dashedRing = new Mesh { name = "selection_ring" };
                dashedRing.SetVertices(verts);
                var white = new Color[verts.Count];
                for (int i = 0; i < white.Length; i++) white[i] = Color.white;
                dashedRing.colors = white;
                dashedRing.SetTriangles(tris, 0);
                dashedRing.RecalculateBounds();
            }
            var go = new GameObject("ring", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = dashedRing;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static readonly Dictionary<Mesh, Mesh> flatMeshes = new Dictionary<Mesh, Mesh>();

        /// <summary>
        /// The art pack's .glb files carry no normals and share vertices between faces. glTFast fills the gap with
        /// Mesh.RecalculateNormals, which smooths across every corner, so boxes shade like pillows. The handoff's
        /// renders (three.js) flat-shade normal-less meshes: unweld each mesh once and recompute, so every face is flat.
        /// </summary>
        public static void FlatShade(GameObject go)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.sharedMesh;
                if (src == null) continue;
                if (!flatMeshes.TryGetValue(src, out var flat)) flatMeshes[src] = flat = Unweld(src);
                mf.sharedMesh = flat;
            }
        }

        static Mesh Unweld(Mesh src)
        {
            if (!src.isReadable || src.GetTopology(0) != MeshTopology.Triangles) return src;
            var v = src.vertices;
            var uv = src.uv;
            var col = src.colors;
            bool hasUv = uv.Length == v.Length, hasCol = col.Length == v.Length;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var cols = new List<Color>();
            var subs = new List<int[]>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tris = src.GetTriangles(s);
                var outTris = new int[tris.Length];
                for (int i = 0; i < tris.Length; i++)
                {
                    outTris[i] = verts.Count;
                    verts.Add(v[tris[i]]);
                    if (hasUv) uvs.Add(uv[tris[i]]);
                    if (hasCol) cols.Add(col[tris[i]]);
                }
                subs.Add(outTris);
            }
            var m = new Mesh { name = src.name + "_flat" };
            if (verts.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts);
            if (hasUv) m.SetUVs(0, uvs);
            if (hasCol) m.SetColors(cols);
            m.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) m.SetTriangles(subs[s], s);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static readonly Dictionary<(Material, int), Material> oreTinted = new Dictionary<(Material, int), Material>();

        /// <summary>
        /// Swap every M_OreTint material (deep mine ore tube, deposit stake cap) for a copy in an ore's colour; crystal
        /// and uranium glow (art pack v0.4).
        /// </summary>
        public static void TintOre(GameObject go, int oreType)
        {
            if (oreType < 0 || oreType >= WorldView.OreColors.Length) return;
            var color = WorldView.OreColors[oreType];
            bool glow = oreType >= 2;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !m.name.StartsWith("M_OreTint")) continue;
                    if (!oreTinted.TryGetValue((m, oreType), out var t))
                    {
                        t = new Material(m) { name = m.name + "_ore" + oreType, enableInstancing = true };
                        t.color = color;
                        if (t.HasProperty("_BaseColor")) t.SetColor("_BaseColor", color);
                        if (t.HasProperty("baseColorFactor")) t.SetColor("baseColorFactor", color);
                        if (glow)
                        {
                            t.EnableKeyword("_EMISSION");
                            t.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                            if (t.HasProperty("_EmissionColor")) t.SetColor("_EmissionColor", color * 1.4f);
                            if (t.HasProperty("emissiveFactor")) t.SetColor("emissiveFactor", color * 1.4f);
                        }
                        oreTinted[(m, oreType)] = t;
                    }
                    mats[i] = t;
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        /// <summary>Swap every M_Team material for a copy tinted to the team's flavour.</summary>
        public static void TintTeam(GameObject go, int team)
        {
            var color = Mats.Team(team);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !m.name.StartsWith("M_Team")) continue;
                    if (!tinted.TryGetValue((m, team), out var t))
                    {
                        t = new Material(m) { name = m.name + "_t" + team, enableInstancing = true };
                        t.color = color; // baseColorFactor is the shader's [MainColor]
                        tinted[(m, team)] = t;
                    }
                    mats[i] = t;
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
        }

        public static Rig Build(string key, int team)
        {
            var root = new GameObject(key).transform;
            var rig = new Rig { Root = root };
            rig.Body = Empty(root, "body");
            var prefab = ModelFor(key);
            if (prefab != null)
            {
                var go = Object.Instantiate(prefab, rig.Body, false);
                go.name = key; // PezMotion reads its profile from the object name
                FlatShade(go);
                TintTeam(go, team);
                rig.Model = go;
                rig.Turret = PezMotion.FindDeep(go.transform, "turret");
                rig.Barrel = PezMotion.FindDeep(go.transform, "barrel");
                rig.Spinner = PezMotion.FindDeep(go.transform, "spinner");
                rig.Bin = PezMotion.FindDeep(go.transform, "bin");
                // Soldiers get hips and legs cut from their body mesh before PezMotion caches the turret's rest pose.
                if (Pez.Sim.Defs.Get(key)?.Armor == Pez.Sim.Armor.Infantry) rig.Gait = Gait.FromModel(rig.Body, go, ++gaitSeed);
                rig.Motion = go.AddComponent<PezMotion>();
                // Visual turrets keep up with the sim's aim so shots leave the barrel, not the side of it.
                if (rig.Motion.profile.turretYawSpeed > 0) rig.Motion.profile.turretYawSpeed = Mathf.Max(rig.Motion.profile.turretYawSpeed, 240f);
                rig.Emerge = go.AddComponent<PezEmerge>();
                if (Altitudes.TryGetValue(key, out var alt)) rig.Altitude = alt;
                if (rig.Barrel != null) rig.BarrelRest = rig.Barrel.localPosition;
                return rig;
            }
            var tc = Mats.Team(team);
            var teamMat = Mats.Lit(tc, 0.45f, 0.25f);
            var teamDark = Mats.Lit(tc * 0.55f, 0.35f, 0.2f);
            var steel = Mats.Lit(Steel, 0.5f, 0.6f);
            var dark = Mats.Lit(DarkSteel, 0.3f, 0.5f);
            var concrete = Mats.Lit(Concrete, 0.1f, 0f);
            var track = Mats.Lit(Track, 0.1f, 0.2f);
            var b = rig.Body;
            switch (key)
            {
                case "light_tank": Tank(rig, teamMat, teamDark, steel, track, 1f, false); break;
                case "heavy_tank": Tank(rig, teamMat, teamDark, steel, track, 1.3f, true); break;
                case "mining_truck":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.3f, 0.14f, 0), new Vector3(0.18f, 0.28f, 1.05f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.3f, 0.14f, 0), new Vector3(0.18f, 0.28f, 1.05f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.38f, -0.1f), new Vector3(0.62f, 0.32f, 0.8f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.62f, 0.25f), new Vector3(0.36f, 0.24f, 0.25f), Mats.Lit(new Color(0.2f, 0.3f, 0.4f), 0.9f, 0.4f));
                    rig.Bin = Part(b, PrimitiveType.Cube, new Vector3(0, 0.58f, -0.25f), new Vector3(0.5f, 0.12f, 0.45f), Mats.Glow(new Color(0.95f, 0.7f, 0.15f), 0.8f));
                    rig.Spinner = Empty(b, "cutter", new Vector3(0, 0.2f, 0.55f));
                    Part(rig.Spinner, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.24f, 0.33f, 0.24f), steel, new Vector3(0, 0, 90));
                    for (int i = 0; i < 4; i++)
                        Part(rig.Spinner, PrimitiveType.Cube, Vector3.zero, new Vector3(0.6f, 0.05f, 0.32f), dark, new Vector3(i * 45, 0, 0));
                    rig.Turret = rig.Spinner;
                    break;
                case "rifleman":
                case "rocket_soldier":
                case "laser_trooper":
                case "medic":
                case "engineer":
                case "sniper":
                case "commando":
                    {
                        float s = 0.85f;
                        // Smoke-plastic torso on licorice legs; the team colour sits on the helmet, on top, where it reads at full zoom-out.
                        Part(b, PrimitiveType.Capsule, new Vector3(0, 0.3f * s, 0), new Vector3(0.16f, 0.13f, 0.12f) * s, Mats.Lit(DarkSteel, 0.5f, 0));
                        Part(b, PrimitiveType.Sphere, new Vector3(0, 0.5f * s, 0), Vector3.one * 0.1f * s, Mats.Lit(Skin, 0.6f, 0));
                        Part(b, PrimitiveType.Sphere, new Vector3(0, 0.53f * s, -0.005f), new Vector3(0.11f, 0.06f, 0.11f) * s, teamMat);
                        rig.Turret = Empty(b, "arms", new Vector3(0, 0.32f * s, 0));
                        if (key == "rifleman")
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.06f, 0, 0.12f), new Vector3(0.03f, 0.04f, 0.26f), dark);
                        else if (key == "engineer")
                        {
                            // Hard hat and toolbox.
                            Part(b, PrimitiveType.Sphere, new Vector3(0, 0.55f * s, 0), new Vector3(0.14f, 0.07f, 0.14f) * s, teamMat);
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.1f, -0.1f, 0.03f), new Vector3(0.07f, 0.06f, 0.12f), Mats.Lit(Kraft, 0.2f, 0f));
                        }
                        else if (key == "sniper")
                        {
                            // Ghillie-dark body overlay and a long scoped rifle.
                            Part(b, PrimitiveType.Capsule, new Vector3(0, 0.3f * s, 0), new Vector3(0.17f, 0.135f, 0.13f) * s, Mats.Lit(Kraft * 0.6f, 0.05f, 0));
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.06f, 0, 0.2f), new Vector3(0.025f, 0.035f, 0.46f), dark);
                            Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0, 1.4f, 0.05f), new Vector3(0.9f, 0.12f, 0.9f), Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 1.5f), new Vector3(90, 0, 0));
                        }
                        else if (key == "commando")
                        {
                            // Beret and a satchel of charges.
                            Part(b, PrimitiveType.Sphere, new Vector3(0.02f, 0.55f * s, 0), new Vector3(0.13f, 0.04f, 0.13f) * s, Mats.Lit(Track, 0.4f, 0));
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.26f * s, -0.09f), new Vector3(0.14f, 0.12f, 0.07f) * s, Mats.Lit(Kraft, 0.2f, 0));
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.08f, 0, 0.08f), new Vector3(0.06f, 0.05f, 0.08f), Mats.Glow(PezPalette.EmissiveAmberIndustryDocking, 2f));
                        }
                        else if (key == "medic")
                        {
                            // White pack with a red cross on the back, no weapon.
                            // White pack with a team-coloured plus (never a red cross: it would read as Cherry).
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f * s, -0.08f), new Vector3(0.14f, 0.16f, 0.07f) * s, Mats.Lit(PezPalette.MaterialsBone, 0.3f, 0));
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f * s, -0.12f), new Vector3(0.1f, 0.03f, 0.01f) * s, teamMat);
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f * s, -0.12f), new Vector3(0.03f, 0.1f, 0.01f) * s, teamMat);
                        }
                        else if (key == "laser_trooper")
                        {
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.06f, 0, 0.13f), new Vector3(0.045f, 0.05f, 0.28f), Mats.Lit(new Color(0.85f, 0.88f, 0.9f), 0.8f, 0.6f));
                            Part(rig.Barrel, PrimitiveType.Cube, new Vector3(0, 0.6f, 0), new Vector3(0.6f, 0.3f, 0.9f), Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 3f));
                        }
                        else
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0.08f, 0.07f, 0.02f), new Vector3(0.07f, 0.17f, 0.07f), Mats.Lit(Kraft, 0.3f, 0f), new Vector3(90, 0, 0));
                        Legs(rig, s, key == "sniper" ? Mats.Lit(Kraft * 0.45f, 0.05f, 0) : Mats.Lit(Track, 0.2f, 0));
                        break;
                    }
                case "command_center":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(-0.35f, 0.45f, 0.3f), new Vector3(1.6f, 0.7f, 1.5f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(-0.35f, 0.85f, 0.3f), new Vector3(1.4f, 0.1f, 1.3f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0.75f, 0.35f, -0.6f), new Vector3(0.9f, 0.6f, 0.9f), teamDark);
                    rig.Turret = Empty(b, "crane", new Vector3(0.9f, 0.1f, 0.9f));
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.9f, 0), new Vector3(0.12f, 1.8f, 0.12f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 1.75f, -0.6f), new Vector3(0.1f, 0.1f, 1.5f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(b, PrimitiveType.Cube, new Vector3(-0.35f, 0.5f, -0.46f), new Vector3(1.2f, 0.4f, 0.05f), Mats.Glow(new Color(0.6f, 0.85f, 1f), 1.2f));
                    break;
                case "power_plant":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0.35f), new Vector3(1.6f, 0.4f, 0.8f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(-0.4f, 0.6f, -0.35f), new Vector3(0.6f, 0.55f, 0.6f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.4f, 0.6f, -0.35f), new Vector3(0.6f, 0.55f, 0.6f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(-0.4f, 1.12f, -0.35f), new Vector3(0.5f, 0.02f, 0.5f), Mats.Glow(new Color(0.3f, 0.9f, 1f), 2.5f));
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.4f, 1.12f, -0.35f), new Vector3(0.5f, 0.02f, 0.5f), Mats.Glow(new Color(0.3f, 0.9f, 1f), 2.5f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.48f, 0.35f), new Vector3(1.62f, 0.06f, 0.82f), teamMat);
                    break;
                case "mining_refinery":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0.2f, 0.5f, 0.55f), new Vector3(2.2f, 0.9f, 1.4f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0.2f, 0.98f, 0.55f), new Vector3(2.2f, 0.08f, 1.4f), teamMat);
                    for (int i = 0; i < 2; i++)
                    {
                        Part(b, PrimitiveType.Cylinder, new Vector3(-0.9f + i * 0.7f, 0.9f, 0.9f), new Vector3(0.5f, 0.75f, 0.5f), Mats.Lit(new Color(0.75f, 0.72f, 0.6f), 0.5f, 0.6f));
                        Part(b, PrimitiveType.Sphere, new Vector3(-0.9f + i * 0.7f, 1.65f, 0.9f), new Vector3(0.5f, 0.25f, 0.5f), teamDark);
                    }
                    // Dock pad where harvesters unload (south edge).
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.06f, -1.0f), new Vector3(1.2f, 0.06f, 0.9f), Mats.Lit(new Color(0.3f, 0.3f, 0.28f), 0.1f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.1f, -1.0f), new Vector3(1.0f, 0.02f, 0.08f), Mats.Glow(new Color(1f, 0.75f, 0.2f), 1.5f));
                    break;
                case "barracks":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.35f, 0.15f), new Vector3(1.6f, 0.6f, 1.2f), Mats.Lit(new Color(0.42f, 0.4f, 0.3f), 0.1f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.72f, 0.15f), new Vector3(1.7f, 0.12f, 1.3f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, -0.47f), new Vector3(0.4f, 0.45f, 0.05f), dark);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.7f, 1.0f, 0.6f), new Vector3(0.03f, 0.6f, 0.03f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0.85f, 1.45f, 0.6f), new Vector3(0.3f, 0.18f, 0.02f), Mats.Glow(tc, 0.8f));
                    break;
                case "factory":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.6f, 0.2f), new Vector3(2.6f, 1.1f, 2.2f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 1.15f, 0.2f), new Vector3(2.4f, 1.1f, 0.9f), teamDark, new Vector3(0, 0, 90));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.45f, -0.92f), new Vector3(1.4f, 0.8f, 0.06f), Mats.Lit(new Color(0.12f, 0.12f, 0.12f), 0.4f, 0.6f));
                    for (int i = 0; i < 5; i++)
                        Part(b, PrimitiveType.Cube, new Vector3(-0.56f + i * 0.28f, 0.45f, -0.95f), new Vector3(0.12f, 0.8f, 0.02f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 1.2f, -0.9f), new Vector3(2.0f, 0.12f, 0.04f), Mats.Glow(tc, 1.4f));
                    break;
                case "radar_dome":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0.1f), new Vector3(1.5f, 0.4f, 1.4f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.47f, 0.1f), new Vector3(1.52f, 0.05f, 1.42f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(-0.25f, 0.6f, 0.2f), new Vector3(0.9f, 0.8f, 0.9f), Mats.Lit(new Color(0.88f, 0.88f, 0.85f), 0.6f, 0.1f));
                    rig.Turret = Empty(b, "dish", new Vector3(0.45f, 0.5f, -0.35f));
                    Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.25f, 0), new Vector3(0.05f, 0.25f, 0.05f), dark);
                    Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.55f, 0.05f), new Vector3(0.6f, 0.03f, 0.6f), Mats.Lit(new Color(0.8f, 0.8f, 0.8f), 0.7f, 0.5f), new Vector3(70, 0, 0));
                    Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.6f, 0.2f), Vector3.one * 0.07f, Mats.Glow(new Color(1f, 0.2f, 0.15f), 3f));
                    break;
                case "gun_turret":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.15f, 0), new Vector3(0.85f, 0.15f, 0.85f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.32f, 0), new Vector3(0.6f, 0.04f, 0.6f), teamMat);
                    rig.Turret = Empty(b, "turret", new Vector3(0, 0.45f, 0));
                    Part(rig.Turret, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.55f, 0.4f, 0.55f), steel);
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.03f, 0.4f), new Vector3(0.09f, 0.3f, 0.09f), dark, new Vector3(90, 0, 0));
                    break;

                // ---- New economy structures
                case "outpost":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0.1f), new Vector3(1.3f, 0.5f, 1.2f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.57f, 0.1f), new Vector3(1.32f, 0.05f, 1.22f), teamMat);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.45f, 1.0f, 0.45f), new Vector3(0.04f, 0.45f, 0.04f), dark);
                    Part(b, PrimitiveType.Sphere, new Vector3(0.45f, 1.48f, 0.45f), Vector3.one * 0.09f, Mats.Glow(tc, 3f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.08f, -0.8f), new Vector3(0.9f, 0.04f, 0.04f), Mats.Glow(new Color(1f, 0.75f, 0.2f), 1.5f));
                    break;
                case "electronics_plant":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.35f, 0.1f), new Vector3(1.6f, 0.6f, 1.3f), Mats.Lit(new Color(0.75f, 0.77f, 0.8f), 0.6f, 0.3f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.66f, 0.1f), new Vector3(1.62f, 0.04f, 1.32f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.38f, -0.56f), new Vector3(1.3f, 0.3f, 0.03f), Mats.Glow(new Color(0.2f, 1f, 0.45f), 1.8f));
                    for (int i = 0; i < 3; i++) Part(b, PrimitiveType.Cylinder, new Vector3(-0.5f + i * 0.5f, 0.85f, 0.45f), new Vector3(0.14f, 0.2f, 0.14f), dark);
                    break;
                case "optics_lab":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0), new Vector3(1.6f, 0.4f, 1.6f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.46f, 0), new Vector3(1.62f, 0.04f, 1.62f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0, 0.5f, 0), new Vector3(1.1f, 0.9f, 1.1f), Mats.Glow(new Color(0.3f, 0.85f, 1f), 0.9f));
                    rig.Turret = Empty(b, "prism", new Vector3(0, 1.05f, 0));
                    Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.22f, 0.4f, 0.22f), Mats.Glow(new Color(0.6f, 0.95f, 1f), 3f), new Vector3(45, 0, 45));
                    break;
                case "enrichment_plant":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0.35f), new Vector3(1.6f, 0.5f, 0.8f), dark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.56f, 0.35f), new Vector3(1.62f, 0.04f, 0.82f), teamMat);
                    for (int i = 0; i < 3; i++)
                    {
                        Part(b, PrimitiveType.Cylinder, new Vector3(-0.5f + i * 0.5f, 0.55f, -0.35f), new Vector3(0.36f, 0.5f, 0.36f), Mats.Lit(new Color(0.3f, 0.32f, 0.3f), 0.6f, 0.7f));
                        Part(b, PrimitiveType.Cylinder, new Vector3(-0.5f + i * 0.5f, 0.55f, -0.35f), new Vector3(0.38f, 0.08f, 0.38f), Mats.Glow(new Color(0.4f, 1f, 0.2f), 2.5f));
                    }
                    break;
                case "composite_foundry":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.4f, 0.1f), new Vector3(1.5f, 0.7f, 1.3f), Mats.Lit(new Color(0.12f, 0.12f, 0.15f), 0.8f, 0.5f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.78f, 0.1f), new Vector3(1.2f, 0.12f, 1.0f), Mats.Lit(new Color(0.18f, 0.18f, 0.22f), 0.8f, 0.5f), new Vector3(0, 45, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, -0.56f), new Vector3(1.1f, 0.08f, 0.03f), Mats.Glow(new Color(0.75f, 0.3f, 1f), 2.5f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.82f, 0.1f), new Vector3(1.52f, 0.03f, 1.32f), teamMat);
                    break;
                case "fusion_reactor":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.3f, 0), new Vector3(2.4f, 0.25f, 2.4f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.56f, 0), new Vector3(2.0f, 0.04f, 2.0f), teamMat);
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * Mathf.PI / 3;
                        Part(b, PrimitiveType.Cube, new Vector3(Mathf.Cos(a) * 0.9f, 0.9f, Mathf.Sin(a) * 0.9f), new Vector3(0.18f, 0.7f, 0.18f), dark);
                    }
                    rig.Turret = Empty(b, "core", new Vector3(0, 1.0f, 0));
                    Part(rig.Turret, PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.75f, Mats.Glow(new Color(1f, 0.35f, 0.9f), 3.5f));
                    break;
                case "airfield":
                    Pad(b, 3, 3, Mats.Lit(new Color(0.22f, 0.22f, 0.22f), 0.2f, 0f));
                    for (int i = 0; i < 5; i++) Part(b, PrimitiveType.Cube, new Vector3(-0.3f, 0.09f, -1.1f + i * 0.5f), new Vector3(0.06f, 0.01f, 0.25f), Mats.Lit(Color.white * 0.9f, 0.2f, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0.95f, 0.5f, 0.95f), new Vector3(0.5f, 0.9f, 0.5f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0.95f, 1.05f, 0.95f), new Vector3(0.65f, 0.25f, 0.65f), Mats.Lit(new Color(0.2f, 0.35f, 0.45f), 0.95f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0.95f, 1.2f, 0.95f), new Vector3(0.7f, 0.05f, 0.7f), teamMat);
                    Part(b, PrimitiveType.Cylinder, new Vector3(-0.3f, 0.1f, 0.9f), new Vector3(0.9f, 0.01f, 0.9f), Mats.Glow(new Color(1f, 0.8f, 0.2f), 0.8f));
                    break;
                case "sam_site":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.12f, 0), new Vector3(0.85f, 0.12f, 0.85f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.26f, 0), new Vector3(0.6f, 0.03f, 0.6f), teamMat);
                    rig.Turret = Empty(b, "launcher", new Vector3(0, 0.35f, 0));
                    rig.Barrel = Empty(rig.Turret, "rack", new Vector3(0, 0.1f, 0));
                    for (int i = 0; i < 4; i++)
                        Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(-0.15f + (i % 2) * 0.3f, 0.12f + (i / 2) * 0.16f, 0.05f), new Vector3(0.11f, 0.25f, 0.11f), Mats.Lit(new Color(0.85f, 0.85f, 0.8f), 0.4f, 0.2f), new Vector3(-60, 0, 0));
                    break;
                case "laser_tower":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.15f, 0), new Vector3(0.8f, 0.15f, 0.8f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.75f, 0), new Vector3(0.22f, 0.6f, 0.22f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.32f, 0), new Vector3(0.55f, 0.03f, 0.55f), teamMat);
                    rig.Turret = Empty(b, "emitter", new Vector3(0, 1.45f, 0));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.28f, 0.42f, 0.28f), Mats.Glow(new Color(0.3f, 0.9f, 1f), 3.5f), new Vector3(45, 0, 45));
                    break;

                // ---- New units
                case "repair_truck":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.25f, 0.12f, 0), new Vector3(0.15f, 0.24f, 0.95f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.25f, 0.12f, 0), new Vector3(0.15f, 0.24f, 0.95f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.28f, 0), new Vector3(0.52f, 0.14f, 1.0f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.46f, 0.32f), new Vector3(0.42f, 0.22f, 0.28f), Mats.Lit(new Color(0.2f, 0.3f, 0.4f), 0.9f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.42f, -0.2f), new Vector3(0.46f, 0.16f, 0.5f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(b, PrimitiveType.Sphere, new Vector3(0.15f, 0.6f, 0.42f), Vector3.one * 0.07f, Mats.Glow(new Color(1f, 0.6f, 0.1f), 3f));
                    // Crane arm that swings to face whatever it's repairing.
                    rig.Turret = Empty(b, "crane", new Vector3(0, 0.52f, -0.25f));
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.12f, 0.2f), new Vector3(0.06f, 0.06f, 0.55f), steel, new Vector3(-20, 0, 0));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.22f, 0.48f), Vector3.one * 0.08f, Mats.Glow(new Color(1f, 0.85f, 0.4f), 2.5f));
                    break;
                case "apc":
                    for (int i = 0; i < 6; i++)
                        Part(b, PrimitiveType.Cylinder, new Vector3(i % 2 == 0 ? -0.28f : 0.28f, 0.11f, -0.3f + (i / 2) * 0.3f), new Vector3(0.2f, 0.05f, 0.2f), track, new Vector3(0, 0, 90));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f, 0), new Vector3(0.52f, 0.26f, 0.95f), Mats.Lit(DarkSteel, 0.5f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.41f, 0), new Vector3(0.44f, 0.02f, 0.6f), teamMat); // team roof band
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0.47f), new Vector3(0.5f, 0.18f, 0.1f), Mats.Lit(DarkSteel, 0.5f, 0f), new Vector3(-30, 0, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, -0.49f), new Vector3(0.32f, 0.2f, 0.02f), dark);
                    rig.Turret = Empty(b, "mg", new Vector3(0, 0.44f, 0.1f));
                    Part(rig.Turret, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.14f, 0.04f, 0.14f), teamMat);
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.04f, 0.15f), new Vector3(0.035f, 0.035f, 0.3f), dark);
                    break;
                case "flak_track":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.85f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.85f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.24f, 0), new Vector3(0.42f, 0.16f, 0.8f), Mats.Lit(DarkSteel, 0.5f, 0f));
                    rig.Turret = Empty(b, "flak", new Vector3(0, 0.38f, -0.05f));
                    Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.3f, 0.14f, 0.26f), teamMat);
                    rig.Barrel = Empty(rig.Turret, "guns", new Vector3(0, 0.06f, 0.05f));
                    for (int i = 0; i < 4; i++)
                        Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(-0.09f + (i % 2) * 0.18f, 0.08f + (i / 2) * 0.07f, 0.16f), new Vector3(0.035f, 0.18f, 0.035f), steel, new Vector3(50, 0, 0));
                    break;
                case "minelayer":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.26f, 0.15f), new Vector3(0.44f, 0.2f, 0.55f), Mats.Lit(DarkSteel, 0.5f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.37f, 0.25f), new Vector3(0.36f, 0.02f, 0.3f), teamMat); // team cab roof
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, -0.3f), new Vector3(0.4f, 0.24f, 0.3f), Mats.Lit(Kraft, 0.2f, 0f));
                    for (int i = 0; i < 3; i++) Part(b, PrimitiveType.Cylinder, new Vector3(-0.12f + i * 0.12f, 0.45f, -0.3f), new Vector3(0.1f, 0.02f, 0.1f), dark);
                    break;
                case "mine":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.03f, 0), new Vector3(0.32f, 0.03f, 0.32f), dark);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.065f, 0), new Vector3(0.22f, 0.01f, 0.22f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0, 0.08f, 0), Vector3.one * 0.05f, Mats.Glow(PezPalette.EmissiveAmberIndustryDocking, 3f));
                    break;
                case "mammoth_tank":
                    Tank(rig, teamMat, teamDark, steel, track, 1.65f, true);
                    // Missile pods either side of the turret.
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.32f, 0.12f, -0.05f), new Vector3(0.12f, 0.12f, 0.3f), dark);
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(-0.32f, 0.12f, -0.05f), new Vector3(0.12f, 0.12f, 0.3f), dark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.42f, -0.4f), new Vector3(0.6f, 0.1f, 0.2f), steel);
                    break;
                case "recon_drone":
                    rig.Altitude = 2.8f;
                    Part(b, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.22f, 0.1f, 0.28f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0, -0.04f, 0.1f), Vector3.one * 0.07f, Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 2.5f));
                    var props = Empty(b, "props", Vector3.zero); // static discs read as spinning props at game distance
                    for (int i = 0; i < 4; i++)
                    {
                        var arm = new Vector3(i % 2 == 0 ? -0.22f : 0.22f, 0.02f, i < 2 ? 0.22f : -0.22f);
                        Part(b, PrimitiveType.Cube, arm * 0.5f, new Vector3(0.03f, 0.02f, 0.3f), dark, new Vector3(0, i % 3 == 0 ? 45 : -45, 0));
                        Part(props, PrimitiveType.Cylinder, arm + Vector3.up * 0.03f, new Vector3(0.18f, 0.005f, 0.18f), Mats.Unlit(new Color(0.75f, 0.75f, 0.75f, 0.45f)));
                    }
                    break;
                case "transport_chopper":
                    rig.Altitude = 2.6f;
                    Part(b, PrimitiveType.Capsule, new Vector3(0, 0, 0), new Vector3(0.42f, 0.55f, 0.42f), teamMat, new Vector3(90, 0, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.06f, 0.42f), new Vector3(0.3f, 0.16f, 0.14f), Mats.Lit(new Color(0.15f, 0.25f, 0.35f), 0.95f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, -0.2f, 0), new Vector3(0.4f, 0.03f, 0.6f), dark);
                    rig.Spinner = Empty(b, "rotors", Vector3.zero);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.28f, 0.35f), new Vector3(1.3f, 0.015f, 0.07f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.28f, 0.35f), new Vector3(0.07f, 0.015f, 1.3f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.32f, -0.4f), new Vector3(1.3f, 0.015f, 0.07f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.32f, -0.4f), new Vector3(0.07f, 0.015f, 1.3f), dark);
                    break;
                case "outpost_truck":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.28f, 0.13f, 0), new Vector3(0.16f, 0.26f, 1.1f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.28f, 0.13f, 0), new Vector3(0.16f, 0.26f, 1.1f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0), new Vector3(0.6f, 0.15f, 1.15f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.5f, 0.4f), new Vector3(0.45f, 0.25f, 0.3f), Mats.Lit(new Color(0.2f, 0.3f, 0.4f), 0.9f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.55f, -0.15f), new Vector3(0.55f, 0.35f, 0.6f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.18f, 0.9f, -0.3f), new Vector3(0.03f, 0.25f, 0.03f), dark);
                    break;
                case "scout_buggy":
                    for (int i = 0; i < 4; i++)
                        Part(b, PrimitiveType.Cylinder, new Vector3(i % 2 == 0 ? -0.24f : 0.24f, 0.1f, i < 2 ? 0.25f : -0.25f), new Vector3(0.18f, 0.05f, 0.18f), track, new Vector3(0, 0, 90));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.18f, 0), new Vector3(0.38f, 0.1f, 0.7f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.28f, -0.05f), new Vector3(0.32f, 0.04f, 0.3f), dark);
                    rig.Turret = Empty(b, "mg", new Vector3(0, 0.33f, -0.1f));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0, 0.15f), new Vector3(0.04f, 0.04f, 0.3f), dark);
                    break;
                case "artillery":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.27f, 0.11f, 0), new Vector3(0.16f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.27f, 0.11f, 0), new Vector3(0.16f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.24f, -0.05f), new Vector3(0.44f, 0.16f, 0.8f), teamMat);
                    rig.Turret = Empty(b, "gun", new Vector3(0, 0.36f, -0.15f));
                    Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.3f, 0.14f, 0.3f), teamDark);
                    rig.Barrel = Empty(rig.Turret, "barrel", new Vector3(0, 0.05f, 0.1f));
                    Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0, 0.22f, 0.38f), new Vector3(0.07f, 0.45f, 0.07f), steel, new Vector3(60, 0, 0));
                    break;
                case "laser_tank":
                    Tank(rig, teamMat, teamDark, steel, track, 1.2f, false);
                    // Laser, not plasma: cyan coils (magenta is reserved for plasma and fusion).
                    Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.15f, -0.05f), Vector3.one * 0.18f, Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 3f));
                    foreach (Transform c in rig.Barrel) c.GetComponent<Renderer>().sharedMaterial = Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 1.6f);
                    break;
                case "gunship":
                    rig.Altitude = 2.4f;
                    Part(b, PrimitiveType.Capsule, new Vector3(0, 0, 0), new Vector3(0.35f, 0.42f, 0.35f), teamMat, new Vector3(90, 0, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.02f, -0.6f), new Vector3(0.06f, 0.06f, 0.5f), teamDark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.12f, -0.82f), new Vector3(0.03f, 0.2f, 0.12f), teamDark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.05f, 0.28f), new Vector3(0.22f, 0.12f, 0.15f), Mats.Lit(new Color(0.15f, 0.25f, 0.35f), 0.95f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, -0.08f, 0), new Vector3(0.7f, 0.04f, 0.12f), dark);
                    rig.Spinner = Empty(b, "rotor", new Vector3(0, 0.24f, 0));
                    Part(rig.Spinner, PrimitiveType.Cube, Vector3.zero, new Vector3(1.4f, 0.015f, 0.07f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, Vector3.zero, new Vector3(0.07f, 0.015f, 1.4f), dark);
                    rig.Turret = Empty(b, "pods", new Vector3(0, -0.1f, 0.1f));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0.3f, 0, 0), new Vector3(0.07f, 0.12f, 0.07f), steel, new Vector3(90, 0, 0));
                    break;
                case "stealth_bomber":
                    {
                        rig.Altitude = 3.2f;
                        var hull = Mats.Lit(new Color(0.09f, 0.09f, 0.11f), 0.85f, 0.6f);
                        Part(b, PrimitiveType.Cube, Vector3.zero, new Vector3(0.9f, 0.08f, 0.9f), hull, new Vector3(0, 45, 0));
                        Part(b, PrimitiveType.Cube, new Vector3(0, 0.05f, 0.1f), new Vector3(0.3f, 0.1f, 0.7f), hull);
                        Part(b, PrimitiveType.Cube, new Vector3(0, 0.02f, -0.45f), new Vector3(1.0f, 0.04f, 0.04f), Mats.Glow(tc, 1.2f));
                        Part(b, PrimitiveType.Cube, new Vector3(0, 0.1f, 0.3f), new Vector3(0.14f, 0.04f, 0.18f), Mats.Lit(new Color(0.25f, 0.2f, 0.1f), 0.95f, 0.8f));
                        break;
                    }
                // Deep mining fallbacks, used only while a model is missing. Swapping in art needs no code: drop geological_surveyor.glb, drill_rig.glb or
                // deep_mine.glb into Resources/PezModels and ModelFor picks it up ahead of these.
                case "geological_surveyor": Surveyor(rig, teamMat, steel, track); break;
                case "drill_rig": DrillRig(rig, teamMat, steel, track); break;
                case "deep_mine": DeepMine(rig, teamMat, steel, concrete); break;
                default:
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0), Vector3.one * 0.5f, teamMat);
                    break;
            }
            if (rig.Barrel != null) rig.BarrelRest = rig.Barrel.localPosition;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
            return rig;
        }

        /// <summary>
        /// Placeholder soldier: hang everything built so far from a hips pivot and stand it on two swinging legs.
        /// </summary>
        static void Legs(Rig rig, float s, Material m)
        {
            var b = rig.Body;
            float hipY = 0.2f * s, legLen = 0.2f * s;
            var kids = new List<Transform>();
            foreach (Transform c in b) kids.Add(c);
            var hips = Empty(b, "hips", new Vector3(0, hipY, 0));
            foreach (var c in kids)
            {
                var lp = c.localPosition;
                c.SetParent(hips, false);
                c.localPosition = lp - hips.localPosition;
            }
            Transform Leg(string name, float x)
            {
                var pivot = Empty(b, name, new Vector3(x, hipY, 0));
                Part(pivot, PrimitiveType.Cube, new Vector3(0, -legLen * 0.5f, 0), new Vector3(0.055f, legLen + 0.02f * s, 0.065f), m);
                return pivot;
            }
            var legL = Leg("leg_l", -0.038f * s);
            var legR = Leg("leg_r", 0.038f * s);
            rig.Gait = new Gait(b, hips, legL, legR, legLen, 1f, ++gaitSeed);
        }

        static void Tank(Rig rig, Material team, Material teamDark, Material steel, Material track, float s, bool twin)
        {
            var b = rig.Body;
            Part(b, PrimitiveType.Cube, new Vector3(-0.27f, 0.11f, 0) * s, new Vector3(0.17f, 0.22f, 0.9f) * s, track);
            Part(b, PrimitiveType.Cube, new Vector3(0.27f, 0.11f, 0) * s, new Vector3(0.17f, 0.22f, 0.9f) * s, track);
            // Stem hull in smoke plastic, team-coloured head (turret) on top: the art pack's 15-25% team mask.
            var hull = Mats.Lit(DarkSteel, 0.5f, 0f);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.22f, 0) * s, new Vector3(0.44f, 0.18f, 0.82f) * s, hull);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.26f, 0.38f) * s, new Vector3(0.42f, 0.1f, 0.12f) * s, hull, new Vector3(-25, 0, 0));
            rig.Turret = Empty(b, "turret", new Vector3(0, 0.34f, -0.04f) * s);
            Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.04f, 0) * s, new Vector3(0.34f, 0.14f, 0.4f) * s, team);
            Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0.08f, 0.13f, -0.08f) * s, new Vector3(0.1f, 0.03f, 0.1f) * s, steel);
            rig.Barrel = Empty(rig.Turret, "barrel", new Vector3(0, 0.05f, 0.2f) * s);
            if (twin)
            {
                Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(-0.06f, 0, 0.25f) * s, new Vector3(0.05f, 0.25f, 0.05f) * s, steel, new Vector3(90, 0, 0));
                Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0.06f, 0, 0.25f) * s, new Vector3(0.05f, 0.25f, 0.05f) * s, steel, new Vector3(90, 0, 0));
            }
            else Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0, 0, 0.22f) * s, new Vector3(0.05f, 0.22f, 0.05f) * s, steel, new Vector3(90, 0, 0));
        }

        // ---- Deep mining placeholders, in the placeholder palette: smoke-plastic hull, team-coloured top, caramel amber
        // for industry. Each is self-contained so it can be deleted when its model lands.
        static Material Smoke => Mats.Lit(DarkSteel, 0.5f, 0f);
        static Material Amber(float glow = 1.6f) => Mats.Glow(PezPalette.EmissiveAmberIndustryDocking, glow);

        /// <summary>Geological Surveyor: a light wheeled vehicle with a sensor mast (Turret) that spins while surveying.</summary>
        static void Surveyor(Rig rig, Material team, Material steel, Material track)
        {
            var b = rig.Body;
            for (int i = 0; i < 4; i++)
                Part(b, PrimitiveType.Cylinder, new Vector3(i % 2 == 0 ? -0.26f : 0.26f, 0.11f, i < 2 ? 0.28f : -0.28f), new Vector3(0.2f, 0.05f, 0.2f), track, new Vector3(0, 0, 90));
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.22f, 0), new Vector3(0.44f, 0.16f, 0.8f), Smoke);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.34f, 0.22f), new Vector3(0.38f, 0.12f, 0.28f), team);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.36f, 0.37f), new Vector3(0.3f, 0.07f, 0.02f), Mats.Lit(new Color(0.15f, 0.25f, 0.35f), 0.95f, 0.4f));
            rig.Turret = Empty(b, "mast", new Vector3(0, 0.3f, -0.18f));
            Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.25f, 0), new Vector3(0.05f, 0.25f, 0.05f), steel);
            Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.52f, 0.06f), new Vector3(0.26f, 0.02f, 0.26f), steel, new Vector3(70, 0, 0));
            Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.54f, 0.1f), Vector3.one * 0.06f, Amber(2f));
        }

        /// <summary>Drill Rig: a slow tracked carrier with its drill tower folded flat along the back.</summary>
        static void DrillRig(Rig rig, Material team, Material steel, Material track)
        {
            var b = rig.Body;
            Part(b, PrimitiveType.Cube, new Vector3(-0.34f, 0.13f, 0), new Vector3(0.2f, 0.26f, 1.3f), track);
            Part(b, PrimitiveType.Cube, new Vector3(0.34f, 0.13f, 0), new Vector3(0.2f, 0.26f, 1.3f), track);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0), new Vector3(0.62f, 0.2f, 1.3f), Smoke);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.5f, 0.45f), new Vector3(0.5f, 0.22f, 0.32f), team);
            // The folded tower: two rails with cross braces, the drill head at the front.
            for (int i = -1; i <= 1; i += 2)
                Part(b, PrimitiveType.Cube, new Vector3(i * 0.14f, 0.47f, -0.2f), new Vector3(0.05f, 0.05f, 1.2f), steel);
            for (int i = 0; i < 5; i++)
                Part(b, PrimitiveType.Cube, new Vector3(0, 0.47f, -0.72f + i * 0.26f), new Vector3(0.28f, 0.03f, 0.03f), steel, new Vector3(0, 35, 0));
            Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.47f, 0.42f), new Vector3(0.12f, 0.14f, 0.12f), Amber(1.2f), new Vector3(90, 0, 0));
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.42f, -0.66f), new Vector3(0.5f, 0.04f, 0.04f), Amber(1.8f));
        }

        /// <summary>Deep Mine (2x2): a derrick headframe over the shaft; its sheave wheel (Turret) turns while pumping.</summary>
        static void DeepMine(Rig rig, Material team, Material steel, Material concrete)
        {
            var b = rig.Body;
            Pad(b, 2, 2, concrete);
            Part(b, PrimitiveType.Cube, new Vector3(0.45f, 0.3f, 0.45f), new Vector3(0.75f, 0.45f, 0.75f), Smoke);
            Part(b, PrimitiveType.Cube, new Vector3(0.45f, 0.56f, 0.45f), new Vector3(0.77f, 0.07f, 0.77f), team);
            Part(b, PrimitiveType.Cylinder, new Vector3(-0.15f, 0.1f, -0.15f), new Vector3(0.6f, 0.04f, 0.6f), Mats.Lit(PezPalette.MaterialsLicorice, 0.2f, 0f));
            // Four-legged derrick leaning in over the shaft.
            for (int i = 0; i < 4; i++)
            {
                float sx = i % 2 == 0 ? -1 : 1, sz = i < 2 ? -1 : 1;
                Part(b, PrimitiveType.Cube, new Vector3(-0.15f + sx * 0.22f, 0.8f, -0.15f + sz * 0.22f), new Vector3(0.05f, 1.45f, 0.05f), steel, new Vector3(-sz * 9f, 0, sx * 9f));
            }
            Part(b, PrimitiveType.Cube, new Vector3(-0.15f, 1.5f, -0.15f), new Vector3(0.3f, 0.06f, 0.3f), team);
            rig.Turret = Empty(b, "sheave", new Vector3(-0.15f, 1.62f, -0.15f));
            Part(rig.Turret, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.34f, 0.025f, 0.34f), steel, new Vector3(0, 0, 90));
            Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.03f, 0.3f, 0.04f), Amber(1.4f));
            Part(b, PrimitiveType.Cube, new Vector3(0.45f, 0.35f, 0.07f), new Vector3(0.5f, 0.05f, 0.02f), Amber(1.6f));
        }

        /// <summary>Concrete foundation covering a w x h footprint, centred on the structure origin.</summary>
        static void Pad(Transform b, int w, int h, Material m)
        {
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.04f, 0), new Vector3(w - 0.08f, 0.08f, h - 0.08f), m);
        }
    }
}
