# Command Input UX

> Read this before touching `CommandInputController`, `ArgumentSuggester`, `AddCommandSuggester`, `FixSuggester`, `SignatureHelpState`, `CommandInputParseResult`, `CallsignArgumentResolver`, `CommandInputView.axaml.cs`, or `MainViewModel.OnCommandText/CaretIndexChanged`. This is the **client-side, keystroke-to-dropdown** journey that runs *before* a command is sent. The server-bound journey (parse → dispatch → queue) is [command-pipeline.md](command-pipeline.md); per-domain handler effects are [command-handlers.md](command-handlers.md).

## Scope

Everything here lives in `src/Yaat.Client/Services/` (plus one `Yaat.Sim` record and the view code-behind). It produces two things as the instructor types:

- **Autocomplete** — a dropdown of `SuggestionItem`s (callsigns, command verbs, runways, fixes, literals, modifiers, macros).
- **Inline signature help** — a one-line parameter hint with the active parameter highlighted.

It also performs **one** server-bound mutation: `CallsignArgumentResolver.TryRewrite` rewrites partial callsign arguments to canonical form in the send path. That is the single point where this subsystem touches the server-bound flow described in [command-pipeline.md](command-pipeline.md).

**Not in scope (do not document here):** `ShownRouteBuilder.cs` shares the `Services/` folder but is the radar "Show flight path" overlay (consumed by `RadarViewModel`, mirrors `ApproachCommandHandler` leg-walking). It has nothing to do with input UX — it belongs to a radar/route-overlay doc.

## File map

| File | Role |
|---|---|
| `CommandInputController.cs` | The parse pass (`ParseCommandInput`), the autocomplete dispatch chain (`UpdateSuggestions`), signature-help entry (`UpdateSignatureHelp`), history nav, accept flow, condition-prefix peel, the inline macro/callsign/verb/condition suggesters. |
| `CommandInputParseResult.cs` | The 16-field positional record consumed by both autocomplete and signature help. |
| `ArgumentSuggester.cs` | Registry-driven argument suggestions (overload literals, runway/fix/approach/callsign/pattern-leg by `TypeHint`, compound modifiers). |
| `AddCommandSuggester.cs` | The `ADD` positional state machine (a hard-coded custom path). |
| `FixSuggester.cs` | Two-tier fix suggestions (route fixes, then a prefix-gated navdata scan). |
| `SignatureHelpState.cs` | Overload set, `FindBestOverload` scoring, part rendering, overload cycling. |
| `CallsignArgumentResolver.cs` | Send-path partial-callsign rewrite of argument tokens. |
| `CallsignMatcher.cs` | Shared exact-then-substring matching with ambiguity detection. |
| `CommandSignature.cs` (Yaat.Client.Services) | `SignaturePart` record (one rendered token of the hint). |
| `Commands/CommandSignature.cs` (Yaat.Sim) | `CommandSignature` / `CommandSignatureSet` / `CommandParameter`; `CommandSignatureSet.FromDefinition` expands a `CommandDefinition`'s overloads. |
| `CommandInputView.axaml.cs` | Key handling (Tab/Enter accept, Up/Down select-or-history, Escape, Alt+arrows), popup visibility gating. |

## The parse-once contract

Both autocomplete and signature help consume the **same** `CommandInputParseResult`, produced once per keystroke or caret move by `CommandInputController.ParseCommandInput(text, caretIndex, scheme)` (`CommandInputController.cs:237`). There is no second parse for signature help — `UpdateSignatureHelp` (`CommandInputController.cs:207`) calls `ParseCommandInput` again, but it is the same static method over the same inputs, so the contract is "parse describes the token at the caret, both consumers read from it."

The result is an immutable record (`CommandInputParseResult.cs:9`). It is **positional with no per-field XML docs** — read this table before constructing or destructuring it:

