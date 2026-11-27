# YAAT-2912: plan for the Scenario merge window (YAATEdit M2.2c)

The owner answered every decision in section 4 with its recommended option; the answers are recorded on YAAT-2912, and the visual ones were asked on the mock `docs/plans/canvases/yaat-2912-decisions/index.html` (lane A each time). Brief B1 has landed.

The tree is `X:/dev/yaat.wt/feat-yaatedit/yaat`, branch `feat/yaatedit`, tip 9b0b7e9d5. Paths below are relative to it, and line numbers are at that commit.

## 1. What exists
- **Engine:** `src/Yaat.Sim/Authoring/ScenarioMerge.cs`
  - `Plan` is at :109.
  - `MergeRow` (:33-42) is a positional record with 8 parameters and no presence or field-key data.
  - Clash-only bulk choices are at :165-189.
  - Order rows: `MergeTree.OrderSuffix` is private (:250), `AddOrderRow` is at :381-407, and an edited value on an order row throws (:484-487).
  - Each planned row knows whether each side holds the value (`PlannedRow`, :240). `AddRow` (:423-444) drops that.
  - An Edit choice always writes a present value (:546).
- **Labels:** `ScenarioFieldLabels` (ScenarioDiff.cs:72-233) is internal. It is keyed by section plus label path ("startingConditions/parking", "presetCommands[]/command", "" for the list itself, "[]" for an item).
- **Store:**
  - `ApplyMerge` (LocalScenarioStore.cs:297-352) needs a server origin (:335-342).
  - `ComputeVersion` is at :93.
  - `CheckScenario` is static and refuses a nameless scenario (used at ScenarioEditSession.cs:129).
- **Session:** `ScenarioEditSession(store, artcc, id)` (ScenarioEditSession.cs:35) reads its text from the store (:41) and uses the store only in `Save` (:140).
- **Client reuse:**
  - `ScenarioAircraftRowViewModel(ScenarioAircraft, int)` builds a grid row (ScenarioAircraftRowViewModel.cs:32).
  - `ScenarioFieldText` holds the field kinds and their errors (:33-96), the engine and weight lists (:99-102) and `FormatTime` (:329).
  - Picker options come from `ScenarioGeneralViewModel.ReadPickersAsync` and `PickerData`, both private (ScenarioGeneralViewModel.cs:331, 444). Rating and auto-delete options are at :36-45.
  - `ScenarioValidationViewModel` runs over a session (ScenarioValidationViewModel.cs:43-49).
  - `ScenarioAircraftViewModel(session, services)` is at ScenarioAircraftViewModel.cs:39. Its view is one control holding the toolbar, detail panel and grid (ScenarioAircraftView.axaml:35-50).
  - Modal pattern: `ImportConflictDialog.ShowAsync` → `DialogPresenter.ShowModalAsync&lt;T&gt;` (ImportConflictDialog.axaml.cs:46; DialogPresenter.cs:37). Geometry: `WindowGeometryHelper` (ParkingPickerWindow.axaml.cs:62).
- **Command checks:** a preset command's error comes from the live checks (ScenarioEditChecks.cs:157-173, 369). A trigger command has no error line in the editor (InitializationTriggerRowViewModel.cs:57-69).
- **IScenarioBrowserPrompts fakes:** ScenarioBrowserViewModelTests.cs:1025, ScenarioEditorTestSupport.cs:247, ScenarioEditorWindowTests.cs:1575, ScenarioEditorTestInRoomTests.cs:312. 2912 does not touch the interface.

## 2. Decisions (orchestrator; evidence and cost)

**D1. Panes and the merged result.** Each pane is a set of lightweight line view models: one `ItemsControl`/`ListBox` whose line template draws the Mine cell, the gutter and the Theirs cell side by side. Lines therefore align by field, by list item and by aircraft id, and the BASE and MERGED VALUE strips go between lines.
- Why not the live views: the General view is expanders full of commit-on-leave text boxes with no read-only mode (no `IsReadOnly` in ViewModels/Authoring). Two live views can't align once one side has an aircraft the other lacks, and gutter buttons can't sit between them.
- Reuse:
  - grid cells from `ScenarioAircraftRowViewModel` over `ScenarioDocument.Read(side text).Scenario` (no session)
  - times through `ScenarioFieldText.FormatTime`
  - labels from `MergeRow.Label`
  - styles copied into the window (no edit to the General view)
