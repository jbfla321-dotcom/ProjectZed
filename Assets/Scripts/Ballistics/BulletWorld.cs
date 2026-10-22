using System.Collections.Generic;
using UnityEngine;

// A bullet in flight. Pooled: don't keep a reference after its gun is told it ended.
public class Bullet
{
    public int id;
    public HitscanGun owner;
    public Transform shooter;
    public BulletType ammo;
    public LayerMask mask;
    public Vector3 position, velocity;
    public float time, distance;
    public float damage, muzzleSpeed;
    public int ricochets;
    public bool alive;
    // Where the tracer is drawn from at the start (the muzzle), relative to the true path (which starts at the eye).
    public Vector3 visualOffset;
    // Free for listeners, e.g. so a bystander hears a bullet crack past once.
    public bool crackHeard;
    public int tracer = -1;

    public float Speed => velocity.magnitude;
}

// Flies every bullet in the scene: gravity, drag against the wind, spin drift and transonic wobble each step,
// then a raycast along the step to see what it hit. Hits can stop the bullet, punch through (losing speed by
// the material's resistance and thickness), or glance off at shallow angles, as in Arma 3. The gun that fired
// a bullet is told about each step and hit, for damage, tracers, marks and sounds.
// Creates itself on the first shot; add one to the scene to change the weather it flies through.
public class BulletWorld : MonoBehaviour
{
    [Header("Air")]
    [Tooltip("Air temperature at sea level (°C). Colder air is denser (more drag) and slows the powder.")]
    public float seaLevelTemperature = 15f;
    [Tooltip("Height above sea level (m) of world y = 0.")]
    public float altitudeOffset = 0f;
    [Tooltip("Wind (m/s) when the scene has no WindManager. With one, bullets drift in the same wind as the trees.")]
    public Vector3 fallbackWind = Vector3.zero;

    [Header("Simulation")]
    [Tooltip("Longest simulation step (s). Each step is one raycast.")]
    public float maxStep = 0.008f;
    public float timeToLive = 5f;
    [Tooltip("Bullets slower than this (m/s) are spent.")]
    public float minSpeed = 60f;
    public int maxRicochets = 2;
    [Tooltip("Random yaw (degrees per √s) while passing through the sound barrier, where bullets lose stability.")]
    public float transonicWobble = 1.5f;

    static BulletWorld instance;
    public static BulletWorld Instance
    {
        get
        {
            if (instance == null) instance = FindAnyObjectByType<BulletWorld>();
            if (instance == null) instance = new GameObject("BulletWorld").AddComponent<BulletWorld>();
            return instance;
        }
    }

    readonly List<Bullet> live = new List<Bullet>();
    readonly Stack<Bullet> pool = new Stack<Bullet>();
    readonly RaycastHit[] hits = new RaycastHit[16];
    int nextId;

    public int LiveCount => live.Count;

    public float TemperatureAt(Vector3 p) => Ballistics.AirTemperature(seaLevelTemperature, p.y + altitudeOffset);

    public Vector3 WindAt(Vector3 p)
    {
        var w = WindManager.Instance;
        return w != null ? w.GetWindAt(p) : fallbackWind;
    }

