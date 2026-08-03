# Military Training Routes and Aerial Refueling (AP/1B)

> Read this before touching `src/Yaat.Sim/Data/MilitaryRoutes/`, `tools/build-mtr-data.py`,
> `MilitaryRoutePhase`, `AerialRefuelingAnchorPhase`, or `MilitaryRouteCommandHandler`.

YAAT recognises the DoD's published military routes the way it recognises airways: a filed
`SAT263043 IR149 LRD040028` expands into the route's points, the aircraft flies the published
altitude blocks, and the instructor can issue the FAA JO 7110.65 §9-2-6 and §9-2-13 clearances.

Two route systems, from one publication:

| | Chapters 2–4 | Chapter 5 |
|---|---|---|
| What | IR / VR / SR **training routes** | AR **refueling tracks and anchors** |
| Count | 213 IR, 304 VR, 131 SR | 156 tracks, 91 anchors |
| Geometry | one-way point sequence | per-direction point sequence; anchors add an orbit |
| Altitude | a block **per segment** | one block **per entry** |
| Clearance | §9-2-6 (`CMTR`, `MTRA`, `XMTR`, `SAYEXIT`) | §9-2-13 (`CAR`) |

## Data source — the AP/1B PDF, cross-checked against FAA AIS

AP/1B (*Area Planning — Military Training Routes, North and South America*) is published by NGA on
the 8-week AIRAC cycle at `https://www.daip.jcs.mil/pdf/ap1b.pdf`. There is no machine-readable
equivalent: the FAA AIS `MTRSegment` ArcGIS layer carries geometry for 348 of the 648 training
routes and none of the refueling data, so it is a cross-check rather than a source.

The PDF is **not fetched automatically** — daip.jcs.mil serves a certificate chain that fails
default verification, and silently disabling verification in a committed build tool is the wrong
default. Download it yourself, then point `--input` at it.

## Pipeline

**`tools/build-mtr-data.py`** (offline, `uv run`) produces two committed Brotli fixtures plus a
plain-text `.meta` provenance sidecar for each:

```bash
curl -sk -o .tmp/ap1b.pdf https://www.daip.jcs.mil/pdf/ap1b.pdf   # -k: the DoD chain fails default verify
uv run tools/build-mtr-data.py --input .tmp/ap1b.pdf --report .tmp/mtr-report.json
```

| Fixture | Contents |
|---|---|
| `ap1b-mtr.json.br` | 648 training routes, 7,039 points, 630 with published widths |
| `ap1b-ar.json.br` | 247 refueling entries, 1,625 points, 385 anchor pattern corners |

The fixtures are **regenerated, never hand-edited**. If a route looks wrong, fix the parser.

### Why it reads word boxes rather than text

`pdftotext -layout` interleaves the Lat/Long column out of row sync on 245 of the 648 routes. The
tool reads word bounding boxes and buckets them into per-page column bands instead, which associates
every cell with its own row.

### Extraction facts that were expensive to learn

