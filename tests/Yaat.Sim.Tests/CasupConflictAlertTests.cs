using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// <c>CASUP</c> toggles the suppression of the pair's active STARS conflict alert: a suppressed alert is reported to the
/// host by the next <see cref="SimulationEngine.TickConflictAlerts"/> pass, a second <c>CASUP</c> restores it, the
/// suppression ends with the alert, and ERAM STCA is not affected.
/// </summary>
public class CasupConflictAlertTests
{
    private static readonly LatLon A = new(37.80, -122.00);
    private static readonly LatLon B = new(37.81, -122.00);
    private static readonly LatLon Far = new(38.50, -122.00);

    public CasupConflictAlertTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Ifr(string callsign, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            Altitude = 4_500,
            IndicatedAirspeed = 250,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Transponder = new AircraftTransponder
            {
                Code = 4521,
                AssignedCode = 4521,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = true, FlightRules = "IFR" },
            Track = new AircraftTrack(),
        };

    private static SimulationEngine EngineWith(params AircraftState[] aircraft)
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
        foreach (AircraftState ac in aircraft)
        {
            engine.World.AddAircraft(ac);
        }
        return engine;
    }

    private static CommandResult Casup(SimulationEngine engine, AircraftState ac, string other) =>
        TrackEngine.Dispatch(
            new SuppressConflictAlertCommand(other),
            ac,
            new TrackDispatchContext(Identity: null, engine.Scenario!, Redirect: null, engine.ConflictAlerts)
        )!;

    [Fact]
    public void Casup_OnActiveAlert_AcceptsAndWithholdsTheAlert()
    {
        AircraftState a = Ifr("AAL123", A);
        SimulationEngine engine = EngineWith(a, Ifr("UAL456", B));
        string id = Assert.Single(engine.TickConflictAlerts().New).Id;

        CommandResult result = Casup(engine, a, "UAL456");
        ConflictAlertChanges changes = engine.TickConflictAlerts();

        Assert.True(result.Success, result.Message);
        Assert.True(engine.ConflictAlerts.FindPair("UAL456", "AAL123")!.Suppressed);
        // Suppressing an alert acknowledges it (7110.65 §5-14-6c.3).
        Assert.True(engine.ConflictAlerts.FindPair("UAL456", "AAL123")!.IsAcknowledged);
        Assert.Equal([id], changes.Suppressed);
        Assert.Empty(changes.Restored);
        Assert.Empty(changes.New);
        Assert.Empty(changes.Cleared);
    }

    [Fact]
    public void Casup_Twice_RestoresTheAlert()
    {
        AircraftState a = Ifr("AAL123", A);
        AircraftState b = Ifr("UAL456", B);
        SimulationEngine engine = EngineWith(a, b);
        string id = Assert.Single(engine.TickConflictAlerts().New).Id;
        Assert.True(Casup(engine, a, "UAL456").Success);
        engine.TickConflictAlerts();

        CommandResult result = Casup(engine, b, "AAL123");
        ConflictAlertChanges changes = engine.TickConflictAlerts();

        Assert.True(result.Success, result.Message);
        Assert.False(engine.ConflictAlerts.Conflicts[id].Suppressed);
        Assert.Equal([id], changes.Restored.Select(c => c.Id));
        Assert.True(Assert.Single(changes.Restored).IsAcknowledged);
        Assert.Empty(changes.Suppressed);
    }

    [Fact]
    public void Casup_NoActiveAlert_IsRefused()
    {
        AircraftState a = Ifr("AAL123", A);
        SimulationEngine engine = EngineWith(a, Ifr("UAL456", Far));
        Assert.Empty(engine.TickConflictAlerts().New);

        CommandResult result = Casup(engine, a, "UAL456");

        Assert.False(result.Success);
        Assert.Equal("No active STARS conflict alert between AAL123 and UAL456", result.Message);
        Assert.Empty(engine.ConflictAlerts.Conflicts);
    }

    [Fact]
    public void Casup_AfterTheAlertEnds_ANewConflictAlertsAgain()
    {
        AircraftState a = Ifr("AAL123", A);
        AircraftState b = Ifr("UAL456", B);
        SimulationEngine engine = EngineWith(a, b);
        string id = Assert.Single(engine.TickConflictAlerts().New).Id;
        Assert.True(Casup(engine, a, "UAL456").Success);
        engine.TickConflictAlerts();

        b.Position = Far;
        Assert.Equal([id], engine.TickConflictAlerts().Cleared);
        b.Position = B;
        ConflictAlertChanges again = engine.TickConflictAlerts();

        ActiveConflict alert = Assert.Single(again.New);
        Assert.Equal(id, alert.Id);
        Assert.False(alert.Suppressed);
        Assert.Empty(again.Suppressed);
    }

    /// <summary>
    /// A pair that diverges for one pass while still well inside the 3.3 nm / 1,100 ft box (a visual follow turning final)
    /// clears the alert, but the encounter has not ended: when the pair closes again the suppressed alert comes back
    /// suppressed, not as a fresh unsuppressed alert.
    /// </summary>
    [Fact]
    public void Casup_DivergingInsideTheBox_KeepsTheSuppression()
    {
        AircraftState a = Ifr("AAL123", A);
        AircraftState b = Ifr("UAL456", B);
        SimulationEngine engine = EngineWith(a, b);
        string id = Assert.Single(engine.TickConflictAlerts().New).Id;
        Assert.True(Casup(engine, a, "UAL456").Success);
        engine.TickConflictAlerts();

        a.TrueTrack = new TrueHeading(180);
        Assert.Equal([id], engine.TickConflictAlerts().Cleared);
        a.TrueTrack = new TrueHeading(0);
        ConflictAlertChanges again = engine.TickConflictAlerts();

        Assert.Empty(again.New);
        Assert.Empty(again.Suppressed);
        Assert.True(engine.ConflictAlerts.Conflicts[id].Suppressed);
    }

    [Fact]
    public void Casup_Latch_SurvivesASnapshot()
    {
        AircraftState a = Ifr("AAL123", A);
        SimulationEngine engine = EngineWith(a, Ifr("UAL456", B));
        string id = Assert.Single(engine.TickConflictAlerts().New).Id;
        Assert.True(Casup(engine, a, "UAL456").Success);
        engine.TickConflictAlerts();
        a.TrueTrack = new TrueHeading(180);
        Assert.Equal([id], engine.TickConflictAlerts().Cleared);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        SimulationEngine restored = EngineWith();
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(new LatchedConflictSuppression("AAL123", "UAL456"), restored.ConflictAlerts.LatchedSuppressions[id]);
    }

    [Fact]
    public void Casup_DoesNotSilenceTheEramStca()
    {
        // ERAM STCA protects a controlled aircraft, so one side is tracked by an ERAM sector.
        AircraftState a = Ifr("AAL123", A);
        a.Track.Owner = TrackOwner.CreateEram("AAL123", "ZOA", "44");
        SimulationEngine engine = EngineWith(a, Ifr("UAL456", B));
        Assert.Single(engine.TickConflictAlerts().New);

        Assert.True(Casup(engine, a, "UAL456").Success);
        engine.TickConflictAlerts();
        EramConflictAlertChanges eram = engine.TickEramConflictAlerts();

        EramActiveConflict alert = Assert.Single(eram.New);
        Assert.False(alert.Suppressed);
        Assert.NotNull(engine.EramConflicts.FindPair("AAL123", "UAL456"));
    }
}
