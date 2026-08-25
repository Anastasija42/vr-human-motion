"""Kimodo generation server — the backend the Unity VR/desktop client talks to.

The client turns a demonstration (walk dots, touch points, poses) into this
request; the server builds Kimodo constraints, samples the motion, runs the
postprocess (foot-skate cleanup + constraint snapping) and returns the clip.

POST /api/generate
    {
      "prompt":      "a person walks to the table and waves",
      "duration":    6.0,                 # seconds (0.5 .. 30)
      "steps":       40,                  # denoising steps
      "flat":        true,                # Unity-friendly flat arrays in the reply
      "waypoints":   [{"x","z","t"}, ...],          # root2d path, meters, Kimodo coords
      "hand_keys":   [{"x","y","z","t"}, ...],      # right-hand trace targets
      "hand_anchor": "fingertip" | "wrist",         # clicks vs. VR controller traces
      "body_keys":   [{"x","z"}, ...],              # where to stand while touching
      "pose_keys":   [{"t","bx","bz","by","h","lx","ly","lz","rx","ry","rz"}, ...]
                     # snapped VR poses: stance, head height, yaw, both wrists
    }
->  {
      "fps", "joint_names", "parents", "bone_offsets_flat",
      "quats_flat" (T*J*4, wxyz), "root_flat" (T*3), "frames_flat" (T*J*3),
      "constraints_used", "hand_constraint", "pose_constraint",
      "session_dir": "data/sessions/<id>", "gen_seconds"
    }
Coordinates: Kimodo is right-handed Y-up; the Unity client negates X at the
boundary. All positions are world-space meters; the server canonicalizes the
path to start at the origin and de-canonicalizes the output.

GET /api/health, /api/prompts (cached, instant prompts), /api/progress.

Run:  .venv/bin/python generation/server.py   (from the repo root)
Env:  KIMODO_MODEL (default Kimodo-SOMA-RP-v1.1), TEXT_ENCODER_DEVICE=cpu to
      keep the 16 GB text encoder off the GPU, PORT (default 8123)
"""

import json
import math
import os
import threading
import time
from datetime import datetime
from pathlib import Path

import numpy as np
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field

ROOT = Path(__file__).resolve().parent.parent
SESSIONS_DIR = ROOT / "data" / "sessions"
MODEL_NAME = os.environ.get("KIMODO_MODEL", "Kimodo-SOMA-RP-v1.1")

app = FastAPI(title="VR Human Motion — Kimodo server")
app.add_middleware(
    CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"]
)

_model = None
_model_err = None
_lock = threading.Lock()  # one generation at a time (single GPU)

_progress = {"phase": "idle", "pct": None, "detail": "", "segment": 0, "updated": 0.0}


def _set_progress(phase, pct=None, detail=""):
    _progress.update(phase=phase, pct=pct, detail=detail, updated=time.time())


def _progress_iter(iterable, **_ignored):
    """tqdm stand-in passed to model(...): reports denoising progress. Called
    once per prompt segment."""
    items = list(iterable)
    total = len(items) or 1
    _progress["segment"] += 1
    seg = _progress["segment"]
    for i, item in enumerate(items):
        _set_progress("sampling", pct=round((i + 1) / total * 100),
                      detail=f"segment {seg}: denoising step {i + 1}/{total}")
        yield item


class LazyCachingEncoder:
    """Drop-in for LLM2VecEncoder that keeps the ~16 GB Llama encoder OUT of
    resident memory: embeddings are cached on disk per prompt text, and the real
    encoder is only constructed for a cache miss, then freed immediately.
    Constructed by kimodo's load_model via the patch in get_model()."""

    EMBED_CACHE = ROOT / "data" / "embedding_cache"

    def __init__(self, **encoder_kwargs):
        self.encoder_kwargs = encoder_kwargs
        self.llm_dim = encoder_kwargs.get("llm_dim", 4096)
        self.device = "cpu"
        self.EMBED_CACHE.mkdir(parents=True, exist_ok=True)

    def get_device(self):
        return self.device

    def to(self, *_, **__):
        return self

    def eval(self):
        return self

    def _path(self, text: str) -> Path:
        import hashlib

        return self.EMBED_CACHE / (hashlib.sha256(text.encode()).hexdigest()[:32] + ".npy")

    def _index(self, text: str) -> None:
        """Record the human-readable prompt behind each cached embedding, so the
        viewer can offer already-encoded (instant) prompts."""
        idx_path = self.EMBED_CACHE / "index.json"
        try:
            idx = json.loads(idx_path.read_text())
        except (OSError, json.JSONDecodeError):
            idx = {}
        idx[self._path(text).name] = text
        idx_path.write_text(json.dumps(idx, indent=1))

    def __call__(self, texts):
        import gc

        import torch

        if isinstance(texts, str):
            texts = [texts]
        missing = [
            t for t in texts if t.strip() and not self._path(t).exists()
        ]
        if missing:
            from kimodo.model.llm2vec import LLM2VecEncoder  # the real class

            print(f"Encoding {len(missing)} new prompt(s) — loading Llama once...")
            _set_progress("encoding", detail=f"new prompt — loading the 16 GB text encoder "
                          f"(~2 min, once per new prompt): {missing[0][:60]!r}")
            t0 = time.time()
            real = LLM2VecEncoder(**self.encoder_kwargs)
            feats, _ = real(missing)  # [B, 1, llm_dim]
            for text, vec in zip(missing, feats):
                np.save(self._path(text), np.asarray(vec.float().cpu())[0])
                self._index(text)
            del real
            gc.collect()
            torch.cuda.empty_cache()
            print(f"Encoded and released Llama in {time.time() - t0:.1f}s")

        vecs = [
            np.zeros(self.llm_dim, dtype=np.float32)
            if not t.strip()
            else np.load(self._path(t))
            for t in texts
        ]
        feat = torch.tensor(np.stack(vecs), dtype=torch.float32)[:, None]  # [B, 1, D]
        return feat, [1] * len(texts)


