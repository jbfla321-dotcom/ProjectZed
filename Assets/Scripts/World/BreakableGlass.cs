using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// A window pane that shatters when it's shot or something hits it hard. Lives on a unit cube scaled to the pane
// (x width, y height, z thickness). The pane is cut into radial shards around the hit point: some edge shards stay
// stuck in the frame, the rest fall (pushed along the bullet), settle, and a few stay on the floor for good.
[RequireComponent(typeof(BoxCollider))]
public class BreakableGlass : MonoBehaviour, IBulletReceiver
{
    public SoundEvent breakSound;
    [Tooltip("Material for the shards; the pane's own if empty.")]
    public Material shardMaterial;
    [Tooltip("A rigidbody hitting the pane at least this fast (m/s) breaks it.")]
    public float breakSpeed = 3f;
    [Tooltip("Already broken when the game starts (a few shards in the frame and on the floor).")]
    public bool startBroken;
    [Range(0f, 1f), Tooltip("Chance that a shard touching the frame stays stuck in it.")]
    public float remnantChance = 0.45f;
    [Tooltip("Falling shards live this long (s) before settling or vanishing.")]
    public float shardLife = 6f;
    [Tooltip("Up to this many settled shards stay on the floor.")]
    public int keepOnFloor = 6;

    public bool Broken { get; private set; }

    const int MaxLiveShards = 300;
    static int liveShards;

    void Start()
    {
        if (startBroken) Shatter(transform.position, Vector3.zero, true);
    }

    public void OnBulletHit(Vector3 point, Vector3 direction, float power) =>
        Shatter(point, direction * Mathf.Lerp(1.5f, 4f, Mathf.Clamp01(power)));

    void OnCollisionEnter(Collision c)
    {
        if (Broken || c.relativeVelocity.magnitude < breakSpeed) return;
        Vector3 push = c.rigidbody != null ? c.rigidbody.linearVelocity * 0.3f : Vector3.zero;
        Shatter(c.GetContact(0).point, push);
    }

    public void Shatter(Vector3 point, Vector3 push, bool silent = false)
    {
        if (Broken) return;
        Broken = true;
        GetComponent<Collider>().enabled = false;
        var rend = GetComponent<MeshRenderer>();
        rend.enabled = false;
        if (!silent && breakSound != null) SoundManager.Play(breakSound, point);

        Vector3 s = transform.lossyScale;
        float w = s.x, h = s.y, t = Mathf.Max(s.z, 0.003f);
        Vector3 lp = transform.InverseTransformPoint(point);
        var c = new Vector2(Mathf.Clamp(lp.x, -0.42f, 0.42f) * w, Mathf.Clamp(lp.y, -0.42f, 0.42f) * h);
        Material mat = shardMaterial != null ? shardMaterial : rend.sharedMaterial;

        var falling = new List<Rigidbody>();
        int onFloor = 0;
        foreach (var poly in Fracture(c, w, h))
        {
            if (TouchesFrame(poly, w, h) && Random.value < remnantChance)
            {
                Shard(poly, mat, t);   // stuck in the frame
                continue;
            }
            if (silent)
            {
                if (onFloor++ < keepOnFloor) FloorShard(poly, mat, t);
                continue;
            }
            if (liveShards >= MaxLiveShards) continue;
            var go = Shard(poly, mat, t);
            go.layer = WorldItems.Layer;
            go.AddComponent<BoxCollider>();
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.05f + Area(poly) * 6f;
            float near = 1f - Mathf.Clamp01(Vector2.Distance(Centroid(poly), c) / Mathf.Max(w, h));
            rb.linearVelocity = push * (0.4f + near) + Random.insideUnitSphere * 0.6f;
            rb.angularVelocity = Random.insideUnitSphere * 8f;
            falling.Add(rb);
            liveShards++;
        }
        if (falling.Count > 0) StartCoroutine(Settle(falling));
    }

    IEnumerator Settle(List<Rigidbody> shards)
    {
        yield return new WaitForSeconds(shardLife);
        int kept = 0;
        var fading = new List<Transform>();
        foreach (var rb in shards)
        {
            liveShards--;
            if (rb == null) continue;
            if (kept < keepOnFloor && rb.linearVelocity.sqrMagnitude < 0.01f)
            {
                kept++;
                Destroy(rb.GetComponent<Collider>());
                Destroy(rb);
            }
            else fading.Add(rb.transform);
        }
        for (float f = 1f; f > 0f; f -= Time.deltaTime * 2f)
        {
            foreach (var tr in fading) if (tr != null) tr.localScale = Vector3.one * f;
            yield return null;
        }
        foreach (var tr in fading) if (tr != null) Destroy(tr.gameObject);
    }

