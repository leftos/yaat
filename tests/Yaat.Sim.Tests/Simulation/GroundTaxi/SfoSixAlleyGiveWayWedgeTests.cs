using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Giving way in the SFO six alley has to leave the push somewhere to go. An arrival rolling up T6A toward
/// an E gate meets an E-pier pusher reversing across that same lane on its way to spot 6B on the far side:
/// the arrival stops at the alley entrance and waits, the push crosses T6A and finishes on T6B, and the
/// arrival — now with a clear lane — carries on to its gate at E9, passing the parked pusher a lane away.
///
/// <para>That is what a ground controller watching the alley expects to see, and it is what this class pins:
/// the give-way is only correct if the aircraft that gave way does not become the obstacle that strands the
/// push. The arrival holds inside the pushback buffer, so the push's remaining leg runs past a stationary
/// aircraft rather than empty pavement, and the two must still clear each other.</para>
/// </summary>
public class SfoSixAlleyGiveWayWedgeTests
{
    private const string Pusher = "PSH1";
    private const string Arrival = "ARR1";
    private const string PusherType = "B738";
    private const string ArrivalType = "E75L";

    /// <summary>
    /// The pusher's stand: an E-pier gate, so the tail crosses T6A — the lane the arrival is cleared up — on
    /// its way across the alley to spot 6B.
    /// </summary>
    private const string AlleyPusherGate = "E6";

    /// <summary>The spot the push ends on: 6B, on the near lane T6B, the other side of the alley from the arrival.</summary>
    private const string AlleySpot = "6B";

    /// <summary>
    /// The arrival's gate: an E-pier stand whose lead-in hangs off T6A, a little past spot 6A, so a clearance
    /// up T6A never enters the T6B lane that spot 6B sits on. Its resolved route clears the pusher parked on
    /// 6B by 194 ft — against the 134.55 ft two half-spans plus the wingtip buffer ask for — where a D gate
    /// would have routed the arrival straight over the parked aircraft.
    /// </summary>
    private const string AlleyGate = "E9";
    private const double SpotToleranceMarginFt = 25.0;
    private const double SpotHeadingToleranceDeg = 15.0;
    private const double MinSeparationFt = 90.0;
    private const double YieldProximityFt = 200.0;
    private const int ChoreographyBudgetSeconds = 400;

    private readonly ITestOutputHelper _output;

    public SfoSixAlleyGiveWayWedgeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Push and taxi are issued in the same second: the E-pier pusher reverses out across T6A — the lane the
    /// arrival is cleared up — and on to the D-side lane's spot 6B, while the arrival taxis up T6A to its gate
    /// on the E pier. The arrival is inside the pushback buffer by the time the tail is in its lane, so it
    /// gives way and holds; the tail then leaves T6A and the push finishes on T6B instead of stopping against
    /// the aircraft holding for it. The pusher parks first and the arrival then reaches its gate, passing the
    /// pusher parked on 6B a lane away (140 ft, against the 134.55 ft two half-spans plus the wingtip buffer
    /// ask for), and the two never come inside 90 ft — the whole point of the lane split, so the arrival's
    /// gate has to be one T6A actually serves.
    /// </summary>
    // FAILS: give-way engages only once the push tail is in the lane (~143 ft); the remaining leg to 6B then passes
    // 125 ft from the held arrival (< 134.55 ft) and both wedge — finding F-6 in docs/plans/sfo-ground-technique-tests.md
    [Fact(
        Skip = "Red pin for finding F-6 (docs/plans/sfo-ground-technique-tests.md): give-way to a crossing push engages too late and the pair wedges — un-skip when the mover holds at the alley entrance"
    )]
    public void ArrivalGivesWayToCrossingPush_PushCompletes()
    {
        var setup = Setup(AlleyPusherGate, AlleySpot, AlleyGate);
        if (setup is null)
        {
            return;
        }

        var alley = setup.Value;
        var engine = alley.Ground.Engine;
        var guard = new DeadlockGuard(alley.Pusher, alley.Arrival);

        string pushCommand = $"PUSH ${AlleySpot}";
        var push = engine.SendCommand(Pusher, pushCommand);
        Assert.True(push.Success, $"'{pushCommand}' from {AlleyPusherGate} failed: {push.Message}");
        string clearance = $"TAXI T A T6A @{AlleyGate}";
        var taxi = engine.SendCommand(Arrival, clearance);
        Assert.True(taxi.Success, $"'{clearance}' from the 28L bar on T failed: {taxi.Message}");

        var run = RunChoreography(alley, guard);
        _output.WriteLine(
            $"pusher parked t={run.PusherParkedSecond}s, arrival parked t={run.ArrivalParkedSecond}s, "
                + $"yielded={run.Yielded}, min separation {run.MinSeparationFt:F0}ft"
        );

        Assert.True(
            run.PusherParkedSecond > 0,
            $"the pusher never completed {pushCommand} within {ChoreographyBudgetSeconds}s (phase={PhaseName(alley.Pusher)})"
        );
        Assert.True(
            run.ArrivalParkedSecond > 0,
            $"the arrival never reached {AlleyGate} within {ChoreographyBudgetSeconds}s (phase={PhaseName(alley.Arrival)})"
        );
        Assert.True(
            run.PusherParkedSecond <= run.ArrivalParkedSecond,
            $"the arrival parked at t={run.ArrivalParkedSecond}s, before the pusher finished its push at t={run.PusherParkedSecond}s"
        );
        Assert.True(
            run.Yielded,
            "the arrival never gave way to the pushing B738: it was never annotated as auto-yielding to PSH1 and never stopped "
                + $"within {YieldProximityFt:F0}ft of it while the push was running"
        );
        Assert.True(
            run.MinSeparationFt >= MinSeparationFt,
            $"the two came within {run.MinSeparationFt:F0}ft of each other (floor {MinSeparationFt:F0}ft) across the {ChoreographyBudgetSeconds}s run"
        );

        AssertRestingOnSpot(alley.Pusher, alley.Spot, alley.Ground.Layout);
        double finalFt = DistanceFt(alley.Arrival.Position, alley.Parking.Position);
        Assert.True(
            finalFt <= TaxiCoverageRunner.ParkingArrivalToleranceFt,
            $"the arrival stopped {finalFt:F0}ft from the {AlleyGate} parking node, past the {TaxiCoverageRunner.ParkingArrivalToleranceFt:F0}ft arrival tolerance"
        );
    }

    /// <summary>The two aircraft of one alley run plus the layout features they are aimed at.</summary>
    private readonly record struct Alley(SfoGround Ground, AircraftState Pusher, AircraftState Arrival, GroundNode Spot, GroundNode Parking);

    /// <summary>What the concurrent push/taxi run produced: when each stopped, whether the arrival gave way, and the closest approach.</summary>
    private readonly record struct ChoreographyRun(int PusherParkedSecond, int ArrivalParkedSecond, bool Yielded, double MinSeparationFt);

    /// <summary>
    /// Builds the SFO world with the pusher parked at <paramref name="pusherGate"/> and the arrival at the
    /// 28L bar on T, or null when the layout or navdata is unavailable (the harness's silent-skip
    /// convention).
    /// </summary>
    /// <param name="pusherGate">Stand the pusher starts on.</param>
    /// <param name="spotName">Spot the pusher is pushed to.</param>
    /// <param name="gateName">Gate the arrival taxis to.</param>
    /// <returns>The built alley, or null to skip.</returns>
    private Alley? Setup(string pusherGate, string spotName, string gateName)
    {
        var built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return null;
        }

        var ground = built.Value;
        var spot = ground.Layout.FindSpotNodeByName(spotName);
        var parking = ground.Layout.FindParkingByName(gateName);
        Assert.True(spot is not null, $"the SFO layout has no spot named '{spotName}'");
        Assert.True(parking is not null, $"the SFO layout has no parking named '{gateName}'");

