using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Exit taxiways that end before the runway's holding distance. MIA T8 ends at S inside the 12/30 holding distance and
/// has no bar of its own there: the T8 exit turns onto S and ends at S's 12/30 bar. COS B1's south-east leg ends on
/// runway 13/31 at a bar short of the 17R/35L holding distance; its east leg passes runway 13/31's bar, moving away from
/// that runway, to the 17R/35L bar at the holding distance, and that is where every B1 exit ends.
/// </summary>
public class ShortBranchExitTests(ITestOutputHelper output)
{
    /// <summary>The 17R centerline node B1 branches from (located by position, not id).</summary>
    private static readonly LatLon B1Branch = new(38.823940, -104.715890);

    [Fact]
    public void Mia12_T8Exit_EndsAtSsBarBeyondTheHoldingDistance()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("MIA", "12", "C25A", "TST1", 1.0);
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;
        Assert.True(engine.SendCommand("TST1", "EXIT T8").Success);
        GroundRunway runway = aircraft.Ground.Layout!.FindRunway("12")!;
        double holdingFt = HoldingDistanceFt(runway);

        (HoldingAfterExitPhase holding, string? exitTaxiway, List<string> clearCalls) = RunToHold(engine, aircraft);

        Assert.Equal("T8", exitTaxiway);
        Assert.False(holding.StoppedInsideHoldingDistance);
        GroundNode bar = aircraft.Ground.Layout!.Nodes[holding.HoldShortNodeId!.Value];
        Assert.Contains(bar.Edges, edge => edge.MatchesTaxiway("S"));
        Assert.True(CrossTrackFt(runway, bar.Position) >= holdingFt - AirportGroundLayout.HoldingDistanceToleranceFt);

