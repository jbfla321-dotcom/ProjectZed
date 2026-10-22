"""Textures for the player's base body. Run right after make_game_body.py (it bakes from that fresh .blend).

- Base_Skin_Albedo: the body's skin, in Ch17's own tone (sampled at his neck) with blotches, mottling and baked
  ambient occlusion, so it reads as skin rather than flat plastic.
- Base_Underwear_Albedo: fabric grain and ambient occlusion on the underwear.
- Ch17_Player_Diffuse: a copy of Ch17's texture for the player only (the NPCs keep theirs): his scalp and the band
  his helmet left painted as bare skin (his hair mesh is hidden in Unity), his neck and hands shifted to the body's
  skin tone with their detail kept, and the seams eased into the exact tone so they disappear."""
import bpy, math, os, shutil, sys
import numpy as np
from mathutils import Vector

SCRIPTS = r"C:/Users/josht/My project/Blender/Scripts"
BLEND = r"C:/Users/josht/My project/Blender/Player/Ch17_Base_v4.blend"
OUT_DIR = r"C:/Users/josht/My project/Assets/Characters/BaseBody"
CH17_DIFFUSE = r"C:/Users/josht/My project/Assets/Characters/Mixamo/Textures/Ch17_1001_Diffuse.png"
HAIRLINE_FRONT = 0.042         # scalp repainted from this height above the eyes at the forehead (covers the band)...
HAIRLINE_BACK = -0.06          # ...down to this at the nape
WRIST_FADE = 0.06  # how far Ch17's skin eases into the exact body tone from the wrist seam
EAR_RADIUS = 0.035  # the scalp repaint leaves this much around each ear alone
NECK_MATCH = 0.06  # his neck is shifted to the body tone this far up from the seam, fading out over as much again

sys.path.append(SCRIPTS)
import body_parts
from fit_sculpt import smoothstep

bpy.ops.wm.open_mainfile(filepath=BLEND)
scene = bpy.context.scene
scene.render.engine = 'CYCLES'; scene.cycles.device = 'CPU'; scene.cycles.samples = 256  # enough that the ambient occlusion bakes smooth, not grainy
scene.render.bake.margin = 16
ch_arm = bpy.data.objects["Armature"]; ch_arm.data.pose_position = 'REST'
ch_body = bpy.data.objects["Ch17_Body"]
body = bpy.data.objects["Base_Body"]; under = bpy.data.objects["Base_Underwear"]
rings, neck_center = body_parts.head_and_hand_rings(ch_body, ch_arm)
skin_tone = body_parts.make_materials(ch_body, neck_center.z)[0].node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value[:3]


def node_tree(mat):
    mat.use_nodes = True
    nt = mat.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); emit = nt.nodes.new("ShaderNodeEmission")
    nt.links.new(emit.outputs[0], out.inputs["Surface"])
    return nt, emit


def noise(nt, scale, detail=4):
    n = nt.nodes.new("ShaderNodeTexNoise"); n.inputs["Scale"].default_value = scale; n.inputs["Detail"].default_value = detail
    coords = nt.nodes.new("ShaderNodeTexCoord")
    nt.links.new(coords.outputs["Object"], n.inputs["Vector"])
    return n.outputs["Fac"]


def mix(nt, a, b, fac, blend='MIX'):
    m = nt.nodes.new("ShaderNodeMix"); m.data_type = 'RGBA'; m.blend_type = blend
    for sock, val in ((m.inputs[0], fac), (m.inputs[6], a), (m.inputs[7], b)):
        if isinstance(val, (float, int)): sock.default_value = val
        elif isinstance(val, tuple): sock.default_value = (*val, 1) if len(val) == 3 else val
        else: nt.links.new(val, sock)
    return m.outputs[2]


def ramp(nt, fac, lo, hi):
    """fac (0..1) remapped to lo..hi."""
    r = nt.nodes.new("ShaderNodeMapRange"); r.inputs["To Min"].default_value = lo; r.inputs["To Max"].default_value = hi
    nt.links.new(fac, r.inputs["Value"]); return r.outputs[0]


