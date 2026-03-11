# Issue #50: Add "Say Speed" Command

## Summary

Add a `SaySpeed` command (primary verb: `SSPD`, alias: `SS`) that broadcasts IAS to the training room terminal, so mentors don't need to mentally convert STARS groundspeed to IAS.

## Architecture

SAY commands are intercepted **before** `CommandDispatcher` in both the client and server:

- **Client** (`CommandDispatcher.cs:278-279`): Returns `Ok("")` for `SayCommand` — server handles broadcast.
- **Server** (`RoomEngine.cs`): Checks `if (simpleParsed is SayCommand sayCmd)` and calls `_broadcast.BroadcastTerminalEntry(...)`.

`SaySpeed` follows the same pattern with its own `SaySpeedCommand` parsed command type.

## Speed Data

`AircraftState.cs:45` — `IndicatedAirspeed` is the correct field. `GroundSpeed` is what STARS displays.

## Files to Edit

### Yaat.Sim (shared)

| File | Change |
|------|--------|
| `src/Yaat.Sim/Commands/CanonicalCommandType.cs` | Add `SaySpeed` entry (after `Say`) |
| `src/Yaat.Sim/Commands/ParsedCommand.cs` | Add `record SaySpeedCommand() : ParsedCommand;` |
| `src/Yaat.Sim/Commands/CommandParser.cs` | Add `"SSPD" => new SaySpeedCommand()` and `"SS" => new SaySpeedCommand()` in the command switch |
| `src/Yaat.Sim/Commands/CommandDescriber.cs` | Add `SaySpeedCommand` description (e.g. "Say Speed") |
| `src/Yaat.Sim/Commands/CommandDispatcher.cs` | Add `case SaySpeedCommand: return Ok("");` (intercepted pre-dispatch) |

### Yaat.Client

| File | Change |
|------|--------|
| `src/Yaat.Client/Services/CommandRegistry.cs` | Add entry: `Cmd(SaySpeed, "Say Speed", "Broadcast", false, ["SSPD", "SS"], [...])` |

### yaat-server

| File | Change |
|------|--------|
| `src/Yaat.Server/Simulation/RoomEngine.cs` | Add intercept: `if (simpleParsed is SaySpeedCommand) { broadcast IAS; return Ok; }` |

## Server Implementation

```csharp
if (simpleParsed is SaySpeedCommand)
{
    var aircraft = room.GetAircraft(callsign); // or however aircraft is retrieved
    var ias = (int)Math.Round(aircraft.State.IndicatedAirspeed);
    await _broadcast.BroadcastTerminalEntry(Room, broadcastInitials, "SaySpeed", callsign, $"{ias} knots");
    return new CommandResultDto(true, null);
}
```

## Terminal Display

The `Kind` field is `"SaySpeed"` in `TerminalBroadcastDto`. Client terminal colorizer may need a case for it (or it falls through to a default style — verify existing behavior).

## Completeness Tests

`tests/Yaat.Client.Tests/CommandSchemeCompletenessTests.cs` — tests will auto-pass once `SaySpeed` is added to `CommandRegistry` and `CanonicalCommandType`.

## Docs to Update

- `USER_GUIDE.md` — add SSPD / SS / Say Speed to command reference
- `docs/yaat-vs-atctrainer.md` — mark as YAAT-only (reference.md shows VICE has `SS`, ATCTrainer has none)
- `docs/command-aliases/reference.md` — update SS row from "Not yet" to implemented; note SSPD as primary
