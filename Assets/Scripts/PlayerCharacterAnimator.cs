using UnityEngine;

// Drives the player's character, which is both the third-person body and (DayZ-style) the first-person view:
// locomotion parameters from the FPSController,
// fire/reload triggers from the gun, and IK so the hands stay on the rifle and the body aims with the camera.
// It also places the rifle (stance, aiming, recoil, reload pose) and, in first person, the eye camera pivot.
[RequireComponent(typeof(Animator))]
public class PlayerCharacterAnimator : MonoBehaviour
{
    [Header("References")]
    public CharacterMotor controller;
    public HitscanGun gun;
    public Transform aimCamera;
    public Transform rightHandTarget;
    public Transform leftHandTarget;

    [Header("Carried Rifle (full body only)")]
    [Tooltip("The body's rifle. Each frame it's placed off the upper chest and aimed at the crosshair target, so it moves with the body rather than the camera.")]
    public Transform carriedGun;
    [Tooltip("Where the rifle sits relative to the upper chest, in the character's facing space.")]
    public Vector3 carriedGunOffset = new Vector3(0.1f, -0.05f, 0.3f);
    [Tooltip("Aim points closer than this are ignored (the rifle would swing wildly), and it points straight ahead instead.")]
    public float minAimDistance = 2f;
    [Tooltip("Rifle placement when lowered (low ready, diagonal across the chest): offset from the aimed position in character space, and pitch/yaw/cant.")]
    public Vector3 loweredGunOffset = new Vector3(-0.14f, -0.13f, -0.02f);
    public Vector3 loweredGunAngles = new Vector3(35f, -50f, 15f);
    [Tooltip("How much the rifle turns with the chest rather than the body's facing (lowered / aimed). Higher = more sway.")]
    [Range(0f, 1f)] public float loweredChestFollow = 0.5f;
    [Range(0f, 1f)] public float aimedChestFollow = 0.25f;
    [Tooltip("How quickly the rifle catches up with where it should be. Lower = looser, more trailing.")]
    public float gunFollowRate = 14f;

    [Header("Stance")]
    public WeaponStance stance;
    [Tooltip("Look-at body weight while the rifle is lowered (the raised value is Look Body Weight).")]
    public float loweredLookBodyWeight = 0.1f;


    [Header("Lean (full body only)")]
    [Tooltip("Degrees of bank per 100 degrees/second of turning.")]
    public float turnLean = 4f;
    [Tooltip("Degrees of bank per metre/second of strafing.")]
    public float strafeLean = 1.2f;
    [Tooltip("Degrees of forward pitch per metre/second of forward speed.")]
    public float runLean = 0.3f;
    public float maxLean = 9f;
    public float leanSmoothing = 6f;

    [Header("Tuning")]
    public float velocityDamping = 0.15f;
    [Tooltip("Weight of the steadier shouldered spine while aiming (the rest comes from the run).")]
    [Range(0f, 1f)] public float aimSpineWeight = 0.6f;
    public float lookWeight = 1f;
    public float lookBodyWeight = 0.35f;

    static readonly int VelXId = Animator.StringToHash("VelX");
    static readonly int VelZId = Animator.StringToHash("VelZ");
    static readonly int CrouchId = Animator.StringToHash("Crouch");
    static readonly int GroundedId = Animator.StringToHash("Grounded");
    static readonly int FireId = Animator.StringToHash("Fire");
    static readonly int ReloadId = Animator.StringToHash("Reload");
    static readonly int JumpId = Animator.StringToHash("Jump");
    static readonly int AirTimeId = Animator.StringToHash("AirTime");
    static readonly int RaiseId = Animator.StringToHash("Raise");
    static readonly int SpeedId = Animator.StringToHash("Speed");
    static readonly int HitId = Animator.StringToHash("Hit");

    // Without a stance (NPCs) the rifle is carried lowered.
    float Raise => stance != null && Armed ? Mathf.SmoothStep(0f, 1f, stance.RaiseBlend) : 0f;