        var pusher = SfoGroundHarness.SpawnParked(ground, Pusher, PusherType, pusherGate);
        var arrival = SfoGroundHarness.SpawnAtHoldShort(ground, Arrival, ArrivalType, ("28L", "T", "B"));
        return new Alley(ground, pusher, arrival, spot!, parking!);
    }

    /// <summary>
    /// Ticks the concurrent push and taxi to completion, watching for the arrival giving way, the closest
    /// approach, and the second each aircraft comes to rest, with a trace line every ten seconds.
    /// </summary>
    private ChoreographyRun RunChoreography(Alley alley, DeadlockGuard guard)
    {
        bool everPushed = false;
        bool yielded = false;
        double minSeparationFt = double.PositiveInfinity;
        int pusherParked = -1;
        int arrivalParked = -1;

        SfoGroundHarness.TickUntil(
            alley.Ground.Engine,
            () => (pusherParked > 0) && (arrivalParked > 0),
            ChoreographyBudgetSeconds,
            second =>
            {
                guard.Tick(second);
                double separationFt = DistanceFt(alley.Pusher.Position, alley.Arrival.Position);
                minSeparationFt = Math.Min(minSeparationFt, separationFt);
                everPushed |= alley.Pusher.Phases?.CurrentPhase is PushbackPhase;
                yielded |= IsGivingWay(alley);
                if ((pusherParked < 0) && everPushed && (alley.Pusher.Phases?.CurrentPhase is AtParkingPhase))
                {
                    pusherParked = second;
                }

                if ((arrivalParked < 0) && (alley.Arrival.Phases?.CurrentPhase is AtParkingPhase))
                {
                    arrivalParked = second;
                }

                if (second % 10 == 0)
                {
                    _output.WriteLine($"t={second, 3}s {Describe(alley.Pusher)} | {Describe(alley.Arrival)} | sep={separationFt:F0}ft");
                }
            }
        );

        return new ChoreographyRun(pusherParked, arrivalParked, yielded, minSeparationFt);
    }

    /// <summary>
    /// The arrival is giving way to a push that is actually running: the detector has annotated it as
    /// auto-yielding to the pusher while the pusher is still rolling. Keyed on the annotation rather than on
    /// "stopped nearby", so it cannot be satisfied by an arrival that stopped for something else.
    /// </summary>
    private static bool IsGivingWay(Alley alley)
    {
        if (alley.Pusher.Phases?.CurrentPhase is not PushbackPhase)
        {
            return false;
        }

        return string.Equals(alley.Arrival.Ground.AutoYieldTarget, Pusher, StringComparison.OrdinalIgnoreCase)
            && (alley.Pusher.GroundSpeed > SfoGroundHarness.StationarySpeedKts);
    }

    /// <summary>
    /// A completed <c>PUSH $spot</c> rests with the nosewheel on the marking — centroid a half fuselage
    /// back — and the nose on the spot's outbound heading, ready to taxi out.
    /// </summary>
    private static void AssertRestingOnSpot(AircraftState ac, GroundNode spot, AirportGroundLayout layout)
    {
        double halfLengthFt = (FaaAircraftDatabase.Get(ac.AircraftType)?.LengthFt ?? 110.0) / 2.0;
        double distFt = DistanceFt(ac.Position, spot.Position);
        Assert.True(
            distFt <= halfLengthFt + SpotToleranceMarginFt,
            $"{ac.Callsign} rested {distFt:F0}ft from the spot — a nose-at-spot setback is ~{halfLengthFt:F0}ft "
                + $"(tolerance +{SpotToleranceMarginFt:F0}ft)"
        );

        Assert.True(
            layout.TryGetSpotOutboundHeading(spot, out double outBearing),
            $"the layout carries no outbound heading for the spot {ac.Callsign} pushed to"
        );
        double headingErrDeg = new TrueHeading(outBearing).AbsAngleTo(ac.TrueHeading);
        Assert.True(
            headingErrDeg <= SpotHeadingToleranceDeg,
            $"{ac.Callsign} rested on heading {ac.TrueHeading.Degrees:F0}° — expected ~{outBearing:F0}° (nose out along the lane), off by {headingErrDeg:F0}°"
        );
    }

    private static string Describe(AircraftState ac) =>
        $"{ac.Callsign} {PhaseName(ac)} gs={ac.GroundSpeed:F1}kt lim={ac.Ground.SpeedLimit?.ToString("F1") ?? "-"} yield={ac.Ground.AutoYieldTarget ?? "-"}";

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";
}
