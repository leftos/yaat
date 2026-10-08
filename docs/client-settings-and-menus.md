# Client settings, View menu and layouts

How the desktop client's configuration surfaces fit together: the Settings window, Scenario defaults against the session flyout, the Import / Export hub, the View menu and its hotkeys, and layouts. It records the design rulings a change to any of them keeps; the files to open for a given change are in [`architecture.md`](architecture.md)'s Task Index, and the user-facing description is in [`../USER_GUIDE.md`](../USER_GUIDE.md).

## Settings window

**Shape.** One window (`Views/SettingsWindow.axaml`, geometry key `"Settings"`, 1040×720, minimum 880×600) with a left sidebar, a search box over it and a footer. The sidebar groups sixteen sections by surface: General (General, Appearance); Session (Scenario defaults); Views (Radar, Ground, Aircraft list, Strips and vTDLS, Terminal); Input (Command input, Command verbs, Macros, Quick commands, Keys); Voice (Speech, Audio devices); Advanced (Server admin).

The order lives in `Views/Settings/SettingsNavigation.cs` and the ids in `ViewModels/SettingsSectionId.cs`; each section is its own `Views/Settings/*Section.axaml`. Keybinds and server admin are separate sections.

**Save model: OK, Apply, Cancel.** Apply commits without closing and can run any number of times, OK commits and closes, and Cancel discards only what came after the last Apply. Views preview a change live while the window is open (`MainWindow.ShowSettingsDialogAsync`), and each Apply runs `ApplyCommittedSettings`. Every section has a Reset section button whose reset stays pending until Apply or OK; it is disabled on the link-only sections.

The Speech section's download, delete, CUDA, voice-pack and delete-samples buttons act at once, so each says Cancel does not undo it (`SettingsWindowSourceTests.EverySpeechActionThatRunsAtOnce_SaysCancelDoesNotUndoIt`).

**Modal over every YAAT window.** Settings blocks input to every open client window without disabling them (`Views/OpenWindows.cs` tracks them, so it also works headless), and focus is restored on close (to the command input when the element that had it is gone or was a menu item).

**Opened at a section.** Every entry point goes through `MainViewModel.RequestSettings` with a section: Tools › Settings and the Open Settings key (Ctrl+, by default) open General; "Settings for this view…" at the end of the radar, ground, aircraft list and terminal right-click menus (`Views/ViewSettingsMenu.cs`) opens that view's section.

"Speech settings…" on the mic status menus, the Speech Debug window and the pilot-voice banner open Speech. A request while the window is open moves the open window to that section rather than opening a second one.

**One home per setting, links elsewhere.** A setting that spans surfaces has one home and shows as a `section-link` button in every other section it concerns: font sizes and the strips/vTDLS zoom live in Appearance, always-on-top in General, and the push-to-talk key in Speech. Colours live with their view (ground colours in Ground, radar tint in Radar, terminal channel colours in Terminal).

The Aircraft list and Strips and vTDLS sections are links only; aircraft-list columns stay in the column chooser.

**Search.** The search box filters the sidebar to matching sections with counts and highlights the first match. `Views/Settings/SettingsSearchCatalog.cs` holds one entry per control (keyed by its binding path, found by its label after its heading) and per link, each with aliases; a link carries the aliases of the setting it opens, so a search finds a shared setting at its home and at each link.

The Keys rows come from the view model's keybind list, so a new key is searchable with no catalog edit.

## Scenario defaults and the session flyout

Two surfaces stay, reconciled rather than merged. Settings › Scenario defaults holds the user's defaults, applied when a scenario loads; the gear flyout on the command input (the session flyout) holds the room's live values for every RPO in it. Both show a shared setting in the same form, with the same control and range: auto-accept is a checkbox plus 0–60 s on both, and the parking call-up rate is the flyout's interval slider (Paused, or once per 10–120 s) on both, stored as a rate percent.

**Settings defaults for the solo pacing.** The solo parking call-up rate and the solo arrival generator rate have defaults under Scenario defaults › Solo training. The scenario setup dialog seeds its pacing sliders from those defaults and never writes back to them.

**Room-only settings.** Live traffic (SWIM, its ceiling, filters and assume) and releases have no Settings default. Scenario defaults lists them in a "Room only" card with no controls.

**One auto cleared-to-land and one auto arrival-spacing flag per room.** Settings keeps per-position defaults (GND, TWR, APP, CTR for cleared-to-land; GND and TWR for arrival spacing). The room holds a single flag of each, loaded from the default for the student's position type, and the flyout labels it with that type ("Auto cleared-to-land (TWR)"); a per-position grid in the flyout was rejected.

**Status-bar links.** The mic indicator and the live-traffic status text keep their quick on/off toggles and gain a link item: "Speech settings…" opens Settings at Speech, and "Live traffic…" opens the session flyout on whichever command input is showing.

The flyout's mechanics (the `Session*` fields, the four `ApplySessionSettingsFrom*` adapters, the echo guard) are in [`client-mainviewmodel.md`](client-mainviewmodel.md#session-settings-echo-suppression).

## Import / Export hub

