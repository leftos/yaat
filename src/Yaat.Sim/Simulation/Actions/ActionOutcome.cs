using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// What routing one action produced: the result the issuer sees, the record the run appended to its action log
/// (null when the action was applied <em>from</em> a record, was a compound whose units recorded themselves, or has
/// <see cref="RecordingPolicy.Never"/>), and the trace of the arm it took.
/// </summary>
public sealed record ActionOutcome(CommandResult Result, RecordedCommand? ToRecord, ActionTrace Trace);

/// <summary>
/// Which arm an action took: the kind the classifier gave it, the scope the router resolved, and whether the body
/// ran on the host rather than in the Sim. The parity test's observable — every entry point routing the same bytes
/// must produce the same trace.
/// </summary>
public readonly record struct ActionTrace(RecordedCommandKind Kind, ActionScope Scope, bool IsHostSlot);
