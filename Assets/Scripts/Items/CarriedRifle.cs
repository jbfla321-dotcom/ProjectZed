using UnityEngine;

// The rifle a character carries (the body's rifle model, placed each frame by PlayerCharacterAnimator).
// It's one object either way: in the hands, or dropped as a physics object lying in the world, still
// holding its magazine and the rounds in it. Dropped on purpose (the player's drop key) it waits to be
// picked up; dropped by a knockdown, the character's ragdoll puts it back in the hands on getting up.
public class CarriedRifle : MonoBehaviour, IInteractable
{
    [Tooltip("Rounds in the inserted magazine.")]
    public int rounds = 30;
    public int magazineSize = 30;
    public float mass = 3.5f;

    public bool IsHeld => transform.parent == holdParent;
    // The magazine model in the rifle (copied when a magazine is dropped during a reload).
    public Transform Magazine { get; private set; }

    Transform holdParent;
    Vector3 holdPosition;
    Quaternion holdRotation;
    Transform[] parts;
    int[] partLayers;

    public static CarriedRifle On(Transform rifle)
    {
        if (rifle == null) return null;
        var item = rifle.GetComponent<CarriedRifle>();
        return item != null ? item : rifle.gameObject.AddComponent<CarriedRifle>();
    }

    void Awake()
    {
        holdParent = transform.parent;
        holdPosition = transform.localPosition;
        holdRotation = transform.localRotation;
        parts = GetComponentsInChildren<Transform>(true);
        partLayers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            partLayers[i] = parts[i].gameObject.layer;
            if (Magazine == null && parts[i].name == "Magazine") Magazine = parts[i];
        }
    }

    // Lets go of the rifle where it is, moving at `velocity`. `ignore`: colliders it shouldn't hit (its owner's limbs).
    public void Drop(Vector3 velocity, Collider[] ignore = null)
    {
        if (!IsHeld) return;
        transform.SetParent(null, true);
        WorldItems.SetLayer(gameObject, WorldItems.Layer);
        WorldItems.MakePhysical(gameObject, mass, velocity);
        if (ignore != null)
        {
            var col = GetComponent<Collider>();
            foreach (var c in ignore) if (c != null) Physics.IgnoreCollision(col, c);
        }
    }

    // Back into the hands.
    public void Attach()
    {
        if (IsHeld) return;
        WorldItems.MakeStatic(gameObject);
        transform.SetParent(holdParent, false);
        transform.localPosition = holdPosition;
        transform.localRotation = holdRotation;
        for (int i = 0; i < parts.Length; i++) parts[i].gameObject.layer = partLayers[i];
    }

    // Only the player who owns it can pick it up (NPC rifles go back to their owners).
    public bool CanInteract(PlayerInteractor who) => !IsHeld && who.Rifle == this;
    public string Prompt => $"Pick up rifle ({rounds}/{magazineSize})";
    public void Interact(PlayerInteractor who) => Attach();
}
