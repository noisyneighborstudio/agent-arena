using System.IO;
using System.Linq;
using Pez.View;
using UnityEditor;
using UnityEngine;

namespace Pez.EditorTools
{
    /// <summary>
    /// Sidebar icons for units the art pack has no icon for (the ones modelled procedurally in Models.cs): the model as
    /// the game builds it, in team 0's colours, three-quarter view on the pack's dark background, 256x192 like the
    /// pack's renders. Only missing icons are made; the pack's own are never overwritten.
    ///   Unity -batchmode -projectPath unity -executeMethod Pez.EditorTools.PezIconRender.RenderMissing -quit
    /// </summary>
    public static class PezIconRender
    {
        const string Dir = "Assets/Pez/Resources/PezIcons";
        const int W = 256, H = 192;

        [MenuItem("Pez/Render Missing Icons")]
        public static void RenderMissing()
        {
            int made = 0;
            var keys = Pez.Sim.Defs.All.Keys.Where(k => !File.Exists($"{Dir}/{k}.png") && k != "mine").OrderBy(k => k).ToList();
            var root = new GameObject("IconStage");
            float shadowWas = QualitySettings.shadowDistance; // a project setting: put it back afterwards
            try
            {
                var camGo = new GameObject("IconCam"); camGo.transform.SetParent(root.transform);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.078f, 0.09f, 0.106f); // the pack's icon background
                cam.fieldOfView = 24f;
                cam.allowHDR = false;
                var sunGo = new GameObject("IconSun"); sunGo.transform.SetParent(root.transform);
                var sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional; sun.intensity = 1.25f; sun.shadows = LightShadows.Soft; sun.color = new Color(1f, 0.96f, 0.9f);
                sunGo.transform.rotation = Quaternion.Euler(52f, -30f, 0f);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.26f, 0.28f, 0.31f);
                RenderSettings.fog = false;
                // A ground the colour of the background, so the model's shadow lands on "nothing".
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.transform.SetParent(root.transform);
                ground.transform.localScale = Vector3.one * 4f;
                ground.GetComponent<Renderer>().sharedMaterial = Mats.Lit(new Color(0.014f, 0.016f, 0.02f), 0f, 0f);
                var rt = new RenderTexture(W, H, 24) { antiAliasing = 8 };
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                foreach (var key in keys)
                {
                    Rig rig;
                    // Units that borrow another's model until they have art (ModelAs) are drawn the way the game draws them.
                    var def = Pez.Sim.Defs.Get(key);
                    try { rig = Models.Build(def?.ModelKey ?? key, 0); }
                    catch (System.Exception ex) { Debug.LogWarning($"PEZICON {key}: no model ({ex.Message})"); continue; }
                    var go = rig.Root.gameObject;
                    go.transform.SetParent(root.transform, false);
                    // Aircraft sit at their flying height in game: bring them down to the stage.
                    rig.Root.position = Vector3.zero;
                    if (rig.Body != null) rig.Body.localPosition = new Vector3(0, rig.Altitude > 0 ? 0.35f : 0f, 0);
                    rig.Root.rotation = Quaternion.Euler(0, -35f, 0);
                    if (def?.ModelAs != null && rig.Model != null && def.ModelScale != 1f) rig.Model.transform.localScale *= def.ModelScale;
                    var b = new Bounds(rig.Root.position, Vector3.zero);
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                    // Three-quarter view from the front left, framed so the model fills about two thirds of the height.
                    float radius = Mathf.Max(0.35f, b.extents.magnitude);
                    float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.78f;
                    var dir = Quaternion.Euler(32f, 210f, 0f) * Vector3.forward;
                    camGo.transform.position = b.center - dir * dist;
                    camGo.transform.rotation = Quaternion.LookRotation(dir);
                    QualitySettings.shadowDistance = dist + radius * 4f;
                    // Three passes, so the shadow lands on the pack's flat background rather than on a visible floor:
                    // the floor with the model's shadow, the bare floor, and the model alone on the background.
                    var renderers = go.GetComponentsInChildren<Renderer>();
                    var floor = ground.GetComponent<Renderer>();
                    Color[] Shoot()
                    {
                        cam.targetTexture = rt; cam.Render();
                        RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
                        RenderTexture.active = null; cam.targetTexture = null;
                        return tex.GetPixels();
                    }
                    foreach (var r in renderers) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                    var shadowed = Shoot();
                    foreach (var r in renderers) r.enabled = false;
                    var bare = Shoot();
                    foreach (var r in renderers) { r.enabled = true; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; }
                    floor.enabled = false;
                    var model = Shoot();
                    floor.enabled = true;
                    var bg = cam.backgroundColor; // as stored in the (sRGB) target
                    var outPx = new Color[model.Length];
                    for (int i = 0; i < model.Length; i++)
                    {
                        var m = model[i];
                        float diff = Mathf.Abs(m.r - bg.r) + Mathf.Abs(m.g - bg.g) + Mathf.Abs(m.b - bg.b);
                        float shade = Mathf.Clamp01((shadowed[i].r + shadowed[i].g + shadowed[i].b) / Mathf.Max(0.001f, bare[i].r + bare[i].g + bare[i].b));
                        var under = bg * Mathf.Lerp(0.45f, 1f, shade);
                        outPx[i] = Color.Lerp(under, m, Mathf.Clamp01(diff / 0.06f));
                    }
                    tex.SetPixels(outPx); tex.Apply();
                    File.WriteAllBytes($"{Dir}/{key}.png", tex.EncodeToPNG());
                    Object.DestroyImmediate(go);
                    made++;
                    Debug.Log($"PEZICON {key}: rendered");
                }
                Object.DestroyImmediate(tex);
                rt.Release();
            }
            finally { Object.DestroyImmediate(root); QualitySettings.shadowDistance = shadowWas; }
            AssetDatabase.Refresh();
            // Import them like the pack's icons.
            var like = AssetImporter.GetAtPath($"{Dir}/gunship.png") as TextureImporter;
            if (like != null)
            {
                var settings = new TextureImporterSettings();
                like.ReadTextureSettings(settings);
                foreach (var key in keys)
                    if (AssetImporter.GetAtPath($"{Dir}/{key}.png") is TextureImporter ti)
                    {
                        ti.SetTextureSettings(settings);
                        ti.textureCompression = like.textureCompression;
                        ti.SaveAndReimport();
                    }
            }
            Debug.Log($"PEZICON done: {made} of {keys.Count} missing icons rendered");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
