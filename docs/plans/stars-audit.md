# STARS Behavioral Sweep — YAAT server emulation vs. CRC (#372)

**Date:** 2026-08-21
**Scope:** The counterpart of the #367 ERAM behavioral sweep, applied to `Vatsim.Nas.Crc.Ui.Displays.Stars*`: every command string CRC's STARS display composes, and every DTO field its renderers read, verified against yaat-server. Wire-DTO *layouts* were out of scope (already guarded by `CrcWireContractTests` + `docs/crc-wire/messaging-contract.json`); this sweep covers grammar and semantics.
**System under audit:** yaat-server CRC STARS emulation + shared `Yaat.Sim` track/pointout/coordination logic.

**Sources compared:**
1. **Decompiled CRC client** — `X:/dev/crc-decompiled/CRC/Vatsim.Nas.Crc.Ui.Displays.Stars*` (authoritative for what CRC sends and reads).
2. **Behavior spec** — `docs/crc/stars.md` (official vNAS CRC STARS manual; command tables at lines 1022–1443).
3. **yaat-server** — `src/Yaat.Server/Hubs/CrcClientState.Stars.cs`, `DtoConverter.cs`, `CrcBroadcastService.cs`, plus shared `Yaat.Sim` (`TrackEngine`, `CoordinationCommandHandler`, …).
4. **Rust read-only reference** — `X:/dev/vatsim-server-rs` for live-wire constant values where cited.

**Method:** 13 parallel dimension finders classified **385 behaviors**; **88 findings** recorded (70 gaps: 9 high / 30 medium / 31 low raw, plus 18 confirmed correctly-client-side). Every one of the **39 high/medium gaps got an independent adversarial verifier** prompted to refute it — **all 39 CONFIRMED** (none refuted, none reclassified; several verifiers narrowed scope in their reasoning without changing the verdict). A completeness critic then read `stars.md` against the union of coverage and surfaced **7 additional misses** (2 high). The 31 low-severity gaps carry the finder's classification, flagged **(UNVERIFIED)** — re-verify before acting on any of them. Four of the highest-impact confirmed findings (PRA unit, MultiFunc C, CA-inhibit ordering, FLID arms) were additionally re-verified by hand post-run.

---

## Executive summary

The STARS emulation is in **much better shape than ERAM was**: the wire envelope, enum values, pick parsing, topic-echo pattern, the DCB/PrefSet/CRDA/RBL negative space, and the core track/handoff/pointout happy paths are all correct, and the client-local vs server-bound command split matches CRC exactly in both directions. The gaps concentrate in three places: **(1) the MultiFunc (F7) dispatcher**, which recognizes an invented consolidation grammar the real client can never send while silently acking everything it doesn't recognize; **(2) write-paths for wire fields CRC renders** — half a dozen DTO fields are published and change-tracked but have no writer, so the CRC features that read them can never activate; and **(3) secondary grammar forms** (coordination receiver entries, handoff-key recall, TCP shorthand parity, clear-forms) where the primary form works and the documented variants fail or misroute.

**Headline defects** (the 9 raw high-severity findings collapse to 5 distinct; the critic added 2 more):

| # | Defect | Effect |
|---|--------|--------|
| H1 | **CRC-keyboard consolidation is entirely inoperative** — CRC sends `C{RECEIVING}{SENDING}[+]` / bare `C`; the server only parses an invented `D+{sending}{receiving}` / `D-` grammar that CRC can never send (its own `D` handler is local and throws on `D+xxx`), with the operand order additionally reversed vs the manual | Every F7 consolidation entry silently no-ops; the server's whole `CrcMultiFuncConsolidate`/`Deconsolidate` path is dead code |
| H2 | **Pilot-reported altitude is divided by 100 twice** — `TrackEngine` stores hundreds, `DtoConverter` divides again | Every PRA below 10,000 ft renders `000*` in LDB/FDB and the coast/suspend list |
| H3 | **CA inhibit never deletes the active conflict from CRC scopes** — the dispatch purges `ConflictAlerts.Conflicts` synchronously *before* `CrcConflictAlert` queries that same dict (further filtered to `IsAcknowledged`), so the delete broadcast is provably dead code and the tick diff can't emit it either | The non-inhibited partner track flashes CA indefinitely on every subscribed scope |
| H4 | **The `**` command family is missing** (convert incoming pointout to handoff; force quicklook own-TCP/list/ALL) — and misroutes: `**`+slew becomes a pointout to TCP `*`, `**ALL`/`**2B` become scratchpad writes. `StarsTrackDto.ForcedPointoutsTo` is published but no code ever writes it | A documented workflow silently performs wrong actions; the forced-pointout FDB path in CRC can never activate |
| H5 | **F13 coordination receiver-side is unreachable** — every entry unconditionally builds `CoordinationReleaseCommand`, so receiver acknowledge (`(LISTID) (FLID)`), bare-`<F13><ENTER>` single-pending ack, and `A*`/`M*` auto-ack toggles all fail (`HandleAcknowledge`/`HandleAutoAck` exist but only the YAAT-client RDACK/RDAUTO path reaches them); tower hold/send-held/recall/reorder/modify/delete forms all return NO TRK, and re-entering the create form appends a duplicate instead of deleting | The receiving TRACON cannot work the rundown list from CRC at all |
| H6 | **Manual handoff redirect is mis-rejected** *(critic)* — stars.md §Redirecting a Handoff: the *recipient* enters a handoff ID + slew; every server handoff path gates on `RequireOwnership`, so the recipient gets ILL TRK. `HandoffRedirectedBy`'s only writer is the consolidation transfer | A documented handoff workflow actively errors |
| H7 | **FLID by tabular-list line number never resolves** *(critic)* — stars.md:1024 defines FLID as ACID *or* beacon *or* tabular list number; `RoomEngine.FindAircraft` resolves callsign/CID/beacon only, even though the server itself assigns and broadcasts the line numbers (`StarsLineNumbers`) | Typing a visible list number into any server-bound keyboard command (TC, HD, CA K, F7 M/Y) returns NO TRK — the exact STARS analog of the ERAM sweep's FLID-by-CID/beacon catch |

