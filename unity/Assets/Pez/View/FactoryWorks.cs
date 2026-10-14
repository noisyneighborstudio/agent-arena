using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The factory as a Pez dispenser on its side, turning out vehicles (MOTION.md factory). Everything runs from the sim's
    /// production queue: f = Progress / BuildTime of the item this factory is making (the queue head, when it's the
    /// building that item comes out of), so low power slows it with the sim and nothing waits on the view.
    ///  - The line: a hull blank (the model's blank node) rises onto the tray at the back (f 0 to 0.12). The gantry picks a
    ///    team head off the press (hook_head replaces die_head, f 0.09), carries it over the hull, lets it down and sets it
    ///    on (blank_head, f 0.44) with welding sparks. From f 0.55 the spring-loaded pusher feeds the hull up the tray and
    ///    into the head house, reaching the chamber mouth at f 1, the gantry riding with it to the end of its runway. The
    ///    spring stretches to stay on the pusher. When the vehicle is trained the hull is in the chamber; the pusher snaps
    ///    back and the spring recoils with a bounce, cocked for the next one. A cancelled item's hull drops away.
    ///  - The press stamps every 1.5 s while the factory is producing (3 s, with a stall on the way up, on low power): each
    ///    stroke puts a fresh head on the die, indexes the foil wrapper roll 36 degrees, puffs steam off the die and smoke
    ///    from the stack (stack_0).
    ///  - The flip-top head over the door lifts with the door (up to 22 degrees as it rolls up) and drops back with a bounce:
    ///    the dispenser opens as each vehicle comes out.
    ///  - Idle: the gantry parked over the press with the hook up, a head waiting on the die, the pusher cocked, the stack
    ///    giving a thin wisp every 3 s (every 4 to 7 s on low power).
    /// A remembered factory under fog shows idle; a burning factory's stack doesn't puff (its smoke is the fire's).
    /// </summary>
    public class FactoryWorks
    {
        // The line's timeline over f.
        const float Rise = 0.12f, Pick = 0.09f, Travel0 = 0.14f, Travel1 = 0.3f, Drop = 0.44f, Raised = 0.55f, Push = 0.55f;
        const float CableMesh = 0.1f; // the cable's modelled length (build_models.py S_factory)

        readonly Transform head, door, spring, pusher, blank, blankHead, gantry, cable, hoist, hookHead, ram, dieHead, spool, stack;
        readonly Quaternion headRest, spoolRest;
        readonly Vector3 ramRest, blankRest;
        readonly float seat, springMesh, back, front, ahead, gantryPress, gantryFront, hoistUp, hoistPick, hoistDrop;
        readonly float phase;

        ProdItem shown;            // the item the line is showing
        float shownF;
        bool hasBlank, picked, restocked = true;
        float pz, pv;              // the pusher's z and speed (a damped spring, so it recoils when it snaps back)
        float gz, hy;              // the gantry's z and the hoist's height (eased)
        float pressT, ramY, spoolAngle, spoolGoal, nextWisp, nextSpark;
        float headAngle, closedT = -1f, lastOpen;

        FactoryWorks(Transform model, int seed)
        {
            head = PezMotion.FindDeep(model, "head");
            door = PezMotion.FindDeep(model, "door");
            spring = PezMotion.FindDeep(model, "spring");
            pusher = PezMotion.FindDeep(model, "pusher");
            blank = PezMotion.FindDeep(model, "blank");
            blankHead = PezMotion.FindDeep(model, "blank_head");
            gantry = PezMotion.FindDeep(model, "gantry");
            cable = PezMotion.FindDeep(model, "cable");
            hoist = PezMotion.FindDeep(model, "hoist");
            hookHead = PezMotion.FindDeep(model, "hook_head");
            ram = PezMotion.FindDeep(model, "ram");
            dieHead = PezMotion.FindDeep(model, "die_head");
            spool = PezMotion.FindDeep(model, "spool");
            stack = PezMotion.FindDeep(model, "stack_0");
            var chamber = PezMotion.FindDeep(model, "chamber");
            phase = seed * 0.37f;
            nextWisp = Time.time + Random.Range(0f, 3f);
            if (head) headRest = head.localRotation;
            if (spool) spoolRest = spool.localRotation;
            if (ram) ramRest = ram.localPosition;
            if (!Line) return;
            // The line's stations, from the model (all under stage_2, in its space; the door is at -z).
            blankRest = blank.localPosition;
            seat = spring.localPosition.z;
            springMesh = (seat - pusher.localPosition.z) / Mathf.Max(0.01f, spring.localScale.y);
            ahead = pusher.localPosition.z - blankRest.z;                       // the hull rides this far ahead of the pusher
            back = seat - 0.3f;                                                 // cocked: the spring near its solid length
            front = chamber ? chamber.localPosition.z + 0.03f : back - 1f;      // the pusher at the chamber mouth
            gantryPress = dieHead ? dieHead.localPosition.z : back + 0.4f;
            gantryFront = front + 0.11f;                                        // the end of the runway, short of the head
            // Hoist heights that bring the carried head's base down onto the die and onto the hull.
            float hookBase = hoist.localPosition.y + hookHead.localPosition.y;  // in gantry space, at the modelled height
            hoistUp = cable.localPosition.y - CableMesh;
            float baseAtUp = hookBase - hoist.localPosition.y + hoistUp;
            hoistPick = hoistUp - (gantry.localPosition.y + baseAtUp - (dieHead ? dieHead.localPosition.y : 0f));
            hoistDrop = hoistUp - (gantry.localPosition.y + baseAtUp - (blankRest.y + blankHead.localPosition.y));
            hoistPick = Mathf.Min(hoistPick, hoistUp);
            pz = back; gz = gantryPress; hy = hoistUp;
            Pose(null, 0f, 0f);
        }

        bool Line => spring && pusher && blank && blankHead && gantry && cable && hoist && hookHead;

        public static FactoryWorks For(GameObject model, int seed) => new FactoryWorks(model.transform, seed);

        /// <summary>The item this factory is making right now, or null: each building works its own item of the team's shared queue
        /// (ProdItem.WorkerId), so a second factory shows its own and not the head's.</summary>
        static ProdItem Making(World w, Entity e) =>
            w.Teams[e.Team].UnitQueues.TryGetValue(e.Def.Produces, out var q) ? q.Find(p => p.WorkerId == e.Id) : null;

        /// <summary>`seen`: this view's team sees its live state; `clean`: not burning (a burning stack's smoke is the fire's).
        /// `audience`: the cameras that may see its effects (WorldView.AudienceFor).</summary>
        public void Tick(World w, Entity e, bool seen, bool clean, List<int> audience, float dt)
        {
            bool low = w.Teams[e.Team].LowPower;
            var item = seen ? Making(w, e) : null;
            var def = item != null ? Defs.Get(item.Key) : null;
            float f = def != null && def.BuildTime > 0f ? Mathf.Clamp01(item.Progress / def.BuildTime) : 0f;
            if (def == null) item = null;
            Fx.Audience = audience;
            if (Line) Feed(item, f, low, clean, dt);
            Press(item != null, low, clean, dt);
            Head(dt);
            if (item == null && clean && seen && stack && Time.time >= nextWisp)
            {
                Fx.StackSmoke(stack.position, 0f);
                nextWisp = Time.time + (low ? Random.Range(4f, 7f) : 3f);
            }
            Fx.Audience = null;
        }

        void Feed(ProdItem item, float f, bool low, bool clean, float dt)
        {
            if (item != shown)
            {
                // The vehicle it was making is out (trained: its hull is in the chamber) or the item went (cancelled):
                // either way the line empties and the pusher snaps back to cock the spring. A cancelled head goes back on
                // the die.
                if (shown != null && shownF < 0.85f) restocked = true;
                hasBlank = picked = false;
                shown = item;
            }
            float goal = item == null || f < Push ? back : Mathf.Lerp(back, front, (f - Push) / (1f - Push));
            if (item != null)
            {
                shownF = f;
                // A new hull goes on once the pusher is back from the last one (where this item has it).
                if (!hasBlank && Mathf.Abs(pz - goal) < 0.08f) hasBlank = true;
                // Picked (or already past the pick when the line first sees it): the die is empty until the next stroke.
                if (!picked && f >= Pick) { picked = true; restocked = false; }
            }
            // The pusher: a stiff spring toward where the line puts it, so it follows the push closely and recoils when it
            // snaps back after a dispense.
            float sdt = Mathf.Min(dt, 1f / 30f);
            pv += ((goal - pz) * 260f - pv * 18f) * sdt;
            pz += pv * sdt;
            if (Mathf.Abs(goal - pz) > 1.5f) { pz = goal; pv = 0f; }
            var p = pusher.localPosition; p.z = pz; pusher.localPosition = p;
            var sc = spring.localScale; sc.y = Mathf.Max(0.05f, seat - pz) / springMesh; spring.localScale = sc;
            Pose(item, f, dt);
            // Welding sparks as the head goes on.
            if (item != null && hasBlank && f > Drop - 0.03f && f < Raised && clean && Time.time >= nextSpark)
            {
                Fx.MuzzleFlash(blankHead.position + Vector3.up * 0.03f, 0.05f);
                nextSpark = Time.time + (low ? 0.4f : 0.2f);
            }
        }

        /// <summary>Hull, heads, gantry and hoist for the line at f (item null: idle).</summary>
        void Pose(ProdItem item, float f, float dt)
        {
            bool on = item != null;
            // The hull sits at the back while it's assembled (rising through the tray floor as it arrives), then rides
            // ahead of the pusher.
            float hullZ = !on || f < Push ? back - ahead : pz - ahead;
            Show(blank, hasBlank);
            if (hasBlank)
            {
                float rise = Mathf.SmoothStep(0f, 1f, f / Rise);
                blank.localPosition = new Vector3(blankRest.x, blankRest.y - 0.3f * (1f - rise), hullZ);
            }
            Show(hookHead, on && f >= Pick && f < Drop);
            Show(blankHead, on && f >= Drop);
            Show(dieHead, !on || !picked || restocked);
            // The gantry: over the press to pick, then over the hull, then riding with it to the end of the runway.
            float gGoal, hGoal = hoistUp;
            if (!on || f < Travel0) gGoal = gantryPress;
            else if (f < Travel1) gGoal = Mathf.Lerp(gantryPress, hullZ, Mathf.SmoothStep(0f, 1f, (f - Travel0) / (Travel1 - Travel0)));
            else gGoal = Mathf.Max(hullZ, gantryFront);
            if (on && f > Pick - 0.05f && f < Travel0) hGoal = Mathf.Lerp(hoistUp, hoistPick, 1f - Mathf.Abs(f - Pick) / 0.05f);
            else if (on && f >= Travel1 && f < Drop) hGoal = Mathf.Lerp(hoistUp, hoistDrop, Mathf.SmoothStep(0f, 1f, (f - Travel1) / (Drop - Travel1)));
            else if (on && f >= Drop && f < Raised) hGoal = Mathf.Lerp(hoistDrop, hoistUp, Mathf.SmoothStep(0f, 1f, (f - Drop) / (Raised - Drop)));
            gz = Mathf.MoveTowards(gz, gGoal, dt * 1.6f);
            hy = Mathf.MoveTowards(hy, hGoal, dt * 0.8f);
            var g = gantry.localPosition; g.z = gz; gantry.localPosition = g;
            var h = hoist.localPosition; h.y = hy; hoist.localPosition = h;
            var c = cable.localScale; c.y = Mathf.Max(0.05f, (cable.localPosition.y - hy) / CableMesh); cable.localScale = c;
        }

        /// <summary>Hidden parts are scaled to nothing rather than deactivated, so the fire shading and team tint still
        /// reach them.</summary>
        static void Show(Transform t, bool on)
        {
            if (!t) return;
            var want = on ? Vector3.one : Vector3.zero;
            if (t.localScale != want) t.localScale = want;
        }

        /// <summary>The press strokes while the factory is producing; each stroke restocks the die, indexes the roll and puffs.</summary>
        void Press(bool producing, bool low, bool clean, float dt)
        {
            if (!ram) return;
            float down = 0f;
            if (producing)
            {
                float was = pressT;
                pressT += dt / (low ? 3f : 1.5f);
                float u = Mathf.Repeat(pressT + phase, 1f), u0 = Mathf.Repeat(was + phase, 1f);
                if (u < 0.07f) down = (u / 0.07f) * (u / 0.07f);                       // falls, accelerating
                else if (u < 0.18f) down = 1f;                                          // on the die
                else if (u < 0.6f)
                {
                    float k = (u - 0.18f) / 0.42f;
                    if (low && k > 0.3f && k < 0.65f) k = 0.3f + (k - 0.3f) * 0.15f;   // labouring: it stalls on the way up
                    down = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, k));
                }
                if (u0 < 0.07f && u >= 0.07f || u0 > u && u >= 0.07f) Stroke(low, clean);
                ramY = down;
            }
            else ramY = Mathf.MoveTowards(ramY, 0f, dt * 2f);
            ram.localPosition = ramRest + Vector3.down * 0.25f * ramY;
            if (spool)
            {
                spoolAngle = Mathf.MoveTowards(spoolAngle, spoolGoal, dt * 160f);
                spool.localRotation = spoolRest * Quaternion.Euler(0f, 0f, spoolAngle);
            }
        }

        void Stroke(bool low, bool clean)
        {
            restocked = true;
            spoolGoal += 36f;
            if (spoolGoal > 3600f) { spoolGoal -= 3600f; spoolAngle -= 3600f; }
            if (!clean) return;
            if (dieHead) Fx.Steam(dieHead.position + Vector3.up * 0.05f, low ? 0.14f : 0.2f);
            if (stack) Fx.StackSmoke(stack.position, low ? 0.4f : 1f);
        }

        /// <summary>The flip-top head lifts as the door rolls up and drops back shut with a bounce.</summary>
        void Head(float dt)
        {
            if (!head || !door) return;
            float open = Mathf.Clamp01((1f - door.localScale.y) / 0.95f);
            float a = 22f * Mathf.SmoothStep(0f, 1f, open);
            if (open < 0.02f && lastOpen >= 0.02f) closedT = 0f;
            lastOpen = open;
            if (closedT >= 0f)
            {
                closedT += dt;
                a += 5f * Mathf.Abs(Mathf.Sin(closedT * 16f)) * Mathf.Exp(-closedT * 7f);
                if (closedT > 0.8f) closedT = -1f;
            }
            if (Mathf.Abs(a - headAngle) < 0.01f) return;
            headAngle = a;
            head.localRotation = headRest * Quaternion.Euler(a, 0f, 0f);
        }
    }
}
