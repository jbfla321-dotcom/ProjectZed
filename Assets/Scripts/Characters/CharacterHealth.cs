using System.Collections.Generic;
using UnityEngine;

// Health for a character (player or NPC) with a ragdoll body. Gunshots (by body part), cars and hard
// landings hurt it. Non-lethal heavy hits knock it down: ragdoll, then it gets back up. Lethal ones kill
// it: ragdoll, then it respawns where it started. The player gets a death camera and a health bar.
[RequireComponent(typeof(CharacterMotor))]
public class CharacterHealth : MonoBehaviour
{
    public enum State { Alive, KnockedDown, Dead }

    public float maxHealth = 100f;
    public float respawnDelay = 5f;

    [Header("Gunshots")]
    public float headMultiplier = 4f; // a rifle headshot (25 x 4) is lethal
    public float limbMultiplier = 0.7f;
    [Tooltip("Push (N·s) the killing shot gives the limb it hits.")]
    public float shotKnockback = 40f;

    [Header("Cars")]
    [Tooltip("Closing speed (m/s) below which a car just bumps the character.")]
    public float carKnockdownSpeed = 4f;
    [Tooltip("Damage per m/s above that speed (about 15 m/s kills).")]
    public float carDamagePerSpeed = 9f;

    [Header("Falls")]
    [Tooltip("Landing faster than this (m/s, about a 3.6 m drop) knocks the character down.")]
    public float fallKnockdownSpeed = 12f;
    [Tooltip("Landing faster than this (about a 10 m drop) is fatal.")]
    public float fallLethalSpeed = 20f;

    [Header("Knockdown")]
    public float minDownTime = 1.5f;

    [Header("Player death camera")]
    public Vector3 deathCameraOffset = new Vector3(0f, 2.5f, -4f);

    public State Current { get; private set; }
    public float Health { get; private set; }
    public bool IsUp => Current == State.Alive && (ragdoll == null || !ragdoll.IsRecovering);

    CharacterMotor motor;
    Ragdoll ragdoll;
    PlayerCharacterAnimator body;
    Camera viewCamera; // player only
    Transform headBone;
    readonly HashSet<Transform> limbBones = new HashSet<Transform>();
    Vector3 spawnPosition;
    Quaternion spawnRotation;
    float downSince;
    Vector3 camVelocity;
    GUIStyle hudStyle;

