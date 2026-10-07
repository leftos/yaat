using Xunit;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// <see cref="TurnAboutFit"/> over real FAA aircraft characteristics rows: the pivot radius R = max(MGW/2, 0.466·WB), and a
/// fit when both the outer main tyre (R + MGW/2) and the nose gear (√(R² + WB²)) stay within the type's TDG half-width
/// (AC 150/5300-13B Table 4-2), with the category fallbacks for a type the database lacks or has no gear figures for.
/// </summary>
public class TurnAboutFitTests
{
    public TurnAboutFitTests() => Yaat.Sim.Testing.TestVnasData.EnsureAircraftDataInitialized();

    [Theory]
    [InlineData("C208", true)]
    [InlineData("C172", true)]
    [InlineData("E55P", false)]
    [InlineData("AT76", false)]
    [InlineData("C25A", false)]
    [InlineData("B738", false)]
    [InlineData("DH8D", false)]
    public void Evaluate_RealRecord_FitsByTheGearFormula(string type, bool expectedFits)
    {
        FaaAircraftRecord record = FaaAircraftDatabase.Get(type) ?? throw new InvalidOperationException($"the FAA database has no {type}");
        double gearWidthFt = record.MainGearWidthFt ?? throw new InvalidOperationException($"{type} has no main-gear width");
        double wheelbaseFt = record.WheelbaseFt ?? throw new InvalidOperationException($"{type} has no wheelbase");
        double expectedRadiusFt = Math.Max(gearWidthFt / 2.0, 0.466 * wheelbaseFt);
        double outerTyreFt = expectedRadiusFt + (gearWidthFt / 2.0);
        double noseFt = Math.Sqrt((expectedRadiusFt * expectedRadiusFt) + (wheelbaseFt * wheelbaseFt));
        double expectedHalfWidthFt = TdgHalfWidthFt(record.Tdg);

        TurnAboutFitResult fit = TurnAboutFit.Evaluate(type, AircraftCategorization.Categorize(type));

        Assert.Equal(expectedHalfWidthFt, fit.HalfWidthFt, 6);
        Assert.Equal(expectedRadiusFt, fit.RadiusFt, 6);
        Assert.Equal(Math.Max(outerTyreFt, noseFt) <= expectedHalfWidthFt, fit.Fits);
        Assert.Equal(expectedFits, fit.Fits);
    }

    /// <summary>
    /// The gear fit over the real ACD rows the taildragger and broken-row rules decide, and the tricycle types either
    /// side of the thresholds: type, whether it fits, and the span or limiting figure the failure message quotes.
    /// </summary>
    [Theory]
    [InlineData("PA18", true, "tail swing + MGW 23.0 / 25")]
    [InlineData("C140", true, "tail swing + MGW 24.8 / 25")]
    [InlineData("C120", true, "tail swing + MGW 24.8 / 25")]
    [InlineData("GC1", true, "tail swing + MGW 24.1 / 25")]
    [InlineData("CH7B", true, "broken row, wheelbase 1.14 x length -> piston fits")]
    [InlineData("PA11", true, "broken row, wheelbase 0.95 x length -> piston fits")]
    [InlineData("BE58", true, "outer tyre 9.6 / 12.5")]
    [InlineData("BT36", true, "nose 10.2 / 12.5 (MGW corrected from 12.8 to 9.6)")]
    [InlineData("PA25", false, "tail swing + MGW 25.2 / 25")]
    [InlineData("C180", false, "tail swing + MGW 28.9 / 25")]
    [InlineData("C185", false, "tail swing + MGW 31.6 / 25")]
    [InlineData("DHC2", false, "tail swing + MGW 33.4 / 25")]
    [InlineData("T6", false, "tail swing + MGW 32.6 / 25")]
    [InlineData("P51", false, "tail swing + MGW 29.7 / 25 (wheelbase/length 0.505)")]
    [InlineData("DC3", false, "tail swing + MGW 57.8 / 50")]
    [InlineData("BE18", false, "tail swing + MGW 50.7 / 35")]
    [InlineData("AT5T", false, "broken row, wheelbase 1.13 x length -> turboprop refuses")]
    [InlineData("MU2", false, "tricycle (wheelbase/length 0.46), nose 19.9 / 12.5")]
    [InlineData("AC56", false, "tricycle, nose 18.4 / 12.5")]
    [InlineData("PA31", false, "outer tyre 13.8 / 12.5")]
    [InlineData("C402", false, "outer tyre 14.7 / 12.5")]
    [InlineData("C414", false, "outer tyre 17.4 / 12.5")]
    [InlineData("C340", false, "outer tyre 12.9 / 12.5")]
    public void Evaluate_TaildraggerAndBrokenRow_FitByTheirOwnRule(string type, bool expectedFits, string figure)
    {
        TurnAboutFitResult fit = TurnAboutFit.Evaluate(type, AircraftCategorization.Categorize(type));

        Assert.True(fit.Fits == expectedFits, $"{type}: expected {expectedFits} ({figure}); got {fit}");
    }

