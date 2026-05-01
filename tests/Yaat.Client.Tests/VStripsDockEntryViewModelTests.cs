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
    public void TabTitle_UsesFacilityName_WhenAvailable()
    {
        var vm = NewVm();
        vm.FacilityName = "OAK ATCT";

        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        Assert.Equal("Strips (OAK ATCT)", entry.TabTitle);
    }

    [Fact]
    public void TabTitle_FallsBackToFacilityId_WhenNameIsNull()
    {
        var vm = NewVm();
        vm.FacilityId = "FAC1";
        vm.FacilityName = null;

        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        Assert.Equal("Strips (FAC1)", entry.TabTitle);
    }

    [Fact]
    public void TabTitle_FallsBackToGenericLabel_WhenIdAndNameNull()
    {
        var vm = NewVm();

        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: true);

        Assert.Equal("Strips", entry.TabTitle);
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
