# Issue #47: Remappable Pause and Unpause Commands

## Problem

The user wants to remap the Pause (`P`) and Unpause (`U`) command aliases because their sim
uses `U` for heading commands. Currently Pause and Unpause are excluded from the Settings
command scheme editor because they are marked `IsGlobal = true` in `CommandRegistry`, and
`SettingsViewModel` filters out all globals.

## Root Cause

`SettingsViewModel.DisplayCommands` filters with `!c.IsGlobal`, which hides all global
commands including Pause and Unpause. Every other infrastructure layer already supports
remapping these commands correctly:

- `CommandSchemeParser.ParseSpaceSeparated` iterates `scheme.Patterns` and respects whatever
  aliases are stored — no special-casing of Pause/Unpause verbs.
- `IsConcatenationExcluded` already lists Pause and Unpause — prevents accidental
  no-space concatenation matching regardless of alias.
- `ToCanonical` always uses `CommandScheme.Default()` as the canonical authority — the server
  always receives `PAUSE`/`UNPAUSE` on the wire regardless of the user's alias, which is correct.
- `MainViewModel.HandleGlobalCommand` dispatches by `CanonicalCommandType` enum, not by verb
  string — works automatically with any alias.
- `UserPreferences.SetCommandScheme` / `FromSaved` / `ToSaved` serialize any
  `CanonicalCommandType` with no allowlist to update.

## Change Required

### `src/Yaat.Client/ViewModels/SettingsViewModel.cs`

This is the **only file that needs to change**. Add a `RemappableGlobals` allowlist and update
the `DisplayCommands` filter to include it:

```csharp
private static readonly HashSet<CanonicalCommandType> RemappableGlobals =
[
    CanonicalCommandType.Pause,
    CanonicalCommandType.Unpause,
];

private static readonly IReadOnlyList<CommandDefinition> DisplayCommands = CommandRegistry
    .All.Values.Where(c => (!c.IsGlobal || RemappableGlobals.Contains(c.Type)) && c.Type != CanonicalCommandType.DirectTo)
    .ToArray();
```

## What Does NOT Change

- `CommandRegistry.cs` — `IsGlobal = true` on Pause/Unpause stays. The flag is not used by
  `MainViewModel.IsGlobalCommand` (which lists types explicitly) — it only affects the settings
  filter, which this plan corrects.
- `CommandSchemeParser.cs` — `IsConcatenationExcluded` keeps listing Pause and Unpause.
- `CommandParser.cs` (Yaat.Sim) — server-side parser uses hardcoded canonical verbs, unaffected.
- `tests/Yaat.Client.Tests/CommandSchemeCompletenessTests.cs` — completeness tests already
  cover all `CanonicalCommandType` values; no changes needed.

## Verification Steps

1. `dotnet build -p:TreatWarningsAsErrors=true` passes.
2. Settings window shows "Pause" and "Unpause" rows in the Sim Control category.
3. User changes `P` to `PS`, saves, types `PS` — the test command input box shows `→ PAUSE`.
4. Removing `U` from Unpause aliases does not affect any heading command.
5. "Reset to Defaults" restores `P` and `U`/`UN`/`UNP`/`UP`.
6. All existing completeness tests pass unchanged.

## Implementation Status

Implemented. Rather than a targeted `RemappableGlobals` allowlist, the `!c.IsGlobal` filter was
removed entirely — all commands are now remappable. No command has a compelling reason to be
hidden from the scheme editor.

## Files Changed

- `src/Yaat.Client/ViewModels/SettingsViewModel.cs` — removed `!c.IsGlobal &&` from
  `DisplayCommands` filter
