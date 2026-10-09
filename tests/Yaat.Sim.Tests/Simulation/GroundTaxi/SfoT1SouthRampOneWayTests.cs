using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// SFO Terminal 1 south ramp (the stands off M1-M5): by local procedure aircraft enter on M1 and leave on M2, and
/// super-class aircraft use M1 both ways. The sidecar makes the ramp portion of M1 one-way inbound (supers exempt) and
/// M2 one-way outbound. A gate clearance naming only A is extended into the ramp on M1, which the clearance did not name:
/// the readback shows M1 and the controller is told <c>M1 not in clearance</c>. A clearance naming M2 inbound is flown
/// as cleared, with the one-way warning. The implied lane never carries a route to a runway holding position.
/// Departures pushed off B2 onto M5 reach a taxiway beyond the start bridge's reach through the auto-routed start leg:
/// out on M2, or on M1 for a super, for which M2 is closed both ways.
/// </summary>
public class SfoT1SouthRampOneWayTests(ITestOutputHelper output)
{
    private const string M1NotInClearance = "M1 not in clearance";

    /// <summary>
    /// WJA1508's clearance from the bug bundle: <c>TAXI T A @B2</c> after exiting 28R resolved as <c>T A M2 M5 @B2</c>,
    /// entering the ramp against the M2 outbound flow. It must enter on M1 and say so.
    /// </summary>
    [Fact]
    public void TaxiTA_ToB2_B738_EntersOnM1_WithAdvisory()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (CommandResult result, TaxiRoute route) = TaxiFromT28RExit(
            layout,
            "B738",
            new TaxiCommand(Path: ["T", "A"], HoldShorts: [], DestinationParking: "B2")
        );

