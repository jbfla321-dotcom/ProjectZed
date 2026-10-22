"""Builds ProjectZed's houses from the floor plans in house_plans.py, plus the furniture / door kit.

Run: blender -b -P make_house.py [-- Cabin Rambler ...]   (no names = every house, and the kit)

Per house:
  Assets/Environment/Houses/Models/House_<Name>.fbx  objects Exterior (siding, trim, roof, porch, foundation),
      Interior (walls inside, floors, ceilings, stairs, baseboards) and Ramps (invisible collision for steps)
  Assets/Environment/Houses/Plans/House_<Name>.json  doors, window panes, rooms, furniture, steps, size; read by
      the Unity HouseBuilder
  Blender/Houses/House_<Name>.blend
Kit: Assets/Environment/Houses/Models/House_Kit.fbx, one object per door slab / furniture piece, each standing on
its own origin and facing +z.

Everything is generated in plan space (= Unity's: x right, y up, z forward) and converted to Blender's axes on the
way in (B = (-x, -z, y)), so the FBX lands in Unity in plan coordinates. All geometry is closed convex solids; faces
are wound outward from each solid's centre and UV'd by box projection in metres (materials set the tiling).
Material slot names (Ext_*, Int_*, Floor_*, Furn_*) are what HouseBuilder maps to real materials.
"""
import bpy, json, math, os, random, sys
from mathutils import Vector

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from house_plans import PLANS, FINISH

PROJECT = r"C:/Users/josht/My project"
OUT_MODELS = PROJECT + "/Assets/Environment/Houses/Models"
OUT_PLANS = PROJECT + "/Assets/Environment/Houses/Plans"
OUT_BLEND = PROJECT + "/Blender/Houses"

EXT_T, INT_T = 0.2, 0.12          # wall thickness, outside / inside walls
STOREY_H = 2.8                    # floor to floor
CEIL = 2.45                       # floor to ceiling
PLATE = CEIL + 0.15               # top storey's outside walls stop here; the roof sits on them
FLOOR = 0.45                      # ground to ground-floor top (crawlspace foundation)
DOOR_H = 2.05
SILL, HEAD, SMALL_SILL = 0.9, 2.1, 1.5
EAVE_OV, RAKE_OV = 0.45, 0.35     # roof overhangs at the eaves / gable ends
ROOF_T = 0.18
PORCH_T = 0.14
RISE = 0.17                       # outside steps
TREAD = 0.28

# Preview colours for renders (Unity replaces every slot with a real material).
COLORS = {
    "Ext_Siding": (0.55, 0.62, 0.66), "Ext_Trim": (0.92, 0.91, 0.86), "Ext_Soffit": (0.85, 0.84, 0.8),
    "Ext_Roof": (0.2, 0.2, 0.21), "Ext_Foundation": (0.5, 0.49, 0.46), "Ext_Deck": (0.45, 0.36, 0.27),
    "Ext_WindowFrame": (0.75, 0.75, 0.73), "Ext_Brick": (0.5, 0.25, 0.2), "Ext_Metal": (0.15, 0.15, 0.15),
    "Int_Plaster": (0.86, 0.83, 0.76), "Int_Wallpaper": (0.75, 0.68, 0.55), "Int_Tile": (0.8, 0.86, 0.85),
    "Int_Plank": (0.6, 0.45, 0.3), "Int_Trim": (0.93, 0.92, 0.88), "Int_Ceiling": (0.93, 0.93, 0.9),
    "Int_Stair": (0.5, 0.36, 0.22), "Floor_Wood": (0.52, 0.37, 0.23), "Floor_Carpet": (0.55, 0.5, 0.42),
    "Floor_Lino": (0.75, 0.72, 0.6), "Ramp": (1.0, 0.0, 1.0),
    "Furn_Wood": (0.5, 0.33, 0.2), "Furn_DarkWood": (0.3, 0.2, 0.13), "Furn_Fabric": (0.45, 0.38, 0.3),
    "Furn_Mattress": (0.9, 0.88, 0.82), "Furn_Blanket": (0.35, 0.42, 0.55), "Furn_White": (0.92, 0.91, 0.87),
    "Furn_Metal": (0.6, 0.6, 0.6), "Furn_Black": (0.05, 0.05, 0.05), "Furn_Porcelain": (0.95, 0.95, 0.93),
    "Furn_Counter": (0.82, 0.78, 0.66), "Furn_Rug": (0.5, 0.25, 0.2), "Furn_Mirror": (0.7, 0.75, 0.78),
    "Door_Paint": (0.93, 0.92, 0.88), "Door_Wood": (0.55, 0.38, 0.22), "Door_Brass": (0.75, 0.6, 0.3),
}


def B(p):
    return Vector((-p[0], -p[2], p[1]))


# ---------------------------------------------------------------- mesh accumulation
class Mesh:
    def __init__(self):
        self.verts, self.faces = [], []   # faces: (vertex indices, material, uvs)

    def add(self, pts_b, mat, uvs):
        i = len(self.verts)
        self.verts.extend(pts_b)
        self.faces.append((list(range(i, i + len(pts_b))), mat, uvs))

    def to_object(self, name, collection):
        me = bpy.data.meshes.new(name)
        mats = sorted({f[1] for f in self.faces})
        me.from_pydata([tuple(v) for v in self.verts], [], [f[0] for f in self.faces])
        uv = me.uv_layers.new(name="UVMap")
        loop = 0
        for poly, (idx, mat, uvs) in zip(me.polygons, self.faces):
            poly.material_index = mats.index(mat)
            for k in range(len(idx)):
                uv.data[poly.loop_start + k].uv = uvs[k]
        for m in mats:
            me.materials.append(material(m))
        me.validate()
        ob = bpy.data.objects.new(name, me)
        collection.objects.link(ob)
        return ob


def material(name):
    m = bpy.data.materials.get(name)
    if m is None:
        m = bpy.data.materials.new(name)
        c = COLORS.get(name, (0.7, 0.7, 0.7))
        m.diffuse_color = (*c, 1.0)
        m.use_nodes = True
        bsdf = m.node_tree.nodes.get("Principled BSDF")
        if bsdf:
            bsdf.inputs["Base Color"].default_value = (*c, 1.0)
            bsdf.inputs["Roughness"].default_value = 0.8
    return m


class Sink:
    """Routes faces to meshes by material: Ext_* -> Exterior, Ramp -> Ramps, everything else -> Interior.
    A kit item uses a single mesh (route=None)."""
    def __init__(self, single=None):
        self.single = single
        self.meshes = {"Exterior": Mesh(), "Interior": Mesh(), "Ramps": Mesh()}

    def mesh_for(self, mat):
        if self.single is not None:
            return self.single
        if mat == "Ramp":
            return self.meshes["Ramps"]
        return self.meshes["Exterior" if mat.startswith("Ext_") else "Interior"]


def solid(sink, pts, faces, mat, frame=None):
    """A closed convex solid. pts: plan-space Vectors; faces: index lists. mat: a material name or a function
    (face centre, outward normal) -> name, both in plan space. frame (origin, ex, ey, ez) gives the UV projection
    axes; default world axes."""
    pts = [Vector(p) for p in pts]
    centre = sum(pts, Vector()) / len(pts)
    o, ex, ey, ez = frame or (Vector(), Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)))
    for f in faces:
        fp = [pts[i] for i in f]
        fc = sum(fp, Vector()) / len(fp)
        n = Vector()
        for a, b in zip(fp, fp[1:] + fp[:1]):   # Newell normal (orientation fixed below)
            n += a.cross(b)
        if n.length < 1e-9:
            continue
        n.normalize()
        if n.dot(fc - centre) < 0:
            n = -n
        name = mat(fc, n) if callable(mat) else mat
        if name is None:
            continue
        # Blender winding: counter-clockwise seen from outside (right-handed, in Blender space)
        bp = [B(p) for p in fp]
        bn = (bp[1] - bp[0]).cross(bp[2] - bp[0])
        bc = sum(bp, Vector()) / len(bp)
        if bn.dot(bc - B(centre)) < 0:
            fp.reverse(); bp.reverse()
        ln = Vector((n.dot(ex), n.dot(ey), n.dot(ez)))
        uvs = []
        for p in fp:
            l = p - o
            lx, ly, lz = l.dot(ex), l.dot(ey), l.dot(ez)
            ax, ay, az = abs(ln.x), abs(ln.y), abs(ln.z)
            if ay >= ax and ay >= az:
                uvs.append((lx, lz))
            elif ax >= az:
                uvs.append((lz, ly))
            else:
                uvs.append((lx, ly))
        sink.mesh_for(name).add(bp, name, uvs)


CUBE_FACES = [[0, 2, 6, 4], [1, 5, 7, 3], [0, 4, 5, 1], [2, 3, 7, 6], [0, 1, 3, 2], [4, 6, 7, 5]]


def obox(sink, c, ex, ey, ez, hx, hy, hz, mat, world_uv=False):
    """Oriented box: centre c, unit axes ex/ey/ez, half sizes."""
    c, ex, ey, ez = Vector(c), Vector(ex), Vector(ey), Vector(ez)
    pts = [c + ex * (hx if i & 1 else -hx) + ey * (hy if i & 2 else -hy) + ez * (hz if i & 4 else -hz) for i in range(8)]
    frame = None if world_uv else (c, ex, ey, ez)
    solid(sink, pts, CUBE_FACES, mat, frame)


