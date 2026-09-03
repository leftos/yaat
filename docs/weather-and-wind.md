# Weather Parsing, Wind Interpolation, and Magnetic Declination

> Read this before touching `WeatherProfile`, `WeatherTimeline`, `MetarParser`, `WindsAloftParser`, `WindInterpolator`,
> `MetarInterpolator`, `MagneticDeclination`, `LiveWeatherService`, or the wind/declination consumers in `FlightPhysics`.
> Four direction conventions and three independent interpolation algorithms coexist here and are never co-located in code —
> mixing them silently produces ~15-20° heading/wind errors.

The ISA compressible-flow airspeed conversions (`IasToTas`/`TasToIas`/`MachToIas`/`IasToMach`) live in `WindInterpolator` but are
**owned by [flight-physics.md](flight-physics.md)** (airspeed-frame model and per-category constants). This doc summarizes and links;
it does not re-derive the equations.

## Data flow at a glance

```
                 scenario JSON (LoadWeather)        live fetch (LiveWeatherService, client)
                          │                                        │
                          ▼                                        ▼
              WeatherTimelineParser.Parse                  BuildLiveWeatherAsync
              (auto-detect v1 vs v2)                       → one WeatherProfile
                  │              │
            v2 Timeline      v1 Profile
                  │              │
                  ▼              ▼
            WeatherTimeline   WeatherProfile  ──────────────────────────────────┐
                  │                                                              │
   per tick:  GetWeatherAt(elapsedSeconds) → interpolated WeatherProfile         │
                  │                                                              │
                  ▼                                                              ▼
            World.Weather (the single active WeatherProfile for the room)  ⇒ CRC / pilot displays
                  │
                  ▼
            FlightPhysics.Update(ac, …, weather)
              ├─ UpdateNavigation  → WCA (crab) from GetWindAt
              └─ UpdatePosition    → groundspeed vector from IasToTas + GetWindComponents
```