    public Bullet Fire(HitscanGun owner, Transform shooter, BulletType ammo, LayerMask mask,
        Vector3 position, Vector3 velocity, float damage, Vector3 visualOffset)
    {
        var b = pool.Count > 0 ? pool.Pop() : new Bullet();
        b.id = ++nextId;
        b.owner = owner;
        b.shooter = shooter;
        b.ammo = ammo;
        b.mask = mask;
        b.position = position;
        b.velocity = velocity;
        b.muzzleSpeed = velocity.magnitude;
        b.damage = damage;
        b.visualOffset = visualOffset;
        b.time = b.distance = 0f;
        b.ricochets = 0;
        b.crackHeard = false;
        b.tracer = -1;
        b.alive = true;
        live.Add(b);
        return b;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var b = live[i];
            float remaining = dt;
            while (remaining > 0f && b.alive)
            {
                float step = Mathf.Min(maxStep, remaining);
                remaining -= step;
                Advance(b, step);
            }
            if (b.alive) continue;
            if (b.owner != null) b.owner.OnBulletEnded(b);
            b.owner = null;
            b.shooter = null;
            live.RemoveAt(i);
            pool.Push(b);
        }
    }

    void Advance(Bullet b, float dt)
    {
        Vector3 from = b.position;
        float altitude = from.y + altitudeOffset;
        float celsius = Ballistics.AirTemperature(seaLevelTemperature, altitude);
        float sound = Ballistics.SpeedOfSound(celsius);
        Ballistics.Step(ref b.position, ref b.velocity, b.ammo.ballisticCoefficient, WindAt(from),
            Ballistics.AirDensity(seaLevelTemperature, altitude), sound, dt);

        // Spin drift: sideways creep to the right of the path, growing with time of flight.
        Vector3 right = Vector3.Cross(Vector3.up, b.velocity).normalized;
        b.position += right * (Ballistics.SpinDrift(b.ammo.stability, b.time + dt) - Ballistics.SpinDrift(b.ammo.stability, b.time));

        // Passing through the sound barrier, the bullet wobbles (ACE3's transonic instability).
        float mach = b.Speed / sound;
        if (mach > 0.9f && mach < 1.1f && transonicWobble > 0f)
        {
            Vector3 axis = Vector3.Cross(b.velocity, Random.onUnitSphere).normalized;
            b.velocity = Quaternion.AngleAxis(Random.Range(-1f, 1f) * transonicWobble * Mathf.Sqrt(dt), axis) * b.velocity;
        }
        b.time += dt;

        Vector3 path = b.position - from;
        float length = path.magnitude;
        if (length > 1e-4f && TryHit(b, from, path / length, length, out RaycastHit hit))
        {
            b.position = hit.point;
            b.distance += hit.distance;
            Report(b, from);
            Impact(b, hit);
        }
        else
        {
            b.distance += length;
            Report(b, from);
        }

        if (b.time >= timeToLive || b.Speed < minSpeed) b.alive = false;
    }

    void Report(Bullet b, Vector3 from)
    {
        if (b.owner != null) b.owner.OnBulletMoved(b, from, b.position);
    }

    // Closest hit along the step that isn't part of the shooter.
    bool TryHit(Bullet b, Vector3 from, Vector3 dir, float length, out RaycastHit closest)
    {
        closest = default;
        int count = Physics.RaycastNonAlloc(from, dir, hits, length, b.mask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var h = hits[i];
            if ((b.shooter != null && h.collider.transform.IsChildOf(b.shooter)) || h.distance >= best) continue;
            best = h.distance;
            closest = h;
        }
        return best < float.MaxValue;
    }

    void Impact(Bullet b, RaycastHit hit)
    {
        float speed = b.Speed;
        Vector3 dir = b.velocity / Mathf.Max(speed, 1e-4f);
        if (b.owner != null) b.owner.OnBulletHit(b, hit, dir, speed);

        var surface = Ballistics.For(Surfaces.Resolve(hit));
        if (surface.stops) { b.alive = false; return; }

        // Glancing blow: the shallower the angle, the likelier it skips off, leaving flatter and slower.
        float grazing = 90f - Vector3.Angle(-dir, hit.normal);
        if (b.ricochets < maxRicochets && grazing < surface.ricochetAngle)
        {
            float t = grazing / surface.ricochetAngle;
            if (Random.value > t * t)
            {
                Vector3 bounce = Vector3.Reflect(dir, hit.normal);
                Vector3 along = Vector3.ProjectOnPlane(bounce, hit.normal);
                if (along.sqrMagnitude > 1e-6f) bounce = Vector3.Slerp(bounce, along.normalized, 0.5f);
                bounce = Quaternion.AngleAxis(Random.Range(-5f, 5f), hit.normal) * bounce;
                b.velocity = bounce.normalized * speed * Mathf.Lerp(0.8f, 0.5f, t);
                b.position = hit.point + hit.normal * 0.01f;
                b.ricochets++;
                return;
            }
        }

        // It broke what it hit (a window pane switches its collider off): carry on through, barely slowed.
        if (!hit.collider.enabled)
        {
            b.velocity *= 0.97f;
            b.position = hit.point + dir * 0.01f;
            return;
        }

        // Punch through if the bullet has the speed for the material's thickness. The ground has no far side.
        if (hit.collider is TerrainCollider) { b.alive = false; return; }
        float resistance = surface.resistance / Mathf.Max(b.ammo.penetration, 0.01f); // m/s per cm
        float maxDepth = speed / resistance * 0.01f;
        // Find the far side by casting back at this collider from beyond the deepest the bullet could reach.
        Vector3 beyond = hit.point + dir * (maxDepth + 0.01f);
        if (maxDepth > 0.002f && hit.collider.Raycast(new Ray(beyond, -dir), out RaycastHit exit, maxDepth + 0.01f))
        {
            float left = speed - Vector3.Distance(hit.point, exit.point) * 100f * resistance;
            if (left > minSpeed)
            {
                // Comes out tumbling a little: the more it slowed, the more it turns.
                float turn = Random.Range(0f, (1f - left / speed) * 12f);
                Vector3 axis = Vector3.Cross(dir, Random.onUnitSphere).normalized;
                b.velocity = Quaternion.AngleAxis(turn, axis) * dir * left;
                b.position = exit.point + dir * 0.01f;
                if (b.owner != null) b.owner.OnBulletExited(b, exit);
                return;
            }
        }
        b.alive = false;
    }
}
