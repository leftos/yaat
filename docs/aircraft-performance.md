# Aircraft Performance & Contributor Corrections

How YAAT resolves per-type aircraft performance, and how to submit a correction when a type
flies wrong (climbs too fast, approaches too fast, wrong ceiling, etc.).

## Where performance comes from

`AircraftPerformance` (`src/Yaat.Sim/AircraftPerformance.cs`) is the single entry point for every
performance value the simulation needs (climb/descent speeds and rates, approach/pattern speeds,
accel/decel, turn rate, ceiling). It resolves each value through four layers, in order:

1. **`AircraftProfiles.json`** (`src/Yaat.Sim/Data/`) — ~160 per-type profiles derived from
   Eurocontrol **BADA** data. This is the bulk source. `AircraftProfileDatabase.Get(type)` returns
   one, stripping prefixes (`H/`) and falling back to a **sibling** type
   (`AircraftProfileSiblings.json`, e.g. `B789 → B788`) when there is no direct entry.
2. **FAA ACD** (Aircraft Characteristics Database, fetched + cached per-AIRAC) — authoritative
   certification **Vref** (approach speed), engine class, and engine count.
3. **`EurocontrolProfileCorrectionAdapter`** — corrects six profile fields at runtime using the ACD
   Vref as ground truth, because the BADA approach speeds run ~20% high. Corrected fields:
   `FinalApproachSpeed`, `PatternSpeed`, `BaseSpeed`, `InitialApproachSpeed`, `ClimbSpeedInitial`,
   `ClimbRateInitial`.
4. **`CategoryPerformance`** — generic per-category (Jet / Turboprop / Piston / Helicopter) constants,
   used when a type has no profile and no sibling.

The problem this leaves: a type with **no profile and no sibling** falls all the way to the generic
category default. The **Cirrus SF50 Vision Jet** is the canonical example — with no profile it climbed
at the generic jet default of **250 KIAS** instead of its real ~170 KIAS.

## The override layer

`AircraftProfileOverrides.json` (`src/Yaat.Sim/Data/`) is a committed, hand-curated layer of
**partial, authoritative** corrections that sit on top of everything above.

- **Partial** — every field is optional. Specify only what you want to correct; everything else is
  left to the layers above (or, for a no-profile type, to a category baseline). A `null`/absent field
  is *not* an explicit `0`.
- **Authoritative** — a field you set wins over the Eurocontrol/ACD rescaling. This is essential:
  the SF50's real 170 kt climb would otherwise be capped to ~122 kt (ACD Vref 87 × the jet climb-speed
  multiplier). `OverrideAwareProfileCorrectionAdapter` returns overridden fields verbatim and only
  delegates the rest to the Eurocontrol adapter.

At startup `AircraftProfileDatabase.Initialize(baseProfiles, overrides)` merges each override onto a
base profile and stores the effective result, so `Get(type)` and all of `AircraftPerformance` work
unchanged. The base it merges onto is:

- the type's **own profile** if it has one, else
- its **sibling's** profile (stamped with this type code), else
- a **category baseline** synthesized by `CategoryPerformance.BaselineProfile(cat)` — sane
  category-typical values for every field, so an override on a no-profile type (SF50) still produces
  a complete profile.

Only types that appear in the override file are affected; every other type behaves exactly as before.

**Init order matters.** `AircraftProfileDatabase.Initialize` must run *after* `AircraftSiblingMap.Initialize` and
`AircraftCategorization.Initialize` — sibling resolution and the no-profile category-baseline merge both depend on them.
`VnasDataService.InitializeAircraftProfiles` and `TestVnasData.EnsureInitialized` both init siblings/categorization before
profiles for this reason; a caller that initializes `AircraftProfileDatabase` standalone gets silent sibling/category fallback
misses instead of a clear error.

## Submitting a correction

