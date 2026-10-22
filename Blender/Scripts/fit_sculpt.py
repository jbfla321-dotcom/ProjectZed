"""Shape the MPFB body like BlendSwap asset 21626 (Human Male Base Mesh, CC0, by nashnir).

The sculpt is 650k triangles with no UVs or weights, so it can't be the game mesh. Instead it's lined up on Ch17's
skeleton and the MPFB body (which keeps its topology, UVs and Mixamo weights) is projected onto its surface."""
import bpy, math
from mathutils import Matrix, Vector, kdtree

SCULPT = r"C:/Users/josht/My project/Blender/Sources/BlendSwap_21626_HumanMaleBaseMesh/extracted/HumanBaseMale.blend"
DECIMATE_TO = 80000   # faces kept from the sculpt; plenty for a projection target
PROJECT_LIMIT = 0.05  # MPFB vertices only move where the sculpt is within this distance along their normal

# Sculpt bone -> Ch17 bone whose head sits at the same joint (sculpt bone heads unless noted)
LANDMARKS = [("Humerus_L", "LeftArm"), ("Humerus_R", "RightArm"), ("Femur_L", "LeftUpLeg"), ("Femur_R", "RightUpLeg"),
             ("Tibia_L", "LeftLeg"), ("Tibia_R", "RightLeg"), ("Heel_L", "LeftFoot"), ("Heel_R", "RightFoot")]


def smoothstep(x):
    x = min(1.0, max(0.0, x)); return x * x * (3 - 2 * x)


def _load():
    with bpy.data.libraries.load(SCULPT) as (src, dst):
        dst.objects = ["MaleBASE", "Armature"]
    sc, sc_arm = dst.objects
    for o in (sc, sc_arm): bpy.context.scene.collection.objects.link(o)
    bpy.context.view_layer.update()
    sc.modifiers.clear()
    mw = sc.matrix_world.copy(); sc.parent = None
    sc.data.transform(mw); sc.matrix_world = Matrix()
    sc.name = "Sculpt_21626"
    # Bone joints in world space, then the armature is no longer needed.
    joints = {b.name: (sc_arm.matrix_world @ b.head_local, sc_arm.matrix_world @ b.tail_local) for b in sc_arm.data.bones}
    bpy.data.objects.remove(sc_arm, do_unlink=True)
    return sc, joints


def _decimate(sc):
    mod = sc.modifiers.new("Decimate", 'DECIMATE'); mod.ratio = DECIMATE_TO / len(sc.data.polygons)
    with bpy.context.temp_override(object=sc, active_object=sc):
        bpy.ops.object.modifier_apply(modifier=mod.name)


def _align(sc, joints, ch_arm):
    """Turn the sculpt to face -Y with its left side on +X (like Ch17), then fit scale and position to Ch17's joints."""
    rot = Matrix.Rotation(math.radians(-90), 4, 'Z')
    for k, (h, t) in joints.items(): joints[k] = (rot @ h, rot @ t)
    ch = lambda n: ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:" + n].head_local
    src = [joints[s][0] for s, _ in LANDMARKS]; dst = [ch(d) for _, d in LANDMARKS]
    cs = sum(src, Vector()) / len(src); cd = sum(dst, Vector()) / len(dst)
    scale = sum((d - cd).dot(s - cs) for s, d in zip(src, dst)) / sum((s - cs).length_squared for s in src)
    m = Matrix.Translation(cd) @ Matrix.Scale(scale, 4) @ Matrix.Translation(-cs)
    for k, (h, t) in joints.items(): joints[k] = (m @ h, m @ t)
    sc.data.transform(m @ rot)
    print(f"sculpt scale {scale:.4f}; landmark misfit (cm):",
          ", ".join(f"{d}={(joints[s][0] - ch(d)).length * 100:.1f}" for s, d in LANDMARKS))


