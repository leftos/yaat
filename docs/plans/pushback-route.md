# Ground-view push route (`PUSHM`) — what is still open

The feature shipped: `PUSHM`, the "Push route…" draw mode, the dead "Push back, face \<taxiway\>" items, the `TAXI $5A`
ramp-lane cut, and the tug-motion rework (curvature-limited moves with explicit reversals, the flown-path check, the
parked-neighbour outline rule, the client preview drawing the planned path). The design and every rule now live in
[`docs/ground/pushback.md`](../ground/pushback.md); the conflict-detector side in
[`docs/conflict-and-visual-detection.md`](../conflict-and-visual-detection.md); the overlay in
[`docs/ground-rendering.md`](../ground-rendering.md). Landed on the branch as `8e16a348` (2026-09-16) after the
aviation re-review found nothing blocking. The re-review's follow-ups are in [MAIN.md](./MAIN.md) Backlog.

## Remaining steps

- [ ] **Step 3f** (aviation review, user 2026-09-15) — towbar accel/brake for **every** tug move, not just `PUSHM`:
      replace the taxi rates (1.0 kt/s up, 5.0 kt/s brake — 5 kt/s is 0.26 g, a shear-pin event through a towbar)
      with ~0.3 kt/s and ~1 kt/s. AC 00-65A §11.1. This is MAIN.md backlog item (a); expect existing pushback test
      expectations to move and replay recordings to desync. The outline stop's margin (`TugMoveStopMarginFt`) is sized
      on `TaxiDecelRate` and follows the new rate automatically; re-judge the parked-neighbour stop tests' measured
      gaps when it lands.
- [ ] **Step 3g** (aviation review, user 2026-09-15) — gate `PushbackPhase.HasLeftTheStand`'s conflict priority on
      leg 1 of a dispatch push rather than any pushback that has left the stand, so a relocation tow does not outrank
      taxiing traffic for the length of an alley. 7110.65 §3-7-1, §3-7-2.a. Today every move after the stand push-off
      reports `HasLeftTheStand` true.
- [ ] **Step 3h — next up (user 2026-09-16: ship first, fix right after)** — a tug plan that starts with short moves
      stops and crawls at every move boundary: `PushbackPhase.MoveSpeedKts` clamps the last `FinalApproachFt` (10 ft)
      of *every* move to `FinalApproachKts` (1 kt) against that move's own `PlannedEnd`, and `PushbackPhase.OnTick`
      sets `IndicatedAirspeed = 0` when `TugKinematics.IsComplete`, so SFO E6 `PUSH $6B` (65 ft straight, 22 ft
      `TurnTo`, then the reversal dwell into the pull) sits at 1 kt or zero three times in its first 87 ft and needs
      ~35 s to reach 5 kt; D15's long first move hides it. Fix: apply the 1 kt clamp and the stop only at a genuine
      stop — the plan's last move, or a boundary whose next move has `DwellBefore` (the reversal, AC 00-65A §11.14)
      — and carry the current speed across every other boundary. Expect the planner suites' timing expectations and
      `D15ToSixAThenD16` traces to move; needs the aviation review and its own commit. Diagnosis trace:
      `SfoSixAlleyArrivalAheadOfPushTests` run with `PushbackPhase` debug logging (pusher at `gs=1.0kt` at t=10 and 20 s,
      `[Push] PSH1: Push TurnTo moved=15.6ft, gs=1.0kts, toEnd=6.5ft`).
- [ ] **Close-out** — delete this file when 3f, 3g and 3h ship.

**Closed by decision (user, 2026-09-15): free-space cuts stay on ramp lanes.** The T5-family half shipped; the `T`/`A`
half is not being done: those are bare-letter movement-area taxiways, which `IsRampTaxilane` rejects by design and
`CrossesForeignPavement` treats as foreign pavement, and the aviation review's regime split (ramp = ramp control,
movement area = ground control, AIM 4-3-18.a.1) says the two are not interchangeable. Do not re-raise it.
