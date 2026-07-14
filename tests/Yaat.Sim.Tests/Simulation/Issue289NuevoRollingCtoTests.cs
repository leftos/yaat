using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #289: a C208 (PCM8679) on the NUEVO8 RNAV SID off OAK RWY 28L turned
/// direct to the first fix (SAPLY, a ~77° left turn) immediately after departure. Its flight
/// plan had been amended from NIMI6 to NUEVO8, and — the actual trigger — the takeoff clearance
/// was issued while it was still taxiing (a rolling CTO). The taxi-consumed InitialClimb dropped
/// the SID runway-transition heading legs (VD 278° -> VM 278° -> CF SAPLY 168°), so instead of
/// flying runway heading and awaiting vectors it navigated straight to SAPLY. After the fix it
/// flies the runway heading (~291° true / 278° magnetic).
///
/// Bundle: issue289-nuevo8-rolling-cto-recording.zip (trimmed to ~350s). The buggy left turn
/// completed by ~t=335, so replaying to t=340 and checking PCM8679's heading distinguishes the
/// fixed behavior (runway heading) from the bug (direct to SAPLY).
/// </summary>
[Collection("NavDbMutator")]
public class Issue289NuevoRollingCtoTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue289-nuevo8-rolling-cto-recording.zip";

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

    [Fact]
    public void Pcm8679_FliesRunwayHeading_NotDirectToSaply()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Replay past the point where the buggy left turn to SAPLY had completed (~t=335).
        engine.Replay(recording, 340);

        var ac = engine.FindAircraft("PCM8679");
        Assert.NotNull(ac);
        Assert.False(ac!.IsOnGround, "PCM8679 should be airborne on the NUEVO8 by t=340");

        double heading = ac.TrueHeading.Degrees;
        var nextFix = ac.Targets.NavigationRoute.Count > 0 ? ac.Targets.NavigationRoute[0].Name : "-";
        output.WriteLine($"PCM8679 t=340 airborne={!ac.IsOnGround} hdg={heading:F1} alt={ac.Altitude:F0} nextfix={nextFix}");

        // OAK 28L runway heading is ~291° true (the NUEVO8 VD/VM legs fly runway heading, 278° mag).
        // Pre-fix the aircraft turned ~77° left to ~211° true, navigating direct to SAPLY.
        Assert.True(
            heading is > 255.0 and < 325.0,
            $"PCM8679 was heading {heading:F0}° true at t=340 — expected to fly the NUEVO8 runway heading "
                + $"(~291° true) off 28L, not turn direct to SAPLY (~211° true). nextfix={nextFix}"
        );
    }
}
