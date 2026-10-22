---
name: projectzed-clothing
description: How clothing is built (Blender make_clothes.py) and worn (Unity ClothingTool) for the base male; HeartBoxers done
metadata:
  node_type: memory
  type: project
  modified: 2026-10-20T23:51:16.830Z
---

Clothing pipeline (started 2026-10-20) for the base male ([[projectzed-base-male]]):
- Blender: `Blender/Scripts/make_clothes.py` cuts each garment from BaseMale_Body's surface (bisect planes relative to the Hips joint), so it inherits his shape + skin weights; lifts off the skin (ease), pouch over genitals, solidify, smart UV, then bakes a print computed in body space (position + normal bake → numpy pattern → pad_islands). Output `Blender/Player/Clothes_<Name>.blend`, `Assets/Characters/Clothing/<Name>/<Name>.fbx` + `_Albedo.png`. Review: `clothes_review.py` (stand/stride/crouch renders, genitals hidden).
- Unity: `Assets/Editor/ClothingTool.cs` — Tools > Characters > Clothing > Wear Heart Boxers (All Characters) / Undress All. Binds garment SMRs to each character's bones by name under `Clothing_<Name>`; hides BaseMale_Genitals while a groin-covering garment is worn (Garments table). Applies to every model with a BaseMale_Body (player, NPCs, mannequin).
- Done: HeartBoxers (white cotton, red hearts, red ribbed waistband, mid-thigh boxers). User: hearts cut at hems/seams are fine (looks like real fabric).
- Garments must look like separate fabric, not paint (user, 2026-10-20): make_clothes bridges hollows (outward-only smoothing, BRIDGE_ITERATIONS) and stretches the seat across the buttock crease (SEAT_SPAN/SEAT_SAG). Apply the same to future garments (e.g. between pecs/shoulder blades for shirts).
- User idea (not built yet): pose-driven fold detail, e.g. 2-3 wrinkle textures swapped/blended as he moves (games call these wrinkle maps: normal maps blended by joint angles).
- Next per user: more clothes for the guys.

**Why:** user is building a wardrobe for the survival game's characters.
**How to apply:** add a build_<garment>() in make_clothes.py and a row + menu item in ClothingTool.
