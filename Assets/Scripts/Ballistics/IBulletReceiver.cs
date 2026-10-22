using UnityEngine;

// Something that reacts to being shot on its own (e.g. a window pane shattering). HitscanGun calls it for whatever
// a bullet hits, before placing the impact mark.
public interface IBulletReceiver
{
    // power: the bullet's remaining speed as a fraction of its muzzle speed.
    void OnBulletHit(Vector3 point, Vector3 direction, float power);
}
