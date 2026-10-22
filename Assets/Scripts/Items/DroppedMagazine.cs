using UnityEngine;

// A magazine lying in the world (dropped during a reload). Picking it up puts it in the pouch with whatever
// rounds it still has.
public class DroppedMagazine : MonoBehaviour, IInteractable
{
    public int rounds;
    public int capacity = 30;

    public bool CanInteract(PlayerInteractor who) => who.Pouch != null;
    public string Prompt => $"Pick up magazine ({rounds}/{capacity})";

    public void Interact(PlayerInteractor who)
    {
        who.Pouch.Add(rounds);
        Destroy(gameObject);
    }

    // A loose copy of the rifle's magazine `model`, at its current pose, falling with `velocity`.
    public static DroppedMagazine Spawn(Transform model, int rounds, int capacity, Vector3 velocity)
    {
        var go = Instantiate(model.gameObject, model.position, model.rotation);
        go.name = "Magazine (dropped)";
        go.transform.localScale = model.lossyScale;
        // The model may be hidden mid-reload; the copy is always visible. An empty one shows no top round.
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            r.enabled = rounds > 0 || !r.name.StartsWith("Top_Round");
        WorldItems.SetLayer(go, WorldItems.Layer);
        WorldItems.MakePhysical(go, 0.45f, velocity);
        var mag = go.AddComponent<DroppedMagazine>();
        mag.rounds = rounds;
        mag.capacity = capacity;
        return mag;
    }
}
