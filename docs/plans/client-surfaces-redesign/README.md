# Client surfaces redesign

Linear YAAT-274 (GitHub #746). The Settings window, the import/export flows and the View menu feel overwhelming; this folder holds the redesign. [`inventory.md`](./inventory.md) is the factual map of what exists today (every setting with its tab and backing preference, every import/export entry point and format, every View menu item and pop-out window).

branch: feat/client-surfaces-redesign (owner ruling): the redesign lands on a feature branch in both repos with draft PRs, merged when it is whole. The branch, its PRs and the Linear project carrying the marker open with the first build item.

## Rulings (owner brainstorm)

- **Scenario settings, two surfaces kept and reconciled.** Settings → Scenarios stays the per-user defaults applied at scenario load; the gear flyout stays the room's live values. Both show the same settings at the same granularity (today the flyout has one Auto cleared-to-land where Settings has four position types, one arrival-spacing box where Settings has GND and TWR, and an auto-accept delay of `-1` where Settings has a checkbox plus 0-60), and the Settings tab is labelled as defaults. Settings that exist only in the flyout (live traffic, solo call-up interval, arrival generator rate, releases) are judged one by one in the design: a default in Settings, or flyout only.
- **Navigation: sidebar, search and jump-in.** A larger Settings window with a left sidebar grouped by surface (General, Scenarios, Radar, Ground, Strips/vTDLS, Terminal, Speech, Keys, and the rest as the design settles), a search box that filters across every section, and a "Settings for this view…" entry in each view's menu that opens its section. A setting that spans surfaces (font sizes, always-on-top) needs one home and search aliases.
- **Save model: OK, Apply, Cancel.** The classic three buttons: Apply commits without closing, OK commits and closes, Cancel discards what was not applied.
- **Import/export: one hub, the existing buttons open it.** One Import / Export window: tick what to include (preferences, macros, command verbs, favorites, grid layout, window profiles) into one bundle file; import previews the bundle's contents and asks merge or replace per item. The per-feature buttons that exist today (Macros tab, Commands tab, favorites bar, column chooser) open the hub with their item preselected. Window profiles and preferences, which cannot be exported today, become exportable through it.
- **View menu: regroup and add hotkeys.** Submenus Windows (pop-outs, New Ground/Radar Window, Strips, vTDLS), Bars (Favorites, Timeline) and Layout (Layouts, Reset Aircraft List Layout), with hotkeys on the common pop-outs.
- **Flyout-only settings are decided one by one on the mocks.** The design proposes, per setting (live traffic and its ceiling, solo parking call-up interval, arrival generator rate, releases), whether it gets a Settings default; the owner rules on the mock.
- **Bundle: a zip of today's files.** One `.yaat-settings.zip` holding a manifest and the existing per-feature files (macros JSON, verbs JSON, the favorites zip, the grid layout JSON, plus preferences and layouts). A single-item export keeps today's extension, so files exported before the hub still import.
- **Import clashes: per item type, then a clash list.** Each ticked item type is Merge or Replace; under Merge, named entries that clash (a macro, a favorite set, a layout) are listed with Skip, Overwrite or Rename, as the macro import does today.
- **Window Profiles and Copy View Settings merge into Layouts.** One concept: a layout holds the window arrangement and the view settings; copying from a scenario becomes one source of a layout. Existing saved window profiles map into layouts.

## Mocks

Design canvas https://claude.ai/artifact/S2FPQNyeFvz5JjgGW5D8m7, saved in [`../canvases/client-surfaces-redesign/`](../canvases/client-surfaces-redesign/): Settings on Scenario defaults, Settings search, the regrouped View menu, the session flyout, and the Import / Export hub's export and import-preview pages.

## Rulings on the mocks (owner)

- **Sidebar.** General, Appearance; Session: Scenario defaults; Views: Radar, Ground, Aircraft list, Strips and vTDLS, Terminal; Input: Command input, Command verbs, Macros, Keys; Voice: Speech, Audio devices; Advanced: Server admin (keybinds and server admin are separate sections). Font sizes and the strips/vTDLS zoom have one home in Appearance and appear as links in each view's section, so search finds them from either; colours live with their view (ground colours in Ground, radar tint in Radar, terminal channel colours in Terminal).
- **Flyout-only settings.** Solo parking call-up rate and solo arrival generator rate get Settings defaults under Scenario defaults › Solo training; live traffic (SWIM, ceiling, filters, assume) and releases stay room-only, listed in Scenario defaults as such.
- **Hotkeys.** Defaults Ctrl+Shift+L aircraft list, G ground, R radar, E terminal (Ctrl+Shift+T is always-on-top), C controllers, M METAR, F favorites bar, and Ctrl+, for Settings; every one rebindable in Settings › Keys, which shows a clash when rebinding.
- **Status-bar menus.** The mic indicator and the live-traffic status text keep their quick on/off toggles and gain a link item ("Speech settings…", "Live traffic…") to the matching Settings section or the session flyout.

## Next

1. Split into build items in a Linear project carrying the branch marker.

## Open decisions

- Where each of today's 120-odd controls lands within its section, and the search's alias list (settled per build item, against the sidebar above).
- What a layout holds exactly (which view settings beyond today's Copy View Settings set), and how a saved window profile maps into one.
