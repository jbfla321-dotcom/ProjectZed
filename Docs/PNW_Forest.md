# ProjectZed: Pacific Northwest forest

The world is now damp PNW temperate rainforest: tall Douglas fir, hemlock, cedar and spruce, alder and big-leaf
maple along the edges, a ferny, mossy floor with fallen logs, foot trails, gravel tracks, and overcast light with fog.
Screenshots (Play mode): `Docs/PNW_Screenshots/`.

## How to rebuild

`Tools > Environment > Build PNW Forest` (`Assets/Editor/PNWForestBuilder.cs`). Run it last, in this order:
Build Forest Field → Build World → Build PNW Forest. Re-running it on its own is fine. It reads forest, meadow and
road areas from the terrains' existing paint and the `World/Roads` meshes, then replaces both terrains' layers,
trees and details. `WorldBuilder.RestoreTile` now restores the field terrain's original layers and prototypes first,
so Build World still works after a PNW build.

Backups from before this change: `SceneBackups/ProjectZed_before_PNW_20261020_2006.unity` and
`SceneBackups/PNW_terrain_backup/` (ForestTerrain.asset, WorldTerrain.asset).

## What was added, and where

| What | Where |
|---|---|
| 6 tree species (prefabs with 2 LODs + trunk capsule collider), generated from real bark textures and foliage cards | `Assets/Environment/PNW/Generated/Prefabs/Tree_*.prefab` |
| Douglas fir 46 m (~38% of stems), western hemlock 34 m (~24%), western red cedar 33 m with flared base (~12%, more in wet spots), Sitka spruce 42 m (~7%), red alder 22 m and big-leaf maple 20 m (edges, road verges, clearings). About 30% of hemlock/cedar are understory saplings. | both terrains: ~78k trees in total, ~6 m spacing in full forest |
| Stumps (Poly Haven, as terrain trees with colliders) | `Generated/Prefabs/Stump_*.prefab` |
| Fallen logs (9–16 m) and mossy rocks, with mesh colliders | scene: `PNW_Props/Props_ForestTerrain`, `PNW_Props/Props_WorldTerrain` (~1,450) |
| Field cover rocks became mossy boulders in the same spots; the old low-poly `Rocks` object is switched off, not deleted | `PNW_Props/FieldRocks` |
| Undergrowth (terrain details, GPU-instanced): sword fern ×4 variants, salal, Oregon grape, red huckleberry, thimbleberry (thickest at forest edges), moss cushions on the mossy ground; the original meadow grass is kept in the open | `Generated/Prefabs/Detail_*.prefab` |
| Ground: meadow grass, dirt (trails and yards), needle duff, moss floor, mossy rock (on slopes over ~30°), gravel (road shoulders and the unpaved tracks) | `Generated/Layer_*.terrainlayer` |
| Foot trails: 7 from the field, 2 from each location, ending at a road or the map edge (painted dirt, cleared of plants) | painted on both terrains |
| Roads: dirt tracks now use the gravel texture; asphalt is darker and wet-looking | `Assets/Environment/World/M_Road_*.mat` |
| Atmosphere: overcast HDRI sky, weak high soft sun, grey-green trilight ambient, exp² fog (density 0.011, gone by ~170 m) | scene lighting settings |

SSAO in `PC_Renderer` is untouched.

## Asset sources (all CC0: public domain, commercial use OK, no attribution required)

Downloaded 2026-10-20, ~196 MB raw. In the project after packing: ~270 MB in `Assets/Environment/PNW`.

- **Poly Haven** (polyhaven.com, CC0)
  - Textures, 2k: forest_leaves_04, forrest_ground_01, mud_forest, gravel_road, mossy_rock, leafy_grass (ground);
    bark_brown_02, pine_bark, japanese_cedar_bark, tree_bark_03, japanese_sycamore, trident_maple_bark (bark)
  - Models, 1k glTF, decimated: fern_02, moss_01, rock_moss_set_01, rock_moss_set_02, tree_stump_01, tree_stump_02,
    dead_tree_trunk, dead_tree_trunk_02, boulder_01
  - Twig maps only from fir_tree_01 (2k); HDRI kloofendal_overcast_puresky (2k)
- **ambientCG** (ambientcg.com, CC0): leaf atlases LeafSet009, 010, 019, 022, 024, 025 (1K)

