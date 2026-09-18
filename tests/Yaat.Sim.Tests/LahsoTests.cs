using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class LahsoTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a runway heading 280° from threshold at (37.0, -122.0).
    /// The departure end is projected ~1nm along heading 280.
    /// </summary>
    private static RunwayInfo MakeLandingRunway()
    {
        (double Lat, double Lon) end = GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(280), 1.0);
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            thresholdLat: 37.0,
            thresholdLon: -122.0,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 280,
            elevationFt: 6
        );
    }

    /// <summary>
    /// Builds a ground layout with two runways that cross:
    /// - 28R/10L heading 280° (the landing runway)
    /// - 33/15 heading 330° crossing about midfield
    /// </summary>
    private static AirportGroundLayout MakeCrossingLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "KOAK" };

        // Landing runway 28R/10L: ~1nm long, heading 280
        (double Lat, double Lon) rwy28End = GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(280), 1.0);
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10L/28R",
                Coordinates = [(37.0, -122.0), (rwy28End.Lat, rwy28End.Lon)],
                WidthFt = 150,
            }
        );

        // Crossing runway 33/15: crosses the landing runway near midpoint
        // Place it so centerlines actually intersect
        (double Lat, double Lon) midpoint = GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(280), 0.5);
        (double Lat, double Lon) cross33Start = GeoMath.ProjectPoint(midpoint.Lat, midpoint.Lon, new TrueHeading(150), 0.5); // south end
        (double Lat, double Lon) cross33End = GeoMath.ProjectPoint(midpoint.Lat, midpoint.Lon, new TrueHeading(330), 0.5); // north end
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "15/33",
                Coordinates = [(cross33Start.Lat, cross33Start.Lon), (cross33End.Lat, cross33End.Lon)],
                WidthFt = 100,
            }
        );

        return layout;
    }

    /// <summary>
    /// <see cref="MakeCrossingLayout"/> with runway 28R's landing threshold displaced
    /// <paramref name="displacementFt"/> down the pavement.
    /// </summary>
    private static AirportGroundLayout MakeCrossingLayoutWithDisplaced28R(double displacementFt)
    {
        AirportGroundLayout layout = MakeCrossingLayout();
        GroundRunway pavement = layout.Runways[0];
        layout.Runways[0] = new GroundRunway
        {
            Name = pavement.Name,
            Coordinates = pavement.Coordinates,
            WidthFt = pavement.WidthFt,
            ThresholdDisplacementFtByEnd = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["28R"] = displacementFt },
        };
        return layout;
    }

    /// <summary>Issues <c>LAHSO 33</c> against <paramref name="layout"/> and returns the resulting target.</summary>
    private static LahsoTarget ClearLahso(RunwayInfo runway, AirportGroundLayout layout)
    {
        AircraftState ac = MakeAircraft(runway);
        ClearLahsoOn(ac, layout);
        return ac.Phases!.LahsoHoldShort!;
    }

    /// <summary>Issues <c>LAHSO 33</c> to <paramref name="ac"/> and asserts the hold-short target took.</summary>
    private static void ClearLahsoOn(AircraftState ac, AirportGroundLayout layout)
    {
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(
            new LandAndHoldShortCommand("33"),
            ac,
            layout,
            TestDispatch.Context(Random.Shared)
        );
        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Phases!.LahsoHoldShort);
    }

    private static AircraftState MakeAircraft(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "OAK1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -121.98),
            TrueHeading = new TrueHeading(280),
            Altitude = 1000,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new LandingPhase());
        return ac;
    }

    // -------------------------------------------------------------------------
    // Command dispatch
    // -------------------------------------------------------------------------

    [Fact]
    public void Lahso_SetsLahsoTarget_AndClearsToLand()
    {
        RunwayInfo runway = MakeLandingRunway();
        AirportGroundLayout layout = MakeCrossingLayout();
        AircraftState ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Contains("hold short runway 33", result.Message!);
        Assert.NotNull(ac.Phases!.LahsoHoldShort);
        Assert.Equal("33", ac.Phases.LahsoHoldShort.CrossingRunwayId);
        Assert.True(ac.Phases.LahsoHoldShort.DistFromThresholdNm > 0);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
    }

    [Fact]
    public void Lahso_RejectsWhenNoGroundLayout()
    {
        RunwayInfo runway = MakeLandingRunway();
        AircraftState ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, null, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("No ground layout", result.Message!);
    }

    [Fact]
    public void Lahso_RejectsWhenNoAssignedRunway()
    {
        var ac = new AircraftState
        {
            Callsign = "OAK1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(280),
            Altitude = 1000,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            Phases = new PhaseList(),
        };

        AirportGroundLayout layout = MakeCrossingLayout();
        var cmd = new LandAndHoldShortCommand("33");
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("No assigned runway", result.Message!);
    }

    [Fact]
    public void Lahso_RejectsWhenCrossingRunwayNotFound()
    {
        RunwayInfo runway = MakeLandingRunway();
        AirportGroundLayout layout = MakeCrossingLayout();
        AircraftState ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("99");
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("not found in ground layout", result.Message!);
    }

    [Fact]
    public void Lahso_RejectsParallelRunways()
    {
        RunwayInfo runway = MakeLandingRunway();
        AircraftState ac = MakeAircraft(runway);

        // Build layout with a parallel runway (same heading, offset laterally)
        var layout = new AirportGroundLayout { AirportId = "KOAK" };
        (double Lat, double Lon) rwy28End = GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(280), 1.0);
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10L/28R",
                Coordinates = [(37.0, -122.0), (rwy28End.Lat, rwy28End.Lon)],
                WidthFt = 150,
            }
        );

        // Parallel runway offset 0.1nm to the south, same heading
        (double Lat, double Lon) parallelStart = GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(190), 0.1);
        (double Lat, double Lon) parallelEnd = GeoMath.ProjectPoint(parallelStart.Lat, parallelStart.Lon, new TrueHeading(280), 1.0);
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10R/28L",
                Coordinates = [(parallelStart.Lat, parallelStart.Lon), (parallelEnd.Lat, parallelEnd.Lon)],
                WidthFt = 150,
            }
        );

        var cmd = new LandAndHoldShortCommand("28L");
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("does not intersect", result.Message!);
    }

    [Fact]
    public void Lahso_HoldShortDistanceIsReasonable()
    {
        RunwayInfo runway = MakeLandingRunway();
        AirportGroundLayout layout = MakeCrossingLayout();
        AircraftState ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        LahsoTarget target = ac.Phases!.LahsoHoldShort!;

        // Intersection is ~0.5nm from threshold. Hold-short should be slightly less
        // due to setback (half crossing width + 200ft RSA).
        // Crossing runway width = 100ft → setback = 50 + 200 = 250ft ≈ 0.041nm
        Assert.True(target.DistFromThresholdNm > 0.3, $"Hold short dist too small: {target.DistFromThresholdNm:F3}nm");
        Assert.True(target.DistFromThresholdNm < 0.6, $"Hold short dist too large: {target.DistFromThresholdNm:F3}nm");
    }

    /// <summary>
    /// A LAHSO clearance offers available landing distance, which runs from the landing threshold
    /// (7110.65 §3-10-4). Displacing the threshold therefore shortens the reported distance by exactly
    /// the displacement — while the hold-short point itself, set back from a fixed runway intersection,
    /// must not move an inch. That pair is what keeps the distance and the point on the same datum as
    /// <see cref="LandingPhase"/>, which measures the aircraft's progress from the landing threshold.
    /// </summary>
    [Fact]
    public void Lahso_DisplacedThreshold_ShortensTheDistanceButNotTheHoldShortPoint()
    {
        const double displacementFt = 1000.0;
        RunwayInfo runway = MakeLandingRunway();

        LahsoTarget undisplacedTarget = ClearLahso(runway, MakeCrossingLayout());
        LahsoTarget displacedTarget = ClearLahso(runway, MakeCrossingLayoutWithDisplaced28R(displacementFt));

        double distanceLostFt = (undisplacedTarget.DistFromThresholdNm - displacedTarget.DistFromThresholdNm) * GeoMath.FeetPerNm;
        Assert.InRange(distanceLostFt, displacementFt - 2, displacementFt + 2);

        double pointMovedFt =
            GeoMath.DistanceNm(undisplacedTarget.Lat, undisplacedTarget.Lon, displacedTarget.Lat, displacedTarget.Lon) * GeoMath.FeetPerNm;
        Assert.InRange(pointMovedFt, 0, 2);
    }

    [Fact]
    public void Lahso_ReplacesApproachEndingWithLandingPhase()
    {
        RunwayInfo runway = MakeLandingRunway();
        AirportGroundLayout layout = MakeCrossingLayout();
        AircraftState ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        CommandResult result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Contains(ac.Phases!.Phases, p => p is LandingPhase);
    }

    /// <summary>
    /// AIM 4-3-11.b.5: an accepted LAHSO clearance stands "unless an amended clearance is obtained" — and a
    /// go-around is one, since it voids the whole landing-family clearance (7110.65 §7-4-1 handles a broken-off
    /// approach as any go-around). Left standing, the target would re-arm on the next landing this aircraft
    /// flies, which may be on another runway entirely. <c>GA</c> routes here through <c>CommandDispatcher</c>.
    /// </summary>
    [Fact]
    public void GoAround_AfterLahso_ClearsTheHoldShortTarget()
    {
        RunwayInfo runway = MakeLandingRunway();
        AirportGroundLayout layout = MakeCrossingLayout();
        AircraftState ac = MakeAircraft(runway);
        ClearLahsoOn(ac, layout);

        CommandResult result = PatternCommandHandler.TryGoAround(new GoAroundCommand(null, null, null), ac, layout);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases!.LahsoHoldShort);
    }

    /// <summary>
    /// The same amended-clearance rule (AIM 4-3-11.b.5) for the option clearances: <c>TG</c> replaces the
    /// landing terminator with a touch-and-go, so the aircraft is no longer landing and no longer holding short
    /// of anything. Left standing, the target would make the next landing brake for a hold short the controller
    /// cancelled two clearances ago.
    /// </summary>
    [Fact]
    public void TouchAndGo_AfterLahso_ClearsTheHoldShortTarget()
    {
        RunwayInfo runway = MakeLandingRunway();
        AirportGroundLayout layout = MakeCrossingLayout();
        AircraftState ac = MakeAircraft(runway);
        ClearLahsoOn(ac, layout);

        CommandResult result = PatternCommandHandler.TrySetupTouchAndGo(ac, OptionPatternModifier.None, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases!.LahsoHoldShort);
    }

    /// <summary>
    /// The hold-short instruction is part of the landing clearance LAHSO grants (7110.65 §3-10-5.b), so
    /// cancelling that clearance cancels it too — the amended-clearance case of AIM 4-3-11.b.5. Left standing,
    /// a cancelled clearance would still be braking the aircraft to a point it was never cleared to.
    /// </summary>
    [Fact]
    public void CancelLandingClearance_AfterLahso_ClearsTheHoldShortTarget()
    {
        RunwayInfo runway = MakeLandingRunway();
        AirportGroundLayout layout = MakeCrossingLayout();
        AircraftState ac = MakeAircraft(runway);
        ClearLahsoOn(ac, layout);

        CommandResult result = PatternCommandHandler.TryCancelLandingClearance(ac);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases!.LahsoHoldShort);
    }

    // -------------------------------------------------------------------------
    // RunwayIntersectionCalculator
    // -------------------------------------------------------------------------

    [Fact]
    public void FindIntersection_CrossingRunways_ReturnsIntersection()
    {
        AirportGroundLayout layout = MakeCrossingLayout();
        GroundRunway landingRwy = layout.Runways[0]; // 10L/28R
        GroundRunway crossingRwy = layout.Runways[1]; // 15/33

        (double Lat, double Lon, double DistFromStartNm)? result = RunwayIntersectionCalculator.FindIntersection(landingRwy, crossingRwy);

        Assert.NotNull(result);
        Assert.True(result.Value.DistFromStartNm > 0);
        Assert.True(result.Value.DistFromStartNm < 1.0);
    }

    [Fact]
    public void FindIntersection_ParallelRunways_ReturnsNull()
    {
        (double Lat, double Lon) start1 = (Lat: 37.0, Lon: -122.0);
        (double Lat, double Lon) end1 = GeoMath.ProjectPoint(start1.Lat, start1.Lon, new TrueHeading(280), 1.0);
        (double Lat, double Lon) start2 = GeoMath.ProjectPoint(start1.Lat, start1.Lon, new TrueHeading(190), 0.1);
        (double Lat, double Lon) end2 = GeoMath.ProjectPoint(start2.Lat, start2.Lon, new TrueHeading(280), 1.0);

        var rwy1 = new GroundRunway
        {
            Name = "10L/28R",
            Coordinates = [(start1.Lat, start1.Lon), (end1.Lat, end1.Lon)],
            WidthFt = 150,
        };
        var rwy2 = new GroundRunway
        {
            Name = "10R/28L",
            Coordinates = [(start2.Lat, start2.Lon), (end2.Lat, end2.Lon)],
            WidthFt = 150,
        };

        (double Lat, double Lon, double DistFromStartNm)? result = RunwayIntersectionCalculator.FindIntersection(rwy1, rwy2);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeHoldShortDistance_AccountsForSetback()
    {
        AirportGroundLayout layout = MakeCrossingLayout();
        GroundRunway landingRwy = layout.Runways[0];
        GroundRunway crossingRwy = layout.Runways[1];

        (double Lat, double Lon, double DistFromStartNm)? intersection = RunwayIntersectionCalculator.FindIntersection(landingRwy, crossingRwy)!;
        double holdShort = RunwayIntersectionCalculator.ComputeHoldShortDistanceNm(
            intersection.Value.DistFromStartNm,
            "28R",
            landingRwy,
            crossingRwy.WidthFt
        );

        // Setback = (100/2 + 200) = 250ft ≈ 0.041nm
        // Hold-short should be less than the raw intersection distance from threshold
        // but positive (intersection is partway down the runway)
        Assert.True(holdShort > 0);

        // For "28R" approach (second end designator), distance from threshold =
        // totalLen - distFromStart. The hold short should be slightly less.
        double totalLen = GeoMath.DistanceNm(
            landingRwy.Coordinates[0].Lat,
            landingRwy.Coordinates[0].Lon,
            landingRwy.Coordinates[1].Lat,
            landingRwy.Coordinates[1].Lon
        );
        double distFromThreshold = totalLen - intersection.Value.DistFromStartNm;
        Assert.True(holdShort < distFromThreshold, "Hold-short distance should be less than raw intersection distance");
    }

    // -------------------------------------------------------------------------
    // Phase acceptance
    // -------------------------------------------------------------------------

    [Fact]
    public void PatternPhases_AcceptLahsoCommand()
    {
        var phases = new Phase[]
        {
            new DownwindPhase(),
            new BasePhase(),
            new CrosswindPhase(),
            new UpwindPhase(),
            new PatternEntryPhase
            {
                EntryLat = 0,
                EntryLon = 0,
                PatternAltitude = 1000,
                Kind = PatternEntryKind.Direct,
            },
        };

        foreach (Phase phase in phases)
        {
            CommandAcceptance acceptance = phase.CanAcceptCommand(CanonicalCommandType.LandAndHoldShort);
            Assert.Equal(CommandAcceptance.Allowed, acceptance);
        }
    }
}
