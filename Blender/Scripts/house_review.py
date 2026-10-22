"""Review renders of the houses built by make_house.py: front and back 3/4 views of the outside and a top-down
cutaway of every storey with the furniture and doors placed from the plan JSON.

Run: blender -b -P house_review.py [-- Cabin ...]   ->  Docs/Houses/<Name>_*.png
"""
import bpy, json, math, os, sys
from mathutils import Vector

PROJECT = r"C:/Users/josht/My project"
PLANS = PROJECT + "/Assets/Environment/Houses/Plans"
BLEND = PROJECT + "/Blender/Houses"
OUT = PROJECT + "/Docs/Houses"


def B(p):
    return Vector((-p[0], -p[2], p[1]))


def link_kit():
    with bpy.data.libraries.load(f"{BLEND}/House_Kit.blend") as (src, dst):
        dst.objects = list(src.objects)
    kit = {}
    for ob in dst.objects:
        kit[ob.name] = ob
    return kit


def instance(kit, name, pos, yaw, scale=(1, 1, 1)):
    src = kit[name]
    ob = bpy.data.objects.new(name + "_inst", src.data)
    bpy.context.scene.collection.objects.link(ob)
    ob.location = B(pos)
    ob.rotation_euler = (0, 0, math.radians(-yaw))
    ob.scale = scale
    return ob


def setup_render():
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_WORKBENCH"
    sh = sc.display.shading
    sh.light = "STUDIO"
    sh.color_type = "MATERIAL"
    sh.show_shadows = True
    sh.show_cavity = True
    sh.cavity_type = "WORLD"
    sc.display.light_direction = (0.4, -0.5, 0.75)
    sc.render.resolution_x, sc.render.resolution_y = 1400, 1000
    sc.render.film_transparent = False
    world = bpy.data.worlds.new("W") if not sc.world else sc.world
    sc.world = world
    world.color = (0.75, 0.8, 0.85)
    # ground
    bpy.ops.mesh.primitive_plane_add(size=80, location=(0, 0, 0))
    g = bpy.context.active_object
    m = bpy.data.materials.new("Ground"); m.diffuse_color = (0.35, 0.42, 0.28, 1)
    g.data.materials.append(m)


def camera(loc, look, ortho=None, clip_start=0.1):
    cam = bpy.data.cameras.new("Cam")
    ob = bpy.data.objects.new("Cam", cam)
    bpy.context.scene.collection.objects.link(ob)
    ob.location = loc
    d = Vector(look) - Vector(loc)
    ob.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
    if ortho:
        cam.type = "ORTHO"; cam.ortho_scale = ortho
    else:
        cam.lens = 30
    cam.clip_start = clip_start
    cam.clip_end = 500
    bpy.context.scene.camera = ob
    return ob


def render(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("RENDER", path)


def review(name):
    bpy.ops.wm.open_mainfile(filepath=f"{BLEND}/House_{name}.blend")
    data = json.load(open(f"{PLANS}/House_{name}.json"))
    for ob in list(bpy.data.objects):
        if ob.name == "Ramps":
            ob.hide_render = True
    setup_render()
    kit = link_kit()
    for f in data["furniture"]:
        instance(kit, f["item"], f["pos"], f["yaw"])
    for d in data["doors"]:
        if not d["hasDoor"]:
            continue
        yaw = d["yaw"]
        a = math.radians(yaw)
        # slab spans hinge (x=0) to x=1 along the wall; hinge at the left end seen from outside, opened 70 deg in
        along = Vector((math.cos(a), 0, -math.sin(a)))
        p = Vector(d["pos"]) - along * (d["width"] / 2)
        instance(kit, "door_exterior" if d["exterior"] else "door_interior", p, yaw,
                 (d["width"], 1, d["height"]))
    for pn in data["panes"]:
        bpy.ops.mesh.primitive_cube_add(size=1)
        g = bpy.context.active_object
        g.location = B(pn["pos"])
        g.rotation_euler = (0, 0, math.radians(-pn["yaw"]))
        g.scale = (pn["size"][0], 0.006, pn["size"][1])
        m = bpy.data.materials.get("GlassPreview") or bpy.data.materials.new("GlassPreview")
        m.diffuse_color = (0.55, 0.7, 0.8, 0.5)
        g.data.materials.append(m)
    W, D = data["size"]
    R = max(W, D)
    top = data.get("ridge", 6)
    os.makedirs(OUT, exist_ok=True)
    camera(B((R * 1.1, top * 0.9, D / 2 + R * 1.3)), B((0, top * 0.45, 0)))
    render(f"{OUT}/{name}_front.png")
    camera(B((-R * 1.1, top * 0.9, -D / 2 - R * 1.3)), B((0, top * 0.45, 0)))
    render(f"{OUT}/{name}_back.png")
    for s in range(data["storeys"]):
        cut = data["floor"] + s * data["storeyHeight"] + 1.6
        h = 40.0
        camera(Vector((0, -0.01, h)), Vector((0, 0, 0)), ortho=R + 4.5, clip_start=h - cut)
        render(f"{OUT}/{name}_plan{s}.png")


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    names = args or ["Cabin", "Rambler", "Farmhouse", "Shack"]
    for n in names:
        review(n)
