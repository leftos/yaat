using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Tow pace and the pull onto a lane, flown end to end through <see cref="PushbackPhase"/> and physics: SFO gate E1
/// <c>PUSH $7</c> in a B738, which took 136 s with the nose swinging from 328° through 84° and back onto the lane's 28°.
/// The tug holds <see cref="CategoryPerformance.PushbackSpeed"/> through turns — the main gear slows by the cosine of
/// the nose-gear steer angle — a creep move slows to <see cref="CategoryPerformance.PushbackAlignSpeed"/> only over its
/// last <see cref="PushbackPhase.AlignCreepFt"/>, and the pull onto the lane turns onto the lane heading without passing
/// it. The E1 tow is flown once for the whole class (<see cref="Fixture"/>).
/// </summary>
public class PushTowSpeedTests(PushTowSpeedTests.Fixture fixture, ITestOutputHelper output) : IClassFixture<PushTowSpeedTests.Fixture>
{
    private const string AircraftType = "B738";
    private const string Gate = "E1";
    private const string Spot = "7";
    private const int BudgetSeconds = 300;

    /// <summary>How far past the lane heading the nose may swing on the pull onto the lane, degrees.</summary>
    private const double MaxPastLaneDeg = 5.0;

    /// <summary>How far off the push speed the tug may run in a settled turning second, knots.</summary>
    private const double TugPaceToleranceKts = 0.2;

    /// <summary>A second whose ground speed changed by more than this is the tug still settling onto its pace, knots.</summary>
    private const double SettledKts = 0.05;

    /// <summary>A second in which the nose turned by more than this is a turning second, degrees.</summary>
    private const double TurnNoticedDeg = 1.0;

    /// <summary>
    /// How far from a move's planned end a second must be to lie outside every stop curve, feet: from 5 kt at the tug's
    /// 1 kt/s brake the run in onto the 1 kt crawl 10 ft out takes about 30 ft.
    /// </summary>
    private const double ClearOfStopCurveFt = 40.0;

    /// <summary>
    /// Where the braking curve onto the alignment creep starts, feet from the creep move's end: the tug's 1 kt/s brake
    /// takes the jet's 5 kt push speed down to its 3 kt alignment creep over (5² − 3²) / 2 = 8 kt·s, 13.5 ft, before the
    /// <see cref="PushbackPhase.AlignCreepFt"/> stretch — 43.5 ft out. A second between the stretch and this point is
    /// braking onto the creep yet still commands the push speed: the commanded pace is the move's, not the curve's.
    /// </summary>
    private const double AlignBrakeStartFt = 43.5;

    [Fact]
    public void E1To7_FinalPull_HeadingMonotone_NoOvershoot()
    {
        if (fixture.Get(output) is not { } run)
        {
            return;
        }

        PushbackPhase final = run.Moves[^1];
        Assert.Equal(PushbackLegKind.Pull, final.Move.Kind);
        List<Sample> pull = [.. run.Samples.Where(s => ReferenceEquals(s.Phase, final))];
        Assert.NotEmpty(pull);
        double startOffDeg = Signed(pull[0].NoseDeg, run.LaneDeg);
        double side = startOffDeg >= 0.0 ? 1.0 : -1.0;
        double pastDeg = pull.Max(s => -side * Signed(s.NoseDeg, run.LaneDeg));
        output.WriteLine(
            $"final pull from nose {pull[0].NoseDeg:F1}° onto the lane's {run.LaneDeg:F1}°: noses {string.Join(" ", pull.Select(s => $"{s.NoseDeg:F0}"))}; "
                + $"{pastDeg:F1}° past the lane heading"
        );
        Assert.True(
            pastDeg <= MaxPastLaneDeg,
            $"the pull onto the lane swung the nose {pastDeg:F1}° past the lane's {run.LaneDeg:F1}° (started on {pull[0].NoseDeg:F1}°)"
        );
    }