def ao_factor(nt, strength):
    ao = nt.nodes.new("ShaderNodeAmbientOcclusion"); ao.samples = 64; ao.inputs["Distance"].default_value = 0.08
    ao.only_local = True  # the object's own creases only: his head shading the body made a darker ring at the neck
    return ramp(nt, ao.outputs["AO"], 1 - strength, 1.0)


def bake(obj, mat, image, clear=True):
    """Emission bake of `mat` on obj's UVs into `image` (clear=False keeps pixels outside obj's islands)."""
    old = [s.material for s in obj.material_slots]
    obj.data.materials.clear(); obj.data.materials.append(mat)
    tex = mat.node_tree.nodes.new("ShaderNodeTexImage"); tex.image = image
    mat.node_tree.nodes.active = tex
    for o in bpy.context.view_layer.objects: o.select_set(o == obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.bake(type='EMIT', use_clear=clear, margin=16)
    obj.data.materials.clear()
    for m in old: obj.data.materials.append(m)


def save(image, name):
    path = os.path.join(OUT_DIR, name + ".png")
    image.filepath_raw = path; image.file_format = 'PNG'; image.save()
    print("SAVED", path)
    return path


# ---------- Body skin ----------
mat = bpy.data.materials.new("bake_skin"); nt, emit = node_tree(mat)
c = skin_tone
c = mix(nt, c, tuple(x * k for x, k in zip(c, (1.07, 0.93, 0.9))), ramp(nt, noise(nt, 6), -0.4, 1.0))  # blotchy redness
c = mix(nt, c, ramp(nt, noise(nt, 30), 0.93, 1.06), 1.0, 'MULTIPLY')                                    # uneven tone
c = mix(nt, c, ramp(nt, noise(nt, 400, 2), 0.95, 1.04), 1.0, 'MULTIPLY')                                # mottling, pores
c = mix(nt, c, ao_factor(nt, 0.35), 1.0, 'MULTIPLY')                                                    # creases
nt.links.new(c, emit.inputs["Color"])
skin_img = bpy.data.images.new("Base_Skin_Albedo", 2048, 2048)
bake(body, mat, skin_img)
# The shading and blotches pull the average away from his real tone; scale it back so the body matches his neck.
px = np.empty(len(skin_img.pixels), dtype=np.float32); skin_img.pixels.foreach_get(px)
px = px.reshape(-1, 4)
lin = np.where(px[:, :3] <= 0.04045, px[:, :3] / 12.92, ((px[:, :3] + 0.055) / 1.055) ** 2.4)
baked = lin.sum(axis=1) > 0.003  # pixels on the body's UV islands (the rest stay cleared to black)
# Pixels along the island edges are only partly covered and come out part black; they showed as dark lines along the
# UV seams (on his back especially). Anything under half the average brightness is treated as off the islands.
baked &= lin.sum(axis=1) > 0.5 * lin[baked].sum(axis=1).mean()
scale = np.array(skin_tone) / lin[baked].mean(axis=0)
print("skin average", lin[baked].mean(axis=0).round(3), "-> scaled by", scale.round(3))
lin[baked] = np.clip(lin[baked] * scale, 0, 1)
lin[~baked] = skin_tone  # off the UV islands: skin, not black, or blurred mip levels bleed dark lines along the seams
px[:, :3] = np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055)
skin_img.pixels.foreach_set(px.ravel())
save(skin_img, "Base_Skin_Albedo")

# ---------- Underwear ----------
mat = bpy.data.materials.new("bake_cloth"); nt, emit = node_tree(mat)
cloth = (0.05, 0.055, 0.06)
c = mix(nt, cloth, ramp(nt, noise(nt, 900, 1), 0.85, 1.12), 1.0, 'MULTIPLY')  # knit grain
c = mix(nt, c, ramp(nt, noise(nt, 12), 0.92, 1.05), 1.0, 'MULTIPLY')          # wear
c = mix(nt, c, ao_factor(nt, 0.6), 1.0, 'MULTIPLY')
nt.links.new(c, emit.inputs["Color"])
cloth_img = bpy.data.images.new("Base_Underwear_Albedo", 1024, 1024)
bake(under, mat, cloth_img)
save(cloth_img, "Base_Underwear_Albedo")

