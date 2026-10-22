using System.Collections.Generic;
using UnityEngine;

// A house placed by HouseBuilder. Knows where its rooms are (house-local boxes), so anything can ask whether a
// point is indoors: SoundManager switches to the Indoor snapshot while the listener is inside one.
[ExecuteAlways]
public class House : MonoBehaviour
{
    public string type;
    [Tooltip("Rooms, in this transform's local space (floor to ceiling).")]
    public Bounds[] rooms = new Bounds[0];
    [Tooltip("Radius around this transform that holds every room (quick reject).")]
    public float radius = 10f;

    static readonly List<House> all = new List<House>();
    public static IReadOnlyList<House> All => all;

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    public bool Contains(Vector3 world)
    {
        Vector3 p = transform.InverseTransformPoint(world);
        foreach (var b in rooms)
            if (b.Contains(p)) return true;
        return false;
    }

    // The house whose rooms hold this point, or null if it's outdoors.
    public static House At(Vector3 world)
    {
        foreach (var h in all)
        {
            if ((h.transform.position - world).sqrMagnitude > h.radius * h.radius) continue;
            if (h.Contains(world)) return h;
        }
        return null;
    }
}
