"""Review renders of a garment file (Clothes_<Name>.blend): front, back, side, close-ups, in a standing pose and a
stride. Genitals are hidden, as they are in Unity while a garment covers them.
Usage: blender -b Clothes_X.blend --python clothes_review.py -- <out_dir>"""
import bpy, math, sys, os
from mathutils import Matrix, Vector

out = sys.argv[sys.argv.index("--") + 1]
bpy.data.objects["BaseMale_Genitals"].hide_render = True
rig = bpy.data.objects["BaseMale_Rig"]
bpy.context.view_layer.objects.active = rig
for o in bpy.context.view_layer.objects: o.select_set(o == rig)
bpy.ops.object.mode_set(mode='POSE')
inv = rig.matrix_world.inverted()


def turn(bone, axis, deg):
    pb = rig.pose.bones["mixamorig:" + bone]
    head = inv @ (rig.matrix_world @ pb.head); ax = (inv.to_3x3() @ Vector(axis)).normalized()
    pb.matrix = Matrix.Translation(head) @ Matrix.Rotation(math.radians(deg), 4, ax) @ Matrix.Translation(-head) @ pb.matrix
    bpy.context.view_layer.update()


def reset():
    for pb in rig.pose.bones: pb.matrix_basis = Matrix()
    bpy.context.view_layer.update()


s = bpy.context.scene
s.render.engine = 'BLENDER_EEVEE' if 'BLENDER_EEVEE' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE_NEXT'
w = bpy.data.worlds.new("W"); s.world = w; w.use_nodes = True
w.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.58, 0.62, 1); w.node_tree.nodes["Background"].inputs[1].default_value = 0.7
for rot, energy in (((50, 0, 30), 3.0), ((60, 0, 200), 1.2)):
    l = bpy.data.objects.new("L", bpy.data.lights.new("L", 'SUN')); l.data.energy = energy; s.collection.objects.link(l)
    l.rotation_euler = tuple(math.radians(a) for a in rot)
cd = bpy.data.cameras.new("C"); cd.lens = 70
cam = bpy.data.objects.new("C", cd); s.collection.objects.link(cam); s.camera = cam


def bone(n): return rig.matrix_world @ rig.pose.bones["mixamorig:" + n].head


def shot(name, target, direction, dist, res=(800, 800)):
    s.render.resolution_x, s.render.resolution_y = res
    cam.location = target + direction.normalized() * dist
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    s.render.filepath = os.path.join(out, name + ".png"); bpy.ops.render.render(write_still=True)


# Standing, arms down.
turn("LeftArm", (0, 1, 0), 75); turn("RightArm", (0, 1, 0), -75)
h = bone("Hips")
shot("front", h, Vector((0.25, -1, 0.05)), 2.2)
shot("back", h, Vector((-0.25, 1, 0.05)), 2.2)
shot("side", h, Vector((1, 0, 0.05)), 2.2)
shot("closeup", h + Vector((0, 0, -0.05)), Vector((0.35, -1, 0)), 1.2)
shot("seat", h + Vector((0, 0, -0.07)), Vector((-0.45, 1, 0.1)), 1.1)
# Stride and a deep squat-like bend, to check the skin doesn't poke through.
turn("LeftUpLeg", (1, 0, 0), -45); turn("LeftLeg", (1, 0, 0), 60); turn("RightUpLeg", (1, 0, 0), 25)
shot("stride", bone("Hips"), Vector((1, -0.4, 0.1)), 2.4)
reset(); turn("LeftArm", (0, 1, 0), 75); turn("RightArm", (0, 1, 0), -75)
turn("LeftUpLeg", (1, 0, 0), -95); turn("RightUpLeg", (1, 0, 0), -95); turn("LeftLeg", (1, 0, 0), 100); turn("RightLeg", (1, 0, 0), 100)
shot("crouch", bone("Hips"), Vector((0.6, -1, 0.2)), 2.4)
