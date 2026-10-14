using System.Collections;
using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The drill rig's act (MOTION.md drill_rig): a helper that owns only the rig's own nodes (dr_*). VehicleWorks keeps the
    /// hull, the treads, sprockets and idlers.
    ///  - On the road (VehicleWorks runs Tick): the amber lamp (dr_lamp) is dark; once the rig on 'drill' is within 4 tiles of
    ///    its zone it blinks (arriving), 0.6 s a beat.
    ///  - The deploy (VehicleWorks.HandOff runs Deploy when the sim has turned the rig into a deep mine): the sim does it in one
    ///    tick, so the view plays it as it hands over, the mine starting at build progress 0 where the sim put it.
    ///      0.0-1.0 s  the mast (dr_mast) stands up off the stern, hinged at its foot, eased, the winch (dr_winch) winding;
    ///                 the lamp lights; it locks upright with a puff of dust at its foot.
    ///      1.0-1.4 s  the auger (dr_drill) runs out of the mast's foot to the ground, turning (2 turns a second);
    ///      1.4 s      it bites: chips of the deposit's own ore fly off the hole (crystal and uranium glowing), a puff of
    ///                 dust tinted to the ore rises; it drills on down 0.14 into the ground, chips flying every 0.12 s.
    ///      1.8-3.6 s  the deposit's own deep mine (deep_mine_iron / _copper / _crystal / _uranium) rises over the shaft
    ///                 through its build stages, while the rig drills itself down: it sinks its whole height (eased in, so it's
    ///                 under the ground by 70% of the rise) with the auger still turning. Nothing pops: the rig is gone under
    ///                 the ground before it's removed, and the mine comes up out of it.
    /// Effects go to the mine's audience (WorldView.AudienceFor).
    /// </summary>
    public class DrillRigAct
    {
        const float MastTime = 1.0f, RunOut = 0.4f, DrillOn = 0.4f, Rise = 1.8f, BitLength = 0.5f, DrillDeeper = 0.14f;
        readonly Renderer lamp;
        float shownLamp = -1f;

        DrillRigAct(Transform model)
        {
            lamp = LampOf(model);
            Glow(lamp, ref shownLamp, 0f);
        }

        /// <summary>The act for a model that has the rig's nodes, else null.</summary>
        public static DrillRigAct For(Transform model) => Has(model) ? new DrillRigAct(model) : null;

        public static bool Has(Transform model) => model && PezMotion.FindDeep(model, "dr_mast");

        static Renderer LampOf(Transform model)
        {
            var l = PezMotion.FindDeep(model, "dr_lamp");
            var ls = l ? PezShade.Emissive(l.gameObject) : new Renderer[0];
            return ls.Length > 0 ? ls[0] : null;
        }

        /// <summary>On the road: the lamp blinks as the rig nears its zone.</summary>
        public void Tick(WorldView view, WorldView.EV v, float dt)
        {
            var e = v.E;
            float g = 0f;
            if (e.Order == Order.Drill && e.ZoneId != 0 && view.World.Map.DepositById(e.ZoneId) is DeepDeposit d && Vec2.Dist(e.Pos, d.Pos) < 4f)
                g = Mathf.Repeat(Time.time, 0.6f) < 0.3f ? 1f : 0f;
            Glow(lamp, ref shownLamp, g);
        }

        static void Glow(Renderer r, ref float shown, float g)
        {
            if (r == null || Mathf.Abs(g - shown) < 0.01f) return;
            shown = g;
            PezShade.Set(r, Mathf.Lerp(0.35f, 1f, g), g);
        }

        /// <summary>The hand-over to the deep mine `into`, run by VehicleWorks.HandOff (the one deploy hand-off), which has
        /// already set the mine back to build progress 0, stopped the rig's PezMotion and copied the mine's `audience`. Runs on
        /// the rig's `m`odel, which WorldView has unparented from its rig; destroys it at the end.</summary>
        public static IEnumerator Deploy(WorldView view, Transform m, WorldView.EV into, List<int> audience)
        {
            var model = m.gameObject;
            var target = into.Rig.Emerge;
            var mast = PezMotion.FindDeep(m, "dr_mast");
            var drill = PezMotion.FindDeep(m, "dr_drill");
            var winch = PezMotion.FindDeep(m, "dr_winch");
            var lamp = LampOf(m);
            float shown = -1f;
            Glow(lamp, ref shown, 1f);
            Quaternion mastRest = mast ? mast.localRotation : Quaternion.identity;
            Quaternion drillRot = drill ? drill.localRotation : Quaternion.identity;
            Quaternion winchRest = winch ? winch.localRotation : Quaternion.identity;
            Vector3 drillRest = drill ? drill.localPosition : Vector3.zero;
            var deposit = view.World.Map.DepositById(into.E.DepositId);
            int ore = deposit != null ? Mathf.Clamp(deposit.Type, 0, WorldView.OreColors.Length - 1) : -1;
            Color32 oreColor = ore >= 0 ? (Color32)WorldView.OreColors[ore] : new Color32(140, 120, 100, 255);
            bool oreGlows = ore >= 2;

            float spin = 0f, outNow = 0f, chipT = 0f;
            void Auger(float dt)
            {
                if (!drill) return;
                spin = Mathf.Repeat(spin + 720f * dt, 360f);
                drill.localPosition = drillRest + drillRot * Vector3.up * outNow;
                drill.localRotation = drillRot * Quaternion.Euler(0f, spin, 0f);
            }
            Vector3 Hole() => drill ? drill.TransformPoint(Vector3.up * BitLength) : m.position;

            // 1. The mast stands up, the winch winding.
            for (float t = 0f; t < MastTime; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / MastTime);
                if (mast) mast.localRotation = mastRest * Quaternion.Euler(-90f * k, 0f, 0f);
                if (winch) winch.localRotation = winchRest * Quaternion.Euler(-540f * k, 0f, 0f);
                yield return null;
            }
            if (mast) mast.localRotation = mastRest * Quaternion.Euler(-90f, 0f, 0f);
            var foot = Hole(); foot.y = 0f;
            Fx.Audience = audience;
            Fx.SpadeDust(foot, -m.forward);
            Fx.Audience = null;

            // 2. The auger runs out to the ground, turning; it bites and drills on down.
            float reach = drill ? Mathf.Max(0.05f, Hole().y) : 0.3f;    // the bit's height over the ground, stood up
            bool bit = false;
            for (float t = 0f; t < RunOut + DrillOn; t += Time.deltaTime)
            {
                float dt = Time.deltaTime;
                outNow = t < RunOut ? reach * Mathf.SmoothStep(0f, 1f, t / RunOut) : reach + DrillDeeper * Mathf.Clamp01((t - RunOut) / DrillOn);
                Auger(dt);
                if (t >= RunOut && !bit)
                {
                    bit = true;
                    Fx.Audience = audience;
                    var h = Hole(); h.y = 0.02f;
                    for (int i = 0; i < 5; i++) Fx.OreBrick(h, Quaternion.Euler(0f, 72f * i + Random.Range(-20f, 20f), 0f) * m.forward, oreColor, oreGlows);
                    Fx.PourDust(h, oreColor);
                    Fx.SpadeDust(h, m.right);
                    Fx.Audience = null;
                }
                if (bit && (chipT -= dt) <= 0f)
                {
                    chipT = 0.12f;
                    Fx.Audience = audience;
                    var h = Hole(); h.y = 0.02f;
                    Fx.OreBrick(h, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * m.forward, oreColor, oreGlows);
                    Fx.Audience = null;
                }
                yield return null;
            }

            // 3. The mine rises over the shaft as the rig drills itself down, out of sight before it goes.
            float top = 0f;
            foreach (var r in model.GetComponentsInChildren<Renderer>()) top = Mathf.Max(top, r.bounds.max.y);
            float depth = top + 0.05f;
            Vector3 start = m.position;
            for (float t = 0f; t < Rise; t += Time.deltaTime)
            {
                float k = t / Rise, s = Mathf.Clamp01(k / 0.7f);
                m.position = start + Vector3.down * depth * s * s * (3f - 2f * s);
                target.SetBuildProgress(k);
                Auger(Time.deltaTime);
                yield return null;
            }
            target.SetBuildProgress(1f);
            Object.Destroy(model);
        }
    }
}
