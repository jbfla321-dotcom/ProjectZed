using UnityEngine;

// One SoundEvent per surface (e.g. footsteps, or bullet impacts). Missing entries fall back to dirt.
[CreateAssetMenu(menuName = "ProjectZed/Audio/Surface Sounds")]
public class SurfaceSounds : ScriptableObject
{
    public SoundEvent dirt, forest, gravel, wood, rock, water, metal, flesh;

    public SoundEvent For(SurfaceType s)
    {
        SoundEvent e = s switch
        {
            SurfaceType.Forest => forest,
            SurfaceType.Gravel => gravel,
            SurfaceType.Wood => wood,
            SurfaceType.Rock => rock,
            SurfaceType.Water => water,
            SurfaceType.Metal => metal,
            SurfaceType.Flesh => flesh,
            _ => dirt,
        };
        return e != null ? e : dirt;
    }
}