- The editable result is the choices dictionary plus aircraft replacements, edited through the per-row MERGED VALUE editor and the merged-aircraft editor.
- `ScenarioEditSession.OverText` (B1) serves only the merged-aircraft editor and the checks at Apply.
- Cost: one Sim factory, and test-all at the end. The temp-store route costs disk writes per rebuild, a temp folder to manage, and a `RootLocks` entry per root (LocalScenarioStore.cs:38,64).

**D2. Absent vs JSON null.** Add read-only `bool InBase`, `InMine`, `InTheirs` to `MergeRow`, set from the slots in `AddRow`. In a two-way merge `InBase` is always false.
- Display: absent reads "(not set)", JSON null reads "(none)".
- Item rows (`FieldKey == "[]"`):
  - three-way: "added in mine/theirs", "removed in mine/theirs"
  - two-way: "only in mine/theirs", with "not in theirs/mine" in the other pane
- Field rows: tinted added or removed by presence.
- Cost: about 10 lines and one test. Deriving it in the view model would be exact only for item rows, since list items are always objects (`IsItemList`, :292).

**D3. Bulk choices are built in the view model, with no engine overload.**
- "Keep mine/theirs for every clash" uses `ChooseAll(side)`.
- "Take mine/theirs for everything" maps every row path to that side, order rows included. It drops earlier edits and aircraft replacements.
- "Edit every clash in place" opens an editor under every clash, pre-filled from its current choice or else mine. Order rows are skipped. An aircraft that exists on only one side gets no bulk merged editor.
- Cost: none in the engine. Its clash-only semantics stay as pinned in ScenarioMergeTests.

**D4. Previous/Next order.**
- Sort key: section rank, then the engine's row index. Section ranks follow ScenarioGeneralView.axaml:240-1027: General=0, ATC positions=1, Initialization triggers=2, Generators=3, VFR arrival generators=4, Overflight generators=5, Strip bays=6, Aircraft=7.
- Navigation walks every clash, resolved or not, with no wrap. It reads "clash n of N" (two-way: "difference n of N").
- On the Aircraft tab, Next selects the aircraft line and shows its detail.
- Each tab header shows its count of open clashes.

**D5. Show base.**
- `CanShowBase = !IsTwoWay`.
- Label: "Show base" with "base: {BaseCaption}"; two-way "Show base (no base version)", disabled.
- Three-way: every clash shows its BASE strip with its reason ("mine changed it to X; the server changed it to Y"); Show base adds strips under auto rows.
- Two-way: the note strip comes from the request; the summary reads "{N} differences to choose" and the footer "Choose a side for {N} differences to apply."; there are no auto-merged tags.

**D6. The field-to-editor table.**
- Add `MergeRow.FieldKey` (the label path) and a public `ScenarioMerge.OrderPathSuffix = "#order"` that `MergeTree` uses.
- The client table `ScenarioMergeFieldEditors` maps (Section, FieldKey) to an editor kind:
  - **Text:** name (with `NameRequiredText`), approach, callsign, type, route…
  - **Time / OptionalTime:** timeOffset, spawnDelay, presetCommands[]/timeOffset, generator times
  - **Number kinds** from `ScenarioFieldText`: distances, altitudes, lat/lon, heading, cruise…
  - **Choice:**
    - ratings, auto-delete, engine, weight, start kind, flight rules
    - student, auto-track and ATC positions from the catalog
    - airports from the config
    - free text when the config can't load, as in the editor
  - **Bool:** autoConnect, enabled, randomize, onAltitudeProfile
  - **CommaList:** autoTrackAirportIds, as the ATC row splits it
  - **Command:** a trigger command has no error line; a preset command shows its parse failure when the box is left
  - **Json fallback:** monospace box; text that isn't JSON shows "Not valid JSON: {message}" when left
