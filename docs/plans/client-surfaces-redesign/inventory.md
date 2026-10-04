# Client surfaces inventory

What exists today, mapped from source for [the redesign](./README.md). Defaults come from `UserPreferences.SavedPrefs` (`src/Yaat.Client.Core/Services/UserPreferences.cs`). Line numbers are hints; confirm against source.

## Settings window

Files: `src/Yaat.Client/Views/SettingsWindow.axaml` (sidebar, section host and footer), `src/Yaat.Client/Views/Settings/*Section.axaml` (one per section; order in `SettingsNavigation.cs`), `SettingsWindow.axaml.cs` (geometry key `"Settings"`, 1040×720, minimum 880×600), `src/Yaat.Client/ViewModels/SettingsViewModel.cs` (~2,100 lines; `Apply()` writes every setting and can run repeatedly; `ResetSection` per section), `src/Yaat.Client/ViewModels/SettingsSectionId.cs`, `UserPreferences.cs` (`CreateDefaults`).

A modal window with a sidebar of fifteen sections in six groups, a "Reset section" button on every section (pending until Apply or OK; disabled on the two link-only sections), and a footer OK, Apply and Cancel. Opened from Tools → Settings (General), and on Speech from the Speech Debug window and from `MainViewModel.PilotVoiceSettingsRequested`; a request while it is open brings the open window to the front at that section.

| Section | Controls | Contents |
|---|---|---|
| General | 10 (+ read-only ARTCC) | Initials (`UserInitials`), Discord status (`DiscordRichPresenceEnabled`, true); windows (raise together, always-on-top ×7, stored in `WindowGeometries[*].IsTopmost`). Reset section keeps the initials |
| Appearance | 10 | Font sizes (7 surfaces) + strips/vTDLS zoom; renderer (macOS) |
| Scenario defaults | 22 | Defaults applied at scenario load: solo training mode, pilot go-around probability, auto-accept handoffs + delay 0-60 s, command run delay min/max, default auto-delete, departure auto-delete distance, validate DCT fixes, auto-clear to land GND/TWR/APP/CTR (true/false/true/true), auto cross runways, auto pull-up to parallel, auto go-around on occupied runway, auto rejected takeoff, auto arrival spacing GND/TWR, VFR commands for IFR, RPO pilot speech, audible alert on pilot transmissions |
| Radar | 25 | Radar display (EuroScope tags, flash NoLndgClnc, conflict alerts, type-mismatch hints, ATPA); student scope sync (4); overlays (MVA tint GND/TWR/APP/CTR, speech bubbles ×5, TPA cone half-angle, scroll sensitivity); radar tint (assigned, unassigned, selected) |
| Ground | 16 | Ground display (3); ground colors (9) + brightness; layer brightness (3) |
| Aircraft list | links only | Font size → Appearance, Always on top → General; columns come from the column chooser |
| Strips and vTDLS | links only | Zoom → Appearance, Always on top → General |
| Terminal | 10 | Terminal channel colors |
| Command input | 2 | Signature help placement, auto-expand suggestion |
| Command verbs | verb grid + 3 | Per-command verb aliases (`CommandScheme`, only non-default aliases stored), "Try it out", Import…, Export…; Reset section restores the default verbs |
| Macros | grid + 6 | CRC aliases folder + Browse, macro grid (`Macros`), Add, Import…, Export Selected, Export All; Reset section clears every macro and the folder |
| Keys | 5 | Hotkeys (aircraft select, focus input, take control, always on top, quick bookmark) |
| Speech | 12 + actions | STT on, auto-focus after speech, Whisper model, LLM model + GPU layers, PTT key, telemetry, sample capture + cache size, pilot voice on, volume, radio FX; download/delete/CUDA/voice-pack/delete-samples buttons, which act at once and are labelled as not undone by Cancel |
| Audio devices | 2 | Input and output device |
| Server admin | 2 | Server admin mode + password |

**Persisted with no Settings UI** (changed elsewhere): Aircraft List column chooser (`ShowOnlyActiveAircraft`, alternating row color, `GridLayout`); View menu (timeline bar, favorites bar/panel, pop-out flags); favorites bar (`FavoritePanelColumns`); window profiles and geometries; radar/ground in-view toolbars (`RadarSettings`, `GroundSettings`, deconflict modes, layers, rotation, favorite video maps, METAR stations); terminal header (hidden kinds, timestamp mode); Connect window (saved servers); status-bar menu (live-traffic list filter); strips/vTDLS UI (split mode/ratio, vTDLS dark mode); automatic (recents, per-scenario history).

