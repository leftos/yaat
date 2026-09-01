namespace Yaat.Sim.ControllerAi;

/// <summary>
/// Runs the controller AI once per sim-second, after physics: refreshes staffing (and publishes it to the scenario so
/// pilots know whom to call), turns last tick's command outcomes into <see cref="AiAnomalyKind.CommandRejected"/>
/// records, builds the world view, and ticks each active brain in (role rank, position id) order. Owns the AI's RNG
/// stream so AI variability never perturbs the pilot or physics streams; the stream is re-seeded on <see cref="Reset"/>
/// rather than snapshotted (a rewind is not bit-identical for the AI, by design).
/// </summary>
public sealed class AiControllerService
{
    public AiControllerService(IReadOnlyList<IPositionBrain> brains, IAiStaffing staffing, IAiCommandSink sink, ControllerAiConfig config)
    {
        Brains = brains.OrderBy(b => ControlRoles.Rank(b.Position.Role)).ThenBy(b => b.Position.PositionId, StringComparer.Ordinal).ToList();
        Staffing = staffing;
        Sink = sink;
        Config = config;
        AiRng = new SerializableRandom(config.Seed);
    }

    public IReadOnlyList<IPositionBrain> Brains { get; }

    public IAiStaffing Staffing { get; }

    public IAiCommandSink Sink { get; }

    public ControllerAiConfig Config { get; }

    public SerializableRandom AiRng { get; private set; }

    public int TickCount { get; private set; }

    public void Tick(AiTickInputs inputs)
    {
        Staffing.Refresh();
        var active = Staffing.ActivePositions;
        var scenario = inputs.Scenario;
        scenario.SetAiStaffedPositions(active);

        double now = scenario.ElapsedSeconds;
        var outcomes = Sink.DrainOutcomes();
        foreach (var outcome in outcomes)
        {
            if (!outcome.Success)
            {
                scenario.AiAnomalies.Record(
                    AiAnomalyKind.CommandRejected,
                    outcome.Request.From.PositionId,
                    outcome.Request.Callsign,
                    now,
                    $"{outcome.Request.Canonical}: {outcome.Reason}"
                );
            }
        }

        var view = AiWorldView.Build(inputs.Aircraft, active, inputs.LayoutFor, inputs.RunwaysFor, Staffing.IsHumanHeld, Staffing.IsAssignedToHuman);
        var context = new AiTickContext
        {
            Snapshot = view.Snapshot,
            View = view,
            Scenario = scenario,
            World = inputs.World,
            Sink = Sink,
            Staffing = Staffing,
            AiRng = AiRng,
            ElapsedSeconds = now,
            Anomalies = scenario.AiAnomalies,
            Outcomes = outcomes,
            Weather = inputs.World.Weather,
            ActiveConflicts = inputs.ActiveConflicts,
            EramConflicts = inputs.EramConflicts,
            AutoAcceptDelaySeconds = scenario.AutoAcceptDelay.TotalSeconds,
        };

        foreach (var brain in Brains)
        {
            if (active.Any(p => p.PositionId == brain.Position.PositionId))
            {
                brain.Tick(context);
            }
        }

        TickCount++;
    }

    public void Reset()
    {
        AiRng = new SerializableRandom(Config.Seed);
        foreach (var brain in Brains)
        {
            brain.Reset();
        }
    }
}