- Each kind converts JSON to text and back, typed to match the document (seconds as numbers, strings quoted), and produces its error.
- Cost: promote `ReadPickersAsync`/`PickerData` to internal (no behaviour change).

**D7. Opening the window and writing.**
- `ScenarioMergeRequest(string ArtccId, string ScenarioId, ScenarioMergeTexts Texts, ScenarioMergeCaptions Captions, Func&lt;string, Task&gt; Write)`
  - `Texts(string? Base, string Mine, string Theirs)`
  - `Captions(Title, Mine, Theirs, Base, TwoWayNote)`
- `ScenarioMergeWindow(request, services, checks)` and `ShowAsync(Window owner) → Task&lt;ScenarioMergeResult?&gt;`, where `ScenarioMergeResult(string MergedJson, IReadOnlyList&lt;ScenarioEditIssue&gt; Warnings)`.
- Apply runs in this order:
  1. Build the merged text.
  2. Refuse text that doesn't parse: either `ScenarioDocument.Read` throws, or `LocalScenarioStore.CheckScenario` returns a reason. The window stays open with the reason on the footer's error line, naming the row from the exception path when it can.
  3. Run the checks.
  4. Show any warnings.
  5. Call `Write`. If it throws, the footer shows the error and the window stays open.
  6. Close with the result.
- Cancel or the close box returns null and writes nothing.
- 2913 adds `IScenarioBrowserPrompts.ShowMergeAsync` and passes `ApplyMerge(artcc, id, merged, theirs, ComputeVersion(theirs))` or `SaveWorkingCopy`. It also reopens the editor copy and asks about unsaved edits before reading Mine.

**D8. Other findings.**
- **Aircraft held by both sides:** they have no engine row of their own, so the merged-aircraft "Keep this" stores `_aircraftEdits[id]` (a JSON object).
  - Apply passes KeepMine for that aircraft's open rows, then swaps the aircraft node by id in the parsed output and writes it again indented.
  - For an aircraft on only one side, the merged editor returns `MergeChoice.Edit(aircraftNode)` on the item row.
  - The merge-so-far for the editor is `Apply(choices + open clashes→KeepMine + the item row forced to the side holding it)` plus the replacements.
- **Merged-aircraft host:** `ScenarioAircraftView` gets a detail-only property that hides the toolbar and grid.
- **`Plan` failures:** `Plan` throws `JsonException`; `ScenarioMergeViewModel.Create` lets it propagate and 2913 reports it.
- **Known bug:** YAAT-2780 (drop-down click swallowed) may hit the merge combos.
- **Docs:**
  - README.md:206 is stale: the M2.2 row still names ScenarioServerChangesWindow.
  - The 2912 mock exists only in `.tmp/mocks`.
  - docs/client-file-map.md and docs/architecture.md need entries for the new files.
  - `ScenarioMerge` and session-over-text need lines in docs/scenario-loading-and-generation.md.
- **Changelog:** none in 2912, since the window has no entry point until 2913.

## 3. Briefs (in order; about 100 tool calls each)
Every brief follows the hard limits: 100 lines and complexity 8 per function, 5 positional parameters, 150-character lines. View models use CommunityToolkit `[ObservableProperty]`/`[RelayCommand]` and no UI types; window code-behind does only wiring. Every brief's last step is to run its own proving commands.

The gate command, with `&lt;proj&gt;`, `&lt;Class&gt;` and `&lt;n&gt;` filled in per brief:
`pwsh tools/gate.ps1 run --cd X:/dev/yaat.wt/feat-yaatedit/yaat --log .tmp/2912-&lt;n&gt;.log --timeout 300 --slot heavy -- dotnet test &lt;proj&gt; -c Release -- --filter-class "*.&lt;Class&gt;"`

