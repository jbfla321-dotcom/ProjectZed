"""Prepares the downloaded Poly Haven (CC0) texture sets for ProjectZed's houses.

Usage: python -I house_textures.py <download dir with one folder per set> <project Assets/Environment/Houses/Textures>

Same layout as pnw_textures.py: <set>_color.jpg, <set>_normal.jpg (OpenGL) and a packed <set>_mask.png
(R metallic, G occlusion, B height from colour luminance, A smoothness = 1 - roughness), which URP Lit reads as
its metallic-gloss and occlusion maps. Sets listed in ROTATE are turned 90 degrees so their boards run
horizontally (the house meshes' UVs run u along the wall, v up).
"""
import os
import shutil
import sys

import numpy as np
from PIL import Image

DL, OUT = sys.argv[1], sys.argv[2]
os.makedirs(OUT, exist_ok=True)
ROTATE = set(sys.argv[3].split(",")) if len(sys.argv) > 3 and sys.argv[3] else set()
# Painted / dyed surfaces that the game tints per house: their colour map is made grey (detail kept, mean ~0.8)
# so the material tint is the colour you see.
TINTABLE = {"white_planks_clean", "painted_plaster_wall", "dirty_carpet", "decrepit_wallpaper", "square_tiled_wall"}


def load(path):
    img = Image.open(path).convert("RGB")
    return img


for name in sorted(os.listdir(DL)):
    src = os.path.join(DL, name)
    if not os.path.isdir(src):
        continue
    diff = os.path.join(src, f"{name}_diff_2k.jpg")
    if not os.path.exists(diff):
        continue
    rot = name in ROTATE
    col_img = load(diff)
    nor_img = load(os.path.join(src, f"{name}_nor_gl_2k.jpg"))
    arm_img = load(os.path.join(src, f"{name}_arm_2k.jpg"))
    if rot:
        col_img = col_img.transpose(Image.Transpose.ROTATE_90)
        arm_img = arm_img.transpose(Image.Transpose.ROTATE_90)
        # Rotating a tangent-space normal map 90 degrees CCW also turns its vectors: (x, y) -> (-y, x)
        n = np.asarray(nor_img.transpose(Image.Transpose.ROTATE_90)).astype(np.float32)
        x, y = n[..., 0].copy(), n[..., 1].copy()
        n[..., 0], n[..., 1] = 255.0 - y, x
        nor_img = Image.fromarray(n.clip(0, 255).astype(np.uint8), "RGB")
    if name in TINTABLE:
        c = np.asarray(col_img).astype(np.float32)
        g = c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722
        g = g / max(g.mean(), 1.0) * 0.8 * 255.0
        # keep a hint of the original hue (stains, pattern) so it isn't flat grey
        c = (np.stack([g] * 3, axis=-1) * 0.85 + c / max(c.mean(), 1.0) * 0.8 * 255.0 * 0.15)
        col_img = Image.fromarray(c.clip(0, 255).astype(np.uint8), "RGB")
    col_img.save(os.path.join(OUT, f"{name}_color.jpg"), quality=92)
    nor_img.save(os.path.join(OUT, f"{name}_normal.jpg"), quality=95)
    arm = np.asarray(arm_img).astype(np.float32)
    col = np.asarray(col_img).astype(np.float32)
    lum = col[..., 0] * 0.2126 + col[..., 1] * 0.7152 + col[..., 2] * 0.0722
    lum = (lum - lum.min()) / max(1.0, lum.max() - lum.min()) * 255.0
    mask = np.stack([arm[..., 2], arm[..., 0], lum, 255.0 - arm[..., 1]], axis=-1)
    Image.fromarray(mask.clip(0, 255).astype(np.uint8), "RGBA").save(os.path.join(OUT, f"{name}_mask.png"))
    avg = col.reshape(-1, 3).mean(axis=0) / 255.0
    print(f"packed {name}{' (rotated)' if rot else ''}  avg colour {avg.round(2).tolist()}")
