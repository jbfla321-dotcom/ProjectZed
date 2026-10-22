using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

// Tools > Audio: builds the game's sound setup from the clips in Assets/Audio (cut by Audio/Scripts/slice.py).
// Every step creates what's missing and refreshes what exists, so it's safe to re-run after adding clips.
//   Apply Import Settings - AudioImportRules on every clip
//   Build Mixer           - Assets/Audio/Mixer/Main.mixer: groups, master compressor, ducking, snapshots, exposed volumes
//   Build Sound Events    - SoundEvent/SurfaceSounds assets in Assets/Audio/Events from the numbered clips
//   Add Footstep Events   - FootL/FootR/Land animation events on the player controller's locomotion clips
//   Wire Scene            - SoundManager, GunAudio and FootstepAudio in the open scene (backs the scene up first)
// Headless: -executeMethod AudioTool.BuildAllFromCommandLine
public static class AudioTool
{
    const string Root = "Assets/Audio";
    const string EventsDir = Root + "/Events";
    const string MixerPath = Root + "/Mixer/Main.mixer";
    static readonly string[] Folders = { "Weapons", "Footsteps", "Ambience", "Vehicles", "Characters", "UI", "Mixer", "Events" };
    static readonly string[] GroupNames = { "Weapons", "Foley", "Vehicles", "Characters", "Ambience", "UI" };

    [MenuItem("Tools/Audio/Build All")]
    public static void BuildAll()
    {
        if (RefuseInPlayMode()) return;
        AssetDatabase.Refresh();
        EnsureFolders();
        ApplyImportSettings();
        ApplyProjectAudioSettings();
        BuildMixer();
        BuildSoundEvents();
        AddFootstepEvents();
        WireScene();
        Debug.Log("[AudioTool] Build All done.");
    }

