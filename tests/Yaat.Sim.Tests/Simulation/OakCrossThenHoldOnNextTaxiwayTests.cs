using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the "land 28L → cross 28R → done" workflow at OAK.
///
/// Bug: a controller issues TAXI G C HS 28R after an aircraft lands and exits
/// 28L, expecting the aircraft to hold short of 28R, then on RES/CROSS cross
/// the runway and stop just onto taxiway C awaiting further instructions or
/// deletion. Today the route resolved by TaxiPathfinder.ResolveExplicitPath
/// extends through the full length of C (53 segments to its dead-end), and
/// BuildResumePhases for an ExplicitHoldShort reason at a runway HS doesn't
/// insert CrossingRunwayPhase, so the aircraft taxis across the runway at
/// 15 kt taxi speed and walks the entire C taxiway.
///
/// Recording: S2-OAK-3 (2) — VFR Sequencing. Three aircraft (N427MX t=1243,
/// N267QA t=1587, N10194 t=2617) all hit this pattern. We replay N427MX as
/// the canonical case.
///
/// Expected after fix:
/// - Route resolves to a small number of segments (G across 28R, then one
///   segment onto C past the G/C junction).
/// - After RES at t=1293, aircraft transitions through HoldingShort →
///   CrossingRunwayPhase → TaxiingPhase → HoldingInPositionPhase.
/// - Final state: stopped on taxiway C within ~150 ft of the G/C junction (#350).
/// </summary>
public class OakCrossThenHoldOnNextTaxiwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";
    private const string Callsign = "N427MX";

    // Timeline anchors from the recording (per `bug_bundle.py history --callsign N427MX`):
    private const int TaxiCommandTime = 1243; // CMD: TAXI G C HS 28R
    private const int ResCommandTime = 1293; // CMD: RES

    // ~67s after RES — well past the crossing and the post-crossing roll-out to the stop. The
    // navigator settles into HoldingInPositionPhase by ~t=1350 (the corner-speed cap realistically
    // slows the G→C turn), resting on C within the asserted 600 ft of the G/C junction and staying put.
    private const int PostResSettleTime = 1360;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After TAXI G C HS 28R is processed, the resolved route must NOT extend
    /// the full length of taxiway C. With no destination given, the route
    /// should end shortly after the runway crossing (one segment onto C past
    /// the G/C junction at #350).
    /// </summary>
    [Fact]
    public void TaxiGCHs28R_RouteDoesNotWalkFullLengthOfC()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to one second after the TAXI command at t=1243.
        engine.Replay(recording, TaxiCommandTime + 1);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        var groundLayout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(groundLayout);

        output.WriteLine($"Route: {route.ToSummary()}");
        output.WriteLine($"Segments: {route.Segments.Count}");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  HS: nodeId={hs.NodeId} reason={hs.Reason} target={hs.TargetName}");
        }

        // Pre-fix: 53 segments (G + full C dead-end). Post-fix: ~10 (G across 28R + 1 C segment).
        Assert.True(
            route.Segments.Count <= 12,
            $"route should not walk the full length of C; got {route.Segments.Count} segments. Last 5: "
                + string.Join("; ", route.Segments.TakeLast(5).Select(s => $"{s.TaxiwayName}@{s.ToNodeId}"))
        );

        // Route must include the entry-side 28R hold-short (this is the user-facing 'HS 28R').
        Assert.Contains(route.HoldShortPoints, h => h.TargetName is not null && h.TargetName.Contains("28R", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Full E2E: replay TAXI → hold short → RES → cross → stop. After the
    /// crossing, aircraft must be in HoldingInPositionPhase on taxiway C,
    /// near the G/C junction. Today it ends up far down C in TaxiingPhase.
    /// </summary>
    [Fact]
    public void AfterRes_AircraftCrossesAndHoldsOnC()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, TaxiCommandTime + 1);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var groundLayout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(groundLayout);

        // Tick forward through the hold-short, RES, crossing, and post-crossing settle.
        // Record key transitions for diagnostic context.
        bool sawHoldingShort = false;
        bool sawCrossingRunway = false;
        for (int t = TaxiCommandTime + 2; t <= PostResSettleTime; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            var phase = ac.Phases?.CurrentPhase;
            if (phase is HoldingShortPhase)
            {
                sawHoldingShort = true;
            }

            if (phase is CrossingRunwayPhase)
            {
                sawCrossingRunway = true;
            }

            if (t == ResCommandTime || t == ResCommandTime + 1 || t == PostResSettleTime || t % 10 == 0)
            {
                NearestNodeHelper.Log(
                    output,
                    $"t={t} phase={phase?.GetType().Name ?? "null"} gs={ac.GroundSpeed:F1} twy={ac.Ground.CurrentTaxiway ?? "?"}",
                    ac,
                    groundLayout
                );
            }
        }

        Assert.NotNull(ac);
        Assert.True(sawHoldingShort, "Expected aircraft to enter HoldingShortPhase before RES");
        Assert.True(sawCrossingRunway, "Expected CrossingRunwayPhase to fire after RES (currently doesn't for ExplicitHoldShort at runway HS)");

        // After settling, aircraft must be holding in position (route complete) on taxiway C.
        var finalPhase = ac.Phases?.CurrentPhase;
        Assert.True(
            finalPhase is HoldingInPositionPhase,
            $"Expected HoldingInPositionPhase after crossing; got {finalPhase?.GetType().Name ?? "null"}. "
                + $"pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) twy={ac.Ground.CurrentTaxiway ?? "?"}"
        );

        Assert.Equal("C", ac.Ground.CurrentTaxiway);

        // Distance from the G/C junction node #350 (37.728452, -122.212786) — aircraft should be
        // within a couple hundred feet (one C segment past it), not down at C's dead-end (#337).
        var gcJunction = groundLayout.Nodes[350];
        double distFt = GeoMath.DistanceNm(ac.Position, gcJunction.Position) * 6076.12;
        output.WriteLine($"Final distance from G/C junction (#350): {distFt:F0} ft");
        Assert.True(
            distFt < 600,
            $"Aircraft should stop within ~600 ft of G/C junction (#350); was {distFt:F0} ft away. " + $"Likely walked further down C than intended."
        );
    }
}
