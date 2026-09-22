using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The field case from the SFO GC 28/01 bundle: SKW3396 (E75L) parked at gate D2 is told <c>PUSH $5A</c> with
/// SKW3398 (E75L) parked at the adjacent gate D1, 133 ft away. A planner blind to parked aircraft picked a template
/// that swung to 23.4 ft of the neighbour, under the 24.5 ft floor its outline sweep holds a tow to; the conflict
/// detector then dead-stopped the tow 110 ft into the manoeuvre and it sat at 0 kt in <see cref="PushbackPhase"/>
/// from t=860 until the RPO gave up at t=993.
///
/// A tug move must either complete to the spot or be refused naming the neighbour it cannot clear — never be
/// accepted and then parked in the middle of the alley.
/// </summary>
[Collection("GroundConflictDebugSink")]
public class SfoPushIntoParkedNeighbourTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-gc-28-01-spot-lanes-recording.zip";

    /// <summary>The t of the recording to replay to: SKW3396 and SKW3398 are both parked and stationary.</summary>
    private const int ReplayToSeconds = 820;

    /// <summary>How long a tow may sit at a standstill inside <see cref="PushbackPhase"/> before it counts as stuck.</summary>
    private const int MaxStalledSeconds = 20;

    private const double StandstillKts = 0.5;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void PushToSpot5A_WithNeighbourAtD1_CompletesOrIsRefusedNamingIt()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayToSeconds);

        AircraftState? subject = engine.FindAircraft("SKW3396");
        AircraftState? neighbour = engine.FindAircraft("SKW3398");
        Assert.NotNull(subject);
        Assert.NotNull(neighbour);
        Assert.IsType<AtParkingPhase>(subject.Phases?.CurrentPhase);
        Assert.IsType<AtParkingPhase>(neighbour.Phases?.CurrentPhase);

        AirportGroundLayout? layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        GroundNode? spot5A = layout.FindSpotNodeByName("5A");
        Assert.NotNull(spot5A);

        double neighbourFt = FeetBetween(subject.Position, neighbour.Position);
        double startClearanceFt = GroundOutline.ClearanceBetween(subject, aTowedNoseFirst: false, neighbour);
        output.WriteLine(
            $"SKW3396 at {subject.Position.Lat:F6}/{subject.Position.Lon:F6} hdg {subject.TrueHeading.Degrees:F1}°; "
                + $"SKW3398 at {neighbour.Position.Lat:F6}/{neighbour.Position.Lon:F6} hdg {neighbour.TrueHeading.Degrees:F1}°; "
                + $"centroids {neighbourFt:F0} ft apart, outlines {startClearanceFt:F1} ft"
        );

        var outlineLines = new List<string>();
        CommandResult result;
        try
        {
            GroundConflictDetector.DebugSink = line =>
            {
                if (line.Contains("[Outline]", StringComparison.Ordinal))
                {
                    outlineLines.Add(line.Trim());
                }
            };
            result = engine.SendCommand("SKW3396", "PUSH $5A");
            output.WriteLine($"PUSH $5A -> success={result.Success}, message={result.Message}");

            if (!result.Success)
            {
                Assert.Contains("SKW3398", result.Message, StringComparison.Ordinal);
                return;
            }

            RunAndAssert(engine, spot5A, outlineLines);
        }
        finally
        {
            GroundConflictDetector.DebugSink = null;
            foreach (string line in outlineLines.Distinct().Take(40))
            {
                output.WriteLine(line);
            }
        }
    }

    /// <summary>Ticks the accepted push and asserts it neither stalls nor stops short of the spot.</summary>
    private void RunAndAssert(SimulationEngine engine, GroundNode spot5A, List<string> outlineLines)
    {
        int stalledSeconds = 0;
        int worstStallSeconds = 0;
        bool reachedSpot = false;
        AircraftState? ac = null;
        output.WriteLine($"{"t", 4} {"gs", 6} {"distSpot", 9} {"push", 5} {"nose", 5} {"phase", -24}");

        // A push that has to turn the nose around before pulling onto the lane is a three-point turn of several
        // hundred feet at tug speeds, and it carries a 5 s dwell at each reversal.
        for (int tick = 1; tick <= 300; tick++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SKW3396");
            Assert.NotNull(ac);

            bool stalled = (ac.Phases?.CurrentPhase is PushbackPhase) && (ac.GroundSpeed < StandstillKts);
            stalledSeconds = stalled ? stalledSeconds + 1 : 0;
            worstStallSeconds = Math.Max(worstStallSeconds, stalledSeconds);

            if ((tick % 5 == 0) || (ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase))
            {
                output.WriteLine(
                    $"{tick, 4} {ac.GroundSpeed, 6:F2} {FeetBetween(ac.Position, spot5A.Position), 9:F0} "
                        + $"{ac.Ground.PushbackTrueHeading?.Degrees ?? -1, 5:F0} {ac.TrueHeading.Degrees, 5:F0} "
                        + $"{ac.Phases?.CurrentPhase?.Name ?? "null", -24}"
                );
            }

            if (ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase)
            {
                reachedSpot = true;
                break;
            }
        }

        Assert.NotNull(ac);
        double finalFt = FeetBetween(ac.Position, spot5A.Position);
        output.WriteLine(
            $"worstStall={worstStallSeconds}s reachedSpot={reachedSpot} finalDistToSpot={finalFt:F0}ft outlineLines={outlineLines.Count}"
        );

        Assert.True(
            worstStallSeconds <= MaxStalledSeconds,
            $"the tow stood still inside PushbackPhase for {worstStallSeconds}s — an accepted push must not be dead-stopped mid-manoeuvre"
        );
        Assert.True(reachedSpot, $"the push should complete to HoldingAfterPushbackPhase, got: {ac.Phases?.CurrentPhase?.Name ?? "null"}");
        double halfLenFt = (FaaAircraftDatabase.Get(ac.AircraftType)?.LengthFt ?? 110.0) / 2.0;
        Assert.True(
            finalFt <= halfLenFt + 25.0,
            $"the push should end nose-at-spot 5A (centroid ~{halfLenFt:F0}ft back), but is {finalFt:F0}ft away"
        );
    }

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a.Lat, a.Lon, b.Lat, b.Lon) * GeoMath.FeetPerNm;
}
