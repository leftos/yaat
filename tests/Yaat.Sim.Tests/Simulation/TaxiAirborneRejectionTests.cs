using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: TAXI command must be rejected for airborne aircraft.
///
/// Recording: S2-OAK-4 VFR Transitions/Radar Concepts — N805FM receives
/// ERB 28R at t=919, CLAND at t=920, then TAXI E RWY 28R at t=953.
/// The aircraft is still airborne at t=953, so the TAXI command should fail.
/// </summary>
public class TaxiAirborneRejectionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/taxi-airborne-recording.zip";

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

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void TaxiCommand_RejectedWhenAircraftIsAirborne()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=952 — one second before the TAXI command.
        // N805FM got ERB 28R at t=919 and CLAND at t=920, still on approach.
        engine.Replay(recording, 952);

        var aircraft = engine.FindAircraft("N805FM");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"N805FM at t=952: alt={aircraft.Altitude:F0}, IsOnGround={aircraft.IsOnGround}, "
                + $"lat={aircraft.Latitude:F6}, lon={aircraft.Longitude:F6}"
        );

        // Aircraft should still be airborne
        Assert.False(aircraft.IsOnGround, $"N805FM should be airborne at t=952 but IsOnGround=true (alt={aircraft.Altitude:F0})");

        // TAXI command must fail for airborne aircraft
        var result = engine.SendCommand("N805FM", "TAXI E RWY 28R");
        Assert.False(result.Success, $"TAXI should be rejected for airborne aircraft, but succeeded: {result.Message}");
    }
}
