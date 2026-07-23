using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-case (JBU577 taxi spin). After landing at SFO, JBU577 was given
/// <c>TAXI G B HS B</c> (t=444) + <c>CROSS</c> (t=447) to cross RWY 01L/19R on taxiway G and hold
/// short of B just beyond. On G the nodes run NW: 867 (runway far-side hold-short) -> 1398 (B's join,
/// ~74 ft NW) -> 155 (G/B junction). 867->1398 is shorter than the aircraft, so it cannot be both fully
/// clear of the runway and short of B.
///
/// Bug (verified by replay): the crossing carries the aircraft ½ length past 867 and the taxiway
/// hold-short stop is offset <c>aircraftLength + 30 ft</c> behind node 1398 — which lands behind the
/// runway. The navigator then reverses ~180° (crossing heading ≈298° -> ≈118°) and drives ~187 ft back
/// SE toward the runway to reach it, holding short facing backward. Desired: stop at B's hold line as it
/// arrives, nose at the line with the tail over the runway bars, NO 180° reversal, NO backward drive.
/// (Representing the "runway not clear" state and warning the controller are work items W2/W3.)
///
/// Replay window: the route is extended by <c>TAXI B M1 Y @B5</c> at t=514, so recorded commands are
/// replayed only up to t=513 via faithful <see cref="SimulationEngine.ReplayOneSecond"/> (which applies
/// the recorded CROSS at t=447 — <see cref="SimulationEngine.TickOneSecond"/> would drop it). Past the
/// window the sim keeps ticking physics-only (<see cref="SimulationEngine.TickOneSecond"/>, no further
/// recorded commands) until the aircraft settles holding short: exit/taxi geometry changes (e.g. the
/// widened high-speed-exit fillets) legitimately shift the crossing a few seconds past the recording's
/// cutoff, and the invariants under test are about HOW it crosses and stops, not when.
/// Recording: issue172-sfo-taxiing-recording (ZOA/SFO).
/// </summary>
public class Issue172Jbu577TaxiSpinTests(ITestOutputHelper output)
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

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .EnableCategory("RunwayExitPhase", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Jbu577_CrossesAndHoldsShortOfB_WithoutReversing()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);

        // Stop replaying before TAXI B M1 Y @B5 (t=514) extends the route — assert only the
        // TAXI G B HS B crossing + hold-short-of-B behavior. Beyond the window, tick physics
        // only (bounded) so the crossing completes and the aircraft settles at B's hold line.
        const int WindowEnd = 513;
        const int PhysicsOnlyEnd = WindowEnd + 60;

        bool sawCrossing = false;
        bool sawHoldShort = false;
        double crossingHeading = double.NaN;
        bool haveRef = false;
        double refLat = 0;
        double refLon = 0;
        double maxForwardFt = double.MinValue;
        double worstRetreatFt = 0;
        int worstRetreatTick = -1;
        double maxHeadingDevDeg = 0;
        int worstHeadingTick = -1;
        string lastPhase = "(none)";
        double lastHeading = double.NaN;

        for (int t = 1; t <= PhysicsOnlyEnd; t++)
        {
            try
            {
                if (t <= WindowEnd)
                {
                    engine.ReplayOneSecond();
                }
                else
                {
                    engine.TickOneSecond();
                }
            }
            catch (InvalidOperationException ex)
            {
                Assert.Fail($"t={t}: orbit/invariant breach during JBU577 taxi — {ex.Message}");
            }

            var ac = engine.FindAircraft("JBU577");
            if (ac is null || t < 444)
            {
                continue;
            }

            string phase = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            double hdg = ac.TrueHeading.Degrees;
            var pos = ac.Position;
            lastPhase = phase;
            lastHeading = hdg;

            if (phase == "CrossingRunwayPhase")
            {
                sawCrossing = true;
                crossingHeading = hdg; // steady ≈298° across the runway
                if (!haveRef)
                {
                    haveRef = true;
                    refLat = pos.Lat;
                    refLon = pos.Lon;
                }
            }

            if (sawCrossing && !double.IsNaN(crossingHeading))
            {
                double dev = GeoMath.AbsBearingDifference(hdg, crossingHeading);
                if (dev > maxHeadingDevDeg)
                {
                    maxHeadingDevDeg = dev;
                    worstHeadingTick = t;
                }

                double fwdFt = ForwardProgressFt(refLat, refLon, pos.Lat, pos.Lon, crossingHeading);
                if (fwdFt > maxForwardFt)
                {
                    maxForwardFt = fwdFt;
                }
                double retreat = maxForwardFt - fwdFt;
                if (retreat > worstRetreatFt)
                {
                    worstRetreatFt = retreat;
                    worstRetreatTick = t;
                }
            }

            if (phase == "HoldingShortPhase")
            {
                sawHoldShort = true;
                if ((t > WindowEnd) && (ac.IndicatedAirspeed < 0.1))
                {
                    output.WriteLine($"t={t}: settled holding short of B — ending physics-only extension");
                    break;
                }
            }

            output.WriteLine($"t={t, 3} ias={ac.IndicatedAirspeed, 5:F1} pos=({pos.Lat:F6},{pos.Lon:F6}) hdg={hdg, 5:F1} phase={phase}");
        }

        Assert.True(sawCrossing, "JBU577 never entered CrossingRunwayPhase — expected it to cross RWY 01L/19R on taxiway G.");
        Assert.True(
            maxForwardFt > 50.0,
            $"JBU577 made no meaningful progress across the runway (maxForward={maxForwardFt:F0}ft) — degenerate, it did not actually cross."
        );

        // The spin: heading reverses ~180° from the crossing direction. A clean hold-short of B keeps the
        // aircraft facing ~NW (the crossing direction) as it stops nose-at-the-line.
        Assert.True(
            maxHeadingDevDeg < 135.0,
            $"JBU577 reversed ~180° after crossing — max heading deviation {maxHeadingDevDeg:F0}° from the crossing heading "
                + $"{crossingHeading:F0}° at t={worstHeadingTick}. It must hold short of B without turning back toward the runway."
        );

        // The spin's other signature: it drives backward (SE) toward the runway to reach a hold-short the
        // crossing carried it past. A correct stop arrives at B's hold line and stays — no retreat.
        Assert.True(
            worstRetreatFt < 50.0,
            $"JBU577 drove backward {worstRetreatFt:F0}ft toward the runway at t={worstRetreatTick} after crossing — "
                + "it must stop at B's hold line as it arrives, not reverse to a hold-short behind the runway."
        );

        Assert.True(sawHoldShort, "JBU577 never reached HoldingShortPhase — expected it to hold short of B.");
        Assert.Equal("HoldingShortPhase", lastPhase);
        Assert.True(
            GeoMath.AbsBearingDifference(lastHeading, crossingHeading) < 90.0,
            $"JBU577 settled facing {lastHeading:F0}° — holding short of B should keep it facing ~the crossing direction "
                + $"({crossingHeading:F0}° NW), not reversed back toward the runway."
        );
    }

    /// <summary>
    /// Signed distance (ft) from the reference point to <paramref name="lat"/>/<paramref name="lon"/>
    /// projected onto the crossing direction. Positive = forward (across the runway), negative = backward
    /// (retreating toward the runway).
    /// </summary>
    private static double ForwardProgressFt(double refLat, double refLon, double lat, double lon, double forwardHeadingDeg)
    {
        double distNm = GeoMath.DistanceNm(new LatLon(refLat, refLon), new LatLon(lat, lon));
        if (distNm < 1e-9)
        {
            return 0;
        }

        double bearing = GeoMath.BearingTo(new LatLon(refLat, refLon), new LatLon(lat, lon));
        double angle = GeoMath.AbsBearingDifference(bearing, forwardHeadingDeg);
        return distNm * GeoMath.FeetPerNm * Math.Cos(angle * Math.PI / 180.0);
    }
}
