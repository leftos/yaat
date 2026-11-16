using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// VECTOR and HIST: band values until a hand change pins them, AUTO clearing both; VECTOR steps 0, 1, 2, 4 and 8 minutes
/// and HIST 0 to 10 dots; a VECTOR click flips OWN and ALL, a setting that never pins; pref sets and the saved state keep
/// OWN or ALL and the two pins.
/// </summary>
public class ScopeVectorHistTests : IDisposable
{
    private const string VectorUsage = "Usage: .scope vector <0|1|2|4|8> | .scope vector <own|all>";

    private const string HistUsage = "Usage: .scope hist <0-10>";

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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scope-vector-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

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
    public async Task VectorSpinner_StepsThe_0_1_2_4_8_Grid_AndStopsAtItsEnds()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal(1, scope.EffectiveBand.VectorMin);

            scope.StepSpinner(ScopeButtonId.Vector, 1);
            Assert.Equal(2, scope.EffectiveBand.VectorMin);
            Assert.Equal("Scope vector 2 MIN (pinned)", main.StatusText);
            scope.StepSpinner(ScopeButtonId.Vector, 1);
            Assert.Equal(4, scope.EffectiveBand.VectorMin);
            scope.StepSpinner(ScopeButtonId.Vector, 1);
            Assert.Equal(8, scope.EffectiveBand.VectorMin);

            main.StatusText = "";
            scope.StepSpinner(ScopeButtonId.Vector, 1);
            Assert.Equal(8, scope.EffectiveBand.VectorMin);
            Assert.Equal("", main.StatusText);

            scope.StepSpinner(ScopeButtonId.Vector, -2);
            Assert.Equal(2, scope.EffectiveBand.VectorMin);
            scope.StepSpinner(ScopeButtonId.Vector, -5);
            Assert.Equal(0, scope.EffectiveBand.VectorMin);
            Assert.Equal("Scope vector 0 MIN (pinned)", main.StatusText);

