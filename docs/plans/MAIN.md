# YAAT plans — index

Entry point for `docs/plans/`. Each row links a subplan; open/done counts are the subplan's own checkboxes at the time of the last edit here.
A fresh agent should start from **Current focus**, then **Next up**, and treat **Backlog** as unscheduled. Finished plans move to
[`archive/`](./archive/); issue-specific plans live in [`open-issues/`](./open-issues/) and are deleted once implemented.

## Current focus (session 2026-08-31 — live-traffic requests, in order)
- [x] **Live-session traffic filters** — implemented + aviation-reviewed 2026-08-31 (awaiting commit); see yaat-server [live-traffic-swim/10-live-session-filters.md](../../../yaat-server/docs/plans/live-traffic-swim/10-live-session-filters.md)
- [x] Aviation-review follow-ups from the filters review — all four closed 2026-08-31 (awaiting commit): (a) no-plan discrete-code shadows now best-guess VFR (`CreateShadow` via `RulesOf`; the filter stays stricter); (b) **no change needed** — `ConflictAlertDetector.IsPairEligible` already excludes shadow↔shadow pairs categorically, so distant-volume CA noise is structurally impossible (shadow↔simulated pairs only exist where the instructor put simulated traffic); (c) SFDPS `initialFlightRules` is only ever IFR/VFR on the wire, **but TAIS `flightRules="E"` is the enroute-hosted IFR plan, not VFR — fixed** (was mislabeling ~105k reports/hr); (d) TAIS plan `<type>` (A/P/E) decides what its single `<airport>` means — now parsed, `P` (proposed departure) maps to Departure instead of Destination
- [x] **Real-world track ownership + controller roster** — implemented 2026-08-31 (awaiting commit): shadows tracked by their real controlling positions (TAIS cps gated by ocr, SFDPS controlling/handoff), pending handoffs display, feed yields silently to TRACK/ASSUME, roster synthesizes "Real World" positions; see yaat-server [live-traffic-swim/11-feed-ownership.md](../../../yaat-server/docs/plans/live-traffic-swim/11-feed-ownership.md)
- [x] Carry TAIS scratchpads onto shadows (question 2026-08-31): `LiveTrack.ScratchPad1/2` → `LiveTrafficSample.Scratchpad1/2` → `AircraftStarsState.Scratchpad1/2` on every sample, under the same feed-yield gate as ownership (empty = feed cleared the pad)
- [x] SWIM plan open questions — 60-min capture processed 2026-08-31; evidence recorded in yaat-server `08-remaining-work.md` §5 (positionTime = radar time; ASDE-X id disagreements are real at ~2 %; 48 % of flights publish from >1 centre; census rarities all zero). Probe promoted to yaat-server `tools/swim-plan-questions.py` (awaiting commit)
- [x] Display-field audit vs vatsim-server-rs / swim-runner-rs (2026-08-31): ranked candidates in yaat-server `08-remaining-work.md` §6; the four top picks wired 2026-08-31 — TAIS `lld` → `GlobalLeaderDirection` (owning-TRACON anti-flap), TAIS `assignedBeaconCode` → FP beacon, cps coarse-vs-specific precedence fix, SFDPS `pointout` → ERAM point-outs; the rest stay unchecked in §6 by value ranking
- [ ] Design the ASDE-X identity-override rule (override identity, never the track key) now that the wire evidence exists — yaat-server `08-remaining-work.md` §5
- [ ] Test-suite speed follow-ups — see [test-suite-speed.md](./test-suite-speed.md) (runner migration, profile, and the first fixes shipped 2026-08-26; pathfinder visited-set, CIFP airport index, and test warm-ups in progress)
- [ ] Pilot AI for solo training — see [pilot-ai-self-training/README.md](./pilot-ai-self-training/README.md) (M10.x readbacks/TTS shipped; M11–M12 subplans are the roadmap)

