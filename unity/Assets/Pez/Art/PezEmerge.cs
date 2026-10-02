// Emerge and remove transforms: nothing in Pez pops onto or off the map.
//  - Structures: SetBuildProgress(0..1) raises stage_0..stage_3 out of the ground, then unfolds functional nodes.
//  - Units: PlayExit(...) drives a new unit out through its producer's door, or presents it on the airfield lift.
//  - Ores: SetClusterAmount / Regrow scale clusters, sinking empty ones.
//  - Everything: PlayRemove() runs the reverse transform, then destroys the GameObject.
// Requires opaque terrain at y = 0 so geometry below ground is hidden while it rises.
using System.Collections;
using UnityEngine;

namespace Pez
{
    public class PezEmerge : MonoBehaviour
    {
        static readonly Vector2[] StageWindows = { new Vector2(0f, .10f), new Vector2(.10f, .45f), new Vector2(.45f, .75f), new Vector2(.75f, .95f) };
        static readonly string[] Functional = { "turret", "spinner", "door", "lift" };

        Transform[] stages = new Transform[4];
        float[] stageHeights = new float[4];
        Transform[] functional;
        Vector3[] functionalRest;

        void Awake()
        {
            for (int i = 0; i < 4; i++)
            {
                stages[i] = PezMotion.FindDeep(transform, "stage_" + i);
                if (stages[i]) stageHeights[i] = (i == 0 ? 0.15f : MaxHeight(stages[i])) + 0.02f;
            }
            functional = new Transform[Functional.Length];
            functionalRest = new Vector3[Functional.Length];
            for (int i = 0; i < Functional.Length; i++)
            {
                functional[i] = PezMotion.FindDeep(transform, Functional[i]);
                if (functional[i]) functionalRest[i] = functional[i].localPosition;
            }
        }

        float MaxHeight(Transform t)
        {
            float top = 0f;
            foreach (var r in t.GetComponentsInChildren<Renderer>())
                top = Mathf.Max(top, r.bounds.max.y - transform.position.y);
            return top;
        }

        static float EaseOutCubic(float k) => 1f - Mathf.Pow(1f - k, 3f);

        // ---------------------------------------------------------------- structures
        /// <summary>Call every frame while building. 0 = nothing above ground, 1 = complete.</summary>
        public void SetBuildProgress(float p)
        {
            p = Mathf.Clamp01(p);
            float shellOffset = 0f; bool shellSet = false;   // doors and lifts ride up with the first wall stage
            for (int i = 0; i < 4; i++)
            {
                if (!stages[i]) continue;
                var w = StageWindows[i];
                float k = Mathf.Clamp01((p - w.x) / (w.y - w.x));
                stages[i].localPosition = new Vector3(0f, -stageHeights[i] * (1f - EaseOutCubic(k)), 0f);
                if (i >= 1 && !shellSet) { shellOffset = stages[i].localPosition.y; shellSet = true; }
                float over = (i == 3 && k >= 1f) ? 1f + 0.04f * Mathf.Exp(-(p - w.y) * 60f) * Mathf.Cos((p - w.y) * 120f) : 1f;
                stages[i].localScale = new Vector3(1f, over, 1f);
            }
            float f = Mathf.Clamp01((p - .95f) / .05f);
            for (int i = 0; i < functional.Length; i++)
            {
                if (!functional[i]) continue;
                bool hidesInside = Functional[i] == "turret" || Functional[i] == "spinner";
                if (hidesInside) functional[i].localPosition = functionalRest[i] + Vector3.down * 0.3f * (1f - EaseOutCubic(f));
                else functional[i].localPosition = functionalRest[i] + Vector3.up * shellOffset;
                if (hidesInside) functional[i].localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, EaseOutCubic(f));
            }
            var motion = GetComponent<PezMotion>();
            if (motion) motion.enabled = p >= 1f;
        }

