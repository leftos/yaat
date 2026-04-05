using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for SFO RAMP-crosses-runway bug: TAXI A E 28R HS E sent to
/// N70234 at a gate creates a straight-line RAMP segment across two runways
/// to reach taxiway A. The command should fail instead.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — N70234 on the ground at SFO.
/// </summary>
public class SfoRampCrossesRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/09304e0c727e.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Diagnostic_FindN70234()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay in chunks, looking for N70234
        for (int t = 0; t <= recording.TotalElapsedSeconds; t += 50)
        {
            engine.Replay(recording, t);
            var ac = engine.FindAircraft("N70234");
            if (ac is not null)
            {
                output.WriteLine($"t={t}: N70234 found at ({ac.Latitude:F6}, {ac.Longitude:F6}) " + $"onGround={ac.IsOnGround}");

                // Try the command
                var result = engine.SendCommand("N70234", "TAXI A E 28R HS E");
                output.WriteLine($"  TAXI command result: Success={result.Success}, Message={result.Message}");
                return;
            }
        }

        output.WriteLine("N70234 not found at any time in recording");
    }

    /// <summary>
    /// TAXI A E 28R HS E to N70234 should fail because reaching taxiway A from
    /// the aircraft's gate position would require crossing runways via a
    /// straight-line RAMP segment.
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

        // Try further into the recording if not found yet
        if (aircraft is null)
        {
            for (int t = 200; t <= recording.TotalElapsedSeconds; t += 100)
            {
                engine.Replay(recording, t);
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

        output.WriteLine($"N70234 at ({aircraft.Latitude:F6}, {aircraft.Longitude:F6}) onGround={aircraft.IsOnGround}");

        var result = engine.SendCommand("N70234", "TAXI A E 28R HS E");
        output.WriteLine($"TAXI result: Success={result.Success}, Message={result.Message}");

        Assert.False(result.Success, $"TAXI A E 28R HS E should fail (route crosses runways) but succeeded: {result.Message}");
    }
}
