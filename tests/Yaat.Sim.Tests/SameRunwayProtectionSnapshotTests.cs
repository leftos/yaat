using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Snapshot schema 25 exists for two fields: <see cref="ControlTargets.SpeedCommandIsControllerIssued"/> (who owns an
/// aircraft's speed — a human controller, or a scenario preset / AI position) and the same-runway arrival protection's
/// ceiling ownership (<see cref="AircraftApproachState.SameRunwayProtectionCeilingKts"/> plus the ceiling it displaced).
/// Both drive behaviour that must survive a rewind, a restore and a replay: a lost provenance flag hands a controller's
/// speed back to the simulated TRACON, and a lost displaced ceiling makes the protection's release delete a published
/// crossing-speed restriction instead of restoring it.
/// </summary>
public class SameRunwayProtectionSnapshotTests
{
    private static AircraftState Arrival() =>
        new()
        {
            Callsign = "SKW5536",
            AircraftType = "CRJ2",
            IsOnGround = false,
            Altitude = 6000,
            IndicatedAirspeed = 250,
        };

    [Fact]
    public void SpeedCommandProvenance_SurvivesSnapshotRoundTrip()
    {
        var aircraft = Arrival();
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.SpeedCommandIsControllerIssued = true;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        Assert.True(restored.Targets.HasExplicitSpeedCommand);
        Assert.True(restored.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ScriptedSpeedProvenance_SurvivesSnapshotRoundTrip()
    {
        var aircraft = Arrival();
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.SpeedCommandIsControllerIssued = false;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        // The pair must not collapse into one flag on the way through: a scripted AT-fix speed carries the explicit
        // flag without the provenance, which is exactly what lets the protection keep managing that aircraft.
        Assert.True(restored.Targets.HasExplicitSpeedCommand);
        Assert.False(restored.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ProtectionCeilingAndDisplacedCeiling_SurviveSnapshotRoundTrip()
    {
        var aircraft = Arrival();
        aircraft.Targets.SpeedCeiling = 163.5;
        aircraft.Approach.SameRunwayProtectionCeilingKts = 163.5;
        aircraft.Approach.SameRunwayProtectionDisplacedCeilingKts = 210.0;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        Assert.Equal(163.5, restored.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(210.0, restored.Approach.SameRunwayProtectionDisplacedCeilingKts);
        Assert.Equal(163.5, restored.Targets.SpeedCeiling);
    }

    [Fact]
    public void ProtectionEngagedOverNoCeiling_RestoresWithNoDisplacedValue()
    {
        var aircraft = Arrival();
        aircraft.Targets.SpeedCeiling = 170.0;
        aircraft.Approach.SameRunwayProtectionCeilingKts = 170.0;
        aircraft.Approach.SameRunwayProtectionDisplacedCeilingKts = null;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        // Engaged with nothing underneath: the null must round-trip as a null, because the release reads it as "there
        // was no ceiling before" and clears the field rather than restoring a value that never existed.
        Assert.Equal(170.0, restored.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(restored.Approach.SameRunwayProtectionDisplacedCeilingKts);
    }

    [Fact]
    public void ProtectionDisengaged_RestoresAsNotEngaged()
    {
        var restored = AircraftState.FromSnapshot(Arrival().ToSnapshot(), groundLayout: null);

        Assert.Null(restored.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(restored.Approach.SameRunwayProtectionDisplacedCeilingKts);
        Assert.False(restored.Targets.SpeedCommandIsControllerIssued);
    }
}
