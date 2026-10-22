"""Clothing for the base male (BaseMale.blend, from make_human.py). Each garment is cut from the body's own surface,
so it has his exact shape and skin weights, then lifted off the skin, thickened and given a baked print.
Output per garment: Blender/Player/Clothes_<Name>.blend and Assets/Characters/Clothing/<Name>/<Name>.fbx + albedo.

Garments:
  HeartBoxers: white cotton boxer shorts with red hearts and a red elastic waistband."""
import bpy, bmesh, math, os, sys
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from body_parts import pad_islands
from fit_sculpt import smoothstep

BASE = r"C:/Users/josht/My project/Blender/Player/BaseMale.blend"
OUT_BLEND = r"C:/Users/josht/My project/Blender/Player/Clothes_{}.blend"
OUT_DIR = r"C:/Users/josht/My project/Assets/Characters/Clothing/{}"

# HeartBoxers, heights relative to the hips joint
WAIST = 0.075        # top of the waistband, just under the navel
LEG = -0.235         # leg openings, mid-thigh (inseam ~13 cm below the crotch)
EASE_WAIST = 0.004   # lift off the skin at the waistband (elastic: snug)...
EASE_LEG = 0.022     # ...growing to this at the leg openings (boxers hang loose)
BRIDGE_ITERATIONS = 60  # outward-only smoothing passes that let the cloth span hollows (see build_heart_boxers)
SEAT_SPAN = 0.12     # the seat is stretched across between the buttocks' crowns within this far of the middle...
SEAT_SAG = 0.004     # ...dipping only this much in between
POUCH_CLEARANCE = 0.006  # the front clears the genitals by this much (they're hidden while it's worn)
THICKNESS = 0.0018
BAND = 0.035         # waistband height
TEX = 2048
CLOTH = (0.93, 0.92, 0.9)      # off-white cotton (sRGB)
HEART = (0.78, 0.06, 0.12)     # red
BAND_RED = (0.72, 0.05, 0.1)
HEART_SPACING = 0.042          # metres between hearts (rows offset by half)
HEART_SIZE = 0.013             # heart half-width
HIP_RADIUS, LEG_RADIUS = 0.18, 0.095  # wraps the print follows: around the hips, and around each thigh...
LEG_AXIS_X = 0.095                    # ...centred this far either side of the middle


