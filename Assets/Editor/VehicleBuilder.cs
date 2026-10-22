using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Turns the Racing Cars Pack models into drivable cars: Tools > Vehicles > Build Drivable Cars.
// Each car is a new prefab whose root holds the physics (Rigidbody, box collider, CarController, one
// WheelCollider per wheel) with the pack's own prefab nested inside as the model, scaled to real-world size
// by its wheels. The pack's Legacy materials are upgraded to URP Lit. The cars are parked in the open field.
public static class VehicleBuilder
{
    const string PackDir = "Assets/Racing Cars Pack 1";
    const string OutDir = "Assets/Vehicles";
    // Real cars: about 0.66 m wheels. The pack models are 1.1-1.8x real size.
    const float WheelDiameter = 0.66f;
    const float SuspensionDistance = 0.22f;
    static readonly string[] WheelNames = { "Front_Left_Wheel", "Front_Right_Wheel", "Rear_Left_Wheel", "Rear_Right_Wheel" };

    [MenuItem("Tools/Vehicles/Build Drivable Cars")]
    public static void Build()
    {
        UpgradeMaterials();
        if (!AssetDatabase.IsValidFolder(OutDir)) AssetDatabase.CreateFolder("Assets", "Vehicles");

        var prefabs = Directory.GetFiles(PackDir + "/Prefabs", "*.prefab").Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToArray();
        var built = prefabs.Select(BuildCar).Where(p => p != null).ToArray();
        PlaceInScene(built);
        AddEntryToPlayer();
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        Debug.Log($"[VehicleBuilder] Built {built.Length} drivable cars in {OutDir}");
    }

    static void UpgradeMaterials()
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { PackDir }))
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (mat.shader == lit) continue;
            var tex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            var color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            mat.shader = lit;
            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", 0.55f); // painted bodywork
            EditorUtility.SetDirty(mat);
        }
    }

    static GameObject BuildCar(string modelPrefabPath)
    {
        var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPrefabPath);
        string name = "Drivable_" + modelPrefab.name;
        var root = new GameObject(name);
        try
        {
            var model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab, root.transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            var wheelVisuals = WheelNames.Select(n => model.transform.Find(n)).ToArray();
            if (wheelVisuals.Any(w => w == null)) { Debug.LogWarning($"[VehicleBuilder] {modelPrefab.name}: wheels not found, skipped"); return null; }
            float diameter = wheelVisuals.Average(w => w.GetComponent<Renderer>().bounds.size.y);
            model.transform.localScale = Vector3.one * (WheelDiameter / diameter);

            // Root pivot on the ground, centred between the wheels.
            Vector3 centre = wheelVisuals.Aggregate(Vector3.zero, (s, w) => s + w.position) / 4f;
            float radius = WheelDiameter * 0.5f;
            model.transform.localPosition = new Vector3(-centre.x, radius - centre.y, -centre.z);
            float axleY = radius;

            // Body collider: the bodywork, with its floor no lower than the axles so the wheels carry the car.
            var bodyRenderers = model.GetComponentsInChildren<Renderer>().Where(r => !wheelVisuals.Contains(r.transform)).ToArray();
            var bounds = bodyRenderers[0].bounds;
            foreach (var r in bodyRenderers) bounds.Encapsulate(r.bounds);
            float bottom = Mathf.Max(bounds.min.y, axleY);
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(bounds.center.x, (bottom + bounds.max.y) * 0.5f, bounds.center.z);
            box.size = new Vector3(bounds.size.x * 0.95f, bounds.max.y - bottom, bounds.size.z * 0.97f);

            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 1300f;
            rb.linearDamping = 0.02f;
            rb.angularDamping = 0.3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.automaticCenterOfMass = false;
            rb.centerOfMass = new Vector3(0f, axleY * 0.8f, 0f); // low, for stability

            var colliderRoot = new GameObject("WheelColliders").transform;
            colliderRoot.SetParent(root.transform, false);
            var car = root.AddComponent<CarController>();
            car.wheels = new CarController.Wheel[4];
            for (int i = 0; i < 4; i++)
            {
                var visual = wheelVisuals[i];
                var wcGo = new GameObject(WheelNames[i].Replace("_Wheel", ""));
                wcGo.transform.SetParent(colliderRoot, false);
                // Mounted half a suspension travel above the wheel, so at rest the wheel sits where the model has it.
                wcGo.transform.position = new Vector3(visual.position.x, axleY + SuspensionDistance * 0.5f, visual.position.z);
                var wc = wcGo.AddComponent<WheelCollider>();
                wc.radius = radius;
                wc.mass = 20f;
                wc.suspensionDistance = SuspensionDistance;
                wc.forceAppPointDistance = 0.1f;
                wc.suspensionSpring = new JointSpring { spring = 35000f, damper = 4500f, targetPosition = 0.5f };
                wc.forwardFriction = new WheelFrictionCurve { extremumSlip = 0.4f, extremumValue = 1f, asymptoteSlip = 0.8f, asymptoteValue = 0.5f, stiffness = 1.5f };
                wc.sidewaysFriction = new WheelFrictionCurve { extremumSlip = 0.25f, extremumValue = 1f, asymptoteSlip = 0.5f, asymptoteValue = 0.75f, stiffness = 1.8f };
                car.wheels[i] = new CarController.Wheel { collider = wc, visual = visual, front = i < 2, left = i % 2 == 0 };
            }

            string path = $"{OutDir}/{name}.prefab";
            return PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { Object.DestroyImmediate(root); }
    }

    // Parked in a row at the back of the field, behind the player's start, facing the field.
    static void PlaceInScene(GameObject[] cars)
    {
        var old = GameObject.Find("Vehicles");
        if (old != null) Object.DestroyImmediate(old);
        var parent = new GameObject("Vehicles").transform;
        var terrain = Terrain.activeTerrain;
        float spacing = 6f, startX = -spacing * (cars.Length - 1) * 0.5f;
        for (int i = 0; i < cars.Length; i++)
        {
            var car = (GameObject)PrefabUtility.InstantiatePrefab(cars[i], parent);
            var pos = new Vector3(startX + i * spacing, 0f, -25f);
            if (terrain != null) pos.y = terrain.SampleHeight(pos) + terrain.transform.position.y + 0.05f;
            car.transform.SetPositionAndRotation(pos, Quaternion.identity);
        }
    }

    static void AddEntryToPlayer()
    {
        var player = GameObject.Find("Player");
        if (player != null && player.GetComponent<VehicleEntry>() == null) player.AddComponent<VehicleEntry>();
    }
}
