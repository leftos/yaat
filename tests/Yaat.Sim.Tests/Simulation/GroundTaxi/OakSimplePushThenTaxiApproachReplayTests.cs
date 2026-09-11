using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// E2E replay of the recorded session behind
/// <see cref="OakSimplePushThenTaxiApproachTests"/>: DAL2150 is given a plain <c>PUSH</c> off OAK gate 15
/// at t=1113 and <c>TAXI U W RWY 30</c> at t=1246. The route starts at node 763, ~105 ft BEHIND the
/// aircraft's post-pushback rest position, and the aircraft is written onto that arc's start rather than
/// taxiing to it.
/// </summary>
public class OakSimplePushThenTaxiApproachReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-push-then-taxi-approach-recording.zip";
    private const string Callsign = "DAL2150";
    private const double TaxiSecond = 1246;
    private const int WindowSec = 40;

    /// <summary>Knots to feet per second.</summary>
    private const double FtPerSecPerKt = 1.6878;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>Every one-second step must be covered at the aircraft's own ground speed — no snap onto the route.</summary>
    [Fact]
    public void TaxiAfterSimplePush_NeverTeleports()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, TaxiSecond);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is not null && route.Segments.Count > 0)
        {
            var first = route.Segments[0];
            output.WriteLine(
                $"route at t={TaxiSecond}: {route.ToSummary()} ({route.Segments.Count} segments); "
                    + $"seg[0] {first.FromNodeId} -> {first.ToNodeId} on {first.TaxiwayName}"
            );
        }

        var prevPos = aircraft.Position;
        double prevIas = aircraft.IndicatedAirspeed;
        double worstExcessFt = double.NegativeInfinity;
        double worstStepFt = 0;
        double worstLimitFt = 0;
        int worstSecond = 0;
        double maxStepFt = 0;
        int maxStepSecond = 0;

        for (int t = 1; t <= WindowSec; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            double stepFt = GeoMath.DistanceNm(prevPos, aircraft.Position) * GeoMath.FeetPerNm;
            double limitFt = ((Math.Max(prevIas, aircraft.IndicatedAirspeed) + 1.0) * FtPerSecPerKt) + 2.0;
            output.WriteLine(
                $"t+{t, 2}: pos=({aircraft.Position.Lat:F7},{aircraft.Position.Lon:F7}) ias={aircraft.IndicatedAirspeed, 5:F1} "
                    + $"hdg={aircraft.TrueHeading.Degrees, 5:F0} seg={aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex ?? -1} "
                    + $"step={stepFt:F1} ft (limit {limitFt:F1})"
            );

            if (stepFt > maxStepFt)
            {
                maxStepFt = stepFt;
                maxStepSecond = t;
            }

            if ((stepFt - limitFt) > worstExcessFt)
            {
                worstExcessFt = stepFt - limitFt;
                worstStepFt = stepFt;
                worstLimitFt = limitFt;
                worstSecond = t;
            }

            prevPos = aircraft.Position;
            prevIas = aircraft.IndicatedAirspeed;
        }

        output.WriteLine($"max 1 s step: {maxStepFt:F1} ft at t+{maxStepSecond}; worst excess {worstExcessFt:F1} ft at t+{worstSecond}");

        Assert.True(
            worstExcessFt <= 0.0,
            $"{Callsign} covered {worstStepFt:F1} ft between t+{worstSecond - 1} and t+{worstSecond}, "
                + $"but its own speed allows only {worstLimitFt:F1} ft — the aircraft was teleported onto its route "
                + $"(largest step of the run: {maxStepFt:F1} ft at t+{maxStepSecond})"
        );
    }
}
