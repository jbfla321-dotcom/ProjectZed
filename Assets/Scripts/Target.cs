using UnityEngine;

// Shootable target: flashes when hit, disappears at zero health, respawns after a delay.
public class Target : MonoBehaviour
{
    public float maxHealth = 100f;
    public float respawnDelay = 3f;
    public Color hitColor = Color.white;
    public float hitFlashTime = 0.08f;

    float health;
    float flashEndTime;
    float respawnTime = -1f;
    Renderer[] renderers;
    Collider[] colliders;
    MaterialPropertyBlock block;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    void Awake()
    {
        health = maxHealth;
        renderers = GetComponentsInChildren<Renderer>();
        colliders = GetComponentsInChildren<Collider>();
        block = new MaterialPropertyBlock();
    }

    public void TakeDamage(float amount, Vector3 point, Vector3 direction)
    {
        if (respawnTime > 0f) return;

        health -= amount;
        flashEndTime = Time.time + hitFlashTime;
        SetFlash(true);

        if (health <= 0f)
        {
            SetVisible(false);
            respawnTime = Time.time + respawnDelay;
        }
    }

    void Update()
    {
        if (flashEndTime > 0f && Time.time >= flashEndTime)
        {
            flashEndTime = 0f;
            SetFlash(false);
        }

        if (respawnTime > 0f && Time.time >= respawnTime)
        {
            respawnTime = -1f;
            health = maxHealth;
            SetVisible(true);
        }
    }

    void SetFlash(bool on)
    {
        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (on)
            {
                block.SetColor(BaseColorId, hitColor);
                r.SetPropertyBlock(block);
            }
            else
            {
                r.SetPropertyBlock(null);
            }
        }
    }

    void SetVisible(bool visible)
    {
        foreach (var r in renderers) if (r != null) r.enabled = visible;
        foreach (var c in colliders) if (c != null) c.enabled = visible;
    }
}
