using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #207: an aircraft must not hold short of the runway it just
/// landed on when given a taxi clearance during its landing rollout.
///
/// Recording: S2-OAK-5 Practical Exam Preparation/Advanced Concepts. N655EX (C210)
/// lands on OAK 28R and, while still in the Landing rollout (IAS ~22 kt, on the
/// runway), the controller issues `TAXI G D J` at t=2222s. Taxiway G exits 28R to the
/// north, so the resolved route's FIRST RunwayCrossing hold-short is 28R/10L at node
/// 361 — the exit bar of the runway the aircraft just landed on. The implicit
/// first-crossing clearance in <see cref="GroundCommandHandler"/> only recognized the
/// post-exit phases (HoldingShort/RunwayExit/HoldingAfterExit), not the Landing
/// rollout, so node 361 stayed an uncleared crossing and the aircraft called
/// "holding short runway 28R/10L at G" for the runway it had just landed on.
///
/// Correct behavior: exiting the landing runway is not a hold-short — the first
/// crossing of that runway is implicitly cleared by the TAXI command. The genuine
/// downstream crossings on taxiway J (28R again at node 501, 28L at node 498) remain
/// uncleared and still require a CROSS clearance.
///
/// NOTE: this recording also exposes a SEPARATE, pre-existing navigator pure-pursuit
/// orbit at the 28R→G exit (a 32° unfilleted shape-point kink at node 360). That orbit
/// is byte-identical before and after this fix and is tracked by issue #213, so this test
/// asserts the route-level clearance rather than ticking the aircraft through the kink.
/// </summary>
public class Issue207LandingRunwayCrossTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue207-oak-landing-taxi-holdshort-recording.zip";
    private const string Callsign = "N655EX";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Issuing TAXI while still in the Landing rollout implicitly clears the first
    /// crossing of the landing runway (its exit bar at node 361), so the aircraft is
    /// not made to hold short of the runway it just landed on. Subsequent crossings on
    /// the route stay uncleared.
    /// </summary>
    [Fact]
    public void TaxiDuringLandingRollout_ImplicitlyClearsLandingRunwayExit()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=2221 — one second before the recorded `TAXI G D J`.
        engine.Replay(recording, 2221);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // Precondition: still rolling out on the landing runway (28R).
        Assert.True(aircraft.IsOnGround, $"{Callsign} should be on the ground during rollout");
        Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        string landedRwy = aircraft.Phases?.ClearedRunwayId ?? aircraft.Phases?.AssignedRunway?.Designator ?? "";
        Assert.False(string.IsNullOrEmpty(landedRwy));
        output.WriteLine($"Precondition: {Callsign} in LandingPhase on {landedRwy}, IAS={aircraft.IndicatedAirspeed:F1}kt");

        // Issue the TAXI command live, exactly as the controller did.
        var result = engine.SendCommand(Callsign, "TAXI G D J");
        Assert.True(result.Success, $"TAXI command failed: {result.Message}");
        output.WriteLine($"TAXI response: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine(
            "Hold-shorts: " + string.Join(", ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason},cleared={h.IsCleared})"))
        );

        var crossings = route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing).ToList();
        Assert.NotEmpty(crossings);

        // The first RunwayCrossing is the exit bar of the runway just landed on — it
        // must overlap the landing runway and be implicitly cleared (no hold short of
        // the runway the aircraft is rolling out on).
        var firstCrossing = crossings[0];
        Assert.False(string.IsNullOrEmpty(firstCrossing.TargetName));
        Assert.True(
            RunwayIdentifier.Parse(firstCrossing.TargetName!).Overlaps(RunwayIdentifier.Parse(landedRwy)),
            $"First RunwayCrossing is for {firstCrossing.TargetName}, expected it to overlap landing runway {landedRwy}"
        );
        Assert.True(
            firstCrossing.IsCleared,
            $"Expected the exit crossing of the landing runway ({firstCrossing.TargetName} at node {firstCrossing.NodeId}) "
                + $"to be implicitly cleared because {Callsign} just landed on {landedRwy}, but IsCleared=false"
        );

        // The response acknowledges the implicit cross of the landing runway.
        Assert.Contains("cross", result.Message, StringComparison.OrdinalIgnoreCase);

        // Subsequent crossings (the genuine re-cross of 28R and the 28L crossing on J)
        // still require clearance — only the landing-runway exit is auto-cleared.
        foreach (var later in crossings.Skip(1))
        {
            Assert.False(
                later.IsCleared,
                $"Downstream crossing {later.TargetName} at node {later.NodeId} should still require explicit clearance, but was auto-cleared"
            );
        }
    }
}
