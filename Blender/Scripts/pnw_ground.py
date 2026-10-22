"""Ground textures and ground-cover cards for ProjectZed's PNW forest (second pass: meadows, moss, litter).

Usage: python -I pnw_ground.py <download dir> <project Assets/Environment/PNW dir>

Inputs (all CC0): ambientCG Moss003 + Grass004 (1K), Poly Haven forest_leaves_03 (2K), Poly Haven grass_medium_01 /
grass_medium_02 blade textures (2K), plus the ambientCG leaf sets already used by pnw_textures.py (in <download dir>/../dl).

- Terrain sets are written like pnw_textures.py's: <name>_color.jpg, _normal.jpg, _mask.png (R metal, G occlusion,
  B height, A smoothness), so they work as URP terrain layers.
- Grass cards are composed from single photographed blades, following what makes game grass read as real:
  clumped tufts (blades fan out from a few shared roots), darker at the base (fake ground occlusion) and lighter at the
  tips, mixed green and dead blades, and seed heads in the summer-meadow card (fescue / oatgrass in seed).
- Ground-cover cards are top-down views (laid nearly flat by the mesh): Oregon oxalis (trifoliate leaves carpeting
  moss) and vanilla leaf (big three-part leaves on stalks), two of the commonest PNW forest-floor herbs.
"""
import math
import os
import random
import shutil
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage

DL, OUT = sys.argv[1], sys.argv[2]
LEAF_DL = os.path.join(os.path.dirname(os.path.abspath(DL)), "dl", "leaf")
TEX_OUT = os.path.join(OUT, "Textures")
CARD_OUT = os.path.join(OUT, "Foliage")


# ---------- Terrain sets ----------
def pack(name, color, normal, ao, rough, height=None, flip_green=False):
    shutil.copyfile(color, os.path.join(TEX_OUT, f"{name}_color.jpg"))
    nrm = Image.open(normal).convert("RGB")
    if flip_green:  # DirectX-style normal to OpenGL
        r, g, b = nrm.split()
        nrm = Image.merge("RGB", (r, g.point(lambda v: 255 - v), b))
    nrm.save(os.path.join(TEX_OUT, f"{name}_normal.jpg"), quality=95)
    size = Image.open(color).size
    a = np.asarray(Image.open(ao).convert("L").resize(size)).astype(np.float32)
    r = np.asarray(Image.open(rough).convert("L").resize(size)).astype(np.float32)
    if height:
        h = np.asarray(Image.open(height).convert("L").resize(size)).astype(np.float32)
    else:
        c = np.asarray(Image.open(color).convert("L")).astype(np.float32)
        h = (c - c.min()) / max(1.0, c.max() - c.min()) * 255.0
    mask = np.stack([np.zeros_like(a), a, h, 255.0 - r], axis=-1)
    Image.fromarray(mask.clip(0, 255).astype(np.uint8), "RGBA").save(os.path.join(TEX_OUT, f"{name}_mask.png"))
    print("packed", name)


def pack_sets():
    for acg in ["Moss003", "Grass004"]:
        d = os.path.join(DL, "acg", f"{acg}_1K-JPG")
        f = lambda m: os.path.join(d, f"{acg}_1K-JPG_{m}.jpg")
        pack(acg.lower(), f("Color"), f("NormalGL"), f("AmbientOcclusion"), f("Roughness"), f("Displacement"))
    d = os.path.join(DL, "forest_leaves_03")
    arm = Image.open(os.path.join(d, "forest_leaves_03_arm_2k.jpg")).convert("RGB")
    ao, rough, _ = arm.split()
    tmp_ao, tmp_r = os.path.join(d, "_ao.png"), os.path.join(d, "_rough.png")
    ao.save(tmp_ao); rough.save(tmp_r)
    pack("forest_leaves_03", os.path.join(d, "forest_leaves_03_diff_2k.jpg"), os.path.join(d, "forest_leaves_03_nor_gl_2k.jpg"), tmp_ao, tmp_r)


