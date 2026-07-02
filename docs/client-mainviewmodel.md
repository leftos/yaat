# Client MainViewModel & App Orchestration

> Read this before touching `MainViewModel` (any partial), `MainWindow.axaml.cs`, or wiring up a new SignalR-driven
> client feature. `MainViewModel` is the integration seam between `ServerConnection`, the four sub-VMs
> (`Ground` / `Radar` / `VStrips` / `VTdls`), `UserPreferences`, the speech pipeline, and the window. Almost every
> client feature touches it, and four invariants here bite if you miss them: the **threading/marshaling contract**,
> the **three-path scenario bootstrap**, the **session-settings echo guard**, and the **shutdown re-entrancy protocol**.

This doc owns the orchestration / threading / lifecycle layer. For the file-tree index of each partial see
[architecture.md](architecture.md); for what happens once a command leaves the client see
[command-pipeline.md](command-pipeline.md) and [command-handlers.md](command-handlers.md); for the wire shapes of the
DTOs the VM consumes see [training-hub-contract.md](training-hub-contract.md).

## Overview

`MainViewModel` is a single root view-model (`ObservableObject`, CommunityToolkit.Mvvm) that `MainWindow` constructs
directly — there is **no DI** (`MainWindow.axaml.cs:57`, `new MainViewModel(new AvaloniaFilePickerService(this))`). It
owns one `ServerConnection` (`MainViewModel.cs:26`), the `UserPreferences` mirror, the `CommandInputController`, the
speech-recognition services, and the two visible sub-VMs `Ground` and `Radar` plus the dynamic `StripsEntries` /
`TdlsEntries` collections. The View talks to the VM through bindings and a small parameterless-event bridge; the VM
never reaches a control directly (MVVM).

The class is split across partial files by concern. The split is purely organizational — they all compile into one
`partial class MainViewModel`, so a private field declared in `MainViewModel.cs` is visible from
`MainViewModel.Timeline.cs`, etc.

## Partial-class ownership map

| File | Owns |
|---|---|
| `MainViewModel.cs` | Constructor + event subscriptions; `SendCommandAsync` pipeline; nav-data init (`InitializeNavDataAsync`); tab / pop-out index arithmetic (`IsTabVisible` / `FindNextVisibleTabIndex` / `EnsureSelectedTabVisible`); terminal-filter toggles + solo (`_isProgrammaticTerminalToggle`); session-settings echo guard (`_isApplyingSessionSettings`); `BuildSpeechContext`; speech-result handlers; `ApplySimState`; the `GridLayoutReset` / `RequestCommandInputFocus` / `TerminalFilterChanged` View-bridge events. |
| `MainViewModel.Rooms.cs` | Connect / disconnect / create-join-leave room; CRC lobby + room members; reconnect + server-restart banner; `ApplyRoomState` / `ClearRoomState`; aircraft assignments + RPO control (`TakeControlAsync` / `GiveControlAsync` / `ReleaseControlAsync`). |
| `MainViewModel.Scenario.cs` | Scenario load / unload; difficulty + setup plan (`ScenarioSetupPlan`); **`ApplyScenarioBootstrap`** (the fan-out router); `ApplyScenarioResult` (loader path); `OnScenarioLoaded` (broadcast path); `ClearScenarioState`. |
| `MainViewModel.Aircraft.cs` | SignalR aircraft handlers (`OnAircraftUpdated` / `OnAircraftSpawned` / `OnAircraftDeleted`); terminal-entry broadcast; speech-bubble attach; `OnPilotTransmissionReceived` → `PilotVoiceService`; `OnSimulationStateChanged`. |
| `MainViewModel.Timeline.cs` | Rewind / recording / export-progress; the command-marker buffer (`_commandMarkerHistory` + `_commandMarkerLock`); timeline-marker poll (`RefreshTimelineMarkersAsync`); save/load recording injects/reads the `bookmarks.json` archive entry. |
| `MainViewModel.Bookmarks.cs` | User-authored timeline bookmarks: `Bookmarks` collection, add / quick-add / next / prev, sorted insert + 500 cap, `BookmarkNamePromptRequested` event (view shows the name popup), `SetBookmarks` / `SnapshotBookmarks` for recording round-trip. Cleared at session boundaries alongside `Aircraft.Clear()`. |
| `MainViewModel.Weather.cs` | Weather load / clear; `OnWeatherChanged`. |
| `MainViewModel.Strips.cs` / `MainViewModel.Tdls.cs` | Multi-facility strips / vTDLS tabs: open/close per-facility entries, the `Subscribe*Entry` / `Unsubscribe*Entry` collection-changed plumbing, per-entry pop-out persistence. |
| `MainViewModel.Favorites.cs` | Quick-command favorites bar/panel. |
| `MainViewModel.ArrivalGenerators.cs` | Live arrival-generator editing. |
| `ScenarioBootstrap.cs` | The `ScenarioBootstrap` record — the common projection the three activation paths feed `ApplyScenarioBootstrap`. |
| `Views/MainWindow.axaml.cs` | View side: creates the VM, subscribes the bridge events, materializes a TabItem-or-Window per tab/entry, owns the shutdown protocol (`OnClosing`). |

