# Aircraft context menus — design

The aircraft right-click menus on the radar, ground and aircraft-list views. This doc holds the settled design. The step list and the reviewed default situation table are in [plans/context-menu-quick-commands.md](plans/context-menu-quick-commands.md); the mocks are the repo copies in `plans/canvases/quick-command-icons/` (the icon strip) and `plans/canvases/ground-context-menu-mocks/` (the ground point menu).

## The menu

- **Quick commands by situation.** An aircraft's menu shows a short flat list of quick commands chosen by its situation, with the full tree one level down under **All Commands**. Situations, not raw phases: about 20 named situations, each a set of phases plus DTO flags. Both terms are in [`CONTEXT.md`](../CONTEXT.md).
- **Top level:** the header (title, the state line, the route summary and hold status rows, the hold-for-release rows, `Command…`, `Note…`), the icon strip, the text quick commands, then Track ▸, Data Block ▸, Squawk ▸, the view section (the radar's Display ▸ and Draw route, the ground's Display ▸, nothing on the aircraft list), Favorite Commands ▸, All Commands ▸, then Delete and the RPO items.

  Last comes **Settings for this view…** (added by each view's right-click handler, not the builder: `RadarView.BuildAircraftRightClickMenu`, `GroundView.BuildAircraftRightClickMenu`, `DataGridView.BuildRowContextMenu`).
- **All Commands** holds the full tree in one fixed order: the relative items and the ground-movement block, Heading, Altitude, Speed, Navigation, Hold, Approach, Procedures, Tower, Pattern, Preset taxi route, Draw taxi route…, Ask pilot, Coordination, Edit flight plan, then Warp… and Release to live feed. In a ground phase, a takeoff still on the ground, a landing roll and the touch-and-go variants every flight group but Tower is left out (`AircraftMenuBuilder.HidesFlightCommands`). Each entry shows by its own catalog predicate, never by the situation flags.
- **Flight rules are a runtime filter, not separate lists.** Each situation has one list; every entry carries *Both* / *IFR only* / *VFR only*, defaulted from the catalog item and overridable per entry, evaluated against `FlightRules` when the menu opens. An aircraft with no flight rules filed gets only the *Both* entries; a *VFR only* entry reaches an IFR aircraft under the "VFR commands for IFR aircraft" setting, as All Commands offers it (every one under *All*, straight-in final alone under *Enter final only*).
- **An entry is a catalog item or custom text.** Catalog items are stable IDs for the menu's items and submenus, keeping their pickers, smart runway defaults and applicability predicates; custom text is a command template with the same substitution as Favorites. A quick entry builds through its catalog entry's own builder, so it behaves exactly as the same item under All Commands.
- **Inapplicable entries are hidden, not disabled.** An issued clearance hides itself and shows its cancel entry instead.
- **The icon strip** shows the glyph-bearing entries of the one per-situation list: the first ten in list order, two rows of five, the first row filled first and an empty second row hidden; every other entry, a glyph-bearing one past the tenth included, is a text entry below it. A list left with no glyph-bearing entry has no strip. A button stands for its entry's own menu item: a sending or prompting item is clicked through, and a submenu (Maintain ▸, Cleared for takeoff ▸) opens as a flyout under the button, marked by a corner notch; choosing a command closes the menu. A label row above the icons names the entry under the pointer (or keyboard focus) with the command it sends, says so when the entry opens a submenu ("›"), and reads "Quick commands" / "point at an icon" when nothing on the strip is pointed at. The tooltip names the entry and, for one that sends a fixed command, that command (`Hold position — HOLD`); it closes, and stays suppressed, while a strip icon's submenu flyout is open. Glyphs are coloured by family (tower, ground, flight, pattern, scope and sim); a left/right pair shares one glyph, and so do Cancel landing clearance and Cancel takeoff clearance, which no situation offers together. The point menu has its own strip of the same shape: the ground point items that apply (Taxi here, Taxi to {runway end} per end, Push to, Custom taxi…), then a separator, then the text items; only an enabled item gets an icon ("No route found" stays a text row), and an airborne aircraft's point menu has no strip.

## Quick commands by situation

The default list of each situation, most frequent first (`QuickCommandDefaults`); *(IFR)* and *(VFR)* mark a per-entry flight-rules override. An aircraft whose situation is Unknown shows no quick commands and no strip. A list names catalog actions only, never a view's display item.

| Situation | Default quick commands |
|---|---|
| At parking | Push back, Preset taxi route ▸, Draw taxi route…, Push back to…, Check release window |
| Pushing back | Hold position, Push route… |
| Holding on ground | Resume taxi, Draw taxi route…, Follow…, Give way to…, Cross |
| Taxiing | Hold position, Hold short of…, Cross, Follow…, Give way to…, Break conflict, Cleared for takeoff ▸, Cancel takeoff clearance |
| Holding short | Cleared for takeoff ▸, Line up and wait, Cross, Resume taxi |
| Lined up | Cleared for takeoff ▸, Cancel takeoff clearance, Draw taxi route… |
| Departing | Fly heading ▸, Maintain ▸, Climb via SID *(IFR)*, Direct to…, Assign speed ▸ |
| IFR enroute | Fly heading ▸, Maintain ▸, Direct to…, Assign speed ▸, Hold…, Cross fix |
| IFR arrival | Descend via STAR *(IFR)*, Maintain ▸, Assign speed ▸, Fly heading ▸, Direct to…, Expect approach ▸, Hold… |
| VFR flight following | Report traffic in sight…, Fly heading ▸, Maintain ▸, Direct to…, Expect approach ▸ |
| Approach | Cleared approach ▸, Maintain ▸, Assign speed ▸, Report field in sight, Cleared visual *(IFR)*, Cleared to land |
| Holding | Cleared approach ▸ *(IFR)*, Direct to…, Maintain ▸, then *(VFR)* Enter left / right downwind, Enter left / right base, Enter final |
| Pattern | Cleared to land, Cleared for the option, Touch and go, Follow…, Extend pattern leg, Make short approach, Make left / right 360, Turn base, Go around |
| Final | Cleared to land, Go around, Cancel landing clearance, Reduce to final approach speed |
| Rollout / exit | Exit left, Exit right, Cross, Draw taxi route… |
| Go-around | Fly heading ▸, Maintain ▸, Cleared approach ▸ *(IFR)*, *(VFR)* Enter left / right downwind |
| Live traffic | Assume control, Assume and track |
| VFR arrival, inbound | Enter left / right downwind, Enter left / right base, Enter final, Report N-mile final…, Report at fix…, Cleared to land, Follow… |
| VFR departure | Fly heading ▸, On course, Maintain ▸, Report at fix…, Make left / right closed traffic |

Holding is also the situation of a VFR hold and an airspace-boundary hold, which is why its IFR entries are followed by VFR-tagged pattern entries. Exit hold and Expect further clearance time have no sim command yet, so the Holding list leaves them out.

### When a quick command shows

A quick command shows when its flight rules fit, its catalog predicate holds (or a quick-list widening below admits it), it builds an item for the aircraft, and no quick-list rule hides it. The rules read the server's situation flags and `NextCrossingRunway` ([training-hub-contract.md](training-hub-contract.md)) and apply to the quick list only: All Commands keeps every entry whatever they say. They live in `AircraftCommandApplicability` (`Shows*`, `Widens*`) and are applied by `QuickCommandResolver`.

- **Cleared for takeoff** shows while taxiing only within reach of the departure runway's hold line, and at a hold-short only at the departure runway's bar; **Line up and wait** also only at that bar. Both hide while the aircraft is held for release, which the sim refuses.
- **Cross** is named with its runway ("Cross 28R") and sends `CROSS 28R`: at a hold-short the held runway, elsewhere the runway the server reports as next to cross, never the assigned runway.
  - At a hold-short it shows only when the bar is not the departure runway's; **Resume taxi** likewise, since the sim refuses `RES` at the departure runway's bar.
  - While taxiing, holding on the ground (in position, after an exit, after a push) or in a rollout/exit phase (the landing rollout, the runway exit, clearing the runway, a rejected takeoff) it shows only when the next uncleared bar on the taxi route is a runway to cross.
  - On the landing rollout it waits until the rollout decelerates, as Exit left / right do: no taxi instruction immediately after touchdown (7110.65 §3-10-9 note).
  - In a rejected takeoff it waits until the aircraft has slowed to taxi speed (30 kt ground speed or less).
  - No Cross is offered for the next runway while the aircraft is still crossing a runway, or while a runway already cleared to cross lies ahead: one crossing at a time (§3-7-2.c). Leaving the runway the aircraft landed on or is clearing does not count as crossing another.
- **Cancel takeoff clearance** shows only once the aircraft holds a takeoff clearance and is not past V1, where the sim answers "unable" (§3-9-11); it also shows while taxiing with a stored takeoff clearance.
- **Cleared approach** hides once the aircraft holds an approach clearance with descent on it (a JFAC/JLOC lateral intercept still offers it). After a go-around or a missed approach it shows again only once a new altitude is assigned: an approach clearance carries the altitude to maintain until established (§4-8-1).
- **Cleared to land** hides on final once a landing clearance is on the aircraft.
- **Cleared visual** shows only for an IFR aircraft that has reported the field or the preceding traffic in sight (§7-4-3).
- **Assign speed** and **Reduce to final approach speed** hide inside the final approach fix (§5-7-1).
- **Exit left / right** show on the rollout only once it decelerates.
- **Give way to…** shows only with a taxi route.
- **Climb via SID** shows only with an active SID or one the filed route names at the departure airport (never for a VFR aircraft); **Descend via STAR** only with an active STAR or one the filed route names at the destination. The lookup is the sim's own (`FiledProcedureLookup`), so the menu offers exactly what a bare `CVIA` / `DVIA` accepts.

Quick-list widenings, each first proven against the sim (`QuickCommandSimAcceptanceTests`):

- **Push route…** also while the push is under way.
- **Draw taxi route…** also while lining up, lined up and waiting, on the landing rollout and in the runway exit.
- **Follow…** and **Give way to…** also while already following another aircraft on the ground.
- The VFR **pattern entries**: a downwind or left base also at the start of a go-around or low approach; any leg entry, straight-in final included, also in an airspace-boundary hold or an AR anchor.
- **Cross** and **Cancel takeoff clearance** in the phases listed above.

## The Quick Commands editor

Settings › Input › **Quick commands** (`SettingsSectionId.QuickCommands`) edits the list of every situation but Unknown. The situations are on the left, named by `QuickCommandSituationNames` in enum order; a blue ● marks a staged list that differs from `QuickCommandDefaults`, an amber ⚠ one holding a custom row that fails validation. On the right are a menu preview, the selected situation's rows in order, and the buttons Add command…, Add custom, Reset this situation and Reset all.

- **Staged, then applied.** When Settings opens, every situation's list is copied from `UserPreferences.GetQuickCommandList` into staged rows (`SettingsViewModel.QuickCommands.cs`), and every edit changes only those rows, Reset this situation and Reset all included (both stage the default list, with no confirmation).

  Apply or OK writes each situation whose staged list differs from the stored one through `UserPreferences.SetQuickCommandList`, which removes the stored override when the list equals the default; Cancel discards. The footer's Reset section runs Reset all.
- **What is stored.** Only the situations whose list differs from the default are stored, so improved defaults reach every situation the user never changed. A stored override is removed by storing the default list again through `UserPreferences.SetQuickCommandList`, which is how the editor applies its staged resets.
- **Rows.** A catalog row shows its glyph and label, a flight-rules choice of Default (its catalog rule), Both, IFR only or VFR only, and a remove button. A custom row adds an editable label, a command and an optional ground command, and offers only the last three flight-rules choices.
- **Validation.** A custom row needs a label and a command that prepares as typed text does (`TypedCommandText.TryPrepare` against the staged macros and the staged verb scheme, the same preprocessing the menu applies when it is built); its problem shows under the row. Editing a macro or a verb alias revalidates every custom row. While any situation has an invalid row, `CanApply` is false and OK and Apply stay disabled, with `ApplyBlockedSummary` naming the situations in their tooltip.
- **Strip mark and preview.** The first `QuickCommandGlyphs.StripCapacity` (ten) rows with a glyph are marked as the strip, by position, as `QuickCommandGlyphs.Split` does at runtime, and the entry list draws a divider after the tenth. The preview draws those rows through the menu's own factory, `QuickCommandStrip.GlyphCell` (via `GlyphIcon`) laid out by `QuickCommandStrip.Rows`, and lists every other row as text. It shows every row, whether or not it would apply to a given aircraft.
- **Reorder.** A row is dragged by its ≡ handle (`QuickCommandsSection.axaml.cs`): a move of at least 4 px starts the drag and shows the drop line, Escape cancels it, and the drop calls `SettingsViewModel.MoveQuickCommandEntry(from, insertIndex)`.
- **Add command…** is a flyout over `QuickCommandCatalogOffer`: the eligible catalog actions (`QuickCommandCatalog.Eligible`) the list does not hold yet, filtered by the search text against label or family and grouped by family under `QuickCommandFamilyHeader` rows, with the glyphs drawn by `QuickCommandStrip.GlyphIcon`. Add, Enter or a double-click adds the selection under its catalog flight rules.
- **Search.** Settings search finds the section through the aliases "quick commands", "context menu", "right-click", "icon strip" and "menu commands" (`SettingsSearchCatalog.QuickCommands`).
- **No import or export in the editor.** Quick-command lists are not yet an item type of the Import / Export hub.

## Where it lives

- **One catalog in `Yaat.Client.Core`, behind interfaces** (`IMenuAircraft`, `IMenuHost`, `MenuCatalogEntry`, `MenuIds`), one tree on every surface: radar, ground and the aircraft list build the same menu from the same catalog with no per-surface filtering, and the same view-agnostic builder serves YAAT Scope's aircraft menu. Every entry resolves to command text sent through `IMenuHost.SendAsync`, which `ClientMenuHost` routes to `MainViewModel.SendCommandForViewAsync` on every view, so a failed send shows in the status line wherever the menu opened. A view adds only its view section of canvas items (Display, Draw route), and a point click opens the point menu; both terms are in [`CONTEXT.md`](../CONTEXT.md).
- **The quick commands** are `QuickCommandDefaults` (the lists), `QuickCommandResolver` (the filters), `QuickCommandGlyphs` (the glyphs and the strip split) and `QuickCommandStrip` (the strip control; its `GlyphIcon`, `GlyphCell` and `Rows` also draw the editor's preview), placed by `AircraftMenuBuilder`. The point menu's strip is `AircraftMenuBuilder.AddPointStrip`, built by `QuickCommandStrip.FromBuilt` from the built point items (the caller bounds the count) with the glyphs in `QuickCommandGlyphs.PointById` / `ForPoint`, kept separate from `ById`; `PointMenuHostCache` wraps the menu host for one point-menu open so the strip and the text items share one set of host answers (taxi choices, runway hold-short targets, the custom-taxi seed).
- **The server classifies the situation.** `SituationClassifier` (`src/Yaat.Sim/Situation/`) stores a snapshotted situation from a sim step with hysteresis bands whose widths the aviation review set, and sends it with one `SituationFlags` wire field and the `NextCrossingRunway` field; the client never re-derives them from the phase string.
- **Storage: global per user.** Only the situations a user changed are stored in `preferences.json`, so improved defaults reach users who never customised (`UserPreferences.GetQuickCommandList` / `SetQuickCommandList`; a list equal to the default is not stored). Situations are stored by name; a stored catalog id that no longer exists, or an unknown situation, is dropped at load with a warning. Menus read the lists through `MenuSession.QuickCommandLists` / `MenuContext.QuickCommandListFor`. The file format for a future Import / Export hub item type exists (`QuickCommandListsFile`, `*.yaat-quickcommands.json`), but nothing wires it to the hub yet (YAAT-340).
- **Custom entries.** A list entry is a catalog entry (`CatalogQuickCommandEntry`) or a custom one (`CustomQuickCommandEntry`: label, command text, optional ground text, flight-rules tag). A custom entry sends its ground text when the aircraft is on the ground and one is set, else its text, with the callsign added as for every command; the text is prepared as typed text is (`TypedCommandText.TryPrepare`: `!NAME` macros expanded, verbs put in canonical form under the user's scheme) when the menu is built, so a macro or alias edit reaches the next menu opened, and the editor validates the same way. The typed path's other steps do not apply: a partial callsign argument (`FOLLOW AAL1`) is sent unresolved. A stored entry that no longer prepares is sent as far as it got, with one client-log warning per entry text per session; it has no glyph, so it always lists as text below the strip.
- **Editor** ([The Quick Commands editor](#the-quick-commands-editor)): `src/Yaat.Client/Views/Settings/QuickCommandsSection.axaml(.cs)` (preview, entry list and strip divider, drag reorder, the Add command… flyout) and `src/Yaat.Client/ViewModels/SettingsViewModel.QuickCommands.cs` (the staged lists, rows, validation, add/remove/move, resets and Apply).

  In `src/Yaat.Client.Core/ContextMenus/`: `QuickCommandCatalog.cs` (the catalog actions the editor may add, with family, label, default flight rules and glyph) and `QuickCommandSituationNames.cs` (each situation's name in the editor).