1. Find the ICAO type designator (e.g. `SF50`, `C172`, `B738`).
2. Add or edit an entry in `src/Yaat.Sim/Data/AircraftProfileOverrides.json`:

   ```jsonc
   {
     "typeCode": "SF50",
     "note": "Cirrus Vision Jet — climbs ~170 KIAS, not the 250 generic-jet default. Source: ...",
     "climbSpeedInitial": 170,
     "climbRateInitial": 1500,
     "ceiling": 31000
   }
   ```

   - `typeCode` is required; `note` documents the correction and its source.
   - Speeds are **KIAS**, except a value `< 1.0` is treated as a **Mach** number (high-altitude
     climb/descent), matching `AircraftProfiles.json`.
   - `cruiseSpeed` is **TAS in knots** measured at `cruiseAltitude` (ft MSL).
   - Climb/descent are altitude-banded: `climbSpeedInitial` (below ~10k), `climbSpeedFl150`,
     `climbSpeedFl240`, `climbSpeedFinal`; rates mirror this. `0` in a banded field means "can't reach
     that altitude".
   - Don't override what the pipeline already gets right. Approach speed usually comes correctly from
     the FAA ACD Vref — only override `finalApproachSpeed` if the ACD value is wrong or missing. A `finalApproachSpeed` override also moves the type's final-approach and RFAS speeds, and its minimum speed (`MinOperatingIasKts`).
3. Run the validation + behavior tests:

   ```bash
   pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj -- --filter-method "*AircraftProfileOverrideTests*"
   ```

   `OverridesJson_LoadsAndIsSane` guards every entry (type resolvable, values in plausible ranges).
4. Aviation-validate your numbers (the `aviation-sim-expert` agent, or POH/AFM data). Performance
   numbers should be realistic for the type, not guessed.

### Fields

All fields are nullable. The full set mirrors `AircraftProfile`:

`isProp`, `isHelo`, `isHeavy`, `isSpeedLimitWaived`, `airborneAccelRate`, `airborneDecelRate`, `groundAccelRate`, `takeoffDistance`, `rotateSpeed`.

Two more fields are not profile fields: `vmo` (KCAS) and `mmo` (Mach) give the type's maximum operating speeds ([Speed limits](#speed-limits-vmo-and-mmo)). `ApplyTo` ignores them.

One field corrects the FAA aircraft characteristics record instead of the profile: `mainGearWidthFt` replaces the record's main-gear width (BT36's 12.8 ft is the BE36's 9.6). `AircraftProfileDatabase.Initialize` hands the overrides to `FaaAircraftDatabase.ApplyOverrides`, which keeps the corrections and applies them again on every `FaaAircraftDatabase.Initialize`, so either load order keeps them. An override with a blank type, a width of zero or less, or a type with no FAA record of its own is logged and not applied. `ApplyTo` ignores the field, and an override that sets nothing else adds no profile entry (the type keeps its sibling fallback).

The climb and cruise fields are `climbSpeedInitial`, `climbSpeedFl150`, `climbSpeedFl240`, `climbSpeedFinal`, `climbRateInitial`, `climbRateFl150`, `climbRateFl240`, `climbRateFinal`, `cruiseSpeed`, `cruiseAltitude`, `ceiling`.

The descent, landing and remaining fields are `descentSpeedInitial`, `descentSpeedFl100`, `initialApproachSpeed`, `descentRateInitial`, `descentRateFl100`, `descentRateApproach`, `finalApproachSpeed`, `landingSpeed`, `landingDistance`, `patternSpeed`, `holdingSpeed`, `length`, `standardTurnRateOverride`.

`groundAccelRate` is also optional in the *bulk* profile: BADA has no such field, so the category value applies unless a type was hand-checked — only F15/F16/F18/F22 and P3 carry one. Note the spool ramp's idle rate and spool time are per *category* (`GroundRollProfile.For`), so a per-type steady rate cannot express a brakes-held static takeoff with thrust set before brake release — the fighters ramp 1.0 → 10 kt/s over 5 s like a rolling takeoff.

