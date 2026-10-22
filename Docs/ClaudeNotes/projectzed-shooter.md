---
name: projectzed-shooter
description: "The user's \"3D shooter\" is the Unity 6 (6000.6.6f1) project ProjectZed at C:\\Users\\josht\\My project"
metadata:
  node_type: memory
  type: project
  modified: 2026-10-22T00:21:01.913Z
---

"Our 3D shooter" = Unity project at `C:\Users\josht\My project`, main scene `Assets/ProjectZed.unity` (DayZ/Zomboid-style survival shooter). Not the old SpaceShooter3D.cs prototype, and not "My project (1)" (empty template).

Code lives in `Assets/Scripts` (FPSController, HitscanGun, WeaponStance, PlayerCharacterAnimator, CameraModeSwitcher, PatrolWalker NPC, Characters/ ragdoll+health, Vehicles/ car+entry) and editor generators in `Assets/Editor` (Tools menu: Build Animations / Build Candidate, Build Ragdolls, Build Drivable Cars, Build Forest Field).

On 2026-10-20 the candidate clips (Action Adventure "idle (3)" + rifle pack's own forward walk/run/diagonals) were promoted to the live PlayerCharacter.controller, used by the player and both NPCs. Imported Mixamo clips now use the character's avatar (Copy From Other); "Create From This Model" had caused a tilted foot during walks.

Animation direction: match DayZ/Arma (references + before/after shots in My project/Docs). User chose upright idle (rifle pack idle) and walk/run (Action Adventure walking/running straight ahead, rifle-pack sides/back) over the hunched rifle-pack forward clips. Reload pose is DayZ-style (low at chest, muzzle up-left, ~70° roll). Ask before picking between animation options.

The project is URP. On 2026-10-20 an HDRP package + 3.9 GB HDRP terrain demo were added (broke compilation); with the user's OK I removed HDRP from the manifest and moved the demo to "My project/_Disabled (HDRP demo)".

World (2026-10-20): Assets/Editor/WorldBuilder.cs (Tools > Environment > Build World) builds a 2048 m WorldTerrain around the original 320 m ForestTerrain (hole in the middle, edges matched), roads + 6 placeholder locations from lists at the top of the file. It restores ForestTerrain from Assets/Environment/World/ForestTerrain_Original.asset each run. The field's old Boundary walls are deactivated. Editor-mode camera captures need an enabled camera.

Dev console (2026-10-20): Assets/Scripts/Dev/DevConsole.cs, auto-created in Editor/dev builds, ` toggles; commands help, fly [speed], tp, pos, spawn car|mag|crate, give mags|rifle. Fly mode lives in FPSController (SetFlying). Only one weapon (the M4) exists so far.

Items (added 2026-10-20, Assets/Scripts/Items): CarriedRifle (droppable rifle, holds its mag rounds), MagazinePouch + PlayerInteractor on Player (F use/pick up, G drop rifle), DroppedMagazine (ejected on reload), AmmoCrate (former LooseCrate_4, 99 mags, floating label). "Item" physics layer 10 ignores Character/Hitbox.

The user usually has the Editor open: drive it live with the `unity` CLI (`unity command eval_file --file x.cs ...`, editor_play/stop, capture_game_view; capture save paths must be inside the project). simulate_key "press" taps can miss wasPressedThisFrame; held keys (down/up) work for isPressed.

Unity can be driven headless when the editor is closed: `"C:/Program Files/Unity/Hub/Editor/6000.6.6f1/Editor/Unity.exe" -batchmode -quit -projectPath "C:/Users/josht/My project" -executeMethod Class.Method`. Unity wipes the project's Temp/ on exit, so write diagnostic output elsewhere.

Crashes (2026-10-21): two Editor crashes at the same Unity.dll offset 0x323d604 (`GlobalKeywordState::operator!=` in the SRP Batcher job), one when entering Play with Reload Domain disabled. It's an engine bug, not our code. Full logs are in `My project/Logs/Editor.log` (the crash-folder Editor.log is only a stub). With the user's OK, Enter Play Mode Options were turned off (full domain and scene reload) in ProjectSettings/EditorSettings.asset. If the crash returns, upgrade the 6.6 patch release. A scratchpad minidump parser compares fault offsets.