**B1. Yaat.Sim: row presence, FieldKey, order suffix, session over text**
- Files:
  - src/Yaat.Sim/Authoring/ScenarioMerge.cs
  - src/Yaat.Sim/Authoring/ScenarioEditSession.cs
  - tests/Yaat.Sim.Tests/Authoring/ScenarioMergeTests.cs
  - tests/Yaat.Sim.Tests/Authoring/ScenarioEditSessionTests.cs
  - docs/scenario-loading-and-generation.md (the ScenarioMerge and ScenarioEditSession lines)
- Steps:
  1. Add read-only `InBase`, `InMine`, `InTheirs` and `FieldKey` to `MergeRow` (not positional), set in `AddRow`.
  2. Add `public const string OrderPathSuffix` on `ScenarioMerge`, used by `MergeTree`.
  3. Add `public static ScenarioEditSession OverText(string artccId, string scenarioId, string json)` with a nullable store; `Save()` on it throws `InvalidOperationException`.
  4. Write the tests and the doc lines.
- Cases:
  - Field added in mine (three-way): AutoMine, InBase=F, InMine=T, InTheirs=F.
  - Theirs set to JSON null: AutoTheirs, InTheirs=T, Theirs==null.
  - Two-way: every row has InBase=F.
  - Aircraft added in theirs: FieldKey "[]", InMine=F.
  - /aircraft/0/startingConditions/parking → "startingConditions/parking".
  - Preset command → "presetCommands[]/command".
  - /autoDeleteMode → "autoDeleteMode".
  - /initializationTriggers/2/command → "command".
  - The /aircraft order row's path ends with `OrderPathSuffix` and has FieldKey "".
  - OverText: reads the text, starts clean, an edit makes it dirty and undoes, Save throws, malformed text throws `JsonException`.
- Proving: `&lt;proj&gt;` = tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj, once each for ScenarioMergeTests, ScenarioEditSessionTests and ScenarioDiffTests.
- At-risk:
  - ScenarioMergeTests and ScenarioEditSessionTests: keep the existing cases green unedited and add new ones.
  - ScenarioDiffTests: keep green unedited (`Entry` is unchanged).
  - Any red outside the new cases: stop and report.

**B2. View-model core: rows, choices, counts, navigation**
- Files:
  - src/Yaat.Client/ViewModels/Authoring/ScenarioMergeRequest.cs (request, texts, captions, result)
  - ScenarioMergeViewModel.cs
  - ScenarioMergeRowViewModel.cs
  - ScenarioMergeValueText.cs
  - tests/Yaat.Client.Tests/Authoring/ScenarioMergeViewModelTests.cs
- Steps:
  1. Build the plan and a row view model per row: label, the three display texts, kind, pressed side, open, added/removed wording.
  2. Keep mine / keep theirs per row into `_choices`; `OpenClashCount`; `CanApply`; summary and footer texts for three-way and two-way.
  3. Previous/Next in the D4 order, plus the tab clash counts.
  4. Show base and Only differences state per D5.
- Cases:
  - Sample with Name theirs-only, Auto-delete clash, one aircraft field auto, one preset clash: "N auto-merged · 2 clashes to resolve", Apply off.
  - Resolve both clashes: Apply on, "All differences resolved: N merged by themselves, 2 chosen."
  - ← on an auto-theirs row stores KeepMine and shows mine pressed.
  - A clash with no choice: nothing pressed.
  - Rows ordered aircraft-first in the JSON: Next walks General sections in view order, then Aircraft; no wrap.
  - Tab counts drop as clashes are chosen.
  - Two-way: "{N} differences to choose", Show base disabled with its label, no auto tags.
  - BASE strip on a clash with Show base off; on an auto row only with Show base on.
  - Absent reads "(not set)", null reads "(none)", a time field reads "8:00".
  - Order row label "Aircraft order", Edit not offered.
  - Malformed mine text: `Create` throws `JsonException`.
- Proving: `&lt;proj&gt;` = tests/Yaat.Client.Tests/Yaat.Client.Tests.csproj, `&lt;Class&gt;` = ScenarioMergeViewModelTests.
- At-risk: none outside new files. Any existing red: stop and report.

