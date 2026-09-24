using System.Globalization;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A tug move to a marked point, typed and dispatched through the engine on the real SFO layout (issue #462): from gate
/// D15, <c>PUSHM $6A ~lat/lon/facing</c> with the point on spot 6B's stop and facing 6B's nose-out heading — the ramp
/// point <c>ForcedPushLegPlannerTests</c> plans onto from 6A. The tow installs, flies, and holds on the point.
/// </summary>
public class MarkedPointPushTests(ITestOutputHelper output)
{
    private const string Narrowbody = "B738";
    private const string Callsign = "UAL462";
    private const string Stand = "D15";

    /// <summary>The planner's end tolerance on a faced goal, feet and degrees.</summary>
    private const double EndToleranceFt = 3.0;

    private const double EndFacingToleranceDeg = 2.0;

    /// <summary>How close a restored run must end to the uninterrupted one, feet and degrees.</summary>
    private const double SamePoseFt = 0.01;

    private const double SamePoseDeg = 0.01;

    /// <summary>The tick budget for the whole tow, seconds.</summary>
    private const int MaxTowSeconds = 900;

    [Fact]
    public void PushmToAMarkedPoint_FromAStandThatIsNotALeg_InstallsTheTowAndHoldsOnThePoint()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        (string command, TugPose point) = Command(ground.Layout);
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Callsign, Narrowbody, Stand);

        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        output.WriteLine($"{command} → {result.Success}: {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.IsType<PushbackPhase>(aircraft.Phases!.CurrentPhase);
        Assert.All(aircraft.Phases.Phases.Take(aircraft.Phases.Phases.Count - 1), p => Assert.IsType<PushbackPhase>(p));
        Assert.IsType<HoldingAfterPushbackPhase>(aircraft.Phases.Phases[^1]);
        Assert.Null(aircraft.Ground.ParkingSpot);

        Assert.True(FlyToRest(ground.Engine) > 0, "the tow never came to rest");
        AssertRestsOn(point, aircraft);
    }

    /// <summary>
    /// A snapshot taken part-way through the marked-point tow, carried through the recording's JSON and restored into a
    /// fresh engine, flies on to the same pose as the tow left uninterrupted.
    /// </summary>
    [Fact]
    public void PushmToAMarkedPoint_SnapshotMidTow_RestoresAndEndsWhereTheUninterruptedTowDoes()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        (string command, TugPose point) = Command(ground.Layout);
        AircraftState uninterrupted = SfoGroundHarness.SpawnParked(ground, Callsign, Narrowbody, Stand);
        Assert.True(ground.Engine.SendCommand(Callsign, command).Success);
        int towSeconds = FlyToRest(ground.Engine);
        Assert.True(towSeconds > 0, "the uninterrupted tow never came to rest");
        AssertRestsOn(point, uninterrupted);

        SfoGround interrupted =
            SfoGroundHarness.Build(output, autoCross: false) ?? throw new InvalidOperationException("SFO loaded once and not again");
        AircraftState before = SfoGroundHarness.SpawnParked(interrupted, Callsign, Narrowbody, Stand);
        Assert.True(interrupted.Engine.SendCommand(Callsign, command).Success);
        for (int second = 0; second < towSeconds / 2; second++)
        {
            interrupted.Engine.TickOneSecond();
        }

        Assert.IsType<PushbackPhase>(before.Phases!.CurrentPhase);
        string json = JsonSerializer.Serialize(interrupted.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SfoGround restoredGround =
            SfoGroundHarness.Build(output, autoCross: false) ?? throw new InvalidOperationException("SFO loaded once and not again");
        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = restoredGround.Engine.FindAircraft(Callsign) ?? throw new InvalidOperationException("the restore lost the aircraft");
        PushbackPhase restoredMove = Assert.IsType<PushbackPhase>(restored.Phases!.CurrentPhase);
        Assert.True(restoredMove.KeepsItsPlan, "the restored move forgot that a tow to a marked point keeps its plan");
        Assert.True(FlyToRest(restoredGround.Engine) > 0, "the restored tow never came to rest");

        double offFt = GeoMath.DistanceNm(restored.Position, uninterrupted.Position) * GeoMath.FeetPerNm;
        double offDeg = restored.TrueHeading.AbsAngleTo(uninterrupted.TrueHeading);
        output.WriteLine(
            $"snapshot at {towSeconds / 2} s of {towSeconds} s; restored run ends {offFt:F4} ft and {offDeg:F4}° off the uninterrupted one"
        );
        Assert.IsType<HoldingAfterPushbackPhase>(restored.Phases.CurrentPhase);
        Assert.True(offFt <= SamePoseFt, $"the restored tow ended {offFt:F4} ft off the uninterrupted one");
        Assert.True(offDeg <= SamePoseDeg, $"the restored tow ended {offDeg:F4}° off the uninterrupted one");
    }

    /// <summary>
    /// A <c>PUSH FACE</c> while a tow to a marked point, or a forced push, is under way is refused: the tow keeps its
    /// plan, and the controller issues a new <c>PUSH</c> to change it.
    /// </summary>
    [Fact]
    public void FaceAmendment_DuringATowToAMarkedPoint_RefusedAsKeepingItsPlan()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        (string command, TugPose _) = Command(ground.Layout);
        SfoGroundHarness.SpawnParked(ground, Callsign, Narrowbody, Stand);
        Assert.True(ground.Engine.SendCommand(Callsign, command).Success);

        CommandResult amended = ground.Engine.SendCommand(Callsign, "PUSH FACE N");

        Assert.False(amended.Success, amended.Message);
        Assert.Equal(KeepsItsPlanRefusal, amended.Message);
    }

    /// <summary>The same refusal for a forced push: F8 to spot 7A forced <c>/PULL</c>, amended with a facing mid-push.</summary>
    [Fact]
    public void FaceAmendment_DuringAForcedPush_RefusedAsKeepingItsPlan()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, Callsign, Narrowbody, "F8");
        CommandResult pushed = ground.Engine.SendCommand(Callsign, "PUSH $7A/PULL");
        Assert.True(pushed.Success, pushed.Message);

        CommandResult amended = ground.Engine.SendCommand(Callsign, "PUSH FACE E");

        Assert.False(amended.Success, amended.Message);
        Assert.Equal(KeepsItsPlanRefusal, amended.Message);
    }

    private const string KeepsItsPlanRefusal = "Unable, a forced push or a push to a marked point keeps its plan — issue a new PUSH to change it";

    /// <summary>The typed command, <c>PUSHM $6A ~lat/lon/facing</c>, and the pose it names: 6B's stop, nose-out, degrees true.</summary>
    private static (string Command, TugPose Point) Command(AirportGroundLayout layout)
    {
        GroundNode sixB = layout.FindSpotNodeByName("6B") ?? throw new InvalidOperationException("SFO spot 6B missing");
        Assert.True(layout.TryGetSpotOutboundHeading(sixB, out double outbound));
        LatLon stop = TugMovePlanner.SpotStopGeometry(sixB, outbound, Narrowbody).Stop;
        var facing = new MagneticHeading(MagneticDeclination.TrueToMagnetic(outbound, stop));
        string command = string.Create(CultureInfo.InvariantCulture, $"PUSHM $6A ~{stop.Lat:F6}/{stop.Lon:F6}/{facing.ToDisplayString()}");
        return (command, new TugPose(stop, outbound));
    }

    /// <summary>Ticks until the aircraft holds after the tow, stationary; the seconds it took, or -1 past the budget.</summary>
    private static int FlyToRest(SimulationEngine engine) =>
        SfoGroundHarness.TickUntil(
            engine,
            () =>
                engine.FindAircraft(Callsign) is { Phases.CurrentPhase: HoldingAfterPushbackPhase } a
                && (a.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            MaxTowSeconds,
            null
        );

    private void AssertRestsOn(TugPose point, AircraftState aircraft)
    {
        double offFt = GeoMath.DistanceNm(aircraft.Position, point.Position) * GeoMath.FeetPerNm;
        double offDeg = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(point.NoseTrueDeg));
        output.WriteLine($"rests {offFt:F2} ft off the marked point, nose {offDeg:F2}° off its facing");
        Assert.True(offFt <= EndToleranceFt, $"rests {offFt:F2} ft off the marked point");
        Assert.True(offDeg <= EndFacingToleranceDeg, $"rests with the nose {offDeg:F2}° off the marked point's facing");
    }
}
