using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

// Rain around the camera: thousands of streaks in a box that follows the view (drawn in one call by the
// ProjectZed/Rain shader, which moves, wraps and slants them with the wind on the GPU), plus splash rings where they
// land. Drops stop at the first thing above the ground (roofs, porch roofs, trees): a height map of the highest
// surface under each spot near the player, built from a grid of downward raycasts whenever the player has moved a
// little, so it never rains indoors. WeatherManager sets intensity (0-1).
[ExecuteAlways]
public class RainSystem : MonoBehaviour
{
    [Range(0f, 1f)] public float intensity;
    public Material rainMaterial;
    public Material splashMaterial;

    [Header("Drops")]
    public int maxDrops = 14000;
    [Tooltip("Size of the box of rain around the camera (m).")]
    public Vector3 boxSize = new Vector3(30f, 18f, 30f);
    [Tooltip("Fall speed (m/s).")]
    public float fallSpeed = 9f;
    public float streakLength = 0.45f;
    public float streakWidth = 0.006f;
    [Tooltip("How much the wind blows the drops sideways (0-1 of the wind speed).")]
    public float windInfluence = 0.6f;

    [Header("Splashes")]
    public int maxSplashes = 900;
    [Tooltip("Splash rings per second per splash slot.")]
    public float splashRate = 2.2f;

    [Header("Shelter map")]
    [Tooltip("Side of the square around the player the shelter map covers (m).")]
    public float shelterSize = 64f;
    public int shelterResolution = 128;
    [Tooltip("Rebuild it when the player has moved this far (m).")]
    public float shelterRebuildDistance = 3f;
    public LayerMask shelterMask = ~0;

    Mesh dropMesh, splashMesh;
    Texture2D shelterTex;
    float[] heights;
    Vector3 shelterCenter = new Vector3(1e9f, 0f, 0f);
    NativeArray<RaycastCommand> commands;
    NativeArray<RaycastHit> results;
    JobHandle pending;
    bool pendingValid;
    Vector3 pendingCenter;

    static readonly int BoxId = Shader.PropertyToID("_RainBoxSize");
    static readonly int CenterId = Shader.PropertyToID("_RainCenter");
    static readonly int VelocityId = Shader.PropertyToID("_RainVelocity");
    static readonly int ParamsId = Shader.PropertyToID("_RainParams");
    static readonly int ShelterId = Shader.PropertyToID("_RainShelter");
    static readonly int ShelterRectId = Shader.PropertyToID("_RainShelterRect");

    void OnEnable()
    {
        dropMesh = BuildMesh(maxDrops, "RainDrops");
        splashMesh = BuildMesh(maxSplashes, "RainSplashes");
        heights = new float[shelterResolution * shelterResolution];
        shelterTex = new Texture2D(shelterResolution, shelterResolution, TextureFormat.RFloat, false, true)
        {
            name = "RainShelter", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
        };
        for (int i = 0; i < heights.Length; i++) heights[i] = -1e5f;
        shelterTex.SetPixelData(heights, 0);
        shelterTex.Apply(false);
        shelterCenter = new Vector3(1e9f, 0f, 0f);
    }

    void OnDisable()
    {
        if (pendingValid) { pending.Complete(); pendingValid = false; }
        if (commands.IsCreated) commands.Dispose();
        if (results.IsCreated) results.Dispose();
        DestroyImmediate(dropMesh);
        DestroyImmediate(splashMesh);
        DestroyImmediate(shelterTex);
    }

    // One quad per drop. Each vertex carries its drop's random seed (position in the box, speed and visibility
    // threshold) and its corner; the shader does the rest.
    static Mesh BuildMesh(int count, string name)
    {
        var rng = new System.Random(name.GetHashCode());
        var verts = new Vector3[count * 4];
        var seeds = new Vector4[count * 4];
        var corners = new Vector2[count * 4];
        var tris = new int[count * 6];
        Vector2[] c = { new Vector2(-1, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(-1, 1) };
        for (int i = 0; i < count; i++)
        {
            var seed = new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            for (int k = 0; k < 4; k++)
            {
                verts[i * 4 + k] = Vector3.zero;
                seeds[i * 4 + k] = seed;
                corners[i * 4 + k] = c[k];
            }
            tris[i * 6] = i * 4; tris[i * 6 + 1] = i * 4 + 1; tris[i * 6 + 2] = i * 4 + 2;
            tris[i * 6 + 3] = i * 4; tris[i * 6 + 4] = i * 4 + 2; tris[i * 6 + 5] = i * 4 + 3;
        }
        var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
        mesh.vertices = verts;
        mesh.SetUVs(0, seeds);
        mesh.SetUVs(1, corners);
        mesh.triangles = tris;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);   // placed by the shader
        return mesh;
    }

    void LateUpdate()
    {
        var cam = Camera.main;
#if UNITY_EDITOR
        if (!Application.isPlaying && UnityEditor.SceneView.lastActiveSceneView != null) cam = UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
        if (cam != null) Render(cam);
    }

