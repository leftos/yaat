# Command usage history

The desktop client keeps a local, per-user record of the commands the user entered and the server accepted, keyed by scenario and by primary airport. A new window lets the user sort and filter that record and add any entry to their favorites in one action. Index line: [MAIN.md](./MAIN.md), under **Programmes next up**; it starts after the next release is cut (user 2026-10-01).

## Decisions (user 2026-10-01)

- **Callsign-free, local only.** An entry never records which aircraft it was sent to. The record is a per-user file on this machine and is never sent anywhere.
- **Granularity.** An entry is the command's exact text with its alias resolved to the canonical verb: `C 50` and `CM 50` are one entry, `CM 50` and `CM 60` are two.
- **Sources.** Commands typed into the command box and commands the speech pipeline produced count. Context-menu clicks, favorites and strip actions do not.
- **Compounds.** A compound line records each of its commands, plus the whole line as a separate compound entry, so a chain can be favorited as one.
- **Window.** One table with a scope selector (this scenario / this airport / all; default this scenario), sortable columns (count, last used, command) and a text filter, with an "add to favorites" action per row.

## Map (exploration 2026-10-01, main @ 8053bf90)

- **An up-arrow recall history already exists.** It is not a usage log:
  - `CommandHistoryEntry(Callsign, Command)` in `UserPreferences.ScenarioCommandHistory` (`UserPreferences.cs` ~:2326, accessors ~:1658-1681).
  - `MainViewModel.AddHistory` (~:4088-4116) keeps 50 per scenario, deduplicated on (callsign, command).
  - It records chat and dot commands with an empty callsign.
  - It records on send, not on accept.
  - It has no airport key, counts or timestamps.
- **Send path.**
  - `MainViewModel.SendCommandAsync` (~:2442) is the one chokepoint for typed lines, speech (which fills `CommandText`; the user presses Enter), favorites buttons (`ExecuteFavoriteAsync`, `MainViewModel.Favorites.cs` ~:136) and macros.
  - Context menus (`SendCommandForViewCoreAsync` ~:3355), timeline/sim controls, hold-for-release, the strips/vTDLS transports and CRC bypass it.
  - The verdict is one `CommandResultDto.Success` per line (`StripsTransportDtos.cs:15`); a compound line is one canonical string and one send (~:2696). `AddHistory` runs regardless of `Success` (~:2706-2711).
- **Callsign stripping and aliases.**
  - `CallsignPrefixResolver.Resolve` splits the leading callsign.
  - `CommandSchemeParser.ParseCompound(...).CanonicalString` is alias-resolved: `C 50` → `CM 50`.
  - `CommandHistoryFormatter.Format` keeps the canonical form only when a callsign was typed.
  - Arguments that name another aircraft:
    - Resolved by `CallsignArgumentResolver` (~:25-30, ~:121-131): `FOLLOW`, `FOLLOWG`, `RTIS`/`RTISF`, `CVA FOLLOW`, and the `GW`/`GIVEWAY`/`BEHIND` condition prefixes.
    - Not client-resolved: `REL`, `AS`, `ACCEPT`, the ghost/`RPOS` family and `TRK RPOS` (`CommandRegistry.cs`).
- **Scenario and airport identity.**
  - `ActiveScenarioId` / `ActiveScenarioName` / `ActiveScenarioPrimaryAirportId` (`MainViewModel.Scenario.cs` ~:766-770).
  - The airport is the scenario's single nullable `primaryAirportId`, normalized by `FavoriteStore.NormalizeAirportId`.
  - Whether the scenario id is null in a solo session with no scenario file is unmeasured.
- **Favorites.**
  - `FavoriteCommand` carries no callsign: the text is appended to the command box for the selected aircraft.
  - Favorites live in Global/Airport/Scenario/Named `FavoriteSet`s, stored one file per entity under `favorites/` (`FavoriteStore.cs`).
  - Add path: `MainViewModel.AddFavorite(fav, setIds)` or `FavoritesBarView.OpenAddFlyoutForCommand(string)` (the command box's "Save as favorite").
- **Persistence.**
  - `YaatPaths` honours `YAAT_APPDATA_DIR`.
  - `UserPreferences` is one blob rewritten on every `Save()`, the wrong home for a growing counter store.
  - `FavoriteStore` (own directory, lock, `Changed` event) and `AtomicFile` are the precedents.
- **Window.**
  - `FavoritesPanelWindow.ShowOrActivate` plus a View-menu item (`MainWindow.axaml` ~:49, wired `MainWindow.axaml.cs` ~:212) is the singleton pattern.
  - `SessionReportWindow.axaml` (sortable `DataGrid` over row view-models) is the closest table to copy.
- **Branch.** The exploration proposes `feat/command-usage-history` (store, window, send-path wiring); decide when it starts.

## Open decisions (for the interview when this starts)

1. Accepted for a compound line: today's single `Success` flag, all-or-nothing, or something more.
2. Favorites buttons go through the command box. Do they count as entered, or are they excluded?
3. The strip rule for arguments that name another aircraft (the lists above): replace them with a placeholder, or leave such commands out.
4. Aggregation per (scenario, airport, command): count, first used, last used. Are refusals logged?
5. Retention and caps, and the on-disk format: one JSON file, JSONL, or one file per scenario.
6. "Per airport": a view-time roll-up over scenarios, or stored separately. Scenarios with no primary airport.
7. Replace `ScenarioCommandHistory` and feed up-arrow recall from the new store ("replace, don't deprecate"), or keep both.
8. Favorites hand-off: the default container, the suggested label, duplicate detection, air or ground category, and the flyout versus a direct add.
9. Window placement, live refresh, and window profiles; export/import.

## Task Index rows for the landing commit

Add rows to `docs/architecture.md`:
- **Command history/recall:** `AddHistory`, `CommandHistoryFormatter`, `CommandHistoryEntry`, `UserPreferences.ScenarioCommandHistory`.
- **Favorites:** `FavoriteStore`, `MainViewModel.Favorites.cs`, `FavoritesBarView`, `FavoritesPanelWindow`, `FavoritesEditorWindow`, `FavoriteExport`.
- **Adding a client tool window:** `WindowGeometryHelper`, `ShowOrActivate`, the View menu, window profiles.
- **Which client paths send commands:** the typed versus clicked list above.
