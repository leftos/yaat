using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 W6 — the crossed runway is a directional anchor for <c>TAXI &lt;twy&gt; CROSS &lt;rwy&gt;</c>.
/// When <c>CROSS &lt;rwy&gt;</c> is the only directional cue (no destination runway / parking / spot), the
/// route must head toward — and across — the named runway and stop just past it, disambiguating the start
/// direction the way a named destination would.
///
/// Vehicle: OAK recording 4d4344011a72.zip (N427MX lands 28L and exits north onto taxiway G, sitting at the
/// 28L hold-short #495). G crosses both 28L (behind) and 28R (ahead), so a bare <c>TAXI G</c> is ambiguous —
/// before W6, <c>TAXI G CROSS 28R</c> from here anchors the WRONG way (back across 28L toward #358). With
/// the crossed-runway anchor, it must route north across 28R (entry hold-short 503 / far hold-short 361) and
/// hold in position just past the far bars (W4 termination), via <see cref="CrossingRunwayPhase"/>.
/// </summary>
public class Issue172TaxiCrossRunwayAnchorTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";
    private const string Callsign = "N427MX";

    private const int EntryHoldShortNode = 503; // 28R hold-short on the 28L (start) side
    private const int FarHoldShortNode = 361; // 28R hold-short on the far side it must cross to
    private const int GcJunctionNode = 350; // first junction past the far bars
    private const int Rwy28LCrossingNode = 358; // the 28L crossing — the WRONG direction (pre-W6 behavior)

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

    [Fact]
    public void TaxiGCross28R_FromAmbiguousStart_RoutesTowardAndAcross28R_NotBackToward28L()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var entryHs = layout.Nodes[EntryHoldShortNode];
        var farHs = layout.Nodes[FarHoldShortNode];
        var gcJunction = layout.Nodes[GcJunctionNode];
        var rwy28L = layout.Nodes[Rwy28LCrossingNode];

        // Replay to N427MX sitting at the 28L hold-short (#495) — the ambiguous start.
        engine.Replay(recording, 1248);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        NearestNodeHelper.Log(output, "pre-override", ac, layout);

        var result = engine.SendCommand(Callsign, "TAXI G CROSS 28R");
        Assert.True(result.Success, result.Message);

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"Route: {route.ToSummary()} ({route.Segments.Count} segments)");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  HS: node={hs.NodeId} reason={hs.Reason} target={hs.TargetName} cleared={hs.IsCleared}");
        }

        // The route must cross 28R (a 28R RunwayCrossing hold-short is present), not walk back across 28L.
        Assert.Contains(
            route.HoldShortPoints,
            h => h.Reason == HoldShortReason.RunwayCrossing && (h.TargetName?.Contains("28R", StringComparison.OrdinalIgnoreCase) ?? false)
        );

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

            if (t % 15 == 0 || ac.Phases?.CurrentPhase is HoldingInPositionPhase)
            {
                double dFar = GeoMath.DistanceNm(ac.Position, farHs.Position) * 6076.12;
                NearestNodeHelper.Log(
                    output,
                    $"t={t} phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"} ias={ac.IndicatedAirspeed:F1} dFar={dFar:F0}",
                    ac,
                    layout
                );
            }
        }

        Assert.NotNull(ac);
        var finalPhase = ac.Phases?.CurrentPhase;
        double distEntry = GeoMath.DistanceNm(ac.Position, entryHs.Position) * 6076.12;
        double distFar = GeoMath.DistanceNm(ac.Position, farHs.Position) * 6076.12;
        double distJct = GeoMath.DistanceNm(ac.Position, gcJunction.Position) * 6076.12;
        double dist28L = GeoMath.DistanceNm(ac.Position, rwy28L.Position) * 6076.12;
        output.WriteLine(
            $"FINAL phase={finalPhase?.GetType().Name ?? "null"} ias={ac.IndicatedAirspeed:F1} "
                + $"distEntry={distEntry:F0} distFar={distFar:F0} distJct={distJct:F0} dist28L={dist28L:F0}"
        );

        Assert.True(sawCrossing, "expected CrossingRunwayPhase across 28R (CROSS pre-cleared it)");
        Assert.True(
            finalPhase is HoldingInPositionPhase,
            $"expected HoldingInPositionPhase just past 28R; got {finalPhase?.GetType().Name ?? "null"}"
        );
        Assert.True(ac.IndicatedAirspeed < 0.5, $"aircraft should be stopped; ias={ac.IndicatedAirspeed:F1}");

        // Went toward 28R and cleared its far bars — NOT back toward 28L.
        Assert.True(distFar < distEntry, $"aircraft should rest past the 28R far bars (distFar={distFar:F0} < distEntry={distEntry:F0})");
        Assert.True(distJct < 250, $"aircraft should hold just past 28R near the G/C junction; was {distJct:F0} ft from #350");
        Assert.True(dist28L > 600, $"aircraft must not have routed back toward the 28L crossing; was only {dist28L:F0} ft from #358");
    }

    // J approaches the 28R crossing from the #379 → #378 → #501 side (high-speed exit geometry).
    private const int JApproachNode = 379;
    private const int Rwy28RHoldShortOnJ = 501; // entry-side 28R hold-short on J
    private const int Rwy28RFarHoldShortOnJ = 374; // far-side 28R hold-short on J

    private static AircraftState SpawnOnJApproaching28R(AirportGroundLayout layout)
    {
        var start = layout.Nodes[JApproachNode];
        var toward = layout.Nodes[Rwy28RHoldShortOnJ];
        var aircraft = new AircraftState
        {
            Callsign = "N172JX",
            AircraftType = "C172",
            Position = start.Position,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(start.Position, toward.Position)),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                CruiseAltitude = 1500,
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    [Fact]
    public void TaxiJCross28R_RoutesAlongJAcross28R_AndHoldsJustPast()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var entryHs = layout.Nodes[Rwy28RHoldShortOnJ];
        var farHs = layout.Nodes[Rwy28RFarHoldShortOnJ];

        var aircraft = SpawnOnJApproaching28R(layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "issue-172-w6-j-cross",
            ScenarioName = "Issue 172 W6 TAXI J CROSS 28R",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunway = false,
        };

        var result = engine.SendCommand("N172JX", "TAXI J CROSS 28R");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"Route: {route.ToSummary()} ({route.Segments.Count} segments)");

        // Routed along J across 28R — a 28R RunwayCrossing hold-short is present.
        Assert.Contains(
            route.HoldShortPoints,
            h => h.Reason == HoldShortReason.RunwayCrossing && (h.TargetName?.Contains("28R", StringComparison.OrdinalIgnoreCase) ?? false)
        );

        bool sawCrossing = false;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft("N172JX");
            if (ac is null)
            {
                break;
            }

            if (ac.Phases?.CurrentPhase is CrossingRunwayPhase)
            {
                sawCrossing = true;
            }
        }

        var final = engine.FindAircraft("N172JX");
        Assert.NotNull(final);
        double distEntry = GeoMath.DistanceNm(final.Position, entryHs.Position) * 6076.12;
        double distFar = GeoMath.DistanceNm(final.Position, farHs.Position) * 6076.12;
        output.WriteLine(
            $"FINAL phase={final.Phases?.CurrentPhase?.GetType().Name ?? "null"} ias={final.IndicatedAirspeed:F1} distEntry={distEntry:F0} distFar={distFar:F0}"
        );

        Assert.True(sawCrossing, "expected CrossingRunwayPhase across 28R on J");
        Assert.True(
            final.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"expected HoldingInPositionPhase just past 28R; got {final.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );
        Assert.True(final.IndicatedAirspeed < 0.5, $"aircraft should be stopped; ias={final.IndicatedAirspeed:F1}");
        Assert.True(distFar < distEntry, $"aircraft should rest past the 28R far bars on J (distFar={distFar:F0} < distEntry={distEntry:F0})");
    }
}