**B3. Sections, list-item and aircraft lines, bulk choices**
- Files:
  - ScenarioMergeSectionViewModel.cs
  - ScenarioMergeAircraftLineViewModel.cs
  - ScenarioMergeViewModel.Bulk.cs
  - ScenarioMergeViewModel.cs (grouping)
  - tests/Yaat.Client.Tests/Authoring/ScenarioMergeBulkTests.cs
- Steps:
  1. Section groups in view order:
     - Settings field lines over both documents
     - list-item lines (positional "#n" or by id), each with its differing field rows under it
     - order rows in their section
  2. Aircraft lines aligned in the engine's order (mine, then theirs-only), with mine and theirs cells from `ScenarioAircraftRowViewModel`, a status text, and the selected aircraft's field rows.
  3. Section and aircraft buttons: their scope (owner decision 3; build the recommended "every differing row in scope"); pressed when every row resolves to that side.
  4. The five whole-scenario commands per D3; Only differences hides identical lines.
- Cases:
  - Positional "#0" trigger choice doesn't touch strip bay "#0".
  - "Keep theirs for every clash" leaves an auto-mine row on mine.
  - "Take theirs for everything" turns every row to theirs, including the order row, and drops an edit.
  - Pressing keep-mine on FDX895 (removed in theirs) keeps it in the merged text.
  - Two-way: an aircraft on one side reads "only in mine".
  - Mine 31 aircraft and theirs 30 give aligned lines with placeholders.
  - Only differences hides the identical ATC lines.
- Proving: Client.Tests, `&lt;Class&gt;` = ScenarioMergeBulkTests, then ScenarioMergeViewModelTests.
- At-risk: ScenarioMergeViewModelTests (from B2): update only where grouping changes the row lookup, because the line model replaces the flat list; same assertions otherwise.

**B4. MERGED VALUE editors**
- Files:
  - ScenarioMergeFieldEditors.cs
  - ScenarioMergedValueViewModel.cs
  - ScenarioGeneralViewModel.cs (make `ReadPickersAsync`/`PickerData` internal)
  - ScenarioMergeRowViewModel.cs (the edit commands)
  - tests/Yaat.Client.Tests/Authoring/ScenarioMergeFieldEditorsTests.cs
  - tests/Yaat.Client.Tests/Authoring/ScenarioMergedValueTests.cs
- Steps:
  1. The kind table per D6, falling back to JSON.
  2. The editor view model: pre-fill from the chosen side or else mine; `CommitText` (on leave) sets the editor's own error line; Keep this produces the typed JSON for `MergeChoice.Edit`; Cancel.
  3. Pickers: static lists, plus the catalog and airports loaded once through `ReadPickersAsync` and the services' post, with free text when loading fails.
  4. Wire "Edit every clash".
- Cases:
  - Trigger time "8:00" keeps 480.
  - "45OO" in a number box shows "Enter a whole number." and Keep is disabled.
  - Auto-delete combo offers Default/Never/OnLanding/Parked and keeps "OnLanding".
  - Preset command that doesn't parse shows its parse error.
  - Unmapped unknown top-level field gets the JSON box; "SNALL" without quotes gives "Not valid JSON…".
  - Clash edited and kept: pressed ✎, `CanApply` follows.
  - Edit every clash opens N editors and skips order rows.
  - Config unavailable: the student position falls back to free text.
- Proving: Client.Tests, `&lt;Class&gt;` = ScenarioMergeFieldEditorsTests, ScenarioMergedValueTests, ScenarioGeneralViewModelTests.
- At-risk: ScenarioGeneralViewModelTests, ScenarioGeneralListsTests, ScenarioPickerGuardTests, ScenarioGeneratorRowsTests: keep green unedited (visibility change only). Any red: stop and report.

**B5. Apply**
- Files:
  - ScenarioMergeViewModel.Apply.cs
  - ScenarioMergeRequest.cs (add a checks/warnings record if needed)
  - tests/Yaat.Client.Tests/Authoring/ScenarioMergeApplyTests.cs
