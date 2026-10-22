# ProjectZed

DayZ / Project Zomboid–style survival shooter set in 80s–90s rural Pacific Northwest.
Unity **6000.6.6f1**, **URP**, Windows. Main scene: `Assets/ProjectZed.unity` (the only scene that matters;
`Assets/Scenes/SampleScene.unity` and `FPSRoom.unity` are leftovers).

Two people co-develop this, each with their own Claude: **Juffowubz** (project owner) and a friend.
Deeper per-system notes live in [`Docs/ClaudeNotes/`](Docs/ClaudeNotes/README.md); read the relevant one before
changing a system.

## How to work here (rules from Juffowubz)

- **Plan first, then wait.** After any request, reply with a short game plan and **don't change anything until the
  person says "lets go"**. Answering questions is fine before that.
- **Use what's already in the project** (stored animation packs, assets) before generating or editing new
  animations/assets. Say so first if nothing stored fits.
- **Check Play mode first.** Before editor changes, check `EditorApplication.isPlaying`; if they're in Play mode, tell
  them instead of silently waiting or stopping it.
- **Ask before downloading** anything (state license + size). Assets so far are CC0 / MIT / royalty-free; keep it
  that way and record sources.
- Juffowubz likes a little light humor in replies (never at the cost of clarity, never in code/commits).
- Ask before choosing between animation/look options; show before/after renders or captures.

## Source control (Git + LFS on GitHub)

- Large binaries (`.fbx .blend .png .jpg .psd .wav .zip .obj .bytes`, big terrain/star `.asset`s) go through
  **Git LFS** (see `.gitattributes`). Run `git lfs install` once per machine.
- **Not in the repo** (rebuilt or local only): `Library/`, `Temp/`, `Logs/`, `UserSettings/`, `Builds/`,
  `SceneBackups/`, `_Disabled (HDRP demo)/`, `*.blend1`. The first open on a new machine re-imports everything
  into `Library/`; that takes a long time and is normal.
- Unity is set to Force Text serialization + visible meta files. **Always commit `.meta` files with their asset.**
- One-time per machine, so scene/prefab conflicts merge semantically:
  ```
  git config merge.unityyamlmerge.name "Unity SmartMerge"
  git config merge.unityyamlmerge.driver "'C:/Program Files/Unity/Hub/Editor/6000.6.6f1/Editor/Data/Tools/UnityYAMLMerge.exe' merge -p %O %B %A %A"
  git config merge.unityyamlmerge.recursive binary
  ```
- **Coordinate on `ProjectZed.unity`.** Almost everything lives in that one big scene, and two people editing it at
  once is the most likely source of painful conflicts. Say who's "in the scene" before touching it; prefer changes
  in scripts, prefabs and editor builder tools (which regenerate scene content) over hand-edits.
- Work on a branch, open a PR, pull `main` before starting. Never commit `Library/`.

## First-time setup on a new machine

1. Install Unity **6000.6.6f1** (Hub) with Windows build support; Git + Git LFS.
2. `git clone <repo>` then open the folder in Unity Hub. Let the import finish.
3. Open `Assets/ProjectZed.unity`.
4. Multiplayer uses Unity Cloud (Relay). The project is linked to Juffowubz's Unity Cloud org; for **Host** to work
   from the second machine that person may need to be added to the Unity Cloud project (Unity Dashboard → project →
   members). Joining by code works for anyone running the game.
5. Optional, for asset pipelines: **Blender 5.2** (`C:/Program Files/Blender Foundation/Blender 5.2/`) with the
   **MPFB** extension. Install MakeHuman CC0 assets from `Blender/Sources/MakeHuman/zip` into
   `%APPDATA%/Blender Foundation/Blender/5.2/mpfb/data`. Python 3 for `Audio/Scripts`.

External raw folders that are **not** in the repo (only needed to regenerate source assets, not to run the game):
`C:/Users/josht/SoundLibrary` (Sonniss raw audio, ~460 MB) and `C:/Users/josht/Downloads/ProjectZed_Houses_dl`
(Poly Haven texture downloads). Their processed outputs are already in `Assets/`.

