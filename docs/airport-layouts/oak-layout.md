# OAK Airport Ground Layout Reference

Auto-generated from `oak.geojson` with manual verification. For use in E2E test development.

**Source**: `X:\dev\yaat-server\ArtccResources\ZOA\airports\oak.geojson`
**Parser**: `GeoJsonParser.Parse("OAK", ...)` — parking connects via RAMP edge to nearest taxiway within 0.15nm.

## Verified Taxi Routes (for E2E tests)

Routes verified against `GroundCommandHandler.TryTaxi` with real parsed layout.

| Start | Path | Destination | Result | Notes |
|-------|------|-------------|--------|-------|
| NEW7 parking | `["D"]` | none | Success | RAMP→D, walks south along D |
| NEW7 parking | `["D", "C"]` | none | Success | D south to C junction at lat 37.728 |
| NEW7 parking | `["D", "C", "B", "W"]` | RWY 30 | Success | Full route, destination hold-short for 30 |
| NEW7 parking | `["D"]` | RWY 30 | Fail | D doesn't reach runway 30 threshold |
| D node (lat>37.735) | `["D", "K", "F"]` | none | Success | Crosses runway 15/33, produces hold-shorts |
| NEW7 parking | Pushback facing D | n/a | Success | PushbackPhase created |

### Key Observations
- **D intersects but does not fully cross runway 15/33** — D meets the runway but doesn't continue on the other side. To cross 15/33, aircraft taxi D to the intersection then use F (which does fully cross). This is how `TAXI D F` works — the pathfinder bridges via the runway centerline edge to reach F on the other side.
- **F fully crosses runway 15/33** — use `["D", "K", "F"]` for runway crossing tests.
- **D connects to C at its southern end** (lat 37.728). D does NOT connect to B, W, or any W variants.
- **Route to runway 30 from NEW parking**: D→C→B→W (4 taxiways). No shortcut exists.
- **W variants (W1-W7)** are short connectors off main W, not connections from D.
- **NEW parking** connects via RAMP to D (nearest taxiway, ~0.05nm).
- **OLD parking** connects via RAMP to F (nearest taxiway, ~0.05nm).

## Summary
- **Parking spots**: 168
- **Spot nodes**: 16
- **Taxiways**: 34 (A, B, B1, B2, B3, B5, C, C1, D, E, F, G, H, J, K, L, P, R, R1, S, S1, T, TC, TE, U, V, W, W1, W2, W3, W4, W5, W6, W7)
- **Runways**: 4

## Runways

### 30 - 12
- 9 centerline points
- Lat: 37.701486 to 37.720057
- Lon: -122.242128 to -122.214273
- Endpoint 1 (first designator): lat=37.701486, lon=-122.214273
- Endpoint 2 (second designator): lat=37.720057, lon=-122.242128

### 28L - 10R
- 7 centerline points
- Lat: 37.722263 to 37.728702
- Lon: -122.225918 to -122.206021
- Endpoint 1 (first designator): lat=37.722263, lon=-122.206021
- Endpoint 2 (second designator): lat=37.728702, lon=-122.225918

### 28R - 10L
- 9 centerline points
- Lat: 37.724806 to 37.730462
- Lon: -122.222196 to -122.204721
- Endpoint 1 (first designator): lat=37.724806, lon=-122.204721
- Endpoint 2 (second designator): lat=37.730462, lon=-122.222196

### 15 - 33
- 6 centerline points
- Lat: 37.730598 to 37.740287
- Lon: -122.222826 to -122.219422
- Endpoint 1 (first designator): lat=37.740287, lon=-122.222826
- Endpoint 2 (second designator): lat=37.730598, lon=-122.219422

## Taxiway Connectivity

