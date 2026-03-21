using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #121: CTOPP (Cleared Takeoff Present Position)
/// returns "Unknown command" when sent to CMD02 (EC35 helicopter).
///
/// Root cause: ApplyCommand (the non-phase dispatch path) was missing cases
/// for ClearedTakeoffPresentCommand and other helicopter/hold commands.
/// These commands only existed in TryApplyTowerCommand (the phase-interactive path).
///
/// Recording: S3-NCTA-1 | Area A Familiarization — CMD02 is an EC35 helicopter
/// airborne at low altitude near SJC.
/// </summary>
public class Issue121CtoppTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue121-ctopp-recording.json";

    private static SessionRecording? LoadRecording()
    {
        if (!File.Exists(RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

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

    /// <summary>
    /// CTOPP on an airborne helicopter must not return "Unknown command".
    /// Before the fix, ApplyCommand had no case for ClearedTakeoffPresentCommand,
    /// so it fell through to the default "Unknown command" case.
    /// After the fix, it properly routes to TryClearedTakeoffPresent which
    /// returns a domain-specific rejection ("requires aircraft to be on the ground").
    /// </summary>
    [Fact]
    public void Ctopp_DoesNotReturnUnknownCommand()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, recording.TotalElapsedSeconds);

        var aircraft = engine.FindAircraft("CMD02");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"CMD02: type={aircraft.AircraftType} alt={aircraft.Altitude:F0} onGround={aircraft.IsOnGround} phase={aircraft.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );

        var result = engine.SendCommand("CMD02", "CTOPP");

        output.WriteLine($"CTOPP result: success={result.Success} message={result.Message}");

        // The key assertion: must not be "Unknown command" (the pre-fix behavior)
        Assert.NotEqual("Unknown command", result.Message);
    }

    /// <summary>
    /// CMD02 is airborne (altitude 2, speed 120), so CTOPP should be rejected
    /// with a domain-specific message, not "Unknown command".
    /// </summary>
    [Fact]
    public void Ctopp_RejectsAirborneHelicopterWithDomainError()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, recording.TotalElapsedSeconds);

        var aircraft = engine.FindAircraft("CMD02");
        Assert.NotNull(aircraft);

        var result = engine.SendCommand("CMD02", "CTOPP");

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
    }

    /// <summary>
    /// CTOPP on a non-helicopter must return "only valid for helicopters",
    /// not "Unknown command".
    /// </summary>
    [Fact]
    public void Ctopp_RejectsNonHelicopterWithDomainError()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, recording.TotalElapsedSeconds);

        // LXJ453 is a fixed-wing jet in this scenario
        var aircraft = engine.FindAircraft("LXJ453");
        Assert.NotNull(aircraft);

        var result = engine.SendCommand("LXJ453", "CTOPP");

        Assert.False(result.Success);
        Assert.Contains("only valid for helicopters", result.Message!);
    }
}
