# Plan: Adopt structured `RunwayIdentifier` equality everywhere (kill raw runway-string compares)

## Context

Spun out of issue #193 triage (the #193 fix itself was unrelated — a LineUp
turn-magnitude bug). While investigating, a real **latent** bug surfaced: runway
designators are compared as raw strings in several command/phase code paths, which
breaks at **single-digit-runway airports** (KMIA 8R/8L/9, etc.).

Root of the mismatch: the vNAS airport-map GeoJSON names runways **without** leading
zeros (`"8R - 26L"`, `"9 - 27"`), so the ground graph carries `"8R"`; the CIFP/NavData
`RunwayInfo` normalizes single-digit runways to the **zero-padded** canonical form
(`"08R"`). Most ground-graph comparisons already go through `RunwayIdentifier`
(`src/Yaat.Sim/Data/Airport/RunwayIdentifier.cs`), which normalizes (`Contains`,
`Equals`, `==`, `Overlaps`, `NormalizeDesignator`; canonical = `"08R"`) — that is why
taxi/auto-taxi already works (and the recently-shipped leading-zero taxi fix). But
several command/phase sites compare with **raw `string.Equals(x, runway.Designator, …)`**
and would behave inconsistently when one side is `"8R"` and the other `"08R"`.

## The abstraction already exists — this is adoption, not new design

`RunwayIdentifier` (readonly struct) already provides exactly what the user asked for:
order-independent `Equals`/`==`/`!=`, `GetHashCode`, `Contains(designator)`,
`Overlaps(other)`, `Parse(input)`, and `NormalizeDesignator(s)`. `RunwayInfo` holds a
`RunwayIdentifier Id` and a normalized `Designator`. The work is to route the raw-string
comparisons through it (or normalize at the boundary), and to consider an `IsSameRunway`
convenience where a `RunwayInfo`/string is compared to another.

## Known raw-string comparison sites (audit seeds — verify + expand)

- `src/Yaat.Sim/Commands/ApproachCommandHandler.cs:461`, `:816`
- `src/Yaat.Sim/Commands/NavigationCommandHandler.cs:993`
- `src/Yaat.Sim/Commands/GroundCommandHandler.cs:1018`
- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:2031`
- `src/Yaat.Sim/Phases/Pattern/VfrFollowPhase.cs:473`
- `src/Yaat.Sim/Phases/ApproachEvaluator.cs:148`, `:169`

Scope signal: `Designator|RunwayId|DepartureRunway|DestinationRunway|AssignedRunway|
ClearedRunwayId|PatternRunway|End1|End2` matches ~957 occurrences across ~90 files in
`src/Yaat.Sim` — most are not comparisons; triage each comparison on merits.

## Deliverable

1. Codebase-wide audit of every place runway ids are compared with `==`/`string.Equals`/
   `Contains`/`StartsWith` (use ast-grep for structure where possible).
2. Migrate each to `RunwayIdentifier`-based comparison (or normalize the free-form
   string with `RunwayIdentifier.NormalizeDesignator` at the boundary). Consider adding
   `RunwayInfo.IsSameRunway(string|RunwayInfo)` if it reads cleaner than `Id.Contains(...)`.
3. TDD: a test airport (or synthetic) with a single-digit runway exercising approach
   clearance (`CAPP 8R`), pattern (`PT 9`), and nav commands so the regressions are
   pinned; verify red→green where the raw compare currently mis-resolves.
4. Per the unreleased-software rule, no compatibility shims — replace the raw compares.

## Notes

- Split refactor from feature (already separate from the #193 commit).
- The recently-shipped CHANGELOG bullet ("A runway named without its leading zero …")
  fixed only the **ground-taxi** path; this refactor covers the remaining
  command/phase paths.
