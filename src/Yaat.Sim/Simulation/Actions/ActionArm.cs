using System.Collections.Frozen;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>Whether a routed command's text is appended to the action log.</summary>
public enum RecordingPolicy
{
    /// <summary>Recorded as issued, accepted or not.</summary>
    Text,

    /// <summary>
    /// Never recorded: transport verbs (the room's clock), bookmarks (timeline metadata the rewind paths carry over
    /// verbatim) and the <c>SHOW</c> query (read-only).
    /// </summary>
    Never,
}

/// <summary>
/// Everything an arm body sees: the engine and host of the run, the action, the text after any <c>AS</c> prefix, the
/// parsed command (null for a multi-verb chain), the aircraft the router resolved for an aircraft-scoped arm and the
/// identity it resolved for the issuing connection. An arm that draws (a reaction delay, a spawn jitter, a generated
/// aircraft, a wall clock) writes the draw back here so the router bakes it into the record.
/// </summary>
public sealed class ArmContext
{
    public required SimulationEngine Engine { get; init; }
    public required IActionHost Host { get; init; }
    public required ActionInput Input { get; init; }
    public required string Remainder { get; init; }
    public required string? AsOverrideTcp { get; init; }
    public required ParsedCommand? Parsed { get; init; }
    public AircraftState? Aircraft { get; init; }
    public TrackOwner? Identity { get; init; }

    public DispatchOrigin Origin => AiConnectionId.OriginOf(Input.ConnectionId);

    /// <summary>True when the action comes from a recording — its draws are in <see cref="ActionInput.Baked"/>.</summary>
    public bool IsRecorded => Input.Baked is not null;

    public double? ReactionDelaySeconds { get; set; }
    public int? SpawnJitterSeconds { get; set; }
    public AircraftSnapshotDto? SpawnedAircraft { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
}

/// <summary>
/// One row of the arm table: the body that applies a <see cref="RecordedCommandKind"/>, what it is addressed to,
/// whether the body is the host's (a slot on <see cref="IActionHost"/>) or the Sim's, and whether its text is recorded.
/// </summary>
public sealed record ActionArm
{
    public required RecordedCommandKind Kind { get; init; }
    public required ActionScope Scope { get; init; }
    public required bool IsHostSlot { get; init; }
    public required RecordingPolicy Recording { get; init; }
    public required Func<ArmContext, CommandResult> Run { get; init; }
}

/// <summary>
/// The one table mapping every <see cref="RecordedCommandKind"/> to the arm that applies it. Live, replay and
/// reconstruction route the same bytes through the same row, so they cannot take different arms. A kind without a row
/// throws at lookup, and <c>ActionRoutingCompletenessTests</c> looks every kind up.
/// </summary>
public static class ArmTable
{
    private static readonly FrozenDictionary<RecordedCommandKind, ActionArm> Rows = Build();

    public static ActionArm For(RecordedCommandKind kind) =>
        Rows.TryGetValue(kind, out var arm) ? arm : throw new InvalidOperationException($"{kind} has no ActionArm — add a row to ArmTable");

    public static IEnumerable<ActionArm> All => Rows.Values;

