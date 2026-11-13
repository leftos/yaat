using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The tug's own pace through a turn entry. The ground speed physics integrates is the main gear's, commanded at
/// <c>5·cos δ</c> (<c>δ</c> the nose-gear steer angle), and the tug on its towbar runs at the gear speed over
/// <c>cos δ</c>. A turn's first step takes the full steer angle at once while the gear sheds speed only at the towbar's
/// 1 kt/s, so the tow brakes ahead of each turn entry onto <c>5·cos δ</c> of that turn.
///
/// <para>Each tow, flown whole, keeps the tug at or under <see cref="TugCeilingKts"/> on every physics sub-tick
/// (<see cref="SubTickDriver"/>: the overrun lasts about a second), never runs its main gear over the tug's pace
/// (<see cref="CategoryPerformance.PushbackSpeed"/> for the type's category), and stays within <see cref="MaxOffTrackFt"/> of the track the planner simulated. Each case's
/// first turn is entered at the steer angle named: a routine-radius turn (δ ≈ 45°) or an explicit <c>PUSH FACE</c>
/// rotating the nose more than 135°, flown at the tight radius (δ = 67.5°).</para>
/// </summary>
public class PushTurnEntryTugPaceTests(ITestOutputHelper output)
{
    /// <summary>
    /// The most the tug may run, knots; a judgement ceiling: the 5.1 kt line (the 5 kt pace with a 0.1 kt allowance)
    /// plus the one sub-tick at a roll-out turn-in where the steer angle flown exceeds the planned one the gear was
    /// braked for: <c>PushbackPhase.Drive</c> commands the gear from the probe step's steer angle and flies the shorter
    /// step, whose steer angle is a little larger.
    /// </summary>
    private const double TugCeilingKts = 5.11;

    /// <summary>Float noise allowed on the gear speed against the tug's pace, knots; not an allowance.</summary>
    private const double NoiseKts = 1e-6;

    /// <summary>The worst flown-vs-planned deviation pushback.md's 5 ft margin is built on, feet.</summary>
    private const double MaxOffTrackFt = 1.19;

    private const int BudgetSeconds = 300;

    [Theory]
    [InlineData("F8", "B738", "PUSH $7A", 45.0)]
    [InlineData("E1", "B738", "PUSH $7", 45.0)]
    [InlineData("D15", "B738", "PUSH $6A", 42.9)]
    [InlineData("E12", "B738", "PUSH $7B", 45.0)]
    [InlineData("D2", "E75L", "PUSH $5A", 45.0)]
    [InlineData("F8", "B738", "PUSH FACE NE", 67.5)]
    [InlineData("F8", "B738", "PUSH FACE E", 67.5)]
    [InlineData("E1", "B738", "PUSH FACE N", 67.5)]
    public void TurnEntry_TugKeepsUnderTheCeiling(string gate, string type, string command, double entrySteerDeg)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL1", type, gate);
        var start = new PushbackPose(ac.Position, ac.TrueHeading.Degrees);
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, result.Message);

        List<PushbackPhase> phases = [.. ac.Phases!.Phases.OfType<PushbackPhase>()];
        TugSimulation planned = TugKinematics.Simulate(
            start,
            [.. phases.Select(p => p.Move)],
            AircraftFootprint.FromType(type),
            TugMovePlanner.StepFt
        );
        List<Sample> samples = [];
        AircraftState? done = SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            ac.Callsign,
            a => a.Phases?.CurrentPhase is HoldingAfterPushbackPhase,
            BudgetSeconds,
            a =>
            {
                if (a.Phases?.CurrentPhase is not PushbackPhase running)
                {
                    return;
                }

                double steerDeg = a.Ground.TowbarTrueHeading is { } towbar ? towbar.AbsAngleTo(a.TrueHeading) : 0.0;
                double offTrackFt = OffTrackFt(planned, phases.IndexOf(running), a.Position);
                samples.Add(new Sample(a.IndicatedAirspeed / Math.Cos(steerDeg * Math.PI / 180.0), a.IndicatedAirspeed, steerDeg, offTrackFt));
            }
        );

        string tow = $"{gate} {command} ({type})";
        Assert.True(done is not null, $"{tow} did not finish within {BudgetSeconds} s");
        Assert.True(
            samples.Any(s => s.SteerDeg >= entrySteerDeg - 0.5),
            $"{tow} never steered at {entrySteerDeg:F1}°, so it no longer exercises that turn entry"
        );

        double paceKts = CategoryPerformance.PushbackSpeed(AircraftCategorization.Categorize(type));
        Sample peak = samples.MaxBy(s => s.TugKts)!;
        double secondsOverCeiling = samples.Count(s => s.TugKts > TugCeilingKts) / (double)SimulationEngine.PhysicsSubTickRate;
        double peakGearKts = samples.Max(s => s.GearKts);
        double worstOffTrackFt = samples.Max(s => s.OffTrackFt);
        output.WriteLine(
            $"{tow}: peak tug {peak.TugKts:F3} kt (gear {peak.GearKts:F2} kt, steer {peak.SteerDeg:F1}°); "
                + $"{secondsOverCeiling:F2} s over {TugCeilingKts:F2} kt; peak gear {peakGearKts:F3} kt; worst off track {worstOffTrackFt:F2} ft"
        );

        Assert.True(
            peak.TugKts <= TugCeilingKts,
            $"{tow}: the tug ran {peak.TugKts:F3} kt (gear {peak.GearKts:F2} kt at {peak.SteerDeg:F1}° of steer), over {TugCeilingKts:F2} kt"
        );
        Assert.True(peakGearKts <= paceKts + NoiseKts, $"{tow}: the main gear ran {peakGearKts:F3} kt, over the {paceKts:F1} kt pace");
        Assert.True(
            worstOffTrackFt <= MaxOffTrackFt,
            $"{tow}: the flown path came {worstOffTrackFt:F2} ft off the planned track, more than {MaxOffTrackFt:F2} ft"
        );
    }

    /// <summary>
    /// How far a flown point lies from the planned track, feet: the nearest of the running move's trace and its two
    /// neighbours', so a boundary crossed a sub-tick early or late is not counted as a deviation.
    /// </summary>
    private static double OffTrackFt(TugSimulation planned, int move, LatLon point)
    {
        int first = Math.Max(0, move - 1);
        int last = Math.Min(planned.Moves.Count - 1, move + 1);
        double best = double.PositiveInfinity;
        for (int m = first; m <= last; m++)
        {
            IReadOnlyList<PushbackPose> trace = planned.Moves[m].Samples;
            for (int i = 1; i < trace.Count; i++)
            {
                best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, trace[i - 1].Position, trace[i].Position));
            }
        }

        return best;
    }

    private sealed record Sample(double TugKts, double GearKts, double SteerDeg, double OffTrackFt);
}
