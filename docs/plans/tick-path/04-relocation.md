# Step 4 — relocate tick-reachable ATC logic into `Yaat.Sim`

Part of [tick-path unification](./README.md). The first slice — attendance as a recorded input, then the track-automation bodies (delayed handoffs, auto-accept, point-out auto-ack, the autotrack passes) — is planned in [04a-attendance-and-track-automation.md](./04a-attendance-and-track-automation.md) (approved 2026-09-06; sub-commits A/B/C, predicted-vs-got recorded there as each lands). The checkboxes below are the scope as designed.

- [ ] 4. Relocate tick-reachable ATC logic into `Yaat.Sim`; TDLS and coast as sim core + wire projection; attendance as a recorded input; server session-persistence DTOs collapse into the Sim snapshot (ADR 0003)
  - [ ] Attendance as a recorded input (`PositionRegistry.IsPositionAttended`/`IsTcpControlledByCrc` gate `ProcessAutoAccept`, `ProcessPointoutAutoAck` and the delayed-handoff consolidation redirect — `TickProcessor.cs:1050, 1189, 1274`; a temp reconstruction room has zero attendance, a same-room rewind sees today's)
  - [ ] Sim-time, not `DateTime.UtcNow`, for `TdlsItemRecord.CreatedUtc/SentUtc/WilcoUtc/ExpiresUtc` (`TdlsCommandHandler.cs:80,139,142,190`, `TickProcessor.cs:282,293,340`) and the strip PDT/ETA text (`StripMutations.cs:878-903`) — every rewound PDC and strip shows the reconstruction's clock today
  - [ ] `FlightStripState` / `TdlsState` into `ScenarioSnapshotDto`, retiring `RoomStateSnapshotMapper`'s duplicate (today rewind never restores either; only session persistence does)
  - [ ] Shrink `IActionHost` as each host-slot body (strips, TDLS, coordination, ASDE-X/SAID, bookmarks) crosses — the action path's step-4 debt, same shape as `IHostSteps`
