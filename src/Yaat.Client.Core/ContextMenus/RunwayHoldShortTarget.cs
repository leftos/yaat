using Yaat.Client.Services;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One hold-short node Taxi to runway offers for a runway end: the taxiway it sits on and why it is offered, read together
/// as <see cref="Label"/> (<c>At W (nearest, full length)</c>).
/// </summary>
/// <param name="Node">The hold-short node the taxi choices route to.</param>
/// <param name="Name">
/// What the target names: the taxiway the hold short sits on (<c>At W</c>), or its reasons, capitalised, when it sits on no
/// named taxiway (<c>Nearest, full length</c>).
/// </param>
/// <param name="Reasons">
/// Why a target on a named taxiway is offered, in order (<c>nearest</c>, <c>full length</c>); empty when
/// <paramref name="Name"/> holds them.
/// </param>
public sealed record RunwayHoldShortTarget(GroundNodeDto Node, string Name, IReadOnlyList<string> Reasons)
{
    /// <summary>The reasons as a title shows them, <c>nearest, full length</c>; null when there are none.</summary>
    public string? ReasonText => (Reasons.Count > 0) ? string.Join(", ", Reasons) : null;

    /// <summary>The name and, when there are any, its reasons in parentheses: <c>At W (nearest, full length)</c>.</summary>
    public string Label => (ReasonText is { } reasons) ? $"{Name} ({reasons})" : Name;
}
