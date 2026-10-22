using System.Collections.Generic;
using UnityEngine;

// The physical body of a character (built by Tools > Characters > Build Ragdolls). While alive its limb
// rigidbodies are kinematic and simply follow the animation (they're the bullet hitboxes). GoLimp hands
// the body to physics; Recover stands it back up through a get-up animation, blending out of the
// ragdoll pose so nothing pops; ResetToAnimated snaps it straight back (respawn).
[DefaultExecutionOrder(200)] // after the body driver, so the get-up blend has the final say on the pose
public class Ragdoll : MonoBehaviour
{
    [Tooltip("How long the get-up blends from the ragdoll pose into the animation (s).")]
    public float recoverBlendTime = 0.4f;
    [Tooltip("The hips must stay slower than this (m/s) for half a second to count as settled.")]
    public float settleSpeed = 0.3f;
    public float maxGetUpTime = 4f;

    public bool IsLimp { get; private set; }
    public bool IsRecovering { get; private set; }
    // The body has come to rest (safe to get up).
    public bool Settled => IsLimp && settledFor >= 0.5f;
    public Transform Hips => hips;

    Animator animator;
    PlayerCharacterAnimator driver;
    Rigidbody[] bodies;
    Collider[] limbColliders;
    Transform hips, head;
    Rigidbody hipsBody;
    Vector3 hipsFront; // the body's front in the hips' own space, to tell face-up from face-down
    float settledFor;

    // The carried rifle drops when the body goes limp and is put back in the hands after (unless it was
    // already on the ground, dropped on purpose: then it stays there).
    Transform gun;
    CarriedRifle rifle;
    bool droppedRifle;

    // Get-up blend: the ragdoll's pose when recovery started.
    Transform[] poseBones;
    Quaternion[] savedRotations;
    Vector3 savedHipsPosition;
    float recoverStart;
    bool getUpStarted;

    void Awake()
    {
        animator = GetComponent<Animator>();
        driver = GetComponent<PlayerCharacterAnimator>();
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        var list = new List<Rigidbody>(GetComponentsInChildren<Rigidbody>(true));
        if (driver != null && driver.carriedGun != null) list.RemoveAll(rb => rb.transform.IsChildOf(driver.carriedGun));
        bodies = list.ToArray();
        hipsBody = hips.GetComponent<Rigidbody>();
        hipsFront = Quaternion.Inverse(hips.rotation) * transform.forward; // measured standing
        var cols = new List<Collider>();
        foreach (var rb in bodies) cols.AddRange(rb.GetComponents<Collider>());
        limbColliders = cols.ToArray();
        // Limbs of the same body never push each other apart (the joints hold them together).
        for (int i = 0; i < limbColliders.Length; i++)
            for (int j = i + 1; j < limbColliders.Length; j++)
                Physics.IgnoreCollision(limbColliders[i], limbColliders[j]);

        if (driver != null && driver.carriedGun != null)
        {
            gun = driver.carriedGun;
            rifle = CarriedRifle.On(gun);
        }
        var bones = new List<Transform>(hips.GetComponentsInChildren<Transform>());
        if (gun != null) bones.RemoveAll(t => t.IsChildOf(gun));
        poseBones = bones.ToArray(); // parents before children
        savedRotations = new Quaternion[poseBones.Length];
    }

