using UnityEngine;

// Another player as seen here: a copy of our own player (same body, rifle, ragdoll and sounds) with the input,
// camera, HUD and health taken out, moved by the state their game sends (NetPlayer). Their shots fly here as
// real bullets, so they hit us, glass and targets in our world; hits on this copy do no damage here (their
// own game works out what hits them).
[RequireComponent(typeof(CharacterController))]
public class NetPuppet : CharacterMotor
{
    [Tooltip("How quickly the body catches up with the latest position it was sent.")]
    public float followRate = 12f;
    [Tooltip("Further behind than this (m) and it jumps straight there (respawn, teleport).")]
    public float snapDistance = 6f;
    [Tooltip("Longest it guesses ahead (s) from the last update's velocity.")]
    public float maxExtrapolation = 0.25f;

    public float walkSpeed = 5f;
    public override Vector3 Velocity => current.velocity;
    public override bool IsGrounded => current.Has(NetPlayerState.Grounded);
    public override bool IsCrouching => current.Has(NetPlayerState.Crouching);
    public override float WalkSpeed => walkSpeed;
    public override void SetControlEnabled(bool on) { }

    // Body parts, found by the names on the player.
    public Transform eye, view;
    public PlayerCharacterAnimator body;
    public WeaponStance stance;
    public HitscanGun gun;
    Ragdoll ragdoll;
    CarriedRifle rifle;

    NetPlayerState current;
    float receivedAt;
    bool placed;
    CharacterHealth.State life = CharacterHealth.State.Alive;

    static NetPuppet template;

    // ---------- Making them ----------
    // A stripped copy of the scene's player, kept inactive and copied for each other player.
    static NetPuppet Template
    {
        get
        {
            if (template != null) return template;
            var player = FindAnyObjectByType<FPSController>();
            if (player == null) return null;
            var holder = new GameObject("NetPuppetTemplate");
            holder.SetActive(false); // nothing on the copy wakes up (Awake) until it's stripped
            var copy = Instantiate(player.gameObject, holder.transform);
            copy.name = "OtherPlayer";
            template = Strip(copy, player);
            return template;
        }
    }

    // Call early (while the player is standing with the rifle in hand), so later copies start that way.
    public static void PrepareTemplate() => _ = Template;

