"""Head, neck and torso check, optionally with Ch17's hair hidden. Arms down like a relaxed idle.
Usage: blender -b Ch17_Base_v4.blend --python torso_check.py -- <out_dir> [nohair]"""
import bpy, math, sys, os
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
out = args[0]
if "nohair" in args:
    for n in ("Ch17_Hair",):
        if n in bpy.data.objects: bpy.data.objects[n].hide_render = True
arm = bpy.data.objects["Armature"]; arm.animation_data_clear(); arm.data.pose_position = "POSE"
bpy.context.view_layer.objects.active = arm; bpy.ops.object.mode_set(mode='POSE')
inv = arm.matrix_world.inverted()


def turn(bone, axis, deg):
    pb = arm.pose.bones["mixamorig1:" + bone]
    head = inv @ (arm.matrix_world @ pb.head); ax = (inv.to_3x3() @ Vector(axis)).normalized()
    pb.matrix = Matrix.Translation(head) @ Matrix.Rotation(math.radians(deg), 4, ax) @ Matrix.Translation(-head) @ pb.matrix
    bpy.context.view_layer.update()


turn("LeftArm", (0, 1, 0), 75); turn("RightArm", (0, 1, 0), -75)
bpy.ops.object.mode_set(mode='OBJECT')
s = bpy.context.scene
for o in bpy.data.objects:
    if o.name == "Sculpt_21626": o.hide_render = True
s.render.engine = 'BLENDER_EEVEE' if 'BLENDER_EEVEE' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE_NEXT'
s.render.resolution_x = s.render.resolution_y = 800
w = bpy.data.worlds.new("W"); s.world = w; w.use_nodes = True
w.node_tree.nodes["Background"].inputs[0].default_value = (0.6, 0.65, 0.7, 1); w.node_tree.nodes["Background"].inputs[1].default_value = 0.6
sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", 'SUN')); sun.data.energy = 3; s.collection.objects.link(sun)
sun.rotation_euler = (math.radians(50), 0, math.radians(30))
cd = bpy.data.cameras.new("C"); cd.lens = 85
cam = bpy.data.objects.new("C", cd); s.collection.objects.link(cam); s.camera = cam


def bone(n): return arm.matrix_world @ arm.pose.bones["mixamorig1:" + n].head


def shot(name, target, direction, dist):
    cam.location = target + direction.normalized() * dist
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    s.render.filepath = os.path.join(out, name + ".png"); bpy.ops.render.render(write_still=True)


shot("torso_front", bone("Spine1"), Vector((0, -1, 0)), 3.4)
shot("torso_back", bone("Spine1"), Vector((0, 1, 0)), 3.4)
shot("head_front", bone("Head"), Vector((0, -1, 0)), 1.3)
shot("head_side", bone("Head"), Vector((1, -0.15, 0)), 1.3)
shot("neck_34", bone("Neck"), Vector((0.7, -1, 0.1)), 1.4)
