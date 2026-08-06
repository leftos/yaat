using Avalonia.Headless.XUnit;
using Xunit;
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

        Assert.Equal("Strips (Oakland Intl ATCT)", studentEntry.TabTitle);
        Assert.Equal("Strips (Oakland Intl ATCT) #2", duplicate.TabTitle);

        vm.CloseStripsEntry(duplicate);

        Assert.Equal("Strips (Oakland Intl ATCT)", studentEntry.TabTitle);
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
