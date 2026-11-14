using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Runway and final-approach geometry shared by the FOLLOW tests: KOAK runways, a runway's threshold point, a point off
/// the final, physics ticks at the engine's four sub-ticks a second, a KOAK VFR C172 factory and circuit setup.
/// </summary>
internal static class FollowTestHelpers
{
    /// <summary>KOAK's <paramref name="designator"/> runway; throws when navdata does not carry it.</summary>
    internal static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    /// <summary><paramref name="rwy"/>'s threshold point.</summary>
    internal static LatLon Threshold(RunwayInfo rwy) => new(rwy.ThresholdLatitude, rwy.ThresholdLongitude);

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
    internal static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading.ToReciprocal(),
            alongNm
        );
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    /// <summary>Runs the production physics + phase step at the engine's four sub-ticks a second (no scenario loaded).</summary>
    internal static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    /// <summary>
    /// An airborne C172 VFR at KOAK from <paramref name="position"/> on <paramref name="heading"/> at <paramref name="altitude"/> ft, 90 kt.
    /// </summary>
    internal static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        MakeVfr(callsign, position, heading, altitude, 90);

    /// <summary>The same KOAK VFR C172 at <paramref name="ias"/> knots indicated.</summary>
    internal static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude, double ias) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    /// <summary>
    /// Puts <paramref name="ac"/> on <paramref name="rwy"/>'s <paramref name="direction"/> circuit from <paramref name="leg"/>,
    /// started.
    /// </summary>
    internal static void PutOnCircuit(AircraftState ac, RunwayInfo rwy, PatternDirection direction, PatternEntryLeg leg)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = rwy,
                Category = AircraftCategorization.Categorize(ac.AircraftType),
                AircraftType = ac.AircraftType,
                WindSpeedKt = 0,
                Direction = direction,
                EntryLeg = leg,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = NavigationDatabase.Instance.GetRunways(rwy.AirportId),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = direction };
        foreach (Phase phase in circuit)
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
    }

    /// <summary>Puts <paramref name="ac"/> on <paramref name="rwy"/>'s right-traffic circuit from <paramref name="leg"/>, started.</summary>
    internal static void PutOnCircuit(AircraftState ac, RunwayInfo rwy, PatternEntryLeg leg) => PutOnCircuit(ac, rwy, PatternDirection.Right, leg);
}
