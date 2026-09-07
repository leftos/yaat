using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Strips and PDCs are pushed state: nothing retracts them when the room they belong to goes away, so leaving a
/// room has to empty the views itself (#424). <see cref="MainViewModel.ClearRoomState"/> reaches every open
/// instance — the student tab, extra facility tabs, and the popped-out windows, which share those same VMs.
/// </summary>
public class MainViewModelLeaveRoomViewsTests
{
    private static readonly FlightStripsConfigDto OakConfig = new(
        FacilityId: "OAK",
        FacilityName: "Oakland Intl ATCT",
        Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 1, "OAK")],
        SeparatorsLocked: false,
        UnderlyingAirports: ["OAK"],
        EnableArrivalStrips: true,
        EnableSeparateArrDepPrinters: true
    );

    private static TdlsItemDto TdlsItem(string id, string callsign) =>
        new(
            id,
            callsign,
            Cid: null,
            FacilityId: "OAK",
            TdlsStatus.Pending,
            Sequence: 0,
            CreatedUtc: default,
            SentUtc: null,
            WilcoUtc: null,
            ExpiresUtc: default,
            SentPayload: null,
            FlightPlan: null
        );

    /// <summary>Feeds a strips VM the broadcasts a live session gives it: bay config, one racked strip, one waiting in the printer.</summary>
    private static void SeedStrips(VStripsViewModel strips, string stripId, string callsign)
    {
        var printerId = stripId + "_PRINTER";
        strips.SetConnected(true);
        strips.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();
        strips.ReconcileItems([
            new StripItemDto(stripId, callsign, false, StripItemType.DepartureStrip, false, [callsign, "", "B738/L"]),
            new StripItemDto(printerId, callsign, false, StripItemType.DepartureStrip, false, [callsign, "", "B738/L"]),
        ]);
        strips.ReconcileFullState(
            new FlightStripsStateDto(
                [printerId],
                [
                    new StripBayContentsDto(
                        "bay-gnd",
                        [
                            [stripId],
                        ]
                    ),
                ],
                false,
                false,
                null,
                null
            )
        );
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(strips.ItemsByIdForTests);
        Assert.NotEqual(0, strips.Printer.PendingCount);
        Assert.Single(strips.Bays.Single(b => b.BayId == "bay-gnd").Racks[0].Strips);
    }

    private static void SeedTdls(VTdlsViewModel tdls, string itemId, string callsign)
    {
        tdls.SetConnected(true);
        var item = new TdlsItemViewModel(TdlsItem(itemId, callsign));
        tdls.DclItems.Add(item);
        tdls.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertStripsEmpty(VStripsViewModel strips)
    {
        Assert.Empty(strips.Bays);
        Assert.Empty(strips.ItemsByIdForTests);
        Assert.Equal(0, strips.Printer.PendingCount);
        Assert.Null(strips.FacilityId);
    }

    private static void AssertTdlsEmpty(VTdlsViewModel tdls)
    {
        Assert.Empty(tdls.DclItems);
        Assert.Empty(tdls.PdcItems);
        Assert.Null(tdls.SelectedItem);
        Assert.Null(tdls.Editor);
    }

    [AvaloniaFact]
    public async Task ClearRoomState_EmptiesEveryStripsAndTdlsView()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        // A second facility tab of each kind: the clear must reach the extra instances, not just index 0.
        await vm.OpenStripsEntryForFacilityAsync("NCT");
        await vm.OpenTdlsEntryForFacilityAsync("NCT");
        Assert.Equal(2, vm.StripsEntries.Count);
        Assert.Equal(2, vm.TdlsEntries.Count);

        SeedStrips(vm.StripsEntries[0].Vm, "S1", "UAL100");
        SeedStrips(vm.StripsEntries[1].Vm, "S2", "SWA200");
        SeedTdls(vm.TdlsEntries[0].Vm, "T1", "UAL100");
        SeedTdls(vm.TdlsEntries[1].Vm, "T2", "SWA200");

        vm.ClearRoomState();
        Dispatcher.UIThread.RunJobs();

        foreach (var entry in vm.StripsEntries)
        {
            AssertStripsEmpty(entry.Vm);
        }
        foreach (var entry in vm.TdlsEntries)
        {
            AssertTdlsEmpty(entry.Vm);
        }
    }

    [AvaloniaFact]
    public void ClearRoomState_DropsCachedBroadcastsSoTheNextRoomStartsEmpty()
    {
        // The strips VM caches the broadcasts it received so a bay config arriving late still renders them.
        // Re-applying a config after the clear must therefore show empty racks, not the left room's strips.
        var vm = new MainViewModel(new FakeFilePickerService());
        var strips = vm.StripsEntries[0].Vm;
        SeedStrips(strips, "S1", "UAL100");

        vm.ClearRoomState();
        Dispatcher.UIThread.RunJobs();

        strips.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(strips.Bays.Single(b => b.BayId == "bay-gnd").Racks[0].Strips);
        Assert.Equal(0, strips.Printer.PendingCount);
    }

    [AvaloniaFact]
    public async Task ClearScenarioState_EmptiesTheStripsButKeepsALinkedFacilityTabsScope()
    {
        // A scenario unload inside the room is not a room exit. Nothing re-bootstraps a linked-facility tab
        // when the next scenario loads (MainViewModel.Strips.cs builds it with autoBootstrapFromScenarioLoaded:
        // false), so dropping its facility here would leave it blank for the rest of the session — only the
        // content the unloaded scenario put in it goes.
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.OpenStripsEntryForFacilityAsync("NCT");
        var linked = vm.StripsEntries[1].Vm;
        SeedStrips(linked, "S2", "SWA200");
        SeedTdls(vm.TdlsEntries[0].Vm, "T1", "UAL100");

        vm.ClearScenarioState();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(linked.ItemsByIdForTests);
        Assert.Equal(0, linked.Printer.PendingCount);
        Assert.Empty(linked.Bays.Single(b => b.BayId == "bay-gnd").Racks[0].Strips);

        // Kept: the facility scope and the bay layout the tab was opened on.
        Assert.Equal("OAK", linked.FacilityId);
        Assert.NotEmpty(linked.Bays);

        AssertTdlsEmpty(vm.TdlsEntries[0].Vm);
    }

    [AvaloniaFact]
    public void TdlsClear_EmptiesTheItemsAndTheEditor()
    {
        // VM-level: the items arrive as a TdlsStateChanged, which is the only path that populates the
        // page's id lookup — Clear has to drop that too, or a stale VM outlives the room it belonged to.
        var transport = new FakeTdlsTransport();
        var tdls = new VTdlsViewModel(transport, (_, _, _) => Task.CompletedTask, getUserInitials: null);

        transport.PushState(new TdlsStateDto([TdlsItem("T1", "UAL100"), TdlsItem("T2", "SWA200")], []));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, tdls.DclItems.Count);
        tdls.SelectedItem = tdls.DclItems[0];

        tdls.Clear();
        Dispatcher.UIThread.RunJobs();

        AssertTdlsEmpty(tdls);
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
