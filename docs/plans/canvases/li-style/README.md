# LayoutInspector palette options (YAAT-1338): record

The settled record of the mock that chose LayoutInspector's HTML render style. Its page and screenshots (1.4 MB) are not committed; the recipe below rebuilds them.

## Question

LayoutInspector's HTML render drew everything not highlighted so dark that taxiways, runways, nodes and labels receded into the background. Which palette should it use?

Every lane showed KOAK with the auto-route from KAI1 (the north-field Kaiser ramp) along C and B to the 28L full-length hold short, plus node #184 highlighted, which puts the page in its "something is highlighted" state. Two framings at 1600×1000: an overview (zoom 2.7×) and a close-up (zoom 12×) of the C / ramp junction beside the KAI ramp. Only colours, opacities, stroke weights and the runway-label weight differed between lanes.

## Options

- **Baseline (as it was)**: unhighlighted edges at 16% opacity and 0.7 px; runway surfaces `#16181c` barely lighter than the background `#0c0d10`; runway labels `#828995` at 10 px.
- **A. Lifted dark**: the same hues on a slightly lifted dark background; unhighlighted edges at 60% and 1.2 px, runways a visible grey band with a light centerline, labels bold and near-white; the orange route unchanged.
- **B. Light chart**: a paper background like an FAA airport diagram; grey taxiway lines at full opacity, dark-asphalt runways with a white centerline, black bold labels, a magenta route.
- **C. Muted mid-tone**: a mid-grey background with darker runways; a hue per feature (amber taxiways, pale-blue runway edges, grey ramp, lilac arcs, red hold shorts) and a magenta route.
- **D. Neutral base, colour means highlight**: a graphite background with the whole unhighlighted layout in neutral greys at 75%; colour only for meaning: the cyan route, red hold shorts, the green highlighted set.

## Answer

The owner chose **D**, and asked for taxiway names drawn along the taxiways on the map. The values are settled in YAAT-1338's description and implemented in `tools/Yaat.LayoutInspector/inspector.js` (`PAINT`) and `inspector.css` (`:root`).

## Rebuild recipe

From the repo root, with the scripts beside this file:

1. Render the baseline with the pre-YAAT-1338 inspector (check out the commit before YAAT-1338 landed): `dotnet run --project tools/Yaat.LayoutInspector -c Release -- tests/Yaat.Sim.Tests/TestData/oak.geojson --html .tmp/mocks/li-style/baseline.html --html-node 184 --html-route 638,1101,1096,1094,342,1093,1090,341,1089,1082,1081,340,1079,1080,1072,1071,339,1069,1070,1067,338,1066,812,193,192,191,190,189,188,507,809,187,808,506,186,806,185,805,184` (run it through `tools/gate.ps1`).
2. Build the variants: `uv run --no-project python docs/plans/canvases/li-style/make_variants.py .tmp/mocks/li-style` (writes `variant-a.html` … `variant-d.html`).
3. Shoot both framings: `node docs/plans/canvases/li-style/shoot.cjs .tmp/mocks/li-style baseline variant-a variant-b variant-c variant-d` (Playwright from `~/.claude/tools/mock-three/node_modules`).
