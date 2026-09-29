# ERAM SRS gap hunt — findings (raw, to be cleaned up)

Candidate ERAM bugs and gaps found by reading the whole *ERAM EDSM SRS Vol 1 Book 2* (`ERAM_EDSM_SRS_210.04_V1B2_SDR-088All_Commands.pdf`, 1006 pages: appendices A–I), not only the Appendix C command tables that `docs/eram/` already extracts. Citations give the SRS section and **printed** page. To read a page: `pdftotext -layout <pdf> eram.txt`, split on `\f`; with 1-based page files, file number = printed page + 16.

This volume is the appendices only. Rules such as track update cadence (#464) may live in other SRS volumes this file cannot see.

Status per item: **unverified** = agent finding not yet re-checked against the code; **verified** = re-checked; **question** = needs a ruling from the reporter or the maintainer.

## Next steps

The work order is the **ERAM — #1 priority** list in [MAIN.md](./MAIN.md); this file holds the detail.

1. #465–#468 are fixed; their sections are gone (git history has them).
2. **Investigate** #464: find a source for the 12 s rate (the CRC manual, CRC's decompiled display refresh, the reporter).
3. **Verify** each `unverified` entry below against the code. Mark it verified, or drop it with the reason.
4. **Rulings needed**:
   - Which C.8 commands with no `na` are in scope.
   - The QS free-text character set (C.1 versus C.8).
   - Whether the `LA`/`LB` text follows the SRS or CRC's own output.
   - Whether per-sector display state is worth doing.
5. **Turn this file into a fix list** grouped by owning file, add a MAIN.md line per group, then delete this file.

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

### A beacon code is accepted as the FLID where the SRS forbids it (QB, LF)
- **Severity**: bug (small) · **Owner**: yaat-server · unverified
- **SRS**: §C.8 QB Code Modification (p.727) and Code/Qualifier Modification (p.732): "Must not enter a beacon code for flight identification" (MsgIllegalFlightID). The other QB variants (p.728–729) and LF (p.711) list only ACID, CID or pick (MsgFlidFormat).
- **YAAT now**: the shared `ResolveEramFlid` (`CrcClientState.Eram.cs:470-499`, `EramFlid.cs:20-30`) accepts a beacon code for every verb, so `QB 1301 1234` changes the code of the aircraft squawking 1234.
- **Fix sketch**: add a per-verb flag that disallows a beacon code.

### QB multiple-FLID variants reject `AAL1/DAL2` with a misleading error
- **Severity**: gap (small) · **Owner**: yaat-server · unverified
- **SRS**: §C.8 QB Discrete Code Request and Equipment Qualifier Modification (p.728–729) allow the multiple-FLID form, and `QB.yaml` records `multiple_flids: true` without a one-flight ruling.
- **YAAT now**: `CrcClientState.Eram.cs:79-88` never claims a slash token as the FLID, so the entry answers MESSAGE TOO SHORT.
- **Fix sketch**: apply the change per flight, or record a ruling and return a clearer error.

### DM ignores fields 26/07/08 and the `/OK` and `*` suffixes
- **Severity**: gap (low priority; CRC docs show only `DM <ACID>`) · **Owner**: yaat-server · unverified
- **SRS**: §C.8 DM (p.664–666). DM takes an optional field 26 (fix), 07 (time) and 08 (altitude), and a `/OK` or `*` suffix on field 02 or 26.
- **YAAT now**: `DispatchDm` (`FlightData.cs:934-942`) takes no arguments, and because `DM` is a trailing-FLID verb:
  - `DM AAL123 250` treats `250` as a CID.
  - `DM AAL123/OK` answers MESSAGE TOO SHORT.
  - `DM AAL123*` answers FLID FORMAT.
- **Fix sketch**: parse the fields and suffixes, or mark them `na` in `DM.yaml` ("YAAT plans are born active").

### QF readout shows the live squawk in the assigned-beacon column
- **Severity**: gap · **Owner**: yaat-server · unverified
- **SRS**: App. D.1 Table 34 (p.866–867). The column holds the Assigned Beacon Code, or the Requested code when none is assigned, or the Last Facility Assigned code after an outbound handoff. D.1 also puts the CID and the controlling sector (`UNK` when unknown) in the FLID column.
- **YAAT now**: `CrcClientState.Eram.FlightData.BeaconReadout` (:145) prints `ac.Transponder.Code`, so a wrong squawk, standby or 1200 reads out as if assigned. `AircraftTransponder.AssignedCode` already exists. The "cid-shortcut" mentioned in the comment at :132 is never printed.
- **Fix sketch**: print `AssignedCode`, and add the CID and controlling sector.

### FLID format checks are looser than §C.1 field 02
- **Severity**: bug (trivial) · **Owner**: yaat-server · unverified
- **SRS**: §C.1 field 02 (p.402–406) sets three rules:
  - An aircraft ID is `Laa(a)(a)(a)(a)` or `Ld`, so a two-character ID must be a letter then a digit.
  - A `dLd` CID may not use A, C, E, F, H or J, which are reserved for Mode C Intruder IDs.
  - "Up to 15 Flight IDs may be specified in a single command entry."
- **YAAT now**: `EramFlid.cs:44-58` accepts `AB`, `IsCid` (:60-73) accepts `1A2`, and no multiple-FLID list is capped (`Altitude.cs:145-153`, `Route.cs:313`, `Conflict.cs:76`).

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

### Standby transponder still reports an altitude
- **Severity**: bug · **Owner**: yaat-server · unverified
- **SRS**: §E.2 p.873, together with the B/C rules p.871–882. Mode C is the reported altitude only "if Mode C altitude for display valid"; otherwise there is no reported altitude. B4 shows `X` with Field C `XXX` when Mode C is disestablished, and `N` when it was never established.
- **YAAT now**: `DtoConverter.ToEramTrack` (:1057-1069) always sends `Altitude = motion.Altitude`. `ToEramTarget` already nulls it for Standby. CRC reads `track.Altitude` (`BaseDataBlockRenderObject.cs:127`), so it never shows `X`/`XXX` for a standby aircraft.
- **Fix sketch**: send a null track `Altitude` when Mode C is not reporting. A frozen or coasted track keeps its snapshot.

### Taking a track (`QT /OK`, coast) gives the previous owner no `K-dd`
- **Severity**: gap · **Owner**: Yaat.Sim · unverified
- **SRS**: §E.3 Table 40 (p.887–888), the Field E `K-(d)dd` columns for "h/o acc (cntrl) ≠ init or rcv (assume cntrl)" and "≠ prev cntrl".
- **YAAT now**: `EramEntryEngine.ApplyTrack` (:557-579) and `ApplyCoast` (:632) call `StartTrack` but never `TrackEngine.MarkRecentHandoffAccepted(..., wasForced: true)`. `K` is set only on the POCONVERT path (:430) and on `ApplyForceHandoff` (`TrackEngine.cs:803`).
- **Fix sketch**: capture the previous owner before `StartTrack`. If it differs from the actor, mark the handoff as recently accepted and forced.

### Field E ground speed shows `000` for a stopped target
- **Severity**: bug (small) · **Owner**: yaat-server · unverified
- **SRS**: §E.3 Table 55 (p.909): ground speed is shown when "velocity data is available and is nonzero".
- **YAAT now**: `DtoConverter.cs:384` sends `GroundSpeed = (int)ac.GroundSpeed`, including 0. CRC prints `{gs:D3}` whenever the value is present.
- **Fix sketch**: send null when the value is ≤ 0.

### Special-code (SPC) blink never times out
- **Severity**: gap · **Owner**: yaat-server · unverified
- **SRS**: §E.3 Table 54 (p.904–905): EMRG, RDOF, HIJK and the adapted SPC text blink "for adp_NonControllingAttentionBlinkingInterval".
- **YAAT now**: `DtoConverter.cs:388` sets `BlinkSpc = IsSpc(code)` for as long as the code is squawked.
- **Fix sketch**: blink for a limited interval after the code first appears. The interval value is not in this volume.

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

### Vertical conformance (`ReachedAssignedAltitude`) is stateless
- **Severity**: unverified · **Owner**: yaat-server
- **SRS**: §E.2 vertical conformance (p.874–875), B4 cases 1b1–1b4 (p.881–882): the result is event-evaluated and latched, and "too low" (`-`) or "too high" (`+`) means outside conformance and not moving toward it.
- **YAAT now**: `DtoConverter.cs:1056-1058` sends `|measured − assigned| ≤ 200 ft`, recomputed every tick against one altitude. A level aircraft 1,000 ft low shows a climb arrow instead of `-`, and a block altitude compares against one end only.

### STARS temporary altitude leaks into ERAM Field B as an interim altitude
- **Severity**: unverified (may be intentional) · **Owner**: yaat-server
- **YAAT now**: `DtoConverter.cs:1102` sends `InterimAltitude = ac.Eram.InterimAltitude ?? ac.Stars.TemporaryAltitude`. SRS Field B uses `FLTS:InterimAltitude` only.

### Dwell lock, leader offset and DRI halo are per aircraft, not per sector, and the dwell lock is not recorded
- **Severity**: gap (matters only when two ERAM CRC sessions share a room) · **Owner**: yaat-server + Yaat.Sim · unverified
- **SRS**: §A.40 (p.210) and §A.41 (p.211), plus the A.1 Table 3 rows 562, 681 and 758 (pp.69, 80, 87), which treat emphasis and leader lines as R-position display state.
- **YAAT now**:
  - yaat-server `DtoConverter.cs:876-885` sends a single per-aircraft `LeaderDirection`, `LeaderLength`, `DriHaloType` and `IsDwellLocked` to every sector (its comment: "single-ERAM-sector training").
  - `CrcClientState.Eram.cs:191` toggles `IsDwellLocked` directly, with no sector key and no `ApplyAndRecord`, so a replay loses it. VCI, FDB-open and point-out minimize are per sector and recorded.
- **Fix sketch**: key these by (facility, sector) the way `OnFrequencySectorIds` is keyed, and record the dwell-lock toggle as a `RecordedEramEntry`. The missing recording is a determinism bug even for a single sector.

### A 1200 code above the conflict-alert floor is drawn as an MCI
- **Severity**: unverified · **Owner**: yaat-server
- **SRS**: A.1 Table 3 rows 9–11 (p.16) define separate MCI, 1200-beacon (V) and "Unpaired MCI Alert Ineligible" symbols. The eligibility rule itself is in Book 1.
- **YAAT now**: `DtoConverter.cs:426-432` returns MCI for any uncorrelated beacon target at or above the floor, following `docs/crc/eram.md:358`.

## Checked and consistent

- Quick Look eligibility (E.1 Table 36) for the state YAAT sends.
- Field B/C/B4 inputs. CRC builds the strings; its own shortfalls (the missing `F` B4, the unreasonable-Mode-C `X`) are CRC-local.
- Frozen, then coast, then normal symbol order (E.4). NONE for a standby aircraft with an assigned code. Field E sector formatting (built by CRC from `Owner`/`HandoffPeer`/`RecentHandoffPeer`).
- CRC has no Departure, Inbound, Hold, Conflict Alert, AHI, MRP or FEL view, no hold track symbol, and no CPDLC/TOC/PID indicators, so YAAT has nothing to send for B.13/B.20/B.21 and the related A.1 rows. The range data block shows the CRR distance only. CRC's 7 ERAM hub methods are all handled by the server.
- The vertical-conformance latch (see the `ReachedAssignedAltitude` candidate) is defined in Book 1, not in this volume.
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