    /// <summary>The worked example: a C208 pivots on its inner main wheel, R 5.85 ft, outer tyre 11.7 ft, nose 9.7 ft, inside 12.5 ft.</summary>
    [Fact]
    public void Evaluate_C208_PivotsOnItsInnerMainWheel()
    {
        TurnAboutFitResult fit = TurnAboutFit.Evaluate("C208", AircraftCategory.Turboprop);

        Assert.True(fit.Fits);
        Assert.Equal(5.85, fit.RadiusFt, 2);
        Assert.Equal(12.5, fit.HalfWidthFt, 6);
    }

    /// <summary>
    /// A taildragger turns on its braked main wheel, so its pivot radius is MGW/2 (PA18, 6.0 ft gear → 3.0 ft), and it is
    /// checked against its own group's half-width (PA18 a 1A, BE18 a 2A).
    /// </summary>
    [Fact]
    public void Evaluate_Taildragger_PivotsOnItsMainWheelAtItsGroupsHalfWidth()
    {
        TurnAboutFitResult pa18 = TurnAboutFit.Evaluate("PA18", AircraftCategorization.Categorize("PA18"));
        Assert.Equal(3.0, pa18.RadiusFt, 6);
        Assert.Equal(12.5, pa18.HalfWidthFt, 6);

        TurnAboutFitResult be18 = TurnAboutFit.Evaluate("BE18", AircraftCategorization.Categorize("BE18"));
        Assert.Equal(17.5, be18.HalfWidthFt, 6);
    }

    [Theory]
    [InlineData(AircraftCategory.Piston, true)]
    [InlineData(AircraftCategory.Helicopter, true)]
    [InlineData(AircraftCategory.Turboprop, false)]
    [InlineData(AircraftCategory.Jet, false)]
    public void Evaluate_TypeAbsentFromTheDatabase_FitsByCategory(AircraftCategory category, bool expectedFits)
    {
        Assert.Null(FaaAircraftDatabase.Get("ZZZZ"));

        TurnAboutFitResult fit = TurnAboutFit.Evaluate("ZZZZ", category);

        Assert.Equal(expectedFits, fit.Fits);
        Assert.Equal(CategoryPerformance.TightTurnFloorRadiusFt(category), fit.RadiusFt, 6);
        Assert.Equal(12.5, fit.HalfWidthFt, 6);
    }

    [Fact]
    public void Evaluate_NullType_FitsByCategory()
    {
        Assert.True(TurnAboutFit.Evaluate(null, AircraftCategory.Piston).Fits);
        Assert.False(TurnAboutFit.Evaluate(null, AircraftCategory.Jet).Fits);
    }