## Next up
- [ ] Live-session assume UX (ZOA Discord 2026-08-31): bulk-assume modes, snapshot-then-assume-all, auto-assume on first command + restore-to-feed command, snapshot-as-scenario-authoring — see yaat-server [live-traffic-swim/09-live-sessions.md](../../../yaat-server/docs/plans/live-traffic-swim/09-live-sessions.md) §3
- [x] `tools/make-oak-northfield-scenario.py` — gitignored 2026-08-31 (user chose local-only over committing)
- [ ] Live-traffic follow-ups from the 2026-08-31 aviation review (receipt-recency model shipped): (a) gate/qualify `RunwaySafetyAdvisor.WarnIfTrafficOnFinal` / `WarnIfLiveTrafficOnRunway` on a coasting shadow (7110.65 5-13-7); (b) deliberate validation pass over the three behaviors the fix un-deadened (shadow-vs-simulated conflict alerts, `GroundAcceleration`, assume coast note); (c) surface per-track observation age to the instructor (5-1-1 judgment)
- [ ] Live session: selecting ZOA > NCT > OAK_APP picks SJC as the primary airport instead of OAK — debug the position-to-primary-airport resolution (reported 2026-08-31)
- [ ] Live traffic via SWIM (#150) — see [open-issues/150-live-traffic-swim.md](./open-issues/150-live-traffic-swim.md) → yaat-server `docs/plans/live-traffic-swim/08-remaining-work.md` (all slices shipped 2026-08-28; **blocked** on FAA ADX access to the LADD list before the feed can go live on YAAT1, then a first-month soak)
- [ ] RPO limited-access mode + VATUSA ARTCC auto-fill — see [rpo-limited-access-and-vatusa-artcc.md](./rpo-limited-access-and-vatusa-artcc.md) (17 open)
- [ ] CRC protocol support gaps — see [crc-protocol-support.md](./crc-protocol-support.md) (13 open of 174; status table of the CRC hub protocol)
- [ ] Controller AI + soak-testing harness — see [controller-ai/README.md](./controller-ai/README.md) (design complete 2026-09-01, not started; supersedes the old solo-training controller-AI plan)
- [ ] vTDLS emulation v1 — see [vtdls-emulation.md](./vtdls-emulation.md) (pre-work landed; remaining PDC flow)

## Backlog
- [ ] Standalone airport GeoJSON editor — see [airport-editor.md](./airport-editor.md) (51 open; not started)
- [ ] Phraseology coverage — see [phraseology-coverage-backlog.md](./phraseology-coverage-backlog.md) + [phraseology-implementation.md](./phraseology-implementation.md) (handoff doc for the rule backlog)
- [ ] "Show nav route" overlay limitations — see [nav-route-overlay-followups.md](./nav-route-overlay-followups.md)
- [ ] BEHIND grammar extensions — see [open-issues-deferred/behind-grammar-extensions.md](./open-issues-deferred/behind-grammar-extensions.md) (18 open; deferred)
- [ ] Fillet S-turn connectors — see [open-issues/fillet-s-turn-connectors.md](./open-issues/fillet-s-turn-connectors.md) (superseded by the pathfinder fix; optional nicety)
- [ ] Bounded HOLDP (EFC model) — time/circuit-limited holds per 7110.65 §4-6-1.c; `HoldingPatternPhase.MaxCircuits` already self-completes, only the HOLDP argument + release path are missing (from the 2026-08-31 chain-hardening aviation review)
- [ ] Live identity-gate divergence: `TrackCommandHandler.HandleTrackCommand` requires AS identity for inhibit/acknowledge-conflict-alert; `TrackEngine.RequiresIdentity` does not — same live-vs-replay class as the fixed CASUP gap
- [ ] FOLLOWG chain E2E — two-aircraft test proving `FOLLOWG X; CROSS <rwy>` fires the crossing at the hold-short (predicate-level pin exists in `IndefiniteHoldMarkerTests`; regime-C firing covered generically)

## Reference (shipped, kept for context)
- [x] Distance measuring tool — [distance-measuring-tool.md](./distance-measuring-tool.md)
- [x] Favorites identity model — [favorites-identity-model.md](./favorites-identity-model.md)
- [x] STARS behavioral sweep (#372) — [stars-audit.md](./stars-audit.md)
- [x] Command/tick synchronization — [command-tick-synchronization.md](./command-tick-synchronization.md)
- [x] Bug-finding expedition 2026-07 — [bug-hunt-2026-07.md](./bug-hunt-2026-07.md)
- [x] Taxi crossing / hold-short precedence (#172) — [open-issues/172-taxi-crossing-holdshort-and-directionality.md](./open-issues/172-taxi-crossing-holdshort-and-directionality.md)
- [x] Archived: [asdex-safety-logic.md](./archive/asdex-safety-logic.md), [eram-audit.md](./archive/eram-audit.md), [post-physics-ownership-refactor.md](./archive/post-physics-ownership-refactor.md), [controller-ai-solo-training.md](./archive/controller-ai-solo-training.md) (superseded by [controller-ai/](./controller-ai/README.md))

## Blockers
- None recorded.
