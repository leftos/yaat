# SFO ground-technique test coverage

Study of the ZOA "SFO GND Fam Video Draft 1" (Michael Bonaga, ZOA EC, 74 min, https://www.youtube.com/watch?v=NZ4jTZwN5gs, transcribed 2026-09-15 from the YouTube auto-captions; the raw timestamped transcript is a local scratch file, `.tmp/yt/transcript.txt`, not committed) against what YAAT can reproduce at KSFO today. Every technique below is paraphrased with its video timestamp and, where the SFO ATCT SOP v1.11 (ZOA reference library, "For Simulation Use Only") states the rule, the SOP section. The SOP outranks the video where they differ.

Companion inventory (2026-09-15): KSFO ground coverage lives entirely in `tests/Yaat.Sim.Tests` (~50 classes: pushback, hold-short binding, A* routing, exits, conflict detection, LUAW; the `TaxiCoverageSfoTests` smoke pairs and the Nightly `TaxiCoverageSfoGridTests` grid). yaat-server has no SFO ground tests. Fixtures: `TestData/sfo.geojson` (shared, re-downloaded), `issue172-sfo.geojson` (pinned), `sfo-gc-scenario.json` (S1-SFO-P GC scenario, 106 aircraft, 28/1), `artcc-zoa-snapshot.json`. Harness: `TestAirportGroundData().GetLayout("SFO")` + `SimulationEngine` + `SendCommand`/`TickOneSecond` (see `docs/test-harness.md`). **Not covered at SFO today:** the explicit `GIVEWAY` verb, departure sequencing/queue order, intersection-departure `28R@E` notation, multi-aircraft ramp choreography, any configuration other than 28/1.

## Layout facts the tests depend on (`sfo.geojson`, LayoutInspector 2026-09-15)

- Taxiways present: A A1 A2 AF AY1–AY4 B B1–B5 BC C C2 C3 CG CZ D E F F1 F2 G GL H K L L2 LF M M1 M2 M3–M5 N P Q Q1 R S S1 S2 S3 SBC SBE SBW T T41E T41W T421–T424 T5 T5A T5B T6 T6A T6B T7 T7A T7B T8 T9 U UB1 UB2 V Y Z Z1 Z2 ZS. Runways 01L/19R 01R/19L 10L/28R 10R/28L. 239 gates (A1T…G14T, SIG1–10, 41-x, 50-x cargo, UB1–5), 33 spots (1–11, 5A/5B/6A/6B/7A/7B, 16, 17, 18, 20–24, 30–35).
- Node ids used in the probes (parking node = `TryTaxi` start node for a gate-parked aircraft): spot 2 = 14, spot 1 = 13, B12 = 948, D15 = 983, D16 = 984, C11 = 964, D5 = 974, E13T = 991, F10 = 1007, G3 = 1042, G1 = 1026, SIG1 = 1044, cargo 50-1 = 1106, spot 5A = 0, 6A = 3, 6B = 5, 16 = 24, 17 = 25, 10 = 11.
- SFO SIDs in `TestData/NavData.dat`: TRUKN2, SNTNA2, SSTIK5, CNDEL5, WESLA5, NIITE4, SAHEY4, MOLEN9, GAPP7, SFO5 (the video's "Santana", "trucking", "GAP 7" are SNTNA2, TRUKN2, GAPP7; DEDHD/GRTFL are TRUKN2 transitions).

### Pathfinder probe results (`--pathfinder <node> … --pf-dest-rwy … --pf-hold-shorts …`)

| Video route | Probe | Result |
|---|---|---|
| Spot 2 `A A1` → 1R (12:28) | p1 | resolves; 1 hold (1R destination) |
| Spot 2 `A F1 B` → 1L (13:02) | p2 | was "Taxiway B does not reach runway 1L" — **fixed 2026-09-15** (connector fold threads the M1 stub; pinned in `SfoVideoRoutePinTests` and `SfoBravoToOneLeftConnectorTests`) |
| Spot 2 `A L F` → 28L HS A1 (16:57) | p3 | resolves; HS A1 explicit + 28L; **no runway crossing** (L passes behind the 1R threshold, F joins 28L south of the 1s) |
| SIG1 `<C` → 28R HS E (18:05) | p4 | resolves; HS E, then 01L/19R + 01R/19L crossings, 28R |
| Cargo 50-1 `C Z B` → 1L HS B1 (20:20) | p5 | was the same "B does not reach 1L" — **fixed 2026-09-15** with p2 |
| G3 super `A Q B F` → 28L HS 1L (24:47) | p6 | resolves; HS 1L explicit, 1R crossing, 28L |
| Spot 2 `A E B Z S` → 10R (47:32) | p8 | resolves; no crossings |
| SIG1 `C C3` → 10L (51:26) | p9 | resolves; `[LI] TIGHT-ARC` 25.7 ft / 3.2 kt at nodes 2317→2314 on C (see findings) |
| Spot 2 `A B1 Z Z1` → 10R (59:54) | p10 | resolves |
| Spot 2 `A E` → 19R (55:58) | p11 | resolves; crosses 10R/28L and 10L/28R |
| Spot 2 `A L` → 1L HS M2 (62:40) | p12 | no route: L does not reach 1L; **no L/M2 junction exists in the layout** |
| B12 `Y A A1` → 1R (05:11) | p13 | resolves from the gate (Y is a taxilane the pathfinder reaches from the B gates) |

## Technique catalogue → proposed tests

Legend: **have** = command surface exists (test is a new pin at SFO); **probe** = route resolves in LayoutInspector, sim behaviour unverified; **gap** = no command surface or the probe failed; **FK** = a facility rule for `KSFO.json` in [controller-ai/10-facility-knowledge.md](./controller-ai/10-facility-knowledge.md) K2, not a sim test.

### A. Ramp and pushback (video 04:00–10:48, 27:02–30:26; SOP 2-1.f, 3-5)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| A1 | Yankee-area gates (B23…C8) push onto taxilane Y instead of A when smaller than a B757 (05:11; SOP 3-5.c) | `PUSH Y A1` from B12 (E75L), then `TAXI Y A A1 1R` | probe (p13 for the taxi) | shipped 2026-09-15: `SfoYankeePushTests` pins the push (plain `PUSH Y` keeps the stand heading; `PUSH Y A1` ends on Y); the taxi-out is finding F-4 (`SfoYankeeTaxiOutPinTests`, skipped red pin). A plain `PUSH Y` is a straight-back push that leaves the nose on the stand heading by design (user decision 2026-09-15) — the RPO names the facing (`PUSH Y A1`, `PUSH Y FACE S`) |
| A2 | B757-or-larger from the same gates pushes onto A (05:43) | `PUSH A` from B12 (B752) | have (`PUSH <twy>`) | same class: ends on A's centreline; `TAXI A A1 1R` starts without a turn-around |
| A3 | Simultaneous tail-to-tail pushes to 5A and 5B (06:52; SOP 3-5.c.i: both usable at once below B757) | C-gate `PUSH $5A` + D-gate `PUSH $5B` issued the same second | have (`Issue233SfoPushToSpotTests` covers one) | shipped 2026-09-15: `SfoSimultaneousAlleyPushTests` — both complete with at most a 10 s in-line yield (lateral bypass along the push direction) |
| A4 | "Push long across the alley" so the arrival gets the near lane (28:44) | D15 `PUSH $6A`; arrival `TAXI T A T6B @D16` | have | shipped 2026-09-15: `SfoSixAlleyChoreographyTests.PushLong_ArrivalNeverSlowed` |
| A5 | Arrival waits on the other lane, turns in when the departure pulls to the spot (28:10) | D15 `PUSH $6B`; arrival `TAXI T A T6A @D16` | have | shipped 2026-09-15 as `.ArrivalWaitsBrieflyForPushOnOtherLane` (D15 → 6B, arrival T6A → E9: D16 hangs off T6B and sits on 6B's lead-in, so the arrival goes to an E-pier gate; a moving pusher abreast costs a bounded hold, a parked one nothing). The video's "waits while the push crosses its lane" case is finding F-6 (`SfoSixAlleyGiveWayWedgeTests`, skipped red pin) |
| A6 | "Push back your discretion, hold short of Alpha" (SOP 3-5.b) | none — no `PUSH … HS A` | **gap** | grammar: a PUSH that stops clear of a named taxiway; until then a plain `PUSH` (≈1.3 lengths) is the stand-in |
| A7 | "Push deeper" onto A so the D junction stays clear for an arrival (29:18) | none — `PUSH A` has no distance/clear-of qualifier | **gap** | grammar candidate `PUSH A CLR D`; note only |
| A8 | Push guaranteed-Alpha gates (C10/C11, D6–D11, E12/E13, F10–F20; 09:42) | `PUSH A` from C11, D8, E13T, F10 | have | rows in `TaxiCoverageData.SfoSmoke` with a push preamble, or a `[Theory]` over the gate list |
| A9 | Supers in/out of the A ramp via spot 1 (video 26:29) vs SOP 3-5.h.i (ADG-VI enter via spot 10; ADG-IV/V for B27 via spot 2) | `TAXI … $10 @G…` / `$2 @B27` | FK | SOP wins; a pilot "unable spot 1" for an A388 is a facility rule |

### B. 28/1 routes (video 12:28–23:05; SOP 3-2.d–f, 3-3, 3-4.b.i, 3-4.c, 4-8.b)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| B1 | Main 1R route, spots 8/9/10 join A → A1 (12:28); hold short of M1 when the 1s are landing (SOP 3-2.f) | `TAXI A A1 1R`, `TAXI A A1 1R HS M1` | probe p1 | pin the route + that `HS M1` binds at the A/M1 junction before A1 |
| B2 | Split 1L departures off via A F1 B (13:02) | `TAXI A F1 B 1L` | **gap (p2)** | investigate: does B carry a 1L bar in the vNAS map, or is 1L full length only via M1 (`B M1 1L`)? If the map is right the readback should say so; if the bar exists, the hold-short matcher misses it |
| B3 | Alpha-Lima-Foxtrot to 28L, hold short of A1 because nothing shields L from a 1R full-length takeoff (16:57; SOP 4-8.b) | `TAXI A L F 28L HS A1` | probe p3 | pin: HS A1 binds on A before L, the route has zero runway crossings, readback names "hold short of alpha one" |
| B4 | West End: cargo/Signature to the 1s via C Z, hold short of the west end (spot 17), then Z and hold short of B1 (19:46–21:27; SOP 3-1.a.vi) | `TAXI T421 C Z B M1 1L HS $17` (#394) then `RES HS B1` | have / probe p5 fails on `B 1L` | extend `Issue394HoldShortSpotTests`: after `RES HS B1` the aircraft stops at Z/B1; `CROSS $17` alone leaves B1 unarmed |
| B5 | FBO departures: mandatory turn direction onto C; to 28R at E hold short of E, not of the runway (18:05; SOP 3-2.d/e) | `TAXI <C HS E RWY 28R` and `TAXI >C Z B M1 1L HS $17` from SIG1 | probe p4 | pin: turn hint honoured (readback "left turn C"), HS E binds at C/E, after `CROSS` the 1L and 1R crossings still hold; the `>` form takes C toward Z |
| B6 | An arrival stopped on an exit clear of 28L has its nose over B; B traffic will not pass (13:36) | arrival on E after landing 28L, no TAXI; departure `TAXI A E B …` or `TAXI B …` through the E junction | have (`GroundConflictDetector`) | pin the B taxier holds short of the junction (or passes only when the wingspan bypass truly clears) — real SFO E/B geometry |
| B7 | Loop arrivals down B, into A at each intersection; B→H→A for Yankee/A ramps; never "swim upstream" on A (14:10–15:19, 31:02) | `TAXI E B H A @B12`, `TAXI E B D A @D16`, `TAXI E A @D16`, `TAXI E B G A @C11` | have | `SfoSmoke` rows from the E/D/T/H/Q exit bars; assert no reversal and ramp entry via the expected lane |
| B8 | 28R at E for north-field departures needs LC coordination for right-turn SIDs; straight-outs allowed without it (SOP 3-4.c.iv/v) | `28R@E` intersection departure display | have (`RunwayEntryPoint`) | pin: SIG1 → `TAXI <C E 28R` shows `28R@E` in the queue suffix; `TAXI <C E 28R` vs full length differ |
| B9 | Multiple runway crossings authorised on K D E L P N C S-chain R; not on H, Q, T (SOP 3-3.b) | `CROSS 1L 1R` on H | FK | warn when a multi-crossing is issued on H/Q/T at SFO |

### C. 28/28 right-turn sequencing (video 32:47–45:17; SOP 3-4.a)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| C1 (shipped 2026-09-15 as `SfoDepartureFunnelTests`; F1 ends at F/L so every F1 form needs `F1 F`; `HS F1@1R` is unsupported — location must be a taxiway — so plain `HS F1`; G crosses 1L before 1R so #2 needs `CROSS 1L`) | Two rivers into one funnel: TRUKN2s staged on inactive 1R short of F1 (via A1, then via G behind), others via A L F to 28L and via A F1 crossing 1L holding short of 1R; release order 3, 1, 4, 2 (36:44–39:41) | #1 `TAXI A A1 1R F1 RWY 28L HS F1`, #2 `TAXI A G 1R F1 RWY 28L HS F1 GIVEWAY #1`, #3 `TAXI A L F 28L HS A1`, #4 `TAXI A F1 RWY 28L CROSS 1L HS 1R`; then #3 `RES`, #1 `FOLLOWG #3`, #4 `CROSS 1R`, #2 `FOLLOWG #4` | have (runway-as-path, `HS`, `GIVEWAY`, `FOLLOWG`, `RunwayDepartureQueue`) | **flagship** `SfoDepartureFunnelTests`: all four routes resolve, #1/#2 hold on 1R pavement short of F1, the 28L queue reads 3-1-4-2, nobody enters 28L, no deadlock at the F1/28L merge |
| C2 | Stash extra TRUKN2s on F (40:14) | `TAXI A F 28L HS 1L` | probe p6 (F crosses 1L then 1R) | row in C1's class: holds at F's 1L bar; `CROSS 1L HS 1R` steps them one runway at a time |
| C3 | Crossing at H: one runway per instruction (35:36; SOP 3-3.b.iii) | `TAXI B H F C CROSS 1L HS 1R RWY 28R` (H does not reach 28R; the 1L/1R crossings are on F) | pinned in `SfoVideoRoutePinTests` | pin: 1L pre-cleared, 1R held; FK: `CROSS 1L 1R` on H warns |
| C4 | Stop sequencing when the runway runs dry; heavies reset a same-SID sequence; group heavies (42:28–43:38) | tower-side (`WakeTurbulenceData`, `REL`) | — | none on the ground; a controller-AI ground-brain rule (04-ground-brain) |
| C5 | Change a TRUKN2 (DEDHD/GRTFL) to SNTNA2 to make a straight-out and fix the sequence (44:11) | flight-plan/SID amendment on the ground | check | verify the amend-route surface (`FP`/route amendment) re-expands the SID runway transition before `CTO`; test at SFO with the two SIDs |
| C6 | LC may exit an arrival onto 1L/1R and hand it to ground (41:19) | `EXIT 1L` after landing 28L | **gap?** | check whether `EXIT`/`EL`/`ER` accept a runway as the exit; if not, add it (runway-as-path already exists for `TAXI`) |

### D. 19/10 southeast (video 45:17–55:22; SOP 3-4.b.iii, 3-3.b.i, 6-1.d.ii.3)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| D1 | Spot 2 departures A E B Z S, split at S: 10L via S1, others 10R via S3 (47:32; SOP 3-4.b.iii.1) | `TAXI A E B Z S S1 10L`, `TAXI A E B Z S S3 10R` | probe p8 | pin both; no crossings; queue suffixes `10L@S1` / `10R@S3` |
| D2 | North field to 10L via C C3 regardless of direction (51:26) | `TAXI C C3 10L` from SIG1 / 50-1 | probe p9 | pin; see finding F-2 (tight arc on C at Signature) |
| D3 | 19L arrivals exit and immediately cross idle 19R; clear the exits fast (46:25) | land 19L, `EXIT G` → auto pull-up short of 19R → `CROSS 19R` → `TAXI B M1 …` | have (documented auto pull-up) | pin at SFO: `Sfo19LExitAutoPullUpTests` for G, F1, H, M |
| D4 | Delay stash on Z C C3 (52:00) | `TAXI Z C C3` (no destination) | have | pin: one-way final taxiway is taxied, the aircraft parks at C3's end |
| D5 | Cross the 10s to Signature at K or D, closest to the threshold (53:06; SOP 3-3.b.i) | `TAXI B K CROSS 10R 10L C @SIG1` | have (`CROSS a b`) | pin the atomic pre-clearance through both bars on K and D |
| D6 | Conditional crossing "behind AAL123 cross 10R, behind SWA456 cross 10L" (53:40; SOP 3-1 phraseology) | `BEHIND AAL123 CROSS 10R` | check | [behind-grammar-extensions.md](./behind-grammar-extensions.md) (deferred by decision) — the condition prefix exists for `GIVEWAY`; verify it gates `CROSS` on a departing aircraft |
| D7 | B1 hot spot: departures join B early via K/Q1/D/T; when in doubt hold everyone short of B1 (49:48) | `TAXI A K B Z S …`, `TAXI A HS B1` | have | rows; pin `HS B1` on A binds at A/B1 for a spot-9 departure |

### E. 19/19 (video 55:22–59:54; SOP 3-4.b.v)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| E1 | Everyone A E to 19R (55:58); the 28s are idle so the preset pre-clears them | `TAXI A E CROSS 28L 28R RWY 19R` | probe p11 | pin the preset form flows through both bars |
| E2 | 19L on request: hold short of 19R on C, LC takes them C L (57:44; SOP 3-4.b.v.1) | `TAXI A E C 19L HS 19R` | have | pin HS 19R binds on C |
| E3 | 28R on request (GAPP7, climb 3,000): C, hold short 19R (58:17) | `TAXI A E C 28R HS 19R` | have | pin |
| E4 | Queue past C → turn onto C then D to rejoin (56:32) | `TAXI E C D 19R` | have | row |

### F. 10/10 (video 59:54–61:00; SOP 3-4.b.iv)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| F1 | Departures B1 Z Z1 to 10R (59:54) | `TAXI A B1 Z Z1 10R` | probe p10 | pin |
| F2 | Cross 19R at S3/S1 when LC wants 10R for arrivals; arrivals cross the 1s past L via P/N/F2 (60:28) | `TAXI Z S3 CROSS 19R …`, `TAXI P CROSS 19L 19R B …` | have | rows |

### G. Flow times and gate hold (video 61:33–69:29; SOP §6, 2-1.f/j)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| G1 | Flow-time aircraft parked on M1, the rest take 1L/1R at M; Lima technique holds short of M2 for >15 min waits (62:05–63:50) | `TAXI A M1 1L`; `TAXI A M 1L` / `TAXI A M 1R`; the L/M2 hold | rows / **gap (p12)** | rows for the M forms; the Lima variant needs the geometry checked — the layout has no L/M2 junction |
| G2 | Release window: off 2 min before / 1 min after; ground delivers ≥5 min prior (62:05; SOP 6-1.g) | `CFR hhmm` (−2/+1 window) | have | none new; `CfrWindowResolver` already matches |
| G3 | Gate hold triggers: queue past G on B (1L) or past M2 on A (1R) in 28/1; past L on F/C in 28/28; past Z1/C3 on Z in 19/10; past B1 on B or Z/R on C in 10/10; past C on E in 19/19 (SOP 6-1.d) | `RunwayDepartureQueue` positions | have | pin: six 1R departures stacked on A read `1R #1…#6`; FK: the per-configuration queue-length trigger |
| G4 | Never hand gate-hold aircraft to ground that cannot push at once; hand them over in runway order (68:20) | scenario/AI behaviour | — | controller-AI ground-brain rule; none |

### H. Supers (video 23:05–27:02; SOP 4-8, 3-5.h)

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| H1 | A388 out of the A ramp: A or B to H, cross at H, 28R by default (23:41) | `TAXI A H F C 28R HS 1R` (A388; H does not reach 28R) | pinned in `SfoVideoRoutePinTests` | pin route; FK: no super on A north of H |
| H2 | A388 out of the G ramp: A to Q/Q1, B, F, hold short 1L (24:47) | `TAXI A Q B F 28L HS 1L` | probe p6 | pin; FK (SOP 4-8.d): a B748/A388 holding between runways closes the runway behind — the runway-occupancy advisory should say so |
| H3 | Super arrival to the A ramp: Q B, hold short F1, then B H A (25:56) | `TAXI Q B HS F1` (F1 never reaches 28L — it ends at F/L), `RES`, `TAXI B H A $1` | first step pinned in `SfoVideoRoutePinTests` | pin the two-step |
| H4 | A380/DC10/MD11/L1011 never depart 1L; no super or 4-engine on A1 (SOP 4-8.a/c) | `TAXI A A1 1L` for A388 | FK | a facility rule producing a pilot "unable" or an instructor warning |

### I. Misc

| # | Technique | YAAT form | Status | Test to add |
|---|---|---|---|---|
| I1 | Verify the hold-short readback names the runway (15:52) | readback of `TAXI … HS 1L` | have | SFO row in the readback tests |
| I2 | Keep stranded arrivals close to the gate: E A G B HS F1 for a D4-bound arrival; reroute 1L departures A G B around it (69:29–71:13) | `TAXI E A G B HS F1`, `TAXI A G B … 1L` | have | two-aircraft row: the stashed arrival never blocks the rerouted departure |
| I3 | Split ground GC1/GC2 by gate range (SOP 2-1.i) | — | — | out of scope (frequency assignment) |

## Findings from the probes (verify before writing the tests)

- **F-1 (fixed 2026-09-15)** `A F1 B 1L` and `C Z B 1L` failed because the 1L bars sit on the M1 and A2 stubs, not on B, and the variant matcher only accepted same-letter connectors. `SegmentExpander.TryRunwayConnectorFallback` now threads a numbered stub ≤ 600 ft off the last taxiway and the echo notes `[via M1 — B reaches 1L through M1]` (`docs/ground/pathfinder.md`).
- **F-2 Tight arc on C at Signature**: `[LI] TIGHT-ARC` radius 25.7 ft / 3.2 kt at nodes 2317→2314 on every route out of SIG1 (p4, p9). Related to the MAIN.md backlog item on C's near-collinear kinks.
- **F-3 No L/M2 junction** in the layout (p12), so the video's Lima flow-time hold cannot be expressed as `HS M2`. Confirm the taxiway naming on the current FAA diagram before treating this as a layout gap.

## Tasks

- [ ] B4: extend `Issue394HoldShortSpotTests` with the `RES HS B1` follow-on after the west-end crossing (F-1 and B2 shipped 2026-09-15)
- [ ] A2 in `SfoYankeePushTests` (A1 shipped 2026-09-15; its taxi-out is F-4)
- [ ] F-6 give-way to a pushback engages too late (found by A4, 2026-09-15): `WouldDeadlock` needs the push tail already inside the mover's lateral envelope, so an arrival rolling down T6A trails a push starting from an E-pier gate to within ~143 ft before it is held; the remaining leg to 6B then passes 125 ft from the held aircraft (< 134.55 ft required) and `PushLegClearanceFt` refuses → both wedge (measured E6 125 ft, E8 66, E10U 23, E12 13). Realistic behaviour: when a started push's remaining leg crosses the mover's route within `PushbackBufferFt`, the mover holds *there* (alley entrance), the push completes, the mover continues. Red pin: `SfoSixAlleyGiveWayWedgeTests` (uncommitted until fixed)
- [ ] F-5 `PUSH $6B` from SFO gates E1 and E2 never reaches `AtParkingPhase` within 400 s with no other traffic (found 2026-09-15 while siting the six-alley pusher; every other E-pier gate parks in 67–151 s). Reproduce with `SfoGroundHarness.SpawnParked` + `PUSH $6B`, dump the tick trajectory (`TickRecorder` → `layout-inspect --ticks`) and file against `docs/ground/pushback.md` spot mode
- [ ] F-4 connector-detour ranking (found by A1, 2026-09-15): `SegmentExpander.TryDetour` keeps the landing with the shortest raw bridge distance, so after `PUSH Y A1` (rest 96 ft south of the Y/AY2 corner, nose 208°) `TAXI Y A A1 1R` bridges through AY2 *behind* the aircraft (180° first hop) although AY3 ahead gives a route 210 ft shorter. Ranking by router cost + tail probe fixes it (pinned red in `Pathfinding/SfoYankeeConnectorChoiceTests`) but from gate G3 the A→Q bridge then bypasses A and the honour check refuses `TAXI A Q B F 28L HS 1L` (pinned green in `SfoDetourHonorsNamedTaxiwayTests`). An honour-the-from-taxiway preference restores G3 but defeats the ramp-lane free-space cut (#396), whose cue is "the resolver failed": measured honouring/cheapest bridge ratios are 1.03 (G3) vs 1.35–1.74 (B20S M4/M5, mid-M3), and "honouring tail resolvable" flips with clearance length on the same gate. The clean discriminator is the cut's own applicability (cleared lane parallel and within `MaxCrossingFt`), so the fix is a contract change: `TryTaxi` prefers a plannable cut when the graph route reaches the cleared lane only beyond the cut cap, the resolver adopts cost+tail (+ honour) ranking, `RampLaneRepositionTests` move to the new contract, and `GroundViewModel.ResolveRemainingRoute` mirrors the trigger. User decision 2026-09-15: backlog (listed in MAIN.md).
- [ ] `Sfo19LExitAutoPullUpTests` (D3)
- [ ] B6 exit-blocks-B conflict pin; I2 two-aircraft stash row
- [ ] `SfoSmoke` rows: A8, B7, D4, E4, F2, G1 (M forms)
- [ ] Check the command surface for C5 (SID amendment on the ground), C6 (`EXIT <runway>`), D6 (`BEHIND x CROSS`) and log each as a gap or a pin
- [ ] Grammar gaps to decide on: A6 (`PUSH … HS A`), A7 (push distance / clear-of), C6
- [ ] FK candidates for `KSFO.json` (controller-ai K2): A9, B9, C3, G3, H1, H2, H4