            main.StatusText = "";
            scope.StepSpinner(ScopeButtonId.Vector, -1);
            Assert.Equal(0, scope.EffectiveBand.VectorMin);
            Assert.Equal("", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task HistSpinner_StepsByOne_AndStopsAt0And10()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal(5, scope.EffectiveBand.History);

            scope.StepSpinner(ScopeButtonId.Hist, 1);
            Assert.Equal(6, scope.EffectiveBand.History);
            Assert.Equal("Scope history 6 (pinned)", main.StatusText);
            scope.StepSpinner(ScopeButtonId.Hist, 5);
            Assert.Equal(10, scope.EffectiveBand.History);

            main.StatusText = "";
            scope.StepSpinner(ScopeButtonId.Hist, 1);
            Assert.Equal(10, scope.EffectiveBand.History);
            Assert.Equal("", main.StatusText);

            scope.StepSpinner(ScopeButtonId.Hist, -12);
            Assert.Equal(0, scope.EffectiveBand.History);

            main.StatusText = "";
            scope.StepSpinner(ScopeButtonId.Hist, -1);
            Assert.Equal(0, scope.EffectiveBand.History);
            Assert.Equal("", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task VectorAndHist_FollowTheBand_UntilAHandChangePinsThem()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            Assert.Equal(0, scope.EffectiveBand.VectorMin);
            Assert.Equal(5, scope.EffectiveBand.History);
            main.RunScopeCommand(".scope range 60");
            Assert.Equal(1, scope.EffectiveBand.VectorMin);
            Assert.False(scope.Pins.Any);

            main.RunScopeCommand(".scope vector 4");
            main.RunScopeCommand(".scope range 200");
            Assert.Equal(4, scope.EffectiveBand.VectorMin);
            Assert.Equal(3, scope.EffectiveBand.History);

            main.RunScopeCommand(".scope hist 2");
            main.RunScopeCommand(".scope range 20");
            Assert.Equal(4, scope.EffectiveBand.VectorMin);
            Assert.Equal(2, scope.EffectiveBand.History);
            Assert.Equal(new ScopeBandPins(null, 4, 2, null), scope.Pins);
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Auto_ClearsTheVectorAndHistPins()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope vector 8");
            main.RunScopeCommand(".scope hist 9");

            main.RunScopeCommand(".scope auto");

            Assert.Equal(ScopeBandPins.None, scope.Pins);
            Assert.Equal(1, scope.EffectiveBand.VectorMin);
            Assert.Equal(5, scope.EffectiveBand.History);
            Assert.False(scope.IsActive(Button(ScopeButtonId.Auto)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SpinnerValues_ShowTheMinutesAndOwnership_AndTheHistCount()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal("1m OWN", scope.SpinnerValue(Button(ScopeButtonId.Vector)));
            Assert.Equal("5", scope.SpinnerValue(Button(ScopeButtonId.Hist)));

            main.RunScopeCommand(".scope range 20");
            main.RunScopeCommand(".scope vector all");

            Assert.Equal("0m ALL", scope.SpinnerValue(Button(ScopeButtonId.Vector)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task VectorClick_FlipsOwnAndAll_WithoutPinning()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal(ScopeVectorOwnership.Own, scope.VectorOwnership);

            scope.Press(Button(ScopeButtonId.Vector));

            Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
            Assert.Equal("Scope vector ALL", main.StatusText);
            Assert.False(scope.Pins.Any);
            Assert.False(scope.IsActive(Button(ScopeButtonId.Auto)));
            Assert.Equal(1, scope.EffectiveBand.VectorMin);

            scope.Press(Button(ScopeButtonId.Vector));

            Assert.Equal(ScopeVectorOwnership.Own, scope.VectorOwnership);
            Assert.Equal("Scope vector OWN", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task VectorVerb_PinsTheMinutes_OrSetsOwnership()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope vector 2");
            Assert.Equal("Scope vector 2 MIN (pinned)", main.StatusText);
            Assert.Equal(2, scope.Pins.VectorMin);

            main.RunScopeCommand(".scope VECTOR All");
            Assert.Equal("Scope vector ALL", main.StatusText);
            Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
            Assert.Equal(new ScopeBandPins(null, 2, null, null), scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope vector 3")]
    [InlineData(".scope vector 9")]
    [InlineData(".scope vector -1")]
    [InlineData(".scope vector 0.5")]
    [InlineData(".scope vector x")]
    [InlineData(".scope vector")]
    [InlineData(".scope vector 1 2")]
    public async Task VectorVerb_OffTheGrid_IsRefusedWithTheAllowedValues(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(command);

            Assert.Equal(VectorUsage, main.StatusText);
            Assert.Equal(ScopeBandPins.None, scope.Pins);
            Assert.Equal(ScopeVectorOwnership.Own, scope.VectorOwnership);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task HistVerb_PinsTheCount()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope hist 3");

            Assert.Equal("Scope history 3 (pinned)", main.StatusText);
            Assert.Equal(3, scope.Pins.History);
            Assert.Equal(3, scope.EffectiveBand.History);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope hist 11")]
    [InlineData(".scope hist -1")]
    [InlineData(".scope hist 2.5")]
    [InlineData(".scope hist x")]
    [InlineData(".scope hist")]
    public async Task HistVerb_OutOfRange_IsRefusedWithTheAllowedValues(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(command);

            Assert.Equal(HistUsage, main.StatusText);
            Assert.Equal(ScopeBandPins.None, scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(9.0)]
    [InlineData(-1.0)]
    [InlineData(3.0)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    public async Task PrefSet_VectorMinOffTheGrid_KeepsTheCurrentPin(double minutes)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope vector 2");

            scope.ApplyPrefSet(scope.CapturePrefSet(null) with { PinVectorMin = minutes });

            Assert.Equal(2, scope.Pins.VectorMin);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(0.0)]
    [InlineData(8.0)]
    public async Task PrefSet_VectorMinOnTheGrid_IsPinned(double minutes)
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            scope.ApplyPrefSet(scope.CapturePrefSet(null) with { PinVectorMin = minutes });

            Assert.Equal(minutes, scope.Pins.VectorMin);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(11, 7)]
    [InlineData(-1, 7)]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    public async Task PrefSet_History_IsPinnedFrom0To10_ElseKeepsTheCurrentPin(int count, int expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope hist 7");

            scope.ApplyPrefSet(scope.CapturePrefSet(null) with { PinHistory = count });

            Assert.Equal(expected, scope.Pins.History);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PinSetters_OutOfRange_Throw()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.PinVectorMin(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.PinHistory(11));
            Assert.Equal(ScopeBandPins.None, scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_CapturesOwnership_WhichIsNoPin()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal("own", scope.CapturePrefSet(null).VectorOwnership);

            main.RunScopeCommand(".scope vector all");

            Assert.Equal("all", scope.CapturePrefSet(null).VectorOwnership);
            Assert.False(scope.Pins.Any);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_UnknownOwnership_KeepsTheCurrentSetting()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope vector all");

            scope.ApplyPrefSet(scope.CapturePrefSet(null) with { VectorOwnership = "sideways" });

            Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_SaveAsThenLoad_RestoresOwnershipAndBothPins()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenStoredScopeAsync();
        try
        {
            main.RunScopeCommand(".scope vector all");
            main.RunScopeCommand(".scope vector 4");
            main.RunScopeCommand(".scope hist 2");
            main.RunScopeCommand(".scope pref saveas VH");
            main.RunScopeCommand(".scope vector own");
            main.RunScopeCommand(".scope auto");

            main.RunScopeCommand(".scope pref load VH");

            Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
            Assert.Equal(new ScopeBandPins(null, 4, 2, null), scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Vector_IsActive_WhileItsMinutesArePinned()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.False(scope.IsActive(Button(ScopeButtonId.Vector)));

            main.RunScopeCommand(".scope vector all");
            Assert.False(scope.IsActive(Button(ScopeButtonId.Vector)));

            main.RunScopeCommand(".scope hist 2");
            Assert.False(scope.IsActive(Button(ScopeButtonId.Vector)));

            main.RunScopeCommand(".scope vector 4");
            Assert.True(scope.IsActive(Button(ScopeButtonId.Vector)));

            main.RunScopeCommand(".scope auto");
            Assert.False(scope.IsActive(Button(ScopeButtonId.Vector)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Hist_IsActive_WhileItsCountIsPinned()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.False(scope.IsActive(Button(ScopeButtonId.Hist)));

            main.RunScopeCommand(".scope vector 4");
            Assert.False(scope.IsActive(Button(ScopeButtonId.Hist)));

            main.RunScopeCommand(".scope hist 2");
            Assert.True(scope.IsActive(Button(ScopeButtonId.Hist)));

            main.RunScopeCommand(".scope auto");
            Assert.False(scope.IsActive(Button(ScopeButtonId.Hist)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SavedState_RestoresOwnershipAndBothPins()
    {
        (MainViewModel main, ScopeViewModel first) = await OpenStoredScopeAsync();
        main.RunScopeCommand(".scope vector all");
        main.RunScopeCommand(".scope vector 4");
        main.RunScopeCommand(".scope hist 2");
        first.Dispose();

        ScopeViewModel reopened = Reopen(main);
        try
        {
            Assert.Equal(ScopeVectorOwnership.All, reopened.VectorOwnership);
            Assert.Equal(new ScopeBandPins(null, 4, 2, null), reopened.Pins);
            Assert.Equal(4, reopened.EffectiveBand.VectorMin);
            Assert.Equal(2, reopened.EffectiveBand.History);
        }
        finally
        {
            reopened.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task HistAndVectorBrite_RoundTripInAPrefSet()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal(100, scope.Brightness.Hist);
            Assert.Equal(100, scope.Brightness.Vector);
            main.RunScopeCommand(".scope brite hist 40");
            main.RunScopeCommand(".scope brite vector 55");
            ScopePrefSet saved = scope.CapturePrefSet(null);
            main.RunScopeCommand(".scope brite hist 100");
            main.RunScopeCommand(".scope brite vector 100");

            scope.ApplyPrefSet(saved);

            Assert.Equal(40, saved.Brightness["hist"]);
            Assert.Equal(55, saved.Brightness["vector"]);
            Assert.Equal(40, scope.Brightness.Hist);
            Assert.Equal(55, scope.Brightness.Vector);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
