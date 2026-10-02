using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pez.EditorTools
{
    /// <summary>
    /// One-shot project setup and build, usable from the menu or batchmode:
    ///   Unity -batchmode -projectPath unity -executeMethod Pez.EditorTools.PezSetup.Build -quit
    /// </summary>
    public static class PezSetup
    {
        const string MatDir = "Assets/Pez/Resources/PezMaterials";
        const string ScenePath = "Assets/Pez/Main.unity";

        [MenuItem("Pez/Setup Project")]
        public static void Setup()
        {
            Directory.CreateDirectory(MatDir);
            // Materials in Resources guarantee their shaders and keyword variants ship in builds.
            MakeMat("Lit", "Standard", false);
            MakeMat("LitEmissive", "Standard", true);
            MakeMat("Terrain", "Pez/VertexColorLit", false);
            MakeMat("Unlit", "Pez/Unlit", false);
            MakeMat("Water", "Pez/Water", false);

            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                // Fog is enabled at runtime; enabling it in the scene keeps fog shader variants from being stripped.
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            PlayerSettings.productName = "Pez";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, "com.pez.rts");
            PlayerSettings.companyName = "Pez";
            PlayerSettings.runInBackground = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1728;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.macRetinaSupport = true;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Pez setup complete");
        }

        static void MakeMat(string name, string shaderName, bool emission)
        {
            var path = $"{MatDir}/{name}.mat";
            var shader = Shader.Find(shaderName);
            if (shader == null) { Debug.LogError($"Shader {shaderName} not found"); return; }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            mat.shader = shader;
            mat.enableInstancing = true;
            if (emission) { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", Color.white); mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None; }
            EditorUtility.SetDirty(mat);
        }

        static string OutPath()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-pezOut");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : "Build/Pez.app";
        }

        /// <summary>Batchmode check that the art pack imported: node names, materials and shaders per model.</summary>
        [MenuItem("Pez/Verify Models")]
        public static void VerifyModels()
        {
            var models = Resources.LoadAll<GameObject>("PezModels");
            Debug.Log($"PEZVERIFY {models.Length} models");
            foreach (var m in models)
            {
                var nodes = string.Join(",", m.GetComponentsInChildren<Transform>(true).Select(t => t.name)
                    .Where(n => n == "turret" || n == "barrel" || n == "spinner" || n == "bin" || n == "bin_ore" || n == "door" || n == "lift" || n.StartsWith("stage_") || n.StartsWith("cluster_")));
                var mats = m.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(x => x != null).Select(x => $"{x.name}[{x.shader.name}]").Distinct();
                var b = new Bounds();
                foreach (var r in m.GetComponentsInChildren<Renderer>(true)) b.Encapsulate(r.bounds);
                Debug.Log($"PEZVERIFY {m.name}: nodes={nodes} bounds={b.min}..{b.max} mats={string.Join(" ", mats)}");
            }
            if (Application.isBatchMode) EditorApplication.Exit(models.Length > 0 ? 0 : 1);
        }

        [MenuItem("Pez/Build macOS")]
        public static void Build()
        {
            Setup();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                // -pezOut lets you build beside a running copy without clobbering it.
                locationPathName = OutPath(),
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None,
            });
            Debug.Log($"Pez build: {report.summary.result}, {report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
