using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// The OAK 28R departure fixture the tower and compound tests share: a jet lined up and waiting at the threshold
/// with the takeoff chain (LineUp → Takeoff → InitialClimb) queued behind it, started so LinedUpAndWaitingPhase is
/// current. VFR because the CTO modifiers these tests exercise (MR270) are VFR-only — an IFR aircraft is restricted
/// to bare CTO or a numeric heading vector.
/// </summary>
internal static class LinedUpAircraft
{
    /// <summary>OAK 28R as the fixture places it; the aircraft's <c>Phases.AssignedRunway</c> is this instance.</summary>
    internal static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );

    /// <summary>The fixture aircraft, lined up on <see cref="Oak28R"/>.</summary>
    internal static AircraftState AtOak28R(string callsign) => On(Oak28R(), callsign);

    /// <summary>The fixture aircraft on a caller-supplied runway, for a test that needs the runway instance too.</summary>
    internal static AircraftState On(RunwayInfo runway, string callsign)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Altitude = PlannedAltitude.Vfr(5000),
                FlightRules = "VFR",
            },
        };

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase { Departure = new DefaultDeparture(), CruiseAltitude = 5000 });

        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        return ac;
    }
}
