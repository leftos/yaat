using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Replay parity for <see cref="RecordedSettingChange"/> actions. The yaat-server
/// records four setting changes (AutoClearedToLand, AutoCrossRunwayMode (the older AutoCrossRunway on/off too), AutoAcceptDelay,
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
        SessionRecording? recording = RecordingLoader.Load(bundlePath);
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
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());

        // Before t=0 — neither setting applied yet
        engine.Replay(recording, 0);
        Assert.Equal(AutoCrossRunwayMode.All, engine.Scenario!.AutoCrossRunwayMode);
        Assert.False(engine.Scenario.AutoClearedToLand, "AutoClearedToLand should still be false at t=0");

        // After t=939 — AutoClearedToLand kicks in
        engine.Replay(recording, 950);
        Assert.Equal(AutoCrossRunwayMode.All, engine.Scenario!.AutoCrossRunwayMode);
        Assert.True(engine.Scenario.AutoClearedToLand, "AutoClearedToLand should be true after t=939 SettingChange");
    }

    /// <summary>
    /// All four recorded setting types (AutoClearedToLand, AutoCrossRunway in its older on/off form,
    /// AutoAcceptDelay, AutoDeleteMode) replay onto the scenario state. Synthetic
    /// recording — uses the existing bundle's scenarioJson so ScenarioLoader has
    /// something valid to chew on, then injects custom SettingChange actions.
    /// </summary>
    /// <summary>
    /// RpoShowPilotSpeech is a sim-control toggle that switches sim-initiated pilot
    /// transmissions between Warning (orange, default) and PilotSpeech (green) terminal
    /// kinds. Like the four other settings, it must round-trip through replay so bundle
    /// snapshots regenerate identically.
    /// </summary>
    [Fact]
    public void RpoShowPilotSpeech_RoundTripsThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "RpoShowPilotSpeech", "True")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.RpoShowPilotSpeech, "RpoShowPilotSpeech should round-trip through replay");
    }

    [Fact]
    public void AutoGoAroundOnOccupiedRunway_RoundTripsThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "AutoGoAroundOnOccupiedRunway", "True")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.AutoGoAroundOnOccupiedRunway, "AutoGoAroundOnOccupiedRunway should round-trip through replay");
    }

    [Fact]
    public void AutoArrivalSpacingOnOccupiedRunway_RoundTripsThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "AutoArrivalSpacingOnOccupiedRunway", "True")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.AutoArrivalSpacingOnOccupiedRunway, "AutoArrivalSpacingOnOccupiedRunway should round-trip through replay");
    }

    [Fact]
    public void AutoRejectTakeoffOnOccupiedRunway_RoundTripsThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "AutoRejectTakeoffOnOccupiedRunway", "True")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.AutoRejectTakeoffOnOccupiedRunway, "AutoRejectTakeoffOnOccupiedRunway should round-trip through replay");
    }

    [Fact]
    public void LiveTrafficSettings_RoundTripThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "LiveTrafficEnabled", "True"), new RecordedSettingChange(0, "LiveTrafficCeilingFt", "7500")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.LiveTrafficEnabled, "LiveTrafficEnabled should round-trip through replay");
        Assert.Equal(7500, engine.Scenario.LiveTrafficCeilingFt);
    }

    [Fact]
    public void SoloTrainingMode_RoundTripsThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "SoloTrainingMode", "True")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.SoloTrainingMode, "SoloTrainingMode should round-trip through replay");
    }

    [Fact]
    public void SoloRpoCommandsAllowed_RoundTripsThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "SoloRpoCommandsAllowed", "True")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.SoloRpoCommandsAllowed, "SoloRpoCommandsAllowed should round-trip through replay");
    }

    [Fact]
    public void SoloPacingRates_RoundTripThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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
            Actions =
            [
                new RecordedSettingChange(0, "SoloParkingInitialCallupRatePercent", "35"),
                new RecordedSettingChange(0, "SoloArrivalGeneratorRatePercent", "65"),
            ],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.Equal(35, engine.Scenario!.SoloParkingInitialCallupRatePercent);
        Assert.Equal(65, engine.Scenario.SoloArrivalGeneratorRatePercent);
    }

    /// <summary>
    /// A recording made with the three-state auto-cross records <c>AutoCrossRunwayMode</c> by name; Inactive runways only
    /// replays onto the scenario state as recorded.
    /// </summary>
    [Fact]
    public void AutoCrossRunwayMode_InactiveOnly_RoundTripsThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var recording = new SessionRecording
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = [new RecordedSettingChange(0, "AutoCrossRunwayMode", "InactiveOnly")],
            TotalElapsedSeconds = 1,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.Equal(AutoCrossRunwayMode.InactiveOnly, engine.Scenario!.AutoCrossRunwayMode);
    }

    [Fact]
    public void AllFourSettingTypes_RoundTripThroughReplay()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.True(engine.Scenario!.AutoClearedToLand, "AutoClearedToLand should round-trip");
        Assert.Equal(AutoCrossRunwayMode.All, engine.Scenario!.AutoCrossRunwayMode);
        Assert.Equal(TimeSpan.FromSeconds(12), engine.Scenario.AutoAcceptDelay);
        Assert.Equal("AfterRollout", engine.Scenario.ClientAutoDeleteOverride);
    }

    [Fact]
    public void StudentLowestWorkedLevel_ReplaysFromTheRecordedChange()
    {
        const string bundlePath = "TestData/66fd6538542e.zip";
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        SimulationEngine ReplayWith(List<RecordedAction> actions)
        {
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
            var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
            engine.Replay(recording, 1);
            return engine;
        }

        SimulationEngine set = ReplayWith([new RecordedSettingChange(0, "StudentLowestWorkedLevel", "GND")]);
        Assert.Equal(AtcPositionLevel.Ground, set.Scenario!.StudentLowestWorkedLevel);

        SimulationEngine cleared = ReplayWith([
            new RecordedSettingChange(0, "StudentLowestWorkedLevel", "GND"),
            new RecordedSettingChange(0, "StudentLowestWorkedLevel", ""),
        ]);
        Assert.Null(cleared.Scenario!.StudentLowestWorkedLevel);
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
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
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
        SessionRecording? baseline = RecordingLoader.Load(bundlePath);
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

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 1);

        Assert.Equal(expected, engine.Scenario!.ClientAutoDeleteOverride);
    }
}
