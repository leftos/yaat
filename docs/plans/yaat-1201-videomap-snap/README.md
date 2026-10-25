# YAAT-1201: snap hold-short points to the painted videomap bars

Design folder for YAAT-1201 (GitHub #1352): the Ground View's MAP and GND layers draw hold-short bars in different places. The owner's rulings are in the issue description; this folder holds the evidence and the code maps the briefs are written from.

## Rulings (owner, recorded on YAAT-1201)

- Snap to the paint: where a painted hold-short bar in the airport's tower-cab videomap pairs with a generated hold point, YAAT's hold point moves onto the painted bar.
- Fix the standoff rule for the intersections with no painted bar, from the aviation review below.
- Feature branch `feat/yaat-1201-videomap-snap` in both repos.
- yaat-server fetches the videomap live from vNAS, cached under `YaatPaths`; the snapped layout is baked into recordings and checkpoints, so a replay never refetches.

## Evidence

- [Mock](../canvases/yaat-1201-holdshort-bars/index.html): three KOAK intersections, five lanes each, over the client's own satellite image. The painted bars sit 1-4 ft from the real markings; YAAT's generated bars sit 30-128 ft off, mostly too close to the runway. YAAT's bar angles match a true perpendicular to the taxiway within 2.2°.
- [KOAK measurement](koak-measurement.md): every painted bar and generated node, their centerline distances, pairing and angles.

## Code maps and review

- [Views map](code-map-views.md): how the MAP layer (videomap, client-only) and the GND layer (server-generated hold-short nodes) are loaded and drawn.
- [Snap map](code-map-snap.md): where the snap sits (`RunwayCrossingDetector.ProcessBoundaryEdge` in `GeoJsonParser.BuildLayout`), the `Parse` signature change, the precompute key, the per-bar standoff the exit logic needs (`IsAtHoldingDistance`), replay, the at-risk tests and the open decisions.
- [Aviation review of the standoff rule](aviation-standoff.md): what the rule approximates (AC 150/5300-13), why OAK's lines sit farther out (angled taxiways, CAT III ILS on 30, an authored 225 ft on 28R), a recommended rule for unpaired intersections, and how to pair a bar safely (the innermost bar on the same taxiway and side; an outer bar is an ILS critical-area hold, left unpaired).