    /// <summary>
    /// Every record of the cached cycle carries main-gear width and wheelbase, so the missing-gear branch is tested on real
    /// rows with one figure nulled: a TDG 1A non-jet fits; a TDG 1A jet and a TDG 1B type do not. A non-jet is drawn on
    /// the piston tight-turn radius, a jet on its own; each takes its TDG's half-width.
    /// </summary>
    [Theory]
    [InlineData("C208", true, false, true, AircraftCategory.Piston)]
    [InlineData("C510", false, true, false, AircraftCategory.Jet)]
    [InlineData("AT76", true, false, false, AircraftCategory.Piston)]
    public void Evaluate_TdgKnownGearMissing_OnlyTdg1ANonJetFits(
        string type,
        bool nullGearWidth,
        bool nullWheelbase,
        bool expectedFits,
        AircraftCategory radiusCategory
    )
    {
        FaaAircraftRecord real = FaaAircraftDatabase.Get(type) ?? throw new InvalidOperationException($"the FAA database has no {type}");
        FaaAircraftRecord missing = real with
        {
            MainGearWidthFt = nullGearWidth ? null : real.MainGearWidthFt,
            WheelbaseFt = nullWheelbase ? null : real.WheelbaseFt,
        };
        AircraftCategory category = AircraftCategorization.Categorize(type);

        TurnAboutFitResult fit = TurnAboutFit.EvaluateRecord(missing, category);

        Assert.Equal(expectedFits, fit.Fits);
        Assert.Equal(CategoryPerformance.TightTurnFloorRadiusFt(radiusCategory), fit.RadiusFt, 6);
        Assert.Equal(TdgHalfWidthFt(real.Tdg), fit.HalfWidthFt, 6);
    }

    /// <summary>
    /// The bound a re-aimed cut's main-gear path must keep within of a centreline covers the nose gear as well as the main
    /// gear: on a turn of the type's radius R the nose runs √(R² + WB²) − R outside the path, which for an A5 (5.3 ft main
    /// gear, 6.6 ft wheelbase) is more than half its main-gear width, so the bound is the half-width less the larger.
    /// </summary>
    [Fact]
    public void ReAimCutBound_A5_CoversTheNoseGear()
    {
        FaaAircraftRecord record = FaaAircraftDatabase.Get("A5") ?? throw new InvalidOperationException("the FAA database has no A5");
        double gearWidthFt = record.MainGearWidthFt ?? throw new InvalidOperationException("A5 has no main-gear width");
        double wheelbaseFt = record.WheelbaseFt ?? throw new InvalidOperationException("A5 has no wheelbase");
        TurnAboutFitResult fit = TurnAboutFit.Evaluate("A5", AircraftCategorization.Categorize("A5"));
        double noseOutsideFt = Math.Sqrt((fit.RadiusFt * fit.RadiusFt) + (wheelbaseFt * wheelbaseFt)) - fit.RadiusFt;
        Assert.True(noseOutsideFt > gearWidthFt / 2.0, $"the A5's nose runs {noseOutsideFt:F2} ft outside its path, not past its main gear");

        double boundFt = GroundNavigator.ReAimCutBoundFt(fit, gearWidthFt, wheelbaseFt);

        Assert.Equal(fit.HalfWidthFt - noseOutsideFt, boundFt, 6);
    }

    /// <summary>A record whose TDG is not one of AC 150/5300-13B's is judged as a type absent from the database.</summary>
    [Fact]
    public void Evaluate_UnrecognisedTdg_FitsByCategory()
    {
        FaaAircraftRecord real = FaaAircraftDatabase.Get("C208") ?? throw new InvalidOperationException("the FAA database has no C208");
        FaaAircraftRecord odd = real with { Tdg = "7" };

        TurnAboutFitResult fit = TurnAboutFit.EvaluateRecord(odd, AircraftCategory.Turboprop);

        Assert.False(fit.Fits);
        Assert.Equal(CategoryPerformance.TightTurnFloorRadiusFt(AircraftCategory.Turboprop), fit.RadiusFt, 6);
        Assert.Equal(12.5, fit.HalfWidthFt, 6);
    }

    /// <summary>Half the taxiway width AC 150/5300-13B Table 4-2 gives <paramref name="tdg"/>.</summary>
    private static double TdgHalfWidthFt(string? tdg) =>
        tdg switch
        {
            "1A" or "1B" => 12.5,
            "2A" or "2B" => 17.5,
            "3" or "4" => 25.0,
            "5" or "6" => 37.5,
            _ => throw new InvalidOperationException($"unexpected TDG '{tdg}'"),
        };
}
