# ERAM audit remediation — Tier 2 & Tier 3

Working checklist for the remaining medium/low ERAM-audit issues. Parent audit: [`eram-audit.md`](eram-audit.md). High-severity Tier 1 is fully shipped (#243–#249, #266) and closed.

**Standing rule:** review the relevant section of `docs/crc/eram.md` before each issue. Each issue: TDD (failing test first) → implement → aviation-sim-expert review for anything with aviation semantics → build clean (`-p:TreatWarningsAsErrors=true`) → `pwsh tools/test-all.ps1` → commit (yaat-server commits reference `Closes https://github.com/leftos/yaat/issues/N`).

Chosen order (bugs → root-cause → display fidelity → commands → low):

- [x] **#254** QR → controller-entered reported altitude (CERA). Unbind `QR` from `DispatchRd`; add `DispatchQr` setting `ac.Eram.ControllerEnteredAltitude` (hundreds). Field already wired to CRC. *(yaat-server `DispatchQr` + verb-switch rebind; `EramWire` test helper drives the switch end-to-end.)*
- [x] **#260** Populate `EramTargetDto.GroundSpeed` (Key 6, currently null) + parse numeric leader direction (`QN` + implied `1-9`). *(GroundSpeed added to `ToEramTarget`; `TryParseLeaderDirection` now accepts numeric keypad digit 1-9 matching CRC's `(LeaderDirection)int.Parse`.)*
- [x] **#258** Point-outs: inverted `ClearEramPointout` caller-auth (now originating sector); typed `QP A <sector> <FLID>`; `QP <FLID>` minimize (new point-out-forces-FDB model + `EramRoomState.PointoutMinimized`); inter-facility concatenated-spec parse. *(`ComputeEramDataBlockFormat`/`ToEramDataBlock` gained subscriber-facility + minimize-set params; both broadcast sites updated.)*
- [x] **#259** FLID resolution by CID / assigned beacon code in `RoomEngine.FindAircraft` (root cause I; cross-cutting). *(Broadened `FindAircraft` (callsign→CID→beacon, callsign-priority); `HandleProcessEramMessage` now resolves a typed last-token FLID since CRC sends typed input as raw tokens, not picks. `EramWire.Typed` test helper.)*
- [ ] **#250** Target `SymbolType` engine: ident / primary(standby) / VFR(1200) / MCI-uncorrelated / reduced-sep from transponder data (root cause C).
- [ ] **#251** ERAM track `Status`: coast-on-target-loss (CST) + frozen field for `QH` (root cause D).
- [ ] **#252** Publish `EramTargetHistories` topic + DTO from `AircraftState.PositionHistory` (root cause F).
- [ ] **#253** Field-E accepted indicator `Oxxx`/`Kxxx`/`OUNK` (populate `RecentHandoffPeer`/`WasForced`).
- [ ] **#255** QU route amendment (direct-to/FRD) + filed-route polyline display.
- [ ] **#256** AM / DM / VP / QB flight-plan MCA commands.
- [ ] **#257** QS writes FDB HSF fourth-line (heading/speed/free-text), not STARS Scratchpad1.
- [ ] **#261** (low) Niche display grab-bag: CRR groups, RDB/CDB, DRI halos, Non-RVSM/SatComm, LA/LB/LC, RF, QH, block altitudes, ground-target B757, autotrack FAA/ICAO. Re-verify each before acting (UNVERIFIED in audit); surface scope to user.
- [ ] **#262** (low) MCA parsing nuances: SPC Field-E codes, multi-FLID batch, QQ `/TT` `///` override, external-ARTCC edit lock. Re-verify each.

Delete this file once all issues are closed (per CLAUDE.md issue-plan convention).
