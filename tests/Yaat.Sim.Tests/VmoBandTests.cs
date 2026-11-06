using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1435: a type whose type certificate limits Vmo by altitude flies, and is judged against, the limit at its
/// altitude rather than the band maximum. The bands are points joined by straight lines, constant below the first and
/// above the last; two points at one altitude are a step, and at that altitude the lower value applies.
/// </summary>
public sealed class VmoBandTests
{
    /// <summary>SMAX holds this many knots under the limit (FlightPhysics.SpeedMaxMarginKts).</summary>
    private const double SpeedMaxMarginKts = 10;

    private const double SpeedSnapKts = 2;

    public VmoBandTests() => TestVnasData.EnsureInitialized();

    public static TheoryData<string> BandedTypes => ["DH8D", "MD11", "CL60", "LJ35", "GLEX", "B463", "E170", "JS20", "CRJ1", "CRJ2", "CRJ7", "CRJ9"];

    [Theory]
    [InlineData("DH8D", 3_000, 245)] // flat band
    [InlineData("DH8D", 9_000, 263.5)] // straight line between two points
    [InlineData("DH8D", 14_000, 284)] // straight line between two points
    [InlineData("DH8D", 30_000, 286)] // above the last point
    [InlineData("CL60", 8_000, 300)] // a step: the lower value at the step altitude
    [InlineData("CL60", 8_001, 348)] // just past the step
    [InlineData("CL60", 24_365, 333)] // the falling line from 22,160 to 26,570
    [InlineData("CL60", 26_570, 318)] // the last point, held above
    [InlineData("JS20", 21_500, 197.5)] // the falling line from 18,000 to 25,000
    [InlineData("E170", 9_000, 310)] // the rising line from 8,000 to 10,000
    [InlineData("GLEX", 40_000, 340)] // above the last point
    public void VmoAtKts_IsTheBandAtThatAltitude(string type, double altitudeFt, double expectedKts) =>
        Assert.Equal(expectedKts, AircraftPerformance.VmoAtKts(type, altitudeFt)!.Value, 0.5);

    /// <summary>The B738 has no bands: its override Vmo, 340 kt (OpenAP), at every altitude.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8_000)]
    [InlineData(35_000)]
    public void VmoAtKts_TypeWithoutBands_IsItsScalarVmo(double altitudeFt) => Assert.Equal(340, AircraftPerformance.VmoAtKts("B738", altitudeFt));

    [Fact]
    public void MaxOperatingIasKts_Dh8dLow_IsItsLowBand() => Assert.Equal(245, AircraftPerformance.MaxOperatingIasKts("DH8D", 3_000)!.Value, 0.5);

    [Fact]
    public void BandValueAt_BelowAFirstPointAboveZero_IsTheFirstValue()
    {
        VmoBandPoint[] bands = [new(5_000, 250), new(10_000, 300)];

        Assert.Equal(250, AircraftPerformance.BandValueAt(bands, 2_000));
    }

    [Fact]
    public void BandValueAt_StepWrittenHigherFirst_IsTheLowerValueAtTheStep()
    {
        VmoBandPoint[] bands = [new(0, 300), new(8_000, 300), new(8_000, 250), new(20_000, 250)];

        Assert.Equal(250, AircraftPerformance.BandValueAt(bands, 8_000));
        Assert.Equal(300, AircraftPerformance.BandValueAt(bands, 7_999));
    }

