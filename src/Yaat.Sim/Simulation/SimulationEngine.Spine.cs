using System.Collections.Immutable;
using System.Diagnostics;
using Yaat.Sim.Simulation.Spine;

namespace Yaat.Sim.Simulation;

// The segment entry points every run kind advances a sim-second through, and the runner that iterates the spine.
public sealed partial class SimulationEngine
{
    private static readonly string[] StepNames = Enum.GetNames<StepId>();

    private readonly BareHost _bareHost;

    /// <summary>The host of a bare (<see cref="RunKind.Test"/>) run of this engine; the replay host delegates to it.</summary>
    internal BareHost BareHost => _bareHost;

    /// <summary>Which spine steps ran this second, in order. See <see cref="Spine.StepTrace"/>.</summary>
    public StepTrace StepTrace { get; } = new();

    /// <summary>
    /// One whole sim-second: <see cref="BeginSecond"/>, <see cref="OpenSecond"/>, <see cref="RunPrePhysics"/>,
    /// <see cref="PhysicsSubTickRate"/> × <see cref="RunPhysicsSubTick"/>, <see cref="RunPostPhysics"/>,
    /// <see cref="RunEndOfSecond"/>. The live room, the bare test engine, the whole-second replay step and
    /// reconstruction all call this; only the sub-tick replay step composes the segments itself.
    /// </summary>
    public void RunSecond(ISimulationHost host)
    {
        BeginSecond();
        OpenSecond(host);
        RunPrePhysics(host);

        double subDelta = 1.0 / PhysicsSubTickRate;
        for (int sub = 0; sub < PhysicsSubTickRate; sub++)
        {
            RunPhysicsSubTick(subDelta, sub);
        }

        RunPostPhysics(host);
        RunEndOfSecond(host);
    }

    /// <summary>Advances the clock to the second about to be simulated. Only this — the sub-tick replay step keeps its own clock.</summary>
    public void BeginSecond()
    {
        RequireScenario().ElapsedSeconds += 1;
    }

    /// <summary>
    /// Opens the second the clock now points at: resets the trace for it and applies the host's pre-tick recorded
    /// actions, so a recorded spawn flies its first second and a live-traffic sample resyncs against the coming
    /// second (#404). The second is the ceiling of the clock, so the sub-tick step — which opens at a quarter past
    /// the previous integer — and the whole-second step agree on which second is opening.
    /// </summary>
    public void OpenSecond(ISimulationHost host)
    {
        int second = (int)Math.Ceiling(RequireScenario().ElapsedSeconds);
        StepTrace.OpenSecond(second);

        StepTrace.Record(StepId.PreTickRecordedActions, 0);
        long start = TimingStart();
        host.ApplyPreTickRecordedActions(second);
        TimingStop(StepNames[(int)StepId.PreTickRecordedActions], start);
    }

    public void RunPrePhysics(ISimulationHost host) => RunSegment(SpineOrder.PrePhysics, host, "PrePhysics");

    /// <summary>One physics sub-tick, traced with its index within the second (0-based).</summary>
    public void RunPhysicsSubTick(double delta, int subTick)
    {
        StepTrace.Record(StepId.Physics, subTick);
        long start = TimingStart();
        TickPhysics(delta);
        TimingStop("Physics", start);
    }

    public void RunPostPhysics(ISimulationHost host) => RunSegment(SpineOrder.PostPhysics, host, "PostPhysics");

    /// <summary>The end-of-second steps, then <see cref="TickCompleted"/>.</summary>
    public void RunEndOfSecond(ISimulationHost host)
    {
        RunSegment(SpineOrder.EndOfSecond, host, "EndOfSecond");
        FireTickCompleted((int)RequireScenario().ElapsedSeconds);
    }

    private void RunSegment(ImmutableArray<SpineStep> steps, ISimulationHost host, string rollup)
    {
        long segmentStart = TimingStart();
        foreach (var step in steps)
        {
            StepTrace.Record(step.Id, 0);
            long start = TimingStart();
            step.Run(this, host);
            TimingStop(StepNames[(int)step.Id], start);
        }

        TimingStop(rollup, segmentStart);
    }

    private SimScenarioState RequireScenario() =>
        Scenario ?? throw new InvalidOperationException("Advancing a sim-second requires a loaded scenario");

    /// <summary>
    /// Pilot transmissions are addressed to whoever answers pilots — the solo student or an AI position. With nobody
    /// answering (an instructor room with the AI off) they are discarded, the pre-roster instructor-mode behaviour,
    /// and the host is not called.
    /// </summary>
    internal void DrainPilotTransmissionsInto(IHostConsumers host)
    {
        if (Scenario is { } scenario && scenario.PilotContacts.AnyAnswering)
        {
            host.OnPilotTransmissions(World.DrainReadyPilotTransmissions(scenario.ElapsedSeconds));
        }
        else
        {
            World.DiscardAllPilotTransmissions();
        }
    }

    // --- Timing ---

    private long TimingStart() => TickTimings is null ? 0 : Stopwatch.GetTimestamp();

    private void TimingStop(string bucket, long start)
    {
        if (TickTimings is null)
        {
            return;
        }

        RecordWorldTiming(bucket, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
