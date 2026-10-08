using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The push targets precomputed for one stand and one design group: the stand it pushes from, the design group it serves
/// (the targets were planned with that group's envelope), and the targets the planner accepted.
/// </summary>
/// <param name="StandName">The stand's name.</param>
/// <param name="DesignGroup">The design group, roman (<c>I</c> to <c>VI</c>).</param>
/// <param name="Targets">The accepted targets, by kind, then path length, then name; empty for a taxi-out stand.</param>
public sealed record PushTargetEntry(string StandName, string DesignGroup, IReadOnlyList<PrecomputedPushTarget> Targets);

/// <summary>What a precomputed push target is, which orders the targets and picks the command's form.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PushTargetKind>))]
public enum PushTargetKind
{
    /// <summary>A taxiway outside the movement area, sent as a bare <c>PUSH &lt;twy&gt;</c>.</summary>
    Taxilane,

    /// <summary>A movement-area taxiway, sent as a bare <c>PUSH &lt;twy&gt;</c>.</summary>
    Taxiway,

    /// <summary>A ramp spot, sent as <c>PUSH $spot</c>.</summary>
    Spot,
}

/// <summary>One push target the planner accepted from a stand for a design group, with the plan it made.</summary>
public sealed record PrecomputedPushTarget
{
    /// <summary>What the target is.</summary>
    public required PushTargetKind Kind { get; init; }

    /// <summary>The taxiway's or spot's name, as the layout names it.</summary>
    public required string Name { get; init; }

    /// <summary>How a taxiway push meets the taxiway, <c>alongside</c> or <c>across</c>; empty for a spot.</summary>
    public required string Note { get; init; }

    /// <summary>The command that makes the push: <c>PUSH {twy}</c> or <c>PUSH ${spot}</c>.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// For a taxiway target, the two true bearings along the taxiway at the exit node the push is planned through, ascending,
    /// rounded to 0.1°; empty for a spot or when no edge of the taxiway meets that node.
    /// </summary>
    public required IReadOnlyList<double> Facings { get; init; }

    /// <summary>The sum of the plan's move path lengths, feet, rounded to 0.1 ft.</summary>
    public required double PathLengthFt { get; init; }

    /// <summary>The plan's moves, in order.</summary>
    public required IReadOnlyList<PushMoveEntry> Moves { get; init; }
}
