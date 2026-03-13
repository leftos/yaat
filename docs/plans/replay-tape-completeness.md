# Replay Tape Completeness — CRC Commands & Flight Plan Amendments

## Problem

The replay tape currently captures instructor/RPO commands sent through `SendCommand`, but misses mutations originating from CRC clients (students, other controllers). For faithful rewind/replay, **every state mutation** must be recorded, regardless of source.

### What's captured today

| Source | Mutation | Captured? |
|--------|----------|-----------|
| Instructor (YAAT) | Aircraft commands (via SendCommand) | Yes — `RecordedCommand` |
| Instructor (YAAT) | Spawn (ADD) | Yes — `RecordedSpawn` |
| Instructor (YAAT) | Delete (DEL) | Yes — `RecordedDelete` |
| Instructor (YAAT) | Warp | Yes — `RecordedWarp` |
| Instructor (YAAT) | Weather change | Yes — `RecordedWeatherChange` |
| Instructor (YAAT) | Settings change | Yes — `RecordedSettingChange` |
| Instructor (YAAT) | PAUSE/UNPAUSE/SIMRATE | Yes — `RecordedCommand` (after Phase 1.6) |
| CRC student | Track (InitiateTrack) | No |
| CRC student | Drop track (DropTrack) | No |
| CRC student | Handoff (InitiateHandoff) | No |
| CRC student | Accept handoff (AcceptHandoff) | No |
| CRC student | Pointout | No |
| CRC student | Amend flight plan | No |
| CRC student | Create flight plan | No |
| CRC student | Scratchpad update | No |
| CRC student | Temp altitude assignment | No |
| CRC student | Speed/heading/altitude assignment | No |
| CRC other controller | All of the above | No |

### What's needed

Every CRC hub method that mutates aircraft or room state must record a `RecordedAction` before applying the mutation. On rewind, these actions are replayed in order.

## Design

### New RecordedAction subtypes

```csharp
// CRC track operations
record RecordedCrcTrack(string Callsign, string ClientId, string Position) : RecordedAction;
record RecordedCrcDropTrack(string Callsign, string ClientId) : RecordedAction;
record RecordedCrcHandoff(string Callsign, string FromPosition, string ToPosition) : RecordedAction;
record RecordedCrcAcceptHandoff(string Callsign, string ClientId) : RecordedAction;
record RecordedCrcPointout(string Callsign, string FromPosition, string ToPosition) : RecordedAction;

// CRC flight plan mutations
record RecordedAmendFlightPlan(string Callsign, FlightPlanDto FlightPlan) : RecordedAction;
record RecordedCreateFlightPlan(string Callsign, FlightPlanDto FlightPlan) : RecordedAction;

// CRC scratchpad / assignments
record RecordedCrcScratchpad(string Callsign, string Value) : RecordedAction;
record RecordedCrcTempAltitude(string Callsign, int Altitude) : RecordedAction;
```

### FlightPlanDto for replay

Flight plan amendments need the full flight plan state for replay (not a diff), since diffs are fragile across rewind boundaries. The `FlightPlanDto` should contain all mutable fields: type, equipment, route, altitude, speed, departure, destination, scratchpad, remarks.

### Recording points

Each CRC hub method in `RoomEngine` that processes a CRC client message should call `Room.ActiveScenario.RecordAction(new RecordedCrc*(...))` after validation but before applying the mutation.

### Replay

`ApplyRecordedAction` in `RoomEngine` gets new cases for each `RecordedCrc*` type, applying the mutation directly (bypassing the CRC hub method to avoid re-broadcasting).

## Checklist

- [ ] Define `FlightPlanDto` record with all mutable flight plan fields
- [ ] Add `RecordedCrcTrack`, `RecordedCrcDropTrack`, `RecordedCrcHandoff`, `RecordedCrcAcceptHandoff`, `RecordedCrcPointout` subtypes
- [ ] Add `RecordedAmendFlightPlan`, `RecordedCreateFlightPlan` subtypes
- [ ] Add `RecordedCrcScratchpad`, `RecordedCrcTempAltitude` subtypes
- [ ] Instrument CRC hub methods in RoomEngine to record actions
- [ ] Implement `ApplyRecordedAction` cases for all new subtypes
- [ ] Add tests for CRC action recording and replay
- [ ] Verify rewind works across CRC mutations

## Dependencies

- Phase 1.6 (unified command pipeline) — DONE
- Understanding of all CRC→server mutation methods (audit needed)

## Priority

Medium — required for faithful rewind when students are actively controlling. Not blocking current instructor-only workflows.
