# Client surfaces inventory

What exists today, mapped from source for [the redesign](./README.md). Defaults come from `UserPreferences.SavedPrefs` (`src/Yaat.Client.Core/Services/UserPreferences.cs`). Line numbers are hints; confirm against source.

## Settings window

Files: `src/Yaat.Client/Views/SettingsWindow.axaml` (sidebar, section host and footer), `src/Yaat.Client/Views/Settings/*Section.axaml` (one per section; order in `SettingsNavigation.cs`), `SettingsWindow.axaml.cs` (geometry key `"Settings"`, 1040×720, minimum 880×600).

Also: `src/Yaat.Client/ViewModels/SettingsViewModel.cs` (~2,100 lines; `Apply()` writes every setting and can run repeatedly; `ResetSection` per section), `src/Yaat.Client/ViewModels/SettingsSectionId.cs`, `UserPreferences.cs` (`CreateDefaults`).

A modal window with a sidebar of fifteen sections in six groups, a "Reset section" button on every section (pending until Apply or OK; disabled on the two link-only sections), and a footer OK, Apply and Cancel.

Opened from Tools → Settings and the Open Settings key (Ctrl+,; General), from the Radar, Ground, Aircraft list and Terminal right-click menus ("Settings for this view…", at that view's section), and on Speech from the mic status menus ("Speech settings…"), the Speech Debug window and the pilot-voice banner, all through `MainViewModel.RequestSettings`; a request while it is open brings the open window to the front at that section.

It is modal over every open YAAT window (`Views/OpenWindows.cs`): their input is blocked without disabling them, and focus returns to where it was on close.

| Section | Controls | Contents |
|---|---|---|
| General | 10 (+ read-only ARTCC) | Initials (`UserInitials`), Discord status (`DiscordRichPresenceEnabled`, true); windows (raise together, always-on-top ×7, stored in `WindowGeometries[*].IsTopmost`). Reset section keeps the initials |
| Appearance | 10 | Font sizes (7 surfaces) + strips/vTDLS zoom; renderer (macOS) |
| Scenario defaults | 24 + note card | Defaults for new rooms, applied at scenario load: solo training mode, pilot go-around probability, solo training (parking call-up interval Paused or 10-120 s, stored as `SoloParkingInitialCallupRatePercent`, 100; arrival generator rate 0-100 %, 100), auto-accept handoffs + delay 0-60 s, command run delay min/max, default auto-delete, departure auto-delete distance, validate DCT fixes, auto-clear to land GND/TWR/APP/CTR (true/false/true/true), auto cross runways, auto pull-up to parallel, auto go-around on occupied runway, auto rejected takeoff, auto arrival spacing GND/TWR, VFR commands for IFR, RPO pilot speech, audible alert on pilot transmissions; a "Room only: set in the session flyout" card (text, no controls) lists live traffic and releases |
| Radar | 25 | Radar display (EuroScope tags, flash NoLndgClnc, conflict alerts, type-mismatch hints, ATPA); student scope sync (4); overlays (MVA tint GND/TWR/APP/CTR, speech bubbles ×5, TPA cone half-angle, scroll sensitivity); radar tint (assigned, unassigned, selected) |
| Ground | 16 | Ground display (3); ground colors (9) + brightness; layer brightness (3) |
| Aircraft list | links only | Font size → Appearance, Always on top → General; columns come from the column chooser |
| Strips and vTDLS | links only | Zoom → Appearance, Always on top → General |
| Terminal | 10 | Terminal channel colors |
| Command input | 2 | Signature help placement, auto-expand suggestion |
| Command verbs | verb grid + 3 | Per-command verb aliases (`CommandScheme`, only non-default aliases stored), "Try it out", Import…, Export… (open the hub with Command verbs ticked); Reset section restores the default verbs |
| Macros | grid + 6 | CRC aliases folder + Browse, macro grid (`Macros`), Add, Import…, Export…, Export Selected… (the last three open the Import / Export hub with Macros ticked; done by YAAT-297); Reset section clears every macro and the folder |
| Keys | 5 | Hotkeys (aircraft select, focus input, take control, always on top, quick bookmark) |
| Speech | 12 + actions | STT on, auto-focus after speech, Whisper model, LLM model + GPU layers, PTT key, telemetry, sample capture + cache size, pilot voice on, volume, radio FX; download/delete/CUDA/voice-pack/delete-samples buttons, which act at once and are labelled as not undone by Cancel |
| Audio devices | 2 | Input and output device |
| Server admin | 2 | Server admin mode + password |

**Persisted with no Settings UI** (changed elsewhere): Aircraft List column chooser (`ShowOnlyActiveAircraft`, alternating row color, `GridLayout`); View menu (timeline bar, favorites bar/panel, pop-out flags); favorites bar (`FavoritePanelColumns`); window profiles and geometries; radar/ground in-view toolbars (`RadarSettings`, `GroundSettings`, deconflict modes, layers, rotation, favorite video maps, METAR stations).

Also persisted with no Settings UI: terminal header (hidden kinds, timestamp mode); Connect window (saved servers); status-bar menu (live-traffic list filter); strips/vTDLS UI (split mode/ratio, vTDLS dark mode); automatic (recents, per-scenario history).

**Reachable outside the Settings window**
- Gear flyout (`src/Yaat.Client/Views/CommandInputView.axaml` ~:216, "Session Settings", the room's live values for all RPOs): solo mode, go-around probability, solo parking call-up interval and arrival generator rate (same controls and ranges as their Settings defaults), auto-delete, departure distance, auto-accept (checkbox "Auto-accept handoffs after" + 0-60 s, as in Settings), command run delay, validate DCT, the auto behaviours, RPO pilot speech.

  The room holds one auto cleared-to-land and one auto arrival spacing flag, loaded from the Settings default for the student's position type and labelled with it ("Auto cleared-to-land (TWR)"); Settings keeps the per-position defaults (four and GND/TWR). Room only, with no Settings default (listed as such in Scenario defaults): live traffic (SWIM) with ceiling, filters and assume; Releases (its own button beside the gear).
- Mic indicator menu (`MainWindow.axaml` ~:527): enable speech recognition (same setting as the Speech section), speech debugging.
- Live-traffic status text menu: SWIM toggle and list-filter radios.
- Always-on-top hotkey writes the same flag as the seven General › Windows checkboxes.

**Defect (confirmed, fixed by YAAT-291):** `QuickBookmarkKeyButton` had no key-capture handlers, so its capture never ended. Every key-capture button carries the `key-capture` class, and `SettingsWindow.axaml.cs` serves them all with one handler.

## Import / export

All pickers go through `FilePickerFactory` / `IFilePickerService`.

**Done by the hub (YAAT-297).** Every per-feature file flow below is replaced by the one Import / Export window (`ImportExportWindow`, `ImportExportViewModel`): the per-feature buttons open it with their item preselected, and Tools › Import / Export… and Settings › General › Import / Export… open it with nothing preselected.

Merge or Replace is per item (Merge for macros, favorites and layouts; the other items only replace and show no mode choice), each item shows a line saying what the chosen mode does, clashes are listed with Skip, Overwrite or Rename, and single-item exports keep the extensions below.

With "Back up all settings first" ticked (default on, remembered in the unbundled `backUpSettingsBeforeImport` preference), every import first writes all six items, as the hub's source holds them, to `backups/settings-backup-<yyyyMMdd-HHmmss>.yaat-settings.zip` in the YAAT data folder with no picker, and imports nothing if that fails. Imports opened inside Settings stage until Apply or OK (`SettingsViewModelImportTarget`); the others apply at once. The rows record the flows the hub replaced.

| Data | Trigger | Format | Merge or replace | When applied | Code |
|---|---|---|---|---|---|
| Macros | Settings › Macros › Import… / Export… / Export Selected… → hub (done) | JSON `List<SavedMacro>`, `*.yaat-macros.json` | Merge or Replace; name clash → Skip, Overwrite or Rename in the hub | On Apply or OK | `SettingsWindow.axaml.cs` (`OpenImportExport`) |
| Command verbs | Settings › Command verbs › Import… / Export… → hub (done) | `*.yaat-verbs.json` | Overlay: listed commands change, unknown skipped and reported in the hub's summary | On Apply or OK | `SettingsWindow.axaml.cs` (`OpenImportExport`) |
| Favorites | Favorites panel header Import / Export → hub (done); the hub's favorites row exports all sets or one set | `*.yaat-favset.zip`, `*.yaat-favlibrary.zip`, single `.json` | Merge (by id; set clashes listed) or Replace, after the hub's full backup when ticked | Immediately | `FavoritesBarView.axaml.cs` (`OpenImportExport`), `FavoritesBundleImport.cs`, `FavoriteExport.cs` |
| Aircraft List layout | Column header right-click → Column Chooser → Import… / Export… → hub (done) | `*.yaat-grid-layout.json` | Replace | On the chooser's OK (other items imported there: immediately) | `ColumnChooserWindow.axaml.cs` (`OpenImportExport`) |
| Layouts | Hub only (done) | In `*.yaat-settings.zip` | Merge or Replace | Per the hub's entry point | `SettingsBundleItems.cs` |
| Preferences | Hub only (done) | In `*.yaat-settings.zip` | Replace (allowlisted keys) | Per the hub's entry point | `UserPreferences.Bundle.cs` |

Other file flows (not user configuration): recordings and bug bundles, export room as scenario, save weather, ASDE-X/SAID temp data, weather timeline and generator editors' Save As, speech samples.

Inconsistencies of the per-feature flows, done by the hub (one Merge/Replace model with clash rows, one window and summary, one error line; the Settings-or-live split in when an import applies stays by entry point): four merge models; some imports wait for Apply or OK while others apply at once; three button placements (Settings section, code-built bar button with flyout, dialog pair), with only Settings showing an inline result.

Extensions differ (`.yaat-*.json`, `.yaat-*.zip`, bare `.json`); error handling differs (inline error, message box, log only, silent; macro export has no try/catch).

## View menu (`src/Yaat.Client/Views/MainWindow.axaml` ~:37-67)

No item has a hotkey.

| # | Item | Kind |
|---|---|---|
| 1 | Reset Aircraft List Layout | command |
| 2 | Pop Out Aircraft List | toggle → `DataGridWindow` |
| 3 | Pop Out Ground View | toggle → `GroundViewWindow` |
| 4 | New Ground Window | airport dialog → extra `GroundViewWindow #n` |
| 5 | Pop Out Radar View | toggle → `RadarViewWindow` |
| 6 | New Radar Window | extra `RadarViewWindow #n` |
| 7 | Pop Out Controllers | toggle → `ControllersWindow` |
| 8 | Pop Out METAR | toggle → `MetarWindow` |
| 9 | Pop Out Terminal | toggle → `TerminalWindow` (the favorites bar follows it) |
| 10 | Show Favorites Bar | toggle |
| 11 | Open Favorites Panel… | singleton window |
| 12 | Strips ▸ | in a room; per tab Pop Out / Split ▸ / Close, then New Strips Tab… ▸ (`RebuildStripsSubmenu`) |
| 13 | vTDLS ▸ | in a room; per tab Pop Out / Close, then New vTDLS Tab… ▸ |
| 14 | Show Timeline Bar | toggle |
| 15 | Copy View Settings… | with a scenario; `CopyViewSettingsDialog` |
| 16 | Window Profiles ▸ | Save Current as Profile…, Manage Profiles…, one item per profile |

Every pop-out and editor window uses `WindowGeometryHelper`; the small modal dialogs (About, Column Chooser, Save Window Profile, airport picker, bug report, favorite set name, telemetry opt-in) do not.

## Duplication across surfaces

- Scenario behaviours: Settings defaults and the flyout's live values (above).
- Speech on/off: Speech section and mic menu.
- Always-on-top: seven General › Windows checkboxes and the hotkey.
- Favorites: View toggles the bar, the panel is a window, columns live inside the bar, and its import/export buttons open the hub (done by YAAT-297).
- Pop-out: one View toggle per view plus per-tab toggles under Strips/vTDLS; Window Profiles and Copy View Settings also set pop-out state.
- Grid layout: Reset in View, Column Chooser behind a header right-click.
- Settings whose effect shows in one window only: font sizes, zoom, colors, MVA tint, speech bubbles, student scope sync, ground display, TPA cone, scroll sensitivity.
