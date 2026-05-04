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
    public async Task OpenStripsEntryForFacilityAsync_ExistingFacility_RedocksAndAddsNoEntry()
    {
        // Re-invoking the command for a facility that already has a tab is the
        // idempotent "bring back from popped-out" path. Must not append a
        // duplicate entry, and must dock an existing popped-out entry.
        var vm = NewVm();
        var studentEntry = vm.StripsEntries[0];
        studentEntry.Vm.FacilityId = "OAK";
        studentEntry.IsPoppedOut = true;
        var startCount = vm.StripsEntries.Count;

        await vm.OpenStripsEntryForFacilityAsync("OAK");

        Assert.Equal(startCount, vm.StripsEntries.Count);
        Assert.False(studentEntry.IsPoppedOut);
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
}
