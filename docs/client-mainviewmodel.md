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
| `MainViewModel.cs` | Constructor + event subscriptions; `SendCommandAsync` pipeline; nav-data init (`InitializeNavDataAsync`); tab / pop-out index arithmetic (`IsTabVisible` / `FindNextVisibleTabIndex` / `EnsureSelectedTabVisible`); terminal-filter toggles + solo (`_isProgrammaticTerminalToggle`); session-settings echo guard (`_isApplyingSessionSettings`); `BuildSpeechContext`; speech-result handlers; `ApplySimState`; the `GridLayoutReset` / `RequestCommandInputFocus` / `TerminalFilterChanged` View-bridge events; `RequestSettings(SettingsSectionId? section)` → `SettingsRequested`, the one path every Settings opener takes (view right-click menus, the Open Settings key, status-bar links, the pilot-voice banner), handled by `MainWindow.ShowSettingsDialogAsync`; a null section (the Open Settings key, Ctrl+, by default) opens the window at General, and a request that reaches an open window from code brings it to the front (at the requested section, or the current one for null). Settings is modal over every window `Views/OpenWindows.cs` lists, pop-outs included, without touching `IsEnabled`: `SettingsWindow` blocks their pointer, key and text input with tunnel handlers and their tap gestures with class handlers, so the windows keep their look for the live preview and a press brings Settings forward; the Open Settings key therefore does nothing while Settings is open (it is not scoped to Settings itself). `ShowSettingsDialogAsync` records the focused element of the active window before opening and `RestoreFocusAfterSettings` puts focus back on close, falling back to the command input when that element is gone, hidden, disabled or in a menu (Tools › Settings… leaves focus on its menu item). `SettingsImported` / `NotifySettingsImported(itemTypes)`: an Import / Export hub opened outside Settings (Tools › Import / Export…, the favorites panel, the column chooser) applies at once and raises it; `MainWindow.OnSettingsImported` runs `ApplyCommittedSettings` and re-applies the grid layout to the live grids when it was imported. |
| `MainViewModel.Rooms.cs` | Connect / disconnect / create-join-leave room; CRC lobby + room members; reconnect + server-restart banner; `ApplyRoomState` / `ClearRoomState` (which also seed and clear `RoomLoadingBy`); `JoinCreatedRoomAsync` (closes a room this client created but could not join); aircraft assignments + RPO control (`TakeControlAsync` / `GiveControlAsync` / `ReleaseControlAsync`); `PermittedArtccs` + `SelectedCreateArtccId` (the Create Room ARTCC picker, shown only with operator grants) and `SetActiveArtcc` — `UserPreferences.ArtccId` is "the ARTCC in effect": home at sign-in, the room's `CreatorArtccId` while in a room (adopted in `ApplyRoomState`, restored in `ClearRoomState`), which every ARTCC-scoped consumer (scenario picker, live session, weather, CRC aliases, web-client URLs) reads. |
| `MainViewModel.Scenario.cs` | Scenario load / unload; difficulty + setup plan (`ScenarioSetupPlan`), parsed off the UI thread; **`ApplyScenarioBootstrap`** (the fan-out router); `ApplyScenarioResult` (loader path); `OnScenarioLoaded` (broadcast path); `ClearScenarioState`; the load overlay's feed and the room-loading gate (`OnScenarioLoadProgress`, `OnRoomLoadingChanged`, `SetRoomLoadingBy`, `ReportLoadFailure`; see **Scenario load overlay and the room-loading gate**); the Discord rich-presence publish (`RichPresence`, `StartRichPresence`, `RefreshRichPresence`). |
| `LoadOverlayViewModel.cs` | The scenario-load overlay's state (`LoadOverlay`, owned by `MainViewModel`) and its `LoadStepViewModel` rows. Not a partial. |
| `MainViewModel.Aircraft.cs` | SignalR aircraft handlers (`OnAircraftUpdated` / `OnAircraftSpawned` / `OnAircraftDeleted`); terminal-entry broadcast; speech-bubble attach; `OnPilotTransmissionReceived` → `PilotVoiceService`; `OnSimulationStateChanged`. |
| `MainViewModel.Timeline.cs` | Rewind / recording / export-progress; the command-marker buffer (`_commandMarkerHistory` + `_commandMarkerLock`); timeline-marker poll (`RefreshTimelineMarkersAsync`); save/load recording injects/reads the `bookmarks.json` archive entry. |
| `MainViewModel.Bookmarks.cs` | Shared timeline bookmarks — server-authoritative, synced across RPOs (GitHub issue #288): `Bookmarks` mirror collection; add / quick-add / rename / delete route through hub RPCs (`ServerConnection.Add/Rename/DeleteBookmarkAsync`); `ApplyBookmarks` reconciles from the `BookmarksChanged` broadcast / `RoomStateDto.Bookmarks` join seed; `BookmarkNamePromptRequested` event (view shows the name popup); `SnapshotBookmarks` for the recording-save `bookmarks.json` stitch. Cleared at session boundaries alongside `Aircraft.Clear()`. Also owns the client half of the `BM` verb (`TryHandleBookmarkLocallyAsync`): `BM LIST` prints to this client's terminal only and `BM GO/NEXT/PREV` drive `RewindToSeconds`, while add/rename/delete fall through to `MainViewModel.HandleBookmarkGlobalCommand` → `SendCommandAsync` → the server. |
| `MainViewModel.PilotVoiceWarning.cs` | Solo missing-pilot-voice warning: `ShowPilotVoiceWarning` (banner), `ConfirmResumeAsync` (the once-per-session modal through the view's `PilotVoiceWarningPrompt` hook, called by `TogglePauseAsync`, typed `UNPAUSE` and `TogglePlayback`), the banner's Voice settings button (`RequestSettings(SettingsSectionId.Speech)`), `ResetPilotVoiceWarningSession` (scenario/recording load, room change; not a reconnect). |
| `MainViewModel.Weather.cs` | Weather load / clear; `OnWeatherChanged`. |
| `MainViewModel.LiveSession.cs` | Live-traffic sessions (no authored scenario): `IsLiveSession` (server-computed, mirrored from the three activation DTOs), the `LIVE` / `PAUSED` / `PLAYBACK` badge (`LiveSessionBadgeText` / `DescribeLiveSession`), `ShowGoLive` + `GoLiveCommand`, `StartLiveSessionAsync` (opens the load overlay titled `Live session`, applies the result through `ApplyScenarioResult`, then live weather), `CanStartLiveSession` (`CanLoadScenario` and a live-traffic feed, so it is off while a load runs). See [live-traffic.md](live-traffic.md) "Live sessions". |
| `MainViewModel.Strips.cs` / `MainViewModel.Tdls.cs` | Multi-facility strips / vTDLS tabs: open/close per-facility entries, the `Subscribe*Entry` / `Unsubscribe*Entry` collection-changed plumbing, per-entry pop-out persistence. |
| `MainViewModel.ViewInstances.cs` | Extra Radar/Ground windows (#434): `ExtraRadarViews` / `ExtraGroundViews` (`RadarViewInstance` / `GroundViewInstance`, ordinals ≥ 2), the `CreateRadarViewModel` / `CreateGroundViewModel` factories the primaries also use, `OpenExtra*View` / `CloseExtra*View` / `ReconcileExtraViews` (profiles), late seeding from the stashed scenario bootstrap / position config / airport position, and the `AllRadarViews` / `AllGroundViews` enumerators every fan-out site loops over. See **Extra view instances** below. |
| `MainViewModel.CrcAliases.cs` | CRC alias support: the `CrcAliasStore` instance, `BuiltInDotCommands` (the reserved names YAAT's own dot commands own), `LoadCrcAliasesAsync` (startup + ARTCC change + Settings Apply), `TryHandleCrcAlias` / `RunCrcAlias`, and `BuildCrcAliasContext` (flight-plan fields for `$dep`/`$arr`/`$route`/`$fullroute`, read off `SelectedAircraft`). Client-only — nothing here reaches the server. |
| `MainViewModel.Favorites.cs` | Quick-command favorites bar/panel. `DisplayFavorites` = scope-filtered base pool + every favorite of each loaded named set in load order (`ComposeDisplayFavorites`); mutators are container-aware (`FindFavoriteContainer` routes an edit to the base pool or the owning set); set load/unload + all-sets bundle import. The ctor subscribes `UserPreferences.FavoriteSetsChanged` → `Dispatcher.UIThread.Post(RefreshDisplayFavorites)` so Favorites Editor mutations reach the live bar. |
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
`OnRoomMemberChanged` (**not** the only writer of `RoomMembers` — `ApplyRoomState` seeds it from
`RoomStateDto.Members`, because the server's join-time push can land before `ActiveRoomId` is assigned and get
dropped by this handler's room guard), `OnCrcLobbyChanged` (logs first, then posts — `MainViewModel.Rooms.cs:697`),
`OnCrcRoomMembersChanged`, `OnWeatherChanged`, `OnArrivalGeneratorsChanged`, `OnPositionDisplayChanged`,
`OnAircraftAssignmentsChanged`, `OnScenarioLoaded`/`OnScenarioUnloaded`, `OnSessionSettingsChanged`,
`OnKickedFromRoom`, `OnRoomRetired`. Omit the `Post` and you get intermittent cross-thread crashes that unit tests
will not catch.

**The deliberate exception:** `OnPilotTransmissionReceived` (`MainViewModel.Aircraft.cs:126`) does **not** marshal. It
only reads preference/scalar state and calls `_pilotVoice.Enqueue(...)`; it never touches an `ObservableCollection`
or observable property, so it is safe to run on the SignalR thread. The contract is "marshal before touching UI
state," not "marshal unconditionally" — but when in doubt, marshal.

These other thread crossings exist outside the plain SignalR handlers:

- **Global PTT key hook.** `GlobalKeyHookService` fires `KeyDown`/`KeyUp` on a background thread (so PTT works while
  another app is focused). `MainWindow.OnGlobalKeyDown`/`OnGlobalKeyUp` (`MainWindow.axaml.cs:2601`/`2624`) post to the
  UI thread before calling `vm.SpeechService.StartPtt()`/`StopPtt()`, edge-triggered via `_globalPttActive`.
  The hook is only installed when `App.GlobalKeyHookEnabled` is set, which `Program.Main` does; headless UI test
  hosts leave it off so they never install a native OS-wide hook (libuiohook's X11 teardown kills a display-less
  Linux test host with exit code 1).
- **Speech service callbacks.** `_speechService.StatusChanged` and `CommandReady` fire off-thread;
  `HandleSpeechServiceStatusChange` and `HandleSpeechServiceCommandReady` (`MainViewModel.cs:1535`/`1540`) both post.
- **Speech context provider (a *pull*, not a callback).** `SpeechRecognitionService.ProcessPipelineAsync` pulls the
  context provider — `BuildSpeechContext` — on its `Task.Run` background thread at PTT release. `BuildSpeechContext`
  reads UI-thread-only state (`Aircraft`, `SelectedAircraft`, `Ground.DomainLayout`), so it self-marshals with a
  `Dispatcher.UIThread.CheckAccess()` guard and `Dispatcher.UIThread.Invoke(...)` — `Invoke` (blocking, returns the
  value) rather than `Post` because the caller needs the result. No deadlock: the pipeline is fire-and-forget, so the
  UI thread is never blocked waiting on it.
- **Scenario-load progress.** `_connection.ScenarioLoadProgress` → `OnScenarioLoadProgress` and `_connection.RoomLoadingChanged` → `OnRoomLoadingChanged` (`MainViewModel.Scenario.cs`) both post. They are subscribed once in the constructor, not around the invoke, because a progress event can arrive before or after `LoadScenario` returns. The overlay and the gate they feed are described in **Scenario load overlay and the room-loading gate** below.
- **Pre-send scenario parsing.** The parses a load runs before it sends (`ScenarioIdentity.ResolveFromJson`, `ScenarioSetupPlan.Create`, `ScenarioDifficultyHelper.FilterByDifficulty`) run under `Task.Run`, so a large scenario does not freeze the window while the overlay is up.

  The preference values they need are read on the UI thread before the `Task.Run`, and the view-model state they feed (the setup dialog's fields, the difficulty warnings, the per-scenario go-around preference) is applied after the await, back on the UI thread.
- **Export-recording progress.** `_connection.ExportRecordingProgress` → `OnExportRecordingProgress`
  (`MainViewModel.Timeline.cs:280`) posts. The download-update progress callback in `UpdateNowAsync`
  (`MainViewModel.cs:1286`) posts too.

**The one off-UI-thread shared field:** the command-marker buffer. `RecordCommandMarker`
(`MainViewModel.Timeline.cs:314`) appends to `_commandMarkerHistory` under `_commandMarkerLock`
(`MainViewModel.Timeline.cs:300`, a `System.Threading.Lock`) because the same buffer is read under that lock by the
periodic `RefreshTimelineMarkersAsync` poll. The lock guards only the list; the visible `TimelineMarkers` collection
is still mutated via `Dispatcher.UIThread.Post`.

**Per-aircraft broadcasts must never trigger a global rebuild per event.** The server sends one `AircraftUpdated` per aircraft, so a
handler that runs an O(n) rebuild (the shown nav-route / taxi-route overlays: `Radar.RefreshShownPaths()` +
`Ground.RefreshShownTaxiRoutes()`) inside `OnAircraftUpdated` is O(n²) per tick — a UI-thread allocation storm that pins the dispatcher
for tens of seconds under dense ground traffic with overlays shown (#280). `OnAircraftUpdated` therefore calls `ScheduleShownRouteRefresh`
(`MainViewModel.Aircraft.cs`), which sets a `_shownRouteRefreshScheduled` flag and posts **one** refresh at
`DispatcherPriority.Background`; the Background job runs after the burst of Default-priority update jobs drains, collapsing N updates into one
rebuild. Apply the same coalesce pattern to any new per-aircraft handler that fans out into global work — `RefreshShownPaths` has a fingerprint
cache, `RefreshShownTaxiRoutes` reconstructs every route on each call, so the ground path is the amplifier.

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
   `IsRadarViewPoppedOut`/`IsTerminalDocked`) and the **student** strips/TDLS entries restore from preferences, and
   `ReconcileExtraViews` recreates the extra Radar/Ground instances from `ExtraRadarViewOrdinals` /
   `ExtraGroundViewOrdinals`.
7. **~25 `ServerConnection` event subscriptions** (`MainViewModel.cs:1180`-`1204`).
8. **Fire-and-forget init** — `InitializeNavDataAsync()`, `_vnasConfigService.InitializeAsync()`,
   `CheckForUpdateAsync()`.

`InitializeNavDataAsync` (`MainViewModel.cs:1215`) loads NavData + CIFP and calls `NavigationDatabase.Initialize(...)`,
then flips `_commandInput.NavDbReady = true` and pushes elevation lookups into `Radar` / `Ground`. Until it completes,
`NavigationDatabase.Instance` is null and `BuildSpeechContext` degrades (see Footguns).

## Scenario activation — three paths, one router

A scenario becomes active through **three** distinct entry points, and they all **must** funnel through
`ApplyScenarioBootstrap` (`MainViewModel.Scenario.cs:782`):

| Path | Trigger | Entry method | Carries |
|---|---|---|---|
| **Loader** | This client invoked `LoadScenario` | `ApplyScenarioResult(LoadScenarioResultDto)` (`Scenario.cs:688`) | full `AllAircraft`, sim state, session settings, active runways and the active-runways prompt; also pushes **this RPO's** preferences to the server |
| **Broadcast** | Another client loaded a scenario | `OnScenarioLoaded(ScenarioLoadedDto)` (`Scenario.cs:731`) | same fields and active runways; does **not** push preferences (only the loading RPO does) and never prompts |
| **Join / reconnect** | `JoinRoom` returned a room with a scenario | `ApplyRoomState(RoomStateDto)` (`Rooms.cs:823`) | snapshot incl. `ElapsedSeconds`/`IsPlayback`/`TapeEnd` and active runways |

`ScenarioBootstrap` (`ScenarioBootstrap.cs`) is a small record that exists precisely so the three differently-named
DTOs project into one shape (`ScenarioId`, `ScenarioName`, `PrimaryAirportId`, `PositionDisplayConfig`,
`FlightStripsConfig`, `Aircraft`, `ElapsedSeconds`). `ElapsedSeconds` is how long the scenario has already been
running when this client picks it up: the loader and broadcast paths pass 0 (both fire as the scenario starts), the
join path passes the room's `RoomStateDto.ElapsedSeconds`. `ApplyScenarioBootstrap` then does the work common to all
three:

- sets `ActiveScenarioId`/`Name`/`PrimaryAirportId` and `_commandInput.PrimaryAirportId`,
- rebuilds the `Aircraft` collection from the DTOs (recomputing `InitialDelayedSpawnCount` /
  `PendingDelayedSpawnCount`),
- fans out to `Radar.ApplyScenarioBootstrap` / `Ground.ApplyScenarioBootstrap`, `VStrips.ApplyBayConfig`, and the
  vTDLS bootstrap (`BootstrapStudentTdlsAsync`),
- ends in `StartRichPresence(bootstrap.ElapsedSeconds)`, which stamps the Discord start time (now minus the elapsed
  seconds, so a joiner's timer matches the room's) and publishes through `RefreshRichPresence` — see
  [discord-integration.md](discord-integration.md#desktop-client-rich-presence).

**Per-path extras stay at the call site:** the `ApplySimState` signature differs (the join path passes elapsed/
playback/tape-end; loader & broadcast use the 2-arg form), `_studentPositionType` and `_isAutoClearedToLand` are set
by the loader/broadcast paths, the `ApplySessionSettingsFrom*` adapter differs, and the loader path additionally fires
the `Send*` preference pushes. The join path needs no `_isAutoClearedToLand` line of its own: `ApplySessionSettingsFromRoom`
ends in `ApplyAutoClearedToLandLocally`, which sets the field from the room's shared value and re-stamps every aircraft.

**Every path stashes the generator editor's sources.** `StashScenarioGeneratorsAndPositions`
(`MainViewModel.ArrivalGenerators.cs`) fills `LatestArrivalGenerators` / `LatestVfrArrivalGenerators` /
`LatestOverflightGenerators` / `LatestPositions`, which Tools → "Edit Aircraft Generators…" opens against with no fetch
of its own. All three DTOs carry the four lists and all three entry methods call it; a path that skipped it would open
the editor empty, and Apply from an empty editor replaces the room's generators with nothing (#442).

**A recording load is a second scenario-identity writer.** `ApplyRecordingResult` (`MainViewModel.Timeline.cs:647`,
reached by the loading client and by the `RecordingLoaded` broadcast) sets `ActiveScenarioId`/`Name`/`PrimaryAirportId`
itself and does not go through the router, so it makes its own `StartRichPresence(result.ElapsedSeconds)` call — the
tape position is where the Discord timer starts. Anything keyed on "the active scenario changed" has to be wired into
both `ApplyScenarioBootstrap` and `ApplyRecordingResult`. A rewind or skip (`RewindToSeconds`) goes through neither,
and leaves the published start time alone.

`RefreshRichPresence` republishes from the current `ActiveScenario*` properties without touching the start time: the
Settings window calls it on each Apply (so the `DiscordRichPresenceEnabled` toggle takes effect at once), and
`OnRoomMemberChanged` calls it when a scenario name arrives after a bootstrap that had none (until then the first line
is the raw scenario id). `MainViewModel.RichPresence` is a settable `IRichPresencePublisher?` that `MainWindow` assigns;
left null (a headless test host, unless the test assigns a fake) every call is a no-op.

`ClearScenarioState` (`Scenario.cs:985`) is the symmetric teardown: it nulls the active-scenario properties, clears the published Discord presence, clears the active runways and closes their prompt, clears `Aircraft`, clears the ground layout / video maps / shown paths, and resets session settings to a neutral `SessionSettingsDto`.

### Active runways and the load prompt

`MainViewModel.ActiveRunways.cs` holds the client's copy of the room's active runways, `RoomActiveRunways` (airport → the ends' tokens as the server spells them, `30` / `D28L` / `A28R`). Whichever payload carrying it arrives last wins, so their order does not matter; `ClearScenarioState` empties it.

It is server-authoritative and replaced wholesale by every payload that carries it: the load result (`ApplyLoadResultActiveRunways`, from `ApplyScenarioResult`), `OnScenarioLoaded`, `ApplyRoomState`, `ApplyRecordingResult` and `ApplyRewindResult` (`RewindResultDto.ActiveRunways`; the rewinder's own result, applied on the UI thread), `OnActiveRunwaysChanged` and `OnScenarioRestarted` (`ScenarioRestartedDto.ActiveRunways`).

The same file holds the prompt the loading mentor answers. It opens only from the loader's own result when `ActiveRunwaysPromptNeeded` is set, the client is a mentor (`!IsNonMentor`) and the load result's `IsLiveSession` is false; a restart, a join and another member's load never open it.

It is an overlay in `MainWindow.axaml` (`ShowActiveRunwaysPrompt`) with one `ActiveRunwaysRow` per airport (the primary first, then each airport the prefill names), pre-filled from `ActiveRunwaysPrefill`.

Under its title a fixed line says what the list drives: `YAAT's aircraft menus use these to name and suggest runways. Aircraft the scenario already gives a runway keep it.`

`ActiveRunwaysPromptNotes` holds, for each row's airport in row order, its runway line (`ActiveRunwaysEditor.AssignedNote`, from the load result's `ActiveRunwaysAssigned`: how many departures and arrivals already have a runway and keep it, or that none does, then the runways the scenario's arrival generators land on) and then its notes on the guess (`ActiveRunwaysEditor.Notes`: no departure or no arrival end implied).

The notes follow the rows, so an airport whose row was accepted loses its lines.

OK checks every row with `ActiveRunwaysEditor.ToCommand` (`ActiveRunwaysEditor.cs`, returning an `ActiveRunwaysAnswer`, `ActiveRunwaysAnswer.cs`; it reads the text with `ActiveRunwayListParser.ParseWithNone`, the reader `ARWY` itself uses, so commas, tabs and new lines count as spaces when looking for `NONE`; an empty row sets every end of every runway the navigation data knows there, `NONE` alone clears) and sends nothing if a row fails. Otherwise it sends one `ARWY {FAA} {tokens}` per airport through `SendCommandAsync`, not added to command history. An accepted row leaves the prompt and a refused one stays with the server's message.

Cancel sends nothing and prints `Active runways not set; use ARWY or Scenario › Active Runways…`. The prompt also closes on `ClearScenarioState` and on another load (`OnScenarioLoaded`, `ApplyRecordingResult`); an `ActiveRunwaysChanged` leaves it open. Once the prompt closes or reopens while an answer is still sending (an unload, leaving the room, another load), the remaining rows are not sent. `ActiveRunwaysRow` (`ActiveRunwaysRow.cs`) and `ActiveRunwaysEditor` are public so an editor window can reuse the text round trip.

**The window** is `ActiveRunwaysWindowViewModel` (`ActiveRunwaysWindowViewModel.cs`) with `Views/ActiveRunwaysWindow.axaml(.cs)`, opened from **Scenario › Active Runways…** (single instance, enabled by `HasScenario`, geometry key `ActiveRunways`). Its rows are the same `ActiveRunwaysRow`s: one per airport of `RoomActiveRunways`, with the primary airport (`ActiveScenarioPrimaryAirportId`) first and the rest in ordinal order, each holding `ActiveRunwaysEditor.ToText` of the room's tokens. The window is its own view model rather than a second prompt — it is a top-level `Window`, not the overlay `Border`.

Rows follow the room: every `RoomActiveRunways` (or primary-airport) change refreshes the rows the user has not typed in, while a row that has been typed in keeps its text; an airport the list newly names gains a row and one it no longer names loses its own. An airport left out of the list entirely would read as an empty row, which means every runway, so the drop matters.

`ApplyAsync(navDb, send)` reads every row with `ActiveRunwaysEditor.ReadRows` — the parse-and-refuse helper `SubmitActiveRunwaysPromptAsync` calls too — and sends one `ARWY` per row whose text differs from the room's current text for that airport. Unlike the prompt it keeps every row and the window open: an accepted row counts as untouched again (so it follows the room once more), a refused one keeps its text with the server's message under it, and the other rows still go. Empty stays every end and `NONE` clears, as `ToCommand` already reads them.

The view model closes (the `IsOpen` flag, which the window watches) on any `ActiveScenarioId` change: an unload and leaving the room null it, another scenario load and a recording load set a new one, and a restart re-activates the same id, so the window ends exactly where the prompt does.

## Scenario load overlay and the room-loading gate

**The overlay.** `MainViewModel.LoadOverlay` (`ViewModels/LoadOverlayViewModel.cs`) is the step table of the load *this* client started, drawn by the "Scenario load overlay" `Border` in `MainWindow.axaml` over the main panel: the title, one row per step (`LoadStepViewModel`: a glyph — `✓` done, `⚠` warning, `✗` failed, `•` running, `○` pending, `–` not needed — its label, detail and problem lines; a not-needed step is grey), and a **Close** button shown only when the overlay stays open.

The server's side of the table (step ids, states, texts) is in [training-hub-contract.md](training-hub-contract.md#scenario-load-progress). Every method runs on the UI thread:

- `BeginLocal(title)` opens it at the click, before any parsing, with one local `Read scenario` row running and the previous load's id and table forgotten. The title is the catalog scenario's name or the local file's name (`ScenarioLoadTitle`), or `Live session`.
- `ApplyProgress(dto)` takes a `ScenarioLoadProgress` event: the first event's `LoadId` is adopted and events of another load are ignored, an event whose `Sequence` is not above the last applied one is dropped, a non-empty `ScenarioName` becomes the title, and the event's table replaces the rows.
- `ApplyResult(result)` takes the RPC result: its `Steps` replace the rows unless a complete event already showed them, then the table is complete. The result alone is enough to render the finished overlay when every event was lost.
- `ApplyRefusal()` closes the overlay of a load that never started: a refusal with no steps (not in a room, another member's load holds the room), an exception from the send, or the detour to the Scenario Setup dialog, which closes it and reopens it (`SendConfirmedScenarioAsync`) when the user confirms.
- Once complete, nothing changes the table. The overlay closes itself when no server row is `warning` or `failed`; otherwise `CanClose` shows the Close button and it stays open until clicked.

`ReportLoadFailure` handles `Success = false`: with steps the overlay stays open on the failed step, without it closes; either way the first warning, when there is one, goes to the status bar and the terminal (else the status bar reads `Scenario load failed`). Starting a live session (`StartLiveSessionAsync`) drives the same overlay.

**The room-loading gate.** The server refuses load, unload, restart, rewind and recording load while any member's load holds the room; the client disables all but the recording load up front (a recording load sent mid-load gets the server's refusal). `RoomLoadingBy` holds the loader's initials: seeded from `RoomStateDto.LoadingBy` in `ApplyRoomState`, set and cleared by `RoomLoadingChanged`, cleared by `OnScenarioLoaded` and `ClearRoomState`.

`IsRoomLoading` is `RoomLoadingBy` set **or** this client's own overlay in flight (`LoadOverlay.IsLoadInFlight`, open and not complete), and it gates `CanLoadScenario` (so the Load menu items and `CanStartLiveSession` too), `CanUnloadScenario`, `CanRestartScenario` and `CanRewind`, the can-execute of the six timeline jump commands; `NotifyRoomLoadingChanged` re-evaluates them all whenever either input changes.

`RewindToSeconds`, which every timeline jump reaches (buttons, scrubber, markers, bookmarks), also refuses on its own with the status `Rewind unavailable while a scenario loads`.

While `RoomLoadingBy` is set the status bar reads `Loading a scenario (by {initials})…`. It never names the scenario, since mid-load the room state still carries the previous scenario's name; the room's terminal line `{initials} is loading '{name}'…` comes from the server. When the load ends and the status bar still shows that line, it becomes `Load by {initials} ended`. Join, reconnect and restored-room status lines go through `SetStatusUnlessRoomLoading`, so they do not hide a running load.

**Closing a room this client could not join.** `CreateRoomAsync` joins its new room through `JoinCreatedRoomAsync`. When `JoinRoom` throws, it calls `ServerConnection.CloseRoomAsync(roomId)` first, so the room does not sit on the server with nobody in it, then rethrows the join's exception to the create's own error handling.

A failed close is logged and reported in the terminal (`[WARN] Could not close room {roomId} after joining it failed: {message}`). A `JoinRoom` that returns null found the room already gone, so there is nothing to close.

Tests: `LoadOverlayViewModelTests` and `MainViewModelLoadOverlayTests` (`tests/Yaat.Client.UI.Tests/ViewModels/`); the DTO shapes in `HubJsonContractTests`.

## Session-settings echo suppression

`ApplySessionSettings` writes 24 `Session*` `[ObservableProperty]` fields from the 23 fields of `SessionSettingsDto`, and the session-settings flyout binds them.

They are `SessionAutoDeleteIndex`, `SessionDepartureAutoDeleteDistanceNm` (a nullable `decimal` for its `NumericUpDown`, blank = off), `SessionAutoAcceptEnabled` + `SessionAutoAcceptDelaySeconds`, the two command-run-delay bounds, `SessionAutoClearedToLand`, `SessionAutoCrossRunway`, `SessionAutoPullUpToParallel`, `SessionAutoGoAroundOnOccupiedRunway`, `SessionAutoRejectTakeoffOnOccupiedRunway` and `SessionAutoArrivalSpacingOnOccupiedRunway`.

They also include `SessionLiveTrafficEnabled` + `SessionLiveTrafficCeilingFt` + `SessionLiveTrafficFilter` (see [live-traffic.md](live-traffic.md) "Client" for the status-bar indicator and the Aircraft List tri-state that hang off them), `SessionValidateDctFixes` and `SessionSoloTrainingMode`.

Last come the four solo-pacing fields (the parking call-up rate percent and the interval seconds the slider shows, the arrival generator rate, the go-around probability), the two `SessionHasSolo*Source` flags, and `SessionRpoShowPilotSpeech`.

Each has an `OnXxxChanged` partial, and the ones that re-send the new value to the server are what the guard protects. The problem: when the **server** broadcasts a settings change, applying it to the bound property would re-trigger `OnXxxChanged`, which would re-send it — a ping-pong.

Properties the flyout binds that are never sent sit beside them, set or recomputed on the client: `SessionAutoClearedToLandLabel` and `SessionAutoArrivalSpacingLabel` ("Auto cleared-to-land (TWR)": the room holds one flag each, while the Settings defaults are per position type).

So the suffix names the student's position type when it is GND, TWR, APP or CTR and is left off otherwise, and `SetStudentPositionType` raises both. The others are `SessionAutoArrivalSpacingApplies` (false for APP and CTR, which greys the arrival-spacing checkbox), `SessionSoloParkingInitialCallupIntervalLabel` and `SessionLiveTrafficFilterSummary`.

Auto-accept is two flyout controls over one wire value: the hub carries a single delay where any negative value means off.

`SessionAutoAcceptWire` (`Yaat.Client.Core/Services/SessionAutoAcceptWire.cs`) maps between them: `ToWire(enabled, delay)` sends the delay or `-1`, and `FromWire(wireDelay, previousDelay)` turns a negative delay into Enabled false with the previous delay kept, so turning the checkbox back on restores it. Both `OnSessionAutoAcceptEnabledChanged` and `OnSessionAutoAcceptDelaySecondsChanged` send `ToWire` of the pair.

The guard is `_isApplyingSessionSettings` (`MainViewModel.cs:3484`). `ApplySessionSettings(SessionSettingsDto)` (`MainViewModel.cs:3490`) sets it `true`, writes all 24 properties, then sets it `false`. Every sending `OnXxxChanged` handler early-returns while the flag is set (e.g. `OnSessionAutoCrossRunwayChanged`, `OnSessionSoloGoAroundProbabilityPercentChanged`), so the broadcast lands without echoing back.

Because the same 23 DTO fields arrive under four different DTO shapes, there are **four adapters** that all build a `SessionSettingsDto` and call `ApplySessionSettings`:

- `ApplySessionSettings(SessionSettingsDto)` — the base, used by the live `OnSessionSettingsChanged` broadcast.
- `ApplySessionSettingsFromRoom(RoomStateDto)` (`MainViewModel.cs:3527`).
- `ApplySessionSettingsFromScenarioLoaded(ScenarioLoadedDto)` (`MainViewModel.cs:3559`).
- `ApplySessionSettingsFromLoadScenarioResult(LoadScenarioResultDto)` (`MainViewModel.cs:3590`).

Add a session setting and **all four** adapters plus the `SessionSettingsDto` (client + server) and the four
source DTOs must change in lockstep — see [training-hub-contract.md](training-hub-contract.md) for the cross-repo
fan-out.

Note the solo-pacing rates funnel through one server call, `SetSoloPacingRatesAsync(parking, arrival, goAround)`, not three separate setters — `OnSessionSoloPacingRateChanged` / `OnSessionSoloParkingInitialCallupIntervalSecondsChanged` / `OnSessionSoloGoAroundProbabilityPercentChanged` all clamp then call it.

The parking pace travels as a rate percent but every control shows an interval; the conversion, the "Paused" / "Once per N sec" label and the load-time defaults (`LoadDefaults`, what a load without the setup dialog sends) live in `Yaat.Client.Core/Services/SoloPacing.cs`, shared by the flyout, the scenario setup dialog and Settings › Scenario defaults. The setup dialog seeds its sliders from the stored defaults and never writes them back; only Settings changes them.

The **terminal-filter solo** feature uses the identical guard pattern under a different flag,
`_isProgrammaticTerminalToggle` (`MainViewModel.cs:821`): `ApplyVisibilityProgrammatic` sets it while flipping the
`Show*Entries` toggles so `OnTerminalToggleChanged` skips persistence and the cancel-solo side effect. There is one
`Show<Kind>Entries` toggle per `TerminalEntryKind` (Command/Response/System/Say/Warning/Error/Chat/Tdls/**Strip**); adding
a channel means touching this toggle set plus `IsEntryVisible`, `CurrentVisibleKinds`, `ApplyVisibilityProgrammatic`,
`PersistTerminalFilters`, the constructor seed, the `TerminalPanelView` toggle button + `EnumerateCategoryToggles`, and the
`TerminalColorScheme`/colorizer/Settings color row (the `Strip` channel — strip command echoes + feedback, tagged server-side
in `RoomEngine.HandleStripCmd` — is the most recent example).

**Terminal scrub + timestamp mode.** Each `TerminalEntry` carries `ElapsedSeconds` (the scenario-elapsed second it
occurred, stamped server-side into `TerminalBroadcastDto` or from `ScenarioElapsedSeconds` for client-local entries).
`TerminalPanelView`'s right-click "Rewind to this moment" hit-tests the clicked line back to its entry (via the parallel
`_lineEntries` list) and calls `RewindToSeconds(entry.ElapsedSeconds)` — the same seek every rewind affordance uses. On
recording load, `LoadRecording` clears and repopulates `TerminalEntries` from `GetTerminalLogAsync()` so the reconstructed
terminal is scrubbable. The header's timestamp-mode button cycles `TerminalTimestampMode` (WallClock / SimElapsed / Both,
persisted in `UserPreferences`); a change raises `TerminalFilterChanged`, which rebuilds the document via `FormatEntry`.

## The command pipeline entry point — `SendCommandAsync`

`SendCommandAsync` (`MainViewModel.cs:1893`) is the client-side resolution chain that runs **before** anything reaches
the server. It does the work that `command-pipeline.md` summarizes in one line ("partial callsign resolution"). In
order:

1. **`** ` override prefix** — strips a leading `** ` and sets `forceOverride`, which re-prepends `** ` onto the
   canonical string before sending (bypasses assignment-ownership checks server-side).
2. **Chat prefix** — a leading `'`, `/`, or `>` routes the remainder to `SendChatAsync` and returns.
3. **Dot commands** — a leading `.` (or the `CRC ` force-alias prefix, which is stripped alongside `** `, or the
   `*T` measuring verb) is handled entirely client-side and returns. `TryHandleScopeMarkerCommand` gets first
   refusal (`.ff` / `.marker` / `.markers` / `.nomarkers`), then `TryHandleMeasureCommand` (`.rbl` / `.norbl` /
   `*T`; `.rbl A B` resolves each token via `MeasureEndpointResolver` and places a radar-view line), then
   `TryHandleCrcAlias` (`MainViewModel.CrcAliases.cs`); none claiming it yields "Unknown command or alias". The
   `CRC ` prefix skips the built-in steps so a shadowed alias is still reachable.
4. **Global command** — `CommandSchemeParser.Parse` + `IsGlobalCommand`; dispatched via `HandleGlobalCommand` with no
   callsign. Exception: the `AS {tcp} {track_command}` *prefix* form is per-aircraft (the standalone `AS {tcp}` is
   global), so it is **not** taken here.
5. **Single-token select** — if the input is one token with no `,`/`;` and matches a callsign, just select that
   aircraft and return (no command sent).
6. **Macro expand** — `TypedCommandText.TryExpandMacros` (over `MacroExpander`, shared with custom quick commands) so callsign-prefix resolution sees real verbs.
7. **Callsign-prefix resolve** — `CallsignPrefixResolver.Resolve`; `Ambiguous` surfaces a status message and aborts;
   `Resolved` sets `target` + strips the prefix from `commandText`. A leading known command verb is never taken as a
   partial callsign (only an exact callsign match overrides), so a bare command whose verb merely appears inside live
   callsigns (`CM` while `CMD2` is up) falls through to the selected aircraft instead of reporting a false ambiguity.
8. **Argument rewrite** — `CallsignArgumentResolver.TryRewrite` canonicalizes partial callsigns inside arguments
   (`FOLLOW UA` → `FOLLOW UAL123`).
9. **RPO control commands** — `TryHandleRpoCommand` (`MainViewModel.cs:2097`) intercepts `TAKE` / `GIVE <initials>` /
   `GIVEUP` (client-local ownership ops, bypass the command pipeline entirely).
10. **`ParseCompound`** — on failure, falls back to **solo natural-language** dispatch
    (`TryDispatchSoloNaturalCommandAsync`) when `SessionSoloTrainingMode` is on; otherwise reports the parse error.
11. **No-target half-strip** — when no aircraft resolved, `HSC`/`HSA`/`HSD` run globally with an empty callsign
    (`IsHalfStripVerb`); otherwise "No aircraft matched."
12. **VFR gate** — `VfrCommandGate.Evaluate(target, canonical, VfrCommandsForIfr)` (`Services/VfrCommandGate.cs`).
    The simulation does not gate commands on flight rules, so this is what refuses a VFR-only command for an IFR
    aircraft when the controller has not opted in; on rejection it sets `StatusText`, adds a terminal Warning, and
    returns without touching the wire. When the setting *does* let one through, `NoteVfrBypassIfNeeded` emits a
    one-per-aircraft advisory so the acceptance is visible. See [command-handlers.md](command-handlers.md).
13. **Dispatch** — `_connection.SendCommandAsync(callsign, canonical, initials)`. On success,
    `RecordCommandMarker` drops a timeline tick; `AddHistory` records the canonical (callsign stripped via
    `CommandHistoryFormatter`); the input box is cleared; `CommandStatusResolver.Resolve` sets the status text.

The server side picks up from `SendCommand` — see [command-pipeline.md](command-pipeline.md) and
[command-handlers.md](command-handlers.md). `TryDispatchSoloNaturalCommandAsync` (step 10) runs the same gate and
dispatch tail for its mapped canonical, and favorites/macros re-enter through `SendCommandAsync` itself.

**Not every client command flows through this chain.** Sub-VMs (Strips/TDLS) dispatch through the simpler
`SendCommandForViewAsync(callsign, command, initials)`, which skips the resolution chain because the caller already
knows the callsign — and so do the **right-click context menus**: their catalog entries send through `IMenuHost.SendAsync` (`Views/ClientMenuHost.cs`), which calls `SendCommandForViewAsync` on every view.
Anything that must apply to *every* command a controller issues therefore needs a second enforcement point: for the VFR
gate that is `AircraftCommandApplicability`, which the menus consult when deciding which items to build.

The **Favorite Commands** submenu is the exception among menus — a favorite carries arbitrary canonical text, so there
is no item-construction gate to rely on. It routes through `SendGatedCommandForViewAsync(target, callsign, command,
initials)`, which runs `VfrCommandGate` and then `SendCommandForViewAsync`. Any future menu that sends
controller-authored text rather than a fixed verb belongs on that path too.

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

**The favorites bar is not a tab.** `ShowFavoritesBar` (pref-backed, default on) and the derived `IsFavoritesBarDocked => ShowFavoritesBar && !IsTerminalPoppedOut` drive the bar in `MainWindow.axaml`; the copy inside `TerminalWindow.axaml` binds `ShowFavoritesBar` alone, since the bar follows the Terminal when it pops out.

The pop-out Favorites Panel is a `FavoritesPanelWindow` singleton per `MainViewModel` (`ShowOrActivate` / `IsOpen` / `Close`); its open state persists as `UserPreferences.IsFavoritesPanelOpen` (restored at startup beside the other pop-outs, not written during shutdown) and both flags ride layouts (`SavedLayout`) as nullable fields — null means "captured before the feature; leave as is", the same convention as `LoadedFavoriteSetIds` and `OpenTabs`.

### Extra view instances (`New Radar Window` / `New Ground Window`)

The docked Radar/Ground views and their pop-outs are the implicit instance #1 and keep today's `IsRadarViewPoppedOut` / `IsGroundViewPoppedOut` semantics.

**View → New Radar Window / New Ground Window** adds an instance ≥ #2 with its **own** `RadarViewModel` / `GroundViewModel` (center, range, zoom, rotation, filters, DCB state, `DataBlockState`, shown routes), based on an airport the user picks first (`ExtraViewAirportDialog`: the ARTCC's airports from `ArtccAirportResolver` with the scenario primary preselected, or any nav-db airport by text — a second view has no scenario-inferred target).

The instance is hosted by a `RadarViewWindow` / `GroundViewWindow` whose *window* DataContext stays `MainViewModel` (the inner view binds `Aircraft` / `GroundShownAirportId` through `$parent[Window]`) while the inner `RadarView` / `GroundView` gets the instance VM via `SetViewModel`.

Geometry key `RadarView#n` / `GroundView#n` rides the ordinary `WindowGeometries` store, so layouts capture it through the live-helper walk; the ordinal lists (`ExtraRadarViewOrdinals`, on prefs and on `SavedLayout`) say which instances exist, and `ReconcileExtraViews` opens/closes to match without resetting survivors. Closing the window removes the instance (not under shutdown, so it restores next launch).

Invariants:

- **One selected aircraft app-wide.** `MainViewModel.SelectedAircraft` fans out to every instance under
  `_isSyncingSelection`; every instance is constructed with the same `OnChildSelectionChanged` callback, so a click in any
  window selects everywhere, datagrid included. An instance's `SelectedAircraft` is a mirror, never its own selection.
- **Per-instance persistence.** `SettingsKeySuffix` (`"#2"`) keys the per-scenario `RadarSettings` / `GroundSettings`
  slot, and `IsPrimary=false` gates every app-wide preference writer (DCB, deconflict mode, ground layers/labels/lock,
  ground rotation) so an extra window's toggles never rewrite the primary's defaults.
- **The base airport decides the instance's data.** A radar extra gets `SetPrimaryAirportId(airport)`, the airport's
  position from the nav db, and `LoadVideoMapsForArtccAsync(artcc, airport, scenarioId)` (`SeedRadarAirport`) — the
  primary's airport is pushed to the primary only, and every scenario bootstrap / recording load re-seeds each extra
  with its own airport. A ground extra whose airport equals the primary's **mirrors** (`GroundViewModel.MirrorLayoutFrom`
  copies `Layout` / `BackgroundImage` / `TowerCabMap` / centre / elevation by reference and follows the primary's
  `PropertyChanged`; the loaders are no-ops while `IsMirroring`); a different airport loads its own layout and tower-cab
  image (`SeedGroundAirport`, re-evaluated on every bootstrap). Never `Dispose()` a mirrored image. The
  `SavedExtraView(Ordinal, AirportId)` records on prefs and layouts carry the airport; `ReconcileExtraViews` matches on
  both and reopens an instance whose airport changed.
- **Fan-out sites loop `AllRadarViews` / `AllGroundViews`**: nav-db push, selection, weather, MVA hints, primary airport
  id/position, position display config, scenario bootstrap (ground extras get only `SetScenarioId`), recording load,
  `ClearScenarioState`, the coalesced shown-route refresh (one Background post for all instances), manifest replacement,
  aircraft deletion. **Primary-only by design**: the `.ff` / `.markers` / `.nomarkers` scope markers, `.rbl`
  (the `Measure` store is shared, so the line still renders everywhere), every `Ground.DomainLayout` reader (speech
  context, taxiway/spot/parking name providers), the Apply Layout dialog's scenario-views source.
- Per-tick cost: nothing per-aircraft touches an instance; N instances add N coalesced refreshes per update burst and N
  canvases on the 10 Hz timer. `RefreshShownTaxiRoutes` short-circuits when nothing is shown.

**Pop-out persistence is asymmetric.** The three fixed views and the **student** Strips/TDLS entry (index 0) persist
their popped-out flag to `UserPreferences` (`OnStripsEntryPropertyChanged` only calls `SetPoppedOut("VStrips", …)`
when `entry.IsStudentEntry`, `MainViewModel.Strips.cs:107`). Extra per-facility tabs are session-scoped and always
start docked. The `Subscribe*Entry` / `Unsubscribe*Entry` pairing (driven by the `CollectionChanged` handler) must
stay balanced or pop-out bookkeeping leaks handlers.

## Aircraft List "Info" column

The Info column text and color come from `Yaat.Sim.AircraftStatusDescriber.Describe(...)`, a pure projection of `AircraftState` (via
`AircraftStatusView.FromState` plus an `AircraftStatusContext` carrying the three non-state inputs: delayed, auto-cleared-to-land, handoff peer
label) to `(SmartStatus, SmartStatusSeverity)`. It runs **once, server-side** in `DtoConverter`, ships on the aircraft DTO, and
`AircraftModel.FromDto` / the DataGrid display the string **verbatim** — there is no client-side status logic, so an Info-column bug is always
in the describer or its projection, never in rendering. Inside the describer, `CheckAlerts` (first-match wins: no landing clearance, landing
without clearance, handoff pending, no altitude assigned) runs after `ComputeNormalStatus`, and an alert **prepends** rather than replaces:
`"{alert} · {normal}"`, keeping the alert's severity. `SmartStatusTests` (Yaat.Client.Tests) is the pinning suite and hand-builds
`AircraftStatusView` without an engine. The flashing `NoLndgClnc` datablock label is a separate path driven by
`AircraftState.NoLandingClearanceWarningActive` (set by `FinalApproachPhase`), not by this text.

## Window geometry & group raise

Every window restores through `WindowGeometryHelper(window, preferences, "Name", defaultW, defaultH).Restore()` (Yaat.Client.Core), which is also
the single choke point that attaches `WindowGroupRaiser`. Invariants the helper depends on:

- **Restore verbatim; clamping is rescue-only.** An edge-snapped window's frame origin legitimately sits a few pixels *outside* the screen
  (Windows' invisible resize borders), so force-fitting it inside the work area shifts it inward and overlaps tiled neighbors. The pure static
  `ResolveGeometry(geo, screens)` restores the saved rectangle unchanged whenever at least a 50×30 px overlap with any screen's working area
  exists; clamping into the target work area is only the fallback for an unplugged monitor or poisoned data (#361). `WindowGeometryResolveTests`
  (plain `[Fact]`s) pins the resolver directly, without a window.
- **A position is only ever persisted from a window that can still report one.** Avalonia's `Window.Position` getter answers
  `PlatformImpl?.Position ?? PixelPoint.Origin`, and `TopLevel.HandleClosed()` nulls `PlatformImpl` — so a capture taken after the platform
  surface is gone returns `(0,0)` while `Width`/`Height` (styled properties) keep their real values. That shape is indistinguishable from a
  window genuinely parked at the screen's top-left corner, and it silently overwrote good geometry in preferences (#408). `_isClosed` (set from
  `Window.Closed`) short-circuits `SaveCurrentGeometry`, and `CaptureCurrentGeometry` falls back to `_lastNormalGeometry` when there is no
  surface. Discriminate on **window liveness, never on the `(0,0)` value** — a user may legitimately place a window at `(0,0)`.
- **`Closing` is not the universal close signal.** Avalonia closes owned windows through `Window.CloseInternal()`, which disposes the child's
  surface directly; under `WindowClosingBehavior.OwnerWindowOnly` a child never raises `Closing`, so `OnClosing` alone would leave the helper in
  the static `ActiveHelpers` registry with a live auto-save timer aimed at a dead window. `Window.Closed` is where unregistration belongs.
- **The restored geometry is authoritative until the post-open verify has run.** The debounced auto-save is armed *before* the window is shown
  (applying `Topmost` during the restore schedules it) and a `DispatcherTimer` runs at Normal priority, while `VerifyStartupGeometry` is posted
  at Background — so on a slow cold start the save wins the race and persists a startup drift before anything corrects it. While
  `_startupVerifyPending`, both change handlers leave `_lastNormalGeometry` alone and persistence reads it rather than the live frame.
- **An applied geometry is remembered as given, not read back.** `ApplyGeometryToWindow` seeds `_lastNormalGeometry` from the `ResolvedGeometry`
  it just wrote. Reading it back instead loses the value whenever the window is maximized or minimized across the write, because both change
  handlers skip non-Normal states — which is why applying a profile to an already-maximized window used to keep persisting the geometry it was
  supposed to replace.
- **Units are mixed by design.** `SavedWindowGeometry.X/Y` and `Screen.WorkingArea` are device pixels; `Width/Height` (from `Window.Width`) are
  DIPs. Any math combining them must convert through `Screen.Scaling` (`SafeScaling` guards a zero).
- **Minimized-position sentinels differ by platform.** Headless and macOS report `(0,0)`, Win32 reports `(-32000,-32000)`; `IsIconicOrigin`
  matches both, and `OnPositionChanged` must never capture an iconic position as `_lastNormalGeometry` — the event can fire before
  `WindowState` flips to `Minimized`, which is how a profile can end up placing a window at -32000.
- **`WindowGroupRaiser` raises every YAAT window when focus returns from another app** (pref `RaiseWindowsTogether`, default on, read at raise
  time; #392). It deliberately does **not** use Win32 window ownership (CRC's mechanism): owned windows sit permanently above their owner, which
  breaks free stacking among YAAT windows. A raise is a `Topmost = true; Topmost = false` pulse per window (`SetWindowPos` with `HWND_TOPMOST` /
  `HWND_NOTOPMOST` + `SWP_NOACTIVATE` on Win32 — raises without stealing focus), ordered by `Window.SortWindowsByZOrder`, which **throws when any
  window's `PlatformImpl` is null** — hence the try/catch with an MRU-order fallback. `WindowGeometryHelper.OnWindowPropertyChanged` ignores
  `TopmostProperty` while `WindowGroupRaiser.IsRaising`, otherwise the pulse flickers the pinned-title marker and triggers a spurious auto-save.
- **`WindowGroupRaiser.IsSuspended` must wrap the *entire* body** of `ApplyLayoutAsync` (whole-layout and partial applies both go through it): the pop-out flips, `Show()`s and the final `ReclaimFocusAfterLayoutApply` activation would each trigger a competing raise mid-apply.
- **Avalonia raises `Activated` before `IsActive` becomes `true`** — never read the activating window's `IsActive` inside the handler.
  Group-inactive detection is a posted (Background-priority) check that no tracked window is active.
- **Headless caveats.** `Topmost` does not move headless z-order (assert via the internal `GroupRaised` event, not visually), `Deactivated`
  fires only on `Hide()`, and `Activated` is posted at Input priority, so tests need `Dispatcher.UIThread.RunJobs()` after `Show()`.

## View ↔ VM event bridge

The VM cannot reach a control (MVVM), so two parameterless events let it poke the View:

- **`GridLayoutReset`** (`MainViewModel.cs:761`) — raised by the `ResetGridLayout` command; `MainWindow` subscribes and
  forwards to `ResetLiveGrid(dataGrid)` (`MainWindow.axaml.cs:217`).
- **`RequestCommandInputFocus`** (`MainViewModel.cs:770`) — raised after a speech transcription populates `CommandText`
  (when `AutoFocusInputAfterSpeech` is set); `MainWindow` forwards to `CommandInputView.FocusCommandInput()`
  (`MainWindow.axaml.cs:226`). A new "VM needs to poke a control" requirement should follow this pattern, not a direct
  control reference.
- **`RequestCommandInputFocus` has a second trigger the bullet above omits.** The app-wide focus-input hotkey
  (default `` ` ``/`OemTilde`, `UserPreferences.FocusInputKey`) also raises it, via a central class handler in
  `src/Yaat.Client/Views/WindowHotkeys.cs` (`InputElement.KeyDownEvent.AddClassHandler<Window>`, registered once at
  `App.OnFrameworkInitializationCompleted`, outside the desktop-lifetime block so headless tests run it too;
  `WindowHotkeys.cs:70` → `vm.FocusCommandInput()`). Scope: `DataContext is MainViewModel` or
  `VStripsViewWindow`/`VTdlsViewWindow`; modal dialogs are excluded. The actual forwarding handler,
  `MainWindow.FocusActiveCommandInput` (`MainWindow.axaml.cs:3056`), branches on `IsTerminalDocked` — docked routes to the
  embedded `CommandInputView`, popped-out routes to `_terminalWindow.FocusCommandInput()`. `WindowHotkeys.cs` also owns a
  sibling always-on-top hotkey (default `Ctrl+Shift+T`) that toggles the *focused* window's topmost via `IAlwaysOnTopToggle`
  (`Yaat.Client.Core/Views`), implemented per-window as `ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost()`.

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

When `_isMainWindowClosing` is set, `OnClosing` also disposes the global key hook and cancels the auto-connect CTS. Two rules keep that path from
wedging the UI thread (#347):

- **No untimed native waits on the UI thread during shutdown.** SharpHook's `Dispose` is a blocking P/Invoke into libuiohook's `hook_stop()`
  with no timeout; calling it synchronously from `OnClosing` froze the app permanently when the native teardown wedged, and everything after it
  (including the server disconnect) never ran. `GlobalKeyHookService.Dispose` runs the native teardown on a background thread and `Join`s it for
  a bounded 2 s (`TeardownTimeout`), abandoning it on timeout — the hook thread is `IsBackground`, so process exit reaps it. The
  `internal GlobalKeyHookService(IGlobalHook)` seam lets a hanging fake pin this.
- **Pop-out `Closing` handlers clear their window field / dictionary entry *before* flipping the VM's pop-out flag.** CommunityToolkit's
  `PropertyChanged` fan-out is synchronous, so flipping `IsXxxPoppedOut` while `_xxxWindow` is still set re-enters `CloseXxxWindow()` →
  `Close()` on the window already inside its own `Closing` event. Headless Avalonia tolerates the re-entrancy, so only a real run shows the wedge.

## Footguns

- **Marshal SignalR handlers that touch UI state.** Every `ServerConnection` event handler that mutates an
  `ObservableCollection` or observable property wraps its body in `Dispatcher.UIThread.Post`. Omit it and you get
  intermittent cross-thread crashes unit tests won't catch. `OnPilotTransmissionReceived` is the one deliberate
  exception (touches no UI state); don't generalize from it.
- **A command the server refuses mid-load needs the room-loading gate.** A new command that reaches `LoadScenario`, `UnloadScenarioAircraft`, `RestartScenario` or a rewind adds `!IsRoomLoading` to its can-execute and its `NotifyCanExecuteChanged` to `NotifyRoomLoadingChanged`, or it stays clickable while the server refuses it.
- **`AircraftView` currency is fragile — never mutate `Aircraft` incrementally without care.** `AircraftView`
  (`DataGridCollectionView` over `Aircraft`, with the active/text filter) drives the aircraft `DataGrid`, and Avalonia's
  incremental collection handling under a filter+sort is buggy in two directions. **Add:** the sorted-insert mis-places a
  new row when the filter has shrunk the view, so every `Aircraft.Add` in `OnAircraftUpdated`/`OnAircraftSpawned` is
  followed by `RefreshAircraftView()`. **Remove:** `AdjustCurrencyForRemove` dereferences a stale `CurrentPosition` and
  throws `ArgumentOutOfRangeException` (crashing the client) when the grid's currency has drifted out of sync — so
  `OnAircraftDeleted` removes through `RemoveAircraftFromList`, which resets currency to "before first"
  (`AircraftView.MoveCurrentToPosition(-1)`) before `Aircraft.Remove`, wraps it in a logged try/catch that rebuilds the
  view as a last resort, and preserves a selection on a different aircraft (GitHub #237). Don't call `Aircraft.Remove`
  directly.
- **Aircraft-list column sorting must never hand `DataGridCollectionView` a throwing comparer.** `SetupDataGrid`
  wires each column's `CustomSortComparer` from `GetColumnSortComparer` (`MainWindow.axaml.cs`), which resolves the
  sort property from `SortMemberPath` or the column's binding, then wraps it in `GroupStableSortComparer`. The
  `DataGridTextColumn`s carry `x:DataType`, so their bindings **compile** — `bound.Binding` is a `CompiledBinding`, not a
  reflection `Binding` (this changed with the Avalonia 12 upgrade). Read the path from `CompiledBinding.Path`, not only
  `Binding.Path`, and let the no-property fallback return `NoOpSortComparer.Instance` — never `Comparer<object>.Default`,
  which throws `ArgumentException: At least one object must implement IComparable` on the raw `AircraftModel` rows,
  aborting `PrepareLocalArray` mid-sort so the view is left truncated (rows silently vanish from the list while staying on
  Radar/Ground/CRC). `PropertySortComparer` is already null-safe, so an unresolved property degrades to a no-op, not a crash.
- **Three scenario-activation paths, one router.** `ApplyScenarioResult` (loader), `OnScenarioLoaded` (broadcast),
  and `ApplyRoomState` (join/reconnect) all go through `ApplyScenarioBootstrap`. Wiring a new scenario-derived field
  into only the loader path silently breaks it for joiners and restart-restore rejoins. Add it to the
  `ScenarioBootstrap` record so all three paths carry it. `ApplyRecordingResult` writes the scenario identity without
  the router, so a consumer of "the active scenario changed" (the Discord presence publish) is wired there too.
- **Session settings need the echo guard.** A new `Session*` `[ObservableProperty]` with an `OnXxxChanged` that re-sends to the server must early-return on `_isApplyingSessionSettings`, and the field must be added to all four `ApplySessionSettingsFrom*` adapters + `SessionSettingsDto`. Miss the guard and the value ping-pongs with the server or the broadcast overwrites the user's local edit; miss an adapter and it drops on one of the join/load/live paths.

  A setting the flyout shows as two controls over one wire value (auto-accept: `SessionAutoAcceptEnabled` + `SessionAutoAcceptDelaySeconds` through `SessionAutoAcceptWire`) sends the pair from both handlers, each guarded. The terminal-filter toggles use the same pattern under `_isProgrammaticTerminalToggle`.
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