class Waypoint(BaseModel):
    x: float
    z: float
    t: float | None = None  # seconds; optional


class HandKey(BaseModel):
    """World-space target for the RIGHT hand (wrist) — a VR hand/controller
    sample, or a click on the table in the viewer."""

    x: float
    y: float
    z: float
    t: float | None = None  # seconds; optional


class PoseKey(BaseModel):
    """One snapped VR pose: everything a 3-tracker rig knows about the
    demonstrator at instant t — both wrist positions, the headset's floor
    position (bx, bz) and yaw h (radians, 0 = +Z). Already in Kimodo coords."""

    t: float
    bx: float
    bz: float
    by: float = 0.0  # headset height; 0 = unknown (assume standing)
    h: float = 0.0
    lx: float
    ly: float
    lz: float
    rx: float
    ry: float
    rz: float


class GenerateRequest(BaseModel):
    prompt: str
    duration: float = Field(default=6.0, gt=0.5, le=30.0)
    waypoints: list[Waypoint] = []
    hand_keys: list[HandKey] = []
    # "fingertip" = keys are where the finger touches (table clicks);
    # "wrist" = keys are wrist/controller poses (VR capture).
    hand_anchor: str = Field(default="fingertip", pattern="^(fingertip|wrist)$")
    # Optional explicit body (root) ground positions during the hand keys —
    # lets a scene-aware client keep the body outside furniture. One entry
    # per hand key, or a single entry used for all.
    body_keys: list[Waypoint] = []
    # Snapped VR poses -> both-hands keyframe constraints (see PoseKey).
    pose_keys: list[PoseKey] = []
    steps: int = Field(default=50, ge=10, le=200)
    seed: int | None = None
    # Unity's JsonUtility can't parse nested arrays — flat=true returns
    # frames_flat (T*J*3 floats) + frame_count/joint_count instead of frames.
    flat: bool = False


def get_model():
    global _model, _model_err
    with _lock:
        if _model is None and _model_err is None:
            try:
                import torch
                from kimodo import load_model
                import kimodo.model as _km

                # swap the lazy caching proxy in for the 16 GB LLM2Vec encoder
                _km.LLM2VecEncoder = LazyCachingEncoder

                device = "cuda:0" if torch.cuda.is_available() else "cpu"
                print(f"Loading {MODEL_NAME} on {device} (first request only)...")
                _set_progress("loading model", detail=f"{MODEL_NAME} on {device}")
                t0 = time.time()
                _model = load_model(MODEL_NAME, device=device, default_family="Kimodo")
                print(f"Model loaded in {time.time() - t0:.1f}s")
            except Exception as e:  # remember the failure instead of retrying forever
                _model_err = e
                raise
        if _model_err is not None:
            raise _model_err
        return _model


