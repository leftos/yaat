using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Intersection departures already work geometrically: TakeoffPhase only uses
/// the runway threshold as a centerline cross-track reference, not as the
/// takeoff origin. The aircraft accelerates from wherever it is when the
/// phase starts — which is the intersection node after taxi + line-up.
/// </summary>
public class TakeoffPhaseIntersectionDepartureTests
{
    private static RunwayInfo Runway28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.72,
            endLon: -122.25, // ~1.5 NM long
            heading: 270,
            elevationFt: 9
        );

    [Fact]
    public void Takeoff_FromMidRunwayPosition_DoesNotTeleportToThreshold()
    {
        // Aircraft sits at an "intersection" 0.5 NM past the threshold along
        // centerline — the position it would reach after taxiing to a runway
        // intersection and lining up there. TakeoffPhase must accelerate from
        // this position, not snap back to the threshold.
        var runway = Runway28R();
        var startPos = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading, 0.5);
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(startPos.Lat, startPos.Lon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Ifr(5000) },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        var takeoff = new TakeoffPhase();
        ac.Phases.Add(takeoff);

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.1,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
        };

        ac.Phases.Start(ctx);
        takeoff.OnTick(ctx);

        double distFromThresholdNm = GeoMath.DistanceNm(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), ac.Position);
        Assert.InRange(distFromThresholdNm, 0.49, 0.55);
    }

    [Fact]
    public void Takeoff_FromMidRunwayPosition_AcceleratesAndLiftsOff()
    {
        // Sanity that intersection departure follows the same accelerate-to-Vr
        // model as a full-length takeoff. Aircraft should reach Vr and become
        // airborne — the intersection just shortens the available distance,
        // which YAAT does not model as a hard constraint (no "runway too short"
        // check); the takeoff still completes.
        var runway = Runway28R();
        var startPos = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading, 0.5);
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(startPos.Lat, startPos.Lon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Ifr(5000) },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        var takeoff = new TakeoffPhase();
        ac.Phases.Add(takeoff);

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
        };

        ac.Phases.Start(ctx);

        bool wentAirborne = false;
        for (int i = 0; i < 90 && !wentAirborne; i++)
        {
            takeoff.OnTick(ctx);
            if (!ac.IsOnGround)
            {
                wentAirborne = true;
            }
        }

        Assert.True(wentAirborne, $"Aircraft should liftoff within 90s, IAS={ac.IndicatedAirspeed:F0}");
    }
}
