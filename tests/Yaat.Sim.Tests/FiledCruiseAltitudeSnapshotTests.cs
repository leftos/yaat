using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="AircraftState.FiledCruiseAltitudeFt"/> is the altitude the pilot filed when the aircraft was created, the
/// climb a departure with no assigned altitude flies to. It must survive a rewind, a restore and a replay, and a pre-V55
/// snapshot, which carries none, is seeded from the plan's altitude (null without one, never 0) so the restored departure
/// climbs as it would have.
/// </summary>
public class FiledCruiseAltitudeSnapshotTests
{
    private static AircraftState Departure(PlannedAltitude altitude) =>
        new()
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "IFR",
                Departure = "KOAK",
                Destination = "KLAX",
                Altitude = altitude,
            },
        };

    private static StateSnapshotDto LegacySnapshot(AircraftSnapshotDto aircraft) =>
        new()
        {
            SchemaVersion = 54,
            ElapsedSeconds = 0,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [aircraft],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "filed-cruise-altitude",
                ScenarioName = "filed cruise altitude migration",
                RngSeed = 42,
                ElapsedSeconds = 0,
                AutoClearedToLand = false,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
                ValidateDctFixes = false,
                IsPaused = false,
                SimRate = 1.0,
                AutoAcceptDelaySeconds = 0,
                IsStudentTowerPosition = false,
            },
        };

    [Fact]
    public void FiledCruiseAltitude_SurvivesSnapshotRoundTrip_AndKeepsItsValueOverAnAmendedPlan()
    {
        AircraftState aircraft = Departure(PlannedAltitude.Ifr(35000));
        aircraft.ClearToFiledAltitude();
        aircraft.FlightPlan.Altitude = PlannedAltitude.Ifr(17000);

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        Assert.Equal(35000, restored.FiledCruiseAltitudeFt);
        Assert.Equal(17000, restored.FlightPlan.Altitude.CruiseFeet);
    }

    [Fact]
    public void PreV55Snapshot_WithPlanAltitude_MigratesToThePlanAltitude()
    {
        AircraftSnapshotDto dto = Departure(PlannedAltitude.Ifr(35000)).ToSnapshot();
        dto.FiledCruiseAltitudeFt = null;
        StateSnapshotDto snapshot = LegacySnapshot(dto);

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(35000, snapshot.Aircraft[0].FiledCruiseAltitudeFt);
        Assert.Equal(35000, AircraftState.FromSnapshot(snapshot.Aircraft[0], groundLayout: null).FiledCruiseAltitudeFt);
    }

    [Fact]
    public void PreV55Snapshot_WithoutPlanAltitude_MigratesToNone_NotZero()
    {
        AircraftSnapshotDto dto = Departure(PlannedAltitude.None).ToSnapshot();
        dto.FiledCruiseAltitudeFt = null;
        StateSnapshotDto snapshot = LegacySnapshot(dto);

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Null(snapshot.Aircraft[0].FiledCruiseAltitudeFt);
    }
}
