import bpy, sys
from mathutils import Vector
path = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.open_mainfile(filepath=path, load_ui=False)
for o in bpy.data.objects:
    line = f"{o.type:9} {o.name!r} parent={o.parent.name if o.parent else None} loc={tuple(round(c,3) for c in o.location)} scale={tuple(round(c,3) for c in o.scale)} rot={tuple(round(c,3) for c in o.rotation_euler)}"
    if o.type == 'MESH':
        bb = [o.matrix_world @ Vector(c) for c in o.bound_box]
        mn = Vector([min(p[i] for p in bb) for i in range(3)]); mx = Vector([max(p[i] for p in bb) for i in range(3)])
        me = o.data
        line += f"\n          verts={len(me.vertices)} faces={len(me.polygons)} min={tuple(round(c,3) for c in mn)} max={tuple(round(c,3) for c in mx)}"
        line += f" vgroups={len(o.vertex_groups)} uvs={[u.name for u in me.uv_layers]} shapekeys={len(me.shape_keys.key_blocks) if me.shape_keys else 0}"
        line += f" mods={[(m.type, m.name) for m in o.modifiers]} mats={[m.name if m else None for m in me.materials]}"
        tris = sum(len(p.vertices) - 2 for p in me.polygons); quads = sum(1 for p in me.polygons if len(p.vertices) == 4)
        line += f" tris={tris} quads={quads}"
    if o.type == 'ARMATURE':
        line += f"\n          bones={len(o.data.bones)}: " + ", ".join(b.name for b in o.data.bones)
        for b in o.data.bones:
            if any(k in b.name.lower() for k in ("upperarm", "upper_arm", "arm", "hand", "hips", "pelvis", "head")):
                h = o.matrix_world @ b.head_local; t = o.matrix_world @ b.tail_local
                line += f"\n            {b.name}: {tuple(round(c,3) for c in h)} -> {tuple(round(c,3) for c in t)}"
    print(line)
for img in bpy.data.images:
    print("IMAGE", img.name, img.filepath, img.size[:], "packed" if img.packed_file else "")
