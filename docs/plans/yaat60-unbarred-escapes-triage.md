# YAAT-60 triage: unbarred runway escapes at ATL, FLL, MIA, AUS, LAX, HKY

Read against main 7e3dec00f. Status: resolved (every entry has a measured mechanism; no code changed).

## Baseline and method

- Sweep on main, as briefed: `pwsh tools/gate.ps1 run --log .tmp/yaat60-sweep.log --timeout 600 --slot heavy -- dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj -c Release -- --filter-method "*EveryCommittedLayout_EveryRouteOntoARunwayFromBeyondItsHoldingDistance*"`: passed, 1/1 (routed to the mac builder). The baseline is clean.
- Regenerated list: `KnownUnbarredEscapes` above the YAAT-1915 comment holds 45 lines; 44 are at the six named airports (the 45th, `sea 34R - 16L: J/J - H`, is out of scope). The map's "~50" and the issue's "33" are both stale: the count is 44.
- Rig (scratch, now removed): a console probe built as assembly `Yaat.Sim.Tests` (for Yaat.Sim's InternalsVisibleTo), linking `TestAirportGroundData.cs` and `TestStandLeadIns.cs`, pinned to the committed NavData/CIFP. It replays `FindUnbarredEscapes` with parent tracking, so each escape comes with its route, the runway's bars, and the detector's own Debug/Warning log lines. At these six airports it reproduces exactly the 44 baseline lines and nothing else (none of these airports has an entry in the YAAT-1915 block).
- Node ids below are the harness's (`TestAirportGroundData`, standard fillets). LayoutInspector with `--airport-code` builds the same ids (checked: FLL #107's four edges match).
- "cross" = feet from the runway centreline; "edge+" = feet past the pavement edge; "thr" = the sweep's escape threshold (rule ceiling S + W_twy/2 + 5 ft tolerance).
- vNAS hold-short features: none. All six maps carry only `parking`, `taxiway`, `runway`, `spot` (MIA) feature types (census of each `TestData/<apt>.geojson`). No TowerCabMaps videomap exists for any of the six, so every bar is seated by the rule.

## Classes found