| # | Field | Meaning |
|---|---|---|
| 0 | `CurrentFragment` | The fragment (between `;`/`,`) containing the caret, leading whitespace stripped. |
| 1 | `ConditionVerb` | The detected condition keyword (`LV`/`AT`/`AS`/`ATFN`/`ONHO`/`GIVEWAY`/`BEHIND`), normalized; `GW` reports as `GIVEWAY`. Null if no prefix. |
| 2 | `StrippedFragment` | The fragment with its condition prefix removed. Empty while still typing the condition argument. |
| 3 | `Tokens` | Whitespace-split tokens of the stripped fragment. If a leading callsign was peeled (see below), it is synthetically prepended so the verb lands at index 1. |
| 4 | `VerbIndex` | Index of the verb token in `Tokens` (0 = direct verb, 1 = after callsign), or -1 if no known verb. |
| 5 | `Verb` | The matched alias text, or null. |
| 6 | `CommandType` | Resolved `CanonicalCommandType?`. |
| 7 | `Definition` | `CommandRegistry.Get(CommandType)`, or null. |
| 8 | `Aliases` | The scheme's aliases for the command (falls back to `Definition.DefaultAliases`). |
| 9 | `ParameterIndex` | Caret position **relative to the verb**: `ActiveTokenIndex - VerbIndex - 1`. Clamped to >= -1. |
| 10 | `TypedArgs` | All tokens after the verb regardless of caret, so signature help can score overloads with full info. |
| 11 | `HasTrailingSpace` | Whether the active position is an empty insertion slot at the end. See the footgun on the un-parenthesized expression. |
| 12 | `CaretIndex` | The (clamped) caret index in full-text coordinates. |
| 13 | `ActiveTokenStart` | Full-text start offset of the active token (= caret when on an insertion point). |
| 14 | `ActiveTokenEnd` | Full-text end offset of the active token (= caret when on an insertion point). |
| 15 | `ActiveTokenIndex` | Index of the active token in `Tokens`, or `Tokens.Length` for a trailing insertion slot. |

**"Active token" means the token at the caret, not the trailing token of the text.** Editing mid-string suggests for the token under the cursor (`ParseCommandInput` scans `tokenBounds` for the one straddling `caretIndex` at `CommandInputController.cs:419`). A test that assumes trailing-token behavior is wrong.

`ParseCommandInput` returns null for empty text, an all-whitespace fragment, or a zero-token stripped fragment. Both consumers treat null as "dismiss."

## Caret / offset model

All bounds in the parse result are **full-text coordinates**, not fragment-relative, so a suggester can splice directly into the original string.

- **Fragment location.** Walk backward from the caret to the nearest `;`/`,` for `fragmentStart`, forward for `fragmentEnd` (`CommandInputController.cs:255`–`272`). `contentStart` skips leading spaces.
- **Tokenization.** `TokenizeWithBounds` (`CommandInputController.cs:483`) produces tokens plus `(Start, End)` spans offset by `strippedStartInText`, so every span is already in full-text coordinates.
- **Active token vs insertion point.** If the caret sits inside a token's `[Start, End]`, that token is active and `HasTrailingSpace` is false locally. If the caret is in whitespace, `ActiveTokenStart == ActiveTokenEnd == caret`, `ActiveTokenIndex` is the next-token slot, and the local trailing-space flag is true (`CommandInputController.cs:429`–`451`). Autocomplete reads `isInsertionPoint = ActiveTokenStart == ActiveTokenEnd` to decide whether to flood callsign/verb options.
- **ParameterIndex math.** `paramIndex = ActiveTokenIndex - VerbIndex - 1` (`CommandInputController.cs:456`). So `paramIndex == 0` is "the first argument" whether the verb is at token 0 (`FH 270`) or token 1 (`UAL1 FH 270`). Off-by-one here silently shifts every suggestion.
- **Leading-callsign peel.** `FindLeadingCallsignEnd` (`CommandInputController.cs:710`) detects `<callsign> <CONDITION> ...` and returns the offset past the callsign so `StripConditionPrefix` sees the bare condition. After tokenizing the post-condition portion, the callsign is **synthetically prepended** at index 0 (`CommandInputController.cs:353`–`362`) so the verb-finder picks the post-condition verb at index 1.

## Condition prefixes

A fragment may begin with a condition prefix: `LV <alt>`, `AT <fix/FRD>`, `AS <tcp>`, `ATFN <dist>`, `ONHO`, `GIVEWAY <cs>`, `BEHIND <cs>`, or `GW <cs>`. `StripConditionPrefix` (`CommandInputController.cs:782`) removes it and reports three things via `out` parameters:

- `conditionVerb` — the normalized keyword (`GW` → `GIVEWAY`).
- `conditionPrefixLen` — characters consumed by the prefix.
- `strippedStartInFragment` — where the post-condition body starts in the fragment, so cursor positions map correctly.

`ONHO` is the **zero-arg** special case (`keywordHasArg = false` at `CommandInputController.cs:837`): the body starts right after the keyword + space, not after an argument.

When the caret is still inside the condition argument (or the prefix itself), `ParseCommandInput` returns a **partial** result: `StrippedFragment` empty, `Verb`/`Definition` null, `VerbIndex == -1`, and `ActiveToken*` describing the contiguous non-space run under the caret (`FindActiveTokenBounds`, `CommandInputController.cs:509`). `UpdateSuggestions` then branches on `ConditionVerb`: `AT` → fix suggestions for the active token; `GIVEWAY`/`BEHIND` → callsign suggestions (`CommandInputController.cs:101`–`126`).

> **Three keyword tables must stay in sync.** `IsConditionKeyword` (line 752), `HasConditionPrefix` (line 764), and the `StripConditionPrefix` switch (lines 798–838) each enumerate the condition keywords independently. Adding a new condition keyword means editing all three.

## Autocomplete dispatch chain

`UpdateSuggestions` (`CommandInputController.cs:68`) clears the list, parses, and runs an **ordered** branch chain. The first branch that produces suggestions wins:

1. **Suppression gate.** If `_pendingSuppressions > 0`, decrement, clear, hide, return. (See accept flow.)
2. **History reset.** If navigating history, reset.
3. **Condition-argument branch.** Empty `StrippedFragment` → fix/callsign suggestions for the condition arg (above).
4. **Flood-suppression setup.** `hasUserPartial = activePartial.Length > 0 || isInsertionPoint` (`CommandInputController.cs:144`). This suppresses the "flood of all options" case where the caret is dropped at offset 0 of a non-empty token with no typed prefix.
5. **Macro (`!`).** Active token starts with `!` → `AddMacroSuggestions` (matches `Macros` by base name).
6. **First-token flood** (`ActiveTokenIndex == 0`, `VerbIndex <= 0`, `hasUserPartial`) → callsign + command-verb + condition suggestions together (the single-token context could be any of the three).
7. **Callsign-at-index-0** (`ActiveTokenIndex == 0`, `VerbIndex == 1`) → callsign suggestions only.
8. **Verb position** (`ActiveTokenIndex == 1`, first token is not a verb) → command-verb suggestions.
9. **`AddCommandSuggester.TryAddAddArgumentSuggestions`** — the `ADD` positional state machine.
10. **`ArgumentSuggester.TryAddArgumentSuggestions`** — registry-driven argument suggestions.
11. **`FixSuggester.TryAddFixSuggestions`** — `DCT`-fix context.

`MaxSuggestions = 10` (`CommandInputController.cs:11`) caps the list everywhere.

`AddCommandVerbSuggestions` ranks candidates: 0 = exact alias, 1 = alias-prefix or `SyntaxPatterns` prefix (e.g. typing `T` matches `T{n}L`), 2 = label substring (`CommandInputController.cs:1037`–`1095`). Delayed/deferred aircraft only see spawn commands (`DelayedOnlyCommands`, line 1017). `IsCompleteSyntaxPattern` (line 693) suppresses the syntax-pattern match once the token already looks like a complete `T{digits}L/R`.

## Registry-driven argument suggestions

`ArgumentSuggester` (`ArgumentSuggester.cs:13`) is **metadata-driven**. It reads `CommandDefinition.Overloads` and classifies the parameter at `ParameterIndex`:

- It first checks `ArgMode` and bails if `None` or `ParameterIndex < 0` (`ArgumentSuggester.cs:34`–`42`). `ArgMode` is *derived* from the overloads + modifiers (`CommandDefinition.cs:27`), not hand-set.
- `OverloadMatchesPrecedingArgs` (`ArgumentSuggester.cs:189`) ensures earlier **literal** parameters match what the user actually typed before an overload contributes a suggestion.
- The parameter's `TypeHint` is matched by **substring**: `runway`, `fix name`, `approach ID`, `callsign`, `pattern leg` (`IsRunwayHint`/`IsFixHint`/`IsApproachHint`/`IsCallsignHint`/`IsPatternLegHint`, lines 299–322). Literals come from `param.IsLiteral`.
- When every overload is exhausted at `ParameterIndex` and the command declares `CompoundModifiers`, those keywords are offered instead (`AddCompoundModifierSuggestions`, line 260), skipping non-repeatable modifiers already typed.

