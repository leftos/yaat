using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-bugs #3/#6: an aircraft taxiing toward a node that a faster, much-nearer aircraft
/// is about to clear was braked toward a stop for a crossing that is already clear by the time it
/// arrives. In the SFO recording, FFT2083 (on M1) was capped to ~4-8 kts yielding to JBU2435 (on M3)
/// for ~25 s, though JBU2435 reached the shared node (413) ~50 s before FFT2083 would.
///
/// The convergence resolver now skips the slowdown when the nearer aircraft will clear the shared
/// node well before the farther one arrives (an ETA gate).
///
/// Recording: S1-SFO-4 (ZOA). JBU2435 taxis M3→M2 at t≈1567; FFT2083 is on M1 heading to parking.
/// </summary>
public class Issue172ParallelPassTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip";

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
    public void Fft2083_NotBrakedForJbu2435_ClearingSharedNodeFirst()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        engine.Replay(recording, 1555);

        int yieldTicksWhileJbuClearingFirst = 0;
        int observed = 0;
        for (int t = 1555; t <= 1615; t++)
        {
            engine.ReplayOneSecond();
            var fft = engine.FindAircraft("FFT2083");
            var jbu = engine.FindAircraft("JBU2435");
            if (fft is null || jbu is null)
            {
                continue;
            }

            var rF = fft.Ground.AssignedTaxiRoute;
            var rJ = jbu.Ground.AssignedTaxiRoute;
            if (rF is null || rJ is null)
            {
                continue;
            }

            int? shared = GroundConflictDetector.FindSharedUpcomingNode(rF, rJ);
            if (shared is null || !layout.Nodes.TryGetValue(shared.Value, out var node))
            {
                continue;
            }

            double fftToShared = GeoMath.DistanceNm(fft.Position, node.Position) * 6076.0;
            double jbuToShared = GeoMath.DistanceNm(jbu.Position, node.Position) * 6076.0;

            // The case the fix targets: JBU is moving and is much nearer the shared node than FFT, so
            // it clears well before FFT arrives. FFT must not be braked to yield in that situation.
            if (jbu.IndicatedAirspeed > 12.0 && jbuToShared < fftToShared - 300.0)
            {
                observed++;
                bool braked = fft.Ground.AutoYieldTarget == "JBU2435";
                if (braked)
                {
                    yieldTicksWhileJbuClearingFirst++;
                    output.WriteLine(
                        $"t={t} FFT braked: yield={fft.Ground.AutoYieldTarget} lim={fft.Ground.SpeedLimit:F1} "
                            + $"fft->{fftToShared:F0}ft jbu->{jbuToShared:F0}ft jbuIas={jbu.IndicatedAirspeed:F1}"
                    );
                }
            }
        }

        output.WriteLine($"observed={observed} yieldTicksWhileJbuClearingFirst={yieldTicksWhileJbuClearingFirst}");
        Assert.True(observed > 0, "Did not observe the JBU-clears-first geometry — recording/window changed");
        Assert.Equal(0, yieldTicksWhileJbuClearingFirst);
    }
}
