---
name: prefer-existing-assets
description: "Use the user's stored animation/art assets (blend, pick, combine) before generating or editing new animations"
metadata:
  node_type: memory
  type: feedback
  modified: 2026-10-20T03:11:42.713Z
---

When an animation or asset problem comes up, first solve it with the assets already in the project (pick better clips, blend or layer them, mix packs) before generating new animations or modifying clip curves.

**Why:** On 2026-10-20 I "fixed" a hunched rifle run by generating edited clip copies (spine bend, then pelvis tilt). That caused an arched-back, butt-out posture. The user pointed out the stored packs already had upright clips to blend, and said: "Lets try to use whatever assets we have available before trying to make new animations from now on".
**How to apply:** Survey the imported clips (e.g. measure posture/speed across packs) and wire the best stored ones into the controller; only generate or edit clips when nothing stored fits, and say so first.
