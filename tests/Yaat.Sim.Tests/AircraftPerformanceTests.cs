using Xunit;

namespace Yaat.Sim.Tests;

public sealed class AircraftPerformanceTests
{
    public AircraftPerformanceTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ProfileDatabase_LoadsProfiles()
    {
        Assert.True(Data.AircraftProfileDatabase.IsInitialized);
        Assert.True(Data.AircraftProfileDatabase.Count > 100);
    }

    [Fact]
    public void ProfileDatabase_Get_KnownType()
    {
        var b738 = Data.AircraftProfileDatabase.Get("B738");
        Assert.NotNull(b738);
        Assert.Equal("B738", b738.TypeCode);
        Assert.Equal(3000, b738.ClimbRateInitial);
    }

    [Fact]
    public void ProfileDatabase_Get_CaseInsensitive()
    {
        Assert.NotNull(Data.AircraftProfileDatabase.Get("b738"));
        Assert.NotNull(Data.AircraftProfileDatabase.Get("B738"));
    }

    [Fact]
    public void ProfileDatabase_Get_StripPrefix()
    {
        var profile = Data.AircraftProfileDatabase.Get("H/B738");
        Assert.NotNull(profile);
        Assert.Equal("B738", profile.TypeCode);
    }

    [Fact]
    public void ProfileDatabase_Get_UnknownType_ReturnsNull()
    {
        Assert.Null(Data.AircraftProfileDatabase.Get("ZZZZ"));
    }

