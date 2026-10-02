using Yaat.Client.Services;

namespace Yaat.Client.Views.Map;

/// <summary>
/// One thing a map right-click landed on: an aircraft (<see cref="Callsign"/> set) or a ground stand node
/// (<see cref="NodeId"/> set). When a click lands on two or more, the canvas offers them in a <see cref="RightClickPicker"/>.
/// </summary>
/// <param name="Label">The picker entry's text.</param>
/// <param name="Callsign">The aircraft's callsign, or null for a node.</param>
/// <param name="NodeId">The ground node's id, or null for an aircraft.</param>
/// <param name="ViaDataBlock">True when the aircraft was hit through its datablock, which choosing it brings to the front.</param>
public sealed record RightClickTarget(string Label, string? Callsign, int? NodeId, bool ViaDataBlock)
{
    /// <summary>An aircraft target labelled <c>CALLSIGN (TYPE)</c>, or just <c>CALLSIGN</c> when no type is known.</summary>
    public static RightClickTarget ForAircraft(string callsign, string? aircraftType, bool viaDataBlock) =>
        new(string.IsNullOrWhiteSpace(aircraftType) ? callsign : $"{callsign} ({aircraftType})", callsign, null, viaDataBlock);

    /// <summary>
    /// A ground node target labelled <c>&lt;Type&gt; &lt;Name&gt;</c> (<c>Parking A5</c>, <c>Spot 3</c>, <c>Helipad H1</c>);
    /// an unnamed node falls back to its <c>#&lt;id&gt;</c> reference token.
    /// </summary>
    public static RightClickTarget ForNode(GroundNodeDto node) =>
        new(string.IsNullOrWhiteSpace(node.Name) ? $"{node.Type} #{node.Id}" : $"{node.Type} {node.Name}", null, node.Id, false);
}
