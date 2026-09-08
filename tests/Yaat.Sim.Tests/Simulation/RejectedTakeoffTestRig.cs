using Microsoft.Extensions.Logging.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Shared fixtures for the rejected-takeoff suites: one runway, the departure/occupant builders and
/// the tick loop that integrates ground displacement (position integration is FlightPhysics' job,
/// absent from a bare PhaseRunner test). Used by both the #410 (occupant on the pavement) and #416
/// (preceding departure) tests so the two agree on the geometry they argue about.
/// </summary>
internal static class RejectedTakeoffTestRig
{
    internal const double PavementLengthNm = 2.0;

    internal static RunwayInfo Runway28R()
    {
        var end = GeoMath.ProjectPoint(37.72, -122.22, new TrueHeading(270), PavementLengthNm);
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 270,
            elevationFt: 9
        );
    }

    internal static AircraftState MakeRollingDeparture(RunwayInfo runway, double iasKts)
    {
        var ac = new AircraftState
        {
            Callsign = "DEP1",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = iasKts,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Ifr(5000) },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(new TakeoffPhase());
        return ac;
    }

    internal static AircraftState MakeLuawOccupant(RunwayInfo runway, double downfieldFt)
    {
        var occ = MakeOnRunwayAircraft("OCC1", runway, downfieldFt, runway.TrueHeading, iasKts: 0);
        occ.Phases = new PhaseList { AssignedRunway = runway };
        occ.Phases.Add(new LinedUpAndWaitingPhase());
        occ.Phases.Start(CommandDispatcher.BuildMinimalContext(occ));
        return occ;
    }

    /// <summary>
    /// A preceding departure <paramref name="downfieldFt"/> ahead on the same runway, rolling at
    /// <paramref name="groundSpeedKts"/>: on its takeoff roll, or — <paramref name="rejecting"/> —
    /// braking from that speed in <see cref="RejectedTakeoffPhase"/>.
    /// </summary>
    internal static AircraftState MakeRollingLeader(RunwayInfo runway, double downfieldFt, double groundSpeedKts, bool rejecting)
    {
        var lead = MakeOnRunwayAircraft("LEAD1", runway, downfieldFt, runway.TrueHeading, groundSpeedKts);
        lead.Phases = new PhaseList { AssignedRunway = runway };
        lead.Phases.Add(rejecting ? new RejectedTakeoffPhase() : new TakeoffPhase());
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead));
        return lead;
    }

    /// <summary>
    /// A preceding departure that has already lifted off, still over the pavement
    /// <paramref name="downfieldFt"/> ahead at <paramref name="aglFt"/>. Its phase is still
    /// <see cref="TakeoffPhase"/> (rotation to the 400 ft AGL completion), which is what makes it a
    /// <see cref="RunwayUseKind.Departing"/> occupant.
    /// </summary>
    internal static AircraftState MakeAirborneLeader(RunwayInfo runway, double downfieldFt, double iasKts, double aglFt)
    {
        var lead = MakeRollingLeader(runway, downfieldFt, iasKts, rejecting: false);
        lead.IsOnGround = false;
        lead.Altitude = runway.ElevationFt + aglFt;
        lead.VerticalSpeed = 2000;
        return lead;
    }

    /// <summary>
    /// A departure rolling from the opposite end of the same pavement toward the trailer —
    /// <see cref="RunwayOccupancy.Classify"/> reads the axis modulo 180, so it is
    /// <see cref="RunwayUseKind.Departing"/> on the trailer's runway too.
    /// </summary>
    internal static AircraftState MakeOpposingLeader(RunwayInfo runway, double downfieldFt, double groundSpeedKts)
    {
        string oppositeEnd = runway.IsActiveEnd(runway.Id.End1) ? runway.Id.End2 : runway.Id.End1;
        var opposing = runway.ForApproach(oppositeEnd);
        var lead = MakeOnRunwayAircraft("LEAD1", runway, downfieldFt, opposing.TrueHeading, groundSpeedKts);
        lead.Phases = new PhaseList { AssignedRunway = opposing };
        lead.Phases.Add(new TakeoffPhase());
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead));
        return lead;
    }

    internal static PhaseContext Ctx(AircraftState departure, RunwayInfo runway, AircraftState occupant, bool autoReject)
    {
        return new PhaseContext
        {
            Aircraft = departure,
            Targets = departure.Targets,
            Category = AircraftCategorization.Categorize(departure.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            ListAircraft = () => [departure, occupant],
            AutoRejectTakeoffOnOccupiedRunway = autoReject,
        };
    }

    /// <summary>
    /// Ticks the departure's phase list for up to <paramref name="seconds"/>, integrating ground
    /// displacement manually (position integration is FlightPhysics' job, absent here); stops
    /// early once airborne.
    /// </summary>
    internal static (bool WentAirborne, double MinSeparationFt) RunRoll(
        AircraftState departure,
        PhaseContext ctx,
        AircraftState occupant,
        int seconds
    )
    {
        bool airborne = false;
        double minSepFt = double.MaxValue;
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(departure, ctx);
            IntegrateGroundDisplacement(departure);
            minSepFt = Math.Min(minSepFt, GeoMath.DistanceNm(departure.Position, occupant.Position) * GeoMath.FeetPerNm);
            if (!departure.IsOnGround)
            {
                airborne = true;
                break;
            }
        }

        return (airborne, minSepFt);
    }

    internal static void IntegrateGroundDisplacement(AircraftState departure)
    {
        if (!departure.IsOnGround || (departure.GroundSpeed <= 0))
        {
            return;
        }

        var moved = GeoMath.ProjectPoint(departure.Position.Lat, departure.Position.Lon, departure.TrueHeading, departure.GroundSpeed / 3600.0);
        departure.Position = new LatLon(moved.Lat, moved.Lon);
    }

    private static AircraftState MakeOnRunwayAircraft(string callsign, RunwayInfo runway, double downfieldFt, TrueHeading heading, double iasKts)
    {
        var pos = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading, downfieldFt / GeoMath.FeetPerNm);
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(pos.Lat, pos.Lon),
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = iasKts,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Ifr(5000) },
        };
    }
}
