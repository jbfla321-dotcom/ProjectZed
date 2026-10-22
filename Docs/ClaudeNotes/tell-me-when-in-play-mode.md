---
name: tell-me-when-in-play-mode
description: "If the Unity editor is in Play mode when I need edit mode, tell the user before continuing"
metadata:
  node_type: memory
  type: feedback
  modified: 2026-10-20T23:35:57.203Z
---

Before editor work that needs edit mode, check `EditorApplication.isPlaying`. If the user is in Play mode, tell them ("you're in Play mode, stop it when you're ready") before continuing, instead of silently waiting or stopping it myself.

**Why:** the user forgets they left Play mode on (2026-10-20: a swap ran in Play mode and would have been lost).
**How to apply:** check at the start of each batch of `unity command` editor changes; my own editor_play/editor_stop test runs are fine.
