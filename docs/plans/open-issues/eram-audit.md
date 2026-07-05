# ERAM Functionality Audit — YAAT server emulation vs. CRC

**Date:** 2026-07-03
**Scope:** Full `docs/crc/eram.md` surface (client-display features catalogued and flagged N/A-for-server).
**System under audit:** `yaat-server` CRC ERAM emulation + shared `Yaat.Sim` track/handoff/pointout logic.

**Sources compared:**
1. **Behavior spec** — `docs/crc/eram.md` (official vNAS CRC ERAM user documentation).
2. **Decompiled CRC client** — `X:/dev/crc-decompiled/CRC/Vatsim.Nas.Crc.Ui.Displays.Eram.*` (the client YAAT feeds; authoritative for the wire contract and MCA command syntax).
3. **Rust read-only emulation** — `X:/dev/vatsim-server-rs` (`radar_state/src/eram_state.rs`, `server/src/clientstate/eram.rs`, `messaging/src/dtos.rs`). Display-only by design — it deliberately omits interactive control (handoffs, pointouts, amendments), so for those features it is "n/a" and the authorities are the spec + decompiled CRC; for display/wire features (targets, data blocks, DTO topics) it is a real reference.

**Method:** 11 parallel dimension finders classified 162 ERAM features `implemented` / `partial` / `missing` / `na_client_side` with file:line evidence; each `missing`/`partial` gap then got an independent adversarial verifier prompted to *refute* it; a completeness critic then read the full spec against the union of covered features. Reconciliation applied each verifier's verdict as the final word over the finder's original call (2 finder false-positives refuted → reclassified `implemented`; 14 reclassified in status/severity; 53 confirmed).

> **Verification note:** one gap's verifier agent died (schema retry-cap) — *Point Outs → "Inter-facility point-out target resolution"* — so it was **re-verified by hand** post-run and confirmed (`partial`/medium). 13 `low`-severity gaps were not individually verified (verification was capped at the 10 highest-severity gaps per dimension; the dropped ones are all `low`), flagged **(UNVERIFIED)** below.

---

## Executive summary

YAAT's ERAM emulation has a **solid wire/DTO backbone and a complete interactive-control engine reachable from the YAAT (instructor/RPO) training client**, but a **thin and incomplete CRC-facing MCA command surface**. The recurring pattern: the underlying capability usually *exists in `Yaat.Sim`/`TrackCommandHandler`* and is exercised by the YAAT client over the SignalR training hub, but the **`ProcessEramMessage` verb switch that a real CRC ERAM controller talks to does not route to it**. A student sitting on a CRC Center (ERAM) position therefore cannot perform several core en-route workflows from their keyboard, even though the same action works when driven by the YAAT client.

