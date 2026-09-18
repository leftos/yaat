using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// PUSH $spot must position the aircraft like a tug does: reverse in an arc PAST the spot to a staging
/// point behind it, then pull FORWARD onto the marking, coming to rest lined up straight along the
/// sub-lane with the NOSEWHEEL on the spot (centroid a half-fuselage back) and the nose facing OUT
/// toward the parent taxiway. Before the fix, <see cref="PushbackPhase"/> reversed the aircraft's
/// CENTROID directly onto the spot node at whatever arbitrary heading the reverse arc left it — the
/// pushback twin of the taxi-to-spot centroid-on-spot defect fixed in issue #234.
///
/// Geometry (real SFO layout): spot 7A (node 6) sits on ramp sub-lane T7A. Its outbound edge points at
/// the A/T7A junction (bearing ~27°, the movement area); the reciprocal (~207°) leads deeper into the
/// RAMP. Nose-out therefore faces ~27°, and the staging point sits along ~207° behind the marking.
/// </summary>
public class PushToSpotLineupTests(ITestOutputHelper output)
{
    private const string Pushed = "PSH1";

    [Fact]
    public void PushToSpot7A_EndsNoseOutNosewheelOnSpot_ViaReversePastThenForward()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("PushbackPhase", LogLevel.Debug).InitializeSimLog();

        GroundNode? spot = layout.FindSpotNodeByName("7A");
        GroundNode? aJunction = layout.FindIntersectionNode("A", "T7A");
        if (spot is null || aJunction is null)
        {
            return;
        }

        // Outbound (nose-out) heading: from the spot toward the A/T7A junction (the movement area).
        double hdgOut = GeoMath.BearingTo(spot.Position, aJunction.Position);
        double hdgIntoRamp = new TrueHeading(hdgOut).ToReciprocal().Degrees;

        double lengthFt = FaaAircraftDatabase.Get("CRJ2")?.LengthFt ?? 88.0;
        double halfLenFt = lengthFt / 2.0;

        // Start the aircraft off to one side of the spot (25° off the sub-lane axis, toward taxiway A),
        // nose facing out — as if it just came off a gate and must be pushed back into the ramp spot.
        // Off-axis is deliberate: pre-fix the reverse ends the aircraft facing start->spot's reciprocal
        // (not the sub-lane's out-heading), so the facing assertion is a real red/green discriminator.
        double startBearing = new TrueHeading(hdgOut - 45.0).Degrees;
        LatLon startPos = GeoMath.ProjectPoint(spot.Position, new TrueHeading(startBearing), 150.0 / GeoMath.FeetPerNm);

        var engine = new SimulationEngine(groundData);
        engine.Scenario = MakeScenario();

        AircraftState ac = MakeGroundAircraft(Pushed, "CRJ2", startPos, new TrueHeading(hdgOut), layout, new AtParkingPhase());
        engine.World.AddAircraft(ac);

        CommandResult cmd = engine.SendCommand(Pushed, "PUSH $7A");
        Assert.True(cmd.Success, $"PUSH command failed: {cmd.Message}");

        // Signed depth is negative out toward the taxiway (start side) and positive behind the spot in
        // the ramp, so the max over the whole run is the staging point — no need to gate on passing.
        double maxDepthIntoRampFt = double.MinValue; // deepest the centroid gets behind the spot
        double finalDistFt = double.NaN;
        double finalHdg = double.NaN;
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            AircraftState? a = engine.FindAircraft(Pushed);
            if (a is null)
            {
                break;
            }

            double distFt = GeoMath.DistanceNm(a.Position, spot.Position) * GeoMath.FeetPerNm;
            maxDepthIntoRampFt = Math.Max(maxDepthIntoRampFt, DepthIntoRamp(a.Position, spot.Position, hdgIntoRamp));

