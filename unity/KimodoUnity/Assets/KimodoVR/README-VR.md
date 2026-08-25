# KimodoVR — client scripts

Demonstration-driven motion generation client. Full documentation, setup and
the controls table are in the repo README (`~/vr-human-motion/README.md`);
the script header of `KimodoVR.cs` carries the same controls summary.

- `KimodoVR.cs` — capture (touch/walk/pose), HUD + VR panel/keyboard, obstacle
  aware route planning, request building, person direction (re-prompt, cut,
  follow-up chain, move/rotate/delete), config save (J), spectator camera (F3).
- `KimodoCharacterPlayer.cs` — skinned Humanoid playback via the bridge
  retargeter, contact IK on the target rig, room/furniture containment, cut window.
- `KimodoSkeletonPlayer.cs` — stick-figure fallback with the same direction API.
- `RobotPoser.cs` — pose URDF-imported robots in Edit mode without physics.

Quick test without a headset: Play → left-click a table → **G**. With a
headset: R-trigger tap a table (laser), **A**.

Server: `TEXT_ENCODER_DEVICE=cpu .venv/bin/python generation/server.py` in WSL,
health at `http://localhost:8123/api/health`.
