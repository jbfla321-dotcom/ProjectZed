"""Player base body built from scratch with MPFB (MakeHuman for Blender): a fully naked adult male.

MakeHuman's body model comes from anthropometric data; the build here is set to an average man (~30 years old,
average weight and muscle) and checked against published measurements (printed at the end). Assets are the CC0
MakeHuman packs downloaded to Blender/Sources/MakeHuman and installed in MPFB's user data:
  skin Aksel (mindfront, colour + normal + specular maps), brown eyes, eyebrow001, eyelashes01, teeth, tongue.
Genitals come from MakeHuman's genital fitting helper, smoothed into proper anatomy and skinned to the hips.
Rig: MPFB's mixamo_unity skeleton, clavicles squared up (square_shoulders.py), posed into a T-pose and applied as
the rest pose (what Unity's humanoid setup expects). Output: Blender/Player/BaseMale.blend and Assets/Characters/BaseMale/BaseMale.fbx (+ textures)."""
import bpy, bmesh, math, os, shutil, sys
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from bl_ext.user_default.mpfb.services.humanservice import HumanService

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from body_parts import pad_islands
from square_shoulders import square_shoulders

ASSETS = r"C:/Users/josht/AppData/Roaming/Blender Foundation/Blender/5.2/mpfb/data"
OUT_BLEND = r"C:/Users/josht/My project/Blender/Player/BaseMale.blend"
OUT_DIR = r"C:/Users/josht/My project/Assets/Characters/BaseMale"
FBX = OUT_DIR + "/BaseMale.fbx"

# MakeHuman macros (0..1). Age 0.5 = 25 years, 1.0 = 90, so 0.54 is about 30.
# Weight and muscle are set from a scan against adult male references (US Army ANSUR II 2012, NHANES): this gives
# stature 174.5 cm (ref ~176), bideltoid width 51 cm (ref ~51), hip breadth 36 cm (ref 34-36), chest ~91 cm and
# waist ~78 cm: a fit, lean average man (population means run ~100 / ~94-102 cm, skewed by heavier men).
MACROS = {"gender": 1.0, "age": 0.54, "muscle": 0.72, "weight": 0.72, "proportions": 0.5, "height": 0.5,
          "cupsize": 0.5, "firmness": 0.5, "race": {"asian": 0.0, "caucasian": 1.0, "african": 0.0}}
SKIN = "skins/young_caucasian_male/young_caucasian_male.mhmat"  # clean, average skin (Aksel's has scars and a black eye)
BODYPARTS = [("eyes", "eyes/high-poly/high-poly.mhclo"), ("eyebrows", "eyebrows/eyebrow001/eyebrow001.mhclo"),
             ("eyelashes", "eyelashes/eyelashes01/eyelashes01.mhclo"), ("teeth", "teeth/teeth_base/teeth_base.mhclo"),
             ("tongue", "tongue/tongue01/tongue01.mhclo")]
RIG = "mixamo_unity"
GENITAL_SUBDIV = 2   # Catmull-Clark levels: the helper is a coarse fitting shape
PLATE_GAP = 0.006    # helper faces this close to the body are its flat mounting plate, removed
# Reference: Veale et al. 2015 (BJU International, 15,521 men): mean flaccid length 9.16 cm, girth 9.31 cm
# (diameter ~2.96 cm). The helper is ~9-10 cm long but 2.7 cm across, so it is widened to match.
PENIS_DIAMETER = 0.0296
SCROTUM_DEPTH = 1.2     # the helper's scrotum is shallow (3.7 cm front to back)
RAPHE_DEPTH = 0.004     # the midline crease that divides the scrotum into two lobes
SCROTUM_SCALE = 1.15    # the helper's scrotum is small next to the reference penis size
SCROTUM_LEFT_DROP = 0.006  # the left side hangs slightly lower