# ---------- Sprites ----------
def load(color, alpha, max_size=1024):
    c = Image.open(color).convert("RGB")
    if c.size[0] > max_size:  # cards are 1024 px; working at 2K only made compositing slow
        c = c.resize((max_size, max_size), Image.LANCZOS)
    a = Image.open(alpha).convert("L").resize(c.size)
    c.putalpha(a)
    return c


def sprites(img, min_px=300, box=None):
    a = np.asarray(img)[..., 3] > 100
    a = ndimage.binary_closing(a, iterations=2)
    lab, _ = ndimage.label(a)
    out = []
    w, h = img.size
    for i, sl in enumerate(ndimage.find_objects(lab)):
        if sl is None or (lab[sl] == i + 1).sum() < min_px:
            continue
        y0, y1, x0, x1 = sl[0].start, sl[0].stop, sl[1].start, sl[1].stop
        if box and not (box[0] <= (x0 + x1) / 2 / w <= box[2] and box[1] <= (y0 + y1) / 2 / h <= box[3]):
            continue
        arr = np.asarray(img.crop((x0, y0, x1, y1))).copy()
        arr[..., 3] = np.where(lab[sl] == i + 1, arr[..., 3], 0)
        out.append(Image.fromarray(arr, "RGBA"))
    out.sort(key=lambda s: -s.size[0] * s.size[1])
    return out


def recolor(sprite, rgb, amount, base_dark=0.0):
    """Tint toward rgb (keeping the photo's light/dark detail); base_dark darkens the bottom of the sprite."""
    arr = np.asarray(sprite).astype(np.float32)
    lum = (arr[..., :3] @ np.array([0.3, 0.59, 0.11]))[..., None] / 255.0
    arr[..., :3] = arr[..., :3] * (1 - amount) + np.array(rgb, np.float32) * lum * 1.7 * amount
    if base_dark:
        h = arr.shape[0]
        ramp = np.clip(np.linspace(0, 1, h) / 0.45, 0, 1)[::-1]  # 1 at the bottom .. 0 above 45% height
        arr[..., :3] *= (1 - base_dark * ramp)[:, None, None]
    return Image.fromarray(arr.clip(0, 255).astype(np.uint8), "RGBA")


