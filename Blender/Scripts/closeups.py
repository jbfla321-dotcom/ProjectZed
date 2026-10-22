"""Close-up renders: crotch with legs apart (front and from below), shoulders/head, in Eevee for realistic shading."""
import bpy, math, sys, os
from mathutils import Matrix, Vector
out = sys.argv[sys.argv.index("--") + 1]
arm = bpy.data.objects["Armature"]; arm.animation_data_clear(); arm.data.pose_position = "POSE"
bpy.context.view_layer.objects.active = arm; bpy.ops.object.mode_set(mode='POSE')
inv = arm.matrix_world.inverted()
def turn(bone, axis, deg):
    pb = arm.pose.bones["mixamorig1:" + bone]
    head = inv @ (arm.matrix_world @ pb.head); ax = (inv.to_3x3() @ Vector(axis)).normalized()
    pb.matrix = Matrix.Translation(head) @ Matrix.Rotation(math.radians(deg), 4, ax) @ Matrix.Translation(-head) @ pb.matrix
    bpy.context.view_layer.update()
pose = sys.argv[sys.argv.index("--") + 2] if len(sys.argv) > sys.argv.index("--") + 2 else "walk"
if pose == "walk":
    turn("LeftArm", (0, 1, 0), 70); turn("RightArm", (0, 1, 0), -70)
    turn("LeftUpLeg", (1, 0, 0), -30); turn("RightUpLeg", (1, 0, 0), 25); turn("LeftLeg", (1, 0, 0), 30)
    turn("LeftUpLeg", (0, 1, 0), -12); turn("RightUpLeg", (0, 1, 0), 12)
bpy.ops.object.mode_set(mode='OBJECT')
s = bpy.context.scene
for o in bpy.data.objects:
    if o.name == "Sculpt_21626": o.hide_render = True
s.render.engine = 'BLENDER_EEVEE' if 'BLENDER_EEVEE' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE_NEXT'
s.render.resolution_x = s.render.resolution_y = 800
w = bpy.data.worlds.new("W"); s.world = w; w.use_nodes = True; w.node_tree.nodes["Background"].inputs[0].default_value = (0.6, 0.65, 0.7, 1); w.node_tree.nodes["Background"].inputs[1].default_value = 0.6
sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", 'SUN')); sun.data.energy = 3; s.collection.objects.link(sun)
sun.rotation_euler = (math.radians(50), 0, math.radians(30))
cd = bpy.data.cameras.new("C"); cd.lens = 85
cam = bpy.data.objects.new("C", cd); s.collection.objects.link(cam); s.camera = cam
def shot(name, target, direction, dist):
    cam.location = target + direction.normalized() * dist
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    s.render.filepath = os.path.join(out, name + ".png"); bpy.ops.render.render(write_still=True)
hips = arm.matrix_world @ arm.pose.bones["mixamorig1:Hips"].head
head = arm.matrix_world @ arm.pose.bones["mixamorig1:Head"].head
shot("crotch_front", hips - Vector((0, 0, 0.12)), Vector((0.15, -1, -0.1)), 1.6)
shot("crotch_below", hips - Vector((0, 0, 0.15)), Vector((0.1, -0.5, -1)), 1.4)
shot("upper_front", head - Vector((0, 0, 0.3)), Vector((0.3, -1, 0.05)), 2.4)
shot("head", head + Vector((0, 0, 0.05)), Vector((0.5, -1, 0.15)), 1.0)
