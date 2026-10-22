using UnityEngine;

// Says what an object is made of, for footsteps and bullet impacts (overrides the name/terrain guess in Surfaces).
public class SurfaceTag : MonoBehaviour
{
    public SurfaceType surface = SurfaceType.Wood;
}
