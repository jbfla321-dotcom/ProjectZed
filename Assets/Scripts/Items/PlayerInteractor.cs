using UnityEngine;
using UnityEngine.InputSystem;

// The player's hands for world items: look at something within reach and press F to use it (pick up a rifle
// or magazine, take from a crate); G drops the rifle. Shows what F would do at the bottom of the screen.
// Lives on the player (next to the FPSController).
public class PlayerInteractor : MonoBehaviour
{
    public Key interactKey = Key.F;
    public Key dropKey = Key.G;
    [Tooltip("How far from the player's chest things can be reached (m).")]
    public float reach = 2.2f;
    [Tooltip("Thickness of the look ray, so small things (a magazine) are easy to aim at.")]
    public float lookRadius = 0.08f;
    [Tooltip("Rifle drop: thrown this fast forward (m/s), on top of the player's own movement.")]
    public float dropThrowSpeed = 1.2f;

    public MagazinePouch Pouch { get; private set; }
    public CarriedRifle Rifle { get; private set; }

    HitscanGun gun;
    CharacterMotor motor;
    PlayerCharacterAnimator body;
    IInteractable target;
    readonly RaycastHit[] hits = new RaycastHit[16];
    GUIStyle promptStyle;

    void Awake()
    {
        Pouch = GetComponent<MagazinePouch>();
        motor = GetComponent<CharacterMotor>();
        gun = GetComponentInChildren<HitscanGun>(true);
        body = GetComponentInChildren<PlayerCharacterAnimator>(true);
        if (body != null) Rifle = CarriedRifle.On(body.carriedGun);
    }

    void Update()
    {
        target = FindTarget();
        var keyboard = Keyboard.current;
        if (keyboard == null || Cursor.lockState != CursorLockMode.Locked) return;

        if (keyboard[interactKey].wasPressedThisFrame && target != null) target.Interact(this);
        if (keyboard[dropKey].wasPressedThisFrame) DropRifle();
    }

    void DropRifle()
    {
        if (Rifle == null || !Rifle.IsHeld || (gun != null && gun.IsReloading)) return;
        Vector3 velocity = (motor != null ? motor.Velocity : Vector3.zero) + transform.forward * dropThrowSpeed;
        Rifle.Drop(velocity, body != null ? body.GetComponentsInChildren<Collider>() : null);
    }

    // The closest usable thing along the view, within reach of the player.
    IInteractable FindTarget()
    {
        var cam = Camera.main;
        if (cam == null) return null;
        Vector3 chest = transform.position + Vector3.up * 1.3f;
        // In third person the camera is behind the player, so allow for that distance too.
        float range = reach + Vector3.Distance(cam.transform.position, chest);
        int count = Physics.SphereCastNonAlloc(cam.transform.position, lookRadius, cam.transform.forward, hits, range,
            CharacterLayers.NotCharacterCapsules, QueryTriggerInteraction.Collide);
        IInteractable best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var h = hits[i];
            if (h.collider.transform.IsChildOf(transform) || h.distance >= bestDistance) continue;
            Vector3 point = h.distance > 0f ? h.point : h.collider.ClosestPoint(cam.transform.position);
            if (Vector3.Distance(point, chest) > reach) continue;
            var item = h.collider.GetComponentInParent<IInteractable>();
            // Something solid in front of the item blocks it, unless it's a character's limb.
            if (item == null && h.collider.gameObject.layer == CharacterLayers.Hitbox) continue;
            bestDistance = h.distance;
            best = item != null && item.CanInteract(this) ? item : null;
        }
        return best;
    }

    void OnGUI()
    {
        if (target == null) return;
        if (promptStyle == null)
            promptStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        GUI.Label(new Rect(0, Screen.height * 0.62f, Screen.width, 40), $"[{interactKey}]  {target.Prompt}", promptStyle);
    }
}