- Steps:
  1. Build the merged text: choices plus aircraft replacements.
  2. Refuse text that doesn't parse (`Read` or `CheckScenario`) with the footer reason naming the row.
  3. Checks seam, then the warnings seam, then `Write`; a `Write` that throws keeps state and shows the error.
  4. Cancel returns null with no write; Apply is disabled while an editor is open (owner decision 5).
- Cases:
  - All resolved: `Write` gets the expected text, and the result has no warnings.
  - The checks return 2 warnings: they are shown, then written, then returned.
  - JSON-box edit gives a string for spawnDelay: refused, nothing written, footer names "Spawn delay".
  - Blank name: refused.
  - `Write` throws: still open, choices kept.
  - Cancel: `Write` never called.
  - Unmodelled field survives.
- Proving: Client.Tests, `&lt;Class&gt;` = ScenarioMergeApplyTests.
- At-risk: none outside new files.

**B6. Merged-aircraft view model**
- Files:
  - ScenarioMergedAircraftViewModel.cs
  - ScenarioMergeViewModel.Apply.cs (merge-so-far helper)
  - tests/Yaat.Client.Tests/Authoring/ScenarioMergedAircraftTests.cs
- Steps:
  1. Build the merge-so-far into `ScenarioEditSession.OverText`, with `ScenarioAircraftViewModel` selecting the aircraft and a `ScenarioValidationViewModel` for its marks.
  2. Keep this: an aircraft on both sides goes to `_aircraftEdits`, its rows show ✎ pressed; an aircraft on one side goes to `MergeChoice.Edit` on the item row.
  3. A per-field choice under a replaced aircraft drops the replacement (owner decision 9).
  4. A checks helper over OverText that the window uses at Apply.
