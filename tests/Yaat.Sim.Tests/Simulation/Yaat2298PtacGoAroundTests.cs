using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2298: on the owner's KFAT session N200WM (C421) was slowed to 96 kt (RFAS at t=1992), then cleared with
/// <c>PTAC 080 030 R11L</c> at 18.8 NM (t=2051), which releases the assigned speed. The intercept's speed anticipation then
/// raised it to 1.3 × FAS (124.8 kt), final approach held that until its FAS stage, and the landing's stabilization gate,
/// judged on the category's Vref (Piston 75, limit 97.5 kt) rather than the type's, sent it around at 0.04 NM flying the
/// 106 kt approach speed the sim itself had set. N111TF (C421, no PTAC) went around at t=3247 the same way.
/// </summary>
public sealed class Yaat2298PtacGoAroundTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/yaat2298-fat-ptac-capp-recording.yaat-bug-report-bundle.zip";
    private const string PtacCallsign = "N200WM";
    private const string NoPtacCallsign = "N111TF";
    private const double PtacRestoreSeconds = 2045.0;
    private const double PtacLandedBySeconds = 2960.0;
    private const double PtacRecordedDeleteSeconds = 2739.0;
    private const double PtacBeforeFasStageSeconds = 2300.0;
    private const double PtacHeldSpeedCeilingKts = 97.0;
    private const double VappToleranceKts = 2.0;
    private const double NoPtacRestoreSeconds = 3200.0;

    // The last second before the recording DELs N111TF at t=3255; seven seconds past the base's go-around at 3247.
    private const double NoPtacEndSeconds = 3254.0;

    /// <summary>
    /// N200WM flies the approach at its own speed and lands without the unstable-approach go-around. The recorded delete of
    /// N200WM (t=2739) is dropped from the replay: the fixed aircraft flies 96 kt in and lands after the recorded delete.
    /// </summary>
    [Fact]
    public void PtacAfterRfas_FlownAtItsOwnVapp_LandsWithoutAnUnstableGoAround()
    {
        if (Restore(PtacRestoreSeconds, dropDeleteOf: PtacCallsign) is not { } restored)
        {
            output.WriteLine($"Skipped: {RecordingPath} is not present");
            return;
        }

        using RecordingArchive archive = restored.Archive;
        // Past the recorded delete the replay runs on actions the live session never saw N200WM answer: none may target it.
        string lateCommands = string.Join(
            ", ",
            archive
                .ReadActions()
                .OfType<RecordedCommand>()
                .Where(c => (c.ElapsedSeconds >= PtacRecordedDeleteSeconds) && (c.Callsign == PtacCallsign) && !IsDeleteOf(c, PtacCallsign))
                .Select(c => $"t={c.ElapsedSeconds:F0} {c.Command}")
        );
        Assert.True(lateCommands.Length == 0, $"Recorded commands for {PtacCallsign} after its delete: {lateCommands}");

        double? goAroundAt = StepWatchingForGoAround(restored.Engine, PtacCallsign, restored.StartedSeconds, PtacLandedBySeconds);

        Assert.True(goAroundAt is null, $"{PtacCallsign} went around at t={goAroundAt:F0}");
        AircraftState aircraft = Find(restored.Engine, PtacCallsign);
        Assert.True(
            aircraft.IsOnGround,
            $"{PtacCallsign} is not on the ground at t={PtacLandedBySeconds:F0} (phase {aircraft.Phases?.CurrentPhase?.Name})"
        );
    }

    [Fact]
    public void PtacAfterRfas_DoesNotAccelerateTowardOnePointThreeFas()
    {
        if (Restore(PtacRestoreSeconds, dropDeleteOf: null) is not { } restored)
        {
            output.WriteLine($"Skipped: {RecordingPath} is not present");
            return;
        }

        using RecordingArchive archive = restored.Archive;
        double maxIas = 0;
        for (int second = 0; second < (int)(PtacBeforeFasStageSeconds - restored.StartedSeconds); second++)
        {
            restored.Engine.ReplayOneSecond();
            maxIas = Math.Max(maxIas, Find(restored.Engine, PtacCallsign).IndicatedAirspeed);
        }

        output.WriteLine($"{PtacCallsign} max IAS {maxIas:F1} kt from t={restored.StartedSeconds:F0} to {PtacBeforeFasStageSeconds:F0}");
        Assert.True(maxIas <= PtacHeldSpeedCeilingKts, $"{PtacCallsign} accelerated to {maxIas:F1} kt after PTAC released its 96 kt speed");
    }

    /// <summary>
    /// The slowed aircraft is raised to its Vapp by the final approach's FAS stage before the landing starts. The recorded
    /// delete of N200WM (t=2739) is dropped from the replay: the fixed aircraft flies 96 kt in and lands after the recorded delete.
    /// </summary>
    [Fact]
    public void PtacAfterRfas_ReachesItsVappBeforeLanding()
    {
        if (Restore(PtacRestoreSeconds, dropDeleteOf: PtacCallsign) is not { } restored)
        {
            output.WriteLine($"Skipped: {RecordingPath} is not present");
            return;
        }

        using RecordingArchive archive = restored.Archive;
        SimulationEngine engine = restored.Engine;
        double? iasAtLanding = null;
        double vapp = 0;
        for (int second = 0; (second < (int)(PtacLandedBySeconds - restored.StartedSeconds)) && (iasAtLanding is null); second++)
        {
            engine.ReplayOneSecond();
            AircraftState aircraft = Find(engine, PtacCallsign);
            if ((aircraft.Phases?.CurrentPhase is LandingPhase) && (aircraft.Phases.AssignedRunway is { } runway))
            {
                iasAtLanding = aircraft.IndicatedAirspeed;
                vapp = AircraftPerformance.FinalApproachSpeedKts(
                    aircraft.AircraftType,
                    AircraftCategorization.Categorize(aircraft.AircraftType),
                    engine.World.Weather,
                    runway.TrueHeading.Degrees
                );
            }
        }

        Assert.True(iasAtLanding is not null, $"{PtacCallsign} never started its landing by t={PtacLandedBySeconds:F0}");
        output.WriteLine($"{PtacCallsign} IAS {iasAtLanding:F1} kt at landing start, Vapp {vapp:F1} kt");
        Assert.InRange(iasAtLanding!.Value, vapp - VappToleranceKts, vapp + VappToleranceKts);
    }

    [Fact]
    public void N111TfWithoutPtac_LandsWithoutAnUnstableGoAround()
    {
        if (Restore(NoPtacRestoreSeconds, dropDeleteOf: null) is not { } restored)
        {
            output.WriteLine($"Skipped: {RecordingPath} is not present");
            return;
        }

        using RecordingArchive archive = restored.Archive;
        output.WriteLine($"Restored at t={restored.StartedSeconds:F0}");
        double? goAroundAt = StepWatchingForGoAround(restored.Engine, NoPtacCallsign, restored.StartedSeconds, NoPtacEndSeconds);

        Assert.True(goAroundAt is null, $"{NoPtacCallsign} went around at t={goAroundAt:F0}");
        AircraftState aircraft = Find(restored.Engine, NoPtacCallsign);
        Assert.True(
            (aircraft.IsOnGround) || (aircraft.Phases?.CurrentPhase is LandingPhase),
            $"{NoPtacCallsign} is neither on the ground nor landing at t={NoPtacEndSeconds:F0} (phase {aircraft.Phases?.CurrentPhase?.Name})"
        );
    }

    private double? StepWatchingForGoAround(SimulationEngine engine, string callsign, double startedSeconds, double endSeconds)
    {
        for (int second = 0; second < (int)(endSeconds - startedSeconds); second++)
        {
            engine.ReplayOneSecond();
            AircraftState? aircraft = engine.World.GetSnapshot().FirstOrDefault(ac => ac.Callsign == callsign);
            if (aircraft is null)
            {
                output.WriteLine($"{callsign} left the world at t={startedSeconds + second + 1:F0}");
                return null;
            }

            if (aircraft.Phases?.CurrentPhase is GoAroundPhase)
            {
                output.WriteLine($"{callsign} in GoAroundPhase at IAS {aircraft.IndicatedAirspeed:F1} kt");
                return startedSeconds + second + 1;
            }
        }

        return null;
    }

    private static AircraftState Find(SimulationEngine engine, string callsign) =>
        engine.World.GetSnapshot().FirstOrDefault(ac => ac.Callsign == callsign)
        ?? throw new InvalidOperationException($"{callsign} is not in the replayed world");

    /// <summary>
    /// The latest snapshot at or before <paramref name="seconds"/> with the replay driver armed behind it, on today's KFAT
    /// layout (the committed fat.geojson): the approach and landing must hold on the current airport. With
    /// <paramref name="dropDeleteOf"/> set, that aircraft's one recorded delete is left out of the armed actions.
    /// </summary>
    private static (SimulationEngine Engine, double StartedSeconds, RecordingArchive Archive)? Restore(double seconds, string? dropDeleteOf)
    {
        TestVnasData.EnsureInitialized();
        if (RecordingLoader.OpenArchive(RecordingPath) is not { } archive)
        {
            return null;
        }

        try
        {
            if (archive.ReadSnapshotAt(seconds) is not { } snapshot)
            {
                Assert.Fail($"{RecordingPath} has no snapshot at or before t={seconds:F0}");
                return null;
            }

            var groundData = RecordingGroundData.Evergreen("a C421's approach and landing on KFAT 11L must hold on today's airport");
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            SessionRecording recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);
            engine.RestoreFromSnapshot(snapshot.State);
            engine.ArmReplay(dropDeleteOf is null ? recording.Actions : WithoutDeleteOf(recording.Actions, dropDeleteOf));
            return (engine, snapshot.ElapsedSeconds, archive);
        }
        catch
        {
            // The caller only owns the archive once it is returned; every failing path disposes it here.
            archive.Dispose();
            throw;
        }
    }

    private static List<RecordedAction> WithoutDeleteOf(List<RecordedAction> actions, string callsign)
    {
        List<RecordedAction> kept = [.. actions.Where(action => !IsDeleteOf(action, callsign))];
        Assert.True(actions.Count - kept.Count == 1, $"Expected exactly one recorded DEL of {callsign}, removed {actions.Count - kept.Count}");
        return kept;
    }

    private static bool IsDeleteOf(RecordedAction action, string callsign) =>
        (action is RecordedCommand command)
        && string.Equals(command.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
        && string.Equals(command.Command.Trim().Split(' ')[0], "DEL", StringComparison.OrdinalIgnoreCase);
}