class Card:
    def __init__(self, size=1024):
        self.size = size
        self.img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        self.draw = ImageDraw.Draw(self.img)

    def line(self, pts, width, color):
        s = self.size
        self.draw.line([(x * s, y * s) for x, y in pts], fill=color + (255,), width=max(1, int(width)))

    def place(self, sprite, base, angle, length, flip=False):
        """Paste a sprite (base at its bottom) with the base at `base` (0..1), pointing along `angle` (0 = up)."""
        s = self.size
        if flip:
            sprite = sprite.transpose(Image.FLIP_LEFT_RIGHT)
        w, h = sprite.size
        L = max(2, int(length * s))
        sp = sprite.resize((max(1, int(w * L / h)), L), Image.LANCZOS)
        w, h = sp.size
        pad = int(math.hypot(w, h)) + 2
        canvas = Image.new("RGBA", (pad * 2, pad * 2), (0, 0, 0, 0))
        canvas.paste(sp, (pad - w // 2, pad - h), sp)
        canvas = canvas.rotate(-angle, resample=Image.BICUBIC)
        # Composite only the overlapping window (clipped to the card) rather than a full-size layer per sprite.
        x0, y0 = int(base[0] * s) - pad, int(base[1] * s) - pad
        cx0, cy0, cx1, cy1 = max(0, x0), max(0, y0), min(s, x0 + 2 * pad), min(s, y0 + 2 * pad)
        if cx1 <= cx0 or cy1 <= cy0:
            return
        piece = canvas.crop((cx0 - x0, cy0 - y0, cx1 - x0, cy1 - y0))
        self.img.alpha_composite(piece, (cx0, cy0))

    def save(self, name):
        arr = np.asarray(self.img).astype(np.float32)
        a = arr[..., 3:4] / 255.0
        rgb = arr[..., :3]
        blur = np.asarray(Image.fromarray((rgb * a).astype(np.uint8)).filter(ImageFilter.BoxBlur(10))).astype(np.float32)
        ab = np.asarray(Image.fromarray((a[..., 0] * 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(10))).astype(np.float32)[..., None] / 255.0
        fill = np.where(ab > 0.01, blur / np.maximum(ab, 0.01), rgb[a[..., 0] > 0.5].mean(axis=0))
        out = np.concatenate([(rgb * a + fill * (1 - a)).clip(0, 255), arr[..., 3:4]], axis=-1).astype(np.uint8)
        Image.fromarray(out, "RGBA").save(os.path.join(CARD_OUT, name + ".png"))
        print("card", name)


# ---------- Grass ----------
def grass_card(name, green, dry, rng, blades, dry_share, green_rgb, dry_rgb, height, seeds=None, seed_count=0):
    """A bunchgrass tuft: blades fan out from 2-3 roots near the bottom centre; outer blades lean more."""
    c = Card()
    roots = [0.5 + rng.uniform(-0.12, 0.12) for _ in range(rng.choice([2, 3]))]
    order = []
    for i in range(blades):
        root = rng.choice(roots) + rng.gauss(0, 0.03)
        lean = (root - 0.5) * 120 + rng.gauss(0, 14)
        length = height * rng.uniform(0.55, 1.0) * (1 - abs(lean) / 160)
        is_dry = rng.random() < dry_share
        order.append((rng.random(), root, lean, length, is_dry))
    order.sort()
    for _, root, lean, length, is_dry in order:
        src = rng.choice(dry if is_dry else green)
        rgb = dry_rgb if is_dry else [v * rng.uniform(0.85, 1.12) for v in green_rgb]
        sp = recolor(src, rgb, 0.55 if is_dry else 0.45, base_dark=0.55)
        c.place(sp, (root, 0.995), lean, length, flip=rng.random() < 0.5)
    for i in range(seed_count):
        # A stem and a nodding seed head above the leaves.
        root = rng.choice(roots) + rng.gauss(0, 0.02)
        lean = (root - 0.5) * 60 + rng.gauss(0, 10)
        L = height * rng.uniform(0.85, 1.05)
        rad = math.radians(lean)
        tip = (root + math.sin(rad) * L, 0.995 - math.cos(rad) * L)
        c.line([(root, 0.995), tip], 3, (150, 140, 92))
        seed_head(c, tip, lean, L * 0.35, rng)
    c.save(name)


def seed_head(c, tip, lean, length, rng):
    """An open, nodding panicle like California oatgrass / fescue in seed: a straw-coloured axis with short side
    branches, each ending in a narrow spikelet that hangs off it."""
    rad = math.radians(lean + rng.uniform(-10, 25))
    axis_dir = (math.sin(rad), -math.cos(rad))
    end = (tip[0] + axis_dir[0] * length, tip[1] + axis_dir[1] * length)
    c.line([tip, end], 2, (165, 150, 100))
    for k in range(rng.randint(7, 11)):
        t = 0.15 + 0.85 * k / 10
        base = (tip[0] + axis_dir[0] * length * t, tip[1] + axis_dir[1] * length * t)
        side = 1 if k % 2 == 0 else -1
        ang = rad + side * rng.uniform(0.5, 0.9)
        bl = length * rng.uniform(0.12, 0.25) * (1.1 - t * 0.5)
        b_end = (base[0] + math.sin(ang) * bl, base[1] - math.cos(ang) * bl + bl * 0.4)  # branches droop a little
        c.line([base, b_end], 1, (160, 145, 95))
        # Spikelet: a small slim ellipse hanging from the branch end.
        s = c.size
        cx, cy = b_end[0] * s, b_end[1] * s
        w, h = s * 0.006, s * rng.uniform(0.016, 0.024)
        shade = rng.uniform(0.85, 1.1)
        col = tuple(int(min(255, v * shade)) for v in (196, 178, 120)) + (255,)
        c.draw.ellipse([cx - w, cy, cx + w, cy + 2 * h], fill=col)


def make_grass(rng):
    g1 = load(os.path.join(DL, "grass_medium_01", "grass_medium_01_diff_2k.jpg"), os.path.join(DL, "grass_medium_01", "grass_medium_01_alpha_2k.png"))
    g2 = load(os.path.join(DL, "grass_medium_02", "grass_medium_02_diff_2k.jpg"), os.path.join(DL, "grass_medium_02", "grass_medium_02_alpha_2k.png"))
    # Only upright blades: the atlas also holds a couple of long horizontal ones, which stood on end become bands.
    blades = [s for s in sprites(g2, min_px=2000) if s.size[1] >= s.size[0] * 3]
    def greenness(s):
        a = np.asarray(s).astype(np.float32)
        m = a[..., 3] > 128
        r, g = a[..., 0][m].mean(), a[..., 1][m].mean()
        return g - r
    # Every blade can be tinted green; the yellower ones also serve as the dead / dry blades.
    green = blades
    dry = [b for b in blades if greenness(b) <= 8] or blades
    print("blades green/dry", len(green), len(dry))
    grass_card("Card_GrassLush", green, dry or green, rng, 70, 0.08, (70, 110, 40), (150, 135, 80), 0.9)
    grass_card("Card_GrassMixed", green, dry or green, rng, 60, 0.3, (82, 112, 45), (160, 140, 85), 0.85)
    grass_card("Card_GrassSeeding", green, dry or green, rng, 45, 0.5, (95, 110, 50), (175, 155, 95), 0.75, None, 7)
    # Poly Haven's own grass impostor clumps (bottom of grass_medium_01's atlas), as a fourth tuft shape.
    clumps = sprites(g1, min_px=5000, box=(0.0, 0.72, 1.0, 1.0))
    if clumps:
        c = Card()
        for i, cl in enumerate(clumps[:3]):
            c.place(recolor(cl, (75, 105, 45), 0.35, base_dark=0.45), (0.3 + 0.2 * i, 0.995), rng.uniform(-8, 8), 0.75)
        c.save("Card_GrassClumps")


# ---------- Ground cover ----------
def leafset(n):
    d = os.path.join(LEAF_DL, f"LeafSet{n:03d}_1K-JPG")
    return load(os.path.join(d, f"LeafSet{n:03d}_1K-JPG_Color.jpg"), os.path.join(d, f"LeafSet{n:03d}_1K-JPG_Opacity.jpg"))


def trifoliate(c, leaves, centre, size, rot, rgb, rng):
    """Three leaflets meeting at a point, seen from above (oxalis: heart-ish; vanilla leaf: broad fan)."""
    for k in range(3):
        sp = recolor(rng.choice(leaves), [v * rng.uniform(0.9, 1.1) for v in rgb], 0.5)
        c.place(sp, centre, rot + k * 120 + rng.uniform(-10, 10), size)


def make_ground_cover(rng):
    round_leaves = sprites(leafset(9), min_px=2000)
    broad = sprites(leafset(24), min_px=2000)
    # Oregon oxalis: a carpet of small trifoliate leaves, bright clover green.
    c = Card()
    for i in range(38):
        r, a = math.sqrt(rng.random()) * 0.38, rng.uniform(0, 6.28)
        trifoliate(c, round_leaves, (0.5 + math.cos(a) * r, 0.5 + math.sin(a) * r), rng.uniform(0.07, 0.11), rng.uniform(0, 360), (70, 135, 45), rng)
    c.save("Card_Oxalis")
    # Vanilla leaf: a few big three-part leaves, slightly overlapping.
    c = Card()
    for i in range(5):
        r, a = math.sqrt(rng.random()) * 0.22, rng.uniform(0, 6.28)
        trifoliate(c, broad, (0.5 + math.cos(a) * r, 0.5 + math.sin(a) * r), rng.uniform(0.2, 0.26), rng.uniform(0, 360), (85, 130, 50), rng)
    c.save("Card_VanillaLeaf")


if __name__ == "__main__":
    os.makedirs(TEX_OUT, exist_ok=True)
    os.makedirs(CARD_OUT, exist_ok=True)
    rng = random.Random(11)
    if "--cards-only" not in sys.argv:
        pack_sets()
    make_grass(rng)
    make_ground_cover(rng)
