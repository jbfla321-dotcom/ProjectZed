using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.InputSystem;

// Co-op over the internet: F8 opens the multiplayer menu. Host makes a game and shows a short join code; the
// friend types the code to join. Unity Relay connects the two (no port forwarding), signed in anonymously
// with Unity Services. The host's world is the shared one: everyone's own player stays theirs to move, and the
// others appear as NetPuppet copies (NetPlayer). Doors, time of day and weather follow the host.
// Command line (testing): -mpHost [-mpCodeFile path] hosts; -mpJoin CODE joins; -mpDirectHost / -mpDirectJoin IP
// skip Relay and connect straight to port 7777.
public class NetSession : MonoBehaviour
{
    public Key toggleKey = Key.F8;
    public const int MaxPlayers = 4;
    const ushort DirectPort = 7777;

    public static NetSession Instance { get; private set; }
    public static bool Active => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
    public static bool IsHost => Active && NetworkManager.Singleton.IsServer;

    public string JoinCode { get; private set; }
    public string Status { get; private set; } = "";

    ISession session;
    bool busy;
    bool open;
    string codeInput = "";
    Rect window = new Rect(20, 20, 340, 230);
    CursorLockMode cursorBefore;
    bool cursorVisibleBefore;
    readonly List<InputDevice> paused = new List<InputDevice>();
    GUIStyle bigStyle, smallStyle;

