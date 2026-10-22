using System.IO;
using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;

// Tools > Multiplayer > Setup: (re)builds the two prefabs NetSession loads from Resources/Multiplayer:
// NetPlayer (one per connected player) and NetworkManager (Unity Transport; the host's scene is shared as is,
// so Netcode's own scene loading is off).
public static class MultiplayerSetup
{
    const string Folder = "Assets/Resources/Multiplayer";

    [MenuItem("Tools/Multiplayer/Setup")]
    public static void Setup()
    {
        Directory.CreateDirectory(Folder);

        var playerGo = new GameObject("NetPlayer");
        playerGo.AddComponent<NetworkObject>();
        playerGo.AddComponent<NetPlayer>();
        var playerPrefab = PrefabUtility.SaveAsPrefabAsset(playerGo, Folder + "/NetPlayer.prefab");
        Object.DestroyImmediate(playerGo);
        // Netcode gives a prefab its network id in OnValidate, which doesn't run for one made from code.
        typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.Invoke(playerPrefab.GetComponent<NetworkObject>(), null);
        EditorUtility.SetDirty(playerPrefab);
        AssetDatabase.SaveAssets();

        var nmGo = new GameObject("NetworkManager");
        var nm = nmGo.AddComponent<NetworkManager>();
        var transport = nmGo.AddComponent<UnityTransport>();
        nm.NetworkConfig ??= new NetworkConfig();
        nm.NetworkConfig.NetworkTransport = transport;
        nm.NetworkConfig.PlayerPrefab = playerPrefab;
        nm.NetworkConfig.EnableSceneManagement = false;
        nm.NetworkConfig.ConnectionApproval = false;
        nm.NetworkConfig.TickRate = 30;
        PrefabUtility.SaveAsPrefabAsset(nmGo, Folder + "/NetworkManager.prefab");
        Object.DestroyImmediate(nmGo);

        AssetDatabase.SaveAssets();
        Debug.Log("MultiplayerSetup: prefabs written to " + Folder);
    }

    // A development build of the main scene for trying multiplayer (two copies side by side): -mpBuildOut path.
    public static void BuildTest()
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-mpBuildOut");
        string output = i >= 0 ? args[i + 1] : "Builds/MultiplayerTest/ProjectZed.exe";
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/ProjectZed.unity" },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development,
        });
        Debug.Log($"MultiplayerSetup: build {report.summary.result} -> {output} ({report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime})");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
}