Processing scripts (re-runnable): `Blender/Scripts/pnw_textures.py` packs ARM maps into URP mask maps and composes the
10 foliage cards (`Assets/Environment/PNW/Foliage`). `Blender/Scripts/pnw_models.py` decimates and exports the models
(`Assets/Environment/PNW/Models`).

## Notes and known limits

- Performance in the editor at the field edge: ~11M tris, ~29k draw calls, CPU ~30 ms / GPU ~15 ms per frame.
  The main levers are `TreeSpacing`, the fern densities in `BuildDetailPrototypes`, and `detailObjectDistance` (70 m).
- No wind animation on foliage yet. Trees have no billboard LOD; the fog and the 190 m tree distance cover that.
- Species are stylised from leaf/needle photos, not botanically exact (e.g. Oregon grape uses salal-like leaflets).

## Wind (for the weather system)

Trees, grass, undergrowth and clothing move in the wind, all on the GPU. The only CPU cost is one script setting three
global shader values per frame, so it doesn't matter how many plants there are.

**Driving it:** the scene's `Weather` object has a `WindManager` (`Assets/Scripts/Weather/WindManager.cs`) and a
`WindZone`. The weather system only needs:

```csharp
WindManager.Instance.SetWind(WindManager.Storm, 20f);              // blend to a preset over 20 s
WindManager.Instance.SetWind(direction, strength, gustiness, turbulence, seconds);
Vector3 w = WindManager.Instance.GetWindAt(pos);                    // local wind in m/s, gusts included
WindManager.Instance.Changed += state => { ... };                   // react to changes (audio, rain angle...)
```

