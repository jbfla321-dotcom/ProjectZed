using UnityEngine;

// A hinged door. Lives on the hinge pivot (its y axis is the hinge); the slab is a child reaching along +x.
// The player's interact key (PlayerInteractor) swings it open away from them, or shut again.
public class Door : MonoBehaviour, IInteractable
{
    [Tooltip("How far it swings open (degrees).")]
    public float openAngle = 95f;
    [Tooltip("Swing speed (degrees per second).")]
    public float speed = 220f;
    public SoundEvent openSound;
    public SoundEvent closeSound;
    [Tooltip("Starts open by this many degrees (+ swings toward local -z). 0 = shut.")]
    public float startAngle;

    Quaternion closedRotation;
    float angle, target;

    public bool IsOpen => target != 0f;
    // The angle it's swinging to (0 = shut).
    public float Target => target;

    // Someone here opened or shut a door (multiplayer sends it to the other players: NetSession).
    public static event System.Action<Door> Swung;

    void Awake()
    {
        closedRotation = transform.localRotation;
        angle = target = startAngle;
        Apply();
        enabled = false;
    }

    public bool CanInteract(PlayerInteractor who) => true;
    public string Prompt => IsOpen ? "Close door" : "Open door";

    public void Interact(PlayerInteractor who)
    {
        if (IsOpen)
        {
            target = 0f;
        }
        else
        {
            // A positive angle swings the slab toward local -z: away from someone standing on the +z side.
            float side = Vector3.Dot(who.transform.position - transform.position, transform.forward);
            target = side >= 0f ? openAngle : -openAngle;
            if (openSound != null) SoundManager.Play(openSound, transform.position);
        }
        enabled = true;
        Swung?.Invoke(this);
    }

    // Swings to `angle` (0 = shut) without telling anyone: another player's copy of this door moved.
    public void SwingTo(float angle)
    {
        if (angle == target) return;
        if (target == 0f && openSound != null) SoundManager.Play(openSound, transform.position);
        target = angle;
        enabled = true;
    }

    void Update()
    {
        angle = Mathf.MoveTowards(angle, target, speed * Time.deltaTime);
        Apply();
        if (angle != target) return;
        enabled = false;
        if (target == 0f && closeSound != null) SoundManager.Play(closeSound, transform.position);
    }

    void Apply() => transform.localRotation = closedRotation * Quaternion.Euler(0f, angle, 0f);
}
