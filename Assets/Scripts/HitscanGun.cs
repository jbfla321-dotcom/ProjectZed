using UnityEngine;
using UnityEngine.InputSystem;

// The player's rifle: left click to fire (hold for auto), R to reload, Page Up / Page Down to change the zero.
// (Named for when shots were instant rays; they're now real bullets, flown by BulletWorld with Arma-style
// ballistics: drop, drag, wind, penetration and ricochets.)
// Lives on the player's eye pivot. The rifle you see is the character's (PlayerCharacterAnimator moves it,
// including recoil and the reload pose); this script owns damage, tracers, bullet holes and the flash.
// The rounds are in the rifle's magazine (CarriedRifle), so they stay with the rifle if it's dropped.
// Reloading needs a spare magazine from the player's MagazinePouch; the one coming out is dropped.
// Tracers and bullet holes are pooled, so firing creates no garbage.
public class HitscanGun : MonoBehaviour
{
    [Header("References")]
    public Transform muzzle;
    public Light muzzleLight;
    public GameObject muzzleFlash;
    public Material tracerMaterial;
    public Material impactMaterial;
    public FPSController player;
    [Tooltip("Where shots are traced from (the view camera), so shots go where the crosshair points. Empty = this transform.")]
    public Transform aimOrigin;

    [Header("Shooting")]
    [Tooltip("Damage at muzzle velocity; a slower bullet does proportionally less, as in Arma.")]
    public float damage = 25f;
    [Tooltip("How far the crosshair's aim point is looked for (m).")]
    public float range = 200f;
    public float fireRate = 10f;
    public bool automatic = true;
    public float spread = 0.6f;
    public float impactForce = 8f;
    public LayerMask hitMask = ~0;

    [Header("Ballistics")]
    public BulletType ammo = new BulletType();
    [Tooltip("Barrel length (mm); sets the muzzle velocity from the ammo's table. 368 = 14.5 inch carbine.")]
    public float barrelLength = 368.3f;
    [Tooltip("Ranges (m) the sights can be zeroed at; Page Up / Page Down steps through them.")]
    public float[] zeroRanges = { 100f, 200f, 300f, 400f, 500f, 600f, 800f };
    public int zeroIndex = 0;

    [Header("Ammo")]
    public int magazineSize = 30;
    public float reloadTime = 2.2f;

    [Header("Feel")]
    [Tooltip("View kick per shot (degrees).")]
    public float recoilPitch = 0.8f;
    public float flashDuration = 0.04f;
    [Tooltip("Longest tracer streak (m).")]
    public float tracerLength = 10f;
    [Tooltip("Tracers in flight at once (one per bullet).")]
    public int maxTracers = 32;
    public int maxImpactMarks = 40;

    CarriedRifle rifle;
    MagazinePouch pouch;
    int looseAmmo; // rounds when there's no rifle model to hold them
    int incomingRounds; // the magazine going in during a reload
    int ejectedRounds = -1; // the magazine that came out, until it's dropped (-1: none)
    float noMagazinesUntil;
    float nextFireTime;
    float reloadEndTime = -1f;
    float flashEndTime;
    readonly RaycastHit[] hitBuffer = new RaycastHit[16];
    float zeroAngle;
    int zeroAngleFor = -1;
    CharacterController playerBody;

    LineRenderer[] tracers;
    int nextTracer;
    Transform[] impactMarks;
    int nextImpact;
    Mesh quadMesh;
    Transform poolRoot;

    GUIStyle hudStyle;
    string hudText;
    int hudAmmo = -1, hudSpares = -2;
    bool hudReloading, hudNoMags;
    float hudZero;

    public bool IsReloading => reloadEndTime > 0f;
    // In the hands (not dropped).
    public bool Held => rifle == null || rifle.IsHeld;

    int Ammo
    {
        get => rifle != null ? rifle.rounds : looseAmmo;
        set { if (rifle != null) rifle.rounds = value; else looseAmmo = value; }
    }
    // 0 at the start of a reload, 1 when the new magazine is in.
    public float ReloadProgress => IsReloading ? Mathf.Clamp01(1f - (reloadEndTime - Time.time) / reloadTime) : 0f;

    public event System.Action Fired;
    public event System.Action ReloadStarted;
    // Trigger pulled on an empty magazine.
    public event System.Action DryFired;
    // The old magazine left the rifle during a reload.
    public event System.Action MagazineEjected;
    // Each simulation step of each bullet in flight (from, to), and everything a bullet strikes (sounds, effects).
    public event System.Action<Bullet, Vector3, Vector3> BulletMoved;
    public event System.Action<RaycastHit> ShotHit;
    // Each round fired: where it starts (the eye) and its velocity, so other players can fly the same bullet.
    public event System.Action<Vector3, Vector3> Shot;

