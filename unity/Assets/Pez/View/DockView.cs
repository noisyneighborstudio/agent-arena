using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// A drop-off gets ready for the truck (economy review R2; fix 6). Driven by the sim's bay steps (Entity.Dock of the
    /// truck whose DockAt is this building), so it is exact and never waits on the view:
    ///  - Align (the truck turns on the spot at the lane head, about 1 s before it reverses): the dock lamps start a
    ///    2 Hz amber chase, the door rolls up and the chute slides out 0.5 tiles toward the truck (0.5 s, ease-out with
    ///    a small overshoot). The chase runs through Reverse and Unload.
    ///  - PullOut: the lamps turn steady cream ("clear") and the chute retracts; the lamps go dark 0.6 s after the bay
    ///    is free, and the door closes 0.5 s after (WorldView's door timer).
    /// The lamps are the model's two amber dock lamps (stage_1__M_E_Amber) plus small pips built here: a column under
    /// each lamp on the refinery and the command center (they chase upward, "come in"), a row between them on the
    /// outpost's low drop pad. The chute is built here too (the pack has no chute node): a kraft trough on the refinery
    /// and the command center, which have a door; the outpost's pad takes ore without one. Off, the lamps are unlit.
    /// A bay that terrain moved to another wall (not the door's) sends the chute through that wall. A model with a hatch
    /// there (hatch_N nodes: the refinery's roll-up shutters) has the chute rest just inside the wall, and it only comes
    /// out once the hatch's owner reports it open (Opening); ChuteBusy tells that owner to hold it open until the chute is back.
    /// The signals follow the bay too: the south lamps and pips are lit only while the bay is the door's (south), and a model
    /// with a lamp lens on each side hatch (dock_lens_N, beside hatch_N: the refinery) gets a short row of pips either side of
    /// it that runs the same Active chase and Clear / off sequence for the wall the truck actually uses. A side lamp that has no
    /// signal of its own (the command center, the outpost) just leaves the south dark. When the bay changes side the old
    /// signal fades out and the new one in over 0.2 s, no snap.
    /// </summary>
    public class DockView
    {
        public enum Phase { Idle, Clear, Active }

        /// <summary>One dock signal: a lamp renderer (the south pair, or a hatch's lens) and the pips that chase up to it.
        /// W is how much the bay is its wall's, 0 to 1 (fades over 0.2 s).</summary>
        class Signal
        {
            public Renderer Lens;
            public Renderer[] Pips;
            public Vector3 Dir;
            public float W, WShown = -1f;
            public Phase Shown = (Phase)(-1);
            public bool Cream;
        }
        readonly System.Collections.Generic.List<Signal> signals = new System.Collections.Generic.List<Signal>();
        readonly Transform chute;
        readonly Transform model, door;
        readonly float halfX, halfY;
        Vector3 chuteIn, chuteOut, wantDir;
        float swapK = 1f;                      // 0..1: how much of the chute shows while it changes sides
        readonly Material amber, cream, lampAmber, lampCream;
        float chuteK, clearT = -1f;
        bool hatched, gated;                   // a hatch in the bay's wall; the wall's panel (hatch or south shutter) gates the chute

        static Material pipAmber, pipCream, kraft, licorice;

        /// <summary>True when the bay is on the door side (south): the door rolls up for the truck.</summary>
        public bool DoorSide { get; private set; }
        /// <summary>The bay's direction out of the building, in the model's space.</summary>
        public Vector3 Dir { get; private set; }
        /// <summary>The chute is changing sides (the sim moved the bay): it draws back, the old wall's hatch shuts, then it
        /// comes up on the new side.</summary>
        public bool Switching => chute != null && wantDir != Dir;
        /// <summary>How open the wall's hatch is toward Dir, 0 to 1 (the hatch's owner sets it). The chute waits for it to be open.</summary>
        public System.Func<Vector3, float> Opening;
        /// <summary>The chute is out, on its way, or wanted out: a hatch in its wall has to stay open.</summary>
        public bool ChuteBusy { get; private set; }
        /// <summary>The bay point (sim coordinates), for trucks unloading beside a drop-off whose lane is built over.</summary>
        public Vec2 BayPoint;

        /// <param name="halfX">Half the footprint across X.</param>
        /// <param name="halfY">Half the footprint across Y (the sim's Y is world Z).</param>
        /// <param name="facing">The sim's bay facing (World.Bay: south unless terrain blocks that lane).</param>
        public static DockView For(Rig rig, float halfX, float halfY, float facing, Vec2 bayPoint)
        {
            var m = rig.Model;
            var stage = PezMotion.FindDeep(m.transform, "stage_1");
            var lampT = stage != null ? PezMotion.FindDeep(stage, "stage_1__M_E_Amber") : null;
            if (lampT == null) return null;
            var dir = new Vector3(Mathf.Cos(facing), 0f, Mathf.Sin(facing));
            return new DockView(m.transform, stage, lampT.GetComponent<Renderer>(), PezMotion.FindDeep(m.transform, "door"), halfX, halfY, dir) { BayPoint = bayPoint };
        }

        DockView(Transform model, Transform stage, Renderer lampR, Transform door, float halfX, float halfY, Vector3 dir)
        {
            this.model = model; this.door = door; this.halfX = halfX; this.halfY = halfY;
            Dir = wantDir = dir;
            amber = lampR.sharedMaterial;
            if (pipAmber == null)
            {
                pipAmber = Look.Model(Mats.Amber, 0.6f, 0f, 0f);
                pipAmber.SetColor("_EmissionColor", (Color)PezPalette.EmissiveAmberIndustryDocking * 3.2f);
                pipCream = Look.Model(Mats.Cream, 0.6f, 0f, 0f);
                pipCream.SetColor("_EmissionColor", (Color)PezPalette.MaterialsCreamPlastic * 1.8f);
                kraft = Look.Model(PezPalette.MaterialsKraft, 0.18f, 0f, 0.25f, 0.3f);
                licorice = Look.Model(PezPalette.MaterialsLicorice, 0.46f, 0f, 0.5f, 0.2f);
            }
            cream = new Material(amber) { name = amber.name + "_cream" };
            cream.color = Mats.Cream;
            cream.SetColor("_EmissionColor", (Color)PezPalette.MaterialsCreamPlastic * 1.8f);
            lampAmber = amber; lampCream = cream;

            // The two lamps, from the lamp mesh's bounds (in the lamp node's space, under stage_1).
            var b = lampR.GetComponent<MeshFilter>().sharedMesh.bounds;
            var lt = lampR.transform;
            float s = Mathf.Min(b.size.y, b.size.z);
            var left = new Vector3(b.min.x + s * 0.5f, b.center.y, b.center.z);
            var right = new Vector3(b.max.x - s * 0.5f, b.center.y, b.center.z);
            float pip = s * 1.05f;
            Renderer[] pips;
            if (b.center.y > 0.3f)
            {
                // A column of three under each lamp, chasing upward to it.
                pips = new Renderer[6];
                for (int i = 0; i < 3; i++)
                {
                    float y = Mathf.Lerp(0.19f, b.center.y - s * 1.35f, i / 2f);
                    pips[i] = Pip(lt, new Vector3(left.x, y, left.z), pip);
                    pips[3 + i] = Pip(lt, new Vector3(right.x, y, right.z), pip);
                }
            }
            else
            {
                // A low pad: a row between the lamps, chasing in from both sides.
                pips = new Renderer[4];
                for (int i = 0; i < 4; i++) pips[i] = Pip(lt, Vector3.Lerp(left, right, (i + 1) / 5f), pip);
            }
            signals.Add(new Signal { Lens = lampR, Pips = pips, Dir = Vector3.back });

            // A lamp on each side hatch: the model's lens (dock_lens_N) and a row of two pips either side of it, along the wall.
            for (int i = 0; ; i++)
            {
                var h = PezMotion.FindDeep(model, "hatch_" + i);
                if (h == null) break;
                var ln = PezMotion.FindDeep(model, "dock_lens_" + i);
                var lr = ln != null ? ln.GetComponentInChildren<Renderer>() : null;
                var hp = model.InverseTransformPoint(h.position); hp.y = 0f;
                if (lr == null || hp.sqrMagnitude < 0.01f) continue;
                var wall = Mathf.Abs(hp.x) > Mathf.Abs(hp.z) ? new Vector3(Mathf.Sign(hp.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(hp.z));
                var along = new Vector3(Mathf.Abs(wall.z), 0f, Mathf.Abs(wall.x));
                var c = model.InverseTransformPoint(ln.position);
                var row = new Renderer[4];
                float[] at = { -0.22f, -0.11f, 0.11f, 0.22f };           // outer, inner | inner, outer: they chase in to the lens
                for (int k = 0; k < 4; k++) row[k] = Pip(stage, stage.InverseTransformPoint(model.TransformPoint(c + along * at[k])), 0.055f);
                signals.Add(new Signal { Lens = lr, Pips = row, Dir = wall });
            }
            foreach (var sg in signals) sg.W = Vector3.Dot(sg.Dir, wantDir) > 0.9f ? 1f : 0f;

            // The chute: a kraft trough sloping down into the building. Out, its lip is just over the backed-in truck's
            // tail (the bay point is half a tile outside the footprint; the bin's rear edge 0.4 in from the truck's
            // centre); in, it's 0.5 back: parked in the refinery's bay, or behind the command center's door. Doors face
            // -Z; when terrain moved the bay to another side, the chute comes out of that wall instead.
            if (door != null)
            {
                var c = new GameObject("chute").transform;
                c.SetParent(stage, false);
                chute = c;
                const float len = 0.62f, w = 0.42f, slope = 12f;
                var tray = new GameObject("tray").transform;
                tray.SetParent(c, false);
                tray.localPosition = new Vector3(0f, 0.24f, len * 0.5f);
                tray.localRotation = Quaternion.Euler(slope, 0f, 0f);    // the outer end higher, at the truck's bed
                Part(tray, new Vector3(0f, 0f, 0f), new Vector3(w, 0.025f, len), kraft);
                Part(tray, new Vector3(-w * 0.5f, 0.035f, 0f), new Vector3(0.03f, 0.07f, len), licorice);
                Part(tray, new Vector3(w * 0.5f, 0.035f, 0f), new Vector3(0.03f, 0.07f, len), licorice);
            }
            SetSide(dir);
            Apply(Phase.Idle, 0f, 0f);
        }

        /// <summary>
        /// Puts the chute on the bay's side. Out, its lip is just over the backed-in truck's tail (the bay point is half a
        /// tile outside the footprint; the bin's rear edge 0.4 in from the truck's centre); in, it's 0.5 back: parked in the
        /// refinery's bay, or behind the command center's door. Doors face -Z; when terrain or a building moved the bay to
        /// another side, the chute comes out of that wall instead, through its hatch when the model has one: then it rests
        /// just inside the wall rather than 0.5 back (which can poke out of a wall).
        /// </summary>
        void SetSide(Vector3 dir)
        {
            Dir = wantDir = dir;
            DoorSide = dir.z < -0.7f;
            hatched = gated = false;
            if (chute == null) return;
            float reach = (Mathf.Abs(dir.x) > 0.7f ? halfX : halfY) + 0.15f;
            chuteOut = dir * reach + (DoorSide ? new Vector3(model.InverseTransformPoint(door.position).x, 0f, 0f) : Vector3.zero);
            chuteIn = chuteOut - dir * 0.5f;
            gated = DoorSide && PezMotion.FindDeep(model, "bay_shutter") != null;   // the refinery's south shutter, down while the bay is elsewhere
            for (int i = 0; ; i++)
            {
                var h = PezMotion.FindDeep(model, "hatch_" + i);
                if (h == null) break;
                var hp = model.InverseTransformPoint(h.position); hp.y = 0f;
                if (hp.sqrMagnitude < 0.01f || Vector3.Dot(hp.normalized, dir) < 0.9f) continue;
                chuteIn = dir * (Vector3.Dot(hp, dir) - 0.12f);
                hatched = gated = true;
                break;
            }
            chuteK = 0f;
            chute.localPosition = chuteIn;
            chute.localRotation = Quaternion.LookRotation(-dir, Vector3.up); // the trough runs along local +Z, into the building
        }

        /// <summary>The sim's bay now (World.Bay): the point trucks use at once; the chute and hatch follow in Tick when its
        /// side has changed (a building that blocked the south lane was destroyed, or one now stands on it).</summary>
        public void Rebay(float facing, Vec2 bayPoint)
        {
            BayPoint = bayPoint;
            var d = new Vector3(Mathf.Cos(facing), 0f, Mathf.Sin(facing));
            if (Vector3.Dot(d, wantDir) < 0.9f) wantDir = d;
        }

        static Renderer Pip(Transform parent, Vector3 pos, float size)
        {
            var t = Models.Part(parent, PrimitiveType.Cube, pos, Vector3.one * size, pipAmber);
            var r = t.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        static void Part(Transform parent, Vector3 pos, Vector3 scale, Material m)
        {
            var t = Models.Part(parent, PrimitiveType.Cube, pos, scale, m);
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        /// <summary>Per frame, with what the bay's truck is doing: Active (Align, Reverse, Unload), Clear (PullOut) or Idle.</summary>
        public void Tick(Phase phase, float dt)
        {
            if (phase == Phase.Clear) clearT = 0.6f;
            else if (phase == Phase.Idle && clearT > 0f) { clearT -= dt; phase = Phase.Clear; }
            // Chute: out while the truck lines up, backs in and unloads; in once it pulls out.
            if (chute != null)
            {
                // Changing sides: the chute draws back first, the old wall's hatch shuts (its owner, once ChuteBusy is false),
                // then the chute shrinks away, appears on the new side and the new hatch opens for the next truck.
                bool switching = wantDir != Dir;
                bool want = phase == Phase.Active && !switching;
                ChuteBusy = want || chuteK > 0f;
                if (switching && chuteK <= 0f && (!gated || Opening == null || Opening(Dir) <= 0.02f))
                {
                    swapK = Mathf.MoveTowards(swapK, 0f, dt / 0.2f);
                    if (swapK <= 0f) SetSide(wantDir);
                }
                else if (!switching) swapK = Mathf.MoveTowards(swapK, 1f, dt / 0.2f);
                chute.localScale = Vector3.one * Mathf.Max(0.001f, swapK * swapK * (3f - 2f * swapK));
                float goal = want && swapK >= 1f && (!gated || Opening == null || Opening(Dir) >= 0.97f) ? 1f : 0f;
                if (chuteK != goal)
                {
                    chuteK = Mathf.MoveTowards(chuteK, goal, dt / 0.5f);
                    float k = goal > 0f ? EaseOutBack(chuteK) : chuteK * chuteK * (3f - 2f * chuteK);
                    chute.localPosition = Vector3.LerpUnclamped(chuteIn, chuteOut, k);
                }
            }
            Apply(phase, Time.time, dt);
        }

        static float EaseOutBack(float k)
        {
            // Ease-out with about 0.03 tiles of overshoot on a 0.5 slide.
            const float c = 0.9f;
            float x = k - 1f;
            return 1f + (c + 1f) * x * x * x + c * x * x;
        }

        void Apply(Phase phase, float now, float dt)
        {
            foreach (var sg in signals)
            {
                // The signal of the wall the bay is on is lit; the others fade to dark.
                bool mine = Vector3.Dot(sg.Dir, wantDir) > 0.9f;
                sg.W = Mathf.MoveTowards(sg.W, mine ? 1f : 0f, dt / 0.2f);
                Show(sg, sg.W > 0.001f ? phase : Phase.Idle, now);
            }
        }

        void Show(Signal sg, Phase phase, float now)
        {
            var pips = sg.Pips;
            float w = sg.W;
            bool creamNow = phase == Phase.Clear;
            if (creamNow != sg.Cream || sg.Shown == (Phase)(-1))
            {
                var pm = creamNow ? pipCream : pipAmber;
                for (int i = 0; i < pips.Length; i++) pips[i].sharedMaterial = pm;
                sg.Lens.sharedMaterial = creamNow ? lampCream : lampAmber;
                sg.Cream = creamNow;
            }
            float lensDim, lensGlow;
            if (phase == Phase.Active)
            {
                // 2 Hz chase: a bright step runs along each column (or in from both ends of the row) to the lamps.
                int n = pips.Length == 6 ? 4 : 3;         // steps per sweep (3 pips + the lamp, or 2 pips + the lamp)
                float ph = now * 2f;
                int lit = (int)((ph - Mathf.Floor(ph)) * n);
                float d = Mathf.Lerp(0.55f, 1f, w);
                for (int i = 0; i < pips.Length; i++)
                {
                    int step = pips.Length == 6 ? i % 3 : (i < 2 ? i : 3 - i);
                    PezShade.Set(pips[i], d, (step == lit ? 1f : step == lit - 1 ? 0.35f : 0.08f) * w);
                }
                lensDim = d; lensGlow = Mathf.Lerp(0.06f, lit == n - 1 ? 1.2f : 0.35f, w);
            }
            else
            {
                bool clear = phase == Phase.Clear;      // steady cream, or unlit
                float d = clear ? Mathf.Lerp(0.55f, 1f, w) : 0.55f;
                if (phase != sg.Shown || w != sg.WShown)
                    for (int i = 0; i < pips.Length; i++) PezShade.Set(pips[i], d, clear ? w : 0f);
                lensDim = d; lensGlow = clear ? Mathf.Lerp(0.06f, 1f, w) : 0.06f;
            }
            // The lens is set every frame: it is one of the model's emissives, which the building's power glow also writes to
            // (the pips are built here and are only ours).
            PezShade.Set(sg.Lens, lensDim, lensGlow);
            sg.Shown = phase; sg.WShown = w;
        }
    }
}
