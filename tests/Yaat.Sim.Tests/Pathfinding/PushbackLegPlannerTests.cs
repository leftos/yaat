using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The multi-leg tug planner, measured against the real SFO ground layout. Every case resolves its nodes by
/// name — ids renumber whenever the layout is regenerated.
///
/// <para>The happy path is the documented ZOA SFO technique: a D-pier stand pushes across the six alley onto
/// spot 6A or spot 6B (<c>SfoSixAlleyChoreographyTests</c>). Chained, that is gate D15 → spot 6A → spot 6B:
/// a 520.7 ft reverse across the alley, then a 140.5 ft pull back onto the near lane.</para>
/// </summary>
public class PushbackLegPlannerTests
{
    private readonly ITestOutputHelper _output;

    public PushbackLegPlannerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Degrees of heading change the tug buys per foot of leg, at the pushback speed and turn rate.</summary>
    private static double TurnPerFootDeg =>
        CategoryPerformance.PushbackTurnRate(AircraftCategory.Jet)
        / (CategoryPerformance.PushbackSpeed(AircraftCategory.Jet) * GeoMath.FeetPerNm / 3600.0);

    [Fact]
    public void D15AcrossSixAlleyToSixAThenBackToSixB_PushesThenPulls()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var stand = Parking(layout, "D15");
        var sixA = Spot(layout, "6A");
        var sixB = Spot(layout, "6B");