def waypoints_to_constraints(waypoints: list[Waypoint], num_frames: int, fps: float):
    """VR world-space waypoints -> Kimodo root2d constraint (canonical space).

    Kimodo expects the path to start at (0,0) at frame 0, so we subtract the first
    waypoint (returned as `offset` for de-canonicalizing the output), and compute
    the initial heading from the first path segment (0 rad = facing +Z).
    """
    if not waypoints:
        return [], (0.0, 0.0), None

    ox, oz = waypoints[0].x, waypoints[0].z
    pts = [(w.x - ox, w.z - oz) for w in waypoints]

    # frame per waypoint: explicit timestamps, else paced at walking speed so
    # the path only claims the frames it needs
    WALK_SPEED = 1.3  # m/s
    if all(w.t is not None for w in waypoints) and len(waypoints) > 1:
        frames = [min(num_frames - 1, max(0, round(w.t * fps))) for w in waypoints]
    else:
        cum = [0.0]
        for (x0, z0), (x1, z1) in zip(pts, pts[1:]):
            cum.append(cum[-1] + math.hypot(x1 - x0, z1 - z0))
        total = cum[-1] or 1.0
        path_frames = min(num_frames - 1, max(len(pts) - 1, round(total / WALK_SPEED * fps)))
        frames = [round(c / total * path_frames) for c in cum]

    # Deduplicate frames (keep last waypoint for a frame), keep strictly increasing.
    by_frame = {}
    for f, p in zip(frames, pts):
        by_frame[f] = p
    frames = sorted(by_frame)
    pts = [by_frame[f] for f in frames]

    heading = None
    if len(pts) > 1:
        dx, dz = pts[1][0] - pts[0][0], pts[1][1] - pts[0][1]
        if abs(dx) + abs(dz) > 1e-6:
            heading = math.atan2(dx, dz)  # 0 = +Z

    constraints = [
        {"type": "root2d", "frame_indices": frames, "smooth_root_2d": pts}
    ]
    return constraints, (ox, oz), heading


def _swing(u, v, dev):
    """Rotation matrix taking direction u to direction v (Rodrigues)."""
    import torch

    u = u / (u.norm() + 1e-9)
    v = v / (v.norm() + 1e-9)
    axis = torch.linalg.cross(u, v)
    s, c = axis.norm(), torch.dot(u, v)
    if s < 1e-6:
        return torch.eye(3, device=dev) if c > 0 else -torch.eye(3, device=dev)
    axis = axis / s
    K = torch.tensor(
        [[0, -axis[2], axis[1]], [axis[2], 0, -axis[0]], [-axis[1], axis[0], 0]],
        device=dev,
    )
    return torch.eye(3, device=dev) + K * s + K @ K * (1 - c)


