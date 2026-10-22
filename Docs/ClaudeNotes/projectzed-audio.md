---
name: projectzed-audio
description: "ProjectZed sound system (phases 1-2 of the sound plan, built 2026-10-21): pipeline, sources, rebuild order"
metadata:
  node_type: memory
  type: project
  modified: 2026-10-21T21:32:23.072Z
---

Sound plan artifact: https://claude.ai/artifact/K9nHQf7ehaqTePRkgme2BA. Phases 1 (foundation) and 2 (guns, impacts, footsteps) built 2026-10-21 for [[projectzed-shooter]].

- Sources: Sonniss GDC bundle files (royalty-free, no attribution), fetched from archive.org mirrors `sonniss-gdc-<year>-game-audio-bundle-normalized` (byte-identical to official zips) because downloads.sonniss.com sits behind a Cloudflare bot challenge and the Drive mirrors hit "quota exceeded". The bundles are only 2-4 sample files per vendor, so multi-hit recordings are sliced into one-shots. Raw files: `C:/Users/josht/SoundLibrary` (manifest.json, _index/ file lists).
- Pipeline: `My project/Audio/Scripts/fetch_sonniss.py` (PICKS table) → `slice.py` (SETS table → Assets/Audio/<Folder>/<Event>_NN.wav + CREDITS.md) → Unity Tools > Audio > Build All (`Assets/Editor/AudioTool.cs`: import settings via AudioImportRules, mixer via reflection on UnityEditor.Audio.AudioMixerController, SoundEvent assets from the Defs table, FootL/FootR/Land events on the controller's clips, scene wiring). All steps idempotent.
- Runtime: Assets/Scripts/Audio (SoundEvent, SoundManager pooled voices + distance delay/low-pass + snapshot switching, Surfaces/SurfaceTag/SurfaceSounds, GunAudio, FootstepAudio). HitscanGun got DryFired/MagazineEjected/ShotTraced/ShotHit events. SoundManager.logPlays logs every sound (handy for headless checks).
- Gotchas: mixers made from script have no view (write MixerGroupView yourself); mixer updateMode must be UnscaledTime or the Paused snapshot never blends; blend trees fire every child's events (PCA filters weight < 0.15, FootstepAudio debounces).
- Gun fix (2026-10-21, user heard a bang every 3rd-4th shot in full auto and off timing): Nagant takes had a slap echo ~0.41 s after the shot and takes differed 16 dB. Now shots are 0.3 s loudness-matched cores (slice.py `level`) + Rifle_FP_Tail/Rifle_Near_Tail played once at burst end (GunAudio), per-event maxVoices, DSP buffer 256 / 64 real voices (Tools > Audio > Apply Project Audio Settings), and HitscanGun keeps a steady fire beat (nextFireTime += interval). Check new gun recordings for secondary transients and level before using them.
- World seam (same day): WorldBuilder.AddSeamSkirt (Tools > Environment > Fix World Seam) shrinks the WorldTerrain hole by one 2 m cell and sinks the world 0.4 m under the field terrain so LOD cracks show ground; screenshots in Docs/Seam.
- Scene backup before wiring: SceneBackups/ProjectZed_before_Audio_20261021_1722.unity.

**Why:** the user wants DayZ-like sound; later phases (ambience, vehicles, characters, UI) build on this.
**How to apply:** add sounds by extending PICKS + SETS, rerun the two scripts, then Build All; new event types go in AudioTool.Defs.
