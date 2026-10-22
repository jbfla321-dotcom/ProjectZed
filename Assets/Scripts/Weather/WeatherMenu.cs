using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// In-game weather and time menu (dev builds and the Editor): F7 opens and closes it. Presets, sliders for every
// weather value, a transition time (0 = instant), auto weather on/off, and the clock (time, day length, pause).
// While it's open the mouse is free and the game's keyboard/mouse input is paused, like the dev console.
public class WeatherMenu : MonoBehaviour
{
    public Key toggleKey = Key.F7;

    bool open;
    float transition = 20f;
    WeatherManager.WeatherState edit;
    bool editing;
    Rect window = new Rect(20, 20, 360, 560);
    CursorLockMode cursorBefore;
    bool cursorVisibleBefore;
    readonly List<InputDevice> paused = new List<InputDevice>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (!Debug.isDebugBuild && !Application.isEditor) return;
        if (FindAnyObjectByType<WeatherMenu>() != null) return;
        var go = new GameObject("WeatherMenu");
        DontDestroyOnLoad(go);
        go.AddComponent<WeatherMenu>();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (!open && kb != null && kb[toggleKey].wasPressedThisFrame) SetOpen(true);
    }

    void OnDisable() => Resume();

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
            editing = false;
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
        foreach (var d in paused) if (d != null && d.added) InputSystem.EnableDevice(d);
        paused.Clear();
    }

    void OnGUI()
    {
        if (!open) return;
        var e = Event.current;
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.F7 || e.keyCode == KeyCode.Escape)) { SetOpen(false); e.Use(); return; }
        window = GUILayout.Window(91717, window, DrawWindow, "Weather & Time  (F7 to close)");
    }

    void DrawWindow(int id)
    {
        var wm = WeatherManager.Instance;
        var tod = TimeOfDay.Instance;
        if (wm == null) { GUILayout.Label("No WeatherManager in the scene (Tools > Environment > Setup Weather)."); GUI.DragWindow(); return; }
        if (!editing) { edit = wm.target; editing = true; }

        GUILayout.Label($"Now: {Describe(wm.current)}" + (wm.IsBlending ? "  (changing)" : ""));
        GUILayout.Space(4);
        GUILayout.Label("Presets");
        int perRow = 4, i = 0;
        GUILayout.BeginHorizontal();
        foreach (var p in WeatherManager.Presets)
        {
            if (i > 0 && i % perRow == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            if (GUILayout.Button(p.name)) { edit = p.state; wm.autoWeather = false; wm.Set(edit, transition); }
            i++;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label($"Transition: {(transition < 0.5f ? "instant" : Mathf.RoundToInt(transition) + " s")}");
        transition = Mathf.Round(GUILayout.HorizontalSlider(transition, 0f, 600f));
        GUILayout.BeginHorizontal();
        foreach (var s in new[] { 0f, 10f, 30f, 120f, 600f })
            if (GUILayout.Button(s == 0f ? "Instant" : s < 60f ? s + " s" : s / 60f + " min")) transition = s;
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        edit.overcast = Slider("Cloud cover", edit.overcast);
        edit.fog = Slider("Fog", edit.fog);
        edit.rain = Slider("Rain", edit.rain);
        edit.storm = Slider("Storm (lightning)", edit.storm);
        edit.wind = Slider("Wind", edit.wind);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Apply sliders")) { wm.autoWeather = false; wm.Set(edit, transition); }
        if (GUILayout.Button("Reset sliders")) edit = wm.target;
        GUILayout.EndHorizontal();
        bool auto = GUILayout.Toggle(wm.autoWeather, " Auto weather (changes on its own)");
        if (auto != wm.autoWeather) wm.autoWeather = auto;

        if (tod != null)
        {
            GUILayout.Space(10);
            GUILayout.Label($"Time {Clock(tod.time)}   sun {tod.SunAltitude:F0}°");
            float t = GUILayout.HorizontalSlider(tod.time, 0f, 24f);
            if (Mathf.Abs(t - tod.time) > 0.001f) tod.SetTime(t);
            GUILayout.BeginHorizontal();
            foreach (var (label, hour) in new[] { ("Dawn", 6.5f), ("Morning", 9f), ("Noon", 13f), ("Evening", 18.5f), ("Dusk", 19.6f), ("Night", 23f) })
                if (GUILayout.Button(label)) tod.SetTime(hour);
            GUILayout.EndHorizontal();
            GUILayout.Label(tod.dayLengthMinutes > 0f ? $"Day length: {tod.dayLengthMinutes:F0} real minutes" : "Clock paused");
            GUILayout.BeginHorizontal();
            foreach (var m in new[] { 0f, 24f, 60f, 120f, 1440f })
                if (GUILayout.Button(m == 0f ? "Pause" : m == 1440f ? "Real time" : m + " min")) tod.dayLengthMinutes = m;
            GUILayout.EndHorizontal();
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Close")) SetOpen(false);
        GUI.DragWindow();
    }

    static float Slider(string label, float v)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(120));
        v = GUILayout.HorizontalSlider(v, 0f, 1f);
        GUILayout.Label(v.ToString("0.00"), GUILayout.Width(36));
        GUILayout.EndHorizontal();
        return v;
    }

    public static string Clock(float hour)
    {
        int h = Mathf.FloorToInt(hour) % 24, m = Mathf.FloorToInt((hour - Mathf.Floor(hour)) * 60f);
        return $"{h:00}:{m:00}";
    }

    public static string Describe(WeatherManager.WeatherState s)
    {
        string sky = s.overcast < 0.2f ? "sunny" : s.overcast < 0.6f ? "cloudy" : "overcast";
        string extra = s.storm > 0.3f && s.rain > 0.2f ? ", storm" : s.rain > 0.5f ? ", rain" : s.rain > 0.05f ? ", drizzle" : "";
        if (s.fog > 0.6f) extra += ", foggy";
        return sky + extra;
    }
}