    // Hands the body to physics, every limb moving at `velocity` (e.g. the character's or a car's).
    public void GoLimp(Vector3 velocity)
    {
        if (IsLimp) return;
        IsLimp = true;
        IsRecovering = false;
        settledFor = 0f;
        if (driver != null) { driver.Recovering = false; driver.enabled = false; }
        animator.enabled = false;
        if (head != null) head.localScale = Vector3.one; // first person hides the head
        foreach (var rb in bodies)
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearVelocity = velocity;
            rb.angularVelocity = Vector3.zero;
        }
        DropGun(velocity);
    }

    // A push at a point (a bullet, a bumper), applied to the limb nearest to it.
    public void AddImpulse(Vector3 point, Vector3 impulse)
    {
        if (!IsLimp) return;
        Rigidbody nearest = hipsBody;
        float best = float.MaxValue;
        foreach (var rb in bodies)
        {
            float d = (rb.worldCenterOfMass - point).sqrMagnitude;
            if (d < best) { best = d; nearest = rb; }
        }
        nearest.AddForceAtPosition(impulse, point, ForceMode.Impulse);
    }

    // Stands back up through the get-up animation (face-up or face-down), starting where the body lies.
    public void Recover()
    {
        if (!IsLimp) return;
        for (int i = 0; i < poseBones.Length; i++) savedRotations[i] = poseBones[i].rotation;
        savedHipsPosition = hips.position;
        bool faceUp = Vector3.Dot(hips.rotation * hipsFront, Vector3.up) > 0f;
        // Which way the body lies: from the hips toward the head, flattened.
        Vector3 bodyAxis = Flat(head.position - hips.position);
        SetKinematic();

        // Start the get-up pose, then line its hips up with where the ragdoll's hips lie.
        Transform root = transform.parent;
        Vector3 ground = GroundBelow(hips.position);
        root.SetPositionAndRotation(ground, Quaternion.LookRotation(faceUp ? -bodyAxis : bodyAxis));
        animator.enabled = true;
        animator.Play(faceUp ? "GetUpFaceUp" : "GetUpFaceDown", 0, 0f);
        animator.Update(0f);
        Vector3 animatedAxis = Flat(head.position - hips.position);
        root.rotation = Quaternion.FromToRotation(animatedAxis, bodyAxis) * root.rotation;
        Vector3 shift = savedHipsPosition - hips.position;
        root.position = GroundBelow(root.position + new Vector3(shift.x, 0f, shift.z));

        IsLimp = false;
        IsRecovering = true;
        getUpStarted = false;
        recoverStart = Time.time;
        if (driver != null) { driver.Recovering = true; driver.enabled = true; }
    }

    // Straight back to the animated body (respawn).
    public void ResetToAnimated()
    {
        SetKinematic();
        IsLimp = false;
        IsRecovering = false;
        animator.enabled = true;
        animator.Rebind();
        if (driver != null) { driver.Recovering = false; driver.enabled = true; }
        ReattachGun();
    }

    void Update()
    {
        if (IsLimp && hipsBody != null)
            settledFor = hipsBody.linearVelocity.magnitude < settleSpeed ? settledFor + Time.deltaTime : 0f;

        if (!IsRecovering) return;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        bool inGetUp = state.IsName("GetUpFaceUp") || state.IsName("GetUpFaceDown");
        if (inGetUp) getUpStarted = true;
        if ((getUpStarted && !inGetUp && !animator.IsInTransition(0)) || Time.time - recoverStart > maxGetUpTime)
        {
            IsRecovering = false;
            if (driver != null) driver.Recovering = false;
            ReattachGun();
        }
    }

    void LateUpdate()
    {
        if (!IsRecovering) return;
        float w = Mathf.SmoothStep(0f, 1f, (Time.time - recoverStart) / recoverBlendTime);
        if (w >= 1f) return;
        hips.position = Vector3.Lerp(savedHipsPosition, hips.position, w);
        for (int i = 0; i < poseBones.Length; i++)
            poseBones[i].rotation = Quaternion.Slerp(savedRotations[i], poseBones[i].rotation, w);
    }

    void SetKinematic()
    {
        foreach (var rb in bodies)
        {
            if (rb.isKinematic) continue;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        }
    }

    void DropGun(Vector3 velocity)
    {
        if (rifle == null || !rifle.IsHeld) return;
        rifle.Drop(velocity, limbColliders);
        droppedRifle = true;
    }

    void ReattachGun()
    {
        if (!droppedRifle) return;
        droppedRifle = false;
        rifle.Attach();
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
    }

    static Vector3 GroundBelow(Vector3 p)
    {
        return Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 10f, CharacterLayers.Environment, QueryTriggerInteraction.Ignore)
            ? hit.point : p;
    }
}
