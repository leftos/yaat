namespace Yaat.Sim.Simulation;

/// <summary>The kinds of run a <see cref="SimulationEngine"/> can be driven as. See <see cref="RunProfile"/>.</summary>
public enum RunKind
{
    /// <summary>A server room ticked in wall-clock time with controllers attached.</summary>
    Live,

    /// <summary>A recorded session applied back onto the engine — the action log is the input, not an output.</summary>
    Replay,

    /// <summary>A bare engine stepped by a test through <see cref="SimulationEngine.TickOneSecond"/>. The default.</summary>
    Test,

    /// <summary>A headless room driven at maximum speed by the soak runner.</summary>
    Soak,
}

/// <summary>
/// What kind of run this is and, consequently, what may legitimately differ from any other run. Every difference
/// between run kinds is a named member here, so "how does replay differ from live?" is answered by this type rather
/// than by grepping for a mode flag. Steps read the allowances, never <see cref="Kind"/> — the kind exists for hosts
/// to declare and for tests to assert.
///
/// <para>
/// This is host state, not simulation state: it is never captured into or restored from a snapshot. The host that
/// drives the engine sets it (the server at engine creation and at its playback transitions, the replay driver for
/// the duration of a replay step), and it is not part of what the tick oracle compares.
/// </para>
/// </summary>
public sealed record RunProfile(RunKind Kind)
{
    public static readonly RunProfile Live = new(RunKind.Live);
    public static readonly RunProfile Replay = new(RunKind.Replay);
    public static readonly RunProfile Test = new(RunKind.Test);
    public static readonly RunProfile Soak = new(RunKind.Soak);

    public static RunProfile For(RunKind kind) =>
        kind switch
        {
            RunKind.Live => Live,
            RunKind.Replay => Replay,
            RunKind.Test => Test,
            RunKind.Soak => Soak,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown run kind"),
        };

    /// <summary>
    /// The action log is an output of this run, so engine-originated actions (generated spawns, live-traffic samples,
    /// AI commands) are appended to it. In a replay the log is the input — appending would re-record what is being
    /// applied.
    /// </summary>
    public bool RecordsActions => Kind != RunKind.Replay;

    /// <summary>
    /// The runtime generators may spawn traffic. In a replay the recorded spawns are the traffic, so the generators
    /// stand down rather than produce a second set.
    /// </summary>
    public bool RunsGenerators => Kind != RunKind.Replay;

    /// <summary>
    /// The controller-AI brains may tick. In a replay their commands are already in the log as recorded actions, and
    /// running the brains again would double them.
    /// </summary>
    public bool RunsControllerAi => Kind != RunKind.Replay;
}
