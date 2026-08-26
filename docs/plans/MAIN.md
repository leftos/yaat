# YAAT plans — index

Entry point for `docs/plans/`. Each row links a subplan; open/done counts are the subplan's own checkboxes at the time of the last edit here.
A fresh agent should start from **Current focus**, then **Next up**, and treat **Backlog** as unscheduled. Finished plans move to
[`archive/`](./archive/); issue-specific plans live in [`open-issues/`](./open-issues/) and are deleted once implemented.

## Current focus
- [ ] Test-suite speed follow-ups — see [test-suite-speed.md](./test-suite-speed.md) (runner migration, profile, and the first fixes shipped 2026-08-26; pathfinder visited-set, CIFP airport index, and test warm-ups in progress)
- [ ] Pilot AI for solo training — see [pilot-ai-self-training/README.md](./pilot-ai-self-training/README.md) (M10.x readbacks/TTS shipped; M11–M12 subplans are the roadmap)

## Next up
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
