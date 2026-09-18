using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// SFO gate B12 faces 284° with taxilane Yankee behind it (~186 ft) and taxiway Alpha a further ~218 ft
/// beyond that, so a B12 departure pushes tail-first ESE onto Y. Whichever form the push takes it must stop
/// on Y, never reversing across it toward Alpha.
///
/// <para>The facing is the controller's to give: a bare <c>PUSH Y</c> is a straight-back push that leaves
/// the nose on the stand heading, and the overloads (<c>PUSH Y A1</c>, <c>PUSH Y FACE S</c>) turn it. What
/// this class covers is the bare form, so a red here is about the push onto the lane itself rather than
/// about any facing argument.</para>
///
/// <para>The <c>PUSH A</c> control pushes the same stand straight onto Alpha and taxis out along it: it
/// shares every assertion except the Yankee-specific ones, so a red on the Y case is about the Y geometry
/// and not about pushbacks or the taxi-out turn in general. The faced push and the taxi-out that follows it
/// are pinned in <see cref="SfoYankeeTaxiOutPinTests"/>.</para>
/// </summary>
public class SfoYankeePushTests
{
    private const double OnTaxiwayToleranceFt = 40.0;
    private const double AlignmentToleranceDeg = 20.0;
    private const double MaxTurnDeg = 135.0;
    private const double AboutFaceDeg = 160.0;
    private const int PushBudgetSeconds = 120;
    private const int TaxiObservationSeconds = 45;

    private readonly ITestOutputHelper _output;

    public SfoYankeePushTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// A bare <c>PUSH Y</c> carries no facing, so it is a straight-back push: it stops on Yankee with the
    /// nose still on the B12 stand heading. The controller picks the facing with the overloads
    /// (<c>PUSH Y A1</c>, <c>PUSH Y FACE S</c>) — which is what <see cref="SfoYankeeTaxiOutPinTests"/> exercises.
    /// </summary>
    [Fact]
    public void E75L_PushY_Plain_EndsOnY_NoseUnchanged()
    {
        SfoGround? built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? stand = ground.Layout.FindParkingByName("B12");
        Assert.True(stand is not null, "the SFO layout has no parking named 'B12'");
        double standHeadingDeg = (stand!.TrueHeading ?? new TrueHeading(0)).Degrees;

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "YKE3", "E75L", "B12");
        PushRun push = Push(ground, ac, "PUSH Y");
        AssertRestingOnYankee(ground, ac, push, "PUSH Y");

