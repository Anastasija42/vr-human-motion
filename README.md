# VR Human Motion — demonstration-driven motion generation

Generate plausible, controllable full-body human motion for a virtual scene
(a lab with tables and Franka Panda robots) by **demonstrating** the spatial
part of a task in VR (walk a path, tap where the hand should touch, strike a
pose) and describing the rest with a text prompt. The demonstration becomes a
set of kinematic constraints for **NVIDIA Kimodo** (motion diffusion); the
generated clip is retargeted onto a skinned humanoid and played back in the
same scene, where it can be directed further (re-prompted, cut, chained,
moved).

## Report and demo video

- **Report** : [`docs/izvestaj.pdf`](docs/izvestaj.pdf)
- **Demo video** — *Interaktivno generisanje ljudskog pokreta u virtuelnoj stvarnosti*:
  [`docs/demo.mp4`](docs/demo.mp4)

## Repository layout

| path | what |
|---|---|
| `generation/server.py` | FastAPI backend: request → Kimodo constraints → motion (+ `viewer.html`, a browser preview at `/`) |
| `kimodo-src/` | upstream [nv-tlabs/kimodo](https://github.com/nv-tlabs/kimodo) checkout (installed into the venv with `pip install -e`) |
| `kimodo-unity/` | Unity bridge package sample (`com.aminhp.kimodobridge`) used by the client for retargeting |
| `data/embedding_cache/` | cached prompt embeddings — cached prompts generate instantly; `index.json` lists them |
| `data/sessions/<id>/` | every generation: `request.json`, `constraints.json`, `motion.npz` |
| `docs/izvestaj.pdf`, `docs/demo.mp4` | course report (Serbian) and the demo video (LFS) |
| `docs/unity-setup.md` | notes on the bridge package / earlier setup |
| **Unity project** | lives outside the repo at `C:\Users\$user\KimodoUnity` |

The client-side logic is four scripts in `Assets/KimodoVR/`:
`KimodoVR.cs` (capture, HUD/VR UI, planning, request building, direction),
`KimodoCharacterPlayer.cs` (skinned playback, contact IK, containment, cutting),
`KimodoSkeletonPlayer.cs` (stick-figure fallback), `RobotPoser.cs` (pose URDF robots in the editor).

## Requirements

- Windows 11 with **WSL2** (Ubuntu) and an NVIDIA GPU with ≥ 12 GB VRAM
  (developed on an RTX 5070 Ti laptop).
- **Unity 6000.5.x** (URP) with packages: OpenXR 1.17, Input System 1.20,
  URDF Importer 0.5.2, Recorder 5.1 — restored automatically from `Packages/manifest.json`.
- A PC-VR headset via OpenXR (tested with Quest-style Touch controllers). The
  whole pipeline also works **without a headset** (desktop + simulated rig).
- Python 3.10 in WSL; Hugging Face access to `meta-llama/Meta-Llama-3-8B-Instruct`
  (the LLM2Vec text encoder is gated — `huggingface-cli login` once).

## Backend setup (WSL)

```bash
cd ~/vr-human-motion
python3.10 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt          # pick the torch CUDA build for your driver
git clone https://github.com/nv-tlabs/kimodo.git kimodo-src   # if not present
pip install -e kimodo-src
huggingface-cli login                    # Llama-3 access for the text encoder

# run (from the repo root). The Kimodo weights (~1 GB) download on first use;
# the Llama encoder (~15 GB) downloads the first time a NEW prompt is encoded.
TEXT_ENCODER_DEVICE=cpu .venv/bin/python generation/server.py
curl -s localhost:8123/api/health        # {"model":"Kimodo-SOMA-RP-v1.1","loaded":false,...}
```

`TEXT_ENCODER_DEVICE=cpu` keeps the 16 GB encoder off the GPU: a new prompt
costs ~2 min once (CPU), then its embedding is cached and reused instantly.
Generation of a few-second clip takes ~10–15 s on the RTX 5070 Ti.

If the server later answers `500 CUDA error: unknown error`, the CUDA context
went stale (typical after the laptop slept) — just restart the process.

## Unity setup (Windows)

1. Open the project in Unity Hub (`C:\Users\$user\KimodoUnity`).
2. Open your scene (e.g. `Assets/lab.unity`). On the **KimodoVR** component:
   - **Character Prefab** — a Humanoid-rigged FBX (Mixamo works; Rig → Humanoid).
   - **Scene Surfaces** — table tops, shelves, or invisible *areas* (an empty
     with a BoxCollider, e.g. one drawer shelf). Green frames + Console
     heights confirm them at Play.
   - **Scene Obstacles** — furniture to route around; **Room Bounds** — a
     collider enclosing the floor (performers are hard-clamped inside);
     **Entrance Anchor** — where auto-planned walks start.
   - Furniture must be real-world scale: the generated human is always 1.75 m.
3. VR: Project Settings → XR Plug-in Management → OpenXR → enable your
   controller's **Interaction Profile** (Oculus Touch / Quest Pro / Index…).
   Without it only triggers arrive and A/B/X map to nothing or to MENU.
4. Press Play. The server URL defaults to `http://localhost:8123` (WSL ports are
   reachable from Windows localhost).

## Using it

**Flow:** show it → pick a prompt → generate → direct the person.

| VR | |
|---|---|
| R-trigger **tap** | laser-place a point: on a surface = orange touch point, on the floor = blue walk dot |
| R-trigger **hold** | trace along a surface with the hand |
| L-trigger hold | record a walking path |
| **X** tap / hold | snap a pose (stick-figure ghost) / open the panel (prompts + controls) |
| **A** | generate — with a person selected: queue a follow-up action |
| **B** tap / hold | next prompt, or select the person the laser points at / start typing (virtual keyboard) |
| sticks (selected) | move (L) · rotate (R left/right) · raise/lower (R up/down) |
| laser on the menu | cut IN / cut OUT at the playhead · full clip · prompts · type · delete |
| R-grip | clear everything |

| Desktop | |
|---|---|
| left-click surface / right-click floor | touch point / walk dot |
| **X** / **H** | snap pose / simulated rig (WASD, QE, RMB look, Ctrl/Shift hands) |
| **G** / **J** | generate / save config to `KimodoConfigs/` and generate from it |
| **P** / **T** | cycle / type a prompt (typing only sets it — G/A generates) |
| click a person | 1–9 or T new behavior · N follow-up · `[` `]` F cut · arrows , . R move/rotate · PgUp/PgDn up/down · Del |
| Tab / F1 / F2 / F3 | panels · tutorial · input readout · spectator camera for recording |

Keep prompts full sentences ("A person crouches and reaches forward.") — terse
words ("Jump.") are out of the model's distribution and act sluggish.
