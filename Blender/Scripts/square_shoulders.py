"""Square up BaseMale's shoulders: lift and draw back the clavicles, keeping the arms in the T-pose.

The MakeHuman rig's clavicles slope 10 degrees down from the sternum to the shoulder and lie almost flat across the
front of the chest (2 degrees back), so the shoulders sit low and rounded forward and the character looks slouched.
A relaxed, upright adult clavicle rises 6-20 degrees from the sternum to the shoulder (about 7 on average) and angles
back about 20 degrees. This rotates each clavicle bone to ELEVATION / RETRACTION, turns the upper arm back by the same
amount (so the arm keeps pointing straight out), bakes the result into the meshes and makes it the new rest pose.

Usage, either way:
  blender -b BaseMale.blend --python square_shoulders.py            (fixes the file, saves it, re-exports the FBX)
  from square_shoulders import square_shoulders; square_shoulders(rig)   (inside make_human.py, before the T-pose)
"""
import bpy, math, os, sys
from mathutils import Matrix, Vector

ELEVATION = 8.0    # degrees the clavicle rises from the sternum to the shoulder (reference 6-20, mean 7)
RETRACTION = 12.0  # degrees it angles back from straight across the chest (reference ~23; less keeps the chest relaxed)

OUT_BLEND = r"C:/Users/josht/My project/Blender/Player/BaseMale.blend"
FBX = r"C:/Users/josht/My project/Assets/Characters/BaseMale/BaseMale.fbx"


def rotate_about_head(rig, pb, q):
    """Rotate a pose bone (and so its children) by the world-space rotation q about its head."""
    inv = rig.matrix_world.inverted()
    bpy.context.view_layer.update()
    pivot = inv @ (rig.matrix_world @ pb.head)
    r = (inv.to_3x3() @ q.to_matrix() @ rig.matrix_world.to_3x3()).to_4x4()
    pb.matrix = Matrix.Translation(pivot) @ r @ Matrix.Translation(-pivot) @ pb.matrix
    bpy.context.view_layer.update()


def clavicle_angles(rig, side):
    pb = rig.pose.bones[f"mixamorig:{side}Shoulder"]
    d = rig.matrix_world.to_3x3() @ (pb.tail - pb.head)
    # Blender: -Y is the character's front, so +Y is back.
    return math.degrees(math.atan2(d.z, math.hypot(d.x, d.y))), math.degrees(math.atan2(d.y, abs(d.x)))


def square_shoulders(rig, elevation=ELEVATION, retraction=RETRACTION):
    """Pose the clavicles to the target angles with the arms unchanged. Leaves the rig in pose mode, posed."""
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='POSE')
    e, r = math.radians(elevation), math.radians(retraction)
    for side, sx in (("Left", 1), ("Right", -1)):
        before = clavicle_angles(rig, side)
        clav = rig.pose.bones[f"mixamorig:{side}Shoulder"]
        d = (rig.matrix_world.to_3x3() @ (clav.tail - clav.head)).normalized()
        target = Vector((sx * math.cos(e) * math.cos(r), math.cos(e) * math.sin(r), math.sin(e)))
        q = d.rotation_difference(target)
        rotate_about_head(rig, clav, q)
        rotate_about_head(rig, rig.pose.bones[f"mixamorig:{side}Arm"], q.inverted())  # arm keeps its direction
        after = clavicle_angles(rig, side)
        print(f"{side} clavicle: elevation {before[0]:+.1f} -> {after[0]:+.1f}, back {before[1]:+.1f} -> {after[1]:+.1f}")


def apply_pose_as_rest(rig):
    """Bake the current pose into every skinned mesh and make it the armature's rest pose."""
    bpy.ops.object.mode_set(mode='OBJECT')
    skinned = [o for o in bpy.data.objects if o.type == 'MESH'
               and any(m.type == 'ARMATURE' and m.object == rig for m in o.modifiers)]
    for o in skinned:
        for m in [m for m in o.modifiers if m.type == 'ARMATURE']:
            with bpy.context.temp_override(object=o, active_object=o):
                bpy.ops.object.modifier_apply(modifier=m.name)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='POSE'); bpy.ops.pose.armature_apply(selected=False)
    bpy.ops.object.mode_set(mode='OBJECT')
    for o in skinned:
        mod = o.modifiers.new("Armature", 'ARMATURE'); mod.object = rig
        o.modifiers.move(len(o.modifiers) - 1, 0)
    return skinned


def export_fbx(rig, skinned, path):
    for o in bpy.context.view_layer.objects: o.select_set(o == rig or o in skinned)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                             bake_anim=False, use_armature_deform_only=False, mesh_smooth_type='FACE',
                             apply_scale_options='FBX_SCALE_ALL', path_mode='STRIP', embed_textures=False)


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out_blend = args[0] if len(args) > 0 else OUT_BLEND
    fbx = args[1] if len(args) > 1 else FBX
    rig = bpy.data.objects["BaseMale_Rig"]
    square_shoulders(rig)
    skinned = apply_pose_as_rest(rig)
    bpy.ops.wm.save_as_mainfile(filepath=out_blend)
    print("SAVED", out_blend)
    export_fbx(rig, skinned, fbx)
    print("EXPORTED", fbx)