    /// <summary>
    /// In every settled turning second clear of a stop curve, the tug — the main gear's speed over the cosine of the
    /// nose-gear steer angle — runs at the push speed, while the main gear itself slows through the turn.
    /// </summary>
    [Fact]
    public void TurningTow_TugHoldsPushbackSpeed()
    {
        if (fixture.Get(output) is not { } run)
        {
            return;
        }

        double pushKts = CategoryPerformance.PushbackSpeed(AircraftCategory.Jet);
        var judged = new List<Sample>();
        List<Sample> samples = run.Samples;
        for (int i = 1; i < samples.Count; i++)
        {
            (Sample before, Sample now) = (samples[i - 1], samples[i]);
            bool sameMove = (now.Phase is not null) && ReferenceEquals(now.Phase, before.Phase);
            bool turning = new TrueHeading(before.NoseDeg).AbsAngleTo(new TrueHeading(now.NoseDeg)) > TurnNoticedDeg;
            bool clearOfStops = (now.ToEndFt > ClearOfStopCurveFt) && (before.ToEndFt > ClearOfStopCurveFt);
            bool settled = Math.Abs(now.GroundSpeedKts - before.GroundSpeedKts) <= SettledKts;
            if (sameMove && turning && clearOfStops && settled)
            {
                judged.Add(now);
            }
        }

        output.WriteLine(
            $"turning seconds (gear kt / steer° / tug kt): {string.Join(" ", judged.Select(s => $"{s.Second}:{s.GroundSpeedKts:F2}/{s.SteerDeg:F0}/{TugKts(s):F2}"))}"
        );
        Assert.True(judged.Count >= 3, $"only {judged.Count} settled turning second(s) clear of a stop curve: {Trace(run)}");
        foreach (Sample s in judged)
        {
            Assert.True(
                Math.Abs(TugKts(s) - pushKts) <= TugPaceToleranceKts,
                $"at t={s.Second}s the tug ran {TugKts(s):F2} kt (gear {s.GroundSpeedKts:F2} kt, steer {s.SteerDeg:F1}°), not the {pushKts:F1} kt push speed: "
                    + Trace(run)
            );
        }

        Assert.Contains(judged, s => s.GroundSpeedKts < pushKts - 1.0);
    }

    [Fact]
    public void CreepMove_AlignSpeedOnlyLast30Ft()
    {
        if (fixture.Get(output) is not { } run)
        {
            return;
        }

        PushbackPhase creep = run.Moves[^1];
        Assert.True(creep.Move.Creep, "E1 $7's last move is not a creep");
        double pushKts = CategoryPerformance.PushbackSpeed(AircraftCategory.Jet);
        double alignKts = CategoryPerformance.PushbackAlignSpeed(AircraftCategory.Jet);
        List<Sample> seconds = [.. run.Samples.Where(s => ReferenceEquals(s.Phase, creep))];
        output.WriteLine($"creep: {string.Join(" ", seconds.Select(s => $"{s.ToEndFt:F0}ft:{s.CommandedKts:F1}"))}");
        Assert.Contains(seconds, s => (s.ToEndFt > PushbackPhase.AlignCreepFt + 1.0) && (s.ToEndFt < AlignBrakeStartFt));
        foreach (Sample s in seconds)
        {
            if (s.ToEndFt > PushbackPhase.AlignCreepFt + 1.0)
            {
                Assert.True(
                    s.CommandedKts == pushKts,
                    $"{s.ToEndFt:F1} ft from the end the creep commanded {s.CommandedKts:F1} kt, not {pushKts:F1}"
                );
            }
            else if (s.ToEndFt < PushbackPhase.AlignCreepFt - 1.0)
            {
                Assert.True(
                    s.CommandedKts == alignKts,
                    $"{s.ToEndFt:F1} ft from the end the creep commanded {s.CommandedKts:F1} kt, not {alignKts:F1}"
                );
            }
        }
    }

    /// <summary>E1 <c>PUSH $7</c> flies in 115 s with the main gear slowed through the turns (109 s at a constant gear speed).</summary>
    [Fact]
    public void E1To7_CompletesWithin115s()
    {
        if (fixture.Get(output) is not { } run)
        {
            return;
        }

        output.WriteLine($"E1 $7 done at t={run.DoneSecond}s");
        Assert.True(run.DoneSecond > 0, $"E1 $7 never completed in {BudgetSeconds}s: {Trace(run)}");
        Assert.True(run.DoneSecond <= 115, $"E1 $7 took {run.DoneSecond}s: {Trace(run)}");
    }

