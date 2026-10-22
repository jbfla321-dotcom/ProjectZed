// Put this file in Assets/Editor/BuildScript.cs (create the Editor folder if needed).
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildScript
{
    const string ScenePath = "Assets/Scenes/Main.unity";
    const string OutputPath = "Builds/Windows/SpaceShooter3D.exe";

    // Called from the command line: -executeMethod BuildScript.Build
    public static void Build()
    {
        // The game builds itself at runtime, so the scene only needs one object with the script.
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("Game");
        go.AddComponent<SpaceShooter3D>();
        EditorSceneManager.SaveScene(scene, ScenePath);

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log("Build succeeded: " + summary.outputPath + " (" + summary.totalSize / (1024 * 1024) + " MB)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("Build failed: " + summary.result + " with " + summary.totalErrors + " error(s)");
            EditorApplication.Exit(1);
        }
    }
}