    // The rifle is in the hands (not dropped). Unarmed, the rifle layers, hand IK and rifle placement are off.
    public bool Armed => rifle == null || rifle.IsHeld;
    CarriedRifle rifle;

    [Header("Grounding")]
    [Tooltip("The movement capsule hovers its skin width above the ground; the body is lowered by the measured gap " +
             "(up to this much, metres) so the feet stand on the ground.")]
    public float maxGroundSink = 0.15f;
    float groundSink;
    Vector3 bodyRestPosition;

    Animator animator;
    Transform head;
    Transform chest;
    float airTime;
    int carriedGunFrame = -1;
    Vector3 aimPoint;
    Quaternion chestRest;
    bool gunPlaced;
    Vector3 gunPos;
    Quaternion gunRot;
    int aimSpineLayer = -1;
    float lastYaw;
    Vector2 lean; // x = pitch, y = roll

    void Awake()
    {
        animator = GetComponent<Animator>();
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        chest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
        if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        // The chest's rest orientation relative to the body, so its animated sway can be measured against it.
        if (chest != null) chestRest = Quaternion.Inverse(transform.rotation) * chest.rotation;
        aimSpineLayer = animator.GetLayerIndex("AimSpine");
        if (carriedGun != null)
        {
            rifle = CarriedRifle.On(carriedGun);
            foreach (var t in carriedGun.GetComponentsInChildren<Transform>(true))
                if (t.name == "SightRear") sightRear = t;
        }
        lastYaw = transform.eulerAngles.y;
        bodyRestPosition = transform.localPosition;
    }

    void OnEnable()
    {
        if (gun != null)
        {
            gun.Fired += OnFired;
            gun.ReloadStarted += OnReloadStarted;
        }
        if (controller != null) controller.Jumped += OnJumped;
    }

    void OnDisable()
    {
        if (gun != null)
        {
            gun.Fired -= OnFired;
            gun.ReloadStarted -= OnReloadStarted;
        }
        if (controller != null) controller.Jumped -= OnJumped;
    }

    // Animation event receivers for the mocap clips (events added by Tools > Audio > Add Footstep Events).
    // Every clip in a blend tree fires its events, so ones that barely contribute are ignored; the blended
    // clips are phase-matched, so the rest land together (FootstepAudio ignores the near-duplicates).
    public event System.Action<bool> Footstep; // true = left foot
    public event System.Action LandImpact;
    const float MinEventWeight = 0.15f;
    void FootL(AnimationEvent e) { if (e.animatorClipInfo.weight >= MinEventWeight) Footstep?.Invoke(true); }
    void FootR(AnimationEvent e) { if (e.animatorClipInfo.weight >= MinEventWeight) Footstep?.Invoke(false); }
    void Land(AnimationEvent e) { if (e.animatorClipInfo.weight >= MinEventWeight) LandImpact?.Invoke(); }

    void OnFired()
    {
        animator.SetTrigger(FireId);
        recoilKick = 1f;
    }
    void OnReloadStarted() => animator.SetTrigger(ReloadId);
    void OnJumped() => animator.SetTrigger(JumpId);

    // Flinch (shooter-pack hit reaction on the upper body). Call this from damage code.
    public void PlayHitReaction() => animator.SetTrigger(HitId);

    // Set by Ragdoll while the body gets up off the ground: only the base layer (the get-up animation)
    // plays; the rifle-carrying layers, hand IK and look-at are off.
    [System.NonSerialized] public bool Recovering;
    float[] defaultLayerWeights;
    bool upperLayersActive = true;

    void SetUpperLayersActive(bool active)
    {
        if (active == upperLayersActive) return;
        upperLayersActive = active;
        if (defaultLayerWeights == null)
        {
            defaultLayerWeights = new float[animator.layerCount];
            for (int i = 1; i < defaultLayerWeights.Length; i++) defaultLayerWeights[i] = animator.GetLayerWeight(i);
        }
        for (int i = 1; i < defaultLayerWeights.Length; i++)
            if (i != aimSpineLayer) animator.SetLayerWeight(i, active ? defaultLayerWeights[i] : 0f);
    }

