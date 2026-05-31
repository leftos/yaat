using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.V2Acceptance;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the SFO RAMP-crosses-runway clearance: TAXI A E 28R HS E sent to N70234 at a gate
/// from which taxiway A lies across active runways. The command must fail — the aircraft cannot be
/// cleared via a taxiway it cannot reach.
///
/// <para>
/// V1 produced a straight-line RAMP segment across two runways to reach A (a runway incursion). V2
/// never crosses runways, but its mandatory-connector detour would otherwise bypass the unreachable A
/// entirely and route to E via a connector — taxiing the aircraft somewhere the controller never
/// cleared. The V2 fix rejects the clearance instead: a named taxiway that appears nowhere in the
/// resolved route fails the command (<c>SegmentExpander</c>). Either way the command fails; this runs
/// on the full V2 stack to pin the V2 behavior.
/// </para>
///
/// Recording: S1-SFO-2 Ground Control 28/01 — N70234 on the ground at SFO.
/// </summary>
[Collection("V2 Acceptance")]
public class SfoRampCrossesRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/e55edd55bed7.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData(FilletMode.Standard);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// TAXI A E 28R HS E to N70234 must fail: taxiway A is unreachable from the aircraft's gate
    /// without crossing a runway, so the clearance cannot be honored.
    /// </summary>
    [Fact]
    public void TaxiCommand_AcrossRunways_ShouldFail()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay far enough for N70234 to exist
        engine.Replay(recording, 100);
        var aircraft = engine.FindAircraft("N70234");

        // Try further into the recording if not found yet. Use FastForwardTo to advance
        // from current state — Replay() resets to t=0 each call, which makes this loop O(N²).
        if (aircraft is null)
        {
            for (int t = 200; t <= recording.TotalElapsedSeconds; t += 100)
            {
                engine.FastForwardTo(t, recording.Actions);
                aircraft = engine.FindAircraft("N70234");
                if (aircraft is not null)
                {
                    output.WriteLine($"Found N70234 at t={t}");
                    break;
                }
            }
        }

        if (aircraft is null)
        {
            output.WriteLine("N70234 not found in recording — skipping");
            return;
        }

        output.WriteLine($"N70234 at ({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) onGround={aircraft.IsOnGround}");

        var result = engine.SendCommand("N70234", "TAXI A E 28R HS E");
        output.WriteLine($"TAXI result: Success={result.Success}, Message={result.Message}");

        Assert.False(
            result.Success,
            $"TAXI A E 28R HS E should fail — taxiway A is unreachable from the gate without crossing a runway — but succeeded: {result.Message}"
        );
    }
}
