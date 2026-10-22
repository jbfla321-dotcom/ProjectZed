using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

// Developer console for testing: ` (backquote / tilde) opens and closes it. Type "help" for the commands.
// While it's open the game's keyboard and mouse are paused (so typing doesn't move or shoot), and the cursor is free.
// Creates itself in the Editor and development builds; add commands in RegisterCommands.
public class DevConsole : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (!Debug.isDebugBuild || FindAnyObjectByType<DevConsole>() != null) return;
        var go = new GameObject("DevConsole");
        DontDestroyOnLoad(go);
        go.AddComponent<DevConsole>();
    }

    class Command
    {
        public string name, usage, help;
        public Action<string[]> run;
    }

    const int MaxLog = 200;
    const string InputControl = "DevConsoleInput";

    readonly Dictionary<string, Command> commands = new Dictionary<string, Command>();
    readonly List<string> log = new List<string>();
    readonly List<string> history = new List<string>();
    int historyIndex;
    string input = "";
    bool open, focusInput;
    Vector2 scroll;
    CursorLockMode cursorBefore;
    bool cursorVisibleBefore;
    readonly List<InputDevice> pausedDevices = new List<InputDevice>();
    GUIStyle logStyle, inputStyle;

    void Awake()
    {
        RegisterCommands();
        Print("Dev console: type 'help' for commands.");
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (!open && kb != null && kb.backquoteKey.wasPressedThisFrame) SetOpen(true);
    }

    void OnDisable() => ResumeInput();

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
            // The console types through IMGUI events, which don't come from these devices.
            foreach (var device in new InputDevice[] { Keyboard.current, Mouse.current })
                if (device != null && device.enabled) { InputSystem.DisableDevice(device); pausedDevices.Add(device); }
            focusInput = true;
            input = "";
        }
        else
        {
            ResumeInput();
            Cursor.lockState = cursorBefore;
            Cursor.visible = cursorVisibleBefore;
        }
    }

    void ResumeInput()
    {
        foreach (var device in pausedDevices)
            if (device != null && device.added) InputSystem.EnableDevice(device);
        pausedDevices.Clear();
    }

    // ---------- UI ----------
    void OnGUI()
    {
        if (!open) return;
        if (logStyle == null)
        {
            logStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, richText = false };
            logStyle.normal.textColor = new Color(0.85f, 0.95f, 0.85f);
            inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = 16 };
        }

        var e = Event.current;
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.BackQuote || e.keyCode == KeyCode.Escape) { e.Use(); SetOpen(false); return; }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.Use(); Submit(); }
            else if (e.keyCode == KeyCode.UpArrow) { e.Use(); Recall(-1); }
            else if (e.keyCode == KeyCode.DownArrow) { e.Use(); Recall(1); }
            else if (e.keyCode == KeyCode.Tab) { e.Use(); Complete(); }
        }

        float height = Screen.height * 0.45f;
        GUI.Box(new Rect(0, 0, Screen.width, height), GUIContent.none);
        GUI.Box(new Rect(0, 0, Screen.width, height), GUIContent.none); // twice: darker
        var logArea = new Rect(10, 8, Screen.width - 20, height - 50);
        string text = string.Join("\n", log);
        float textHeight = logStyle.CalcHeight(new GUIContent(text), logArea.width - 20);
        scroll = GUI.BeginScrollView(logArea, scroll, new Rect(0, 0, logArea.width - 20, Mathf.Max(textHeight, logArea.height)));
        GUI.Label(new Rect(0, 0, logArea.width - 20, textHeight), text, logStyle);
        GUI.EndScrollView();

        GUI.SetNextControlName(InputControl);
        input = GUI.TextField(new Rect(10, height - 36, Screen.width - 20, 28), input, inputStyle).Replace("`", "");
        if (focusInput)
        {
            GUI.FocusControl(InputControl);
            focusInput = false;
        }
    }

    void Submit()
    {
        string line = input.Trim();
        input = "";
        focusInput = true;
        if (line.Length == 0) return;
        history.Add(line);
        historyIndex = history.Count;
        Print("> " + line);
        Execute(line);
    }

    void Recall(int step)
    {
        if (history.Count == 0) return;
        historyIndex = Mathf.Clamp(historyIndex + step, 0, history.Count);
        input = historyIndex < history.Count ? history[historyIndex] : "";
        focusInput = true;
    }

    void Complete()
    {
        var matches = commands.Keys.Where(k => k.StartsWith(input.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).ToList();
        if (matches.Count == 1) input = matches[0] + " ";
        else if (matches.Count > 1) Print(string.Join("  ", matches));
        focusInput = true;
    }

    public void Print(string line)
    {
        log.Add(line);
        if (log.Count > MaxLog) log.RemoveAt(0);
        scroll.y = float.MaxValue;
    }

    public void Execute(string line)
    {
        var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (!commands.TryGetValue(parts[0].ToLowerInvariant(), out var cmd))
        {
            Print($"Unknown command '{parts[0]}'. Type 'help'.");
            return;
        }
        try { cmd.run(parts.Skip(1).ToArray()); }
        catch (Exception ex) { Print("Error: " + ex.Message); }
    }

    void Add(string name, string usage, string help, Action<string[]> run) =>
        commands[name] = new Command { name = name, usage = usage, help = help, run = run };

    // ---------- Commands ----------
    void RegisterCommands()
    {
        Add("help", "help", "List commands.", args =>
        {
            foreach (var c in commands.Values.OrderBy(c => c.name)) Print($"{c.usage,-28} {c.help}");
        });
        Add("clear", "clear", "Clear the console.", args => log.Clear());
        Add("fly", "fly [speed]", "Toggle fly mode (WASD + look, Space up, Ctrl down, Shift fast).", Fly);
        Add("tp", "tp <place> | tp <x> <z> | tp <x> <y> <z>", "Teleport to a named location or coordinates.", Teleport);
        Add("pos", "pos", "Show where you are.", args =>
        {
            var p = Player();
            Print($"Position {p.transform.position.x:F1} {p.transform.position.y:F1} {p.transform.position.z:F1}, facing {p.transform.eulerAngles.y:F0} degrees");
        });
        Add("spawn", "spawn car|mag|crate [what]", "Spawn in front of you. 'spawn car' lists the cars.", Spawn);
        Add("give", "give mags [n] | give rifle", "Add magazines to your pouch, or put your rifle back in your hands.", Give);
        Add("wind", "wind calm|breeze|windy|storm | wind <strength 0-1> [direction] [seconds]", "Set the wind (blends over a few seconds).", Wind);
        Add("weather", "weather [preset|auto|<value> <0-1>] [seconds]", "Show or set the weather: sunny, cloudy, overcast, foggy, drizzle, rain, storm; or overcast/fog/rain/storm/wind <0-1>. F7 opens the weather menu.", Weather);
        Add("time", "time [hh[:mm]] | time speed <minutes per day> | time pause", "Show or set the time of day.", TimeCmd);
    }

    void Weather(string[] args)
    {
        var wm = WeatherManager.Instance;
        if (wm == null) throw new Exception("No WeatherManager in the scene (Tools > Environment > Setup Weather).");
        if (args.Length == 0)
        {
            Print($"Weather: {WeatherMenu.Describe(wm.current)} ({wm.current}){(wm.autoWeather ? ", auto" : "")}. Presets: " + string.Join(", ", WeatherManager.Presets.Select(p => p.name.ToLowerInvariant())));
            return;
        }
        string what = args[0].ToLowerInvariant();
        if (what == "auto") { wm.autoWeather = args.Length < 2 || args[1] != "off"; Print("Auto weather " + (wm.autoWeather ? "on." : "off.")); return; }
        if (wm.SetPreset(what, args.Length > 1 ? ParseFloat(args[1]) : 0f))
        {
            wm.autoWeather = false;
            Print($"Weather -> {what}{(args.Length > 1 ? $" over {args[1]} s" : "")}.");
            return;
        }
        if (args.Length < 2) throw new Exception("Unknown preset. Try: weather rain 30, or weather fog 0.7 10");
        var s = wm.target;
        float v = Mathf.Clamp01(ParseFloat(args[1]));
        switch (what)
        {
            case "overcast": case "clouds": s.overcast = v; break;
            case "fog": s.fog = v; break;
            case "rain": s.rain = v; break;
            case "storm": s.storm = v; break;
            case "wind": s.wind = v; break;
            default: throw new Exception("Unknown weather value: " + what);
        }
        wm.autoWeather = false;
        float seconds = args.Length > 2 ? ParseFloat(args[2]) : 0f;
        wm.Set(s, seconds);
        Print($"Weather -> {s}{(seconds > 0f ? $" over {seconds:F0} s" : "")}.");
    }

    void TimeCmd(string[] args)
    {
        var tod = TimeOfDay.Instance;
        if (tod == null) throw new Exception("No TimeOfDay in the scene (Tools > Environment > Setup Weather).");
        if (args.Length == 0) { Print($"Time {WeatherMenu.Clock(tod.time)}, sun {tod.SunAltitude:F0} deg, day = {tod.dayLengthMinutes:F0} real minutes."); return; }
        if (args[0] == "pause") { tod.dayLengthMinutes = 0f; Print("Clock paused."); return; }
        if (args[0] == "speed" && args.Length > 1) { tod.dayLengthMinutes = Mathf.Max(0f, ParseFloat(args[1])); Print($"Day length {tod.dayLengthMinutes:F0} real minutes."); return; }
        var parts = args[0].Split(':');
        float hour = ParseFloat(parts[0]) + (parts.Length > 1 ? ParseFloat(parts[1]) / 60f : 0f);
        tod.SetTime(hour);
        Print($"Time -> {WeatherMenu.Clock(tod.time)}.");
    }

    void Wind(string[] args)
    {
        var w = WindManager.Instance;
        if (w == null) throw new Exception("No WindManager in the scene (Tools > Environment > Build PNW Forest adds one).");
        if (args.Length == 0)
        {
            Print($"Wind {w.current.strength:F2} towards {w.current.direction:F0} deg ({w.Speed:F0} m/s), gustiness {w.current.gustiness:F2}, turbulence {w.current.turbulence:F2}.");
            return;
        }
        WindManager.WindState target;
        switch (args[0].ToLowerInvariant())
        {
            case "calm": target = WindManager.Calm; break;
            case "breeze": target = WindManager.Breeze; break;
            case "windy": target = WindManager.Windy; break;
            case "storm": target = WindManager.Storm; break;
            default:
                float s = Mathf.Clamp01(ParseFloat(args[0]));
                target = WindManager.WindState.Lerp(WindManager.Calm, WindManager.Storm, s);
                target.strength = s;
                break;
        }
        target.direction = args.Length > 1 ? ParseFloat(args[1]) : w.current.direction;
        float seconds = args.Length > 2 ? ParseFloat(args[2]) : 4f;
        w.SetWind(target, seconds);
        Print($"Wind -> {target.strength:F2} towards {target.direction:F0} deg over {seconds:F0} s.");
    }

    FPSController Player()
    {
        var p = FindAnyObjectByType<FPSController>();
        if (p == null) throw new Exception("No player in the scene.");
        return p;
    }

    void Fly(string[] args)
    {
        var p = Player();
        if (args.Length > 0)
        {
            p.flySpeed = Mathf.Max(1f, ParseFloat(args[0]));
            if (!p.Flying) p.SetFlying(true);
        }
        else p.SetFlying(!p.Flying);
        Print(p.Flying ? $"Fly mode ON at {p.flySpeed:F0} m/s (Shift x{p.flyFastMultiplier:F0}). 'fly' again to land." : "Fly mode OFF (set down on the ground).");
    }

    // Named places: the world's locations, plus the field in the middle.
    Dictionary<string, Vector3> Places()
    {
        var places = new Dictionary<string, Vector3> { ["center"] = new Vector3(0f, 0f, -9f) };
        var root = GameObject.Find("World/Locations");
        if (root != null)
            foreach (Transform loc in root.transform) places[Key(loc.name)] = loc.position;
        return places;
    }

    static string Key(string name) => new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    void Teleport(string[] args)
    {
        var p = Player();
        var places = Places();
        if (args.Length == 0) { Print("Places: " + string.Join(", ", places.Keys)); return; }
        Vector3 target;
        if (args.Length >= 2 && float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            float x = ParseFloat(args[0]), z = ParseFloat(args[args.Length - 1]);
            target = args.Length >= 3 ? new Vector3(x, ParseFloat(args[1]), z) : new Vector3(x, GroundY(x, z), z);
        }
        else
        {
            string want = Key(string.Join("", args));
            var match = places.Keys.FirstOrDefault(k => k == want) ?? places.Keys.FirstOrDefault(k => k.StartsWith(want)) ?? places.Keys.FirstOrDefault(k => k.Contains(want));
            if (match == null) { Print($"No place '{want}'. Places: {string.Join(", ", places.Keys)}"); return; }
            target = places[match];
            target.y = GroundY(target.x, target.z);
        }
        var cc = p.GetComponent<CharacterController>();
        bool ccWasOn = cc.enabled;
        cc.enabled = false;
        p.transform.position = target + Vector3.up * 0.1f;
        cc.enabled = ccWasOn;
        Print($"Teleported to {target.x:F0} {target.y:F0} {target.z:F0}.");
    }

    void Spawn(string[] args)
    {
        string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (what)
        {
            case "car": SpawnCar(args.Skip(1).ToArray()); break;
            case "mag": case "magazine":
            {
                int rounds = args.Length > 1 ? (int)ParseFloat(args[1]) : 30;
                var rifle = PlayerRifle();
                if (rifle == null || rifle.Magazine == null) throw new Exception("No rifle magazine model to copy.");
                var spot = InFront(1.5f) + Vector3.up * 0.5f;
                var mag = DroppedMagazine.Spawn(rifle.Magazine, rounds, rifle.magazineSize, Vector3.zero);
                mag.transform.SetPositionAndRotation(spot, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 90f));
                Print($"Spawned a magazine ({rounds}/{rifle.magazineSize}).");
                break;
            }
            case "crate":
            {
                int mags = args.Length > 1 ? (int)ParseFloat(args[1]) : 99;
                var crate = SpawnCrate(InFront(3f), mags);
                Print($"Spawned an ammo crate with {crate.magazines} magazines.");
                break;
            }
            default:
                Print("Usage: spawn car [number|name] | spawn mag [rounds] | spawn crate [magazines]");
                break;
        }
    }

    void SpawnCar(string[] args)
    {
        // The scene's own cars are the templates (spawned copies aren't listed, so the numbers stay put).
        var cars = CarController.All.Where(c => c != null && !c.name.EndsWith("(spawned)")).OrderBy(c => c.name).ToList();
        if (cars.Count == 0) throw new Exception("No cars in the scene to copy.");
        if (args.Length == 0)
        {
            for (int i = 0; i < cars.Count; i++) Print($"  {i + 1}: {CarName(cars[i])}");
            Print("Usage: spawn car <number|name>");
            return;
        }
        CarController template;
        if (int.TryParse(args[0], out int n)) template = n >= 1 && n <= cars.Count ? cars[n - 1] : null;
        else
        {
            string want = Key(string.Join("", args));
            template = cars.FirstOrDefault(c => Key(CarName(c)).Contains(want));
        }
        if (template == null) { Print("No such car. 'spawn car' lists them."); return; }
        var player = Player();
        if (player.transform.IsChildOf(template.transform)) { Print("Get out of that car first."); return; }

        // Side-on, a few metres ahead, dropped just above the ground.
        Vector3 spot = InFront(6f) + Vector3.up * 0.6f;
        Quaternion facing = Quaternion.LookRotation(player.transform.right);
        var car = Instantiate(template.gameObject, spot, facing);
        car.name = template.name + " (spawned)";
        var rb = car.GetComponent<Rigidbody>();
        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        Print($"Spawned {CarName(template)}.");
    }

    // "Drivable_Car3" -> "Car 3" (the car pack's models have no better names).
    static string CarName(CarController car) => car.name.Replace("Drivable_", "").Replace("Car", "Car ");

    AmmoCrate SpawnCrate(Vector3 spot, int mags)
    {
        var template = FindAnyObjectByType<AmmoCrate>();
        AmmoCrate crate;
        if (template != null)
        {
            crate = Instantiate(template, spot + Vector3.up * 0.5f, Quaternion.identity);
            if (template.label != null) crate.label = Instantiate(template.label);
        }
        else
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.position = spot + Vector3.up * 0.5f;
            go.transform.localScale = Vector3.one * 0.6f;
            go.AddComponent<Rigidbody>().mass = 20f;
            crate = go.AddComponent<AmmoCrate>();
        }
        crate.name = "AmmoCrate (spawned)";
        crate.magazines = mags;
        if (crate.label != null) crate.label.text = $"AMMO\n{mags} × {crate.contents}";
        return crate;
    }

    void Give(string[] args)
    {
        string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var player = Player();
        if (what == "mags" || what == "mag" || what == "magazines")
        {
            var pouch = player.GetComponent<MagazinePouch>();
            if (pouch == null) throw new Exception("The player has no magazine pouch.");
            int n = args.Length > 1 ? (int)ParseFloat(args[1]) : 5;
            for (int i = 0; i < n; i++) pouch.Add(pouch.magazineSize);
            Print($"Added {n} full magazines ({pouch.Count} in pouch).");
        }
        else if (what == "rifle")
        {
            var rifle = PlayerRifle();
            if (rifle == null) throw new Exception("The player has no rifle.");
            if (rifle.IsHeld) { Print("Your rifle is already in your hands."); return; }
            rifle.Attach();
            Print("Rifle back in your hands.");
        }
        else Print("Usage: give mags [n] | give rifle");
    }

    CarriedRifle PlayerRifle()
    {
        var hands = Player().GetComponent<PlayerInteractor>();
        return hands != null ? hands.Rifle : null;
    }

    // A point on the ground this far ahead of the player.
    Vector3 InFront(float distance)
    {
        var t = Player().transform;
        Vector3 forward = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
        Vector3 p = t.position + forward * distance;
        p.y = GroundY(p.x, p.z, t.position.y + 50f);
        return p;
    }

    static float GroundY(float x, float z, float from = 1000f)
    {
        return Physics.Raycast(new Vector3(x, from, z), Vector3.down, out RaycastHit hit, from + 1000f, CharacterLayers.Environment, QueryTriggerInteraction.Ignore)
            ? hit.point.y : 0f;
    }

    static float ParseFloat(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
