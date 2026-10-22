using UnityEngine;

// The physics layers characters use: "Character" for movement capsules, "Hitbox" for the ragdoll limbs
// (which follow the animation while alive and are what bullets hit). Cameras and movement checks that
// mean "the world" should use Environment so they never collide with a character's own body.
public static class CharacterLayers
{
    static int character = -1, hitbox = -1;

    // Looked up until the layers exist, then cached.
    public static int Character => character >= 0 ? character : character = LayerMask.NameToLayer("Character");
    public static int Hitbox => hitbox >= 0 ? hitbox : hitbox = LayerMask.NameToLayer("Hitbox");

    public static int Environment => ~(Bit(Character) | Bit(Hitbox));
    public static int NotCharacterCapsules => ~Bit(Character);

    static int Bit(int layer) => layer >= 0 ? 1 << layer : 0;
}
