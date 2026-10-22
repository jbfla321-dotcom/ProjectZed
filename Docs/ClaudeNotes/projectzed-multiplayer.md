---
name: projectzed-multiplayer
description: "ProjectZed co-op: NGO 2.13.3 + Multiplayer Services Relay, F8 menu, NetPuppet copies, build fixes, test harness"
metadata:
  node_type: memory
  type: project
  modified: 2026-10-22T22:55:31.201Z
---

Co-op added 2026-10-22 (Assets/Scripts/Multiplayer, Editor/Multiplayer/MultiplayerSetup.cs, Resources/Multiplayer prefabs from Tools > Multiplayer > Setup).
- NGO 2.13.3 (3.x needs Unity 6000.7) + com.unity.services.multiplayer 2.3.3; Unity Cloud project already linked. F8 menu: Host shows join code, Join by code (Relay, anonymous sign-in, random auth profile per run).
- Each player's scene Player stays local; others are NetPuppet = runtime-stripped clone of the Player (NetPuppet.Strip), driven by NetPlayer's owner-written NetworkVariable. Shots replicate as real bullets (HitscanGun.Shot/RemoteFire); victim's own game applies damage. Doors (Door.Swung/SwingTo, indexed by hierarchy path), time and weather follow the host. Netcode scene management is OFF.
- Not synced yet: cars, dropped items/crates, NPCs, glass broken by non-bullets.
- Build blockers fixed same day: embedded Packages/com.jiaozi158.unity-physically-based-sky-urp (bakingOutput.lightmapBakeType), PC_Renderer VolumetricFog shader refs were null (black screen in builds), Burst AOT disabled (ProjectSettings/BurstAotSettings_StandaloneWindows.json) after a Burst AccessViolation crash.
- Test: MultiplayerSetup.BuildTest -mpBuildOut <exe>, then run exe with -mpHost -mpCodeFile f -mpTest -mpShots dir, second with -mpJoin CODE -mpTest (NetTestDriver). Batchmode clients can't move (no Keyboard). The bald figure near spawn is the scene's BaseMale_Mannequin, not a bug.
