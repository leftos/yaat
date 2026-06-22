using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests;

// Ctrl+F8 toggles the radar DCB (issue #215). The VM owns the visibility state, resets the
// sub-menu to Main when hiding (mirroring CRC), and persists through UserPreferences.
// UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process temp
// directory; a fresh UserPreferences instance proves the disk round-trip. Each test sets its own
// starting value, so it doesn't depend on the global RadarDcbVisible left by a sibling test.
public class RadarViewModelDcbToggleTests
{
    private static RadarViewModel NewVm() => new(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);

    [Fact]
    public void ToggleDcbVisible_FlipsVisibilityAndPersists()
    {
        var prefs = new UserPreferences();
        prefs.SetRadarDcbVisible(true);
        var vm = NewVm();
        vm.SetPreferences(prefs);
        Assert.True(vm.IsDcbVisible);

        vm.ToggleDcbVisibleCommand.Execute(null);
        Assert.False(vm.IsDcbVisible);
        Assert.False(new UserPreferences().RadarDcbVisible);

        vm.ToggleDcbVisibleCommand.Execute(null);
        Assert.True(vm.IsDcbVisible);
        Assert.True(new UserPreferences().RadarDcbVisible);
    }

    [Fact]
    public void ToggleDcbVisible_HidingResetsSubmenuToMain()
    {
        var prefs = new UserPreferences();
        prefs.SetRadarDcbVisible(true);
        var vm = NewVm();
        vm.SetPreferences(prefs);
        vm.DcbMode = DcbMenuMode.Aux;

        vm.ToggleDcbVisibleCommand.Execute(null);

        Assert.False(vm.IsDcbVisible);
        Assert.Equal(DcbMenuMode.Main, vm.DcbMode);
    }

    [Fact]
    public void SetPreferences_RestoresPersistedVisibility()
    {
        new UserPreferences().SetRadarDcbVisible(false);

        var prefs = new UserPreferences();
        var vm = NewVm();
        vm.SetPreferences(prefs);

        Assert.False(vm.IsDcbVisible);
    }
}