| Taxiway | Lat Range | Lon Range | Points | Connects To |
|---------|-----------|-----------|--------|-------------|
| A | 37.7234�37.7255 | -122.2054�-122.2034 | 17 | B, C |
| B | 37.7109�37.7258 | -122.2246�-122.2045 | 41 | A, B1, B2, B3, B5, C, R, S, T, V, W |
| B1 | 37.7186�37.7198 | -122.2127�-122.2089 | 21 | B, R |
| B2 | 37.7180�37.7185 | -122.2146�-122.2141 | 2 | B |
| B3 | 37.7170�37.7179 | -122.2194�-122.2165 | 14 | B, B5 |
| B5 | 37.7150�37.7170 | -122.2206�-122.2194 | 14 | B, B3 |
| C | 37.7255�37.7316 | -122.2226�-122.2036 | 23 | A, B, C1, D, E, F, G, H, J |
| C1 | 37.7304�37.7313 | -122.2220�-122.2216 | 2 | C |
| D | 37.7283�37.7401 | -122.2227�-122.2124 | 40 | C, G, H, J, K |
| E | 37.7264�37.7274 | -122.2097�-122.2095 | 4 | C |
| F | 37.7316�37.7402 | -122.2256�-122.2226 | 38 | C, K, L |
| G | 37.7249�37.7286 | -122.2142�-122.2127 | 6 | C, D |
| H | 37.7254�37.7292 | -122.2157�-122.2137 | 5 | C, D |
| J | 37.7287�37.7307 | -122.2258�-122.2158 | 19 | C, D, K, P |
| K | 37.7298�37.7373 | -122.2255�-122.2198 | 23 | D, F, J, L |
| L | 37.7343�37.7351 | -122.2243�-122.2207 | 14 | F, K |
| P | 37.7261�37.7300 | -122.2192�-122.2179 | 5 | J |
| R | 37.7179�37.7200 | -122.2157�-122.2127 | 20 | B, B1, R1 |
| R1 | 37.7195�37.7211 | -122.2178�-122.2148 | 9 | R |
| S | 37.7106�37.7158 | -122.2185�-122.2164 | 28 | B, S1, T |
| S1 | 37.7115�37.7128 | -122.2205�-122.2175 | 13 | S, T |
| T | 37.7088�37.7131 | -122.2222�-122.2159 | 23 | B, S, S1, TC, TE, U, V |
| TC | 37.7088�37.7110 | -122.2159�-122.2138 | 11 | T, TE, U |
| TE | 37.7087�37.7104 | -122.2159�-122.2129 | 16 | T, TC, U |
| U | 37.7066�37.7088 | -122.2182�-122.2159 | 6 | T, TC, TE, W, W3 |
| V | 37.7117�37.7135 | -122.2259�-122.2222 | 11 | B, T, W, W4 |
| W | 37.7041�37.7200 | -122.2393�-122.2145 | 15 | B, U, V, W1, W2, W3, W4, W5, W6, W7 |
| W1 | 37.7016�37.7041 | -122.2145�-122.2136 | 16 | W, W2 |
| W2 | 37.7024�37.7041 | -122.2157�-122.2143 | 11 | W, W1 |
| W3 | 37.7062�37.7066 | -122.2220�-122.2182 | 16 | U, W |
| W4 | 37.7089�37.7117 | -122.2263�-122.2255 | 17 | V, W |
| W5 | 37.7123�37.7150 | -122.2315�-122.2305 | 21 | W |
| W6 | 37.7191�37.7200 | -122.2407�-122.2393 | 9 | W, W7 |
| W7 | 37.7200�37.7209 | -122.2420�-122.2393 | 13 | W, W6 |

## Spot Nodes

| Name | Lat | Lon |
|------|-----|-----|
| 1 | 37.707123 | -122.217646 |
| 2 | 37.713648 | -122.221417 |
| 3 | 37.712771 | -122.222541 |
| 4 | 37.713160 | -122.228022 |
| 5 | 37.715953 | -122.232210 |
| 7 | 37.713316 | -122.223581 |
| 9 | 37.720057 | -122.212125 |
| A8RN | 37.725135 | -122.203463 |
| A8RS | 37.723870 | -122.204109 |
| C | 37.709320 | -122.215396 |
| E | 37.708991 | -122.214853 |
| I30 | 37.702461 | -122.213571 |
| I8R | 37.725692 | -122.204250 |
| K0LN | 37.732121 | -122.225038 |
| K0LS | 37.730751 | -122.225388 |
| W | 37.711012 | -122.218016 |

## Parking Areas

### 1 (1 spots)
- Names: 1
- Lat: 37.712835 to 37.712835
- Lon: -122.216034 to -122.216034
- Heading: 113
- Nearest taxiway: **S** (0.0511nm)

### 10 (1 spots)
- Names: 10
- Lat: 37.710498 to 37.710498
- Lon: -122.215729 to -122.215729
- Heading: 286
- Nearest taxiway: **TC** (0.0549nm)

