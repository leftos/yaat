using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #194: at KOAK a VFR aircraft (N346G) assigned departure
/// runway 28R taxis via C/B and must cross runway 15/33 en route. When it reaches the
/// hold-short line of the <em>crossing</em> runway 15/33 (not its departure runway) the
/// solo-mode pilot wrongly transmitted "...holding short runway 15/33, ready for departure."
///
/// Recording: S2-OAK-1 (1) VFR Takeoff/Landing (ZOA, solo mode). N346G's route puts a
/// <see cref="HoldShortReason.RunwayCrossing"/> hold-short at 15/33 (snapshot-confirmed at
/// t=160s) and a <see cref="HoldShortReason.DestinationRunway"/> hold-short at 28R. "Ready
/// for departure" must fire only at the departure runway, never at an intermediate crossing.
///
/// The "ready for departure" call records a persistent <see cref="PilotPendingRequestKind.Takeoff"/>
/// pending request (snapshot-serialized), which is the stable observable here — the spoken
/// transmission itself is drained within the tick it is queued.
/// </summary>
public class Issue194CrossingReadyForDepartureTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue194-crossing-ready-for-departure-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N346G_HoldingShortOfCrossingRunway_DoesNotReportReadyForDeparture()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine($"Skipped: {RecordingPath} or test data not present");
            return;
        }

        // Full replay to a point where N346G is holding short of the 15/33 crossing
        // (recording's own snapshot at t=160 has it in HoldingShort/RunwayCrossing/15/33).
        engine.Replay(recording, 165);

        var ac = engine.FindAircraft("N346G");
        Assert.NotNull(ac);

        // Confirm we actually reproduced the scenario: holding short of the CROSSING runway.
        var phase = ac.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.True(phase is not null, $"Expected N346G to be holding short at t=165; current phase was {ac.Phases?.CurrentPhase?.Name ?? "(none)"}");
        Assert.Equal(HoldShortReason.RunwayCrossing, phase!.HoldShort.Reason);
        Assert.Equal("15/33", phase.HoldShort.TargetName);

        // The bug: holding short of a crossing runway records a "ready for departure" Takeoff
        // request. That must not happen — the aircraft is not departing from 15/33.
        var request = ac.PendingPilotRequest;
        output.WriteLine(
            request is null
                ? "N346G PendingPilotRequest: (none)"
                : $"N346G PendingPilotRequest: Kind={request.Kind} Runway={request.RunwayId} Line=\"{request.LastPilotLine}\""
        );

        Assert.False(
            request is { Kind: PilotPendingRequestKind.Takeoff },
            $"N346G should not have a Takeoff (ready-for-departure) request while holding short of crossing runway "
                + $"{phase.HoldShort.TargetName}; got: {request?.LastPilotLine}"
        );
        Assert.True(
            request?.LastPilotLine is null || !request.LastPilotLine.Contains("ready for departure", StringComparison.OrdinalIgnoreCase),
            $"N346G should not report 'ready for departure' at a crossing; got: {request?.LastPilotLine}"
        );
    }
}
