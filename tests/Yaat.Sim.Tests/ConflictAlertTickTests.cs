using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="SimulationEngine.TickConflictAlerts"/> — the engine-owned terminal Conflict Alert pass that
/// runs the detector, updates the engine's <see cref="SimulationEngine.ConflictAlerts"/> set, and returns
/// the opened/closed pairs for a host to broadcast. The server calls it in place of running the detector
/// and diffing the set itself.
/// </summary>
public class ConflictAlertTickTests
{
    private static AircraftState ModeCAircraft(string callsign, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            Altitude = 4500,
            IndicatedAirspeed = 110,
            Transponder = new AircraftTransponder
            {
                Code = 1200,
                AssignedCode = 1200,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" },
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
        foreach (var ac in aircraft)
        {
            engine.World.AddAircraft(ac);
        }
        return engine;
    }

    [Fact]
    public void TickConflictAlerts_PairInsideThreshold_ReturnsNewThenStaysQuiet()
    {
        var engine = EngineWith(ModeCAircraft("N123AB", new LatLon(37.80, -122.00)), ModeCAircraft("N456CD", new LatLon(37.81, -122.00)));

        var first = engine.TickConflictAlerts();

        var pair = Assert.Single(first.New);
        Assert.Equal("N123AB", pair.CallsignA);
        Assert.Equal("N456CD", pair.CallsignB);
        Assert.Empty(first.Cleared);
        Assert.True(engine.ConflictAlerts.Conflicts.ContainsKey(pair.Id));

        // Detector re-runs, pair unchanged — no new opens, nothing cleared.
        var second = engine.TickConflictAlerts();
        Assert.Empty(second.New);
        Assert.Empty(second.Cleared);
    }

    /// <summary>
    /// The standalone/replay tick must re-evaluate conflicts, not just carry whatever the snapshot restored.
    /// <c>RestoreFromSnapshot</c> repopulates <see cref="SimulationEngine.ConflictAlerts"/>, so in the
    /// <c>Replay → RestoreFromSnapshot → ReplayOneSecond</c> pattern a restored conflict that never gets
    /// re-evaluated is pinned for the rest of the replay — simultaneously stale and frozen.
    /// </summary>
    [Fact]
    public void TickOneSecond_ReEvaluatesConflicts_SoARestoredPairDoesNotStayPinned()
    {
        var a = ModeCAircraft("N123AB", new LatLon(37.80, -122.00));
        var b = ModeCAircraft("N456CD", new LatLon(37.81, -122.00));
        var engine = EngineWith(a, b);

        // Open a conflict — this is the state a snapshot restore hands back.
        var opened = engine.TickConflictAlerts();
        var pair = Assert.Single(opened.New);
        Assert.True(engine.ConflictAlerts.Conflicts.ContainsKey(pair.Id));

        // Separate them well beyond the threshold, then advance the sim the way replay does.
        b.Position = new LatLon(38.50, -122.00);
        engine.TickOneSecond();

        Assert.False(
            engine.ConflictAlerts.Conflicts.ContainsKey(pair.Id),
            "the standalone tick never re-ran detection, so the conflict stayed pinned after the pair separated"
        );
    }

    [Fact]
    public void TickConflictAlerts_PairSeparates_ReturnsCleared()
    {
        var a = ModeCAircraft("N123AB", new LatLon(37.80, -122.00));
        var b = ModeCAircraft("N456CD", new LatLon(37.81, -122.00));
        var engine = EngineWith(a, b);

        var opened = engine.TickConflictAlerts();
        var id = Assert.Single(opened.New).Id;

        // Move well past the 3.3 nm hysteresis-clear threshold.
        b.Position = new LatLon(38.20, -122.00);

        var closed = engine.TickConflictAlerts();
        Assert.Contains(id, closed.Cleared);
        Assert.False(engine.ConflictAlerts.Conflicts.ContainsKey(id));
    }
}
