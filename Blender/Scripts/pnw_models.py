"""Decimates the Poly Haven (CC0) nature models for ProjectZed's PNW forest and exports them as FBX.

Run: blender -b -P pnw_models.py -- <download dir>/mod <project Assets/Environment/PNW/Models dir>

Each glTF is imported, every mesh is decimated to about TARGET tris (per mesh object, so a "set" keeps its separate
rocks), transforms are applied, and the result is written to <name>.fbx next to its textures (copied as
<name>_color.jpg / _normal.jpg / _arm.jpg or _rough.jpg). Materials are set up in Unity by PNWForestBuilder.
"""
import os
import shutil
import sys

import bpy

SRC, OUT = sys.argv[sys.argv.index("--") + 1:][:2]
os.makedirs(OUT, exist_ok=True)

# Target triangles per mesh object. Undergrowth is instanced thousands of times, so it is kept very light.
TARGET = {
    "fern_02": 3000, "moss_01": 1200,
    "rock_moss_set_01": 1500, "rock_moss_set_02": 1500,
    "tree_stump_01": 2500, "tree_stump_02": 2500,
    "dead_tree_trunk": 3500, "dead_tree_trunk_02": 3500,
    "boulder_01": 3000,
}


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


for name, target in TARGET.items():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    folder = os.path.join(SRC, name)
    bpy.ops.import_scene.gltf(filepath=os.path.join(folder, name + ".gltf"))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    report = []
    for o in meshes:
        before = tri_count(o)
        if before > target:
            mod = o.modifiers.new("dec", "DECIMATE")
            mod.ratio = target / before
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.modifier_apply(modifier=mod.name)
        report.append(f"{o.name}:{before}->{tri_count(o)}")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, name + ".fbx"), use_selection=False, object_types={"MESH"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                             mesh_smooth_type="FACE", path_mode="STRIP", embed_textures=False, add_leaf_bones=False)
    for f in os.listdir(os.path.join(folder, "textures")):
        kind = f.replace(name + "_", "").split("_")[0]  # diff / nor / arm / rough
        dst = {"diff": "color", "nor": "normal", "arm": "arm", "rough": "rough"}.get(kind)
        if dst:
            shutil.copyfile(os.path.join(folder, "textures", f), os.path.join(OUT, f"{name}_{dst}.jpg"))
    print("PNW", name, " ".join(report))
