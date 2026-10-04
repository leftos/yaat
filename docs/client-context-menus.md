# Aircraft context menus — design

The aircraft right-click menus on the radar, ground and aircraft-list views. This doc holds the settled design; it is being built on `feat/context-menu-quick-commands` (PR #471, server half leftos/yaat-server#21), so parts of it are not on `main` yet. The step list and the default situation table are in [plans/context-menu-quick-commands.md](plans/context-menu-quick-commands.md); the icon strip's mock is the repo copy in `plans/canvases/quick-command-icons/`.

## The menu

- **Quick commands by situation.** An aircraft's menu shows a short flat list of quick commands chosen by its situation, with the full tree one level down under **All Commands**. Situations, not raw phases: about 20 named situations, each a set of phases plus DTO flags. Both terms are in [`CONTEXT.md`](../CONTEXT.md).
- **Top level:** header, `Command…`, the quick commands, then Track ▸, Data Block ▸, Squawk ▸, Display ▸, Favorites ▸, All Commands ▸, then Delete and the RPO items. Ask pilot, Coordination and Sim Control live under All Commands.
- **Flight rules are a runtime filter, not separate lists.** Each situation has one list; every entry carries *Both* / *IFR only* / *VFR only*, defaulted from the catalog item and overridable per entry, evaluated against `FlightRules` when the menu opens.
- **An entry is a catalog item or custom text.** Catalog items are stable IDs for the menu's items and submenus, keeping their pickers, smart runway defaults and applicability predicates; custom text is a command template with the same substitution as Favorites.
- **Inapplicable entries are hidden, not disabled.** An issued clearance hides itself and shows its cancel entry instead.
- **The icon strip** shows the glyph-bearing entries of the one per-situation list (two rows of five above the text entries), the rest as text below. Cancel landing clearance shares the Cancel takeoff glyph.

## Where it lives

- **One catalog in `Yaat.Client.Core`, behind interfaces** (`IMenuAircraft`, `IMenuHost`, `MenuCatalogEntry`, `MenuIds`), one tree on every surface: radar, ground and the aircraft list build the same menu from the same catalog with no per-surface filtering, and the same view-agnostic builder serves YAAT Scope's aircraft menu. Every entry resolves to command text sent through `IMenuHost.SendAsync`, which `ClientMenuHost` routes to `MainViewModel.SendCommandForViewAsync` on every view, so a failed send shows in the status line wherever the menu opened. A view adds only its view section of canvas items (Display, Draw route), and a point click opens the point menu; both terms are in [`CONTEXT.md`](../CONTEXT.md).
- **The server classifies the situation.** `SituationClassifier` (`src/Yaat.Sim/Situation/`) stores a snapshotted situation from a sim step with hysteresis bands whose widths the aviation review set, and sends it with one `SituationFlags` wire field; the client never re-derives it from the phase string.
- **Storage: global per user, exportable.** Only the situations a user changed are stored in `preferences.json`, so improved defaults reach users who never customised; import and export to a `*.yaat-verbs.json` file.
- **Editor:** a Quick Commands section in Settings, situations on the left and the ordered entry list on the right (add from catalog, add custom text, reorder, flight-rules filter per entry), with **Reset this situation** and **Reset all**.
