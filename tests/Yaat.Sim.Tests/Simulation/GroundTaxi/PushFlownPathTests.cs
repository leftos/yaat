using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The path a push actually flies has to stay inside the margin the planner cleared it against: the aircraft's outline
/// keeps <see cref="TugTaxiwayClearance.ClearanceMarginFt"/> from a taxiway's object-free half-width along the
/// <em>planned</em> trace, so a flown path that wanders further off that trace than the margin can brush a taxiway the
/// plan was chosen to miss.
///
/// <para>Two departures are pinned here, one per assertion. The step the phase steers has to be the distance physics is
/// about to move the aircraft — the airspeed after this sub-tick's tow acceleration or braking — or a braking step turns
/// less than the distance it covers allows and the aircraft drifts wide of the plan. And each move has to steer on its
/// first sub-tick: physics moves the aircraft before the next tick reaches the new move's <c>OnTick</c>, so a move that
/// waits one sub-tick runs its first couple of feet along the previous move's direction.</para>
/// </summary>
public class PushFlownPathTests
{
    private readonly ITestOutputHelper _output;

    public PushFlownPathTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("D2", "E75L", "PUSH $5A", "D1")]
    [InlineData("D2", "E75L", "PUSH $5A", null)]
    [InlineData("F8", "B738", "PUSH $7A", null)]
    public void FlownPath_StaysWithinTheClearanceMarginOfThePlan(string gate, string type, string command, string? occupiedStand)
    {
        if (SfoGroundHarness.Build(_output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        if (occupiedStand is not null)
        {
            SfoGroundHarness.SpawnParked(ground, "SKW3398", "E75L", occupiedStand);
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "SKW3396", type, gate);
        var start = new PushbackPose(ac.Position, ac.TrueHeading.Degrees);
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, result.Message);
        List<PushbackPhase> phases = [.. ac.Phases!.Phases.OfType<PushbackPhase>()];
        var footprint = AircraftFootprint.FromType(type);
        double radiusFt = TugKinematics.TurnRadiusFt(footprint, tight: false);
        TugSimulation planned = TugKinematics.Simulate(start, [.. phases.Select(p => p.Move)], footprint, TugMovePlanner.StepFt);

        List<(int Move, PushbackPose Pose)> flown = FlyPhases(ground.Engine, ac, phases);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        (double worstOffFt, double worstRatio, double worstOverRatio) = Measure(flown, phases, planned, radiusFt);
        _output.WriteLine($"{command} from {gate} in a {type}: worst off track {worstOffFt:F2} ft, turn ratio {worstRatio:F2}..{worstOverRatio:F2}");

        Assert.True(
            worstRatio >= 0.99,
            $"a saturated turning step turned only {worstRatio:F2} of the curvature the radius allows over the distance it moved"
        );
        Assert.True(
            worstOverRatio <= 1.01,
            $"a saturated turning step turned {worstOverRatio:F2} of the curvature the radius allows over the distance it moved: "
                + "tighter than the radius"
        );
        Assert.True(
            worstOffFt <= TugTaxiwayClearance.ClearanceMarginFt,
            $"the flown path ran {worstOffFt:F2} ft off the planned track, past the {TugTaxiwayClearance.ClearanceMarginFt:F0} ft margin"
        );
    }

    /// <summary>
    /// Runs the tow a physics sub-tick at a time, recording where the aircraft was after each one and which move of the
    /// plan was running then, the first entry being the pose the push started from.
    /// </summary>
    private static List<(int Move, PushbackPose Pose)> FlyPhases(SimulationEngine engine, AircraftState ac, List<PushbackPhase> phases)
    {
        var flown = new List<(int Move, PushbackPose Pose)> { (0, new PushbackPose(ac.Position, ac.TrueHeading.Degrees)) };
        for (int second = 0; (second < 300) && (ac.Phases?.CurrentPhase is PushbackPhase); second++)
        {
            engine.BeginSecond();
            engine.OpenSecond(engine.BareHost);
            engine.RunPrePhysics(engine.BareHost);
            for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
            {
                engine.RunPhysicsSubTick(1.0 / SimulationEngine.PhysicsSubTickRate, sub);
                if (ac.Phases?.CurrentPhase is PushbackPhase running)
                {
                    flown.Add((phases.IndexOf(running), new PushbackPose(ac.Position, ac.TrueHeading.Degrees)));
                }
            }

            engine.RunPostPhysics(engine.BareHost);
            engine.RunEndOfSecond(engine.BareHost);
        }

        return flown;
    }

    /// <summary>
    /// The three departures over the flown sub-ticks: the farthest any pose sat from the planned trace, against the move
    /// that was running and its neighbours (a pose at a move boundary belongs to both), and the least and the greatest
    /// share of the curvature the turn radius allows that a step actually turned while the move was asking for all of it
    /// — the first catches a step that turns too little for the distance it moved, the second one that turns too much.
    /// </summary>
    private static (double OffFt, double Ratio, double OverRatio) Measure(
        List<(int Move, PushbackPose Pose)> flown,
        List<PushbackPhase> phases,
        TugSimulation planned,
        double radiusFt
    )
    {
        double worstOffFt = 0.0;
        double worstRatio = 1.0;
        double worstOverRatio = 1.0;
        for (int k = 1; k < flown.Count; k++)
        {
            (int move, PushbackPose pose) = flown[k];
            PushbackPose before = flown[k - 1].Pose;
            TugMove tug = phases[move].Move;
            int lo = Math.Max(0, move - 1);
            int hi = Math.Min(phases.Count, move + 2);
            worstOffFt = Math.Max(worstOffFt, Enumerable.Range(lo, hi - lo).Min(m => OffTrackFt(planned.Moves[m].Samples, pose.Position)));

            double stepFt = GeoMath.DistanceNm(before.Position, pose.Position) * GeoMath.FeetPerNm;
            if (stepFt < 0.05)
            {
                continue;
            }

            double travelBefore = before.TravelTrueDeg(tug.Kind);
            double turnDeg = new TrueHeading(travelBefore).SignedAngleTo(new TrueHeading(pose.TravelTrueDeg(tug.Kind)));
            double allowedDeg = stepFt / radiusFt * 180.0 / Math.PI;
            var progress = new TugMoveProgress(before.Position, travelBefore, 0.0, false, 0.0);
            double wantDeg = new TrueHeading(travelBefore).SignedAngleTo(
                new TrueHeading(TugKinematics.CommandedTravelDeg(before, tug, progress, radiusFt))
            );
            if (Math.Abs(wantDeg) > allowedDeg * 1.05)
            {
                double ratio = Math.Sign(wantDeg) * turnDeg / allowedDeg;
                worstRatio = Math.Min(worstRatio, ratio);
                worstOverRatio = Math.Max(worstOverRatio, ratio);
            }
        }

        return (worstOffFt, worstRatio, worstOverRatio);
    }

    /// <summary>How far <paramref name="point"/> lies from the nearest of the sample-to-sample segments, feet.</summary>
    private static double OffTrackFt(IReadOnlyList<PushbackPose> samples, LatLon point)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < samples.Count; i++)
        {
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, samples[i - 1].Position, samples[i].Position));
        }

        return best;
    }
}
