using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Effects test bench, off unless the app is started with -fxdemo. Plays every explosion recipe in turn at the
    /// centre of the main camera's view, one every 4 s, so a capture sees each one from flash to settled debris.
    /// "-fxdemo storm" instead sets off ten random explosions a second around the view centre (a frame-rate check).
    /// </summary>
    public class FxDemo : MonoBehaviour
    {
        bool storm;
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

        void Update()
        {
            if (Time.time < next) return;
            var c = Focus();
            var team = teams[step & 1];
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
    }
}
