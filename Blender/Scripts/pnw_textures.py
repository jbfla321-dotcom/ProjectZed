"""Prepares the downloaded CC0 textures for ProjectZed's PNW forest (Poly Haven + ambientCG).

Usage: python -I pnw_textures.py <download dir> <project Assets/Environment/PNW dir>

- Ground and bark sets (Poly Haven diff / nor_gl / arm) are copied as <name>_color.jpg / <name>_normal.jpg plus a packed
  <name>_mask.png: R metallic, G occlusion, B height (colour luminance), A smoothness (1 - roughness). That layout is
  both the URP terrain layer mask map and, read as metallic-gloss + occlusion, the URP Lit maps.
- Foliage cards are composed from individual leaves / twigs cut out of the leaf atlases: each card is a whole branch
  spray or leafy cluster on one RGBA texture, so a tree or bush only needs a few alpha-tested quads.
"""
import math
import os
import random
import shutil
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage

DL, OUT = sys.argv[1], sys.argv[2]  # optional 3rd arg --cards-only
TEX_OUT = os.path.join(OUT, "Textures")
CARD_OUT = os.path.join(OUT, "Foliage")
os.makedirs(TEX_OUT, exist_ok=True)
os.makedirs(CARD_OUT, exist_ok=True)

SETS = ["forest_leaves_04", "forrest_ground_01", "mud_forest", "gravel_road", "mossy_rock", "leafy_grass",
        "bark_brown_02", "pine_bark", "japanese_cedar_bark", "tree_bark_03", "japanese_sycamore", "trident_maple_bark"]


def pack_sets():
    for name in SETS:
        src = os.path.join(DL, "tex", name)
        diff = os.path.join(src, f"{name}_diff_2k.jpg")
        shutil.copyfile(diff, os.path.join(TEX_OUT, f"{name}_color.jpg"))
        shutil.copyfile(os.path.join(src, f"{name}_nor_gl_2k.jpg"), os.path.join(TEX_OUT, f"{name}_normal.jpg"))
        arm = np.asarray(Image.open(os.path.join(src, f"{name}_arm_2k.jpg")).convert("RGB")).astype(np.float32)
        col = np.asarray(Image.open(diff).convert("RGB")).astype(np.float32)
        lum = col[..., 0] * 0.2126 + col[..., 1] * 0.7152 + col[..., 2] * 0.0722
        lum = (lum - lum.min()) / max(1.0, lum.max() - lum.min()) * 255.0
        mask = np.stack([arm[..., 2], arm[..., 0], lum, 255.0 - arm[..., 1]], axis=-1)
        Image.fromarray(mask.clip(0, 255).astype(np.uint8), "RGBA").save(os.path.join(TEX_OUT, f"{name}_mask.png"))
        print("packed", name)


# ---------- Sprites ----------
def load_atlas(color, alpha):
    c = Image.open(color).convert("RGB")
    a = Image.open(alpha).convert("L").resize(c.size)
    img = c.copy()
    img.putalpha(a)
    return img


def sprites(img, min_px=400, box=None):
    """Cut every separate leaf / twig out of an atlas (connected opaque regions), cropped, sorted by size."""
    a = np.asarray(img)[..., 3] > 100
    a = ndimage.binary_closing(a, iterations=2)
    lab, n = ndimage.label(a)
    out = []
    for i, sl in enumerate(ndimage.find_objects(lab)):
        if sl is None:
            continue
        y0, y1, x0, x1 = sl[0].start, sl[0].stop, sl[1].start, sl[1].stop
        if (lab[sl] == i + 1).sum() < min_px:
            continue
        if box:
            w, h = img.size
            cx, cy = (x0 + x1) / 2 / w, (y0 + y1) / 2 / h
            if not (box[0] <= cx <= box[2] and box[1] <= cy <= box[3]):
                continue
        crop = img.crop((x0, y0, x1, y1))
        m = (lab[sl] == i + 1)
        arr = np.asarray(crop).copy()
        arr[..., 3] = np.where(m, arr[..., 3], 0)
        out.append(Image.fromarray(arr, "RGBA"))
    out.sort(key=lambda s: -s.size[0] * s.size[1])
    return out


def tint(sprite, rgb, amount):
    arr = np.asarray(sprite).astype(np.float32)
    lum = (arr[..., :3] @ np.array([0.3, 0.59, 0.11]))[..., None] / 255.0
    target = np.array(rgb, np.float32)[None, None, :] * lum * 1.6
    arr[..., :3] = arr[..., :3] * (1 - amount) + target * amount
    return Image.fromarray(arr.clip(0, 255).astype(np.uint8), "RGBA")