- Cases:
  - NKS2904: parking auto-mine 12, preset 2 clash pre-filled from mine; an edit merging both is kept, and Apply writes it.
  - An identical field (callsign) edited in the merged editor survives Apply.
  - N4471K added in theirs: Edit plus Keep gives `Edit(node)`.
  - Cancel changes nothing.
  - The store is never written (assert the fixture store's bytes).
- Proving: Client.Tests, `&lt;Class&gt;` = ScenarioMergedAircraftTests, ScenarioAircraftViewModelTests.
- At-risk: ScenarioAircraftViewModelTests and ScenarioAircraftDetailTests: keep green unedited.

**B7. Window and General tab**
- Files:
  - src/Yaat.Client/Views/Authoring/ScenarioMergeWindow.axaml
  - src/Yaat.Client/Views/Authoring/ScenarioMergeWindow.axaml.cs
  - tests/Yaat.Client.UI.Tests/Views/ScenarioMergeWindowTests.cs
- Steps:
  1. The window: geometry key "ScenarioMerge", `AutomationGate.ApplyShowActivated`, `ShowAsync` through `DialogPresenter`, the toolbar (summary, Previous/Next, Show base, Only differences, Whole scenario menu), the two-way note, tabs with counts, the MINE/THEIRS caption row, the footer.
  2. The General line template: gutter toggles ← ✎ → bound to the pressed state; clash, auto, added and removed classes; blue outline on the side Apply writes; BASE strip; MERGED VALUE strip through DataTemplates per editor kind.
  3. Apply/Cancel close through `DialogPresenter.Close`; the warnings box (owner decision 1).
- Cases:
  - Pressing → on a clash enables Apply once every clash is chosen.
  - Apply returns the result and calls the fake `Write`.
  - Close box returns null.
  - Two-way: Show base disabled with its label text.
  - ✎ opens the editor's TextBox; Keep commits.
- Proving: `&lt;proj&gt;` = tests/Yaat.Client.UI.Tests/Yaat.Client.UI.Tests.csproj, `&lt;Class&gt;` = ScenarioMergeWindowTests, DialogPresenterTests.
- At-risk: DialogPresenterTests: keep green unedited.

**B8. Aircraft tab, merged-aircraft host, docs, full suite**
- Files:
  - ScenarioMergeWindow.axaml
  - ScenarioMergeWindow.axaml.cs
  - src/Yaat.Client/Views/Authoring/ScenarioAircraftView.axaml
  - src/Yaat.Client/Views/Authoring/ScenarioAircraftView.axaml.cs (detail-only property)
  - tests/Yaat.Client.UI.Tests/Views/ScenarioMergeWindowTests.cs
  - docs: client-file-map.md, architecture.md (Task Index row), plans/yaatedit/README.md (M2.2 row: ScenarioMergeWindow and ScenarioMergeViewModel), and a copy of .tmp/mocks/yaat-2912-merged-value/index.html to docs/plans/canvases/yaat-2912-merged-value/index.html. Docs carry no counts or line numbers.
- Steps:
  1. Aircraft grid lines and the selected detail rows.
  2. Host the MERGED AIRCRAFT strip with the aircraft view in detail-only mode, with Keep this and Cancel.
  3. Docs.
  4. Final run: `pwsh tools/test-all.ps1` bare. Yaat.Sim changed in B1, so `dotnet test yaat.slnx` is not enough.
- Cases:
  - Selecting NKS2904 shows its detail with the BASE strip on the preset clash.
  - Next from the last General clash jumps to the Aircraft tab.
  - ✎ on an aircraft opens the merged detail, and Keep this updates the line.
  - Detail-only mode hides the toolbar and grid.
- Proving: UI.Tests, `&lt;Class&gt;` = ScenarioMergeWindowTests and ScenarioEditorWindowTests, then test-all.
- At-risk: ScenarioEditorWindowTests, ScenarioEditorTestInRoomTests and ParkingPickerWindowTests: keep green unedited (the new property defaults off). Any red in test-all outside the 2912 files: stop and report.

## 4. Decisions the owner must still make
1. **Warnings at Apply:** recommended is one message box (OK only) listing the findings ("The merged scenario has N problems; it is applied and the editor marks them"), then the write.
2. **The order row:** recommended label "{list} order" ("Aircraft order", "Preset commands order"), each pane cell listing the items in that side's order (callsigns, command text), and no ✎.
3. **Section and aircraft buttons:** do they cover every differing row in scope, auto rows included (recommended, so an aircraft with only auto rows has live buttons), or clashes only as the engine's choose-section does?
4. **"Edit every clash in place":** recommended is to open an editor pre-filled from mine on each clash, except order rows and one-sided aircraft, each committed by its own Keep this.
5. **Apply while a MERGED VALUE editor is open:** recommended is to disable Apply until the editor is kept or cancelled.
6. **Previous/Next:** recommended is to walk every clash, resolved or not, without wrapping, counting all clashes.
7. **Collapsing runs of list items** ("+ 11 more SQALL, every 4:00 to 48:00"), which the mock draws: recommended is no collapse in 2912 and to let Only differences hide identical items.
8. **Absent vs none:** recommended is "(not set)" for an absent value and "(none)" for null, with added and removed tints on single fields.
9. **A per-field choice under an aircraft that has a merged-detail edit:** recommended is that it drops that aircraft's edit.
10. **Panes drawn to match the editor's look rather than reusing its live views:** this departs from the accepted note "the panes reuse the editor's own General tab and aircraft grid and editor views"; only the merged-aircraft editor is the live view.

Captions such as "working copy · edited today 14:12" are filled per entry point by YAAT-2913, not by 2912.

### Critical Files for Implementation
- X:/dev/yaat.wt/feat-yaatedit/yaat/src/Yaat.Sim/Authoring/ScenarioMerge.cs
- X:/dev/yaat.wt/feat-yaatedit/yaat/src/Yaat.Sim/Authoring/ScenarioEditSession.cs
- X:/dev/yaat.wt/feat-yaatedit/yaat/src/Yaat.Client/ViewModels/Authoring/ScenarioFieldText.cs
- X:/dev/yaat.wt/feat-yaatedit/yaat/src/Yaat.Client/ViewModels/Authoring/ScenarioGeneralViewModel.cs
- X:/dev/yaat.wt/feat-yaatedit/yaat/src/Yaat.Client/Views/Authoring/ImportConflictDialog.axaml.cs
