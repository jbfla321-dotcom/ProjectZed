using UnityEngine;

// Footsteps and landings for a character body (player or NPC), from the FootL/FootR/Land animation events
// that PlayerCharacterAnimator passes on. Each step checks what's under that foot (terrain layer, SurfaceTag,
// material name) and plays that surface's step, louder and brighter when sprinting, soft when crouched,
// sometimes with a quiet rustle of clothes and gear on top.
[RequireComponent(typeof(PlayerCharacterAnimator))]
public class FootstepAudio : MonoBehaviour
{
    public SurfaceSounds steps;
    public SoundEvent land;
    public SoundEvent gearRustle;

    [Header("Gait")]
    public float walkVolume = 0.75f;
    public float sprintVolume = 1f;
    public float crouchVolume = 0.35f;
    public float sprintPitch = 1.06f;
    [Tooltip("Faster than this times the walk speed counts as sprinting (for characters without a sprint flag).")]
    public float sprintSpeedFactor = 1.2f;
    [Tooltip("Slower than this (m/s) the feet are only shuffling: no steps.")]
    public float minSpeed = 0.25f;

    [Header("Gear")]
    [Range(0f, 1f)] public float rustleChance = 0.35f;
    [Range(0f, 1f)] public float sprintRustleChance = 0.8f;
    public float rustleVolume = 0.5f;

    [Header("Landing")]
    [Tooltip("Fall speeds (m/s) mapped to landing volume: quiet at the first, full at the second.")]
    public Vector2 landSpeedRange = new Vector2(3f, 10f);

    // Blended clips fire their events a few frames apart; these windows keep one sound per footfall.
    const float SameFootWindow = 0.25f, AnyFootWindow = 0.1f, LandWindow = 0.4f;

    PlayerCharacterAnimator body;
    Animator animator;
    CharacterMotor motor;
    Transform leftFoot, rightFoot;
    float lastLeft = -1f, lastRight = -1f, lastAny = -1f, lastLand = -1f;
    float pendingLandSpeed;
    readonly RaycastHit[] hits = new RaycastHit[4];

    void Awake()
    {
        body = GetComponent<PlayerCharacterAnimator>();
        animator = GetComponent<Animator>();
        motor = body.controller;
        leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
    }

    void OnEnable()
    {
        body.Footstep += OnFootstep;
        body.LandImpact += OnLandEvent;
        if (motor != null) motor.Landed += OnLanded;
    }

    void OnDisable()
    {
        body.Footstep -= OnFootstep;
        body.LandImpact -= OnLandEvent;
        if (motor != null) motor.Landed -= OnLanded;
    }

    float HorizontalSpeed => motor != null ? new Vector2(motor.Velocity.x, motor.Velocity.z).magnitude : 1f;

    bool Sprinting => motor is FPSController fps ? fps.IsSprinting
        : motor != null && HorizontalSpeed > motor.WalkSpeed * sprintSpeedFactor;

    void OnFootstep(bool left)
    {
        float now = Time.time;
        ref float lastSame = ref (left ? ref lastLeft : ref lastRight);
        if (now - lastSame < SameFootWindow || now - lastAny < AnyFootWindow) return;
        if (motor != null && (!motor.IsGrounded || HorizontalSpeed < minSpeed)) return;
        lastSame = now;
        lastAny = now;

        Transform foot = left ? leftFoot : rightFoot;
        if (foot == null || !Ground(foot.position, out RaycastHit hit)) return;
        bool crouched = motor != null && motor.IsCrouching;
        bool sprint = !crouched && Sprinting;
        float vol = crouched ? crouchVolume : sprint ? sprintVolume : walkVolume;
        float pitch = sprint ? sprintPitch : 1f;
        if (steps != null) SoundManager.Play(steps.For(Surfaces.Resolve(hit)), hit.point, vol, pitch);
        if (gearRustle != null && !crouched && Random.value < (sprint ? sprintRustleChance : rustleChance))
            SoundManager.PlayAttached(gearRustle, transform, rustleVolume * vol);
    }

    // The motor reports the fall speed; the landing clip's event (if it plays) may arrive just before or after.
    void OnLanded(float impactSpeed)
    {
        pendingLandSpeed = impactSpeed;
        PlayLand(impactSpeed);
    }

    void OnLandEvent() => PlayLand(pendingLandSpeed > 0f ? pendingLandSpeed : landSpeedRange.x);

    void PlayLand(float impactSpeed)
    {
        float now = Time.time;
        if (now - lastLand < LandWindow) return;
        lastLand = now;
        lastLeft = lastRight = lastAny = now; // the landing covers the next footfall
        float k = Mathf.InverseLerp(landSpeedRange.x, landSpeedRange.y, impactSpeed);
        float vol = Mathf.Lerp(0.5f, 1f, k);
        Vector3 at = leftFoot != null && rightFoot != null ? (leftFoot.position + rightFoot.position) * 0.5f : transform.position;
        if (!Ground(at, out RaycastHit hit)) hit.point = transform.position;
        SoundManager.Play(land, hit.point, vol);
        if (steps != null && hit.collider != null) SoundManager.Play(steps.For(Surfaces.Resolve(hit)), hit.point, vol);
        if (gearRustle != null) SoundManager.PlayAttached(gearRustle, transform, rustleVolume * vol);
    }

    // The ground under a point (ignoring characters and loose items).
    bool Ground(Vector3 at, out RaycastHit hit)
    {
        int mask = CharacterLayers.Environment;
        if (WorldItems.Layer != 0) mask &= ~(1 << WorldItems.Layer);
        int n = Physics.RaycastNonAlloc(at + Vector3.up * 0.5f, Vector3.down, hits, 1.5f, mask, QueryTriggerInteraction.Ignore);
        hit = default;
        float best = float.MaxValue;
        Transform self = motor != null ? motor.transform : transform.root;
        for (int i = 0; i < n; i++)
        {
            if (hits[i].collider.transform.IsChildOf(self) || hits[i].distance >= best) continue;
            best = hits[i].distance;
            hit = hits[i];
        }
        return best < float.MaxValue;
    }
}
