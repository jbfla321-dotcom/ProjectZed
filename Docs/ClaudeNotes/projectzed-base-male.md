---
name: projectzed-base-male
description: "The from-scratch naked base male (MPFB/MakeHuman, CC0) that replaced the BlendSwap-sculpt base body; how it's built and set up in Unity"
metadata:
  node_type: memory
  type: project
  modified: 2026-10-20T23:36:43.962Z
---

2026-10-20: user gave up on the BlendSwap-sculpt base body ([[projectzed-base-body]]) and asked for a completely naked, anatomically complete male built from scratch, accurate to references.

- Build: `Blender/Scripts/make_human.py` → `Blender/Player/BaseMale.blend` + `Assets/Characters/BaseMale/BaseMale.fbx` (+ Textures). MPFB human, MACROS weight/muscle 0.72 chosen by scanning against ANSUR II / NHANES (stature 174.5, bideltoid 51, hip 36, chest ~91, waist ~78: fit average man). Skin young_caucasian_male, brown eyes, eyebrow001, eyelashes01, teeth, tongue; mixamo_unity rig, T-pose applied as rest. Genitals from MakeHuman's helper-genital fitting geometry (plate removed, subdivided, penis widened to 2.96 cm dia per Veale 2015, scrotum fuller), parented/weighted to Hips. Skin texture is island-padded.
- Assets: CC0 MakeHuman packs (system assets 267 MB, skins02 72 MB) downloaded with user OK to `Blender/Sources/MakeHuman`, installed into `%APPDATA%/Blender Foundation/Blender/5.2/mpfb/data`.
- Review: `human_review.py` (measurements + Eevee renders).
- Unity: `Assets/Editor/BaseMaleSetup.cs` (Tools > Characters > Set Up Base Male): Humanoid import, own avatar (valid), URP materials (eyes transparent so the cornea shell doesn't hide the iris; brow/lash cutout with coverage-preserving mips), places `BaseMale_Mannequin` (relaxed idle) 3 m in front of the player, removing BaseBody_Mannequin. 2026-10-20: Player, NPC_New and NPC_Current switched via Tools > Characters > Apply Base Male to All Characters (BaseMaleSetup.ApplyToAll: replaces Ch17 meshes+skeleton under each Body, keeps WorldGun/components, sets BaseMaleAvatar, then RagdollBuilder.Build). Verified in Play mode. Pre-swap scene backup: `My project/SceneBackups/ProjectZed_before_BaseMale_*.unity`. BaseBodyBuilder menu items now refuse (no Ch17_Body). Bones are now `mixamorig:` not `mixamorig1:`.
- Dark grainy bands where arms are near the torso (and old body's back streaks) = project SSAO in Settings/PC_Renderer.asset (radius 0.3, downsampled, 2 samples), not the model. Not changed; asked user.

**Why:** user wants an accurate naked human base to fit outfits on later.
**How to apply:** rerun make_human.py then the Unity menu item after changes; check with human_review.py and Play-mode snapshots.
