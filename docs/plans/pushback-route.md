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
- [ ] **Close-out** — delete this file when 3f and 3g ship.

**Closed by decision (user, 2026-09-15): free-space cuts stay on ramp lanes.** The T5-family half shipped; the `T`/`A`
half is not being done: those are bare-letter movement-area taxiways, which `IsRampTaxilane` rejects by design and
`CrossesForeignPavement` treats as foreign pavement, and the aviation review's regime split (ramp = ramp control,
movement area = ground control, AIM 4-3-18.a.1) says the two are not interchangeable. Do not re-raise it.
