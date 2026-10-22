using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Attach this to an empty GameObject in any 3D scene, then press Play.
// Fly forward through an asteroid field: dodge, and shoot what you can.
public class SpaceShooter3D : MonoBehaviour
{
    class Rock
    {
        public Transform t;
        public float speed;
        public float radius;
        public Vector3 spin;
    }

    [Header("Tuning")]
    public float playerSpeed = 11f;
    public float bulletSpeed = 55f;
    public float fireCooldown = 0.15f;
    public int startingLives = 3;
    public float boundsX = 7f;
    public float boundsY = 3.5f;

    Transform ship;
    readonly List<Transform> bullets = new List<Transform>();
    readonly List<Rock> rocks = new List<Rock>();
    readonly List<Transform> stars = new List<Transform>();
    readonly List<Transform> flames = new List<Transform>();

    float fireTimer, spawnTimer, elapsed, hitFlash;
    float rollAngle;
    int score, lives, best;
    bool gameOver;

    const float SpawnZ = 80f;
    const float KillZ = -14f;

    void Start()
    {
        SetupCamera();
        SetupLight();
        best = PlayerPrefs.GetInt("best3d", 0);
        lives = startingLives;
        BuildShip();
        BuildStars();
    }

    void Update()
    {
        if (gameOver)
        {
            if (Input.GetKeyDown(KeyCode.R))
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            return;
        }

        elapsed += Time.deltaTime;
        hitFlash = Mathf.Max(0f, hitFlash - Time.deltaTime);

        MoveShip();
        PulseFlames();
        Shoot();
        Spawn();
        UpdateBullets();
        UpdateRocks();
        UpdateStars();
    }

    // ---------- Setup ----------

