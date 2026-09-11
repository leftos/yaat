using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E verification for GitHub issue #429 ("aircraft blow through the localizer at KFAT").
///
/// Fixture: <c>TestData/issue429-fat-approaches-recording.zip</c> — a ZOA session at KFAT where three
/// arrivals were vectored <c>FH 260</c> and then cleared for the ILS Y runway 29R (<c>I29RY</c>, final
/// approach course ≈293° magnetic / ≈306° true). ASA1054's CAPP is recorded at t=3017.
///
/// What was wrong: a 30°-off-the-runway-number vector is exactly what the intercept phase is built to
/// accept, but both of its leniencies were dead. The runway-number heading came from a regex anchored at
/// the end of the approach id, which a vNAS multiple-approach suffix ("I29RY" = ILS Y 29R) defeats, so the
/// comparison fell back to the true final approach course and the magnetic variation alone (≈13°E) pushed
/// the cut to 32.9°; and the controller's assigned heading, the third comparison, had already been cleared
/// by the CAPP handler before the phase was installed. The aircraft crossed the localizer at t≈3105 and the
/// approach clearance was dropped. The instructor's own correction is recorded at t≈3115, so this test
/// stops before it.
/// </summary>
public class Issue429FatApproachesTests
{
    private const string RecordingPath = "TestData/issue429-fat-approaches-recording.zip";
    private const string Callsign = "ASA1054";
    private const int ReplayStartSeconds = 3010;
    private const int WatchEndSeconds = 3100;
    private const string LandingCallsign = "N200WM";
    private const int LandingReplayStartSeconds = 1390;
    private const int LandingWatchSeconds = 240;
    private const int MaxStandstillSecondsInExit = 15;
    private const string VectoredCallsign = "UAL1486";
    private const int VectoredStartSeconds = 2170;
    private const int VectoredWatchEndSeconds = 2200;

    private readonly ITestOutputHelper _output;

    public Issue429FatApproachesTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin navdata singletons before any test body runs (static-singleton race).
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? BuildEngine()
    {
        return BuildEngineWith(new TestAirportGroundData());
    }