def abox(sink, mn, mx, mat):
    """Axis-aligned box from min/max corners (plan space), world-space UVs so neighbouring pieces line up."""
    mn, mx = Vector(mn), Vector(mx)
    if min(mx - mn) <= 1e-5:
        return
    c, h = (mn + mx) / 2, (mx - mn) / 2
    obox(sink, c, (1, 0, 0), (0, 1, 0), (0, 0, 1), h.x, h.y, h.z, mat, world_uv=True)


def ybox(sink, c, size, yaw, mat):
    """Box rotated about y by yaw degrees (0 = local +z faces plan +z); c is the bottom centre."""
    a = math.radians(yaw)
    ex = Vector((math.cos(a), 0, -math.sin(a)))
    ez = Vector((math.sin(a), 0, math.cos(a)))
    w, h, d = size
    obox(sink, Vector(c) + Vector((0, h / 2, 0)), ex, (0, 1, 0), ez, w / 2, h / 2, d / 2, mat)


def prism(sink, poly, offset, mat):
    """Extrudes a convex planar polygon (plan-space points) by an offset vector."""
    poly = [Vector(p) for p in poly]
    n = len(poly)
    pts = poly + [p + Vector(offset) for p in poly]
    faces = [list(range(n)), list(range(n, 2 * n))] + [[i, (i + 1) % n, n + (i + 1) % n, n + i] for i in range(n)]
    solid(sink, pts, faces, mat)


def cylinder(sink, base, radius, height, mat, seg=12, axis="y"):
    base = Vector(base)
    ring = []
    for i in range(seg):
        a = 2 * math.pi * i / seg
        if axis == "y":
            ring.append(base + Vector((math.cos(a) * radius, 0, math.sin(a) * radius)))
        elif axis == "z":
            ring.append(base + Vector((math.cos(a) * radius, math.sin(a) * radius, 0)))
        else:
            ring.append(base + Vector((0, math.cos(a) * radius, math.sin(a) * radius)))
    off = {"y": (0, height, 0), "z": (0, 0, height), "x": (height, 0, 0)}[axis]
    prism(sink, ring, off, mat)


# ---------------------------------------------------------------- plan geometry
class Wall:
    def __init__(self, axis, c, a, b, ext):
        self.axis, self.c, self.a, self.b, self.ext = axis, c, a, b, ext   # axis "x": runs along x at z = c
        self.t = EXT_T if ext else INT_T
        self.openings = []

    def contains(self, x, z, pad=0.0):
        u, v = (x, z) if self.axis == "x" else (z, x)
        return abs(v - self.c) <= self.t / 2 + pad and self.a - self.t / 2 - pad <= u <= self.b + self.t / 2 + pad

    def point(self, u, y=0.0):
        return Vector((u, y, self.c)) if self.axis == "x" else Vector((self.c, y, u))


def merge(intervals):
    out = []
    for a, b in sorted(intervals):
        if out and a <= out[-1][1] + 1e-4:
            out[-1][1] = max(out[-1][1], b)
        else:
            out.append([a, b])
    return out


def wall_lines(rooms, hw, hd):
    lines = {}
    for r in rooms:
        x0, z0, x1, z1 = r["rect"]
        for key, a, b in ((("x", z0), x0, x1), (("x", z1), x0, x1), (("z", x0), z0, z1), (("z", x1), z0, z1)):
            lines.setdefault((key[0], round(key[1], 4)), []).append((a, b))
    walls = []
    for (axis, c), ivs in lines.items():
        ext = abs(abs(c) - (hd if axis == "x" else hw)) < 1e-3
        for a, b in merge(ivs):
            walls.append(Wall(axis, c, a, b, ext))
    return walls


def subtract(rect, hole):
    x0, z0, x1, z1 = rect
    hx0, hz0, hx1, hz1 = hole
    if hx1 <= x0 or hx0 >= x1 or hz1 <= z0 or hz0 >= z1:
        return [rect]
    out = []
    if hz0 > z0: out.append([x0, z0, x1, hz0])
    if hz1 < z1: out.append([x0, hz1, x1, z1])
    zz0, zz1 = max(z0, hz0), min(z1, hz1)
    if hx0 > x0: out.append([x0, zz0, hx0, zz1])
    if hx1 < x1: out.append([hx1, zz0, x1, zz1])
    return out


def opening_span(kind):
    if kind in ("door", "arch"):
        return 0.0, DOOR_H if kind == "door" else DOOR_H + 0.05
    if kind == "small":
        return SMALL_SILL, HEAD
    return SILL, HEAD


