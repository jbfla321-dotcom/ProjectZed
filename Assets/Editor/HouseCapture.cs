using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Screenshots of the built houses for review (Docs/Houses/Unity_*.png): for the first house of each type, a view
// from its yard and one from inside its biggest room, plus a window shot out (physics stepped by hand).
// Tools > Environment > Capture Houses, or -executeMethod HouseCapture.CaptureFromCommandLine. Nothing is saved
// to the scene.
public static class HouseCapture
{
    const string Out = "Docs/Houses";

    [MenuItem("Tools/Environment/Capture Houses")]
    public static void Capture()
    {
        Directory.CreateDirectory(Out);
        var houses = Object.FindObjectsByType<House>(FindObjectsInactive.Exclude).OrderBy(h => h.transform.parent.parent.name + h.transform.parent.name).ToList();
        foreach (var group in houses.GroupBy(h => h.type))
        {
            var h = group.First();
            var t = h.transform;
            float r = h.radius;
            // Yard: in front and off to one side, eye height
            Vector3 eye = t.TransformPoint(new Vector3(-r * 0.9f, 1.7f, r * 1.5f));
            eye.y = GroundAt(eye) + 1.7f;
            Shoot(eye, t.TransformPoint(new Vector3(0f, 2.2f, 0f)), $"{Out}/Unity_{h.type}_outside.png", 60f);
            // Inside the biggest ground-floor room, from one corner toward the opposite one
            var room = h.rooms.Where(b => b.min.y < 1.5f).OrderByDescending(b => b.size.x * b.size.z).First();
            Vector3 a = new Vector3(room.min.x + 0.35f, room.min.y + 1.65f, room.min.z + 0.35f);
            Vector3 b2 = new Vector3(room.max.x - 0.5f, room.min.y + 0.9f, room.max.z - 0.5f);
            Shoot(t.TransformPoint(a), t.TransformPoint(b2), $"{Out}/Unity_{h.type}_inside.png", 75f);
            Shoot(t.TransformPoint(new Vector3(room.max.x - 0.35f, room.min.y + 1.65f, room.max.z - 0.35f)),
                  t.TransformPoint(new Vector3(room.min.x + 0.5f, room.min.y + 0.9f, room.min.z + 0.5f)), $"{Out}/Unity_{h.type}_inside2.png", 75f);
        }

        // Shoot a window out on the first rambler (or any house) and let the shards fall for a second.
        var target = houses.FirstOrDefault(h => h.type == "Rambler") ?? houses.FirstOrDefault();
        if (target != null)
        {
            var glass = target.GetComponentsInChildren<BreakableGlass>().Where(g => !g.startBroken).OrderByDescending(g => g.transform.lossyScale.x).First();
            var gt = glass.transform;
            Vector3 view = gt.position + gt.forward * 2.2f + gt.right * 0.8f;
            view.y = gt.position.y;
            var mode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            glass.Shatter(gt.position + gt.right * gt.lossyScale.x * 0.15f, -gt.forward * 3f);
            for (int i = 0; i < 25; i++) Physics.Simulate(0.02f);
            Shoot(view, gt.position - gt.forward * 0.5f, $"{Out}/Unity_glass_shatter.png", 60f);
            for (int i = 0; i < 100; i++) Physics.Simulate(0.02f);
            Shoot(view, gt.position - gt.forward * 0.5f - Vector3.up * 0.6f, $"{Out}/Unity_glass_after.png", 60f);
            Physics.simulationMode = mode;
        }
        Debug.Log("[HouseCapture] Done");
    }

    public static void CaptureFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        Capture();
    }

    static float GroundAt(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var hit, 200f)) return hit.point.y;
        var t = Terrain.activeTerrain;
        return t != null ? t.SampleHeight(p) + t.transform.position.y : p.y;
    }

    static void Shoot(Vector3 from, Vector3 at, string path, float fov)
    {
        var go = new GameObject("CaptureCam");
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = fov;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 1500f;
        go.transform.position = from;
        go.transform.LookAt(at);
        var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);
        Debug.Log("[HouseCapture] " + path);
    }
}