- `WindState` has four fields. `direction` is the compass heading the wind blows towards. `strength` runs from 0 (calm) to 1 (storm), and `gustiness` and `turbulence` are each 0 to 1. The presets are `Calm`, `Breeze`, `Windy` and `Storm`.
- Changing settings never makes anything jump, because the sway and flutter phases accumulate over time.
- Particle systems (rain, falling leaves, smoke) follow the wind through the `WindZone` if their "External Forces" module is ticked.
- Simulated cloth (`Cloth` components) gets the wind as an acceleration. It's updated 5 times a second, and only within 40 m of the camera. Register cloth spawned later with `WindManager.Register(cloth)`.
- Dev console (`` ` ``): `wind storm`, `wind calm`, or `wind <0-1> [direction] [seconds]`. `wind` on its own shows the current settings.
- In the editor you can change `current` on `Weather/WindManager` and see the result. It also runs outside Play mode.

**The shader:** `ProjectZed/WindLit` (`Assets/Shaders/Wind`) is URP Lit plus vertex wind in the forward, shadow, depth and depth-normals passes. SSAO, shadows, Forward+ and GPU instancing all work, and it stays compatible with the SRP Batcher.

Each material picks one of three modes:
- `_WIND_TREE`: bends with height and flutters toward the branch tips.
- `_WIND_PLANT`: bends with height, for grass, ferns and shrubs.
- `_WIND_CLOTH`: ripples the cloth outward, so fabric never sinks into the body.

There are two per-material vectors: `_WindParams` (bend height, sway in metres, flutter in metres, flutter radius) and `_WindParams2` (sway speed, flutter speed). The builders set both: Build PNW Forest for plants, and ClothingTool for garments. Leaf and fabric flutter fades out between 45 and 85 m, where it would only shimmer.

- If URP is upgraded, regenerate `WindLit.shader` from the new `Lit.shader` and re-copy `LitInput.hlsl` into `WindLitInput.hlsl` (adding the two `_Wind` lines back).
- Meadow grass is now a small mesh clump with the wind shader, not the terrain's billboard grass, which can't take a custom shader.

Editor measurement at the field edge in a storm: about 6 ms CPU and 3–6 ms GPU per frame, 5.7–7M triangles.

## Tree variety, tops and pop-in (2026-10-21)

- **Variants:** each species is built in 4 shapes (`Tree_<Species>_1..4`). Variant 1 is narrow and high-crowned, 2 is full and low-crowned, and 3 is old and irregular. On variant 3, cedars get a dead spike top and Douglas fir and spruce a snapped top with new leaders.
  - Every tree also gets its own crown scale and bulges, a one-sided lean of the crown, gaps, a trunk lean and bow, and dead lower branch stubs.
  - There are also 2 snags (standing dead, broken-off firs) at about 2% of forest stems.
  - Prototype layout: species × 4, then the snags, then the stumps (`TreeProto` / `SnagProto` / `StumpProto`).
- **Tops:** a spire of up-angled sprays plus a proper upright leader; hemlock's leader nods over. The old top cards were sideways sprays stood on end.
- **LOD pop:** both LODs now come from the same seed and the same random sequence, so the far version is the same tree with every other branch as a larger card. Before, the two drifted apart into different trees. The LODGroups crossfade, and the switch is at 50% of screen height.
- **Undergrowth pop:** `_WIND_PLANT` meshes shrink to nothing between 72% and 97% of the terrain's detail distance (`WindManager.detailFadeStart`), so they grow in rather than popping. World terrain detail patches are now about 16 m (they were 64 m), so far plants are culled instead of drawn at zero size.
- Tree distance is 175 m; the fog has covered everything by then.
- **Measured in the editor (Play mode):** about 10.5M triangles at the field edge and 14.5M deep in the forest. That includes the SSAO depth pass and the shadow cascades, which draw the trees again. Frame times in the editor are noisy (11–26 ms CPU); measure in a build for real numbers.

## Trunks showing through the crowns (2026-10-21)

The conifer stems were visible right up through the foliage. There were three causes, and each is fixed in `BuildConifer`:
- **Hollow core:** the first ~12% of each spray texture is bare twig, and the cards started at the bark, so there was a needle-free tube around the stem all the way up. Cards now start just behind the stem, so the needles begin at the trunk.
- **No inner foliage:** real crowns are dense near the stem (short inner shoots and sprouts, densest toward the top), while ours were only flat sprays radiating outward. Each conifer now gets crossed upright "core" sprays on the stem every 2.2 m through the crown, wider on the far version, and the same at both LODs.
- **Stem too thick and too bright:** the trunk now tapers faster up the crown (exponent 0.85 → 1.2). From 1 m above the lowest branches it uses `BarkCrown_<Species>`, the same bark at about 37% brightness, as stand-in shade for the crown blocking the sky (game trees normally bake this in as ambient occlusion).
- **Cost:** about 0.5M extra triangles on screen (11.1M at the field edge, 15.1M on the ridge).

## Ground and grass pass (2026-10-21)

**Research basis:**
- PNW lowland forest floors are over 90% litter (needles and rotting wood), with moss on 20% or more (USGS cedar/hemlock forest description).
- Oregon oxalis carpets the moss in damp shade, and vanilla leaf is about the commonest forest-floor herb, growing in big patches (NPS Olympic handbooks).
- Native meadows are bunchgrass prairie: Roemer's / Idaho / red fescue, California oatgrass, tufted hairgrass in wet spots, and camas (Willamette Valley ecoregion, USGS prairie descriptions).
- Game technique: mix textures at different scales and add macro colour variation against tiling; darken grass blades near the base and lighten the tips; clump blades into tufts; light grass with up-facing normals so it matches the ground.

**New assets (CC0, approved before download):**
- ambientCG Moss003 and Grass004 (1K)
- Poly Haven forest_leaves_03 (2K)
- Poly Haven grass_medium_01 / grass_medium_02 blade textures (2K)

`Blender/Scripts/pnw_ground.py` packs these and composes the new cards: `Card_GrassLush/Mixed/Seeding/Clumps`, `Card_Oxalis` and `Card_VanillaLeaf`.

**Terrain (8 layers, still 2 passes):**
- Meadow = leafy_grass + **grass004 thatch**, split by a wet/dry "dryness" field.
- Forest floor = orange duff + **dark needle litter (forest_leaves_03)**, mixed in drifts.
- Moss is now **Moss003**, a brighter moss carpet.
- Moss is painted in rings around the bases of trees (taller than half size), stumps and snags, and along fallen logs (`AddMossCollars`).

**Undergrowth:**
- **Grass:** 4 bunchgrass tufts (3 crossed cards, normals 75% up) replace the old blade clumps. Lush where it's wet, seeding oatgrass/fescue where it's dry, mixed everywhere, plus a few Poly Haven clump impostors. Material tints above 1 lift the composed cards, which come out dark.
- **Forest-floor herbs:** Oregon oxalis (flat leaf cards on moss), vanilla leaf patches, and deer fern (small, dark sword-fern mesh) only in the wettest spots.

**Measured (editor, Play mode):** 12.1M triangles at the field edge and 15.6M in the forest, about 14 ms CPU / 14 ms GPU at both.

Screenshots: `Docs/PNW_Screenshots/G0_before_*` vs `G3_*`.
