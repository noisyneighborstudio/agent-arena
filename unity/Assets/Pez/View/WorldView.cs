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
            // Art-pack model state
            public float LastFire = -99f, DoorTimer;
            public bool Destroyed, Tipped;
            public int PrevCargo;
            public float BuiltShown = -1f;
            public Vector3? SpawnFrom;
            public float SpawnT;
            public float LandK;          // aircraft: 0 flying, 1 parked on its pad (eases between)
            // Hull feel (MOTION.md): nose-up under acceleration, dip when braking, kick back on firing.
            public float HullSpeed, HullPitch, HullPitchVel;
            public int OreTint = -1;     // deep mine: the ore its tube is tinted to; mining truck: the ore its load shows
            public int Born;             // frame the view was made (deploys pair a unit with the structure it became)
            public float BoardT;         // boarding: 0..1 while the passenger shrinks into its carrier
            public Bars Bars;            // world-space health and fuel bars (seen by every camera)
            public bool MainShow = true; // the local view's visibility and fade scale, restored after a stream render
            public float MainScale = 1f;
            // Power state on structures: emissive renderers, the smoothed glow and what was last applied, steam.
            public Renderer[] Glows;
            public float GlowK = -1f, GlowShown = -1f, RateShown = 1f, SteamAcc;
        }

        public World World { get; private set; }
        public TerrainView Terrain { get; private set; }
        public DeepDepositsView Deposits { get; private set; }
        public readonly Dictionary<int, EV> Views = new Dictionary<int, EV>();
        readonly Dictionary<int, Transform> projectiles = new Dictionary<int, Transform>();
        readonly Dictionary<int, (Vector3 start, Vec2 simStart, float total)> projectileStart = new Dictionary<int, (Vector3, Vec2, float)>();
        /// <summary>How far (tiles) a shell flies from the barrel tip before it is exactly on the sim's path.</summary>
        const float MuzzleBlend = 0.12f;
        public static readonly Color[] OreColors =
        {
            PezPalette.OreIronOre,   // cinnamon
            PezPalette.OreCopperOre, // copper with mint patina
            PezPalette.OreCrystal,   // ice
            PezPalette.OreUranium,   // sour acid
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
            Plinths.Clear(); // a new world: no foundations placed yet
            var tgo = new GameObject("Terrain");
            tgo.transform.SetParent(transform, false);
            Terrain = tgo.AddComponent<TerrainView>();
            Terrain.Build(w.Map);
            Deposits = new GameObject("DeepDeposits").AddComponent<DeepDepositsView>();
            Deposits.transform.SetParent(transform, false);
            Deposits.Init(w);
            mapVersion = w.MapVersion;
            lastSeq = w.Events.Count > 0 ? w.Events[w.Events.Count - 1].Seq : 0;
            Fx.Prewarm();
        }

        // When a unit drops out of a team's sight it shrinks away over half a second instead of blinking off.
        const float FogFade = 0.5f;
        readonly Dictionary<(int team, int id), float> lastSeen = new Dictionary<(int, int), float>();

        /// <summary>How much of `e` team `team` sees: 1 in sight, 1 to 0 over FogFade after it slips into the fog.</summary>
        float Visibility(Entity e, int team)
        {
            if (ShownFor(e, team)) { if (team >= 0) lastSeen[(team, e.Id)] = Time.time; return 1f; }
            if (team < 0 || e.IsCarried || e.Dead || e.IsStructure || !lastSeen.TryGetValue((team, e.Id), out var t)) return 0f;
            return Mathf.Clamp01(1f - (Time.time - t) / FogFade);
        }

        bool ShownFor(Entity e, int team) => !e.IsCarried && (team < 0 || World.IsVisibleTo(team, e) ||
                                             (e.IsStructure && World.Teams[team].KnownEnemyStructures.ContainsKey(e.Id)));

        /// <summary>
        /// Temporarily show only what `team` can see (for a per-player stream render); call with PovTeam to restore.
        /// Selection rings belong to the local player and are hidden in other teams' renders.
        /// </summary>
        public void SetPov(int team)
        {
            foreach (var v in Views.Values)
            {
                bool main = team == PovTeam;
                float vis = main ? (v.MainShow ? v.MainScale : 0f) : v.E.Dead ? 0f : Visibility(v.E, team);
                bool show = vis > 0f;
                var go = v.Rig.Root.gameObject;
                if (go.activeSelf != show) go.SetActive(show);
                if (show && v.BoardT <= 0f) v.Rig.Root.localScale = Vector3.one * vis;
                bool ring = team == PovTeam && Selected.Contains(v.E.Id) && !v.E.IsStructure;
                if (v.Ring.gameObject.activeSelf != ring) v.Ring.gameObject.SetActive(ring);
                v.Bars?.ForView(v.E, team); // a player's stream shows only their own units' fuel
            }
            Deposits?.SetPov(team, PovTeam);
        }

        int mapVersion;

        /// <summary>The map grew (a player joined) or salvage appeared (a player left): rebuild the terrain.</summary>
        void RebuildTerrain()
        {
            if (Terrain != null) Destroy(Terrain.gameObject);
            var tgo = new GameObject("Terrain");
            tgo.transform.SetParent(transform, false);
            Terrain = tgo.AddComponent<TerrainView>();
            Terrain.Build(World.Map);
            mapVersion = World.MapVersion;
            nextFog = 0;
            MapRebuilt?.Invoke();
        }

        public System.Action MapRebuilt;

        public void Sync(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            var w = World;
            if (w.MapVersion != mapVersion) RebuildTerrain();
            var seen = new HashSet<int>();
            foreach (var e in w.Entities)
            {
                if (e.Dead) continue;
                seen.Add(e.Id);
                if (!Views.TryGetValue(e.Id, out var v)) Views[e.Id] = v = Create(e);
                // Boarding: the passenger slides into its carrier and shrinks away instead of vanishing.
                if (e.IsCarried && v.BoardT < 1f && v.Rig.Root.gameObject.activeSelf) { Board(v); continue; }
                if (!e.IsCarried && v.BoardT > 0f) { v.BoardT = 0f; v.Rig.Root.localScale = Vector3.one; }
                float vis = Visibility(e, PovTeam);
                bool show = vis > 0f;
                v.MainShow = show; v.MainScale = vis;
                if (v.Rig.Root.gameObject.activeSelf != show) v.Rig.Root.gameObject.SetActive(show);
                if (!show) continue;
                v.Rig.Root.localScale = Vector3.one * vis;
                UpdateView(v, alpha);
                if (v.Bars != null) { v.Bars.Update(e); v.Bars.ForView(e, PovTeam); }
            }
            PlayEvents();
            var gone = new List<int>();
            foreach (var kv in Views) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var id in gone)
            {
                Retire(Views[id]); Views.Remove(id); Selected.Remove(id); Plinths.Unregister(id);
                for (int t = 0; t < 8; t++) lastSeen.Remove((t, id));
            }

            Deposits.Sync(PovTeam);
            SyncProjectiles(alpha);
            if (Time.time >= nextFog) { nextFog = Time.time + 0.2f; Terrain.UpdateFog(w, PovTeam); }
            SyncShields();
        }

        readonly Dictionary<int, Transform> shields = new Dictionary<int, Transform>();

        /// <summary>A translucent, gently pulsing dome over each base under newcomer protection.</summary>
        void SyncShields()
        {
            foreach (var t in World.Teams)
            {
                bool on = World.IsProtected(t.Id);
                shields.TryGetValue(t.Id, out var dome);
                if (!on) { if (dome != null) { Destroy(dome.gameObject); shields.Remove(t.Id); } continue; }
                if (dome == null)
                {
                    // Protection is a status, and status never uses team hues: a faint cream dome for every team.
                    var c = Mats.Cream; c.a = 0.07f;
                    dome = Models.Part(transform, PrimitiveType.Sphere, W(t.StartPos), new Vector3(13f, 7f, 13f), Mats.Unlit(c, true));
                    dome.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    shields[t.Id] = dome;
                }
                float k = 1f + Mathf.Sin(Time.time * 1.5f + t.Id) * 0.02f;
                dome.localScale = new Vector3(13f, 7f, 13f) * k;
            }
        }

        /// <summary>Nothing pops off the map: art-pack models sink or collapse (wrecks linger) before they're destroyed.</summary>
        void Board(EV v)
        {
            v.BoardT = Mathf.Min(1f, v.BoardT + Time.deltaTime / 0.35f);
            var root = v.Rig.Root;
            if (Views.TryGetValue(v.E.CarrierId, out var carrier) && carrier.Rig.Root.gameObject.activeInHierarchy)
                root.position = Vector3.Lerp(root.position, carrier.Rig.Root.position, v.BoardT);
            root.localScale = Vector3.one * (1f - v.BoardT * v.BoardT);
            v.MainShow = v.BoardT < 1f; v.MainScale = 1f - v.BoardT * v.BoardT;
            if (v.BoardT >= 1f) root.gameObject.SetActive(false);
        }

        /// <summary>The structure a deployable unit just turned into (spawned this frame where it stood).</summary>
        EV DeployedInto(EV v)
        {
            foreach (var o in Views.Values)
                if (o.E.Team == v.E.Team && o.E.IsStructure && o.E.Def.Key == v.E.Def.DeploysInto && o.Rig.HasModel && o.Rig.Emerge != null &&
                    Vec2.Dist(o.E.Center, v.E.Pos) <= 3.5f && Time.frameCount - o.Born <= 1)
                    return o;
            return null;
        }

        void Retire(EV v)
        {
            var rig = v.Rig;
            if (rig.HasModel && rig.Root.gameObject.activeInHierarchy && !v.Destroyed && v.E.Def.DeploysInto != null && DeployedInto(v) is EV into)
            {
                // Outpost truck / drill rig: the mast stands up, the chassis sinks and the structure rises in its place.
                rig.Model.transform.SetParent(transform, true);
                into.Rig.Emerge.SetBuildProgress(0f);
                rig.Emerge.StartCoroutine(rig.Emerge.PlayDeployInto(into.Rig.Emerge));
            }
            else if (rig.HasModel && rig.Root.gameObject.activeInHierarchy)
            {
                rig.Model.transform.SetParent(transform, true);
                // Vehicles leave a wreck for a while; infantry and dismantled forces just sink away.
                bool wreck = v.Destroyed && v.E.Def.Armor != Armor.Infantry;
                rig.Emerge.StartCoroutine(rig.Emerge.PlayRemove(wreck, v.E.IsStructure ? 1.2f : 1.5f));
            }
            Destroy(rig.Root.gameObject);
        }

        bool Producing(Entity e)
        {
            var t = World.Teams[e.Team];
            if (e.Def.Key == "command_center") return t.StructureQueue.Count > 0;
            if (e.Def.Key == "radar_dome") return e.IsComplete && !t.LowPower;
            if (e.Def.Produces != Producer.None && t.UnitQueues.TryGetValue(e.Def.Produces, out var q)) return q.Count > 0;
            return e.Working;
        }

        // ---- deep mining (art pack v0.4 hooks)

        /// <summary>Deep mine: the ore tube takes the deposit's colour and drains with its reserve.</summary>
        void DeepMine(EV v)
        {
            var d = World.Map.DepositById(v.E.DepositId);
            if (d == null) return;
            if (v.OreTint != d.Type) { Models.TintOre(v.Rig.Model, d.Type); v.OreTint = d.Type; }
            v.Rig.Motion.SetOreLevel(d.Initial > 0 ? d.Amount / d.Initial : 0);
        }

        // Power plant tower tops in model space (the two M_E_Cyan cores; the pack's glTF x is mirrored on import).
        static readonly Vector3[] Towers = { new Vector3(0.42f, 1.45f, -0.30f), new Vector3(-0.42f, 1.65f, 0.32f) };

        /// <summary>
        /// Power you can see (MOTION.md power_plant; base-building review rec. 1). From the team's PowerUsed,
        /// PowerProduced and LowPower:
        ///  - power plant cores glow 0.6 to 1.0 with the load (eased over 0.4 s); on low power they flicker at 4 Hz
        ///    between 0.25 and 1 with a 0.1 s dropout every 2-3 s; dark until the plant is complete (the sim counts only
        ///    complete plants). Steam rises from both towers, faster under load; on low power it sputters.
        ///  - consumers (Power &lt; 0) dim their emissives to 50% and run their machinery at half speed on low power, as
        ///    the sim halves their production.
        /// </summary>
        void Power(EV v)
        {
            var e = v.E;
            var t = World.Teams[e.Team];
            bool plant = e.Def.Key == "power_plant";
            v.Glows ??= PezShade.Emissive(v.Rig.Model);
            float dt = Time.deltaTime, now = Time.time, glow, rate = 1f;
            if (plant)
            {
                float load = t.PowerProduced > 0 ? Mathf.Clamp01(t.PowerUsed / (float)t.PowerProduced) : 1f;
                float steady = e.IsComplete ? Mathf.Lerp(0.6f, 1f, load) : 0f;
                v.GlowK = v.GlowK < 0f ? steady : Mathf.MoveTowards(v.GlowK, steady, dt / 0.4f);
                glow = v.GlowK;
                if (e.IsComplete && t.LowPower)
                {
                    // 4 Hz flicker, and now and then the cores drop out for a tenth of a second.
                    float ph = now * 4f + e.Id * 0.37f;
                    glow = ph - Mathf.Floor(ph) < 0.5f ? 1f : 0.25f;
                    float cyc = now / 2.5f + e.Id * 0.61f, inCyc = (cyc - Mathf.Floor(cyc)) * 2.5f;
                    if (inCyc < 0.1f) glow = 0.05f;
                }
                if (e.IsComplete) Steam(v, load, t.LowPower);
            }
            else
            {
                float target = e.IsComplete && t.LowPower ? 0.5f : 1f;
                v.GlowK = v.GlowK < 0f ? target : Mathf.MoveTowards(v.GlowK, target, dt / 0.4f);
                glow = v.GlowK;
                rate = e.IsComplete && t.LowPower ? 0.5f : 1f;
            }
            // The lamp body darkens with its light (a cyan core at 0.25 emission still read as lit from its albedo).
            if (Mathf.Abs(glow - v.GlowShown) > 0.01f) { PezShade.Set(v.Glows, Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, glow)), glow); v.GlowShown = glow; }
            if (rate != v.RateShown) { v.Rig.Motion.SetRate(rate); v.RateShown = rate; }
        }

        /// <summary>Cream steam wisps off both towers: one every 0.8/load s each; on low power, bursts of 3 then a 1 s gap.</summary>
        void Steam(EV v, float load, bool low)
        {
            float dt = Time.deltaTime;
            if (low)
            {
                float cyc = Time.time / 1.6f + v.E.Id * 0.3f, inCyc = (cyc - Mathf.Floor(cyc)) * 1.6f;
                if (inCyc >= 0.6f) return; // the gap
                v.SteamAcc += dt * 5f;    // 3 puffs in the burst
            }
            else v.SteamAcc += dt * Mathf.Max(load, 0.2f) / 0.8f;
            while (v.SteamAcc >= 1f)
            {
                v.SteamAcc -= 1f;
                var m = v.Rig.Model.transform;
                for (int i = 0; i < Towers.Length; i++) Fx.Steam(m.TransformPoint(Towers[i]), low ? 0.3f : 0.38f);
            }
        }

        /// <summary>Surveyor: thump while it stands surveying (each slam sends a ripple); moving off cancels.</summary>
        void Survey(EV v)
        {
            var e = v.E;
            var m = v.Rig.Motion;
            if (m.OnThump == null) m.OnThump = () => Deposits.Ripple(v.Rig.Root.position);
            bool surveying = e.Order == Order.Survey && !e.Moving && Vec2.Dist(e.Pos, e.OrderPos) <= 0.6f;
            if (surveying && !m.Surveying) m.Survey();
            else if (!surveying && m.Surveying) m.CancelSurvey();
        }

        /// <summary>Point the model's turret where the sim says it's facing; idle turrets scan.</summary>
        void Aim(EV v)
        {
            var e = v.E;
            bool engaged = Time.time - v.LastFire < 3f || e.Order == Order.Attack;
            if (!engaged) { v.Rig.Motion.ClearAim(); return; }
            var dir = new Vector3(Mathf.Cos(e.TurretFacing), 0, Mathf.Sin(e.TurretFacing));
            v.Rig.Motion.AimAt(v.Rig.Root.position + dir * 5f);
        }

        EV Create(Entity e)
        {
            var rig = Models.Build(e.Def.ModelKey, e.Team); // an invention looks like its base unit (plus a badge: Bars)
            rig.Root.SetParent(transform, false);
            // Units read better a touch larger than their collision radius. Art-pack infantry are built at 0.55 tall;
            // the handoff recommends 1.3-1.4x so they read at game zoom.
            if (!e.IsStructure)
                rig.Body.localScale = Vector3.one * (rig.HasModel ? (e.Def.Armor == Armor.Infantry ? 1.35f : 1f) : (e.Def.Armor == Armor.Infantry ? 1.5f : 1.2f));
            // Selection rings are cream for every team (HUD kit); ownership already shows on the model.
            var ring = Models.SelectionRing(rig.Root, Mats.Unlit(new Color(Mats.Cream.r, Mats.Cream.g, Mats.Cream.b, 0.9f)));
            ring.localPosition = new Vector3(0, 0.03f, 0);
            float r = e.IsStructure ? Mathf.Max(e.Def.SizeX, e.Def.SizeY) * 0.75f : e.Def.Radius * 2.6f;
            ring.localScale = new Vector3(r, 1f, r);
            ring.gameObject.SetActive(false);
            var v = new EV { E = e, Rig = rig, Ring = ring, Born = Time.frameCount, Bars = e.IsMine ? null : new Bars(rig.Root, e, 0f) };
            if (e.IsStructure) { rig.Root.position = W(e.Center); Plinths.Register(e.Id, rig.Root.position, rig.Plinth); }
            return v;
        }

        void UpdateView(EV v, float alpha)
        {
            var e = v.E;
            var rig = v.Rig;
            if (e.IsStructure && rig.HasModel)
            {
                // Build stages rise out of the pad as construction progresses.
                if (!Mathf.Approximately(v.BuiltShown, e.BuildProgress)) { rig.Emerge.SetBuildProgress(e.BuildProgress); v.BuiltShown = e.BuildProgress; }
                rig.Motion.SetWorking(e.IsComplete && Producing(e));
                if (e.Def.Key == "power_plant" || e.Def.Power < 0) Power(v);
                if (e.Def.Key == "deep_mine") DeepMine(v);
                if (rig.Turret != null) Aim(v);
                if (v.DoorTimer > 0 && (v.DoorTimer -= Time.deltaTime) <= 0) rig.Motion.SetDoorOpen(false);
            }
            else if (e.IsStructure)
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
                else if (rig.Turret != null && e.Def.Key == "deep_mine")
                    rig.Turret.localRotation = Quaternion.Euler(e.Working ? Time.time * 140f : 0, 0, 0); // the sheave turns while it pumps
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
                if (e.IsAir)
                {
                    // Out of fuel trips end with the aircraft settling onto its pad, and lifting off again after.
                    v.LandK = Mathf.MoveTowards(v.LandK, e.Landed ? 1f : 0f, Time.deltaTime * 0.8f);
                    if (rig.Altitude > 0) alt = Mathf.Lerp(alt, 0.55f, v.LandK * v.LandK * (3f - 2f * v.LandK));
                }
                rig.Root.position = W(pos, alt);
                if (v.SpawnFrom.HasValue)
                {
                    // New units roll out of their producer's door (or rise off the airfield lift) instead of popping in.
                    v.SpawnT = Mathf.Min(1f, v.SpawnT + Time.deltaTime / 0.9f);
                    float k = 1f - Mathf.Pow(1f - v.SpawnT, 3f);
                    rig.Root.position = Vector3.Lerp(v.SpawnFrom.Value, rig.Root.position, k);
                    if (v.SpawnT >= 1f) v.SpawnFrom = null;
                }
                // Ground units ride up onto a structure's plinth and down its driveway ramp (leaving a producer, docking).
                if (!e.IsAir) { var rp = rig.Root.position; float py = Plinths.HeightAt(rp); if (py > 0f) { rp.y += py; rig.Root.position = rp; } }
                var targetRot = Quaternion.Euler(0, Yaw(e.Facing), 0);
                rig.Root.rotation = Quaternion.Slerp(rig.Root.rotation, targetRot, Time.deltaTime * 14f);
                if (!e.IsAir && e.Def.Armor != Armor.Infantry) HullFeel(v);
                if (rig.HasModel)
                {
                    if (rig.Turret != null) Aim(v);
                    if (e.IsAir) rig.Motion.SetWorking(true);
                    if (e.Def.Key == "geological_surveyor") Survey(v);
                    if (e.IsHarvester)
                    {
                        rig.Motion.SetBinLoad(e.Cargo / (float)e.Def.HarvestCapacity);
                        // The load shows the ore it really is (crystal and uranium glow). The bin keeps the last colour
                        // while it empties (the sim clears CargoType at zero).
                        if (e.CargoType >= 0 && e.CargoType != v.OreTint) { Models.TintOre(rig.Model, e.CargoType); v.OreTint = e.CargoType; }
                        rig.Motion.SetWorking(e.Order == Order.Harvest && !e.Moving && e.HarvestTile.HasValue);
                        // In a bay the bed follows the sim's dock steps: it tips during Unload's settle and holds while the
                        // ore goes in, then lowers in PullOut's first 0.25 s.
                        rig.Motion.SetBinTipped(e.Order == Order.ReturnOre && e.Dock == DockStep.Unload);
                        if (e.Order == Order.ReturnOre && e.Dock == DockStep.Unload && !v.Tipped) { v.Tipped = true; OpenDoorNear(e.Team, e.Pos, 2.5f, 2f); }
                        // Unloading beside a drop-off whose lane is built over: the old one-shot tip, on the first ore out.
                        if (e.Order == Order.ReturnOre && e.Dock < DockStep.Align && e.Cargo < v.PrevCargo && !v.Tipped)
                        {
                            rig.Motion.TipBin();
                            v.Tipped = true;
                            OpenDoorNear(e.Team, e.Pos, 2.5f, 2f);
                        }
                        if (e.Cargo == 0) v.Tipped = false;
                        v.PrevCargo = e.Cargo;
                    }
                }
                else
                {
                    // Procedural placeholder (units the art pack doesn't cover yet).
                    if (rig.Spinner != null && e.IsAir) rig.Spinner.Rotate(0, 1400f * Time.deltaTime, 0, Space.Self);
                    else if (rig.Spinner != null)
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
                }
                // Aircraft bank into turns.
                if (e.IsAir) rig.Body.localRotation = Quaternion.Euler(e.Moving ? 6f : 0, 0, Mathf.Clamp(Mathf.DeltaAngle(rig.Root.eulerAngles.y, Yaw(e.Facing)) * 0.6f, -25f, 25f));
                // Infantry walk: the stride follows the ground actually covered this frame, and they settle when they stop.
                if (rig.Gait != null) rig.Gait.Tick(rig.Root, Time.deltaTime);
            }
            if (rig.Barrel != null && !rig.HasModel)
            {
                v.Recoil = Mathf.MoveTowards(v.Recoil, 0, Time.deltaTime * 0.6f);
                rig.Barrel.localPosition = rig.BarrelRest + Vector3.back * v.Recoil;
            }
            bool sel = Selected.Contains(e.Id) && !e.IsStructure; // structures get the HUD's corner brackets instead
            if (v.Ring.gameObject.activeSelf != sel) v.Ring.gameObject.SetActive(sel);
        }

        /// <summary>Acceleration pitches the hull nose-up 2 degrees, braking dips it 3 (a 0.25 s spring on the visual body).</summary>
        void HullFeel(EV v)
        {
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            float speed = Vec2.Dist(v.E.PrevPos, v.E.Pos) / World.Dt;
            float smoothed = Mathf.Lerp(v.HullSpeed, speed, 1f - Mathf.Exp(-dt * 6f));
            float accel = (smoothed - v.HullSpeed) / dt;
            v.HullSpeed = smoothed;
            float target = Mathf.Clamp(-accel * 1.5f, -2f, 3f); // nose-up is negative pitch
            v.HullPitch = Mathf.SmoothDamp(v.HullPitch, target, ref v.HullPitchVel, 0.25f, Mathf.Infinity, dt);
            v.Rig.Body.localRotation = Quaternion.Euler(v.HullPitch, 0, 0);
        }

        void SyncProjectiles(float alpha)
        {
            var live = new HashSet<int>();
            foreach (var p in World.Projectiles)
            {
                live.Add(p.Id);
                if (!projectiles.TryGetValue(p.Id, out var t))
                {
                    // The shell leaves the real barrel tip (art-pack models), not the sim's 2D muzzle at a fixed height.
                    Vector3 from;
                    if (Views.TryGetValue(p.SourceId, out var sv) && sv.Rig.HasMuzzle && sv.Rig.Root.gameObject.activeInHierarchy) from = MuzzleOf(p.SourceId, p.PrevPos);
                    else from = W(p.PrevPos, sv != null ? sv.Rig.Root.position.y + 0.3f : 0.4f);
                    var c = p.Weapon.Name == "rocket" ? new Color(1f, 0.55f, 0.2f) : new Color(1f, 0.9f, 0.5f);
                    t = Models.Part(transform, PrimitiveType.Sphere, from, Vector3.one * (p.Weapon.Name == "heavy_cannon" ? 0.14f : 0.1f), Mats.Glow(c, 4f));
                    t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var trail = t.gameObject.AddComponent<TrailRenderer>();
                    trail.sharedMaterial = Mats.Unlit(new Color(c.r, c.g, c.b, 0.5f), true);
                    trail.time = p.Weapon.Name == "rocket" ? 0.5f : 0.12f;
                    trail.startWidth = 0.08f; trail.endWidth = 0f;
                    trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    projectiles[p.Id] = t;
                    projectileStart[p.Id] = (from, p.PrevPos, Mathf.Max(0.1f, Vec2.Dist(p.PrevPos, p.TargetPos)));
                }
                // Height: from the barrel tip to the target's height, with a lob for artillery. Across the ground the shell
                // starts at the tip and joins the sim's path within MuzzleBlend tiles of flight.
                var (start, simStart, total) = projectileStart[p.Id];
                var ground = Vec2.Lerp(p.PrevPos, p.Pos, alpha);
                float k = Mathf.Clamp01(1f - Vec2.Dist(ground, p.TargetPos) / total);
                float endY = Views.TryGetValue(p.TargetId, out var tv) ? tv.Rig.Root.position.y + 0.3f : 0.3f;
                float arc = p.Weapon.Name == "artillery" ? 4f * k * (1 - k) * Mathf.Min(4f, total * 0.35f) : 0f;
                var pos = W(ground, Mathf.Lerp(start.y, endY, k) + arc);
                float off = 1f - Mathf.SmoothStep(0f, 1f, Vec2.Dist(ground, simStart) / MuzzleBlend);
                if (off > 0f) { pos.x += (start.x - simStart.X) * off; pos.z += (start.z - simStart.Y) * off; }
                t.position = pos;
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
                                // Lasers (including the laser tank's beam cannon) are rock-candy cyan; magenta is for plasma only.
                                Fx.Beam(from, to, PezPalette.EmissiveCyanLaserOptics, ev.Key == "beam" ? 0.12f : 0.06f);
                                Fx.MuzzleFlash(to, 0.1f);
                            }
                            else if (ev.Key == "c4")
                            {
                                // Charge planted and blown: no tracer, a big blast on the target.
                                Fx.Explosion(to, 1.1f);
                                Fx.Scorch(W(ev.Pos2), 1.2f);
                            }
                            else
                            {
                                Fx.Tracer(from, to, ev.Key == "sniper" ? new Color(0.6f, 0.95f, 1f, 1f) : new Color(1f, 0.85f, 0.4f, 0.9f));
                                Fx.MuzzleFlash(from, ev.Key == "sniper" ? 0.1f : 0.06f);
                                Fx.BulletImpact(to);
                            }
                            Kick(ev.A, beam ? 0.04f : 0.02f);
                            break;
                        }
                    case "fire":
                        Fx.MuzzleFlash(MuzzleOf(ev.A, ev.Pos), ev.Key == "heavy_cannon" ? 0.2f : 0.13f);
                        Kick(ev.A, 0.08f);
                        break;
                    case "trained":
                        if (Views.TryGetValue(ev.A, out var tv2))
                        {
                            var def2 = Defs.Get(ev.Key);
                            var producer = OpenDoorNear(ev.Team, ev.Pos, 3f, 1.6f);
                            if (producer != null)
                            {
                                if (def2.IsAir && producer.E.Def.Key == "airfield") { producer.Rig.Motion.SnapLiftDown(); producer.Rig.Motion.SetLiftUp(true); }
                                // Start at the door (south face) and roll out to where the sim placed it.
                                var c = producer.E.Center;
                                tv2.SpawnFrom = def2.IsAir ? W(c, 0.2f) : W(new Vec2(c.X, producer.E.Origin.Y + 0.3f));
                                tv2.SpawnT = 0;
                            }
                        }
                        break;
                    case "salvaged":
                        // A leaving player's base turns to salvage: a puff of sugar dust, not a silent swap.
                        { var sd = Defs.Get(ev.Key); Fx.Salvaged(W(ev.Pos), sd != null && sd.IsStructure ? sd.SizeX : 1f); }
                        break;
                    case "captured":
                        Fx.Beam(W(ev.Pos, 0.2f), W(ev.Pos, 4f), Mats.Team(ev.Team), 0.25f);
                        Fx.MuzzleFlash(W(ev.Pos, 0.6f), 0.4f);
                        break;
                    case "repair":
                    case "heal":
                        {
                            bool heal = ev.Type == "heal";
                            var from = MuzzleOf(ev.A, ev.Pos);
                            var to = W(ev.Pos2, HeightOf(ev.B, heal ? 0.3f : 0.5f));
                            // Heal beam: team-neutral cream-white. Welding beam: caramel amber.
                            Fx.Beam(from, to, heal ? new Color(0.96f, 0.95f, 0.91f, 0.8f) : new Color(Mats.Amber.r, Mats.Amber.g, Mats.Amber.b, 0.9f), heal ? 0.05f : 0.035f);
                            if (!heal) Fx.MuzzleFlash(to, 0.05f); // welding sparks
                            break;
                        }
                    case "hit":
                        Fx.Hit(ev.Key, W(ev.Pos, HeightOf(ev.B, 0.2f))); // scale and character by weapon
                        break;
                    case "destroyed":
                        {
                            Views.TryGetValue(ev.A, out var dead);
                            if (dead != null) dead.Destroyed = true;
                            var def = Defs.Get(ev.Key);
                            var team = Mats.Team(ev.Team);
                            if (def.Armor == Armor.Infantry) Fx.InfantryDeath(W(ev.Pos), team);
                            else if (def.IsStructure) Fx.BuildingDestroyed(W(ev.Pos), def.SizeX, team);
                            else if (def.IsMine) Fx.Mine(W(ev.Pos));
                            else if (def.IsAir && dead != null && dead.Rig.Root.position.y > 0.5f)
                            {
                                // The airframe falls burning and blows up where it hits the ground; Fx takes the model.
                                var root = dead.Rig.Root;
                                Transform hulk = null;
                                if (dead.Rig.HasModel && root.gameObject.activeInHierarchy) { hulk = dead.Rig.Model.transform; hulk.SetParent(null, true); root.gameObject.SetActive(false); }
                                Fx.AircraftDestroyed(hulk, root.position, root.forward * def.Speed * 0.6f, team);
                            }
                            else Fx.VehicleDestroyed(W(ev.Pos, dead != null ? Mathf.Max(0.3f, dead.Rig.Root.position.y) : 0.3f), team);
                            break;
                        }
                }
            }
        }

        /// <summary>Where a shot leaves the shooter: the real barrel tip on art-pack models (it follows the turret's yaw,
        /// the barrel's pitch and its recoil), a fixed reach on procedural placeholders.</summary>
        Vector3 MuzzleOf(int id, Vec2 fallback)
        {
            if (Views.TryGetValue(id, out var v) && v.Rig.Barrel != null)
                return v.Rig.HasMuzzle ? v.Rig.Barrel.TransformPoint(v.Rig.MuzzleLocal) : v.Rig.Barrel.position + v.Rig.Barrel.parent.forward * 0.35f;
            return W(fallback, 0.4f);
        }

        float HeightOf(int id, float fallback) => Views.TryGetValue(id, out var v) && v.Rig.Altitude > 0 ? v.Rig.Root.position.y : fallback;

        void Kick(int id, float amount)
        {
            if (!Views.TryGetValue(id, out var v)) return;
            v.LastFire = Time.time;
            if (v.Rig.HasModel) v.Rig.Motion.Fire(); else v.Recoil = amount;
            if (!v.E.IsStructure && !v.E.IsAir && v.E.Def.Armor != Armor.Infantry) v.HullPitch -= 1.5f; // hull kicks back (nose up)
        }

        /// <summary>Open the roll-up door of the team's structure nearest a point (unit exits, truck docking).</summary>
        EV OpenDoorNear(int team, Vec2 p, float within, float seconds)
        {
            EV best = null; float bd = within;
            foreach (var v in Views.Values)
            {
                if (v.E.Team != team || !v.E.IsStructure || !v.Rig.HasModel) continue;
                float d = v.E.DistFrom(p);
                if (d < bd) { bd = d; best = v; }
            }
            if (best == null) return null;
            best.Rig.Motion.SetDoorOpen(true);
            best.DoorTimer = seconds;
            return best;
        }
    }
}