        public IEnumerator PlayBuild(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) { SetBuildProgress(t / seconds); yield return null; }
            SetBuildProgress(1f);
        }

        // ---------------------------------------------------------------- units
        /// <summary>Unit starts 0.6 inside the producer's door, door rolls up, unit drives out along the producer's -Z.</summary>
        public IEnumerator PlayExit(PezMotion producer, float exitDistance = 1.5f, float speed = 1.4f)
        {
            Transform door = PezMotion.FindDeep(producer.transform, "door");
            Vector3 fwd = -producer.transform.forward;                     // doors face -Z
            Vector3 doorPos = door ? new Vector3(door.position.x, producer.transform.position.y, door.position.z) : producer.transform.position;
            transform.SetPositionAndRotation(doorPos - fwd * 0.6f, Quaternion.LookRotation(fwd, Vector3.up));
            producer.SetDoorOpen(true);
            while (!producer.DoorIsOpen) yield return null;
            Vector3 end = doorPos + fwd * exitDistance;
            while ((transform.position - end).sqrMagnitude > 0.0004f)
            {
                transform.position = Vector3.MoveTowards(transform.position, end, speed * 0.6f * Time.deltaTime);
                yield return null;
            }
            yield return new WaitForSeconds(0.5f);
            producer.SetDoorOpen(false);
        }

        /// <summary>Aircraft rides the airfield lift up from -0.6, then climbs to cruise altitude.</summary>
        public IEnumerator PlayLift(PezMotion airfield, float cruiseAltitude, float spinupSeconds = 1f, float climbSeconds = 1.5f)
        {
            Transform lift = PezMotion.FindDeep(airfield.transform, "lift");
            airfield.SnapLiftDown();
            transform.SetParent(lift, false);
            transform.localPosition = Vector3.zero;
            airfield.SetLiftUp(true);
            yield return new WaitForSeconds(1.2f);
            transform.SetParent(null, true);
            var m = GetComponent<PezMotion>();
            if (m) m.SetWorking(true);
            yield return new WaitForSeconds(spinupSeconds);
            Vector3 a = transform.position, b = a + Vector3.up * cruiseAltitude;
            for (float t = 0f; t < climbSeconds; t += Time.deltaTime)
            {
                transform.position = Vector3.Lerp(a, b, EaseOutCubic(t / climbSeconds));
                yield return null;
            }
            transform.position = b;
            airfield.SetLiftUp(false);
        }

        /// <summary>outpost_truck → outpost, drill_rig → deep_mine. The carrier (this) sinks while the target's stages rise.
        /// `target` is already instantiated at the grid-aligned spot and starts at build progress 0.</summary>
        public IEnumerator PlayDeployInto(PezEmerge target, float riseSeconds = 1.8f)
        {
            var m = GetComponent<PezMotion>();
            if (m && PezMotion.FindDeep(transform, "mast"))
            {
                m.SetMastRaised(true);
                while (!m.MastRaised) yield return null;
                yield return new WaitForSeconds(0.4f);   // bit bites
            }
            target.SetBuildProgress(0f);
            Vector3 start = transform.position;
            for (float t = 0f; t < riseSeconds; t += Time.deltaTime)
            {
                float k = t / riseSeconds;
                transform.position = start + Vector3.down * 0.6f * Mathf.Clamp01(k / 0.55f);   // chassis sinks over ~1 s
                target.SetBuildProgress(k);
                yield return null;
            }
            target.SetBuildProgress(1f);
            Destroy(gameObject);
        }

        // ---------------------------------------------------------------- ores
        public void SetClusterAmount(int cluster, float fraction)
        {
            var c = PezMotion.FindDeep(transform, "cluster_" + cluster);
            if (!c) return;
            float s = fraction <= 0f ? 0f : Mathf.Max(0.25f, fraction);
            if (s > 0f) { c.localScale = Vector3.one * s; c.gameObject.SetActive(true); }
            else StartCoroutine(SinkCluster(c));
        }

        IEnumerator SinkCluster(Transform c)
        {
            Vector3 p = c.localPosition;
            for (float t = 0f; t < 1f; t += Time.deltaTime) { c.localPosition = p + Vector3.down * 0.2f * t; yield return null; }
            c.gameObject.SetActive(false);
            c.localPosition = p;
        }

        public IEnumerator Regrow(int cluster, float seconds = 6f)
        {
            var c = PezMotion.FindDeep(transform, "cluster_" + cluster);
            if (!c) yield break;
            Vector3 p = c.localPosition;
            c.gameObject.SetActive(true);
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float k = EaseOutCubic(t / seconds);
                c.localScale = Vector3.one * Mathf.Lerp(0.1f, 1f, k);
                c.localPosition = p + Vector3.down * 0.2f * (1f - k);
                yield return null;
            }
            c.localScale = Vector3.one; c.localPosition = p;
        }

        // ---------------------------------------------------------------- removal
        /// <summary>Destroyed or sold. Structures sink stage by stage; units sink and darken. Destroys the GameObject at the end.</summary>
        public IEnumerator PlayRemove(bool destroyed, float seconds = 1.5f)
        {
            var m = GetComponent<PezMotion>();
            if (m) m.enabled = false;
            Vector3 origin = transform.position;
            if (stages[0] || stages[1])
            {
                if (destroyed)
                    for (float t = 0f; t < .4f; t += Time.deltaTime) { transform.position = origin + (Vector3)Random.insideUnitCircle * .03f; yield return null; }
                transform.position = origin;
                for (float t = 0f; t < seconds; t += Time.deltaTime) { SetBuildProgress(1f - t / seconds); yield return null; }
                SetBuildProgress(0f);
            }
            else
            {
                Transform turret = PezMotion.FindDeep(transform, "turret");
                if (destroyed && turret) StartCoroutine(Fling(turret));
                for (float t = 0f; t < .5f; t += Time.deltaTime) { transform.position = origin + Vector3.down * .06f * (t / .5f); yield return null; }
                if (destroyed) yield return new WaitForSeconds(20f);
                Vector3 p = transform.position;
                for (float t = 0f; t < 2f; t += Time.deltaTime) { transform.position = p + Vector3.down * 0.6f * (t / 2f); yield return null; }
            }
            Destroy(gameObject);
        }

        IEnumerator Fling(Transform head)
        {
            head.SetParent(null, true);
            Vector3 v = new Vector3(Random.Range(-.8f, .8f), 2.5f, Random.Range(-.8f, .8f)), p = head.position;
            for (float t = 0f; t < .6f; t += Time.deltaTime)
            {
                p += v * Time.deltaTime; v += Physics.gravity * Time.deltaTime;
                head.position = p; head.Rotate(Vector3.right, 540f * Time.deltaTime);
                head.localScale = Vector3.one * (1f - t / .6f);
                yield return null;
            }
            Destroy(head.gameObject);
        }
    }
}
