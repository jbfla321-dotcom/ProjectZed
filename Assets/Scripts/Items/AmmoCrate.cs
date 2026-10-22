using UnityEngine;

// A crate of loaded magazines: look at it and press the interact key to take one. Its label floats upright
// above it (whichever way the crate has tumbled) and shows how many are left.
public class AmmoCrate : MonoBehaviour, IInteractable
{
    public int magazines = 99;
    public int roundsPerMagazine = 30;
    public string contents = "5.56 MAGAZINES";
    [Tooltip("Text above the crate (a TextMesh, not parented to the crate so it stays upright).")]
    public TextMesh label;
    public float labelHeight = 0.6f;

    public bool CanInteract(PlayerInteractor who) => who.Pouch != null && magazines > 0;
    public string Prompt => magazines > 0 ? $"Take magazine ({magazines} left)" : "Empty";

    public void Interact(PlayerInteractor who)
    {
        if (magazines <= 0) return;
        magazines--;
        who.Pouch.Add(roundsPerMagazine);
        UpdateLabel();
    }

    void Start() => UpdateLabel();

    void LateUpdate()
    {
        if (label != null) label.transform.position = transform.position + Vector3.up * labelHeight;
    }

    void OnValidate() => UpdateLabel();

    void UpdateLabel()
    {
        if (label != null) label.text = $"AMMO\n{magazines} × {contents}";
    }
}