    void SetupCamera()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }
        cam.orthographic = false;
        cam.fieldOfView = 60f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.02f, 0.02f, 0.07f);
        cam.transform.position = new Vector3(0, 2.2f, -9f);
        cam.transform.LookAt(new Vector3(0, 0, 12f));
    }

    void SetupLight()
    {
#if UNITY_2023_1_OR_NEWER
        bool hasLight = FindFirstObjectByType<Light>() != null;
#else
        bool hasLight = FindObjectOfType<Light>() != null;
#endif
        if (!hasLight)
        {
            var go = new GameObject("Sun");
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            go.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
        }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.45f);
    }

    void BuildShip()
    {
        var root = new GameObject("Ship");
        root.transform.position = new Vector3(0, 0, 0);
        ship = root.transform;

        Color hull = new Color(0.85f, 0.88f, 0.95f);
        Color accent = new Color(0.1f, 0.45f, 0.9f);
        Color dark = new Color(0.18f, 0.2f, 0.28f);

        // Fuselage: a stretched oval with a pointed nose cone
        Prim(PrimitiveType.Sphere, new Vector3(0, 0, -0.1f), new Vector3(0.75f, 0.5f, 2.4f), hull, ship);
        Transform nose = Prim(PrimitiveType.Capsule, new Vector3(0, 0, 1.25f), new Vector3(0.28f, 0.55f, 0.28f), accent, ship);
        nose.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // Cockpit canopy
        Glow(PrimitiveType.Sphere, new Vector3(0, 0.22f, 0.4f), new Vector3(0.45f, 0.32f, 0.95f), new Color(0.15f, 0.6f, 1f), 0.4f, ship);

        // Swept-back wings with colored leading stripes
        for (int side = -1; side <= 1; side += 2)
        {
            Transform wing = Prim(PrimitiveType.Cube, new Vector3(side * 0.95f, -0.05f, -0.25f), new Vector3(1.6f, 0.07f, 0.75f), accent, ship);
            wing.localRotation = Quaternion.Euler(0f, side * 25f, side * -4f);

            Transform stripe = Prim(PrimitiveType.Cube, new Vector3(side * 1.0f, -0.01f, -0.1f), new Vector3(1.3f, 0.075f, 0.12f), hull, ship);
            stripe.localRotation = wing.localRotation;

            // Wingtip light: red on the left, green on the right
            Color tip = side < 0 ? new Color(1f, 0.15f, 0.15f) : new Color(0.2f, 1f, 0.3f);
            Glow(PrimitiveType.Sphere, new Vector3(side * 1.62f, -0.05f, -0.58f), Vector3.one * 0.16f, tip, 2.5f, ship);

            // Tail fin, tilted outward
            Transform fin = Prim(PrimitiveType.Cube, new Vector3(side * 0.38f, 0.3f, -0.95f), new Vector3(0.07f, 0.55f, 0.5f), dark, ship);
            fin.localRotation = Quaternion.Euler(0f, 0f, side * -18f);

            // Engine pod with glowing exhaust flame
            Transform pod = Prim(PrimitiveType.Cylinder, new Vector3(side * 0.5f, -0.08f, -0.95f), new Vector3(0.32f, 0.4f, 0.32f), dark, ship);
            pod.localRotation = Quaternion.Euler(90f, 0f, 0f);

            Transform flame = Glow(PrimitiveType.Sphere, new Vector3(side * 0.5f, -0.08f, -1.55f), new Vector3(0.22f, 0.22f, 0.9f), new Color(1f, 0.55f, 0.1f), 3f, ship);
            flames.Add(flame);
        }

        // Center engine glow
        flames.Add(Glow(PrimitiveType.Sphere, new Vector3(0, 0, -1.45f), new Vector3(0.26f, 0.26f, 0.8f), new Color(0.4f, 0.8f, 1f), 3f, ship));
    }

    // Like Prim, but the surface glows (emission), used for lights, flames and the canopy.
    Transform Glow(PrimitiveType type, Vector3 pos, Vector3 scale, Color color, float intensity, Transform parent)
    {
        Transform t = Prim(type, pos, scale, color, parent);
        Material m = t.GetComponent<Renderer>().material;
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", color * intensity);
        return t;
    }

    void PulseFlames()
    {
        for (int i = 0; i < flames.Count; i++)
        {
            float f = 1f + 0.25f * Mathf.Sin(Time.time * 40f + i * 1.7f) + Random.Range(-0.08f, 0.08f);
            Vector3 s = flames[i].localScale;
            flames[i].localScale = new Vector3(s.x, s.y, 0.85f * f);
        }
    }

    void BuildStars()
    {
        for (int i = 0; i < 140; i++)
        {
            float s = Random.Range(0.05f, 0.15f);
            Vector3 p = new Vector3(Random.Range(-45f, 45f), Random.Range(-28f, 28f), Random.Range(-10f, 100f));
            stars.Add(Prim(PrimitiveType.Cube, p, new Vector3(s, s, s * 6f), Color.white, null));
        }
    }

    // ---------- Gameplay ----------

    void MoveShip()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        Vector3 dir = new Vector3(h, v, 0f);
        if (dir.sqrMagnitude > 1f) dir.Normalize();

        Vector3 p = ship.position + dir * playerSpeed * Time.deltaTime;
        p.x = Mathf.Clamp(p.x, -boundsX, boundsX);
        p.y = Mathf.Clamp(p.y, -boundsY, boundsY);
        ship.position = p;

        // Bank into turns and pitch slightly with vertical movement
        rollAngle = Mathf.Lerp(rollAngle, -h * 35f, 8f * Time.deltaTime);
        float pitch = -v * 12f;
        ship.rotation = Quaternion.Euler(pitch, 0f, rollAngle);

        // Blink while recently hit
        bool visible = hitFlash <= 0f || ((int)(hitFlash * 20f) % 2 == 0);
        foreach (var r in ship.GetComponentsInChildren<Renderer>()) r.enabled = visible;
    }

    void Shoot()
    {
        fireTimer -= Time.deltaTime;
        if (Input.GetKey(KeyCode.Space) && fireTimer <= 0f)
        {
            fireTimer = fireCooldown;
            Transform b = Prim(PrimitiveType.Capsule, ship.position + new Vector3(0, 0, 1.2f),
                               new Vector3(0.18f, 0.5f, 0.18f), new Color(1f, 0.9f, 0.2f), null);
            b.rotation = Quaternion.Euler(90f, 0f, 0f); // capsule points along +Z
            bullets.Add(b);
        }
    }

    void Spawn()
    {
        spawnTimer -= Time.deltaTime;
        if (spawnTimer > 0f) return;

        spawnTimer = Mathf.Max(0.22f, 0.9f - elapsed * 0.01f);

        float size = Random.Range(1.0f, 2.6f);
        Vector3 pos = new Vector3(Random.Range(-boundsX - 1f, boundsX + 1f),
                                  Random.Range(-boundsY - 1f, boundsY + 1f), SpawnZ);
        float shade = Random.Range(0.35f, 0.6f);
        Transform t = Prim(PrimitiveType.Sphere, pos, Vector3.one * size,
                           new Color(shade + 0.15f, shade, shade - 0.1f), null);

        rocks.Add(new Rock
        {
            t = t,
            radius = size * 0.5f,
            speed = Random.Range(14f, 22f) + elapsed * 0.15f,
            spin = Random.insideUnitSphere * 90f
        });
    }

    void UpdateBullets()
    {
        for (int i = bullets.Count - 1; i >= 0; i--)
        {
            Transform b = bullets[i];
            b.position += Vector3.forward * bulletSpeed * Time.deltaTime;

            bool remove = b.position.z > SpawnZ + 10f;

            for (int j = rocks.Count - 1; j >= 0 && !remove; j--)
            {
                float hitDist = rocks[j].radius + 0.3f;
                if ((b.position - rocks[j].t.position).sqrMagnitude < hitDist * hitDist)
                {
                    Destroy(rocks[j].t.gameObject);
                    rocks.RemoveAt(j);
                    score += 10;
                    remove = true;
                }
            }

            if (remove)
            {
                Destroy(b.gameObject);
                bullets.RemoveAt(i);
            }
        }
    }

    void UpdateRocks()
    {
        for (int i = rocks.Count - 1; i >= 0; i--)
        {
            Rock r = rocks[i];
            r.t.position += Vector3.back * r.speed * Time.deltaTime;
            r.t.Rotate(r.spin * Time.deltaTime);

            float hitDist = r.radius + 0.8f;
            bool hitShip = hitFlash <= 0f &&
                           (r.t.position - ship.position).sqrMagnitude < hitDist * hitDist;

            if (hitShip)
            {
                Destroy(r.t.gameObject);
                rocks.RemoveAt(i);
                lives--;
                hitFlash = 1.2f;
                if (lives <= 0) EndGame();
            }
            else if (r.t.position.z < KillZ)
            {
                Destroy(r.t.gameObject);
                rocks.RemoveAt(i);
            }
        }
    }

    void UpdateStars()
    {
        float speed = 30f + elapsed * 0.3f;
        foreach (Transform s in stars)
        {
            s.position += Vector3.back * speed * Time.deltaTime;
            if (s.position.z < -12f)
                s.position = new Vector3(Random.Range(-45f, 45f), Random.Range(-28f, 28f), 100f);
        }
    }

    void EndGame()
    {
        gameOver = true;
        foreach (var r in ship.GetComponentsInChildren<Renderer>()) r.enabled = true;
        if (score > best)
        {
            best = score;
            PlayerPrefs.SetInt("best3d", best);
        }
    }

    // ---------- Helpers ----------

    // Creates a primitive with no collider (we do our own distance checks).
    // Copies the primitive's default material so it works in Built-in, URP and HDRP.
    Transform Prim(PrimitiveType type, Vector3 pos, Vector3 scale, Color color, Transform parent)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);

        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;

        Renderer rend = go.GetComponent<Renderer>();
        Material m = new Material(rend.sharedMaterial);
        m.color = color;
        rend.material = m;
        return go.transform;
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(15, 10, 400, 40), "Score: " + score + "    Best: " + best, style);
        GUI.Label(new Rect(15, 40, 400, 40), "Lives: " + lives, style);

        if (gameOver)
        {
            var big = new GUIStyle(style) { fontSize = 44, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0, Screen.height / 2f - 60, Screen.width, 60), "GAME OVER", big);
            var small = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0, Screen.height / 2f, Screen.width, 40), "Press R to restart", small);
        }
    }
}