## Threading & marshaling contract

`ServerConnection` raises its events on a **SignalR background thread**. The Avalonia UI objects the VM mutates —
the `Aircraft`, `TerminalEntries`, `CrcLobbyClients`, `CrcRoomMembers`, `RoomMembers`, `TimelineMarkers`
`ObservableCollection`s and every `[ObservableProperty]` setter — are **UI-thread-only**. So the rule is:

> **Every `ServerConnection` event handler that mutates an `ObservableCollection` or an observable property must wrap
> its body in `Avalonia.Threading.Dispatcher.UIThread.Post(...)`.**

This holds for `OnAircraftUpdated`/`Spawned`/`Deleted`, `OnSimulationStateChanged`, `OnTerminalEntry`,
`OnReconnecting`/`OnReconnected`/`OnConnectionClosed`, `OnServerRestarting`/`ReadyComplete`, `OnRoomAvailableForCid`,
`OnRoomMemberChanged`, `OnCrcLobbyChanged` (logs first, then posts — `MainViewModel.Rooms.cs:697`),
`OnCrcRoomMembersChanged`, `OnWeatherChanged`, `OnArrivalGeneratorsChanged`, `OnPositionDisplayChanged`,
`OnAircraftAssignmentsChanged`, `OnScenarioLoaded`/`OnScenarioUnloaded`, `OnSessionSettingsChanged`,
`OnKickedFromRoom`, `OnRoomRetired`. Omit the `Post` and you get intermittent cross-thread crashes that unit tests
will not catch.

**The deliberate exception:** `OnPilotTransmissionReceived` (`MainViewModel.Aircraft.cs:126`) does **not** marshal. It
only reads preference/scalar state and calls `_pilotVoice.Enqueue(...)`; it never touches an `ObservableCollection`
or observable property, so it is safe to run on the SignalR thread. The contract is "marshal before touching UI
state," not "marshal unconditionally" — but when in doubt, marshal.

Four other background-thread → UI-thread crossings exist outside the SignalR handlers:

- **Global PTT key hook.** `GlobalKeyHookService` fires `KeyDown`/`KeyUp` on a background thread (so PTT works while
  another app is focused). `MainWindow.OnGlobalKeyDown`/`OnGlobalKeyUp` (`MainWindow.axaml.cs:2601`/`2624`) post to the
  UI thread before calling `vm.SpeechService.StartPtt()`/`StopPtt()`, edge-triggered via `_globalPttActive`.
- **Speech service callbacks.** `_speechService.StatusChanged` and `CommandReady` fire off-thread;
  `HandleSpeechServiceStatusChange` and `HandleSpeechServiceCommandReady` (`MainViewModel.cs:1535`/`1540`) both post.
- **Speech context provider (a *pull*, not a callback).** `SpeechRecognitionService.ProcessPipelineAsync` pulls the
  context provider — `BuildSpeechContext` — on its `Task.Run` background thread at PTT release. `BuildSpeechContext`
  reads UI-thread-only state (`Aircraft`, `SelectedAircraft`, `Ground.DomainLayout`), so it self-marshals with a
  `Dispatcher.UIThread.CheckAccess()` guard and `Dispatcher.UIThread.Invoke(...)` — `Invoke` (blocking, returns the
  value) rather than `Post` because the caller needs the result. No deadlock: the pipeline is fire-and-forget, so the
  UI thread is never blocked waiting on it.
