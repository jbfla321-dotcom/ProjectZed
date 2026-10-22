using UnityEngine;
using UnityEngine.InputSystem;

// One view camera for both perspectives (V toggles). In first person it sits at the eye pivot, which the
// character's head drives (DayZ-style true first person: you see your own body). In third person it slides
// out over the shoulder. The eye pivot is always the aim pivot, so mouse look, recoil and aiming behave
// the same in both. Runs after the body has placed the eye pivot for the frame.
[DefaultExecutionOrder(100)]
public class CameraModeSwitcher : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The aim pivot at the character's eyes (mouse look turns it).")]
    public Transform eyePivot;
    [Tooltip("The only rendering camera.")]
    public Camera viewCamera;
    public PlayerCharacterAnimator bodyDriver;
    public HitscanGun gun;

    [Header("Third-Person Camera")]
    public Key toggleKey = Key.V;
    public bool startInThirdPerson;
    public Vector3 shoulderOffset = new Vector3(0.6f, 0.2f, -2.6f);
    [Tooltip("Shoulder offset while aiming (closer, so the zoomed view is over the shoulder).")]
    public Vector3 aimShoulderOffset = new Vector3(0.55f, 0.1f, -1.25f);
    public float zoomSpeed = 5f;
    public float collisionRadius = 0.2f;
    public LayerMask collisionMask = ~0;

    public bool IsThirdPerson => thirdPerson;

    // Set by WeaponStance (0 = hip, 1 = aiming): third person pulls in closer over the shoulder.
    [HideInInspector] public float aimBlend;

    bool thirdPerson;
    float zoom; // 0 = at the eyes, 1 = full shoulder offset
    FPSController controller;

    void Start()
    {
        controller = GetComponent<FPSController>();
        thirdPerson = startInThirdPerson;
        zoom = thirdPerson ? 1f : 0f;
        // Shots go where the crosshair (centre of the view camera) points, in either perspective.
        if (gun != null) gun.aimOrigin = viewCamera.transform;
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
            thirdPerson = !thirdPerson;
    }

    void LateUpdate()
    {
        zoom = Mathf.MoveTowards(zoom, thirdPerson ? 1f : 0f, zoomSpeed * Time.deltaTime);
        // The body becomes the first-person view (camera follows the head, head hidden) once the camera is home.
        if (bodyDriver != null) bodyDriver.firstPersonView = zoom < 0.02f;
        if (controller != null) controller.bobScale = 1f - zoom;
        PlaceCamera();
    }

    void PlaceCamera()
    {
        float t = Mathf.SmoothStep(0f, 1f, zoom);
        Vector3 offset = Vector3.Lerp(shoulderOffset, aimShoulderOffset, aimBlend) * t;
        Vector3 desired = eyePivot.position + eyePivot.rotation * offset;
        Vector3 toCamera = desired - eyePivot.position;
        float distance = toCamera.magnitude;

        // Pull in instead of clipping through walls. Characters (the player's own capsule and limbs) are
        // left out, so the player never blocks their own camera.
        if (distance > 0.001f && Physics.SphereCast(eyePivot.position, collisionRadius, toCamera / distance, out RaycastHit hit,
                distance, collisionMask & CharacterLayers.Environment, QueryTriggerInteraction.Ignore))
            desired = eyePivot.position + toCamera / distance * Mathf.Max(hit.distance - 0.05f, 0.1f);

        viewCamera.transform.SetPositionAndRotation(desired, eyePivot.rotation);
    }
}
