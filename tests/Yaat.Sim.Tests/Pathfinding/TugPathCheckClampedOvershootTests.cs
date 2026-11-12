using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// The taxiway overshoot of a push onto taxiway X is measured against X's charted polyline clamped at its ends, not the
/// infinite line through the nearest straight edge (docs/ground/pushback.md, the flown-path check). The charted polyline
/// carries each end the fillet pass rounded off on to its corner, so a rounded dead end ends at the charted end and the
/// legs of a rounded bend meet. A tow that starts beyond an end of X and never gets farther past it than it started is
/// measured against the line as before (the narrowed exception). The nine cases the rule was chosen from: four stay
/// accepted, three that run past taxiway A's dead end at SFO node 33 are refused, with the spot 10 push that stays short
/// of the half-span past node 33 as the accepted contrast, and OAK PCM1's push onto R1, accepted for a C208 and refused
/// with the design group II envelope. Then a sample in the gap between R1's tangent node and its charted end, which the
/// charted polyline reads by its lateral offset.
/// </summary>
public class TugPathCheckClampedOvershootTests
{
    private const string Narrowbody = "B738";

    private static readonly Regex OvershootPastA = new(
        @"^Unable, the move to taxiway A would take the aircraft \d+ ft past taxiway A$",
        RegexOptions.CultureInvariant
    );

    private readonly ITestOutputHelper _output;

    public TugPathCheckClampedOvershootTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// SFO C8 <c>PUSH Y TAIL S</c> and <c>PUSH Y FACE N</c> with a B739, as
    /// <c>PushTaxiwayTailFacingTests.PushOntoY_FromC8_FacesTheWayTheHintSays</c> drives them (both hint magnetic north): C8
    /// lies beyond Y's north end and lines up on Y's extended line, so it is measured as before and stays accepted. Both
    /// forms resolve to the same goal: the tail-south hint is the nose-north facing.
    /// </summary>
    [Theory]
    [InlineData("PUSH Y TAIL S", 0.0)]
    [InlineData("PUSH Y FACE N", 0.0)]
    public void C8PushYFacingNorth_StartsBeyondYsEnd_StaysAccepted(string command, double hintMagneticDeg)
    {
        if (Load("SFO") is not { } layout)
        {
            return;
        }

        _output.WriteLine(command);
        GroundNode stand = Parking(layout, "C8");
        TugGoal goal = TaxiwayLineFacing(layout, stand.Position, "Y", hintMagneticDeg);

        AssertPlanned(layout, StandStart(stand, "B739", goal));
    }

    /// <summary>
    /// SFO B2 <c>PUSH M4</c> with a B737, as <c>TugMovePlannerTests.B2StraightBackToM4Alongside_PushesOffThenCurvesOntoTheCentreline</c>
    /// plans it: the push curves onto M4's centreline alongside and stays accepted.
    /// </summary>
    [Fact]
    public void B2PushM4_StaysAccepted()
    {
        if (Load("SFO") is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "B2");
        GroundNode exit = Exit(layout, stand.Position, "M4");

        AssertPlanned(layout, StandStart(stand, "B737", TugGoal.StraightBackTo(exit, "M4")));
    }

    /// <summary>
    /// OAK gate 25 <c>PUSH TE</c> with a B737, as <c>TugMovePlannerTests.PushTe_FromOakGate25_CapturesTeAlongsideInsteadOfALongStraightPush</c>
    /// plans it: stays accepted.
    /// </summary>
    [Fact]
    public void Oak25PushTe_StaysAccepted()
    {
        if (Load("OAK") is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "25");
        GroundNode exit = Exit(layout, stand.Position, "TE");

        AssertPlanned(layout, StandStart(stand, "B737", TugGoal.StraightBackTo(exit, "TE")));
    }

    /// <summary>
    /// OAK gate 25 <c>PUSH TE</c> with a B738 parked crossways 230 ft down the push line, as
    /// <c>TugMovePlannerTests.BarePushToTaxiway_WithANeighbourOnThePushLine_IsStillPlannedAndNotesTheNeighbour</c> builds it:
    /// stays accepted.
    /// </summary>
    [Fact]
    public void Oak25PushTe_WithANeighbourOnThePushLine_StaysAccepted()
    {
        if (Load("OAK") is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "25");
        GroundNode exit = Exit(layout, stand.Position, "TE");
        double pushDeg = new TrueHeading(stand.TrueHeading!.Value.Degrees + 180.0).Degrees;
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "PRK1",
            Position = GeoMath.ProjectPoint(stand.Position, new TrueHeading(pushDeg), 230.0 / GeoMath.FeetPerNm),
            TrueHeadingDeg = new TrueHeading(pushDeg + 90.0).Degrees,
            Footprint = AircraftFootprint.FromType(Narrowbody),
            StandName = null,
        };
        TugRequest request = StandStart(stand, Narrowbody, TugGoal.StraightBackTo(exit, "TE")) with { ParkedNeighbours = [neighbour] };