def shade(sprite, k):
    arr = np.asarray(sprite).astype(np.float32)
    arr[..., :3] *= k
    return Image.fromarray(arr.clip(0, 255).astype(np.uint8), "RGBA")


class Card:
    def __init__(self, size=1024):
        self.size = size
        self.img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        self.draw = ImageDraw.Draw(self.img)

    def stem(self, pts, width, color=(70, 52, 36)):
        """A twig as a tapering polyline (pts in 0..1 card space, y down)."""
        s = self.size
        for i in range(len(pts) - 1):
            w = max(1, int(width * (1 - i / len(pts)) * s / 1024))
            self.draw.line([(pts[i][0] * s, pts[i][1] * s), (pts[i + 1][0] * s, pts[i + 1][1] * s)], fill=color + (255,), width=w)

    def place(self, sprite, base, angle, length, base_at="bottom"):
        """Paste a sprite with its base at `base` (0..1), pointing along `angle` (degrees, 0 = up, 90 = right)."""
        s = self.size
        if base_at == "right":
            sprite = sprite.rotate(-90, expand=True)
        elif base_at == "left":
            sprite = sprite.rotate(90, expand=True)
        w, h = sprite.size
        L = max(2, int(length * s))
        sp = sprite.resize((max(1, int(w * L / h)), L), Image.LANCZOS)
        w, h = sp.size
        pad = int(math.hypot(w, h)) + 2
        canvas = Image.new("RGBA", (pad * 2, pad * 2), (0, 0, 0, 0))
        canvas.paste(sp, (pad - w // 2, pad - h), sp)
        canvas = canvas.rotate(-angle, resample=Image.BICUBIC)
        # Via a full-size layer, so sprites hanging over the card edge are clipped rather than rejected.
        layer = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        layer.paste(canvas, (int(base[0] * s) - pad, int(base[1] * s) - pad), canvas)
        self.img.alpha_composite(layer)

    def save(self, name):
        # Bleed leaf colour into the transparent area so mipmaps and alpha edges don't pick up a dark fringe.
        arr = np.asarray(self.img).astype(np.float32)
        a = arr[..., 3:4] / 255.0
        rgb = arr[..., :3]
        premul = Image.fromarray((rgb * a).astype(np.uint8)).filter(ImageFilter.BoxBlur(12))
        blur = np.asarray(premul).astype(np.float32)
        ablur = np.asarray(Image.fromarray((a[..., 0] * 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(12))).astype(np.float32)[..., None] / 255.0
        mean = rgb[a[..., 0] > 0.5].mean(axis=0)
        fill = np.where(ablur > 0.01, blur / np.maximum(ablur, 0.01), mean)
        out = rgb * a + fill * (1 - a)
        res = np.concatenate([out.clip(0, 255), arr[..., 3:4]], axis=-1).astype(np.uint8)
        Image.fromarray(res, "RGBA").save(os.path.join(CARD_OUT, name + ".png"))
        print("card", name)


def leafset(n):
    d = os.path.join(DL, "leaf", f"LeafSet{n:03d}_1K-JPG")
    return load_atlas(os.path.join(d, f"LeafSet{n:03d}_1K-JPG_Color.jpg"), os.path.join(d, f"LeafSet{n:03d}_1K-JPG_Opacity.jpg"))


def frond(name, sprite_list, base_at, rng, count, spread, size0, size1, tint_rgb, tint_amt, stem_w, droop=0.0, tip=True):
    """A flat conifer branch spray: an axis from the left edge to the right with sprays along both sides."""
    c = Card()
    axis = [(0.02 + 0.94 * t, 0.5 + droop * t * t) for t in np.linspace(0, 1, 12)]
    c.stem(axis, stem_w)
    for i in range(count):
        t = 0.08 + 0.86 * i / count
        side = 1 if i % 2 == 0 else -1
        x, y = 0.02 + 0.94 * t, 0.5 + droop * t * t
        sp = tint(rng.choice(sprite_list), tint_rgb, tint_amt)
        sp = shade(sp, 0.75 + 0.25 * t + rng.uniform(-0.08, 0.08))
        ang = 90 - side * (spread + rng.uniform(-10, 10))
        length = (size0 + (size1 - size0) * t) * rng.uniform(0.85, 1.1) * (1 - 0.25 * t * t)
        c.place(sp, (x, y), ang, length, base_at)
    if tip:
        sp = tint(sprite_list[0], tint_rgb, tint_amt)
        c.place(sp, axis[-3], 90, size1 * 0.9, base_at)
    c.save(name)


def cluster(name, leaves, rng, twigs, leaves_per_twig, leaf_len, tint_rgb=None, tint_amt=0.0, stem_w=10,
            reach=0.42, spread=150, stem_color=(70, 52, 36), compound=False):
    """A leafy cluster: twigs radiating from the bottom centre, leaves along and at the end of each twig."""
    c = Card()
    order = []
    for k in range(twigs):
        ang = -spread / 2 + spread * (k + 0.5) / twigs + rng.uniform(-8, 8)
        L = reach * rng.uniform(0.75, 1.0)
        rad = math.radians(ang)
        bend = rng.uniform(-0.15, 0.15)
        pts = []
        for t in np.linspace(0, 1, 8):
            a2 = rad + bend * t
            pts.append((0.5 + math.sin(a2) * L * t, 0.97 - math.cos(a2) * L * t))
        c.stem(pts, stem_w, stem_color)
        for j in range(leaves_per_twig):
            t = 0.3 + 0.7 * (j + 1) / leaves_per_twig
            p = pts[min(7, int(t * 7))]
            side = 1 if j % 2 == 0 else -1
            la = ang + (0 if j == leaves_per_twig - 1 else side * rng.uniform(35, 65))
            order.append((rng.random(), p, la, leaf_len * rng.uniform(0.8, 1.15) * (0.7 + 0.3 * t), t))
        if compound:  # pinnate leaflets (Oregon grape): pairs along the stalk
            for t in np.linspace(0.3, 0.9, 5):
                p = pts[int(t * 7)]
                for side in (-1, 1):
                    order.append((rng.random(), p, ang + side * 60, leaf_len * 0.8, t))
    order.sort()
    for _, p, la, L, t in order:
        sp = rng.choice(leaves)
        if tint_rgb:
            sp = tint(sp, tint_rgb, tint_amt)
        sp = shade(sp, 0.7 + 0.3 * t + rng.uniform(-0.1, 0.1))
        c.place(sp, p, la, L)
    c.save(name)


def make_cards():
    rng = random.Random(7)
    fir = load_atlas(os.path.join(DL, "twig", "fir_tree_01_twig_diff_2k.jpg"), os.path.join(DL, "twig", "fir_tree_01_twig_alpha_2k.png"))
    fir_twigs = sprites(fir, min_px=3000, box=(0.2, 0.0, 1.0, 0.82))
    print("fir twigs", len(fir_twigs), [s.size for s in fir_twigs])
    sprays = sprites(leafset(19), min_px=2000)
    print("sprays", len(sprays), [s.size for s in sprays])

    # Douglas fir: soft dark-green needles; Sitka spruce: stiffer, blue-green, denser.
    frond("Card_DouglasFir", fir_twigs, "bottom", rng, 22, 55, 0.30, 0.20, (52, 78, 40), 0.35, 14, droop=0.05)
    frond("Card_SitkaSpruce", fir_twigs, "bottom", rng, 30, 62, 0.32, 0.2, (40, 64, 62), 0.55, 12, droop=-0.03)
    # Western red cedar: flat, lacy, yellow-green drooping sprays; western hemlock: finer, darker, feathery.
    frond("Card_RedCedar", sprays, "right", rng, 14, 40, 0.42, 0.28, (70, 92, 34), 0.6, 10, droop=0.12)
    frond("Card_Hemlock", sprays, "right", rng, 20, 50, 0.32, 0.2, (34, 54, 28), 0.7, 8, droop=0.08)

    maple = sprites(leafset(10), min_px=2000)
    alder = sprites(leafset(24), min_px=2000)
    salal = sprites(leafset(22), min_px=2000)
    huck = sprites(leafset(9), min_px=2000)
    thimble = sprites(leafset(25), min_px=2000)
    print("leaves", len(maple), len(alder), len(salal), len(huck), len(thimble))
    cluster("Card_BigleafMaple", maple, rng, 7, 4, 0.21, (70, 110, 35), 0.25, 12, reach=0.4)
    cluster("Card_RedAlder", alder, rng, 9, 5, 0.15, None, 0.0, 9, reach=0.42)
    cluster("Card_Salal", salal, rng, 6, 6, 0.17, (45, 80, 35), 0.45, 8, reach=0.45, stem_color=(110, 60, 40))
    cluster("Card_OregonGrape", salal, rng, 7, 1, 0.13, (34, 64, 36), 0.65, 7, reach=0.45, stem_color=(90, 50, 40), compound=True)
    cluster("Card_Huckleberry", huck, rng, 12, 7, 0.08, (70, 120, 40), 0.35, 4, reach=0.48, spread=110, stem_color=(80, 110, 50))
    cluster("Card_Thimbleberry", thimble, rng, 7, 1, 0.22, (80, 125, 45), 0.2, 6, reach=0.36, spread=130, stem_color=(90, 110, 60))


if __name__ == "__main__":
    if "--cards-only" not in sys.argv:
        pack_sets()
    make_cards()
