using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-599: SFO stand F5's lead-in meets taxiway T7A only through the fillet joining the ramp to T7A's southern end,
/// so the push ray crosses no straight T7A edge. The E75L there gets <c>PUSH T7A</c>, then <c>TAXI T7A $7A</c> once the
/// tow is done: the push is accepted and ends on T7A's centreline extended into the fillet's corner, and the taxi turns
/// onto T7A from there and reaches spot 7A.
/// </summary>
public class SfoPushOntoT7AFromF5Tests(ITestOutputHelper output)
{
    private const string Callsign = "SKW4798";
    private const string AircraftType = "E75L";
    private const string Stand = "F5";

    /// <summary>A generous budget for the tow to finish, seconds.</summary>
    private const int TowBudgetSeconds = 600;

    /// <summary>A generous budget for the taxi from the push's end to spot 7A, seconds.</summary>
    private const int TaxiBudgetSeconds = 600;

    /// <summary>How far the nose may swing off the stand heading and still count as not yet turned, degrees.</summary>
    private const double TurnedDeg = 10.0;

    /// <summary>
    /// How far beyond half the fuselage the centre may stop from spot 7A and still have reached it, feet: a taxi to a spot
    /// stops with the nose at the mark, so the centre rests about half the fuselage short of it.
    /// </summary>
    private const double SpotSlackFt = 5.0;

    [Fact]
    public void PushT7A_FromF5_E75L_CompletesOnT7AEnd_ThenTaxiT7A7ATaxisFromThere()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("TugMovePlanner", LogLevel.Debug)
            .EnableCategory("TugPathCheck", LogLevel.Debug)
            .InitializeSimLog();
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, Stand);
        double standDeg = ac.TrueHeading.Degrees;

        CommandResult push = ground.Engine.SendCommand(Callsign, "PUSH T7A");
        output.WriteLine($"{Stand} PUSH T7A: {push.Success} \"{push.Message}\"");
        Assert.True(push.Success, $"PUSH T7A off {Stand} was refused: {push.Message}");
        LatLon plannedEnd = Assert.Single(ac.Phases!.Phases.OfType<PushbackPhase>()).PlannedEnd;

        int second = SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is not PushbackPhase, TowBudgetSeconds, null);
        (GroundNode end, double lineDeg) = T7AEnd(ground.Layout);
        double offLineFt = OffLineFt(ac.Position, end.Position, lineDeg);
        double offPlanFt = FeetBetween(ac.Position, plannedEnd);
        output.WriteLine(
            $"tow done at t={second}s on taxiway {ac.Ground.CurrentTaxiway ?? "(none)"} at ({ac.Position.Lat:F6}, {ac.Position.Lon:F6}), "
                + $"nose {ac.TrueHeading.Degrees:F1}°: {offLineFt:F2} ft off T7A's extended line ({lineDeg:F1}° through node {end.Id}), "
                + $"{offPlanFt:F2} ft off the planned end"
        );
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.True(offLineFt <= 2.0, $"the push ended {offLineFt:F2} ft off T7A's extended centreline");
        Assert.True(offPlanFt <= 2.0, $"the push ended {offPlanFt:F2} ft off its planned end");
        Assert.Equal("T7A", ac.Ground.CurrentTaxiway);

        CommandResult taxi = ground.Engine.SendCommand(Callsign, "TAXI T7A $7A");
        output.WriteLine($"TAXI T7A $7A: {taxi.Success} \"{taxi.Message}\"");
        Assert.True(taxi.Success, $"TAXI T7A $7A after the push was refused: {taxi.Message}");
        AssertTaxisToSevenA(ground, ac, standDeg);
    }

    /// <summary>
    /// Ticks the taxi until the aircraft stops taxiing or the budget runs out: before its nose swings
    /// <see cref="TurnedDeg"/> off the stand heading it moves no more than 30 ft along that heading, and it stops with
    /// its nose at spot 7A (<see cref="SpotSlackFt"/>), lined up with T7A.
    /// </summary>
    private void AssertTaxisToSevenA(SfoGround ground, AircraftState ac, double standDeg)
    {
        GroundNode spot = ground.Layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO layout carries no spot 7A");
        LatLon from = ac.Position;
        double aheadBeforeTurnFt = 0.0;
        bool turned = false;
        int second = 0;
        for (; (second < TaxiBudgetSeconds) && (ac.Phases?.CurrentPhase is TaxiingPhase); second++)
        {
            ground.Engine.TickOneSecond();
            turned |= GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, standDeg) > TurnedDeg;
            if (!turned)
            {
                double movedFt = FeetBetween(from, ac.Position);
                double alongDeg = (GeoMath.BearingTo(from, ac.Position) - standDeg) * Math.PI / 180.0;
                aheadBeforeTurnFt = Math.Max(aheadBeforeTurnFt, movedFt * Math.Cos(alongDeg));
            }
        }

        double toSpotFt = FeetBetween(ac.Position, spot.Position);
        double lineDeg =
            ground.Layout.GetEdgeBearingForTaxiway(spot, "T7A", ac.TrueHeading.Degrees) ?? throw new InvalidOperationException("no T7A edge at 7A");
        double offLineDeg = GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, lineDeg);
        output.WriteLine(
            $"taxi stopped at t={second}s in {ac.Phases?.CurrentPhase?.GetType().Name}: {toSpotFt:F1} ft from spot 7A, nose "
                + $"{ac.TrueHeading.Degrees:F1}° against T7A's {lineDeg:F1}°; moved {aheadBeforeTurnFt:F1} ft along {standDeg:F1}° before turning"
        );
        double reachFt = (AircraftFootprint.FromType(AircraftType).LengthFt / 2.0) + SpotSlackFt;
        Assert.True(aheadBeforeTurnFt <= 30.0, $"the taxi moved {aheadBeforeTurnFt:F1} ft along the stand heading before turning onto T7A");
        Assert.IsNotType<TaxiingPhase>(ac.Phases?.CurrentPhase);
        Assert.True(toSpotFt <= reachFt, $"the taxi ended {toSpotFt:F1} ft from spot 7A, more than the {reachFt:F1} ft that puts the nose on it");
        Assert.True(offLineDeg <= 5.0, $"the taxi ended with the nose {offLineDeg:F1}° off T7A");
    }

    /// <summary>T7A's southern end, the node F5's lead-in fillet meets it at, and the direction of T7A's straight edge there, degrees true.</summary>
    private static (GroundNode End, double LineDeg) T7AEnd(AirportGroundLayout layout)
    {
        GroundNode stand = layout.FindParkingByName(Stand) ?? throw new InvalidOperationException($"SFO layout carries no parking named {Stand}");
        GroundNode rampSide = stand.Edges.Single(e => e.IsRamp).OtherNode(stand);
        GroundNode end = rampSide.Edges.OfType<GroundArc>().Single(a => a.MatchesTaxiway("T7A")).OtherNode(rampSide);
        GroundEdge edge = end.Edges.OfType<GroundEdge>().Single(e => e.MatchesTaxiway("T7A"));
        return (end, GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position));
    }

    /// <summary>How far <paramref name="point"/> lies off the line through <paramref name="through"/> on <paramref name="lineDeg"/>, feet.</summary>
    private static double OffLineFt(LatLon point, LatLon through, double lineDeg) =>
        Math.Abs(FeetBetween(through, point) * Math.Sin((GeoMath.BearingTo(through, point) - lineDeg) * Math.PI / 180.0));

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
}
