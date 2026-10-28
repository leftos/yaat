"""Build LayoutInspector style variants from a rendered baseline HTML.

Usage: python make_variants.py <dir>   (reads <dir>/baseline.html, writes <dir>/variant-*.html)

Only colours, opacities, stroke weights and the runway-label font weight change; geometry is untouched.
The hard-coded paint literals in draw() are first rerouted through new PAINT keys, so every variant
(and the eventual implementation) is one PAINT object plus one :root block.
"""

import re
import sys
from pathlib import Path

# Baseline literals rerouted through PAINT keys (old text -> new text, expected count).
REROUTE = [
    (
        "      ctx.globalAlpha = 0.16;\n      ctx.strokeStyle = e.rwy",
        "      ctx.globalAlpha = PAINT.dimAlpha;\n      ctx.strokeStyle = e.rwy",
        1,
    ),
    (
        "      ctx.globalAlpha = 0.16;\n      ctx.strokeStyle = PAINT.legendArc;\n      ctx.lineWidth = 0.7;",
        "      ctx.globalAlpha = PAINT.dimArcAlpha;\n      ctx.strokeStyle = PAINT.legendArc;\n      ctx.lineWidth = PAINT.dimWidth;",
        1,
    ),
    ("      ctx.lineWidth = 0.7;\n      if (e.rwy)", "      ctx.lineWidth = PAINT.dimWidth;\n      if (e.rwy)", 1),
    ('ctx.strokeStyle = "rgba(240, 136, 62, 0.25)";', "ctx.strokeStyle = PAINT.routeHalo;", 1),
    ('PAINT.legendRwy : "#d29922";', "PAINT.legendRwy : PAINT.nodePlain;", 1),
    ("haveAnyHl ? 0.3 : 0.6;", "haveAnyHl ? PAINT.nodeDimAlpha : 0.6;", 1),
    (
        '    ctx.fillStyle = PAINT.textFaint;\n    ctx.font = "10px " + FONT_MONO;',
        "    ctx.fillStyle = PAINT.rwLabel;\n    ctx.font = PAINT.rwLabelFont + FONT_MONO;",
        1,
    ),
]

BASE_PAINT = {
    "bg": "#0c0d10",
    "surface": "#15171a",
    "hairline": "#1f2126",
    "hairlineStrong": "#2a2d33",
    "textFaint": "#828995",
    "accent": "#f0883e",
    "accentSoft": "rgba(240, 136, 62, 0.25)",
    "legendTwy": "#3fb950",
    "legendRwy": "#58a6ff",
    "legendArc": "#8957e5",
    "legendRamp": "#8b949e",
    "legendHs": "#f85149",
    "legendRoute": "#f0883e",
    "edgeTwyHi": "#56d364",
    "edgeRwyHi": "#79c0ff",
    "edgeRampHi": "#aab1bd",
    "arcHi": "#d2a8ff",
    "arcJunctionHi": "#bc8cff",
    "rwSurface": "#16181c",
    "rwSurfaceHl": "#2a2e35",
    "rwCenterline": "#262a31",
    "rwCenterlineHl": "#565d6a",
    "measureRuler": "#e6db74",
    # New keys (baseline values reproduce today's render exactly)
    "dimAlpha": 0.16,
    "dimArcAlpha": 0.16,
    "dimWidth": 0.7,
    "routeHalo": "rgba(240, 136, 62, 0.25)",
    "nodePlain": "#d29922",
    "nodeDimAlpha": 0.3,
    "rwLabel": "#828995",
    "rwLabelFont": "10px ",
}

BASE_ROOT = {
    "--bg": "#0c0d10",
    "--surface": "#15171a",
    "--surface-tint": "rgba(21, 23, 26, 0.92)",
    "--hairline": "#1f2126",
    "--hairline-strong": "#2a2d33",
    "--text": "#edeef1",
    "--text-muted": "#aab1bd",
    "--text-faint": "#828995",
    "--accent": "#f0883e",
    "--accent-soft": "rgba(240, 136, 62, 0.15)",
    "--danger": "#e5534b",
    "--legend-twy": "#3fb950",
    "--legend-rwy": "#58a6ff",
    "--legend-arc": "#8957e5",
    "--legend-ramp": "#8b949e",
    "--legend-hs": "#f85149",
    "--legend-route": "#f0883e",
}

