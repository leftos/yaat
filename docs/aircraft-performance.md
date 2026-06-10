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
     the FAA ACD Vref — only override `finalApproachSpeed` if the ACD value is wrong or missing.
3. Run the validation + behavior tests:

   ```bash
   dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter "FullyQualifiedName~AircraftProfileOverrideTests"
   ```

   `OverridesJson_LoadsAndIsSane` guards every entry (type resolvable, values in plausible ranges).
4. Aviation-validate your numbers (the `aviation-sim-expert` agent, or POH/AFM data). Performance
   numbers should be realistic for the type, not guessed.

### Fields

All fields are nullable. The full set mirrors `AircraftProfile`:

`isProp`, `isHelo`, `isHeavy`, `isSpeedLimitWaived`, `airborneAccelRate`, `airborneDecelRate`,
`groundAccelRate`, `groundDecelRate`, `takeoffDistance`, `rotateSpeed`, `climbSpeedInitial`,
`climbSpeedFl150`, `climbSpeedFl240`, `climbSpeedFinal`, `climbRateInitial`, `climbRateFl150`,
`climbRateFl240`, `climbRateFinal`, `cruiseSpeed`, `cruiseAltitude`, `ceiling`, `descentSpeedInitial`,
`descentSpeedFl100`, `initialApproachSpeed`, `descentRateInitial`, `descentRateFl100`,
`descentRateApproach`, `finalApproachSpeed`, `landingSpeed`, `landingDistance`, `patternSpeed`,
`holdingSpeed`, `length`, `standardTurnRateOverride`.

## Files

| File | Role |
|------|------|
| `Data/AircraftProfiles.json` | BADA-derived base profiles (bulk). |
| `Data/AircraftProfileSiblings.json` | Type → nearest-sibling fallback map. |
| `Data/AircraftProfileOverrides.json` | **Authoritative partial corrections (edit this to fix a type).** |
| `Data/AircraftProfileOverride.cs` | Override DTO + `ApplyTo` partial-merge. |
| `Data/AircraftProfileDatabase.cs` | Loads/merges profiles + overrides; `Get`, `IsOverridden`. |
| `Data/EurocontrolProfileCorrectionAdapter.cs` | Runtime ACD-anchored correction of six fields. |
| `Data/OverrideAwareProfileCorrectionAdapter.cs` | Wraps the above; overridden fields bypass it. |
| `AircraftCategory.cs` | `CategoryPerformance` constants + `BaselineProfile(cat)`. |
| `AircraftPerformance.cs` | The unified lookup used by the simulation. |
