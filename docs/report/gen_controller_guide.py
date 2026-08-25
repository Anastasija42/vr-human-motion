# -*- coding: utf-8 -*-
"""Quest-style VR controller diagram with labeled bindings -> Unity Resources."""
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.transforms as mtr
from matplotlib.patches import Circle, Ellipse, FancyBboxPatch, FancyArrowPatch

BG = "#12141b"
LINE = "#9db1cc"
TXT = "#e6edf7"
ACC = "#8fd0ff"
WARM = "#ffd479"
LEAD = "#4d5a72"

fig, ax = plt.subplots(figsize=(13, 8.4))
fig.patch.set_facecolor(BG)
ax.set_facecolor(BG)


def controller(cx, mirror):
    """Quest-Touch-like: tilted tracking ring, face plate, angled grip.
    Stick sits top-INNER, face buttons below-outer, on the face plate."""
    s = -1 if mirror else 1
    ring = Ellipse((cx + 0.25 * s, 6.1), 2.5, 1.35, fill=False, ec=LINE, lw=2.6)
    ring.set_transform(mtr.Affine2D().rotate_deg_around(cx + 0.25 * s, 6.1, -18 * s) + ax.transData)
    ax.add_patch(ring)
    face = FancyBboxPatch((cx - 0.75, 4.35), 1.5, 1.55,
                          boxstyle="round,pad=0.18", fill=False, ec=LINE, lw=2.6)
    ax.add_patch(face)
    g = FancyBboxPatch((cx - 0.36, 2.25), 0.72, 2.1,
                       boxstyle="round,pad=0.14", fill=False, ec=LINE, lw=2.6)
    g.set_transform(mtr.Affine2D().rotate_deg_around(cx, 4.3, 16 * s) + ax.transData)
    ax.add_patch(g)
    sx, sy = cx - 0.32 * s, 5.62
    ax.add_patch(Circle((sx, sy), 0.34, fill=False, ec=ACC, lw=2.6))
    ax.add_patch(Circle((sx, sy), 0.15, color=ACC))
    ax.add_patch(Ellipse((cx + 0.42 * s, 6.28), 0.55, 0.34, fill=False, ec=LINE, lw=2.2))
    return sx, sy


def label(ex, ey, tx, ty, text, color=TXT, ha="left", fs=14):
    ax.add_patch(FancyArrowPatch((tx, ty), (ex, ey), arrowstyle="-", color=LEAD, lw=1.5))
    dx = 0.1 if ha == "left" else (-0.1 if ha == "right" else 0)
    ax.text(tx + dx, ty, text, color=color, fontsize=fs, ha=ha, va="center",
            linespacing=1.25)


# ---------------- LEFT (cx=4.1): stick top-right(inner), X/Y below-left(outer)
lsx, lsy = controller(4.1, True)
ax.text(4.1, 7.6, "LEFT", color=LINE, fontsize=17, ha="center", fontweight="bold")
yx, yy = 3.7, 5.35
xx, xy = 3.88, 4.78
ax.add_patch(Circle((yx, yy), 0.24, fill=False, ec=WARM, lw=2.4))
ax.text(yx, yy, "Y", color=WARM, fontsize=13, ha="center", va="center", fontweight="bold")
ax.add_patch(Circle((xx, xy), 0.24, fill=False, ec=WARM, lw=2.4))
ax.text(xx, xy, "X", color=WARM, fontsize=13, ha="center", va="center", fontweight="bold")
ax.add_patch(Circle((4.45, 4.7), 0.17, fill=False, ec=ACC, lw=2.0))
ax.text(4.45, 4.7, "≡", color=ACC, fontsize=11, ha="center", va="center")

label(3.6, 6.5, 0.25, 7.15, "hold — WALK PATH", WARM)
label(xx - 0.22, xy, 0.25, 4.5, "X tap — SNAP POSE\nX HOLD — menu panel\n(selected: delete)", WARM)
label(yx - 0.22, yy + 0.08, 0.25, 5.95, "Y — next prompt", TXT)
label(4.45, 4.52, 0.25, 3.2, "MENU — panel (alt)", ACC)
label(lsx, lsy + 0.36, 6.5, 7.25, "L-stick — move person / panel", ACC, "center")

# ---------------- RIGHT (cx=8.9): stick top-left(inner), A/B below-right(outer)
rsx, rsy = controller(8.9, False)
ax.text(8.9, 7.6, "RIGHT", color=LINE, fontsize=17, ha="center", fontweight="bold")
bx, by = 9.3, 5.35
axx, ay = 9.12, 4.78
ax.add_patch(Circle((bx, by), 0.24, fill=False, ec=WARM, lw=2.4))
ax.text(bx, by, "B", color=WARM, fontsize=13, ha="center", va="center", fontweight="bold")
ax.add_patch(Circle((axx, ay), 0.24, fill=False, ec=WARM, lw=2.4))
ax.text(axx, ay, "A", color=WARM, fontsize=13, ha="center", va="center", fontweight="bold")

label(9.4, 6.5, 12.75, 7.15, "hold — TRACE TOUCH", WARM, "right")
label(axx + 0.22, ay, 12.75, 4.5, "A — GENERATE\n(selected: follow-up)", WARM, "right")
label(bx + 0.22, by + 0.08, 12.75, 5.95, "B tap — prompt / select\nB HOLD — type (VR keyboard)", TXT, "right")
label(rsx, rsy + 0.36, 6.5, 2.5, "R-stick — rotate · raise/lower\nscroll prompts · panel dist", ACC, "center")

ax.text(6.5, 1.5, "your hands show as controllers with a LASER from the right one —",
        color=TXT, fontsize=12.5, ha="center")
ax.text(6.5, 1.05, "aim the laser at a person + B tap = select · B tap again = release",
        color=ACC, fontsize=13, ha="center")
ax.text(6.5, 0.45, "grips:  RIGHT grip = clear all · LEFT grip = pose (alt)",
        color="#9aa4b5", fontsize=12, ha="center")

ax.set_xlim(0, 13)
ax.set_ylim(0, 8.1)
ax.axis("off")
fig.savefig("/mnt/c/Users/anast/KimodoUnity/Assets/KimodoVR/Resources/controller_guide.png",
            dpi=115, bbox_inches="tight", facecolor=BG)
print("saved")
