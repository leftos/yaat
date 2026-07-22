using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    private static TdlsItemDto Item(string id, string callsign, TdlsFlightPlanInfoDto? fp = null, string facilityId = "") =>
        new(
            id,
            callsign,
            Cid: null,
            FacilityId: facilityId,
            TdlsStatus.Pending,
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

    private static VTdlsView BootView(VTdlsViewModel vm)
    {
        var view = new VTdlsView { DataContext = vm };
        var window = new Window
        {
            Width = 900,
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
        public event Action<TdlsItemDto>? TdlsItemChanged;
        public event Action<TdlsItemRemovedDto>? TdlsItemRemoved;
#pragma warning restore CS0067
        public event Action<TdlsStateDto>? TdlsStateChanged;

        public void PushState(TdlsStateDto state) => TdlsStateChanged?.Invoke(state);

        public Task<List<AccessibleFacilityDto>> GetAccessibleTdlsFacilitiesAsync() => Task.FromResult(new List<AccessibleFacilityDto>());

        public Task<TdlsFacilityViewDto?> GetTdlsFacilityViewAsync(string facilityId) => Task.FromResult<TdlsFacilityViewDto?>(null);

        public Task RequestFullTdlsStateAsync() => Task.CompletedTask;
    }
}
