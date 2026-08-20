using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Focused unit coverage for <see cref="VStripsDockEntryViewModel"/>'s
/// title binding and dock-state flag. Multi-tab scaffolding on
/// <see cref="MainViewModel"/> is exercised indirectly — a dedicated
/// test for the RPC-backed OpenStripsEntryForFacilityAsync path would
/// require a fake SignalR connection, which we add when integration
/// testing the server RPCs end-to-end.
/// </summary>
public class VStripsDockEntryViewModelTests
{
    private static VStripsViewModel NewVm() => new(new ServerConnection(), (_, _, _) => Task.CompletedTask, getUserInitials: null);

    [Fact]
    public void TabTitle_PrefixesPendingPrinterCount_AndTracksIt()
    {
        var vm = NewVm();
        vm.FacilityId = "OAK";
        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        var titleChanges = 0;
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VStripsDockEntryViewModel.TabTitle))
            {
                titleChanges++;
            }
        };

        var dep = new StripItemViewModel(new StripItemDto("STRIP_N248ZV", "N248ZV", false, StripItemType.DepartureStrip, false, [], "OAK", ""));
        var arr = new StripItemViewModel(new StripItemDto("ARRIVAL_N248ZV", "N248ZV", false, StripItemType.ArrivalStrip, false, [], "OAK", ""));
        vm.Printer.ReplaceAll([dep.Id, arr.Id], new Dictionary<string, StripItemViewModel> { [dep.Id] = dep, [arr.Id] = arr });

        Assert.Equal("(2) OAK - vStrips", entry.TabTitle);
        Assert.True(titleChanges > 0, "pending-count change must re-render the tab title");

        vm.Printer.Clear();
        Assert.Equal("OAK - vStrips", entry.TabTitle);
    }

    [Fact]
    public void TabTitle_PrefersFacilityId_OverName()
    {
        var vm = NewVm();
        vm.FacilityId = "OAK";
        vm.FacilityName = "Oakland Intl ATCT";

        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        Assert.Equal("OAK - vStrips", entry.TabTitle);
    }

    [Fact]
    public void TabTitle_FallsBackToFacilityName_WhenIdIsNull()
    {
        var vm = NewVm();
        vm.FacilityId = null;
        vm.FacilityName = "Oakland Intl ATCT";

        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        Assert.Equal("Oakland Intl ATCT - vStrips", entry.TabTitle);
    }

    [Fact]
    public void TabTitle_FallsBackToGenericLabel_WhenIdAndNameNull()
    {
        var vm = NewVm();

        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        Assert.Equal("vStrips", entry.TabTitle);
    }

    [Fact]
    public void TabTitle_FiresPropertyChanged_WhenFacilityNameChanges()
    {
        var vm = NewVm();
        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        var titleChanges = 0;
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VStripsDockEntryViewModel.TabTitle))
            {
                titleChanges++;
            }
        };

        vm.FacilityName = "Fresno ATCT";
        vm.FacilityName = "OAK ATCT";

        Assert.Equal(2, titleChanges);
    }

    [Fact]
    public void IsPoppedOut_DefaultsFalse_AndIsObservable()
    {
        var vm = NewVm();
        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: false);

        Assert.False(entry.IsPoppedOut);

        var changes = 0;
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VStripsDockEntryViewModel.IsPoppedOut))
            {
                changes++;
            }
        };

        entry.IsPoppedOut = true;
        entry.IsPoppedOut = false;

        Assert.Equal(2, changes);
    }
}