    void Update()
    {
        SetUpperLayersActive(!Recovering && Armed);
        if (Recovering)
        {
            if (aimSpineLayer >= 0) animator.SetLayerWeight(aimSpineLayer, 0f);
            return;
        }
        if (controller == null)
        {
            animator.SetFloat(VelXId, 0f);
            animator.SetFloat(VelZId, 0f);
            animator.SetBool(CrouchId, false);
            animator.SetBool(GroundedId, true);
        }
        else
        {
            Vector3 local = controller.transform.InverseTransformDirection(controller.Velocity);
            animator.SetFloat(VelXId, local.x, velocityDamping, Time.deltaTime);
            animator.SetFloat(VelZId, local.z, velocityDamping, Time.deltaTime);
            animator.SetFloat(SpeedId, new Vector2(local.x, local.z).magnitude, velocityDamping, Time.deltaTime);
            animator.SetBool(CrouchId, controller.IsCrouching);
            animator.SetBool(GroundedId, controller.IsGrounded);
            // Time off the ground, so stepping off a stair doesn't trigger the fall animation.
            airTime = controller.IsGrounded ? 0f : airTime + Time.deltaTime;
            animator.SetFloat(AirTimeId, airTime);
            UpdateLean(local);
            UpdateGrounding();
        }

        animator.SetFloat(RaiseId, Raise);
        if (aimSpineLayer >= 0) animator.SetLayerWeight(aimSpineLayer, Raise * aimSpineWeight);

    }

    // ---------- Magazine swap ----------
    // During a reload the left hand pulls the rifle's own magazine, lowers it to the belt, brings it back
    // and seats it, then returns to the handguard. Points are in the rifle's (GunModel's) space.
    [Header("Reload (left hand path, rifle space)")]
    public Vector3 magGrabPoint = new Vector3(-0.05f, -0.17f, 0.04f);
    public Vector3 beltPoint = new Vector3(-0.12f, -0.3f, -0.02f);
    [Tooltip("How far the magazine drops out of the well before it's carried away.")]
    public float magDropDistance = 0.08f;

    Transform magazine;
    bool magazineDisplaced; // one more pass after a reload ends, to put the magazine back exactly
    Vector3 magRestPos;
    Quaternion magRestRot;
    bool magazineFound;

    void FindMagazine()
    {
        magazineFound = true;
        if (leftHandTarget == null) return;
        foreach (var t in leftHandTarget.parent.GetComponentsInChildren<Transform>(true))
            if (t.name == "Magazine") { magazine = t; break; }
        if (magazine != null) { magRestPos = magazine.localPosition; magRestRot = magazine.localRotation; }
    }

