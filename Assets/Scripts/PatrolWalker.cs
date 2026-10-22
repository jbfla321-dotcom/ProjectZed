using UnityEngine;

// NPC brain: walks a set number of steps, turns around on the spot, walks back, and repeats forever.
// Steps are counted from the animated feet (each time the other foot becomes the planted one), so the
// count matches what you see. The body is a copy of the player's (PlayerCharacterAnimator), driven by this.
[RequireComponent(typeof(CharacterController))]
public class PatrolWalker : CharacterMotor
{
    [Tooltip("Footfalls before turning around.")]
    public int steps = 20;
    [Tooltip("Walking speed (m/s). 1.6 matches the walk animation's own pace.")]
    public float walkSpeed = 1.6f;
    public float acceleration = 4f;
    [Tooltip("Turn-around speed (degrees/second).")]
    public float turnSpeed = 150f;
    [Tooltip("Seconds to stand idle at each end before turning around.")]
    public float pauseAtEnds = 0f;
    public float gravity = -20f;
    [Tooltip("Animator whose feet are counted (found in children if empty).")]
    public Animator body;

    public override Vector3 Velocity => velocity;
    public override bool IsGrounded => controller.isGrounded;
    public override float WalkSpeed => walkSpeed;

    // Which foot is planted must differ by this much (m) to count as a new step, so jitter isn't counted.
    const float StepHysteresis = 0.02f;

    CharacterController controller;
    Transform leftFoot, rightFoot;
    Vector3 velocity;
    int stepCount;
    bool leftPlanted;
    bool turning;
    float turnTargetYaw;
    float turnAfter;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (body == null) body = GetComponentInChildren<Animator>();
        leftFoot = body.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = body.GetBoneTransform(HumanBodyBones.RightFoot);
    }

    void Start() => StartWalking();

    void Update()
    {
        float speed = new Vector2(velocity.x, velocity.z).magnitude;
        if (turning)
        {
            // Stop, then turn on the spot.
            speed = Mathf.MoveTowards(speed, 0f, acceleration * Time.deltaTime);
            if (speed < 0.05f && Time.time >= turnAfter)
            {
                float yaw = Mathf.MoveTowardsAngle(transform.eulerAngles.y, turnTargetYaw, turnSpeed * Time.deltaTime);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                if (Mathf.Abs(Mathf.DeltaAngle(yaw, turnTargetYaw)) < 0.5f) StartWalking();
            }
        }
        else
        {
            speed = Mathf.MoveTowards(speed, walkSpeed, acceleration * Time.deltaTime);
            CountSteps();
            if (stepCount >= steps)
            {
                turning = true;
                turnTargetYaw = transform.eulerAngles.y + 180f;
                // Stopping takes speed / acceleration seconds; the pause starts once stopped.
                turnAfter = Time.time + speed / acceleration + pauseAtEnds;
            }
        }

        Vector3 horizontal = transform.forward * speed;
        velocity.y = controller.isGrounded && velocity.y < 0f ? -2f : velocity.y + gravity * Time.deltaTime;
        velocity = new Vector3(horizontal.x, velocity.y, horizontal.z);
        controller.Move(velocity * Time.deltaTime);
    }

    void StartWalking()
    {
        turning = false;
        stepCount = 0;
        leftPlanted = leftFoot.position.y < rightFoot.position.y;
    }

    void CountSteps()
    {
        float lead = rightFoot.position.y - leftFoot.position.y; // > 0: left foot is lower (planted)
        if (!leftPlanted && lead > StepHysteresis) { leftPlanted = true; stepCount++; }
        else if (leftPlanted && lead < -StepHysteresis) { leftPlanted = false; stepCount++; }
    }
}
