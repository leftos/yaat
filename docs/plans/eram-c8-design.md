# ERAM C.8 commands — design passes

Design for the twelve ERAM commands the SRS C.8 table lists that YAAT does not handle yet: flight-plan tools `FR`, `FP`, `DQ`, `RM`, `SP`; conflict alert `CA`, `RK`; messages and strips `SM`, `RS`; weather `SW`, `UR`, `WX`. The ruling that puts all twelve in scope, and drops `CA.yaml`'s `na`, is in [docs/eram/rulings.md](../eram/rulings.md#rulings). Each decision below is a best guess grounded in a named source; the points the sources leave open are listed at the end as questions for ERAM controllers, each with the guess the design goes with.

## Sources and conventions

- **SRS**: *ERAM EDSM SRS Vol 1 Book 2* (the PDF named in [docs/eram/README.md](../eram/README.md)). §C.8 is the validation table, §C.2 the format and routing table, §C.1 the field definitions. Pages are printed page numbers. The §C.2 tables extract badly (columns interleave), so a §C.2 claim below is the reading that fits every row around it. None of the twelve YAML files has fields extracted yet (each is `handler: not implemented`, `fields: []`), so the formats and checks below come from the PDF itself.
- **CRC**: CRC handles only `SR`, `QD`, `WR`, `MR`, `QB` (code-list forms) and `AR` itself. It sends every other entry, all twelve included, to `ProcessEramMessage` as `EramPositionType.RSide` (`Vatsim.Nas.Crc.Ui.Displays.Eram.Input/InputManager.cs:505-528`, `:963`). It shows the result's `Feedback` lines in the MCA feedback area and its `Response` in the Response Area (`InputManager.cs:924-929`). `ViewRa` splits a response on `\n`, wraps it to the area's width and scrolls it ten lines at a time (`Eram.Views/ViewRa.cs:82-117`). CRC has no ERAM view for sector messages (`ViewTime.cs` draws only the clock), conflict-alert status, strips, upper winds or significant weather. Its Weather Station Report view and `WR R` download live METARs from the vNAS METAR URL (`Vatsim.Nas.Crc.Weather/MetarRepository.cs:52-111`), not from yaat-server. The CRC manual ([docs/crc/eram.md](../crc/eram.md) Table 17) lists none of the twelve. The one channel the server can push text through outside a command's own response is a private message (`ReceivePrivateMessage`, `yaat-server/src/Yaat.Server/Hubs/CrcClientState.Info.cs:134`).
- **vatsim-server-rs** (`D:/vatsim-server-rs`, 2f3a6b0) implements none of the twelve (`rg '"(FR|FP|DQ|RM|SP|CA|RK|SM|RS|SW|UR|WX)"' crates` finds only a leader direction `SW`).
- **Server shape**: each command gets a dispatch arm in `CrcClientState.DispatchEramMessage` (`yaat-server/src/Yaat.Server/Hubs/CrcClientState.Eram.cs:90-143`), an entry in `EramVariants.Verbs` and `EramVariants.Descriptors` (`Hubs/Eram/EramVariants.cs`), a filled YAML (`format`, `multiple_flids`, `handler`, `fields`) and an `EramConformance<Xx>Tests` file, plus a succeeding-command row per variant key in `EramFeedbackConventionTests` (and its `ReadoutKeys` entry for a readout). `EramReferenceConformanceTests` then holds the descriptors and error texts to the YAML. The README's list of readouts that write the Response Area (`QF`, `LA`–`LE`) gains `FR`, `RK`, `SM` (readout), `UR`.
- **Valid source**: §C.2 marks `DQ`, `FR` and `RS` as D-position or A-position commands, and `CA`, `RK`, `SP`, `SW` and `SM` create/delete as AT Specialist commands. CRC is always the R-position, a VATSIM ERAM controller works the R and D positions of a sector together, and VATSIM has no AT Specialist. **Decision**: accept all twelve from CRC, and say so in each YAML's `note` ("§C.2 lists no R-position source; YAAT accepts it from CRC, which stands in for the D-position and the AT Specialist"). A position-type check (`MsgR-PositionNotConfigured` on a D-position entry) is `na` with QF.yaml's reason, "CRC always enters commands from the R-Position".
- **Replay**: an entry that changes simulation state is recorded and replays exactly. Per-track ERAM state uses `RecordedEramEntry`. The existing record types are reused where one fits: `RecordedRequestNewBeaconCode`, `RecordedAmendFlightPlan`, and the strip record behind `RecordAndDispatchStrip`. Room-level ERAM state (conflict-alert settings, sector messages, entered weather reports) needs a new `RecordedEramRoomEntry(ElapsedSeconds, FacilityId, Entry)`, modelled on `RecordedEramCrrGroup`. Its entries carry absolute values, never toggles, as the dwell-lock ruling requires. Text pushed to CRC as a message is recorded as `RecordedChat`, which the Sim's replay ignores, playback echoes into the instructor terminal (so the live push shows the same terminal line), and bundles keep.

