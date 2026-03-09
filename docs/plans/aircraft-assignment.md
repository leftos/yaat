# Aircraft Assignment (Sole Control)

Each instructor/RPO in a room can be assigned one or more aircraft. Commands to unowned aircraft are rejected unless prefixed with `** `.

## Design

### Data Model

**Server — `TrainingRoom`:**
- `Dictionary<string, string> AircraftAssignments` — `callsign → connectionId` (reverse from before — one owner per aircraft, fast lookup)
- Helper: `GetAssignedController(callsign) → connectionId?`
- Helper: `GetAssignmentsForConnection(connectionId) → List<string>` (callsigns)
- Helper: `HasAnyAssignments() → bool`

**Why connectionId, not CID:** Consistent with existing identity model (`Members`, `ActivePositionByConnection`, `RecordedCommand.ConnectionId`). If a user reconnects, assignments reset — acceptable for a training tool.

**Server — `RoomEngine`:**
- `AssignAircraft(callsign, targetConnectionId)` — sets owner; broadcasts
- `AssignAircraftBatch(callsigns, targetConnectionId)` — bulk assign; single broadcast
- `UnassignAircraft(callsign)` — removes owner; broadcasts
- `UnassignAircraftBatch(callsigns)` — bulk unassign; single broadcast
- `ClearAssignmentsForConnection(connectionId)` — bulk clear on leave
- `GetAssignments() → Dictionary<string, string>` — callsign→initials for UI sync

Any member can assign/reassign any aircraft to any other member, or unassign back to "everyone".

**Cleanup:**
- On `LeaveRoom`: `ClearAssignmentsForConnection(connectionId)`
- On `DeleteAircraft`: remove callsign from assignments
- On `UnloadScenario`: clear all assignments

### Command Enforcement

**Server — `RoomEngine.SendCommandAsync`:**

Before dispatch (after callsign resolution, before parse):

```
1. Check if command starts with "** " → strip prefix, set forceOverride = true
2. If !forceOverride && HasAnyAssignments():
   a. GetAssignedController(callsign) → ownerConnectionId
   b. If ownerConnectionId != null && ownerConnectionId != connectionId:
      → return CommandResultDto(false, $"Assigned to {ownerInitials}. Prefix with ** to override.")
```

