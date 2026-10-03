using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// World-space health and fuel bars (Pez/Bar shader). They live in the scene, not the IMGUI HUD, so the main window,
    /// every player's live stream and look stills all show them. Health shows when a unit or building is hurt (and a
    /// building's progress while it's going up); fuel shows when a vehicle or aircraft is below full. An agent-invented
    /// unit also carries a small amber badge at the bar's left end, always on, so a design reads apart from its base unit.
    /// Bars sit on their own layer, which no camera culls in: PezPost draws them after post-processing
    /// (<see cref="DrawOverlay"/>), so tonemapping, bloom and AO never touch them.
    /// </summary>
    public class Bars
    {
        public const int Layer = 29;
        static readonly List<MeshRenderer> all = new List<MeshRenderer>();
        static readonly List<MaterialPropertyBlock> blocks = new List<MaterialPropertyBlock>();

        /// <summary>Queue every bar that's showing (enabled, in an active entity) into an overlay command buffer.</summary>
        public static void DrawOverlay(UnityEngine.Rendering.CommandBuffer cb)
        {
            if (material == null) return;
            int used = 0;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var r = all[i];
                if (r == null) { all[i] = all[all.Count - 1]; all.RemoveAt(all.Count - 1); continue; }
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (used == blocks.Count) blocks.Add(new MaterialPropertyBlock());
                var b = blocks[used++];
                r.GetPropertyBlock(b);
                cb.DrawMesh(quad, r.localToWorldMatrix, material, 0, 0, b);
            }
        }

        static Mesh quad;
        static Material material;
        static readonly int FillId = Shader.PropertyToID("_Fill"), FillColorId = Shader.PropertyToID("_FillColor"),
                            SizeId = Shader.PropertyToID("_Size"), HazardId = Shader.PropertyToID("_Hazard");
        static readonly Color Fuel = new Color(0.56f, 0.89f, 1f);

        readonly MeshRenderer hp, fuel, badge;
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        float shownHp = -1, shownFuel = -1;
        bool hpHazard, building;
        /// <summary>Whether the fuel bar wants showing at all (views then decide whose fuel they may reveal).</summary>
        public bool FuelWanted { get; private set; }

        public Bars(Transform root, Entity e, float altitude)
        {
            if (quad == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad = tmp.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(tmp);
                material = new Material(Resources.Load<Shader>("PezShaders/PezBar") ?? Shader.Find("Pez/Bar"));
            }
            float width = e.IsStructure ? Mathf.Clamp(Mathf.Max(e.Def.SizeX, e.Def.SizeY) * 0.55f, 1f, 1.8f) : e.Def.Armor == Armor.Infantry ? 0.6f : 0.9f;
            float lift = e.IsStructure ? 1.4f + Mathf.Max(e.Def.SizeX, e.Def.SizeY) * 0.25f : (e.Def.Armor == Armor.Infantry ? 0.95f : 1.05f) + altitude;
            hp = Make(root, "hp_bar", lift);
            fuel = Make(root, "fuel_bar", lift);
            block.SetVector(SizeId, new Vector4(width, 0.12f, 0, 0));
            hp.SetPropertyBlock(block);
            block.SetVector(SizeId, new Vector4(width, 0.08f, -0.11f, 0)); // a thinner strip just under the health bar
            fuel.SetPropertyBlock(block);
            hp.enabled = fuel.enabled = false;
            if (e.Def.OwnerTeam >= 0)
            {
                badge = Make(root, "invented_badge", lift);
                block.SetVector(SizeId, new Vector4(0.16f, 0.16f, 0f, -width * 0.5f - 0.13f));
                block.SetFloat(FillId, 1f);
                block.SetColor(FillColorId, Mats.Amber);
                badge.SetPropertyBlock(block);
            }
        }

        static MeshRenderer Make(Transform root, string name, float lift)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0, lift, 0);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            all.Add(r);
            return r;
        }

        /// <summary>Refresh from the entity (cheap when nothing changed).</summary>
        public void Update(Entity e)
        {
            // Health (or construction progress, in amber, while a building goes up).
            building = e.IsStructure && !e.IsComplete;
            float f = building ? e.BuildProgress : Mathf.Clamp01(e.Hp / e.Def.MaxHp);
            bool want = building || f < 0.999f;
            if (hp.enabled != want) hp.enabled = want;
            if (want && Mathf.Abs(f - shownHp) > 0.004f)
            {
                shownHp = f;
                hp.GetPropertyBlock(block);
                block.SetFloat(FillId, f);
                bool hazard = !building && f < 0.25f;
                block.SetColor(FillColorId, building ? Mats.Amber : f < 0.5f && !hazard ? Mats.Amber : Mats.Cream);
                block.SetFloat(HazardId, hazard ? 1 : 0);
                hp.SetPropertyBlock(block);
            }
            // Fuel.
            float ff = e.Def.UsesFuel ? Mathf.Clamp01(e.FuelFraction) : 1f;
            FuelWanted = e.Def.UsesFuel && (ff < 0.98f || e.Stranded) && !e.Landed;
            if (FuelWanted && Mathf.Abs(ff - shownFuel) > 0.004f)
            {
                shownFuel = ff;
                fuel.GetPropertyBlock(block);
                block.SetFloat(FillId, ff);
                block.SetColor(FillColorId, ff < 0.3f ? Mats.Amber : Fuel);
                block.SetFloat(HazardId, e.Stranded ? 1 : 0);
                fuel.SetPropertyBlock(block);
            }
        }

        /// <summary>Show the fuel bar in this view? Players only see their own units' fuel; spectators see everyone's.</summary>
        public void ForView(Entity e, int team)
        {
            bool show = FuelWanted && (team < 0 || e.Team == team);
            if (fuel.enabled != show) fuel.enabled = show;
        }
    }
}
