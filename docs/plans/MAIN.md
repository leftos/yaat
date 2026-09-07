# YAAT plans — index

Entry point for `docs/plans/`. One line per item; the detail lives in the linked subplan. A fresh agent starts at **Current focus**, then **Next up**; **Backlog** is unscheduled. A programme with several subplans has a folder with a `README.md` that tracks its own milestones. Finished plans are deleted, not archived — git history is the record. Issue-specific plans live in [`open-issues/`](./open-issues/) and are deleted once implemented.

## Current focus

- [ ] **Tick-path unification** — [tick-path/README.md](./tick-path/README.md): one tick spine, one action router, one body per behaviour across live, replay and reconstruction. Steps 1–3 and 3d shipped (ADRs 0001–0007); step 4 (relocation) is underway — its first slice shipped 2026-09-06 (attendance as a recorded input; delayed handoffs, auto-accept, point-out auto-ack and the autotrack passes are Sim steps; all twelve oracle baselines empty), **next is sim-time for TDLS / strip timestamps, then strips + TDLS into the snapshot, then shrink `IActionHost`, plus a reconstruct-vs-live-log pin on the 2026-09-06 S2-OAK-4 bundle (the reconstruction went around 5–10 s after the live run; possibly already fixed by 3d-4b)** — [tick-path/04-relocation.md](./tick-path/04-relocation.md). Controller AI waits on this (steer 2026-09-02).

## Next up

