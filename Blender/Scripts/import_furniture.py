"""Turns pieces of the downloaded BlendSwap furniture packs (CC0, declared by the uploaders) into game-ready
alternates for the house kit: modifiers applied, joined, turned to face +z (plan space), stood on the origin,
scaled to the kit piece they stand in for, decimated, and exported with their textures.

Run: blender -b -Y -P import_furniture.py -- <BlendSwap download dir>
Out: Assets/Environment/Houses/Models/House_KitImported.fbx (+ textures beside it) and
     Assets/Environment/Houses/Plans/House_KitImported.json (which kit item each one can replace, and where).

Sources (blendswap.com, CC0): Medieval Furniture #26451 (AnyRPG), Metal bedframe #16481 (MiriamAHoyt),
Living/TV Room Study #25834 (mbarros). The other four downloaded packs (kitchen, bathroom, mission bedroom,
garden) were too heavy or too modern to use as they are.
"""
import bmesh, bpy, json, math, os, sys
from mathutils import Matrix, Vector

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import make_house

SRC = sys.argv[sys.argv.index("--") + 1]
OUT_MODELS = make_house.OUT_MODELS
OUT_TEX = OUT_MODELS + "/Textures"
OUT_PLANS = make_house.OUT_PLANS

# item: source file, objects, fit box (w, h, d) in metres, triangle budget, material renames, which kit item it
# replaces in which house types (and how often), and how to find its back ("top": the high part, e.g. a chair's
# backrest or a bed's headboard; or an object that marks the front).
SPECS = {
    "chair_rustic": {"file": "medieval/medieval.blend", "objects": ["ChairOld"], "fit": (0.5, 0.95, 0.55), "tris": 400,
                     "replaces": "chair", "types": ["Shack", "Cabin"], "chance": 0.7, "back": "top"},
    "table_rustic": {"file": "medieval/medieval.blend", "objects": ["TableKitchenOld"], "fit": (0.9, 0.77, 0.7), "tris": 300,
                     "replaces": "table_rough", "types": ["Shack", "Cabin"], "chance": 0.8, "back": None},
    "bed_rustic":   {"file": "medieval/medieval.blend", "objects": ["Bed", "Blanket", "PillowWhite"], "fit": (1.0, 0.8, 2.0), "tris": 400,
                     "replaces": "bed_single", "types": ["Shack"], "chance": 0.6, "back": "top"},
    "chest_rustic": {"file": "medieval/medieval.blend", "objects": ["ChestBottom", "ChestTop", "ChestLock"], "fit": (0.8, 0.55, 0.5), "tris": 400,
                     "replaces": "chest", "types": ["Shack", "Cabin", "Farmhouse", "Rambler"], "chance": 1.0, "back": "front:ChestLock"},
    "bed_iron":     {"file": "metalbed/x/metal bed.blend", "objects": ["bedframe arch foot", "bedframe arch head", "bedframe sides", "bedframe slats",
                                                                       "foot horiz. bars", "foot vert. bars", "foot vert.bars", "head horiz. bars"],
                     "fit": (0.95, 1.0, 2.0), "tris": 3000, "mats": {"bedframe": "Furn_Metal"}, "mattress": True, "uniform": True,
                     "replaces": "bed_single", "types": ["Cabin", "Shack", "Farmhouse", "Rambler"], "chance": 0.5, "back": "top"},
    "couch_leather": {"file": "living/living.blend", "objects": ["couch body", "back cushion", "seat cushion", "feet"], "fit": (2.0, 0.85, 0.9),
                      "tris": 3000, "mats": {"leather_darkbrown": "Furn_Leather", "Material.002": "Furn_DarkWood"}, "uniform": True,
                      "replaces": "couch", "types": ["Rambler", "Farmhouse", "Cabin"], "chance": 0.5, "back": "top"},
}


def evaluated_copy(ob, dg):
    """A new mesh object with ob's modifiers applied (subdivision capped at 1) and its world transform baked."""
    for m in ob.modifiers:
        if m.type == "SUBSURF":
            m.levels = min(m.levels, 1)
            m.render_levels = min(m.render_levels, 1)
        if m.type == "COLLISION":
            m.show_viewport = False
    dg.update()
    ev = ob.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ev, depsgraph=dg)
    me.transform(ob.matrix_world)
    copy = bpy.data.objects.new(ob.name + "_baked", me)
    bpy.context.scene.collection.objects.link(copy)
    return copy