**Descent-ladder orientation.** In these Eurocontrol-derived profiles `descentRateInitial` is the *initial descent from
cruise* — the cruise-end rate (a gentle pushover, ~800 fpm for a B738) — exactly as `descentSpeedInitial` is a Mach number,
whereas `climbRateInitial` is ground level. `AircraftPerformance.DescentRate` interpolates `descentRateApproach` at 0 ft →
`descentRateFl100` at 10,000 ft → `descentRateInitial` at `ceiling`. Anchoring "Initial" at 0 ft (a copy of the climb
ladder) inverts both tails of every jet baseline.

## Speed limits (Vmo and Mmo)

`AircraftPerformance.MaxOperatingIasKts(type, altitude)` is the fastest the type may be flown: the lower of Vmo and Mmo's IAS equivalent at that altitude, or Vmo alone where the type has no Mmo (a helicopter). It is null for a type with neither figure, and then nothing is refused or capped for it. `MaxOperatingMach(type)` is Mmo alone, and `MinOperatingIasKts(type, category)` is the type's minimum speed at every altitude (a helicopter has no minimum). It resolves as the FAA approach speed (ACD) when the record has one, else a `finalApproachSpeed` override, else the profile's final approach speed divided by 1.2 (`ProfileFinalApproachSpeedHighFactor`, since the raw Eurocontrol figure sits about 20% above Vref), else the category default.

The physics cap and the unable refusal both read these ([flight-physics.md](flight-physics.md), COMMANDS.md).

Vmo and Mmo come from two sources, an override figure first:

1. **An override entry's `vmo` (KCAS) and `mmo`** in `AircraftProfileOverrides.json`, from the type certificate data sheet (TCDS) or OpenAP. They are per exact type with no sibling fallback, and an entry carrying only `vmo`/`mmo` creates or alters no profile. Where the TCDS gives Vmo or Mmo in altitude bands, the entry takes the band maximum. A helicopter's `vmo` is its own Vne, so it flies that figure rather than the DUMH class's 150 kt.
2. **The type's speed envelope class** from `Data/AircraftSpeedEnvelopeClasses.json`, a flat `{ "A320": "J2M" }` table over the profile types (a type with no entry takes its sibling's class).

   The class figures live in `AircraftSpeedEnvelope` and are the dummy aircraft models shipped with EUROCONTROL's pybada: BZJT 292 kt / M0.755, J2M 340 / M0.82, J2H 335 / M0.82, J4H 365 / M0.92, TP2M 250 / M0.55, GA 126 / M0.24, DUMH (helicopters) 150 kt and no Mmo. A class code that names no class fails the load.

Either figure is then raised to the highest IAS and Mach the type's own profile schedule flies (`ProfileScheduleFloor`), sampled every 100 ft from the ground to the ceiling and at the schedule's band altitudes (10,000 ft, FL150, FL240, cruise altitude, ceiling), so the limit never falls below a speed the profile itself uses. The result is cached per type and cleared whenever the profiles, the envelope classes, the categorization or the correction adapter is replaced.

## Files

| File | Role |
|------|------|
| `Data/AircraftProfiles.json` | BADA-derived base profiles (bulk). |
| `Data/AircraftSpeedEnvelopeClasses.json` | Type → speed envelope class code (Vmo/Mmo source). |
| `AircraftSpeedEnvelope.cs` | The class figures and `ClassFor` (own entry, else sibling's). |
| `Data/AircraftProfileSiblings.json` | Type → nearest-sibling fallback map. |
| `Data/AircraftProfileOverrides.json` | **Authoritative partial corrections (edit this to fix a type).** |
| `Data/AircraftProfileOverride.cs` | Override DTO + `ApplyTo` partial-merge. |
| `Data/AircraftProfileDatabase.cs` | Loads/merges profiles + overrides; `Get`, `IsOverridden`. |
| `Data/EurocontrolProfileCorrectionAdapter.cs` | Runtime ACD-anchored correction of six fields. |
| `Data/OverrideAwareProfileCorrectionAdapter.cs` | Wraps the above; overridden fields bypass it. |
| `AircraftCategory.cs` | `CategoryPerformance` constants + `BaselineProfile(cat)`. |
| `AircraftPerformance.cs` | The unified lookup used by the simulation. |
