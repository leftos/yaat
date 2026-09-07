# Typed command arguments

Steer 2026-09-07: the command registry must identify the **type** of each argument, not describe it in prose, so
that (a) every argument type has its own validator, (b) parsed arguments map onto record fields by type, and
(c) a slot that can take two types (runway *or* altitude) is resolved by walking the overloads and asking which
declared type's validator accepts the token — the same table in the server parser, the client canonicalizer and
autocomplete.

## Why

`CommandParameter(Name, TypeHint: string, …)` (`src/Yaat.Sim/Commands/CommandSignature.cs`) carries free text:
`"runway designator"`, `"pattern alt"`, `"altitude in hundreds"`, `"altitude"`, `"hundreds or PA"`, `"0-360"`,
`"fix name"`, `"traffic callsign"`, `"e.g. 28R"` — about thirty distinct strings for roughly a dozen types.
Consumers string-match them: `ArgumentSuggester.IsRunwayHint/IsFixHint/IsCallsignHint/…`,
`CallsignArgumentResolver` (`TypeHint.Contains("callsign")`), `CommandDefinition.SampleArg`. The parsers do not
read the registry at all — each `Parse*` method re-implements its own token rules, which is how `MLT 33` came to
mean 3,300 ft: `ParseMakeTraffic` decided "number-like ⇒ altitude" on its own.

## Target shape

- `CommandArgumentType` enum: `Runway`, `Altitude`, `Heading`, `Speed`, `Fix`, `Airway`,
  `Approach`, `Callsign`, `AircraftType`, `Taxiway`, `Spot`, `Parking`, `Position`, `Facility`, `Bay`, `Distance`,
  `Literal(...)`, `FreeText`, plus the small closed sets (`PatternDirection` MLT/MRT, `WakeClass`, `EngineClass`,
  `FlightRules`).
- One validator per type (`IArgumentValidator<T>`, "little" by design): shape rules and normalisation only
  (`RunwayArgument`: 1–2 digits + optional L/C/R → padded designator; `AltitudeArgument`: 3+ digits, hundreds or
  feet → feet via `AltitudeResolver`, the same shorthand every altitude slot takes). There is **no combined
  runway-or-altitude type**. The validators may overlap on 2-digit tokens; the resolver decides by precedence only
  where both types are candidates at a position (runway wins a 1–2 digit token, so an altitude there needs 3+
  digits), and a position whose viable overloads declare one type takes that type's full grammar (`MLT 15 15`).
  Existence checks (the airport lacks that runway, the fix is unknown) stay at dispatch, where the context is.
- **Overload resolution, like a compiler.** A slot that can take either a runway or an altitude is just two
  overloads (`[Runway]`, `[Altitude]`, `[Runway, Altitude]`). The resolver walks the still-viable overloads token
  by token, tries the validators of the types they declare at that position in precedence order, and drops
  overloads whose validator rejects; one survivor binds, none is a typed failure naming what was expected. The pattern modifiers are the
  first resolver client (step 1).
- `CommandParameter` gains `Type: CommandArgumentType`; `TypeHint` becomes the *display* string derived from the
  type (kept for the cheatsheet/suggester until they read the type directly), never a decision input.
- Overload matching by type: the registry's overloads (`O("RunwayAltitude", [Runway, Altitude])`) drive the
  parser — a `ParsedCommand` record is built by binding validated values to the overload's fields, so a new
  overload is a registry row plus a record field, not a hand-written token loop.
- Consumers switch on the type: `ArgumentSuggester`, `CallsignArgumentResolver`, `CommandSchemeParser`
  (client canonicalizer — the same table, so the two parsers cannot drift, closing the #335 class for good),
  `SignatureHelpState`, the cheatsheet generator.

## Steps

- [x] 1. `RunwayArgument` / `AltitudeArgument` validators + the overload resolver landed with the pattern modifiers
  (`MLT/MRT`, `CTO MLT/MRT`, `COPT/TG/SG/LA MLT/MRT`), `CommandArgumentType` starting at `{Runway, Altitude}` —
  shipped 2026-09-07 (`src/Yaat.Sim/Commands/Arguments/`)
- [ ] 2. Audit: every `Parse*` site with a runway-or-altitude slot, and every registry `R(...)` hint, tabulated to
  a type; list the hints that map to no type (they are the missing enum members). Output: a table in this file.
  Known starting points: `ApproachCommandParser.IsRunwayDesignator` (private, still the old "letter or leading
  zero" rule), `CommandParser.ParseTouchAndGo` (landing runway = any leading non-MLT/MRT token, unpadded),
  `ELB`/`ERB` runway-vs-final-distance, and `CommandParser.IsRunwayDesignator`, now dead after step 1 — delete it
- [ ] 3. `CommandArgumentType` + validators + `CommandParameter.Type`; registry rows converted mechanically
  (`R("runway", …)` → `Arg(Runway, "runway")`); `TypeHint` derived; completeness test: every parameter has a type
  and every type has a validator
- [ ] 4. Consumers read the type (suggester, callsign resolver, signature help, cheatsheet, client canonicalizer)
- [ ] 5. Parser binding: overload-driven parsing for the commands whose token loops only re-state the registry;
  the bespoke grammars (`TAXI`, `CTO`, strips) keep their parsers but validate arguments through the typed
  validators. Delete the per-command "number-like" heuristics as each command migrates
- [ ] 6. Scenario corpus: rerun yaat-server's `validate-all-scenarios.py`; presets written in a retired shorthand
  (an altitude alone as two digits, `CTO MLT 15` meaning 1,500 ft) are listed in `docs/scenario-validation-known-failures.md` or rewritten

Aviation review is not owed for this (no behaviour beyond argument typing); `csharp-reviewer` is, per step.
