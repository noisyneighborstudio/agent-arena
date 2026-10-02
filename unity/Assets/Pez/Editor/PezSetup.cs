using System.IO;
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

        [MenuItem("Pez/Build macOS")]
        public static void Build()
        {
            Setup();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Build/Pez.app",
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None,
            });
            Debug.Log($"Pez build: {report.summary.result}, {report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