        AssertReachesParking(layout, route, "B2");
        Assert.True(DrivesStraight(route, "M1"), "the route must enter the ramp on M1");
        Assert.False(DrivesStraight(route, "M2"), "the route must not enter the ramp on M2 (one-way outbound)");
        Assert.Contains(M1NotInClearance, route.Warnings);
        Assert.Equal(["M1"], route.ImpliedLanes);
        Assert.Contains("A M1", result.Message);
        Assert.Contains("@B2", result.Message);
    }

    /// <summary>A super (A388) cannot enter on M2 either: <c>TAXI T A @B2</c> enters on M1.</summary>
    [Fact]
    public void TaxiTA_ToB2_A388_EntersOnM1()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (_, TaxiRoute route) = TaxiFromT28RExit(layout, "A388", new TaxiCommand(Path: ["T", "A"], HoldShorts: [], DestinationParking: "B2"));

        AssertReachesParking(layout, route, "B2");
        Assert.True(DrivesStraight(route, "M1"), "the super must enter the ramp on M1");
        Assert.False(DrivesStraight(route, "M2"), "the super must not enter the ramp on M2");
    }

    /// <summary>
    /// An explicit clearance naming M2 inbound (<c>TAXI T A M2 M5 @B2</c>) is the controller's call: it is flown as
    /// cleared, with the one-way warning, and no lane is implied.
    /// </summary>
    [Fact]
    public void TaxiTAM2M5_ToB2_FlownAsCleared_WithOneWayWarning()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (_, TaxiRoute route) = TaxiFromT28RExit(
            layout,
            "B738",
            new TaxiCommand(Path: ["T", "A", "M2", "M5"], HoldShorts: [], DestinationParking: "B2")
        );

        AssertReachesParking(layout, route, "B2");
        Assert.True(DrivesStraight(route, "M2"), "the cleared M2 must be driven");
        Assert.False(DrivesStraight(route, "M1"), "no lane is implied when the clearance names the way in");
        Assert.Contains("Taxiing M2 against one-way direction", route.Warnings);
        Assert.DoesNotContain(M1NotInClearance, route.Warnings);
    }

    /// <summary>
    /// M1 continues SE of the A and B junctions to runway 01L's holding position. A gate or spot clearance may imply M1
    /// only into the ramp, never across that holding position: spot 35 sits on M1 7 ft past the 01L bar, so every route
    /// to it ends a segment at the holding position, and <c>TAXI B $35</c> is refused — neither as the one-way lane nor as
    /// the spot's lead-in is M1 implied through the bar.
    /// </summary>
    [Fact]
    public void TaxiB_ToSpot35_NeverImpliesM1AcrossThe01LHold()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot35 = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("35"));
        GroundNode start = NearestNodeOnTaxiway(layout, "B", new LatLon(37.6072, -122.3870));
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["B"],
            out string? failReason,
            new ExplicitPathOptions { OccupiedTaxiway = "B", DestinationHintNode = spot35 },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        output.WriteLine($"route: {route?.FormatTaxiwaySequence() ?? $"none ({failReason})"}");

        GroundNode bar = Assert.Single(spot35.Edges.Select(e => e.OtherNode(spot35)), n => n.Type == GroundNodeType.RunwayHoldShort);
        output.WriteLine($"spot 35 #{spot35.Id} is {GeoMath.DistanceNm(bar.Position, spot35.Position) * GeoMath.FeetPerNm:F0} ft past bar #{bar.Id}");
        Assert.Null(route);
    }

    /// <summary>
    /// A B738 pushed off B2 onto M5 (<c>PUSH M5</c>), then <c>TAXI A</c>. A lies well beyond the start bridge's reach, so
    /// the start leg is auto-routed: along the ramp and out on M2, the one-way outbound lane. M2 has gates one edge off it,
    /// so it classifies as a ramp taxilane (non-movement) and the leg takes it freely, with no advisory. Never out on M1
    /// against its one-way flow, never refused, never held short.
    /// </summary>
    [Fact]
    public void PushedOffB2_TaxiA_B738_LeavesOnM2()
    {
        if (PushOffB2("B738") is not { } pushed)
        {
            return;
        }

        (CommandResult result, TaxiRoute route) = Taxi(pushed, "TAXI A");

        Assert.True(result.Success, result.Message);
        Assert.True(DrivesStraight(route, "M2"), "the departure must leave the ramp on M2");
        Assert.False(DrivesStraight(route, "M1"), "the departure must not leave the ramp on M1 (one-way inbound)");
        Assert.True(DrivesStraight(route, "A"), "the route must reach A");
        Assert.False(DrivesAgainstOneWay(pushed.Layout, route, WakeTurbulenceData.WakeClass.Large), "no segment may run against a one-way lane");
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.DoesNotContain(route.Warnings, w => w.EndsWith("not in clearance", StringComparison.Ordinal));
    }

    /// <summary>A B738 pushed off B2 onto M5, then <c>TAXI $2</c>: out on M2 to spot 2, never on M1.</summary>
    [Fact]
    public void PushedOffB2_TaxiSpot2_B738_LeavesOnM2()
    {
        if (PushOffB2("B738") is not { } pushed)
        {
            return;
        }

        (_, TaxiRoute route) = Taxi(pushed, "TAXI $2");

        GroundNode spot2 = Assert.IsType<GroundNode>(pushed.Layout.FindSpotNodeByName("2"));
        Assert.Contains(route.Segments, s => s.ToNodeId == spot2.Id);
        Assert.True(DrivesStraight(route, "M2"), "the route to spot 2 must run on M2");
        Assert.False(DrivesStraight(route, "M1"), "the route to spot 2 must not run on M1");
        Assert.False(DrivesAgainstOneWay(pushed.Layout, route, WakeTurbulenceData.WakeClass.Large), "no segment may run against a one-way lane");
        AssertStaysInsideTheRamp(pushed.Layout, route, []);
        AssertNoRunway(pushed.Layout, route);
    }

    /// <summary>
    /// A B738 pushed off B2 onto M5, then <c>TAXI $1</c> with no taxiway named. The aircraft starts inside the ramp, so it
    /// stays inside it: spot 1 sits on M1's inbound span, and the route reaches it over the ramp and M1 in M1's own
    /// direction, never on A (movement area, not cleared) and never at a runway holding position.
    /// </summary>
    [Fact]
    public void PushedOffB2_TaxiSpot1_B738_StaysInsideTheRamp()
    {
        if (PushOffB2("B738") is not { } pushed)
        {
            return;
        }

        (CommandResult result, TaxiRoute route) = Taxi(pushed, "TAXI $1");

        GroundNode spot1 = Assert.IsType<GroundNode>(pushed.Layout.FindSpotNodeByName("1"));
        Assert.Contains(route.Segments, s => s.ToNodeId == spot1.Id);
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("A"));
        AssertStaysInsideTheRamp(pushed.Layout, route, ["M1"]);
        Assert.False(DrivesAgainstOneWay(pushed.Layout, route, WakeTurbulenceData.WakeClass.Large), "no segment may run against a one-way lane");
        AssertNoRunway(pushed.Layout, route);
        foreach (TaxiRouteSegment cut in route.Segments.Where(s => IsRampCut(s.Edge.Edge)))
        {
            output.WriteLine(
                $"ramp cut #{cut.FromNodeId} → #{cut.ToNodeId}: {cut.Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft at {cut.Edge.DepartureBearing:F0}°"
            );
        }

        Assert.Contains(route.Segments, s => IsRampCut(s.Edge.Edge));
        Assert.Equal(["M1"], route.ImpliedLanes);
        Assert.Contains(M1NotInClearance, route.Warnings);
        Assert.DoesNotContain(route.Warnings, w => w.EndsWith("not in the route issued", StringComparison.Ordinal));
        Assert.Contains("M1", result.Message);

        // Flown: the aircraft stops on spot 1 lined up along M1 in its inbound direction.
        TaxiRouteSegment arrival = route.Segments.Last(s => s.ToNodeId == spot1.Id);
        SfoGroundHarness.TickUntil(pushed.Ground.Engine, () => pushed.Aircraft.Phases?.CurrentPhase is not TaxiingPhase, 900, null);
        double offSpotFt = GeoMath.DistanceNm(pushed.Aircraft.Position, spot1.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"stopped {offSpotFt:F0} ft from spot 1 facing {pushed.Aircraft.TrueHeading.Degrees:F0}° (M1 inbound {arrival.Edge.ArrivalBearing:F0}°)"
        );
        Assert.IsType<HoldingInPositionPhase>(pushed.Aircraft.Phases?.CurrentPhase);
        Assert.True(offSpotFt <= AircraftLength.ResolveFt("B738"), $"stopped {offSpotFt:F0} ft from spot 1, more than a fuselage");
        Assert.True(GeoMath.AbsBearingDifference(pushed.Aircraft.TrueHeading.Degrees, arrival.Edge.ArrivalBearing) <= 15.0, "lined up along M1");
    }

    /// <summary>
    /// A ramp-confined cut keeps clear of the other aircraft on the ground. A B738 pushed off B2 onto M5 with
    /// <c>TAXI $1</c> crosses the ramp on one straight leg from M2 to the M1 node short of spot 1 — 338 ft to node #416 on
    /// the current layout. Park a B738 on that leg's midpoint, the closest approach to it zero and so inside
    /// <c>ownHalfSpan + otherHalfSpan + WingtipBufferFt</c> by any figure: the cut may not be used, and the re-issued
    /// clearance either re-plans inside the ramp or is refused outright.
    /// </summary>
    [Fact]
    public void PushedOffB2_TaxiSpot1_WithAnAircraftOnTheCut_UsesNoCut()
    {
        if (PushOffB2("B738") is not { } pushed)
        {
            return;
        }

        (_, TaxiRoute clear) = Taxi(pushed, "TAXI $1");
        TaxiRouteSegment cut = Assert.Single(clear.Segments, s => IsRampCut(s.Edge.Edge));
        GroundNode landing = pushed.Layout.Nodes[cut.ToNodeId];
        output.WriteLine(
            $"cut #{cut.FromNodeId} → #{cut.ToNodeId} ({landing.Name}): {cut.Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft at "
                + $"{cut.Edge.DepartureBearing:F0}°"
        );
        Assert.True(landing.Edges.Any(e => e.MatchesTaxiway("M1")), $"the cut must land on M1, not {landing.Name}");

        // Parked on the cut's midpoint: the leg's closest approach to it is zero, inside every wingtip clearance.
        (LatLon midpoint, TrueHeading alongCut) = CutMidpoint(cut);
        SfoGroundHarness.SpawnAt(pushed.Ground, "UAL1", "B738", (VirtualNode.Create(midpoint.Lat, midpoint.Lon), alongCut), new AtParkingPhase());
        output.WriteLine($"UAL1 parked on the cut at {midpoint.Lat:F6},{midpoint.Lon:F6}");

        CommandResult blocked = pushed.Ground.Engine.SendCommand(pushed.Aircraft.Callsign, "TAXI $1");
        output.WriteLine($"'TAXI $1' with the cut blocked: {blocked.Success} — {blocked.Message}");
        if (!blocked.Success)
        {
            Assert.Equal("Unable, need a route to spot 1. To auto-route it: TAXIAUTO $1", blocked.Message);
            return;
        }

        TaxiRoute rerouted = Assert.IsType<TaxiRoute>(pushed.Aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"re-resolved route: {rerouted.FormatTaxiwaySequence()} ({rerouted.Segments.Count} segments)");
        Assert.DoesNotContain(rerouted.Segments, s => (s.FromNodeId == cut.FromNodeId) && (s.ToNodeId == cut.ToNodeId));
        AssertStaysInsideTheRamp(pushed.Layout, rerouted, ["M1"]);
    }

    /// <summary>The midpoint of a cut's straight leg and the bearing along it.</summary>
    private static (LatLon Midpoint, TrueHeading Along) CutMidpoint(TaxiRouteSegment cut)
    {
        LatLon from = cut.Edge.FromNode.Position;
        LatLon to = cut.Edge.ToNode.Position;
        return (new LatLon((from.Lat + to.Lat) / 2.0, (from.Lon + to.Lon) / 2.0), new TrueHeading(GeoMath.BearingTo(from, to)));
    }

    /// <summary>
    /// A B738 pushed off B2 onto M5, then <c>TAXI $35</c>. Spot 35 sits on M1 between the B junction and runway 01L's
    /// holding position; the only ways to it run on B or A, movement area the clearance does not name, so the TAXI is
    /// refused and the aircraft stays where it is.
    /// </summary>
    [Fact]
    public void PushedOffB2_TaxiSpot35_B738_RefusedAndStaysPut()
    {
        if (PushOffB2("B738") is not { } pushed)
        {
            return;
        }

        LatLon before = pushed.Aircraft.Position;
        CommandResult result = pushed.Ground.Engine.SendCommand(pushed.Aircraft.Callsign, "TAXI $35");
        output.WriteLine($"'TAXI $35': {result.Success} — {result.Message}");

        Assert.False(result.Success);
        Assert.Equal("Unable, need a route to spot 35. To auto-route it: TAXIAUTO $35", result.Message);
        Assert.IsType<HoldingAfterPushbackPhase>(pushed.Aircraft.Phases?.CurrentPhase);
        SfoGroundHarness.TickUntil(pushed.Ground.Engine, () => false, 10, null);
        Assert.True(GeoMath.DistanceNm(before, pushed.Aircraft.Position) * GeoMath.FeetPerNm < 1.0, "the refused aircraft must not move");
    }

    /// <summary>
    /// <c>TAXIAUTO</c> is the unrestricted auto-route: a B738 pushed off B2 onto M5 with <c>TAXIAUTO $1</c> takes the best
    /// route, out on M2 and in on A and M1 as before, and is told nothing about the taxiways it did not name.
    /// </summary>
    [Fact]
    public void PushedOffB2_TaxiAutoSpot1_B738_Unconfined_NoAdvisory()
    {
        if (PushOffB2("B738") is not { } pushed)
        {
            return;
        }

        (_, TaxiRoute route) = Taxi(pushed, "TAXIAUTO $1");

        GroundNode spot1 = Assert.IsType<GroundNode>(pushed.Layout.FindSpotNodeByName("1"));
        Assert.Contains(route.Segments, s => s.ToNodeId == spot1.Id);
        Assert.True(DrivesStraight(route, "A"), "the auto-route takes A as before");
        Assert.DoesNotContain(route.Warnings, w => w.EndsWith("not in the route issued", StringComparison.Ordinal));
    }

    /// <summary>
    /// An arrival on T just off 28R with a bare <c>TAXI @B2</c> starts on the movement area, so its route is the auto-route
    /// as before, and every uncleared movement-area taxiway it drives is named in an advisory.
    /// </summary>
    [Fact]
    public void TaxiToB2_FromT28RExit_B738_AdvisesEveryUnclearedTaxiway()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (_, TaxiRoute route) = TaxiFromT28RExit(layout, "B738", new TaxiCommand(Path: [], HoldShorts: [], DestinationParking: "B2"));

        AssertReachesParking(layout, route, "B2");
        Assert.True(DrivesStraight(route, "A"), "the route drives A");
        foreach (string taxiway in new[] { "A", "M1" }.Where(t => DrivesStraight(route, t)))
        {
            Assert.Contains(RouteMaterialiser.NotInRouteIssuedWarning(taxiway), route.Warnings);
        }
    }

    /// <summary>The same arrival with <c>TAXIAUTO @B2</c>: the auto-route raises no advisory.</summary>
    [Fact]
    public void TaxiAutoToB2_FromT28RExit_B738_NoAdvisory()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        AircraftState aircraft = AircraftAtT28RExit(layout, "B738");
        CommandResult result = GroundCommandHandler.TryTaxiAuto(aircraft, new TaxiAutoCommand(null, "B2", null), layout, isScenarioScripted: false);
        TaxiRoute route = AssertAccepted(result, aircraft);

        AssertReachesParking(layout, route, "B2");
        Assert.DoesNotContain(route.Warnings, w => w.EndsWith("not in the route issued", StringComparison.Ordinal));
    }

    /// <summary>
    /// A search with no aircraft (the runway exit walk) sees the constraints a Large aircraft sees: M2's supers-only
    /// closure does not block it, while M2's own one-way still does.
    /// </summary>
    [Fact]
    public void AircraftlessSearch_NotBlockedBySupersOnlyClosure()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        IReadOnlySet<(int From, int To)> aircraftless = OneWayResolver.GetForbiddenMoves(layout, OneWayResolver.AircraftlessWakeClass);
        IReadOnlySet<(int From, int To)> large = OneWayResolver.GetForbiddenMoves(layout, WakeTurbulenceData.WakeClass.Large);
        var supersOnly = OneWayResolver.GetForbiddenMoves(layout, WakeTurbulenceData.WakeClass.Super).Except(large).ToList();

        Assert.NotEmpty(supersOnly);
        Assert.DoesNotContain(supersOnly, aircraftless.Contains);
        Assert.NotEmpty(large);
        Assert.All(large, move => Assert.Contains(move, aircraftless));
    }

    /// <summary>
    /// A super (A388) at stand B2, cleared <c>TAXI A</c>: M2 is closed to supers both ways and M1 is two-way for them, so
    /// the route leaves the ramp along M5 and on M1, with the <c>M1 not in clearance</c> advisory, never on M2.
    /// </summary>
    [Fact]
    public void FromStandB2_TaxiA_A388_LeavesOnM1()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "WJA1509", "A388", "B2");
        (CommandResult result, TaxiRoute route) = Taxi((ground, aircraft, ground.Layout), "TAXI A");

        Assert.True(DrivesStraight(route, "M1"), "the super must leave the ramp on M1");
        Assert.False(DrivesStraight(route, "M2"), "the super must not use M2");
        Assert.True(DrivesStraight(route, "A"), "the route must reach A");
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Contains(M1NotInClearance, route.Warnings);
        Assert.Contains("M1", result.Message);
    }

    /// <summary>
    /// The super pushed off B2 onto M5 and lined up along it, then <c>TAXI A</c>: the route ahead needs a turn about on M5,
    /// which a jet refuses.
    /// </summary>
    [Fact]
    public void PushedOffB2_TaxiA_A388_RefusesToTurnAboutOnM5()
    {
        if (PushOffB2("A388") is not { } pushed)
        {
            return;
        }

        CommandResult result = pushed.Ground.Engine.SendCommand(pushed.Aircraft.Callsign, "TAXI A");
        output.WriteLine($"TAXI A: {result.Success} — {result.Message}");
        Assert.False(result.Success, result.Message);
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("M5"), result.Message);
    }

    /// <summary>
    /// A super inbound with <c>TAXI T A M2 M5 @B2</c>: M2 is closed to supers, but the controller named it, so the route is
    /// flown as cleared with the one-way warning.
    /// </summary>
    [Fact]
    public void TaxiTAM2M5_ToB2_A388_FlownAsCleared_WithOneWayWarning()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (_, TaxiRoute route) = TaxiFromT28RExit(
            layout,
            "A388",
            new TaxiCommand(Path: ["T", "A", "M2", "M5"], HoldShorts: [], DestinationParking: "B2")
        );

        AssertReachesParking(layout, route, "B2");
        Assert.True(DrivesStraight(route, "M2"), "the cleared M2 must be driven");
        Assert.Contains("Taxiing M2 against one-way direction", route.Warnings);
        Assert.DoesNotContain(M1NotInClearance, route.Warnings);
    }

    /// <summary>
    /// A B738 pushed off B2 onto M5, then the scenario's <c>TAXI M4 M1 $1</c>: M1 toward spot 1 runs against its inbound
    /// flow, but the controller named it, so M1 is driven to spot 1 with the one-way warning.
    /// </summary>
    [Fact]
    public void PushedOffB2_TaxiM4M1Spot1_B738_FlownAsCleared_WithOneWayWarning()
    {
        if (PushOffB2("B738") is not { } pushed)
        {
            return;
        }

        (_, TaxiRoute route) = Taxi(pushed, "TAXI M4 M1 $1");

        GroundNode spot1 = Assert.IsType<GroundNode>(pushed.Layout.FindSpotNodeByName("1"));
        Assert.Contains(route.Segments, s => s.ToNodeId == spot1.Id);
        Assert.True(DrivesStraight(route, "M1"), "the cleared M1 must be driven");
        Assert.Contains("Taxiing M1 against one-way direction", route.Warnings);
    }

    /// <summary>
    /// The start leg never implies a lane across a runway holding position. From the M1/A2 junction on the runway side of
    /// M1's 01L holding position, <c>TAXI M5</c> could reach M5 only on M1 back across that holding position, so it is
    /// refused.
    /// </summary>
    [Fact]
    public void TaxiM5_FromBeyondThe01LHoldOnM1_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode start = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway("M1")) && n.Edges.Any(e => e.MatchesTaxiway("A2")))
            .MinBy(n => GeoMath.DistanceNm(n.Position, new LatLon(37.608027, -122.382849)))!;
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["M5"],
            out string? failReason,
            new ExplicitPathOptions { OccupiedTaxiway = null },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        output.WriteLine($"start #{start.Id}; route: {route?.FormatTaxiwaySequence() ?? $"none ({failReason})"}");

        Assert.Null(route);
    }

    private (SfoGround Ground, AircraftState Aircraft, AirportGroundLayout Layout)? PushOffB2(string type)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "WJA1509", type, "B2");
        CommandResult push = ground.Engine.SendCommand(aircraft.Callsign, "PUSH M5");
        output.WriteLine($"PUSH M5: {push.Success} — {push.Message}");
        Assert.True(push.Success, push.Message);
        SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);
        Assert.IsType<HoldingAfterPushbackPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"pushed to {aircraft.Position} facing {aircraft.TrueHeading.Degrees:F1}°");
        return (ground, aircraft, ground.Layout);
    }

    private (CommandResult Result, TaxiRoute Route) Taxi(
        (SfoGround Ground, AircraftState Aircraft, AirportGroundLayout Layout) pushed,
        string command
    )
    {
        CommandResult result = pushed.Ground.Engine.SendCommand(pushed.Aircraft.Callsign, command);
        output.WriteLine($"'{command}'");
        return (result, AssertAccepted(result, pushed.Aircraft));
    }

    private static bool DrivesAgainstOneWay(AirportGroundLayout layout, TaxiRoute route, WakeTurbulenceData.WakeClass wakeClass)
    {
        IReadOnlySet<(int From, int To)> forbidden = OneWayResolver.GetForbiddenMoves(layout, wakeClass);
        return route.Segments.Any(s => forbidden.Contains((s.FromNodeId, s.ToNodeId)));
    }

    private (CommandResult Result, TaxiRoute Route) TaxiFromT28RExit(AirportGroundLayout layout, string type, TaxiCommand taxi)
    {
        AircraftState aircraft = AircraftAtT28RExit(layout, type);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        return (result, AssertAccepted(result, aircraft));
    }

    private static AircraftState AircraftAtT28RExit(AirportGroundLayout layout, string type)
    {
        GroundNode bar = layout
            .GetRunwayHoldShortNodes("28R")
            .Where(n => n.Edges.Any(e => e.MatchesTaxiway("T")))
            .OrderBy(n => n.Position.Lat)
            .First();
        GroundNode onT = bar.Edges.Where(e => e.MatchesTaxiway("T")).Select(e => e.OtherNode(bar)).OrderBy(n => n.Position.Lat).First();
        var heading = new TrueHeading(GeoMath.BearingTo(bar.Position, onT.Position));
        return MakeAircraft(layout, type, (bar.Position, heading), new HoldingInPositionPhase());
    }

    /// <summary>
    /// Every taxiway the route drives is ramp pavement (RAMP, a ramp taxilane, a free-space leg) or one of
    /// <paramref name="allowed"/>; runway pavement is left to <see cref="AssertNoRunway"/>.
    /// </summary>
    private void AssertStaysInsideTheRamp(AirportGroundLayout layout, TaxiRoute route, IReadOnlyCollection<string> allowed)
    {
        var classification = MovementAreaClassification.For(layout);
        var driven = route
            .Segments.Where(s => !s.Edge.Edge.IsRunwayCenterline)
            .SelectMany(s => SegmentExpander.EdgeNames(s.Edge.Edge))
            .Where(n => (n.Length > 0) && classification.IsMovementArea(n) && !allowed.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        output.WriteLine($"movement-area taxiways driven beyond [{string.Join(" ", allowed)}]: [{string.Join(" ", driven)}]");
        Assert.Empty(driven);
    }

    /// <summary>No segment of the route ends at a runway holding position or runs on runway pavement, and it holds short of no runway.</summary>
    private static void AssertNoRunway(AirportGroundLayout layout, TaxiRoute route)
    {
        Assert.DoesNotContain(
            route.Segments,
            s =>
                s.Edge.Edge.IsRunwayCenterline
                || (layout.Nodes.TryGetValue(s.ToNodeId, out GroundNode? n) && (n.Type == GroundNodeType.RunwayHoldShort))
        );
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway);
    }

    /// <summary>A free-space cut across the ramp between two nodes of the ground layout (not the approach leg from the aircraft).</summary>
    private static bool IsRampCut(IGroundEdge edge) => VirtualNode.IsVirtualEdge(edge) && (edge.Nodes[0].Id >= 0) && (edge.Nodes[1].Id >= 0);

    private TaxiRoute AssertAccepted(CommandResult result, AircraftState aircraft)
    {
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        output.WriteLine($"warnings: {string.Join(" | ", route.Warnings)}");
        return route;
    }

    private static void AssertReachesParking(AirportGroundLayout layout, TaxiRoute route, string parking)
    {
        GroundNode stand = Assert.IsType<GroundNode>(layout.FindParkingByName(parking));
        Assert.Equal(stand.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
    }

    private static bool DrivesStraight(TaxiRoute route, string taxiway) =>
        route.Segments.Any(s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway(taxiway));

    private static GroundNode NearestNodeOnTaxiway(AirportGroundLayout layout, string taxiway, LatLon position) =>
        layout.Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway(taxiway))).OrderBy(n => GeoMath.DistanceNm(n.Position, position)).First();

    private static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("SFO");
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, string type, (LatLon Position, TrueHeading Heading) pose, Phase startPhase)
    {
        var aircraft = new AircraftState
        {
            Callsign = "WJA1508",
            AircraftType = type,
            Position = pose.Position,
            TrueHeading = pose.Heading,
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO", Destination = "YYC" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