**Reachable outside the Settings window**
- Gear flyout (`src/Yaat.Client/Views/CommandInputView.axaml` ~:216, "Session Settings", the room's live values for all RPOs): solo mode, go-around probability, auto-delete, departure distance, auto-accept delay (`-1` = off), command run delay, validate DCT, the auto behaviours, RPO pilot speech. Granularity differs: one Auto cleared-to-land (Settings: four), one arrival spacing (Settings: GND and TWR). Flyout only: live traffic (SWIM) with ceiling, filters and assume; solo parking call-up interval; arrival generator rate; Releases.
- Mic indicator menu (`MainWindow.axaml` ~:527): enable speech recognition (same setting as the Speech section), speech debugging.
- Live-traffic status text menu: SWIM toggle and list-filter radios.
- Always-on-top hotkey writes the same flag as the seven General › Windows checkboxes.

**Defect (confirmed, fixed by YAAT-291):** `QuickBookmarkKeyButton` had no key-capture handlers, so its capture never ended. Every key-capture button carries the `key-capture` class, and `SettingsWindow.axaml.cs` serves them all with one handler.

## Import / export

All pickers go through `FilePickerFactory` / `IFilePickerService`.

| Data | Trigger | Format | Merge or replace | When applied | Code |
|---|---|---|---|---|---|
| Macros | Settings › Macros › Import… / Export Selected / Export All | JSON `List<SavedMacro>`, `*.yaat-macros.json` | Merge; name clash → `MacroImportWindow` (skip, overwrite, rename per item) | On Apply or OK | `SettingsWindow.axaml.cs` ~:230-312, ~:393-460 |
| Command verbs | Settings › Command verbs › Import… / Export… | `*.yaat-verbs.json` | Overlay: listed commands change, unknown skipped and reported | On Apply or OK | `SettingsWindow.axaml.cs` ~:313-392 |
| Favorites | Favorites bar Import / Export (flyout of sets + "Everything") | `*.yaat-favset.zip`, `*.yaat-favlibrary.zip`, single `.json` | Asked once: add (merge by id), replace all, or save current first | Immediately | `FavoritesBarView.axaml.cs` ~:541-760, `FavoriteExport.cs` |
| Aircraft List layout | Column header right-click → Column Chooser → Import… / Export… | `*.yaat-grid-layout.json` | Replace; a bad file is dropped silently | Immediately | `ColumnChooserWindow.axaml.cs` ~:207-300 |
| Window profiles | View → Window Profiles | Stored in preferences only | No file import/export | — | `MainWindow.axaml.cs` ~:2187-2300 |
| Preferences | — | — | No import/export | — | — |

Other file flows (not user configuration): recordings and bug bundles, export room as scenario, save weather, ASDE-X/SAID temp data, weather timeline and generator editors' Save As, speech samples.

Inconsistencies: four merge models; some imports wait for Apply or OK while others apply at once; three button placements (Settings section, code-built bar button with flyout, dialog pair), with only Settings showing an inline result; extensions differ (`.yaat-*.json`, `.yaat-*.zip`, bare `.json`); error handling differs (inline error, message box, log only, silent; macro export has no try/catch).

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

Every pop-out and editor window uses `WindowGeometryHelper`; the small modal dialogs (About, Column Chooser, Macro Import, Save Window Profile, airport picker, bug report, favorite set name, telemetry opt-in) do not.

## Duplication across surfaces

- Scenario behaviours: Settings defaults and the flyout's live values (above).
- Speech on/off: Speech section and mic menu.
- Always-on-top: seven General › Windows checkboxes and the hotkey.
- Favorites: View toggles the bar, the panel is a window, import/export/columns live inside the bar.
- Pop-out: one View toggle per view plus per-tab toggles under Strips/vTDLS; Window Profiles and Copy View Settings also set pop-out state.
- Grid layout: Reset in View, Column Chooser behind a header right-click.
- Settings whose effect shows in one window only: font sizes, zoom, colors, MVA tint, speech bubbles, student scope sync, ground display, TPA cone, scroll sensitivity.
