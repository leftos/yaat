# Client settings, View menu and layouts

How the desktop client's configuration surfaces fit together: the Settings window, Scenario defaults against the session flyout, the Import / Export hub, the View menu and its hotkeys, and layouts. It records the design rulings a change to any of them keeps; the files to open for a given change are in [`architecture.md`](architecture.md)'s Task Index, and the user-facing description is in [`../USER_GUIDE.md`](../USER_GUIDE.md).

## Settings window

**Shape.** One window (`Views/SettingsWindow.axaml`, geometry key `"Settings"`, 1040×720, minimum 880×600) with a left sidebar, a search box over it and a footer. The sidebar groups sixteen sections by surface: General (General, Appearance); Session (Scenario defaults); Views (Radar, Ground, Aircraft list, Strips and vTDLS, Terminal); Input (Command input, Command verbs, Macros, Quick commands, Keys); Voice (Speech, Audio devices); Advanced (Server admin).

The order lives in `src/Yaat.Client/Views/Settings/SettingsNavigation.cs` and the ids in `src/Yaat.Client/ViewModels/SettingsSectionId.cs`; each section is its own `src/Yaat.Client/Views/Settings/*Section.axaml`. Keybinds and server admin are separate sections.

**Save model: OK, Apply, Cancel.** Apply commits without closing and can run any number of times, OK commits and closes, and Cancel discards only what came after the last Apply. Views preview a change live while the window is open (`MainWindow.ShowSettingsDialogAsync`), and each Apply runs `ApplyCommittedSettings`. Every section has a Reset section button whose reset stays pending until Apply or OK; it is disabled on the link-only sections.

The Speech section's model download, cancel and delete, GPU runtime download, cancel and removal, Whisper download and delete, CUDA, voice-pack and delete-samples buttons act at once, so each says Cancel does not undo it (`SettingsWindowSourceTests.EverySpeechActionThatRunsAtOnce_SaysCancelDoesNotUndoIt`).

