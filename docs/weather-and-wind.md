# Weather Parsing, Wind Interpolation, and Magnetic Declination

> Read this before touching `WeatherProfile`, `WeatherTimeline`, `MetarParser`, `WindsAloftParser`, `WindInterpolator`,
> `MetarInterpolator`, `MagneticDeclination`, `LiveWeatherService`, `Grib2Reader`, `GriddedWindField`, or the wind/declination consumers in `FlightPhysics`.
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
                                                           + GfsGridFetcher (NOMADS) → UploadWindGrid
                                                             → profile.WindGridHash
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
| `WindGridHash` (`string?`) | **Serialized** (`windGridHash`, left out when null). The SHA-256 of the GFS grid's raw GRIB2 bytes, lowercase hex: the name the profile's grid is registered, recorded and restored under. See [Wind grids: hash, registry and pins](#wind-grids-hash-registry-and-pins). |
| `WindGrid` (`GriddedWindField?`) | **`[JsonIgnore]`**. The GFS grid over the room; where it covers a position, the position-taking `WindInterpolator` overloads take the winds above layer 0 from it. Set only by `WindGridRegistry.Attach` from `WindGridHash`; see [Sampling the grid at a position](#sampling-the-grid-at-a-position). |
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

`WeatherProfile.SurfaceAltimeterInHg()` is the altimeter setting of the first METAR that reports one (else `StandardAltimeterInHg`, 29.92), cached per profile. With `SurfaceElevationFt()` it gives the surface pressure altitude that floors a GFS column.

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

`GetWindAt` (`WindInterpolator.GetWindAt`) returns a `WindAtAltitude(DirectionDeg, SpeedKts)`; this is the layer path every no-position call takes:

- Null/empty profile → zero wind.
- Below the lowest layer or above the highest → **clamped** to the nearest layer (no extrapolation).
- Between layers: linear `t`, then **direction is decomposed into unit N/E components and lerped**, so the `350°↔010°` wraparound is handled correctly (`InterpolateMean`). Speed lerps linearly.
- `DirectionDeg` inherits the layer convention: **wind FROM, MAGNETIC** (the layers are magnetic).
- `GetMeanWindAt(profile, altitudeFt)` is the same interpolation with no `WindVariation` (gusts and direction swing) applied; the two share the private `Bracket`/`InterpolateMean` helpers. ERAM `UR` reads it, converting the direction to true at the requested location, when the profile has no grid there (`GetGriddedMeanWindTrueAt` otherwise).
- The overloads that take a `WindSamplePoint` replace the layer interpolation with a grid column where the profile has a `WindGrid` covering the point; see [Sampling the grid at a position](#sampling-the-grid-at-a-position).

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

- **`GetAtmosphere(altitudeFt)`** (`WindInterpolator.cs:264`) returns ISA temperature (K) and pressure ratio δ = P/P₀, splitting at the tropopause (`TropopauseM = 11,000 m`, i.e. ~36,089 ft): troposphere uses the standard lapse rate, stratosphere is isothermal.
- **`IasToTas` / `TasToIas` / `MachToIas` / `IasToMach`** are full ISA compressible-flow conversions (CAS↔Mach↔TAS via isentropic impact-pressure relations). **The equations, the airspeed-frame model (IAS is the single source of truth, TAS/GS/Mach derive), and the per-category constants are documented in [flight-physics.md](flight-physics.md)** — do not duplicate them here. `SpeedOfSoundKts(altitudeFt)` is the ISA speed of sound for the same model.
- **`PressureToIsaAltitudeFt(pressureHpa)`** is the inverse of the pressure ratio `GetAtmosphere` gives: the ISA pressure altitude (ft) of a static pressure, standard lapse rate up to the tropopause and isothermal above (500 hPa ≈ 18,289 ft). Zero, negative or NaN pressure throws `ArgumentOutOfRangeException`. `GriddedWindField` uses it to place each isobaric level.
- **`ComputeWindCorrectionAngle(desiredTrack, tas, windFrom, windSpeed)`** (`WindInterpolator.cs:319`) returns the crab angle (WCA) so the aircraft tracks a straight ground path: positive = crab right. It reverses **wind FROM → blows TOWARD** by `+180°` internally, then projects the cross-track wind component. Returns 0 if TAS or wind speed is zero.

`GetWindComponents` (`WindInterpolator.GetWindComponents`, with and without a `WindSamplePoint`) is the other consumer-facing helper: it returns the wind as
`(NorthKts, EastKts)` in the direction the wind **blows toward** (FROM `+180°`), ready to add to the TAS vector.

## GRIB2 reader and gridded wind field (`Weather/`)

`Grib2Reader.Read(bytes)` and `GriddedWindField` decode a NOMADS GFS lat/lon regional subset (isobaric levels; UGRD, VGRD and TMP) into a sampleable wind column.

A `WeatherProfile` carries one as `WindGrid` (`[JsonIgnore]`, resolved from the serialized `WindGridHash`; a profile rebuilt from a weather timeline has none), and the position-taking `WindInterpolator` overloads sample it (see [Sampling the grid at a position](#sampling-the-grid-at-a-position)). A profile with no grid runs the layer path.

- **Reader subset** (`Grib2Reader`): edition 2, grid template 3.0 (regular lat/lon), product template 4.0 with the forecast time in hours, data representation template 5.0 (simple packing), section 6 bitmap present (indicator 0) or absent (255). Anything else throws `Grib2FormatException` naming the section and the template or value found.

  So does an empty, truncated, or otherwise malformed file (missing `GRIB`/`7777` markers, section lengths that overrun the message, increments that disagree with the corner-to-corner span, a bad reference date). So does a section 5 whose reference value is not finite, or whose binary scale factor is outside ±127 or decimal scale factor outside ±38 (float32's exponent limits, which keep the unpacking arithmetic finite), and a section 7 value that computes to a non-finite float.
- **Size caps**: `Grib2Reader.MaxGridPoints` (160,000) bounds the points one message's grid may declare, checked before any value array is allocated (a file with 0 bits per value declares any count in a few bytes); `Grib2Reader.MaxDecodedPoints` (9,600,000 = 3 variables × 20 levels × `MaxGridPoints`) bounds the points all of a file's fields declare. A section 7 not preceded by a new section 4 (a message repeats sections 2–7, 3–7 or 4–7, never a bare 7) is refused. Each throws `Grib2FormatException`.
- **Scanning modes**: only the 0x80 (columns run west) and 0x40 (rows run north) bits are accepted, and all four combinations are reordered. Every `Grib2Message.Values` array is row-major from the south-west corner (`index = j·Ni + i`, rows south to north, columns west to east) whatever the file's mode; a point the bitmap marks missing is `NaN`. Grid longitudes stay in the file's 0–360 convention.
- **Levels**: `Grib2Message.FirstSurfaceValue` is pascals for an isobaric surface (type 100). `GriddedWindField.FromMessages` ignores any message that is not UGRD (0, 2, 2), VGRD (0, 2, 3) or TMP (0, 0, 0) on type 100, groups the rest by whole-pascal pressure, and places each level at its ISA pressure altitude via `WindInterpolator.PressureToIsaAltitudeFt` (feet MSL; a standard-atmosphere height, not geopotential height from the model). Levels come out lowest altitude (highest pressure) first.
- **Sampling**: `SampleColumn(lat, lon)` accepts longitude as −180..180 or 0..360, bilinearly interpolates U, V and T between the four surrounding grid points, then converts each level: speed from the U/V magnitude (m/s → kt), temperature kelvin → Celsius, and direction as the meteorological **FROM** bearing in degrees **TRUE**, `atan2(−U, −V)` normalised to [0, 360), with a calm reported as 0. Direction is derived after the U/V interpolation, never interpolated itself, so the 350°↔010° wrap cannot appear.
- **Refusals**: `FromMessages` throws `InvalidOperationException` (naming the parameter and level) for a grid smaller than 2 × 2, messages that disagree on grid, reference time or forecast hour, a value count that is not Ni × Nj, a level that rounds to 0 Pa or below, a duplicated parameter at a level, or a parameter missing at a level the others have.

  It also throws for a decoded value outside its range: a wind component past ±150 m/s or a temperature outside 160–350 K (for example `UGRD at 500 hPa has value 151 m/s, outside ±150 m/s`; the server wraps it as `The wind grid could not be decoded: …`).

  The bounds bracket every real GFS grid and only catch a damaged one: the strongest jets run about 100–125 m/s, 150 hPa polar winter lows about 183–190 K, and 1000 hPa extrapolated over high terrain about 325–335 K. A bitmap-missing `NaN` is not a range violation and is skipped. `SampleColumn` returns `null` outside the grid, and drops a level whose four corners include a `NaN` in U, V or T (the column is then shorter, possibly empty).
- **Direction is TRUE**: unlike `WindLayer.Direction` (magnetic), a `GriddedWindSample` is true, so a consumer feeding the magnetic-based physics must convert it with `MagneticDeclination.TrueToMagnetic`. See the [cheat-sheet](#direction--units-convention-cheat-sheet).

### Wind grids: hash, registry and pins

A grid is recorded once, by content, not once per place the weather text appears. The weather text every path re-parses (the room's weather JSON, a recorded weather change, a snapshot's `WeatherJson`, an archive's `weather.json`) carries only `WindGridHash`; the bytes live in `WindGridRegistry` and in the recording archive.

- **`WindGridRegistry`** (`Weather/WindGridRegistry.cs`) is the process-wide, thread-safe store, keyed by `HashOf(grib2)` (SHA-256, lowercase hex). `Register(bytes)` decodes the file (`Grib2Reader` then `GriddedWindField.FromMessages`; a bad file throws `Grib2FormatException` or `InvalidOperationException`) and keeps the bytes and the field; registering held bytes stores nothing new. `TryGetField` and `TryGetBytes` look an entry up.
- **Bounded**: at most `MaxUnpinned` (8) unpinned entries are kept, the least recently used evicted first. A pinned entry is never evicted. `ResetForTests` empties the registry (a pin handle taken before it releases nothing afterwards).
- **Pins** count: `Pin(hash)` / `TryPin` return an `IDisposable` and the entry is evictable again once every handle is disposed. `RegisterAndPin` inserts and pins in one step so no other registration can evict the grid in between.
- **`WindGridRegistry.Attach(profile)`** sets `profile.WindGrid` from `WindGridHash`. It runs where weather text becomes a profile: `WeatherTimelineParser.ParseV1` and the snapshot restore in `SimulationEngine.Snapshots.cs` (the restore does not re-parse a v1 source). A hash with no registered grid logs a warning and leaves the profile on the FD wind layers. A timeline is never given a grid.
- **Live weather only**: `Attach` ignores (with a warning) a hash on a profile that is not shaped like live weather (`IsLiveWeather`: it has METARs and its lowest wind layer is the METAR surface layer at 0 ft MSL that the grid column sits above).
- **Pins are owned by the Sim or by the server's room**: `SimScenarioState.WindGridPins` holds a Sim-hosted scenario's pins (a replay, the soak and headless runs), replaced through `ReplaceWindGridPins` and released by the engine's next `LoadScenario` and by a host that unloads. `WindGridPins` (`Weather/WindGridPins.cs`) is one owner's pins on a set of hashes; `Repin()` takes a second, independent set on the same grids. yaat-server never sets `SimScenarioState.WindGridPins`: its `TrainingRoom` is the one holder (below).
- **Server rooms**: a member sends a grid with the hub method `UploadWindGrid(byte[]? grib2)` (see [training-hub-contract.md](training-hub-contract.md)), which returns the grid's hash. The server decodes and hashes the bytes itself (`WindGridRegistry.RegisterAndPin`), refuses more than `WindGridRegistry.MaxGridBytes` (512 KB, the one size cap every path shares), refuses a grid the room does not pin yet once the room pins `TrainingRoom.MaxRoomWindGrids` (= `WindGridRegistry.MaxRoomGrids`, 16) besides its pending upload (`TrainingRoom.WindGridRefusal`), and keeps the upload as the room's `PendingWindGridHash` (`SetPendingWindGrid`, which returns the refusal; a later upload replaces it; a weather load that names any grid consumes it, `NoteWeatherLoaded`). A hub `LoadWeather` whose weather names a grid the room would refuse returns `CommandResultDto(false, msg)` with the same refusal and loads nothing. The weather JSON then names the hash as `windGridHash`.
- **Room pins**: `TrainingRoom.PinnedWindGridHashes` is every hash the room's current weather, its scenario tape (initial weather and recorded weather changes), the archive manifest's `WindGridHashes`, the loaded snapshots and the pending upload name (`TrainingRoom.WindGridHashesNamed` gathers all but the pending upload). `RebuildWindGridPins` pins the new set before it releases the old, and runs after every weather change and every load, unload, restart, rewind and checkpoint restore; `ReleaseWindGridPins` runs at room removal and leaves every later rebuild pinning nothing. A recording-archive load registers and pins its grids (`RecordingArchive.RegisterWindGrids`) inside the gated commit, holds them until the room's pins include them, and releases them if the load fails.
- **Recording**: the archive stores each referenced grid's raw GRIB2 once as `wind-grids/{hash}.grib2.br` and lists the hashes in the manifest `WindGridHashes` (see [snapshots-and-replay.md](snapshots-and-replay.md)). `WindGridHashes.Referenced` collects the hashes from the initial weather, every `RecordedWeatherChange` and every snapshot's `WeatherJson`. A live server export and the soak sink bundle the grids through the public `RecordingArchiveWriter.WriteReferencedWindGrids(writer, initialWeatherJson, actions, snapshotWeatherJsons)`; where the snapshots are not at hand (they stream and are not kept) they pass none, since every snapshot's weather is the initial weather or a recorded change.
- **Reading**: `RecordingArchive.RegisterWindGrids` (over the shared `RegisterWindGridEntries`) verifies each entry hashes to its name, registers and pins it, and runs before any loader parses the weather text, so a recording that changes weather mid-run replays each change on its grid every time. An entry that is missing, damaged (not Brotli) or does not decode is skipped with a warning and its weather flies the FD layers; a read that fails after registering grids releases the pins it took.

  A recording is refused whole, with a `WindGridLimitException`, when its manifest lists more than `WindGridRegistry.MaxRoomGrids` (16) distinct hashes (duplicates count once) or an entry decompresses past `WindGridRegistry.MaxGridBytes` (512 KB). Sizes are enforced while decompressing (`WindGridRegistry.ReadBrotliWithinLimit` reads at most the cap plus one byte), never from the size the archive declares, and a size refusal partway through releases the pins already taken.

  The server runs `RecordingArchive.WindGridLimitRefusal` first (`RoomEngine.LoadRecordingArchiveGuardedAsync`), before the room is cleared, so the refusal reaches the user as a `CommandResultDto(false, msg)` and the room keeps its scenario. A checkpoint is never refused: it keeps the first 17 distinct grids and skips the rest and any oversized entry with a warning. The limit policy is the callers': the shared helpers only report an oversize entry (`WindGridEntryProblem.TooLarge`).

  The pins ride on `SessionRecording.WindGridPins`; `ReplayDriver` takes its own (`Repin`) before the scenario load and hands them to the scenario state. The server's recording-archive load (`RecordingManager`) calls `RegisterWindGrids` itself and its room takes the pins instead (Room pins above); loading a `SessionRecording` that holds pins, it takes its own `Repin` until the room's pins include the grids.
- **Shared helpers**: the archive and the checkpoint read and write grid entries through the same Sim code: `RecordingArchive.ReadWindGridEntry` (one entry, as a `WindGridEntryRead` whose `WindGridEntryProblem` is `Missing`, `TooLarge` or `WrongHash`), `RecordingArchive.RegisterWindGridEntries` (register and pin a list of entries) and `RecordingArchiveWriter.WriteWindGridEntry`.
- **Room checkpoints**: a planned-restart checkpoint stores each grid the room's weather and tape name (`TrainingRoom.CommittedWindGridHashes`, so not a pending, uncommitted upload) as `wind-grids/{hash}.grib2.br` and lists the hashes in its manifest `WindGridHashes` (version 4; see [session-persistence.md](session-persistence.md)). The restore's `RoomCheckpointArchive.RegisterWindGrids` verifies, registers and temporarily pins them before the weather re-load and the snapshot restore, `RebuildWindGridPins` then gives the room its own pins, and the temporary pins are released on every path. A missing, mis-hashed or undecodable entry is skipped with a warning and its weather flies the FD layers; a version 3 or older checkpoint restores as before.
- **A snapshot of a profile whose `WindGrid` has no hash** (tests may assign one directly) logs a warning and restores without the grid.
- **One station sets the surface floor.** The grid column's floor uses `SurfaceElevationFt()` and `SurfaceAltimeterInHg()`, each read from one METAR station of the profile, so a room spanning airports at different elevations floors the column at that one station's surface pressure altitude, not each airport's.

### Sampling the grid at a position

`WindInterpolator.GetWindAt(profile, WindSamplePoint at, altitudeFt, simTimeSeconds, phaseSeconds)` and the matching `GetWindComponents` take a `WindSamplePoint(LatLon Position, double DeclinationDeg)`. The declination is east-positive and turns the grid's true directions magnetic as the layers are. Where `WindGrid` covers the position the wind comes from a column built per call:

- **Layer 0 is the bottom of the column**, placed at the surface datum (`SurfaceElevationFt()`) rather than its authored altitude, and stays as authored (magnetic). The FD layers above it are not used.
- **Grid levels are kept only above the surface's pressure altitude plus 500 ft** (`GridLevelGroundClearanceFt`). `SurfacePressureAltitudeFt` is `SurfaceElevationFt()` + (29.92 − altimeter) × 1000, the altimeter from `SurfaceAltimeterInHg()` (the first METAR that reports one, else 29.92). A level nearer the ground than that is an extrapolation through the terrain, not the wind an aircraft flies in.
- **Only the two bracketing levels are sampled**: `GriddedWindField.SampleBracket(lat, lon, altitudeFt, floorFt)` bilinearly interpolates just the kept level below and the kept level at or above the altitude (skipping a level with a `NaN` corner), with no allocation. Altitude interpolation between them is the same N/E vector lerp as the layers.
- **Variability**: a grid level within 3,000 ft AGL (`WindVariation.ZeroAmplitudeTopAglFt`) carries layer 0's gust and direction-spread amplitudes re-capped against its own mean speed (`WindVariation.CapAmplitudes`), so `AmplitudeTaper` fades them with height as it does over the layers; higher levels carry none and are never VRB.
- **Per position**: each grid level is converted to magnetic with the declination of the `WindSamplePoint`.
- **Fallback is the old path exactly**: no grid, a position outside it, or no grid level kept after the floor reads the layers as the no-position overload does.
- `GetGriddedMeanWindTrueAt(profile, at, altitudeFt)` is the same column unperturbed and in TRUE (the grid's directions as given, layer 0 turned true); it returns null where the fallback applies. ERAM `UR` uses it.

Consumers that sample at their position: `FlightPhysics` (crab and the airborne groundspeed vector, from the aircraft's cached `Declination`), `LiveTrafficKinematics` (airborne shadows), `HoldingPatternPhase` (outbound heading and leg time), and yaat-server ERAM `UR`.

Consumers that stay on the no-position layer path: ground samples (`FlightPhysics` on the ground, ground shadows), the exits-ahead touchdown forecast, the approach-speed additives (`AircraftPerformance.WindApproachAdditive`, `GustApproachAdditive`) and runway-in-use selection, all of which read layer 0.

## Magnetic declination (`MagneticDeclination.cs`)

`MagneticDeclination` is the NOAA **World Magnetic Model (WMM)** — degree-12 spherical-harmonic evaluation via the `Geo` library,
**globally accurate, not a CONUS longitude approximation** (`MagneticDeclination.cs:6-15`).

- Sign convention: **positive = east declination**. `true → magnetic = true − declination`; `magnetic → true = magnetic + declination`.
- API: `GetDeclination(lat, lon)` / `GetDeclination(LatLon)`, `TrueToMagnetic(deg, …)`, `MagneticToTrue(deg, …)`. The convenience overloads forward to the `(lat, lon)` forms.
- **The WMM epoch is resolved once at startup** as a static field (`EpochDate`, `MagneticDeclination.cs:20-34`): the model covering "now" is stable for the process lifetime, so per-tick LINQ scans of the embedded models are avoided. If the process runs past the newest bundled epoch (a stale package), it clamps to the last valid day rather than throwing.
- `TryCalculate` returning null → declination `0.0` (safe fallback).
- **Process-wide grid cache** (`GridCache`, 0.02° cells): `GetDeclination` evaluates the WMM once per cell, **at the cell centre**, and every later lookup in that cell returns the same value. Each `Geo` evaluation allocates ~100 KB of working arrays, and aircraft in a pattern or on the ground keep re-entering the same handful of cells, so this removed most of the per-tick declination cost in replay profiles.

  The cell-centre rule keeps the value a pure function of the cell — independent of which aircraft (or thread) got there first — so replays stay reproducible; the error bound (~0.01° within a cell) is the same one the per-aircraft box below already accepts. The cache is cleared when it exceeds 200k cells.

### The per-aircraft declination box-cache (`FlightPhysics.RefreshDeclinationCache`)

`FlightPhysics.Update` recomputes declination only when the aircraft has moved outside a small box, on the scenario's magnetic-model date (`PhysicsTickOptions.MagneticModelDateUtc`, from `SimulationWorld.MagneticModelDateUtc`). A live-traffic shadow refreshes the same cache from `LiveTrafficKinematics.Advance` / `Resync`, which take that date too, so the declination that turns a gridded wind magnetic is the recorded day's on a replay:

- Two `AircraftState` fields back this (in `AircraftState.cs`):
  - `Declination` — the cached value, **serialized** (round-trips in snapshots).
  - `DeclinationCachePosition` (`LatLon?`) — `[JsonIgnore]` on `AircraftState` but carried through a snapshot (`AircraftSnapshotDto.DeclinationCachePosition`); `null` = "not cached yet".
- **Box threshold = `0.02°`** (≈ 1.2 nm of latitude). Inside the box the cached `Declination` is reused, because the WMM is ~0.06 ms/call and at 4 Hz × N aircraft it would dominate the tick.
- **Out-of-range / non-finite-position guard**: if `Position` is NaN/∞ or `|lat| > 90` / `|lon| > 180`, the update is skipped and logged (`Geo.Coordinate`'s ctor would otherwise throw and crash the tick), keeping the previously cached value.

`AircraftState.MagneticHeading` / `MagneticTrack` derive from `TrueHeading`/`TrueTrack` and this cached `Declination`.

## Consumption and broadcast

### In `FlightPhysics` (per tick)

Two physics steps consume weather (`FlightPhysics.Update` passes `weather` through):

- **`UpdateNavigation`**: for airborne aircraft with IAS > 0, computes WCA from `IasToTas` + `GetWindAt` at the aircraft's position (`WindSamplePoint(Position, Declination)`) and sets `TargetTrueHeading = bearing + wca`, so the aircraft holds a straight ground track instead of a pursuit curve.
- **`UpdatePosition`** airborne branch: `TAS = IasToTas(IAS, alt)`; ground-speed vector = `TAS·(cos/sin heading) + GetWindComponents(...)` at the aircraft; the wind is **cached into `AircraftState.WindComponents`** so `GroundSpeed` can be recomputed on read without weather context. On the ground the cached wind is the no-position layer path (it feeds `HeadwindKts` for the frame flips); the ground branch's motion ignores wind — groundspeed is derived from IAS and track follows heading.
- **Other airborne readers**: `LiveTrafficKinematics.Advance` fills a shadow's `WindComponents` at its position (a ground shadow stays on the layer path); `HoldingPatternPhase` samples the wind at the aircraft for the outbound heading and leg time. ERAM `UR` in yaat-server reads the grid in true.

See [tick-loop.md](tick-loop.md) for where these steps sit in the 8-step `FlightPhysics.Update` order and the PrePhysics→Physics×4→PostPhysics frame.

### Selection and broadcast cadence

- **Replay / scenario engine** (`SimulationEngine.cs`): the timeline is collapsed at restore/replay sites (`World.Weather = timeline.GetWeatherAt(t)`), and `ApplyWeatherJson` (`SimulationEngine.cs:2562`) routes a `LoadWeather` JSON through `WeatherTimelineParser` — a timeline is stored on `Scenario.WeatherTimeline` and immediately collapsed; a v1 profile clears the timeline and sets `World.Weather` directly.

  Snapshots serialize `World.Weather` as JSON (`SimulationEngine.cs:135`, `:148`) plus `Scenario.WeatherSourceJson`; `RestoreFromSnapshot` rebuilds `Scenario.WeatherTimeline` from that source so v2 evolution survives a snapshot-based rewind (the parsed timeline object itself is not snapshotted).
- **Every run kind, every second** (the `WeatherAdvance` spine step, `SimulationEngine.AdvanceWeatherTimeline` — [tick-loop.md](tick-loop.md)): if a timeline is active, `GetWeatherAt(elapsed)` is recomputed into `World.Weather` ungated, to drive physics/visual acquisition; the live host mirrors it onto `Room.Weather`. This does not broadcast. Broadcasts are driven by the reported-METAR issuer (see below), which has its own SPECI criteria.

  The continuous advance is the faithful semantic, aviation-reviewed: 7110.65 §2-6-3/-4/-5, §3-1-8, §3-5-1, §3-5-3, §13-1-14 and AIM 7-1 / 4-3 are silent on how a simulated wind field is discretised, because every FAA quantisation is a reporting one and lives in `MetarIssuer` / `SpeciCriteria`. Runway-in-use selection (30° / 5 kt hysteresis) and SPECI (45° / 10 kt) cannot chatter on sub-degree ticks.

  `WeatherProfile` carries per-instance METAR caches, so `WeatherTimeline.BuildProfile` hands back the same instance for the same static period (inside a transition a new profile per second is inherent).

### Reported METAR reconstruction (`MetarIssuer`, `MetarComposer`, `SpeciCriteria`)

The METAR *string* broadcast to clients (`WeatherChangedDto.Metars`) is reconstructed from the continuous weather and re-issued like a real observation, rather than echoing the static loaded text:

- **Routine** METAR for every station once per hour at **:53Z** (`MetarIssuer.RoutineObservationMinute`); **SPECI** when a station's conditions cross a threshold since its last issued report (`SpeciCriteria`: wind shift ≥45°/≥10 kt, visibility crossing 3/2/1/½/¼ SM, ceiling crossing 3000/1500/1000/500 ft, precip onset/cessation — a basic subset of AIM TBL 7-1-1).
- The observation clock is the **session clock** — `SimScenarioState.SessionStartUtc` + elapsed sim time (`SimTimeUtc`), so pause/SimRate behave and the :53 grid is a function of sim time — a room returning to live after a rewind resumes the grid the live room was on (a reconstruction host issues nothing; `ReconstructionHost.IssueMetars` is empty); conditions are **sampled and frozen at issuance** so the report holds steady between issues (realistic observation lag).

  An issuer built mid-session (the rebuild after a rewind) seeds its last-routine instant and every station's baseline from the current second and already carries the routine that fell inside the session, so its first tick re-issues nothing and the reports do not revert to the load-time text until the next :53.
- Surface wind comes from the **actual physics surface wind** (lowest layer, magnetic) converted to **true** via `MagneticToTrue` — METARs are true; physics stays magnetic. Encoding follows AIM 7-1-28 (10SM cap, `CLR` for automated clear, altimeter truncated, clouds >12,000 ft omitted).
- **Display-only**: physics, visual acquisition, and all operational logic keep reading the continuous `World.Weather` — never the reported strings. Only the broadcast DTO carries them.
- Enabled per load via the `reconstructMetars` flag on `LoadWeather` — **true** for file/API weather, **false** for live-fetched weather (left untouched). The *intent* persists separately from the live issuer: `SimScenarioState.MetarReissuanceEnabled` (serialized in the snapshot DTO and the recording manifest) plus `WeatherSourceJson` (the last-applied weather JSON).

  The live `MetarIssuer` is runtime-only and torn down on every replay/rewind/recording-load; the server tick loop rebuilds it on return to live via `RoomEngine.EnsureLiveMetarIssuer` **only** when the persisted intent says the weather was dynamic, anchored to the session clock at that second.

  `LoadWeather` leaves the intent and issuer untouched while reconstructing (gated on `Room.IsBroadcastSuppressed`/`IsPlaybackMode`); the replayed `RecordedWeatherChange.ReconstructMetars`, the restored snapshot, or the recording manifest supplies it instead. The temp replay engine (report generation) stays off.
- **Known limitations**: a precip-onset/cessation SPECI does not rewrite the present-weather group (the modeled precip is generic Rain/Snow, not a METAR token), so the body keeps the base wx group; a `VV` (indefinite-ceiling) base is re-reported as `OVC` (the parser models VV as a synthetic OVC layer).

## Live weather (client)

`ArtccAirportResolver.GetAirportIdsAsync(artccId)` supplies `airportIds` first, from the vNAS data-api ARTCC config (disk-cached, 6h TTL).
`ExtractUnderlyingAirports` **recurses the `facility` / `childFacilities` tree**, collecting
`starsConfiguration.areas[].underlyingAirports[]` from every STARS facility and deduplicating — a STARS config belongs to a facility
(each TRACON has its own), never to the document root. IDs are FAA-style (`OAK`); `BuildLiveWeatherAsync` prefixes three-letter IDs
with `K` before querying (`LiveWeatherService.cs:34`). An empty list short-circuits the command with "No airports found for ARTCC".

`LiveWeatherService.BuildLiveWeatherAsync(artccId, airportIds)` (`LiveWeatherService.cs:25`) fetches from `aviationweather.gov` and
assembles one `WeatherProfile`:

- METARs (`/api/data/metar`) and FD winds (`/api/data/windtemp`) are fetched in parallel; returns `null` only if **both** fail.
- **FD wind layers** (`BuildWindLayersFromFd`): for each altitude level, vector-average the non-light-variable station reports, then **convert TRUE → MAGNETIC** via `MagneticDeclination.TrueToMagnetic` using the ARTCC center as a proxy position (`LiveWeatherService.cs:223`).
- **Surface wind from METAR** (`BuildSurfaceWindLayer`): vector-averaged METAR winds inserted as an `Altitude = 0` layer, converted **TRUE → MAGNETIC** via the ARTCC-center declination like the FD layers (METAR text winds are true per AIM 7-1-28; only ATIS/ASOS voice is magnetic). VRB stations contribute speed only (all-VRB → a `Variable` layer); gusts and dddVddd spreads are mined from each station's raw text.
- `GetArtccCenter(artccId)` (`LiveWeatherService.cs:353`) is a hand-maintained ARTCC → primary-airport table used purely as a declination proxy; unknown ARTCC falls back to mid-CONUS `(39.0, −98.0)`.
- `FdRegionMapping.GetRegion(artccId)` (`FdRegionMapping.cs`) is a separate hand-maintained ARTCC → FD-region table (`bos`/`mia`/`chi`/`dfw`/`slc`/`sfo`/`alaska`); an unmapped ARTCC disables the FD fetch and the profile has no winds-aloft layers.

### GFS grid fetch and upload

`MainViewModel.LoadLiveWeatherAsync` starts `GfsGridFetcher.FetchAsync(artccId, nowUtc, …)` before `BuildLiveWeatherAsync` and awaits it after, so the NOMADS GET runs beside the AWC calls (cancelled, and its outcome logged, when there is no weather to go with it). The status line reads "Fetching GFS winds aloft (HHZ fFFF)..." only if the grid is still running after the AWC build, and shows the predicted newest cycle, not one a 404 step-back later settles on.

- **Request**: the NOMADS GRIB filter (`filter_gfs_0p25.pl`) for UGRD, VGRD and TMP on the 20 isobaric levels `Grib2Reader` expects, over the ARTCC boundary's extremes (`ArtccBoundaryDatabase.Default.FindById`) plus `GfsGridFetcher.BoxMarginDeg` (1.5°), at least 2° × 2°, rounded outward to whole degrees, longitudes in the grid's 0–360 convention.
- **Cycle**: `NewestCycle` is the latest 00/06/12/18Z cycle at least 3.5 h old; the forecast hour is the one nearest now, clamped to 0..120. A 404 means not posted yet and steps back 6 h (`StepBack`), three cycles in all; any other status, timeout or connection error ends the fetch as `RequestFailed`. Each GET and its body read share the 20 s request timeout.
- **Size**: the response is read only up to `WindGridRegistry.MaxGridBytes` (512 KB, the same cap the server enforces). A box whose estimated size (`EstimatedBytes`: 0.25° points × 60 messages × 1.3 bytes, the ratio measured on the ZOA sample) passes the cap, or that spans more than 180° of longitude (ZAN), is refused before any request as `BoxTooLarge`.
- **Upload**: only a live-shaped profile (`WindGridRegistry.IsLiveWeather`) uses the grid. `ServerConnection.UploadWindGridAsync` sends the bytes, the returned hash goes to `profile.WindGridHash`, and the usual `LoadWeather` follows (the hub consumes the pending upload). The client compares the returned hash with `WindGridRegistry.HashOf` and logs a mismatch, using the server's.
- **Failure**: every failure (no boundary, box too large, no cycle posted, request failed, over the upload limit, a `HubException` refusal whose server text becomes the reason) leaves `WindGridHash` null and the weather loads on the FD layers, with one terminal warning from `GfsGridFetcher.WarningText(reason, hasFdLayers)`, worded for the winds the weather then flies: "Live weather: GFS winds unavailable — {reason}; using FD winds aloft only." with FD layers, "Live weather: GFS winds unavailable — {reason}; METAR surface winds only." without. The same two texts serve a saved or API load, where `hasFdLayers` comes from `WindsAloftSource.HasFdLayers(json)` on the JSON itself (a timeline counts as having them).
- **Saved files**: `MainViewModel` keeps the uploaded bytes beside the active weather JSON (`ActiveWindGrid`). Save Weather (`WeatherSaveGrid.PrepareAsync`, run on the JSON and grid captured before the file picker opens) writes them through `SavedWeatherGrid.Embed` as a base64 `windGridGrib2` beside `windGridHash`. A member who holds no bytes for the named hash (anyone but the loader, or the loader after a restart) fetches the room's copy with `ServerConnection.GetWindGridAsync` (hub `GetWindGrid`, pinned hashes only), checks it against the 512 KB cap, the hash and the GRIB2 decode, embeds it and keeps it as the active grid while the weather still names that hash. When the fetch fails (`not connected to the server`, the server's refusal text, `grid over the upload limit`, `the server's grid did not match its hash`, `the server's grid could not be decoded`) the file is saved without `windGridHash`, so it reloads on FD winds, and the status bar, the terminal and the log say `Saved without its GFS grid: {reason}` in place of `Weather saved: {name}`. Loading a file or API weather runs `PrepareSavedWeatherAsync`: `SavedWeatherGrid.Extract` pulls the bytes out (the JSON sent never carries them), they are re-uploaded, and the server's hash replaces the file's. A file naming a hash with no usable bytes has the hash stripped and warns with the reason `grid not in the saved file`; bytes the client or server refuses (over the upload limit, a `HubException`) warn with that refusal as the reason. These are two different cases in the source line. A room weather echo keeps the held bytes only while it names the same hash.

### Winds-aloft source line

`MainViewModel.WindsAloftLine` is the METAR header's second line (`MetarView.axaml`, under the weather name, hidden when null), and a load's final status-bar message says the same. The wording lives in `WindsAloftSource` (`MainViewModel.Weather.cs`); the grid's attribution is `GfsGridFetcher.SourceLabel`: `NOAA GFS 12Z 16 Oct, +4 h forecast` (cycle in UTC, day without a leading zero, the forecast hour the grid was fetched for; it does not advance with sim time). `{name}` is the weather's own name.

| State | Header line | Status bar |
|---|---|---|
| Live, grid loaded | `Winds aloft: {GRID}` | `Loaded: {name} · winds aloft: {GRID}` |
| Live, GFS failed, FD loaded | `Winds aloft: FAA FD forecast · GFS unavailable ({reason})` | `Loaded: {name} · winds aloft: FAA FD (GFS unavailable)` |
| Live, FD failed too | `Winds aloft: none · METAR surface winds only` | `Loaded: {name} · no winds aloft, METAR surface winds only` |
| Saved or API file with a grid | `Winds aloft: {GRID} · saved file` | `Weather loaded: {name} · winds aloft: {GRID} (saved file)` |
| Saved file, grid missing, FD layers | `Winds aloft: FAA FD forecast · GFS grid not in the saved file` | `Weather loaded: {name} · winds aloft: FAA FD (GFS grid not in the saved file)` |
| Saved file, grid missing, no FD layers | `Winds aloft: none · METAR surface winds only · GFS grid not in the saved file` | `Weather loaded: {name} · no winds aloft, METAR surface winds only (GFS grid not in the saved file)` |
| Saved file, grid refused, FD layers | `Winds aloft: FAA FD forecast · GFS unavailable ({reason})` | `Weather loaded: {name} · winds aloft: FAA FD (GFS unavailable)` |
| Saved file, grid refused, no FD layers | `Winds aloft: none · METAR surface winds only · GFS unavailable ({reason})` | `Weather loaded: {name} · no winds aloft, METAR surface winds only (GFS unavailable)` |
| Scenario weather | `Winds aloft: scenario weather` | the scenario load's message |
| Replay | `Winds aloft: {GRID} · recorded` | none |

- **Two sources of the line**: the loader's own report (`ForLiveLoad`, `ForSavedLoad`, applied by `ApplyLoadedWindsAloft`) knows the GFS failure reason and the saved-file origin. Every other member, a rejoin and a replay get the line from the room's weather alone (`ForRoomWeather`): the grid's run from `WeatherChangedDto.WindGridCycleUtc` and `WindGridForecastHours`, else by shape. Live-shaped weather (`IsLiveShaped`: METARs and a first wind layer at 0 ft, the `WindGridRegistry.IsLiveWeather` rule) shows `Winds aloft: FAA FD forecast` with layers above the surface and the METAR-only line without; any other weather shows the scenario line.
- **The loader keeps its line** over the room's echo of that same load: the echo is matched on name and `SourceJson`, which the server sets to the load's own JSON before broadcasting. Any later weather replaces it. A saved load that names no grid keeps its usual status and the room's weather sets the line.
- **Playback**: the `· recorded` suffix follows `IsPlaybackMode`; the line is re-read from the last room weather when playback ends.
- **Warnings**: a live load with neither the grid nor FD layers also adds one terminal warning, `GfsGridFetcher.FdUnavailableWarning` ("Live weather: FD winds aloft unavailable; METAR surface winds only."), from the report's `Warning`. A saved load without FD layers whose grid is missing or refused adds only the GFS warning (METAR-only wording), never `FdUnavailableWarning`: a missing FD is not a fetch failure there.
- **Cleared weather** clears the line.

## Direction & units convention cheat-sheet

The single most error-prone area. Memorize this before touching any wind value.

| Quantity | FROM or TOWARD | True or Magnetic | Altitude ref | Where |
|---|---|---|---|---|
| `WindLayer.Direction` | FROM | **Magnetic** | feet **MSL** | `WeatherProfile.cs:14` |
| `WindAtAltitude.DirectionDeg` (`GetWindAt`) | FROM | Magnetic (inherits layer; a gridded level is turned magnetic per position) | — | `WindInterpolator.GetWindAt` |
| `GriddedWindSample.DirectionTrueDeg` (`GriddedWindField.SampleColumn`) | FROM | **True** (derived from U/V) | ISA pressure altitude, feet | `Weather/GriddedWindField.cs` |
| `WindAtLevel.DirectionTrue` (FD parser) | FROM | **True** | feet (level) | `WindsAloftParser.cs:4` |
| METAR text wind | FROM | **True** (AIM 7-1-28; converted to magnetic when building the surface layer) | surface | `LiveWeatherService.BuildSurfaceWindLayer` |
| FD-derived layer (after `BuildWindLayersFromFd`) | FROM | Magnetic (converted via ARTCC-center declination) | feet MSL | `LiveWeatherService.cs:223` |
| `GetWindComponents` output | **TOWARD** (FROM `+180°`) | matches input | — | `WindInterpolator.GetWindComponents` |
| Cloud layer base (`CloudLayer.BaseFeetAgl`) | — | — | feet **AGL** (hundreds in METAR) | `MetarParser.cs:15` |

A profile layer's direction is **magnetic**; a steering `bearing` from `GeoMath.BearingTo` is **true**. `ComputeWindCorrectionAngle`
is fed the true bearing alongside the magnetic wind direction (`FlightPhysics.UpdateNavigation`) — the WCA it produces therefore carries
the local declination into the crab. This is the current behavior; do not "fix" the mismatch without understanding that the wind
profile is authored in magnetic by convention.

## Test map

The behaviors above already have regression coverage — find the right harness before adding a new test:

| Test file | Pins |
|---|---|
| `tests/Yaat.Sim.Tests/MetarParserTests.cs` | station-id heuristic, visibility forms, FEW/SCT/BKN/OVC + VV synthetic-OVC, ceiling = lowest BKN/OVC, wind/altimeter. |
| `tests/Yaat.Sim.Tests/WindsAloftParserTests.cs` | FD header-column matching, DDSS decode (≥100 kt, 9900 light-variable, temp-suffix strip). |
| `tests/Yaat.Sim.Tests/WindInterpolatorTests.cs` | altitude N/E lerp, clamp outside range, IAS/TAS/Mach conversions, WCA, `PressureToIsaAltitudeFt` round-trip against `GetAtmosphere`. |
| `tests/Yaat.Sim.Tests/Grib2ReaderTests.cs` | the real NOMADS GFS sample `TestData/gfs-zoa-sample.grib2` (60 messages, header/grid/values checked against ecCodes), all four scanning-mode reorderings, sign-magnitude binary scale, bitmap NaN, and the refusals (template 5.2, edition 1, truncated file, bad reference date, increment/latitude/point-count disagreements, non-finite reference value, binary/decimal scale out of range, a value overflowing float32). |
| `tests/Yaat.Sim.Tests/GriddedWindFieldTests.cs` | the sample's cycle, bounds and 20 levels; exact-point and bilinear sampling; outside-grid null; ±longitude equivalence; FROM direction and speed from U/V (due-north wind is +0); a NaN corner drops only that level; construction errors naming parameter and level; wind and temperature range refusals (just past and exactly on each bound, a `NaN` value accepted). |
| `tests/Yaat.Sim.Tests/MetarInterpolatorTests.cs` | exact-station match, 50 nm IDW visibility, lowest-ceiling layer set. |
| `tests/Yaat.Sim.Tests/WeatherTimelineTests.cs` | time interpolation, layer-count snap. |
| `tests/Yaat.Sim.Tests/WeatherTimelineParserTests.cs` | v1/v2 auto-detect, v2 validation/sort, error cases. |
| `tests/Yaat.Sim.Tests/WindPhysicsTests.cs` | end-to-end crab + groundspeed-vector behavior under wind, including a gridded profile: the airborne ground vector, a fix-target crab, and a ground aircraft at a high field staying on the layers. |
| `tests/Yaat.Sim.Tests/GriddedWindSamplingTests.cs` | the position-taking `GetWindAt` over a `WindGrid`: each position gets its own grid wind, the sweep against a whole-column reference (`SampleBracket`), layer 0 at the surface datum, the altimeter-dependent ground-clearance floor, the borrowed gust amplitude and its re-cap (`CapAmplitudes`), and the fall-back to the layers (no grid, outside it). |
| `tests/Yaat.Sim.Tests/HoldingPatternGridWindTests.cs` | `HoldingPatternPhase` outbound heading (direct entry) and outbound leg time under a gridded wind at the fix. |
| `tests/Yaat.Sim.Tests/LiveTraffic/LiveTrafficKinematicsTests.cs` | shadow `Advance` / `Resync`, including `ShadowWind_IsTheGridWindAtItsPosition`. |
| `tests/Yaat.Sim.Tests/MagneticDeclinationTests.cs` | WMM declination values, true↔magnetic round-trips. |
| `tests/Yaat.Client.Tests/LiveWeatherServiceTests.cs`, `LiveWeatherRealDataTests.cs` | live fetch assembly, FD→magnetic conversion, surface-wind layer. |
| `tests/Yaat.Client.Tests/GfsGridFetcherTests.cs` | the NOMADS URL against the sample's, cycle choice and step-back across midnight, forecast-hour rounding and clamps, the ZOA box, the 180° and size-estimate refusals, and `FetchAsync` over a stub handler (404 step-back, server error, over-cap body, stalled and reset bodies, unknown ARTCC, ZAN with no request). |
| `tests/Yaat.Client.Tests/SavedWeatherGridTests.cs` | `SavedWeatherGrid` embed/extract round trip, a hash without bytes or corrupt base64 stripping the hash, `NamedHash`/`NamesGrid`/`WithHash`, and the weather editor dropping both grid fields. |
| `tests/Yaat.Client.Tests/WeatherSaveGridTests.cs` | `WeatherSaveGrid.PrepareAsync`: no hash never fetches, a held grid embeds without a fetch, a member without bytes fetches by hash and the file round-trips through `SavedWeatherGrid.Extract`, and each fetch failure (refused, not connected, canceled or closed mid-fetch, over the cap, another hash, undecodable) strips the hash with its reason. |
| `tests/Yaat.Client.UI.Tests/ViewModels/MainViewModelSaveWeatherTests.cs` | `SaveWeatherAsync` with no connection: the file is written without the grid hash, the recent-weather entry is added and the "Saved without its GFS grid" warning reaches the terminal. |
| `tests/Yaat.Client.Tests/WindsAloftSourceTests.cs` | `WindsAloftSource` and `GfsGridFetcher.SourceLabel`: the header line, status text and warning for each state (grid, FD fallback with reason, METAR only, saved file with and without its grid, the room's weather and playback), the live-shape rule, and the run read from the committed `gfs-zoa-sample.grib2`. |
| `tests/Yaat.Client.UI.Tests/ViewModels/MainViewModelWindsAloftLineTests.cs` | `WindsAloftLine` follows `WeatherChanged` (another member's grid, replay suffix, cleared weather), the loader's report outlives its own echo and is replaced by any later weather, and the METAR header shows the line and hides it when empty. |
| `tests/Yaat.Server.Tests/WeatherLoadEchoTests.cs` (yaat-server) | a profile or timeline load's own `WeatherChanged` carries that load's `SourceJson`. |
| `tests/Yaat.Server.Tests/WeatherSeedOnJoinTests.cs` (yaat-server) | the join seed and `BroadcastWeatherChanged` carry the grid's `WindGridCycleUtc` and `WindGridForecastHours`, and nulls for weather without a grid. |
| `tests/Yaat.Client.Tests/ArtccAirportResolverTests.cs` | underlying-airport extraction from the real ZOA data-api config (facility-tree recursion, dedup). |

## Footguns

- **Direction conventions are never co-located and differ by source.** `WindLayer.Direction` is FROM/magnetic/MSL; `WindAtLevel.DirectionTrue` (FD) is FROM/**true**; METAR text winds are true (AIM 7-1-28) and `BuildSurfaceWindLayer` converts them with `TrueToMagnetic` via the ARTCC-center declination; FD winds get the same conversion in `BuildWindLayersFromFd`; a `GriddedWindSample` is true and is turned magnetic per position by the position-taking `WindInterpolator` overloads.

  `GetWindComponents`/`ComputeWindCorrectionAngle` reverse FROM→TOWARD by `+180°`. Mixing any two silently yields ~15-20° errors. See the [cheat-sheet](#direction--units-convention-cheat-sheet).
- **Three interpolation algorithms, three different rules.** Altitude (`GetWindAt`) and time (`InterpolateWindLayers`) use N/E vector lerp; spatial (`MetarInterpolator`) does **not** lerp direction at all (lowest-ceiling layer set + IDW visibility). In the time axis, **cloud cover step-changes discretely at `t=0.5`** and a **layer-count mismatch snaps to the target list** instead of interpolating.
- **`WindLayers` re-sorts on assignment.** Setting `WeatherProfile.WindLayers` re-orders ascending by altitude (`WeatherProfile.cs:46`); insertion order is not preserved. The `GetWindAt` clamp/scan assumes this sort.
- **FD `DecodeWind` overloads the code.** `DD ≥ 50` means direction `(DD−50)×10` and speed `SS+100` (winds ≥ 100 kt); `9900` is light-and-variable; a temperature suffix (`2714-08`) must be stripped first. Column assignment is by **nearest header-center distance**, not fixed offsets — fragile to header spacing.
- **`VV` is a synthetic OVC layer.** `MetarParser` injects vertical-visibility / indefinite-ceiling as an `Overcast` `CloudLayer` (`MetarParser.cs:245`) so multi-layer obstruction logic treats it uniformly. It is not an OVC token in the raw METAR — don't expect it to round-trip back to `VV`.
- **`DeclinationCachePosition` is snapshotted** (`AircraftSnapshotDto.DeclinationCachePosition`), so a restored aircraft keeps the position its `Declination` was computed at and recomputes where the original did. It is `null` only for an aircraft whose cache was cold, or from a snapshot written before the field existed. Looping `ReplayFromStartTo(t)` — which resets to `t=0` every call — is still wrong for a declination check; use `FastForwardTo(target, …)` to advance from current state.
- **`ParsedMetarOverrides` is `[JsonIgnore]`** (`WeatherProfile.cs:59`) and only populated by `WeatherTimeline.GetWeatherAt` during a transition window; `GetWeatherForAirport` checks it before its own cache (`WeatherProfile.cs:68`). After a snapshot round-trip mid-transition the overrides are gone and ceilings/visibility revert to parsing the raw METAR text — a subtle replay divergence to watch for if interpolated weather "jumps" on restore.
- **`WindGrid` is `[JsonIgnore]`; the weather text carries `WindGridHash` and `WindGridRegistry.Attach` resolves it.** A hash whose grid is not registered, or on weather that is not live weather, flies the layer path with only a log warning. A saved weather file therefore embeds the grid bytes (`windGridGrib2`) and Load Weather re-uploads them, because a bare hash would load silently on the FD layers on any server that no longer holds the grid; a file with a hash and no usable bytes is stripped client-side and warns. A profile rebuilt from a weather timeline carries no grid.
- **Position-aware and layer-0 readers must not be mixed up.** Airborne consumers sample at their position; the ground, the exits-ahead touchdown forecast, approach-speed additives, runway-in-use and `WindObservation` read layer 0. A new wind reader picks one deliberately (see the Task Index row "Make a wind consumer position-aware or add a wind reader").
- **The WMM epoch is fixed at process start** (`MagneticDeclination.cs:20`). A long-lived process whose date passes the newest bundled epoch clamps to the last valid day; declination will not track the calendar past that point.
- **`GetArtccCenter` and `FdRegionMapping` are hand-maintained tables** (`LiveWeatherService.cs:353`, `FdRegionMapping.cs:9`). An ARTCC missing from `FdRegionMapping` silently disables FD winds; one missing from `GetArtccCenter` falls back to mid-CONUS `(39, −98)` for the declination proxy, skewing the TRUE→MAGNETIC conversion of FD winds.
- **The mean/instantaneous split is load-bearing.** `WindLayer.Direction/Speed` stay the authored MEAN; the instantaneous wind exists only at the `GetWindAt` output. Reporting (`MetarIssuer`), SPECI decisions, runway selection, crosswind limits, and any approach-stability gate must read the mean (or the observed 2-minute mean via `WindObservation`) — never the instantaneous value, which wobbles by design.
- **Weather lives in two places, and a scenario reload only rebuilds one.** `TrainingRoom.Weather` / `TrainingRoom.WeatherSourceJson` are room-scoped and survive anything; `World.Weather`, `SimScenarioState.WeatherTimeline`, and `MetarIssuer` belong to the engine, which a restart or rewind throws away.

  Rewind repairs itself and reproduces the tape: its starting weather, then every recorded weather change up to the target, a recorded clear included, and a clear taken in playback wins at a rewind to that second. With a snapshot at or before the target the snapshot restore supplies the weather; with none, `RecordingManager.ApplyTapeStartingWeather` loads the tape's starting weather before the replay. A **restart** has an empty tape and a **scenario load** starts one, so `RecordingManager.RestartScenarioAsync` and `ScenarioLifecycleService.CommitPreparedScenario` (its `ReapplyRoomWeather`) each explicitly re-run `RoomEngine.LoadWeather(Room.WeatherSourceJson, Room.SessionSettings.MetarReissuanceEnabled)` once the new engine is in place; a restart after the room's weather was cleared records a weather clear at t=0 on the fresh tape.

  The tape's starting weather (`SimScenarioState.InitialWeatherJson`) is not the room's weather at the reload: a scenario load sets it from the room's weather then, and every reload after (rewind, restart, recording load, checkpoint restore) carries the tape's own through `ReloadStart.InitialWeatherJson` (the scenario's, the recording's or the checkpoint's), so exports after any of them start on the original weather.

  Without it the room reports loaded weather to clients while the simulation flies calm — the shape of issue #313's session-setting loss. Anything else that rebuilds the engine needs the same call.
