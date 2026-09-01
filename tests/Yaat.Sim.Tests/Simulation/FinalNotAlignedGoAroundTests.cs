using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Follow-up to issue #412: a lateral-alignment go-around on final. The issue-412 recording's
/// snapshots carry the original buggy flight — N115SA (PAY3) blown ~1,250 ft through the OAK 28L
/// final onto the 28R centerline at ~250 ft on short final. Before this check, a cleared-to-land
/// aircraft in that state flew essentially to the flare before anything objected (the first
/// lateral gate was LandingPhase's stabilization check at ~30 ft AGL). AIM 5-5-5.a.1(b): the
/// pilot executes a missed approach on determining a safe landing is not possible; 7110.65
/// §3-10-5.d — "go-around, you appear to be aligned with the wrong runway" — is the
/// controller-side authority for exactly this condition.
///
/// Replay strategy: hybrid — restore the recorded snapshot at t=2255 (the buggy overshoot, on
/// FinalApproach ~0.6 nm out, ~380 ft right of the 28R centerline i.e. ~1,380 ft right of the
/// assigned 28L), issue CLAND so no other go-around path fires, then tick physics and require
/// the not-aligned go-around while the aircraft still has altitude.
/// </summary>
public class FinalNotAlignedGoAroundTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue412-wrong-runway-pattern-recording.zip";
    private const string Callsign = "N115SA";
    private const int SnapshotTime = 2255;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("FinalApproachPhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void ClearedToLand_FarOffAssignedCenterline_GoesAroundWithAltitudeToSpare()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snapshot = archive.ReadSnapshotAt(SnapshotTime);
        if (snapshot is null)
        {
            return;
        }
        engine.RestoreFromSnapshot(snapshot.State);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);

        // Clear it to land so the no-clearance gates stay quiet — only the lateral
        // check (or, without it, the very late LandingPhase gate) can object.
        var cland = engine.SendCommand(Callsign, "CLAND");
        Assert.True(cland.Success, $"CLAND rejected: {cland.Message}");

        double goAroundAltitude = double.NaN;
        bool notAlignedVoiced = false;
        // Pin the go-around to THIS gate (and its phraseology), not one of the other
        // short-final go-around paths: the transmission names the assigned runway. Warnings
        // are drained from the aircraft each tick, so listen on the engine's fan-out.
        engine.WarningEmitted += (callsign, warning) =>
            notAlignedVoiced |= (callsign == Callsign) && warning.Contains("not lined up with runway 28L", StringComparison.OrdinalIgnoreCase);

        for (int t = 0; t < 120; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            if (aircraft.Phases?.CurrentPhase is GoAroundPhase && double.IsNaN(goAroundAltitude))
            {
                goAroundAltitude = aircraft.Altitude;
                output.WriteLine($"t=+{t}: go-around at {aircraft.Altitude:F0} ft (notAlignedVoiced={notAlignedVoiced})");
                break;
            }

            Assert.False(
                aircraft.IsOnGround,
                "Aircraft landed despite being far off the assigned runway's centerline — the lateral gate never fired"
            );
        }

        Assert.False(double.IsNaN(goAroundAltitude), "No go-around triggered from the misaligned short final");

        // The lateral gate must fire from FinalApproach with altitude in hand — not the
        // LandingPhase stabilization gate at flare height.
        Assert.True(
            goAroundAltitude > 150,
            $"Go-around came at {goAroundAltitude:F0} ft — that's the flare-height stabilization gate, not a short-final alignment check"
        );
        Assert.True(notAlignedVoiced, "The go-around didn't carry the not-lined-up transmission naming runway 28L");
    }
}
