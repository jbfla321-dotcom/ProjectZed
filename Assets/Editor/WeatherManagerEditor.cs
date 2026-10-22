using UnityEditor;
using UnityEngine;

// Preset and time buttons on the Weather object's inspector, so the weather can be set in Edit mode too.
[CustomEditor(typeof(WeatherManager))]
public class WeatherManagerEditor : Editor
{
    float seconds;

    public override void OnInspectorGUI()
    {
        var wm = (WeatherManager)target;
        EditorGUILayout.LabelField("Set the weather", EditorStyles.boldLabel);
        seconds = EditorGUILayout.Slider("Transition (s, Play mode)", seconds, 0f, 600f);
        EditorGUILayout.BeginHorizontal();
        int i = 0;
        foreach (var p in WeatherManager.Presets)
        {
            if (i > 0 && i % 4 == 0) { EditorGUILayout.EndHorizontal(); EditorGUILayout.BeginHorizontal(); }
            if (GUILayout.Button(p.name))
            {
                Undo.RecordObject(wm, "Set weather");
                wm.autoWeather = false;
                wm.Set(p.state, Application.isPlaying ? seconds : 0f);
                EditorUtility.SetDirty(wm);
            }
            i++;
        }
        EditorGUILayout.EndHorizontal();

        var tod = wm.GetComponent<TimeOfDay>();
        if (tod != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Time {WeatherMenu.Clock(tod.time)}  (sun {tod.SunAltitude:F0}°)", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            foreach (var (label, hour) in new[] { ("Dawn", 6.5f), ("Morning", 9f), ("Noon", 13f), ("Evening", 18.5f), ("Dusk", 19.6f), ("Night", 23f) })
                if (GUILayout.Button(label)) { Undo.RecordObject(tod, "Set time"); tod.SetTime(hour); EditorUtility.SetDirty(tod); }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.Space();
        DrawDefaultInspector();
    }
}
