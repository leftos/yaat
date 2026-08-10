# Variable Wind Simulation (VRB + dddVddd + live gusts)

## Context

Today YAAT's winds are perfectly steady. The gaps, confirmed by code inspection:

- `MetarParser.ParseWind` (`src/Yaat.Sim/MetarParser.cs:55,332`) parses `VRBssKT` as `WindDirectionDeg = null` — displayed as "VRB" by `WeatherDisplayInfo`, but with no physics meaning.
- The `dddVddd` variable-direction group (e.g. `220V280`) is parsed nowhere; `MetarComposer` (`src/Yaat.Sim/MetarComposer.cs:26,125-126`) deliberately strips it from reconstructed METARs ("We do not model a variable-direction spread").
- Physics reads a single fixed `WindLayer.Direction` per layer via `WindInterpolator.GetWindAt`; there is no time-variability model.
- `WindLayer.Gusts` is stored, parsed, and time-lerped but never applied to physics (documented footgun in `docs/weather-and-wind.md`).
- `LiveWeatherService.BuildSurfaceWindLayer` skips VRB stations entirely when vector-averaging the surface layer.

User decision (2026-08-10): implement **full simulation** — parse VRB and the `dddVddd` spread, make surface wind direction actually wander over time (affecting crab, groundspeed, runway wind components), and fold in making gusts live, since it's the same "wind isn't steady" gap.

Key constraints known up front:
- Replays re-simulate deterministically → time-varying wind must be a pure function of sim time (seeded noise), never `Random`/`DateTime.Now` per tick.
- Aviation realism review is mandatory (flight physics scope) — wander period/amplitude and gust application must be grounded in real observations, not guessed.
- `WeatherTimeline.HasMeaningfulChange` gates per-tick `World.Weather` updates on >1° direction / >0.5 kt speed changes — a wandering wind interacts with this gate and with SPECI wind-shift criteria.

## Exploration findings

### Data model / wire / display (explorer report, verified file:line)

- **Adding optional fields to `WindLayer` is safe and precedented.** `WindLayer` (`src/Yaat.Sim/WeatherProfile.cs:6-26`): `Id, Altitude, Direction, Speed, Gusts(double?)`. Plain System.Text.Json POCOs; `WeatherTimelineParser` uses only `PropertyNameCaseInsensitive` — unknown fields skip, missing fields default. `Gusts` is already a YAAT-only extension: ATCTrainer's native wire shape (`WeatherProfileLayerDto`, `src/Yaat.Client/Services/TrainingDataService.cs:93`) is `Id, Altitude, Direction, Speed` only — no gusts, no variability concept. Scenario JSONs carry no weather at all.
- **`ParsedMetar`** (`MetarParser.cs:27-36`) has `WindDirectionDeg/WindSpeedKts/WindGustKts` (all `int?`); VRB → direction null with speed intact. `VariableFrom/VariableTo` fit as two more `int?` fields. A `\b\d{3}V\d{3}\b` regex already exists in `MetarComposer.cs:26` (used only to strip).
- **Wire**: `WeatherChangedDto` + `WindLayerDto(int Altitude, int Direction, int Speed, int? Gusts)` — server `yaat-server/src/Yaat.Server/Dtos/TrainingDtos.cs:485,529`, client `ServerConnection.cs:1402,1404`. Weather is an event-driven room broadcast (`TrainingBroadcastService.BroadcastWeatherChanged`, fired on LoadWeather/ClearWeather/MetarIssuer re-issuance) — **not** covered by `TrainingDtoFingerprint`, so new fields broadcast automatically; no fingerprint work needed.
- **Displays consume METAR text, not WindLayers.** `dto.WindLayers` is consumed nowhere client-side. `WeatherDisplayInfo` (`MainViewModel.Weather.cs:13-54`) is built by re-parsing `dto.Metars` via `MetarParser.Parse` and **already renders "VRB"** for null direction (`:38-41`). vStrips shows raw METAR verbatim. vTDLS: none. CRC SSA wind is CRC-native, not fed by YAAT. No ATIS feature exists.
- **⇒ The display blocker is `MetarComposer.PatchWind`** (`MetarComposer.cs:117-127`): it replaces the wind group with a composed `dddssKT` and *deletes* any `dddVddd` group. `ReportedConditions` (`src/Yaat.Sim/ReportedConditions.cs:8-18`) has non-nullable `WindDirTrueDeg` and no VRB/variability representation.
- **Pre-existing bug found**: `MetarIssuer.SampleWind` text-fallback (`MetarIssuer.cs:172-177`) treats a VRB base METAR (null direction) as **calm** — a loaded `VRB15KT` with no wind layers is re-reported `00000KT`.
- **Pre-existing gap**: `LiveWeatherService.BuildSurfaceWindLayer` (`LiveWeatherService.cs:239-280`) skips null-`Wdir` (VRB) stations from the vector-averaged surface layer (`:248-251`).
- **Snapshots/recordings**: `World.Weather` round-trips as a raw `WeatherProfile` JSON blob (`SimulationEngine.cs:210,223` — doc line refs drifted); `RecordedWeatherChange` carries the raw source JSON. Adding an optional field is a no-op for old recordings (`docs/snapshots-and-replay.md:47,169`) — no migrator/upgrader step.
- **Tests to extend**: `MetarParserTests.cs:364` (`[Theory]` incl. existing VRB case), `WeatherTimelineTests`, `WindInterpolatorTests`, `WeatherDisplayInfoTests.cs:37-44` (VRB render), `LiveWeatherServiceTests.cs:8-32` (VRB parse).
- Doc follow-up: `docs/weather-and-wind.md` "known limitations" doesn't list the VRB-stripping; line refs `SimulationEngine.cs:135/148` are stale (now 210/223).