## Errors to add to `error-responses.yaml`

These IDs appear in the checks below and are not in the table yet (all `source: coined`):

| ID | Text | Used by |
|---|---|---|
| `MsgNoRemarks` | `NO MESSAGE TEXT` | SM |
| `MsgFieldOmission` | `FIELD OMISSION` | WX |
| `MsgInvalidAltitude` | `INVALID ALT` | UR |
| `MsgNoDataPresent` | `NO DATA PRESENT` | FP |
| `MsgFieldMismatch` | `FIELD MISMATCH` | FP |
| `MsgOTHNoDataPresent` | `OTH NO DATA` | FP |

Every other text below is quoted from the table as it stands.

## Brief grouping and build order

| Brief | Commands | Owning files | Why together | Order |
|---|---|---|---|---|
| C1 D-side and flight-plan tools | `DQ`, `FR`, `RS`, `RM` | Srv `CrcClientState.Eram.FlightData.cs`, `CrcClientState.Eram.Track.cs`, `Eram/EramVariants.cs`; the four YAMLs | each reuses an R-side handler (`QB`, `QF`, `QX FP`) or the FLID resolver alone; no new state | first |
| C2 room ERAM state and conflict-alert settings | `CA`, `RK` | Sim `Simulation/RecordedAction.cs`, new `Simulation/Eram/EramRoomState.cs`, `SimulationEngine.Eram.cs`, `Simulation/Actions/ActionRouter.cs`, `Snapshots/ServerSnapshotDto.cs`; Srv new `CrcClientState.Eram.ConflictSettings.cs`, `CrcBroadcastService.cs` | builds the `RecordedEramRoomEntry` that C3 and C4 reuse | second; after B4 (the conflict pass) |
| C3 messages | `SM`, `SW` | Srv new `CrcClientState.Eram.Messages.cs`; Sim `EramRoomState.cs` (sector messages) | both push text to ERAM sessions as a private message | after C2 |
| C4 weather | `UR`, `WX` | Srv new `CrcClientState.Eram.Weather.cs`; Sim `EramRoomState.cs` (entered reports); Srv weather broadcast (`WeatherChangedDto` composer) | both read or write the room's weather | after C2 |
| C5 filing | `FP`, `SP` | Srv new `CrcClientState.Eram.Filing.cs`, sharing AM's field parsers in `CrcClientState.Eram.FlightData.cs` | `SP` is `FP`'s field set with a stereo tag for a route | last, the largest |

The sections follow that order.

## C1 — D-side and flight-plan tools

### DQ — Discrete Code Request

**ERAM**: `DQ (60) 02`, the D-position form of `QB`'s Discrete Code Request (§C.2 p.535; §C.8 p.667–668, descriptor `DIS CODE REQ`). Field 60 must be `/OK` (`MsgCofieFormat`). Field 02 is an ACID (`Laa(a)(a)(a)(a)` or `Ld`) or a CID (`MsgFlidFormat`). A CID, or any entry carrying `/OK`, must resolve to a system plan. An ACID without `/OK` need not resolve (`MsgFLIDNotStored`). Field 02 must not match two flights (`MsgFLIDDuplication`). A beacon code and a trackball pick are not listed forms.

**CRC**: typed only; sent raw; feedback area.

**YAAT**: `CrcClientState.Eram.FlightData.cs` `RequestQbDiscreteCode` (:1293) draws a code through `RoomEngine.RequestNewBeaconCode`, which records `RecordedRequestNewBeaconCode` (`RoomEngine.cs:1080-1097`).

**Behaviour**: a `"DQ"` arm reads field 60, resolves field 02 as an ACID or CID only (a beacon code, a list or a pick → `FLID FORMAT`, matching the error list below), then calls `RequestDiscreteCode` and answers `ACCEPT / DIS CODE REQ / <ACID>`. YAAT cannot reserve a code for a flight that is not in the room, so an ACID that names no aircraft is `FLID NOT STORED` even without `/OK`. The YAML notes this departure from the "does not have to resolve" rule. One flight per entry (§C.2 "Single").

**State**: changes state, recorded as `RecordedRequestNewBeaconCode`.

**Errors**: `{cofie} FORMAT`, `FLID FORMAT`, `FLID NOT STORED`, `DUPLICATE FLID`.

**Tests**: `EramConformanceDqTests`: each error; a DQ draws the same code a QB would, attributed to the entering sector; a replay of the recording reproduces the code.

**Size**: one-site.

### FR — Flight Plan Readout Request