**What is genuinely solid** (baseline, don't re-verify): the ERAM topic/DTO wire contract (EramTargets, EramTracks, EramDataBlocks, EramPointout, EramSectorConfiguration, EramRouteLines, ProcessEramMessage result DTO) with correct field ordering and enum parity; track ownership + authorization guards; the full handoff/pointout/consolidation **engine** (initiate/accept/recall/force/auto-accept/redirect) on the YAAT-client path; auto-track (`.autotrack`/UpdateAutoTrackAirports) including departure acquisition and delta semantics; NEXRAD; QL quick-look storage; FDB field population.

**Headline defects** (7 distinct high-severity issues; the 13 raw high-gaps collapse because 4 dimensions independently found the handoff gap, 2 found the QX gap, and 3 found the QZ/QQ inversion):

| # | Defect | Effect |
|---|--------|--------|
| H1 | **ERAM handoffs (initiate/accept/recall/force) not dispatched over the CRC wire** | A CRC ERAM controller cannot hand off, accept, recall, or force a track from the keyboard. Logic exists but only the YAAT client can reach it. STARS implements this fully — pure asymmetry. |
| H2 | **No implied-command fallback in the `ProcessEramMessage` verb switch** | Bare `<FLID>`, numeric leader `1 <FLID>`, `/len`, sector-id handoff, `/OK` — a large fraction of routine ERAM interaction — all hit `default → FORMAT`. Root cause of H1, H3, and several mediums. |
| H3 | **`QX` (drop track) verb unhandled** | F4 / typing `QX <FLID>` returns FORMAT. Drop works only via the non-spec `QT D` form, which also leaves `HandoffPeer`/`HandoffInitiatedAt`/`CreatedByOwner` stale. |
| H4 | **`QZ`/`QQ` altitude semantics inverted vs spec** | `QZ` (should set *assigned* altitude) writes *interim*; `QQ` (should set *interim*) writes *assigned*. Controllers set the wrong altitude field. |
| H5 | **Data-block `Format` hard-coded to `Fdb` for every track, globally** | LDBs never appear; every sector sees every track as a full FDB; QL quick-look is inert; the inbound-handoff "LDB→FDB" cue is absent. Not computed per subscribing sector. |
| H6 | **EramDataBlocks create/delete ID mismatch → ghost data blocks** | Create keys blocks on bare callsign; every delete path keys on `"CALLSIGN"+callsign`, so `Remove` never matches and FDBs persist after the aircraft is gone. |
| H7 | **ERAM conflict alert / STCA entirely unpublished** | An ERAM controller receives *no* conflict alerts (no STC list, no flashing data block). The `EramShortTermConflicts` topic is a stub returning `null`; `ConflictStatus` is hard-coded `NoConflict`. |

Beyond these, the display fidelity of **targets** (7 of 8 symbol types unreachable — all traffic is `CorrelatedBeacon`), **track status** (coast/frozen/free never render), **target history trails**, and **CRR groups** are missing or stubbed, and several flight-plan MCA commands (`AM`, `DM`, `VP`, `QB`, `QR`, `QU`-amend) are absent or mis-wired.

---

## Systemic root causes

Most gaps trace to nine systemic causes. Fixing the cause resolves the cluster.

### A. `ProcessEramMessage` has no implied-command / fallback arm
`yaat-server: src/Yaat.Server/Hubs/CrcClientState.Eram.cs:47-61` switches on `elements[0].Token` and handles only `QN/QF/QL/RD/QU/QT/QZ/QQ/QS/QP/QR`, falling through to `_ => (false, ["FORMAT"], null)`. CRC's `InputManager` handles only `SR/QD/WR/MR/QB/AR` client-side and sends *everything else* — including all implied commands and handoffs — to this one method (`crc-decompiled: …/Eram.Input/InputManager.cs:505-529`). So any first token that is a bare FLID, a digit `1-9` (leader direction), `//`/`/len`, a sector id, `/OK`, or a verb not in the list is rejected.
**Cluster:** H1, H2, H3, plus MISSING `DM`, `VP`, `AM`, `LA`/`LB`/`LC`, `QB`(assign), `LF`, `RF`, `QH`; PARTIAL leader-direction numeric parse, datablock FLID-cycle, `/OK <FLID>` force.

### B. Data-block `Format` is a global constant, never computed per sector
`DtoConverter.ToEramDataBlock` (`yaat-server: src/Yaat.Server/Simulation/DtoConverter.cs:560`) hard-codes `Format = EramDataBlockFormat.Fdb` and takes no subscribing-sector argument; `CrcBroadcastService.cs:165-168,1290` broadcasts one shared block list to all ERAM subscribers. Contrast EramTracks, which *is* recomputed per subscriber (`CrcBroadcastService.cs:1567`). The Rust reference computes it per sector (`vatsim-server-rs: crates/server/src/clientstate/eram.rs:384-446`: FDB if handoff-to-me / quicklooked / owned, else paired/unpaired LDB).
**Cluster:** H5, PARTIAL Quick-Look (inert), PARTIAL inbound-handoff FDB flash, MISSING datablock FLID-cycle/minimize, MISSING RDB/CDB.

### C. Target `SymbolType` is hard-coded `CorrelatedBeacon`
`DtoConverter.cs:265` emits `CorrelatedBeacon` for every aircraft; `WasModeCPreviouslyReceived=true`, `BlinkSpc=false`, `IsCorrelated=true` are likewise constants. The discriminating sim data exists (`AircraftTransponder.Mode/Code/IsIdenting`). 7 of the 8 spec symbol types (eram.md Table 1) are unreachable.
**Cluster:** MISSING Ident / Primary(standby) / MCI / VFR(1200) / Reduced-separation symbols; PARTIAL correlation-symbol engine; PARTIAL WasModeCPreviouslyReceived/BlinkSpc.

### D. ERAM track `Status` hard-coded `Normal`, `IsCorrelated` hard-coded `true`
`DtoConverter.cs:577-578` — the only assignment to `EramTrackDto.Status` anywhere. `Coasting`/`Frozen` enum members (`CrcDtos.Session.cs:299-300`) are dead code; `AircraftEramState` has no status/coast/frozen field. CRC renders the four track types and CST/FRZN Field-E from this wire field.
**Cluster:** PARTIAL four-track-types; MISSING coast-on-target-loss; MISSING QH freeze; PARTIAL Field-E CST/FRZN.

### E. ERAM conflict alert / STCA is unpublished
`CrcBroadcastService.cs:117-121` stubs `EramShortTermConflicts` returning `null` with a misleading "Populated in later buckets" comment; no `EramShortTermConflictDto` / `ReceiveEramShortTermConflicts` exists; `ConflictStatus` hard-coded `NoConflict`. YAAT already computes STARS conflicts (`ConflictAlertDetector`), but it is STARS-shaped (5 s prediction, flat 3nm/1000ft, terminal-corridor suppression, no facility-ownership gate) and not ERAM-tuned (4-min lookahead, 5nm / 3nm≤FL230, data-block altitude).
**Cluster:** H7, MISSING EramShortTermConflicts topic, PARTIAL detector-fidelity, MISSING CDB.

### F. Missing server-provided display topics
Three topics the CRC client subscribes to are never published: **EramTargetHistories** (history trail; grep = none), **EramShortTermConflicts** (stub → null), **EramCrrGroups** (stub → null; `SetEramCrrGroupColor`/`ClearOrDeleteEramCrrGroup` are NilAck no-ops). These are server-supplied *data*, not client-only concerns.

### G. Altitude command wiring is inverted / mis-routed
`QZ`↔`QQ` swapped (H4). Additionally `QR` (controller-entered reported altitude / CERA) is mis-bound to `DispatchRd` (route display) with a "redirect semantics deferred" comment (`CrcClientState.Eram.cs:59`) — it is not merely absent but wired to the wrong behavior.

### H. EramDataBlocks delete-ID format mismatch (H6)
Create sets `Id = ac.Callsign` (bare, `DtoConverter.cs:558`); every delete path sends `"CALLSIGN"+callsign` (`CrcBroadcastService.cs:599,739,942,1303-1306`). CRC's `DataBlockRepository` removes by exact id string, so the delete never matches → ghost FDBs. (The bare id also diverges from the canonical vsrs id format.)

### I. FLID resolution is callsign-only
`RoomEngine.FindAircraft` (`yaat-server: src/Yaat.Server/Simulation/RoomEngine.cs:947`) matches `a.Callsign` only, but the spec defines an FLID as **ACID *or* assigned beacon code *or* CID** (eram.md:1589). YAAT generates 3-char CIDs and broadcasts both CID and beacon on the wire, yet typing either into the MCA returns "FLID not found". Cross-cutting: affects every `<FLID>` command. *(Surfaced by the completeness critic, not the per-dimension finders.)*

---

## Verdict by dimension

Status legend: ✅ implemented · 🟡 partial · ❌ missing · ⬜ N/A client-side. Severity: 🔴 high · 🟠 medium · 🟢 low. Cites are repo-relative (`yaat-server:` = `src/Yaat.Server/…`, `yaat:` = `src/Yaat.Sim/…`, `crc:` = decompiled CRC, `vsrs:` = vatsim-server-rs).

### 1. Tracks & Ownership
✅ Ownership representation (owned/other/unowned), take/drop authorization guards, `QT /OK` force-take, canonical DROP (YAAT-client), IsCorrelated field emission.

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| `QX` drop verb | ❌ | 🔴 | CRC sends `QX <FLID>` → server drops | A · verb switch has no `QX` case, `CrcClientState.Eram.cs:47-61` |
| Four ERAM track types (Flat/Free/Coast/Frozen) | 🟡 | 🟠 | Status/IsCorrelated drive 4 symbols → only Flat ever emitted | D · `DtoConverter.cs:577-578` |
| Coast track on target loss (CST) | ❌ | 🟠 | Lost target coasts, shows CST → ERAM track *deleted* | D/E · `CrcVisibilityTracker.cs:172`, `CrcBroadcastService.cs:1307` |
| Track Range cmds `LA`/`LB`/`LC` | ❌ | 🟠 | Server computes range/bearing readout → FORMAT | A · no handler; data+geo helpers exist |
| `QT <location>` association / free-coast-frozen model | 🟡 | 🟢 | `QT` takes location, associates near target → location ignored, needs live aircraft | A/D · `CrcClientState.Eram.cs:360-407` |
| `QT` start track | 🟡 | 🟢 | Create track for unactivated FP at a location → requires existing aircraft, location dropped | `CrcClientState.Eram.cs:371-375` |
| `QH` freeze track | ❌ | 🟢 | `QH F <loc> <FLID>` freezes, sets Frozen → FORMAT | A/D · no handler, no frozen field |

### 2. Handoffs
✅ (all YAAT-client / training-hub path) initiate/accept/recall, Field-E outbound `Hxxx`/`HUNK`, scenario & auto-track-driven auto-handoff, training auto-accept, inter-facility target resolution (ERAM↔ERAM, ERAM↔STARS), consolidation redirect, ONHO trigger. ⬜ AUTO HO INHIB (spec says "not simulated").

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| CRC-native handoff dispatch (initiate/accept/recall via MCA) | ❌ | 🔴 | Implied `<sector> <FLID>` / `<FLID>` / `/OK <FLID>` → all FORMAT | A/H1 · `CrcClientState.Eram.cs:47-61`; STARS has it (`CrcClientState.Stars.cs:364-427`) |
| Force handoff `/OK <FLID>` (steal) | 🟡 | 🟠 | Bare `/OK <FLID>` steal + `Kxxx` indicator → only `QT /OK` works; `/OK <FLID>` FORMAT | A · `CrcClientState.Eram.cs:385` |
| Field-E accepted indicator `Oxxx`/`Kxxx`/`OUNK` | ❌ | 🟠 | Rendered from RecentHandoffPeer/WasForced → both hard-coded null/false | `DtoConverter.cs:609-610` (DTO fields exist, wire-aligned) |
| Inbound-handoff FDB flash (per-sector format) | 🟡 | 🟠 | Receiver's LDB→FDB on pending handoff → format global-constant FDB | B · `DtoConverter.cs:560` |

### 3. Point Outs
✅ Initiate `QP <sector> <FLID>`, R-side/D-side independent clearing, EramPointout wire DTO, duplicate-pointout guard. ⬜ FDB `P`→`A` indicator render, receive/ack pop-up, `QP <FLID>` minimize, recall (all client-rendered).

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| Acknowledge `QP A <sector> <FLID>` (keyboard form) | 🟡 | 🟠 | Typed `QP A <sec> <FLID>` acks → only menu-click `QP A <PO_id>` works | `CrcClientState.Eram.cs:540-567` |
| Remove ack'd point-out (`ClearEramPointout` / click 'A') | 🟡 | 🟠 | Initiator clicks 'A' to clear → caller-auth **inverted**; initiator's clear NilAck'd, 'A' persists | `CrcClientState.Eram.cs:640-648` |
| Inter-facility point-out target resolution | 🟡 | 🟠 **(verified✓)** | Same-facility `QP 15 <cs>` OK; cross-ARTCC `ZLA15` (no delimiter) → `FAC_SEC` underscore split can't parse it → defaults to caller's own facility. Underscore convention is YAAT-invented; real client never sends it | `CrcClientState.Eram.cs:605-614` |
| Point-out state re-broadcast (change-tracking) | 🟡 | 🟢 | Pointout change re-sends EramTrack → `Eram.Pointouts` not in any fingerprint; relies on position churn | `AircraftChangeTracker.cs:507-515` |

### 4. ERAM↔STARS Handoff, QT & Consolidation
✅ QT take/force, cross-facility handoff-code resolution both directions (e.g. `Q2B`, `C44`), owner/handoff wire state, QT auto-track VATSIMism, STARS multi-sector consolidation (auto + CONS/DECON), consolidation-aware redirect + auto-accept suppression, per-(aircraft,TCP) shared display state. ⬜ ERAM-sector consolidation (multi-ERAM-sector ownership).

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| ERAM handoff initiate via CRC keyboard | 🟡 | 🔴 | Implied `<sector> <FLID>` sets HandoffPeer → logic exists, ERAM dispatcher doesn't call it | A/H1 (dup) |
| ERAM handoff accept/recall via CRC keyboard | 🟡 | 🔴 | Bare `<FLID>` accept/recall → verb `""`/FLID → FORMAT | A/H1 (dup) |
| `QT` drop divergence (QX) + NOT-YOUR-TRACK guard | 🟡 | 🟢 | Real CRC drops via `QX` → wired to non-spec `QT D` | A/H3 (dup) |
| Force-accept/steal `/OK <FLID>` | 🟡 | 🟢 | ERAM keyboard `/OK <FLID>` → FORMAT (only `QT /OK`) | A (dup) |

### 5. Data Blocks (FDB/LDB/CDB/RDB + fields)
✅ FDB field population (callsign, CID, altitudes, GS, scratchpad, beacon, dest, type), dwell-emphasis lock, VCI (`//`). ⬜ DB-FIELDS toolbar toggles (client filters).

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| Format selection FDB vs paired/unpaired LDB vs CDB | 🟡 | 🔴 | Per-sector FDB/LDB/CDB → hard-coded `Fdb`, not per-subscriber | B/H5 · `DtoConverter.cs:560` |
| Leader direction & length (`QN` + implied) | 🟡 | 🟠 | Numeric `1-9` dir, `/0-3` len, bare `1 <FLID>` → only compass *text* parsed; numeric & bare → FORMAT | A · `CrcClientState.Eram.cs:176-192` |
| Quick Look (`QL`) | 🟡 | 🟠 | Promotes quicklooked sectors' tracks to FDB → set stored but never read (inert; format constant) | B · `EramRoomState.cs:20` unused in DtoConverter |
| Changing DB types / FLID-cycle & `QP <FLID>` minimize | ❌ | 🟠 | Bare `<FLID>` cycles LDB↔FDB → FORMAT; no FDB-open state | A/B · grep `FdbOpen` empty |
| FDB Line-4 HSF heading/speed/free-text (`QS`) | 🟡 | 🟠 | `QS <hdg>`/`QS /<spd>`/`` QS `text `` → writes STARS Scratchpad1; AssignedHeading/Speed hard-coded null | `DtoConverter.cs:616-617` |
| Distance Reference Indicators (`QP J`/`QP T`, HaloType) | ❌ | 🟠 | DRI halos → HaloType hard-coded None; `QP J/T` misread as point-out to sector J/T | B · `DtoConverter.cs:564` |
| Non-RVSM box & SatComm `*` (equipment-derived) | 🟡 | 🟢 | Derived from IsRvsmCapable/IsSatCommCapable → hard-coded true/false, never from EquipmentSuffix | `DtoConverter.cs:210-211` |
| Conflict Data Block (CDB) + EramConflictStatus | ❌ | 🟢 | CDB for Mode-C intruders → ConflictStatus NoConflict, Format never Cdb | E |
| Range Data Block (RDB) + CrrGroup | 🟡 | 🟢 | RDB = nm to CRR group → CrrGroup hard-coded null; no LF support | F |
| EramDataBlock create/delete Id format | 🟡 | 🟢→🔴* | create=bare callsign, delete=`CALLSIGN`+cs (see Wire dim, rated 🔴 there) | H/H6 |
| Field-E status values (CST/FRZN/beacon-mismatch) | 🟡 | 🟢 **(UNVERIFIED)** | CST/FRZN from Status → Status hard-coded Normal | D |

### 6. Targets, Symbology & Conflict Alert
✅ EramTargets topic + core EramTargetDto (emit/update/delete).

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| Conflict Alert / STCA surfacing (flashing DB + STC list) | ❌ | 🔴 | EramShortTermConflicts topic + flashing → stub returns null; ConflictStatus NoConflict | E/H7 · `CrcBroadcastService.cs:117-121` |
| Correlated/uncorrelated symbol-type engine | 🟡 | 🟠 | 8 symbol types per return → all `CorrelatedBeacon` | C · `DtoConverter.cs:265` |
| Ident beacon symbol | ❌ | 🟠 | IsIdenting → IdentingBeacon → never read | C · `DtoConverter.cs:254-269` |
| Primary (transponder off/standby) symbol | ❌ | 🟠 | Standby → primary symbol, suppress Mode-C → still CorrelatedBeacon w/ altitude | C · `DtoConverter.cs:261,265` |
| MCI / Uncorrelated Beacon (above/below CA floor) | ❌ | 🟠 | Uncorrelated beacon vs MCI → no correlation test | C |
| Target History trail (EramTargetHistories) | ❌ | 🟠 | Dedicated topic/DTO → grep none | F |
| Conflict-detector fidelity vs ERAM | 🟡 | 🟠 | 4-min, 5/3nm≤FL230, DB-alt, facility-gated → STARS-shaped 5s/3nm flat | E |
| EramTargetDto.GroundSpeed (Key 6) | 🟡 | 🟠 | Populated → omitted (null) while every sibling converter sets it | `DtoConverter.cs:254-269` |
| Code-1200 / VFR symbol | ❌ | 🟢 | 1200 → Vfr → CorrelatedBeacon | C |
| Reduced-separation symbol (≤FL230) | ❌ | 🟢 | ReducedSeparation dot → CorrelatedBeacon | C |
| WasModeCPreviouslyReceived / BlinkSpc (SPC blink) | 🟡 | 🟢 **(UNVERIFIED)** | Emergency codes blink → BlinkSpc hard-coded false | `DtoConverter.cs:266-267` |
| CDB for Mode-C intruders | ❌ | 🟢 **(UNVERIFIED)** | Blinking CDB → none | E |
| Ground targets aircraft vs heavy (Top-Down) | 🟡 | 🟢 **(UNVERIFIED)** | ERAM: B757 *not* heavy → IsCwtHeavy marks B757 heavy | see VATSIMisms dim |

### 7. ERAM Command Reference (MCA)
✅ ProcessEramMessage dispatch + result-DTO contract, `QT` (+`QT D`/`/OK`), `QL`. ⬜ client-side `SR/QD/WR/MR/AR` + keyboard shortcuts.

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| Implied commands (bare `<FLID>`, `//`, `1-9 <FLID>`, `/0-3 <FLID>`, `<sector> <FLID>`) | ❌ | 🔴 | Server parses implied → no default arm → FORMAT | A/H2 |
| Handoffs initiate/accept/recall/force | ❌ | 🔴 | see H1 | A/H1 |
| `QX` drop | ❌ | 🔴 | see H3 | A/H3 |
| `QZ` vs `QQ` altitude semantics | 🟡 | 🔴 | QZ=assigned, QQ=interim → **inverted** | G/H4 · `CrcClientState.Eram.cs:410-491` |
| `QN` implied preface (VCI//, numeric leader dir/len, positioning) | 🟡 | 🟠 | Numeric leader → text-only parse; positioning/handoff-preface absent | A · `CrcClientState.Eram.cs:112-192` |
| `QR` reported altitude (CERA) | 🟡 | 🟠 | Sets CERA → **mis-wired to route display** | G · `CrcClientState.Eram.cs:59` |
| `QP` point-out (minimize/DRI forms) | 🟡 | 🟠 | `QP <FLID>` minimize, `QP J/T` DRI → only initiate/ack | B |
| `QU` route display + amend-direct | 🟡 | 🟠 | display **and** route-amend → display only (straight-line) | A/see FP dim |
| `AM` amend (TYP/BCN/SPD/ALT/RMK/RTE) | 🟡→❌ | 🟠 | typed `AM` field/route edit → FORMAT (structured FPE only) | A |
| `DM` activate flight plan | ❌ | 🟠 | activates FP (QT prereq) → FORMAT | A/I |
| `QB` assign/request beacon, equip, voice | ❌ | 🟠 **(UNVERIFIED)** | assign forms → FORMAT (only 4-digit view client-side) | A |
| `VP` create VFR flight plan | ❌ | 🟠 **(UNVERIFIED)** | `VP <type> <route> <FLID>` → FORMAT (STARS has it) | A |
| `QF` flight-plan readout | 🟡 | 🟢 **(UNVERIFIED)** | Zulu/CID/owning-sector/remarks → omits them; uses live IAS not filed cruise | `CrcClientState.Eram.cs:194-220` |
| `QS` HSF fourth-line free-text | 🟡 | 🟢 **(UNVERIFIED)** | dedicated ERAM 4th line → STARS Scratchpad1 | `CrcClientState.Eram.cs:493-517` |
| `LA`/`LB`/`LC` readouts | ❌ | 🟢 **(UNVERIFIED)** | geo/speed readout → FORMAT | A |
| `QH` freeze / `LF` CRR group / `RF` force FP transfer | ❌ | 🟢 **(UNVERIFIED)** | each verb → FORMAT | A/F |

### 8. Flight Plans & Amendments
✅ Structured AmendFlightPlan (FPE hub method), CreateFlightPlan + VP-via-STARS, RequestNewBeaconCode. ⬜ `SR` (open FPE).

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| `QZ` assigned/VFR/OTP/block | 🟡 | 🔴 | assigned altitude → sets **interim** | G/H4 |
| `QQ` interim/local/proc/reported/clear | 🟡 | 🔴 | interim tier → sets **assigned**; missing R-prefix, override, multi-FLID | G/H4 |
| `QU` route **amendment** (direct-to, FRD insert) | ❌ | 🟠 | rewrite FP route → only draws a line, never mutates Route | A · `CrcClientState.Eram.cs:319-358` |
| `AM` field/RTE amendment (typed MCA) | ❌ | 🟠 | parse `AM` tokens → FORMAT | A |
| `QF` readout | 🟡 | 🟠 | full field set → omits Zulu/CID/sector/remarks | `CrcClientState.Eram.cs:210-219` |
| `QR` CERA | 🟡 | 🟠 | reported altitude → mis-wired to DispatchRd | G |
| `QU` route **display** fidelity | 🟡 | 🟠 | filed-route polyline through fixes → straight-line dead-reckoning stub | `CrcClientState.Eram.cs:669-686` |
| `QB` beacon/equipment assignment via MCA | 🟡 | 🟠 | assign code/equip → FORMAT (auto-gen via separate hub method) | A |
| `QS` HSF fourth-line | 🟡 | 🟠 | ERAM 4th-line fields → STARS Scratchpad1 | dup DB dim |
| `DM` activate FP | ❌ | 🟢 **(UNVERIFIED)** | Proposed→Active → FORMAT; all plans born Active | A/I |
| FlightPlanDto 32-field readout exposure | 🟡 | 🟢 | full field set → WakeTurb='L', RVSM=true, EDT/ATD/fuel=0 placeholders | `DtoConverter.cs:137-233` |
| ParsedAltitude block/above forms | ❌ | 🟢 **(UNVERIFIED)** | BlockLowAltitude/IsAbove → hard-coded null/false | `DtoConverter.cs:235-252` |

### 9. CRC Wire DTO / Topic Contract
✅ Topic envelope shape, EramTargets (11 fields), EramTracks (22 fields), EramDataBlocks (10 fields), EramPointout nested DTO, EramSectorConfiguration, EramRouteLines + Receive/Delete, ProcessEramMessage inbound + result DTO, enum value+string parity. **This backbone is the strongest part of the emulation.**

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| EramDataBlocks delete-ID format mismatch (ghost blocks) | 🟡 | 🔴 | delete id == create id → create bare / delete `CALLSIGN`+cs → never removed | H/H6 · `DtoConverter.cs:558` vs `CrcBroadcastService.cs:1303-1306` |
| EramTargetHistories topic + DTO | ❌ | 🟠 | history-trail topic → none | F |
| EramShortTermConflicts topic + DTO | ❌ | 🟠 | STCA topic → stub null | E/F |
| EramCrrGroups topic + DTO + inbound cmds | ❌ | 🟢 | CRR group topic → stub null, inbound NilAck | F |

### 10. Views / GeoMaps / Toolbars / NEXRAD / Settings
✅ MCA/Response-Area pipeline, NEXRAD (NexradData topic), EramSectorConfiguration (VVL + CRR color), QL display toggle. ⬜ (correctly client-side, but noted where a **data feed** is needed): Beacon Code View (needs QB + beacon data), Altimeter Settings View (needs `AR`/`QD` + altimeter data), Weather Station Report (needs `WR` + METAR), GeoMaps/MR, Time View, Check Lists, toolbars/brightness/font/cursor menus, Settings, declutter, scope markers.

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| Route display view (RD/QU, EramRouteLines) | 🟡 | 🟠 | polyline through filed route fixes → 2-point straight-line stub | dup FP · `CrcClientState.Eram.cs:669-686` |
| Continuous Range Readout (CRR) view + `LF` + EramCrrGroups | ❌ | 🟠 | server builds CRR groups, `LF` adds → `LF` FORMAT, topic stub null | F · `CrcBroadcastService.cs:117-121` |

> Note: the data-backed views (Beacon Code, Altimeter, Weather Station) are catalogued N/A-client for *rendering*, but each depends on a server command/data feed (`QB`, `AR`/`QD`, `WR`) that is **absent** — so they are functionally unusable end-to-end. Tracked under their command dimensions.

### 11. VATSIMisms & Autotrack
✅ `.autotrack`/UpdateAutoTrackAirports, delta semantics (add/`-X`/none/steal), departure acquisition, list-echo, scenario-preseeded + generator autotrack, Top-Down ground-target data publication + DB fields, TDM GeoMap flag. ⬜ Top-Down toggle/render (Ctrl+T), single-click track, declutter, scope markers (all client gestures).

| Feature | St | Sev | Expected → Actual | Theme / cite |
|---|---|---|---|---|
| FAA/ICAO departure-ID equivalence for autotrack | 🟡 | 🟠 | match FAA *or* ICAO id → only `dep == id \|\| "K"+id` (CONUS K-prefix only) | `RoomEngine.cs:1854`, `TickProcessor.cs:937,1378` |
| Ground-target heavy icon (IsHeavy) | 🟡 | 🟢 | ERAM: B757 *not* heavy → IsCwtHeavy incl. CWT 'E' marks B757 heavy | `DtoConverter.cs:1600-1604` vs `WakeTurbulenceData.cs:46-48` |

---

## Completeness-critic findings (missed by the per-dimension finders)

| # | Item | Sev | Why it matters | Cite |
|---|---|---|---|---|
| C1 | **FLID resolution by CID and assigned beacon code** (not just callsign) | 🟠 | Spec: FLID = ACID *or* beacon *or* CID (eram.md:1589). YAAT generates CIDs and broadcasts CID+beacon, but `FindAircraft` matches callsign only → typing a visible CID/beacon returns "FLID not found" on *every* `<FLID>` command | `RoomEngine.cs:947` |
| C2 | FDB Field-E SPC text values (HIJK/RDOF/EMRG/ADIZ/LLNK/AFIO) | 🟢 | Client-rendered from beacon code; STARS path has `GetForcedSpcs`, but ERAM ADIZ(1276)/AFIO(7777) never enumerated | eram.md:652-657 |
| C3 | Multiple-FLID batch entry (`QQ 110 A/B/C`, `QU`, `LF`) | 🟢 | Dispatcher pulls a single `firstCallsign`; slash-separated lists affect ≤1 aircraft silently | `CrcClientState.Eram.cs:22,31,44` |
| C4 | `QQ` override tokens `/TT` and `///` (not `/OK`) | 🟢 | QQ uniquely overrides logic checks with `/TT`/`///`; DispatchQq recognizes neither | eram.md:1017 |
| C5 | External-ARTCC edit lock persists even with `/OK` | 🟢 | Distinct auth rule: `/OK` overrides same-facility checks but must **not** allow editing a flight owned by a different ARTCC | eram.md:48 |

Critic's overall read: coverage of the spec's Contents list is thorough; the display/wire contract is the strong part; **gaps concentrate in MCA input-parsing fidelity**, not the wire.

---

## Prioritized remediation checklist

**Tier 1 — core CRC-ERAM workflows broken (do first):**
- [x] **Add an implied-command dispatch arm to `ProcessEramMessage`** (root cause A) — route bare `<FLID>`, numeric leader `1-9`, `/len`, `<sector> <FLID>`, and `/OK <FLID>` instead of `default → FORMAT`. Unblocks handoffs, drop, datablock toggle/positioning. *(#243, `DispatchImplied` + per-sector `EramRoomState.FdbOpen`.)*
- [x] **Wire ERAM handoffs over the CRC path** (H1) — initiate/accept/recall/force → existing `TrackCommandHandler.HandleHandoff`/`HandleAccept`/`HandleForceHandoff`. Mirror the STARS implementation (`CrcClientState.Stars.cs:364-427`). *(#244; also fixed force-take leaving stale `HandoffPeer`.)*
- [x] **Handle the `QX` drop verb** (H3) → `TrackEngine.HandleDrop` (which correctly clears HandoffPeer/HandoffInitiatedAt/CreatedByOwner); retire or alias the non-spec `QT D`. *(#245, `DispatchQx`; removed `QT D`.)*
- [x] **Fix the `QZ`↔`QQ` inversion** (H4) — QZ writes assigned, QQ writes interim; add QQ R/L/P sub-modes + clear forms. *(#246: QZ amends the flight plan via AmendFlightPlan incl. VFR/OTP; QQ interim/R/L/P/clear with interim⊥procedure; deleted redundant `Stars.AssignedAltitude`. QZ block-altitude split to a follow-up.)*
- [x] **Fix the EramDataBlocks delete-ID mismatch** (H6) — align create/delete id (adopt the canonical vsrs id format). *(#247: create id now `CALLSIGN{callsign}`.)*
- [x] **Compute data-block `Format` per subscribing sector** (H5) — FDB if owned/handoff-to-me/quicklooked/manually-open, else paired/unpaired LDB (mirror `vsrs eram.rs:384-446`). Also makes QL and the inbound-handoff cue work. *(#248: `ComputeEramDataBlockFormat`, recomputed per subscriber; reads QuickLook + FdbOpen.)*
- [x] **Publish ERAM conflict alerts** (H7) — implement `EramShortTermConflicts` topic + DTO from the existing `ConflictAlertState`; set `ConflictStatus`. *(#249: `EramShortTermConflictDto` {Id, bare AircraftIdA/B}, `ToEramShortTermConflict`, initial-data + incremental `BroadcastConflictAlertsAsync` + delete-on-removal; FDB `ConflictStatus`=ControlledControlled via `ComputeEramConflictStatus` + a change-tracker re-send. Aviation-reviewed: ship as interim.)*
- [x] **ERAM STCA detector retune** (H7 follow-on) — en-route model instead of the terminal detector. *(#266: `EramConflictDetector` + `EramConflictState` (own `ESTCA_` set) with a 4-min swept-CPA probe, 5 nm / 3 nm-≤FL230 lateral, data-block-altitude vertical envelope (§381/§383), no corridor suppression; `ProcessEramConflictAlerts` + `BroadcastEramConflictAlertsAsync` with the §377 per-subscribing-facility gate (owner facility stamped per tick). Aviation-reviewed: fixed a units bug — QQ stored interim altitudes in feet not hundreds (CRC mis-render) — and the LIA>Proc>Interim data-block precedence. Deferred: uncorrelated Mode-C-intruder path (ControlledUncontrolled) with #250/#261; handoff-mid-conflict RDB re-attribution.)*

**Tier 2 — display fidelity & notable commands:**
- [ ] Target `SymbolType` engine (root cause C): ident, primary(standby), VFR(1200), MCI/uncorrelated-beacon, reduced-sep from existing transponder data.
- [ ] ERAM track `Status` (root cause D): coast-on-target-loss (CST), and a frozen field for `QH`.
- [ ] Publish `EramTargetHistories` (root cause F) from `AircraftState.PositionHistory`.
- [x] Field-E accepted indicator `Oxxx`/`Kxxx`/`OUNK` (populate RecentHandoffPeer/WasForced). *(#253: per-subscriber, previous-owner-only; 30 s sim-elapsed window (vsrs QU_AUTO_REMOVE_SECS precedent), previous owner keeps an FDB during the window; `O`=accept, `K`=force-take; set at the shared `TrackEngine.MarkRecentHandoffAccepted` choke point (manual accept / accept-all / auto-accept / force), cleared on drop.)*
- [ ] `QR` → controller-entered reported altitude (currently mis-wired to route display).
- [x] `QU` route amendment (direct-to + FRD) and filed-route polyline for `QU` display. *(#255: polyline traces `NavigationRoute`/route-expansion fixes truncated at minutes×gs (`/M`→destination + underlined X); toggle/clear-all/multi-FLID/30 s auto-remove sweep; amendment = present-position FRD + direct fixes + tail-keep (resumes the filed route at the direct fix — including when the fix lies on an airway already in the route, via `IsAirway`/`ExpandAirwaySegment`, per 7110.65 §4-2-5.a.3) via `AmendFlightPlan`; `<location>` left-click read from element index 6. Aviation-reviewed.)*
- [x] `AM`, `DM`, `VP`, `QB`(assign) MCA commands. *(#256: `AM` amends TYP/BCN/SPD/ALT/RMK (+ field numbers 3/4/5/8/11) and splices routes via `RTE` (new `RouteSplicer`, join/resume/replace + `[`/`]` dep/dest swap, 7110.65 §4-2-5); `AM <FLID>` = readout. `DM` = graceful activation accept (YAAT plans born active). `VP` files a VFR plan on an existing track (attach-only, no auto-altitude/beacon draw). `QB` auto/discrete-beacon (octal-validated) / equipment-suffix / voice-type. Voice type modeled on vNAS `VoiceType`, canonical in FP remarks (`/v//r//t/`, full implied) via new `FlightPlanVoice` — derived on amend + scenario load, synced from the `SetVoiceType` hub. Aviation-reviewed.)*
- [ ] `QS` HSF fourth-line fields (heading/speed/free-text) instead of STARS Scratchpad1.
- [ ] `QP` point-out: fix inverted `ClearEramPointout` caller-auth; typed `QP A <sector> <FLID>`; `QP <FLID>` minimize.
- [ ] **C1: FLID resolution by CID / beacon code** in `FindAircraft`.
- [ ] EramTargetDto.GroundSpeed population; leader-direction numeric parse.

**Tier 3 — niche / low incidence:**
- [ ] CRR groups (`EramCrrGroups` topic + `LF`), RDB/CDB, DRI halos (`QP J/T`), Non-RVSM/SatComm derivation, `LA`/`LB`/`LC` readouts, `RF` FP transfer, block/above altitudes, ParsedAltitude, ground-target B757 heavy exception, autotrack FAA/ICAO equivalence.
- [ ] C2–C5 parsing nuances (SPC Field-E values, multi-FLID batch, `/TT`/`///` override, external-ARTCC edit lock).

---

## Caveats & verification notes

- **Reconciliation:** 80 gaps after applying verifier verdicts (53 confirmed, 14 reclassified in status/severity, 2 finder false-positives refuted and dropped). Severities are the verifier-corrected values.
- **Unverified (13, all `low`):** the one gap whose verifier agent died — *Point Outs → "Inter-facility point-out target resolution"* — was re-verified by hand and **confirmed** (see dim 3). The remaining 13 unverified gaps are all `low`-severity, not individually adversarially checked because verification was capped at the 10 highest-severity gaps per dimension; they carry the finder's classification (flagged **(UNVERIFIED)** in the tables). Re-verify before acting on any of them.
- **Duplication is intentional in the tables** (per-dimension traceability): H1 appears under dims 2, 4, 7; H3 under 1, 7; H4 under 7, 8. The root-cause section deduplicates.
- **YAAT vs vsrs framing:** for interactive-control features (handoffs, pointouts, amendments) vsrs is correctly "n/a" (read-only by design); the authorities there are the spec + decompiled CRC. For display/wire features vsrs was used as the concrete reference (and YAAT matches it on the core EramTarget/EramTrack contract).
- **Not a defect:** the FDB column-0 "R" not-your-control indicator is client-rendered from the wire `Owner` field (Key 3), which YAAT emits correctly.

*Audit artifacts (session-local): reconciled digest and per-gap evidence in the workflow transcript `journal.jsonl`; run `wf_140b5ca2-bd6`.*
