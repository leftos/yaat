using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression for the replay/reconstruction two-way-comms divergence surfaced by the
/// S2-OAK-3 "VFR Sequencing" bundle. In solo training, N436MS (a VFR arrival inbound to the
/// OAK Class C, track-owned by SFO_DEP and never handed to the student) is given <c>FH 150</c>
/// at t=26. A vector establishes two-way communication, so the Class C entry gate should clear
/// and the aircraft should keep flying its heading.
///
/// The live <see cref="SimulationEngine.SendCommand"/> path calls
/// <see cref="Yaat.Sim.Pilot.PilotInitialContactEligibility.RegisterControllerContact"/> on a
/// successful dispatch; the replay path (<see cref="SimulationEngine.ReplayCommand"/>) did not,
/// so a replayed/reconstructed <c>FH 150</c> never set the gate flags and N436MS spuriously
/// entered an <see cref="AirspaceBoundaryHoldPhase"/> and orbited — diverging from the live
/// session and corrupting every reconstructed bundle snapshot.
/// </summary>
public class ReplayTwoWayCommsTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-vfr-twoway-comms-replay-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void ReplayedVector_EstablishesTwoWayComms_NoBoundaryHold()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine($"Skipped: {RecordingPath} or test data not present");
            return;
        }

        // FH 150 lands at t=26; the buggy boundary hold fired at t=55. Replay well past it.
        engine.Replay(recording, 80);

        var ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);

        output.WriteLine(
            $"N436MS: madeContact={ac.HasMadeInitialContact} ctrlAck={ac.HasControllerAcknowledgedInitialContact} "
                + $"phase={ac.Phases?.CurrentPhase?.Name ?? "(none)"} hdg={ac.TrueHeading.Degrees:F0}"
        );

        bool inBoundaryHold = ac.Phases?.Phases.Any(p => p is AirspaceBoundaryHoldPhase) ?? false;
        Assert.False(inBoundaryHold, "Replayed FH 150 should establish two-way comms; N436MS must not enter an AirspaceBoundaryHold.");

        // The vector established two-way comms: the controller side is always acknowledged, and
        // because N436MS is owned by another position (the AI pilot cannot check in itself), the
        // controller's instruction also marks the pilot side, satisfying the Class C entry gate.
        Assert.True(ac.HasControllerAcknowledgedInitialContact, "FH 150 should acknowledge controller contact on replay.");
        Assert.True(ac.HasMadeInitialContact, "FH 150 to an other-position-owned aircraft should establish initial contact on replay.");
    }
}
