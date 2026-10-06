using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests;

/// <summary>
/// A layout saves the Strips and vTDLS tabs open beside the student's own and reopens them on apply. Headless there
/// is no server, so a tab's facility switch fails and the tests assign <c>FacilityId</c> the way the switch would.
/// </summary>
public class LayoutServiceOpenTabsTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static async Task<VStripsDockEntryViewModel> OpenStripsTabAsync(MainViewModel vm, string facilityId)
    {
        await vm.OpenStripsEntryForFacilityAsync(facilityId);
        VStripsDockEntryViewModel entry = vm.StripsEntries[^1];
        entry.Vm.FacilityId = facilityId;
        return entry;
    }

    private static async Task<VTdlsDockEntryViewModel> OpenTdlsTabAsync(MainViewModel vm, string facilityId)
    {
        await vm.OpenTdlsEntryForFacilityAsync(facilityId);
        VTdlsDockEntryViewModel entry = vm.TdlsEntries[^1];
        entry.Vm.FacilityId = facilityId;
        return entry;
    }

    private static void MakeAccessible(MainViewModel vm, params string[] facilityIds)
    {
        foreach (string id in facilityIds)
        {
            vm.VStrips.AccessibleFacilities.Add(new AccessibleFacilityDto(id, id, IsStudentFacility: false));
            vm.VTdls.AccessibleFacilities.Add(new AccessibleFacilityDto(id, id, IsStudentFacility: false));
        }
    }

    private static List<VStripsDockEntryViewModel> ExtraStripsTabs(MainViewModel vm) => [.. vm.StripsEntries.Where(e => !e.IsStudentEntry)];

    private static List<VTdlsDockEntryViewModel> ExtraTdlsTabs(MainViewModel vm) => [.. vm.TdlsEntries.Where(e => !e.IsStudentEntry)];

    [AvaloniaFact]
    public async Task CaptureCurrent_RecordsTheTabsOpenBesideTheStudentsOwn()
    {
        MainViewModel vm = NewVm();
        vm.VStrips.FacilityId = "OAK";
        await OpenStripsTabAsync(vm, "NCT");
        await OpenStripsTabAsync(vm, "NCT");
        await OpenTdlsTabAsync(vm, "SFO");

        SavedLayout layout = new LayoutService(vm.Preferences).CaptureCurrent("tabs", vm);

        // The student's own tab is always open, so only the extra tabs are listed, in tab order.
        Assert.NotNull(layout.OpenTabs);
        Assert.Equal(["NCT", "NCT"], layout.OpenTabs.Strips);
        Assert.Equal(["SFO"], layout.OpenTabs.Tdls);
    }

    [AvaloniaFact]
    public async Task ApplyOpenTabs_FieldSet_OpensTheListedTabsAndClosesTheRest()
    {
        MainViewModel vm = NewVm();
        MakeAccessible(vm, "NCT", "SFO", "SJC");
        VStripsDockEntryViewModel keep = await OpenStripsTabAsync(vm, "NCT");
        VStripsDockEntryViewModel unlisted = await OpenStripsTabAsync(vm, "SJC");
        VTdlsDockEntryViewModel unlistedTdls = await OpenTdlsTabAsync(vm, "SJC");
        var layout = new SavedLayout
        {
            Name = "tabs",
            OpenTabs = new SavedOpenTabs { Strips = ["NCT", "SFO"], Tdls = ["SFO"] },
        };

        IReadOnlyList<string> skipped = await new LayoutService(vm.Preferences).ApplyOpenTabsAsync(layout, vm);

        Assert.Empty(skipped);
        List<VStripsDockEntryViewModel> strips = ExtraStripsTabs(vm);
        // NCT was already open and stays the same tab; SJC is not listed and closes; SFO opens.
        Assert.Equal(2, strips.Count);
        Assert.Same(keep, strips[0]);
        Assert.DoesNotContain(unlisted, strips);
        List<VTdlsDockEntryViewModel> tdls = ExtraTdlsTabs(vm);
        Assert.Single(tdls);
        Assert.NotSame(unlistedTdls, tdls[0]);
    }

    [AvaloniaFact]
    public async Task ApplyOpenTabs_FieldNull_LeavesTheOpenTabsAsTheyAre()
    {
        MainViewModel vm = NewVm();
        MakeAccessible(vm, "NCT", "SFO");
        VStripsDockEntryViewModel strips = await OpenStripsTabAsync(vm, "NCT");
        VTdlsDockEntryViewModel tdls = await OpenTdlsTabAsync(vm, "SFO");
        var layout = new SavedLayout { Name = "no-tabs", OpenTabs = null };

        IReadOnlyList<string> skipped = await new LayoutService(vm.Preferences).ApplyOpenTabsAsync(layout, vm);

        Assert.Empty(skipped);
        Assert.Equal([strips], ExtraStripsTabs(vm));
        Assert.Equal([tdls], ExtraTdlsTabs(vm));
    }

    [AvaloniaFact]
    public async Task ApplyOpenTabs_NoAccessibleFacilities_ChangesNothingAndReportsNothing()
    {
        // No room (or not loaded yet): the student position's facility list is empty, so nothing can be judged
        // unavailable — the apply leaves every tab alone rather than closing them and reporting all as gone.
        MainViewModel vm = NewVm();
        VStripsDockEntryViewModel strips = await OpenStripsTabAsync(vm, "NCT");
        VTdlsDockEntryViewModel tdls = await OpenTdlsTabAsync(vm, "SFO");
        var layout = new SavedLayout
        {
            Name = "no-room",
            OpenTabs = new SavedOpenTabs { Strips = ["OAK"], Tdls = ["SMF"] },
        };

        IReadOnlyList<string> skipped = await new LayoutService(vm.Preferences).ApplyOpenTabsAsync(layout, vm);

        Assert.Empty(skipped);
        Assert.Equal([strips], ExtraStripsTabs(vm));
        Assert.Equal([tdls], ExtraTdlsTabs(vm));
    }

    [AvaloniaFact]
    public async Task ApplyOpenTabs_StudentsOwnStripsFacility_IsSkippedSilently()
    {
        MainViewModel vm = NewVm();
        vm.VStrips.AccessibleFacilities.Add(new AccessibleFacilityDto("OAK", "OAK", IsStudentFacility: true));
        vm.VStrips.AccessibleFacilities.Add(new AccessibleFacilityDto("NCT", "NCT", IsStudentFacility: false));
        var layout = new SavedLayout
        {
            Name = "own",
            OpenTabs = new SavedOpenTabs { Strips = ["OAK"], Tdls = [] },
        };

        IReadOnlyList<string> skipped = await new LayoutService(vm.Preferences).ApplyOpenTabsAsync(layout, vm);

        // The student's own facility already has its tab, as with vTDLS: no second tab, and nothing reported.
        Assert.Empty(skipped);
        Assert.Empty(ExtraStripsTabs(vm));
    }

    [AvaloniaFact]
    public async Task ApplyOpenTabs_FacilityNoLongerAccessible_IsSkippedAndReported()
    {
        MainViewModel vm = NewVm();
        MakeAccessible(vm, "NCT");
        var layout = new SavedLayout
        {
            Name = "gone",
            OpenTabs = new SavedOpenTabs { Strips = ["ZOA", "NCT"], Tdls = ["SMF"] },
        };

        IReadOnlyList<string> skipped = await new LayoutService(vm.Preferences).ApplyOpenTabsAsync(layout, vm);

        Assert.Equal(["ZOA", "SMF"], skipped);
        Assert.Single(ExtraStripsTabs(vm));
        Assert.Empty(ExtraTdlsTabs(vm));
    }
}