    void Awake()
    {
        motor = GetComponent<CharacterMotor>();
        ragdoll = GetComponentInChildren<Ragdoll>();
        body = GetComponentInChildren<PlayerCharacterAnimator>();
        var switcher = GetComponent<CameraModeSwitcher>();
        if (switcher != null) viewCamera = switcher.viewCamera;
        if (body != null)
        {
            var a = body.GetComponent<Animator>();
            headBone = a.GetBoneTransform(HumanBodyBones.Head);
            foreach (var b in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftLowerArm,
                         HumanBodyBones.RightLowerArm, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
                         HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg })
                limbBones.Add(a.GetBoneTransform(b));
        }
        spawnPosition = transform.position;
        spawnRotation = transform.rotation;
        Health = maxHealth;
    }

    void OnEnable() => motor.Landed += OnLanded;
    void OnDisable() => motor.Landed -= OnLanded;

    // A bullet: damage by the body part hit; the killing shot throws the body.
    public void TakeShot(float damage, Vector3 point, Vector3 direction, Collider part)
    {
        if (Current == State.Dead) return;
        Transform hit = part != null ? part.transform : null;
        float multiplier = hit != null && hit == headBone ? headMultiplier : hit != null && limbBones.Contains(hit) ? limbMultiplier : 1f;
        Health -= damage * multiplier;
        if (Health <= 0f)
        {
            Die(motor.Velocity);
            if (ragdoll != null) ragdoll.AddImpulse(point, direction.normalized * shotKnockback);
        }
        else if (Current == State.Alive && body != null) body.PlayHitReaction();
    }

    // A car hit at `closingSpeed`; the body is thrown with the car's velocity.
    public void HitByVehicle(float closingSpeed, Vector3 carVelocity)
    {
        if (Current != State.Alive || closingSpeed < carKnockdownSpeed) return;
        Health -= (closingSpeed - carKnockdownSpeed) * carDamagePerSpeed;
        Vector3 throwVelocity = carVelocity * 0.9f + Vector3.up * Mathf.Min(closingSpeed * 0.25f, 4f);
        if (Health <= 0f) Die(throwVelocity);
        else KnockDown(throwVelocity, 0f);
    }

    // Ragdoll for a while, then get back up (unless the damage kills).
    public void KnockDown(Vector3 velocity, float damage)
    {
        if (Current != State.Alive) return;
        Health -= damage;
        if (Health <= 0f) { Die(velocity); return; }
        Current = State.KnockedDown;
        GoDown(velocity);
    }

    void Die(Vector3 velocity)
    {
        Health = 0f;
        bool wasDown = Current != State.Alive;
        Current = State.Dead;
        if (!wasDown) GoDown(velocity);
        downSince = Time.time;
    }

    void GoDown(Vector3 velocity)
    {
        downSince = Time.time;
        motor.SetControlEnabled(false);
        if (body != null) body.firstPersonView = false; // the death/knockdown view is from outside
        if (ragdoll != null) ragdoll.GoLimp(velocity);
        camVelocity = Vector3.zero;
    }

    void OnLanded(float impactSpeed)
    {
        if (impactSpeed < fallKnockdownSpeed) return;
        float t = Mathf.InverseLerp(fallKnockdownSpeed, fallLethalSpeed, impactSpeed);
        KnockDown(motor.Velocity, Mathf.Lerp(20f, maxHealth, t * t));
    }

    void Update()
    {
        if (Current == State.KnockedDown && ragdoll != null && ragdoll.IsLimp
            && Time.time - downSince > minDownTime && ragdoll.Settled)
            ragdoll.Recover();

        if (Current == State.KnockedDown && ragdoll != null && !ragdoll.IsLimp && !ragdoll.IsRecovering)
        {
            Current = State.Alive;
            motor.SetControlEnabled(true);
        }

        if (Current == State.Dead && Time.time - downSince > respawnDelay) Respawn();
    }

    void Respawn()
    {
        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        if (ragdoll != null) ragdoll.ResetToAnimated();
        Health = maxHealth;
        Current = State.Alive;
        motor.SetControlEnabled(true);
    }

    void LateUpdate()
    {
        // Player: while down, watch the body from a little behind and above.
        if (viewCamera == null || ragdoll == null || IsUp) return;
        Vector3 target = ragdoll.Hips.position;
        Vector3 back = Vector3.ProjectOnPlane(viewCamera.transform.forward, Vector3.up).normalized;
        if (back.sqrMagnitude < 0.01f) back = transform.forward;
        Vector3 desired = target + Quaternion.LookRotation(back) * deathCameraOffset;
        Transform cam = viewCamera.transform;
        cam.position = Vector3.SmoothDamp(cam.position, desired, ref camVelocity, 0.3f);
        cam.rotation = Quaternion.Slerp(cam.rotation, Quaternion.LookRotation(target - cam.position), 1f - Mathf.Exp(-8f * Time.deltaTime));
    }

    void OnGUI()
    {
        if (viewCamera == null) return; // player only
        if (hudStyle == null)
            hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        // Health bar, bottom left.
        var bar = new Rect(20, Screen.height - 44, 220, 18);
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(bar, Texture2D.whiteTexture);
        GUI.color = Color.Lerp(new Color(0.85f, 0.15f, 0.1f), new Color(0.2f, 0.8f, 0.3f), Health / maxHealth);
        GUI.DrawTexture(new Rect(bar.x + 2, bar.y + 2, (bar.width - 4) * Mathf.Clamp01(Health / maxHealth), bar.height - 4), Texture2D.whiteTexture);
        GUI.color = Color.white;
        if (Current == State.Dead)
            GUI.Label(new Rect(0, Screen.height * 0.4f, Screen.width, 50),
                $"You died - respawning in {Mathf.CeilToInt(respawnDelay - (Time.time - downSince))}", hudStyle);
    }

}
