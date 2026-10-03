# Client surfaces redesign

Linear YAAT-274 (GitHub #746). The Settings window, the import/export flows and the View menu feel overwhelming; this folder holds the redesign. [`inventory.md`](./inventory.md) is the factual map of what exists today (every setting with its tab and backing preference, every import/export entry point and format, every View menu item and pop-out window).

branch: feat/client-surfaces-redesign (owner ruling): the redesign lands on a feature branch in both repos with draft PRs, merged when it is whole. The branch, its PRs and the Linear project carrying the marker open with the first build item.

## Rulings (owner brainstorm)

- **Scenario settings, two surfaces kept and reconciled.** Settings → Scenarios stays the per-user defaults applied at scenario load; the gear flyout stays the room's live values. Both show the same settings at the same granularity (today the flyout has one Auto cleared-to-land where Settings has four position types, one arrival-spacing box where Settings has GND and TWR, and an auto-accept delay of `-1` where Settings has a checkbox plus 0-60), and the Settings tab is labelled as defaults. Settings that exist only in the flyout (live traffic, solo call-up interval, arrival generator rate, releases) are judged one by one in the design: a default in Settings, or flyout only.
- **Navigation: sidebar, search and jump-in.** A larger Settings window with a left sidebar grouped by surface (General, Scenarios, Radar, Ground, Strips/vTDLS, Terminal, Speech, Keys, and the rest as the design settles), a search box that filters across every section, and a "Settings for this view…" entry in each view's menu that opens its section. A setting that spans surfaces (font sizes, always-on-top) needs one home and search aliases.
- **Save model: OK, Apply, Cancel.** The classic three buttons: Apply commits without closing, OK commits and closes, Cancel discards what was not applied.
- **Import/export: one hub, the existing buttons open it.** One Import / Export window: tick what to include (preferences, macros, command verbs, favorites, grid layout, window profiles) into one bundle file; import previews the bundle's contents and asks merge or replace per item. The per-feature buttons that exist today (Macros tab, Commands tab, favorites bar, column chooser) open the hub with their item preselected. Window profiles and preferences, which cannot be exported today, become exportable through it.
- **View menu: regroup and add hotkeys.** Submenus Windows (pop-outs, New Ground/Radar Window, Strips, vTDLS), Bars (Favorites, Timeline) and Layout (Window Profiles, Copy View Settings, Reset Aircraft List Layout), with hotkeys on the common pop-outs.

## Next

1. Brainstorm the remaining open decisions with the owner (below).
2. Mocks of the Settings window, the import/export hub and the View menu as an Artifact Design canvas, saved under `docs/plans/canvases/` beside its URL.
3. Split into build items in a Linear project carrying the branch marker.

## Open decisions

- The sidebar's exact sections and where each of today's 120-odd controls lands; the search's aliases.
- Which flyout-only settings get a Settings default.
- The bundle format (one zip with a manifest and the existing per-feature files inside, or one JSON) and its extension; whether the per-feature files keep their own extensions for single-item export.
- Merge semantics per item in the hub preview (today: macros resolve clashes per item, verbs overlay, favorites ask merge or replace once, grid layout replaces).
- Which pop-outs get hotkeys, and which keys.
- Whether Copy View Settings and Window Profiles merge into one concept.
- Whether the mic and live-traffic status-bar context menus stay settings entry points, and whether keybinds and server admin mode stay together under one section.
