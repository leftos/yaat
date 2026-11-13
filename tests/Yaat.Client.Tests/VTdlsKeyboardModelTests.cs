using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Tdls.Services;
using Yaat.Client.Tdls.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// vTDLS's keyboard model at the view-model: the highlight Up and Down move and Enter opens, the active list Tab cycles,
/// where the highlight goes when the lists change or a row is sent, and facility cycling. The highlight is not the
/// selection: only opening a row selects it.
/// </summary>
public class VTdlsKeyboardModelTests
{
    [AvaloniaFact]
    public void Page_OpensWithTheFirstDclRowHighlighted()
    {
        VTdlsViewModel vm = PageWith(Pending("id1", "UAL1", 1), Pending("id2", "UAL2", 2));

        Assert.Equal(TdlsAircraftList.Dcl, vm.ActiveList);
        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);
        Assert.Null(vm.SelectedItem);
    }

    [AvaloniaFact]
    public void MoveHighlight_WrapsBothWays_WithoutSelecting()
    {
        VTdlsViewModel vm = PageWith(Pending("id1", "UAL1", 1), Pending("id2", "UAL2", 2), Pending("id3", "UAL3", 3));
        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);

        vm.MoveHighlight(-1);
        Assert.Equal("UAL3", vm.HighlightedItem?.AircraftId);
        vm.MoveHighlight(1);
        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);
        vm.MoveHighlight(1);
        Assert.Equal("UAL2", vm.HighlightedItem?.AircraftId);

        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.Editor);
    }

    [AvaloniaFact]
    public void MoveHighlight_OnAnEmptyList_HighlightsNothing()
    {
        VTdlsViewModel vm = PageWith();

        vm.MoveHighlight(1);

        Assert.Null(vm.HighlightedItem);
    }

    [AvaloniaFact]
    public void CycleActiveList_GoesDclCpdlcPdc_AndCpdlcHighlightsNothing()
    {
        VTdlsViewModel vm = PageWith(Pending("id1", "UAL1", 1), Sent("id2", "AAL2", 2));
        Assert.Equal(TdlsAircraftList.Dcl, vm.ActiveList);
        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);

        vm.CycleActiveList(1);
        Assert.Equal(TdlsAircraftList.Cpdlc, vm.ActiveList);
        Assert.Null(vm.HighlightedItem);

        vm.CycleActiveList(1);
        Assert.Equal(TdlsAircraftList.Pdc, vm.ActiveList);
        Assert.Equal("AAL2", vm.HighlightedItem?.AircraftId);

        vm.CycleActiveList(1);
        Assert.Equal(TdlsAircraftList.Dcl, vm.ActiveList);
        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);

        vm.CycleActiveList(-1);
        Assert.Equal(TdlsAircraftList.Pdc, vm.ActiveList);
    }

    [AvaloniaFact]
    public void OpenHighlighted_SelectsTheHighlightedRow()
    {
        VTdlsViewModel vm = PageWith(Pending("id1", "UAL1", 1), Pending("id2", "UAL2", 2));
        vm.MoveHighlight(1);

        Assert.True(vm.OpenHighlighted());

        Assert.Equal("UAL2", vm.SelectedItem?.AircraftId);
        Assert.Equal("UAL2", vm.Editor?.Callsign);
    }

    // ── The highlight follows the lists as they change ──

    [AvaloniaFact]
    public void RowArrivingAheadOfTheHighlight_LeavesTheHighlightOnItsRow()
    {
        var transport = new FakeTdlsTransport();
        VTdlsViewModel vm = Page(transport, Succeed, Pending("id2", "UAL2", 2), Pending("id3", "UAL3", 3));
        vm.MoveHighlight(1);
        Assert.Equal("UAL3", vm.HighlightedItem?.AircraftId);

        transport.PushItem(Pending("id1", "UAL1", 1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("UAL1", vm.DclItems[0].AircraftId);
        Assert.Equal("UAL3", vm.HighlightedItem?.AircraftId);
    }

    [AvaloniaFact]
    public void RemovingTheHighlightedRow_MovesTheHighlightToTheRowInItsPlace()
    {
        var transport = new FakeTdlsTransport();
        VTdlsViewModel vm = Page(transport, Succeed, Pending("id1", "UAL1", 1), Pending("id2", "UAL2", 2), Pending("id3", "UAL3", 3));
        vm.MoveHighlight(1);
        Assert.Equal("UAL2", vm.HighlightedItem?.AircraftId);

        transport.PushRemoved(new TdlsItemRemovedDto("id2", "OAK", "UAL2", Dumped: true));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("UAL3", vm.HighlightedItem?.AircraftId);
    }

    [AvaloniaFact]
    public void HighlightedDclRowTurningSent_MovesTheHighlightWithinDcl()
    {
        var transport = new FakeTdlsTransport();
        VTdlsViewModel vm = Page(transport, Succeed, Pending("id1", "UAL1", 1), Pending("id2", "UAL2", 2));
        vm.MoveHighlight(1);
        Assert.Equal("UAL2", vm.HighlightedItem?.AircraftId);

        transport.PushItem(Sent("id2", "UAL2", 2));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("UAL2", vm.PdcItems.Single().AircraftId);
        Assert.Equal(TdlsAircraftList.Dcl, vm.ActiveList);
        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);
    }

    // ── After a send ──

    [AvaloniaFact]
    public async Task Send_MovesTheHighlightToTheNextDclRow()
    {
        VTdlsViewModel vm = PageWith(Pending("id1", "UAL1", 1), Pending("id2", "UAL2", 2), Pending("id3", "UAL3", 3));

        await SendRow(vm, "UAL2");

        Assert.Equal("UAL3", vm.HighlightedItem?.AircraftId);
        Assert.Equal(TdlsAircraftList.Dcl, vm.ActiveList);
    }

    [AvaloniaFact]
    public async Task Send_OfTheLastDclRow_HighlightsTheFirst()
    {
        VTdlsViewModel vm = PageWith(Pending("id1", "UAL1", 1), Pending("id2", "UAL2", 2));

        await SendRow(vm, "UAL2");

        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);
    }

    [AvaloniaFact]
    public async Task Send_OfTheOnlyDclRow_HighlightsNothingOnceItIsSent_ThenTheNextArrival()
    {
        var transport = new FakeTdlsTransport();
        VTdlsViewModel vm = Page(transport, Succeed, Pending("id1", "UAL1", 1));

        await SendRow(vm, "UAL1");
        transport.PushItem(Sent("id1", "UAL1", 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.DclItems);
        Assert.Null(vm.HighlightedItem);

        transport.PushItem(Pending("id2", "UAL2", 2));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("UAL2", vm.HighlightedItem?.AircraftId);
    }

    /// <summary>The neighbour taken at send time can leave the list before the server answers; the highlight never lands on it.</summary>
    [AvaloniaFact]
    public async Task Send_WhoseNextRowIsRemovedMeanwhile_HighlightsAListedDclRow()
    {
        var transport = new FakeTdlsTransport();
        VTdlsViewModel vm = Page(
            transport,
            () =>
            {
                transport.PushRemoved(new TdlsItemRemovedDto("id3", "OAK", "UAL3", Dumped: true));
                Dispatcher.UIThread.RunJobs();
                return new CommandResultDto(true, null);
            },
            Pending("id1", "UAL1", 1),
            Pending("id2", "UAL2", 2),
            Pending("id3", "UAL3", 3)
        );

        await SendRow(vm, "UAL2");

        Assert.Equal(["UAL1", "UAL2"], vm.DclItems.Select(i => i.AircraftId));
        Assert.Equal("UAL2", vm.HighlightedItem?.AircraftId);
    }

    [AvaloniaFact]
    public async Task FailedSend_LeavesTheHighlightOnTheSentRow()
    {
        VTdlsViewModel vm = Page(
            new FakeTdlsTransport(),
            () => new CommandResultDto(false, "rejected"),
            Pending("id1", "UAL1", 1),
            Pending("id2", "UAL2", 2)
        );

        await SendRow(vm, "UAL1");

        Assert.NotNull(vm.CommandError);
        Assert.Equal("UAL1", vm.HighlightedItem?.AircraftId);
    }

    // ── Facility cycling ──

    [AvaloniaFact]
    public async Task CycleFacility_FromAFacilityNotInTheList_GoesToTheFirst()
    {
        var transport = new FakeTdlsTransport
        {
            AccessibleFacilities = [new AccessibleFacilityDto("IAD", "Dulles", true), new AccessibleFacilityDto("DCA", "National", false)],
        };
        VTdlsViewModel vm = Page(transport, Succeed);
        Assert.Equal("OAK", vm.FacilityId);

        await vm.CycleFacilityAsync(1);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("IAD", vm.FacilityId);
    }

    /// <summary>Opens a DCL row and sends its clearance.</summary>
    private static async Task SendRow(VTdlsViewModel vm, string callsign)
    {
        vm.SelectedItem = vm.DclItems.Single(i => i.AircraftId == callsign);
        Dispatcher.UIThread.RunJobs();
        TdlsFlightPlanEditorViewModel editor = Assert.IsType<TdlsFlightPlanEditorViewModel>(vm.Editor);
        Assert.True(editor.IsSendEnabled);
        await editor.SendCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static TdlsConfigDto Config() =>
        new(
            FacilityId: "OAK",
            FacilityName: "Oakland ATCT",
            MandatorySid: false,
            MandatoryClimbout: false,
            MandatoryClimbvia: false,
            MandatoryInitialAlt: false,
            MandatoryDepFreq: false,
            MandatoryExpect: false,
            MandatoryContactInfo: false,
            MandatoryLocalInfo: false,
            Sids:
            [
                new TdlsSidDto(
                    "OAKLAND4",
                    "OAKLAND4",
                    [new TdlsSidTransitionDto("ALTAM", "ALTAMONT", "ALTAM", null, null, null, null, DefaultDepFreq: "120.9", null, null)]
                ),
            ],
            Climbouts: [],
            Climbvias: [],
            InitialAlts: [],
            DepFreqs: [new TdlsClearanceValueDto("120.9", "120.9")],
            Expects: [],
            ContactInfos: [],
            LocalInfos: [],
            DefaultSidId: "OAKLAND4",
            DefaultTransitionId: "ALTAM"
        );

    private static CommandResultDto? Succeed() => new(true, null);

    /// <summary>A view-model on OAK's page listing <paramref name="items"/>, whose every command succeeds.</summary>
    private static VTdlsViewModel PageWith(params TdlsItemDto[] items) => Page(new FakeTdlsTransport(), Succeed, items);

    /// <summary>A view-model on OAK's page listing <paramref name="items"/>; <paramref name="onCommand"/> answers each command.</summary>
    private static VTdlsViewModel Page(FakeTdlsTransport transport, Func<CommandResultDto?> onCommand, params TdlsItemDto[] items)
    {
        var vm = new VTdlsViewModel(transport, (_, _, _) => Task.FromResult(onCommand()), getUserInitials: null);
        TdlsConfigDto config = Config();
        vm.ApplyFacilityView(config.FacilityId, new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config], StudentFrequencyMhz: null));
        transport.Push(new TdlsStateDto([.. items], []));
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static TdlsItemDto Pending(string id, string callsign, int sequence) => Item(id, callsign, sequence, TdlsStatus.Pending, null);

    private static TdlsItemDto Sent(string id, string callsign, int sequence) =>
        Item(id, callsign, sequence, TdlsStatus.Sent, new ClearanceDto(null, "OAKLAND4", "ALTAM", null, null, null, null, null, "120.9"));

    private static TdlsItemDto Item(string id, string callsign, int sequence, TdlsStatus status, ClearanceDto? sent) =>
        new(
            id,
            callsign,
            Cid: null,
            FacilityId: "OAK",
            status,
            Sequence: sequence,
            CreatedUtc: default,
            SentUtc: null,
            WilcoUtc: null,
            ExpiresUtc: default,
            SentPayload: sent,
            FlightPlan: null
        );
}
