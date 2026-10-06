# Client surfaces redesign

Linear YAAT-274 (GitHub #746). The Settings window, the import/export flows and the View menu feel overwhelming; this folder holds the redesign. [`inventory.md`](./inventory.md) is the factual map of what exists today (every setting with its tab and backing preference, every import/export entry point and format, every View menu item and pop-out window).

branch: feat/client-surfaces-redesign (owner ruling): the redesign lands on a feature branch in both repos with draft PRs, merged when it is whole. The branch, its PRs and the Linear project carrying the marker open with the first build item.

## Rulings (owner brainstorm)

- **Scenario settings, two surfaces kept and reconciled.** Settings → Scenario defaults stays the per-user defaults applied at scenario load; the gear flyout stays the room's live values. Both show the same settings in the same form (auto-accept as a checkbox plus 0-60 s on both; the per-position auto cleared-to-land and arrival-spacing defaults are settled in the rulings on the mocks below), and the Settings section is labelled as defaults.

  Settings that exist only in the flyout (live traffic, solo call-up interval, arrival generator rate, releases) are judged one by one in the design: a default in Settings, or flyout only.
- **Navigation: sidebar, search and jump-in.** A larger Settings window with a left sidebar grouped by surface (General, Scenarios, Radar, Ground, Strips/vTDLS, Terminal, Speech, Keys, and the rest as the design settles), a search box that filters across every section, and a "Settings for this view…" entry in each view's menu that opens its section. A setting that spans surfaces (font sizes, always-on-top) needs one home and search aliases.
- **Save model: OK, Apply, Cancel.** The classic three buttons: Apply commits without closing, OK commits and closes, Cancel discards what was not applied.
- **Import/export: one hub, the existing buttons open it.** One Import / Export window: tick what to include (preferences, macros, command verbs, favorites, grid layout, layouts) into one bundle file; import previews the bundle's contents and asks merge or replace per item.

  The per-feature buttons that exist today (Macros tab, Commands tab, favorites bar, column chooser) open the hub with their item preselected. Layouts and preferences, which cannot be exported today, become exportable through it.
- **View menu: regroup and add hotkeys.** Submenus Windows (pop-outs, New Ground/Radar Window, Strips, vTDLS), Bars (Favorites, Timeline) and Layout (Layouts, Reset Aircraft List Layout), with hotkeys on the common pop-outs.
- **Flyout-only settings are decided one by one on the mocks.** The design proposes, per setting (live traffic and its ceiling, solo parking call-up interval, arrival generator rate, releases), whether it gets a Settings default; the owner rules on the mock.
- **Bundle: a zip of today's files.** One `.yaat-settings.zip` holding a manifest and the existing per-feature files (macros JSON, verbs JSON, the favorites zip, the grid layout JSON, plus preferences and layouts). A single-item export keeps today's extension, so files exported before the hub still import.
- **Import clashes: per item type, then a clash list.** Each ticked item type is Merge or Replace; under Merge, named entries that clash (a macro, a favorite set, a layout) are listed with Skip, Overwrite or Rename, as the macro import does today.
- **Window Profiles and Copy View Settings merge into Layouts.** A layout holds the window arrangement only: geometries, the pop-out windows, extra Radar/Ground windows, the Aircraft List grid layout, favorite sets and bar/panel state, and the open Strips/vTDLS tabs.

  No radar, ground or terminal view settings ride along, so layouts stay portable across scenarios; copying view settings from a scenario is a source in the apply dialog, never stored in a layout. Saved window profiles carry over as layouts by a load-time rename (names and timestamps kept).

## Mocks

Design canvas https://claude.ai/artifact/S2FPQNyeFvz5JjgGW5D8m7, saved in [`../canvases/client-surfaces-redesign/`](../canvases/client-surfaces-redesign/): Settings on Scenario defaults, Settings search, the regrouped View menu, the session flyout, and the Import / Export hub's export and import-preview pages.

## Rulings on the mocks (owner)

- **Sidebar.** General, Appearance; Session: Scenario defaults; Views: Radar, Ground, Aircraft list, Strips and vTDLS, Terminal; Input: Command input, Command verbs, Macros, Keys; Voice: Speech, Audio devices; Advanced: Server admin (keybinds and server admin are separate sections).

  Font sizes and the strips/vTDLS zoom have one home in Appearance and appear as links in each view's section, so search finds them from either; colours live with their view (ground colours in Ground, radar tint in Radar, terminal channel colours in Terminal).
- **Flyout-only settings.** Solo parking call-up rate and solo arrival generator rate get Settings defaults under Scenario defaults › Solo training; live traffic (SWIM, ceiling, filters, assume) and releases stay room-only, listed in Scenario defaults as such.
- **Session flyout and setup dialog.**

  The session flyout shows the room's single auto cleared-to-land and auto arrival-spacing flags, labelled with the student's position type ("Auto cleared-to-land (TWR)"), superseding the GearFlyout mock's per-position grid; the scenario setup dialog seeds its pacing sliders from the Settings pacing defaults without writing them back; the parking call-up default uses the flyout's interval slider (Paused or once per 10-120 s), superseding the Main mock's "rate %".
- **Hotkeys.** Defaults Ctrl+Shift+L aircraft list, G ground, R radar, E terminal (Ctrl+Shift+T is always-on-top), C controllers, M METAR, F favorites bar, and Ctrl+, for Settings; every one rebindable in Settings › Keys, which shows a clash when rebinding.
- **Status-bar menus.** The mic indicator and the live-traffic status text keep their quick on/off toggles and gain a link item ("Speech settings…", "Live traffic…") to the matching Settings section or the session flyout.

## Build items

Linear project `Client surfaces redesign` (carrying the branch marker), sub-issues of YAAT-274: YAAT-291 Settings window shell (sidebar, OK/Apply/Cancel), YAAT-292 search and cross-section links, YAAT-293 open Settings at a section (view menus, Ctrl+, , status-bar links), YAAT-294 Scenario defaults reconciled with the session flyout, YAAT-295 View menu regroup and hotkeys, YAAT-296 Layouts, YAAT-297 Import / Export hub. YAAT-292 to YAAT-295 build on YAAT-291; YAAT-297 builds on YAAT-296.

## Open decisions

- Where each of today's 120-odd controls lands within its section, and the search's alias list (settled per build item, against the sidebar above).