# Each variant: (PAINT overrides, :root overrides). Legend CSS vars track the PAINT legend colours.
VARIANTS = {
    "a": (
        {
            "bg": "#11141a",
            "surface": "#1a1e25",
            "textFaint": "#9aa2ae",
            "rwSurface": "#2c313a",
            "rwSurfaceHl": "#3b424e",
            "rwCenterline": "#77808d",
            "rwCenterlineHl": "#a9b2bf",
            "dimAlpha": 0.6,
            "dimArcAlpha": 0.45,
            "dimWidth": 1.2,
            "nodeDimAlpha": 0.6,
            "rwLabel": "#d4dae2",
            "rwLabelFont": "600 11px ",
        },
        {"--bg": "#11141a", "--surface": "#1a1e25", "--surface-tint": "rgba(26, 30, 37, 0.92)", "--text-faint": "#9aa2ae"},
    ),
    "b": (
        {
            "bg": "#f6f5f0",
            "surface": "#ffffff",
            "hairline": "#dedbd2",
            "hairlineStrong": "#c6c2b7",
            "textFaint": "#5f646c",
            "accent": "#c8007a",
            "accentSoft": "rgba(200, 0, 122, 0.18)",
            "legendTwy": "#8a8f98",
            "legendRwy": "#0b3a8c",
            "legendArc": "#8a8f98",
            "legendRamp": "#b3ada0",
            "legendHs": "#d1242f",
            "legendRoute": "#c8007a",
            "edgeTwyHi": "#1a7f37",
            "edgeRwyHi": "#7cb7ff",
            "edgeRampHi": "#57606a",
            "arcHi": "#8250df",
            "arcJunctionHi": "#6639ba",
            "rwSurface": "#4b4f56",
            "rwSurfaceHl": "#2b2e33",
            "rwCenterline": "#e9eaec",
            "rwCenterlineHl": "#ffffff",
            "measureRuler": "#9a6700",
            "dimAlpha": 1.0,
            "dimArcAlpha": 0.8,
            "dimWidth": 1.3,
            "routeHalo": "rgba(200, 0, 122, 0.22)",
            "nodePlain": "#9a6700",
            "nodeDimAlpha": 0.55,
            "rwLabel": "#111317",
            "rwLabelFont": "700 11px ",
        },
        {
            "--bg": "#f6f5f0",
            "--surface": "#ffffff",
            "--surface-tint": "rgba(255, 255, 255, 0.94)",
            "--hairline": "#dedbd2",
            "--hairline-strong": "#c6c2b7",
            "--text": "#16181c",
            "--text-muted": "#454a52",
            "--text-faint": "#5f646c",
            "--accent": "#c8007a",
            "--accent-soft": "rgba(200, 0, 122, 0.12)",
            "--danger": "#d1242f",
            "--legend-twy": "#8a8f98",
            "--legend-rwy": "#0b3a8c",
            "--legend-arc": "#8a8f98",
            "--legend-ramp": "#b3ada0",
            "--legend-hs": "#d1242f",
            "--legend-route": "#c8007a",
        },
    ),
    "c": (
        {
            "bg": "#3b4048",
            "surface": "#30343b",
            "hairline": "#4a5059",
            "hairlineStrong": "#5a616b",
            "textFaint": "#c3c9d2",
            "accent": "#ff4fc3",
            "accentSoft": "rgba(255, 79, 195, 0.25)",
            "legendTwy": "#e2bd4f",
            "legendRwy": "#8fb8ff",
            "legendArc": "#c9a6ff",
            "legendRamp": "#aab0ba",
            "legendHs": "#ff5f57",
            "legendRoute": "#ff4fc3",
            "edgeTwyHi": "#ffd75e",
            "edgeRwyHi": "#bcd6ff",
            "edgeRampHi": "#dde1e7",
            "arcHi": "#e2cdff",
            "arcJunctionHi": "#d4b8ff",
            "rwSurface": "#24272c",
            "rwSurfaceHl": "#1a1c20",
            "rwCenterline": "#8d95a1",
            "rwCenterlineHl": "#cfd5dd",
            "dimAlpha": 0.7,
            "dimArcAlpha": 0.5,
            "dimWidth": 1.2,
            "routeHalo": "rgba(255, 79, 195, 0.3)",
            "nodePlain": "#ead9a0",
            "nodeDimAlpha": 0.55,
            "rwLabel": "#f1f3f6",
            "rwLabelFont": "600 11px ",
        },
        {
            "--bg": "#3b4048",
            "--surface": "#30343b",
            "--surface-tint": "rgba(44, 48, 55, 0.94)",
            "--hairline": "#4a5059",
            "--hairline-strong": "#5a616b",
            "--text-muted": "#c3c9d2",
            "--text-faint": "#a3aab4",
            "--accent": "#ff4fc3",
            "--accent-soft": "rgba(255, 79, 195, 0.15)",
            "--legend-twy": "#e2bd4f",
            "--legend-rwy": "#8fb8ff",
            "--legend-arc": "#c9a6ff",
            "--legend-ramp": "#aab0ba",
            "--legend-hs": "#ff5f57",
            "--legend-route": "#ff4fc3",
        },
    ),
    "d": (
        {
            "bg": "#1c2027",
            "surface": "#242932",
            "hairline": "#2e343e",
            "hairlineStrong": "#3a414c",
            "textFaint": "#a9b0bb",
            "accent": "#22d3ee",
            "accentSoft": "rgba(34, 211, 238, 0.25)",
            "legendTwy": "#b4bac4",
            "legendRwy": "#7e93ad",
            "legendArc": "#b4bac4",
            "legendRamp": "#737a85",
            "legendHs": "#ff5a52",
            "legendRoute": "#22d3ee",
            "rwSurface": "#3a404a",
            "rwSurfaceHl": "#4a5160",
            "rwCenterline": "#9aa2ae",
            "rwCenterlineHl": "#d0d5dc",
            "dimAlpha": 0.75,
            "dimArcAlpha": 0.5,
            "dimWidth": 1.1,
            "routeHalo": "rgba(34, 211, 238, 0.28)",
            "nodePlain": "#c7ccd4",
            "nodeDimAlpha": 0.45,
            "rwLabel": "#eef0f3",
            "rwLabelFont": "600 11px ",
        },
        {
            "--bg": "#1c2027",
            "--surface": "#242932",
            "--surface-tint": "rgba(36, 41, 50, 0.93)",
            "--hairline": "#2e343e",
            "--hairline-strong": "#3a414c",
            "--text-faint": "#a9b0bb",
            "--accent": "#22d3ee",
            "--accent-soft": "rgba(34, 211, 238, 0.15)",
            "--legend-twy": "#b4bac4",
            "--legend-rwy": "#7e93ad",
            "--legend-arc": "#b4bac4",
            "--legend-ramp": "#737a85",
            "--legend-hs": "#ff5a52",
            "--legend-route": "#22d3ee",
        },
    ),
}


