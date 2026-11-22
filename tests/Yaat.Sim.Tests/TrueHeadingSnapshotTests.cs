using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// A snapshot restore gives back the heading it captured bit for bit. A heading first made from a negative input (a left
/// turn across north) is normalised once by <see cref="TrueHeading"/>'s constructor; normalising the stored value a second
/// time can move its last bit, and a rewind then drifts from the live run by one ulp. <see cref="TrueHeading.FromSnapshot"/>
/// keeps a stored value in [0, 360) as it is and normalises anything else as the constructor does.
/// </summary>
public class TrueHeadingSnapshotTests
{
    /// <summary>A raw heading whose normalised value changes its last bit when normalised again.</summary>
    private const double NegativeInputDeg = -0.7749301944036802;

    private static long Bits(double degrees) => BitConverter.DoubleToInt64Bits(degrees);

    [Fact]
    public void HeadingMadeFromANegativeInput_SurvivesSnapshotRoundTripBitForBit()
    {
        var aircraft = new AircraftState
        {
            Callsign = "SKW5966",
            AircraftType = "CRJ7",
            IsOnGround = true,
            TrueHeading = new TrueHeading(NegativeInputDeg),
            TrueTrack = new TrueHeading(NegativeInputDeg),
        };
        Assert.NotEqual(Bits(aircraft.TrueHeading.Degrees), Bits(new TrueHeading(aircraft.TrueHeading.Degrees).Degrees));

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        Assert.Equal(Bits(aircraft.TrueHeading.Degrees), Bits(restored.TrueHeading.Degrees));
        Assert.Equal(Bits(aircraft.TrueTrack.Degrees), Bits(restored.TrueTrack.Degrees));
    }

    /// <summary>
    /// A final approach course stored from a negative input comes back from a final approach phase's restore bit for bit
    /// (it reaches the phase through a pattern variable, not straight off the DTO).
    /// </summary>
    [Fact]
    public void FinalApproachCourseMadeFromANegativeInput_SurvivesPhaseRoundTripBitForBit()
    {
        double stored = new TrueHeading(NegativeInputDeg).Degrees;
        Assert.NotEqual(Bits(stored), Bits(new TrueHeading(stored).Degrees));
        JsonObject json = JsonSerializer.SerializeToNode((FinalApproachPhaseDto)new FinalApproachPhase().ToSnapshot())!.AsObject();
        json[nameof(FinalApproachPhaseDto.FinalApproachCourseDeg)] = stored;
        FinalApproachPhaseDto dto = Assert.IsType<FinalApproachPhaseDto>(json.Deserialize<FinalApproachPhaseDto>());

        FinalApproachPhaseDto restored = Assert.IsType<FinalApproachPhaseDto>(FinalApproachPhase.FromSnapshot(dto).ToSnapshot());

        Assert.Equal(Bits(stored), Bits(Assert.IsType<double>(restored.FinalApproachCourseDeg)));
    }

    /// <summary>
    /// A runway crossing's segment set-up heading stored from a negative input comes back from the phase's restore bit for
    /// bit: a crossing restored before its first tick snapshots the set-up pose it came back with.
    /// </summary>
    [Fact]
    public void CrossingSetupHeadingMadeFromANegativeInput_SurvivesPhaseRoundTripBitForBit()
    {
        double stored = StoredFromNegativeInput();
        RunwayInfo runway = Oak28R();
        CrossingRunwayPhaseDto dto = WithFields<CrossingRunwayPhaseDto>(
            new CrossingRunwayPhase(approachNodeId: 1, targetNodeId: 2, runwayId: "28R").ToSnapshot(),
            (nameof(CrossingRunwayPhaseDto.SegmentSetupLat), JsonValue.Create(runway.Lat1)),
            (nameof(CrossingRunwayPhaseDto.SegmentSetupLon), JsonValue.Create(runway.Lon1)),
            (nameof(CrossingRunwayPhaseDto.SegmentSetupHeadingDeg), JsonValue.Create(stored))
        );

        PhaseDto restored = CrossingRunwayPhase.FromSnapshot(dto, groundLayout: null).ToSnapshot();

        Assert.Equal(Bits(stored), Bits(Assert.IsType<double>(Assert.IsType<CrossingRunwayPhaseDto>(restored).SegmentSetupHeadingDeg)));
    }

