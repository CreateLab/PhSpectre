"""Generates the themed .cube LUTs bundled with PhSpectre and registers them in looks.json.

    python scripts/gen-looks.py

Standard library only; the same recipes always give byte-identical files. Looks that are not in LOOKS
below (the older ones) are left untouched. All colours are display-referred sRGB in 0..1.
"""
import colorsys
import json
import re
from pathlib import Path

SIZE = 17
ASSETS = Path(__file__).resolve().parent.parent / "PhSpectre.Avalonia" / "Assets" / "Looks"


def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else hi if v > hi else v


def smooth(e0, e1, x):
    t = clamp((x - e0) / (e1 - e0))
    return t * t * (3 - 2 * t)


def luma(c):
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]


def rgb(hexcode):
    return tuple(int(hexcode[i:i + 2], 16) / 255 for i in (0, 2, 4))


def mix(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def hue_distance(a, b):
    """Signed shortest way from hue a to hue b, in degrees."""
    return (b - a + 180) % 360 - 180


def tone(c, look):
    """Contrast, brightness and black / white levels, per channel."""
    out = []
    for v in c:
        v = clamp(v) ** look.get("gamma", 1.0)
        k = look.get("contrast", 0.0)
        v = v + (smooth(0, 1, v) - v) * k if k >= 0 else 0.5 + (v - 0.5) * (1 + k * 0.5)
        lo, hi = look.get("black", 0.0), look.get("white", 1.0)
        out.append(lo + (hi - lo) * v)
    return tuple(out)


def saturate(c, amount):
    g = luma(c)
    return tuple(clamp(g + (v - g) * amount) for v in c)


def hue_bands(c, bands):
    """bands: (centre, width, target hue, pull 0..1, saturation factor, brightness factor)."""
    h, s, v = colorsys.rgb_to_hsv(*(clamp(x) for x in c))
    if s < 1e-4:
        return c
    h *= 360
    shift, sat, val = 0.0, 1.0, 1.0
    # Greys have no meaningful hue: fade the effect in with saturation.
    presence = smooth(0.03, 0.25, s)
    for centre, width, target, pull, sf, vf in bands:
        w = (1 - smooth(width * 0.35, width, abs(hue_distance(centre, h)))) * presence
        if w <= 0:
            continue
        shift += hue_distance(h, target) * pull * w
        sat *= 1 + (sf - 1) * w
        val *= 1 + (vf - 1) * w
    return colorsys.hsv_to_rgb(((h + shift) % 360) / 360, clamp(s * sat), clamp(v * val))


def gradient(stops, t):
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        if t <= p1:
            return mix(c0, c1, clamp((t - p0) / (p1 - p0)))
    return stops[-1][1]


def split_tone(c, look):
    """Pushes shadows, mid-tones and highlights toward a colour without changing their brightness much."""
    l = clamp(luma(c))
    weights = {
        # Fade to nothing at pure black and white so both ends stay clean.
        "shadows": (1 - smooth(0.15, 0.65, l)) * smooth(0.0, 0.12, l),
        "mids": smooth(0.1, 0.45, l) * (1 - smooth(0.55, 0.9, l)),
        "highs": smooth(0.35, 0.8, l) * (1 - smooth(0.9, 1.0, l)),
    }
    out = list(c)
    for key, w in weights.items():
        if key not in look:
            continue
        hexcode, amount = look[key]
        tint = rgb(hexcode)
        g = luma(tint)
        for i in range(3):
            out[i] += (tint[i] - g) * amount * w
    return tuple(clamp(v) for v in out)


def skin_weight(c):
    """How much a colour looks like skin: warm hue, moderate saturation, not too dark. Soft edges."""
    h, s, v = colorsys.rgb_to_hsv(*c)
    d = hue_distance(22, h * 360)
    hue = 1 - smooth(12, 30, abs(d))
    sat = smooth(0.08, 0.22, s) * (1 - smooth(0.65, 0.85, s))
    val = smooth(0.12, 0.32, v)
    return hue * sat * val


def grade(c, look):
    out = tone(c, look)
    if "mono" in look:
        out = mix(out, (luma(out),) * 3, look["mono"])
    out = saturate(out, look.get("saturation", 1.0))
    if "bands" in look:
        out = hue_bands(out, look["bands"])
    if "map" in look:
        stops, amount = look["map"]
        g = luma(out)
        mapped = gradient([(p, rgb(h)) for p, h in stops], g)
        # Keep the original brightness: only the colour comes from the gradient.
        d = g - luma(mapped)
        mapped = tuple(clamp(v + d * 0.7) for v in mapped)
        out = mix(out, mapped, amount)
    out = split_tone(out, look)

    keep = look.get("skin", 0.0)
    if keep > 0:
        # Skin keeps the look's light and contrast but only a trace of its colour shift.
        natural = tone(c, look)
        natural = saturate(natural, 1 + (look.get("saturation", 1.0) - 1) * 0.3)
        out = mix(out, natural, keep * skin_weight(c))
    return tuple(clamp(v) for v in out)


# Hue centres for readability.
RED, ORANGE, YELLOW, LIME, GREEN, TEAL, CYAN, BLUE, VIOLET, MAGENTA, PINK = 0, 30, 55, 85, 125, 170, 190, 225, 270, 305, 335

LOOKS = [
    # ── Neon ────────────────────────────────────────────────────────────────────────
    ("Vice Sunset", "Neon", dict(contrast=0.2, black=0.03, saturation=1.2, skin=0.65,
        bands=[(BLUE, 60, CYAN, 0.5, 1.15, 1.0), (GREEN, 55, TEAL, 0.8, 1.0, 0.95), (RED, 30, PINK, 0.6, 1.1, 1.0),
               (YELLOW, 30, ORANGE, 0.6, 1.1, 1.0)],
        shadows=("1fb5c9", 0.22), mids=("c23aa8", 0.16), highs=("ff8a9c", 0.3))),
    ("Neon Violet", "Neon", dict(contrast=0.3, saturation=1.25, skin=0.65,
        bands=[(BLUE, 70, VIOLET, 0.85, 1.2, 1.0), (GREEN, 70, 105, 0.8, 1.35, 1.1), (CYAN, 35, VIOLET, 0.5, 1.0, 1.0)],
        shadows=("5b17d6", 0.38), mids=("7a2fc0", 0.08), highs=("7dff4a", 0.2))),
    ("Midnight Neon", "Neon", dict(contrast=0.35, gamma=1.08, saturation=1.2, skin=0.55,
        bands=[(BLUE, 60, CYAN, 0.7, 1.25, 1.0), (VIOLET, 40, MAGENTA, 0.7, 1.2, 1.0), (RED, 30, PINK, 0.8, 1.15, 1.0),
               (GREEN, 60, CYAN, 0.7, 0.9, 0.9)],
        shadows=("0a4fa0", 0.35), highs=("ff2fa8", 0.2))),
    ("Acid Yellow", "Neon", dict(contrast=0.32, saturation=1.15, skin=0.6,
        bands=[(ORANGE, 40, YELLOW, 0.75, 1.2, 1.05), (GREEN, 70, YELLOW, 0.5, 1.15, 1.05), (BLUE, 70, CYAN, 0.8, 1.2, 1.0),
               (MAGENTA, 50, RED, 0.5, 0.8, 1.0)],
        shadows=("0b6f8f", 0.28), mids=("8a8a00", 0.1), highs=("f5ff2a", 0.28))),
    ("Synthwave 84", "Neon", dict(contrast=0.15, black=0.04, saturation=1.1, skin=0.5,
        map=([(0.0, "120338"), (0.3, "4b1390"), (0.55, "d42a90"), (0.78, "ff7a45"), (1.0, "ffe9a8")], 0.55))),
    ("Vaporwave", "Neon", dict(contrast=-0.18, black=0.12, white=0.97, saturation=0.95, skin=0.45,
        map=([(0.0, "2e3a7a"), (0.35, "6f7fd0"), (0.6, "e08fd0"), (0.82, "9ff0e8"), (1.0, "fff0fa")], 0.45),
        shadows=("5a4ae0", 0.15))),
    ("Tokyo Rain", "Neon", dict(contrast=0.22, gamma=1.1, black=0.02, saturation=0.9, skin=0.6,
        bands=[(GREEN, 70, TEAL, 0.85, 0.85, 0.9), (RED, 35, RED, 0.0, 1.45, 1.05), (YELLOW, 30, ORANGE, 0.5, 0.8, 1.0),
               (BLUE, 60, 205, 0.6, 1.2, 1.0)],
        shadows=("0f6f78", 0.3), highs=("a9d8ff", 0.16))),
    ("Red District", "Neon", dict(contrast=0.28, gamma=1.06, saturation=1.1, skin=0.55,
        bands=[(BLUE, 80, VIOLET, 0.8, 0.9, 0.85), (GREEN, 70, 205, 0.7, 0.5, 0.8), (MAGENTA, 50, PINK, 0.6, 1.25, 1.0)],
        shadows=("3a0a5e", 0.28), mids=("c01030", 0.22), highs=("ff5560", 0.25))),
    # ── Games ───────────────────────────────────────────────────────────────────────
    ("Wasteland", "Games", dict(contrast=0.12, black=0.06, white=0.96, saturation=0.6, skin=0.3,
        bands=[(BLUE, 80, 185, 0.7, 0.55, 1.0), (GREEN, 70, YELLOW, 0.75, 0.8, 1.0)],
        shadows=("5a4a20", 0.18), mids=("c89a3a", 0.22), highs=("ffe0a0", 0.2))),
    ("Toxic Zone", "Games", dict(contrast=0.25, gamma=1.05, black=0.03, saturation=0.75, skin=0.45,
        bands=[(YELLOW, 40, LIME, 0.8, 1.15, 1.0), (BLUE, 70, TEAL, 0.9, 0.7, 0.9), (RED, 30, ORANGE, 0.4, 0.85, 1.0)],
        shadows=("123a18", 0.3), mids=("6f8f10", 0.24), highs=("d8f060", 0.2))),
    ("Frontier Dusk", "Games", dict(contrast=0.24, saturation=1.0, skin=0.5,
        bands=[(YELLOW, 40, ORANGE, 0.6, 1.1, 1.0), (GREEN, 70, YELLOW, 0.6, 0.7, 0.85), (BLUE, 70, 215, 0.4, 0.65, 0.9)],
        shadows=("46262a", 0.25), mids=("b8602a", 0.2), highs=("ffbe6a", 0.35))),
    ("Frost Realm", "Games", dict(contrast=0.15, gamma=0.94, black=0.04, saturation=0.65, skin=0.5,
        bands=[(ORANGE, 40, ORANGE, 0.0, 0.7, 1.0), (GREEN, 70, TEAL, 0.9, 0.6, 1.1), (BLUE, 60, 205, 0.5, 1.2, 1.05)],
        shadows=("1c3f80", 0.28), mids=("5090c8", 0.14), highs=("d4f0ff", 0.25))),
    ("Ember Ash", "Games", dict(contrast=0.3, gamma=1.18, saturation=0.45, skin=0.35,
        bands=[(ORANGE, 35, 20, 0.5, 1.8, 1.1), (RED, 30, RED, 0.0, 1.5, 1.0)],
        shadows=("1c2430", 0.22), highs=("ff7a1a", 0.3))),
    ("Pixel Pop", "Games", dict(contrast=0.25, gamma=0.94, saturation=1.55, skin=0.75,
        bands=[(GREEN, 70, 110, 0.4, 1.15, 1.08), (BLUE, 50, 215, 0.3, 1.15, 1.05)])),
    ("Painted City", "Games", dict(contrast=0.4, black=0.05, saturation=1.3, skin=0.5,
        bands=[(BLUE, 70, 235, 0.5, 1.3, 1.0), (GREEN, 60, TEAL, 0.9, 1.3, 1.05), (RED, 35, PINK, 0.9, 1.3, 1.05),
               (VIOLET, 40, MAGENTA, 0.7, 1.25, 1.05)],
        shadows=("2020c0", 0.3), mids=("e020a0", 0.1), highs=("40f0e0", 0.2))),
    # ── Cinema ──────────────────────────────────────────────────────────────────────
    ("Green Code", "Cinema", dict(contrast=0.3, gamma=1.08, saturation=0.5, skin=0.2,
        map=([(0.0, "000a02"), (0.35, "12461f"), (0.65, "5fa860"), (1.0, "e6ffe0")], 0.7))),
    ("Desert Spice", "Cinema", dict(contrast=0.08, black=0.05, white=0.95, saturation=0.7, skin=0.35,
        bands=[(BLUE, 90, 200, 0.5, 0.35, 1.05), (GREEN, 70, ORANGE, 0.7, 0.55, 1.0)],
        shadows=("6a4020", 0.2), mids=("d08a40", 0.28), highs=("f8d8a8", 0.25))),
    ("Orange Haze", "Cinema", dict(contrast=-0.05, black=0.06, saturation=0.8, skin=0.15,
        map=([(0.0, "1a0800"), (0.35, "803008"), (0.65, "e07818"), (1.0, "ffe6b0")], 0.78))),
    ("Pastel Hotel", "Cinema", dict(contrast=-0.15, gamma=0.88, black=0.07, saturation=0.9, skin=0.55,
        bands=[(RED, 40, PINK, 0.6, 0.85, 1.08), (BLUE, 60, 195, 0.6, 0.8, 1.12), (GREEN, 60, 155, 0.6, 0.7, 1.1),
               (YELLOW, 30, YELLOW, 0.0, 1.15, 1.05)],
        shadows=("8a5a90", 0.14), mids=("ff90a0", 0.12), highs=("ffe8c0", 0.2))),
    ("Scorched Road", "Cinema", dict(contrast=0.45, saturation=1.35, skin=0.3,
        bands=[(YELLOW, 45, 28, 0.7, 1.25, 1.0), (BLUE, 80, 188, 0.85, 1.3, 1.0), (GREEN, 60, ORANGE, 0.5, 0.8, 0.95)],
        shadows=("00707a", 0.3), highs=("ff8c10", 0.3))),
    ("Sick Stairs", "Cinema", dict(contrast=0.22, gamma=1.08, black=0.03, saturation=0.85, skin=0.45,
        bands=[(RED, 35, 345, 0.7, 1.2, 0.85), (BLUE, 70, 175, 0.8, 0.8, 0.95), (YELLOW, 35, 65, 0.6, 1.0, 0.95)],
        shadows=("0c4a3a", 0.34), mids=("6a7020", 0.14), highs=("e0c060", 0.2))),
    ("Paris Green", "Cinema", dict(contrast=0.26, saturation=1.2, skin=0.5,
        bands=[(GREEN, 60, 110, 0.5, 1.2, 0.95), (BLUE, 70, 175, 0.85, 0.75, 0.95), (RED, 35, 5, 0.4, 1.25, 1.0)],
        shadows=("0a5020", 0.25), mids=("b08a10", 0.2), highs=("ffd860", 0.28))),
    ("Noir Rain", "Cinema", dict(contrast=0.45, gamma=1.12, saturation=0.25, skin=0.3,
        bands=[(RED, 30, RED, 0.0, 2.2, 1.0)],
        shadows=("102a48", 0.25), highs=("c8d8e8", 0.12))),
    ("Crimson Room", "Cinema", dict(contrast=0.3, gamma=1.1, saturation=0.9, skin=0.4,
        map=([(0.0, "080004"), (0.35, "5a0818"), (0.65, "c82830"), (1.0, "ffd8c8")], 0.5))),
    # ── Portrait: gentle, flattering, natural skin ──────────────────────────────────
    ("Clean Skin", "Portrait", dict(gamma=0.94, contrast=0.08, black=0.015,
        bands=[(RED, 28, 14, 0.4, 0.9, 1.04), (ORANGE, 30, 25, 0.35, 0.95, 1.05), (YELLOW, 30, 42, 0.45, 0.82, 1.02)],
        shadows=("30506a", 0.05))),
    ("Peach", "Portrait", dict(gamma=0.9, contrast=0.04, black=0.03, saturation=1.04,
        bands=[(ORANGE, 40, 20, 0.5, 1.05, 1.05), (YELLOW, 30, 38, 0.6, 0.85, 1.03), (GREEN, 60, 100, 0.3, 0.75, 1.0)],
        mids=("ff8a70", 0.1), highs=("ffd0b8", 0.14))),
    ("Porcelain", "Portrait", dict(gamma=0.8, contrast=-0.08, black=0.04, saturation=0.86,
        bands=[(ORANGE, 45, 20, 0.2, 0.75, 1.07), (YELLOW, 30, 40, 0.4, 0.65, 1.04)],
        shadows=("506090", 0.1), highs=("e8e8ff", 0.08))),
    ("Honey Tan", "Portrait", dict(gamma=1.07, contrast=0.2, saturation=1.08,
        bands=[(RED, 28, 18, 0.4, 1.0, 1.0), (ORANGE, 40, 28, 0.3, 1.18, 0.97), (BLUE, 60, 200, 0.3, 0.85, 1.0)],
        mids=("c88020", 0.12), highs=("ffd070", 0.12))),
    ("Soft Film Portrait", "Portrait", dict(contrast=-0.1, black=0.075, white=0.97, saturation=0.84,
        bands=[(GREEN, 70, 105, 0.35, 0.7, 1.03), (BLUE, 60, 195, 0.4, 0.8, 1.02), (ORANGE, 40, 23, 0.25, 1.05, 1.03)],
        shadows=("207060", 0.12), highs=("ffe4c4", 0.16))),
    # ── Film and trends ─────────────────────────────────────────────────────────────
    ("Tungsten Night", "Film", dict(contrast=0.2, black=0.045, saturation=1.08, skin=0.55,
        bands=[(ORANGE, 40, 16, 0.45, 1.15, 1.0), (YELLOW, 35, 75, 0.65, 0.85, 1.0), (BLUE, 60, 198, 0.6, 1.2, 1.0)],
        shadows=("0c6890", 0.42), mids=("2870b0", 0.2), highs=("ffb090", 0.18))),
    ("Digicam 2016", "Film", dict(gamma=0.82, contrast=0.22, black=0.02, saturation=1.22, skin=0.4,
        bands=[(BLUE, 60, 215, 0.3, 1.15, 1.05)],
        shadows=("3040a0", 0.14), highs=("f4f8ff", 0.1), mids=("ff70b0", 0.07))),
    ("Summer Disposable", "Film", dict(contrast=0.12, gamma=0.95, black=0.09, white=0.98, saturation=1.1, skin=0.45,
        bands=[(GREEN, 65, 90, 0.5, 0.9, 1.0), (BLUE, 60, 190, 0.55, 0.95, 1.0)],
        shadows=("106a40", 0.18), mids=("e8a020", 0.12), highs=("fff090", 0.2))),
]


def slug(name):
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def cube_text(name, look):
    lines = [f"# {name} - generated for PhSpectre (original work)", f'TITLE "{name}"', f"LUT_3D_SIZE {SIZE}"]
    n = SIZE - 1
    for b in range(SIZE):
        for g in range(SIZE):
            for r in range(SIZE):
                lines.append("%.4f %.4f %.4f" % grade((r / n, g / n, b / n), look))
    return "\n".join(lines) + "\n"


def main():
    index_path = ASSETS / "looks.json"
    index = json.loads(index_path.read_text(encoding="utf-8-sig"))
    mine = {f"bundled-lut-{slug(name)}" for name, _, _ in LOOKS}
    assert len(mine) == len(LOOKS), "two looks share a name"
    index = [item for item in index if item["id"] not in mine]
    for name, category, look in LOOKS:
        file = f"luts/lut-{slug(name)}.cube"
        (ASSETS / file).write_bytes(cube_text(name, look).encode("utf-8"))
        index.append(dict(id=f"bundled-lut-{slug(name)}", name=name, kind="lut", category=category, file=file, skipped=[]))
    index_path.write_text(json.dumps(index, ensure_ascii=False, indent=4) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(LOOKS)} looks written, {len(index)} in the index")


if __name__ == "__main__":
    main()
