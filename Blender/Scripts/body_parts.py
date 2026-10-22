"""Pieces shared by the base-body builds: the cut at Ch17's neck and wrists, the underwear, and the materials."""
import bpy, bmesh, math, os
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

OVERLAP = 0.02   # how far the body continues inside Ch17's neck / wrists
BLEND = 0.05     # distance over which the body eases onto Ch17's ring at the neck...
WRIST_BLEND = 0.13  # ...and at the wrists: most of the forearm, so it tapers to his thinner wrist instead of stepping
INSET = 0.003    # body surface sits this far inside Ch17's (when Ch17's surface isn't given)
# With Ch17's surface given, the two cross over instead: at his open edge the body sits OUTSET outside it (hiding
# the edge), and by the end of the overlap it is CROSS_INSET inside his skin (hiding the body's own edge).
OUTSET = 0.003
CROSS_INSET = 0.004
CROSS_AT, CROSS_SPAN = 0.4, 0.15  # the crossing starts 40% into the overlap and takes 15% of it


class Ring:
    """Ch17's open edge as a radius around an axis, by angle, starting at `start` along the axis."""
    def __init__(self, pts, origin, axis, side, bins=48):
        self.origin, self.axis, self.bins = origin, axis.normalized(), bins
        self.side = side  # +1: Ch17's mesh lies in the +axis direction from the ring
        along = [(p - origin).dot(self.axis) for p in pts]
        self.start = min(along) if side > 0 else max(along)
        ref = Vector((0, 0, 1)) if abs(self.axis.z) < 0.9 else Vector((1, 0, 0))
        self.u = (ref - self.axis * ref.dot(self.axis)).normalized(); self.v = self.axis.cross(self.u)
        radii = [[] for _ in range(bins)]
        for p, a in zip(pts, along):
            if abs(a - self.start) < 0.012:
                d = p - origin - self.axis * a
                radii[self.bin(d)].append(d.length)
        filled = [sum(r) / len(r) if r else None for r in radii]
        for i in range(bins):  # fill empty bins from neighbours
            if filled[i] is None:
                k = 1
                while filled[(i - k) % bins] is None and filled[(i + k) % bins] is None: k += 1
                filled[i] = filled[(i - k) % bins] if filled[(i - k) % bins] is not None else filled[(i + k) % bins]
        self.radius = filled
    def bin(self, d):
        ang = math.atan2(d.dot(self.v), d.dot(self.u))
        return int((ang + math.pi) / (2 * math.pi) * self.bins) % self.bins
    def local(self, p):
        a = (p - self.origin).dot(self.axis)
        d = p - self.origin - self.axis * a
        return (a - self.start) * self.side, d  # depth past the ring into Ch17's mesh (+), radial vector


def head_and_hand_rings(ch_body, ch_arm):
    """Rings at Ch17's neck opening and wrists, plus the neck opening's centre."""
    ch_pts = [ch_body.matrix_world @ v.co for v in ch_body.data.vertices]
    neck_pts = [p for p in ch_pts if p.z < 1.56 and abs(p.x) < 0.2]
    # The neck opening is slanted (low at the front collar, higher at the back), so fit the cut plane to the head
    # mesh's open edge loop there rather than assuming a level ring.
    open_edges = {}
    for poly in ch_body.data.polygons:
        for k in poly.edge_keys: open_edges[k] = open_edges.get(k, 0) + 1
    # Group the open edges into loops (the head also has open eyes, mouth...) and take the lowest one near the middle.
    nbrs = {}
    for k, n in open_edges.items():
        if n == 1: nbrs.setdefault(k[0], []).append(k[1]); nbrs.setdefault(k[1], []).append(k[0])
    loops, seen = [], set()
    for s in nbrs:
        if s in seen: continue
        loop, todo = [], [s]; seen.add(s)
        while todo:
            i = todo.pop(); loop.append(i)
            for j in nbrs[i]:
                if j not in seen: seen.add(j); todo.append(j)
        loops.append(loop)
    central = [l for l in loops if len(l) > 12 and abs(sum(ch_pts[i].x for i in l) / len(l)) < 0.05]
    neck_loop = min(central, key=lambda l: sum(ch_pts[i].z for i in l) / len(l))
    neck_ring_pts = [ch_pts[i] for i in neck_loop]
    neck_center = sum(neck_ring_pts, Vector()) / len(neck_ring_pts)
    _, _, vt = np.linalg.svd(np.array([p - neck_center for p in neck_ring_pts]))
    neck_axis = Vector(vt[2]); neck_axis = neck_axis if neck_axis.z > 0 else -neck_axis
    print(f"neck ring: {len(neck_ring_pts)} pts, centre {tuple(round(c, 3) for c in neck_center)}, "
          f"tilt {math.degrees(neck_axis.angle(Vector((0, 0, 1)))):.1f} deg")
    rings = [("neck", Ring(neck_pts, neck_center, neck_axis, +1), 0.12)]
    for side in (+1, -1):
        hand = [p for p in ch_pts if p.x * side > 0.3]
        hand_bone = ch_arm.data.bones["mixamorig1:LeftHand" if side > 0 else "mixamorig1:RightHand"]
        fore_bone = ch_arm.data.bones["mixamorig1:LeftForeArm" if side > 0 else "mixamorig1:RightForeArm"]
        h = ch_arm.matrix_world @ hand_bone.head_local; f = ch_arm.matrix_world @ fore_bone.head_local
        rings.append(("wrist", Ring(hand, f, h - f, +1), 0.09))
    return rings, neck_center


