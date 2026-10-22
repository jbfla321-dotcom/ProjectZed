"""Game base body built from BlendSwap asset 21626 (Human Male Base Mesh, CC0, by nashnir) itself.

Run after make_base.py, which leaves the sculpt lined up on Ch17's skeleton and an MPFB body fitted to it. Here the
sculpt is remeshed to a light quad mesh, cut at Ch17's neck and wrists (his head and hands stay), skinned with the
MPFB body's Mixamo weights, UV-unwrapped, given underwear, and exported for Unity."""
import bpy, sys
from mathutils import Vector

SCRIPTS = r"C:/Users/josht/My project/Blender/Scripts"
IN = r"C:/Users/josht/My project/Blender/Player/Ch17_Base_v3.blend"
OUT = r"C:/Users/josht/My project/Blender/Player/Ch17_Base_v4.blend"
FBX = r"C:/Users/josht/My project/Assets/Characters/BaseBody/Ch17_BaseBody.fbx"
VOXEL_SIZES = (0.008, 0.01, 0.012, 0.015, 0.02)  # voxel remesh tried before retopology, finest first
TARGET_FACES = 14000   # quads in the game mesh (head and hands already cut away)
FALLBACK_VOXEL = 0.011  # plain voxel remesh used if QuadriFlow refuses them all
MAX_INFLUENCES = 4     # bones per vertex (Unity's default skin weights setting)
# The sculpt is a bodybuilder; next to Ch17's head and hands he looked swollen. Each part is pulled toward its bone
# by this factor (left and right alike), blended by the skin weights.
# A pair (side to side, front to back) narrows the torso more than it flattens it: the sculpt's chest and lats made
# a bodybuilder's V (1.37x his waist), an ordinary man is nearer 1.15-1.2.
SLIM = {"Arm": 0.74, "ForeArm": 0.78, "Shoulder": 0.78, "Spine": (0.93, 0.96), "Spine1": (0.78, 0.92),
        "Spine2": (0.76, 0.9), "UpLeg": 0.93, "Leg": 0.95}
# The lats under the arms follow the collarbones, so the slimming above pulls them up rather than in. Narrow the torso
# side to side by this factor at chest height, fading out toward the waist and the tops of the shoulders.
LAT_NARROW = 0.8
# Below the groin, this much of the hips' weight moves to the thigh, so the underwear's leg bands wrap the thigh when
# it lifts instead of hanging like a skirt (and the inner thigh doesn't show under them).
THIGH_TAKEOVER = 0.85
SEAM_BLEND = 0.05  # how far from the neck and wrist edges the body eases from Ch17's weights back to its own
# Armpits: within this distance of each shoulder joint the skin weights are smoothed, so the chest, back and arm blend
# gradually and the pec isn't dragged into a sharp fold when the arms come down from the T-pose.
ARMPIT_RADIUS = 0.17
ARMPIT_SMOOTH_ITERATIONS = 25

sys.path.append(SCRIPTS)
import body_parts
from fit_sculpt import smoothstep

bpy.ops.wm.open_mainfile(filepath=IN)
ch_arm = bpy.data.objects["Armature"]
ch_body = bpy.data.objects["Ch17_Body"]
mpfb = bpy.data.objects["Base_Body"]
sculpt = bpy.data.objects["Sculpt_21626"]
hips = ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:Hips"].head_local


def apply(obj, mod):
    with bpy.context.temp_override(object=obj, active_object=obj):
        bpy.ops.object.modifier_apply(modifier=mod.name)


# ---------- 1. Retopologise the sculpt: voxel remesh (watertight), then QuadriFlow quads ----------
game = sculpt.copy(); game.data = sculpt.data.copy()
bpy.context.scene.collection.objects.link(game)
game.hide_set(False); game.hide_render = False
mpfb.name = mpfb.data.name = "Base_Body_MPFB"
game.name = game.data.name = "Base_Body"
for o in bpy.context.view_layer.objects: o.select_set(o == game)
bpy.context.view_layer.objects.active = game
# Cut the head and hands off first: the fingers make QuadriFlow reject fine voxel remeshes, and a coarse one fuses
# the inner thighs into a web of skin. (The voxel remesh closes the cut openings; they're cut again in step 5.)
rings, neck_center = body_parts.head_and_hand_rings(ch_body, ch_arm)
body_parts.cut_at_head_and_hands(game, rings)
import bmesh
bm = bmesh.new(); bm.from_mesh(game.data)  # close the cut openings so the voxel remesh can tell inside from out
bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)
bm.to_mesh(game.data); bm.free()
# If QuadriFlow still rejects a voxel size, go coarser. The voxel size only guides the new topology: detail comes
# back from the sculpt next.
source = game.data
for vs in VOXEL_SIZES:
    game.data = source.copy(); game.data.remesh_voxel_size = vs
    bpy.ops.object.voxel_remesh()
    if bpy.ops.object.quadriflow_remesh(target_faces=TARGET_FACES, use_mesh_symmetry=True, use_preserve_sharp=False,
                                        use_preserve_boundary=False, smooth_normals=False, mode='FACES') == {'FINISHED'}:
        break