**One hub, opened from every old entry point.** One window (`Views/ImportExportWindow`, `ViewModels/ImportExportViewModel.cs`) exports and imports every item type: preferences, macros, command verbs, favorites, the aircraft-list grid layout and layouts. The per-feature buttons (Settings › Macros and Command verbs, the favorites panel header, the column chooser) open it with their item ticked; Tools › Import / Export… and Settings › General open it with nothing ticked.

**The bundle is a zip of the single-item files.** A `.yaat-settings.zip` holds a `manifest.json` and one file per item, each in its single-item format. A single-item export keeps that item's own extension (`.yaat-macros.json`, `.yaat-verbs.json`, `.yaat-favset.zip` / `.yaat-favlibrary.zip`, `.yaat-grid-layout.json`), so files exported before the hub still import. Preferences and layouts have no single-item file and always travel in a bundle.

**Merge or Replace per item, then a clash list.** Merge is offered for macros, favorites and layouts; preferences, verbs and the grid layout import by Replace only (`SettingsBundleFormats.SupportsMerge`). Each row says what its mode will do. Under Merge, an incoming named entry that matches an existing one (an import clash) is listed with Skip, Overwrite or Rename, as the macro import did before the hub.

**A full backup before every import.** With "Back up all settings first" ticked (on by default; `backUpSettingsBeforeImport`, which never travels in a bundle), an import first writes all six items to `backups/settings-backup-<yyyyMMdd-HHmmss>.yaat-settings.zip` in the YAAT data folder, with no picker, and imports nothing if that write fails.

**When an import applies depends on where the hub was opened.** Opened inside Settings, an import stages until Apply or OK (`SettingsViewModelImportTarget`); opened anywhere else, it applies at once. A column layout imported from the column chooser stages until the chooser's OK.

**Preferences travel by allowlist.** Only Settings-section preference keys travel, each with a validation rule (`UserPreferences.Bundle.cs`, `BundledRules`); identity, servers, admin access and secrets never do, and a model source travels only without a local path. Every other key is listed in `UnbundledKeys` with its reason.

## View menu and hotkeys

**Three submenus.** View holds Windows (the pop-outs, New ground window…, New radar window…, Strips, vTDLS), Bars (Favorites bar, Favorites panel…, Timeline bar) and Layout (the saved layouts, then Save current as layout…, From this scenario's views…, Manage layouts…, Reset aircraft list columns).

**Hotkeys on the common pop-outs, every one rebindable.** Defaults: Ctrl+Shift+L aircraft list, G ground view, R radar view, E terminal, C controllers, M METAR, F favorites bar, and Ctrl+, for Settings; Ctrl+Shift+T stays always-on-top. Settings › Keys rebinds each and shows a clash when a new binding is already taken. The View menu shows each item's current hotkey (`MainWindow.ShowMenuHotkeys`), and `Views/WindowHotkeys.cs` dispatches them from every window.

## Layouts

**A layout is the window arrangement only.** It holds window geometries, the pop-out windows, extra Radar and Ground windows, the aircraft-list grid layout, favorite sets and bar/panel state, and the open Strips and vTDLS tabs. It holds no radar, ground or terminal view settings, so a layout stays portable across scenarios.

Copying view settings from the loaded scenario is a source in the Apply Layout dialog (From this scenario's views…), never stored in a layout. Layouts replaced both Window Profiles and Copy View Settings; saved window profiles become layouts at load (`UserPreferences.MigrateLegacyWindowProfiles`, names and timestamps kept).

## Couplings a change must keep

- **A new Settings control needs a search entry.** `SettingsWindowSourceTests.EveryBoundControlHasACatalogEntry` fails on a bound control with no `SettingsSearchCatalog` entry, `EveryCatalogLabelExistsInItsSection` on an entry whose label is not in its section, and `SettingsWindowNavigationTests.EveryCatalogEntry_ResolvesToItsOwnControlInItsSection` on one that resolves to the wrong control.
- **A new preference key needs a bundle decision.** Place it in `BundledRules` with a validation rule, or in `UnbundledKeys` with a reason; `SettingsBundlePreferencesTests.EverySettingsSectionPreference_IsAllowlistedOrNamedExcluded` fails until it is.
- **A new view gets "Settings for this view…".** Its right-click menu appends `ViewSettingsMenu`'s item after the shared aircraft menu, pinned per view by `ViewSettingsMenuTests`.
- **A new View menu pop-out gets an item under Windows.** `ViewMenuPopOutTests.ViewMenu_HasAPopOutItemForEveryDockablePanel` fails otherwise, and an item with a hotkey shows it (`ViewMenu_PopOutAndBarItems_ShowTheirCurrentHotkey`).
- **A new hotkey is a preference plus a keybind descriptor.** Its `UserPreferences` field and its entry in the view model's keybind list give it its Keys row, its search entry, its clash check and its reset; dispatch goes through `WindowHotkeys` (the Task Index row "Add or rebind a configurable hotkey").
- **A new session setting is decided as a default or room-only.** A room setting either gets a Scenario defaults control in the same form as the flyout's, or a line in the "Room only" card.
