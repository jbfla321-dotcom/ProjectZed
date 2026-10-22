using UnityEngine;

// What a character body (PlayerCharacterAnimator) needs from whatever moves it: the player's FPSController
// or an NPC's brain. Lets NPCs reuse the player's exact animation setup.
public abstract class CharacterMotor : MonoBehaviour
{
    public abstract Vector3 Velocity { get; }
    public abstract bool IsGrounded { get; }
    public virtual bool IsCrouching => false;
    // Normal (non-sprint) movement speed, used to scale lean.
    public abstract float WalkSpeed { get; }

    public event System.Action Jumped;
    protected void OnJumped() => Jumped?.Invoke();

    // Touched down after a real fall, with the downward impact speed (m/s).
    public event System.Action<float> Landed;
    protected void OnLanded(float impactSpeed) => Landed?.Invoke(impactSpeed);

    // Pauses or resumes this character's own control (input, movement, weapon, camera) while it's
    // driving, knocked down or dead. Overridden by characters that have more than the motor to pause.
    public virtual void SetControlEnabled(bool on)
    {
        enabled = on;
        var cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = on;
    }
}