def cut_at_head_and_hands(body, rings, ch_body=None):
    """Cut the body inside Ch17's neck and wrist rings and pull its edge in to meet Ch17's skin, so the two overlap
    instead of leaving a gap. Given ch_body, the overlap follows his actual surface and crosses from just outside his
    open edge to just inside his skin, so neither mesh's edge shows."""
    surface = None
    if ch_body is not None:
        sb = bmesh.new(); sb.from_mesh(ch_body.data); sb.transform(ch_body.matrix_world)  # rest shape, world space
        surface = BVHTree.FromBMesh(sb); sb.free()
    bm = bmesh.new(); bm.from_mesh(body.data)
    bmw = body.matrix_world; bmw_inv = bmw.inverted()
    doomed = set()
    for v in bm.verts:
        p = bmw @ v.co
        if abs(p.x) < 0.15 and rings[0][1].local(p)[0] > 0.08:  # well inside the head, wherever it is
            doomed.add(v); continue
        for name, ring, reach in rings:
            depth_in, d = ring.local(p)
            if name == "wrist" and depth_in > 0.05:  # well past the wrist: the hand, however wide the fingers spread
                doomed.add(v); break
            if d.length > reach: continue
            if depth_in > OVERLAP: doomed.add(v); break
            if surface is not None and depth_in > 0:
                q, n, _, _ = surface.find_nearest(p)
                if q is not None:
                    if n.dot(q - ring.origin - ring.axis * (q - ring.origin).dot(ring.axis)) < 0: n = -n  # outward
                    # Cross over abruptly mid-overlap: a gradual crossing keeps the two surfaces within millimetres
                    # over its whole length, and their different triangles poke through each other in patches.
                    t = min(1.0, max(0.0, (depth_in / OVERLAP - CROSS_AT) / CROSS_SPAN))
                    v.co = bmw_inv @ (q + n * (OUTSET + (-CROSS_INSET - OUTSET) * t))
                    break
            blend = BLEND if name == "neck" else WRIST_BLEND
            if depth_in > -blend:
                target = ring.radius[ring.bin(d)] + (OUTSET if surface is not None else -INSET)
                k = min(1.0, (depth_in + blend) / blend)
                k = k * k * (3 - 2 * k)
                r = d.length + (target - d.length) * k
                if d.length > 1e-6:
                    v.co = bmw_inv @ (p - d + d.normalized() * r)
                break
    bmesh.ops.delete(bm, geom=list(doomed), context='VERTS')
    bm.to_mesh(body.data); bm.free()
    print(f"cut {len(doomed)} verts at the neck and wrists")


UNDERWEAR_WAIST = 0.11   # waistband height above the hips joint
UNDERWEAR_LEG = 0.17     # leg openings at least this far below it...
UNDERWEAR_CROTCH = 0.05  # ...and this far below the bottom of the crotch, so no skin shows between the legs


def _underwear_band(body, ch_arm):
    hips = ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:Hips"].head_local
    mw = body.matrix_world
    middle = [(mw @ v.co).z for v in body.data.vertices if abs((mw @ v.co).x) < 0.012 and (mw @ v.co).z < hips.z]
    crotch_z = min(middle) if middle else hips.z
    return hips, hips.z + UNDERWEAR_WAIST, min(hips.z - UNDERWEAR_LEG, crotch_z - UNDERWEAR_CROTCH)


def remove_skin_under_underwear(body, ch_arm, margin=0.025):
    """Delete the body's faces that the underwear covers (keeping `margin` under its edges). The two share skin
    weights, so the cut edge stays hidden in any pose."""
    _, waist_z, leg_z = _underwear_band(body, ch_arm)
    mw = body.matrix_world
    bm = bmesh.new(); bm.from_mesh(body.data)
    hidden = [f for f in bm.faces if all(leg_z + margin < (mw @ v.co).z < waist_z - margin for v in f.verts)]
    bmesh.ops.delete(bm, geom=hidden, context='FACES')
    bm.to_mesh(body.data); bm.free()
    print(f"removed {len(hidden)} faces of skin under the underwear")