def build(name, spec):
    bpy.ops.wm.open_mainfile(filepath=os.path.join(SRC, spec["file"]), load_ui=False)
    bpy.ops.file.unpack_all(method="WRITE_LOCAL") if any(i.packed_file for i in bpy.data.images) else None
    dg = bpy.context.evaluated_depsgraph_get()
    parts = [evaluated_copy(bpy.data.objects[n], dg) for n in spec["objects"]]
    front_marker = None
    if spec["back"] and spec["back"].startswith("front:"):
        mk = next(p for p in parts if p.name.startswith(spec["back"][6:]))
        front_marker = sum((v.co for v in mk.data.vertices), Vector()) / len(mk.data.vertices)
    # Join into one object
    for o in bpy.context.scene.objects:
        o.select_set(False)
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    ob = parts[0]
    ob.name = name
    me = ob.data
    vs = [v.co.copy() for v in me.vertices]
    lo = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
    hi = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    centre = (lo + hi) / 2
    # Turn it so its back is toward Blender +Y (plan -z), i.e. it faces plan +z
    yaw = 0.0
    if spec["back"] == "top" or front_marker is not None:
        if front_marker is not None:
            d = -(front_marker - centre)
        else:
            top = [v for v in vs if v.z > lo.z + (hi.z - lo.z) * 0.7]
            d = sum(top, Vector()) / len(top) - centre
        d.z = 0
        ang = math.atan2(d.x, d.y)                     # angle of the back direction from +Y
        yaw = round(ang / (math.pi / 2)) * (math.pi / 2)    # turn counter-clockwise by it, snapped to 90 degrees
    me.transform(Matrix.Translation(-Vector((centre.x, centre.y, lo.z))))
    me.transform(Matrix.Rotation(yaw, 4, "Z"))
    vs = [v.co for v in me.vertices]
    size = Vector((max(v.x for v in vs) - min(v.x for v in vs), max(v.y for v in vs) - min(v.y for v in vs), max(v.z for v in vs)))
    w, h, d = spec["fit"]
    if spec.get("uniform"):
        s = min(w / size.x, d / size.y, h / size.z)
        me.transform(Matrix.Scale(s, 4))
    else:   # stretched to fill the kit piece's box (the low-poly rustic set takes it fine)
        me.transform(Matrix.Diagonal((w / size.x, d / size.y, h / size.z, 1.0)))
    # Decimate to budget
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    if tris > spec["tris"]:
        mod = ob.modifiers.new("Decimate", "DECIMATE")
        mod.ratio = spec["tris"] / tris
        mod.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = ob
        bpy.ops.object.modifier_apply(modifier=mod.name)
    # Materials: renamed to kit slots, or kept (textures and all) as Imp_<item>_<name>
    for slot in ob.material_slots:
        m = slot.material
        if m is None:
            continue
        mats = spec.get("mats", {})
        new = mats.get(m.name) or mats.get(m.name.split(".")[0])
        if new:
            slot.material = make_house.material(new)
        else:
            m.name = f"Imp_{name}_{m.name.split('.')[0]}"
    if spec.get("mattress"):   # the iron frame comes bare: add the kit's mattress, blanket and pillow
        km = make_house.Mesh()
        sink = make_house.Sink(single=km)
        dims = ob.dimensions          # Blender x = width, y = length
        wd, base, dd = dims.x - 0.08, 0.3, dims.y - 0.12
        make_house.k_box(sink, -wd / 2, base, -dd / 2, wd / 2, base + 0.18, dd / 2, "Furn_Mattress")
        make_house.k_box(sink, -wd / 2 - 0.01, base + 0.1, -dd / 2 + 0.45, wd / 2 + 0.01, base + 0.21, dd / 2 + 0.01, "Furn_Blanket")
        make_house.k_box(sink, -wd / 2 + 0.08, base + 0.18, -dd / 2 + 0.05, wd / 2 - 0.08, base + 0.28, -dd / 2 + 0.4, "Furn_Mattress")
        extra = km.to_object(name + "_bedding", bpy.context.scene.collection)
        for o in bpy.context.scene.objects:
            o.select_set(False)
        extra.select_set(True); ob.select_set(True)
        bpy.context.view_layer.objects.active = ob
        bpy.ops.object.join()
    ob.data.update()
    bpy.context.view_layer.update()
    final = sum(len(p.vertices) - 2 for p in ob.data.polygons)
    dims = ob.dimensions
    print(f"IMPORT {name}: {final} tris, {dims.x:.2f} x {dims.z:.2f} x {dims.y:.2f} m, turned {math.degrees(yaw):.0f} deg, "
          f"mats {[s.material.name for s in ob.material_slots if s.material]}")
    # Hand the finished mesh over by writing it to a temp library
    ob.data.name = name
    path = os.path.join(os.environ.get("TEMP", "."), f"pz_import_{name}.blend")
    bpy.data.libraries.write(path, {ob}, fake_user=True, path_remap="ABSOLUTE")
    return path


def main():
    os.makedirs(OUT_TEX, exist_ok=True)
    libs = {name: build(name, spec) for name, spec in SPECS.items()}
    bpy.ops.wm.read_factory_settings(use_empty=True)
    col = bpy.context.scene.collection
    for name, path in libs.items():
        with bpy.data.libraries.load(path, link=False) as (src, dst):
            dst.objects = [name]
        for ob in dst.objects:
            col.objects.link(ob)
            ob.location = (0, 0, 0)
    # Copy textures beside the FBX so Unity's importer finds them by name
    for im in bpy.data.images:
        if im.filepath and im.source == "FILE":
            src = bpy.path.abspath(im.filepath)
            if os.path.exists(src):
                dst = os.path.join(OUT_TEX, os.path.basename(src))
                if not os.path.exists(dst):
                    import shutil
                    shutil.copyfile(src, dst)
                im.filepath = dst
    bpy.ops.export_scene.fbx(filepath=f"{OUT_MODELS}/House_KitImported.fbx", use_selection=False, object_types={"MESH"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                             bake_space_transform=True, mesh_smooth_type="FACE", path_mode="STRIP", embed_textures=False,
                             add_leaf_bones=False)
    bpy.ops.wm.save_as_mainfile(filepath=f"{make_house.OUT_BLEND}/House_KitImported.blend")
    with open(f"{OUT_PLANS}/House_KitImported.json", "w") as fh:
        json.dump({"items": [{"item": n, "replaces": s["replaces"], "types": s["types"], "chance": s["chance"]} for n, s in SPECS.items()]}, fh, indent=1)
    print("EXPORTED House_KitImported.fbx")


main()
