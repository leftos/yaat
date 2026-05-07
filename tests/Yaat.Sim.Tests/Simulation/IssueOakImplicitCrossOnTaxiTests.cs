using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E: implicit first-crossing on TAXI when an aircraft is already holding short.
///
/// Recording: S2-OAK-4 VFR Transitions/Radar Concepts. N342T (DA42) is holding short
/// of OAK 28R/10L on taxiway B (node 188). The controller issues `TAXI B RWY 28L`
/// at t=674s. Taxiway B crosses 28R/10L on its way to 28L, so the new route's first
/// hold-short is a RunwayCrossing of 28R/10L at the very node the aircraft is sitting
/// on. Expected behavior: the controller's TAXI command implicitly clears that first
/// crossing (no separate CTO needed); the response acknowledges it; and the aircraft
/// taxis through without re-entering HoldingShortPhase for 28R/10L. Subsequent
/// hold-shorts on the new route (e.g. the 28L destination) still apply.
/// </summary>
public class IssueOakImplicitCrossOnTaxiTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/10797ffbbfea.zip";
    private const string Callsign = "N342T";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("HoldingShortPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// When the aircraft is already in HoldingShortPhase for runway X and a new TAXI
    /// command produces a route whose first RunwayCrossing is also for runway X, the
    /// hold-short is marked cleared, the response message acknowledges the implicit
    /// cross, and the aircraft does not re-enter HoldingShortPhase for X.
    /// </summary>
    [Fact]
    public void TaxiAcrossSameRunway_ImplicitlyClearsFirstCrossing()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=673 — one second before the recorded `TAXI B RWY 28L`.
        engine.Replay(recording, 673);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // Precondition: aircraft is holding short of 28R on taxiway B.
        var priorPhase = aircraft.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(priorPhase);
        Assert.False(string.IsNullOrEmpty(priorPhase.HoldShort.TargetName));
        string priorRwy = priorPhase.HoldShort.TargetName!;
        output.WriteLine(
            $"Precondition: {Callsign} holding short of {priorRwy} at node {priorPhase.HoldShort.NodeId} on {aircraft.Ground.CurrentTaxiway}"
        );

        // Issue the TAXI command live (not via replay), exactly as the controller did.
        var result = engine.SendCommand(Callsign, "TAXI B RWY 28L");
        Assert.True(result.Success, $"TAXI command failed: {result.Message}");
        output.WriteLine($"TAXI response: {result.Message}");

        // The new route should have at least one RunwayCrossing hold-short, and the
        // first one (which sits at the same node the aircraft is parked at) must be
        // marked cleared.
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        var firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
        Assert.NotNull(firstCrossing);
        Assert.False(string.IsNullOrEmpty(firstCrossing.TargetName));
        Assert.True(
            RunwayIdentifier.Parse(firstCrossing.TargetName!).Overlaps(RunwayIdentifier.Parse(priorRwy)),
            $"First RunwayCrossing is for {firstCrossing.TargetName} which doesn't overlap prior hold-short {priorRwy}"
        );
        Assert.True(
            firstCrossing.IsCleared,
            $"Expected the first RunwayCrossing ({firstCrossing.TargetName} at node {firstCrossing.NodeId}) to be implicitly cleared because {Callsign} was already holding short of {priorRwy}, but IsCleared=false"
        );

        // The TAXI response should acknowledge the implicit cross.
        Assert.Contains("cross", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(firstCrossing.TargetName!, result.Message, StringComparison.OrdinalIgnoreCase);

        // Tick forward and confirm the aircraft moves through the crossing without
        // re-entering HoldingShortPhase for that runway.
        bool sawNonZeroSpeed = false;
        var crossedRunway = RunwayIdentifier.Parse(firstCrossing.TargetName!);

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            if (aircraft.GroundSpeed > 0.5)
            {
                sawNonZeroSpeed = true;
            }

            if (aircraft.Phases?.CurrentPhase is HoldingShortPhase hs && hs.HoldShort.TargetName is { Length: > 0 } target)
            {
                var hsRwy = RunwayIdentifier.Parse(target);
                Assert.False(
                    hsRwy.Overlaps(crossedRunway),
                    $"After implicit clear, {Callsign} should not re-enter HoldingShortPhase for {target} at t+{t} (overlapped {firstCrossing.TargetName})"
                );
            }
        }

        Assert.True(sawNonZeroSpeed, $"{Callsign} never started moving in the 120s after TAXI was issued");
    }

    /// <summary>
    /// The implicit clear only covers the first crossing — the destination runway
    /// hold-short (28L) still applies, and the aircraft holds there.
    /// </summary>
    [Fact]
    public void TaxiAcrossSameRunway_StillHoldsAtDestination()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 673);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var result = engine.SendCommand(Callsign, "TAXI B RWY 28L");
        Assert.True(result.Success, $"TAXI command failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        // The destination 28L hold-short must NOT be cleared.
        var destHold = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(destHold);
        Assert.False(destHold.IsCleared, "Destination runway hold-short should remain uncleared after TAXI");

        // Tick forward; aircraft should eventually reach a HoldingShortPhase whose
        // runway overlaps 28L (the destination), without ever crossing it.
        var dest28L = RunwayIdentifier.Parse("28L");
        bool reachedDestinationHold = false;

        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hs
                && hs.HoldShort.TargetName is { Length: > 0 } target
                && RunwayIdentifier.Parse(target).Overlaps(dest28L)
            )
            {
                reachedDestinationHold = true;
                output.WriteLine($"t+{t}: {Callsign} holding short of {target} on {aircraft.Ground.CurrentTaxiway} (destination)");
                break;
            }
        }

        Assert.True(reachedDestinationHold, $"{Callsign} did not reach the 28L destination hold-short within 240s");
    }
}
