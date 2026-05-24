using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the N157LE "CTO MLT on RWY 30 got stuck lining up" bug from the
/// S2-OAK-3 (2) VFR Sequencing bundle.
///
/// N157LE (P28A) is taxied via <c>TAXI B W RWY 30</c> at t=2135 with response
/// "Taxi via B W W1 RWY 30 (cross 28R/10L)", then given <c>CTO MLT</c> at t=2511.
/// The variant walker at <see cref="TaxiVariantResolver.AutoExtendVariant"/>
/// previously constructed <see cref="WalkOptions"/> without <c>StopAtRunwayId</c>,
/// so the W1 walk ran past OAK node 41 (RunwayHoldShort for 30/12) to node 42
/// (TaxiwayIntersection on the runway centerline). The destination hold-short
/// landed on the centerline node; when LineUp engaged its geometry rejected
/// the on-centerline/divergent-heading pose as a Fault and the aircraft sat
/// indefinitely. User deleted at t=2663.
///
/// Two regressions are asserted:
///   1. After <c>TAXI B W RWY 30</c>, the taxi route terminates at a
///      RunwayHoldShort node for runway 30, not on the runway centerline.
///   2. After <c>CTO MLT</c> fires and enough wall-clock elapses, N157LE has
///      progressed past <see cref="LineUpPhase"/> (entered <see cref="TakeoffPhase"/>
///      or beyond, or become airborne). Pre-fix this never happens.
/// </summary>
public class Bug157leCtoMltStuckTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/157le-cto-mlt-runway-30-stuck-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N157LE";

    /// <summary>
    /// 1s after the <c>TAXI B W RWY 30</c> action at t=2135 — the route has
    /// been resolved and pinned onto N157LE.
    /// </summary>
    private const int AfterTaxiCommand = 2136;

    /// <summary>
    /// Far enough past <c>CTO MLT</c> at t=2511 that, with the fix, LineUp
    /// has run and TakeoffPhase has engaged. Pre-fix N157LE is stuck in
    /// LineUp.Faulted from t≈2530 onward.
    /// </summary>
    private const int AfterCtoCleared = 2600;

    private static SimulationEngine? BuildEngine(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void TaxiBwRwy30_RouteTerminatesAtHoldShort()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine(output);
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, AfterTaxiCommand);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var route = ac.Ground?.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.True(route.Segments.Count > 0, "Expected non-empty taxi route");

        int terminalId = route.Segments[^1].ToNodeId;

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("OAK");
        Assert.NotNull(layout);
        Assert.True(layout.Nodes.TryGetValue(terminalId, out var terminalNode), $"Terminal node #{terminalId} missing from OAK layout");

        output.WriteLine(
            $"Route terminal: node #{terminalId} type={terminalNode.Type} runway={terminalNode.RunwayId?.ToString() ?? "(none)"} "
                + $"at ({terminalNode.Position.Lat:F6}, {terminalNode.Position.Lon:F6})"
        );

        Assert.True(
            terminalNode.Type == GroundNodeType.RunwayHoldShort,
            $"Taxi route must terminate at a RunwayHoldShort, not on the runway. " + $"Terminal #{terminalId} is {terminalNode.Type}."
        );
        Assert.True(
            terminalNode.RunwayId is { } terminalRwy && terminalRwy.Contains("30"),
            $"Terminal hold-short #{terminalId} must protect runway 30 (RunwayId={terminalNode.RunwayId?.ToString() ?? "(null)"})."
        );
    }

    [Fact]
    public void CtoMlt_AdvancesPastLineUpPhase()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine(output);
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, AfterCtoCleared);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var current = ac.Phases?.CurrentPhase;
        string chain = DescribePhases(ac);
        output.WriteLine(
            $"t={AfterCtoCleared} {Callsign}: phase={current?.GetType().Name ?? "(null)"} "
                + $"alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F1} chain={chain}"
        );

        Assert.True(
            current is not LineUpPhase,
            $"N157LE must not still be in LineUpPhase {AfterCtoCleared - 2511}s after CTO MLT. "
                + $"Current phase={current?.GetType().Name ?? "(null)"}, chain={chain}"
        );
    }

    private static string DescribePhases(AircraftState ac)
    {
        if (ac.Phases is null)
        {
            return "(null)";
        }
        var plist = ac.Phases.Phases;
        if (plist.Count == 0)
        {
            return "(empty)";
        }
        return string.Join(" -> ", plist.Select((p, i) => i == ac.Phases.CurrentIndex ? $"[{p.GetType().Name}]" : p.GetType().Name));
    }

    /// <summary>
    /// Regression: after the AutoExtendVariant branch-finder fix, every segment's
    /// FromNodeId must equal the previous segment's ToNodeId. Pre-fix, the W → W1
    /// variant extension dropped the bridging W segment (683 → 16) because
    /// branchSegmentIndex was set to <c>i</c> for ToNodeId matches in the first
    /// loop, when it should have been <c>i + 1</c>.
    /// </summary>
    [Fact]
    public void TaxiBwRwy30_RouteHasNoDiscontinuity()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine(output);
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, AfterTaxiCommand);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        var route = ac.Ground?.AssignedTaxiRoute;
        Assert.NotNull(route);

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        for (int i = 1; i < route.Segments.Count; i++)
        {
            int prevTo = route.Segments[i - 1].ToNodeId;
            int curFrom = route.Segments[i].FromNodeId;
            if (prevTo != curFrom)
            {
                double gapFt = 0;
                if (layout.Nodes.TryGetValue(prevTo, out var a) && layout.Nodes.TryGetValue(curFrom, out var b))
                {
                    gapFt = GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;
                }
                Assert.Fail(
                    $"Route discontinuity at segment[{i}]: previous ToNodeId={prevTo} "
                        + $"({route.Segments[i - 1].TaxiwayName}), current FromNodeId={curFrom} "
                        + $"({route.Segments[i].TaxiwayName}), gap {gapFt:F1} ft"
                );
            }
        }
    }
}