    // Queues the rain for this frame around cam (also used by editor captures).
    public void Render(Camera cam)
    {
        Vector3 eye = cam.transform.position;
        UpdateShelter(eye);
        if (intensity <= 0.001f || rainMaterial == null) return;

        Vector3 wind = WindManager.Instance != null ? WindManager.Instance.GetWindAt(eye) * windInfluence : Vector3.zero;
        Vector3 velocity = new Vector3(wind.x, -fallSpeed, wind.z);
        float t = Application.isPlaying ? Time.time : (float)(System.DateTime.Now.TimeOfDay.TotalSeconds % 3600.0);
        foreach (var m in new[] { rainMaterial, splashMaterial })
        {
            if (m == null) continue;
            m.SetVector(BoxId, new Vector4(boxSize.x, boxSize.y, boxSize.z, intensity));
            m.SetVector(CenterId, new Vector4(eye.x, eye.y, eye.z, t));
            m.SetVector(VelocityId, new Vector4(velocity.x, velocity.y, velocity.z, streakLength * Mathf.Lerp(0.7f, 1.2f, intensity)));
            m.SetVector(ParamsId, new Vector4(streakWidth, splashRate, 0f, 0f));
            m.SetTexture(ShelterId, shelterTex);
            m.SetVector(ShelterRectId, new Vector4(shelterCenter.x - shelterSize / 2f, shelterCenter.z - shelterSize / 2f, 1f / shelterSize, 0f));
        }
        var rp = new RenderParams(rainMaterial) { worldBounds = new Bounds(eye, boxSize * 2f), shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, camera = cam };
        Graphics.RenderMesh(rp, dropMesh, 0, Matrix4x4.identity);
        if (splashMaterial != null)
        {
            var sp = new RenderParams(splashMaterial) { worldBounds = new Bounds(eye, boxSize * 2f), shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, camera = cam };
            Graphics.RenderMesh(sp, splashMesh, 0, Matrix4x4.identity);
        }
    }

    // ---------- Shelter map ----------
    // Highest surface under each texel, from raycasts straight down (jobified; results picked up next frame).
    void UpdateShelter(Vector3 eye)
    {
        if (pendingValid && pending.IsCompleted)
        {
            pending.Complete();
            pendingValid = false;
            int n = shelterResolution;
            for (int i = 0; i < n * n; i++)
                heights[i] = results[i].distance > 0f ? results[i].point.y : -1e5f;
            shelterTex.SetPixelData(heights, 0);
            shelterTex.Apply(false);
            shelterCenter = pendingCenter;
        }
        if (pendingValid || intensity <= 0.001f && Application.isPlaying) return;
        Vector3 flat = new Vector3(eye.x, 0f, eye.z);
        if ((flat - new Vector3(shelterCenter.x, 0f, shelterCenter.z)).sqrMagnitude < shelterRebuildDistance * shelterRebuildDistance) return;

        int res = shelterResolution;
        if (!commands.IsCreated || commands.Length != res * res)
        {
            if (commands.IsCreated) commands.Dispose();
            if (results.IsCreated) results.Dispose();
            commands = new NativeArray<RaycastCommand>(res * res, Allocator.Persistent);
            results = new NativeArray<RaycastHit>(res * res, Allocator.Persistent);
        }
        float texel = shelterSize / res;
        // Snap to the texel grid so the map doesn't shimmer as it follows the player
        Vector3 center = new Vector3(Mathf.Round(eye.x / texel) * texel, 0f, Mathf.Round(eye.z / texel) * texel);
        float top = eye.y + 150f;
        var query = new QueryParameters(shelterMask & ~(1 << 2), false, QueryTriggerInteraction.Ignore, false);
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                var origin = new Vector3(center.x - shelterSize / 2f + (x + 0.5f) * texel, top, center.z - shelterSize / 2f + (z + 0.5f) * texel);
                commands[z * res + x] = new RaycastCommand(origin, Vector3.down, query, 400f);
            }
        pending = RaycastCommand.ScheduleBatch(commands, results, 64);
        pendingValid = true;
        pendingCenter = center;
        if (!Application.isPlaying) { pending.Complete(); UpdateShelter(eye); }
    }

    // Is this spot under cover (a roof or tree above it)? For gameplay and audio.
    public bool IsSheltered(Vector3 position)
    {
        float u = (position.x - (shelterCenter.x - shelterSize / 2f)) / shelterSize, v = (position.z - (shelterCenter.z - shelterSize / 2f)) / shelterSize;
        if (u < 0f || u >= 1f || v < 0f || v >= 1f) return false;
        int res = shelterResolution;
        return heights[Mathf.Min(res - 1, (int)(v * res)) * res + Mathf.Min(res - 1, (int)(u * res))] > position.y + 0.5f;
    }
}
