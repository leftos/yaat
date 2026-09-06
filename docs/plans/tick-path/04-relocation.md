# Step 4 — relocate tick-reachable ATC logic into `Yaat.Sim`

Part of [tick-path unification](./README.md). Not started; the checkboxes below are the scope as designed.

- [ ] 4. Relocate tick-reachable ATC logic into `Yaat.Sim`; TDLS and coast as sim core + wire projection; attendance as a recorded input; server session-persistence DTOs collapse into the Sim snapshot (ADR 0003)
  - [ ] Attendance as a recorded input (`PositionRegistry.IsPositionAttended`/`IsTcpControlledByCrc` gate `ProcessAutoAccept`, `ProcessPointoutAutoAck` and the delayed-handoff consolidation redirect — `TickProcessor.cs:1086-1094, 1225, 1310`; a temp reconstruction room has zero attendance, a same-room rewind sees today's)
  - [ ] Sim-time, not `DateTime.UtcNow`, for `TdlsItemRecord.CreatedUtc/SentUtc/WilcoUtc/ExpiresUtc` (`TdlsCommandHandler.cs:80,139,142,190`, `TickProcessor.cs:282,293,340`) and the strip PDT/ETA text (`StripMutations.cs:878-903`) — every rewound PDC and strip shows the reconstruction's clock today
  - [ ] `FlightStripState` / `TdlsState` into `ScenarioSnapshotDto`, retiring `RoomStateSnapshotMapper`'s duplicate (today rewind never restores either; only session persistence does)
  - [ ] Shrink `IActionHost` as each host-slot body (strips, TDLS, coordination, ASDE-X/SAID, bookmarks) crosses — the action path's step-4 debt, same shape as `IHostSteps`
