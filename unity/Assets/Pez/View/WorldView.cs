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
            // Mass in motion: the heavy's start squat, artillery deployed (0..1) and whether it has rammed this cycle.
            public float StartSquat, Deploy;
            public bool Rammed;
            public int OreTint = -1;     // deep mine: the ore its tube is tinted to; mining truck: the ore its load shows
            public float DustNext;       // decaying structure: when its next grit falls
            public int TeamShown;        // the team colour its model wears (a captured building changes hands)
            public int Born;             // frame the view was made (deploys pair a unit with the structure it became)
            public float BoardT;         // boarding: 0..1 while the passenger shrinks into its carrier
            public Bars Bars;            // world-space health and fuel bars (seen by every camera)
            public bool MainShow = true; // the local view's visibility and fade scale, restored after a stream render
            public float MainScale = 1f;
            // Power state on structures: emissive renderers, the smoothed glow and what was last applied, steam.
            public Renderer[] Glows;
            public float GlowK = -1f, GlowShown = -1f, RateShown = 1f;
            public float[] SteamNext;    // per tower: when it next puffs (each tower on its own irregular rhythm)
            public bool GlowHidden;
            public Renderer[] Halos;     // power plant: the light rising out of its open stacks      // the neutral "last seen" glow is applied (a stream render of fogged enemy state)
            // Damage state on finished buildings: smoke and fire intensity (eased), emitters, and the roof they rise from.
            public float SmokeK, FireK;
            public Vector3[] FireSpots;  // where a burning building's flames rise from (fixed per building)
            public BuildingFire Fire = new BuildingFire();
            public Bounds? Roof;
            // Drop-offs: the dock lamps and chute, and what the bay's truck reported this frame or last.
            public DockView Dock;
            public int DockFrame = -10;
            public DockView.Phase DockBest;
            // Construction: the staked site, the last progress seen (stage landings) and the crane's beam cadence.
            public SiteView Site;
            public float PrevBuild = -1f, BeamT;
            // The dump: the last bay step seen, the laden squat (eased), and the pour's dust cadence.
            public DockStep PrevDock;
            // Roll-out anticipation: the lamps' blink factor, the vent puffs already given, the dark bay behind the door.
            public float Blink = 1f;
            public int Vents;
            public Transform Bay, Door;
            public float DoorHeight;
            public float Squat, PourDust;
        }

        public World World { get; private set; }
        public TerrainView Terrain { get; private set; }
        public DeepDepositsView Deposits { get; private set; }
        public readonly Dictionary<int, EV> Views = new Dictionary<int, EV>();
        readonly Dictionary<int, Transform> projectiles = new Dictionary<int, Transform>();
        readonly Dictionary<int, (Vector3 start, Vec2 simStart, float total)> projectileStart = new Dictionary<int, (Vector3, Vec2, float)>();
        /// <summary>How far (tiles) a shell flies from the barrel tip before it is exactly on the sim's path.</summary>
        const float MuzzleBlend = 0.12f;
        readonly Dictionary<int, Transform> shellShadows = new Dictionary<int, Transform>();
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

        readonly List<int> audience = new List<int>();
        static MaterialPropertyBlock haloBlock;

        /// <summary>
        /// The cameras that may see a building's state effects right now: the main view (if its team sees the building,
        /// or it's a spectator view) and each player's stream whose team sees it. Remembered-but-unseen enemy buildings
        /// show none of their current state.
        /// </summary>
        List<int> AudienceFor(Entity e)
        {
            audience.Clear();
            if (PovTeam < 0 || e.Team == PovTeam || World.IsVisibleTo(PovTeam, e)) audience.Add(TerrainView.MainFogLayer);
            for (int t = 0; t < Mathf.Min(8, World.Teams.Count); t++)
                if (e.Team == t || World.IsVisibleTo(t, e)) audience.Add(TerrainView.TeamFogLayerBase + t);
            return audience;
        }

        /// <summary>Does this view's team see a building's live state (power, damage), or only remember it under fog?</summary>
        bool SeesState(Entity e, int team) => team < 0 || e.Team == team || World.IsVisibleTo(team, e);

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
                // A remembered enemy building under fog shows no live power state: neutral glow in that render.
                if (show && v.Glows != null && v.GlowShown >= 0f)
                {
                    bool hide = !SeesState(v.E, team);
                    if (hide != v.GlowHidden)
                    {
                        float g = hide ? 0.8f : v.GlowShown;
                        PezShade.Set(v.Glows, Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, g)), g);
                        v.GlowHidden = hide;
                    }
                }
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
                bool on = World.IsProtected(t.Id) && World.Showcase == null; // the kitchen sink protects everyone: no domes there
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
            if (e.Team < 0) return false; // neutral: idle until someone takes it
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
        static readonly Vector3[] Towers = { new Vector3(0.42f, 1.78f, -0.30f), new Vector3(-0.42f, 1.78f, 0.32f) }; // the mouths of the open stacks (PowerCores)

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
                // An offline defence (upkeep unpaid) goes nearly dark and stops dead; low power halves everything.
                float target = e.Offline ? 0.1f : e.IsComplete && t.LowPower ? 0.5f : 1f;
                v.GlowK = v.GlowK < 0f ? target : Mathf.MoveTowards(v.GlowK, target, dt / 0.4f);
                glow = v.GlowK;
                rate = e.Offline ? 0f : e.IsComplete && t.LowPower ? 0.5f : 1f;
            }
            // The lamp body darkens with its light (a cyan core at 0.25 emission still read as lit from its albedo).
            glow *= v.Blink;
            if (!SeesState(e, PovTeam)) glow = 0.8f; // remembered under fog: no live state
            if (v.GlowHidden || Mathf.Abs(glow - v.GlowShown) > 0.01f)
            {
                PezShade.Set(v.Glows, Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, glow)), glow);
                v.GlowShown = glow; v.GlowHidden = false;
                // The light rising out of the power plant's open stacks follows the cores.
                v.Halos ??= System.Array.FindAll(v.Rig.Model.GetComponentsInChildren<Renderer>(true), r => r.name == PowerCores.HaloName);
                if (v.Halos.Length > 0)
                {
                    haloBlock ??= new MaterialPropertyBlock();
                    haloBlock.SetColor("_Color", new Color(1f, 1f, 1f, Mathf.Clamp01(glow)));
                    foreach (var h in v.Halos) h.SetPropertyBlock(haloBlock);
                }
            }
            if (rate != v.RateShown) { v.Rig.Motion.SetRate(rate); v.RateShown = rate; }
        }

        static readonly float[] StageLands = { 0.10f, 0.45f, 0.75f };
        static readonly Color BeamCream = new Color(0.93f, 0.89f, 0.82f, 0.9f);

        /// <summary>
        /// Construction as an interaction (base-building review rec. 2; fix 7), all driven by the sim's BuildProgress and
        /// StructureQueue, so low power slows it automatically and nothing waits on the view:
        ///  - the site is staked out from the moment it's placed (SiteView), with a pallet of bricks while it's queued;
        ///  - each stage landing (progress crossing 0.10, 0.45, 0.75) kicks dust out at the footprint's corners;
        ///  - the command center's crane slews to the queue head and a cream construction beam runs from its hook to the
        ///    rising structure every 0.25 s (stuttering on low power); with nothing queued the crane idles in a sweep.
        /// </summary>
        void Construction(EV v)
        {
            var e = v.E;
            float dt = Time.deltaTime;
            if (v.Site != null)
            {
                var q = World.Teams[e.Team].StructureQueue;
                bool queued = !e.IsComplete && e.BuildProgress <= 0f && (q.Count == 0 || q[0].StructureId != e.Id);
                v.Site.Tick(e.IsComplete, queued, dt);
                if (v.Site.Done) v.Site = null;
            }
            float p = e.BuildProgress;
            if (v.PrevBuild >= 0f && p > v.PrevBuild && !e.IsComplete)
                foreach (var t in StageLands)
                    if (v.PrevBuild < t && p >= t) Fx.StageDust(v.Rig.Root.position, e.Def.SizeX, e.Def.SizeY);
            v.PrevBuild = p;
            if (e.Def.Key == "command_center" && e.IsComplete) Crane(v);
        }

        void Crane(EV v)
        {
            var team = World.Teams[v.E.Team];
            var head = team.StructureQueue.Count > 0 ? World.Get(team.StructureQueue[0].StructureId) : null;
            var m = v.Rig.Motion;
            m.SetCraneTarget(head != null, head != null ? W(head.Center) : Vector3.zero);
            if (head == null || head.IsComplete || !Views.TryGetValue(head.Id, out var hv)) return;
            if ((v.BeamT -= Time.deltaTime) > 0f) return;
            v.BeamT = 0.25f;
            if (team.LowPower && ((int)(Time.time * 8f + v.E.Id * 0.37f) & 1) == 1) return; // stutters at 4 Hz on low power
            float top = 0.15f + Mathf.Max(0.05f, head.BuildProgress) * Mathf.Max(head.Def.SizeX, head.Def.SizeY) * 0.55f;
            var to = hv.Rig.Root.position + new Vector3(Random.Range(-0.1f, 0.1f), top, Random.Range(-0.1f, 0.1f));
            Fx.Beam(m.CraneHook, to, BeamCream, 0.04f, lamp: false);
            Fx.MuzzleFlash(to, 0.05f); // welding sparks at the work
        }

        /// <summary>
        /// Damage states on finished buildings, from the building's health (never on construction sites, which are low on
        /// health by design). Three stages that blend into each other:
        ///  - smouldering (below 50%): thin grey wisps off the roof, a dull orange glow on it, the odd ember lifting off;
        ///  - standing fire (from about 32%): tongues of flame from one, then two fixed spots on the roof, darker smoke, a
        ///    steady flickering light on the ground and a stream of embers;
        ///  - raging (below about 12%): three spots, taller and denser flames, a thick black column, embers streaming
        ///    downwind.
        /// Fades in over 0.5 s and out over 2 s (repairs put it out). Everything drifts with FxSystems.Wind.
        /// </summary>
        void Damage(EV v)
        {
            var e = v.E;
            float hp = e.Hp / Mathf.Max(1f, e.Def.MaxHp), dt = Time.deltaTime, now = Time.time;
            float smoke = e.IsComplete && hp < 0.5f ? 1f : 0f;
            // Flames exactly when the sim says it's burning (World.BurnBelow), growing as it burns down.
            float fire = e.IsComplete ? Mathf.Clamp01(Mathf.InverseLerp(World.BurnBelow, 0.06f, hp)) : 0f;
            if (e.IsComplete && hp < World.BurnBelow) fire = Mathf.Max(fire, 0.05f);
            v.SmokeK = Mathf.MoveTowards(v.SmokeK, smoke, dt / (smoke > v.SmokeK ? 0.5f : 2f));
            v.FireK = Mathf.MoveTowards(v.FireK, fire, dt / (fire > v.FireK ? 0.8f : 2f));
            if (v.SmokeK <= 0f && v.FireK <= 0f) return;
            if (v.Roof == null)
            {
                // Once, from the finished model: the roof (bounds), and three fire spots on it, fixed for this building.
                var b = new Bounds(v.Rig.Root.position, Vector3.zero);
                foreach (var r in v.Rig.Model.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                v.Roof = b;
                var rnd = new System.Random(e.Id * 7919 + 17);
                float R(float a, float c) => a + (float)rnd.NextDouble() * (c - a);
                v.FireSpots = new Vector3[3];
                for (int i = 0; i < 3; i++)
                    v.FireSpots[i] = new Vector3(b.center.x + R(-0.55f, 0.55f) * b.extents.x, b.max.y * R(0.78f, 0.92f), b.center.z + R(-0.55f, 0.55f) * b.extents.z);
            }
            float size = Mathf.Sqrt(Mathf.Max(1, e.Def.SizeX));
            Fx.Audience = AudienceFor(e);
            v.Fire.Tick(v.FireSpots, v.Roof.Value.center, size, v.SmokeK, v.FireK, e.Id);
            Fx.Audience = null;
        }

        /// <summary>
        /// Cream steam wisps off each tower, every tower on its own irregular rhythm (about one every 0.8/load s, each gap
        /// jittered), so no two towers, here or on other plants, puff in step. On low power the towers sputter: quick
        /// clusters of puffs with uneven pauses.
        /// </summary>
        void Steam(EV v, float load, bool low)
        {
            float now = Time.time;
            if (v.SteamNext == null)
            {
                v.SteamNext = new float[Towers.Length];
                for (int i = 0; i < Towers.Length; i++) v.SteamNext[i] = now + Random.Range(0f, 1.5f);
            }
            bool emitted = false;
            for (int i = 0; i < Towers.Length; i++)
            {
                if (now < v.SteamNext[i]) continue;
                if (!emitted) { Fx.Audience = AudienceFor(v.E); emitted = true; }
                Fx.Steam(v.Rig.Model.transform.TransformPoint(Towers[i]), low ? 0.3f : 0.38f);
                float gap = low ? (Random.value < 0.3f ? Random.Range(0.7f, 1.5f) : Random.Range(0.12f, 0.3f))
                                : 0.8f / Mathf.Max(load, 0.2f) * Random.Range(0.6f, 1.4f);
                v.SteamNext[i] = Mathf.Max(v.SteamNext[i] + gap, now + 0.05f);
            }
            if (emitted) Fx.Audience = null;
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

        /// <summary>Point the model's turret where the sim says it's facing; idle turrets rest (PezMotion: hold, settle, an occasional glance).</summary>
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
            // Units drawn with another unit's model (new drones without art yet): their own size and flying height.
            if (e.Def.ModelAs != null && rig.Model != null && e.Def.ModelScale != 1f) rig.Model.transform.localScale *= e.Def.ModelScale;
            if (e.Def.HighAltitude) rig.Altitude = 5f;
            else if (e.Def.IsAir && rig.Altitude <= 0f && e.Def.ModelAs != null) rig.Altitude = 2.2f;
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
            var v = new EV { E = e, Rig = rig, Ring = ring, Born = Time.frameCount, Bars = e.IsMine ? null : new Bars(rig.Root, e, 0f), TeamShown = e.Team };
            if (e.IsStructure) { rig.Root.position = W(e.Center); Plinths.Register(e.Id, rig.Root.position, rig.Plinth); }
            if (e.IsStructure && !e.IsComplete && rig.HasModel) v.Site = new SiteView(rig.Root, e.Def.SizeX, e.Def.SizeY);
            if (e.IsStructure && e.Def.DropOff && rig.HasModel)
            {
                var (bay, _, facing, _) = World.Bay(e);
                v.Dock = DockView.For(rig, e.Def.SizeX * 0.5f, e.Def.SizeY * 0.5f, facing, bay);
            }
            return v;
        }

        void UpdateView(EV v, float alpha)
        {
            var e = v.E;
            var rig = v.Rig;
            if (e.IsStructure && rig.HasModel && v.TeamShown != e.Team)
            {
                // Captured (an engineer took it; a derrick claimed or released): it wears its new owner's colours.
                Models.TintTeam(rig.Model, e.Team);
                v.TeamShown = e.Team;
            }
            if (e.IsStructure && rig.HasModel)
            {
                // Build stages rise out of the pad as construction progresses.
                if (!Mathf.Approximately(v.BuiltShown, e.BuildProgress)) { rig.Emerge.SetBuildProgress(e.BuildProgress); v.BuiltShown = e.BuildProgress; }
                rig.Motion.SetWorking(e.IsComplete && Producing(e));
                if (e.IsComplete && e.Def.Produces != Producer.None && e.Def.Key != "command_center" && e.Def.Key != "airfield") RollOut(v);
                if (e.Def.Key == "power_plant" || e.Def.Power < 0) Power(v);
                if (e.IsComplete || v.SmokeK > 0f) Damage(v);
                Construction(v);
                if (e.Def.Key == "deep_mine") DeepMine(v);
                if (World.Decaying(e) && Time.time >= v.DustNext)
                {
                    // The match's decay stage: grit crumbles off the roof every few seconds.
                    Fx.DecayDust(rig.Root.position, Mathf.Max(e.Def.SizeX, e.Def.SizeY));
                    v.DustNext = Time.time + Random.Range(2.5f, 5f);
                }
                if (rig.Turret != null) Aim(v);
                if (v.DoorTimer > 0 && (v.DoorTimer -= Time.deltaTime) <= 0) rig.Motion.SetDoorOpen(false);
                if (v.Dock != null)
                {
                    var phase = v.DockFrame >= Time.frameCount - 1 ? v.DockBest : DockView.Phase.Idle;
                    v.Dock.Tick(phase, Time.deltaTime);
                    // The door is up from Align until the truck has pulled out, and rolls down 0.5 s after.
                    if (phase != DockView.Phase.Idle && v.Dock.DoorSide) { rig.Motion.SetDoorOpen(true); v.DoorTimer = Mathf.Max(v.DoorTimer, 0.5f); }
                }
            }
            else if (e.IsStructure)
            {
                // Construction: the building rises out of its foundation.
                float p = e.BuildProgress;
                rig.Body.localScale = new Vector3(1, Mathf.Lerp(0.08f, 1f, p), 1);
                if (rig.Turret != null && (e.Def.Key == "optics_lab" || e.Def.Key == "fusion_reactor"))
                {
                    rig.Turret.localRotation = Quaternion.Euler(0, Time.time * (e.Working ? 90f : 8f) + e.Id * 47f, 0);
                    float pulse = e.Working ? 1f + Mathf.Sin(Time.time * 4f + e.Id) * 0.08f : 0.8f;
                    rig.Turret.localScale = Vector3.one * pulse;
                }
                else if (rig.Turret != null && e.Def.Key == "radar_dome")
                    rig.Turret.localRotation = Quaternion.Euler(0, e.IsComplete ? Time.time * 50f + e.Id * 47f : 0, 0);
                else if (rig.Turret != null && e.Def.ModelKey == "deep_mine") // a deep mine, or a derrick drawn as one
                    rig.Turret.localRotation = Quaternion.Euler(e.Working ? Time.time * 140f + e.Id * 47f : 0, 0, 0); // the sheave turns while it pumps
                else if (rig.Turret != null && e.Def.Key == "construction_yard")
                {
                    bool building = World.Teams[e.Team].StructureQueue.Count > 0;
                    rig.Turret.localRotation = Quaternion.Euler(0, building ? Time.time * 40f + e.Id * 47f : 200f, 0);
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
                    float k = Mathf.SmoothStep(0f, 1f, v.SpawnT); // from rest: it eases out of the bay, no pop
                    rig.Root.position = Vector3.Lerp(v.SpawnFrom.Value, rig.Root.position, k);
                    if (v.SpawnT >= 1f) v.SpawnFrom = null;
                }
                // Ground units ride up onto a structure's plinth and down its driveway ramp (leaving a producer, docking).
                if (!e.IsAir) { var rp = rig.Root.position; float py = Plinths.HeightAt(rp); if (py > 0f) { rp.y += py; rig.Root.position = rp; } }
                var targetRot = Quaternion.Euler(0, Yaw(e.Facing), 0);
                rig.Root.rotation = Quaternion.Slerp(rig.Root.rotation, targetRot, Time.deltaTime * 14f);
                if (!e.IsAir && e.Def.Armor != Armor.Infantry) HullFeel(v);
                if (e.Def.Key == "artillery" && rig.HasModel) Artillery(v);
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
                        ReportDock(e);
                        Dump(v);
                        // Nothing left to mine (the sim idles it after warning the surface is exhausted): park, power down.
                        rig.Motion.SetParked(e.Order == Order.Idle && e.Cargo == 0 && !e.Moving);
                        // Unloading beside a drop-off whose lane is built over: the old one-shot tip, on the first ore out
                        // (the drop-off's door, lamps and chute answer through ReportDock).
                        if (e.Order == Order.ReturnOre && e.Dock < DockStep.Align && e.Cargo < v.PrevCargo && !v.Tipped)
                        {
                            rig.Motion.TipBin();
                            v.Tipped = true;
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

        /// <summary>
        /// A vehicle (or soldier) rolls out with anticipation (production review 4a; fix 9). From the queue the view knows
        /// the seconds left to `trained`: T = (BuildTime - Progress) / rate (half on low power). On the team's first producer:
        ///  - T &lt; 1.5 s: its amber lamps blink at 2 Hz;
        ///  - T &lt; 0.8 s: three steam puffs vent from the roof, 0.2 s apart;
        ///  - the door starts up so it's fully open 0.1 s before the unit appears (earlier on low power, when the door
        ///    itself runs at half speed), and closes 1.4 s after;
        ///  - the unit then eases out of a dark bay: it starts 0.6 inside the door and leaves from rest (smoothstep, 0.9 s).
        /// The bay is a dark panel that fills exactly the opening the rolling door uncovers (the pack's factory has a
        /// wall right behind its door). Cancelling a queue just stops the lamps; the door closes on its timer.
        /// </summary>
        void RollOut(EV v)
        {
            var e = v.E;
            var t = World.Teams[e.Team];
            var rig = v.Rig;
            if (v.Door == null)
            {
                v.Door = PezMotion.FindDeep(rig.Model.transform, "door");
                if (v.Door != null) { v.Bay = DarkBay(v.Door); v.DoorHeight = DoorH(v.Door); }
            }
            if (v.Bay != null)
            {
                // The opening the door uncovers as it rolls up (it scales down toward its top edge).
                float open = 1f - v.Door.localScale.y;
                bool show = open > 0.02f;
                if (v.Bay.gameObject.activeSelf != show) v.Bay.gameObject.SetActive(show);
                if (show) { var sc = v.Bay.localScale; sc.y = v.DoorHeight * open; v.Bay.localScale = sc; var lp = v.Bay.localPosition; lp.y = v.Door.localPosition.y - v.DoorHeight + sc.y * 0.5f; v.Bay.localPosition = lp; }
            }
            v.Blink = 1f;
            if (!t.UnitQueues.TryGetValue(e.Def.Produces, out var q) || q.Count == 0) { v.Vents = 0; return; }
            if (!IsFirstProducer(e)) return;
            var def = Defs.Get(q[0].Key);
            if (def == null) return;
            float rate = t.LowPower ? 0.5f : 1f;
            float T = (def.BuildTime - q[0].Progress) / rate;
            if (T < 1.5f) v.Blink = (Time.time * 4f + e.Id * 0.731f) % 2f < 1f ? 1.6f : 0.15f; // each factory blinks on its own phase
            if (T > 0.8f) v.Vents = 0;
            else if (v.Vents < 3 && T < 0.8f - v.Vents * 0.2f)
            {
                v.Vents++;
                var top = rig.Model.transform.TransformPoint(new Vector3(0f, RoofTop(v), 0.4f));
                Fx.Steam(top, 0.4f);
            }
            float lead = 0.1f + 0.35f / rate;
            if (T < lead && v.Door != null) { rig.Motion.SetDoorOpen(true); v.DoorTimer = Mathf.Max(v.DoorTimer, T + 1.4f); }
        }

        static float DoorH(Transform door)
        {
            var mf = door.GetComponentInChildren<MeshFilter>();
            return mf != null ? mf.sharedMesh.bounds.size.y : 0.8f;
        }

        float RoofTop(EV v)
        {
            if (v.Roof == null)
            {
                var b = new Bounds(v.Rig.Root.position, Vector3.zero);
                foreach (var r in v.Rig.Model.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                v.Roof = b;
            }
            return v.Roof.Value.max.y - v.Rig.Root.position.y;
        }

        static Material bayMat;

        /// <summary>A dark panel just in front of the door plane, sized each frame to the opening the door uncovers.</summary>
        static Transform DarkBay(Transform door)
        {
            bayMat ??= Look.Model(new Color(0.035f, 0.032f, 0.034f), 0.05f, 0f, 0f, 0f);
            var mf = door.GetComponentInChildren<MeshFilter>();
            var b = mf != null ? mf.sharedMesh.bounds : new Bounds(new Vector3(0, -0.4f, 0), new Vector3(1.4f, 0.8f, 0.06f));
            var t = Models.Part(door.parent, PrimitiveType.Cube, door.localPosition + new Vector3(0f, 0f, b.min.z - 0.012f), new Vector3(b.size.x * 0.97f, 0.01f, 0.01f), bayMat);
            t.name = "dark_bay";
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.gameObject.SetActive(false);
            return t;
        }

        bool IsFirstProducer(Entity e)
        {
            foreach (var o in World.Entities)
                if (!o.Dead && o.Team == e.Team && o.IsStructure && o.IsComplete && o.Def.Produces == e.Def.Produces) return o == e;
            return false;
        }

        /// <summary>
        /// The dump as a performance (economy review R3; fix 8), inside the sim's Unload, with the truck already backed in
        /// (bin end to the building, so no pivot is needed):
        ///  - the laden truck squats 0.03 and rises as the weight leaves (follows Cargo, eased);
        ///  - the bed tips during Unload's 0.3 s settle (SetBinTipped);
        ///  - every 10 ore that leaves pours one brick in the ore's colour (crystal and uranium glow) from the bin's rear
        ///    lip, arcing over it into the bay, with an ore-tinted dust puff at the chute mouth every 0.25 s;
        ///  - the bed slams down in PullOut's first 0.25 s with a 1.5 deg hull bounce, and one stubborn last brick pops out.
        /// </summary>
        void Dump(EV v)
        {
            var e = v.E;
            var rig = v.Rig;
            float load = e.Cargo / (float)e.Def.HarvestCapacity;
            v.Squat = Mathf.MoveTowards(v.Squat, load, Time.deltaTime * 2f);
            var bp = rig.Body.localPosition; bp.y = -0.03f * v.Squat; rig.Body.localPosition = bp;
            bool bay = e.Order == Order.ReturnOre && (e.Dock == DockStep.Unload || e.Dock < DockStep.Align);
            if (bay && e.Cargo < v.PrevCargo && e.CargoType >= 0 && rig.Bin != null)
            {
                var back = -rig.Root.forward;                          // the bin end, toward the building
                var lip = rig.Bin.position + Vector3.up * 0.12f + back * 0.06f;
                Color32 c = OreColors[e.CargoType];
                int n = Mathf.Max(1, (v.PrevCargo - e.Cargo) / 10);
                for (int i = 0; i < n; i++) Fx.OreBrick(lip, back, c, e.CargoType >= 2);
                if ((v.PourDust -= Time.deltaTime) <= 0f) { v.PourDust = 0.25f; Fx.PourDust(lip + back * 0.1f + Vector3.up * 0.15f, c); }
            }
            if (e.Dock == DockStep.PullOut && v.PrevDock == DockStep.Unload)
            {
                v.HullPitchVel -= 18f;                                // the bed slams down: a small hull bounce
                if (rig.Bin != null && v.OreTint >= 0)
                    Fx.OreBrick(rig.Bin.position + Vector3.up * 0.15f, -rig.Root.forward * 0.4f + Vector3.up * 1.2f, OreColors[v.OreTint], v.OreTint >= 2);
            }
            v.PrevDock = e.Dock;
        }

        /// <summary>
        /// A delivering truck tells its drop-off what it's doing, from the sim's bay steps (exact, no distance guessing):
        /// Align, Reverse and Unload make the bay Active (lamps chase, door up, chute out), PullOut makes it Clear. A truck
        /// unloading beside a drop-off whose lane is built over (it stays in Approach) makes it Active while it tips.
        /// </summary>
        void ReportDock(Entity e)
        {
            if (e.Order != Order.ReturnOre || e.DockAt == 0 || !Views.TryGetValue(e.DockAt, out var dv) || dv.Dock == null) return;
            var ph = e.Dock >= DockStep.Align && e.Dock <= DockStep.Unload ? DockView.Phase.Active
                   : e.Dock == DockStep.PullOut ? DockView.Phase.Clear
                   : !e.Moving && e.Cargo > 0 && Vec2.Dist(e.Pos, dv.Dock.BayPoint) <= 0.7f ? DockView.Phase.Active
                   : DockView.Phase.Idle;
            if (dv.DockFrame != Time.frameCount) { dv.DockFrame = Time.frameCount; dv.DockBest = ph; }
            else if (ph > dv.DockBest) dv.DockBest = ph;
        }

        /// <summary>A hull's spring per weight class (production review rec. 5): period, damping, nose-up and nose-down
        /// limits (deg), and the kick a shot gives it (deg).</summary>
        struct HullClass { public float Period, Damping, Up, Down, Kick; }
        static readonly HullClass LightHull = new HullClass { Period = 0.25f, Damping = 0.35f, Up = 2f, Down = 3f, Kick = 1.5f };   // bobs twice
        static readonly HullClass HeavyHull = new HullClass { Period = 0.6f, Damping = 0.55f, Up = 1.5f, Down = 2.5f, Kick = 2.5f };  // one slow rock
        static readonly HullClass ArtyHull = new HullClass { Period = 0.5f, Damping = 0.7f, Up = 2f, Down = 3f, Kick = 3f };
        static readonly HullClass OtherHull = new HullClass { Period = 0.3f, Damping = 0.5f, Up = 2f, Down = 3f, Kick = 1.5f };

        static HullClass ClassOf(Entity e) => e.Def.Key switch
        {
            "light_tank" or "laser_tank" or "scout_buggy" => LightHull,
            "heavy_tank" or "mammoth_tank" => HeavyHull,
            "artillery" => ArtyHull,
            _ => OtherHull,
        };

        /// <summary>
        /// Mass in motion: acceleration pitches the hull nose-up and braking dips it, on an underdamped spring per weight
        /// class (light bobs twice, heavy rocks once slowly, artillery settles). The braking dip starts about 0.3 s before
        /// the sim stops the vehicle (it's about to reach its firing range or its move point), so stopping and the first
        /// shot's kick no longer cancel. The heavy squats a little as it pulls away.
        /// </summary>
        void HullFeel(EV v)
        {
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            var e = v.E;
            var hc = ClassOf(e);
            float speed = Vec2.Dist(e.PrevPos, e.Pos) / World.Dt;
            float smoothed = Mathf.Lerp(v.HullSpeed, speed, 1f - Mathf.Exp(-dt * 6f));
            float accel = (smoothed - v.HullSpeed) / dt;
            v.HullSpeed = smoothed;
            float target = Mathf.Clamp(-accel * 1.5f, -hc.Up, hc.Down); // nose-up is negative pitch
            if (e.Moving && AboutToStop(e)) target = hc.Down;
            float w = 6.2831853f / hc.Period;
            v.HullPitchVel += (w * w * (target - v.HullPitch) - 2f * hc.Damping * w * v.HullPitchVel) * dt;
            v.HullPitch = Mathf.Clamp(v.HullPitch + v.HullPitchVel * dt, -hc.Up * 1.6f, hc.Down * 1.6f);
            v.Rig.Body.localRotation = Quaternion.Euler(v.HullPitch, 0, 0);
            if (e.Def.Key == "heavy_tank") v.StartSquat = Mathf.MoveTowards(v.StartSquat, accel > 0.4f ? 1f : 0f, dt / 0.25f);
            if (e.Def.Key == "heavy_tank" || e.Def.Key == "artillery")
            {
                var bp = v.Rig.Body.localPosition; bp.y = -0.015f * v.StartSquat - 0.04f * v.Deploy; v.Rig.Body.localPosition = bp;
            }
        }

        /// <summary>Will the sim stop this vehicle within about 0.3 s? (It stops dead at Range x 0.95 from an attack target,
        /// or on reaching its move point.)</summary>
        bool AboutToStop(Entity e)
        {
            float reach = e.Def.Speed * 0.3f;
            if ((e.Order == Order.Attack || e.Order == Order.AttackMove) && e.TargetId != 0 && e.Def.Weapon != null)
            {
                var t = World.Get(e.TargetId);
                if (t != null) { float rem = Vec2.Dist(e.Pos, t.Center) - e.Def.Weapon.Range * 0.95f; return rem > 0f && rem < reach; }
            }
            if (e.Order == Order.Move) { float rem = Vec2.Dist(e.Pos, e.OrderPos); return rem < reach; }
            return false;
        }

        /// <summary>
        /// Artillery's cycle inside its 3.5 s cooldown: it deploys when it stops to fight (body squats 0.04 over 0.4 s,
        /// spade dust at the rear corners), loads (the barrel rams back and returns, 0.4 s, ending well before the earliest
        /// shot) and undeploys in 0.25 s when the sim moves it.
        /// </summary>
        void Artillery(EV v)
        {
            var e = v.E;
            bool engaged = Time.time - v.LastFire < 3f || e.Order == Order.Attack;
            bool deploy = !e.Moving && engaged;
            float was = v.Deploy;
            v.Deploy = Mathf.MoveTowards(v.Deploy, deploy ? 1f : 0f, Time.deltaTime / (deploy ? 0.4f : 0.25f));
            if (was < 0.9f && v.Deploy >= 0.9f)
            {
                var r = v.Rig.Root;
                Fx.SpadeDust(r.position - r.forward * 0.42f + r.right * 0.26f, -r.forward);
                Fx.SpadeDust(r.position - r.forward * 0.42f - r.right * 0.26f, -r.forward);
            }
            if (engaged && e.Cooldown > 0.2f && e.Cooldown < 0.6f && !v.Rammed) { v.Rig.Motion.Ram(); v.Rammed = true; }
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
                    bool shell = p.Weapon.Name == "artillery";
                    t = Models.Part(transform, PrimitiveType.Sphere, from, Vector3.one * (shell ? 0.16f : p.Weapon.Name == "heavy_cannon" ? 0.14f : 0.1f), Mats.Glow(c, shell ? 2.2f : 4f));
                    t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var trail = t.gameObject.AddComponent<TrailRenderer>();
                    trail.sharedMaterial = Mats.Unlit(new Color(c.r, c.g, c.b, 0.5f), true);
                    trail.time = p.Weapon.Name == "rocket" ? 0.5f : shell ? 0.35f : 0.12f;
                    if (shell)
                    {
                        // Its shadow crosses the ground under it, so viewers can read where it will land.
                        var sh = Models.Part(transform, PrimitiveType.Cylinder, new Vector3(from.x, 0.03f, from.z), new Vector3(0.22f, 0.002f, 0.22f), Mats.Unlit(new Color(0.09f, 0.07f, 0.06f, 0.35f)));
                        sh.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        shellShadows[p.Id] = sh;
                    }
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
                if (shellShadows.TryGetValue(p.Id, out var shadow))
                {
                    shadow.position = new Vector3(pos.x, 0.03f, pos.z);
                    if (shadow.gameObject.activeSelf != t.gameObject.activeSelf) shadow.gameObject.SetActive(t.gameObject.activeSelf);
                }
                bool show = PovTeam < 0 || VisibleTile(p.Pos);
                if (t.gameObject.activeSelf != show) t.gameObject.SetActive(show);
            }
            var dead = new List<int>();
            foreach (var kv in projectiles) if (!live.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var id in dead)
            {
                Destroy(projectiles[id].gameObject); projectiles.Remove(id); projectileStart.Remove(id);
                if (shellShadows.TryGetValue(id, out var sh)) { Destroy(sh.gameObject); shellShadows.Remove(id); }
            }
        }

        bool VisibleTile(Vec2 p)
        {
            var t = Int2.Of(p);
            return World.Map.InBounds(t.X, t.Y) && World.Teams[PovTeam].Visible[World.Map.Idx(t.X, t.Y)];
        }

        /// <summary>The cameras that may see an event's effects: the main view, and each stream whose team sees the spot.</summary>
        List<int> AudienceAt(Vec2 p, int eventTeam)
        {
            audience.Clear();
            audience.Add(TerrainView.MainFogLayer);
            var tile = Int2.Of(p);
            bool inMap = World.Map.InBounds(tile.X, tile.Y);
            int idx = inMap ? World.Map.Idx(tile.X, tile.Y) : 0;
            for (int t = 0; t < Mathf.Min(8, World.Teams.Count); t++)
                if (t == eventTeam || inMap && World.Teams[t].Visible[idx]) audience.Add(TerrainView.TeamFogLayerBase + t);
            return audience;
        }

        void PlayEvents()
        {
            try { PlayEventsInner(); }
            finally { Fx.Audience = null; }
        }

        void PlayEventsInner()
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
                // Its effects (blasts, smoke, sparks, scorch) only reach the player streams whose team can see the spot.
                Fx.Audience = AudienceAt(ev.Pos, ev.Team);
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
                        {
                            var tip = MuzzleOf(ev.A, ev.Pos);
                            bool arty = ev.Key == "artillery";
                            Fx.MuzzleFlash(tip, arty ? 0.3f : ev.Key == "heavy_cannon" ? 0.2f : 0.13f);
                            if (Views.TryGetValue(ev.A, out var sv) && !sv.E.IsStructure)
                            {
                                // The ground notices: a dust ring under a heavy's shot, a blast ring and lingering smoke for artillery.
                                if (ev.Key == "heavy_cannon") Fx.GroundRing(sv.Rig.Root.position, 1.4f, 0.5f, 0);
                                if (arty) { Fx.GroundRing(sv.Rig.Root.position, 2f, 0.6f, 6); Fx.BarrelSmoke(tip); }
                            }
                            Kick(ev.A, 0.08f);
                            break;
                        }
                    case "trained":
                        if (Views.TryGetValue(ev.A, out var tv2))
                        {
                            var def2 = Defs.Get(ev.Key);
                            var producer = OpenDoorNear(ev.Team, ev.Pos, 3f, 1.4f);
                            if (producer != null)
                            {
                                if (def2.IsAir && producer.E.Def.Key == "airfield") { producer.Rig.Motion.SnapLiftDown(); producer.Rig.Motion.SetLiftUp(true); }
                                // Start at the door (south face) and roll out to where the sim placed it.
                                var c = producer.E.Center;
                                // From 0.6 inside the door (hidden in the dark bay) when the model has one.
                                var door = producer.Rig.HasModel ? PezMotion.FindDeep(producer.Rig.Model.transform, "door") : null;
                                tv2.SpawnFrom = def2.IsAir ? W(c, 0.2f)
                                              : door != null ? new Vector3(door.position.x, 0f, door.position.z + 0.6f)
                                              : W(new Vec2(c.X, producer.E.Origin.Y + 0.3f));
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
                return v.Rig.Twin ? v.Rig.Barrel.TransformPoint((v.Rig.Motion.NextTwin & 1) == 0 ? v.Rig.MuzzleL : v.Rig.MuzzleR)
                     : v.Rig.HasMuzzle ? v.Rig.Barrel.TransformPoint(v.Rig.MuzzleLocal) : v.Rig.Barrel.position + v.Rig.Barrel.parent.forward * 0.35f;
            return W(fallback, 0.4f);
        }

        float HeightOf(int id, float fallback) => Views.TryGetValue(id, out var v) && v.Rig.Altitude > 0 ? v.Rig.Root.position.y : fallback;

        void Kick(int id, float amount)
        {
            if (!Views.TryGetValue(id, out var v)) return;
            v.LastFire = Time.time;
            if (v.Rig.HasModel) v.Rig.Motion.Fire(); else v.Recoil = amount;
            if (!v.E.IsStructure && !v.E.IsAir && v.E.Def.Armor != Armor.Infantry)
            {
                // The hull kicks back (nose up): an impulse on the spring, so it rides on top of any settling dip.
                var hc = ClassOf(v.E);
                v.HullPitchVel -= hc.Kick * 6.2831853f / hc.Period;
                v.Rammed = false;
            }
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