    /// <summary>A procedure turn's inbound course stored from a negative input comes back from the approach phase's restore bit for bit.</summary>
    [Fact]
    public void ProcedureTurnInboundCourseMadeFromANegativeInput_SurvivesPhaseRoundTripBitForBit()
    {
        double stored = StoredFromNegativeInput();
        RunwayInfo runway = Oak28R();
        ApproachNavigationPhaseDto dto = WithFields<ApproachNavigationPhaseDto>(
            new ApproachNavigationPhase { Fixes = [] }.ToSnapshot(),
            (nameof(ApproachNavigationPhaseDto.PostTurnAnchorLat), JsonValue.Create(runway.Lat1)),
            (nameof(ApproachNavigationPhaseDto.PostTurnAnchorLon), JsonValue.Create(runway.Lon1)),
            (nameof(ApproachNavigationPhaseDto.PostTurnInboundCourseDeg), JsonValue.Create(stored))
        );

        PhaseDto restored = ApproachNavigationPhase.FromSnapshot(dto).ToSnapshot();

        Assert.Equal(Bits(stored), Bits(Assert.IsType<double>(Assert.IsType<ApproachNavigationPhaseDto>(restored).PostTurnInboundCourseDeg)));
    }

    /// <summary>
    /// A line-up's plan start heading stored from a negative input comes back from the phase's restore bit for bit: a running
    /// line-up restored before its rebuild snapshots the manoeuvre it came back with.
    /// </summary>
    [Fact]
    public void LineUpPlanStartHeadingMadeFromANegativeInput_SurvivesPhaseRoundTripBitForBit()
    {
        double stored = StoredFromNegativeInput();
        RunwayInfo runway = Oak28R();
        LineUpPhaseDto dto = WithFields<LineUpPhaseDto>(
            new LineUpPhase().ToSnapshot(),
            (nameof(LineUpPhaseDto.Status), JsonValue.Create((int)PhaseStatus.Active)),
            (nameof(LineUpPhaseDto.ManeuverState), JsonValue.Create((int)LineUpPhase.State.NoseOut)),
            (nameof(LineUpPhaseDto.PlanStartLat), JsonValue.Create(runway.Lat1)),
            (nameof(LineUpPhaseDto.PlanStartLon), JsonValue.Create(runway.Lon1)),
            (nameof(LineUpPhaseDto.PlanStartHeadingDeg), JsonValue.Create(stored))
        );

        PhaseDto restored = LineUpPhase.FromSnapshot(dto).ToSnapshot();

        Assert.Equal(Bits(stored), Bits(Assert.IsType<double>(Assert.IsType<LineUpPhaseDto>(restored).PlanStartHeadingDeg)));
    }

