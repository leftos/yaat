# Plan: Unify Dual Command Input Pipeline

**Goal:** Replace the two independent parse passes in `CommandInputController` (one for autocomplete, one for signature help) with a single parse-once, render-twice architecture.

**Key insight:** RWY is already registered in `CommandRegistry` as `AssignRunway` with alias `["RWY"]` and compound modifier `Mod("TAXI", null, false)`. The hardcoded RWY branches in `ArgumentSuggester` and `CommandInputController.BuildRwySignatureSet()` duplicate what the registry already describes. The fix is to make the input pipeline use the registry uniformly, not to add a second RWY entry.

## Design

### New type: `CommandInputParseResult`

A record produced by a single parse pass, consumed by both suggestion and signature help systems.

```csharp
// Yaat.Client/Services/CommandInputParseResult.cs
public record CommandInputParseResult(
    string CurrentFragment,       // after split on ;/,
    string? ConditionVerb,        // LV, AT, GIVEWAY, BEHIND, AS — or null
    string? ConditionArg,         // the condition's argument — or null
    string[] Tokens,              // space-split tokens of the stripped fragment
    int VerbIndex,                // index of verb in Tokens; -1 if not found
    string? Verb,                 // the verb string (uppercase)
    CanonicalCommandType? CommandType,  // resolved type, or null
    CommandDefinition? Definition,     // full registry definition, or null
    IReadOnlyList<string> Aliases,     // current aliases from scheme (for signature rendering)
    int ParameterIndex,           // 0-based param the cursor is on
    string[] TypedArgs,           // args typed after verb
    bool HasTrailingSpace         // whether input ends with space
);
```

### Parse method

```csharp
// CommandInputController — new private static method
private static CommandInputParseResult? ParseCommandInput(string text, CommandScheme scheme)
```

This consolidates:
- `GetCurrentFragment()` (line 524)
- `StripConditionPrefix()` (line 539)
- Token splitting
- Verb finding (currently `IsKnownVerb` line 496 + `FindVerbIndex` in ArgumentSuggester line 401)
- `ResolveVerbToType()` (line 325)
- `CommandRegistry.Get()` lookup
- Parameter index calculation

## Checklist

### Phase 1: Add `CommandInputParseResult` and parse method
- [x] Create `CommandInputParseResult.cs` in `Yaat.Client/Services/`
- [x] Add `ParseCommandInput(string text, CommandScheme scheme)` to `CommandInputController`

### Phase 2: Refactor `UpdateSignatureHelp` to use parse result
- [x] Rewrite `UpdateSignatureHelp` to use `ParseCommandInput`
- [x] Remove inline verb finding, condition stripping, token splitting
- [x] Delete `BuildRwySignatureSet()` — RWY now uses `CommandSignatureSet.FromDefinition` like all other commands

### Phase 3: Refactor `UpdateSuggestions` to use parse result
- [x] Rewrite `UpdateSuggestions` to use `ParseCommandInput`
- [x] Refactor `ArgumentSuggester.TryAddArgumentSuggestions()` to accept `CommandInputParseResult`
- [x] Delete `FindVerbIndex()`, `IsRecognizedVerb()`, `FindCommandDefinition()` from `ArgumentSuggester`
- [x] Remove hardcoded RWY branch from `ArgumentSuggester`

### Phase 4: Clean up dead code
- [x] Remove hardcoded RWY check from `IsKnownVerb()` (RWY is already in scheme as AssignRunway alias)
- [x] Delete `BuildRwySignatureSet()`
- [x] `IsKnownVerb` and `ResolveVerbToType` kept as internal helpers for `ParseCommandInput` (simplified, no RWY special cases)
- [x] All tests pass (305 client, 2295 sim)
- [x] `prek run` passes

### Phase 5: Tests
- [x] Added 14 tests in `CommandInputParseTests` covering: bare verb, callsign+verb, RWY→AssignRunway resolution, compound commands, condition prefixes (LV/AT), unknown tokens, parameter index calculation, alias resolution
- [x] Added compound modifier suggestions to `AddRegistrySuggestions()` — when past all positional parameters, suggests applicable compound modifier keywords (TAXI for RWY, HS/CROSS/NODEL for TAXI, @ and $ for PUSH, etc.). Non-repeatable modifiers already typed are excluded.

## Files changed

| File | Change |
|------|--------|
| `Yaat.Client/Services/CommandInputParseResult.cs` | **New** — parse result record |
| `Yaat.Client/Services/CommandInputController.cs` | Add `ParseCommandInput`; simplify `UpdateSuggestions` and `UpdateSignatureHelp`; delete `IsKnownVerb`, `ResolveVerbToType`, `BuildRwySignatureSet` |
| `Yaat.Client/Services/ArgumentSuggester.cs` | Accept `CommandInputParseResult` instead of fragment; delete `FindVerbIndex`, `IsRecognizedVerb`, `FindCommandDefinition`, RWY branch |

## Risk

- **Low risk overall** — this is mechanical refactoring, not logic changes
- **RWY compound modifier suggestions** — the existing `AddRegistrySuggestions()` doesn't handle `CompoundModifiers` from `CommandDefinition`. After removing the hardcoded RWY branch, we need to verify that `RWY 28R ` still suggests TAXI. If it doesn't (likely), we need to add compound modifier awareness to `AddRegistrySuggestions()`. This is the one non-mechanical piece.
- **Signature help for RWY** — Currently hand-builds two overloads (AssignRunway + Taxi). After this refactor, signature help will show whatever `AssignRunway`'s registry definition provides. Check that this is adequate — the registry has one overload with one param `[runway]` and compound modifier `TAXI`. If users expect to see the taxi route signature too, we may need to add a second overload to the registry entry.

## What this does NOT change

- `CommandSchemeParser` — the actual command parser is untouched
- `CommandRegistry` — no structural changes
- `CommandScheme` — still exists for user alias customization
- `SignatureHelpState` — rendering logic stays the same; it just receives its data from the shared parse result
- Server-side anything — purely client-side refactor
