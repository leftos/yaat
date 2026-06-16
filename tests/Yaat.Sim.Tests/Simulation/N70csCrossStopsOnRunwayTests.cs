using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Bug: N70CS was given <c>TAXI &gt;J HS 28R</c> (taxiway J crosses runway 28R/10L and continues past
/// it), held short, then <c>CROSS</c> — but it stopped <em>on</em> the runway (between the paired
/// hold-short bars) instead of crossing fully and stopping just clear on the far side, forcing a
/// <c>TAXI J C</c> recovery.
///
/// Root cause: <see cref="RouteMaterialiser"/> truncated the route one segment past the NEAR-side
/// hold short (mid-runway) for an explicit runway hold-short on a through-taxiway, instead of
/// extending to the far-side (exit) hold short. Recording:
/// <c>n70cs-cross-stops-on-runway-recording.yaat-bug-report-bundle.zip</c> (OAK/ZOA).
/// </summary>
public class N70csCrossStopsOnRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n70cs-cross-stops-on-runway-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N70CS";

    // OAK: taxiway J approaches the 28R/10L crossing from #379 -> #378 -> #501 (entry-side hold short).
    private const int JApproachNode = 379;
    private const int Rwy28RHoldShortOnJ = 501;

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

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static bool Is28RHoldShort(AirportGroundLayout layout, int nodeId) =>
        layout.Nodes.TryGetValue(nodeId, out var node)
        && node.Type == GroundNodeType.RunwayHoldShort
        && node.RunwayId is { } rwy
        && rwy.Contains("28R");

    [Fact]
    public void TaxiJHoldShort28R_ExtendsRouteAcrossToFarSideBar_NotMidRunway()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        // "TAXI J HS 28R" — J is the only cleared taxiway and it crosses 28R. The route must extend
        // through the crossing to the far-side 28R hold-short bar so a later CROSS leaves the aircraft
        // just clear, rather than truncating one segment past the near bar onto the runway.
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: JApproachNode,
            taxiwayNames: ["J"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "OAK", ExplicitHoldShorts = ["28R"] },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        output.WriteLine($"Route: {route.ToSummary()} ({route.Segments.Count} segments)");
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var s = route.Segments[i];
            output.WriteLine($"  [{i, 2}] {s.FromNodeId, 5} -> {s.ToNodeId, 5} ({s.TaxiwayName})");
        }

        // The route reaches BOTH the near-side and far-side 28R/10L hold-short bars (a full crossing),
        // and ENDS at a far-side bar (just clear) — not one segment past the near bar on the runway.
        var rwyHoldShortNodes = route.Segments.Select(s => s.ToNodeId).Where(id => Is28RHoldShort(layout, id)).Distinct().ToList();
        Assert.True(
            rwyHoldShortNodes.Count >= 2,
            $"route should cross 28R reaching both hold-short bars; reached {rwyHoldShortNodes.Count}: [{string.Join(",", rwyHoldShortNodes)}]"
        );
        Assert.True(
            Is28RHoldShort(layout, route.Segments[^1].ToNodeId),
            $"route should END at the far-side 28R hold short (just clear); ended at node {route.Segments[^1].ToNodeId}"
        );

        // The near-side bar is the explicit hold-short; the far-side exit bar is dropped (auto-crossed).
        Assert.Contains(route.HoldShortPoints, h => h.NodeId == Rwy28RHoldShortOnJ && h.Reason == HoldShortReason.ExplicitHoldShort);
    }

    [Fact]
    public void Replay_GivenCrossWhileHoldingShort_CrossesFullyAndStopsClearOf28R()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay through the recorded "TAXI >J HS 28R" (t=2417); tick (physics only — do NOT apply the
        // recorded CROSS at t=2518) until N70CS settles holding short of 28R.
        engine.Replay(recording, 2430);
        var ac = engine.FindAircraft(Callsign);
        if (ac is null)
        {
            return; // arrival did not reproduce on this machine's data — silent skip
        }

        bool holdingShort = false;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            if (
                ac.Phases?.CurrentPhase is HoldingShortPhase hp
                && hp.HoldShort.TargetName is { } tn
                && tn.Contains("28R", StringComparison.OrdinalIgnoreCase)
            )
            {
                holdingShort = true;
                break;
            }
        }

        Assert.NotNull(ac);
        Assert.True(holdingShort, $"N70CS should hold short of 28R; phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"}");
        var holdShortPos = ac.Position;
        output.WriteLine($"holding short at ({holdShortPos.Lat:F6},{holdShortPos.Lon:F6})");

        var crossResult = engine.SendCommand(Callsign, "CROSS");
        Assert.True(crossResult.Success, $"CROSS failed: {crossResult.Message}");

        bool sawCrossing = false;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            if (ac.Phases?.CurrentPhase is CrossingRunwayPhase)
            {
                sawCrossing = true;
            }

            if (sawCrossing && ac.Phases?.CurrentPhase is HoldingInPositionPhase && ac.IndicatedAirspeed < 0.5)
            {
                break;
            }
        }

        Assert.NotNull(ac);
        double distFromHoldShort = GeoMath.DistanceNm(ac.Position, holdShortPos) * 6076.12;
        output.WriteLine(
            $"FINAL phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"} ias={ac.IndicatedAirspeed:F1} "
                + $"distFromHoldShort={distFromHoldShort:F0} ft"
        );

        Assert.True(sawCrossing, "N70CS should pass through CrossingRunwayPhase after CROSS");
        Assert.True(
            ac.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"expected HoldingInPositionPhase; got {ac.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );
        Assert.True(ac.IndicatedAirspeed < 0.5, $"aircraft should be stopped; ias={ac.IndicatedAirspeed:F1}");

        // The 28R/10L crossing on J spans ~632 ft between the paired hold-short bars. A correct cross
        // leaves the aircraft just clear on the far side (~630+ ft from where it held). The bug stopped
        // it mid-runway (~278 ft out), still between the bars.
        Assert.True(
            distFromHoldShort > 550,
            $"aircraft must cross fully clear of 28R; stopped only {distFromHoldShort:F0} ft from the hold-short line (on the runway)"
        );
    }
}
