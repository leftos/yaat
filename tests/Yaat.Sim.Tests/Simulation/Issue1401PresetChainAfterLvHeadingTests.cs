using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

[Collection("NavDbMutator")]
public class Issue1401PresetChainAfterLvHeadingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue1401-mry-lv-heading-chain-stall-recording.zip";

    [Fact]
    public void PresetChainAfterLvHeading_RunsWithoutWaitingForTheSeparatePresetClimb()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        var engine = new SimulationEngine(
            RecordingGroundData.Evergreen("KMRY has no layout; the fault is the airborne queue after the departure"),
            PushPlanStore.CreateShipped()
        );
        engine.Replay(recording, 185);
        AircraftState? ac = engine.FindAircraft("EJA802");
        Assert.NotNull(ac);

        int? jradoAt = null;
        int? aptAt = null;
        int? interceptAt = null;
        int? shoeyAt = null;
        for (int t = 186; t <= 379; t++)
        {
            engine.ReplayOneSecond();
            jradoAt ??= ac.Queue.Blocks.Any(b => (b.Description == "JRADO SNS264") && b.IsApplied) ? t : null;
            aptAt ??= ac.Queue.Blocks.Any(b => (b.Description == "at 5000ft: APT SJC") && b.IsApplied) ? t : null;
            interceptAt ??= ac.Queue.Blocks.Any(b => (b.Trigger?.Type == BlockTriggerType.InterceptRadial) && b.IsApplied) ? t : null;
            shoeyAt ??= ac.Targets.NavigationRoute.Any(n => n.Name == "SHOEY") ? t : null;
        }

        output.WriteLine($"JRADO at t={jradoAt}, APT SJC at t={aptAt}, alt={ac.Altitude:F0}");
        // Heading 329 is held from t=210 and WAIT 10 follows it; the separate CM 7000 is still climbing (6865 ft at t=379).
        Assert.True(jradoAt is <= 230, $"JRADO SNS264 applied at t={jradoAt?.ToString() ?? "never"}");
        Assert.NotNull(aptAt); // the AT 5000 preset fires once the aircraft passes 5000 ft (about t=288)
        Assert.True(
            shoeyAt is null || (interceptAt is not null && interceptAt <= shoeyAt),
            $"DCT SHOEY applied at t={shoeyAt}, before the SNS 264 intercept (t={interceptAt?.ToString() ?? "never"})"
        );
    }
}
