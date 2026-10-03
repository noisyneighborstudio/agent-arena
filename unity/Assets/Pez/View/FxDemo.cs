using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Effects test bench, off unless the app is started with -fxdemo. Plays every explosion recipe in turn at the
    /// centre of the main camera's view, one every 4 s, so a capture sees each one from flash to settled debris.
    /// "-fxdemo storm" instead sets off ten random explosions a second around the view centre (a frame-rate check).
    /// "-fxdemo wrecks" stages vehicle deaths (art review fix 1): a row of live light tank, heavy tank and artillery
    /// models at the view centre, and every 8 s one of a second row is destroyed beside them, so a capture can compare a
    /// live hull with a fresh wreck and one that has lain a while. "-fxdemo fires" stages building fires (FireSim) at every
    /// stage, one of them fought by a repair beam. View-only: nothing here touches the sim.
    /// </summary>
    public class FxDemo : MonoBehaviour
    {
        bool storm, wrecks;
        float next = 3f;
        int step;
        static readonly Color[] teams = { new Color32(46, 115, 255, 255), new Color32(242, 46, 31, 255) };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "-fxdemo")
                {
                    var d = new GameObject("FxDemo").AddComponent<FxDemo>();
                    d.storm = i + 1 < args.Length && args[i + 1] == "storm";
                    d.wrecks = i + 1 < args.Length && args[i + 1] == "wrecks";
                    d.fires = i + 1 < args.Length && args[i + 1] == "fires";
                    DontDestroyOnLoad(d.gameObject);
                }
        }

        static Vector3 Focus()
        {
            var cam = Camera.main;
            if (cam == null) return Vector3.zero;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            return new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float t) ? ray.GetPoint(t) : Vector3.zero;
        }

        // "-fxdemo fires": real buildings at the view centre, each burning at a stage (FireSim; BuildingFire where the
        // fluid fire can't run): a barracks smouldering, a factory burning, a command center raging, and a power plant
        // raging that a repair beam fights every other 8 s (black smoke, then white steam as it's knocked down).
        bool fires;
        static readonly (string key, float smoke, float fire, bool fought)[] fireBench =
        {
            ("barracks", 1f, 0f, false), ("factory", 1f, 0.4f, false), ("command_center", 1f, 1f, false), ("power_plant", 1f, 1f, true),
        };
        Rig[] fireRigs;
        FireSim[] fireSims;
        FireShade[] fireShades;
        BuildingFire[] fireStates;
        Vector3 fireAt;
        float fireBeamT;

        void Fires(Vector3 c)
        {
            // Follow the camera: if the view moved away, rebuild the bench at the new view centre.
            if (fireRigs != null && (fireAt - c).magnitude > 8f)
            {
                for (int b = 0; b < fireRigs.Length; b++) { fireSims[b]?.Release(); Destroy(fireRigs[b].Root.gameObject); }
                fireRigs = null;
            }
            if (fireRigs == null)
            {
                fireAt = c;
                int n = fireBench.Length;
                fireRigs = new Rig[n]; fireSims = new FireSim[n]; fireStates = new BuildingFire[n]; fireShades = new FireShade[n];
                for (int b = 0; b < n; b++)
                {
                    var rig = Models.Build(fireBench[b].key, b & 1);
                    rig.Root.position = new Vector3(Mathf.Round(c.x) + (b - (n - 1) * 0.5f) * 4f, 0f, Mathf.Round(c.z));
                    rig.Emerge?.SetBuildProgress(1f);
                    fireRigs[b] = rig;
                    fireStates[b] = new BuildingFire();
                }
                return; // voxelize next frame, once the transforms have settled
            }
            float now = Time.time;
            for (int b = 0; b < fireRigs.Length; b++)
            {
                var (key, smoke, fire, fought) = fireBench[b];
                var rig = fireRigs[b];
                if (fireSims[b] == null && rig.HasModel) { fireSims[b] = FireSim.Create(rig.Model, b + 1); fireShades[b] = new FireShade(rig.Model); }
                bool beam = fought && ((int)(now / 8f) & 1) == 1;
                var bounds = new Bounds(rig.Root.position, Vector3.zero);
                foreach (var r in rig.Model.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);
                var hit = new Vector3(bounds.center.x, bounds.max.y * 0.8f, bounds.min.z);
                if (beam && (fireBeamT -= Time.deltaTime) <= 0f)
                {
                    // As WorldView draws a repair: the caramel welding beam from a truck's spot in front, sparks at the work.
                    fireBeamT = 0.3f;
                    var from = new Vector3(bounds.center.x - 1.2f, 0.45f, bounds.min.z - 1.6f);
                    Fx.Beam(from, hit, new Color(Mats.Amber.r, Mats.Amber.g, Mats.Amber.b, 0.9f), 0.035f);
                    Fx.MuzzleFlash(hit, 0.05f);
                }
                fireShades[b]?.Set(fire * (beam ? 0.4f : 1f), 1f - 0.5f * fire);
                if (fireSims[b] != null) fireSims[b].Tick(smoke, fire, beam ? 1f : 0f, hit, null);
                else
                {
                    var spots = new[] { bounds.center + Vector3.up * bounds.extents.y * 0.85f, bounds.center + new Vector3(0.4f, bounds.extents.y * 0.8f, 0.3f), bounds.center + new Vector3(-0.4f, bounds.extents.y * 0.8f, -0.3f) };
                    fireStates[b].Tick(spots, bounds.center, 1.4f, smoke, fire, b + 1);
                }
            }
        }

        void Update()
        {
            if (fires) { Fires(Focus()); return; }
            if (Time.time < next) return;
            var c = Focus();
            var team = teams[step & 1];
            if (wrecks) { Wrecks(c); return; }
            if (storm)
            {
                next = Time.time + 0.1f;
                var p = c + new Vector3(Random.Range(-8f, 8f), 0f, Random.Range(-8f, 8f));
                switch (step++ % 6)
                {
                    case 0: Fx.VehicleDestroyed(p + Vector3.up * 0.3f, team); break;
                    case 1: Fx.Hit("artillery", p); break;
                    case 2: Fx.Hit("rocket", p + Vector3.up * 0.3f); break;
                    case 3: Fx.InfantryDeath(p, team); break;
                    case 4: Fx.Hit("heavy_cannon", p + Vector3.up * 0.3f); break;
                    default: if (Random.value < 0.15f) Fx.BuildingDestroyed(p, 2, team); else Fx.BulletImpact(p + Vector3.up * 0.3f); break;
                }
                return;
            }
            next = Time.time + 4f;
            switch (step++ % 9)
            {
                case 0: Fx.Hit("artillery", c); break;
                case 1: Fx.VehicleDestroyed(c + Vector3.up * 0.3f, team); break;
                case 2: Fx.BuildingDestroyed(c, 3, team); next += 4f; break;
                case 3:
                    {
                        // A stand-in airframe so the fall and the impact can be seen.
                        var hulk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        Destroy(hulk.GetComponent<Collider>());
                        hulk.transform.localScale = new Vector3(0.9f, 0.18f, 0.6f);
                        hulk.GetComponent<Renderer>().sharedMaterial = Mats.Lit(team);
                        var from = c + new Vector3(-2f, 3f, -1f);
                        hulk.transform.position = from;
                        Fx.AircraftDestroyed(hulk.transform, from, new Vector3(1.6f, 0f, 0.8f), team);
                        break;
                    }
                case 4: for (int i = 0; i < 3; i++) Fx.InfantryDeath(c + new Vector3(i * 0.6f - 0.6f, 0f, 0f), team); break;
                case 5: Fx.Mine(c); break;
                case 6: Fx.Hit("rocket", c + Vector3.up * 0.4f); for (int i = 0; i < 6; i++) Fx.BulletImpact(c + Random.insideUnitSphere * 0.6f + Vector3.up * 0.4f); break;
                case 7: Fx.Salvaged(c, 2f); break;
                default: Fx.Hit("bombs", c); break;
            }
        }

        static readonly string[] wreckKinds = { "light_tank", "heavy_tank", "artillery" };
        Vector3 wreckAt;
        bool wreckRow;

        void Wrecks(Vector3 c)
        {
            next = Time.time + 8f;
            if (!wreckRow)
            {
                // The live row, for comparison: one of each, team colours alternating.
                wreckRow = true; wreckAt = new Vector3(Mathf.Round(c.x), 0f, Mathf.Round(c.z));
                for (int i = 0; i < wreckKinds.Length; i++)
                    Models.Build(wreckKinds[i], i & 1).Root.SetPositionAndRotation(wreckAt + new Vector3(i * 1.4f - 1.4f, 0f, 1.2f), Quaternion.Euler(0, 200f, 0));
            }
            int k = step++ % wreckKinds.Length;
            var rig = Models.Build(wreckKinds[k], k & 1);
            var pos = wreckAt + new Vector3(k * 1.4f - 1.4f, 0f, -0.6f);
            rig.Root.SetPositionAndRotation(pos, Quaternion.Euler(0, 200f, 0));
            // As WorldView.Retire does for a destroyed vehicle: the model leaves its rig and plays its removal.
            rig.Model.transform.SetParent(null, true);
            rig.Emerge.StartCoroutine(rig.Emerge.PlayRemove(true));
            Destroy(rig.Root.gameObject);
            Fx.VehicleDestroyed(pos + Vector3.up * 0.3f, Mats.Team(k & 1));
        }
    }
}
