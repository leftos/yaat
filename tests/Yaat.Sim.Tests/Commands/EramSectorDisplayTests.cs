using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Leader direction and length, DRI halo and dwell lock are each ERAM sector's own display state
/// (<see cref="AircraftEramState.SectorDisplays"/>): one sector's entry never reaches another's, the dwell lock is a
/// recorded absolute <c>DWELL {facility} {sector} 1|0</c> entry that a replay reproduces, and the state round-trips
/// through the snapshot.
/// </summary>
public class EramSectorDisplayTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/66fd6538542e.zip";

    private static CommandResult Apply(AircraftState ac, string entry) =>
        EramEntryEngine.Apply(ac, entry, new EramEntryContext(null, Scenario: null, Redirect: null, new EramConflictState(), SweptPosition: null));

    private static AircraftState Aircraft() => new() { Callsign = "UAL1", AircraftType = "B738" };

    [Fact]
    public void Dwell_SetsAndClearsTheLock_ForTheNamedSectorOnly()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "DWELL ZOA 45 1").Success);

        CommandResult set = Apply(ac, "DWELL ZOA 44 1");

        Assert.True(set.Success, set.Message);
        Assert.True(ac.Eram.DisplayFor("ZOA", "44").IsDwellLocked);
        Assert.True(ac.Eram.DisplayFor("ZOA", "45").IsDwellLocked);
        Assert.False(ac.Eram.DisplayFor("ZNY", "44").IsDwellLocked);

        CommandResult cleared = Apply(ac, "DWELL ZOA 44 0");

        Assert.True(cleared.Success, cleared.Message);
        Assert.False(ac.Eram.DisplayFor("ZOA", "44").IsDwellLocked);
        Assert.True(ac.Eram.DisplayFor("ZOA", "45").IsDwellLocked);
    }

    [Fact]
    public void Dwell_IsAbsolute_SoRepeatingItChangesNothing()
    {
        AircraftState ac = Aircraft();

        Apply(ac, "DWELL ZOA 44 1");
        Apply(ac, "DWELL ZOA 44 1");

        Assert.True(ac.Eram.DisplayFor("ZOA", "44").IsDwellLocked);
    }

    [Fact]
    public void Dwell_Cleared_RemovesTheSectorEntry()
    {
        AircraftState ac = Aircraft();

        Apply(ac, "DWELL ZOA 44 1");
        Apply(ac, "DWELL ZOA 44 0");

        Assert.Empty(ac.Eram.SectorDisplays);
    }

    [Theory]
    [InlineData("DWELL", EramEntryErrors.MessageTooShort)]
    [InlineData("DWELL ZOA 44", EramEntryErrors.MessageTooShort)]
    [InlineData("DWELL ZOA 44 1 0", EramEntryErrors.MessageTooLong)]
    [InlineData("DWELL ZOA 44 2", "MsgCofieFormat 2")]
    [InlineData("DWELL ZOA 44 ON", "MsgCofieFormat ON")]
    public void Dwell_Malformed_IsRefused_AndChangesNothing(string entry, string message)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Empty(ac.Eram.SectorDisplays);
    }

    [Fact]
    public void SectorDisplays_RoundTripThroughTheSnapshot()
    {
        AircraftState ac = Aircraft();
        Apply(ac, "LEADER ZOA 44 D9 L2");
        Apply(ac, "DRI ZOA 44 T");
        Apply(ac, "DWELL ZOA 45 1");

        var restored = AircraftEramState.FromSnapshot(ac.Eram.ToSnapshot());

        Assert.Equal(new EramSectorDisplay(new EramSectorKey("ZOA", "44"), 9, 2, 2, false), restored.DisplayFor("ZOA", "44"));
        Assert.Equal(new EramSectorDisplay(new EramSectorKey("ZOA", "45"), null, null, null, true), restored.DisplayFor("ZOA", "45"));
        Assert.Equal(2, restored.SectorDisplays.Count);
    }

    [Fact]
    public void OldSnapshot_WithThePerAircraftScalars_Loads_WithNoSectorState()
    {
        // The per-aircraft fields named no sector, so they map to none: the old snapshot loads with every sector at
        // CRC's defaults.
        const string json = """{ "IsDwellLocked": true, "LeaderDirection": 7, "LeaderLength": 2, "DriHaloType": 1, "InterimAltitude": 240 }""";

        AircraftEramStateDto dto = JsonSerializer.Deserialize<AircraftEramStateDto>(json, RecordingJsonOptions.Default)!;
        var eram = AircraftEramState.FromSnapshot(dto);

        Assert.Empty(eram.SectorDisplays);
        Assert.Equal(240, eram.InterimAltitude);
    }

    [Fact]
    public void Replay_OfTwoDwellEntries_ReproducesThePerSectorLock()
    {
        SessionRecording? baseline = RecordingLoader.Load(BundlePath);
        if (baseline is null)
        {
            output.WriteLine($"Skipped: {BundlePath} not present");
            return;
        }

        TestVnasData.EnsureInitialized();
        var probe = new SimulationEngine(new TestAirportGroundData());
        probe.Replay(WithActions(baseline, [], 0), 0);
        string callsign = probe.World.GetSnapshot()[0].Callsign;

        List<RecordedAction> actions =
        [
            new RecordedEramEntry(1, callsign, "DWELL ZOA 44 1", null, null, null, null, null),
            // The same value again keeps the lock: a toggle would clear it here.
            new RecordedEramEntry(2, callsign, "DWELL ZOA 44 1", null, null, null, null, null),
            new RecordedEramEntry(2, callsign, "DWELL ZOA 45 1", null, null, null, null, null),
            new RecordedEramEntry(3, callsign, "DWELL ZOA 44 0", null, null, null, null, null),
        ];
        SessionRecording recording = WithActions(baseline, actions, 5);
        var replay = new SimulationEngine(new TestAirportGroundData());

        replay.Replay(recording, 2);
        AircraftEramState at2 = replay.World.FindAircraft(callsign)!.Eram;
        Assert.True(at2.DisplayFor("ZOA", "44").IsDwellLocked);
        Assert.True(at2.DisplayFor("ZOA", "45").IsDwellLocked);

        replay.Replay(recording, 4);
        AircraftEramState at4 = replay.World.FindAircraft(callsign)!.Eram;
        Assert.False(at4.DisplayFor("ZOA", "44").IsDwellLocked);
        Assert.True(at4.DisplayFor("ZOA", "45").IsDwellLocked);
    }

    private static SessionRecording WithActions(SessionRecording baseline, List<RecordedAction> actions, double total) =>
        new()
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = actions,
            TotalElapsedSeconds = total,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };
}