def make_underwear(body, ch_arm):
    """Boxer briefs: the body's hip band, cut straight at the waist and thighs and lifted off the skin."""
    hips, waist_z, leg_z = _underwear_band(body, ch_arm)
    print(f"hips z={hips.z:.3f} underwear {leg_z:.3f}..{waist_z:.3f}")
    under = body.copy(); under.data = body.data.copy()
    bpy.context.scene.collection.objects.link(under)
    under.name = under.data.name = "Base_Underwear"
    inv = body.matrix_world.inverted()
    bm = bmesh.new(); bm.from_mesh(under.data)
    # Bisect rather than picking whole faces, so the waistband and leg openings are straight, not stair-stepped.
    for z, up in ((waist_z, 1), (leg_z, -1)):
        co = inv @ Vector((0, 0, z)); no = (inv.to_3x3() @ Vector((0, 0, up))).normalized()
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=co, plane_no=no, clear_outer=True)
    bm.normal_update()
    # Off the skin by 7 mm, but tucked in to 2 mm at the waistband and leg openings.
    for v in bm.verts:
        v.co += v.normal * (0.002 if v.is_boundary else 0.007)
    bm.to_mesh(under.data); bm.free()
    for m in under.modifiers:
        if m.type == 'ARMATURE': m.object = ch_arm
    return under


def _srgb_to_linear(c): return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def make_materials(ch_body, neck_cut):
    """Skin matching Ch17's (sampled from its texture just above the neck cut) and dark grey underwear cloth."""
    me = ch_body.data; uv = me.uv_layers.active.data
    mat = ch_body.material_slots[0].material
    img = next(n.image for n in mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image and "1001_Diffuse" in n.image.filepath)
    w, h = img.size; px = img.pixels[:]
    acc = Vector((0, 0, 0)); n = 0
    for poly in me.polygons:
        c = ch_body.matrix_world @ poly.center
        if abs(c.x) < 0.2 and neck_cut < c.z < neck_cut + 0.05:
            for li in poly.loop_indices:
                u, v = uv[li].uv
                x = min(w - 1, max(0, int(u % 1 * w))); y = min(h - 1, max(0, int(v % 1 * h)))
                i = (y * w + x) * 4
                acc += Vector(px[i:i + 3]); n += 1
    skin_rgb = Vector([_srgb_to_linear(c) for c in acc / max(n, 1)])
    print("skin rgb", tuple(round(c, 3) for c in skin_rgb))
    skin = bpy.data.materials.get("Base_Skin") or bpy.data.materials.new("Base_Skin")
    skin.use_nodes = True
    bsdf = skin.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*skin_rgb, 1)
    bsdf.inputs["Roughness"].default_value = 0.55
    cloth = bpy.data.materials.get("Base_Underwear") or bpy.data.materials.new("Base_Underwear")
    cloth.use_nodes = True
    cb = cloth.node_tree.nodes["Principled BSDF"]
    cb.inputs["Base Color"].default_value = (0.06, 0.065, 0.07, 1)
    cb.inputs["Roughness"].default_value = 0.85
    return skin, cloth


def pad_islands(path, obj, passes=48):
    """Fill the skin texture outside the body's UV islands by growing the island edges outward, so smaller mip
    levels don't blend in the dark padding (it showed as dark streaks along the UV seams in Unity)."""
    img = bpy.data.images.load(path, check_existing=False); w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32); img.pixels.foreach_get(px); px = px.reshape(h, w, 4)
    # Island mask: rasterise the UV triangles.
    mask = np.zeros((h, w), dtype=bool)
    uv = obj.data.uv_layers.active.data
    for poly in obj.data.polygons:
        pts = np.array([uv[i].uv[:] for i in poly.loop_indices]) * (w, h)
        x0, y0 = np.floor(pts.min(0)).astype(int); x1, y1 = np.ceil(pts.max(0)).astype(int)
        x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, w - 1), min(y1, h - 1)
        if x1 < x0 or y1 < y0: continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        inside = np.zeros_like(gx, dtype=bool)
        for k in range(1, len(pts) - 1):  # fan of triangles
            a, b, c = pts[0], pts[k], pts[k + 1]
            d = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
            if abs(d) < 1e-9: continue
            l1 = ((b[1] - c[1]) * (gx - c[0]) + (c[0] - b[0]) * (gy - c[1])) / d
            l2 = ((c[1] - a[1]) * (gx - c[0]) + (a[0] - c[0]) * (gy - c[1])) / d
            inside |= (l1 >= -0.02) & (l2 >= -0.02) & (1 - l1 - l2 >= -0.02)
        mask[y0:y1 + 1, x0:x1 + 1] |= inside
    filled = mask.copy(); rgb = px[..., :3].copy()
    for _ in range(passes):  # grow: each empty pixel takes the average of its filled neighbours
        acc = np.zeros_like(rgb); cnt = np.zeros((h, w), dtype=np.float32)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            m = np.roll(filled, (dy, dx), (0, 1)); acc += np.roll(rgb, (dy, dx), (0, 1)) * m[..., None]; cnt += m
        grow = (~filled) & (cnt > 0)
        rgb[grow] = acc[grow] / cnt[grow][:, None]; filled |= grow
    rgb[~filled] = rgb[mask].mean(0)
    px[..., :3] = rgb; px[..., 3] = 1.0
    img.pixels.foreach_set(px.ravel()); img.filepath_raw = path; img.file_format = 'PNG'; img.save()
    print(f"padded {os.path.basename(path)}: islands cover {mask.mean() * 100:.0f}%")
