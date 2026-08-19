# Navigation Database & Route Expansion

> Read this before touching `NavigationDatabase`, `RouteExpander`, `FrdResolver`, `CustomFixLoader`, or `ApproachGateDatabase` —
> or before fixing any "flight plan turns back through every fix", "SID won't resolve", "airport code not recognized", or
> "custom fix missing" bug. This doc owns the singleton lifecycle, the FAA↔ICAO normalization that repeats across ~6 lookups,
> procedure-version resolution, the route-expansion token loop, **the RV-SID emit-all-vs-emit-nothing footgun**, FRD/custom-fix
> resolution, and the approach-shorthand / approach-gate machinery.

`NavigationDatabase` is the static singleton that answers "where is fix X", "what runways does this airport have", "what fixes
does this SID/STAR/airway contain", and "what's the published approach for this shorthand". It is loaded once at startup from two
required data sources — vNAS protobuf NavData (airports, fixes, airways, SID/STAR bodies + transitions) and FAA CIFP
(per-airport SIDs/STARs/approaches, plus VOR/DME/NDB navaids) — and read everywhere in the sim. It does **not** own ground
layouts (see [ground/README.md](ground/README.md)) or anything physics-related.

The companion `RouteExpander` turns a route string like `"NIMI5 OAK V6 SAC"` into an ordered list of fix names. The two are
tightly coupled: `RouteExpander` reads SID/STAR/airway data straight out of a `NavigationDatabase`.

## Where it lives

| File | Role |
|---|---|
| `src/Yaat.Sim/Data/NavigationDatabase.cs` | The singleton: indexes, lookups, normalization, procedure-version resolution, approach shorthand. |
| `src/Yaat.Sim/Data/RouteExpander.cs` | Stateless route-token expander (SID → STAR → dot-airway → bare-airway → plain fix). |
| `src/Yaat.Sim/Data/FrdResolver.cs` | Fix-Radial-Distance string ↔ lat/lon. |
| `src/Yaat.Sim/Data/CustomFixLoader.cs` / `CustomFixDefinition.cs` | Loads `Data/ARTCCs/{ARTCC}/CustomFixes/*.json` into custom fixes. |
| `src/Yaat.Sim/Data/CustomProcedureLoader.cs` | Indexes ARTCC-supplied CIFP fragments (`Data/ARTCCs/{ARTCC}/Procedures/*.cifp`). |
| `src/Yaat.Sim/Data/ProcedureSource.cs` | Which non-current source supplied a procedure (prior cycle vs ARTCC fragment). |
| `tools/stash-procedure.py` | Finds a procedure in any reachable AIRAC cycle and writes the fragment. |
| `src/Yaat.Sim/Data/ApproachGateDatabase.cs` | Precomputed min-intercept distances per (airport, runway), FAA 7110.65 §5-9-1. |
| `src/Yaat.Sim/Testing/TestVnasData.cs` | Test entry point — populates the singleton with real data (never synthetic). |
| `tests/Yaat.Sim.Tests/NavDbMutatorCollection.cs` | xUnit collection that serializes tests which swap in a synthetic DB. |

CIFP column-offset details belong to the CIFP-reference section of `CLAUDE.md` and `reference/cifp/`, not here.

## Lifecycle & the singleton

`NavigationDatabase` is a sealed class with a mutable static instance. Three production-facing entry points and a test helper:

| Member | What it does |
|---|---|
| `Initialize(navData, cifpFilePath, artccsBaseDir?, supplementaryCifpFilePath?)` | Constructs the instance and stores it as the **process-wide default** (`_defaultInstance`). |
| `SetInstance(db)` | Replaces the process-wide default directly (used by tests and re-init). |
| `ScopedOverride(db)` → `IDisposable` | Sets a **thread-local (`AsyncLocal`) override** visible only to the current async context; disposing clears it back to the default. |
| `ForTesting(...)` | Builds a hand-seeded instance (no NavData/CIFP file load) for unit tests. Private parameterless ctor, public static factory. |

Resolution order, in `NavigationDatabase.cs:62` / `:72`:

- `Instance` → `_scopedInstance.Value ?? _defaultInstance ?? throw`. **Throws** `InvalidOperationException` if neither is set.
- `InstanceOrNull` → `_scopedInstance.Value ?? _defaultInstance`. Returns `null` instead of throwing — use it when the lookup is
  best-effort and the caller has a fallback.

Picking the wrong accessor either crashes a best-effort lookup or silently no-ops a required one. `Instance` is the right call
when the sim genuinely cannot proceed without nav data; `InstanceOrNull` is for UI/speech paths that degrade gracefully.

**Production wiring** is in yaat-server `YaatHost.cs:177` — after `VnasDataService.InitializeAsync()` and
`CifpDataService.InitializeAsync()` download/cache the data:

```
NavigationDatabase.Initialize(vnasData.NavData, cifpService.CifpFilePath, supplementaryCifpFilePaths: cifpService.SupplementaryCifpFilePaths);
var cifpData = CifpParser.Parse(cifpService.CifpFilePath);
ApproachGateDatabase.Initialize(cifpData);
```

Note the order: `ApproachGateDatabase.Initialize` reads `NavigationDatabase.Instance` internally (`ApproachGateDatabase.cs:28`), so
the nav DB must be initialized first.

### What the constructor builds

`NavigationDatabase(navData, cifpFilePath, artccsBaseDir?, supplementaryCifpFilePath?)` (`NavigationDatabase.cs:114`) runs, in
order: `LoadCifpRunwayThresholdElevations` (see below — it must precede `BuildIndex`, which stamps them onto runway ends),
`BuildIndex` (airports/fixes/runways/airways, eager), `BuildProcedureIndex` (SID/STAR bodies + transitions, eager),
`LoadCifpNavaids` (supplements `_navDb` with VOR/DME/NDB from CIFP), then the per-ARTCC user-data loads (`LoadCustomFixes`,
`LoadFixPronunciations`, initial-contact transfers, wake directives, taxi routes, avoid-taxiways), then `AllFixNames`. CIFP
**procedures** (SIDs/STARs/approaches) are *not* loaded here — they parse lazily per airport on first access (see below).