### 11 (1 spots)
- Names: 11
- Lat: 37.710737 to 37.710737
- Lon: -122.217065 to -122.217065
- Heading: 87
- Nearest taxiway: **S** (0.0479nm)

### 12 (1 spots)
- Names: 12
- Lat: 37.710154 to 37.710154
- Lon: -122.215875 to -122.215875
- Heading: 314
- Nearest taxiway: **TC** (0.0519nm)

### 14 (1 spots)
- Names: 14
- Lat: 37.710018 to 37.710018
- Lon: -122.216266 to -122.216266
- Heading: 348
- Nearest taxiway: **T** (0.0464nm)

### 15 (1 spots)
- Names: 15
- Lat: 37.710404 to 37.710404
- Lon: -122.216973 to -122.216973
- Heading: 53
- Nearest taxiway: **T** (0.0455nm)

### 17 (1 spots)
- Names: 17
- Lat: 37.710222 to 37.710222
- Lon: -122.216637 to -122.216637
- Heading: 13
- Nearest taxiway: **T** (0.0461nm)

### 20 (1 spots)
- Names: 20
- Lat: 37.711752 to 37.711752
- Lon: -122.213820 to -122.213820
- Heading: 360
- Nearest taxiway: **TC** (0.0455nm)

### 21 (1 spots)
- Names: 21
- Lat: 37.711585 to 37.711585
- Lon: -122.213345 to -122.213345
- Heading: 24
- Nearest taxiway: **TC** (0.0422nm)

### 22 (1 spots)
- Names: 22
- Lat: 37.711247 to 37.711247
- Lon: -122.213066 to -122.213066
- Heading: 43
- Nearest taxiway: **TC** (0.0390nm)

### 23 (1 spots)
- Names: 23
- Lat: 37.710866 to 37.710866
- Lon: -122.212911 to -122.212911
- Heading: 47
- Nearest taxiway: **TE** (0.0281nm)

### 24 (1 spots)
- Names: 24
- Lat: 37.710888 to 37.710888
- Lon: -122.212386 to -122.212386
- Heading: 21
- Nearest taxiway: **TE** (0.0369nm)

### 25 (1 spots)
- Names: 25
- Lat: 37.710532 to 37.710532
- Lon: -122.211952 to -122.211952
- Heading: 68
- Nearest taxiway: **TE** (0.0438nm)

### 26 (1 spots)
- Names: 26
- Lat: 37.710069 to 37.710069
- Lon: -122.212365 to -122.212365
- Heading: 115
- Nearest taxiway: **TE** (0.0307nm)

### 27 (1 spots)
- Names: 27
- Lat: 37.709844 to 37.709844
- Lon: -122.212770 to -122.212770
- Heading: 113
- Nearest taxiway: **TE** (0.0336nm)

### 29 (1 spots)
- Names: 29
- Lat: 37.709552 to 37.709552
- Lon: -122.213087 to -122.213087
- Heading: 113
- Nearest taxiway: **TE** (0.0473nm)

### 3 (1 spots)
- Names: 3
- Lat: 37.712045 to 37.712045
- Lon: -122.215268 to -122.215268
- Heading: 113
- Nearest taxiway: **S** (0.0805nm)

### 30 (1 spots)
- Names: 30
- Lat: 37.709265 to 37.709265
- Lon: -122.213399 to -122.213399
- Heading: 113
- Nearest taxiway: **TE** (0.0456nm)

### 31 (1 spots)
- Names: 31
- Lat: 37.708906 to 37.708906
- Lon: -122.213493 to -122.213493
- Heading: 100
- Nearest taxiway: **TE** (0.0562nm)

### 32 (1 spots)
- Names: 32
- Lat: 37.708623 to 37.708623
- Lon: -122.213783 to -122.213783
- Heading: 85
- Nearest taxiway: **TE** (0.0547nm)

### 4 (1 spots)
- Names: 4
- Lat: 37.711569 to 37.711569
- Lon: -122.214407 to -122.214407
- Heading: 293
- Nearest taxiway: **TC** (0.0444nm)

### 5 (1 spots)
- Names: 5
- Lat: 37.711752 to 37.711752
- Lon: -122.215699 to -122.215699
- Heading: 144
- Nearest taxiway: **S** (0.0652nm)

