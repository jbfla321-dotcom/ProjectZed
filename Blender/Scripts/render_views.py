import bpy, math, sys, os
from mathutils import Vector
out_dir = sys.argv[sys.argv.index("--")+1]
hide = sys.argv[sys.argv.index("--")+2].split(",") if len(sys.argv) > sys.argv.index("--")+2 else []
tag = sys.argv[sys.argv.index("--")+3] if len(sys.argv) > sys.argv.index("--")+3 else "cur"
scene = bpy.context.scene
for o in bpy.data.objects:
    if o.name in hide: o.hide_render = True
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'TEXTURE'
scene.render.resolution_x = 700; scene.render.resolution_y = 1000
scene.render.film_transparent = False
cam_data = bpy.data.cameras.new("Cam"); cam_data.type = 'ORTHO'; cam_data.ortho_scale = 2.2
cam = bpy.data.objects.new("Cam", cam_data); scene.collection.objects.link(cam); scene.camera = cam
# find the character's center (Blender: Z up)
meshes = [o for o in bpy.data.objects if o.type == 'MESH' and not o.hide_render and o.visible_get()]
pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))); hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
center = (lo + hi) / 2
print("bounds", lo, hi)
for name, yaw in [("front", 0), ("back", 180), ("side", 90)]:
    d = Vector((math.sin(math.radians(yaw)), -math.cos(math.radians(yaw)), 0))
    cam.location = center + d * 5
    cam.rotation_euler = (math.radians(90), 0, math.radians(yaw))
    scene.render.filepath = os.path.join(out_dir, f"{tag}_{name}.png")
    bpy.ops.render.render(write_still=True)
# close-up of head/torso
cam_data.ortho_scale = 0.9
cam.location = Vector((center.x, center.y - 5, hi.z - 0.35)); cam.rotation_euler = (math.radians(90), 0, 0)
scene.render.resolution_x = 900; scene.render.resolution_y = 900
scene.render.filepath = os.path.join(out_dir, f"{tag}_upper.png"); bpy.ops.render.render(write_still=True)
print("RENDERED")