The Speech model downloads belong to the app, not the window: `MainViewModel.SttDownloads` owns the installers, so a download keeps running after Settings closes and a reopened window shows its progress. `SettingsViewModel` exposes the model cards (`SttModelCards`, `SttModelCardViewModel`; the picker's drop-down is a card list, and the Mode and Device radios bind to the enums through `EnumEqualsConverter`), the selected mode and device, and `LastApplyChangedSttEngineOrMode`, which tells the host to swap the engine after an Apply; the window calls `ReleaseSttDownloads` on close, which stops mirroring and leaves the downloads running.

The Speech section's **Benchmark this machine…** button sits under the licence line, before Mode, beside a summary line (`SettingsViewModel.BenchmarkSummaryText`, `BenchmarkFailedText`, `BenchmarkSummaryTail`) that follows `MainViewModel.SttBench`. The row shows only when the window was built with a benchmark service (`SettingsWindowServices.SttBench`; the standalone window has none). The button raises `SettingsWindow.BenchmarkRequested` and `MainWindow` opens the Speech benchmark window, unowned and skipped by the modal block, so it stays usable under Settings. Its Apply saves the row's model, mode and device at once, so Cancel does not undo it, and calls `SettingsViewModel.ShowSavedSttChoice` so the open window's OK or Apply writes the same values back. Each model card's Measured line follows the selected mode and device. Details: [`speech-recognition-pipeline.md`](speech-recognition-pipeline.md) (The Speech benchmark window).

**Modal over every YAAT window.** Settings blocks input to every open client window without disabling them (`Views/OpenWindows.cs` tracks them, so it also works headless), and focus is restored on close (to the command input when the element that had it is gone or was a menu item).

**Opened at a section.** Every entry point goes through `MainViewModel.RequestSettings` with a section: Tools › Settings and the Open Settings key (Ctrl+, by default) open General; "Settings for this view…" at the end of the radar, ground, aircraft list and terminal right-click menus (`Views/ViewSettingsMenu.cs`) opens that view's section.

"Speech settings…" on the mic status menus, the Speech Debug window and the pilot-voice banner open Speech. A request while the window is open moves the open window to that section rather than opening a second one.

**One home per setting, links elsewhere.** A setting that spans surfaces has one home and shows as a `section-link` button (the style lives in `App.axaml`, since the main window's status bar uses it too) in every other section it concerns: font sizes and the strips/vTDLS zoom live in Appearance, always-on-top in General, and the push-to-talk key in Speech. Colours live with their view (ground colours in Ground, radar tint in Radar, terminal channel colours in Terminal).

The Aircraft list and Strips and vTDLS sections are links only; aircraft-list columns stay in the column chooser.

**Search.** The search box filters the sidebar to matching sections with counts and highlights the first match. `Views/Settings/SettingsSearchCatalog.cs` holds one entry per control (keyed by its binding path, found by its label after its heading) and per link, each with aliases; a link carries the aliases of the setting it opens, so a search finds a shared setting at its home and at each link.

The Keys rows come from the view model's keybind list, so a new key is searchable with no catalog edit.

## Scenario defaults and the session flyout

Two surfaces stay, reconciled rather than merged. Settings › Scenario defaults holds the user's defaults, applied when a scenario loads; the gear flyout on the command input (the session flyout) holds the room's live values for every RPO in it. Both show a shared setting in the same form, with the same control and range: auto-accept is a checkbox plus 0–60 s on both, and the parking call-up rate is the flyout's interval slider (Paused, or once per 10–120 s) on both, stored as a rate percent, and auto-cross is a three-way combo (Off, All runways, Inactive runways only) on both, stored as the enum name so an old saved on/off preference loads as All runways or Off.

**Settings defaults for the solo pacing.** The solo parking call-up rate and the solo arrival generator rate have defaults under Scenario defaults › Solo training. The scenario setup dialog seeds its pacing sliders from those defaults and never writes back to them.

**Solo advisory settings are client-only.** Scenario defaults › Solo training also holds "Pre-fill traffic advisories" (a combo: True values, the default, or Left for the student) and the "Live advisory check" checkbox (default off), stored as `soloAdvisoryPrefill` and `soloAdvisoryLiveCheck`. Both follow OK/Apply/Cancel and Reset section and travel in the settings bundle; an unknown pre-fill name is ignored with a logged warning. The solo Report traffic in sight… flyout reads the session copies through `MenuSession.SoloAdvisoryPrefill` and `SoloAdvisoryLiveCheck` ([client-context-menus.md](client-context-menus.md)).

The session flyout shows the same two controls under "Solo Training Mode", only while it is on. Their values are this client's copy for the session: never written to the preferences, never sent to the server, so no other RPO sees them. The copy returns to the user's defaults when the client joins a different room and when a scenario or recording loads; a reconnect to the same room, a timeline seek and a session-settings broadcast keep the student's choice. Nothing reads them yet.

**Room-only settings.** Live traffic (SWIM, its ceiling, filters and assume) and releases have no Settings default. Scenario defaults lists them in a "Room only" card with no controls.

**One auto cleared-to-land and one auto arrival-spacing flag per room.** Settings keeps per-position defaults (GND, TWR, APP, CTR for cleared-to-land; GND and TWR for arrival spacing). The room holds a single flag of each, loaded from the default for the student's position type, and the flyout labels it with that type ("Auto cleared-to-land (TWR)"); a per-position grid in the flyout was rejected.

A recording load replaces that flag, and the rest of the flyout, with the tape's session settings; the per-position default is not applied to it.

**Status-bar links.** The mic indicator and the live-traffic status text keep their quick on/off toggles and gain a link item: "Speech settings…" opens Settings at Speech, and "Live traffic…" opens the session flyout on whichever command input is showing.

The flyout's mechanics (the `Session*` fields, the four `ApplySessionSettingsFrom*` adapters, the echo guard, the debounce that holds a typed number until the user pauses, presses Enter or leaves the box) are in [`client-mainviewmodel.md`](client-mainviewmodel.md#session-settings-echo-suppression).

## Import / Export hub

**One hub, opened from every old entry point.** One window (`Views/ImportExportWindow`, `ViewModels/ImportExportViewModel.cs`) exports and imports every item type: preferences, macros, command verbs, favorites, the aircraft-list grid layout and layouts. The per-feature buttons (Settings › Macros and Command verbs, the favorites panel header, the column chooser) open it with their item ticked; Tools › Import / Export… and Settings › General open it with nothing ticked.

**Show quick command icons** (Input › Quick commands, default on) is the `showQuickCommandIcons` preference (`UserPreferences.ShowQuickCommandIcons`, `SettingsViewModel.ShowQuickCommandIcons`). It follows OK/Apply/Cancel, travels in the settings bundle as a flag, is found by Settings search, and the section's Reset turns it back on along with the quick-command lists.

`ClientMenuHost` hands it to the menus as `MenuSession.ShowQuickCommandIcons`; what it changes is in [client-context-menus.md](client-context-menus.md) (Icons off).

**The bundle is a zip of the single-item files.** A `.yaat-settings.zip` holds a `manifest.json` and one file per item, each in its single-item format. A single-item export keeps that item's own extension (`.yaat-macros.json`, `.yaat-verbs.json`, `.yaat-favset.zip` / `.yaat-favlibrary.zip`, `.yaat-grid-layout.json`), so files exported before the hub still import. Preferences and layouts have no single-item file and always travel in a bundle.

**Merge or Replace per item, then a clash list.** Merge is offered for macros, favorites and layouts; preferences, verbs and the grid layout import by Replace only (`SettingsBundleFormats.SupportsMerge`). Each row says what its mode will do. Under Merge, an incoming named entry that matches an existing one (an import clash) is listed with Skip, Overwrite or Rename, as the macro import did before the hub.

**A full backup before every import.** With "Back up all settings first" ticked (on by default; `backUpSettingsBeforeImport`, which never travels in a bundle), an import first writes all six items to `backups/settings-backup-<yyyyMMdd-HHmmss>.yaat-settings.zip` in the YAAT data folder, with no picker, and imports nothing if that write fails.

**When an import applies depends on where the hub was opened.** Opened inside Settings, an import stages until Apply or OK (`SettingsViewModelImportTarget`); opened anywhere else, it applies at once. A column layout imported from the column chooser stages until the chooser's OK.

**Preferences travel by allowlist.** Only Settings-section preference keys travel, each with a validation rule (`UserPreferences.Bundle.cs`, `BundledRules`); identity, servers, admin access and secrets never do, and a model source travels only without a local path. Every other key is listed in `UnbundledKeys` with its reason.

## View menu and hotkeys

**Three submenus.** View holds Windows (the pop-outs, Sco_pe among them, New ground window…, New Scope window…, Strips, vTDLS), Bars (Favorites bar, Favorites panel…, Timeline bar) and Layout (the saved layouts, then Save current as layout…, From this scenario's views…, Manage layouts…, Reset aircraft list columns).

**Hotkeys on the common pop-outs, every one rebindable.** Defaults: Ctrl+Shift+L aircraft list, G ground view, R Scope (the "PopOutRadarView" row, "Pop out Scope"), E terminal, C controllers, M METAR, F favorites bar, and Ctrl+, for Settings; Ctrl+Shift+T stays always-on-top. Settings › Keys rebinds each and shows a clash when a new binding is already taken. The View menu shows each item's current hotkey (`MainWindow.ShowMenuHotkeys`), and `Views/WindowHotkeys.cs` dispatches them from every window.

## Layouts

**A layout is the window arrangement only.** It holds window geometries, the pop-out windows (the Scope's included), extra Scope and Ground windows, the aircraft-list grid layout, favorite sets (loaded ids, bar order and whether Global shows) and bar/panel state, and the open Strips and vTDLS tabs. It holds no Scope, ground or terminal view settings, so a layout stays portable across scenarios. `SavedLayout.IsScopePoppedOut` and `ExtraScopeViews` are null on a layout saved before the Scope was a layout part, which leaves the Scopes as they are on apply; no layout opens a Radar window.

**The docked terminal's size is a window arrangement.** `SavedLayout.TerminalSplitRatio` is the terminal's share of the tabs + terminal height; null on a layout captured before it was recorded, which leaves the current split alone. The live value is `MainViewModel.TerminalSplitRatio`, clamped to 0.15–0.85 and persisted as the `terminalSplitRatio` preference (unbundled: it travels with layouts); `MainWindow` writes it when the splitter drag completes and `UpdateContentGridLayout` lays the rows out from it on every pop-out toggle. The Apply Layout dialog's Terminal row reads "docked · size 25%", "popped out" or "docked" (no saved size); applying it sets the ratio after the pop-out flags and only when the terminal ends up docked, since a popped-out terminal is sized by its own window geometry.

Copying view settings from the loaded scenario is a source in the Apply Layout dialog (From this scenario's views…), never stored in a layout. Layouts replaced both Window Profiles and Copy View Settings; saved window profiles become layouts at load (`UserPreferences.MigrateLegacyWindowProfiles`, names and timestamps kept).

## Couplings a change must keep

- **A new Settings control needs a search entry.** `SettingsWindowSourceTests.EveryBoundControlHasACatalogEntry` fails on a bound control with no `SettingsSearchCatalog` entry, `EveryCatalogLabelExistsInItsSection` on an entry whose label is not in its section, and `SettingsWindowNavigationTests.EveryCatalogEntry_ResolvesToItsOwnControlInItsSection` on one that resolves to the wrong control.
- **A new preference key needs a bundle decision.** Place it in `BundledRules` with a validation rule, or in `UnbundledKeys` with a reason; `SettingsBundlePreferencesTests.EverySettingsSectionPreference_IsAllowlistedOrNamedExcluded` fails until it is.
- **A new view gets "Settings for this view…".** Its right-click menu appends `ViewSettingsMenu`'s item after the shared aircraft menu, pinned per view by `ViewSettingsMenuTests`.
- **A new View menu pop-out gets an item under Windows.** `ViewMenuPopOutTests.ViewMenu_HasAPopOutItemForEveryDockablePanel` fails otherwise, and an item with a hotkey shows it (`ViewMenu_PopOutAndBarItems_ShowTheirCurrentHotkey`).
- **A new hotkey is a preference plus a keybind descriptor.** Its `UserPreferences` field and its entry in the view model's keybind list give it its Keys row, its search entry, its clash check and its reset; dispatch goes through `WindowHotkeys` (the Task Index row "Add or rebind a configurable hotkey").
- **A new session setting is decided as a default or room-only.** A room setting either gets a Scenario defaults control in the same form as the flyout's, or a line in the "Room only" card.

## File map

One entry per file; paths are relative to `src/Yaat.Client/`.

```
Services/
  SettingsBundle.cs             # Settings bundle model: SettingsItemType (preferences, macros, verbs, favorites, grid layout, layouts), SettingsBundleEntry, SettingsBundleFormats (format names, legacy single-item extensions, which types Merge), SettingsBundle (a read bundle + skipped entries)
  SettingsBundleFile.cs         # *.yaat-settings.zip reader/writer: manifest.json (bundleVersion, writtenBy, entries); a legacy single-item file reads as a one-entry bundle; a single ticked legacy type exports as its legacy file; newer bundles import known entries and report the rest
  BoundedZipReader.cs           # Zip reads capped while copying (32 MB an entry, 128 MB a file) for bundles and the favorites zip inside them
  SettingsBundleItems.cs        # Per-item adapters: build an entry from today's state (byte-identical to the legacy serializers) and read one back, rejecting null or nameless macros/layouts and a layout whose favorite set order lists an empty entry
  SettingsImportPlanner.cs      # Import plans: Merge clashes (macros by base name, favorite sets by name or id, layouts by name) with Skip/Overwrite/Rename and suggestions; ValidateRenames; ApplyAll (favorites first, then layouts' LoadedFavoriteSetIds and FavoriteSetOrder remapped)
  SettingsImportTarget.cs       # ISettingsImportTarget (where an import writes: UserPreferencesImportTarget, or the Settings view model's staged rows)
  SettingsExportSource.cs       # ISettingsExportSource (what an export reads, one entry per item type): UserPreferencesExportSource over preferences, favorites and the command scheme
  UserPreferences.Bundle.cs     # The preferences a bundle carries: an allowlist of Settings-section keys, each with a BundledPreferenceRule (range, colour, enum, keybind, model-source checks) that rejects a bad value before anything is written; UnbundledKeys names every excluded key and why
  BundledPreferenceRule.cs      # One allowlisted preference's validation rule
  ViewSettingsCopyCatalog.cs    # Shared catalog of the Apply Layout dialog's groups: Ground per-scenario view settings (scenario source) and LayoutGroups (saved-layout source), each Key/Label/Describe/AreEqual/Copy. Single source of truth for the dialog's diff rows and MainWindow's merge-on-apply.

ViewModels/
  ImportExportViewModel.cs      # The Import / Export hub: Export tab (ticked item types → one bundle, or a single item's own file), Import tab (bundle preview, Merge/Replace per type, clash rows with Skip/Overwrite/Rename, result summary) over an ISettingsImportTarget and ISettingsExportSource; the favorites row exports the library or one set; each import row's EffectText says what the chosen mode does, and only rows offering both modes show the Merge/Replace combo; built as ImportExportViewModel(target, source, ImportExportOpening, ImportExportFiles, UserPreferences); while BackUpFirst is ticked (remembered as UserPreferences.BackUpSettingsBeforeImport) Import first writes every item type from the source to <BackupsFolder>/settings-backup-<yyyyMMdd-HHmmss>.yaat-settings.zip (-2, -3 on a same-second clash; named from the injected TimeProvider) and imports nothing if that fails; every export and backup is written to a temp file through ImportExportFiles.CreateFile (File.Open CreateNew) and moved into place
  SettingsViewModel.Bundle.cs   # Partial: Settings' side of the hub — StageMacros/StageVerbs/StagePreferences/StageLayouts/StageGridLayout/StageFavoritesWrite, committed by Apply (LastApplyCommittedGridLayout tells MainWindow to re-apply the live grids) and dropped by Cancel; Staged* getters and ExportStagedPreferences are what an export from Settings reads
  SettingsViewModel.QuickCommands.cs # Partial: the Quick Commands section's staged state — situations, entry rows (catalog or custom, flight rules, TypedCommandText validation, strip mark by position), the Add command… offer (unlisted eligible actions, searched, by family), add/remove/move, staged resets; Apply writes changed situations (SetQuickCommandList); custom rows revalidate on macro or verb edits; any error blocks Apply (CanApply, ApplyBlockedSummary)
  SettingsViewModelImportTarget.cs # The hub's ISettingsImportTarget inside Settings: stages every item in the SettingsViewModel; favorites import into a staged copy of the store (under StagedFavoritesFolder, owner-PID marker, crash leftovers swept) replayed into the real store on Apply; disposed when Settings closes
  SettingsViewModelExportSource.cs # The hub's ISettingsExportSource inside Settings: exports what the window shows (unapplied edits and staged imports included); SelectedMacros narrows a macros export to Export Selected…'s rows
  SettingsSectionId.cs          # The Settings window's sections, one per sidebar entry (General … Server admin); MainWindow.ShowSettingsDialogAsync opens at one
  SettingsViewModel.cs          # Every Settings value, organised by section: loads at construction, Apply (ApplyCommand) commits and raises Applied (repeatable; only changed always-on-top values are written), ResetSection resets the selected section to UserPreferences.CreateDefaults as a pending edit (one Reset* method per section; link-only sections disable it); includes STT/TTS model download flows, which act at once, the benchmark line (IsBenchmarkAvailable, only with an SttBenchService) and ShowSavedSttChoice, which shows a model, mode and device saved elsewhere without saving; LastApplyChangedSpeechModels says an Apply changed the LLM path, GPU layers or the Whisper size, so the window warms the models. LM-Kit catalogs + GPU probe load via LoadModelCatalogsAsync (Task.Run) — never at construction; see the deadlock note below.

Views/
  SettingsWindow.axaml.cs       # Modal Settings: sidebar of sections, the selected section's view, Reset section and OK/Apply/Cancel footer; built from a SettingsWindowServices record (capture, sample store, favorites, STT downloads, benchmark; the same file), BenchmarkRequested when Benchmark this machine… is pressed, SelectSection(SettingsSectionId), section-link buttons that jump between sections, key capture for every key-capture button (Enter/Escape/Space are bindable while capturing); modal over every window in OpenWindows without disabling them: tunnel handlers drop pointer/key/text input (a press brings Settings forward) and Tapped/DoubleTapped/RightTapped/Holding class handlers stop gestures in the blocked windows (the Speech benchmark window is left usable), all removed in OnClosed; OpenImportExport opens the hub over a SettingsViewModelImportTarget so imports stage until Apply/OK
  Settings/SettingsNavigation.cs # Sidebar rows in display order: group headers (General, Session, Views, Input, Voice, Advanced) and their sections (SettingsNavItem, whose MatchCount is the search badge)
  Settings/SettingsSearchCatalog.cs # Search catalog: one entry per bound control (section, label, Within heading, aliases) and per section-link button; SettingsWindowSourceTests keeps it in step with the section .axaml
  Settings/SettingsSearch.cs    # Pure search matcher over the catalog (every word against label, heading, section title, aliases) → per-section counts and ordered matches
  Settings/*Section.axaml(.cs)  # One UserControl per section (GeneralSection … ServerAdminSection), sharing the window's SettingsViewModel; Aircraft list and Strips and vTDLS hold only links
  Settings/QuickCommandsSection.axaml(.cs) # Quick commands section: situation list (● changed, ⚠ to fix), menu preview through QuickCommandStrip's factories, entry rows with the divider where the icon strip ends (QuickCommandStripDivider), drag reorder by the ≡ handle (Escape cancels), the Add command… flyout (search, family headers, Add/Enter/double-click)
  ImportExportWindow.axaml.cs   # The Import / Export hub window (geometry key "ImportExport"; file pickers, tabs over ImportExportViewModel). ShowLiveAsync opens it on the live preferences and favorites (Tools menu, favorites panel) and raises MainViewModel.NotifySettingsImported with what it applied; ShowOverAsync returns the applied item types
  ViewSettingsMenu.cs           # The shared "Settings for this view…" item the Radar, Ground, Aircraft list and Terminal right-click menus end with (MainViewModel.RequestSettings)
```
