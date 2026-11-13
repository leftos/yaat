using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Tdls.Services;
using Yaat.Client.Tdls.ViewModels;
using Yaat.Client.Tdls.Views.VTdls;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VTdls;

namespace Yaat.Client.UI.Tests.Views;

// View-layer coverage for the shared in-view Find (Ctrl+F) wired into VTdlsView.
// The find core itself is unit-tested in tests/Yaat.Client.Tests/Find; these prove
// the vTDLS key wiring, snapshot, and highlight bindings survive a real layout pass.
public class VTdlsViewInteractionTests
{
    [AvaloniaFact]
    public void Find_CtrlFOpens_TypingHighlightsMatches_EscClosesAndClears()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(
            new TdlsStateDto(
                [Item("id1", "UAL111", facilityId: "IAD"), Item("id2", "AAL222", facilityId: "IAD"), Item("id3", "UAL333", facilityId: "IAD")],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        Assert.Equal(3, vm.DclItems.Count);

        view.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.F,
                KeyModifiers = KeyModifiers.Control,
            }
        );
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.FindController.IsVisible);

        view.FindController.Query = "UAL";
        Dispatcher.UIThread.RunJobs();

        var byCallsign = vm.DclItems.ToDictionary(i => i.AircraftId);
        Assert.True(byCallsign["UAL111"].IsFindMatch);
        Assert.True(byCallsign["UAL333"].IsFindMatch);
        Assert.False(byCallsign["AAL222"].IsFindMatch);
        Assert.Equal("1/2", view.FindController.MatchSummary);

        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindController.IsVisible);
        Assert.False(byCallsign["UAL111"].IsFindMatch);
    }

    [AvaloniaFact]
    public void Find_MatchesFlightPlanText_NotJustCallsign()
    {
        // "All visible text": a query hitting the filed route/destination finds the item.
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        var fp = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: 1234,
            Departure: "KSFO",
            Destination: "KLAX",
            Route: "SSTIK2",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "123",
            CruiseAltitude: 35000
        );
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", fp, facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        view.FindController.Open();
        view.FindController.Query = "klax";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1/1", view.FindController.MatchSummary);
        Assert.True(vm.DclItems.Single().IsFindMatch);
    }

    [AvaloniaFact]
    public void FlightPlanHeaderAndListText_IsSelectable()
    {
        // Upstream vTDLS is a web page, so a controller can drag-select the route or the
        // remarks and copy them out. Plain TextBlocks cannot be selected at all; the
        // read-only value text therefore has to render as SelectableTextBlock.
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        var fp = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: 1234,
            Departure: "KSFO",
            Destination: "KLAX",
            Route: "SSTIK2",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "CTC NORCAL",
            Cid: "123",
            CruiseAltitude: 35000
        );
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", fp, facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        // Selecting the DCL item opens the flight-plan editor, so the header fields render.
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selectable = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.Contains(selectable, t => t.Text == "UAL111");
        Assert.Contains(selectable, t => t.Text == "RMK: CTC NORCAL");
        SelectableTextBlock route = Assert.Single(selectable, t => t.Text == "KSFO.SSTIK2.KLAX");

        // "KSFO.SSTIK2.KLAX" — chars 5..10 are the filed route itself.
        route.SelectionStart = 5;
        route.SelectionEnd = 11;
        Assert.Equal("SSTIK2", route.SelectedText);
    }

    [AvaloniaFact]
    public void FooterStatus_TracksEditorImmediately_WithoutWaitingForTheClockTick()
    {
        // The footer used to be repainted only by the 1 Hz Zulu-clock timer, so it lagged
        // the dropdowns by up to a second. No timer fires inside this test's lifetime —
        // every assertion below therefore proves the footer is binding-driven.
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        // Nothing selected — the editor is closed, so the footer shows the idle status.
        Assert.Equal("CLEARANCE TYPE: PDC", view.FooterStatusText);
        Assert.False(view.FooterStatusIsWarning);

        // Selecting a Pending item opens the editor with the mandatory Dep Freq unset.
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Departure frequency", view.FooterStatusText);
        Assert.True(view.FooterStatusIsWarning);

        // Filling it must flip the footer on the spot, not on the next tick.
        vm.Editor!.SelectedDepFreq = vm.Editor.DepFreqs[1];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("CLEARANCE TYPE: PDC", view.FooterStatusText);
        Assert.False(view.FooterStatusIsWarning);
    }

    [AvaloniaFact]
    public void FooterStatus_DropsAFieldFromTheList_AsEachOneIsFilled()
    {
        // Two mandatory fields blank: filling one leaves CanSend false, so the guard the
        // footer binding hangs off cannot be CanSend alone — the list of names has to be
        // re-raised on every recompute or the footer keeps naming a field already filled.
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq() with { MandatoryInitialAlt = true });
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Maintain, Departure frequency", view.FooterStatusText);

        vm.Editor!.SelectedInitialAlt = vm.Editor.InitialAlts[1];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Departure frequency", view.FooterStatusText);
    }

    /// <summary>
    /// Climb via and Maintain are alternatives (7110.65 §4-3-2), and upstream keeps both fields selectable: picking a
    /// climb-via empties the rendered Maintain dropdown and leaves it enabled, so the controller can pick a Maintain
    /// back over the climb-via.
    /// </summary>
    [AvaloniaFact]
    public void ClimbViaSelection_LeavesTheMaintainDropdownEnabled()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithClimbVias());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        ComboBox maintain = DropdownRowCombos(view)["Maintain"];
        vm.Editor!.SelectedInitialAlt = vm.Editor.InitialAlts[1];
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(maintain.SelectedItem);

        vm.Editor.SelectedClimbvia = vm.Editor.Climbvias.Single(v => v.Value == "CLIMB VIA SID");
        Dispatcher.UIThread.RunJobs();

        Assert.True(maintain.IsEnabled);
        Assert.True(maintain.IsEffectivelyEnabled);
        Assert.Null(maintain.SelectedItem);
        Assert.Null(vm.Editor.SelectedInitialAlt);
    }

    /// <summary>
    /// An amendment that lands while the controller is composing a clearance updates the header in place. Re-inserting
    /// the item's view-model would null the two-way bound ListBox selection, which closes the editor and discards every
    /// dropdown already chosen — so the same editor instance has to survive the push and show the new route.
    /// </summary>
    [AvaloniaFact]
    public void AmendedFlightPlan_RefreshesTheOpenEditorInPlace()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", FlightPlanWithRoute("SSTIK2"), facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        TdlsClearanceValueDto composed = editor.DepFreqs[1];
        editor.SelectedDepFreq = composed;
        Dispatcher.UIThread.RunJobs();

        transport.PushItem(Item("id1", "UAL1742", FlightPlanWithRoute("RNLDI4 OTTTO"), facilityId: "IAD"));
        Dispatcher.UIThread.RunJobs();

        // Same item VM, same selection, same editor — and the clearance composed so far is untouched.
        Assert.Same(editor, vm.Editor);
        Assert.Same(vm.DclItems.Single(), vm.SelectedItem);
        Assert.Same(composed, vm.Editor!.SelectedDepFreq);

        // The header follows the amendment.
        Assert.Equal("RNLDI4 OTTTO", vm.Editor.FlightPlan!.Route);
        Assert.Contains("RNLDI4 OTTTO", vm.Editor.FlightPlan.RouteDisplay);
    }

    /// <summary>
    /// An amendment that changes the filed route re-derives the selections the route owns. The reported shape: UAL300
    /// amended from GAPP7 to GUNNR7 showed the new route in the header while the SID dropdown still read GAPP7, so the
    /// clearance about to be sent named a SID the aircraft is no longer filed on.
    /// </summary>
    [AvaloniaFact]
    public void AmendedRoute_ReseedsTheSidAndTransition()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL300", FlightPlanWithRoute("GAPP7 BOOKE J80 BOS"), facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        Assert.Equal("GAPP7", editor.SelectedSid?.Name);
        Assert.Equal("GAPP7-BOOKE", editor.SelectedTransition?.Id);

        transport.PushItem(Item("id1", "UAL300", FlightPlanWithRoute("GUNNR7 SPACY J80 BOS"), facilityId: "IAD"));
        Dispatcher.UIThread.RunJobs();

        // Same editor instance — the amendment must not close what the controller is composing.
        Assert.Same(editor, vm.Editor);
        Assert.Equal("GUNNR7 SPACY J80 BOS", editor.FlightPlan!.Route);
        Assert.Equal("GUNNR7", editor.SelectedSid?.Name);
        Assert.Equal("GUNNR7-SPACY", editor.SelectedTransition?.Id);

        // The new pairing's defaults come with it, rather than leaving the old SID's frequency under the new route.
        Assert.Equal("126.650", editor.DepFreq);
        Assert.Equal("20 MIN AFT DP", editor.Expect);
    }

    /// <summary>
    /// The other half of the same push: an amendment that leaves the route alone (here a remarks edit) derives nothing,
    /// so every selection stays exactly as the controller left it — including a SID they picked over the filed one.
    /// </summary>
    [AvaloniaFact]
    public void AmendedRemarksWithoutRouteChange_LeaveTheSelectionsAlone()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        TdlsFlightPlanInfoDto filed = FlightPlanWithRoute("GAPP7 BOOKE J80 BOS");
        transport.PushState(new TdlsStateDto([Item("id1", "UAL300", filed, facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;

        // The controller overrides the route-derived SID and the transition's defaulted Expect.
        editor.SelectedSid = editor.Sids.Single(s => s.Name == "GUNNR7");
        editor.Expect = "10 MIN AFT DP";
        Dispatcher.UIThread.RunJobs();

        transport.PushItem(Item("id1", "UAL300", filed with { Remarks = "CTC NORCAL" }, facilityId: "IAD"));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(editor, vm.Editor);
        Assert.Equal("CTC NORCAL", editor.FlightPlan!.Remarks);
        Assert.Equal("GUNNR7", editor.SelectedSid?.Name);
        Assert.Equal("10 MIN AFT DP", editor.Expect);
    }

    /// <summary>
    /// The editor's dropdown row is star-sized, so a narrow window squeezes the always-populated fields to their text
    /// width — the reported screenshot had SID and the transition pressed to nothing beside the wide climb-via field.
    /// The floor has to sit on the column definitions: a star-sized column does not widen for its child's MinWidth, so
    /// a minimum on the ComboBox itself makes the control overflow its column and lie across the field beside it.
    /// </summary>
    [AvaloniaFact]
    public void NarrowWindow_DropdownRowFieldsKeepTheirWidthWithoutOverlapping()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL300", FlightPlanWithRoute("GAPP7 BOOKE J80 BOS"), facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootViewAtWidth(vm, windowWidth: 700);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Dictionary<string, ComboBox> row = DropdownRowCombos(view);
        AssertAtLeastWide(row, "SID", 96);
        AssertAtLeastWide(row, "Transition", 96);
        AssertAtLeastWide(row, "Maintain", 100);

        // Same coordinate space (one Grid, five children), so a field running into the next one shows up as an
        // arranged rect that reaches past its neighbour's left edge.
        var ordered = row.OrderBy(f => f.Value.Bounds.Left).ToList();
        for (int i = 0; (i + 1) < ordered.Count; i++)
        {
            (string? name, ComboBox? box) = (ordered[i].Key, ordered[i].Value);
            (string? nextName, ComboBox? nextBox) = (ordered[i + 1].Key, ordered[i + 1].Value);
            Assert.True(
                box.Bounds.Right <= nextBox.Bounds.Left,
                $"{name} ends at {box.Bounds.Right} and overlaps {nextName}, which starts at {nextBox.Bounds.Left}"
            );
        }
    }

    private static void AssertAtLeastWide(IReadOnlyDictionary<string, ComboBox> row, string field, double minWidth) =>
        Assert.True(row[field].Bounds.Width >= minWidth, $"{field} rendered {row[field].Bounds.Width}px wide, below its {minWidth}px floor");

    /// <summary>
    /// Picking "- - - -" through the rendered dropdowns, the way a controller does, empties the field in the view-model
    /// and in the control. On the SID it empties the SID and the transition and leaves the seven fields alone, and the
    /// next item's editor still opens on its defaults.
    /// </summary>
    [AvaloniaFact]
    public void ClearEntry_PickedThroughTheRenderedDropdowns_EmptiesTheField()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL300", facilityId: "IAD"), Item("id2", "UAL301", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        vm.SelectedItem = vm.DclItems.Single(i => i.AircraftId == "UAL300");
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        Assert.Equal("125.050", editor.DepFreq);

        ComboBox depFreq = EditorCombo(view, "Departure frequency");
        depFreq.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(editor.SelectedDepFreq);
        Assert.Null(depFreq.SelectedItem);

        ComboBox sid = DropdownRowCombos(view)["SID"];
        sid.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(editor.SelectedSid);
        Assert.Null(editor.SelectedTransition);
        Assert.Null(sid.SelectedItem);
        Assert.Equal("10 MIN AFT DP", editor.Expect);

        vm.SelectedItem = vm.DclItems.Single(i => i.AircraftId == "UAL301");
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(editor, vm.Editor);
        Assert.Equal("GAPP7", vm.Editor!.SelectedSid?.Name);
        Assert.Equal("10 MIN AFT DP", vm.Editor.Expect);
        Assert.Equal("125.050", vm.Editor.DepFreq);
    }

    /// <summary>The editor dropdown whose tooltip names <paramref name="field"/>.</summary>
    private static ComboBox EditorCombo(VTdlsView view, string field) =>
        view.GetVisualDescendants().OfType<ComboBox>().Single(c => string.Equals(ToolTip.GetTip(c) as string, field, StringComparison.Ordinal));

    /// <summary>The five dropdowns of the editor's SID row, keyed by the tooltip naming each field.</summary>
    private static Dictionary<string, ComboBox> DropdownRowCombos(VTdlsView view)
    {
        string[] fields = ["SID", "Transition", "Climb out", "Climb via", "Maintain"];
        var found = view.GetVisualDescendants()
            .OfType<ComboBox>()
            .Select(c => (Field: ToolTip.GetTip(c) as string, Box: c))
            .Where(c => (c.Field is not null) && fields.Contains(c.Field))
            .ToDictionary(c => c.Field!, c => c.Box);
        Assert.Equal(fields.Length, found.Count);
        return found;
    }

    /// <summary>
    /// Re-applying the facility page (a reconnect or a scenario reload re-fetches it) clears the lists while keeping the
    /// identities, and the full-state re-push that follows is what has to put them back: nothing about those items
    /// moved between or within the lists, they are simply no longer in either one.
    /// </summary>
    [AvaloniaFact]
    public void ReapplyingTheFacilityPage_RelistsTheItems()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq() with { FacilityId = "SFO", FacilityName = "San Francisco Intl ATCT" });
        var state = new TdlsStateDto(
            [
                Item("id1", "UAL1742", facilityId: "SFO"),
                Item("id2", "SWA200", facilityId: "SFO"),
                Item("id3", "AAL9", facilityId: "SFO", status: TdlsStatus.Sent),
            ],
            []
        );

        transport.PushState(state);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.DclItems.Count);
        Assert.Single(vm.PdcItems);
        TdlsItemViewModel pendingFirst = vm.DclItems[0];
        TdlsItemViewModel pendingSecond = vm.DclItems[1];
        TdlsItemViewModel sent = vm.PdcItems[0];

        // Applying the facility page empties both lists — that is the clearing contract.
        SeedFacility(vm, ConfigWithMandatoryDepFreq() with { FacilityId = "SFO", FacilityName = "San Francisco Intl ATCT" });
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.DclItems);
        Assert.Empty(vm.PdcItems);

        // The answer to RequestFullTdlsState: same items, same instances, back on their lists.
        transport.PushState(state);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.DclItems.Count);
        Assert.Single(vm.PdcItems);
        Assert.Same(pendingFirst, vm.DclItems[0]);
        Assert.Same(pendingSecond, vm.DclItems[1]);
        Assert.Same(sent, vm.PdcItems[0]);
    }

    [AvaloniaFact]
    public void OpsConfig_SwitchingActiveConfig_ReplacesTheSidListAndClosesTheEditor()
    {
        // OAK's shape: the facility-level SID list is empty and each config carries its own
        // SIDs, with a different id for the same SID name — so an open editor's SelectedSid
        // would point at an id that no longer exists after a switch.
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, OpsConfigFacility() with { ActiveOpConfigId = "cfg-west" });
        transport.PushState(
            new TdlsStateDto([Item("id1", "UAL1742", facilityId: "OAK")], []) { ActiveOpConfigs = [new TdlsActiveOpConfigDto("OAK", "cfg-west")] }
        );
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        Assert.True(vm.AreOpConfigsEnabled);
        Assert.Equal("OAKW", vm.ActiveOpConfigName);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([TdlsFlightPlanEditorViewModel.ClearEntryId, "sid-west"], vm.Editor!.Sids.Select(s => s.Id));

        // The server broadcasting a different active config drops the editor rather than
        // leaving it holding a SID id that no longer resolves.
        transport.PushState(
            new TdlsStateDto([Item("id1", "UAL1742", facilityId: "OAK")], []) { ActiveOpConfigs = [new TdlsActiveOpConfigDto("OAK", "cfg-east")] }
        );
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("cfg-east", vm.ActiveOpConfigId);
        Assert.Equal("OAKE", vm.ActiveOpConfigName);
        Assert.Null(vm.Editor);

        // Reopening builds from the new config's SID list.
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([TdlsFlightPlanEditorViewModel.ClearEntryId, "sid-east"], vm.Editor!.Sids.Select(s => s.Id));
    }

    [AvaloniaFact]
    public async Task OpsConfig_SaveGoesToTheServer_AndIsHiddenWhereConfigsAreDisabled()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport _) = MakeVm();
        SeedFacility(vm, OpsConfigFacility());
        BootView(vm);

        SentCommands.Clear();
        Assert.True(await vm.SaveOpConfigAsync("cfg-east"));
        // Global command (empty callsign) rather than an RPC — that is what gets it into the
        // action log so a replay reproduces the configuration change.
        Assert.Equal([("", "TDLSOPS OAK cfg-east")], SentCommands);

        // A facility without ops configs hides the footer menu entirely.
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        Assert.False(vm.AreOpConfigsEnabled);
    }

    // ── Consolidated parent page (upstream's NCT-over-its-children view) ──

    [AvaloniaFact]
    public void Consolidated_ShowsEveryMemberFacilitysItems_AndNoOthers()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedConsolidated(vm);

        transport.PushState(
            new TdlsStateDto(
                [
                    Item("id1", "UAL1742", facilityId: "OAK"),
                    Item("id2", "SWA200", facilityId: "SFO"),
                    // A facility outside the consolidated set must not leak onto the page.
                    Item("id3", "AAL9", facilityId: "SMF"),
                ],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        Assert.True(vm.IsConsolidated);
        Assert.Equal(["UAL1742", "SWA200"], vm.DclItems.Select(i => i.AircraftId));
    }

    [AvaloniaFact]
    public void Consolidated_EditorAndOpsConfig_FollowTheSelectedItemsOwnFacility()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedConsolidated(vm);
        transport.PushState(
            new TdlsStateDto([Item("id1", "UAL1742", facilityId: "OAK"), Item("id2", "SWA200", facilityId: "SFO")], [])
            {
                ActiveOpConfigs = [new TdlsActiveOpConfigDto("OAK", "cfg-east")],
            }
        );
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        // Nothing selected: no single facility speaks for the page.
        Assert.Null(vm.Config);
        Assert.False(vm.AreOpConfigsEnabled);

        // Selecting an OAK item brings OAK's ops-config-driven SID list into force.
        vm.SelectedItem = vm.DclItems.Single(i => i.AircraftId == "UAL1742");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("OAK", vm.Config!.FacilityId);
        Assert.True(vm.AreOpConfigsEnabled);
        Assert.Equal("OAKE", vm.ActiveOpConfigName);
        Assert.Equal([TdlsFlightPlanEditorViewModel.ClearEntryId, "sid-east"], vm.Editor!.Sids.Select(s => s.Id));

        // Moving to the SFO item swaps the whole configuration under the editor.
        vm.SelectedItem = vm.DclItems.Single(i => i.AircraftId == "SWA200");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SFO", vm.Config!.FacilityId);
        Assert.False(vm.AreOpConfigsEnabled);
    }

    [AvaloniaFact]
    public async Task Consolidated_OpsConfigSave_TargetsTheMemberFacility_NotTheParent()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedConsolidated(vm);
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "OAK")], []));
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();

        SentCommands.Clear();
        Assert.True(await vm.SaveOpConfigAsync("cfg-east"));
        // NCT owns no TDLS configuration at all — naming the page would be rejected.
        Assert.Equal([("", "TDLSOPS OAK cfg-east")], SentCommands);
    }

    // ── An item the page cannot open ──

    /// <summary>
    /// Before a facility page is applied there is no configuration to open any item with, so nothing is listed: the
    /// full-state request that follows every page apply brings the items back.
    /// </summary>
    [AvaloniaFact]
    public void ItemPushedBeforeAnyFacilityPage_IsNotListed()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();

        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        transport.PushItem(Item("id2", "AAL9", facilityId: "IAD"));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.DclItems);
        Assert.Empty(vm.PdcItems);
    }

    /// <summary>
    /// The reported latch: a pending item whose facility has no configuration on the page highlighted, opened nothing,
    /// and a second click on the same row never re-fired. It has to say why, and let go of the selection so the next
    /// click tries again.
    /// </summary>
    [AvaloniaFact]
    public void SelectingAnItemWithNoFacilityConfig_ShowsTheErrorAndReleasesTheSelection()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport _) = MakeVm();
        // Two members, so there is no sole-member configuration to fall back on.
        SeedConsolidated(vm);
        var orphan = new TdlsItemViewModel(Item("id9", "AAL9", facilityId: "SMF"));

        vm.SelectedItem = orphan;
        Assert.Null(vm.Editor);
        Assert.Equal("TDLS configuration for SMF not found", vm.CommandError);
        Assert.Equal("TDLS configuration for SMF not found", vm.FooterStatusText);
        Assert.True(vm.IsFooterStatusWarning);

        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.SelectedItem);

        // The same row again re-fires rather than latching.
        vm.CommandError = null;
        vm.SelectedItem = orphan;
        Assert.Equal("TDLS configuration for SMF not found", vm.CommandError);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.SelectedItem);
    }

    // ── Re-bootstrapping the page ──

    /// <summary>
    /// Re-selecting the facility already shown keeps its page but still asks for the full state: after a reconnect or a
    /// scenario load that request is the only thing that puts the items back. A reload fetches both again.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingToTheSameFacility_RequestsTheStateAgain_AndReloadRefetchesThePage()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        TdlsConfigDto config = ConfigWithMandatoryDepFreq();
        transport.FacilityView = new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config], StudentFrequencyMhz: null);

        await vm.SwitchFacilityAsync("IAD");
        Dispatcher.UIThread.RunJobs();
        await vm.SwitchFacilityAsync("IAD");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, transport.FacilityViewRequests);
        Assert.Equal(2, transport.FullStateRequests);

        await vm.ReloadFacilityAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, transport.FacilityViewRequests);
        Assert.Equal(3, transport.FullStateRequests);
    }

    [AvaloniaFact]
    public async Task ReloadWithNoFacility_FetchesNothing()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();

        await vm.ReloadFacilityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, transport.FacilityViewRequests);
        Assert.Equal(0, transport.FullStateRequests);
    }

    /// <summary>
    /// A scenario changed while the page was open (or during an outage) can change the facility's configuration; a
    /// reload of the page already shown adopts the refetched one rather than keeping the old.
    /// </summary>
    [AvaloniaFact]
    public async Task ReloadOfTheSamePage_AdoptsTheRefetchedConfiguration()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        TdlsConfigDto before = ConfigWithMandatoryDepFreq();
        transport.FacilityView = new TdlsFacilityViewDto(before.FacilityId, before.FacilityName, [before], StudentFrequencyMhz: null);
        await vm.SwitchFacilityAsync("IAD");
        Dispatcher.UIThread.RunJobs();

        TdlsConfigDto after = ConfigWithTwoSids();
        transport.FacilityView = new TdlsFacilityViewDto(after.FacilityId, after.FacilityName, [after], StudentFrequencyMhz: null);
        await vm.ReloadFacilityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(after, vm.Config);
    }

    /// <summary>
    /// A page fetch still in flight when the page is cleared (the scenario unloaded meanwhile) must not land on the
    /// cleared page: the latest thing done to the page wins, so the stale facility and its configuration stay off it.
    /// </summary>
    [AvaloniaFact]
    public async Task ClearDuringAFacilityFetch_KeepsThePageCleared()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        TdlsConfigDto config = ConfigWithMandatoryDepFreq();
        var held = new TaskCompletionSource<TdlsFacilityViewDto?>();
        transport.HeldFacilityView = held;

        Task switching = vm.SwitchFacilityAsync("IAD");
        vm.Clear();
        held.SetResult(new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config], StudentFrequencyMhz: null));
        await switching;
        Dispatcher.UIThread.RunJobs();

        // Clear keeps the page identity it had, which before the fetch was none.
        Assert.Null(vm.FacilityId);
        Assert.Null(vm.Config);
        Assert.Empty(vm.DclItems);
        Assert.Empty(vm.PdcItems);
    }

    // ── Send and dump failures ──

    /// <summary>A send the server rejects shows its message in the footer, and the next accepted send clears it.</summary>
    [AvaloniaFact]
    public async Task SendRejectedByTheServer_ShowsTheErrorInTheFooter_UntilASendSucceeds()
    {
        var answer = new CommandResultDto(false, "boom");
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm(() => answer);
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        editor.SelectedDepFreq = editor.DepFreqs[1];
        Dispatcher.UIThread.RunJobs();

        await editor.SendCommand.ExecuteAsync(null);
        Assert.Equal("Error sending PDC: boom", vm.FooterStatusText);
        Assert.True(vm.IsFooterStatusWarning);

        answer = new CommandResultDto(true, null);
        await editor.SendCommand.ExecuteAsync(null);
        Assert.Null(vm.CommandError);
        Assert.Equal("CLEARANCE TYPE: PDC", vm.FooterStatusText);
    }

    /// <summary>A dump that never reached the server says so rather than failing silently.</summary>
    [AvaloniaFact]
    public async Task DumpWithNoResponse_ShowsTheErrorInTheFooter()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm(() => null);
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();

        await vm.DumpSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Error dumping flight plan: no response from server", vm.FooterStatusText);
        Assert.True(vm.IsFooterStatusWarning);
    }

    /// <summary>While disconnected a dump goes nowhere, so none is attempted.</summary>
    [AvaloniaFact]
    public async Task DumpWhileDisconnected_SendsNothing()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        TdlsItemViewModel item = vm.DclItems.Single();

        // Losing the connection empties the page; re-selecting the item the controller still holds is what a dump
        // fired from a stale view would act on.
        vm.SetConnected(false);
        vm.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        SentCommands.Clear();

        await vm.DumpSelectedCommand.ExecuteAsync(null);

        Assert.Empty(SentCommands);
    }

    /// <summary>While disconnected a send goes nowhere either, even with every mandatory field filled.</summary>
    [AvaloniaFact]
    public async Task SendWhileDisconnected_SendsNothing()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        TdlsItemViewModel item = vm.DclItems.Single();

        vm.SetConnected(false);
        vm.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        editor.SelectedDepFreq = editor.DepFreqs[1];
        Dispatcher.UIThread.RunJobs();
        SentCommands.Clear();

        await editor.SendCommand.ExecuteAsync(null);

        Assert.Empty(SentCommands);
    }

    /// <summary>Disconnected, the editor's Send and Dump buttons cannot be pressed, even on a clearance ready to send.</summary>
    [AvaloniaFact]
    public void SendAndDumpButtons_AreDisabledWhileDisconnected()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        TdlsItemViewModel item = vm.DclItems.Single();
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);

        vm.SetConnected(false);
        vm.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        vm.Editor!.SelectedDepFreq = vm.Editor.DepFreqs[1];
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.Editor.IsSendEnabled);
        Assert.False(EditorButton(view, "Send").IsEffectivelyEnabled);
        Assert.False(EditorButton(view, "Dump").IsEffectivelyEnabled);
    }

    /// <summary>An editor that throws while it is built says so in the footer and releases the row, rather than latching.</summary>
    [AvaloniaFact]
    public void EditorThatFailsToBuild_ShowsTheErrorAndReleasesTheSelection()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        // A configuration with no SID list at all is malformed enough to make the editor throw.
        SeedFacility(vm, ConfigWithMandatoryDepFreq() with { Sids = null! });
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();

        vm.SelectedItem = vm.DclItems.Single();

        Assert.Null(vm.Editor);
        Assert.StartsWith("Error opening UAL1742: ", vm.CommandError);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.SelectedItem);
    }

    /// <summary>A sent PDC that carries no clearance has nothing to review: it says so rather than highlighting an empty row.</summary>
    [AvaloniaFact]
    public void SentItemWithoutAClearance_ShowsTheErrorAndReleasesTheSelection()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent)], []));
        Dispatcher.UIThread.RunJobs();

        vm.SelectedItem = vm.PdcItems.Single();

        Assert.Null(vm.Editor);
        Assert.Equal("Error opening AAL9: no sent clearance", vm.CommandError);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.SelectedItem);
    }

    [AvaloniaFact]
    public void Clear_ClearsTheShownError()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport _) = MakeVm();
        vm.CommandError = "Error sending PDC: boom";

        vm.Clear();

        Assert.Null(vm.CommandError);
    }

    [AvaloniaFact]
    public void ApplyingAFacilityPage_ClearsTheShownError()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport _) = MakeVm();
        vm.CommandError = "Error sending PDC: boom";

        SeedFacility(vm, ConfigWithMandatoryDepFreq());

        Assert.Null(vm.CommandError);
    }

    // ── Dump on a sent PDC ──

    /// <summary>Upstream hides Dump once the item already carries a clearance; a pending item keeps it.</summary>
    [AvaloniaFact]
    public void DumpButton_IsHiddenOnASentPdc_AndShownOnAPendingItem()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        var issued = new ClearanceDto("10 MIN AFT DP", "RNLDI4", "OTTTO", null, null, null, null, null, "125.050");
        transport.PushState(
            new TdlsStateDto(
                [
                    Item("id1", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent) with
                    {
                        SentPayload = issued,
                    },
                    Item("id2", "UAL1742", facilityId: "IAD"),
                ],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);

        vm.SelectedItem = vm.PdcItems.Single();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Editor!.IsReadOnly);
        Assert.False(EditorButton(view, "Dump").IsVisible);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(EditorButton(view, "Dump").IsVisible);
    }

    // ── Selection: one row per list ──

    /// <summary>
    /// While the read-only review of a sent PDC is open, a click on a pending row is ignored so a stray click cannot
    /// throw the review away. Cancel (F10) frees the selection, and the next click opens the pending editor.
    /// </summary>
    [AvaloniaFact]
    public void ClickingAPendingItemWhileReviewingASentPdc_IsIgnoredUntilCancel()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        var issued = new ClearanceDto("10 MIN AFT DP", "RNLDI4", "OTTTO", null, null, null, null, null, "125.050");
        transport.PushState(
            new TdlsStateDto(
                [
                    Item("id1", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent) with
                    {
                        SentPayload = issued,
                    },
                    Item("id2", "UAL1742", facilityId: "IAD"),
                ],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        // Review the sent PDC: the read-only editor opens on it.
        ClickRow(view, "PdcListBox", "AAL9");
        Assert.Equal("AAL9", vm.Editor?.Callsign);
        TdlsItemViewModel sent = vm.PdcItems.Single();
        TdlsFlightPlanEditorViewModel review = vm.Editor!;

        // A click on the pending row is ignored while the review is open.
        ClickRow(view, "DclListBox", "UAL1742");
        Assert.Same(sent, vm.SelectedItem);
        Assert.Same(review, vm.Editor);
        Assert.Null(vm.SelectedPendingItem);

        PressCancel(view);

        // Cancel let go of the selection, so the same click now opens the pending item's editor.
        ClickRow(view, "DclListBox", "UAL1742");
        Assert.Same(vm.DclItems.Single(), vm.SelectedItem);
        Assert.Equal("UAL1742", vm.Editor?.Callsign);
        Assert.False(vm.Editor!.IsReadOnly);
    }

    /// <summary>
    /// The reverse direction: a pending clearance is open for composing and a click on a sent PDC row is ignored, so
    /// the composed clearance is not thrown away. Cancel (F10) frees the selection and the next click opens the review.
    /// </summary>
    [AvaloniaFact]
    public void ClickingASentPdcWhileAPendingItemIsOpen_IsIgnoredUntilCancel()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        var issued = new ClearanceDto("10 MIN AFT DP", "RNLDI4", "OTTTO", null, null, null, null, null, "125.050");
        transport.PushState(
            new TdlsStateDto(
                [
                    Item("id1", "UAL1742", facilityId: "IAD"),
                    Item("id2", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent) with
                    {
                        SentPayload = issued,
                    },
                ],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();
        // Tall enough that the editor open above leaves the star-sized PDC row a clickable height.
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);

        ClickRow(view, "DclListBox", "UAL1742");
        Assert.Equal("UAL1742", vm.Editor?.Callsign);
        TdlsItemViewModel pending = vm.DclItems.Single();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;

        ClickRow(view, "PdcListBox", "AAL9");
        Assert.Same(pending, vm.SelectedItem);
        Assert.Same(editor, vm.Editor);
        Assert.Null(vm.SelectedSentItem);

        PressCancel(view);

        ClickRow(view, "PdcListBox", "AAL9");
        Assert.Same(vm.PdcItems.Single(), vm.SelectedItem);
        Assert.Equal("AAL9", vm.Editor?.Callsign);
        Assert.True(vm.Editor!.IsReadOnly);
    }

    // ── Row picks are ignored while an editor is open ──

    /// <summary>A pick on another pending row is a no-op while the editable editor is open.</summary>
    [AvaloniaFact]
    public void RowClick_WhileDclEditorOpen_IsIgnored()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD"), Item("id2", "AAL222", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();

        TdlsItemViewModel a = vm.DclItems.Single(i => i.AircraftId == "UAL111");
        TdlsItemViewModel b = vm.DclItems.Single(i => i.AircraftId == "AAL222");
        vm.SelectedItem = a;
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        Assert.NotNull(editor);

        vm.SelectedPendingItem = b;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(a, vm.SelectedItem);
        Assert.Same(editor, vm.Editor);
        Assert.Same(a, vm.SelectedPendingItem);
    }

    /// <summary>A pick in the sent list is a no-op while the pending item's editor is open.</summary>
    [AvaloniaFact]
    public void SentRowClick_WhileDclEditorOpen_IsIgnored()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        var issued = new ClearanceDto("10 MIN AFT DP", "RNLDI4", "OTTTO", null, null, null, null, null, "125.050");
        transport.PushState(
            new TdlsStateDto(
                [
                    Item("id1", "UAL111", facilityId: "IAD"),
                    Item("id2", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent) with
                    {
                        SentPayload = issued,
                    },
                ],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();

        TdlsItemViewModel a = vm.DclItems.Single();
        vm.SelectedItem = a;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Editor);

        vm.SelectedSentItem = vm.PdcItems.Single();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(a, vm.SelectedItem);
        Assert.Same(a, vm.SelectedPendingItem);
        Assert.Null(vm.SelectedSentItem);
    }

    /// <summary>A pick on a pending row is a no-op while the sent PDC's read-only review is open.</summary>
    [AvaloniaFact]
    public void PendingRowClick_WhileSentReviewOpen_IsIgnored()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        var issued = new ClearanceDto("10 MIN AFT DP", "RNLDI4", "OTTTO", null, null, null, null, null, "125.050");
        transport.PushState(
            new TdlsStateDto(
                [
                    Item("id1", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent) with
                    {
                        SentPayload = issued,
                    },
                    Item("id2", "UAL111", facilityId: "IAD"),
                ],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();

        TdlsItemViewModel s = vm.PdcItems.Single();
        vm.SelectedItem = s;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Editor!.IsReadOnly);

        vm.SelectedPendingItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(s, vm.SelectedItem);
        Assert.Null(vm.SelectedPendingItem);
    }

    /// <summary>Clearing a list's own selection is ignored too while the editor is open, so its row stays highlighted.</summary>
    [AvaloniaFact]
    public void ListDeselect_WhileEditorOpen_KeepsTheSelection()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();

        TdlsItemViewModel a = vm.DclItems.Single();
        vm.SelectedItem = a;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Editor);

        vm.SelectedPendingItem = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(a, vm.SelectedItem);
        Assert.Same(a, vm.SelectedPendingItem);
    }

    /// <summary>With no editor open, a row pick selects and opens the clicked row again.</summary>
    [AvaloniaFact]
    public void RowClick_AfterCancel_OpensTheNewRow()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD"), Item("id2", "AAL222", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();

        vm.SelectedItem = vm.DclItems.Single(i => i.AircraftId == "UAL111");
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Editor);

        // What Cancel (F10) does: clear the selection, which closes the editor.
        vm.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Editor);

        TdlsItemViewModel b = vm.DclItems.Single(i => i.AircraftId == "AAL222");
        vm.SelectedPendingItem = b;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, vm.SelectedItem);
        Assert.Equal("AAL222", vm.Editor?.Callsign);
    }

    /// <summary>
    /// A send moves the pending item into the sent list. The editor closes because the move clears the selection
    /// explicitly — a bound list dropping its row no longer nulls a shared selection, so nothing else would.
    /// </summary>
    [AvaloniaFact]
    public void SendingTheSelectedPendingItem_ClosesItsEditor()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Editor);

        var issued = new ClearanceDto("10 MIN AFT DP", "RNLDI4", "OTTTO", null, null, null, null, null, "125.050");
        transport.PushItem(Item("id1", "UAL1742", facilityId: "IAD") with { Status = TdlsStatus.Sent, SentPayload = issued });
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.Editor);
    }

    /// <summary>
    /// Cancel closes the editor by clearing the view-model selection. Each list now holds its own selection, so that
    /// clear has to reach both lists: a row left highlighted with no editor is exactly the stuck page the report
    /// describes.
    /// </summary>
    [AvaloniaFact]
    public void CancelAfterClickingARow_ClearsTheHighlight()
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(
            new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD"), Item("id2", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent)], [])
        );
        Dispatcher.UIThread.RunJobs();
        // Tall enough that the editor's button row sits inside the window and takes a pointer click.
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);

        ClickRow(view, "DclListBox", "UAL1742");
        Assert.Equal("UAL1742", vm.Editor?.Callsign);

        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        ClickControl(UiWindow(view), EditorButton(view, "Cancel"));
        Assert.Null(vm.Editor);

        Assert.Null(view.FindControl<ListBox>("DclListBox")!.SelectedItem);
        Assert.Null(view.FindControl<ListBox>("PdcListBox")!.SelectedItem);
        Assert.False(vm.DclItems.Single().IsSelected);
    }

    // ── Theme: the view renders the same on every host ──

    /// <summary>
    /// The view carries its own light and dark palettes, so the Fluent controls inside it have to follow the view's
    /// mode rather than the host's: the desktop app runs Fluent Dark, the web app Fluent Light, and either one left in
    /// charge paints the editor's buttons and dropdowns in the other palette's colours. The pin has to follow a
    /// runtime toggle both ways, not just the mode the view opened in.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Theme_ViewPinsItsThemeVariantToItsOwnMode(bool dark)
    {
        (VTdlsViewModel? vm, FakeTdlsTransport _) = MakeVm();
        vm.IsDarkMode = dark;
        VTdlsView view = BootView(vm);
        AssertThemeVariantFollowsMode(view, vm);

        ToggleDarkMode(view, vm);
        AssertThemeVariantFollowsMode(view, vm);

        ToggleDarkMode(view, vm);
        AssertThemeVariantFollowsMode(view, vm);
    }

    /// <summary>
    /// The reported shape (GitHub #1340): on the web host in light mode the editor's buttons and dropdowns were stock
    /// Fluent Light, near-white and, when disabled, pale grey on the white editor panel, so they could not be seen.
    /// Each control has to stand out from the panel and carry text that reads on it, enabled and disabled alike, in
    /// the mode the view opened in and after the controller toggles dark mode either way.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Theme_EditorButtonsAndDropdowns_StandOutFromThePanel_WithReadableText(bool dark)
    {
        (VTdlsViewModel? vm, FakeTdlsTransport? transport) = MakeVm();
        vm.IsDarkMode = dark;
        SeedFacility(vm, ConfigWithClimbVias());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);

        // The editor opens one mandatory field short, so Send starts disabled. The climb-via pick gives the Climb via
        // list a selected item for the open-list check to read.
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        vm.Editor!.SelectedClimbvia = vm.Editor.Climbvias.Single(v => v.Value == "CLIMB VIA SID");
        Dispatcher.UIThread.RunJobs();
        // An editable editor has no disabled dropdown, so the test disables the Maintain control itself.
        DropdownRowCombos(view)["Maintain"].IsEnabled = false;
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Color opened = AssertEditorPalette(view, vm);

        ToggleDarkMode(view, vm);
        Color toggled = AssertEditorPalette(view, vm);
        Assert.NotEqual(opened, toggled);

        ToggleDarkMode(view, vm);
        Assert.Equal(opened, AssertEditorPalette(view, vm));

        vm.Editor.SelectedDepFreq = vm.Editor.DepFreqs[1];
        Dispatcher.UIThread.RunJobs();
        Button send = EditorButton(view, "Send");
        Assert.True(send.IsEffectivelyEnabled);
        AssertStandsOutAndReads("Send", ButtonColours(send), opened, enabled: true);
    }

    private static void ToggleDarkMode(VTdlsView view, VTdlsViewModel vm)
    {
        vm.IsDarkMode = !vm.IsDarkMode;
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertThemeVariantFollowsMode(VTdlsView view, VTdlsViewModel vm) =>
        Assert.Equal(vm.IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light, view.ActualThemeVariant);

    /// <summary>
    /// Checks the editor against the palette of the view's current mode: the theme variant, an enabled and a disabled
    /// button, an enabled and a disabled dropdown, and the items of an open drop-down list. Returns the panel colour.
    /// </summary>
    private static Color AssertEditorPalette(VTdlsView view, VTdlsViewModel vm)
    {
        AssertThemeVariantFollowsMode(view, vm);
        Color panel = SolidColour(view.Resources["VTdlsListBg"] as IBrush, "the editor panel");

        Button dump = EditorButton(view, "Dump");
        Assert.True(dump.IsEffectivelyEnabled);
        AssertStandsOutAndReads("Dump", ButtonColours(dump), panel, enabled: true);

        Button send = EditorButton(view, "Send");
        Assert.False(send.IsEffectivelyEnabled);
        AssertStandsOutAndReads("Send (disabled)", ButtonColours(send), panel, enabled: false);

        Dictionary<string, ComboBox> row = DropdownRowCombos(view);
        Assert.True(row["SID"].IsEffectivelyEnabled);
        AssertStandsOutAndReads("SID", ComboBoxColours(row["SID"]), panel, enabled: true);
        Assert.False(row["Maintain"].IsEffectivelyEnabled);
        AssertStandsOutAndReads("Maintain (disabled)", ComboBoxColours(row["Maintain"]), panel, enabled: false);

        AssertOpenListReads(row["Climb via"]);
        return panel;
    }

    /// <summary>Opens the dropdown and checks the selected item and an unselected one read on the list behind them.</summary>
    private static void AssertOpenListReads(ComboBox box)
    {
        string field = ToolTip.GetTip(box) as string ?? "dropdown";
        box.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        try
        {
            Popup popup = box.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup");
            Border list = Assert.IsType<Border>(popup.Child);
            Color listBackground = Over(SolidColour(list.Background, $"{field} list"), 1.0, Colors.Black);
            var items = box.GetRealizedContainers().OfType<ComboBoxItem>().ToList();
            Assert.Equal(box.ItemCount, items.Count);
            AssertItemReads($"{field} selected item", items.Single(i => i.IsSelected), listBackground);
            AssertItemReads($"{field} unselected item", items.First(i => !i.IsSelected), listBackground);
        }
        finally
        {
            box.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void AssertItemReads(string item, ComboBoxItem container, Color listBackground)
    {
        ContentPresenter presenter = container.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        Color background = Over(SolidColour(presenter.Background, $"{item} background"), 1.0, listBackground);
        Color text = Over(SolidColour(presenter.Foreground, $"{item} text"), 1.0, background);
        double contrast = ContrastRatio(text, background);
        Assert.True(contrast >= 4.5, $"{item} text {text} on {background} has a contrast of {contrast:F2}:1, under 4.5:1");
    }

    private static Button EditorButton(VTdlsView view, string label) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == label);

    /// <summary>A control's painted background, its border, and the brush and opacity of the text it shows.</summary>
    private readonly record struct ControlColours(IBrush? Background, IBrush? Border, IBrush? Foreground, double ForegroundOpacity);

    private static ControlColours ButtonColours(Button button)
    {
        ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        return new ControlColours(presenter.Background, presenter.BorderBrush, presenter.Foreground, 1.0);
    }

    /// <summary>The dropdown's box, and whichever text it shows: the selected value, or the placeholder when nothing is chosen.</summary>
    private static ControlColours ComboBoxColours(ComboBox box)
    {
        var parts = box.GetVisualDescendants().ToList();
        Border background = parts.OfType<Border>().First(b => b.Name == "Background");
        if (box.SelectionBoxItem is null)
        {
            TextBlock placeholder = parts.OfType<TextBlock>().First(t => t.Name == "PlaceholderTextBlock");
            return new ControlColours(background.Background, background.BorderBrush, placeholder.Foreground, placeholder.Opacity);
        }
        ContentControl selected = parts.OfType<ContentControl>().First(c => c.Name == "ContentPresenter");
        return new ControlColours(background.Background, background.BorderBrush, selected.Foreground, 1.0);
    }

    /// <summary>
    /// A control stands out when its fill is not the panel's colour and its text reads on the fill (4.5:1). An enabled
    /// control must also be outlined against the panel at the WCAG non-text minimum of 3:1, since a light fill on a
    /// white panel is too faint to mark the control alone.
    /// </summary>
    private static void AssertStandsOutAndReads(string control, ControlColours colours, Color panel, bool enabled)
    {
        Color background = Over(SolidColour(colours.Background, $"{control} background"), 1.0, panel);
        Assert.True(background != panel, $"{control} paints {background}, the editor panel's own colour, so it vanishes into the panel");

        Color text = Over(SolidColour(colours.Foreground, $"{control} text"), colours.ForegroundOpacity, background);
        double contrast = ContrastRatio(text, background);
        Assert.True(contrast >= 4.5, $"{control} text {text} on {background} has a contrast of {contrast:F2}:1, under 4.5:1");

        if (enabled)
        {
            Color border = Over(SolidColour(colours.Border, $"{control} border"), 1.0, panel);
            double outline = ContrastRatio(border, panel);
            Assert.True(outline >= 3.0, $"{control} border {border} on the panel's {panel} has a contrast of {outline:F2}:1, under 3:1");
        }
    }

    /// <summary>The colour of a solid brush with the brush's own opacity folded into its alpha.</summary>
    private static Color SolidColour(IBrush? brush, string what)
    {
        if (brush is not ISolidColorBrush solid)
        {
            Assert.Fail($"{what} is painted by {brush?.GetType().Name ?? "no brush"}, not a solid colour");
            return default;
        }
        return Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B);
    }

    /// <summary>Composites <paramref name="top"/>, further faded by <paramref name="opacity"/>, over an opaque <paramref name="under"/>.</summary>
    private static Color Over(Color top, double opacity, Color under)
    {
        double alpha = (top.A / 255.0) * opacity;
        byte Mix(byte t, byte u) => (byte)Math.Round((t * alpha) + (u * (1 - alpha)));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    /// <summary>WCAG 2 contrast ratio between two opaque colours.</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    // ── Facility Menu: Temp. Dep Freqs, Override Dep Freq ────────

    [AvaloniaFact]
    public void FacilityMenu_ShowsTheDepartureFrequencyItems_BelowDarkMode()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);

        MenuFlyout flyout = OpenFacilityMenu(view);

        var items = flyout.Items.Cast<object>().ToList();
        int darkMode = items.FindIndex(i => (i as MenuItem)?.Header as string == "Dark Mode");
        Assert.True(darkMode >= 0, "the Facility Menu has no Dark Mode item");
        Assert.IsType<Separator>(items[darkMode + 1]);
        string[] labels = [.. items.Skip(darkMode + 2).OfType<MenuItem>().Select(MenuLabel)];
        Assert.Equal(["Temp. Dep Freqs:", "Override Dep Freq:", "Add current frequency to Temp. Dep Freqs"], labels);
        MenuItem addCurrent = Assert.IsType<MenuItem>(items[darkMode + 4]);
        Assert.Equal(MenuItemToggleType.CheckBox, addCurrent.ToggleType);
    }

    [AvaloniaFact]
    public void FacilityMenu_TypingAValidFrequency_AddsItToTheTempList()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);

        TextBox box = TypeTempDepFreqs(OpenFacilityMenu(view), "125.35");

        Assert.Equal(["125.35"], vm.TempDepFreqs);
        Assert.False(vm.IsTempDepFreqsInvalid);
        Assert.Equal("125.35", box.Text);
    }

    [AvaloniaFact]
    public void FacilityMenu_TypingAnInvalidFrequency_LeavesItOutOfTheTempList()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);

        MenuFlyout flyout = OpenFacilityMenu(view);
        TypeTempDepFreqs(flyout, "TOOLONGXX");

        Assert.Empty(vm.TempDepFreqs);
        Assert.True(vm.IsTempDepFreqsInvalid);
        TextBlock message = HostRowControls(flyout, "Temp. Dep Freqs:").OfType<TextBlock>().Single(t => t.Text == "Invalid Frequency");
        Assert.True(message.IsVisible);
    }

    /// <summary>
    /// The checkbox's frequency survives the menu closing: the box still shows the list it was opened on, and closing
    /// commits only what the controller typed, never that untouched text over the list the checkbox changed.
    /// </summary>
    [AvaloniaFact]
    public void FacilityMenu_AddCurrentFrequency_ThenClosing_KeepsTheStudentFrequency()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        TdlsConfigDto config = ConfigWithMandatoryDepFreq();
        vm.ApplyFacilityView(config.FacilityId, new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config], "121.100"));
        vm.CommitTempDepFreqs("125.35");
        VTdlsView view = BootView(vm);

        MenuFlyout flyout = OpenFacilityMenu(view);
        Dispatcher.UIThread.RunJobs();
        ClickMenuItem(flyout, "Add current frequency to Temp. Dep Freqs");
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["121.100", "125.35"], vm.TempDepFreqs);
    }

    [AvaloniaFact]
    public void FacilityMenu_PickingAnOverride_SetsItAndKeepsItSelected()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        vm.CommitTempDepFreqs("125.35, 119.1");
        VTdlsView view = BootView(vm);

        ComboBox picker = HostRowControls(OpenFacilityMenu(view), "Override Dep Freq:").OfType<ComboBox>().Single();
        picker.SelectedItem = "119.1";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("119.1", vm.OverrideDepFreq);
        Assert.Equal("119.1", picker.SelectedItem);
    }

    [AvaloniaFact]
    public void FacilityMenu_AddCurrentFrequency_AddsTheStudentFrequency()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        TdlsConfigDto config = ConfigWithMandatoryDepFreq();
        vm.ApplyFacilityView(config.FacilityId, new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config], "121.100"));
        VTdlsView view = BootView(vm);

        ClickMenuItem(OpenFacilityMenu(view), "Add current frequency to Temp. Dep Freqs");

        Assert.True(vm.AddCurrentFrequencyToTempDepFreqs);
        Assert.Equal(["121.100"], vm.TempDepFreqs);
    }

    [AvaloniaFact]
    public void FacilityMenu_AddCurrentFrequency_WithNoStudentFrequency_AddsNothing()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);

        ClickMenuItem(OpenFacilityMenu(view), "Add current frequency to Temp. Dep Freqs");

        Assert.True(vm.AddCurrentFrequencyToTempDepFreqs);
        Assert.Empty(vm.TempDepFreqs);
    }

    /// <summary>Clicks the Facility button and returns the menu it opened.</summary>
    private static MenuFlyout OpenFacilityMenu(VTdlsView view)
    {
        Button button = view.FindControl<Button>("FacilityButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return flyout;
    }

    /// <summary>A menu item's label: its header text, or the label in front of the control it hosts.</summary>
    private static string MenuLabel(MenuItem item) =>
        item.Header as string ?? Assert.IsAssignableFrom<Panel>(item.Header).Children.OfType<TextBlock>().First().Text ?? "";

    /// <summary>Types into the Temp. Dep Freqs box with the keyboard and commits it with Enter, as a controller does.</summary>
    private static TextBox TypeTempDepFreqs(MenuFlyout flyout, string text)
    {
        MenuItem row = flyout.Items.OfType<MenuItem>().Single(i => (i.Header is Panel) && (MenuLabel(i) == "Temp. Dep Freqs:"));
        TextBox box = Assert.Single(((Panel)row.Header!).GetVisualDescendants().OfType<TextBox>());
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        TopLevel topLevel = TopLevel.GetTopLevel(box)!;
        topLevel.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(text, box.Text);
        topLevel.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        return box;
    }

    /// <summary>The controls a hosted menu row shows behind its label.</summary>
    private static IEnumerable<Visual> HostRowControls(MenuFlyout flyout, string label)
    {
        MenuItem row = flyout.Items.OfType<MenuItem>().Single(i => (i.Header is Panel) && (MenuLabel(i) == label));
        return ((Panel)row.Header!).GetVisualDescendants();
    }

    private static void ClickMenuItem(MenuFlyout flyout, string label)
    {
        MenuItem item = flyout.Items.OfType<MenuItem>().Single(i => MenuLabel(i) == label);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // ── Keyboard model (upstream vTDLS) ──────────────────────────

    [AvaloniaFact]
    public void PageOpensOnTheFirstDclRow_AndDownTwiceThenEnterOpensTheThird()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(
            new TdlsStateDto(
                [Item("id1", "UAL111", facilityId: "IAD"), Item("id2", "AAL222", facilityId: "IAD"), Item("id3", "DAL333", facilityId: "IAD")],
                []
            )
        );
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);
        Assert.Equal(["UAL111"], HighlightedRows(view, "DclListBox"));

        PressKey(view, Key.Down);
        PressKey(view, Key.Down);
        Assert.Null(vm.Editor);
        Assert.Equal(["DAL333"], HighlightedRows(view, "DclListBox"));

        PressKey(view, Key.Enter);

        Assert.Equal("DAL333", vm.Editor?.Callsign);
        Assert.Equal("DAL333", vm.SelectedItem?.AircraftId);
    }

    /// <summary>Down on a focused list moves the highlight; the ListBox no longer selects the row, which would open its editor.</summary>
    [AvaloniaFact]
    public void DownOnTheFocusedList_HighlightsWithoutOpeningAnEditor()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        VTdlsView view = BootViewWithFocusedDclRow(vm, transport);

        UiWindow(view).KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Editor);
        Assert.Null(vm.SelectedItem);
        Assert.Equal("AAL222", vm.HighlightedItem?.AircraftId);
        Assert.Equal(["AAL222"], HighlightedRows(view, "DclListBox"));
    }

    /// <summary>The list's other navigation keys would select a row of a focused ListBox too, and with it open an editor.</summary>
    [AvaloniaTheory]
    [InlineData(PhysicalKey.ArrowRight)]
    [InlineData(PhysicalKey.ArrowLeft)]
    [InlineData(PhysicalKey.Home)]
    [InlineData(PhysicalKey.End)]
    [InlineData(PhysicalKey.PageDown)]
    [InlineData(PhysicalKey.PageUp)]
    public void OtherNavigationKeysOnTheFocusedList_OpenNoEditor(PhysicalKey key)
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        VTdlsView view = BootViewWithFocusedDclRow(vm, transport);

        UiWindow(view).KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Editor);
        Assert.Null(vm.SelectedItem);
        Assert.Equal("UAL111", vm.HighlightedItem?.AircraftId);
    }

    /// <summary>Two DCL rows on the page, keyboard focus on the first, where a click leaves it; the ListBox itself does not take focus.</summary>
    private static VTdlsView BootViewWithFocusedDclRow(VTdlsViewModel vm, FakeTdlsTransport transport)
    {
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD"), Item("id2", "AAL222", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);
        ListBox list = view.FindControl<ListBox>("DclListBox")!;
        list.GetVisualDescendants().OfType<ListBoxItem>().First().Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsKeyboardFocusWithin);
        return view;
    }

    [AvaloniaFact]
    public void RowClick_MovesTheHighlightAndTheActiveList()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(
            new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD"), Item("id2", "AAL9", facilityId: "IAD", status: TdlsStatus.Sent)], [])
        );
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);
        Assert.Equal("DclHeader", ActiveHeader(view));

        ClickRow(view, "PdcListBox", "AAL9");

        Assert.Equal(TdlsAircraftList.Pdc, vm.ActiveList);
        Assert.Equal("PdcHeader", ActiveHeader(view));
        Assert.Equal("AAL9", vm.HighlightedItem?.AircraftId);
        Assert.Equal(["AAL9"], HighlightedRows(view, "PdcListBox"));
        Assert.Empty(HighlightedRows(view, "DclListBox"));
    }

    [AvaloniaFact]
    public void ShiftTabAndBacktick_CycleTheListsBackwards()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);

        PressKey(view, Key.Tab, KeyModifiers.Shift);
        Assert.Equal("PdcHeader", ActiveHeader(view));
        PressKey(view, Key.OemTilde);
        Assert.Equal("CpdlcHeader", ActiveHeader(view));
        PressKey(view, Key.OemTilde);
        Assert.Equal("DclHeader", ActiveHeader(view));
    }

    [AvaloniaFact]
    public void Backtick_InTheEditor_StepsTheFieldsBackwards()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SidCombo", FocusedName(view));

        PressKeyOnFocused(view, Key.OemTilde);
        Assert.Equal("LocalInfoCombo", FocusedName(view));
        PressKeyOnFocused(view, Key.OemTilde);
        Assert.Equal("ContactInfoCombo", FocusedName(view));
    }

    [AvaloniaFact]
    public void ClosingTheEditor_ReturnsFocusToTheView()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SidCombo", FocusedName(view));

        PressCancel(view);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Editor);
        Assert.Same(view, TopLevel.GetTopLevel(view)!.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void ClosingTheEditor_LeavesFocusOnAControlOutsideTheView()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        var view = new VTdlsView { DataContext = vm, Height = 800 };
        var outside = new TextBox { Name = "Outside" };
        var window = new Window
        {
            Width = 900,
            Height = 900,
            Content = new StackPanel { Children = { outside, view } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        outside.Focus();
        Dispatcher.UIThread.RunJobs();

        vm.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Editor);
        Assert.Same(outside, window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void CtrlAltRight_WhileDisconnected_DoesNothing()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        transport.AccessibleFacilities = [new AccessibleFacilityDto("IAD", "Dulles", true), new AccessibleFacilityDto("DCA", "National", false)];
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);
        vm.SetConnected(false);
        Dispatcher.UIThread.RunJobs();

        PressKey(view, Key.Right, KeyModifiers.Control | KeyModifiers.Alt);

        Assert.Equal("IAD", vm.FacilityId);
        Assert.Equal(0, transport.FacilityViewRequests);
    }

    /// <summary>
    /// Upstream's list headers in each theme: the active list's darker and bold, the others pale and regular, black text
    /// reading on all of them; and the highlighted row inverted, while a row both highlighted and open keeps the
    /// selection colours.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Theme_ListHeadersAndTheHighlightedRow(bool dark)
    {
        string activeHeader = dark ? "#1c8a1c" : "#11be11";
        string inactiveHeader = dark ? "#5fa05f" : "#80ee80";
        string highlightBg = dark ? "#ffffff" : "#000000";
        string highlightFg = dark ? "#000000" : "#ffffff";
        string selectedBg = dark ? "#5aa9ff" : "#000000";
        string selectedFg = dark ? "#000000" : "#ffffff";
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD"), Item("id2", "AAL222", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        vm.IsDarkMode = dark;
        VTdlsView view = BootView(vm);

        Color black = Colors.Black;
        foreach (
            (string name, string expected, FontWeight weight) in (List<(string, string, FontWeight)>)
                [
                    ("DclHeader", activeHeader, FontWeight.Bold),
                    ("PdcHeader", inactiveHeader, FontWeight.Normal),
                    ("CpdlcHeader", inactiveHeader, FontWeight.Normal),
                ]
        )
        {
            Border header = view.FindControl<Border>(name)!;
            Color background = SolidColour(header.Background, name);
            Assert.Equal(Color.Parse(expected), background);
            Assert.Equal(weight, ((TextBlock)header.Child!).FontWeight);
            Assert.True(ContrastRatio(black, background) >= 4.5, $"{name} text reads at {ContrastRatio(black, background):F2}:1");
        }

        (Color bg, Color fg) = RowColours(view, "UAL111");
        Assert.Equal(Color.Parse(highlightBg), bg);
        Assert.Equal(Color.Parse(highlightFg), fg);

        ClickRow(view, "DclListBox", "UAL111");
        Assert.Same(vm.HighlightedItem, vm.SelectedItem);
        (bg, fg) = RowColours(view, "UAL111");
        Assert.Equal(Color.Parse(selectedBg), bg);
        Assert.Equal(Color.Parse(selectedFg), fg);
    }

    /// <summary>The background and text colour a DCL row's template draws it in.</summary>
    private static (Color Background, Color Foreground) RowColours(VTdlsView view, string callsign)
    {
        ListBoxItem row = view.FindControl<ListBox>("DclListBox")!
            .GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Single(c => (c.DataContext as TdlsItemViewModel)?.AircraftId == callsign);
        row.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        ContentPresenter presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First();
        return (SolidColour(presenter.Background, $"{callsign} row"), SolidColour(presenter.Foreground, $"{callsign} row text"));
    }

    /// <summary>Raises a key on the focused control, so it tunnels through the view to it.</summary>
    private static void PressKeyOnFocused(VTdlsView view, Key key)
    {
        var focused = (Interactive)TopLevel.GetTopLevel(view)!.FocusManager!.GetFocusedElement()!;
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Tab_MovesTheActiveHeader_DclCpdlcPdc()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);
        Assert.Equal("DclHeader", ActiveHeader(view));

        PressKey(view, Key.Tab);
        Assert.Equal("CpdlcHeader", ActiveHeader(view));
        PressKey(view, Key.Tab);
        Assert.Equal("PdcHeader", ActiveHeader(view));
        PressKey(view, Key.Tab);
        Assert.Equal("DclHeader", ActiveHeader(view));
    }

    [AvaloniaFact]
    public void EditorOpen_FocusesSid_AndTabStepsTheFieldsInUpstreamOrder()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SidCombo", FocusedName(view));

        string[] order =
        [
            "TransitionCombo",
            "ClimboutCombo",
            "ClimbviaCombo",
            "InitialAltCombo",
            "DepFreqCombo",
            "ExpectCombo",
            "ContactInfoCombo",
            "LocalInfoCombo",
            "SidCombo",
        ];
        foreach (string expected in order)
        {
            UiWindow(view).KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, FocusedName(view));
        }

        UiWindow(view).KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("LocalInfoCombo", FocusedName(view));
        UiWindow(view).KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ContactInfoCombo", FocusedName(view));
    }

    [AvaloniaFact]
    public void RightAndLeft_OnAFocusedField_LeaveItsSelectionAlone()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootViewAtSize(vm, windowWidth: 900, windowHeight: 900);
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        ComboBox sid = view.FindControl<ComboBox>("SidCombo")!;
        Assert.Equal("SidCombo", FocusedName(view));
        object? before = sid.SelectedItem;

        UiWindow(view).KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        UiWindow(view).KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, sid.SelectedItem);
        Assert.Equal("SidCombo", FocusedName(view));
    }

    [AvaloniaFact]
    public void Esc_OpensTheFacilityMenu_AndEscAgainClosesIt()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);
        Button button = view.FindControl<Button>("FacilityButton")!;

        PressKey(view, Key.Escape);
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);

        PressKey(view, Key.Escape);
        Assert.False(flyout.IsOpen);
    }

    /// <summary>
    /// Keys typed in the Facility Menu belong to the menu: its popup routes them up through the view's button, but the
    /// list keys must not act on them. Enter commits the frequencies rather than opening the highlighted row, and the
    /// arrows leave the list highlight where it was.
    /// </summary>
    [AvaloniaFact]
    public void KeysTypedInTheFacilityMenu_DoNotDriveTheLists()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", facilityId: "IAD"), Item("id2", "AAL222", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        VTdlsView view = BootView(vm);
        PressKey(view, Key.Down);
        TdlsItemViewModel? highlighted = vm.HighlightedItem;
        Assert.NotNull(highlighted);

        MenuFlyout flyout = OpenFacilityMenu(view);
        TextBox box = TypeTempDepFreqs(flyout, "125.35");
        TopLevel menuTop = TopLevel.GetTopLevel(box)!;
        menuTop.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        menuTop.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        menuTop.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // On the desktop the menu's popup passes its events to the window it belongs to, through the Facility button
        // and so through the view. The headless menu does not route that way, so a popup the view hosts stands in: its
        // contents are a separate visual tree whose key events route through the view, which the probe confirms.
        var popupBox = new TextBox();
        var popup = new Popup { Child = popupBox, PlacementTarget = view.FindControl<Button>("FacilityButton") };
        view.GetVisualDescendants().OfType<Grid>().First().Children.Add(popup);
        popup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        popupBox.Focus();
        Dispatcher.UIThread.RunJobs();
        int throughView = 0;
        view.AddHandler(InputElement.KeyDownEvent, (_, _) => throughView++, RoutingStrategies.Tunnel, handledEventsToo: true);
        foreach (PhysicalKey key in (PhysicalKey[])[PhysicalKey.ArrowDown, PhysicalKey.ArrowUp, PhysicalKey.Tab, PhysicalKey.Enter])
        {
            TopLevel.GetTopLevel(popupBox)!.KeyPressQwerty(key, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(4, throughView);
        Assert.Null(vm.Editor);
        Assert.Equal(TdlsAircraftList.Dcl, vm.ActiveList);
        Assert.Equal(["125.35"], vm.TempDepFreqs);
        Assert.Same(highlighted, vm.HighlightedItem);
    }

    [AvaloniaFact]
    public void Esc_WithFindOpen_ClosesFindOnly()
    {
        (VTdlsViewModel vm, _) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);
        PressKey(view, Key.F, KeyModifiers.Control);
        Assert.True(view.FindController.IsVisible);

        PressKey(view, Key.Escape);

        Assert.False(view.FindController.IsVisible);
        Assert.Null(FlyoutBase.GetAttachedFlyout(view.FindControl<Button>("FacilityButton")!));
    }

    [AvaloniaFact]
    public void CtrlAltRight_SwitchesToTheNextAccessibleFacility_AndWraps()
    {
        (VTdlsViewModel vm, FakeTdlsTransport transport) = MakeVm();
        transport.AccessibleFacilities = [new AccessibleFacilityDto("IAD", "Dulles", true), new AccessibleFacilityDto("DCA", "National", false)];
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        VTdlsView view = BootView(vm);

        PressKey(view, Key.Right, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal("DCA", vm.FacilityId);

        PressKey(view, Key.Right, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal("IAD", vm.FacilityId);

        PressKey(view, Key.Left, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal("DCA", vm.FacilityId);
    }

    /// <summary>Raises a key on the view, as the Find test does: tunnelling and bubbling through the view's own handlers.</summary>
    private static void PressKey(VTdlsView view, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        view.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
            }
        );
        Dispatcher.UIThread.RunJobs();
    }

    private static string? FocusedName(VTdlsView view) => (TopLevel.GetTopLevel(view)!.FocusManager!.GetFocusedElement() as Control)?.Name;

    /// <summary>The single list header drawn active.</summary>
    private static string ActiveHeader(VTdlsView view) =>
        Assert.Single((string[])["DclHeader", "CpdlcHeader", "PdcHeader"], n => view.FindControl<Border>(n)!.Classes.Contains("active"));

    /// <summary>The callsigns of the rows a list draws highlighted.</summary>
    private static string[] HighlightedRows(VTdlsView view, string listName) =>
        [
            .. view.FindControl<ListBox>(listName)!
                .GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Where(row => row.Classes.Contains("highlighted"))
                .Select(row => ((TdlsItemViewModel)row.DataContext!).AircraftId),
        ];

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Puts the VM on a consolidated NCT page over two members: OAK (ops configs
    /// enabled, per-config SID ids) and SFO (plain, no ops configs).
    /// </summary>
    private static void SeedConsolidated(VTdlsViewModel vm) =>
        vm.ApplyFacilityView(
            "NCT",
            new TdlsFacilityViewDto(
                "NCT",
                "Northern California TRACON",
                [OpsConfigFacility(), ConfigWithMandatoryDepFreq() with { FacilityId = "SFO", FacilityName = "San Francisco Intl ATCT" }],
                StudentFrequencyMhz: null
            )
        );

    /// <summary>Puts the VM on a single-facility page backed by <paramref name="config"/>.</summary>
    private static void SeedFacility(VTdlsViewModel vm, TdlsConfigDto config) =>
        vm.ApplyFacilityView(config.FacilityId, new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config], StudentFrequencyMhz: null));

    /// <summary>Facility shaped like OAK: ops configs on, facility-level SID list empty, a distinct SID id per config.</summary>
    private static TdlsConfigDto OpsConfigFacility()
    {
        static TdlsSidDto Sid(string id) =>
            new(id, "HUSSH2", [new TdlsSidTransitionDto($"{id}-t", "- - - -", null, null, null, null, null, null, null, null)]);

        return ConfigWithMandatoryDepFreq() with
        {
            FacilityId = "OAK",
            Sids = [],
            MandatorySid = false,
            MandatoryDepFreq = false,
            MandatoryExpect = false,
            DclOpConfigsEnabled = true,
            OpConfigs =
            [
                new TdlsOpConfigDto("cfg-west", "OAKW", [Sid("sid-west")], null, null),
                new TdlsOpConfigDto("cfg-east", "OAKE", [Sid("sid-east")], null, null),
            ],
            ActiveOpConfigId = "cfg-west",
        };
    }

    /// <summary>Facility whose transition supplies no departure frequency, so the editor opens one mandatory field short.</summary>
    private static TdlsConfigDto ConfigWithMandatoryDepFreq() =>
        new(
            FacilityId: "IAD",
            FacilityName: "Washington Dulles ATCT",
            MandatorySid: true,
            MandatoryClimbout: false,
            MandatoryClimbvia: false,
            MandatoryInitialAlt: false,
            MandatoryDepFreq: true,
            MandatoryExpect: true,
            MandatoryContactInfo: false,
            MandatoryLocalInfo: false,
            Sids:
            [
                new TdlsSidDto(
                    "RNLDI4",
                    "RNLDI4",
                    [
                        new TdlsSidTransitionDto(
                            "OTTTO",
                            "OTTTO",
                            FirstRoutePoint: "OTTTO",
                            DefaultExpect: "10 MIN AFT DP",
                            DefaultClimbout: null,
                            DefaultClimbvia: null,
                            DefaultInitialAlt: null,
                            DefaultDepFreq: null,
                            DefaultContactInfo: null,
                            DefaultLocalInfo: null
                        ),
                    ]
                ),
            ],
            Climbouts: [],
            Climbvias: [],
            InitialAlts: [new TdlsClearanceValueDto("3000FT", "3000FT")],
            DepFreqs: [new TdlsClearanceValueDto("125050", "125.050")],
            Expects: [new TdlsClearanceValueDto("10MIN", "10 MIN AFT DP")],
            ContactInfos: [],
            LocalInfos: [],
            DefaultSidId: "RNLDI4",
            DefaultTransitionId: "OTTTO"
        );

    /// <summary>
    /// The same facility with a climb-via list on it — OAK's shape: the plain instruction plus the FE's "- - - -"
    /// placeholder entry. Nothing defaults to one, so the editor still opens with Maintain in play.
    /// </summary>
    private static TdlsConfigDto ConfigWithClimbVias() =>
        ConfigWithMandatoryDepFreq() with
        {
            Climbvias = [new TdlsClearanceValueDto("clbvia", "CLIMB VIA SID"), new TdlsClearanceValueDto("none", "- - - -")],
        };

    /// <summary>
    /// Facility offering two SIDs with one transition each, so an amended route can name a different SID than the one
    /// filed. The two transitions define different Expect and departure-frequency defaults, which is what makes an
    /// applied default visible after the switch.
    /// </summary>
    private static TdlsConfigDto ConfigWithTwoSids() =>
        ConfigWithMandatoryDepFreq() with
        {
            Sids =
            [
                new TdlsSidDto("GAPP7", "GAPP7", [TransitionAt("GAPP7-BOOKE", "BOOKE", "10 MIN AFT DP", "125.050")]),
                new TdlsSidDto("GUNNR7", "GUNNR7", [TransitionAt("GUNNR7-SPACY", "SPACY", "20 MIN AFT DP", "126.650")]),
            ],
            Expects = [new TdlsClearanceValueDto("10MIN", "10 MIN AFT DP"), new TdlsClearanceValueDto("20MIN", "20 MIN AFT DP")],
            DepFreqs = [new TdlsClearanceValueDto("125050", "125.050"), new TdlsClearanceValueDto("126650", "126.650")],
            DefaultSidId = "GAPP7",
            DefaultTransitionId = "GAPP7-BOOKE",
        };

    /// <summary>A transition entered at <paramref name="fix"/> and named after it, carrying the FE's defaults for that pairing.</summary>
    private static TdlsSidTransitionDto TransitionAt(string id, string fix, string expect, string depFreq) =>
        new(
            id,
            fix,
            FirstRoutePoint: fix,
            DefaultExpect: expect,
            DefaultClimbout: null,
            DefaultClimbvia: null,
            DefaultInitialAlt: null,
            DefaultDepFreq: depFreq,
            DefaultContactInfo: null,
            DefaultLocalInfo: null
        );

    private static TdlsFlightPlanInfoDto FlightPlanWithRoute(string route) =>
        new(
            AssignedBeaconCode: 1234,
            Departure: "KIAD",
            Destination: "KBOS",
            Route: route,
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "123",
            CruiseAltitude: 35000
        );

    private static TdlsItemDto Item(
        string id,
        string callsign,
        TdlsFlightPlanInfoDto? fp = null,
        string facilityId = "",
        TdlsStatus status = TdlsStatus.Pending
    ) =>
        new(
            id,
            callsign,
            Cid: null,
            FacilityId: facilityId,
            status,
            Sequence: 0,
            CreatedUtc: default,
            SentUtc: null,
            WilcoUtc: null,
            ExpiresUtc: default,
            SentPayload: null,
            FlightPlan: fp
        );

    /// <summary>A view-model over a fake transport whose every sent command succeeds.</summary>
    private static (VTdlsViewModel Vm, FakeTdlsTransport Transport) MakeVm() => MakeVm(() => new CommandResultDto(true, null));

    /// <summary>A view-model over a fake transport. <paramref name="commandResult"/> answers each sent command.</summary>
    private static (VTdlsViewModel Vm, FakeTdlsTransport Transport) MakeVm(Func<CommandResultDto?> commandResult)
    {
        var transport = new FakeTdlsTransport();
        var vm = new VTdlsViewModel(transport, (callsign, command, _) => RecordCommand(callsign, command, commandResult()), getUserInitials: null);
        return (vm, transport);
    }

    /// <summary>Canonical commands the view-model emitted, so tests can assert the wire text rather than an RPC call.</summary>
    private static readonly List<(string Callsign, string Command)> SentCommands = [];

    private static Task<CommandResultDto?> RecordCommand(string callsign, string command, CommandResultDto? result)
    {
        SentCommands.Add((callsign, command));
        return Task.FromResult(result);
    }

    private static VTdlsView BootView(VTdlsViewModel vm) => BootViewAtWidth(vm, windowWidth: 900);

    /// <summary>Boots the view in a window of a given width — a narrow one is where the editor's star-sized rows are under pressure.</summary>
    private static VTdlsView BootViewAtWidth(VTdlsViewModel vm, double windowWidth) => BootViewAtSize(vm, windowWidth, windowHeight: 500);

    /// <summary>Boots the view in a window of a given size: the page's lists and editor share one vertical stack.</summary>
    private static VTdlsView BootViewAtSize(VTdlsViewModel vm, double windowWidth, double windowHeight)
    {
        var view = new VTdlsView { DataContext = vm };
        var window = new Window
        {
            Width = windowWidth,
            Height = windowHeight,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    /// <summary>Presses Cancel's key (F10) on the view, the way a controller does.</summary>
    private static void PressCancel(VTdlsView view)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F10 });
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks a list row with the pointer, the way a controller does, so the ListBox's own selection and its binding run.</summary>
    private static void ClickRow(VTdlsView view, string listName, string callsign)
    {
        ListBox list = view.FindControl<ListBox>(listName)!;
        ListBoxItem row = list.GetVisualDescendants().OfType<ListBoxItem>().Single(c => (c.DataContext as TdlsItemViewModel)?.AircraftId == callsign);
        ClickControl(UiWindow(view), row);
    }

    private static Window UiWindow(VTdlsView view) => (Window)TopLevel.GetTopLevel(view)!;

    /// <summary>Clicks a control with the pointer, the way a controller does.</summary>
    private static void ClickControl(Window window, Control control)
    {
        Point centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeTdlsTransport : ITdlsTransport
    {
        public bool IsConnected => true;

#pragma warning disable CS0067 // required by the interface but never raised by this fake
        public event Action? Connected;
        public event Action<Exception?>? Closed;
        public event Action<Exception?>? Reconnecting;
        public event Action<string?>? Reconnected;
        public event Action<TdlsItemRemovedDto>? TdlsItemRemoved;
#pragma warning restore CS0067
        public event Action<TdlsItemDto>? TdlsItemChanged;
        public event Action<TdlsStateDto>? TdlsStateChanged;

        public void PushState(TdlsStateDto state) => TdlsStateChanged?.Invoke(state);

        /// <summary>One item changed — the incremental broadcast a flight-plan amendment produces.</summary>
        public void PushItem(TdlsItemDto item) => TdlsItemChanged?.Invoke(item);

        /// <summary>The facilities <see cref="GetAccessibleTdlsFacilitiesAsync"/> answers with; none by default.</summary>
        public List<AccessibleFacilityDto> AccessibleFacilities { get; set; } = [];

        public Task<List<AccessibleFacilityDto>> GetAccessibleTdlsFacilitiesAsync() =>
            Task.FromResult(new List<AccessibleFacilityDto>(AccessibleFacilities));

        /// <summary>The page <see cref="GetTdlsFacilityViewAsync"/> answers with; null by default.</summary>
        public TdlsFacilityViewDto? FacilityView { get; set; }

        public int FacilityViewRequests { get; private set; }

        public int FullStateRequests { get; private set; }

        /// <summary>When set, the facility-view fetch waits on this instead of answering at once with <see cref="FacilityView"/>.</summary>
        public TaskCompletionSource<TdlsFacilityViewDto?>? HeldFacilityView { get; set; }

        public Task<TdlsFacilityViewDto?> GetTdlsFacilityViewAsync(string facilityId)
        {
            FacilityViewRequests++;
            return HeldFacilityView?.Task ?? Task.FromResult(FacilityView);
        }

        public Task RequestFullTdlsStateAsync()
        {
            FullStateRequests++;
            return Task.CompletedTask;
        }
    }
}
