using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// What one player sends about themselves, many times a second.
public struct NetPlayerState : INetworkSerializeByMemcpy
{
    public Vector3 position;
    public float yaw;
    public Vector3 velocity;
    // The eye pivot and the view camera, relative to the player (where they look and aim from).
    public Vector3 eyePosition, viewPosition;
    public Quaternion eyeRotation, viewRotation;
    public float raise, aim;
    public byte flags;
    public byte life; // CharacterHealth.State

    public const byte Valid = 1, Grounded = 2, Crouching = 4, Armed = 8;
    public bool Has(byte flag) => (flags & flag) != 0;
}

// One per connected player (NetSession's player prefab). The owner's copy reads their real player (the scene's
// FPSController) and sends its state, shots, reloads, jumps and door swings; every other copy shows them as a
// NetPuppet. The host's copy also sends the time of day and weather.
public class NetPlayer : NetworkBehaviour
{
    [Tooltip("Seconds between time-of-day / weather updates from the host.")]
    public float worldSyncInterval = 2f;

    public static NetPlayer Local { get; private set; }
    public static readonly List<NetPlayer> All = new List<NetPlayer>();

    readonly NetworkVariable<NetPlayerState> state = new NetworkVariable<NetPlayerState>(default,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetPlayerState State => state.Value;

    // Owner side: the real player.
    FPSController player;
    Transform eye, view;
    WeaponStance stance;
    PlayerCharacterAnimator body;
    CharacterHealth health;
    HitscanGun gun;
    CharacterController cc;
    bool placedNearHost;
    float nextWorldSync;

    // Everyone else: their copy of this player.
    NetPuppet puppet;

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        if (IsOwner)
        {
            Local = this;
            player = FindAnyObjectByType<FPSController>();
            if (player != null)
            {
                eye = player.cameraPivot;
                var switcher = player.GetComponent<CameraModeSwitcher>();
                view = switcher != null && switcher.viewCamera != null ? switcher.viewCamera.transform : eye;
                stance = player.GetComponentInChildren<WeaponStance>(true);
                body = player.GetComponentInChildren<PlayerCharacterAnimator>(true);
                health = player.GetComponent<CharacterHealth>();
                cc = player.GetComponent<CharacterController>();
                gun = player.GetComponentInChildren<HitscanGun>(true);
                if (gun != null)
                {
                    gun.Shot += OnShot;
                    gun.ReloadStarted += OnReloadStarted;
                }
                player.Jumped += OnJumped;
            }
            placedNearHost = IsServer;
            Debug.Log($"NetPlayer: we are client {OwnerClientId}");
        }
        else
        {
            puppet = NetPuppet.Spawn(OwnerClientId == NetworkManager.ServerClientId ? "Host" : "Player " + (OwnerClientId + 1));
            if (puppet != null) state.OnValueChanged += OnStateChanged;
            Debug.Log($"NetPlayer: player {OwnerClientId} joined, showing them as {(puppet != null ? puppet.name : "nothing")}");
            // A new player gets the doors as the host has them.
            if (IsServer && NetSession.Instance != null)
                DoorStatesRpc(NetSession.Instance.DoorTargets(), RpcTarget.Single(OwnerClientId, RpcTargetUse.Temp));
        }
        if (IsServer) nextWorldSync = 0f;
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        if (Local == this) Local = null;
        if (gun != null)
        {
            gun.Shot -= OnShot;
            gun.ReloadStarted -= OnReloadStarted;
        }
        if (player != null) player.Jumped -= OnJumped;
        state.OnValueChanged -= OnStateChanged;
        if (puppet != null) Destroy(puppet.gameObject);
        Debug.Log($"NetPlayer: player {OwnerClientId} left");
    }

