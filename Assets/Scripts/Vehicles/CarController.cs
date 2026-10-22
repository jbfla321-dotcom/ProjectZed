using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Simple arcade-leaning car on Unity's WheelColliders: rear-wheel drive, speed-sensitive steering, brakes,
// reverse, handbrake (loosens the rear grip for slides), anti-roll bars and a little downforce.
// Only takes input while someone is driving (see VehicleEntry).
[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    [System.Serializable]
    public class Wheel
    {
        public WheelCollider collider;
        public Transform visual;
        public bool front;
        public bool left;
        [HideInInspector] public Quaternion visualOffset;
    }

    public Wheel[] wheels = new Wheel[4];

    [Header("Engine & Brakes")]
    [Tooltip("Drive torque per rear wheel (Nm).")]
    public float motorTorque = 900f;
    public float reverseTorque = 500f;
    public float brakeTorque = 2500f;
    public float handbrakeTorque = 4000f;
    [Tooltip("Top speed (m/s). 40 m/s is about 145 km/h.")]
    public float topSpeed = 40f;
    public float maxReverseSpeed = 8f;

    [Header("Steering")]
    public float maxSteerAngle = 32f;
    [Tooltip("Steering angle left at top speed, as a fraction of the maximum.")]
    [Range(0f, 1f)] public float highSpeedSteer = 0.3f;
    public float steerSpeed = 3f;

    [Header("Handling")]
    public float antiRoll = 8000f;
    public float downforce = 3f;
    [Tooltip("Rear sideways grip while the handbrake is held (1 = normal).")]
    [Range(0.1f, 1f)] public float handbrakeGrip = 0.45f;

    public bool HasDriver { get; set; }
    // Driving input from code (tests, AI): x = steer, y = throttle; overrides the keyboard while non-zero.
    [System.NonSerialized] public Vector2 scriptedInput;
    public float Speed => Vector3.Dot(body.linearVelocity, transform.forward);

    // Every active car, so finding a nearby one doesn't need a scene search.
    static readonly List<CarController> cars = new List<CarController>();
    public static IReadOnlyList<CarController> All => cars;
    void OnEnable() => cars.Add(this);
    void OnDisable() => cars.Remove(this);

    Rigidbody body;
    float steer;
    float rearStiffness;
    Vector3 lastVelocity; // before this step's collisions, for running people over

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        foreach (var w in wheels)
            if (w.visual != null) w.visualOffset = Quaternion.Inverse(transform.rotation) * w.visual.rotation;
        foreach (var w in wheels)
            if (!w.front) { rearStiffness = w.collider.sidewaysFriction.stiffness; break; }
    }

    void FixedUpdate()
    {
        float throttle = 0f, steerInput = 0f;
        bool handbrake = false;
        var kb = Keyboard.current;
        if (HasDriver && kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) throttle -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steerInput += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steerInput -= 1f;
            handbrake = kb.spaceKey.isPressed;
        }
        else handbrake = true; // parked
        if (scriptedInput != Vector2.zero) { steerInput = scriptedInput.x; throttle = scriptedInput.y; handbrake = false; }

        float speed = Speed;
        float speed01 = Mathf.Clamp01(Mathf.Abs(speed) / topSpeed);
        steer = Mathf.MoveTowards(steer, steerInput, steerSpeed * Time.fixedDeltaTime);
        float steerAngle = steer * maxSteerAngle * Mathf.Lerp(1f, highSpeedSteer, speed01);

        // Pressing against the direction of travel brakes first, then drives the other way once nearly stopped.
        bool braking = (throttle < 0f && speed > 1f) || (throttle > 0f && speed < -1f);
        float drive = 0f;
        if (!braking)
        {
            if (throttle > 0f && speed < topSpeed) drive = throttle * motorTorque;
            else if (throttle < 0f && speed > -maxReverseSpeed) drive = throttle * reverseTorque;
        }

        foreach (var w in wheels)
        {
            if (w.front) w.collider.steerAngle = steerAngle;
            else w.collider.motorTorque = drive;
            float brake = braking ? brakeTorque : 0f;
            if (!w.front && handbrake) brake = Mathf.Max(brake, handbrakeTorque);
            // Engine braking when coasting, so the car doesn't roll forever.
            if (throttle == 0f && !handbrake) brake = Mathf.Max(brake, 80f);
            w.collider.brakeTorque = brake;

            if (!w.front)
            {
                var f = w.collider.sidewaysFriction;
                f.stiffness = HasDriver && handbrake ? rearStiffness * handbrakeGrip : rearStiffness;
                w.collider.sidewaysFriction = f;
            }
        }

        ApplyAntiRoll(true);
        ApplyAntiRoll(false);
        body.AddForce(-transform.up * downforce * body.linearVelocity.sqrMagnitude);
        lastVelocity = body.linearVelocity;
    }

    void Update()
    {
        foreach (var w in wheels)
        {
            if (w.visual == null) continue;
            w.collider.GetWorldPose(out Vector3 pos, out Quaternion rot);
            w.visual.SetPositionAndRotation(pos, rot * w.visualOffset);
        }
    }

    // Hitting a person: hurt/knock them down, and keep going instead of stopping dead against their
    // movement capsule (their ragdoll limbs, now loose, take the real hit).
    void OnCollisionEnter(Collision collision)
    {
        if (!(collision.collider is CharacterController)) return;
        var victim = collision.collider.GetComponent<CharacterHealth>();
        if (victim == null || !victim.IsUp) return;
        float closing = collision.relativeVelocity.magnitude;
        victim.HitByVehicle(closing, lastVelocity);
        if (!victim.IsUp) body.linearVelocity = lastVelocity * 0.92f;
    }

    // Pushes down the more compressed side of an axle and lifts the other, so the car doesn't roll over in turns.
    void ApplyAntiRoll(bool frontAxle)
    {
        WheelCollider left = null, right = null;
        foreach (var w in wheels)
        {
            if (w.front != frontAxle) continue;
            if (w.left) left = w.collider; else right = w.collider;
        }
        if (left == null || right == null) return;
        float travelL = Travel(left, out bool groundedL), travelR = Travel(right, out bool groundedR);
        float force = (travelL - travelR) * antiRoll;
        if (groundedL) body.AddForceAtPosition(left.transform.up * -force, left.transform.position);
        if (groundedR) body.AddForceAtPosition(right.transform.up * force, right.transform.position);
    }

    static float Travel(WheelCollider wc, out bool grounded)
    {
        grounded = wc.GetGroundHit(out WheelHit hit);
        if (!grounded) return 1f;
        return (-wc.transform.InverseTransformPoint(hit.point).y - wc.radius) / wc.suspensionDistance;
    }
}
