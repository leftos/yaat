# ERAM command conformance against the ERAM EDSM SRS

**Status (2026-09-27):** Waves 0–4 are done: the reference in `docs/eram/`, the file split, ACCEPT/descriptor feedback with table errors, flight-ID validation and the conformance test, per-field checks for every verb we answer, and the Wave 4 commands (LD, LE, QX `/R` and FP, CO, AM for the ten fields YAAT models, QT coast, HM/QH hold). Next: Wave 5.

## Context

A controller on VATSIM shared the FAA ERAM EDSM SRS, Volume 1 Book 2 (ERAM_EDSM_SRS_210.04_V1B2, EAF100). Its Appendix C lists every ERAM command, its field formats, the checks each field must pass, the error each failed check returns, and the confirmation text shown on success (the "message type descriptor"). The report: yaat-server's ERAM responses don't read like real ERAM, and there are useful commands we don't handle.

**The source PDF is not in the repo.** It lives on the maintainer's machine at `C:\Users\lefto\Downloads\ERAM_EDSM_SRS_210.04_V1B2_SDR-088All_Commands.pdf`. To work from its text, run `pdftotext -layout <pdf> eram.txt` and split the result on form feeds (`\f`) into one file per page, reading it as latin-1. Page-file index = printed page number + 15. The validation table (C.8) runs from printed page 624 to 846, the field definitions (C.1) from 401 to 527, the command routing table (C.2) from 528 to 568, and the flight data fields table (C.9) from 847 to 864.

The code confirms it. `D:\yaat-server\src\Yaat.Server\Hubs\CrcClientState.Eram.cs` (2974 lines) answers every success with ad-hoc text in the Response Area and an empty feedback area, e.g. `(true, [], "QT {cs}")` at :1458 and `"Leader set"` at :409. CRC's own locally handled commands (`D:\crc-decompiled\CRC\Vatsim.Nas.Crc.Ui.Displays.Eram.Input\InputManager.cs:779-914`) show the real convention:
- success: feedback `["ACCEPT", <descriptor>, <detail>]`;
- error: one line such as `BCN CODE FORMAT` or `MESSAGE TOO LONG`;
- the Response Area carries only readouts.

Field checks are mostly missing. Flight IDs never report a format error, a duplicate match or a wrong pick.

Goal:
- a git-tracked YAML command reference extracted from the spec;
- a conformance test that holds the server to that reference;
- ACCEPT/descriptor feedback and per-field validation for every verb we already handle;
- a first wave of new commands.

## Decisions (made with the user)

- **Scope:** conformance for the existing verbs, plus new commands. The first wave of new commands is CO, the full AM field set, HM/QH hold with QX `/R` and QX FP, and LD/LE.
- **Reference:** YAML in the yaat repo under `docs/eram/`, one file per command. The PDF itself is not committed. Code and tests cite our docs, never SRS page numbers.
- **Enforcement:** a conformance test in yaat-server. YamlDotNet goes in the **test project only**.
- **Error text:** the spec gives only error IDs, so we write the literal text in CRC's style (`FLID FORMAT`, `<field> FORMAT`, `MESSAGE TOO LONG`). Each entry is marked `crc`, `spec` or `coined`.
- **`/OK`:** follow the spec. Accept it on QZ, QR, QS, QB and AM too; the user expects more `/OK` in vNAS's coming "ERAM 2". `EramEditOwnershipTests.cs:117` changes from rejected to accepted.
- **Bare `AM <FLID>`:** stays a readout, as the CRC docs describe.

## Wave 0: command reference (yaat repo)