    static NetPuppet Strip(GameObject copy, FPSController original)
    {
        var puppet = copy.AddComponent<NetPuppet>();
        puppet.walkSpeed = original.walkSpeed;
        var switcher = original.GetComponent<CameraModeSwitcher>();
        puppet.eye = Find(copy.transform, original.cameraPivot);
        puppet.view = switcher != null && switcher.viewCamera != null ? Find(copy.transform, switcher.viewCamera.transform) : puppet.eye;
        puppet.body = copy.GetComponentInChildren<PlayerCharacterAnimator>(true);
        puppet.stance = copy.GetComponentInChildren<WeaponStance>(true);
        puppet.gun = copy.GetComponentInChildren<HitscanGun>(true);

        // Keep the body (animation, ragdoll, footsteps) and the rifle; everything that reads input, owns a camera,
        // listens, shows a HUD or keeps health goes. Some components need others, so take them off in passes.
        var keep = new System.Collections.Generic.HashSet<Component> { puppet, copy.GetComponent<CharacterController>(), puppet.stance, puppet.gun };
        if (puppet.gun != null) foreach (var c in puppet.gun.GetComponents<GunAudio>()) keep.Add(c);
        for (int pass = 0; pass < 4; pass++)
        {
            foreach (var c in copy.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || keep.Contains(c)) continue;
                if (puppet.body != null && c.transform.IsChildOf(puppet.body.transform)) continue;
                if (c.gameObject == copy && c is not MonoBehaviour) continue; // the capsule
                if (RequiredByAnother(c)) continue; // next pass, once that one's gone
                Object.DestroyImmediate(c);
            }
        }
        if (puppet.body != null)
        {
            puppet.body.controller = puppet;
            puppet.body.firstPersonView = false;
            puppet.body.aimCamera = puppet.eye;
        }
        if (puppet.stance != null)
        {
            puppet.stance.enabled = false;
            puppet.stance.gun = null;
            puppet.stance.controller = null;
            puppet.stance.cameraSwitcher = null;
            puppet.stance.viewCamera = null;
        }
        if (puppet.gun != null)
        {
            puppet.gun.player = null;
            puppet.gun.aimOrigin = puppet.view;
        }
        return puppet;
    }

    static bool RequiredByAnother(Component c)
    {
        foreach (var other in c.GetComponents<Component>())
        {
            if (other == null || other == c) continue;
            foreach (RequireComponent r in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                foreach (var type in new[] { r.m_Type0, r.m_Type1, r.m_Type2 })
                    if (type != null && type.IsInstanceOfType(c)) return true;
        }
        return false;
    }

    // The same object in the copy as `target` is in the original (by path).
    static Transform Find(Transform copyRoot, Transform target)
    {
        if (target == null) return null;
        var path = new System.Collections.Generic.List<int>();
        for (var t = target; t.parent != null && t.GetComponent<FPSController>() == null; t = t.parent) path.Add(t.GetSiblingIndex());
        var found = copyRoot;
        for (int i = path.Count - 1; i >= 0; i--) found = found.GetChild(path[i]);
        return found;
    }

    public static NetPuppet Spawn(string label)
    {
        var t = Template;
        if (t == null) return null;
        var puppet = Instantiate(t);
        puppet.name = label;
        puppet.gameObject.SetActive(false); // until the first update says where they are
        puppet.AddLabel(label);
        return puppet;
    }

    void Awake()
    {
        ragdoll = GetComponentInChildren<Ragdoll>();
        if (gun != null) gun.remote = true;
        if (body != null && body.carriedGun != null) rifle = CarriedRifle.On(body.carriedGun);
    }

    void AddLabel(string text)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 2.15f, 0f);
        var mesh = go.AddComponent<TextMesh>();
        mesh.text = text;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.characterSize = 0.05f;
        mesh.fontSize = 48;
        mesh.color = new Color(0.75f, 0.95f, 1f);
        mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        go.GetComponent<MeshRenderer>().sharedMaterial = mesh.font.material;
        go.AddComponent<FaceCamera>();
    }

    // ---------- Driving them ----------
    public void Receive(NetPlayerState s)
    {
        if (!s.Has(NetPlayerState.Valid)) return;
        current = s;
        receivedAt = Time.time;
        if (!gameObject.activeSelf)
        {
            transform.SetPositionAndRotation(s.position, Quaternion.Euler(0f, s.yaw, 0f));
            gameObject.SetActive(true);
            placed = true;
        }
    }

    public void Fire(Vector3 origin, Vector3 velocity) { if (isActiveAndEnabled && gun != null) gun.RemoteFire(origin, velocity); }
    public void Reload() { if (isActiveAndEnabled && gun != null) gun.RemoteReload(); }
    public void Jump() => OnJumped();

    void Update()
    {
        if (!placed) return;
        UpdateLife((CharacterHealth.State)current.life);
        bool down = ragdoll != null && (ragdoll.IsLimp || ragdoll.IsRecovering);
        if (!down) Follow();

        if (stance != null) stance.SetBlends(current.raise, current.aim);
        bool armed = current.Has(NetPlayerState.Armed);
        if (rifle != null && !down)
        {
            if (!armed && rifle.IsHeld) rifle.Drop(Vector3.zero);
            else if (armed && !rifle.IsHeld) rifle.Attach();
        }
    }

    void Follow()
    {
        float ahead = Mathf.Min(Time.time - receivedAt, maxExtrapolation);
        Vector3 target = current.position + new Vector3(current.velocity.x, IsGrounded ? 0f : current.velocity.y, current.velocity.z) * ahead;
        float k = 1f - Mathf.Exp(-followRate * Time.deltaTime);
        Vector3 pos = (target - transform.position).sqrMagnitude > snapDistance * snapDistance ? target : Vector3.Lerp(transform.position, target, k);
        Quaternion rot = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, current.yaw, 0f), k);
        transform.SetPositionAndRotation(pos, rot);
        if (eye != null) eye.SetLocalPositionAndRotation(current.eyePosition, current.eyeRotation);
        if (view != null && view != eye)
            view.SetPositionAndRotation(transform.TransformPoint(current.viewPosition), transform.rotation * current.viewRotation);
    }

    // Knocked down, killed, back up, respawned.
    void UpdateLife(CharacterHealth.State now)
    {
        if (now == life || ragdoll == null) return;
        var before = life;
        life = now;
        if (before == CharacterHealth.State.Alive) ragdoll.GoLimp(current.velocity);
        else if (now == CharacterHealth.State.Alive)
        {
            if (before == CharacterHealth.State.KnockedDown) ragdoll.Recover();
            else
            {
                ragdoll.ResetToAnimated();
                transform.SetPositionAndRotation(current.position, Quaternion.Euler(0f, current.yaw, 0f));
            }
        }
    }
}
