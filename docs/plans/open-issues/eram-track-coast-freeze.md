# #251 — ERAM track Status: coast (CST) + frozen (FRZN)

Finding D of the ERAM audit. Scope (user): **Full** — coverage-loss coast + disconnect coast + QH freeze + the mandatory correctness rules. Also fold in the STARS coast 5→12 s reconciliation. All aviation-reviewed (aviation-sim-expert, 7110.65 §5-13-7/§5-13-8/§5-2-15).

## Grounded parameters (do not re-guess)
- **Coast duration = 24 s** for ERAM. Source: `vatsim-server-rs radar_state/lib.rs ERAM_VIEW_TIMEOUT_S = 24` (≈2× the ~12 s ARSR sweep); aviation-defensible band 12–60 s. NOT the ASDE-X 45 s (that's the surface Coast/Suspend List, `asdex.md:799`, wrong display) and NOT the terminal 5 s.
- **ERAM coverage floor = field elevation + 1,500 ft AGL** (band 1,000–3,000). AGL over field elev (not MSL, not the ASR ceiling). Higher than the STARS terminal floor because ARSR loses low targets sooner (radar horizon). Reuses the STARS AGL mechanism with a bigger constant.
- **STARS coast 5 → 12 s** (`CrcVisibilityTracker.CoastDurationSeconds`), matching `STARS_VIEW_TIMEOUT_S = 12` (~2 ASR sweeps). Fold-in; update STARS coast-timing tests.

## Semantics (7110.65-grounded)
- **Unify triggers:** coverage-loss and disconnect are indistinguishable to ERAM → same 24 s "no returns" timer. An explicit controller drop (QX) deletes immediately (only *involuntary* target loss coasts). In YAAT, QX releases the track but the aircraft/target remains, so QX is not a coast trigger at all — only aircraft removal (DEL/disconnect) is.
- **Reacquisition (§5-13-8.3):** if the aircraft climbs back above the floor before the coast timer expires → un-coast to Normal, do NOT delete.
- **`IsCorrelated` stays true during coast** (§5-13-8: a coast track is still flight-plan-aided/FLAT). `Status` alone drives the coast symbol. (Free-vs-Flat via IsCorrelated is a separate refinement — leave IsCorrelated=true, out of this issue.)
- **Coast affects target vs track differently:** during coast the raw EramTarget (return) is gone (delete once on entry), but the EramTrack + EramDataBlock persist with `Status=Coasting` (CST in Field E). Above floor = all Normal. Coast expired = all deleted.
- **Coasting AND frozen tracks are excluded from separation/conflict** (§5-13-7 "do not use coast tracks in… separation"): `EramConflictDetector` (STCA) and `SoloTrainingEvaluator` separation must skip them. Their positions are extrapolated/static, not measured.
- **QH freeze (`QH F <location> <FLID>`):** static snapshot at the location, unpaired from the *target* (not the flight plan → IsCorrelated stays true), keeps last altitude/data block, `Status=Frozen`, FRZN in Field E. **Exempt from every auto-removal path** (coast, landing-drop). Unfreeze via re-start-track (TRACK): `Status=Normal`, re-pair to target, resume live position (discard frozen location), re-enable coast. Freeze ≠ hold (hold keeps a *paired* track).

## Implementation steps
1. [x] **STARS 5→12** — `CrcVisibilityTracker.CoastDurationSeconds = 12` + comment cite; STARS coast tests updated.
2. [x] **`AircraftEramState` frozen fields** — `IsFrozen`, `FrozenLat`/`FrozenLon`, `FrozenAltitude`; snapshot round-trip in `AircraftEramStateDto`. Old snapshots deserialize null → not frozen.
3. [x] **`CrcVisibilityTracker.EvaluateEram`** — stateful ERAM eval: floor = field elev + `EramCoverageFloorAglFt` (1500), 200 ft hysteresis; coast ≤ `EramCoastSeconds`=24; reacquisition; frozen short-circuit. New `AircraftCrcState` + `CrcVisibilityResult` fields (EramTargetAction, EramTrackAction, EramCoasting, EramNewlyVisible).
4. [x] **`EvaluateSnapshot`** — decoupled ERAM target/track/datablock from `vis.StarsAction`; initial-data (`BuildTopicData`) EramTargets/Tracks/DataBlocks now use `IsVisibleOnEram`; `Status=Coasting` carried via `batch.EramCoastingCallsigns`.
5. [x] **`ToEramTrack(ac, sector, isCoasting)`** — Status Normal/Coasting/Frozen; frozen renders at frozen location + snapshot altitude; IsCorrelated stays true (FLAT through coast).
6. [x] **QH handler** — `DispatchQh` (`QH F <location> <FLID>`), location via `FrdResolver`; unfreeze on `QT` (`UnfreezeEramTrack`). **TODO:** also unfreeze on the training-hub `TRACK` path (`TrackCommandHandler`).
8. [x] **Conflict exclusion** — `EramConflictDetector` skips `ac.Eram.IsFrozen` (§5-13-7); coasting is excluded implicitly (below coverage floor = out of en-route conflict altitudes). (STARS CA / ATPA / solo-scoring intentionally still see the live aircraft — freeze is ERAM-display-only.)
9. [x] **Tests** — ERAM coast entry/reacquire/expire (4), STARS-vs-ERAM floor split, frozen exemption, ToEramTrack Status (3), QH freeze/unfreeze/unknown-loc (3). Full cross-repo `test-all` green.

## REMAINING
7. [ ] **Disconnect coast** — the second coast trigger. On DEL/disconnect, coast the ERAM track (Status=Coasting from lastState) for 24 s before deleting, mirroring the surface `SurfaceCoastStore` pattern (new ERAM coast store on room state + `BroadcastDisconnectAsync` hook + per-tick expiry sweep). Frozen aircraft delete immediately on removal (no coast). This is arguably the *more common* trigger for a Center trainer (pilot disconnect vs descent below coverage), so it should land before #251 is closed.
- [ ] TrackCommandHandler `TRACK` unfreeze (step 6 TODO).

Delete this file once #251 (incl. the disconnect coast) is implemented.