    /// <summary>
    /// A follow's climb-out upwind heading and its lead-base leg track, each stored from a negative input, come back from
    /// the follow phase's restore bit for bit.
    /// </summary>
    [Fact]
    public void FollowClimbOutAndLeadBaseHeadingsMadeFromANegativeInput_SurvivePhaseRoundTripBitForBit()
    {
        double stored = StoredFromNegativeInput();
        RunwayInfo runway = Oak28R();
        PatternWaypoints waypoints = PatternGeometry.Compute(
            new PatternGeometryRequest(runway, AircraftCategory.Piston, "C172", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = null,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        var climbOutGate = new FollowClimbOutGateDto
        {
            DepartureEndLat = runway.Lat2,
            DepartureEndLon = runway.Lon2,
            UpwindHeadingDeg = stored,
            MinTurnAltitude = waypoints.PatternAltitude,
        };
        var leadBase = new FollowLeadBaseDto
        {
            Runway = runway.ToSnapshot(),
            Waypoints = waypoints.ToSnapshot(),
            StartLat = waypoints.BaseTurnLat,
            StartLon = waypoints.BaseTurnLon,
            LegTrackDeg = stored,
        };
        VfrFollowPhaseDto dto = WithFields<VfrFollowPhaseDto>(
            new VfrFollowPhase("N172SP", patternReturn: null).ToSnapshot(),
            (nameof(VfrFollowPhaseDto.ClimbOutGate), JsonSerializer.SerializeToNode(climbOutGate)),
            (nameof(VfrFollowPhaseDto.LeadBase), JsonSerializer.SerializeToNode(leadBase))
        );

        VfrFollowPhaseDto restored = Assert.IsType<VfrFollowPhaseDto>(VfrFollowPhase.FromSnapshot(dto).ToSnapshot());

        Assert.Equal(Bits(stored), Bits(Assert.IsType<FollowClimbOutGateDto>(restored.ClimbOutGate).UpwindHeadingDeg));
        Assert.Equal(Bits(stored), Bits(Assert.IsType<FollowLeadBaseDto>(restored.LeadBase).LegTrackDeg));
    }

    /// <summary>The value a heading made from <see cref="NegativeInputDeg"/> stores, checked to move its last bit when normalised again.</summary>
    private static double StoredFromNegativeInput()
    {
        double stored = new TrueHeading(NegativeInputDeg).Degrees;
        Assert.NotEqual(Bits(stored), Bits(new TrueHeading(stored).Degrees));
        return stored;
    }

    /// <summary>OAK runway 28R from the test navdata.</summary>
    private static RunwayInfo Oak28R()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(runway);
        return runway;
    }

    /// <summary>
    /// <paramref name="snapshot"/> serialized as <typeparamref name="TDto"/> with each of <paramref name="fields"/> set, read
    /// back: the snapshot a phase holding those stored values writes.
    /// </summary>
    private static TDto WithFields<TDto>(PhaseDto snapshot, params (string Name, JsonNode? Value)[] fields)
        where TDto : PhaseDto
    {
        JsonObject json = JsonSerializer.SerializeToNode(Assert.IsType<TDto>(snapshot))!.AsObject();
        foreach ((string name, JsonNode? value) in fields)
        {
            json[name] = value;
        }

        return Assert.IsType<TDto>(json.Deserialize<TDto>());
    }

    [Fact]
    public void FromSnapshot_NaN_IsNaN() => Assert.True(double.IsNaN(TrueHeading.FromSnapshot(double.NaN).Degrees));

    /// <summary>The constructor's remainder of an infinity is NaN, and FromSnapshot gives what the constructor does.</summary>
    [Fact]
    public void FromSnapshot_PositiveInfinity_IsNaN() => Assert.True(double.IsNaN(TrueHeading.FromSnapshot(double.PositiveInfinity).Degrees));

    /// <summary>The constructor's remainder of an infinity is NaN, and FromSnapshot gives what the constructor does.</summary>
    [Fact]
    public void FromSnapshot_NegativeInfinity_IsNaN() => Assert.True(double.IsNaN(TrueHeading.FromSnapshot(double.NegativeInfinity).Degrees));

    [Fact]
    public void FromSnapshot_360_IsZero() => Assert.Equal(Bits(0.0), Bits(TrueHeading.FromSnapshot(360.0).Degrees));

    [Fact]
    public void FromSnapshot_Negative10_Is350() => Assert.Equal(Bits(350.0), Bits(TrueHeading.FromSnapshot(-10.0).Degrees));

    [Fact]
    public void FromSnapshot_725_Is5() => Assert.Equal(Bits(5.0), Bits(TrueHeading.FromSnapshot(725.0).Degrees));

    /// <summary>Negative zero is not a stored heading: it goes through the constructor and comes back as positive zero.</summary>
    [Fact]
    public void FromSnapshot_NegativeZero_IsPositiveZero() => Assert.Equal(Bits(0.0), Bits(TrueHeading.FromSnapshot(-0.0).Degrees));

    [Fact]
    public void FromSnapshot_StoredInRangeValue_IsKeptBitForBit()
    {
        double stored = new TrueHeading(NegativeInputDeg).Degrees;
        Assert.NotEqual(Bits(stored), Bits(new TrueHeading(stored).Degrees));

        Assert.Equal(Bits(stored), Bits(TrueHeading.FromSnapshot(stored).Degrees));
    }
}
