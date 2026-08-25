# Unity setup — Kimodo authoring with the bridge in WSL

The plugin project is copied to **`C:\Users\$user\KimodoUnity`** (Windows-side
copy of `kimodo-unity/`; Unity should not open projects across `\\wsl$`).

The bridge server runs in WSL from our venv — Unity just connects to it:

```bash
# in WSL, if not already running (check: curl -s localhost:8765/health)
cd ~/vr-human-motion/kimodo-unity/Packages/com.aminhp.kimodobridge/Server~
TEXT_ENCODER_DEVICE=cpu PYTHONPATH=. ~/vr-human-motion/.venv/bin/python \
  -m kimodo_bridge --host 0.0.0.0 --port 8765
```

## Steps in Unity (on Windows)

1. **Unity Hub → Open → `C:\Users\$user\KimodoUnity`.**
   The project was made with Unity **6000.4.4f1**; if your installed 6000.x
   differs, Hub offers to open/upgrade with your version — accept.
2. Open `Assets/Scenes/SampleScene.unity`.
3. Find the **KimodoBridge** manager object (or create it via the plugin's
   menu). In its inspector set **Server URL = `http://127.0.0.1:8765`** and
   press **Connect** — it detects the already-running WSL server and adopts
   it (no Python path needed on Windows; WSL ports are reachable from
   Windows localhost).
4. Set up a character: any **Humanoid-rigged** model works (the plugin
   retargets via Mecanim muscle space). Quick sources: Unity Asset Store
   "Starter Assets" characters, Mixamo FBX (import → Rig → Humanoid), or any
   glTF/FBX humanoid. Use the plugin's character-setup menu on the model.
5. Type a prompt, **Generate** — first use of a new prompt pays the ~2 min
   text-encoder load in WSL (watch `data/bridge.log`); repeats are seconds.
   Preview, scrub, add waypoint/end-effector constraints with scene gizmos.
6. **Bake** to an AnimationClip — the `.anim` plays with no server running.

## Notes

- Only run one heavy generation at a time — the browser server (:8123) and
  the bridge (:8765) share the GPU. Idle servers are fine together.
- The bridge caches its own embeddings in-process (their code); our
  disk cache in `data/embedding_cache/` only serves our server on :8123.
- Robots: install the URDF Importer (`https://github.com/Unity-Technologies/URDF-Importer`
  via Package Manager → Add package from git URL) and import a robot URDF
  into the same scene the human performs in. Kimodo's G1 checkpoint can
  drive a Unitree G1 robot model — the bridge exposes it as model
  `kimodo-g1-rp`.
- VR later: install the VIVE OpenXR plugin (XR Elite) and this same scene
  becomes the headset app; start with PCVR streaming.