    public static void BuildAllFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        BuildAll();
    }

    static bool RefuseInPlayMode()
    {
        if (!EditorApplication.isPlaying) return false;
        Debug.LogWarning("[AudioTool] Stop Play mode first: changes made now would be lost.");
        return true;
    }

    static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets", "Audio");
        foreach (var f in Folders)
            if (!AssetDatabase.IsValidFolder(Root + "/" + f)) AssetDatabase.CreateFolder(Root, f);
    }

    // ---------- Project audio settings ----------
    // Best-latency DSP buffer (256 samples instead of 1024), so a shot is heard with the muzzle flash rather than
    // a few frames later; and enough real voices for the pool plus everyone else's AudioSources.
    [MenuItem("Tools/Audio/Apply Project Audio Settings")]
    public static void ApplyProjectAudioSettings()
    {
        var manager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset").FirstOrDefault();
        if (manager == null) return;
        var so = new SerializedObject(manager);
        so.FindProperty("m_RequestedDSPBufferSize").intValue = 256;
        so.FindProperty("m_RealVoiceCount").intValue = 64;
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log("[AudioTool] Project audio: DSP buffer 256 (best latency), 64 real voices.");
    }

    // ---------- Import settings ----------
    [MenuItem("Tools/Audio/Apply Import Settings")]
    public static void ApplyImportSettings()
    {
        int changed = 0;
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is AudioImporter imp && AudioImportRules.Apply(imp, path))
                {
                    imp.SaveAndReimport();
                    changed++;
                }
            }
        }
        finally { AssetDatabase.StopAssetEditing(); }
        Debug.Log($"[AudioTool] Import settings: {changed} clip(s) updated.");
    }

    // ---------- Mixer ----------
    // Unity has no public API for authoring mixers; this drives the editor's own AudioMixerController (the
    // same calls the Audio Mixer window makes) through reflection.
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type EdType(string name) => typeof(Editor).Assembly.GetType("UnityEditor.Audio." + name, true);

    static object Call(object target, string method, params object[] args)
    {
        var type = target as Type ?? target.GetType();
        var m = type.GetMethods(BF).First(x => x.Name == method && x.GetParameters().Length == args.Length);
        return m.Invoke(target is Type ? null : target, args);
    }
    static object Get(object target, string prop) => target.GetType().GetProperty(prop, BF).GetValue(target);
    static void Set(object target, string prop, object value) => target.GetType().GetProperty(prop, BF).SetValue(target, value);

    // Snapshot settings: group volume (dB) and low-pass cutoff (Hz) per snapshot; groups not listed stay at 0 dB / open.
    static readonly Dictionary<string, Dictionary<string, (float db, float cutoff)>> Snapshots = new Dictionary<string, Dictionary<string, (float, float)>>
    {
        ["Outdoor"] = new Dictionary<string, (float, float)>(),
        ["InVehicle"] = new Dictionary<string, (float, float)>
        {
            ["Weapons"] = (-4f, 4500f), ["Foley"] = (-12f, 2000f), ["Characters"] = (-6f, 3500f), ["Ambience"] = (-10f, 2500f),
        },
        ["Indoor"] = new Dictionary<string, (float, float)>
        {
            ["Ambience"] = (-12f, 6000f),
        },
        ["Paused"] = new Dictionary<string, (float, float)>
        {
            ["Weapons"] = (-80f, 22000f), ["Foley"] = (-80f, 22000f), ["Vehicles"] = (-80f, 22000f),
            ["Characters"] = (-80f, 22000f), ["Ambience"] = (-20f, 1500f),
        },
    };
    // Groups that get a low-pass (for the InVehicle/Indoor muffling).
    static readonly string[] LowPassGroups = { "Weapons", "Foley", "Characters", "Ambience" };

    [MenuItem("Tools/Audio/Build Mixer")]
    public static void BuildMixer()
    {
        EnsureFolders();
        object ctrl = AssetDatabase.LoadMainAssetAtPath(MixerPath);
        if (ctrl == null) ctrl = Call(EdType("AudioMixerController"), "CreateMixerControllerAtPath", MixerPath);

        object master = Get(ctrl, "masterGroup");
        var groups = new Dictionary<string, object>();
        foreach (var name in GroupNames)
        {
            object g = ((Array)Get(master, "children")).Cast<object>().FirstOrDefault(c => ((Object)c).name == name);
            if (g == null)
            {
                g = Call(ctrl, "CreateNewGroup", name, false);
                Call(ctrl, "AddChildToParent", g, master);
            }
            groups[name] = g;
        }
        ShowInView(ctrl, new[] { master }.Concat(groups.Values));

        // Master: a fast compressor near 0 dBFS acts as the limiter.
        object limiter = Effect(ctrl, master, "Compressor");
        foreach (var name in LowPassGroups) Effect(ctrl, groups[name], "Lowpass Simple");
        // Gunfire ducks the ambience: a send from Weapons into a Duck Volume on Ambience.
        object duck = Effect(ctrl, groups["Ambience"], "Duck Volume");
        object send = Effect(ctrl, groups["Weapons"], "Send");
        Set(send, "sendTarget", duck);

        // Snapshots: the default one becomes Outdoor; the rest are cloned from it.
        var snaps = ((Array)Get(ctrl, "snapshots")).Cast<object>().ToList();
        object outdoor = snaps.FirstOrDefault(s => ((Object)s).name == "Outdoor") ?? snaps[0];
        ((Object)outdoor).name = "Outdoor";
        foreach (var name in Snapshots.Keys)
        {
            if (snaps.Any(s => ((Object)s).name == name)) continue;
            Set(ctrl, "TargetSnapshot", outdoor);
            Call(ctrl, "CloneNewSnapshotFromTarget", false);
            object clone = Get(ctrl, "TargetSnapshot");
            ((Object)clone).name = name;
            snaps.Add(clone);
        }
        Set(ctrl, "startSnapshot", (AudioMixerSnapshot)outdoor);
        Set(ctrl, "TargetSnapshot", outdoor);

        foreach (var snap in snaps)
        {
            if (!Snapshots.TryGetValue(((Object)snap).name, out var settings)) continue;
            Call(master, "SetValueForVolume", ctrl, snap, 0f);
            foreach (var name in GroupNames)
            {
                var (db, cutoff) = settings.TryGetValue(name, out var v) ? v : (0f, 22000f);
                Call(groups[name], "SetValueForVolume", ctrl, snap, db);
                if (LowPassGroups.Contains(name))
                    Call(FindEffect(groups[name], "Lowpass Simple"), "SetValueForParameter", ctrl, snap, "Cutoff freq", cutoff);
            }
            Call(limiter, "SetValueForParameter", ctrl, snap, "Threshold", -3f);
            Call(limiter, "SetValueForParameter", ctrl, snap, "Attack", 5f);
            Call(limiter, "SetValueForParameter", ctrl, snap, "Release", 120f);
            Call(duck, "SetValueForParameter", ctrl, snap, "Threshold", -30f);
            Call(duck, "SetValueForParameter", ctrl, snap, "Ratio", 4f);
            Call(duck, "SetValueForParameter", ctrl, snap, "Attack Time", 0.02f);
            Call(duck, "SetValueForParameter", ctrl, snap, "Release Time", 0.8f);
            Call(send, "SetValueForMixLevel", ctrl, snap, 0f);
        }

        // Exposed volumes, named e.g. "WeaponsVolume" (AudioMixer.SetFloat("WeaponsVolume", dB)).
        Expose(ctrl, master, "MasterVolume");
        foreach (var name in GroupNames) Expose(ctrl, groups[name], name + "Volume");

        // Snapshot blends must run while the game is paused (timeScale 0).
        ((AudioMixer)ctrl).updateMode = AudioMixerUpdateMode.UnscaledTime;

        EditorUtility.SetDirty((Object)ctrl);
        AssetDatabase.SaveAssets();
        Debug.Log("[AudioTool] Mixer built: " + MixerPath);
    }

    // The Audio Mixer window lists the groups in its current view; a mixer made from script starts with none.
    static void ShowInView(object ctrl, IEnumerable<object> groups)
    {
        var viewType = EdType("MixerGroupView");
        var views = (Array)Get(ctrl, "views");
        object view = views.Length > 0 ? views.GetValue(0) : Activator.CreateInstance(viewType);
        var guidsField = viewType.GetField("guids", BF);
        var guids = ((GUID[])guidsField.GetValue(view) ?? new GUID[0]).ToList();
        foreach (var g in groups)
        {
            var id = (GUID)Get(g, "groupID");
            if (!guids.Contains(id)) guids.Add(id);
        }
        guidsField.SetValue(view, guids.ToArray());
        if (views.Length == 0)
        {
            viewType.GetField("name", BF).SetValue(view, "View");
            views = Array.CreateInstance(viewType, 1);
        }
        views.SetValue(view, 0);
        Set(ctrl, "views", views);
    }

    static object FindEffect(object group, string effectName) =>
        ((Array)Get(group, "effects")).Cast<object>().FirstOrDefault(e => (string)Get(e, "effectName") == effectName);

    // The group's effect of this kind, added after its fader if missing.
    static object Effect(object ctrl, object group, string effectName)
    {
        var existing = FindEffect(group, effectName);
        if (existing != null) return existing;
        var effect = Activator.CreateInstance(EdType("AudioMixerEffectController"), effectName);
        Call(effect, "PreallocateGUIDs");
        AssetDatabase.AddObjectToAsset((Object)effect, (Object)ctrl);
        Call(group, "InsertEffect", effect, ((Array)Get(group, "effects")).Length);
        return effect;
    }

    static void Expose(object ctrl, object group, string name)
    {
        var guid = (GUID)Call(group, "GetGUIDForVolume");
        if (!(bool)Call(ctrl, "ContainsExposedParameter", guid))
            Call(ctrl, "AddExposedParameter", Activator.CreateInstance(EdType("AudioGroupParameterPath"), group, guid));
        var exposed = (Array)Get(ctrl, "exposedParameters");
        for (int i = 0; i < exposed.Length; i++)
        {
            object p = exposed.GetValue(i);
            if ((GUID)p.GetType().GetField("guid", BF).GetValue(p) != guid) continue;
            p.GetType().GetField("name", BF).SetValue(p, name);
            exposed.SetValue(p, i);
        }
        Set(ctrl, "exposedParameters", exposed);
    }

    // ---------- Sound events ----------
    class Def
    {
        public string name, clips, mid, far, group = "Weapons";
        public Vector2 volume = new Vector2(0.9f, 1f), pitch = new Vector2(0.95f, 1.05f);
        public float spatial = 1f, min = 1f, max = 50f;
        public float midDistance = 60f, midMin = 30f, farDistance = 300f, farMin = 150f, crossfade = 60f;
        public int priority = 128, maxVoices;
    }

    static readonly Def[] Defs =
    {
        // The shooter's own shot: stereo, in the head. Pitched up a little (the recordings are 7.62 rifles; the M4 is 5.56).
        // Short, loudness-matched cores (one per shot); the tail plays once when a burst ends (GunAudio).
        new Def { name = "Rifle_Shot_FP", clips = "Weapons/Rifle_FP", volume = new Vector2(0.95f, 1f), pitch = new Vector2(1.06f, 1.1f), spatial = 0f, priority = 0, maxVoices = 4 },
        new Def { name = "Rifle_Tail_FP", clips = "Weapons/Rifle_FP_Tail", volume = new Vector2(0.4f, 0.45f), pitch = new Vector2(1.04f, 1.08f), spatial = 0f, priority = 5, maxVoices = 2 },
        // Everyone else's: close recording up to ~80 m, then 30-50 m recordings, then 65-200 m ones past ~350 m.
        new Def { name = "Rifle_Shot", clips = "Weapons/Rifle_Near", mid = "Weapons/Rifle_Mid", far = "Weapons/Rifle_Far",
                  pitch = new Vector2(1.04f, 1.1f), min = 4f, max = 1500f, midDistance = 80f, midMin = 30f, farDistance = 350f, farMin = 150f, crossfade = 80f, priority = 10, maxVoices = 8 },
        new Def { name = "Rifle_Tail", clips = "Weapons/Rifle_Near_Tail", volume = new Vector2(0.4f, 0.45f), pitch = new Vector2(1.04f, 1.08f), min = 4f, max = 300f, priority = 15, maxVoices = 2 },
        new Def { name = "Bullet_Crack", clips = "Weapons/Bullet_Crack", volume = new Vector2(0.7f, 0.9f), pitch = new Vector2(0.9f, 1.1f), min = 2f, max = 40f, priority = 20 },
        new Def { name = "Dry_Fire", clips = "Weapons/Dry_Fire", group = "Foley", volume = new Vector2(0.7f, 0.8f), spatial = 0.7f, max = 15f, priority = 40 },
        new Def { name = "Mag_Out", clips = "Weapons/Mag_Out", group = "Foley", volume = new Vector2(0.6f, 0.7f), spatial = 0.7f, max = 15f, priority = 40 },
        new Def { name = "Mag_In", clips = "Weapons/Mag_In", group = "Foley", volume = new Vector2(0.7f, 0.8f), spatial = 0.7f, max = 15f, priority = 40 },
        new Def { name = "Charge", clips = "Weapons/Charge", group = "Foley", volume = new Vector2(0.7f, 0.8f), spatial = 0.7f, max = 15f, priority = 40 },
        new Def { name = "Impact_Dirt", clips = "Weapons/Impact_Dirt", volume = new Vector2(0.7f, 0.9f), pitch = new Vector2(0.9f, 1.1f), min = 1.5f, max = 80f, priority = 60 },
        new Def { name = "Impact_Rock", clips = "Weapons/Impact_Rock", volume = new Vector2(0.7f, 0.9f), pitch = new Vector2(0.9f, 1.1f), min = 1.5f, max = 80f, priority = 60 },
        new Def { name = "Impact_Wood", clips = "Weapons/Impact_Wood", volume = new Vector2(0.7f, 0.9f), pitch = new Vector2(0.88f, 1.12f), min = 1.5f, max = 80f, priority = 60 },
        new Def { name = "Impact_Metal", clips = "Weapons/Impact_Metal", volume = new Vector2(0.7f, 0.9f), pitch = new Vector2(0.9f, 1.1f), min = 1.5f, max = 80f, priority = 60 },
        new Def { name = "Impact_Flesh", clips = "Weapons/Impact_Flesh", volume = new Vector2(0.8f, 1f), pitch = new Vector2(0.85f, 1.15f), min = 1.5f, max = 60f, priority = 50 },
        new Def { name = "Step_Dirt", clips = "Footsteps/Step_Dirt", group = "Foley", volume = new Vector2(0.55f, 0.7f), pitch = new Vector2(0.92f, 1.08f), max = 30f, priority = 90 },
        new Def { name = "Step_Forest", clips = "Footsteps/Step_Forest", group = "Foley", volume = new Vector2(0.55f, 0.7f), pitch = new Vector2(0.92f, 1.08f), max = 30f, priority = 90 },
        new Def { name = "Step_Gravel", clips = "Footsteps/Step_Gravel", group = "Foley", volume = new Vector2(0.55f, 0.7f), pitch = new Vector2(0.92f, 1.08f), max = 30f, priority = 90 },
        new Def { name = "Step_Wood", clips = "Footsteps/Step_Wood", group = "Foley", volume = new Vector2(0.55f, 0.7f), pitch = new Vector2(0.92f, 1.08f), max = 30f, priority = 90 },
        new Def { name = "Step_Rock", clips = "Footsteps/Step_Rock", group = "Foley", volume = new Vector2(0.55f, 0.7f), pitch = new Vector2(0.92f, 1.08f), max = 30f, priority = 90 },
        new Def { name = "Step_Water", clips = "Footsteps/Step_Water", group = "Foley", volume = new Vector2(0.55f, 0.7f), pitch = new Vector2(0.92f, 1.08f), max = 30f, priority = 90 },
        new Def { name = "Land", clips = "Footsteps/Land", group = "Foley", volume = new Vector2(0.7f, 0.85f), max = 35f, priority = 80 },
        new Def { name = "Gear_Rustle", clips = "Footsteps/Gear_Rustle", group = "Foley", volume = new Vector2(0.3f, 0.4f), pitch = new Vector2(0.9f, 1.1f), max = 12f, priority = 150 },
        // Houses: doors (Door) and window panes shattering (BreakableGlass)
        new Def { name = "Door_Open", clips = "World/Door_Open", group = "Foley", volume = new Vector2(0.55f, 0.7f), pitch = new Vector2(0.93f, 1.07f), max = 25f, priority = 70 },
        new Def { name = "Door_Close", clips = "World/Door_Close", group = "Foley", volume = new Vector2(0.5f, 0.65f), pitch = new Vector2(0.93f, 1.07f), max = 30f, priority = 70 },
        new Def { name = "Thunder", clips = "World/Thunder", group = "Ambience", volume = new Vector2(0.8f, 1f), pitch = new Vector2(0.85f, 1.1f), min = 300f, max = 6000f, priority = 30, maxVoices = 3 },
        new Def { name = "Glass_Break", clips = "World/Glass_Break", group = "Foley", volume = new Vector2(0.85f, 1f), pitch = new Vector2(0.9f, 1.1f), min = 2f, max = 90f, priority = 45, maxVoices = 4 },
    };

    [MenuItem("Tools/Audio/Build Sound Events")]
    public static void BuildSoundEvents()
    {
        EnsureFolders();
        var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
        foreach (var d in Defs)
        {
            var ev = LoadOrCreate<SoundEvent>(EventsDir + "/" + d.name + ".asset");
            ev.clips = Clips(d.clips);
            ev.midClips = d.mid != null ? Clips(d.mid) : new AudioClip[0];
            ev.farClips = d.far != null ? Clips(d.far) : new AudioClip[0];
            ev.output = mixer != null ? mixer.FindMatchingGroups(d.group).FirstOrDefault(g => g.name == d.group) : null;
            ev.volume = d.volume;
            ev.pitch = d.pitch;
            ev.spatialBlend = d.spatial;
            ev.minDistance = d.min;
            ev.maxDistance = d.max;
            ev.midDistance = d.midDistance;
            ev.midMinDistance = d.midMin;
            ev.farDistance = d.farDistance;
            ev.farMinDistance = d.farMin;
            ev.crossfade = d.crossfade;
            ev.priority = d.priority;
            ev.maxVoices = d.maxVoices;
            if (ev.clips.Length == 0) Debug.LogWarning($"[AudioTool] {d.name}: no clips found for {d.clips}_NN");
            EditorUtility.SetDirty(ev);
        }

        var steps = LoadOrCreate<SurfaceSounds>(EventsDir + "/Footsteps.asset");
        steps.dirt = Event("Step_Dirt"); steps.forest = Event("Step_Forest"); steps.gravel = Event("Step_Gravel");
        steps.wood = Event("Step_Wood"); steps.rock = Event("Step_Rock"); steps.water = Event("Step_Water");
        steps.metal = Event("Step_Rock"); steps.flesh = Event("Step_Dirt");
        EditorUtility.SetDirty(steps);

        var impacts = LoadOrCreate<SurfaceSounds>(EventsDir + "/BulletImpacts.asset");
        impacts.dirt = Event("Impact_Dirt"); impacts.forest = Event("Impact_Dirt"); impacts.gravel = Event("Impact_Rock");
        impacts.wood = Event("Impact_Wood"); impacts.rock = Event("Impact_Rock"); impacts.water = Event("Impact_Dirt");
        impacts.metal = Event("Impact_Metal"); impacts.flesh = Event("Impact_Flesh");
        EditorUtility.SetDirty(impacts);

        AssetDatabase.SaveAssets();
        Debug.Log($"[AudioTool] {Defs.Length} sound events built in {EventsDir}.");
    }

    static T LoadOrCreate<T>(string path) where T : ScriptableObject
    {
        var a = AssetDatabase.LoadAssetAtPath<T>(path);
        if (a != null) return a;
        a = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(a, path);
        return a;
    }

    static SoundEvent Event(string name) => AssetDatabase.LoadAssetAtPath<SoundEvent>(EventsDir + "/" + name + ".asset");

    // "Weapons/Rifle_Mid" -> Assets/Audio/Weapons/Rifle_Mid_01.wav, _02, ...
    static AudioClip[] Clips(string prefix)
    {
        string folder = Root + "/" + Path.GetDirectoryName(prefix).Replace('\\', '/');
        string stem = Path.GetFileName(prefix) + "_";
        return AssetDatabase.FindAssets("t:AudioClip", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => { string n = Path.GetFileNameWithoutExtension(p); return n.StartsWith(stem) && int.TryParse(n.Substring(stem.Length), out _); })
            .OrderBy(p => p)
            .Select(AssetDatabase.LoadAssetAtPath<AudioClip>)
            .ToArray();
    }

    // ---------- Footstep animation events ----------
    static readonly string[] FootEvents = { "FootL", "FootR", "Land" };

    [MenuItem("Tools/Audio/Add Footstep Events")]
    public static void AddFootstepEvents()
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(PlayerAnimationBuilder.ControllerPath);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerAnimationBuilder.ModelPath);
        if (ctrl == null || model == null) { Debug.LogError("[AudioTool] Player controller or model missing."); return; }

        var steps = new HashSet<AnimationClip>();
        var lands = new HashSet<AnimationClip>();
        foreach (var cs in ctrl.layers[0].stateMachine.states)
        {
            var s = cs.state;
            if (s.name == "Locomotion" || s.name == "Crouch") CollectClips(s.motion, steps);
            else if (s.name == "Land") CollectClips(s.motion, lands);
        }

        // Sample everything first: writing the events reimports the FBX files, which replaces the clip objects.
        var planned = new Dictionary<AnimationClip, List<AnimationEvent>>();
        var instance = Object.Instantiate(model);
        instance.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var anim = instance.GetComponent<Animator>();
            Transform l = anim.GetBoneTransform(HumanBodyBones.LeftFoot), r = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            foreach (var c in steps) planned[c] = FootStrikes(c, instance, l, r);
            foreach (var c in lands) planned[c] = Touchdown(c, instance, l, r);
        }
        finally { Object.DestroyImmediate(instance); }

        int written = 0;
        var importers = new Dictionary<string, (ModelImporter imp, ModelImporterClipAnimation[] clips, bool changed)>();
        foreach (var kv in planned)
        {
            var clip = kv.Key;
            string path = AssetDatabase.GetAssetPath(clip);
            if (AssetImporter.GetAtPath(path) is ModelImporter mi)
            {
                if (!importers.TryGetValue(path, out var entry))
                {
                    var anims = mi.clipAnimations;
                    if (anims == null || anims.Length == 0) anims = mi.defaultClipAnimations;
                    entry = (mi, anims, false);
                }
                var ca = entry.clips.FirstOrDefault(a => a.name == clip.name);
                if (ca == null) continue;
                // The importer stores event times normalised (0-1).
                var events = kv.Value.Select(e => new AnimationEvent { functionName = e.functionName, time = e.time / clip.length }).ToArray();
                var merged = MergeEvents(ca.events, events, out bool changed);
                if (changed) { ca.events = merged; entry.changed = true; written++; }
                importers[path] = entry;
            }
            else
            {
                var merged = MergeEvents(AnimationUtility.GetAnimationEvents(clip), kv.Value.ToArray(), out bool changed);
                if (changed) { AnimationUtility.SetAnimationEvents(clip, merged); EditorUtility.SetDirty(clip); written++; }
            }
        }
        foreach (var (imp, clips, changed) in importers.Values)
        {
            if (!changed) continue;
            imp.clipAnimations = clips;
            imp.SaveAndReimport();
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[AudioTool] Footstep events: {steps.Count} locomotion + {lands.Count} landing clips checked, {written} updated. " +
                  string.Join(", ", planned.Select(kv => $"{kv.Key.name}:{kv.Value.Count}")));
    }

    static void CollectClips(Motion m, HashSet<AnimationClip> into)
    {
        if (m is AnimationClip c) into.Add(c);
        else if (m is BlendTree bt) foreach (var child in bt.children) CollectClips(child.motion, into);
    }

    // Replaces the clip's foot events with the new ones (other events are kept). Reports whether anything differs.
    static AnimationEvent[] MergeEvents(AnimationEvent[] existing, AnimationEvent[] foot, out bool changed)
    {
        existing ??= new AnimationEvent[0];
        var old = existing.Where(e => FootEvents.Contains(e.functionName)).Select(e => e.functionName + Mathf.Round(e.time * 1000f)).OrderBy(s => s);
        var neu = foot.Select(e => e.functionName + Mathf.Round(e.time * 1000f)).OrderBy(s => s);
        changed = !old.SequenceEqual(neu);
        return existing.Where(e => !FootEvents.Contains(e.functionName)).Concat(foot).OrderBy(e => e.time).ToArray();
    }

    // A foot strikes when it becomes the lower of the two (the planted one), with 2 cm of hysteresis so jitter
    // doesn't count. Looping clips are scanned twice so a strike across the loop point is found.
    static List<AnimationEvent> FootStrikes(AnimationClip clip, GameObject go, Transform l, Transform r)
    {
        const float hysteresis = 0.02f;
        int n = Mathf.Max(64, Mathf.CeilToInt(clip.length * 120f));
        var diff = new float[n]; // > 0: left foot lower
        for (int i = 0; i < n; i++)
        {
            clip.SampleAnimation(go, clip.length * i / n);
            diff[i] = go.transform.InverseTransformPoint(r.position).y - go.transform.InverseTransformPoint(l.position).y;
        }
        var events = new List<AnimationEvent>();
        bool loop = clip.isLooping;
        bool leftDown = diff[0] > 0f;
        for (int i = 0; i < (loop ? 2 * n : n); i++)
        {
            float d = diff[i % n];
            bool flip = leftDown ? d < -hysteresis : d > hysteresis;
            if (!flip) continue;
            leftDown = !leftDown;
            if (loop && i < n) continue; // first lap only settles which foot is down
            events.Add(new AnimationEvent { functionName = leftDown ? "FootL" : "FootR", time = clip.length * (i % n) / n });
        }
        return events;
    }

    // The first moment the lower foot reaches the ground (within 3 cm of its lowest point in the clip).
    static List<AnimationEvent> Touchdown(AnimationClip clip, GameObject go, Transform l, Transform r)
    {
        int n = Mathf.Max(32, Mathf.CeilToInt(clip.length * 120f));
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            clip.SampleAnimation(go, clip.length * i / n);
            y[i] = Mathf.Min(go.transform.InverseTransformPoint(l.position).y, go.transform.InverseTransformPoint(r.position).y);
        }
        float lowest = y.Min();
        int at = Array.FindIndex(y, v => v <= lowest + 0.03f);
        return new List<AnimationEvent> { new AnimationEvent { functionName = "Land", time = clip.length * Mathf.Max(at, 0) / n } };
    }

    // ---------- Scene ----------
    [MenuItem("Tools/Audio/Wire Scene")]
    public static void WireScene()
    {
        if (RefuseInPlayMode()) return;
        var scene = EditorSceneManager.GetActiveScene();
        BackupScene(scene.path);

        var manager = Object.FindAnyObjectByType<SoundManager>(FindObjectsInactive.Include);
        if (manager == null) manager = new GameObject("Audio").AddComponent<SoundManager>();
        manager.mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
        var player = Object.FindAnyObjectByType<FPSController>(FindObjectsInactive.Include);
        if (player != null) manager.vehicleEntry = player.GetComponent<VehicleEntry>();
        EditorUtility.SetDirty(manager);

        foreach (var gun in Object.FindObjectsByType<HitscanGun>(FindObjectsInactive.Include))
        {
            var ga = gun.GetComponent<GunAudio>();
            if (ga == null) ga = gun.gameObject.AddComponent<GunAudio>();
            ga.shotFirstPerson = Event("Rifle_Shot_FP");
            ga.shot3D = Event("Rifle_Shot");
            ga.tailFirstPerson = Event("Rifle_Tail_FP");
            ga.tail3D = Event("Rifle_Tail");
            ga.bulletCrack = Event("Bullet_Crack");
            ga.dryFire = Event("Dry_Fire");
            ga.magOut = Event("Mag_Out");
            ga.magIn = Event("Mag_In");
            ga.charge = Event("Charge");
            ga.impacts = AssetDatabase.LoadAssetAtPath<SurfaceSounds>(EventsDir + "/BulletImpacts.asset");
            EditorUtility.SetDirty(ga);
        }

        int bodies = 0;
        foreach (var body in Object.FindObjectsByType<PlayerCharacterAnimator>(FindObjectsInactive.Include))
        {
            var fa = body.GetComponent<FootstepAudio>();
            if (fa == null) fa = body.gameObject.AddComponent<FootstepAudio>();
            fa.steps = AssetDatabase.LoadAssetAtPath<SurfaceSounds>(EventsDir + "/Footsteps.asset");
            fa.land = Event("Land");
            fa.gearRustle = Event("Gear_Rustle");
            EditorUtility.SetDirty(fa);
            bodies++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[AudioTool] Scene wired: SoundManager, GunAudio, FootstepAudio on {bodies} character(s).");
    }

    // One backup of the scene from before audio was first wired in (SceneBackups/ProjectZed_before_Audio_*.unity).
    static void BackupScene(string scenePath)
    {
        if (string.IsNullOrEmpty(scenePath)) return;
        string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "SceneBackups");
        Directory.CreateDirectory(dir);
        string stem = Path.GetFileNameWithoutExtension(scenePath) + "_before_Audio_";
        if (Directory.GetFiles(dir, stem + "*.unity").Length > 0) return;
        File.Copy(scenePath, Path.Combine(dir, stem + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".unity"));
    }
}
