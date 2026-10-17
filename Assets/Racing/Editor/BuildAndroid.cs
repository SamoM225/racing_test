using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Racing.EditorTools
{
    /// <summary>
    /// APK to install on a phone (sideload, debug-signed): portrait, IL2CPP ARM64 (new phones have no 32-bit support).
    /// Output Builds/RCRacing.apk, or the path in env var RACING_APK (batch builds).
    /// </summary>
    public static class BuildAndroid
    {
        /// <summary>
        /// The game makes its materials at runtime, so nothing in the scene references their shaders and a player build
        /// strips them (magenta screen). These two Resources materials carry Standard and the terrain shader into every build.
        /// </summary>
        [MenuItem("Racing/Create Build Materials")]
        public static void EnsureMaterials()
        {
            Make("Assets/Racing/Resources/RacingLit.mat", "Standard");
            Make("Assets/Racing/Resources/RacingTerrain.mat", "Nature/Terrain/Standard");
            AssetDatabase.SaveAssets();

            static void Make(string path, string shader)
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(path) == null)
                    AssetDatabase.CreateAsset(new Material(Shader.Find(shader)), path);
            }
        }

        [MenuItem("Racing/Build Android APK")]
        public static void Build()
        {
            EnsureMaterials();
            PlayerSettings.productName = "RC Racing";
            if (PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android).StartsWith("com.DefaultCompany") ||
                string.IsNullOrEmpty(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)))
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.racingtest.rcracing");
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.buildAppBundle = false; // .apk, not the Play Store .aab

            string path = System.Environment.GetEnvironmentVariable("RACING_APK");
            if (string.IsNullOrEmpty(path)) path = "Builds/RCRacing.apk";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Race.unity" },
                locationPathName = path,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            bool ok = report.summary.result == BuildResult.Succeeded;
            if (ok) Debug.Log($"RACING APK BUILT {path} ({report.summary.totalSize / (1024f * 1024f):0.0} MB, {report.summary.totalTime.TotalMinutes:0.0} min)");
            else Debug.LogError($"RACING APK FAILED: {report.summary.result}, {report.summary.totalErrors} errors");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
            else if (ok) EditorUtility.RevealInFinder(path);
        }
    }
}
