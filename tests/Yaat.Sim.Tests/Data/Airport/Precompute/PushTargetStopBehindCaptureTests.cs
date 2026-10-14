using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// A stored push target whose last move is a line move with a stop that lies before the point where the move captures
/// the line: the stored end lies past the stop, so the move ends at capture and the stop never decides where. The live
/// check treats such a move as a floating line (<see cref="PushTargetLiveCheck.EndsOnAFloatingLine"/>), so a real aircraft
/// that captures the line sooner than the envelope is not graded unflyable for the shorter slide, and the same push flown
/// in the simulation completes.
/// </summary>
public class PushTargetStopBehindCaptureTests(ITestOutputHelper output)
{
    /// <summary>How long the push may take in the simulation; both measured pushes complete in under a minute.</summary>
    private const int PushBudgetSeconds = 180;

    /// <summary>
    /// OAK gate 23's <c>PUSH TC</c> for a B732 with a B737 parked on gate 24: its stored end lies past its stop, it is
    /// clear, and the push completes.
    /// </summary>
    [Fact]
    public void Oak_Gate23_PushTc_B732_B737OnGate24_ClearAndFlown() =>
        StopBehindCapture_IsClearAndFlown("23", "TC", ("SWA2436", "B732"), [("SWA1118", "B737", "24")]);

    /// <summary>
    /// OAK PCM1's <c>PUSH R1</c> for a C208 on an empty apron: its stored end lies past its stop, it is clear, and the
    /// push completes.
    /// </summary>
    [Fact]
    public void Oak_Pcm1_PushR1_C208_EmptyApron_ClearAndFlown() => StopBehindCapture_IsClearAndFlown("PCM1", "R1", ("BXR1960", "C208"), []);

    private void StopBehindCapture_IsClearAndFlown(
        string stand,
        string targetName,
        (string Callsign, string Type) subject,
        IReadOnlyList<(string Callsign, string Type, string Stand)> parked
    )
    {
        SimulationEngine? engine = Issue222PushbackParkedNeighborTests.BuildEngine(output);
        Assert.NotNull(engine);
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = $"test-oak-{stand}-stop-behind-capture",
            ScenarioName = $"OAK {stand} stop behind capture",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunway = false,
        };
        AircraftState aircraft = Issue222PushbackParkedNeighborTests.SpawnParked(engine, layout, subject.Callsign, subject.Type, stand);
        foreach ((string callsign, string type, string neighbourStand) in parked)
        {
            _ = Issue222PushbackParkedNeighborTests.SpawnParked(engine, layout, callsign, type, neighbourStand);
        }

        PrecomputedPushTarget target = StoredTarget(layout, stand, subject.Type, targetName);
        PushMoveEntry last = target.Moves[^1];
        Assert.Equal(TugMoveShape.ViaLine, last.Shape);
        Assert.NotNull(last.StopAtLatitude);
        Assert.NotNull(last.StopAtLongitude);
        var stop = new LatLon(last.StopAtLatitude.Value, last.StopAtLongitude.Value);
        var plannedEnd = new LatLon(last.PlannedEndLatitude, last.PlannedEndLongitude);
        double pastStopFt = GeoMath.AlongTrackDistanceNm(plannedEnd, stop, new TrueHeading(last.LineTravelTrueDeg)) * GeoMath.FeetPerNm;
        output.WriteLine($"{stand} {target.Command} ({subject.Type}): stored end {pastStopFt:F1} ft past the stop");
        Assert.True(pastStopFt > TugKinematics.StopToleranceFt, $"the stored end lies only {pastStopFt:F1} ft past the stop");

        var request = new PushTargetLiveCheckRequest
        {
            Subject = NeighbourCandidate.From(aircraft),
            Actual = AircraftFootprint.FromType(subject.Type),
            Others = NeighbourCandidate.OnGround(engine.World.GetSnapshot()),
        };
        Assert.Equal(PushTargetLiveOutcome.Clear, PushTargetLiveCheck.Check(target, request).Outcome);

        CommandResult push = engine.SendCommand(subject.Callsign, target.Command);
        Assert.True(push.Success, $"{target.Command} off {stand} was refused: {push.Message}");
        int completedAt = -1;
        for (int t = 1; (t <= PushBudgetSeconds) && (completedAt < 0); t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase)
            {
                completedAt = t;
            }
        }

        output.WriteLine($"{subject.Callsign} {target.Command}: completed at t={completedAt}s");
        Assert.True(
            completedAt > 0,
            $"{subject.Callsign} never completed {target.Command} within {PushBudgetSeconds}s (phase {aircraft.Phases?.CurrentPhase?.Name})"
        );
    }

    /// <summary>The stand's stored target of that name for the type's design group, planned as the precompute plans it.</summary>
    private static PrecomputedPushTarget StoredTarget(AirportGroundLayout layout, string stand, string type, string targetName)
    {
        string group = AirplaneDesignGroups.GroupForType(type).ToString();
        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            PushTargetPlannerTests.Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { stand },
            PushTargetPlannerTests.Sequential
        );
        PushTargetEntry entry = Assert.Single(entries, e => e.DesignGroup == group);
        return Assert.Single(entry.Targets, t => t.Name == targetName);
    }
}