- [ ] **Controller AI v1** — on hold behind the tick work; [controller-ai/README.md](./controller-ai/README.md) (H0 / CA0 / CA1 / K1-lite shipped; the open follow-ups are listed there) and the v1 slice [controller-ai/12-milestone-v1-scope.md](./controller-ai/12-milestone-v1-scope.md). The per-frequency radio model ([controller-ai/11-radio-model.md](./controller-ai/11-radio-model.md)) ships first.
- [ ] Pilot AI for solo training — [pilot-ai-self-training/README.md](./pilot-ai-self-training/README.md) (M10.x and Wave 1 shipped; next is Wave 2, M11.2 pilot-initiated requests)
- [ ] Live traffic via SWIM (#150) — [open-issues/150-live-traffic-swim.md](./open-issues/150-live-traffic-swim.md) → yaat-server `docs/plans/live-traffic-swim/08-remaining-work.md`; **blocked** on FAA ADX access to the LADD list
- [ ] Live-session assume UX (ZOA Discord 2026-08-31): bulk-assume modes, snapshot-then-assume-all, auto-assume on first command, snapshot-as-scenario authoring — yaat-server [live-traffic-swim/09-live-sessions.md](../../../yaat-server/docs/plans/live-traffic-swim/09-live-sessions.md) §3
- [ ] CRC protocol support gaps — [crc-protocol-support.md](./crc-protocol-support.md) (13 open of 174; the status table of the CRC hub protocol)
- [ ] vTDLS emulation v1 — [vtdls-emulation.md](./vtdls-emulation.md) (pre-work landed; the PDC flow remains)
- [ ] Test-suite speed follow-ups — [test-suite-speed.md](./test-suite-speed.md) (two profiling items and two stale VSTest filter strings; the TUnit verdict — do not migrate — is recorded there)
- [ ] **Typed command arguments** (steer 2026-09-07) — [typed-command-arguments.md](./typed-command-arguments.md): the registry names each argument's type instead of a prose hint, one small validator per type, arguments bind to record fields by type, and a slot that takes a runway or an altitude is two overloads resolved like a compiler resolves them — the token is tried against each viable overload's declared type (`RunwayArgument`: 1–2 digits + optional L/C/R; `AltitudeArgument`: 3+ digits) — with no combined type; a runway the airport lacks is rejected at dispatch. Step 1 (the pattern modifiers) is in flight; the audit of the remaining slots is step 2

## Backlog

Findings and small items with no subplan:

- [ ] **Reloading a scenario does not reset vStrips state** (reported 2026-09-07): after a reload the strip bays should return to the scenario's starting strips, keeping **only** the separators the user placed; today the previous session's strip state persists. Check `ScenarioLifecycleService` unload/reload vs `FlightStripState` and the Strips client's handling of the reload broadcast; related to the step-4 item "FlightStripState / TdlsState into ScenarioSnapshotDto"
- [ ] Session-clock stragglers (findings 2026-09-07, session-clock survey): `PilotSayBuilder.cs:103` builds a military-route exit-fix estimate as `DateTime.UtcNow + minutes` ("at HHmm") and `StarsTrackSharedState.IsQueriedUntil` is a wall-clock `DateTime?` inside the aircraft snapshot (a replayed query flash expires at the wrong instant) — both take `SimScenarioState.SimTimeUtc`
- [ ] `CAACK` inside a chain or preset silently no-ops (finding 2026-09-05, 3d-3c review): `TrackEngine.Dispatch` returns null for it and `SimulationEngine.DeferredCommands` only branches on `Success == false`, so `FH 090, CAACK` acknowledges nothing — hand `Dispatch` the conflict-alert set
- [ ] `TAXIALL {rwy}` is adjacent-only (finding 2026-09-05, 3d-3b): it builds an empty-path `TaxiCommand`; routing each aircraft as `TAXIAUTO {rwy}` does is one line in `SimulationEngine.TaxiAll` but needs an aviation-review pass on whether "everyone taxi to 28R" implies a route
- [ ] Category-aware fillet floor (`NoseWheelTurnRadiusFt`: 25 ft jet / 18 turboprop / 15 piston) — reroutes ~9 % of jet fillets at OAK/SFO, needs the PathfinderGrid/Nightly sweeps and the SFO J133 (#165) case re-judged; also from that review: the intra-fillet no-surge rule in `ArcProfileLimitKts`, `SpeedProfile` sample count vs missed curvature minima, the <45° straight-cut corner speed gap (`CornerSpeedForAngle` 26 kt at 45° vs ~10 kt over the arc)
- [ ] Design the ASDE-X identity-override rule (override identity, never the track key) — yaat-server `live-traffic-swim/08-remaining-work.md` §5
- [ ] Live session: selecting ZOA > NCT > OAK_APP picks SJC as the primary airport instead of OAK (reported 2026-08-31)
- [ ] Live-traffic follow-ups from the 2026-08-31 aviation review: (a) gate `RunwaySafetyAdvisor.WarnIfTrafficOnFinal` / `WarnIfLiveTrafficOnRunway` on a coasting shadow (7110.65 §5-13-7); (b) validate the three behaviours the receipt-recency fix un-deadened (shadow-vs-simulated conflict alerts, `GroundAcceleration`, assume coast note); (c) surface per-track observation age to the instructor
- [ ] Review the docs structure — user-facing vs internal dev docs (steer 2026-09-02): the root carries USER_GUIDE / COMMANDS / SOLO_TRAINING / GETTING_STARTED / INSTALL beside `docs/`; decide the boundary and where each audience starts
- [ ] Regenerate `docs/scenario-validation-known-failures.md` (last full run 2026-03-12) with yaat-server's `python tools/validate-all-scenarios.py`
- [ ] Hub tests for the non-mentor invite gate (RPO limited access shipped 2026-09-05, plan deleted): an uninvited non-mentor `JoinRoom` is rejected, and a `PullRpo` invite → auto-join round-trips; `TrainingHubAccessHandlerTests` and the kick/invite store tests already cover the rest
- [ ] `RecordedAction` subtype coverage (finding 2026-09-06, 3d-5b-3): nothing round-trips the subtypes through the polymorphic serializer or asserts every subtype has a `[JsonDerivedType]`, so a missing registration surfaces only as a recording-load failure
- [ ] `ReprintDepartureStripAfterAmendment` mints a fresh strip id on replay (finding 2026-09-06, 3d-5b-3), so a rewind across a flight-plan amendment stacks a duplicate departure strip — bake the id onto `RecordedAmendFlightPlan` as `RecordedStripRequest` does
- [ ] `StripMutations.RequestDepartureStripForAircraft` / `BuildDepartureStripFields` still carry optional `displayDestinationAirportIds` parameters (pre-existing; the no-optional-parameters rule)
- [ ] Bounded HOLDP (EFC model) — 7110.65 §4-6-1.c; `HoldingPatternPhase.MaxCircuits` already self-completes, only the HOLDP argument + release path are missing
- [ ] FOLLOWG chain E2E — a two-aircraft test that `FOLLOWG X; CROSS <rwy>` fires the crossing at the hold-short (predicate-level pin exists in `IndefiniteHoldMarkerTests`)
- [ ] Big-file hotspots to keep in mind when planning parallel work: `PatternCommandHandler.cs`, `MainViewModel.cs`, `CommandParser.cs`, `CommandDispatcher.cs`, `MainWindow.axaml.cs`, `GroundCommandHandler.cs` (3,000–4,000 lines each)

Subplans without a schedule:

- [ ] Standalone airport GeoJSON editor — [airport-editor.md](./airport-editor.md) (51 open; not started)
- [ ] Phraseology coverage — [phraseology-coverage-backlog.md](./phraseology-coverage-backlog.md) + [phraseology-implementation.md](./phraseology-implementation.md) (the handoff doc for the rule backlog)
- [ ] "Show nav route" overlay limitations — [nav-route-overlay-followups.md](./nav-route-overlay-followups.md)
- [ ] BEHIND grammar extensions — [behind-grammar-extensions.md](./behind-grammar-extensions.md) (18 open; deferred by decision)

## Blockers

- #150 live traffic: FAA ADX access to the LADD list before the feed can go live on YAAT1.

## Folder map

| Path | What it holds |
|---|---|
| [tick-path/](./tick-path/README.md) | The tick-path unification programme — one file per step, predicted-vs-got per sub-commit |
| [controller-ai/](./controller-ai/README.md) | Controller AI + soak harness — subdesigns 01–12, the milestone table, the open follow-ups |
| [pilot-ai-self-training/](./pilot-ai-self-training/README.md) | Pilot AI for solo training — the milestone table plus the M11–M12 stubs (shipped M10.x subplans are deleted) |
| [open-issues/](./open-issues/) | Plans for open GitHub issues (#150) |
| the loose `*.md` files | Feature subplans and backlogs that are still open — every one is linked above |
