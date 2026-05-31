using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the OAK north-field taxi-spin bug surfaced from the
/// S1-OAK-P (A) practical-exam bundle.
///
/// Two north-field aircraft fail to taxi when issued explicit TAXI commands:
///
///   EDG320 — Parking SIG4 (node 641, hdg 110), TAXI D C B HS 28R at t=28s.
///   TWY801 — Parking GA3  (node 621, hdg 290), TAXI C B HS 28R at t=44s.
///
/// LayoutInspector confirms north-field parking edges are fillet pairs:
///   SIG4 (641): RAMP -> 1332 (forward Nodes[0]=641) and 1333 (reverse Nodes[0]=1332).
///   GA3  (621): RAMP -> 1224 (forward Nodes[0]=621) and 1222 (reverse Nodes[0]=1222).
///
/// User TAXI commands flow through <c>TaxiPathfinder.ResolveExplicitPath</c>.
/// This recording replays the north-field ramp departures end-to-end and asserts
/// the aircraft reaches its hold-short without orbiting the ramp.
/// </summary>
public class OakNorthFieldTaxiSpinTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-northfield-taxi-spinning-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Heading-rotation guard: after the TAXI command fires, neither aircraft
    /// should accumulate more than 320° of total absolute heading rotation
    /// over 30 s, AND signed rotation must stay within 200°. The natural
    /// taxi-out from a north-field parking spot requires &lt; 200° signed
    /// (short-way ramp connector + arc onto the named taxiway). The bug
    /// produces a rotating orbit instead — typical accumulated absolute
    /// rotation is &gt; 600° while the aircraft stays stationary near the
    /// ramp.
    /// </summary>
    [Theory]
    [InlineData("EDG320", 28, 320.0, 200.0)]
    [InlineData("TWY801", 44, 320.0, 200.0)]
    public void TaxiOut_DoesNotSpinNearlyFullCircle(string callsign, int taxiCommandSeconds, double maxCumulativeAbsDeg, double maxAbsSignedDeg)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay through the moment the TAXI command fires for this aircraft
        // (actions are the canonical TAXI commands logged in the bundle).
        engine.Replay(recording, taxiCommandSeconds);

        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);

        double prevHdg = ac.TrueHeading.Degrees;
        double cumulativeAbs = 0;
        double cumulativeSigned = 0;

        const int observeSeconds = 30;
        for (int t = 1; t <= observeSeconds; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);

            double curHdg = ac.TrueHeading.Degrees;
            double delta = (((curHdg - prevHdg) + 540.0) % 360.0) - 180.0;
            cumulativeAbs += Math.Abs(delta);
            cumulativeSigned += delta;
            prevHdg = curHdg;
        }

        output.WriteLine(
            $"{callsign}: cumulativeAbs={cumulativeAbs:F0}deg cumulativeSigned={cumulativeSigned:F0}deg "
                + $"(max abs {maxCumulativeAbsDeg:F0}, max signed {maxAbsSignedDeg:F0})"
        );

        Assert.True(
            cumulativeAbs <= maxCumulativeAbsDeg,
            $"{callsign} accumulated {cumulativeAbs:F0}deg of rotation in {observeSeconds}s — expected <= {maxCumulativeAbsDeg:F0}deg. "
                + "Spinning at the ramp-connector fillet pair compounds rotation; a clean taxi-out should stay well under this."
        );
        Assert.True(
            Math.Abs(cumulativeSigned) <= maxAbsSignedDeg,
            $"{callsign} accumulated {cumulativeSigned:F0}deg of *signed* rotation in {observeSeconds}s — expected |signed| <= {maxAbsSignedDeg:F0}deg."
        );
    }

    /// <summary>
    /// Forward-progress guard: 60 s after the TAXI command fires, the
    /// aircraft should have displaced &gt;= 500 ft from its position at the
    /// moment of the TAXI command. The bug produces &lt; 100 ft of net
    /// displacement (aircraft orbits the ramp).
    /// </summary>
    [Theory]
    [InlineData("EDG320", 28)]
    [InlineData("TWY801", 44)]
    public void TaxiOut_MakesForwardProgress(string callsign, int taxiCommandSeconds)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, taxiCommandSeconds);
        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);
        var startPos = ac.Position;

        const int observeSeconds = 60;
        for (int t = 1; t <= observeSeconds; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);
        }

        double movedFt = GeoMath.DistanceNm(startPos, ac!.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"{callsign} after {observeSeconds}s: moved={movedFt:F0}ft hdg={ac.TrueHeading.Degrees:F0} "
                + $"ias={ac.IndicatedAirspeed:F1} segIdx={ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}"
        );

        const double minProgressFt = 500.0;
        Assert.True(
            movedFt >= minProgressFt,
            $"{callsign} only moved {movedFt:F0} ft in {observeSeconds}s after TAXI - expected >= {minProgressFt:F0} ft. "
                + "The aircraft is orbiting the ramp-connector fillet pair near its parking spot."
        );
    }
}