else:
    # QuadriFlow's manifold check is unreliable on this sculpt; an even voxel grid of quads is a fine game mesh too.
    vs = FALLBACK_VOXEL
    game.data = source.copy(); game.data.remesh_voxel_size = vs
    bpy.ops.object.voxel_remesh()
    print("QuadriFlow refused every voxel size; using the plain voxel remesh")
game.data.name = "Base_Body"
# With symmetry on, QuadriFlow builds one half and mirrors it, leaving the halves unjoined down the middle.
import bmesh
bm = bmesh.new(); bm.from_mesh(game.data)
welded = len(bm.verts); bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0005); welded -= len(bm.verts)
bm.to_mesh(game.data); bm.free()
print(f"quadriflow from {vs} m voxels:", len(game.data.polygons), "faces; welded", welded, "verts at the centre")

# Pull the quads back onto the sculpt's surface for its detail.
sw = game.modifiers.new("Detail", 'SHRINKWRAP'); sw.target = sculpt; sw.wrap_method = 'NEAREST_SURFACEPOINT'
apply(game, sw)
# That also picks up the sculpt's fine surface noise, which game lighting showed as dark streaks down his back.
# A gentle smooth irons it out and keeps the muscle shapes.
sm = game.modifiers.new("Iron", 'LAPLACIANSMOOTH'); sm.lambda_factor = 0.5; sm.iterations = 4; sm.use_volume_preserve = True
apply(game, sm)

