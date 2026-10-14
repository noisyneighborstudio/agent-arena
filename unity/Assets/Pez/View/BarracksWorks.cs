using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The barracks as a soldier dispenser (MOTION.md barracks). All of it from the sim's training queue, so it never
    /// leads or lags `trained`:
    ///  - the clear case under the head holds four soldiers, stacked like candy: raw dough at the bottom, primed in the
    ///    middle, finished (uniform, boots and team helmet) at the top. Each is a figurine of the soldier it becomes, its
    ///    own model at a case figure's size: the top one is the soldier in training, the ones below the next in the queue
    ///    (the lower ones, past the end of the queue, the last soldier queued; the rifleman before anything is queued).
    ///    A long weapon that would reach the glass turns the figure aside, so it stands across the case. While a soldier
    ///    trains the stack rises with its progress, each one baking as it climbs, a fresh lump of dough swells in at the
    ///    bottom, and the oven strip at the case's foot glows.
    ///  - 1.5 s before the soldier is out, the top one rises out of the case up into the flip-top head (head). The head
    ///    snaps open and spits it out of its mouth (pop), full size and tucked into a ball, hugging its weapon down along
    ///    its body (a long rifle no longer sticks out of the ball like a spoke); it somersaults through the air,
    ///    lands in front (land) with a puff of dust, rolls on to its feet (exit) and stands up as the real unit appears
    ///    there (WorldView spawns it at Exit). The head drops shut.
    ///  - the flag on the flag node is cloth: it streams downwind (FxSystems.Wind) and ripples, harder in a stronger wind.
    /// A model without those nodes gets the parts it has.
    /// </summary>
    public class BarracksWorks
    {
        const int Slots = 4;
        const float Fig = 1.5f;                  // a case figure's size (1 = 0.13 tall)
        const float RiseAt = 1.5f, OpenAt = 1.25f, SpitAt = 1.1f, LandAt = 0.55f, RollEnd = 0.2f;
        const float Tuck = 0.65f;                // a tucked soldier's height, of its standing height

        readonly Transform model, head, flag, stackRoot;
        readonly Renderer oven;
        readonly Quaternion headRest;
        readonly Vector3 lo, hi, pop, land, exit;   // model space
        readonly List<Soldier> stack = new List<Soldier>(); // bottom to top; [0] is the next lump, swelling in
        readonly Material dough, skin, kraft, helmet;
        readonly int team;
        Soldier prop;                            // the one out of the case, on its way out
        Vector3 propFrom;
        float headK, lastP = -1f, ovenK = -1f, ovenLit = -1f;
        bool hadPop, thudded;

        // The cloth flag.
        readonly Transform cloth;
        readonly Mesh clothMesh;
        Vector3[] clothV;
        const int CU = 10, CV = 4;
        const float FlagW = 0.62f, FlagH = 0.38f;
        float flagYaw = float.NaN, flagPhase;
        readonly float phase;

        static readonly Vector3 HelmetSize = new Vector3(0.054f, 0.03f, 0.054f) * Fig;
        const string FigureKey = "rifleman";     // the case figurines before anything's queued
        // The case's inside, either side of the stack's axis (build_models.S_barracks: .42 wide, .22 deep), less a margin.
        const float CaseHalfDepth = 0.095f, CaseHalfWidth = 0.19f;
        const float HugDeg = 60f;                // tucked, the arms hug the weapon down along the body

        class Soldier
        {
            public string Key;                   // the soldier it's a figurine of
            public Transform Root, Body, Head, Helmet;   // the primitive figure (no figurine model)
            public Renderer[] Parts;                     // the figurine: its renderers and their coats (dough, primed, finished)
            public Material[][] Coats;
            public int Coat = -1;
            public Transform Ball;               // once spat out: the tuck (squashes about the middle) ...
            public GameObject Unit;              // ... holding the real unit's model, full size
            public float Half;                   // that model's half height
            public Transform Arms;               // its arms (and weapon), hugged in while it's tucked
            public Quaternion ArmsRest;
        }

        BarracksWorks(Transform model, int team, int seed)
        {
            this.model = model;
            this.team = team;
            phase = seed * 0.53f;
            head = PezMotion.FindDeep(model, "head");
            if (head) headRest = head.localRotation;
            flag = PezMotion.FindDeep(model, "flag");
            var o = PezMotion.FindDeep(model, "oven");
            oven = o ? o.GetComponentInChildren<Renderer>() : null;
            var a = PezMotion.FindDeep(model, "stack_lo");
            var b = PezMotion.FindDeep(model, "stack_hi");
            var p = PezMotion.FindDeep(model, "pop");
            var l = PezMotion.FindDeep(model, "land");
            var x = PezMotion.FindDeep(model, "exit");

            dough = Mats.Lit(new Color(0.93f, 0.84f, 0.68f), 0.15f, 0f);
            skin = Mats.Lit(PezPalette.MaterialsCreamPlastic, 0.3f, 0f);
            kraft = Mats.Lit(PezPalette.MaterialsKraft, 0.2f, 0f);
            helmet = Mats.Lit(Mats.Team(team), 0.45f, 0f);

            if (a && b && p && l && x)
            {
                lo = model.InverseTransformPoint(a.position);
                hi = model.InverseTransformPoint(b.position);
                pop = model.InverseTransformPoint(p.position);
                land = model.InverseTransformPoint(l.position);
                exit = model.InverseTransformPoint(x.position);
                stackRoot = new GameObject("soldier_stack").transform;
                stackRoot.SetParent(model, false);
                for (int i = 0; i <= Slots; i++) stack.Add(NewSoldier(FigureKey));
            }

            if (flag)
            {
                cloth = new GameObject("flag_cloth").transform;
                cloth.SetParent(flag, false);
                clothMesh = new Mesh { name = "flag_cloth" };
                clothMesh.MarkDynamic();
                clothV = new Vector3[CU * CV * 2];
                var tris = new List<int>();
                for (int side = 0; side < 2; side++)
                    for (int u = 0; u < CU - 1; u++)
                        for (int v = 0; v < CV - 1; v++)
                        {
                            int off = side * CU * CV, i0 = off + u * CV + v, i1 = i0 + CV, i2 = i0 + 1, i3 = i1 + 1;
                            if (side == 0) tris.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
                            else tris.AddRange(new[] { i0, i1, i2, i1, i3, i2 });
                        }
                Cloth(0f, 0.5f);
                clothMesh.vertices = clothV;
                clothMesh.triangles = tris.ToArray();
                clothMesh.RecalculateNormals();
                cloth.gameObject.AddComponent<MeshFilter>().sharedMesh = clothMesh;
                var r = cloth.gameObject.AddComponent<MeshRenderer>();
                r.sharedMaterial = helmet;
            }
        }

        public static BarracksWorks For(GameObject model, int team, int seed) => new BarracksWorks(model.transform, team, seed);

        /// <summary>Where a trained soldier appears: where it stands up after its roll (world), if this model has one.</summary>
        public bool HasExit => stackRoot != null;
        public Vector3 Exit => model.TransformPoint(exit);
        /// <summary>Which way it faces when it stands up there (world): the real unit starts out facing the same way.</summary>
        public Quaternion ExitFacing
        {
            get { var d = exit - land; d.y = 0f; return model.rotation * (d.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(d.normalized, Vector3.up) : Quaternion.identity); }
        }
        /// <summary>Roll-out steam puffs from the head's mouth.</summary>
        public int Heads => head ? 1 : 0;
        public Vector3 Head(int i) => stackRoot != null ? model.TransformPoint(pop) + Vector3.up * 0.1f : head.position + Vector3.up * 0.2f;

        /// <summary>The model a soldier of this kind is drawn with (an invented unit looks like its base unit).</summary>
        static string FigureModel(string key) => Pez.Sim.Defs.Get(key)?.ModelKey ?? key;

        Soldier NewSoldier(string key)
        {
            var s = new Soldier { Key = key, Root = new GameObject("soldier").transform };
            s.Root.SetParent(stackRoot, false);
            s.Root.localScale = Vector3.zero;
            var mk = FigureModel(key);
            if (Models.ModelFor(mk) == null) mk = FigureKey;
            if (Models.ModelFor(mk) != null)
            {
                // The soldier's own rig, still, a case figure tall, facing out of the case (-z).
                var rig = Models.Build(mk, team);
                foreach (var c in rig.Root.GetComponentsInChildren<PezMotion>()) Object.Destroy(c);
                foreach (var c in rig.Root.GetComponentsInChildren<PezEmerge>()) Object.Destroy(c);
                SoldierWorks.Rest(rig.Root.gameObject);   // a beam weapon's glass at rest
                s.Parts = rig.Root.GetComponentsInChildren<Renderer>(true);
                float top = 0f, ahead = 0f;
                var at = rig.Root.position;
                foreach (var r in s.Parts) { top = Mathf.Max(top, r.bounds.max.y - at.y); ahead = Mathf.Max(ahead, r.bounds.max.z - at.z); }
                float scale = 0.13f * Fig / Mathf.Max(0.1f, top);
                // A weapon that would reach the glass (the sniper's rifle): the figure turns aside until it fits, across the
                // case, and only if it's still too long, shrinks.
                float reach = ahead * scale, turn = 0f;
                if (reach > CaseHalfDepth)
                {
                    turn = Mathf.Min(75f, Mathf.Acos(CaseHalfDepth / reach) * Mathf.Rad2Deg);
                    float across = reach * Mathf.Sin(turn * Mathf.Deg2Rad);
                    if (across > CaseHalfWidth) scale *= CaseHalfWidth / across;
                }
                var fig = rig.Root;
                fig.SetParent(s.Root, false);
                fig.localRotation = Quaternion.Euler(0f, 180f + turn, 0f);
                fig.localScale = Vector3.one * scale;
                // Raw dough all over; primed (kraft, the face and hands cream, the helmet bare); finished as built.
                s.Coats = new Material[s.Parts.Length * 3][];
                for (int i = 0; i < s.Parts.Length; i++)
                {
                    var own = s.Parts[i].sharedMaterials;
                    var raw = new Material[own.Length]; var primed = new Material[own.Length];
                    for (int m = 0; m < own.Length; m++)
                    {
                        string n = own[m] != null ? own[m].name : "";
                        raw[m] = dough;
                        primed[m] = n.StartsWith("M_CreamPlastic") || n.StartsWith("M_Team") ? skin : kraft;
                    }
                    s.Coats[i * 3] = raw; s.Coats[i * 3 + 1] = primed; s.Coats[i * 3 + 2] = own;
                }
                return s;
            }
            s.Body = Models.Part(s.Root, PrimitiveType.Capsule, new Vector3(0f, 0.042f, 0f) * Fig, new Vector3(0.056f, 0.042f, 0.056f) * Fig, dough);
            s.Head = Models.Part(s.Root, PrimitiveType.Sphere, new Vector3(0f, 0.1f, 0f) * Fig, Vector3.one * 0.043f * Fig, dough);
            s.Helmet = Models.Part(s.Root, PrimitiveType.Sphere, new Vector3(0f, 0.112f, 0f) * Fig, HelmetSize, helmet);
            return s;
        }

        /// <summary>How baked a soldier looks at slot s (0 bottom .. 3 top): raw dough, primed, finished.</summary>
        void Bake(Soldier s, float slot, float scale)
        {
            // Raw dough is a squat lump; it stands up as it bakes.
            float k = Mathf.Clamp01(slot / 2f);
            var squat = new Vector3(Mathf.Lerp(1.15f, 1f, k), Mathf.Lerp(0.75f, 1f, k), Mathf.Lerp(1.15f, 1f, k));
            if (s.Parts != null)
            {
                int coat = slot > 2.3f ? 2 : slot > 1.2f ? 1 : 0;
                if (coat != s.Coat)
                {
                    for (int i = 0; i < s.Parts.Length; i++) if (s.Parts[i] != null) s.Parts[i].sharedMaterials = s.Coats[i * 3 + coat];
                    s.Coat = coat;
                }
                s.Root.localScale = squat * scale;
                return;
            }
            bool primed = slot > 1.2f;
            var body = primed ? kraft : dough;
            var face = primed ? skin : dough;
            if (s.Body.GetComponent<Renderer>().sharedMaterial != body) s.Body.GetComponent<Renderer>().sharedMaterial = body;
            if (s.Head.GetComponent<Renderer>().sharedMaterial != face) s.Head.GetComponent<Renderer>().sharedMaterial = face;
            float h = Mathf.SmoothStep(0f, 1f, (slot - 2.2f) / 0.5f);          // the helmet goes on near the top
            s.Helmet.localScale = HelmetSize * h;
            s.Helmet.gameObject.SetActive(h > 0.01f);
            s.Root.localScale = squat * scale;
        }

        Vector3 SlotPos(float slot) => Vector3.Lerp(lo, hi, slot / (Slots - 1));

        /// <summary>Swap the figure for the unit it was baking into, full size and tucked, hidden in the head.</summary>
        void Spit(Soldier s, string unit)
        {
            var figure = s.Parts != null ? s.Root.GetChild(0).gameObject : null;
            if (figure != null) figure.SetActive(false);
            else { s.Body.gameObject.SetActive(false); s.Head.gameObject.SetActive(false); s.Helmet.gameObject.SetActive(false); }
            s.Root.localScale = Vector3.one;
            s.Ball = new GameObject("tuck").transform;
            s.Ball.SetParent(s.Root, false);
            var rig = unit != null ? Models.Build(FigureModel(unit), team) : null;
            if (rig != null)
            {
                // The unit's own rig, as WorldView would build and draw it, held still: a stand-in until the real one
                // appears in its place, the same size.
                rig.Body.localScale = Vector3.one * Models.DrawScale(Pez.Sim.Defs.Get(unit), rig.HasModel);
                s.Unit = rig.Root.gameObject;
                s.Unit.transform.SetParent(s.Ball, false);
                foreach (var c in s.Unit.GetComponentsInChildren<PezMotion>()) Object.Destroy(c);
                foreach (var c in s.Unit.GetComponentsInChildren<PezEmerge>()) Object.Destroy(c);
                SoldierWorks.Rest(s.Unit);   // a beam weapon at rest, as the real one's when it takes over
                s.Arms = rig.HasModel ? PezMotion.FindDeep(rig.Model.transform, "arms") : null;
                if (s.Arms) s.ArmsRest = s.Arms.localRotation;   // hugged in (Out) once its bounds are taken, standing
            }
            else
            {
                // No model for it: the finished figure at a soldier's size.
                s.Unit = new GameObject("figure");
                s.Unit.transform.SetParent(s.Ball, false);
                if (figure != null) { figure.transform.SetParent(s.Unit.transform, false); figure.SetActive(true); }
                else foreach (var part in new[] { s.Body, s.Head, s.Helmet }) { part.SetParent(s.Unit.transform, false); part.gameObject.SetActive(true); }
                s.Unit.transform.localScale = Vector3.one * (0.54f / (0.13f * Fig));
            }
            // Pivot about its middle, so it tumbles and squashes round its own centre.
            s.Root.localRotation = Quaternion.identity;
            var bounds = new Bounds(); bool any = false;
            foreach (var r in s.Unit.GetComponentsInChildren<Renderer>())
            {
                if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
            }
            float lossy = Mathf.Max(1e-4f, s.Root.lossyScale.y);
            float bottom = any ? s.Root.InverseTransformPoint(bounds.min).y : 0f;
            s.Half = any ? bounds.size.y / lossy * 0.5f : 0.27f;
            s.Unit.transform.localPosition += Vector3.up * (-bottom - s.Half);
        }

        /// <param name="training">This barracks is working on a soldier (each barracks works its own item of the team's queue).</param>
        /// <param name="p">That soldier's progress, 0..1.</param>
        /// <param name="T">Seconds until it's out (`trained`).</param>
        /// <param name="queue">The soldiers queued at this barracks, the one it is training first (null if it isn't training):
        /// the top one is spat out as that unit's model, and the case's figurines are the ones queued. Not the team's queue
        /// itself, whose head can be another barracks' soldier (WorldView.QueuedAt).</param>
        /// <remarks>Call with Fx.Audience set: the landing kicks up dust.</remarks>
        public void Tick(bool training, float p, float T, List<Pez.Sim.ProdItem> queue, float dt)
        {
            Flag(dt);
            if (stackRoot == null) { HeadOnly(training, dt); return; }
            string unit = training && queue != null && queue.Count > 0 ? queue[0].Key : null;

            // A soldier finished (progress reset or training stopped after the pop): the one out front has become the
            // real unit; the stack moves down the list and a new lump is made ready at the bottom.
            bool done = hadPop && (!training || p < lastP - 0.3f);
            if (done)
            {
                if (prop != null) { Object.Destroy(prop.Root.gameObject); prop = null; }
                stack.Insert(0, NewSoldier(stack.Count > 0 ? stack[0].Key : FigureKey));
                hadPop = false;
            }
            if (training) Queued(queue);
            lastP = training ? p : -1f;
            float o = training ? Mathf.Clamp01(p) : 0f;

            // The oven glows while a soldier bakes.
            if (oven)
            {
                ovenK = ovenK < 0f ? (training ? 1f : 0f) : Mathf.MoveTowards(ovenK, training ? 1f : 0f, dt * 2f);
                float g = ovenK * (0.85f + 0.15f * Mathf.Sin(Time.time * 3.1f + phase));
                if (Mathf.Abs(g - ovenLit) > 0.02f) { PezShade.Set(oven, Mathf.Lerp(0.35f, 1f, ovenK), g * 1.4f); ovenLit = g; }
            }

            // The top one leaves the case.
            if (training && !hadPop && T < RiseAt && stack.Count > 1)
            {
                prop = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                propFrom = prop.Root.localPosition;
                hadPop = true;
                thudded = false;
            }

            // The stack: [0] swells in below the case, the rest climb a slot as the soldier trains.
            // Until the top one has gone, it waits at the top and the one below stops short of it.
            int n = stack.Count;
            for (int i = 0; i < n; i++)
            {
                float slot = Mathf.Min(i - 1 + o, Slots - 1);
                if (!hadPop && i < n - 1) slot = Mathf.Min(slot, Slots - 1.7f);
                float scale = i == 0 ? Mathf.SmoothStep(0f, 1f, o / 0.35f) : 1f;
                var s = stack[i];
                s.Root.localPosition = SlotPos(Mathf.Max(0f, slot));
                s.Root.localRotation = Quaternion.identity;
                Bake(s, Mathf.Max(0f, slot), scale);
                if (s.Root.gameObject.activeSelf != scale > 0.01f) s.Root.gameObject.SetActive(scale > 0.01f);
            }

            if (prop != null) Out(prop, T, unit);

            // The head: snaps open just before the spit, drops shut with a bounce once the soldier's clear.
            bool open = training && T < OpenAt && T > SpitAt - 0.25f;
            headK = Mathf.MoveTowards(headK, open ? 1f : 0f, dt / (open ? 0.1f : 0.22f));
            float k = open ? Mathf.SmoothStep(0f, 1f, headK) : headK * headK;
            if (head) head.localRotation = headRest * Quaternion.Euler(40f * k, 0f, 0f);
        }

        /// <summary>The figurines follow the queue: the top one is the soldier in training (once that one has left the case,
        /// the next), each below it the one after, and those past the end of the queue the last one queued. One that's the
        /// wrong soldier (the queue changed) is remade as the right one where it stands.</summary>
        void Queued(List<Pez.Sim.ProdItem> queue)
        {
            if (queue == null || queue.Count == 0) return;
            int n = stack.Count;
            for (int j = 0; j < n; j++)
            {
                int q = Mathf.Min(j + (hadPop ? 1 : 0), queue.Count - 1);
                int i = n - 1 - j;
                var key = queue[q].Key;
                if (stack[i].Key == key) continue;
                var old = stack[i];
                stack[i] = NewSoldier(key);
                stack[i].Root.localPosition = old.Root.localPosition;
                Object.Destroy(old.Root.gameObject);
            }
        }

        /// <summary>The one out of the case: up into the head, then spat out, a somersault, a landing, a roll, up.</summary>
        void Out(Soldier s, float T, string unit)
        {
            if (T > SpitAt)
            {
                // Rising out of the top of the case into the head, out of sight once it's in.
                float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((RiseAt - T) / (RiseAt - OpenAt)));
                s.Root.localPosition = propFrom + Vector3.up * (0.3f * u);
                Bake(s, Slots - 1, 1f);
                bool show = u < 0.95f;
                if (s.Root.gameObject.activeSelf != show) s.Root.gameObject.SetActive(show);
                return;
            }
            if (s.Ball == null) { Spit(s, unit); s.Root.gameObject.SetActive(true); }

            var dir = exit - land; dir.y = 0f;
            var face = dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir.normalized, Vector3.up) : Quaternion.identity;
            float r = s.Half * Tuck;                 // a tucked soldier's radius: its centre's height on the ground
            float spin, squash = Tuck;
            Vector3 at;
            if (T > LandAt)
            {
                // Out of the mouth on an arc, somersaulting forward one and a half times. It's thrown out before it falls,
                // so it clears the case below the mouth.
                float u = (SpitAt - T) / (SpitAt - LandAt);
                var to = land + Vector3.up * r;
                at = Vector3.Lerp(pop, to, 1f - (1f - u) * (1f - u));
                at.y = Mathf.Lerp(pop.y, to.y, u) + 0.35f * 4f * u * (1f - u);
                spin = 540f * u;
                // A stretch as it leaves the mouth, a squash as it hits the ground.
                squash = Tuck * (1f + 0.25f * Mathf.Exp(-u * 12f));
            }
            else if (T > RollEnd)
            {
                // The roll: on along the ground to its feet, slowing, the last half turn.
                float u = (LandAt - T) / (LandAt - RollEnd);
                float e = 1f - (1f - u) * (1f - u);
                at = Vector3.Lerp(land, exit, e) + Vector3.up * r;
                spin = 540f + 180f * e;
                squash = Tuck * (1f - 0.18f * Mathf.Exp(-u * 10f));
                if (!thudded)
                {
                    thudded = true;
                    Fx.SpadeDust(model.TransformPoint(land), -model.TransformDirection(dir.normalized));
                }
            }
            else
            {
                // Up: unfurls from the tuck to standing, overshooting a little.
                float u = Mathf.Clamp01((RollEnd - T) / RollEnd);
                float stand = 1f + 0.12f * Mathf.Sin(u * Mathf.PI) - (1f - Tuck) * (1f - Mathf.SmoothStep(0f, 1f, u * 1.4f));
                squash = stand;
                at = exit + Vector3.up * (s.Half * stand);
                spin = 720f;
            }
            s.Root.localPosition = at;
            s.Root.localRotation = face * Quaternion.Euler(spin, 0f, 0f);
            // Tucked, it hugs its weapon down along its body; it lets go as it stands, back to the rest the real one has.
            if (s.Arms)
            {
                float hug = T > RollEnd ? 1f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((RollEnd - T) / RollEnd * 1.4f));
                s.Arms.localRotation = s.ArmsRest * Quaternion.Euler(HugDeg * hug, 0f, 0f);
            }
            // Tucked into a ball: shorter, a little fatter.
            float fat = 1f + (1f - Mathf.Min(1f, squash)) * 0.3f;
            s.Ball.localScale = new Vector3(fat, squash, fat);
        }

        void HeadOnly(bool training, float dt)
        {
            if (!head) return;
            float t = Time.time + phase;
            float a = training ? Mathf.Max(0f, Mathf.Sin(t * 6.2831853f / 1.6f)) * 25f : 0f;
            head.localRotation = headRest * Quaternion.Euler(a, 0f, 0f);
        }

        // ---------------------------------------------------------------- the flag

        void Flag(float dt)
        {
            if (cloth == null) return;
            var wind = FxSystems.Wind; wind.y = 0f;
            float strength = Mathf.Clamp(wind.magnitude / 0.5f, 0.3f, 1.6f);
            // The cloth's +x streams downwind; it swings round to a change of wind over a second or two.
            float want = wind.sqrMagnitude > 1e-4f ? Mathf.Atan2(-wind.z, wind.x) * Mathf.Rad2Deg : 0f;
            flagYaw = float.IsNaN(flagYaw) ? want : Mathf.MoveTowardsAngle(flagYaw, want, 60f * dt);
            cloth.rotation = Quaternion.Euler(0f, flagYaw, 0f);
            flagPhase += dt * (5f + 4f * strength);
            Cloth(flagPhase + phase, strength);
            clothMesh.vertices = clothV;
            clothMesh.RecalculateNormals();
            clothMesh.RecalculateBounds();
        }

        /// <summary>The cloth's vertices: a travelling ripple that grows toward the free edge, a little droop in a light
        /// wind, both faces.</summary>
        void Cloth(float t, float strength)
        {
            for (int side = 0; side < 2; side++)
                for (int u = 0; u < CU; u++)
                    for (int v = 0; v < CV; v++)
                    {
                        float fu = u / (CU - 1f), fv = v / (CV - 1f);
                        float x = fu * FlagW;
                        float wave = Mathf.Sin(fu * 7.5f - t + fv * 0.8f) * 0.07f * fu + Mathf.Sin(fu * 13f - t * 1.7f) * 0.018f * fu;
                        float droop = (1f - Mathf.Clamp01(strength)) * 0.08f * fu * fu;
                        clothV[side * CU * CV + u * CV + v] = new Vector3(x * (1f - 0.05f * Mathf.Abs(wave) * 10f * fu), -fv * FlagH - droop, wave * (0.6f + 0.6f * strength));
                    }
        }
    }
}
