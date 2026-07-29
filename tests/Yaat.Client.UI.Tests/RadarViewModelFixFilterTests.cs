using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests;

// vNAS NavData publishes thousands of adapted fixes whose identifiers are themselves FRD strings
// (OAK169001, ABI037030, …). They are valid, resolvable identifiers — typing one still works — but
// they are not chartable waypoints, so the scope's fix overlay must not paint them and they must
// never anchor a deduced FRD.
public class RadarViewModelFixFilterTests
{
    private static RadarViewModel NewVm() => new(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);

    [Fact]
    public void SetNavDbReady_ExcludesFrdNamedFixesFromDisplay()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["OAK"] = (37.7213, -122.2208),
            ["SUNOL"] = (37.5922, -121.8822),
            ["OAK169001"] = (37.7053, -122.2166),
        };
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting(fixes));

        var vm = NewVm();
        vm.SetNavDbReady();

        Assert.NotNull(vm.Fixes);
        Assert.Contains(vm.Fixes, f => f.Name == "OAK");
        Assert.Contains(vm.Fixes, f => f.Name == "SUNOL");
        Assert.DoesNotContain(vm.Fixes, f => f.Name == "OAK169001");
    }

    [Fact]
    public void SetNavDbReady_KeepsFrdNamedFixesInAutocompleteNames()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["OAK"] = (37.7213, -122.2208),
            ["OAK169001"] = (37.7053, -122.2166),
        };
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting(fixes));

        var vm = NewVm();
        vm.SetNavDbReady();

        Assert.NotNull(vm.FixNames);
        Assert.Contains("OAK169001", vm.FixNames);
    }
}