    // Doors by index (the same order in every copy of the game), so an open/shut can be sent as a number.
    Door[] doors;
    readonly Dictionary<Door, int> doorIndex = new Dictionary<Door, int>();
    bool applyingDoor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("NetSession");
        DontDestroyOnLoad(go);
        go.AddComponent<NetSession>();
    }

    void Awake()
    {
        Instance = this;
        var prefab = Resources.Load<GameObject>("Multiplayer/NetworkManager");
        if (prefab == null)
        {
            Debug.LogWarning("NetSession: Resources/Multiplayer/NetworkManager is missing (Tools > Multiplayer > Setup)");
            enabled = false;
            return;
        }
        var nm = Instantiate(prefab);
        nm.name = "NetworkManager";
        DontDestroyOnLoad(nm);
    }

    void OnEnable() => Door.Swung += OnDoorSwung;
    void OnDisable()
    {
        Door.Swung -= OnDoorSwung;
        Resume();
    }

    async void Start()
    {
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetPuppet.PrepareTemplate(); // now, while our player stands with the rifle in hand

        string[] args = Environment.GetCommandLineArgs();
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Array.IndexOf(args, "-mpDirectHost") >= 0) StartDirect(true, "127.0.0.1");
        else if (Arg("-mpDirectJoin") != null) StartDirect(false, Arg("-mpDirectJoin"));
        else if (Array.IndexOf(args, "-mpHost") >= 0)
        {
            await Host();
            string file = Arg("-mpCodeFile");
            if (file != null && JoinCode != null) System.IO.File.WriteAllText(file, JoinCode);
        }
        else if (Arg("-mpJoin") != null) await Join(Arg("-mpJoin"));
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (!open && kb != null && kb[toggleKey].wasPressedThisFrame) SetOpen(true);
    }

    // ---------- Sessions ----------
    async Task SignIn()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            // A fresh profile each run, so two copies on one computer (testing) sign in as different players.
            var options = new InitializationOptions();
            options.SetProfile("p" + Guid.NewGuid().ToString("N").Substring(0, 12));
            await UnityServices.InitializeAsync(options);
        }
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    public async Task Host()
    {
        if (busy || Active) return;
        busy = true;
        Status = "Starting...";
        try
        {
            await SignIn();
            var options = new SessionOptions { MaxPlayers = MaxPlayers, IsPrivate = true }.WithRelayNetwork();
            session = await MultiplayerService.Instance.CreateSessionAsync(options);
            JoinCode = session.Code;
            Status = "Hosting";
            Debug.Log($"NetSession: hosting, join code {JoinCode}");
        }
        catch (Exception e)
        {
            Status = "Couldn't host: " + e.Message;
            Debug.LogException(e);
            session = null;
        }
        busy = false;
    }

    public async Task Join(string code)
    {
        code = code?.Trim().ToUpperInvariant();
        if (busy || Active || string.IsNullOrEmpty(code)) return;
        busy = true;
        Status = "Joining...";
        try
        {
            await SignIn();
            session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            JoinCode = session.Code;
            Status = "Connected";
            Debug.Log($"NetSession: joined {JoinCode}");
        }
        catch (Exception e)
        {
            Status = "Couldn't join: " + e.Message;
            Debug.LogException(e);
            session = null;
        }
        busy = false;
    }

    public async Task Leave()
    {
        if (busy) return;
        busy = true;
        try { if (session != null) await session.LeaveAsync(); }
        catch (Exception e) { Debug.LogException(e); }
        session = null;
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) NetworkManager.Singleton.Shutdown();
        JoinCode = null;
        Status = "Left the game";
        busy = false;
    }

    // Straight to an IP address (no Relay), for testing on one computer or a LAN.
    void StartDirect(bool host, string address)
    {
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData(address, DirectPort, host ? "0.0.0.0" : null);
        bool ok = host ? NetworkManager.Singleton.StartHost() : NetworkManager.Singleton.StartClient();
        Status = ok ? (host ? "Hosting (direct)" : "Connecting (direct)...") : "Couldn't start";
        Debug.Log($"NetSession: direct {(host ? "host" : "client")} {address}:{DirectPort} -> {ok}");
    }

    void OnClientConnected(ulong id)
    {
        if (id == NetworkManager.Singleton.LocalClientId && !NetworkManager.Singleton.IsServer) Status = "Connected";
        Debug.Log($"NetSession: client {id} connected");
    }

    void OnClientDisconnect(ulong id)
    {
        var nm = NetworkManager.Singleton;
        if (nm.IsServer) { Debug.Log($"NetSession: client {id} left"); return; }
        // We're a client and lost the host.
        Status = string.IsNullOrEmpty(nm.DisconnectReason) ? "Disconnected from the host" : "Disconnected: " + nm.DisconnectReason;
        Debug.Log("NetSession: " + Status);
        session = null;
        JoinCode = null;
    }

    // ---------- Doors ----------
    void IndexDoors()
    {
        doors = FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Array.Sort(doors, (a, b) => string.CompareOrdinal(PathOf(a.transform), PathOf(b.transform)));
        doorIndex.Clear();
        for (int i = 0; i < doors.Length; i++) doorIndex[doors[i]] = i;
    }

    static string PathOf(Transform t)
    {
        string path = t.gameObject.scene.name;
        var stack = new List<string>();
        for (; t != null; t = t.parent) stack.Add(t.GetSiblingIndex() + t.name);
        for (int i = stack.Count - 1; i >= 0; i--) path += "/" + stack[i];
        return path;
    }

    Door[] Doors
    {
        get
        {
            if (doors == null || Array.Exists(doors, d => d == null)) IndexDoors();
            return doors;
        }
    }

    void OnDoorSwung(Door door)
    {
        if (applyingDoor || !Active || NetPlayer.Local == null) return;
        if (Doors != null && doorIndex.TryGetValue(door, out int i)) NetPlayer.Local.SendDoor(i, door.Target);
    }

    public void ApplyDoor(int index, float target)
    {
        var all = Doors;
        if (index < 0 || index >= all.Length) return;
        if (all[index].Target == target) return;
        applyingDoor = true;
        all[index].SwingTo(target);
        applyingDoor = false;
        Debug.Log($"NetSession: door {index} ({all[index].name} at {all[index].transform.position}) swung to {target} by another player");
    }

    // Every door's angle (for a player joining).
    public float[] DoorTargets()
    {
        var all = Doors;
        var targets = new float[all.Length];
        for (int i = 0; i < all.Length; i++) targets[i] = all[i].Target;
        return targets;
    }

    public void ApplyDoorTargets(float[] targets)
    {
        var all = Doors;
        if (targets.Length != all.Length) { Debug.LogWarning($"NetSession: host has {targets.Length} doors, we have {all.Length}"); return; }
        for (int i = 0; i < all.Length; i++) ApplyDoor(i, targets[i]);
    }

    // ---------- Menu ----------
    void SetOpen(bool value)
    {
        if (open == value) return;
        open = value;
        if (open)
        {
            cursorBefore = Cursor.lockState;
            cursorVisibleBefore = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            foreach (var d in new InputDevice[] { Keyboard.current, Mouse.current })
                if (d != null && d.enabled) { InputSystem.DisableDevice(d); paused.Add(d); }
        }
        else
        {
            Resume();
            Cursor.lockState = cursorBefore;
            Cursor.visible = cursorVisibleBefore;
        }
    }

    void Resume()
    {
        foreach (var d in paused) if (d != null) InputSystem.EnableDevice(d);
        paused.Clear();
    }

    int PlayerCount => Active ? (NetworkManager.Singleton.IsServer ? NetworkManager.Singleton.ConnectedClientsIds.Count : NetPlayer.All.Count) : 0;

    void OnGUI()
    {
        if (bigStyle == null)
        {
            bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.UpperRight };
        }
        if (!open)
        {
            if (Active)
                GUI.Label(new Rect(Screen.width - 420, 8, 410, 22),
                    $"Co-op{(JoinCode != null ? " · code " + JoinCode : "")} · {PlayerCount} player{(PlayerCount == 1 ? "" : "s")} (F8)", smallStyle);
            return;
        }
        var e = Event.current;
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.F8 || e.keyCode == KeyCode.Escape)) { SetOpen(false); e.Use(); return; }
        window = GUI.Window(0x6E6574, window, DrawWindow, "Multiplayer (F8)");
    }

    void DrawWindow(int id)
    {
        GUILayout.Space(4);
        if (Active)
        {
            if (NetworkManager.Singleton.IsServer)
            {
                GUILayout.Label("Your friend joins with this code:");
                GUILayout.Label(JoinCode ?? "(direct connection)", bigStyle, GUILayout.Height(40));
                if (JoinCode != null && GUILayout.Button("Copy code")) GUIUtility.systemCopyBuffer = JoinCode;
            }
            else GUILayout.Label($"In {(JoinCode != null ? JoinCode : "a game")} with the host.");
            GUILayout.Label($"Players: {PlayerCount}");
            GUILayout.FlexibleSpace();
            GUI.enabled = !busy;
            if (GUILayout.Button("Leave game")) _ = Leave();
            GUI.enabled = true;
        }
        else
        {
            GUI.enabled = !busy;
            if (GUILayout.Button("Host a game", GUILayout.Height(32))) _ = Host();
            GUILayout.Space(10);
            GUILayout.Label("Or join with a code:");
            GUILayout.BeginHorizontal();
            codeInput = GUILayout.TextField(codeInput, 12, GUILayout.Height(26)).ToUpperInvariant();
            if (GUILayout.Button("Paste", GUILayout.Width(60), GUILayout.Height(26))) codeInput = GUIUtility.systemCopyBuffer.Trim().ToUpperInvariant();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Join", GUILayout.Height(28))) _ = Join(codeInput);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
        }
        if (!string.IsNullOrEmpty(Status)) GUILayout.Label(Status);
        if (GUILayout.Button("Close")) SetOpen(false);
        GUI.DragWindow();
    }
}