**Because this is metadata-driven, adding a normal command needs ZERO suggester code** — declare its `Overloads`, `CompoundModifiers`, `SyntaxPatterns`, and `TypeHint` strings in `CommandRegistry`, and the suggestions follow.

**The one custom path inside `ArgumentSuggester`:** `CVA FOLLOW <callsign>`. The `ClearedVisualApproach` definition declares only a `runway` overload (`CommandRegistry.cs:1545`–`1551`); its `LEFT|RIGHT|FOLLOW <cs>` modifiers are a custom server-side parser, not registry metadata. So `ArgumentSuggester` special-cases it: if `ParameterIndex >= 1` and the previous typed arg is `FOLLOW`, it offers callsign suggestions and returns (`ArgumentSuggester.cs:63`–`77`).

Runway, approach, and fix suggestions are sourced per-context: runways from `NavigationDatabase.Instance.GetRunways(primaryAirportId)` (both ends unless they're equal); approaches from `GetApproaches(targetAircraft.Destination)`; fixes via `FixSuggester` (below).

## The ADD command grammar

`AddCommandSuggester` (`AddCommandSuggester.cs:9`) is a **hard-coded positional state machine** driven by `parsed.ParameterIndex` (called `completedArgs`). `ADD` always lives at token index 0. The positional grammar:

| `completedArgs` | Suggestions |
|---|---|
| 0 | `I` (IFR) / `V` (VFR). |
| 1 | `S` (Small) / `L` (Large) / `H` (Heavy). |
| 2 | Engine, gated by the weight token: Small → `P`/`T`, Large → `T`/`J`, Heavy → `J` (`AddEngineOptions`, line 167). |
| 3 | Position: `@fix` (fix flyout if partial starts with `@`), `-bearing` hint, or runway designators (`AddPositionSuggestions`, line 202). |
| >= 4 | Variant-dependent: bearing variant (`-`) needs >= 6; fix variant (`@`) needs >= 5; runway variant offers a distance hint at 4, then type/`*`airline overrides (`AddTypeAndAirlineOverrides`, line 338). |

Type and airline names come from `AircraftGenerator.GetTypesForCombo` / `GetAirlines`, scoped to the parsed weight + engine. **Changing the `ADD` grammar requires touching this file** — it is not metadata-driven.

## Fix suggestions and the no-global-pickers rule

`FixSuggester` (`FixSuggester.cs:8`) is **two-tier and per-aircraft scoped**:

- **Tier 1 — route fixes.** `CollectRouteFixNames` (line 173) gathers the selected aircraft's `NavigationRoute`, its expanded filed `Route`, departure, and destination. These render as `SuggestionKind.RouteFix` ("Route").
- **Tier 2 — navdata prefix scan.** `AddNavdataFixSuggestions` (line 228) binary-searches the sorted `AllFixNames` for the prefix and walks forward while names match.

> **`AddNavdataFixSuggestions` early-returns when no prefix is typed** (`FixSuggester.cs:239`) — it never floods the ~40k navdata fixes. This is the enforcement point of the project's **no global navdata/CIFP pickers** rule: suggestions are per-aircraft scoped (Tier 1) plus a prefix-gated scan (Tier 2). Do not "helpfully" show all fixes.

`AddAtFixSuggestionsForActiveToken` (line 119) is the `@`-prefixed variant used by the `AT` condition and the `ADD @fix` position; it prepends `@` to both the displayed text and the inserted text, and only hits Tier 2 when a fix prefix is present.

## Signature help

`UpdateSignatureHelp` (`CommandInputController.cs:207`) dismisses unless there's a verb + definition, and waits until the user has typed past the verb (a space after it). It then builds `CommandSignatureSet.FromDefinition(Definition, Aliases)` (`Commands/CommandSignature.cs:15`), which turns each `CommandOverload` into a `CommandSignature`, and calls `SignatureHelp.Show(set, ParameterIndex, TypedArgs)`.

`SignatureHelpState.FindBestOverload` (`SignatureHelpState.cs:155`) is a **hand-tuned additive scoring heuristic**:

1. **Eliminate by literal.** For finished args (`j < paramIndex`) a literal parameter must match exactly; for the in-progress arg (`j == paramIndex`) the literal must *start with* the typed prefix (so `CTO R` keeps `RH`/`RT` eligible). Failing this eliminates the overload (`SignatureHelpState.cs:174`–`204`).
2. **Score the survivors:**
   - `+30` if the overload still has a parameter slot at the cursor (stops `ELB 28L ` from showing the 1-arg overload after the cursor moved past it).
   - `+10` if args are typed and the overload has parameters.
   - `+5` if the parameter count equals the typed-arg count; else `+2` if it has more.
   - `+20` per typed arg that matches a literal parameter name.
   - `+8` for the bare overload when no args are typed.

`BuildParts` (`SignatureHelpState.cs:113`) renders the hint: the first alias as the verb, then each parameter as plain text (literal), `[name]` (required), or `[name?]` (optional), with the active parameter flagged. The result is `IReadOnlyList<SignaturePart>` (`Text`, `IsParameter`, `IsActive` — `Yaat.Client.Services` `CommandSignature.cs:3`). `NextOverload`/`PreviousOverload` cycle when more than one overload exists (Alt+Down/Alt+Up — see view).

> **Don't eyeball the weights.** Changing a single weight can flip which overload shows for ambiguous commands like `CTO`/`ELB`. Adjust against `SignatureHelpStateTests` / `CommandSignatureRegistryTests`, not by inspection.

## Token replacement and accept flow

`BuildTokenReplacement` (`CommandInputController.cs:55`) splices a chosen value into the active token's span, preserving the suffix, guaranteeing exactly one space before the next token, and appending a trailing space at end-of-text. Every suggester calls it so insertion is uniform; the returned caret lands one space past the inserted value.

`AcceptSuggestion` (`CommandInputController.cs:187`) returns `(Text, Caret)` from the selected item and **sets `_pendingSuppressions = 2`** — the accept mutates `CommandText` then the caret, firing two change events; both must be swallowed or the dropdown immediately re-pops on the freshly-inserted token. `NavigateHistory` sets `_pendingSuppressions = 1` for its single text-replacement event.

> **A new accept path must set the suppression counter** to match the number of `Text`/`Caret` change events it triggers, or suggestions re-pop after acceptance.

## Send-path callsign rewrite — the one server-facing touchpoint

`CallsignArgumentResolver.TryRewrite` (`CallsignArgumentResolver.cs:43`) runs in `MainViewModel.SendCommandAsync` **after** `MacroExpander.TryExpand` and `CallsignPrefixResolver.Resolve`, **before** the command is sent (`MainViewModel.cs:1691` → `1710` → `1727`). It is the only place this subsystem mutates the server-bound string; everything after it is [command-pipeline.md](command-pipeline.md).

What it does:

- **Block-by-block.** `SplitBlocks` (line 78) preserves `;`/`,` separators verbatim so structure round-trips.
- **Allowlist.** Only `Follow`, `FollowGround`, `ReportTrafficInSight`, `ReportTrafficInSightForced` (`GenericCallsignArgCommands`, line 25), plus the `CVA FOLLOW` custom scan and `GIVEWAY`/`BEHIND` condition arguments, get partial-callsign resolution. For the generic commands it inspects overload parameters and rewrites any argument slot whose `TypeHint` contains `callsign`.
- **`CallsignMatcher.Match`** (`CallsignMatcher.cs:32`) resolves each token: exact → leave (already canonical); unique substring → rewrite; none → leave untouched (the server rejects at exec time, matching typo behavior); ambiguous → return an error message and abort the send.

> A command whose parameter merely has a `callsign` `TypeHint` (e.g. `ACCEPT`) is **intentionally not** partial-resolved. The server's `FindAircraft` stays exact-match-only; partial resolution is a client convenience reserved for the allowlist.

## Integration and threading

- **Triggers.** `MainViewModel.OnCommandTextChanged` (`MainViewModel.cs:1592`) and `OnCommandCaretIndexChanged` (line 1603) call `UpdateSuggestions` + `UpdateSignatureHelp`. `OnCommandTextChanged` clamps the caret to the new length first; `OnCommandCaretIndexChanged` skips updates while history navigation is active.
- **Injection points.** `_commandInput.Macros` is set from preferences; `NavDbReady` flips true when the nav database loads; `PrimaryAirportId` is set on scenario/timeline bootstrap and cleared on disconnect (`MainViewModel.Scenario.cs:484`, `585`).
- **Key handling** (`CommandInputView.axaml.cs:91`): Tab accepts (auto-selecting index 0 if none selected); Enter optionally auto-expands then sends; Up/Down select within the dropdown or navigate history when it's closed; Escape dismisses popups or clears the input; Alt+Up/Down cycles signature-help overloads.
- **Popup gating.** Two `CommandInputView` instances share one `MainViewModel`. The popup `IsOpen` is driven from code-behind, gated on `IsVisible` (`CommandInputView.axaml.cs:81`), so the hidden embedded instance never pops a dropdown at screen (0,0).

## How to add suggestions for a new command

1. **Declare metadata in `CommandRegistry`** — `Overloads` (with `TypeHint` strings `runway` / `fix name` / `approach ID` / `callsign` / `pattern leg` where you want contextual suggestions), `CompoundModifiers`, and `SyntaxPatterns`. The suggesters are metadata-driven, so this usually needs **no suggester code**.
2. **Only touch suggester code for the custom paths** — `ADD` (`AddCommandSuggester`) and `CVA FOLLOW` (special-cased in `ArgumentSuggester` and `CallsignArgumentResolver`).
3. **If an argument is a partial-resolvable callsign**, add the command to `GenericCallsignArgCommands` in `CallsignArgumentResolver` (a `callsign` `TypeHint` alone does not opt in).
4. **The completeness guard** in `CommandSignatureRegistryTests` enforces that every `CanonicalCommandType` has registry coverage — run it after registry changes.

## Footguns

- **`CommandInputParseResult` is a 16-field positional record with no per-field docs.** Use the table above; don't reconstruct field order from memory. Its `HasTrailingSpace` argument is computed with an **un-parenthesized mixed `&&`/`||`** expression — `hasTrailingSpaceAtCursor || fragmentHasTrailingSpace && activeTokenIndex >= tokens.Length` (`CommandInputController.cs:475`). C# precedence binds the `&&` first; read it as `A || (B && C)`. Touch with care.
- **`ParameterIndex` is verb-relative** (`ActiveTokenIndex - VerbIndex - 1`). The same `paramIndex == 0` means "first arg" whether the verb is at token 0 or 1. Off-by-one silently shifts every suggestion.
- **Active token = token at the caret, not the trailing token.** Mid-string edits suggest for the token under the cursor.
- **`_pendingSuppressions`.** `AcceptSuggestion` sets it to 2 (Text + Caret events); `NavigateHistory` sets it to 1. A new accept path that forgets this re-pops the dropdown after acceptance.
- **Suggesters are metadata-driven, except `ADD` and `CVA FOLLOW`.** Adding a normal command needs no suggester code; changing `ADD` or `CVA` does.
- **`TypeHint` matching is fragile substring matching** (`param.TypeHint.Contains("callsign")`). Renaming a `TypeHint` string in the registry silently breaks the corresponding suggestion path with no compile error.
- **`CallsignArgumentResolver` uses an explicit allowlist.** A `callsign` `TypeHint` alone does not opt a command into partial resolution; the server's `FindAircraft` stays exact-match by design.
- **`FixSuggester` returns nothing with no prefix typed.** This is the enforcement point of the no-global-pickers rule. Don't show all fixes.
- **Three condition-keyword tables must stay in sync** — `IsConditionKeyword`, `HasConditionPrefix`, and the `StripConditionPrefix` switch.
- **`FindBestOverload` is hand-tuned scoring** (`+30`/`+20`/`+10`/`+8`/`+5`/`+2`). Adjust against the signature-help and registry tests, not by eyeballing.
- **Two `CommandInputView` instances share one VM.** Popup `IsOpen` is gated on `IsVisible` in code-behind so the hidden instance never pops at (0,0).
- **`ShownRouteBuilder.cs` is not input UX.** It lives in `Services/` but builds the radar "Show flight path" overlay. Do not document or wire it here.