def build_hand_constraint(
    skel, keys: list[HandKey], num_frames: int, fps: float, offset_xz,
    anchor: str = "fingertip", body_keys: list = (),
):
    """VR/table right-hand targets -> RightHandConstraintSet.

    Kimodo's hand constraint wants a full-body pose per key (it pins only the
    hand cluster + root placement out of it). We synthesize one: the skeleton's
    neutral pose stood next to the trace, facing it, with the hand group
    rigidly translated so the wrist lands on each target point.
    """
    import torch

    from kimodo.constraints import RightHandConstraintSet, create_pairs

    class _PosOnlyRightHand(RightHandConstraintSet):
        """Right-hand constraint with the wrist ROTATION left free: VR capture
        gives positions, and our synthetic pose's T-pose wrist orientation
        would fight the reach if pinned."""

        def update_constraints(self, data_dict, index_dict):
            crop = torch.arange(len(self.frame_indices), device=self.frame_indices.device)
            data_dict["global_joints_positions"].append(
                self.global_joints_positions[tuple(create_pairs(crop, self.pos_indices).T)]
            )
            index_dict["global_joints_positions"].append(
                create_pairs(self.frame_indices, self.pos_indices)
            )
            data_dict["smooth_root_2d"].append(self.smooth_root_2d)
            index_dict["smooth_root_2d"].append(self.frame_indices)
            data_dict["root_y_pos"].append(self.root_y_pos)
            index_dict["root_y_pos"].append(self.frame_indices)
            data_dict["global_root_heading"].append(self.global_root_heading)
            index_dict["global_root_heading"].append(self.frame_indices)

    ox, oz = offset_xz
    if all(k.t is not None for k in keys) and len(keys) > 1:
        frames = [min(num_frames - 1, max(0, round(k.t * fps))) for k in keys]
    else:
        # no timestamps: trace through the middle of the clip (walk-up before,
        # settle after)
        f0, f1 = round(0.3 * num_frames), round(0.9 * num_frames)
        n = len(keys)
        frames = [round(f0 + (f1 - f0) * i / max(1, n - 1)) for i in range(n)]
    by_frame = {}
    for f, k in zip(frames, keys):
        by_frame[f] = k
    frames = sorted(by_frame)
    keys = [by_frame[f] for f in frames]

    dev = skel.neutral_joints.device
    pts = torch.tensor(
        [[k.x - ox, k.y, k.z - oz] for k in keys], dtype=torch.float32, device=dev
    )  # [T,3]

    # body placement: explicit body_keys, else a 0.28 m standoff following the trace
    STANDOFF = 0.28
    span = pts[-1, [0, 2]] - pts[0, [0, 2]]
    if len(pts) > 1 and float(span.norm()) > 1e-3:
        d = span / span.norm()
        facing = torch.tensor([d[1], -d[0]], device=dev)  # perpendicular to the line
    else:
        facing = torch.tensor([0.0, 1.0], device=dev)  # face +Z
    if body_keys:
        bk = list(body_keys)
        if len(bk) == 1:
            bk = bk * len(keys)
        body_xz_per_key = torch.tensor(
            [[b.x - ox, b.z - oz] for b in bk[: len(keys)]],
            dtype=torch.float32, device=dev,
        )
        mid = len(keys) // 2
        toward = pts[mid, [0, 2]] - body_xz_per_key[mid]
        heading = math.atan2(float(toward[0]), float(toward[1]))
    else:
        body_xz_per_key = pts[:, [0, 2]] - facing * STANDOFF  # [T,2]
        heading = math.atan2(float(facing[0]), float(facing[1]))  # 0 = +Z

    # densify: a key every ~4 frames so the hand can't sag between clicks
    if len(frames) > 1:
        df, dp, db = [], [], []
        for i in range(len(frames) - 1):
            f0, f1 = frames[i], frames[i + 1]
            steps = max(1, (f1 - f0) // 4)
            for s in range(steps):
                a = s / steps
                df.append(round(f0 + (f1 - f0) * a))
                dp.append(pts[i] * (1 - a) + pts[i + 1] * a)
                db.append(body_xz_per_key[i] * (1 - a) + body_xz_per_key[i + 1] * a)
        df.append(frames[-1])
        dp.append(pts[-1])
        db.append(body_xz_per_key[-1])
        frames = df
        pts = torch.stack(dp)
        body_xz_per_key = torch.stack(db)

    cy, sy = math.cos(heading), math.sin(heading)
    R = torch.tensor(
        [[cy, 0.0, sy], [0.0, 1.0, 0.0], [-sy, 0.0, cy]], dtype=torch.float32, device=dev
    )
    # hips height per key: standing for targets above ~0.65 m, lower for low
    # targets — a pinned standing pelvis would forbid the crouch the reach needs
    def _root_h(ty):
        return 0.97 if ty >= 0.65 else max(0.45, 0.97 - (0.65 - ty) * 1.1)

    pose = skel.neutral_joints.float() @ R.T  # neutral pose (hips at origin) rotated
    # global rotations: heading-aligned neutral stand-in (skel30 ships none)
    grots = R.unsqueeze(0).expand(skel.nbjoints, 3, 3).contiguous()  # [J,3,3]

    T = len(frames)
    poses = pose.unsqueeze(0).repeat(T, 1, 1)
    for i in range(T):
        poses[i] += torch.tensor(
            [body_xz_per_key[i, 0], _root_h(float(pts[i, 1])), body_xz_per_key[i, 1]], device=dev
        )
    grots_t = grots.unsqueeze(0).repeat(T, 1, 1, 1)

    # fingertip anchor: the reduced skeleton tracks wrist + thumb tip + middle tip
    hand_ids = torch.tensor(
        [skel.bone_index[n] for n in skel.bone_order_names if n.startswith("RightHand")],
        device=dev,
    )
    n = skel.bone_index
    arm, forearm, wrist_j, thumb, tip = (
        n["RightArm"], n["RightForeArm"], n["RightHand"],
        n["RightHandThumbEnd"], n["RightHandMiddleEnd"],
    )
    neutral = skel.neutral_joints.float()
    o_fa = R @ (neutral[forearm] - neutral[arm])     # world-frame neutral offsets
    o_ha = R @ (neutral[wrist_j] - neutral[forearm])
    o_tip = R @ (neutral[tip] - neutral[wrist_j])
    o_thumb = R @ (neutral[thumb] - neutral[wrist_j])
    a_len, b_len, h_len = float(o_fa.norm()), float(o_ha.norm()), float(o_tip.norm())
    down = torch.tensor([0.0, -1.0, 0.0], device=dev)

    for i in range(T):
        S = poses[i, arm]
        K = pts[i]
        to_k = K - S
        # wrist target: fingertip anchor puts the wrist one hand-length short
        W = K - to_k / (to_k.norm() + 1e-9) * h_len if anchor == "fingertip" else K
        d = torch.clamp((W - S).norm(), abs(a_len - b_len) + 0.01, a_len + b_len - 0.01)
        axis = (W - S) / ((W - S).norm() + 1e-9)
        W = S + axis * d  # clamp unreachable targets onto the reachable sphere
        pole = down - torch.dot(down, axis) * axis
        if pole.norm() < 1e-4:
            pole = torch.tensor([0.0, 0.0, -1.0], device=dev)
        pole = pole / pole.norm()
        cos_a = (a_len**2 + d**2 - b_len**2) / (2 * a_len * d)
        cos_a = torch.clamp(torch.tensor(float(cos_a)), -1.0, 1.0)
        sin_a = (1 - cos_a**2).sqrt()
        E = S + axis * (a_len * cos_a) + pole * (a_len * sin_a)

        A1 = _swing(o_fa, E - S, dev) @ R     # upper arm
        A2 = _swing(o_ha, W - E, dev) @ R     # forearm
        # hand follows the forearm frame (wrist in line); a fingertip anchor
        # swings it just enough to put the tip on the target
        o_tipA = A2 @ R.T @ o_tip             # neutral tip offset, forearm frame
        if anchor == "fingertip":
            A3 = _swing(o_tipA, K - W, dev) @ A2
        else:
            A3 = A2

        poses[i, forearm] = E
        poses[i, wrist_j] = W
        poses[i, tip] = K if anchor == "fingertip" else W + o_tipA
        poses[i, thumb] = W + A3 @ R.T @ o_thumb
        grots_t[i, arm] = A1
        grots_t[i, forearm] = A2
        grots_t[i, wrist_j] = A3
        grots_t[i, thumb] = A3
        grots_t[i, tip] = A3

    # frame_indices stay on CPU (matches the JSON-loading path)
    cons = _PosOnlyRightHand(skel, torch.tensor(frames), poses, grots_t, None)
    info = {
        "frame_indices": frames,
        "targets": [[round(float(v), 3) for v in p] for p in pts],
        "body_xz": [[round(float(v), 3) for v in b] for b in body_xz_per_key],
        "heading_rad": round(heading, 3),
    }
    return cons, info


def build_pose_constraints(skel, pose_keys, num_frames, fps, offset_xz):
    """Snapped VR poses -> a left-hand + a right-hand keyframe constraint set.

    Each PoseKey is everything a 3-tracker rig knows about the demonstrator at
    instant t: both wrist positions, stance (bx, bz), headset height (by) and
    yaw (h). We synthesize a full-body pose per key — the neutral stance
    rotated to the yaw, placed at the stance (root height estimated from the
    headset so crouches survive), each arm two-bone-IK'd so its wrist lands on
    its captured controller position — then pin ONLY the two wrist positions
    plus root xz/y/heading out of it. Hand orientation, elbows, legs stay the
    model's to perform; between the keyframes it is fully unconstrained."""
    import torch

    from kimodo.constraints import LeftHandConstraintSet, RightHandConstraintSet, create_pairs

    class _PosOnly:
        """Pin the wrist POSITION, root placement, and facing — rotations stay
        free (VR gives positions; our synthetic orientation is a T-pose
        stand-in that must not be enforced)."""

        wrist_joint = ""

        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            self.pos_indices = torch.tensor([self.skeleton.bone_index[self.wrist_joint]])

        def update_constraints(self, data_dict, index_dict):
            crop = torch.arange(len(self.frame_indices), device=self.frame_indices.device)
            data_dict["global_joints_positions"].append(
                self.global_joints_positions[tuple(create_pairs(crop, self.pos_indices).T)]
            )
            index_dict["global_joints_positions"].append(
                create_pairs(self.frame_indices, self.pos_indices)
            )
            data_dict["smooth_root_2d"].append(self.smooth_root_2d)
            index_dict["smooth_root_2d"].append(self.frame_indices)
            if getattr(self, "pin_root_y", True):
                data_dict["root_y_pos"].append(self.root_y_pos)
                index_dict["root_y_pos"].append(self.frame_indices)
            data_dict["global_root_heading"].append(self.global_root_heading)
            index_dict["global_root_heading"].append(self.frame_indices)

    # the postprocess snap dispatches on set NAMES — one properly named set per hand
    class _PoseLeft(_PosOnly, LeftHandConstraintSet):
        wrist_joint = "LeftHand"

    class _PoseRight(_PosOnly, RightHandConstraintSet):
        wrist_joint = "RightHand"

    ox, oz = offset_xz
    keys = sorted(pose_keys, key=lambda k: k.t)
    frames = [min(num_frames - 1, max(0, round(k.t * fps))) for k in keys]
    by_frame = {}
    for f, k in zip(frames, keys):
        by_frame[f] = k
    frames = sorted(by_frame)
    keys = [by_frame[f] for f in frames]

    # hold each pose over a contiguous ~0.23 s window: single-frame pins are
    # too sparse for the guidance, alternating frames made the postprocess oscillate
    held_frames, held_keys = [], []
    for f, k in zip(frames, keys):
        for df in range(-3, 4):
            hf = min(num_frames - 1, max(0, f + df))
            if not held_frames or hf > held_frames[-1]:
                held_frames.append(hf)
                held_keys.append(k)
    frames, keys = held_frames, held_keys

    dev = skel.neutral_joints.device
    neutral = skel.neutral_joints.float()
    n = skel.bone_index
    down = torch.tensor([0.0, -1.0, 0.0], device=dev)

    T = len(frames)
    poses = torch.zeros(T, skel.nbjoints, 3, device=dev)
    grots_t = torch.zeros(T, skel.nbjoints, 3, 3, device=dev)
    min_root_h = 1.05
    for i, k in enumerate(keys):
        # headset sits ~0.75 m above the hips; 0 = client sent no height
        root_h = min(1.05, max(0.45, k.by - 0.75)) if k.by > 0.2 else 0.97
        # low hands imply a low body even when the head reads standing
        lo = min(k.ly, k.ry)
        if lo < 0.65:
            root_h = min(root_h, max(0.45, 0.97 - (0.65 - lo) * 1.1))
        min_root_h = min(min_root_h, root_h)
        cy, sy = math.cos(k.h), math.sin(k.h)
        R = torch.tensor(
            [[cy, 0.0, sy], [0.0, 1.0, 0.0], [-sy, 0.0, cy]],
            dtype=torch.float32, device=dev,
        )
        pose = neutral @ R.T + torch.tensor(
            [k.bx - ox, root_h, k.bz - oz], dtype=torch.float32, device=dev
        )
        grots = R.unsqueeze(0).expand(skel.nbjoints, 3, 3).contiguous()

        for side, tgt in (("Left", (k.lx, k.ly, k.lz)), ("Right", (k.rx, k.ry, k.rz))):
            arm, forearm, wrist = n[side + "Arm"], n[side + "ForeArm"], n[side + "Hand"]
            thumb, tip = n[side + "HandThumbEnd"], n[side + "HandMiddleEnd"]
            o_fa = R @ (neutral[forearm] - neutral[arm])
            o_ha = R @ (neutral[wrist] - neutral[forearm])
            a_len, b_len = float(o_fa.norm()), float(o_ha.norm())

            S = pose[arm]
            W = torch.tensor(
                [tgt[0] - ox, tgt[1], tgt[2] - oz], dtype=torch.float32, device=dev
            )
            d = torch.clamp((W - S).norm(), abs(a_len - b_len) + 0.01, a_len + b_len - 0.01)
            axis = (W - S) / ((W - S).norm() + 1e-9)
            W = S + axis * d  # clamp unreachable targets onto the reachable sphere
            # elbow pole: down-and-out for low reaches, out-and-back above the shoulder
            out = R @ torch.tensor([1.0 if side == "Left" else -1.0, 0.0, 0.0],
                                   dtype=torch.float32, device=dev)
            back = R @ torch.tensor([0.0, 0.0, -0.35], dtype=torch.float32, device=dev)
            pref = (out + back) if float((W - S)[1]) > 0.05 else (down + out * 0.5)
            pole = pref - torch.dot(pref, axis) * axis
            if pole.norm() < 1e-4:
                pole = down - torch.dot(down, axis) * axis
            if pole.norm() < 1e-4:
                pole = torch.tensor([0.0, 0.0, -1.0], device=dev)
            pole = pole / pole.norm()
            cos_a = (a_len**2 + float(d) ** 2 - b_len**2) / (2 * a_len * float(d))
            cos_a = torch.clamp(torch.tensor(cos_a, device=dev), -1.0, 1.0)
            sin_a = (1 - cos_a**2).sqrt()
            E = S + axis * (a_len * cos_a) + pole * (a_len * sin_a)

            pose[forearm] = E
            pose[wrist] = W
            A1p = _swing(o_fa, E - S, dev) @ R
            A2p = _swing(o_ha, W - E, dev) @ R
            # the hand continues the forearm — wrist in line
            pose[tip] = W + A2p @ (neutral[tip] - neutral[wrist])
            pose[thumb] = W + A2p @ (neutral[thumb] - neutral[wrist])
            grots[arm] = A1p
            grots[forearm] = A2p
            grots[wrist] = A2p
            grots[thumb] = A2p
            grots[tip] = A2p

        poses[i] = pose
        grots_t[i] = grots

    cons = [
        _PoseLeft(skel, torch.tensor(frames), poses, grots_t, None),
        _PoseRight(skel, torch.tensor(frames), poses, grots_t, None),
    ]
    # pin pelvis height only for a demonstrated crouch (a locked standing pelvis hunches)
    for c in cons:
        c.pin_root_y = min_root_h < 0.9
    info = {
        "frame_indices": frames,
        "left": [[round(k.lx - ox, 3), round(k.ly, 3), round(k.lz - oz, 3)] for k in keys],
        "right": [[round(k.rx - ox, 3), round(k.ry, 3), round(k.rz - oz, 3)] for k in keys],
        "headings": [round(k.h, 3) for k in keys],
    }
    return cons, info


_skel77 = None


def _postprocess_full(output, constraints_json, req, num_frames, fps, offset_xz):
    """Whole-clip foot-skate cleanup + constraint snapping on the 77-joint
    output skeleton (the model's own per-segment postprocess skips the first
    segment, so we do it here with the full, uncropped constraint list)."""
    global _skel77
    import torch

    from kimodo.constraints import load_constraints_lst
    from kimodo.postprocess import post_process_motion
    from kimodo.skeleton.registry import build_skeleton

    if _skel77 is None:
        _skel77 = build_skeleton(77)

    cons = list(load_constraints_lst(constraints_json, _skel77)) if constraints_json else []
    if req.hand_keys:
        hand_cons, _ = build_hand_constraint(
            _skel77, req.hand_keys, num_frames, fps, offset_xz,
            req.hand_anchor, req.body_keys,
        )
        cons.append(hand_cons)
    if req.pose_keys:
        pose_cons, _ = build_pose_constraints(
            _skel77, req.pose_keys, num_frames, fps, offset_xz
        )
        cons.extend(pose_cons)
    if not cons:
        return output

    def _b(name, want_dims):  # numpy [T,...] -> torch [1,T,...]
        t = torch.tensor(np.asarray(output[name]))
        if t.dim() == want_dims - 1:
            t = t.unsqueeze(0)
        return t

    corrected = post_process_motion(
        _b("local_rot_mats", 5), _b("root_positions", 3), _b("foot_contacts", 3),
        _skel77, cons,
    )
    out = dict(output)
    for k, v in corrected.items():
        out[k] = np.asarray(v[0])  # strip batch dim
    return out


@app.get("/api/prompts")
def prompts():
    """Prompts whose embeddings are already cached — generating with these
    never touches the Llama encoder."""
    try:
        idx = json.loads((LazyCachingEncoder.EMBED_CACHE / "index.json").read_text())
    except (OSError, json.JSONDecodeError):
        idx = {}
    return {"prompts": sorted(set(idx.values()))}


@app.get("/api/progress")
def progress():
    return _progress


@app.get("/api/health")
def health():
    return {
        "model": MODEL_NAME,
        "loaded": _model is not None,
        "load_error": str(_model_err) if _model_err else None,
    }


@app.post("/api/generate")
def generate(req: GenerateRequest):
    import torch
    from kimodo.constraints import load_constraints_lst

    model = get_model()
    fps = float(model.fps)
    num_frames = int(req.duration * fps)

    constraints_json, (ox, oz), heading = waypoints_to_constraints(
        req.waypoints, num_frames, fps
    )
    # a pose near the clip start sets the initial facing (walking backward is legitimate)
    if req.pose_keys:
        pk0 = sorted(req.pose_keys, key=lambda k: k.t)[0]
        if heading is None or pk0.t < 0.5:
            heading = pk0.h
    constraint_lst = (
        load_constraints_lst(constraints_json, model.skeleton) if constraints_json else []
    )

    hand_info = None
    if req.hand_keys:
        hand_cons, hand_info = build_hand_constraint(
            model.skeleton, req.hand_keys, num_frames, fps, (ox, oz),
            req.hand_anchor, req.body_keys,
        )
        constraint_lst.append(hand_cons)

    pose_info = None
    if req.pose_keys:
        pose_cons, pose_info = build_pose_constraints(
            model.skeleton, req.pose_keys, num_frames, fps, (ox, oz)
        )
        constraint_lst.extend(pose_cons)

    # sentences become sequential prompt segments (like the official CLI); the first
    # segment carries the walk. Always pass lists — a bare string is consumed per char.
    texts = [s.strip() for s in req.prompt.split(".") if s.strip()]
    if len(texts) <= 1:
        texts, seg_frames = [req.prompt], [num_frames]
    else:
        path_end = constraints_json[0]["frame_indices"][-1] if constraints_json else 0
        first = max(path_end + 1, 30) if path_end else num_frames // len(texts)
        first = min(first, num_frames - 30 * (len(texts) - 1))
        rest, n_rest = num_frames - first, len(texts) - 1
        seg_frames = [first] + [rest // n_rest] * n_rest
        seg_frames[-1] += rest - sum(seg_frames[1:])

    if req.seed is not None:
        from kimodo.tools import seed_everything

        seed_everything(req.seed)

    first_heading = (
        torch.tensor([heading], device=next(model.parameters()).device)
        if heading is not None
        else None
    )

    t0 = time.time()
    if _lock.locked():
        _set_progress("queued", detail="another generation is running")
    with _lock:
        _progress["segment"] = 0
        _set_progress("preparing", detail="building conditioning")
        # hand traces: hard guidance; pose-only: softer (CFG 5 makes the body stiff)
        if req.hand_keys:
            cfg_extra = {"cfg_weight": [2.0, 5.0]}
        elif req.pose_keys:
            cfg_extra = {"cfg_weight": [2.0, 3.5]}
        else:
            cfg_extra = {}
        try:
            output = model(
                texts,
                seg_frames,
                constraint_lst=constraint_lst,
                **cfg_extra,
                num_denoising_steps=req.steps,
                num_samples=1,
                multi_prompt=True,
                # in-model postprocess skips the first segment — we run it on the full clip
                post_processing=False,
                return_numpy=True,
                first_heading_angle=first_heading,
                progress_bar=_progress_iter,
            )
            _set_progress("postprocess", detail="foot-skate cleanup + constraint snap")
            output = _postprocess_full(output, constraints_json, req, num_frames, fps, (ox, oz))
            _set_progress("done", pct=100)
        except Exception as e:
            _set_progress("error", detail=str(e)[:300])
            raise
    gen_seconds = time.time() - t0

    joints = np.asarray(output["posed_joints"])  # [S, T, J, 3]
    if joints.ndim == 4:
        joints = joints[0]
    # De-canonicalize back into the client's world space.
    joints = joints.copy()
    joints[..., 0] += ox
    joints[..., 2] += oz

    # SOMA samples on 30 joints but outputs 77 — names/parents must match the arrays
    skel = model.skeleton
    if joints.shape[1] != skel.nbjoints:
        global _skel77
        if _skel77 is None:
            from kimodo.skeleton.registry import build_skeleton

            _skel77 = build_skeleton(77)
        skel = _skel77
    joint_names = list(skel.bone_order_names)
    parents = [int(p) for p in skel.joint_parents.cpu().tolist()]

    session_id = datetime.now().strftime("%Y%m%d-%H%M%S")
    session_dir = SESSIONS_DIR / session_id
    session_dir.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        session_dir / "motion.npz",
        **{k: np.asarray(v) for k, v in output.items() if hasattr(v, "shape")},
    )
    (session_dir / "request.json").write_text(req.model_dump_json(indent=2))
    (session_dir / "constraints.json").write_text(json.dumps(constraints_json, indent=2))

    resp = {
        "fps": fps,
        "joint_names": joint_names,
        "parents": parents,
        "constraints_used": constraints_json,
        "hand_constraint": hand_info,
        "pose_constraint": pose_info,
        "segments": [texts, seg_frames] if isinstance(texts, list) else None,
        "session_dir": str(session_dir.relative_to(ROOT)),
        "gen_seconds": round(gen_seconds, 1),
    }
    if req.flat:
        import torch

        from kimodo.geometry import matrix_to_quaternion

        resp["frame_count"] = int(joints.shape[0])
        resp["joint_count"] = int(joints.shape[1])
        resp["frames_flat"] = joints.round(4).ravel().tolist()
        # retarget data (Kimodo coords): wxyz local quats, root trajectory, rest offsets
        lrm = np.asarray(output["local_rot_mats"])
        if lrm.ndim == 5:
            lrm = lrm[0]
        quats = matrix_to_quaternion(torch.tensor(lrm)).numpy()  # [T,J,4] wxyz
        rootp = np.asarray(output["root_positions"])
        if rootp.ndim == 3:
            rootp = rootp[0]
        # de-canonicalize like `joints` above
        rootp = rootp.copy()
        rootp[..., 0] += ox
        rootp[..., 2] += oz
        neutral = skel.neutral_joints.cpu().numpy()
        par = parents
        offsets = np.stack(
            [neutral[j] - (neutral[par[j]] if par[j] >= 0 else 0) for j in range(len(par))]
        )
        resp["quats_flat"] = quats.round(5).ravel().tolist()
        resp["root_flat"] = rootp.round(4).ravel().tolist()
        resp["bone_offsets_flat"] = offsets.round(4).ravel().tolist()
        resp["root_index"] = 0
    else:
        resp["frames"] = joints.round(4).tolist()
    return resp


@app.get("/")
def index():
    return FileResponse(Path(__file__).parent / "viewer.html")


if __name__ == "__main__":
    port = int(os.environ.get("PORT", "8123"))
    print(f"VR Human Motion server -> http://127.0.0.1:{port}")
    uvicorn.run(app, host="0.0.0.0", port=port)
