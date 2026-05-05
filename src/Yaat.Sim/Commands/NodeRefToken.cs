namespace Yaat.Sim.Commands;

/// <summary>
/// Helpers for parsing user-typed node-reference tokens of the form
/// <c>#&lt;id&gt;</c>. Used by the command parser to recognise an explicit
/// graph-node reference inside a TAXI clearance and by the pathfinder when
/// validating that path. The token format is part of the command grammar,
/// not the routing algorithm — keeping these helpers next to the parser
/// makes that ownership explicit.
/// </summary>
public static class NodeRefToken
{
    /// <summary>
    /// Returns true if <paramref name="token"/> is a node reference (e.g., "#42").
    /// </summary>
    public static bool IsNodeReference(string token) => token.Length > 1 && token[0] == '#' && int.TryParse(token.AsSpan(1), out _);

    /// <summary>
    /// Parses the numeric node ID from a node reference token (e.g., "#42" → 42).
    /// Caller must verify <see cref="IsNodeReference"/> first.
    /// </summary>
    public static int ParseNodeId(string token) => int.Parse(token.AsSpan(1));
}