    // Another player's rifle (multiplayer, NetPuppet): no input or HUD; shots and reloads come from the network.
    [System.NonSerialized] public bool remote;

    public int Rounds => Ammo;

    void Awake()
    {
        looseAmmo = magazineSize;
        if (player == null) player = GetComponentInParent<FPSController>();
        if (player != null)
        {
            pouch = player.GetComponent<MagazinePouch>();
            playerBody = player.GetComponent<CharacterController>();
            var body = player.GetComponentInChildren<PlayerCharacterAnimator>(true);
            if (body != null) rifle = CarriedRifle.On(body.carriedGun);
            if (rifle != null) rifle.magazineSize = magazineSize;
        }
        SetFlash(false);
        BuildPools();
    }

    void Update()
    {
        if (remote)
        {
            if (IsReloading && Time.time >= reloadEndTime) reloadEndTime = -1f;
            if (Time.time >= flashEndTime) SetFlash(false);
            return;
        }
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        if (IsReloading && Time.time >= reloadEndTime)
        {
            EjectMagazine(Vector3.zero); // if the reload animation didn't already
            Ammo = incomingRounds;
            reloadEndTime = -1f;
        }
        if (!Held) return;

        if (keyboard.rKey.wasPressedThisFrame && !IsReloading && Ammo < magazineSize)
            StartReload();
        if (keyboard.pageUpKey.wasPressedThisFrame) zeroIndex = Mathf.Min(zeroIndex + 1, zeroRanges.Length - 1);
        if (keyboard.pageDownKey.wasPressedThisFrame) zeroIndex = Mathf.Max(zeroIndex - 1, 0);

        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool trigger = automatic ? mouse.leftButton.isPressed : mouse.leftButton.wasPressedThisFrame;
        bool freeLooking = player != null && player.IsFreeLooking;
        if (locked && trigger && !freeLooking && !IsReloading && Time.time >= nextFireTime)
        {
            if (Ammo > 0) Fire();
            else if (mouse.leftButton.wasPressedThisFrame)
            {
                DryFired?.Invoke();
                StartReload();
            }
        }

        if (Time.time >= flashEndTime) SetFlash(false);
    }

    public float ZeroRange => zeroRanges.Length > 0 ? zeroRanges[Mathf.Clamp(zeroIndex, 0, zeroRanges.Length - 1)] : 0f;

    // Muzzle velocity in today's air (powder temperature follows the air around the rifle).
    public float MuzzleVelocity => ammo.MuzzleVelocity(barrelLength, BulletWorld.Instance.TemperatureAt(transform.position));

    void Fire()
    {
        // Keep a steady beat while the trigger is held (adding to the last shot's time rather than now, so frame
        // timing doesn't stretch each gap); after a pause, start counting from now.
        float interval = 1f / fireRate;
        nextFireTime = Time.time - nextFireTime < interval ? nextFireTime + interval : Time.time + interval;
        Ammo--;
        Fired?.Invoke();

        // Random spread inside a cone around the view direction.
        Vector2 jitter = Random.insideUnitCircle * Mathf.Tan(spread * Mathf.Deg2Rad);
        Transform aim = aimOrigin != null ? aimOrigin : transform;
        Vector3 dir = (aim.forward + aim.right * jitter.x + aim.up * jitter.y).normalized;
        // The barrel points slightly up from the line of sight, so the dropping bullet meets it at the zero range.
        if (zeroAngleFor != zeroIndex)
        {
            zeroAngle = Ballistics.ZeroAngle(ammo, ammo.MuzzleVelocity(barrelLength, 15f), ZeroRange);
            zeroAngleFor = zeroIndex;
        }
        dir = Quaternion.AngleAxis(-zeroAngle, aim.right) * dir;

        // The bullet flies from the eye so it goes where the crosshair points; its tracer starts at the muzzle.
        // It also carries the shooter's own movement.
        Vector3 from = muzzle != null ? muzzle.position : transform.position;
        Transform shooter = player != null ? player.transform : transform.root;
        Vector3 velocity = dir * MuzzleVelocity + (playerBody != null ? playerBody.velocity : Vector3.zero);
        var bullet = BulletWorld.Instance.Fire(this, shooter, ammo, hitMask & CharacterLayers.NotCharacterCapsules,
            aim.position, velocity, damage, from - aim.position);
        bullet.tracer = TakeTracer();
        SetFlash(true);
        flashEndTime = Time.time + flashDuration;
        Shot?.Invoke(aim.position, velocity);

        if (player != null) player.AddRecoil(recoilPitch, Random.Range(-0.3f, 0.3f) * recoilPitch);
    }

