using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Pins the contract of <c>MainViewModel.OpenStripsEntryForFacilityAsync</c> —
/// the underlying command the View → Strips → "New Strips Tab..." menu picker
/// invokes once the user picks a facility. The picker itself (parent-submenu
/// wiring in <c>MainWindow.axaml.cs::OnNewStripsTabSubmenuOpened</c>) is
/// verified manually because there is no headless harness for nested
/// MenuFlyout open/close timing in this repo.
/// </summary>
public class MainViewModelStripsTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    [AvaloniaFact]
    public async Task OpenStripsEntryForFacilityAsync_NullId_DoesNothing()
    {
        var vm = NewVm();
        var startCount = vm.StripsEntries.Count;

        await vm.OpenStripsEntryForFacilityAsync(null!);

        Assert.Equal(startCount, vm.StripsEntries.Count);
    }

    [AvaloniaFact]
    public async Task OpenStripsEntryForFacilityAsync_EmptyId_DoesNothing()
    {
        var vm = NewVm();
        var startCount = vm.StripsEntries.Count;

        await vm.OpenStripsEntryForFacilityAsync("");

        Assert.Equal(startCount, vm.StripsEntries.Count);
    }

    [AvaloniaFact]
    public async Task OpenStripsEntryForFacilityAsync_ExistingFacility_AppendsDuplicateEntry()
    {
        // Re-invoking the command for a facility that already has a tab opens
        // a second independent view of that facility (e.g. to monitor two
        // bays side by side), mirroring how the browser client can open the
        // same facility in two windows. The existing entry's pop-out state is
        // left alone.
        var vm = NewVm();
        var studentEntry = vm.StripsEntries[0];
        studentEntry.Vm.FacilityId = "OAK";
        studentEntry.IsPoppedOut = true;
        var startCount = vm.StripsEntries.Count;

        await vm.OpenStripsEntryForFacilityAsync("OAK");

        Assert.Equal(startCount + 1, vm.StripsEntries.Count);
        Assert.True(studentEntry.IsPoppedOut);
        Assert.False(vm.StripsEntries[^1].IsStudentEntry);
    }

    [AvaloniaFact]
    public async Task DuplicateFacilityEntries_GetOrdinalSuffixes_AndHealOnClose()
    {
        // Two entries on the same facility must be distinguishable: the
        // second (and later) same-facility tabs get a " #n" title suffix.
        // Closing a duplicate recomputes the ordinals so a now-unique entry
        // returns to the clean title.
        var vm = NewVm();
        var studentEntry = vm.StripsEntries[0];
        studentEntry.Vm.FacilityId = "OAK";
        studentEntry.Vm.FacilityName = "Oakland Intl ATCT";

        await vm.OpenStripsEntryForFacilityAsync("OAK");
        var duplicate = vm.StripsEntries[^1];
        // The headless RPC fails (no server), so assign the facility the way
        // SwitchFacilityAsync would have — this also exercises the
        // FacilityId-change → recompute path.
        duplicate.Vm.FacilityId = "OAK";
        duplicate.Vm.FacilityName = "Oakland Intl ATCT";

        Assert.Equal("OAK - vStrips", studentEntry.TabTitle);
        Assert.Equal("OAK - vStrips #2", duplicate.TabTitle);

        vm.CloseStripsEntry(duplicate);

        Assert.Equal("OAK - vStrips", studentEntry.TabTitle);
    }

    [AvaloniaFact]
    public async Task OpenStripsEntryForFacilityAsync_NewFacility_AppendsNonStudentEntry()
    {
        // For an unknown facility, the command always appends a new entry
        // BEFORE it awaits the SwitchFacilityAsync RPC. The RPC swallows
        // exceptions, so even with no live server the entry persists in
        // StripsEntries — that's what we assert here. Bay/facility wiring
        // is exercised separately by VStripsViewModelTests.
        var vm = NewVm();
        var startCount = vm.StripsEntries.Count;

        await vm.OpenStripsEntryForFacilityAsync("NCT");

        Assert.Equal(startCount + 1, vm.StripsEntries.Count);
        var added = vm.StripsEntries[^1];
        Assert.False(added.IsStudentEntry);
        Assert.False(added.IsPoppedOut);
    }

    [AvaloniaFact]
    public async Task SplitStripsEntry_CreatesSecondaryVm_AndUnsplitDiscardsIt()
    {
        var vm = NewVm();
        var entry = vm.StripsEntries[0];
        // The student entry's split persists in preferences, which the test
        // process shares across tests — establish a clean baseline instead of
        // assuming one.
        vm.UnsplitStripsEntry(entry);
        Assert.Equal(StripsSplitMode.None, entry.SplitMode);
        Assert.Null(entry.SecondaryVm);

        await vm.SplitStripsEntryAsync(entry, StripsSplitMode.SideBySide);

        Assert.Equal(StripsSplitMode.SideBySide, entry.SplitMode);
        Assert.NotNull(entry.SecondaryVm);

        // Re-orienting keeps the same secondary pane (and its bay selection).
        var secondary = entry.SecondaryVm;
        await vm.SplitStripsEntryAsync(entry, StripsSplitMode.Stacked);
        Assert.Equal(StripsSplitMode.Stacked, entry.SplitMode);
        Assert.Same(secondary, entry.SecondaryVm);

        vm.UnsplitStripsEntry(entry);
        Assert.Equal(StripsSplitMode.None, entry.SplitMode);
        Assert.Null(entry.SecondaryVm);
    }

    private static readonly FlightStripsConfigDto OakConfig = new(
        FacilityId: "OAK",
        FacilityName: "Oakland Intl ATCT",
        Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 1, "OAK")],
        SeparatorsLocked: false,
        UnderlyingAirports: ["OAK"],
        EnableArrivalStrips: true,
        EnableSeparateArrDepPrinters: true
    );

    /// <summary>
    /// Simulates the broadcasts a live session's student VM has already
    /// received: bay config, one departure strip racked in GROUND, and a
    /// KOAK METAR.
    /// </summary>
    private static void SeedLiveSession(VStripsViewModel student)
    {
        student.SetConnected(true);
        student.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();
        student.ReconcileItems([new StripItemDto("S1", "UAL100", false, StripItemType.DepartureStrip, false, ["UAL100", "", "B738/L"])]);
        student.ReconcileFullState(
            new FlightStripsStateDto(
                [],
                [
                    new StripBayContentsDto(
                        "bay-gnd",
                        [
                            ["S1"],
                        ]
                    ),
                ],
                false,
                false,
                null,
                null
            )
        );
        student.ApplyMetars(["KOAK 061819Z AUTO 00000KT 10SM CLR A2992"]);
        Assert.True(student.HasMetars);
        Assert.Single(student.Bays.Single(b => b.BayId == "bay-gnd").Racks[0].Strips);
    }

    private static void AssertSeeded(VStripsViewModel view)
    {
        // The offline test can't run the RPC-based facility switch, so apply
        // the bay config directly the way SwitchFacilityAsync would have; the
        // seeded caches must then populate the racks (same re-apply path a
        // late-arriving room join uses).
        view.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();

        var rack = view.Bays.Single(b => b.BayId == "bay-gnd").Racks[0];
        var strip = Assert.Single(rack.Strips);
        Assert.Equal("S1", strip.Id);
        Assert.True(view.HasMetars);
        Assert.Contains("KOAK", view.PrimaryMetar!.Raw);
    }

    [AvaloniaFact]
    public async Task SplitSecondaryPane_SeedsCurrentStripsAndMetars()
    {
        // A pane created mid-session must show the strips and METARs the
        // session already has — state and METARs are broadcast-only, so
        // without seeding the new pane sits empty (and its header misaligns
        // with the primary's) until the next server change.
        var vm = NewVm();
        var entry = vm.StripsEntries[0];
        vm.UnsplitStripsEntry(entry);
        SeedLiveSession(entry.Vm);

        await vm.SplitStripsEntryAsync(entry, StripsSplitMode.SideBySide);

        AssertSeeded(entry.SecondaryVm!);
    }

    [AvaloniaFact]
    public async Task DuplicateFacilityTab_SeedsCurrentStripsAndMetars()
    {
        // Same seeding contract for a duplicate facility tab opened mid-session.
        var vm = NewVm();
        var entry = vm.StripsEntries[0];
        vm.UnsplitStripsEntry(entry);
        SeedLiveSession(entry.Vm);

        await vm.OpenStripsEntryForFacilityAsync("OAK");

        AssertSeeded(vm.StripsEntries[^1].Vm);
    }

    private static StripItemDto Departure(string id, string callsign) =>
        new(id, callsign, false, StripItemType.DepartureStrip, false, [callsign, "", "B738/L"]);

    private static FlightStripsStateDto GroundBayState(params string[] rackIds) =>
        new([], [new StripBayContentsDto("bay-gnd", [rackIds])], false, false, null, null);

    [AvaloniaFact]
    public async Task SplitSecondaryPane_SeedsStripsFromMultipleItemBroadcasts()
    {
        // Issue #366: item broadcasts are incremental deltas — each strip's
        // full payload typically arrives once, when it's printed. A pane
        // created mid-session must be seeded with every strip the peer has
        // accumulated, not just whatever the most recent delta contained.
        var vm = NewVm();
        var entry = vm.StripsEntries[0];
        vm.UnsplitStripsEntry(entry);
        var student = entry.Vm;
        student.SetConnected(true);
        student.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();
        student.ReconcileItems([Departure("S1", "UAL100")]);
        student.ReconcileItems([Departure("S2", "SWA200")]);
        student.ReconcileFullState(GroundBayState("S1", "S2"));

        await vm.SplitStripsEntryAsync(entry, StripsSplitMode.SideBySide);
        var secondary = entry.SecondaryVm!;
        secondary.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();

        var rack = secondary.Bays.Single(b => b.BayId == "bay-gnd").Racks[0];
        Assert.Equal(new[] { "S1", "S2" }, rack.Strips.Select(s => s.Id).ToArray());
    }

    [AvaloniaFact]
    public async Task SplitSecondaryPane_DoesNotResurrectDeletedStrips()
    {
        // A strip deleted before the split (full state no longer references
        // it) must not reappear in the seeded pane just because its DTO was
        // once broadcast.
        var vm = NewVm();
        var entry = vm.StripsEntries[0];
        vm.UnsplitStripsEntry(entry);
        var student = entry.Vm;
        student.SetConnected(true);
        student.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();
        student.ReconcileItems([Departure("S1", "UAL100")]);
        student.ReconcileItems([Departure("S2", "SWA200")]);
        student.ReconcileFullState(GroundBayState("S2"));

        await vm.SplitStripsEntryAsync(entry, StripsSplitMode.SideBySide);
        var secondary = entry.SecondaryVm!;
        secondary.ApplyBayConfig(OakConfig);
        Dispatcher.UIThread.RunJobs();

        var rack = secondary.Bays.Single(b => b.BayId == "bay-gnd").Racks[0];
        Assert.Equal(new[] { "S2" }, rack.Strips.Select(s => s.Id).ToArray());
    }

    [AvaloniaFact]
    public void SplitRatio_IsClampedToUsableRange()
    {
        var entry = new VStripsDockEntryViewModel(NewVm().StripsEntries[0].Vm, isStudentEntry: false);

        entry.SplitRatio = 0.01;
        Assert.Equal(0.15, entry.SplitRatio);

        entry.SplitRatio = 0.99;
        Assert.Equal(0.85, entry.SplitRatio);

        entry.SplitRatio = 0.6;
        Assert.Equal(0.6, entry.SplitRatio);
    }
}
