using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's map-tool verbs and tiles: <c>.scope mva</c> and the MVA tile turn the MVA tint on and off, <c>.scope lock</c>
/// and DISPLAY's LOCK lock pan and zoom, and <c>.scope center</c> is DISPLAY's OFF CNTR.
/// </summary>
public class ScopeMapToolsCommandTests
{
    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    [AvaloniaFact]
    public async Task MvaVerb_TurnsTheTintOnAndOff_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope mva on");
            Assert.True(scope.ShowMvaHints);
            Assert.Equal("Scope MVA ON", main.StatusText);

            main.RunScopeCommand(".scope mva off");
            Assert.False(scope.ShowMvaHints);
            Assert.Equal("Scope MVA OFF", main.StatusText);

            main.RunScopeCommand(".scope mva");
            Assert.Equal("Usage: .scope mva <on|off>", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MvaTile_FlipsTheTint_LitWhileOn()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeButton mva = Button(ScopeButtonId.Mva);
            Assert.Equal(("MVA", ScopeButtonKind.Toggle), (mva.Label, mva.Kind));
            scope.ShowMvaHints = false;

            scope.Press(mva);
            Assert.True(scope.ShowMvaHints);
            Assert.True(scope.IsActive(mva));

            scope.Press(mva);
            Assert.False(scope.ShowMvaHints);
            Assert.False(scope.IsActive(mva));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public void MvaTile_TheNextSeedTakesTheSourcesDefaultAgain()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.ScopeMaps.ShowMvaHintsDefault = false;
        LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;

        scope.Press(Button(ScopeButtonId.Mva));
        Assert.True(scope.ShowMvaHints);

        LoadSource(main);
        Assert.False(scope.ShowMvaHints);
    }

    [AvaloniaFact]
    public void Display_IsAMenuOfFixWxLockAndOffCntr()
    {
        ScopeButton display = Button(ScopeButtonId.Display);

        Assert.Equal(("DISPLAY", ScopeButtonKind.Menu), (display.Label, display.Kind));
        Assert.Equal(
            [ScopeButtonId.DisplayFix, ScopeButtonId.DisplayWx, ScopeButtonId.DisplayLock, ScopeButtonId.DisplayOffCntr],
            display.Children.Select(c => c.Id)
        );
        Assert.Equal(["FIX", "WX", "LOCK", "OFF CNTR"], display.Children.Select(c => c.Label));
        Assert.Equal(
            [ScopeButtonKind.Toggle, ScopeButtonKind.Toggle, ScopeButtonKind.Toggle, ScopeButtonKind.Command],
            display.Children.Select(c => c.Kind)
        );
        Assert.Equal([".scope fix", ".scope wx", ".scope lock", ".scope center"], display.Children.Select(c => c.Command));
    }

    [AvaloniaFact]
    public async Task LockVerb_LocksAndUnlocks_AndEchoes_TheLockTileLitWhileLocked()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeButton lockTile = Button(ScopeButtonId.DisplayLock);

            main.RunScopeCommand(".scope lock on");
            Assert.True(scope.IsPanZoomLocked);
            Assert.True(scope.IsActive(lockTile));
            Assert.Equal("Scope LOCK ON", main.StatusText);

            scope.Press(lockTile);
            Assert.False(scope.IsPanZoomLocked);
            Assert.Equal("Scope LOCK OFF", main.StatusText);

            main.RunScopeCommand(".scope lock maybe");
            Assert.Equal("Usage: .scope lock <on|off>", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MapToolVerbs_WithNoScopeOpen_SayNoScopeIsOpen()
    {
        MainViewModel main = await MainAsync();

        foreach (string command in new[] { ".scope mva on", ".scope lock on", ".scope center" })
        {
            main.RunScopeCommand(command);
            Assert.Equal("No YAAT Scope is open", main.StatusText);
        }
    }

    [AvaloniaFact]
    public async Task CenterVerb_TakesNoArgument()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope center now");
            Assert.Equal("Usage: .scope center", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>Loads one STARS area and map 31 into the source, so the scope seeds.</summary>
    internal static void LoadSource(MainViewModel main, double lat = 37.62, double lon = -122.38) =>
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto(
                "ZOA",
                "NCT",
                [new StarsAreaDto("A", "Area A", lat, lon, 60, [])],
                [new VideoMapInfoDto("m31", "Map 31", "MAP31", [], "A", 31, AlwaysVisible: false, TdmOnly: false)],
                []
            ),
            null,
            null
        );
}
