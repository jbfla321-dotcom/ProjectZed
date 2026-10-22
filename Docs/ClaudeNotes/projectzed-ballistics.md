---
name: projectzed-ballistics
description: "ProjectZed gun ballistics — BulletWorld projectile sim (Arma 3/ACE3 style) behind HitscanGun; files, tuning, open issues"
metadata:
  node_type: memory
  type: project
  modified: 2026-10-22T00:21:27.658Z
---

Since 2026-10-21 HitscanGun (name kept for scene refs) fires real bullets: Assets/Scripts/Ballistics/Ballistics.cs (G7 drag table, ISA air density, zeroing, spin drift, per-SurfaceType penetration/ricochet) + BulletWorld.cs (auto-created singleton, steps bullets ≤8 ms with a raycast per step, wind from WindManager.GetWindAt). Default ammo = ACE3 5.56 (G7 0.151, barrel/velocity table), 368 mm barrel → 865 m/s.
- Zero: Page Up/Down (100–800 m), shown on HUD. Damage scales by impact speed / muzzle speed.
- GunAudio crack now uses gun.BulletMoved (once per bullet, supersonic only); ShotTraced event was removed.
- Tools > Ballistics > Print Range Card (Assets/Editor/BallisticsTool.cs) — verified 500 m drop −233 cm, 4 m/s wind 100 cm.
- Open: spread 0.6° cone is far wider than rifle dispersion; bullets stop at flesh (no overpenetration); a hollow object's thickness is measured end to end.
Related: [[projectzed-audio]], [[projectzed-shooter]]
