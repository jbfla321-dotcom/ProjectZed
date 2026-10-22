using System.Collections.Generic;
using UnityEngine;

public enum SurfaceType { Dirt, Forest, Gravel, Wood, Rock, Water, Metal, Flesh, Glass }

// What a ray hit is made of, for footsteps and bullet impacts. In order: a SurfaceTag on the object,
// a character (flesh), the terrain's dominant layer under the point (or Wood for a terrain tree's trunk),
// then keywords in the physics material's or the render material's name. Dirt if nothing matches.
public static class Surfaces
{
    static readonly Dictionary<Object, SurfaceType?> nameCache = new Dictionary<Object, SurfaceType?>();

    public static SurfaceType Resolve(RaycastHit hit)
    {
        var col = hit.collider;
        if (col == null) return SurfaceType.Dirt;
        var tag = col.GetComponentInParent<SurfaceTag>();
        if (tag != null) return tag.surface;
        if (col.gameObject.layer == CharacterLayers.Hitbox || col.GetComponentInParent<CharacterHealth>() != null) return SurfaceType.Flesh;
        if (col is TerrainCollider tc) return FromTerrain(tc, hit.point);
        if (col.sharedMaterial != null && FromName(col.sharedMaterial) is SurfaceType p) return p;
        var r = col.GetComponent<Renderer>();
        if (r == null) r = col.GetComponentInParent<Renderer>();
        if (r != null && r.sharedMaterial != null && FromName(r.sharedMaterial) is SurfaceType m) return m;
        return FromName(col.gameObject) ?? SurfaceType.Dirt;
    }

    static SurfaceType FromTerrain(TerrainCollider tc, Vector3 point)
    {
        var terrain = tc.GetComponent<Terrain>();
        var data = terrain != null ? terrain.terrainData : tc.terrainData;
        if (data == null) return SurfaceType.Dirt;
        Vector3 origin = tc.transform.position;
        // Terrain trees share the terrain collider: a hit well above the ground is a trunk.
        if (terrain != null && point.y > terrain.SampleHeight(point) + origin.y + 0.4f) return SurfaceType.Wood;

        var layers = data.terrainLayers;
        if (layers == null || layers.Length == 0) return SurfaceType.Dirt;
        int x = Mathf.Clamp((int)((point.x - origin.x) / data.size.x * data.alphamapWidth), 0, data.alphamapWidth - 1);
        int z = Mathf.Clamp((int)((point.z - origin.z) / data.size.z * data.alphamapHeight), 0, data.alphamapHeight - 1);
        var w = data.GetAlphamaps(x, z, 1, 1);
        int best = 0;
        for (int i = 1; i < w.GetLength(2); i++) if (w[0, 0, i] > w[0, 0, best]) best = i;
        return best < layers.Length && layers[best] != null ? FromName(layers[best]) ?? SurfaceType.Dirt : SurfaceType.Dirt;
    }

    // Keyword match on an asset's name, cached per asset. Rock is checked before forest so "MossyRock" is rock.
    public static SurfaceType? FromName(Object o)
    {
        if (o == null) return null;
        if (nameCache.TryGetValue(o, out var cached)) return cached;
        string n = o.name.ToLowerInvariant();
        SurfaceType? s =
            Has(n, "glass", "window") ? SurfaceType.Glass :
            Has(n, "water", "puddle", "river", "stream") ? SurfaceType.Water :
            Has(n, "metal", "steel", "iron", "tin", "car", "vehicle", "chrome") ? SurfaceType.Metal :
            Has(n, "wood", "plank", "log", "bark", "crate", "timber", "trunk", "stump", "snag") ? SurfaceType.Wood :
            Has(n, "gravel", "road", "pebble") ? SurfaceType.Gravel :
            Has(n, "rock", "stone", "cliff", "boulder", "concrete", "asphalt", "brick") ? SurfaceType.Rock :
            Has(n, "moss", "needle", "litter", "duff", "leaf", "leaves", "fern", "meadow", "grass", "thatch", "forest") ? SurfaceType.Forest :
            Has(n, "dirt", "mud", "soil", "ground", "sand", "earth") ? SurfaceType.Dirt :
            (SurfaceType?)null;
        nameCache[o] = s;
        return s;
    }

    static bool Has(string s, params string[] words)
    {
        foreach (var w in words) if (s.Contains(w)) return true;
        return false;
    }
}