`World.Weather` is one `WeatherProfile` at a time. A v2 timeline is collapsed to a profile every tick by `GetWeatherAt`; a v1 profile
or a live-weather profile is used directly. Selection is wired in `SimulationEngine.cs` (`ApplyWeatherJson`, replay restore paths) and
in the live server tick (`RoomTickLoopService.cs`); see [Consumption and broadcast](#consumption-and-broadcast).

## Data model

### `WeatherProfile` / `WindLayer` (`WeatherProfile.cs`)

`WeatherProfile` is the ATCTrainer-compatible "v1" shape and also the runtime currency everything downstream consumes.

| Member | Notes |
|---|---|
| `WindLayers` (`List<WindLayer>`) | **Re-sorted ascending by altitude on every set** (`WeatherProfile.cs:43-47`) — never assume insertion order survives. |
| `Metars` (`List<string>`) | Raw METAR strings; parsed lazily and cached per airport. |
| `Precipitation` (`string?`) | Free-form; only compared for change detection, not modeled in physics. |
| `ParsedMetarOverrides` | **`[JsonIgnore]`** (`WeatherProfile.cs:59`). Pre-computed interpolated METAR values; see [the override handshake](#footguns). |
| `GetWeatherForAirport(id)` | Override → exact station → 50 nm IDW (`WeatherProfile.cs:66`). Caches the result in a per-airport dictionary. |

`WindLayer` (`WeatherProfile.cs:5`):

| Field | Meaning |
|---|---|
| `Altitude` | feet **MSL** (`WeatherProfile.cs:10`). |
| `Direction` | wind **FROM** direction in **degrees MAGNETIC** — the authored MEAN; the instantaneous wind physics sees is this plus the `WindVariation` perturbation. |
| `Speed` | knots (mean). |
| `Gusts` (`double?`) | peak speed; drives the gust perturbation amplitude, the reported G group, and the approach-speed wind additive. |
| `DirectionVariabilityDeg` (`double?`) | half-spread: direction wanders within ±this around the mean (a METAR `180V240` on a 210 mean authors 30). |
| `Variable` (`bool?`) | VRB — no prevailing direction; the wind wanders the full circle at light speeds. |

`WeatherProfile.SurfaceElevationFt()` resolves the ground datum for the variability altitude taper from the profile's first
nav-database METAR station (fallback: lowest layer altitude) — the lowest layer is often authored well above the field, and
live weather authors it at 0 MSL regardless of terrain, so the layer altitude is not a usable ground reference.

### `WeatherPeriod` / `WeatherTimeline` (`WeatherPeriod.cs`, `WeatherTimeline.cs`)

The "v2" shape is a `WeatherTimeline` with a list of `WeatherPeriod`s:

| `WeatherPeriod` field | Meaning |
|---|---|
| `StartMinutes` | period activates at this elapsed sim-minute. |
| `TransitionMinutes` | blend duration into this period; `0` = instant. |
| `WindLayers` / `Metars` / `Precipitation` | the target weather for this period. |

`WeatherTimeline.GetWeatherAt(elapsedSeconds)` (`WeatherTimeline.cs:23`) is the per-tick collapse: it picks the active period (last
period whose `StartMinutes*60 ≤ elapsed`), and — if inside the transition window — blends from the **previous** period. See
[time interpolation](#c-time-along-the-timeline--weathertimelinegetweatherat).

### v1 vs v2 JSON and auto-detection (`WeatherTimelineParser.cs`)

`WeatherTimelineParser.Parse(json)` returns a `WeatherParseResult` discriminated union — exactly one of `Timeline`, `Profile`, or
`Error` is set (`WeatherTimelineParser.cs:8`):

- **Auto-detect rule** (`WeatherTimelineParser.cs:41`): if the root object has a `"periods"` array → v2 `WeatherTimeline`; otherwise → v1 `WeatherProfile`.
- **v2 validation** (`ParseV2`): empty `periods` → error; **every period must have ≥1 wind layer** or it errors (`WeatherTimelineParser.cs:73-79`); periods are then **sorted by `StartMinutes`**.
- **v1** (`ParseV1`): straight `WeatherProfile` deserialize.
- JSON is parsed case-insensitively (`PropertyNameCaseInsensitive = true`).

## Wire-format parsers

### `MetarParser` (`MetarParser.cs`)

`MetarParser.Parse(metar)` produces a `ParsedMetar` record (`MetarParser.cs:17`): station id, ceiling (ft AGL), cloud layers,
visibility (statute miles), and optional wind/altimeter.

- **Station id heuristic** (`MetarParser.cs:65-79`): first 4-char token of letters/digits whose first char is a letter (e.g. `KSFO`, `KE16`), explicitly skipping `AUTO`. No id → returns `null`.
- **Visibility** (`ParseVisibility`): handles `P6SM` (greater-than → returns the number), `M1/4SM` (less-than → the fraction), mixed `1 1/2SM` → `1.5`, pure fraction `1/2` → `0.5`, and whole `10SM`. The regex requires word boundaries (`(?<!\S)…(?!\S)`).
- **Cloud layers** (`ParseLayers`): `FEW/SCT/BKN/OVC` + 3-digit **hundreds of feet** (`OVC012` → 1200 ft). `CLR`/`SKC` → no layers, no ceiling. **Ceiling = lowest `BKN` or `OVC` base** (`FEW`/`SCT` never set a ceiling).
- **`VV` (vertical visibility / indefinite ceiling)** is modeled as a **synthetic `Overcast` layer** (`MetarParser.cs:239-250`) so downstream multi-layer obstruction logic (`VisualDetection`) treats it like a real OVC — it is **not** an OVC token in the source METAR.
- **Wind** (`ParseWind`): `dddssKT` / `dddssGggKT` / `VRBssKT`. `VRB` → null direction + `WindVariable = true`. A `dddVddd` variable-direction group after the wind group parses into `WindVarFromDeg`/`WindVarToDeg` (extremes coded clockwise; `350V030` wraps through north).
- **Altimeter** (`ParseAltimeter`): `A2992` → `29.92` inHg.
- **`ToIcao`** (`MetarParser.cs:110`): 3-letter FAA id → prepend `K`; 4-char `K…` assumed already ICAO.

Two interpolation helpers live here (used by the timeline, not by `Parse`):
- `CeilingFromLayers(layers)` — lowest `BKN`/`OVC` base from a layer list (re-derives a ceiling from an interpolated layer set instead of lerping the old scalar).
- `InterpolateLayers(from, to, t)` — pairwise by index (both lists pre-sorted by base); **base altitudes lerp, cover type step-changes at `t=0.5`** (cover is discrete), and extras on the longer side pass through unchanged.

### `WindsAloftParser` (`WindsAloftParser.cs`) — FAA FD decode

Parses the FAA Winds-and-Temperatures-Aloft fixed-width text (`aviationweather.gov /api/data/windtemp`) into `StationWinds`
(a station id + a list of `WindAtLevel`). `WindAtLevel` carries `DirectionTrue` (degrees **TRUE**, `int`), `SpeedKts`, and
`IsLightVariable` (`WindsAloftParser.cs:4`).

- **Header-driven columns** (`ParseHeaderColumnCenters`): finds the `FT` header line and records the **center character index** of each standard level (`3000 6000 9000 12000 18000 24000 30000 34000 39000`). Data tokens are then assigned to the **nearest column by center distance**, not by fixed offset — this is fragile to header spacing.
- **`DecodeWind(altitudeFt, code)`** (`WindsAloftParser.cs:164`) decodes the 4-char `DDSS` code:
  - Strips a temperature suffix first (`2714-08` → `2714`, sign at index ≥ 4).
  - `9900` → light-and-variable: direction 0, speed 0, `IsLightVariable = true`.
  - `DD < 50`: direction = `DD × 10`, speed = `SS` kt.
  - **`DD ≥ 50`: direction = `(DD − 50) × 10`, speed = `SS + 100` kt** — the overflow encoding for winds ≥ 100 kt.
  - Direction `000` with non-zero speed normalizes to `360`.

## Variable wind — `WindVariation` + `WindObservation`

The wind physics flies in is time-varying: `WindInterpolator.GetWindAt(profile, alt, simTimeSeconds, phaseSeconds)` resolves
each layer's variability amplitudes (authored gusts / direction spread / VRB, cross-derived when only one is authored) and
perturbs the interpolated mean via `WindVariation.Compute` — deterministic band-limited value noise, a pure function of sim
time (no RNG, no state), so replays reproduce exactly and no `RecordedAction` exists for it.

- **Bounded by construction**: instantaneous speed never exceeds `mean + (Gusts − mean)` and direction never leaves the
  authored ±`DirectionVariabilityDeg` arc. The normalized noise stack is over-scaled and clamped
  (`LongitudinalEnvelopeScale`/`LateralEnvelopeScale`) so finite observation windows actually realize the envelope.
- **Structure**: 3 octaves at Kolmogorov amplitude scaling, base period `clamp(432/U_kt, 12, 120) s`, finest floored at 4 s,
  plus a lateral-only 300 s meander octave; lulls are shallower than gusts (`LullAsymmetryFactor`). VRB uses a 240 s meander,
  its own shallow taper (full ≤200 ft AGL, zero by 1000), speed ±50% around the reported 2-minute mean, direction wrapping
  the circle.
- **Altitude taper**: full amplitude ≤1000 ft above `SurfaceElevationFt()`, smoothstep to zero at 3000 ft — cruise stays
  clean; aloft layers only perturb if they author variability themselves.
- **Per-aircraft decorrelation**: each aircraft samples the field at a stable callsign-hash phase
  (`WindVariation.PhaseSecondsFor`, FNV-1a — never `string.GetHashCode`); reporting surfaces sample at phase 0. The mean is
  common; only the perturbation is phase-shifted. A layer with no authored variability passes through bit-identical.
- **Sim time is quantized to whole seconds** inside `Compute` so the live 1 Hz path and the 0.25 s sub-tick replay path agree.

`WindObservation.Observe(weather, elapsedSeconds)` samples the phase-0 field the way an ASOS does (2-min scalar mean speed +
vector mean direction, 10-min peak/lull, 5-s grid, at the `SurfaceElevationFt()` datum). `MetarIssuer` reports the observed
means (so successive reports differ realistically) while coding the ENVELOPE groups — G value, dddVddd arc, VRB
classification — from the authored layer values (a finite sample only under-estimates a bounded envelope; cross-derived
amplitudes drive physics but never fabricate a reported group). FMH-1 coding: spread ≥180° → VRB at any speed; ≥60° → VRB at
≤6 kt, dddVddd above; calm <1 kt; PK WND remark (after AO1/AO2) when the 10-min peak exceeds 25 kt, never undercutting the
G group. `SpeciCriteria` wind checks are double-gated: vs the last issued report (hysteresis) AND vs a recent observation
(wind shift ≥45° within 15 min at ≥10 kt; squall +16 kt within ~2 min to ≥22 kt); VRB endpoints never shift.

Pilots fly the wind too: `AircraftPerformance.WindApproachAdditive` (Vapp = Vref + ½ steady headwind + full gust increment,
cap 20 kt — Boeing FCTM/Airbus FCOM) feeds `FinalApproachPhase`'s speed targets, and the gust-only part
(`GustApproachAdditive`) is maintained to touchdown on `LandingPhase.Vref` and the follow-overtake floor. On the ground,
`AircraftState.IndicatedAirspeed` carries WHEEL speed; `GroundFrame` owns the liftoff/touchdown conversions (rotation at Vr
indicated — headwind shortens the roll by the v² law; touchdown wheel speed = TAS − headwind). See
[flight-physics.md](flight-physics.md).

Calibration acceptance tests (`WindCalibrationTests`) pin the constants — tune constants, never tests.

## The three interpolation axes — keep them straight

Three distinct algorithms interpolate weather along three different axes. Only **altitude** and **time** use N/E vector
decomposition for direction; the **spatial** axis does not interpolate direction at all.

### (a) Altitude, within one profile — `WindInterpolator.GetWindAt`

`GetWindAt(profile, altitudeFt)` (`WindInterpolator.cs:34`) returns a `WindAtAltitude(DirectionDeg, SpeedKts)`:

- Null/empty profile → zero wind.
- Below the lowest layer or above the highest → **clamped** to the nearest layer (no extrapolation).
- Between layers: linear `t`, then **direction is decomposed into unit N/E components and lerped**, so the `350°↔010°` wraparound is handled correctly (`WindInterpolator.cs:63-72`). Speed lerps linearly.
- `DirectionDeg` inherits the layer convention: **wind FROM, MAGNETIC** (the layers are magnetic).

### (b) Spatial, across stations — `MetarInterpolator.GetWeatherForAirport`

`MetarInterpolator.GetWeatherForAirport(metars, airportId)` (`MetarInterpolator.cs:13`):

- **Exact station match first** (`MetarParser.FindStation`) — returned verbatim if found.
- Otherwise resolves the airport position via `NavigationDatabase.Instance.GetFixPosition`, gathers every station within
  **50 nm** (`MaxInterpolationRangeNm`), stripping a leading `K` for FAA fix lookup when needed.
- One nearby station → used directly. Multiple → `Interpolate`:
  - **Ceiling/layers: take the station with the lowest `BKN`/`OVC` ceiling and use its full layer set** (conservative; preserves multi-layer fidelity). If no station reports a ceiling, union all layers and dedupe.
  - **Visibility: inverse-distance-weighted average** (weight `1/max(dist, 0.1)`).
- This axis produces a single `ParsedMetar`; it never lerps wind *direction*.

### (c) Time, along the timeline — `WeatherTimeline.GetWeatherAt`

Inside a transition window (`WeatherTimeline.cs:70-87`), with `t` = fraction through the window:

- **Wind layers** (`InterpolateWindLayers`): if the two periods' layer **counts differ, snap to the target list** (`WeatherTimeline.cs:147-151`); otherwise per-layer speed lerp + N/E-vector direction lerp, matching the altitude axis. Gusts lerp only if both have them.
- **METARs** do not lerp as text — they become `ParsedMetarOverrides` (`InterpolateMetars`): per matching station, interpolate cloud layers (cover step-changes at `t=0.5`), derive a fresh ceiling via `CeilingFromLayers`, and IDW-free linear-lerp visibility and altimeter.
- The transition window is **truncated** if the next period starts before it ends.
- Outside any transition (first period, `TransitionMinutes ≤ 0`, or past the window end), `BuildProfile` returns the active period directly with no overrides.

## ISA atmosphere, airspeed, and wind-correction math

`WindInterpolator` carries the ISA standard-atmosphere model used for airspeed conversion:

- **`GetAtmosphere(altitudeFt)`** (`WindInterpolator.cs:164`) returns ISA temperature (K) and pressure ratio δ = P/P₀, splitting at the tropopause (`TropopauseM = 11,000 m`, i.e. ~36,089 ft): troposphere uses the standard lapse rate, stratosphere is isothermal.
- **`IasToTas` / `TasToIas` / `MachToIas` / `IasToMach`** are full ISA compressible-flow conversions (CAS↔Mach↔TAS via isentropic impact-pressure relations). **The equations, the airspeed-frame model (IAS is the single source of truth, TAS/GS/Mach derive), and the per-category constants are documented in [flight-physics.md](flight-physics.md)** — do not duplicate them here. `SpeedOfSoundKts(altitudeFt)` is the ISA speed of sound for the same model.
- **`ComputeWindCorrectionAngle(desiredTrack, tas, windFrom, windSpeed)`** (`WindInterpolator.cs:184`) returns the crab angle (WCA) so the aircraft tracks a straight ground path: positive = crab right. It reverses **wind FROM → blows TOWARD** by `+180°` internally, then projects the cross-track wind component. Returns 0 if TAS or wind speed is zero.

`GetWindComponents(profile, altitudeFt)` (`WindInterpolator.cs:86`) is the other consumer-facing helper: it returns the wind as
`(NorthKts, EastKts)` in the direction the wind **blows toward** (FROM `+180°`), ready to add to the TAS vector.

## Magnetic declination (`MagneticDeclination.cs`)

`MagneticDeclination` is the NOAA **World Magnetic Model (WMM)** — degree-12 spherical-harmonic evaluation via the `Geo` library,
**globally accurate, not a CONUS longitude approximation** (`MagneticDeclination.cs:6-15`).

- Sign convention: **positive = east declination**. `true → magnetic = true − declination`; `magnetic → true = magnetic + declination`.
- API: `GetDeclination(lat, lon)` / `GetDeclination(LatLon)`, `TrueToMagnetic(deg, …)`, `MagneticToTrue(deg, …)`. The convenience overloads forward to the `(lat, lon)` forms.
- **The WMM epoch is resolved once at startup** as a static field (`EpochDate`, `MagneticDeclination.cs:20-34`): the model covering "now" is stable for the process lifetime, so per-tick LINQ scans of the embedded models are avoided. If the process runs past the newest bundled epoch (a stale package), it clamps to the last valid day rather than throwing.
- `TryCalculate` returning null → declination `0.0` (safe fallback).
- **Process-wide grid cache** (`GridCache`, 0.02° cells): `GetDeclination` evaluates the WMM once per cell, **at the cell centre**, and every later lookup in that cell returns the same value. Each `Geo` evaluation allocates ~100 KB of working arrays, and aircraft in a pattern or on the ground keep re-entering the same handful of cells, so this removed most of the per-tick declination cost in replay profiles. The cell-centre rule keeps the value a pure function of the cell — independent of which aircraft (or thread) got there first — so replays stay reproducible; the error bound (~0.01° within a cell) is the same one the per-aircraft box below already accepts. The cache is cleared when it exceeds 200k cells.

### The per-aircraft declination box-cache (`FlightPhysics.cs:50-85`)

`FlightPhysics.Update` recomputes declination only when the aircraft has moved outside a small box:

- Two `AircraftState` fields back this (`AircraftState.cs:54-64`):
  - `Declination` — the cached value, **serialized** (round-trips in snapshots).
  - `DeclinationCachePosition` (`LatLon?`) — **`[JsonIgnore]`, runtime-only**; `null` = "not cached yet".
- **Box threshold = `0.02°`** (≈ 1.2 nm of latitude). Inside the box the cached `Declination` is reused, because the WMM is ~0.06 ms/call and at 4 Hz × N aircraft it would dominate the tick.
- **Out-of-range / non-finite-position guard**: if `Position` is NaN/∞ or `|lat| > 90` / `|lon| > 180`, the update is skipped and logged (`Geo.Coordinate`'s ctor would otherwise throw and crash the tick), keeping the previously cached value.

`AircraftState.MagneticHeading` / `MagneticTrack` derive from `TrueHeading`/`TrueTrack` and this cached `Declination`
(`AircraftState.cs:66-70`).

## Consumption and broadcast

### In `FlightPhysics` (per tick)

Two physics steps consume weather (`FlightPhysics.Update` passes `weather` through):

- **`UpdateNavigation`** (`FlightPhysics.cs:224-233`): for airborne aircraft with IAS > 0, computes WCA from `IasToTas` + `GetWindAt` and sets `TargetTrueHeading = bearing + wca`, so the aircraft holds a straight ground track instead of a pursuit curve.
- **`UpdatePosition`** airborne branch (`FlightPhysics.cs:1034-1058`): `TAS = IasToTas(IAS, alt)`; ground-speed vector = `TAS·(cos/sin heading) + GetWindComponents(...)`; the wind is **cached into `AircraftState.WindComponents`** so `GroundSpeed` can be recomputed on read without weather context. The **ground branch** (`FlightPhysics.cs:1022-1031`) ignores wind entirely — groundspeed is derived from IAS and track follows heading.

See [tick-loop.md](tick-loop.md) for where these steps sit in the 8-step `FlightPhysics.Update` order and the PrePhysics→Physics×4→PostPhysics frame.

### Selection and broadcast cadence

- **Replay / scenario engine** (`SimulationEngine.cs`): the timeline is collapsed at restore/replay sites (`World.Weather = timeline.GetWeatherAt(t)`), and `ApplyWeatherJson` (`SimulationEngine.cs:2562`) routes a `LoadWeather` JSON through `WeatherTimelineParser` — a timeline is stored on `Scenario.WeatherTimeline` and immediately collapsed; a v1 profile clears the timeline and sets `World.Weather` directly. Snapshots serialize `World.Weather` as JSON (`SimulationEngine.cs:135`, `:148`) plus `Scenario.WeatherSourceJson`; `RestoreFromSnapshot` rebuilds `Scenario.WeatherTimeline` from that source so v2 evolution survives a snapshot-based rewind (the parsed timeline object itself is not snapshotted).
- **Live server tick** (`RoomTickLoopService.cs`): each second, if a timeline is active, `GetWeatherAt(elapsed)` is recomputed into `World.Weather` (gated by `HasMeaningfulChange`) to drive physics/visual acquisition — this no longer broadcasts. Broadcasts are instead driven by the reported-METAR issuer (see below).
- **`HasMeaningfulChange(a, b)`** (`WeatherTimeline.cs:94`) gates the continuous `World.Weather` update: true if precipitation changed, METAR list changed, layer count changed, or **any layer's direction differs by > 1°, speed by > 0.5 kt, gusts by > 0.5 kt, variability spread by > 1°, or the VRB flag flipped**.

### Reported METAR reconstruction (`MetarIssuer`, `MetarComposer`, `SpeciCriteria`)

The METAR *string* broadcast to clients (`WeatherChangedDto.Metars`) is reconstructed from the continuous weather and re-issued like a real observation, rather than echoing the static loaded text:

- **Routine** METAR for every station once per hour at **:53Z** (`MetarIssuer.RoutineObservationMinute`); **SPECI** when a station's conditions cross a threshold since its last issued report (`SpeciCriteria`: wind shift ≥45°/≥10 kt, visibility crossing 3/2/1/½/¼ SM, ceiling crossing 3000/1500/1000/500 ft, precip onset/cessation — a basic subset of AIM TBL 7-1-1).
- The observation clock is **real-world UTC at load + elapsed sim time** (so pause/SimRate behave); conditions are **sampled and frozen at issuance** so the report holds steady between issues (realistic observation lag).
- Surface wind comes from the **actual physics surface wind** (lowest layer, magnetic) converted to **true** via `MagneticToTrue` — METARs are true; physics stays magnetic. Encoding follows AIM 7-1-28 (10SM cap, `CLR` for automated clear, altimeter truncated, clouds >12,000 ft omitted).
- **Display-only**: physics, visual acquisition, and all operational logic keep reading the continuous `World.Weather` — never the reported strings. Only the broadcast DTO carries them.
- Enabled per load via the `reconstructMetars` flag on `LoadWeather` — **true** for file/API weather, **false** for live-fetched weather (left untouched). The *intent* persists separately from the live issuer: `SimScenarioState.MetarReissuanceEnabled` (serialized in the snapshot DTO and the recording manifest) plus `WeatherSourceJson` (the last-applied weather JSON). The live `MetarIssuer` is runtime-only and torn down on every replay/rewind/recording-load (which keeps replay deterministic, since it anchors to `DateTime.UtcNow`); the server tick loop rebuilds it on return to live via `RoomEngine.EnsureLiveMetarIssuer` **only** when the persisted intent says the weather was dynamic. `LoadWeather` leaves the intent and issuer untouched while reconstructing (gated on `Room.IsBroadcastSuppressed`/`IsPlaybackMode`); the replayed `RecordedWeatherChange.ReconstructMetars`, the restored snapshot, or the recording manifest supplies it instead. The temp replay engine (report generation) stays off.
- **Known limitations**: a precip-onset/cessation SPECI does not rewrite the present-weather group (the modeled precip is generic Rain/Snow, not a METAR token), so the body keeps the base wx group; a `VV` (indefinite-ceiling) base is re-reported as `OVC` (the parser models VV as a synthetic OVC layer).

## Live weather (client)

`ArtccAirportResolver.GetAirportIdsAsync(artccId)` supplies `airportIds` first, from the vNAS data-api ARTCC config (disk-cached, 6h TTL).
`ExtractUnderlyingAirports` **recurses the `facility` / `childFacilities` tree**, collecting
`starsConfiguration.areas[].underlyingAirports[]` from every STARS facility and deduplicating — a STARS config belongs to a facility
(each TRACON has its own), never to the document root. IDs are FAA-style (`OAK`); `BuildLiveWeatherAsync` prefixes three-letter IDs
with `K` before querying (`LiveWeatherService.cs:33`). An empty list short-circuits the command with "No airports found for ARTCC".

`LiveWeatherService.BuildLiveWeatherAsync(artccId, airportIds)` (`LiveWeatherService.cs:24`) fetches from `aviationweather.gov` and
assembles one `WeatherProfile`:

- METARs (`/api/data/metar`) and FD winds (`/api/data/windtemp`) are fetched in parallel; returns `null` only if **both** fail.
- **FD wind layers** (`BuildWindLayersFromFd`): for each altitude level, vector-average the non-light-variable station reports, then **convert TRUE → MAGNETIC** via `MagneticDeclination.TrueToMagnetic` using the ARTCC center as a proxy position (`LiveWeatherService.cs:190`).
- **Surface wind from METAR** (`BuildSurfaceWindLayer`): vector-averaged METAR winds inserted as an `Altitude = 0` layer, converted **TRUE → MAGNETIC** via the ARTCC-center declination like the FD layers (METAR text winds are true per AIM 7-1-28; only ATIS/ASOS voice is magnetic). VRB stations contribute speed only (all-VRB → a `Variable` layer); gusts and dddVddd spreads are mined from each station's raw text.
- `GetArtccCenter(artccId)` (`LiveWeatherService.cs:253`) is a hand-maintained ARTCC → primary-airport table used purely as a declination proxy; unknown ARTCC falls back to mid-CONUS `(39.0, −98.0)`.
- `FdRegionMapping.GetRegion(artccId)` (`FdRegionMapping.cs`) is a separate hand-maintained ARTCC → FD-region table (`bos`/`mia`/`chi`/`dfw`/`slc`/`sfo`/`alaska`); an unmapped ARTCC disables the FD fetch and the profile has no winds-aloft layers.

## Direction & units convention cheat-sheet

The single most error-prone area. Memorize this before touching any wind value.

| Quantity | FROM or TOWARD | True or Magnetic | Altitude ref | Where |
|---|---|---|---|---|
| `WindLayer.Direction` | FROM | **Magnetic** | feet **MSL** | `WeatherProfile.cs:14` |
| `WindAtAltitude.DirectionDeg` (`GetWindAt`) | FROM | Magnetic (inherits layer) | — | `WindInterpolator.cs:34` |
| `WindAtLevel.DirectionTrue` (FD parser) | FROM | **True** | feet (level) | `WindsAloftParser.cs:4` |
| METAR text wind | FROM | **True** (AIM 7-1-28; converted to magnetic when building the surface layer) | surface | `LiveWeatherService.BuildSurfaceWindLayer` |
| FD-derived layer (after `BuildWindLayersFromFd`) | FROM | Magnetic (converted via ARTCC-center declination) | feet MSL | `LiveWeatherService.cs:190` |
| `GetWindComponents` output | **TOWARD** (FROM `+180°`) | matches input | — | `WindInterpolator.cs:86` |
| Cloud layer base (`CloudLayer.BaseFeetAgl`) | — | — | feet **AGL** (hundreds in METAR) | `MetarParser.cs:15` |

A profile layer's direction is **magnetic**; a steering `bearing` from `GeoMath.BearingTo` is **true**. `ComputeWindCorrectionAngle`
is fed the true bearing alongside the magnetic wind direction (`FlightPhysics.cs:228-230`) — the WCA it produces therefore carries
the local declination into the crab. This is the current behavior; do not "fix" the mismatch without understanding that the wind
profile is authored in magnetic by convention.

## Test map

The behaviors above already have regression coverage — find the right harness before adding a new test:

| Test file | Pins |
|---|---|
| `tests/Yaat.Sim.Tests/MetarParserTests.cs` | station-id heuristic, visibility forms, FEW/SCT/BKN/OVC + VV synthetic-OVC, ceiling = lowest BKN/OVC, wind/altimeter. |
| `tests/Yaat.Sim.Tests/WindsAloftParserTests.cs` | FD header-column matching, DDSS decode (≥100 kt, 9900 light-variable, temp-suffix strip). |
| `tests/Yaat.Sim.Tests/WindInterpolatorTests.cs` | altitude N/E lerp, clamp outside range, IAS/TAS/Mach conversions, WCA. |
| `tests/Yaat.Sim.Tests/MetarInterpolatorTests.cs` | exact-station match, 50 nm IDW visibility, lowest-ceiling layer set. |
| `tests/Yaat.Sim.Tests/WeatherTimelineTests.cs` | time interpolation, layer-count snap, `HasMeaningfulChange`. |
| `tests/Yaat.Sim.Tests/WeatherTimelineParserTests.cs` | v1/v2 auto-detect, v2 validation/sort, error cases. |
| `tests/Yaat.Sim.Tests/WindPhysicsTests.cs` | end-to-end crab + groundspeed-vector behavior under wind. |
| `tests/Yaat.Sim.Tests/MagneticDeclinationTests.cs` | WMM declination values, true↔magnetic round-trips. |
| `tests/Yaat.Client.Tests/LiveWeatherServiceTests.cs`, `LiveWeatherRealDataTests.cs` | live fetch assembly, FD→magnetic conversion, surface-wind layer. |
| `tests/Yaat.Client.Tests/ArtccAirportResolverTests.cs` | underlying-airport extraction from the real ZOA data-api config (facility-tree recursion, dedup). |

## Footguns

- **Direction conventions are never co-located and differ by source.** `WindLayer.Direction` is FROM/magnetic/MSL; `WindAtLevel.DirectionTrue` (FD) is FROM/**true**; raw METAR winds are already magnetic (`LiveWeatherService.cs:243` adds no conversion); FD winds get `TrueToMagnetic`'d via ARTCC-center declination (`LiveWeatherService.cs:190`). `GetWindComponents`/`ComputeWindCorrectionAngle` reverse FROM→TOWARD by `+180°`. Mixing any two silently yields ~15-20° errors. See the [cheat-sheet](#direction--units-convention-cheat-sheet).
- **Three interpolation algorithms, three different rules.** Altitude (`GetWindAt`) and time (`InterpolateWindLayers`) use N/E vector lerp; spatial (`MetarInterpolator`) does **not** lerp direction at all (lowest-ceiling layer set + IDW visibility). In the time axis, **cloud cover step-changes discretely at `t=0.5`** and a **layer-count mismatch snaps to the target list** instead of interpolating.
- **`WindLayers` re-sorts on assignment.** Setting `WeatherProfile.WindLayers` re-orders ascending by altitude (`WeatherProfile.cs:46`); insertion order is not preserved. The `GetWindAt` clamp/scan assumes this sort.
- **FD `DecodeWind` overloads the code.** `DD ≥ 50` means direction `(DD−50)×10` and speed `SS+100` (winds ≥ 100 kt); `9900` is light-and-variable; a temperature suffix (`2714-08`) must be stripped first. Column assignment is by **nearest header-center distance**, not fixed offsets — fragile to header spacing.
- **`VV` is a synthetic OVC layer.** `MetarParser` injects vertical-visibility / indefinite-ceiling as an `Overcast` `CloudLayer` (`MetarParser.cs:245`) so multi-layer obstruction logic treats it uniformly. It is not an OVC token in the raw METAR — don't expect it to round-trip back to `VV`.
- **`DeclinationCachePosition` is `[JsonIgnore]` runtime-only** (`AircraftState.cs:63-64`). After a snapshot/DTO round-trip it is `null` and the WMM re-evaluates on the first tick. Looping `ReplayFromStartTo(t)` — which resets to `t=0` every call — trips declination cache-mismatch assertions. This is the same footgun documented in [snapshots-and-replay.md](snapshots-and-replay.md); use `FastForwardTo(target, …)` to advance from current state, never loop `ReplayFromStartTo`. The `Declination` value itself **is** serialized, so a snapshot restore keeps the last cached declination until the next box-exit recompute.
- **`ParsedMetarOverrides` is `[JsonIgnore]`** (`WeatherProfile.cs:59`) and only populated by `WeatherTimeline.GetWeatherAt` during a transition window; `GetWeatherForAirport` checks it before its own cache (`WeatherProfile.cs:68`). After a snapshot round-trip mid-transition the overrides are gone and ceilings/visibility revert to parsing the raw METAR text — a subtle replay divergence to watch for if interpolated weather "jumps" on restore.
- **The WMM epoch is fixed at process start** (`MagneticDeclination.cs:20`). A long-lived process whose date passes the newest bundled epoch clamps to the last valid day; declination will not track the calendar past that point.
- **`GetArtccCenter` and `FdRegionMapping` are hand-maintained tables** (`LiveWeatherService.cs:253`, `FdRegionMapping.cs:9`). An ARTCC missing from `FdRegionMapping` silently disables FD winds; one missing from `GetArtccCenter` falls back to mid-CONUS `(39, −98)` for the declination proxy, skewing the TRUE→MAGNETIC conversion of FD winds.
- **The mean/instantaneous split is load-bearing.** `WindLayer.Direction/Speed` stay the authored MEAN; the instantaneous wind exists only at the `GetWindAt` output. Reporting (`MetarIssuer`), SPECI decisions, runway selection, crosswind limits, and any approach-stability gate must read the mean (or the observed 2-minute mean via `WindObservation`) — never the instantaneous value, which wobbles by design.
- **Weather lives in two places, and a scenario reload only rebuilds one.** `TrainingRoom.Weather` / `TrainingRoom.WeatherSourceJson` are room-scoped and survive anything; `World.Weather`, `SimScenarioState.WeatherTimeline`, and `MetarIssuer` belong to the engine, which a restart or rewind throws away. Rewind repairs itself (snapshot restore + replayed `RecordedWeatherChange`), but a **restart** has an empty tape and a **scenario load** starts one, so `RecordingManager.RestartScenarioAsync` and `ScenarioLifecycleService.LoadScenarioAsync` each explicitly re-run `RoomEngine.LoadWeather(Room.WeatherSourceJson, Room.SessionSettings.MetarReissuanceEnabled)` once the new engine is in place. Without it the room reports loaded weather to clients while the simulation flies calm — the shape of issue #313's session-setting loss. Anything else that rebuilds the engine needs the same call.
