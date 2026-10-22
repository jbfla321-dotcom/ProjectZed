"""Pose Ch17's rig (arms down, elbow/knee bent, leg forward, spine twist) and render, to check the skinning."""
import bpy, math, sys, os
from mathutils import Matrix, Vector
out = sys.argv[sys.argv.index("--") + 1]
arm = bpy.data.objects["Armature"]
print("pose_position", arm.data.pose_position, "action", arm.animation_data.action if arm.animation_data else None)
arm.animation_data_clear(); arm.data.pose_position = "POSE"
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='POSE')
inv = arm.matrix_world.inverted()
def turn(bone, axis, deg):
    pb = arm.pose.bones["mixamorig1:" + bone]
    head = inv @ (arm.matrix_world @ pb.head)
    ax = (inv.to_3x3() @ Vector(axis)).normalized()
    pb.matrix = Matrix.Translation(head) @ Matrix.Rotation(math.radians(deg), 4, ax) @ Matrix.Translation(-head) @ pb.matrix
    bpy.context.view_layer.update()
turn("LeftArm", (0, 1, 0), 65); turn("RightArm", (0, 1, 0), -65)     # arms down to the sides
turn("LeftForeArm", (0, 0, 1), 70)                                   # elbow bend
turn("RightArm", (1, 0, 0), -40)                                     # right arm forward
turn("LeftUpLeg", (1, 0, 0), -50); turn("LeftLeg", (1, 0, 0), 80)    # knee up and bent
turn("Spine1", (0, 0, 1), 20)                                        # twist
bpy.ops.object.mode_set(mode='OBJECT')
s = bpy.context.scene
for o in bpy.data.objects:
    if o.name == "Sculpt_21626": o.hide_render = True
s.render.engine = 'BLENDER_WORKBENCH'; s.display.shading.light = 'STUDIO'; s.display.shading.color_type = 'TEXTURE'
s.render.resolution_x = 800; s.render.resolution_y = 1000
cd = bpy.data.cameras.new("C"); cd.type = 'ORTHO'; cd.ortho_scale = 2.0
cam = bpy.data.objects.new("C", cd); s.collection.objects.link(cam); s.camera = cam
for name, yaw in (("pose_front", 20), ("pose_side", 100), ("pose_back", 200)):
    d = Vector((math.sin(math.radians(yaw)), -math.cos(math.radians(yaw)), 0))
    cam.location = Vector((0, 0, 0.95)) + d * 5; cam.rotation_euler = (math.radians(90), 0, math.radians(yaw))
    s.render.filepath = os.path.join(out, name + ".png"); bpy.ops.render.render(write_still=True)