        AssertPlanned(layout, request);
    }

    /// <summary>
    /// SFO spot 10, <c>PUSH A FACE N</c> with a B738 standing on the spot (not a stand), nose true: no existing test drives
    /// it. LayoutInspector probe (<c>tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 33 --node-depth 1</c>): "Node 33 ...
    /// Lat: 37.619842 Lon: -122.392652", its only A edge "-> Node 1143 via A (0.0124nm) bearing=27.6°", so node 33 is A's
    /// south dead end; spot 10 is the geojson's <c>{"type": "spot", "name": "10"} [-122.39286656, 37.62017624]</c>, about
    /// 79 ft along A's line from node 33 and west of it. Probe of this request: nose 070 is refused "Unable, the move to
    /// taxiway A would take the aircraft 71 ft past taxiway A"; nose 010 plans, ending on A's extended line (96 ft from
    /// the A exit node 1144, bearing 207.7°). The 070 push runs farther past A's dead end than the B738's half-span; the
    /// 010 one does not.
    /// </summary>
    [Theory]
    [InlineData(70.0, true, 71.0)]
    [InlineData(10.0, false, 0.0)]
    public void Spot10PushAFaceNorth_RefusedOnlyWhenItRunsFarPastAsDeadEnd(double noseTrueDeg, bool refused, double pastFt)
    {
        if (Load("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "10");
        TugGoal goal = TaxiwayLineFacing(layout, spot.Position, "A", hintMagneticDeg: 0.0);
        TugRequest request = Request(new PushbackPose(spot.Position, noseTrueDeg), atStand: false, Narrowbody, goal);

        if (refused)
        {
            AssertRefusedPastA(layout, request, pastFt);
        }
        else
        {
            AssertPlanned(layout, request);
        }
    }

    /// <summary>
    /// SFO G11R and G10 <c>PUSH A FACE N</c> with a B738: no existing test drives them. The stands are the geojson's
    /// <c>{"type": "parking", "name": "G11R", "heading": 25} [-122.39385479, 37.61815595]</c> and
    /// <c>{"type": "parking", "name": "G10", "heading": 196} [-122.39329072, 37.61915742]</c>, both south-west of A's
    /// dead end at node 33 (probe above). The push ends on A's extended line past node 33, so it is refused for
    /// overshooting A.
    /// </summary>
    [Theory]
    [InlineData("G11R", 838.0)]
    [InlineData("G10", 325.0)]
    public void GGatePushAFaceNorth_EndsPastAsDeadEnd_IsRefused(string standName, double pastFt)
    {
        if (Load("SFO") is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, standName);
        TugGoal goal = TaxiwayLineFacing(layout, stand.Position, "A", hintMagneticDeg: 0.0);

        AssertRefusedPastA(layout, StandStart(stand, Narrowbody, goal), pastFt);
    }

    /// <summary>
    /// OAK PCM1 bare <c>PUSH R1</c> with a C208. LayoutInspector probe (<c>oak.geojson --airport-code KOAK --no-fillets
    /// --node 271</c>): R1's charted south-west end is "Node 271 ... Lat: 37.720417 Lon: -122.217794" with only R1 and the PCM1
    /// connector. The fillet pass rounds that corner into an R1-RAMP arc, so R1's last straight edge stops 39 ft short of it.
    /// The push ends on R1's extended line about 18 ft past the charted end, inside the C208's half-span: accepted. The
    /// test calls the planner directly, bypassing PCM1's sidecar <c>TaxiOut</c> stand departure.
    /// </summary>
    [Fact]
    public void Pcm1PushR1_EndsWithinHalfSpanOfR1sChartedEnd_StaysAccepted()
    {
        if (Load("OAK") is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "PCM1");
        AssertPlanned(layout, StandStart(stand, "C208", TugGoal.StraightBackTo(Exit(layout, stand.Position, "R1"), "R1")));
    }

    /// <summary>
    /// The same push with the design group II envelope ends about 93 ft past R1's charted end: refused. Called on the
    /// planner directly, as above.
    /// </summary>
    [Fact]
    public void Pcm1PushR1_GroupIiEnvelope_RunsFarPastR1sChartedEnd_IsRefused()
    {
        if (Load("OAK") is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "PCM1");
        TugRequest request = StandStart(stand, "C208", TugGoal.StraightBackTo(Exit(layout, stand.Position, "R1"), "R1")) with
        {
            Footprint = DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.II),
        };
        Assert.Null(Plan(layout, request, out string refusal));
        Assert.Matches(@"^Unable, the move to taxiway R1 would take the aircraft \d+ ft past taxiway R1$", refusal);
        AssertPastFt(refusal, 93.0);
    }

    /// <summary>
    /// A C208 sample in the gap between R1's last straight edge's end node (the fillet tangent node near PCM1) and R1's
    /// charted end 39 ft beyond it, 30 ft past the tangent node and offset to the far side from a start abeam R1 on the
    /// other side. The charted polyline reads its lateral offset: 5 ft is accepted (the along-distance, about 30 ft, would
    /// pass the C208's 26 ft bound), 35 ft is refused naming 35 ft.
    /// </summary>
    [Theory]
    [InlineData(5.0, false)]
    [InlineData(35.0, true)]
    public void SampleInR1sChartedEndGap_ReadsItsLateralOffset(double lateralFt, bool refused)
    {
        if (Load("OAK") is not { } layout)
        {
            return;
        }

        (LatLon end, LatLon inner) = FreeEndNear(layout, "R1", Parking(layout, "PCM1").Position);
        double outwardDeg = GeoMath.BearingTo(inner, end);
        LatLon start = Project(Project(end, outwardDeg + 180.0, 100.0), outwardDeg - 90.0, 60.0);
        LatLon gap = Project(Project(end, outwardDeg, 30.0), outwardDeg + 90.0, lateralFt);

        AssertReading(layout, "R1", [new PushbackPose(start, outwardDeg), new PushbackPose(gap, outwardDeg)], refused ? lateralFt : null);
    }

    private static IEnumerable<GroundEdge> StraightOf(AirportGroundLayout layout, string taxiway) =>
        layout.Edges.Where(e => e.MatchesTaxiway(taxiway) && (e.Nodes[0] != e.Nodes[1]));

    /// <summary>
    /// The end node of <paramref name="taxiway"/>'s straight edges nearest <paramref name="near"/> that no other straight
    /// edge of the taxiway touches, and its edge's other node.
    /// </summary>
    private static (LatLon End, LatLon Inner) FreeEndNear(AirportGroundLayout layout, string taxiway, LatLon near)
    {
        List<GroundEdge> straight = [.. StraightOf(layout, taxiway)];
        return straight
            .SelectMany(e => new[] { (End: e.Nodes[0], Inner: e.Nodes[1]), (End: e.Nodes[1], Inner: e.Nodes[0]) })
            .Where(p => straight.Count(e => e.Nodes.Contains(p.End)) == 1)
            .OrderBy(p => GeoMath.DistanceNm(p.End.Position, near))
            .Select(p => (p.End.Position, p.Inner.Position))
            .First();
    }

    private static LatLon Project(LatLon from, double trueDeg, double feet) =>
        GeoMath.ProjectPoint(from, new TrueHeading(trueDeg), feet / GeoMath.FeetPerNm);

    /// <summary>
    /// Checks a C208 tow through <paramref name="samples"/> onto <paramref name="taxiway"/>: accepted when
    /// <paramref name="refusedFt"/> is null, else refused naming that many feet past the taxiway (±1 ft).
    /// </summary>
    private void AssertReading(AirportGroundLayout layout, string taxiway, PushbackPose[] samples, double? refusedFt)
    {
        var check = new TugPathCheck(layout, MovementAreaClassification.For(layout), AircraftFootprint.FromType("C208"), samples[0].Position);
        var trace = new TugMoveTrace
        {
            Move = TugMove.Straight(PushbackLegKind.Push, 0.0),
            Samples = samples,
            Completed = true,
            PathLengthFt = 0.0,
            MaxTravelDeviationDeg = 0.0,
            End = samples[^1],
            EndCrossTrackFt = null,
            EndLineTravelErrorDeg = null,
            EndOvershootFt = null,
        };
        var judging = new TugTaxiwayJudging(new HashSet<string>(), new HashSet<string>([taxiway]), CrossingRule: false, NodeGoal: false);
        TugPathRefusal? refusal = check.Check([trace], judging, ($"the move to taxiway {taxiway}", null), forced: false);
        _output.WriteLine(refusal is { } r ? $"refused: {r.Message}" : "accepted");
        if (refusedFt is not { } expectedFt)
        {
            Assert.Null(refusal);
            return;
        }

        Assert.NotNull(refusal);
        AssertPastFt(refusal.Value.Message, expectedFt);
    }

    /// <summary>Asserts the refusal names <paramref name="expectedFt"/> feet past the taxiway, ±1 ft.</summary>
    private static void AssertPastFt(string refusal, double expectedFt)
    {
        Match past = Regex.Match(refusal, @"(\d+) ft past taxiway");
        Assert.True(past.Success, refusal);
        Assert.InRange(double.Parse(past.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), expectedFt - 1.0, expectedFt + 1.0);
    }

    private static AirportGroundLayout? Load(string airport) => new TestAirportGroundData().GetLayout(airport);

    /// <summary>The goal a <c>PUSH &lt;taxiway&gt; FACE &lt;hint&gt;</c> resolves to, as the pushback handler derives it.</summary>
    private static TugGoal TaxiwayLineFacing(AirportGroundLayout layout, LatLon from, string taxiway, double hintMagneticDeg)
    {
        GroundNode exit = Exit(layout, from, taxiway);
        double hintTrueDeg = MagneticDeclination.MagneticToTrue(hintMagneticDeg, from);
        double facingDeg = layout.GetEdgeBearingForTaxiway(exit, taxiway, hintTrueDeg) ?? hintTrueDeg;
        return TugGoal.TaxiwayLine(exit, taxiway, facingDeg);
    }

    private static GroundNode Exit(AirportGroundLayout layout, LatLon from, string taxiway) =>
        layout.FindExitByTaxiway(from, taxiway) ?? throw new InvalidOperationException($"no taxiway {taxiway} exit near the start");

    private static GroundNode Spot(AirportGroundLayout layout, string name) =>
        layout.FindSpotNodeByName(name) ?? throw new InvalidOperationException($"layout carries no spot named {name}");

    private static GroundNode Parking(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"layout carries no parking named {name}");

    private static TugRequest StandStart(GroundNode stand, string aircraftType, TugGoal goal) =>
        Request(new PushbackPose(stand.Position, stand.TrueHeading!.Value.Degrees), atStand: true, aircraftType, goal);

    private static TugRequest Request(PushbackPose start, bool atStand, string aircraftType, TugGoal goal) =>
        new()
        {
            Start = start,
            StartsAtStand = atStand,
            StandName = null,
            Footprint = AircraftFootprint.FromType(aircraftType),
            MovementArea = null,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    private TugPlan? Plan(AirportGroundLayout layout, TugRequest request, out string refusal)
    {
        request = request with { MovementArea = MovementAreaClassification.For(layout) };
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out refusal);
        _output.WriteLine(plan is null ? $"refused: {refusal}" : $"planned: {plan.Moves.Count} move(s), {plan.Moves.Sum(m => m.PathLengthFt):F0} ft");
        return plan;
    }

    private void AssertPlanned(AirportGroundLayout layout, TugRequest request)
    {
        TugPlan? plan = Plan(layout, request, out string refusal);
        Assert.True(plan is not null, $"the plan was refused: {refusal}");
        Assert.Equal(string.Empty, refusal);
    }

    private void AssertRefusedPastA(AirportGroundLayout layout, TugRequest request, double pastFt)
    {
        TugPlan? plan = Plan(layout, request, out string refusal);
        if (plan is not null)
        {
            GroundNode exit = Exit(layout, request.Start.Position, "A");
            LatLon end = plan.End.Position;
            _output.WriteLine(
                $"exit node {exit.Id}; start {GeoMath.DistanceNm(exit.Position, request.Start.Position) * GeoMath.FeetPerNm:F0} ft "
                    + $"bearing {GeoMath.BearingTo(exit.Position, request.Start.Position):F1} from it; end ({end.Lat:F6},{end.Lon:F6}) "
                    + $"{GeoMath.DistanceNm(exit.Position, end) * GeoMath.FeetPerNm:F0} ft bearing {GeoMath.BearingTo(exit.Position, end):F1}, "
                    + $"nose {plan.End.NoseTrueDeg:F1}"
            );
        }

        Assert.Null(plan);
        Assert.Matches(OvershootPastA, refusal);
        AssertPastFt(refusal, pastFt);
    }
}
