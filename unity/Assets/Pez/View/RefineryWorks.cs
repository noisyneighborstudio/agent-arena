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

        static Material iron, copper;

        public int Stacks => stacks.Length;
        public Vector3 Mouth(int i) => stacks[i].position;

        RefineryWorks(Transform model, Transform a, Transform b, Transform[] stacks)
        {
            this.stacks = stacks;
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
            return new RefineryWorks(t, PezMotion.FindDeep(t, "belt_in"), PezMotion.FindDeep(t, "belt_out"), stacks.ToArray());
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