    static float Segment(float t, float start, float end) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, end, t));

    // Reload progress when the old magazine (pulled clear of the well) is let go, and when the new one is in
    // the hand at the belt.
    const float MagReleaseTime = 0.25f, MagFromBeltTime = 0.42f;
    Renderer[] magazineRenderers;
    bool magazineVisible = true;

    void SetMagazineVisible(bool visible)
    {
        if (visible == magazineVisible) return;
        magazineVisible = visible;
        magazineRenderers ??= magazine.GetComponentsInChildren<Renderer>(true);
        foreach (var r in magazineRenderers) r.enabled = visible;
    }

    // Left-hand goal for this point in the reload; also moves the magazine while it's in the hand.
    void ReloadHandGoal(out Vector3 position, out Quaternion rotation)
    {
        if (!magazineFound) FindMagazine();
        Transform gunSpace = leftHandTarget.parent;
        float t = gun.ReloadProgress;

        Vector3 hold = leftHandTarget.localPosition;
        Quaternion holdRot = leftHandTarget.localRotation;
        Vector3 grab = magGrabPoint;
        // Palm against the magazine's left side, fingers down and forward around it.
        Quaternion grabRot = Quaternion.LookRotation(new Vector3(0.15f, -0.55f, 0.8f), Vector3.left);
        Vector3 dropped = grab + Vector3.down * magDropDistance;

        Vector3 p; Quaternion r; Vector3 magOffset = Vector3.zero;
        if (t < 0.15f)      { float k = Segment(t, 0f, 0.15f); p = Vector3.Lerp(hold, grab, k); r = Quaternion.Slerp(holdRot, grabRot, k); }
        else if (t < 0.25f) { float k = Segment(t, 0.15f, 0.25f); p = Vector3.Lerp(grab, dropped, k); r = grabRot; magOffset = p - grab; }
        else if (t < 0.42f) { float k = Segment(t, 0.25f, 0.42f); p = Vector3.Lerp(dropped, beltPoint, k); r = grabRot; magOffset = p - grab; }
        else if (t < 0.52f) { p = beltPoint; r = grabRot; magOffset = p - grab; }
        else if (t < 0.7f)  { float k = Segment(t, 0.52f, 0.7f); p = Vector3.Lerp(beltPoint, dropped, k); r = grabRot; magOffset = p - grab; }
        else if (t < 0.78f) { float k = Segment(t, 0.7f, 0.78f); p = Vector3.Lerp(dropped, grab + Vector3.up * 0.005f, k); r = grabRot; magOffset = p - grab; }
        else if (t < 0.84f) { p = grab; r = grabRot; }
        else                { float k = Segment(t, 0.84f, 1f); p = Vector3.Lerp(grab, hold, k); r = Quaternion.Slerp(grabRot, holdRot, k); }

        if (magazine != null)
        {
            // magOffset is in rifle space; the magazine's parent may be rotated/offset, so convert through world space.
            Vector3 restWorld = magazine.parent.TransformPoint(magRestPos);
            Vector3 newPos = restWorld + gunSpace.TransformVector(magOffset);
            // Once it's out of the well the old magazine is let go (dropped as a loose one); the hand fetches a
            // new one from the belt, so the rifle's magazine is hidden until the hand is there.
            if (t >= MagReleaseTime && gun.IsReloading)
                gun.EjectMagazine(Time.deltaTime > 0f ? Vector3.ClampMagnitude((newPos - magazine.position) / Time.deltaTime, 3f) : Vector3.zero);
            SetMagazineVisible(!gun.IsReloading || t < MagReleaseTime || t >= MagFromBeltTime);
            magazine.position = newPos;
            magazine.localRotation = magRestRot;
        }
        position = gunSpace.TransformPoint(p);
        rotation = gunSpace.rotation * r;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (Recovering)
        {
            animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
            animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
            animator.SetLookAtWeight(0f);
            return;
        }
        if (!Armed)
        {
            // Rifle dropped: hands free, the head still looks where the camera does.
            SetHandGoal(AvatarIKGoal.RightHand, null, 0f);
            SetHandGoal(AvatarIKGoal.LeftHand, null, 0f);
            if (aimCamera != null)
            {
                animator.SetLookAtWeight(lookWeight, loweredLookBodyWeight, 1f, 0f, 0.5f);
                animator.SetLookAtPosition(aimCamera.position + aimCamera.forward * 50f);
            }
            return;
        }
        // Once per frame (this runs for every IK layer): mount the rifle before the hands are solved onto it.
        if (carriedGunFrame != Time.frameCount)
        {
            carriedGunFrame = Time.frameCount;
            aimPoint = gun != null ? gun.GetAimPoint() : aimCamera != null ? aimCamera.position + aimCamera.forward * 50f : transform.position + transform.forward * 50f;
            if (carriedGun != null && chest != null) PlaceCarriedGun();
        }

        SetHandGoal(AvatarIKGoal.RightHand, rightHandTarget, 1f);
        if (gun != null && leftHandTarget != null && (gun.IsReloading || magazineDisplaced))
        {
            ReloadHandGoal(out Vector3 lp, out Quaternion lr);
            magazineDisplaced = gun.IsReloading;
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1f);
            animator.SetIKPosition(AvatarIKGoal.LeftHand, lp);
            animator.SetIKRotation(AvatarIKGoal.LeftHand, lr);
        }
        else
        {
            SetHandGoal(AvatarIKGoal.LeftHand, leftHandTarget, 1f);
        }

        if (aimCamera != null)
        {
            // The torso only twists toward the target when the rifle is up; lowered, just the head looks.
            animator.SetLookAtWeight(lookWeight, Mathf.Lerp(loweredLookBodyWeight, lookBodyWeight, Raise), 1f, 0f, 0.5f);
            animator.SetLookAtPosition(aimPoint);
        }
    }

    // Lowers the body onto the ground under the movement capsule (which floats by its skin width).
    void UpdateGrounding()
    {
        float target = 0f;
        Transform root = controller.transform;
        int worldMask = CharacterLayers.Environment;
        if (WorldItems.Layer != 0) worldMask &= ~(1 << WorldItems.Layer); // not onto loose magazines
        if (controller.IsGrounded && Physics.Raycast(root.position + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit,
                0.3f + maxGroundSink, worldMask, QueryTriggerInteraction.Ignore))
            target = Mathf.Clamp(hit.point.y - root.position.y, -maxGroundSink, 0f);
        groundSink = Mathf.MoveTowards(groundSink, target, 0.5f * Time.deltaTime);
        transform.localPosition = bodyRestPosition + Vector3.up * groundSink;
    }

    // Banks the whole body into turns and strafes and leans it into running, pivoting at the feet.
    void UpdateLean(Vector3 localVelocity)
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        float yaw = transform.eulerAngles.y;
        float yawRate = Mathf.DeltaAngle(lastYaw, yaw) / dt;
        lastYaw = yaw;

        float speed01 = controller.IsGrounded ? Mathf.Clamp01(new Vector2(localVelocity.x, localVelocity.z).magnitude / controller.WalkSpeed) : 0f;
        float roll = (-yawRate / 100f * turnLean * speed01) - localVelocity.x * strafeLean;
        float pitch = localVelocity.z * runLean;
        var target = new Vector2(Mathf.Clamp(pitch, -maxLean, maxLean), Mathf.Clamp(roll, -maxLean, maxLean));
        lean = Vector2.Lerp(lean, target, 1f - Mathf.Exp(-leanSmoothing * dt));
        transform.localRotation = Quaternion.Euler(lean.x, 0f, lean.y);
    }

    // The chest pose here is last frame's, but the rifle and the hand goals both use it, so they stay together.
    void PlaceCarriedGun()
    {
        float r = Raise;
        // Frame partly following the chest, so the rifle (and the hands locked to it) sway with the run.
        Quaternion chestFacing = chest.rotation * Quaternion.Inverse(chestRest);
        Quaternion frame = Quaternion.Slerp(transform.rotation, chestFacing, Mathf.Lerp(loweredChestFollow, aimedChestFollow, r));

        // Raised: shouldered and aimed at the crosshair target.
        Vector3 aimedPos = chest.position + frame * carriedGunOffset;
        Vector3 dir = aimPoint - aimedPos;
        if (dir.magnitude < minAimDistance)
            dir = aimCamera != null ? aimCamera.forward : transform.forward;
        Quaternion aimedRot = Quaternion.LookRotation(dir, frame * Vector3.up);

        // Lowered: low ready, diagonal across the chest with the muzzle toward the left knee.
        Vector3 loweredPos = aimedPos + frame * loweredGunOffset;
        Quaternion loweredRot = frame * Quaternion.Euler(loweredGunAngles);

        Vector3 pos = Vector3.Lerp(loweredPos, aimedPos, r);
        Quaternion rot = Quaternion.Slerp(loweredRot, aimedRot, r);
        // Trail slightly behind the body instead of being welded to it.
        if (!gunPlaced || Time.deltaTime <= 0f)
        {
            gunPos = pos; gunRot = rot; gunPlaced = true;
        }
        else
        {
            float k = 1f - Mathf.Exp(-gunFollowRate * Time.deltaTime);
            // A small trailing offset that decays toward the target; capped so running never drags the rifle away.
            Vector3 lag = Vector3.ClampMagnitude(Vector3.Lerp(gunPos - pos, Vector3.zero, k), 0.03f);
            gunPos = pos + lag;
            gunRot = Quaternion.Slerp(gunRot, rot, k);
        }
        // Recoil: kick back and muzzle up, recovering quickly.
        Quaternion finalRot = gunRot * Quaternion.Euler(-recoilPitch * recoilKick, 0f, 0f);
        Vector3 finalPos = gunPos - (gunRot * Vector3.forward) * (recoilBack * recoilKick);

        // Reload: bring the rifle up in front of the chest, canted so the magazine well faces the eyes,
        // so the magazine swap is in view (first person) and reads clearly (third person).
        reloadBlend = Mathf.MoveTowards(reloadBlend, gun != null && gun.IsReloading ? 1f : 0f, reloadPoseSpeed * Time.deltaTime);
        if (reloadBlend > 0f && aimCamera != null)
        {
            float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, aimCamera.eulerAngles.x), 5f, 35f);
            Quaternion view = Quaternion.Euler(pitch, transform.parent.eulerAngles.y, 0f);
            Vector3 rPos = aimCamera.position + view * reloadPoseOffset;
            Quaternion rRot = view * Quaternion.Euler(reloadPoseEuler);
            float k = Mathf.SmoothStep(0f, 1f, reloadBlend);
            finalPos = Vector3.Lerp(finalPos, rPos, k);
            finalRot = Quaternion.Slerp(finalRot, rRot, k);
        }

        // True first-person aiming: bring the rifle's rear sight in front of the eye, aligned with the view.
        float ads = firstPersonView && stance != null && aimCamera != null ? Mathf.SmoothStep(0f, 1f, stance.AimBlend) : 0f;
        if (ads > 0f && sightRear != null)
        {
            Quaternion adsRot = aimCamera.rotation * Quaternion.Euler(-recoilPitch * recoilKick * 0.5f, 0f, 0f);
            Vector3 sightOffset = Quaternion.Inverse(carriedGun.rotation) * (sightRear.position - carriedGun.position);
            Vector3 adsPos = aimCamera.position + aimCamera.forward * (adsEyeRelief + recoilBack * recoilKick) - adsRot * sightOffset;
            finalPos = Vector3.Lerp(finalPos, adsPos, ads);
            finalRot = Quaternion.Slerp(finalRot, adsRot, ads);
        }
        carriedGun.SetPositionAndRotation(finalPos, finalRot);
        if (ads < 0.5f && reloadBlend < 0.5f) KeepGunOutOfTorso();
    }

    // ---------- True first person (camera in the head, DayZ style) ----------
    [Header("True First Person")]
    [Tooltip("Set by CameraModeSwitcher: this body is the first-person view (camera at its eyes).")]
    public bool firstPersonView;
    [Tooltip("Eye position relative to the head bone, in the character's facing space.")]
    public Vector3 eyeOffset = new Vector3(0f, 0.08f, 0.11f);
    [Tooltip("How quickly the camera follows the head. Lower = smoother, less bob.")]
    public float eyeFollowRate = 18f;
    [Tooltip("The camera never trails the eyes by more than this (metres).")]
    public float maxEyeLag = 0.02f;
    [Tooltip("Distance from the eye to the rear sight when aiming down sights.")]
    public float adsEyeRelief = 0.12f;
    [Header("Rifle Recoil (full body)")]
    public float recoilPitch = 5f;
    public float recoilBack = 0.035f;
    public float recoilRecovery = 14f;

    [Header("Reload Pose")]
    // DayZ-style (Docs/AnimationReferences.md): pulled in low against the upper chest, muzzle up and across to the
    // left, rolled part-way so the magazine well faces the free hand. Elbows stay bent rather than arms out at eye level.
    [Tooltip("Rifle position during a reload, relative to the eyes (x right, y up, z forward).")]
    public Vector3 reloadPoseOffset = new Vector3(-0.01f, -0.18f, 0.36f);
    [Tooltip("Rifle rotation during a reload: pitch up, yaw across the body, cant so the magazine well faces the free hand.")]
    public Vector3 reloadPoseEuler = new Vector3(-45f, -40f, 70f);
    public float reloadPoseSpeed = 5f;

    float reloadBlend;
    Transform sightRear;
    float recoilKick;
    Vector3 eyePos;
    bool eyePlaced;
    // The player's controller (null for NPCs), which skips its own head bob while the body drives the camera.
    FPSController PlayerController => controller as FPSController;

    void UpdateEyeCamera()
    {
        if (!firstPersonView || head == null || aimCamera == null)
        {
            eyePlaced = false;
            if (PlayerController != null) PlayerController.cameraDrivenExternally = false;
            return;
        }
        if (PlayerController != null) PlayerController.cameraDrivenExternally = true;
        // Smooth in the player's own space, so only the head's bob is smoothed: moving (even sprinting)
        // never drags the camera back behind the eyes.
        Transform player = transform.parent;
        Vector3 target = player.InverseTransformPoint(head.position + player.rotation * eyeOffset);
        if (eyePlaced)
        {
            eyePos = Vector3.Lerp(eyePos, target, 1f - Mathf.Exp(-eyeFollowRate * Time.deltaTime));
            eyePos = target + Vector3.ClampMagnitude(eyePos - target, maxEyeLag);
        }
        else eyePos = target;
        eyePlaced = true;
        aimCamera.position = player.TransformPoint(eyePos);
    }

    [Header("Torso Clearance (full body only)")]
    [Tooltip("The rifle is kept at least this far from the spine (hips to neck), so it never sinks into the chest.")]
    public float torsoClearance = 0.2f;
    [Tooltip("Points on the rifle (gun space) that must stay clear: stock, receiver, grip, magazine, handguard, muzzle.")]
    public Vector3[] clearancePoints =
    {
        new Vector3(0f, 0f, -0.22f), new Vector3(0f, 0f, -0.08f), new Vector3(0f, -0.1f, -0.08f),
        new Vector3(0f, -0.16f, 0.04f), new Vector3(0f, 0f, 0.1f), new Vector3(0f, 0f, 0.25f), new Vector3(0f, 0f, 0.42f),
    };

    // Pushes the rifle forward (along the body's facing) until every clearance point is outside the torso.
    // The hands are IK'd to the rifle, so they move out with it.
    void KeepGunOutOfTorso()
    {
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        if (hips == null || neck == null || clearancePoints == null) return;
        Transform gunSpace = leftHandTarget != null ? leftHandTarget.parent : carriedGun;
        Vector3 a = hips.position, axis = neck.position - a;
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

        float push = 0f;
        foreach (var local in clearancePoints)
        {
            Vector3 p = gunSpace.TransformPoint(local);
            float t = Mathf.Clamp01(Vector3.Dot(p - a, axis) / axis.sqrMagnitude);
            Vector3 radial = p - (a + axis * t);
            radial.y = 0f;
            float along = Vector3.Dot(radial, forward);
            float side = (radial - forward * along).magnitude;
            if (side >= torsoClearance) continue;
            // Forward distance this point needs to be outside the clearance circle.
            float needed = Mathf.Sqrt(torsoClearance * torsoClearance - side * side);
            push = Mathf.Max(push, needed - along);
        }
        if (push > 0f) carriedGun.position += forward * push;
    }

    void SetHandGoal(AvatarIKGoal goal, Transform target, float weight)
    {
        if (target == null) weight = 0f;
        animator.SetIKPositionWeight(goal, weight);
        animator.SetIKRotationWeight(goal, weight);
        if (target == null) return;
        animator.SetIKPosition(goal, target.position);
        animator.SetIKRotation(goal, target.rotation);
    }

    void LateUpdate()
    {
        // True first person: collapse the head (face, beard, helmet, hair) so the camera isn't inside it.
        if (head != null) head.localScale = firstPersonView ? Vector3.zero : Vector3.one;
        recoilKick = Mathf.MoveTowards(recoilKick, 0f, recoilRecovery * recoilKick * Time.deltaTime + 0.5f * Time.deltaTime);
        UpdateEyeCamera();
    }
}
