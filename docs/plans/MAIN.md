# YAAT plans — index

Entry point for `docs/plans/`. Each row links a subplan; open/done counts are the subplan's own checkboxes at the time of the last edit here.
A fresh agent should start from **Current focus**, then **Next up**, and treat **Backlog** as unscheduled. Finished plans move to
[`archive/`](./archive/); issue-specific plans live in [`open-issues/`](./open-issues/) and are deleted once implemented.

## Current focus (session 2026-08-31 — live-traffic requests, in order)
- [x] **Live-session traffic filters** — implemented + aviation-reviewed 2026-08-31 (awaiting commit); see yaat-server [live-traffic-swim/10-live-session-filters.md](../../../yaat-server/docs/plans/live-traffic-swim/10-live-session-filters.md)
- [ ] Aviation-review follow-ups from the filters review (2026-08-31): (a) three-state flight rules on the shadow itself — `CreateShadow` still stamps a no-plan discrete-code track's datablock/assume path as IFR, mislabeling VFR flight following (§5-2-7.a); (b) gate CA + runway-occupancy advisories to the room's facility volume instead of "whatever shadows exist" (matters once a radius filter watches a distant volume); (c) audit real SFDPS `initialFlightRules` enum values (DVFR/SVFR forms) beyond {VFR}; (d) verify TAIS single-`airport` field semantics for departures out of the sensor's own field
- [ ] **Real-world track ownership + controller roster** (question 2026-08-31) — CRC controller list empty and shadows untracked, while vatsim-server-rs shows feed-owned tracks and handoffs; owner data is already parsed to `LiveFlightPlan.OwnerFacility/OwnerSector` but dropped at `CreateShadow`. Study vatsim-server-rs first; notes in yaat-server [live-traffic-swim/09-live-sessions.md](../../../yaat-server/docs/plans/live-traffic-swim/09-live-sessions.md) §3
- [ ] Carry TAIS scratchpads onto shadows (question 2026-08-31): `scratchPad1`/`scratchPad2` ARE parsed (TaisParser → correlator `Entry.ScratchPad1/2`) but dropped at the `LiveTrack` boundary — add them to `LiveTrack`/`LiveTrafficSample`, set `AircraftStarsState.Scratchpad1/2` on spawn and on sample apply (they change mid-flight)
- [x] SWIM plan open questions — 60-min capture processed 2026-08-31; evidence recorded in yaat-server `08-remaining-work.md` §5 (positionTime = radar time; ASDE-X id disagreements are real at ~2 %; 48 % of flights publish from >1 centre; census rarities all zero). Probe promoted to yaat-server `tools/swim-plan-questions.py` (awaiting commit)
- [ ] Design the ASDE-X identity-override rule (override identity, never the track key) now that the wire evidence exists — yaat-server `08-remaining-work.md` §5
- [ ] Test-suite speed follow-ups — see [test-suite-speed.md](./test-suite-speed.md) (runner migration, profile, and the first fixes shipped 2026-08-26; pathfinder visited-set, CIFP airport index, and test warm-ups in progress)
- [ ] Pilot AI for solo training — see [pilot-ai-self-training/README.md](./pilot-ai-self-training/README.md) (M10.x readbacks/TTS shipped; M11–M12 subplans are the roadmap)

## Next up
- [ ] Live-session assume UX (ZOA Discord 2026-08-31): bulk-assume modes, snapshot-then-assume-all, auto-assume on first command + restore-to-feed command, snapshot-as-scenario-authoring — see yaat-server [live-traffic-swim/09-live-sessions.md](../../../yaat-server/docs/plans/live-traffic-swim/09-live-sessions.md) §3
- [ ] Commit `tools/make-oak-northfield-scenario.py` (untracked; excluded from the live-traffic commits as unrelated)
- [ ] Live-traffic follow-ups from the 2026-08-31 aviation review (receipt-recency model shipped): (a) gate/qualify `RunwaySafetyAdvisor.WarnIfTrafficOnFinal` / `WarnIfLiveTrafficOnRunway` on a coasting shadow (7110.65 5-13-7); (b) deliberate validation pass over the three behaviors the fix un-deadened (shadow-vs-simulated conflict alerts, `GroundAcceleration`, assume coast note); (c) surface per-track observation age to the instructor (5-1-1 judgment)
- [ ] Live session: selecting ZOA > NCT > OAK_APP picks SJC as the primary airport instead of OAK — debug the position-to-primary-airport resolution (reported 2026-08-31)
- [ ] Live traffic via SWIM (#150) — see [open-issues/150-live-traffic-swim.md](./open-issues/150-live-traffic-swim.md) → yaat-server `docs/plans/live-traffic-swim/08-remaining-work.md` (all slices shipped 2026-08-28; **blocked** on FAA ADX access to the LADD list before the feed can go live on YAAT1, then a first-month soak)
- [ ] RPO limited-access mode + VATUSA ARTCC auto-fill — see [rpo-limited-access-and-vatusa-artcc.md](./rpo-limited-access-and-vatusa-artcc.md) (17 open)
- [ ] CRC protocol support gaps — see [crc-protocol-support.md](./crc-protocol-support.md) (13 open of 174; status table of the CRC hub protocol)
- [ ] Controller AI for solo training — see [controller-ai.md](./controller-ai.md) (design plan, not started)
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
- [x] Archived: [asdex-safety-logic.md](./archive/asdex-safety-logic.md), [eram-audit.md](./archive/eram-audit.md), [post-physics-ownership-refactor.md](./archive/post-physics-ownership-refactor.md)

## Blockers
- None recorded.