## Layout

| Path | What |
|---|---|
| `Assets/Scripts/` | Runtime code: `FPSController`, `HitscanGun` (fires real bullets despite the name), `WeaponStance`, `PlayerCharacterAnimator`, `CameraModeSwitcher`, `Audio/`, `Ballistics/`, `Characters/` (ragdoll, health), `Dev/` (console), `Items/`, `Multiplayer/`, `Vehicles/`, `Weather/`, `World/` (doors, glass, houses) |
| `Assets/Editor/` | Builder tools behind the **Tools** menu (see below); most also have headless `...FromCommandLine` entry points |
| `Assets/Settings/` | URP assets: `PC_RPAsset.asset`, `PC_Renderer.asset` (renderer features: volumetric clouds, volumetric fog, SSAO) |
| `Assets/Shaders/` | `Wind/` (ProjectZed/WindLit), `Weather/` (rain) |
| `Assets/Environment/` | World terrain, PNW forest assets (CC0), houses, weather profiles |
| `Assets/Characters/` | Base male, clothing, animations (Mixamo + packs), controller |
| `Assets/Audio/` | Sliced one-shot WAVs + SoundEvent assets + CREDITS.md |
| `Assets/Resources/Multiplayer/` | Netcode prefabs (made by Tools > Multiplayer > Setup) |
| `Blender/Scripts/` | Python generators: `make_human.py`, `make_clothes.py`, `make_house.py`/`house_plans.py`, `pnw_*.py`, review/render scripts |
| `Blender/Player`, `Blender/Houses`, `Blender/Sources` | Source .blend files and downloaded CC0 sources |
| `Audio/Scripts/` | `fetch_sonniss.py`, `slice.py` (audio pipeline) |
| `Docs/` | Research notes, screenshots, `ClaudeNotes/` |
| `Packages/com.jiaozi158.unity-physically-based-sky-urp/` | **Embedded, patched** sky package (see gotchas) |

## What's built