- **Export-recording progress.** `_connection.ExportRecordingProgress` → `OnExportRecordingProgress`
  (`MainViewModel.Timeline.cs:280`) posts. The download-update progress callback in `UpdateNowAsync`
  (`MainViewModel.cs:1286`) posts too.

**The one off-UI-thread shared field:** the command-marker buffer. `RecordCommandMarker`
(`MainViewModel.Timeline.cs:314`) appends to `_commandMarkerHistory` under `_commandMarkerLock`
(`MainViewModel.Timeline.cs:300`, a `System.Threading.Lock`) because the same buffer is read under that lock by the
periodic `RefreshTimelineMarkersAsync` poll. The lock guards only the list; the visible `TimelineMarkers` collection
is still mutated via `Dispatcher.UIThread.Post`.

## Lifecycle: constructor wiring order

`MainViewModel(IFilePickerService)` (`MainViewModel.cs:1085`) wires things in a deliberate order:

1. **Preferences mirror** — `_isSpeechEnabled`, `_sessionSoloTrainingMode`, etc. seeded from `_preferences`.
2. **Speech pipeline** — order matters: `AudioCaptureService` → `WhisperSttEngine` → `LocalLlmService` → the two LLM
   consumers (`LocalLlmCommandMapper`, `LocalLlmCallsignResolver`) → `SpeechRecognitionService` (needs all of them).
   `_speechService.StatusChanged`/`CommandReady` are hooked here, and a fire-and-forget `PrewarmAsync` runs when
   `SpeechEnabled` so the first PTT press doesn't stall on model load. See
   [speech-recognition-pipeline.md](speech-recognition-pipeline.md).
3. **Aircraft view filter** — `AircraftView = new DataGridCollectionView(Aircraft)` with the active/text filter.
4. **Sub-VM construction** — `Ground` and `Radar` built with the `SendCommandForViewAsync` callback and an aircraft
   lookup; `Ground.ShownAirportChanged` re-publishes `GroundShownAirportId`.
5. **Strips/TDLS student entries** — `StripsEntries[0]` and `TdlsEntries[0]` are created (always element 0), then
   `Subscribe*Entry` + the `CollectionChanged` hooks are attached.
6. **Pop-out flag restore** — the three fixed views (`IsDataGridPoppedOut`/`IsGroundViewPoppedOut`/
   `IsRadarViewPoppedOut`/`IsTerminalDocked`) and the **student** strips/TDLS entries restore from preferences.
7. **~25 `ServerConnection` event subscriptions** (`MainViewModel.cs:1180`-`1204`).
8. **Fire-and-forget init** — `InitializeNavDataAsync()`, `_vnasConfigService.InitializeAsync()`,
   `CheckForUpdateAsync()`.

`InitializeNavDataAsync` (`MainViewModel.cs:1215`) loads NavData + CIFP and calls `NavigationDatabase.Initialize(...)`,
then flips `_commandInput.NavDbReady = true` and pushes elevation lookups into `Radar` / `Ground`. Until it completes,
`NavigationDatabase.Instance` is null and `BuildSpeechContext` degrades (see Footguns).

## Scenario activation — three paths, one router

A scenario becomes active through **three** distinct entry points, and they all **must** funnel through
`ApplyScenarioBootstrap` (`MainViewModel.Scenario.cs:474`):

| Path | Trigger | Entry method | Carries |
|---|---|---|---|
| **Loader** | This client invoked `LoadScenario` | `ApplyScenarioResult(LoadScenarioResultDto)` (`Scenario.cs:407`) | full `AllAircraft`, sim state, session settings; also pushes **this RPO's** preferences to the server |
| **Broadcast** | Another client loaded a scenario | `OnScenarioLoaded(ScenarioLoadedDto)` (`Scenario.cs:435`) | same fields; does **not** push preferences (only the loading RPO does) |
| **Join / reconnect** | `JoinRoom` returned a room with a scenario | `ApplyRoomState(RoomStateDto)` (`Rooms.cs:594`) | snapshot incl. `ElapsedSeconds`/`IsPlayback`/`TapeEnd` |

`ScenarioBootstrap` (`ScenarioBootstrap.cs`) is a small record that exists precisely so the three differently-named
DTOs project into one shape (`ScenarioId`, `ScenarioName`, `PrimaryAirportId`, `PositionDisplayConfig`,
`FlightStripsConfig`, `Aircraft`). `ApplyScenarioBootstrap` then does the work common to all three:

- sets `ActiveScenarioId`/`Name`/`PrimaryAirportId` and `_commandInput.PrimaryAirportId`,
- rebuilds the `Aircraft` collection from the DTOs (recomputing `InitialDelayedSpawnCount` /
  `PendingDelayedSpawnCount`),
- fans out to `Radar.ApplyScenarioBootstrap` / `Ground.ApplyScenarioBootstrap`, `VStrips.ApplyBayConfig`, and the
  vTDLS bootstrap (`BootstrapStudentTdlsAsync`).

**Per-path extras stay at the call site:** the `ApplySimState` signature differs (the join path passes elapsed/
playback/tape-end; loader & broadcast use the 2-arg form), `_studentPositionType` and `_isAutoClearedToLand` are set
by the loader/broadcast paths, the `ApplySessionSettingsFrom*` adapter differs, and the loader path additionally fires
the `Send*` preference pushes.

`ClearScenarioState` (`Scenario.cs:578`) is the symmetric teardown: it nulls the active-scenario properties, clears
`Aircraft`, clears the ground layout / video maps / shown paths, and resets session settings to a neutral
`SessionSettingsDto`.

## Session-settings echo suppression

The session-settings flyout binds 13 `[ObservableProperty]` fields (`SessionAutoDeleteIndex`,
`SessionAutoAcceptDelaySeconds`, `SessionAutoClearedToLand`, `SessionAutoCrossRunway`, `SessionValidateDctFixes`,
`SessionSoloTrainingMode`, the three solo-pacing rates, the two `HasSolo*Source` flags, `SessionRpoShowPilotSpeech`,
…). Each has an `OnXxxChanged` partial that re-sends the new value to the server. The problem: when the **server**
broadcasts a settings change, applying it to the bound property would re-trigger `OnXxxChanged`, which would re-send
it — a ping-pong.

The guard is `_isApplyingSessionSettings` (`MainViewModel.cs:2434`). `ApplySessionSettings(SessionSettingsDto)`
(`MainViewModel.cs:2441`) sets it `true`, writes all 13 properties, then sets it `false`. Every `OnXxxChanged`
handler early-returns while the flag is set (e.g. `OnSessionAutoCrossRunwayChanged`,
`OnSessionSoloGoAroundProbabilityPercentChanged`), so the broadcast lands without echoing back.

Because the same 13 fields arrive under four different DTO shapes, there are **four adapters** that all build a
`SessionSettingsDto` and call `ApplySessionSettings`:

- `ApplySessionSettings(SessionSettingsDto)` — the base, used by the live `OnSessionSettingsChanged` broadcast.
- `ApplySessionSettingsFromRoom(RoomStateDto)` (`Scenario`-adjacent in `MainViewModel.cs:2463`).
- `ApplySessionSettingsFromScenarioLoaded(ScenarioLoadedDto)` (`MainViewModel.cs:2484`).
- `ApplySessionSettingsFromLoadScenarioResult(LoadScenarioResultDto)` (`MainViewModel.cs:2505`).

Add a session setting and **all four** adapters plus the `SessionSettingsDto` (client + server) and the four
source DTOs must change in lockstep — see [training-hub-contract.md](training-hub-contract.md) for the cross-repo
fan-out.

Note the solo-pacing rates funnel through one server call, `SetSoloPacingRatesAsync(parking, arrival, goAround)`, not
three separate setters — `OnSessionSoloPacingRateChanged` / `OnSessionSoloParkingInitialCallupIntervalSecondsChanged` /
`OnSessionSoloGoAroundProbabilityPercentChanged` all clamp then call it.

