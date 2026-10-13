using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The electronics plant's circuit-candy line (MOTION.md electronics_plant), run from the sim: one cycle per circuit,
    /// so one a second, and half that on low power (the sim halves production).
    ///  - Each cycle the pick-and-place carriage (the model's gantry) dips its nozzle at the copper feeder, carries the chip
    ///    (held) over the belt, pecks it onto the board under it (chip_N, with a solder glint) and goes home; then the belt
    ///    indexes one slot: a blank drops out of the magazine, the boards (board_N) move on through the reflow oven, whose
    ///    vent (fume) puffs solder fumes, and the last goes into the dispenser stem, where the finished boards stack up
    ///    behind the window (mag_N). Every fifth board the team flip-top head pops, the stack is dispensed and a board
    ///    (candy) flips out of the lip and away. The copper reel turns with the line; the roof fan turns while it has power.
    ///  - Starved (the sim's Working is false: not enough copper or steel), the line finishes the board in hand and parks,
    ///    and the input that ran out shows: the reel's copper (winding) runs down to the hub, the magazine's blanks are gone.
    ///  - Glow is state: the clean-room panes and skylight (panes) are lit while the line runs, at half on low power, and
    ///    standing by (dim) when it's starved. The signal tower's acid lamp (lamp_run) is steady while it runs and blinks at
    ///    2 Hz on low power; its amber lamp (lamp_wait) blinks while it waits for inputs. All dark until it's built.
    /// It owns the panes and lamps: For() takes them out of WorldView's generic power glow. A model without these nodes
    /// gets the parts it has.
    /// </summary>
    public class CircuitWorks
    {
        const int Slots = 5, Batch = 5;
        const float PlaceU = 0.43f, IndexU = 0.7f; // in a cycle: the chip goes down at 0.43, the belt indexes from 0.7

        readonly Transform gantry, nozzle, held, reel, winding, blanks, head, candy, fan, fume;
        readonly Transform[] boards, chips, mags;
        readonly Vector3[] slots;
        readonly Vector3 gantryHome, nozzleRest, candyRest, candyOut, windingRest;
        readonly Quaternion headRest, reelRest, fanRest, candyRot;
        readonly float placeZ, pickDip, placeDip;
        readonly Renderer[] panes;
        readonly Renderer runLamp, waitLamp;
        readonly float phase;
        float u, popT = -1f, reelAngle, fanAngle, fanRpm, wind = 1f, paneK = -1f;
        int shift, stacked;
        bool running, dispensed;
        float shownPane = -1f, shownRun = -1f, shownWait = -1f;

        CircuitWorks(Transform model, int seed, ref Renderer[] glows)
        {
            gantry = PezMotion.FindDeep(model, "gantry");
            nozzle = PezMotion.FindDeep(model, "nozzle");
            held = PezMotion.FindDeep(model, "held");
            reel = PezMotion.FindDeep(model, "reel");
            winding = PezMotion.FindDeep(model, "winding");
            blanks = PezMotion.FindDeep(model, "blanks");
            head = PezMotion.FindDeep(model, "head");
            candy = PezMotion.FindDeep(model, "candy");
            fan = PezMotion.FindDeep(model, "fan");
            fume = PezMotion.FindDeep(model, "fume");
            boards = Numbered(model, "board_", Slots);
            chips = Numbered(model, "chip_", Slots);
            mags = Numbered(model, "mag_", Batch);
            slots = new Vector3[boards.Length];
            for (int i = 0; i < boards.Length; i++) slots[i] = boards[i].localPosition;
            if (gantry) gantryHome = gantry.localPosition;
            // The carriage runs along z to over the second board slot (the gantry and the boards share a parent).
            placeZ = slots.Length > 1 ? slots[1].z : gantryHome.z;
            if (nozzle) nozzleRest = nozzle.localPosition;
            // How far the nozzle goes down: to the chips on the feeder, and to the top of a board (measured, so the model
            // can move).
            pickDip = placeDip = 0.08f;
            if (held && slots.Length > 1 && gantry)
            {
                // In the carriage's parent stage (model units, whatever the stage's rise): the feeder's chips top out at
                // 0.585, a board's top is 0.04 over its slot.
                float tip = gantry.parent.InverseTransformPoint(held.position).y;
                placeDip = Mathf.Max(0.02f, tip - (slots[1].y + 0.04f));
                pickDip = Mathf.Max(placeDip, tip - 0.585f);
            }
            if (head) headRest = head.localRotation;
            if (reel) reelRest = reel.localRotation;
            if (fan) fanRest = fan.localRotation;
            if (winding) windingRest = winding.localScale;
            if (candy)
            {
                candyRest = candy.localPosition; candyRot = candy.localRotation;
                // Out over the lip: away from the head's hinge.
                var d = head ? candyRest - head.localPosition : Vector3.back;
                d.y = 0f;
                candyOut = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.back;
                candy.gameObject.SetActive(false);
            }
            if (held) held.gameObject.SetActive(false);
            panes = Lights(model, "panes");
            var run = Lights(model, "lamp_run"); runLamp = run.Length > 0 ? run[0] : null;
            var wait = Lights(model, "lamp_wait"); waitLamp = wait.Length > 0 ? wait[0] : null;
            // These lights say the line's state, so WorldView's power glow leaves them alone.
            if (glows != null)
                glows = System.Array.FindAll(glows, r => r != runLamp && r != waitLamp && System.Array.IndexOf(panes, r) < 0);
            phase = seed * 0.37f;
            stacked = Mathf.Abs(seed) % Batch;            // plants don't all pop together
            u = Mathf.Repeat(seed * 0.29f, 1f) * IndexU;  // and each starts mid-cycle, before its belt indexes
            Pose();
        }

        public static CircuitWorks For(GameObject model, int seed, ref Renderer[] glows) => new CircuitWorks(model.transform, seed, ref glows);

        static Transform[] Numbered(Transform model, string prefix, int max)
        {
            var list = new System.Collections.Generic.List<Transform>();
            for (Transform c; list.Count < max && (c = PezMotion.FindDeep(model, prefix + list.Count)) != null; ) list.Add(c);
            return list.ToArray();
        }

        /// <summary>The emissive renderers under a node.</summary>
        static Renderer[] Lights(Transform model, string node)
        {
            var n = PezMotion.FindDeep(model, node);
            return n ? PezShade.Emissive(n.gameObject) : new Renderer[0];
        }

        /// <summary>
        /// `complete`: built; `working`: the sim's Working (it made circuits this tick); `sees`: the local view sees its live
        /// state (else it holds still, its lights neutral); `fumes`: not on fire (a burning plant's smoke is the fire's).
        /// Effects go to Fx.Audience, which the caller sets.
        /// </summary>
        public void Tick(Team team, bool complete, bool working, bool sees, bool fumes, float dt)
        {
            bool low = team.LowPower;
            working &= complete;
            if (!sees) { Lamps(0.8f, 0.25f, 0.25f, dt, true); return; } // remembered under fog: no live state
            float rate = low ? 0.5f : 1f;

            // The line: runs while working; starved, it finishes the board in hand and parks at the start of a cycle.
            if (working) running = true;
            if (running)
            {
                float was = u;
                u += dt * rate;
                if (was < PlaceU && u >= PlaceU) Place(fumes);
                if (was < 0.55f && u >= 0.55f && fumes && fume && Random.value < 0.85f) Fx.Steam(fume.position, low ? 0.11f : 0.14f);
                if (u >= 1f)
                {
                    u -= 1f;
                    Indexed();
                    if (!working) { running = false; u = 0f; }
                }
            }
            Pose();

            // The reel turns with the line; the fan with the power.
            if (reel && running)
            {
                reelAngle = Mathf.Repeat(reelAngle + 40f * rate * dt, 360f);
                reel.localRotation = reelRest * Quaternion.Euler(0f, 0f, reelAngle);
            }
            if (fan)
            {
                float want = !complete ? 0f : low ? 45f : 90f;
                fanRpm = Mathf.MoveTowards(fanRpm, want, dt * 60f);
                fanAngle = Mathf.Repeat(fanAngle + fanRpm * 6f * dt, 360f);
                fan.localRotation = fanRest * Quaternion.Euler(0f, fanAngle, 0f);
            }

            // What ran out shows while it's starved: the copper on the reel runs down to the hub, the blanks are gone.
            bool starved = complete && !working;
            bool noCopper = starved && Have(team, "copper") <= 0.001f, noSteel = starved && Have(team, "steel") <= 0.001f;
            wind = Mathf.MoveTowards(wind, noCopper ? 0.4f : 1f, dt * 0.6f);
            if (winding) winding.localScale = new Vector3(windingRest.x * wind, windingRest.y * wind, windingRest.z);
            if (blanks && blanks.gameObject.activeSelf == noSteel) blanks.gameObject.SetActive(!noSteel);

            // The head pops every Batch boards.
            if (popT >= 0f) Pop(dt);

            // Glow is state.
            float now = Time.time;
            if (!complete) Lamps(0.05f, 0.05f, 0.05f, dt, true);
            else if (working)
                Lamps(low ? 0.5f : 1f, low && Mathf.Repeat(now * 2f + phase, 1f) >= 0.5f ? 0.12f : 1f, 0.12f, dt, false);
            else
                Lamps(0.22f, 0.12f, Mathf.Repeat(now * 0.9f + phase, 1f) < 0.65f ? 1f : 0.2f, dt, false);
        }

        static float Have(Team t, string item) => (t.Stock.TryGetValue(item, out var v) ? v : 0f) - t.Reserved(item);

        /// <summary>The chip goes down on the board under the carriage.</summary>
        void Place(bool fx)
        {
            if (fx && held) Fx.MuzzleFlash(held.position, 0.02f); // a solder glint at the nozzle
        }

        /// <summary>The belt has indexed one slot: the last board is in the stem.</summary>
        void Indexed()
        {
            shift = (shift + 1) % Slots;
            stacked++;
            if (stacked >= Batch && popT < 0f) { popT = 0f; dispensed = false; }
        }

        int SlotOf(int board) => (board + shift) % Slots;

        void Pose()
        {
            // Carriage: pick at the feeder (0-0.15), out over the board (0.15-0.33), place (0.33-0.53), home (0.53-0.7).
            if (gantry)
            {
                float go = u < 0.15f ? 0f : u < 0.33f ? Mathf.SmoothStep(0f, 1f, (u - 0.15f) / 0.18f)
                         : u < 0.53f ? 1f : u < 0.7f ? 1f - Mathf.SmoothStep(0f, 1f, (u - 0.53f) / 0.17f) : 0f;
                var p = gantryHome; p.z = Mathf.Lerp(gantryHome.z, placeZ, go);
                gantry.localPosition = p;
            }
            if (nozzle)
            {
                float dip = 0f;
                if (u < 0.15f) dip = pickDip * Mathf.Sin(Mathf.PI * u / 0.15f);
                else if (u >= 0.35f && u < 0.51f) dip = placeDip * Mathf.Sin(Mathf.PI * (u - 0.35f) / 0.16f);
                nozzle.localPosition = nozzleRest + Vector3.down * dip;
            }
            if (held)
            {
                bool carrying = running && u >= 0.075f && u < PlaceU;
                if (held.gameObject.activeSelf != carrying) held.gameObject.SetActive(carrying);
            }
            // Boards: at rest in their slots; from IndexU the belt moves each on one slot, the one in the stem comes back
            // as a blank dropping out of the magazine into slot 0.
            float k = u < IndexU ? 0f : Mathf.SmoothStep(0f, 1f, (u - IndexU) / (1f - IndexU));
            for (int i = 0; i < boards.Length; i++)
            {
                int s = SlotOf(i);
                bool show; Vector3 p;
                if (s < Slots - 1) { p = Vector3.Lerp(slots[s], slots[s + 1], k); show = true; }
                else { p = slots[0] + Vector3.up * 0.09f * (1f - k); show = k > 0.35f; } // in the stem; then the next blank
                boards[i].localPosition = p;
                if (boards[i].gameObject.activeSelf != show) boards[i].gameObject.SetActive(show);
                if (i < chips.Length)
                {
                    bool chip = s < Slots - 1 && (s >= 2 || (s == 1 && u >= PlaceU));
                    if (chips[i].gameObject.activeSelf != chip) chips[i].gameObject.SetActive(chip);
                }
            }
            // The stack behind the window; while the head pops it empties from the top.
            int shown = stacked;
            if (popT >= 0.08f && !dispensed) shown = Mathf.Max(0, Mathf.Min(stacked, Batch) - 1 - (int)((popT - 0.08f) / 0.035f));
            for (int i = 0; i < mags.Length; i++)
            {
                bool m = i < shown;
                if (mags[i].gameObject.activeSelf != m) mags[i].gameObject.SetActive(m);
            }
        }

        /// <summary>The dispenser pops: the head snaps open, the stack goes up and out, one board flips away over the lip,
        /// and the head drops shut with a bounce.</summary>
        void Pop(float dt)
        {
            popT += dt;
            float t = popT;
            if (head)
            {
                float a;
                if (t < 0.1f) a = Mathf.SmoothStep(0f, 1f, t / 0.1f);
                else if (t < 0.4f) a = 1f;
                else { float v = t - 0.4f; a = Mathf.Abs(Mathf.Cos(v * 7f)) * Mathf.Exp(-v * 5f); }
                head.localRotation = headRest * Quaternion.Euler(40f * a, 0f, 0f);
            }
            if (t >= 0.08f + 0.035f * Batch && !dispensed) { stacked = Mathf.Max(0, stacked - Batch); dispensed = true; }
            if (candy)
            {
                float f = (t - 0.1f) / 0.8f;
                bool fly = f > 0f && f < 1f;
                if (candy.gameObject.activeSelf != fly) candy.gameObject.SetActive(fly);
                if (fly)
                {
                    candy.localPosition = candyRest + candyOut * (0.3f * f) + Vector3.up * (0.45f * Mathf.Sin(Mathf.PI * f * 0.8f) - 0.1f * f);
                    candy.localRotation = candyRot * Quaternion.Euler(-260f * f, 120f * f, 0f);
                    candy.localScale = Vector3.one * (f < 0.65f ? 1f : Mathf.Lerp(1f, 0f, (f - 0.65f) / 0.35f));
                }
            }
            if (t > 1.5f)
            {
                if (head) head.localRotation = headRest;
                popT = -1f;
                if (stacked >= Batch) { popT = 0f; dispensed = false; } // a batch filled while it was popping
            }
        }

        /// <summary>Panes (eased over 0.4 s, or set at once), the run lamp and the wait lamp.</summary>
        void Lamps(float pane, float run, float wait, float dt, bool snap)
        {
            paneK = snap || paneK < 0f ? pane : Mathf.MoveTowards(paneK, pane, dt / 0.4f);
            if (Mathf.Abs(paneK - shownPane) > 0.005f) { foreach (var r in panes) Light(r, paneK); shownPane = paneK; }
            if (run != shownRun) { Light(runLamp, run); shownRun = run; }
            if (wait != shownWait) { Light(waitLamp, wait); shownWait = wait; }
        }

        // The lamp body darkens with its light, as WorldView's power glow does.
        static void Light(Renderer r, float g) => PezShade.Set(r, Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, g)), g);
    }
}