def build_heart_boxers():
    bpy.ops.wm.open_mainfile(filepath=BASE)
    body = bpy.data.objects["BaseMale_Body"]; rig = bpy.data.objects["BaseMale_Rig"]
    gen = bpy.data.objects["BaseMale_Genitals"]
    hips = rig.matrix_world @ rig.data.bones["mixamorig:Hips"].head_local
    top, bottom = hips.z + WAIST, hips.z + LEG

    shorts = body.copy(); shorts.data = body.data.copy(); bpy.context.scene.collection.objects.link(shorts)
    shorts.name = shorts.data.name = "HeartBoxers"
    mw = shorts.matrix_world; inv = mw.inverted()
    bm = bmesh.new(); bm.from_mesh(shorts.data)
    # Straight waistband and leg openings: bisect, then keep the band between.
    for z, up in ((top, 1), (bottom, -1)):
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=inv @ Vector((0, 0, z)),
                               plane_no=(inv.to_3x3() @ Vector((0, 0, up))).normalized(), clear_outer=True)
    # The bisect also leaves bits of the arms/hands that hang in the band: keep the biggest piece only.
    bm.verts.ensure_lookup_table()
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
    islands.sort(key=len)
    bmesh.ops.delete(bm, geom=[v for isl in islands[:-1] for v in isl], context='VERTS')
    print(f"shorts: kept {len(islands[-1])} verts, dropped {len(islands) - 1} other pieces (hands)")

    # Lift off the skin: snug at the waist, looser toward the legs.
    bm.normal_update()
    for v in bm.verts:
        z = (mw @ v.co).z
        k = smoothstep((top - z) / (top - bottom))
        v.co += v.normal * (EASE_WAIST + (EASE_LEG - EASE_WAIST) * k)

    # Fabric doesn't sink into hollows: it stretches across the crease between the buttocks and the groin creases.
    # Smooth the shape outward only (each vertex may move out toward its neighbours' average, never in), so the
    # hollows fill and the rounded parts stay where they are.
    bm.normal_update()
    normals = {v: v.normal.copy() for v in bm.verts}
    weight = {v: smoothstep(((top - BAND) - (mw @ v.co).z) / 0.03) for v in bm.verts}  # waistband stays snug
    filled = 0.0
    for _ in range(BRIDGE_ITERATIONS):
        moves = {}
        for v in bm.verts:
            nb = [e.other_vert(v).co for e in v.link_edges]
            out = (sum(nb, Vector()) / len(nb) - v.co).dot(normals[v])
            if out > 0: moves[v] = normals[v] * out * 0.5 * weight[v]
        for v, m in moves.items(): v.co += m; filled = max(filled, m.length)
    print(f"bridged hollows over {BRIDGE_ITERATIONS} passes")

    # The crease between the buttocks is too deep for that: at each height, stretch the cloth straight across from
    # the crown of one buttock to the other (with a slight sag), and lift anything behind that line onto it.
    crotch_z = min((mw @ v.co).z for v in bm.verts if abs((mw @ v.co).x - hips.x) < 0.01)
    world = {v: mw @ v.co for v in bm.verts}
    seat = [v for v in bm.verts if world[v].y > hips.y and crotch_z - 0.02 < world[v].z < top - BAND]
    lifted, strip = 0, set()
    for v in seat:
        p = world[v]
        if abs(p.x - hips.x) > SEAT_SPAN: continue
        level = [world[u] for u in seat if abs(world[u].z - p.z) < 0.012]
        crowns = []
        for side in (-1, 1):
            cheek = [q for q in level if 0.015 < (q.x - hips.x) * side < SEAT_SPAN]
            if cheek: crowns.append(max(cheek, key=lambda q: q.y))
        if len(crowns) < 2: continue
        a, b = crowns
        if not a.x < p.x < b.x: continue  # only the cloth between the two crowns spans the gap
        t = (p.x - a.x) / (b.x - a.x)
        across = a.y + (b.y - a.y) * t - SEAT_SAG * 4 * t * (1 - t)
        fade = smoothstep((p.z - (crotch_z - 0.02)) / 0.05)  # meets the leg openings smoothly at the bottom
        if p.y < across:
            p = Vector((p.x, p.y + (across - p.y) * fade, p.z))
            v.co = inv @ p; lifted += 1; strip.add(v)
    print(f"seat: lifted {lifted} verts across the crease")
    # The crease's walls now lie on top of each other along the stretched line: spread them out evenly.
    relax = {u for v in strip for e in v.link_edges for u in (v, e.other_vert(v))}
    for _ in range(20):
        bmesh.ops.smooth_vert(bm, verts=[v for v in relax if not v.is_boundary], factor=0.5,
                              use_axis_x=True, use_axis_y=False, use_axis_z=True)
    for _ in range(10):  # blend the lifted strip into its surroundings, still outward only
        bm.normal_update()
        for v in seat:
            nb = [e.other_vert(v).co for e in v.link_edges]
            out = (sum(nb, Vector()) / len(nb) - v.co).dot(v.normal)
            if out > 0: v.co += v.normal * out * 0.5 * weight[v]

    # Pouch: push the front out over the genitals, then relax it into a soft bulge.
    gb = bmesh.new(); gb.from_mesh(gen.data); gb.transform(gen.matrix_world)
    gtree = BVHTree.FromBMesh(gb); gb.free()
    pushed = set()
    for _ in range(3):
        bm.normal_update()
        for v in bm.verts:
            p = mw @ v.co
            q, n, _, d = gtree.find_nearest(p)
            if q is None or d > 0.05: continue
            outside = (p - q).dot(n) > 0
            if not outside or d < POUCH_CLEARANCE:
                normal = (mw.to_3x3() @ v.normal).normalized()
                # move out along the shorts' normal until clear of the genital surface
                step = (POUCH_CLEARANCE + (d if not outside else -d))
                v.co += inv.to_3x3() @ (normal * max(step, 0.002))
                pushed.add(v)
    region = set()
    for v in pushed:
        region.add(v)
        for e in v.link_edges:
            region.add(e.other_vert(v))
            for e2 in e.other_vert(v).link_edges: region.add(e2.other_vert(e.other_vert(v)))
    for _ in range(12):
        bmesh.ops.smooth_vert(bm, verts=list(region - {v for v in region if v.is_boundary}), factor=0.5,
                              use_axis_x=True, use_axis_y=True, use_axis_z=True)
    print(f"pouch: {len(pushed)} verts pushed out, {len(region)} relaxed")
    bm.to_mesh(shorts.data); bm.free()

    # UVs, then thickness (the rim shows at the openings).
    for o in bpy.context.view_layer.objects: o.select_set(o == shorts)
    bpy.context.view_layer.objects.active = shorts
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.012)
    bpy.ops.object.mode_set(mode='OBJECT')
    sol = shorts.modifiers.new("Thickness", 'SOLIDIFY'); sol.thickness = THICKNESS; sol.offset = -1
    sol.use_rim = True; sol.use_even_offset = True
    shorts.modifiers.move(shorts.modifiers.find(sol.name), 0)
    with bpy.context.temp_override(object=shorts, active_object=shorts):
        bpy.ops.object.modifier_apply(modifier=sol.name)
    for p in shorts.data.polygons: p.use_smooth = True

    tex_path = bake_heart_print(shorts, hips, top, bottom)

    mat = bpy.data.materials.new("HeartBoxers"); mat.use_nodes = True
    t = mat.node_tree.nodes.new("ShaderNodeTexImage"); t.image = bpy.data.images.load(tex_path)
    bsdf = mat.node_tree.nodes["Principled BSDF"]; bsdf.inputs["Roughness"].default_value = 0.85
    mat.node_tree.links.new(t.outputs[0], bsdf.inputs["Base Color"])
    shorts.data.materials.clear(); shorts.data.materials.append(mat)
    print("HeartBoxers:", len(shorts.data.vertices), "verts")
    export("HeartBoxers", shorts, rig)


