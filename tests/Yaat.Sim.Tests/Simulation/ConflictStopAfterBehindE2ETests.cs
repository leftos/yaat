using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the second-order bug exposed once BEHIND/GIVEWAY for ground taxi
/// started working: the held aircraft (N569SX) is parked at HoldingAfterExit, and
/// the target aircraft (N152SP) — which is taxiing past it on the next taxiway —
/// gets stopped by GroundConflictDetector.ApplyClosingLimit because N569SX is
/// inside DefaultStopDistanceFt (100 ft) and within the 90° "closing" cone.
///
/// In the bundle the user has to send N152SP a `BREAK` command at t=596 to
/// override the conflict limit and continue taxiing. The expected behavior is
/// that N152SP keeps moving — N569SX is parked off to the side, not in
/// N152SP's path.
///
/// Recording: S2-OAK-3 (2) | VFR Sequencing — second take, BEHIND target callsign
/// typed correctly as `N152SP`. Action timeline:
///   t=521 N569SX: GIVEWAY N152SP TAXI C D @NEW1
///   t=565 N152SP auto-stops at hold-short E (preset `HS E`)
///   t=575 N152SP: TAXI C B RWY 28R (new route)
///   t=580–595 N152SP stuck at ias=0 because of GroundConflictDetector
///   t=596 N152SP: BREAK → can finally proceed
/// </summary>
[Collection("NavDbMutator")]
public class ConflictStopAfterBehindE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/conflict-stop-after-behind-recording.yaat-bug-report-bundle.zip";

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
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Reproduces the bug geometry directly: two C172s placed at the bundle's
    /// captured positions/headings, then GroundConflictDetector.ApplySpeedLimits
    /// is run with diagnosticLog wired to test output.
    ///
    /// The bundle's snapshot at t=590 captures N152SP stuck at ias=0 with
    /// SpeedLimit=0 ~98 ft from parked N569SX. We can't replay that snapshot
    /// directly because TaxiRoute.FromSnapshot drops the route when node IDs
    /// don't survive layout regeneration — so we set up the geometry from
    /// scratch using the same positions, headings, types, and a 1-segment
    /// taxi route synthesized from the OAK layout.
    /// </summary>
    [Fact]
    public void Diagnostic_LogConflictDetectorReasoning()
    {
        var groundLayout = new TestAirportGroundData().GetLayout("OAK");
        if (groundLayout is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();

        var (n152, n569) = BuildBundleGeometry(groundLayout);

        output.WriteLine(
            $"N152SP: type={n152.AircraftType} pos=({n152.Position.Lat:F6},{n152.Position.Lon:F6}) hdg={n152.TrueHeading.Degrees:F0} phase={n152.Phases?.CurrentPhase?.Name ?? "-"} route={(n152.Ground.AssignedTaxiRoute is null ? "NULL" : "set")}"
        );
        output.WriteLine(
            $"N569SX: type={n569.AircraftType} pos=({n569.Position.Lat:F6},{n569.Position.Lon:F6}) hdg={n569.TrueHeading.Degrees:F0} phase={n569.Phases?.CurrentPhase?.Name ?? "-"}"
        );

        double gapFt = GeoMath.DistanceNm(n152.Position, n569.Position) * 6076.12;
        double bearing = GeoMath.BearingTo(n152.Position, n569.Position);
        double offNose = Math.Abs(((n152.TrueHeading.Degrees - bearing + 540) % 360) - 180);
        output.WriteLine($"gap = {gapFt:F0} ft, bearing N152SP→N569SX = {bearing:F0}°, off-nose = {offNose:F0}°");

        output.WriteLine("");
        output.WriteLine("=== ApplySpeedLimits diagnostic trace ===");
        GroundConflictDetector.ApplySpeedLimits([n152, n569], groundLayout, deltaSeconds: 0.0, diagnosticLog: output.WriteLine);

        output.WriteLine("");
        output.WriteLine(
            $"After: N152SP.SpeedLimit={n152.Ground.SpeedLimit?.ToString("F1") ?? "-"}, N569SX.SpeedLimit={n569.Ground.SpeedLimit?.ToString("F1") ?? "-"}"
        );
    }

    /// <summary>
    /// Asserts the bug: with the bundle's geometry (parked C172 N569SX 98 ft
    /// off-nose-right of taxiing C172 N152SP), GroundConflictDetector should
    /// NOT impose SpeedLimit=0. Two C172s have ~36 ft wingspans (18 ft from
    /// centerline to wingtip); the lateral clearance at 68° off-nose and 98 ft
    /// total is ~91 ft, leaving ~55 ft of wingtip-to-wingtip room.
    ///
    /// Fails today (ApplyClosingLimit treats anything inside the 90° cone +
    /// 100 ft as "stop"); passes after lateral clearance is computed from
    /// aircraft wingspans.
    /// </summary>
    [Fact]
    public void TaxiingAircraft_NotStoppedByParkedAircraftWithAdequateWingtipClearance()
    {
        var groundLayout = new TestAirportGroundData().GetLayout("OAK");
        if (groundLayout is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();

        var (n152, n569) = BuildBundleGeometry(groundLayout);

        GroundConflictDetector.ApplySpeedLimits([n152, n569], groundLayout, deltaSeconds: 0.0);

        output.WriteLine($"After ApplySpeedLimits: N152SP.SpeedLimit={n152.Ground.SpeedLimit?.ToString("F1") ?? "-"}");

        Assert.False(
            n152.Ground.SpeedLimit == 0,
            $"N152SP (C172 taxiing past parked C172 N569SX 98 ft off-nose-right) should not be stopped by the conflict detector — but SpeedLimit={n152.Ground.SpeedLimit?.ToString("F1") ?? "-"}"
        );
    }

    /// <summary>
    /// Constructs the geometry captured in the bundle at t=590:
    ///   N152SP — C172 taxiing east-southeast at (37.727453, -122.209696) hdg 112°
    ///   N569SX — C172 stationary at HoldingAfterExit at (37.727179, -122.209697) hdg 336°
    /// N152SP needs an active route (CurrentSegment non-null) so Classify returns
    /// MovementState.Taxiing rather than Stationary. We synthesize a 1-segment
    /// route from the nearest two nodes in the layout — the actual route content
    /// doesn't affect ApplyClosingLimit's geometry checks (which use position
    /// and heading directly).
    /// </summary>
    private static (AircraftState N152SP, AircraftState N569SX) BuildBundleGeometry(Yaat.Sim.Data.Airport.AirportGroundLayout layout)
    {
        var n152Pos = new LatLon(37.727453, -122.209696);
        var n569Pos = new LatLon(37.727179, -122.209697);

        var n152 = new AircraftState
        {
            Callsign = "N152SP",
            AircraftType = "C172",
            Position = n152Pos,
            TrueHeading = new TrueHeading(112.0),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        n152.Ground.LayoutAirportId = "OAK";
        n152.Ground.Layout = layout;
        n152.Ground.AssignedTaxiRoute = SynthesizeOneSegmentRoute(layout, n152Pos);

        var taxiing = new TaxiingPhase();
        var n152Phases = new Yaat.Sim.Phases.PhaseList();
        n152Phases.Add(taxiing);
        n152.Phases = n152Phases;

        var n569 = new AircraftState
        {
            Callsign = "N569SX",
            AircraftType = "C172",
            Position = n569Pos,
            TrueHeading = new TrueHeading(335.5),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        n569.Ground.LayoutAirportId = "OAK";
        n569.Ground.Layout = layout;

        var holding = new HoldingAfterExitPhase();
        var n569Phases = new Yaat.Sim.Phases.PhaseList();
        n569Phases.Add(holding);
        n569.Phases = n569Phases;

        return (n152, n569);
    }

    private static Yaat.Sim.Data.Airport.TaxiRoute SynthesizeOneSegmentRoute(Yaat.Sim.Data.Airport.AirportGroundLayout layout, LatLon near)
    {
        // Find two distinct nodes near the position to synthesize a single edge.
        // The conflict detector only inspects the segment's node IDs and the
        // owning aircraft's heading, not the route's geographic accuracy.
        var nearestNode = layout.FindNearestNode(near.Lat, near.Lon);
        Assert.NotNull(nearestNode);
        var neighborEdge = nearestNode.Edges.FirstOrDefault();
        Assert.NotNull(neighborEdge);
        var otherNode = neighborEdge.OtherNode(nearestNode);

        var segment = new Yaat.Sim.Data.Airport.TaxiRouteSegment
        {
            Edge = neighborEdge.Directed(nearestNode, otherNode),
            TaxiwayName = neighborEdge.TaxiwayName,
        };
        return new Yaat.Sim.Data.Airport.TaxiRoute { Segments = [segment], HoldShortPoints = [] };
    }
}