The **terminal-filter solo** feature uses the identical guard pattern under a different flag,
`_isProgrammaticTerminalToggle` (`MainViewModel.cs:821`): `ApplyVisibilityProgrammatic` sets it while flipping the
`Show*Entries` toggles so `OnTerminalToggleChanged` skips persistence and the cancel-solo side effect. There is one
`Show<Kind>Entries` toggle per `TerminalEntryKind` (Command/Response/System/Say/Warning/Error/Chat/Tdls/**Strip**); adding
a channel means touching this toggle set plus `IsEntryVisible`, `CurrentVisibleKinds`, `ApplyVisibilityProgrammatic`,
`PersistTerminalFilters`, the constructor seed, the `TerminalPanelView` toggle button + `EnumerateCategoryToggles`, and the
`TerminalColorScheme`/colorizer/Settings color row (the `Strip` channel — strip command echoes + feedback, tagged server-side
in `RoomEngine.HandleStripCmd` — is the most recent example).

## The command pipeline entry point — `SendCommandAsync`

`SendCommandAsync` (`MainViewModel.cs:1618`) is the client-side resolution chain that runs **before** anything reaches
the server. It does the work that `command-pipeline.md` summarizes in one line ("partial callsign resolution"). In
order:

1. **`** ` override prefix** — strips a leading `** ` and sets `forceOverride`, which re-prepends `** ` onto the
   canonical string before sending (bypasses assignment-ownership checks server-side).
2. **Chat prefix** — a leading `'`, `/`, or `>` routes the remainder to `SendChatAsync` and returns.
3. **Global command** — `CommandSchemeParser.Parse` + `IsGlobalCommand`; dispatched via `HandleGlobalCommand` with no
   callsign. Exception: the `AS {tcp} {track_command}` *prefix* form is per-aircraft (the standalone `AS {tcp}` is
   global), so it is **not** taken here.
4. **Single-token select** — if the input is one token with no `,`/`;` and matches a callsign, just select that
   aircraft and return (no command sent).
5. **Macro expand** — `MacroExpander.TryExpand` so callsign-prefix resolution sees real verbs.
6. **Callsign-prefix resolve** — `CallsignPrefixResolver.Resolve`; `Ambiguous` surfaces a status message and aborts;
   `Resolved` sets `target` + strips the prefix from `commandText`.
7. **Argument rewrite** — `CallsignArgumentResolver.TryRewrite` canonicalizes partial callsigns inside arguments
   (`FOLLOW UA` → `FOLLOW UAL123`).
8. **RPO control commands** — `TryHandleRpoCommand` (`MainViewModel.cs:2097`) intercepts `TAKE` / `GIVE <initials>` /
   `GIVEUP` (client-local ownership ops, bypass the command pipeline entirely).
9. **`ParseCompound`** — on failure, falls back to **solo natural-language** dispatch
   (`TryDispatchSoloNaturalCommandAsync`) when `SessionSoloTrainingMode` is on; otherwise reports the parse error.
10. **No-target half-strip** — when no aircraft resolved, `HSC`/`HSA`/`HSD` run globally with an empty callsign
    (`IsHalfStripVerb`); otherwise "No aircraft matched."
11. **Dispatch** — `_connection.SendCommandAsync(callsign, canonical, initials)`. On success,
    `RecordCommandMarker` drops a timeline tick; `AddHistory` records the canonical (callsign stripped via
    `CommandHistoryFormatter`); the input box is cleared; `CommandStatusResolver.Resolve` sets the status text.

The server side picks up from `SendCommand` — see [command-pipeline.md](command-pipeline.md) and
[command-handlers.md](command-handlers.md). Sub-VMs (Ground/Radar/Strips/TDLS) dispatch through the simpler
`SendCommandForViewAsync(callsign, command, initials)` (`MainViewModel.cs:2390`), which skips the resolution chain
because the caller already knows the callsign.

`HandleSpeechServiceCommandReady` (`MainViewModel.cs:1540`) feeds this chain: it prepends the recognized callsign onto
the canonical command (`"SWA123 FH 270"`) so the `CallsignPrefixResolver` path auto-dispatches on Enter, then raises
`RequestCommandInputFocus` when the user opted in.

## Tabs & pop-out windows

The main tab control has a fixed-index layout, hard-coded in both the VM arithmetic and the View materialization:

```
0 = Aircraft List (DataGrid)   1 = Ground View   2 = Radar View
3 .. 3+StripsEntries.Count-1   = Strips tabs (student entry first)
then TdlsEntries               = vTDLS tabs (student entry first)
```

`GroundViewTabIndex = 1` (`MainViewModel.cs:64`) is the only named constant; the rest is positional.
`IsTabVisible(index)` (`MainViewModel.cs:528`) and `FindNextVisibleTabIndex` (`MainViewModel.cs:503`) compute
`stripsBase = 3` and `tdlsBase = stripsBase + StripsEntries.Count` directly — the same order
`MainWindow.axaml.cs` appends `TabItem`s via `tabControl.Items.Add`. Reordering tabs means touching both sides.

`EnsureSelectedTabVisible` (`MainViewModel.cs:443`) shifts `SelectedTabIndex` off a popped-out tab to the next docked
one (wrapping; returns the same index when everything is popped out, in which case the whole tab area is hidden via
the `IsAnyTabVisible` binding). It's called internally on any pop-out flip (`OnTabPoppedOutChanged`) **and** externally
by `MainWindow` once the dynamic Strips/TDLS `TabItem`s are materialized — because Avalonia's two-way
`SelectedIndex` binding doesn't propagate VM→TabControl when the VM value was set before the dynamic tabs existed
(`MainWindow.axaml.cs:267`-`284`).

**Pop-out persistence is asymmetric.** The three fixed views and the **student** Strips/TDLS entry (index 0) persist
their popped-out flag to `UserPreferences` (`OnStripsEntryPropertyChanged` only calls `SetPoppedOut("VStrips", …)`
when `entry.IsStudentEntry`, `MainViewModel.Strips.cs:107`). Extra per-facility tabs are session-scoped and always
start docked. The `Subscribe*Entry` / `Unsubscribe*Entry` pairing (driven by the `CollectionChanged` handler) must
stay balanced or pop-out bookkeeping leaks handlers.

## View ↔ VM event bridge

The VM cannot reach a control (MVVM), so two parameterless events let it poke the View:

- **`GridLayoutReset`** (`MainViewModel.cs:761`) — raised by the `ResetGridLayout` command; `MainWindow` subscribes and
  forwards to `ResetLiveGrid(dataGrid)` (`MainWindow.axaml.cs:217`).
- **`RequestCommandInputFocus`** (`MainViewModel.cs:770`) — raised after a speech transcription populates `CommandText`
  (when `AutoFocusInputAfterSpeech` is set); `MainWindow` forwards to `CommandInputView.FocusCommandInput()`
  (`MainWindow.axaml.cs:226`). A new "VM needs to poke a control" requirement should follow this pattern, not a direct
  control reference.

(`TerminalFilterChanged` is a third such event, consumed by the terminal view's filter predicate.)

On the property side, `MainWindow.OnViewModelPropertyChanged` (`MainWindow.axaml.cs:1037`) switches on changed
property names to drive side effects the bindings can't express: pop-out window create/close (`IsTerminalDocked`,
`IsDataGridPoppedOut`, `IsGroundViewPoppedOut`, `IsRadarViewPoppedOut`), content-grid row resizing (`IsAnyTabVisible`),
and recent-menu enablement (`ActiveScenarioId` / `ActiveRoomId`).

## Shutdown protocol

`MainWindow.OnClosing` is `async void` (`MainWindow.axaml.cs:2495`) and **re-enters itself**: when a scenario is
loaded it shows a confirm-exit dialog, cancels the first close (`e.Cancel = true`), and a second `OnClosing` fires
from the inner `Close()` with a fresh args object.

- **`_isMainWindowClosing` is sticky** — only ever set to `true`, never reset. Without that, when entry #1 resumes
  after the `await dialog.ShowDialog(this)` it would overwrite the flag back to `false` using its stale
  `e.Cancel = true`, making the child pop-out windows treat the cascade shutdown as a manual close and clobber their
  persisted pop-out flags (`MainWindow.axaml.cs:2562`-`2575`).
- **`AppLifetime.MarkShuttingDown()`** (`AppLifetime.cs:14`) is the cross-window signal. Pop-out `Closing` handlers
  call `IsClosingFromShutdown(_isMainWindowClosing)` (`MainWindow.axaml.cs:1671`), which is
  `isMainWindowClosing || AppLifetime.IsShuttingDown`, and only revert the dock flag when the user closed *that*
  window manually — covering shutdown paths (File > Exit, Velopack restart, `CancelKeyPress`) that close pop-outs
  before `MainWindow.OnClosing` runs.
- **Velopack restart bypasses the close pipeline.** `UpdateNowAsync` (`MainViewModel.cs:1274`) calls
  `WindowGeometryHelper.FlushAllSavedGeometries()` (`MainViewModel.cs:1293`) **before** `ApplyUpdateAndRestart`, because Velopack's restart
  never fires `Window.Closing`, so the per-window geometry save (hooked there by `WindowGeometryHelper`) would
  otherwise be lost.

When `_isMainWindowClosing` is set, `OnClosing` also disposes the global key hook and cancels the auto-connect CTS.

## Footguns

- **Marshal SignalR handlers that touch UI state.** Every `ServerConnection` event handler that mutates an
  `ObservableCollection` or observable property wraps its body in `Dispatcher.UIThread.Post`. Omit it and you get
  intermittent cross-thread crashes unit tests won't catch. `OnPilotTransmissionReceived` is the one deliberate
  exception (touches no UI state); don't generalize from it.
- **Three scenario-activation paths, one router.** `ApplyScenarioResult` (loader), `OnScenarioLoaded` (broadcast),
  and `ApplyRoomState` (join/reconnect) all go through `ApplyScenarioBootstrap`. Wiring a new scenario-derived field
  into only the loader path silently breaks it for joiners and restart-restore rejoins. Add it to the
  `ScenarioBootstrap` record so all three paths carry it.
- **Session settings need the echo guard.** A new `Session*` `[ObservableProperty]` with an `OnXxxChanged` that
  re-sends to the server must early-return on `_isApplyingSessionSettings`, and the field must be added to all four
  `ApplySessionSettingsFrom*` adapters + `SessionSettingsDto`. Miss the guard and the value ping-pongs with the
  server or the broadcast overwrites the user's local edit; miss an adapter and it drops on one of the
  join/load/live paths. The terminal-filter toggles use the same pattern under `_isProgrammaticTerminalToggle`.
- **`OnClosing` re-enters; `_isMainWindowClosing` must stay sticky.** Resetting it makes pop-out windows treat the
  cascade shutdown as a manual close and clobber persisted pop-out flags. `AppLifetime.MarkShuttingDown()` is the
  cross-window signal for shutdown paths that don't go through `MainWindow.OnClosing`.
- **Velopack restart skips `Window.Closing`.** Call `WindowGeometryHelper.FlushAllSavedGeometries()` manually before
  `ApplyUpdateAndRestart` or per-window geometry is lost.
- **Tab index arithmetic is positional and fragile.** `0/1/2 = Aircraft/Ground/Radar`, then Strips at base 3, then
  TDLS — the same order `MainWindow.axaml.cs` appends `TabItem`s. `IsTabVisible`/`FindNextVisibleTabIndex` hard-code
  it; reordering tabs requires touching both the VM arithmetic and the View materialization. After wiring the dynamic
  tabs, `MainWindow` pushes `SelectedIndex` explicitly because Avalonia's two-way binding doesn't propagate
  VM→TabControl when the VM value was set before the tabs materialized.
- **The VM can't touch controls.** Focus / grid-reset go through the parameterless `RequestCommandInputFocus` /
  `GridLayoutReset` events that `MainWindow` forwards via `FindControl`. A new "poke a control" need follows this
  pattern, not a direct reference.
- **`BuildSpeechContext` degrades until both NavDb and a ground layout have loaded.**
  `GroundViewModel` owns the reconstructed domain ground layout (`Ground.DomainLayout`); `BuildSpeechContext`
  (`MainViewModel.cs:1381`) borrows it for taxiway names and reads `NavigationDatabase.Instance` for runways /
  custom-fix patterns / procedures. Until `InitializeNavDataAsync` completes **and** a ground layout has loaded, the
  taxiway / runway / procedure sets come back empty and the speech rule mapper + LLM fallback skip those checks.
- **Pop-out persistence is asymmetric.** Only the student Strips/TDLS entry (index 0) and the three fixed views
  persist their popped-out flag; extra per-facility tabs are session-scoped and always start docked. Keep the
  `Subscribe*Entry` / `Unsubscribe*Entry` pairing balanced or handler subscriptions leak.
- **`SimulationStateChanged` carries five args.** `OnSimulationStateChanged(paused, rate, elapsed, isPlayback,
  tapeEnd)` and `ApplySimState` share the full set, but `ApplyScenarioResult` / `OnScenarioLoaded` call the 2-arg
  `ApplySimState` form (elapsed defaults to 0). Only the join/reconnect path seeds elapsed/playback/tape-end — don't
  assume a fresh load knows the elapsed clock until the first broadcast lands.