- **Player / weapons**: first/third person (V), M4 with reloads, DayZ-style stances; dev console (`` ` ``: `fly`,
  `tp`, `spawn car|mag|crate`, `give`, `weather`, `time`). Items: droppable rifle, magazines, ammo crate (F use, G drop).
- **Ballistics**: `Assets/Scripts/Ballistics` — Arma/ACE3-style projectiles (G7 drag, air density, zeroing with
  Page Up/Down, spin drift, wind from WindManager, penetration/ricochet per surface). Tools > Ballistics > Print Range Card.
- **Audio**: SoundManager with pooled voices, distance delay/low-pass, mixer snapshots (indoor, paused); guns,
  impacts, footsteps by surface, doors, glass, rain/thunder. Tools > Audio > Build All.
- **World**: 2048 m terrain around the original field, roads, placeholder locations (Tools > Environment > Build World),
  PNW rainforest dressing + GPU wind (Build PNW Forest).
- **Houses**: Cabin, Rambler, Farmhouse, Shack built from Blender plans; opening doors, shattering windows, indoor
  sound + lighting (Build Houses, Bake House Lighting).
- **Weather / day-night**: real sun path, volumetric clouds, PB sky, volumetric fog, rain with shelter, storms and
  lightning, wet surfaces, eye adaptation; **F7** weather menu (Tools > Environment > Setup Weather).
- **Characters**: from-scratch MPFB base male on player + NPCs (Tools > Characters > Set Up Base Male / Apply Base
  Male to All Characters), clothing pipeline (Heart Boxers so far), ragdolls, patrolling NPCs.
- **Vehicles**: drivable cars (Build Drivable Cars).
- **Co-op multiplayer**: Netcode for GameObjects 2.13.3 + Multiplayer Services 2.3.3 (Relay). **F8** menu: Host
  shows a 6-letter code, the other player joins with it. Remote players are `NetPuppet` clones; bullets, doors,
  time and weather sync. Tools > Multiplayer > Setup.

### Editor tool rebuild order

`Build Forest Field` → `Build World` → `Build PNW Forest` → `Build Houses` → `Bake House Lighting`.
Audio: rerun `slice.py`, then **Tools > Audio > Build All** (then Build Houses if door/glass events changed).
Blender scripts change → rerun the matching Unity menu item. All tools are meant to be idempotent.

## In progress / not done yet

- Not synced in multiplayer yet: cars, dropped items/crates, NPCs, glass broken by non-bullets.
- Weather: hand play-test, perf measurement, player flashlight (nights indoors are pitch dark).
- Houses: hand play-test; only houses so far (businesses/other buildings later).
- Clothing: more garments; idea of wrinkle maps driven by joint angles.
- Ballistics: spread cone too wide (0.6°), no overpenetration through flesh.
- Friend build (one-click zipped Windows build): planned, on hold until multiplayer is finished. Notes in
  `Docs/ClaudeNotes/projectzed-friend-build.md`. `Assets/Editor/BuildScript.cs` is a SpaceShooter leftover; replace it.
  Build Settings still list only SampleScene; productName is still "My project".

## Gotchas (read before you "fix" these)

- **Editor crash in the SRP Batcher** (`GlobalKeywordState::operator!=`, Unity.dll offset 0x323d604) is a Unity
  engine bug. Workaround in place: Enter Play Mode Options are **off** (full domain + scene reload,
  `ProjectSettings/EditorSettings.asset`). Don't turn them back on. If it returns, upgrade the 6.6 patch release;
  disabling the SRP Batcher (`PC_RPAsset.asset`) is the last resort.
- **Burst AOT is disabled for Windows builds** (`ProjectSettings/BurstAotSettings_StandaloneWindows.json`) after a
  Burst AccessViolation crash in the player. Leave it off.
- **The PB sky package is embedded** at `Packages/com.jiaozi158.unity-physically-based-sky-urp` and patched to use
  `bakingOutput.lightmapBakeType` (the original called an editor-only API and broke player builds). The manifest
  still has the git URL, but the embedded copy wins. Don't delete it or swap back to the registry/git version.
- **VolumetricFog shader refs in `Assets/Settings/PC_Renderer.asset`** (`downsampleDepthShader`,
  `volumetricFogShader`) must stay assigned. They were null once and builds rendered a black screen.
- PB sky tints by the main light colour: keep the sun light white. Volumetric fog APV contribution stays off (it glowed at night).
- SSAO in `PC_Renderer.asset` causes dark grainy bands where arms meet the torso; not the model.
- Mixer `updateMode` must be UnscaledTime or the Paused snapshot never blends. Blend trees fire every child's
  animation events (FootstepAudio filters/debounces).
- Netcode scene management is OFF; each player keeps their own scene Player. The bald figure near spawn is
  `BaseMale_Mannequin`, not a network bug.
- Batch-mode Unity compiler sometimes crashes (CLR 0x80131506): just rerun. Unity wipes `Temp/` on exit.
- Blender can't open scripts from very long paths (MAX_PATH): keep scripts in `Blender/Scripts`.
- Imported Mixamo clips use the character's avatar (Copy From Other); "Create From This Model" tilts the feet.

## Driving Unity from Claude

- Editor open: use the `unity` CLI (`unity command eval_file --file x.cs`, `editor_play`/`editor_stop`,
  `capture_game_view`; capture paths must be inside the project).
- Editor closed (headless):
  `"C:/Program Files/Unity/Hub/Editor/6000.6.6f1/Editor/Unity.exe" -batchmode -quit -projectPath "<project>" -executeMethod Class.Method`
- Multiplayer test harness: `MultiplayerSetup.BuildTest -mpBuildOut <exe>`, then run the exe with
  `-mpHost -mpCodeFile f -mpTest` and a second with `-mpJoin CODE -mpTest`.
- Before big scene-wiring changes, tools save a copy to `SceneBackups/` (local only, not in git).