### Physics / determinism (explorer report, verified file:line)

- **Wind consumers** (only these read direction/speed): `FlightPhysics.UpdateNavigation` WCA (`FlightPhysics.cs:271-273` — no elapsed-time param), `FlightPhysics.UpdatePosition` groundspeed vector + `WindComponents` cache (`:1183-1185` — only sub-tick delta), `HoldingPatternPhase` timed-leg + outbound triple-drift (`:305-311`, `:346-352` — has `ctx.ScenarioElapsedSeconds`). No runway-selection, conflict-detection, or ATIS wind consumers exist. (Doc line refs in weather-and-wind.md/flight-physics.md drifted ~110-150 lines.)
- **Architecture conclusion**: do NOT thread elapsed time into `FlightPhysics`/`WindInterpolator` (signature change → `SimulationWorld.cs:281,287` + 224 test call sites across 51 files). Instead, apply the wander when `World.Weather` is collapsed — the 4 existing per-second collapse sites: `SimulationEngine.cs:1756`, `:1958`, `:2040` (standalone/replay, unconditional) and yaat-server `SimulationHostedService.cs:190-198` (live, gated by `HasMeaningfulChange`). All 4 sub-ticks in a second use the same profile. `WindInterpolator` API unchanged.
- **Determinism**: `Scenario.ElapsedSeconds` is deterministic (tick-derived, not wall-clock). Established RNG pattern is sparse event-triggered draws from `SerializableRandom` (`World.Rng`, snapshotted); per-tick draws would perturb the shared stream — architecturally novel and risky. **A pure function `f(elapsedSeconds, seed)` (value-noise) needs no RNG and replays for free — no new `RecordedAction`.** The per-second timeline collapse is already re-derived on replay, never recorded.
- **`HasMeaningfulChange`** (`WeatherTimeline.cs:94`, live-server-only gate at `SimulationHostedService.cs:193`): a wandering wind trips it every second — not a correctness issue (physics ignores object identity), but the dedup gate stops deduping; continuous `World.Weather` update does NOT broadcast by itself (only `MetarIssuer.Tick` issuance triggers `BroadcastWeatherChanged`).
- **SPECI spam risk**: `SpeciCriteria.IsWindShift` (`SpeciCriteria.cs:45-58`) fires at ≥45° shift with both endpoints ≥10 kt, re-baselining each issuance — an oscillation with envelope ≥45° at ≥10 kt would spam SPECIs. Reported METARs should sample the **mean** wind (+ emit VRB/dddVddd groups), not the instantaneous wandered value.
- **`MetarIssuer.SampleWind`** (`MetarIssuer.cs:134-183`) reads lowest layer, rounds to 10°, magnetic→true; `Calm` (≤2 kt) is the only "no clean direction" state. `ReportedConditions` needs variability fields.
- **Gust plumbing**: parse → `WindLayer.Gusts` → time-lerp → `MetarIssuer` G-group (cosmetic). `WindAtAltitude` record has no Gusts member (altitude interpolation drops it); zero physics consumption confirmed. Plausible hooks: additive fluctuation on the groundspeed vector in `UpdatePosition` (`FlightPhysics.cs:1183-1192`), or — uniformly with direction wander — collapse gust-driven speed fluctuation into the effective per-second `WindLayer.Speed`.
- **Replay tolerance note**: `SnapshotDiff` tolerances (±0.5 nm, ±5°, ±10 kt) absorb ordinary drift; a fast high-amplitude wander could mask genuine divergence — keep amplitudes realistic and consider tightening when debugging determinism.
- **v1-profile caveat (my synthesis)**: v1/live-weather profiles never go through `GetWeatherAt` — `World.Weather` is set once. The wander application must therefore run per second for *any* weather source, and the **mean** wind must survive: either keep the base profile separate or store mean+spread fields alongside the effective `Direction` so re-application is idempotent and snapshots round-trip the mean.

