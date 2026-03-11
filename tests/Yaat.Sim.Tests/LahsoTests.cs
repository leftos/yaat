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
        var end = GeoMath.ProjectPoint(37.0, -122.0, 280, 1.0);
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
        var rwy28End = GeoMath.ProjectPoint(37.0, -122.0, 280, 1.0);
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
        var midpoint = GeoMath.ProjectPoint(37.0, -122.0, 280, 0.5);
        var cross33Start = GeoMath.ProjectPoint(midpoint.Lat, midpoint.Lon, 150, 0.5); // south end
        var cross33End = GeoMath.ProjectPoint(midpoint.Lat, midpoint.Lon, 330, 0.5); // north end
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

    private static AircraftState MakeAircraft(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "OAK1",
            AircraftType = "B738",
            Latitude = 37.0,
            Longitude = -121.98,
            Heading = 280,
            Altitude = 1000,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            Destination = "KOAK",
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
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
        var runway = MakeLandingRunway();
        var layout = MakeCrossingLayout();
        var ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        var result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout);

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
        var runway = MakeLandingRunway();
        var ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        var result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, null);

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
            Latitude = 37.0,
            Longitude = -122.0,
            Heading = 280,
            Altitude = 1000,
            IndicatedAirspeed = 140,
            IsOnGround = false,
        };
        ac.Phases = new PhaseList();

        var layout = MakeCrossingLayout();
        var cmd = new LandAndHoldShortCommand("33");
        var result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout);

        Assert.False(result.Success);
        Assert.Contains("No assigned runway", result.Message!);
    }

    [Fact]
    public void Lahso_RejectsWhenCrossingRunwayNotFound()
    {
        var runway = MakeLandingRunway();
        var layout = MakeCrossingLayout();
        var ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("99");
        var result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout);

        Assert.False(result.Success);
        Assert.Contains("not found in ground layout", result.Message!);
    }

    [Fact]
    public void Lahso_RejectsParallelRunways()
    {
        var runway = MakeLandingRunway();
        var ac = MakeAircraft(runway);

        // Build layout with a parallel runway (same heading, offset laterally)
        var layout = new AirportGroundLayout { AirportId = "KOAK" };
        var rwy28End = GeoMath.ProjectPoint(37.0, -122.0, 280, 1.0);
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10L/28R",
                Coordinates = [(37.0, -122.0), (rwy28End.Lat, rwy28End.Lon)],
                WidthFt = 150,
            }
        );

        // Parallel runway offset 0.1nm to the south, same heading
        var parallelStart = GeoMath.ProjectPoint(37.0, -122.0, 190, 0.1);
        var parallelEnd = GeoMath.ProjectPoint(parallelStart.Lat, parallelStart.Lon, 280, 1.0);
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "10R/28L",
                Coordinates = [(parallelStart.Lat, parallelStart.Lon), (parallelEnd.Lat, parallelEnd.Lon)],
                WidthFt = 150,
            }
        );

        var cmd = new LandAndHoldShortCommand("28L");
        var result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout);

        Assert.False(result.Success);
        Assert.Contains("does not intersect", result.Message!);
    }

    [Fact]
    public void Lahso_HoldShortDistanceIsReasonable()
    {
        var runway = MakeLandingRunway();
        var layout = MakeCrossingLayout();
        var ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        var result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout);

        Assert.True(result.Success);
        var target = ac.Phases!.LahsoHoldShort!;

        // Intersection is ~0.5nm from threshold. Hold-short should be slightly less
        // due to setback (half crossing width + 200ft RSA).
        // Crossing runway width = 100ft → setback = 50 + 200 = 250ft ≈ 0.041nm
        Assert.True(target.DistFromThresholdNm > 0.3, $"Hold short dist too small: {target.DistFromThresholdNm:F3}nm");
        Assert.True(target.DistFromThresholdNm < 0.6, $"Hold short dist too large: {target.DistFromThresholdNm:F3}nm");
    }

    [Fact]
    public void Lahso_ReplacesApproachEndingWithLandingPhase()
    {
        var runway = MakeLandingRunway();
        var layout = MakeCrossingLayout();
        var ac = MakeAircraft(runway);

        var cmd = new LandAndHoldShortCommand("33");
        var result = PatternCommandHandler.TryLandAndHoldShort(cmd, ac, layout);

        Assert.True(result.Success);
        Assert.Contains(ac.Phases!.Phases, p => p is LandingPhase);
    }

    // -------------------------------------------------------------------------
    // RunwayIntersectionCalculator
    // -------------------------------------------------------------------------

    [Fact]
    public void FindIntersection_CrossingRunways_ReturnsIntersection()
    {
        var layout = MakeCrossingLayout();
        var landingRwy = layout.Runways[0]; // 10L/28R
        var crossingRwy = layout.Runways[1]; // 15/33

        var result = RunwayIntersectionCalculator.FindIntersection(landingRwy, crossingRwy);

        Assert.NotNull(result);
        Assert.True(result.Value.DistFromStartNm > 0);
        Assert.True(result.Value.DistFromStartNm < 1.0);
    }

    [Fact]
    public void FindIntersection_ParallelRunways_ReturnsNull()
    {
        var start1 = (Lat: 37.0, Lon: -122.0);
        var end1 = GeoMath.ProjectPoint(start1.Lat, start1.Lon, 280, 1.0);
        var start2 = GeoMath.ProjectPoint(start1.Lat, start1.Lon, 190, 0.1);
        var end2 = GeoMath.ProjectPoint(start2.Lat, start2.Lon, 280, 1.0);

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

        var result = RunwayIntersectionCalculator.FindIntersection(rwy1, rwy2);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeHoldShortDistance_AccountsForSetback()
    {
        var layout = MakeCrossingLayout();
        var landingRwy = layout.Runways[0];
        var crossingRwy = layout.Runways[1];

        var intersection = RunwayIntersectionCalculator.FindIntersection(landingRwy, crossingRwy)!;
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
            },
        };

        foreach (var phase in phases)
        {
            var acceptance = phase.CanAcceptCommand(CanonicalCommandType.LandAndHoldShort);
            Assert.Equal(CommandAcceptance.Allowed, acceptance);
        }
    }
}
