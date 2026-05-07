using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: at t=287 the controller issued <c>CTO TRDCT OAK30NUM 014</c> to
/// N172SP (Cessna 172, departing OAK 28L). At t=383 the controller issued
/// <c>SALT</c>. The aircraft was airborne in InitialClimbPhase at ~660 ft,
/// climbing toward the bundled 1400 ft target, but the pilot reply built by
/// <see cref="PilotSayBuilder.BuildAltitude"/> reported the current altitude
/// only — no "Leaving X for 1,400" — because <c>Targets.AssignedAltitude</c>
/// was never populated by the CTO command path.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts.
/// </summary>
public class IssueSaltAfterCtoAltitudeTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/10797ffbbfea.zip";
    private const string Callsign = "N172SP";
    private const int SaltTime = 383;
    private const int ExpectedAssignedAltitude = 1400;

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

    /// <summary>
    /// After replaying to t=SaltTime the aircraft has already been cleared for
    /// takeoff with a bundled altitude of 1,400 ft and is climbing. The
    /// controller-assigned altitude must be visible on
    /// <see cref="Yaat.Sim.ControlTargets.AssignedAltitude"/> so SALT, the
    /// datablock, and SnapshotDiff all read a consistent value.
    /// </summary>
    [Fact]
    public void N172SP_AssignedAltitude_PopulatedFromCtoBundledAltitude()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, SaltTime);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"{Callsign} at t={SaltTime}: alt={aircraft.Altitude:F0} "
                + $"tgtAlt={aircraft.Targets.TargetAltitude} "
                + $"assignedAlt={aircraft.Targets.AssignedAltitude?.ToString() ?? "null"}"
        );

        Assert.Equal(ExpectedAssignedAltitude, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// SALT during initial climb after CTO must yield "Leaving {current} for
    /// 1,400". The aircraft is around 600–700 ft AGL at t=SaltTime.
    /// </summary>
    [Fact]
    public void N172SP_BuildAltitude_AnnouncesClimbToBundledAltitude()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, SaltTime);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var spoken = PilotSayBuilder.BuildAltitude(aircraft);
        output.WriteLine($"BuildAltitude → \"{spoken}\"");

        Assert.StartsWith("Leaving ", spoken, StringComparison.Ordinal);
        Assert.Contains("1,400", spoken, StringComparison.Ordinal);
    }
}