        double noseDriftDeg = new TrueHeading(standHeadingDeg).AbsAngleTo(ac.TrueHeading);
        _output.WriteLine($"plain PUSH Y: stand heading {standHeadingDeg:F0}°, nose at rest {ac.TrueHeading.Degrees:F0}°, drift {noseDriftDeg:F0}°");
        Assert.True(
            noseDriftDeg <= AlignmentToleranceDeg,
            $"a bare 'PUSH Y' turned the nose {noseDriftDeg:F0}° off the {standHeadingDeg:F0}° stand heading — a taxiway push with no "
                + "facing goes straight back; the facing comes from the overloads ('PUSH Y A1', 'PUSH Y FACE S')"
        );
    }

    /// <summary>
    /// Control: the same stand pushed straight onto Alpha rests on A and taxis out on A with the same
    /// continuous turn, so the Yankee cases' assertions are not measuring pushback or taxi-out in general.
    /// </summary>
    [Fact]
    public void B752_PushA_Control()
    {
        SfoGround? built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "YKE2", "B752", "B12");
        PushRun push = Push(ground, ac, "PUSH A");

        double distAFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "A", ac.Position);
        _output.WriteLine($"PUSH A completed t={push.CompletedSecond}s: distA={distAFt:F0}ft phase={PhaseName(ac)}");
        Assert.True(
            distAFt <= OnTaxiwayToleranceFt,
            $"the push ended {distAFt:F0}ft off taxiway A, past the {OnTaxiwayToleranceFt:F0}ft on-taxiway tolerance"
        );

        TaxiRoute route = SendTaxi(ground, ac, "TAXI A A1 1R");
        List<string> legs = NamedLegs(route);
        Assert.True(legs.Count > 0, "the route has no named-taxiway segments (only ramp lead-outs and junction arcs)");
        Assert.Equal("A", legs[0]);

        TaxiStart taxi = ObserveTaxiStart(ground, ac, "A");
        AssertContinuousTurn(taxi.MaxAbsTurnDeg, "TAXI A A1 1R");
    }

    /// <summary>One pushback run: the second it finished and how close to Alpha it ever got.</summary>
    private readonly record struct PushRun(int CompletedSecond, double MinDistanceToAlphaFt);

    /// <summary>The first seconds of a taxi: the largest turn off the start heading and the first taxiway left for.</summary>
    private readonly record struct TaxiStart(double MaxAbsTurnDeg, string? FirstTaxiwayLeft);

    /// <summary>
    /// Issues a pushback and ticks until it completes, sampling the distance to taxiway A every second the
    /// aircraft is actually reversing.
    /// </summary>
    private PushRun Push(SfoGround ground, AircraftState ac, string command)
    {
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, $"'{command}' from B12 failed: {result.Message}");

        bool everPushed = false;
        double minAlphaFt = double.PositiveInfinity;
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => everPushed && (ac.Phases?.CurrentPhase is not PushbackPhase),
            PushBudgetSeconds,
            _ =>
            {
                if (ac.Phases?.CurrentPhase is not PushbackPhase)
                {
                    return;
                }

                everPushed = true;
                minAlphaFt = Math.Min(minAlphaFt, SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "A", ac.Position));
            }
        );

        Assert.True(
            completed > 0,
            $"'{command}' never completed within {PushBudgetSeconds}s (phase={PhaseName(ac)}, ever in PushbackPhase={everPushed})"
        );
        return new PushRun(completed, minAlphaFt);
    }

    /// <summary>The push came to rest on Yankee: within the on-lane tolerance of Y, and nearer Y than Alpha.</summary>
    private void AssertRestingOnYankee(SfoGround ground, AircraftState ac, PushRun push, string command)
    {
        double distYFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "Y", ac.Position);
        double distAFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "A", ac.Position);
        _output.WriteLine(
            $"'{command}' completed t={push.CompletedSecond}s: distY={distYFt:F0}ft distA={distAFt:F0}ft "
                + $"minDistA={push.MinDistanceToAlphaFt:F0}ft heading={ac.TrueHeading.Degrees:F0}° phase={PhaseName(ac)}"
        );

        Assert.True(
            distYFt <= OnTaxiwayToleranceFt,
            $"the push ended {distYFt:F0}ft off taxilane Y, past the {OnTaxiwayToleranceFt:F0}ft on-lane tolerance"
        );
        Assert.True(distYFt < distAFt, $"the push ended closer to A ({distAFt:F0}ft) than to Y ({distYFt:F0}ft) — it pushed onto the wrong taxiway");
    }

    /// <summary>Issues a taxi clearance, asserts it was accepted, and returns the route it resolved to.</summary>
    private TaxiRoute SendTaxi(SfoGround ground, AircraftState ac, string command)
    {
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, $"'{command}' after the pushback failed: {result.Message}");

        TaxiRoute? route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(_output, route);
        return route;
    }

    /// <summary>
    /// Ticks the taxi out and reports how far the nose ever swung off the heading it started from, plus the
    /// first named taxiway it moved onto after <paramref name="startTaxiway"/> (ramp lead-outs and junction
    /// arcs are transitions, not a change of taxiway).
    /// </summary>
    private TaxiStart ObserveTaxiStart(SfoGround ground, AircraftState ac, string startTaxiway)
    {
        double startHeadingDeg = ac.TrueHeading.Degrees;
        double maxTurnDeg = 0;
        string? firstLeft = null;

        SfoGroundHarness.TickUntil(
            ground.Engine,
            () => false,
            TaxiObservationSeconds,
            _ =>
            {
                maxTurnDeg = Math.Max(maxTurnDeg, Math.Abs(GeoMath.SignedBearingDifference(startHeadingDeg, ac.TrueHeading.Degrees)));
                string? taxiway = ac.Ground.CurrentTaxiway;
                if (
                    (firstLeft is null)
                    && (taxiway is not null)
                    && IsNamedTaxiway(taxiway)
                    && !string.Equals(taxiway, startTaxiway, StringComparison.OrdinalIgnoreCase)
                )
                {
                    firstLeft = taxiway;
                }
            }
        );

        _output.WriteLine(
            $"taxi from {startTaxiway}: start heading {startHeadingDeg:F0}°, max turn {maxTurnDeg:F0}° over "
                + $"{TaxiObservationSeconds}s, first taxiway left for = {firstLeft ?? "(still on start taxiway)"}"
        );
        return new TaxiStart(maxTurnDeg, firstLeft);
    }

    /// <summary>
    /// The turn out of the gate is one continuous turn onto the lane: an aircraft that swings ~180° has
    /// reversed course (turned back past the stand) rather than taxiing out.
    /// </summary>
    private static void AssertContinuousTurn(double maxAbsTurnDeg, string command)
    {
        Assert.True(
            maxAbsTurnDeg < AboutFaceDeg,
            $"'{command}' swung {maxAbsTurnDeg:F0}° off the heading it started the taxi on — that is an about-face (~180°), "
                + "the aircraft reversed course instead of taxiing out"
        );
        Assert.True(
            maxAbsTurnDeg <= MaxTurnDeg,
            $"'{command}' swung {maxAbsTurnDeg:F0}° off the heading it started the taxi on, past the {MaxTurnDeg:F0}° a continuous turn onto the lane takes"
        );
    }

    /// <summary>The route's segments that are legs of a named taxiway, in route order.</summary>
    private static List<string> NamedLegs(TaxiRoute route) => [.. route.Segments.Select(s => s.TaxiwayName).Where(IsNamedTaxiway)];

    /// <summary>
    /// True for a leg of one named taxiway — not the ramp lead-out and not a junction arc, whose name joins
    /// the two taxiways it transitions between (<c>"Y - AY2"</c>).
    /// </summary>
    private static bool IsNamedTaxiway(string taxiwayName) =>
        !string.Equals(taxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase) && !taxiwayName.Contains(" - ", StringComparison.Ordinal);

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";
}
