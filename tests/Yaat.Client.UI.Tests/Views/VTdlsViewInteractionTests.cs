using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
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
        var (vm, transport) = MakeVm();
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111"), Item("id2", "AAL222"), Item("id3", "UAL333")], []));
        Dispatcher.UIThread.RunJobs();
        var view = BootView(vm);

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
        var (vm, transport) = MakeVm();
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
        transport.PushState(new TdlsStateDto([Item("id1", "UAL111", fp)], []));
        Dispatcher.UIThread.RunJobs();
        var view = BootView(vm);

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
        var (vm, transport) = MakeVm();
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
        var view = BootView(vm);

        // Selecting the DCL item opens the flight-plan editor, so the header fields render.
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selectable = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.Contains(selectable, t => t.Text == "UAL111");
        Assert.Contains(selectable, t => t.Text == "RMK: CTC NORCAL");
        var route = Assert.Single(selectable, t => t.Text == "KSFO.SSTIK2.KLAX");

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
        var (vm, transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        var view = BootView(vm);

        // Nothing selected — the editor is closed, so the footer shows the idle status.
        Assert.Equal("CLEARANCE TYPE: PDC", view.FooterStatusText);
        Assert.False(view.FooterStatusIsWarning);

        // Selecting a Pending item opens the editor with the mandatory Dep Freq unset.
        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Departure frequency", view.FooterStatusText);
        Assert.True(view.FooterStatusIsWarning);

        // Filling it must flip the footer on the spot, not on the next tick.
        vm.Editor!.SelectedDepFreq = vm.Editor.DepFreqs[0];
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
        var (vm, transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq() with { MandatoryInitialAlt = true });
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        var view = BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Maintain, Departure frequency", view.FooterStatusText);

        vm.Editor!.SelectedInitialAlt = vm.Editor.InitialAlts[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Departure frequency", view.FooterStatusText);
    }

    /// <summary>
    /// An amendment that lands while the controller is composing a clearance updates the header in place. Re-inserting
    /// the item's view-model would null the two-way bound ListBox selection, which closes the editor and discards every
    /// dropdown already chosen — so the same editor instance has to survive the push and show the new route.
    /// </summary>
    [AvaloniaFact]
    public void AmendedFlightPlan_RefreshesTheOpenEditorInPlace()
    {
        var (vm, transport) = MakeVm();
        SeedFacility(vm, ConfigWithMandatoryDepFreq());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742", FlightPlanWithRoute("SSTIK2"), facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        var editor = vm.Editor!;
        var composed = editor.DepFreqs[0];
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
        var (vm, transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL300", FlightPlanWithRoute("GAPP7 BOOKE J80 BOS"), facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        var editor = vm.Editor!;
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
        var (vm, transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        var filed = FlightPlanWithRoute("GAPP7 BOOKE J80 BOS");
        transport.PushState(new TdlsStateDto([Item("id1", "UAL300", filed, facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        var editor = vm.Editor!;

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
        var (vm, transport) = MakeVm();
        SeedFacility(vm, ConfigWithTwoSids());
        transport.PushState(new TdlsStateDto([Item("id1", "UAL300", FlightPlanWithRoute("GAPP7 BOOKE J80 BOS"), facilityId: "IAD")], []));
        Dispatcher.UIThread.RunJobs();
        var view = BootViewAtWidth(vm, windowWidth: 700);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var row = DropdownRowCombos(view);
        AssertAtLeastWide(row, "SID", 96);
        AssertAtLeastWide(row, "Transition", 96);
        AssertAtLeastWide(row, "Maintain", 100);

        // Same coordinate space (one Grid, five children), so a field running into the next one shows up as an
        // arranged rect that reaches past its neighbour's left edge.
        var ordered = row.OrderBy(f => f.Value.Bounds.Left).ToList();
        for (var i = 0; (i + 1) < ordered.Count; i++)
        {
            var (name, box) = (ordered[i].Key, ordered[i].Value);
            var (nextName, nextBox) = (ordered[i + 1].Key, ordered[i + 1].Value);
            Assert.True(
                box.Bounds.Right <= nextBox.Bounds.Left,
                $"{name} ends at {box.Bounds.Right} and overlaps {nextName}, which starts at {nextBox.Bounds.Left}"
            );
        }
    }

    private static void AssertAtLeastWide(IReadOnlyDictionary<string, ComboBox> row, string field, double minWidth) =>
        Assert.True(row[field].Bounds.Width >= minWidth, $"{field} rendered {row[field].Bounds.Width}px wide, below its {minWidth}px floor");

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
    /// The load-time state push lands before any facility page has been fetched, so the items are created
    /// and listed against an empty member set. Switching to the facility clears the lists while keeping the
    /// identities, and the full-state re-push that follows the switch is what has to put them back: nothing
    /// about those items moved between or within the lists, they are simply no longer in either one.
    /// </summary>
    [AvaloniaFact]
    public void FacilitySwitchAfterLoadTimeState_RelistsTheItems()
    {
        var (vm, transport) = MakeVm();
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
        var pendingFirst = vm.DclItems[0];
        var pendingSecond = vm.DclItems[1];
        var sent = vm.PdcItems[0];

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
        var (vm, transport) = MakeVm();
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
        Assert.Equal("sid-west", vm.Editor!.Sids.Single().Id);

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
        Assert.Equal("sid-east", vm.Editor!.Sids.Single().Id);
    }

    [AvaloniaFact]
    public async Task OpsConfig_SaveGoesToTheServer_AndIsHiddenWhereConfigsAreDisabled()
    {
        var (vm, _) = MakeVm();
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
        var (vm, transport) = MakeVm();
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
        var (vm, transport) = MakeVm();
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
        Assert.Equal("sid-east", vm.Editor!.Sids.Single().Id);

        // Moving to the SFO item swaps the whole configuration under the editor.
        vm.SelectedItem = vm.DclItems.Single(i => i.AircraftId == "SWA200");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SFO", vm.Config!.FacilityId);
        Assert.False(vm.AreOpConfigsEnabled);
    }

    [AvaloniaFact]
    public async Task Consolidated_OpsConfigSave_TargetsTheMemberFacility_NotTheParent()
    {
        var (vm, transport) = MakeVm();
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
                [OpsConfigFacility(), ConfigWithMandatoryDepFreq() with { FacilityId = "SFO", FacilityName = "San Francisco Intl ATCT" }]
            )
        );

    /// <summary>Puts the VM on a single-facility page backed by <paramref name="config"/>.</summary>
    private static void SeedFacility(VTdlsViewModel vm, TdlsConfigDto config) =>
        vm.ApplyFacilityView(config.FacilityId, new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config]));

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

    private static (VTdlsViewModel Vm, FakeTdlsTransport Transport) MakeVm()
    {
        var transport = new FakeTdlsTransport();
        var vm = new VTdlsViewModel(transport, (callsign, command, _) => RecordCommand(callsign, command), getUserInitials: null);
        return (vm, transport);
    }

    /// <summary>Canonical commands the view-model emitted, so tests can assert the wire text rather than an RPC call.</summary>
    private static readonly List<(string Callsign, string Command)> SentCommands = [];

    private static Task RecordCommand(string callsign, string command)
    {
        SentCommands.Add((callsign, command));
        return Task.CompletedTask;
    }

    private static VTdlsView BootView(VTdlsViewModel vm) => BootViewAtWidth(vm, windowWidth: 900);

    /// <summary>Boots the view in a window of a given width — a narrow one is where the editor's star-sized rows are under pressure.</summary>
    private static VTdlsView BootViewAtWidth(VTdlsViewModel vm, double windowWidth)
    {
        var view = new VTdlsView { DataContext = vm };
        var window = new Window
        {
            Width = windowWidth,
            Height = 500,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return view;
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

        public Task<List<AccessibleFacilityDto>> GetAccessibleTdlsFacilitiesAsync() => Task.FromResult(new List<AccessibleFacilityDto>());

        public Task<TdlsFacilityViewDto?> GetTdlsFacilityViewAsync(string facilityId) => Task.FromResult<TdlsFacilityViewDto?>(null);

        public Task RequestFullTdlsStateAsync() => Task.CompletedTask;
    }
}
