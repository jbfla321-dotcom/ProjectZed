---
name: projectzed-houses
description: "ProjectZed houses (2026-10-22) - Blender make_house.py plans + Unity HouseBuilder replacing placeholder boxes; doors, shattering glass, indoor audio"
metadata:
  node_type: memory
  type: project
  modified: 2026-10-22T16:03:37.506Z
---

Houses suite started 2026-10-22 for [[projectzed-shooter]] (houses only; businesses/other buildings later). User picked 80s-90s rural PNW, shattering windows, and checking BlendSwap CC0 furniture first.

- Blender: `Blender/Scripts/house_plans.py` (plans in Unity coords: rooms, openings, furniture by wall/fraction) -> `make_house.py` (Exterior/Interior/Ramps FBX + JSON per type, House_Kit.fbx doors+furniture; warns on furniture overlaps/doorways/windows) -> `house_review.py` renders to Docs/Houses. Types: Cabin, Rambler, Farmhouse (2 storey, stairs), Shack.
- Unity: Tools > Environment > Build Houses (`Assets/Editor/HouseBuilder.cs`, run after Build World + Build PNW Forest; headless `HouseBuilder.BuildFromCommandLine`), Capture Houses (`HouseCapture.cs`). Picks type per placeholder by clearance + site list, faces nearest road, random paint/finishes/furniture, some pre-broken windows, clears terrain trees/details, LODGroup hides interiors far away.
- Runtime: World/Door.cs (IInteractable), World/BreakableGlass.cs (IBulletReceiver, radial fracture), World/House.cs (rooms -> SoundManager Indoor snapshot), SurfaceType.Glass added; bullets pass broken panes.
- Textures (Poly Haven CC0, approved + downloaded 2026-10-22): `house_textures.py <dl> Assets/Environment/Houses/Textures white_planks_clean` packs color/normal/mask; paintable sets are greyed so HouseBuilder tints drive colour; tiles match real-world size. Raw downloads in C:/Users/josht/Downloads/ProjectZed_Houses_dl.
- BlendSwap (approved, 7 packs downloaded 2026-10-22): only medieval (rustic chair/table/bed/chest), metal bedframe and the living-room leather couch were usable; `import_furniture.py` -> House_KitImported.fbx + json alternates (replaces kit items per house type). Kitchen/bathroom/mission/garden packs too heavy or modern.
- Sounds (approved 2026-10-22): Door_Open/Door_Close/Glass_Break events (Assets/Audio/World, slice.py `at` mode cuts at given seconds; AudioTool Defs). Rebuild order after changing them: Build Sound Events -> Build Houses. Not yet play-tested by hand.
