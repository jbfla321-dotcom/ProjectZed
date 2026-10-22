using UnityEngine;
using UnityEngine.InputSystem;

// Lets the player get into a car (E when close) and drive it, and out again (E). While driving, the
// on-foot systems (movement, weapon, camera modes) are paused, the body is hidden, and the view camera
// becomes a smooth chase camera behind the car.
public class VehicleEntry : MonoBehaviour
{
    public Key useKey = Key.E;
    [Tooltip("How close (m) to a car's side the player must be to get in.")]
    public float reach = 2.5f;

    [Header("Chase Camera")]
    public Vector3 chaseOffset = new Vector3(0f, 2.2f, -6.5f);
    public float chaseLookHeight = 1.2f;
    public float followSharpness = 6f;
    public float collisionRadius = 0.25f;
    [Tooltip("Hold middle mouse to orbit the camera around the car; it swings back behind on release.")]
    public float orbitSensitivity = 0.15f;
    public float orbitReturnSpeed = 4f;

    [Header("On-foot parts (found on this player if empty)")]
    public FPSController controller;
    public CameraModeSwitcher cameraSwitcher;
    public GameObject body;
    [Tooltip("Getting out faster than this (m/s) throws the player out as a ragdoll.")]
    public float bailOutSpeed = 6f;
    [Tooltip("Damage per m/s above that speed (bailing out at 60 km/h costs about half your health).")]
    public float bailOutDamagePerSpeed = 5f;

    public CarController Car { get; private set; }

    CharacterHealth health;
    Camera viewCamera;
    CarController nearby;
    float nextScan;
    Vector3 camVelocity;
    float orbitYaw, orbitPitch;
    GUIStyle promptStyle;

    void Awake()
    {
        if (controller == null) controller = GetComponent<FPSController>();
        if (cameraSwitcher == null) cameraSwitcher = GetComponent<CameraModeSwitcher>();
        health = GetComponent<CharacterHealth>();
        viewCamera = cameraSwitcher.viewCamera;
        if (body == null)
        {
            var bodyDriver = GetComponentInChildren<PlayerCharacterAnimator>();
            if (bodyDriver != null) body = bodyDriver.gameObject;
        }
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (Car == null && Time.time >= nextScan)
        {
            nextScan = Time.time + 0.2f;
            nearby = FindNearbyCar();
        }
        if (kb == null || !kb[useKey].wasPressedThisFrame) return;
        if (Car != null) Exit();
        else if (nearby != null && (health == null || health.IsUp)) Enter(nearby);
    }

    CarController FindNearbyCar()
    {
        CarController best = null;
        float bestDistance = reach;
        foreach (var car in CarController.All)
        {
            if (car.HasDriver) continue;
            // Distance to the car's body, not its centre, so long and short cars feel the same.
            var col = car.GetComponent<Collider>();
            Vector3 closest = col != null ? col.ClosestPoint(transform.position + Vector3.up) : car.transform.position;
            float d = Vector3.Distance(closest, transform.position + Vector3.up);
            if (d < bestDistance) { bestDistance = d; best = car; }
        }
        return best;
    }

    void Enter(CarController car)
    {
        Car = car;
        car.HasDriver = true;
        SetOnFoot(false);
        transform.SetParent(car.transform, true);
        transform.localPosition = new Vector3(-0.4f, 0.4f, 0f);
        transform.localRotation = Quaternion.identity;
        camVelocity = Vector3.zero;
        orbitYaw = orbitPitch = 0f;
    }

    void Exit()
    {
        var car = Car;
        Car = null;
        car.HasDriver = false;
        transform.SetParent(null, true);
        // Step out on the driver's (left) side, on the ground, upright and facing the way the car points.
        var col = car.GetComponent<Collider>();
        float halfWidth = col != null ? col.bounds.extents.x : 1f;
        Vector3 side = car.transform.position - Vector3.ProjectOnPlane(car.transform.right, Vector3.up).normalized * (halfWidth + 0.8f);
        if (Physics.Raycast(side + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 20f, CharacterLayers.Environment, QueryTriggerInteraction.Ignore))
            side.y = hit.point.y;
        Vector3 forward = Vector3.ProjectOnPlane(car.transform.forward, Vector3.up);
        transform.SetPositionAndRotation(side, Quaternion.LookRotation(forward.sqrMagnitude > 0.01f ? forward : Vector3.forward));
        SetOnFoot(true);
        // Bailing out of a moving car: tumble out with the car's momentum.
        var carVelocity = car.GetComponent<Rigidbody>().linearVelocity;
        if (health != null && carVelocity.magnitude > bailOutSpeed)
            health.KnockDown(carVelocity, (carVelocity.magnitude - bailOutSpeed) * bailOutDamagePerSpeed);
    }

    void SetOnFoot(bool onFoot)
    {
        controller.SetControlEnabled(onFoot);
        if (body != null) body.SetActive(onFoot);
    }

    void LateUpdate()
    {
        if (Car == null) return;
        // Behind the car along its heading (ignoring its pitch and roll), smoothed, kept out of walls.
        Transform car = Car.transform;
        Vector3 flatForward = Vector3.ProjectOnPlane(car.forward, Vector3.up).normalized;
        Quaternion heading = Quaternion.LookRotation(flatForward.sqrMagnitude > 0.01f ? flatForward : Vector3.forward);
        Vector3 lookAt = car.position + Vector3.up * chaseLookHeight;
        UpdateOrbit();
        Vector3 desired = car.position + heading * Quaternion.Euler(orbitPitch, orbitYaw, 0f) * chaseOffset;
        Vector3 toCam = desired - lookAt;
        if (Physics.SphereCast(lookAt, collisionRadius, toCam.normalized, out RaycastHit hit, toCam.magnitude, CharacterLayers.Environment, QueryTriggerInteraction.Ignore)
            && !hit.transform.IsChildOf(car))
            desired = lookAt + toCam.normalized * Mathf.Max(hit.distance - 0.1f, 1f);
        Transform cam = viewCamera.transform;
        cam.position = Vector3.SmoothDamp(cam.position, desired, ref camVelocity, 1f / followSharpness);
        cam.rotation = Quaternion.LookRotation(lookAt - cam.position);
    }

    void UpdateOrbit()
    {
        var mouse = Mouse.current;
        if (mouse != null && mouse.middleButton.isPressed && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 delta = mouse.delta.ReadValue() * orbitSensitivity;
            orbitYaw = Mathf.Repeat(orbitYaw + delta.x + 180f, 360f) - 180f;
            orbitPitch = Mathf.Clamp(orbitPitch - delta.y, -15f, 50f);
        }
        else
        {
            float k = 1f - Mathf.Exp(-orbitReturnSpeed * Time.deltaTime);
            orbitYaw = Mathf.Lerp(orbitYaw, 0f, k);
            orbitPitch = Mathf.Lerp(orbitPitch, 0f, k);
        }
    }

    void OnGUI()
    {
        if (Car != null || nearby == null) return;
        if (promptStyle == null)
            promptStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        GUI.Label(new Rect(0, Screen.height * 0.6f, Screen.width, 40), $"Press {useKey} to drive", promptStyle);
    }
}
