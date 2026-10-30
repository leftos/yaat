using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A bare <c>TAXI B</c> given to a B738 rolling west on KOAK C, its nose already inside B's taxiway setback and its centre
/// past the length setback stop (L + 30 ft back from the C/B junction), its nose still short of the junction node. The hold
/// short of B where it stands cannot be made at taxi speed, so the clearance is answered unable and the aircraft brakes at
/// its firm rate onto the stop it can make, as an <c>HS</c> armed past its setback stop does: the route stays on C ending
/// in the hold short of B, the aircraft holds short of B with its nose short of the junction node, and it never takes the
/// clearance as a taxi along B into the junction.
/// </summary>
public class BareTaxiFromRollingOnCTests(ITestOutputHelper output)
{
    private const string Callsign = "SWA1314";
    private const string AircraftType = "B738";

    /// <summary>How far from the commanded speed (kt) the aircraft may be when the clearance is issued.</summary>
    private const double IssueSpeedToleranceKts = 0.6;

    /// <summary>
    /// The margin (ft) the firm braking curve onto a stop reaches zero short of it (<c>GroundNavigator.SetBackStopMarginFt</c>),
    /// plus a foot of rounding.
    /// </summary>
    private const double FirmStopToleranceFt = 3.0;

    /// <summary>How much more (kt/s) a one-second speed drop may be than the firm rate.</summary>
    private const double DecelToleranceKtsPerSec = 0.2;

