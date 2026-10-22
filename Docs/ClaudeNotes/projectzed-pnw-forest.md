---
name: projectzed-pnw-forest
description: ProjectZed world was turned into PNW rainforest by PNWForestBuilder.cs (2026-10-20); rebuild order and asset sources
metadata:
  node_type: memory
  type: project
  modified: 2026-10-21T01:07:29.258Z
---

On 2026-10-20 the ProjectZed world got a Pacific Northwest makeover: Tools > Environment > Build PNW Forest
(Assets/Editor/PNWForestBuilder.cs) must run after Build Forest Field and Build World. Assets are all CC0 (Poly Haven +
ambientCG) in Assets/Environment/PNW; Blender/Scripts/pnw_textures.py and pnw_models.py prepare them. Notes and
screenshots are in Docs/PNW_Forest.md and Docs/PNW_Screenshots.

Wind (2026-10-20): GPU vertex wind via shader ProjectZed/WindLit (Assets/Shaders/Wind, generated from URP Lit) on
trees, grass, undergrowth and garments; driven by WindManager on the scene's "Weather" object (SetWind / presets /
GetWindAt / Changed). The user is building a full weather system on top of it, so keep that API stable and optimized.

**Why:** the user wanted real PNW species, undergrowth and ground, using free commercial-use assets, with licence and size confirmed before each download.

**How to apply:** for later environment work, extend the PNW builder rather than ForestBuilder's low-poly trees. Editor perf is ~30 ms CPU at the field edge, so watch tree and fern density. Related: [[projectzed-shooter]], [[prefer-existing-assets]]