    private static FrozenDictionary<RecordedCommandKind, ActionArm> Build()
    {
        var rows = new List<ActionArm>
        {
            Sim(RecordedCommandKind.Compound, ActionArms.Aviation),
            Sim(RecordedCommandKind.Say, ActionArms.Aviation),
            Sim(RecordedCommandKind.ShowQueued, RecordingPolicy.Never, ActionArms.ShowQueued),
            Sim(RecordedCommandKind.FlightPlan, ActionArms.FlightPlan),
            Sim(RecordedCommandKind.Delete, ActionArms.Delete),
            Sim(RecordedCommandKind.DeleteQueued, ActionArms.DeleteQueued),
            Sim(RecordedCommandKind.Note, ActionArms.Note),
            Sim(RecordedCommandKind.SpawnNow, ActionArms.SpawnNow),
            Sim(RecordedCommandKind.SpawnDelay, ActionArms.SpawnDelay),
            Sim(RecordedCommandKind.SetActivePosition, ActionArms.SetActivePosition),
            Sim(RecordedCommandKind.TrackOwnership, ActionArms.Track),
            Sim(RecordedCommandKind.AcceptAllHandoffs, ActionArms.GlobalTrack),
            Sim(RecordedCommandKind.InitiateHandoffAll, ActionArms.GlobalTrack),
            Sim(RecordedCommandKind.GhostTrack, ActionArms.GhostTrack),
            Sim(RecordedCommandKind.Reposition, ActionArms.Reposition),
            Sim(RecordedCommandKind.SquawkAll, ActionArms.SquawkAll),
            Sim(RecordedCommandKind.TaxiAll, ActionArms.TaxiAll),
            Sim(RecordedCommandKind.HoldForRelease, ActionArms.HoldForRelease),
            Sim(RecordedCommandKind.DisarmHoldForRelease, ActionArms.DisarmHoldForRelease),
            Sim(RecordedCommandKind.ReleaseDeparture, ActionArms.ReleaseDeparture),
            Sim(RecordedCommandKind.Cfr, ActionArms.Cfr),
            Sim(RecordedCommandKind.Timer, ActionArms.Timer),
            Sim(RecordedCommandKind.Consolidate, ActionArms.Consolidate),
            Sim(RecordedCommandKind.Deconsolidate, ActionArms.Deconsolidate),
            Sim(RecordedCommandKind.AddAircraft, ActionArms.AddAircraft),
            Host(RecordedCommandKind.Strip, RecordingPolicy.Text, static ctx => ctx.Host.ApplyStrip(ctx.Input.Callsign, ctx.Parsed!, ctx.Identity)),
            Host(RecordedCommandKind.Tdls, RecordingPolicy.Text, static ctx => ctx.Host.ApplyTdls(ctx.Aircraft!, ctx.Parsed!)),
            Host(RecordedCommandKind.TdlsOps, RecordingPolicy.Text, static ctx => ctx.Host.ApplyTdlsOpsConfig((TdlsOpsConfigCommand)ctx.Parsed!)),
            Host(
                RecordedCommandKind.Coordination,
                RecordingPolicy.Text,
                static ctx => ctx.Host.ApplyCoordination(ctx.Aircraft!, ctx.Parsed!, ctx.Identity)
            ),
            Host(
                RecordedCommandKind.GlobalCoordination,
                RecordingPolicy.Text,
                static ctx => ctx.Host.ApplyGlobalCoordination((CoordinationAutoAckCommand)ctx.Parsed!, ctx.Identity)
            ),
            Host(RecordedCommandKind.AsdexEnableAllAlerts, RecordingPolicy.Text, static ctx => ctx.Host.ApplyAsdexEnableAllAlerts()),
            Host(
                RecordedCommandKind.Bookmark,
                RecordingPolicy.Never,
                static ctx => ctx.Host.ApplyBookmark((BookmarkCommand)ctx.Parsed!, ctx.Input.Initials)
            ),
            Host(RecordedCommandKind.Transport, RecordingPolicy.Never, static ctx => ctx.Host.ApplyTransport(ctx.Parsed!)),
        };

        foreach (var row in rows)
        {
            var expected = RecordedCommandClassifier.ScopeOf(row.Kind);
            if (row.Scope != expected)
            {
                throw new InvalidOperationException($"ArmTable row {row.Kind} declares scope {row.Scope}; the classifier says {expected}");
            }
        }

        return rows.ToFrozenDictionary(r => r.Kind);
    }

    private static ActionArm Sim(RecordedCommandKind kind, Func<ArmContext, CommandResult> run) => Sim(kind, RecordingPolicy.Text, run);

    private static ActionArm Sim(RecordedCommandKind kind, RecordingPolicy recording, Func<ArmContext, CommandResult> run) =>
        new()
        {
            Kind = kind,
            Scope = RecordedCommandClassifier.ScopeOf(kind),
            IsHostSlot = false,
            Recording = recording,
            Run = run,
        };

    private static ActionArm Host(RecordedCommandKind kind, RecordingPolicy recording, Func<ArmContext, CommandResult> run) =>
        new()
        {
            Kind = kind,
            Scope = RecordedCommandClassifier.ScopeOf(kind),
            IsHostSlot = true,
            Recording = recording,
            Run = run,
        };
}
