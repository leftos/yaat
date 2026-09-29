# ERAM `DM` (Departure message): design

The design for the parts of `DM` that YAAT does not handle yet: fields 26, 07 and 08, and the `/OK` and `*` suffixes. It follows the ruling in [eram-srs-findings.md](eram-srs-findings.md) ("`DM`: implement every field"). The field checks and error IDs are in [`docs/eram/commands/DM.yaml`](../eram/commands/DM.yaml); the error texts are in [`error-responses.yaml`](../eram/error-responses.yaml).

## Sources

- **SRS** (`ERAM_EDSM_SRS_210.04_V1B2`, see [docs/eram/README.md](../eram/README.md)). C.8 DM (printed pp. 664–666) gives syntax only. C.1 (pp. 405–408, 446):
  - Field 07 (Coordination Time) is a type letter plus `dddd` or `XXdd`; in a DM the letter "must be omitted, and is presumed to be D".
  - Field 26 (Departure Point): "Any legal fix … may be used".
  - `/OK` on field 02 or 26 "causes the eligibility checks to be bypassed".
  - `*` on field 02 "inhibits the use of preferred routes"; on field 26 it "inhibits application of ADRs". `⊕` does the same for IERRs/ERRs/RIEEs.
  - App. D.1–D.2 (pp. 866–867): the flight plan readout has Coordination Fix and Coordination Time (6 chars) columns, between Airspeed and Altitude.
- **vatsim-server-rs** `TODO.md:278-288`: coordination time is a letter plus `hhmm` at the coordination fix; P proposed, D departed, E en route, A ARTS arrival, F flush (processed as P).
- **CRC** (decompiled): `FlightPlanStatus` is read only by the STARS TAB list (`SystemListTab.cs:40`, Proposed plans without a track) and VFR list (`SystemListVfr.cs:48`). `ActualDepartureTime` is stored (`FlightPlan.cs:42`) and round-tripped by the FPE (`FlightPlanEditorViewModel.cs:447,698`) but never displayed. The ERAM FDB has no coordination time. `docs/crc/eram.md` §DM Command documents `DM <ACID>` only.
- **7110.65** §2-3-3 blocks 14, 18, 19: departure times are posted on strips.

## YAAT today

- `D:/yaat-server/src/Yaat.Server/Hubs/CrcClientState.Eram.cs:124` routes `DM` to `DispatchDm(firstCallsign, firstPickKind)`. `:457-459` lists `DM` in `TrailingFlidVerbs`, so the last token is taken as the FLID. `DM`'s format puts field 02 first, so every operand is misread:
  - `DM AAL123 250` treats `250` as a CID.
  - `DM AAL123/OK` answers `MESSAGE TOO SHORT`.
  - `DM AAL123*` answers `FLID FORMAT`.
- `CrcClientState.Eram.FlightData.cs:947-961` (`DispatchDm`) resolves the FLID and returns `ACCEPT DEPARTURE` without changing anything. Its summary says "YAAT flight plans are created active", which is wrong.
- `D:/yaat-server/src/Yaat.Server/Simulation/DtoConverter.cs:207-208` makes a plan Proposed while the aircraft is on the ground and has never been airborne (`HasBeenAirborne`). `:255-256,301-302` send both departure times as 0.
- `StripMutations.cs:357,1002` prints the session clock as the vStrips proposed-departure time. `:575` leaves the arrival strip's coordination fix empty.
- The YAAT client shows neither a plan's status nor its coordination data (no `Proposed` or `HasBeenAirborne` hits under `src/Yaat.Client*`).

## Design

### Parsing (server)

1. Remove `DM` from `TrailingFlidVerbs`. `DM` re-parses its raw elements the way `RF` does (`DispatchRf`): field 02 is a pick or the first typed token, and the fields that follow are optional, in the order 26, 07, 08.
2. **Field 02** is one FLID or a slash-joined list (`multiple_flids: true`). The suffix grammar is `[*|⊕][/OK]`: a star must come before `/OK`. A standalone `/OK` token directly after field 02 or field 26 counts as that field's suffix, the precedent `SplitOverrideField` set for `RF`.
3. **Assigning each operand to a field.** The fields keep their order. Each operand goes to the first open field whose form it matches:
   - `dddd` or `XXdd` is field 07.
   - a field-08 form (`(d)dd`, `VFR`, `OTP`, `ABV/`, `B` blocks, `alt/fix/alt`) is field 08.
   - a token with a letter is field 26.
   - An operand that fits no open field is `{cofie} FORMAT`.
   - More than three operands is `MESSAGE TOO LONG`.

   A fix name made only of digits cannot exist, which is why `1230` and `050` are never read as field 26.
