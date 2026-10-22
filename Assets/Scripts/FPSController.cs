using UnityEngine;
using UnityEngine.InputSystem;

// Basic first-person controller: mouse look, walk, sprint, jump, crouch.
// Put this on a GameObject with a CharacterController and a child Camera.
[RequireComponent(typeof(CharacterController))]
public class FPSController : CharacterMotor
{
    [Header("References")]
    public Transform cameraPivot;

    [Header("Look")]
    public float mouseSensitivity = 0.1f;
    public float maxPitch = 85f;

    [Header("Free Look (hold middle mouse)")]
    [Tooltip("Third person: how far the camera can swing away from the body's facing (degrees).")]
    public float maxFreeLookYaw = 110f;
    // First person the view is the character's head, so it's held to a real neck's range of motion
    // (healthy adult: about 72-80 degrees of rotation each way, 70 extension (up), 55 flexion (down)).
    [Tooltip("First person: head turn left/right from the body's facing (degrees).")]
    public float neckRotation = 75f;
    [Tooltip("First person: head tilt up / down from where the body is aiming (degrees).")]
    public float neckExtension = 70f;
    public float neckFlexion = 55f;
    [Tooltip("How quickly the view returns to straight ahead when the button is released.")]
    public float freeLookReturnSpeed = 10f;

    [Header("Movement")]
    public float walkSpeed = 5f;
    public float sprintSpeed = 8.5f;
    public float crouchSpeed = 2.5f;
    public float acceleration = 12f;
    public float airControl = 0.3f;

    [Header("Jump & Gravity")]
    public float jumpHeight = 1.3f;
    public float gravity = -20f;
    public float coyoteTime = 0.12f;
    public float jumpBuffer = 0.12f;

    [Header("Crouch")]
    public float standHeight = 1.8f;
    public float crouchHeight = 1.0f;
    public float crouchTransitionSpeed = 10f;

    [Header("Head Bob")]
    public float bobFrequency = 9f;
    public float bobAmount = 0.05f;

    [Header("Fly (dev console)")]
    [Tooltip("Fly mode speed (m/s): look where you want to go, WASD, Space up, Ctrl/C down, Shift for faster.")]
    public float flySpeed = 15f;
    public float flyFastMultiplier = 4f;

    CharacterController controller;
    Vector3 horizontalVelocity;
    float verticalVelocity;
    float pitch;
    float freeLookYaw, freeLookPitch;
    float lastGroundedTime = -1f;
    float lastJumpPressedTime = -1f;
    bool isCrouching;
    float bobTimer;
    float cameraBaseY;

    public override Vector3 Velocity => horizontalVelocity + Vector3.up * verticalVelocity;
    // isGrounded flickers on slopes and steps, so treat a very recent contact as grounded.
    // Flying counts as grounded (and still), so the body just stands while it floats.
    public override bool IsGrounded => Flying || Time.time - lastGroundedTime < 0.1f;
    // Fly mode (no gravity, no collisions): for getting around and testing.
    public bool Flying { get; private set; }
    public override bool IsCrouching => isCrouching;
    public bool IsSprinting => isSprinting;
    public override float WalkSpeed => walkSpeed;
    // Middle mouse held: looking around independently of the body (aiming and firing are blocked).
    public bool IsFreeLooking { get; private set; }

    // Set each frame by other systems (e.g. WeaponStance while aiming down sights).
    [HideInInspector] public float speedMultiplier = 1f;
    [HideInInspector] public float lookSensitivityScale = 1f;
    [HideInInspector] public bool sprintBlocked;
    [HideInInspector] public bool showCrosshair = true;
    // True first person: the camera is placed at the character's eyes by PlayerCharacterAnimator, so no head bob here.
    [HideInInspector] public bool cameraDrivenExternally;
    // Scales the head bob (CameraModeSwitcher fades it out in third person, where the body already bobs).
    [HideInInspector] public float bobScale = 1f;
    // Movement input from code (tests, cutscenes); overrides WASD while non-zero.
    [System.NonSerialized] public Vector2 scriptedMoveInput;
    [System.NonSerialized] public bool scriptedSprint;