### 6 (1 spots)
- Names: 6
- Lat: 37.711168 to 37.711168
- Lon: -122.214521 to -122.214521
- Heading: 304
- Nearest taxiway: **TC** (0.0347nm)

### 7 (1 spots)
- Names: 7
- Lat: 37.711681 to 37.711681
- Lon: -122.216169 to -122.216169
- Heading: 126
- Nearest taxiway: **S** (0.0469nm)

### 8 (1 spots)
- Names: 8
- Lat: 37.711081 to 37.711081
- Lon: -122.215010 to -122.215010
- Heading: 315
- Nearest taxiway: **TC** (0.0486nm)

### 9 (1 spots)
- Names: 9
- Lat: 37.711352 to 37.711352
- Lon: -122.216521 to -122.216521
- Heading: 126
- Nearest taxiway: **S** (0.0457nm)

### A (6 spots)
- Names: 1A, 3A, 7A, 8A, 9A, A
- Lat: 37.710863 to 37.715750
- Lon: -122.216837 to -122.215414
- Heading: 293
- Nearest taxiway: **S** (0.0382nm)

### ARGUS (1 spots)
- Names: ARGUS
- Lat: 37.734786 to 37.734786
- Lon: -122.216043 to -122.216043
- Heading: 44
- Nearest taxiway: **D** (0.0672nm)

### B (3 spots)
- Names: 1B, 8B, B
- Lat: 37.711050 to 37.715353
- Lon: -122.216462 to -122.215114
- Heading: 325
- Nearest taxiway: **S** (0.0496nm)

### C (2 spots)
- Names: 1C, C
- Lat: 37.713372 to 37.714871
- Lon: -122.215984 to -122.215561
- Heading: 114
- Nearest taxiway: **S** (0.0322nm)

### CARGO (2 spots)
- Names: CARGO1, CARGO2
- Lat: 37.737069 to 37.737580
- Lon: -122.218838 to -122.218485
- Heading: 78
- Nearest taxiway: **D** (0.0467nm)

### CHEVRON (1 spots)
- Names: CHEVRON
- Lat: 37.734780 to 37.734780
- Lon: -122.218645 to -122.218645
- Heading: 110
- Nearest taxiway: **D** (0.0363nm)

### D (2 spots)
- Names: 1D, D
- Lat: 37.713726 to 37.714479
- Lon: -122.215615 to -122.215540
- Heading: 76
- Nearest taxiway: **S** (0.0414nm)

### DHL (2 spots)
- Names: DHL1, DHL2
- Lat: 37.738696 to 37.738986
- Lon: -122.220300 to -122.219918
- Heading: 44
- Nearest taxiway: **D** (0.0405nm)

### E (1 spots)
- Names: E
- Lat: 37.714028 to 37.714028
- Lon: -122.217468 to -122.217468
- Heading: 256
- Nearest taxiway: **S** (0.0423nm)

### F (1 spots)
- Names: F
- Lat: 37.714367 to 37.714367
- Lon: -122.217822 to -122.217822
- Heading: 214
- Nearest taxiway: **S** (0.0413nm)

### FDX (18 spots)
- Names: FDX1, FDX10, FDX11, FDX12, FDX13, FDX14, FDX15, FDX16, FDX17, FDX18, FDX2, FDX3, FDX4, FDX5, FDX6, FDX7, FDX8, FDX9
- Lat: 37.715910 to 37.720740
- Lon: -122.221326 to -122.214840
- Heading: 305
- Nearest taxiway: **B3** (0.0191nm)

### G (1 spots)
- Names: G
- Lat: 37.714741 to 37.714741
- Lon: -122.218310 to -122.218310
- Heading: 215
- Nearest taxiway: **S** (0.0438nm)

### GA (20 spots)
- Names: GA1, GA10, GA11, GA12, GA13, GA14, GA15, GA16, GA17, GA18, GA19, GA2, GA20, GA3, GA4, GA5, GA6, GA7, GA8, GA9
- Lat: 37.727075 to 37.739293
- Lon: -122.224478 to -122.206357
- Heading: 110
- Nearest taxiway: **D** (0.0250nm)