def crotch_height(obj, hips):
    """Lowest point of the garment near the body's midline (the bottom of the crotch seam)."""
    mw = obj.matrix_world
    mid = [(mw @ v.co).z for v in obj.data.vertices if abs((mw @ v.co).x - hips.x) < 0.01]
    return min(mid)


def bake_heart_print(obj, hips, top, bottom):
    """Heart pattern laid out around the body (so it is evenly spaced and upright everywhere, whatever the UVs),
    baked into the garment's UV texture."""
    out_dir = OUT_DIR.format("HeartBoxers"); os.makedirs(out_dir, exist_ok=True)
    s = bpy.context.scene; s.render.engine = 'CYCLES'; s.cycles.device = 'CPU'; s.cycles.samples = 1
    # 1. Bake each texel's world position (and which face side) into a float image.
    pos_img = bpy.data.images.new("pos", TEX, TEX, float_buffer=True)
    mat = bpy.data.materials.new("bake"); mat.use_nodes = True; nt = mat.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); emit = nt.nodes.new("ShaderNodeEmission")
    geo = nt.nodes.new("ShaderNodeNewGeometry"); nt.links.new(geo.outputs["Position"], emit.inputs["Color"])
    nt.links.new(emit.outputs[0], out.inputs["Surface"])
    tex = nt.nodes.new("ShaderNodeTexImage"); tex.image = pos_img; nt.nodes.active = tex
    obj.data.materials.clear(); obj.data.materials.append(mat)
    for o in bpy.context.view_layer.objects: o.select_set(o == obj)
    bpy.context.view_layer.objects.active = obj
    # A margin carries positions past the island edges, so the print continues across UV seams without gaps.
    bpy.ops.object.bake(type='EMIT', margin=12, use_clear=True)
    px = np.empty(TEX * TEX * 4, dtype=np.float32); pos_img.pixels.foreach_get(px); px = px.reshape(TEX, TEX, 4)
    P = px[..., :3].copy(); covered = np.abs(P).sum(-1) > 1e-6
    # ...and each texel's surface direction: the wraps stretch the print on cloth facing up or down (under the seat).
    nt.links.new(geo.outputs["Normal"], emit.inputs["Color"])
    bpy.ops.object.bake(type='EMIT', margin=12, use_clear=True)
    nrm = np.empty(TEX * TEX * 4, dtype=np.float32); pos_img.pixels.foreach_get(nrm)
    facing_z = nrm.reshape(TEX, TEX, 4)[..., 2]

    # 2. Pattern coordinates: wrapped around the hips above the crotch and around each thigh below it. Each wrap's
    # circumference is a whole number of hearts, so the pattern meets itself without a seam.
    x, y, z = P[..., 0] - hips.x, P[..., 1] - hips.y, P[..., 2]
    crotch = crotch_height(obj, hips)
    in_leg = z < crotch
    thigh_x = np.where(x > 0, LEG_AXIS_X, -LEG_AXIS_X)
    hip_n, leg_n = round(2 * math.pi * HIP_RADIUS / HEART_SPACING), round(2 * math.pi * LEG_RADIUS / HEART_SPACING)
    angle = np.where(in_leg, np.arctan2(x - thigh_x, -y), np.arctan2(x, -y))
    u = angle / (2 * math.pi) * np.where(in_leg, leg_n, hip_n) * HEART_SPACING
    v = z - bottom
    row = np.floor(v / HEART_SPACING)
    cell_u = u / HEART_SPACING + 0.5 * (row % 2)
    cu = (cell_u - np.floor(cell_u) - 0.5) * HEART_SPACING
    cv = (v / HEART_SPACING - row - 0.5) * HEART_SPACING
    hx, hy = cu / HEART_SIZE, cv / HEART_SIZE * 1.1 + 0.15
    heart = (hx ** 2 + hy ** 2 - 1) ** 3 - hx ** 2 * hy ** 3 <= 0
    # Hearts are cut off at the waistband, hems and crotch seam, like real printed fabric. Only cloth facing up or
    # down (under the seat) is left plain, where the wraps would stretch them into smears.
    heart &= np.abs(facing_z) < 0.6

    col = np.empty(P.shape, dtype=np.float32); col[:] = CLOTH
    col[heart] = HEART
    # Fine cotton weave and soft unevenness.
    rng = np.random.default_rng(3)
    col *= (1 - 0.03 * rng.random(P.shape[:2]))[..., None]
    # Waistband: red elastic with fine ribs, a stitched edge; hems at the leg openings.
    band = (top - z) < BAND
    rib = 0.9 + 0.1 * (np.cos(u / 0.004 * math.pi) > 0)
    col[band] = np.array(BAND_RED) * rib[band][:, None]
    stitch = np.abs((top - z) - BAND - 0.004) < 0.0008
    col[stitch & ~band] *= 0.75
    hem = (z - bottom) < 0.018
    col[hem & ~heart] *= 0.94
    hem_stitch = np.abs((z - bottom) - 0.016) < 0.0008
    col[hem_stitch] *= 0.75

    img = bpy.data.images.new("HeartBoxers_Albedo", TEX, TEX)
    rgba = np.ones((TEX, TEX, 4), dtype=np.float32); rgba[..., :3] = col
    rgba[~covered, :3] = 0
    img.pixels.foreach_set(rgba.ravel())
    path = os.path.join(out_dir, "HeartBoxers_Albedo.png")
    img.filepath_raw = path; img.file_format = 'PNG'; img.save()
    bpy.data.images.remove(img); bpy.data.images.remove(pos_img)
    pad_islands(path, obj)
    print("baked", path, f"coverage {covered.mean() * 100:.0f}%")
    return path


def export(name, garment, rig):
    blend = OUT_BLEND.format(name)
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    fbx = os.path.join(OUT_DIR.format(name), name + ".fbx")
    for o in bpy.context.view_layer.objects: o.select_set(o in (garment, rig))
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                             bake_anim=False, use_armature_deform_only=False, mesh_smooth_type='FACE',
                             apply_scale_options='FBX_SCALE_ALL', path_mode='STRIP', embed_textures=False)
    print("SAVED", blend, "EXPORTED", fbx)


build_heart_boxers()