# ---------- Player copy of Ch17's texture: bare scalp, neck and hands in the body tone ----------
mw = ch_body.matrix_world
pts = [mw @ v.co for v in ch_body.data.vertices]
# Eye height from the eyelid openings (open edge loops either side of the face's centre line).
open_edges = {}
for poly in ch_body.data.polygons:
    for k in poly.edge_keys: open_edges[k] = open_edges.get(k, 0) + 1
loop_verts = {i for k, n in open_edges.items() if n == 1 for i in k}
eye_pts = [pts[i] for i in loop_verts if 0.015 < abs(pts[i].x) < 0.05 and pts[i].z > neck_center.z + 0.1 and pts[i].y < neck_center.y - 0.04]
eye_z = sum(p.z for p in eye_pts) / len(eye_pts)
head_pts = [p for p in pts if p.z > eye_z and abs(p.x) < 0.12]
head_y = sum(p.y for p in head_pts) / len(head_pts)
print(f"eyes at z={eye_z:.3f} ({len(eye_pts)} pts), head centre y={head_y:.3f}")
# Ears: the head's widest points either side, around eye height.
ears = [max((p for p in pts if abs(p.z - eye_z) < 0.04 and p.x * side > 0 and abs(p.x) < 0.12), key=lambda p: p.x * side)
        for side in (1, -1)]
print("ears at", [tuple(round(c, 3) for c in e) for e in ears])

# Per-vertex masks: Scalp (bare skin painted over his scalp and the helmet band), Neck and Hand (his texture's
# colour shifted to the body tone, detail kept), Seam (the last few cm before the body, eased to the exact tone).
masks = {n: [0.0] * len(pts) for n in ("Scalp", "Neck", "Hand", "Seam")}
for i, p in enumerate(pts):
    if abs(p.x) < 0.12 and p.z > eye_z - 0.1:
        front = -math.cos(math.atan2(p.x, -(p.y - head_y)))  # -1 at the forehead, +1 at the back
        hairline = eye_z + HAIRLINE_FRONT + (HAIRLINE_BACK - HAIRLINE_FRONT) * (front + 1) / 2
        h = smoothstep((p.z - hairline) / 0.02)
        if min((p - e).length for e in ears) < EAR_RADIUS: h = 0.0  # leave the ears as they are
        masks["Scalp"][i] = h
    for name, ring, _ in rings:
        depth, d = ring.local(p)
        if d.length < 0.15 and depth > -0.01:
            if name == "neck":  # tone shift only: a flat strip at the neck read as a pale collar
                masks["Neck"][i] = max(masks["Neck"][i], 1 - smoothstep((depth - NECK_MATCH) / NECK_MATCH))
            else:
                masks["Hand"][i] = 1.0
                masks["Seam"][i] = max(masks["Seam"][i], 1 - smoothstep((depth - 0.005) / WRIST_FADE))
for n, values in masks.items():
    layer = ch_body.data.color_attributes.new(n + "Mask", 'FLOAT_COLOR', 'POINT')
    for i, x in enumerate(values): layer.data[i].color = (x, x, x, 1)

src_img = bpy.data.images.load(CH17_DIFFUSE, check_existing=False)
w, h = src_img.size
texels = np.empty(w * h * 4, dtype=np.float32); src_img.pixels.foreach_get(texels); texels = texels.reshape(h, w, 4)
uvs = ch_body.data.uv_layers.active.data


