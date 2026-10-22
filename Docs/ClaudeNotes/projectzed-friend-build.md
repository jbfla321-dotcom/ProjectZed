---
name: projectzed-friend-build
description: Planned one-click zipped Windows build for sending ProjectZed to a friend; on hold until multiplayer is done
metadata:
  node_type: memory
  type: project
  modified: 2026-10-22T22:56:03.038Z
---

On hold (2026-10-22): user wants a one-click Windows build to zip and send a friend, but only after multiplayer is finished.

Findings from planning (verify they're still true before starting):
- EditorBuildSettings lists only Assets/Scenes/SampleScene.unity, not Assets/ProjectZed.unity.
- Assets/Editor/BuildScript.cs is leftover from SpaceShooter3D and creates a new scene. Replace it, don't reuse it.
- F7 WeatherMenu (WeatherMenu.cs ~line 24) and DevConsole (~line 16) are gated to debug builds/Editor. The user still has to choose: A) enable them in the friend build (recommended), B) ship a Development build, or C) leave them out.
- productName is "My project"; it should become ProjectZed.
- Player builds used to fail because the PBR sky package called the editor-only Light.lightmapBakeType. The co-op thread fixed it (package embedded at Packages/com.jiaozi158.unity-physically-based-sky-urp, now uses bakingOutput.lightmapBakeType). Don't revert it to the registry version.
- Co-op multiplayer was done by 2026-10-22: F8 menu, the host shows a 6-letter code and the friend joins with it. Build fixes from that work: VolumetricFog shaders assigned in PC_Renderer.asset (this fixed a black screen in builds), and Burst turned off for builds after it crashed. Test the zip with a host and a join. Burst AOT is turned off in ProjectSettings/BurstAotSettings_StandaloneWindows.json (it crashed with an AccessViolation). MultiplayerSetup.BuildTest already builds ProjectZed cleanly, so it may be a good starting point to reuse.

Plan: Tools > Build menu item, build to Builds/ProjectZed, strip _BurstDebugInformation_DoNotShip, add a HOW TO PLAY.txt, zip, open the folder. Test the player build at runtime (weather, F7, guns, audio, houses, glass, Shader.Find shaders). Zip will be several GB.
Related: [[projectzed-weather]], [[plan-before-work]]
