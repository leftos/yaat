# First-use tutorial (YAAT-1253)

An optional, guided tour of the desktop client that teaches a new instructor or RPO the views, command verbs and CRC aliases, macros, favorites and quick commands, the aircraft grid, and the settings worth setting before a first session. Planned for vNext+1.

Code map: the exploration behind this design is summarised in [Surfaces](#surfaces); every anchor there was read at main `ab573e384`.

## Owner rulings

- **Trigger**: a "Take the tour?" prompt at every launch until the tutorial is finished or declined ("Don't ask again"). It applies to everyone, existing users included, on the release that ships it. Help > Take the tour starts it any time.
- **Format**: coach marks. The window is dimmed, the target control sits in a spotlight cut-out with an amber ring, and a callout bubble anchored to it holds the step text, "Step n of N", progress dots, Back and Skip. Mock: [canvases/first-use-tutorial-format/](canvases/first-use-tutorial-format/index.html), lane A.
- **Interaction**: do-it steps. Each step asks the user to act and advances when it detects the action; every step can be skipped.
- **Room**: the tutorial runs against a live server room, and walks the user through connecting first. A user who is connected but cannot load a scenario (only mentors and I1+ can) is asked to join a room with aircraft, and offered to skip those steps and come back to them later.
- **Resume**: progress is saved per step; the next launch resumes at the next unfinished step.
- **Setup done with the user**: server and VATSIM sign-in; ARTCC data and facility; display and layout; the SAY and WRN speech bubbles. Speech recognition and pilot voice are not tutorial steps.
- **Aliases**: both kinds. A command verb is added hands-on and tried; CRC `.NAME` aliases get one explain-and-point step.
- **Favorites**: both. A favorite is made on the Favorites bar, then one quick command (the aircraft context-menu icon strip) is customised.

## Orchestrator decisions (not owner rulings; open to the owner)

- **Steps are data.** One step catalog (a C# list of records in `Yaat.Client`, no new file format): id, chapter, anchor, text, completion predicate, prerequisite. Text lives only there, so wording changes in one place. No localization layer: the client has none.
- **Anchors** are `x:Name` or `AutomationId` of a control, resolved with `FindControl` on the window that owns it; a tab is anchored by its `TabItem` name, never by index (tab order is hard-coded, `MainViewModel.cs:64`).
- **Completion** is detected from view-model state and store events, not clicks: `SelectedTabIndex`, a pop-out flag, `ShowFavoritesBar`, a favorite saved, a macro or command verb in the applied preferences (Settings stages changes until Apply/OK, so those steps complete on Apply), a `RequestSettings` section reached, `IsInRoom`, a command sent by the user. A step with no detectable outcome (the CRC aliases explanation) completes on Next.
- **Settings is modal over every window**, so the coach-mark layer is per window: one in `MainWindow`, one in `SettingsWindow`, each drawing only steps anchored in it. The tutorial controller is window-agnostic and tells the layer of the anchor's window to draw.
- **Persistence**: two new `UserPreferences` fields, `TutorialState` (NotStarted / InProgress / Finished / Declined) and `TutorialCompletedSteps` (step ids). Both are unbundled (`UserPreferences.Bundle.cs` `UnbundledKeys`, like identity): a settings bundle imported from another user must not mark their tour done. Reset-all-settings leaves them alone.
- **Prerequisites**: a step that needs nav data waits on `NavDbReady`; one that needs a room waits on `IsInRoom` with the ask-to-join callout; one that needs aircraft waits on a non-empty aircraft list. A wait shows the callout with "Skip for now", which leaves the step unfinished for the next launch.
- **Interruptions**: a disconnect, a room change or a server restart banner pauses the tour at the current step (the layer hides); it resumes when the step's prerequisite holds again.
- **Ground steps stay on viewing and selecting**, never on a specific hold-short placement, which YAAT-1214/1215/1216 are moving.

## Step outline

Chapters run in this order; within a room-dependent chapter, steps wait for their prerequisite.

1. **Welcome**: what the tour covers, how to skip, where to find it again (Help > Take the tour).
2. **Connect**: File > Connect…, the server (default YAAT1), VATSIM sign-in in the browser. Completes on a connected server.
3. **Your setup** (no room needed; in Settings): initials (General); ARTCC and facility (General; the ARTCC comes from the VATSIM profile, the step shows it and the data download status); display (Appearance: font, zoom; Radar and Ground colours, pointed at); the SAY and WRN speech bubbles (`ShowSpeechBubbles`, `ShowWarningSpeechBubbles`).
4. **Command verbs and CRC aliases** (Settings > Command verbs, Macros): add a verb, try it in "Try it out"; then one step explaining CRC `.NAME` aliases and the folder override.
5. **Macros** (Settings > Macros): add a macro and see its Preview.
6. **Join a room**: create or join a room with a scenario; a user who cannot load one gets the ask-to-join callout and "Skip for now".
7. **Views**: Aircraft List, Ground, Radar, Controllers, METAR tabs; pop one out and put it back; the terminal; save a layout (View > Layout).
8. **The aircraft grid**: select an aircraft, sort a column, open the Column Chooser.
9. **Commands**: send a first command to the selected aircraft from the command input (the `GETTING_STARTED.md` step 5 example), then run the macro from chapter 5.
10. **Favorites**: save a command as a favorite on the Favorites bar; open Settings > Quick commands and customise one icon; see it on the aircraft context menu.
11. **Done**: where the docs are (Help menu), the tour can be replayed.

## Testing

- One headless UI test per step resolves its anchor in its window (`tests/Yaat.Client.UI.Tests`), so renaming a control breaks a test, not the tour.
- Completion predicates are unit-tested against the view-model state they read.
- A client-driver run (yaat-server `docs/client-driver-mcp.md`) walks the whole tour once against a server from source before the item lands.
- New UI tests run in the suite YAAT-177 tracks (its hang); they wait or are gated as that item rules.

## Docs to change when it lands

`GETTING_STARTED.md` (the tour as the first step), `USER_GUIDE.md` (a Tutorial section), `docs/client-settings-and-menus.md` ("Couplings a change must keep": a renamed anchor control needs its step updated), `docs/architecture.md` (Task Index row: change the first-use tutorial), `CONTEXT.md` glossary entries (coach mark, tutorial step).

## Surfaces

- First run today: none. The closest once-only prompt is the speech-telemetry opt-in (`UserPreferences.SpeechTelemetryPromptShown`, `MainViewModel.SpeechTelemetry.cs:30-40`, `MainWindow.axaml.cs:3859`, `DialogPresenter.ShowModalAsync`).
- Views and hotkeys: `MainWindow.axaml:661-674` tabs (Aircraft List Ctrl+Shift+L, Ground Ctrl+Shift+G, Radar Ctrl+Shift+R, Controllers Ctrl+Shift+C, METAR Ctrl+Shift+M); Strips and vTDLS need a room (`:82,85`); Help menu `:197-209`.
- Settings: `Views/Settings/SettingsNavigation.cs:8-32` (16 sections), deep link `MainViewModel.RequestSettings(SettingsSectionId)`; Command verbs `CommandVerbsSection.axaml` (Try it out `:11-14`); Macros `MacrosSection.axaml` (CRC folder `:20-31`, Add Macro `:36`); Quick commands `QuickCommandsSection.axaml`.
- Favorites: `Views/FavoritesBarView.axaml`, `FavoritesPanelWindow.axaml`, `FavoritesEditorWindow.axaml`; store `Yaat.Client.Core/Services/FavoriteStore.cs`.
- Aircraft grid: `Views/DataGridView.axaml:78-184`, `ColumnChooserWindow.axaml`.
- Connect: `Yaat.Client.Core/Views/ConnectWindow.axaml`, `docs/vatsim-auth.md`; the RPO waiting overlay `MainWindow.axaml:1261`.
- Speech bubbles: `UserPreferences.cs:351-354` (`ShowSpeechBubbles`, `ShowWarningSpeechBubbles`).
- Overlays: Avalonia 12.1 has no teaching-tip control; the client's in-window overlays are Grid overlays in `MainWindow.axaml` (`ScenarioLoadOverlay` `:1307` and others). The coach-mark layer is new code.
