using System.Reflection;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Live look tuning, off unless the app is started with -looktune PATH. Watches PATH (lines of "Name = value", or
    /// "Name = r, g, b" for colours; # comments) and sets the matching public static float/Color field on
    /// <see cref="PezPost"/> or <see cref="Look"/>, then reapplies the lighting. Also accepts "Shader.NAME = value" to set
    /// a global shader float. For tuning the look against a running game without rebuilding; never on in the arena.
    /// </summary>
    public class LookTune : MonoBehaviour
    {
        string path;
        System.DateTime stamp;

        public static void Start()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-looktune");
            if (i < 0 || i + 1 >= args.Length || Object.FindFirstObjectByType<LookTune>() != null) return;
            var go = new GameObject("LookTune");
            DontDestroyOnLoad(go);
            go.AddComponent<LookTune>().path = args[i + 1];
        }

        float next;
        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            if (!System.IO.File.Exists(path)) return;
            var t = System.IO.File.GetLastWriteTimeUtc(path);
            if (t == stamp) return;
            stamp = t;
            int n = 0;
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var line = raw.Split('#')[0].Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var name = line.Substring(0, eq).Trim();
                var vals = line.Substring(eq + 1).Split(',');
                var f = new float[vals.Length];
                bool ok = true;
                for (int k = 0; k < vals.Length; k++) ok &= float.TryParse(vals[k].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f[k]);
                if (!ok) continue;
                if (name.StartsWith("Shader.")) { Shader.SetGlobalFloat(name.Substring(7), f[0]); n++; continue; }
                foreach (var type in new[] { typeof(PezPost), typeof(Look) })
                {
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                    if (field == null || field.IsLiteral || field.IsInitOnly) continue;
                    if (field.FieldType == typeof(float)) { field.SetValue(null, f[0]); n++; }
                    else if (field.FieldType == typeof(Color) && f.Length >= 3) { field.SetValue(null, new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f)); n++; }
                }
            }
            Look.ApplyLighting();
            Debug.Log($"LookTune: applied {n} values from {path}");
        }
    }
}