def _straighten_arms(sc, joints, ch_arm):
    """The sculpt stands in an A-pose; swing each upper arm, then forearm, onto Ch17's arm directions."""
    ch = lambda n: (ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:" + n].head_local,
                    ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:" + n].tail_local)
    import bmesh
    from mathutils import Quaternion
    bm = bmesh.new(); bm.from_mesh(sc.data); bm.verts.ensure_lookup_table()
    downstream = {"Humerus": ["Humerus", "Forearm", "Palm", "Fingers"], "Forearm": ["Forearm", "Palm", "Fingers"]}
    for s, side, sign in (("L", "Left", 1), ("R", "Right", -1)):
        sh = joints[f"Humerus_{s}"][0]
        SH_BLEND = 0.07
        # The arm: everything reachable from the fingertips without going medial of the shoulder below the armpit,
        # so the torso's side and the hip next to the hanging hand aren't caught.
        lateral = lambda p: (p.x - sh.x) * sign
        ok = lambda p: lateral(p) > -SH_BLEND and (p.z > sh.z - 0.08 or lateral(p) > 0)
        tip = joints[f"Fingers_{s}"][1]
        start = min(bm.verts, key=lambda v: (v.co - tip).length_squared)
        arm, todo = {start}, [start]
        while todo:
            v = todo.pop()
            for e in v.link_edges:
                o = e.other_vert(v)
                if o not in arm and ok(o.co): arm.add(o); todo.append(o)
        print(f"  {side} arm: {len(arm)} of {len(bm.verts)} verts")
        for sb, cb in ((f"Humerus_{s}", f"{side}Arm"), (f"Forearm_{s}", f"{side}ForeArm")):
            h, t = joints[sb]; ch_h, ch_t = ch(cb)
            q = (t - h).normalized().rotation_difference((ch_t - ch_h).normalized())
            print(f"  {sb}: {math.degrees(q.angle):.1f} deg")
            axis = (t - h).normalized()
            for v in arm:
                if sb.startswith("Humerus"): k = smoothstep((lateral(v.co) + SH_BLEND) / (2 * SH_BLEND))
                else: k = smoothstep(((v.co - h).dot(axis) + 0.04) / 0.08)
                if k > 0: v.co = h + Quaternion().slerp(q, k).to_matrix() @ (v.co - h)
            R = q.to_matrix()
            for name in downstream[sb.split("_")[0]]:  # bones further down the arm move with it
                jh, jt = joints[f"{name}_{s}"]; joints[f"{name}_{s}"] = (h + R @ (jh - h), h + R @ (jt - h))
        # Match the arm's length too: slide the elbow and wrist onto Ch17's, stretching the segments between.
        el_s, wr_s = joints[f"Forearm_{s}"][0], joints[f"Palm_{s}"][0]
        d_el, d_wr = ch(f"{side}ForeArm")[0] - el_s, ch(f"{side}Hand")[0] - wr_s
        ax1, len1 = (el_s - sh).normalized(), (el_s - sh).length
        ax2, len2 = (wr_s - el_s).normalized(), (wr_s - el_s).length
        print(f"  {side} elbow moved {d_el.length * 100:.1f} cm, wrist {d_wr.length * 100:.1f} cm")
        for v in arm:
            t1 = (v.co - sh).dot(ax1) / len1
            if t1 < 1: v.co += d_el * smoothstep(t1)
            else: v.co += d_el.lerp(d_wr, min(1.0, max(0.0, (v.co - el_s).dot(ax2) / len2)))
        for name, d in ((f"Forearm_{s}", d_el), (f"Palm_{s}", d_wr), (f"Fingers_{s}", d_wr)):
            jh, jt = joints[name]; joints[name] = (jh + d, jt + d)
    bm.to_mesh(sc.data); bm.free()


def fit(body, ch_arm, protect_z, protect_y):
    """Project `body` onto the fitted sculpt. protect_z: (lo, hi) of the underwear band, whose front-centre is kept."""
    sc, joints = _load()
    _decimate(sc)
    _align(sc, joints, ch_arm)
    _straighten_arms(sc, joints, ch_arm)

    # Weight: 0 at the neck / wrist cuts (the seam with Ch17's head and hands), easing to 1 over ~8 cm.
    me = body.data; mw = body.matrix_world
    edge_count = {}
    for e in me.edges: edge_count[e.key] = 0
    for p in me.polygons:
        for k in p.edge_keys: edge_count[k] += 1
    boundary = {i for k, n in edge_count.items() if n == 1 for i in k}
    tree = kdtree.KDTree(len(boundary))
    for i in boundary: tree.insert(mw @ me.vertices[i].co, i)
    tree.balance()
    vg = body.vertex_groups.new(name="Sculpt_Fit")
    vs = body.vertex_groups.new(name="Shoulder_Smooth")
    # Shoulders/armpits: the sculpt is creased there by swinging its arms up, so they're left out of the projection
    # and smoothed instead.
    shoulders = [ch_arm.matrix_world @ ch_arm.data.bones["mixamorig1:" + n].head_local for n in ("LeftArm", "RightArm")]
    lo, hi = protect_z
    for v in me.vertices:
        p = mw @ v.co
        seam = smoothstep((tree.find(p)[2] - 0.015) / 0.08)
        d = min((p - s).length for s in shoulders)
        vs.add([v.index], seam * (1 - smoothstep((d - 0.08) / 0.10)), 'REPLACE')
        w = seam * smoothstep((d - 0.10) / 0.08)
        if lo - 0.04 < p.z < hi and p.y < protect_y and abs(p.x) < 0.12:  # front of the crotch: keep MPFB's
            inside = min((p.z - (lo - 0.04)) / 0.04, (hi - p.z) / 0.04, (0.12 - abs(p.x)) / 0.04)
            w *= 1 - smoothstep(inside)
        vg.add([v.index], w, 'REPLACE')

    sw = body.modifiers.new("SculptFit", 'SHRINKWRAP')
    sw.target = sc; sw.wrap_method = 'PROJECT'; sw.vertex_group = vg.name
    sw.use_negative_direction = True; sw.use_positive_direction = True
    sw.project_limit = PROJECT_LIMIT; sw.offset = 0.0
    sm = body.modifiers.new("SculptFitSmooth", 'SMOOTH'); sm.factor = 0.3; sm.iterations = 2; sm.vertex_group = vg.name
    ss = body.modifiers.new("ShoulderSmooth", 'LAPLACIANSMOOTH'); ss.lambda_factor = 0.6; ss.iterations = 12
    ss.use_volume_preserve = True; ss.vertex_group = vs.name
    with bpy.context.temp_override(object=body, active_object=body):
        for name in (sw.name, sm.name, ss.name): bpy.ops.object.modifier_apply(modifier=name)
    for name in ("Sculpt_Fit", "Shoulder_Smooth"):  # `vg`/`vs` are stale once modifiers are applied
        body.vertex_groups.remove(body.vertex_groups[name])

    # Keep the fitted sculpt in the file for reference, hidden.
    sc.hide_set(True); sc.hide_render = True
    return sc