4. The checks run in DM.yaml's order, and every check runs before anything is recorded:

| Check | Error | Text |
|---|---|---|
| FLID checks (existing `ResolveEramFlid`) | MsgFlidFormat / NoTBFlightID / IllegalFlightID / FLIDNotStored / FLIDDuplication | `FLID FORMAT` / `INVALID PICK` / `ILLEGAL FLID` / `FLID NOT STORED` / `DUPLICATE FLID` |
| Flight ID `M` | MsgAIDMissing | `AID MISSING` |
| Field 26 form or unknown fix (`EramFields.ParseLocation`) | MsgCofieFormat | `{cofie} FORMAT` |
| Suffix misplaced: on field 02 while field 26 is present, or `/OK` before `*` | MsgCofieFormat | `{cofie} FORMAT` (the whole token) |
| Several FLIDs together with 26, 07 or 08 | MsgMultipleFLIDsNotAllowed | `MULTIPLE FLIDS INVALID` |
| Field 07 form, hours above 23 or minutes above 59, `XX` above 99 | MsgTIMFormat | `TIME FORMAT` (a new `EramError.TIMFormat`) |
| Field 08 (`EramFields.ParseAmAltitude`, AM's full grammar, which matches DM's rule row for row) | MsgALTFormat / MsgInvalidBlockedAltitude | `ALT FORMAT` / `INVALID BLOCKED ALT` |
| Eligibility, skipped by `/OK` (`RejectIfNotEditable`) | YaatSessionNotActive / YaatNotYourControl | `SESSION NOT ACTIVE` / `NOT YOUR CONTROL` |

   A multiple-FLID `DM` is all or nothing: every flight is resolved and checked, and the first failure refuses the whole entry.

### Meaning (sim)

The SRS's Coordination Fix and Coordination Time belong to the SystemPlan (D.1), so they go on the flight plan. Add `AircraftFlightPlan.DepartureMessage`, a `DepartureMessage(string Fix, TimeOnly Time)` or null. A new plan filed with `DA` or `VP` clears it.

| Field | Decision |
|---|---|
| (none) | The DM is the activation. `DepartureMessage = (plan departure, current sim minute)`. |
| 26 | `Fix` is field 26 as typed, upper-cased, without its suffixes. The route and the departure airport are left alone. Omitted: the plan's departure airport. |
| 07 | Type D (SRS C.1). `dddd` is that time. `XXdd` is the sim clock (`SimScenarioState.SimTimeUtc`, truncated to the minute) plus `dd` minutes, the C.1 reading of "relative to the current clock time". The time is not checked against a window, since C.8 has no such check. Omitted: the current sim minute. |
| 08 | Amends the assigned altitude on the flight plan through `RoomEngine.AmendFlightPlan`, the path `QZ` uses (`CrcClientState.Eram.Altitude.cs:79-86`). |
| `/OK` | Skips the eligibility check for every named flight. |
| `*` / `⊕` | Accepted and validated. They change nothing: YAAT applies no preferred routes, ADRs or IERRs. |

- **A departure not yet airborne:**
  - The plan turns Active. `ToFlightPlanStatus` becomes `Active` when `HasBeenAirborne || DepartureMessage is not null`.
  - The aircraft itself is not affected: a DM is flight-data bookkeeping and clears nobody for takeoff.
  - Takeoff later changes nothing further.
- **An airborne flight** (already Active) and **a landed flight:** the DM is accepted and overwrites `DepartureMessage`.

### Displays

