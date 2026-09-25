using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The pushes SFO stands D11, E13T, E13K, F10, C10 and C11 normally fly: back onto taxiway A. A runs across the push
/// behind each of them, so a B738's bare <c>PUSH A</c> takes the documented Across rule (docs/ground/pushback.md,
/// <c>StraightBackTo</c>): accepted, one straight push that completes with the aircraft's centre on A and the nose on
/// the stand heading, across A. <c>PUSHF A</c> installs the same moves: with nothing about, forcing changes no plan. The
/// tick playback of each plain push is recorded under <c>.tmp/pushf/</c> for rendering.
/// </summary>
public class SfoPushOntoATests(ITestOutputHelper output)
{
    private const string Pusher = "UAL1";
    private const string Narrowbody = "B738";

    /// <summary>A generous budget for any tow here to finish, seconds.</summary>
    private const int TowBudgetSeconds = 600;

    [Theory]
    [InlineData("D11")]
    [InlineData("E13T")]
    [InlineData("E13K")]
    [InlineData("F10")]
    [InlineData("C10")]
    [InlineData("C11")]
    public void PushA_EndsOnAOnTheStandHeading_AndPushfAFliesTheSamePlan(string gate)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, gate);
        string path = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "pushf", $"sfo-{gate}-push-a.json");
        List<TugMove> plainMoves;
        int second;
        using (TickRecorder.Attach(ground.Engine, path, Pusher))
        {
            CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSH A");
            plainMoves = QueuedMoves(pusher);
            output.WriteLine($"{gate} PUSH A: {plain.Success} \"{plain.Message}\"; moves {Describe(plainMoves)}");
            Assert.True(plain.Success, $"PUSH A off {gate} was refused: {plain.Message}");
            second = SfoGroundHarness.TickUntil(ground.Engine, () => pusher.Phases?.CurrentPhase is not PushbackPhase, TowBudgetSeconds, null);
        }

        output.WriteLine($"{gate}: tow done at t={second}s");
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
        Assert.Equal("Push Straight", Describe(plainMoves));
        AssertOnAOnTheStandHeading(ground.Layout, gate, pusher);

        List<TugMove> forcedMoves = ForcedMoves(gate);
        Assert.Equal(Describe(plainMoves), Describe(forcedMoves));
        Assert.Equal(plainMoves, forcedMoves);
    }

    /// <summary>
    /// The aircraft stopped on A's edge through the gate's A exit, the reference point within 3 ft of that line (the
    /// planner's end tolerance), with the nose still on the stand heading (within 2°): a straight push never turns it.
    /// </summary>
    private void AssertOnAOnTheStandHeading(AirportGroundLayout layout, string gate, AircraftState ac)
    {
        GroundNode stand = layout.FindParkingByName(gate) ?? throw new InvalidOperationException($"SFO gate {gate} missing");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "A") ?? throw new InvalidOperationException($"no A exit off {gate}");
        double lineDeg = layout.GetEdgeBearingForTaxiway(exit, "A", ac.TrueHeading.Degrees) ?? throw new InvalidOperationException("no A edge");
        double fromExitFt = GeoMath.DistanceNm(exit.Position, ac.Position) * GeoMath.FeetPerNm;
        double offFt = Math.Abs(fromExitFt * Math.Sin((GeoMath.BearingTo(exit.Position, ac.Position) - lineDeg) * Math.PI / 180.0));
        double standDeg = Assert.NotNull(stand.TrueHeading).Degrees;
        string nose = $"nose {ac.TrueHeading.Degrees:F1}° against A's {lineDeg:F1}° and the stand's {standDeg:F1}°";
        output.WriteLine($"{gate}: ended {offFt:F1} ft off A (exit {exit.Id}), {nose}");
        Assert.True(offFt <= 3.0, $"{gate}: ended {offFt:F1} ft off A's centreline");
        Assert.True(
            GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, standDeg) < 2.0,
            $"{gate}: ended on {ac.TrueHeading.Degrees:F1}°, off the stand heading {standDeg:F1}°"
        );
    }

    /// <summary>The tug moves <c>PUSHF A</c> installs off <paramref name="gate"/> in a fresh world.</summary>
    private List<TugMove> ForcedMoves(string gate)
    {
        SfoGround ground = SfoGroundHarness.Build(output, autoCross: false)!.Value;
        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, gate);
        CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF A");
        List<TugMove> moves = QueuedMoves(pusher);
        output.WriteLine($"{gate} PUSHF A: {forced.Success} \"{forced.Message}\"; moves {Describe(moves)}");
        Assert.True(forced.Success, $"PUSHF A off {gate} was refused: {forced.Message}");
        return moves;
    }

    private static List<TugMove> QueuedMoves(AircraftState aircraft) => [.. aircraft.Phases!.Phases.OfType<PushbackPhase>().Select(p => p.Move)];

    private static string Describe(IReadOnlyList<TugMove> moves) => string.Join(", ", moves.Select(m => $"{m.Kind} {m.Shape}"));
}
