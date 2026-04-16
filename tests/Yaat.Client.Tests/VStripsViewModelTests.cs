using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Unit coverage for <see cref="VStripsViewModel"/> reconciliation logic. All tests
/// drive state synchronously by calling <see cref="VStripsViewModel.ApplyBayConfig"/>,
/// <see cref="VStripsViewModel.ReconcileItems"/>, and
/// <see cref="VStripsViewModel.ReconcileFullState"/> directly, avoiding the Avalonia
/// dispatcher. Command-emitting helpers use a capture delegate to assert the exact
/// canonical strings the VM would send to the server.
/// </summary>
public class VStripsViewModelTests
{
    // ── Helpers ──────────────────────────────────────────────────

    private static ServerConnection FakeConnection() => new();

    private static (VStripsViewModel Vm, List<(string Callsign, string Command)> Captured) MakeVm()
    {
        var captured = new List<(string, string)>();
        var vm = new VStripsViewModel(
            FakeConnection(),
            sendCommand: (cs, cmd, _) =>
            {
                captured.Add((cs, cmd));
                return Task.CompletedTask;
            },
            preferences: null
        );
        return (vm, captured);
    }

    private static FlightStripsConfigDto SimpleConfig() =>
        new(
            FacilityId: "FAC1",
            FacilityName: "Fresno ATCT",
            Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 2), new StripBayConfigDto("bay-loc", "LOCAL", 2)],
            HasTwoPrinters: false,
            SeparatorsLocked: false
        );

    private static StripItemDto FullStrip(string id, string callsign) =>
        new(id, callsign, IsDisconnected: false, StripItemType.DepartureStrip, IsOffset: false, FieldValues: [callsign, "", "B738/L"]);

    private static StripItemDto HalfStrip(string id, string firstLine) =>
        new(id, AircraftId: null, IsDisconnected: false, StripItemType.HalfStripLeft, IsOffset: false, FieldValues: [firstLine]);

    private static FlightStripsStateDto State(string[]? printer = null, params (string BayId, string[][] Racks)[] bays) =>
        new(
            PrinterItems: printer ?? [],
            BayItems: bays.Select(b => new StripBayContentsDto(b.BayId, b.Racks)).ToArray(),
            NewItemInPrinter: false,
            NewItemInArrivalPrinter: false,
            NewItemInBayId: null,
            ItemMovedOrCreatedBySessionId: null
        );

    // Workaround: ApplyBayConfig posts to the Avalonia dispatcher. Tests that
    // don't have Avalonia initialized must use the synchronous ReconcileItems /
    // ReconcileFullState paths directly — ApplyBayConfig is exercised through
    // a dedicated Avalonia-aware harness in the view-level tests.
    private static void SeedBays(VStripsViewModel vm, FlightStripsConfigDto config)
    {
        // Use reflection to bypass the dispatcher hop in tests so the VM is in
        // a usable state without a real UI thread. This mirrors the pattern
        // used in other client VM unit tests for observable-collection setup.
        var baysField = typeof(VStripsViewModel).GetField(
            "_baysById",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        )!;
        var baysDict = (Dictionary<string, StripBayViewModel>)baysField.GetValue(vm)!;

        foreach (var bayDto in config.Bays)
        {
            var bayVm = new StripBayViewModel(bayDto);
            vm.Bays.Add(bayVm);
            baysDict[bayDto.Id] = bayVm;
        }

        vm.FacilityId = config.FacilityId;
        vm.FacilityName = config.FacilityName;
        vm.SeparatorsLocked = config.SeparatorsLocked;
    }

    // ── ReconcileItems ───────────────────────────────────────────

    [Fact]
    public void ReconcileItems_CreatesNewViewModelsForUnknownIds()
    {
        var (vm, _) = MakeVm();
        vm.ReconcileItems([FullStrip("S1", "UAL100"), FullStrip("S2", "UAL200")]);

        Assert.Equal(2, vm.ItemsByIdForTests.Count);
        Assert.Equal("UAL100", vm.ItemsByIdForTests["S1"].AircraftId);
        Assert.Equal("UAL200", vm.ItemsByIdForTests["S2"].AircraftId);
    }

    [Fact]
    public void ReconcileItems_UpdatesExistingInstanceInPlace()
    {
        var (vm, _) = MakeVm();
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);
        var original = vm.ItemsByIdForTests["S1"];

        var fieldsUpdated = new string[18];
        for (var i = 0; i < 18; i++)
        {
            fieldsUpdated[i] = "";
        }
        fieldsUpdated[0] = "UAL100";
        fieldsUpdated[9] = "RV"; // annotation box 10

        vm.ReconcileItems([new StripItemDto("S1", "UAL100", false, StripItemType.DepartureStrip, false, fieldsUpdated)]);

        // Same instance, updated fields — bindings stay stable.
        Assert.Same(original, vm.ItemsByIdForTests["S1"]);
        Assert.Equal("RV", original.Annotation10);
    }

    // ── ReconcileFullState ───────────────────────────────────────

    [Fact]
    public void ReconcileFullState_PlacesItemsInCorrectRacks()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());

        vm.ReconcileItems([FullStrip("S1", "UAL100"), FullStrip("S2", "UAL200")]);
        vm.ReconcileFullState(
            State(
                null,
                (
                    "bay-gnd",
                    [
                        ["S1", "S2"],
                        [],
                    ]
                ),
                (
                    "bay-loc",
                    [
                        [],
                        [],
                    ]
                )
            )
        );

        var groundBay = vm.Bays.Single(b => b.BayId == "bay-gnd");
        Assert.Equal(2, groundBay.Racks[0].Strips.Count);
        Assert.Equal("S1", groundBay.Racks[0].Strips[0].Id);
        Assert.Equal("S2", groundBay.Racks[0].Strips[1].Id);
        Assert.Empty(groundBay.Racks[1].Strips);
    }

    [Fact]
    public void ReconcileFullState_DropsStaleItems()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());

        vm.ReconcileItems([FullStrip("S1", "UAL100"), FullStrip("S2", "UAL200")]);
        // S2 is no longer referenced anywhere.
        vm.ReconcileFullState(
            State(
                null,
                (
                    "bay-gnd",
                    [
                        ["S1"],
                        [],
                    ]
                )
            )
        );

        Assert.True(vm.ItemsByIdForTests.ContainsKey("S1"));
        Assert.False(vm.ItemsByIdForTests.ContainsKey("S2"));
    }

    [Fact]
    public void ReconcileFullState_HasNewItemFlaggedForTargetBay()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        var state = new FlightStripsStateDto(
            PrinterItems: [],
            BayItems:
            [
                new StripBayContentsDto(
                    "bay-gnd",
                    [
                        ["S1"],
                        [],
                    ]
                ),
                new StripBayContentsDto(
                    "bay-loc",
                    [
                        [],
                        [],
                    ]
                ),
            ],
            NewItemInPrinter: false,
            NewItemInArrivalPrinter: false,
            NewItemInBayId: "bay-gnd",
            ItemMovedOrCreatedBySessionId: null
        );
        vm.ReconcileFullState(state);

        Assert.True(vm.Bays.Single(b => b.BayId == "bay-gnd").HasNewItem);
        Assert.False(vm.Bays.Single(b => b.BayId == "bay-loc").HasNewItem);
    }

    [Fact]
    public void ReconcileFullState_PrinterItems_PopulatePrinterQueue()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());

        vm.ReconcileItems([FullStrip("S1", "UAL100")]);
        vm.ReconcileFullState(State(printer: ["S1"]));

        Assert.Single(vm.Printer.Queue);
        Assert.Equal("S1", vm.Printer.Queue[0].Id);
    }

    // ── Command dispatch (canonical wire format) ─────────────────

    [Fact]
    public async Task MoveStripAsync_FullStrip_EmitsStripCanonical()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.MoveStripAsync(vm.ItemsByIdForTests["S1"], vm.Bays.Single(b => b.BayId == "bay-loc"), rack: 1, index: 2);

        var entry = Assert.Single(captured);
        Assert.Equal("UAL100", entry.Callsign);
        Assert.Equal("STRIP LOCAL 1 2", entry.Command);
    }

    [Fact]
    public async Task MoveStripAsync_HalfStrip_EmitsHsmWithKey()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([HalfStrip("H1", "NORDO")]);

        await vm.MoveStripAsync(vm.ItemsByIdForTests["H1"], vm.Bays.Single(b => b.BayId == "bay-loc"), 0, 0);

        var entry = Assert.Single(captured);
        Assert.Equal("", entry.Callsign);
        Assert.Equal("HSM NORDO LOCAL/0/0", entry.Command);
    }

    [Fact]
    public async Task DeleteStripAsync_FullStrip_EmitsStripd()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.DeleteStripAsync(vm.ItemsByIdForTests["S1"]);

        var entry = Assert.Single(captured);
        Assert.Equal("UAL100", entry.Callsign);
        Assert.Equal("STRIPD", entry.Command);
    }

    [Fact]
    public async Task ToggleOffsetAsync_FullStrip_EmitsStripo()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.ToggleOffsetAsync(vm.ItemsByIdForTests["S1"]);

        Assert.Equal(("UAL100", "STRIPO"), captured[0]);
    }

    [Fact]
    public async Task AnnotateAsync_EmitsAnOnAircraftCallsign()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.AnnotateAsync(vm.ItemsByIdForTests["S1"], 3, "RV");

        Assert.Equal(("UAL100", "AN 3 RV"), captured[0]);
    }

    [Fact]
    public async Task CreateSeparatorAsync_WhenLocked_DoesNothing()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.SeparatorsLocked = true;

        await vm.CreateSeparatorAsync(SeparatorStyle.White, vm.Bays[0], 0, 0, "HOLD");

        Assert.Empty(captured);
    }

    [Fact]
    public async Task CreateBlankAsync_NullBay_EmitsBareBlank()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());

        await vm.CreateBlankAsync(null, null, null);

        Assert.Equal(("", "BLANK"), captured[0]);
    }
}