# ---------- 2. Shoulders and crotch ----------
# Shoulders: the sculpt creases where its arms were raised from an A-pose, so smooth them.
# Front of the crotch: take the MPFB body's smooth shape there (keeps the underwear clean; no anatomy under it).
shoulders = [ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:" + n].head_local for n in ("LeftArm", "RightArm")]
lo, hi = hips.z - 0.21, hips.z + 0.11
g_sh = game.vertex_groups.new(name="Shoulder_Smooth"); g_cr = game.vertex_groups.new(name="Crotch")
for v in game.data.vertices:
    p = game.matrix_world @ v.co
    d = min((p - s).length for s in shoulders)
    g_sh.add([v.index], 1 - smoothstep((d - 0.06) / 0.10), 'REPLACE')
    inside = min((p.z - lo) / 0.04, (hi - p.z) / 0.04, (0.12 - abs(p.x)) / 0.04, (hips.y - p.y) / 0.03)
    g_cr.add([v.index], smoothstep(inside), 'REPLACE')
# Nearest point, not projection: the anatomy's normals point sideways and down, away from the MPFB surface.
# Snapping piles vertices on top of each other, so relax and re-snap a few times to spread them over the surface.
for _ in range(4):
    cr = game.modifiers.new("Crotch", 'SHRINKWRAP'); cr.target = mpfb; cr.wrap_method = 'NEAREST_SURFACEPOINT'
    cr.vertex_group = "Crotch"
    apply(game, cr)
    rx = game.modifiers.new("Relax", 'SMOOTH'); rx.factor = 1.0; rx.iterations = 10; rx.vertex_group = "Crotch"
    apply(game, rx)
cr = game.modifiers.new("Crotch", 'SHRINKWRAP'); cr.target = mpfb; cr.wrap_method = 'NEAREST_SURFACEPOINT'
cr.vertex_group = "Crotch"
apply(game, cr)
for group, lam, its in (("Shoulder_Smooth", 0.6, 10), ("Crotch", 0.5, 4)):
    sm = game.modifiers.new("Smooth", 'LAPLACIANSMOOTH'); sm.lambda_factor = lam; sm.iterations = its
    sm.use_volume_preserve = True; sm.vertex_group = group
    apply(game, sm)
for name in ("Shoulder_Smooth", "Crotch"): game.vertex_groups.remove(game.vertex_groups[name])

# ---------- 3. Skin weights: Mixamo weights from the MPFB body fitted to the same shape ----------
game.vertex_groups.clear()
dt = game.modifiers.new("Weights", 'DATA_TRANSFER'); dt.object = mpfb
dt.use_vert_data = True; dt.data_types_verts = {'VGROUP_WEIGHTS'}; dt.vert_mapping = 'POLYINTERP_NEAREST'
dt.layers_vgroup_select_src = 'ALL'; dt.layers_vgroup_select_dst = 'NAME'
with bpy.context.temp_override(object=game, active_object=game):
    bpy.ops.object.datalayout_transfer(modifier=dt.name)
apply(game, dt)

# ---------- 4. Slim: pull each part toward the bones it follows ----------
segments = {}
for b in ch_arm.data.bones:
    part = b.name.split(":")[-1].replace("Left", "").replace("Right", "")
    if part in SLIM:
        segments[b.name] = (ch_arm.matrix_world @ b.head_local, ch_arm.matrix_world @ b.tail_local, SLIM[part])
group_names = {vg.index: vg.name for vg in game.vertex_groups}
for v in game.data.vertices:
    p = game.matrix_world @ v.co
    moved, total = Vector(), 0.0
    for g in v.groups:
        q = p
        if group_names[g.group] in segments:
            a, b, s = segments[group_names[g.group]]
            t = min(1.0, max(0.0, (p - a).dot(b - a) / (b - a).length_squared))
            c = a + (b - a) * t
            if isinstance(s, tuple):  # spine bones run vertically: scale across and front-to-back separately
                q = c + Vector(((p - c).x * s[0], (p - c).y * s[1], (p - c).z))
            else:
                q = c + (p - c) * s
        moved += q * g.weight; total += g.weight
    if total > 0: v.co = game.matrix_world.inverted() @ (moved / total)

# Chest and lats: narrow toward the centre line, by how much each vertex follows the torso (so the arms stay put).
torso_bones = {g.index for g in game.vertex_groups
               if g.name.split(":")[-1].replace("Left", "").replace("Right", "") in ("Spine", "Spine1", "Spine2", "Shoulder")}
low = hips.z + 0.22                                                            # lower ribs
high = (ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:LeftArm"].head_local).z - 0.05  # just under the shoulders
for v in game.data.vertices:
    total = sum(g.weight for g in v.groups)
    if total <= 0: continue
    follows = sum(g.weight for g in v.groups if g.group in torso_bones) / total
    p = game.matrix_world @ v.co
    band = smoothstep((p.z - low) / 0.08) * (1 - smoothstep((p.z - high) / 0.06))
    p.x *= 1 - (1 - LAT_NARROW) * band * follows
    v.co = game.matrix_world.inverted() @ p

# ---------- 5. Cut at Ch17's neck and wrists ----------
body_parts.cut_at_head_and_hands(game, rings, ch_body)
# Drop any small loose bits left over by the cut.
bm = bmesh.new(); bm.from_mesh(game.data)
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
small = [isl for isl in islands if len(isl) < 200]
bmesh.ops.delete(bm, geom=[v for isl in small for v in isl], context='VERTS')
print(f"islands: {len(islands)}, removed {len(small)} small, verts kept {len(bm.verts)}")
bm.to_mesh(game.data); bm.free()

# Seams: near the neck and wrist edges take Ch17's own weights, easing back over SEAM_BLEND, so the body and his
# head and hands bend as one and the edge stays tucked inside his skin in any pose.
from mathutils import kdtree
edge_count = {}
for poly in game.data.polygons:
    for k in poly.edge_keys: edge_count[k] = edge_count.get(k, 0) + 1
seam = {i for k, n in edge_count.items() if n == 1 for i in k}
tree = kdtree.KDTree(len(seam))
for i in seam: tree.insert(game.matrix_world @ game.data.vertices[i].co, i)
tree.balance()
blend = game.vertex_groups.new(name="Seam_Blend")
for v in game.data.vertices:
    blend.add([v.index], 1 - smoothstep(tree.find(game.matrix_world @ v.co)[2] / SEAM_BLEND), 'REPLACE')
dt = game.modifiers.new("SeamWeights", 'DATA_TRANSFER'); dt.object = ch_body
dt.use_vert_data = True; dt.data_types_verts = {'VGROUP_WEIGHTS'}; dt.vert_mapping = 'POLYINTERP_NEAREST'
dt.layers_vgroup_select_src = 'ALL'; dt.layers_vgroup_select_dst = 'NAME'
dt.mix_mode = 'REPLACE'; dt.mix_factor = 1.0; dt.vertex_group = blend.name
with bpy.context.temp_override(object=game, active_object=game):
    bpy.ops.object.datalayout_transfer(modifier=dt.name)
apply(game, dt)
game.vertex_groups.remove(game.vertex_groups["Seam_Blend"])

# Armpits: average each vertex's weights with its neighbours', strongest at the shoulder joint, fading out.
neighbours = [[] for _ in game.data.vertices]
for e in game.data.edges:
    a, b = e.vertices; neighbours[a].append(b); neighbours[b].append(a)
shoulders = [ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:" + n].head_local for n in ("LeftArm", "RightArm")]
strength = {}
for v in game.data.vertices:
    d = min(((game.matrix_world @ v.co) - s).length for s in shoulders)
    s = 1 - smoothstep((d - ARMPIT_RADIUS / 2) / (ARMPIT_RADIUS / 2))
    if s > 0 and neighbours[v.index]: strength[v.index] = s
weights = [{g.group: g.weight for g in v.groups} for v in game.data.vertices]
for _ in range(ARMPIT_SMOOTH_ITERATIONS):
    updated = {}
    for i, s in strength.items():
        avg = {}
        for j in neighbours[i]:
            for g, w in weights[j].items(): avg[g] = avg.get(g, 0.0) + w / len(neighbours[i])
        updated[i] = {g: weights[i].get(g, 0.0) * (1 - 0.5 * s) + avg.get(g, 0.0) * 0.5 * s for g in set(weights[i]) | set(avg)}
    for i, w in updated.items(): weights[i] = w
for i in strength:
    for g, w in weights[i].items():
        if w > 1e-4: game.vertex_groups[g].add([i], w, 'REPLACE')
        else: game.vertex_groups[g].remove([i])
print(f"armpit weights smoothed on {len(strength)} verts")

with bpy.context.temp_override(object=game, active_object=game, selected_editable_objects=[game]):
    bpy.ops.object.vertex_group_limit_total(group_select_mode='ALL', limit=MAX_INFLUENCES)
    bpy.ops.object.vertex_group_normalize_all(group_select_mode='ALL', lock_active=False)
# Thighs: hand the hips' weight below the groin to the thigh, more the further down (not at the centre of the crotch).
hip_g = game.vertex_groups["mixamorig1:Hips"].index
thigh_g = [game.vertex_groups[f"mixamorig1:{s}UpLeg"].index for s in ("Left", "Right")]
for v in game.data.vertices:
    p = game.matrix_world @ v.co
    f = THIGH_TAKEOVER * smoothstep((hips.z - 0.04 - p.z) / 0.10) * smoothstep((abs(p.x) - 0.015) / 0.02)
    groups = {g.group: g for g in v.groups}
    if f <= 0 or hip_g not in groups: continue
    thighs = [groups[i] for i in thigh_g if i in groups]
    if not thighs: continue
    thigh = max(thighs, key=lambda g: g.weight)
    moved = groups[hip_g].weight * f
    groups[hip_g].weight -= moved; thigh.weight += moved
unweighted = sum(1 for v in game.data.vertices if not v.groups)
print(f"weights: {len(game.vertex_groups)} bones, {unweighted} unweighted verts")

game.parent = ch_arm
game.matrix_parent_inverse = ch_arm.matrix_world.inverted()
am = game.modifiers.new("Armature", 'ARMATURE'); am.object = ch_arm

# ---------- 6. Underwear, and no skin under it, so nothing can poke through when he moves ----------
for p in game.data.polygons: p.use_smooth = True
old = bpy.data.objects.get("Base_Underwear")
if old: bpy.data.objects.remove(old, do_unlink=True)
under = body_parts.make_underwear(game, ch_arm)
body_parts.remove_skin_under_underwear(game, ch_arm)

# ---------- 7. UVs and materials; the MPFB body is put away ----------
for obj in (game, under):
    bpy.context.view_layer.objects.active = obj
    for o in bpy.context.view_layer.objects: o.select_set(o == obj)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.01)
    bpy.ops.object.mode_set(mode='OBJECT')
skin, cloth = body_parts.make_materials(ch_body, neck_center.z)
game.data.materials.clear(); game.data.materials.append(skin)
under.data.materials.clear(); under.data.materials.append(cloth)
ref = bpy.data.collections.new("MPFB reference (hidden)")
bpy.context.scene.collection.children.link(ref)
for c in list(mpfb.users_collection): c.objects.unlink(mpfb)
ref.objects.link(mpfb)
bpy.context.view_layer.layer_collection.children[ref.name].exclude = True

print("game body verts", len(game.data.vertices), "tris", sum(len(p.vertices) - 2 for p in game.data.polygons),
      "| underwear verts", len(under.data.vertices))
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print("SAVED", OUT)

# ---------- 8. FBX for Unity: the skeleton, Ch17's head/hands (for checking alignment) and the new meshes ----------
import os
os.makedirs(os.path.dirname(FBX), exist_ok=True)
export = [ch_arm, ch_body, game, under]
for o in bpy.context.view_layer.objects: o.select_set(o in export)
bpy.context.view_layer.objects.active = ch_arm
bpy.ops.export_scene.fbx(filepath=FBX, use_selection=True, object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                         bake_anim=False, use_armature_deform_only=False, mesh_smooth_type='FACE',
                         apply_scale_options='FBX_SCALE_ALL', path_mode='STRIP', embed_textures=False)
print("EXPORTED", FBX)
