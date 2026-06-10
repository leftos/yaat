using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #196: an instructor cannot keep a departure at an
/// assigned speed because YAAT rejects <c>SPD</c> commands within 5 nm of the
/// runway. The 7110.65 §5-7-1.b.4 "no speed inside 5 nm final" rule was gated on
/// <c>AssignedRunway != null</c> + distance-to-threshold, but AssignedRunway is
/// also the *departure* runway, so departing aircraft within 5 nm of the field
/// were wrongly blocked.
///
/// Recording: (OTS) COSTC-N | ZDV / KCOS. N50CD = SF50 Cirrus Vision Jet, IFR
/// departure off 35R to KIAH (preset <c>AT 7500 SPD 180</c>). In the recording
/// the controller's <c>SPD 180</c> at t=195 and t=226 produced no AssignedSpeed
/// change (silently rejected); only <c>SPDN</c> (force-teleport) worked, and the
/// over-broad AutoCancelSpeedAtFinal wiped even that within a tick.
/// </summary>
public class Issue196SpeedAssignmentTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue196-speed-5nm-departure-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N50CD";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

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

    private static double? DistanceToAssignedRunwayNm(AircraftState ac)
    {
        if (ac.Phases?.AssignedRunway is not { } rwy)
        {
            return null;
        }

        return GeoMath.DistanceNm(ac.Position, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude));
    }

    /// <summary>
    /// The bug: a departing SF50 in InitialClimb within 5 nm of its departure
    /// runway has plain <c>SPD</c> rejected. After the fix the command is
    /// accepted and the target sticks (not wiped by AutoCancelSpeedAtFinal).
    /// </summary>
    [Fact]
    public void N50CD_Departure_AcceptsSpeedWithin5nm()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // t=180: N50CD is airborne in initial climb ~4.3 nm from the 35R threshold
        // (it crosses the 5 nm gate boundary around t=195).
        engine.Replay(recording, 180);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // Confirm the bug's preconditions: airborne departure, within 5 nm of 35R.
        double? dist = DistanceToAssignedRunwayNm(aircraft);
        output.WriteLine($"setup: phase={aircraft.Phases?.CurrentPhase?.Name} dist={dist?.ToString("F2") ?? "n/a"} " + $"alt={aircraft.Altitude:F0}");
        Assert.False(aircraft.IsOnGround);
        Assert.NotNull(dist);
        Assert.True(dist <= 5.0, $"Expected N50CD within 5 nm of 35R; was {dist:F2}");

        var result = engine.SendCommand(Callsign, "SPD 180");
        output.WriteLine($"SPD 180 -> Success={result.Success} Message={result.Message}");
        Assert.True(result.Success, $"SPD 180 to a departure should be accepted; got: {result.Message}");

        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.Equal(180, aircraft.Targets.TargetSpeed);
        Assert.Equal(180, aircraft.Targets.AssignedSpeed);

        // AutoCancelSpeedAtFinal must not wipe a departure's explicit speed assignment
        // (the over-broad gate cleared HasExplicitSpeedCommand every tick within 5nm).
        for (int i = 0; i < 15; i++)
        {
            engine.TickOneSecond();
        }
        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand, "Departure's explicit speed must persist within 5nm");
        Assert.Equal(180, aircraft.Targets.AssignedSpeed);
    }
}
