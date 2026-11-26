using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Per-value unpin: <c>.scope hist auto</c> and <c>.scope vector auto</c> each clear their own pin only, so that value
/// follows the band again while every other pin, and OWN or ALL, stays; AUTO stays lit while any pin is left.
/// </summary>
public class ScopeValueUnpinTests
{
    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    [AvaloniaFact]
    public async Task HistAuto_ClearsTheHistoryPinOnly()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope hist 3");
            main.RunScopeCommand(".scope vector 2");

            main.RunScopeCommand(".scope hist auto");

            Assert.Null(scope.Pins.History);
            Assert.Equal(2, scope.Pins.VectorMin);
            Assert.Equal("Radar history AUTO", main.StatusText);
            Assert.False(scope.IsActive(Button(ScopeButtonId.Hist)));
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task VectorAuto_ClearsTheVectorPinOnly_AndKeepsOwnOrAll()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope vector all");
            main.RunScopeCommand(".scope vector 2");
            main.RunScopeCommand(".scope hist 3");

            main.RunScopeCommand(".scope vector auto");

            Assert.Null(scope.Pins.VectorMin);
            Assert.Equal(3, scope.Pins.History);
            Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
            Assert.Equal("Radar vector AUTO", main.StatusText);
            Assert.False(scope.IsActive(Button(ScopeButtonId.Vector)));
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task HistAutoThenVectorAuto_LeaveNoPin_AndAutoGoesDark()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope hist 3");
            main.RunScopeCommand(".scope vector 2");

            main.RunScopeCommand(".scope hist auto");
            main.RunScopeCommand(".scope vector auto");

            Assert.Equal(ScopeBandPins.None, scope.Pins);
            Assert.False(scope.IsActive(Button(ScopeButtonId.Auto)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task HistAutoAndVectorAuto_OnUnpinnedValues_ChangeNothing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others full");
            ScopeBandPins before = scope.Pins;
            ScopeZoomBand band = scope.EffectiveBand;

            main.RunScopeCommand(".scope hist auto");
            main.RunScopeCommand(".scope vector auto");

            Assert.Equal(before, scope.Pins);
            Assert.Equal(band, scope.EffectiveBand);
            Assert.Equal(ScopeVectorOwnership.Own, scope.VectorOwnership);
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));
        }
        finally
        {
            scope.Dispose();
        }
    }
}
