using UnityEngine;
using UnityEngine.InputSystem;

// The rifle's stance: lowered by default, raised to the hip while firing, aimed down sights on right click.
// Hold right click to aim: first person centres the sights; third person pulls the camera in over the shoulder.
// PlayerCharacterAnimator reads RaiseBlend / AimBlend to pose the rifle; this sets the gun,
// movement and camera values that depend on them.
public class WeaponStance : MonoBehaviour
{
    [Header("References")]
    public HitscanGun gun;
    public FPSController controller;
    public CameraModeSwitcher cameraSwitcher;
    public Camera viewCamera;

    [Header("Raise / Lower")]
    [Tooltip("Seconds the rifle stays up at the hip after the last shot.")]
    public float hipHoldTime = 0.6f;
    public float raiseSpeed = 12f;
    public float lowerSpeed = 3f;

    [Header("Aim Down Sights")]
    public float aimSpeed = 7f;
    public float aimFov = 55f;
    public float aimMoveMultiplier = 0.6f;
    public float aimLookMultiplier = 0.7f;

    [Header("Spread (degrees)")]
    public float hipSpread = 1.5f;
    public float aimSpread = 0.1f;

    public bool IsAiming { get; private set; }
    // 0 = lowered, 1 = raised (hip or sights).
    public float RaiseBlend { get; private set; }
    // 0 = hip, 1 = fully down the sights.
    public float AimBlend { get; private set; }

    // Another player's rifle (multiplayer): this stays disabled and the blends come from the network.
    public void SetBlends(float raise, float aim)
    {
        RaiseBlend = raise;
        AimBlend = aim;
    }

    float lastFireTime = -10f;
    float baseFov = 75f;

    void Awake()
    {
        if (viewCamera != null) baseFov = viewCamera.fieldOfView;
    }

    void OnEnable() { if (gun != null) gun.Fired += OnFired; }
    void OnDisable() { if (gun != null) gun.Fired -= OnFired; }
    void OnFired() => lastFireTime = Time.time;

    void Update()
    {
        var mouse = Mouse.current;
        IsAiming = mouse != null && Cursor.lockState == CursorLockMode.Locked && mouse.rightButton.isPressed;
        if (controller != null && (controller.IsSprinting || controller.IsFreeLooking)) IsAiming = false;
        bool armed = gun == null || gun.Held;
        if (!armed) IsAiming = false;

        float dt = Time.deltaTime;
        AimBlend = Mathf.MoveTowards(AimBlend, IsAiming ? 1f : 0f, aimSpeed * dt);
        // Firing and reloading bring the rifle up (so the magazine swap is in view).
        bool raised = armed && (IsAiming || Time.time - lastFireTime < hipHoldTime || (gun != null && gun.IsReloading));
        RaiseBlend = Mathf.MoveTowards(RaiseBlend, raised ? 1f : 0f, (raised ? raiseSpeed : lowerSpeed) * dt);

        float aim = Mathf.SmoothStep(0f, 1f, AimBlend);
        if (gun != null) gun.spread = Mathf.Lerp(hipSpread, aimSpread, aim);
        if (controller != null)
        {
            controller.speedMultiplier = Mathf.Lerp(1f, aimMoveMultiplier, aim);
            controller.lookSensitivityScale = Mathf.Lerp(1f, aimLookMultiplier, aim);
            controller.sprintBlocked = IsAiming;
            // First-person aiming uses the iron sights; third person keeps the crosshair.
            bool thirdPerson = cameraSwitcher != null && cameraSwitcher.IsThirdPerson;
            controller.showCrosshair = thirdPerson || AimBlend < 0.5f;
        }
        if (viewCamera != null) viewCamera.fieldOfView = Mathf.Lerp(baseFov, aimFov, aim);
        if (cameraSwitcher != null) cameraSwitcher.aimBlend = aim;
    }
}
