"""Probe: an MPFB adult male with the genital helper kept, measurements printed, crotch rendered."""
import bpy, bmesh, math, sys, os
from mathutils import Vector
from bl_ext.user_default.mpfb.services.humanservice import HumanService
from bl_ext.user_default.mpfb.services.targetservice import TargetService

out = sys.argv[sys.argv.index("--") + 1]
for o in list(bpy.data.objects): bpy.data.objects.remove(o, do_unlink=True)
macros = {"gender": 1.0, "age": 0.54, "muscle": 0.55, "weight": 0.5, "proportions": 0.5, "height": 0.5,
          "cupsize": 0.5, "firmness": 0.5, "race": {"asian": 0.0, "caucasian": 1.0, "african": 0.0}}
body = HumanService.create_human(mask_helpers=False, detailed_helpers=True, extra_vertex_groups=True,
                                 feet_on_ground=True, scale=0.1, macro_detail_dict=macros)
for t in ("genitals/penis-length-incr", "genitals/penis-circ-incr", "genitals/penis-testicles-incr"):
    TargetService.set_target_value(body, t.split("/")[-1], 0.0) if False else None
print("shape keys:", [k.name for k in body.data.shape_keys.key_blocks][:12] if body.data.shape_keys else None)
with bpy.context.temp_override(object=body, active_object=body):
    bpy.ops.object.shape_key_remove(all=True, apply_mix=True)

mw = body.matrix_world
groups = {g.name: g.index for g in body.vertex_groups}
body_only = [v for v in body.data.vertices if not any(body.vertex_groups[g.group].name.startswith(("helper", "joint", "Helper", "Joint")) for g in v.groups)]
genital = [v for v in body.data.vertices if any(g.group == groups["helper-genital"] and g.weight > 0.5 for g in v.groups)]
pts = [mw @ v.co for v in body_only]
zmin = min(p.z for p in pts); zmax = max(p.z for p in pts)
print(f"stature {(zmax - zmin) * 100:.1f} cm, body verts {len(body_only)}, genital helper verts {len(genital)}")
gp = [mw @ v.co for v in genital]
if gp:
    lo = Vector([min(p[i] for p in gp) for i in range(3)]); hi = Vector([max(p[i] for p in gp) for i in range(3)])
    print("genital helper bounds", tuple(round(c, 3) for c in lo), tuple(round(c, 3) for c in hi))
    # Is it attached to the body surface (shared edges) or a separate shell?
    gi = {v.index for v in genital}
    shared = sum(1 for e in body.data.edges if (e.vertices[0] in gi) != (e.vertices[1] in gi))
    print("edges linking helper to the rest:", shared)

# Hide every helper except the genital one, for the render.
bm = bmesh.new(); bm.from_mesh(body.data)
deform = bm.verts.layers.deform.active
drop = [v for v in bm.verts if any(body.vertex_groups[i].name.startswith(("helper", "joint")) and body.vertex_groups[i].name != "helper-genital" and w > 0.5 for i, w in v[deform].items())]
bmesh.ops.delete(bm, geom=drop, context='VERTS'); bm.to_mesh(body.data); bm.free()

s = bpy.context.scene
s.render.engine = 'BLENDER_WORKBENCH'; s.display.shading.light = 'STUDIO'
s.render.resolution_x = s.render.resolution_y = 700
cd = bpy.data.cameras.new("C"); cd.lens = 85
cam = bpy.data.objects.new("C", cd); s.collection.objects.link(cam); s.camera = cam
crotch = Vector((0, 0, zmin + (zmax - zmin) * 0.48))
for name, d, dist in (("crotch_front", Vector((0.2, -1, 0)), 1.2), ("crotch_side", Vector((1, -0.3, 0)), 1.2), ("full", Vector((0.3, -1, 0.1)), 6.5)):
    target = crotch if name != "full" else Vector((0, 0, (zmin + zmax) / 2))
    cam.location = target + d.normalized() * dist
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    s.render.filepath = os.path.join(out, name + ".png"); bpy.ops.render.render(write_still=True)