    // Another player fired: the same bullet (from their eye, with their spread and zero), drawn from this rifle's muzzle.
    public void RemoteFire(Vector3 origin, Vector3 velocity)
    {
        Fired?.Invoke();
        Vector3 from = muzzle != null ? muzzle.position : transform.position;
        var bullet = BulletWorld.Instance.Fire(this, transform.root, ammo, hitMask & CharacterLayers.NotCharacterCapsules,
            origin, velocity, damage, from - origin);
        bullet.tracer = TakeTracer();
        SetFlash(true);
        flashEndTime = Time.time + flashDuration;
    }

    // Another player started a reload (the magazine swap is animated; the rounds are theirs to count).
    public void RemoteReload()
    {
        reloadEndTime = Time.time + reloadTime;
        ReloadStarted?.Invoke();
    }

    void OnDestroy()
    {
        if (poolRoot != null) Destroy(poolRoot.gameObject);
    }

    // World point under the crosshair: what a shot would hit, ignoring spread.
    public Vector3 GetAimPoint()
    {
        Transform aim = aimOrigin != null ? aimOrigin : transform;
        return TryGetHit(aim.position, aim.forward, out RaycastHit hit) ? hit.point : aim.position + aim.forward * range;
    }

    // Closest hit that isn't part of the shooter (the ray starts inside the player's capsule).
    bool TryGetHit(Vector3 origin, Vector3 dir, out RaycastHit closest)
    {
        closest = default;
        int count = Physics.RaycastNonAlloc(origin, dir, hitBuffer, range, hitMask & CharacterLayers.NotCharacterCapsules, QueryTriggerInteraction.Ignore);
        Transform self = player != null ? player.transform : transform.root;
        float best = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var h = hitBuffer[i];
            if (h.collider.transform.IsChildOf(self) || h.distance >= best) continue;
            best = h.distance;
            closest = h;
        }
        return best < float.MaxValue;
    }

    void StartReload()
    {
        int next = pouch != null ? pouch.TakeFullest() : magazineSize;
        if (next < 0)
        {
            noMagazinesUntil = Time.time + 1.5f;
            return;
        }
        incomingRounds = next;
        ejectedRounds = Ammo;
        Ammo = 0;
        reloadEndTime = Time.time + reloadTime;
        ReloadStarted?.Invoke();
    }

    // Drops the magazine that came out of the rifle (once per reload), as a loose magazine with its
    // remaining rounds. The reload animation calls this when the hand lets go of it.
    public void EjectMagazine(Vector3 velocity)
    {
        if (ejectedRounds < 0) return;
        if (pouch != null && rifle != null && rifle.Magazine != null)
            DroppedMagazine.Spawn(rifle.Magazine, ejectedRounds, magazineSize, velocity);
        ejectedRounds = -1;
        MagazineEjected?.Invoke();
    }

    void SetFlash(bool on)
    {
        if (muzzleLight != null) muzzleLight.enabled = on;
        if (muzzleFlash != null)
        {
            muzzleFlash.SetActive(on);
            if (on) muzzleFlash.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        }
    }

    // ---------- Bullets (called by BulletWorld) ----------
    public void OnBulletMoved(Bullet b, Vector3 from, Vector3 to)
    {
        BulletMoved?.Invoke(b, from, to);
        if (b.tracer >= 0) DrawTracer(b);
    }

    public void OnBulletHit(Bullet b, RaycastHit hit, Vector3 dir, float speed)
    {
        // Damage and knock follow the bullet's remaining speed (Arma scales hit damage by impact speed).
        float power = speed / Mathf.Max(b.muzzleSpeed, 1f);
        var target = hit.collider.GetComponentInParent<Target>();
        if (target != null) target.TakeDamage(b.damage * power, hit.point, dir);

        // Characters are hit on their limbs (the capsule is skipped); they handle their own knockback.
        var victim = hit.collider.GetComponentInParent<CharacterHealth>();
        if (victim != null) victim.TakeShot(b.damage * power, hit.point, dir, hit.collider);
        else if (hit.rigidbody != null)
            hit.rigidbody.AddForceAtPosition(dir * impactForce * power, hit.point, ForceMode.Impulse);

        hit.collider.GetComponentInParent<IBulletReceiver>()?.OnBulletHit(hit.point, dir, power);

        ShotHit?.Invoke(hit);
        PlaceImpact(hit);
    }

    // Came out the far side of something it went through.
    public void OnBulletExited(Bullet b, RaycastHit exit) => PlaceImpact(exit);

    public void OnBulletEnded(Bullet b)
    {
        if (b.tracer >= 0) tracers[b.tracer].enabled = false;
        b.tracer = -1;
    }

    // ---------- Pools ----------
    void BuildPools()
    {
        poolRoot = new GameObject("GunEffects").transform;
        tracers = new LineRenderer[Mathf.Max(maxTracers, 1)];
        for (int i = 0; i < tracers.Length; i++)
        {
            var line = new GameObject("Tracer").AddComponent<LineRenderer>();
            line.transform.SetParent(poolRoot, false);
            line.positionCount = 2;
            line.startWidth = 0.02f;
            line.endWidth = 0.01f;
            line.sharedMaterial = tracerMaterial;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            tracers[i] = line;
        }

        var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);
        impactMarks = new Transform[maxImpactMarks];
    }

    // A free tracer line, or -1 if every one is on a bullet in flight (that round flies untraced).
    int TakeTracer()
    {
        for (int n = 0; n < tracers.Length; n++)
        {
            int i = (nextTracer + n) % tracers.Length;
            if (tracers[i].enabled) continue;
            nextTracer = (i + 1) % tracers.Length;
            tracers[i].enabled = true;
            tracers[i].positionCount = 0; // drawn from the bullet's first step
            return i;
        }
        return -1;
    }

    // A streak behind the bullet. Near the rifle it's shifted from the true path (which starts at the eye)
    // to start at the muzzle, blending out over the first 20 m. It widens with distance so it stays visible.
    void DrawTracer(Bullet b)
    {
        var line = tracers[b.tracer];
        float speed = b.Speed;
        if (speed < 1f) return;
        Vector3 back = b.velocity / speed;
        float length = Mathf.Min(tracerLength, b.distance);
        Vector3 head = b.position + b.visualOffset * Mathf.Clamp01(1f - b.distance / 20f);
        Vector3 tail = b.position - back * length + b.visualOffset * Mathf.Clamp01(1f - (b.distance - length) / 20f);
        line.positionCount = 2;
        line.SetPosition(0, tail);
        line.SetPosition(1, head);
        var cam = Camera.main;
        float far = cam != null ? Vector3.Distance(cam.transform.position, head) : 0f;
        line.widthMultiplier = 1f + far * 0.02f;
    }

    void PlaceImpact(RaycastHit hit)
    {
        // No marks on people, or on something the shot just broke (a window pane).
        if (hit.collider.gameObject.layer == CharacterLayers.Hitbox || hit.collider is CharacterController || !hit.collider.enabled) return;
        // Reuse the oldest mark. Marks ride on what they hit (e.g. a crate), so one may have been
        // destroyed with its surface; recreate it then.
        var mark = impactMarks[nextImpact];
        if (mark == null)
        {
            var go = new GameObject("BulletHole");
            go.AddComponent<MeshFilter>().sharedMesh = quadMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = impactMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            mark = go.transform;
            impactMarks[nextImpact] = mark;
        }
        // Ride on the nearest evenly scaled transform: under a squashed one (e.g. a target's flattened disc)
        // the rotated mark would be skewed and stretched.
        Transform surface = hit.collider.transform;
        while (surface.parent != null && !UniformScale(surface.lossyScale)) surface = surface.parent;
        mark.SetParent(surface, false);
        mark.SetPositionAndRotation(hit.point + hit.normal * 0.005f,
            Quaternion.LookRotation(-hit.normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
        mark.localScale = Vector3.one * (0.08f / Mathf.Max(surface.lossyScale.x, 1e-4f));
        nextImpact = (nextImpact + 1) % impactMarks.Length;
    }

    static bool UniformScale(Vector3 s) =>
        Mathf.Abs(s.x - s.y) < 0.01f * Mathf.Abs(s.x) && Mathf.Abs(s.x - s.z) < 0.01f * Mathf.Abs(s.x);

    void OnGUI()
    {
        if (!Held || remote) return;
        if (hudStyle == null)
            hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Bold };
        int spares = pouch != null ? pouch.Count : -1;
        bool noMags = Time.time < noMagazinesUntil;
        if (hudAmmo != Ammo || hudReloading != IsReloading || hudSpares != spares || hudNoMags != noMags || hudZero != ZeroRange)
        {
            hudZero = ZeroRange;
            hudAmmo = Ammo;
            hudReloading = IsReloading;
            hudSpares = spares;
            hudNoMags = noMags;
            hudText = hudReloading ? "RELOADING..." : noMags ? "NO MAGAZINES" : Ammo + " / " + magazineSize;
            if (spares >= 0) hudText += "\nMAGS " + spares;
            hudText += "\nZERO " + hudZero + " m";
        }
        GUI.Label(new Rect(Screen.width - 330, Screen.height - 100, 300, 80), hudText, hudStyle);
    }
}
