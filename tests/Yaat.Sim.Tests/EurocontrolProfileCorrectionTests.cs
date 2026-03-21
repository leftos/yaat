using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="EurocontrolProfileCorrectionAdapter"/>.
/// Validates corrected speeds and climb rates against known real-world values.
/// </summary>
public sealed class EurocontrolProfileCorrectionTests
{
    private readonly EurocontrolProfileCorrectionAdapter _adapter = new();

    // C172 profile (from AircraftProfiles.json)
    private static readonly AircraftProfile C172Profile = new()
    {
        TypeCode = "C172",
        FinalApproachSpeed = 81,
        PatternSpeed = 74,
        ClimbSpeedInitial = 90,
        ClimbRateInitial = 400,
        InitialApproachSpeed = 110,
        Ceiling = 13000,
        RotateSpeed = 60,
        LandingSpeed = 65,
    };

    // C172 FAA ACD record
    private static readonly FaaAircraftRecord C172Acd = new()
    {
        IcaoCode = "C172",
        ApproachSpeedKnot = 62,
        PhysicalClassEngine = "Piston",
        NumEngines = 1,
    };

    // Real C172 values for reference:
    //   Vr=55, Vy=75, Pattern=80, Base=70, Final=65, ClimbRate=730 fpm

    [Fact]
    public void C172_FinalApproachSpeed_UsesAcdVref()
    {
        double fas = _adapter.FinalApproachSpeed(C172Profile, C172Acd);

        // ACD says 62 kts (real: 65). Profile had 81 — way too high.
        Assert.Equal(62, fas);
    }

    [Fact]
    public void C172_PatternSpeed_RaisedToVrefFloor()
    {
        double pattern = _adapter.PatternSpeed(C172Profile, C172Acd);

        // max(74, 62 × 1.25=77.5) = 77.5. Real: 80. Profile was 74.
        Assert.Equal(77.5, pattern, 1);
        Assert.True(pattern > C172Profile.PatternSpeed);
    }

    [Fact]
    public void C172_BaseSpeed_MidpointOfPatternAndFas()
    {
        double baseSpd = _adapter.BaseSpeed(C172Profile, C172Acd);

        // (77.5 + 62) / 2 = 69.75. Real: 70.
        Assert.InRange(baseSpd, 68, 72);
    }

    [Fact]
    public void C172_ClimbSpeed_CappedByVrefMultiplier()
    {
        double climbSpd = _adapter.ClimbSpeedInitial(C172Profile, C172Acd);

        // min(90, 62 × 1.25=77.5) = 77.5. Real: 75. Profile was 90.
        Assert.Equal(77.5, climbSpd, 1);
        Assert.True(climbSpd < C172Profile.ClimbSpeedInitial);
    }

    [Fact]
    public void C172_ClimbRate_EstimatedFromCeiling()
    {
        double cr = _adapter.ClimbRateInitial(C172Profile, C172Acd);

        // max(400, 13000/18=722) = 722. Real: 730. Profile was 400.
        Assert.InRange(cr, 700, 750);
        Assert.True(cr > C172Profile.ClimbRateInitial);
    }

    [Fact]
    public void C172_InitialApproachSpeed_ScaledByRatio()
    {
        double ias = _adapter.InitialApproachSpeed(C172Profile, C172Acd);

        // 110 × (62/81) = 84.2. Profile was 110.
        Assert.InRange(ias, 82, 86);
    }

    // --- No ACD data: all values pass through unchanged ---

    [Fact]
    public void NoAcd_AllValuesPassThrough()
    {
        Assert.Equal(81, _adapter.FinalApproachSpeed(C172Profile, null));
        Assert.Equal(74, _adapter.PatternSpeed(C172Profile, null));
        Assert.Equal(90, _adapter.ClimbSpeedInitial(C172Profile, null));
        Assert.Equal(400, _adapter.ClimbRateInitial(C172Profile, null));
        Assert.Equal(110, _adapter.InitialApproachSpeed(C172Profile, null));
    }

    // --- Jet: climb rate NOT corrected (jets have accurate profile data) ---

    [Fact]
    public void Jet_ClimbRate_NotCorrected()
    {
        var b738Profile = new AircraftProfile
        {
            TypeCode = "B738",
            ClimbRateInitial = 3000,
            Ceiling = 41000,
            FinalApproachSpeed = 175,
        };
        var b738Acd = new FaaAircraftRecord
        {
            IcaoCode = "B738",
            ApproachSpeedKnot = 144,
            PhysicalClassEngine = "Jet",
            NumEngines = 2,
        };

        double cr = _adapter.ClimbRateInitial(b738Profile, b738Acd);
        Assert.Equal(3000, cr);
    }

    // --- Twin piston: different divisor ---

    [Fact]
    public void TwinPiston_ClimbRate_UsesTwinDivisor()
    {
        var be58Profile = new AircraftProfile
        {
            TypeCode = "BE58",
            ClimbRateInitial = 1500,
            Ceiling = 20000,
            FinalApproachSpeed = 118,
        };
        var be58Acd = new FaaAircraftRecord
        {
            IcaoCode = "BE58",
            ApproachSpeedKnot = 95,
            PhysicalClassEngine = "Piston",
            NumEngines = 2,
        };

        double cr = _adapter.ClimbRateInitial(be58Profile, be58Acd);

        // max(1500, 20000/13=1538) = 1538. Real: 1500. Profile already good.
        Assert.InRange(cr, 1500, 1550);
    }

    // --- Turbocharged piston: larger divisor ---

    [Fact]
    public void TurbochargedPiston_ClimbRate_UsesLargerDivisor()
    {
        var c210Profile = new AircraftProfile
        {
            TypeCode = "C210",
            ClimbRateInitial = 500,
            Ceiling = 27000, // > 20k = likely turbocharged
            FinalApproachSpeed = 93,
        };
        var c210Acd = new FaaAircraftRecord
        {
            IcaoCode = "C210",
            ApproachSpeedKnot = 85,
            PhysicalClassEngine = "Piston",
            NumEngines = 1,
        };

        double cr = _adapter.ClimbRateInitial(c210Profile, c210Acd);

        // max(500, 27000/28=964) = 964. Real: 930.
        Assert.InRange(cr, 940, 980);
    }

    // --- Passthrough adapter returns raw profile values ---

    [Fact]
    public void PassthroughAdapter_ReturnsProfileValues()
    {
        var passthrough = new PassthroughProfileCorrectionAdapter();

        Assert.Equal(81, passthrough.FinalApproachSpeed(C172Profile, C172Acd));
        Assert.Equal(74, passthrough.PatternSpeed(C172Profile, C172Acd));
        Assert.Equal(90, passthrough.ClimbSpeedInitial(C172Profile, C172Acd));
        Assert.Equal(400, passthrough.ClimbRateInitial(C172Profile, C172Acd));
    }
}