    /// <summary>
    /// <paramref name="speedKts"/> on C, <c>TAXI B</c> issued the first second the centre is within
    /// <paramref name="issueWithinFt"/> of the C/B junction.
    /// </summary>
    [Theory]
    [InlineData(20.0, 150.0)]
    [InlineData(10.0, 135.0)]
    [InlineData(10.0, 120.0)]
    public void BareTaxiB_RollingOnCInsideTheSetback_IsUnableAndHoldsShortOfTheJunction(double speedKts, double issueWithinFt)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        GroundNode junction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("B", "C"));
        GroundNode caJunction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("A", "C"));
        GroundNode nextOnC = caJunction.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C")).Select(e => e.OtherNode(caJunction)).Single();
        var heading = new TrueHeading(GeoMath.BearingTo(caJunction.Position, nextOnC.Position));
        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        AircraftState aircraft = SpawnRolling(layout, caJunction.Position, heading, speedKts);
        engine.World.AddAircraft(aircraft);

        CommandResult toD = engine.SendCommand(Callsign, "TAXI C D");
        Assert.True(toD.Success, $"'TAXI C D' was refused: {toD.Message}");
        Assert.True(engine.SendCommand(Callsign, $"SPD {speedKts:F0}").Success);

        double lengthFt = AircraftLength.ResolveFt(AircraftType);
        double lengthSetbackFt = lengthFt + HoldShortAnnotator.TaxiwayHoldShortBufferFt;
        (LatLon Position, TrueHeading Heading, double SpeedKts)? issue = null;
        CommandResult? taxiB = null;
        for (int second = 1; (second <= 60) && (taxiB is null); second++)
        {
            engine.TickOneSecond();
            double toJunctionFt = Ft(aircraft.Position, junction.Position);
            if ((toJunctionFt > issueWithinFt) || (Math.Abs(aircraft.GroundSpeed - speedKts) > IssueSpeedToleranceKts))
            {
                continue;
            }

            double noseFt = NoseToCentrelineFt(layout, aircraft, "B");
            double noseShortOfJunctionFt = AlongFt(Nose(aircraft), aircraft.TrueHeading, junction.Position);
            output.WriteLine(
                $"TAXI B at {aircraft.GroundSpeed:F1} kt, centre {toJunctionFt:F1} ft from junction {junction.Id} (length setback "
                    + $"{lengthSetbackFt:F1}), nose {noseShortOfJunctionFt:F1} ft short of it and {noseFt:F1} ft from B's centreline "
                    + $"(setback {TaxiwayNoseSetbackFt(layout):F1})"
            );
            Assert.True(noseFt < TaxiwayNoseSetbackFt(layout), "the nose is not inside B's setback");
            Assert.True(toJunctionFt < lengthSetbackFt, "the centre is not past the length setback stop");
            Assert.True(noseShortOfJunctionFt > 0.0, "the nose is past the junction node");
            issue = (aircraft.Position, aircraft.TrueHeading, aircraft.GroundSpeed);
            taxiB = engine.SendCommand(Callsign, "TAXI B");
        }

        Assert.True((taxiB is not null) && (issue is not null), $"{Callsign} never reached the C/B junction at {speedKts:F0} kt");
        (LatLon Position, TrueHeading Heading, double SpeedKts) issued = issue.Value;
        output.WriteLine($"TAXI B: success={taxiB.Success} '{taxiB.Message}'");
        Assert.True(taxiB.Success, $"'TAXI B' was refused: {taxiB.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.All(route.Segments, s => Assert.False(s.Edge.Edge.MatchesTaxiway("B"), $"segment {s.FromNodeId}->{s.ToNodeId} drives B"));
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints, h => (h.Reason == HoldShortReason.RouteIncomplete) && (h.TargetName == "B"));
        Assert.Equal(junction.Id, hold.NodeId);
        Assert.True(hold.Unable, "the hold short of B where the rolling aircraft stands was not judged unmakeable");
        Assert.Contains("Unable to hold short of B", taxiB.Message, StringComparison.Ordinal);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxiB.PilotReadback);
        output.WriteLine($"readback '{readback.Speech.Terminal}'");
        Assert.Contains("unable", readback.Speech.Terminal, StringComparison.OrdinalIgnoreCase);

        List<double> speeds = [];
        double nearestToJunctionFt = double.PositiveInfinity;
        for (int second = 1; (second <= 60) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase); second++)
        {
            engine.TickOneSecond();
            speeds.Add(aircraft.GroundSpeed);
            nearestToJunctionFt = Math.Min(nearestToJunctionFt, Ft(aircraft.Position, junction.Position));
        }

        output.WriteLine($"speeds after TAXI B: {string.Join(" ", speeds.Select(s => s.ToString("F2")))}");
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        double previousKts = issued.SpeedKts;
        foreach (double kts in speeds.Take(speeds.Count - 1))
        {
            Assert.True(previousKts - kts <= firmRate + DecelToleranceKtsPerSec, $"slowed {previousKts - kts:F2} kt in one second");
            previousKts = kts;
        }

        double rolledFt = AlongFt(issued.Position, issued.Heading, aircraft.Position);
        double firmStopFt = FirmStopFt(issued.SpeedKts, firmRate);
        double noseShortFt = AlongFt(Nose(aircraft), aircraft.TrueHeading, junction.Position);
        output.WriteLine(
            $"held: rolled {rolledFt:F1} ft (firm-rate stop {firmStopFt:F1}), nose {noseShortFt:F1} ft short of the junction node and "
                + $"{NoseToCentrelineFt(layout, aircraft, "B"):F1} ft from B's centreline, nearest centre {nearestToJunctionFt:F1} ft"
        );
        Assert.True(rolledFt <= firmStopFt + FirmStopToleranceFt, $"rolled {rolledFt:F1} ft, past the {firmStopFt:F1} ft firm-rate stop");
        Assert.True(noseShortFt > 0.0, $"the nose stopped {-noseShortFt:F1} ft past the C/B junction node");
        Assert.True(
            nearestToJunctionFt > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt,
            $"the aircraft came {nearestToJunctionFt:F1} ft from the junction"
        );
    }

    /// <summary>The speed (kt) the makeable case rolls west on C at.</summary>
    private const double MakeableSpeedKts = 10.0;

    /// <summary>
    /// How close (ft) to the C/B junction the makeable case's centre is when <c>TAXI B</c> goes out: its nose still outside
    /// B's setback, and the setback stop farther ahead than its taxi-rate stop.
    /// </summary>
    private const double MakeableIssueWithinFt = 400.0;

    /// <summary>
    /// The same B738 rolling west on C, given a bare <c>TAXI B</c> while its nose is still outside B's setback and the setback
    /// stop lies farther ahead than its taxi-rate stop: a normal hold short of B, read back as cleared and never as unable. It
    /// brakes at no more than its taxi rate and holds at the placed stop, its nose short of the junction node.
    /// </summary>
    [Fact]
    public void BareTaxiB_RollingOnCShortOfTheSetback_HoldsShortOfBWithNoUnable()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        GroundNode junction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("B", "C"));
        GroundNode caJunction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("A", "C"));
        GroundNode nextOnC = caJunction.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C")).Select(e => e.OtherNode(caJunction)).Single();
        var heading = new TrueHeading(GeoMath.BearingTo(caJunction.Position, nextOnC.Position));
        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        AircraftState aircraft = SpawnRolling(layout, caJunction.Position, heading, MakeableSpeedKts);
        engine.World.AddAircraft(aircraft);
        Assert.True(engine.SendCommand(Callsign, "TAXI C D").Success);
        Assert.True(engine.SendCommand(Callsign, $"SPD {MakeableSpeedKts:F0}").Success);

        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        CommandResult? taxiB = null;
        for (int second = 1; (second <= 60) && (taxiB is null); second++)
        {
            engine.TickOneSecond();
            double toJunctionFt = Ft(aircraft.Position, junction.Position);
            output.WriteLine(
                $"t={second}: {aircraft.GroundSpeed:F1} kt, centre {toJunctionFt:F1} ft from junction {junction.Id}, nose "
                    + $"{NoseToCentrelineFt(layout, aircraft, "B"):F1} ft from B's centreline"
            );
            if ((toJunctionFt > MakeableIssueWithinFt) || (Math.Abs(aircraft.GroundSpeed - MakeableSpeedKts) > IssueSpeedToleranceKts))
            {
                continue;
            }

            Assert.True(NoseToCentrelineFt(layout, aircraft, "B") >= TaxiwayNoseSetbackFt(layout), "the nose is already inside B's setback");
            taxiB = engine.SendCommand(Callsign, "TAXI B");
        }

        Assert.True(taxiB is not null, $"{Callsign} never came within {MakeableIssueWithinFt:F0} ft of the C/B junction");
        output.WriteLine($"TAXI B: success={taxiB.Success} '{taxiB.Message}'");
        Assert.True(taxiB.Success, $"'TAXI B' was refused: {taxiB.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.All(route.Segments, s => Assert.False(s.Edge.Edge.MatchesTaxiway("B"), $"segment {s.FromNodeId}->{s.ToNodeId} drives B"));
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints, h => (h.Reason == HoldShortReason.RouteIncomplete) && (h.TargetName == "B"));
        Assert.Equal(junction.Id, hold.NodeId);
        double stopAheadFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, hold);
        double taxiStopFt = FirmStopFt(aircraft.GroundSpeed, taxiRate);
        output.WriteLine($"stop {stopAheadFt:F1} ft ahead, taxi-rate stop {taxiStopFt:F1} ft");
        Assert.True(stopAheadFt > taxiStopFt, $"the stop {stopAheadFt:F1} ft ahead is inside the {taxiStopFt:F1} ft taxi-rate stop");
        Assert.False(hold.Unable, "a hold short of B the aircraft can brake for at its taxi rate was judged unmakeable");
        Assert.DoesNotContain("Unable", taxiB.Message, StringComparison.Ordinal);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxiB.PilotReadback);
        output.WriteLine($"readback '{readback.Speech.Terminal}'");
        Assert.DoesNotContain("unable", readback.Speech.Terminal, StringComparison.OrdinalIgnoreCase);

        double previousKts = aircraft.GroundSpeed;
        double steepestDropKts = 0.0;
        for (int second = 1; (second <= 90) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase); second++)
        {
            engine.TickOneSecond();
            steepestDropKts = Math.Max(steepestDropKts, previousKts - aircraft.GroundSpeed);
            previousKts = aircraft.GroundSpeed;
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        double heldShortOfStopFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, hold);
        double noseShortFt = AlongFt(Nose(aircraft), aircraft.TrueHeading, junction.Position);
        output.WriteLine(
            $"held {heldShortOfStopFt:F1} ft short of the stop, nose {noseShortFt:F1} ft short of the junction, "
                + $"steepest drop {steepestDropKts:F2} kt/s"
        );
        Assert.True(steepestDropKts <= taxiRate + DecelToleranceKtsPerSec, $"slowed {steepestDropKts:F2} kt in one second, above the taxi rate");
        Assert.True(heldShortOfStopFt >= -FirmStopToleranceFt, $"held {-heldShortOfStopFt:F1} ft past the placed stop");
        Assert.True(noseShortFt > 0.0, $"the nose stopped {-noseShortFt:F1} ft past the C/B junction node");
    }

    /// <summary>
    /// The sub-ticked stop (ft) from <paramref name="speedKts"/> at <paramref name="rateKtsPerSec"/>: v²/2a plus half a
    /// physics sub-tick's travel (docs/ground/hold-short-placement.md, Feasibility).
    /// </summary>
    private static double FirmStopFt(double speedKts, double rateKtsPerSec)
    {
        double ftPerSecPerKt = GeoMath.FeetPerNm / 3600.0;
        double v = speedKts * ftPerSecPerKt;
        double a = rateKtsPerSec * ftPerSecPerKt;
        return ((v * v) / (2.0 * a)) + (v / SimulationEngine.PhysicsSubTickRate / 2.0);
    }

    /// <summary>A B738 at <paramref name="position"/> already rolling at <paramref name="speedKts"/>, as a snapshot restore would leave it.</summary>
    private static AircraftState SpawnRolling(AirportGroundLayout layout, LatLon position, TrueHeading heading, double speedKts)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = 6,
            IndicatedAirspeed = speedKts,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "C";
        aircraft.IndicatedAirspeed = speedKts;
        return aircraft;
    }

    private static double TaxiwayNoseSetbackFt(AirportGroundLayout layout) =>
        Math.Max(
            (AircraftLength.ResolveFt(AircraftType) / 2.0) + HoldShortAnnotator.TaxiwayHoldShortBufferFt,
            HoldShortAnnotator.WingtipClearanceFloorFt(layout.Runways.Max(r => r.WidthFt))
        );

    private static LatLon Nose(AircraftState aircraft) =>
        GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm);

    private static double NoseToCentrelineFt(AirportGroundLayout layout, AircraftState aircraft, string taxiway)
    {
        LatLon nose = Nose(aircraft);
        return HoldShortAnnotator.DistanceToCentrelineFt(nose, layout.Edges.Where(e => e.MatchesTaxiway(taxiway)), nose);
    }

    private static double Ft(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// How far <paramref name="point"/> lies ahead of <paramref name="from"/> along <paramref name="heading"/> (ft; negative behind).
    /// </summary>
    private static double AlongFt(LatLon from, TrueHeading heading, LatLon point) =>
        Ft(from, point) * Math.Cos(GeoMath.AbsBearingDifference(heading.Degrees, GeoMath.BearingTo(from, point)) * Math.PI / 180.0);
}
