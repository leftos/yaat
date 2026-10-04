# Taxi append

A taxi command that extends the aircraft's current taxi route instead of replacing it. The part of the queued route the aircraft has not covered stays as cleared, and the new taxiways (and optionally a new destination) are appended after it. It is the ground counterpart of the direct-to append, `ADCT`. Index line: [MAIN.md](./MAIN.md), under **Programmes next up**. It starts after the next release is cut (user 2026-10-01).

## Map (exploration 2026-10-01, main @ da27e847)

- **The model, `ADCT`** (`CanonicalCommandType.AppendDirectTo`; `CommandRegistry.cs` ~:382; `CommandParser.ParseAppendDirectTo` ~:1521; `FlightCommandHandler.ApplyAppendDirectTo` ~:856).
  - It appends to the tail of the self-consuming `NavigationRoute`, so there is no splice.
  - Readback: "Then direct X".
  - Tests: `AppendDirectToTests`, `AdctParserTests`.
- **The ground precedent for mutating a live route** is `GroundCommandHandler.TryExtendRouteAcrossDestinationRunway` (~:5113, bare `CROSS` at the destination bar).
  - It runs `Segments.AddRange`, merges the new hold-shorts (skipping nodes that already have one), and re-labels the old `DestinationRunway` bar as a pre-cleared `RunwayCrossing`.
  - It calls neither `NotifyTaxiHoldShortsChanged` nor `RefreshSpeedConstraints`. Whether the braking plan goes stale there is unchecked.
- **Route model** (`TaxiRoute.cs`).
  - Progress is `CurrentSegmentIndex`, so "not yet covered" is `Segments[CurrentSegmentIndex..]`.
  - A runway destination is a `DestinationRunway` hold-short at `Segments[^1].ToNodeId`.
  - `DestinationSpot` and `DestinationParking` are `init`-only, so a new destination means a new `TaxiRoute` (as `SetDestination` ~:3000 does).
  - `TaxiingPhase` re-reads the route every tick. After an edit it needs `RefreshSpeedConstraints` and `NotifyHoldShortsChanged`.
- **Building the tail.**
  - `ResolveExplicitPath(layout, Segments[^1].ToNodeId, names, …)` with `StartHeadingTrue = Segments[^1].Edge.ArrivalBearing`.
  - The expander's head cannot be seeded with the prior edge, so the seam's reversal and admissibility check (jet 135°, `GeometricAdmissibility`) must be added or checked separately.
  - Hold-shorts:
    - The fresh sub-route annotates its own.
    - Merge them, skipping nodes that already have a hold-short.
    - Then run `ComputeHoldShortPositions` on the whole route.
    - Keep `AddImplicitRunwayHoldShorts` and the materialiser on one `RouteCrossesRunwayAfterStart`.
- **What a plain `TAXI` drops** (`TryTaxiCore` ~:632-654): a new `PhaseList`, so stored `CTO`/`LUAW`, the landing clearance, `Ground.Hold`, `CommandedTaxiSpeedKts` and every cleared crossing. An in-place append keeps all of these, which is its main value over re-issuing `TAXI`.
- **Grammar.**
  - Use a new verb that reuses `TaxiCommand`'s parse (path tokens, `HS`, `CROSS`, `RWY`, `@`, `$`), not a `TAXI` modifier. Every ground phase answers `Taxi` with `ClearsPhase`, and `CanAcceptCommand` cannot see a modifier.
  - The new verb needs an explicit arm in each ground phase.
  - `ATXI` is air taxi.
  - The registration checklist is the Task Index's "Add a new command" row.
- **Client.** The overlay re-resolves from the DTO's taxiway string, so it follows an append with no client change. The caveat is its first-`CurrentTaxiway` trim when the append revisits the current taxiway (unverified).
- **Phases.**
  - `TaxiingPhase` and `HoldingShortPhase` can take it. At a hold, edit the route without clearing the hold or its resume chain.
  - `RunwayExitPhase` owns a private exit route, so appending there would not move the aircraft.
  - `FollowingPhase` and `PushbackPhase` have no route to extend.
  - Parked or completed routes have nothing left.
- **Overlap.** The work shares files with Wave 2's ground grammar (`CommandRegistry`, `GroundCommandParser`, `CommandDispatcher`, `GroundCommandHandler`, `HoldingShortPhase`). Sequence it after Wave 2, or with it.

## Decisions (user 2026-10-01)

- **A named new destination replaces the old one.** The aircraft drives through the old destination. A spot or parking stop is dropped. A runway bar on the way follows decision 3 below.
- **No remaining route falls back to `TAXI`.** If the aircraft has no route left (stopped at the end, parked, pushing back, following), the append acts as a plain `TAXI` from where it stands. That path installs a fresh `PhaseList`, so the response must say what it dropped, the same way as the drop-and-warn backlog line for a same-runway re-taxi.
- **Atomic.** If the appended taxiways do not connect to the end of the remaining route, the append is refused, the route is left unchanged, and the reply names the taxiway that fails to connect.
- **Feature branch** `feat/taxi-append`: it opens when the work starts, after the release, and is sequenced after Wave 2's ground grammar items.
- **Verb** (user): `TAXIA` ("taxi append"), beside `TAXIALL` and `TAXIAUTO`. ATCTrainer's command list has no append form; `ADCT` is the airborne precedent.

## Open decisions

1. (decided above: `TAXIA`)
2. (decided above)
3. What happens to an old runway destination's bar: re-armed as an explicit hold-short, converted to a cleared crossing (the `CROSS` precedent), or dropped? This includes what a stored `CTO`/`LUAW` does when the aircraft reaches it (`aviation-sim-expert`).
4. Readback wording (7110.65 §3-7-2, AIM 4-3-18; `ADCT` says "Then …"), for the response and for the solo pilot (`aviation-sim-expert`).
5. (decided above: fall back to `TAXI`)
6. `CROSS` in an append clears only the crossings the appended part adds, never a bar already on the route (`aviation-sim-expert`).
7. (decided above: atomic)
8. Which phases accept it as an append: Taxiing and HoldingShort, and `RunwayExitPhase`, whose exit route the append extends after the exit's end (user); an exit not yet committed has no end, so it falls back to `TAXI` from the exit. `GIVEWAY`, `NODEL` and the taxi speed carry onto the appended part (user).

## Task Index rows for the landing commit

- **Edit a live taxi route** (append, splice): `GroundCommandHandler` (`TryTaxiCore`, `TryExtendRouteAcrossDestinationRunway`, `TryAddExplicitHoldShorts`) → `TaxiRoute` → `HoldShortAnnotator` → `TaxiingPhase` (`NotifyHoldShortsChanged`, `CompleteRoute`) → `GroundNavigator.RefreshSpeedConstraints` → each ground phase's `CanAcceptCommand` → `docs/ground/*.md`.
- **Ground command grammar**: `GroundCommandParser.ParseTaxiTokens` → the `CommandRegistry` TAXI entry → `CommandSchemeParser`.