def shape_genitals(obj):
    """Penis: round to the reference girth, with the slight narrowing below the glans and a fuller glans.
    Scrotum: deeper, with a midline crease."""
    import numpy as np
    bm = bmesh.new(); bm.from_mesh(obj.data); bm.verts.ensure_lookup_table(); bm.normal_update()
    islands, seen = [], set()
    for v in bm.verts:
        if v in seen: continue
        isl, todo = [], [v]; seen.add(v)
        while todo:
            a = todo.pop(); isl.append(a)
            for e in a.link_edges:
                b = e.other_vert(a)
                if b not in seen: seen.add(b); todo.append(b)
        islands.append(isl)
    width = lambda isl: max(v.co.x for v in isl) - min(v.co.x for v in isl)
    penis = min(islands, key=width); scrotum = max(islands, key=width)
    # Penis axis from its shape; t runs 0 at the root (top) to 1 at the tip.
    pts = np.array([v.co[:] for v in penis]); centre = pts.mean(axis=0)
    axis = Vector(np.linalg.svd(pts - centre)[2][0])
    if axis.z > 0: axis = -axis
    c = Vector(centre)
    proj = [(v.co - c).dot(axis) for v in penis]; lo, hi = min(proj), max(proj)
    radii = [((v.co - c) - axis * a).length for v, a in zip(penis, proj) if 0.3 < (a - lo) / (hi - lo) < 0.6]
    widen = (PENIS_DIAMETER / 2) / (sum(radii) / len(radii))
    for v, a in zip(penis, proj):
        t = (a - lo) / (hi - lo)
        # Smooth bumps, not steps: a shallow groove (coronal sulcus) just below a slightly fuller glans.
        profile = 1 - 0.08 * math.exp(-((t - 0.76) / 0.03) ** 2) + 0.07 * math.exp(-((t - 0.85) / 0.05) ** 2)
        radial = (v.co - c) - axis * a
        v.co = c + axis * a + radial * widen * profile
        v.co.x -= c.x  # centred on the body's midline
    print(f"penis: length {(hi - lo) * 100:.1f} cm, widened x{widen:.2f} to {PENIS_DIAMETER * 100:.2f} cm across")
    # Scrotum: fuller and deeper, hanging a little lower on the left (as is typical), with a midline crease.
    sc = sum((v.co for v in scrotum), Vector()) / len(scrotum)
    top = max(v.co.z for v in scrotum)
    for v in scrotum:
        v.co.y = sc.y + (v.co.y - sc.y) * SCROTUM_DEPTH
        v.co = Vector((sc.x, sc.y, top)) + (v.co - Vector((sc.x, sc.y, top))) * SCROTUM_SCALE
        if v.co.x > sc.x: v.co.z -= SCROTUM_LEFT_DROP * min(1.0, (v.co.x - sc.x) / 0.012) * (top - v.co.z) / 0.06
        v.co.x -= sc.x  # centred on the body's midline, behind the penis
    sc.x = 0.0
    bm.normal_update()
    for v in scrotum:
        v.co -= v.normal * RAPHE_DEPTH * math.exp(-((v.co.x - sc.x) / 0.006) ** 2)
    bm.to_mesh(obj.data); bm.free()


def apply_all_modifiers(obj, types):
    for m in list(obj.modifiers):
        if m.type in types:
            with bpy.context.temp_override(object=obj, active_object=obj):
                bpy.ops.object.modifier_apply(modifier=m.name)


bpy.ops.wm.read_factory_settings(use_empty=True)

# ---------- 1. Body ----------
body = HumanService.create_human(mask_helpers=True, detailed_helpers=True, extra_vertex_groups=True,
                                 feet_on_ground=True, scale=0.1, macro_detail_dict=MACROS)
with bpy.context.temp_override(object=body, active_object=body):
    bpy.ops.object.shape_key_remove(all=True, apply_mix=True)  # bake the build into the mesh
body.name = "BaseMale_Body"

# ---------- 2. Rig, then the body parts (fitted to the helpers, and rigged) and the skin ----------
rig = HumanService.add_builtin_rig(body, RIG, import_weights=True)
rig.name = "BaseMale_Rig"
for kind, path in BODYPARTS:
    HumanService.add_mhclo_asset(os.path.join(ASSETS, path), body, asset_type=kind, subdiv_levels=0,
                                 material_type="MAKESKIN")
HumanService.set_character_skin(os.path.join(ASSETS, SKIN), body, skin_type="GAMEENGINE", material_instances=False)

# ---------- 3. Genitals from the fitting helper (before the helpers are deleted) ----------
gen = body.copy(); gen.data = body.data.copy(); bpy.context.scene.collection.objects.link(gen)
gen.name = gen.data.name = "BaseMale_Genitals"
gen.modifiers.clear()
genital_group = body.vertex_groups["helper-genital"].index
helper_groups = {g.index for g in body.vertex_groups if g.name.lower().startswith(("helper", "joint"))}
bm = bmesh.new(); bm.from_mesh(gen.data); deform = bm.verts.layers.deform.active
bmesh.ops.delete(bm, geom=[v for v in bm.verts if v[deform].get(genital_group, 0) < 0.5], context='VERTS')
# The helper sits on a flat plate lying on the pubis: remove the faces that hug the body surface.
sb = bmesh.new(); sb.from_mesh(body.data); sdeform = sb.verts.layers.deform.active
bmesh.ops.delete(sb, geom=[v for v in sb.verts if any(i in helper_groups for i in v[sdeform].keys())], context='VERTS')
skin_surface = BVHTree.FromBMesh(sb); sb.free()
plate = [f for f in bm.faces if all(skin_surface.find_nearest(v.co)[3] < PLATE_GAP for v in f.verts)]
bmesh.ops.delete(bm, geom=plate, context='FACES')
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
bm.to_mesh(gen.data); bm.free()
gen.vertex_groups.clear()
sub = gen.modifiers.new("Smooth", 'SUBSURF'); sub.levels = sub.render_levels = GENITAL_SUBDIV
apply_all_modifiers(gen, {'SUBSURF'})
shape_genitals(gen)
for p in gen.data.polygons: p.use_smooth = True
print(f"genitals: removed {len(plate)} plate faces, {len(gen.data.vertices)} verts after smoothing")

