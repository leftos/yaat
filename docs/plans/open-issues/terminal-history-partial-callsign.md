# Terminal history shows partial callsign instead of resolved canonical

## Bug (user's report)

> Partial callsign matches aren't filtered out of the terminal history. So if I enter `436 ERD 28R` and it gets matched to N436MS, "436" remains in the terminal history, which is undesired.

## Reproduction

No recording needed. Repro steps:

1. Launch client, connect to a local server, load any scenario with an aircraft whose callsign ends with (or contains) a digit prefix that isn't unique from the start.
2. Type `{prefix} {some-command}` in the input (e.g., `436 ERD 28R`).
3. Press Enter — partial match resolves the callsign to the canonical form (e.g., `N436MS`) and the command is sent.
4. Observe terminal history: it shows `436 ERD 28R` rather than `N436MS ERD 28R`.

## Suspected code

- `src/Yaat.Client/ViewModels/MainViewModel.cs:977` — `SendCommandAsync()`.
  - Line 1049 captures `originalInput = text` (raw user string).
  - Line 1069 calls `TryResolveCallsignPrefix(commandText, scheme)` which returns the resolved canonical form.
  - Line 1176 calls `AddHistory(originalInput)` — this is where the raw string gets stored; should store the canonical resolution when it differs.
- `src/Yaat.Client/ViewModels/MainViewModel.cs:1879` — `AddHistory()` appends to `CommandHistory`.
- `src/Yaat.Client/Services/CommandInputController.cs:418` — `ResolveTargetAircraft()` (autocomplete); tab-completed inputs already use canonical, so no regression there.

## Proposed fix

When `TryResolveCallsignPrefix` rewrites the callsign prefix, build a display string with the canonical callsign substituted in place, and pass **that** to `AddHistory`. Keep the original raw text available if it's needed elsewhere (e.g., up-arrow recall), but the history display should always show the resolved form.

Edge cases to handle:
- Prefix equals canonical (no rewrite needed).
- Prefix not resolvable (no match) — keep original in history as-is so the user sees what they typed.
- Multiple partial matches, disambiguated by context — same rule: show whatever was dispatched.
- Commands without a callsign prefix (e.g., global commands) — no change.

## Acceptance criteria

- Typing `436 ERD 28R` and pressing Enter results in `N436MS ERD 28R` (or whatever canonical form) in terminal history.
- Unresolved inputs retain their original text in history.
- Tab-completed and already-canonical inputs unchanged (no regression).
- Unit test(s) cover the above cases.

## TDD note

This is a client-side fix with no sim/server involvement.

- Add tests in `tests/Yaat.Client.Tests/` (or wherever client tests live; grep for `MainViewModelTests`).
- If `MainViewModel.SendCommandAsync` is hard to test in isolation, extract the "build display string from raw + resolution" step into a small pure helper and test that.
- No recording replay needed.