        double halfLengthNm = AircraftLength.ResolveFt("C25A") / 2.0 / GeoMath.FeetPerNm;
        LatLon tail = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading.ToReciprocal(), halfLengthNm);
        double tailFt = CrossTrackFt(runway, tail);
        output.WriteLine($"held at S bar #{bar.Id}, tail {tailFt:F0} ft from the 12/30 centerline; holding distance {holdingFt:F0} ft");
        Assert.True(tailFt >= holdingFt - AirportGroundLayout.HoldingDistanceToleranceFt, $"tail {tailFt:F0} ft from the centerline");
        Assert.Equal("S", aircraft.Ground.CurrentTaxiway);
        Assert.NotEmpty(clearCalls);
    }

    /// <summary>
    /// From B1's own 17R centerline node, every exit search that offers B1 takes its east leg through runway 13/31's bar
    /// to the 17R/35L bar at the holding distance, never the south-east leg that ends on runway 13. No rollout reaches B1
    /// as a candidate on its own: 17R arrivals touch down past it and 35L arrivals take an A exit first, so the search is
    /// tested directly.
    /// </summary>
    [Fact]
    public void Cos17R_ExitSearchFromB1_EndsAtThe17RBarBeyondRunway13()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("COS");
        if ((layout is null) || (TestVnasData.NavigationDb is null))
        {
            return;
        }

        TrueHeading heading = NavigationDatabase.Instance.GetRunway("COS", "17R")!.TrueHeading;
        GroundNode? centerline = layout.FindNearestCenterlineNode(B1Branch.Lat, B1Branch.Lon, heading, "17R");
        Assert.NotNull(centerline);
        GroundRunway runway = layout.FindRunway("17R")!;

        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? named = layout.FindAdjacentHoldShort(
            centerline,
            "17R",
            heading,
            new ExitPreference { Taxiway = "B1" }
        );
        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? leftSide = layout.FindAdjacentHoldShort(
            centerline,
            "17R",
            heading,
            new ExitPreference { Side = ExitSide.Left }
        );
        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? any = layout.FindAdjacentHoldShort(centerline, "17R", heading, null);
        foreach (
            (string label, (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? exit) in new[]
            {
                ("B1", named),
                ("left", leftSide),
                ("any", any),
            }
        )
        {
            output.WriteLine(
                exit is { } found
                    ? $"{label}: {found.Taxiway} → HS #{found.Node.Id} {CrossTrackFt(runway, found.Node.Position):F0} ft, "
                        + $"path [{string.Join("→", found.Path.Select(n => n.Id))}]"
                    : $"{label}: no exit"
            );
        }

        Assert.NotNull(named);
        Assert.Equal("B1", named.Value.Taxiway);
        Assert.Contains(named.Value.Path, node => (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId?.Contains("13") == true));
        foreach ((GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? exit in new[] { named, leftSide, any })
        {
            if (exit is { } found)
            {
                AssertClearOfEveryRunway(layout, runway, found.Node);
            }
        }
    }

    [Fact]
    public void Cos35L_InstructedB1Exit_HoldsAtThe35LBarBeyondRunway13AndReportsClear()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("COS", "35L", "C25A", "TST1", 1.0);
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo landingRunway) = spawned;
        Assert.True(engine.SendCommand("TST1", "EXIT B1").Success);
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        GroundRunway runway = layout.FindRunway("35L")!;

        (HoldingAfterExitPhase holding, string? exitTaxiway, List<string> clearCalls) = RunToHold(engine, aircraft);

        GroundNode bar = layout.Nodes[holding.HoldShortNodeId!.Value];
        output.WriteLine($"exited on {exitTaxiway}, holding at HS #{bar.Id} ({bar.RunwayId}) {CrossTrackFt(runway, bar.Position):F0} ft from 35L");
        Assert.Equal("B1", exitTaxiway);
        Assert.True(bar.RunwayId?.Contains("35L") == true, $"HS #{bar.Id} belongs to {bar.RunwayId}");
        Assert.Contains(bar.Edges, edge => edge.MatchesTaxiway("B1"));
        AssertClearOfEveryRunway(layout, runway, bar);
        Assert.False(RunwayOnPavement(layout, aircraft.Position), "the aircraft holds on a runway");
        Assert.NotEmpty(clearCalls);
        Assert.NotEqual(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(aircraft, landingRunway));
    }

    /// <summary>
    /// ATL A4 ends at a bar short of the 8L/26R holding distance with nothing of that runway beyond it. Told to exit
    /// there, the arrival holds at that bar still on the runway, without reporting clear, and does not pull up to the
    /// parallel even with auto pull-up on; the stop survives a snapshot round trip.
    /// </summary>
    [Fact]
    public void Atl26R_InstructedA4Exit_HoldsAtItsShortBarWithoutReportingClear()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("ATL", "26R", "C25A", "TST1", 1.0);
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo landingRunway) = spawned;
        engine.Scenario!.AutoPullUpToParallel = true;
        Assert.True(engine.SendCommand("TST1", "EXIT A4").Success);
        GroundRunway runway = aircraft.Ground.Layout!.FindRunway("26R")!;

        (HoldingAfterExitPhase holding, string? exitTaxiway, List<string> clearCalls) = RunToHold(engine, aircraft);

        GroundNode bar = aircraft.Ground.Layout!.Nodes[holding.HoldShortNodeId!.Value];
        output.WriteLine($"exited on {exitTaxiway}, holding at HS #{bar.Id} {CrossTrackFt(runway, bar.Position):F0} ft from 26R");
        Assert.Equal("A4", exitTaxiway);
        Assert.Contains(bar.Edges, edge => edge.MatchesTaxiway("A4"));
        Assert.True(CrossTrackFt(runway, bar.Position) < HoldingDistanceFt(runway) - AirportGroundLayout.HoldingDistanceToleranceFt);
        Assert.Empty(clearCalls);
        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(aircraft, landingRunway));
        Assert.True(holding.StoppedInsideHoldingDistance);
        var restored = HoldingAfterExitPhase.FromSnapshot((HoldingAfterExitPhaseDto)holding.ToSnapshot());
        Assert.True(restored.StoppedInsideHoldingDistance);
    }

    /// <summary>
    /// Naming A4 from its 26R centerline node finds A4's short bar; listing 26R's exits taxiway by taxiway (what the exit
    /// side inference counts) does not list it, since nobody instructed that exit.
    /// </summary>
    [Fact]
    public void Atl26R_ExitListing_OmitsA4sShortBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("ATL");
        if ((layout is null) || (TestVnasData.NavigationDb is null))
        {
            return;
        }

        TrueHeading heading = NavigationDatabase.Instance.GetRunway("ATL", "26R")!.TrueHeading;
        GroundRunway runway = layout.FindRunway("26R")!;
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        double holdingFt = HoldingDistanceFt(runway);
        var named = new ExitPreference { Taxiway = "A4" };
        (GroundNode Centerline, (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side) Exit)? shortExit = layout
            .Nodes.Values.Where(node =>
                node.Edges.Any(edge => edge.IsRunwayCenterline)
                && node.Edges.Any(edge => !edge.IsRunwayCenterline && edge.MatchesTaxiway("A4"))
                && RunwayCrossingDetector.IsOnRunway(node.Position, rect)
            )
            .Select(node => (Centerline: node, Exit: layout.FindAdjacentHoldShort(node, "26R", heading, named)))
            .Where(found =>
                (found.Exit is { } exit) && (CrossTrackFt(runway, exit.Node.Position) < holdingFt - AirportGroundLayout.HoldingDistanceToleranceFt)
            )
            .Select(found =>
                ((GroundNode Centerline, (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side) Exit)?)
                    (found.Centerline, found.Exit!.Value)
            )
            .FirstOrDefault();
        Assert.NotNull(shortExit);
        (GroundNode centerline, (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side) exit) = shortExit.Value;

        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? listed = layout.FindAdjacentHoldShortForListing(
            centerline,
            "26R",
            heading,
            new ExitPreference { Taxiway = "A4", Side = exit.Side }
        );

        output.WriteLine($"named A4 from CL #{centerline.Id} → HS #{exit.Node.Id}; listed → {(listed is { } l ? $"HS #{l.Node.Id}" : "nothing")}");
        Assert.NotEqual(exit.Node.Id, listed?.Node.Id);
        Assert.True(
            (listed is not { } found) || (CrossTrackFt(runway, found.Node.Position) >= holdingFt - AirportGroundLayout.HoldingDistanceToleranceFt),
            "the listing offers a bar inside the holding distance"
        );
    }

    /// <summary>The bar is at <paramref name="runway"/>'s holding distance and on no runway's pavement.</summary>
    private static void AssertClearOfEveryRunway(AirportGroundLayout layout, GroundRunway runway, GroundNode bar)
    {
        Assert.True(
            CrossTrackFt(runway, bar.Position) >= HoldingDistanceFt(runway) - AirportGroundLayout.HoldingDistanceToleranceFt,
            $"HS #{bar.Id} is {CrossTrackFt(runway, bar.Position):F0} ft from the centerline, inside the holding distance"
        );
        Assert.False(RunwayOnPavement(layout, bar.Position), $"HS #{bar.Id} is on a runway");
        Assert.DoesNotContain(bar.Edges, edge => edge.IsRunwayCenterline);
    }

    private static bool RunwayOnPavement(AirportGroundLayout layout, LatLon position) =>
        layout.Runways.Any(runway => RunwayCrossingDetector.IsOnRunway(position, RunwayCrossingDetector.BuildRunwayRectangle(runway)));

    /// <summary>
    /// Ticks until the arrival holds after its exit; returns the phase, the taxiway it exited on and every "clear of
    /// runway" line the pilot queued on the way.
    /// </summary>
    private static (HoldingAfterExitPhase Holding, string? ExitTaxiway, List<string> ClearCalls) RunToHold(
        SimulationEngine engine,
        AircraftState aircraft
    )
    {
        string? exitTaxiway = null;
        var clearCalls = new List<string>();
        void Capture(string callsign, string line)
        {
            if ((callsign == aircraft.Callsign) && line.Contains("clear of runway", StringComparison.OrdinalIgnoreCase))
            {
                clearCalls.Add(line);
            }
        }

        engine.WarningEmitted += Capture;
        engine.TerminalEntryEmitted += entry => Capture(entry.Callsign, entry.Message);
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is RunwayExitPhase)
            {
                exitTaxiway ??= aircraft.Ground.CurrentTaxiway;
            }

            if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase holding)
            {
                return (holding, exitTaxiway, clearCalls);
            }
        }

        Assert.Fail($"{aircraft.Callsign} never finished its runway exit within 400 s");
        return default;
    }

    private static double HoldingDistanceFt(GroundRunway runway) =>
        RunwayCrossingDetector.BuildRunwayRectangle(runway).HoldShortNm * GeoMath.FeetPerNm;

    private static double CrossTrackFt(GroundRunway runway, LatLon position)
    {
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        return Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)) * GeoMath.FeetPerNm;
    }
}
