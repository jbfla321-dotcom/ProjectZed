import bpy, bmesh, sys
from mathutils import Matrix, Vector

SRC = r"C:/Users/josht/My project/Blender/Player/Ch17_Player.blend"
OUT = r"C:/Users/josht/My project/Blender/Player/Ch17_Base_v3.blend"
bpy.ops.wm.open_mainfile(filepath=SRC)

from bl_ext.user_default.mpfb.services.humanservice import HumanService

ch_arm = bpy.data.objects["Armature"]

# ---------- 1. Generate an MPFB male body with the Mixamo rig ----------
macros = {"gender": 1.0, "age": 0.5, "muscle": 0.6, "weight": 0.5, "proportions": 0.6, "height": 0.6,
          "cupsize": 0.5, "firmness": 0.5, "race": {"asian": 0.1, "caucasian": 0.5, "african": 0.4}}
body = HumanService.create_human(mask_helpers=True, detailed_helpers=False, extra_vertex_groups=False,
                                 feet_on_ground=True, scale=0.1, macro_detail_dict=macros)
rig = HumanService.add_builtin_rig(body, "mixamo", import_weights=True)
print("MPFB body", body.name, "rig", rig.name, "bones", len(rig.data.bones))

# Bake the body shape (macros) so vertex positions are final.
bpy.context.view_layer.objects.active = body
with bpy.context.temp_override(object=body, active_object=body):
    bpy.ops.object.shape_key_remove(all=True, apply_mix=True)

# MPFB's mixamo rig comes in a T-pose rest while its mesh is in an A-pose, so posing it barely moves the arms.
# Re-seat the rig's rest on the mesh: each joint is where its two bones' skin weights overlap.
def depth(b):
    d = 0
    while b.parent: d += 1; b = b.parent
    return d

gidx = {vg.name: vg.index for vg in body.vertex_groups}
vw = []  # per vertex {group index: weight}
for v in body.data.vertices:
    vw.append({g.group: g.weight for g in v.groups})
coords = [body.matrix_world @ v.co for v in body.data.vertices]

def joint(parent_name, child_name):
    gp, gc = gidx.get(parent_name), gidx.get(child_name)
    if gp is None or gc is None: return None
    acc = Vector(); tot = 0.0
    for i, w in enumerate(vw):
        k = w.get(gp, 0.0) * w.get(gc, 0.0)
        if k > 0: acc += coords[i] * k; tot += k
    return acc / tot if tot > 1e-4 else None

rig_inv0 = rig.matrix_world.inverted()
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode='EDIT')
ebs = rig.data.edit_bones
heads = {}
for eb in sorted(ebs, key=lambda e: depth(rig.data.bones[e.name])):
    if eb.parent is None: continue
    j = joint(eb.parent.name, eb.name)
    if j is not None: heads[eb.name] = rig_inv0 @ j
for eb in sorted(ebs, key=lambda e: depth(rig.data.bones[e.name])):
    if eb.name in heads:
        offset = eb.tail - eb.head
        eb.use_connect = False
        eb.head = heads[eb.name]
        eb.tail = eb.head + offset
for eb in ebs:
    kids = [c for c in eb.children if c.name in heads]
    if len(kids) == 1:  # a chain: point at the child's joint
        eb.tail = heads[kids[0].name]
bpy.ops.object.mode_set(mode='OBJECT')
print("re-seated joints:", len(heads))

# ---------- 2. Pose the MPFB rig onto Ch17's skeleton (T-pose, matching joints) ----------
def ch_name(n): return n.replace("mixamorig:", "mixamorig1:")

bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode='POSE')
rig_inv = rig.matrix_world.inverted()
for pb in rig.pose.bones:
    pb.bone.inherit_scale = 'NONE'