    /// <summary>How long the review cases' tows take end to end, reported; each must complete inside the budget.</summary>
    [Theory]
    [InlineData("E12", "B738", "PUSH $7B")]
    [InlineData("F8", "CRJ7", "PUSH $7A")]
    [InlineData("D7", "B738", "PUSH A F1")]
    [InlineData("D15", "B738", "PUSH $6A")]
    public void ReviewCaseTow_Completes_ReportsTime(string gate, string aircraftType, string command)
    {
        if (Fly(output, gate, aircraftType, command) is not { } run)
        {
            return;
        }

        output.WriteLine($"{gate} {command} ({aircraftType}) done at t={run.DoneSecond}s");
        Assert.True(run.DoneSecond > 0, $"{gate} {command} never completed in {BudgetSeconds}s: {Trace(run)}");
    }

    /// <summary>The E1 <c>PUSH $7</c> tow, flown once and shared by every test in the class.</summary>
    public sealed class Fixture
    {
        private readonly Lock _gate = new();
        private bool _flown;
        private PushRun? _run;

        internal PushRun? Get(ITestOutputHelper output)
        {
            lock (_gate)
            {
                if (!_flown)
                {
                    _run = Fly(output, Gate, AircraftType, $"PUSH ${Spot}");
                    _flown = true;
                }

                return _run;
            }
        }
    }

    /// <summary>
    /// One second of the tow: the nose, the main gear's speed, the nose-gear steer angle, the move running it, how far
    /// it is from that move's end and what it commands.
    /// </summary>
    internal readonly record struct Sample(
        int Second,
        double NoseDeg,
        double GroundSpeedKts,
        double SteerDeg,
        PushbackPhase? Phase,
        double ToEndFt,
        double CommandedKts
    );

    /// <summary>A finished tow: its moves in order, the per-second samples, the spot lane's heading and when it came to rest.</summary>
    internal sealed record PushRun(IReadOnlyList<PushbackPhase> Moves, List<Sample> Samples, double LaneDeg, int DoneSecond);

    private static PushRun? Fly(ITestOutputHelper output, string gate, string aircraftType, string command)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        double laneDeg = double.NaN;
        if (ground.Layout.FindSpotNodeByName(Spot) is { } spot && command == $"PUSH ${Spot}")
        {
            Assert.True(ground.Layout.TryGetSpotOutboundHeading(spot, out laneDeg), $"spot {Spot} has no outbound heading");
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL7", aircraftType, gate);
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, result.Message);
        List<PushbackPhase> moves = [.. ac.Phases!.Phases.OfType<PushbackPhase>()];
        output.WriteLine(
            $"{gate} {command}: {string.Join(", ", moves.Select(m => $"{m.Move.Kind} {m.Move.Shape}{(m.Move.Creep ? " creep" : "")}"))}"
        );

        var samples = new List<Sample>();
        int done = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase,
            BudgetSeconds,
            second =>
            {
                var phase = ac.Phases?.CurrentPhase as PushbackPhase;
                double toEndFt = phase is null ? 0.0 : GeoMath.DistanceNm(ac.Position, phase.PlannedEnd) * GeoMath.FeetPerNm;
                double steerDeg = ac.Ground.TowbarTrueHeading is { } towbar ? towbar.AbsAngleTo(ac.TrueHeading) : 0.0;
                samples.Add(
                    new Sample(second, ac.TrueHeading.Degrees, ac.GroundSpeed, steerDeg, phase, toEndFt, phase?.CommandedSpeedKts(ac) ?? 0.0)
                );
            }
        );
        return new PushRun(moves, samples, laneDeg, done);
    }

    /// <summary>The tug's own speed in a sample: the main gear's over the cosine of the nose-gear steer angle, knots.</summary>
    private static double TugKts(Sample s) => s.GroundSpeedKts / Math.Cos(s.SteerDeg * Math.PI / 180.0);

    private static double Signed(double deg, double fromDeg) => new TrueHeading(fromDeg).SignedAngleTo(new TrueHeading(deg));

    private static string Trace(PushRun run) => string.Join(" ", run.Samples.Select(s => $"{s.Second}:{s.GroundSpeedKts:F1}@{s.NoseDeg:F0}"));
}
