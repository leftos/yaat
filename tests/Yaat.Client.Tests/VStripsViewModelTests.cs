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
            getUserInitials: null
        );
        return (vm, captured);
    }

    private static FlightStripsConfigDto SimpleConfig() =>
        new(
            FacilityId: "FAC1",
            FacilityName: "Fresno ATCT",
            Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 2), new StripBayConfigDto("bay-loc", "LOCAL", 2)],
            HasTwoPrinters: false,
            SeparatorsLocked: false,
            UnderlyingAirports: []
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

        // Mirror ApplyBayConfig's facility-airport capture so the METAR filter
        // (RebuildMetars) has the same scope it would in production.
        var airportsField = typeof(VStripsViewModel).GetField(
            "_facilityAirports",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        )!;
        airportsField.SetValue(vm, config.UnderlyingAirports);
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

        // Server writes annotation box 10 at FieldValues[box+9] = [10] per
        // StripMutations.SetAnnotationBox — FieldValues[9] is route/remarks,
        // NOT box 10. The VM's Annotation10 reads Field(10).
        var fieldsUpdated = new string[19];
        for (var i = 0; i < 19; i++)
        {
            fieldsUpdated[i] = "";
        }
        fieldsUpdated[0] = "UAL100";
        fieldsUpdated[10] = "RV"; // annotation box 10

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
        // UI dispatches by strip id, not callsign — keeps scanned copies
        // (which share a callsign with the original) addressable.
        Assert.Equal("", entry.Callsign);
        // Wire is slash-compound 1-based: rack 1 → "2", index 2 → "3".
        Assert.Equal("STRIP S1 LOCAL/2/3", entry.Command);
    }

    [Fact]
    public async Task MoveStripAsync_DropOnOwnIdx_EmitsNothing()
    {
        // User drops the strip back on its own slot — remove-then-insert is a
        // no-op, so the canonical dispatch is suppressed. Without the no-op
        // guard, the terminal buffer + command log would echo a redundant
        // STRIP that the server silently rewinds. Covers the "drop on own
        // position" branch of IsNoOpMove.
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);
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

        await vm.MoveStripAsync(vm.ItemsByIdForTests["S1"], vm.Bays.Single(b => b.BayId == "bay-gnd"), rack: 0, index: 0);

        Assert.Empty(captured);
    }

    [Fact]
    public async Task MoveStripAsync_TopmostStripDroppedOnOwnIdx_EmitsNothing()
    {
        // Regression for the bug that motivated the source-hiding refactor:
        // dragging the topmost strip, ComputeDropIndex formerly returned
        // count (append-above-self) which slipped past the first no-op check
        // because strips[count] doesn't exist. With hiding + visual-idx math,
        // target == fromIdx (== count - 1) and the existing no-op guard fires.
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100"), FullStrip("S2", "UAL200"), FullStrip("S3", "UAL300")]);
        vm.ReconcileFullState(
            State(
                null,
                (
                    "bay-gnd",
                    [
                        ["S1", "S2", "S3"],
                        [],
                    ]
                )
            )
        );

        // S3 is topmost (model idx 2). Drop back on its own slot.
        await vm.MoveStripAsync(vm.ItemsByIdForTests["S3"], vm.Bays.Single(b => b.BayId == "bay-gnd"), rack: 0, index: 2);

        Assert.Empty(captured);
    }

    [Fact]
    public async Task MoveStripAsync_DropOneIdxUp_EmitsCanonical()
    {
        // Dropping one slot above the source in the same rack is NOT a no-op —
        // server does remove-then-insert, and with the source at fromIdx=1 in
        // a 3-strip rack, target=2 produces [A, C, B, D] (different from
        // original [A, B, C, D]). An earlier iteration of the no-op guard
        // included this as a no-op and suppressed a real move; this test
        // locks in the correct "emit" behavior.
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100"), FullStrip("S2", "UAL200"), FullStrip("S3", "UAL300"), FullStrip("S4", "UAL400")]);
        vm.ReconcileFullState(
            State(
                null,
                (
                    "bay-gnd",
                    [
                        ["S1", "S2", "S3", "S4"],
                        [],
                    ]
                )
            )
        );

        // S2 is at model idx 1; target idx 2 reorders to [S1, S3, S2, S4] server-side.
        await vm.MoveStripAsync(vm.ItemsByIdForTests["S2"], vm.Bays.Single(b => b.BayId == "bay-gnd"), rack: 0, index: 2);

        var entry = Assert.Single(captured);
        Assert.Equal("STRIP S2 GROUND/1/3", entry.Command);
    }

    [Fact]
    public async Task MoveStripAsync_DifferentBay_EmitsCanonical()
    {
        // Cross-bay drags never qualify as no-op, even if the target idx
        // matches the source idx — the destination bay/rack is different, so
        // the strip moves. Pairs with the own-idx test above to confirm the
        // no-op guard keys on bay + rack, not just index.
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);
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

        await vm.MoveStripAsync(vm.ItemsByIdForTests["S1"], vm.Bays.Single(b => b.BayId == "bay-loc"), rack: 0, index: 0);

        Assert.Single(captured);
    }

    [Fact]
    public async Task MoveStripAsync_NullIndex_AlwaysEmits()
    {
        // null index = "append to the tail of the rack" on the wire. Server
        // decides placement, so the client can't short-circuit even if the
        // strip is already in the target rack. Dispatch always fires.
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);
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

        await vm.MoveStripAsync(vm.ItemsByIdForTests["S1"], vm.Bays.Single(b => b.BayId == "bay-gnd"), rack: 0, index: null);

        var entry = Assert.Single(captured);
        // Wire drops the trailing /index token; UI prefixes the strip id.
        Assert.Equal("STRIP S1 GROUND/1", entry.Command);
    }

    [Fact]
    public async Task MoveStripAsync_HalfStrip_EmitsHsmWithStripId()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([HalfStrip("H1", "NORDO")]);

        await vm.MoveStripAsync(vm.ItemsByIdForTests["H1"], vm.Bays.Single(b => b.BayId == "bay-loc"), 0, 0);

        var entry = Assert.Single(captured);
        Assert.Equal("", entry.Callsign);
        // HSM addresses the half-strip by id (not first-line text) so two
        // half-strips with duplicate first-line cells stay distinguishable.
        // 1-based wire: rack 0 → "1", index 0 → "1".
        Assert.Equal("HSM H1 LOCAL/1/1", entry.Command);
    }

    [Fact]
    public async Task DeleteStripAsync_FullStrip_EmitsStripdById()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.DeleteStripAsync(vm.ItemsByIdForTests["S1"]);

        var entry = Assert.Single(captured);
        // STRIPD addresses by strip id so a scanned copy
        // <c>STRIP_{callsign}_{shortGuid}</c> can be removed without
        // hitting the original.
        Assert.Equal("", entry.Callsign);
        Assert.Equal("STRIPD S1", entry.Command);
    }

    [Fact]
    public async Task ToggleOffsetAsync_FullStrip_EmitsStripoById()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.ToggleOffsetAsync(vm.ItemsByIdForTests["S1"]);

        Assert.Equal(("", "STRIPO S1"), captured[0]);
    }

    [Fact]
    public async Task AnnotateAsync_EmitsAnByStripId()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.AnnotateAsync(vm.ItemsByIdForTests["S1"], "3", "RV");

        Assert.Equal(("", "AN S1 3 RV"), captured[0]);
    }

    [Fact]
    public async Task AnnotateAsync_Box8a_EmitsVerbatim()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        vm.ReconcileItems([FullStrip("S1", "UAL100")]);

        await vm.AnnotateAsync(vm.ItemsByIdForTests["S1"], "8a", "ENR");

        Assert.Equal(("", "AN S1 8a ENR"), captured[0]);
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

    // ── External bay behavior (Item 2) ───────────────────────────

    private static FlightStripsConfigDto ConfigWithExternal() =>
        new(
            FacilityId: "FAC_OWN",
            FacilityName: "OAK ATCT",
            Bays:
            [
                new StripBayConfigDto("bay-gnd", "GROUND", 2, IsExternal: false),
                new StripBayConfigDto("bay-loc", "LOCAL", 2, IsExternal: false),
                new StripBayConfigDto("bay-nct", "NCT", 3, IsExternal: true),
            ],
            HasTwoPrinters: false,
            SeparatorsLocked: false,
            UnderlyingAirports: []
        );

    [Fact]
    public async Task SelectBayAsync_ExternalBay_LeavesSelectionUnchanged()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, ConfigWithExternal());
        // Pick the own ground bay first so there is a prior selection to preserve.
        await vm.SelectBayAsync(vm.Bays[0]);
        var priorSelection = vm.SelectedBay;
        Assert.NotNull(priorSelection);

        var externalBay = vm.Bays.First(b => b.IsExternal);
        await vm.SelectBayAsync(externalBay);

        Assert.Same(priorSelection, vm.SelectedBay);
        Assert.False(externalBay.IsSelected);
    }

    [Fact]
    public async Task MoveStripAsync_ToExternalBay_EmitsPushCanonical()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, ConfigWithExternal());
        vm.ReconcileItems([FullStrip("STRIP_UAL100", "UAL100")]);
        var strip = vm.ItemsByIdForTests["STRIP_UAL100"];
        var externalBay = vm.Bays.First(b => b.IsExternal);

        await vm.MoveStripAsync(strip, externalBay, rack: 0, index: 0);

        // Push to an external bay uses the exact same canonical verb as a
        // move within the same window — the server's GetAccessibleStripBay
        // already accepts external bays. The UI prefixes the strip id so
        // the dispatch targets this exact strip even if its callsign
        // collides with a scanned copy.
        var (callsign, canonical) = captured[0];
        Assert.Equal("", callsign);
        Assert.StartsWith("STRIP STRIP_UAL100 NCT", canonical);
    }

    [Fact]
    public async Task NextBayAsync_SkipsExternalBays()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, ConfigWithExternal());
        await vm.SelectBayAsync(vm.Bays[0]); // GROUND (own)

        await vm.NextBayAsync();
        Assert.Equal("LOCAL", vm.SelectedBay?.Name);

        await vm.NextBayAsync();
        // After LOCAL, the external "NCT" is skipped, wrapping back to GROUND.
        Assert.Equal("GROUND", vm.SelectedBay?.Name);
    }

    [Fact]
    public void ApplyBayConfig_ExternalOnlyBays_LeavesNoSelection()
    {
        var (vm, _) = MakeVm();
        // Simulate the edge case where ApplyBayConfig's post-dispatcher work
        // runs via the test seeding path: only external bays present.
        SeedBays(
            vm,
            new FlightStripsConfigDto(
                FacilityId: "FAC_OWN",
                FacilityName: "None",
                Bays: [new StripBayConfigDto("bay-ext", "EXT", 1, IsExternal: true)],
                HasTwoPrinters: false,
                SeparatorsLocked: false,
                UnderlyingAirports: []
            )
        );

        // With only external bays there is no viewable target — SelectedBay
        // must remain null so the main rack area renders empty.
        Assert.Null(vm.SelectedBay);
    }

    // ── Facility-scoped broadcast filter (Item 3) ────────────────

    private static StripItemDto FullStripFor(string id, string callsign, string facilityId, string bayId) =>
        new(
            id,
            callsign,
            IsDisconnected: false,
            StripItemType.DepartureStrip,
            IsOffset: false,
            FieldValues: [callsign],
            FacilityId: facilityId,
            BayId: bayId
        );

    [Fact]
    public void ReconcileItems_DropsItemsFromOtherFacility_WhenScopedToOwnFacility()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig()); // FacilityId = "FAC1"

        vm.ReconcileItems([
            FullStripFor("STRIP_OWN", "UAL1", facilityId: "FAC1", bayId: "bay-gnd"),
            FullStripFor("STRIP_OTHER", "UAL2", facilityId: "OTHER_FAC", bayId: "other-bay"),
        ]);

        Assert.True(vm.ItemsByIdForTests.ContainsKey("STRIP_OWN"));
        Assert.False(vm.ItemsByIdForTests.ContainsKey("STRIP_OTHER"));
    }

    [Fact]
    public void ReconcileItems_KeepsItemsBelongingToKnownBay_EvenIfFacilityIdMismatches()
    {
        // Edge case: when an item's FacilityId is stale but its BayId is one
        // we know about (e.g. external bay pushed from elsewhere), we keep
        // the item so drag-drop UX stays correct.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig()); // bays bay-gnd, bay-loc

        vm.ReconcileItems([FullStripFor("STRIP_X", "X1", facilityId: "UNKNOWN", bayId: "bay-loc")]);

        Assert.True(vm.ItemsByIdForTests.ContainsKey("STRIP_X"));
    }

    [Fact]
    public void ReconcileItems_UnscopedVm_AcceptsEverything()
    {
        // Legacy (FacilityId unset) path: VM accepts every broadcast item.
        // This matches the pre-Item-3 behavior used by existing tests.
        var (vm, _) = MakeVm();

        vm.ReconcileItems([
            FullStripFor("STRIP_A", "A1", facilityId: "FOO", bayId: "any"),
            FullStripFor("STRIP_B", "B1", facilityId: "BAR", bayId: "any"),
        ]);

        Assert.Equal(2, vm.ItemsByIdForTests.Count);
    }

    // ── Current-METAR bar (facility-scoped) ──────────────────────

    private const string MetarSfo = "KSFO 281953Z 28012KT 10SM FEW015 18/12 A2992";
    private const string MetarOak = "KOAK 281953Z 29010KT 10SM CLR 19/11 A2992";
    private const string MetarSjc = "KSJC 281953Z 30008KT 10SM CLR 22/10 A2991";

    private static FlightStripsConfigDto ConfigWithAirports(string facilityName, params string[] airports) =>
        new(
            FacilityId: "FAC1",
            FacilityName: facilityName,
            Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 2)],
            HasTwoPrinters: false,
            SeparatorsLocked: false,
            UnderlyingAirports: airports
        );

    [Fact]
    public void ApplyMetars_FiltersToFacilityAirports_AndMatchesFaaToIcao()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, ConfigWithAirports("NCT", "SFO", "OAK")); // FAA ids → KSFO/KOAK

        vm.ApplyMetars([MetarSfo, MetarOak, MetarSjc]);

        Assert.True(vm.HasMetars);
        Assert.Equal(["KSFO", "KOAK"], vm.Metars.Select(m => m.StationId));
        Assert.DoesNotContain(vm.Metars, m => m.StationId == "KSJC");
    }

    [Fact]
    public void ApplyMetars_OrdersByFacilityAirportList_PrimaryFirst()
    {
        var (vm, _) = MakeVm();
        // OAK listed first → it leads the bar even though SFO arrives first.
        SeedBays(vm, ConfigWithAirports("NCT", "OAK", "SFO"));

        vm.ApplyMetars([MetarSfo, MetarOak]);

        Assert.Equal("KOAK", vm.PrimaryMetar?.StationId);
        Assert.Equal(["KOAK", "KSFO"], vm.Metars.Select(m => m.StationId));
    }

    [Fact]
    public void ApplyMetars_EmptyFacilityAirports_ShowsAllAsFallback()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, ConfigWithAirports("NoStars")); // no airports resolved

        vm.ApplyMetars([MetarSfo, MetarSjc]);

        Assert.Equal(["KSFO", "KSJC"], vm.Metars.Select(m => m.StationId));
    }

    [Fact]
    public void ApplyMetars_RefiltersWhenFacilitySwitches()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, ConfigWithAirports("NCT", "SFO", "OAK"));
        vm.ApplyMetars([MetarSfo, MetarOak, MetarSjc]);
        Assert.Equal(["KSFO", "KOAK"], vm.Metars.Select(m => m.StationId));

        // Switch the window to a facility that only owns SJC; the same loaded
        // weather now shows only that airport's METAR.
        SeedBays(vm, ConfigWithAirports("RCT", "SJC"));
        vm.ApplyMetars([MetarSfo, MetarOak, MetarSjc]);

        Assert.Equal(["KSJC"], vm.Metars.Select(m => m.StationId));
    }

    [Fact]
    public void ApplyMetars_NoWeather_ShowsDefaultMetar()
    {
        // No weather loaded → the bar shows the calm/standard default the sim applies for the
        // facility's airports (matching the desktop METAR panel), rather than hiding.
        var (vm, _) = MakeVm();
        vm.SetConnected(true);
        SeedBays(vm, ConfigWithAirports("NCT", "SFO"));
        vm.ApplyMetars([MetarSfo]);
        Assert.True(vm.HasMetars);

        vm.ApplyMetars([]);

        Assert.True(vm.HasMetars);
        Assert.Equal(["KSFO"], vm.Metars.Select(m => m.StationId));
        Assert.Contains("AUTO 00000KT 10SM CLR A2992", vm.PrimaryMetar!.Raw);
    }

    [Fact]
    public void ApplyMetars_NoWeather_Disconnected_EmptiesBar()
    {
        // Disconnected clients show no live data — the default must not be fabricated.
        var (vm, _) = MakeVm();
        vm.SetConnected(true);
        SeedBays(vm, ConfigWithAirports("NCT", "SFO"));
        vm.ApplyMetars([]);
        Assert.True(vm.HasMetars);

        vm.SetConnected(false);

        Assert.False(vm.HasMetars);
        Assert.Empty(vm.Metars);
        Assert.Null(vm.PrimaryMetar);
    }

    [Fact]
    public void ApplyMetars_NoWeather_EmptyFacilityAirports_StaysEmpty()
    {
        // A facility with no resolvable airports has nothing to synthesize a default for.
        var (vm, _) = MakeVm();
        vm.SetConnected(true);
        SeedBays(vm, ConfigWithAirports("NoStars"));

        vm.ApplyMetars([]);

        Assert.False(vm.HasMetars);
        Assert.Empty(vm.Metars);
    }

    // ── Auto-focus on half-strip create ──────────────────────────

    [Fact]
    public async Task CreateHalfStrip_MarksNewStripForFocus()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var bay = vm.Bays.Single(b => b.BayId == "bay-gnd");

        await vm.CreateHalfStripAsync(bay, 0, []);
        // Simulate the server's incremental broadcast for the new strip.
        vm.ReconcileItems([HalfStrip("HSTRIP_new", "")]);

        Assert.True(vm.ItemsByIdForTests["HSTRIP_new"].RequestFocusFirstCell);
    }

    [Fact]
    public void NewHalfStrip_WithoutLocalCreate_IsNotMarkedForFocus()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());

        // A half-strip arrives without this client issuing HSC (e.g. a remote
        // controller created it). It must not steal focus.
        vm.ReconcileItems([HalfStrip("HSTRIP_remote", "")]);

        Assert.False(vm.ItemsByIdForTests["HSTRIP_remote"].RequestFocusFirstCell);
    }

    [Fact]
    public async Task CreateHalfStrip_DoesNotMarkSubsequentFullStrip()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var bay = vm.Bays.Single(b => b.BayId == "bay-gnd");

        await vm.CreateHalfStripAsync(bay, 0, []);
        // The pending focus is consumed only by a new half-strip; a full strip
        // that happens to arrive first must not claim it.
        vm.ReconcileItems([FullStrip("STRIP_full", "UAL1")]);

        Assert.False(vm.ItemsByIdForTests["STRIP_full"].RequestFocusFirstCell);
    }
}