            bool done = a.Phases?.CurrentPhase is HoldingAfterPushbackPhase && a.GroundSpeed < 0.5;
            if (done && t > 3)
            {
                finalDistFt = distFt;
                finalHdg = a.TrueHeading.Degrees;
                break;
            }
        }

        output.WriteLine(
            $"PUSH $7A: rest {finalDistFt:F0}ft from spot (half-length {halfLenFt:F0}ft), nose {finalHdg:F0}° "
                + $"(out={hdgOut:F0}°), deepest-behind {maxDepthIntoRampFt:F0}ft."
        );

        Assert.False(double.IsNaN(finalDistFt), "aircraft never settled at the spot");

        // 1. Nosewheel on the spot: centroid rests ~half a fuselage behind the marking (not centered on
        //    it, the pre-fix ~0-3 ft).
        Assert.True(
            finalDistFt >= halfLenFt - 15.0 && finalDistFt <= halfLenFt + 30.0,
            $"rested {finalDistFt:F0}ft from the spot — expected a nose-at-spot setback of ~{halfLenFt:F0}ft. "
                + "Centroid-on-spot juts the fuselage a half-length past the marking toward taxiway A."
        );

        // 2. Lined up straight, nose facing OUT toward the taxiway (pre-fix: arbitrary heading).
        double hdgErr = new TrueHeading(hdgOut).AbsAngleTo(new TrueHeading(finalHdg));
        Assert.True(hdgErr <= 12.0, $"nose ended {finalHdg:F0}° — expected ~{hdgOut:F0}° (out toward the taxiway), off by {hdgErr:F0}°.");

        // 3. Reversed PAST the spot, then pulled forward: the centroid went deeper into the ramp than its
        //    final rest before coming forward onto the marking (pre-fix: single reverse straight to the
        //    spot, never overshooting behind it).
        Assert.True(
            maxDepthIntoRampFt > finalDistFt + 20.0,
            $"no reverse-past-then-forward: deepest-behind was {maxDepthIntoRampFt:F0}ft vs final {finalDistFt:F0}ft. "
                + "Expected the tug to overshoot behind the spot then pull forward to line up."
        );
    }

    /// <summary>
    /// A snapshot taken part-way along the creep pull onto the spot must round-trip field for field, and the
    /// restored creep — flown on its own from the pose the original had — must still finish with the nosewheel on
    /// the mark (guards the move's DTO fields against a future serialization regression).
    /// </summary>
    [Fact]
    public void PushToSpot7A_SnapshotMidCreep_RestoresAndFinishesOnTheMark()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        GroundNode? spot = layout.FindSpotNodeByName("7A");
        GroundNode? aJunction = layout.FindIntersectionNode("A", "T7A");
        if (spot is null || aJunction is null)
        {
            return;
        }

        double hdgOut = GeoMath.BearingTo(spot.Position, aJunction.Position);
        LatLon startPos = GeoMath.ProjectPoint(spot.Position, new TrueHeading(hdgOut), 140.0 / GeoMath.FeetPerNm);

        var engine = new SimulationEngine(groundData);
        engine.Scenario = MakeScenario();
        AircraftState ac = MakeGroundAircraft(Pushed, "CRJ2", startPos, new TrueHeading(hdgOut), layout, new AtParkingPhase());
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand(Pushed, "PUSH $7A").Success);

        // Tick until the pushback is part-way along its creep pull onto the spot.
        PushbackPhase? creep = null;
        for (int t = 1; t <= 240 && creep is null; t++)
        {
            engine.TickOneSecond();
            if (
                (ac.Phases?.CurrentPhase is PushbackPhase { Kind: PushbackLegKind.Pull, Move.Creep: true } p)
                && (p.ToSnapshot() is PushbackPhaseDto { ProgressDistanceFt: > 2.0 })
            )
            {
                creep = p;
            }
        }

        Assert.NotNull(creep);
        PushbackPhaseDto midDto = Assert.IsType<PushbackPhaseDto>(creep.ToSnapshot());
        var restored = PushbackPhase.FromSnapshot(midDto);
        PushbackPhaseDto reDto = Assert.IsType<PushbackPhaseDto>(restored.ToSnapshot());
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize<PhaseDto>(midDto, RecordingJsonOptions.Default),
            System.Text.Json.JsonSerializer.Serialize<PhaseDto>(reDto, RecordingJsonOptions.Default)
        );
        Assert.Equal(creep.Move, restored.Move);
        Assert.Equal(creep.PlannedEnd, restored.PlannedEnd);

        // Fly the restored creep in its own engine from the pose the original had; the phase is installed active,
        // as a restore leaves it, so it is not restarted.
        var twinEngine = new SimulationEngine(groundData);
        twinEngine.Scenario = MakeScenario();
        AircraftState twin = MakeGroundAircraft("TWIN1", "CRJ2", ac.Position, ac.TrueHeading, layout, new HoldingAfterPushbackPhase());
        twin.Phases = new PhaseList();
        twin.Phases.Add(restored);
        twin.IndicatedAirspeed = ac.IndicatedAirspeed;
        twinEngine.World.AddAircraft(twin);
        for (int t = 1; t <= 120 && twin.Phases.CurrentPhase is PushbackPhase; t++)
        {
            twinEngine.TickOneSecond();
        }

        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outboundDeg), "spot 7A has no outbound heading");
        (LatLon stop, LatLon _) = TugMovePlanner.SpotStopGeometry(spot, outboundDeg, "CRJ2");
        double offStopFt = GeoMath.DistanceNm(twin.Position, stop) * GeoMath.FeetPerNm;
        double offNoseDeg = new TrueHeading(outboundDeg).AbsAngleTo(twin.TrueHeading);
        output.WriteLine($"restored creep ended {offStopFt:F2} ft off the stop point, nose {offNoseDeg:F2}° off nose-out");
        Assert.False(twin.Phases.CurrentPhase is PushbackPhase, "the restored creep never finished");
        Assert.True(offStopFt <= 3.0, $"the restored creep ended {offStopFt:F2} ft off the stop point");
        Assert.True(offNoseDeg <= 1.0, $"the restored creep ended with the nose {offNoseDeg:F2}° off nose-out");
    }

    /// <summary>
    /// A ramp spot is a marking the tug positions the aircraft onto and it waits there for instructions —
    /// not a stand it parks on. <c>PUSH $spot</c> must therefore hand over to
    /// <see cref="HoldingAfterPushbackPhase"/>, not <see cref="AtParkingPhase"/> (which is what
    /// <c>PUSH @gate</c> still ends in).
    /// </summary>
    [Fact]
    public void PushToSpot_CompletesInHoldingAfterPushback_NotAtParking()
    {
        SpotPushWorld? world = BuildSpotPushWorld();
        if (world is null)
        {
            return;
        }

        Assert.True(world.Value.Engine.SendCommand(Pushed, "PUSH $7A").Success);

        Phase? terminal = TickPushToRest(world.Value.Engine);
        Assert.True(terminal is not null, "the push never came to rest within 240s");
        Assert.IsType<HoldingAfterPushbackPhase>(terminal);
    }

    /// <summary>
    /// The aircraft was standing at a gate when the push was issued, so <c>Ground.ParkingSpot</c> names that
    /// gate. Once it has been pushed onto a ramp spot it has left the stand and is not on another one, so the
    /// field must be cleared — leaving the origin gate there reports a stand the aircraft no longer occupies.
    /// </summary>
    [Fact]
    public void PushToSpot_ClearsOriginGateFromParkingSpot()
    {
        SpotPushWorld? world = BuildSpotPushWorld();
        if (world is null)
        {
            return;
        }

        (SimulationEngine? engine, AircraftState? ac, AirportGroundLayout? layout, GroundNode _) = world.Value;

        // The stand the aircraft is pushing off: the nearest real parking node to where it starts.
        GroundNode? originGate = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && !string.IsNullOrWhiteSpace(n.Name))
            .OrderBy(n => GeoMath.DistanceNm(ac.Position, n.Position))
            .FirstOrDefault();
        Assert.True(originGate is not null, "the SFO layout has no named parking node to start the push from");
        ac.Ground.ParkingSpot = originGate!.Name;
        output.WriteLine($"origin gate {originGate.Name} set as ParkingSpot before PUSH $7A");

        Assert.True(engine.SendCommand(Pushed, "PUSH $7A").Success);
        Phase? terminal = TickPushToRest(engine);
        Assert.True(terminal is not null, "the push never came to rest within 240s");

        Assert.True(
            ac.Ground.ParkingSpot is null,
            $"after being pushed onto spot 7A the aircraft still reports ParkingSpot='{ac.Ground.ParkingSpot}' — "
                + "it has left that stand, and a ramp spot is not a stand"
        );
    }

    /// <summary>
    /// Holding on a spot, the aircraft can be pushed again: the terminal phase of a spot push accepts a fresh
    /// <c>PUSH</c> (see <see cref="HoldingAfterPushbackPhase.CanAcceptCommand"/>), so an RPO can reposition an
    /// aircraft that is waiting on a marking.
    /// </summary>
    [Fact]
    public void PushToSpot_ThenPushAgainIsAccepted()
    {
        SpotPushWorld? world = BuildSpotPushWorld();
        if (world is null)
        {
            return;
        }

        (SimulationEngine? engine, AircraftState? ac, AirportGroundLayout? layout, GroundNode? spot) = world.Value;
        Assert.True(engine.SendCommand(Pushed, "PUSH $7A").Success);
        Assert.True(TickPushToRest(engine) is not null, "the push never came to rest within 240s");

        // Any other named spot on the layout: the second push only has to be accepted, not to be short.
        GroundNode? nextSpot = layout
            .Nodes.Values.Where(n =>
                (n.Type == GroundNodeType.Spot)
                && !string.IsNullOrWhiteSpace(n.Name)
                && !string.Equals(n.Name, spot.Name, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(n => GeoMath.DistanceNm(ac.Position, n.Position))
            .FirstOrDefault();
        Assert.True(nextSpot is not null, "the SFO layout has only one named spot node");

        CommandResult again = engine.SendCommand(Pushed, $"PUSH ${nextSpot!.Name}");
        output.WriteLine($"second push 'PUSH ${nextSpot.Name}' from {ac.Phases?.CurrentPhase?.Name}: {again.Message}");
        Assert.True(again.Success, $"a second PUSH after a spot push was refused: {again.Message}");
    }

    /// <summary>The SFO world a spot push runs in: the engine, the pusher, the layout, and spot 7A.</summary>
    private readonly record struct SpotPushWorld(SimulationEngine Engine, AircraftState Aircraft, AirportGroundLayout Layout, GroundNode Spot);

    /// <summary>
    /// Builds the world of <see cref="PushToSpot7A_EndsNoseOutNosewheelOnSpot_ViaReversePastThenForward"/>: a
    /// CRJ2 at parking 150 ft off to one side of SFO spot 7A, nose out toward taxiway A, ready to be pushed
    /// onto the spot. Null when navdata or the SFO layout is unavailable (the silent-skip convention).
    /// </summary>
    /// <returns>The built world, or null to skip.</returns>
    private SpotPushWorld? BuildSpotPushWorld()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        GroundNode? spot = layout.FindSpotNodeByName("7A");
        GroundNode? aJunction = layout.FindIntersectionNode("A", "T7A");
        if (spot is null || aJunction is null)
        {
            return null;
        }

        double hdgOut = GeoMath.BearingTo(spot.Position, aJunction.Position);
        double startBearing = new TrueHeading(hdgOut - 45.0).Degrees;
        LatLon startPos = GeoMath.ProjectPoint(spot.Position, new TrueHeading(startBearing), 150.0 / GeoMath.FeetPerNm);

        var engine = new SimulationEngine(groundData);
        engine.Scenario = MakeScenario();
        AircraftState ac = MakeGroundAircraft(Pushed, "CRJ2", startPos, new TrueHeading(hdgOut), layout, new AtParkingPhase());
        engine.World.AddAircraft(ac);
        return new SpotPushWorld(engine, ac, layout, spot);
    }

    /// <summary>
    /// Ticks until the pushback has run and the aircraft has come to rest, and returns the phase it handed
    /// over to — deliberately phase-agnostic, so it reports whichever terminal the handler produced.
    /// </summary>
    /// <param name="engine">Engine carrying the pusher.</param>
    /// <returns>The terminal phase, or null if the push never settled within 240s.</returns>
    private static Phase? TickPushToRest(SimulationEngine engine)
    {
        bool everPushed = false;
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            AircraftState? ac = engine.FindAircraft(Pushed);
            if (ac is null)
            {
                break;
            }

            everPushed |= ac.Phases?.CurrentPhase is PushbackPhase;
            if (everPushed && ac.Phases?.CurrentPhase is not PushbackPhase && ac.GroundSpeed < 0.5)
            {
                return ac.Phases?.CurrentPhase;
            }
        }

        return null;
    }

    /// <summary>
    /// Signed distance of a position "into the ramp" from the spot along the inbound (ramp-side) axis.
    /// Positive = behind the spot (ramp side); negative = out toward the taxiway.
    /// </summary>
    private static double DepthIntoRamp(LatLon pos, LatLon spot, double hdgIntoRamp)
    {
        double distFt = GeoMath.DistanceNm(pos, spot) * GeoMath.FeetPerNm;
        double bearing = GeoMath.BearingTo(spot, pos);
        double err = new TrueHeading(hdgIntoRamp).AbsAngleTo(new TrueHeading(bearing));
        return err <= 90.0 ? distFt : -distFt;
    }

    private static SimScenarioState MakeScenario() =>
        new()
        {
            ScenarioId = "push-spot-synth",
            ScenarioName = "push-spot-synth",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
            AutoCrossRunway = false,
        };

    private static AircraftState MakeGroundAircraft(
        string callsign,
        string type,
        LatLon pos,
        TrueHeading hdg,
        AirportGroundLayout layout,
        Phase startPhase
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pos,
            TrueHeading = hdg,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(startPhase);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, layout));
        ac.Ground.Layout = layout;
        return ac;
    }
}
