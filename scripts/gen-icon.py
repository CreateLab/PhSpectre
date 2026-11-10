"""Generates the PhSpectre launcher icon from one geometry (6-blade aperture, 4 spectral blades).

Outputs:
  PhSpectre.Android/Resources/drawable/ic_launcher_foreground.xml  (color, adaptive foreground)
  PhSpectre.Android/Resources/drawable/ic_launcher_monochrome.xml  (themed icon, Android 13+)
  PhSpectre.Android/Resources/drawable/ic_launcher_splash.xml      (splash: dark disc + glyph)
  docs/design/icon/*.svg and store-icon-512.png (Play Console)
Run: python scripts/gen-icon.py
"""
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RES = ROOT / "PhSpectre.Android" / "Resources"
DOCS = ROOT / "docs" / "design" / "icon"

BG = "#17171B"
GRAY_A, GRAY_B = "#8E8E96", "#77777F"
SPECTRUM = {-30: "#F28A1B", 30: "#1FB87A", 90: "#2E5BF0", 150: "#7A3FDB"}
GRAYS = {-150: GRAY_B, -90: GRAY_A}

CX = CY = 54.0      # adaptive icon canvas is 108x108, safe zone is the centre 66
R_OUT, R_IN = 26.0, 8.5
TWIST = 35.0        # pinwheel twist of the blade edges
SHRINK = 0.955      # shrinks each blade about its centroid -> the gaps between blades
ARC_STEPS = 6


def pol(deg, r):
    a = math.radians(deg)
    return (CX + r * math.cos(a), CY + r * math.sin(a))


def blade(c):
    pts = [pol(c - 30, R_IN), pol(c + 30, R_IN)]
    for i in range(ARC_STEPS + 1):  # outer arc, end -> start
        pts.append(pol(c + 30 + TWIST - 60 * i / ARC_STEPS, R_OUT))
    mx = sum(p[0] for p in pts) / len(pts)
    my = sum(p[1] for p in pts) / len(pts)
    return [(mx + (x - mx) * SHRINK, my + (y - my) * SHRINK) for x, y in pts]


CENTRES = [-150, -90, -30, 30, 90, 150]
BLADES = [(c, blade(c), SPECTRUM.get(c) or GRAYS[c]) for c in CENTRES]


def path_data(pts, scale=1.0, ox=0.0, oy=0.0):
    f = lambda p: f"{(p[0] - CX) * scale + CX + ox:.2f} {(p[1] - CY) * scale + CY + oy:.2f}"
    return "M " + " L ".join(f(p) for p in pts) + " Z"


def vector_xml(fill_for, with_bg=None, scale=1.0):
    out = ['<?xml version="1.0" encoding="utf-8"?>',
           '<vector xmlns:android="http://schemas.android.com/apk/res/android"',
           '    android:width="108dp" android:height="108dp"',
           '    android:viewportWidth="108" android:viewportHeight="108">']
    if with_bg:
        out.append(f'    <path android:fillColor="{with_bg}" android:pathData="M 0 0 H 108 V 108 H 0 Z" />')
    for c, pts, color in BLADES:
        out.append(f'    <path android:fillColor="{fill_for(color)}" android:pathData="{path_data(pts, scale)}" />')
    out.append('</vector>')
    return "\n".join(out) + "\n"


def svg(size, fill_for, bg=None, scale=1.0, rounded=False):
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 108 108">']
    if bg:
        rx = ' rx="24"' if rounded else ""
        out.append(f'  <rect width="108" height="108"{rx} fill="{bg}"/>')
    for c, pts, color in BLADES:
        out.append(f'  <path fill="{fill_for(color)}" d="{path_data(pts, scale)}"/>')
    out.append('</svg>')
    return "\n".join(out) + "\n"


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def render_png(size, scale):
    from PIL import Image, ImageDraw
    ss = 4
    s = size * ss
    img = Image.new("RGB", (s, s), BG)
    d = ImageDraw.Draw(img)
    k = s / 108.0
    for c, pts, color in BLADES:
        poly = [(((x - CX) * scale + CX) * k, ((y - CY) * scale + CY) * k) for x, y in pts]
        d.polygon(poly, fill=color)
    return img.resize((size, size), Image.LANCZOS)


same = lambda c: c
mono = lambda c: "#FF000000"

# adaptive icon layers
write(RES / "drawable" / "ic_launcher_foreground.xml", vector_xml(same))
write(RES / "drawable" / "ic_launcher_monochrome.xml", vector_xml(mono))
# splash (Android 12+ draws it inside a circle, so the dark disc is part of the drawable)
write(RES / "drawable" / "ic_launcher_splash.xml", vector_xml(same, with_bg=BG))

# design sources
write(DOCS / "icon-color.svg", svg(512, same, bg=BG, scale=1.45, rounded=True))
write(DOCS / "icon-foreground.svg", svg(432, same))
write(DOCS / "icon-monochrome.svg", svg(432, lambda c: "#000000"))
DOCS.mkdir(parents=True, exist_ok=True)
render_png(512, 1.45).save(DOCS / "store-icon-512.png")
render_png(432, 1.0).save(DOCS / "preview-launcher-432.png")
print("icon written")