    // A shard in the pane's plane, at its place in the pane.
    GameObject Shard(Vector2[] poly, Material mat, float thickness)
    {
        Vector2 centre = Centroid(poly);
        var go = new GameObject("GlassShard");
        go.transform.SetParent(transform.parent, false);
        go.transform.SetPositionAndRotation(transform.TransformPoint(new Vector3(centre.x / transform.lossyScale.x, centre.y / transform.lossyScale.y, 0f)), transform.rotation);
        go.AddComponent<MeshFilter>().sharedMesh = Extrude(poly, centre, thickness);
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    // A shard lying flat on the floor just inside the window.
    void FloorShard(Vector2[] poly, Material mat, float thickness)
    {
        Vector3 from = transform.position - transform.forward * Random.Range(0.2f, 0.8f) + transform.right * Random.Range(-0.4f, 0.4f) * transform.lossyScale.x;
        if (!Physics.Raycast(from, Vector3.down, out var hit, 3f, CharacterLayers.Environment, QueryTriggerInteraction.Ignore)) return;
        var go = Shard(poly, mat, thickness);
        go.transform.SetPositionAndRotation(hit.point + Vector3.up * (thickness * 0.5f + 0.002f),
            Quaternion.Euler(90f, Random.Range(0f, 360f), 0f));
    }

    // ---------- Fracture ----------
    // Radial cracks from the hit point to the frame, cut by a few rings: every piece is convex.
    static List<Vector2[]> Fracture(Vector2 c, float w, float h)
    {
        float hw = w / 2f, hh = h / 2f;
        int rays = Random.Range(7, 11);
        var angles = new float[rays];
        float a0 = Random.value * Mathf.PI * 2f;
        for (int i = 0; i < rays; i++)
            angles[i] = a0 + (i + Random.Range(-0.3f, 0.3f)) * Mathf.PI * 2f / rays;
        var dirs = new Vector2[rays];
        var reach = new float[rays];
        for (int i = 0; i < rays; i++)
        {
            dirs[i] = new Vector2(Mathf.Cos(angles[i]), Mathf.Sin(angles[i]));
            reach[i] = ToFrame(c, dirs[i], hw, hh);
        }
        float[] rings = { Random.Range(0.035f, 0.07f), Random.Range(0.13f, 0.24f), Random.Range(0.35f, 0.55f) };
        var corners = new[] { new Vector2(hw, hh), new Vector2(-hw, hh), new Vector2(-hw, -hh), new Vector2(hw, -hh) };

        var pieces = new List<Vector2[]>();
        for (int i = 0; i < rays; i++)
        {
            int j = (i + 1) % rays;
            Vector2 At(int k, float r) => c + dirs[k] * Mathf.Min(r, reach[k] * 0.96f);
            Vector2 prevI = c, prevJ = c;
            for (int k = 0; k < rings.Length; k++)
            {
                Vector2 pi = At(i, rings[k]), pj = At(j, rings[k]);
                AddPiece(pieces, k == 0 ? new[] { c, pi, pj } : new[] { prevI, pi, pj, prevJ });
                prevI = pi; prevJ = pj;
            }
            // Outer piece out to the frame, with any corners between the two cracks.
            var outer = new List<Vector2> { prevI, c + dirs[i] * reach[i] };
            float span = Mathf.Repeat(angles[j] - angles[i], Mathf.PI * 2f);
            var between = new List<(float, Vector2)>();
            foreach (var corner in corners)
            {
                float d = Mathf.Repeat(Mathf.Atan2(corner.y - c.y, corner.x - c.x) - angles[i], Mathf.PI * 2f);
                if (d > 0f && d < span) between.Add((d, corner));
            }
            between.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            foreach (var b in between) outer.Add(b.Item2);
            outer.Add(c + dirs[j] * reach[j]);
            outer.Add(prevJ);
            AddPiece(pieces, outer.ToArray());
        }
        return pieces;
    }

    static void AddPiece(List<Vector2[]> pieces, Vector2[] poly)
    {
        if (Area(poly) > 2e-4f) pieces.Add(poly);
    }

    static float ToFrame(Vector2 c, Vector2 d, float hw, float hh)
    {
        float tx = d.x > 1e-5f ? (hw - c.x) / d.x : d.x < -1e-5f ? (-hw - c.x) / d.x : float.MaxValue;
        float ty = d.y > 1e-5f ? (hh - c.y) / d.y : d.y < -1e-5f ? (-hh - c.y) / d.y : float.MaxValue;
        return Mathf.Min(tx, ty);
    }

    static bool TouchesFrame(Vector2[] poly, float w, float h)
    {
        foreach (var p in poly)
            if (Mathf.Abs(p.x) > w / 2f - 1e-3f || Mathf.Abs(p.y) > h / 2f - 1e-3f) return true;
        return false;
    }

    static float Area(Vector2[] poly)
    {
        float a = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 p = poly[i], q = poly[(i + 1) % poly.Length];
            a += p.x * q.y - q.x * p.y;
        }
        return Mathf.Abs(a) * 0.5f;
    }

    static Vector2 Centroid(Vector2[] poly)
    {
        Vector2 sum = Vector2.zero;
        foreach (var p in poly) sum += p;
        return sum / poly.Length;
    }

    // A slab of glass: the polygon on both faces plus its edges, flat shaded, centred on the polygon's centroid.
    static Mesh Extrude(Vector2[] poly, Vector2 centre, float thickness)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();
        float hz = thickness / 2f;
        Vector3 mid = Vector3.zero;
        void Face(params Vector3[] f)
        {
            Vector3 fc = Vector3.zero;
            foreach (var v in f) fc += v;
            fc /= f.Length;
            int start = verts.Count;
            verts.AddRange(f);
            bool flip = Vector3.Dot(Vector3.Cross(f[1] - f[0], f[2] - f[0]), fc - mid) < 0f;
            for (int i = 1; i < f.Length - 1; i++)
            {
                tris.Add(start);
                tris.Add(start + (flip ? i + 1 : i));
                tris.Add(start + (flip ? i : i + 1));
            }
        }
        int n = poly.Length;
        var front = new Vector3[n];
        var back = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 p = poly[i] - centre;
            front[i] = new Vector3(p.x, p.y, -hz);
            back[i] = new Vector3(p.x, p.y, hz);
        }
        Face(front);
        Face(back);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Face(front[i], front[j], back[j], back[i]);
        }
        var mesh = new Mesh { name = "GlassShard" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