    bool isSprinting;
    bool wasGrounded = true;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (cameraPivot == null && Camera.main != null)
            cameraPivot = Camera.main.transform;
        controller.height = standHeight;
        controller.center = new Vector3(0f, standHeight / 2f, 0f);
        cameraBaseY = standHeight - 0.15f;
    }

    void OnEnable() => LockCursor(true);

    // The player also pauses its weapon and camera modes (driving, knocked down, dead).
    public override void SetControlEnabled(bool on)
    {
        if (!on) SetFlying(false);
        base.SetControlEnabled(on);
        var stance = GetComponentInChildren<WeaponStance>(true);
        if (stance != null) stance.enabled = on;
        var gun = GetComponentInChildren<HitscanGun>(true);
        if (gun != null) gun.enabled = on;
        var switcher = GetComponent<CameraModeSwitcher>();
        if (switcher != null) switcher.enabled = on;
        var hands = GetComponent<PlayerInteractor>();
        if (hands != null) hands.enabled = on;
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        if (keyboard.escapeKey.wasPressedThisFrame) LockCursor(false);
        if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked) LockCursor(true);

        Look(mouse);
        if (Flying)
        {
            Fly(keyboard);
            return;
        }
        Crouch(keyboard);
        Move(keyboard);
        HeadBob();
    }

    public void SetFlying(bool on)
    {
        if (on == Flying) return;
        Flying = on;
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        isCrouching = false;
        controller.enabled = !on;
        if (on) return;
        // Set down on the ground below, so stopping in mid-air isn't a fatal fall.
        if (Physics.Raycast(transform.position + Vector3.up, Vector3.down, out RaycastHit hit, 5000f, CharacterLayers.Environment, QueryTriggerInteraction.Ignore))
            transform.position = hit.point;
        lastGroundedTime = Time.time;
        wasGrounded = true;
    }

    // Moves straight through everything, towards where the camera looks.
    void Fly(Keyboard keyboard)
    {
        Vector3 forward = cameraPivot != null ? cameraPivot.forward : transform.forward;
        Vector3 move = Vector3.zero;
        if (keyboard.wKey.isPressed) move += forward;
        if (keyboard.sKey.isPressed) move -= forward;
        if (keyboard.dKey.isPressed) move += transform.right;
        if (keyboard.aKey.isPressed) move -= transform.right;
        if (keyboard.spaceKey.isPressed) move += Vector3.up;
        if (keyboard.leftCtrlKey.isPressed || keyboard.cKey.isPressed) move -= Vector3.up;
        float speed = flySpeed * (keyboard.leftShiftKey.isPressed ? flyFastMultiplier : 1f);
        transform.position += Vector3.ClampMagnitude(move, 1f) * speed * Time.deltaTime;
    }

    void Look(Mouse mouse)
    {
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        Vector2 delta = locked ? mouse.delta.ReadValue() * mouseSensitivity * lookSensitivityScale : Vector2.zero;

        // Free look: the mouse turns only the view; the body keeps its heading (and walks that way).
        IsFreeLooking = locked && mouse.middleButton.isPressed;
        if (IsFreeLooking)
        {
            freeLookYaw += delta.x;
            freeLookPitch -= delta.y;
        }
        else
        {
            pitch = Mathf.Clamp(pitch - delta.y, -maxPitch, maxPitch);
            transform.Rotate(Vector3.up, delta.x);
            float k = 1f - Mathf.Exp(-freeLookReturnSpeed * Time.deltaTime);
            freeLookYaw = Mathf.Lerp(freeLookYaw, 0f, k);
            freeLookPitch = Mathf.Lerp(freeLookPitch, 0f, k);
        }
        // Limits apply every frame, so switching to first person mid-look pulls the view in to the neck's range.
        // cameraDrivenExternally is true exactly when the view is the character's own eyes.
        bool firstPerson = cameraDrivenExternally;
        float maxYaw = firstPerson ? neckRotation : maxFreeLookYaw;
        freeLookYaw = Mathf.Clamp(freeLookYaw, -maxYaw, maxYaw);
        float up = firstPerson ? neckExtension : maxPitch, down = firstPerson ? neckFlexion : maxPitch;
        freeLookPitch = Mathf.Clamp(pitch + freeLookPitch, Mathf.Max(-maxPitch, pitch - up), Mathf.Min(maxPitch, pitch + down)) - pitch;
        if (cameraPivot != null)
            cameraPivot.localRotation = Quaternion.Euler(pitch + freeLookPitch, freeLookYaw, 0f);
    }

    void Crouch(Keyboard keyboard)
    {
        bool wantsCrouch = keyboard.leftCtrlKey.isPressed || keyboard.cKey.isPressed;

        // Only stand back up if nothing is overhead.
        if (!wantsCrouch && isCrouching)
        {
            float radius = controller.radius * 0.95f;
            Vector3 origin = transform.position + Vector3.up * (controller.height - radius);
            float distance = standHeight - controller.height + 0.05f;
            if (Physics.SphereCast(origin, radius, Vector3.up, out _, distance, CharacterLayers.Environment, QueryTriggerInteraction.Ignore))
                wantsCrouch = true;
        }
        isCrouching = wantsCrouch;

        float targetHeight = isCrouching ? crouchHeight : standHeight;
        float h = Mathf.Lerp(controller.height, targetHeight, crouchTransitionSpeed * Time.deltaTime);
        controller.height = h;
        controller.center = new Vector3(0f, h / 2f, 0f);
        cameraBaseY = h - 0.15f;
    }

    void Move(Keyboard keyboard)
    {
        bool grounded = controller.isGrounded;
        if (grounded) lastGroundedTime = Time.time;

        Vector2 input = Vector2.zero;
        if (keyboard.wKey.isPressed) input.y += 1f;
        if (keyboard.sKey.isPressed) input.y -= 1f;
        if (keyboard.dKey.isPressed) input.x += 1f;
        if (keyboard.aKey.isPressed) input.x -= 1f;
        if (scriptedMoveInput != Vector2.zero) input = scriptedMoveInput;
        input = Vector2.ClampMagnitude(input, 1f);

        bool sprinting = (keyboard.leftShiftKey.isPressed || scriptedSprint) && !isCrouching && !sprintBlocked && input.y > 0f;
        isSprinting = sprinting && grounded;
        float speed = (isCrouching ? crouchSpeed : sprinting ? sprintSpeed : walkSpeed) * speedMultiplier;

        Vector3 wishDir = transform.right * input.x + transform.forward * input.y;
        Vector3 targetVelocity = wishDir * speed;
        float accel = grounded ? acceleration : acceleration * airControl;
        horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVelocity, accel * speed * Time.deltaTime);

        // Jump with coyote time and input buffering so it feels forgiving.
        if (keyboard.spaceKey.wasPressedThisFrame) lastJumpPressedTime = Time.time;
        bool canJump = Time.time - lastGroundedTime <= coyoteTime && !isCrouching;
        if (canJump && Time.time - lastJumpPressedTime <= jumpBuffer)
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            lastJumpPressedTime = -1f;
            lastGroundedTime = -1f;
            OnJumped();
        }

        // Report landings with the impact speed (only real falls, not stepping off a stair).
        if (grounded && !wasGrounded && verticalVelocity < -3f) OnLanded(-verticalVelocity);
        wasGrounded = grounded;
        if (!controller.enabled) return; // the landing knocked the player down

        if (grounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;

        CollisionFlags flags = controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
        if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f) verticalVelocity = 0f;
    }

    void HeadBob()
    {
        if (cameraPivot == null || cameraDrivenExternally) return;

        float planarSpeed = horizontalVelocity.magnitude;
        if (controller.isGrounded && planarSpeed > 0.5f)
            bobTimer += Time.deltaTime * bobFrequency * (planarSpeed / walkSpeed);
        else
            bobTimer = 0f;

        float bob = Mathf.Sin(bobTimer) * bobAmount * bobScale;
        // Settle back over the capsule too (first person leaves the pivot wherever the head was).
        cameraPivot.localPosition = Vector3.Lerp(cameraPivot.localPosition, new Vector3(0f, cameraBaseY + bob, 0f), 15f * Time.deltaTime);
    }

    // Kicks the view up (and optionally sideways), e.g. from weapon recoil.
    public void AddRecoil(float pitchUp, float yaw = 0f)
    {
        pitch = Mathf.Clamp(pitch - pitchUp, -maxPitch, maxPitch);
        transform.Rotate(Vector3.up, yaw);
    }

    void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void OnGUI()
    {
        if (Cursor.lockState != CursorLockMode.Locked || !showCrosshair) return;
        // Simple crosshair.
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        GUI.DrawTexture(new Rect(cx - 1, cy - 8, 2, 16), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - 8, cy - 1, 16, 2), Texture2D.whiteTexture);
    }
}