1. **A route header must be the only word on its row.** Matching the designator pattern anywhere in
   the top band picks up cross-references in wrapped SOP prose on continuation pages ("IR-022 is
   normally flown on…") and fabricates 47 phantom empty routes.
2. **Column bands are cached per page *parity*.** Facing pages are mirrored — `Pt` sits at x≈105 on
   one and x≈155 on the other — and continuation pages repeat neither the header nor the originating
   page's margins. Getting this right recovered 525 points across the 42 longest routes.
3. **The Lat/Long band is a tight window** (header x + 2 … + 26), not open to the right edge. SOP
   entries cite tower and hazard coordinates inline in prose; an open band turns those into points.
4. **A point row must carry its own latitude.** That alone rejects the SOP prose whose short words
   ("NM", "SA") drift into the Pt column — 82 phantom points.
5. **Chapter 5 is printed sideways.** 203 of 211 words on a page carry `upright=False` with a 90°
   text matrix, so pdfplumber (which groups by page x) assembles them backwards — "thgilF" for
   "Flight". `page_words()` rebuilds them in a transposed frame, after which chapter 5 reads
   structurally like chapters 2–4.

### Two heuristics that are wrong — recorded so they are not retried

- **Ending the table on an SOP list marker** `(a)` / `(1)`. Markers appear legitimately *inside* SR
  tables — SR-221 carries `(a) SUCKCHON DROP ZONE` as an annotation on point J — and using them
  costs 122 genuine points across SR-218…SR-246.
- **Treating `NOTE:` / `CAUTION:` as table enders.** VR-1001 carries a mid-table `NOTE:` in the
  Altitude column; ending there truncates the route at K and swallows K's longitude.

### Correctness gates

- **The FRD oracle** is the important one. Where a point publishes a Fac/Rad/Dist, the great-circle
  distance from that navaid to the printed lat/long must equal the published distance. Distance is
  declination-independent, so it needs no WMM model, and a row mis-association throws a point tens to
  hundreds of NM off. Currently **99.93%** (5,612/5,616) on training routes and **99.94%**
  (1,738/1,739) on refueling data, median error 0.22 NM. The gate is 95%; the handful of
  disagreements are AP/1B errata — VR-1140 point A genuinely prints `BFV 094/219`, an implausible
  219 DME.
- **The FAA cross-check** compares against the AIS `MTRSegment` layer, FAA-vertex → nearest parsed
  point. Deliberately **not** fatal per route: the two sources publish on different cycles and
  diverge in both directions (VR-1351 is 14 points in AP/1B but 2 vertices in the layer; IR-983 is in
  the layer and absent from AP/1B 2607). The gate is on the *fraction* of diverging routes.

## Recognition and expansion

Routes are **shadow-registered into `_airways`**, which is what lets `JAWY IR149` and the radar
context menu's `GetFiledAirways` work with no changes to either. Expansion does *not* ride that path:
`ExpandAirwaySegment` walks bidirectionally and matches anchors by name, both wrong here, and it now
refuses to reverse along a military route.

Points live in a separate `_militaryRoutePoints` dictionary consulted by `GetFixPosition` only after
`_navDb` misses. That keeps ~8,600 synthetic names out of `AllFixNames` *and* `GetFixTuples`, so they
never reach autocomplete, the DIST suggester, the scope's fix overlay, or FRD anchoring — and a real
fix always wins a clash by construction.

Names are `{designator}{label}` (`IR149A`, `AR1ARIP`). AP/1B labels always start with a letter, so a
minted name never ends in three or six digits and `FrdResolver.ParseFrd` can never read it as an FRD
anchor. A test pins that property.

`MilitaryRouteExpander` anchors on the bracketing **FRDs**: AP/1B chapter 1 §IV.B.1 files a route as
`{entry FRD} {designator} {exit FRD}`, and those are the route's published entry/exit FRDs rather
than names of points on it. Snap tolerance 15 NM, forward-only, with one-sided and no-anchor
fallbacks.

## Directions are separate geometries

**A refueling track's two published directions are not the same line flown backwards.** Opposing
tracks are laterally offset so the traffic is separated: only 33 of the 82 two-direction entries are
exact reversals, and AR4A's southbound ARIP sits 50 NM from its northbound exit.

So each direction is a `MilitaryRouteVariant` with its own points, and its points are name-suffixed
(`AR4AARIPN` vs `AR4AARIPS`) so one synthetic fix name never maps to two positions. `MilitaryRoute.Points`
exposes the first variant, which is what keeps expansion and the airway shadow index working without
either learning about directions.

Which direction is meant is answered two ways:

- **In a filed route** — `MilitaryRouteExpander.SelectVariant` scores the bracketing anchor *pair*.
  Scoring the entry alone does not separate offset parallels, whose entries can be nearly co-located
  while their exits are a hundred miles apart.
- **In a clearance** — `MilitaryRouteCommandHandler` picks the direction with a joinable point
  nearest ahead of the aircraft. The direction is not something the clearance says.

## Flying a route

`MilitaryRoutePhase` handles training routes and refueling **tracks**; `AerialRefuelingAnchorPhase`
handles **anchors**.

A phase rather than a queued `CommandBlock` because the vertical constraint changes at every route
point with no new clearance and has to be re-asserted from durable state: `CommandBlock.ApplyAction`
is not restored after a snapshot, and `FlightCommandHandler`'s climb, descend and force-altitude
paths all null both altitude bounds.

Anchors get their own phase because a track **terminates** and an anchor does not. A track runs from
its ARIP to an exit and the phase completes when the points run out; an anchor is a racetrack the
aircraft stays in until ATC clears it out, so an empty navigation route means "fly another lap". The
orbit flown is the pattern AP/1B prints — its corners, in the published order — rather than a
racetrack computed from a fix and an inbound course.

**Where in the block the aircraft flies** differs by route system, and the difference is deliberate:

- **Training route** — just above the floor (AIM 3-5-2 low-level tactical training). On a scope, MTR
  traffic is recognisable precisely by Mode C working through the block segment by segment.
- **Refueling** — mid-block. §9-2-13.f NOTE 3 has refueling occupying at least three consecutive
  altitudes, and §9-2-13.i has the tanker departing from the top of the block and the receiver from
  the bottom.

**Speed.** The 14 CFR 91.117(a) waiver AP/1B chapter 1 §I grants only lifts the 250 kt cap; nothing
publishes a speed. `AircraftPerformance.MilitaryRouteSpeedKts` supplies a category default on
establishment — 400 for a tactical jet, 250 otherwise — because operating above 250 knots is the
defining characteristic of the program (P/CG, AIM 3-5-2.c). IR and VR only: an SR is defined as
250 KIAS or less with no waiver, and a refueling track is flown at tanker speed.

## Commands

| Verb | Aliases | Args | Section |
|---|---|---|---|
| `CMTR` | `CIR` | `<route>` / `<route> <alt>` / `<route> B<alt>` | §9-2-6.a |
| `MTRA` | `MRA` | — | §9-2-6.a |
| `XMTR` | `EMTR` | `<dest> [<alt>] [VIA <route>]` | §9-2-6.b |
| `SAYEXIT` | `SAYXF` | — | §9-2-6.e |
| `CAR` | `CREF` | `<track>` / `<track> <floor> <ceiling>` | §9-2-13 |

`CMTR` on a refueling track and `CAR` on a training route each point at the other: §9-2-6 is titled
*IFR Military Training Routes* and §9-2-13 has its own phraseology and a block altitude clause.

`XMTR`'s altitude sits **between the destination and `VIA`** because the route of flight runs
greedily to end of line — a trailing altitude could not be told apart from a route fix, while `VIA`
delimits the slot and reads in §9-2-6.b's own order.

### Phraseology inverts between the two systems

- **§2-5-1.f (training routes)** — state the letters, then the number in group form:
  `IR531` → "i-r five thirty one". `PhraseologyVerbalizer.SpellMilitaryRoute`.
- **§9-2-13 (refueling)** — "REFUELING ALONG (**number**) TRACK": the letters are **not spoken at
  all**, and "track" follows the number. `AR312` → "three twelve track".
  `PhraseologyVerbalizer.SpellRefuelingTrack`.

A test pins the two apart. Note that §2-5-1.f covers only I-R and V-R; the SR group form YAAT emits
is an extension by analogy, not a citation.

All five readbacks are **builders** in `PilotResponder.VerbalizeForReadback`, not
`PhraseologyRule`s — the rules exist but are `SttOnly`. Two reasons: rule selection happens per
canonical type without seeing which arguments are present, so `CMTR`'s three forms collapse onto the
longest pattern and an assigned altitude is silently read back as "maintain route altitudes"; and
`RenderPattern` joins tokens with spaces, so a pattern cannot carry the comma before an altitude
clause. `MTRA` and `XMTR` additionally have no choice — neither carries its designator, which lives
in aircraft state, and nothing in the `Verbalize → VerbalizeCore → ExtractArgs` chain sees an
`AircraftState`.

## Client display surfaces

- **Aircraft List** — an `MTR` column showing designator plus block (`IR149 050B060`). The block is
  written in the hundreds-of-feet strip form of §4-5-2 / §13-1-1, not `5,000-6,000`.
- **Radar** — `NavRouteShapeKind.MilitaryRouteCorridor` draws the protected corridor as a closed
  dashed polygon under "Show nav route". §9-2-6.d is the one fact in §9-2-6 a controller must keep
  acting on, and AP/1B publishes the width per span and *asymmetrically* about the centerline, so it
  cannot be drawn as a uniform buffer.

The altitude crosses the wire as **pre-rendered text**, not two numbers. The change tracker
fingerprints it, and an AGL bound re-resolving to the same MSL pair would otherwise churn the
fingerprint every tick. More importantly it reports the *resolved MSL* pair the sim enforced rather
than the published notation — see the AGL footgun below.

## Footguns

- **AGL bounds are resolved against a terrain proxy, and YAAT has no terrain model.** `MvaDatabase`
  floor minus the 1,000 ft obstacle buffer is the primary proxy (MVA errs high, airports err low);
  nearest-airport elevation is the fallback. An unresolvable AGL bound is left **unenforced** rather
  than defaulted to sea level — a "05 AGL" floor armed at 500 ft MSL sits thousands of feet
  underground over the Great Basin and the aircraft would actively descend toward it. This is the
  design's known inaccuracy, not an oversight.
- **`MilitaryRouteDatabase.Default` is read from inside the `NavigationDatabase` constructor**, so a
  leaked test override poisons every navigation database built afterwards. Use `ScopedOverride`, not
  the bare `SetInstance`, and prefer constructor injection over both.
- **`IsBehind` uses heading, not ground track**, deliberately. `AircraftState.TrueTrack` is written by
  `FlightPhysics` each tick and carries no "populated yet" signal, so it is still zero on an aircraft
  spawned and cleared in the same breath — and zero is a legal northbound track, so the default
  cannot be told from a real value. Reading it flips the join decision by 180°.
- **The DCT reversal guard lives in `CommandDispatcher`, not the phase.**
  `MilitaryRoutePhase.CanAcceptCommand` returns `ClearsPhase` for a direct-to, so by the time it
  could object it has been torn down. There are seven direct-to verbs and the guard covers all of
  them in one place.
- **IR-149 point A publishes "As assigned to"**, so its first segment legitimately arms no block. A
  test asserting on bounds must sequence past it.
- **`PhaseList.Clear` only calls `OnEnd` on an `Active` phase** — a test driving `OnTick` directly
  must set `Status = PhaseStatus.Active` first or teardown silently does not run.
- Military-route tests use `[Collection("NavDbMutator")]` and call `TestVnasData.EnsureInitialized()`
  in the class constructor.

## Not modelled

- **Route width containment.** The corridor is drawn; nothing keeps an aircraft inside it or keeps
  other traffic out.
- **`CROSS (fix) AT OR LATER THAN (time)`** (§9-2-6) — see
  [issue #329](https://github.com/leftos/yaat/issues/329). A time restriction is a scheduling
  constraint rather than a state constraint, which is a different control problem.
- **The §4-5-2 `CRUISE` clearance** — see [issue #328](https://github.com/leftos/yaat/issues/328).
  YAAT's existing `CRUISE` verb amends the *filed* cruise altitude via `TrackEngine`; §9-2-6.a's
  cruise clearance is a vertical block from the MEA and a different concept behind the same name.
- **MARSA as a typed command.** §9-2-6.c establishes it by letter of agreement and §9-2-13 NOTE 3
  makes it a declaration on frequency, so it is route metadata. An amendment issued to a MARSA
  aircraft is accepted and **voids** MARSA per §9-2-13.e rather than being rejected — a controller
  who watches separation responsibility land back on them learns the rule; one whose keystroke is
  refused learns nothing.
