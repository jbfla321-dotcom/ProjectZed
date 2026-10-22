using System.Reflection;
using UnityEngine;

// Automated co-op check, only with -mpTest on the command line (two copies of a build: one -mpHost, one -mpJoin).
// The joining player walks, jumps, fires and swings a door; the host logs what it sees of them and takes
// screenshots (-mpShots folder). Both quit after -mpTestSeconds (default 45).
public class NetTestDriver : MonoBehaviour
{
    float start;
    float quitAt;
    string shots;
    int shotCount, firedSeen;
    bool walked, fired, swung, jumped, worldChanged;
    FPSController player;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (System.Array.IndexOf(args, "-mpTest") < 0) return;
        var go = new GameObject("NetTestDriver");
        DontDestroyOnLoad(go);
        var d = go.AddComponent<NetTestDriver>();
        int i = System.Array.IndexOf(args, "-mpShots");
        d.shots = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        i = System.Array.IndexOf(args, "-mpTestSeconds");
        d.quitAt = i >= 0 && i + 1 < args.Length ? float.Parse(args[i + 1]) : 45f;
    }

    void Start()
    {
        start = Time.time;
        player = FindAnyObjectByType<FPSController>();
        InvokeRepeating(nameof(Report), 2f, 2f);
    }

    float Since => Time.time - start;

    void Update()
    {
        if (Since > quitAt) { Debug.Log("NetTest: done"); Application.Quit(); enabled = false; return; }
        var local = NetPlayer.Local;
        if (local == null || player == null) return;

        foreach (var p in FindObjectsByType<NetPuppet>(FindObjectsSortMode.None))
            if (p.gun != null && p.GetComponent<FiredCounter>() == null) p.gun.Fired += p.gameObject.AddComponent<FiredCounter>().Count;

        // Face the other player for the screenshots (the joining player once its walk is done).
        if (local.IsServer || Since > 24f)
            foreach (var p in FindObjectsByType<NetPuppet>(FindObjectsSortMode.None))
            {
                Vector3 to = p.transform.position - player.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f) player.transform.rotation = Quaternion.LookRotation(to);
            }
        if (shots != null && shotCount < 5 && Since > 12f + shotCount * 6f)
        {
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, $"{(local.IsServer ? "host" : "client")}_{shotCount}.png"));
            shotCount++;
        }
        if (local.IsServer)
        {
            // Change the world here; the joining player's should follow.
            if (!worldChanged && Since > 4f)
            {
                worldChanged = true;
                WeatherManager.Instance?.SetPreset("Sunny");
                TimeOfDay.Instance?.SetTime(17f);
                Debug.Log("NetTest: host set Sunny, 17:00");
            }
            return;
        }

        // The joining player.
        float t = Since;
        if (!walked && t > 10f) { player.scriptedMoveInput = new Vector2(0f, 1f); walked = true; Debug.Log("NetTest: walking"); }
        if (walked && t > 12f) player.scriptedMoveInput = Vector2.zero;
        if (!jumped && t > 14f)
        {
            jumped = true;
            typeof(CharacterMotor).GetMethod("OnJumped", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(player, null);
            Debug.Log("NetTest: jumped");
        }
        if (!fired && t > 18f)
        {
            fired = true;
            var gun = player.GetComponentInChildren<HitscanGun>();
            var fire = typeof(HitscanGun).GetMethod("Fire", BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < 3; i++) fire.Invoke(gun, null);
            Debug.Log("NetTest: fired 3");
        }
        if (!swung && t > 22f)
        {
            swung = true;
            Door nearest = null;
            float best = float.MaxValue;
            foreach (var d in FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                float dist = (d.transform.position - player.transform.position).sqrMagnitude;
                if (dist < best) { best = dist; nearest = d; }
            }
            if (nearest != null)
            {
                nearest.Interact(player.GetComponent<PlayerInteractor>());
                Debug.Log($"NetTest: swung door {nearest.name} at {nearest.transform.position} to {nearest.Target}");
            }
        }
    }

    void Report()
    {
        var local = NetPlayer.Local;
        string me = player != null ? player.transform.position.ToString("F1") : "-";
        string s = $"NetTest t={Since:F0} {(local == null ? "not connected" : local.IsServer ? "host" : "client")} me={me} status='{NetSession.Instance?.Status}' code={NetSession.Instance?.JoinCode}";
        foreach (var p in FindObjectsByType<NetPuppet>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var fc = p.GetComponent<FiredCounter>();
            var anim = p.body != null ? p.body.GetComponent<Animator>() : null;
            s += $" | {p.name} active={p.gameObject.activeSelf} pos={p.transform.position:F1} vel={p.Velocity:F1} speedParam={(anim != null ? anim.GetFloat("Speed") : -1):F2} shotsSeen={(fc != null ? fc.n : 0)}";
        }
        var clock = TimeOfDay.Instance;
        var w = WeatherManager.Instance;
        if (clock != null) s += $" | time={clock.time:F3}";
        if (w != null) s += $" weather=({w.current})";
        Debug.Log(s);
    }

    class FiredCounter : MonoBehaviour { public int n; public void Count() => n++; }
}