    void OnStateChanged(NetPlayerState previous, NetPlayerState current) => puppet.Receive(current);

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner && player != null)
        {
            state.Value = Capture();
            if (!placedNearHost) PlaceNearHost();
        }
        if (IsServer && IsOwner && Time.time >= nextWorldSync)
        {
            nextWorldSync = Time.time + worldSyncInterval;
            SendWorld();
        }
    }

    NetPlayerState Capture()
    {
        Transform root = player.transform;
        Quaternion toLocal = Quaternion.Inverse(root.rotation);
        var s = new NetPlayerState
        {
            position = root.position,
            yaw = root.eulerAngles.y,
            velocity = player.Velocity,
            eyePosition = root.InverseTransformPoint(eye.position),
            eyeRotation = toLocal * eye.rotation,
            viewPosition = root.InverseTransformPoint(view.position),
            viewRotation = toLocal * view.rotation,
            raise = stance != null ? stance.RaiseBlend : 0f,
            aim = stance != null ? stance.AimBlend : 0f,
            life = (byte)(health != null ? health.Current : CharacterHealth.State.Alive),
            flags = NetPlayerState.Valid,
        };
        // The capsule's own flag too: at a low frame rate IsGrounded's short grace period can lapse between frames.
        if (player.IsGrounded || (cc != null && cc.isGrounded)) s.flags |= NetPlayerState.Grounded;
        if (player.IsCrouching) s.flags |= NetPlayerState.Crouching;
        if (body == null || body.Armed) s.flags |= NetPlayerState.Armed;
        return s;
    }

    // Someone joining starts next to the host instead of at the scene's spawn point.
    void PlaceNearHost()
    {
        foreach (var p in All)
        {
            if (p.OwnerClientId != NetworkManager.ServerClientId || !p.State.Has(NetPlayerState.Valid)) continue;
            var host = p.State;
            Quaternion facing = Quaternion.Euler(0f, host.yaw, 0f);
            Vector3 spot = host.position + facing * new Vector3(1.5f, 0f, 0.5f);
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            player.transform.SetPositionAndRotation(spot + Vector3.up * 0.1f, facing);
            if (cc != null) cc.enabled = true;
            placedNearHost = true;
            Debug.Log($"NetPlayer: placed next to the host at {spot}");
            return;
        }
    }

    // ---------- Events from the owner ----------
    void OnShot(Vector3 origin, Vector3 velocity) => FireRpc(origin, velocity);
    void OnReloadStarted() => ReloadRpc();
    void OnJumped() => JumpRpc();

    [Rpc(SendTo.NotOwner, Delivery = RpcDelivery.Reliable)]
    void FireRpc(Vector3 origin, Vector3 velocity)
    {
        if (puppet != null) puppet.Fire(origin, velocity);
    }

    [Rpc(SendTo.NotOwner)]
    void ReloadRpc()
    {
        if (puppet != null) puppet.Reload();
    }

    [Rpc(SendTo.NotOwner)]
    void JumpRpc()
    {
        if (puppet != null) puppet.Jump();
    }

    // ---------- Doors ----------
    public void SendDoor(int index, float target) => DoorRpc(index, target);

    [Rpc(SendTo.NotMe)]
    void DoorRpc(int index, float target)
    {
        if (NetSession.Instance != null) NetSession.Instance.ApplyDoor(index, target);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void DoorStatesRpc(float[] targets, RpcParams rpcParams = default)
    {
        if (NetSession.Instance != null) NetSession.Instance.ApplyDoorTargets(targets);
    }

    // ---------- Time of day and weather (host) ----------
    void SendWorld()
    {
        var clock = TimeOfDay.Instance;
        var weather = WeatherManager.Instance;
        var w = weather != null ? weather.current : default;
        WorldRpc(clock != null ? clock.time : -1f, weather != null, w.overcast, w.fog, w.rain, w.storm, w.wind);
    }

    [Rpc(SendTo.NotServer)]
    void WorldRpc(float hour, bool hasWeather, float overcast, float fog, float rain, float storm, float wind)
    {
        var clock = TimeOfDay.Instance;
        // The clock keeps running here between updates; it's only corrected when it drifts.
        if (clock != null && hour >= 0f && Mathf.Abs(Mathf.DeltaAngle(clock.time * 15f, hour * 15f)) > 0.1f) clock.SetTime(hour);
        var weather = WeatherManager.Instance;
        if (weather != null && hasWeather)
        {
            weather.autoWeather = false; // the host's weather changes
            // Blend over the gap to the next update, so the host's slow changes stay smooth here.
            weather.Set(new WeatherManager.WeatherState(overcast, fog, rain, storm, wind), worldSyncInterval * 1.1f);
        }
    }
}