missing = []
for bone in sorted(rig.data.bones, key=depth):
    target = ch_arm.data.bones.get(ch_name(bone.name))
    if target is None:
        missing.append(bone.name); continue
    head = rig_inv @ (ch_arm.matrix_world @ target.head_local)
    tail = rig_inv @ (ch_arm.matrix_world @ target.tail_local)
    rest = bone.matrix_local.to_3x3().normalized()
    rest_y = rest.col[1]
    want_y = (tail - head)
    stretch = want_y.length / max(bone.length, 1e-6)
    rot = rest_y.rotation_difference(want_y.normalized()).to_matrix()
    m3 = rot @ rest @ Matrix.Diagonal((1.0, stretch, 1.0))
    m = m3.to_4x4()
    m.translation = head
    pb = rig.pose.bones[bone.name]
    pb.matrix = m
    bpy.context.view_layer.update()
print("not on Ch17:", missing)
bpy.ops.object.mode_set(mode='OBJECT')

# Bake the posed shape into the mesh (shape keys first, then the helper mask and the armature).
bpy.context.view_layer.objects.active = body
body.select_set(True)
if body.data.shape_keys:
    with bpy.context.temp_override(object=body, active_object=body):
        bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
for mod in list(body.modifiers):
    with bpy.context.temp_override(object=body, active_object=body):
        bpy.ops.object.modifier_apply(modifier=mod.name)
mw = body.matrix_world.copy()
body.parent = None
body.matrix_world = mw
bpy.data.objects.remove(rig, do_unlink=True)

# Weights: MPFB's mixamo groups -> Ch17's bone names; drop everything that isn't a bone.
bone_names = {b.name for b in ch_arm.data.bones}
for vg in list(body.vertex_groups):
    new = ch_name(vg.name)
    if new in bone_names: vg.name = new
    else: body.vertex_groups.remove(vg)
# Each vertex's weights sum to 1 (MPFB's don't always; Unity expects it).
for v in body.data.vertices:
    tot = sum(g.weight for g in v.groups)
    if tot > 0:
        for g in v.groups: g.weight /= tot

# ---------- 3. Cut away the MPFB head and hands (Ch17's own head and hands stay) ----------
import math
sys.path.append(r"C:/Users/josht/My project/Blender/Scripts")
import body_parts, fit_sculpt
ch_body = bpy.data.objects["Ch17_Body"]
rings, neck_center = body_parts.head_and_hand_rings(ch_body, ch_arm)
body_parts.cut_at_head_and_hands(body, rings, ch_body)

# ---------- 3b. Shape: project the body onto BlendSwap's Human Male Base Mesh (asset 21626) ----------
hips = ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:Hips"].head_local
fit_sculpt.fit(body, ch_arm, protect_z=(hips.z - 0.17, hips.z + 0.11), protect_y=hips.y)

body.name = body.data.name = "Base_Body"
body.parent = ch_arm
body.matrix_parent_inverse = ch_arm.matrix_world.inverted()
arm_mod = body.modifiers.new("Armature", 'ARMATURE'); arm_mod.object = ch_arm

# ---------- 4. Underwear, 5. Materials ----------
under = body_parts.make_underwear(body, ch_arm)
skin, cloth = body_parts.make_materials(ch_body, neck_center.z)
body.data.materials.clear(); body.data.materials.append(skin)
under.data.materials.clear(); under.data.materials.append(cloth)
# ---------- 6. Put the construction outfit away (kept, hidden) ----------
outfit = bpy.data.collections.new("Original Outfit (hidden)")
bpy.context.scene.collection.children.link(outfit)
for name in ["Ch17_Helmet", "Ch17_Vest", "Ch17_Shirt", "Ch17_Pants", "Ch17_Boots"]:
    o = bpy.data.objects[name]
    for c in list(o.users_collection): c.objects.unlink(o)
    outfit.objects.link(o)
bpy.context.view_layer.layer_collection.children[outfit.name].exclude = True

print("verts body", len(body.data.vertices), "underwear", len(under.data.vertices))
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print("SAVED", OUT)