# ---------- 4. Remove the fitting helpers and joint cubes from the body ----------
for m in [m for m in body.modifiers if m.type == 'MASK']: body.modifiers.remove(m)
bm = bmesh.new(); bm.from_mesh(body.data); deform = bm.verts.layers.deform.active
bmesh.ops.delete(bm, geom=[v for v in bm.verts if any(i in helper_groups for i in v[deform].keys())], context='VERTS')
bm.to_mesh(body.data); bm.free()
for g in [g for g in body.vertex_groups if g.index in helper_groups]: body.vertex_groups.remove(g)
for p in body.data.polygons: p.use_smooth = True

# Genitals ride on the pelvis.
hips = next(b.name for b in rig.data.bones if b.name.lower().endswith("hips"))
gen.parent = rig; gen.matrix_parent_inverse = rig.matrix_world.inverted()
gen.vertex_groups.new(name=hips).add(list(range(len(gen.data.vertices))), 1.0, 'REPLACE')
gen.modifiers.new("Armature", 'ARMATURE').object = rig
gen.data.materials.clear(); gen.data.materials.append(body.data.materials[0])
# Same skin texture as the body: take the UVs of the nearest body surface, so the skin continues across the root.
dt = gen.modifiers.new("SkinUV", 'DATA_TRANSFER'); dt.object = body
dt.use_loop_data = True; dt.data_types_loops = {'UV'}; dt.loop_mapping = 'POLYINTERP_NEAREST'
dt.layers_uv_select_src = 'ALL'; dt.layers_uv_select_dst = 'NAME'
with bpy.context.temp_override(object=gen, active_object=gen):
    bpy.ops.object.datalayout_transfer(modifier=dt.name)
gen.modifiers.move(gen.modifiers.find(dt.name), 0)
apply_all_modifiers(gen, {'DATA_TRANSFER'})

meshes = [o for o in bpy.data.objects if o.type == 'MESH']
print("objects:", [(o.name, len(o.data.vertices), [m.name for m in o.data.materials if m]) for o in meshes])
print("rig bones:", len(rig.data.bones), [b.name for b in rig.data.bones][:8])

# ---------- 6. T-pose as the rest pose: arms straight out to the sides ----------
bpy.context.view_layer.objects.active = rig
for o in bpy.context.view_layer.objects: o.select_set(o == rig)
bpy.ops.object.mode_set(mode='POSE')
square_shoulders(rig)  # MakeHuman's clavicles slope down and forward, which reads as slouched in game
inv = rig.matrix_world.inverted()
for side, out_dir in (("Left", Vector((1, 0, 0))), ("Right", Vector((-1, 0, 0)))):
    for name in ("Arm", "ForeArm", "Hand"):
        pb = rig.pose.bones[f"mixamorig:{side}{name}"]
        bpy.context.view_layer.update()
        head = rig.matrix_world @ pb.head; tail = rig.matrix_world @ pb.tail
        q = (tail - head).normalized().rotation_difference(out_dir)
        pivot = inv @ head
        pb.matrix = Matrix.Translation(pivot) @ (inv.to_3x3() @ q.to_matrix() @ rig.matrix_world.to_3x3()).to_4x4() @ Matrix.Translation(-pivot) @ pb.matrix
bpy.context.view_layer.update()
bpy.ops.object.mode_set(mode='OBJECT')
skinned = [o for o in bpy.data.objects if o.type == 'MESH' and any(m.type == 'ARMATURE' for m in o.modifiers)]
for o in skinned:
    apply_all_modifiers(o, {'ARMATURE'})
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode='POSE'); bpy.ops.pose.armature_apply(selected=False); bpy.ops.object.mode_set(mode='OBJECT')
for o in skinned:
    o.modifiers.new("Armature", 'ARMATURE').object = rig
print("T-pose applied to", [o.name for o in skinned])

bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND)
print("SAVED", OUT_BLEND)

# ---------- 7. FBX and textures for Unity ----------
tex_dir = os.path.join(OUT_DIR, "Textures"); os.makedirs(tex_dir, exist_ok=True)
for mat in bpy.data.materials:
    if not mat.use_nodes: continue
    for n in mat.node_tree.nodes:
        if n.type == 'TEX_IMAGE' and n.image and n.image.filepath:
            src = bpy.path.abspath(n.image.filepath)
            if os.path.exists(src): shutil.copy2(src, os.path.join(tex_dir, os.path.basename(src)))


pad_islands(os.path.join(tex_dir, "young_lightskinned_male_diffuse.png"), body)
print("textures:", sorted(os.listdir(tex_dir)))
for o in bpy.context.view_layer.objects: o.select_set(o == rig or o in skinned)
bpy.context.view_layer.objects.active = rig
bpy.ops.export_scene.fbx(filepath=FBX, use_selection=True, object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                         bake_anim=False, use_armature_deform_only=False, mesh_smooth_type='FACE',
                         apply_scale_options='FBX_SCALE_ALL', path_mode='STRIP', embed_textures=False)
print("EXPORTED", FBX)