def js_value(v):
    return repr(v) if isinstance(v, float) else f'"{v}"'


def paint_block(paint):
    body = "".join(f"  {k}: {js_value(v)},\n" for k, v in paint.items())
    return "const PAINT = {\n" + body + "};"


def root_block(root):
    return ":root {\n" + "".join(f"  {k}: {v};\n" for k, v in root.items())


def build(html, paint_over, root_over):
    paint = {**BASE_PAINT, **paint_over}
    root = {**BASE_ROOT, **root_over}
    out, n = re.subn(r"const PAINT = \{.*?\n\};", lambda _: paint_block(paint), html, count=1, flags=re.S)
    assert n == 1, "PAINT block not found"
    out, n = re.subn(r":root \{\n(?:  --(?!font)[^\n]*\n)+", lambda _: root_block(root), out, count=1)
    assert n == 1, ":root block not found"
    return out


def main():
    d = Path(sys.argv[1])
    html = (d / "baseline.html").read_text(encoding="utf-8")
    for old, new, count in REROUTE:
        found = html.count(old)
        assert found == count, f"expected {count} of {old!r}, found {found}"
        html = html.replace(old, new)
    # Baseline rebuilt through the same path must reproduce the baseline paint values.
    (d / "baseline-rerouted.html").write_text(build(html, {}, {}), encoding="utf-8", newline="\n")
    for name, (paint_over, root_over) in VARIANTS.items():
        unknown = set(paint_over) - set(BASE_PAINT)
        assert not unknown, f"unknown PAINT keys {unknown}"
        (d / f"variant-{name}.html").write_text(build(html, paint_over, root_over), encoding="utf-8", newline="\n")
        print("wrote", d / f"variant-{name}.html")


if __name__ == "__main__":
    main()
