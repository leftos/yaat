# Issue #319 — Recognize military training routes as airways

**Status**: in progress, paused. Branch `feat/military-training-routes` in **both** yaat and yaat-server.
All builds and tests green at the pause point (7,931 sim + 352 client UI + 1,553 server).

A fresh agent should be able to resume from this file alone. Read it top to bottom before touching code.

---

## What the issue asked for

[leftos/yaat#319](https://github.com/leftos/yaat/issues/319), labels `enhancement` / `scenarios` /
`center-cmds`. The issue body is one link: the DoD **AP/1B** publication (*Area Planning — Military
Training Routes, North and South America*, NGA, 8-week AIRAC cycle,
`https://www.daip.jcs.mil/pdf/ap1b.pdf`).

**Scope decisions the maintainer made** (do not re-litigate):

| Question | Decision |
|---|---|
| Data source | AP/1B PDF as source of truth + FAA AIS ArcGIS `MTRSegment` as an automated cross-check |
| Behaviour | Full MTR semantics — expansion, `JAWY`, altitude blocks, route width, §9-2-6 phraseology |
| Point naming | Registered in the fix namespace, excluded from autocomplete |
| Aerial refueling tracks | **In scope** (AP/1B chapter 5) |
| AGL floors with no terrain model | Ship a proxy; surface the resolved MSL pair to the instructor |
| Route width | Radar overlay only — no containment physics |
| `CROSS (fix) AT OR LATER THAN (time)` | **Deferred to a separate issue** — file it |
| VR/SR clearances | Warn but allow (see "aviation review" below) |
| Low-level profile | Fly a target inside the block |
| MARSA | Void on amendment, pilot objects — do not reject the keystroke |

---

## What is done

### Data pipeline — `tools/build-mtr-data.py`

Parses AP/1B into `src/Yaat.Sim/Data/MilitaryRoutes/ap1b-mtr.json.br` (Brotli, ~200 KB) plus an
`ap1b-mtr.meta` provenance sidecar. **648 routes** (213 IR / 304 VR / 131 SR), **7,039 points**.

Run it with:

```bash
curl -sk -o .tmp/ap1b.pdf https://www.daip.jcs.mil/pdf/ap1b.pdf   # -k: DoD chain fails default verify
uv run tools/build-mtr-data.py --input .tmp/ap1b.pdf --report .tmp/mtr-report.json
```

**Four extraction facts that were expensive to learn — do not regress them:**

1. **Header detection requires a single-word row.** Matching the designator pattern anywhere in the
   top band picks up route cross-references in wrapped SOP prose on continuation pages ("IR-022 is
   normally flown on…") and fabricates 47 phantom empty routes.
2. **Column bands are cached per page *parity*.** Facing pages are mirrored (`Pt` at x≈105 on one,
   x≈155 on the other) and continuation pages repeat neither the header nor the originating page's
   margins. Getting this right recovered **525 points across the 42 longest routes** — IR-178 went
   from 15 points to 53.
3. **The Lat/Long band is a tight window** (header x + 2 … + 26), not open to the right edge. SOP
   entries cite tower and hazard coordinates inline in prose; an open band turns them into points.
4. **A point row must carry its own latitude.** That alone rejects the SOP prose whose short words
   ("NM", "SA") drift into the Pt column — 82 phantom points.

**Two heuristics that are wrong — recorded so they aren't retried:**

- Ending the table on an SOP list marker `(a)` / `(1)`. Markers appear **legitimately inside** SR
  tables — SR-221 carries `(a) SUCKCHON DROP ZONE` as an annotation on point J — and using them
  costs 122 genuine points across SR-218…SR-246.
- Treating `NOTE:` / `CAUTION:` as table enders. VR-1001 carries a mid-table `NOTE:` in the Altitude
  column; ending there truncates the route at K and swallows K's longitude.

**Correctness checks built into the tool:**

- **FRD oracle** (the important one): where a point publishes a Fac/Rad/Dist, the great-circle
  distance from that navaid to the printed lat/long must equal the published distance. Distance is
  declination-independent, so it needs no WMM model, and a row mis-association throws a point tens to
  hundreds of NM off. **Currently 99.93% (5,612/5,616), median error 0.25 NM.** The four
  disagreements are AP/1B errata — VR-1140 point A genuinely prints `BFV 094/219`, an implausible
  219 DME. Gate is 95%.
- **FAA cross-check** against the AIS `MTRSegment` layer, measured FAA-vertex→nearest-parsed-point.
  Deliberately **not** fatal per route: the layer diverges from AP/1B in both directions because they
  publish on different cycles (VR-1351 is a 14-point route in AP/1B but 2 vertices in the layer;
  IR-177 ends at Q in AP/1B while the layer carries a longer earlier version; IR-983 is in the layer
  and absent from AP/1B 2607). The gate is on the *fraction* of diverging routes, not any one route.

### Recognition and expansion (commit `9a1e6d03`)

- `MilitaryRouteDatabase` mirrors `MvaDatabase` (lazy `Default`, 3-tier fixture search, Brotli,
  silent-empty) **plus** a `ScopedOverride` IDisposable that `MvaDatabase` lacks — this database is
  read from inside the `NavigationDatabase` constructor, so a leaked override poisons every nav DB
  built afterwards.
- Routes are **shadow-registered into `_airways`**, which is what makes `JAWY IR149` and the radar
  context menu's `GetFiledAirways` work with zero changes to either. Expansion does *not* ride that
  path — `ExpandAirwaySegment` walks bidirectionally and matches anchors by name, both wrong here —
  and that method now refuses to reverse along a military route.
- Points live in a **separate `_militaryRoutePoints` dictionary**, consulted by `GetFixPosition` only
  after `_navDb` misses. That keeps ~7,000 synthetic names out of `AllFixNames` *and* `GetFixTuples`
  with no filtering, so they never reach autocomplete, the DIST suggester, the scope's fix overlay,
  or FRD anchoring — and a real fix always wins a clash by construction.
- Names are `{designator}{label}` (`IR149A`). AP/1B labels always start with a letter, so a minted
  name never has three trailing digits and `FrdResolver.ParseFrd` can never read it as an FRD anchor.
  A test pins that property.
- `MilitaryRouteExpander` anchors on the bracketing **FRDs** — AP/1B ch.1 §IV.B.1 files
  `SAT263043 IR149 LRD040028`, and those two are exactly IR-149's published entry/exit FRDs. Snap
  tolerance 15 NM, forward-only, with one-sided and no-anchor fallbacks.
- **Two pre-existing bugs fixed**: `ArrivalRouteResolver` and `RouteChainer` used a bare
  `GetFixPosition`, silently dropping *every* FRD in a filed route, military or not. `RouteChainer`
  dropped them with no log line at all.

### Sim semantics (commit `915caf8b`)

`AircraftMilitaryRoute` satellite + snapshot (**schema v17**), `MilitaryRoutePhase` with all four DTO
wiring points, altitude blocks via `ControlTargets.AltitudeFloor`/`Ceiling`, and four commands:

| Verb | Aliases | Args |
|---|---|---|
| `CMTR` | `CIR` | `<route>` / `<route> <alt>` / `<route> B<alt>` |
| `MTRA` | `MRA` | — |
| `XMTR` | `EMTR` | `<dest> [VIA <route>]` |
| `SAYEXIT` | `SAYXF` | — |

Plus `SpellMilitaryRoute` group form (§2-5-1.f), squawk 4000 on VR/SR, the 91.117 waiver, and MARSA.

**Why a phase and not a `CommandBlock`**: `CommandBlock.ApplyAction` is not restored after a
snapshot, and `FlightCommandHandler`'s climb/descend/force-altitude paths all null both altitude
bounds — so the block must be re-asserted from durable state at each segment boundary.

### Wire + UX (commits `6424fecb`, server `a658d82`)

Three DTO fields both repos (`MilitaryRoute`, `MilitaryRouteAltitudeText`, `MilitaryRouteMarsa`),
fingerprinted in `AircraftChangeTracker`. The altitude is **pre-rendered text carrying the resolved
MSL pair**, not the published notation — see the AGL note below.

Also closed a pre-existing gap: `ArgumentSuggester` had no `airway` type hint, so `JAWY` offered no
completions for *any* airway.

### Aviation review (uncommitted at pause — see "resume here")

`aviation-sim-expert` reviewed the whole feature against local 7110.65/AIM. **Five must-fixes, all
applied:**

1. **At-or-below never applied the published floor.** `ReArmBlock` gated on `RouteAltitudes`, making
   `ApplyBlock`'s at-or-below branch unreachable — the aircraft could descend below the segment's
   minimum IFR altitude unopposed.
2. **`XMTR` bypassed `OnEnd`** by nulling `aircraft.Phases`, so the VR beacon code was never restored
   and the strip kept a stale block. Now calls `PhaseList.Clear(ctx)`.
3. **91.117(d) does reach 91.117(c).** "This section" is all of 91.117. The two waivers have opposite
   shapes: the DoD's is unlimited but lifts (a) only; 91.117(d) reaches every paragraph but only up
   to minimum safe speed. `RegulatorySpeedLimit` now takes them separately.
4. **AGL floors fell back to sea level** when no airport was within 100 NM — a "05 AGL" floor armed
   at 500 ft MSL is thousands of feet underground over the Great Basin and the aircraft descends
   toward it. Now `MvaDatabase` floor minus the 1,000 ft obstacle buffer is the primary terrain proxy
   (MVA errs high, airports err low), and an unresolvable AGL bound is left **unenforced**.
5. **`CMTR` on a VR/SR isn't a real clearance** — §9-2-6 is IFR-only. Now warns and drops the
   "cleared into" wording while still placing the aircraft on the route as traffic.

Plus: MARSA no longer defaults true on AR tracks (§9-2-13 NOTE 3 makes it a declaration on frequency);
MARSA amendments are accepted and void MARSA per §9-2-13.e instead of being rejected; the aircraft now
flies a target *inside* the block; the exit estimate is a UTC clock time per §2-4-17.c.1.

---

## Resume here

### 1. Immediate: the AR-track parser (chapter 5)

**The hard part is solved and committed** — `page_words()` in `tools/build-mtr-data.py` rebuilds
words from rotated chars. Chapter 5 tables are printed **sideways**: 203 of 211 words on a page carry
`upright=False` with a 90° text matrix, so pdfplumber (which groups by page x) assembles them
backwards — "thgilF" for "Flight". `page_words` transposes them, after which chapter 5 reads
structurally like chapters 2–4. Verified output:

```
NUMBER ARIP ARCP CHECK POINTS EXIT CR PLAN ALTITUDES UNIT ARTCC
AR5H
 N39°20.00'  N39°23.00'  N39°23.00' ENI VORTAC a. 283.900 FL250/FL330 60 OSS/OSO
(East) W131°00.00' W128°49.00' W126°11.00' 279/38  b. 342.550 Travis AFB, CA ARCP-306.2E
```

Still to do: an `extract_ar_tracks()` pass emitting `ap1b-ar.json.br` (**239 designators**: `AR1`,
`AR3H`, `AR4A`, `AR717B`…). Structure — cells are 3–4 line stacks (facility / radial-distance / lat /
lon); `(North)`/`(South)`/`(East)`/`(West)` sub-rows are separate directional variants; the
Navigation Check Points column overflows below its own row. Map to `MilitaryRouteType.Ar` with a
point role per ARIP / ARCP / checkpoint / EXIT. `MilitaryRouteDatabase` already globs `*.json.br`, so
a second fixture merges with no loader change.

**AR phraseology is different and must not reuse `CMTR`.** §9-2-13 has its own form, and it inverts
the designator: `CLEARED TO CONDUCT REFUELING ALONG (number) TRACK` — `AR-312` is "along three twelve
track", not "a-r three twelve". Altitude clause is `MAINTAIN BLOCK (altitude) THROUGH (altitude)`
(§4-5-7.g), which fits the block model better than §9-2-6.a's forms.

### 2. Pilot readbacks — wording is already settled

`PhraseologyRules.cs` has no `CMTR`/`MTRA`/`XMTR`/`SAYEXIT` entries, so none of the four produces a
readback and the STT pipeline can't recognise the spoken controller form. `SpellMilitaryRoute` exists
but is referenced only from tests. The aviation review supplied exact wording:

| Command | Terminal | TTS body |
|---|---|---|
| `CMTR IR149` | `cleared into IR149, maintain IR149 altitudes` | `cleared into i-r one forty nine, maintain i-r one forty nine altitudes` |
| `CMTR IR149 50` | `cleared into IR149, maintain 5000` | `cleared into i-r one forty nine, maintain five thousand` |
| `CMTR IR149 B50` | `cleared into IR149, maintain at or below 5000` | `cleared into i-r one forty nine, maintain at or below five thousand` |
| `MTRA` | `maintain IR149 altitudes` | `maintain i-r one forty nine altitudes` |
| `XMTR` | `cleared to KTCM from IR149 via V495 SEA, maintain 24000` | `cleared to tacoma narrows from i-r one forty nine via victor four ninety five, seattle, maintain two four thousand` |
| `SAYEXIT` | `estimating IR149 exit point B at 1443, request 24000 after exit` | `estimating i-r one forty nine exit point bravo at one four four three zulu, requesting two four thousand after exit` |

Implementation notes from the review:
- `MTRA` and `XMTR` carry no designator in the command record — it comes from
  `aircraft.MilitaryRoute.Designator`. A `PhraseologyRule` can't reach aircraft state, so both need a
  builder in `PilotResponder.VerbalizeForReadback` following the `BuildExtendPatternClause` pattern.
- `XMTR` needs a **route-string speller** (`V495 SEA` → "victor four ninety five, seattle"). §2-5-1.a
  for the airway token, existing `SpellFix` for the fix. Recommend `SpellRouteString` in
  `PhraseologyVerbalizer` — reusable by any future reroute command.
- Keep `maintain at or below` identical to the existing `ClimbMaintainCommand { AtOrBelow }` rendering
  at `PhraseologyVerbalizer.cs:139-143`.
- **SR has no authority.** §2-5-1.f covers only I-R and V-R. Current output "s-r nine hundred" is an
  extension by analogy — document it as such, not as a citation.

### 3. Remaining should-fixes from the review

- **Route speed.** The waiver lifts the cap but nothing supplies a speed, so the aircraft transits at
  whatever it arrived with. >250 kt is the defining characteristic of the program (P/CG, AIM 3-5-2.c).
  Suggest ~400 KIAS for a tactical jet on establishment, 250 for non-jet, overridable by `SPD`.
  AP/1B publishes no per-route speed, so this is a category default, not data.
- **Entry should default to a published entry point.** `FindJoinIndex` joins at the nearest non-behind
  point; §9-2-6 hangs its structure on the published entry fix. Default to the nearest primary/
  alternate entry ahead, allow mid-route join only as a forced form. Also `IsBehind` uses
  `TrueHeading` — ground track is the better proxy in a turn.
- **`XMTR` needs an altitude argument.** §9-2-6.b's phraseology is two lines ending "MAINTAIN
  (altitude)"; today an exit clearance leaves the aircraft with no assigned altitude at all.
- **`DCT` to a route point behind the aircraft** flies it backwards down a one-way route. Either
  exclude military points from `DCT` resolution or reject a lower-index point while the phase is
  active.
- **Route width overlay** (the maintainer chose display-only). `MilitaryRoute.Widths` is parsed and on
  the wire; the radar overlay is unwritten. The review argues §9-2-6.d is the one fact in §9-2-6 the
  controller must act on continuously, so this is higher value than it looks.
- **Block notation on the strip.** `AltitudeText` renders `5,000–6,000`; controller convention is
  `050B060` (§13-1-1, §4-5-2). Also avoids a non-ASCII en dash in strip text.
- Nice-to-have: `CRUISE (altitude)` (§9-2-6.a, distinct training case); warn when a `CMTR` altitude is
  below the segment floor; instructor warning when a second aircraft is cleared into an occupied route
  (§9-2-6.a separation).

### 4. Docs (Phase 7, not started)

- **New** `docs/military-training-routes.md`, modelled on `docs/minimum-vectoring-altitude.md`.
- `docs/architecture.md` — Task Index row, subsystem table row, `Data/` tree entries, and a
  `build-mtr-data.py` entry under Root Scripts.
- `docs/navigation-database.md` — owns route expansion. Add `MilitaryRouteDatabase`, the MTR
  expansion subsection, the hidden-synthetic-fix concept, and footguns. **Its line citations have
  drifted badly** (cites `LoadCustomFixes` at :1542, actually :2030) — don't trust its numbers.
- `docs/phases.md`, `docs/aircraft-data-model.md` ("thirteen satellites" appears **twice** — it is
  now fourteen), `docs/command-handlers.md`, `docs/pilot-phraseology.md`.
- `COMMANDS.md` + `docs/command-cheatsheet.json` → `node tools/build-cheatsheet.mjs` (prek and CI
  enforce the HTML stays in sync).
- `USER_GUIDE.md`, `CHANGELOG.md`.
- `docs/plans/phraseology-coverage-backlog.md` — drop "Military Training Routes (IR/VR)" from the
  §2-5 `OutOfScope` clause (:2076), move the §9-2-6 clearances from `MissingCanonical` to `Covered`
  (:2270ff), and **leave** "CROSS (fix) AT OR LATER THAN (time)" in `MissingCanonical`.
- File the deferred `CROSS … AT OR LATER THAN` issue.
- Delete this file when the issue closes.

---

## Gotchas for whoever resumes

- **`.tmp/ap1b.pdf` is not committed** (7.6 MB, gitignored). Re-download before running the build
  tool. The DoD host needs `curl -k`.
- **`.tmp/navaids.json`** caches the FAA navaid layer for the FRD oracle; it re-fetches if absent.
- The fixture is **regenerated**, not hand-edited. If a route looks wrong, fix the parser.
- `TestVnasData.EnsureInitialized()` in every test class constructor; military-route tests use
  `[Collection("NavDbMutator")]`.
- `PhaseList.Clear` only calls `OnEnd` on an **Active** phase — a test that drives `OnTick` directly
  must set `Status = PhaseStatus.Active` first or teardown silently doesn't run.
- IR-149 point A publishes "As assigned to", so the **first segment legitimately arms no block**. A
  test asserting on bounds must sequence past it.
- `prek` must run from Git Bash, not PowerShell.