    /// <summary>
    /// Descending under SMAX toward a lower band below a step, the aircraft slows ahead of the step and passes it at no more
    /// than the lower band less the SMAX margin.
    /// </summary>
    [Theory]
    [InlineData("LJ35", 16_000, "DM 100", 14_000, 290)]
    [InlineData("GLEX", 10_000, "DM 060", 8_000, 290)]
    public void Smax_DescendingIntoALowerBand_SlowsAheadOfTheStep(string type, double startFt, string descend, double stepFt, double lowKts)
    {
        double highKts = AircraftPerformance.VmoAtKts(type, startFt)!.Value - SpeedMaxMarginKts;
        AircraftState ac = Airborne(type, altitude: startFt, ias: highKts);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, descend).Success);

        double? iasAtStep = null;
        for (int tick = 0; (tick < 4 * 600) && (iasAtStep is null); tick++)
        {
            FlightPhysics.Update(ac, 0.25);
            if (ac.Altitude <= stepFt)
            {
                iasAtStep = ac.IndicatedAirspeed;
            }
        }

        Assert.NotNull(iasAtStep);
        Assert.True(iasAtStep <= lowKts + SpeedSnapKts, $"{type} passed {stepFt:F0} ft at {iasAtStep:F1} KIAS, past {lowKts}");
    }

    /// <summary>Climbing under SMAX into a higher band above a step, the aircraft speeds up only once past the step.</summary>
    [Theory]
    [InlineData("LJ35", 12_000, "CM 160", 14_000, 290)]
    [InlineData("GLEX", 6_000, "CM 100", 8_000, 290)]
    public void Smax_ClimbingIntoAHigherBand_SpeedsUpOnlyPastTheStep(string type, double startFt, string climb, double stepFt, double lowKts)
    {
        AircraftState ac = Airborne(type, altitude: startFt, ias: lowKts);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, climb).Success);

        double? assignedPastStep = null;
        for (int tick = 0; (tick < 4 * 600) && (assignedPastStep is null || assignedPastStep == lowKts); tick++)
        {
            FlightPhysics.Update(ac, 0.25);
            if (ac.Altitude <= stepFt)
            {
                Assert.Equal(lowKts, ac.Targets.AssignedSpeed);
            }
            else
            {
                assignedPastStep = ac.Targets.AssignedSpeed;
            }
        }

        Assert.NotNull(assignedPastStep);
        Assert.True(assignedPastStep > lowKts, $"{type} past {stepFt:F0} ft still held {assignedPastStep} kt");
    }

    [Fact]
    public void Speed_Crj9At30000_RefusesPastItsHighStretch()
    {
        AircraftState ac = Airborne("CRJ9", altitude: 30_000, ias: 280);
        ac.Targets.AssignedAltitude = 30_000;

        CommandResult result = Send(ac, "SPD 330");

        Assert.False(result.Success, result.Message);
        Assert.Contains("fastest", result.Message);
    }

    public static TheoryData<string, VmoBandPoint[]> BadPoints =>
        new()
        {
            { "goes down in altitude", [new(0, 300), new(10_000, 300), new(5_000, 300)] },
            { "more than two points", [new(0, 300), new(8_000, 300), new(8_000, 300), new(8_000, 300)] },
            { "non-positive KIAS", [new(0, 300), new(8_000, 0), new(10_000, 300)] },
        };

    [Theory]
    [MemberData(nameof(BadPoints))]
    public void Validation_BadPoints_ThrowNamingTheTypeAndTheProblem(string problem, VmoBandPoint[] points)
    {
        AircraftProfileOverride ov = new()
        {
            TypeCode = "ZZ03",
            Vmo = 300,
            VmoBands = points,
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AircraftProfileDatabase.ValidateVmoBands(ov));
        Assert.Contains("ZZ03", ex.Message);
        Assert.Contains(problem, ex.Message);
    }

    [Fact]
    public void Validation_EmptyBands_ThrowNamingTheTypeAndTheProblem()
    {
        AircraftProfileOverride ov = new()
        {
            TypeCode = "ZZ04",
            Vmo = 300,
            VmoBands = [],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AircraftProfileDatabase.ValidateVmoBands(ov));
        Assert.Contains("ZZ04", ex.Message);
        Assert.Contains("has no points", ex.Message);
    }

    [Fact]
    public void Smax_Dh8dLevelAt5000_SettlesUnderItsLowBand()
    {
        AircraftState ac = Airborne("DH8D", altitude: 5_000, ias: 200);

        Assert.True(Send(ac, "SMAX").Success);
        for (int tick = 0; tick < 4 * 120; tick++)
        {
            FlightPhysics.Update(ac, 0.25);
        }

        double expected = 245 - SpeedMaxMarginKts;
        Assert.Equal(expected, ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, expected - SpeedSnapKts, expected + SpeedSnapKts);
    }

    [Fact]
    public void Speed_Dh8dAt5000_RefusesPastItsLowBand()
    {
        AircraftState ac = Airborne("DH8D", altitude: 5_000, ias: 200);

        CommandResult result = Send(ac, "SPD 260");

        Assert.False(result.Success, result.Message);
        Assert.Contains("245 knots is the fastest", result.Message);
    }

    [Fact]
    public void Speed_Dh8dAt15000_AcceptsUnderItsHighBand()
    {
        AircraftState ac = Airborne("DH8D", altitude: 15_000, ias: 220);
        ac.Targets.AssignedAltitude = 15_000;

        CommandResult result = Send(ac, "SPD 260");

        Assert.True(result.Success, result.Message);
        Assert.Equal(260, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void Validation_BandsThatFallThenRise_ThrowNamingTheType()
    {
        AircraftProfileOverride ov = new()
        {
            TypeCode = "ZZ01",
            Vmo = 300,
            VmoBands = [new VmoBandPoint(0, 300), new VmoBandPoint(10_000, 250), new VmoBandPoint(20_000, 280)],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AircraftProfileDatabase.ValidateVmoBands(ov));
        Assert.Contains("ZZ01", ex.Message);
    }

    [Fact]
    public void Validation_VmoNotTheBandMaximum_ThrowsNamingTheType()
    {
        AircraftProfileOverride ov = new()
        {
            TypeCode = "ZZ02",
            Vmo = 320,
            VmoBands = [new VmoBandPoint(0, 250), new VmoBandPoint(10_000, 300)],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AircraftProfileDatabase.ValidateVmoBands(ov));
        Assert.Contains("ZZ02", ex.Message);
    }

    /// <summary>
    /// A banded type's profile never flies past its limit (the band and Mmo, <c>MaxOperatingIasKts</c>), since the bands
    /// are never raised to the profile: at every 100 ft to its ceiling its climb and descent speeds sit at or under that
    /// limit less the SMAX margin, and its level cruise (up to its cruise altitude) at or under the limit itself. The
    /// constant-IAS cruise below the cruise altitude is the model's extrapolation, which the envelope clamps in flight.
    /// </summary>
    [Theory]
    [MemberData(nameof(BandedTypes))]
    public void ProfileSpeeds_StayUnderTheBand(string type)
    {
        AircraftProfile profile = AircraftProfileDatabase.Get(type)!;
        AircraftCategory cat = AircraftCategorization.Categorize(type);
        var trips = new List<string>();
        for (double alt = 0; alt <= profile.Ceiling; alt += 100)
        {
            double limit = AircraftPerformance.MaxOperatingIasKts(type, alt)!.Value;
            double band = limit - SpeedMaxMarginKts;
            double climb = AircraftPerformance.ClimbSpeed(type, cat, alt);
            double descent = AircraftPerformance.DescentSpeed(type, cat, alt);
            double cruise = alt <= profile.CruiseAltitude ? AircraftPerformance.DefaultSpeed(type, cat, alt, targetAltitude: null) : 0;
            if ((climb > band) || (descent > band) || (cruise > limit))
            {
                trips.Add($"{alt:F0} ft: limit {limit:F1}, climb {climb:F1}, descent {descent:F1}, cruise {cruise:F1}");
            }
        }

        Assert.True(trips.Count == 0, $"{type} flies past its band at {string.Join("; ", trips)}");
    }

    private static AircraftState Airborne(string type, double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "PACK12",
            AircraftType = type,
            Position = new LatLon(37.62, -122.20),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KSFO",
                Destination = "KTCM",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(280);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(
            parsed.Value!,
            aircraft,
            TestDispatch.Context(new SerializableRandom(1), isScenarioScripted: false)
        );
    }
}