If no assignments exist at all, the feature is inactive (zero friction for rooms that don't use it).

The `** ` prefix is stripped server-side so it never reaches the command parser.

**Override feedback:** When `** ` override is used successfully, the terminal broadcast includes a note: `"{initials} (override): {callsign} {command}"` instead of the normal `"{initials}: {callsign} {command}"`. No separate broadcast — just inline with the existing command feedback.

**Ownership change broadcast:** When assignments change, broadcast a system terminal entry: `"JOE assigned AAL123, DAL456 to BOB"` or `"JOE unassigned AAL123 (available to all)"`.

### Recording & Playback

**`RecordedCommand`** already stores `ConnectionId`. No changes needed — replay uses the original connectionId for identity resolution. The `** ` prefix is stripped before recording, so replayed commands bypass assignment checks naturally (playback already bypasses SendCommandAsync).

Assignment changes are NOT recorded — they're room management, not simulation state. On rewind, assignments persist as-is.

### Hub API

**Client→Server:**
- `AssignAircraft(callsign, targetConnectionId)` — any member can assign any aircraft to any member
- `AssignAircraftBatch(List<string> callsigns, targetConnectionId)` — bulk assign
- `UnassignAircraft(callsign)` — restore to "everyone"
- `UnassignAircraftBatch(List<string> callsigns)` — bulk unassign
- `GetAircraftAssignments()` → `AircraftAssignmentsDto`

**Server→Client:**
- `AircraftAssignmentsChanged(AircraftAssignmentsDto)` — broadcast full state on any change

**`AircraftAssignmentsDto`:**
```csharp
public record AircraftAssignmentsDto(
    Dictionary<string, string> Assignments,  // callsign → initials
    List<RoomMemberDto> Members              // connectionId, cid, initials (for target picker)
);
public record RoomMemberDto(string ConnectionId, string Initials);
```

### Client

**`ServerConnection`:**
- `AssignAircraftAsync(callsign, targetConnectionId)`
- `AssignAircraftBatchAsync(List<string> callsigns, targetConnectionId)`
- `UnassignAircraftAsync(callsign)`
- `UnassignAircraftBatchAsync(List<string> callsigns)`
- `GetAircraftAssignmentsAsync() → AircraftAssignmentsDto`
- Event: `AircraftAssignmentsChanged(AircraftAssignmentsDto)`

**`AircraftModel`:**
- `AssignedTo` (`string?` initials) — updated from assignments on change
- Updated in `MainViewModel` when `AircraftAssignmentsChanged` fires

**`MainViewModel`:**
- `ObservableCollection<RoomMemberInfo> RoomMembers` — for target picker (connectionId + initials)
- Updates `AircraftModel.AssignedTo` on assignment changes
- Fetches assignments on room join

**`** ` prefix handling in `SendCommandAsync`:**
- If raw input starts with `** `, strip from input text, prepend `** ` to the canonical command string sent to server
- Server strips it in `SendCommandAsync` before parsing

### UI — DataGrid

**New "Ctrl" column** in `DataGridView.axaml` — bound to `AssignedTo`, positioned after Owner/HO columns.

**Context menu additions** (in `DataGridView.axaml.cs` `OnGridContextRequested`):
- After existing items, add separator + assignment section
- "Assign to >" submenu listing room members (initials) — clicking assigns selected aircraft
- "Unassign" item — restores to "everyone"
- Both operate on **all selected rows** in the DataGrid (multi-select support for bulk assign)

### UI — Radar Datablock

**Line 3** of the datablock currently shows: `{Owner} >{Handoff} .{SP1} +{SP2}`

Add assignment indicator: if aircraft is assigned, prepend controller initials in brackets before owner field:
```
[BOB] ZOA41 >NOR .SP1 +SP2
```

If assigned to the current user, show `[ME]` instead of initials (shorter, instantly recognizable):
```
[ME] ZOA41 .SP1 +SP2
```

If not assigned to anyone, no brackets — unchanged from current behavior.

Implementation: in `TargetRenderer.BuildOwnerScratchpadLine`, add an `assignedTo` parameter. Prepend `[{assignedTo}]` or `[ME]` before the existing owner string.

### UI — Radar Context Menu

Add to `RadarView.ContextMenus.cs` `OnAircraftRightClicked`:
- "Assign to >" submenu with room members
- "Unassign" item
- Single aircraft only (radar right-click is always one target)

## Implementation Chunks

### Chunk 1: Server data model + enforcement
- [x] Add `AircraftAssignments` dictionary to `TrainingRoom` (callsign → connectionId)
- [x] Add helper methods (`GetAssignedController`, `ClearAssignmentsForConnection`)
- [x] Add `RoomEngine` methods: `AssignAircraftAsync`, `UnassignAircraftAsync`, `GetAssignmentsDto`, `BroadcastAssignmentsAsync`
- [x] Add enforcement in `RoomEngine.SendCommandAsync` (check + `** ` strip)
- [x] Override terminal feedback: "(override)" inline tag via `broadcastInitials`
- [x] Assignment change terminal broadcast: system message
- [x] Clean up on leave/disconnect/delete/unload
- [x] Add hub methods to `TrainingHub` (AssignAircraft, UnassignAircraft, GetAircraftAssignments)
- [x] Add `AircraftAssignmentsChanged` broadcast via `ITrainingBroadcast`
- [x] DTOs: `AircraftAssignmentsDto`, `AssignableMemberDto`
- [x] Tests: 19 tests covering assignment CRUD, batch ops, enforcement (blocked/override/unassigned/no-assignments), cleanup on leave/delete/unload, terminal messages, DTO shape

### Chunk 2: Client integration + UI
- [x] Add `ServerConnection` methods + event + DTOs
- [x] Add `AircraftAssignmentsChanged` handler in `MainViewModel`
- [x] Add `AssignedTo` to `AircraftModel`
- [x] Track `AssignableMembers` list (from assignments DTO)
- [x] Add "Ctrl" column to DataGrid
- [x] DataGrid context menu: "Assign to" submenu + "Unassign" (multi-select)
- [x] Radar context menu: "Assign to" submenu + "Unassign"
- [x] Radar datablock: `[INITIALS]` prefix on line 3
- [x] Handle `** ` prefix in `SendCommandAsync`
- [x] Fetch assignments on room join
- [x] Update USER_GUIDE.md
