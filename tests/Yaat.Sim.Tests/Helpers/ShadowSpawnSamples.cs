using Xunit;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Recorded spawns for the tests of a pre-55 tape's filed-altitude seeding.</summary>
internal static class ShadowSpawnSamples
{
    /// <summary>
    /// A shadow's first recorded sample, its spawn state filed at <paramref name="planAltitudeFt"/> (no plan altitude when
    /// null) with no pilot altitude, as a tape written before snapshot schema 55 holds it.
    /// </summary>
    public static RecordedLiveTrafficSample ShadowSpawnSample(int? planAltitudeFt = 23000)
    {
        var liveSample = new LiveTrafficSample(0, 37.9, -121.3, 11_000, 300, 90, 0, LiveTrafficSource.Stars, 4521);
        AircraftSnapshotDto spawnState = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "IFR",
                Departure = "KSFO",
                Destination = "KSMF",
                Altitude = planAltitudeFt is { } feet ? PlannedAltitude.Ifr(feet) : PlannedAltitude.None,
            },
        }.ToSnapshot();
        Assert.Null(spawnState.FiledCruiseAltitudeFt);
        return new RecordedLiveTrafficSample(1, "UAL123", liveSample, spawnState);
    }

    /// <summary>
    /// A generated aircraft's recorded spawn, its aircraft filed at <paramref name="planAltitudeFt"/> with no pilot altitude,
    /// as a tape written before snapshot schema 55 holds it.
    /// </summary>
    public static RecordedAircraftSpawn GeneratedSpawn(int planAltitudeFt)
    {
        AircraftSnapshotDto spawn = new AircraftState
        {
            Callsign = "N789GH",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "IFR",
                Departure = "KOAK",
                Destination = "KSAC",
                Altitude = PlannedAltitude.Ifr(planAltitudeFt),
            },
        }.ToSnapshot();
        Assert.Null(spawn.FiledCruiseAltitudeFt);
        return new RecordedAircraftSpawn(1, spawn);
    }
}