    private SimulationEngine? BuildEngineWith(IAirportGroundData groundData)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).EnableCategory("InterceptCoursePhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// ASA1054 is on the recorded 260 vector when its CAPP fires; it must join the final approach course
    /// instead of reporting through the localizer.
    /// </summary>
    [Fact]
    public void Asa1054_Capp_OnHeading260_JoinsFinal()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // The spine drains PendingWarnings/PendingNotifications every second, so the refusal is only
        // observable on the engine's own events.
        var refusals = new List<string>();
        engine.WarningEmitted += (callsign, warning) => Record(refusals, callsign, warning);
        engine.TerminalEntryEmitted += entry => Record(refusals, entry.Callsign, entry.Message);

        engine.Replay(recording, ReplayStartSeconds);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        string approachId = aircraft.Phases?.ActiveApproach?.ApproachId ?? "none";
        string flown = $"hdg={aircraft.TrueHeading.Degrees:F0} alt={aircraft.Altitude:F0} approach={approachId}";
        _output.WriteLine($"t={ReplayStartSeconds}: {flown} phases={FormatPhases(aircraft)}");

        for (int t = ReplayStartSeconds + 1; t <= WatchEndSeconds; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            if (t % 20 == 0)
            {
                _output.WriteLine($"  t={t}: hdg={aircraft.TrueHeading.Degrees:F0} alt={aircraft.Altitude:F0} phases={FormatPhases(aircraft)}");
            }
        }

        _output.WriteLine($"t={WatchEndSeconds}: phases={FormatPhases(aircraft)} approach={aircraft.Phases?.ActiveApproach?.ApproachId ?? "none"}");

        Assert.True(refusals.Count == 0, $"{Callsign} refused the intercept: {string.Join("; ", refusals)}");
        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.CurrentPhase);
    }

    /// <summary>
    /// UAL1486 was given "cross SANGO at 11,000" and then vectored off it with FH 225. The vector drops the
    /// route, so the crossing-restriction rate the descent planner had computed (-732 fpm) must be released:
    /// the aircraft resumes its A319 profile descent toward the assigned altitude instead of mushing down at
    /// the stale rate through DM/EXP.
    /// </summary>
    [Fact]
    public void Ual1486_VectorOffCrossingRestriction_ResumesProfileDescent()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, VectoredStartSeconds);

        var aircraft = engine.FindAircraft(VectoredCallsign);
        Assert.NotNull(aircraft);
        string vertical = $"alt={aircraft.Altitude:F0} vs={aircraft.VerticalSpeed:F0} target={aircraft.Targets.TargetAltitude}";
        _output.WriteLine($"t={VectoredStartSeconds}: {vertical} route={aircraft.Targets.NavigationRoute.Count}");

        for (int t = VectoredStartSeconds + 1; t <= VectoredWatchEndSeconds; t++)
        {
            engine.ReplayOneSecond();
        }

        aircraft = engine.FindAircraft(VectoredCallsign);
        Assert.NotNull(aircraft);
        _output.WriteLine(
            $"t={VectoredWatchEndSeconds}: alt={aircraft.Altitude:F0} vs={aircraft.VerticalSpeed:F0} target={aircraft.Targets.TargetAltitude}"
        );

        Assert.True(
            Math.Abs(aircraft.VerticalSpeed) > 1500,
            $"{VectoredCallsign} is still flying the stale crossing-restriction rate: {aircraft.VerticalSpeed:F0} fpm at {aircraft.Altitude:F0} ft"
        );
    }

    /// <summary>
    /// KFAT has no ground layout, so a landed arrival has no exit to taxi to. N200WM finished its rollout,
    /// entered <see cref="RunwayExitPhase"/> and stayed there at a standstill for the rest of the session:
    /// the queued <see cref="HoldingAfterExitPhase"/> never started, so the engine's auto-delete (in
    /// <c>OnLanding</c> mode since t=1320) never matched and seven landings piled up on the runway.
    /// </summary>
    [Fact]
    public void N200wm_LandsAtLayoutlessFat_IsAutoDeleted()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngineWith(new NullGroundData());
        if (recording is null || engine is null)
        {
            _output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, LandingReplayStartSeconds);

        var aircraft = engine.World.FindAircraft(LandingCallsign);
        Assert.NotNull(aircraft);
        _output.WriteLine(
            $"t={LandingReplayStartSeconds}: autoDelete={engine.Scenario?.EffectiveAutoDeleteMode ?? "none"} "
                + $"layout={(engine.World.GroundLayout is null ? "none" : "present")} phases={FormatPhases(aircraft)}"
        );
        Assert.Equal("OnLanding", engine.Scenario?.EffectiveAutoDeleteMode);

        var standstillRun = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int worstStandstill = 0;
        string worstCallsign = "none";

        for (int t = LandingReplayStartSeconds + 1; t <= LandingReplayStartSeconds + LandingWatchSeconds; t++)
        {
            engine.ReplayOneSecond();

            foreach (var ac in engine.World.GetSnapshot())
            {
                bool stuck = (ac.Phases?.CurrentPhase is RunwayExitPhase) && (ac.GroundSpeed < 1);
                int run = stuck ? standstillRun.GetValueOrDefault(ac.Callsign) + 1 : 0;
                standstillRun[ac.Callsign] = run;
                if (run > worstStandstill)
                {
                    worstStandstill = run;
                    worstCallsign = ac.Callsign;
                }
            }
        }

        _output.WriteLine(
            $"t={LandingReplayStartSeconds + LandingWatchSeconds}: {LandingCallsign}="
                + $"{(engine.World.FindAircraft(LandingCallsign) is { } left ? FormatPhases(left) : "deleted")}, "
                + $"worst standstill in the exit phase: {worstCallsign} {worstStandstill}s"
        );

        Assert.Null(engine.World.FindAircraft(LandingCallsign));
        Assert.True(worstStandstill <= MaxStandstillSecondsInExit, $"{worstCallsign} sat in RunwayExitPhase at a standstill for {worstStandstill}s");
    }

    private static void Record(List<string> refusals, string callsign, string message)
    {
        if (callsign.Equals(Callsign, StringComparison.OrdinalIgnoreCase) && message.Contains("passing through", StringComparison.OrdinalIgnoreCase))
        {
            refusals.Add(message);
        }
    }

    /// <summary>
    /// The recorded session had no ground layout for KFAT: layouts come from the vNAS airport map, and the
    /// harness's <c>TestData/fat.geojson</c> stands in for a map the client never had. Replaying the recording
    /// against this provider reproduces the recorded condition — an airport the engine has no ground data for.
    /// </summary>
    private static string FormatPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return "null";
        }

        return string.Join(", ", aircraft.Phases.Phases.Select(p => $"{p.Name}({p.Status})"));
    }
}
