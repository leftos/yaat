# YAAT-1201 measurement: KOAK videomap hold-short bars vs generated hold-short nodes

Projection: equirectangular at lat 37.72, 364567.2 ft/deg (GroundRenderer's constant). Bearings are true, axis angles mod 180.
Videomap candidates: 2-point LineStrings, color #fce028, thickness 2, zIndex 3, 28-121 ft long (the map said 28-120; one bar measures 120.5 ft under this projection). Generated: every RunwayHoldShort node from `LayoutInspector tests/Yaat.Sim.Tests/TestData/oak.geojson --airport-code KOAK --dump` (identical content to the cached `airports/OAK.geojson`).
Pairing: one-to-one, greedy by midpoint-to-node distance, same runway and same side of its centerline, cutoff 200 ft. 'Nearest node' is the unconstrained nearest generated node.

Counts: 35 videomap candidates, 31 generated nodes, 31 pairs, 4 videomap unpaired, 0 generated unpaired.

## Summary by runway (paired bars)

| Runway | Pairs | Paint CL ft min / median / max | Generated CL ft min / max | Paint minus generated ft min / median / max | Pair dist ft median / max |
|---|---|---|---|---|---|
| 15 - 33 | 7 | 157 / 170 / 239 | 174 / 221 | -18 / -5 / +18 | 18 / 27 |
| 28L - 10R | 6 | 253 / 261 / 303 | 250 / 253 | +3 / +11 / +53 | 23 / 65 |
| 28R - 10L | 11 | 246 / 253 / 294 | 222 / 225 | +21 / +28 / +69 | 34 / 89 |
| 30 - 12 | 7 | 277 / 282 / 320 | 249 / 250 | +27 / +32 / +70 | 33 / 126 |

All pairs: pair distance median 29 ft, max 126 ft; 7 pairs over 50 ft. Painted line vs the client's generated tick over 10 deg: V01 24 deg, V09 36 deg, V10 45 deg, V35 50 deg.

## Pairs

| Paint | Node | Runway | Taxiway (node) | Pair dist ft | Paint CL ft (mid / near-far end) | Node CL ft | CL delta ft | Paint len ft | Paint vs twy ° | Paint vs rwy ° | Client tick vs paint ° | True perp vs paint ° |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| V24 | 505 | 28L/10R | J | 4 | 253 (252-253) | 250 | +3 | 83 | 90 (J) | 0 | 0 | 0 |
| V03 | 520 | 15/33 | K | 4 | 172 (165-179) | 175 | -3 | 48 | 90 (K) | 17 | 0 | 0 |
| V15 | 184 | 28L/10R | B | 5 | 258 (257-258) | 253 | +5 | 74 | 90 (B) | 0 | 0 | 0 |
| V02 | 521 | 15/33 | F | 7 | 170 (169-172) | 175 | -5 | 48 | 89 (F) | 4 | 1 | 1 |
| V04 | 519 | 15/33 | K | 9 | 168 (161-175) | 175 | -7 | 49 | 90 (K) | 17 | 0 | 0 |
| V22 | 502 | 28L/10R | G | 12 | 262 (262-262) | 250 | +12 | 32 | 89 (G) | 0 | 1 | 1 |
| V01 | 424 | 15/33 | D | 18 | 158 (157-158) | 174 | -16 | 92 | 82 (D) | 1 | 24 | 24 |
| V07 | 517 | 15/33 | C | 19 | 239 (241-241) | 221 | +18 | 52 | 89 (C) | 37 | 1 | 1 |
| V05 | 522 | 15/33 | L | 19 | 157 (142-171) | 175 | -18 | 49 | 88 (L) | 37 | 2 | 2 |
| V13 | 506 | 28R/10L | B | 21 | 246 (246-246) | 225 | +21 | 73 | 90 (B) | 1 | 0 | 0 |
| V20 | 515 | 28R/10L | H | 25 | 250 (250-250) | 225 | +25 | 30 | 90 (H) | 1 | 0 | 0 |
| V21 | 514 | 28R/10L | G | 26 | 251 (251-251) | 225 | +26 | 31 | 89 (G) | 0 | 1 | 1 |
| V06 | 518 | 15/33 | C | 27 | 193 (177-209) | 175 | +18 | 51 | 90 (C) | 38 | 0 | 0 |
| V08 | 509 | 28R/10L | H | 27 | 252 (252-252) | 225 | +27 | 29 | 89 (H) | 1 | 1 | 1 |
| V25 | 513 | 28R/10L | C1 | 27 | 252 (252-252) | 225 | +27 | 70 | 89 (C1) | 0 | 1 | 1 |
| V29 | 496 | 30/12 | W2 | 29 | 277 (276-277) | 250 | +27 | 89 | 89 (W2) | 1 | 1 | 1 |
| V28 | 495 | 30/12 | W1 | 30 | 278 (277-278) | 250 | +28 | 89 | 90 (W1) | 0 | 0 | 0 |
| V33 | 498 | 30/12 | W7 | 32 | 281 (281-281) | 250 | +31 | 92 | 90 (W7) | 0 | 0 | 0 |
| V32 | 497 | 30/12 | W6 | 33 | 282 (282-282) | 250 | +32 | 92 | 90 (W6) | 0 | 0 | 0 |
| V19 | 503 | 28L/10R | H | 33 | 260 (259-260) | 250 | +10 | 95 | 89 (H) | 1 | 1 | 1 |
| V09 | 508 | 28R/10L | G | 34 | 254 (253-254) | 225 | +29 | 66 | 79 (G) | 1 | 36 | 36 |
| V16 | 501 | 28L/10R | B | 34 | 267 (237-297) | 250 | +17 | 87 | 88 (B) | 44 | 2 | 2 |
| V11 | 507 | 28R/10L | B | 35 | 253 (253-253) | 225 | +28 | 119 | 86 (B) | 0 | 0 | 0 |
| V10 | 510 | 28R/10L | E | 36 | 253 (253-254) | 225 | +28 | 77 | 68 (E) | 0 | 45 | 45 |
| V35 | 374 | 28R/10L | J | 53 | 258 (257-260) | 222 | +37 | 120 | 40 (J) | 2 | 50 | 50 |
| V17 | 504 | 28L/10R | P | 65 | 303 (288-318) | 250 | +53 | 49 | 89 (P) | 39 | 1 | 1 |
| V23 | 512 | 28R/10L | J | 83 | 272 (243-302) | 225 | +48 | 77 | 90 (J) | 51 | 0 | 0 |
| V18 | 516 | 28R/10L | P | 89 | 294 (279-309) | 225 | +69 | 48 | 90 (P) | 38 | 0 | 0 |
| V34 | 499 | 30/12 | W4 | 118 | 310 (279-342) | 250 | +60 | 75 | 87 (W4) | 56 | 5 | 5 |
| V30 | 86 | 30/12 | W3 | 125 | 313 (281-346) | 249 | +65 | 79 | 89 (W3) | 56 | 5 | 7 |
| V31 | 500 | 30/12 | W5 | 126 | 320 (283-357) | 250 | +70 | 88 | 89 (W5) | 57 | 4 | 4 |

## Videomap bars with no pair

| Paint | Nearest runway | CL ft (mid / near-far) | Len ft | Nearest twy (offset ft) | vs twy ° | vs rwy ° | Nearest node (ft) |
|---|---|---|---|---|---|---|---|
| V12 | 28R - 10L | 378 (354-404) | 67 | A (10) | 89 | 0 | 507 (323) |
| V14 | 28R - 10L | 378 (358-400) | 56 | A (2) | 90 | 0 | 506 (330) |
| V26 | 28R - 10L | 1018 (997-1039) | 46 | K (2) | 90 | 9 | 513 (1030) |
| V27 | 28L - 10R | 768 (766-770) | 49 | K (0) | 89 | 15 | 505 (536) |

## Generated nodes (all), with the client's drawn angle

Client tick: perpendicular to node -> first non-RWY neighbour in `layout.Edges` order (`GroundRenderer.ComputeHoldShortAngle`, first edge wins via `ContainsKey`). True perpendicular: perpendicular to the nearest segment of the node's taxiway centerline in the source GeoJSON.

| Node | Runway | Taxiways | CL ft | Neighbour used (edge) | Client tick brg ° | True perp brg ° | Diff ° | Paired paint (dist ft) |
|---|---|---|---|---|---|---|---|---|
| 424 | 15/33 | D | 174 | 423 (D) | 7 | 7 | 0 | V01 (18) |
| 517 | 15/33 | C | 221 | 353 (C) | 22 | 22 | 0 | V07 (19) |
| 518 | 15/33 | C | 175 | 355 (C) | 22 | 22 | 0 | V06 (27) |
| 519 | 15/33 | K | 175 | 1264 (K) | 148 | 148 | 0 | V04 (9) |
| 520 | 15/33 | K | 175 | 1237 (K) | 148 | 148 | 0 | V03 (4) |
| 521 | 15/33 | F | 175 | 478 (F) | 160 | 160 | 0 | V02 (7) |
| 522 | 15/33 | L | 175 | 490 (L) | 20 | 20 | 0 | V05 (19) |
| 184 | 28L/10R | B | 253 | 803 (B) | 112 | 112 | 0 | V15 (5) |
| 501 | 28L/10R | B | 250 | 176 (B) | 158 | 158 | 0 | V16 (34) |
| 502 | 28L/10R | G | 250 | 514 (G) | 112 | 112 | 0 | V22 (12) |
| 503 | 28L/10R | H | 250 | 515 (H) | 112 | 112 | 0 | V19 (33) |
| 504 | 28L/10R | P | 250 | 516 (P) | 74 | 74 | 0 | V17 (65) |
| 505 | 28L/10R | J | 250 | 389 (J) | 112 | 112 | 0 | V24 (4) |
| 374 | 28R/10L | J | 222 | 1131 (J) | 164 | 164 | 0 | V35 (53) |
| 506 | 28R/10L | B | 225 | 186 (B) | 112 | 112 | 0 | V13 (21) |
| 507 | 28R/10L | B | 225 | 188 (B) | 112 | 112 | 0 | V11 (35) |
| 508 | 28R/10L | G | 225 | 360 (G) | 75 | 75 | 0 | V09 (34) |
| 509 | 28R/10L | H | 225 | 1123 (H) | 112 | 112 | 0 | V08 (27) |
| 510 | 28R/10L | E | 225 | 368 (E) | 68 | 68 | 0 | V10 (36) |
| 512 | 28R/10L | J | 225 | 378 (J) | 164 | 164 | 0 | V23 (83) |
| 513 | 28R/10L | C1 | 225 | 1138 (C1) | 112 | 112 | 0 | V25 (27) |
| 514 | 28R/10L | G | 225 | 502 (G) | 112 | 112 | 0 | V21 (26) |
| 515 | 28R/10L | H | 225 | 503 (H) | 112 | 112 | 0 | V20 (25) |
| 516 | 28R/10L | P | 225 | 504 (P) | 74 | 74 | 0 | V18 (89) |
| 86 | 30/12 | W3 | 249 | 85 (W3) | 10 | 13 | 2 | V30 (125) |
| 495 | 30/12 | W1 | 250 | 41 (W1) | 130 | 130 | 0 | V28 (30) |
| 496 | 30/12 | W2 | 250 | 51 (W2) | 130 | 130 | 0 | V29 (29) |
| 497 | 30/12 | W6 | 250 | 59 (W6) | 130 | 130 | 0 | V32 (33) |
| 498 | 30/12 | W7 | 250 | 71 (W7) | 130 | 130 | 0 | V33 (32) |
| 499 | 30/12 | W4 | 250 | 102 (W4) | 68 | 68 | 0 | V34 (118) |
| 500 | 30/12 | W5 | 250 | 105 (W5) | 69 | 69 | 0 | V31 (126) |

## Satellite check (chosen intersections)

Source: the client's SAT layer image, `%LOCALAPPDATA%/yaat/cache/towercab/ZOA/OAK-HighRes.jpg` (9984 x 9984 px, EXIF GPS bottom-left 37.679473,-122.275085 / top-right 37.764201,-122.167969, mapped linearly in lat/lon as `GroundRenderer.DrawBackgroundImage` does): about 3.1 ft per pixel at full resolution; the client downsizes it to 4096 px, about 7.5 ft per pixel.

Real marking: found by `.tmp/yaat-1201/detect.py`, a line search (1 deg, 1 ft steps, +-2.5 ft band) over pixel yellowness above the local median, in a box around the painted bar and the node plus 25 ft; each result checked by eye on the debug crops `.tmp/yaat-1201/det-*.png`. Offsets are perpendicular distances to the detected line; angles are against its bearing.

| Intersection | Real CL ft | Real brg deg | Paint CL ft | Paint off ft | Paint mid to real mid ft | Paint angle deg | Node CL ft | Node off ft | Node to real mid ft | Client tick angle deg | True perp angle deg |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Runway 28R/10L at taxiway E (node 510, painted V10) | 258 | 118 | 253 | 4 | 10 | 5 | 225 | 30 | 45 | 50 | 50 |
| Runway 30/12 at taxiway W5 (node 500, painted V31) | 316 | 74 | 320 | 1 | 6 | 1 | 250 | 128 | 128 | 5 | 5 |
| Runway 28R/10L at taxiway P (node 516, painted V18) | 293 | 76 | 294 | 2 | 3 | 2 | 225 | 87 | 87 | 2 | 2 |