### HELI (3 spots)
- Names: HELI, HELI1, HELI2
- Lat: 37.725780 to 37.727495
- Lon: -122.206828 to -122.202606
- Heading: 290
- Nearest taxiway: **C** (0.0442nm)

### JSX (3 spots)
- Names: JSX1, JSX2, JSX3
- Lat: 37.726386 to 37.726673
- Lon: -122.205590 to -122.204608
- Heading: 290
- Nearest taxiway: **C** (0.0316nm)

### KAI (7 spots)
- Names: KAI1, KAI2, KAI3, KAI4, KAI5, KAI6, KAI7
- Lat: 37.728144 to 37.729027
- Lon: -122.211149 to -122.209200
- Heading: 290
- Nearest taxiway: **C** (0.0502nm)

### MTN (8 spots)
- Names: MTN1, MTN2, MTN3, MTN4, MTN5, MTN6, MTN7, MTN8
- Lat: 37.719307 to 37.719949
- Lon: -122.211050 to -122.208059
- Heading: 313
- Nearest taxiway: **B1** (0.0205nm)

### MTNA (3 spots)
- Names: MTN1A, MTN2A, MTN6A
- Lat: 37.719430 to 37.719839
- Lon: -122.211018 to -122.208458
- Heading: 274
- Nearest taxiway: **B1** (0.0253nm)

### NEW (7 spots)
- Names: NEW1, NEW2, NEW3, NEW4, NEW5, NEW6, NEW7
- Lat: 37.739447 to 37.740346
- Lon: -122.221057 to -122.220085
- Heading: 133
- Nearest taxiway: **D** (0.0512nm)

### OLD (8 spots)
- Names: OLD1, OLD2, OLD3, OLD4, OLD5, OLD6, OLD7, OLD8
- Lat: 37.738936 to 37.740234
- Lon: -122.226136 to -122.225038
- Heading: 7
- Nearest taxiway: **F** (0.0461nm)

### PCM (3 spots)
- Names: PCM1, PCM2, PCM3
- Lat: 37.720364 to 37.720689
- Lon: -122.217653 to -122.217323
- Heading: 80
- Nearest taxiway: **R1** (0.0069nm)

### PT (2 spots)
- Names: PT1, PT2
- Lat: 37.709153 to 37.709444
- Lon: -122.218654 to -122.217972
- Heading: 116
- Nearest taxiway: **T** (0.0466nm)

### PXT (3 spots)
- Names: PXT1, PXT2, PXT3
- Lat: 37.729054 to 37.729913
- Lon: -122.212367 to -122.211610
- Heading: 290
- Nearest taxiway: **D** (0.0670nm)

### R (1 spots)
- Names: R1
- Lat: 37.713294 to 37.713294
- Lon: -122.219854 to -122.219854
- Heading: 319
- Nearest taxiway: **S1** (0.0331nm)

### RON (10 spots)
- Names: RON1, RON10, RON2, RON3, RON4, RON5, RON6, RON7, RON8, RON9
- Lat: 37.708371 to 37.711072
- Lon: -122.221492 to -122.217347
- Heading: 207
- Nearest taxiway: **T** (0.0601nm)

### RONA (1 spots)
- Names: RON5A
- Lat: 37.709656 to 37.709656
- Lon: -122.219150 to -122.219150
- Heading: 207
- Nearest taxiway: **T** (0.0588nm)

### S (13 spots)
- Names: S1, S12, S13, S14, S15, S2, S3, S4, S5, S6, S7, S8, S9
- Lat: 37.711457 to 37.714215
- Lon: -122.220602 to -122.217673
- Heading: 102
- Nearest taxiway: **S1** (0.0096nm)

### SA (1 spots)
- Names: S5A
- Lat: 37.712959 to 37.712959
- Lon: -122.220699 to -122.220699
- Heading: 297
- Nearest taxiway: **T** (0.0389nm)

### SB (2 spots)
- Names: S5B, S8B
- Lat: 37.713182 to 37.713602
- Lon: -122.220715 to -122.219718
- Heading: 125
- Nearest taxiway: **S1** (0.0453nm)

### SIG (7 spots)
- Names: SIG1, SIG2, SIG3, SIG4, SIG5, SIG6, SIG7
- Lat: 37.729256 to 37.730850
- Lon: -122.213439 to -122.212173
- Heading: 290
- Nearest taxiway: **H** (0.0616nm)