- **ERAM FDB**: only field 08 shows, through `ParsedAltitude`, as for `QZ`. The status and the time have no place on the FDB.
- **QF flight plan readout** (`FlightPlanReadout`, `FlightData.cs:129-144`): once a DM has been entered, `<fix> D<hhmm>` is inserted after the speed, the position D.2 gives the columns. Without a DM the readout is unchanged.
- **CRC flight plan DTO**:
  - `Status` becomes Active, so the STARS TAB list drops the row. The STARS VFR list keeps a VFR plan only while it has a target or a track.
  - `ActualDepartureTime` = `hhmm` as an integer (0 without a DM).
  - `AircraftChangeTracker`'s fingerprint (`:142`, `:983`) already carries `Status`. It gains the departure time.
- **vStrips**: no change. Tower strips print once and a DM does not reprint them. A field-08 amendment reprints exactly as a `QZ` does.
- **YAAT client**: no change.

### Replay

- The live handler resolves every relative time and default first, then records:
  - Field 08, when present, is a `RecordedAmendFlightPlan`, the record `AmendFlightPlan` already writes.
  - Then a `RecordedEramEntry(elapsed, callsign, "DM {hhmm} {fix}", null)`, one per flight.
- `EramEntryEngine.Apply` gains a `DM` case that writes `DepartureMessage` and nothing else. The time is absolute in the record, so a replay never reads the clock.
- `AircraftSnapshotDto` gains the nullable pair, restored by `AircraftState` (`:451`, `:560`). Check `docs/snapshots-and-replay.md` on whether the schema version needs a bump.
- The engine's grammar summary gains the `DM` form.

## Tests

yaat-server, `CrcEramFlightPlanTests`:

- `Dm_ExistingFlid_GracefulAccept` asserts `Status` flips Proposed→Active on the ground.
- `DM AAL123 250` amends the assigned altitude to FL250 (the regression listed in the findings).
- `DM AAL123 OAK`, `DM AAL123 1230` and `DM AAL123 XX05` set the fix and time. The QF readout shows `OAK D1230`.
- `DM AAL123/OK` and `DM AAL123 /OK` both override on another position's track. Without `/OK` the answer is `NOT YOUR CONTROL`.
- `DM AAL123*`, `DM AAL123*/OK` and `DM AAL123 OAK*/OK` are accepted.
- Refusals: `DM AAL123/OK*` → `AAL123/OK* FORMAT`; `DM AAL123* OAK` → `AAL123* FORMAT`; `DM AAL1/UAL2 1230` → `MULTIPLE FLIDS INVALID`; `2460` and `1275` → `TIME FORMAT`; `050B040` → `INVALID BLOCKED ALT`; `ZZZZZ` and `12345` → `{token} FORMAT`; `DM AAL123 050 1230` → `1230 FORMAT`; four operands → `MESSAGE TOO LONG`.
- `DM AAL1/UAL2` activates both flights, and one unknown FLID refuses both.
- `EramFeedbackConventionTests` and `EramReferenceConformanceTests` stay green.

yaat (Sim):

- `EramEntryEngine.Apply("DM 1230 OAK")` sets `DepartureMessage`.
- A snapshot round-trip keeps it.
- A recording replays the DM, and a rewind to before it restores Proposed.
- `ToFlightPlanStatus` is covered for DM'd, airborne and neither.

## Questions for ERAM controllers

Each question is followed by the answer this design assumes.

1. When you enter a departure time relative to now in a DM (`DM AAL123 XX05`), does it mean five minutes from now or five minutes ago? *Assumed: from now, the literal reading of "relative to the current clock time".*
2. If you DM a flight that is already active (auto-departed by the TRACON, or airborne), does ERAM take it and replace the departure time and fix, or reject it? If it rejects, with what message? *Assumed: accepted, and it overwrites.*
3. When you put a departure point in a DM (`DM N123AB SAC`), does ERAM only record it as the coordination fix on the readout and strips, or does it also rebuild the converted route to start at that point? *Assumed: coordination fix only.*
4. Which positions can DM a proposed departure without `/OK`, and what error do you see without it? *Assumed: anyone while no sector controls the flight; otherwise only the controlling sector, answered NOT YOUR CONTROL.*
5. Does ERAM refuse a DM whose time is in the future, or more than a few minutes in the past? *Assumed: no window check.*