Layout under `D:\yaat\docs\eram\`:
- **`README.md`**: what the reference is, the format notation (L = letter, d = digit, a = letter or digit, parentheses = optional), the schema, the `handler` key convention and the error `source` flag.
- **`commands/<ID>.yaml`**: one file per command. Schema:
  ```yaml
  id: QZ
  variants:
    - title: Assigned Altitude
      descriptor: ASSIGNED ALT
      format: "01 (60) 08 02"        # field order, from the command routing table
      multiple_flids: false
      handler: QZ.AssignedAltitude   # or: not implemented
      na: <reason>                   # only when a variant can't apply on VATSIM
      fields:
        - field: 08 Assigned Altitude
          rule: |
            One of ddd, OTP, OTP/ddd, VFR, VFR/ddd, dddBddd.
          error: MsgALTFormat
  ```
  - **Every** command ID in the spec's validation table (~150) gets a file.
  - In-scope IDs get full `fields`: ALL, AM, AR, CO, DM, HM, LA–LF, MR, QA, QB, QD, QF, QH, QL, QN, QP, QQ, QR, QS, QT, QU, QV, QX, QZ, RF, VP, WR.
  - Out-of-scope IDs get only title, descriptor, `handler: not implemented`, `na:` and `fields: []`. These are CPDLC, sign-in, flow control, maintenance, ARTS, E-MSAW, frequencies, weather and strips.
  - `AM.yaml` also carries `amendable_fields` from the flight data fields table (abbreviation, field number, whether the field can be deleted).
  - `RD` is yaat-only and is marked `source: yaat`.
- **`error-responses.yaml`**: entries like `{ id: MsgFlidFormat, text: FLID FORMAT, source: coined }`.
  - `{cofie}` in a text stands for the contents of the field in error.
  - Errors that exist only in yaat use a `Yaat` prefix: `YaatAlreadyTracked`, `YaatPoExists`, `YaatDupNewId`, `YaatInternal`.
  - Proposed texts include FLID FORMAT, INVALID PICK, ILLEGAL FLID, FLID NOT STORED, DUPLICATE FLID, NO FLIGHT PLAN, `{cofie} FORMAT`, ALT FORMAT, BCN CODE FORMAT, INVALID BLOCKED ALT, INVALID COMBINATION, INVALID DIRECTION, INVALID LENGTH, SECTOR FORMAT, NOT ADAPTED, NOT YOUR CONTROL (a spec literal that replaces our NOT YOUR TRACK), and SESSION NOT ACTIVE (CRC's own text, replacing NOT ACTIVE).

Extraction (the scripts stay in the scratchpad, since the PDF text isn't in the repo):
1. A Python skeleton script runs over the per-page text files (see the source-PDF note above; the validation table is page files p0639–p0861).
   - It strips headers and footers, then splits at command-header lines. It handles the two-line `QN (or / QZ)` headers and the headers whose descriptor sits on the next line (AM, QT Track, QZ, QH Hold, QP DRI, RF).
   - It writes each YAML skeleton, plus a sidecar holding the raw block and the multiset of error IDs for each variant.
2. Agents fill `fields` for about five in-scope commands per batch.
   - They open PDF page images with `Read pages=` wherever the error IDs in the right column are stacked out of line with their checks.
   - The routing-table `format` values are transcribed from page images, because that table's text is interleaved across columns.
3. A fidelity script runs before commit. It checks that, per variant, the YAML's error-ID multiset equals the raw one, and that descriptors and variant counts match the skeleton. It also checks that every referenced error ID exists in `error-responses.yaml` and that every file parses with the required keys.
4. A reviewer agent then compares the YAML against page images for every `format` and a 10% sample of variants.

Also, in the same commit:
- Add glossary entries for descriptor, cofie, FLID, implied command and `/OK`, linked from `docs/README.md`.
- Fix the stale ERAM section of `docs/crc-protocol-support.md` (:55, :270). It describes QZ and QQ backwards and says QR is an alias of RD.
- Write the subplan `docs/plans/eram-conformance.md` (waves as below) with one line in `docs/plans/MAIN.md`.

## Wave 1: structure only, no behaviour change (yaat-server)

- Split `CrcClientState.Eram.cs` into partials: `.Eram.cs` (dispatch, parsing, response), `.Implied`, `.Display` (QN, QL, QS, VCI, leader), `.Track` (QT, QX, QH), `.Altitude` (QZ, QQ, QR), `.FlightData` (AM, VP, QB, DM, QF), `.Route` (QU, RD), `.Readouts` (LA, LB, LC), `.Crr` (LF), `.Pointout` (QP). Follow the behaviour-preserving-refactor skill.
- Add `Hubs/Eram/EramResult.cs`: a record struct with `Deconstruct`, so existing tuple-deconstructing tests compile unchanged. `BuildEramMessageResponse` (:2923) takes it.
- `EramMessageElement` gains an `EramPickKind`, taken from the pick slot in `ParseProcessEramMessageArgs` (~:2838), so the flight-ID validator can reject the wrong kind of pick.
- Log the exception in the empty `catch` in `TryReadLocation` (~:2884).
- Verify: `tools/gate.sh .tmp/w1.log timeout 120 dotnet test` shows every existing test green unchanged; the build has zero warnings.

## Wave 2: feedback convention, flight IDs, conformance test

**In `Hubs/Eram/`:**
- **`EramError.cs`**: an enum named after the YAML IDs, with `Id()` and `Template()` in one switch.
- **`EramVariants.cs`**: a handler key → descriptor map (e.g. `QT.Track`, `QN.AcceptHandoff`, `QP.Dri`) and the `Verbs` set that the dispatch switch (:79) keys off.
- **`EramResult` factories:** only `Accept(variantKey, detail)`, `Readout(variantKey, text)` and `Reject(EramError, cofie)`. No factory takes a raw string, so the type system guarantees every text the server emits comes from the table.
- **`EramFlid.cs`**: one validator for the flight-ID field (the "F02 block"):
  - format: ACID, CID, or four octal digits;
  - illegal: a beacon code ending in 00;
  - not stored;
  - duplicate;
  - wrong pick kind.
  It needs a new `RoomEngine.FindAircraftCandidates` (next to `FindAircraft`, `RoomEngine.cs:884`) that returns all matches.

**Behaviour changes:**
- Every success becomes `ACCEPT` / descriptor / ACID. Readouts (QF, LA, LB, LC) keep the Response Area and also get ACCEPT.

**In Yaat.Sim:**
- `Commands/EramEntryErrors.cs` holds stable error-key constants that equal YAML IDs.
- `EramEntryEngine` returns those keys. The server maps them to `EramError`; an unknown key is logged and becomes `YaatInternal`.

**Test side:**
- Add YamlDotNet to `tests/Yaat.Server.Tests/Yaat.Server.Tests.csproj`, looking up the current stable version first.
- Add an assembly-metadata `YaatDocsDir` derived from `$(YaatSimProject)` (`Directory.Build.props:9-11`, sibling `..\yaat` or `extern\yaat`).
- **`EramReferenceConformanceTests`** checks:
  1. every dispatched verb has a YAML file, with implied commands mapping to `QN.yaml`;
  2. descriptors match between the YAML and the code in both directions;
  3. every `EramError` has an entry with the same text, and vice versa;
  4. every error referenced by an implemented variant exists;
  5. the schema has the right shape.
- `EramFlidValidationTests` covers the flight-ID checks. Update the literals in `EramEditOwnershipTests` and `CrcEramFlightPlanTests`.

**Landing:**
- The yaat YAML lands first. yaat-server CI builds against `extern/yaat` and goes green once the submodule bot bumps it.
- Verify: filtered runs (`timeout 30 dotnet test -- --filter-class *EramReferenceConformanceTests` etc.), then `pwsh D:\yaat\tools\test-all.ps1`.

## Wave 3: per-field validation for the existing verbs

Add `Hubs/Eram/EramFields.cs`, a set of pure parsers that return `(value, EramError?)`. They cover altitude (wrapping `FlightPlanAltitude.Parse` and `TryParseBlockAltitude`), offset, `/OK`, location (fix, FRD, lat/long bounds), speed `S(d)(d)dd`, heading, HHMM, and sector `01–128` / `L(d)dd`.

Fix these gaps, each test first:
- **Handoff ownership.** An ERAM handoff initiate (`<sector> <FLID>`, and `QZ`/`QN` forms) never checks that the initiating sector owns the track: neither the server's implied handoff path nor the Sim's `TrackEngine.ApplyHandoff` does. Reject with `YaatNotYourControl` unless `/OK` is given.
- **Pick kinds for the location-taking verbs.** QT, QH, LF, LC, LA, LB, QU and RD take a clicked location as a separate field, so Wave 2 left their flight-ID pick-kind check off. Add it together with each verb's location field.
- **`DispatchQu` complexity.** It is over the 100-line / complexity-8 limit; split it into per-form helpers while adding QU's field checks.
- **QZ prefix.** `QZ` is also the alternative prefix for implied commands, so `QZ 44 <FLID>` is a handoff. Two digits mean a sector; three mean an altitude. Today `DispatchEramMessage` (:89) always treats it as an altitude.
- **QN:** reject a direction outside 1–9 and a leader length other than 0, 1, 2, 3 or 5 (QN.yaml field 59; `TryParsePositioningLeader` accepts only 0–3 today). A bad handoff sector answers `{cofie} FORMAT` under QN (field 16), not QP's `SECTOR FORMAT`, and `L00`/`L000` (undirected handoff to a facility) is legal.
- **QS:** free text is 1–8 characters with no spaces (Sim `EramEntryEngine.FreeTextMaxLength` is 40 today). Add action/data combination errors and the full speed grammar.
- **QT:** add the location operand and `QT D` target pairing; CRC sends it on Ctrl+Shift+click (`InputManager.cs:309`), and today we ignore the `D`.
- **QH F:** use the clicked location, which is dropped today.
- **QU, LA, LB, LC:** bounds checks; INVALID TIME and INVALID SPEED.
- **QP:** sector format, non-adapted sector, duplicate sector.
- **QB:** code plus equipment in one entry; `0` combined with a code is invalid.
- **LF:** duplicate FLID, MESSAGE TOO LONG, label format.
- **QF:** selection character.
- **VP:** type and route formats.
- **`/OK`** everywhere the spec allows it.

Close the replay gap: point-out create and acknowledge (:2478-2499, :2574), DRI (:2518), VCI (:326-333) and leader/offset (:400-407, :484-505) write state directly today. Move them onto `RecordedEramEntry` with new `EramEntryEngine` forms, tested in `EramEntryEngineTests`.

**Wave 3 decisions (user 2026-09-26):**
- Handoff ownership lives in the Sim, as an ERAM entry form (revised by the user the same day): every `HO` reaches the Sim as the same command text, so the Sim cannot tell a CRC handoff from an instructor's typed `HO`, which acts as the track's owner (COMMANDS.md). ERAM's `<sector> <FLID> [/OK]` handoff (and the `QN`/`QZ` forms) becomes a `RecordedEramEntry` form that `EramEntryEngine` checks for ownership (`/OK` forces; the pending recipient's redirect is exempt) before calling `TrackEngine.ApplyHandoff`, so replay applies the same check. STARS keeps its server-side `RequireOwnership` (`ILL TRK`); the typed `HO` is unchanged.
- QS free text follows the spec: 1–8 characters with no spaces, longer answers `INVALID TEXT FORMAT`; `EramEntryEngine.FreeTextMaxLength` (40) and its truncation go. Check the recording fixtures for a longer QS entry first.
- An amendment to an aircraft whose callsign fails `Callsign.IsValid` (over 7 characters) reports failure instead of ACCEPT: `SimulationEngine.AmendFlightPlan` returns its outcome and `RoomEngine.AmendFlightPlan` passes it on.
- Every Wave 3 check maps to an existing `EramError`; no new error entries are expected (explorer 2026-09-26). The replay-gap forms need no snapshot change: the fields already round-trip in `AircraftEramState`.
- QB's rule "`0` with a code is invalid" belongs to the combined code-and-equipment variant, which Wave 3 implements.

**Wave 3 briefs:** W3-1 `EramFields.cs` (shared parsers; first). Then, in parallel: W3-2 Sim handoff ownership + amend outcome + QN bounds + QS + the VCI/leader replay gap (`TrackEngine`, `EramEntryEngine`, `.Implied`, `.Display`); W3-3 QT, QH F, QZ prefix, `/OK` on QZ/QR (`.Track`, `.Altitude`, `.Eram.cs`); W3-4 QU split + bounds, RD, LA/LB/LC, LF (`.Route`, `.Readouts`, `.Crr`). Last: W3-5 QP + point-out/DRI replay gap + QB/QF/VP + `/OK` on QB/AM (`.Pointout`, `.FlightData`, `EramEntryEngine`, after W3-2).

**Handoff review rulings (user 2026-09-26):** the STARS redirect exemption does not apply to ERAM (a pending recipient gets NOT YOUR CONTROL and must accept, then re-initiate, or use `/OK`); `/OK` forces a handoff only on a track an ERAM sector of the same ARTCC owns (CRC `docs/crc/eram.md`: overrides never reach an external ARTCC's flights); a bare STARS TCP (`2B`) is not a handoff sector — spec only, `2B FORMAT` — while `Q2B`-style codes still work.

**QR and self-handoff rulings (aviation consult 2026-09-26; the sources are silent, so these are yaat rulings, noted in the YAML):** QR is limited to the controlling sector through `RejectIfNotEditable`, `/OK` overriding within the same ARTCC, because the CERA is the owner's own verification record (`docs/crc/eram.md` :48, :624); `QR 000` clears the CERA (QR.yaml field 54); QR's field 54 is exactly `ddd` (ALT FORMAT) and one flight only. A handoff whose resolved target is the track's owner is refused with a coined `YaatHandoffToOwner` = `SECTOR IS OWNER` (it also covers a forced handoff that resolves to the owner).

QT with fields 05/08/56/68/74 (Coast Track) and QH's `/OK` validate and then accept with no effect; they are built in Wave 4 (QT coast, HM/QH hold).

Tests: one `EramConformance<Verb>Tests` class per verb through the `Harness/EramWire.cs` harness. Each has an accept case asserting `["ACCEPT", descriptor, acid]` and one case per field check. Every new variant or error goes into the YAML first, which is the failing test.

## Wave 4: first new commands

All Wave 4 items have landed; the rulings below are the record of how each was decided.

**Wave 4 rulings (user 2026-09-27; the SRS is silent on each, so they are yaat rulings noted in the YAML):** LD and LE measure along the remaining route (`RemainingRouteFixes`) and fall back to LC's straight-line distance when the fix is not on it; LD answers a bare Zulu `HHMM`, LE answers LC's `<gs>KT <±delta>`, both against the wall clock like LC. `QX /R` is the same state change as `QX` (drop), answered with the SURRENDER CONTROL descriptor. `QX FP` removes the strip the way `STRIPD` does and ACCEPTs as a no-op when there is none.

**Wave 4 decision round (user + aviation consult 2026-09-27):**
- **CO** is ERAM-only (7110.65 §5-13-1c vs STARS §5-14-6c are separate systems) and per-alert (§5-13-1c.1): a `Suppressed` flag on the active `EramActiveConflict`, set and cleared by a recorded ERAM entry; suppressed alerts are withheld from the STCA list and the FDB conflict status, and the suppression dies with the alert, so a new conflict between the same pair alerts again. No ownership gate (§5-13-1c.3 coordination is procedural). CO on a pair with no active alert is refused; the same aircraft twice is `INVALID COMBINATION`. The existing STARS `CASUP` list is not touched. The flag defaults false, so no schema bump.
- **AM** validates every pair before applying any (all-or-nothing). RAL, SAI (`H`) and NUM become Yaat.Sim flight-plan fields (SAI separate from TYP's `H/` prefix, NUM stored with no simulation effect), shown in the QF / bare-AM readout (CRC's wire has no slot for them); ERAM AM only, with no field in YAAT's own Flight Plan Editor. EQP writes `IcaoEquipmentCodes`. `BCN -` clears the assigned code without triggering the file-time discrete draw.
- **HM / QH hold**: a fix-only entry leaves direction, turns and leg blank (§5-13-9a.4, §4-6-4e: no defaults the controller did not issue). A radial in field 310 is accepted and rounded to the nearest of the 8 points (the radial, not its reciprocal; ties go clockwise) on the wire, with the raw radial kept in `AircraftHoldAnnotation`. FRD / lat-long locations are stored as the typed string. EFC-only, `/*` and `C` forms read-modify-write the stored annotation. Ownership follows `RejectIfNotEditable` with `/OK`.
- **QT coast (CT)** is Sim state like the QH freeze (`AircraftEramState` fields through an `EramEntryEngine` form, snapshot schema bump). The coasted track moves (§5-13-8a FLAT): from the entered location on the entered heading, else along the filed route, at the entered speed, else the filed TAS; no wind. It ends only on a plain QT (start track) or QX; no timer, no auto re-pair. A coasted track is exempt from STCA like a frozen one (§5-13-7); the unpaired real target is handled as any unpaired target.

## Wave 5: remaining commands (next; the ERAM-only steer covers it, user 2026-09-27)

- RF: validate and ACCEPT only (today `RF` falls to the implied path and is misread as a handoff to a sector named "RF").
- QF field selection (field 12).
- QA auto handoff and QV quick vector.

Settled without work (explorer 2026-09-27): QP request/suppress data block is already built (`QP.ReqSuppDb` → the Sim `MIN` entry; one-directional, as `docs/crc/eram.md` :862 describes). DM for proposed plans needs nothing: YAAT has no proposed-plan object (`FlightPlanStatus.Proposed` is derived from "on the ground, never airborne"), and `DM.Departure` already resolves the FLID and ACCEPTs. `EramPointoutState.IsRecipientSuppressed` is set nowhere and read by nothing in CRC's UI, a dead field.

**Wave 5 rulings (user 2026-09-27):**
- **QT `C` (convert point-out)**: the entering sector must be the receiver of a pending ERAM point-out on the track (else `YaatPoNotFound`); one recorded Sim entry then takes the track for it and removes that point-out, as STARS `**` does (`TrackEngine.HandleConvertPointout`). `/OK` (field 60) overrides the receiver check. Landed 2026-09-27 as the `POCONVERT` entry form: the server matches the receiver and records the point-out's key (or `/OK`); a point-out cleared on the R side is not convertible; `/OK` with no match also takes an untracked track; a convert unfreezes, ends a coast and marks the previous owner only when there is a different one.
- **QF field 12** answers for the ten fields YAAT models (TYP, BCN, SPD, ALT, RAL, RTE, RMK, NUM, SAI, EQP); the `amendable_fields` rows in `AM.yaml` that YAAT stores nothing for are marked `na`. Wave 4's AM work built those ten, not "the full AM field set".
- **QA / QV**: an ERAM entry changes what the scopes show, never the aircraft (user: a controller does not fly the aircraft through ERAM). Read from the SRS (2026-09-27): QA sets or edits the host's **auto-handoff inhibit** for a flight (MONF) or for receiving sectors/facilities (AERO), shown as an FDB indicator and an AUTO HO INHIB view; QV draws a console-local **user-requested velocity vector** for the entered heading and speed (EDSM (L), never sent to the host, other sectors never see it). YAAT has no automatic handoff and CRC's wire has no slot for either (`EramTrackDto`, `EramDataBlockDto`), so both validate their fields and ACCEPT with no effect, each YAML carrying a note that says why.

## Verification

- **Wave 0:** the fidelity script passes; the reviewer agent's sample shows no discrepancies.
- **Each code wave:** red-then-green filtered tests; `tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true` with zero warnings; `pwsh D:\yaat\tools\test-all.ps1` whenever Yaat.Sim changes.
- **End to end, per wave:**
  - Run yaat-server from source (port 5130) with a CRC ERAM session.
  - Drive the MCA with the yaat-client-driver MCP (`list_windows`, `click_point`, `send_keys`, `screenshot`).
  - Enter one accept and one reject case per changed verb, and confirm three things: the feedback area shows `ACCEPT` / descriptor / ACID, rejects are one line, and the Response Area shows only readouts.
- **Reviews:** csharp-reviewer on each code wave. aviation-sim-expert on CO and hold semantics (Wave 4) and on any wording that pilots or controllers would hear or read.

## Critical files

- `D:\yaat\docs\eram\**` (new)
- `D:\yaat-server\src\Yaat.Server\Hubs\CrcClientState.Eram*.cs` (split) and `Hubs\Eram\*` (new)
- `D:\yaat\src\Yaat.Sim\Commands\EramEntryEngine.cs` and `EramEntryErrors.cs` (new)
- `D:\yaat-server\src\Yaat.Server\Simulation\RoomEngine.cs`
- `D:\yaat-server\tests\Yaat.Server.Tests\Harness\EramWire.cs` and `Yaat.Server.Tests.csproj`
- Reference only: `D:\crc-decompiled\CRC\Vatsim.Nas.Crc.Ui.Displays.Eram.Input\InputManager.cs`