| Class | Entries | Code? |
|---|---|---|
| DATA QUIRK: ATL authored `holdShortDistance` (450 on 8L/26R, 500 on 9R/27L) lies outside the map's own parallel taxiways (A ~392-400 ft, B ~403-435 ft, R ~401-409 ft) | 14 | no (stays listed) |
| DATA QUIRK: HKY runway 1/19 is permanently closed; the vNAS map still draws it | 3 | no (stays listed) |
| TWIN EDGE (two taxiway names drawn on the same vertices give parallel edges between one node pair; the bar splits only one, and `BoundaryCrossings` walks only one name per node pair) | 16 (11 at the bar's edge, 5 that diverge before it) | yes |
| FORK INSIDE THE HOLDING AREA (OTHER): the placement walk passes a junction inside the holding distance and seats the bar on one branch; the other branch leaves outward with no bar | 10 | yes |
| GRAPH-BUILDER CHAIN SLIP (OTHER): `TaxiwayGraphBuilder.InsertNodeInChain` inserts into `NodeIds` but not `Coords`, so a later intersection on the same LineString lands one slot early | 1 | yes |

The briefed classes NAME-CHANGE WALK, DEAD-END FALLBACK, CEILING and CENTRELINE SEAT have no entries; why each was ruled out is under "Hypotheses ruled out".

## Per-entry table

Runway lines: S, thr. ATL 9R/27L S 500.0 (authored; rule 290.3), thr 542.5. ATL 8R/26L S 260.2, thr 302.7. ATL 8L/26R S 450.0 (authored; rule 290.1), thr 492.5. FLL 10L/28R S 250.1, thr 292.6. FLL 10R/28L S 250.7, thr 293.1. HKY 1/19 S 250.0 (no-navdata defaults), thr 292.5. MIA 8R/26L S 280.1 (200 ft wide), thr 322.6. MIA 12/30 S 280.0 (authored; rule 250.1), thr 322.5. MIA 9/27 S 310.0 (authored; rule 250.1), thr 352.5. AUS 18L/36R S 284.9, thr 327.4. LAX 6R/24L S 251.1, thr 293.6.

| # | Entry | Class | Evidence (route, escape node, nearest bar) | Render |
|---|---|---|---|---|
| 1 | `atl 9R - 27L: SJ/SJ - SJ2` | DATA QUIRK (authored S 500) | SC > SC-R arc > R (#1283 402.0) > SJ; escape #1280 1409.1 (edge+1334.1). Nearest 9R bars: SC #699 500.2 (edge+425.2), #698 509.6, both past R. Detector: "dead-end on R ... both endpoints are junctions ... skipping" | - |
| 2 | `atl 9R - 27L: R12` | DATA QUIRK (authored S 500) | From the 27L end #1328 along R (#1325 407.1) > R12; escape #596 596.3 (edge+521.3). Nearest bars N12 #703 508.2, J #702 516.6 | - |
| 3 | `atl 9R - 27L: R6` | DATA QUIRK (authored S 500) | SC > R (402-406) > R6; escape #156 643.3 (edge+568.3). Nearest SC #699 500.2; R7 fallback #690 378.1 kept ("route out of junction #89 passes the holding distance") | - |
| 4 | `atl 9R - 27L: R10` | DATA QUIRK (authored S 500) | R > R10; escape #598 681.1 (edge+606.1). Nearest N12 #703 508.2 | - |
| 5 | `atl 9R - 27L: T/N - T` | DATA QUIRK (authored S 500); a latent FORK underneath | N2 > N2-T arc > T; escape #2885 593.3 (edge+518.3). N2 bar #694 532.4 is 403 ft away; R3 fallback #697 179.9 kept. With the rule's S it becomes `T/T - N2` (#1156 344.3 vs thr 332.8): T leaves N2 at 283-311 ft, a fork | - |
| 6 | `atl 8R - 26L: B2/E1` | GRAPH-BUILDER CHAIN SLIP | E1 #2468 (51.5) > #676 (97.9, "intersection(E1/B2)") > B2 edge 335.7 ft > #409 389.3 (edge+314.3). Bars E1 #746 278.8 and B2 #754 278.9, each 127 ft from #409. Raw B2 LineString runs c2 (=#409) > c3 (=#463, ~279) > c4 (runway); the graph chain runs #409 > #676 > #463, so #676 went into the wrong segment | `.tmp/yaat60/render/atl.html` |
| 7 | `atl 8L - 26R: V` | DATA QUIRK (authored S 450) | H > V-H arc > V; escape #383 605.0 (edge+530.0). H bar #769 466.1 lies past the H/V junction #2466 (372.9) | - |
| 8 | `atl 8L - 26R: A7/A7 - RAMP` | DATA QUIRK (authored S 450) | D > A (395-399) > A7; escape #2608 565.3. D bars #767 451.2, #768 462.6, past A | - |
| 9 | `atl 8L - 26R: B` (#511) | DATA QUIRK (authored S 450) | B15 > B (403-468) > B bends out; escape #511 505.1. B13 fallback #776 397.2 kept; B15 dead-end skipped ("both endpoints are junctions", 408 ft) | - |
| 10 | `atl 8L - 26R: A` (#560) | DATA QUIRK (authored S 450) | A6 > A (397-417); escape #560 499.2. A6 dead-end skipped (485 ft) | - |
| 11 | `atl 8L - 26R: A5/A5 - RAMP` | DATA QUIRK (authored S 450) | D > A > A5; escape #2711 669.3. C fallback #771 372.9 kept | - |
| 12 | `atl 8L - 26R: B2/B - B2` | DATA QUIRK (authored S 450) | H > B (408-410) > B-B2 arc; escape #2565 522.8. B1 fallback #772 385.1 kept | - |
| 13 | `atl 8L - 26R: A3/R20 - A3` | DATA QUIRK (authored S 450) | D > A (392-400) > A3; escape #2801 749.1. Fallbacks C #771 and A4 #777 371.0 kept | - |
| 14 | `atl 8L - 26R: B` (#519) | DATA QUIRK (authored S 450) | H > B; escape #519 514.3. B3 fallback #773 416.8 kept, 188 ft away | - |
| 15 | `atl 8L - 26R: A` (#566) | DATA QUIRK (authored S 450) | D > A, which bends out from 390.8 to 555.4; escape #566 555.4 | - |
| 16 | `fll 10L - 28R: B1/B` | TWIN (at the bar's edge) | #0 > B #72-#73 (157.5 ft), twin of B1 #72-#340-#73; bar B1 #340 250.1; escape #74 364.7 | `.tmp/yaat60/render/fll.html` |
| 17 | `fll 10L - 28R: B12/B` | TWIN (at the bar's edge) | B12 #108-#107 (219.1 ft), twin of B #108-#344-#107; bar #344 250.2; escape #107 331.9 | fll.html |
| 18 | `fll 10L - 28R: A/A8` | TWIN (at the bar's edge) | A8 #619-#37 (176.0 ft), twin of A J38-#332-#37; bar #332 250.1; escape #36 356.2 | fll.html |
| 19 | `fll 10L - 28R: A/A1` | TWIN (at the bar's edge) | A1 #1-#2 (117.8 ft), twin of A #1-#331-#2; bar #331 250.1; escape #512 293.5 (0.9 ft over thr) | fll.html |
| 20 | `fll 10R - 28L: J12/J` | TWIN (at the bar's edge) | J12 #230-#229 (261.3 ft), twin of J #230-#353-#229; bar #353 251.0; escape #229 426.7 | fll.html |
| 21 | `fll 10R - 28L: J4` | FORK (same-name Y) | J4 is one LineString that runs from J to the runway and retraces its stem to a second leg. Stem #283 (39.0) > #282 > fork ~#281 (~140); bar #285 282.3 on one leg; the other leg #280 > #279 > #278 > #277 has no bar; escape #277 331.7 | fll.html |
| 22 | `fll 10R - 28L: J1` | TWIN (diverges before the bar) | J and J1 are twins #202-#1318-#1319-#204 off the runway; J1 is walked (bar #352 252.4 on J1's leg), J never is; J leaves at #204 (163.4) > #209 (265.8) > #205 (290.7) > J1 #1320; escape #1320 304.0 | fll.html (route drawn) |
| 23 | `fll 10R - 28L: J1/J` | TWIN (diverges before the bar) | Same route, then J #205-#1322; escape #1322 326.8 | fll.html |
| 24 | `hky 1 - 19: A3/A3 - B` | DATA QUIRK (closed runway) | A-RWY01/19 arc > A (#173 116.6) > A-A3 arc > #89 (06/24's bar) > A3; escape #177 296.5. The 01/19 bars: A #96 275.5, W fallback #94 127.6 (edge+52.6) | - |
| 25 | `hky 1 - 19: B - RWY06/24` | DATA QUIRK (closed runway) | Same start, then A3 > B > B-RWY06/24 arc; escape #241 315.3 (on 06/24 pavement) | - |
| 26 | `hky 1 - 19: NRAMP` | DATA QUIRK (closed runway) | From the 19 end #44 along B, which runs 197-205 ft off 1/19 (inside S) > NRAMP; escape #41 310.2. B bars #52 199.7 and #55 202.6 are dead-end upgrades | - |
| 27 | `mia 8R - 26L: M1L/M1L - Q1` | TWIN (diverges before the bar) | #320-#319 carries six twins (M1, M1L, N, M, P, Q). Only M1 is walked (bar #574 315.7 on M1 #315-#314). M1L leaves at #319 (122.0); escape #1975 388.0 | `.tmp/yaat60/render/mia.html` |
| 28 | `mia 8R - 26L: N/Q/Q1/M1L` | TWIN (diverges before the bar) | M1L twin > N #319-#321 > M1L > N/Q twins #1974-#324; escape #324 399.4 | mia.html |
| 29 | `mia 8R - 26L: Q1/M1L - Q1` | TWIN (diverges before the bar) | Same as 27, then the M1L-Q1 arc; escape #1982 398.8 | mia.html |
| 30 | `mia 8R - 26L: P/M1/M/PAD` | TWIN (at the bar's edge) | P #315-#314 (280.0 ft), twin of M1 #315-#574-#314; escape #314 357.6 | mia.html |
| 31 | `mia 8R - 26L: M/PAD - M` | TWIN (at the bar's edge) | M #315-#1958 (pre-fillet M #315-#314, a twin of M1); escape #1958 333.8 | mia.html |
| 32 | `mia 8R - 26L: M11/M` | TWIN (at the bar's edge) | M11, N13, M are triplets #255-#256-#257-#258; bar #564 301.5 splits only M11 #258-J259; N13 twin #258-#1653 bypasses; escape #1654 356.5 | mia.html |
| 33 | `mia 8R - 26L: M11/N13/M` | TWIN (at the bar's edge) | Same bypass; escape #260 364.3 | mia.html |
| 34 | `mia 12 - 30: Q1` | FORK (same-name) | Q1 from the 12 end #129 forks at J517 (#2160-#2161, 171-189 ft). Bar #597 311.8 on the leg toward #1982; the other leg #2162 > #2019 (309.8) > #520 (327.7) has no bar; escape #520 327.7 | mia.html (route drawn) |
| 35 | `mia 12 - 30: PAD/PAD - Q1` | FORK | Unbarred Q1 leg > PAD-Q1 arc; escape #2018 442.5 | mia.html |
| 36 | `mia 12 - 30: M1L/M1L - PAD` | FORK | Unbarred Q1 leg > PAD > M1L-PAD arc; escape #1984 400.6 | mia.html |
| 37 | `mia 12 - 30: Q1/PAD - Q1` | FORK | Unbarred Q1 leg > PAD-Q1 arc; escape #2020 355.8 | mia.html |
| 38 | `mia 12 - 30: PAD/Q1` | FORK | Unbarred Q1 leg > PAD; escape #334 338.1 | mia.html |
| 39 | `mia 12 - 30: PAD` | FORK | Unbarred Q1 leg > PAD; escape #2017 351.4 | mia.html |
| 40 | `mia 9 - 27: V/T - V` | FORK (different name), present only because of the authored S 310 | U > U-V arc at J98 (#970 280.1, #971 309.7) > V; U bar #602 335.0 lies past the junction; escape #916 441.0. With the rule's S (250.1) the entry clears | mia.html |
| 41 | `aus 18L - 36R: H/H - A` | FORK (different name) | G from #51 (55.0) > G/H junction #47 275.8 (9 ft inside S) > H; G bar #147 317.1 lies past the junction; escape #288 337.8 | `.tmp/yaat60/render/aus.html` |
| 42 | `aus 18L - 36R: H/G - H/H - G` | FORK (different name) | G > G-H arc #272 > #273; escape #273 356.4 | aus.html |
| 43 | `lax 6R - 24L: E` | TWIN (at the bar's edge) | E6 #377-#1788 (269.0 ft; pre-fillet E6 #377-J186), twin of E #377-#394-J186; bar #394 253.0; escape #187 315.1 | `.tmp/yaat60/render/lax.html` |
| 44 | `lax 6R - 24L: E6` | TWIN (at the bar's edge) | Same bypass; escape #213 330.2 | lax.html |

Counts: DATA QUIRK 17 (ATL 14, HKY 3); TWIN 16; FORK 10; GRAPH-BUILDER 1.

### The data quirks, with the one-line reasons for the ratchet

- ATL (entries 1-5 and 7-15): "vNAS map authors holdShortDistance 500 (9R/27L) / 450 (8L/26R) ft, outside its own parallel taxiways R/A/B at ~400 ft, so the parallels lie inside the holding area." Counterfactual, run on a scratch copy of `atl.geojson` with the three `holdShortDistance` keys removed (S falls to the rule's 290): 8L/26R goes from 9 escapes to 0 and 9R/27L from 5 to 1. The one left is a new line, `T/T - N2`, a fork (class 2 below). The rule value is AC 150/5300-13B's 280 ft (CAT III, 150 ft wide) plus the elevation term. A full-length parallel taxiway inside the hold line cannot be the real layout. Whether YAAT should keep trusting an authored S above the rule is an owner decision (see "Owner decision" below); under the ruling these stay listed.
- HKY (entries 24-26): "runway 1/19 is permanently closed (FAA NPRM 87 FR 65178, 2022, https://thefederalregister.org/documents/2022-22901/proposed-amendment-of-class-d-and-e-airspace-hickory-and-morganton-nc); the pinned NavData has no 01/19 record and the vNAS map still draws it." Measured: `NavigationDatabase.GetRunways("HKY")` returns only 06/24; `RunwayStandoffInputs.Lookup` falls back to width 150, length 5000, no ILS, elevation 0, which gives S 250.

## Code class 1 (do first): TWIN EDGE, 16 entries in two commits

**Root cause.** The vNAS maps draw one taxiway under two or more names on the same vertices (FLL A/A1, A/A8, B/B1, B/B12, J/J12, J/J1; LAX E/E6; MIA M11/N13/M and M1/M1L/N/M/P/Q). `TaxiwayGraphBuilder.BuildEdgesFromTaxiway` deduplicates edges per name only (`src/Yaat.Sim/Data/Airport/TaxiwayGraphBuilder.cs:79-96`), so each name gets its own edge between the same two nodes. The detector then fails twice:

- `BoundaryCrossings` dedups crossings by node pair alone (`src/Yaat.Sim/Data/Airport/RunwayCrossingDetector.cs:261`, `:283`): the first twin in edge order is walked and the others never are. The MIA log has a "Seating" line for M1 and none for M1L, N, M, P or Q at #320-#319.
- Every mint splits only the edge it walked: `SplitEdgeAtOneNode(layout, edge, hsNode)` at `RunwayCrossingDetector.cs:1058` (ProcessBoundaryEdge), `:1738` (InterpolateAndInsert) and `:1863` (DeadEndFallback). The twin keeps the two nodes joined with no bar between them.

Who else depends on this: `PairWithUncalibratedStandoff` and `CandidatesFor` key paint pairs by `(On.Id, Off.Id)` (`:174`, `:330`), so per-name crossings would share one key (OAK is the only videomap airport). `RemoveFallbackBar`'s `halvesIntact` check (`:1942`) expects exactly two halves.

**Minimal fix, part 1 (11 entries: 16-20, 30-33, 43, 44).** Add a helper beside `SplitEdgeAtOneNode` that, after a bar node is minted on edge (a, b), also splits every other non-centreline `GroundEdge` in `layout.Edges` joining a and b (any name) at the same bar node. Call it at the two mint sites in `ProcessBoundaryEdge` (`:1058`) and `InterpolateAndInsert` (`:1738`). Leave `DeadEndFallback` (`:1863`) alone, or record the twin halves in its `SplitEdgeRecord`, so `RemoveFallbackBar` can still restore a dropped fallback. It mints no node: bar ids stay put, and only the fillet stage downstream can change. Do not touch the graph builder's twin creation or the walk.

**Minimal fix, part 2 (5 entries: 22, 23, 27-29).** Key `BoundaryCrossings`' `processed` set by (on, off, taxiway name) instead of the node pair (`:261`, `:283`), so each name is walked along its own edges. In `FindHoldShortInsertionPoint` (`:1404`), stop a walk that reaches an existing bar of the same runway and return that bar, so a name whose path coincides with an already-barred twin seats on the same node instead of minting a second one beside it. Keep the paint-pair dictionaries (`:174`, `:330`) keyed so that two names on one node pair cannot overwrite each other's candidates, for example by adding the name to the key. This part mints new bars at FLL 10R (J, beside J1) and MIA 8R (M1L, N, Q), which renumbers later node ids at both airports.

**Reach beyond YAAT-60.** A scan of every committed layout, for escape routes that step along a straight edge whose two ends are both straight-edge neighbours of a bar of that runway, finds this bypass in at least 9 YAAT-1915 lines: TCL x2, OPF x2, LAF x2, VNY, SAN, LAS. It is a lower bound, because a twin that fillet tangent cuts re-ended (LAX E6, MIA M) escapes the heuristic. The ratchet fails on "No longer seen", so each commit must also remove the YAAT-1915 lines it clears and say so on YAAT-1915.

**Tests at risk.** `RunwayCrossingDetectorTests` (the ratchet, `KnownAngledBarViolations` and `KnownAngledBarInversions`, which must stay empty), `HoldShortBarFlagTests`, `Issue190Mia8RHoldShortTests`, `MiaSpawnReplayCrossTests`, `Issue191CrossRunwayCommandTests`, `Issue192MiaGateEntryTests`, `Issue405ExitDirectionOverrideTests`, `ArrivalParkingPickerTests`, `IssueFllDal880TaxiBacktrackBTests`, `TaxiwayDepartureHeadingTests`, `SingleDigitRunwayCommandTests`, `RunwayDesignatorNormalizationTests`, `AirTaxiDestinationMarkerTests`, `JunctionContinuationTests`, `StateAwarePruningTests`, `GroundArcBezierPlaybackGuardTests`, the FLL smoke pairs (`TaxiCoverageFllTests`, `FilletTaxiCoverageTests`, `TaxiCoverageAcceptanceTests`, `FilletCornerRoutingTests`), and the precompute cache for FLL, LAX, MIA (part 2: FLL, MIA) plus TCL, OPF, LAF, VNY, SAN, LAS. Those are the files that load an FLL, MIA, LAX or AUS layout (Grep for `GetLayout("<apt>")`). None hardcodes a node id at those airports (Grep for `Nodes[\d+]` and `NodeId == \d+`). The only literal-id test, `Issue899DetourConnectorTests` (COS #40 and #248), uses builder ids, which are minted before any bar.

**Red-first tests** (in `RunwayCrossingDetectorTests`, beside `FullOverlap_StaysAtS`):

```csharp
/// <summary>
/// A taxiway drawn twice under two names gives two edges between one pair of nodes; the bar seated on one of them splits
/// the other too, so neither name reaches the runway around it: FLL B12 over B, A1 and A8 over A, J12 over J, LAX E6 over E,
/// MIA N13 over M11 and P over M1.
/// </summary>
[Theory]
[InlineData("FLL", "10L", "B12", 250.2)]
[InlineData("FLL", "10L", "A1", 250.1)]
[InlineData("FLL", "10L", "A8", 250.1)]
[InlineData("FLL", "10R", "J12", 251.0)]
[InlineData("LAX", "6R", "E6", 253.0)]
[InlineData("MIA", "8R", "N13", 301.5)]
[InlineData("MIA", "8R", "P", 315.7)]
public void DetectRunwayCrossings_TwinOfABarredEdge_PassesThroughTheSameBar(string airport, string runwayEnd, string twin, double barFt) =>
    AssertBarAt(airport, runwayEnd, twin, barFt, 0.5);

/// <summary>
/// MIA 8R's first edge off the runway at M1 carries six names (M1, M1L, N, M, P, Q); each name that leaves the shared stretch
/// inside the holding distance is walked and barred on its own, between S (280.1 ft) and S + W/2 (317.6 ft).
/// </summary>
[Theory]
[InlineData("MIA", "8R", "M1L")]
[InlineData("MIA", "8R", "N")]
[InlineData("MIA", "8R", "Q")]
public void DetectRunwayCrossings_TwinBoundaryEdge_EachNameIsWalkedToItsOwnBar(string airport, string runwayEnd, string taxiway) =>
    AssertBarAt(airport, runwayEnd, taxiway, 298.85, 18.75);
```

Why each row fails today, and not tautologically: measured exactly as `AssertBarAt` selects, no bar of that runway has an edge with the row's name (count 0 for all ten rows). The part-1 distance is the existing bar's own cross-track, so a row passes only when the twin runs through that bar. Rows that would pass vacuously are left out: MIA 8R `M` matches six bars through fillet arcs alone, and FLL 10R `J` already has bar #353 at 251.0. Ratchet removals: part 1 removes entries 16-20, 30-33, 43, 44; part 2 removes 22, 23, 27-29 (FLL J's bar is pinned only by the ratchet).

## Code class 2: FORK INSIDE THE HOLDING AREA, 10 entries

**Root cause.** `FindHoldShortInsertionPoint` (`RunwayCrossingDetector.cs:1404-1450`) follows one continuation per node: `PickWalkContinuation` (`:1457-1492`) takes the straightest same-name edge. A junction it passes short of the target therefore seats nothing on its other branches: a second same-name leg (FLL J4, MIA Q1) or another taxiway (AUS H off G, MIA V off U). A branch that never touches the runway has no boundary edge of its own. `DeadEndFallback` and `DropRedundantFallbackBars` handle only a walk that stops at the junction, not one that passes through it.

**Minimal fix.** In `FindHoldShortInsertionPoint`, at each node the walk passes before reaching the target, take every other straight, non-centreline edge leaving that node whose far node is farther from the centreline (not the arrival edge, not the chosen continuation). Seat a bar on each such branch through the same placement (`ProcessBoundaryEdge` on a `Crossing(branchEdge, junction, far)` with the branch's own `CrossingStandoffNm` target), with a visited set against loops. A branch walk that dead-ends or runs out of `HoldShortWalkMaxHops` seats nothing; there is no fallback for side branches. Without that rule, ATL's authored-S runways would gain fallback bars on the parallel taxiways A, B and R. Leave the main-branch bar where it is.

**Entries cleared:** 21 (FLL J4), 34-39 (MIA 12/30 Q1), 40 (MIA 9/27 V), 41-42 (AUS H). Under the rule's S, MIA 12/30's fork still leaves `Q1/PAD - Q1` (#2019 309.8) listed, so the fork does not depend on MIA's authored 280; entry 40 does depend on the authored 310.

**Tests at risk:** the class-1 list for FLL and MIA, plus any test loading the AUS ground layout (the Grep for `GetLayout("AUS")` found none in `tests/`) and `MiaRwy9RolloutExitTests` (MIA 9/27 exits). New bars renumber later node ids at FLL, MIA, AUS and at every other layout with such a fork, so YAAT-1915 lines may also clear. Re-run the precompute cache for each changed airport. `KnownAngledBarViolations` and `KnownAngledBarInversions` must stay empty.

**Red-first test:**

```csharp
/// <summary>
/// A branch that leaves the placement walk inside the holding distance and heads away from the runway gets a bar of its own:
/// FLL J4 and MIA Q1 fork into two legs, MIA V leaves U and AUS H leaves G short of their bars.
/// </summary>
[Theory]
[InlineData("FLL", "10R", "J4", 2)]
[InlineData("MIA", "12", "Q1", 2)]
[InlineData("MIA", "27", "V", 1)]
[InlineData("AUS", "18L", "H", 1)]
public void DetectRunwayCrossings_BranchLeavingTheWalkInsideTheHoldingDistance_GetsItsOwnBar(string airport, string runwayEnd, string branch, int expectedBars)
{
    AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
    Assert.NotNull(layout);

    Assert.Equal(expectedBars, RunwayBars(layout, runwayEnd).Count(bar => RunwayHoldBars.BarTaxiwayName(bar) == branch));
}
```

Today's counts by `BarTaxiwayName`: J4 1 (#285), Q1 1 (#597), V 0, H 0. `AssertBarAt` would pass vacuously for V and H: arcs alone put #602 (335.0) on V and #147 (317.1) on H. Ratchet removals: 21, 34-42.

## Code class 3: GRAPH-BUILDER CHAIN SLIP, 1 entry, highest risk

**Root cause.** `TaxiwayGraphBuilder.InsertNodeInChain` (`src/Yaat.Sim/Data/Airport/TaxiwayGraphBuilder.cs:192-201`) inserts a new intersection node into `NodeIds` but not into `Coords`. `EnsureNodeInChain` (`:171-190`) inserts into both. `FindLineStringIntersections` (`:121-166`) indexes segments by `Coords` position and inserts at `NodeIds[a+1]`, so after one new mid-segment node the chain is a slot behind. ATL B2's segment 0 (c0-c1) is crossed by F2 at t=0.605, a new node. B2's segment 3 (c3-c4) is crossed by E1 at t=0.676, and that node (#676) is inserted between c2 (#409) and c3 (#463) instead of between c3 and c4. The result is the zig-zag #409 > #676 > #463 that lets E1 > B2 skip both bars. `ProcessTaxiway` also hands the feature's own `Coords` list to the chain (`:43`), so `EnsureNodeInChain` mutates the parsed feature.

**Minimal fix.** During `DetectIntersections`, collect each taxiway's new and reused intersection nodes as (segment index, t along the segment, node id), and splice them into `NodeIds` in (segment, t) order after all pairs are processed, without mutating `Coords` mid-loop. Keep `Coords` and `NodeIds` aligned if anything still reads them by index. Do not touch `ProcessTaxiway`'s vertex snapping.

**Entries cleared:** 6, and only together with class-1 part 2. After the builder fix, E1 and B2 share the boundary edge J410-#676, so only one of them would be walked.

**Risk.** The bug reaches graph construction at every airport. A census of every committed layout flags 40 with a builder edge that runs over another node of its own taxiway or lies off its LineString. That is an upper bound: fillet tangent cuts give false positives, such as FLL J4 #281-#285 over #1377. Expect node renumbering, route and budget changes and precompute recomputes across many airports; run the full suite and the Nightly grids.

**Red-first test** (in `GeoJsonParserTests` or beside the sweep). It uses the unfilleted graph so tangent cuts cannot trip it; it is red today, because B2 #409-#676 runs 0.8 ft from #463:

```csharp
/// <summary>
/// Every straight B2 edge at ATL joins two vertices B2's own LineString joins: an intersection lands in the segment it crosses,
/// so no B2 edge runs back over another B2 node (the E1 crossing was put one slot early, between B2's vertices at E1 and at
/// the hold line, when an earlier crossing on B2 had shifted the chain).
/// </summary>
[Fact]
public void TaxiwayGraph_AtlB2_NoEdgeRunsOverAnotherNodeOfItsTaxiway()
{
    AirportGroundLayout? layout = new TestAirportGroundData(FilletMode.None).GetLayout("ATL");
    Assert.NotNull(layout);

    List<GroundEdge> b2 = [.. layout.Edges.Where(edge => !edge.IsRunwayCenterline && (edge.TaxiwayName == "B2"))];
    List<GroundNode> nodes = [.. b2.SelectMany(edge => edge.Nodes).DistinctBy(node => node.Id)];
    foreach (GroundEdge edge in b2)
    {
        var heading = new TrueHeading(GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position));
        foreach (GroundNode node in nodes.Where(node => !edge.HasNode(node.Id)))
        {
            double alongFraction = GeoMath.AlongTrackDistanceNm(node.Position, edge.Nodes[0].Position, heading) / edge.DistanceNm;
            double offFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(node.Position, edge.Nodes[0].Position, heading)) * FeetPerNm;
            Assert.False(
                (alongFraction > 0.05) && (alongFraction < 0.95) && (offFt < 3.0),
                $"B2 #{edge.Nodes[0].Id}-#{edge.Nodes[1].Id} runs over #{node.Id} ({offFt:F1} ft off it)"
            );
        }
    }
}
```

Ratchet removal: 6, landed with class-1 part 2.

## Order, by entries cleared per risk

1. Class 1, part 1 (twin split at the bar): 11 entries plus at least 9 YAAT-1915 lines. No node minted by the change itself; lowest risk.
2. Class 1, part 2 (per-name boundary walks): 5 entries. New bars at FLL and MIA.
3. Class 2 (forks): 10 entries. New bars at FLL, MIA, AUS and wherever else such forks sit.
4. Class 3 (graph builder): 1 entry, needs part 2 as well. Touches graph construction at every airport.

After all three, 17 entries stay listed (ATL 14, HKY 3), each with its one-line reason. The issue's count should be restated as 44 triaged: 27 code, 17 data.

## Owner decision this triage cannot make

ATL's 14 entries (and the latent `T/T - N2` fork under them) are a data quirk under the ruling. They also go away in code if `HoldShortStandoff.BaseSeparationFt` stops trusting an authored `holdShortDistance` that puts the hold line beyond a taxiway drawn parallel to the runway (or above the rule by some margin). The precedence code already overrides an authored value the other way (`WarnWhenRuleOverAuthored`, more than 15 ft below the rule). Measured effect of dropping ATL's authored values: 14 entries clear and 1 fork line appears. Every bar on ATL 10/28, 9R/27L and 8L/26R moves inward from 450/500 to about 290 ft (renumbering ids, and an aviation review owed). MIA's authored 280 (12/30) and 310 (9/27) carry no such contradiction, so the measurement gives no reason to override them.

## Hypotheses ruled out

- NAME-CHANGE WALK (A3/A5/A7 to RAMP, J1 to J, MIA PAD/Q1): the ATL A3/A5/A7 escapes are reached along parallel A inside the authored S, and the rule-S counterfactual clears all 9 on 8L/26R, so no walk stopping at RAMP is involved. FLL J1 to J is the twin dedup: J is never walked. MIA PAD/Q1 sit behind the unbarred Q1 leg.
- DEAD-END FALLBACK: the fallback bars present (ATL R3, R7, R11, C, A4, B1, B3, B13; HKY W #94) are kept with "a route out of junction passes the holding distance", and none of them lies on an escape route. They are a symptom, not a cause.
- CEILING: the smallest overshoot is FLL A/A1 at 0.9 ft over thr, a twin bypass that keeps going outward. All others are 5.2 ft or more over (MIA Q1 #520 +5.2, AUS H +10.4, FLL J1 +10.9). A higher ceiling would hide entries, not fix them.
- CENTRELINE SEAT (HKY): the 01/19 bar #94 sits 127.6 ft from the centreline, 1.2 ft from W node #14, which is 127.5 ft from centreline node #13. The issue comment's "#14 centreline, 0.1 m" describes an older graph, and the runway is closed anyway. The test comment at `tests/Yaat.Sim.Tests/RunwayCrossingDetectorTests.cs:1322` is stale with it.
- The HKY S of 250 as a lookup bug: `GetRunways("HKY")` really has only 06/24; the runway is closed.

## Renders

Interactive LayoutInspector HTML (`--html`), with annotated bars and escapes and one escape route drawn per file: `X:/dev/yaat/.tmp/yaat60/render/fll.html` (twins, J1 route, J4), `lax.html` (E/E6 route), `mia.html` (8R twins, the 12/30 Q1 fork route, 9/27 U/V), `aus.html` (G/H route), `atl.html` (B2/E1 route). The data-quirk entries were not rendered.

## Stale text found

- `RunwayCrossingDetectorTests.cs:1315` and `:1332` say "tracked by YAAT-1214" (Done); these entries belong to YAAT-60.
- `:1321-1323`: the HKY comment's centreline seat (see above).
- YAAT-60's 2026-10-14 comment: the node ids and the centreline claim; also the "33" in the title.
