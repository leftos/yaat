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
/// direction the way a named destination would. Here N172JX starts on J approaching the 28R crossing and
/// must route along J across 28R, holding in position just past the far bars, via
/// <see cref="CrossingRunwayPhase"/>.
/// </summary>
public class Issue172TaxiCrossRunwayAnchorTests(ITestOutputHelper output)
{
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
                Altitude = PlannedAltitude.Vfr(1500),
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
