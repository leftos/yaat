using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// KSEA taxiway D crosses runway 34R/16L at an angle (way across 283°), so the line through D's 34R holding position marking,
/// square to that way across, runs on obliquely over the airport. A C172 taxiing east on C at node 174, just clear of C's own
/// 34R bar, cleared <c>TAXI C B D CROSS 34R</c>, is already past that line though D's 34R bar is hundreds of feet ahead along
/// the route, round the C/B and B/D corners. It has not reached that crossing: a HOLD there is a plain HOLD on a taxiway, and a
/// re-route that leaves the crossing out holds short of 34R as written.
/// </summary>
public sealed class SeaCrossingNotYetReachedTests
{
    private const string Callsign = "N172SP";
    private const string Piston = "C172";
    private const string Taxi = "TAXI C B D CROSS 34R";
    private const string ReRoute = "TAXI C B D";

    /// <summary>KSEA node 174 on taxiway C, east of C's 34R bar.</summary>
    private const int OnC = 174;

    /// <summary>The next C node east of <see cref="OnC"/>, toward taxiway B.</summary>
    private const int EastOnC = 841;

    /// <summary>D's near 34R/16L bar, entered from B.</summary>
    private const int DBar = 414;

    /// <summary>The speed (kt) the C172 is taxiing at when the HOLD or the re-route is given.</summary>
    private const double TaxiKts = 12.0;

    /// <summary>How far (ft) along the route D's 34R bar must still be: well clear of the crossing.</summary>
    private const double FarFromTheCrossingFt = 300.0;

    public SeaCrossingNotYetReachedTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void HoldPastTheMarkingsLineButShortOfTheCrossing_BrakesAtTheTaxiRate_ReadsBackPlainHoldPosition()
    {
        if (Start() is not { } rig)
        {
            return;
        }

        PilotSpeechText readback = Assert.IsType<PilotSpeechText>(
            PilotResponder.BuildReadback(new([new ParsedBlock(null, [new HoldPositionCommand()])]), rig.Aircraft, ActiveRunways.Empty)
        );
        double beforeKts = rig.Aircraft.GroundSpeed;
        Assert.True(rig.Engine.SendCommand(Callsign, "HOLD").Success);
        rig.Engine.TickPhysics(1.0 / SimulationEngine.PhysicsSubTickRate);
        double lossKts = beforeKts - rig.Aircraft.GroundSpeed;
        double taxiStepKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston) / SimulationEngine.PhysicsSubTickRate;

        Assert.Equal("hold position", readback.Terminal);
        Assert.True(
            lossKts <= taxiStepKts + 1e-6,
            $"the {Piston} lost {lossKts:F3} kt in the sub-tick after the HOLD at {beforeKts:F1} kt, more than the taxi rate's {taxiStepKts:F3}"
        );
    }

    [Fact]
    public void ReRoutePastTheMarkingsLineButShortOfTheCrossing_DoesNotKeepTheCrossingCleared()
    {
        if (Start() is not { } rig)
        {
            return;
        }

        CommandResult reRoute = rig.Engine.SendCommand(Callsign, ReRoute);
        Assert.True(reRoute.Success, reRoute.Message);
        Assert.DoesNotContain("(cross ", reRoute.Message, StringComparison.Ordinal);
    }

    private sealed record Rig(SimulationEngine Engine, AircraftState Aircraft);

    /// <summary>
    /// Spawns the C172 at <see cref="OnC"/> heading east along C, clears it <see cref="Taxi"/>, sets it rolling at
    /// <see cref="TaxiKts"/> and runs one physics sub-tick; then checks the geometry the tests are about: its nose past the line
    /// through D's 34R marking, the bar still <see cref="FarFromTheCrossingFt"/> or more along the route. Null when the KSEA layout is
    /// unavailable.
    /// </summary>
    private static Rig? Start()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("SEA");
        if (layout is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "yaat-710-r3",
                ScenarioName = "YAAT-710 crossing not yet reached",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SEA",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };

        GroundNode start = layout.Nodes[OnC];
        var heading = new TrueHeading(GeoMath.BearingTo(start.Position, layout.Nodes[EastOnC].Position));
        AircraftState aircraft = Spawn(engine, layout, start, heading);
        CommandResult taxi = engine.SendCommand(Callsign, Taxi);
        Assert.True(taxi.Success, taxi.Message);
        aircraft.IndicatedAirspeed = TaxiKts;
        engine.TickPhysics(1.0 / SimulationEngine.PhysicsSubTickRate);

        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int barSegment = route.Segments.FindIndex(s => s.ToNodeId == DBar);
        Assert.True(barSegment >= 0, $"{Taxi} does not reach D's 34R bar: {route.ToSummary()}");
        HoldShortPoint bar = Assert.IsType<HoldShortPoint>(route.GetHoldShortAt(DBar));
        Assert.True(bar.IsCleared, $"{Taxi} left D's 34R bar uncleared");
        double acrossDeg = route.Segments[barSegment + 1].Edge.DepartureBearing;
        double nosePastFt =
            (GeoMath.AlongTrackDistanceNm(aircraft.Position, layout.Nodes[DBar].Position, new TrueHeading(acrossDeg)) * GeoMath.FeetPerNm)
            + (AircraftLength.ResolveFt(Piston) / 2.0);
        double toBarFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, bar);
        Assert.True(nosePastFt > 0.0, $"the {Piston}'s nose is {nosePastFt:F1} ft short of the line through D's 34R marking");
        Assert.True(toBarFt > FarFromTheCrossingFt, $"D's 34R bar is only {toBarFt:F0} ft ahead along {route.ToSummary()}");
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        return new Rig(engine, aircraft);
    }

    private static AircraftState Spawn(SimulationEngine engine, AirportGroundLayout layout, GroundNode node, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = Piston,
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSEA",
                Destination = "KBFI",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(3500),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
