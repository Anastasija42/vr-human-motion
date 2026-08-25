# -*- coding: utf-8 -*-
"""Vector diagrams for docs/report/overleaf_sr.tex (Serbian labels)."""
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch

OUT = "/home/ana/vr-human-motion/docs/report/figures/"
BOX = dict(boxstyle="round,pad=0.32", lw=1.1)
C_UNITY = ("#eaf2fb", "#1e5a8c")
C_SRV = ("#fdf3e3", "#8c651e")
C_MODEL = ("#eefaef", "#2e7d43")
C_IO = ("#f3f0fa", "#5b4a8c")


def box(ax, x, y, w, h, title, lines, fc, ec):
    ax.add_patch(FancyBboxPatch((x - w / 2, y - h / 2), w, h,
                                fc=fc, ec=ec, **BOX))
    ty = y + h / 2 - 0.16 if lines else y
    ax.text(x, ty, title, ha="center",
            va="top" if lines else "center",
            fontsize=10.5, fontweight="bold", color=ec)
    if lines:
        ax.text(x, y - h / 2 + 0.10, "\n".join(lines), ha="center",
                va="bottom", fontsize=9, color="#333333", linespacing=1.35)


def arrow(ax, x1, y1, x2, y2, label=""):
    ax.add_patch(FancyArrowPatch((x1, y1), (x2, y2), arrowstyle="-|>",
                                 mutation_scale=14, lw=1.2, color="#555555"))
    if label:
        ax.text((x1 + x2) / 2 + 0.09, (y1 + y2) / 2, label, fontsize=8.6,
                color="#555555", ha="left", va="center", style="italic")


# ---------------------------------------------------------------- Figure 1
fig, ax = plt.subplots(figsize=(4.9, 8.2))
steps = [
    ("KORISNIK", ["tekstualni opis · putanja", "trag dodira · ključne poze"], C_IO, 1.05),
    ("UNITY — klijent", ["obrada demonstracije", "planiranje kroz scenu"], C_UNITY, 1.05),
    ("JSON zahtev", [], C_IO, 0.55),
    ("SERVER", ["transformacija koordinata", "konstrukcija Kimodo ograničenja"], C_SRV, 1.05),
    ("KIMODO", ["difuziono generisanje"], C_MODEL, 0.85),
    ("Naknadna obrada", ["klizanje stopala · constraint snapping"], C_SRV, 0.85),
    ("UNITY — reprodukcija", ["retargetovanje + IK kontakta"], C_UNITY, 0.85),
    ("GENERISANI ČOVEK U SCENI", [], C_IO, 0.55),
]
total = sum(h for *_a, h in steps) + 0.55 * (len(steps) - 1)
y = total
for i, (title, lines, col, h) in enumerate(steps):
    y -= h / 2
    box(ax, 2.4, y, 4.2, h, title, lines, *col)
    if i < len(steps) - 1:
        arrow(ax, 2.4, y - h / 2, 2.4, y - h / 2 - 0.55)
    y -= h / 2 + 0.55
ax.set_xlim(0, 4.8), ax.set_ylim(-0.2, total + 0.2), ax.axis("off")
fig.savefig(OUT + "pipeline.pdf", bbox_inches="tight")
plt.close(fig)

# ---------------------------------------------------------------- Figure 2
fig, ax = plt.subplots(figsize=(7.6, 4.6))
rows = [
    ("hod korisnika\n(vizir na tlu)", "root2d(t)\nputanja korena", 3.9),
    ("desni kontroler\npo površini", "right-hand(t)\ntrag dodira šake", 2.8),
    ("vizir + dva kontrolera\n(dugme X)", "ključna poza\nkoren · pravac · leva i desna šaka", 1.7),
    ("tekstualni opis", "vektorska reprezentacija teksta\n(LLM2Vec, keširano)", 0.6),
]
ax.text(1.55, 4.75, "VR demonstracija", ha="center", fontsize=11,
        fontweight="bold", color=C_UNITY[1])
ax.text(6.05, 4.75, "Kimodo reprezentacija", ha="center", fontsize=11,
        fontweight="bold", color=C_MODEL[1])
for left, right, y in rows:
    box(ax, 1.55, y, 2.9, 0.9, "", [], *C_UNITY)
    ax.text(1.55, y, left, ha="center", va="center", fontsize=9.3, color="#1e3a52")
    box(ax, 6.05, y, 3.5, 0.9, "", [], *C_MODEL)
    ax.text(6.05, y, right, ha="center", va="center", fontsize=9.3, color="#1e4a2c")
    arrow(ax, 3.05, y, 4.25, y)
ax.set_xlim(0, 7.9), ax.set_ylim(0, 5.1), ax.axis("off")
fig.savefig(OUT + "demo_mapping.pdf", bbox_inches="tight")
plt.close(fig)

# ---------------------------------------------------------------- Figure 3
fig, ax = plt.subplots(figsize=(4.6, 6.4))
steps = [
    ("Kimodo skelet", ["generisane rotacije zglobova"], C_MODEL),
    ("HumanPoseHandler", ["normalizovana humanoidna poza", "(mišićne koordinate)"], C_UNITY),
    ("Skinovani karakter", ["druge proporcije tela", "→ kontakt blago odstupa"], C_UNITY),
    ("IK korekcija kontakta", ["dvokosna IK ruke, vrh prsta,", "glatko utapanje oko kontakta"], C_SRV),
    ("Precizan dodir površine", [], C_IO),
]
y = 6.6
for i, (title, lines, col) in enumerate(steps):
    h = 0.55 + 0.3 * len(lines)
    y -= h / 2
    box(ax, 2.2, y, 4.0, h, title, lines, *col)
    if i < len(steps) - 1:
        arrow(ax, 2.2, y - h / 2, 2.2, y - h / 2 - 0.45)
    y -= h / 2 + 0.45
ax.set_xlim(0, 4.4), ax.set_ylim(y - 0.1, 6.75), ax.axis("off")
fig.savefig(OUT + "retarget.pdf", bbox_inches="tight")
plt.close(fig)

print("figures written to", OUT)
