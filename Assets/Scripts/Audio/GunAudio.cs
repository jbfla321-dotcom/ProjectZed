using UnityEngine;

// The rifle's sounds, driven by HitscanGun's events. The shooter hears the close stereo first-person shot;
// anyone else hears the 3D shot (close/mid/far layers, delayed and filtered by distance) and, if a supersonic bullet
// passes near them, its crack. Each shot is a short core; the shot's tail (its echo off the
// surroundings) plays once when the trigger stops, so full auto stays tight instead of piling up tails. Reload sounds follow the left hand's magazine swap
// (PlayerCharacterAnimator's reload timings); bullet impacts play by surface where the shot lands.
[RequireComponent(typeof(HitscanGun))]
public class GunAudio : MonoBehaviour
{
    [Header("Shots")]
    public SoundEvent shotFirstPerson;
    public SoundEvent shot3D;
    public SoundEvent tailFirstPerson;
    public SoundEvent tail3D;
    [Tooltip("The tail plays once no shot has followed for this many shot intervals.")]
    public float tailAfterIntervals = 1.3f;
    public SoundEvent bulletCrack;
    [Tooltip("Listeners within this distance (m) of the bullet's path hear it crack past.")]
    public float crackRadius = 6f;
    [Tooltip("No crack this close to the muzzle (the shot itself covers it).")]
    public float crackMinRange = 10f;

    [Header("Handling")]
    public SoundEvent dryFire;
    public SoundEvent magOut;
    public SoundEvent magIn;
    public SoundEvent charge;
    [Tooltip("Reload progress (0-1) when the new magazine seats, and when the charging handle is pulled (empty reloads only).")]
    public float magInAt = 0.74f;
    public float chargeAt = 0.9f;

    [Header("Impacts")]
    public SurfaceSounds impacts;

    [Tooltip("Debug: play the 3D shot (as another player would hear it) instead of the first-person one.")]
    public bool hearAsBystander;

    HitscanGun gun;
    Transform handlingPoint;
    bool reloadEmpty, magInPlayed, chargePlayed;
    int lastRounds;
    float lastShotTime;
    bool tailPending, tailFirstPersonPending;

    void Awake()
    {
        gun = GetComponent<HitscanGun>();
        var body = gun.player != null ? gun.player.GetComponentInChildren<PlayerCharacterAnimator>(true) : null;
        handlingPoint = body != null && body.carriedGun != null ? body.carriedGun : transform;
        lastRounds = gun.Rounds;
    }

    void OnEnable()
    {
        gun.Fired += OnFired;
        gun.ReloadStarted += OnReloadStarted;
        gun.DryFired += OnDryFired;
        gun.MagazineEjected += OnMagazineEjected;
        gun.BulletMoved += OnBulletMoved;
        gun.ShotHit += OnShotHit;
    }

    void OnDisable()
    {
        gun.Fired -= OnFired;
        gun.ReloadStarted -= OnReloadStarted;
        gun.DryFired -= OnDryFired;
        gun.MagazineEjected -= OnMagazineEjected;
        gun.BulletMoved -= OnBulletMoved;
        gun.ShotHit -= OnShotHit;
    }

    // The listener is this gun's shooter (the local player's own rifle).
    bool ShooterIsListening
    {
        get
        {
            if (hearAsBystander) return false;
            var sm = SoundManager.Instance;
            var l = sm != null ? sm.Listener : null;
            return l != null && gun.player != null && l.transform.IsChildOf(gun.player.transform);
        }
    }

    Vector3 MuzzlePosition => gun.muzzle != null ? gun.muzzle.position : transform.position;

    void OnFired()
    {
        lastRounds = gun.Rounds;
        bool own = ShooterIsListening;
        if (own) SoundManager.Play(shotFirstPerson, MuzzlePosition);
        else SoundManager.Play(shot3D, MuzzlePosition);
        lastShotTime = Time.time;
        tailPending = true;
        tailFirstPersonPending = own;
    }

    void UpdateTail()
    {
        if (!tailPending || Time.time - lastShotTime < tailAfterIntervals / Mathf.Max(gun.fireRate, 0.1f)) return;
        tailPending = false;
        SoundManager.Play(tailFirstPersonPending ? tailFirstPerson : tail3D, MuzzlePosition);
    }

    // Each step of a bullet's flight: it cracks once as it passes the listener, and only while still supersonic.
    void OnBulletMoved(Bullet bullet, Vector3 from, Vector3 to)
    {
        if (bulletCrack == null || bullet.crackHeard || bullet.distance < crackMinRange || ShooterIsListening) return;
        var l = SoundManager.Instance != null ? SoundManager.Instance.Listener : null;
        if (l == null) return;
        Vector3 p = l.transform.position;
        if (bullet.Speed < Ballistics.SpeedOfSound(BulletWorld.Instance.TemperatureAt(p))) return;
        // Closest point of this step to the listener.
        Vector3 path = to - from;
        float len = path.magnitude;
        if (len < 1e-4f) return;
        float along = Mathf.Clamp(Vector3.Dot(p - from, path / len), 0f, len);
        Vector3 closest = from + path / len * along;
        if (Vector3.Distance(closest, p) > crackRadius) return;
        bullet.crackHeard = true;
        SoundManager.Play(bulletCrack, closest);
    }

    void OnShotHit(RaycastHit hit)
    {
        if (impacts == null) return;
        SoundManager.Play(impacts.For(Surfaces.Resolve(hit)), hit.point);
    }

    void OnDryFired() => SoundManager.PlayAttached(dryFire, handlingPoint);

    void OnReloadStarted()
    {
        reloadEmpty = lastRounds == 0;
        magInPlayed = chargePlayed = false;
    }

    void OnMagazineEjected() => SoundManager.PlayAttached(magOut, handlingPoint);

    void Update()
    {
        UpdateTail();
        if (!gun.IsReloading)
        {
            lastRounds = gun.Rounds;
            return;
        }
        float t = gun.ReloadProgress;
        if (!magInPlayed && t >= magInAt) { magInPlayed = true; SoundManager.PlayAttached(magIn, handlingPoint); }
        if (reloadEmpty && !chargePlayed && t >= chargeAt) { chargePlayed = true; SoundManager.PlayAttached(charge, handlingPoint); }
    }
}