def tone_shift(mask):
    """Per-channel factor that brings his texture's average over `mask` to the body's skin tone (linear)."""
    picks = [texels[min(h - 1, int(uvs[l.index].uv[1] % 1 * h)), min(w - 1, int(uvs[l.index].uv[0] % 1 * w)), :3]
             for l in ch_body.data.loops if mask[l.vertex_index] > 0.5]
    srgb = np.array(picks)
    # Median, not mean: the grime on his hands drags a mean down and the shift then overshoots to pale.
    mean = np.median(np.where(srgb <= 0.04045, srgb / 12.92, ((srgb + 0.055) / 1.055) ** 2.4), axis=0)
    print(f"{len(picks)} texels, average {mean.round(3)} -> x{(np.array(skin_tone) / mean).round(3)}")
    return tuple(np.array(skin_tone) / mean)


neck_shift, hand_shift = tone_shift(masks["Neck"]), tone_shift(masks["Hand"])

work = os.path.join(OUT_DIR, "Ch17_Player_Diffuse.png")
shutil.copyfile(CH17_DIFFUSE, work)
head_img = bpy.data.images.load(work); head_img.colorspace_settings.name = 'sRGB'
mat = bpy.data.materials.new("bake_head"); nt, emit = node_tree(mat)
src = nt.nodes.new("ShaderNodeTexImage"); src.image = src_img
src.interpolation = 'Closest'
uv = nt.nodes.new("ShaderNodeUVMap"); nt.links.new(uv.outputs[0], src.inputs[0])
def attr(name):
    a = nt.nodes.new("ShaderNodeAttribute"); a.attribute_name = name; return a.outputs["Fac"]
c = src.outputs["Color"]
c = mix(nt, c, mix(nt, c, neck_shift, 1.0, 'MULTIPLY'), attr("NeckMask"))
# Hands: their texture is so red that a per-channel shift turns them grey-green. Keep only its brightness detail
# (knuckles, creases) on the body's exact tone, with the grime contrast halved.
bw = nt.nodes.new("ShaderNodeRGBToBW"); nt.links.new(src.outputs["Color"], bw.inputs[0])
lum = nt.nodes.new("ShaderNodeMath"); lum.operation = 'DIVIDE'; nt.links.new(bw.outputs[0], lum.inputs[0])
lum.inputs[1].default_value = float(np.dot(np.array(skin_tone) / np.array(hand_shift), (0.2126, 0.7152, 0.0722)))
detail = mix(nt, (1.0, 1.0, 1.0), lum.outputs[0], 0.5)  # halfway between flat and full detail
hands = mix(nt, skin_tone, detail, 1.0, 'MULTIPLY')
c = mix(nt, c, hands, attr("HandMask"))
c = mix(nt, c, skin_tone, attr("SeamMask"))
scalp = mix(nt, skin_tone, ramp(nt, noise(nt, 40), 0.96, 1.04), 1.0, 'MULTIPLY')  # bare scalp, faintly uneven
c = mix(nt, c, scalp, attr("ScalpMask"))
nt.links.new(c, emit.inputs["Color"])
bake(ch_body, mat, head_img, clear=False)
head_img.save()
print("SAVED", work)

# Show the results in the .blend too.
for obj, img in ((body, skin_img), (under, cloth_img)):
    m = obj.material_slots[0].material; m.use_nodes = True
    t = m.node_tree.nodes.new("ShaderNodeTexImage"); t.image = img
    m.node_tree.links.new(t.outputs[0], m.node_tree.nodes["Principled BSDF"].inputs["Base Color"])
    m.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.7 if obj == body else 0.9
ch_mat = ch_body.material_slots[0].material.copy(); ch_mat.name = "Ch17_body_player"
for n in ch_mat.node_tree.nodes:
    if n.type == 'TEX_IMAGE' and n.image and "1001_Diffuse" in n.image.filepath: n.image = head_img
ch_body.material_slots[0].material = ch_mat
for n in masks: ch_body.data.color_attributes.remove(ch_body.data.color_attributes[n + "Mask"])
bpy.data.objects["Ch17_Hair"].hide_render = True; bpy.data.objects["Ch17_Hair"].hide_set(True)  # as in Unity
bpy.ops.wm.save_mainfile()
print("DONE")
