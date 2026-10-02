using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>Mirrors the sim into GameObjects each frame, interpolating between ticks, and plays event effects.</summary>
    public class WorldView : MonoBehaviour
    {
        public class EV
        {
            public Entity E;
            public Rig Rig;
            public Transform Ring;
            public float Recoil;
        }

        public World World { get; private set; }
        public TerrainView Terrain { get; private set; }
        public readonly Dictionary<int, EV> Views = new Dictionary<int, EV>();
        readonly Dictionary<int, Transform> projectiles = new Dictionary<int, Transform>();
        readonly Dictionary<int, (Vector3 start, float total)> projectileStart = new Dictionary<int, (Vector3, float)>();
        public static readonly Color[] OreColors =
        {
            new Color(0.62f, 0.32f, 0.22f), // iron_ore: rust
            new Color(0.95f, 0.55f, 0.2f),  // copper_ore: orange
            new Color(0.3f, 0.75f, 1f),     // crystal: ice blue
            new Color(0.45f, 1f, 0.25f),    // uranium: toxic green
        };
        long lastSeq;
        float nextFog;
        /// <summary>Team whose fog applies (-1 = spectator sees everything).</summary>
        public int PovTeam = -1;
        public HashSet<int> Selected = new HashSet<int>();

        public static Vector3 W(Vec2 v, float y = 0) => new Vector3(v.X, y, v.Y);
        public static Vec2 S(Vector3 v) => new Vec2(v.x, v.z);
        static float Yaw(float simAngle) => 90f - simAngle * Mathf.Rad2Deg;

        public void Init(World w, int povTeam)
        {
            World = w;
            PovTeam = povTeam;
            var tgo = new GameObject("Terrain");
            tgo.transform.SetParent(transform, false);
            Terrain = tgo.AddComponent<TerrainView>();
            Terrain.Build(w.Map);
            lastSeq = w.Events.Count > 0 ? w.Events[w.Events.Count - 1].Seq : 0;
        }

        bool Shown(Entity e) => PovTeam < 0 || World.IsVisibleTo(PovTeam, e) ||
                                (e.IsStructure && World.Teams[PovTeam].KnownEnemyStructures.ContainsKey(e.Id));

        public void Sync(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            var w = World;
            var seen = new HashSet<int>();
            foreach (var e in w.Entities)
            {
                if (e.Dead) continue;
                seen.Add(e.Id);
                if (!Views.TryGetValue(e.Id, out var v)) Views[e.Id] = v = Create(e);
                bool show = Shown(e);
                if (v.Rig.Root.gameObject.activeSelf != show) v.Rig.Root.gameObject.SetActive(show);
                if (!show) continue;
                UpdateView(v, alpha);
            }
            var gone = new List<int>();
            foreach (var kv in Views) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var id in gone) { Destroy(Views[id].Rig.Root.gameObject); Views.Remove(id); Selected.Remove(id); }

            SyncProjectiles(alpha);
            PlayEvents();
            if (Time.time >= nextFog) { nextFog = Time.time + 0.2f; Terrain.UpdateFog(w, PovTeam); }
        }

        EV Create(Entity e)
        {
            var rig = Models.Build(e.Def.Key, e.Team);
            rig.Root.SetParent(transform, false);
            // Units read better a touch larger than their collision radius.
            if (!e.IsStructure) rig.Body.localScale = Vector3.one * (e.Def.Armor == Armor.Infantry ? 1.5f : 1.2f);
            var ring = Models.Part(rig.Root, PrimitiveType.Cylinder, new Vector3(0, 0.03f, 0), Vector3.one, Mats.Unlit(new Color(0.3f, 1f, 0.4f, 0.55f)));
            float r = e.IsStructure ? Mathf.Max(e.Def.SizeX, e.Def.SizeY) * 0.75f : e.Def.Radius * 2.6f;
            ring.localScale = new Vector3(r, 0.003f, r);
            ring.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.gameObject.SetActive(false);
            var v = new EV { E = e, Rig = rig, Ring = ring };
            if (e.IsStructure) rig.Root.position = W(e.Center);
            return v;
        }

        void UpdateView(EV v, float alpha)
        {
            var e = v.E;
            var rig = v.Rig;
            if (e.IsStructure)
            {
                // Construction: the building rises out of its foundation.
                float p = e.BuildProgress;
                rig.Body.localScale = new Vector3(1, Mathf.Lerp(0.08f, 1f, p), 1);
                if (rig.Turret != null && (e.Def.Key == "optics_lab" || e.Def.Key == "fusion_reactor"))
                {
                    rig.Turret.localRotation = Quaternion.Euler(0, Time.time * (e.Working ? 90f : 8f), 0);
                    float pulse = e.Working ? 1f + Mathf.Sin(Time.time * 4f + e.Id) * 0.08f : 0.8f;
                    rig.Turret.localScale = Vector3.one * pulse;
                }
                else if (rig.Turret != null && e.Def.Key == "radar_dome")
                    rig.Turret.localRotation = Quaternion.Euler(0, e.IsComplete ? Time.time * 50f : 0, 0);
                else if (rig.Turret != null && e.Def.Key == "construction_yard")
                {
                    bool building = World.Teams[e.Team].StructureQueue.Count > 0;
                    rig.Turret.localRotation = Quaternion.Euler(0, building ? Time.time * 40f : 200f, 0);
                }
                else if (rig.Turret != null) rig.Turret.rotation = Quaternion.Euler(0, Yaw(e.TurretFacing), 0);
            }
            else
            {
                var pos = Vec2.Lerp(e.PrevPos, e.Pos, alpha);
                float alt = rig.Altitude > 0 ? rig.Altitude + Mathf.Sin(Time.time * 1.7f + e.Id) * 0.08f : 0;
                rig.Root.position = W(pos, alt);
                var targetRot = Quaternion.Euler(0, Yaw(e.Facing), 0);
                rig.Root.rotation = Quaternion.Slerp(rig.Root.rotation, targetRot, Time.deltaTime * 14f);
                if (rig.Spinner != null && e.IsAir) rig.Spinner.Rotate(0, 1400f * Time.deltaTime, 0, Space.Self);
                if (rig.Spinner != null && !e.IsAir)
                {
                    bool working = e.Order == Order.Harvest && !e.Moving && e.HarvestTile.HasValue;
                    rig.Spinner.Rotate(working ? 600f * Time.deltaTime : 0, 0, 0, Space.Self);
                }
                else if (rig.Turret != null) rig.Turret.rotation = Quaternion.Slerp(rig.Turret.rotation, Quaternion.Euler(0, Yaw(e.TurretFacing), 0), Time.deltaTime * 16f);
                if (rig.Bin != null)
                {
                    rig.Bin.gameObject.SetActive(e.Cargo > 0);
                    if (e.CargoType >= 0) rig.Bin.GetComponent<Renderer>().sharedMaterial = Mats.Glow(OreColors[e.CargoType], 0.8f);
                }
                // Aircraft bank into turns.
                if (e.IsAir) rig.Body.localRotation = Quaternion.Euler(e.Moving ? 6f : 0, 0, Mathf.Clamp(Mathf.DeltaAngle(rig.Root.eulerAngles.y, Yaw(e.Facing)) * 0.6f, -25f, 25f));
                // Infantry bob while walking.
                if (e.Def.Armor == Armor.Infantry) rig.Body.localPosition = new Vector3(0, e.Moving ? Mathf.Abs(Mathf.Sin(Time.time * 12f + e.Id)) * 0.04f : 0, 0);
            }
            if (rig.Barrel != null)
            {
                v.Recoil = Mathf.MoveTowards(v.Recoil, 0, Time.deltaTime * 0.6f);
                rig.Barrel.localPosition = rig.BarrelRest + Vector3.back * v.Recoil;
            }
            bool sel = Selected.Contains(e.Id);
            if (v.Ring.gameObject.activeSelf != sel) v.Ring.gameObject.SetActive(sel);
        }

        void SyncProjectiles(float alpha)
        {
            var live = new HashSet<int>();
            foreach (var p in World.Projectiles)
            {
                live.Add(p.Id);
                if (!projectiles.TryGetValue(p.Id, out var t))
                {
                    var c = p.Weapon.Name == "rocket" ? new Color(1f, 0.55f, 0.2f) : new Color(1f, 0.9f, 0.5f);
                    t = Models.Part(transform, PrimitiveType.Sphere, W(p.Pos, 0.4f), Vector3.one * (p.Weapon.Name == "heavy_cannon" ? 0.14f : 0.1f), Mats.Glow(c, 4f));
                    t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var trail = t.gameObject.AddComponent<TrailRenderer>();
                    trail.sharedMaterial = Mats.Unlit(new Color(c.r, c.g, c.b, 0.5f), true);
                    trail.time = p.Weapon.Name == "rocket" ? 0.5f : 0.12f;
                    trail.startWidth = 0.08f; trail.endWidth = 0f;
                    trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    projectiles[p.Id] = t;
                    var src = Views.TryGetValue(p.SourceId, out var sv) ? sv.Rig.Root.position.y + 0.3f : 0.4f;
                    projectileStart[p.Id] = (W(p.Pos, src), Mathf.Max(0.1f, Vec2.Dist(p.Pos, p.TargetPos)));
                }
                // Height: from the shooter's altitude to the target's, with a lob for artillery.
                var (start, total) = projectileStart[p.Id];
                var ground = Vec2.Lerp(p.PrevPos, p.Pos, alpha);
                float k = Mathf.Clamp01(1f - Vec2.Dist(ground, p.TargetPos) / total);
                float endY = Views.TryGetValue(p.TargetId, out var tv) ? tv.Rig.Root.position.y + 0.3f : 0.3f;
                float arc = p.Weapon.Name == "artillery" ? 4f * k * (1 - k) * Mathf.Min(4f, total * 0.35f) : 0f;
                t.position = W(ground, Mathf.Lerp(start.y, endY, k) + arc);
                bool show = PovTeam < 0 || VisibleTile(p.Pos);
                if (t.gameObject.activeSelf != show) t.gameObject.SetActive(show);
            }
            var dead = new List<int>();
            foreach (var kv in projectiles) if (!live.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var id in dead) { Destroy(projectiles[id].gameObject); projectiles.Remove(id); projectileStart.Remove(id); }
        }

        bool VisibleTile(Vec2 p)
        {
            var t = Int2.Of(p);
            return World.Map.InBounds(t.X, t.Y) && World.Teams[PovTeam].Visible[World.Map.Idx(t.X, t.Y)];
        }

        void PlayEvents()
        {
            var evs = World.Events;
            int start = evs.Count - 1;
            while (start >= 0 && evs[start].Seq > lastSeq) start--;
            for (int i = start + 1; i < evs.Count; i++)
            {
                var ev = evs[i];
                lastSeq = ev.Seq;
                bool visible = PovTeam < 0 || VisibleTile(ev.Pos) || ev.Team == PovTeam;
                if (!visible) continue;
                switch (ev.Type)
                {
                    case "shot":
                        {
                            var from = MuzzleOf(ev.A, ev.Pos);
                            var to = W(ev.Pos2, HeightOf(ev.B, 0.3f));
                            bool beam = ev.Key == "laser" || ev.Key == "beam";
                            if (beam)
                            {
                                var c = ev.Key == "beam" ? new Color(1f, 0.35f, 0.95f, 1f) : new Color(0.35f, 0.95f, 1f, 1f);
                                Fx.Beam(from, to, c, ev.Key == "beam" ? 0.12f : 0.06f);
                                Fx.MuzzleFlash(to, 0.1f);
                            }
                            else
                            {
                                Fx.Tracer(from, to, new Color(1f, 0.85f, 0.4f, 0.9f));
                                Fx.MuzzleFlash(from, 0.06f);
                            }
                            Kick(ev.A, beam ? 0.04f : 0.02f);
                            break;
                        }
                    case "fire":
                        Fx.MuzzleFlash(MuzzleOf(ev.A, ev.Pos), ev.Key == "heavy_cannon" ? 0.2f : 0.13f);
                        Kick(ev.A, 0.08f);
                        break;
                    case "repair":
                    case "heal":
                        {
                            bool heal = ev.Type == "heal";
                            var from = MuzzleOf(ev.A, ev.Pos);
                            var to = W(ev.Pos2, HeightOf(ev.B, heal ? 0.3f : 0.5f));
                            Fx.Beam(from, to, heal ? new Color(0.3f, 1f, 0.45f, 0.8f) : new Color(1f, 0.75f, 0.25f, 0.9f), heal ? 0.05f : 0.035f);
                            if (!heal) Fx.MuzzleFlash(to, 0.05f); // welding sparks
                            break;
                        }
                    case "hit":
                        {
                            float size = ev.Key == "bombs" ? 1.6f : ev.Key == "artillery" ? 0.9f : ev.Key == "heavy_cannon" ? 0.55f : ev.Key == "rocket" || ev.Key == "sam" ? 0.45f : 0.3f;
                            Fx.Explosion(W(ev.Pos, HeightOf(ev.B, 0.2f)), size);
                            if (ev.Key == "bombs" || ev.Key == "artillery") Fx.Scorch(W(ev.Pos), size * 1.3f);
                            break;
                        }
                    case "destroyed":
                        {
                            var def = Defs.Get(ev.Key);
                            float size = def.IsStructure ? def.SizeX * 1.2f : def.Armor == Armor.Infantry ? 0.35f : 1f;
                            if (def.Armor == Armor.Infantry) { Fx.Explosion(W(ev.Pos), 0.2f); break; }
                            float y = Views.TryGetValue(ev.A, out var dv) ? dv.Rig.Root.position.y : 0.3f;
                            Fx.Explosion(W(ev.Pos, y), size);
                            Fx.Debris(W(ev.Pos), def.IsStructure ? 2f : 1f, Mats.Team(ev.Team), def.IsStructure ? 18 : 8);
                            Fx.Scorch(W(ev.Pos), size * 1.2f);
                            if (def.IsStructure) for (int k = 0; k < def.SizeX; k++) Fx.Explosion(W(ev.Pos + new Vec2(Random.Range(-1f, 1f), Random.Range(-1f, 1f)) * def.SizeX * 0.4f, 0.3f), size * 0.5f);
                            break;
                        }
                }
            }
        }

        Vector3 MuzzleOf(int id, Vec2 fallback)
        {
            if (Views.TryGetValue(id, out var v) && v.Rig.Barrel != null)
                return v.Rig.Barrel.position + v.Rig.Barrel.parent.forward * 0.35f;
            return W(fallback, 0.4f);
        }

        float HeightOf(int id, float fallback) => Views.TryGetValue(id, out var v) && v.Rig.Altitude > 0 ? v.Rig.Root.position.y : fallback;

        void Kick(int id, float amount)
        {
            if (Views.TryGetValue(id, out var v)) v.Recoil = amount;
        }
    }
}
