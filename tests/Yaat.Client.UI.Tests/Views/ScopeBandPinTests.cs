using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Zoom bands: a manual change stays pinned until AUTO. The range picks the band and never pins; a pin holds over every
/// band and every profile until AUTO clears it, AUTO lights while any pin is set, and pref sets and the saved state keep
/// the pinned values only.
/// </summary>
public class ScopeBandPinTests : IDisposable
{
    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scope-pin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    private static async Task<MainViewModel> MainAsync()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return main;
    }

    /// <summary>A scope with no viewer and no store: the Mixed profile, at 40 NM.</summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenScopeAsync()
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;
        return (main, scope);
    }

    /// <summary>A scope viewed from ZOA sector 44, saving through this test's store.</summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenStoredScopeAsync()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
        return (main, Reopen(main));
    }

    private ScopeViewModel Reopen(MainViewModel main)
    {
        var scope = new ScopeViewModel(main, Store(), profileStore: null);
        main.OpenScope = scope;
        return scope;
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());

    [AvaloniaFact]
    public async Task RangeChange_AfterAPin_StaysPinned()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);

            main.RunScopeCommand(".scope others partial");
            main.RunScopeCommand(".scope range 25");
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);

            main.RunScopeCommand(".scope range 200");
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);
            Assert.Null(scope.EffectiveBand.MaxNm);
            Assert.Equal(ScopeProfile.OthersPartial, scope.Pins.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Auto_ClearsThePin_AndTheValueFollowsTheBandAgain()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            main.RunScopeCommand(".scope others partial");

            main.RunScopeCommand(".scope auto");

            Assert.Equal(ScopeBandPins.None, scope.Pins);
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
            main.RunScopeCommand(".scope range 60");
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Auto_IsLitOnlyWhileAPinIsSet()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.False(scope.IsActive(Button(ScopeButtonId.Auto)));
            Assert.False(scope.IsActive(Button(ScopeButtonId.Others)));

            main.RunScopeCommand(".scope others full");
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));
            Assert.True(scope.IsActive(Button(ScopeButtonId.Others)));

            main.RunScopeCommand(".scope range 100");
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));

            main.RunScopeCommand(".scope auto");
            Assert.False(scope.IsActive(Button(ScopeButtonId.Auto)));
            Assert.False(scope.IsActive(Button(ScopeButtonId.Others)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OthersButton_FlipsTheLevelInEffect_AndPinsIt()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            Assert.Equal("FULL", scope.SpinnerValue(Button(ScopeButtonId.Others)));

            scope.Press(Button(ScopeButtonId.Others));

            Assert.Equal(ScopeProfile.OthersPartial, scope.Pins.OthersLevel);
            Assert.Equal("PARTIAL", scope.SpinnerValue(Button(ScopeButtonId.Others)));
            Assert.Equal("Scope others PARTIAL (pinned)", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ProfileChange_KeepsThePins()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            main.RunScopeCommand(".scope others partial");

            main.RunScopeCommand(".scope profile stars");

            Assert.Equal(ScopeProfileKind.Stars, scope.ProfileKind);
            Assert.Equal(ScopeProfile.OthersPartial, scope.Pins.OthersLevel);
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);
            main.RunScopeCommand(".scope auto");
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_CapturesAndAppliesEveryPin_AndUnpinnedValuesAreNotSaved()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopePrefSet unpinned = scope.CapturePrefSet(null);
            Assert.Null(unpinned.PinOthersLevel);
            Assert.Null(unpinned.PinVectorMin);
            Assert.Null(unpinned.PinHistory);
            Assert.Null(unpinned.PinRings);

            ScopePrefSet pinned = unpinned with { PinOthersLevel = "full", PinVectorMin = 2, PinHistory = 7, PinRings = "dri" };
            ScopePrefSet read = JsonSerializer.Deserialize<ScopePrefSet>(
                JsonSerializer.Serialize(pinned, ScopePrefSet.JsonOptions),
                ScopePrefSet.JsonOptions
            )!;
            scope.ApplyPrefSet(read);

            Assert.Equal(new ScopeBandPins("full", 2, 7, "dri"), scope.Pins);
            ScopePrefSet captured = scope.CapturePrefSet(null);
            Assert.Equal("full", captured.PinOthersLevel);
            Assert.Equal(2, captured.PinVectorMin);
            Assert.Equal(7, captured.PinHistory);
            Assert.Equal("dri", captured.PinRings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_UnknownPinValues_KeepTheCurrentPins()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others full");

            scope.ApplyPrefSet(scope.CapturePrefSet(null) with { PinOthersLevel = "half", PinHistory = -1, PinRings = "halo" });

            Assert.Equal(ScopeBandPins.None with { OthersLevel = "full" }, scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_SaveAsThenLoad_RestoresThePin()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenStoredScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others full");
            main.RunScopeCommand(".scope pref saveas PINS");
            main.RunScopeCommand(".scope auto");

            main.RunScopeCommand(".scope pref load PINS");

            Assert.Equal(ScopeProfile.OthersFull, scope.Pins.OthersLevel);
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SavedState_RestoresThePins_AndSavesNoneOnceCleared()
    {
        (MainViewModel main, ScopeViewModel first) = await OpenStoredScopeAsync();
        main.RunScopeCommand(".scope others full");
        first.Dispose();

        ScopeViewModel reopened = Reopen(main);
        try
        {
            Assert.Equal(ScopeProfile.OthersFull, reopened.Pins.OthersLevel);
            Assert.True(reopened.IsActive(Button(ScopeButtonId.Auto)));
            main.RunScopeCommand(".scope auto");
        }
        finally
        {
            reopened.Dispose();
        }

        Assert.Null(Store().LoadCurrent("ZOA")!.PinOthersLevel);
    }
}
