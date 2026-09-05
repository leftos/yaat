using System.Text.Json;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Pins the spine through its own instrument. The snapshot oracle (yaat-server <c>TickOracleTests</c>) sees state,
/// not order — every host iterates the same lists, so the one thing that can observe <em>which steps ran, in what
/// order, how many times</em> is the <see cref="StepTrace"/>. Three things are asserted here: the literal step
/// sequence of one bare second (the only pin on <c>TickPilotProactive</c> sitting after the detectors), that the
/// sub-tick replay step composes the same second as the whole-second step, and the per-second counts.
/// </summary>
public class SpineTraceTests
{
    /// <summary>The spine, written out. A reordering anywhere in <see cref="SpineOrder"/> fails this.</summary>
    private static readonly TracedStep[] OneBareSecond =
    [
        new(StepId.PreTickRecordedActions, 0),
        new(StepId.TickPrePhysics, 0),
        new(StepId.TerminalEntries, 0),
        new(StepId.DelayedHandoffs, 0),
        new(StepId.LiveTrafficSync, 0),
        new(StepId.Physics, 0),
        new(StepId.Physics, 1),
        new(StepId.Physics, 2),
        new(StepId.Physics, 3),
        new(StepId.LiveTrafficRunwayUse, 0),
        new(StepId.Transponders, 0),
        new(StepId.AutoAccept, 0),
        new(StepId.PointoutAutoAck, 0),
        new(StepId.FlightPlanCreatorAutoTrack, 0),
        new(StepId.DeferredAutoTrack, 0),
        new(StepId.CoordinationTimers, 0),
        new(StepId.TowerLists, 0),
        new(StepId.VisualDetection, 0),
        new(StepId.ConflictAlerts, 0),
        new(StepId.EramConflictAlerts, 0),
        new(StepId.AsdexAlerts, 0),
        new(StepId.SoloTrainingEvaluation, 0),
        new(StepId.PilotProactive, 0),
        new(StepId.Warnings, 0),
        new(StepId.Notifications, 0),
        new(StepId.PilotSpeech, 0),
        new(StepId.PilotReadbacks, 0),
        new(StepId.PilotTransmissions, 0),
        new(StepId.ApproachScores, 0),
        new(StepId.AutoArrivalStrips, 0),
        new(StepId.AutoApproachDepartureStrips, 0),
        new(StepId.AutoTdlsQueue, 0),
        new(StepId.TdlsAutoWilco, 0),
        new(StepId.TdlsExpiry, 0),
        new(StepId.TdlsTrackRemoval, 0),
        new(StepId.StripDispatches, 0),
        new(StepId.AutoDelete, 0),
        new(StepId.SurfaceCoastExpiry, 0),
        new(StepId.RundownBroadcast, 0),
        new(StepId.LiveTrafficStatusBroadcast, 0),
        new(StepId.TimersBroadcast, 0),
        new(StepId.PositionHistory, 0),
        new(StepId.WeatherAdvance, 0),
        new(StepId.MetarIssuance, 0),
        new(StepId.RecordedActions, 0),
        new(StepId.ControllerAi, 0),
    ];

    [Fact]
    public void TickOneSecond_RunsExactlyTheSpine_InOrder()
    {
        var engine = BuildEngine();

        engine.TickOneSecond();

        Assert.Equal(1, engine.StepTrace.LastSecond);
        Assert.Equal(OneBareSecond, engine.StepTrace.LastSequence);
    }

    [Fact]
    public void EveryStepId_AppearsInTheSpine()
    {
        var traced = OneBareSecond.Select(s => s.Id).ToHashSet();
        foreach (var id in Enum.GetValues<StepId>())
        {
            Assert.Contains(id, traced);
        }
    }

    [Fact]
    public void Counts_AreOnePerStepAndFourPhysicsSubTicks()
    {
        var engine = BuildEngine();

        engine.TickOneSecond();
        engine.TickOneSecond();

        Assert.Equal(2, engine.StepTrace.LastSecond);
        Assert.Equal(engine.Scenario!.ElapsedSeconds, engine.StepTrace.LastSecond);
        foreach (var id in Enum.GetValues<StepId>())
        {
            int expected = id == StepId.Physics ? SimulationEngine.PhysicsSubTickRate : 1;
            Assert.Equal(expected, engine.StepTrace.CountInLastSecond(id));
            Assert.Equal(2 * expected, engine.StepTrace.TotalCount(id));
        }
    }

    /// <summary>
    /// Four sub-tick steps from an integer second must compose the same second as one whole-second step: same
    /// trace digest, same captured state. The sub-tick path opens the second at a quarter past the previous integer
    /// where the whole-second path opens it at the integer; the digest cannot see that (both open second N), so the
    /// snapshot compare is what would catch a delayed spawn or a timed preset landing a second apart.
    /// </summary>
    [Fact]
    public void ReplayOneSubTick_TimesFour_MatchesReplayOneSecond()
    {
        var whole = BuildEngine();
        var split = BuildEngine();
        whole.ArmReplay([]);
        split.ArmReplay([]);

        for (int second = 0; second < 3; second++)
        {
            whole.ReplayOneSecond();
            for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
            {
                split.ReplayOneSubTick();
            }

            Assert.Equal(whole.StepTrace.LastSecond, split.StepTrace.LastSecond);
            Assert.Equal(whole.StepTrace.LastSequence, split.StepTrace.LastSequence);
            Assert.Equal(whole.StepTrace.LastDigest, split.StepTrace.LastDigest);
            Assert.Equal(Serialize(whole), Serialize(split));
        }
    }

    [Fact]
    public void Digest_ChangesWithTheSecond_AndWithTheSequence()
    {
        var engine = BuildEngine();

        engine.TickOneSecond();
        ulong first = engine.StepTrace.LastDigest;
        engine.TickOneSecond();
        ulong second = engine.StepTrace.LastDigest;

        Assert.NotEqual(first, second);

        // Two engines at the same second with the same sequence digest identically — the digest is a function of
        // the trace, not of the engine.
        var other = BuildEngine();
        other.TickOneSecond();
        Assert.Equal(first, other.StepTrace.LastDigest);
    }

    private static string Serialize(SimulationEngine engine) => JsonSerializer.Serialize(engine.CaptureSnapshot(0), RecordingJsonOptions.Default);

    /// <summary>One airborne aircraft in a hand-built scenario: enough for every step to have something to iterate.</summary>
    private static SimulationEngine BuildEngine()
    {
        var aircraft = new AircraftState
        {
            Callsign = "SWA123",
            AircraftType = "B738",
            Position = new LatLon(37.72, -122.22),
            Altitude = 3000,
            IndicatedAirspeed = 210,
            Transponder = new AircraftTransponder
            {
                Code = 1200,
                AssignedCode = 1200,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" },
            Track = new AircraftTrack(),
        };

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "spine",
                ScenarioName = "Spine",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.AddAircraft(aircraft);
        return engine;
    }
}
