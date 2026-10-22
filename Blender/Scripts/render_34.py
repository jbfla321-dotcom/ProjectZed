import bpy, math, sys, os
from mathutils import Vector
out = sys.argv[sys.argv.index("--") + 1]
s = bpy.context.scene
for o in bpy.data.objects:
    if o.name == "Sculpt_21626": o.hide_render = True
s.render.engine = 'BLENDER_WORKBENCH'; s.display.shading.light = 'STUDIO'; s.display.shading.color_type = 'TEXTURE'
s.render.resolution_x = s.render.resolution_y = 900
cd = bpy.data.cameras.new("C"); cd.type = 'ORTHO'; cd.ortho_scale = 1.0
cam = bpy.data.objects.new("C", cd); s.collection.objects.link(cam); s.camera = cam
for name, yaw in (("34front", 35), ("34back", 215)):
    d = Vector((math.sin(math.radians(yaw)), -math.cos(math.radians(yaw)), 0))
    cam.location = Vector((0, 0, 1.3)) + d * 5; cam.rotation_euler = (math.radians(90), 0, math.radians(yaw))
    s.render.filepath = os.path.join(out, name + ".png"); bpy.ops.render.render(write_still=True)
