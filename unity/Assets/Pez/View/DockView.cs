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
    /// </summary>
    public class DockView
    {
        public enum Phase { Idle, Clear, Active }

        readonly Renderer lamps;               // the model's own pair
        readonly Renderer[] pips;
        readonly Transform chute;
        readonly Vector3 chuteIn, chuteOut;
        readonly Material amber, cream, lampAmber, lampCream;
        float chuteK, clearT = -1f;
        Phase shown = (Phase)(-1);
        bool creamShown;

        static Material pipAmber, pipCream, kraft, licorice;

        /// <summary>True when the bay is on the door side (south): the door rolls up for the truck.</summary>
        public readonly bool DoorSide;
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
            lamps = lampR;
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

            // The chute: a kraft trough sloping down into the building. Out, its lip is just over the backed-in truck's
            // tail (the bay point is half a tile outside the footprint; the bin's rear edge 0.4 in from the truck's
            // centre); in, it's 0.5 back: parked in the refinery's bay, or behind the command center's door. Doors face
            // -Z; when terrain moved the bay to another side, the chute comes out of that wall instead.
            DoorSide = dir.z < -0.7f;
            if (door != null)
            {
                float reach = (Mathf.Abs(dir.x) > 0.7f ? halfX : halfY) + 0.15f;
                var c = new GameObject("chute").transform;
                c.SetParent(stage, false);
                chuteOut = dir * reach + (DoorSide ? new Vector3(model.InverseTransformPoint(door.position).x, 0f, 0f) : Vector3.zero);
                chuteIn = chuteOut - dir * 0.5f;
                c.localPosition = chuteIn;
                c.localRotation = Quaternion.LookRotation(-dir, Vector3.up); // the trough runs along local +Z, into the building
                const float len = 0.62f, w = 0.42f, slope = 12f;
                var tray = new GameObject("tray").transform;
                tray.SetParent(c, false);
                tray.localPosition = new Vector3(0f, 0.24f, len * 0.5f);
                tray.localRotation = Quaternion.Euler(slope, 0f, 0f);    // the outer end higher, at the truck's bed
                Part(tray, new Vector3(0f, 0f, 0f), new Vector3(w, 0.025f, len), kraft);
                Part(tray, new Vector3(-w * 0.5f, 0.035f, 0f), new Vector3(0.03f, 0.07f, len), licorice);
                Part(tray, new Vector3(w * 0.5f, 0.035f, 0f), new Vector3(0.03f, 0.07f, len), licorice);
                chute = c;
            }
            Apply(Phase.Idle, 0f);
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
                float goal = phase == Phase.Active ? 1f : 0f;
                if (chuteK != goal)
                {
                    chuteK = Mathf.MoveTowards(chuteK, goal, dt / 0.5f);
                    float k = goal > 0f ? EaseOutBack(chuteK) : chuteK * chuteK * (3f - 2f * chuteK);
                    chute.localPosition = Vector3.LerpUnclamped(chuteIn, chuteOut, k);
                }
            }
            Apply(phase, Time.time);
        }

        static float EaseOutBack(float k)
        {
            // Ease-out with about 0.03 tiles of overshoot on a 0.5 slide.
            const float c = 0.9f;
            float x = k - 1f;
            return 1f + (c + 1f) * x * x * x + c * x * x;
        }

        void Apply(Phase phase, float now)
        {
            bool creamNow = phase == Phase.Clear;
            if (creamNow != creamShown || phase != shown)
            {
                var pm = creamNow ? pipCream : pipAmber;
                for (int i = 0; i < pips.Length; i++) pips[i].sharedMaterial = pm;
                lamps.sharedMaterial = creamNow ? lampCream : lampAmber;
                creamShown = creamNow;
            }
            if (phase == Phase.Active)
            {
                // 2 Hz chase: a bright step runs along each column (or in from both ends of the row) to the lamps.
                int n = pips.Length == 6 ? 4 : 3;         // steps per sweep (3 pips + the lamp, or 2 pips + the lamp)
                float ph = now * 2f;
                int lit = (int)((ph - Mathf.Floor(ph)) * n);
                for (int i = 0; i < pips.Length; i++)
                {
                    int step = pips.Length == 6 ? i % 3 : (i < 2 ? i : 3 - i);
                    PezShade.Set(pips[i], 1f, step == lit ? 1f : step == lit - 1 ? 0.35f : 0.08f);
                }
                PezShade.Set(lamps, 1f, lit == n - 1 ? 1.2f : 0.35f);
            }
            else if (phase != shown)
            {
                float g = phase == Phase.Clear ? 1f : 0f;   // steady cream, or unlit
                float d = phase == Phase.Clear ? 1f : 0.55f;
                for (int i = 0; i < pips.Length; i++) PezShade.Set(pips[i], d, g);
                PezShade.Set(lamps, d, phase == Phase.Clear ? 1f : 0.06f);
            }
            shown = phase;
        }
    }
}
