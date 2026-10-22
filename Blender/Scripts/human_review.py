"""Review renders and measurements of BaseMale.blend (Eevee, soft studio light).
Usage: blender -b BaseMale.blend --python human_review.py -- <out_dir>"""
import bpy, math, sys, os
from mathutils import Vector

out = sys.argv[sys.argv.index("--") + 1]
body = bpy.data.objects["BaseMale_Body"]
dg = bpy.context.evaluated_depsgraph_get()
pts = [body.matrix_world @ v.co for v in body.evaluated_get(dg).to_mesh().vertices]
zmin, zmax = min(p.z for p in pts), max(p.z for p in pts)
H = zmax - zmin


def width_at(z, tol=0.01, xlim=None):
    band = [p for p in pts if abs(p.z - z) < tol and (xlim is None or abs(p.x) < xlim)]
    return (max(p.x for p in band) - min(p.x for p in band)) if band else 0


def girth_at(z, xlim):
    """Approximate circumference: perimeter of the 2D convex hull of the body slice."""
    band = sorted({(round(p.x, 3), round(p.y, 3)) for p in pts if abs(p.z - z) < 0.006 and abs(p.x) < xlim})
    if len(band) < 3: return 0
    def cross(o, a, b): return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in band:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0: lower.pop()
        lower.append(p)
    for p in reversed(band):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0: upper.pop()
        upper.append(p)
    hull = lower[:-1] + upper[:-1]
    return sum(math.dist(hull[i], hull[i - 1]) for i in range(len(hull)))


rig = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
bone = lambda n: rig.matrix_world @ rig.data.bones["mixamorig:" + n].head_local
sh_l, sh_r = bone("LeftArm"), bone("RightArm")
head_pts = [p for p in pts if p.z > bone("Neck").z + 0.05]
chin_z = min(p.z for p in pts if p.z > bone("Neck").z and p.y < bone("Head").y - 0.06 and abs(p.x) < 0.02)
print(f"MEASURE stature {H*100:.1f} cm | shoulder joints {abs(sh_l.x - sh_r.x)*100:.1f} cm (biacromial ~ joints + 2-3 cm)"
      f" | head height (crown-chin) {(zmax - chin_z)*100:.1f} cm | head breadth {width_at(bone('Head').z + 0.08, 0.02, 0.12)*100:.1f} cm")
hz = bone("Hips").z
print(f"MEASURE hip breadth {width_at(hz - 0.06, 0.015, 0.25)*100:.1f} cm | waist girth {girth_at(hz + 0.13, 0.22)*100:.1f} cm"
      f" | chest girth {girth_at(bone('Spine2').z, 0.2)*100:.1f} cm | crotch height {min(p.z for p in pts if abs(p.x) < 0.01 and p.z < hz)*100:.1f} cm"
      f" | head/stature {H / (zmax - chin_z):.2f} heads")

s = bpy.context.scene
s.render.engine = 'BLENDER_EEVEE' if 'BLENDER_EEVEE' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE_NEXT'
s.render.resolution_x, s.render.resolution_y = 700, 1000
w = bpy.data.worlds.new("W"); s.world = w; w.use_nodes = True
w.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.58, 0.62, 1); w.node_tree.nodes["Background"].inputs[1].default_value = 0.7
for rot, energy in (((50, 0, 30), 3.0), ((60, 0, 200), 1.2)):
    l = bpy.data.objects.new("L", bpy.data.lights.new("L", 'SUN')); l.data.energy = energy; s.collection.objects.link(l)
    l.rotation_euler = tuple(math.radians(a) for a in rot)
cd = bpy.data.cameras.new("C"); cd.lens = 70
cam = bpy.data.objects.new("C", cd); s.collection.objects.link(cam); s.camera = cam


def shot(name, target, direction, dist, res=(700, 1000)):
    s.render.resolution_x, s.render.resolution_y = res
    cam.location = target + direction.normalized() * dist
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    s.render.filepath = os.path.join(out, name + ".png"); bpy.ops.render.render(write_still=True)


mid = Vector((0, 0, zmin + H / 2))
shot("full_front", mid, Vector((0, -1, 0)), 6.0)
shot("full_side", mid, Vector((1, 0, 0)), 6.0)
shot("full_back", mid, Vector((0, 1, 0)), 6.0)
shot("face", bone("Head") + Vector((0, 0, 0.06)), Vector((0.35, -1, 0.05)), 1.1, (800, 800))
shot("torso", bone("Spine1"), Vector((0.3, -1, 0)), 2.6, (800, 800))
shot("groin", bone("Hips") - Vector((0, 0, 0.1)), Vector((0.25, -1, 0)), 1.3, (800, 800))