**What is genuinely solid** (verified baseline — don't re-verify): the `ProcessStarsCommandDto` envelope and pick-struct parsing; all wire enum values (`StarsCommandType`, `LeaderDirection`, `StarsTpaType`, `TransponderMode`) vs the contract dump; the U+0080→backtick normalization; `DisplayError`/`LogError`/readout-area round-trip incl. own-TCP keying; the handled-command-type set exactly matching CRC's sent-type set in both directions; handoff accept single-broadcast state sequences feeding CRC's transition detectors; the per-TCP shared-state round-trip (incl. all 7 `KnownTypeDrift` entries — **verdict: benign**, see Caveats); consolidation topic semantics; the paused-sim change-key infrastructure (with the three specific holes listed below); DCB/PrefSets/CRDA/RBL/system-list negative space; NEXRAD.

---

## Systemic root causes

### A. `CrcMultiFunc` silently acks everything it doesn't recognize, and its consolidation grammar is invented
`CrcClientState.Stars.cs:918-951` dispatches only `M`/`Y`/`D+`/`D-`; everything else logs at Info and returns `null` = success-with-no-error. CRC's F7 switch (`InputManager.cs:2048-2067`) handles `B,D,E,F,L,N,O,P,Q,R,S,T,Z` locally and forwards the rest — including the real consolidation letter `C` (stars.md Table 32: `C{RECEIVING}{SENDING}[+]`, bare `C` = release) and the doubled-digit global leader `L##`+slew (`InputManager.cs:2264-2267` deliberately returns false). CRC's own `ProcessMultiFuncD` throws on `D+xxx` before sending, so the server's `D+`/`D-` handlers (`:1098-1178`, first token treated as *sending* — inverted vs the manual) are unreachable dead code.
**Cluster:** H1, MultiFunc `L` global leader direction (missing/medium — plumbing exists: `TrackEngine.HandleLeaderDirection`, DTO key 35, change key all wired, only the CRC write path is absent), unknown-sub-letter silent success (low), implied type-amend masking (E below).
**Fix shape:** re-key consolidation to `C` with the operand order swapped and bare-`C` = self-deconsolidate; add the `L` branch (honoring root cause B's keypad flag); return a FORMAT-style error for unrecognized MultiFunc bodies.

### B. `InvertNumericKeypad` (DTO Key 5) is never read on the STARS path
`ParseProcessStarsCommandArgs` (`CrcClientState.Stars.cs:1543-1589`) reads fields 0–4 only; the ERAM parser reads and applies it (`CrcClientState.Eram.cs:3014-3021`). Currently zero impact — every STARS leader-direction parse CRC performs is client-local — but the one wire input that needs it is exactly the missing MultiFunc `L##` handler (A). Implementing A without B mirrors directions for hardware-keyboard/inverted-keypad users.

### C. Published-but-never-written wire fields (the CRC feature reading them can never activate)
The DTO field, converter read, and `AircraftChangeTracker` key all exist; no command writes the state:
- `ForcedPointoutsTo` — sourced from `ac.Eram.ForcedPointoutsTo` (an ERAM field feeding the STARS DTO, `DtoConverter.cs:100`); no writer anywhere. Blocks the whole `**` force-quicklook family (H4) and the forced-pointout slew-ack.
- `IsDuplicateBeaconInhibited` — CRC forwards the owner's bare slew on a dup-beacon track expecting the server to set it; only snapshot round-trip touches it (missing/medium).
- Beacon-mismatch resolve — owner bare slew on `BeaconCode != AssignedBeaconCode` falls to CAACK + null; nothing clears the mismatch render (missing/medium).
- Rejected-pointout clear — `CrcImpliedBareSlew`'s retract branch and `TrackEngine.HandleRetractPointout` both require `IsPending`, so a `Rejected` pointout sticks on the wire forever (partial/medium, found independently by D4 and D6).
- `ForcedSpcs` beyond beacon-derived — no manual `(SPC)<SLEW>` force (low) and no MSAW computation, so the `LA` code can never appear while the MSAW-inhibit indicator still toggles (missing/medium).
- `IsSpcAcknowledged` — constant false is **correct** (no client→server ack path exists; Rust reference sends constant false); confined cost: acks never propagate across scopes in a room (low).

### D. Clear-forms store a sentinel instead of null
`TA 0` (F7 `M Δ000` and the RPO TA-clear) stores `0` into `int? TemporaryAltitude` — CRC renders `A000` forever, since it blanks only on null (`DisplayElementTracks.cs:1035`). Contrast `HandlePilotReportedAltitude`, which correctly maps 0→null (`TrackEngine.cs:459-463` vs `:525`).

### E. Implied-command capture order misroutes unhandled grammar
`CrcImpliedActionAsync`'s arm order makes unknown text land in the wrong feature instead of erroring: `EndsWith('*')` captures `**` (→ pointout to TCP `*`) and the Table-38 aircraft-type amend `F16*` (→ pointout to TCP `F16`); the SP1 catch-all swallows `**ALL` and 4-char types like `B738`; TCP-shorthand expansion runs before the 3-digit PRA arm, so PRA `001`–`009` become handoff attempts when a matching single-TCP subset exists; a bare `C` (omitted-sector host-ARTCC handoff, stars.md:426) never reaches the ERAM-handoff branch (`int.TryParse(paramStr[1..])` needs digits).
**Cluster:** H4's misroutes, type-amend (critic medium), PRA-shadow (low), bare-`C` (low), garbage-AID→FP-create (low).

### F. Entry-path asymmetry: the same grammar works on one route and fails on the other
- `<HND OFF><SLEW>` recall: any clicked-track Handoff goes to `CrcHandoff`, which rejects the empty tcpCode with INVALID ENTRY; `CrcHandoffAcceptOrRecall` is wired only to the no-click keyboard path (`CrcClientState.Stars.cs:366-372,395`).
- TCP shorthand (`G`, subset-only `2`): `ExpandTcpShorthand` is called only on the implied path (`:656`); the HND OFF path dispatches the raw code and the pointout path (`PO {tcp}`, `:870-878`) never expands — identical entries succeed implied and fail elsewhere.
- Keyboard-FLID resolution exists only for Handoff/TerminateControl/ConflictAlert (`ResolveKeyboardFlid:174`).

### G. Coordination (F13) is parsed as a single form
`CrcCoordination` (`:431-461`) splits into at most `[listId, callsign]` and always builds `CoordinationReleaseCommand`. Everything else in Tables 34/35 fails: hold `/`, positioned hold `/ ##`, hold-with-text, send-held, recall, reorder, modify-text (all → NO TRK because the remainder is fed to `FindAircraft`); delete-existing appends a duplicate (`HandleRelease` never checks); receiver ack / bare-Enter ack / `A*`/`M*` (H5). `HandleHold`/`HandleRecall`/`HandleAcknowledge`/`HandleAutoAck` all exist in `CoordinationCommandHandler` but only the YAAT-client RDH/RDR/RDACK/RDAUTO path reaches them.

### H. Flight-plan semantics divergences
- `FlightPlanStatus.Proposed` is never produced (`DtoConverter.cs:198,249` hardcode Active) → CRC's **TAB list is permanently empty** and pre-target VFR plans drop off the VFR list (partial/medium).
- VP (F9) is create-only (`DUP NEW ID` on re-entry) though stars.md says "create **or amend**" (partial/medium).
- The explicit `<F6>/<F9>` + click forms discard the click: only the Implied branch threads `spawnPos`, so the unsupported track spawns at LatLon(0,0) — off-scope, silently lost (partial/medium).
- DA flight-rules `.P` (VFR-on-top) is flattened to plain VFR before `FromRulesAndFeet` — which supports OTP (partial/medium).
- Lenient-accepts (all low): DA takes non-octal beacons, ignores unknown tokens; VP swallows malformed altitude; IC ignores the typed FLID and skips the FP check with non-STARS error text.

### I. Converter value semantics (display-realism cluster)
- **H2** PRA double-division (`DtoConverter.cs:71,123`).
- Standby transponder: `ToStarsTrack` has no standby gate — beacon + Mode-C altitude keep publishing after SQS, internally inconsistent with `ToEramTarget` (nulls on standby) and the ASDE-X suppression (partial/medium).
- `DisplayRequestedAltitude = (CruiseFeet ?? 0) > 0` forces the `R###` FDB timeshare on every IFR track — arrivals/overflights included — bypassing CRC's area-config gate; the Rust reference sends constant false (partial/medium).
- `StarsCoastPhase.Phase2` is never emitted (tracks delete after 12 s Phase1), so CRC's Phase2 rendering and the COAST/SUSPEND list are dead, and the coast ladder diverges from the documented 30 s + 5 min (2 lows).
- `ForcedSpcs` LN (law-enforcement) never derived (Rust reference does) (low).

### J. Re-send triggers that only fire on position churn (paused-sim / stationary staleness)
The change-key infrastructure is otherwise solid, with these holes: `StarsTrackFingerprint` misses Voice.Type and FlightRules-derived `IsMsawInhibited`; `GroundTargetFingerprint` misses TypeCode/IsHeavy/VoiceType (TDM datablock); `IsQueriedUntil` expiry is client-side time-decay relying on vNAS sweep-cadence re-sends YAAT doesn't do (the queried LDB extension sticks — and this generalizes to any decay-by-re-receive feature); conflict `IsAcknowledged` flips are never re-broadcast (other scopes keep alarming); coordination lists aren't cleared on scenario unload; deleted aircraft leak their line-number assignment (`Release` is only on the visibility-drop path).

### K. Missing topic publisher: `StarsConfiguration`
CRC subscribes `Topic.StarsConfiguration(facilityId)` and renders the SSA SECTOR PLAN field from it; `StarsConfigurationItemDto` exists with no publisher, so every scope shows the `CFG` fallback (missing/low, found by D9 + D10; `ConfigurationPlans` already exists in the parsed ARTCC config model).

### L. Identity/authorization gaps
UN pointout-reject has no recipient check (any third party can reject, low); manual handoff redirect blocked by `RequireOwnership` (H6); `CrcImpliedBareSlew` dispatches CAACK on *every* fall-through slew, mutating CA-ack state as a cross-surface side effect of unrelated gestures (noted, not filed separately).

---

## Verdict by dimension

Status legend: 🟡 partial · ❌ missing · ⬜ N/A client-side (server correctly uninvolved). Severity: 🔴 high · 🟠 medium · 🟢 low. All 🔴/🟠 entries were adversarially verified and confirmed; 🟢 entries are **(UNVERIFIED)** unless noted. Cites: `crc:` = decompiled CRC, `srv:` = yaat-server `src/Yaat.Server/`, `sim:` = yaat `src/Yaat.Sim/`.

### D1. Wire contract & envelope
✅ Envelope parse, all enum values vs contract, dual int/string enum arm, error/readout round-trip, U+0080 normalization, pre-normalized ParameterString, sent-type set parity.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| MultiFunc `L##` global leader (slew) | ❌ | 🟠 | falls through to server → unrecognized, silent success (`srv:Hubs/CrcClientState.Stars.cs:930-950`; engine exists `sim:Commands/TrackEngine.cs:536-540`) |
| `InvertNumericKeypad` Key 5 unread | 🟡 | 🟢 | ERAM parses+applies; STARS parser reads fields 0-4 only. Zero impact until `L##` lands (`srv:…Stars.cs:1543-1589` vs `Eram.cs:3014-3021`) |
| Ghost clicks / IsMiddleClick | ⬜ | 🟢 | blocked client-side; middle-click rides shared state — server correctly uninvolved |

Also: CRC-side bug (not ours) — the keyboard `L## FLID` form's regex `^([1-9])\1$ (\S+)$` has a mid-pattern `$` and can never match (`crc:…Stars.Input/InputManager.cs:2280`); only the slew form reaches any server.

### D2. Command dispatch & prompts
✅ 19-type dispatch parity, prompts, auto-submit, two-click handling, `CrcUnhandledCommand` verbs confirmed never sent (correct dead-code safety net).

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| MultiFunc consolidation `C…` | 🟡 | 🔴 | H1 (dup of D5a/b) — real grammar unhandled; server grammar unreachable + operand-reversed (invented in `662ecad7`) |
| `<HND OFF><SLEW>` recall | 🟡 | 🟠 | clicked-track Handoff always → `CrcHandoff` → INVALID ENTRY on empty tcp (`srv:…Stars.cs:366-372,345-348`) |
| VfrPlan voice form `<F9>(V\|R\|T)<SLEW>` | ❌ | 🟠 | clicked track discarded, <3 tokens → FORMAT; no STARS voice route (`srv:…Stars.cs:119-124,1374-1385`) |
| F13 sub-form grammar | 🟡 | 🟠 | G (dup of D4) — single-form parse |
| MultiFunc `L##` | ❌ | 🟢 | dup D1/D5 |
| `CrcUnhandledCommand` verbs | ⬜ | 🟢 | all 9 confirmed client-local incl. DCB SavePrefSetAs |

### D3. Always-server commands (IC, TC, DA, VP, CA, RP)
✅ IC/TC/TC-ALL/ghost-cleanup happy paths, RP forms 2/3, DA/VP core grammar + readout echo, pre-send gates, paused-sim propagation of all D3 mutations.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| CA inhibit never clears the active conflict | 🟡 | 🔴 | H3 — purge-before-query dead code (`srv:…Stars.cs:476-495` vs `srv:Simulation/TrackCommandHandler.cs:650-670`; diff can't rescue: `sim:Simulation/SimulationEngine.cs:1223-1239`) |
| VP amend of existing VFR plan | 🟡 | 🟠 | create-only → DUP NEW ID (`srv:Simulation/FlightPlanCommandHandler.cs:33-38`) |
| F9 voice form | ❌ | 🟠 | dup D2 |
| Explicit F6/F9 + click spawns at (0,0) | 🟡 | 🟠 | only Implied threads spawnPos (`srv:…Stars.cs:113-124` vs `:542-549`; `srv:Simulation/RoomEngine.cs:278`) |
| DA `.P` (VFR-on-top) → plain VFR | 🟡 | 🟠 | `ParseFlightRules` maps `.P`→"VFR"; OTP support exists unused (`srv:…Stars.cs:1479-1488`, `sim:FlightPlanAltitude.cs:54-66`) |
| IC ignores typed FLID / no FP check / `ALREADY TRACKED` text | 🟡 | 🟢 | non-STARS vocabulary, no NO FLIGHT/DUP TRK |
| DA accepts non-octal beacon | 🟡 | 🟢 | `4890` files instead of FORMAT (`srv:…Stars.cs:1292-1295`, contrast `:1074-1080`) |
| DA/VP readout echo keyed to primary TCP | 🟡 | 🟢 | secondary-position scope shows nothing (`srv:Hubs/CrcClientState.Messaging.cs:109-118`) |
| RP Form 1 rejects coasting sources | 🟡 | 🟢 | Parked-binding only; deliberate single-surveillance divergence, recorded |
| CA accepts entries without `K` | 🟡 | 🟢 | paramStr never inspected on slew form |
| Pre-send gates | ⬜ | 🟢 | client-side, correctly absent server-side |

### D4. Handoff / pointout / coordination
✅ Handoff initiate (full codes) both routes, accept/recall via bare slew + keyboard, interfacility Δ + `C{sector}`, single-broadcast accept sequences (transition detectors + `WasPreviouslyOwned` semantics), pointout initiate/ack/UN core, Tcp ULID-preserving mapping, all D4 change keys.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| `**` family (pointout→handoff, force quicklook) | ❌ | 🔴 | H4 — no handling + misroutes; `ForcedPointoutsTo` writerless (`srv:…Stars.cs:644-648,607`; `srv:Simulation/DtoConverter.cs:100`) |
| F13 receiver ack / bare-Enter / `A*`/`M*` | ❌ | 🔴 | H5 — always `CoordinationReleaseCommand`; `HandleAcknowledge`/`HandleAutoAck` CRC-unreachable (`srv:Simulation/CoordinationCommandHandler.cs:81-84,212-321`) |
| F13 tower forms (hold/send/recall/reorder/modify/delete) | 🟡 | 🟠 | G — remainder fed to `FindAircraft` → NO TRK; delete duplicates (`srv:…Stars.cs:438-456`) |
| `<HND OFF><SLEW>` recall | 🟡 | 🟠 | dup D2 |
| Sender slew dismissal of rejected (UN'd) pointout | 🟡 | 🟠 | `IsPending`-only branches; Rejected sticks forever (`srv:…Stars.cs:792-815`, `sim:Commands/TrackEngine.cs:494-503`) |
| TCP shorthand on HND OFF path | 🟡 | 🟠 | F — expansion only on implied path (`srv:…Stars.cs:332-357` vs `:656`) |
| TCP shorthand on pointout path (`G*`) | 🟡 | 🟠 | F — `PO {raw}`; `FindTcpByCode` full-code only (`srv:Simulation/TrackCommandHandler.cs:730-755`) |
| Pointout auto-ack for simulated positions | ❌ | 🟠 | handoff-parity gap: `ProcessAutoAccept` is handoff-only; solo-mode pointouts to virtual sectors flash forever (`srv:Simulation/TickProcessor.cs:1285-1333`) |
| UN reject recipient validation | 🟡 | 🟢 | any position can reject (`sim:Commands/TrackEngine.cs:483-491`) |
| F13 T/TE/TI, list move/resize, ZDE/ZDI | ⬜ | 🟢 | correctly client-local |

### D5a/D5b. MultiFunc (F7)
✅ M/Y core amend grammar, B/E/F confirmed fully client-local, *J/*P TPA shared-state round-trip, D+ readout dependency identified.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| Consolidation `C` grammar | ❌ | 🔴 | H1 (`srv:…Stars.cs:918-951`; order also inverted in `SplitTcpCodes:1220-1236`) |
| `D+`/`D-` handlers = dead code | 🟡 | 🔴 | H1 — CRC's local D handler throws before sending (`crc:…Input/InputManager.cs:2129-2188`) |
| `Y` PRA wire unit | 🟡 | 🟠 | H2 (`srv:Simulation/DtoConverter.cs:71,123`; test asserts state only, `CrcKeyboardStarsCommandTests.cs:117-126`) |
| `M Δ000` clear stores 0 not null | 🟡 | 🟠 | D — `A000` forever; also breaks RPO TA-clear (`sim:Commands/TrackEngine.cs:459-463`) |
| `L##` global leader | ❌ | 🟠 | A/B (dup) |
| `Y` PRA Mode-C precondition | 🟡 | 🟢 | no gate; PRA silently replaces live Mode-C readout |
| Unknown sub-letters → silent success | 🟡 | 🟢 | A — no FORMAT feedback |
| `L##` keyboard form | ⬜ | 🟢 | dead in CRC (malformed regex) — no server work possible |

Also noted: `IsModeCInhibited` and `Transponder.AssignCode` are mutated directly in the hub rather than via `RecordAndDispatch` — likely invisible to replay/bug-bundle action recording (`srv:…Stars.cs:1043,1086`).

### D6. Implied command
✅ Bare-slew accept/retract/ack core, `++NNN`/`+NNN`/`+text`/`.`-clear, `text*` pointout, TCP handoff, `C{sector}`, Δ interfacility, `NNN` PRA arm, SP1 fallthrough, *J/*P no-op consistency, SPC-ack locality (constant `IsSpcAcknowledged=false` confirmed correct), altitude-before-scratchpad ordering.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| `**` family | ❌ | 🟠 | H4/E (dup of D4's 🔴; D6 rated the implied-grammar half medium) |
| Owner slew on rejected pointout | 🟡 | 🟠 | dup D4 |
| Beacon-mismatch resolve slew | ❌ | 🟠 | C — falls to CAACK + null; mismatch renders forever (`srv:…Stars.cs:755-827`) |
| Duplicate-beacon inhibit slew | ❌ | 🟠 | C — `IsDuplicateBeaconInhibited` writerless (`srv:Simulation/DtoConverter.cs:74,93`) |
| Forced-pointout slew ack | ❌ | 🟢 | C — dead until `**` lands |
| PRA `001`-`009` shadowed by TCP shorthand | 🟡 | 🟢 | E — becomes handoff attempt |
| `(SPC)<SLEW>` force/un-force | ❌ | 🟢 | C — falls to SP1 write |
| UN third-party reject | 🟡 | 🟢 | dup D4 |
| New-AID manual acquisition | 🟡 | 🟢 | CALLSIGN MISMATCH unless exact sim callsign (arguably deliberate; non-STARS text) |
| Garbage AID → FP create | 🟡 | 🟢 | no AID-shape validation (`srv:…Stars.cs:1263-1343`) |
| *J/*P, SPC ack locality | ⬜ | 🟢 | correct |

Also: server dead arms confirmed harmless (digit-LDR, bare-slew implied IC — CRC handles both locally); fall-through CAACK side effect (L above).

### D7. Shared track state round-trip
✅ 12-field write → per-TCP read-back verified end-to-end; consolidation keying (pointout-accept redirected to attended parent, `MarkPreviousOwnerRetained` keyed by resolved owner); tick-gate serialization; **all 7 `KnownTypeDrift` entries resolved: benign** (see Caveats).

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| `IsQueriedUntil` expiry re-push | 🟡 | 🟢 | J — CRC decays it on re-receive; YAAT sends change-driven only, so the queried LDB extension sticks on stationary/paused tracks |
| Per-TCP selection under consolidation | ⬜ | 🟢 | server publishes verbatim + keys own writes correctly |

### D8. StarsTrackDto field semantics
✅ `IsAdsb=true` (matches live wire), `GroundTrack` = true track (CRC adds MagVar), hundreds units for altitudes, history order, ATPA TCP code forms, parked-datablock constant block (deliberate), fingerprint coverage apart from the two holes below.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| `ReportedAltitude` /100 twice | 🟡 | 🔴 | H2 |
| `ForcedPointoutsTo` writerless (ERAM field feeding STARS DTO) | ❌ | 🟠 | C/H4 |
| `DisplayRequestedAltitude` = any filed cruise | 🟡 | 🟠 | I — forces `R###` on every IFR FDB; Rust ref sends false (`srv:Simulation/DtoConverter.cs:72,124`) |
| `AssignedAltitude` clear (TA 000) | 🟡 | 🟠 | D (dup) |
| Standby transponder keeps beacon + Mode-C | 🟡 | 🟠 | I — no standby gate, inconsistent with ERAM/ASDE-X converters (`srv:Simulation/DtoConverter.cs:70-79` vs `:336-352`) |
| `StarsCoastPhase.Phase2` never sent | 🟡 | 🟢 | I — 12 s Phase1 → delete (`srv:Simulation/CrcVisibilityTracker.cs:484-504`) |
| Fingerprint misses VoiceType / FlightRules | 🟡 | 🟢 | J |
| `ForcedSpcs` LN code | 🟡 | 🟢 | I |
| `IsSpcAcknowledged=false` | ⬜ | 🟢 | correct (seed resolved) |

### D9. Datablock / SSA / ground rendering
✅ Scratchpad resolution, handoff chars, position symbols, PTL/J-ring inputs, history darkening, TDM datablock inputs, `ForcedSpcs` flags-union parse, MSAW-inhibit glyph derivation.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| PRA display value | 🟡 | 🔴 | H2 (dup) |
| Standby transponder | 🟡 | 🟠 | dup D8 |
| MSAW LA alert via ForcedSpcs | ❌ | 🟠 | C — no MSAW/low-altitude computation exists anywhere; inhibit toggles an alert that can never fire (`srv:Simulation/DtoConverter.cs:1274-1284`) |
| `DisplayRequestedAltitude` | 🟡 | 🟢 | dup D8 |
| `IsSpcAcknowledged` propagation | 🟡 | 🟢 | C — multi-scope rooms only |
| SSA SECTOR PLAN (`StarsConfiguration`) | ❌ | 🟢 | K (dup D10) |
| TDM ground-target fingerprint | 🟡 | 🟢 | J (dup D10) |
| SSA altimeter/winds = live VATSIM wx | ⬜ | 🟢 | structural emulation boundary — CRC fetches METARs from live VATSIM; scenario weather can diverge. Recorded so nobody "fixes" it with a dead endpoint |

Also noted: `IsMsawInhibited |= IsVfr` puts the `*` inhibited glyph on every VFR FDB — looks deliberate but changes line 1 for all VFR aircraft; confirm intent when touching MSAW.

### D10. Auxiliary topics & pushes
✅ Readout push own-TCP keying, unknown-topic graceful degrade, per-client topic echo (no #367 class), consolidation topic semantics + re-broadcast triggers, conflict initial data, GroundTargets WebSocket-critical equivalence, STC DTO id form.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| CA inhibit conflict deletion | 🟡 | 🟠 | H3 (dup of D3's 🔴) |
| `StarsConfiguration` publisher | ❌ | 🟢 | K |
| Conflict ack not re-broadcast | 🟡 | 🟢 | J — other scopes keep alarming |
| Line-number release leak on deletion | 🟡 | 🟢 | J — stale initial data + accelerated 99-wrap (`srv:Simulation/CrcBroadcastService.cs:1852-1855` only Release site) |
| Coordination lists not cleared on unload | 🟡 | 🟢 | J |
| GroundTarget fingerprint | 🟡 | 🟢 | J |
| Tower P-list seeding into StarsCoordination | ⬜ | 🟢 | dead wire traffic — CRC drops channel-less list ids; TWR lists are client-computed. Latent: `ToTowerListDto` constants would render a caution line if a config ever paired the id with a channel |

Also: coordination countdown timers convert sim→wall clock at broadcast; drift on pause/rate change until next broadcast (cosmetic).

### D11a/D11b. Negative space
✅ CRDA (fully client-side; all consumed track inputs published), RBL, dot commands, scope markers, DCB all-185-buttons, PrefSets, never-sent types — the negative space is exactly right in both directions.

| Feature | St | Sev | Expected → Actual |
|---|---|---|---|
| TAB list — `Proposed` never produced | 🟡 | 🟠 | H (root cause) — TAB permanently empty; VFR list pre-target leg dead (`srv:Simulation/DtoConverter.cs:198,249`) |
| COAST/SUSPEND list — Phase2 | 🟡 | 🟢 | dup D8 |
| Unknown MultiFunc → silent success | 🟡 | 🟢 | dup A |
| CRDA / RBL / .wx / DCB / PrefSets / never-sent types | ⬜ | 🟢 | all correct |

Also: NEXRAD refresh pushes are keyed by `room.CreatorArtccId` while CRC subscribes under its own ARTCC — a cross-ARTCC client would get only the snapshot (marginal); WX AVL is dark under scenario (non-live) weather by design; `.contactme` to a simulated pilot produces no pilot reaction.

---

## Completeness-critic findings (missed by every per-dimension finder)

| # | Item | Sev | Why it matters |
|---|---|---|---|
| C1 | **Manual handoff redirect** (recipient enters handoff ID + slew) | 🔴 | H6 — dedicated stars.md section (:438-440), not a table row, so no dimension owned it. `RequireOwnership` mis-rejects the recipient with ILL TRK; `HandoffRedirectedBy`'s only writer is the consolidation transfer (`srv:…Stars.cs:339,607,1209,1997`) |
| C2 | **FLID by tabular-list line number** | 🔴 | H7 — `FindAircraft` (`srv:Simulation/RoomEngine.cs:1205-1214`) resolves callsign/CID/beacon only; CRC resolves list numbers client-side only for the *local* D-digest and sends raw digits for TC/HD/CA-K/M/Y |
| C3 | **Aircraft-type amend `(A/C TYPE)<SLEW>`** with `*` right-padding (Table 38) | 🟠 | `F16*`+slew → pointout to TCP `F16`; `B738`+slew → scratchpad write. Silently does the wrong thing (root cause E) |
| C4 | **Secondary display deactivation must drop its owned tracks** (§Secondary STARS Displays) | 🟠 | `HandleDeactivateSecondaryPosition`/`Close` only unregister; tracks stay owned by a position no one is working (`srv:Hubs/CrcClientState.Secondary.cs:100-142`) |
| C5 | Bare-`C` omitted-sector host-ARTCC handoff | 🟢 | needs digits after `C`; consumed by shorthand arm or scratchpad (root cause E) |
| C6 | STCA eligibility gate — spec limits prediction to *associated tracks owned by your facility*; YAAT alerts on unowned/unassociated pairs too | 🟢 | numeric algorithm itself is a faithful implementation (5 s / 3 NM / 1000 ft / divergence / final-approach corridor — reassuring); the superset alerting is arguably a defensible trainer choice — decide, then document |
| C7 | Autotrack departure-acquisition runtime behavior + Table-10 assigned-by-controller WHO rule | 🟢 | machinery exists but audited by nobody; known wrinkle: `CrcMultiFuncM` beacon amend records `assignedByFacilityId: null`, so STARS-assigned codes can't attribute the auto-acquire |

---

## Prioritized remediation checklist

Issues filed per root-cause cluster (confirmed high+medium; lows ride along where the same fix covers them or stay in this report).

**Tier 1 — whole-feature outages:**
- [ ] **MultiFunc dispatcher** (A/B): re-key consolidation to `C{RECEIVING}{SENDING}[+]` + bare `C` (delete the unreachable `D+`/`D-` grammar, swap the operand order), add the `L##` global-leader branch honoring `InvertNumericKeypad` (Key 5), and return a FORMAT-style error for unrecognized MultiFunc bodies instead of silent success. → **#373**
- [ ] **PRA wire unit** (H2): drop the spurious `/100` in both `ToStarsTrack` and `ToParkedDataBlock`; add a DTO-level test (the existing test asserts stored state only). → **#374**
- [ ] **CA inhibit conflict deletion** (H3): capture the conflict ids *before* dispatch (or return the removed ids from `HandleInhibitConflictAlert`) and broadcast `DeleteStarsShortTermConflicts`; drop the `IsAcknowledged` filter. Optionally re-broadcast `IsAcknowledged` flips so other scopes silence (J). → **#375**
- [ ] **`**` family + `ForcedPointoutsTo` lifecycle** (H4/C): implement pointout→handoff conversion and force-quicklook (own/list/ALL + keyboard form), give `ForcedPointoutsTo` a STARS-side write/clear path (today an ERAM field feeds the STARS DTO), and fix the capture order so `**` never lands in pointout/scratchpad arms. → **#376**
- [ ] **F13 coordination grammar** (H5/G): parse Tables 34/35 — receiver ack, bare-Enter single-pending ack, `A*`/`M*` auto-ack, tower hold/`## `/text/send-held/recall/reorder/modify/delete — routing to the existing `CoordinationCommandHandler` verbs (all present, all CRC-unreachable today); make delete-existing actually delete. → **#377**
- [ ] **Manual handoff redirect** (H6): recipient-entered handoff ID + slew on an inbound handoff redirects it (populate `HandoffRedirectedBy`); relax `RequireOwnership` for exactly that case. → **#378**
- [ ] **FLID by tabular-list line number** (H7): add a line-number arm to `FindAircraft` (the assignments live in `room.LineNumbers`), for TC/HD/CA-K/M/Y keyboard forms. → **#379**

**Tier 2 — secondary forms & display fidelity:**
- [ ] **Handoff/pointout entry-path parity** (F): clicked-track empty-param Handoff → recall; `ExpandTcpShorthand` on the HND OFF and pointout paths; bare-`C` omitted-sector form. → **#380**
- [ ] **Pointout lifecycle** (C/L): clear Rejected pointouts on owner slew; validate UN reject recipient; auto-ack pointouts to simulated positions (parity with handoff auto-accept). → **#381**
- [ ] **STARS voice type** `<F9>(V|R|T)<SLEW>` → `FlightPlanVoice` (route exists only via ERAM QB / FP editor today). → **#382**
- [ ] **Flight-plan semantics** (H): VP amend-on-existing; thread the click location through the explicit F6/F9 branches (no more (0,0) spawns); `.P` → OTP; produce `FlightPlanStatus.Proposed` pre-association so the TAB/VFR lists work. Lenient-accept lows ride along. → **#383**
- [ ] **Bare-slew indication clears** (C): beacon-mismatch resolve; duplicate-beacon inhibit (both fields already published + change-tracked). → **#384**
- [ ] **Temporary-altitude clear** (D): `TA 0` → null (both CRC `M Δ000` and RPO TA-clear). → **#385**
- [ ] **Standby transponder STARS gating** (I): null beacon/Mode-C on standby, matching the ERAM/ASDE-X converters. → **#386**
- [ ] **`DisplayRequestedAltitude` semantics** (I): stop forcing `R###` globally (constant false per the live-wire reference, or model the selective per-track intent). → **#387**
- [ ] **MSAW low-altitude (LA) alert** (C): compute MSAW and publish via `ForcedSpcs`; wire the existing inhibit state to something real. → **#388**
- [ ] **Secondary-display deactivation drops owned tracks** (C4). → **#389**
- [ ] **Implied aircraft-type amend** `(A/C TYPE)*<SLEW>` (C3, fix with the E capture-order work). → **#390**

**Tier 3 — lows (in-report only; re-verify before acting — all UNVERIFIED):**
coast Phase2 + COAST/SUSPEND ladder; `IsQueriedUntil`-class re-send decay (generalize: any client-side time-decay needs a re-send or fingerprint term); fingerprint holes (VoiceType/FlightRules, TDM type/heavy/voice); conflict-ack re-broadcast; line-number release on deletion; coordination-list clear on unload; `StarsConfiguration` publisher (SSA SECTOR PLAN); `ForcedSpcs` LN; `(SPC)<SLEW>` manual force; PRA 001-009 shadow; PRA Mode-C precondition; IC/DA/VP lenient-accepts + non-STARS error vocabulary; readout echo on secondary positions; RP Form 1 coasting sources; UN third-party reject; STCA eligibility gate decision (C6); autotrack audit + beacon-assigner attribution (C7); hub-direct mutations bypassing `RecordAndDispatch` (`IsModeCInhibited`, `AssignCode`); CAACK cross-surface side effect.

**Infrastructure:** add `tests/Yaat.Server.Tests/Harness/StarsWire.cs` (the STARS counterpart of `EramWire.cs`) with the first Tier-1 fix — every STARS wire test currently hand-rolls its MessagePack frame. Update the `KnownTypeDrift` comment in `CrcWireContractTests.cs` (see below) with the next yaat-server commit.

---

## Caveats & verification notes

- **The 7 `StarsTrackSharedStateDto` `KnownTypeDrift` entries are BENIGN — investigation closed.** Three independent reasons: (1) CRC guards every drifted field with `HasValue` before applying (the one unconditional apply, `IsQueriedUntil`, is kept nullable by YAAT); (2) entries exist only for TCPs that wrote them, and the *write* DTO is itself non-nullable, so the echo is an identity round-trip; (3) server-created entries mutate in place and their defaults (`false`, `LeaderDirection.Default=5`, `TpaType.None=0`, `0.0`) exactly equal CRC `Track`'s initial values, so applying them is a no-op. Replace the "under investigation (#367 follow-up)" comment with this verdict.
- **Verification asymmetry:** all 39 high/medium gaps adversarially verified (39 confirmed / 0 refuted / 0 reclassified — verifiers engaged substantively; several narrowed scope, e.g. the CA-inhibit refuter established that the inhibited track's *own* indications do clear, leaving only the partner-track staleness). The 31 lows are unverified finder claims.
- **Duplication is intentional in the tables** (per-dimension traceability): H1 appears under D2/D5a/D5b, H2 under D5b/D8/D9, H3 under D3/D10, H4 under D4/D6/D8, the voice form under D2/D3. The root-cause section deduplicates.
- **One inter-agent inconsistency, resolved:** D9's notes speculated the MultiFunc M/Y handlers might be absent; D5b (and a hand check) confirm they exist (`CrcMultiFuncY:1008`/`CrcMultiFuncM:1039`) — D9's PRA finding is about the converter unit, which stands.
- **Not defects:** `IsAdsb=true`, `IsSpcAcknowledged=false`, the parked-datablock constant block, `GroundTrack` as true track, and the live-METAR SSA weather are all confirmed correct or structural (matching the Rust live-wire reference where cited).
- **CRC-side bugs found in passing** (nothing YAAT can do): the MultiFunc `L##` keyboard-form regex is malformed and dead; the `L(1-9)(1-9) FLID` form therefore never reaches any server.

*Audit artifacts (session-local): per-agent findings + verdicts in workflow run `wf_6bae5bad-f29` (`journal.jsonl`).*