## User decisions (2026-08-10, via AskUserQuestion)

1. **Architecture: per-aircraft phase (Tier 2).** Thread sim time + a stable per-aircraft phase into the wind lookup so each aircraft sees a decorrelated perturbation. Accepts the `FlightPhysics.Update`/`WindInterpolator` signature change (~224 test call sites / 51 files).
2. **Reported METARs observe the simulated field**: sample the instantaneous wind over trailing 2-min/10-min windows (ASOS 5-s grid) at issuance and apply FMH-1 coding rules — VRB / dddVddd / G groups all emerge from one code path.
3. **All add-ons in scope**: approach gust additive (Vref + half gust increment, cap +20 kt), squall SPECI + PK WND remark, and the pre-existing ground-branch-ignores-wind gap.

## Design (synthesis of the two design-agent reports)

Full detail lives in the companion files (same directory):
- `…-agent-a9b1c74caebec3d03.md` — implementation plan (written for the global-profile variant; step decomposition and parsing/reporting/client sections remain valid, the wiring section is superseded by Tier 2 below)
- `…-agent-a855568c3f51af225.md` — aviation realism spec (constants, derivations, FMH-1 rules, acceptance criteria) — **authoritative for all numbers**

### Core model (aviation spec)

Perturb the wind as a **Cartesian (u,v) vector in the mean-wind frame** — `W(t) = W_mean + σᵤ·Nᵤ(t)·û + σᵥ·Nᵥ(t)·v̂` — with `Nᵤ,Nᵥ` independent band-limited value noise (3 octaves, ratio 3, amplitude ratio 3^(−1/3), finest floored at 4 s; base period `clamp(432/U_kt, 12, 120)` s; plus a lateral-only 300 s meander octave). VRB, gust/direction correlation, and the 6-kt VRB threshold all *emerge* — no special cases. Bounded noise ⇒ reported gust is a hard ceiling (testable invariant). Key constants (all in one `WindVariation` static class; see spec §7 for the full table + derivations): `DefaultTurbulenceIntensity=0.18`, `GustPeakFactor=2.75`, `LateralToLongitudinalSigmaRatio=0.90`, `SpreadToSigmaThetaFactor=4.0` (weakest evidence — **calibrate via the round-trip test, don't trust a priori**), `LullAsymmetryFactor=0.65`, VRB: `VrbMeanVectorFraction=0.35`, `VrbIsotropicSigmaFactor≈0.80` (Rayleigh-derived), `VrbBaseEddyPeriodSeconds=240`. AGL taper: full amplitude ≤1000 ft AGL, smoothstep to zero at 3000 ft; winds-aloft layers steady unless explicitly authored with variability/gusts. Authoring clamps per spec §7.

### Architecture (Tier 2)

- **Mean wind stays exactly where it is**: `WindLayer.Direction/Speed` remain the authored mean; `World.Weather` collapse sites, `HasMeaningfulChange`, snapshots — all untouched. The mean/instantaneous separation is structural, so no cloned profiles or `MeanDirection` shadow fields are needed.
- **The perturbation is computed inside the wind lookup**: `WindInterpolator.GetWindAt`/`GetWindComponents`/`ComputeWindCorrectionAngle` gain required (no optional params — repo rule) `double simTimeSeconds` + `double phaseSeconds` arguments; `FlightPhysics.Update` and `SimulationWorld.Tick` thread them through. Per-aircraft `phaseSeconds` = stable **callsign hash** (FNV-1a, never `string.GetHashCode`) mapped into `PerAircraftPhaseSpanSeconds=3600`; survives snapshot restore/replay by construction. Windsock/METAR/observation sampling uses phase 0. σᵤ/σᵥ interpolate between layers with the same N/E scheme as the mean vector, AGL taper applied last (spec invariant).
- **Determinism**: pure closed-form `f(simTime, seed, layer, phase)`; forbidden: `SerializableRandom` draws, incremental walks, wall-clock. Layer seed = FNV-1a of `WindLayer.Id` ⊕ altitude bits. Replay re-ticks the same `t` sequence → no new `RecordedAction`, no snapshot state.
- **Second physics consumer**: `HoldingPatternPhase` (`:305-311`, `:346-352`) already has `ctx.ScenarioElapsedSeconds` — pass the aircraft's phase alongside.
- **Forward invariants** (state in docs; no such logic exists yet): runway selection, crosswind limits, approach-stability gates must read the **mean**, never the instantaneous wind.

## Implementation steps (each independently buildable/testable)

**Step 0 — mechanical refactor commit (split from feature, per repo convention).** Thread `simTimeSeconds` + `phaseSeconds` through `WindInterpolator` public API, `FlightPhysics.Update` overloads (`FlightPhysics.cs:37-59`), `SimulationWorld.Tick` (`SimulationWorld.cs:236-291`; engine passes `Scenario.ElapsedSeconds`), `HoldingPatternPhase`. Pass real values but compute **zero perturbation** (engine not written yet) → behavior-neutral; all 224 call sites updated; full suite green; commit as `ref:`.

**Step 1 — parsing + model fields.** `MetarParser`: parse `dddVddd` into new `ParsedMetar` fields `WindVarFromDeg`/`WindVarToDeg` (`int?`) + `bool WindVariable` for VRB (preserve "was VRB" instead of only null direction). `WindLayer`: add `double? DirectionVariabilityDeg` (half-spread) + `bool? Variable`. Carry both through `WeatherTimeline.InterpolateWindLayers` and add them + `Gusts` to `HasMeaningfulChange`. Tests: `MetarParserTests` (incl. clockwise-arc assertion: `280V350` = 70° arc; `350V280` rejected), `WeatherTimelineTests`.

**Step 2 — `WindVariation` engine, unwired.** New `src/Yaat.Sim/WindVariation.cs`: value-noise, octaves, (u,v) model, σ resolution order (gust→σᵤ, spread→σᵥ, cross-derive only as fallback — never when both present), VRB isotropic mode, AGL taper, clamps. New `WindVariationTests`: determinism (same inputs → same output), bounds (never exceeds gust ceiling / V-group arc), zero-variability layers bit-identical passthrough, seed stability.

**Step 3 — wire into physics.** Replace Step 0's zero-perturbation stub with `WindVariation` inside `WindInterpolator`. Tests: `WindPhysicsTests` extension (GS/track wobble appears with variability, absent without), replay determinism E2E (record → replay → identical), existing recordings (no authored variability) unchanged.

**Step 4 — observed-METAR reporting.** `MetarIssuer`: at issuance, sample the phase-0 field over trailing windows (24×5 s for mean/spread, 120×5 s for peak/lull — pure function, evaluable at negative t at scenario start) and apply FMH-1 coding: VRB paths (≤6 kt + ≥60°; >6 kt + ≥180° → VRB required, no V-group), dddVddd (>6 kt, 60–179°, extremes clockwise + converted to TRUE like the mean), gust (≥10 kt peak-to-**lull** over 10 min), calm <1 kt (fixes current ≤2 threshold). `ReportedConditions` gains variability fields; `MetarComposer.ComposeWind`/`PatchWind` emit VRB/dddVddd instead of stripping. `SpeciCriteria`: wind-shift compares 2-min means (+ regression test: variability alone never fires it over 60 min); add squall SPECI (TBL 7-1-1 item 7) + PK WND remark (>25 kt). Fix `SampleWind` VRB-as-calm text-fallback bug (`MetarIssuer.cs:172-177`).

**Step 5 — approach gust additive.** Pilots fly approach at `Vref + min(GustKt − MeanKt, 20)/2` (Boeing FCTM/Airbus FCOM technique) — a mean speed increase where final-approach target speed is set (likely `FinalApproachPhase`/`AircraftPerformance` approach-speed resolution; locate exactly during implementation). Controller-visible via spacing; test pins the additive.

**Step 6 — ground-branch wind (pre-existing gap; land LAST, isolated).** `FlightPhysics.UpdatePosition` ground branch (`:1022-1031` area) currently moves at IAS with track=heading. Fix so ground roll treats wheel speed as groundspeed and IAS = GS + headwind component (rotation at Vr IAS ⇒ GS at rotation reduced by headwind; ASDE-X GS correct). **Risk**: changes trajectories for every windy recording → run full replay suite; desynced recordings handled per the ship-fix-delete-desynced-recordings policy. Aviation review this step specifically.

**Step 7 — client + live weather.** `WeatherDisplayInfo.ToDisplayString` renders the dddVddd group (VRB already renders); weather editor (`WeatherPeriodViewModel`, `WeatherTimelineEditorWindow.axaml`) gains variability/Variable columns; `LiveWeatherService.BuildSurfaceWindLayer` stops dropping VRB stations (average speeds; all-VRB → `Variable` layer; mine spread from `RawOb` via the extended parser).

**Step 8 — calibration + acceptance (spec §9).** Round-trip tests that TUNE constants (never the tests): author `21015KT 180V240` → 30 min of simulated 2-min observations → median spread 60±10°, direction 210±10°, speed 15±1.5 kt (calibrates `SpreadToSigmaThetaFactor`); `18G28KT` → peak 28±2, mean 18±1.5, lull ≥10; `VRB04KT` → scalar mean 4±0.6, direction coverage >270° (pins VRB constants). **Dense**-traffic conflict E2E: a scenario conflict-free under steady wind stays alert-free under gusty wind (conflict-detector changes have emergent timing — dense, not two-aircraft). Then `pwsh tools/test-all.ps1` (cross-repo).

**Step 9 — docs.** `docs/weather-and-wind.md`: new WindVariation section, delete the "Gusts stored but never applied" footgun, fix stale line refs (WCA now `FlightPhysics.cs:268-274`, UpdatePosition `:1156-1199`, snapshot `SimulationEngine.cs:210/:223`), document mean-vs-instantaneous invariants + forward invariants. `docs/architecture.md` entry for `WindVariation.cs`. `USER_GUIDE.md` (weather editor variability authoring). `CHANGELOG.md`. COMMANDS.md unaffected (no new commands).

**Aviation-realism review gate**: steps 3, 4, 5, 6 get `aviation-sim-expert` review before commit (mandatory scope); the spec agent's report is the design-time half of that obligation.

## Risks / notes

- Step 0's 224-call-site sweep is big but mechanical; committing it separately keeps the feature diff reviewable.
- Wire DTO (`WindLayerDto`) can optionally gain variability fields, but nothing client-side consumes wind layers today — METAR text is the display channel, so it's not required for the feature.
- Authored METAR text saying VRB with steady layers: the issuer prefers layers; auto-mining base-METAR variability into layers at `LoadWeather` is possible follow-up scope, not in this plan.
- No windsock/tower-cab wind rendering exists; visible-crab payoff is follow-on UI, not part of done.
- Gust fronts / frontal passage (coupled veer+gust) are deliberately excluded — that's a scripted mean-wind shift on a timeline, not stationary turbulence.

## Verification

1. Per-step: targeted `dotnet test --filter` runs (30 s timeout) as listed above; build with `-p:TreatWarningsAsErrors=true`.
2. Replay determinism: record a variable-wind session → replay → `SnapshotDiff` clean; rewind to arbitrary t and resume.
3. Non-regression: zero-variability scenarios bit-identical (steady-wind E2Es untouched); existing recordings replay green until Step 6, which is isolated + handled explicitly.
4. Calibration acceptance tests of Step 8 are the realism definition-of-done.
5. Final: `pwsh tools/test-all.ps1` (both repos), `prek run` before each commit.