class House:
    def __init__(self, name, plan):
        self.name, self.plan = name, plan
        self.W, self.D = plan["size"]
        self.hw, self.hd = self.W / 2, self.D / 2
        self.floor = plan.get("floor", FLOOR)
        self.n = plan["storeys"]
        self.base = [self.floor + s * STOREY_H for s in range(self.n)]
        self.plate = self.base[-1] + PLATE
        self.sink = Sink()
        self.walls = [wall_lines(r, self.hw, self.hd) for r in plan["rooms"]]
        self.data = {"name": name, "size": [self.W, self.D], "floor": self.floor, "storeys": self.n,
                     "storeyHeight": STOREY_H, "ceiling": CEIL, "doors": [], "panes": [], "rooms": [],
                     "furniture": [], "lights": []}
        self.rng = random.Random(name)

    # -- lookups
    def outside(self, p):
        return abs(p.x) > self.hw + EXT_T / 2 - 1e-3 or abs(p.z) > self.hd + EXT_T / 2 - 1e-3

    def storey_at(self, y):
        s = int((y - self.floor) // STOREY_H)
        return min(max(s, 0), self.n - 1)

    def room_at(self, p, s):
        for r in self.plan["rooms"][s]:
            x0, z0, x1, z1 = r["rect"]
            if x0 - 1e-4 <= p.x <= x1 + 1e-4 and z0 - 1e-4 <= p.z <= z1 + 1e-4:
                return r
        return None

    def in_wall(self, p, s):
        return any(w.contains(p.x, p.z) for w in self.walls[s])

    def wall_mat(self, s, wall):
        def f(c, n):
            trim = "Ext_Trim" if wall.ext else "Int_Trim"
            if abs(n.y) > 0.5:
                return trim
            p = c + n * 0.03
            if self.outside(p):
                return "Ext_Siding"
            if self.in_wall(p, s):
                return trim
            r = self.room_at(p, s)
            return FINISH[r["type"]][0] if r else "Int_Plaster"
        return f

    # -- build
    def build(self):
        self.assign_openings()
        for s in range(self.n):
            for w in self.walls[s]:
                self.build_wall(s, w)
            self.build_floor(s)
            self.build_baseboards(s)
            self.build_rooms_data(s)
        self.build_foundation()
        self.build_roof()
        self.build_porch()
        self.build_stairs()
        self.build_rails()
        self.build_chimney()
        self.build_steps()
        self.furnish()
        return self

    def assign_openings(self):
        for o in self.plan["openings"]:
            s, (x, z) = o["s"], o["at"]
            for w in self.walls[s]:
                if w.axis == "x" and abs(z - w.c) < 0.01 and w.a < x < w.b:
                    u = x
                elif w.axis == "z" and abs(x - w.c) < 0.01 and w.a < z < w.b:
                    u = z
                else:
                    continue
                y0, y1 = opening_span(o["kind"])
                w.openings.append({"u0": u - o["w"] / 2, "u1": u + o["w"] / 2, "y0": y0, "y1": y1, "kind": o["kind"], "u": u, "w": o["w"]})
                break
            else:
                raise ValueError(f"{self.name}: opening {o} is not on a wall")

    def wall_top(self, s, w):
        if not w.ext:
            return self.base[s] + CEIL
        return self.base[s] + (STOREY_H if s < self.n - 1 else PLATE)

    def piece(self, s, w, u0, u1, y0, y1, mat):
        h = w.t / 2
        if w.axis == "x":
            abox(self.sink, (u0, y0, w.c - h), (u1, y1, w.c + h), mat)
        else:
            abox(self.sink, (w.c - h, y0, u0), (w.c + h, y1, u1), mat)

    def build_wall(self, s, w):
        y0, y1 = self.base[s], self.wall_top(s, w)
        ext_end = EXT_T / 2 if w.ext else INT_T / 2
        lo, hi = w.a - ext_end, w.b + ext_end
        mat = self.wall_mat(s, w)
        cur = lo
        for o in sorted(w.openings, key=lambda o: o["u0"]):
            self.piece(s, w, cur, o["u0"], y0, y1, mat)
            if o["y0"] > 0:
                self.piece(s, w, o["u0"], o["u1"], y0, y0 + o["y0"], mat)
            self.piece(s, w, o["u0"], o["u1"], y0 + o["y1"], y1, mat)
            cur = o["u1"]
            self.build_opening(s, w, o)
        self.piece(s, w, cur, hi, y0, y1, mat)
        if w.ext and s == 0:
            self.corner_boards(w)

    def corner_boards(self, w):
        # Vertical trim at the outside corners, full height.
        if w.axis != "z":
            return
        top = self.plate if self.n == 1 else self.base[-1] + PLATE
        for zc in (w.a, w.b):
            sx, sz = (1 if w.c > 0 else -1), (1 if zc > 0 else -1)
            x = w.c + sx * (EXT_T / 2 + 0.012)
            z = zc + sz * (EXT_T / 2 + 0.012)
            abox(self.sink, (min(x, x - sx * 0.14), self.floor - 0.05, min(z, z - sz * 0.14)),
                 (max(x, x - sx * 0.14), top, max(z, z - sz * 0.14)), "Ext_Trim")

    def side_normals(self, w):
        """Unit normals of both faces of a wall, outside one first for outside walls."""
        n = Vector((0, 0, 1)) if w.axis == "x" else Vector((1, 0, 0))
        if w.ext and w.c < 0:
            n = -n
        return [n, -n]

    def build_opening(self, s, w, o):
        y0 = self.base[s]
        along = Vector((1, 0, 0)) if w.axis == "x" else Vector((0, 0, 1))
        mid_u = (o["u0"] + o["u1"]) / 2
        width, oy0, oy1 = o["u1"] - o["u0"], y0 + o["y0"], y0 + o["y1"]
        normals = self.side_normals(w)
        # Casings on each face: two jambs + head (+ sill / stool for windows)
        for k, n in enumerate(normals):
            outside = w.ext and k == 0
            trim = "Ext_Trim" if outside else "Int_Trim"
            cw = 0.1 if outside else 0.07
            proud = 0.022 if outside else 0.015
            def at(u, y, out):
                p = w.point(u, y)
                return p + n * (w.t / 2 + out)
            for side in (-1, 1):
                u = o["u0"] - cw / 2 if side < 0 else o["u1"] + cw / 2
                bot = oy0 - (0.06 if o["y0"] > 0 and outside else 0.0)
                top = oy1 + cw
                c = at(u, (bot + top) / 2, proud / 2)
                obox(self.sink, c, along, (0, 1, 0), n, cw / 2, (top - bot) / 2, proud / 2, trim, world_uv=True)
            c = at(mid_u, oy1 + cw / 2, proud / 2)
            obox(self.sink, c, along, (0, 1, 0), n, width / 2 + cw, cw / 2, proud / 2, trim, world_uv=True)
            if o["y0"] > 0:
                if outside:   # sloped-looking sill board sticking out
                    c = at(mid_u, oy0 - 0.03, 0.03)
                    obox(self.sink, c, along, (0, 1, 0), n, width / 2 + cw + 0.03, 0.03, 0.06, trim, world_uv=True)
                else:        # stool + apron
                    c = at(mid_u, oy0 - 0.015, 0.03)
                    obox(self.sink, c, along, (0, 1, 0), n, width / 2 + cw, 0.015, 0.045, trim, world_uv=True)
                    c = at(mid_u, oy0 - 0.08, proud / 2)
                    obox(self.sink, c, along, (0, 1, 0), n, width / 2 + cw * 0.6, 0.05, proud / 2, trim, world_uv=True)
        n_out = normals[0]
        yaw = math.degrees(math.atan2(n_out.x, n_out.z))
        centre = w.point(mid_u, 0)
        if o["kind"] in ("window", "small"):
            self.build_window(w, o, centre, along, n_out, oy0, oy1, yaw)
        else:
            self.data["doors"].append({
                "pos": list(w.point(mid_u, oy0)), "width": round(width, 4), "height": round(oy1 - oy0, 4),
                "yaw": round(yaw, 2), "exterior": w.ext, "hasDoor": o["kind"] == "door",
                "thickness": w.t, "storey": s})

    def build_window(self, w, o, centre, along, n, oy0, oy1, yaw):
        """Aluminium slider: a frame inside the opening and a meeting rail; two glass panes (the left one
        slightly outboard, as sliders are)."""
        width, height = o["u1"] - o["u0"], oy1 - oy0
        fw, fd = 0.035, 0.06
        mat = "Ext_WindowFrame"
        mid = (oy0 + oy1) / 2
        u_mid = (o["u0"] + o["u1"]) / 2
        def P(u, y):
            return w.point(u, y)
        obox(self.sink, P(u_mid, oy0 + fw / 2), along, (0, 1, 0), n, width / 2, fw / 2, fd / 2, mat)
        obox(self.sink, P(u_mid, oy1 - fw / 2), along, (0, 1, 0), n, width / 2, fw / 2, fd / 2, mat)
        obox(self.sink, P(o["u0"] + fw / 2, mid), along, (0, 1, 0), n, fw / 2, height / 2 - fw, fd / 2, mat)
        obox(self.sink, P(o["u1"] - fw / 2, mid), along, (0, 1, 0), n, fw / 2, height / 2 - fw, fd / 2, mat)
        panes = 2 if width > 0.65 else 1
        if panes == 2:
            obox(self.sink, P(u_mid, mid), along, (0, 1, 0), n, fw / 2, height / 2 - fw, fd / 2, mat)
        inner_w = (width - 2 * fw - (fw if panes == 2 else 0)) / panes
        for i in range(panes):
            u = o["u0"] + fw + inner_w / 2 + i * (inner_w + fw)
            off = (0.012 if i == 0 else -0.012) if panes == 2 else 0.0
            c = P(u, mid) + n * off
            self.data["panes"].append({"pos": list(c), "size": [round(inner_w, 4), round(height - 2 * fw, 4)],
                                       "yaw": round(yaw, 2), "exterior": w.ext})

    def build_floor(self, s):
        voids = self.plan.get("voids", [[]] * self.n)[s]
        top = self.base[s]
        for r in self.plan["rooms"][s]:
            rects = [r["rect"]]
            for v in voids:
                rects = [q for rr in rects for q in subtract(rr, v)]
            finish = FINISH[r["type"]][1]
            for x0, z0, x1, z1 in rects:
                if s == 0:
                    abox(self.sink, (x0, top - 0.06, z0), (x1, top, z1), lambda c, n, f=finish: f if n.y > 0.5 else "Int_Trim")
                else:
                    abox(self.sink, (x0, top - (STOREY_H - CEIL), z0), (x1, top, z1),
                         lambda c, n, f=finish: f if n.y > 0.5 else ("Int_Ceiling" if n.y < -0.5 else "Int_Trim"))
        # Ceiling under the roof
        if s == self.n - 1:
            for r in self.plan["rooms"][s]:
                x0, z0, x1, z1 = r["rect"]
                abox(self.sink, (x0, top + CEIL, z0), (x1, top + CEIL + 0.1, z1), "Int_Ceiling")

    def build_baseboards(self, s):
        y = self.base[s]
        h, th = 0.09, 0.012
        for r in self.plan["rooms"][s]:
            x0, z0, x1, z1 = r["rect"]
            if r["type"] in ("bath", "shack"):
                continue
            for side in "NSEW":
                if side in "NS":
                    zc = z1 if side == "N" else z0
                    w = self.wall_on("x", zc, (x0 + x1) / 2, s)
                    if w is None: continue
                    inset = (w.t / 2) * (-1 if side == "N" else 1)
                    lo, hi = x0 + INT_T / 2, x1 - INT_T / 2
                    cuts = [(o["u0"] - 0.07, o["u1"] + 0.07) for ww in self.walls[s] if ww.axis == "x" and abs(ww.c - zc) < 1e-3
                            for o in ww.openings if o["y0"] == 0]
                    for a, b in self.spans(lo, hi, cuts):
                        z = zc + inset
                        abox(self.sink, (a, y, min(z, z + math.copysign(th, inset))), (b, y + h, max(z, z + math.copysign(th, inset))), "Int_Trim")
                else:
                    xc = x1 if side == "E" else x0
                    w = self.wall_on("z", xc, (z0 + z1) / 2, s)
                    if w is None: continue
                    inset = (w.t / 2) * (-1 if side == "E" else 1)
                    lo, hi = z0 + INT_T / 2, z1 - INT_T / 2
                    cuts = [(o["u0"] - 0.07, o["u1"] + 0.07) for ww in self.walls[s] if ww.axis == "z" and abs(ww.c - xc) < 1e-3
                            for o in ww.openings if o["y0"] == 0]
                    for a, b in self.spans(lo, hi, cuts):
                        x = xc + inset
                        abox(self.sink, (min(x, x + math.copysign(th, inset)), y, a), (max(x, x + math.copysign(th, inset)), y + h, b), "Int_Trim")

    def wall_on(self, axis, c, u, s):
        for w in self.walls[s]:
            if w.axis == axis and abs(w.c - c) < 1e-3 and w.a - 1e-3 <= u <= w.b + 1e-3:
                return w
        return None

    @staticmethod
    def spans(lo, hi, cuts):
        out, cur = [], lo
        for a, b in sorted(cuts):
            if b <= cur or a >= hi:
                continue
            if a > cur: out.append((cur, a))
            cur = max(cur, b)
        if cur < hi: out.append((cur, hi))
        return out

    def build_rooms_data(self, s):
        for r in self.plan["rooms"][s]:
            x0, z0, x1, z1 = r["rect"]
            self.data["rooms"].append({"name": r["name"], "type": r["type"], "storey": s,
                                       "min": [x0, self.base[s], z0], "max": [x1, self.base[s] + CEIL, z1]})
            # A ceiling light per room (fixture model + where Unity may put a light), in the biggest part of the
            # room that isn't open to a stairwell (on this storey or the one above)
            voids = self.plan.get("voids", [[]] * self.n)
            parts = [r["rect"]]
            for v in voids[s] + (voids[s + 1] if s + 1 < self.n else []):
                parts = [q for p in parts for q in subtract(p, v)]
            if not parts:
                continue
            px0, pz0, px1, pz1 = max(parts, key=lambda q: (q[2] - q[0]) * (q[3] - q[1]))
            cx, cz = (px0 + px1) / 2, (pz0 + pz1) / 2
            y = self.base[s] + CEIL
            cylinder(self.sink, (cx, y - 0.08, cz), 0.16, 0.08, "Furn_White", seg=10)
            self.data["lights"].append({"pos": [cx, y - 0.12, cz], "room": r["name"]})

    def build_foundation(self):
        hw, hd = self.hw + EXT_T / 2, self.hd + EXT_T / 2
        if self.plan.get("roof") == "shed":   # shack: plank rim on concrete blocks
            abox(self.sink, (-hw, self.floor - 0.22, -hd), (hw, self.floor - 0.06, hd), "Ext_Siding")
            for x in (-hw + 0.2, 0, hw - 0.2):
                for z in (-hd + 0.2, hd - 0.2):
                    abox(self.sink, (x - 0.19, -0.3, z - 0.19), (x + 0.19, self.floor - 0.22, z + 0.19), "Ext_Foundation")
        else:
            abox(self.sink, (-hw, -0.4, -hd), (hw, self.floor - 0.06, hd), "Ext_Foundation")

    # -- roofs
    def roof_frame(self):
        """Roof-local axes: u along the ridge, v across it. Returns (u half length incl. walls, v half span,
        to_plan(u, y, v))."""
        if self.plan.get("ridge", "x") == "z":
            return self.hd + EXT_T / 2, self.hw + EXT_T / 2, (lambda u, y, v: Vector((v, y, u)))
        return self.hw + EXT_T / 2, self.hd + EXT_T / 2, (lambda u, y, v: Vector((u, y, v)))

    def roof_mat(self, up):
        def f(c, n):
            d = n.dot(up)
            if d > 0.7: return "Ext_Roof"
            if d < -0.7: return "Ext_Soffit"
            return "Ext_Trim"
        return f

    def build_roof(self):
        a = math.atan(self.plan["pitch"])
        hu, hv, P = self.roof_frame()
        top = self.plate
        L = hu + RAKE_OV
        if self.plan["roof"] == "shed":
            # One slope, high at the back (-v), low at the front; walls under it are topped up.
            rise = 2 * hv * math.tan(a)
            d = (P(0, -math.sin(a), 1) - P(0, 0, 0)).normalized()          # down the slope, toward the front
            nrm = (P(0, math.cos(a), math.sin(a)) - P(0, 0, 0)).normalized()
            start = P(0, top + rise + EAVE_OV * math.tan(a), -hv - EAVE_OV)
            length = (2 * hv + 2 * EAVE_OV) / math.cos(a)
            ex = (P(1, 0, 0) - P(0, 0, 0)).normalized()
            obox(self.sink, start + d * length / 2 + nrm * ROOF_T / 2, ex, nrm, d, L, ROOF_T / 2, length / 2, self.roof_mat(nrm))
            for su in (-1, 1):   # side wall tops: triangles
                poly = [P(su * hu - EXT_T / 2 * su, top, hv), P(su * hu - EXT_T / 2 * su, top, -hv), P(su * hu - EXT_T / 2 * su, top + rise, -hv)]
                prism(self.sink, poly, (P(su * EXT_T, 0, 0) - P(0, 0, 0)), "Ext_Siding")
            # back wall top-up
            b0, b1 = P(-hu, top, -hv), P(hu, top + rise, -hv + EXT_T)
            abox(self.sink, Vector(map(min, b0, b1)), Vector(map(max, b0, b1)), "Ext_Siding")
            self.data["ridge"] = top + rise
            return
        rise = hv * math.tan(a)
        ridge = top + rise
        length = (hv + EAVE_OV) / math.cos(a)
        ex = (P(1, 0, 0) - P(0, 0, 0)).normalized()
        for sv in (-1, 1):
            d = (P(0, -math.sin(a), sv * math.cos(a)) - P(0, 0, 0)).normalized()
            nrm = (P(0, math.cos(a), sv * math.sin(a)) - P(0, 0, 0)).normalized()
            start = P(0, ridge, 0)
            obox(self.sink, start + d * length / 2 + nrm * ROOF_T / 2, ex, nrm, d, L, ROOF_T / 2, length / 2, self.roof_mat(nrm))
            # fascia board along the eave
            eave = start + d * length
            obox(self.sink, eave + nrm * (ROOF_T / 2) + d * 0.012, ex, (0, 1, 0), d, L + 0.01, ROOF_T * 0.6, 0.012, "Ext_Trim")
        # ridge cap, bedded into both slopes
        c0 = P(-L, ridge + ROOF_T * math.cos(a) - 0.05, -0.2)
        c1 = P(L, ridge + ROOF_T / math.cos(a) + 0.02, 0.2)
        abox(self.sink, Vector(map(min, c0, c1)), Vector(map(max, c0, c1)), "Ext_Roof")
        # Gable ends: siding triangles on top of the end walls
        for su in (-1, 1):
            uo = su * (hu - EXT_T / 2)
            poly = [P(uo, top, -hv), P(uo, top, hv), P(uo, ridge, 0)]
            prism(self.sink, poly, (P(-su * EXT_T, 0, 0) - P(0, 0, 0)), "Ext_Siding")
            # gable vent (louvre) near the peak
            vy = top + rise * 0.55
            if rise > 0.9:
                c = P(su * (hu + 0.01), vy, 0)
                n = (P(su, 0, 0) - P(0, 0, 0)).normalized()
                along = (P(0, 0, 1) - P(0, 0, 0)).normalized()
                obox(self.sink, c, along, (0, 1, 0), n, 0.25, 0.18, 0.02, "Ext_Trim")
        self.data["ridge"] = ridge

    def build_porch(self):
        pc = self.plan.get("porch")
        if not pc:
            return
        x0, x1 = pc["x"]
        z0 = self.hd + EXT_T / 2
        z1 = z0 + pc["depth"]
        deck = self.floor - 0.03
        deckmat = lambda c, n: "Ext_Deck" if n.y > 0.5 else "Ext_Trim"
        abox(self.sink, (x0, deck - 0.2, z0), (x1, deck, z1), deckmat)
        abox(self.sink, (x0 + 0.05, -0.3, z0), (x1 - 0.05, deck - 0.2, z1 - 0.05), "Ext_Foundation")
        # Roof: low shed off the front wall, kept under the main eave
        two = self.n > 1
        b = math.atan(3 / 12 if two else 1.5 / 12)
        if two:
            attach = self.base[1] - 0.15
        elif self.plan.get("ridge", "x") == "z":
            attach = self.plate - 0.1
        else:
            a = math.atan(self.plan["pitch"])
            attach = self.plate - PORCH_T / math.cos(b) - EAVE_OV * (math.tan(a) - math.tan(b)) - 0.02
        ov = 0.3
        run = pc["depth"] + ov
        length = run / math.cos(b)
        d = Vector((0, -math.sin(b), math.cos(b)))
        nrm = Vector((0, math.cos(b), math.sin(b)))
        start = Vector(((x0 + x1) / 2, attach, z0))
        obox(self.sink, start + d * length / 2 + nrm * PORCH_T / 2, (1, 0, 0), nrm, d, (x1 - x0) / 2 + 0.2, PORCH_T / 2,
             length / 2, self.roof_mat(nrm))
        # beam + posts at the front edge
        beam_y = attach - pc["depth"] * math.tan(b) - 0.02
        abox(self.sink, (x0 - 0.05, beam_y - 0.18, z1 - 0.2), (x1 + 0.05, beam_y, z1 - 0.06), "Ext_Trim")
        n_posts = max(2, int(round((x1 - x0) / 2.4)) + 1)
        for i in range(n_posts):
            x = x0 + 0.13 + (x1 - x0 - 0.26) * i / (n_posts - 1)
            abox(self.sink, (x - 0.07, deck, z1 - 0.2), (x + 0.07, beam_y - 0.18, z1 - 0.06), "Ext_Trim")
        # simple railing between posts except at the steps
        sx = pc.get("steps_x")
        rail_y = deck + 0.85
        segs = self.spans(x0 + 0.2, x1 - 0.2, [(sx - 0.7, sx + 0.7)] if sx is not None else [])
        for a_, b_ in segs:
            abox(self.sink, (a_, rail_y - 0.06, z1 - 0.17), (b_, rail_y, z1 - 0.09), "Ext_Trim")
            abox(self.sink, (a_, deck + 0.08, z1 - 0.16), (b_, deck + 0.14, z1 - 0.1), "Ext_Trim")
            k = int((b_ - a_) / 0.14)
            for i in range(1, k):
                x = a_ + (b_ - a_) * i / k
                abox(self.sink, (x - 0.02, deck + 0.14, z1 - 0.15), (x + 0.02, rail_y - 0.06, z1 - 0.11), "Ext_Trim")
        self.porch_edge = (z1, deck)

    def build_stairs(self):
        for st in self.plan.get("stairs", []):
            s, (x0, x1), zb, dr, n = st["s"], st["x"], st["z_bottom"], st["dir"], st["treads"]
            y0 = self.base[s]
            rise = STOREY_H / (n + 1)
            run = 0.27
            mat = lambda c, nn: "Int_Stair" if nn.y > 0.5 else "Int_Trim"
            for i in range(1, n + 1):
                za, zb_ = zb + dr * (i - 1) * run, zb + dr * i * run
                abox(self.sink, (x0, y0, min(za, zb_)), (x1, y0 + i * rise, max(za, zb_)), mat)
            self.ramp(Vector(((x0 + x1) / 2, y0, zb)), Vector(((x0 + x1) / 2, y0 + STOREY_H, zb + dr * n * run)), x1 - x0)

    def ramp(self, low, high, width):
        """Invisible collision slope from low to high (both on the centreline, at nosing height)."""
        d = high - low
        flat = Vector((d.x, 0, d.z)).normalized()
        side = Vector((flat.z, 0, -flat.x))
        dn = d.normalized()
        nrm = dn.cross(side)
        if nrm.y < 0:
            nrm = -nrm
        c = (low + high) / 2 - nrm * 0.04
        obox(self.sink, c, side, nrm, dn, width / 2, 0.04, d.length / 2 + 0.05, "Ramp")

    def build_rails(self):
        for s, rails in enumerate(self.plan.get("rails", [])):
            y = self.base[s]
            for x0, z0, x1, z1 in rails:
                horiz = abs(z1 - z0) < 1e-3
                a, b = (x0, x1) if horiz else (z0, z1)
                def bx(u0, u1, y0, y1, th):
                    if horiz:
                        abox(self.sink, (u0, y0, z0 - th), (u1, y1, z0 + th), "Int_Trim")
                    else:
                        abox(self.sink, (x0 - th, y0, u0), (x0 + th, y1, u1), "Int_Trim")
                bx(a, b, y + 0.86, y + 0.92, 0.04)
                k = int((b - a) / 0.12)
                for i in range(k + 1):
                    u = a + (b - a) * i / max(k, 1)
                    th = 0.045 if i in (0, k) else 0.018
                    bx(u - th, u + th, y, y + 0.88, th)

    def build_chimney(self):
        kind = self.plan.get("chimney")
        if not kind:
            return
        ridge = self.data.get("ridge", self.plate + 1)
        if kind == "stovepipe":
            # Through the roof above the wood stove (stoves stand on the west wall)
            hu, hv, P = self.roof_frame()
            x, z = -self.hw + 0.5, (0.0 if self.n else 0)
            stove = next((f for f in self.plan["furniture"] if f.get("item") == "woodstove"), None)
            if stove:
                r = next(r for r in self.plan["rooms"][0] if r["name"] == stove["room"])
                x0, z0, x1, z1 = r["rect"]
                z = z0 + (z1 - z0) * stove["t"]
                x = x0 + 0.45
            cylinder(self.sink, (x, self.base[0] + CEIL, z), 0.08, ridge + 0.9 - self.base[0] - CEIL, "Ext_Metal", seg=10)
            cylinder(self.sink, (x, ridge + 0.85, z), 0.14, 0.06, "Ext_Metal", seg=10)
        else:
            # Brick chimney up the outside of the west gable
            x = -self.hw - EXT_T / 2 - 0.35
            abox(self.sink, (x - 0.35, -0.3, -0.45), (x + 0.35, ridge + 0.6, 0.45), "Ext_Brick")
            abox(self.sink, (x - 0.4, ridge + 0.6, -0.5), (x + 0.4, ridge + 0.7, 0.5), "Ext_Brick")

    def build_steps(self):
        steps = []
        pc = self.plan.get("porch")
        if pc and pc.get("steps_x") is not None:
            z1, deck = self.porch_edge
            steps.append((pc["steps_x"], z1, 1, deck, 1.3))
        if "back_steps_x" in self.plan:
            steps.append((self.plan["back_steps_x"], -self.hd - EXT_T / 2, -1, self.floor - 0.03, 1.1))
        if "stoop_x" in self.plan:
            steps.append((self.plan["stoop_x"], self.hd + EXT_T / 2, 1, self.floor - 0.03, 1.1))
        stepmat = lambda c, n: "Ext_Deck" if n.y > 0.5 else "Ext_Trim"
        self.data["steps"] = []
        for x, z, dr, top, width in steps:
            if "porch" not in self.plan or dr < 0 or pc is None or pc.get("steps_x") != x:
                # landing outside a door with no porch
                abox(self.sink, (x - width / 2, top - 0.2, min(z, z + dr * 0.9)), (x + width / 2, top, max(z, z + dr * 0.9)), stepmat)
                z = z + dr * 0.9
            n = max(1, math.ceil(top / RISE) - 1)
            rise = top / (n + 1)
            for i in range(n):
                y = top - (i + 1) * rise
                za, zb = z + dr * i * TREAD, z + dr * (i + 1) * TREAD
                abox(self.sink, (x - width / 2, -0.25, min(za, zb)), (x + width / 2, y, max(za, zb)), stepmat)
            self.ramp(Vector((x, 0.0, z + dr * n * TREAD)), Vector((x, top, z)), width)
            self.data["steps"].append({"x": x, "z": z + dr * n * TREAD})

    # -- furniture
    def furnish(self):
        rooms = {r["name"]: (r, s) for s, rs in enumerate(self.plan["rooms"]) for r in rs}
        for f in self.plan["furniture"]:
            r, s = rooms[f["room"]]
            x0, z0, x1, z1 = r["rect"]
            y = self.base[s]
            p = f.get("p", 1.0)
            if "wall" in f:
                items = f["run"] if "run" in f else [f["item"]]
                side = f["wall"]
                # inside faces of the room's walls
                ix0, ix1 = x0 + self.half_t(s, "z", x0, (z0 + z1) / 2), x1 - self.half_t(s, "z", x1, (z0 + z1) / 2)
                iz0, iz1 = z0 + self.half_t(s, "x", z0, (x0 + x1) / 2), z1 - self.half_t(s, "x", z1, (x0 + x1) / 2)
                if side in "NS":
                    lo, hi = ix0, ix1
                else:
                    lo, hi = iz0, iz1
                widths = [self.item_width(it) for it in items]
                total = sum(widths)
                if "run" in f:
                    cur = lo + (hi - lo) * f["t"]
                else:
                    cur = lo + (hi - lo) * f["t"] - total / 2
                    cur = min(max(cur, lo), hi - total)
                for it, wdt in zip(items, widths):
                    u = cur + wdt / 2
                    cur += wdt
                    if it.startswith("gap"):
                        continue
                    depth = KIT[it]["size"][2]
                    if side == "N":   pos, yaw = (u, iz1 - depth / 2), 180
                    elif side == "S": pos, yaw = (u, iz0 + depth / 2), 0
                    elif side == "E": pos, yaw = (ix1 - depth / 2, u), -90
                    else:             pos, yaw = (ix0 + depth / 2, u), 90
                    self.place(it, pos, y, yaw, p, f["room"])
            else:
                fx, fz = f["at"]
                pos = (x0 + (x1 - x0) * fx, z0 + (z1 - z0) * fz)
                self.place(f["item"], pos, y, f.get("yaw", 0), p, f["room"])

    def half_t(self, s, axis, c, u):
        w = self.wall_on(axis, c, u, s)
        return w.t / 2 if w else 0.0

    @staticmethod
    def item_width(it):
        if it.startswith("gap"):
            return float(it.split(":")[1]) if ":" in it else 0.6
        return KIT[it]["size"][0]

    def place(self, item, pos, y, yaw, p, room):
        self.data["furniture"].append({"item": item, "pos": [round(pos[0], 4), round(y + KIT[item].get("y", 0.0), 4), round(pos[1], 4)],
                                       "yaw": yaw, "chance": p, "room": room})

    def check(self):
        """Warns about furniture overlapping each other, doorways or windows (tall pieces)."""
        boxes = []
        for f in self.data["furniture"]:
            w, h, d = KIT[f["item"]]["size"]
            if abs(f["yaw"]) % 180 == 90:
                w, d = d, w
            x, y, z = f["pos"]
            boxes.append((f["item"], f["room"], x - w / 2, z - d / 2, x + w / 2, z + d / 2, y, y + h))
        for i, a in enumerate(boxes):
            for b in boxes[i + 1:]:
                if "rug" in (a[0], b[0]) or ({a[0], b[0]} & {"chair"} and ("table" in a[0] or "table" in b[0])):
                    continue
                if a[2] < b[4] - 0.02 and b[2] < a[4] - 0.02 and a[3] < b[5] - 0.02 and b[3] < a[5] - 0.02 and a[6] < b[7] and b[6] < a[7]:
                    print(f"  WARN {self.name}: {a[0]} ({a[1]}) overlaps {b[0]} ({b[1]})")
            for dr in self.data["doors"]:
                x, _, z = dr["pos"]
                r = dr["width"] / 2 + 0.5
                if a[2] < x + r and x - r < a[4] and a[3] < z + r and z - r < a[5] and a[0] != "rug" and abs(dr["pos"][1] - a[6]) < 1:
                    # door swing / passage zone: square of the door width either side
                    yaw = math.radians(dr["yaw"])
                    nx, nz = math.sin(yaw), math.cos(yaw)
                    # distance across the wall
                    across = abs((a[2] + a[4]) / 2 - x) * abs(nx) + abs((a[3] + a[5]) / 2 - z) * abs(nz)
                    along = abs((a[2] + a[4]) / 2 - x) * abs(nz) + abs((a[3] + a[5]) / 2 - z) * abs(nx)
                    ext = (a[4] - a[2]) * abs(nz) + (a[5] - a[3]) * abs(nx)
                    if along < dr["width"] / 2 + ext / 2 - 0.05 and across < 0.95:
                        print(f"  WARN {self.name}: {a[0]} ({a[1]}) blocks a doorway at {x:.2f},{z:.2f}")
            if a[7] - a[6] > 1.0 and a[0] != "sink":
                for pn in self.data["panes"]:
                    x, py, z = pn["pos"]
                    if a[2] - 0.15 < x < a[4] + 0.15 and a[3] - 0.15 < z < a[5] + 0.15 and a[6] < py < a[7] + 0.6:
                        print(f"  WARN {self.name}: {a[0]} ({a[1]}) stands in front of a window")

    def export(self):
        os.makedirs(OUT_MODELS, exist_ok=True); os.makedirs(OUT_PLANS, exist_ok=True); os.makedirs(OUT_BLEND, exist_ok=True)
        bpy.ops.wm.read_factory_settings(use_empty=True)
        col = bpy.context.scene.collection
        tris = {}
        for name, mesh in self.sink.meshes.items():
            if mesh.faces:
                ob = mesh.to_object(name, col)
                tris[name] = sum(len(f[0]) - 2 for f in mesh.faces)
        fbx = f"{OUT_MODELS}/House_{self.name}.fbx"
        bpy.ops.export_scene.fbx(filepath=fbx, use_selection=False, object_types={"MESH"}, apply_unit_scale=True,
                                 apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                                 bake_space_transform=True, mesh_smooth_type="FACE", path_mode="STRIP",
                                 embed_textures=False, add_leaf_bones=False)
        bpy.ops.wm.save_as_mainfile(filepath=f"{OUT_BLEND}/House_{self.name}.blend")
        self.data["triangles"] = tris
        with open(f"{OUT_PLANS}/House_{self.name}.json", "w") as fh:
            json.dump(self.data, fh, indent=1)
        print(f"HOUSE {self.name}: tris {tris}, {len(self.data['doors'])} doorways, {len(self.data['panes'])} panes, "
              f"{len(self.data['furniture'])} furniture")


# ---------------------------------------------------------------- kit: doors + furniture
# Each item: size (w, h, d) footprint for placement; y = mount height; build(sink) draws it standing on its origin
# facing +z (its front / the side you use).
def k_box(s, x0, y0, z0, x1, y1, z1, m):
    abox(s, (x0, y0, z0), (x1, y1, z1), m)


def legs(s, w, d, h, t, m, inset=0.03):
    for sx in (-1, 1):
        for sz in (-1, 1):
            x, z = sx * (w / 2 - inset - t / 2), sz * (d / 2 - inset - t / 2)
            k_box(s, x - t / 2, 0, z - t / 2, x + t / 2, h, z + t / 2, m)


def door_slab(s, panel):
    """Unit door, 1 m wide x 1 m tall, hinge at x = 0, slab spanning x 0..1 (Unity scales it to the opening)."""
    t = 0.04
    m = "Door_Paint"
    k_box(s, 0.005, 0.005, -t / 2, 0.995, 0.995, t / 2, m)
    if panel:   # raised panels both sides (six-panel style)
        for side in (-1, 1):
            z = side * (t / 2 + 0.004)
            for col in (0, 1):
                for row, (y0, y1) in enumerate(((0.06, 0.36), (0.42, 0.62), (0.68, 0.94))):
                    x0 = 0.12 + col * 0.41
                    k_box(s, x0, y0, min(z, z - side * 0.008), x0 + 0.35, y1, max(z, z - side * 0.008), m)
    for side in (-1, 1):   # knob (stretches with the door, so it's kept small)
        cylinder(s, (0.9, 0.47, side * t / 2), 0.03, side * 0.05, "Door_Brass", seg=10, axis="z")


def b_bed(s, w, metal=False):
    d = 2.0
    if metal:
        for sx in (-1, 1):
            for z, top in ((-d / 2 + 0.03, 1.0), (d / 2 - 0.03, 0.75)):
                cylinder(s, (sx * (w / 2 - 0.03), 0, z), 0.02, top, "Furn_Metal", seg=8)
            k_box(s, -w / 2, 0.85, -d / 2 + 0.01, w / 2, 0.88, -d / 2 + 0.05, "Furn_Metal")
            k_box(s, -w / 2, 0.62, d / 2 - 0.05, w / 2, 0.65, d / 2 - 0.01, "Furn_Metal")
        k_box(s, -w / 2 + 0.02, 0.28, -d / 2 + 0.05, w / 2 - 0.02, 0.32, d / 2 - 0.05, "Furn_Metal")
        base = 0.32
    else:
        k_box(s, -w / 2, 0.08, -d / 2 + 0.06, w / 2, 0.32, d / 2, "Furn_Wood")
        legs(s, w, d - 0.06, 0.08, 0.06, "Furn_Wood")
        k_box(s, -w / 2 - 0.02, 0, -d / 2, w / 2 + 0.02, 1.0, -d / 2 + 0.06, "Furn_Wood")
        base = 0.32
    k_box(s, -w / 2 + 0.03, base, -d / 2 + 0.07, w / 2 - 0.03, base + 0.2, d / 2 - 0.03, "Furn_Mattress")
    k_box(s, -w / 2 + 0.01, base + 0.12, -d / 2 + 0.55, w / 2 - 0.01, base + 0.23, d / 2 - 0.01, "Furn_Blanket")
    pillows = 2 if w > 1.2 else 1
    pw = (w - 0.2) / pillows
    for i in range(pillows):
        x = -w / 2 + 0.1 + pw * i
        k_box(s, x + 0.03, base + 0.2, -d / 2 + 0.12, x + pw - 0.03, base + 0.3, -d / 2 + 0.48, "Furn_Mattress")


def b_nightstand(s):
    k_box(s, -0.22, 0.05, -0.2, 0.22, 0.55, 0.2, "Furn_Wood")
    legs(s, 0.44, 0.4, 0.05, 0.04, "Furn_Wood", 0.01)
    k_box(s, -0.18, 0.38, 0.2, 0.18, 0.5, 0.21, "Furn_DarkWood")
    cylinder(s, (0, 0.55, 0), 0.08, 0.04, "Furn_Metal", seg=10)          # lamp
    cylinder(s, (0, 0.59, 0), 0.015, 0.25, "Furn_Metal", seg=6)
    cylinder(s, (0, 0.78, 0), 0.12, 0.18, "Furn_White", seg=10)


def b_dresser(s):
    w, d, h = 1.2, 0.5, 0.85
    k_box(s, -w / 2, 0.07, -d / 2, w / 2, h, d / 2, "Furn_Wood")
    k_box(s, -w / 2 + 0.03, 0, -d / 2 + 0.03, w / 2 - 0.03, 0.07, d / 2 - 0.03, "Furn_DarkWood")
    for i in range(3):
        y0 = 0.1 + i * 0.25
        k_box(s, -w / 2 + 0.04, y0, d / 2, w / 2 - 0.04, y0 + 0.22, d / 2 + 0.012, "Furn_Wood")
        for x in (-0.3, 0.3):
            k_box(s, x - 0.05, y0 + 0.1, d / 2 + 0.012, x + 0.05, y0 + 0.12, d / 2 + 0.03, "Furn_Metal")


def b_wardrobe(s):
    w, d, h = 1.0, 0.58, 2.0
    k_box(s, -w / 2, 0.05, -d / 2, w / 2, h, d / 2, "Furn_Wood")
    k_box(s, -w / 2 + 0.02, 0, -d / 2 + 0.02, w / 2 - 0.02, 0.05, d / 2 - 0.02, "Furn_DarkWood")
    for sx in (-1, 1):
        k_box(s, sx * 0.005, 0.1, d / 2, sx * (w / 2 - 0.03), h - 0.05, d / 2 + 0.012, "Furn_Wood")
        k_box(s, sx * 0.05 - 0.01, 0.95, d / 2 + 0.012, sx * 0.05 + 0.01, 1.15, d / 2 + 0.03, "Furn_Metal")


def b_couch(s, w=2.0):
    d, m = 0.9, "Furn_Fabric"
    k_box(s, -w / 2, 0.1, -d / 2, w / 2, 0.42, d / 2, m)
    legs(s, w, d, 0.1, 0.05, "Furn_DarkWood", 0.04)
    k_box(s, -w / 2, 0.42, -d / 2, w / 2, 0.85, -d / 2 + 0.22, m)               # back
    for sx in (-1, 1):
        k_box(s, sx * w / 2 - (0.18 if sx > 0 else 0), 0.42, -d / 2, sx * w / 2 + (0 if sx > 0 else 0.18), 0.62, d / 2, m)
    n = 3 if w > 1.5 else 1
    cw = (w - 0.36) / n
    for i in range(n):
        x = -w / 2 + 0.18 + cw * i
        k_box(s, x + 0.01, 0.42, -d / 2 + 0.22, x + cw - 0.01, 0.55, d / 2 - 0.02, m)
        k_box(s, x + 0.02, 0.55, -d / 2 + 0.2, x + cw - 0.02, 0.82, -d / 2 + 0.34, m)


def b_coffee_table(s):
    k_box(s, -0.55, 0.4, -0.3, 0.55, 0.45, 0.3, "Furn_Wood")
    legs(s, 1.1, 0.6, 0.4, 0.05, "Furn_Wood")
    k_box(s, -0.5, 0.12, -0.25, 0.5, 0.14, 0.25, "Furn_Wood")


def b_tv_stand(s):
    k_box(s, -0.5, 0.05, -0.22, 0.5, 0.5, 0.22, "Furn_DarkWood")
    k_box(s, -0.46, 0.08, 0.22, -0.02, 0.46, 0.232, "Furn_DarkWood")
    k_box(s, 0.02, 0.08, 0.22, 0.46, 0.46, 0.232, "Furn_DarkWood")
    k_box(s, -0.3, 0.5, -0.22, 0.3, 0.98, 0.2, "Furn_Black")                    # CRT body
    k_box(s, -0.23, 0.56, 0.2, 0.21, 0.92, 0.215, "Furn_Mirror")                 # screen
    k_box(s, -0.2, 0.52, -0.42, 0.2, 0.9, -0.22, "Furn_Black")                   # tube bulge
    k_box(s, 0.22, 0.6, 0.2, 0.27, 0.85, 0.212, "Furn_Metal")                    # knobs panel
    k_box(s, -0.22, 0.42, 0.06, 0.22, 0.5, 0.2, "Furn_Black")                    # VCR on the shelf


def b_dining_table(s, w=1.4, d=0.85):
    k_box(s, -w / 2, 0.71, -d / 2, w / 2, 0.75, d / 2, "Furn_Wood")
    k_box(s, -w / 2 + 0.06, 0.62, -d / 2 + 0.06, w / 2 - 0.06, 0.71, d / 2 - 0.06, "Furn_Wood")
    legs(s, w, d, 0.71, 0.06, "Furn_Wood", 0.06)


def b_table_rough(s):
    k_box(s, -0.45, 0.72, -0.35, 0.45, 0.77, 0.35, "Furn_DarkWood")
    legs(s, 0.9, 0.7, 0.72, 0.08, "Furn_DarkWood", 0.04)
    k_box(s, -0.38, 0.2, -0.02, 0.38, 0.26, 0.02, "Furn_DarkWood")


def b_chair(s):
    k_box(s, -0.22, 0.42, -0.22, 0.22, 0.46, 0.22, "Furn_Wood")
    legs(s, 0.44, 0.44, 0.42, 0.04, "Furn_Wood", 0.01)
    for sx in (-1, 1):
        k_box(s, sx * 0.2 - 0.02, 0.46, -0.22, sx * 0.2 + 0.02, 0.92, -0.18, "Furn_Wood")
    k_box(s, -0.2, 0.72, -0.22, 0.2, 0.88, -0.19, "Furn_Wood")


def b_armchair(s):
    b_couch(s, 0.9)


def b_counter(s, kind="counter"):
    w, d = 0.6, 0.62
    k_box(s, -w / 2, 0.1, -d / 2, w / 2, 0.86, d / 2 - 0.04, "Furn_Wood")
    k_box(s, -w / 2 + 0.01, 0, -d / 2, w / 2 - 0.01, 0.1, d / 2 - 0.1, "Furn_Black")       # toe kick
    k_box(s, -w / 2, 0.86, -d / 2, w / 2, 0.9, d / 2, "Furn_Counter")
    k_box(s, -w / 2 + 0.02, 0.12, d / 2 - 0.04, w / 2 - 0.02, 0.66, d / 2 - 0.025, "Furn_Wood")   # door
    k_box(s, -w / 2 + 0.02, 0.69, d / 2 - 0.04, w / 2 - 0.02, 0.83, d / 2 - 0.025, "Furn_Wood")   # drawer
    k_box(s, -0.06, 0.75, d / 2 - 0.025, 0.06, 0.77, d / 2 - 0.01, "Furn_Metal")
    k_box(s, -w / 2, 0.9, -d / 2, w / 2, 1.0, -d / 2 + 0.015, "Furn_Counter")             # backsplash
    if kind == "sink":
        k_box(s, -0.22, 0.75, -0.18, 0.22, 0.905, 0.2, "Furn_Metal")
        cylinder(s, (0, 0.9, -0.24), 0.02, 0.22, "Furn_Metal", seg=8)
        k_box(s, -0.015, 1.08, -0.24, 0.015, 1.11, -0.08, "Furn_Metal")


def b_upper(s):
    w, d, h = 0.6, 0.33, 0.75
    k_box(s, -w / 2, 0, -d / 2, w / 2, h, d / 2 - 0.02, "Furn_Wood")
    k_box(s, -w / 2 + 0.015, 0.015, d / 2 - 0.02, w / 2 - 0.015, h - 0.015, d / 2, "Furn_Wood")
    k_box(s, w / 2 - 0.08, 0.05, d / 2, w / 2 - 0.065, 0.17, d / 2 + 0.015, "Furn_Metal")


def b_stove(s):
    w, d = 0.76, 0.66
    k_box(s, -w / 2, 0, -d / 2, w / 2, 0.92, d / 2, "Furn_White")
    k_box(s, -w / 2 + 0.06, 0.15, d / 2, w / 2 - 0.06, 0.7, d / 2 + 0.01, "Furn_Black")    # oven window
    k_box(s, -w / 2 + 0.06, 0.74, d / 2, w / 2 - 0.06, 0.77, d / 2 + 0.04, "Furn_Metal")    # handle
    k_box(s, -w / 2, 0.92, -d / 2, w / 2, 1.12, -d / 2 + 0.06, "Furn_White")                # backguard
    for x in (-0.18, 0.18):
        for z in (-0.12, 0.15):
            cylinder(s, (x, 0.92, z), 0.09, 0.012, "Furn_Black", seg=10)


def b_fridge(s):
    w, d, h = 0.8, 0.72, 1.75
    k_box(s, -w / 2, 0.02, -d / 2, w / 2, h, d / 2 - 0.04, "Furn_White")
    k_box(s, -w / 2 + 0.005, 1.2, d / 2 - 0.04, w / 2 - 0.005, h - 0.01, d / 2, "Furn_White")
    k_box(s, -w / 2 + 0.005, 0.04, d / 2 - 0.04, w / 2 - 0.005, 1.18, d / 2, "Furn_White")
    for y0, y1 in ((1.3, 1.6), (0.75, 1.1)):
        k_box(s, -w / 2 + 0.05, y0, d / 2, -w / 2 + 0.08, y1, d / 2 + 0.04, "Furn_Metal")


def b_toilet(s):
    k_box(s, -0.2, 0.42, -0.36, 0.2, 0.78, -0.18, "Furn_Porcelain")          # tank
    cylinder(s, (0, 0, 0.0), 0.15, 0.4, "Furn_Porcelain", seg=12)            # bowl base
    k_box(s, -0.18, 0.3, -0.18, 0.18, 0.4, 0.2, "Furn_Porcelain")
    cylinder(s, (0, 0.4, 0.04), 0.19, 0.03, "Furn_White", seg=12)            # seat


def b_tub(s):
    w, d, h = 1.52, 0.76, 0.55
    m = "Furn_Porcelain"
    k_box(s, -w / 2, 0, -d / 2, w / 2, 0.08, d / 2, m)
    k_box(s, -w / 2, 0.08, -d / 2, w / 2, h, -d / 2 + 0.08, m)
    k_box(s, -w / 2, 0.08, d / 2 - 0.08, w / 2, h, d / 2, m)
    k_box(s, -w / 2, 0.08, -d / 2 + 0.08, -w / 2 + 0.1, h, d / 2 - 0.08, m)
    k_box(s, w / 2 - 0.1, 0.08, -d / 2 + 0.08, w / 2, h, d / 2 - 0.08, m)
    cylinder(s, (-w / 2 + 0.06, 0.62, 0), 0.02, 0.18, "Furn_Metal", seg=8, axis="x")


def b_shower(s):
    w = 0.9
    k_box(s, -w / 2, 0, -w / 2, w / 2, 0.1, w / 2, "Furn_Porcelain")
    for sx in (-1, 1):
        k_box(s, sx * w / 2 - 0.02, 0.1, -w / 2, sx * w / 2 + 0.02, 1.95, w / 2, "Furn_Porcelain")
    k_box(s, -w / 2, 1.9, w / 2 - 0.03, w / 2, 1.93, w / 2, "Furn_Metal")    # curtain rod
    k_box(s, -w / 2 + 0.05, 0.5, w / 2 - 0.06, -0.05, 1.85, w / 2 - 0.04, "Furn_Blanket")  # curtain
    cylinder(s, (0, 1.75, -w / 2 + 0.02), 0.012, 0.15, "Furn_Metal", seg=6, axis="z")
    cylinder(s, (0, 1.75, -w / 2 + 0.17), 0.05, 0.02, "Furn_Metal", seg=8, axis="z")


def b_vanity(s):
    w, d = 0.8, 0.5
    k_box(s, -w / 2, 0.08, -d / 2, w / 2, 0.82, d / 2 - 0.02, "Furn_Wood")
    k_box(s, -w / 2 + 0.02, 0.1, d / 2 - 0.02, -0.01, 0.78, d / 2, "Furn_Wood")
    k_box(s, 0.01, 0.1, d / 2 - 0.02, w / 2 - 0.02, 0.78, d / 2, "Furn_Wood")
    k_box(s, -w / 2, 0.82, -d / 2, w / 2, 0.86, d / 2, "Furn_Counter")
    cylinder(s, (0, 0.8, 0.02), 0.17, 0.065, "Furn_Porcelain", seg=12)
    cylinder(s, (0, 0.86, -d / 2 + 0.06), 0.015, 0.15, "Furn_Metal", seg=8)
    k_box(s, -0.35, 1.05, -d / 2, 0.35, 1.75, -d / 2 + 0.03, "Furn_Mirror")    # medicine cabinet
    k_box(s, -0.37, 1.03, -d / 2, 0.37, 1.77, -d / 2 + 0.02, "Furn_Wood")


def b_bookshelf(s):
    w, d, h = 0.9, 0.3, 1.8
    m = "Furn_Wood"
    k_box(s, -w / 2, 0, -d / 2, -w / 2 + 0.02, h, d / 2, m)
    k_box(s, w / 2 - 0.02, 0, -d / 2, w / 2, h, d / 2, m)
    k_box(s, -w / 2, 0, -d / 2, w / 2, h, -d / 2 + 0.01, m)
    rnd = random.Random(7)
    for i in range(5):
        y = 0.05 + i * 0.43
        k_box(s, -w / 2, y - 0.02, -d / 2, w / 2, y, d / 2, m)
        if i < 4:   # books
            x = -w / 2 + 0.03
            while x < w / 2 - 0.08:
                bw = rnd.uniform(0.025, 0.05)
                bh = rnd.uniform(0.2, 0.32)
                if rnd.random() < 0.85:
                    k_box(s, x, y, -d / 2 + 0.03, x + bw, y + bh, d / 2 - 0.04, rnd.choice(["Furn_Blanket", "Furn_Rug", "Furn_Fabric", "Furn_White"]))
                x += bw + 0.003
    k_box(s, -w / 2, h - 0.02, -d / 2, w / 2, h, d / 2, m)


def b_woodstove(s):
    w, d = 0.6, 0.55
    k_box(s, -0.65, 0, -0.55, 0.65, 0.03, 0.75, "Ext_Brick")                 # hearth pad
    k_box(s, -w / 2, 0.15, -d / 2, w / 2, 0.75, d / 2, "Furn_Black")
    legs(s, w, d, 0.15, 0.04, "Furn_Black", 0.02)
    k_box(s, -0.2, 0.28, d / 2, 0.2, 0.6, d / 2 + 0.02, "Furn_Black")
    k_box(s, -0.15, 0.33, d / 2 + 0.02, 0.15, 0.55, d / 2 + 0.025, "Furn_Rug")    # glowing-ish window
    cylinder(s, (0, 0.75, -0.05), 0.08, CEIL - 0.75, "Furn_Black", seg=10)   # flue up to the ceiling


def b_washer(s, dryer=False):
    w, d, h = 0.68, 0.68, 0.9
    k_box(s, -w / 2, 0.02, -d / 2, w / 2, h, d / 2, "Furn_White")
    k_box(s, -w / 2, h, -d / 2, w / 2, h + 0.12, -d / 2 + 0.1, "Furn_White")
    if dryer:
        cylinder(s, (0, 0.5, d / 2), 0.2, 0.02, "Furn_Black", seg=14, axis="z")
    else:
        k_box(s, -0.25, h, -0.18, 0.25, h + 0.005, 0.28, "Furn_Metal")


def b_shelf_metal(s):
    w, d, h = 1.2, 0.45, 1.8
    for sx in (-1, 1):
        for sz in (-1, 1):
            k_box(s, sx * w / 2 - (0.03 if sx > 0 else 0), 0, sz * d / 2 - (0.03 if sz > 0 else 0),
                  sx * w / 2 + (0 if sx > 0 else 0.03), h, sz * d / 2 + (0 if sz > 0 else 0.03), "Furn_Metal")
    for i in range(5):
        y = 0.1 + i * 0.42
        k_box(s, -w / 2, y, -d / 2, w / 2, y + 0.02, d / 2, "Furn_Metal")


def b_chest(s):
    w, h, d = 0.8, 0.5, 0.45
    k_box(s, -w / 2, 0, -d / 2, w / 2, h - 0.12, d / 2, "Furn_Wood")
    k_box(s, -w / 2 - 0.01, h - 0.12, -d / 2 - 0.01, w / 2 + 0.01, h, d / 2 + 0.01, "Furn_Wood")
    for x in (-w / 2 + 0.08, w / 2 - 0.08):
        k_box(s, x - 0.025, 0.02, d / 2, x + 0.025, h, d / 2 + 0.015, "Furn_Metal")
    k_box(s, -0.04, h - 0.2, d / 2, 0.04, h - 0.1, d / 2 + 0.02, "Furn_Metal")


def b_rug(s):
    k_box(s, -1.0, 0, -0.7, 1.0, 0.008, 0.7, "Furn_Rug")


KIT = {
    "door_interior":  {"size": (1, 1, 0.04), "build": lambda s: door_slab(s, False)},
    "door_exterior":  {"size": (1, 1, 0.04), "build": lambda s: door_slab(s, True)},
    "bed_double":     {"size": (1.44, 1.0, 2.0), "build": lambda s: b_bed(s, 1.4)},
    "bed_single":     {"size": (0.95, 1.0, 2.0), "build": lambda s: b_bed(s, 0.95, metal=True)},
    "nightstand":     {"size": (0.44, 0.96, 0.42), "build": b_nightstand},
    "dresser":        {"size": (1.2, 0.85, 0.53), "build": b_dresser},
    "wardrobe":       {"size": (1.0, 2.0, 0.61), "build": b_wardrobe},
    "couch":          {"size": (2.0, 0.85, 0.9), "build": b_couch},
    "armchair":       {"size": (0.9, 0.85, 0.9), "build": b_armchair},
    "coffee_table":   {"size": (1.1, 0.45, 0.6), "build": b_coffee_table},
    "tv_stand":       {"size": (1.0, 0.98, 0.64), "build": b_tv_stand},
    "dining_table":   {"size": (1.4, 0.75, 0.85), "build": lambda s: b_dining_table(s)},
    "dining_table_small": {"size": (0.9, 0.75, 0.75), "build": lambda s: b_dining_table(s, 0.9, 0.75)},
    "table_rough":    {"size": (0.9, 0.77, 0.7), "build": b_table_rough},
    "chair":          {"size": (0.44, 0.92, 0.44), "build": b_chair},
    "counter":        {"size": (0.6, 1.0, 0.62), "build": lambda s: b_counter(s)},
    "sink":           {"size": (0.6, 1.1, 0.62), "build": lambda s: b_counter(s, "sink")},
    "upper":          {"size": (0.6, 0.75, 0.33), "y": 1.45, "build": b_upper},
    "stove":          {"size": (0.76, 1.12, 0.66), "build": b_stove},
    "fridge":         {"size": (0.8, 1.75, 0.72), "build": b_fridge},
    "toilet":         {"size": (0.4, 0.78, 0.72), "build": b_toilet},
    "tub":            {"size": (1.52, 0.7, 0.76), "build": b_tub},
    "shower":         {"size": (0.9, 1.95, 0.9), "build": b_shower},
    "vanity":         {"size": (0.8, 1.77, 0.5), "build": b_vanity},
    "bookshelf":      {"size": (0.9, 1.8, 0.3), "build": b_bookshelf},
    "woodstove":      {"size": (1.3, 2.45, 1.3), "build": b_woodstove},
    "washer":         {"size": (0.68, 1.02, 0.68), "build": lambda s: b_washer(s)},
    "dryer":          {"size": (0.68, 1.02, 0.68), "build": lambda s: b_washer(s, True)},
    "shelf_metal":    {"size": (1.2, 1.8, 0.45), "build": b_shelf_metal},
    "rug":            {"size": (2.0, 0.01, 1.4), "build": b_rug},
    "chest":          {"size": (0.8, 0.5, 0.47), "build": b_chest},
}


def build_kit():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    col = bpy.context.scene.collection
    report = []
    for name, it in KIT.items():
        m = Mesh()
        it["build"](Sink(single=m))
        m.to_object(name, col)
        report.append(f"{name}:{sum(len(f[0]) - 2 for f in m.faces)}")
    bpy.ops.export_scene.fbx(filepath=f"{OUT_MODELS}/House_Kit.fbx", use_selection=False, object_types={"MESH"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                             bake_space_transform=True, mesh_smooth_type="FACE", path_mode="STRIP", embed_textures=False,
                             add_leaf_bones=False)
    bpy.ops.wm.save_as_mainfile(filepath=f"{OUT_BLEND}/House_Kit.blend")
    with open(f"{OUT_PLANS}/House_Kit.json", "w") as fh:
        json.dump({k: {"size": list(v["size"]), "y": v.get("y", 0.0)} for k, v in KIT.items()}, fh, indent=1)
    print("KIT tris", " ".join(report))


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    names = [a for a in args if a in PLANS] or list(PLANS)
    os.makedirs(OUT_MODELS, exist_ok=True); os.makedirs(OUT_PLANS, exist_ok=True); os.makedirs(OUT_BLEND, exist_ok=True)
    for n in names:
        h = House(n, PLANS[n]).build()
        h.check()
        h.export()
    if not args or "kit" in args:
        build_kit()