        var legs = PushbackLegPlanner.Plan(
            layout,
            stand.Position,
            stand.TrueHeading!.Value.Degrees,
            startsAtStand: true,
            [new PushbackTarget(sixA, IsSpot: true), new PushbackTarget(sixB, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        Assert.Equal(string.Empty, refusal);
        Assert.NotNull(legs);
        LogLegs(stand.Position, legs);

        Assert.Equal(2, legs.Count);
        Assert.Equal(PushbackLegKind.Push, legs[0].Kind);
        Assert.Equal(PushbackLegKind.Pull, legs[1].Kind);
    }

    /// <summary>
    /// The AC 00-65A §11.10 preference bites: D15 → 6B → 6A leaves the tail at 6B pointing back at the stand
    /// (the natural end heading), which would make the hop across to 6A another reverse. The planner turns the
    /// aircraft during the first leg instead, so the second leg is a pull.
    /// </summary>
    [Fact]
    public void D15ToSixBThenSixA_TurnsOnTheFirstLegSoTheSecondIsAPull()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var stand = Parking(layout, "D15");
        var sixA = Spot(layout, "6A");
        var sixB = Spot(layout, "6B");

        var legs = PushbackLegPlanner.Plan(
            layout,
            stand.Position,
            stand.TrueHeading!.Value.Degrees,
            startsAtStand: true,
            [new PushbackTarget(sixB, IsSpot: true), new PushbackTarget(sixA, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        Assert.Equal(string.Empty, refusal);
        Assert.NotNull(legs);
        LogLegs(stand.Position, legs);

        double naturalEnd = GeoMath.BearingTo(stand.Position, sixB.Position) + 180.0;
        double desired = GeoMath.BearingTo(sixB.Position, sixA.Position);
        double needed = GeoMath.AbsBearingDifference(desired, naturalEnd);
        _output.WriteLine(
            $"natural end {naturalEnd:0.0}  desired {desired:0.0}  needed {needed:0.0}°  available {legs[0].LengthFt * TurnPerFootDeg:0.0}°"
        );

        Assert.Equal(PushbackLegKind.Push, legs[0].Kind);
        Assert.Equal(new TrueHeading(desired).ToDisplayInt(), legs[0].EndTrueHeadingDeg);
        Assert.True(needed > 90.0, $"the natural end heading must be more than 90° off the desired one, was {needed:0.0}°");
        Assert.Equal(PushbackLegKind.Pull, legs[1].Kind);
    }

    /// <summary>
    /// The same preference, denied: D5 → 5A is only ~226 ft, too short for the tug to swing the nose onto the
    /// bearing toward spot 5, so the first leg ends on its natural heading and the second leg falls out a push.
    /// </summary>
    [Fact]
    public void D5ToFiveAThenFive_ShortFirstLegCannotTurnSoTheSecondIsAPush()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var stand = Parking(layout, "D5");
        var fiveA = Spot(layout, "5A");
        var five = Spot(layout, "5");

        var legs = PushbackLegPlanner.Plan(
            layout,
            stand.Position,
            stand.TrueHeading!.Value.Degrees,
            startsAtStand: true,
            [new PushbackTarget(fiveA, IsSpot: true), new PushbackTarget(five, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        Assert.Equal(string.Empty, refusal);
        Assert.NotNull(legs);
        LogLegs(stand.Position, legs);

        double naturalEnd = GeoMath.BearingTo(stand.Position, fiveA.Position) + 180.0;
        double desired = GeoMath.BearingTo(fiveA.Position, five.Position);
        double needed = GeoMath.AbsBearingDifference(desired, naturalEnd);
        double available = legs[0].LengthFt * TurnPerFootDeg;
        _output.WriteLine($"natural end {naturalEnd:0.0}  desired {desired:0.0}  needed {needed:0.0}°  available {available:0.0}°");

        Assert.True(needed > available, $"the turn must be out of reach: needed {needed:0.0}°, available {available:0.0}°");
        Assert.Equal(new TrueHeading(naturalEnd).ToDisplayInt(), legs[0].EndTrueHeadingDeg);
        Assert.Equal(PushbackLegKind.Push, legs[1].Kind);
    }

    /// <summary>
    /// The motivating report: SKW3396 had already made a bare <c>PUSH</c> and was sitting out in the alley when
    /// the next target was asked for. Off a stand the first leg is always a reverse; out in the alley it is not,
    /// and a target ahead of the nose is a pull.
    /// </summary>
    [Fact]
    public void AlreadyInTheAlley_FirstLegToATargetAheadIsAPull()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var sixA = Spot(layout, "6A");
        var sixB = Spot(layout, "6B");
        double noseBearing = GeoMath.BearingTo(sixB.Position, sixA.Position);

        var legs = PushbackLegPlanner.Plan(
            layout,
            sixB.Position,
            noseBearing,
            startsAtStand: false,
            [new PushbackTarget(sixA, IsSpot: true), new PushbackTarget(sixB, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        Assert.Equal(string.Empty, refusal);
        Assert.NotNull(legs);
        LogLegs(sixB.Position, legs);

        Assert.Equal(PushbackLegKind.Pull, legs[0].Kind);
    }

    /// <summary>
    /// The case the whole feature exists for: an aircraft that has already made a <c>PUSH A</c> is standing
    /// <em>on</em> taxiway A, so A's own edges cut its first leg at ~0 ft from the leg's start point. A
    /// movement-area rule that windows only the end point refuses every move off that pose.
    /// </summary>
    [Fact]
    public void StandingOnTaxiwayA_LegOffThatPavementIntoTheRamp_Planned()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var twentyTwo = Spot(layout, "22");
        var sixB = Spot(layout, "6B");
        var onTaxiwayA = NearestNodeOnTaxiway(layout, "A", twentyTwo.Position);
        _output.WriteLine(
            $"node {onTaxiwayA.Id} on taxiway A is {GeoMath.DistanceNm(onTaxiwayA.Position, twentyTwo.Position) * GeoMath.FeetPerNm:0} ft from spot 22"
        );

        var legs = PushbackLegPlanner.Plan(
            layout,
            onTaxiwayA.Position,
            GeoMath.BearingTo(onTaxiwayA.Position, twentyTwo.Position),
            startsAtStand: false,
            [new PushbackTarget(twentyTwo, IsSpot: true), new PushbackTarget(sixB, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        _output.WriteLine(refusal);
        Assert.Equal(string.Empty, refusal);
        Assert.NotNull(legs);
        LogLegs(onTaxiwayA.Position, legs);
        Assert.Equal(2, legs.Count);
    }

    /// <summary>
    /// SFO's six-alley entrance arc carries both taxiway <c>A</c> and ramp lane <c>T6A</c>, and shared pavement
    /// at a ramp entrance counts as ramp: a tug move off taxiway A into the alley is a legitimate move, so that
    /// arc must not cut it. Read as taxiway A, the arc refused the leg ~75 ft in.
    /// </summary>
    [Fact]
    public void FromTaxiwayAIntoTheSixAlley_SharedEntranceArcDoesNotCutTheLeg()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var sixA = Spot(layout, "6A");
        var sixB = Spot(layout, "6B");
        var onTaxiwayA = NearestNodeOnTaxiway(layout, "A", sixA.Position);
        string crossings = MidLegCrossings(layout, onTaxiwayA.Position, sixA.Position);
        _output.WriteLine(
            $"node {onTaxiwayA.Id} on taxiway A is {GeoMath.DistanceNm(onTaxiwayA.Position, sixA.Position) * GeoMath.FeetPerNm:0} ft from spot 6A"
        );
        _output.WriteLine($"mid-leg crossings: {crossings}");

        var legs = PushbackLegPlanner.Plan(
            layout,
            onTaxiwayA.Position,
            GeoMath.BearingTo(onTaxiwayA.Position, sixA.Position),
            startsAtStand: false,
            [new PushbackTarget(sixA, IsSpot: true), new PushbackTarget(sixB, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        Assert.True(refusal.Length == 0, $"the leg off taxiway A into the six alley was refused: {refusal} (mid-leg crossings: {crossings})");
        Assert.NotNull(legs);
        LogLegs(onTaxiwayA.Position, legs);
        Assert.Equal(2, legs.Count);
        Assert.True(
            MidLegEdges(layout, onTaxiwayA.Position, sixA.Position).Any(e => e.MatchesTaxiway("A") && e.MatchesTaxiway("T6A")),
            $"leg 1 has to cut the shared A/T6A entrance arc away from both of its ends, or the case is not pinned (mid-leg crossings: {crossings})"
        );
    }

    [Fact]
    public void OnAStandWithTheFirstTargetAheadOfTheNose_Refused()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var stand = Parking(layout, "D5");
        var sixA = Spot(layout, "6A");
        var sixB = Spot(layout, "6B");
        _output.WriteLine($"D5 heading {stand.TrueHeading!.Value.Degrees:0.0}, bearing to 6A {GeoMath.BearingTo(stand.Position, sixA.Position):0.0}");

        var legs = PushbackLegPlanner.Plan(
            layout,
            stand.Position,
            stand.TrueHeading!.Value.Degrees,
            startsAtStand: true,
            [new PushbackTarget(sixA, IsSpot: true), new PushbackTarget(sixB, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        _output.WriteLine(refusal);
        Assert.Null(legs);
        Assert.Contains("ahead of the nose", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleTarget_RefusedAndNamesPlainPush()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var stand = Parking(layout, "D15");
        var sixA = Spot(layout, "6A");

        var legs = PushbackLegPlanner.Plan(
            layout,
            stand.Position,
            stand.TrueHeading!.Value.Degrees,
            startsAtStand: true,
            [new PushbackTarget(sixA, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        _output.WriteLine(refusal);
        Assert.Null(legs);
        Assert.Contains("PUSH", refusal, StringComparison.Ordinal);
    }

    /// <summary>Spot 18 to spot 33 is 1,872 ft — under the sanity cap, but straight across 28L/10R and 28R/10L.</summary>
    [Fact]
    public void LegAcrossARunway_Refused()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var eighteen = Spot(layout, "18");
        var thirtyThree = Spot(layout, "33");

        var legs = PushbackLegPlanner.Plan(
            layout,
            eighteen.Position,
            GeoMath.BearingTo(eighteen.Position, thirtyThree.Position),
            startsAtStand: false,
            // The second target only satisfies the two-target minimum; the first leg is refused before it is read.
            [new PushbackTarget(thirtyThree, IsSpot: true), new PushbackTarget(eighteen, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        _output.WriteLine($"18 → 33 is {GeoMath.DistanceNm(eighteen.Position, thirtyThree.Position) * GeoMath.FeetPerNm:0.0} ft");
        _output.WriteLine(refusal);
        Assert.Null(legs);
        Assert.Contains("crosses runway", refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gate D1 to spot 34 is 1,035 ft and cuts clean across taxiway A well short of the spot.</summary>
    [Fact]
    public void LegTransitingMovementAreaPavement_Refused()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var stand = Parking(layout, "D1");
        var thirtyFour = Spot(layout, "34");
        var sixA = Spot(layout, "6A");

        var legs = PushbackLegPlanner.Plan(
            layout,
            stand.Position,
            stand.TrueHeading!.Value.Degrees,
            startsAtStand: true,
            [new PushbackTarget(thirtyFour, IsSpot: true), new PushbackTarget(sixA, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        _output.WriteLine($"D1 → 34 is {GeoMath.DistanceNm(stand.Position, thirtyFour.Position) * GeoMath.FeetPerNm:0.0} ft");
        _output.WriteLine(refusal);
        Assert.Null(legs);
        Assert.Contains("crosses taxiway", refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gate D5 to spot 1 is 3,092 ft — a mis-click, not a tug move.</summary>
    [Fact]
    public void LegBeyondTheSanityCap_Refused()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var stand = Parking(layout, "D5");
        var one = Spot(layout, "1");
        var sixA = Spot(layout, "6A");

        var legs = PushbackLegPlanner.Plan(
            layout,
            stand.Position,
            stand.TrueHeading!.Value.Degrees,
            startsAtStand: true,
            [new PushbackTarget(one, IsSpot: true), new PushbackTarget(sixA, IsSpot: true)],
            explicitFinalFacingTrueDeg: null,
            AircraftCategory.Jet,
            out string refusal
        );

        _output.WriteLine($"D5 → 1 is {GeoMath.DistanceNm(stand.Position, one.Position) * GeoMath.FeetPerNm:0.0} ft");
        _output.WriteLine(refusal);
        Assert.Null(legs);
        Assert.Contains("sanity guard", refusal, StringComparison.OrdinalIgnoreCase);
    }

    private void LogLegs(LatLon start, IReadOnlyList<PushbackLeg> legs)
    {
        var from = start;
        for (int i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            double bearing = GeoMath.BearingTo(from, leg.Target);
            _output.WriteLine(
                $"leg {i + 1}: {leg.Kind} bearing {bearing:0.0}  end heading {leg.EndTrueHeadingDeg:000}  length {leg.LengthFt:0.0} ft  available turn {leg.LengthFt * TurnPerFootDeg:0.0}°"
            );
            from = leg.Target;
        }
    }

    /// <summary>
    /// The edges the leg cuts more than 25 ft from either of its ends — the ones the planner's transit rule
    /// reads. The 25 ft mirrors its own end window: an intersection inside that is the leg arriving at pavement
    /// or leaving pavement it stands on, which is allowed and therefore proves nothing here.
    /// </summary>
    private static IEnumerable<IGroundEdge> MidLegEdges(AirportGroundLayout layout, LatLon from, LatLon to)
    {
        foreach (var edge in layout.AllEdges)
        {
            if (GeoMath.SegmentsIntersect(from, to, edge.Nodes[0].Position, edge.Nodes[1].Position) is not { } hit)
            {
                continue;
            }

            double toEndFt = Math.Min(GeoMath.DistanceNm(hit.Point, from), GeoMath.DistanceNm(hit.Point, to)) * GeoMath.FeetPerNm;
            if (toEndFt > 25.0)
            {
                yield return edge;
            }
        }
    }

    private static string MidLegCrossings(AirportGroundLayout layout, LatLon from, LatLon to) =>
        string.Join(", ", MidLegEdges(layout, from, to).Select(e => e.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase));

    private static GroundNode Spot(AirportGroundLayout layout, string name) =>
        layout.FindSpotNodeByName(name) ?? throw new InvalidOperationException($"SFO layout carries no spot named {name}");

    private static GroundNode NearestNodeOnTaxiway(AirportGroundLayout layout, string taxiway, LatLon near) =>
        layout.GetNodesOnTaxiway(taxiway).OrderBy(n => GeoMath.DistanceNm(n.Position, near)).FirstOrDefault()
        ?? throw new InvalidOperationException($"SFO layout carries no node on taxiway {taxiway}");

    private static GroundNode Parking(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"SFO layout carries no parking named {name}");
}
