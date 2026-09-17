using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// An E-pier push and an inbound taxi share the SFO six alley: a B738 at gate E6 pushes back across T6A to
/// spot 6B on the near lane, while an E75L off the 28L bar taxis up that same T6A to its gate at E9. The two
/// legs interleave rather than contend — the arrival is up the lane and parked while the push is still on its
/// first leg, and the push then finishes on 6B behind it. This class pins that the alley keeps flowing:
/// neither aircraft is held for the other, both come to rest where they were sent, and they never close
/// inside the 90 ft floor.
///
/// <para>It began as the red pin for the F-6 wedge, where the arrival was held at the alley mouth until the
/// push tail was already in its lane and the two stranded each other; it now guards against that
/// regression.</para>
/// </summary>
public class SfoSixAlleyArrivalAheadOfPushTests
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
    /// up T6A never enters the T6B lane that spot 6B sits on. The path it flies comes no closer than about
    /// 140 ft to where the push comes to rest on 6B — against the 134.55 ft two half-spans plus the wingtip
    /// buffer ask for — where a D gate would have routed the arrival straight over the stopped aircraft.
    /// </summary>
    private const string AlleyGate = "E9";
    private const double SpotToleranceMarginFt = 25.0;
    private const double SpotHeadingToleranceDeg = 15.0;
    private const double MinSeparationFt = 90.0;
    private const int ChoreographyBudgetSeconds = 400;

    /// <summary>
    /// The arrival's unimpeded run from the 28L bar up T6A to E9 measures 75 s. Being held for the push costs
    /// tens of seconds at the alley mouth, so parking later than this budget means the alley contended.
    /// </summary>
    private const int ArrivalUnimpededBudgetSeconds = 100;

    private readonly ITestOutputHelper _output;

    public SfoSixAlleyArrivalAheadOfPushTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Push and taxi are issued in the same second: the E6 pusher reverses out across T6A — the lane the
    /// arrival is cleared up — and on toward the D-side lane's spot 6B, while the arrival taxis up T6A to its
    /// gate on the E pier. The arrival takes T6A into the alley unimpeded while the push is still working its
    /// first leg, and parks at E9 inside its unimpeded budget; the push then completes behind it, resting
    /// nose-out on 6B. Neither is held for the other and the two never come inside the 90 ft floor — the point
    /// of the lane split, so the arrival's gate has to be one T6A actually serves.
    /// </summary>
    [Fact]
    public void ArrivalCrossesAlleyAheadOfPush_PushCompletes()
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
            $"push finished t={run.PusherDoneSecond}s, arrival parked t={run.ArrivalParkedSecond}s, "
                + $"yielded={run.Yielded}, min separation {run.MinSeparationFt:F0}ft"
        );

        Assert.True(
            run.PusherDoneSecond > 0,
            $"the pusher never completed {pushCommand} within {ChoreographyBudgetSeconds}s (phase={PhaseName(alley.Pusher)})"
        );
        Assert.True(
            run.ArrivalParkedSecond > 0,
            $"the arrival never reached {AlleyGate} within {ChoreographyBudgetSeconds}s (phase={PhaseName(alley.Arrival)})"
        );
        Assert.True(
            run.ArrivalParkedSecond <= ArrivalUnimpededBudgetSeconds,
            $"the arrival took {run.ArrivalParkedSecond}s to reach {AlleyGate} against an unimpeded budget of "
                + $"{ArrivalUnimpededBudgetSeconds}s: it was held up on its way through the alley (yielded={run.Yielded})"
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
    private readonly record struct ChoreographyRun(int PusherDoneSecond, int ArrivalParkedSecond, bool Yielded, double MinSeparationFt);

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
        int pusherDone = -1;
        int arrivalParked = -1;

        SfoGroundHarness.TickUntil(
            alley.Ground.Engine,
            () => (pusherDone > 0) && (arrivalParked > 0),
            ChoreographyBudgetSeconds,
            second =>
            {
                guard.Tick(second);
                double separationFt = DistanceFt(alley.Pusher.Position, alley.Arrival.Position);
                minSeparationFt = Math.Min(minSeparationFt, separationFt);
                everPushed |= alley.Pusher.Phases?.CurrentPhase is PushbackPhase;
                yielded |= IsGivingWay(alley);
                if ((pusherDone < 0) && everPushed && (alley.Pusher.Phases?.CurrentPhase is HoldingAfterPushbackPhase))
                {
                    pusherDone = second;
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

        return new ChoreographyRun(pusherDone, arrivalParked, yielded, minSeparationFt);
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
