using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The mining refinery at work (MOTION.md mining_refinery). While it refines, lumps of ore ride the belt over the dock
    /// from the intake hood (the model's belt_in) to the smelter hood (belt_out), flattened as they pass under the crusher
    /// roll (the model's spinner, which PezMotion turns while it works). Each lump is iron or copper ore, from what the team
    /// has waiting. When the refinery runs out of ore the belt runs on until it's empty, then stops. Half speed on low
    /// power. The smokestacks' mouths (stack_0, stack_1) are where WorldView's stack smoke leaves from.
    /// A model without the belt markers gets an inert works: no lumps, no stacks.
    /// The wall hatches (hatch_0, hatch_1, ...: roll-up shutters in the east, west and north walls) are this class's too: when
    /// terrain moves the truck bay to one of those walls, the dock's chute (DockView) comes out through it. The shutter facing
    /// the bay rolls up (0.3 s) before the chute is let out and stays up while the chute is out or on its way, then rolls
    /// down (0.4 s) once it is back inside, so the chute never passes through a closed shutter. DockView holds the chute
    /// until Open says the hatch is up.
    /// The south bay has a shutter of its own (bay_shutter, the same cream roll-up): it is rolled up (so the bay is as it always
    /// was) while the truck bay is the south one and shut whenever the bay is on a side wall, so the dock nobody is using reads
    /// as closed, not as an open dark doorway. It follows a bay that changes side the way the hatches do: it holds up while the
    /// chute is still out, rolls down once it is back, and rolls up again before the chute uses the south bay.
    /// </summary>
    public class RefineryWorks
    {
        const int Count = 8;
        const float Speed = 0.5f;   // tiles a second along the belt
        const float Size = 0.1f;

        readonly Transform belt;    // holds the lumps: hidden in a render whose team can't see the refinery's state
        readonly Transform[] lumps, stacks;
        readonly Vector3 from, to;
        readonly bool[] riding;
        readonly float[] last;
        readonly float rate;        // belt fraction a second
        float phase;
        bool shown = true;

        readonly Transform[] hatches;
        readonly Vector3[] hatchOut, hatchRest;   // each hatch's outward direction (model space) and rest scale
        readonly float[] hatchK;                   // 0 shut .. 1 rolled up
        readonly Transform bayShutter;             // the south bay's shutter: rolled up in the model (a point), scaled down to shut it
        float bayK = 1f;                           // 0 shut .. 1 rolled up
        bool dockHooked;

        static Material iron, copper;

        public int Stacks => stacks.Length;
        public Vector3 Mouth(int i) => stacks[i].position;

        RefineryWorks(Transform model, Transform a, Transform b, Transform[] stacks, Transform[] hatches)
        {
            this.stacks = stacks;
            this.hatches = hatches;
            bayShutter = PezMotion.FindDeep(model, "bay_shutter");
            hatchK = new float[hatches.Length]; hatchOut = new Vector3[hatches.Length]; hatchRest = new Vector3[hatches.Length];
            for (int i = 0; i < hatches.Length; i++)
            {
                var p = model.InverseTransformPoint(hatches[i].position); p.y = 0f;
                hatchOut[i] = Mathf.Abs(p.x) > Mathf.Abs(p.z) ? new Vector3(Mathf.Sign(p.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(p.z));
                hatchRest[i] = hatches[i].localScale;
            }
            lumps = new Transform[a != null && b != null ? Count : 0];
            riding = new bool[lumps.Length]; last = new float[lumps.Length];
            if (lumps.Length == 0) return;
            from = model.InverseTransformPoint(a.position) + Vector3.up * Size * 0.5f;
            to = model.InverseTransformPoint(b.position) + Vector3.up * Size * 0.5f;
            rate = Speed / Mathf.Max(0.1f, Vector3.Distance(from, to));
            if (iron == null) iron = Mats.Lit(PezPalette.OreIronOre, 0.3f, 0f);
            if (copper == null) copper = Mats.Lit(PezPalette.OreCopperOre, 0.45f, 0.3f);
            belt = new GameObject("ore_belt").transform;
            belt.SetParent(model, false);
            for (int i = 0; i < Count; i++)
            {
                // Chunks, not dice: each sits at its own slant.
                lumps[i] = Models.Part(belt, PrimitiveType.Cube, from, Vector3.zero, iron, new Vector3(Random.Range(-20f, 20f), Random.Range(0f, 90f), Random.Range(-20f, 20f)));
                lumps[i].gameObject.SetActive(false);
                last[i] = (float)i / Count; // none riding yet: each goes on at the intake as its slot comes round
            }
        }

        public static RefineryWorks For(GameObject model)
        {
            var t = model.transform;
            var stacks = new System.Collections.Generic.List<Transform>();
            for (Transform s; (s = PezMotion.FindDeep(t, "stack_" + stacks.Count)) != null; ) stacks.Add(s);
            var hatches = new System.Collections.Generic.List<Transform>();
            for (Transform h; (h = PezMotion.FindDeep(t, "hatch_" + hatches.Count)) != null; ) hatches.Add(h);
            return new RefineryWorks(t, PezMotion.FindDeep(t, "belt_in"), PezMotion.FindDeep(t, "belt_out"), stacks.ToArray(), hatches.ToArray());
        }

        /// <summary>How far the hatch in the wall toward `dir` (model space) is rolled up, 0 to 1; 1 when there is none.</summary>
        public float Open(Vector3 dir)
        {
            if (bayShutter != null && Vector3.Dot(Vector3.back, dir) > 0.9f) return bayK;
            for (int i = 0; i < hatches.Length; i++)
                if (Vector3.Dot(hatchOut[i], dir) > 0.9f) return hatchK[i];
            return 1f;
        }

        /// <summary>Per frame, with the refinery's dock: rolls the shutter in the bay's wall up while the chute is wanted out or
        /// still out, and down again once it's back inside.</summary>
        public void Hatches(DockView dock, float dt)
        {
            if (hatches.Length == 0 && bayShutter == null) return;
            if (dock != null && !dockHooked) { dock.Opening = Open; dockHooked = true; }
            for (int i = 0; i < hatches.Length; i++)
            {
                bool up = dock != null && dock.ChuteBusy && Vector3.Dot(hatchOut[i], dock.Dir) > 0.9f;
                hatchK[i] = Mathf.MoveTowards(hatchK[i], up ? 1f : 0f, dt / (up ? 0.3f : 0.4f));
                float k = hatchK[i] * hatchK[i] * (3f - 2f * hatchK[i]);
                var sc = hatchRest[i]; sc.y *= Mathf.Lerp(1f, 0.06f, k);   // rolls up into the lintel, from its top edge
                hatches[i].localScale = sc;
            }
            if (bayShutter != null)
            {
                // Up while the bay is the south one (and while the chute is still out of it, if the bay is moving away), else down.
                bool south = dock == null || Vector3.Dot(Vector3.back, dock.Dir) > 0.9f && (!dock.Switching || dock.ChuteBusy);
                bayK = Mathf.MoveTowards(bayK, south ? 1f : 0f, dt / (south ? 0.3f : 0.4f));
                float k = bayK * bayK * (3f - 2f * bayK);
                // Rolls up into its top edge, then is a point (the model's rest pose): nothing of it is left in the south bay.
                bayShutter.localScale = bayK >= 0.999f ? Vector3.one * 0.001f : new Vector3(1f, Mathf.Max(0.001f, Mathf.Lerp(1f, 0.04f, k)), 1f);
            }
        }

        /// <summary>A stream render whose team only remembers this refinery under fog shows an empty belt.</summary>
        public void Show(bool on)
        {
            if (belt == null || on == shown) return;
            belt.gameObject.SetActive(on); shown = on;
        }

        /// <summary>`working`: the sim says it's refining; `ironWaiting` / `copperWaiting`: what the next lump can be;
        /// `power`: 1, or 0.5 on low power.</summary>
        public void Tick(bool working, bool ironWaiting, bool copperWaiting, float power, float dt)
        {
            if (lumps.Length == 0) return;
            bool any = working;
            for (int i = 0; i < lumps.Length && !any; i++) any = riding[i];
            if (!any) return; // stopped and empty
            phase = Mathf.Repeat(phase + dt * rate * power, 1f);
            for (int i = 0; i < lumps.Length; i++)
            {
                float u = Mathf.Repeat(phase + (float)i / lumps.Length, 1f);
                if (u < last[i])
                {
                    // Round to the intake again: a new lump goes on only while there's ore to refine.
                    riding[i] = working;
                    if (riding[i]) lumps[i].GetComponent<Renderer>().sharedMaterial = copperWaiting && (!ironWaiting || Random.value < 0.5f) ? copper : iron;
                    if (lumps[i].gameObject.activeSelf != riding[i]) lumps[i].gameObject.SetActive(riding[i]);
                }
                last[i] = u;
                if (!riding[i]) continue;
                // Out from under the intake hood, then flattened under the crusher roll on the way into the smelter.
                float grow = Mathf.SmoothStep(0f, 1f, u / 0.08f), crush = Mathf.SmoothStep(0f, 1f, (u - 0.78f) / 0.13f);
                float s = Size * grow, h = s * Mathf.Lerp(1f, 0.25f, crush);
                var p = Vector3.Lerp(from, to, u);
                p.y += (h - Size) * 0.5f;
                lumps[i].localPosition = p;
                lumps[i].localScale = new Vector3(s * (1f + 0.3f * crush), h, s * (1f + 0.3f * crush));
            }
        }
    }
}
