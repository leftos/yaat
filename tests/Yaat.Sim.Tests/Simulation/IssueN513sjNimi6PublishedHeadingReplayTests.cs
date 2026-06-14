using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the N513SJ NIMI6 prior-cycle fix. The bug bundle (S2-OAK-5, N513SJ filed "NIMI6 OAK V6 SAC",
/// bare CTO off KOAK 28R) was recorded on a production server whose CIFP cache only reached one cycle back
/// — already a cycle whose CIFP omitted NIMITZ — so the SID degraded to a runway-heading hold.
///
/// Under the test harness the supplementary CIFP still carries NIMI5, so the recency-capped chain recovers
/// NIMITZ's published initial vectors heading (315° magnetic). This test confirms the recording's exact
/// flow (taxi → LUAW → bare CTO) lands the InitialClimb on the published heading rather than the degraded
/// runway-heading hold, end to end.
/// </summary>
public class IssueN513sjNimi6PublishedHeadingReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/nimi6-rv-sid-heading-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Replay_N513SJ_InitialClimbAppliesPublishedHeading315()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        // Only meaningful when the harness can actually resolve NIMITZ from a prior cycle (the bundled
        // supplementary still carries NIMI5). If the env lacks it, skip rather than assert a false negative.
        if (TestVnasData.NavigationDb!.GetSid("KOAK", "NIMI6") is null)
        {
            output.WriteLine("NIMI not resolvable in this environment, skipping");
            return;
        }

        // Bare CTO is at t=2821; replay just past it.
        engine.Replay(recording, 2835);
        var ac = engine.FindAircraft("N513SJ");
        if (ac is null)
        {
            output.WriteLine("N513SJ not present at t=2835, skipping");
            return;
        }

        var climb = ac.Phases?.Phases.OfType<InitialClimbPhase>().FirstOrDefault();
        Assert.NotNull(climb);
        var dto = (InitialClimbPhaseDto)climb!.ToSnapshot();

        Assert.False(
            dto.RvSidHoldRunwayHeading,
            "NIMI6 should resolve its published 315 heading from a cached prior cycle, not degrade to runway heading."
        );
        Assert.NotNull(dto.SidDepartureHeadingMagnetic);
        Assert.Equal(315.0, dto.SidDepartureHeadingMagnetic!.Value, 1.0);
    }
}
