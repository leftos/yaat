using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Rebuilds a held runway departure at spawn time: a runway start queued lined up (<see cref="AircraftInitializer.InitializeOnRunway"/>)
/// whose field is armed for hold for release spawns instead holding short at its runway's lineup bar, the bar
/// <see cref="GroundCommandHandler.ResolveRunwayHoldShortNode"/> picks, as a departure taxied there would. With no taxi graph or no bar
/// for the runway it stays lined up. Either way it is held for release.
/// </summary>
public static class HeldRunwaySpawnBuilder
{
    private static readonly ILogger Log = SimLog.CreateLogger("HeldRunwaySpawnBuilder");

    /// <summary>
    /// The lineup hold-short bar of <paramref name="state"/>'s assigned runway in its ground layout; null when it has no layout, no
    /// taxi graph, no assigned runway or no bar for that runway.
    /// </summary>
    public static GroundNode? FindDepartureBar(AircraftState state)
    {
        if ((state.Ground.Layout is not { HasTaxiGraph: true } layout) || (state.Phases?.AssignedRunway is not { } runway))
        {
            return null;
        }

        return GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget(runway.Designator, null, false), null);
    }

    /// <summary>
    /// Marks <paramref name="state"/> held for release and, when its runway has a bar, moves it there holding short of the runway:
    /// on the ground, stopped, facing the runway, with the runway assigned as a taxi clearance assigns it. A SAY-only spawn makes no
    /// ready call at the bar: its SAY is its one transmission.
    /// </summary>
    public static void PlaceHeld(AircraftState state)
    {
        state.Ground.HeldForRelease = true;
        state.Ground.ReleasedForDeparture = false;
        if ((FindDepartureBar(state) is not { } bar) || (state.Phases?.AssignedRunway is not { } runway))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("{Callsign}: no hold-short bar for its runway; it spawns lined up, held", state.Callsign);
            }

            return;
        }

        var holding = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = bar.Id,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = runway.Designator,
                Latitude = bar.Position.Lat,
                Longitude = bar.Position.Lon,
            },
            HoldShortArrival.HeldShort
        );
        if (state.Ground.InitialCallup == InitialCallupPlan.RunwaySayOnly)
        {
            holding.SuppressReadyCall();
        }

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(holding);
        state.Phases = phases;
        state.Procedure.DepartureRunway = runway.Designator;
        TrueHeading heading = HeadingOntoRunway(bar.Position, runway);
        state.Position = bar.Position;
        state.TrueHeading = heading;
        state.TrueTrack = heading;
        state.IndicatedAirspeed = 0;
        state.IsOnGround = true;
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("{Callsign}: spawns held at the bar of {Runway}, node {NodeId}", state.Callsign, runway.Designator, bar.Id);
        }
    }

    /// <summary>The heading from the bar square onto the runway centreline, so the aircraft faces the runway it will enter.</summary>
    private static TrueHeading HeadingOntoRunway(LatLon bar, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double along = GeoMath.AlongTrackDistanceNm(bar, threshold, runway.TrueHeading);
        LatLon foot = GeoMath.ProjectPoint(threshold, runway.TrueHeading, along);
        return new TrueHeading(GeoMath.BearingTo(bar, foot));
    }
}
