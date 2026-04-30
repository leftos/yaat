using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Replay parity for <see cref="RecordedSettingChange"/> actions. The yaat-server
/// records four setting changes (AutoClearedToLand, AutoCrossRunway, AutoAcceptDelay,
/// AutoDeleteMode); <see cref="SimulationEngine.ApplySettingChange"/> must replay all
/// four so a bundle's playback matches what the user saw live. Bundle export
/// regenerates snapshots via replay, so any setting that doesn't round-trip silently
/// produces incorrect snapshots.
/// </summary>
public class SettingChangeReplayTests(ITestOutputHelper output)
{
    /// <summary>
    /// The S2-OAK-4 bundle records `AutoCrossRunway=True` at t=0 and
    /// `AutoClearedToLand=True` at t=939. After full replay the scenario state
    /// must reflect both — N80ZU's clean landing in
    /// <see cref="Issue143OakErdCompoundAndGaDirectionTests.Diagnostic_N80zu_FullFlight"/>
    /// depends on AutoClearedToLand being true at the threshold.
    /// </summary>
    [Fact]
    public void S2Oak4Bundle_AutoClearedToLandAndAutoCrossRunway_ApplyDuringReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        var recording = RecordingLoader.Load(bundlePath);
        if (recording is null)
        {
            output.WriteLine($"Skipped: {bundlePath} not present");
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);

        // Before t=0 — neither setting applied yet
        engine.Replay(recording, 0);
        Assert.True(engine.Scenario!.AutoCrossRunway, "AutoCrossRunway should be true after t=0 SettingChange");
        Assert.False(engine.Scenario.AutoClearedToLand, "AutoClearedToLand should still be false at t=0");

        // After t=939 — AutoClearedToLand kicks in
        engine.Replay(recording, 950);
        Assert.True(engine.Scenario!.AutoCrossRunway, "AutoCrossRunway should still be true after t=939");
        Assert.True(engine.Scenario.AutoClearedToLand, "AutoClearedToLand should be true after t=939 SettingChange");
    }

    /// <summary>
    /// All four recorded setting types (AutoClearedToLand, AutoCrossRunway,
    /// AutoAcceptDelay, AutoDeleteMode) replay onto the scenario state. Synthetic
    /// recording — uses the existing bundle's scenarioJson so ScenarioLoader has
    /// something valid to chew on, then injects custom SettingChange actions.
    /// </summary>
    [Fact]
    public void AllFourSettingTypes_RoundTripThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        var baseline = RecordingLoader.Load(bundlePath);
        if (baseline is null)
        {
            output.WriteLine($"Skipped: {bundlePath} not present");
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var actions = new List<RecordedAction>
        {
            new RecordedSettingChange(0, "AutoClearedToLand", "True"),
            new RecordedSettingChange(0, "AutoCrossRunway", "True"),
            new RecordedSettingChange(0, "AutoAcceptDelay", "12"),
            new RecordedSettingChange(0, "AutoDeleteMode", "AfterRollout"),
        };

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = actions,
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.AutoClearedToLand, "AutoClearedToLand should round-trip");
        Assert.True(engine.Scenario.AutoCrossRunway, "AutoCrossRunway should round-trip");
        Assert.Equal(TimeSpan.FromSeconds(12), engine.Scenario.AutoAcceptDelay);
        Assert.Equal("AfterRollout", engine.Scenario.ClientAutoDeleteOverride);
    }

    /// <summary>
    /// AutoAcceptDelay is clamped to [0, 60] for non-negative inputs and
    /// passes -1 through verbatim (mirrors yaat-server's SimControlService logic).
    /// </summary>
    [Theory]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    [InlineData("90", 60)] // clamped down
    [InlineData("-1", -1)]
    [InlineData("-5", -1)] // anything negative → -1
    public void AutoAcceptDelay_ParsesAndClamps(string value, int expectedSeconds)
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        var baseline = RecordingLoader.Load(bundlePath);
        if (baseline is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "AutoAcceptDelay", value)],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, 1);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), engine.Scenario!.AutoAcceptDelay);
    }

    /// <summary>
    /// AutoDeleteMode null/empty resets the client override to null (server's
    /// "fall back to scenario default" semantics).
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("AfterTouchdown", "AfterTouchdown")]
    public void AutoDeleteMode_NullOrEmptyClearsOverride(string? value, string? expected)
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        var baseline = RecordingLoader.Load(bundlePath);
        if (baseline is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            // Pre-set with a value so we can verify reset
            Actions = [new RecordedSettingChange(0, "AutoDeleteMode", "AfterRollout"), new RecordedSettingChange(0, "AutoDeleteMode", value)],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, 1);

        Assert.Equal(expected, engine.Scenario!.ClientAutoDeleteOverride);
    }
}
