using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="RunwayLandability.IsLandable"/>: a runway end is landable when its landing distance available (LDA, AIM
/// 4-3-6.d.3(d)) is at least 1.15 times the type's landing distance (14 CFR 121.195(d)'s factor), with no wind, surface or
/// elevation correction; a type with no figure, a helicopter and a ground vehicle always count as landable. Every case is
/// measured against the committed navigation data and aircraft profiles.
/// </summary>
public class LandableRunwayTests
{
    private readonly ITestOutputHelper _output;
    private readonly NavigationDatabase _navDb;

    public LandableRunwayTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDb = TestVnasData.NavigationDb ?? throw new InvalidOperationException("TestVnasData did not load a navigation database");
    }

    /// <summary>
    /// The landing distance available on <paramref name="designator"/> at <paramref name="airport"/>, printed beside the
    /// end's pavement length.
    /// </summary>
    private double Lda(string airport, string designator)
    {
        RunwayInfo? runway = _navDb.GetRunway(airport, designator);
        Assert.NotNull(runway);
        double lda = RunwayLandability.LandingDistanceAvailableFt(_navDb, runway);
        string declared = _navDb.DeclaredLandingDistanceFt(airport, designator)?.ToString("F0") ?? "none";
        _output.WriteLine($"{airport} {designator}: LDA={lda:F0}ft (declared {declared}), pavement={runway.PavementLengthFt:F0}ft");
        return lda;
    }

    private double LandingDistance(string type)
    {
        AircraftProfile? profile = AircraftProfileDatabase.Get(type);
        double landing = profile?.LandingDistance ?? 0;
        _output.WriteLine($"{type}: landing distance {landing:F0}ft, needs {landing * RunwayLandability.LandingDistanceFactor:F0}ft");
        return landing;
    }

    [Fact]
    public void Landable_Koak28R_ShortForB738_LandableForA320()
    {
        double lda = Lda("KOAK", "28R");
        LandingDistance("B738");
        LandingDistance("A320");

        Assert.Equal(5457, lda, 0);
        Assert.False(RunwayLandability.IsLandable(lda, "B738"));
        Assert.True(RunwayLandability.IsLandable(lda, "A320"));
    }

    /// <summary>
    /// The DH8A is short on KOAK 33 by 19 ft: it needs 3,395 ft against the 3,376 ft declared, so any change to the DH8A's
    /// landing distance figure can flip this test.
    /// </summary>
    [Fact]
    public void Landable_Koak33_ShortForDh8a()
    {
        double lda = Lda("KOAK", "33");
        LandingDistance("DH8A");

        Assert.Equal(3376, lda, 0);
        Assert.False(RunwayLandability.IsLandable(lda, "DH8A"));
    }

    /// <summary>
    /// The MD81's base profile carries no landing distance; its override gives it the MD82's 5,200 ft, so it needs 5,980 ft
    /// and KOAK 33's 3,376 ft is short.
    /// </summary>
    [Fact]
    public void Landable_Koak33_ShortForMd81()
    {
        double lda = Lda("KOAK", "33");

        Assert.Equal(5200, LandingDistance("MD81"));
        Assert.False(RunwayLandability.IsLandable(lda, "MD81"));
    }

    [Fact]
    public void Landable_Ksfo01L_ShortForA346AndB744()
    {
        double lda = Lda("KSFO", "01L");
        LandingDistance("A346");
        LandingDistance("B744");

        Assert.Equal(7010, lda, 0);
        Assert.False(RunwayLandability.IsLandable(lda, "A346"));
        Assert.False(RunwayLandability.IsLandable(lda, "B744"));
    }

    /// <summary>A landing distance available of exactly 1.15 times the B738's landing distance is enough: the rule is "at least".</summary>
    [Fact]
    public void Landable_AtExactlyTheFactor_IsLandable()
    {
        double need = RunwayLandability.LandingDistanceFactor * LandingDistance("B738");

        Assert.True(RunwayLandability.IsLandable(need, "B738"));
    }

    [Fact]
    public void Landable_OneFootUnderTheFactor_IsShort()
    {
        double need = RunwayLandability.LandingDistanceFactor * LandingDistance("B738");

        Assert.False(RunwayLandability.IsLandable(need - 1, "B738"));
    }

    /// <summary>
    /// KOAK 33 is the field's shortest end. A type with no landing distance (CL60's profile has none, XXXX has no profile),
    /// a helicopter (R44, H60) and a ground vehicle (VEH*) are never short. The helicopter and VEH rows pin the outcome
    /// only: today every helicopter profile carries no landing distance and no VEH type has a profile, so they pass through
    /// the no-figure rule and would stay green without the helicopter and VEH checks.
    /// </summary>
    [Theory]
    [InlineData("CL60")]
    [InlineData("XXXX")]
    [InlineData("")]
    [InlineData("R44")]
    [InlineData("H60")]
    [InlineData("VEH1")]
    [InlineData("VEHICLE")]
    public void Landable_NoFigureHeloAndVeh_CountAsLandable(string type)
    {
        double lda = Lda("KOAK", "33");
        LandingDistance(type);

        Assert.True(RunwayLandability.IsLandable(lda, type));
    }

    /// <summary>
    /// KSJC 12R/30L declares 8,587 ft landing 12R and 7,614 ft landing 30L over 11,001 ft of pavement: the navigation
    /// database answers each end's own declared landing distance, by either airport id and either designator form, and
    /// none for an end it does not know.
    /// </summary>
    [Fact]
    public void DeclaredLandingDistance_IsPerEnd()
    {
        double r12R = Lda("KSJC", "12R");
        double r30L = Lda("KSJC", "30L");
        Lda("KOAK", "28R");
        Lda("KOAK", "10L");

        Assert.Equal(8587, r12R, 0);
        Assert.Equal(7614, r30L, 0);
        Assert.Equal(7614, _navDb.DeclaredLandingDistanceFt("SJC", "30L") ?? 0, 0);
        Assert.Equal(7010, _navDb.DeclaredLandingDistanceFt("KSFO", "1L") ?? 0, 0);
        Assert.True(r12R < _navDb.GetRunway("KSJC", "12R")!.PavementLengthFt);
        Assert.Null(_navDb.DeclaredLandingDistanceFt("KSJC", "99"));
        Assert.Null(_navDb.DeclaredLandingDistanceFt("ZZZZ", "12R"));
    }
}
