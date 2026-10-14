using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The geological surveyor's act (MOTION.md geological_surveyor), a helper VehicleWorks runs. It owns only the
    /// surveyor's own nodes (sv_*); VehicleWorks keeps the hull, the wheels and the steering. Driven by the sim's survey: the
    /// surveyor stands within 0.6 of its site for EntityDef.SurveySeconds (8 s, e.WorkTimer), then every deep deposit within
    /// SurveyRadius (12 tiles) gets its flag (DeepDepositsView raises the stakes that tick).
    ///  - It stops: the collar foot (sv_foot) goes down onto the ground and the mast (sv_mast) telescopes up 0.14 (0.4 s).
    ///  - It listens: the dish (sv_dish) turns, up to 90 deg/s, and the feed horn's amber bead (sv_feed) lights.
    ///  - It sounds: the team hammer (sv_hammer) in the thumper's window is drawn up 0.09 on its spring (sv_spring stretches
    ///    to it) and slams down five times, at 1.2, 2.6, 4.0, 5.4 and 6.8 s of the sim's survey. Each slam sends a ripple
    ///    out across the ground to the survey radius over 1.2 s, kicks dust off the foot and jolts the hull; the last ripple
    ///    reaches 12 tiles at 8 s, the tick the sim plants the flags.
    ///  - Its progress: the four amber lamps on the thumper's cap (sv_tick_0..3) light one by one, at 1.6, 3.6, 5.6 and 7.6 s.
    ///  - It packs up ahead of the sim (which may send it straight on to its next site at 8 s): the dish turns home from
    ///    6.8 s, the foot lifts at 7.8 s and the mast drops once the dish faces forward. When the sim plants the flags the
    ///    four lamps blink together three times, then go dark.
    /// The clock follows e.WorkTimer, smoothed between the sim's ticks; anything else (moving off, a new order) packs up.
    /// </summary>
    public class SurveyorAct
    {
        const float MastUp = 0.14f, FootDown = 0.033f, Lift = 0.09f, SpringLen = 0.3f, HammerTop = 0.07f;
        /// <summary>The slams, in sim seconds into the survey: the last ripple (1.2 s) lands as the flags go in at 8 s.</summary>
        static readonly float[] Beats = { 1.2f, 2.6f, 4.0f, 5.4f, 6.8f };
        const float Draw = 0.55f, Slam = 0.05f;

        readonly Transform mast, dish, hammer, spring, foot;
        readonly Vector3 mastRest, hammerRest, footRest, springScale;
        readonly Quaternion dishRest;
        readonly Renderer feed;
        readonly Renderer[] ticks;
        float mastK, footK, yaw, yawSpeed, clock = -1f, lastClock = -1f, found = -1f, shownFeed = -1f;
        readonly float[] shownTick;

        SurveyorAct(Transform model)
        {
            mast = PezMotion.FindDeep(model, "sv_mast");
            dish = PezMotion.FindDeep(model, "sv_dish");
            hammer = PezMotion.FindDeep(model, "sv_hammer");
            spring = PezMotion.FindDeep(model, "sv_spring");
            foot = PezMotion.FindDeep(model, "sv_foot");
            if (mast) mastRest = mast.localPosition;
            if (dish) dishRest = dish.localRotation;
            if (hammer) hammerRest = hammer.localPosition;
            if (foot) footRest = foot.localPosition;
            if (spring) springScale = spring.localScale;
            var f = PezMotion.FindDeep(model, "sv_feed");
            var fs = f ? PezShade.Emissive(f.gameObject) : new Renderer[0];
            feed = fs.Length > 0 ? fs[0] : null;
            ticks = new Renderer[4];
            for (int i = 0; i < 4; i++)
            {
                var t = PezMotion.FindDeep(model, "sv_tick_" + i);
                var ts = t ? PezShade.Emissive(t.gameObject) : new Renderer[0];
                ticks[i] = ts.Length > 0 ? ts[0] : null;
            }
            shownTick = new[] { -1f, -1f, -1f, -1f };
        }

        /// <summary>The act for a model that has the surveyor's nodes, else null.</summary>
        public static SurveyorAct For(Transform model) => model && PezMotion.FindDeep(model, "sv_mast") ? new SurveyorAct(model) : null;

        public void Tick(WorldView view, WorldView.EV v, VehicleWorks works, float dt)
        {
            var e = v.E;
            bool surveying = e.Order == Order.Survey && !e.Moving && Vec2.Dist(e.Pos, e.OrderPos) <= 0.6f;
            if (surveying)
            {
                float sim = e.WorkTimer;
                // Run smoothly between the sim's ticks, never more than a tick from it.
                clock = clock < 0f || Mathf.Abs(clock - sim) > 0.25f ? sim : Mathf.Clamp(clock + dt, sim - 0.05f, sim + 0.06f);
            }
            else
            {
                // The survey just finished (the sim planted the flags and moved it on): the lamps blink.
                if (clock > EntityDef.SurveySeconds - 0.6f) found = 0f;
                clock = -1f;
            }
            float t = clock;

            // The slams: draw the hammer up before each beat, drop it at the beat.
            float lift = 0f;
            if (t >= 0f)
                foreach (var b in Beats)
                {
                    if (t >= b - Draw && t < b) lift = Lift * Mathf.SmoothStep(0f, 1f, (t - (b - Draw)) / (Draw - 0.05f));
                    else if (t >= b && t < b + Slam) lift = Lift * (1f - (t - b) / Slam);
                    if (lastClock >= 0f && lastClock < b && t >= b) Thump(view, v, works);
                }
            lastClock = t;
            if (hammer) hammer.localPosition = hammerRest + Vector3.up * lift;
            if (spring)
            {
                // Hung from under the cap down to the hammer's top.
                float top = spring.localPosition.y, len = top - (hammerRest.y + lift + HammerTop);
                float rest = top - (hammerRest.y + HammerTop);
                spring.localScale = new Vector3(springScale.x, springScale.y * Mathf.Max(0.05f, len / Mathf.Max(0.01f, rest)), springScale.z);
            }

            // Foot down while it surveys (up again at 7.8 s); the mast up while it listens.
            bool settled = t >= 0f && t < EntityDef.SurveySeconds - 0.2f;
            footK = Mathf.MoveTowards(footK, settled ? 1f : 0f, dt / 0.25f);
            if (foot) foot.localPosition = footRest + Vector3.down * FootDown * Mathf.SmoothStep(0f, 1f, footK);
            // The dish turns while it listens and goes home from the last slam; the mast drops only with the dish home.
            bool listen = t >= 0f && t < Beats[Beats.Length - 1];
            yawSpeed = Mathf.MoveTowards(yawSpeed, listen && mastK > 0.95f ? 90f : 0f, dt * 120f);
            if (listen) yaw = Mathf.Repeat(yaw + yawSpeed * dt, 360f);
            else yaw = Mathf.MoveTowardsAngle(yaw, 0f, dt * 180f);
            bool home = Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) < 1f;
            if (home && !listen) yaw = 0f;
            bool up = (t >= 0f && t < EntityDef.SurveySeconds - 0.4f) || !home;
            mastK = Mathf.MoveTowards(mastK, up ? 1f : 0f, dt / 0.4f);
            if (mast) mast.localPosition = mastRest + Vector3.up * MastUp * Mathf.SmoothStep(0f, 1f, mastK);
            if (dish) dish.localRotation = dishRest * Quaternion.Euler(0f, yaw, 0f);

            // Lamps: the feed horn listens; the ticks count the survey up, then blink together at the find.
            Glow(feed, ref shownFeed, Mathf.Clamp01(mastK * 1.4f - 0.4f) * (0.75f + 0.25f * Mathf.Sin(Time.time * 9f)));
            float blink = -1f;
            if (found >= 0f)
            {
                found += dt;
                blink = found < 0.9f ? (Mathf.Repeat(found, 0.3f) < 0.17f ? 1f : 0f) : 0f;
                if (found >= 0.9f) found = -1f;
            }
            for (int i = 0; i < 4; i++)
                Glow(ticks[i], ref shownTick[i], blink >= 0f ? blink : t >= 1.6f + 2f * i ? 1f : 0f);
        }

        void Thump(WorldView view, WorldView.EV v, VehicleWorks works)
        {
            works.Bump(14f);                                   // the slam jolts the hull (nose down: the stern kicks up)
            var at = foot ? foot.position : v.Rig.Root.position;
            view.Deposits?.Ripple(at);
            var root = v.Rig.Root;
            var g = new Vector3(at.x, 0f, at.z);
            for (int i = 0; i < 4; i++)
            {
                var dir = Quaternion.Euler(0f, 45f + 90f * i, 0f) * root.forward;
                Fx.TrackDust(g + dir * 0.08f, dir, 0.6f);
            }
        }

        static void Glow(Renderer r, ref float shown, float g)
        {
            if (r == null || Mathf.Abs(g - shown) < 0.01f) return;
            shown = g;
            PezShade.Set(r, Mathf.Lerp(0.35f, 1f, g), g);   // dark lamps go dark, as the deep mines' do
        }
    }
}
