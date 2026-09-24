using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #454: a numbered taxilane named in the clearance is driven, never swapped for its parallel sibling. SFO gate D1
/// hangs off T5B's ramp; <c>TAXI B K A T5A @D1</c> resolved to "B K A T5B" with no T5A edge driven, because the
/// parking-stop picker accepted the A/T5A junction node as a "stop on T5A" and extended down T5B. Wanted: drive
/// T5A, then cut across the apron from T5A to the stand, rolling in on its heading; the readback stays the clearance
/// as issued, ramp taxilanes the driven path adds are silent, and a movement-area taxiway the clearance did not name
/// is warned.
/// </summary>
public class Issue454NamedTaxilaneHonouredTests(ITestOutputHelper output)
{
    /// <summary>SKW3398 at bundle t≈878: holding short of K on B, nosed 297.9° true.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) Skw3398HeldShortOfKPose = (
        new LatLon(37.621827131692896, -122.38554745714856),
        new TrueHeading(297.9)
    );

    /// <summary>UAL2627 at bundle t=1072: holding short of B4 on B, nosed 207.8° true.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) Ual2627HeldShortOfB4Pose = (
        new LatLon(37.62151440020498, -122.39258499943642),
        new TrueHeading(207.76920356327798)
    );

    private const int KOnBJunctionNode = 135;
    private const int B4OnBHoldNode = 1355;

    /// <summary>
    /// D1 is 321 ft across the apron from T5A, so the cut goes to the stand, rolling in on the stand heading, and the
    /// aircraft parks lined up with it. F1 is 521 ft from its nearest
    /// T8 node, beyond <see cref="RampLaneReposition.MaxCrossingFt"/>, so the cut crosses onto T9 and follows F1's
    /// lead-in — T8 is still driven first.
    /// </summary>
    [Theory]
    [InlineData("SKW3398", "E75L", "K", KOnBJunctionNode, "TAXI B K A T5A @D1", "T5A", "T5B", "D1", "Taxi via B K A T5A @D1", true)]
    [InlineData("UAL2627", "A320", "B4", B4OnBHoldNode, "TAXI B4 T8 @F1", "T8", "T9", "F1", "Taxi via B B4 T8 @F1", false)]
    public void Taxi_NamedTaxilane_IsDriven_ThenCutsAcrossTheApron(
        string callsign,
        string type,
        string heldShortOf,
        int holdNode,
        string command,
        string named,
        string sibling,
        string stand,
        string readback,
        bool directCut
    )
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("RampLaneReposition", LogLevel.Debug).InitializeSimLog();
        (LatLon Position, TrueHeading Heading) pose = callsign == "SKW3398" ? Skw3398HeldShortOfKPose : Ual2627HeldShortOfB4Pose;
        var hold = new HoldShortPoint
        {
            NodeId = holdNode,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = heldShortOf,
        };
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            callsign,
            type,
            (ground.Layout.Nodes[holdNode], pose.Heading),
            new HoldingShortPhase(hold)
        );
        aircraft.Position = pose.Position;
        aircraft.Ground.CurrentTaxiway = "B";

        CommandResult result = ground.Engine.SendCommand(callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        foreach (string warning in route.Warnings)
        {
            output.WriteLine($"warning: {warning}");
        }

        int lastNamed = route.Segments.FindLastIndex(s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway(named));
        Assert.True(lastNamed >= 0, $"{named} was named in the clearance but no {named} edge is driven: {route.FormatTaxiwaySequence()}");

        GroundNode standNode = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName(stand));
        Assert.Equal(standNode.Id, route.Segments[^1].ToNodeId);
        int crossingIndex = route.Segments.FindLastIndex(s => VirtualNode.IsVirtualEdge(s.Edge.Edge));
        Assert.True(crossingIndex > lastNamed, $"the route should leave {named} across the apron: {route.FormatTaxiwaySequence()}");
        if (directCut)
        {
            AssertRollsInToStand(route, standNode, lastNamed);
            Assert.DoesNotContain(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway(sibling));
        }
        else
        {
            Assert.DoesNotContain(route.Segments.Take(crossingIndex), s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway(sibling));
        }

        Assert.Equal(readback, Assert.IsType<string>(result.Message).Split(" [")[0]);

        var classification = MovementAreaClassification.For(ground.Layout);
        var cleared = new HashSet<string>(readback["Taxi via ".Length..].Split(' '), StringComparer.OrdinalIgnoreCase);
        var drivenStraight = route
            .Segments.Where(s => (s.Edge.Edge is not GroundArc) && !VirtualNode.IsVirtualEdge(s.Edge.Edge))
            .Select(s => s.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string name in drivenStraight.Where(n => !cleared.Contains(n)))
        {
            string phrase = $"taxiing via {name} — not in the route issued";
            if (classification.IsMovementArea(name))
            {
                Assert.Contains(phrase, route.Warnings);
            }
            else
            {
                Assert.DoesNotContain(phrase, route.Warnings);
            }
        }

        Assert.DoesNotContain(route.Warnings, w => w.Contains(sibling, StringComparison.OrdinalIgnoreCase));
        if (directCut)
        {
            AssertParksOnStandHeading(ground, aircraft, standNode);
        }
    }

    /// <summary>
    /// The route ends in the two free-space legs, both after the named lane: across the apron to the roll-in point,
    /// then in to the stand on its heading.
    /// </summary>
    private static void AssertRollsInToStand(TaxiRoute route, GroundNode stand, int lastNamed)
    {
        TaxiRouteSegment crossing = route.Segments[^2];
        TaxiRouteSegment rollIn = route.Segments[^1];
        Assert.True(route.Segments.Count - 2 > lastNamed, $"the crossing should follow the named lane: {route.FormatTaxiwaySequence()}");
        Assert.True(VirtualNode.IsVirtualEdge(crossing.Edge.Edge), $"the second-last segment should be the apron crossing: {crossing.TaxiwayName}");
        Assert.True(VirtualNode.IsVirtualEdge(rollIn.Edge.Edge), $"the last segment should be the roll-in: {rollIn.TaxiwayName}");
        Assert.Equal("RAMP", crossing.TaxiwayName);
        Assert.Equal("RAMP", rollIn.TaxiwayName);
        Assert.Equal(crossing.ToNodeId, rollIn.FromNodeId);
        Assert.Equal(stand.Id, rollIn.ToNodeId);
        double standHeading = Assert.IsType<TrueHeading>(stand.TrueHeading).Degrees;
        Assert.True(
            GeoMath.AbsBearingDifference(rollIn.Edge.ArrivalBearing, standHeading) <= 1.0,
            $"the roll-in runs {rollIn.Edge.ArrivalBearing:F1}°, not on the {standHeading:F1}° stand heading"
        );
    }

    /// <summary>The taxi completes with the aircraft parked on the stand, nosed within 2° of the stand heading.</summary>
    private void AssertParksOnStandHeading(SfoGround ground, AircraftState aircraft, GroundNode stand)
    {
        LatLon previous = aircraft.Position;
        double drivenFt = 0.0;
        int parkedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is AtParkingPhase,
            400,
            _ =>
            {
                drivenFt += GeoMath.DistanceNm(previous, aircraft.Position) * GeoMath.FeetPerNm;
                previous = aircraft.Position;
            }
        );
        double standHeading = Assert.IsType<TrueHeading>(stand.TrueHeading).Degrees;
        double errorDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, standHeading);
        double offStandFt = GeoMath.DistanceNm(aircraft.Position, stand.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"{aircraft.Callsign} at {stand.Name} after {parkedAt}s: heading {aircraft.TrueHeading.Degrees:F1}° (stand {standHeading:F1}°), "
                + $"driven {drivenFt:F0} ft, {offStandFt:F0} ft off the stand, phase {aircraft.Phases?.CurrentPhase?.Name}"
        );
        Assert.True(parkedAt > 0, $"{aircraft.Callsign} never parked at {stand.Name}: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(errorDeg <= 2.0, $"{aircraft.Callsign} parked at {aircraft.TrueHeading.Degrees:F1}°, {errorDeg:F1}° off the stand heading");
    }

    /// <summary>
    /// The planner's direct cut to D1 rolls in on the stand heading: the apron crossing from T5A ends one E75L
    /// fuselage (106.0 ft in the FAA ACD) out from D1 on the reciprocal of its 345° heading, and the last leg runs in
    /// from there.
    /// </summary>
    [Fact]
    public void DirectStandCut_RollsInOnStandHeading()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode d1 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("D1"));
        double fuselageFt = TugMovePlanner.FuselageLengthFt("E75L");
        RampLaneDestinationCutPlan? plan = RampLaneReposition.TryPlanDestinationCut(
            ground.Layout,
            new RampLaneDestinationCutRequest
            {
                StartNodeId = KOnBJunctionNode,
                Path = ["B", "K", "A", "T5A"],
                Destination = d1,
                Options = new ExplicitPathOptions { OccupiedTaxiway = "B", StartHeadingTrue = Skw3398HeldShortOfKPose.Heading.Degrees },
                Category = AircraftCategory.Jet,
                AircraftLengthFt = fuselageFt,
            }
        );
        Assert.NotNull(plan);
        SfoGroundHarness.DumpRoute(output, plan.Route);
        TaxiRouteSegment rollIn = plan.Route.Segments[^1];
        Assert.Equal(d1.Id, rollIn.ToNodeId);
        Assert.True(VirtualNode.IsVirtualEdge(rollIn.Edge.Edge), "the last leg should be the free-space roll-in");
        LatLon approach = rollIn.Edge.FromNode.Position;
        double outFt = GeoMath.DistanceNm(d1.Position, approach) * GeoMath.FeetPerNm;
        double bearingDeg = GeoMath.BearingTo(d1.Position, approach);
        output.WriteLine($"approach point {outFt:F1} ft from D1 on {bearingDeg:F1}°; plan crossing {plan.CrossingFt:F0} ft from #{plan.FromNode.Id}");
        Assert.Equal("T5A", plan.Lane);
        Assert.Equal(106.0, fuselageFt, 0.05);
        Assert.InRange(outFt, fuselageFt - 1.0, fuselageFt + 1.0);
        Assert.True(GeoMath.AbsBearingDifference(bearingDeg, 165.0) <= 1.0, $"the approach point is on {bearingDeg:F1}° from D1, not 165°");
    }

    /// <summary>OAK DAL2150 resting after a plain PUSH off gate 15 (see <c>TaxiApproachLegTests</c>).</summary>
    private static readonly (LatLon Position, TrueHeading Heading) OakPushedOffGate15Pose = (
        new LatLon(37.710217680439534, -122.21728593336832),
        new TrueHeading(53.0)
    );

    /// <summary>
    /// The pilot reads back the clearance as issued, like the controller echo: the ramp lane the driven path adds
    /// (T5B, T9) and the runway-entry connector the resolver extends onto (OAK W1) are never spoken or shown.
    /// </summary>
    [Fact]
    public void PilotTaxiReadback_OmitsAddedLanesAndConnectors()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AssertSfoReadback(ground, "SKW3398", "E75L", "K", KOnBJunctionNode, "TAXI B K A T5A @D1", ["B", "K", "A", "T5A"], ["T5B"]);
        AssertSfoReadback(ground, "UAL2627", "A320", "B4", B4OnBHoldNode, "TAXI B4 T8 @F1", ["B4", "T8"], ["T9"]);

        AirportGroundLayout? oak = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(oak);
        var aircraft = new AircraftState
        {
            Callsign = "DAL2150",
            AircraftType = "A319",
            Position = OakPushedOffGate15Pose.Position,
            TrueHeading = OakPushedOffGate15Pose.Heading,
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, oak));
        aircraft.Ground.Layout = oak;
        CompoundCommand compound = ParseCompound("TAXI U W RWY 30");
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(compound.Blocks[0].Commands[0]), oak);
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Contains(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("W1"));
        AssertReadbackNames(compound, result, aircraft, ["U", "W"], ["W1"]);
    }

    private void AssertSfoReadback(
        SfoGround ground,
        string callsign,
        string type,
        string heldShortOf,
        int holdNode,
        string command,
        string[] named,
        string[] unnamed
    )
    {
        (LatLon Position, TrueHeading Heading) pose = callsign == "SKW3398" ? Skw3398HeldShortOfKPose : Ual2627HeldShortOfB4Pose;
        var hold = new HoldShortPoint
        {
            NodeId = holdNode,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = heldShortOf,
        };
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            callsign,
            type,
            (ground.Layout.Nodes[holdNode], pose.Heading),
            new HoldingShortPhase(hold)
        );
        aircraft.Position = pose.Position;
        aircraft.Ground.CurrentTaxiway = "B";

        CommandResult result = ground.Engine.SendCommand(callsign, command);
        Assert.True(result.Success, result.Message);
        AssertReadbackNames(ParseCompound(command), result, aircraft, named, unnamed);
    }

    private void AssertReadbackNames(CompoundCommand compound, CommandResult result, AircraftState aircraft, string[] named, string[] unnamed)
    {
        PilotSpeechText readback = Assert.IsType<PilotSpeechText>(
            PilotResponder.BuildReadbackAsApplied(compound, result, aircraft, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate)
        );
        output.WriteLine($"{aircraft.Callsign} terminal: {readback.TerminalForRpo}");
        output.WriteLine($"{aircraft.Callsign} tts: {readback.Tts}");
        foreach (string name in named)
        {
            Assert.Matches($@"(?<![A-Za-z0-9]){name}(?![A-Za-z0-9])", readback.Terminal);
        }

        foreach (string name in unnamed)
        {
            Assert.DoesNotMatch($@"(?<![A-Za-z0-9]){name}(?![A-Za-z0-9])", readback.Terminal);
            Assert.DoesNotMatch($@"(?<![A-Za-z0-9]){name}(?![A-Za-z0-9])", readback.TerminalForRpo);
            Assert.DoesNotContain(SpokenTaxiway(name), readback.Tts, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static CompoundCommand ParseCompound(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command, "");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return parsed.Value!;
    }

    /// <summary>The spoken form of a letters-and-digits taxiway name: <c>T5B</c> → <c>tango five bravo</c>.</summary>
    private static string SpokenTaxiway(string name) =>
        string.Join(
            " ",
            name.Select(c =>
                c switch
                {
                    'B' => "bravo",
                    'T' => "tango",
                    'W' => "whiskey",
                    '1' => "one",
                    '5' => "five",
                    '9' => "nine",
                    _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no spoken form in this test"),
                }
            )
        );
}