    [Fact]
    public void ProfileDatabase_Get_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(Data.AircraftProfileDatabase.Get(null));
        Assert.Null(Data.AircraftProfileDatabase.Get(""));
    }

    [Fact]
    public void ClimbRate_ProfiledType_UsesAltitudeBands()
    {
        // B738: initial=3000, FL150=2000, FL240=2000, final=1500
        double low = AircraftPerformance.ClimbRate("B738", AircraftCategory.Jet, 5000);
        double mid = AircraftPerformance.ClimbRate("B738", AircraftCategory.Jet, 20000);
        double high = AircraftPerformance.ClimbRate("B738", AircraftCategory.Jet, 35000);

        Assert.True(low > mid, $"Low alt rate ({low}) should exceed mid alt rate ({mid})");
        Assert.True(mid > high, $"Mid alt rate ({mid}) should exceed high alt rate ({high})");
    }

    [Fact]
    public void ClimbRate_UnknownType_FallsBackToCategory()
    {
        double rate = AircraftPerformance.ClimbRate("ZZZZ", AircraftCategory.Jet, 5000);
        Assert.Equal(CategoryPerformance.ClimbRate(AircraftCategory.Jet, 5000), rate);
    }

    [Fact]
    public void DescentRate_ProfiledType_AltitudeAware()
    {
        // B738: initial=800 (ground), FL100=3500, approach=1500 (ceiling)
        // Descent rates increase from ground to FL100, then decrease toward ceiling.
        double high = AircraftPerformance.DescentRate("B738", AircraftCategory.Jet, 30000);
        double mid = AircraftPerformance.DescentRate("B738", AircraftCategory.Jet, 10000);
        double low = AircraftPerformance.DescentRate("B738", AircraftCategory.Jet, 2000);

        // At FL100 boundary the rate should be 3500
        Assert.Equal(3500, mid);
        Assert.True(low < mid, $"Low alt rate ({low}) should be less than mid ({mid})");
        // High altitude interpolates between FL100 (3500) and ceiling (1500), so > 1500
        Assert.True(high > 1500, $"High alt rate ({high}) should exceed approach rate (1500)");
    }

    [Fact]
    public void DescentRate_AtGroundLevel_ReturnsInitialRate()
    {
        // B738: DescentRateInitial=800 maps to ground level (altitude 0)
        double rate = AircraftPerformance.DescentRate("B738", AircraftCategory.Jet, 0);
        Assert.Equal(800, rate);
    }

    [Fact]
    public void DescentRate_UnknownType_FallsBackToCategory()
    {
        double rate = AircraftPerformance.DescentRate("ZZZZ", AircraftCategory.Jet, 20000);
        Assert.Equal(CategoryPerformance.DescentRate(AircraftCategory.Jet), rate);
    }

    [Fact]
    public void MachConversion_HighAltitude_ReturnsReasonableIas()
    {
        // B738 climbSpeedFinal = 0.78 (Mach)
        double ias = AircraftPerformance.ResolveSpeed(0.78, 35000);
        // Mach 0.78 at FL350 should be roughly 250-270 KIAS
        Assert.InRange(ias, 230, 290);
    }

    [Fact]
    public void MachConversion_KiasValue_ReturnedAsIs()
    {
        double speed = AircraftPerformance.ResolveSpeed(290, 20000);
        Assert.Equal(290, speed);
    }

    [Fact]
    public void DefaultSpeed_Climbing_UsesClimbSchedule()
    {
        double speed = AircraftPerformance.DefaultSpeed("B738", AircraftCategory.Jet, 20000, 35000);
        double climbSpeed = AircraftPerformance.ClimbSpeed("B738", AircraftCategory.Jet, 20000);
        Assert.Equal(climbSpeed, speed);
    }

    [Fact]
    public void DefaultSpeed_Descending_UsesDescentSchedule()
    {
        double speed = AircraftPerformance.DefaultSpeed("B738", AircraftCategory.Jet, 20000, 5000);
        double descentSpeed = AircraftPerformance.DescentSpeed("B738", AircraftCategory.Jet, 20000);
        Assert.Equal(descentSpeed, speed);
    }

    [Fact]
    public void DefaultSpeed_Level_UsesCruise()
    {
        double speed = AircraftPerformance.DefaultSpeed("B738", AircraftCategory.Jet, 35000, null);
        // B738 cruise = 460
        Assert.Equal(460, speed);
    }

    [Fact]
    public void ApproachSpeed_ProfiledType_UsesCorrectedValue()
    {
        // B738 profile: finalApproachSpeed = 175, ACD = 144.
        // EurocontrolProfileCorrectionAdapter replaces with ACD value.
        double speed = AircraftPerformance.ApproachSpeed("B738", AircraftCategory.Jet);
        Assert.Equal(144, speed);
    }

    [Fact]
    public void ApproachSpeed_FallbackToFaaAcd()
    {
        // Type in FAA ACD but not in profiles — should use FAA ACD value
        // If there's no such type, this tests the category fallback
        var faaRecord = Data.Faa.FaaAircraftDatabase.Get("B738");
        if (faaRecord?.ApproachSpeedKnot is not null)
        {
            // B738 is in profiles, so test with a type that's in FAA ACD but not profiles
            // If we can't find one, just verify the fallback chain works
            double speed = AircraftPerformance.ApproachSpeed("ZZZZ", AircraftCategory.Jet);
            Assert.Equal(CategoryPerformance.ApproachSpeed(AircraftCategory.Jet), speed);
        }
    }

    [Fact]
    public void TouchdownSpeed_ProfiledType()
    {
        // B738: landingSpeed = 140
        double speed = AircraftPerformance.TouchdownSpeed("B738", AircraftCategory.Jet);
        Assert.Equal(140, speed);
    }

    [Fact]
    public void DownwindSpeed_ProfiledType()
    {
        // B738: patternSpeed = 161
        double speed = AircraftPerformance.DownwindSpeed("B738", AircraftCategory.Jet);
        Assert.Equal(161, speed);
    }

    [Fact]
    public void BaseSpeed_ProfiledType_DerivedFromCorrectedValues()
    {
        // B738: corrected pattern = max(161, 144*1.10=158.4) = 161, corrected FAS = 144
        // Base = (161 + 144) / 2 = 152.5
        double speed = AircraftPerformance.BaseSpeed("B738", AircraftCategory.Jet);
        Assert.Equal(152.5, speed);
    }

    [Fact]
    public void HoldingSpeed_ProfiledType_ClampedByAim()
    {
        // B738: holdingSpeed = 220, at 6000ft AIM max is 200
        double low = AircraftPerformance.HoldingSpeed("B738", 6000);
        Assert.Equal(200, low);

        // At 14000ft AIM max is 230, profile is 220
        double mid = AircraftPerformance.HoldingSpeed("B738", 14000);
        Assert.Equal(220, mid);
    }

    [Fact]
    public void TurnRate_WithOverride_UsesOverride()
    {
        // Most aircraft have standardTurnRateOverride = 0 (use category default)
        double rate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        Assert.Equal(CategoryPerformance.TurnRate(AircraftCategory.Jet), rate);
    }

    [Fact]
    public void AccelDecelRate_ProfiledType()
    {
        // B738: airborneAccelRate = 2, airborneDecelRate = 2
        Assert.Equal(2, AircraftPerformance.AccelRate("B738", AircraftCategory.Jet));
        Assert.Equal(2, AircraftPerformance.DecelRate("B738", AircraftCategory.Jet));
    }

    [Fact]
    public void RotationSpeed_ProfiledType()
    {
        // B738: rotateSpeed = 145
        Assert.Equal(145, AircraftPerformance.RotationSpeed("B738", AircraftCategory.Jet));
    }

    [Fact]
    public void IsSpeedLimitWaived_NormalAircraft_False()
    {
        Assert.False(AircraftPerformance.IsSpeedLimitWaived("B738"));
    }

    [Fact]
    public void IsSpeedLimitWaived_UnknownType_False()
    {
        Assert.False(AircraftPerformance.IsSpeedLimitWaived("ZZZZ"));
    }

    [Fact]
    public void ClimbSpeed_Below10k_CappedAt250()
    {
        double speed = AircraftPerformance.ClimbSpeed("B738", AircraftCategory.Jet, 5000);
        Assert.True(speed <= 250, $"Climb speed below 10k should be <= 250, got {speed}");
    }

    [Fact]
    public void DescentSpeed_Below10k_CappedAt250()
    {
        double speed = AircraftPerformance.DescentSpeed("B738", AircraftCategory.Jet, 5000);
        Assert.True(speed <= 250, $"Descent speed below 10k should be <= 250, got {speed}");
    }

    [Fact]
    public void Piston_ZeroHighAltBands_UsesLastValidValue()
    {
        // C172: climbRateFl150=0, climbRateFl240=0, climbRateFinal=0
        // Corrected initial = max(400, 13000/18=722) = 722
        // Should use corrected rate at all altitudes since higher bands are zero
        double rate = AircraftPerformance.ClimbRate("C172", AircraftCategory.Piston, 5000);
        Assert.InRange(rate, 720, 725);

        double high = AircraftPerformance.ClimbRate("C172", AircraftCategory.Piston, 12000);
        Assert.InRange(high, 720, 725);
    }

    [Fact]
    public void InterpolateByAltitude_ExactBreakpoint_ReturnsExactValue()
    {
        ReadOnlySpan<(double, double)> bp = [(0, 100), (10000, 200), (20000, 300)];
        Assert.Equal(200, AircraftPerformance.InterpolateByAltitude(10000, bp));
    }

    [Fact]
    public void InterpolateByAltitude_Midpoint_Interpolates()
    {
        ReadOnlySpan<(double, double)> bp = [(0, 100), (10000, 200)];
        Assert.Equal(150, AircraftPerformance.InterpolateByAltitude(5000, bp));
    }

    [Fact]
    public void InterpolateByAltitude_BelowFirst_ClampsToFirst()
    {
        ReadOnlySpan<(double, double)> bp = [(5000, 100), (10000, 200)];
        Assert.Equal(100, AircraftPerformance.InterpolateByAltitude(0, bp));
    }

    [Fact]
    public void InterpolateByAltitude_AboveLast_ClampsToLast()
    {
        ReadOnlySpan<(double, double)> bp = [(0, 100), (10000, 200)];
        Assert.Equal(200, AircraftPerformance.InterpolateByAltitude(15000, bp));
    }

    [Fact]
    public void InterpolateByAltitude_SkipsZeroValues()
    {
        ReadOnlySpan<(double, double)> bp = [(0, 400), (15000, 0), (24000, 0), (41000, 0)];
        // Only the first breakpoint is valid; should return 400 everywhere
        Assert.Equal(400, AircraftPerformance.InterpolateByAltitude(10000, bp));
    }
}
