# YAAT-658 ruling: `EXP` on TAXI, TAXIAUTO and CROSS

Status: adopted by the owner as drafted, every open question below taking its recommendation; defects A to C are fixed inside YAAT-658, D is its own plan item. Aviation review by aviation-sim-expert, read against the local 7110.65 / AIM.

## What the FAA gives us

- **7110.65 §3-7-2.j** (the md file's item 10 of 3-7-2): "Issue instructions to expedite a taxiing aircraft or a moving vehicle. PHRASEOLOGY- TAXI WITHOUT DELAY (traffic if necessary). EXIT/PROCEED/CROSS (runway/taxiway) at (runway/taxiway) WITHOUT DELAY." This one paragraph covers all three commands. It is the whole FAA basis for the modifier.
- **7110.65 §2-1-5.b**: "Use the word 'expedite' only when prompt compliance is required to avoid the development of an imminent situation. If an 'expedite' climb or descent clearance is issued by ATC, and subsequently the altitude to maintain is changed or restated without an expedite instruction, the expedite instruction is canceled." The ground phrase is therefore "without delay", never "expedite". The cancel-on-restatement rule is the model for how long a ground expedite lasts (by analogy only: the text names climb and descent).
- **7110.65 §3-7-3.a**: "Avoid clearances which require: a. Super or heavy aircraft to use greater than normal taxiing power." This is a controller duty, not a reason for the pilot to refuse (see 5).
- **P/CG EXPEDITE**: "...the approximate best rate ... should be used without requiring an exceptional change in aircraft handling characteristics." Expedite means brisk, not maximum effort, wherever a stop is required.
- **AIM 4-3-21.a** (exit "without delay") and the P/CG "Clear of the runway" (the tail past the holding position marking) set where a crossing ends.
- **No FAA figures exist** for taxi speeds, rates or durations. Every number below is a judgement call.

## 1. TAXI / TAXIAUTO with EXP

- **Target speed: keep the existing knob.** `TAXI ... EXP` / `TAXIAUTO ... EXP` sets `Ground.IsExpeditingTaxi`. That is the same flag the standalone ground `EXP` sets today (`FlightCommandHandler.ApplyExpedite`, :455-468). The straight-segment cap is `TaxiSpeed × TaxiExpediteMultiplier` (1.3): jet 39, turboprop 32.5, piston 26, helicopter 19.5 kt. This is the already-reviewed ceiling that `SPD` also clamps to (`MaxCommandedTaxiSpeed`). The new modifier must not create a second expedite regime. Judgement call.
- **Geometry still governs.** Corner speeds, the arc speed profile, the current-arc cap, turn-about ω·R, spot crawl and conflict/speed-limit caps all win, exactly as `TaxiingPhase.OnTick` (:194-197) already does. EXP raises only the straight-line ceiling.
- **Acceleration: a modest bump, without heavies.** I recommend a new `ExpediteTaxiAccelRate` of 1.5 kt/s for jets, turboprops and pistons. Super/Heavy (by FAA weight class) and helicopters stay at the validated 1.0. Judgement call: "without delay" from a standstill means more than breakaway thrust, and §3-7-3.a argues against it for heavies.

  This matters most for the crossing use case. A jet crossing from rest over about 600 ft takes about 26.7 s at 1.0 kt/s and about 21.8 s at 1.5 kt/s. See open question Q2.
- **Braking for a hold-short is unchanged.** A required stop is planned at the category taxi rate, and the existing ladder still applies: taxi rate, then firm rate, then the runway-bar dead stop (navigator.md § Stopping at an uncleared bar). The higher entry speed just starts the braking curve earlier. EXP never means harder braking for a bar the aircraft must stop at (P/CG EXPEDITE: "without ... exceptional change in handling").
- **Duration: the whole new route, ended by the following.**
  - Route completion: arrival at the destination, or the destination-runway hold short. `IsExpeditingLineup` is the separate knob for line-up.
  - HOLD, HS, GIVEWAY (already cleared at GroundCommandHandler.cs:4942/4947, :5831, :6060).
  - `SPD` (already, :307).
  - `RNS` and `NORM`.
  - Any new TAXI/TAXIAUTO without EXP (the §2-1-5.b restatement rule).
  - A release from a stop (RES, or a CROSS that releases a held aircraft) without EXP. RES already clears it (:5000).

  A pre-cleared crossing does **not** end it, and neither does a CROSS pre-clearance issued while the aircraft is rolling. It does not end at the next hold-short point by itself: stopping there is not a cancel, but the release that follows is.

## 2. CROSS with EXP ("cross runway 28R [at B] without delay")

- **Speed.** While crossing, the cap is the expedite cap (TaxiSpeed × 1.3). The `RunwayCrossingSpeed` floor is unchanged. Today `CrossingRunwayPhase` hard-codes `navigator.MaxSpeedKts = TaxiSpeed` (CrossingRunwayPhase.cs:509), so it ignores both expedite and `SPD`. Pre-existing defect A below.
- **No stop at the near bar.** A cleared crossing never stops at the near bar. A pre-cleared crossing is already entered from a moving `TaxiingPhase` (`BuildPreClearedCrossingPhases`, TaxiingPhase.cs:2043). EXP changes nothing here.

  A CROSS EXP issued before the bar starts the expedite at issuance: the aircraft does not slow for the bar beyond what the geometry needs. A CROSS EXP that releases a held aircraft starts at once (no reaction delay is modelled for any release).
- **Acceleration.** Same as item 1.
- **End.** The expedite ends when the crossing phase completes, which is the existing point ½ aircraft length past the far bar: tail past the far holding position marking, the P/CG "Clear of the runway" and AIM 4-3-21.a/b.

  For a multi-runway `CROSS 28L 28R EXP` (7110.65 §3-7-2.c, multiple crossings), it ends clear of the **last listed** runway. Between the runways the aircraft keeps expediting, because sitting between parallels is the exposure the controller is trying to shorten (judgement call).

  A route-scope expedite (`TAXI ... EXP`) still running continues past the crossing.
- **This needs a scope, not just the bool.** `IsExpeditingTaxi` alone cannot say "until clear of runway X". Use either an enum `ExpediteScope { None, Crossing, Route }` or a second flag that `CrossingRunwayPhase.HandRouteBack` clears. Either must be snapshotted (`AircraftGroundOpsDto`). Implementer's choice; I lean towards the enum.

## 3. TAXI EXP on the landing rollout (YAAT-334)

**Confirmed, with one amendment.**

- TAXI EXP on the rollout sets `IsExpeditingExit`. The implied exit (the route's first turn-off) is judged with `RolloutBraking.NamedExitBrakingLimit(category, expedite: true)`, the max-effort `ExpediteExitDecelRate`, through the same `JudgeExitReach` path as `EXIT <twy> EXP`. An exit that cannot be made even at that limit gets the same refusal as YAAT-334: refuse the whole TAXI with "unable {twy}".
- It also sets the route-scope taxi expedite for the deferred route. "Taxi without delay" given on the rollout means exit without delay *and* taxi without delay. `IsExpeditingExit` clears at exit completion as today (`RunwayExitPhase` :1419), so the taxi expedite carries on after it.
- **Amendment:** a TAXI **without** EXP on the rollout follows `KeepsStandingExpedite`.
  - It keeps a standing `IsExpeditingExit` only when its implied exit is the same taxiway already instructed under EXP.
  - Otherwise it clears the standing expedite, and the exit is judged at the firm rate, exactly as a bare `EXIT <other twy>` does today (landing-and-runway-exit.md:203).

## 4. Phraseology

### Controller forms the parser and STT should map

Today STT has no ground "without delay" or "expedite" rules at all. `PhraseologyRules.cs` maps only expedite climb and descent (:76-92), so the existing `EXP` exit modifier is not reachable by voice either.

| Spoken (controller) | Canonical | Basis |
|---|---|---|
| "taxi without delay" (standalone, to a taxiing aircraft) | `EXP` (existing ground path) | §3-7-2.j |
| "runway 28R, taxi via B C, without delay" | `TAXI B C RWY 28R EXP` | §3-7-2.j |
| "expedite taxi" / "expedite your taxi" | `EXP` | SttOnly. Non-standard under §2-1-5.b, but common |
| "cross runway 28R [at B] without delay" | `CROSS 28R EXP` | §3-7-2.j |
| "expedite crossing (runway 28R)" / "expedite across" | `CROSS [28R] EXP` | SttOnly, non-standard |
| "exit [left/right] [at W3] without delay" | `EL/ER/EXIT ... EXP` | §3-7-2.j (closes the existing gap) |

**Parser footgun.** `CROSS` takes a repeating runway list and a repeating `HS` list, and `TAXI` has a repeating `HS`/`CROSS`. `EXP` must be a reserved keyword that ends those lists. Otherwise `CROSS 28R HS B EXP` reads `EXP` as a hold-short target. The canonical (`CommandDescriber.FormatCrossCanonical` / `FormatTaxiCanonical`) must emit `EXP` and round-trip.

### Pilot readback

Use "without delay" everywhere, matching the existing exit (`CommandDescriber.cs:1260-1262`), LUAW WD and the standalone `EXP` readbacks ("taxi without delay", `PilotResponder.cs:793`, pinned by `ExpediteReadbackTests`). Reuse `PilotResponder.AppendWithoutDelayClause`.

- TAXI EXP: "runway 28R, taxi via B, C, without delay, hold short of runway 28L". The required hold-short readback (7110.65 §3-7-2.a, AIM 4-4-7) stays last as the safety-critical item. The FAA does not mandate the order; judgement call.
- CROSS EXP: "cross runway 28R at B, without delay". Multi-runway with HS: "cross runways 28L and 28R, without delay, hold short of runway 1L".
- An "expedite" spoken by the controller is still read back as "without delay". The pilot reads back the instruction; the wording follows the FAA phrase.

## 5. What the sim should refuse

- **PUSH / PUSHM / PUSHF EXP**: refuse as an unknown modifier, i.e. do not add EXP to their registry entries. §3-7-2.j does not cover tug moves (CLAUDE.md: the tug holds 5 kt). Standalone `EXP` during a pushback already refuses without a taxi route.
- **TAXIALL EXP**: do not offer it. It is an instructor bulk tool, and a blanket expedite of every parked aircraft is not an ATC instruction.
- **ATXI EXP**: do not offer it. AIM 4-3-17 makes air-taxi speed the pilot's choice ("The pilot is solely responsible for selecting a safe airspeed").
- **Heavy in a tight turn**: **no refusal.** The geometry caps already slow every type through corners and fillets. EXP raises only the straight-line ceiling, so there is nothing for the pilot to be unable to do.
- **Super/Heavy generally**: **accept, do not refuse.** §3-7-3.a is the controller's rule to avoid, and the trainer should let the trainee issue the clearance and see it flown.

  I recommend that heavies get no speed bump: cap at the normal jet `TaxiSpeed`, 30 kt, which is already at the top of wide-body practice. They should also get no acceleration bump. See Q3.
- **CROSS EXP with no crossing ahead**: the existing CROSS refusals apply unchanged.
- **EXP plus SPD in one compound**: no refusal. The two knobs are mutually exclusive, so the later one wins, as today.

## Pre-existing defects found (out of scope, for the plan)

- **A.** `CrossingRunwayPhase.cs:509` caps the crossing at `TaxiSpeed`, ignoring `IsExpeditingTaxi` and `CommandedTaxiSpeedKts`. An aircraft under the standalone `EXP` today slows from 39 to 30 kt to cross a runway, which is backwards. Item 2 fixes this for EXP; the `SPD` half stays a defect.
- **B.** A new TAXI does not clear `IsExpeditingTaxi`: `GroundCommandHandler.cs:372-374` clears only `CommandedTaxiSpeedKts`, so a stale expedite leaks into the next route. Route completion does not clear it either.
- **C.** `NORM` (`FlightCommandHandler.ApplyNormalRate`, :523-530) clears the exit and line-up expedites but not `IsExpeditingTaxi`. `RNS` clears it only while `TaxiingPhase` is current (:320).
- **D.** `GroundConflictDetector.LiveRequestedSpeedKts` (:2431-2433) falls back to `CommandedTaxiSpeedKts`, else `TaxiSpeed`, and ignores the expedite. This matters only before the navigator first publishes.
- **E. Nit.** These citations should be §3-7-2.j:
  - §3-7-2.a.10 at CrossingRunwayPhase.cs:19 and :497.
  - §3-7-2.b.10 at GroundCommandHandler.cs:6760, ParsedCommand.cs:309, RunwayOccupancy.cs:119 and docs/landing-and-runway-exit.md:201.

  `PilotResponder` and `ExpediteReadbackTests` already cite it correctly.

## Questions settled by the owner (each recommendation adopted)

1. **One expedite with a scope, or separate flags per command?** Recommend one route/crossing scope (an enum) beside the existing `IsExpeditingExit` and `IsExpeditingLineup`, which keep their own lifetimes. The taxi side then has a single knob.
2. **Acceleration bump under EXP?** Recommend yes: 1.5 kt/s for jet, turboprop and piston; 1.0 for Super/Heavy and helicopters. This adds one new constant to CLAUDE.md's validated list. The alternative is a speed-only expedite, which barely helps a crossing that starts from the bar.
3. **Heavies under EXP?** Recommend accepting it with no speed or acceleration bump (cap 30 kt, accel 1.0), per §3-7-3.a. The trainee sees an accepted clearance that buys little. Alternative: the full 1.3× for everyone.
4. **Does a release without EXP (RES, or a CROSS from a stop) end a route expedite?** Recommend yes, for consistency with RES today and the §2-1-5.b restatement rule. A CROSS pre-clearance issued while rolling leaves it running.
5. **Aliases `WD` / `ND` (as on LUAW/CTO) on the ground commands?** Recommend no: `EXP` only. Taxiway names are free text, and two-letter tokens risk colliding with real designators. `EXP` already matches EXIT/EL/ER.
6. **Fix defects A to C in this item?** Recommend A (crossing cap, including `SPD`), B and C in this item. The new modifier depends on the flag's lifetime being right. D can be its own plan add.
