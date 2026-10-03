# Client surfaces inventory

What exists today, mapped from source for [the redesign](./README.md). Defaults come from `UserPreferences.SavedPrefs` (`src/Yaat.Client.Core/Services/UserPreferences.cs`). Line numbers are hints; confirm against source.

## Settings window

Files: `src/Yaat.Client/Views/SettingsWindow.axaml` (~1,700 lines), `SettingsWindow.axaml.cs` (geometry key `"Settings"`, 560×440), `src/Yaat.Client/ViewModels/SettingsViewModel.cs` (~1,900 lines; `Save()` writes every setting in one batch), `UserPreferences.cs`.

A modal window, nine tabs, footer Save and Cancel, plus "Reset to Defaults" (Commands tab only) and "Clear All Macros" (Macros tab only). Opened from Tools → Settings, from the Speech Debug window, and on the Speech tab from `MainViewModel.PilotVoiceSettingsRequested`.

| Tab | Controls | Contents |
|---|---|---|
| Identity | 2 (+ read-only ARTCC) | Initials (`UserInitials`), Discord status (`DiscordRichPresenceEnabled`, true) |
| Scenarios | 22 | Defaults applied at scenario load: solo training mode, pilot go-around probability, auto-accept handoffs + delay 0-60 s, command run delay min/max, default auto-delete, departure auto-delete distance, validate DCT fixes, auto-clear to land GND/TWR/APP/CTR (true/false/true/true), auto cross runways, auto pull-up to parallel, auto go-around on occupied runway, auto rejected takeoff, auto arrival spacing GND/TWR, VFR commands for IFR, RPO pilot speech, audible alert on pilot transmissions |
| Display | 43 | Font sizes (7 surfaces) + strips/vTDLS zoom; renderer (macOS); command input (signature help placement, auto-expand suggestion); radar display (EuroScope tags, flash NoLndgClnc, conflict alerts, type-mismatch hints, ATPA); student scope sync (4); ground display (3); overlays (MVA tint GND/TWR/APP/CTR, speech bubbles ×5, TPA cone half-angle, scroll sensitivity); windows (raise together, always-on-top ×7, stored in `WindowGeometries[*].IsTopmost`) |
| Colors | 28 + reset | Ground (9 colors, 4 brightness sliders), radar tint (assigned, unassigned, selected), terminal channels (10) |
| Commands | verb grid + 3 | Per-command verb aliases (`CommandScheme`, only non-default aliases stored), "Try it out", Import…, Export…, Reset to Defaults |
| Macros | grid + 5 | CRC aliases folder + Browse, macro grid (`Macros`), Add, Import…, Export Selected, Export All, Clear All |
| Audio | 2 | Input and output device |
| Speech | 12 + actions | STT on, auto-focus after speech, Whisper model, LLM model + GPU layers, PTT key, telemetry, sample capture + cache size, pilot voice on, volume, radio FX; download/delete/CUDA/voice-pack buttons |
| Advanced | 7 | Hotkeys (aircraft select, focus input, take control, always on top, quick bookmark), server admin mode + password |

**Persisted with no Settings UI** (changed elsewhere): Aircraft List column chooser (`ShowOnlyActiveAircraft`, alternating row color, `GridLayout`); View menu (timeline bar, favorites bar/panel, pop-out flags); favorites bar (`FavoritePanelColumns`); window profiles and geometries; radar/ground in-view toolbars (`RadarSettings`, `GroundSettings`, deconflict modes, layers, rotation, favorite video maps, METAR stations); terminal header (hidden kinds, timestamp mode); Connect window (saved servers); status-bar menu (live-traffic list filter); strips/vTDLS UI (split mode/ratio, vTDLS dark mode); automatic (recents, per-scenario history).

**Reachable outside the Settings window**
- Gear flyout (`src/Yaat.Client/Views/CommandInputView.axaml` ~:216, "Session Settings", the room's live values for all RPOs): solo mode, go-around probability, auto-delete, departure distance, auto-accept delay (`-1` = off), command run delay, validate DCT, the auto behaviours, RPO pilot speech. Granularity differs: one Auto cleared-to-land (Settings: four), one arrival spacing (Settings: GND and TWR). Flyout only: live traffic (SWIM) with ceiling, filters and assume; solo parking call-up interval; arrival generator rate; Releases.
- Mic indicator menu (`MainWindow.axaml` ~:527): enable speech recognition (same setting as the Speech tab), speech debugging.
- Live-traffic status text menu: SWIM toggle and list-filter radios.
- Always-on-top hotkey writes the same flag as the seven Display checkboxes.

**Suspected defect (unverified):** `SettingsWindow.axaml.cs` ~:71-88 attaches the key-capture handlers to five buttons but not to `QuickBookmarkKeyButton`.

## Import / export

All pickers go through `FilePickerFactory` / `IFilePickerService`.

| Data | Trigger | Format | Merge or replace | When applied | Code |
|---|---|---|---|---|---|
| Macros | Settings → Macros → Import… / Export Selected / Export All | JSON `List<SavedMacro>`, `*.yaat-macros.json` | Merge; name clash → `MacroImportWindow` (skip, overwrite, rename per item) | On Save | `SettingsWindow.axaml.cs` ~:136-372 |
| Command verbs | Settings → Commands → Import… / Export… | `*.yaat-verbs.json` | Overlay: listed commands change, unknown skipped and reported | On Save | `SettingsWindow.axaml.cs` ~:219-278 |
| Favorites | Favorites bar Import / Export (flyout of sets + "Everything") | `*.yaat-favset.zip`, `*.yaat-favlibrary.zip`, single `.json` | Asked once: add (merge by id), replace all, or save current first | Immediately | `FavoritesBarView.axaml.cs` ~:541-760, `FavoriteExport.cs` |
| Aircraft List layout | Column header right-click → Column Chooser → Import… / Export… | `*.yaat-grid-layout.json` | Replace; a bad file is dropped silently | Immediately | `ColumnChooserWindow.axaml.cs` ~:207-300 |
| Window profiles | View → Window Profiles | Stored in preferences only | No file import/export | — | `MainWindow.axaml.cs` ~:2187-2300 |
| Preferences | — | — | No import/export | — | — |

Other file flows (not user configuration): recordings and bug bundles, export room as scenario, save weather, ASDE-X/SAID temp data, weather timeline and generator editors' Save As, speech samples.

Inconsistencies: four merge models; some imports wait for Save while others apply at once; three button placements (Settings tab, code-built bar button with flyout, dialog pair), with only Settings showing an inline result; extensions differ (`.yaat-*.json`, `.yaat-*.zip`, bare `.json`); error handling differs (inline error, message box, log only, silent; macro export has no try/catch).

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
- Speech on/off: Speech tab and mic menu.
- Always-on-top: seven Display checkboxes and the hotkey.
- Favorites: View toggles the bar, the panel is a window, import/export/columns live inside the bar.
- Pop-out: one View toggle per view plus per-tab toggles under Strips/vTDLS; Window Profiles and Copy View Settings also set pop-out state.
- Grid layout: Reset in View, Column Chooser behind a header right-click.
- Settings whose effect shows in one window only: font sizes, zoom, colors, MVA tint, speech bubbles, student scope sync, ground display, TPA cone, scroll sensitivity.