### Runway end elevations come from the CIFP

The vNAS NavData carries **one elevation per airport**, so `RunwayInfo.Elevation1Ft`/`Elevation2Ft` used to be field elevation
on both ends. A glidepath is referenced to the **landing threshold** of the end being flown to (AIM 5-4-5.b.3: the TCH is "the
height of the glide slope above threshold"), so on a sloped runway that put the whole final approach off by the runway's rise —
140 ft at KASE, whose RWY 15 threshold is 7,680 ft and RWY 33 is 7,820 ft.

`CifpParser.ParseRunwayThresholdElevations` reads the CIFP airport-runway records (section `P`, subsection `G`) and
`ThresholdElevationFt` stamps each end during `BuildIndex`, keyed by ICAO id with a `K`+FAA-id fallback and falling back to
airport elevation whenever the CIFP has nothing (no CIFP loaded, non-US airport, omitted runway). Nothing downstream changed:
`ctx.FieldElevation` is already `runway?.ElevationFt`, so the glidepath, flare and touchdown altitude all follow.

Note this is the *threshold* elevation, not TDZE (the highest point in the first 3,000 ft). TDZE is the reference for
approach minimums; the glidepath's datum is the threshold, which is what the CIFP field gives.

`artccsBaseDir` defaults to `{AppContext.BaseDirectory}/Data/ARTCCs`; pass an empty string to skip per-ARTCC loading entirely
(many tests do). `supplementaryCifpFilePath` is only retained if the file exists and differs from the primary CIFP path
(`NavigationDatabase.cs:117`); otherwise it is dropped to `null` and the supplementary fallback is disabled.

## The xUnit singleton-race hazard

The singleton is mutable static, and xUnit runs test classes in parallel by default. This produces a documented flake class.

`TestVnasData.EnsureInitialized()` (`TestVnasData.cs:250`) is the safe entry point for any test that needs real nav data. It
populates six static singletons once per process (under a lock) — `AircraftCategorization`, `WakeTurbulenceData`,
`FaaAircraftDatabase`, `AircraftProfileDatabase`, `AircraftSiblingMap`, and the `AircraftPerformance` profile-correction adapter —
and then **always re-sets** `NavigationDatabase.SetInstance(navDb)` on every call (the re-set is outside the one-time lock block). The always-re-set is deliberate: other test
classes (parser tests) swap in synthetic `ForTesting()` databases via `SetInstance`, so a later `EnsureInitialized()` restores
the real one. `EnsureInitialized()` itself is idempotent and safe for parallel execution.

`TestVnasData.NavigationDb` (`TestVnasData.cs:45`) uses **double-check locking** and only publishes the instance after CIFP has
also resolved. The guard exists so a concurrent test class can't observe a partially-initialized DB that has NavData but no CIFP
path (which would make every procedure lookup silently return empty).

Two mutation patterns, two scopes:

- **`ScopedOverride(db)`** — the preferred test pattern. `using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);`
  installs a thread-local override that disposes back to the default. It does not leak across test classes, so it does **not**
  need the serializing collection.
- **`ForTesting()` + `SetInstance()`** — mutates the process-wide default and **leaks into parallel test classes**. Any test that
  does this must join the `[Collection("NavDbMutator")]` collection (`NavDbMutatorCollection.cs`), which serializes those tests so
  they don't stomp each other or a concurrent reader.

Symptom of getting this wrong: a test that passes in isolation but flakes in the full suite, typically a value mismatch where one
call returned the default fallback and the next returned loaded data. If you see that, confirm the racing class calls
`EnsureInitialized()` in its constructor (or uses `ScopedOverride`) before any test-method body runs. See `tests/Yaat.Sim.Tests/ModuleInit.cs`
for the assembly-load warm-up and [e2e-tdd-issue-debugging.md](e2e-tdd-issue-debugging.md) for the broader replay-test harness.

## Identifier normalization: FAA ↔ ICAO

CONUS airports have both an FAA id (`OAK`) and an ICAO id (`KOAK`); scenario files and vNAS data use them interchangeably. Two
static helpers normalize:

- `NormalizeAirport(code)` (`NavigationDatabase.cs:2059`) — uppercases, trims, and strips a leading `K` **only when the length is
  exactly 4** (`KOAK` → `OAK`; a 3-letter `OAK` is unchanged). Note this means a non-CONUS 4-letter ICAO that happens to start
  with K is also stripped — acceptable for the CONUS-focused dataset.
- `AirportIdsMatch(a, b)` (`:2101`) — true when both normalize equal; empty/null never matches.
- `TryResolveAirport(input, out canonicalId)` (`:2077`) — looks the trimmed-uppercased input up in `_airportCanonical`, which maps
  every recognized FAA and ICAO id to the airport's canonical form (ICAO when published, FAA fallback otherwise). Returns `false`
  for unknown airports — callers should reject rather than store an unrecognized code.
- `TryResolveFaaId(input, out faaId)` (`:2111`) — the inverse of the above: resolves any recognized form to the **published FAA
  id** via `_airportFaaIds`. Unlike the `NormalizeAirport` K-strip this is correct for non-CONUS fields (`PANC` → `ANC`,
  `PHNL` → `HNL`, `TJSJ` → `SJU`). Airports with no published FAA id (most foreign fields) are deliberately **absent** from the
  map, so the call returns `false` and callers fall back to the identifier as filed — matching CRC's display behavior. Use this
  wherever an FAA id is being *displayed*, e.g. `AutoScratchpadResolver` filling the STARS scratchpad slot.

  > `NavigationDatabase.ForTesting` seeds `_airportFaaIds` from the recognized-airport keys via `NormalizeAirport`. Tests built
  > on `ForTesting` therefore get the K-strip approximation, not published FAA ids; tests that need the real mapping must use
  > `TestVnasData`.

Beyond the canonical map, several lookups **reimplement** a 3↔4-letter K-prefix fallback inline: `GetAirportElevation`
(`:470`), `GetAirportName` (`:498`), `HasRunwayAtLeast` (`:596`). The pattern is: try the key as given, then for a
4-letter K-prefixed code try the 3-letter form, then for a 3-letter code try `"K"+code`. A new airport-keyed lookup that forgets
this fallback will fail for whichever of `OAK`/`KOAK` the caller didn't pass. Note `GetRunway` (`:642`) does **not** carry this
fallback — it does a single direct lookup and returns `null` on a key miss, which is why `ApproachGateDatabase.Initialize` calls
`GetRunway(airport, …) ?? GetRunway($"K{airport}", …)` at its call site.

The CIFP loaders go the other way: `LoadSids`/`LoadStars`/`LoadApproaches` (`:1733`+) take a normalized (FAA-form) code and
prepend `K` when it is ≤3 chars, because the CIFP file is keyed by ICAO id.

## Procedure-version resolution

Published procedures carry a version digit (`BDEGA4`, `CNDEL5`) that increments each AIRAC cycle. A scenario filed against an
older cycle must still resolve. `StripTrailingDigits(s)` (`NavigationDatabase.cs:1938`) trims trailing digits down to the base
name **but preserves at least 2 characters** (`end > 2` guard); it returns the input unchanged if there are no trailing digits.

- `ResolveSidId(rawId)` / `ResolveStarId(rawId)` (`:729` / `:773`) — return the input if it's an exact key in the SID/STAR-body
  index; else strip the version digit and scan for a key whose base name matches (`CNDEL5` → `CNDEL6`); else `null`. **If the raw
  id has no trailing digits and isn't an exact key, they return `null` immediately** (the `baseName == rawId` early-out) — they do
  not fuzzy-match digit-less identifiers.
- `FindSidInList` (`:942`) / the inline STAR equivalent in `GetStar` (`:965`) apply the same exact-then-base-name fallback against
  the parsed **CIFP** procedure list.

An exact-ID lookup that bypasses these resolvers will silently miss the current cycle's procedure whenever the scenario filed an
older version.

**Command-only version-less escape valve.** Do not extend `ResolveSidId`/`ResolveStarId` to resolve a bare, digit-less base name
(`TEJAS`) globally — STARs are named after their first fix, so a global version-less resolver would mis-classify a bare fix token
as a STAR and expand the whole procedure inside `RouteExpander`/`ScenarioLoader`/`ScenarioValidator`. For controller/pilot commands
that should accept a short STAR name (e.g. `JARR`), resolve explicitly via `NavigationDatabase.ResolveCommandStarId(airport, rawId)`
(`:1104`; CIFP first, then NavData bodies, returns the input unchanged on no match) before calling the strict lookups — the `JARR`
handler (`NavigationCommandHandler`) and `AircraftGenerator` do this. `DVIA` self-resolve scans the *filed route* (already-versioned
names) with the strict `ResolveStarId`, so it doesn't need the loose form.

### Procedure resolution has three tiers

A procedure's coded legs can be absent from the *current* FAA CIFP while a scenario still files it — either the procedure was
retired, or (like the NIMITZ SID at KOAK) it is still charted but simply missing from the CIFP dataset. `GetSid` / `GetStar` /
`GetApproach` search, in order:

1. **Current FAA cycle** — the normal case; `source` out-param is `null`, no advisory.
2. **ARTCC-supplied fragment** (`Data/ARTCCs/{ARTCC}/Procedures/*.cifp`) — see below.
3. **Supplementary chain** of cached prior AIRAC cycles — see below.

Tiers 2 and 3 return a `ProcedureSource(Kind, Label)` via the `out` overload, which drives the instructor advisory
(`CommandDispatcher.ProcedureSourceAdvisory`). `Label` is the ARTCC id for `ArtccCustom` and the AIRAC cycle id for `PriorCycle`.

### ARTCC-supplied CIFP fragments

The chain in tier 3 is bounded twice over: by **time** (~12 months of cycles) and by **place** (only cycles this machine has
cached — a fresh server has one). A committed fragment is neither. `CustomProcedureLoader.LoadAll(artccsBaseDir)` indexes
`Data/ARTCCs/{ARTCC}/Procedures/*.cifp`, and `NavigationDatabase.LoadCustomProcedures` (called from the constructor, after
`BuildProcedureIndex`) parses each **eagerly** into `_customProcedures`, keyed by normalized airport.

A fragment is verbatim ARINC 424 records copied out of a published cycle — `CifpParser` reads it exactly as it reads a full
cycle file, so there is no second parser and no schema to drift. Generate one with `tools/stash-procedure.py`; never by hand.
Full authoring guide: [`src/Yaat.Sim/Data/ARTCCs/README.md`](../src/Yaat.Sim/Data/ARTCCs/README.md#procedures).

Two behaviors worth knowing:

- **Shadow warning.** Loading warns when the current cycle already publishes a fragment's procedure — the fragment can never win
  (tier 1 precedes it) and should be deleted.
- **NavData-side registration.** vNAS NavData and CIFP are *separate* stores: NavData holds the flat SID/STAR fix-name bodies that
  drive `ResolveSidId` → `RouteExpander` → route-string parsing and autocomplete, while CIFP holds the typed flyable legs. A
  fragment supplies the CIFP side; for a procedure vNAS does **not** carry, `RegisterCustomSidOnNavDataSide` /
  `RegisterCustomStarOnNavDataSide` also derive a body (`LateralFixes` of the common legs, fix-less heading legs dropped, adjacent
  duplicates collapsed) plus its real CIFP enroute transitions. This is skipped entirely when `ResolveSidId`/`ResolveStarId`
  already resolves — **live vNAS data is never shadowed** — and it never synthesizes vNAS-style `[OAK, X]` routing hints.

`ApproachGateDatabase.Initialize(primary, additional)` takes the fragment parse results as its second argument
(`NavigationDatabase.CustomProcedureFilePaths` → `CifpParser.Parse`), so a pinned approach's FAF still yields a real gate distance
instead of the 7.0 nm default.

### Supplementary CIFP chain for retired procedures

`GetSid` / `GetStar` / `GetApproach` fall through to the **supplementary chain**
(`_supplementaryCifpFilePaths`, newest→oldest cached prior cycles) and resolve from the most recent cycle that still carries the
procedure, logging a warning and returning the source cycle id via the `out ProcedureSource? source` overload (which drives the
instructor advisory). The chain is the cached prior AIRAC cycles within a recency cap (`CifpPathResolver.MaxSupplementaryLookbackCycles`
= ~1 year), assembled by `CifpDataService` into `SupplementaryCifpFilePaths` — the app caches each cycle it downloads, so this
auto-accumulates with no shipped data. Resolving newest-prior-only (the old single-path behavior) missed procedures retired more than
one cycle ago even when older cached cycles still had them. Production wires `cifpService.SupplementaryCifpFilePaths` (`YaatHost.cs`);
a bare `new NavigationDatabase(navData, cifp, artccsBaseDir: "")` in a test passes an empty chain, so a retired SID returns `null`
there. (`IssueN513sjNimiRvSidCifpMissTests` exercises the empty-chain degradation; `IssueN513sjNimi6PriorCycleChainTests`
exercises the chain walk recovering NIMI5's published 315° heading; `CustomProcedureResolutionTests` covers the fragment tier.)

The chain is **skipped when the current cycle lists zero procedures of that kind at the airport** (and a current CIFP is actually
loaded — with none, the chain is the sole source). It exists to resolve version drift in procedures the airport still publishes;
an airport with no SIDs at all (HDN) never had a version to drift from, and each chain miss is a full linear scan of every cached
prior-cycle file — a burst that blew the server tick budget when route tokens were probed at procedure-less fields.
(`SupplementaryChainSkipTests` pins the skip per kind.)

> Tests that assert on *unresolvable*-procedure behavior must pass `artccsBaseDir: ""`. The shipped ZOA NIMITZ fragment resolves
> `NIMI5`/`NIMI6` at KOAK in production, so a test that constructs a `NavigationDatabase` with the default ARTCC dir will no longer
> see the degradation path. Both NIMI test classes now pass `""` explicitly for this reason.

### Lazy per-airport CIFP cache

`GetSids`/`GetStars`/`GetApproaches` parse the CIFP file for an airport on first access and cache the result in a
`ConcurrentDictionary` keyed by normalized airport (`NavigationDatabase.cs:959`, `:983`, `:1057`). The first access per airport
pays the parse cost; the cache lives for the process lifetime with no invalidation on AIRAC change mid-run.

## Route expansion (`RouteExpander`)

`RouteExpander.Expand(route, navDb, includeAllTransitionsOnMismatch = true)` (`RouteExpander.cs:31`) splits the route on spaces and
walks tokens left to right. For each token it tries, in order:

1. **SID** — `navDb.ResolveSidId(rawName)` matches → emit the SID body, then resolve the transition (see RV-SID section below).
2. **STAR** — `navDb.ResolveStarId(rawName)` matches → emit body from the join point (`ExpandStar`).
3. **Dot-airway** — `FIX.AIRWAY` (e.g. `PORTE.V25`) → emit the head fix, then the airway segment from that fix to the next
   non-numeric token.
4. **Bare airway** — token is a known airway id → expand from the previously emitted fix to the next non-numeric token.
5. **Plain fix** — emit as-is.

Adjacent duplicates are collapsed (`EmitDeduped`). Numeric tokens (altitude/speed constraints like `050`, `250`) are skipped
(`double.TryParse` guard at `:49` and in `FindNextNonNumericToken`).

`ExpandStar` (`:177`) finds the join point: if the preceding emitted fix appears in the STAR body, emission resumes after it;
otherwise it checks the STAR's transitions for the join fix and emits the remaining transition fixes plus the full body.
`ExpandAirwaySegment` (`:817`) is **bidirectional** — it walks the airway fix list forward or backward depending on whether the
from-fix precedes or follows the to-fix.

### Military routes (AP/1B) — `MilitaryRouteExpander`

An `IR` / `VR` / `SR` / `AR` token in a filed route expands through **`MilitaryRouteExpander`**, not
through `ExpandAirwaySegment`, for two reasons: AP/1B routes are one-way and reversals are prohibited
(chapter 1 §V.B.1), and chapter 1 §IV.B.1 brackets a route with the **FRDs** of its entry and exit
(`SAT263043 IR149 LRD040028`) rather than with names of points on it. `ExpandAirwaySegment` walks
bidirectionally and matches anchors by name, so it now refuses to reverse along a military route.

Routes *are* still shadow-registered into `_airways`, which is what lets `JAWY IR149` and the radar
context menu's `GetFiledAirways` work without either learning a second concept.

Their points live in a separate `_militaryRoutePoints` dictionary that `GetFixPosition` consults only
after `_navDb` misses. That keeps ~8,600 synthetic names (`IR149A`, `AR1ARIP`) out of `AllFixNames`
*and* `GetFixTuples` — so they never reach autocomplete, the DIST suggester, the scope's fix overlay,
or FRD anchoring — and a real fix always wins a name clash by construction. AP/1B labels always start
with a letter, so a minted name never ends in three or six digits and `FrdResolver.ParseFrd` can
never read one as an FRD anchor (see "FRD-named fixes are never FRD anchors" below).

A refueling track publishes **two directions that are not reverses of each other** (they are offset
parallels), so `SelectVariant` scores the bracketing anchor *pair* to decide which was filed. Full
detail in [military-training-routes.md](military-training-routes.md).

Two convenience wrappers on the DB:

- `ExpandRoute(route)` (`:867`) → `Expand(route, this)` with the default `true`. This is the **autocomplete / UI** path
  (`FixSuggester` on the client, `PilotSayBuilder` route readback).
- `ExpandRouteForNavigation(route, departureAirport)` (`:877`) → `Expand(route, this, includeAllTransitionsOnMismatch: false)`,
  then strips leading fixes within **1 nm** of the departure airport (a colocated VOR like `OAK` is paperwork-only and would
  otherwise show up as a turn-back to the field).

## THE RV-SID FOOTGUN

This is the recurring bug this doc exists to prevent. `RouteExpander.Expand` defaults `includeAllTransitionsOnMismatch = true`,
and **`true` is the wrong value for flight-plan / navigation use.**

**The data shape.** Radar-vectors SIDs (e.g. NIMI5, OAK6) have no published lateral path beyond the departure field's colocated
navaid — the pilot flies runway heading and awaits vectors. But vNAS protobuf NavData encodes adapted-route *hints* for these SIDs
as synthetic "transitions": NIMI5 carries `[OAK, CCR]`, `[OAK, PYE]`, `[OAK, SAU]`, … one synthetic transition per common
downstream fix. These are **not** published CIFP transitions; they're routing hints, and each one starts by transiting back
through OAK.

**The failure.** When a route token after the SID doesn't name any transition, `ExpandSid` (`RouteExpander.cs:111`) decides what
to do based on the flag:

- `true` (autocomplete): emit **every** transition's fixes, so the UI can suggest any reachable exit fix.
- `false` (navigation): emit **nothing** beyond the SID body; let the caller's main loop process the real post-SID tokens
  (airway, direct fix).

If a nav caller leaves the default `true`, expanding `NIMI5 OAK V6 SAC` fabricates a turn-back through every synthetic transition
fix (`CCR`, `PYE`, `SAU`, …) — a nonsensical `NavigationRoute` that sends the aircraft turning back through the departure field.
With `false`, the SID body emits, the `[OAK,…]` hints are suppressed on the mismatch, and `V6 SAC` resolves as a real airway leg.

**The caller table** — who passes what, and why:

| Caller | Flag | Context |
|---|---|---|
| `NavigationDatabase.ExpandRoute` | `true` (default) | Autocomplete / UI fix highlighting (`FixSuggester` client). |
| `PilotSayBuilder` route readback | `true` (default) | Speech "say route" — wants every reachable fix named. |
| `NavigationDatabase.ExpandRouteForNavigation` | **`false`** | Building a flying `NavigationRoute`. |
| `DepartureClearanceHandler.BuildFallbackNavTargets` (`:849`) | **`false`** (via `ExpandRouteForNavigation`) | NavData fallback when CIFP can't supply SID legs. |
| `DepartureClearanceHandler.AppendPostSidEnrouteFixes` | **`false`** | Post-SID enroute portion of a departure clearance. |
| `RouteChainer` (`:42`) | **`false`** | Chaining a route onto the active navigation. |
| `ArrivalRouteResolver` (`:29`) | **`false`** | Materializing a scenario aircraft's filed route. |

**Rule: any new caller that produces a flying route must pass `false`.** Only autocomplete/UI/speech paths want `true`.

### The second footgun: a co-located departure navaid on the CIFP departure path

The `includeAllTransitionsOnMismatch` flag is not the only way a departure route can turn back over the
field. `DepartureClearanceHandler.TryResolveSidFromCifp` builds a departure route from CIFP legs (runway
transition → common → enroute transition), then appends the filed enroute remainder via
`AppendPostSidEnrouteFixes`. Filed routes commonly carry the departure's **co-located reference navaid** as a
redundant token between the SID and its transition — e.g. `HUSSH2 OAK SYRAH …`, where OAK is KOAK's on-field
VORTAC and SYRAH names the enroute transition.

Two things went wrong for a **fixed-path** RNAV SID (one with a real lateral body, unlike the RV-SIDs above):

1. **Transition matching** only inspected the *first* post-SID token (`OAK`), which is not a transition
   name, so no enroute transition was applied (the published REBAS/TAMMM/SYRAH legs were dropped).
2. **The append** then emitted the co-located OAK VORTAC literally, *after* the SID body (HUSSH, NIITE). It
   was not a leading fix, so `StripNearDepartureTargets` (which strips only leading targets) left it in — and
   `NIITE → OAK` reversed the aircraft ~140° back over the airport.

**The general rule** (`TryResolveSidFromCifp` + `AppendPostSidEnrouteFixes`): the first fix after the
departure procedure is dropped when it is the departure airport's own reference — a **real navaid within
1 nm of the field** (`IsFixColocatedWithDeparture`: OAK for KOAK) or **an identifier that doesn't resolve to
a navaid** (MRY for KMRY, dropped by the `GetFixPosition is null` skip). A real navaid that is *not*
co-located (SAC for KSAC, ~well outside 1 nm) is a genuine routing fix and is kept. Two mechanisms cooperate:

- **Transition matching** skips a leading co-located token so `HUSSH2 OAK SYRAH` matches the SYRAH
  transition and produces `HUSSH → NIITE → REBAS → TAMMM → SYRAH` (the published legs, with REBAS ≥ 8000).
- **The append** still expands airways with the co-located navaid as anchor (so `NIMI5 OAK V6 SAC` resolves
  V6 from OAK), then **drops the leading co-located fix from the result** — so it anchors expansion but is
  never flown as a waypoint. This covers the no-transition case (`HUSSH2 OAK <non-transition-fix>`) too.

RV-SIDs never needed the append fix on their own (their empty core body leaves the co-located navaid
*leading*, where `StripNearDepartureTargets` already removed it), but the append drop now handles both SID
shapes uniformly.

### The CIFP-absent RV-SID fallback

When CIFP can't resolve the SID at all (e.g. the procedure was retired from the current cycle and there's no supplementary CIFP),
`DepartureClearanceHandler` still needs to know whether to hold runway heading or turn direct to the first fix.
`NavigationDatabase.IsRadarVectorsSidWithoutLateralPath(sidName, departureAirport)` (`NavigationDatabase.cs:677`) answers this from
the vNAS body alone: it resolves the SID id, and returns `true` if the body is empty, or if **every** body fix is within 1 nm of
the departure airport (the colocated-navaid signature of an RV-SID). On `true`, `DepartureClearanceHandler` (`:670`) degrades to a
radar-vectors departure (`RvSidHoldRunwayHeading: true`) and retains the expanded fixes as the post-vectors route.

## FRD & custom fixes

### FrdResolver

`FrdResolver.ParseFrd(s)` (`FrdResolver.cs:31`) guesses the format purely by length and digit-suffix:

- **`{FIX}{radial:3}{distance:3}`** — only when `Length >= 8` and the last **6** characters are all digits and the fix-name
  remainder is `>= 2` chars. E.g. `OAK090010` → fix `OAK`, radial 090, distance 010.
- **`{FIX}{radial:3}`** — only when `Length >= 5` and the last **3** characters are all digits and the remainder is `>= 2` chars.
- **bare fix** — anything else returns `(s, null, null)`.

`Resolve` (`:7`) looks up the fix position, then projects via great-circle `ProjectPosition` (`:133`). Because the parse is
length/suffix-driven with only a `>= 2` minimum fix-name length, a short fix name with trailing digits can be misparsed — keep fix
names ≥ 2 chars and be wary of digit-suffixed names.

**FRD radials are magnetic** (AIM §4-2-10 — all bearings are magnetic unless "true" is stated; 7110.65 §4-4-3.a.1.2 — degree-distance "azimuth in degrees magnetic"). Both directions apply the WMM declination
at the **fix (origin)** position via `MagneticDeclination`: `ToFrd` converts the true bearing to magnetic before formatting, and
`Resolve` converts the parsed magnetic radial back to true before projecting. The two are exact inverses (modulo the 1° radial
rounding), so `ToFrd`→`Resolve` round-trips to the original point. Every FRD string in the app — ATCTrainer scenario `FixOrFrd`
spawns, custom-fix `frd` definitions, user-typed direct-to, the ERAM QU/RD present-position anchor — is interpreted as magnetic.

`ToFrd(lat, lon, fixes, maxNm = 50.0)` is the reverse: find the nearest fix within the cap (default **50 nm**), compute
the magnetic radial, and format `{FIX}{radial:D3}{distance:D3}`. It returns the bare fix name when within 0.1 nm, and **`null`
when the rounded distance exceeds 999 nm** (can't fit the 3-digit field).

### FRD-named fixes are never FRD anchors

vNAS NavData publishes ~6,900 adapted fixes whose *identifiers are themselves FRD strings* — `OAK169001`, `ABI037030`,
`OAK163001`. `BuildIndex` loads every one into `_navDb`, so they reach `AllFixNames`, `GetFixTuples()`, and the client's
`RadarViewModel.Fixes` alongside real waypoints. Left unguarded, `ToFrd` picks one as the nearest fix and emits an
FRD-of-an-FRD (`DCTF OAK169001231001`) that nothing downstream can render sensibly.

`FrdResolver.IsFrdIdentifier(name)` classifies them: a **2–5 ASCII-letter anchor followed by exactly six digits whose leading
three form a valid azimuth (001–360)**. The radial-only `{FIX}{radial:3}` shape is deliberately *not* matched — NavData carries
real 5-character letter+digit identifiers that would be false positives.

Two consumers:

- `ToFrd` runs one pass tracking two candidates: the nearest fix of *any* kind within 0.1 nm (returned as a bare identifier —
  `OAK169001` is a real, resolvable fix and naming it directly is correct), and separately the nearest **non-FRD-named** fix,
  which is the only thing allowed to anchor a constructed FRD. `IsFrdIdentifier` is evaluated only when a candidate beats the
  current best distance, because `ToFrd` runs per render frame over the whole fix list while a route is being drawn.
- `RadarViewModel.BuildVisibleFixes` skips them, so the Show Fixes overlay and its hover label don't paint 6,900 FRD strings
  across the scope. `AllFixNames` is **not** filtered — typing `OAK169001` in a DCT still resolves.

This covers every deduction site at once: the map right-click menu (FRD header, Copy FRD, Pin marker, Direct to, Warp), the
draw-route cursor label and drawn-waypoint names, `PilotSayBuilder` position reports, and yaat-server's ERAM QU/RD
present-position anchors (`CrcClientState.Eram.cs`).

### Custom fixes

`CustomFixLoader.LoadAll(artccsBaseDir)` (`CustomFixLoader.cs:19`) scans `{artccsBaseDir}/{ARTCC}/CustomFixes/*.json` across every
ARTCC subdirectory and deserializes each into `CustomFixDefinition` records. Per-definition validation (`LoadFile`, `:46`): a
definition with **no aliases** is skipped; one with neither `lat`/`lon` nor `frd` is skipped; one with **both** keeps lat/lon and
drops `frd` (with a warning). The loader returns warnings but does **not** dedupe aliases.

`NavigationDatabase.LoadCustomFixes` (`:1542`) consumes the result: it resolves each fix's position (lat/lon directly, or via
`FrdResolver.Resolve` for `frd`), then registers every alias into `_navDb` via `TryAdd`. **The alias-conflict warning lives here,
not in the loader** — `TryAdd` failing (alias already present) logs `"custom fix alias … conflicts with existing entry"`. Friendly
`name` values feed `_customFixNames` (used by pilot speech to render `OAK30NUM` as "Oakland Runway 30 Numbers").

`spokenPatterns` (optional, `CustomFixDefinition.cs:33`) feed the speech pipeline: each phrase is digit-normalized
(`AtcNumberParser.NormalizeDigits`), lowercased, tokenized, and paired with the first alias as a `CustomFixSpeechPattern`. The
patterns are sorted by **descending token count** so the speech collapse step's longest-match scan picks the most specific phrase
first. See [speech-recognition-pipeline.md](speech-recognition-pipeline.md) for how the speech side consumes these.

### Fix pronunciations vs. display names

`FixPronunciations/*.json` (loaded by `FixPronunciationLoader` → `LoadFixPronunciations`, `:1788`) carries two **distinct**
concepts per `FixPronunciationDefinition` entry:

- `pronunciations` — phonetic spelling hints for the speech engine. Used to seed Whisper's `initial_prompt` and to collapse spoken
  transcripts back to the fix identifier. These can be deliberately mis-spelled for the decoder (e.g. `SYRAH` → "see rah",
  `VPCOL` → "Oakland Colliseum").
- `displayName` (optional) — a genuine human-readable name shown in operator-facing terminal text. **Only entries that supply a
  `displayName` surface in the display** — so phonetic-only hints like "see rah" never leak into a command response. Populated into
  `_fixDisplayNames`; display names may differ from pronunciations (`VPCOL` displays the corrected "Oakland Coliseum").

Three accessors expose these, with different fallback chains:

| Method | Returns | Fallback chain | Used by |
|--------|---------|----------------|---------|
| `GetFixPronunciations(fix)` (`:442`) | phonetic list | — (empty if none) | speech pipeline (Whisper hints) |
| `GetFixFriendlyName(fix)` (`:460`) | spoken label | `pronunciations[0]` → custom-fix `name` → raw id | spoken traffic advisories |
| `GetFixDisplayName(fix)` (`:479`) | **display** name or `null` | `displayName` → custom-fix `name` → `null` (never phonetic, never navaid/airport) | command responses, pilot readbacks, `AT <fix>` conditions, SPOS/SHDG |

The presentation helpers `PhraseologyVerbalizer.FixDisplayText` / `FixDisplayTextUpper` wrap `GetFixDisplayName` to render a fix as
`"Name (ID)"` (e.g. `VPCBT` → "Lake Chabot (VPCBT)") or the bare uppercase identifier when it has no display name.

## Approach shorthand & gate distances

### Approach shorthand resolution

`ResolveApproachId(airportCode, shorthand)` (`NavigationDatabase.cs:1063`) maps a terse approach reference to a published approach
id. It tries an exact id match first, then `ParseShorthand` (`:1851`) which decomposes into `(typeCode, runway, variant)`:

- `TryStripTypePrefix` (`:1901`) recognizes spelled-out prefixes (`ILS`→`I`, `LOC`→`L`, `RNAV`→`H`, `GPS`→`P`, `VOR`→`V`,
  `NDB`→`N`, `LDA`→`X`, `TACAN`→`T`, `SDF`→`U`) and the single-letter `{letter}{digit}` form (e.g. `I28R`).
- With a type code, it matches `TypeCode + Runway (+ optional variant suffix)`. On a miss it tries the **H↔R alternation** (RNAV
  variants), so `H28R` will fall back to `R28R` and vice-versa.
- With no type code (a bare runway like `28R`), it returns the best approach for that runway by **type priority** (`GetTypePriority`,
  `:1949`): ILS < LOC < RNAV(GPS) < RNAV < GPS < everything else.

`ResolveApproachCandidates(airportCode, shorthand)` (`:1148`) is the *multi-result* variant: when a shorthand matches several
variants (e.g. `I17R` matching both `I17RX` and `I17RZ`), it returns all of them for `ApproachCommandHandler` to disambiguate by
connectivity. See [command-handlers.md](command-handlers.md) for how the approach handler consumes these and
[landing-and-runway-exit.md](landing-and-runway-exit.md) for what happens once an approach is flown.

### Approach gate / min-intercept distances

`ApproachGateDatabase` (`ApproachGateDatabase.cs`) precomputes a minimum intercept distance per (airport, runway) from the FAA
7110.65 §5-9-1 approach-gate concept: `approachGate = max(FAF→threshold distance + 1 nm, 5 nm)`, then `minIntercept = gate + 2 nm`.
`Initialize(cifpData)` (`:26`) walks `cifpData.FafFixes`, resolves the FAF position (from `NavigationDatabase.Instance`, falling
back to the CIFP terminal-waypoint table), measures FAF→threshold, and stores the result keyed by `(NormalizeAirport(airport),
runway)`. `GetMinInterceptDistanceNm` (`:80`) returns the stored value or a **7.0 nm default** when uninitialized or unknown.

`ApproachGateDatabase` has its **own private `NormalizeAirport`** (`:97`) that strips a leading `K` with no length check — distinct
from `NavigationDatabase.NormalizeAirport`'s length-4 guard. Both are used to key the same airport ids; the difference is benign
for CONUS codes but worth knowing when matching keys.

## Spatial helpers

Airports are indexed into a 1°×1° (~60 nm) spatial grid at build time, deduplicated under the canonical id
(`AddAirportToSpatialIndex`, `NavigationDatabase.cs:631`). Two lookups walk it:

- `FindNearestAirportElevation(position, maxRangeNm = 100)` (`:522`) — nearest airport's elevation, a terrain proxy for STARS/AGL
  gating when no precise runway reference exists.
- `FindNearestSizeableAirport(position, minRunwayLengthFt, maxRangeNm)` (`:562`) — nearest airport whose longest *pavement* clears the
  threshold; used to anchor pilot position reports against a recognizable field instead of an arbitrary RNAV waypoint.

Both expand the bucket radius to cover the range cap (`ceil(maxRangeNm / 60)`).

## Adding a lookup / extending

1. If the lookup is keyed by airport, route the key through `NormalizeAirport` (or use `AirportIdsMatch`) **and** implement the
   3↔4-letter K-prefix fallback — don't assume the caller passed FAA vs ICAO form.
2. If it's a procedure lookup, go through `ResolveSidId`/`ResolveStarId`/`FindSidInList` so version drift (`CNDEL5`→`CNDEL6`)
   resolves; consider whether the ARTCC-fragment and supplementary-CIFP fallbacks apply (all three tiers, in order).
3. Decide `Instance` (throws — required lookup) vs `InstanceOrNull` (best-effort with a fallback).
4. If tests need to seed the new data, add a parameter to `ForTesting(...)` so tests don't reach for a synthetic stub.
5. If the lookup parses CIFP per-airport, cache it in a `ConcurrentDictionary` and use `GetOrAdd`, mirroring `GetSids`.

## Footguns / Pitfalls

- **RV-SID emit-all is the default and it's wrong for navigation.** `RouteExpander.Expand` defaults
  `includeAllTransitionsOnMismatch = true`. Any caller building a flying `NavigationRoute` MUST pass `false`, or a radar-vectors
  SID (NIMI5/OAK6) fabricates a turn-back through every synthetic `[OAK, …]` transition fix. Autocomplete/UI/speech paths want
  `true`; everything else wants `false`. (See the caller table above.)
- **The singleton is mutable static.** `Instance` returns the `ScopedOverride` (AsyncLocal) if set, else the process-wide default.
  `ForTesting()` + `SetInstance()` leak into parallel test classes — that's why such tests join `[Collection("NavDbMutator")]` and
  why `EnsureInitialized()` re-sets the real instance on every call. Prefer `ScopedOverride` (auto-disposes, no leak).
- **`Instance` throws; `InstanceOrNull` returns null.** Picking the wrong one either crashes a best-effort lookup or silently
  no-ops a required one.
- **Procedure-version drift.** A scenario filed `CNDEL5` resolves to `CNDEL6` only via the `StripTrailingDigits` base-name
  fallback. An exact-id lookup that bypasses `ResolveSidId`/`FindSidInList` silently misses the current cycle's procedure. Note
  `StripTrailingDigits` preserves ≥ 2 chars and returns the input unchanged when there are no trailing digits — and
  `ResolveSidId`/`ResolveStarId` return `null` (not the input) for a digit-less id that isn't an exact key.
- **Retired procedures need the supplementary CIFP or an ARTCC fragment.** A SID dropped from the current FAA cycle resolves only
  via the fragment tier or `GetSid → GetSupplementarySids`. Without `supplementaryCifpFilePath` wired (production passes it; a bare
  `ForTesting`/`new` with no supplementary path does not) *and* without a committed fragment, the SID returns `null`.
- **The prior-cycle chain expires; a fragment does not.** The chain is capped at ~12 months and is built from whatever cycles that
  particular machine cached, so the same scenario can fly differently on two deployments and will eventually stop resolving
  everywhere. Anything a facility actually depends on belongs in `Data/ARTCCs/{ARTCC}/Procedures/` — stash it with
  `tools/stash-procedure.py` **while a cycle that still has it is reachable**, not after the window closes.
- **A test asserting an unresolvable procedure must pass `artccsBaseDir: ""`.** Otherwise it picks up the committed fragments and
  the procedure resolves after all. This already bit both KOAK NIMITZ test classes.
- **CIFP speed limits carry a *type*, and continuation records are skipped.** `CifpParser.ParseSpeedRestriction(speedStr, descChar)`
  maps the ARINC 424 §5.261 speed-limit description (col 117) to `CifpSpeedRestrictionType { AtOrBelow, AtOrAbove, Mandatory }`:
  `'-'` → AtOrBelow (max), `'+'` → AtOrAbove (min — only ~4 legs in all US CIFP, e.g. KIAH DOOBI3 HHART 230+), blank → Mandatory.
  Physics maps AtOrBelow/Mandatory → `SpeedCeiling` and AtOrAbove → `SpeedFloor` (skipped in the decel look-ahead so a minimum never
  slows the aircraft; the 91.117 250-kt cap still clamps the floor). The parser also **skips ARINC continuation records**
  (continuation-record number at col 38 ≠ `' '`/`'0'`/`'1'`) — they repeat a fix with a different field layout, so their reserved
  padding otherwise injects phantom speed/alt restrictions (e.g. IAH RNAV 08R MATON → 2 kt) and duplicate fixes.
- **FAA↔ICAO K-prefix fallback is duplicated across several lookups.** `GetAirportElevation`, `GetAirportName`, and
  `HasRunwayAtLeast` each re-implement it. A new airport-keyed lookup that forgets it fails for whichever of `OAK`/`KOAK`
  the caller didn't pass. `GetRunway` notably does *not* carry the fallback — callers (e.g. `ApproachGateDatabase.Initialize`)
  must try both forms themselves. `NormalizeAirport` strips `K` only at length 4.
- **CIFP procedures parse lazily and cache for the process lifetime.** The first `GetSids`/`GetStars`/`GetApproaches` per airport
  pays a parse cost; there's no invalidation on AIRAC change within a run.
- **FRD parse is ambiguous by construction.** `ParseFrd` distinguishes the three forms purely by length + trailing-digit count
  (6-digit / 3-digit / none) with a `>= 2` fix-name minimum. A short fix name plus trailing digits can be misparsed. `ToFrd` caps
  at 50 nm by default and returns `null` past 999 nm. Note `ParseFrd` also decomposes a *bare* FRD-named fix — `ParseFrd("OAK169001").Fix`
  is `"OAK"`, not `"OAK169001"` — so a test that inspects the parsed anchor must probe far enough out that the distance doesn't
  round to zero, or it will silently assert against the bare-name path instead of a constructed FRD.
- **The nav DB is full of fixes that are already FRDs.** ~6,900 vNAS-adapted identifiers (`OAK169001`) sit in `_navDb` next to real
  waypoints. Anything that *deduces* an FRD must go through `ToFrd`, which excludes them via `IsFrdIdentifier`; a new code path that
  picks a nearest fix itself will reintroduce `OAK169001231001`.
- **Custom-fix alias conflicts surface in `NavigationDatabase`, not the loader.** `CustomFixLoader` only validates aliases/lat-lon/frd
  presence; the "alias conflicts with existing entry" warning comes from `_navDb.TryAdd` failing in `LoadCustomFixes`. A custom
  fix whose alias collides with a real fix is silently not registered (warning only).
- **`ApproachGateDatabase.Initialize` depends on `NavigationDatabase.Instance`.** It reads the nav singleton internally, so
  initialize the nav DB first (production does: nav DB, then approach gate DB).
- **`ExpandRouteForNavigation` strips leading fixes within 1 nm of the departure airport.** A colocated VOR (like `OAK` at OAK)
  is dropped; callers expecting the raw expansion will see those fixes missing.
