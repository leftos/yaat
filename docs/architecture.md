# Architecture — File Tree

> **Read this file when you need to locate specific files or understand project structure.**
> CLAUDE.md contains the architectural summary; this file has the full annotated tree.

## Task Index — "I need to change X, which files?"

| Task | Key files (in order of relevance) |
|------|----------------------------------|
| **Add a new command** | `CommandRegistry.cs` → `CommandScheme.cs` → `CommandSchemeParser.cs` → `CommandDispatcher.cs` → appropriate `*CommandHandler.cs` (+ `VfrCommandPolicy.cs` if it is a VFR-only verb) |
| **Add a new phase** | `Phase.cs` (base) → new phase class → `PhaseList.cs` (registration) → `PhaseRunner.cs` (lifecycle) → `PhaseSnapshotDto.cs` (serialization) → `CommandDispatcher.cs` (acceptance) |
| **Altitude commands** | `AltitudeResolver.cs`, `FlightCommandHandler.cs`, `FlightPhysics.cs` (UpdateAltitude), `ControlTargets.cs` |
| **Speed commands** | `FlightCommandHandler.cs`, `FlightPhysics.cs` (UpdateSpeed/UpdateSpeedPlanning), `ControlTargets.cs`, `AircraftPerformance.cs` |
| **Heading/navigation** | `FlightCommandHandler.cs`, `NavigationCommandHandler.cs`, `FlightPhysics.cs` (UpdateNavigation/UpdateHeading), `ControlTargets.cs` |
| **Ground taxiing** | `GroundNavigator.cs`, `TaxiPathfinder.cs`, `TaxiingPhase.cs`, `TaxiRoute.cs`, `AirportGroundLayout.cs`, `RouteCostFunction.cs`, `GeometricAdmissibility.cs`, `AutoRouter.cs`, `SegmentExpander.cs`, `PartialRoute.cs` |
| **Pushback / tug move (`PUSH`, `PUSHM`, `PUSHF`), or a brief for one** | [`ground/pushback.md`](./ground/pushback.md) (the rules, then "Writing a push brief": probe recipe, premises, refusal texts) → `GroundCommandHandler.cs` (`ResolvePushTarget`, `TryPushbackMulti`) → `TugMovePlanner.cs` → `TugPathCheck.cs`, `TugTaxiwayClearance.cs` → `PushbackPhase.cs`; tests `Pathfinding/TugMovePlannerTests.cs`, `Pathfinding/TugAlleyClearanceTests.cs` |
| **Ground layout parsing** | `GeoJsonParser.cs`, `IFilletArcGenerator` / `FilletGeneratorFactory`, `FilletArcGenerator.cs` + `Fillet/` (plan-then-execute edge-split), `TaxiwayGraphBuilder.cs`, `CoordinateIndex.cs` |
| **Runway exits** | `LandingPhase.cs`, `ForcedLandingProfile.cs` (CLANDF), `RunwayExitPhase.cs`, `ExitPreference.cs`, `ExitCapacityResolver.cs`, `AirportGroundLayout.cs` (FindExitPath) |
| **Approach procedures** | `ApproachCommandHandler.cs`, `ApproachNavigationPhase.cs`, `FinalApproachPhase.cs`, `CifpParser.cs` |
| **SID/STAR** | `DepartureClearanceHandler.cs`, `InitialClimbPhase.cs`, `DepartureProcedurePhase.cs`, `ProcedureLegResolver.cs`, `ProcedureLeg.cs`, `CifpParser.cs`, `NavigationDatabase.cs` |
| **Radar rendering** | `RadarCanvas.cs` (input/zoom) → `RadarRenderer.cs` (drawing) → `TargetRenderer.cs` (datablocks) → `VideoMapRenderer.cs` (maps) |
| **Change where a radar or ground overlay label sits relative to datablocks** | `docs/radar-rendering.md` → `RadarCanvas.cs` → `GroundCanvas.cs` → `RadarDatablockLayout.ResolveBlockOffset` → `TargetRenderer.cs` / `GroundRenderer.cs` → `RangeBearingRenderer.cs` → `RblReadoutPlacement.cs` → `DatablockDeconfliction.cs` → `DatablockHitTestParityTests.cs` |
| **Ground view rendering** | `GroundCanvas.cs` (input/hit-test) → `GroundRenderer.cs` (drawing, 3 layers) |
| **Add or move a setting in the Settings window** | [`plans/client-surfaces-redesign/inventory.md`](./plans/client-surfaces-redesign/inventory.md) (every setting, entry point and format) → `Views/Settings/SettingsNavigation.cs` (sidebar order; ids in `ViewModels/SettingsSectionId.cs`) → the section's `Views/Settings/*Section.axaml` → `Views/Settings/SettingsSearchCatalog.cs` (the setting's search entry: label, heading, aliases) → `Views/SettingsWindow.axaml(.cs)` (section host, search box and sidebar filter, footer, key capture, `section-link` buttons) → `ViewModels/SettingsViewModel.cs` (constructor load, `Apply`/`ApplyCommand`, `Reset*` per section) → `Yaat.Client.Core/Services/UserPreferences.cs` (`SavedPrefs`, `CreateDefaults`) → `MainWindow.axaml.cs` (`ShowSettingsDialogAsync`: live preview, `ApplyCommittedSettings` on each Apply, focus record and restore) → `Views/OpenWindows.cs` (the open windows Settings blocks input to) → `tests/Yaat.Client.UI.Tests/Views/SettingsWindowSourceTests.cs` (the binding inventory) |
| **Add or change a session-flyout (room live) setting** | `Views/CommandInputView.axaml` (~:216, the gear flyout) → `ViewModels/MainViewModel.cs` (`Session*` fields, `ApplySessionSettings` and its three `ApplySessionSettingsFrom*` adapters, `OnSession*Changed` behind the `_isApplyingSessionSettings` guard, the `Send*` methods that push the Settings defaults after a load) → `MainViewModel.Scenario.cs` (`ApplyScenarioResult`, which calls the `Send*` methods; `SendScenarioToServer`) → [`client-mainviewmodel.md`](client-mainviewmodel.md#session-settings-echo-suppression) (the echo guard) → [`training-hub-contract.md`](training-hub-contract.md) (the four DTOs that carry the setting) → `tests/Yaat.Client.UI.Tests/ViewModels/MainViewModelSessionSettingsTests.cs` |
| **Change a Scenario defaults control's load-time application** | `Views/Settings/ScenarioDefaultsSection.axaml` → `ViewModels/SettingsViewModel.cs` (constructor load, `Apply`, `ResetScenarioDefaults`) → `Yaat.Client.Core/Services/UserPreferences.cs` (e.g. `SetSoloPacingRates`) → `MainViewModel.Scenario.cs` (`ExecuteLoadScenario`, `ScenarioSetupPlan`, `ApplyScenarioResult`'s `Send*` calls) → `Yaat.Client.Core/Services/SoloPacing.cs` (`LoadDefaults`: the pacing pair a load without the setup dialog sends) → tests `SettingsViewModelApplyTests`, `SettingsViewModelResetSectionTests`, `tests/Yaat.Client.Tests/SoloPacingTests.cs` |
| **Export or import settings (preferences, macros, verbs, favorites, grid layout, layouts) as a bundle** | [`plans/client-surfaces-redesign/README.md`](./plans/client-surfaces-redesign/README.md) (Import/export, Bundle, Import clashes) → `Yaat.Client.Core/Services/SettingsBundle.cs` (types, formats) → `SettingsBundleFile.cs` (zip + manifest, legacy single files) → `SettingsBundleItems.cs` (per-item adapters) → `SettingsImportPlanner.cs` (clashes, `ApplyAll`) → `SettingsImportTarget.cs` → `SettingsExportSource.cs` → `UserPreferences.Bundle.cs` (preferences allowlist and rules) → `Yaat.Client/ViewModels/ImportExportViewModel.cs` (the hub: export plan, import preview, clash rows, effect lines, the full backup before each import, atomic writes through `ImportExportFiles`' create-file seam, summary) → `Views/ImportExportWindow.axaml(.cs)` (`ShowLiveAsync` for the entry points outside Settings, `ShowOverAsync`) → the entry points: `MainWindow.axaml.cs` (Tools › Import / Export…, `OnSettingsImported`), `SettingsWindow.axaml.cs` (`OpenImportExport`: General, Macros, Command verbs), `FavoritesBarView.axaml.cs`, `ColumnChooserWindow.axaml.cs` → Settings' staged side: `ViewModels/SettingsViewModelImportTarget.cs` → `SettingsViewModelExportSource.cs` → `SettingsViewModel.Bundle.cs` → tests `SettingsBundleTests`, `SettingsBundleClashTests`, `SettingsBundlePreferencesTests`, `SettingsBundleHardeningTests`, `ImportExportViewModelTests`, `ImportExportWindowTests`, `SettingsViewModelImportTargetTests`, `FavoritesHubImportTests`, `ColumnChooserWindowTests`, `SettingsDialogHostTests` |
| **Change the Import / Export hub's backup or Merge/Replace behaviour** | `Yaat.Client/ViewModels/ImportExportViewModel.cs` (`BackUpFirst`, the backup's name and folder, `ImportItemRow.EffectText`, mode choice per row) → `Views/ImportExportWindow.axaml(.cs)` (the checkbox, effect line, backup result and **Open Folder**; constructs the view model with `YaatPaths.Combine("backups")`) → `Yaat.Client.Core/Services/SettingsExportSource.cs` (what a live backup holds) → `ViewModels/SettingsViewModelExportSource.cs` (what a backup taken inside Settings holds) → `tests/Yaat.Client.Tests/ImportExportViewModelTests.cs` → `USER_GUIDE.md` "Importing and exporting settings" |
| **Add or rebind a configurable hotkey** | `Yaat.Client.Core/Services/UserPreferences.cs` (the key's field, property, `Set*Key`, load default) → `ViewModels/SettingsViewModel.cs` (the keybind descriptor list: capture, reset, Apply, clash check; `KeysSectionKeybindRows`) → `Views/Settings/KeysSection.axaml` (rows rendered from the list) → `Views/WindowHotkeys.cs` (dispatch from every window, `FixedChords`) or `MainWindow.OnKeyDown` (take control, quick bookmark) → `MainWindow.axaml.cs` (`ShowMenuHotkeys`) → tests `WindowHotkeysTests`, `SettingsKeyCaptureTests`, `SettingsViewModelApplyTests`, `UserPreferencesDefaultsTests` |
| **Change the View menu or an import/export flow** | [`plans/client-surfaces-redesign/inventory.md`](./plans/client-surfaces-redesign/inventory.md) (every setting, entry point and format) → `Views/MainWindow.axaml` (View menu) → `MainWindow.axaml.cs` (`RebuildStripsSubmenu`, `RebuildTdlsSubmenu`, `PopulateLayoutMenu`) → `Views/FavoritesBarView.axaml.cs` (palette-header Import/Export open the hub with Favorites ticked) → `Views/ColumnChooserWindow.axaml.cs` (Import/Export open the hub; an imported column layout stages in the chooser) → `Views/CommandInputView.axaml` (gear flyout: the room's live session settings) |
| **Window layouts (save, apply, what a layout holds)** | `UserPreferences.cs` (`SavedLayout`, `SavedOpenTabs`, load-time rename from window profiles) → `Services/LayoutService.cs` (capture/apply) → `MainWindow.axaml.cs` (`PopulateLayoutMenu`, `ApplyLayoutAsync`) → `Views/ApplyLayoutDialog.axaml.cs` → `Services/ViewSettingsCopyCatalog.cs` (`LayoutGroups`) → tests `UserPreferencesLayoutTests`, `LayoutServiceOpenTabsTests`, `Issue365LayoutApplyRestoreTests` |
| **Change a pilot-voice setting (volume, radio effect, speed) or how the pilot voice is synthesized or played** | [`solo-training-pilot-speech.md`](./solo-training-pilot-speech.md) (Client-side TTS) → `Yaat.Client.Core/Services/UserPreferences.cs` (`PilotVoice*`, `SetPilotVoiceSettings`, `SavedPrefs`) → `ViewModels/SettingsViewModel.cs` (pilot-voice fields, load, `Apply`) → `Views/Settings/SpeechSection.axaml` ("Solo pilot voice") → `ViewModels/MainViewModel.Aircraft.cs` (`OnPilotTransmissionReceived`) → `Services/PilotVoiceService.cs` (`PilotVoiceRequest`, `SherpaOnnxPilotVoiceSynthesizer.SpeakAsync`, `RadioAudioFx`) → `tests/Yaat.Client.Tests/PilotVoiceServiceTests.cs` |
| **Add or change a user-guide screenshot (GuideCapture scene)** | [Yaat.GuideCapture](#yaatguidecapture--cli-tool-toolsyaatguidecapture) below → `tools/Yaat.GuideCapture/Capture/SceneCatalog.cs` → `Capture/Scene.cs` → `Scenes/ScenarioSceneBase.cs` (connected-room scenes) or `Scenes/StandaloneWindowSceneBase.cs` (standalone dialogs) → `Scenes/FlightPlanEditorScene.cs` (child window) / `Scenes/FavoritesScenes.cs` (a control in a bare window) → `Capture/SceneActions.cs` (`OpenMenuAsync` for menu shots, `OpenButtonFlyoutAsync` for a button's flyout, `SendCommandAsync` for a command and its terminal reply) → `USER_GUIDE.md` / `GETTING_STARTED.md` → `docs/user-guide/img/`; a scene that changes a preference restores it in `AfterCapture` |
| **Add a timeline/playback/bookmark screenshot scene** | [Yaat.GuideCapture](#yaatguidecapture--cli-tool-toolsyaatguidecapture) below → `tools/Yaat.GuideCapture/Capture/SceneCatalog.cs` → `tools/Yaat.GuideCapture/Scenes/TimelineSceneBase.cs` (`StagePlaybackAsync`) → `Scenes/ScenarioSceneBase.cs` → `Capture/RoomTicks.cs` → `src/Yaat.Client/ViewModels/MainViewModel.Timeline.cs` / `MainViewModel.Bookmarks.cs` → `src/Yaat.Client/Views/MainWindow.axaml` (timeline bar) |
| **Command input UX** | `CommandInputController.cs` (parse pipeline) → `ArgumentSuggester.cs` (dropdown values) → `SignatureHelpState.cs` (inline hints) |
| **Change the aircraft right-click menu (radar, ground, aircraft list: one tree on every view)** | [client-context-menus.md](client-context-menus.md) (the design) → [plans/context-menu-quick-commands.md](plans/context-menu-quick-commands.md) (the steps and their rulings) → [plans/context-menu-builder-refactor.md](plans/context-menu-builder-refactor.md) (Brief split: what each landed brief changed; Decided: the view-section slot, the point menu, Draw taxi route… and Push route… on every view) → `Yaat.Client.Core/ContextMenus/AircraftMenuBuilder.cs` (`Build(aircraft, click, host, viewSection)`: the one menu, in one order by the aircraft's kind; each view passes its click (`MenuClick.cs`), a `Views/ClientMenuHost.cs` and its view section: the radar's and ground's `BuildViewSection` in `Views/Radar/RadarView.ContextMenus.cs` / `Views/Ground/GroundView.axaml.cs`, an empty one from `Views/DataGridView.axaml.cs`; the quick commands at the top are `QuickCommandResolver.cs` (next row), the flight and tower groups under All Commands its private `AddFlightGroups` in one fixed order with `HidesFlightCommands` leaving out all but Tower in a ground phase, the RPO items `MainViewModel.Rooms.cs` `BuildRpoMenuItems`, Favorites `Views/FavoritesContextMenu.cs`) → `Yaat.Client.Core/ContextMenus/SharedMenuGroups.cs` (the groups, from `MenuCatalog.cs` entries shown by their `AircraftCommandApplicability.cs` predicates alone; a new item is `MenuIds` → `MenuCatalog` → its group, plus an `IMenuHost` member that `ClientMenuHost` and the test `RecordingMenuHost` both serve when it needs a popup or a host answer) → each view's right-click handler then appends `Views/ViewSettingsMenu.cs`'s "Settings for this view…" after the builder's menu (`RadarView.BuildAircraftRightClickMenu`, `GroundView.BuildAircraftRightClickMenu`, `DataGridView.BuildRowContextMenu`; pinned by `ViewSettingsMenuTests`) → `tests/Yaat.Client.UI.Tests/Views/MenuGoldenTests.cs` + `Goldens/menu/{radar,ground,list}/` (re-pinned with `YAAT_MENU_GOLDEN_REGENERATE=1`, the diff reviewed; `EveryView_SharesTheMenuOutsideItsViewSection` holds the three views to one tree; the builder's order and branches: `AircraftMenuBuilderTests.cs`, the host: `ClientMenuHostTests.cs`) |
| **Change what a context-menu aircraft shows per situation (the quick commands and the icon strip)** | [client-context-menus.md](client-context-menus.md) (Quick commands by situation: the lists and the show/hide rules) → [plans/context-menu-quick-commands.md](plans/context-menu-quick-commands.md) (the default situation table and its review notes) → `src/Yaat.Sim/Situation/SituationClassifier.cs` (which situation) → `SituationFlagCalculator.cs` (the flags and `NextCrossingRunway` the rules read; a new input goes through the "situation is classified" row below) → `Yaat.Client.Core/ContextMenus/QuickCommandDefaults.cs` (each situation's ordered list, flight-rules overrides) → `MenuCatalog.cs` / `MenuIds.cs` (the entry a list names; a missing one is added there first) → `AircraftCommandApplicability.cs` (the catalog predicates, then the quick-list-only `Shows*` visibility and `Widens*` widening rules) → `QuickCommandResolver.cs` (the `Visibility` and `Widening` tables that apply them, the flight-rules filter) → `QuickCommandGlyphs.cs` (catalog ID → glyph, the strip split) → `QuickCommandStrip.cs` (the strip control) → `AircraftMenuBuilder.cs` (`AddQuickCommands`) → tests: `tests/Yaat.Client.Tests/QuickCommandDefaultsTests.cs`, `QuickCommandResolverTests.cs`, `QuickCommandVisibilityRulesTests.cs`, `AircraftCommandApplicabilityTests.cs`; `tests/Yaat.Client.UI.Tests/Views/QuickCommandStripTests.cs` and the menu goldens; a widened rule is first proven in `tests/Yaat.Sim.Tests/Commands/QuickCommandSimAcceptanceTests.cs` (the sim accepts the command in that phase) |
| **Add or change the Quick Commands editor in Settings** | [client-context-menus.md](client-context-menus.md) (The Quick Commands editor: staging, storage, validation, preview) → `ViewModels/SettingsViewModel.QuickCommands.cs` (staged lists and rows, validation through `TypedCommandText`, the strip mark, the Add command… offer, add/remove/move, resets, `ApplyQuickCommandLists`) → `Views/Settings/QuickCommandsSection.axaml(.cs)` (preview, entry list and strip divider, drag reorder, the flyout) → `Yaat.Client.Core/ContextMenus/QuickCommandCatalog.cs` (the actions the editor may add) → `QuickCommandStrip.cs` (`GlyphIcon`, `GlyphCell`, `Rows`: the menu's factory the preview draws with) → tests `tests/Yaat.Client.UI.Tests/ViewModels/SettingsViewModelQuickCommandsTests.cs`, `tests/Yaat.Client.UI.Tests/Views/QuickCommandsSectionTests.cs` |
| **Change an aircraft's length or its fallback for types missing from the FAA database** | `src/Yaat.Sim/Data/Faa/AircraftLength.cs` (`ResolveFt`: FAA ACD length, else the CWT bucket table) → callers `AircraftCategory.SimplePushbackDistanceNm`, `Phases/Ground/TaxiingPhase.cs` (spot stop), `Phases/Ground/PushbackPhase.cs` (ramp priority), `GroundConflictDetector.cs` → tests `Data/AircraftLengthTests`, `Phases/ClearRunwayPhaseFallbackLengthTests` |
| **Change landing rollout braking, exit choice, runway exit or LAHSO** | [landing-and-runway-exit.md](landing-and-runway-exit.md) → `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` (`TryFindCandidate` / `ExitCandidateQuery`, `CommittedExitBrakingLimit`, `EvaluateAndApplyNamedExitInstruction` / `GiveUpNamedExit`, `TickLahsoStop`) → `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` (`FromSnapshot` path validation, `StartExitNavigation` / `ApplyExitDecelRate`, `EvaluateRetarget`, `CompleteExit`) → `src/Yaat.Sim/Phases/RolloutBraking.cs` → `src/Yaat.Sim/Commands/GroundCommandHandler.cs` (`TryExitCommand`) → tests `LandingExitDecelTests`, `Simulation/RunwayExitRestoreTests`, `Simulation/LahsoRolloutTests`, `SfoRunwayExitTests`, `RolloutBrakingRatesTests` |
| **Fix an STT phraseology rule that never matches** (or a wrong canonical from the ouroboros) | [speech-recognition-pipeline.md](speech-recognition-pipeline.md) (the mapper's pass order) → [plans/stt-rule-gaps.md](plans/stt-rule-gaps.md) → `tools/Yaat.SpeechSandbox/Corpus/atc-ouroboros-baseline.json` (`failures[]`: each failing case's transcript and mapper output) → `tools/Yaat.SpeechSandbox/SynthTemplates.cs` (template → expected canonical) → `src/Yaat.Sim/Speech/PhraseologyRules.cs` → `PhraseologyMapper.cs` (`MapWithTrace` pass order, capture guards) → `AtcNumberParser.NormalizeDigits` / `NatoLetterNormalizer.cs` / `TrafficCallsignNormalizer.cs` (what rewrites the tokens first) → `CallsignParser.cs` → `PhraseologyVerbalizer.cs` (shares the non-`SttOnly` patterns) → `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs`, `tests/Yaat.Client.Tests/AtcOuroborosTests.cs` (`KnownGaps`) |
| **Change how the speech eval or the ouroboros scores, synthesizes or caches audio** | [speech-recognition-pipeline.md](speech-recognition-pipeline.md) (Speech Sandbox Tool) → `tools/Yaat.SpeechSandbox/EvalRunner.cs` (`ScoreCaseAsync`, `CanonicalsMatch`, `Tally`) → `AtcOuroborosResults.cs` (`Compare`, `HasRegression`) → `AtcOuroborosRunner.cs` → `SynthTemplates.cs` (`Render`, `SpeakerPool`) → `SynthCorpusGenerator.cs` → `SynthAudioCache.cs` → `PiperSynthesizer.cs` → `src/Yaat.Client/Services/PilotVoiceService.cs` (`RadioAudioFx`) → tests `tests/Yaat.Client.Tests/AtcOuroborosTests.cs`, `SynthAudioCacheTests.cs`, `EvalRunnerSessionStubTests.cs` |
| **Swap or add an STT engine, a Whisper model source, or the GPU backend** | [speech-recognition-pipeline.md](speech-recognition-pipeline.md) (STT Stage, Bumping LM-Kit.NET) → `src/Yaat.Client/Services/WhisperSttEngine.cs` → `SpeechRecognitionService.cs` → `LmKitModelCatalog.cs` → `CudaBackendInstaller.cs` → `src/Yaat.Client/Program.cs` (backend directory, license) → `ViewModels/SettingsViewModel.cs` + `Views/SettingsWindow.axaml` → `src/Yaat.Client.Core/Services/UserPreferences.cs` (`WhisperModelSize`, `SetSpeechSettings`) → `tools/Yaat.SpeechSandbox/EvalRunner.cs` (`EvalPipeline`), `SherpaSttEngine.cs` → `Yaat.Client.csproj` + `Yaat.SpeechSandbox.csproj` → `.github/workflows/release-macos.yml` → `tests/Yaat.Client.Tests/SpeechModelDefaultsTests.cs` |
| **Solo auto-accept floor or delay** | `SimScenarioState.cs` (`SoloAutoAcceptFloorSeconds`, `EffectiveAutoAcceptDelaySeconds`) → `SimulationEngine.TrackAutomation.cs` (`TickAutoAccept`) → `ControllerAi/AiControllerService.cs` → `ControllerAi/AiTickContext.cs` → `HandoffUnacceptedRule.cs` → `USER_GUIDE.md` (Auto-Accept) |
| **Per-tick step trace or `TickTimings`** | `SimulationEngine.Spine.cs` (`RunSegment`, `RunPhysicsSubTick`, timing) → `Spine/StepTrace.cs` → `SimulationEngine.Replay.cs` (`TickTimings`, `DumpTickTimings`) → `docs/tick-loop.md` § The step trace; yaat-server `Soak/SoakEpisodeRunner.cs`, `Simulation/RoomEngine.cs` (`CreateTempReplayEngine` forwards the sink), `tests/Yaat.Server.Tests/Oracle/TickOracleTests.cs`; tests `Simulation/TickTimingsTests.cs`, `Simulation/SpineTraceTests.cs` |
| **When or whether a solo ground spawn makes its first call (ready to taxi, or a clearance request to delivery)** | `docs/solo-training-pilot-speech.md` (Which spawns make the first call) → `Scenarios/InitialCallupClassifier.cs` + `InitialCallupPlan.cs` + `PresetTaxiStop.cs` → `ScenarioLoader.cs` (`LoadAircraft`, `ArmCoordinateGroundSpawn`, `LoadAtParking`) → `Data/Airport/GroundSpawnSnap.cs` (`SpawnTaxiwayAt`) → `Pilot/InitialCallupCall.cs` + `Pilot/ReadyToTaxiLocation.cs` → `Phases/Ground/AtParkingPhase.cs` (+ `HoldingAfterPushbackPhase.cs`, `HoldingInPositionPhase.cs`, `HoldingShortPhase.cs`) → `Commands/GroundCommandHandler.cs` (`InstallTugMove`) → `Pilot/PilotResponder.cs` (`BuildReadyToTaxi`, `BuildClearanceRequest`) → `Pilot/PilotRequestTracker.cs` → `AircraftGroundOps.cs` (+ DTO) → `ScenarioPacing.cs` → `Yaat.Client/Services/ScenarioDifficultyHelper.cs`; tests `M101GroundSpawnCheckInTests`, `InitialCallupClassifierTests`, `AtParkingPhaseCallupTests`, `ReadyToTaxiCallupE2ETests`, `DelayedInitialCallupE2ETests`, `DeliveryClearanceRequestE2ETests` |
| **Untowered runway spawn release request (and the solo-room auto takeoffs: runway spawns under a radar APP/CTR student and released hold-for-release departures; in an RPO room nothing takes off by itself and the RPO or a preset launches it)** | `docs/solo-training-pilot-speech.md` (Runway spawns) → `docs/hold-for-release.md` (Release requests from untowered runway spawns) → `Pilot/RunwaySpawnCall.cs` → `Phases/Tower/LinedUpAndWaitingPhase.cs` → `Simulation/SimulationEngine.Presets.cs` (`ProcessRunwaySpawnAutoTakeoffs`, `ProcessReleasedGroundDepartures`, `IsRunwaySpawnFieldTowered`) → `ControllerAi/AiPositionResolver.cs` (`IsTowered`) → `Simulation/HeldReleaseService.cs` (`ReleaseHeldGroundDeparture`, spawn gate `ReleasedAtSpawnGate`) → `Pilot/PilotRequestTracker.cs` (`SatisfyOpenRequest`) → `Pilot/PilotResponder.cs` (`BuildReleaseRequest`); tests `RunwaySpawnCallE2ETests`, `ReleasedDepartureLineUpE2ETests` |
| **Withhold auto-accept while a track may show CST** | `Commands/TrackEngine.cs` (`IsAutoAcceptWithheld`) → `Simulation/SimulationEngine.TrackAutomation.cs` (`TickAutoAccept`) → `Simulation/Coast/DisconnectCoastRules.cs` (`IsVisibleOnEram`) → `AircraftEramState.cs` (`IsCoastTrack`) → `ControllerAi/Rules/HandoffUnacceptedRule.cs` (skips a withheld handoff) → yaat-server `Simulation/CrcVisibilityTracker.cs` (`EvaluateEram`) → tests `TrackAutomationStepTests.cs`, `ObserverRulesTests.cs`, yaat-server `AutoAcceptCstTests.cs` |
| **Weather** | `WeatherProfile.cs`, `WeatherTimeline.cs`, `WindInterpolator.cs`, `WindVariation.cs`, `WindObservation.cs`, `LiveWeatherService.cs`, `MetarComposer.cs`, `MetarIssuer.cs`, `SpeciCriteria.cs` |
| **Scenarios** | `ScenarioLoader.cs`, `ScenarioExporter.cs` (the loader run backwards, for `TrainingHub.ExportRoomAsScenario`), `ScenarioModels.cs`, `AircraftInitializer.cs`, `ScenarioLifecycleService.cs` (server) |
| **Snapshots/replay** | `StateSnapshotDto.cs`, `AircraftSnapshotDto.cs`, `RecordingArchive.cs`, `SimulationEngine.cs` |
| **Produce a recording headless (a demo or video clip)** | yaat-server `tools/Yaat.SoakRunner/Program.cs` (`--script`, `--solo`, `--solo-rpo-commands`; `SoakOptions.cs`) → `src/Yaat.Server/Soak/SoakEpisodeRunner.cs` (+ `SoakScript.cs`, the script format) → `Soak/SoakRecordingSink.cs` → `Simulation/Headless/HeadlessRoom.cs` (`SendScriptedCommand`) → yaat `src/Yaat.Sim/Simulation/RecordingArchiveWriter.cs` → [`client-driver-mcp.md`](client-driver-mcp.md#recording-a-demo) "Recording a demo"; server tests `Soak/ScriptedEpisodeTests`, `Soak/SoakScriptTests` |
| **Add or change an automation pipe method (the client's in-process driver endpoint)** | [plans/client-driver-background.md](plans/client-driver-background.md) → `src/Yaat.Client/Automation/AutomationHost.cs` → `AutomationDispatcher.cs` (register the handler) → `Handlers/` → `Tree/NodeRegistry.cs` → `Protocol/` (wire types, no Avalonia; the MCP links them) → `Transport/` → `App.axaml.cs` (start/dispose) → `Handlers/HandlerResult.cs` + `Protocol/ProtocolMethods.cs` + `Protocol/AutomationErrorCodes.cs` (a new method's name and error codes) → `Selectors/SelectorRequestHelper.cs` / `Handlers/TargetResolver.cs` → `Tree/ElementDescription.cs` → `tests/Yaat.Client.UI.Tests/Automation/` on `Helpers/AutomationHostFixture.cs` + `Helpers/AutomationPipeTestClient.cs` (pixel tests: `tests/Yaat.Client.UI.Render.Tests/Automation/`) |
| **Add an app-defined tool the client-driver MCP can call** | [client-driver-mcp.md](client-driver-mcp.md) → `src/Yaat.Client/Automation/Tools/AutomationTools.*.cs` (an `[AutomationTool]` method and its availability method) → `AutomationToolAttribute.cs` → `Handlers/CallAppToolHandler.cs` (binding) → `tests/Yaat.Client.UI.Tests/Automation/AppToolsTests.cs`; a new pipe method instead: `Protocol/ProtocolMethods.cs` → `AutomationDispatcher.cs` → `Handlers/QueueFilePickHandler.cs` (the exemplar) → `tools/Yaat.ClientDriver.Mcp/Tools/PipeTools.cs` → `tests/Yaat.ClientDriver.Mcp.Tests/WaitUntilToolTests.cs` |
| **Place a range-bearing line or move a data block (the `.rbl` command, the drag, or an app tool)** | [radar-rendering.md](radar-rendering.md) → `src/Yaat.Client/ViewModels/MainViewModel.cs` (`TryHandleMeasureCommand`, `PlaceMeasurementFromText` → `MeasurePlacement`, shared by `.rbl A B` and `place_rbl`) → `ViewModels/RangeBearingViewState.cs` (`Place`/`Remove`/`Clear`, the 15-slot store) → `Services/MeasureEndpointResolver.cs` → `ViewModels/DataBlockViewState.cs` (`ManualOffsets`, `SetManualOffset`/`RemoveManualOffset`, `ManualOffsetsChanged`) → `Views/Radar/RadarCanvas.cs` (drag, reset, repaint on the event) → `ViewModels/RadarViewModel.cs` (`SetDataBlockOffset`) → server leader direction: `LDR` → `Yaat.Sim/Commands/TrackEngine.cs` (`HandleLeaderDirection`) → `Automation/Tools/AutomationTools.Measure.cs` / `.DataBlock.cs` → `tests/Yaat.Client.UI.Tests/Automation/AppToolsTests.cs`, `Views/DataBlockStatePersistenceTests.cs` |
| **Record the client's window from the client-driver MCP** | [client-driver-mcp.md](client-driver-mcp.md) ("Recording a demo") → `tools/Yaat.ClientDriver.Mcp/Tools/RecordTools.cs` → `Recording/RecordingSession.cs` (one recording, marks, stop) → `Recording/FfmpegPipeline.cs` (the command) → `Recording/ProcessRecordingBackend.cs` / `IRecordingBackend.cs` → `Recording/ClientAreaCrop.cs` + `NativeInput.ReadWindowGeometry` → `tools/Yaat.WindowRecorder/Program.cs` → `Tools/PipeTools.cs` (wait_until's stop_recording) → `tests/Yaat.ClientDriver.Mcp.Tests/RecordToolsTests.cs`, `FfmpegPipelineTests.cs`; live: `live-check.ps1 -Record` |
| **Make the driven client invisible or silent for a recording** | [client-driver-mcp.md](client-driver-mcp.md) ("Recording a demo", "A cloaked window") → `tools/Yaat.ClientDriver.Mcp/Tools/ProcessTools.cs` (`launch_yaat` `cloaked`, `audioOutputDevice`, `IsToolOwnedVariable`) → `src/Yaat.Client/Program.cs` + `Automation/AutomationMode.cs` (`YAAT_CLOAK`) → `src/Yaat.Client.Core/Views/AutomationGate.cs` (`ApplyShowActivated`, `Cloaker`) → `src/Yaat.Client/Automation/Tools/AutomationTools.Window.cs` (`set_cloaked`) → `src/Yaat.Client/Services/PilotVoiceService.cs` (`ResolveOutputDevice`) → `tools/Yaat.WindowRecorder/` (process loopback) → `tests/Yaat.ClientDriver.Mcp.Tests/LaunchYaatTests.cs`, `tests/Yaat.Client.UI.Tests/Automation/AppToolsTests.cs`, `AutomationNoActivateStyleTests.cs` |
| **Route a client-driver MCP tool over a YAAT client's automation pipe** | [client-driver-mcp.md](client-driver-mcp.md) → [plans/client-driver-background.md](plans/client-driver-background.md) → `tools/Yaat.ClientDriver.Mcp/Tools/InputTools.cs` / `Tools/InspectTools.cs` → `Pipe/PipeCalls.cs` (element- and pid-level sends, error mapping) → `Pipe/PipeCallErrors.cs` (client errors appended to every result) → `Pipe/PipeDirectory.cs` (discovery, the remembered pid) → `Pipe/PipeInput.cs` → `ElementRegistry.cs` / `ElementRef.cs` → client `src/Yaat.Client/Automation/Handlers/` + `Protocol/` (the method it calls) → `tests/Yaat.ClientDriver.Mcp.Tests/` (`InputToolsPipeTests.cs`, `InspectToolsPipeTests.cs`, `PipeCallsTests.cs`, `ClientErrorsTests.cs`, `BatchToolsTests.cs`) |
| **Make the client's file dialogs answerable by an automation agent (injected picker, `queue_file_pick`)** | [plans/client-driver-background.md](plans/client-driver-background.md) → `src/Yaat.Client/Services/FilePickerFactory.cs` (every client picker is built here) → `src/Yaat.Client/Automation/InjectedFilePickerService.cs` → `Automation/FilePickQueue.cs` → `Automation/Handlers/QueueFilePickHandler.cs` → `Automation/Protocol/QueueFilePickParams.cs` / `QueueFilePickResult.cs` → `src/Yaat.Client.Core/Services/IFilePickerService.cs` → `src/Yaat.Client/Services/AvaloniaFilePickerService.cs` → `tests/Yaat.Client.UI.Tests/Automation/FilePickTests.cs`, `AutomationModeSourceTests.cs` (no picker or `StorageProvider` outside the seam) |
| **Change the Load Scenario window / local recent scenarios** | `src/Yaat.Client/Views/LoadScenarioWindow.axaml(.cs)` → `src/Yaat.Client/Views/MainWindow.axaml.cs` (`OnLoadScenarioClick`) → `src/Yaat.Client/ViewModels/MainViewModel.Scenario.cs` (`ExecuteLoadScenario`, `RememberLoadedScenario`) → `src/Yaat.Client.Core/Services/UserPreferences.cs` (`AddRecentScenario` / `RemoveRecentScenario`) → `src/Yaat.Client.Core/Services/IFilePickerService.cs` |
| **Scenario load: prepare/commit, the load flag, load progress** | [`server-rooms-and-hub.md`](server-rooms-and-hub.md#scenario-load-prepare-commit-and-the-load-flag) → yaat-server `Simulation/ScenarioLifecycleService.cs` (`PrepareScenarioAsync`, `PrepareResourcesAsync`, `CommitPreparedScenario`, `PopulateRoom`) → `Simulation/RoomEngine.cs` (`LoadScenarioGuardedAsync`, `StartLiveSessionGuardedAsync`, `LoadRecordingGuardedAsync`, `RunUnderLoadFlagAsync`) → `Simulation/TrainingRoom.cs` (gate, load flag) → `Hubs/TrainingHub.cs` (`LoadScenario`, `StartLiveSession`, `LoadRecording`, the refused lifecycle calls) → `Data/AirportGroundDataService.cs` / `Data/ArtccConfigService.cs` (fetch outcomes) → `Simulation/ScenarioLoadReporter.cs` (the step table and its texts) → `src/Yaat.Sim/Scenarios/ScenarioResourceManifest.cs`; wire [`training-hub-contract.md`](training-hub-contract.md#scenario-load-progress); server tests `ScenarioLoadPrepareCommitTests`, `ScenarioLoadRefusalTests`, `ScenarioLoadProgressTests`, `Harness/ScenarioLoadFixtures.cs`, `Harness/RoomEngineTestHarness.cs` |
| **Scenario load overlay (client)** | `src/Yaat.Client/ViewModels/LoadOverlayViewModel.cs` (state, close rules) → `MainViewModel.Scenario.cs` (`OnScenarioLoadProgress`, `OnRoomLoadingChanged`, `IsRoomLoading`, the off-UI-thread parse) → `Views/MainWindow.axaml` (the overlay) → `src/Yaat.Client.Core/Services/ServerConnection.cs` (`ScenarioLoadProgressDto`, `LoadStepDto`, `Steps`, `LoadingBy`) + `YaatHubJsonContext.cs`; [`client-mainviewmodel.md`](client-mainviewmodel.md#scenario-load-overlay-and-the-room-loading-gate); tests `tests/Yaat.Client.UI.Tests/ViewModels/LoadOverlayViewModelTests.cs`, `MainViewModelLoadOverlayTests.cs`, `tests/Yaat.Client.Tests/HubJsonContractTests.cs` |
| **Room resource pin (what reloads read)** | yaat-server `Simulation/RoomResourcePin.cs` (`RoomResourcePin`, `PinnedAirportGroundData`) → `Simulation/ScenarioLifecycleService.cs` (`ReloadForRewind(Async)`, `ReloadRecording`, `PinArchivedResourcesAsync`) → `Simulation/RecordingManager.cs` (export bundle, `MigrateToV2Async`, recording loads) → `Simulation/Persistence/` (checkpoint v2 `artcc-configs.json.br`); [`server-rooms-and-hub.md`](server-rooms-and-hub.md#the-resource-pin); tests `Simulation/RoomResourcePinTests`, `SessionPersistenceTests` |
| **CRC protocol** | `CrcDtos*.cs` (wire format) → `DtoConverter.cs` (translation) → `CrcBroadcastService.cs` (dispatch) → `CrcWebSocketHandler.cs` (connection) |
| **vEDST: joined sessions, `GenerateFrd`, private messages** | [`vedst.md`](vedst.md) → yaat-server `Hubs/CrcClientState.Join.cs` (allowlist, join/leave, fan-out) → `CrcClientState.Session.cs` (`HandleGenerateFrd`) → `CrcClientState.Messaging.cs` (`HandleSendPrivateMessage`) → `Protocol/CrcJsonTranscoder.cs` (method/callback maps) → `Auth/VnasCompatEndpoints.cs`; docs [`crc-protocol-support.md`](crc-protocol-support.md), [`vatsim-auth.md`](vatsim-auth.md), [`vedst-sign-in.md`](vedst-sign-in.md); tests `CrcJoinSessionTests`, `CrcJoinFanOutTests`, `Hubs/CrcVfrRemarksResponseWireTests`, `CrcJsonTranscoderTests` |
| **Discord Rich Presence (desktop client)** | `src/Yaat.Client/Services/Discord/` (`DiscordRichPresenceService` worker, `DiscordIpcConnector`, `DiscordIpcFrame`, `DiscordActivity`/`DiscordRpcJson`) → publish points in `MainViewModel.Scenario.cs` (`StartRichPresence` / `RefreshRichPresence`) and `MainViewModel.Timeline.cs` (`ApplyRecordingResult`) → `MainWindow.axaml.cs` (construction behind `App.DiscordRichPresenceAvailable`); `docs/discord-integration.md` |
| **Discord bot / server-cost tracker** | `tools/discord-bot/src/worker.js` (routing, GitHub↔thread sync) → `src/support.js` (Ko-fi webhook, ledger, ticker + embed) → `scripts/setup-support.js`; `docs/discord-integration.md` |

## Subsystem deep-dive docs

The Task Index above tells you *which files*; these docs explain *how each subsystem works* — read the relevant one before a non-trivial change. (Full annotated list with summaries in [CLAUDE.md](../CLAUDE.md) → Reference Docs.)

| Area | Deep-dive doc(s) |
|------|------------------|
| Command dispatch & per-domain handlers | [command-pipeline.md](command-pipeline.md), [command-handlers.md](command-handlers.md) |
| Chained-command (`;`/`,`) contract | [command-chaining.md](command-chaining.md) |
| Command input (autocomplete / signature help) | [command-input-ux.md](command-input-ux.md) |
| Aircraft data model & `SimulationWorld` | [aircraft-data-model.md](aircraft-data-model.md) |
| Flight physics, airspeed frames & constants | [flight-physics.md](flight-physics.md) |
| Per-tick execution order | [tick-loop.md](tick-loop.md) |
| Phase system (base contract) | [phases.md](phases.md) |
| Airborne approach / pattern geometry | [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md) |
| Landing rollout & runway exit | [landing-and-runway-exit.md](landing-and-runway-exit.md) |
| Ground stack (fillet / pathfinder / navigator / hold-short placement) | [ground/README.md](ground/README.md) |
| Navigation database & route expansion | [navigation-database.md](navigation-database.md) |
| Conflict / alert / visual detection | [conflict-and-visual-detection.md](conflict-and-visual-detection.md) |
| Weather & wind | [weather-and-wind.md](weather-and-wind.md) |
| Airspace (Class B/C) & boundary crossing | [airspace-database.md](airspace-database.md) |
| Minimum Vectoring Altitude (MVA) | [minimum-vectoring-altitude.md](minimum-vectoring-altitude.md) |
| Military training routes & aerial refueling (AP/1B) | [military-training-routes.md](military-training-routes.md) |
| Controller AI facility SOP knowledge | [facility-ops-knowledge.md](facility-ops-knowledge.md) |
| Scenario loading & aircraft generation | [scenario-loading-and-generation.md](scenario-loading-and-generation.md) |
| Snapshots & replay | [snapshots-and-replay.md](snapshots-and-replay.md) |
| Solo-training evaluation & scoring | [solo-training-evaluation.md](solo-training-evaluation.md) |
| STARS/ERAM track sharing & consolidation | [track-sharing-and-consolidation.md](track-sharing-and-consolidation.md) |
| CRC display state & broadcast | [crc-display-state.md](crc-display-state.md) |
| Change what a map, taxi-node, runway-threshold or runway-surface right-click offers (the point menu) | [plans/context-menu-builder-refactor.md](plans/context-menu-builder-refactor.md) (Design, Point clicks) → `Views/Ground/GroundCanvas.cs` (`HandleRightClick`, `FindRunwaysAtPoint`, `FindRunwayThresholdAtPoint`) → `Yaat.Client.Core/ContextMenus/MenuClick.cs` (`MenuPoint`: node, runway end, surface runways, warp node) → `MenuIds.cs` / `MenuCatalog.cs` (the `point.*` entries, `point.taxi-to-runway`) → `AircraftMenuBuilder.cs` (`BuildPointMenu`) → `IMenuHost.cs` (`DescribePoint`, `GetTaxiChoices`, `GetCustomTaxiSeed`, the runway hold-short targets) → `Views/ClientMenuHost.cs` → `ViewModels/GroundViewModel.cs` (`FindRoutesToNode`, `BuildTaxi*Variants`, the hold-short pickers) → the views' `OnMapRightClicked` / `OnNodeRightClicked` / `OnRunwayThresholdClicked` / the runway-surface handler → `tests/Yaat.Client.UI.Tests/Views/{PointMenuTests,PointMenuViewTests,GroundCanvasRunwaySurfaceHitTests,ClientMenuHostGroundTests,RecordingMenuHost}.cs`, `tests/Yaat.Client.UI.Tests/ViewModels/GroundViewModelRunwayHoldShortTests.cs` |
| Change the aircraft menu's ground-movement items or the ground view's section (Display ▸) | [plans/context-menu-quick-commands.md](plans/context-menu-quick-commands.md) (Brief 5 settled) → `Yaat.Client.Core/ContextMenus/AircraftMenuBuilder.cs` → `Views/ClientMenuHost.cs` → `Views/Ground/GroundView.axaml.cs` (`BuildAircraftContextMenu`, `BuildViewSection`, `BuildCanvasItems`) → `Yaat.Client.Core/ContextMenus/` (`MenuIds` `ground.*`, `MenuCatalog`, `SharedMenuGroups`, `AircraftCommandApplicability`, `HoldShortMenuHelper`, `RelativeTraffic`) → `ViewModels/GroundViewModel.cs` → `tests/Yaat.Client.UI.Tests/Views/{GroundMovementMenuTests,GroundSubmenuCharacterizationTests,GroundSubmenuGroupTests,ClientMenuHostGroundTests,Issue229TakeoffMenuRunwayTests,GroundContextMenuHoldShortTests,MenuCatalogCommandTests,MenuGoldenTests}.cs` and `Goldens/menu/ground/` |
| Change how an aircraft's situation is classified, or add a situation input or flag | [plans/context-menu-quick-commands.md](plans/context-menu-quick-commands.md) (Step 1 and Step 6 rulings) → `src/Yaat.Sim/Situation/SituationClassifier.cs` / `SituationFlagCalculator.cs` → `Situation/AircraftSituationState.cs` → `Simulation/SimulationEngine.Tick.cs` (`TickSituation`) + `Simulation/Spine/SpineOrder.cs` → `Simulation/Snapshots/AircraftSituationStateDto.cs` → yaat-server `Simulation/DtoConverter.cs` and `AircraftChangeTracker.cs` (the stored value, fingerprinted) → [training-hub-contract.md](training-hub-contract.md) → the client's `ServerConnection.cs` `AircraftDto`, `Models/AircraftModel.cs`, `Yaat.Client.Core/ContextMenus/IMenuAircraft.cs` (a new wire field travels all three) → `tests/Yaat.Sim.Tests/Situation/SituationClassifierTests.cs`, `SituationFlagCalculatorTests.cs`, `SituationStepTests.cs`, `Simulation/SpineTraceTests.cs`, `tests/Yaat.Client.Tests/AircraftDtoSituationTests.cs` |
| A right click opens the wrong target's menu (aircraft vs aircraft, aircraft vs parking spot) | [ground-rendering.md](ground-rendering.md) "Right-click vs right-drag" → `Views/Ground/GroundCanvas.cs` / `Views/Radar/RadarCanvas.cs` (`HandleRightClick`, `FindRightClickTargets`, `FindAircraftAtPoint`, `FindDataBlockAtPoint`, `FindNodeAtPoint`) → `Views/Map/RightClickPicker.cs` + `RightClickTarget.cs` → `GroundView.axaml.cs` (`OnNodeRightClicked`, `OnAircraftRightClicked`) → `tests/Yaat.Client.UI.Tests/Views/*RightClick*` |
| Add or change an ERAM command (e.g. `FP`/`SP` filing) | [eram/README.md](eram/README.md) → the command's `eram/commands/*.yaml` → yaat-server `CrcClientState.Eram.cs` (`DispatchEramMessage`) → its `CrcClientState.Eram.*.cs` handler (filing: `Eram.Filing.cs`, reusing the AM amenders in `Eram.FlightData.cs`) → `Eram/EramVariants.cs`, `Eram/EramError.cs` → `RoomEngine.AmendFlightPlan` for a plan change → `EramConformance*Tests`, `EramFeedbackConventionTests` |
| ERAM 12 s sweep, coasts and the QT/QH display position | [crc-display-state.md](crc-display-state.md) § ERAM 12 s sweep → yaat-server `AircraftChangeTracker.cs` (the sweep state, `EramSweptPosition`) → `CrcVisibilityTracker.cs` (`EvaluateEram`, the coast anchor) → `CrcBroadcastService.cs` (`AddEramUpdates`, `BuildEramTrack`, `BuildInitialData`) → `DtoConverter.cs` (`EramMotion`) → `CrcClientState.Eram.cs` (`EramCoastEntryFor`) → Yaat.Sim `Commands/EramEntryEngine.cs` (QT/QH) → `CrcEramSweepTests`, `CrcVisibilityTrackerTests`, `RecordedStateChangeTests` |
| ASDE-X/SAID membership and CRC surface-display visibility (who decides, where hysteresis lives) | [crc-display-state.md](crc-display-state.md) → `src/Yaat.Sim/Asdex/SurfaceMembership.cs` → `src/Yaat.Sim/Simulation/SimulationEngine.Asdex.cs` (`TickSurfaceMembership`) → yaat-server `src/Yaat.Server/Simulation/CrcVisibilityTracker.cs` → `CrcBroadcastService.cs` (`BroadcastDisconnectAsync`) → `Simulation/RoomHost.cs` (`OnDisconnectCoast*`) → `tests/Yaat.Sim.Tests/Simulation/Asdex/SurfaceMembershipStepTests.cs`, yaat-server `tests/Yaat.Server.Tests/CrcVisibilityTrackerTests.cs`, `DisconnectCoastWiringTests.cs` |
| Change FOLLOW's refusals or sequencing | [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md) (*Visual following*, *FOLLOW rulings a change must respect*), [`COMMANDS.md`](../COMMANDS.md) *FOLLOW refusals* → `CommandDispatcher` (`TryAirborneFollow` → `RouteFollow` → `TryRouteRunwaylessLead` / `TryFollowFromPatternLeg` → `InstallFollow`) → `AirborneFollowHelper` (`PatternLegIndex`, `SequenceLegIndex`, `IsClosedTrafficClimb` (a close-parallel `DepartureRunway` through `RunwayGeometry.AreCloseParallels`), `SharedLegOrderNm`) → each phase's `CanAcceptCommand`; a FOLLOW from a climb or go-around: `TakeoffPhase` / `GoAroundPhase` (`IPendingPursuitClimb`) → `PhaseRunner` (the hand-over that starts a pending pursuit) → `VfrFollowPhase` (`ClimbOutGate`) → `UpwindPhase.PastDepartureEndAtTurnAltitude` → `PhaseSnapshotDto` → `FollowRunwaylessLeadFromPatternTests`, `FollowSequenceRefusalTests`, `FollowOutsidePatternRefusalTests`, `FollowClimbFollowerTests` |
| FOLLOW from an instrument approach or a pursuit | `CommandDispatcher.RouteFollow` (`GroundOrElsewhereLeadRefusal`, `SameLeadReFollow`, `PursuitNewLeadRoute` (a new lead during a from-base pursuit or turn-out: `VfrFollowPhase.NewLeadCircuit`, `FollowerPathToThresholdNm`, `AirborneFollowHelper.IsStraightInEntry`; `FollowPursuitNewLeadTests`), `RunwaylessLeadConeRefusal`) → `KeepApproachFollow` / `InstallFollow` → `AirborneFollowHelper` (`FollowerRemainingPathNm`, `SequenceRemainingPathNm`, `IsIfrFollower`, `CheckLeadLifecycle`) → `VisualApproachHelper.HandleTrafficContactLost` → `InterceptCoursePhase` / `ApproachNavigationPhase` (`IsMissedApproach`) / `ProcedureTurnPhase` / `HoldingPatternPhase` (`IsHoldInLieu`) (lead lifecycle only) → `FinalApproachPhase` (the final-approach speed spacing) → `VfrFollowPhase` (`PatternReturn`, `UpdateTarget`) → `ApproachGateDatabase` (FAF) → `FollowOutsidePatternRefusalTests`, `FollowSequenceRefusalTests`, `FollowKeepApproachTests`, `FollowPreFinalSpacingTests` |
| Client↔server SignalR contract | [training-hub-contract.md](training-hub-contract.md), [server-rooms-and-hub.md](server-rooms-and-hub.md) |
| Client MainViewModel & orchestration | [client-mainviewmodel.md](client-mainviewmodel.md) |
| Radar display & rendering | [radar-rendering.md](radar-rendering.md) |
| Ground view rendering & taxi-route overlays | [ground-rendering.md](ground-rendering.md) |
| Flight strips / vTDLS | [flight-strips.md](flight-strips.md), [vtdls.md](vtdls.md) |
| Speech (STT) & pilot speech (TTS) | [speech-recognition-pipeline.md](speech-recognition-pipeline.md), [solo-training-pilot-speech.md](solo-training-pilot-speech.md) |
| Pilot phraseology (wording / forms / AIM) | [pilot-phraseology.md](pilot-phraseology.md) |
| Logging | [logging.md](logging.md) |
| Test harness & fixtures | [test-harness.md](test-harness.md) |
| Test class almanac (shape → folder → what it pins → harness → placement rule) | [test-map.md](test-map.md) |

## Integration Footguns

- **Modify `AircraftState`** → must mirror changes in `AircraftSnapshotDto.cs` + add migration in `SnapshotSchemaMigrator.cs`
- **New command type** → must add to `CanonicalCommandType` enum, `CommandRegistry` definitions, AND `CommandScheme.Default()`. Tests enforce completeness.
- **New phase** → must add `[JsonDerivedType]` attribute in `PhaseSnapshotDto.cs` for serialization
- **Modify `ControlTargets`** → check `ControlTargetsDto.cs` snapshot parity
- **Aircraft performance** → sync `AircraftProfiles.json` + `AircraftProfileDatabase` + `AircraftPerformance.cs` fallback logic; per-type corrections go in `AircraftProfileOverrides.json` (see `docs/aircraft-performance.md`)
- **New hub call that replaces or clears a room's scenario** → refuse it while `TrainingRoom.LoadingBy` is set, before the tick gate and again inside it (`TrainingHub.ThrowIfLoadingScenario` / `UnlessLoadingScenario`), and gate its client command on `MainViewModel.IsRoomLoading` (see `docs/server-rooms-and-hub.md` § Scenario load)
- **New reload or reconstruction path** → read the room's `ResourcePin` (ARTCC configs and layouts), never `ArtccConfigService` / `AirportGroundDataService` directly, or a rewind stops reproducing the live run after vNAS changes a map or config
- **The one compile edge from yaat to yaat-server** is `tools/Yaat.GuideCapture`'s conditional `ProjectReference` to `../yaat-server/src/Yaat.Server` (`HAS_YAAT_SERVER`), which CI never sees but which gives prek's `yaat.slnx` build hook its cross-repo deadlock and the partial-commit stash rule.

  Decided route to break it: move GuideCapture's in-process server host into a yaat-server tool, which then builds against yaat through the existing server → Sim reference; driving a server process over HTTP/SignalR was rejected. The two WASM projects' post-publish `wwwroot` copy is a file copy, not a compile edge. Once the edge is gone, the deadlock and stash rules go with it.
- **New scenario-JSON field that names an airport or ARTCC** → add it to `ScenarioResourceManifest` as well as the loader, or the server's load never prefetches it (`ScenarioResourceManifestTests.Corpus_LoaderAsksForNoAirportOutsideTheManifest` catches the airport half)

## Test Locations

> Before adding a test, open [`docs/test-map.md`](./test-map.md): it maps every *class* of test (shape → folder → what it pins → harness → placement rule) by subsystem, so you can tell whether a behaviour is already pinned and where the new test belongs. The list below is the flagship-file index; the map is the almanac.

- **Sim tests**: `tests/Yaat.Sim.Tests/` — commands, phases, physics, parsers, nav data
  - **Filed speed text**: `AircraftFlightPlanSpeedFieldTests.cs` (`FormatSpeedField`: SC wins over a Mach number, Mach is M plus three digits of hundredths, knots are bare digits, no speed is empty)
  - **Pathfinding**: `Pathfinding/FilletCornerRoutingTests.cs` (fillet arc vs square-pivot routing), `Pathfinding/SfoDetourHonorsNamedTaxiwayTests.cs` (gate G3 `TAXI A Q B F 28L HS 1L`: the parking-node A→Q bridge must still reach A — pins the honour-named-taxiway behaviour any detour re-ranking has to keep), `Pathfinding/AutoRouterPruningTests.cs` (A* pruning via arrival taxiway), `Pathfinding/ArcRadiusFloorAdmissibilityTests.cs` (steerable radius floor)
  - **Fillet arc speed**: `Fillet/GroundArcSpeedProfileTests.cs` (local-curvature arc speed). `GroundNavigatorArcSpeedProfileTests.cs` (navigator speed profile application). `GroundNavigatorStraightHandoffTests.cs` (straight-after-arc heading release).

    `Simulation/GroundTaxi/NodeAimedEntryOntoFilletTests.cs` (a TAXI issued inside the turning diameter of a fillet's near end, facing away from it — SFO gate E2 SKW5707, `5d33df162626.zip` — hands the entry-alignment arc over on the straight line to the fillet's far end instead of its Bézier, never teleports, round-trips a mid-line snapshot, and is not held to the fillet's cornering speed since the straight does not corner).

    `Simulation/GroundTaxi/AimedPastOntoHoldShortBarTests.cs` (an entry-alignment arc aimed past onto a hold-short junction node retires the leg and re-aims the navigator's internally-moved target onto the junction's painted bar, not the junction itself, bypassing `TaxiingPhase`'s own segment set-up)
  - **Ground taxi**: `Simulation/GroundTaxi/OakUwFilletCornerTests.cs` (bundle replay: fillet routing + speed profiles). `Simulation/GroundTaxi/HoldOnCurveTests.cs` (a `HOLD` landing mid-fillet keeps the navigator ticking, so `RES` does not write the aircraft backwards onto the stale playback pose — the hold contract in [`ground/navigator.md`](./ground/navigator.md)).

    `GroundNavigatorRouteEndSpeedTests.cs` (`RouteEndSpeedKts`: a taxi whose destination-runway bar is already covered by a stored clearance arrives rolling, an uncleared one still arrives stopped; plus the two runway-incursion guards — a heavy reaches 0 kt before the roll per 7110.65 §3-9-6.c, and a `CTOC` inside the bar does not cross it).

    `Simulation/SfoCtoIntersectionDepartureE2ETests.cs` (the SFO taxiway-E intersection departure reaches the takeoff roll within the pinned seconds of the clearance and never parks on the runway side of the holding position).

    `Simulation/GroundTaxi/OakSimplePushThenTaxiApproachTests.cs` (unit-level: a PUSH-then-TAXI route starting behind the aircraft is driven to via a free-space leg, not teleported onto). `Simulation/GroundTaxi/OakSimplePushThenTaxiApproachReplayTests.cs` (E2E replay of the same PUSH/TAXI session off a recording).

    `Simulation/GroundTaxi/TaxiApproachLegTests.cs` (`TaxiApproachLeg.Prepend` unit cover: the four refusal shapes plus the along-runway-roll case). `VirtualNodeTests.cs` (position-hashed virtual-node ids: same position → same id, a foot apart → a different one, cross-process determinism).

    `Phases/CrossingRunwayTailClearTests.cs` (the tail-clearance leg past a runway crossing follows the aircraft's own route rather than the graph's straightest continuation, and the phase hands the route back at the right segment; a type missing from the FAA database clears by half its CWT fallback length).

    `Phases/ClearRunwayPhaseFallbackLengthTests.cs` (`CLRWY`'s pull-forward past the hold bar uses the same CWT fallback length for an unknown type). `Simulation/GroundTaxi/SfoVideoRoutePinTests.cs` (the SFO ground-controller routes from the ZOA familiarization video and SOP — 28/1, 28/28, 19/10, 19/19, 10/10, supers — resolve with exactly the hold-shorts each clearance implies; start points by gate/spot/bar/junction name).

    `Helpers/SfoGroundHarness.cs` (engine + SFO layout builder, name-resolved spawns at parking/spot/hold-short/junction, `TickUntil`, order-preserving hold-short assertion with route dump, `DeadlockGuard` for multi-aircraft choreography).

    `Simulation/SfoBravoToOneLeftConnectorTests.cs` (`TAXI A F1 B 1L` / `C Z B 1L` thread the M1 stub to the 1L bar with the `via M1` note, the engine drives it to the hold; 1R / 28L off B still refuse with the same message; the connector finder's accept/reject cases).

    `Simulation/GroundTaxi/SfoSimultaneousAlleyPushTests.cs` (two tugs pushing into adjacent alley lanes — SFO 5A/5B, SOP 3-5.c.i — both complete with at most a brief in-line yield; a pusher aimed dead at another aircraft still stops while an abeam one clears). `Simulation/GroundTaxi/SfoDepartureFunnelTests.cs` (the 28/28 west-plan departure funnel: four B738s stage at F1-on-1R, A1 and the 1R bar, release 3-1-4-2 with RES/FOLLOWG/CROSS, end in a 28L line ranked 1..4 with nobody on 28L).

    `Phases/Ground/FollowingPhaseHoldShortTests.cs` (a follower reaching its own destination bar holds as a departure, and does not stop at the far-side bar of a runway it is leaving).

    `Pathfinding/ForcedPushLegPlannerTests.cs` (`/PUSH`/`/PULL` legs: only the forced kind after the stand push-off, a `/PUSH` onto a spot ends on the stop, a forced leg with no flyable plan is refused, a pull whose 30 ft tug lead ends on a taxiway is refused, marked points plan onto the pose or are refused on a taxiway, runway or holding position).

    `Commands/PushLegKindAndFreePoseParseTests.cs` (the suffix and `~lat/lon[/facing]` grammar, examples and non-examples, NaN/Infinity refused). `Simulation/GroundTaxi/MarkedPointPushTests.cs` (a marked-point tow flown through the handler ends on the point; a snapshot mid-tow restores to the same end pose and keeps `KeepsItsPlan`; a mid-tow `FACE` is refused as keeping its plan).

    `Phases/Ground/PushbackMoveBoundaryTests.cs` (SFO E6 `PUSH $6B`, a three-move plan: the tug holds 5 kt through the straight→turn boundary, still stops and dwells before the reversal pull, completes on 6B, and a taxi clearance that cuts a continuing move stops the aircraft dead).

    In the same plan every second's speed change stays within `CategoryPerformance.TugAccelRate`/`TugDecelRate`, the tug holds push speed through the turn instead of easing onto a wingtip cap ahead of it (`HoldsPushSpeedThroughTheTurn`), and every stop is reached at the `FinalApproachKts` crawl first.

    `Pathfinding/TugAlleyClearanceTests.cs` (a push kept off the movement area — a spot, stand or node goal — stays outside a taxiway's Airplane Design Group object-free half-width plus a 5 ft margin: SFO taxiway A is ADG IV at 129.5 ft off a 218 ft parallel to Y).

    The airport's own runway-width bucket shares `AirplaneDesignGroups.FromRunwayWidth`'s buckets with the hold-short wingtip floor, F8→7A and E12→7B stay clear via the straight-then-line push, and D7 `PUSH A F1` — sent onto A — is not held to it.

    `Simulation/GroundTaxi/PushTowSpeedTests.cs` (SFO E1 `PUSH $7` flown end to end through `PushbackPhase` and physics: the tug holds `PushbackSpeed` through turns while the main gear slows by the cosine of the nose-gear steer angle, a creep move only slows to `PushbackAlignSpeed` over its last `AlignCreepFt` = 30 ft, and the final pull onto the lane turns onto the lane heading without passing it).

    `Simulation/GroundTaxi/PushReadbackNotesTests.cs` (a `TugPlan`'s warnings — a long push, a far facing junction, a taxiway foul — reach the RPO as parentheticals on the push/`PUSHM`/`FACE`-amendment readback and never the pilot's spoken readback, which is verbalized from the command).

    `Simulation/GroundTaxi/SfoSixAlleyChoreographyTests.cs` (pusher from D15 into 6A/6B vs an arrival taxiing the other alley lane: never slowed on the far lane to D16; on T6A to gate E9 a moving pusher abreast at ~140 ft costs a bounded hold via the trail limit and a parked one costs nothing — a D-pier push into T6B never enters T6A).

    `Simulation/GroundTaxi/SfoYankeePushTests.cs` (B12: plain `PUSH Y` ends on Y with the nose on the stand heading; a B752 `PUSH A` control).

    `Simulation/GroundTaxi/SfoYankeeTaxiOutPinTests.cs` and `Pathfinding/SfoYankeeConnectorChoiceTests.cs` (after `PUSH Y A1`, `TAXI Y A A1 1R` leaves Y through AY3 ahead of the nose, not AY2 behind it: the connector detour ranks candidate bridges by pavement cost plus a reversal charge against the aircraft's pose — the resolver-level pin and the flown taxi-out).

    `Simulation/GroundTaxi/SfoSixAlleyArrivalAheadOfPushTests.cs` (an E6 push to 6B against an arrival taxiing T6A to E9: the arrival is through the alley and parked while the push is still on its first leg, the push completes to 6B afterwards, and the two never close inside 90 ft — the former F-6 wedge pin, now a regression guard against a re-introduced hold).

    `Simulation/Ual58Spot9ReversalTests.cs` (SFO bundle replay of the "UAL58 did a loop" report: after `TAXI A F 28L` off spot 9's T9/A junction, the entry-alignment reversal and the turn onto A must cancel into a net ≤180°/peak ≤210° rotation that gets established on A's 27.6° bearing rather than compound into one ~270° sweep; a second case pins the free-space arc leaving gate G10 against overshooting the 21 ft ramp leg before it settles on spot 9).

    `Simulation/GroundTaxi/ForcedPushTests.cs` (`PUSHF`/`PUSHMF`: parses and describes back to itself, keeps every part of the plain push/tug-move grammar).

    A plain push a parked neighbour or an unnamed taxiway would refuse suggests the forced form, which the neighbour-ranking step keeps the most room to (`TugNeighbourClearance.Shortlist`) and the detector never brakes for. Forcing keeps the runway, holding-position, sanity-guard and start-overlap refusals — with no suggestion — and every `PUSHM` pass-through hint.

    `AircraftGroundOps.ForcedTowIgnoresParked` is set on install and a forced/plain amendment, cleared on completion or a command that clears the tow, and round-trips a snapshot. Each rule a forced plan overrides is noted once on the readback (`TugPlanBuilder.AddOverride` keeps a neighbour at its closest pass).

    A faced goal no template lines up on takes the geometric fallback onto the spot. `Simulation/GroundTaxi/SfoPushOntoATests.cs` (SFO D11, E13T, E13K, F10, C10, C11: a bare `PUSH A` takes the documented Across rule and ends with the aircraft's centre on A on the stand heading; `PUSHF A` installs the identical plan since nothing is about to force past).

    `Simulation/GroundTaxi/PushmHintE2ETests.cs` (`PUSHM`'s pass-through hints flown end to end on the real SFO ramp beyond the F8 reference case: a hint no arrival passes taken through on a move of its own, and a hint that is a graph node rather than a spot). `Simulation/GroundTaxi/FollowGroundBarStopTests.cs` (the KOAK H1 clip: a `FOLLOWG` follower brakes into the runway 33 bar on C at the piston taxi brake rate, rests with its nose at the hold line, and reports holding short).

    `Simulation/GroundTaxi/GiveWayStopBrakingTests.cs` (the KOAK H2 clip: a `GIVEWAY` keeps taxiing and brakes at the taxi rate to a stop with its centre clear of the crossing track by wingtip clearance, the traffic crosses unstopped and the hold releases; told inside its taxi-rate stopping distance it brakes at no more than the max-effort rate and never passes the point).

    `Simulation/GroundTaxi/GroundNavigatorArcRestoreTests.cs` (a snapshot taken mid entry-alignment turn restores onto the original's positions every second; a saved from-node that does not match the segment drops the playback with a warning).

    `Simulation/GroundTaxi/CrossingRunwayRestoreTests.cs` (a crossing restored mid-slice, or snapshotted again before its first tick, stays on the original's slice segment and path). `Simulation/GroundTaxi/KoakFollowClip.cs` (helper: loads the KOAK clip scenario and replays a `script.txt` one sim-second at a time)
  - **Commands**: `Commands/TrackResolverTests.cs` (the one TCP→TrackOwner chain against the real ZOA config — including the STARS interfacility handoff-code leg — and `ResolveIdentity` precedence: AS override → AI connection → selection → student; the position-callsign and `callsign@tcp` fallbacks; `PositionSelections` snapshot/restore).

    `Commands/CoordinationCanonicalRoundTripTests.cs` + `Commands/ConsolidationCanonicalRoundTripTests.cs` (describe∘parse is the identity for every coordination / consolidation shape the router records — `RDH {list} {text}`, `RDTXT /{list} {text}`, `CON+ {recv} {send}`).

    `Commands/EramEntryEngineHoldTests.cs` (`ParseHoldFields` accepts a known field 21 location and refuses one that does not resolve; `ParseRecordedHoldFields` trusts the recorded location). `Data/EramFixResolverTests.cs` (`ParseLocation` resolves a fix, a fix radial distance and lat/long, and refuses anything else).

    `Commands/EramEntryEngineTests.cs` (every ERAM entry form the engine applies: the TRACK owner guard and its /OK override, FREEZE, the QQ tiers, QR, the QS heading / speed / free-text canonical forms and delete forms, LF, the HM / QH hold forms — new, present-position, EFC-only edit, holding instructions, cancel; an unknown entry refused).

    `Commands/RunwayAndAltitudeArgumentTests.cs` (RunwayArgument/AltitudeArgument accept-sets are disjoint over representative tokens; each validator's accepted shapes, normalized). `Commands/PatternModifierArgumentGrammarTests.cs` (the pattern modifier's `[runway] [altitude]` tail told apart by shape through CommandArgumentResolver — a bare two-digit token is a runway, three-plus digits is an altitude — including the failure wording naming the argument that followed)
  - **Replay**: `Simulation/ReplayGeneratorStandDownTests.cs` (generators stand down unconditionally in replay/playback). `Simulation/RunProfileTests.cs` (run profile kinds + mode flags). `Simulation/FlightPlanCommandReplayKeepsPhaseTests.cs` (flight-plan commands skip replay, preserving phase continuity).

    `Simulation/Actions/ActionRouterIdentityIsolationTests.cs` (a recorded `AS` under an AI connection id lands in the engine's `PositionSelections` but never displaces the AI position's own identity; the selections survive a snapshot round trip and a pre-feature snapshot restores them empty).

    `Simulation/Actions/RecordedActionHostRoutingTests.cs` (a recorded ASDE-X / SAID mutation reaches the host's slot, a weather change and a live-traffic removal reach the host's consumers, and a never-recorded kind — `PAUSE`, `BM` — is inert from a record without the host being asked)
  - **Action routing** (tick-path step 3d): `Simulation/Actions/FlightPlanAndQueryArmTests.cs` (the arms the live chain handed to Yaat.Sim: FP/DA/RMK file through the engine, record the amendment and tag the creator — audit + tag from a record; a bare APT is an aviation command recorded as text with its procedure clear; SHOWAT goes to the host and is never recorded; a pure ghost's DROP leaves the world; INHCA drops the aircraft's conflicts).

    `Simulation/Actions/ActionRouterTests.cs` (one routing for fresh and recorded commands — a global command applies with an empty callsign, an aircraft-scoped one refuses identically on both entry points, the reaction delay is sampled and baked on issue, read-backs and the frequency gate are produced on replay when someone answers pilots).

    Every fresh command is recorded accepted or not and a recorded one never is, a recorded verdict that differs logs the replay-fidelity warning, legacy transport records stay inert, a scoped-special compound routes and records per unit, a chain with a non-compoundable verb is refused.

    `Simulation/Actions/AddAndTaxiAllArmTests.cs` (the ADD arm spawns and bakes its snapshot onto the record; a recorded ADD without a snapshot derives the same aircraft with the shared RNG and beacon pool in lockstep; a baked snapshot wins over a disagreeing derivation — or a failed one — reserves its beacon and logs the replay-fidelity warning; TAXIALL taxis every aircraft at parking).

    `Simulation/Actions/TrackFamilyArmTests.cs` (ACCEPTALL accepts the handoffs offered to the resolved identity and HOALL offers every owned track to the TCP, both refused without an identity; GHOST creates a phantom staggered 0.1 nm per ghost off the runway threshold or overlays an existing aircraft unless another position owns it; RPOSLOC parks and RPOSMOVE re-associates the datablock; CAACK acknowledges the aircraft's conflict alerts on the engine).

    `Commands/ConsolidationRedirectTests.cs` (the Track arm on the real NCT hierarchy: a handoff to an unattended TCP combined into an attended one lands there with `HandoffRedirectedBy` set, does not redirect when nobody is attended, a recipient re-addressing an inbound handoff re-points it, a point-out lands on the attended owner).

    `Simulation/Actions/ConsolidationArmTests.cs` (against the real NCT hierarchy: CON records a basic override and leaves tracks alone; CON+ moves the sender's block — tracks transfer, handoffs redirect — except an attended descendant's, with a host stub answering attendance; an unknown position and a loop are refused with nothing written; DECON removes the override).

    `Simulation/Actions/ActionRoutingCompletenessTests.cs` (every `ParsedCommand` subtype classifies to one `RecordedCommandKind` with an `ActionScope`, every kind has a scope and an `ArmTable` row, the never-recorded set is exactly bookmarks + transport, and every type in `IsAviationCommand` reaches a dispatcher arm — `PhaseGatedArms` names the probe's blind spot exactly).

    `Simulation/Actions/RecordingCorpusRoutingCensusTests.cs` (how every committed recording's commands classify today, asserted byte-for-byte against `TestData/recording-routing-census.json`; regenerate with `YAAT_ROUTING_CENSUS_REGENERATE=1`). `Helpers/ParsedCommandDummies.cs` (a placeholder instance of every concrete `ParsedCommand` subtype for table sweeps). `Helpers/AttendanceActionHost.cs` (an `IActionHost` with no room whose CRC attendance answer the test controls).

    `Simulation/Actions/RecordedStateChangeTests.cs` (the derived records apply through the router on the bare engine — shared state clears the dismissed point-out, clearance and hold annotation replace whole, an ERAM entry resolves its identity code, a CRR group applies to the engine and notifies the host, a safety-logic push writes the scenario's config, a strip request prints in the engine under the recorded id).

    A record for a missing aircraft — or a strip request the engine refuses — is refused with a replay-fidelity warning; IssueDerived records only what applied
  - **Spine**: `Simulation/AutoTrackStepTests.cs` (ApplyAutoTrackConditions on a loaded aircraft owns it and queues the student handoff; a generator spawn with an autotrack configuration is owned before its spawn record; a departure crossing the display floor is claimed by the autoTrackAirportIds position; an aircraft squawking its assigned code flips to the FP creator).

    `Simulation/ReplayAtcRosterTests.cs` (the resolved ATC roster round-trips a snapshot without aliasing the DTO's lists; a snapshot without the field leaves the loader's roster; a replay restores snapshot 0's roster and auto-tracks a departure crossing the display floor, while a recording without it replays unowned).

    `Simulation/TrackAutomationStepTests.cs` (delayed handoffs, auto-accept and the point-out timeout on the bare engine: a pending point-out to an unattended recipient is withdrawn at 30 s and not before, the student's own waits in solo mode by TCP or by position, an attended recipient is untouched, auto-accept off leaves point-outs pending).

    `Simulation/Actions/AutoTrackChangeRecordTests.cs` (a `RecordedAutoTrackChange` round-trips the polymorphic serializer; issued derived it creates the roster entry from the ARTCC config, steals the airport, claims an airborne untracked departure; `-X` removes globally and `none` clears the caller only; an unresolvable position is refused and not logged; `ApplyRecorded` reproduces the roster).

    `Simulation/SpineTraceTests.cs` (the literal step sequence of a bare second, sub-tick ×4 = whole second by digest and snapshot, per-second counts). `Simulation/PostPhysicsDrainOrderTests.cs` (drain order through the buffers)
  - **Follow/Pattern**: `Simulation/FollowStraightInJetBaseTurnTests.cs` (S2-OAK-5 replay: a C172 turns base behind a straight-in LJ60 by projected threshold ETA, not at its touchdown). `Simulation/N342TFollowStraightInDownwindTests.cs` (DA42 behind a straight-in C25C: no cut-in, runway clear at arrival). `Simulation/FollowPatternSequencingAuditTests.cs` (synthetic KOAK circuits: leg holds behind an extending lead).

    `AirborneFollowTests.cs` (spacing math, sequencing gate, pursuit heading). `FlownRunwayResolutionTests.cs` (runway resolution across pattern changes). `Simulation/OptionClearancePatternModifierTests.cs` (option clearance pattern modifiers and routing). `Simulation/OnTheGoConditionTests.cs` (OTG condition fires after next option or go-around; a queued cycle-terminator block is discarded with a cancellation warning if the aircraft lands full stop instead).

    `Simulation/ParallelRunwayMltFromUpwindTests.cs` (MLT from upwind on parallel runways). `Simulation/ParallelRunwayMltFromDownwindTests.cs` (MLT from downwind on parallel runways). `Simulation/RunwayTransitionCircuitTeardropTests.cs` (BuildRunwayTransitionCircuit's crossing arm joins through a MidfieldCrossingPhase and a jet/turboprop descends through a TeardropReentryPhase before the downwind; the parallel arm never crosses).

    `Simulation/TeardropReentryEntryHeightTests.cs` (a wrong-side turbine join gets a teardrop only when its crossing is above TPA: none at an unauthored field, where it rejoins the downwind track; one at an authored-low field; the teardrop never commands a climb above the crossing altitude, and inside the cap band its anchor sits at the crossing altitude).

    `FollowArmedPatternRunwayTests.cs` (a FOLLOW pattern-join or lead-landed final-sequence rebuild carries over an armed PatternRunway instead of cancelling it, and satisfies the arming when the follower joins that runway itself).

    `Simulation/BaseFollowSpacingTests.cs` (C172/BE20 pairs at OAK, follower on base behind a lead on final or base: keep when well behind, widen 30° away from the field and roll out behind on the 3° glidepath, a default piston circuit widens rather than turning out, a widen that can no longer build the gap breaks off mid-widen).

    A break-off turns out to the downwind with exactly one call and later lands behind — even beyond the 5 nm range, and from a lead on base ahead without a join — a lead on an ILS in `ApproachNavigationPhase` is in scope, a late break-off near the centerline goes around, a structural overtake still goes around; the widen-gain arithmetic, the parallel-final gate and the `FollowWidenActive` snapshot round-trip.

    `Simulation/FollowTurnOutTests.cs` (the pursuit turn-out: triggered level or ahead and by a 20 s stalled parallel hold at the offset cap, one call, the 2 nm / 6 nm distance limit holding the downwind heading without a second call, a lead going around ending it into a downwind entry without a call, a landed lead with the follower beyond the threshold re-entering by the upwind and never joining the final).

    Unit cover of the shortest-path, final-frame, reversal-turn, offset-band, exit, distance-limit and stall-window helpers; snapshot round-trips of the turn-out and the stall window
  - **Helicopter**: `Simulation/HelicopterLandSpotFromDistanceTests.cs` (S2-OAK-5 replay: an R22 told LAND @SIG1 from 9 nm holds 500 ft until the 6° final instead of diving to air-taxi height), `Simulation/HelicopterLandGateTests.cs` (the on-field gate: on/over the field → AirTaxi chain, off-field LAND → HelicopterApproachPhase, off-field ATXI refused; TOD / final-start math)
  - **Final-approach speed window datum**: `FinalApproachLandingDatumTests.cs` (KSJC 30L, 2,537 ft displaced: `FlightPhysics.AutoCancelSpeedAtFinal` and `SPD`'s 5 nm-final rejection measure the §5-7-1.b.4 window from the landing threshold, `ApproachCommandHandler.IsOnFinal` takes its bearing from it — an aircraft over the displaced stretch is on final — and a null layout falls back to the pavement threshold)
  - **Route geometry guards**: `Helpers/RouteGeometryAsserts.cs` (structural: no square pivot where fillet exists)
  - **Attendance**: `Helpers/AttendanceTestSupport.cs` (test helpers for CRC attendance state), `Simulation/Actions/AttendanceRecordTests.cs` (a RecordedAttendanceChange replaces the engine's set, resolves ids through the room's config, keeps an unresolvable id by id only, round-trips the snapshot and the serializer; a fresh replay starts empty)
  - **Strip id baking**: `Simulation/Actions/StripIdBakedDrawTests.cs` (the strip id a creating verb — SEP/HSC/SCAN — draws is a baked draw like the reaction delay and the generated aircraft: `RecordedCommand.StripId` round-trips the archive serializer, `BakedDraws.Of` carries it, and the strip arm bakes the id the verb minted onto the record)
  - **Strip steps**: `Simulation/Strips/StripStepTests.cs` (the flight-strip bodies on the bare engine: the spawn hook's auto-print, the approach student's takeoff-roll print, the creating verbs behind `SEP`/`HSC`, the deferred strip dispatch the engine applies itself, and the change tracker the router and the post-physics drain step hand to the host — over real ZOA data with no server in the process)
  - **Tower lists**: `Simulation/TowerLists/TowerListStepTests.cs` (the P-list step on the bare engine over real ZOA data: an in-range aircraft enters with the tick's second, the coordination flag is raised by the spine step, a static second re-stamps nothing and reports no change, a snapshot restore keeps the live dwell second).

    `Simulation/Snapshots/TowerListSnapshotMapperTests.cs` (round-trip incl. entry order, null → empty, ClearSession keeps the airports). `TowerListTrackerTests.cs` (the tracker's range geometry — moved from the server suite)
  - **Bookmarks**: `Simulation/Bookmarks/BookmarkStepTests.cs` (the bookmark bodies on the bare engine: `BM ADD`/`RENAME`/`DELETE`/`DEL ALL`, one `OnBookmarksChanged` per successful mutation and none for a refused one, a recorded `BM ADD` inert)
  - **Session clock**: `Simulation/TransportStepTests.cs` (`PAUSE`/`UNPAUSE`/`SIMRATE` on the bare engine: the clamp to 1..16, the live-traffic refusal, `(false, "No active scenario")`, one `OnSimStateChanged` per accepted verb, a recorded `PAUSE` inert)
  - **ASDE-X / SAID mutations**: `Simulation/Asdex/AsdexMutationStepTests.cs` (a recorded tag / edit / suspend / inhibit / terminate on the bare engine and the SAID twins, the terminate consumer once, an edit carrying an empty field stored as written, `ASDXALERTS` clearing two inhibits, a `ReplayDriver` replay holding the tag)
  - **ASDE-X safety-logic configuration**: `Simulation/Asdex/AsdexSafetyLogicConfigStepTests.cs` (a recorded push writes the scenario's config on the bare engine and is a no-op with no scenario loaded, the config round-trips through a snapshot and restores as null from a snapshot without one, the record round-trips through recording JSON, a scenario unload clears it)
  - **ASDE-X alert step**: `Simulation/Asdex/AsdexAlertStepTests.cs` (on the bare engine a closed runway with an aircraft on it raises the alert and hands the consumer the new alert, the conflict ending clears it and hands over its id, a reader holding the standing set is not disturbed by a later tick, no configuration raises nothing, the set round-trips through a snapshot and restores as empty from a snapshot without one, a scenario unload clears it, the alert order is the same across runs)
  - **ERAM CRR groups**: `Simulation/Eram/EramCrrGroupStepTests.cs` (a recorded create / replace / recolor / null-latitude delete on the bare engine, one `OnEramCrrGroupsChanged` per applied record, an unknown colour, the snapshot round-trip, a `ReplayDriver` replay rebuilding the group, the Sim-vs-wire colour numbering)
  - **ERAM conflict-alert settings**: `Simulation/Eram/EramRoomSettingsStepTests.cs` (a recorded CA function entry sets the facility's function and tells the host once; a sector-display entry is absolute, never a toggle, and a facility back at the defaults is dropped; malformed entries, including a display entry naming a literal ALL, change nothing).

    `ShowsConflict` per function, per sector and for MCI pairs, and shows for an unknown facility; the snapshot round-trip, a restore with no section resetting to the defaults, a `ReplayDriver` replay from zero rebuilding the settings; concurrent readers never throw while another thread applies entries
  - **ERAM sector messages**: `Simulation/Eram/EramSectorMessagesStepTests.cs` (a recorded `SM {sector} {text}` stores per facility and sector without touching the conflict-alert settings or telling the host; a second one overwrites; `SMDE {sector}` deletes, and deleting a sector holding none applies and changes nothing).

    Malformed entries — missing sector or text, `ALL`, padded text, lower-case verb, another verb — fail and leave the stored messages alone; the snapshot round-trip in facility then sector order, a snapshot with no section restoring none; a `ReplayDriver` replay from zero rebuilding the messages
  - **Coordination**: `Simulation/Coordination/CoordinationStepTests.cs` (the coordination bodies on the bare engine over the real ZOA `POAK` list: `RD`/`RDH`/`RDR`/`RDACK`/`RDDEL`/`RDPOS`/`RDTXT`/`RDAUTO`, the deterministic `{ListId}-{SequenceNumber}` id, the timers step reached through `RunSecond` and its dirty flag delivered by the `StateChanges` spine step, a `TRACK` voiding the items, a non-sender refused, and a `ReplayDriver` replay holding the same item id as live)
  - **TDLS**: `Simulation/Tdls/TdlsStepTests.cs` (the vTDLS bodies on the bare engine: the spawn hook's auto-queue, the four tick steps — auto-queue, auto-WILCO, TTL expiry, track removal — the `TDLSQ`/`TDLSS`/`TDLSW`/`TDLSDUMP`/`TDLSOPS` command handler, and the change tracker the router drains into the host — over real OAK navdata with no server in the process)
  - **Departure auto-delete**: `DepartureAutoDeleteTests.cs` (the session's `DepartureAutoDeleteDistanceNm` on the bare engine over real OAK navdata: a ground-spawned departure past the distance is removed with the arrival mode unset or `Never`, tracked or not, and stamped `Departed` with a debrief row; one inside it, an arrival, a non-primary departure, a local flight back to the primary airport and a live-traffic shadow stay).

    Bare `NODEL` and `TAXI … NODEL` keep it while `DEL` still removes it; the distance and the `NODEL` flag survive a snapshot restore; the recorded setting change replays onto the scenario
  - **Scenario resource manifest**: `Scenarios/ScenarioResourceManifestTests.cs` (the ARTCC, neighbour ARTCCs and airports a scenario JSON names, `MapRequiredAirportIds` — primary, `Parking` and ground-spawn airports only — and `Corpus_LoaderAsksForNoAirportOutsideTheManifest`, the loader/manifest drift guard over every example and test-data scenario)
  - **Scenario export**: `Scenarios/ScenarioExporterTests.cs` (`ScenarioExporter` against real navdata/layouts: a parked aircraft exports `Parking` and a shadow within 75 ft of a stand does too, one taxiing past the stand node or holding short exports `Coordinates` flagged; a lined-up-and-waiting aircraft exports `OnRunway`).

    A simulated final and a geometrically-detected shadow final export `OnFinal` with the loader's own distance metric, gated by destination match, the 0.5 nm over-threshold cutoff and the glidepath/climb-rate window.

    An en-route IFR aircraft keeps its remaining route, a vectored one is flagged off-route, a shadow's filed route is trimmed to the fixes ahead or flagged untrimmed; a descend/climb-via or an assigned altitude/speed round-trips as a preset command through the real parser. A shadow's heading and IAS come from its air vector, not ground track/speed; the loader-reload round trip lands every aircraft within 20 ft and 1° of heading
- **Client tests**: `tests/Yaat.Client.Tests/` — view model logic, command input
  - **Hub JSON contract**: `HubJsonContractTests.cs` (every Core-owned `ServerConnection` return type resolves through `YaatHubJsonContext`; the `ScenarioLoadProgress` payload DTOs do too, and the server's load shapes — `Steps`, `LoadingBy` — deserialize into the client records)
  - **Ground overlay**: `GroundViewModelApproachLegOverlayTests.cs` (the client reconstructs the server's free-space approach leg so the drawn route starts at the aircraft, not the route's first graph node). `Views/GroundRendererRouteDrawTests.cs` (a route segment's screen endpoints fall back to its own node references when a virtual-node endpoint isn't in the layout's node table).

    `GroundRendererTugTests.cs` (renders `DrawAircraft` to an offscreen bitmap: the tug body paints only when `AircraftModel.TowbarHeading` is set, lands on the towbar side of the nose axis, and is skipped once it would render under `GroundRenderer.MinTugPx`). `AircraftModelTowbarHeadingTests.cs` (`AircraftModel.TowbarHeading` follows `AircraftDto.TowbarTrueHeadingDeg` on both `FromDto` and `UpdateFromDto`, including back to null when the tug detaches)
  - **Cruise speed display**: `AircraftModelCruiseSpeedDisplayTests.cs` (a Mach or classified plan shows M/SC in the summary and cruise display, a knots plan keeps its suffix, no speed shows the altitude alone, the editor SPD box is empty with the Mach as placeholder for a Mach plan, `FromDto`/`UpdateFromDto` carry both fields)
  - **Favorites sharing**: `FavoriteExportTests.cs` (set/library zip round-trips, merge vs replace, the #437 user library under `TestData/favorites/` — 452 favorites merged into Global)
  - **Discord Rich Presence**: `DiscordIpcFrameTests.cs` (frame round-trip, the little-endian header, oversize / negative length refused, a peer closing mid-frame).

    `DiscordRichPresenceServiceTests.cs` (the worker against a scripted Discord over an in-memory duplex stream and a manual clock: handshake then `SET_ACTIVITY`, latest-wins, clear closes the connection, back-off while Discord is absent, ping → pong, reconnect after a server close, prompt `Dispose` under a hanging connect, handshake timeout, a clear during the handshake, the 128-char clamp)
  - **Speech telemetry**: `SpeechTelemetryUploaderTests.cs` (each pending sample POSTed oldest first and its marker cleared; a server or transport failure stops the pass and leaves samples pending; a rejected sample is dropped and the rest upload; telemetry off, no server URL, no token, or a sample captured without queueForUpload makes no request).

    `SpeechSampleStoreTests.cs` (upload-pending markers, WriteBundle). `TelemetryTranscriptRegressionTests.cs` (every promoted transcript in `TestData/speech-transcripts/telemetry-regressions.json` maps to its canonical, and the harness fails when a label is wrong)
  - **Bug report filing**: `BugReportIssueBuilderTests.cs` (`BuildUrl`: GitHub new-issue query, title/body round-trip special characters, body carries form + bundle name + environment, not-in-a-room and no-attachment wording, callsigns and expected sections, over-long text truncated to the URL ceiling with a marker and never through a multi-byte escape)
  - **Speech ouroboros**: `AtcOuroborosTests.cs` (every `SynthTemplates` template verifies through the rule mapper or is a known gap, unique keys, three variants per family, deterministic per-seed sampling, unverifiable templates reported and never sampled, per-family/template/total aggregation, baseline diff classification and exit codes), `EvalRunnerSessionStubTests.cs` (an expected.json stub built from a store-written session carries canonical, transcript and callsigns)
- **UI tests**: `tests/Yaat.Client.UI.Tests/` — headless window tests for views and layout. `Views/MessageBoxDialogTests.cs` drives a custom and a standard MsBox dialog to a button click so a MessageBox.Avalonia pin that does not load against the shipped Avalonia (the 3.x line under Avalonia 12, GitHub #437) fails there instead of in a user's dialog.

  `Automation/AutomationWaitUntilTests.cs` (the pipe's `wait_until` on a stub `IAutomationState`: each condition kind holds and reports its last value, `mode: all`, a timeout answers met=false rather than an error, an absent callsign reads `absent`).

  `log_matches` sees only lines added after the call (also across a log trim) and skips `IsHistory` lines, bad kinds, regexes and rates are `INVALID_PARAM`, the screenshot is taken at the matching poll, `then` actions run in order on a match and not on a timeout
  - **Discord Rich Presence**: `ViewModels/MainViewModelRichPresenceTests.cs` (a recording `IRichPresencePublisher` fake on `MainViewModel.RichPresence`: a join publishes name + `ARTCC · airport` back-dated by the room's elapsed seconds, a recording load publishes its own scenario, a late scenario name republishes, no scenario / preference off publishes nothing, leaving clears, the state line omits the missing half).

    `UserPreferencesDiscordRichPresenceTests.cs` (`DiscordRichPresenceEnabled` defaults on and round-trips)
  - **Speech telemetry**: `ViewModels/SpeechTelemetryPromptTests.cs` (first enable raises the opt-in offer once and accepting opts in, declining leaves speech on and telemetry off, an answered offer never re-raises, the on-open offer follows speech state, one dialog at a time).

    `ViewModels/SettingsViewModelSpeechTelemetryTests.cs` (saving telemetry on marks the offer answered and turns capture on; an untouched save leaves the offer due; on, Apply, off, Apply in one window leaves it off). `UserPreferencesSpeechTelemetryTests.cs` (telemetry defaults off and round-trips, forces capture on, prompt-shown round-trips)
  - **Settings window**: `Views/SettingsWindowSourceTests.cs` (every binding and named control of the tabbed window survives in the sections; every Speech action that runs at once says Cancel doesn't undo it). `Views/SettingsWindowNavigationTests.cs` (one selectable sidebar row per section, `SelectSection` shows each section's view, section links open their target, rows carry automation ids and headers cannot be selected).

    `Views/SettingsDialogHostTests.cs` (Apply refreshes the live views and Cancel rolls back only what came after it, OK commits and closes, Tools → Settings opens on General and the pilot-voice request on Speech, a Speech Debug request while Settings is open moves the open window to Speech).

    `Views/SettingsKeyCaptureTests.cs` (the Quick bookmark key captures a combo and ends capture; Enter/Escape/Space are captured while capturing, and otherwise Enter applies and closes, Escape closes without applying).

    `Views/SettingsTryItOutTests.cs` (Enter in Try it out leaves the window open and applies nothing). `ViewModels/SettingsViewModelApplyTests.cs` (an applied value survives a later uncommitted edit; Apply writes only the always-on-top values that changed).

    `ViewModels/SettingsViewModelResetSectionTests.cs` (one test per section: Reset section shows the defaults and the preferences change only on Apply; General keeps the initials; link-only sections disable it; Cancel after a reset changes nothing). `UserPreferencesDefaultsTests.cs` (`CreateDefaults` reads the fresh-file defaults, not the user's file, and a setter on it throws).

    `ViewModels/SettingsViewModelQuickCommandsTests.cs` (the Quick commands section's staged lists: add, remove, move, resets, validation, the Add command… offer, Apply writes only changed situations). `Views/QuickCommandsSectionTests.cs` (the section's entry order, drag drops, the strip divider, the preview, the flyout's Add, the flight-rules caption and the invalid-row warning).

    `Helpers/PreferencesFileScope.cs` restores `preferences.json` after a test that commits settings
  - **Scenario load overlay**: `ViewModels/LoadOverlayViewModelTests.cs` (a stale `Sequence` and another load's events are dropped, an all-`done`/`notNeeded` table closes itself, a `warning` or `failed` step keeps it open until Close, the RPC result's `Steps` alone renders the final table, a refusal closes it).

    `ViewModels/MainViewModelLoadOverlayTests.cs` (progress events reach the overlay; `IsRoomLoading` from `RoomLoadingBy` or the client's own load disables Load, Unload, Restart and the rewinds and raises their can-execute changes; the status line while and after a member's load; `RewindToSeconds` sends nothing mid-load)
  - **Departure auto-delete**: `UserPreferencesDepartureAutoDeleteTests.cs` (a preferences file without `departureAutoDeleteDistanceNm` reads null; a set distance persists and null clears it), `ViewModels/MainViewModelSessionSettingsTests.cs` (the session-flyout box follows `DepartureAutoDeleteDistanceNm` from `SessionSettingsDto` and the load result, null blanking it)
- **UI render tests**: `tests/Yaat.Client.UI.Render.Tests/` — headless tests that need real pixels (`UseHeadlessDrawing = false` + Skia, as `tools/Yaat.GuideCapture`), a separate assembly because the headless setting is per-assembly; `Automation/AutomationScreenshotTests.cs` (the pipe's `screenshot`: window and element captures at the render scale, overlay popups included).

  It links `AutomationHostFixture.cs`, `AutomationPipeTestClient.cs` and `ModuleInit.cs` from `Yaat.Client.UI.Tests` rather than copying them.
- **Client-driver MCP tests**: `tests/Yaat.ClientDriver.Mcp.Tests/` — the MCP's pipe client against a real headless `AutomationHost`. `PipeClientTests.cs` (a request round-trips, a host error keeps its own text in `RemoteMessage`, a dispose during an in-flight request fails that request, a cancel drops the connection and the next call reconnects, `PipeDirectory` drops a cached client whose process has exited).

  `ElementRegistryTests.cs` (UIA and pipe ids share one sequence, a pipe node keeps its id, a pipe id is refused by the UIA resolver). `InspectToolsPipeTests.cs` (list_windows, dump_tree, find_elements and get_value on a YAAT pipe: row shapes and window-relative rects, the 400-line cap, descendants only, exact Avalonia types, automationId falling back to x:Name, an empty TextBox reading "", a stale id, the UIA fallback when the cached client was evicted).

  `InputToolsPipeTests.cs` (click, invoke, click_point, set_text, send_keys, focus and set_input_mode on a YAAT pipe: the semantic action named, modifiers and double-click reaching the pointer, disabled / gone / not-focusable / out-of-bounds errors, keys with no element going to the remembered pipe pid, a UIA window id refused for click_point).

  `PipeDescribeTests.cs` (row formatting edge cases). `PipeCallsTests.cs` (a stale node, another host error, a closed or missing pipe, routing with and without a discovery file, the remembered pid kept, recorded and cleared).

  `BatchToolsTests.cs` (batch_drive over a stub pipe: step order, first-failure stop, refusals, assert wait and no_errors, the answer timeout and the 300 s deadline, caller cancellation). `ClientErrorsTests.cs` (the formatted client-errors block lists every entry with its exception; a tool call's result carries the block on a success and an error answer and stays unchanged without client errors).

  `WaitUntilToolTests.cs` (wait_until sends conditions, mode and the clamped timeout, the request timeout covers the host wait, met=false is a result not an error, the screenshot is saved and its path returned, an unsavable screenshot is a line, then-action results and a failed action without a reason are reported, the ceiling matches the host's).

  `RecorderLocatorTests.cs` (the recorder copied under `recorder/` exists and answers `--help`). `RecordToolsTests.cs` (record_start/mark/stop over a fake backend: target resolution, refusals, defaults, marks file, a died pipeline, the stop summary, wait_until's stop_recording).

  `FfmpegPipelineTests.cs` (the recorder and ffmpeg arguments, quoting, encoder listing). `ClientAreaCropTests.cs` (the crop at any title-bar offset, even sides). `RecordingFakes.cs` (the fake backend and process). `TestAppBuilder.cs` (the headless Avalonia app)
- **LayoutInspector tests**: `tests/Yaat.LayoutInspector.Tests/` — CLI option parsing and tick-recording merge. `CliOptionsTicksTests.cs` (`--ticks` is repeatable, `[LABEL=]<path>` split only when the text before `=` has no path separator or `.`, repeated flags keep their order, no `--ticks` leaves `TickSources` empty).

  `TickRecordingMergerTests.cs` (`TickRecordingMerger.Merge`: a LABEL renames a one-aircraft run's callsign to the label and a multi-aircraft run's to `LABEL:CALLSIGN`, merged aircraft get palette colours while a single unlabelled recording keeps its own, ticks are ordered by time with source order preserved on ties, and it throws `TickMergeException` on a shared unlabelled callsign or recordings from different airports)
- **Test data**: `tests/Yaat.Sim.Tests/TestData/` — NavData.dat + `navdata-manifest.json`, FAACIFP18.gz + `cifp-manifest.json`, airport GeoJSON, `oak-u-w-fillet-corner-recording.zip` (E2E fillet routing test fixture).

  `oak-push-then-taxi-approach-recording.zip` (DAL2150 PUSH-then-TAXI off OAK gate 15, for the approach-leg replay test). `s2-oak5-follow-heli-recording.zip` (S2-OAK-5 bundle: FOLLOW behind a straight-in jet, helicopter LAND @spot from off-field, CRC flight-plan amendment during a hover hold).

  `ual58-spot9-reversal-recording.yaat-bug-report-bundle.zip` (UAL58 B77W off SFO gate G10: `TAXI T9 $9` to spot 9, then `TAXI A F 28L` reversing onto taxiway A — the entry-alignment reversal fixture for `Ual58Spot9ReversalTests`). `recording-routing-census.json` (per-fixture routing census of every recorded command — the triage worklist for the action-router work).

  Refresh pins: `tools/refresh-navdata.py`, FAA CIFP via `CifpPathResolver` at test load, airport GeoJSON via `tools/refresh-test-layouts.py`.
- **Shared loader**: `TestVnasData.EnsureInitialized()` — always use this, never synthetic stubs

## Root Scripts

```
AGENTS.md                         # Codex project wrapper; points Codex back to CLAUDE.md and maps Claude agents/commands/hooks to Codex behavior.
.claude/rules/cifp-parser.md      # Path-scoped Claude guidance (loads only for src/Yaat.Sim/Data/Vnas, reference/cifp, tools/Yaat.CifpInspector): the cifparse/parseCifp column-offset authority + re-clone recipe.
tests/CLAUDE.md                   # Test-project Claude guidance: docs/test-harness.md pointer + the singleton-race, YAAT_APPDATA_DIR and xunit.runner.json gotchas (moved out of the root CLAUDE.md).
Setup-CrcEnvironment.ps1          # Adds YAAT1 to CRC's DevEnvironments.json (-Servers overrides for self-hosted)
deploy-targets.ps1                # Per-deployment map (DropletIp, ServerPath, ServerUrl, RemoteEnvFile) + Resolve-DeployTarget; dot-sourced by the deploy scripts
deploy-to-droplet.ps1             # Deploys yaat-server to a droplet (CI-built ghcr image by default; -BuildOnDroplet / -BuildImageOnly variants; -ServerRef/-ClientRef pin a hotfix build to a branch/tag instead of both mains)
deploy-secrets.ps1                # Merges yaat-server/.env + .env.<target> over the droplet's env file (key names only printed, timestamped backup, -DryRun)
deploy-ladd.ps1                   # Ships yaat-server/ladd/ladd.json (FAA LADD block list, restricted) to <ServerPath>/ladd/; -Restart recreates the container
update-ladd.ps1                   # Monthly LADD routine in one command: fetch-ladd.py (ADX portal document sync) -> refresh-ladd.py -> deploy-ladd.ps1 ->
                                  # deploy-to-droplet.ps1 -WaitForEmptyRooms -> deploy-ladd.ps1 -Restart; -Zip uses an already-downloaded zip
swim-slice.ps1                    # Live-traffic repro: copies the SWIM raw-log hours covering -From/-To off the droplet's yaat-swim-raw volume (or -Local) and
                                  # runs yaat-server tools/Yaat.SwimSlice cut [-Artcc [-Facility]]; output under yaat-server/.tmp/swim-slices (FAA data, never shared)
tools/codex-yaat.ps1              # Launches Codex from the YAAT repo root and adds ..\yaat-server as an extra writable/readable directory.
tools/setup-codex.ps1             # Creates user-local Codex skill junctions and registers MCP servers without committing local state or token values.
tools/build-artcc-boundaries.py   # Downloads the NASR 28-day ARB CSV (or --zip) and writes src/Yaat.Sim/Data/Artcc/ArtccBoundaries.geojson (--strata LOW,HIGH; re-run per cycle)
tools/bug_bundle.py               # Inspects, extracts, installs, and validates v4 bug bundles (*.yaat-bug-report-bundle.zip, *-recording.zip): info (bundle contents), history (per-callsign triage), snapshot (at a tick), actions (filtered log), terminal (all broadcasts). Subcommands bridge RecordingArchive queries and the bundle export manifest.
tools/refresh-faa-airspace.ps1    # Reads vNAS training scenario primary airports by ARTCC, then downloads matching FAA AIS Class Airspace GeoJSON/Brotli.
tools/refresh-airport-airlines.ps1 # Builds Data/airport-airlines.json.br from BTS T-100 segment ZIPs, OurAirports, and OpenFlights carrier/route crosswalks.
tools/refresh-test-layouts.py     # Re-fetches each tests/Yaat.Sim.Tests/TestData/<ID>.geojson (plain 3-4 letter stems only; `-`/prefixed snapshots are skipped) from the vNAS training-airport map API, normalized to LF; an airport with no map is kept as committed. `--check` reports what would change and exits 1 if anything would.
tools/refresh-airline-fleets.py   # Parses Airfleets PDFs into Data/airline-fleets.json + .meta provenance sidecar.
tools/refresh-aircraft-display-names.py # One-shot tool that queries OpenAI for human-readable names of every ICAO type in AircraftSpecs.json and writes Data/aircraft-display-names.json + .meta sidecar. Re-run only when AircraftSpecs.json gains new types.
tools/refresh-crc-docs.py         # Mirrors the vNAS docs site (docs.virtualnas.net, Material for MkDocs) into docs/crc/ + docs/vnas-data-admin/: fetches each page, converts HTML->Markdown, downloads images, rewrites links. See docs/crc/README.md.
tools/parse_airfleets.py          # pdfplumber parser used by refresh-airline-fleets.py; maps fleet variants to ICAO Doc 8643 types.
tools/build-mtr-data.py           # Parses the DoD AP/1B PDF by word bounding box into Data/MilitaryRoutes/ap1b-mtr.json.br + ap1b-ar.json.br (+ .meta sidecars). Gated by an FRD oracle (navaid-to-point distance must match the published Fac/Rad/Dist) and an FAA AIS MTRSegment cross-check. See military-training-routes.md.
tools/mcp/context7-stdio.ps1      # Context7 stdio adapter that reads CONTEXT7_API_KEY from the environment when Codex cannot express the custom header.
tools/mcp/exa-stdio.ps1           # Exa stdio adapter that reads EXA_API_KEY from the environment when a local authenticated Exa MCP is preferred.
tools/hooks/whitespace-fix-autostage.sh # prek hook: trailing-whitespace + EOF-newline fixes, auto-staged so the commit proceeds.
tools/hooks/dotnet-format-wrapper.sh    # prek hook wrapper for `dotnet format style` / `analyzers`.
tools/hooks/csharpier-wrapper.sh        # prek hook wrapper for `dotnet csharpier format .`.
tools/hooks/claude-guard-bash.sh   # Claude Code PreToolUse(Bash) guard: denies uncaptured/untimed dotnet test|build|run (must run through tools/gate.ps1 or redirect to a .tmp log; `| tee` is denied), bare `dotnet format`, dotnet -q/--nologo, and `prek run --all-files`. Matches only in command position (quoted spans and heredoc bodies stripped) so grepping a doc that mentions a guarded command is not blocked. Registered from .claude/settings.json (tracked, so contributors + the CI Claude workflows get it too).
tools/hooks/claude-guard-read.sh   # Claude Code PreToolUse(Read) guard: denies reading secret-bearing files (.env*, *.pem/key/pfx/p12, credentials, secrets.*, appsettings.Local.json). Narrow by design — `Secrets.cs` and docs about credentials stay readable.
tools/hooks/claude-guard-cases.jsonl    # Expected allow/deny table for the Bash guard.
tools/hooks/test-claude-guards.sh       # Runs the table above; run by hand after changing a guard file.
tools/gate.ps1                    # `pwsh tools/gate.ps1 -Log <log> -TimeoutSeconds <seconds> -Slot heavy|light -- <command...>`, all four required (a launcher for ~/.claude/tools/gate/gate.ps1, which runs the command at below-normal priority with the log and tail but no slots or watchdog where that file is missing; runs from Bash too). Full output to a log, only the tail (plus the failure lines on red) on screen, exits with the command's own status, also failing when the log holds `Build FAILED` or `error CS`; kills a stalled run, one past its ceiling on the load-adjusted clock, or one at the 5x wall backstop (exit 124); runs the command at below-normal priority with its own MSBuild nodes, holding one heavy slot ((threads - 1) / 4 machine-wide) or one light slot ((threads - 1) / 2). Use for every dotnet build/test/run, always `-Slot heavy` here; `cmd | tee` floods the context with the whole log and reports tee's status.
.mcp.json                         # Claude Code project MCP registration: `yaat-client-driver` → `pwsh tools/Yaat.ClientDriver.Mcp/launch.ps1`, which runs the server from a shadow copy of its build output outside the repo so a running server never locks `bin/`
tools/measure-test-loop.ps1       # Measures the developer test loop (discovery, one-class, whole-project wall+CPU, incremental/cold build, full gate) as medians over N runs. Detects xunit.v3 vs TUnit from the csproj and picks the matching filter syntax, so a framework-migration branch can be diffed against main. Baseline numbers: docs/plans/tunit-migration.md.
tools/analyze-test-schedule.py    # Reads a TRX of per-test durations and LPT-packs it onto N workers under both scheduling models (xunit's per-collection vs per-test), reporting the makespan gap. Answers "would a different scheduler help?" without changing frameworks — on this suite the gap measures 0.0s.
tools/gen-synthetic-suite.py      # Emits a throwaway test project matching Yaat.Sim.Tests' shape (class/test/InlineData counts) in xunit.v3 or TUnit, with trivial bodies. Measures a framework's *compile* cost at our scale without converting real files — how the TUnit source generator was shown to add ~5s per incremental build at 9,306 cases.
tools/speech_telemetry.py         # Server-side speech telemetry triage (stdlib Python, admin password from YAAT_ADMIN_PASSWORD): pull (download + unpack bundles into .tmp/speech-telemetry/cases/), summary (worst-first triage table), eval (score cases through Yaat.SpeechSandbox --eval), reviewed (mark cases seen), promote (append a reviewed case's text-only labels to the telemetry regression corpus).
tools/tests/test_speech_telemetry.py # pytest for speech_telemetry.py against a fake admin server: pull watermark and errors, summary ordering/JSON/width, reviewed bookkeeping, promote (text-only, sorted, duplicate/unreviewed refusal), case staging, gate-run eval command
```

The sibling yaat-server repo registers the same two guards via its own `.claude/settings.json` → `tools/hooks/claude-guard.sh`, a shim that resolves these scripts through the sibling checkout first and `extern/yaat/` second (the same order `Directory.Build.props` uses for Yaat.Sim) and fails open if neither is present.

See [installer-release.md](installer-release.md) for the Velopack packaging, auto-update, CRC install prompt, and the tag-driven `release.yml` (win+linux) / `release-macos.yml` (Apple Silicon + Intel, one Velopack channel per architecture) pipelines. macOS code signing/notarization (Developer ID certs, entitlements + `Info.plist` template in `build/macos/`, the eight `MACOS_*` secrets) is set up via [macos-code-signing.md](macos-code-signing.md).

## Yaat.Client.Strips — WASM-clean strip layer (`src/Yaat.Client.Strips/`)

Foundation for the flight-strip view. Pure Avalonia + SignalR + CommunityToolkit.Mvvm — no Avalonia.Desktop, no Velopack, no file IO. The browser strips client (`tools/Yaat.VStrips.Web`) consumes only this assembly so its WASM publish closure stays free of Win32-only code. Yaat.Client.Core project-references Strips and exposes the shared types up to Yaat.Client.

```
Logging/
  ConsoleLineLoggerProvider.cs  # ILoggerProvider that writes one text line per entry to Console.Out (browser DevTools console). Used by Yaat.VStrips.Web/Program.cs and AppLog.InitializeForBrowser.

Services/
  StripDtos.cs                  # StripItemType / StripItemDto / StripBayContentsDto / FlightStripsStateDto / StripBayConfigDto / FlightStripsConfigDto — wire-format records for the strip surface. FlightStripsConfigDto carries the vNAS enableArrivalStrips / enableSeparateArrDepPrinters flags that drive the printer modal's carousel split.
  ClientProductTitle.cs         # Shared "(N) {FACILITY} - {product}[ (YAAT)]" tab/window/page title format for vStrips + vTDLS (desktop surfaces skip the YAAT suffix; browser pages include it).
  StripsTransportDtos.cs        # AccessibleFacilityDto (return of GetAccessibleFacilities / GetAccessibleTdlsFacilities; IsConsolidated marks a vTDLS parent page) + CommandResultDto (return of every strip command) + StripsWeatherDto (narrow WeatherChanged projection — raw METARs) + StripMetarEntry (parsed station + raw, for the METAR bar). Live here because the IStripsTransport surface returns/feeds them.
  IStripsTransport.cs           # Narrow contract VStripsViewModel depends on. IsConnected + transport-state events + StripsConfigChanged + FlightStripsStateChanged + StripItemsChanged + MetarsChanged (raw METARs for the current-METAR bar) + Get/RequestStrips RPC trio. ServerConnection (Core) and BrowserStripsTransport (here) both implement it.
  BrowserStripsTransport.cs     # WASM-side IStripsTransport. Owns its own HubConnection, wires JsonHubProtocol against YaatStripsHubJsonContext only, exposes auto-join helpers (FindRoomForMyCidAsync, JoinRoomAsync, SendCommandAsync, ConnectAsync, RoomAvailableForCid event) needed by tools/Yaat.VStrips.Web/MainView. Browser-only DTO subsets (BrowserRoomInfoDto, BrowserJoinRoomResultDto, BrowserScenarioLoadedDto) downscope the wire format so the WASM bundle ships only the fields the strip view reads.
  YaatStripsHubJsonContext.cs   # Source-generated JsonSerializerContext for the strip DTO subset. Inserted into the JsonHubProtocol resolver chain by both ServerConnection (alongside YaatHubJsonContext) and BrowserStripsTransport (alone).

ViewModels/
  VStripsViewModel.cs           # Root vStrips VM; manages strip bays, items, rack state. Ctor takes IStripsTransport + send-command delegate + Func<string>? getUserInitials.
  StripItemViewModel.cs         # Per-strip observable model: flight data, annotations
  StripBayViewModel.cs          # Per-bay container: list of strips, visibility state
  StripRackViewModel.cs         # Rack (visual height) management per bay
  StripPrinterViewModel.cs      # Auto-print on aircraft departure/arrival
  VStripsCanonicalBuilder.cs    # Build canonical strip commands from UI mutations

Find/                           # Shared in-view Find (Ctrl+F) — also reused by vTDLS
  IFindableItem.cs              # Row contract: GetFindText() + IsFindMatch/IsCurrentFindMatch flags. Implemented by StripItemViewModel and TdlsItemViewModel.
  FindMatcher.cs                # Pure matcher: whitespace-tokenized, case-insensitive AND over GetFindText().
  FindController.cs             # ObservableObject driving one view's find: query/visibility, match set (tracked by reference), Next/Previous/Close/Refresh + scroll-into-view callback. No Avalonia dep — unit-tested.

Views/Find/
  FindBarView.axaml(.cs)        # Shared find-bar overlay (query box, match counter, ◀ ▶ ✕). DataContext is a FindController; hosted by VStripsView and VTdlsView.

Views/VStrips/
  VStripsView.axaml(.cs)        # Embedded vStrips UserControl. Pointer-capture strip drag (no OS DnD): display-rate ghost + animated drop-preview gap with index hysteresis, Esc cancel, wheel scroll + edge autoscroll mid-drag, drop-settle animation. Also hosts the FindBar overlay (Ctrl+F, scoped to the shown bay) and sticky-bottom rack scroll.
  StickyScroll.cs               # Pure decision for the racks ScrollViewer sticky-bottom behavior — re-pin to bottom when content grows while the user was already at the bottom. Unit-tested.
  FlightStripControl.axaml(.cs) # Custom control rendering CRC-matching strip visuals (cream cells, barcode, handwriting, offset, disconnected ✗, selection ring, cyan find-match highlight)
  InlineTextEditPopup.axaml(.cs) # Shared popup editor for annotations, half-strip lines, separator labels

Resources/Fonts/                # JetBrainsMono-Regular.ttf, JetBrainsMono-Bold.ttf, JetBrainsMono-Italic.ttf, JetBrainsMono-BoldItalic.ttf, OFL.txt — embedded for cross-platform monospace consistency (Inter doesn't column-align); italic variants are needed so annotation cells (FontStyle=Italic + FontWeight=Bold) render real bold on WASM where Segoe Script / Lucida Handwriting aren't available

AppBuilderExtensions.cs         # WithJetBrainsMonoFont() — registers the embedded font collection at avares://Yaat.Client.Strips/Resources/Fonts. Called by tools/Yaat.VStrips.Web/Program.cs.
```

## Yaat.Client.Tdls — WASM-clean vTDLS layer (`src/Yaat.Client.Tdls/`)

Sibling of Yaat.Client.Strips for the vTDLS (Pre-Departure Clearance) view. Same constraints: pure Avalonia + SignalR + CommunityToolkit.Mvvm — no Avalonia.Desktop, no Velopack, no file IO. Both the embedded vTDLS tab in Yaat.Client and the browser app at `tools/Yaat.VTdls.Web` consume this assembly. ProjectReferences `Yaat.Sim` + `Yaat.Client.Strips` (the latter just for the shared JetBrains Mono font registration via `WithJetBrainsMonoFont()`).

```
Services/
  TdlsDtos.cs                   # Client-side JSON mirrors of the server vTDLS DTOs: TdlsStatus enum, TdlsItemDto, TdlsItemRemovedDto, TdlsStateDto, ClearanceDto, TdlsConfigDto + nested SID/transition/value records, TdlsFacilityViewDto (one config per member facility — a consolidated parent carries its children's). Property names match the server one-for-one so System.Text.Json round-trips without converters.
  ITdlsTransport.cs             # Narrow contract VTdlsViewModel depends on. IsConnected + transport-state events + TdlsItemChanged/Removed/StateChanged broadcasts + GetAccessibleTdlsFacilities/GetTdlsFacilityView/RequestFullTdlsState RPC trio. ServerConnection (Core) and BrowserTdlsTransport (here) both implement it.
  BrowserTdlsTransport.cs       # WASM-side ITdlsTransport. Owns its own HubConnection, wires JsonHubProtocol against YaatTdlsHubJsonContext only, exposes auto-join helpers (FindRoomForMyCidAsync, JoinRoomAsync, SendCommandAsync, ConnectAsync, RoomAvailableForCid event) needed by tools/Yaat.VTdls.Web/MainView. Browser-only DTO subsets (BrowserTdlsRoomInfoDto, BrowserTdlsJoinRoomResultDto) downscope the wire format.
  YaatTdlsHubJsonContext.cs     # Source-generated JsonSerializerContext for the TDLS DTO subset. Inserted into the JsonHubProtocol resolver chain by both ServerConnection (alongside YaatHubJsonContext + YaatStripsHubJsonContext) and BrowserTdlsTransport (alone).

ViewModels/
  VTdlsViewModel.cs             # Root vTDLS VM; reconciles DCL (Pending) and PDC (Sent+Wilco) lists from broadcast events; surfaces accessible-facility list + Switch/Refresh. Ctor takes ITdlsTransport + send-command delegate + Func<string>? getUserInitials. Clear() empties every item, the selection and any open editor (called on connection loss and by the host when the room's session goes away).
  TdlsItemViewModel.cs          # Per-item observable: AircraftId, Status, Sequence, timestamps, SentPayload. Instance identity preserved across reconciles so Avalonia bindings stay stable.
  TdlsFlightPlanEditorViewModel.cs # Nine-field editor; wraps a working ClearanceDto, exposes per-field dropdowns from the facility's TdlsConfigDto, applies SID+transition defaults on selection, gates Send button on mandatory-field completion. A climb-via value disables and clears Maintain (IsInitialAltEnabled) and satisfies a mandatory InitialAlt — one altitude instruction per clearance.
  VTdlsCanonicalBuilder.cs      # Build canonical TDLS commands (TDLSQ / TDLSS Expect|Sid|... | LocalInfo / TDLSW / TDLSDUMP) from UI gestures.

Views/VTdls/
  VTdlsView.axaml(.cs)          # Embedded vTDLS UserControl. Layout mirrors upstream tdls.virtualnas.net: black header chrome, DCL list on top (full width, WrapPanel Vertical column wrap), PDC + decorative empty CPDLC split 50/50 below, flight-plan editor docks at bottom when a DCL item is selected, footer with CLEARANCE TYPE status + Zulu clock. Hosts the shared FindBar overlay (Ctrl+F, searches the DCL + PDC lists). Key bindings: F4 Dump, F10 close editor, F12 Send, Ctrl+F / F3 / Shift+F3 find.
```

## Yaat.Client.Core — Shared library (`src/Yaat.Client.Core/`)

Code referenced by Yaat.Client that needs Avalonia.Desktop, Velopack, or file-system access. No LM-Kit, PortAudio, or SharpHook dependencies. Project-references Yaat.Client.Strips for the strip layer. Namespace stays `Yaat.Client.*`.

```
Logging/
  AppLog.cs                     # Static logger factory; Initialize(logFileName) called by each desktop app's Program.cs. Wraps SimLog; both Initialize paths also register RecentErrors (AppLog.RecentErrors, the RecentErrorLog). WASM has its own inline init in Program.cs that wires SimLog directly.
  RecentErrorLog.cs             # ILoggerProvider keeping the last 200 Error/Critical entries (RecentErrorEntry) with ever-growing sequence numbers; Since(sequence, max) reads the newer ones. Registered by AppLog as AppLog.RecentErrors
  FileLoggerProvider.cs         # Writes to YaatPaths.AppDataRoot/<logFileName> (yaat-client.log); rotates the previous 3 sessions to .log.1/.2/.3 on launch so a relaunch can't destroy a crash/freeze log

Services/
  ServerConnection.cs           # SignalR client to /hubs/training (JSON). TakeControlConfirmation is asked before SendCommandAsync sends a command. Scenario-load events ScenarioLoadProgress (ScenarioLoadProgressDto/LoadStepDto) and RoomLoadingChanged; CloseRoomAsync; LoadScenarioResultDto.Steps and RoomStateDto.LoadingBy are init properties
                                # ActionRouter.WouldRecord says would cut short a tape the room is playing back (null = send, the WASM front-ends);
                                # the playback flag it reads is seeded from the sim-state push, a join, a rewind, a loaded recording and a timeline read,
                                # because a rewound room is paused and never ticks. Implements IStripsTransport from Strips. Inline DTOs for everything outside the strip surface (rooms, aircraft, weather, CRC, recordings). Includes PilotTransmissionBroadcastDto + PilotTransmissionReceived for solo-training audio. ConnectAsync takes an access-token provider (the YAAT session token). GetMyPermittedArtccsAsync = the ARTCCs the caller may create rooms for (home + operator grants). ExportRoomAsScenarioAsync invokes ExportRoomAsScenario, returning a ScenarioExportResultDto (Json/Name/AircraftCount/Flags of ScenarioExportFlagDto/DeniedReason).
  VatsimAuthClient.cs           # Client side of server-mediated VATSIM Connect: system-browser + loopback handoff (or /auth/dev when a server is in dev-bypass — passes the stored ARTCC so the dev token carries an artcc claim), token refresh, per-server session persistence to auth-sessions.json. Supplies the SignalR access token.
  YaatReconnectPolicy.cs        # IRetryPolicy for the SignalR HubConnection: keeps retrying through a full server restart/deploy (up to ~15 min) instead of giving up after ~40s, so a session resumes automatically once the server is back.
  CfrAlertMonitor.cs            # Per-aircraft CFR release-window latch; evaluates each window vs real UTC (via CfrAlertEvaluator) and reports early/late/expired violations once. Wall-clock, alert-only (#230)
  UpdateService.cs              # Velopack auto-updater. Constructor takes channel? — null for Yaat.Client (default platform channel). CheckForUpdateAsync returns an UpdateCheckResult (UpdateAvailable/UpToDate/NotInstalled/Failed) so Help > Check for Updates can report each case; the startup check ignores all but UpdateAvailable.
  ClientVersionGate.cs          # Reads GET /api/client-requirements before connecting. Below Minimum -> refuse with a message; below Recommended -> dismissible banner. Fails open on any error.
  SoloPacing.cs                 # Solo-training pacing as the client shows it: the parking call-up rate percent (100% = one call-up per 20 s, 0 = paused) ↔ the 0 or 10-120 s interval every control shows, the "Paused" / "Once per N sec" label, and LoadDefaults (the stored Settings pacing pair a load without the setup dialog sends). Shared by the session flyout, the scenario setup dialog and Settings › Scenario defaults
  SessionAutoAcceptWire.cs      # The session flyout's auto-accept checkbox + 0-60 s delay ↔ the wire's single AutoAcceptDelaySeconds (-1 = off); the box keeps its delay while the checkbox is off
  YaatHubJsonContext.cs         # Source-generated JsonSerializerContext for the broader DTO surface (room state, aircraft, weather, CRC, scenarios). Strip DTOs live in YaatStripsHubJsonContext (Strips); both contexts insert into the same resolver chain.
  MacroDefinition.cs            # Macro model: Name, Expansion, ParameterNames
  CrcAlias.cs                   # One CRC alias definition: Name (with leading dot), ReplacementTokens, ArgumentCount, source file + line
  CrcAliasFileParser.cs         # Pure text→aliases. Mirrors CRC AliasParser.LoadAliases: a line counts only if it starts with '.', is >=4 chars, and matches ^(\.\w+)\s+(.+)$ — which is why header text and # comments are skipped (CRC has no comment syntax). ArgumentCount = consecutive $1,$2,... stopping at the first gap
  CrcAliasStore.cs              # Loads {ArtccId}.txt then MyAliases.txt (personal wins) from the CRC Aliases dir — the same two fixed names CRC reads, never a glob; case-insensitive lookup; TryExpand does CRC's right-to-left token scan with rescan-from-end, so alias bodies may reference other aliases (1000-substitution recursion cap). ResolveDirectory reuses CrcConfigService.GetCrcConfigDir
  CrcAliasVariables.cs          # $dep/$arr/$route/$fullroute against a pure CrcAliasContext ("----" sentinel when absent) + $urlescape(x). Two passes, no-arg before function-form, so $urlescape($fullroute) resolves without recursive re-parsing

ContextMenus/                   # The aircraft right-click menu catalog shared by radar, ground and the aircraft list (namespace Yaat.Client.ContextMenus; ContextMenu would shadow Avalonia's type)
  AircraftCommandApplicability.cs # Static: single source of truth for whether a tower/ground/landing/pattern command fits an aircraft's state (CanClearForTakeoff/CanClearToLand/CanPushBack/…), read against IMenuAircraft; plus the quick-list-only rules QuickCommandResolver applies: Shows* (hide an entry by the situation flags, NextCrossingRunway and the clearances on the aircraft) and Widens* (admit an entry in a phase its catalog predicate leaves out, each proven against the sim in QuickCommandSimAcceptanceTests)
  CanvasMenuItems.cs            # Static: the view sections' canvas-only items, each built from the state the surface hands it (data-block form and position reset, nav route, measure, taxi-route mode radio, hide datablock, Draw route, and the leader-direction / J-ring / cone / blank / unblank overlays, which send through the host), plus Display, which assembles a view's blocks into the submenu; never catalog entries, no MenuIds, never on a quick-command list
  IMenuAircraft.cs              # Read-only view of an aircraft the applicability predicates and the Warp seed read; AircraftModel implements it
  IMenuHost.cs                  # What a catalog entry's builder needs from the view that opened the menu, the same on every view: Session, SendAsync, the popups (ShowInputPopup, ShowListPopup/ShowFilteredListPopup, ShowWarpPopup, the command and note flyouts), OpenFlightPlanEditor, BuildFavorites, BuildRpoItems, AssumeSelectedLiveTrafficAsync, the reads (FixNames, GetFieldElevation, GetGroundTrafficCallsigns), the ground-movement answers (GetHoldShortChoices, SetRoutePreview, GetPushbackFaceChoices, GetPushbackToChoices, GetPresetTaxiChoices), EnterDrawRoute and EnterPushRoute (which show the primary ground view first), and the point menu's DescribePoint, GetTaxiChoices, GetRunwayHoldShortTargets and GetCustomTaxiSeed; ClientMenuHost serves every member on every view and none throws; no canvas state: each view builds its canvas items itself (CanvasMenuItems)
  BlankInput.cs                 # Enum Closes/Submits: what an input popup does on a blank submit; Submits only on the optional fields that send the bare verb (RTIS, CTO Custom…, the pattern-entry runway tier)
  MenuCommandChoice.cs          # Record Label/Command?/Preview/Children: a host-answered submenu choice carrying its finished command text and optional preview TaxiRoute (the ground's hold-short items, the point menu's taxi tree); children make a submenu, no command and no children a disabled row, Separator a separator
  MenuTextSeed.cs               # Record Text/Caret: an input popup's initial text and caret (Custom taxi's prefill from the clicked node, naming the clicked runway end on a threshold click)
  MenuCatalog.cs                # Static: one MenuCatalogEntry per MenuIds action (All, Get); a leaf's click sends its command text through IMenuHost.SendAsync; Command…, Note…, Warp…, Edit flight plan, Draw taxi route… and Push route… call the host instead; the point.* entries are the point menu's items; `BuildSend` is the one send-item builder, which CanvasMenuItems' overlay items reuse
  AircraftMenuBuilder.cs        # Static Build(aircraft, click, host, viewSection): the aircraft context menu every view shows, in one order by the aircraft's kind (delayed spawn, surface live-traffic shadow, any other aircraft): header, the quick commands (strip, then text), Track, Data Block, Squawk, the view section, Favorites, All Commands (the full tree, flight groups in one fixed order, all but Tower hidden in a ground phase by HidesFlightCommands), Delete
  QuickCommandEntry.cs          # Closed hierarchy: CatalogQuickCommandEntry (CatalogId + FlightRules?, null = catalog default) or CustomQuickCommandEntry (label, text, ground text?, FlightRules; MenuId "quick.custom", no glyph)
  QuickCommandDefaults.cs       # Static: each AircraftSituation's ordered default quick-command list (Unknown has none, so an unclassified aircraft shows no quick commands); catalog actions only, never a display item
  QuickCommandResolver.cs       # Static Resolve(aircraft, context, buildsAnItem) → QuickCommandResolution (strip items + text entries): the situation's list filtered by flight rules (honouring the VFR-commands-for-IFR setting), the catalog predicate or a quick-list Widening rule, and a quick-list Visibility rule; both tables apply only here, All Commands keeps every entry
  QuickCommandGlyphs.cs         # Static: catalog ID → QuickCommandGlyph (24 px stroke path + QuickCommandGlyphFamily colour) from the approved icon set; Split: the first ten glyph-bearing entries form the strip, the rest are text
  QuickCommandCatalog.cs        # QuickCommandCatalogItem + the catalog actions the Quick Commands editor may add (id, family for grouping, label, default flight rules, glyph); display items and actions needing a selected or relative aircraft are not eligible
  QuickCommandSituationNames.cs # Static: the name the Quick Commands editor shows for each classified AircraftSituation (Unknown has none); Classified lists them in enum order
  QuickCommandStrip.cs          # Static Build: the icon strip, one MenuItem holding up to two rows of five glyph buttons; a button clicks its catalog entry's own built item, or opens that item's submenu as a flyout (corner notch); the tooltip names the label and the command (MenuCommandText); GlyphIcon, GlyphCell and Rows are the glyph, button-square and two-row layout factories the strip and the Settings Quick Commands preview both draw with
  MenuCommandText.cs            # Attached property: the command a sending menu item sends, recorded by MenuCatalog.BuildSend so the strip tooltip can name it
  MenuCatalogEntry.cs           # Record: stable ID, label, default flight-rules filter, applicability predicate, builder → MenuItem? (aircraft nullable: a view builds the menu with no aircraft model when the clicked callsign has none); under All Commands the predicate alone decides whether it shows, in a quick list the resolver's widening and visibility rules also apply
  MenuClick.cs                  # Record: the click context — the commanded callsign, the previous selection (sender of the relative items), the clicked point (MenuPoint: position, ground node, runway end, the runways under a runway-surface click, the node Warp here uses there; null on an aircraft click), the list's selected rows
  RunwayHoldShortTarget.cs      # Record Node/Label: one hold-short target a runway-surface click offers per runway end ("At B (nearest)", "Full length (at W)")
  MenuContext.cs                # Record: the click (MenuClick) and the session (MenuSession), with read-through Callsign / PreviousSelection / Initials / SoloTrainingMode / VfrCommandsForIfr — the predicates' non-aircraft inputs
  MenuFlightRules.cs            # Enum Both/IfrOnly/VfrOnly: a quick-command entry's default flight-rules filter
  MenuIds.cs                    # Stable menu IDs, <group>.<item>, one per action, append-only (exported preferences carry them)
  MenuLabeledValue.cs           # A picker value with its display label (altitude list: the value in feet, shown as FLnnn at or above 18000); the catalog unwraps it before the pick
  MenuMeasureState.cs           # Enum None/NoAnchor/HasAnchor: a view's measure tool as `CanvasMenuItems.Measure` reads it (None = no tool, no item)
  MenuPickerDescriptor.cs       # Tag on a picker menu item: the values its list/filtered-list/input popup offers, readable without opening it (the menu goldens print it)
  MenuSession.cs                # Record: the menu session — initials, solo training mode, VFR-for-IFR mode
  RunwayDesignatorComparer.cs   # Orders runway designators by number then L/C/R (the runway flyout and the menu runway pickers)
  RunwayDesignators.cs          # Static: ForAirport — an airport's runway ends in display form, sorted, for the visual-approach and pattern pickers
  SharedMenuGroups.cs           # Static: the menu's groups, built from catalog entries in one order and text on every view: the header (`AddHeader`: title, route summary and hold status rows, the release items, Command…, Note…), favorites, the live-traffic assume items, track, data block, squawk, ask pilot, coordination, heading, altitude, speed, navigation, hold, approach (with Report when…), procedures, tower and pattern, the relative group, Edit flight plan, the sim-control items that close All Commands (`AddSimControl`: Warp…, Release to live feed), the foot (`AddFoot`: Delete), a delayed aircraft's menu (`AddDelayedSpawn`), the multi-select assume (`AddAssumeSelected`) and a runway-surface point click's Taxi to {end} items (`AddTaxiToRunwayEnds`); AircraftMenuBuilder places them, including a surface live-traffic shadow's read-only menu (its private `AddSurfaceShadow`: track, data block, the view section, Favorites, All Commands holding only coordination, then the foot)
  TaxiRouteDisplayMode.cs       # Enum Follow/AlwaysShow/AlwaysHide: an aircraft's per-aircraft taxi-route override in the ground view (the Taxi route radio submenu)
  SpawnDelay.cs                 # The Change spawn delay presets and the custom box's parser (90, 2m15s, 1h → seconds)
  HoldShortMenuHelper.cs        # The held runway from the "Holding Short {rwy}" phase, for the Cross, Line up and wait and Cleared for takeoff items on every view
  RelativeTraffic.cs            # Relative-selection gates over IMenuAircraft: HasRelativeContext, the ground pair (ShouldOfferGroundActions, OffersGroundRelative, both on the ground and the selected aircraft controllable), the airborne pair (both airborne, controllable) and ShouldOfferFollow (the selected aircraft reported the clicked one in sight); SharedMenuGroups.AddRelative builds the block on every view

Models/
  TerminalColorScheme.cs        # Operator-tunable per-Kind terminal foreground colors (Command/Response/System/Say/PilotSpeech/Warning/Error/Chat/Tdls/Strip); defaults match the legacy hard-coded scheme
  CommandHistoryEntry.cs        # Up-arrow recall entry: (Callsign, Command). Callsign-less command text + the aircraft it was sent to (empty = global/untargeted); recall filters by selected aircraft
  DatablockDeconflictMode.cs    # Per-view datablock-deconfliction setting (Off / Snap / Free-form); persisted per view in UserPreferences
  GroundColorScheme.cs          # Theme/color scheme for strips
  RendererMode.cs               # User-selectable GPU backend (Auto/Metal/OpenGl/Software); macOS-only — the default OpenGL path is emulated over Metal and burns CPU on Apple Silicon, so Metal renders directly; ignored on Windows/Linux
  TerminalEntry.cs              # Terminal/radio log entry (Kind: Command/Response/System/Say/Warning/Error/Chat/PilotSpeech/Tdls/Strip); Sequence (process-wide counter, a log position that survives trimming; LastSequence reads the newest) and IsHistory (a line rebuilt from a loaded recording)
  LiveTrafficListFilter.cs      # Aircraft List treatment of live-traffic shadows (All / HideLive / OnlyLive); persisted in UserPreferences

ViewModels/
  ConnectViewModel.cs           # Room/identity connection flow

Views/
  ConnectWindow.axaml.cs        # Server/room/identity entry dialog
  WindowGeometryHelper.cs       # Save/restore window position+size+min/max/topmost state; profile-apply path (ApplyGeometry) un-minimizes + activates (#365); post-open drift verify re-applies the saved spot, and the save path never persists a position from a window with no platform surface (#408); composes WindowSystemMenuHelper + WindowNativeMenuHelper for cross-platform always-on-top discoverability; attaches every window to WindowGroupRaiser; in automation mode applies ShowActivated=false, never activates, keeps the requested pin without setting Topmost, and keeps windows Normal (a saved Maximized/Minimized state is remembered for saving, not applied, since any visible WindowState change activates on Win32)
  WindowGroupRaiser.cs          # CRC-style group raise (#392): when focus returns from another app, raises all tracked windows via Topmost pulses (no focus steal) in Z-order, clicked window last; suspended during profile apply; gated by the RaiseWindowsTogether preference (default on); never raises in automation mode
  WindowActivationExtensions.cs # Window.RestoreAndActivate(): un-minimize (WindowState.Normal) before Activate — every reuse-and-activate window (FPE, Favorites Panel, Speech Debug, Session Report, Weather/Arrival editors) goes through this (#360); skips Activate in automation mode
  DialogPresenter.cs            # ShowModalAsync[<T>](dialog, owner): every client dialog opens through it. Outside automation mode it is ShowDialog; in automation mode the dialog shows never-activated and non-modal, the owner is disabled by hand (one count per owner) and its user close cancelled (ShouldCancelOwnerClose), and the result comes back on Closed. Result dialogs close through DialogPresenter.Close(this, value); AutomationModeSourceTests rejects a raw ShowDialog or a bare Close(result)
  AutomationGate.cs             # Core-side automation-mode flag (SuppressActivation, set through Yaat.Client's AutomationMode) and ApplyShowActivated(window) (ShowActivated=false plus WS_EX_NOACTIVATE through the Win32 window-styles callback, NoActivateStyles; on Windows also SetWindowPos to the bottom of the Z order before the first show, and an Opened handler that places an owned window directly above its owner; with CloakWindows set, a DWM cloak before the first show through the Cloaker seam, exit 3 on failure) for windows that do not build a WindowGeometryHelper and for every DialogPresenter dialog; AutomationModeSourceTests keeps every Activate()/Topmost=true behind it
  WindowSystemMenuHelper.cs     # Windows-only: injects "Always on Top" into the title-bar system menu via WM_SYSCOMMAND + SetWindowSubclass
  WindowNativeMenuHelper.cs     # macOS-only: adds "Window → Always on Top" to the menu bar via Avalonia NativeMenu
  KeybindHelper.cs              # Keyboard shortcut resolution
  IAlwaysOnTopToggle.cs         # Window contract for the central always-on-top hotkey (WindowHotkeys); each window delegates to its WindowGeometryHelper.ToggleTopmost
  VStrips/VStripsViewWindow.axaml.cs # Pop-out window shell for the strips view — sizes/positions/titles itself; content (a VStripsSplitHost) is supplied by the creating host. Stays in Core because it depends on UserPreferences + WindowGeometryHelper + KeybindHelper; only the desktop hosts open this Window.
  VTdls/VTdlsViewWindow.axaml.cs # Pop-out window wrapper for VTdlsView. Same shape as VStripsViewWindow: per-facility geometry key (`VTdlsView:{facilityId}`), first-time topmost inherited from the global `VTdlsView` default, AlwaysOnTop hotkey from UserPreferences.
```

## Yaat.Client — Avalonia desktop app (`src/Yaat.Client/`)

```
Automation/
  AutomationHost.cs             # The pipe host: a NamedPipeTransport (current user only) + DiscoveryFile + AutomationDispatcher over one NodeRegistry and a roots provider; started from App.OnFrameworkInitializationCompleted on Windows in automation mode (AutomationHostFactory.StartIfEnabled), disposed on exit and Ctrl+C. Derived from Zafiro.Avalonia.Mcp (MIT, NOTICE)
  AutomationHostFactory.cs      # StartIfEnabled guard, PipeName(pid) = yaat-automation-<pid>, DiscoveryDirectory = %TEMP%/yaat-automation (a deliberate exception to YaatPaths, so a driver finds every client)
  AutomationDispatcher.cs       # Reads one JSON request per line, routes by method to an IRequestHandler, answers a result or a coded AutomationError (malformed / missing id / unknown method → INVALID_PARAM, handler exception → INTERNAL, logged); every answer carries in clientErrors the errors the client logged from the call's start to its answer (AppLog.RecentErrors, at most AutomationResponse.MaxClientErrors)
  FilePickQueue.cs              # The process-wide FIFO of file-dialog answers (FilePickAnswer: a path or a cancel) that queue_file_pick fills and the injected picker drains; Enqueue returns the length after the enqueue, all under one lock
  InjectedFilePickerService.cs  # Automation mode's IFilePickerService: no TopLevel or StorageProvider, each call takes one FilePickQueue answer (path, or null / [] for a cancel); an empty queue throws InvalidOperationException at once
  IAutomationState.cs           # The simulation state the pipe reads (aircraft, scenario seconds, pause, sim rate, terminal cursor and entries since it) and the client actions it may take (PauseAsync, UnpauseAsync, SetRateAsync, as AutomationActionOutcome); UI thread only
  MainViewModelAutomationState.cs # IAutomationState over MainViewModel: terminal entries by TerminalEntry.Sequence, actions send raw PAUSE / UNPAUSE / SIMRATE through the server connection
  Handlers/                     # IRequestHandler, HandlerResult, PingHandler (pid + protocol version), ListWindowsHandler (the provider's windows, their owned windows, their overlay popups, as WindowInfo with node ids), TreeHandler (get_tree: Visual/Logical, depth, a nodeId or selector root, each window's overlay popups as their own roots, listed once); input: ClickHandler (Avalonia's own click through the automation peers' IInvokeProvider/IToggleProvider, menu items and selection, the synthetic pointer only when no meaningful action exists), ClickPointHandler (window-relative DIPs, InputHitTest, one pointer press/release), SendKeysHandler + SendKeysParser (today's MCP SendKeys syntax: text events at the caret, routed KeyDown/KeyUp for braced keys and Ctrl/Alt prefixes, Shift on a letter or digit types the shifted character, push-to-talk refused), SetTextHandler, FocusHandler, SyntheticPointer, InputParams, TargetResolver; WaitUntilHandler (wait_until: polls IAutomationState every 100 ms for on_ground / landed / phase_is / phase_is_not / queue_empty / log_matches / sim_seconds conditions, any or all, answers met or not met as a WaitUntilResult, captures the optional screenshot and runs the then actions pause / set_rate at the matching poll) + WaitUntilParams (request parsing and validation: WaitUntilCondition, WaitUntilAction, timeout clamped to 100-600000 ms); ListAppToolsHandler (list_app_tools: every app tool with its parameters and current availability) and CallAppToolHandler (call_app_tool: binds a JSON object by parameter name, INVALID_PARAM for an unknown tool or a missing / unknown / wrong-kind argument, checked before availability)
  AutomationToolAttribute.cs    # [AutomationTool(name, description, availabilityMethod)]: marks an AutomationTools method as an app tool the pipe lists and calls; the availability method is a public parameterless string? (null = available)
  Tools/                        # AutomationTools (partial per area: Connection, Sim, Radar, Session, Recording, Measure, DataBlock, Window) over MainViewModel + IAutomationState: the app tools set_cloaked (available whenever the main window is up; the window list through the OpenWindows seam), connect, create_room, set_sim_rate, play, pause, center_radar, center_radar_at, center_radar_on_fix, set_video_map, set_ptl, get_framing, set_solo, load_recording, seek, prepare_take (load, wait for maps, seek, pause, then FrameTake applies maps/centre/range/PTL/RBLs; RunTakeStepsAsync is the test seam), place_rbl, remove_rbl, clear_rbls (primary radar only), set_leader_direction, set_datablock_offset, reset_datablock_offset; the catalog is read by reflection once; a tool rejects its own arguments with AppToolArgumentException (INVALID_PARAM)
  Protocol/                     # The wire types (AutomationRequest/Response/Error/ErrorCodes, ClientLogEntry (one client error in a response's clientErrors: level, category, message, exception), DiscoveryInfo, NodeInfo, WindowInfo, PingResult, WaitUntilResult (met flag, held indices, last value per condition, optional screenshot and then-action outcomes), AppToolInfo, CallAppToolParams, CallAppToolResult, GetSimTimeResult (sim seconds, paused flag, sim rate), ProtocolMethods, ProtocolSerializer, ProtocolVersion (1.4.0), ErrorDetails; WindowInfo carries each window's native handle, 0 when there is none) — no Avalonia dependency, so the client-driver MCP can link the files as source
  Transport/                    # NamedPipeTransport (PipeOptions.CurrentUserOnly, one task per client, 100 ms back-off on a failed accept), DiscoveryFile (atomic write of <pid>.json, delete on dispose, sweep of dead / reused-pid / unparsable files and orphaned .tmp)
  Selectors/                    # SelectorParser (CSS-like: Type, *, #Name, #42 node id, [P=v] / *= ^= $=, [dc.Prop=v] over a locally set DataContext, descendant/child combinators, :nth on the last compound, :visible/:enabled/:has-text/:role…, comma alternatives; errors with positions), SelectorEngine (exact type or base type, UI thread only), SelectorRequestHelper (TryResolveSingle → MISSING/INVALID_SELECTOR, NO_MATCH, AMBIGUOUS_SELECTOR, STALE_NODE)
  Tree/NodeRegistry.cs          # Per-host stable node ids over weak references to visuals (UI thread only; dead entries pruned)
  Tree/NodeInfoBuilder.cs       # Builds NodeInfo (type, name, bounds, text, role, state, parent/owner ids, children to a depth)
  Tree/ElementDescription.cs    # The one role table and text reader get_tree reports and selectors match (text falls back to AutomationProperties.Name)
  AutomationMode.cs             # Automation mode switch: on when YAAT_AUTOMATION=1 (ReadFromEnvironment, set once in Program.Main), plus the cloak switch YAAT_CLOAK=1 (ReadCloakFromEnvironment; Program.ApplyCloakSwitch exits 2 when it is set without automation); IsEnabled drives AutomationGate.SuppressActivation, and Program turns off the global key hook and Discord Rich Presence and enables Win32 OverlayPopups

Models/
  AircraftModel.cs              # ObservableObject wrapping AircraftDto; computed displays; FromDto/UpdateFromDto; CruiseMach/IsSpeedClassified show a filed Mach (`M078`) or classified (`SC`) speed read-only via FiledSpeedDisplay, EditorSpeedText, EditorSpeedPlaceholder
  AircraftSpeechBubble.cs       # Per-aircraft speech bubble model for opt-in SAY/pilot (green) and WARN (amber) overlays on Radar/Ground views (text, severity, user-scaled duration or persist-until-clicked, dismiss state).
  FlightPlanAmendment.cs        # Immutable flight-plan amendment record (type/suffix/route/altitude/beacon/scratchpad) built by the flight plan editor and dispatched to the server

Services/
  FilePickerFactory.cs          # Create(TopLevel): the injected picker in automation mode, AvaloniaFilePickerService otherwise; the only place the client constructs AvaloniaFilePickerService
  ServerConnection.cs           # SignalR client to /hubs/training (JSON); inline DTOs
  CommandInputController.cs     # Autocomplete (callsign/command/fix/macro), history nav, signature help, FixDb binary search; unified ParseCommandInput drives both suggestion and signature pipelines
  CommandInputParseResult.cs    # Immutable parse result consumed by both autocomplete and signature help
  CommandSignature.cs           # SignaturePart record (AXAML DataType dependency)
  SignatureHelpState.cs         # Observable state for signature help tooltip (overload nav, active param, dedup)
  MacroDefinition.cs            # Macro model: Name, Expansion, ParameterNames (positional &1 or named &hdg)
  MacroExpander.cs              # Static TryExpand: scan-and-replace !NAME args in command text
  TypedCommandText.cs           # Static: the preprocessing authored command text gets before it is sent — TryExpandMacros (the typed path's macro step) and TryPrepare (macros, then canonical verbs under the controller's scheme), shared by the typed path, custom quick commands and the Quick Commands editor's validation
  ScrollStepAccumulator.cs      # Converts a burst of mouse-wheel events into whole discrete steps scaled by UserPreferences.ScrollSensitivity — slows DCB range/PTL/history/brightness spinners on a fast Mac trackpad (#275)
  CommandHistoryFormatter.cs    # Pure formatter — canonicalizes partial callsign prefix in up-arrow recall history
  NaturalCommandNormalizer.cs   # Shared transcript-to-canonical normalizer for solo-mode typed natural-language ATC input
  PilotVoicePack.cs             # Shared Piper voice-pack discovery/validation for installer and sherpa-onnx playback.
  PiperVoiceInstaller.cs        # Settings-driven Piper voice-pack downloader/extractor into YaatPaths app data.
  PilotVoiceService.cs          # Off-by-default solo-training pilot voice queue. Consumes PilotTransmissionBroadcastDto FIFO; synthesizes with sherpa-onnx/Piper, applies NAudio.Dsp radio FX, plays through PortAudio.
  PilotSpeechAlertService.cs    # Optional RPO-mode pilot-speech ding for TerminalEntryKind.PilotSpeech; generated in code and played through PortAudio.
  SpeechSampleStore.cs          # Push-to-talk sample store: writes each capture (audio + session.json) locally; a sample taken while telemetry is on gets an empty `upload-pending` marker (Add's queueForUpload). PendingUploadIds/MarkUploaded/ClearPendingUploads manage the markers; WriteBundle zips chosen samples to a stream.
  SpeechTelemetryUploader.cs    # Uploads pending speech samples, oldest first, one single-sample zip per POST to {server}/telemetry/speech with the session bearer token, only while SpeechTelemetryEnabled; a permanent rejection (400/413) drops the marker, a transient failure stops the pass and leaves the rest pending.
  BugReportIssueBuilder.cs      # Builds the prefilled GitHub new-issue URL for Scenario → File Bug Report (BugReportForm + BugReportEnvironment → BuildUrl); body mirrors the bug_report.md template, long text is truncated to a URL length ceiling without splitting an escape sequence.
  FileReveal.cs                 # Shows a file in the OS file manager (Explorer select, Finder reveal, containing folder on Linux); never throws, a refusing platform is logged.
  TrainingDataService.cs         # Fetches scenarios/weather from vNAS data API (data-api.vnas.vatsim.net)
  (UpdateService.cs lives in Yaat.Client.Core; MainViewModel constructs it with channel: null)
  ArgumentSuggester.cs           # Command argument autocomplete from CommandRegistry metadata (literal options + contextual fix/runway suggestions)
  FixSuggester.cs               # Fix name suggestions from FixDb
  AddCommandSuggester.cs        # ADD positional state machine: rules/weight/engine options, position (@spot parking names, runways, arrival routes), type/airline overrides
  SuggestionItem.cs             # Suggestion display model (text, kind, description)
  ScenarioDifficultyHelper.cs   # Scenario difficulty classification
  VideoMapService.cs            # Video map download/cache/parse (conditional HTTP freshness check)
  VnasConfigService.cs          # Fetches vNAS configuration (base URLs for video maps, tower cab images)
  TowerCabImageService.cs       # Downloads/caches tower cab JPEG backgrounds with EXIF geo-referencing
  TowerCabMapParser.cs          # Parses tower cab GeoJSON video maps into filled polygons + colored lines
  LiveWeatherService.cs         # Fetches live METARs + FD winds from aviationweather.gov → WeatherProfile
  ArtccAirportResolver.cs       # Fetches vNAS ARTCC config → underlying airport IDs (cached); recurses the facility tree for every STARS config
  LiveSessionAirportDefaults.cs # Pure: which airports the live-session picker offers for a position (tower-cab airports under its facility, else the ARTCC) and the default
  FdRegionMapping.cs            # Static ARTCC → FD region code mapping
  UserPreferences.cs            # JSON to YaatPaths.AppDataRoot/preferences.json (%LOCALAPPDATA%/yaat/; incl. PilotVoiceEnabled/Volume/RadioFxEnabled (default off), DiscordRichPresenceEnabled (default on), macros, loaded favorite-set ids (FavoriteSetsChanged event) + legacy favorites fields kept readable for FavoriteLegacyMigration, favorite video maps per ARTCC/airport/scenario, favorite METAR stations per scenario, and the macOS renderer-backend override read by Program.BuildAvaloniaApp); PreferencesVersion gates one-time load migrations (MigratePreferences) — v0→v1 resets AutoArrivalSpacingOnOccupiedRunwayTwr to its new off default for a file saved before the field existed
  AtomicFile.cs                 # WriteAllText: .tmp write then replacing move, retried ~0.7 s while another process (an antivirus scan) briefly holds the target; used by UserPreferences and FavoriteStore
  FavoriteStore.cs              # Identity model for favorite commands: FavoriteCommand entities (8-hex ids) + FavoriteSet containers (Global/Airport/Scenario/Named, id-list membership), file-per-entity persistence under %LOCALAPPDATA%/yaat/favorites/ ([Label].{id}.json, rename-follows-label), ComposeDisplay (visible containers in order), Changed event
  FavoriteLegacyMigration.cs    # One-time conversion of pre-identity favorites (scope-field base pool, embedded named sets, loaded names) into FavoriteStore; maps window-profile loaded-set names to ids and drops the legacy preferences fields
  FavoriteExport.cs             # Zip sharing: [Name].yaat-favset.zip (set.json + favorites/[Label].{id}.json) and .yaat-favlibrary.zip (all sets + all favorites + loaded ids); import takes a FavoriteImportMode — Merge by id (scope sets merge into local containers, named-set name collisions auto-suffix) or Replace (FavoriteStore.Clear after the file parses, then the file's sets become the loaded set); Merge reports each set's incoming → final id (FavoriteImportResult.SetIdMap)
  SettingsBundle.cs             # Settings bundle model: SettingsItemType (preferences, macros, verbs, favorites, grid layout, layouts), SettingsBundleEntry, SettingsBundleFormats (format names, legacy single-item extensions, which types Merge), SettingsBundle (a read bundle + skipped entries)
  SettingsBundleFile.cs         # *.yaat-settings.zip reader/writer: manifest.json (bundleVersion, writtenBy, entries); a legacy single-item file reads as a one-entry bundle; a single ticked legacy type exports as its legacy file; newer bundles import known entries and report the rest
  BoundedZipReader.cs           # Zip reads capped while copying (32 MB an entry, 128 MB a file) for bundles and the favorites zip inside them
  SettingsBundleItems.cs        # Per-item adapters: build an entry from today's state (byte-identical to the legacy serializers) and read one back, rejecting null or nameless macros/layouts
  SettingsImportPlanner.cs      # Import plans: Merge clashes (macros by base name, favorite sets by name or id, layouts by name) with Skip/Overwrite/Rename and suggestions; ValidateRenames; ApplyAll (favorites first, then layouts' LoadedFavoriteSetIds remapped)
  SettingsImportTarget.cs       # ISettingsImportTarget (where an import writes: UserPreferencesImportTarget, or the Settings view model's staged rows)
  SettingsExportSource.cs       # ISettingsExportSource (what an export reads, one entry per item type): UserPreferencesExportSource over preferences, favorites and the command scheme
  UserPreferences.Bundle.cs     # The preferences a bundle carries: an allowlist of Settings-section keys, each with a BundledPreferenceRule (range, colour, enum, keybind, model-source checks) that rejects a bad value before anything is written; UnbundledKeys names every excluded key and why
  BundledPreferenceRule.cs      # One allowlisted preference's validation rule
  CommandSchemeFile.cs          # Shareable command-verb file (*.yaat-verbs.json, full scheme keyed by CanonicalCommandType name) — Serialize/Deserialize behind Settings › Command verbs Import/Export; unknown command names are reported, not thrown
  QuickCommandListsFile.cs      # Shareable quick-command lists (*.yaat-quickcommands.json: version, situations by name, tagged catalog/custom entries) — overrides only; Replace and per-situation Merge (Skip/Overwrite); unreadable entries dropped and reported
  MenuGroup.cs                  # Enum of context menu groups (Heading, Altitude, Speed, Tower, etc.)
  ContextMenuProfile.cs         # Record: Primary/Secondary/Hidden menu groups for a phase
  ContextMenuProfileService.cs  # Static: maps phase name + isOnGround → ContextMenuProfile (which radar submenu GROUPS show)
  AircraftCommandApplicability.cs # Static: single source of truth for whether a tower/ground/landing/pattern command fits an aircraft's state (CanClearForTakeoff/CanClearToLand/CanIssueVfrOption/CanEnterPattern/CanEnterFinal/CanIssuePatternManeuvers/CanExitRunway/CanDrawTaxiRoute/...), all false for a live-traffic shadow (IsControllable) which only gets CanAssume; consumed by all three right-click surfaces + phase classifiers used by ContextMenuProfileService. The VfrCommandsForIfr-aware predicates are enforcement, not clutter suppression — the sim does not gate on flight rules
  VfrCommandGate.cs             # Static: checks a canonical command against the controller's VfrCommandsForIfr setting before MainViewModel.SendCommandAsync puts it on the wire; re-parses via CommandParser so no verb list is duplicated (issue #317)
  BuildInfo.cs                  # Static: version (from AssemblyInformationalVersion) + release-vs-dev detection (VelopackLocator.Current); used by title bar, About window, and startup log line
  DocLinks.cs                   # Static: GitHub URLs for user-facing docs (README/USER_GUIDE/COMMANDS/CHANGELOG/issues), pinned to release tag for installed builds, main for dev
  UrlLauncher.cs                # Static: opens HTTPS URLs in OS default browser (Process.Start with UseShellExecute)
  CrcAliasExecutor.cs           # Plans an expanded CRC alias into a CrcAliasExecution (Echo/ScopeMarkers/OpenUrl/Unsupported/Failed) — substitutes variables first, then reads the verb, matching CRC's ordering. .echo expands \n/\s/\t into terminal lines; .openurl takes only the first token and returns AbsoluteUri (ToString would un-escape and undo $urlescape); .am/.msg/.autotrack/.wallop and verb-less prose report unsupported
  CallsignPrefixResolver.cs     # Pure resolver: partial callsign prefix → unique aircraft or list of matching candidates. Used by MainViewModel.SendCommandAsync to disambiguate `N12` when multiple aircraft match. A leading known command verb (via CommandScheme.IsKnownVerb) is never treated as a partial callsign — only an exact match overrides it — so `CM 020` isn't matched against `CMD2`.
  CommandErrorFormatter.cs      # Pure formatter for unrecognized-command errors: when the leading token is a known callsign (partial/complete), names the verb after it instead of blaming the callsign. Used by MainViewModel.SendCommandAsync.
  MeasureEndpointResolver.cs    # Pure resolver for `.rbl A B` / `*T A B` endpoint tokens: exact callsign → fix/FRD → partial callsign, with ambiguity + navdata-not-ready errors. Used by MainViewModel's measure dot-command.
  LayoutService.cs              # Captures/applies named layouts (SavedLayout in UserPreferences): per-window geometry + the six pop-out toggles + extra Radar/Ground windows + favorites state + open Strips/vTDLS tabs + DataGrid column layout; never view settings. Surfaced via View → Layout ▸; a partial apply (Apply Layout dialog) stages only the ticked groups and leaves favorite sets alone.
  ViewSettingsCopyCatalog.cs    # Shared catalog of the Apply Layout dialog's groups: Ground/Radar per-scenario view settings (scenario source) and LayoutGroups (saved-layout source), each Key/Label/Describe/AreEqual/Copy. Single source of truth for the dialog's diff rows and MainWindow's merge-on-apply.
  ShownRouteBuilder.cs          # Pure builder for the radar "Show nav route" overlay. Produces a multi-segment path from AircraftModel.NavRouteFixes (server-provided positions, so arcs/custom/FRD fixes draw verbatim; synthetic arc vertices carry empty names) with per-fix crossing-restriction labels, plus a procedure vector tail (5 nm arrow off the last STAR fix on FM/VM/VA legs) + the expected approach line (IAF/transition → FAF → threshold, FAC extended back 5 nm when no transition is named).
  UiThreadWatchdog.cs           # Background thread that logs a [warn] + runtime/memory snapshot (working set, managed heap, .NET memory-load %, last GC pause, thread count) when the Avalonia dispatcher stalls >2s, and the stall duration + GC deltas on recovery. Diagnoses otherwise-traceless UI-thread freezes; started from App.OnFrameworkInitializationCompleted (desktop lifetime only). Past 15s it also shows the user a native (non-Avalonia, since the dispatcher is wedged) message box pointing at the log — once per process, Windows only. On Windows a hard freeze also writes a thread-info minidump via FreezeDumpWriter and logs every thread's managed call stack (UI thread first) via ManagedStackCapture.
  ManagedStackCapture.cs       # ClrMD snapshot self-attach (Windows-only) that formats every live thread's managed stack, requested (UI) thread first. Called once by UiThreadWatchdog on a 15s hard freeze so a wedged dispatcher is diagnosable from the log (GitHub #347).
  FreezeDumpWriter.cs          # MiniDumpWriteDump self-capture (Windows-only): writes yaat-freeze-<timestamp>.dmp (thread-info flags only — a few MB, no heap/user data) next to yaat-client.log on a 15s hard freeze, keeping the newest 3. Carries the native stacks ManagedStackCapture cannot walk (GitHub #347's wedge was below a native transition).
  Discord/                      # Discord Rich Presence over Discord's local RPC-over-IPC channel, hand-rolled with no package dependency — see docs/discord-integration.md "Desktop client Rich Presence"
    IRichPresencePublisher.cs   # What MainViewModel sees: Publish(DiscordActivity) / Clear(), fire-and-forget, never throw
    DiscordActivity.cs          # DiscordActivity record (Details, State, StartUnixSeconds) + DiscordRpcJson: Utf8JsonWriter builders for the handshake and SET_ACTIVITY payloads, text fields clamped to 128 chars
    DiscordIpcFrame.cs          # Wire framing [int32 opcode LE][int32 length LE][UTF-8 JSON]; opcode constants (handshake/frame/close/ping/pong); 64 KiB read cap
    IDiscordIpcConnector.cs     # Opens the IPC stream, or null when Discord is not running; the seam tests script Discord's side through
    DiscordIpcConnector.cs      # Sweeps discord-ipc-0..9: Windows named pipe, else a Unix socket under XDG_RUNTIME_DIR/TMPDIR/TMP/TEMP//tmp; logs "Discord is not running" once per absence
    DiscordRichPresenceService.cs # One background worker, latest-wins: connects only while an activity is wanted, handshake (10 s timeout) → SET_ACTIVITY → ping/pong; clearing = closing the connection; back-off 5 s doubling to 60 s; Dispose bounded to 2 s

ViewModels/
  MainViewModel.cs              # Root VM; SendCommandAsync pipeline; nav data init; the session-flyout Session* fields and their echo guard
  MainViewModel.Rooms.cs        # Partial: room lifecycle (create/join/leave; JoinCreatedRoomAsync closes a just-created room via CloseRoom when joining it throws), RoomLoadingBy seeded/cleared in ApplyRoomState/ClearRoomState, aircraft assignments; PermittedArtccs + the Create Room ARTCC picker; SetActiveArtcc — UserPreferences.ArtccId is the ARTCC in effect (home at sign-in, the room's while in a room)
  LoadOverlayViewModel.cs       # The scenario-load progress overlay's state (LoadStepViewModel rows): LoadId/Sequence ordering, result-vs-event precedence, close rules; owned by MainViewModel as LoadOverlay
  MainViewModel.Aircraft.cs     # Partial: aircraft management (spawn/delete/update), terminal broadcast handling, and PilotTransmissionBroadcast gate to PilotVoiceService.
  MainViewModel.Scenario.cs     # Partial: scenario load/unload/restart; the load overlay and room-loading gate (OnScenarioLoadProgress, OnRoomLoadingChanged, RoomLoadingBy/IsRoomLoading, ReportLoadFailure); the pre-send parse (ScenarioIdentity, ScenarioSetupPlan, FilterByDifficulty) runs under Task.Run. Load+Unload are mentor-only (CanLoadScenario/CanUnloadScenario gate on IsNonMentor); Restart is open to any room member. Rejections raise a terminal warning via ReportScenarioActionFailure, not just StatusText. ClearScenarioState (unload, and via MainViewModel.Rooms.cs's ClearRoomState on Leave Room) also empties every open Strips/TDLS view — StripsEntries via VStripsViewModel.ApplyBayConfig(null) (primary + secondary VM) and TdlsEntries via VTdlsViewModel.Clear() — since strips/PDCs are pushed state nothing else retracts. Also the Discord rich-presence publish: RichPresence (IRichPresencePublisher?, assigned by MainWindow, null in headless hosts), StartRichPresence(elapsedSeconds) at the end of ApplyScenarioBootstrap and of MainViewModel.Timeline.cs's ApplyRecordingResult, RefreshRichPresence on each Settings Apply and a late scenario name, cleared in ClearScenarioState. CanExportRoomAsScenario (CanLoadScenario + at least one aircraft, wired by WireScenarioExportAvailability off PropertyChanged/Aircraft.CollectionChanged) gates Export Room as Scenario; ExportRoomAsScenarioAsync calls the hub, saves the returned JSON via IFilePickerService, and returns the aircraft still needing preset commands or deletion for MainWindow to show in ScenarioExportReviewWindow.
  MainViewModel.ArrivalGenerators.cs # Partial: live arrival-generator editing (open editor window, push edits to sim, Save As)
  MainViewModel.HoldForRelease.cs # Partial: hold-for-release rundown mirror + REL release commands (HeldDeparturesChanged handler, RoomStateDto.Rundown seed)
  MainViewModel.Timers.cs       # Partial: TIMER countdown mirror + cancel command (TimersChanged handler, RoomStateDto.Timers seed, command-bar timers panel)
  MainViewModel.Weather.cs      # Partial: weather load/clear commands + WeatherChanged handler; retains raw METARs (Metars) for the METAR window, sorted per-scenario favorites first then alphabetical
  MainViewModel.LiveSession.cs  # Partial: live-traffic sessions — IsLiveSession mirror (from LoadScenarioResult/ScenarioLoaded/RoomState), LIVE/PAUSED/PLAYBACK badge + Go Live command, StartLiveSessionAsync (result applied like a scenario load, then live weather), CanStartLiveSession
  MainViewModel.PilotVoiceWarning.cs # Partial: solo missing-pilot-voice warning — banner flag, once-per-session resume modal (ConfirmResumeAsync gates the Pause button, typed UNPAUSE and timeline play), session reset on scenario/recording load and room change; see docs/solo-training-pilot-speech.md
  MainViewModel.Controllers.cs  # Partial: online-controller list (OnlineControllers + CRC-grouped ControllerGroups) for the Controllers tab; refresh via GetOnlineControllers, re-fetched on CRC membership + scenario load/unload
  MainViewModel.ActivePosition.cs # Partial: active-position (TCP) indicator/dropdown in the input bar (ActiveTcp + ActiveTcpOptions); seeded from the bootstrap PositionDisplayConfig, follows PositionDisplayChanged (standalone AS only), picking a TCP sends AS [TCP]
  MainViewModel.Favorites.cs    # Partial: favorite commands (quick-access bar/panel, ground overrides, blank spacers) over FavoriteStore — DisplayFavorites of FavoriteDisplayEntry (favorite + container id), membership-based Add/Update/Delete, container-scoped reorder/blank-insert, FavoriteContainerOption picker rows (on-demand airport/scenario containers), zip import/export passthroughs
  FavoriteSetEditorModel.cs     # Pure list surgery behind the Favorites Editor: generic multi-select MoveUp/MoveDown (contiguous-block aware) over a set's ordered id list
  MainViewModel.Timeline.cs     # Partial: rewind timeline markers — color-coded finding ticks (red Safety, amber Warning, blue Coach) + grey command ticks; periodic refresh, click-to-rewind (every jump refused while IsRoomLoading: CanRewind on the six jump commands, and RewindToSeconds itself), hover details, per-aircraft filter from the Session Report Aircraft tab. Also save/load recording (injects/reads the bookmarks.json archive entry); ApplyRecordingResult is a scenario-identity writer outside the bootstrap router, so it makes its own StartRichPresence call. Also the shared bug-report bundle writer: FetchRecordingForBundleAsync (pause + export with progress) and WriteBugReportBundleAsync (recording entries with bookmarks folded in + client/server logs), used by Save Bug Report Bundle and File Bug Report.
  MainViewModel.SpeechTelemetry.cs # Partial: the one-time speech-telemetry opt-in — SpeechTelemetryPrompt (set by the view; null in headless hosts means never prompt), OfferSpeechTelemetryIfDueAsync (raised on first speech enable and once on window open, never two dialogs at once), and the pending-sample upload against the connected server.
  MainViewModel.BugReport.cs    # Partial: Scenario → File Bug Report — BugReportPrompt (view-supplied form), writes a bundle under %LOCALAPPDATA%/yaat/bug-reports/ (recording + logs in a room, client log alone otherwise), opens the prefilled GitHub issue and reveals the bundle via FileReveal.
  MainViewModel.Bookmarks.cs    # Partial: shared server-synced timeline bookmarks (Bookmarks mirror, add/quick-add/rename/delete via hub RPCs, ApplyBookmarks from BookmarksChanged broadcast/RoomStateDto seed, name-prompt event, SnapshotBookmarks for recording save). Also the client half of the BM verb: TryHandleBookmarkLocallyAsync (LIST query + GO/NEXT/PREV seeks); mutations go to the server via the command pipeline.
  MainViewModel.CrcAliases.cs   # Partial: CRC alias support — CrcAliasStore instance + BuiltInDotCommands (names YAAT's own dot commands reserve: scope markers, .rbl/.norbl, .reloadaliases), LoadCrcAliasesAsync (startup, ARTCC change, Settings Apply, .reloadaliases), TryHandleCrcAlias/RunCrcAlias, BuildCrcAliasContext (SelectedAircraft flight plan for $dep/$arr/$route/$fullroute). Client-only; never reaches the server.
  MainViewModel.ConflictAlerts.cs # Partial: terminal conflict-alert pairs (ApplyConflictAlerts from ConflictAlertsChanged broadcast/RoomStateDto seed, projected onto AircraftModel.ConflictPeerCallsign for both members; SeedConflictPeer covers an aircraft appearing after the broadcast). Not carried on AircraftDto — see docs/radar-rendering.md § Conflict alerts.
  MainViewModel.AtpaResults.cs  # Partial: ATPA in-trail pairs (ApplyAtpaResults from the AtpaResultsChanged broadcast/RoomStateDto seed, projected onto the trailing aircraft's AtpaLeadCallsign/AtpaAllowedSeparationNm/AtpaConeState; SeedAtpaResult for late-added aircraft). Same never-on-AircraftDto rule — see docs/radar-rendering.md § ATPA cones.
  MainViewModel.ViewInstances.cs # Partial: extra Radar/Ground windows (#434) — ExtraRadarViews/ExtraGroundViews, the CreateRadarViewModel/CreateGroundViewModel factories, OpenExtra*/CloseExtra*/ReconcileExtraViews, late seeding from the stashed bootstrap/position config, AllRadarViews/AllGroundViews fan-out enumerators. One app-wide SelectedAircraft. See docs/client-mainviewmodel.md § Extra view instances
  MapViewInstance.cs            # RadarViewInstance / GroundViewInstance (ordinal ≥ 2 + its own view-model) and ViewInstanceOrdinals (RadarView#n / GroundView#n geometry keys, lowest free ordinal)
  MainViewModel.Strips.cs       # Partial: multi-facility strips tabs (StripsEntries) — open/close per-facility entries (same facility may be opened twice; duplicate tabs disambiguated via DuplicateOrdinal " #n" title suffix), split/unsplit (SplitStripsEntryAsync creates the entry's SecondaryVm; split mode + ratio persist for the student entry), strips zoom fan-out + persistence
  VStripsDockEntryViewModel.cs  # One strips tab/window entry: facility-scoped VStripsViewModel + pop-out flag + TabTitle (facility name + duplicate suffix) + split state (SplitMode / SplitRatio / SecondaryVm)
  StripsSplitMode.cs            # Split layout of a strips entry: None / SideBySide / Stacked — named by pane arrangement because "split horizontally" is ambiguous
  TimelineMarkerVm.cs           # Per-marker view-model: timestamp, kind, severity, title, callsign, canonical command (commands only).
  TimelineBookmarkVm.cs         # Per-bookmark view-model (editable Name, gold rail tick, Rename/Delete/Jump commands delegating to MainViewModel callbacks).
  AutoClearedToLandSync.cs      # Subscribes to UserPreferences.AutoClearedToLand changes; pushes the new value to every aircraft (local + room-broadcast) so the toggle takes effect mid-session without a scenario reload.
  DataBlockViewState.cs         # Session-persistent per-callsign datablock state (manual offsets, highlights, hide/show, minified, z-order) owned by Ground/RadarViewModel and bound into the canvases — survives tab switches and pop-outs (#350)
  GroundViewModel.cs            # Ground view; loads layout, A* pathfinding, commands
  GroundViewModel.Measure.cs    # Partial: distance measuring tool on the ground view (feet below a mile)
  RadarViewModel.cs             # Radar view; video map loading, toggle items, DCB, persistence
  RadarViewModel.Measure.cs     # Partial: distance measuring tool on the radar (nautical miles)
  RangeBearingViewState.cs      # Observable mirror of the one RangeBearingLineStore behind both map views (shared slot pool, per-view visibility); owns the tool's behaviour
  ImportExportViewModel.cs      # The Import / Export hub: Export tab (ticked item types → one bundle, or a single item's own file), Import tab (bundle preview, Merge/Replace per type, clash rows with Skip/Overwrite/Rename, result summary) over an ISettingsImportTarget and ISettingsExportSource; the favorites row exports the library or one set; each import row's EffectText says what the chosen mode does, and only rows offering both modes show the Merge/Replace combo; built as ImportExportViewModel(target, source, ImportExportOpening, ImportExportFiles, UserPreferences); while BackUpFirst is ticked (remembered as UserPreferences.BackUpSettingsBeforeImport) Import first writes every item type from the source to <BackupsFolder>/settings-backup-<yyyyMMdd-HHmmss>.yaat-settings.zip (-2, -3 on a same-second clash; named from the injected TimeProvider) and imports nothing if that fails; every export and backup is written to a temp file through ImportExportFiles.CreateFile (File.Open CreateNew) and moved into place
  SettingsViewModel.Bundle.cs   # Partial: Settings' side of the hub — StageMacros/StageVerbs/StagePreferences/StageLayouts/StageGridLayout/StageFavoritesWrite, committed by Apply (LastApplyCommittedGridLayout tells MainWindow to re-apply the live grids) and dropped by Cancel; Staged* getters and ExportStagedPreferences are what an export from Settings reads
  SettingsViewModel.QuickCommands.cs # Partial: the Quick Commands section's staged state — situations, entry rows (catalog or custom, flight rules, TypedCommandText validation, strip mark by position), the Add command… offer (unlisted eligible actions, searched, by family), add/remove/move, staged resets; Apply writes changed situations (SetQuickCommandList); custom rows revalidate on macro or verb edits; any error blocks Apply (CanApply, ApplyBlockedSummary)
  SettingsViewModelImportTarget.cs # The hub's ISettingsImportTarget inside Settings: stages every item in the SettingsViewModel; favorites import into a staged copy of the store (under StagedFavoritesFolder, owner-PID marker, crash leftovers swept) replayed into the real store on Apply; disposed when Settings closes
  SettingsViewModelExportSource.cs # The hub's ISettingsExportSource inside Settings: exports what the window shows (unapplied edits and staged imports included); SelectedMacros narrows a macros export to Export Selected…'s rows
  SettingsSectionId.cs          # The Settings window's sections, one per sidebar entry (General … Server admin); MainWindow.ShowSettingsDialogAsync opens at one
  SettingsViewModel.cs          # Every Settings value, organised by section: loads at construction, Apply (ApplyCommand) commits and raises Applied (repeatable; only changed always-on-top values are written), ResetSection resets the selected section to UserPreferences.CreateDefaults as a pending edit (one Reset* method per section; link-only sections disable it); includes STT/TTS model download flows, which act at once. LM-Kit catalogs + GPU probe load via LoadModelCatalogsAsync (Task.Run) — never at construction; see the deadlock note below.
  WeatherPeriodViewModel.cs     # Per-period VM: wind layers, METARs, precipitation, start/transition minutes
  WeatherTimelineEditorViewModel.cs  # Timeline editor VM: period list, BuildJson (v1 if 1 period, v2 if 2+), FromJson
  ArrivalGeneratorsEditorViewModel.cs # Aircraft generator editor VM: three row lists, Apply (push to sim), Save As (write scenario JSON)
  GeneratorRowViewModel.cs      # Per-row VM for the IFR-arrival tab: runway/type/rate/AutoTrack/Active fields
  VfrArrivalGeneratorRowViewModel.cs # Per-row VM for the VFR-arrival tab: bearing arc/alt band/direct-to/initial V-S/Active
  OverflightGeneratorRowViewModel.cs # Per-row VM for the overflight tab: from/to arcs, exit distance, hemispheric snap, Active
  *Converter.cs                 # IValueConverters for UI bindings (Dock, Pause, SuggestionKindColor, SignatureHelp, RunwayDisplay, TypeMismatchBrush for the Aircraft List's Filed column)

Views/
  MainWindow.axaml              # Main window layout (View menu: Windows / Bars / Layout submenus, gesture text set by ShowMenuHotkeys); the export-recording overlay and the scenario-load overlay (LoadOverlay's step rows and Close button) sit over the main panel
  MainWindow.axaml.cs           # Tab layout (DataGrid/Ground/Radar); room bar; pop-out management; constructs DiscordRichPresenceService only when App.DiscordRichPresenceAvailable (set by Program.Main, so headless hosts never open the pipe) and disposes it in OnClosing; Tools › Import / Export… (the hub, nothing ticked) and OnSettingsImported (live views follow an import made outside Settings); ShowSettingsDialogAsync records the focused element of the active window and RestoreFocusAfterSettings puts focus back on close (the command input when it is gone or in a menu); ShowColumnChooserAsync
  ScenarioExportReviewWindow.axaml.cs # Lists the aircraft an Export Room as Scenario run could not restart in the same situation, each with its reason, so the author knows which to give preset commands or delete; Copy puts the list on the clipboard as `CALLSIGN — reason` lines
  VStrips/VStripsSplitHost.cs   # Hosts one strips entry's pane layout: a single VStripsView, or two full views around a GridSplitter when the entry is split; splitter drags write back the entry's SplitRatio. Docked TabItems and popped-out VStripsViewWindows both host this.
  CommandInputView.axaml.cs     # Keyboard: Esc/Up/Down/Tab/Enter for suggestions/history
  FavoritesBarView.axaml.cs     # Favorite command buttons bar and tabbed panel content (click/ctrl+click/right-click); Sets load/unload flyout (bar + palette header); add/edit flyouts with the "In" membership checkboxes (every container as a peer; shared entity, not copies); palette-header Import/Export, which open the Import / Export hub with Favorites ticked (ImportExportWindow.ShowLiveAsync)
  FavoritesPanelWindow.axaml.cs # Pop-out favorite commands panel with saved geometry
  FavoritesEditorWindow.axaml.cs # Favorites Editor (Sets → Manage sets…): container pane (Global + airports + scenarios + named sets with Loaded checkboxes + "Not in any set" orphans; named-set create/rename/delete) + multi-select favorites pane (move up/down, add-to/move-to another set, remove-from-set, delete everywhere)
  FavoriteSetNameDialog.axaml.cs # Name-entry dialog for creating/renaming a favorite set; Save disabled on case-insensitive collision (sets never overwrite)
  ExtraViewAirportDialog.axaml.cs # Base-airport prompt for View > New Radar/Ground Window: the ARTCC's airports (scenario primary preselected) or any nav-db airport by id; OK gated on the caller's isKnownAirport
  ControllersView.axaml.cs      # Controllers tab content: CRC-style facility-grouped list (handoff id / position name / freq) over MainViewModel.ControllerGroups
  ControllersWindow.axaml.cs    # Pop-out host for ControllersView (View > Pop Out Controllers)
  MetarView.axaml.cs            # METAR tab content: per-airport METAR list over MainViewModel.Metars with a per-scenario favorite-station star toggle
  MetarWindow.axaml.cs          # Pop-out host for MetarView (View > Pop Out METAR)
  FavoritesContextMenu.cs       # Builds the Favorite Commands submenu attached to aircraft right-click menus (list/ground/radar)
  ClientMenuHost.cs             # The IMenuHost the radar, ground and aircraft-list views build per right-click over MainViewModel (SendCommandForViewAsync, popups at the pointer on the anchor, session, favorites, ground traffic and the ground-movement answers, Draw taxi route); SetRoutePreview shows a route on every ground view (main.AllGroundViews)
  LiveTrafficDvrFlyout.cs       # Click-the-live-badge DVR control: feed log window (GetLiveTrafficWindow), slider + HH:mm → SeekLiveTraffic, Go Live
  LiveSessionWindow.axaml.cs    # Start Live Session picker: facility TreeView (GetArtccFacilityTree) → positions (starred first) → airport combo (LiveSessionAirportDefaults) + ceiling; returns LiveSessionChoice, pre-selects UserPreferences.LastLiveSession
  LiveTrafficFilterEditor.axaml(.cs) # Structured editor UserControl over the canonical filter string, hosted by the Start Live Session Filters tab and the mid-session dialog
  LiveTrafficFilterWindow.axaml(.cs) # Mid-session filter dialog opened from the session-settings flyout
  AssumeLiveTrafficWindow.axaml(.cs) # Bulk-assume dialog (all / within radius of airport-fix-FRD, flight-rules filter) from the session-settings flyout → AssumeLiveTraffic hub call
  FavoritesContextMenuModel.cs  # Pure model behind FavoritesContextMenu: resolves active favorites against the clicked aircraft for headless tests
  DataGridView.axaml.cs         # Aircraft data grid (extracted from MainWindow)
  DataGridWindow.axaml.cs       # Pop-out data grid window
  ColumnChooserWindow.axaml.cs  # Aircraft List column chooser over a ColumnChooserState (opened by MainWindow.ShowColumnChooserAsync); Import/Export open the hub with the column layout ticked: an imported column layout stages in the chooser's rows (OK applies, Cancel drops), other items apply at once (LiveImported)
  TerminalPanelView.axaml.cs    # Auto-scroll with user-scroll detection
  TerminalWindow.axaml.cs       # Pop-out terminal (shares MainViewModel)
  SettingsWindow.axaml.cs       # Modal Settings: sidebar of sections, the selected section's view, Reset section and OK/Apply/Cancel footer; SelectSection(SettingsSectionId), section-link buttons that jump between sections, key capture for every key-capture button (Enter/Escape/Space are bindable while capturing); modal over every window in OpenWindows without disabling them: tunnel handlers drop pointer/key/text input (a press brings Settings forward) and Tapped/DoubleTapped/RightTapped/Holding class handlers stop gestures in the blocked windows, all removed in OnClosed; OpenImportExport opens the hub over a SettingsViewModelImportTarget so imports stage until Apply/OK
  Settings/SettingsNavigation.cs # Sidebar rows in display order: group headers (General, Session, Views, Input, Voice, Advanced) and their sections (SettingsNavItem, whose MatchCount is the search badge)
  Settings/SettingsSearchCatalog.cs # Search catalog: one entry per bound control (section, label, Within heading, aliases) and per section-link button; SettingsWindowSourceTests keeps it in step with the section .axaml
  Settings/SettingsSearch.cs    # Pure search matcher over the catalog (every word against label, heading, section title, aliases) → per-section counts and ordered matches
  Settings/*Section.axaml(.cs)  # One UserControl per section (GeneralSection … ServerAdminSection), sharing the window's SettingsViewModel; Aircraft list and Strips and vTDLS hold only links
  Settings/QuickCommandsSection.axaml(.cs) # Quick commands section: situation list (● changed, ⚠ to fix), menu preview through QuickCommandStrip's factories, entry rows with the divider where the icon strip ends (QuickCommandStripDivider), drag reorder by the ≡ handle (Escape cancels), the Add command… flyout (search, family headers, Add/Enter/double-click)
  ImportExportWindow.axaml.cs   # The Import / Export hub window (geometry key "ImportExport"; file pickers, tabs over ImportExportViewModel). ShowLiveAsync opens it on the live preferences and favorites (Tools menu, favorites panel) and raises MainViewModel.NotifySettingsImported with what it applied; ShowOverAsync returns the applied item types
  OpenWindows.cs                # Weak registry of the app's open windows (WindowOpened/WindowClosed class handlers, registered in App.Initialize); works without a desktop lifetime, so Settings reads it to block every other window
  MessageBoxPresenter.cs        # ShowStandardAsync / ShowCustomAsync: the one seam for MsBox.Avalonia message boxes; builds the package's MsBoxWindow and opens it through DialogPresenter, so in automation mode boxes are never-activated and non-modal (AutomationModeSourceTests rejects MsBox use elsewhere)
  SpeechTelemetryOptInDialog.axaml(.cs) # One-time offer to send push-to-talk recordings to the YAAT developers; returns the accept/decline answer to MainViewModel.SpeechTelemetryPrompt
  FileBugReportDialog.axaml(.cs) # Scenario → File Bug Report form (title, what happened, expected, callsigns); returns a BugReportForm to MainViewModel.BugReportPrompt
  LoadWeatherWindow.axaml.cs    # Weather profile picker modal (folder scan, name + layer count)
  WeatherEditorControl.axaml.cs # Per-period weather editing UserControl (precipitation, wind layers grid, METARs)
  AboutWindow.axaml.cs          # Help → About dialog: version, build kind, .NET runtime, log path, GitHub link, optional Ko-fi support link
  WeatherTimelineEditorWindow.axaml.cs  # Timeline editor: period list (left) + WeatherEditorControl (right); v1/v2 auto-format on save
  ArrivalGeneratorsEditorWindow.axaml.cs # Live arrival-generator editor: row grid + Apply (push to sim) / Save As (new scenario JSON)
  SessionReportWindow.axaml.cs  # Live solo-training session report: score, coaching notes, separation timeline, approach/runway grids, per-aircraft debrief tab with "Show on Timeline" cross-link
  TimelineMarkerCanvas.cs       # Panel that arranges marker children along the rewind scrub slider by their attached Time against MaxTime; MainWindow's timeline bar uses it as the items panel for the bookmark row and the finding/command marker row, setting Time on each item container. TimelineMarkerVisuals holds the marker brushes.
  ManageLayoutsDialog.axaml(.cs)         # View → Layout → Manage layouts…: list saved layouts, apply, update from current, rename, delete
  SaveLayoutDialog.axaml(.cs)            # Name-entry dialog for saving the current window arrangement as a new layout
  ApplyLayoutDialog.axaml(.cs)           # View → Layout → From this scenario's views…: source picker (scenario views or saved layout), grouped Current-vs-Source diff with per-section checkboxes (every pop-out row), airport-mismatch warning. Returns selected keys for MainWindow to apply via ViewSettingsCopyCatalog / LayoutService.
  CommandFlyout.cs              # Floating focused command-entry popup opened from aircraft right-click menus (radar/ground/flight list)
  MenuPopups.cs                 # The one popup service every menu host calls: code-built input (initial text + caret; trims, and a blank submit closes or hands "" to the callback per the caller's BlankInput), list, filtered list, warp (WarpSeed), command and note flyouts, each opened at the pointer in the anchor's overlay layer
  ContextMenuExtensions.cs      # Helpers for building Avalonia context menus (right-click submenus, command items)
  FlightPlanEditorWindow.axaml.cs # Built-in flight plan editor window: view/amend fields + route, recycle beacon, live squawk refresh; raises an amend callback per edit; the SPD box holds knots only and shows a Mach/classified speed as its placeholder
  FlightPlanEditorManager.cs    # Static single-instance opener/lifecycle for the flight plan editor window (reuses one open editor)
  FlightPlanEditorAmendmentBuilder.cs # Builds a FlightPlanAmendment from the built-in flight plan editor's field/route edits for dispatch to the server
  HoldShortMenuHelper.cs        # Shared resolver: held runway from the "Holding Short {rwy}" phase, used by ground-map + aircraft-list cross/LUAW menu items
  ViewSettingsMenu.cs           # The shared "Settings for this view…" item the Radar, Ground, Aircraft list and Terminal right-click menus end with (MainViewModel.RequestSettings)
  WindowHotkeys.cs              # App-wide class handler for window-level hotkeys (focus command input, always-on-top, the pop-out and favorites-bar toggles, Open Settings, Ctrl+F8 radar DCB toggle; FixedChords lists the non-rebindable chords the Keys clash check covers); routes focus to the visible CommandInputView and toggles topmost via IAlwaysOnTopToggle

Views/Map/
  MapViewport.cs                # Shared equirectangular projection for map views
  MapCanvasBase.cs              # ICustomDrawOperation base + pan/zoom input handling
  TextStyle.cs                  # Paired (SKFont, SKPaint) for measuring + drawing text; keeps draw and hit-test metrics identical
  DatablockDeconfliction.cs     # Pure opt-in datablock overlap resolver shared by radar + ground (snap / free-form)
  RangeBearingLines.cs          # Distance measuring tool (CRC STARS *T): endpoints, 15-slot store (lines tagged per RblView — radar/ground each render only their own), label formatting, resolver, readout anchor + viewport clamp (RblLabelPlacement), hit-test
  RblReadoutPlacement.cs        # Places each range/bearing readout at the least-covered of eight spots around its anchor and nudges auto-placed datablocks clear when every spot is covered; shared by radar + ground canvases
  RangeBearingRenderer.cs       # Draws measurement lines + readouts at the rects RblReadoutPlacement chose; shared by radar + ground renderers
  RightClickGesture.cs          # Right-button click-vs-drag tracker shared by radar + ground: menu on release-without-drag, pan otherwise
  RightClickTarget.cs           # One thing a right click hit (aircraft or parking/spot/helipad node) and its picker label
  RightClickPicker.cs           # Small menu listing several right-click targets; choosing one raises that target's own right-click event

Views/Ground/
  GroundView.axaml.cs           # Ground view control with context menus + docked toolbar (layer/label toggles, hidden-scrollbar horizontal ScrollViewer); OnToolbarPointerWheelChanged wheel-scrolls when toolbar is narrower than content
  GroundViewWindow.axaml.cs     # Pop-out ground window with enforced minimum dimensions; takes a geometry key + title and hosts a GroundViewModel via SetViewModel (the primary pop-out uses "GroundView"/vm.Ground, an extra instance GroundView#n/its own VM); window DataContext stays MainViewModel
  GroundCanvas.cs               # SkiaSharp canvas with StyledProperties + hit-testing
  GroundRenderer.cs             # Stateless SkiaSharp ground renderer (3 layers: satellite, video map, YAAT layout); skips never-driven >155° hairpin fillet arcs; DrawAdwMarks strokes the server-resolved ADW reference ticks (ADW toolbar toggle); DrawTug draws the towbar + tug body off a grounded aircraft's AircraftModel.TowbarHeading, skipped below MinTugPx
  RunwayRectangle.cs            # Static ScreenCorners(GroundRunwayDto, MapViewport): a runway's painted rectangle in screen space, shared by GroundRenderer.DrawRunways and GroundCanvas's runway-surface hit test

Views/Radar/
  RadarView.axaml.cs            # Radar view control with DCB (range, map shortcuts, FIX, LOCK)
  RadarView.ContextMenus.cs     # Partial: the radar's right-click menus: the aircraft menu through AircraftMenuBuilder with the radar's view section (Display ▸, Draw route), and the map point menu (BuildMapPointMenu: the selected aircraft's point items, then FRD, markers, Measure and MVA)
  RadarView.Popups.cs           # Partial: the waypoint-condition popup (the menu pickers are Views/MenuPopups.cs)
  RadarViewWindow.axaml.cs      # Pop-out radar window; takes a geometry key + title and hosts a RadarViewModel via SetViewModel (primary: "RadarView"/vm.Radar; extra instance: RadarView#n/its own VM, exposed as RadarVm for the Ctrl+F8 hotkey); window DataContext stays MainViewModel
  RadarCanvas.cs                # SkiaSharp canvas with pan/zoom lock
  RadarRenderer.cs              # Stateless SkiaSharp radar renderer
  RadarDatablockLayout.cs       # Datablock line/field layout (EuroScope tag + standard datablock geometry shared with renderer + click hit-testing)
  EuroScopeTagLayout.cs         # TagFieldId + per-field EuroScope tag rects for hit testing and flyout dispatch
  VideoMapRenderer.cs           # Video map line/label rendering
  TargetRenderer.cs             # Aircraft target/datablock rendering
  Flyouts/
    FlyoutAppearance.cs         # Shared visual styling for tag flyouts (altitude, speed, runway, scratchpad popups)
```

## Yaat.VStrips.Web — Browser strip client (`tools/Yaat.VStrips.Web/`)

WebAssembly Avalonia client for flight strips. Hosted by yaat-server at `/vstrips/` so users open it in any browser without an install. References only Yaat.Client.Strips (no Avalonia.Desktop, no Velopack, no file IO) so the WASM publish closure stays small. Identity (CID, initials, ARTCC) flows in via URL query — first-time visitors fill a landing form that redirects with the params filled in.

```
Program.cs                       # Entry point. Wires SimLog to ConsoleLineLoggerProvider (browser DevTools console). Stores window.location.search + window.location.origin on App so MainView can decide live-connect vs. offline spike.
App.axaml(.cs)                   # XAML app root. Holds LocationSearch/LocationOrigin static strings populated by Program.Main.
MainView.axaml(.cs)              # Root view. Hosts VStripsView, handles auto-join via BrowserStripsTransport, surfaces the missing-identity landing form.
wwwroot/index.html               # WASM host page; loaded by yaat-server's static-file middleware at /vstrips/.
wwwroot/main.js                  # Boot script — passes window.location.search + origin into the WASM Main args.
wwwroot/app.css                  # Page chrome (status footer, landing form).
runtimeconfig.template.json      # net10.0-browser runtime config (JsonSerializerIsReflectionEnabledByDefault=true so SignalR JoinRoom works in WASM).
test/smoke.mjs                   # Headless WASM smoke test (Playwright).
test/live.mjs                    # Live-server smoke test against a running yaat-server instance.
```

## Yaat.VTdls.Web — Browser vTDLS client (`tools/Yaat.VTdls.Web/`)

WebAssembly Avalonia client for the vTDLS view. Hosted by yaat-server at `/vtdls/` (mapped in `ServerApp`). Mirrors the VStrips.Web shape: references Yaat.Client.Tdls + Yaat.Client.Strips (for the shared JetBrains Mono font registration), no Avalonia.Desktop / Velopack / file IO. Identity (CID, initials, ARTCC) flows in via URL query — first-time visitors fill a landing form that redirects with the params filled in.

```
Program.cs                       # Entry point. Wires SimLog to ConsoleLineLoggerProvider. Stores window.location.search + origin on App.
App.axaml(.cs)                   # XAML app root with the Light theme variant (upstream vTDLS is light-themed) + SubtleTextBrush/MonoFont resources.
MainView.axaml(.cs)              # Root view. Hosts VTdlsView, handles auto-join via BrowserTdlsTransport, calls RefreshAccessibleFacilities + SwitchFacility on the first facility after JoinRoom.
wwwroot/index.html               # WASM host page; landing form (DOM-only, no innerHTML); gates the WASM boot on identity params; localStorage key `yaat-vtdls-identity`.
wwwroot/main.js                  # Boot script.
wwwroot/app.css                  # Page chrome (status footer + landing form).
runtimeconfig.template.json      # net10.0-browser runtime config.
```

## Yaat.Sim — Shared simulation library (`src/Yaat.Sim/`)

No UI deps. Deps: Google.Protobuf, Microsoft.Extensions.Logging.Abstractions.

```
# Core
AircraftState.cs               # Mutable aircraft entity. Identity + kinematics flat at top; cohesive
                               # state grouped into sub-objects (FlightPlan, Transponder, Ground, Track,
                               # Stars, Eram, Approach, Procedure, Pattern, Clearance, HoldAnnotation,
                               # Ghost, DataBlock, Voice). DataBlock = STARS Track Reposition (TRK RPOS)
                               # surveillance/datablock split: Parked emits a second wire StarsTrackDto.
                               # Each sub-object owns its own ToSnapshot/FromSnapshot pair
                               # with a matching DTO under Simulation/Snapshots/.
                               # DeclinationCachePosition (LatLon?): null = "not cached", not serialized.
                               # Ground.Layout is [JsonIgnore]; Ground.LayoutAirportId preserves the
                               # reference so archive restore can reattach.
                               # PendingObservations: ephemeral pilot-side "watch for condition" state (not persisted in snapshots)
                               # WindSpeedKts: computed wind magnitude (sqrt of N²+E² components) used for pattern flyability floor
                               # LiveTraffic (AircraftLiveTraffic?): non-null ⇔ IsShadow — a real aircraft mirrored from a feed,
                               # driven by LiveTraffic/LiveTrafficKinematics instead of FlightPhysics. See live-traffic.md.
                               # AssumedFromLiveTraffic (bool, snapshotted, default false): set by LiveTrafficAssumer.Assume as LiveTraffic is cleared —
                               # the marker UNASSUME requires (a scenario aircraft with the same callsign is not restorable)
                               # FOOTGUN: changes here must be mirrored in AircraftSnapshotDto + SnapshotSchemaMigrator
ControlTargets.cs              # Autopilot targets: heading, altitude, speed (IAS), NavigationRoute
                               # DesiredVerticalRate (phase/instructor) vs PlannedVerticalRate (FlightPhysics step-climb/descent
                               # planners, recomputed and cleared every tick): DesiredVerticalRate wins when both are set.
                               # PlannedVerticalRate is deliberately absent from ControlTargetsDto — re-derived on the first
                               # tick after a restore, so it's an exception to the "mirror in ControlTargetsDto" footgun below.
AircraftGroundOps.cs           # Ground sub-object of AircraftState (AircraftState.Ground; snapshot AircraftGroundOpsDto): layout reference ([JsonIgnore] Layout + LayoutAirportId), assigned taxi route, parking spot and current taxiway, hold directive, auto-delete flags, expedite/commanded taxi speed, ground-conflict speed limit and auto-yield, runway queue position, pushback/towbar headings and ForcedTowIgnoresParked,
                               # the solo initial call-up (InitialCallup plan, InitialCallupDecisionProcessed, SpawnTaxiway, PushedBackFrom, PushEndSpot, PresetTaxiStop, VfrDepartureDirection), taxi-in call (AwaitingTaxiInCall, ReleasedToGround),
                               # hold-for-release (HeldForRelease, ReleasedForDeparture, ReleasedAtSeconds, ReleasedAtSpawnGate) and the CFR window (ReleaseWindowStartUtc/EndUtc)
AircraftPattern.cs             # Aircraft pattern state; PendingLandingClearance carries PatternRunwayId / PatternAltitudeFt for runway-change clearance routing
NavRouteFixDto.cs              # Wire record (Name/Lat/Lon/RestrictionLines) for the client "Show nav route" overlay; carries server positions + pre-formatted crossing-restriction labels. Empty Name = synthetic arc vertex. Referenced by both AircraftStateDto (server) and AircraftDto (client)
NavRouteShapeDto.cs           # Wire record (Kind/Points/Labels) for active-procedure geometry on the "Show nav route" overlay: hold racetracks, procedure turns, and open-ended SID coded-leg vectors — paths the flat NavRouteFixDto route can't express. NavRouteShapeKind styles the shape
NavRouteOverlayProjector.cs   # Projects an aircraft's active phase (HoldingPatternPhase/ProcedureTurnPhase/DepartureProcedurePhase) into NavRouteShapeDto shapes (racetrack, PT barb, chained coded-leg vectors w/ restriction labels). Pure geometry; fed into AircraftStateDto.NavRouteShapes by DtoConverter
ProcedureLeg.cs                # Typed ARINC-424 procedure leg (path terminator + course/altitude/turn, + DME/along-track distance or radial termination for CD/VD/FD/FC/CR/VR) flown by DepartureProcedurePhase; built by ProcedureLegResolver
                               # NavigationTarget: Position (LatLon) + optional AltitudeRestriction + SpeedRestriction (for SID/STAR via mode); IsSyntheticArcName(name) flags ARCnn arc-densification vertices
                               # TargetMach: when set, UpdateSpeed recomputes equivalent IAS each tick (Mach hold)
LatLon.cs                      # Readonly record struct: public LatLon(double Lat, double Lon). The canonical coordinate type
LatLonBounds.cs                # Internal: axis-aligned lat/lon bbox pre-filter shared by AirspaceVolume + MvaSector (O(1) reject before ray-cast)
                               # across Yaat.Sim / Yaat.Client / yaat-server. Field names match CRC Point DTO. No implicit tuple conversion
                               # (forces explicit `new LatLon(lat, lon)` at external-JSON boundaries so argument swaps don't slip through)
Callsign.cs                    # Static IsValid(string?): regex ^[A-Z0-9\-]{1,7}$. Boundary check used by STARS DA/VP/FP creation
                               # to reject typos like "*T <fix>" before they create stray flight plans.
PlannedAltitude.cs             # Value type for the filed flight-plan altitude (notation axis): single / block / VFR /
                               # VFR-on-top / above, in feet. Mirrors vNAS common/ParsedAltitude minus RawValue. Distinct from
                               # ControlTargets.AssignedAltitude (current ATC clearance) and FlightRules (IFR/VFR rules axis).
                               # UntilFix(feet, fix, afterFixFeet) builds ERAM's fix-qualified form (AltitudeFix / AfterFixFeet,
                               # init-only; data only — AircraftFlightPlan's latch picks the altitude in effect for Field B and QF).
AircraftFlightPlan.cs          # Flight plan sub-object of AircraftState; FormatSpeedField(knots, mach, classified) writes the filed speed as ERAM does (SC / Mddd / knots / empty), shared by the client displays and the server's ERAM speed readout
FlightPlanAltitude.cs          # Parser + formatter for the CRC altitude grammar used in FP forms and STARS DA/VP:
                               # `VFR` (rules-only), `VFR/045` / `OTP/120` (rules + altitude), `045` (IFR + altitude), blank.
                               # Parse → (Rules, PlannedAltitude); Format(PlannedAltitude) → text (incl. block NNNBNNN). OTP is
                               # VFR rules + VFR-on-top notation. FromRulesAndFeet builds a PlannedAltitude from rules + a plain int.
                               # Also `A170` (above) and `170/SJC/110` (fix-qualified; ParseFixQualified is the one grammar ERAM
                               # AM ALT uses too, through Data/EramFixResolver).
Data/EramFixResolver.cs        # ERAM fix forms shared by the Sim and the server: fix name, FRD and lat/long checks (IsFixName,
                               # IsFrdForm, ParseLatLong, IsAltitudeFixForm), ParseLocation (a field 68 location → position) and
                               # Resolve(fix) → position for the altitude-fix latch.
FlightPlanVoice.cs             # Couples FP remarks ↔ voice type (AircraftVoice.Type: 1=Full/2=ReceiveOnly/3=TextOnly). Remarks are
                               # canonical: ParseVoiceType reads a /v//r//t/ marker (full implied when absent), ApplyVoiceMarker writes it.
                               # A VATSIM convention (no FAA field). Driven by ERAM QB /v|/r|/t and the SetVoiceType hub; derived on amend + load.
RouteSplicer.cs                # ERAM AM RTE route-splice grammar (docs/crc/eram.md Table 8): join/resume/replace over [dep]+enroute+[dest]
                               # with a dotted anchor list, plus [ / ] (↑/↓) departure/destination swap. Splice semantics only (no SID/STAR expansion).
FlightPhysics.cs               # Static 8-step Update: navigation→descentPlan→climbPlan→speedPlan→heading→altitude→speed→position→queue; PhysicsTickOptions carries the per-tick scenario inputs (solo/RPO routing flags, magnetic-model day)
                               # UpdateSpeedPlanning: proactive speed look-ahead for procedure fixes (mirrors descent/climb planning)
                               # Auto speed schedule: skipped when ActiveApproach or ManagesSpeed (pattern phases)
                               # 14 CFR 91.117: 250 KIAS cap below 10,000 ft in UpdateSpeed() and ApplyFixConstraints(); public
                               #   RegulatorySpeedLimit(aircraft) adds the 200 KIAS Class B shelf cap. A TargetSpeed above the limit
                               #   stays standing at the cap (ArriveAtGoal) and is taken up when the cap lifts; an aircraft with no
                               #   target over the limit slows to it (BoundsCorrectionTarget); a chained SPD completes on
                               #   IsSpeedAssignmentHeldAtRegulatoryLimit
                               # UpdateSpeed: physics is the sole integrator of ground speed. SpeedChangeRate splits airborne
                               #   AircraftPerformance accel/decel from on-ground CategoryPerformance.TaxiAccelRate/TaxiDecelRate,
                               #   each overridable per aircraft via ControlTargets.DesiredAccelRate/DesiredDecelRate (a tug move
                               #   publishes the towbar rate while it has the aircraft); GroundNavigator only publishes
                               #   TargetSpeed/DesiredDecelRate for physics to close (roll phases — TakeoffPhase,
                               #   RunwayHoldingPhase, etc. — still write IndicatedAirspeed directly by design)
                               # Wind physics: TAS = IasToTas(IAS, alt); GS/Track derived from TAS + wind vector; WCA applied to nav
                               # ApplyFixConstraints: SID/STAR via-mode constraint enforcement at waypoints
                               # Bank angle: computed in UpdateHeading from atan(TAS × turnRate × coeff); sign follows turn direction
                               # Expedite: IsExpediting → direction-split rate (climb ×1.15 / descent ×2.0, per-category caps+floor); Mach hold: TargetMach → recompute IAS each tick
GeoMath.cs                     # Static: DistanceNm (haversine), BearingTo, TurnHeadingToward, GenerateArcPoints (RF/AF), PointInRing (even-odd ray-cast)
                               # Each primary function has scalar (double, double, double, double) and LatLon (LatLon, LatLon) overloads
                               # FootOfPerpendicular returns (LatLon Foot, double AlongNm, bool Clamped)
ClientKind.cs                  # Static constants: Main / VStrips / VTdls identify which YAAT app a SignalR client is running. DisplayName() labels one on its own (Room Members badge); DisplaySuffix() appends to a sentence.
ClientVersions.cs              # Compares client version strings for the server's version gate. Numeric major.minor.patch only — the -beta suffix carries no ordering. Fails open on anything unparseable.
                               # Sent on CreateRoom/JoinRoom; stored in RoomMember.Kind; DisplaySuffix appends e.g.
                               # " (Flight Strips)" / " (vTDLS)" to terminal-broadcast verbs ("joined the room (vTDLS)").
SimLog.cs                      # Static logger factory for Yaat.Sim; Initialize(ILoggerFactory) at startup
Diagnostics/ThreadCpuTime.cs   # Static: the calling thread's CPU time (GetThreadTimes / clock_gettime(CLOCK_THREAD_CPUTIME_ID)); timing-budget tests measure with it, not Stopwatch (docs/test-harness.md)
SerializableRandom.cs          # Xoshiro256** PRNG with serializable state (RngState record); drop-in Random replacement
DeterministicHash.cs           # Internal: Fnv1a(salt, callsign), the per-callsign draw behind fixed per-aircraft choices (release auto-CTO jitter with an empty salt, the after-taxi-arrival and release-request delays, the VFR departure direction); replay-safe, no RNG state
InitialCallupPlan.cs           # Enum: the solo initial call a spawn makes (None / StandCall / AfterPush / AfterTaxiArrival / RunwaySayOnly / RunwayNoPreset), set once at load by Scenarios/InitialCallupClassifier; AircraftGroundOps.InitialCallup
PresetTaxiStop.cs              # Record: the stop a spawn's timed TAXI preset ends at (Spot / TaxiwayHoldShort / RouteEnd + name + held-on taxiway), recorded at load for an AfterTaxiArrival plan; AircraftGroundOps.PresetTaxiStop (+ PresetTaxiStopDto)
SimulationWorld.cs             # Thread-safe aircraft collection; GetSnapshot, Tick, DrainWarnings
                               # WeatherProfile? Weather — passed to FlightPhysics.Update() each tick
                               # Rng (snapshotted) + live-only ReactionDelayRng / ReleaseJitterRng (NOT snapshotted):
                               # command-run delay + REL spawn jitter sample off the dedicated RNGs and bake the value into
                               # the recording, so replay reproduces it without perturbing the shared Rng stream.
CommandQueue.cs                # CommandBlock (trigger + closure + TrackedCommands), BlockTrigger
                               # CommandDimension flags (Lateral|Vertical|Speed) for dimension-aware queue clearing
                               # BlockTriggerType.AfterCycleTerminator + TriggerTerminatorObserved fire after the next option or go-around (OTG); a full-stop landing can never satisfy it, so FlightPhysics.DiscardMissedCycleTerminatorBlocks marks it TriggerMissed and drops it with a cancellation warning
                               # ReadyToAdvance: lateral gates block advancement; altitude/speed are fire-and-forget
                               # SourceCommandText on CommandBlock/DeferredDispatch for snapshot restore
                               # DiscardChainRemainder: fire-time failure aborts the same-dispatch chain remainder (see docs/command-chaining.md)
AircraftCategory.cs            # Enum + AircraftCategorization (static Init from AircraftSpecs.json)
                               # CategoryPerformance: fallback aviation constants (taxi, pattern geometry, flare, etc.)
                               # CornerSpeedForAngle: piecewise taxi speed curve (0-30° max, 30-90° corner, 90-150° tight corner)
Situation/AircraftSituation.cs # The aircraft's situation for context-menu quick commands (append-only numeric enum, sent on AircraftUpdated)
Situation/SituationClassifier.cs # Classify(ac, simTime, previous) → AircraftSituation: live traffic, then phase type (a turn takes the phase it resumes, a standalone turn keeps the stored flight-rules situation), then flight rules and the inbound/departing predicates with per-state hysteresis
Situation/AircraftSituationState.cs # Satellite: the stored situation, the situation flags, the runway to cross next (NextCrossingRunway), the most recent liftoff time and WasOnGround; written by SimulationEngine.TickSituation, snapshotted as Simulation/Snapshots/AircraftSituationStateDto.cs
Situation/SituationFlags.cs # [Flags] inputs for the quick-list visibility rules (nearing the departure bar, holding short of the departure runway, inside the FAF, rollout decelerating, field / traffic in sight, cleared for takeoff, past V1, cleared for the approach with descent); append-only bits
Situation/SituationFlagCalculator.cs # Compute(ac, situation, previous, …) → SituationFlags and NextCrossingRunway(ac, situation, groundLayout) → the runway whose bar is the next uncleared one on the taxi route (null while a previous runway is not yet crossed, §3-7-2.c), once a second in the Situation step; the latches and bands read the previous flags
AircraftStatusDescriber.cs     # Pure AircraftState→text projection for the Aircraft List "Info" column.
                               # Describe(AircraftState) / Describe(AircraftStatusView); server computes once
                               # per broadcast → AircraftDto.SmartStatus (client just displays it), TickRecorder
                               # calls it too. One implementation so all surfaces agree.
RunwayDepartureQueue.cs        # Static per-hold-short departure-queue ranker (one runway-end label per line via DepartureDesignator: phase departure runway → own destination bar → bar display name, never a combined pavement id). UpdatePositions(world) runs
                               # each sim-second in SimulationEngine.TickPrePhysics (the per-second hook the
                               # live server shares — it never calls SimulationEngine.TickPostPhysics),
                               # writing 1-based AircraftGroundOps.RunwayQueuePosition + RunwayQueueRunway +
                               # RunwayQueueIntersection per aircraft (0/"" = not in line; even a lone #1
                               # counts). Drives the ground-datablock "{runway} #N" suffix (e.g. "28R #2",
                               # "28R@E #2" off an intersection) + Info-column "(#N)". Second pass (RankFollowers)
                               # ranks a follower (FollowingPhase, or HoldingShortPhase with the follow queued behind
                               # it) directly behind its leader when the leader is in a line, the follower's own route
                               # ends at that bar and it is within ProximityNm of it (leader's tier, distance = leader
                               # + gap), iterated to a fixpoint so chains rank all the way down; a follower that cannot
                               # inherit (leader lined up, different bar) falls back to its own route like a taxier.
AircraftPerformance.cs         # Unified perf API: profile-first with category fallback. Altitude-banded
                               # climb/descent rates, Mach-aware speeds, 91.117 waiver support
GroundRollProfile.cs           # GroundRollProfile: the takeoff-roll spool ramp (idle → steady accel over the category's spool time) with
                               # closed forms SpeedAt/DistanceKtSecondsAt/TimeAtSpeed/TimeToCoverKtSeconds; the roll phases integrate it
                               # and every roll predictor (WillBeFlying, PrecedingDepartureBlock, RejectedTakeoff) projects on it.
GroundOutline.cs               # Plan-view aircraft outline for ground clearance (fuselage with a 30 ft tug lead on a pull, wing, tailplane) in a flat GroundOutlineFrame; Clearance between two outlines, ClearanceBetween two aircraft. GroundConflictDetector sweeps it along a tug move's RemainingPath against parked/held neighbours; GroundCommandHandler refuses a tow that starts already touching one
GroundOutlineSweep.cs          # The per-sample outline sweep (Sweep) and floor rule (FloorFt) shared by GroundConflictDetector.TugMoveFoulsParkedAt (a tow under way) and TugPlanBuilder.Judge (candidate templates against TugRequest.ParkedNeighbours at plan time), so planner and detector cannot disagree
GroundConflictDetector.cs      # Static pairwise ground proximity → SpeedLimit overrides. Runway priority via RunwayOccupancy.ClassifyByPhase;
                               # tug move vs parked/held neighbour: the outline sweep over the move's continuing run (TugMoveFoulsParkedAt +
                               # TugRunContinuation, floor anchored to the move's start) and a braking limit down to the towbar rate rather than
                               # a hard stop (TugMoveLimit/TugMoveBrakingLimitKts, margin from PushbackSpeed; floored each pass at the live speed less
                               # one detector interval of towbar braking, TowbarBrakingFloorKts, so a fouled-point's foot-or-two of jitter between
                               # path samples cannot ask for more braking than the towbar rate in one pass); compared for a yield target
                               # (ShowTugMoveYield) against the move's commanded main-gear speed (PushbackPhase.CommandedGearSpeedKts — the tug's pace
                               # times cos of the nose-gear steer angle), not the tug's own pace, since a turn slows the gear below it; an active
                               # PushbackPhase is never Stationary. WouldDeadlock's push-outranks-taxi rule is scoped to HasRampPriority (leg 1 of a stand push-off only).
                               # A forced tow (AircraftGroundOps.ForcedTowIgnoresParked, PUSHF/PUSHMF) does not stop for an aircraft at a stand or resting
                               # after a push (IsParkedAtStandOrAfterPush: AtParkingPhase or HoldingAfterPushbackPhase); one held or holding on the
                               # pavement still stops it, and moving traffic still limits it.
                               # GiveWayStop: a GIVEWAY's give-way point — how far the held aircraft still taxis along its route before its centre is within the
                               # pair's lateral clearance of the target's track through the first shared node, or its nose (half its length ahead) within the
                               # target's half of it; null with a noStopReason when the routes share no such junction
                               # live-traffic shadows = MovementState.External (obstacle, never subject; Ground.ExternalOnRunway by geometry).
                               # Single-pass pair classifier (SameEdgeTrailing/SameEdgeHeadOn/
                               # Converging/Crossing/Pushback/Stationary). Honors Ground.Hold
                               # (HoldPosition or GiveWay) via IsImmobile + speed gate for
                               # parked-obstacle classification. Stationary-named phases
                               # (LineUpPhase, HoldingInPositionPhase) only count as Stationary
                               # while IsAtRest (GroundSpeed < HeldStationarySpeedKts AND no positive
                               # TargetSpeed), so rolling or pinned-but-commanding LineUpPhase aircraft
                               # don't skip conflict checking (#409).
                               # Every close-range conflict resolves one-holds-one-goes
                               # (deterministic holder, never both stopped), incl.
                               # converging-merge arbitration — except a parallel-lane pass
                               # (HasParallelTrackLateralRoom: tracks within 20° of parallel/anti-parallel,
                               # each with more room from the other's track than half-spans + WingtipBufferFt,
                               # now and projected along the route segment over the pair's stopping time, same side of the track),
                               # where ComputeClosingLimit's moving-obstacle lateral bypass and ResolveHeadOn's
                               # 300 ft ring both stand down and neither aircraft holds. DebugSink logs the
                               # specific hold kind so the controller GIVEWAY relationship is observable
                               # ("ControllerGiveWay A→B" pair line).
HoldDirective.cs               # Structured ground-hold directive: HoldKind { HoldPosition,
                               # GiveWay } + optional YieldTarget callsign. Replaces the
                               # historical IsHeld+GiveWayTarget pair on AircraftGroundOps.
                               # Construct via HoldDirective.HoldPosition or
                               # HoldDirective.GiveWay(target). IsGiveWayFor(callsign)
                               # tests the pair relationship for the conflict detector.
GiveWayConstants.cs            # Auto-release tuning for direct GIVEWAY holds (FlightPhysics.UpdateGiveWayResume):
                               # safety-timeout (300s), target-stationary threshold (30s),
                               # stationary speed threshold, timeout clear-distance. Direct holds
                               # only — deferred BEHIND keeps pure-geometry release. A direct hold
                               # also releases on GroundConflictDetector.TargetReachesMergeFirst
                               # (target on its last taxiway into the merge, nearer it, same way out).
ConflictAlertDetector.cs       # Static STARS CA detection: 3nm/1000ft thresholds, 5s extrapolation, hysteresis, approach suppression;
                               # IsPairEligible = live-traffic shadow policy (shared with the ERAM detector); CASUP suppresses an active alert in ConflictAlertState, not here
EramConflictDetector.cs        # Static ERAM (en-route) STCA detection: 5nm lateral (3nm at/below FL230) + 1000ft vertical, 4-min extrapolation, uses assigned/interim data-block altitudes, scoped per ERAM facility
EramConflictState.cs           # Per-facility ERAM conflict-alert state (active STCA pairs) driving the Center data-block flash
Asdex/AsdexSafetyLogicDetector.cs  # Static ASDE-X Safety Logic detection: closed-runway, occupied-runway, taxi-onto-active-runway, taxiway-landing incursions → CRC surface alerts;
                                    # alignment is an axis test (back-taxi = runway user), arrival AGL measured from AsdexRunwaySurface.ElevationFt (SimulationEngine.TickAsdexAlerts resolves it from the nav-DB runway)
Asdex/AsdexSafetyLogicConfig.cs    # AsdexSafetyLogicConfig (Runways, RunwayConfigurationId, InhibitedArrivalAlertPositionIds) + AsdexRunwayConfig (Id, AreaPoints as LatLon,
                                    # IsClosed): the safety-logic configuration a CRC surface display pushes, Sim-native. Scenario state
                                    # (SimScenarioState.AsdexSafetyLogicConfig); ToSnapshot / FromSnapshot map it to ScenarioSnapshotDto's AsdexSafetyLogicConfigDto.
Asdex/SurfaceMembership.cs         # ASDE-X / SAID hysteresis membership, pure: EvaluateAsdex / EvaluateSaid (enter at range + ceiling, leave at ceiling
                                    # + 600 ft; SAID ceiling = field elevation + 2,500 ft; a pure phantom is a member of nothing; ids no longer configured drop
                                    # out). SurfaceAirports.Resolve(config, navDb) builds the airport sets (SAID ceilings precomputed), cached by
                                    # SimulationEngine.TickSurfaceMembership and EvaluateSurfaceMembership(aircraft) (every spawn, every successful
                                    # aircraft command), which write AircraftStarsState.VisibleAsdexAirports / VisibleSaidAirports.
                                    # MayEnterAsdex / MayEnterSaid are the public enter tests (no hysteresis) EvaluateAsdex / EvaluateSaid use and the server's
                                    # stateless subscribe-replay helpers wrap; SaidSurfaceAirport.From(SaidAirportInfo, NavigationDatabase) holds the SAID
                                    # ceiling math (field elevation + 2,500 ft). yaat-server's CrcVisibilityTracker diffs these sets, so hysteresis lives only here.
                                    # yaat-server's DtoConverter projects it to and from the CRC wire DTO
Training/SameRunwaySeparation.cs   # SrsCategory + 7110.65 §3-9-6/§3-10-3 landmark distances and the satisfied-predicates (crossed end / airborne /
                                   # landed-and-past-landmark) + landing/departure-family occupant tests, shared by the evaluator and OccupiedRunwayGoAround
Training/SoloTrainingEvaluator.cs  # Solo-training scorecard: FAA separation, wake, runway-operation separation, structured traffic-advisory/safety-alert/wake-advisory/field-proof events, ARTCC WakeDirectives, Class C outer-area/no-minima advisory scoring, active timeline, report buckets
Training/AircraftCompletion.cs     # Per-aircraft lifecycle stamps: spawn time, completion time, completion reason (Landed / Handed off / Dropped / Transited — overflight past its exit radius / Departed — departure past the session's auto-delete distance), filed route + operation classification used by the Session Report Aircraft tab.
Training/AircraftDebriefCoachingTemplates.cs  # Pure templates: one-line coaching note per completion reason + severity profile, consumed when aggregating per-aircraft debrief blocks from existing findings.
WeatherProfile.cs              # WeatherProfile + WindLayer; ATCTrainer-compatible JSON; layers sorted by altitude on load
                               # GetWeatherForAirport: cached METAR lookup via MetarInterpolator
WeatherPeriod.cs               # Single weather period in a v2 timeline: startMinutes, transitionMinutes, windLayers, metars, precipitation
WeatherTimeline.cs             # Time-based weather evolution: list of WeatherPeriods; GetWeatherAt(elapsedSeconds) interpolates wind
                               # layers during transitions (N/E vector decomposition); METARs/precipitation snap at transition start
WeatherTimelineParser.cs       # Static v1/v2 auto-detection parser: checks for "periods" array → WeatherTimeline, else → WeatherProfile
                               # Returns WeatherParseResult discriminated union (Timeline | Profile | Error)
LiveTraffic/LiveTrafficSample.cs     # LiveTrafficSample (sim-time observation: pos/alt/GS/track/VS?/source/beacon; Instance + ObservedAtUtc = feed provenance for bundles),
                                     # LiveTrafficSource (Stars/Eram/Asdex), LiveTrafficRemovalReason
LiveTraffic/AircraftLiveTraffic.cs   # Shadow satellite: last sample fields, SecondsSinceSample (dead-reckoning clock), AppliedAtSimSeconds + DeliverySilenceSeconds (freshness clock — coast/removal), IsCoasting, ExternalId
LiveTraffic/LiveTrafficAssumer.cs    # ASSUME hand-off: shadow → simulated aircraft in place; feed clearances first, then level/climb/descent, hold, final/visual,
                                     # route rejoin (NextFixAhead), initial climb, VFR, runway/surface kinds. Never refused. Also run implicitly by
                                     # CommandDispatcher.DispatchCompound's shadow gate for any non-SAY command to a shadow; stamps
                                     # AircraftState.AssumedFromLiveTraffic, the marker UNASSUME (ActionArms.Unassume) requires. See live-traffic.md.
LiveTraffic/ILiveTrafficFeedPort.cs  # The feed port the host implements (BeginSecond → LiveTrafficFeedSecond, ShadowStatus, EndSecond) + LiveTrafficFeedTrack, LiveTrafficShadowStatus, EmptyLiveTrafficFeedPort (always inert; BareHost/ReplayHost)
LiveTraffic/LiveTrafficKinematics.cs # CreateShadow / Apply(sample) / Resync(simNow) / Advance(dt): dead-reckons a shadow from its latest sample and writes the air vector (heading+IAS)
                                     # so the computed GroundSpeed equals the sampled GS under the room wind; coasts after two missed sweeps. See live-traffic.md.
LiveTraffic/LiveTrafficFilter.cs     # Shared live-traffic filter model (rules VFR/IFR/both, flight-plan airport list, radius); canonical-string TryParse/Serialize/Describe; carried on SimScenarioState.LiveTrafficFilter
LiveTraffic/LiveTrafficOwnerResolver.cs # Real-world ownership on shadows: sample owner/pending fields -> TrackOwner via the scenario's ArtccConfig (TCP/ERAM-sector match, synthetic fallback) + HandoffPeer pending display; feed yields to any ordinary Track.Owner write (OwnerFromLiveFeed). See live-traffic.md.
WindInterpolator.cs            # Static wind utilities: GetWindAt, GetWindComponents (vector lerp through 0/360; take sim time + phase),
                               # IasToTas/TasToIas/MachToIas/IasToMach (ISA compressible-flow equations), ComputeWindCorrectionAngle
WindVariation.cs               # Deterministic time-varying wind perturbation: bounded value-noise gust/direction wander + VRB model,
                               # per-aircraft callsign-phase decorrelation, AGL taper (pure function of sim time — no RNG)
WindObservation.cs             # ASOS-style observation of the simulated surface wind (2-min means, 10-min peak/lull, 5-s grid)
                               # feeding MetarIssuer's reported winds and SPECI time bounds
GroundFrame.cs                 # Air<->ground frame conversions for IndicatedAirspeed (wheel speed on the ground): rotation gate,
                               # liftoff/touchdown flips with headwind + density correction
MetarParser.cs                 # Static METAR parsing: station ID, ceiling (BKN/OVC), visibility (SM); ParsedMetar record
DefaultMetar.cs                # Static: builds the no-weather default METAR (calm/10SM/CLR/29.92) shared by the METAR panel, radar/ground overlay, vStrips bar
MetarInterpolator.cs           # Static: GetWeatherForAirport — exact station match then IDW interpolation within 50nm
ReportedConditions.cs          # Snapshot of modeled surface weather for one station (true wind, vis, sky/ceiling, altimeter, precip)
MetarComposer.cs               # Static: reconstructs a reported METAR by patching dynamic groups into a base METAR (AIM 7-1-28)
SpeciCriteria.cs               # Static: SPECI decision vs last issued (wind shift, vis/ceiling crossings, precip) — AIM TBL 7-1-1
MetarIssuer.cs                 # Per-room state machine: routine METAR at :53 + SPECI on change; freezes conditions at issuance
WindsAloftParser.cs            # Static: parses FAA FD fixed-width text → StationWinds[]; DecodeWind handles 100+kt, light/variable
MagneticDeclination.cs         # Static: NOAA World Magnetic Model (WMM) declination via the Geo library, memoized on a 0.02° grid (cell-centre eval) per evaluation day; TrueToMagnetic/MagneticToTrue conversion.
                               # Sim state passes the scenario's MagneticModelDateUtc (the day of its SessionStartUtc, recorded with the session so replays never drift); display-only callers use EvaluationDateUtc (the process day)
VisualDetection.cs             # Static: TryAcquireAirport, TryAcquireAirportForRunway, TryAcquireTraffic, IsOccludedByBank
                               # Maintained-contact variants (already-in-sight, weather-only incl. visibility-collapse × 1.25 tolerance): TryMaintainAirportContact, TryMaintainTrafficContact
                               # Airport visibility envelope AirportVisibilityRangeNm (vis × 0.869 × 1.5 × max(1, AGL/3000ft) — Koschmieder slab; shared by acquire + maintain; traffic keeps the literal cap)
                               # Returns VisualAcquisitionResult { Acquired, Reason, DistanceNm, MaxRangeNm }
                               # VisualAcquisitionFailure enum: InClassA, AboveCeiling, MixedCeiling, BehindOwnship, OccludedByBank, OutOfRange, OppositeSideOfRunway
                               # Forward hemisphere (traffic ±110°, airport ±120°), visibility, ceiling, bank angle occlusion (7110.65 §7-4-4.c.2), WTG-based traffic range
                               # FL180 gate on airport (visual approach eligibility) but NOT traffic (pilots can see in Class A)
VisualAcquisition.cs           # Static helpers: TryAcquireTraffic(ownship, target, weather), TryMaintainTrafficContact(...) and TryAcquireAirport(ownship, weather)
                               # Bundle METAR/elevation/bank-angle lookup around VisualDetection so ALL traffic paths (RTIS first-check,
                               # PilotObservationUpdater re-check, CheckLeadLifecycle, TickVisualDetection) use identical ownship-nearest-station
                               # inputs. TryAcquireAirport returns null when the destination is missing or not in the nav db (caller drops the observation).
PilotObservation.cs            # Abstract record PilotObservation + TrafficAcquisitionObservation(TargetCallsign) + FieldAcquisitionObservation
                               # Pilot-side "watch for a condition" state — populated when RTIS/RFIS soft-fail (pilot keeps looking)
                               # Extension points for future "report leaving altitude", "report passing fix", etc.
PilotObservationUpdater.cs     # Static per-tick evaluator called from FlightPhysics.Update after UpdateCommandQueue
                               # Re-runs VisualAcquisition.TryAcquireTraffic / TryAcquireAirport; on success sets the matching
                               # HasReported* flag and pushes the in-sight pilot readback through PilotResponder.RouteRpoSayReadback:
                               # RPO+RpoShowPilotSpeech → PendingPilotSpeech (green spelled-out via BuildTrafficInSight/BuildFieldInSight),
                               # solo → typed PendingPilotTransmissions for delayed SAY/audio; RPO default → PendingPilotReadbacks only
                               # (SAY channel, "Have <target> in sight" / "Have the field in sight").
                               # Silently drops observations whose target has left the sim or whose
                               # destination is no longer lookupable.
WakeTurbulenceData.cs          # Static: CWT code lookup from AircraftCwt.json; wake minima (TBL 5-5-2) + alerted visual TrafficDetectionRangeNm (9-arcmin silhouette model, clamp [1.5, 12] nm)

# Track operations
TrackOwner.cs                  # Record: Callsign, FacilityId, Subset, SectorId, OwnerType
TrackOwnerType.cs              # Enum: Other, Eram, Stars, Caats, Atop
Tcp.cs                         # Record: Subset, SectorId, Id, ParentTcpId
StarsPointout.cs / StarsPointoutStatus.cs  # Pointout state
StarsDatablockClassifier.cs    # Pure: projects a track's STARS view for a TCP (color White/Green/Yellow/Cyan, level LDB/PDB/FDB, leader dir); mirrors CRC DisplayElementTracks. Used by DtoConverter to fill AircraftStateDto.Student* for the instructor radar
EramPointoutState.cs           # Per-aircraft ERAM pointout record (mirrors vatsim-server-rs radar_state::PointoutState)
EramSectorKey.cs               # (Facility, Sector) key for the per-aircraft ERAM FDB-open and point-out-minimize lists
                               # Round-tripped via the Eram satellite (AircraftEramStateDto.Pointouts/ForcedPointoutsTo), like other serialized ERAM state

# Coordination
CoordinationChannel.cs         # Channel config: ListId, Title, SendingTcps, Receivers, Items
CoordinationItem.cs            # Single coordination entry: status lifecycle, expiry, origin TCP
StarsCoordinationStatus.cs     # Enum: Unsent→Unacknowledged→Acknowledged→Recalled→Expiry→Void
TowerListTracker.cs            # The STARS tower P-lists: airports keyed by TowerListKey(facility, listId) from the ARTCC's TowerListConfigurations (ZOA's FAT and NCT both define P1), and per-list dwell
                               # entries (callsign, EnteredAtSeconds) Update() maintains from the world snapshot each tick; ClearSession / RestoreEntries are the
                               # snapshot seam (TowerListSnapshotMapper); a duplicate key is rejected at collect time, same-second entries read back in callsign order

# Commands/
Commands/CanonicalCommandType.cs    # Enum of every command type
Commands/ParsedCommand.cs           # Discriminated union records; CompoundCommand/ParsedBlock/BlockCondition; includes server-only commands (DEL, PAUSE, ADD, etc.)
Commands/CommandDefinition.cs       # ArgMode enum, CommandDefinition/CommandOverload/CompoundModifier records; command metadata includes solo-pilot unable eligibility
Commands/CommandRegistry.cs         # Single source of truth: CommandDefinition per type (label, category, aliases, overloads, modifiers, pilot-unable eligibility)
Commands/CommandScheme.cs           # CanonicalCommandType → CommandPattern (aliases only); Default() from registry
Commands/CommandSchemeParser.cs     # Parse/ParseCompound (;/, syntax); ExpandSpeedUntil; concatenation fallback; ToCanonical()
Commands/CommandSignature.cs        # Records: CommandParameter, CommandSignature, CommandSignatureSet; FromDefinition factory
Commands/CommandDispatcher.cs       # Static: DispatchCompound (phase interaction), ApplyCommand (thin routing switch),
                                    # TryApplyTowerCommand, queue infrastructure, condition conversion, shared utilities
                                    # TryAirborneFollow: same-runway pattern-leg FOLLOW = in-place retarget; lead established toward a
                                    # runway = pattern-aware install on the follower's own side (#352), except a follower already in the
                                    # final-approach corridor (CanJoinLeadFinalDirectly) keeps the direct VfrFollowPhase in-trail join
                                    # IsConditionalIncoming: conditional commands are additive (no clear); only immediate supersede
                                    # ClearConflictingBlocks: dimension-aware selective queue clearing (preserveTriggeredBlocks for deferred firing)
                                    # SplitBlockNonConflicting: splits mixed-dimension blocks on partial conflicts
                                    # RejectFlightPlanCommand: refuses DA/FP/RMK at both public entries (the server applies flight-plan edits; replay skips them)
                                    # ApplyCommandCore passes ctx.GroundLayout to CTOPP/ATXI/LAND handlers
                                    # ClearPhaseChain: the one phase-teardown path (immediate dispatch, triggered re-dispatch, APT to another airport)
                                    # PeelTransparentHead: a leading all-transparent `;` block, or a leading APT inside a `,` block (`APT OAK, ELB 28L 4`),
                                    # applies first and the remainder re-dispatches fresh; RehydrateRestoredBlock matches a peeled remainder by command-count suffix
Commands/ConditionalList.cs         # Unified conditional list: pending queue trigger blocks + DeferredDispatches (WAIT/WAITD/BEHIND)
                                    # Enumerate/ToLines/Delete — backs SHOWAT/SHOWCOND, Pending Cmds column, DELAT/DELCOND/DC; excludes reaction-delay deferrals
Commands/DispatchContext.cs         # Record: GroundLayout, Rng, Weather, FindAircraft/ListAircraft, ValidateDctFixes, AutoCrossRunway, PreserveConditionals, IsScenarioScripted,
                                    # SessionStartUtc (the session clock for pilot speech that names a time of day — SAYEXIT's estimate; never DateTime.UtcNow)
Commands/DispatchOrigin.cs          # DispatchOrigin (Human | ControllerAi) + AiConnectionId ("AI:{positionId}" synthetic connection id; origin derived from it live and on replay)
                                    # Bundled at SimulationEngine/RoomEngine call sites; threaded through all internal helpers
Commands/FlightCommandHandler.cs    # Heading, altitude, speed, squawk, direct-to, warp, wait/say commands
Commands/NavigationCommandHandler.cs # Multi-block navigation: JRADO/JRADI, depart/cross fix, JARR STAR resolution,
                                    # JAWY airway intercept, CVIA/DVIA (DVIA SPD fix), JFAC, holding pattern, RFIS/RTIS/SAFAL, list approaches
Commands/VfrCommandPolicy.cs        # VfrCommandsForIfr enum (None/EnterFinalOnly/All) + classification of VFR-only commands
                                    # (RequiresVfr pattern set, IsVfrOnlyDeparture CTO modifiers, FOLLOW, CM A/B). The dispatcher
                                    # does NOT gate on flight rules — the desktop client enforces this against the setting (issue #317)
Commands/CommandDescriber.cs        # Static: DescribeCommand, DescribeNatural, classification helpers
                                    # GetDimension, GetCommandDimension, GetCompoundDimensions for queue clearing; strip family
                                    # commands are phase-transparent (STRIP/STRIPD/SCAN/etc., half-strip, separators, blanks)
                                    # InstallsIndefiniteHoldPhase: HP/VFR-hold/FOLLOW installers → dispatch-time chain warning
Commands/CompoundPolicy.cs          # Shared client+server chained-command policy: IsNonCompoundable rejection set +
                                    # FindNonCompoundableInChain ("{verb} cannot be part of a chained command"); DEL/DEST excluded (they chain);
                                    # ASSUME/UNASSUME are in the set (a spaced "ASSUME ; H 180" parses as one UnsupportedCommand and slips this
                                    # pre-check, so CommandDispatcher carries its own guard with the same message)
                                    # IsFlightPlanCommand: identifies DA/FP/RMK (flight-plan amend commands that skip replay and bypass dispatch)
Commands/TrafficAdvisoryMatcher.cs  # Shared RTIS/SAFAL target matching: clock + VFR relative-octant/pattern-leg/landmark forms, best-candidate-by-weighted-error + Exact/Imprecise grade
Commands/AltitudeResolver.cs        # Plain int or AGL format → feet MSL
Commands/CfrWindow.cs               # CFR types: ReleaseWindow, CfrAlertKind, CfrAction/CfrPhase, FAA-fixed −2/+1 window constants (7110.65 §4-3-4.e.5)
Commands/CfrWindowResolver.cs       # Pure HHMM→absolute-UTC window resolver (nearest-instant) + CfrAlertEvaluator (early/late/expired-grounded)
Commands/NodeRefToken.cs            # Parses user-typed `#<id>` node-reference tokens used in TAXI clearances; co-located with the parser since the token format is grammar, not routing
Commands/RouteChainer.cs            # After DCT to on-route fix, appends remaining route fixes
Commands/ApproachCommandHandler.cs  # Approach clearance logic (CAPP/JAPP/PTAC/CAPPSI/JAPPSI/CAPPF/JAPPF/PTACF forced variants/CVA visual approach); RF/AF arc expansion in BuildApproachFixes
Commands/DepartureClearanceHandler.cs  # Departure clearance + CIFP SID resolution, CancelTakeoff, ClearedTakeoffPresent (CTOPP)
Commands/DepartureCommandParser.cs  # Departure-specific command parsing; ParsePatternModifierArgs shared by MLT/MRT pattern modifiers and CTO option clearances, resolved through Commands/Arguments/CommandArgumentResolver.cs
Commands/GroundCommandHandler.cs    # Ground operation command logic (taxi, pushback, hold short, helicopter landing @spot). Owns the pre-pathfinder path rewrites: the HS-taxiway fold (HoldShortTaxiwaysToFold / AugmentPathWithHoldShortTaxiways — applied only when the as-cleared route fails AsClearedRejectionReason, or the command has no destination), the current-taxiway prepend, TrimPassedNodeRefPrefix (drops drawn #nodes the aircraft already taxied past), ResolveSpotVias (rewrites every non-trailing $spot in the path to its #nodeId so the route is forced through it, refusing an unknown name; LastSpotViaName names the via — not the graph node — in a DestinationUnreachable refusal), and the post-resolve IsPlausibleNodeRefResolution guard. Also drops an unreachable gate-adjacent lead-out taxiway (TryDropGateLeadOut: within GateAdjacentTaxiwayMaxFt, no runway between) and routes the rest with a warning naming the lane used (issue #396). For LAND/ATXI (TryLand/TryAirTaxi): IsOnFieldForAirTaxi (on the ground, or within the layout's node bounding box + 0.3 nm and ≤ 500 ft AGL) keeps the AirTaxi chain on the field; off the field LAND installs HelicopterApproachPhase and ATXI is refused ("unable" — air taxi is a surface movement, 7110.65 §3-11-3 NOTE). An ATXI destination resolves through TryResolveAirTaxiDestination → AirTaxiDestination (Parking / Spot / Runway) by its marker, the TAXI destination markers carried as AirTaxiCommand.TargetKind: `@` a helipad or gate, `$` a spot, a bare token always a runway (DescribeUnresolvedAirTaxiDestination words the refusal of a bare non-runway token with the `@`/`$` hint): a runway resolves to its holding-position bar (ResolveRunwayHoldShortNode — nearest the threshold, or the bar incident to the `@taxiway`), set back half a fuselage (WithHoldShortSetback), and ends in a DestinationRunway HoldingShortPhase with the departure runway assigned; a spot ends in HoldingInPositionPhase. CaptureDepartureRunwayAssignment / ApplyDepartureRunway / WarnIfDepartureRunwayChanged give TAXI and ATXI the same runway assignment and the §3-7-2 runway-change warning. PUSH/PUSHM plan around parked or held neighbours within 400 ft of the start pose (ParkedNeighboursNear → TugRequest.ParkedNeighbours; skips a neighbour the aircraft already touches where it stands, since that one gets OverlapRefusal's own message instead). WithPushNotes appends the accepted TugPlan's Warnings (a TugFoulsTaxiwayWarning naming the outline part reaching in deepest, a TugLongPushWarning for a long tow onto a named taxiway) and a far-facing-junction note as RPO-only parentheticals on the PUSH/PUSHM/FACE-amendment readback — never spoken, since the pilot's readback is verbalized from the command, not this text. RES/TryResumeTaxi also breaks a ground-conflict stall or crawl (Ground.SpeedLimit at or below GroundConflictDetector.SlowTaxiSpeedKts) the same as BREAK, not just a HOLD/GIVEWAY. HS marks a bar the aircraft cannot brake to in time (MarkUnmakeableHoldShort → TaxiingPhase.IsHoldShortUnmakeable) HoldShortPoint.Unable and answers "unable to hold short of X — stopping"; NotifyTaxiHoldShortsChanged re-aims a taxi already under way at a bar just armed or re-armed instead of letting it drive on to the segment's junction node. The TAXI readback names only the clearance as issued (BuildTaxiReadback/ReadbackTaxiwayFilter over TaxiRoute.ToSummary's filtered overload): a ramp taxilane or movement-area taxiway the driven path adds is left out of speech, and a movement-area one is instead warned (WarnUnclearedMovementAreaTaxiways, RouteMaterialiser.NotInRouteIssuedWarning) unless it is the runway-entry connector the clearance implies (ImpliedRunwayEntryConnector: a numbered variant of the last cleared taxiway reaching the destination-runway bar) or the gate's/spot's short implied lead-in (SegmentExpander.ImpliedDestinationLeadIn), both driven and read back silently. OccupiedTaxiway (now given the ground layout) also credits an aircraft standing on pavement with no CurrentTaxiway recorded: it projects the aircraft's position onto the layout's nearest taxi edge, within RampLaneReposition.CurrentLaneMaxFt, and counts that taxiway as occupied unless the aircraft is on a stand (issue #454). Issue #461: when the clearance's taxiways do not join up, ApplyMissingTaxiwayFallbacks runs last — CheckStartReachesFirstCleared holds the aircraft short of the missing link on the taxiway it occupies (or, with no route at all, refuses naming the link) when the start cannot reach the clearance's first taxiway over the cleared/occupied taxiways and apron; failing that, TryHoldShortOfMissingTaxiway tries appending one more uncleared movement-area taxiway off the last cleared taxiway (a runway destination prefers the entry nearest the threshold, else the better route) and, when the augmented clearance reaches the destination, cuts the route at the first node on it (HoldShortBeforeTaxiway, kept off runway pavement by KeepOffRunwayPavement) and ends it at a HoldShortReason.RouteIncomplete hold instead of refusing the TAXI outright. PUSHF/PUSHMF (ForcedPushback/ForcedPushbackMulti) plan the same request with TugRequest.Forced set; a plain PUSH/PUSHM the planner refuses that the forced form would be installed for (planned as a throwaway probe and checked against the same install refusal — the start overlap for a new tow, none for an amendment — never installed itself: PlannerRefusal/ForcedFormWouldInstall) appends "To force it: <command>" to the refusal text. An installed forced tow logs each rule its plan overrode (LogForcedOverrides) and WithPushNotes appends one RPO-only parenthetical per TugPlan.ForcedOverrides entry (ForcedNote) after the plan's ordinary Warnings notes; the tow sets AircraftGroundOps.ForcedTowIgnoresParked on install and on every re-plan (a mid-push FACE amendment, a PUSHM redirect), which PushbackPhase clears when its last move completes or anything else ends the move
Commands/TrackEngine.cs             # Pure domain logic for STARS track ops: Track, Drop, Handoff, Accept, Cancel, PointOut, Acknowledge,
                                    # RejectPointout, RetractPointout, Scratchpad1/2, TempAlt, Cruise, PilotReportedAlt,
                                    # InhibitConflictAlert, LeaderDirection, JRing, Cone. All methods mutate AircraftState directly.
                                    # Dispatch(parsed, ac, identity, scenario, redirect): top-level switch routing any track ParsedCommand to
                                    # the right HandleX/ApplyX; ApplyHandoff / ApplyPointOut land an unattended target on its attended consolidation owner
                                    # through the ConsolidationRedirect (null = nobody answers attendance = no redirect) and ApplyHandoff re-points an
                                    # inbound handoff its recipient re-addresses. The ActionRouter's TrackOwnership arm (Simulation/Actions/) runs it on
                                    # every run kind — the live room, a Sim replay, a server reconstruction, the bare test engine (yaat-server has
                                    # no track handler; its test harness's TrackCommandSeam wraps the leaves). DispatchGlobal(cmd, world, scenario, identity): ACCEPTALL / HOALL;
                                    # AcknowledgeConflictAlert(ac, conflicts): CAACK on the engine's conflict-alert set; ApplySharedState(ac, tcpId, dto): a position's
                                    # per-TCP shared display state replaced whole + the dismissed point-out cleared (the RecordedStarsSharedStateChange applier)
Commands/TrackEngine.Ghost.cs       # The STARS display-object bodies: CreateGhostTrack → GhostTrackOutcome (result + the created phantom; a phantom staggered 0.1 nm/ghost off the runway threshold along the
                                    # reciprocal, or an overlay on an existing aircraft that never steals another position's track), RepositionToLocation /
                                    # RepositionMove (park / re-associate an unsupported datablock; ParkedDataBlockId is the "RPOS" wire-id prefix the server's
                                    # broadcast layer uses). The ActionRouter arms call these on every run kind
Commands/EramEntryEngine.cs         # The one body for the ERAM keyboard entries that write per-track ERAM state, applied from a RecordedEramEntry on every run kind:
                                    # TRACK [/OK] (owner guard unless forced; clears the handoff, unfreezes), FREEZE {lat} {lon} (altitude snapshotted at apply),
                                    # QQ / QQ L / QQ [R|L|P]{alt} (interim tiers, hundreds of feet), QR {ddd} (CERA; 000 clears), QS * | */ | /* | /{speed} | {heading} | `{text}
                                    # (the HSF fields in CRC's canonical forms — ParseHsfHeading / ParseHsfSpeed; free text 1–8 alphanumerics), LF [{label}]
                                    # (CRR membership; bare = clear), VCI {sector} (on-frequency toggle), LEADER {facility} {sector} [D{1-9}] [L{0,1,2,3,5}] (that sector's data-block offset), DWELL {facility} {sector} 1|0 (that sector's dwell lock, absolute),
                                    # HANDOFF {code} [/OK] (sector adapted, then owner only — /OK forces for another sector of the same ARTCC, never a STARS or
                                    # external owner — and a target that already owns the track is refused; then TrackEngine.ApplyHandoff), PO {fromFac}
                                    # {fromSec} {toFac} {toSec}… / POACK / POCLEAR (point-outs; the hub decides who may acknowledge or clear, the Sim refuses
                                    # a repeated or missing point-out), DRI {facility} {sector} [J|T] (that sector's halo; bare = clear), HM / QH {field 21} [{field 310}] (hold annotation, in
                                    # EramEntryEngine.Hold.cs). Apply takes an EramEntryContext (identity, scenario, consolidation redirect).
                                    # A refusal's CommandResult.Message is an EramEntryErrors id, optionally a space then the field in error; success messages
                                    # are free text. The live CRC handler keeps the wire parsing, FLID / scope / FDB validation and feedback and records the
                                    # entry through RoomEngine.ApplyAndRecord
Commands/EramEntryEngine.Hold.cs    # The HM / QH hold rules: field-21/310 parsing (ParseHoldFields validates each location against a NavigationDatabase through
                                    # EramFixResolver.ParseLocation; ParseRecordedHoldFields is the shape-only read replay uses), the merge with the stored AircraftHoldAnnotation,
                                    # the EFC edit, holding instructions and the present-position fix; RefuseHoldEntry is the validation the handler runs first
Commands/EramHoldEntry.cs           # The parsed hold entry: EramHoldEntry, EramHoldData, EramHoldingInstructions, EramHoldDataKind, EramEfcEdit
Commands/EramEntryContext.cs        # What an ERAM entry is applied with: the acting identity, the scenario, and the consolidation redirect a handoff reads
Commands/EramEntryErrors.cs         # Error ids EramEntryEngine answers a refused entry with (id alone, or id + space + the field in error, e.g. `MsgCofieFormat 12.5X`);
                                    # each is an id from docs/eram/error-responses.yaml — yaat-server maps the id back to CRC's error-table text (EramErrors), so
                                    # the Sim carries no display wording. The All list backs yaat-server's EramReferenceConformanceTests, which holds every id
                                    # to the reference.
Commands/ConsolidationRedirect.cs   # Where a handoff or point-out addressed to an unattended TCP lands: TryRedirect(target) → the attended position whose
                                    # airspace absorbed it (GetConsolidationOwner over the facility hierarchy + ConsolidationState), null when the target is
                                    # attended or has no other owner. Built per dispatch from the scenario, the engine's ConsolidationState and Attendance
Commands/TrackResolver.cs           # AS-prefix extraction (e.g. "AS 3Y ACCEPT" → "ACCEPT" + "3Y" override); the one TCP→TrackOwner chain (student TCP →
                                    # scenario ATC → facility TCP → ERAM code → STARS interfacility handoff code → ERAM-to-STARS prefix → position callsign or callsign@tcp, reading
                                    # scenario.ArtccConfig); owner→TCP lookup; ResolveIdentity(scenario, selections, connectionId, asOverride) — the one
                                    # identity resolver (AS override → AI connection's position → selection → student); AsPrefixCode(owner) — the code an
                                    # "AS {code}" prefix names an owner by (C{sector} / {subset}{sector} / callsign), so a CRC-issued command round-trips its identity
Commands/PatternCommandHandler.cs   # Pattern operation command logic (extend, rock wings, GoAround, CTL, sequence, etc.); EF loop detection via turn-arc geometry, same-runway continue no-op, never-route-outbound reject, and the pattern retarget that degrades a too-close EF into a base entry; runway-changing entries void the landing clearance; pre-arms EXT/SA/MNA and landing/option clearances behind a still-queued pattern entry; present-position downwind join (IsAtOrPastDownwindEntry) when the aircraft is already alongside the downwind between the entry point and the base turn (#352); BuildActiveLegChain / ResolveFlownRunway / ResolveOptionClearancePattern resolve pattern and option clearance routing
Commands/RunwaySafetyAdvisor.cs     # Non-blocking 7110.65 3-9-4/3-9-6 occupied-runway advisories (PendingWarnings → amber terminal; never prefixed with the callsign): landing-family clearance
                                    # (pavement test = RunwayOccupancy.IsOnPavement for holding-in-position occupants; live-traffic shadows by
                                    # RunwayOccupancy.Classify geometry; WarnIfTrafficOnFinal = 3-9-4.d shadows within 6 nm on LUAW; WarnIfLandedTrafficBlocksTakeoff = 3-9-6.b arrival not yet clear on CTO;
                                    # WarnIfAnotherHoldingInPosition = 3-9-4.h second LUAW on the same pavement, not safety-logic gated)
                                    # (CLAND/COPT/TG/SG/LA/LAHSO/CLANDF) with traffic holding in position / taxiing to line up on the runway, and the reverse
                                    # (LUAW with a landing-family clearance outstanding — only while that arrival is airborne; touchdown ends 3-9-4.c). WarnIfRunwayOccupiedForTakeoff = 3-9-6.a/b: takeoff clearance
                                    # issued with another aircraft holding in position or a live-traffic shadow on/landing on the runway.
                                    # WarnIfStoppedTrafficOnRunway = 3-10-3.a.1/3-9-6.a third occupant kind (#411): traffic frozen on the pavement by a
                                    # HOLDPOSITION/GIVEWAY hold directive (Ground.IsImmobile + IsOnPavement, e.g. a stopped runway crossing); moving crossings stay silent.
                                    # Suppressed when ArtccConfigResolver.AirportHasFullSafetyLogic finds an ASDE-X config with runway configurations
                                    # for the airport (CRC Safety Logic covers the incursion there)
Commands/FlightPlanCommandHandler.cs # Flight-plan amendment validation: TryChangeDestination resolves FAA/ICAO airport input via NavigationDatabase.TryResolveAirport,
                                    # writes canonical ICAO to FlightPlan.Destination, rejects unknown airports; clears the arrival procedure state the old destination had,
                                    # cancelling (ClearPhaseChain + "cancelled by APT OAK" warning) a pattern/approach/go-around flown to another airport; APT to the
                                    # airport it is already arriving at is a plan correction that keeps everything; a departure's chain is never touched.
                                    # The dispatcher's ChangeDestination arm (phase-transparent, so a bare APT edits a parked or holding aircraft's plan) on every run kind
Commands/FlightPlanNormalization.cs # Flight-plan input normalization shared by the typed FP/VP/DA verbs (the router's flight-plan arm) and the CRC editor: type/suffix split
                                    # (default suffix A), FAA→ICAO airport canonicalization (unknown identifiers pass through; the amend variant keeps the clear sentinel),
                                    # route split (single token = destination), FromCreateCommand / FromCreateAbbreviatedCommand → the FlightPlanAmendment a typed verb files
Commands/FlightPlanEcho.cs          # The two-line filed-plan readout (callsign type/equipment beacon; departure destination altitude or NO ROUTE) STARS shows and the
                                    # terminal repeats; HasRoute(command) for the second line. A pure function of the aircraft, rebuilt by the CRC readout after the arm ran
Commands/MilitaryRouteCommandHandler.cs # Military training route (AP/1B) commands; occupancy/route advisories go to PendingWarnings (never prefixed with the aircraft's own callsign)

# Commands/Arguments/ — typed positional command arguments (docs/plans/typed-command-arguments.md)
Commands/Arguments/CommandArgumentType.cs      # Enum of migrated argument slot types (Runway, Altitude); grows as more slots move onto typed arguments (docs/plans/typed-command-arguments.md). Every member needs a validator in CommandArgumentResolver; where two types compete at one overload position the resolver's precedence decides (Runway before Altitude)
Commands/Arguments/RunwayArgument.cs           # Shape validator: 1-2 digits with an optional L/C/R suffix, normalized through RunwayIdentifier.NormalizeDesignator (shape only — whether the airport has the runway is a dispatch-time check)
Commands/Arguments/AltitudeArgument.cs         # Shape validator: everything AltitudeResolver reads (2-digit hundreds shorthand, full feet, AGL form). Overlaps RunwayArgument on 2-digit tokens by design — the resolver's precedence, not the validator, makes a 1-2 digit token a runway where both types are candidates
Commands/Arguments/CommandArgumentResolver.cs  # Resolves a token list against a command's overload shapes like compiler overload resolution: each token is tried against the types the still-viable shapes declare at that position, in precedence order, narrowing the viable set; one survivor binds (CommandArgumentValue(Type, Token, Value) + ValueOf<T>/TokenOf), none is a failure naming what was expected. An altitude-only position takes the full altitude grammar, so MLT 15 15 is runway 15 at 1,500 ft

# Phases/ — clearance-gated behavior
Phases/Phase.cs                # Abstract: OnStart/OnTick/OnEnd, CanAcceptCommand→CommandAcceptance, OnCommandAccepted (release internal state machines on accept), ManagesSpeed (suppresses auto schedule), IsIdleAwaitingCommands (terminal phases awaiting controller input; lets queue advance untriggered blocks while idle)
Phases/PhaseList.cs            # Mutable list: AssignedRunway, TaxiRoute, LandingClearance, ActiveApproach, DepartureClearance, RequestedExit/ResolvedExit, GivenUpExitTaxiways (exits the crew said "unable" to, shared by LandingPhase and RunwayExitPhase), mutations
Phases/PhaseRunner.cs          # Static lifecycle: start→tick→advance; auto-appends exit/pattern phases; auto-cycle builds the runway transition when PatternRunway ≠ AssignedRunway
Phases/PhaseContext.cs         # Readonly tick context; includes Weather, TowerPosition for RV SID heading hold, PilotContacts (who answers pilot calls) + ToEligibilityContext()
Phases/TowerCabPhases.cs       # Phase families inside local control's jurisdiction: IsArrivalSide (final, landing, pattern, go-around, tower maneuvers on final) — the airborne check-in guard; departures deliberately excluded
Phases/PhaseStatus.cs          # Enum: phase lifecycle status
Phases/CommandAcceptance.cs    # Enum: Allowed, Rejected, ClearsPhase
Phases/CourseLineSteering.cs   # Heading onto a course line through a fix: the course corrected by cross-track error, capped at a 45° cut; used by the procedure turn's inbound intercept and DepartureProcedurePhase's course legs
Phases/ClearanceRequirement.cs # Clearance requirement definitions
Phases/IGroundRollClock.cs      # A phase flying a takeoff roll along the GroundRollProfile spool ramp reports how far into it the roll is (RollClockSeconds); predictors (same-runway separation, rejected takeoff, preceding departure) place the aircraft where the roll has it instead of inferring from speed
Phases/ExitPreference.cs       # ExitSide enum, ExitPreference class, ResolvedExitInfo (branch point + path + turn-off speed), ExitInstructionVerdict (accept/refuse an EL/ER/EXIT on the rollout or a late re-target, with the pilot's own "unable" line in PilotUnable)
Phases/RolloutBraking.cs       # Braking kinematics (required decel, DecelOverDistanceKtsPerSec, braking distance, max entry speed for a stop) + NamedExitBrakingLimit (per-category firm rate, max effort under EXP) and the turn-off tolerance, shared by LandingPhase and RunwayExitPhase
Phases/ClearanceType.cs        # Enum: LineUpAndWait, ClearedForTakeoff/Land/Option/TouchAndGo/StopAndGo, RunwayCrossing
Phases/RunwayInfo.cs           # Runway geometry. Both ends' thresholds/headings/elevations; Designator picks the active one. PavementLengthFt is DERIVED from the two ends' coordinates and is the only length — the nav data's landing_distance_available is deliberately not stored (declared per end, differs between them, and no caller wants it; an arrival's usable distance comes from LandingThreshold). TWO elevation datums: ElevationFt = the active end's landing threshold (CIFP-sourced, carries the glidepath); AirportElevationFt = the field (carries traffic pattern altitude, AIM 4-3-3), falling back to the mean of the ends when unset so pre-existing fixtures/snapshots keep their value.
Phases/GlideSlopeGeometry.cs   # Glidepath altitude/descent rate (3°, 6° helicopter). AltitudeAtDistance carries a threshold-CROSSING HEIGHT, so the aiming point (AimPointFt = height/tan) emerges from the geometry instead of being tuned per category. Production calls the (dist, elev, category) overload, which pairs AngleForCategory with CategoryPerformance.WheelCrossingHeightFt (wheel band per AIM 1-1-9.d.7, not the published antenna TCH).
Phases/InterceptAngleLimits.cs # 7110.65 §5-9-2 TBL 5-9-1 final-approach-course interception angles: 20° inside 2 nm of the approach gate (every category), otherwise 30° / 45° helicopter. Single home for the rule — InterceptCoursePhase's bust-through gate, FinalApproachPhase's ApproachScore legality verdict, and its forced-intercept glideslope bypass all read it. PatternCommandHandler.MaxCloseInFinalAngleOffDeg deliberately diverges (45° helicopter at any distance; VFR-pattern airmanship analogy, not the vectoring limit).
Phases/PatternGeometry.cs      # 7 pattern waypoints from RunwayInfo + category + direction + the authored GroundRunway (required param). The arrival legs (Threshold, downwind abeam, base turn) hang off LandingThreshold.Resolve; DepartureEnd/CrosswindTurn stay on the pavement end per AIM 4-3-2. MinFlyablePatternSizeNm computes the minimum flyable pattern width for an aircraft type given wind; Compute(runway, category, aircraftType, windSpeedKt, ...) enforces this floor to prevent overshooting the final onto adjacent runways (issue #412). ComputeTransition builds runway-transition waypoints; MidfieldAlongTrackNm computes midfield crossing point distance. ResolveAuthoredOverrides(runway, authoredRunway, category, ...) treats the authored TPA as the established pattern altitude and applies the AIM 4-3-3.a category rule (turbine +500, helicopter <=500, piston verbatim); a command TPA wins verbatim
Phases/PatternBuilder.cs       # BuildCircuit, BuildNextCircuit, BuildRunwayTransitionCircuit (one runway's upwind into another's pattern: close parallels turn crosswind beyond both departure ends, crossing runways join through a MidfieldCrossingPhase), BuildPatternExitCircuit (CTO MRC/MRD pattern-exit departures: upwind[→crosswind]→PatternExitPhase, no landing tail), UpdateWaypoints. All Build* methods take aircraftType and windSpeedKt for pattern flyability floor enforcement. BuildFieldCrossingPrefix: the shared crossing→downwind join (crossing, then a TeardropReentryPhase for a jet/turboprop entry-height crossing, else RejoinTrack on the downwind) used by PatternCommandHandler's arrival entry and wrong-side MLT/MRT rebuild and by BuildRunwayTransitionCircuit's crossing arm
Phases/RunwayGeometry.cs       # AreCloseParallels: same airport, headings within 5°, centerlines within 0.25 nm (AIM §5-4-19 side-step envelope) — shared by the EF sidestep and the runway-transition circuit
Phases/PhaseClearSummary.cs    # Builds short label ("pattern to RWY 28R", "approach to RWY 28R", or phase Name) for the cancellation warning surfaced when a command clears the active phase chain; labels from AssignedRunway only

# Phases/Tower/
LineUpPhase.cs                 # State-machine lineup. A restore rebuilds the maneuver on its first tick (RollingMode round-trips; the plan/navigator do not). Preferred: graph-taxi (State.GraphTaxi) — follow the taxiway onto the runway via LineUpGraphRoute + a GroundNavigator; LUAW stops on the centerline rollout, a rolling clearance completes at the fillet exit and lets TakeoffPhase have the rollout as the first 80 ft of the roll. Fallback (no clean junction arc): synthetic LineUpGeometry — Aligned (straight → fillet arc → rollout) or Pivot (SlowTurn → perpendicular straight → SlowTurn → rollout); its straights run at taxi speed braking onto ArcSpeedKts, which governs the arc alone. A hold (CTOC / HOLD) pins the speed but never skips the steering tick. Faulted stays stopped (user recovers via TAXI / CANCEL CLEARANCE)
LineUpGraphRoute.cs            # Graph-aware lineup route (issue #239): walks taxiway edges from the node nearest the aircraft (forward, toward the runway) to the junction bearing a departure-aligned runway fillet arc, then the arc onto the centerline + a rollout straight projected down the runway heading. The walk skips nodes ON the centerline, not just centerline edges — a junction fillet carries no such flag, so at a runway-CROSSING taxiway it used to step through the reciprocal-end prong onto the runway and dead-end (SFO 28R off E). Two speeds: MaxSpeedKts caps the straights at taxi speed, FlowSpeedKts is the rolling floor; the fillet is left to the arc's own SpeedProfile. Follows the painted lead-on lines instead of cutting a diagonal across the taxiway/runway junction. Returns null (→ synthetic pivot) when no departure-aligned onto-runway arc route resolves
LineUpGeometry.cs              # Pure geometry fallback: classifies aircraft pose as Aligned, Pivot, or Fault; builds LineUpPathPlan with closed-form primitives (nose-out, arc, pivot turns, straight, rollout). The single-arc Aligned path is gated by a 90° turn cap + convergence check (above 90° the straight nose-out backs toward the runway-start corner, issue #203); large-turn / non-converging / parallel-taxiway poses fall through to the Pivot (issue #193), as do shallow poses where the straight path wastes >20% of remaining runway (issue #142). Fault only when there is no room to pivot (cross-track < main-gear turn radius), past runway end, or degenerate pivot; ResumedRolloutSpeedKts / ResumedSwingRolloutLengthFt size the restore-only fallback rollout so a mid-turn restore finishes its swing at the ground yaw rate (LineUpPhase publishes TurnRateOverride and clears it in OnEnd)
LineUpArcPlayback.cs           # Closed-form circular-arc playback (invariant I2: position and heading are functions of a single scalar)
LinedUpAndWaitingPhase.cs      # Hold at threshold; await ClearedForTakeoff
TakeoffPhase.cs                # Ground roll→Vr→400ft AGL; each roll tick offers RejectedTakeoff.TryTrigger a shot at ending the roll
RejectedTakeoff.cs             # Rejected-takeoff trigger/decision/install (issue #410): blocker scan via RunwayOccupancy (OnSurface/Landing always, Crossing unless projected clear, a preceding Departing per PrecedingDepartureBlock), Takeoff Safety Training Aid decision table (low-speed reject for any blocker; high-speed/past-V1 reject only when liftoff+overfly margin doesn't fit), single construction site shared with the CTOC mid-roll abort; gated by AutoRejectTakeoffOnOccupiedRunway; IsAtOrPastV1 is the one V1 test the CTOC handler and the PastV1 situation flag share
PrecedingDepartureBlock.cs     # Whether a preceding rolling/airborne departure blocks the trailer: §3-9-6.a landmark spacing (SameRunwaySeparation) projected to roll start at a standstill, a projected-rendezvous collision test once rolling, opposite-direction rollers always
RejectedTakeoffPhase.cs        # 2s reaction (25.109(a)(2)) then max-effort braking on the centerline (RejectedTakeoffDecelRate) → "stopped on the runway, standing by" → HoldingInPositionPhase; honest overrun past the pavement end raises a solo-eval finding
InitialClimbPhase.cs           # Climb to 1500ft AGL, assigned, or the SID's published TDLS initial-altitude cap (AircraftProcedure.SidInitialAltitudeFt); activates SID via mode; RV SID heading hold until handoff+5s; hands off to DepartureProcedurePhase at the TERPS gate for charted heading/course SID legs
DepartureProcedurePhase.cs     # Flies charted ARINC-424 SID legs: VA (heading→alt), VI/CI (heading/course→intercept), VM (heading→manual), CA (course→alt), course-tracked CF, CD/VD/FD/FC (course/heading→DME or along-track distance), CR/VR (→radial); levels off at a leg's at-or-below/between crossing altitude until it sequences (AIM 5-2-9.e), then loads NavigationRoute for the fix-to-fix remainder
FinalApproachPhase.cs          # Glideslope; occupied-runway go-around (OccupiedRunwayGoAround), lateral-alignment go-around (CheckLateralAlignmentGate: inside 1nm, >0.08nm off the LIVE assigned runway's centerline, established-track, not realigning by the threshold, 3s sustain; retarget/join grace), then no-clearance warning/go-around at DA/MDA when published, otherwise 200ft AGL; illegal intercept check (§5-9-1); uncontrolled decel stages clean → approach-flap (FinalApproachSpeedSchedule, ~9nm) → 1.3·Vref (5nm) → Vref at a per-aircraft distance (FinalApproachSpeedVariety); the flap/config latches fire only at the stage's kinematic trigger, not merely from already being at/below the stage speed, so an arrival sped back up after a restore still flies the stage later; FasReachGateNm/MaxConfigTriggerNm/ApproachFlapTriggerHeadroomNm and ConfigurationReachGateNm are internal for SimulationEngine.Generators' post-ceiling speed-restore gate
FinalApproachSpeedSchedule.cs  # Uncontrolled long-final speed schedule, additive on Vref like airline flap schedules: clean (Vref+70 capped 240 heavy/220 large/210 RJ; turboprop Vref+55/200; piston Vref+35/120, ±5kt per-callsign jitter), approach-flap max(1.3·Vref, min(clean−25, Vref+45)) settled by a per-callsign 9±1.5nm (jet) / 8±1nm (turboprop) gate; SpeedAtDistanceKts feeds OnFinal spawns and the generator spacing ceiling
FinalApproachSpeedVariety.cs   # Deterministic per-callsign FAS-reduction distance (right-skewed 2.0-5.0nm); lazily assigned in FinalApproachPhase gated on SimScenarioState.FinalApproachSpeedVarietyEnabled (off by default, server-on for live, recording-captured for replay), stored on AircraftApproachState, so arrivals slow to final approach speed at varied distances
LandingPhase.cs                # Flare→touchdown→rollout; continuous exit evaluation (resolve→brake→commit/abandon→relax preference); a named exit judged by one reach test (JudgeExitReach) at command time (EvaluateAndApplyNamedExitInstruction → "unable {twy}" / "unable, no {twy} ahead" refusals) and on the tick (GiveUpUnreachableNamedExit, MarkExitUnable), a refused one given up for the landing (PhaseList.GivenUpExitTaxiways); LAHSO-aware; CLANDF forced rollout (TickForcedRollout: graph exits only at 3–6 kt/s, else stop 300 ft before the runway end; sets PhaseList.ForcedRollout on a completed ground end)
ForcedLandingProfile.cs        # CLANDF instructor-override constants and guidance: signed aim point on the runway ahead (6° / threshold+1,000 ft / aircraft+500 ft, pulled back for a 6 kt/s stop + 500 ft), 3,000/1,000 fpm descent cap, 5 kt/s airborne decel to Vref + wind, flare floor, rollout braking band
HelicopterApproachPhase.cs     # LAND @spot from off the airport (7110.65 §3-11-6): holds the present altitude direct to the spot (a standing SPD is the en-route speed, else type cruise; ≤ 90 kt inside 3 nm), descends at 400 ft/nm to the 500 ft AGL rotorcraft pattern altitude, then a 6° final at 60 kt onto the spot aiming at the air-taxi height, decelerating to a hover over the last 0.25 nm; hands off to HelicopterLandingPhase over the spot. Installed by GroundCommandHandler.TryLand when IsOnFieldForAirTaxi is false (on the field the AirTaxi chain is kept)
RunwayHoldingPhase.cs          # LAHSO: hold at 0kts on runway after landing; clearance-gated (RunwayCrossing)
GoAroundPhase.cs               # TOGA, runway heading. Climbs to pattern altitude−300 (VFR/pattern traffic), the published missed-approach altitude (instrument), or 2000ft AGL (self-clear)
OccupiedRunwayGoAround.cs      # Pilot-initiated go-around ≤30 s from the threshold when RunwayOccupancy finds a blocking occupant (stopped, crossing, or inside the §3-10-3 landmark); gated by AutoGoAroundOnOccupiedRunway
GoAroundHelper.cs              # Shared go-around wiring for the auto trigger + the GA command: pattern-intent resolution (ResolvePatternIntent), climb-out altitude (ResolveClimbOutAltitude), phase-list install (voids the standing + pending landing clearance and a LAHSO hold-short target), pre-GA landing-intent capture
                               # InstallGoAroundPhases voids a VIS* clearance (any go-around off a visual kills the approach authorization — 7110.65 §7-4-1)
VisualApproachHelper.cs        # AIM §5-5-11.a.3 lost-visual-reference consequence engine: HandleTrafficContactLost (handback vs end-of-visual),
                               # EndVisualLostReference (committed → go-around w/ spoken reason; else level-off + "unable the visual, request vectors"),
                               # VoidVisualApproach (clearance/flags/follow/landing-clearance teardown). Fed by CheckLeadLifecycle + TickVisualDetection.
TouchAndGoPhase.cs / StopAndGoPhase.cs / LowApproachPhase.cs
MakeTurnPhase.cs               # 360/270 turn tracking (cumulative degrees, exit heading); clones pattern phase for 360s; slows to holding speed then resumes
STurnPhase.cs                  # S-turn phase: alternating 30° deviations from final heading for spacing
VfrHoldPhase.cs                # VFR hold: orbit at current position (HPP) or navigate-then-orbit at fix (HFIX); slows to holding speed then resumes
ManeuverSpeedController.cs     # Shared holding-speed slow-down + resume for tight maneuvers (MakeTurn/VfrHold/STurn)
AirspaceBoundaryHoldPhase.cs   # Solo-training VFR boundary hold outside Class B/C until the Bravo clearance or two-way-comms gate is satisfied.

# Phases/Approach/
ApproachNavigationPhase.cs     # Navigate through CIFP fix sequence (IAF→IF→FAF) with alt/speed restrictions + next-fix speed look-ahead
InterceptCoursePhase.cs        # Fly current heading until intercepting final approach course; detects bust-through (sign flip → "unable, passing through the localizer"; 180s timeout → "unable to intercept the localizer, request vectors") and routes the pilot transmission (solo TTS or RPO amber line) via PilotResponder. ForcedIntercept (PTACF, CAPPF implied-PTAC) bypasses the 30° capture gate — forces capture on steep cuts, overshoots expected
                               # AssignedInterceptHeading: the controller's vector captured by the installer (ApproachCommandHandler/NavigationCommandHandler) before the clearance/join nulls ctx.Targets.AssignedMagneticHeading — legality is judged against this, not the live (already-cleared) target
HoldingPatternPhase.cs         # AIM 5-3-8 holding with entry determination; MaxCircuits for hold-in-lieu
ProcedureTurnPhase.cs          # AIM 5-4-9 procedure turn (PI leg): outbound on FAC reciprocal → 45° offset → 180° turn back → intercept inbound. Engaged by CAPP when DCT matches PT anchor or intercept angle > 90°
ApproachClearance.cs           # Record on PhaseList storing active approach state + pre-built MAP fixes

# Phases/Pattern/
UpwindPhase / CrosswindPhase / DownwindPhase / BasePhase / PatternEntryPhase
BaseFollowSpacing.cs           # Static: a base follower's spacing behind a lead on final (by phase or geometry) or on base ahead, each BasePhase tick after the structural go-around — projects the rollout against the lead (runway occupancy included) and returns Keep / Widen (30° away from the field to 1.5 turn radii, altitude held, glidepath re-planned after) / BreakOff (VfrFollowPhase turn-out) / GoAround (under 0.4 nm left before the final turn, lead not abeam); never breaks off a structural overtake. ProjectedBaseGapNm judges the turn-out's exit to base
DownwindPhase.cs               # Pattern downwind leg; ExitAtMidfield / MidfieldLeadNm for midfield crossing decisions
MidfieldCrossingPhase.cs       # Cross the field to the correct pattern side (wrong-side entry, crossing-runway departure join, in-pattern crossover); InitialTurn biases the first turn, CrossAtPatternAltitude keeps an in-pattern crossover at TPA (entry crossings put turbines at max(TPA, field + 1,500 ft))
PatternReportHelper.cs         # Voices a controller-armed "turning {leg}" pilot report (REPORT command, #211) from each pattern phase's OnStart; reads the ReportArmed* flags on AircraftApproachState so reports re-arm every circuit
PatternExitPhase.cs            # Terminal leg of a CTO MRC/MRD/MLC/MLD pattern-exit departure: rolls out on the exit-leg heading (crosswind or downwind) and departs the area, climbing continuously toward the assigned/cruise altitude (no level-off at TPA), then completes to free flight. UpwindPhase/CrosswindPhase carry an optional DepartureClimbTargetFt so the legs climb out continuously rather than capping at pattern altitude.
PatternLateralOffset.cs        # OFL / OFR (OFFSETL / OFFSETR) state holder. One-shot lateral dogleg + parallel hold on the current pattern leg (upwind/crosswind/downwind/base). Default 0.5 NM, range 0.1–1.5 NM. State lives on the active phase only; discards on the next leg transition.
VfrFollowPhase.cs              # VFR FOLLOW free-pursuit phase (installed only for free-flight leads or corridor-positioned followers since #352 — see CommandDispatcher.TryAirborneFollow). Trails the lead — steers relative to the lead's ground track (parallel / lag-pursuit / shallow widen, never just aiming at the lead) + speed with spacing correction, altitude untouched; auto-joins lead's pattern when within 3 nm of the downwind abeam point AND within 5 nm of the lead AND on the correct side of the runway. For a lead on a straight-in final with no pattern (TryJoinLeadFinal) sequences onto that runway's final (PatternEntry→Final→Landing, no clearance — awaits CLAND) once trailing + aligned + not capturing across a parallel runway's final (JoinCapturePathCrossesParallelFinal — tests BOTH stored runway ends; navdata orientation is arbitrary); the lead-landed fallback (TrySequenceBehindLandedLead) sequences onto the captured runway only through the final join's geometry gates minus in-trail (≥ 0.5 nm out on the approach side, ≤ 1.0 nm cross-track, ≤ 30° intercept, no parallel-final crossing), else ends the follow into a pattern re-entry for that runway (upwind entry from past the threshold, midfield crossing from beyond a parallel; an armed PatternRunway kept). Turn-out (TryStartTurnOut/TickTurnOut): a follower level with or ahead of a lead on base/final, stalled alongside it at the offset cap for 20 s, or sent by a BaseFollowSpacing break-off turns to the downwind heading with one call (BuildTurningDownwindForSpacing), holds an offset band at pattern altitude, and turns base once the lead is abeam and the base projection gives pattern spacing + 0.1 nm; past 2 nm / 6 nm it holds an extended downwind for the controller; a lead that lands, goes around or leaves final ends it into a downwind entry. Spacing uses wider free-flight distances (1.5/2.0/3.5 nm) vs pattern-tight (1.0/1.5/2.0 nm). CarryArmedPatternRunway: the pattern-join and final-sequence phase-list rebuilds carry over an armed PhaseList.PatternRunway (e.g. an option clearance's pending MLT/MRT transition) instead of stamping the join runway into both fields and silently cancelling it; warns via PatternCommandHandler.WarnIfArmedTransitionCrossesField when the carried transition crosses the field
AirborneFollowHelper.cs        # Shared spacing math. GetAdjustedSpeed for pattern phases (ctx-based, slow-only: capped at leg baseline) + AdjustedFreeFlightSpeed for VfrFollowPhase (wider margins) + ComputeFreePursuitHeading (free-pursuit lateral trail-keeping law: parallel/lag-pursuit/widen regimes, FollowWidenState hysteresis). Auto-cancels with warning if follower can't maintain separation at min speed. ShouldBreakOffFollowForSpacing: a base/final follower in a structural overtake (IsStructuralOvertake: own Vref + gust additive > lead IAS + 10 kt) closing inside 0.8 nm breaks off + goes around rather than overfly/cut in front. ShouldHoldForLeadSequencing holds a downwind follower's base turn behind a flow-ahead lead by projection (lead aft of the 3-9 line; threshold ETA ≥ lead touchdown + RunwayClearanceSeconds; projected rollout spacing ≥ desired), with PatternCornerCutNm shortening the follower's rectangular path; ShouldExtendDownwind (straight-line proximity) applies only to leads that are not flow-ahead.

# Phases/Ground/
AtParkingPhase / PushbackPhase (flies one `TugMove` of a `TugMovePlanner` plan — every `PUSH`/`PUSHF` form and `PUSHM`/`PUSHMF` install one phase per push or pull, steered by `TugKinematics` on the type's turn radius with a 5 s dwell at each reversal; the tug holds a steady push speed through straights and turns alike, while the main gear — the point the move steers and physics moves — runs at that speed times cos δ, δ the nose-gear steer angle the towbar draws (`CommandedGearSpeedKts`, what `GroundConflictDetector` compares an outline limit against), creeping to the alignment speed over the last `AlignCreepFt` (30 ft) of a creep move onto a mark; restores pre-tug-move snapshots as the equivalent move; clears `AircraftGroundOps.ForcedTowIgnoresParked` (a forced tow, set by the command on install) once its last move completes or anything else ends a move — see docs/ground/pushback.md) / TaxiingPhase (a GIVEWAY hold keeps taxiing to GroundConflictDetector.GiveWayStop and brakes onto a GroundStopBraking curve via ApplyHeldSpeed, stopping dead only with no give-way point; re-aims at an uncleared hold-short's painted bar, not just the junction node, after the navigator advances its own target internally — an entry-alignment retirement or a fillet handed over on its aimed line — not only after this phase's own segment set-up) / HoldingShortPhase (a HoldShortReason.RouteIncomplete hold — the end of a route that could not reach its destination as cleared, issue #461 — rejects RES/CROSS naming the missing taxiway and the pilot's report asks for further taxi)
CrossingRunwayPhase / HoldingAfterExitPhase / FollowingPhase (a follower stops its nose at a runway hold line through GroundStopBraking)
GroundStopBraking.cs         # Internal static stop-braking rule shared by a FOLLOWG follower at a runway bar and a GIVEWAY at its give-way point: StopBraking (Routine/MaxEffort/Backstop) chosen by ChooseStopBraking from the stopping distance at the taxi vs the max-effort (ExpediteExitDecelRate) rate, MaxEffortBrakingTravelThisTickFt, StopCurveKts (the braking-curve speed that reaches zero SetBackStopMarginFt short of the stop), FeetPerSecondPerKt
GroundNavigator.cs           # Core ground nav: closed-form arc playback (plays the real cubic Bezier), pure-pursuit tracking (look-ahead floored at the category main-gear turn radius), turn-rate-feasibility corner-speed cap (arc speed profile limits target by local fillet curvature), one-tick overshoot backstop only at stop targets / last segment / arc entries (pass-through nodes advance on pass), guarded [Nav] speed-cap attribution logging, entry-alignment rounding, orbit invariant, ReleaseHeadingHold when a straight primitive takes over from an arc, arc-entry offset blend for a curve entered off-line, I8 no-teleport guard (CheckNoTeleport/ThrowOnTeleport). A >135° entry (ReversalEntryThresholdDeg) is a reversal: aimed at the first route node the arc cannot overshoot rather than a bearing (FindAimNode, stopped at a hold-short/runway-hold-short bar via IsBarNode), swept against its short way when the route's next turn would otherwise compound with it (ShouldReverseAgainstShortWay/SignedTurnAfterEntry), and any legs it rolled out past retired on completion (TryRetireLegsTheArcAimedPast). When the aim lands on a fillet segment (a GroundArc) — its own to-node, or the far end of a leg retired past — the arc's exit tangent points at that node, not along the curve, so the fillet is instead handed over on the straight line to it (InstallAimedLineOverFillet, gated to a Bezier pending primitive) rather than played as its Bezier from the nearest curve point, which would bleed the hand-over offset off over the few feet of arc left far faster than the aircraft drives and trip the I8 no-teleport guard; the straight still arrives on the fillet's to-node through the ordinary straight arrival, so the owning phase's node-arrival hook still fires. This state round-trips through the snapshot (GroundNavigatorDto.OnAimedLineOverFillet + AimedLineFilletFromNodeId) so a restore mid-line rebuilds the same straight rather than the curve the aircraft is not standing on. The active primitive and its playback progress also round-trip (GroundNavigatorDto.Playback: GroundNavigatorPlaybackDto / PathPrimitiveDto): FromSnapshot holds it back and the owning phase's first SetupSegment resumes it (TryResumeRestoredPlayback) when the segment's from- and to-nodes match the saved ones, so a restore mid-turn goes on along the same curve. Two braking rates: DecelRateKts for the route and every stop on it, and an optional SlowdownDecelRateKts for corner/arc slowdowns only (SplitRateBrakingLimit plans each constraint on its own curve; set by RunwayExitPhase on the turn-off, not serialized)
RunwayExitPhase.cs             # Rolls on centerline until exit found; builds TaxiRoute from exit path and hands off to TaxiingPhase. No ground layout (TickStopWithoutLayout): no exit graph to search, so it just rolls to a stop on the runway (FlightPhysics.StationaryGroundSpeedKts) and completes, starting the queued HoldingAfterExitPhase. Stopped with no exit ahead (TickStoppedWithoutExit): a ForcedRollout backtracks to the nearest exit behind (FindExitBehind, heading flipped once the route is built); any other aircraft holds, waits for an occupied/full exit ahead, or reports once "unable to exit, request back-taxi to X" (AIM 4-3-21.a). The turn-off flies at the rate the rollout chose the exit with (TurnOffDecelRate → ApplyExitDecelRate → GroundNavigator.SlowdownDecelRateKts; the route-end stop at the taxi rate, the whole route at max effort under EXP). Its searches skip PhaseList.GivenUpExitTaxiways while rolling, not once stopped
HoldingAfterExitPhase.cs       # Post-exit hold: broadcasts "clear of runway", faces away from runway, awaits taxi command. Skips the broadcast when there was no ground layout and no exit taken — the aircraft is still on the runway, with nothing to report clear of
ClearRunwayPhase.cs            # CLRWY: pulls a tail-over-runway aircraft (hold-short of a taxiway sitting closer than its own length past a crossed runway) forward until just clear (½ length past the bars), then holds

# ControllerAi/ — the controller AI (docs/plans/controller-ai/): CA0 = identity, resolver, staffing, jurisdiction, anomaly ledger, service, sinks; CA1 = ground brain + taxi/crossing rules, pacing, runway-in-use resolver
ControllerAi/ControlRole.cs    # Ground | Local | Approach | Center (+ ControlRoles: tick rank, GND/TWR/APP/CTR position type, alias parsing)
ControllerAi/AiPositionConfig.cs  # One AI-staffed position: real TrackOwner identity + TCP, position id/callsign, radio name, facility, airports it answers for (deep value equality)
ControllerAi/ControllerAiConfig.cs  # Session config: seed, enabled position ids, role overrides, RunwayInUse, RunwayConfigurations (per-airport named configuration); ToSnapshot/FromSnapshot (ControllerAiConfigDto on the scenario snapshot)
ControllerAi/AiPositionResolver.cs  # ARTCC config → playable positions for a primary airport (cab facility + its TRACON + ARTCC): Catalog / Resolve / InferRole (_DEL skipped unless overridden)
ControllerAi/IAiStaffing.cs    # Which configured positions are active + who is human (IsHumanHeld(AiPositionConfig), IsAssignedToHuman); HeadlessAiStaffing = solo student only
ControllerAi/PositionJurisdiction.cs  # Which AI position is responsible for an aircraft (phase family → cab role, along-a-runway → Local per 3-1-3.a.4; unstaffed cab falls through to the radar track owner; RunwayOccupancy for phase-less; human assignments excluded) + AiWorldView (callsign-sorted snapshot, per-position jurisdiction)
ControllerAi/AiTickContext.cs  # AiTickInputs (host → service), AiTickContext (what a brain sees), IPositionBrain, LayoutFor/RunwaysFor/RunwayInUse accessors
ControllerAi/AiCommands.cs     # AiIntent, AiCommandRequest (no AS prefix — the AI connection id names the position), AiCommandOutcome, IAiCommandSink
ControllerAi/AiAnomaly.cs      # AiAnomalyKind (incl. CoordinationTimeout: a crossing Ground asked Local for is still uncleared; KnowledgeConflict: facility knowledge violates tailwind limits) / AiAnomalyEvent / AiAnomalyLog: (kind, position, subject) episodes Open/Close/Record, Drain in order, never snapshotted
ControllerAi/AiControllerService.cs  # Per-second AI tick: staffing refresh → publish AiStaffedPositions → outcomes → CommandRejected → world view → brains in (rank, id) order; owns AiRng (re-seeded on Reset) + RunwayInUseState
ControllerAi/EngineAiCommandSink.cs  # Pure-engine sink: synchronous SimulationEngine.DispatchAiCommand, outcomes drained next tick
ControllerAi/Rules/IDecisionRule.cs  # AiRuleScope (tick, position, jurisdiction, memos, pacing, CloseVanished, TryIssue = the single paced emission path) + IDecisionRule
ControllerAi/Rules/StuckAircraftRule.cs  # Movement phase with < 50 ft net progress for 180 s (600 s when the conflict detector held it at any point during the stall; never under HOLD / hold-for-release)
ControllerAi/Rules/UnansweredPilotRequestRule.cs  # A request this role answers still open after the pilot had to ask again (STANDBY re-bases the pilot's clock); held-for-release takeoffs exempt
ControllerAi/Rules/HandoffUnacceptedRule.cs  # Radar roles: a handoff to/from the position pending past AutoAcceptDelay + 60 s
ControllerAi/Rules/ConflictAlertInAiJurisdictionRule.cs  # A terminal CA on an aircraft in the jurisdiction, keyed by conflict id; closes on clear or acknowledge
ControllerAi/Brains/ObserverBrain.cs  # Issues no commands: runs the four watchdog rules over its jurisdiction
ControllerAi/Brains/GroundBrain.cs  # The AI Ground position: the three watchdogs unpaced, then answer-taxi-out → runway-crossing → answer-taxi-in → hand-to-local, paced; settles last tick's outcomes into the memos first
ControllerAi/AiAircraftMemo.cs  # Per-aircraft brain memory (never snapshotted): movement anchor + sticky yield, GroundIntent, in-flight request + effect deadline, bounded-retry ledger (2 retries, backoff, GaveUp), think-time observation, pending crossing
ControllerAi/AiPacing.cs        # A frequency is serial: one transmission per position per tick, 5 ± 2 s gap (AiRng), 2–8 s per-aircraft think time (FNV-1a on callsign + rule)
ControllerAi/RunwayInUse.cs     # SurfaceWind (steady + gust + variable; HeadwindOn, WorstTailwindOn), RunwayInUseResolver (the generic §3-5-1 rule: a session designator, else the end most aligned with the magnetic wind ≥ 5 kt, else the longest pavement; PavementOf), RunwayUseDecision, RunwayUsabilityGate.Apply (prunes departure runways over 10 kt dry / 5 kt wet tailwind; null when none survives), RunwayInUseState(knowledge lookup) (per-airport memo on the service, held while the wind stays within 30° / 5 kt and the precipitation state is unchanged; precedence designator → named configuration (kept, gate violation filed) → facility knowledge pruned by the gate → generic; recursion guard for mutual partner couplings)
ControllerAi/Knowledge/FacilityOps.cs  # Facility SOP knowledge records: FacilityOps (facility id + airport id + runway configurations + selection policy + assignment policy), RunwayConfiguration, ConfigurationRunways, RunwaySelectionPolicy, PartnerCoupling, RunwayAssignmentRule, AircraftPredicate, RunwayAssignmentEffect, SopAircraftClass (P/T/J NCT 1-7 classes), FacilityOpsJson (strict System.Text.Json options: unknown members and enum values fail)
ControllerAi/Knowledge/FacilityOpsDatabase.cs  # Process-wide static loader: Initialize(directory, navigation), SetInstance (tests), For (airport id lookup), LoadDirectory, Load, FacilityOpsValidator (cross-checks against navdata: airports, runways, configurations, partner couplings), FacilityOpsValidationException
ControllerAi/Knowledge/SopAircraftClassifier.cs  # Classify (aircraft type → P/T/J), Matches (predicate → aircraft type → bool): jet or four-engine turboprop = J; cruise TAS ≥ 180 kt (from profile or category baseline) = T; else P; unknown MTOW treats as "over" for T, unknown engine count never matches
ControllerAi/Knowledge/FacilityRunwaySelector.cs  # Select (facility ops + airport + wind + partner-config callback + runways + magnetic-model date → decision): partner coupling → calm-wind config (gust-inclusive threshold) → best-headwind wind-aligned config; FacilityRunwayAssigner.AssignDepartureRunway (aircraft + decision + runways → runway end: exclusion rules per SopAircraftClassifier, the whole set stands when nothing is left; a constrained aircraft gets the longest remaining pavement, others the nearest departure threshold, designator tiebreak)
ControllerAi/Rules/AnswerTaxiOutRule.cs  # Ground rule 1: an open ready-to-taxi request from a parked / pushed-back aircraft → TAXIAUTO <runway>, per-aircraft via FacilityRunwayAssigner when knowledge exists
ControllerAi/Rules/AnswerTaxiInRule.cs   # Ground rule 4: a taxi-in request naming a parking spot → TAXIAUTO @<spot> (re-picked when the spot was taken meanwhile)
ControllerAi/Rules/RunwayCrossingRule.cs  # Ground rule 2: the next uncleared crossing bar (holding at it or within 500 ft) → CROSS <near end> as a combined position when nobody holds Local, else one terminal request line + CoordinationTimeout after 120 s
ControllerAi/Rules/RunwayCrossingGate.cs  # May a combined tower let an aircraft cross now: no departing/landing/on-surface/short-final occupant, no arrival inside the final gate (3 NM or 90 s within 10 NM, on the course, not climbing, < 2,500 ft AGL), crosser not held
ControllerAi/Rules/HandToLocalRule.cs  # Ground rule 5: CT <Local callsign> while taxiing, every crossing behind, within 1,200 ft of the departure-runway bar
ControllerAi/Rules/TaxiRouteProgress.cs  # Reads a taxi route for the brain: next uncleared crossing bar + along-route distance, distance to the departure bar, nearest crossing end

# Pilot/ — solo-training pilot AI (deterministic readbacks). Phraseology/forms reference: docs/pilot-phraseology.md; delivery plumbing: docs/solo-training-pilot-speech.md
Pilot/PhraseologyVerbalizer.cs # Static: inverts a PhraseologyRule for a given accepted ParsedCommand → Verbalize() spoken-English / VerbalizeTerminal() compact readback (shared rule, per-token formatter strategy).
                               # Picks the first-declared rule per CanonicalCommandType by default; Varied mode can use PilotShortcuts when the frequency is busy.
Pilot/FrequencyActivityMeter.cs # Rolling 60-second pilot-transmission counter; classifies active frequency load as Quiet/Moderate/Busy/Saturated.
Pilot/FrequencyState.cs        # Sim-level active-frequency queue. Serializes solo pilot SAY/audio transmissions and gives awaited command readbacks priority over proactive calls.
Pilot/PilotTransmission.cs     # Record: Callsign, Text, SpeechText, SourceKind, Kind. Transient typed side queue for solo-training SAY/audio broadcasts.
Pilot/PilotPendingRequest.cs   # Snapshot-serialized pending pilot request model for solo-training follow-up reminders.
Pilot/ImplicitBravoClearance.cs # Solo only: decides whether a heading/DCT/pattern entry/approach clearance implies a Class B clearance (waiting pilot, 120/300 s level ray, airport inside a Bravo); CaptureWait runs before dispatch (ActionArms.Aviation), TryGrant in ApplyPostDispatch sets IsClearedIntoBravo and the readback clause. See docs/airspace-database.md.
Pilot/PilotRequestTracker.cs   # Records pilot-originated requests, applies controller responses (TAXI/TAXIAUTO/PUSH/FOLLOWG… satisfy a Taxi request, a beacon code a Clearance request), SatisfyOpenRequest for answers that are not aircraft commands (REL/HFROFF → Release, TDLSS → Clearance), and schedules normal/standby follow-up reminders.
Pilot/TaxiInRequest.cs         # The arrival's call to ground after the runway exit (AIM 4-3-21.c): "clear of runway 28R at W, taxi to gate 29" from the post-exit idle phases, recorded as a Taxi request with the pilot's parking
Pilot/InitialCallupCall.cs     # The solo initial call-up shared by AtParkingPhase, HoldingAfterPushbackPhase, HoldingInPositionPhase and HoldingShortPhase: TryMake (pacing rate, answering GND, pacing slot → "ready to taxi" + Taxi request; to a _DEL student a clearance request + Clearance request instead), CanCall, the post-push and after-taxi-arrival delays, BeforeTow/AfterTow (call-up bookkeeping around a tug move)
Pilot/ReadyToTaxiLocation.cs   # Where a ready-to-taxi or clearance-request call is made from (Ramp / Stand / Spot / Taxiway / PushedBackFrom / HoldingShort); ForStandCall picks stand, spawn taxiway, else ramp
Pilot/RunwaySpawnCall.cs       # Runway-spawn rules at the lined-up call point (LinedUpCall / Silent / ReleaseRequest) for a radar student; TryRequestRelease (IFR at an untowered field, 5–10 s after lining up: holds for release + Release request); IsAtAutoTakeoffPoint for SimulationEngine.ProcessRunwaySpawnAutoTakeoffs
Pilot/VfrDepartureDirection.cs # The cardinal a VFR departure names to a delivery student: a ±45° sector with no other airport within 10 NM, picked per callsign (DeterministicHash), else the one whose nearest airport is farthest
Pilot/ArrivalParkingPicker.cs  # The parking an arriving pilot asks for: operator's own ramp / cargo apron / numbered gate / non-gate spot by callsign, free spots only, an FNV-1a draw (replay-safe)
Pilot/PilotContactRoster.cs    # Who answers pilot calls: AI-staffed positions (student stand-ins) + the solo student; ResolveFor(aircraft, GND|TWR, atAirportId, eligibility, checkEligibility) picks the addressee (tower-cab AI positions match the physical airport only; every candidate obeys the SOP transfer rules; ground calls fall back to an AI tower working alone); PilotAnsweringPosition.MarkInitialContact keeps HasMadeInitialContact student-scoped and latches AI contact per position id
Pilot/PilotResponder.cs        # Static: BuildReadback(CompoundCommand, AircraftState) → PilotSpeechText? (compact terminal + spoken TTS) for solo-training mode.
Pilot/PushReadbackPhrases.cs   # Static: the one sentence builder for PUSH/PUSHM readbacks (PushWords) — the handler's RSP + CommandResult.PilotReadback and the verbalizer's parse-only form for a queued push.
                               # Uses PhraseologyVerbalizer for rule-backed commands; ground spawn / "going around" / airborne-spawn / VFR closed-traffic check-ins live here directly
                               # Adds light deterministic Quiet-frequency flavor and preserves runway/callsign-critical readback content.
                               # Also: BuildTrafficInSight / BuildFieldInSight / BuildHoldingShortTaxi (requestFurtherTaxi appends "request further taxi" at a route-incomplete hold, issue #461) / BuildHoldingShortCrossing / BuildClearOfRunway / BuildGoingAround / BuildApproachingMinimumsNoLandingClearance / BuildUnable / BuildUnablePatternSize / BuildLostSightOf*
                               # / BuildUnableTo* / BuildUnableHoldShortClause (gated by IsUnmakeableHoldShort on HoldShortPoint.Unable) — the spelled-out spoken forms used by RPO PilotSpeech routing.
                               # QueueSoloPilotTransmission / QueueSoloPilotReadback put solo pilot speech into PendingPilotTransmissions;
                               # command RSP lines stay immediate while delayed SAY lines represent what the pilot says on frequency.
                               # RouteRpoTransmission(aircraft, soloMode, rpoShowPilotSpeech, pilotSpeechText, warningText) — three-way helper
                               # used by every sim-initiated pilot transmission site to pick the right destination collection.
Pilot/PilotProactive.cs        # Static: TickAirborneCheckIn(AircraftState, SimScenarioState, airportLookup) — fires once-per-aircraft when first ticked airborne in solo mode; skipped inside the tower's arrival side (TowerCabPhases.IsArrivalSide).
                               # Idempotent via HasMadeInitialContact. Called from SimulationEngine.TickPostPhysics. Also inserts solo-training VFR Class B/C boundary holds from FAA AIS airspace data and ticks pending-request reminders.
Pilot/PilotPersonality.cs      # Enum controlling readback variation. Verbatim emits textbook form; Varied enables activity-aware solo-training shortcuts.
Pilot/PilotSayBuilder.cs       # Static: pilot-style transmission text for SAY-class verbs (SALT/SHDG/SPOS/SSPD/SMACH/SEAPP).
                               # AIM-compliant spoken phraseology (digit-by-digit, "thousand"/"hundred"/"flight level", "Mach point X").
                               # Used by CommandDispatcher for triggered/sequenced SAY blocks AND by yaat-server's SayCommandHandler
                               # for direct controller queries — same text, different routing layer adds the controller's initials.

# Speech/ — STT + phraseology rule engine (Yaat.Sim layer)
Speech/PhraseologyMapper.cs    # Static: transcript → canonical command (rule-based layer of the hybrid NLU).
                               # Pipeline: digit normalize → tokenize/strip filler → callsign extract → condition extract → longest-match against PhraseologyRules.All
Speech/PhraseologyRule.cs      # Single rule record: Pattern (literal / literal? / {capture} tokens) + OutputTemplate + CanonicalType
Speech/PhraseologyRules.cs     # Static catalog of all phraseology → canonical rules, organized by command category to mirror CommandRegistry
Speech/PhraseologyCommandMapper.cs  # ISpeechCommandMapper adapter so the rule engine can sit alongside the LLM fallback in the speech pipeline list
Speech/ISpeechCommandMapper.cs # Interface + MapContext record (active callsigns, programmed fixes, custom-fix patterns) shared by rule + LLM mappers
Speech/CanonicalCommandGrammar.cs   # GBNF grammar generated from CommandRegistry.AliasToCanonicType; constrains LLM fallback output to valid canonical commands
Speech/AtcNumberParser.cs      # Bidirectional spoken-numbers ↔ digit conversion (NormalizeDigits, FlightNumberToWords, AltitudeToWords)
Speech/CallsignParser.cs       # Spoken callsign ↔ ICAO callsign (TryParseLeading/Trailing for transcripts; IcaoToSpoken for prompt seeding)
Speech/AirlineTelephony.cs     # Static bidirectional airline ICAO ↔ telephony map; data from OpenFlights airlines.dat (ODbL 1.0)
Speech/AircraftTypeNames.cs    # Static ICAO type designator → spoken manufacturer/family name (e.g. C25C → "Citation"); preprocessed from vNAS AircraftSpecs
Speech/ScenarioCallsignExtractor.cs # Pulls custom telephony designators from scenario flight-plan remarks for whisper initial_prompt seeding
Speech/NatoPhoneticAlphabet.cs # Single canonical NATO letter ↔ word map consumed by every other Speech/ class
Speech/NatoLetterNormalizer.cs # Collapses runs of NATO words ("tango uniform whiskey") into single taxiway tokens; topology-aware via the airport taxiway set
Speech/TrafficCallsignNormalizer.cs # Collapses a spoken traffic callsign after "follow" / "behind" / "give way to" into one ICAO token (kept only when on frequency), before the NATO collapse; IsPrecededByCue keeps CallsignParser's trailing parse off that tail
Speech/NatoNearMissResolver.cs # Levenshtein-1 rewrite of Whisper NATO mishears; runs after custom-fix collapse and before callsign extraction
Speech/PhoneticFixMatcher.cs   # Fuzzy-match transcribed tokens against known fix names (Whisper transcribes fixes phonetically — this restores the canonical id)
Speech/CustomFixSpeechPattern.cs # Multi-token spoken pattern → custom-fix canonical alias (e.g. "runway 30 numbers" → OAK30NUM); built at NavigationDatabase load
Speech/WhisperBiasingPrompt.cs # Static initial_prompt assembled from ATC numbers, all PhraseologyRules literals, and the SCRAMBLED NATO alphabet (avoids the alphabetical-extrapolation prior)
Speech/Data/                   # Static reference data: airlines.tsv (OpenFlights), aircraft-types.tsv (ICAO Doc 8643 via vNAS), source .meta + LICENSE-OPENFLIGHTS.txt

# Data/
Data/NavigationDatabase.cs     # Static singleton: unified NavData fixes/runways/airways/SID/STAR indexes + lazy CIFP procedures.
                               # Access via NavigationDatabase.Instance (initialized at startup, SetInstance for tests).
Data/RouteExpander.cs          # Static: expands route strings (SID/STAR/airway/fix tokens) into ordered fix lists
Data/FiledProcedureLookup.cs   # Static FindSid/FindStar: the SID or STAR a filed route names, as its airport publishes it, without activating it; shared by a bare CVIA/DVIA (NavigationCommandHandler) and the client's Climb via SID / Descend via STAR quick-list rules
Data/ProcedureLegResolver.cs   # Static: resolves CIFP legs → typed ProcedureLeg sequence (keeps VA/VI/VM/CA heading legs + CD/VD/FD/FC/CR/VR distance/radial legs the flat resolver drops); extracts the active leading prefix for DepartureProcedurePhase
Data/CustomFixDefinition.cs / CustomFixLoader.cs  # Custom fix JSON loading from Data/ARTCCs/{ARTCC}/CustomFixes/*.json
Data/CustomProcedureLoader.cs  # Indexes ARTCC-supplied CIFP fragments (Data/ARTCCs/{ARTCC}/Procedures/*.cifp) — verbatim ARINC 424 records pinning a procedure the current FAA cycle dropped. NavigationDatabase.LoadCustomProcedures parses them eagerly via CifpParser (no second parser) and inserts them as tier 2 of GetSid/GetStar/GetApproach: current cycle -> ARTCC fragment -> cached prior cycles. Also registers a derived NavData-side body for procedures vNAS doesn't carry. Authored by tools/stash-procedure.py.
Data/ProcedureSource.cs        # ProcedureSourceKind (PriorCycle | ArtccCustom) + label (AIRAC cycle id / ARTCC id); null means the current FAA cycle. Drives CommandDispatcher.ProcedureSourceAdvisory.
Data/TaxiRouteDefinition.cs    # Per-route value type (path tokens, destination, canonical-command synthesis) carried in the sidecar's taxiRoutes section.
Data/AirportSidecarDefinition.cs / AirportSidecarLoader.cs / AirportSidecarCatalog.cs  # Unified per-airport ground sidecar from Data/ARTCCs/{ARTCC}/Airports/{airport}.json (avoidTaxiways + taxiRoutes + implicitConnectors + oneWayEdges + blockedTurns + adw + exitDirections + exitCapacity + movementAreaTaxiways/nonMovementTaxilanes override sections).
                               # NavigationDatabase.AirportSidecars exposes GetAvoidedTaxiways / GetTaxiRoutes / GetImplicitConnectors / GetOneWayConstraints / GetBlockedTurns / GetAdwWindows / GetExitDirection / GetExitCapacity. avoidTaxiways read by SearchContext.Compile for auto routes only (two-pass: avoided taxiway used only when destination otherwise unreachable; explicit TAXI unaffected). taxiRoutes validated lazily at menu-build time via TaxiPathfinder.ResolveExplicitPath; surfaced in GroundView's right-click "Preset taxi route" submenu. implicitConnectors authorize a named connector (e.g. SFO LF) in SearchContext.BuildAuthorizedTaxiwaySet only when the cleared sequence places its two 'between' taxiways adjacent, AND let SegmentExpander.Run thread that connector instead of crossing at the taxiways' apex; SegmentExpander also runs adaptive bridge-entry selection (shallow pass + deep pass on dead-end finds — issue #396). blockedTurns forbid one intersection corner (the L/F apex) for AUTO + explicit routes and suppress its fillet arc in Ground View. exitDirections override the default exit (turn-off) side per landing runway end — consulted first by AirportGroundLayout.InferPreferredExitSide, ahead of the GeoJSON turnoff (whose reciprocal-end value is derived by flipping) and the layout heuristics (issue #405, KMIA 26R).
Data/ExitCapacityDefinition.cs # exitCapacity sidecar DTO (ExitCapacityEntry) and the validated ExitCapacityRule (runway, taxiway, maxAircraft, maxAircraftAboveCwt, cwtThreshold) the loader produces; read via AirportSidecarCatalog.GetExitCapacity.
Data/OneWayConstraintDefinition.cs # One-way taxiway + blocked-turn DTOs (coordinate-polyline form: ordered [lon,lat] path. OneWay: path = allowed direction; block reverse|both. BlockedTurn: >=3-point L-shape through an intersection apex, always bidirectional/hard).
Data/Airport/Pathfinding/VisitedNodeSet.cs # Per-PartialRoute visited-node set: immutable sorted int[] (copy-on-Add, binary-search Contains); replaced ImmutableHashSet<int>
Data/Airport/Pathfinding/PolylineSnapper.cs # Shared snap-to-node + connected-span trace (direct edge / taxiway-restricted BFS) for the coordinate-polyline sidecar constructs; used by OneWayResolver and BlockedTurnResolver.
Data/Airport/Pathfinding/OneWayResolver.cs / OneWayMode.cs # Resolves oneWayEdges against a layout into a forbidden directed-move set (ConditionalWeakTable per-layout cache, one set per wake class: GetForbiddenMoves(layout, wakeClass) skips every constraint whose ExemptWakeClasses holds that class; aircraft-less searches pass AircraftlessWakeClass = Large). GetOneWayLaneTaxiways names every taxiway on any constraint span, ignoring exemptions — the one-way lanes the resolver may imply. SearchContext.IsForbiddenMove gates AutoRouter (HardExclude, auto routes) ; RouteMaterialiser emits a wrong-way warning (Warn, explicit routes + the auto two-pass fallback in TaxiPathfinder.RunWithAvoidance).
Data/Airport/Pathfinding/RouteCostFunction.cs # Single cost function for every search: distance, turn budget (head-node delta + a fillet's sweep), taxiway transitions, runway crossings; Fastest adds traversal time (straight at taxi speed, arc via GroundArc.TraversalSeconds) plus the corner's speed dip and nose-wheel-radius pivot sweep
Data/Airport/Pathfinding/GeometricAdmissibility.cs # Hard gates on every edge: per-category heading-change limit, MinSteerableArcRadiusFt (fillets tighter than the smallest category main-gear turn radius are not routable); PruningStateKey = (node, 1° arrival-bearing bucket, arrival taxiway) for the state-aware closed set
Data/Airport/Pathfinding/AutoRouter.cs # Flat A* over the ground graph (auto routes, detours, reach probes): Run / RunWithCost (also returns the search's accumulated cost), same-side centerline-run re-run, MaxExpansions cap
Data/Airport/Pathfinding/SegmentExpander.cs # Explicit named-taxiway resolver: per-segment local searches, junction picks scored by tail probes (ProbeTailCost + ProbeDestinationReachCost, the latter priced with the A*'s own cost), parking→taxiway bridge, implicit connectors, detour fallback. A parking-stop pick that reaches the destination only through the junction node without driving a straight edge of the named taxiway is rejected unless its extension stays confined to the cleared taxiways and apron (issue #454). The destination extension (ExtendToDestination) hard-excludes every uncleared movement-area taxiway (UnclearedMovementAreaTaxiways: not cleared, not RAMP, not a ramp taxilane, not runway pavement) and falls back to TryExtendViaImpliedLeadIn, which admits one such taxiway as the gate's or spot's implied lead-in when the route drives at most MaxImpliedLeadInFt (1,000 ft) of it before reaching apron (ImpliedDestinationLeadIn) — otherwise the caller holds the aircraft short of it (issue #461). The extension always hard-excludes one-way moves; after the lead-in it tries TryExtendViaOneWayLane (one uncleared one-way lane implied, never reaching a runway holding position; the lead-in obeys the same runway refusal). TryAutoRoutedStartLeg re-resolves a clearance whose first taxiway BridgeStartToTaxiway cannot reach behind an auto-routed start leg over nonmovement pavement (≤ MaxStartLegFt 4,000 ft, at most one implied one-way lane, no runway holding position). FindRampConfinedRoute is the ramp-only auto-route (movement-area taxiways excluded, one implied one-way lane at most). Implied lanes land in TaxiRoute.ImpliedLanes with RouteMaterialiser.NotInClearanceWarning ("M1 not in clearance")
Data/Airport/Pathfinding/PartialRoute.cs # Immutable linked-list search state: head node, arrival bearing, last edge/taxiway, accumulated cost, visited node set (copy-on-Add sorted int[]); MaterialiseEdges
Data/Airport/LandingThreshold.cs # Resolve(RunwayInfo, AirportGroundLayout? | GroundRunway?) -> the end's LANDING threshold: the pavement threshold projected downfield by the airport map's `threshold` displacement, along RunwayInfo's own course (so it stays on the centerline every along/cross-track calc uses). Falls back to the pavement threshold with no layout, which is what pre-existing replays depend on. Arrival-side callers (FinalApproachPhase, LandingPhase, LowApproachPhase, ApproachNavigationPhase, InterceptCoursePhase, PatternGeometry, ApproachGateDatabase, SoloTrainingEvaluator, and the §5-7-1.b.4 final window: ApproachCommandHandler.IsOnFinal, FlightPhysics.AutoCancelSpeedAtFinal, SPD's 5 nm-final rejection, the arrival-spacing and same-runway-protection passes) go through it; departure/surface callers deliberately do not. Datum table: landing-and-runway-exit.md.
Data/Airport/ExitCapacityResolver.cs # Resolves exitCapacity rules against a layout (per-layout cached, like AdwResolver) into ExitCapacitySegments: the rule's taxiway from the landing runway's exit hold-short to the parallel runway's (FindParallelRunwayCrossing). A rule that does not resolve to exactly one segment logs an Error and is dropped for that layout. SimulationEngine.Tick counts each segment's occupants and marks a full one's exit bar occupied for the arrival's exit choice.
Data/Airport/AdwResolver.cs    # Resolves the adw sidecar section against a layout (per-layout cached, same shape as BlockedTurnResolver) into AdwMark line segments: an Outer and an Inner tick per window, laid perpendicular to the arrival runway's final approach course at the published ranges from its landing threshold (positive = outbound on final, negative = past the threshold, down the runway). DtoConverter -> GroundLayoutDto.AdwMarks -> GroundRenderer.DrawAdwMarks. Display-only; no simulation behavior reads it. See ground-rendering.md.
Data/Airport/Pathfinding/BlockedTurnResolver.cs # Resolves blockedTurns against a layout (per-layout cached) into forbidden pivot turn-triples (prev,apex,next), forbidden bypass-arc moves, and hidden corner-arc pairs. Gated hard in AutoRouter + SegmentExpander (SearchContext.IsBlockedTurn / IsBlockedArcMove, both AUTO and explicit). HiddenArcPairs -> GroundArcDto.HiddenInGroundView (DtoConverter) -> GroundRenderer omits the arc.
Data/InitialContactTransferRule.cs / InitialContactTransferLoader.cs / InitialContactTransferCatalog.cs
                               # ARTCC/airport SOP exceptions for pilot initial-contact comm transfer, loaded from Data/ARTCCs/{ARTCC}/InitialContactTransfers/*.json.
Data/WakeDirectiveRule.cs / WakeDirectiveLoader.cs / WakeDirectiveCatalog.cs
                               # ARTCC static wake waivers and wake-advisory scoring directives, loaded from Data/ARTCCs/{ARTCC}/WakeDirectives/*.json.
Data/Airspace/AirspaceDatabase.cs # FAA AIS GeoJSON loader/query service: loads all Data/Airspace/*.geojson and *.geojson.br, volume containment, projected Class B/C boundary entry, under-a-Bravo-shelf test for the 91.117(c) speed cap.
Data/Airspace/AirspaceVolume.cs / AirspaceBoundaryCrossing.cs / AirspaceClass.cs # Airspace model primitives plus crossing result (point-in-polygon via shared GeoMath.PointInRing + LatLonBounds).
Data/Airspace/AirspaceAvoidance.cs # VFR self-restriction geometry: level-off altitude beneath a shelf floor (round hundred, 91.159-conforming above 3000 AGL) and the turn-away direction. See airspace-database.md.
Data/Airspace/faa-training-primary-class-bc.geojson.br # Checked-in Brotli FAA AIS fixture for B/C airspace at all vNAS training primary airports.
Data/Artcc/ArtccBoundaryDatabase.cs # Lateral ARTCC boundaries (one polygon per center) from Data/Artcc/*.geojson[.br]; FindById / FindContaining; same bundled-fixture + bbox + PointInRing shape as AirspaceDatabase. Center-room live-traffic scoping.
Data/Artcc/ArtccBoundary.cs        # One center's rings + bbox: Contains (any ring), DistanceToEdgeNm (the server's 30 nm center-room buffer)
Data/Artcc/ArtccBoundaries.geojson # 26 US ARTCC/CERAP MultiPolygons from FAA NASR ARB (LOW + HIGH rings; UNLIMITED where that is all there is);
                                   # built by tools/build-artcc-boundaries.py, re-run per 28-day cycle
Data/FieldElevationResolver.cs     # Resolves per-airport field elevation (AGL floor for display-floor gating); AcquisitionFloorAglFt stamp so a departure appears on display only once AGL>=100ft
Data/Mva/MvaDatabase.cs / MvaSector.cs / MvaRelation.cs # FAA AIXM-derived MVA sectors: exterior-minus-holes containment + altitude Classify (Below/At/Above). See minimum-vectoring-altitude.md.
Data/Mva/FAA_MVA_FUS3.geojson.br # Committed FAA MVA charts (FUS3), all 148 published facilities: 3,268 sectors with MSL floors + facility tags, Brotli-compressed, built by tools/build-mva-data.py --all.
Data/MilitaryRoutes/MilitaryRoute.cs / MilitaryRoutePoint.cs / MilitaryRouteAltitude.cs / MilitaryRouteType.cs # DoD AP/1B route model: one-way point sequence, per-segment altitude block, protected widths, and (chapter 5) per-direction variants with an anchor orbit pattern. See military-training-routes.md.
Data/MilitaryRoutes/MilitaryRouteDatabase.cs # Lazy process-wide Default + 3-tier fixture search + ScopedOverride (read from the NavigationDatabase ctor, so a leaked override poisons every later nav DB).
Data/MilitaryRoutes/MilitaryRouteExpander.cs # Expands a designator in a filed route, anchoring on the bracketing FRDs AP/1B files rather than on point names; scores the anchor pair to pick which published direction was filed.
Data/MilitaryRoutes/ap1b-mtr.json.br # Committed AP/1B chapters 2-4: 648 training routes (213 IR / 304 VR / 131 SR), 7,039 points, Brotli, built by tools/build-mtr-data.py.
Data/MilitaryRoutes/ap1b-ar.json.br # Committed AP/1B chapter 5: 247 aerial refueling entries (156 tracks / 91 anchors), 1,625 points + 385 orbit corners, built by the same tool.
Data/ARTCCs/                   # User-submitted per-ARTCC data root (CustomFixes, FixPronunciations, Airports, InitialContactTransfers, WakeDirectives, Procedures, SurfaceTempData — see Data/ARTCCs/README.md).
Data/ARTCCs/{ARTCC}/SurfaceTempData/{FACILITY}.json # Committed ASDE-X / SAAB SAID drawn geometry (restricted/closed areas + text, optionally filed into a numbered SET). Read by yaat-server's FacilityTempDataStore, which seeds every room from it and layers runtime controller edits on top. Authored by tools/build-surface-temp-data.py or exported from a live room via the client's Scenario menu.
Data/ARTCCs/ZOA/Procedures/koak-nimi.cifp # Pinned KOAK NIMITZ SID (NIMI5): charted and flown, but dropped from the FAA CIFP at cycle 2605. Carries the published 315 deg initial turn that the ~12-month prior-cycle chain would otherwise lose.
Data/FrdResolver.cs            # Fix-Radial-Distance ↔ lat/lon; IsFrdIdentifier gates FRD-named fixes
Data/LatLonParser.cs           # ERAM DDMM/DDDMM lat-long strings (//4220N/7110W) → lat/lon (CRR group locations)
Data/ApproachGateDatabase.cs   # Static: FAF->pavement-threshold distances from CIFP; GetFafDistanceNm(airport, runway, thresholdDisplacementNm) is the FAF distance to the LANDING threshold, InsideFafLimitNm the §5-7-1.b.4 inside-the-FAF limit; GetMinInterceptDistanceNm(airport, runway, thresholdDisplacementNm) finishes the §5-9-1 gate on the LANDING datum (P/CG: 1 nm outside the FAF, never closer than 5 nm to the landing threshold). Built at startup before any airport map exists, hence the read-time displacement.
Data/VideoMapMetadata.cs       # Video map metadata model
Data/VideoMapData.cs           # Video map data structures (lines, labels, filters)
Data/VideoMapParser.cs         # GeoJSON → VideoMapData
Data/HttpFileCache.cs          # Shared vNAS download→disk cache: AlwaysRefetch (/api, no HEAD/Last-Modified) vs HeadLastModified (/Files) freshness, optional disk-TTL skip, network-failure fallback (reported as `HttpCacheResult.RefreshFailed`). Used by AirportLayoutDownloader, ArtccAirportResolver, ArtccConfigService (server), VideoMapService, GroundViewModel tower-cab.

# Data/Airport/
IAirportGroundData.cs          # Interface: GetLayout(airportId) + GetSourceGeoJson(airportId)
AirportLayoutDownloader.cs     # Fetches airport ground GeoJSON from vNAS training API; caches under %LOCALAPPDATA%/yaat/cache/airports/
AirportGroundLayout.cs         # Graph: IGroundEdge interface, GroundNode, GroundEdge (straight), GroundArc (bezier fillet arc: P1/P2 control points + MinRadiusOfCurvatureFt, SafeSpeedForRadiusKts per category, SpeedProfile(category) cached local-curvature speed samples, TraversalSeconds). DirectionalEdge (traversal direction).
                               # GroundRunway.ThresholdDisplacementForEnd / LandingThresholdForEnd expose the vNAS map's per-end `threshold` displacement (Coordinates endpoints are *pavement* ends, and so are RunwayInfo's). Read by AdwResolver and by Data/Airport/LandingThreshold.cs.
                               # AllEdges (Edges+Arcs), FindAdjacentHoldShort (BFS, max 12 hops; returns Side; hops onto a joining taxiway's bar at a dead end, passes another runway's bar moving away; FindAdjacentHoldShortForListing for enumerations), FindExitFromCenterline (walk centerlines, returns side+walk node), FindOnSidePreferredExit (lookahead: defer off-side, prefer later on-side), FindExitPath, FindNearestHoldShortAhead, FindExitAheadOnRunway, ComputeExitAngle
CubicBezier.cs                 # Bezier math utilities; used by FilletArcGenerator (arc generation) and GroundNavigator (path following)
IFilletArcGenerator.cs         # Pluggable fillet contract; None + Standard implementations; FilletMode on GeoJsonParser.Parse
FilletMode.cs                  # Fillet mode enum: None (no-op pass) / Standard (the real generator)
NullFilletArcGenerator.cs      # No-op generator for FilletMode.None and raw-layout tests
FilletGeneratorFactory.cs    # FilletMode → IFilletArcGenerator (None / Standard)
FilletArcGeneratorRegistry.cs# Enumerates implemented generators (None, Standard)
FilletStatistics.cs          # Per-pass fillet tallies returned by Apply
FilletArcGenerator.cs      # The fillet generator: classify junctions → resolve cuts → plan → execute → normalize
Fillet/                        # Plan-then-execute fillet pipeline (edge-split connectivity)
  FilletGeometry.cs            # Turn angle, ideal tangent, cubic-bezier build (control points project toward the junction)
  FilletGraphNormalizer.cs     # Post-execute: recompute distances, drop self-loops/degenerate arcs, sweep isolated nodes (no coincident-node merge — plan guarantees none)
  CutId.cs                     # Type-distinct cut identifier (no Origin-string parsing)
  FilletEndpoint.cs            # Type-distinct fillet endpoint; with CutId lets cleanup passes pattern-match instead of parsing Origin strings
  TaxiwayArmBuilder.cs         # One arm per outbound edge; TaxiwayWalk along same-named taxiway
  JunctionClassifier.cs        # Eligibility + Skip/Simple/MultiCorner/Preserve + collinear pairs
  CornerPlanner.cs             # Arm-pair corners (≥15°) and collinear pairs (<15°)
  ArmCutResolver.cs            # Tangent-cut placement per arm; corner arcs + straight connectors; tangent merges
  FilletEdgeSplitPlanner.cs    # Order-independent connectivity: split each original edge once by its cuts, drop only removed-junction stubs → SurvivingEdgeOp
  FilletPlanBuilder.cs         # Assemble the immutable FilletPlan (cuts, merges, corner arcs, surviving edges, nodes/edges to remove)
  FilletPlanExecutor.cs        # Materialize cut nodes + surviving edges + corner arcs (degenerate arc → straight chord) in one pass; remove consumed edges + removed junctions
  FilletPlanCutRedirect.cs     # Union-find survivor map for tangent merges + stable-anchor binding
  (also FilletPlan/JunctionPlan/CornerSpec/ResolvedArmCut plan model + TaxiwayArm(Terminus), JunctionKind, FilletEligibility, ManualArcDetector, SharedArmTangentPass, PlanWarning, FilletConstants, FilletPlanConsistency)
RunwayIdentifier.cs            # Struct: runway designator parsing/matching; NormalizeDesignator (zero-pad canonical) + ToDisplayDesignator/ToDisplayString (FAA no-leading-zero display form)
                               # FromApproachId: extracts the runway designator from a procedure id ("I29RY" → "29R", "VDM-A" → null for circling); shared by CifpParser and InterceptCoursePhase.GetRunwayHeading
TaxiRoute.cs                   # Resolved path: TaxiRouteSegment (DirectionalEdge wrapping IGroundEdge) + HoldShortPoints (with dynamic lat/lon offset) + DestinationParking/DestinationSpot + completion. ToSummary takes an optional includeTaxiway filter so a caller can render only the clearance as issued (GroundCommandHandler's TAXI readback). HoldShortReason.RouteIncomplete marks a route's end held short of a taxiway the destination needs but the clearance did not reach (issue #461). ImpliedLanes lists the one-way lanes the resolver implied, uncleared (named in the readback, exempt from the not-in-the-route-issued warning; not snapshotted)
TaxiRouteAutoCross.cs          # Applies AutoCrossRunway toggle to a route's RunwayCrossing hold-shorts; reused at TAXI-resolution and on mid-session toggle (SimulationWorld.ApplyAutoCrossToActiveTaxiRoutes)
TaxiRouteFormatter.cs          # SINGLE owner of route -> taxiway-name extraction: TaxiwayLegs (decomposes junction composite labels "W - W6"=GroundArc.TaxiwayNames, stays on the name being followed, drops RAMP, flags runway legs) + CleanTaxiwaySequence (legs minus runways, readable TAXI form) + BuildReadableTaxiPath (clean names + terminal #node pin for a mid-taxiway stop). TaxiRoute.ToSummary/FormatTaxiwaySequence call TaxiwayLegs so the readback, Aircraft List column, and draw-route Copy cannot drift apart
TaxiPathfinder.cs            # Taxi pathfinder (static; every public method takes the aircraft's AircraftCategory and WakeTurbulenceData.WakeClass, carried on SearchContext.WakeClass to pick the one-way constraints that bind): FindRampConfinedRoute (node→node inside the ramp, SegmentExpander.FindRampConfinedRoute), ResolveExplicitPathDetailed (SegmentExpander, structured PathfindingFailure for handler reaction — issue #396), ResolveExplicitPath (message-only wrapper), FindRoute/FindRoutes (A* AutoRouter, per-preference), FindRunwayRoute (TAXIAUTO), FindAdjacentRunwayRoute (bare TAXI <rwy>: only the bar the aircraft is already at / a ≤600 ft straight run to it, else null → refused), FindFullLengthLineupHoldShort. Along-runway travel is hard-limited to runways named in the clearance (SearchContext.AllowedCenterlineNames + same-side-run validation in AutoRouter); a bare final taxiway holds at its transition junction. See Data/Airport/Pathfinding/ + docs/ground/pathfinder.md
ExplicitPathOptions.cs         # RoutePreference enum + ExplicitPathOptions input bag (pathfinder inputs)
HoldShortTarget.cs             # Structured HS target: Target (taxiway/runway/spot) + optional OnTaxiway location — the C@J command form (issue #358) — or IsSpot for the $17 spot form (issue #394; MatchKey keeps the $ sigil in HoldShortPoint.TargetName); TryParse/Parse/ToCanonical round-trip both syntaxes
VirtualNode.cs                 # Factory for virtual ground nodes (position-hashed negative ids — deterministic across processes and snapshot restores); CreateEdge, CreateSegment, OffsetBefore/OffsetPast
MovementAreaClassification.cs  # Per-layout movement vs non-movement (ramp taxilane) verdict per taxiway name, five ordered rules (single letter/runway/RAMP; runway hold-short -> movement; parking gate one edge off -> taxilane; joins 2+ single-letter taxiways -> movement; else taxilane) plus the sidecar overrides both ways
RampLaneReposition.cs          # TryPlanRampConfinedRoute = the ramp-confined route of a plain TAXI naming only a gate/spot from inside the ramp (StartsOffMovementArea): the ramp graph (TaxiPathfinder.FindRampConfinedRoute), else one clear free-space cut across the apron between ramp-confined head and tail (TryPlanRampConfinedCut; FindLegObstacle wingtip check shared with the spot line-up). Ramp-lane cuts the map does not connect: IsRampTaxilane (reads MovementAreaClassification)/AreSiblingLanes, TryPlan = start cut onto the first cleared lane (SFO M3 → M4, #396), TryPlanDestinationCut = a direct cut from the named lane straight to the stand tried first (TryPlanDirectStandCut, issue #454), else last cleared lane → apron → the stand's sibling lane (OAK TE → TC @22, #400); every stand cut rolls in on the stand's heading (RollInApproachNode: one fuselage out on the centreline, on the heading's reciprocal) rather than arriving at the crossing's own angle — TryPlanResolvedRouteCut (now given the aircraft's fuselage length) does the same for a route that already resolved the long way round; free-space VirtualNode legs + graph routes; EntersSpotAlongItsLane requires a spot destination's tail to land on the spot's own lane with at least one fuselage length of run (SpotAlignmentRunFt, floored at 100 ft) so the aircraft is straight by the marking instead of crossing it
TaxiApproachLeg.cs             # Bridges an aircraft's live position to the node its resolved taxi route starts at with a free-space RAMP leg (a VirtualNode segment), so a PUSH that rests short of the route's start node is driven to it instead of teleported onto the route in one tick; also bridges a landing rollout to the runway-exit fillet ahead via the along-runway-roll case
TugKinematics.cs               # The tug-move motion body (pure, deterministic): TugPose/TugMove (Straight, ToPoint, ViaLine with the roll-out capture law, TurnTo; Tight/Creep/DwellBefore flags), curvature-limited SteerTravel (turns by at most step/R — a stopped aircraft never rotates), Advance/Record/IsComplete, Simulate (per-move traces sampled every 5 ft), TurnRadiusFt from the FAA wheelbase. Shared by TugMovePlanner (to plan) and PushbackPhase (to fly) — docs/ground/pushback.md
TugParkedNeighbours.cs         # TugNeighbourCandidate (plain per-aircraft input: pose, type, stand, hold/phase/speeds) + Build: the parked/held aircraft within RangeFt (400 ft) of a tug start pose, excluding self and any already-overlapping one; shared by GroundCommandHandler and the client push-route preview; FindStartOverlap → TugStartOverlap (its Refusal is the one OverlapRefusal text both sides show)
TugMovePlanner.cs              # Plans every PUSH/PUSHF form and PUSHM/PUSHMF as a chain of TugMoves: TugGoal (Spot/Stand/Node/TaxiwayLine/StraightBackTo/Facing/Clear), TugRequest → TugPlan; stand push-off, faced goals via T1/T2/T3 candidate templates simulated and ranked (fewest reversals, shortest path, then template order) — except a lane goal (a spot reached off a stand) instead ranks by path length plus a penalty on lateral departure from the stand's lead-in line and gets a straight-then-line T0 candidate per side (push-off, straight back along the lead-in line, then a push capturing the spot's lane), and a TaxiwayLine goal with a facing taxiway (PUSH <twy> <facing-twy>) ranks by least total nose rotation: near the facing's junction, candidates in both directions along the taxiway end nose-toward it one routine turn radius short; farther out the facing only picks the direction, ending as soon as the push is lined up (TugPlan.FacingTaxiwayIsFar past FarFacingJunctionFt = 1,500 ft) — bare PUSH <twy> split across/alongside at AcrossAngleDeg (45°), TugAmendment for the #167 mid-push facing change, SpotStopGeometry; the chosen candidate for a non-movement-area goal is then run through TugTaxiwayClearance (alley clearance), which may swap it for one — including a fouling-fallback T0 with a shortened straight, and as a last resort a stepped T4 — that stays outside the taxiways' object-free areas (KeepOffTheMovementArea); every lane candidate passes the ±100° swing band and ranks below stand-clear ones when it enters an empty stand's footprint (TugEmptyStands); when a parked neighbour drops a candidate, TugLazyPools adds angled push-offs, extended straights and the multi-point T5 before refusing; TugPlan carries structured Warnings (TugFoulsTaxiwayWarning, TugLongPushWarning past LongPushToTaxiwayFt = 500 ft) that GroundCommandHandler turns into RPO-only readback notes; refuses through TugPathCheck, or NeighbourRefusal — a Faced-goal candidate whose flown path swings into one of TugRequest.ParkedNeighbours (fed by TugParkedNeighbours.Build, 400 ft of the start pose, on the server and in the client preview alike), the same GroundOutlineSweep floor GroundConflictDetector holds a move under way to, so the planner never accepts a candidate the detector would dead-stop mid-manoeuvre; single-shape goals have no alternative template to fall back on and keep going to the tug's own outline-stop instead. A forced request (TugRequest.Forced) skips the taxiway/alley-clearance/overswing/parked-neighbour rules that would refuse or steer a plain tow away (IsClear always true, no swing band, TugPathCheck.Check judges only the runway and holding-position rules), records each rule its kept candidate broke as TugPlan.ForcedOverrides (NoteForcedOverrides: TugPathCheck.SkippedTaxiwayHits for the taxiway rules, the alley clearance and overswing via NoteForcedFouling/NoteForcedOverswing, the parked-neighbour sweep via NoteForcedNeighbours/NeighbourPass — AddOverride keeps one entry per rule, a neighbour's at the closest pass), ranks past a parked neighbour by TugNeighbourClearance.Shortlist first when the chosen candidate breaks the floor (RanksPastNeighbours/ForcedPastANeighbour/BestPastNeighbours: keeping the floor beats the most room beats an overlap, within DecidingMarginFt = 5 ft of the best), and — when a faced goal keeps no candidate and none was refused or hint-dropped — falls back to ForcedFallbackCandidates: a stand push-off then a geometric tow to a point on the goal's approach line at ForcedFallbackRunInRadii line-capture radii back, either side, then the move onto the line, ranked by IsShorterTow (path length, then reversals) rather than the real-push rotation keys
TugPathCheck.cs                # Flown-path check for a candidate plan: footprint vs runway half-width, footprint side vs holding-position edges, taxiway goals: the centre's overshoot past the goal taxiway (half a wingspan); other goals: fuselage vs movement-area pavement the goal did not name (exempt: the taxiway straight behind the stand within 300 ft; per-goal leaving/arriving runs seeded by the pavement the fuselage stands across, extended along the same-name chain within a fuselage length); FirstCrossing/NearestAlongside ray helpers for the straight-back rule. Check's forced flag (PUSHF/PUSHMF) limits the judged rules to the runway and holding-position ones; SkippedTaxiwayHits instead returns every taxiway rule a forced tow's kept candidate broke — the overshoot, or each movement-area taxiway crossed, one per taxiway rather than refusing at the first — for TugPlanBuilder.NoteForcedPathOverrides to record as TugPlan.ForcedOverrides
TugPavementClassifier.cs       # Classifies the pavement a tug move would cross (runway / holding position / movement-area taxiway vs ramp), reading MovementAreaClassification so TugPathCheck can test every edge on the field; a leg may touch movement-area pavement within a set distance of either end
AirplaneDesignGroup.cs         # AirplaneDesignGroup enum (I-VI, AC 150/5300-13B Table 1-2) + AirplaneDesignGroups: MaxWingspanFt, TaxiwayObjectFreeHalfWidthFt (0.7·W+10, Table 4-1) and TaxiwaySeparationFt (1.2·W+10) per group. FromRunwayWidth buckets an airport's own group from its widest runway (shared by HoldShortAnnotator.WingtipClearanceFloorFt); ForTaxiway derives one taxiway's group as the smaller of the airport's own and the largest group whose TaxiwaySeparationFt fits the spacing to its nearest parallel movement-area taxiway (within 15° of heading, held for at least 300 ft, sampled every 25 ft) — cached per layout and MovementAreaClassification
TugTaxiwayClearance.cs         # The alley clearance: how far a tug move's outline (fuselage/wing/tailplane, GroundOutline without the tug's lead) reaches into the object-free area of the movement-area taxiways near it, against each one's AirplaneDesignGroups half-width plus a 5 ft margin (ClearanceMarginFt); a wider taxiway's centreline is clipped where a narrower one's own zone overlaps it, so a taxiway running up to and across a narrower one protects nothing past the narrower one's own zone. TaxiwaysFouledAt (which taxiways the outline already touches at a pose, exempted from later checks); Measure/Fouls scan a candidate's flown path for the deepest penetration (TugTaxiwayFouling: taxiway, TugFootprintPart, peak depth, and exposure = penetration × feet flown). Built once per layout + classification and shared by every plan at the airport; used by TugMovePlanner to keep a spot/stand/node push out of the taxiways it does not name
TaxiwayGraphBuilder.cs         # Graph construction from GeoJSON nodes/edges
GeoJsonParser.cs               # GeoJSON→layout; DetectRunwayCrossings via SplitEdgeAtNode
CoordinateIndex.cs             # Spatial index for coordinate-based lookups
RunwayCrossingDetector.cs      # Detect taxiway/runway crossings; seat hold-short bars at the constant perpendicular standoff (geojson holdShortDistance, else AC 150/5300-13B width heuristic) — see docs/ground/hold-short-placement.md
RunwayIntersectionCalculator.cs # Runway centerline/projected-path intersections for LAHSO and solo-training runway scoring. Reported distances are on the PAVEMENT datum (their consumers are departures rolling from it); ComputeHoldShortDistanceNm is the exception and subtracts the displacement, so a LAHSO distance is an available landing distance.
RunwayEntryPoint.cs            # Classify a runway hold short as full-length vs intersection departure for a given end: nearest hold short (along-track on the pavement centerline) is full length, plus an OPPOSITE-side one within OppositeSideBandFt or on the same taxiway (one entrance reachable from both sides); SAME-side extras are always named intersections. Display only, feeds RunwayDepartureQueue
HoldShortAnnotator.cs          # Public: annotate hold-short points on taxi routes; ComputeHoldShortPositions offsets taxiway HS by fuselage length (skips an Unable bar, whose position is the stop TaxiingPhase moved it to; the client's push preview calls it). Owns RouteCrossesRunwayAfterStart, the shared "is the route's start bar the entry side of a crossing it makes" predicate — RouteMaterialiser.AnnotateStartCrossing uses the same one so the two annotators cannot drift (see docs/ground/pathfinder.md). WingtipClearanceFloorFt (the crossed-taxiway wingtip clearance floor) is now derived from AirplaneDesignGroups.MaxWingspanFt(AirplaneDesignGroups.FromRunwayWidth(...)) / 2 + 25 ft rather than its own hardcoded per-bucket table — same figures, one source of truth shared with the alley clearance
RunwayCrossingEnd.cs           # Which end of a crossed runway to name (nearest threshold) from a bar's combined "28R/10L" target — the pilot's hold-short report and the AI's CROSS agree

# Data/
AircraftProfile.cs             # Per-type performance profile record (from AircraftProfiles.json). GroundAccelRate is nullable —
                               # the bulk BADA-derived data has no such field, so it's set for only a handful of hand-checked
                               # types (fighters, P3); null falls back to the category rate (AircraftPerformance.GroundAccelRate)
AircraftProfileDatabase.cs     # Static lookup: Get → merged AircraftProfile?; Initialize(base, overrides), IsOverridden
AircraftProfiles.json          # ATCTrainer per-type perf data: altitude-banded climb/descent, Mach speeds
AircraftProfileOverride.cs     # Nullable partial-override DTO + ApplyTo merge (authoritative per-type corrections)
AircraftProfileOverrides.json  # Contributor corrections layer (seeded with SF50); see docs/aircraft-performance.md
EurocontrolProfileCorrectionAdapter.cs  # Runtime ACD-anchored correction of 6 profile fields (BADA speeds run high)
OverrideAwareProfileCorrectionAdapter.cs # Wraps the above; overridden fields bypass it (overrides win)
AirlineFleets.cs               # Static map: airline ICAO ↔ ICAO Doc 8643 aircraft type with airframe counts
                               # Both directions pre-computed; loaded lazily from airline-fleets.json
                               # Refresh via tools/refresh-airline-fleets.py — see docs/airline-fleets.md
airline-fleets.json            # Generated map (Airfleets World Fleet Listing, paid quarterly snapshot)
airline-fleets.meta            # Provenance sidecar (per-PDF SHA-256, parsed counts) — committed alongside
AirportAirlines.cs             # Static map: airport IATA/ICAO -> served airline ICAO list for arrival-generator callsign selection. See docs/airport-airlines.md.
                               # Loaded lazily from airport-airlines.json.br; normalizes K/P-prefixed U.S. ICAOs to local IDs
airport-airlines.json.br       # Generated Brotli fixture from BTS T-100 segment data for current generator airports
                               # OpenFlights route backfill is used only for airports missing BTS carrier hits
airport-airlines.meta          # Provenance sidecar with source ZIP row counts, target airports, and unmapped BTS carriers
AircraftDisplayNames.cs        # Static map: ICAO aircraft type → human-readable display name (e.g. "B738" → "Boeing 737-800").
                               # Loaded lazily from aircraft-display-names.json; used by the Aircraft List Name column and radar/EuroScope/right-click fallbacks when no flight plan is filed.
aircraft-display-names.json    # Generated map from tools/refresh-aircraft-display-names.py (one entry per ICAO type in AircraftSpecs.json).
aircraft-display-names-source.meta # Provenance sidecar: model + prompt hash + ICAO type list used at generation time.

# Data/Faa/
FaaAircraftRecord.cs           # Full FAA ACD row: wingspan, length, tail height, gear geometry, MTOW, classifications
FaaAircraftDatabase.cs         # Static lookup: Get(aircraftType) → FaaAircraftRecord?; used for physical dimensions
AircraftLength.cs              # ResolveFt(type): the one aircraft-length resolver — FAA length, else CwtFallbackLengthFt (the CWT-bucket table) for types the database lacks
FaaAircraftDataService.cs      # Downloads FAA ACD xlsx, parses all columns, caches per AIRAC cycle

# Data/Vnas/
VnasDataService.cs             # Downloads NavData protobuf + specs; serial-based cache
NavDataPathResolver.cs         # Test/offline NavData.dat resolve: vNAS cache, download, TestData fallback
CifpPathResolver.cs            # Current AIRAC CIFP: cache, FAA download, bundled gz fallback; supplementary = recency-capped chain of cached prior cycles, newest→oldest (retired/renamed procedures)
AiracCycle.cs                  # AIRAC cycle calculator (epoch Jan 23 2025, 28-day); CyclesBetween() for the supplementary recency cap
VnasConfig.cs                  # Config API DTO
CacheManifest.cs               # Cache manifest tracking serials
AircraftSpecEntry.cs           # VNAS aircraft specs model
AircraftCwtEntry.cs            # VNAS aircraft CWT model
ArtccConfig.cs                 # ARTCC config models (ArtccConfigRoot, FacilityConfig, PositionConfig, TcpConfig, StarsConfig, etc.)
ArtccConfigResolver.cs         # Pure-function resolvers as extension methods on ArtccConfigRoot:
                               # ResolvePosition / ResolveTcpCode / ResolveEramCode / ResolveEramToStarsHandoffCode (Q2B-style ERAM→STARS prefix) / FindPositionByCallsign / FindTcpByCode /
                               # ExpandTcpShorthand / GetCoordinationChannels / GetAllAsdexAirports / GetAllTowerCabAirports /
                               # GetAllAccessibleStripBays (display set) / GetAllCommandTargetableStripBays (authorization set) / GetAccessibleStripBay(position, facilityId, bayName) /
                               # GetAccessibleStripFacilities (own + descendants + externalBays-linked) / GetAccessibleTdlsFacilities (own + descendants, with MemberFacilityIds for the consolidated parent page) /
                               # GetConsolidationItems / GetConsolidationOwner /
                               # GetSidInitialAltitudeFt (departure TDLS initial-altitude cap) / etc.
                               # Server's ArtccConfigService delegates to these; replay applier uses them via TrackResolver.
ArtccAccessRecords.cs          # AccessibleBay, AccessibleFacility, AccessibleTdlsFacility (+ MemberFacilityIds), AsdexAirportInfo, TowerCabAirportInfo records used by the resolvers.
BeaconCodePool.cs              # Discrete-code allocator. AssignNextCode(isVfr) draws from the ARTCC config's banks
                               # (Ifr→Any for IFR, Vfr→Any for VFR), falling back to sequential 0001-7777 octal when no
                               # matching bank exists or a bank is exhausted; returns 0 only when all 4096 are in use.
                               # Tracks assigned codes (MarkUsed/Release) so no two live aircraft share one.
                               # IsAssignableCode is the single reserved-code gate for every assigning path: excludes
                               # non-discrete codes (ending in 00), the whole 7500-7777 block, the monitored VFR
                               # conspicuity codes (1202/1203/1255/1276/1277), and the DoD 5000-5062 block.
                               # Callers: ScenarioLoader.AssignSpawnBeacons (post-load pass, after banks are configured),
                               # AircraftGenerator (ADD/arrival IFR spawns), SimulationEngine (FP file + amend).
                               # SimulationWorld.GenerateBeaconCode (RANDSQ) reuses only the IsAssignableCode gate.
                               # NextCandidate/BankCursors + RestoreCursors round-trip the draw cursors through snapshots.
CifpDataService.cs             # FAA CIFP zip download/extract per AIRAC cycle
CifpAirportIndex.cs            # Once-per-file byte-range index of each airport's SUSAP records (list of ranges — some airports are split); the airport-scoped CifpParser entry points read through it instead of streaming the whole file
CifpParser.cs                  # ARINC 424 parser: approaches (subsection F), SIDs (D), STARs (E), airport magnetic variation (A), navaids with their station declination (D, PN: ParseNavaids -> CifpNavaid), airport runways (G: ParseRunwayThresholdElevations -> per-END landing threshold elevation, the glidepath datum NavData has no per-end value for); FAF fixes, terminal waypoints
                               # Approach runway extracted via RunwayIdentifier.FromApproachId (shared with InterceptCoursePhase.GetRunwayHeading)
                               # ParseTerminalWaypoints: per-airport section-C waypoints for RF center fix + leg fix resolution
CifpModels.cs                  # CIFP data models: CifpApproachProcedure, CifpSidProcedure, CifpStarProcedure, CifpLeg, CifpTransition
                               # CifpLeg: ArcRadiusNm, ArcCenterLat/Lon (RF), RecommendedNavaidId, Theta, Rho (AF), FixLat/FixLon (CIFP terminal-waypoint coords)
                               # CifpLegExtensions.ResolveFixPosition: leg fix -> position, CIFP coords first then NavigationDatabase (covers CNFs absent from vNAS NavData)
CrossingRestrictionLabel.cs    # Formats a CifpAltitudeRestriction/CifpSpeedRestriction into ≥/≤ FL-aware label lines for the "Show nav route" overlay; server-side, fed into NavRouteFixDto.RestrictionLines by DtoConverter

# Scenarios/
ScenarioLoader.cs              # JSON → ScenarioLoadResult; resolves starting conditions, nav routes, beacon codes
InitialCallupClassifier.cs     # A spawn's presets + spawn kind → its InitialCallupPlan (who makes the solo first call, and when); the client's pre-load pacing hint reuses ClassifyPresets
ScenarioResourceManifest.cs    # Scenario JSON → the ARTCC, neighbour ARTCCs and airports a load will fetch (shares the loader's airport chains), and the airports that need a full ground map; for the server's prefetch and its load warnings
ScenarioExporter.cs            # The loader run backwards: a room's live aircraft (simulated + live-traffic shadows) → Scenario JSON, for the server's
                               # ExportRoomAsScenario. Each aircraft becomes Parking/OnRunway/OnFinal/Coordinates by the same rules the loader restarts
                               # them under; anything it cannot reproduce exports as Coordinates and is flagged (ScenarioExportFlag) instead of guessed.
                               # See docs/scenario-loading-and-generation.md § Export.
ArrivalRouteResolver.cs        # Shared STAR-route builder: PopulateNavigationRoute + ApplyAltitudeProfile (descend-via
                               # overlay). Used by ScenarioLoader (onAltitudeProfile arrivals) + AircraftGenerator (ADD on-STAR)
ScenarioModels.cs              # Scenario JSON DTOs: Scenario, ScenarioAircraft, StartingConditions, PresetCommand,
                               # IGeneratorConfig + the three generator configs (IFR arrival / VFR arrival / overflight), GeneratorsPayload
                               # ScenarioGeneratorConfig (renamed to avoid collision with AircraftGenerator static class)
ScenarioIdentity.cs            # Shared scenario ID fallback hashing/normalization for server load and sim replay
ScenarioValidator.cs           # Offline scenario checks (preset parse, SID/STAR versions, transition fixes, physical-vs-filed aircraft type); driven by the yaat-server CLI + Discord workflow, see docs/scenario-validation.md
                               # ScenarioValidationResult, PresetParseFailure, ProcedureIssue, ProcedureIssueKind records
                               # Detects outdated procedure versions (VersionChanged) and missing procedures (NotFound)
AircraftInitializer.cs         # InitializeOnRunway/AtParking/OnFinal → PhaseInitResult; FinalApproachPoint = the on-final position + glide-path altitude InitializeOnFinal and the generator spawn-clear check share
DepartureSpawnClassifier.cs    # IsHeldSpawnCandidate(loaded) — classifies a departure for hold-for-release spawn-gating
AircraftGenerator.cs           # SpawnRequest → AircraftState (runtime spawn generator); CategoryFor(EngineKind) = the category every pool type for an engine kind must categorize as
GeneratorActivation.cs         # IsActive(config, elapsed) = Enabled ?? time window; the single per-tick activation gate
HemisphericAltitude.cs         # 14 CFR 91.159(a) VFR cruising altitudes: snap a level overflight to odd/even+500 by course
VfrSpawnSiting.cs              # VFR spawn gates: clear of Class B/C, clear of standard radar separation; bearing/range rolls
SpawnRequest.cs                # Spawn descriptor
ScenarioRatingClassifier.cs    # Maps VATSIM rating short/long forms (S3 / Student3 etc.) to an ordinal;
                               # IsRatingSufficient(rating, required) for the scenario gate and
                               # IsInstructorOrAbove(rating) for the server connection gate. Shared by the
                               # client picker filter and the server-side gating decision.

# Simulation/
RunwayOccupancy.cs             # Phase-independent runway-use classifier (RunwayUseKind: Departing/Landing/OnSurface/ShortFinal/Crossing);; IsAlongRunway (on the pavement + aligned: a use of the runway other than crossing, 3-1-3.a.4)
                               # IsOnFinal (6 nm advisory ring), ClassifyBest (oriented pavements, LandedOnRunway-aware), AirportRunways (FAA/ICAO).
                               # Phase evidence first (ClassifyByPhase), geometry (pavement rectangle + axis alignment on the ground,
                               # TCH + final-approach course in the air; rotorcraft over the pavement below 100 ft AGL = surface movement — descending
                               # Landing, < 20 kt OnSurface, else axis — never ShortFinal/Departing) only for phase-less aircraft; AirTaxiPhase /
                               # HelicopterLandingPhase by phase; IsRolling = 35 kt, or 20 kt + 2.5 kt/s over 4 s of feed samples; landing-threshold
                               # distance/time helpers.
                               # Consumed by RunwaySafetyAdvisor, GroundConflictDetector.IsOnRunway, SoloTrainingEvaluator.IsTakeoffRoll.
Attendance.cs                  # CRC attendance as engine state (the first recorded input, ADR 0003): Attendance — Replace(positionIds, config) resolves each vNAS position id to
                               # its owner + TCP through the room's ARTCC config (an unresolvable id stays id-only); IsTcpAttended / IsPositionIdAttended / IsOwnerAttended,
                               # ConsolidationOwnerOf + IsTcpControlledByCrc (the tick gates' question over the facility hierarchy + ConsolidationState); PositionIds for the
                               # snapshot and the record. Written on the tick thread only (the live sync, the router, restore) — no lock. + AttendedPosition(PositionId, Owner?, Tcp?)
SimulationEngine.cs            # Scenario load, tick orchestration, replay (ReplayFromStartTo — full from-scratch replay;; ControllerAi + TickControllerAi() (post-second AI tick; never in replay/playback) + Actions (the ActionRouter every controller action goes through) + LocalConnectionId (what a bare SendCommand issues under) + RecordAction
                               # FastForwardTo — advance from current time; ReplayRange — between two timestamps;
                               # ReplayOneSecond/SubTick — stepping);
                               # CaptureSnapshot/RestoreFromSnapshot; reattaches GroundLayouts to delayed spawns on restore.
                               # RehydrateRestoredQueueBlocks rebuilds restored blocks' ParsedCommands/ApplyAction from SourceCommandText
                               # each TickPhysics (shared by both hosts) so queued commands survive rewind/replay/restore.
                               # ApplyPostDispatch is the single post-command hook both hosts call (see solo-training-pilot-speech.md).
                               # DeleteAircraft is the sim-side half of DEL (stamps CompletionReason.Dropped, clears a queued delayed
                               # spawn, removes) — the live server and replay both route through it.
                               # TerminalEntryEmitted event fires for every terminal entry (command echoes, preset outcomes, warnings) so
                               # subscribers react as entries happen instead of polling DrainTerminalEntries (issue #396)
                               # Split across partial files by cluster (ownership map: tick-loop.md § the engine's partial files).
                               # SimulationEngine.cs itself keeps engine state, the lifecycle events and the terminal-entry sink.
SimulationEngine.Snapshots.cs  # CaptureSnapshot/RestoreFromSnapshot + the server's slice (CaptureServerSnapshot/RestoreServerSnapshot). Restore re-binds each live aircraft, and each delayed-spawn aircraft, to its own airport's layout from the persisted Ground.LayoutAirportId (primary layout as the live-aircraft fallback)
SimulationEngine.Scenario.cs   # LoadScenario + ResolveGroundLayout (per aircraft) + ResolveAirportLayout(airportId): the layout of a RUNWAY's own field — World.GroundLayout for the primary airport, the ground data's otherwise, null with no map — which every threshold measurement about a runway resolves through
SimulationEngine.Spine.cs      # The segment entry points every run kind advances a sim-second through — RunSecond, BeginSecond, OpenSecond,
                               # RunPrePhysics, RunPhysicsSubTick, RunPostPhysics, RunEndOfSecond — and the runner that iterates SpineOrder,
                               # records StepTrace and times steps into TickTimings. BareHost (the Test-run host) lives on the engine.
SimulationEngine.Tick.cs       # The engine's own step bodies: TickPrePhysics/TickPhysics, the detectors, TickPilotProactive, TickControllerAi,
                               # and the bare-host wrappers TickOneSecond/TickPostPhysics. The order is SpineOrder's, not this file's.
                               # TickEramConflictAlerts returns an EramConflictAlertChanges (SimulationEngine.cs): New, Cleared, Suppressed, Restored and Reclassified — the
                               # standing alerts whose Mode C Intruder status flipped, so a host that filters by it re-evaluates them
                               # BuildOccupiedHoldShortNodes also counts each exit capacity segment's occupants; OccupancyForExitChoice adds a full segment's exit bar to an arrival's OccupiedHoldShortNodes
SimulationEngine.Replay.cs     # Replay entry points (ReplayFromStartTo/FastForwardTo/ReplayRange/Replay/ArmReplay/ReplayOneSecond/ReplayOneSubTick)
                               # — thin delegators over Replay/ReplayDriver.cs, which runs the spine under a ReplayHost.
                               # ArmReplay arms the driver against a scenario loaded by other means (the tick oracle).
                               # RunProfile (defaults to Test; hosts set it) + EnterReplay(), the scope the driver runs every step under.
                               # TickTimings: opt-in per-step timing sink (null in production; the soak runner and the reconstruction benchmark attach one)
RunProfile.cs                  # RunKind (Live/Replay/Test/Soak) + RunProfile: the one enumeration of what a replay may do differently —
                               # RecordsActions / RunsGenerators / RunsControllerAi, all false only for Replay (ADR 0005). Host state, never snapshotted.
SimulationEngine.Commands.cs   # SendCommand/DispatchAiCommand (one-line callers of Actions.Issue), DeferForReaction, BuildDispatchContext, TaxiAll (every aircraft at parking through the TAXI arm), ApplyPostDispatch (the
                               # aviation arm's post-dispatch body on every run kind) + WarpAircraft/AmendFlightPlan/RequestNewBeaconCode
SimulationEngine.DeferredCommands.cs  # ProcessDeferredDispatches + triggered track blocks; holds a scenario-scripted deferral that would end an active pushback (EndsPushback), and any scripted one the pushback would reject behind it, until the phase ends, then releases ready deferrals in expiry order
SimulationEngine.Generators.cs # Arrival/VFR/overflight generators: spawning, spacing, weight and engine selection; stand down in every replay/playback; AutoTrackGeneratedSpawn (a spawn whose generator carries an autotrack configuration is owned, then recorded, then its [AutoTrack] lines emitted); TickPrePhysicsResult is one SpawnedAircraft list; ApplyArrivalSpacing's CorridorAircraft only counts aircraft actually inbound to land on the runway (IsArrivalToRunway, via ApproachCommandHandler.IsOnFinal/IsInboundToLand) so a departure sharing the runway isn't mistaken for an arrival; once a ceiling is no longer needed it also restores the stream lead's scheduled speed, or a follower's current ceiling (RestoreManagedSpeed, gated by ArrivalSpacingManager.SpeedRestoreGateNm/SpeedRestoreDeadbandKts); CorridorAircraft measures along-final distance from the landing threshold (LandingThreshold.Resolve, 7110.65 §5-5-4.h); both passes take every on-final verdict and threshold distance from the runway's own field's layout (ResolveAirportLayout(runway.AirportId), secondary airports included — never the aircraft's Ground.Layout), and the protection pass's release path carries runway + layout + distance as one ArrivalThresholdDatum; the same-runway protection pass announces its own release (resume normal / published speed) through SameRunwayArrivalProtection's line builders, held off while a handoff to the student is in progress (HandoffToStudentInProgress) so the receiving controller inherits the standing ceiling
SimulationEngine.Presets.cs    # Release queue, timers, timed presets, triggers, global commands
SimulationEngine.LiveTraffic.cs  # Shadow samples, beacon tracking, runway-use latching
SimulationEngine.Recording.cs  # RecordAction (the one append site, gated by RunProfile.RecordsActions) + ApplyRecordedAircraftSpawn (the pre-tick half of the router's ApplyRecorded)
SimulationEngine.RecordedAppliers.cs  # The bodies the router's spawn arms and derived-record appliers call: SpawnNow/SpawnDelay, AddAircraft (derived from the shared RNG + beacon pool on every run kind;
                                      # the recorded snapshot wins on disagreement with a replay-fidelity warning), ApplyRecordedWeatherChange,
                                      # ApplySettingChange (mirrors the server's SimControlService recorders), ApplyGeneratorsJson, ApplyWeatherJson
SimulationEngine.Consolidation.cs  # Consolidate (CON / CON+ over ConsolidationState: records the override; a full consolidation moves the sender's block —
                                   # GetConsolidatedDescendants reading Attendance — transferring owned tracks and redirecting handoffs)
                                   # + Deconsolidate (DECON). One body on every run kind; the server's HandleConsolidationCmd and reconstruction wrap it
SimulationEngine.TrackAutomation.cs # Track automation on every run kind. ApplyAutoTrackConditions(loaded): a scenario aircraft's autoTrackConditions or a generator's
                                   # AutoTrackConfiguration → owner (the roster position, else the autoTrackAirportIds position for an airborne departure), scratchpad
                                   # rules, ERAM interim/cleared altitudes, a delayed handoff to the student on DelayedHandoffQueue (called by the server's PopulateRoom
                                   # and by AutoTrackGeneratedSpawn; the engine's own LoadScenario holds no ARTCC config and does not). ApplyAutoTrackChange + helpers
                                   # apply a recorded RecordedAutoTrackChange on every run kind (the server routes CRC .AUTOTRACK deltas as recorded inputs). Spine steps:
                                   # TickDelayedHandoffs (pre-physics; a target folded under an attended TCP waits), TickAutoAccept (post-physics;
                                   # after AutoAcceptDelay — floored at SimScenarioState.SoloAutoAcceptFloorSeconds in solo mode — unless the target TCP is CRC-controlled
                                   # per Attendance, the student's own in solo mode, or a live-feed owner), TickPointoutTimeout (post-physics; a pending point-out to an
                                   # unattended recipient is withdrawn after SimScenarioState.PointoutNoActionSeconds and the initiator told to coordinate
                                   # verbally — 7110.65 §5-4-7.a.1.(a); the student's own waits in solo mode; the AutoAcceptDelay > 0 gate still enables it
                                   # outside solo), TickFlightPlanCreatorAutoTrack (an untracked aircraft
                                   # squawking its assigned code → the plan's CreatedByOwner) before TickDeferredAutoTrack (an untracked departure above the display floor
                                   # → the autoTrackAirportIds position). The [AutoTrack]/[AutoAccept]/[Pointout] lines go through EmitTerminal
SimulationEngine.Tdls.cs       # The vTDLS spine steps (TickAutoTdlsQueue, TickTdlsAutoWilco, TickTdlsExpiry, TickTdlsTrackRemoval), the AfterAircraftSpawned spawn
                               # hook (QueueSpawnTdlsPdc auto-queues a departure's PDC inline at spawn, then PrintSpawnStrip — SimulationEngine.Strips.cs — prints
                               # its departure strip, the order the live room ran them in; TickAutoTdlsQueue stays the catch-up path for a plan edited after spawn),
                               # SetTdlsOpConfig/ApplyTdlsOpConfig (TDLSOPS), IsDepartureAircraft, InitializeFromArtcc (loads the coordination channels — SimulationEngine.Coordination.cs — pre-creates strip bay slots +
                               # registers a TdlsConfig for every ARTCC facility that has one; run by scenario load and ReplayDriver), and
                               # DrainStateChangesInto (hands the host what FlightStripState.Changes and TdlsState.Changes accumulated since the last drain,
                               # strips first, then the coordination, bookmark and session-clock dirty flags as OnCoordinationChanged / OnBookmarksChanged / OnSimStateChanged). Decides from engine state alone (the session clock, the ARTCC's TDLS configuration, the world), so every run
                               # kind builds the same DCL/PDC lists.
SimulationEngine.Bookmarks.cs  # The bookmark bodies: AddBookmark(timeSeconds, name, initials) / RenameBookmark / DeleteBookmark /
                               # DeleteAllBookmarks over SimScenarioState.Bookmarks + NextBookmarkId (MaxBookmarks 500, the id in the result Message), each marking the
                               # bookmark dirty flag that DrainStateChangesInto hands the host as OnBookmarksChanged. Bookmarks stay out of the snapshot on purpose (a
                               # rewind carries them over) and BM is RecordingPolicy.Never. The server's RoomEngine.AddBookmark/RenameBookmark/DeleteBookmark wrappers
                               # (the desktop client's bookmark RPCs, explicit timeSeconds) call these then drain, so a paused room still broadcasts
SimulationEngine.Transport.cs  # The session-clock bodies: Pause() / Resume() / SetSimRate(int) over SimScenarioState.IsPaused /
                               # SimRate (clamped 1..16; refused while LiveTrafficEnabled; (false, "No active scenario") without one), each marking the sim-state dirty flag
                               # (OnSimStateChanged → the host's BroadcastSimState). The unattended-pause and rewind paths still write IsPaused directly and broadcast
                               # themselves. PAUSE/UNPAUSE/SIMRATE stay RecordingPolicy.Never — a rewind must never pause itself
SimulationEngine.EramConformance.cs  # TickEramVerticalConformance (spine step EramVerticalConformance, after AltitudeFixPassage): latches
                               # AircraftEramState.ReachedAssignedAltitude inside the assigned band (±200 ft; block floor−200..ceiling+200; ABV ≥ −200),
                               # clears it when EramConformanceKey (EramAltitudeFeet, block floor, ABV) changes; skipped while QT-coasted, frozen or no Mode C
SimulationEngine.Asdex.cs      # The recorded CRC ASDE-X / SAID display mutations: ApplyAsdexMutation /
                               # ApplySaidMutation (tag / terminate / suspend / inhibit / edit onto AircraftStarsState through TrackEngine.SetAsdexField +
                               # the SetSaidField / HandleSaidVerb twins; a Terminate tells the host once — OnAsdexTrackTerminated / OnSaidTrackTerminated —
                               # so the room's one-shot delete marker fires) and EnableAllAsdexAlerts (ASDXALERTS, a per-aircraft sweep, a Sim arm). A CRC
                               # EditDbFields echo stores "" as written; only the typed ASDXSP1-family maps an empty argument to null (clear).
                               # ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange) writes Scenario.AsdexSafetyLogicConfig — the body for a CRC
                               # safety-logic push on every run kind; dropped with a Debug line when no scenario is loaded.
                               # TickAsdexAlerts(IHostConsumers) is the post-physics alert step (a Sim spine step on every run kind): the configured runway
                               # footprints (elevation from the nearest airport's nav-DB runway within 5 nm) + the ground layout's true taxiways go to
                               # AsdexSafetyLogicDetector.Detect, and ApplyAsdexDetection folds the findings into Scenario.ActiveAsdexAlerts and hands the
                               # host only the diff (IStateChangeConsumer.OnAsdexAlertsChanged: new alerts, cleared ids; never both empty). No config
                               # standing clears the set. A tick that moves nothing leaves the set's reference alone
SimulationEngine.DisconnectCoast.cs # The disconnect-coast lifecycle as Sim state (Scenario.DisconnectCoasts, callsign → AircraftDisconnectCoast): RegisterDisconnectCoast
                               # (called by DeleteAircraft, RemoveLiveTraffic, its replay twin ApplyRecordedLiveTrafficRemoval and TickAutoDelete before
                               # World.RemoveAircraft) builds the facets — ERAM when
                               # DisconnectCoastRules.IsVisibleOnEram and not frozen (24 s), one ASDE-X / SAID facet per AircraftStarsState membership (45 s, IsDrop at
                               # the destination) — and replaces any standing entry; TickDisconnectCoastExpiry (post-physics Sim step after the host's
                               # auto-delete) expires facets on sim time and hands the host OnDisconnectCoastExpired, which ends those coast entries (also while broadcasts are suppressed) and queues
                               # their CRC deletes on TrainingRoom.PendingCoastDeletes; AfterAircraftSpawned (public; SpawnShadow for a live-feed shadow) clears a re-spawned
                               # callsign's entry, drained as OnDisconnectCoastsCleared, which ends the server's coast caches and queues a delete for each entry it ends;
                               # the queue is sent, awaited and in order, at the start of the room's next unsuppressed CRC broadcast pass, before any live track
SimulationEngine.Eram.cs       # The ERAM CRR-group definitions: CrrGroups (label → EramCrrGroup, case-insensitive) and
                               # ApplyCrrGroup(RecordedEramCrrGroup) — create/replace/recolor, null latitude = delete — marking the dirty flag DrainStateChangesInto
                               # hands the host as OnEramCrrGroupsChanged (the room re-pushes the whole EramCrrGroups topic; a delete is still the CRC handler's
                               # own additive-topic removal). Applied from ActionRouter.ApplyStateRecordCore on every run kind, so a Sim replay has the groups;
                               # snapshotted as ServerSnapshotDto.CrrGroups; cleared by ReplayDriver's t=0 block. Also the room's conflict-alert settings: EramRoomSettings
                               # (the CA entry; detection never reads it, only what a host shows each sector) with ApplyEramRoomEntry(RecordedEramRoomEntry) — a malformed
                               # entry is logged and refused — and its own dirty flag drained as OnEramConflictSettingsChanged (payload-less); snapshotted as
                               # ServerSnapshotDto.EramConflictSettings; a snapshot restore also drops ERAM conflicts absent from the snapshot.
                               # ApplyEramRoomEntry dispatches on the entry's verb (CA to EramRoomSettings, SM / SMDE to EramSectorMessages, which notifies no host); EramSectorMessages is snapshotted as
                               # ServerSnapshotDto.EramSectorMessages (CaptureEramSectorMessages) and cleared by ReplayDriver's t=0 block
SimulationEngine.Coordination.cs # The coordination bodies: TickCoordinationTimers (post-physics: an acknowledged release voids
                               # CoordinationAckExpirySeconds after the ack, flags DepartureExpirationWarning at CoordinationExpiryWarningSeconds remaining, a
                               # recalled item reverts to Unsent after CoordinationRecallLingerSeconds — SimScenarioState constants), RemoveCoordinationOnRadarAcquisition
                               # (the Track arm's tail: a TRACK voids the aircraft's items), InitializeCoordinationChannelsFromArtcc (Scenario.CoordinationChannels
                               # from the ARTCC's STARS lists), and the CoordinationChanged dirty flag (MarkCoordinationChanged / DrainCoordinationChanged) every
                               # mutation sets — payload-less because the StarsCoordination topic is always pushed whole. Also the tower-list half of that topic:
                               # TickTowerLists (post-physics: TowerListTracker.Update over the world snapshot, marks the flag on change) and
                               # InitializeTowerListsFromArtcc (the P-list airports from the ARTCC, run by InitializeFromArtcc after the coordination channels)
SimulationEngine.Strips.cs     # The flight-strip spine steps (TickAutoArrivalStrips, TickAutoApproachDepartureStrips, TickStripDispatches — the queued
                               # preset/deferred/triggered strip verbs the command queue could not apply when they were issued), the spawn hook's strip half
                               # (PrintSpawnStrip/TryPlaceConfiguredStrip — position-type default routing vs. a scenario-configured bay/rack), and
                               # ReprintDepartureStripAfterAmendment (a flight-plan amendment prints a NEW departure strip carrying the bumped revision; the
                               # id it printed under, baked from the record, keeps a replay from minting a second copy). Decides from engine state alone (the
                               # session clock, the scenario's student position, the ARTCC's bay configuration, the world), so every run kind builds the same
                               # racks and printer queues.
AddAircraftOutcome.cs          # What an ADD produced: the aircraft now in the world + its spawn snapshot (baked onto the RecordedCommand), or the refusal
SimScenarioState.cs            # Per-scenario runtime state: queues, settings, ATC positions, coordination, ArtccConfig (loaded from bundle on replay), LiveTrafficFilter (carried from room settings),
                               # SessionStartUtc (the pinned instant t=0 is anchored to: the room clock for a live load or restart, the recorded instant for a replay, ProcessDayUtc — the unclamped process day — where no clock exists; snapshotted + in the recording manifest) + SimTimeUtc (start + elapsed) + MagneticModelDateUtc (derived: the start's UTC day),
                               # AiStaffedPositions (published by the AI host; never snapshotted) + PilotContacts (memoized PilotContactRoster) + IsAiStaffed,
                               # AsdexSafetyLogicConfig (the last CRC safety-logic push, null until a display pushes one; snapshotted, read by the surface-alert step),
                               # ActiveAsdexAlerts (the standing ASDE-X alerts by id: an ordinal ImmutableSortedDictionary swapped by reference — written only on the tick thread, read off the tick gate by the CRC initial-data build; snapshotted)
ScenarioPacing.cs              # Shared solo-training pacing helpers for parking call-up intervals and arrival generator rates
ArrivalSpacingManager.cs       # Pure in-trail spacing math for the generator stream, plus the speed-restore policy (SpeedRestoreGateNm, SpeedRestoreDeadbandKts) RestoreManagedSpeed and RNS-on-final share — simulated approach-controller speed equalization: SpacingCeilingKts (proportional ceiling) and InTrailCeilingKts (raises it with a time-based closure allowance while the pair — InTrailPair: leader/follower IAS, GS, Vref, along-final distance, target gap — is farther apart than the target, bounded by the leader's Vref so the gap still holds when it crosses the threshold, 7110.65 §5-5-4.h); SimulationEngine.ApplyArrivalSpacing drives it
SameRunwayArrivalProtection.cs # Same-runway arrival protection: required threshold interval (category constant / live leader rollout, floored by 3 NM radar + wake), §5-7-3.c distance-keyed speed floor, the 20 NM pre-clearance range, the 10 NM tower speed authority + Vapp (§5-7-3.f), and the shared vacate arithmetic OccupiedRunwayGoAround projects against; SimulationEngine.ApplySameRunwayArrivalProtection drives it
ScratchpadRuleEngine.cs        # Applies the facility's vNAS scratchpad rules (airport/route/altitude match → Template) to SP1/SP2
                               # at track-acquisition events. Only fills an empty field that was not explicitly cleared.
                               # Also owns MaxScratchpadLength (3, or 4 with Allow4CharacterScratchpad).
AutoScratchpadResolver.cs      # Pure: the STARS destination fallback shown in the SP1 slot when no real scratchpad is set,
                               # gated by the area's ShowDestination* adaptation; classifies departure/arrival/primary-arrival
                               # (primary = area.TowerListConfigurations[0]). Mirrors CRC GetScratchpads + UpdateFlightType.
                               # Display-only — DtoConverter fills AircraftStateDto.AutoScratchpad1; never persisted to AircraftState.
                               # Not truncated to the scratchpad limit — STARS clips only controller-entered text.
SessionRecording.cs            # v1 (commands) + v2 (commands + snapshots) recording format; ArtccConfigJson optional bundle; StudentPositionState (from snapshot 0) for Sim-side replay restore; TerminalLog (broadcast terminal stream) for terminal-scrub repopulation
RecordedAction.cs              # Polymorphic recorded actions: Command, Chat, AmendFlightPlan, RequestNewBeaconCode, WeatherChange, SettingChange, AircraftSpawn,
                               # LiveTrafficSample (pre-tick, like AircraftSpawn — SimulationEngine.IsPreTickAction), LiveTrafficRemoval (a Deleted reason re-adds the callsign to SimScenarioState.SuppressedLiveTraffic on replay),
                               # LiveTrafficStatus (feed health + wall clock per status broadcast; diagnostic only, replay ignores it),
                               # AttendanceChange (the full set of attended vNAS position ids — a recorded input the live host derives from its connections when it changes, not a controller action),
                               # the derived records a CRC handler writes for state it used to change without a trace (tick-path 3d-5b):
                               # StarsSharedStateChange (a position's per-TCP shared display state), ClearanceChange, HoldAnnotationChange (null = delete),
                               # EramEntry (an ERAM keyboard entry in Commands/EramEntryEngine's grammar + the acting position's AS code), EramCrrGroup (null Lat = delete),
                               # EramRoomEntry (RecordedEramRoomEntry: FacilityId + one absolute CA settings or SM / SMDE sector-message entry with explicit sector ids — the recorder expands ALL),
                               # StripRequest (a manual Request Strip: null FacilityId = the hub's own-bay form, else CRC's departure strip for that facility; StripId is the
                               # id the live print minted, baked on so a same-room rewind prints nothing and a from-scratch reconstruction prints under the same id),
                               # AsdexSafetyLogicChange (FacilityId + Config, the Sim-native Asdex/AsdexSafetyLogicConfig the CRC wire DTO projects to — scenario state,
                               # applied by SimulationEngine.ApplyRecordedAsdexSafetyLogic on every run kind),
                               # AutoTrackChange (a CRC .AUTOTRACK delta — positive ids, -X, none — the second recorded input; SimulationEngine.ApplyAutoTrackChange applies it on every run kind)
                               # AmendFlightPlan's StripId is the id the amendment's departure-strip reprint printed under, baked on so a replay reprints
                               # under it instead of minting a second copy beside what a snapshot restore carried in; null on a pre-feature record and when
                               # the reprint printed nothing, both of which mint as before
                               # RecordedCommand bakes the live run's draws (ReactionDelaySeconds, SpawnJitterSeconds, SpawnedAircraft, IssuedAtUtc, StripId — the id a
                               # creating strip verb (SEP/HSC/SCAN) minted, so replay creates the item under it instead of drawing again — all nullable)
                               # for replay determinism and carries Accepted (null = pre-feature = accepted) so a replay can compare verdicts
RecordedTerminalEntry.cs       # One broadcast terminal line (kind/callsign/message) with wall-clock Timestamp + scenario-elapsed ElapsedSeconds; persisted as terminal-log.json.br so a loaded recording repopulates the terminal and each line scrubs the replay
RecordedCommandClassifier.cs   # The exhaustive command classifier: RecordedCommandKind (one per ParsedCommand subtype, no default —
                               # UnroutedCommandException) + ScopeOf(kind) → ActionScope + IsAviationCommand (the dispatcher-owned list).
                               # Classify(text) is the key into Simulation/Actions/ArmTable for every fresh and recorded command in Yaat.Sim;
                               # Say (the SAY* queries) and ShowQueued (SHOWAT/SHOWCOND — never recorded) are separate kinds; the live room, a Sim replay and a
                               # server reconstruction all look the same kind up
TimerCommandApplier.cs         # The one TIMER body (set / cancel ActiveTimers on SimScenarioState, with the controller-facing messages);
                               # the router's Timer arm runs it on every run kind; the live room's OnTimersChanged consumer adds the broadcast
RecordingCompression.cs        # Brotli compress/decompress; auto-detects Brotli, gzip, or plain JSON on read
RecordingArchive.cs            # v4 ZIP archive reader: on-demand snapshot loading (JSON deserialized straight from the Brotli stream; spawn synthesis decodes only first-seen aircraft), layout/source-GeoJSON reading, seek API
                               # ToBaseSessionRecording (no snapshots), FindNearestSnapshotIndex, ReadSnapshotAt, ReadArtccConfigJson
                               # ReadBookmarks / static WriteBookmarks (client-injected bookmarks.json; optional, manifest-untracked)
TimelineBookmark.cs            # TimelineBookmark + RecordingBookmarks records (the bookmarks.json payload)
RecordingArchiveWriter.cs      # v4 ZIP archive writer: streaming snapshots + deduplicated layouts/source GeoJSON + bundled ArtccConfig
RecordingManifest.cs           # Archive manifest: snapshot index, LayoutAirportIds, AirportGeoJsonIds, HasArtccConfig, metadata, SessionStartUtc (+ ResolveSessionStartUtc: recorded instant → RecordedAtUtc day → process day)
RecordingSchemaUpgrader.cs     # Surgical in-place snapshot schema upgrade (via SnapshotSchemaMigrator, never re-sim); handles .br/v4-zip/bug-bundle; drives yaat-server's Yaat.RecordingUpgrader CLI.
                               # Also rewrites recorded canonicals in place: retired HSE → HSA id form (HalfStripEditCanonicalRewriter) and the required FACILITY/BAY bay token (StripBayCanonicalQualifier, resolved against the recording's own ArtccConfig + student position).
StripBayCanonicalQualifier.cs  # Idempotent bay-token qualifier for recorded canonicals: adds the owning facility to STRIP/SCAN/HSC/HSM/SEP/SEPM/BLANK/... dest-specs; leaves id-form and bayless verbs alone.
HalfStripEditCanonicalRewriter.cs # Idempotent rewrite of the retired `HSE <id> …` half-strip verb into `HSA <id> …` for archived action logs.
CompoundCanonical.cs           # RewriteUnits: applies a per-unit rewrite across `;`/`,` compound canonicals, preserving separators and padding; returns the input instance when nothing changed.
RecordingJsonOptions.cs        # Shared JsonSerializerOptions for recording serialization

# Simulation/Spine/ — the one ordered definition of a sim-second (docs/tick-loop.md § the spine, ADR 0001)
SpineOrder.cs                  # The three step lists (PrePhysics / PostPhysics / EndOfSecond) in the live server's order — the adjudication record of ADR 0002
StepId.cs                      # One member per step, in spine order; the trace's and the timing buckets' key. PostPhysicsTerminalEntries drains the lines the
                               # track-automation steps emit so they reach the room the same second
SpineStep.cs                   # One list entry: a sim step (engine body, gets only IHostConsumers) or a host step (gets only IHostSteps)
IHostSteps.cs                  # The host's step view — every server-owned body as a named member, no defaults (a new member breaks every host); header lists the
                               # step-4 debt — none left: LiveTrafficSync became SimulationEngine.TickLiveTrafficSync over IHostConsumers.LiveTrafficFeed (04e). CoordinationTimers and TowerLists moved out (SimulationEngine.TickCoordinationTimers / TickTowerLists). The two strip auto-print passes (AutoArrivalStrips/
                               # AutoApproachDepartureStrips) and the four TDLS tick steps (AutoTdlsQueue/TdlsAutoWilco/TdlsExpiry/TdlsTrackRemoval) moved
                               # out — they're Sim steps now, engine bodies in SimulationEngine.Strips.cs / SimulationEngine.Tdls.cs. AsdexAlerts moved out
                               # (SimulationEngine.TickAsdexAlerts)
IHostConsumers.cs              # The host's consumer view — OnPrePhysics / OnTerminalEntries / OnConflictAlerts / the drains / OnStripsChanged / OnTdlsChanged / OnCoordinationChanged
                               # (IStateChangeConsumer, shared with IActionHost) for what the strip, TDLS and coordination mutations touched
IStateChangeConsumer.cs        # Where a drained StripChangeSet / TdlsChangeSet and the coordination dirty flag (OnCoordinationChanged, payload-less) go. Declared on its own because both halves of a host reach it: the action
                               # router holds the action view (IActionHost) and the post-physics drain step the consumer view (IHostConsumers), and one
                               # implementation on a host answers both. OnStripsChanged / OnTdlsChanged: the host broadcasts unless suppressed; a
                               # reconstruction drops them and the room re-syncs afterwards. OnAsdexAlertsChanged(newAlerts, clearedAlertIds): the ASDE-X
                               # alert step's diff (CRC's alert topic is additive with an explicit delete); the room broadcasts it unless suppressed
ISimulationHost.cs             # IHostSteps + IHostConsumers + Actions.IActionHost; four implementations (BareHost, ReplayHost, yaat-server LiveRoomHost / ReconstructionHost)
StepTrace.cs                   # Per-second (StepId, subTick) sequence + FNV-1a digest + counts; on by default, allocation-free once warm
BareHost.cs                    # The Test-run host: every spine slot empty, consumers fire the engine's events; as an IActionHost
                               # IsPositionAttended is false and every consumer is discarded

# Simulation/Actions/ — the action router: a controller action is routed once, in Yaat.Sim (ADR 0007; docs/command-pipeline.md § one routing table)
ActionRouter.cs                # SimulationEngine.Actions. Issue(ActionInput, host) for a fresh command, Apply(RecordedCommand, host) for a recorded one,
                               # ApplyRecorded(RecordedAction, host) for any recorded action (spawn / live-traffic / amendment — reprints the departure strip
                               # under its baked StripId, then drains what that touched into the host — / weather / setting / generators /
                               # the derived state records — shared state, clearance, hold annotation via their Sim appliers, an ERAM entry via EramEntryEngine
                               # with the identity code resolved, a CRR group / safety-logic push via their engine bodies, a strip request via
                               # StripRequests.PrintRequestedStrip — a refusal is a replay-fidelity warning; ApplyStateRecord drains the strip/TDLS mutations
                               # into the host after its own body, same as Finish does for a routed command), IssueDerived(record, host)
                               # for a derived record produced now (same body, appended to the log only when it applied — the server's ApplyAndRecord).
                               # Stages: strip the AS prefix → refuse a chain with a non-compoundable verb → split a scoped-special compound into units
                               # → classify → resolve scope (Aircraft: FindAircraft or the identical "Aircraft 'X' not found") and identity → run the
                               # ArmTable row → record (fresh: through RecordAction, accepted or not) or compare verdicts (recorded: the replay-fidelity
                               # warning when Accepted disagrees) → drain what the strip/TDLS/coordination mutations touched into the host (DrainStateChangesInto,
                               # IStateChangeConsumer.OnStripsChanged/OnTdlsChanged/OnCoordinationChanged) before the result returns. LastTrace is the parity test's observable. Overloads without a host use the bare host.
                               # WouldRecord(command) answers the policy question — whether the arm routing that text fresh would record it — without running
                               # it: the mirror of the stages above (AS prefix, the two chain refusals, the scoped-special split, the ArmTable row's
                               # RecordingPolicy), changed with them. Policy, not outcome: a replay-profile engine appends nothing whatever it answers.
                               # It is what the server gates its implicit take-control on, so pressing play on a rewound timeline replays the tape instead of
                               # truncating it (RoomEngine.TakeControlIfCommandDiverges); the client asks it before prompting.
ArmTable.cs (in ActionArm.cs)  # ActionArm (kind, scope, RecordingPolicy, Run) + ArmTable.For(kind) — one row per
                               # RecordedCommandKind, scope asserted equal to the classifier's at construction; RecordingPolicy.Never = ShowQueued + Bookmark + Transport;
                               # ArmContext is what a body sees (engine, host, input, remainder, parsed, resolved aircraft/identity) and writes its draws
                               # (ReactionDelaySeconds, SpawnJitterSeconds, SpawnedAircraft, IssuedAtUtc, StripId — the id a creating strip verb minted) into,
                               # so the router bakes them onto the record
ActionArms.cs                  # The Sim bodies: Aviation (ParseCompound → ReactionDelayPolicy → defer or DispatchCompound → ApplyPostDispatch; a landing clearance
                               # caches the ground layout, a successful APT reprints the departure strip (ReprintDepartureStripAfterAmendment) and bakes the
                               # id onto the record), ShowQueued (ConditionalList lines to
                               # OnQueuedCommandsShown, never recorded), FlightPlan (FP/VP/DA/RMK: FlightPlanNormalization → SimulationEngine.AmendFlightPlan →
                               # ReprintDepartureStripAfterAmendment (its id baked onto RecordedAmendFlightPlan.StripId, so a replay reprints under it instead
                               # of minting a second copy) + the filing identity as FlightPlan.CreatedByOwner; DA is create-only (DUP NEW ID) and a VFR filing (VP)
                               # over an existing IFR/OTP plan is DUP NEW ID, never converted — FP, the IFR/OTP spelling, amends anything; from a record only
                               # the creator tag is applied — the amendment recorded beside it carries the plan), Delete (a shadow → SimulationEngine.HideLiveTraffic),
                               # Unassume (UNASSUME: an AssumedFromLiveTraffic aircraft leaves as DEL does, minus the suppression and the removal record, so the
                               # next ShadowTrafficSync re-spawns the shadow), DeleteQueued, Note, SpawnNow/SpawnDelay, SetActivePosition (OnPositionSelected with the typed code), Track
                               # (TrackEngine.Dispatch; CAACK to TrackEngine.AcknowledgeConflictAlert; the tails on every run kind — TRACK applies the facility's
                               # scratchpad rules + RemoveCoordinationOnRadarAcquisition, INHCA drops the aircraft's active conflicts, ASDE-X TERM and a recorded CRC terminate → OnAsdexTrackTerminated / OnSaidTrackTerminated, a ghost's
                               # DROP lifts the overlay (OnGhostOverlayRemoved) or removes the phantom (OnAircraftDeleted)), GlobalTrack (ACCEPTALL/HOALL via
                               # TrackEngine.DispatchGlobal), GhostTrack (a created phantom is handed to OnAircraftSpawned), Reposition, SquawkAll, HFR/HFROFF/REL (baked jitter else ReleaseJitterRng), Cfr (baked clock else now),
                               # Timer, TaxiAll, AddAircraft (SimulationEngine.AddAircraft; bakes the spawned aircraft onto a fresh record),
                               # Consolidate/Deconsolidate (SimulationEngine.Consolidate / Deconsolidate; OnConsolidationChanged),
                               # Coordination/GlobalCoordination (CoordinationCommandHandler.Handle / HandleGlobal over the engine — Sim arms)
IActionHost.cs                 # The action-path view of a host, part of ISimulationHost and IStateChangeConsumer: no Apply* slot is left — every
                               # recorded state change has an engine body — only the consumers a Sim arm or applier notifies (OnAircraftSpawned, OnAircraftDeleted(callsign, lastState),
                               # OnPositionSelected(conn, owner, tcpCode), OnGhostOverlayRemoved, OnAsdexTrackTerminated, OnStripsChanged(StripChangeSet) /
                               # OnTdlsChanged(TdlsChangeSet) / OnCoordinationChanged() (IStateChangeConsumer, shared with IHostConsumers — the router drains all three after every routed
                               # action; strips, TDLS and coordination themselves crossed whole into Yaat.Sim, so this is only
                               # the broadcast the host still owes), OnTimersChanged, OnConsolidationChanged, OnHeldDeparturesChanged, OnWeatherChanged,
                               # OnQueuedCommandsShown). No defaults: a new consumer fails the build in every host until each has answered. BareHost + ReplayHost
                               # discard them; yaat-server's RoomHost answers with the room's broadcasts and gives a fresh action the room's tails (spawn hooks and
                               # broadcasts, display config, CRC broadcasts) that a replaying room skips
ActionInput.cs                 # ActionInput (callsign, command, connection id, initials, Baked) + BakedDraws (reaction delay, spawn jitter, spawned aircraft,
                               # issued-at clock, strip id) — the values a live run drew, read back from the record so no other run draws them
ActionOutcome.cs               # ActionOutcome (result, the record produced, trace) + ActionTrace (kind, scope)
ActionRefusals.cs              # HostOnly (no arm in the track table for the verb) / NoScenario / AircraftNotFound — the results for an action no body on this run can apply
ReactionDelayPolicy.cs         # Decide(scenario, world, aircraft, compound, baked): baked wins; else null when no range is active, the compound carries
                               # an unsupported verb (refused at once by DispatchCompound rather than delayed), explicit leading timing
                               # (WAIT/WAITD/BEHIND), is purely comm (CON/FCA/ACK), or contains any "Sim Control" instructor verb
                               # (FHN/CMN/SPDN/WARP/WARPG/TRATE/DEL — WAIT/WAITD skipped); else sample ReactionDelayRng and clamp to the latest
                               # pending reaction deferral so issue order is preserved
ActionScope.cs                 # Global / Callsign / Aircraft / Position — what the action router resolves before an arm runs (a property of the RecordedCommandKind)
PositionSelections.cs          # connection id → the TrackOwner a bare AS selected; one lock-guarded map per engine (the server room owns one instance for
                               # its lifetime and hands it to every engine it creates); Snapshot()/Restore() back ServerSnapshotDto.PositionSelections

# Simulation/Replay/
RecordedActionPump.cs          # The one pump behind a Sim replay, the server's reconstruction and its tape playback: walks an action log with a cursor and
                               # a pre-tick set — ApplyPreTick(second) lands spawns / live-traffic samples before physics without moving the cursor,
                               # ApplyThrough(second) applies the rest and advances past all of it, SeekTo repositions after a jump, Reseat adopts a cursor
                               # kept elsewhere (the room's PlaybackCursor) and forgets the pre-tick bookkeeping when it moved
ReplayDriver.cs                # Drives a recording forward (range / one second / one sub-tick) by running the spine under a ReplayHost;
                               # owns the driver's RecordedActionPump. Rebuilds strip bays + TDLS facility configs from the ARTCC
                               # (SimulationEngine.InitializeFromArtcc — the coordination channels too) once the config and student position are restored, so the mutations
                               # find what they assume exists. Internal — SimulationEngine.Replay.cs is the public surface over it.
ReplayHost.cs                  # The bare host plus pre-tick recorded actions and post-second action application through the pump and
                               # Actions.ApplyRecorded (unless the caller supplies its own applier); as an IActionHost it delegates every member to the bare host
ScenarioQueues.cs              # DelayedSpawn (+ HeldForRelease), ScheduledTrigger, ScheduledPreset, ScheduledRelease, ActiveTimer (TIMER countdowns),
                               # IGeneratorRuntimeState + GeneratorState / VfrArrivalGeneratorState / OverflightGeneratorState, DelayedHandoff
HeldReleaseService.cs          # Hold-for-release: Arm/Disarm/Release an airport's IFR departures + BuildRundown. See docs/hold-for-release.md
CfrDepartureService.cs         # CFR: sets/clears/reports a departure's alert-only release-time window (AircraftGroundOps.ReleaseWindow*Utc) + echo
ConsolidationState.cs          # Thread-safe manual consolidation overrides

# Simulation/Strips/ — flight-strip state + command/tick logic, engine-owned (SimulationEngine.Strips, SimulationEngine.Strips.cs)
FlightStripState.cs            # The strips, the bay/rack layout and the two printer queues for one run. A fresh engine starts empty and the
                               # snapshot's server section carries it across a rewind, a restore or a reconstruction, so every run kind has the
                               # same strips at the same second. Mutations funnel through StripMutations under the single Gate lock
                               # (moves touch Items + source rack + dest rack together); Items/Bays are concurrent collections for read-only
                               # enumeration only. StripItemRecord: one strip (id, aircraft id, type, offset, field values, facility/bay/rack/index).
                               # Changes (StripChangeTracker) records what StripMutations touched since the host's last drain — transient, not
                               # snapshotted, cleared by ClearSession alongside the session state.
StripItemType.cs               # What a strip item is: a printed departure/arrival strip, one of the four separator styles, half of a split strip, or a
                               # blank — the vStrips wire numbering; StripItemRecord.Type stores it as an int so the schema doesn't move when this enum
                               # gains a member
StripChangeTracker.cs          # The broadcast seam for FlightStripState: StripChangeSet (ChangedItemIds, FullState — what the host broadcasts from) and
                               # StripChangeTracker itself (MarkChanged/MarkFullState; Drain takes everything accumulated and resets).
                               # IStateChangeConsumer.OnStripsChanged (Spine/IStateChangeConsumer.cs) is the interface both IActionHost and IHostConsumers
                               # share, so the router (after every routed action) and the post-physics StateChanges spine step (for what the tick steps
                               # produced) each reach it once. Transient — never snapshotted, cleared by FlightStripState.ClearSession.
StripMutations.cs              # Stateless strip mutation helpers (create/move/annotate/delete/print-queue logic) over FlightStripState, holding Gate for
                               # every multi-slice update and recording what changed into FlightStripState.Changes; shared by StripCommandHandler, the
                               # engine's auto-print hooks (SimulationEngine.Strips.cs) and StripRequests. MintStripId draws a manual request's id
                               # (ARRIVAL_{callsign} fixed; STRIP_{callsign} when free, else a hex-suffixed duplicate); NewScanStripId draws a SCAN copy's
                               # id; IsSeparatorType is the shared predicate for the four separator styles
StripCommandHandler.cs         # Single dispatch point for every canonical strip verb (STRIP, SCAN, STRIPD, STRIPO, AN, HSC, HSA, HSD, HSM, HSO, HSS, SEP,
                               # SEPD, BLANK, BLANKD) against the engine's Strips, on every run kind. StripApplyResult(Result, StripId): the verdict, and
                               # for a creating verb (SEP/HSC/SCAN/BLANK) the id it minted or reused — the ArmTable strip arm bakes a non-null one onto the
                               # record so replay creates the item under the same id
StripRequests.cs               # The manual "Request Strip" body: StripRequestPlan (aircraft/scenario/facility/format/ETA) + ResolveStripRequest (the
                               # training-hub form vs. a CRC facility's departure request resolve the same plan) + PrintRequestedStrip (the
                               # RecordedStripRequest applier — an ARRIVAL_ id reprints and requeues, a STRIP_ id prints nothing if the engine already
                               # holds it) + IsArrivalCandidate + ResolveAirportPosition (ETA reference point)

# Simulation/Tdls/ — vTDLS session state + command/tick logic, engine-owned (SimulationEngine.Tdls, SimulationEngine.Tdls.cs)
TdlsState.cs                   # The DCL (Pending) and PDC (Sent/Wilco) lists plus a Dumped lockout (keeps a controller-removed entry from being
                               # re-created) for one run. Engine-owned; a fresh engine starts empty and the snapshot carries the session. Configs
                               # (per-facility TdlsConfig) is the exception — reloaded from the ARTCC on every scenario load and never snapshotted,
                               # so ClearSession (not Reset) is what a restore uses and leaves Configs alone; Reset also clears Configs, for
                               # scenario unload. ResolveActiveOpConfigId / ResolveSids honor the facility's active operational configuration.
                               # Changes (TdlsChangeTracker) records what TdlsMutations touched since the host's last drain — transient, not
                               # snapshotted, cleared by ClearSession alongside the session state.
                               # TdlsItemRecord: one list entry (Pending → Sent → Wilco, removed on Dump/TTL-expiry/activation); TdlsItemStatus
TdlsChangeTracker.cs           # The broadcast seam for TdlsState: TdlsRemoval (one item that left Items — dumped, or removed by TTL/track-removal),
                               # TdlsChangeSet (ChangedItemIds, Removed, FullState — what the host broadcasts from) and TdlsChangeTracker itself
                               # (MarkChanged/MarkRemoved/MarkFullState; Drain takes everything accumulated and resets). IStateChangeConsumer.OnTdlsChanged
                               # (Spine/IStateChangeConsumer.cs) is the interface both IActionHost and IHostConsumers share, so the router (after every
                               # routed action) and the post-physics StateChanges spine step (for what the tick steps produced) each reach it once.
                               # Transient — never snapshotted, cleared by TdlsState.ClearSession — mutated under TdlsState.Gate like the state it describes.
TdlsMutations.cs               # Stateless mutation helpers for TdlsState (queue/send/mark-Wilco/dump/expire, ResolveFacilityForAirport, FindActiveItem) —
                               # callers hold Gate externally. Helpers that allocate new ids advance NextItemId; helpers that change status return the
                               # updated record (or null if the item didn't exist) and record it in TdlsState.Changes for the host to broadcast.
                               # ClearancePayloadFromFields nulls an FE placeholder as well as an empty field, so a "- - - -" pick never reaches
                               # SentPayload, the PDC text or the snapshot.
TdlsCommandHandler.cs          # Static dispatch for TDLSQ / TDLSS / TDLSW / TDLSDUMP against SimulationEngine.Tdls: resolves the aircraft's TDLS facility,
                               # mutates through TdlsMutations, and builds the ACARS PDC text for a TDLSS send; owns the auto/manual WILCO delay
                               # (DefaultWilcoDelay). ArmTable's Tdls/TdlsOps rows call it (and SimulationEngine.ApplyTdlsOpConfig) as Sim bodies now,
                               # not host slots. ValidateMandatoryFields treats a climb-via as satisfying a mandatory InitialAlt — one altitude
                               # instruction per clearance (7110.65 4-3-2), so requiring both would be a dead end.
TdlsClearance.cs               # The clearance a PDC carries: the nine canonical TDLSS payload fields, in the order the command describes them.
                               # The simulation's clearance model — held by TdlsItemRecord and round-tripped by the snapshot; the server projects
                               # it onto the CRC wire ClearanceDto on its way out
TdlsPlaceholder.cs             # The one predicate for the FE's "no value" entry (blank, or dashes and spaces only — "- - - -" is a real selectable
                               # list entry at SMF, not just an empty dropdown's display text). Shared by the Sim's PDC text and mandatory-field
                               # gates and by the client editor's transition matching, climb-via rule and missing-field list.

# Simulation/Eram/ — ERAM CRR-group, conflict-alert-settings and sector-message core types, engine-owned (SimulationEngine.Eram.cs owns the dictionary, the settings + dirty flags)
EramRoomSettings.cs            # The room's per-facility conflict-alert settings (CA and MCI function, sectors whose CA or MCI display is off). Copy-on-write: every write publishes a
                               # new immutable FrozenDictionary of EramFacilityConflictSettings records and bumps Version (unique across instances, so an engine swap never reads as
                               # unchanged), so the CRC broadcast reads without the room gate. TryApply takes a RecordedEramRoomEntry's four absolute shapes (CA {CA|MCI} FUNCTION
                               # {ON|OFF}, CA {CA|MCI} DISPLAY {sector}… {ON|OFF}); a facility back at the defaults is dropped; ShowsConflict(facility, sector, isMciPair) is the per-sector filter
EramWeatherReports.cs          # The room's ERAM `WX` weather reports, keyed by ICAO station (last entry wins); expire at the first :53 after entry by sim time (`SimulationEngine.ExpireEramWeatherReports` in the end-of-second weather step); snapshotted, recorded, replayed; never broadcast to clients
EramSectorMessages.cs          # The room's ERAM sector messages: one EramSectorMessage(FacilityId, SectorId, Text) per facility and sector, copy-on-write like EramRoomSettings (one volatile swap of a
                               # FrozenDictionary). TryApply takes a RecordedEramRoomEntry's two absolute shapes (SM {sector} {text} stores or overwrites, SMDE {sector} deletes) and refuses anything else;
                               # TryGet / Messages read, Replace / Clear serve snapshot restore and replay t=0
EramCrrGroup.cs                # EramCrrGroup(Label, EramCrrColor Color, Latitude, Longitude) + the EramCrrColor enum mirroring the wire CrrColor (parity pinned on
                               # both sides); the wire EramCrrGroupDto stays server-side behind DtoConverter.ToEramCrrGroupDto

# Simulation/Coast/ — the disconnect-coast types (SimulationEngine.DisconnectCoast.cs owns the lifecycle)
DisconnectCoastScope.cs        # Eram / Asdex / Said
DisconnectCoastFacet.cs        # DisconnectCoastFacet(Scope, FacilityId — null for ERAM, IsDrop, DeadlineSimSeconds)
AircraftDisconnectCoast.cs     # AircraftDisconnectCoast(Anchor, AnchorTrackDeg, AnchorGroundSpeed, CoastStartSimSeconds, Facets): the last pose a coast dead-reckons from
ExpiredDisconnectCoastFacet.cs # (Callsign, Facet) — one entry of an OnDisconnectCoastExpired payload
EramSweepGrid.cs               # Static: the ERAM 12 s sweep grid (SweepSeconds, OffsetSeconds — FNV-1a of the callsign mod 12, Index, LastSweepSimSeconds); the ERAM
                               # facet's deadline is the last grid sweep (not before spawn) + 24 s; yaat-server's AircraftChangeTracker sweeps on the same grid
DisconnectCoastRules.cs        # IsVisibleOnEram (ERAM coverage: field elevation + EramCoverageFloorAglFt 1,500 ft; frozen / unsupported ghost visible, on-ground not) and
                               # IsDestinationFacility; yaat-server's CrcVisibilityTracker.IsVisibleOnEram wraps the first

# Simulation/Coordination/ — STARS coordination-verb command logic, engine-owned (SimulationEngine.Coordination.cs owns the timer/init/dirty-flag half)
CoordinationCommandHandler.cs  # Static dispatch for the aircraft-scoped verbs (RD/RDH/RDR/RDACK/RDDEL/RDPOS/RDTXT, via Handle) and the position-scoped
                               # RDAUTO (HandleGlobal) against SimScenarioState.CoordinationChannels. InferSenderListId resolves a list-less sender verb
                               # ahead of time so the recorded canonical carries the list explicitly. Item ids are {ListId}-{SequenceNumber} off the
                               # channel's snapshotted counter, not a fresh draw, so a rebuilt list holds the same items under the same ids; every
                               # successful mutation calls SimulationEngine.MarkCoordinationChanged

# Simulation/Bookmarks/ — timeline-bookmark verb logic, engine-owned (SimulationEngine.Bookmarks.cs owns the bodies + dirty flag)
BookmarkCommandHandler.cs      # Static dispatch of BM ADD / RENAME / DELETE / DEL ALL (HandleAdd/HandleRename/HandleDelete/HandleDeleteAll) onto the engine bodies;
                               # ADD stamps the scenario's ElapsedSeconds. Sim arm

# Simulation/Transport/ — session-clock verb logic, engine-owned (SimulationEngine.Transport.cs owns the bodies + dirty flag)
TransportCommandHandler.cs     # Static dispatch of PAUSE / UNPAUSE / SIMRATE onto SimulationEngine.Pause/Resume/SetSimRate. Sim arm

# Simulation/Oracle/ — state-equivalence between run kinds (docs/tick-loop.md, ADR 0004). Driver: yaat-server TickOracleTests.
SnapshotTreeDiff.cs            # Parallel JsonNode walk over two StateSnapshotDto captures -> one SnapshotDivergence per differing leaf, at the JSON-pointer path.
                               # Aircraft keyed by callsign (only list that reorders), everything else index-keyed; embedded-JSON strings (WeatherJson,
                               # ConfigJson, ...) re-parsed so paths reach inside them; negative VirtualNode ids normalized to -V.
SnapshotStateHash.cs           # FNV-1a 64 over an ordinal-key-sorted canonical serialization (CanonicalUtf8) of ToComparableNode's tree; the
                               # oracle's per-second gate: equal hashes skip the tree diff, different hashes run it.
DivergencePath.cs              # Normalize(): collapses every [key] to [*], so the baseline is about fields, not about which aircraft spawned.
DivergenceAccumulator.cs       # Folds a per-second divergence stream by normalized path: first second + a few concrete examples, plus FirstDivergentSecond.
TickOracleBaseline.cs          # The checked-in accepted-divergence set (Load/Render/CompareTo) + TickOracleComparison (Added/Removed/regression, Describe).
OracleExemptions.cs            # Permanently-accepted paths, with reasons. Empty by design — distinct from the baseline, which is meant to shrink to nothing.

# Simulation/Snapshots/
AircraftLiveTrafficDto.cs      # Nullable AircraftSnapshotDto.LiveTraffic: last sample + dead-reckoning clock of a shadow aircraft (see live-traffic.md)
StateSnapshotDto.cs            # Top-level snapshot DTO + TimedSnapshot (elapsed + action index + state)
AircraftSnapshotDto.cs         # Aircraft state DTO (~100 fields) + nested DTOs (TrackOwner, Tcp, Pointout, SharedState, student-frequency eligibility, etc.)
ControlTargetsDto.cs           # Control targets + NavigationTarget + altitude/speed restriction DTOs
CommandQueueDto.cs             # CommandBlock/TrackedCommand/BlockTrigger/DeferredDispatch DTOs
PhaseSnapshotDto.cs            # Polymorphic PhaseDto with [JsonDerivedType] for all ~35 Phase subclasses; GroundNavigatorDto.Playback (GroundNavigatorPlaybackDto + PathPrimitiveDto: Straight/Bezier/SlowTurn) carries the navigator's mid-turn state
                               # RunwayInfoDto, ApproachClearanceDto, DepartureClearanceDto, PatternWaypointsDto, etc.
ScenarioSnapshotDto.cs         # SimScenarioState DTO: queues, generators, settings, coordination channels; ControllerAi (ControllerAiConfigDto, null when off);
                               # AtcPositions (AtcPositionDto: the resolved ATC roster — scenario atc record + owner + TCP; null in a pre-feature snapshot leaves the loader's roster);
                               # AsdexSafetyLogicConfig (AsdexSafetyLogicConfigDto + AsdexRunwayConfigDto; null-absent, no schema bump — a restore replaces, so null restores as no config);
                               # ActiveAsdexAlerts (List<AsdexSafetyAlertDto>, ordinal-sorted by id; null-absent, no schema bump — a restore replaces, so null restores as an empty set)
ServerSnapshotDto.cs           # Server-side state: consolidation overrides, conflict alerts, beacon code pool, position selections, attended CRC
                               # positions, the flight strips (Strips) + vTDLS session (Tdls), the tower-list dwell entries (TowerLists, schema 23) and the
                               # ERAM CRR group definitions (CrrGroups; membership rides each aircraft's ERAM state) and conflict-alert settings (EramConflictSettings, one EramConflictSettingsSnapshotDto per non-default facility) and sector messages (EramSectorMessages, one EramSectorMessageSnapshotDto per stored message) — all null-absent in a
                               # pre-feature snapshot, which restores empty
FlightStripSnapshotDto.cs      # Every strip, the bay/rack layout, both printer queues and the blank-id counter
FlightStripSnapshotMapper.cs   # FlightStripState ⇄ FlightStripSnapshotDto. Restore replaces, never merges — the snapshot is the whole strip state
                               # at its second, so anything the target engine held is cleared first
TdlsSnapshotDto.cs             # The vTDLS items, the dumped lockout, the active ops configs, the pending auto-WILCOs and the id counter. The
                               # per-facility TdlsConfig map is deliberately out — a load re-derives it from the ARTCC before the restore runs
TowerListSnapshotMapper.cs     # TowerListTracker ⇄ TowerListSnapshotDto (lists with entries only, each with its FacilityId; Restore clears the session and re-adds; null → empty;
                               # a list without a facility id or for a (facility, list) the room does not configure is dropped with one aggregated warning; the list airports are the ARTCC's, never snapshotted)
TdlsSnapshotMapper.cs          # TdlsState ⇄ TdlsSnapshotDto. Restore replaces the session state and leaves TdlsState.Configs alone — the scenario
                               # load that runs before a restore has just re-derived it from the ARTCC, and the snapshot never carried it
TaxiRouteDto.cs                # Taxi route segments + hold-short points (re-resolved from ground layout on restore; HoldShortPointDto.Unable keeps a moved unmakeable bar where it is)
SnapshotSchemaMigrator.cs      # Sequential migration chain for snapshot DTO versioning; SnapshotSchemaException

# Soak/ — soak-testing harness pieces shared by the yaat-server soak runner and live attach (docs/plans/controller-ai/08)
CapturedLogRecord.cs           # One captured log entry (level, category, formatted message, exception text, UTC stamp)
CapturingSimLogProvider.cs     # Bounded ring-buffer ILoggerProvider: Warning+ tap registered on the same factory as SimLog.Initialize; Drain() per tick, DroppedCount on overflow

# Testing/
TestVnasData.cs                # Shared test data loader: NavData, CIFP, AircraftSpecs, AircraftCwt, FaaAcd, AircraftProfiles, FacilityOpsDatabase (from Data/FacilityOps). EnsureAircraftDataInitialized loads just the aircraft tables once per process without touching the NavigationDatabase singleton, for a harness (e.g. yaat-server's RoomEngineTestHarness) that installs its own

Proto/nav_data.proto           # Compiled by Grpc.Tools → NavDataSet
```

## Yaat.LayoutInspector — CLI tool (`tools/Yaat.LayoutInspector/`)

Loads airport GeoJSON and queries the ground graph (nodes, taxiways, runways, exits, BFS path traces, pathfinder route forensics, parking/spots), renders interactive HTML maps with optional tick overlays, and prints text tick-tables from `TickRecorder` JSON. Output modes are mutually exclusive: `--html` → HtmlRenderCommand, `--dump` → DumpCommand, `--tick-table`/`--tick-summary` → TickTableCommand, otherwise → QueryCommand (text or `--json`).

```
Program.cs                     # Thin entry: parse args → bootstrap → dispatch ICommand
CliOptions.cs                  # Options record + TryParse (all arg parsing lives here, including comma-separated --node id lists, batch query flags, --html-route, --pathfinder, repeatable --ticks [LABEL=]<path> sources)
UsageText.cs                   # --help text
Bootstrap.cs                   # NavData auto-discovery (walks up to yaat.slnx) + debug logger wiring

Commands/
  ICommand.cs                  # int Execute(LayoutAnalyzer, CliOptions)
  QueryCommand.cs              # Default: text/json query dispatch (--taxiway, --runway, --node, --exits, --bfs, --pathfinder, --parking, --spots, --intersection, --validate)
  HtmlRenderCommand.cs         # --html <path>: interactive HTML render; honors all --html-* highlights and overlays --ticks animation when present
  DumpCommand.cs               # --dump: full airport JSON to stdout
  TickTableCommand.cs          # --tick-table / --tick-summary: TickRecorder JSON → fixed-width text table; optional --tick-ref + --tick-hold-shorts add cross-track / along-track columns

Tick/
  TickRecording.cs             # Top-level TickRecorder JSON schema as sealed records (mirrors Yaat.Sim.Tests.Helpers.TickRecording); rejects unknown major versions
  TickJsonReader.cs            # JSON file → TickRecording
  TickRecordingLoader.cs       # Reads every --ticks source; a missing/empty/malformed file becomes a TickSourceProblem instead of hiding the others
  TickRecordingMerger.cs       # Merges --ticks sources into one recording: a LABEL renames a source's aircraft (one aircraft → LABEL, several → LABEL:CALLSIGN), palette-colours multi-source merges, orders ticks by time; throws TickMergeException on a shared unlabelled callsign or mixed airports
  TickDataRow.cs               # One per-tick aircraft state row used by both HTML overlay and text formatter
  RunwayReference.cs           # Runway centerline (lat/lon + true heading) for signed xteFt / hdgErr columns
  HoldShortResolver.cs         # Resolve --tick-hold-shorts taxiway letters → GroundNode list + along-track distance math

LayoutAnalyzer.cs              # Core query engine over AirportGroundLayout
LayoutValidator.cs             # Post-fillet sanity checks: stale node refs, degenerate arcs, tangent misalignment (run via --validate)
QueryResults.cs                # Result record DTOs for all queries
IFormatter.cs                  # Output formatter interface (text vs JSON)
TextFormatter.cs               # Human-readable stdout formatter for query results
JsonFormatter.cs               # JSON stdout formatter (--json flag)
TickTableFormatter.cs          # Fixed-width text formatter for --tick-table / --tick-summary (not an IFormatter — operates on a row list, not single results)
HtmlRenderer.cs                # Interactive HTML+Canvas renderer; embeds layout JSON in inspector-template.html, all rendering happens client-side
inspector-template.html        # Page shell — pan/zoom, search, toggle highlights, tick-overlay player, URL-hash persisted view
inspector.css / inspector.js   # Extracted styles + client logic (layout polish, forensic restyle); kept out of the C# string template
```

## Yaat.SpeechSandbox — GUI/CLI tool (`tools/Yaat.SpeechSandbox/`)

Interactive sandbox for the speech pipeline (STT) and text-to-speech (TTS) experiments. Loads `UserPreferences` from the standard YAAT config location so the sandbox uses the same models/settings as the live app.

```
Program.cs                     # Entry: dispatches CLI subcommands (--pipeline, --lmkit-stt, --lmkit-models, --lmkit-gpus, --yaat-catalog, --llm-probe, --ouroboros, --atc-ouroboros, --eval) or launches the GUI
App.axaml{,.cs}                # Avalonia app shell; Fluent dark theme + h2/subtle styles
MainWindow.axaml{,.cs}         # TabControl host with two tabs: STT pipeline (existing) + TTS sandbox (M10.0)
TtsSandboxView.axaml{,.cs}     # TTS tab: sherpa-onnx + Piper LibriTTS-R + tunable radio FX (band-pass/Q/drive/squelch); auto-detects voice pack at .tmp/voices/, plays through PortAudio
OuroborosRunner.cs             # --ouroboros: synthetic round-trip harness (canonical → readback → Piper TTS → STT pipeline → compare); PASS/FLAKY/FAIL per case + markdown report
OuroborosCorpus.cs             # Corpus JSON schema for --ouroboros cases
AtcOuroborosRunner.cs          # --atc-ouroboros: controller-voice ouroboros — SynthTemplates cases across every PhraseologyRules family, spoken with Piper, scored through EvalRunner, aggregated per family/template and diffed against Corpus/atc-ouroboros-baseline.json (--update-baseline); exit 3 on a per-family or totals regression
AtcOuroborosResults.cs         # results.json / baseline schema (FamilyResult, TemplateResult, TotalsResult, AtcOuroborosResults, the nested CommandRates) plus the pure aggregation, the rate-based family diff (one-case floor, totals over shared families) and exit-code logic
CommandScoring.cs              # Per-clause command scoring: recognised / wrong-args / wrong-verb / inserted / rejected, order-insensitive over `,` clauses
SynthTemplates.cs              # Controller-phraseology template catalog (SynthTemplate per rule family, Compound for multi-clause) and the verify-or-gap planner: renders, verifies through the rule mapper, samples only verified templates, records the rest as TemplateGap
Corpus/atc-ouroboros-baseline.json # Committed --atc-ouroboros baseline the diff compares against
PiperSynthesizer.cs            # sherpa-onnx Piper synthesis shared by the TTS tab and ouroboros
EvalRunner.cs                  # --eval: real-audio eval harness; scores the production pipeline against labeled captured WAVs (tests/Yaat.Client.Tests/TestData/speech-corpus/); canonical exact-match + WER + STT latency, LMKIT_TEST_MODEL override, --whisper/--parakeet STT A/B flags, scores each case under the context its expected.json carries (runways, taxiways, destinations), auto-stubs expected.json from sample-store session.json (camelCase, deserialized as SpeechSession); in-process core used by --atc-ouroboros
SherpaSttEngine.cs             # sherpa-onnx OfflineRecognizer (NeMo transducer, e.g. Parakeet-TDT) STT probe engine for --eval --parakeet; sandbox-only prototype
SynthCorpusGenerator.cs        # --synth-corpus: synthetic controller-phraseology eval cases from SynthTemplates (verified plan → Piper multi-speaker synth → --eval case dirs marked synthetic, with template + context); unverifiable templates reported as GAP lines and left out
SynthAudioCache.cs             # Content-addressed cache of synthesized case WAVs (%LOCALAPPDATA%/yaat/cache/synth-audio/), keyed by PipelineVersion, voice-pack model hash, length scale, text, speaker, speed, sample rate and padding (first writer wins), so one seed gives the same audio across runs (Piper differs per process); --no-synth-cache bypasses it
```

## Yaat.GuideCapture — CLI tool (`tools/Yaat.GuideCapture/`)

Headless screenshot harness for `USER_GUIDE.md`. Boots an in-process `yaat-server` on a free loopback port, runs `Yaat.Client` under `Avalonia.Headless` with the real Skia backend (`UseHeadlessDrawing = false` + `UseSkia()`), then drives every Scene in `SceneCatalog.All` through `HeadlessUnitTestSession.Dispatch`. Each scene captures one PNG via `Window.CaptureRenderedFrame()`. Rerun whenever a UI surface in the guide changes.

```
Program.cs                     # Entry: parses --scene/--out, starts InProcessServer, pins every wall clock a capture shows (server TerminalClock, client WallClock seams on MainViewModel / VStripsViewModel / VTdlsView) to FixedTimeProvider.CaptureInstant and the server's ScenarioSeedSource (session start + RNG seed), dispatches scenes; calls Environment.Exit so headless threads don't keep the process alive
ModuleInit.cs                  # Redirects YAAT_APPDATA_DIR to a temp folder + seeds preferences.json (CID/initials AB/ARTCC) so MainViewModel.AttemptConnectAsync passes its identity gates
Capture/FixedTimeProvider.cs   # TimeProvider frozen at CaptureInstant, the instant every pinned clock shows
Capture/RoomTicks.cs           # AdvancePausedAsync: runs a paused room forward an exact number of sim-seconds (RoomEngine.AdvanceLiveSecond under the tick gate, in chunks so the radar builds its history), waiting two hosted-loop passes per step (detected by clearing TrainingRoom.PausedSinceUtc) so the client holds the step's state before the capture
Server/InProcessServer.cs      # Port allocation (TcpListener trick) + ServerApp.BuildAsync + StartAsync/StopAsync lifecycle; exposes Services and starts with LiveTraffic:Enabled so the live-traffic scene can feed the store
Capture/Scene.cs               # Abstract scene: BeforeWindowAsync, CreateWindow, AfterShowAsync, GetCaptureTarget for popout children, ExtraWindows; AfterCapture runs once the scene ends, captured or not, and restores any preference or view setting the scene changed so no later scene inherits it
Capture/Runner.cs              # Per-scene flow: setup → show → settle → capture PNG; Width/Height of 0 means "use the window's natural size"; then runs the scene's AfterCapture (a throw is written to stderr and fails the scene, counted in the exit status like a capture failure) and closes its ExtraWindows (each close guarded, a failure written to stderr)
Capture/CaptureContext.cs      # Per-run state: ServerUrl, ServerServices (the in-process server's DI root), RepoRoot (walks up to yaat.slnx)
Capture/SceneActions.cs        # WaitUntilAsync + WaitForConnectionAsync / CreateRoomAsync / LoadScenarioAsync helpers shared by scenarios; OpenMenuAsync opens a MainWindow top-level menu by header (the headless dropdown draws in the overlay layer, so the capture includes it); OpenButtonFlyoutAsync opens the Flyout of the button with a given content, which also draws in the overlay layer; SendCommandAsync sends a command to an aircraft through the command box and returns the terminal line that answers it (response, warning or error)
Capture/SceneCatalog.cs        # Static catalog of every scene
Scenes/                        # ScenarioSceneBase (connect → room → load → tab) + per-scene subclasses
                               # MainWindow*Scene — empty / connected-empty / overview / popped-out
                               # Getting Started: MenuFileScene / MenuScenarioScene (menu open) / ConnectDialogScene / FirstCommandScene (selected aircraft, terminal reply, typed command)
                               # AircraftListScene / GroundViewScene / RadarViewScene (paused, advanced by RoomTicks) / FlightStripsScene / VtdlsTabScene (each finds its tab by the view whose DataContext is the dock entry, never by index)
                               # TerminalPanelScene / CommandBarScene / MetarWindowScene / FileBugReportDialogScene / ExportRoomScenarioScene
                               # TimelineSceneBase — turns the timeline bar on (restored in AfterCapture); StagePlaybackAsync runs the OAK room a paused 2:00 with bookmarks at 0:30 and 1:30, rewinds to 1:00, refreshes the timeline markers and waits for a stable aircraft count
                               # TimelinePlaybackScene / BookmarksListScene (Bookmarks ▾ flyout open) / TakeControlDialogScene (the confirmation as a child window, cancelled after the capture) / TerminalRewindMenuScene (a terminal line's right-click "Rewind to 1:30" menu in a host window)
                               # GroundViewZoom — zooms the ground view in on a point for a close-up and puts the saved centre and zoom back
                               # GroundTaxiRouteScene (a pushed-back aircraft taxiing with its route pinned on "Always show", mode restored after) / JustLandedScene (an ADD arrival on a 4 NM final run forward to just after touchdown on runway 30)
                               # LiveTrafficScene — RadarViewScene + Live Traffic on + four fake SWIM tracks upserted into LiveTrafficStore (dashed shadows, LIVE status bar)
                               # GroundViewPopoutScene / RadarViewPopoutScene
                               # FlightPlanEditorScene
                               # FavoritesBarScene / FavoritesPanelScene
                               # ArrivalGeneratorsEditorScene
                               # StandaloneWindowSceneBase + Settings/LoadScenario/LoadWeather/Weather/About
```

The OAK clearances scenario `docs/atctrainer-scenario-examples/01H06NVK7VN8BS7MCDXHKJZ7MQ.json` is the canonical fixture for every "scenario loaded" scene.

Runs are deterministic for most scenes (pinned clocks, pinned seed, paused fixed-tick rooms, and the strip barcode drawn from `FlightStripControl.BarcodeHash`, FNV-1a, rather than the per-process `string.GetHashCode`); the scenes that still differ run to run are the blinking-element phase (`Environment.TickCount64` sites) and client arrival-order races, tracked in the plan.

## Yaat.ClientDriver.Mcp — MCP server (`tools/Yaat.ClientDriver.Mcp/`)

MCP stdio server that drives the real desktop client and a running CRC (enumerate, click, type, screenshot, log tail): a YAAT client with an automation pipe is driven over the pipe, everything else (CRC, a client without one) through Windows UI Automation. Windows-only TFM with `EnableWindowsTargeting` so the Linux CI solution build still compiles it. Reference: [`client-driver-mcp.md`](client-driver-mcp.md).

```
Program.cs                     # Per-monitor-v2 DPI awareness, then the MCP host: stdio transport, tools from the assembly, a call-tool filter (WithPipeCallErrors) that appends the client's logged errors to every tool result, all logging to stderr (stdout is the protocol channel)
ElementRegistry.cs             # Singleton: one short-id sequence (e1, e2, …) for UIA elements (deduped by runtime id) and pipe nodes (deduped by pid + node id); Resolve returns the ElementRef, ResolveUia the AutomationElement (stale/disabled/timeout → McpException; a pipe id refused)
ElementRef.cs                  # What an id maps to: UiaElementRef(AutomationElement) or PipeNodeRef(pid, node id)
UiaQuery.cs                    # Find / describe / dump-tree helpers over System.Windows.Automation
NativeInput.cs                 # P/Invoke: SendInput clicks, SetForegroundWindow, minimised test; one input gate serialises click and SendKeys sequences; blocked input throws; ReadWindowGeometry (DWM frame bounds, client area on screen, minimized) for a recording's crop
WindowCapture.cs               # CopyFromScreen of a window's bounds, downscale, PNG under .tmp/client-driver/shots/; FromPng saves a pipe shot (PipeShot) through the same downscale-and-save path
Tools/ProcessTools.cs          # launch_yaat (Yaat.Client.exe only, scratch YAAT_APPDATA_DIR), list_processes, stop_process (pid + start time, or a Yaat.Client by name), tail_yaat_log
Tools/InspectTools.cs          # list_windows, dump_tree, find_elements, get_value, screenshot (a pipe id: the client's own render by nodeId, downscaled to maxWidth)
Tools/InputTools.cs            # invoke, click, click_point, set_text, send_keys, focus, set_input_mode: a pipe id goes to the client's pipe method (invoke as click; click_point with a pipe windowElementId in window DIPs; send_keys with no element to the remembered pipe pid's focus), anything else through UIA input; every successful UIA-routed call (here and in InspectTools) clears the remembered pipe pid
Tools/PipeTools.cs             # wait_for, wait_until and queue_file_pick, pipe-only: the pid argument else the remembered pipe pid (TargetPid, shared with BatchTools); wait_for's request timeout is its timeoutMs + 5 s; wait_until (simulation-state conditions, optional screenshot saved through InspectTools.SavePipeShot, then actions) clamps timeoutMs to 100-600000 ms with the same +5 s margin and formats met or not met with each condition's last value; its stop_recording action is held back from the client and run here after a met answer (a final mark, then RecordingSession.StopAsync)
Tools/RecordTools.cs           # record_start (pid → its main window, over the pipe for a YAAT client, else UIA; or a list_windows id; fps 1-60; audio defaults to on with a pipe), record_mark (sim time from the client's get_sim_time, 2 s wait, null for CRC), record_stop
Recording/RecordingSession.cs  # Singleton: the server's one recording (start, mark, stop under one gate), its paths (<clip>.mp4, -marks.json, -recorder.log, -ffmpeg.log, a deadline file), the marks file rewritten on each mark, the 1 h cap, a died pipeline reported with its exit code and log tail; disposed (pipeline killed) with the host
Recording/IRecordingBackend.cs # The seam to processes and windows (ffmpeg on PATH, recorder exe, encoder choice, UIA top-level windows, window geometry, probe, pipeline start) and IRecordingProcess, the running pipeline
Recording/ProcessRecordingBackend.cs # The real backend: runs the recorder's --probe, lists ffmpeg's encoders and probe-encodes one 256x256 frame for h264_nvenc, starts the recorder piped into ffmpeg, kills the tree
Recording/FfmpegPipeline.cs    # Pure command building: the recorder's probe and run arguments, ffmpeg's arguments (rawvideo BGRA from stdin stamped by arrival time, -fps_mode vfr; the s16le audio pipe; the crop filter; h264_nvenc or libx264; -n), cmd quoting of the joined pipeline, encoder-listing parse
Recording/ClientAreaCrop.cs    # The crop box: the window's client area offset inside the captured frame (frame = DWM extended frame bounds), its sides rounded down to even for yuv420p
Recording/RecorderLog.cs       # Reads the recorder's stderr log back: the first-frame time (clip seconds), its end line (seconds, frames) and the tail for an error
Tools/AppTools.cs              # list_app_tools and call_app_tool over the pipe (the client's [AutomationTool] methods); call_app_tool waits up to 120 s (AppTools.CallTimeout)
Tools/BatchTools.cs            # batch_drive: 1-100 steps over one client's pipe (any pipe method by name, assert wait via wait_for, assert no_errors via PipeCallErrors), stops at the first failure, 300 s deadline, JSON result; screenshots saved through InspectTools.SavePipeShot
Pipe/PipeClient.cs             # One named-pipe connection to a client's automation host (protocol DTOs linked from the client's Automation/Protocol): serialised requests, a 30 s request timeout, the connection dropped on a cancel, a broken pipe or an unparsable answer and re-opened on the next call; each answer's clientErrors go to PipeCallErrors
Pipe/PipeDirectory.cs          # Finds a client's pipe from its discovery file (%TEMP%/yaat-automation/<pid>.json), caches one PipeClient per pid, evicts a client whose process has exited; ForgetAsync; LastTargetPid, the pid of the last successful pipe call (cleared when that pid is forgotten, evicted or its pipe found closed)
Pipe/PipeCalls.cs              # The one place a tool talks to a pipe: SendForElementAsync for an element id (STALE_NODE reads as the UIA gone message), SendForPidAsync for a pid; a closed or missing pipe reads as "the automation pipe for pid N closed", other host errors keep their message; GetNodeAsync reads one node; TryRouteAsync picks pipe or UIA for a pid
Pipe/PipeCallErrors.cs         # MCP call-tool filter: collects every pipe answer's clientErrors during one tool call (an AsyncLocal collector, fed by PipeClient) and appends one formatted text block to the result, success or error (an McpException is rethrown with the block after its message; any other exception becomes an error result carrying the block), with the count of entries the client omitted in its header; WithPipeCallErrors() is the one registration Program.cs and the tests share
Pipe/PipeInput.cs              # The input tools' pipe params (PipePointer: button, modifiers, click count) and result wording: "clicked (<action>) on <row> (pipe)", "sent '<text>' to <row>, N strokes (pipe)"
Pipe/PipeDescribe.cs           # The row shape of a pipe node or window in dump_tree / find_elements / list_windows: Avalonia type, text, id (AutomationId else x:Name), state, rect in window DIPs (NodeInfo.WindowBounds)
Pipe/PipeRemoteException.cs    # A host error answer: code, the host's own message (RemoteMessage) and its hint
Recording/RecorderLocator.cs   # FindRecorder(): recorder/Yaat.WindowRecorder.exe beside the MCP assembly (FindRecorder(baseDirectory) for tests) (the csproj copies tools/Yaat.WindowRecorder's output there); missing → an actionable InvalidOperationException
launch.ps1                     # What `.mcp.json` runs: copies bin/ to %LOCALAPPDATA%/yaat/client-driver-mcp/run-<pid> and starts the server there, so a running server never locks the build output; prunes dead sessions' copies
McpStdio.ps1                   # Dot-sourced JSON-RPC-over-stdio plumbing for the two scripts
smoke.ps1                      # Protocol smoke: stdout is pure JSON, every tool listed; opens no window
live-check.ps1                 # Live pass against a real client: default and -Background over the automation pipe (foreground and cursor must not change; -Background also asserts WS_EX_NOACTIVATE and that minimizing a console in front never hands the client the foreground), -WithInput over UI Automation with real input, -Record (record_start/mark/stop on the client: encoder, client-area size, an audio stream, sim-time marks, a minimized window refused); captures CRC's first display window when CRC is running
```

## Yaat.WindowRecorder — window and process-audio capture (`tools/Yaat.WindowRecorder/`)

Helper exe the client-driver MCP launches to record a window: Windows.Graphics.Capture video by window title or handle (raw BGRA frames on stdout, a steady frame rate from a timer since WGC delivers frames only on redraw) and the process's own audio through WASAPI process loopback (s16le into a named pipe); every message on stderr.

It refuses a minimized or hidden window before capture (exit 2) and names on stderr each mid-run change between capturable and hidden-or-minimized, and a close once (a DWM-cloaked window counts as capturable).

A port of the `video-capture` skill's WgcCapture with the same flags (`--title`, `--hwnd`, `--fps`, `--seconds`, `--until-file`, `--probe`, `--audio-pid`, `--audio-pipe`, `--audio-only`, `--help`). TFM `net10.0-windows10.0.26100.0` with `EnableWindowsTargeting` so the Linux CI solution build compiles it; Vortice.Direct3D11/DXGI for the D3D device. The MCP references it with `ReferenceOutputAssembly=false` and copies its output under `recorder/` (see `Recording/RecorderLocator.cs` above).

## FOLLOW montage clips (`tools/montage/follow/`)

One folder per clip id of the FOLLOW video montage ([`docs/plans/follow-video-montage.md`](plans/follow-video-montage.md)): each holds the clip's spawn-only `scenario.json`, the timed instructor commands that fly it into the situation (`script.txt`), and its rule card (`card.md`); the folder's `README.md` says how to record one.

## stash-procedure.py — CLI tool (`tools/stash-procedure.py`)

Captures a published procedure as an ARTCC CIFP fragment before it ages out of reach. The FAA sometimes drops a still-charted procedure from the CIFP dataset (KOAK NIMITZ); the prior-cycle chain recovers it for only ~12 months and only on a machine that cached the right cycle, so anything a facility depends on has to be pinned into `Data/ARTCCs/{ARTCC}/Procedures/*.cifp`.

```bash
python tools/stash-procedure.py NIMI --airport KOAK              # find + write the fragment
python tools/stash-procedure.py NIMI --airport KOAK --dry-run    # report which cycles carry it, write nothing
python tools/stash-procedure.py BDEGA4 --airport KSFO --kind star --artcc ZOA
```

Stdlib-only Python. A bare name (`NIMI`) matches every version (`NIMI5`/`NIMI6`) using the same base-name rule as `NavigationDatabase.StripTrailingDigits`; an exact id matches only itself. Searches newest-first across the local CIFP cache (`%LOCALAPPDATA%/yaat/cache/cifp/`), the repo's bundled `TestData/FAACIFP18.gz`, any `--search-path`, and optionally the FAA server (`--fetch`, usually 404s for past cycles).

Line selection mirrors `CifpParser`'s own gate exactly, and terminal-waypoint (`PC`) records are emitted alongside legs that reference an arc center. The owning ARTCC is inferred from existing `Data/ARTCCs/*/` content naming the airport; `--artcc` overrides. `--verify` round-trips the result through `Yaat.CifpInspector`.

## build-surface-temp-data.py — CLI tool (`tools/build-surface-temp-data.py`)

Generates the ASDE-X / SAAB SAID final-approach overlay vZOA controllers otherwise hand-draw at SFO: each runway's extended centerline outbound from the landing threshold, with a short mark across it at each whole mile.

The mark shape is copied from the reporter's screenshot — centred on the runway's own centerline, crossing it on both sides, and stopping short of the parallel so the two never merge; no text. Writes an ARTCC sidecar at `Data/ARTCCs/{ARTCC}/SurfaceTempData/{FACILITY}.json`, which yaat-server seeds every room from.

```bash
python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --runways 28L 28R --set 1 --set-name FINALS
python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --runways 28L 28R --dry-run
```

Stdlib-only Python. Runway ends come from the vNAS airport map GeoJSON (the same surface map the controller sees) — fetched from the training API, or read from the bundled `TestData/{airport}.geojson` when present. Bearings are TRUE, because the output is lat/lon; the centerline is drawn on the reciprocal of the landing course.

CRC closes an area's ring itself and draws it as an outline plus a hatched fill, so lines and ticks are emitted as quads `LINE_WIDTH_NM` (10 ft) across — wide enough to tessellate (a zero-area ring throws in the client) and narrow enough to stay sub-pixel, so the outline collapses to the 1-px line in the issue #312 reference instead of a hatched band.

`--append` merges a second flow's group into the same facility file, each group in its own SET; `--length`, `--tick-interval`, and `--tick-length` tune the geometry; `--set` files everything into one CRC SET so a controller can toggle the whole overlay off.

Committing generated output is one way to author a sidecar; the other is drawing it in CRC and using the client's **Tools → Export ASDE-X / SAID Temp Data...**, which emits the same schema from a live room.

## discord-bot — Cloudflare Worker (`tools/discord-bot/`)

Bridges the Discord server with GitHub (forum threads ↔ issues, scenario-validation buttons) and hosts the Ko-fi-fed server-cost tracker. JS, no framework; state in the `THREAD_ISSUES` KV namespace; tested with vitest (`pnpm test`, also in CI). Operating notes, secrets, and footguns: [`docs/discord-integration.md`](discord-integration.md).

```
wrangler.toml                     # Worker name, KV/R2 bindings, cron (*/5), vars (GITHUB_REPO, VALIDATION_REPO, MONTHLY_COST_USD, KOFI_PAGE_URL), secret names
validation-channels.json          # ARTCC → scenario-validation channel id
support-config.json               # Server-cost tracker ids written by setup-support (guild, ticker voice channel, #server-costs, pinned embed message, the two supporter roles)
src/worker.js                     # fetch/scheduled entry points: Discord interactions (slash commands, buttons), POST /github webhook, POST /kofi, POST /sync/N; thread↔issue sync, githubFetch pacing/retry, GitHub App auth
src/support.js                    # Server-cost tracker: Ko-fi form-body parse + verification_token check, KV ledger (record in list metadata), carry-forward month math, channel-name ticker + embed rendering, change-gated Discord writes, forgetPayment
src/validation-channels.js        # validation-channels.json → lookup maps
src/register.js                   # Registers the slash commands (guild or global scope; clears the other)
scripts/ensure-validation-buttons.js  # Pins a Run Validation button in every ARTCC channel (also run by the yaat-server validation workflow)
scripts/setup-support.js          # Idempotently creates the supporter roles, the locked ticker voice channel, the read-only #server-costs channel, the pinned embed; writes support-config.json
src/worker.test.js, src/support.test.js  # vitest: githubFetch retry/pacing, queued issues; Ko-fi webhook, ledger idempotency, carry-forward math, display change gating
```

## yaat-crc-config — Standalone Rust binary (`tools/yaat-crc-config/`)

Tiny (~200 KB) standalone tool that ports the YAAT client's `Tools → Configure CRC Environments` flow into a single small binary. Lets students who only want to point CRC at YAAT skip installing the full client.

Released independently from the `crc-config-v*` tag via `.github/workflows/yaat-crc-config.yml`; the macOS build wraps the binary in a minimal `.app` bundle (the tool is dialog-driven, so it is double-clickable), signs it with the client's Developer ID Application certificate, and ships it notarized and stapled inside a likewise-notarized `.dmg`.

A bare Mach-O cannot carry a stapled ticket, so the `.app` wrapper is what lets Gatekeeper clear it offline. Windows and Linux ship the bare executable.

```
Cargo.toml                     # Size-optimized release profile (opt-level=z, lto=fat, panic=abort, strip)
build.rs                       # Validates ../../docs/crc-environments.json schema at build time
src/main.rs                    # Flow: detect dir → already configured? → confirm → upsert → success. windows_subsystem="windows" so double-click doesn't flash a console.
src/config.rs                  # Mirrors CrcConfigService.cs: find_crc_config_dir (Win registry + LOCALAPPDATA, Mac/Linux home paths), are_entries_present, upsert_entries (preserves unrelated keys via serde_json::Value)
src/dialog.rs                  # Cross-platform native dialogs: MessageBoxW (Win), osascript (Mac), zenity/kdialog/console (Linux)
```

The canonical YAAT environment list (`YAAT1`) lives in `docs/crc-environments.json` — a single source of truth shared by this tool, `Yaat.Client.Core/Services/CrcConfigService.cs` (embedded resource), and `Setup-CrcEnvironment.ps1` (read at script execution time when run from the repo). Self-hosted servers are not defaults; they are added by hand or via `Setup-CrcEnvironment.ps1 -Servers`.

## yaat-server — ASP.NET Core server (`..\yaat-server\`)

Separate repo. References Yaat.Sim via sibling project ref. Provides: SignalR comms, CRC protocol, training rooms, scenario loading, broadcast fan-out.

```
src/Yaat.Server/
  Program.cs                   # DI setup, VNAS/CIFP init, route mapping, AdminPassword validation
  YaatOptions.cs               # IOptions: AdminPassword (Yaat section); NexradOptions (Nexrad section: Enabled, RefreshMinutes)

  Hubs/
    TrainingHub.cs             # /hubs/training (JSON); room lifecycle + delegates to RoomEngine
    CrcWebSocketHandler.cs     # Raw WebSocket /hubs/client for CRC; resolves room via JWT CID
    CrcClientState.cs          # Per-CRC state machine; holds RoomEngine ref; topic subscriptions; BuildTopicPayload helper
    CrcClientState.Session.cs  # Partial: session lifecycle (StartSession, EndSession, lifecycle push helpers)
    CrcClientState.Stars.cs    # Partial: STARS display-related state (consolidation, datablock format)
    CrcClientState.Asdex.cs    # Partial: ASDEX handlers (temp data, presets, safety config) with event broadcasts
    CrcClientState.Strips.cs   # Partial: flight strip CRUD with event broadcasts
    CrcClientManager.cs        # Client registry; BroadcastAsync fan-out
    NegotiateHandler.cs        # POST /hubs/client/negotiate; JWT extraction → CrcNegotiateTokenStore
    CrcNegotiateTokenStore.cs  # ConcurrentDictionary token→CID for CRC room resolution
    ApiStubHandler.cs          # GET/POST /api/* → [] (CRC startup probes)

  Simulation/
    TrainingRoom.cs            # Room state: Members, World, ActiveScenario, Weather, Engine, GroupName, ConsolidationState, LineNumbers, AircraftAssignments, SessionSettings, ResourcePin, the load flag (LoadingBy / TryBeginLoad / EndLoad)
    RoomResourcePin.cs         # The ARTCC configs and airport layouts a room's load used (PinnedAirportGroundData); every reload, export and checkpoint reads them instead of the live caches
    ScenarioLoadReporter.cs    # A scenario load's progress step table and its texts (ScenarioLoadSteps), sent to the loader as ScenarioLoadProgress
    RoomSessionSettings.cs     # Room-level copy of the controller-settable session settings; seeds every freshly built SimScenarioState so they survive a scenario load, restart, or rewind (issue #313)
    TrainingRoomManager.cs     # Room registry + client→room + CID→room mapping + admin tracking

See [session-persistence.md](session-persistence.md) for planned-restart room checkpoints (yaat-server `Simulation/Persistence/`).
    RoomEngine.cs              # Per-room facade: tick, commands, scenario, broadcast, consolidation
    ConsolidationState.cs      # Thread-safe manual consolidation overrides per room
    RoomEngineFactory.cs       # Creates RoomEngine with shared singleton deps
    RoomTickLoopService.cs # Thin orchestrator: 1s tick loop iterating rooms
    TickProcessor.cs           # Stateless tick logic (physics, spawns, triggers, pilot proactive hooks); drains ready solo frequency transmissions as SAY entries and emits PilotTransmissionBroadcast
    ScenarioLifecycleService.cs # Scenario load (prepare off the tick gate, commit under it)/unload/spawn/generator logic; room close
    ScenarioState.cs           # Per-room active scenario state: queues, positions, generators, channels
    TrainingBroadcastService.cs # SignalR hub context wrapper for training clients, including PilotTransmissionBroadcast fan-out.
    PilotVoiceAssigner.cs      # Pure deterministic `(scenario rng seed, callsign) -> speaker id 0..903` helper for pilot voice events.
    CrcBroadcastService.cs     # CRC wire-protocol broadcast; per-room scoped via BroadcastBatch; BroadcastToTopicSubscribersAsync
    CrcVisibilityTracker.cs    # STARS/ERAM/TowerCab visibility rules; STARS hysteresis (add at elev+100, remove at elev); AircraftState.IsVehicle excluded from STARS; ASDE-X/SAID evaluators are pure diffs of the Sim's per-aircraft membership sets
    StarsLineNumberAssigner.cs # Per-room sequential line number assignment (1-99 wrap)
    StripCommandHandler.cs     # Flight strip command dispatch (all 15 canonical verbs incl. SCAN)
    StripBroadcaster.cs        # Flight strip broadcast coordination: SignalR + CRC topic paths
    StripMutations.cs          # Stateless strip mutation helper: create/delete/amend logic
    StripCommandTranslator.cs  # Translate CRC MessagePack invocations → canonical command strings
    DtoConverter.cs            # AircraftState → CRC + training DTOs + ASDEX/strip converters
    INexradProvider.cs         # NEXRAD imagery provider contract (INexradProvider); gated on room.Weather == null (preset-weather short-circuit)
    EmptyNexradProvider.cs     # Kill-switch: returns NexradDataDto.Empty() regardless of inputs (Nexrad:Enabled=false)
    WmsNexradProvider.cs       # Default: fetches NOAA opengeo conus_cref_qcd PNG, 5-min TTL cache, cos(mid_lat) width
    NexradRefreshHostedService.cs # PeriodicTimer (Nexrad:RefreshMinutes, default 5); refreshes cache + broadcasts ReceiveNexradData per room with live weather

  Commands/
    CommandParser.cs           # Server-side canonical parsing; IsTrackCommand(), IsCoordinationCommand()
    DepartureCommandParser.cs  # Departure-specific command parsing
    GroundCommandParser.cs     # Ground operation command parsing

  Protocol/                    # CRC binary: VarintCodec, MessageFraming, SignalRMessageParser, SignalRMessageBuilder
  Dtos/
    TrainingDtos.cs            # JSON DTOs for training client communication
    CrcDtos.cs                 # Main CRC binary DTOs (MessagePack)
    CrcDtos.FlightPlan.cs      # Partial: flight plan-related CRC DTOs
    CrcDtos.Session.cs         # Partial: session/StartSession CRC DTOs
    CrcDtos.Stars.cs           # Partial: STARS display-related CRC DTOs (line numbers, short-term conflicts, readout area)
    CrcDtos.Asdex.cs           # ASDEX event DTOs (temp data, presets, safety config, hold bars, alerts)
    CrcDtos.Strips.cs          # Flight strip DTOs (StripItemDto, FlightStripsStateDto, StripBayContentsDto)
    FlightStripsConfigDto.cs   # Flight strip bay layout config (delivered on ScenarioLoaded/RoomStateDto)
    CrcEnums.cs                # Enums for CRC protocol
    CrcFormatters.cs           # Formatting helpers for CRC DTOs
    TopicFormatter.cs          # Topic subscription/message formatting
  Data/
    AirportGroundDataService.cs  # IAirportGroundData impl; fetches GeoJSON from vNAS training API
    ArtccConfig.cs             # VNAS ARTCC config deserialization models (VideoMapConfig, StarsAreaConfig, etc.) — lives in Yaat.Sim/Data/Vnas/
    ArtccConfigService.cs      # Loader: downloads + caches ARTCC config from vNAS; resolution methods delegate to ArtccConfigResolver in Sim
    ArtccConfigService.Consolidation.cs  # Partial: thin facade over Sim's ArtccConfigResolver consolidation methods
    ArtccConfigService.VideoMaps.cs      # Partial: facility video maps + position display DTO builders (CRC wire-format, kept server-side)
    PositionRegistry.cs        # Thread-safe CRC + RPO position tracking
    NexradBoundsLoader.cs      # Parses Data/Nexrad/NexradBoundingBoxes.geojson (24 ARTCCs) → per-ARTCC NexradBounds (N/S/E/W)
  Udp/UdpStubServer.cs        # UDP port 6809 stub (CRC keepalive/registration)
  Logging/FileLoggerProvider.cs
```