**ERAM**: the D-position readout, §C.2 p.538–539: `FR 02 (16) (12)…(12)`, `FR 02 (42) (16)`, `FR 02 42 (16)` with 42 `A`/`C`/`F` (ICAO), `*` (duplicates) or `D` (data comm). §C.8 p.695–697, descriptor `READOUT`. Field 02 is an ACID, CID, octal beacon or `/Edddd`, with no pick (`MsgFlidFormat`); a beacon must be discrete (`MsgIllegalFlightID`); the flight must be found, and found once unless 42 is `*`. A lone single-character field must be `1 2 A I C F P D *` (`MsgCofieFormat`); 42 must be `1 2 A I C D F *` (`MsgInvalidSelection`); with 42 `*`, field 02 must be an ACID (`MsgFlidFormat`); 16 must be `P` (`MsgIncorrectRouting`); 42 and 12 do not combine (`MsgInvalidCombination`); 12 is a two-digit reference number, an abbreviation or a 918 indicator (`MsgInvalidReference`) and is never repeated (`MsgCofieDuplicateFieldReference`). The print option in pending mode (`MsgNoFSPPrintingInPendingMode`) is `na`, since CRC has no printer channel.

**CRC**: the result's `Response` goes to the Response Area.

**YAAT**: `DispatchQf` and its helpers (`FlightData.cs:12-143`) already produce the one-line readout and the field-12 readout.

**Behaviour**: generalise the QF parse by the verb's routing letter and selection set (`R` and `QfSelections` for QF, `P` and FR's set for FR), and share the readout builders. **Decision**: `P` (print) writes the Response Area, the only output CRC has.

**State**: readout only.

**Errors**: `FLID FORMAT`, `ILLEGAL FLID`, `FLID NOT STORED`, `DUPLICATE FLID`, `{cofie} FORMAT`, `INVALID SELECTION`, `INCORRECT ROUTING`, `INVALID COMBINATION`, `INVALID FIELD REF`, `{cofie} DUPLICATE FIELD REF`.

**Tests**: `EramConformanceFrTests`, one per check. `FR <ACID>` returns the same text as `QF <ACID>`. `FR R <ACID>` is `INCORRECT ROUTING`.

