using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The optics lab at work (MOTION.md optics_lab). It grinds crystal into lenses while the sim says it's working
    /// (Entity.Working: it had crystal to grind this tick), 0.5 lenses a second, half that on low power.
    ///  - Working: the team dome (the model's head node) tracks the sky, turning 3 deg/s, and every 9 to 14 s slews to a
    ///    new star at 25 deg/s while the telescope (scope) nods to a new height. The dome only turns through the arc that
    ///    keeps the telescope 50 deg clear of the dispenser either side (it would reach it within 29 deg), reversing at
    ///    the ends, and the telescope only looks down once it's home, so it never swings through the dispenser, the hopper
    ///    or the bench. The light (light, from the collimator)
    ///    reaches out to the prism and the prism fans it into three rays (rays) onto the lens blanks on the turntable
    ///    (lathe), which turns at 80 rpm. Each lens finished pops out of the dispenser: its team head (cap, hinged at the
    ///    back) flips open, the top lens (candy) rises into the throat with a cyan glint and is given out, the head drops
    ///    shut with a bounce and the stack (stack) pushes the next one up.
    ///  - Idle (no crystal): the light and rays go out, the turntable coasts to a stop, the dome turns home and the
    ///    telescope parks, pointing down; its objective lens goes dim.
    ///  - Low power: everything at half speed, the dome turning in jerks, and the light and rays browning out.
    /// A render whose team only remembers the lab under fog shows it dark and still. A model without those nodes gets the
    /// parts it has.
    /// </summary>
    public class OpticsWorks
    {
        const float LensRate = 0.5f;      // lenses a second (Defs: the recipe's rate), halved on low power
        const float Pitch = 0.12f;        // the stack's spacing (the model's lens candies)
        const float Home = 45f;           // the dome's parking yaw: the parked telescope looks out between the hopper and the bench
        const float Clear = 50f;          // the telescope keeps this far (yaw, either side) from the dispenser's bearing: it never swings through it, and from the game camera it visibly stops short
        const float Raised = 8f;          // the lowest the telescope looks (nod) anywhere but home: above the hopper and the bench

        readonly Transform head, scope, light, rays, lathe, stack, candy, cap;
        readonly Quaternion headRest, scopeRest, capRest;
        readonly Vector3 stackRest, candyRest;
        readonly Renderer objective, candyLamp;
        readonly float phase;
        float yaw, yawGoal, nod, nodGoal, slewAt = -1f, rpm, lightK, raysK, lensT, popT = -1f, workK;
        readonly float yawMin = -1e6f, yawMax = 1e6f; // the arc the dome may turn through (never past the dispenser)

        OpticsWorks(Transform model, int seed)
        {
            head = PezMotion.FindDeep(model, "head");
            scope = PezMotion.FindDeep(model, "scope");
            light = PezMotion.FindDeep(model, "light");
            rays = PezMotion.FindDeep(model, "rays");
            lathe = PezMotion.FindDeep(model, "lathe");
            stack = PezMotion.FindDeep(model, "stack");
            candy = PezMotion.FindDeep(model, "candy");
            cap = PezMotion.FindDeep(model, "cap");
            if (head) headRest = head.localRotation;
            if (scope) scopeRest = scope.localRotation;
            if (cap) capRest = cap.localRotation;
            if (stack) stackRest = stack.localPosition;
            if (candy) candyRest = candy.localPosition;
            var o = PezMotion.FindDeep(model, "scope__M_E_Cyan");
            objective = o ? o.GetComponent<Renderer>() : null;
            var c = PezMotion.FindDeep(model, "candy__M_E_Cyan");
            candyLamp = c ? c.GetComponent<Renderer>() : null;
            phase = seed * 0.37f;
            lensT = Mathf.Repeat(seed * 0.29f, 1f);
            nod = nodGoal = 40f; yaw = yawGoal = Home; // parked until it has work
            if (head && stack)
            {
                // The dispenser stands taller than the telescope's reach: the dome's arc stops short of it on both sides
                // (sampled in clear.py: the barrel touches it within 29 deg of its bearing at every height it looks).
                // The bearing comes from the nodes, so the importer's mirrored x doesn't matter.
                var to = head.parent.InverseTransformPoint(stack.position) - head.localPosition; to.y = 0f;
                var fwd = headRest * Vector3.forward; fwd.y = 0f;
                float bearing = Vector3.SignedAngle(fwd, to, Vector3.up);
                float lo = bearing + Clear;
                lo += 360f * Mathf.Floor((Home - lo) / 360f);             // the arc's start at or below home
                yawMin = lo; yawMax = lo + 360f - 2f * Clear;
            }
        }

        public static OpticsWorks For(GameObject model, int seed) => new OpticsWorks(model.transform, seed);

        /// <summary>`working`: the sim says it's grinding; `low`: its team is on low power; `seen`: the render's team can
        /// see its live state (false: remembered under fog).</summary>
        public void Tick(bool working, bool low, bool seen, float dt)
        {
            float now = Time.time;
            if (!seen) working = low = false;
            float rate = low ? 0.5f : 1f;
            workK = Mathf.MoveTowards(workK, working ? 1f : 0f, dt / 0.6f);

            // The dome: track and slew while working, turn home and park the telescope when idle. Low power: in jerks.
            if (seen && head)
            {
                float motor = low ? (Mathf.Repeat(now * 1.6f + phase, 1f) < 0.45f ? 1f : 0f) : 1f;
                if (working)
                {
                    if (slewAt < 0f || now >= slewAt)
                    {
                        // A new star, 40-120 deg away within the arc (the way that has room, if only one does).
                        float step = Random.Range(40f, 120f), up = yawMax - 10f - yaw, down = yaw - yawMin - 10f;
                        bool ahead = up >= step && down >= step ? Random.value < 0.5f : up >= down;
                        yawGoal = Mathf.Clamp(yaw + (ahead ? step : -step), yawMin + 10f, yawMax - 10f);
                        nodGoal = Random.Range(-12f, Raised);
                        slewAt = now + Random.Range(9f, 14f) / rate;
                    }
                    if (nod > Raised) nod = Mathf.MoveTowards(nod, Raised, 20f * rate * motor * dt); // unparking: up before it turns
                    else
                    {
                        yawGoal = Mathf.Min(yawGoal + 3f * rate * motor * dt, yawMax);         // tracking the sky, to the arc's end
                        if (yaw >= yawMax - 0.5f) slewAt = now;                                 // at the stop: slew back
                        yaw = Mathf.Clamp(Mathf.MoveTowards(yaw, yawGoal, 25f * rate * motor * dt), yawMin, yawMax);
                        nod = Mathf.MoveTowards(nod, nodGoal, 12f * rate * motor * dt);
                    }
                }
                else
                {
                    slewAt = -1f;
                    // Home the way it came (inside the arc), then park: it only looks down once it's home.
                    yaw = Mathf.MoveTowards(yaw, Home, 20f * dt);
                    yawGoal = yaw;
                    bool home = Mathf.Abs(yaw - Home) < 10f;
                    nod = Mathf.MoveTowards(nod, home ? 40f : Mathf.Min(nod, Raised), 15f * dt); // parked, looking down
                }
                head.localRotation = headRest * Quaternion.Euler(0f, yaw, 0f);
                if (scope) scope.localRotation = scopeRest * Quaternion.Euler(nod, 0f, 0f);
            }

            // The light reaches out of the collimator to the prism, then the rays fan out of the prism; out in reverse.
            // Low power browns them out: a 4 Hz flicker with a dropout every 2 to 3 s.
            bool on = working;
            if (working && low)
            {
                on = Mathf.Repeat(now * 4f + phase, 1f) < 0.6f;
                float cyc = now / 2.5f + phase, inCyc = (cyc - Mathf.Floor(cyc)) * 2.5f;
                if (inCyc < 0.25f) on = false;
            }
            if (low && working) { lightK = on ? 1f : 0f; raysK = lightK; }
            else
            {
                lightK = Mathf.MoveTowards(lightK, on ? 1f : (raysK > 0f ? 1f : 0f), dt / 0.2f);
                raysK = Mathf.MoveTowards(raysK, on && lightK >= 1f ? 1f : 0f, dt / 0.2f);
            }
            if (light) Show(light, lightK);
            if (rays) Show(rays, raysK);
            if (objective) PezShade.Set(objective, Mathf.Lerp(0.35f, 1f, workK), Mathf.Lerp(0.15f, low ? 0.5f : 1f, workK) * (working && !on ? 0.3f : 1f));

            // The turntable turns under the rays and coasts to a stop without them.
            if (lathe)
            {
                rpm = Mathf.MoveTowards(rpm, working ? 80f * rate : 0f, 60f * dt);
                lathe.localRotation *= Quaternion.Euler(0f, rpm * 6f * dt, 0f);
            }

            // A lens finished: the dispenser gives it out.
            if (working && (lensT += dt * LensRate * rate) >= 1f) { lensT -= 1f; popT = 0f; }
            if (popT >= 0f) Pop(dt, low);
        }

        static void Show(Transform t, float k)
        {
            float s = k <= 0f ? 0.0001f : Mathf.SmoothStep(0f, 1f, k);
            t.localScale = new Vector3(s, s, s);
            if (t.gameObject.activeSelf != k > 0f) t.gameObject.SetActive(k > 0f);
        }

        /// <summary>The Pez moment, 1.1 s: the head snaps open, the top lens rises into the throat with a glint and is gone,
        /// the head drops shut with a bounce while the stack pushes the next lens up into its place.</summary>
        void Pop(float dt, bool low)
        {
            popT += dt;
            float t = popT;
            if (cap)
            {
                float a;
                if (t < 0.12f) a = Mathf.SmoothStep(0f, 1f, t / 0.12f);
                else if (t < 0.45f) a = 1f;
                else { float u = t - 0.45f; a = Mathf.Abs(Mathf.Cos(u * 7f)) * Mathf.Exp(-u * 5f); }
                cap.localRotation = capRest * Quaternion.Euler(40f * a, 0f, 0f);
            }
            if (candy)
            {
                // Up into the throat, then out over the lip toward the front, shrinking away.
                float up = Mathf.SmoothStep(0f, 1f, t / 0.14f) * 0.1f;
                float go = Mathf.Clamp01((t - 0.2f) / 0.22f);
                var front = cap ? candy.parent.InverseTransformDirection(cap.parent.TransformDirection(capRest * Vector3.back)) : Vector3.zero;
                front.y = 0f;
                candy.localPosition = candyRest + Vector3.up * (up + 0.06f * Mathf.Sin(go * Mathf.PI)) + front.normalized * 0.16f * go;
                float s = t < 0.42f ? 1f - go * go : 0f;
                if (t >= 0.85f) { s = 1f; candy.localPosition = candyRest; }                // the next one, now on top
                candy.localScale = Vector3.one * Mathf.Max(s, 0.0001f);
                if (candyLamp) PezShade.Set(candyLamp, 1f, (low ? 0.5f : 1f) * (t < 0.42f ? 1f + 2.5f * Mathf.Sin(Mathf.Clamp01(t / 0.3f) * Mathf.PI) : 1f));
            }
            if (stack)
            {
                // The spring pushes the stack up one lens, then the view swaps it back as the new top lens appears.
                float k = Mathf.SmoothStep(0f, 1f, (t - 0.45f) / 0.4f);
                stack.localPosition = stackRest + Vector3.up * (t < 0.85f ? Pitch * k : 0f);
            }
            if (t >= 1.1f)
            {
                popT = -1f;
                if (cap) cap.localRotation = capRest;
                if (stack) stack.localPosition = stackRest;
            }
        }
    }
}
