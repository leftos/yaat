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
        vm.Config = ConfigWithMandatoryDepFreq();
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742")], []));
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
        vm.Config = ConfigWithMandatoryDepFreq() with { MandatoryInitialAlt = true };
        transport.PushState(new TdlsStateDto([Item("id1", "UAL1742")], []));
        Dispatcher.UIThread.RunJobs();
        var view = BootView(vm);

        vm.SelectedItem = vm.DclItems.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Maintain, Departure frequency", view.FooterStatusText);

        vm.Editor!.SelectedInitialAlt = vm.Editor.InitialAlts[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MANDATORY FIELD NOT SET — Departure frequency", view.FooterStatusText);
    }

    // ── Helpers ──────────────────────────────────────────────────

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

    private static TdlsItemDto Item(string id, string callsign, TdlsFlightPlanInfoDto? fp = null) =>
        new(
            id,
            callsign,
            Cid: null,
            FacilityId: "",
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
        var vm = new VTdlsViewModel(transport, (_, _, _) => Task.CompletedTask, getUserInitials: null);
        return (vm, transport);
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

        public Task<TdlsConfigDto?> GetTdlsConfigForFacilityAsync(string facilityId) => Task.FromResult<TdlsConfigDto?>(null);

        public Task RequestFullTdlsStateAsync() => Task.CompletedTask;
    }
}