**Size**: multi-file (`FlightData.cs`, `EramVariants.cs`, FR.yaml; QF's tests stay green).

### RS — Remove Strip

**ERAM**: `RS 02`, the D-position and A-position form of `QX FP` (§C.2 p.556–557; §C.8 p.798–799, descriptor `REMOVE STRIP`). Field 02 is an ACID, CID or octal beacon, with no pick (`MsgFlidFormat`). It may carry a `/OK` suffix from a D or A position. A beacon must be discrete (`MsgIllegalFlightID`). The flight is found (`MsgFLIDNotStored`), and only once (`MsgFLIDDuplication`).

**CRC**: feedback area. Strips live in vStrips, which yaat-server already feeds.

**YAAT**: `CrcClientState.Eram.Track.cs` `RemoveQxStrip` (:163-179) removes the strip through `RecordAndDispatchStrip(…, "STRIPD", …)`, and answers ACCEPT when the aircraft has no strip.

**Behaviour**: an `"RS"` arm resolves field 02, cutting a `/OK` suffix (which has no ownership gate to lift here, as for QX FP), refuses a pick with `FLID FORMAT`, and calls `RemoveQxStrip` under the key `RS.RemoveStrip`.

**State**: changes state, through the existing strip record.

**Errors**: `FLID FORMAT`, `ILLEGAL FLID`, `DUPLICATE FLID`, `FLID NOT STORED`.

**Tests**: `EramConformanceRsTests`; RS and `QX FP` leave the same strip set; a replay removes the strip at the same second.

**Size**: one-site.

### RM — Request Route Conversion

**ERAM**: `RM 02` with `/OK` allowed on field 02 (§C.2 p.556, sent to `FLTS:Request Route Conversion`; §C.8 p.797–798, descriptor `ROUTE CONVERSION`). Field 02 is an ACID with an optional departure point (`/fix`, `/FRD`, `/lat-long`), a CID, an octal beacon or a pick (`MsgFlidFormat`). A pick must capture a track or a flight entry (`MsgNoTBFlightID`). A beacon must be discrete (`MsgIllegalFlightID`). The flight is found (`MsgFLIDNotStored`), and only once (`MsgFLIDDuplication`). ERAM re-runs its route conversion on the stored plan. The volume defines no output beyond the descriptor.

**YAAT**: YAAT stores no converted route apart from the aircraft's navigation route. It builds that route when the plan is filed or amended, and the route display draws from it (`CrcClientState.Eram.Route.cs` `BuildRouteLineFromAircraft`). There is nothing stale to convert again, and rebuilding the navigation route would change where the aircraft flies.

**Behaviour**: an `"RM"` arm runs the field-02 checks, cutting `/OK` and the departure point the way `DispatchRf` does (`FlightData.cs:971-998`), and answers `ACCEPT / ROUTE CONVERSION / <ACID>` with no change.

**State**: none.

**Errors**: `FLID FORMAT`, `INVALID PICK`, `ILLEGAL FLID`, `FLID NOT STORED`, `DUPLICATE FLID`.

**Tests**: `EramConformanceRmTests`, one per check. An accepted RM leaves the aircraft's plan and route byte-identical.

**Size**: one-site.

## C2 — Room ERAM state and conflict-alert settings

### CA — Conflict Alert/MCI On-Off

**ERAM**: four actions (§C.2 p.531–532), all with descriptor `MCI CA SETTING` (§C.8 p.666):

- `CA 36`: the centre's CA function on or off;
- `CA 14 (14)… 36`: CA display for up to five sectors;
- `CA 16 36`: the MCI function (field 16 `INT`);
- `CA 16 14 (14)… 36`: MCI display, where field 14 may be `ALL`.

§C.1 field 36 (p.451) gives the examples `CA ON`, `CA 55 56 OFF` and `CA INT 55 56 OFF`. The checks: 36 is `ON`/`OFF` (`MsgCofieIllegalAction`); 16 is `INT` (`MsgCofieFormat`); 14 is `(d)dd` 01–128, or `ALL` with `INT` (`MsgCofieFormat`); an adapted sector (`MsgNon-AdaptedSector`) with an R-position (`MsgR-PositionNotConfigured`, `na` with QA.yaml's reason: YAAT adapts a sector only through its position); at most five 14s, and `ALL` alone (`MsgMessageTooLong`).

**CRC**: shows STCA through the `EramShortTermConflicts` topic and the data blocks' `ConflictStatus`. It has no setting view.

**YAAT**: `EramConflictDetector` and `SimulationEngine.TickEramConflictAlerts` (`SimulationEngine.Tick.cs:737-827`) keep `EramConflicts`, which `CrcBroadcastService.BroadcastEramConflictAlertsAsync` (:562-594) gates per client by facility. The data-block roles come from `SnapshotEramConflictRoles(room, facility)` (:238). No on/off state exists.

**Behaviour**: new Sim `EramRoomState` per facility holds `CaFunctionOn`, `MciFunctionOn` (default on), and the sets `CaDisplayOffSectors` and `MciDisplayOffSectors`. **Decision**: detection is left alone, and the settings filter what each client sees. A function off hides every alert in the facility, or every MCI pair (one with `IntruderCallsign`). A sector's display off hides them only from that sector's sessions. The filter applies in both the conflict topic and the data-block roles. A settings change resends the topic to the affected clients (delete all, send what is visible), through a host drain like `OnEramCrrGroupsChanged`.

**State**: changes state. Recorded as `RecordedEramRoomEntry(facility, "CA {CA|MCI} {ALL|FN|sector…} {ON|OFF}")` and snapshotted in `ServerSnapshotDto` beside `CrrGroups`.

**Errors**: `{cofie} ILLEGAL ACTION`, `{cofie} FORMAT`, `SECTOR NOT ADAPTED`, `MESSAGE TOO LONG`.

**Tests**: Sim tests for apply, snapshot round-trip and replay equality. Server tests with `RoomEngineTestHarness` and `RecordingWebSocket`: sector 55 off stops 55's alerts and FDB flash while 56 still gets them; `CA OFF` clears every alert; `CA INT OFF` hides only the MCI pairs; `CA ON` restores the active alerts. `EramConformanceCaTests` covers each check.

**Size**: design.

### RK — Conflict Alert Status Request

**ERAM**: `RK (14)…(14)` and `RK 16 (14)…(14)` (§C.2 p.556; §C.8 p.797, descriptor `REQ CA STATUS`). §C.1 field 16 (p.434–435): `RK INT 10 11` requests "the MCI display status for sectors 10 and 11", and `RK INT` "the MCI function status for the center and/or the display status for each sector". The checks: 16 if present is `INT` (`MsgCofieFormat`); 14 is `(d)dd` 01–128 (`MsgCofieFormat`), adapted (`MsgNon-AdaptedSector`) with an R-position (`MsgR-PositionNotConfigured`, `na`), at most five (`MsgMessageTooLong`). The readout's layout is in the B-level "Display Conflict Alert Status Report", which is not in this volume.

**CRC**: Response Area.

**YAAT**: C2's `EramRoomState`.

**Behaviour**: a readout of the function status, then each named sector (or every adapted sector whose display is off, when none is named):

```
CA FUNCTION ON
CA DISPLAY OFF 55 56
```

`RK INT` gives the same with `MCI`. A named sector prints `55 CA ON` or `55 CA OFF`, one per line. The layout is coined.

**State**: readout only.

**Errors**: `{cofie} FORMAT`, `SECTOR NOT ADAPTED`, `MESSAGE TOO LONG`.

**Tests**: `EramConformanceRkTests`; readout text after each CA form.

**Size**: multi-file (the new file, `EramVariants.cs`, RK.yaml, tests).

## C3 — Messages

### SM — R-position Message Text

**ERAM**: three actions (§C.2 p.565), all with descriptor `SECTOR MESSAGE` (§C.8 p.810–811):

- create, `SM 14 (14)… 11`, sending a message to up to 48 sectors or `ALL`;
- delete, `SM DE` (field 64);
- readout, `SM`.

§A.1 rows 1061–1064 (p.134–135) give the R-position Time View a Sector Message Notification Area, yellow while unacknowledged and white once acknowledged. The checks: 14 is `(d)dd` 01–128 or `ALL` (`MsgCofieFormat`), adapted (`MsgNon-AdaptedSector`) with an R-position (`MsgR-PositionNotConfigured`, `na`), at most 48, and `ALL` alone (`MsgMessageTooLong`). 64 is `DE` (`MsgInvalidActionType`). `DE` takes no 14 and no 11 (`MsgCofieInvalidCombination`). 11 is the clear-weather symbol (`\u0087` on the wire, `EramChar.ClearWeather` = 135) followed by at least one non-blank character (`MsgNoRemarks`), and at most 32 characters once trimmed (`MsgCofieFormat`).

**CRC**: no notification area. Its Time View draws only the clock (`ViewTime.cs`).

**YAAT**: nothing.

**Behaviour**: `EramRoomState` keeps one message per (facility, sector). **Decision**: since CRC cannot show the notification area:

- create stores the text for each addressed sector and pushes it to that sector's ERAM sessions as a private message from the sender's sector (`SECTOR 55: <text>`);
- `SM` reads the entering sector's message into the Response Area (`NO SECTOR MESSAGE` when there is none);
- `SM DE` deletes the entering sector's message, which is the acknowledgement.

The text is free, spaces included, from the clear-weather symbol to the end of the entry.

**State**: create and delete change state, recorded as `RecordedEramRoomEntry(facility, "SM {sector} {text}")` and `"SMDE {sector}"`. The push is recorded as `RecordedChat`.

**Errors**: `{cofie} FORMAT`, `SECTOR NOT ADAPTED`, `MESSAGE TOO LONG`, `INVALID ACTION`, `{cofie} INVALID COMBINATION`, `NO MESSAGE TEXT`.

**Tests**: `EramConformanceSmTests`; a two-session delivery test; replay reproduces the table.

**Size**: multi-file.

### SW — Significant Weather

**ERAM**: an AT Specialist entry of a SIGMET (fields 500–506, separated by `>>`), MIS (`507 512>>511`), CWA/UCWA (`507>>512>>504>>505`) or CWSU (`501 509 511`) report (§C.2 p.560–561). The line fields are defined by the WMSCR ICD (§C.1 p.501). §C.8 p.821: "No additional error checking performed by EDSM. All error checks will be done by EWDP", which is outside this volume, so the YAML has no field rows. Descriptor `SIGNIFICANT WEATHER`.

**CRC**: no SIGMET view.

**YAAT**: no SIGMET model. Weather physics never reads text reports ([weather-and-wind.md](../weather-and-wind.md)).

**Behaviour**: `SW <text>` answers `ACCEPT / SIGNIFICANT WEATHER` and pushes the text, with each `>>` turned into a line break, as a private message to every ERAM session of the facility. That is the distribution a SIGMET gets. A bare `SW` is `MESSAGE TOO SHORT`. Nothing is stored.

**State**: none. The push is recorded as `RecordedChat`.

**Errors**: `MESSAGE TOO SHORT` only (EDSM runs no other checks).

**Tests**: delivery to both sessions of a facility and none to another facility.

**Size**: one-site (it reuses SM's push helper).

## C4 — Weather

### UR — Upper Wind Request

**ERAM**: `UR (300 | 301 | 302 (302)…) [65 | 68]`, valid from specially adapted R-positions (§C.2 p.565; §C.8 p.838–840, descriptor `UPPER WIND REQ`). §C.1 fields 300–302 (p.492):

- 300 is a block, `(d)d0B(d)d0` between 010 and 590 (`MsgInvalidAltitude`), with the second altitude higher (`MsgInvalidBlockedAltitude`);
- 301 is a bound, `(d)d0+` or `(d)d0-` (`MsgInvalidAltitude`);
- 302 is an altitude `(d)d0` (`MsgInvalidAltitude`), up to five (`MsgMessageTooLong`).

300, 301 and 302 are exclusive of each other (`MsgMessageTooLong`). 65 is a trackball point (`MsgNoSituationDisplayPointCapture`) and excludes 68 (`MsgMessageTooLong`). 68 is a fix, FRD or lat/long, with the usual range checks (`MsgCofieFormat`). A-position pending mode is `na`. The readout layout is in the B-level "Upper Winds Request", which is not in this volume.

**CRC**: a pick carries a `Location`; the result goes to the Response Area.

**YAAT**: the room's `WeatherProfile.WindLayers`, magnetic and room-wide, with no temperatures ([weather-and-wind.md](../weather-and-wind.md) §Data model). `MagneticDeclination.MagneticToTrue` converts a direction to true. LA/LB/LF already parse field 65 and 68 operands.

**Behaviour**: interpolate the mean wind (without the `WindVariation` gusts) at each altitude and print it true, to 10°, in knots:

```
UPPER WINDS SFO
300 270/045
340 280/060
```

- With no altitude field, read out the FAA FD levels 030, 060, 090, 120, 180, 240, 300, 340 and 390.
- With 300 or 301, read out the FD levels inside the block or bound.
- With 302, read out the altitudes given.
- The location names the header and the declination.
- No wind layers reads out `NO UPPER WIND DATA`.

**State**: readout only.

**Errors**: `INVALID ALT`, `INVALID BLOCKED ALT`, `MESSAGE TOO LONG`, `{cofie} FORMAT`, `NO LOCATION PICKED`.

**Tests**: `EramConformanceUrTests`; the readout against a profile with known layers (direction converted to true, interpolation between layers).

**Size**: multi-file.

### WX — Weather Data

**ERAM**: `WX 13 35 45 (13 35 45)…`, at most 16 sets (§C.2 p.561; §C.8 p.845, descriptor `WEATHER DATA`), sent to `EWDP:Update Weather Data`. It enters a station's weather report. The checks:

- 13 is `aa(a)(a)(a)` (`MsgCofieFormat`);
- 35 is `dddd` HHMM (`MsgInvalidTime`);
- 45 starts with the clear-weather symbol (`MsgFieldOmission`), may contain blanks, and runs at most 240 characters (`MsgMessageTooLong`);
- each 13 is followed by 35 and 45 (`MsgFieldOmission`);
- a trailing `*` on 45 starts the next set.

**CRC**: its weather views read live METARs, never the server's.

**YAAT**: `MetarIssuer` issues the reported METAR strings that `WeatherChangedDto.Metars` carries to YAAT clients. They are display-only; physics reads the continuous `World.Weather` ([weather-and-wind.md](../weather-and-wind.md) §Reported METAR reconstruction).

**Behaviour**: `EramRoomState` keeps the entered reports per station. The weather broadcast shows an entered report in place of the station's issued one until the issuer's next routine or SPECI observation for that station. That is when a real observer's next report would supersede it. Physics is untouched.

**State**: changes state, recorded as `RecordedEramRoomEntry(facility, "WX {station} {hhmm} {text}")` per set.

**Errors**: `{cofie} FORMAT`, `INVALID TIME`, `FIELD OMISSION`, `MESSAGE TOO LONG`.

**Tests**: `EramConformanceWxTests`; multi-set parse; the broadcast shows the entered report until the next :53Z issuance; replay reproduces it.

**Size**: design (it touches the METAR broadcast path).

### C4 rulings

Grounded in the design above and the code as mapped; ERAM behaviour is decided, not asked.

- **Landing**: on `feat/eram-weather` in both repos (user 2026-09-29), as two stacked briefs: C4a `UR`, then C4b `WX`.
- **UR mean wind**: extract `WindInterpolator.GetMeanWindAt(WeatherProfile?, double altFt)` from `GetWindAt`'s inline N/E component interpolation (clamped outside the layer range, no `WindVariation`); `GetWindAt` calls it.
- **UR levels**: no altitude field reads the FD levels 030–390; a 300 block reads the FD levels inside it inclusive; a 301 bound reads the FD levels at or above (`+`) or at or below (`-`) it; 302 reads exactly the altitudes typed.
- **UR layout**: the design's form, `UPPER WINDS <name>` then `<alt hundreds, 3 digits> <true dir, 3 digits, to 10°>/<kt, 3 digits>` per line; `NO UPPER WIND DATA` with no layers.
- **UR location**: a pick or typed fix/FRD/lat-long names the header and gives the declination; the header is the typed token, or for a pick the nearest airport's id. A bare `UR` uses the room's primary airport; with none it answers `NO LOCATION PICKED`.
- **WX store**: keyed by station only (the METAR broadcast is room-wide), last entry wins; `FacilityId` stays on the record for shape parity. No snapshot schema bump (additive nullable list, as C2 and C3).
- **WX expiry**: an entered report stands until the first of (a) the next :53 routine instant after its entry, in sim time (`SessionStartUtc + ElapsedSeconds`), which replay reproduces, and (b) in a live room, the station's issuer re-issuing it (`MetarIssuer` exposes the station's last-issued time; a SPECI counts). The typed HHMM stays in the text only.
- **WX reach** (user 2026-09-29): entered reports stay in ERAM. They never reach YAAT clients or vStrips (no `WeatherChangedDto` change, no overlay of `Metars`); the instructor sees the `WX` entry as an echoed ERAM command in the YAAT terminal, as CRC STARS commands are. They are stored, snapshotted, recorded and replayed, and expire as above; an ERAM readout shows them only if some `WR` form reaches the server (CRC answers `WR` itself from vNAS). The store is keyed by ICAO station id, so `WX OAK` and `WX KOAK` are one report.
- **Errors**: add `MsgFieldOmission` (`FIELD OMISSION`) and `MsgInvalidAltitude` (`INVALID ALT`), `source: coined`.

## C5 — Filing

### FP — Flight Plan Message

**ERAM**: proposed `FP 02 03 (04) 05 06 07 09 10 (11) (12 17)…`, active `FP 02 03 (04) 05 06 07 08 10 (11) (12 17)…`, at most 32 12/17 pairs (§C.2 p.538; §C.8 p.676–686, descriptor `FLIGHT PLAN`). A field 07 starting `P` makes the next field a requested altitude (09); one starting `D` or `E` makes it an assigned altitude (08). The field checks:

- 02 is `Laa(a)(a)(a)(a)`, `Ld` or `M`, meaning "aircraft identification missing" (§C.1 p.402) (`MsgFlidFormat`);
- 03 is `((d)(d)L/)La(a)(a)(/L)` with `H` as its indicator (`MsgTYPFormat`, `MsgSAIFormat`);
- 04 is octal (`MsgBeaconCodeFormat`);
- 05 is `Mddd` (1–500), TAS 1–3700 or `SC` (`MsgSPDFormat`);
- 06 is a fix, FRD or lat/long (`MsgFIXFormat`);
- 07 is `Ldddd` or `LXXdd` with L in `D E P` (`MsgTIMFormat`);
- 08 and 09 are altitudes (`MsgALTFormat`, `MsgRALFormat`, `MsgInvalidBlockedAltitude`);
- 10 is at least two characters, and a single element is a stereo tag (`MsgRTEFormat`);
- 11 starts with the clear-weather or overcast symbol (`MsgRMKFormat`);
- the 12/17 pairs run the AM field-reference checks.

**YAAT**: `DispatchVp` (`FlightData.cs` ~:1541-1585) files a plan through `RoomEngine.AmendFlightPlan` (recorded `RecordedAmendFlightPlan`). AM's parsers (`BuildAmFields`, `FlightData.cs` ~:494-604) cover fields 03, 04, 05, 08, 09, 10, 11 and the 12/17 fields AM models (TYP, BCN, SPD, ALT, RAL, RTE, RMK, NUM, SAI, EQP); 06 and 07 have no AM parser (06: `EramFields.ParseLocation` + `EramFixResolver`; 07: new, `EramFields.ParseHhmm` takes only `dddd`). Plans are born active (DM's summary, :947-951), and YAAT keeps no coordination fix (the AM.yaml `FIX` row is `na`).

**Behaviour**: one amendment on an aircraft in the room.

- An aircraft with no plan gets the full plan.
- An aircraft that already has a plan is refused `DUPLICATE FLID` (use AM to change it).
- `M`, or an ACID with no aircraft, is `FLID NOT STORED`, because YAAT cannot hold a plan with no aircraft.
- 04 assigns the code; without it a discrete code is drawn for the entering sector.
- 06 and 07 are checked, then dropped.
- A proposed entry's 09 becomes the filed and requested altitude.
- A stereo tag is `<tag> NOT ADAPTED`, since no vNAS ARTCC configuration carries stereo routes (`rg -i stereo docs/vnas-artcc-config-examples` finds none).
- There is no ownership gate, since no plan exists yet to own.

**State**: changes state, recorded as `RecordedAmendFlightPlan`.

**Errors**: `FLID FORMAT`, `TYP FORMAT`, `SAI FORMAT`, `BCN CODE FORMAT`, `SPD FORMAT`, `FIX FORMAT`, `TIME FORMAT`, `ALT FORMAT`, `RAL FORMAT`, `INVALID BLOCKED ALT`, `RTE FORMAT`, `RMK FORMAT`, `INVALID FIELD REF`, `{cofie} DUPLICATE FIELD REF`, `ILLEGAL SOURCE`, `NO DATA PRESENT`, `MINUS SIGN NOT VALID`, `FIELD MISMATCH`, `ACT FORMAT`, `WAK FORMAT`, `EQP FORMAT`, `SRV FORMAT`, `ALA FORMAT`, `FLR FORMAT`, `FLT FORMAT`, `NUM FORMAT`, `OTH FORMAT`, `OTH DUPLICATE INDICATOR`, `OTH NO DATA`, `{cofie} FORMAT`, `DUPLICATE FLID`, `FLID NOT STORED`, `{cofie} NOT ADAPTED`.

**Tests**: `EramConformanceFpTests`, one per check. Active and proposed plans both file. A replay reproduces the plan and the code.

**Size**: design.

### SP — Stereo Flight Plan Message

**ERAM**: an FP whose route is a stereo tag, a facility-adapted stored route (§C.2 p.565; §C.8 p.813–816, descriptor `STEREO FLIGHT PLAN`). It takes fields 02 (with `M`), 03, 05, 07, 08 or 09, 10 as a tag `La(a)(a)(a)(a)(a)(a)` (`MsgRTEFormat`), and 11, with FP's checks.

**YAAT**: no stereo-route adaptation exists (see FP).

**Behaviour**: run FP's parsers for SP's field set. When every check passes, refuse `<tag> NOT ADAPTED`, so a controller learns the facility has no such route rather than seeing a false ACCEPT.

**State**: none.

**Errors**: `FLID FORMAT`, `TYP FORMAT`, `SAI FORMAT`, `SPD FORMAT`, `TIME FORMAT`, `ALT FORMAT`, `RAL FORMAT`, `INVALID BLOCKED ALT`, `RTE FORMAT`, `RMK FORMAT`, `{cofie} NOT ADAPTED`.

**Tests**: `EramConformanceSpTests`, one per check, and a well-formed entry answering `NOT ADAPTED`.

**Size**: multi-file (after FP).

### C5 rulings (2026-09-30, grounded, not asked)

- **Filing path**: `FP` builds one `FlightPlanAmendmentDto` and calls `RoomEngine.AmendFlightPlan`, as `DispatchVp` does, carrying `BeaconAssignedByFacilityId`/`SectorId` as VP does so the drawn code replays. `RecordAndDispatchFlightPlan` (the STARS/CRC create path) is not used: `CreateFlightPlanCommand` carries no speed, beacon, remarks or requested altitude.
- **Dispatch**: `FP`/`SP` re-parse the raw elements like `AM` (`"AM" => DispatchAm(elements)`), not as trailing-FLID verbs. New file `CrcClientState.Eram.Filing.cs`.
- **Duplicate**: `DUPLICATE FLID`, as above (VP's `DUP NEW ID` stays VP's). **Ownership**: none, as above.
- **Fields 08/09**: an active entry's 08 goes through `AmendAmAltitude` with AM's semantics; a proposed entry's 09 through `AmendAmRequestedAltitude` and also sets the filed altitude, as above.
- **Unmodelled 12/17 fields** (ACT, WAK, SRV, ALA, FLR, FLT, OTH): handled exactly as `AM` handles a reference to them today; their format errors cannot fire and are `na` in `FP.yaml` with that reason.
- **SP**: resolves no aircraft; a well-formed entry answers `<tag> NOT ADAPTED` whatever the ACID. `SP.StereoFlightPlan` is an implemented variant key with no success path; `EramFeedbackConventionTests` carries a refusal row for it.
- **Echo**: C5 lands after the ERAM terminal echo (MAIN.md "Echo ERAM commands"), so `FP`/`SP` need no echo code of their own.

## Questions for ERAM controllers

Only the points the SRS appendices, the CRC manual and the decompiled CRC leave open. Each gives the guess the design goes with.

1. **RK readout.** When you enter `RK`, `RK 55 56` or `RK INT`, what does the Response Area show, word for word? Our guess: `CA FUNCTION ON`, then `CA DISPLAY OFF 55 56`; with named sectors, one line each (`55 CA ON`); `MCI` in place of `CA` for `RK INT`.
2. **UR readout.** What does `UR` print: which altitudes when you give none, whether temperatures are shown, and the layout? Our guess: a header with the location, then one line per FD level (030 to 390) as `altitude direction/speed`, true direction to 10°, no temperature.
3. **Sector messages at the R-position.** When an `SM` message arrives, you see it in the Time View until acknowledged. Is `SM DE` at your position how you acknowledge or clear it, and does plain `SM` put the text in the Response Area? Our guess: yes to both. The message also arrives as a text message, because the simulated display has no notification area.
4. **CA display off for a sector.** Does `CA 55 OFF` only stop sector 55's display from showing conflict alerts, or does it also stop alerts on tracks sector 55 owns from showing at other sectors? Our guess: only sector 55's own display.
5. **FP for an aircraft that already has a plan.** What does ERAM answer when you enter an FP for an aircraft ID that already has an active flight plan in your centre? Our guess: it is refused as a duplicate, and you amend the existing plan with AM instead.
6. **RM feedback.** After `RM <ACID>` on a flight whose route converts cleanly, do you see anything beyond ACCEPT (a readout of the converted route, a route display)? Our guess: ACCEPT only.
