# ERAM SRS gap hunt — findings (raw, to be cleaned up)

Candidate ERAM bugs and gaps found by reading the whole *ERAM EDSM SRS Vol 1 Book 2* (`ERAM_EDSM_SRS_210.04_V1B2_SDR-088All_Commands.pdf`, 1006 pages: appendices A–I), not only the Appendix C command tables that `docs/eram/` already extracts. Citations give the SRS section and **printed** page. To read a page: `pdftotext -layout <pdf> eram.txt`, split on `\f`; with 1-based page files, file number = printed page + 16.

This volume is the appendices only. Rules such as track update cadence (#464) may live in other SRS volumes this file cannot see.

Status per item: **unverified** = agent finding not yet re-checked against the code; **verified** = re-checked; **question** = needs a ruling from the reporter or the maintainer.

## Next steps

The work order is the **ERAM — #1 priority** list in [MAIN.md](./MAIN.md); this file holds the detail.

1. #465–#468 are fixed; their sections are gone (git history has them).
2. **Investigate** #464: find a source for the 12 s rate (the CRC manual, CRC's decompiled display refresh, the reporter).
3. **Verify** each `unverified` entry below against the code. Mark it verified, or drop it with the reason.
4. **Rulings** — settled, see [Rulings](#rulings-user-2026-09-28) below.
5. **Turn this file into a fix list** grouped by owning file, add a MAIN.md line per group, then delete this file. The brief grouping is under [Brief grouping](#brief-grouping-verified-2026-09-28).
6. **Cross-check `vatsim-server-rs`** (user steer 2026-09-28): its ERAM emulation (`X:/dev/vatsim-server-rs`, `crates/server/src/clientstate/eram.rs`, `crates/radar_state/src/eram_state.rs`; pull first) may hold clues for every item here. Read it before briefing each group.

## Rulings (user 2026-09-28)

- **C.8 commands with no `na`**: all in scope — flight-plan tools (`FR`, `FP`, `DQ`, `RM`, `SP`), conflict alert (`CA`, `RK`; drop `CA.yaml`'s `na`), messages and strips (`SM`, `RS`), weather (`SW`, `UR`, `WX`). Each needs its own design pass before a brief.
- **QS free text**: accept the §C.1 field 155 set — A–Z, 0–9 and `- + = * / _ . ,` (the arrows and the overcast symbol cannot be typed). The tokeniser must keep `/ddd` and `/Sddd` speeds apart from a `/` inside free text.
- **`LA`/`LB` text**: match CRC, else the SRS. CRC prints nothing of its own (the decompiled client has no LA/LB text; the server fills the response area), so follow §F.5 Tables 76–77: `RANGE * 82.6 NM / BEARING * 248 DEG MAG / FROM 1ST TB ENTRY / AT 154 KNOTS 1 HR 35 MIN`. `EramRangeReadoutTests.FormatFlyingTime_IsMinutesSeconds` changes with it.
- **Per-sector display state**: key leader direction, leader length, DRI halo and dwell lock by (facility, sector) now, like `OnFrequencySectorIds`, and record the dwell lock as a `RecordedEramEntry` carrying the sector key and an absolute value (`DWELL <facility> <sector> 1|0`, never a toggle, so a replay cannot desync on a missed entry).
- **#464 update rate**: CRC draws every ERAM update it receives (`Eram.Tracks/TrackManager.cs` `ReceiveTrack` has no timer), so the 1 s rate is yaat-server's (`RoomTickLoopService` 1 s tick; `AircraftChangeTracker.EramTargetFingerprint` changes every second for a moving aircraft). Send the position part of the ERAM target and track on a 12 s sweep with a **per-aircraft stagger** (a stable callsign hash mod 12, sim-elapsed time, not wall clock); state changes (handoff, HSF, VCI, point-out, symbol, beacon) still go out on the next tick; a newly visible aircraft is sent at once. The **ERAM history trail follows the sweep**: one dot per 12 s update, from an ERAM-specific sample (today's 5 s `PositionHistory` stays for STARS and YAAT's radar). STARS is untouched. Keep `EramCoastSeconds` (24 s = 2 sweeps) consistent. Red test model: `tests/Yaat.Server.Tests/CrcAcceptedIndicatorTests.cs` (`RoomEngineTestHarness`, `RecordingWebSocket`), asserting ≤ 2 position `ReceiveEramTracks` in 12 broadcasts for a moving aircraft and a handoff sent on the next tick.
- **MCI alert floor**: the ARTCC's `ConflictAlertFloor` when non-zero, else the SRS 12,500 ft; the 99,500 ft ceiling applies.
- **1200 code**: never an MCI — excluded from the MCI symbol (it draws as VFR) and from conflict-alert intruder eligibility. This departs from `docs/crc/eram.md:358`.
- **Vertical-conformance latch**: set when within ±200 ft of the assigned altitude (a block altitude uses its own floor and ceiling), cleared only when the assigned altitude changes. Latched in a Yaat.Sim post-physics step for replay determinism.
- **Handoff retract**: show `O` plus the retracted recipient to the initiator (send the recipient as the recent-handoff peer).
- **SPC blink**: a named 30 s constant after the code first appears (latched like `IdentStartedAt`), in the target fingerprint so a stationary aircraft re-sends.
- **`QL` limit of 5**: per entry; the toggled set may grow past 5 over several entries.
- **`QB` multiple FLIDs**: apply per flight for the equipment-qualifier and voice variants; refuse code assignment with `MULTIPLE FLIDS NOT ALLOWED`.
- **`DM`**: implement every field — 26 (coordination fix), 07 (time), 08 (altitude) and the `/OK` and `*` suffixes. Fields 26 and 07 need a design pass (what a coordination fix and a departure time change in the sim) before a brief; the draft is [eram-dm-design.md](./eram-dm-design.md).
- **`QB` code to several FLIDs**: refused with the SRS text the YAML carries for `MsgMultipleFLIDsNotAllowed`, `MULTIPLE FLIDS INVALID` (landed with B5).
- **B3 latch** (grounded in CRC `BaseDataBlockRenderObject.GetVerticalConformance`): evaluated at once when the assignment is first seen; no assigned altitude counts as reached; a block sets within floor − 200 to ceiling + 200 and an ABV altitude from altitude − 200 up (CRC's bounds); measured on the aircraft's own altitude, not evaluated while the track is coasted or frozen or Mode C is absent; a change is keyed on the value (a same-value reassignment is no change) and the key is snapshotted; a new `StepId.EramVerticalConformance` after `AltitudeFixPassage`; no schema bump.
- **B1**: the retract `O` shows the initiator's own sector (CRC's `FdbRenderObject.GetFieldESectorId` prints the owner's sector, never the peer's, so the recipient cannot be shown; see [eram-open-questions.md](./eram-open-questions.md)). `QT /OK` marks `K` only when the entry has a scenario. QQ field 76 is exactly `[L|P|R]ddd` > 0, one per entry, else `ALT FORMAT`; a letters-only token other than `L` is `<token> ILLEGAL ACTION`; a token starting with `/` is field 513, valid as `/` plus two of A–Z or `/`, else `<token> FORMAT`. QS field 60 and QS multiple FLIDs are a separate item.
- **B6** (SRS §F.5 Tables 76–77, `QL.yaml`): QL checks every token's format (`(d)dd`, 1–128, or `ALL`) before the count, counts raw tokens (at most 5 per entry), checks no adaptation, and stores the sector by number (`044` → `44`), matched by number. LA/LB print one item per line joined with `\n`: `RANGE * <d.d> NM`, `BEARING * <ddd> DEG MAG` (`DEG TRUE` with `T/`, and always TRUE for a radar site), then `FROM 1ST TB ENTRY` (LA), `RADAR SITE <id>` (LA field 13) or `FROM TB TO FIX <name>` (LB; no name for a picked fix), then with a speed `AT <kt> KNOTS [<h> HR ]<m> MIN`, minutes rounded to the nearest, `<h> HR` alone on a whole hour. LB's bearing runs from the track to the fix, as the text says. A three-letter operand that matches an ASR site's `AsrId` is the site, ahead of a fix of the same name; the SRS's `2116 ACP` line has no data source and is omitted. Distances use the invariant culture.
- **B4 conflict pass**: the 5 s pass is gated on `(int)Scenario.ElapsedSeconds % 5 == 0` (global phase, no new state), with suppression changes still reported every second. An IFR/MCI pair first detected already inside minima (present positions: 5/3 NM lateral and under 1,000 ft) never alerts while it stays inside; an alert already up is kept. The MCI floor is the owning facility's `ConflictAlertFloor`, else 12,500 ft, inclusive, with a 99,500 ft ceiling, and the **MCI symbol uses the same floor** (one helper feeds both; outside the band an uncorrelated non-1200 target draws as `UncorrelatedBeacon`, per CRC's manual Table 1). The floor is the tracked side's ERAM owner facility's; a STARS-owned side takes the 12,500 ft default. A pair whose intruder leaves the band drops an active alert too. A 1200 code never alerts, flight plan or not. STARS CA is untouched.

Verified against the code 2026-09-28 (explorer pass): coast marking the previous owner `K` is **not a bug** — `ApplyCoast` refuses a track owned by another position, so it never has a previous owner. Ground speed 0 matters for the ERAM target only (Field E reads `target.GroundSpeed`; the track's speed drives only the vector). The `AM.yaml` 918 claim is wrong as worded — SWIM and scenario remarks carry REG/, PBN/, DOF/ as unparsed text in `AircraftFlightPlan.Remarks` — but nothing parses them; reword the `na` reason.

## Clues from vatsim-server-rs (2f3a6b0)

It mirrors the FAA SWIM feed and changes nothing in the NAS, so most controller-action fields are hardcoded and QQ, QB, DM, LA/LB, conflict alert and the C.8 list are absent. What it does add:

- **Cadence** (#464): SWIM ERAM data arrives ~12 s per centre (`docs/per-source-display-plan.md`); `crates/server/src/clientstate/util.rs` has `ERAM_HISTORY_INTERVAL_SEC = 12` (STARS 6) and a 24 s ERAM staleness; the 12 s send throttle is an open TODO there (`TODO.md` "send eram updates only every 12s"). It re-sends ERAM targets on every trigger (STARS updates, admin toggles) and gates only the history dots on an ERAM-sourced update — so B8 must keep other triggers from re-sending ERAM position.
- **History dots**: sent over UDP (topic `EramTargetHistories`), oldest first. CRC's history dictionary is keyed by `Id` and **never evicts** (`docs/stars-history-warmup-plan.md`), so each dot needs a stable id (callsign + sweep timestamp), never a fresh random one — check what `DtoConverter.ToEramTargetHistory` sends today.
- **QF**: its readout is `"{zulu}\n{cid} {callsign}({owner sector}) {type} {assigned beacon} {speed} {route} {remarks}"`; its comment's sample carries the assigned altitude after the speed (`… 2370 0 190 KLGA.TNNIS6…`) though the code drops it. A STARS owner prints as TRACON id + subset + sector (`Q2B`).
- **QL**: adds sectors and drops invalid ones silently, with a "limit the QL sectors to 5" TODO — consistent with the 5-per-entry ruling; YAAT refuses invalid ones instead.
- **FLID**: purely by length (3 = CID, 4 = beacon, else ACID); no `dLd`, no lists.
- **Reached assigned altitude**: symmetric ±200 ft, computed once when the track DTO is built (an accidental latch; an earlier climb-only rule was dropped). Matches the latch ruling.
- **Per-sector state**: leader and dwell are global per aircraft there, a known shortcut ("per-sector eram config?"); FDB-open, QL, RD, QU are per sector. Nothing to copy for B7.
- **1200**: it removed the ERAM VFR target symbol (every target `CorrelatedBeacon`) with no reason given; YAAT's ruling (1200 draws as VFR, never MCI) stands.
- **DM**: only coordination-time letter meanings (`TODO.md`: A, D departed, E, F, P proposed) — input to the DM design pass.
- It answers every entry `is_success = true` with empty feedback; YAAT's refusals stay.

## Brief grouping (verified 2026-09-28)

| Brief | Source files | Covers | Order |
|---|---|---|---|
| B4 conflict pass | Sim `EramConflictDetector.cs`, `Simulation/SimulationEngine.Tick.cs`, `AircraftTransponder.cs` (`IsVfrCode`); Srv `DtoConverter.cs` (`ComputeEramSymbolType` floor) | 5 s cadence (clock-derived, suppression changes still every second), no immediate IFR/MCI alert, floor/ceiling, 1200 not an intruder | any time |
| B5 DM | Srv `CrcClientState.Eram.FlightData.cs` (`DispatchDm`), `CrcClientState.Eram.cs` (`TrailingFlidVerbs`) | fields 26/07/08, `/OK` and `*` | after [eram-dm-design.md](./eram-dm-design.md) is ruled |
| B6 QL and LA/LB | Srv `CrcClientState.Eram.Display.cs`, `CrcClientState.Eram.Readouts.cs`; Sim `Data/Vnas/ArtccConfig.cs` (`AsrId`) | QL field 214 + number matching, LA field 13, LA/LB SRS text | concurrent with B2 |
| B1 entry engine and QQ | Sim `Commands/EramEntryEngine.cs`, `Commands/TrackEngine.cs`; Srv `CrcClientState.Eram.cs`, `CrcClientState.Eram.Altitude.cs` | `QT /OK` `K`, retract `O`, QQ 76/36/513 + the cap of 15 (`EramFlid.MaxFlidListLength`, `MESSAGE TOO LONG`, as QB/QU), QS C.1 charset | any time |
| B7 per-sector state | Srv `DtoConverter.cs`, `CrcClientState.Eram.cs`; Sim `AircraftEramState.cs` + snapshot DTO, `EramEntryEngine.cs` (`DWELL`) | per-sector leader/DRI/dwell + dwell recording | after B1 and B3 |
| B8 #464 sweep | Srv `CrcBroadcastService.cs`, `AircraftChangeTracker.cs`, `DtoConverter.cs` (history) | 12 s staggered sweep, ERAM history; the SPC blink edge (`CrcBroadcastService` ORs `DtoChangeFlags.EramTarget`) is a state change and stays outside the sweep | any time |

B2 and most of B5 landed; the QF leftovers are under [QF beacon fallbacks](#qf-beacon-fallbacks-and-the-empty-altitude).

## Filed issues

### #464 — ERAM update rate should be 12 s, not 1 s
- **Not in this volume.** The C.7 dynamic parameter table has no track update rate, and no other appendix states one. The rate is an ERAM surveillance and display rule from Book 1 or the CRC manual. Check `docs/crc/eram.md` and CRC's decompiled display refresh logic, then decide from the reporter's word plus CRC's behaviour.

## Command validation candidates

### QQ interim altitude (field 76) is barely validated
- **Severity**: bug · **Owner**: Yaat.Sim (parse) + yaat-server (field 513) · unverified
- **SRS**: §C.8 QQ (p.758). Field 76 must be `ddd`, `Lddd`, `Pddd` or `Rddd` with ddd > 0 (MsgALTFormat). Field 36 must be `L` (MsgCofieIllegalAction). Field 513 is `/xx` (A–Z, `/`). A QQ with no field 36 deletes "the interim or procedure altitude … (whichever is present)", and says nothing about the CERA. YAAT's delete already matches this, which closes the #466 side-question.
- **YAAT now**: `EramEntryEngine.ApplyQq` (:846-892) uses `int.TryParse` per token and the first parsable token wins. As a result:
  - `QQ 000`, `QQ 5`, `QQ -5` and `QQ 12345` are all stored.
  - In `QQ 110 ABC`, the `ABC` is ignored, and in `QQ X 110` the `X` is skipped.
  - A bad field 36 never produces a refusal.
  - On the server side, `CrcClientState.Eram.Altitude.DispatchQq` (:116-191) splits any slash token other than `/TT` or `///` as a FLID list, so a field 513 answers a FLID error.
  - QQ has no conformance test.
- **Fix sketch**: parse field 76 as exactly `[L|P|R]ddd` with ddd > 0, one per entry. Refuse a field 36 other than `L`. Add `EramConformanceQqTests`.

### QL has no field 214 checks, and quick-look sector matching is by string
- **Severity**: bug · **Owner**: yaat-server · unverified
- **SRS**: §C.8 QL (p.744): "Field 214 … Must be in the format (d)dd, and in the range of 01 - 128", and "A maximum of 5 Field 214s is permitted" (MsgFieldInErrorEnterValidFieldInformation, MsgMessageTooLong).
- **YAAT now**: `CrcClientState.Eram.Display.DispatchQl` (:258-295) toggles any upper-cased token into a set, so `QL BANANA`, `QL 999` and seven sectors are all accepted. `DtoConverter.cs:918` matches with `quickLookSectors.Contains(sectorId)`, a string compare, while every other sector match compares by number (`EramFields.cs:516-531`). `QL 044` against sector `44` therefore silently does nothing. `ALL` is a YAAT addition. QL has no conformance test.
- **Fix sketch**: validate each token as `(d)dd` in 1–128, or `ALL`, with at most 5. Normalise each token to the adapted sector id by number before storing it.

### DM ignores fields 26/07/08 and the `/OK` and `*` suffixes
- **Severity**: gap (low priority; CRC docs show only `DM <ACID>`) · **Owner**: yaat-server · unverified
- **SRS**: §C.8 DM (p.664–666). DM takes an optional field 26 (fix), 07 (time) and 08 (altitude), and a `/OK` or `*` suffix on field 02 or 26.
- **YAAT now**: `DispatchDm` (`FlightData.cs:934-942`) takes no arguments, and because `DM` is a trailing-FLID verb:
  - `DM AAL123 250` treats `250` as a CID.
  - `DM AAL123/OK` answers MESSAGE TOO SHORT.
  - `DM AAL123*` answers FLID FORMAT.
- **Fix sketch**: parse the fields and suffixes, or mark them `na` in `DM.yaml` ("YAAT plans are born active").

### QF beacon fallbacks and the empty altitude
- **Severity**: gap (small) · **Owner**: yaat-server
- **SRS**: App. D.1 Table 34 (p.866–867): the beacon column holds the Assigned code, else the Requested code, else the Last Facility Assigned code after an outbound handoff.
- **YAAT now**: `CrcClientState.Eram.FlightData.BeaconReadout` prints `Transponder.AssignedCode` or `----`; the full readout prints the present altitude when no assigned altitude is set, where the field-referenced ALT readout prints `-`.
- **Fix sketch**: add the two beacon fallbacks; decide the empty assigned-altitude text.

### QS free-text character set and the knots floor
- **Severity**: unverified (the SRS contradicts itself on the character set) · **Owner**: Yaat.Sim
- **Character set**: §C.1 field 155 (p.486) allows A–Z, 0–9 and `- + = * / _ . ,`, plus the up and down arrows and the overcast symbol. C.8, as recorded in `QS.yaml`, says "1-8 non-special characters". `EramEntryEngine.cs:1008` accepts A–Z and 0–9 only.
- **Knots floor**: `/ddd` and `/Sddd` have no lower bound; the 070–380 range applies to the uplink only (p.486). `EramEntryEngine.cs:1076` rejects anything below 100, so `/075` is refused. This fix belongs with #467.

### C.7 dynamic parameters YAAT models
The C.7 table (Table 31, p.605–623) has no track update rate, data-block cadence, conflict lookahead, or handoff or point-out timer, so it cannot answer #464 or #465.

| Parameter | SRS | YAAT | Verdict |
|---|---|---|---|
| URDT Unspecified Route Display Time | 20 min | `Route.cs:400` 20 min | matches |
| RDRI Route Display Request Interval | 30 s | `EramRouteLineSeconds = 30` (`Route.cs:72`) | matches in value (the SRS defines it as a request interval, not a lifetime) |
| IPOD Interfacility Point Out Auto Drop | 15 min | no timer | not modelled |
| APSB / APSC conflict-alert floor, MCI floor | 0 ft / 12,500 ft | no floor constant in `EramConflictDetector.cs` | unverified; check against the MCI path |
| INTC / INTF intruder ceiling / floor | 99,500 / 12,500 ft | not checked | unverified |
| CRDT / SRDT / PCRI code reassignment delays | 60 / 30 / 15 min | `BeaconCodePool.Release` frees a code immediately | low; the cursor rarely reuses a code |

### Smaller C.8 points
- QN offset should accept a Mode C Intruder ID (`dLd`) for a conflict data block (p.748–749). No MID handling was found.
- These commands have no `na` in `docs/eram/commands/` and read as undecided, not as out of scope, so each needs a product call: `FR`, `DQ`, `FP` (no fields extracted), `CA` (its `na` reason is doubtful given `EramConflictDetector`), `RS`, `RK`, `SM`, `RM`, `SP`, `SW`, `UR` and `WX`.
- `AM.yaml` marks the 918 indicators (REG/, PBN/, DOF/ …) `na` with "no VATSIM flight plan carries it", but VATSIM ICAO remarks do carry them. Check whether the plan's remarks expose them.
- C.8 describes no processing semantics: each command defers to "the B-level requirements", which are not in this volume.

## New candidates

### Taking a track (`QT /OK`, coast) gives the previous owner no `K-dd`
- **Severity**: gap · **Owner**: Yaat.Sim · unverified
- **SRS**: §E.3 Table 40 (p.887–888), the Field E `K-(d)dd` columns for "h/o acc (cntrl) ≠ init or rcv (assume cntrl)" and "≠ prev cntrl".
- **YAAT now**: `EramEntryEngine.ApplyTrack` (:557-579) and `ApplyCoast` (:632) call `StartTrack` but never `TrackEngine.MarkRecentHandoffAccepted(..., wasForced: true)`. `K` is set only on the POCONVERT path (:430) and on `ApplyForceHandoff` (`TrackEngine.cs:803`).
- **Fix sketch**: capture the previous owner before `StartTrack`. If it differs from the actor, mark the handoff as recently accepted and forced.

### Handoff retract gives the initiator no `O-dd`
- **Severity**: gap (low) · **Owner**: Yaat.Sim · unverified
- **SRS**: §E.3 Table 40 (p.888), "h/o acc (cntrl) = init (retract)".
- **YAAT now**: `TrackEngine.HandleCancel` (:254-263) clears the peer and timers but sets no recent-accept state.

### `LA` / `LB` readout text does not follow the SRS format
- **Severity**: unverified (check against a real CRC capture first) · **Owner**: yaat-server
- **SRS**: §F.5 Tables 76–77 (p.933–935): "RANGE * 82.6 NM / BEARING * 248 DEG MAG / FROM 1ST TB ENTRY / AT 154 KNOTS 1 HR 35 MIN".
- **YAAT now**: `CrcClientState.Eram.Readouts.cs:231,235` prints `"{dist:F1}NM {brg} {kt}KT {mm:ss}"`, and `FormatFlyingTime` gives minutes:seconds (1 h 35 min prints as `95:00`). `EramRangeReadoutTests.FormatFlyingTime_IsMinutesSeconds` pins this.

### `LA` field 13 (radar site) is not handled
- **Severity**: gap · **Owner**: yaat-server · unverified
- **SRS**: Table 76 (p.934), the last row, "… / 2116 ACP / RADAR SITE QUU".
- **YAAT now**: `DispatchLa` (`Readouts.cs:175-180`) accepts only two position operands, and a site ID resolves as a fix.
- **Fix sketch**: resolve the ID against the ARTCC config's ASR sites (already read at `CrcBroadcastService.cs:2662`).

### ERAM conflict pass runs every second, not every 5 s
- **Severity**: unverified (the source is the CRC manual; the SRS gives no cadence) · **Owner**: Yaat.Sim
- **Source**: `docs/crc/eram.md` (Conflict Alert Processing): "ERAM performs a conflict detection pass every 5 seconds".
- **YAAT now**: `SimulationEngine.Tick.cs:737-742` runs the pass every sim-second.

### Immediate alerts for IFR/Mode-C-intruder pairs
- **Severity**: unverified (the table is garbled in the text extraction) · **Owner**: Yaat.Sim
- **SRS**: §H.1 (p.953–954): "Immediate alerts are not reported for IFR/MCI pairs."
- **YAAT now**: `EramConflictDetector.cs:77-90` has one test for all alerts, and the Mode-C-intruder path in `SimulationEngine.Tick.cs` alerts even when the pair is already inside minima.

### Dwell lock, leader offset and DRI halo are per aircraft, not per sector, and the dwell lock is not recorded
- **Severity**: gap (matters only when two ERAM CRC sessions share a room) · **Owner**: yaat-server + Yaat.Sim · unverified
- **SRS**: §A.40 (p.210) and §A.41 (p.211), plus the A.1 Table 3 rows 562, 681 and 758 (pp.69, 80, 87), which treat emphasis and leader lines as R-position display state.
- **YAAT now**:
  - yaat-server `DtoConverter.cs:876-885` sends a single per-aircraft `LeaderDirection`, `LeaderLength`, `DriHaloType` and `IsDwellLocked` to every sector (its comment: "single-ERAM-sector training").
  - `CrcClientState.Eram.cs:191` toggles `IsDwellLocked` directly, with no sector key and no `ApplyAndRecord`, so a replay loses it. VCI, FDB-open and point-out minimize are per sector and recorded.
- **Fix sketch**: key these by (facility, sector) the way `OnFrequencySectorIds` is keyed, and record the dwell-lock toggle as a `RecordedEramEntry`. The missing recording is a determinism bug even for a single sector.

## Checked and consistent

- Quick Look eligibility (E.1 Table 36) for the state YAAT sends.
- Field B/C/B4 inputs. CRC builds the strings; its own shortfalls (the missing `F` B4, the unreasonable-Mode-C `X`) are CRC-local.
- Frozen, then coast, then normal symbol order (E.4). NONE for a standby aircraft with an assigned code. Field E sector formatting (built by CRC from `Owner`/`HandoffPeer`/`RecentHandoffPeer`).
- CRC has no Departure, Inbound, Hold, Conflict Alert, AHI, MRP or FEL view, no hold track symbol, and no CPDLC/TOC/PID indicators, so YAAT has nothing to send for B.13/B.20/B.21 and the related A.1 rows. The range data block shows the CRR distance only. CRC's 7 ERAM hub methods are all handled by the server.
- Appendix F UTM/alert messages, Appendix G sign-in and Appendix I composition qualifiers: not applicable on VATSIM (FDIO/ARTS coordination, sign-in, CPDLC).

## Coverage

| Range (printed pages) | Section | Status |
|---|---|---|
| 1–12, 13–260, 261–400 | Scope, App A, App B | read; the A.1 colour table was keyword-scanned for state rows; most App B figures did not extract (captions only) |
| 401–623, 846–868 | C.1–C.7, C.9, App D | read; C.1 in full, C.2–C.6 skimmed for behaviour; the C.3 and C.7 flag columns are garbled |
| 624–735 | C.8 part 1 | read; the YAML matches the SRS row for row (AM, CO, DM, HM, LA–LF, QA, QB, QF) |
| 736–845 | C.8 part 2 | read; the YAML matches; QL and QQ have no conformance tests |
| 869–882 | E.1–E.2 | read; Table 37 garbled, rebuilt from the prose |
| 883–914 | E.3–E.4 | read; Tables 42–53 (interfacility handoffs) garbled |
| 915–990 | F, G, H, I | read; G and the uplink parts of I skimmed as N/A; H.1 matrix garbled |
