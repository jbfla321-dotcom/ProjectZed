using UnityEngine;

// Something the player can use by looking at it and pressing the interact key (see PlayerInteractor).
public interface IInteractable
{
    // Whether `who` can use this right now (e.g. a rifle can only be picked up while it's on the ground).
    bool CanInteract(PlayerInteractor who);
    // Shown on screen while looking at it, e.g. "Pick up magazine (30)".
    string Prompt { get; }
    void Interact(PlayerInteractor who);
}

// Loose things lying in the world (dropped rifles, magazines). They live on the "Item" layer, which characters'
// movement capsules and limb hitboxes don't collide with, so walking over them doesn't trip anyone up or
// kick them around; bullets and everything else still hit them.
public static class WorldItems
{
    static int layer = -1;
    static bool collisionsSet;

    public static int Layer
    {
        get
        {
            if (layer < 0)
            {
                int l = LayerMask.NameToLayer("Item");
                if (l < 0) return 0; // layer missing: Default
                layer = l;
            }
            if (!collisionsSet)
            {
                collisionsSet = true;
                if (CharacterLayers.Character >= 0) Physics.IgnoreLayerCollision(layer, CharacterLayers.Character, true);
                if (CharacterLayers.Hitbox >= 0) Physics.IgnoreLayerCollision(layer, CharacterLayers.Hitbox, true);
            }
            return layer;
        }
    }

    public static void SetLayer(GameObject go, int layer)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    // Makes `go` a loose physics object: one box around all its visible parts and a rigidbody.
    public static Rigidbody MakePhysical(GameObject go, float mass, Vector3 velocity)
    {
        var col = go.AddComponent<BoxCollider>();
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            // Bounds in the object's own space, so the box fits snugly whatever its orientation.
            var t = go.transform;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            foreach (var r in renderers)
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                var b = r.localBounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = t.InverseTransformPoint(r.transform.TransformPoint(corner));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            if (min.x <= max.x)
            {
                col.center = (min + max) / 2f;
                col.size = max - min;
            }
        }
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // small and fast: don't tunnel into the ground
        rb.linearVelocity = velocity;
        return rb;
    }

    public static void MakeStatic(GameObject go)
    {
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null) Object.Destroy(rb);
        var col = go.GetComponent<BoxCollider>();
        if (col != null) Object.Destroy(col);
    }
}
