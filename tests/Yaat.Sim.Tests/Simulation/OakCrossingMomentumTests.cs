using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Runway-crossing momentum (7110.65 §3-7-2 "cross and continue without delay"):
/// once an aircraft is established in the crossing it must NOT brake toward a
/// stop on the runway or at the far-side hold-short before the crossing
/// completes. The "land 28L → cross 28R → hold on C" workflow (N427MX, a piston,
/// crossing speed 12 kt) is the canonical case. Before the crossing-speed floor,
/// the aircraft dipped to the off-centerline re-acquire speed (~5 kt) mid-runway —
/// the runway-incursion anti-pattern this guards against.
/// </summary>
[Collection("Acceptance")]
public class OakCrossingMomentumTests(ITestOutputHelper output)
{
    [Fact]
    public void AfterRes_MaintainsCrossingMomentum()
    {
        OakCrossingMomentum.AssertMomentum(output, FilletMode.Standard);
    }
}

internal static class OakCrossingMomentum
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";
    private const string Callsign = "N427MX";
    private const int ResCommandTime = 1293;

    // Once moving above this, the aircraft is "established" in the crossing.
    private const double EstablishedKts = 8.0;

    public static void AssertMomentum(ITestOutputHelper output, FilletMode mode)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: NavigationDb not initialized");
            return;
        }

        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            output.WriteLine("SKIP: recording not present");
            return;
        }

        var groundData = new TestAirportGroundData(mode);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);

        engine.Replay(recording, ResCommandTime); // through RES

        bool established = false;
        bool sawCrossing = false;
        double minWhileEstablished = double.MaxValue;
        for (int t = ResCommandTime + 1; t <= 1360; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }
            bool crossing = ac.Phases?.CurrentPhase is CrossingRunwayPhase;
            if (crossing)
            {
                sawCrossing = true;
                if (ac.GroundSpeed >= EstablishedKts)
                {
                    established = true;
                }
                if (established)
                {
                    minWhileEstablished = Math.Min(minWhileEstablished, ac.GroundSpeed);
                }
            }
            output.WriteLine($"t={t} phase={ac.Phases?.CurrentPhase?.Name, -20} gs={ac.GroundSpeed, 5:F1} crossing={crossing} est={established}");
            if (!crossing && established)
            {
                break; // crossing finished
            }
        }

        Assert.True(sawCrossing, "Expected aircraft to enter CrossingRunwayPhase after RES");
        Assert.True(established, "Expected aircraft to get established (>=8 kt) during the crossing");
        output.WriteLine($"min gs while established in crossing ({mode}): {minWhileEstablished:F1} kt");
        Assert.True(
            minWhileEstablished >= EstablishedKts,
            $"[{mode}] aircraft braked to {minWhileEstablished:F1} kt mid-crossing — expected to maintain >= {EstablishedKts:F0} kt "
                + "(7110.65 §3-7-2 cross and continue without delay)."
        );
    }
}
